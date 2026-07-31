using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Reactive.Linq;
using Writersword.Core.Interfaces.Services;
using Writersword.Core.Interfaces.Services.Storage;
using Writersword.Core.Interfaces.WorkFlows;

namespace Writersword.Infrastructure.Services.Project
{
    /// <summary>
    /// Сервис автоматического сохранения активной вкладки
    /// Периодически вызывает ProjectWorkflow.SaveDocumentAsync() для активной вкладки
    /// </summary>
    public class AutoSaveService : IAutoSaveService
    {
        private readonly ILogger<AutoSaveService> _logger;
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
            _logger = App.Services.GetService<ILogger<AutoSaveService>>()!;
            _projectWorkflow = projectWorkflow;
            _tabCollection = tabCollection;
        }

        /// <summary>Включить автосохранение</summary>
        public void Enable()
        {
            // Если интервал = 0, не запускаем
            if (_interval == TimeSpan.Zero)
            {
                _logger.LogDebug("Interval is 0, not starting");
                return;
            }

            // Останавливаем старый таймер
            _timer?.Dispose();

            // Запускаем новый
            _timer = Observable.Interval(_interval)
                .Subscribe(async _ => await SaveActiveTabAsync());

            _logger.LogDebug("Enabled with interval: {Minutes} minutes", _interval.TotalMinutes);
        }

        /// <summary>Выключить автосохранение</summary>
        public void Disable()
        {
            _timer?.Dispose();
            _timer = null;
            _logger.LogDebug("Disabled");
        }

        /// <summary>
        /// Установить интервал автосохранения
        /// Если interval = TimeSpan.Zero, автосохранение отключается
        /// </summary>
        public void SetInterval(TimeSpan interval)
        {
            _interval = interval;
            _logger.LogDebug("Interval set to: {Minutes} minutes", interval.TotalMinutes);

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
                    _logger.LogDebug("No active tab, skipping");
                    return;
                }

                // Не сохраняем если в Compare mode
                if (activeTab.Context.IsInCompareMode)
                {
                    _logger.LogDebug("Active tab in Compare mode, skipping");
                    return;
                }

                _logger.LogDebug("Auto-saving tab: {TabTitle}", activeTab.Title);

                bool success = await _projectWorkflow.SaveDocumentAsync(activeTab, isAutoSave: true);

                if (success)
                {
                    _logger.LogDebug("Successfully saved: {TabTitle}", activeTab.Title);
                    ProjectSaved?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    _logger.LogWarning("Failed to save: {TabTitle}", activeTab.Title);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Auto-save failed");
            }
        }
    }
}