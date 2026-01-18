using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Runtime.Intrinsics.Arm;
using System.Threading.Tasks;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Interfaces.Services;
using Writersword.Core.Models.Modules;
using Writersword.Src.Core.Interfaces.Services.Storage;

namespace Writersword.Src.Infrastructure.Services.Modules
{
    /// <summary>
    /// Сервис фонового кеширования состояния модулей
    /// Периодически сохраняет SessionData модулей в .wsasd файл
    /// Используется для recovery и переключения вкладок/WorkMode
    /// </summary>
    public class CacheUpdateService : ICacheUpdateService
    {
        private readonly ICacheService _cacheService;
        private readonly IModuleStateCollectorService _stateCollector;
        private IDisposable? _cacheUpdateSubscription;
        private TimeSpan _interval = TimeSpan.FromSeconds(10);
        private string? _currentProjectPath;
        private Func<IEnumerable<IModule>>? _getActiveModules;
        private readonly IDataComparisonService _comparisonService;

        /// <summary>Событие завершения кеширования</summary>
        public event EventHandler? CacheSaved;

        public CacheUpdateService(
            ICacheService cacheService,
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
            // Сначала останавливаем таймер
            _cacheUpdateSubscription?.Dispose();
            _cacheUpdateSubscription = null;

            // Только ПОТОМ обнуляем переменные
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
        /// Собирает состояния всех активных модулей и сохраняет в .wsasd
        /// НЕ СОХРАНЯЕТ если:
        /// - Данные не изменились
        /// - Все модули пустые (нет CustomData)
        /// </summary>
        /// <summary>
        /// Выполнить обновление кеша
        /// Сохраняет ТОЛЬКО если данные отличаются от сохранённого ZIP файла
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
                var activeModules = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    return getModulesCallback();
                });

                if (_getActiveModules == null)
                {
                    Console.WriteLine("[CacheUpdateService] Skipped: service stopped during execution");
                    return;
                }

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

                    if (_cacheService.HasCache(projectPath))
                    {
                        _cacheService.DeleteCache(projectPath);
                        Console.WriteLine("[CacheUpdateService] Deleted outdated cache");
                    }

                    return;
                }

                // Получаем сервисы
                var projectService = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                    .GetRequiredService<IProjectService>(App.Services);

                var tab = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var tabCollection = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                        .GetRequiredService<Writersword.Src.Core.Interfaces.WorkFlows.ITabCollection>(App.Services);
                    return tabCollection.FindByPath(projectPath);
                });

                // Временно закрываем ZIP для чтения файла
                if (tab != null)
                {
                    tab.Context.CloseZipStorage();
                }

                var savedProject = await projectService.LoadAsync(projectPath);

                // Открываем ZIP обратно
                if (tab != null)
                {
                    tab.Context.ReopenZipStorage();
                }

                if (savedProject != null)
                {
                    bool dataChanged = false;

                    // Быстрая проверка: разное количество модулей = изменения есть
                    if (moduleStates.Count != savedProject.ModulesData.Count)
                    {
                        dataChanged = true;
                    }
                    else
                    {
                        // Сравниваем данные каждого модуля
                        foreach (var kvp in moduleStates)
                        {
                            if (!savedProject.ModulesData.TryGetValue(kvp.Key, out var savedData))
                            {
                                dataChanged = true;
                                break;
                            }

                            var currentCustomData = kvp.Value.CustomData;

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

                // Сохраняем кеш только если данные отличаются от ZIP
                await _cacheService.SaveCacheAsync(projectPath, moduleStates);
                CacheSaved?.Invoke(this, EventArgs.Empty);
                Console.WriteLine($"[CacheUpdateService] Cache updated: {moduleStates.Count} modules");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CacheUpdateService] ERROR: {ex.Message}");

                // В случае ошибки переоткрываем ZIP
                try
                {
                    var tab = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        var tabCollection = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                            .GetRequiredService<Writersword.Src.Core.Interfaces.WorkFlows.ITabCollection>(App.Services);
                        return tabCollection.FindByPath(projectPath);
                    });

                    if (tab != null)
                    {
                        tab.Context.ReopenZipStorage();
                    }
                }
                catch { }
            }
        }
    }
}