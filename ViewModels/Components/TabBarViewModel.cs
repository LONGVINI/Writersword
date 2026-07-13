using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Writersword.Core.Interfaces.Services.Storage;
using Writersword.Core.Interfaces.WorkFlows;
using Writersword.ViewModels;

using Writersword.Infrastructure.Services.Tabs;

namespace Writersword.ViewModels.Components
{
    /// <summary>
    /// ViewModel для панели вкладок
    /// Управляет открытыми документами и их переключением
    /// </summary>
    public class TabBarViewModel : ViewModelBase
    {
        private readonly ILogger<TabBarViewModel> _logger;
        private readonly ITabCollection _tabCollection;
        private readonly IProjectWorkflow _projectWorkflow;

        private TabCollection ConcreteCollection => (_tabCollection as TabCollection)!;

        /// <summary>Список открытых вкладок (из TabCollection)</summary>
        public ObservableCollection<DocumentTabViewModel> Tabs => ConcreteCollection.Tabs;

        /// <summary>Активная вкладка</summary>
        public DocumentTabViewModel? ActiveTab
        {
            get => ConcreteCollection.ActiveTab;
            set => ConcreteCollection.ActiveTab = value;
        }

        /// <summary>Есть ли RecoveryBanner у активной вкладки</summary>
        public bool HasRecoveryBanner => ActiveTab?.HasRecoveryBanner ?? false;

        /// <summary>Команда создания новой вкладки (показывает Welcome screen)</summary>
        public ReactiveCommand<Unit, Unit> CreateNewTabCommand { get; }

        /// <summary>Команда активации вкладки</summary>
        public ReactiveCommand<DocumentTabViewModel, Unit> ActivateTabCommand { get; }

        /// <summary>Команда закрытия вкладки</summary>
        public ReactiveCommand<DocumentTabViewModel, Unit> CloseTabCommand { get; }

        public TabBarViewModel(
            ITabCollection tabCollection,
            IProjectWorkflow projectWorkflow)
        {
            _logger = App.Services.GetService<ILogger<TabBarViewModel>>()!;
            _tabCollection = tabCollection;
            _projectWorkflow = projectWorkflow;

            CreateNewTabCommand = ReactiveCommand.Create(CreateNewTab);

            ActivateTabCommand = ReactiveCommand.Create<DocumentTabViewModel>(ActivateTab);

            CloseTabCommand = ReactiveCommand.CreateFromTask<DocumentTabViewModel>(CloseTabAsync);

            ConcreteCollection.ActiveTabChanged += OnActiveTabChanged;

            ConcreteCollection.ActiveTabChanged += (_, __) =>
            {
                this.RaisePropertyChanged(nameof(HasRecoveryBanner));
            };

            _logger.LogDebug("Initialized");
        }

        /// <summary>Создать новую вкладку (показывает Welcome screen)</summary>
        private async void CreateNewTab()
        {
            _logger.LogDebug("CreateNewTab clicked");

            if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow != null)
            {
                await App.ShowWelcomeScreen(desktop.MainWindow);
            }
        }

        /// <summary>
        /// Активировать вкладку
        /// Сохраняет workspace.json старой вкладки НЕМЕДЛЕННО перед переключением
        /// Сохраняет кеш старой вкладки
        /// Public: вызывается также из TabDragDropBehavior при отпускании
        /// перетащенной вкладки — тот же путь активации, что и у обычного клика.
        /// </summary>
        public async void ActivateTab(DocumentTabViewModel tab)
        {
            try
            {
                _logger.LogDebug("Activating tab: {TabTitle}", tab.Title);

                var oldTab = ActiveTab;

                // Список активных модулей старой вкладки снимается ДО переключения:
                // после смены ActiveTab GetActiveModules вернёт модули новой вкладки.
                System.Collections.Generic.List<Writersword.Core.Interfaces.Modules.IModule>? oldTabModules = null;
                if (oldTab != null && oldTab != tab)
                {
                    var mainViewModel = App.Services.GetRequiredService<MainWindowViewModel>();
                    oldTabModules = mainViewModel.GetActiveModules();
                }

                // Переключение выполняется СРАЗУ — сохранения старой вкладки его
                // не задерживают. Раньше await-ы workspace.json и кеша стояли ДО
                // смены ActiveTab, и переключение (в том числе на старте
                // перетаскивания вкладки) ощутимо запаздывало.
                ActiveTab = tab;

                // Сохранения старой вкладки догоняют после переключения: её модули
                // припаркованы живыми, сбор данных с них потокобезопасен (тот же
                // путь, что у периодического автосейва).
                if (oldTab != null && oldTab != tab)
                {
                    _logger.LogDebug("Saving previous tab in background: {OldTabTitle}", oldTab.Title);

                    if (!string.IsNullOrEmpty(oldTab.FilePath))
                    {
                        var workflow = App.Services.GetRequiredService<IProjectWorkflow>();
                        var autoSave = workflow.GetAutoSaveServiceForProject(oldTab.FilePath);

                        if (autoSave != null)
                        {
                            await autoSave.SaveNowAsync();
                            _logger.LogDebug("workspace.json saved for: {OldTabTitle}", oldTab.Title);
                        }
                    }

                    var capturedModules = oldTabModules
                        ?? new System.Collections.Generic.List<Writersword.Core.Interfaces.Modules.IModule>();
                    await oldTab.SaveToCacheAsync(() => capturedModules);

                    _logger.LogDebug("Old tab saved to cache");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error activating tab: {TabTitle}", tab.Title);
            }
        }

        /// <summary>Закрыть вкладку</summary>
        private async Task CloseTabAsync(DocumentTabViewModel tab)
        {
            _logger.LogDebug("Closing tab: {TabTitle}", tab.Title);

            if (!string.IsNullOrEmpty(tab.FilePath))
            {
                var workflow = App.Services.GetRequiredService<IProjectWorkflow>();
                var autoSave = workflow.GetAutoSaveServiceForProject(tab.FilePath);

                if (autoSave != null)
                {
                    _logger.LogDebug("Saving workspace BEFORE closing tab: {TabTitle}", tab.Title);
                    await autoSave.SaveNowAsync();
                    _logger.LogDebug("Workspace saved successfully before close");
                }
            }

            bool closed = await _projectWorkflow.CloseDocumentAsync(tab);

            if (!closed)
            {
                _logger.LogDebug("Close cancelled by user");
                return;
            }

            tab.RecoveryBanner = null;
            _logger.LogDebug("RecoveryBanner cleared before removal");

            _tabCollection.Remove(tab);
            _logger.LogDebug("Tab removed from collection");

            if (Tabs.Count == 0)
            {
                _logger.LogDebug("No tabs left - clearing UI and showing Welcome");

                var mainViewModel = App.Services.GetRequiredService<MainWindowViewModel>();
                mainViewModel.ClearUIWhenNoTabs();

                if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                    && desktop.MainWindow != null)
                {
                    await App.ShowWelcomeScreen(desktop.MainWindow);
                }
            }
        }

        /// <summary>
        /// Обработчик изменения активной вкладки
        /// Вызывается когда TabCollection.ActiveTab изменилось
        /// </summary>
        private void OnActiveTabChanged(DocumentTabViewModel? newTab, DocumentTabViewModel? previousTab)
        {
            this.RaisePropertyChanged(nameof(ActiveTab));

            if (newTab != null)
            {
                _logger.LogDebug("Active tab changed: {TabTitle} (previous: {PreviousTitle})",
                    newTab.Title, previousTab?.Title ?? "none");
            }
        }

        /// <summary>
        /// Поменять местами две вкладки
        /// </summary>
        public void SwapTabs(int oldIndex, int newIndex)
        {
            if (oldIndex < 0 || oldIndex >= Tabs.Count ||
                newIndex < 0 || newIndex >= Tabs.Count)
            {
                return;
            }

            _logger.LogDebug("SwapTabs: {OldIndex} <-> {NewIndex}", oldIndex, newIndex);

            var tab = Tabs[oldIndex];
            var wasActive = (tab == ActiveTab);

            Tabs.RemoveAt(oldIndex);
            Tabs.Insert(newIndex, tab);
        }

        /// <summary>
        /// Сохранить порядок вкладок в settings.json.
        /// Вызывается из TabDragDropBehavior после завершения перетаскивания.
        /// </summary>
        public void SaveTabsOrder()
        {
            _logger.LogDebug("Saving tabs order");

            var settingsService = App.Services.GetRequiredService<ISettingsService>();

            var paths = Tabs
                .Select(t => t.FilePath)
                .Where(p => !string.IsNullOrEmpty(p))
                .ToList();

            settingsService.SaveOpenProjects(paths!);

            _logger.LogDebug("Saved {Count} tabs in new order", paths.Count);
        }
    }
}