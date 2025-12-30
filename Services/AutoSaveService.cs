using System;
using System.Linq;
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
        private Func<string>? _getContent;

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
        public void Start(string projectPath, Func<string> getContent)
        {
            Stop();

            _currentProjectPath = projectPath;
            _getContent = getContent;

            _autoSaveSubscription = Observable
                .Interval(_interval)
                .Subscribe(async _ => await PerformAutoSave());
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
            if (string.IsNullOrEmpty(_currentProjectPath) || _getContent == null)
                return;

            try
            {
                var project = _projectService.GetProjectByPath(_currentProjectPath);
                if (project == null)
                    return;

                // Обновляем контент ПЕРЕД сохранением
                var currentContent = _getContent();
                project.ModulesData["TextEditor"] = currentContent;
                project.LastModified = DateTime.Now;

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