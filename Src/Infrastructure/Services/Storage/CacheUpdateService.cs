using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
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

        /// <summary>Событие завершения кеширования</summary>
        public event EventHandler? CacheSaved;

        public CacheUpdateService(
            ICacheService cacheService,
            IModuleStateCollectorService stateCollector)
        {
            _cacheService = cacheService;
            _stateCollector = stateCollector;
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
        /// </summary>
        private async Task PerformCacheUpdateAsync()
        {
            // Проверяем ДО получения модулей
            if (string.IsNullOrEmpty(_currentProjectPath) || _getActiveModules == null)
            {
                Console.WriteLine("[CacheUpdateService] Skipped: service stopped or not initialized");
                return;
            }

            try
            {
                // Сохраняем локальную копию callback (защита от race condition)
                var getModulesCallback = _getActiveModules;
                if (getModulesCallback == null)
                {
                    Console.WriteLine("[CacheUpdateService] Skipped: callback is null");
                    return;
                }

                // Получаем модули в UI потоке
                var activeModules = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    return getModulesCallback();
                });

                // Проверяем что сервис не остановили пока ждали
                if (_getActiveModules == null)
                {
                    Console.WriteLine("[CacheUpdateService] Skipped: service stopped during execution");
                    return;
                }

                // Собираем ПОЛНЫЕ состояния (CustomData + SessionData)
                var moduleStates = _stateCollector.CollectAllStates(activeModules);

                if (moduleStates.Count == 0)
                {
                    Console.WriteLine("[CacheUpdateService] No modules to cache");
                    return;
                }

                // Сохраняем в кеш
                await _cacheService.SaveCacheAsync(_currentProjectPath, moduleStates);

                CacheSaved?.Invoke(this, EventArgs.Empty);

                Console.WriteLine($"[CacheUpdateService] Cache updated: {moduleStates.Count} modules");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CacheUpdateService] ERROR: {ex.Message}");
                Console.WriteLine($"[CacheUpdateService] Stack trace: {ex.StackTrace}");
            }
        }
    }
}