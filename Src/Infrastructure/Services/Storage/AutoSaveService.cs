using System;
using System.Reactive.Linq;
using Writersword.Core.Interfaces.Services;
using Writersword.Src.Core.Interfaces.Services.Storage;
using Writersword.Src.Core.Interfaces.WorkFlows;

namespace Writersword.Src.Infrastructure.Services.Project
{
    /// <summary>
    /// Сервис автоматического сохранения активной вкладки
    /// Периодически вызывает ProjectWorkflow.SaveDocumentAsync() для активной вкладки
    /// </summary>
    public class AutoSaveService : IAutoSaveService
    {
        private readonly IProjectWorkflow _projectWorkflow;
        private readonly ITabCollection _tabCollection;
        private IDisposable? _timer;
        private TimeSpan _interval = TimeSpan.FromMinutes(2);
        private bool _isEnabled = true;

        public event EventHandler? ProjectSaved;

        /// <summary>Включено ли автосохранение</summary>
        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                _isEnabled = value;
                if (_isEnabled)
                    Enable();
                else
                    Disable();
            }
        }

        /// <summary>Текущий интервал автосохранения</summary>
        public TimeSpan Interval => _interval;

        public AutoSaveService(
            IProjectWorkflow projectWorkflow,
            ITabCollection tabCollection)
        {
            _projectWorkflow = projectWorkflow;
            _tabCollection = tabCollection;
        }

        /// <summary>Включить автосохранение</summary>
        public void Enable()
        {
            // Если интервал = 0, не запускаем
            if (_interval == TimeSpan.Zero)
            {
                Console.WriteLine("[AutoSaveService] Interval is 0, not starting");
                return;
            }

            // Останавливаем старый таймер
            _timer?.Dispose();

            // Запускаем новый
            _timer = Observable.Interval(_interval)
                .Subscribe(async _ => await SaveActiveTabAsync());

            Console.WriteLine($"[AutoSaveService] Enabled with interval: {_interval.TotalMinutes} minutes");
        }

        /// <summary>Выключить автосохранение</summary>
        public void Disable()
        {
            _timer?.Dispose();
            _timer = null;
            Console.WriteLine("[AutoSaveService] Disabled");
        }

        /// <summary>
        /// Установить интервал автосохранения
        /// Если interval = TimeSpan.Zero, автосохранение отключается
        /// </summary>
        public void SetInterval(TimeSpan interval)
        {
            _interval = interval;
            Console.WriteLine($"[AutoSaveService] Interval set to: {interval.TotalMinutes} minutes");

            // Если интервал = 0, отключаем
            if (interval == TimeSpan.Zero)
            {
                Disable();
            }
            // Если включено, перезапускаем с новым интервалом
            else if (_isEnabled)
            {
                Enable();
            }
        }

        /// <summary>
        /// Сохранить активную вкладку
        /// Вызывается автоматически по таймеру
        /// </summary>
        private async System.Threading.Tasks.Task SaveActiveTabAsync()
        {
            try
            {
                var activeTab = _tabCollection.ActiveTab;
                if (activeTab == null)
                {
                    Console.WriteLine("[AutoSaveService] No active tab, skipping");
                    return;
                }

                // Не сохраняем если в Compare mode
                if (activeTab.Context.IsInCompareMode)
                {
                    Console.WriteLine("[AutoSaveService] Active tab in Compare mode, skipping");
                    return;
                }

                Console.WriteLine($"[AutoSaveService] Auto-saving tab: {activeTab.Title}");

                bool success = await _projectWorkflow.SaveDocumentAsync(activeTab);

                if (success)
                {
                    Console.WriteLine($"[AutoSaveService] Successfully saved: {activeTab.Title}");
                    ProjectSaved?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    Console.WriteLine($"[AutoSaveService] Failed to save: {activeTab.Title}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AutoSaveService] ERROR: {ex.Message}");
            }
        }
    }
}