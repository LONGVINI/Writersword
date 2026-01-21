using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Interfaces.Services;
using Writersword.Core.Models.Modules;
using Writersword.Core.Models.Project;
using Writersword.Src.Core.Interfaces.Services.Storage;

namespace Writersword.Src.Infrastructure.Services.Storage
{
    /// <summary>
    /// Сервис фонового кеширования состояния модулей
    /// Периодически сохраняет SessionData модулей в .wsasd файл (ZIP архив)
    /// Используется для recovery и переключения вкладок/WorkMode
    /// Использует debounce для оптимизации (сохраняет только после паузы)
    /// </summary>
    public class CacheUpdateService : ICacheUpdateService
    {
        private readonly IZipCacheService _cacheService;
        private readonly IModuleStateCollectorService _stateCollector;
        private readonly IDataComparisonService _comparisonService;
        private IDisposable? _cacheUpdateSubscription;
        private DebounceTimer? _debounceTimer;
        private TimeSpan _interval = TimeSpan.FromSeconds(10);
        private string? _currentProjectPath;
        private Func<IEnumerable<IModule>>? _getActiveModules;

        /// <summary>Событие завершения кеширования</summary>
        public event EventHandler? CacheSaved;

        public CacheUpdateService(
            IZipCacheService cacheService,
            IModuleStateCollectorService stateCollector,
            IDataComparisonService comparisonService)
        {
            _cacheService = cacheService;
            _stateCollector = stateCollector;
            _comparisonService = comparisonService;
        }

        /// <summary>
        /// Запустить фоновое кеширование для проекта
        /// </summary>
        /// <param name="projectPath">Путь к проекту</param>
        /// <param name="getActiveModules">Функция получения активных модулей</param>
        public void Start(string projectPath, Func<IEnumerable<IModule>> getActiveModules)
        {
            Stop();

            _currentProjectPath = projectPath;
            _getActiveModules = getActiveModules;

            _cacheUpdateSubscription = Observable
                .Interval(_interval)
                .Subscribe(async _ => await PerformCacheUpdateAsync());

            Console.WriteLine($"[CacheUpdateService] Started for: {projectPath}");
        }

        /// <summary>Остановить фоновое кеширование</summary>
        public void Stop()
        {
            // Останавливаем таймеры
            _cacheUpdateSubscription?.Dispose();
            _cacheUpdateSubscription = null;

            _debounceTimer?.Dispose();
            _debounceTimer = null;

            // Обнуляем переменные
            _currentProjectPath = null;
            _getActiveModules = null;

            Console.WriteLine("[CacheUpdateService] Stopped");
        }

        /// <summary>Принудительно сохранить в кеш СЕЙЧАС</summary>
        public void SaveToCache()
        {
            _ = PerformCacheUpdateAsync();
        }

        /// <summary>Установить интервал кеширования</summary>
        public void SetInterval(TimeSpan interval)
        {
            _interval = interval;
            Console.WriteLine($"[CacheUpdateService] Interval set to: {interval.TotalSeconds}s");
        }

        /// <summary>
        /// Выполнить обновление кеша
        /// Сохраняет ТОЛЬКО если данные отличаются от сохранённого ZIP файла
        /// Использует хеширование для быстрой проверки изменений
        /// Читает ZIP БЕЗ блокировки для оптимизации
        /// </summary>
        private async Task PerformCacheUpdateAsync()
        {
            // Сохраняем путь и callback в локальные переменные для защиты от изменения во время выполнения
            var projectPath = _currentProjectPath;
            var getModulesCallback = _getActiveModules;

            if (string.IsNullOrEmpty(projectPath) || getModulesCallback == null)
            {
                Console.WriteLine("[CacheUpdateService] Skipped: service stopped");
                return;
            }

            try
            {
                // Получаем активные модули из UI потока
                var activeModules = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    return getModulesCallback();
                });

                // Проверяем что сервис не остановился во время выполнения
                if (_getActiveModules == null)
                {
                    Console.WriteLine("[CacheUpdateService] Skipped: service stopped during execution");
                    return;
                }

                // Собираем состояния всех модулей
                var moduleStates = _stateCollector.CollectAllStates(activeModules);

                if (moduleStates.Count == 0)
                {
                    Console.WriteLine("[CacheUpdateService] No modules to cache");
                    return;
                }

                // Проверяем есть ли реальные данные в модулях
                bool hasAnyRealData = false;

                foreach (var kvp in moduleStates)
                {
                    var customData = kvp.Value.CustomData;

                    if (customData == null)
                        continue;

                    if (customData is string str)
                    {
                        if (!string.IsNullOrWhiteSpace(str))
                        {
                            hasAnyRealData = true;
                            break;
                        }
                    }
                    else
                    {
                        hasAnyRealData = true;
                        break;
                    }
                }

                if (!hasAnyRealData)
                {
                    Console.WriteLine("[CacheUpdateService] No real data, skipping");

                    // Удаляем устаревший кеш если он есть
                    if (_cacheService.HasCache(projectPath))
                    {
                        _cacheService.DeleteCache(projectPath);
                        Console.WriteLine("[CacheUpdateService] Deleted outdated cache");
                    }

                    return;
                }

                // Читаем данные из ZIP БЕЗ блокировки файла
                var savedProjectData = _cacheService.ReadProjectDataWithoutLock(projectPath);

                if (savedProjectData != null)
                {
                    bool dataChanged = false;

                    // Быстрая проверка: разное количество модулей = изменения есть
                    if (moduleStates.Count != savedProjectData.Count)
                    {
                        dataChanged = true;
                    }
                    else
                    {
                        // Сравниваем данные каждого модуля
                        foreach (var kvp in moduleStates)
                        {
                            if (!savedProjectData.TryGetValue(kvp.Key, out var savedData))
                            {
                                dataChanged = true;
                                break;
                            }

                            var currentCustomData = kvp.Value.CustomData;

                            // Оптимизированное сравнение для строк
                            if (currentCustomData is string currentStr && savedData is string savedStr)
                            {
                                if (currentStr != savedStr)
                                {
                                    dataChanged = true;
                                    break;
                                }
                            }
                            else if (!Equals(currentCustomData, savedData))
                            {
                                dataChanged = true;
                                break;
                            }
                        }
                    }

                    if (!dataChanged)
                    {
                        Console.WriteLine("[CacheUpdateService] No changes from ZIP, skipping");
                        return;
                    }
                }

                // Получаем ProjectId из проекта для ZIP кеша
                // Читаем project.json БЕЗ блокировки с FileShare.ReadWrite
                ProjectFile? project = null;
                try
                {
                    using (var stream = new System.IO.FileStream(projectPath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
                    using (var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read))
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
                    Console.WriteLine($"[CacheUpdateService] Error reading ProjectId: {ex.Message}");
                }

                if (project == null)
                {
                    Console.WriteLine("[CacheUpdateService] ERROR: Cannot get ProjectId");
                    return;
                }

                // Сохраняем кеш только если данные отличаются от ZIP
                await _cacheService.SaveCacheAsync(projectPath, project.Id, moduleStates);
                CacheSaved?.Invoke(this, EventArgs.Empty);
                Console.WriteLine($"[CacheUpdateService] Cache updated: {moduleStates.Count} modules");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CacheUpdateService] ERROR: {ex.Message}");
            }
        }
    }
}