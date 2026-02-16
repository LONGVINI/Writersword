using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Xaml.Interactivity;
using Dock.Model.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using HarfBuzzSharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Threading.Tasks;
using Writersword.Core.Enums;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Interfaces.Services;
using Writersword.Core.Models.Project;
using Writersword.Core.Models.Settings;
using Writersword.Core.Models.WorkModes;
using Writersword.Modules.Common;
using Writersword.Src.Core.Interfaces.Services;
using Writersword.Src.Core.Interfaces.Services.Input;
using Writersword.Src.Core.Interfaces.Services.Storage;
using Writersword.Src.Core.Interfaces.Services.UI;
using Writersword.Src.Core.Interfaces.WorkFlows;
using Writersword.Src.Core.Interfaces.WorkModes;
using Writersword.Src.Infrastructure.Dock;
using Writersword.Src.Infrastructure.Services.Tabs;
using Writersword.ViewModels.Components;
using Writersword.Views;

namespace Writersword.ViewModels
{
    /// <summary>
    /// ViewModel главного окна приложения
    /// Координирует компоненты UI и делегирует управление в WorkspaceController
    /// 
    /// АРХИТЕКТУРА:
    /// - MenuBar: управление файлами (New, Open, Save)
    /// - TabBar: управление вкладками документов
    /// - WorkModeBar: переключение режимов работы
    /// - ModulePanel: добавление/удаление модулей
    /// - NotificationService: всплывающие уведомления
    /// 
    /// ИЗОЛЯЦИЯ:
    /// - Вся логика управления WorkModes/Layout/Float окнами находится в WorkspaceController
    /// - MainWindowViewModel только отображает UI активной вкладки
    /// </summary>
    public class MainWindowViewModel : ViewModelBase
    {
        private readonly ILogger<MainWindowViewModel> _logger;

        // ========================================
        // КОМПОНЕНТЫ UI
        // ========================================

        /// <summary>Компонент главного меню</summary>
        public MenuBarViewModel MenuBar { get; }

        /// <summary>Компонент панели вкладок</summary>
        public TabBarViewModel TabBar { get; }

        /// <summary>Компонент панели режимов работы</summary>
        public WorkModeBarViewModel WorkModeBar { get; }

        /// <summary>Компонент панели модулей</summary>
        public ModulePanelViewModel ModulePanel { get; }

        // ========================================
        // СЕРВИСЫ
        // ========================================

        private readonly IProjectWorkflow _projectWorkflow;
        private readonly ITabCollection _tabCollection;
        private readonly IDialogService _dialogService;
        private readonly ISettingsService _settingsService;
        private readonly IProjectService _projectService;
        private readonly IHotKeyService _hotKeyService;
        private readonly IWorkModeConfigurationService _workModeConfigService;
        private readonly DockFactory _dockFactory;
        private readonly IZipCacheService _cacheService;
        private readonly ICacheUpdateService _cacheUpdateService;

        // ========================================
        // СОСТОЯНИЕ
        // ========================================

        private string _title = "Writersword";
        private IRootDock? _dockLayout;

        /// <summary>Словарь для отслеживания флоат-модулей каждой вкладки (tabId -> List moduleId)</summary>
        private readonly Dictionary<string, List<string>> _tabFloatModules = new();

        /// <summary>Подписки на изменения в слотах модулей</summary>
        private readonly CompositeDisposable _moduleSlotSubscriptions = new CompositeDisposable();

        /// <summary>Список всех доступных типов модулей с их метаданными</summary>
        public ObservableCollection<ModuleMenuItem> AllModules { get; } = new();

        /// <summary>Список всех доступных WorkMode типов с их метаданными</summary>
        public ObservableCollection<WorkModeMenuItem> AllWorkModes { get; } = new();

        /// <summary>Заголовок окна</summary>
        public string Title
        {
            get => _title;
            set => this.RaiseAndSetIfChanged(ref _title, value);
        }

        /// <summary>Layout для Dock системы</summary>
        public IRootDock? DockLayout
        {
            get => _dockLayout;
            set => this.RaiseAndSetIfChanged(ref _dockLayout, value);
        }

        // ========================================
        // КОМАНДЫ (для горячих клавиш)
        // ========================================

        public ReactiveCommand<Unit, Unit> NewProjectCommand { get; }
        public ReactiveCommand<Unit, Unit> OpenProjectCommand { get; }
        public ReactiveCommand<Unit, Unit> SaveProjectCommand { get; }
        public ReactiveCommand<Unit, Unit> SaveAsProjectCommand { get; }
        public ReactiveCommand<Unit, Unit> ExitCommand { get; }
        public ReactiveCommand<Unit, Unit> CreateNewTabCommand { get; }
        public ReactiveCommand<string, Unit> ToggleWorkModeCommand { get; }
        public ReactiveCommand<string, Unit> ToggleModuleCommand { get; }

        // ========================================
        // КОНСТРУКТОР
        // ========================================

        public MainWindowViewModel(
            MenuBarViewModel menuBar,
            TabBarViewModel tabBar,
            WorkModeBarViewModel workModeBar,
            ModulePanelViewModel modulePanel,
            IProjectWorkflow projectWorkflow,
            ITabCollection tabCollection,
            IDialogService dialogService,
            ISettingsService settingsService,
            IProjectService projectService,
            IHotKeyService hotKeyService,
            IWorkModeConfigurationService workModeConfigService,
            IZipCacheService cacheService,
            DockFactory dockFactory)
        {
            _logger = App.Services.GetService<ILogger<MainWindowViewModel>>()!;

            MenuBar = menuBar;
            TabBar = tabBar;
            WorkModeBar = workModeBar;
            ModulePanel = modulePanel;

            _projectWorkflow = projectWorkflow;
            _tabCollection = tabCollection;
            _dialogService = dialogService;
            _settingsService = settingsService;
            _projectService = projectService;
            _hotKeyService = hotKeyService;
            _workModeConfigService = workModeConfigService;
            _dockFactory = dockFactory;
            _cacheService = cacheService;
            _cacheUpdateService = App.Services.GetRequiredService<ICacheUpdateService>();

            MenuBar.SetMainViewModelProvider(() => this);
            MenuBar.SetActiveTabProvider(() => TabBar.ActiveTab);
            WorkModeBar.SetWorkModeSwitchedHandler(OnWorkModeSwitched);
            WorkModeBar.SetWorkModesReorderedHandler(OnWorkModesReordered);
            ModulePanel.SetModuleHandlers(OnModuleAdded, OnModuleRemoved);
            ModulePanel.SetModuleCheckHandlers(
                moduleId => FindModuleInstance(moduleId).module != null,
                moduleId => FocusModule(moduleId)
            );

            NewProjectCommand = MenuBar.NewProjectCommand;
            OpenProjectCommand = MenuBar.OpenProjectCommand;
            SaveProjectCommand = MenuBar.SaveProjectCommand;
            SaveAsProjectCommand = MenuBar.SaveAsProjectCommand;
            ExitCommand = MenuBar.ExitCommand;
            CreateNewTabCommand = TabBar.CreateNewTabCommand;

            ToggleWorkModeCommand = ReactiveCommand.Create<string>(ToggleWorkMode);
            ToggleModuleCommand = ReactiveCommand.Create<string>(ToggleModule);

            _projectWorkflow.ProjectOpened += OnProjectOpened;
            _projectWorkflow.ProjectSaved += OnProjectSaved;
            _projectWorkflow.ProjectClosed += OnProjectClosed;

            _tabCollection.ActiveTabChanged += (newTab, previousTab) =>
            {
                if (newTab != null)
                    OnTabActivated(newTab, previousTab);

                MenuBar.UpdateHasActiveTab();
            };

            _settingsService.Load();
            RegisterHotKeys();
            InitializeDockFactory();
            InitializeMenuItems();

            _logger.LogDebug("MainWindowViewModel initialized");
        }

        // ========================================
        // ОБРАБОТЧИКИ СОБЫТИЙ КОМПОНЕНТОВ
        // ========================================

        /// <summary>
        /// Обработчик активации вкладки (из TabBar)
        /// Сохраняет предыдущую вкладку, деактивирует её workspace, активирует новую
        /// </summary>
        public async void OnTabActivated(DocumentTabViewModel tab, DocumentTabViewModel? previousTab)
        {
            _logger.LogDebug("Tab activated: {Title}, previous: {PreviousTitle}",
                tab.Title, previousTab?.Title ?? "none");

            // 1. Сохраняем и деактивируем предыдущую вкладку
            if (previousTab != null && previousTab != tab && previousTab.Workspace != null)
            {
                _logger.LogDebug("Deactivating previous tab workspace: {Title}", previousTab.Title);

                // Сохраняем workspace.json
                await previousTab.Workspace.SaveWorkspaceAsync();
                _logger.LogDebug("Previous workspace saved");

                // Деактивируем
                previousTab.Workspace.Deactivate();
                _logger.LogDebug("Previous tab deactivated successfully");
            }

            // 2. ЛЕНИВАЯ ИНИЦИАЛИЗАЦИЯ: если workspace не загружен - загружаем
            if (!tab.IsLoaded)
            {
                _logger.LogDebug("Tab not loaded, initializing workspace: {Title}", tab.Title);

                var projectWorkflow = App.Services.GetRequiredService<IProjectWorkflow>();
                bool success = await projectWorkflow.EnsureWorkspaceInitialized(tab);

                if (!success)
                {
                    _logger.LogError("Failed to initialize workspace for: {Title}", tab.Title);
                    return;
                }

                _logger.LogDebug("Workspace initialized successfully: {Title}", tab.Title);
            }

            // 3. Очищаем текущий layout
            DockLayout = null!;
            _logger.LogDebug("DockLayout cleared in MainWindow");

            if (tab.Workspace == null)
            {
                _logger.LogWarning("Workspace still null after initialization for tab: {Title}", tab.Title);
                return;
            }

            // 4. Активируем workspace
            _logger.LogDebug("Ensuring workspace is activated: {Title}", tab.Title);
            tab.EnsureWorkspaceActivated();
            _logger.LogDebug("Workspace activation complete: {Title}", tab.Title);

            // 5. Получаем layout ПОСЛЕ активации
            DockLayout = tab.Workspace.GetCurrentLayout();
            _logger.LogDebug("DockLayout set to new tab");

            var workModes = tab.Workspace.GetAvailableWorkModes();
            WorkModeBar.LoadWorkModes(workModes);

            var activeWorkMode = tab.Workspace.GetActiveWorkMode();
            if (activeWorkMode != null)
            {
                ModulePanel.LoadModulesForWorkMode(activeWorkMode);
            }

            UpdateWorkModeMenuItems();
            UpdateModuleMenuItems();

            tab.Workspace.WorkspaceChanged -= OnWorkspaceChanged;
            tab.Workspace.WorkspaceChanged += OnWorkspaceChanged;

            if (tab.Context.IsInCompareMode)
            {
                _cacheUpdateService.Stop();
                tab.Workspace.RefreshModulesFromContext();
                _logger.LogDebug("Compare mode - cache disabled, modules read-only");
            }
            else if (!string.IsNullOrEmpty(tab.FilePath))
            {
                _cacheUpdateService.Stop();
                _cacheUpdateService.Start(tab.FilePath, () => tab.Workspace.GetActiveModules());
            }

            _logger.LogDebug("Tab UI updated");
        }

        /// <summary>
        /// Обработчик изменений Workspace
        /// </summary>
        private void OnWorkspaceChanged(object? sender, EventArgs e)
        {
            _logger.LogDebug("Workspace changed, updating UI");

            var activeTab = TabBar.ActiveTab;
            if (activeTab?.Workspace == null) return;

            var activeWorkMode = activeTab.Workspace.GetActiveWorkMode();
            if (activeWorkMode != null)
            {
                ModulePanel.LoadModulesForWorkMode(activeWorkMode);
            }

            UpdateModuleMenuItems();
        }

        /// <summary>
        /// Обработчик переключения WorkMode (из WorkModeBar)
        /// Делегирует в WorkspaceController активной вкладки
        /// </summary>
        private async Task OnWorkModeSwitched(WorkMode newWorkMode)
        {
            _logger.LogDebug("WorkMode switch requested: {Title}", newWorkMode.Title);

            var activeTab = TabBar.ActiveTab;
            if (activeTab?.Workspace == null)
            {
                _logger.LogDebug("No active tab with Workspace");
                return;
            }

            activeTab.Workspace.SwitchWorkMode(newWorkMode);

            DockLayout = activeTab.Workspace.GetCurrentLayout();
            ModulePanel.LoadModulesForWorkMode(newWorkMode);

            UpdateWorkModeMenuItems();
            UpdateModuleMenuItems();

            _logger.LogDebug("WorkMode switched in UI");

            await Task.CompletedTask;
        }

        /// <summary>
        /// Обработчик сохранения порядка WorkModes после drag-and-drop (из WorkModeBar)
        /// Делегирует в WorkspaceController активной вкладки для сохранения в workspace.json
        /// </summary>
        private void OnWorkModesReordered()
        {
            _logger.LogDebug("WorkModes reordered, saving workspace");

            var activeTab = TabBar.ActiveTab;
            if (activeTab?.Workspace == null)
            {
                _logger.LogDebug("No active tab with Workspace");
                return;
            }

            activeTab.Workspace.SaveWorkspaceAsync();

            _logger.LogDebug("WorkModes order saved for: {Title}", activeTab.Title);
        }

        /// <summary>
        /// Обработчик добавления модуля (из ModulePanel)
        /// Делегирует в WorkspaceController активной вкладки
        /// </summary>
        private void OnModuleAdded(string moduleId)
        {
            _logger.LogDebug("Module add requested: {ModuleId}", moduleId);

            var activeTab = TabBar.ActiveTab;
            if (activeTab?.Workspace == null)
            {
                _logger.LogDebug("No active tab with Workspace");
                return;
            }

            activeTab.Workspace.AddModule(moduleId);

            DockLayout = activeTab.Workspace.GetCurrentLayout();

            var moduleItem = ModulePanel.AvailableModules.FirstOrDefault(m => m.ModuleId == moduleId);
            if (moduleItem != null)
            {
                moduleItem.IsActive = true;
            }

            UpdateModuleMenuItems();
            FocusModule(moduleId);

            _logger.LogDebug("Module added in UI");
        }

        /// <summary>
        /// Обработчик удаления модуля (из ModulePanel)
        /// Делегирует в WorkspaceController активной вкладки
        /// </summary>
        private void OnModuleRemoved(string moduleId)
        {
            _logger.LogDebug("Module remove requested: {ModuleId}", moduleId);

            var activeTab = TabBar.ActiveTab;
            if (activeTab?.Workspace == null)
            {
                _logger.LogDebug("No active tab with Workspace");
                return;
            }

            activeTab.Workspace.RemoveModule(moduleId);

            var moduleItem = ModulePanel.AvailableModules.FirstOrDefault(m => m.ModuleId == moduleId);
            if (moduleItem != null)
            {
                moduleItem.IsActive = false;
                _logger.LogDebug("Set IsActive=false for {ModuleId}", moduleId);
            }

            UpdateModuleMenuItems();

            _logger.LogDebug("Module removed in UI");
        }

        // ========================================
        // ОБРАБОТЧИКИ СОБЫТИЙ СЕРВИСОВ
        // ========================================

        /// <summary>Обработчик открытия проекта</summary>
        private void OnProjectOpened(DocumentTabViewModel tab)
        {
            _logger.LogInformation("Project opened: {Title}", tab.Title);
        }

        /// <summary>Обработчик сохранения проекта</summary>
        private void OnProjectSaved(DocumentTabViewModel tab)
        {
            _logger.LogInformation("Project saved: {Title}", tab.Title);
        }

        /// <summary>Обработчик закрытия проекта</summary>
        private void OnProjectClosed(DocumentTabViewModel tab)
        {
            _logger.LogDebug("Project closed: {Title}", tab.Title);

            if (!string.IsNullOrEmpty(tab.FilePath) && tab.Workspace != null)
            {
                _logger.LogDebug("Saving workspace before closing project");
                tab.Workspace.SaveWorkspaceAsync().Wait();
                _logger.LogDebug("Workspace saved for: {Title}", tab.Title);
            }

            _tabFloatModules.Remove(tab.Id);

            _cacheUpdateService.Stop();
        }

        /// <summary>
        /// Очистить UI когда не осталось вкладок
        /// Вызывается из TabBarViewModel после удаления последней вкладки
        /// </summary>
        public void ClearUIWhenNoTabs()
        {
            _logger.LogDebug("ClearUIWhenNoTabs called");

            _moduleSlotSubscriptions.Clear();

            _cacheUpdateService.Stop();
            _logger.LogDebug("Caching stopped (no tabs)");

            DockLayout = null;

            WorkModeBar.LoadWorkModes(new List<WorkMode>());

            ModulePanel.Clear();

            _tabFloatModules.Clear();

            _logger.LogDebug("UI completely cleared");
        }

        // ========================================
        // ИНИЦИАЛИЗАЦИЯ WORKMODES
        // ========================================

        /// <summary>
        /// Инициализировать WorkModes для вкладки
        /// Теперь только обновляет UI, вся логика в WorkspaceController
        /// </summary>
        public void InitializeWorkModesForTab(DocumentTabViewModel tab)
        {
            if (tab.Workspace == null)
            {
                _logger.LogWarning("Workspace not initialized");
                return;
            }

            _logger.LogDebug("Updating UI for tab: {Title}", tab.Title);

            AllWorkModes.Clear();

            OnTabActivated(tab, null);
        }

        // ========================================
        // РАБОТА С ФЛОАТ-ОКНАМИ И МОДУЛЯМИ
        // ========================================

        /// <summary>
        /// Получить ВСЕ открытые модули текущей вкладки (дочерние + флоат)
        /// </summary>
        private List<IModule> GetAllOpenModules()
        {
            var activeTab = TabBar.ActiveTab;
            if (activeTab?.Workspace == null)
            {
                return new List<IModule>();
            }

            var allModules = new List<IModule>();

            allModules.AddRange(activeTab.Workspace.GetActiveModules());

            if (DockLayout?.Windows != null)
            {
                foreach (var window in DockLayout.Windows)
                {
                    var floatModules = GetModulesFromFloatWindow(window);
                    allModules.AddRange(floatModules);
                }
            }

            _logger.LogDebug("Total open modules: {Count} (docked + float)", allModules.Count);
            return allModules;
        }

        /// <summary>
        /// Получить модули из флоат-окна
        /// </summary>
        private List<IModule> GetModulesFromFloatWindow(IDockWindow window)
        {
            var modules = new List<IModule>();

            if (window.Layout is IDock floatLayout)
            {
                CollectModulesRecursive(floatLayout, modules);
            }

            return modules;
        }

        /// <summary>
        /// Рекурсивно собрать модули из Dock структуры
        /// </summary>
        private void CollectModulesRecursive(IDockable dockable, List<IModule> result)
        {
            if (dockable is Document document && document.Content is Avalonia.Controls.Control control)
            {
                if (control.DataContext is object viewModel)
                {
                    var activeTab = TabBar.ActiveTab;
                    if (activeTab != null)
                    {
                        var allModules = activeTab.ModuleContext.GetAllModules();
                        var module = allModules.FirstOrDefault(m => m.ViewModel == viewModel);
                        if (module != null)
                        {
                            result.Add(module);
                        }
                    }
                }
            }

            if (dockable is IDock dock && dock.VisibleDockables != null)
            {
                foreach (var child in dock.VisibleDockables)
                {
                    CollectModulesRecursive(child, result);
                }
            }
        }

        /// <summary>
        /// Найти модуль по ModuleId (в дочерних или флоат-окнах)
        /// Возвращает найденный модуль и флаг isFloat
        /// </summary>
        private (IModule? module, bool isFloat) FindModuleInstance(string moduleId)
        {
            _logger.LogDebug("FindModuleInstance called for: {ModuleId}", moduleId);

            var activeTab = TabBar.ActiveTab;
            if (activeTab?.Workspace == null)
            {
                _logger.LogDebug("No active tab, returning null");
                return (null, false);
            }

            var allOpenModules = GetAllOpenModules();
            _logger.LogDebug("AllOpenModules count: {Count}", allOpenModules.Count);

            var foundModule = allOpenModules.FirstOrDefault(m => m.Metadata.ModuleId == moduleId);

            if (foundModule == null)
            {
                _logger.LogDebug("Module {ModuleId} not found in open modules", moduleId);
                return (null, false);
            }

            _logger.LogDebug("Found module {ModuleId}, checking if float...", moduleId);

            if (DockLayout?.Windows != null)
            {
                foreach (var window in DockLayout.Windows)
                {
                    var floatModules = GetModulesFromFloatWindow(window);
                    if (floatModules.Any(m => m.InstanceId == foundModule.InstanceId))
                    {
                        _logger.LogDebug("Module {ModuleId} is in float window", moduleId);
                        return (foundModule, true);
                    }
                }
            }

            _logger.LogDebug("Module {ModuleId} is in dock", moduleId);
            return (foundModule, false);
        }

        /// <summary>
        /// Закрыть все флоат-окна текущей вкладки
        /// </summary>
        private void CloseAllFloatWindows()
        {
            if (DockLayout?.Windows == null || DockLayout.Windows.Count == 0)
            {
                return;
            }

            _logger.LogDebug("Closing {Count} float windows", DockLayout.Windows.Count);

            foreach (var window in DockLayout.Windows.ToList())
            {
                if (window.Host is HostWindow hostWindow)
                {
                    hostWindow.Exit();
                    _logger.LogDebug("Closed float window: {WindowId}", window.Id);
                }
            }

            DockLayout.Windows.Clear();
            _logger.LogDebug("All float windows closed");
        }

        /// <summary>
        /// Восстановить флоат-окна для вкладки из workspace.json
        /// </summary>
        private void RestoreFloatWindows(DocumentTabViewModel tab)
        {
            if (DockLayout == null || tab.Workspace == null)
            {
                return;
            }

            var activeWorkMode = tab.Workspace.GetActiveWorkMode();
            if (activeWorkMode == null)
            {
                return;
            }

            var floatingSlots = activeWorkMode.ModuleSlots.Where(s => s.IsFloating).ToList();

            if (floatingSlots.Count == 0)
            {
                _logger.LogDebug("No floating modules to restore");
                return;
            }

            _logger.LogDebug("Restoring {Count} float windows", floatingSlots.Count);

            _dockFactory.CreateFloatingWindows(DockLayout, activeWorkMode);

            _logger.LogDebug("Float windows restored");
        }

        // ========================================
        // ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ
        // ========================================

        /// <summary>
        /// Получить список активных модулей текущей вкладки
        /// Делегирует в WorkspaceController
        /// </summary>
        public List<IModule> GetActiveModules()
        {
            var activeTab = TabBar.ActiveTab;
            if (activeTab?.Workspace == null)
            {
                _logger.LogDebug("No active tab with Workspace");
                return new List<IModule>();
            }

            return activeTab.Workspace.GetActiveModules();
        }

        /// <summary>Получить проект для вкладки</summary>
        private ProjectFile? GetProjectForTab(DocumentTabViewModel tab)
        {
            var filePath = tab.FilePath;
            if (string.IsNullOrEmpty(filePath)) return null;

            return _projectService.GetProjectByPath(filePath);
        }

        /// <summary>Инициализировать Dock фабрику</summary>
        private void InitializeDockFactory()
        {
            _dockFactory.Initialize();
            _logger.LogDebug("Dock factory initialized");
        }

        // ========================================
        // ГОРЯЧИЕ КЛАВИШИ
        // ========================================

        /// <summary>Регистрация горячих клавиш</summary>
        private void RegisterHotKeys()
        {
            _hotKeyService.Register("file.new", new HotKey
            {
                DisplayNameKey = "HotKey_File_New",
                DefaultGesture = new KeyGesture(Key.N, KeyModifiers.Control)
            }, NewProjectCommand);

            _hotKeyService.Register("file.open", new HotKey
            {
                DisplayNameKey = "HotKey_File_Open",
                DefaultGesture = new KeyGesture(Key.O, KeyModifiers.Control)
            }, OpenProjectCommand);

            _hotKeyService.Register("file.save", new HotKey
            {
                DisplayNameKey = "HotKey_File_Save",
                DefaultGesture = new KeyGesture(Key.S, KeyModifiers.Control)
            }, SaveProjectCommand);

            _hotKeyService.Register("file.saveas", new HotKey
            {
                DisplayNameKey = "HotKey_File_SaveAs",
                DefaultGesture = new KeyGesture(Key.S, KeyModifiers.Control | KeyModifiers.Shift)
            }, SaveAsProjectCommand);

            _hotKeyService.Register("file.closetab", new HotKey
            {
                DisplayNameKey = "HotKey_File_CloseTab",
                DefaultGesture = new KeyGesture(Key.W, KeyModifiers.Control)
            }, ReactiveCommand.CreateFromTask(async () =>
            {
                if (TabBar.ActiveTab != null)
                    await _projectWorkflow.CloseDocumentAsync(TabBar.ActiveTab);
            }));

            _hotKeyService.Register("file.newtab", new HotKey
            {
                DisplayNameKey = "HotKey_File_NewTab",
                DefaultGesture = new KeyGesture(Key.T, KeyModifiers.Control)
            }, CreateNewTabCommand);

            _logger.LogDebug("Hot keys registered");
        }

        // ========================================
        // ПУБЛИЧНЫЕ МЕТОДЫ (для App.axaml.cs)
        // ========================================

        /// <summary>
        /// Загрузить проект при старте приложения
        /// Вызывается из App.axaml.cs при восстановлении сессии
        /// </summary>
        public async void LoadProject(string filePath)
        {
            _logger.LogDebug("Loading project: {Path}", filePath);

            var existingTab = _tabCollection.FindByPath(filePath);
            if (existingTab != null)
            {
                _logger.LogDebug("Project already open");
                _tabCollection.ActiveTab = existingTab;
                return;
            }

            var tab = await _projectWorkflow.OpenDocumentAsync(filePath);
            if (tab != null)
            {
                _tabCollection.Add(tab);
                _tabCollection.ActiveTab = tab;
                _settingsService.AddRecentProject(filePath);
            }
        }

        /// <summary>Элемент меню для модуля</summary>
        public class ModuleMenuItem : ReactiveObject
        {
            private bool _isEnabled;
            private bool _isChecked;

            public string ModuleId { get; set; } = "";
            public string Name { get; set; } = "";
            public string Icon { get; set; } = "";
            public bool IsUniversal { get; set; }

            /// <summary>Доступен ли модуль для текущего WorkMode</summary>
            public bool IsEnabled
            {
                get => _isEnabled;
                set => this.RaiseAndSetIfChanged(ref _isEnabled, value);
            }

            /// <summary>Включен ли модуль в текущем WorkMode</summary>
            public bool IsChecked
            {
                get => _isChecked;
                set => this.RaiseAndSetIfChanged(ref _isChecked, value);
            }
        }

        /// <summary>Элемент меню для WorkMode</summary>
        public class WorkModeMenuItem : ReactiveObject
        {
            private bool _isChecked;

            public string WorkModeId { get; set; } = "";
            public string Name { get; set; } = "";
            public string Icon { get; set; } = "";

            public bool IsChecked
            {
                get => _isChecked;
                set => this.RaiseAndSetIfChanged(ref _isChecked, value);
            }
        }

        /// <summary>
        /// Инициализировать элементы меню для модулей и WorkMode
        /// </summary>
        private void InitializeMenuItems()
        {
            var moduleFactory = App.Services.GetRequiredService<ModuleFactory>();
            var workModeRegistry = App.Services.GetRequiredService<Src.WorkModes.Common.WorkModeRegistry>();

            var allModuleMetadata = moduleFactory.GetAllModuleMetadata();

            foreach (var metadata in allModuleMetadata)
            {
                AllModules.Add(new ModuleMenuItem
                {
                    ModuleId = metadata.ModuleId,
                    Name = metadata.DisplayName,
                    IsUniversal = metadata.IsUniversal,
                    IsEnabled = false,
                    IsChecked = false
                });
            }

            _logger.LogDebug("Loaded {Count} modules from metadata", AllModules.Count);

            var allWorkModes = workModeRegistry.GetAll();

            foreach (var workMode in allWorkModes)
            {
                AllWorkModes.Add(new WorkModeMenuItem
                {
                    WorkModeId = workMode.Id,
                    Name = workMode.DisplayName,
                    Icon = workMode.Icon,
                    IsChecked = false
                });
            }

            _logger.LogDebug("Loaded {Count} WorkModes from registry", AllWorkModes.Count);
        }

        /// <summary>Открыть/переключить WorkMode</summary>
        private void ToggleWorkMode(string workModeId)
        {
            _logger.LogDebug("Toggling WorkMode: {WorkModeId}", workModeId);

            var activeTab = TabBar.ActiveTab;
            if (activeTab?.Workspace == null)
            {
                _logger.LogDebug("No active tab with Workspace");
                return;
            }

            var workModeBar = WorkModeBar;
            var existingWorkMode = workModeBar.WorkModes.FirstOrDefault(wm => wm.WorkModeId == workModeId);

            if (existingWorkMode != null)
            {
                _logger.LogDebug("WorkMode exists, switching to it");
                workModeBar.SwitchWorkModeCommand.Execute(existingWorkMode).Subscribe();
            }
            else
            {
                _logger.LogDebug("WorkMode not found, creating new");

                var project = GetProjectForTab(activeTab);
                if (project == null)
                {
                    _logger.LogDebug("No active project");
                    return;
                }

                var workModeRegistry = App.Services.GetRequiredService<Writersword.Src.WorkModes.Common.WorkModeRegistry>();
                var workModeInstance = workModeRegistry.GetWorkMode(workModeId);

                if (workModeInstance == null)
                {
                    _logger.LogWarning("WorkMode not found in registry: {WorkModeId}", workModeId);
                    return;
                }

                var workModeService = activeTab.Workspace.GetWorkModeService();
                if (workModeService == null)
                {
                    _logger.LogWarning("No WorkModeService available");
                    return;
                }

                var newWorkMode = workModeService.AddWorkMode(
                    workModeId,
                    workModeInstance.DisplayName,
                    workModeInstance.Icon
                );

                newWorkMode.IsCloseable = workModeInstance.IsCloseable;
                newWorkMode.Order = workModeInstance.Order;

                WorkModeBar.LoadWorkModes(workModeService.GetAllWorkModes());
                workModeBar.SwitchWorkModeCommand.Execute(newWorkMode).Subscribe();

                _logger.LogInformation("Created and switched to WorkMode: {Title}", newWorkMode.Title);
            }

            UpdateWorkModeMenuItems();
        }

        /// <summary>
        /// Открыть модуль через меню
        /// Если модуль уже открыт - фокусирует его окно
        /// Если не открыт - создаёт новый
        /// </summary>
        private void ToggleModule(string moduleId)
        {
            _logger.LogDebug("Menu clicked for module: {ModuleId}", moduleId);

            var activeTab = TabBar.ActiveTab;
            if (activeTab?.Workspace == null)
            {
                _logger.LogDebug("No active tab with Workspace");
                return;
            }

            var (existingModule, isFloat) = FindModuleInstance(moduleId);

            if (existingModule != null)
            {
                _logger.LogDebug("Module already open (float={IsFloat}), focusing", isFloat);
                FocusModule(moduleId);
                return;
            }

            _logger.LogDebug("Module not open, creating new");
            ModulePanel.OpenModule(moduleId);
            FocusModule(moduleId);
            UpdateModuleMenuItems();
        }

        /// <summary>
        /// Найти и активировать вкладку модуля в UI
        /// Поддерживает поиск как в Dock панелях, так и в Float окнах
        /// </summary>
        private void FocusModule(string moduleId)
        {
            if (DockLayout == null) return;

            string documentId = $"Module_{moduleId}";

            if (FocusDocumentInFloatWindow(DockLayout, documentId))
            {
                _logger.LogDebug("Module found and focused in float window: {ModuleId}", moduleId);
                return;
            }

            if (FocusDocumentRecursive(DockLayout, documentId))
            {
                _logger.LogDebug("Module found and focused in dock: {ModuleId}", moduleId);
                return;
            }

            _logger.LogWarning("Module not found in UI: {ModuleId}", moduleId);
        }

        /// <summary>
        /// Попытаться найти и активировать документ во Float окне
        /// </summary>
        private bool FocusDocumentInFloatWindow(IRootDock rootDock, string documentId)
        {
            if (rootDock.Windows == null || rootDock.Windows.Count == 0)
                return false;

            foreach (var window in rootDock.Windows)
            {
                if (window.Layout is IDock floatLayout)
                {
                    if (FocusDocumentRecursive(floatLayout, documentId))
                    {
                        if (window.Host is HostWindow hostWindow)
                        {
                            try
                            {
                                if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                                {
                                    if (hostWindow.GetWindow() is FloatingWindow floatWindow)
                                    {
                                        floatWindow.Activate();
                                    }
                                }
                                else
                                {
                                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                                    {
                                        if (hostWindow.GetWindow() is FloatingWindow floatWindow)
                                        {
                                            floatWindow.Activate();
                                        }
                                    });
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Error activating float window");
                            }
                        }
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Рекурсивно найти и активировать Document
        /// </summary>
        private bool FocusDocumentRecursive(IDockable dockable, string documentId)
        {
            if (dockable is IDock dock && dock.VisibleDockables != null)
            {
                var document = dock.VisibleDockables.FirstOrDefault(d => d.Id == documentId);
                if (document != null)
                {
                    _logger.LogDebug("Found and focusing document: {DocumentId}", documentId);
                    dock.ActiveDockable = document;
                    return true;
                }

                foreach (var child in dock.VisibleDockables)
                {
                    if (FocusDocumentRecursive(child, documentId))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>Обновить состояние элементов меню WorkMode</summary>
        private void UpdateWorkModeMenuItems()
        {
            var workModes = WorkModeBar.WorkModes;

            foreach (var menuItem in AllWorkModes)
            {
                menuItem.IsChecked = workModes.Any(wm => wm.WorkModeId == menuItem.WorkModeId);
            }

            // Сортируем WorkModes по Order
            var sorted = AllWorkModes
                .OrderBy(wm => workModes.FirstOrDefault(w => w.WorkModeId == wm.WorkModeId)?.Order ?? int.MaxValue)
                .ToList();

            AllWorkModes.Clear();
            foreach (var item in sorted)
            {
                AllWorkModes.Add(item);
            }
        }

        /// <summary>
        /// Обновить состояние элементов меню модулей
        /// Подписывается на изменения IsCurrentlyOpen в слотах для автоматического обновления
        /// </summary>
        private void UpdateModuleMenuItems()
        {
            var activeTab = TabBar.ActiveTab;
            if (activeTab?.Workspace == null)
            {
                _logger.LogDebug("No active WorkMode - all module menu items disabled");
                foreach (var menuItem in AllModules)
                {
                    menuItem.IsEnabled = false;
                    menuItem.IsChecked = false;
                }
                return;
            }

            var activeWorkMode = activeTab.Workspace.GetActiveWorkMode();
            if (activeWorkMode == null)
            {
                _logger.LogDebug("No active WorkMode - all module menu items disabled");
                foreach (var menuItem in AllModules)
                {
                    menuItem.IsEnabled = false;
                    menuItem.IsChecked = false;
                }
                return;
            }

            _logger.LogDebug("Updating module menu items for WorkMode: {Title}", activeWorkMode.Title);

            ModulePanel.RefreshModuleStates();

            _moduleSlotSubscriptions.Clear();

            foreach (var slot in activeWorkMode.ModuleSlots)
            {

                var subscription = slot.WhenAnyValue(x => x.IsCurrentlyOpen)
                    .Subscribe(_ =>
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            UpdateModuleMenuItemsInternal();
                        });
                    });

                _moduleSlotSubscriptions.Add(subscription);
            }

            UpdateModuleMenuItemsInternal();
        }

        /// <summary>
        /// Внутренний метод обновления меню модулей
        /// Читает состояние напрямую из слотов (single source of truth)
        /// Учитывает категории модулей (Required, Optional, Unwanted, Forbidden)
        /// </summary>
        private void UpdateModuleMenuItemsInternal()
        {
            _logger.LogDebug("AllModules count: {Count}", AllModules.Count);
            _logger.LogDebug("ModulePanel.AvailableModules count: {Count}", ModulePanel.AvailableModules.Count);

            var activeTab = TabBar.ActiveTab;
            if (activeTab?.Workspace == null) return;

            var activeWorkMode = activeTab.Workspace.GetActiveWorkMode();
            if (activeWorkMode == null) return;

            _logger.LogDebug("UpdateModuleMenuItemsInternal for WorkMode: {Title}", activeWorkMode.Title);

            foreach (var menuItem in AllModules)
            {
                // Определяем категорию модуля
                ModuleCategory category;

                if (activeWorkMode.ModuleCategories.TryGetValue(menuItem.ModuleId, out var explicitCategory))
                {
                    category = explicitCategory;
                }
                else
                {
                    // Если не указан явно - по умолчанию Optional
                    category = ModuleCategory.Optional;
                }

                // Ищем слот модуля
                var slot = activeWorkMode.ModuleSlots.FirstOrDefault(s => s.ModuleType == menuItem.ModuleId);

                switch (category)
                {
                    case ModuleCategory.Required:
                        // Обязательный - всегда включён, нельзя выключить
                        menuItem.IsEnabled = true;
                        menuItem.IsChecked = true;
                        _logger.LogDebug("Module {ModuleId}: Required (always enabled)", menuItem.ModuleId);
                        break;

                    case ModuleCategory.Optional:
                        // Обычный - можно включить/выключить
                        menuItem.IsEnabled = true;
                        menuItem.IsChecked = slot != null && slot.IsCurrentlyOpen;
                        _logger.LogDebug("Module {ModuleId}: Optional, Checked={IsChecked}",
                            menuItem.ModuleId, menuItem.IsChecked);
                        break;

                    case ModuleCategory.Unwanted:
                        // Не рекомендуется - можно включить, но показываем предупреждение
                        menuItem.IsEnabled = true;
                        menuItem.IsChecked = slot != null && slot.IsCurrentlyOpen;
                        _logger.LogDebug("Module {ModuleId}: Unwanted, Checked={IsChecked}",
                            menuItem.ModuleId, menuItem.IsChecked);
                        break;

                    case ModuleCategory.Forbidden:
                        // Запрещён - заблокирован
                        menuItem.IsEnabled = false;
                        menuItem.IsChecked = false;
                        _logger.LogDebug("Module {ModuleId}: Forbidden (disabled)", menuItem.ModuleId);
                        break;

                    default:
                        // На всякий случай
                        menuItem.IsEnabled = false;
                        menuItem.IsChecked = false;
                        break;
                }
            }
        }

        /// <summary>
        /// Обработчик закрытия модуля пользователем через крестик
        /// Делегирует в WorkspaceController активной вкладки
        /// </summary>
        public void HandleModuleClosedInDock(string moduleId)
        {
            if (string.IsNullOrWhiteSpace(moduleId))
            {
                _logger.LogWarning("HandleModuleClosedInDock: moduleId is null or empty, ignoring");
                return;
            }

            if (moduleId.Contains("IDockWindow") ||
                moduleId.Contains("Float_") ||
                moduleId.StartsWith("Module_") ||
                moduleId.Contains("Splitter"))
            {
                _logger.LogWarning("HandleModuleClosedInDock: Invalid moduleId '{ModuleId}', ignoring", moduleId);
                return;
            }

            _logger.LogDebug("Module closed in dock: {ModuleId}", moduleId);

            var activeTab = TabBar.ActiveTab;
            if (activeTab?.Workspace == null)
            {
                _logger.LogDebug("No active tab with Workspace");
                return;
            }

            activeTab.Workspace.HandleModuleClosedInDock(moduleId);

            UpdateModuleMenuItems();
        }
    }
}