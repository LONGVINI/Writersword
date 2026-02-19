using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Interfaces.Services;
using Writersword.Core.Models.Project;
using Writersword.Src.Core.Interfaces.Services.Storage;

namespace Writersword.Src.Infrastructure.Services.Storage
{
    /// <summary>
    /// Сервис фонового кеширования состояния модулей
    /// Периодически сохраняет данные модулей в .wsasd файл (ZIP архив)
    /// Сохраняет только если данные отличаются от сохранённого ZIP файла
    /// Ключ данных модуля — moduleType, не InstanceId
    /// </summary>
    public class CacheUpdateService : ICacheUpdateService
    {
        private readonly ILogger<CacheUpdateService> _logger;
        private readonly IZipCacheService _cacheService;
        private readonly IModuleStateCollectorService _stateCollector;
        private readonly IDataComparisonService _comparisonService;
        private IDisposable? _cacheUpdateSubscription;
        private DebounceTimer? _debounceTimer;
        private TimeSpan _interval = TimeSpan.FromSeconds(10);
        private string? _currentProjectPath;
        private Func<IEnumerable<IModule>>? _getActiveModules;

        public event EventHandler? CacheSaved;

        public CacheUpdateService(
            IZipCacheService cacheService,
            IModuleStateCollectorService stateCollector,
            IDataComparisonService comparisonService)
        {
            _logger = App.Services.GetService<ILogger<CacheUpdateService>>()!;
            _cacheService = cacheService;
            _stateCollector = stateCollector;
            _comparisonService = comparisonService;
        }

        /// <summary>
        /// Запустить фоновое кеширование для проекта
        /// </summary>
        public void Start(string projectPath, Func<IEnumerable<IModule>> getActiveModules)
        {
            Stop();

            _currentProjectPath = projectPath;
            _getActiveModules = getActiveModules;

            _cacheUpdateSubscription = Observable
                .Interval(_interval)
                .Subscribe(async _ => await PerformCacheUpdateAsync());

            _logger.LogDebug("Started for: {ProjectPath}", projectPath);
        }

        /// <summary>
        /// Остановить фоновое кеширование
        /// </summary>
        public void Stop()
        {
            _cacheUpdateSubscription?.Dispose();
            _cacheUpdateSubscription = null;

            _debounceTimer?.Dispose();
            _debounceTimer = null;

            _currentProjectPath = null;
            _getActiveModules = null;

            _logger.LogDebug("Stopped");
        }

        /// <summary>
        /// Принудительно сохранить в кеш немедленно
        /// </summary>
        public void SaveToCache()
        {
            _ = PerformCacheUpdateAsync();
        }

        /// <summary>
        /// Установить интервал кеширования
        /// </summary>
        public void SetInterval(TimeSpan interval)
        {
            _interval = interval;
            _logger.LogDebug("Interval set to: {Seconds}s", interval.TotalSeconds);
        }

        /// <summary>
        /// Выполнить обновление кеша
        /// Сохраняет только если данные отличаются от сохранённого ZIP файла
        /// </summary>
        private async Task PerformCacheUpdateAsync()
        {
            var projectPath = _currentProjectPath;
            var getModulesCallback = _getActiveModules;

            if (string.IsNullOrEmpty(projectPath) || getModulesCallback == null)
            {
                _logger.LogDebug("Skipped: service stopped");
                return;
            }

            try
            {
                var activeModules = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    getModulesCallback());

                if (_getActiveModules == null)
                {
                    _logger.LogDebug("Skipped: service stopped during execution");
                    return;
                }

                var (customData, sessionData) = _stateCollector.CollectAllData(activeModules);

                if (customData.Count == 0)
                {
                    _logger.LogDebug("No modules to cache");
                    return;
                }

                bool hasAnyRealData = false;
                foreach (var kvp in customData)
                {
                    if (kvp.Value == null) continue;
                    if (kvp.Value is string str)
                    {
                        if (!string.IsNullOrWhiteSpace(str)) { hasAnyRealData = true; break; }
                    }
                    else
                    {
                        hasAnyRealData = true;
                        break;
                    }
                }

                if (!hasAnyRealData)
                {
                    _logger.LogDebug("No real data, skipping");
                    if (_cacheService.HasCache(projectPath))
                    {
                        _cacheService.DeleteCache(projectPath);
                        _logger.LogDebug("Deleted outdated cache");
                    }
                    return;
                }

                var savedProjectData = _cacheService.ReadProjectDataWithoutLock(projectPath);

                if (savedProjectData != null)
                {
                    bool dataChanged = customData.Count != savedProjectData.Count;

                    if (!dataChanged)
                    {
                        foreach (var kvp in customData)
                        {
                            if (!savedProjectData.TryGetValue(kvp.Key, out var savedData))
                            {
                                dataChanged = true;
                                break;
                            }

                            if (kvp.Value is string currentStr && savedData is string savedStr)
                            {
                                if (currentStr != savedStr) { dataChanged = true; break; }
                            }
                            else if (!Equals(kvp.Value, savedData))
                            {
                                dataChanged = true;
                                break;
                            }
                        }
                    }

                    if (!dataChanged)
                    {
                        _logger.LogDebug("No changes from ZIP, skipping");
                        return;
                    }
                }

                ProjectFile? project = null;
                try
                {
                    using (var stream = new System.IO.FileStream(
                        projectPath, System.IO.FileMode.Open,
                        System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
                    using (var archive = new System.IO.Compression.ZipArchive(
                        stream, System.IO.Compression.ZipArchiveMode.Read))
                    {
                        var entry = archive.GetEntry("project.json");
                        if (entry != null)
                        {
                            using (var entryStream = entry.Open())
                            using (var reader = new System.IO.StreamReader(entryStream))
                            {
                                var json = reader.ReadToEnd();
                                project = Newtonsoft.Json.JsonConvert.DeserializeObject<ProjectFile>(json);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error reading ProjectId");
                }

                if (project == null)
                {
                    _logger.LogError("Cannot get ProjectId, aborting cache update");
                    return;
                }

                await _cacheService.SaveCacheAsync(projectPath, project.Id, customData, sessionData);

                CacheSaved?.Invoke(this, EventArgs.Empty);
                _logger.LogDebug("Cache updated: {Count} modules", customData.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cache update failed");
            }
        }
    }
}