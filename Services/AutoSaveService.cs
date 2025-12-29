using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Writersword.Services.Interfaces;

namespace Writersword.Services
{
    /// <summary>
    /// Сервис автоматического сохранения проекта в кеш
    /// Периодически сохраняет проект в .wsasd файл
    /// </summary>
    public class AutoSaveService : IAutoSaveService
    {
        private readonly ICacheService _cacheService;
        private readonly IProjectService _projectService;
        private IDisposable? _autoSaveSubscription;
        private TimeSpan _interval = TimeSpan.FromSeconds(10);
        private string? _currentProjectPath;

        /// <summary>Событие завершения автосохранения</summary>
        public event EventHandler? AutoSaveCompleted;

        public AutoSaveService(ICacheService cacheService, IProjectService projectService)
        {
            _cacheService = cacheService;
            _projectService = projectService;
        }

        /// <summary>
        /// Запустить автосохранение для проекта
        /// </summary>
        public void Start(string projectPath)
        {
            Stop();

            _currentProjectPath = projectPath;

            _autoSaveSubscription = Observable
                .Interval(_interval)
                .Subscribe(async _ => await PerformAutoSave());

            Console.WriteLine($"[AutoSaveService] Started for: {projectPath}");
        }

        /// <summary>
        /// Остановить автосохранение
        /// </summary>
        public void Stop()
        {
            _autoSaveSubscription?.Dispose();
            _autoSaveSubscription = null;
            _currentProjectPath = null;

            Console.WriteLine("[AutoSaveService] Stopped");
        }

        /// <summary>
        /// Принудительно запустить сохранение
        /// </summary>
        public void TriggerSave()
        {
            _ = PerformAutoSave();
        }

        /// <summary>
        /// Установить интервал автосохранения
        /// </summary>
        public void SetInterval(TimeSpan interval)
        {
            _interval = interval;
            Console.WriteLine($"[AutoSaveService] Interval set to: {interval.TotalSeconds}s");
        }

        /// <summary>
        /// Выполнить автосохранение
        /// </summary>
        private async Task PerformAutoSave()
        {
            if (string.IsNullOrEmpty(_currentProjectPath))
                return;

            try
            {
                var project = _projectService.GetProjectByPath(_currentProjectPath);
                if (project == null)
                    return;

                await _cacheService.SaveToCacheAsync(project, _currentProjectPath);

                AutoSaveCompleted?.Invoke(this, EventArgs.Empty);
                Console.WriteLine("[AutoSaveService] Auto-save completed");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AutoSaveService] ERROR: {ex.Message}");
            }
        }
    }
}