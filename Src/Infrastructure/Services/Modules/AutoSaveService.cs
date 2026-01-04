using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Interfaces.Services;
using Writersword.Core.Models.Modules;


namespace Writersword.Src.Infrastructure.Services.Modules
{
    /// <summary>
    /// Сервис автоматического сохранения SessionData модулей в кеш
    /// Периодически сохраняет состояния активных модулей в .wsasd файл
    /// </summary>
    public class AutoSaveService : IAutoSaveService
    {
        private readonly ICacheService _cacheService;
        private readonly IModuleStateCollectorService _stateCollector;
        private IDisposable? _autoSaveSubscription;
        private TimeSpan _interval = TimeSpan.FromSeconds(10);
        private string? _currentProjectPath;
        private Func<IEnumerable<IModule>>? _getActiveModules;

        /// <summary>Событие завершения автосохранения</summary>
        public event EventHandler? AutoSaveCompleted;

        public AutoSaveService(
            ICacheService cacheService,
            IModuleStateCollectorService stateCollector)
        {
            _cacheService = cacheService;
            _stateCollector = stateCollector;
        }

        /// <summary>
        /// Запустить автосохранение для проекта
        /// </summary>
        /// <param name="projectPath">Путь к проекту</param>
        /// <param name="getActiveModules">Функция получения активных модулей</param>
        public void Start(string projectPath, Func<IEnumerable<IModule>> getActiveModules)
        {
            Stop();

            _currentProjectPath = projectPath;
            _getActiveModules = getActiveModules;

            _autoSaveSubscription = Observable
                .Interval(_interval)
                .Subscribe(async _ => await PerformAutoSaveAsync());

            Console.WriteLine($"[AutoSaveService] Started for: {projectPath}");
        }

        /// <summary>Остановить автосохранение</summary>
        public void Stop()
        {
            _autoSaveSubscription?.Dispose();
            _autoSaveSubscription = null;
            _currentProjectPath = null;
            _getActiveModules = null;

            Console.WriteLine("[AutoSaveService] Stopped");
        }

        /// <summary>Принудительно запустить сохранение</summary>
        public void TriggerSave()
        {
            _ = PerformAutoSaveAsync();
        }

        /// <summary>Установить интервал автосохранения</summary>
        public void SetInterval(TimeSpan interval)
        {
            _interval = interval;
            Console.WriteLine($"[AutoSaveService] Interval set to: {interval.TotalSeconds}s");
        }

        /// <summary>
        /// Выполнить автосохранение
        /// Собирает SessionData всех активных модулей и сохраняет в кеш
        /// </summary>
        private async Task PerformAutoSaveAsync()
        {
            if (string.IsNullOrEmpty(_currentProjectPath) || _getActiveModules == null)
                return;

            try
            {
                // 1. Получаем модули в UI потоке!
                var activeModules = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    return _getActiveModules();
                });

                // 2. Собираем ПОЛНЫЕ состояния (CustomData + SessionData)
                var moduleStates = _stateCollector.CollectAllStates(activeModules);

                if (moduleStates.Count == 0)
                {
                    Console.WriteLine("[AutoSaveService] No modules to save");
                    return;
                }

                // 3. Сохраняем в кеш
                await _cacheService.SaveCacheAsync(_currentProjectPath, moduleStates);

                AutoSaveCompleted?.Invoke(this, EventArgs.Empty);

                Console.WriteLine($"[AutoSaveService] Auto-save completed: {moduleStates.Count} modules");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AutoSaveService] ERROR: {ex.Message}");
            }
        }
    }
}