using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Writersword.Core.Models.Settings;
using Writersword.Core.Models.WorkModes;
using Writersword.Src.Core.Interfaces.Services;
using Writersword.Src.Core.Interfaces.Services.Storage;
using Writersword.Src.Core.Interfaces.Services.UI;
using Writersword.Src.Core.Interfaces.WorkFlows;
using Writersword.Src.Core.Interfaces.WorkModes;
using Writersword.Src.ProjectTypes.Common;
using Writersword.Src.WorkModes.Common;
using Writersword.Views;

namespace Writersword.ViewModels.Components
{
    /// <summary>
    /// ViewModel для главного меню приложения (File, Edit, View)
    /// Отвечает за команды работы с проектами
    /// </summary>
    public class MenuBarViewModel : ViewModelBase
    {
        private readonly ILogger<MenuBarViewModel> _logger;
        private readonly IProjectWorkflow _projectWorkflow;
        private readonly ISettingsService _settingsService;
        private readonly ITabCollection _tabCollection;
        private readonly IWorkModeConfigurationService _workModeConfigService;
        private readonly INotificationService _notificationService;
        private readonly IDialogService _dialogService;
        private readonly IWorkspaceConfigService _workspaceConfigService;
        private readonly ProjectTypeRegistry _projectTypeRegistry;

        private Func<MainWindowViewModel>? _mainViewModelProvider;

        /// <summary>Список недавних проектов</summary>
        public ObservableCollection<RecentProjectItem> RecentProjects { get; } = new();

        /// <summary>Список всех доступных WorkModes (для меню View)</summary>
        public ObservableCollection<MainWindowViewModel.WorkModeMenuItem> AllWorkModes
        {
            get
            {
                var mainVM = _mainViewModelProvider?.Invoke();
                return mainVM?.AllWorkModes ?? new ObservableCollection<MainWindowViewModel.WorkModeMenuItem>();
            }
        }

        /// <summary>Список всех доступных модулей (для меню View)</summary>
        public ObservableCollection<MainWindowViewModel.ModuleMenuItem> AllModules
        {
            get
            {
                var mainVM = _mainViewModelProvider?.Invoke();
                return mainVM?.AllModules ?? new ObservableCollection<MainWindowViewModel.ModuleMenuItem>();
            }
        }

        /// <summary>Команда переключения WorkMode</summary>
        public ReactiveCommand<string, Unit> ToggleWorkModeCommand
        {
            get
            {
                var mainVM = _mainViewModelProvider?.Invoke();
                return mainVM?.ToggleWorkModeCommand ?? ReactiveCommand.Create<string>(_ => { });
            }
        }

        /// <summary>Команда переключения модуля</summary>
        public ReactiveCommand<string, Unit> ToggleModuleCommand
        {
            get
            {
                var mainVM = _mainViewModelProvider?.Invoke();
                return mainVM?.ToggleModuleCommand ?? ReactiveCommand.Create<string>(_ => { });
            }
        }

        /// <summary>Команда создания нового проекта (Ctrl+N)</summary>
        public ReactiveCommand<Unit, Unit> NewProjectCommand { get; }

        /// <summary>Команда открытия проекта (Ctrl+O)</summary>
        public ReactiveCommand<Unit, Unit> OpenProjectCommand { get; }

        /// <summary>Команда открытия недавнего проекта</summary>
        public ReactiveCommand<string, Unit> OpenRecentProjectCommand { get; }

        /// <summary>Команда сохранения проекта (Ctrl+S)</summary>
        public ReactiveCommand<Unit, Unit> SaveProjectCommand { get; }

        /// <summary>Команда "Сохранить как..." (Ctrl+Shift+S)</summary>
        public ReactiveCommand<Unit, Unit> SaveAsProjectCommand { get; }

        /// <summary>Команда выхода из приложения</summary>
        public ReactiveCommand<Unit, Unit> ExitCommand { get; }

        /// <summary>Команда сохранения конфигурации глобально (для типа проекта)</summary>
        public ReactiveCommand<Unit, Unit> SaveWorkspaceGlobalCommand { get; }

        /// <summary>Команда сброса до глобальной конфигурации</summary>
        public ReactiveCommand<Unit, Unit> ResetWorkspaceToGlobalCommand { get; }

        /// <summary>Команда сброса до дефолтной конфигурации</summary>
        public ReactiveCommand<Unit, Unit> ResetWorkspaceToDefaultCommand { get; }

        /// <summary>Команда сохранения всех открытых проектов</summary>
        public ReactiveCommand<Unit, Unit> SaveAllProjectsCommand { get; }

        /// <summary>Команда закрытия активной вкладки</summary>
        public ReactiveCommand<Unit, Unit> CloseTabCommand { get; }

        /// <summary>Команда закрытия всех вкладок</summary>
        public ReactiveCommand<Unit, Unit> CloseAllTabsCommand { get; }

        /// <summary>Команда закрытия всех вкладок кроме активной</summary>
        public ReactiveCommand<Unit, Unit> CloseOtherTabsCommand { get; }


        private bool _hasActiveTab;

        /// <summary>Есть ли активная вкладка (для IsEnabled кнопок)</summary>
        public bool HasActiveTab
        {
            get => _hasActiveTab;
            private set => this.RaiseAndSetIfChanged(ref _hasActiveTab, value);
        }

        /// <summary>Обновить состояние HasActiveTab</summary>
        public void UpdateHasActiveTab()
        {
            HasActiveTab = _getActiveTab?.Invoke() != null;
        }

        /// <summary>Функция для получения активной вкладки (передаётся извне)</summary>
        private Func<DocumentTabViewModel?>? _getActiveTab;

        public MenuBarViewModel(
             IProjectWorkflow projectWorkflow,
             ISettingsService settingsService,
             ITabCollection tabCollection,
             IWorkModeConfigurationService workModeConfigService,
             INotificationService notificationService,
             IDialogService dialogService,
             IWorkspaceConfigService workspaceConfigService,
             ProjectTypeRegistry projectTypeRegistry)
        {
            _logger = App.Services.GetService<ILogger<MenuBarViewModel>>()!;
            _projectWorkflow = projectWorkflow;
            _settingsService = settingsService;
            _tabCollection = tabCollection;
            _workModeConfigService = workModeConfigService;
            _notificationService = notificationService;
            _dialogService = dialogService;
            _workspaceConfigService = workspaceConfigService;
            _projectTypeRegistry = projectTypeRegistry;

            NewProjectCommand = ReactiveCommand.Create(NewProject);
            OpenProjectCommand = ReactiveCommand.CreateFromTask(OpenProject);
            OpenRecentProjectCommand = ReactiveCommand.CreateFromTask<string>(OpenRecentProject);
            SaveProjectCommand = ReactiveCommand.CreateFromTask(SaveProject);
            SaveAsProjectCommand = ReactiveCommand.CreateFromTask(SaveAsProject);
            ExitCommand = ReactiveCommand.Create(Exit);
            SaveWorkspaceGlobalCommand = ReactiveCommand.CreateFromTask(SaveWorkspaceGlobal);
            ResetWorkspaceToGlobalCommand = ReactiveCommand.CreateFromTask(ResetWorkspaceToGlobal);
            ResetWorkspaceToDefaultCommand = ReactiveCommand.CreateFromTask(ResetWorkspaceToDefault);
            SaveAllProjectsCommand = ReactiveCommand.CreateFromTask(SaveAllProjects);
            CloseTabCommand = ReactiveCommand.CreateFromTask(CloseTab);
            CloseAllTabsCommand = ReactiveCommand.CreateFromTask(CloseAllTabs);
            CloseOtherTabsCommand = ReactiveCommand.CreateFromTask(CloseOtherTabs);

            LoadRecentProjects();

            _logger.LogDebug("Initialized");
        }

        /// <summary>
        /// Загрузить список недавних проектов из настроек
        /// </summary>
        private void LoadRecentProjects()
        {
            RecentProjects.Clear();

            var recentProjects = _settingsService.RecentProjects;

            foreach (var recent in recentProjects.Take(10))
            {
                if (File.Exists(recent.Path))
                {
                    RecentProjects.Add(new RecentProjectItem
                    {
                        FilePath = recent.Path,
                        ProjectName = recent.Name
                    });
                }
            }

            _logger.LogDebug("Loaded {Count} recent projects", RecentProjects.Count);
        }

        /// <summary>
        /// Установить функцию получения активной вкладки
        /// Вызывается из MainWindowViewModel после создания
        /// </summary>
        public void SetActiveTabProvider(Func<DocumentTabViewModel?> getActiveTab)
        {
            _getActiveTab = getActiveTab;
        }

        /// <summary>Создать новый проект (показывает Welcome окно)</summary>
        private async void NewProject()
        {
            _logger.LogDebug("NewProject clicked");

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow != null)
            {
                await App.ShowWelcomeScreen(desktop.MainWindow);
            }
        }

        /// <summary>Открыть существующий проект (показывает Welcome окно)</summary>
        /// <summary>Открыть существующий проект через файловый диалог</summary>
        private async Task OpenProject()
        {
            _logger.LogDebug("OpenProject clicked");

            var tab = await _projectWorkflow.OpenDocumentAsync();
            if (tab != null)
            {
                var existingTab = _tabCollection.FindByPath(tab.FilePath);
                if (existingTab != null)
                {
                    _logger.LogDebug("Project already open, activating tab");
                    _tabCollection.ActiveTab = existingTab;
                    return;
                }

                _tabCollection.Add(tab);
                _tabCollection.ActiveTab = tab;

                LoadRecentProjects();
            }
        }

        /// <summary>Открыть недавний проект</summary>
        private async Task OpenRecentProject(string filePath)
        {
            _logger.LogDebug("Opening recent project: {FilePath}", filePath);

            if (!File.Exists(filePath))
            {
                _logger.LogWarning("File not found: {FilePath}", filePath);

                var item = RecentProjects.FirstOrDefault(r => r.FilePath == filePath);
                if (item != null)
                {
                    RecentProjects.Remove(item);
                }

                return;
            }

            var existingTab = _tabCollection.FindByPath(filePath);
            if (existingTab != null)
            {
                _logger.LogDebug("Project already open, activating tab");
                _tabCollection.ActiveTab = existingTab;
                return;
            }

            var tab = await _projectWorkflow.OpenDocumentAsync(filePath);
            if (tab != null)
            {
                _tabCollection.Add(tab);
                _tabCollection.ActiveTab = tab;
                _settingsService.AddRecentProject(filePath);

                LoadRecentProjects();
            }
        }

        /// <summary>Сохранить активный проект</summary>
        private async Task SaveProject()
        {
            var activeTab = _getActiveTab?.Invoke();

            if (activeTab == null)
            {
                _logger.LogDebug("SaveProject: No active tab");
                return;
            }

            _logger.LogDebug("SaveProject: {TabTitle}", activeTab.Title);
            await _projectWorkflow.SaveDocumentAsync(activeTab);
        }

        /// <summary>Сохранить активный проект как...</summary>
        private async Task SaveAsProject()
        {
            var activeTab = _getActiveTab?.Invoke();

            if (activeTab == null)
            {
                _logger.LogDebug("SaveAsProject: No active tab");
                return;
            }

            _logger.LogDebug("SaveAsProject: {TabTitle}", activeTab.Title);
            await _projectWorkflow.SaveAsDocumentAsync(activeTab);

            LoadRecentProjects();
        }

        /// <summary>Сохранить все открытые проекты у которых есть несохранённые изменения</summary>
        private async Task SaveAllProjects()
        {
            _logger.LogDebug("SaveAllProjects called");

            var allTabs = _tabCollection.Tabs;
            if (!allTabs.Any())
            {
                _logger.LogDebug("SaveAllProjects: no open tabs");
                return;
            }

            int saved = 0;
            int failed = 0;

            foreach (var tab in allTabs.ToList())
            {
                try
                {
                    bool hasChanges = await _projectWorkflow.HasUnsavedChanges(tab);
                    if (!hasChanges)
                    {
                        _logger.LogDebug("No changes in tab: {Title}", tab.Title);
                        continue;
                    }

                    _logger.LogDebug("Saving tab: {Title}", tab.Title);
                    bool success = await _projectWorkflow.SaveDocumentAsync(tab);

                    if (success)
                        saved++;
                    else
                        failed++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error saving tab: {Title}", tab.Title);
                    failed++;
                }
            }

            if (failed > 0)
                _notificationService.ShowError($"Сохранено: {saved}, ошибок: {failed}");
            else if (saved > 0)
                _notificationService.ShowSuccess($"Сохранено проектов: {saved}");
            else
                _logger.LogDebug("SaveAllProjects: nothing to save");
        }

        /// <summary>Закрыть активную вкладку</summary>
        private async Task CloseTab()
        {
            var activeTab = _getActiveTab?.Invoke();
            if (activeTab == null)
            {
                _logger.LogDebug("CloseTab: no active tab");
                return;
            }

            _logger.LogDebug("CloseTab: {Title}", activeTab.Title);

            await SaveWorkspaceBeforeClose(activeTab);

            bool closed = await _projectWorkflow.CloseDocumentAsync(activeTab);
            if (!closed)
            {
                _logger.LogDebug("CloseTab cancelled by user");
                return;
            }

            activeTab.RecoveryBanner = null;
            _tabCollection.Remove(activeTab);
            await HandleNoTabsLeft();
        }

        /// <summary>Закрыть все вкладки</summary>
        private async Task CloseAllTabs()
        {
            _logger.LogDebug("CloseAllTabs called");

            foreach (var tab in _tabCollection.Tabs.ToList())
            {
                await SaveWorkspaceBeforeClose(tab);

                bool closed = await _projectWorkflow.CloseDocumentAsync(tab);
                if (!closed)
                {
                    _logger.LogDebug("CloseAllTabs: close cancelled on tab {Title}", tab.Title);
                    continue;
                }

                tab.RecoveryBanner = null;
                _tabCollection.Remove(tab);
            }

            await HandleNoTabsLeft();
        }


        /// <summary>Закрыть все вкладки кроме активной</summary>
        private async Task CloseOtherTabs()
        {
            var activeTab = _getActiveTab?.Invoke();
            if (activeTab == null)
            {
                _logger.LogDebug("CloseOtherTabs: no active tab");
                return;
            }

            _logger.LogDebug("CloseOtherTabs: keeping {Title}", activeTab.Title);

            foreach (var tab in _tabCollection.Tabs.Where(t => t != activeTab).ToList())
            {
                await SaveWorkspaceBeforeClose(tab);

                bool closed = await _projectWorkflow.CloseDocumentAsync(tab);
                if (!closed)
                {
                    _logger.LogDebug("CloseOtherTabs: close cancelled on tab {Title}", tab.Title);
                    continue;
                }

                tab.RecoveryBanner = null;
                _tabCollection.Remove(tab);
            }
        }

        /// <summary>
        /// Сохраняет workspace.json перед закрытием вкладки через AutoSaveService.
        /// Вызывается перед CloseDocumentAsync чтобы не потерять расположение панелей.
        /// </summary>
        private async Task SaveWorkspaceBeforeClose(DocumentTabViewModel tab)
        {
            if (string.IsNullOrEmpty(tab.FilePath)) return;

            var autoSave = _projectWorkflow.GetAutoSaveServiceForProject(tab.FilePath);
            if (autoSave != null)
            {
                _logger.LogDebug("Saving workspace before close: {Title}", tab.Title);
                await autoSave.SaveNowAsync();
            }
        }

        /// <summary>
        /// Вызывается после закрытия вкладок.
        /// Если вкладок не осталось — очищает UI и показывает Welcome Screen.
        /// </summary>
        private async Task HandleNoTabsLeft()
        {
            if (_tabCollection.Tabs.Count > 0) return;

            var mainVM = _mainViewModelProvider?.Invoke();
            mainVM?.ClearUIWhenNoTabs();

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow != null)
            {
                await App.ShowWelcomeScreen(desktop.MainWindow);
            }
        }

        /// <summary>Выход из приложения</summary>
        private void Exit()
        {
            _logger.LogDebug("Exit clicked");

            if (Application.Current?.ApplicationLifetime
                is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow != null)
            {
                desktop.MainWindow.Close();
            }
        }

        /// <summary>
        /// Установить провайдер MainWindowViewModel для доступа к AllWorkModes/AllModules
        /// Вызывается из MainWindowViewModel после создания
        /// </summary>
        public void SetMainViewModelProvider(Func<MainWindowViewModel> provider)
        {
            _mainViewModelProvider = provider;
            _logger.LogDebug("MainViewModel provider set");
        }

        /// <summary>
        /// Сохранить конфигурацию глобально (для всех проектов данного типа)
        /// Применится ко ВСЕМ будущим проектам типа "Novel", "Screenplay" и т.д.
        /// </summary>
        private async Task SaveWorkspaceGlobal()
        {
            var activeTab = _getActiveTab?.Invoke();
            if (activeTab == null)
            {
                _logger.LogDebug("SaveWorkspaceGlobal: No active tab");
                return;
            }

            try
            {
                var project = activeTab.GetProject();
                var projectTypeObj = _projectTypeRegistry.GetById(project.Type);
                string displayName = projectTypeObj?.DisplayName ?? project.Type;

                var result = await _dialogService.ShowMessageAsync(
                    "Сохранить как глобальные настройки?",
                    $"Текущая конфигурация будет применена ко всем новым проектам типа \"{displayName}\". Предыдущие глобальные настройки будут перезаписаны. Продолжить?",
                    MessageBoxType.Warning,
                    MessageBoxButtons.YesNo
                );

                if (result != MessageBoxResult.Yes)
                {
                    _logger.LogDebug("Save global cancelled");
                    return;
                }

                if (activeTab.Workspace == null)
                {
                    _logger.LogWarning("No Workspace on active tab");
                    return;
                }

                var currentWorkModes = activeTab.Workspace.GetAvailableWorkModes();

                var config = new WorkspaceConfig
                {
                    ProjectType = project.Type,
                    Name = $"{project.Type} Configuration",
                    WorkModes = currentWorkModes
                };

                _settingsService.SaveWorkspaceConfig(project.Type, config);

                _notificationService.ShowSuccess($"Конфигурация сохранена для типа {displayName}");
                _logger.LogDebug("Workspace saved globally for: {ProjectType}", project.Type);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving global workspace");
            }
        }

        /// <summary>
        /// Сбросить конфигурацию до глобальной
        /// Удаляет workspace.json из ZIP и перезагружает глобальную конфигурацию
        /// </summary>
        private async Task ResetWorkspaceToGlobal()
        {
            var activeTab = _getActiveTab?.Invoke();
            if (activeTab == null)
            {
                _logger.LogDebug("ResetWorkspaceToGlobal: No active tab");
                return;
            }

            try
            {
                var result = await _dialogService.ShowMessageAsync(
                    "Восстановить из глобальных настроек?",
                    "Локальная конфигурация будет удалена. Продолжить?",
                    MessageBoxType.Warning,
                    MessageBoxButtons.YesNo
                );

                if (result != MessageBoxResult.Yes)
                {
                    _logger.LogDebug("Reset to global cancelled");
                    return;
                }

                if (activeTab.Workspace == null)
                {
                    _logger.LogWarning("No Workspace on active tab");
                    return;
                }

                var project = activeTab.GetProject();
                var fileStorage = activeTab.Context.FileStorage;

                if (fileStorage != null)
                    _workspaceConfigService.DeleteFromZip(fileStorage);

                var globalWorkModes = _workModeConfigService.LoadConfiguration(project.Type, null);

                activeTab.Workspace.ReloadFromGlobalConfig(globalWorkModes);

                var mainVM = _mainViewModelProvider?.Invoke();
                var activeWorkMode = activeTab.Workspace.GetActiveWorkMode();
                mainVM?.ModulePanel.LoadModulesForWorkMode(activeWorkMode);

                _notificationService.ShowSuccess("Конфигурация восстановлена из глобальных настроек");
                _logger.LogDebug("Workspace reset to global");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resetting to global");
            }
        }

        /// <summary>
        /// Сбросить конфигурацию до дефолтной
        /// Вся логика сброса слотов и пересоздания layout делегируется WorkspaceController
        /// </summary>
        private async Task ResetWorkspaceToDefault()
        {
            _logger.LogDebug("ResetWorkspaceToDefault called");

            var activeTab = _getActiveTab?.Invoke();
            if (activeTab == null)
            {
                _logger.LogWarning("No active tab");
                return;
            }

            try
            {
                var result = await _dialogService.ShowMessageAsync(
                    "Сбросить до дефолта?",
                    "Текущий WorkMode будет сброшен до настроек по умолчанию. Продолжить?",
                    MessageBoxType.Warning,
                    MessageBoxButtons.YesNo
                );

                if (result != MessageBoxResult.Yes)
                {
                    _logger.LogDebug("Cancelled");
                    return;
                }

                if (activeTab.Workspace == null)
                {
                    _logger.LogWarning("No Workspace");
                    return;
                }

                var activeWorkMode = activeTab.Workspace.GetActiveWorkMode();
                if (activeWorkMode == null)
                {
                    _logger.LogWarning("No active WorkMode");
                    return;
                }

                var workModeRegistry = App.Services.GetRequiredService<WorkModeRegistry>();
                var registeredWorkMode = workModeRegistry.GetWorkMode(activeWorkMode.WorkModeId);

                if (registeredWorkMode == null)
                {
                    _logger.LogWarning("WorkMode not found in registry: {WorkModeId}", activeWorkMode.WorkModeId);
                    return;
                }

                var defaultConfig = registeredWorkMode.GetDefaultConfig();

                // Сброс слотов, очистка контекста и пересоздание layout — всё в контроллере
                // DockLayout обновится автоматически через WorkspaceChanged event
                activeTab.Workspace.ResetWorkModeToDefault(activeWorkMode, defaultConfig);

                // Только обновляем ModulePanel — остальное сделает OnWorkspaceChanged
                var mainVM = _mainViewModelProvider?.Invoke();
                mainVM?.ModulePanel.LoadModulesForWorkMode(activeWorkMode);

                _notificationService.ShowSuccess($"WorkMode '{activeWorkMode.Title}' сброшен до дефолта");
                _logger.LogDebug("Reset to default complete");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resetting to default");
            }
        }
    }

        /// <summary>
        /// Элемент списка недавних проектов
        /// </summary>
        public class RecentProjectItem
    {
        /// <summary>Полный путь к файлу проекта</summary>
        public string FilePath { get; set; } = "";

        /// <summary>Имя файла для отображения в меню</summary>
        public string ProjectName { get; set; } = "";
    }
}