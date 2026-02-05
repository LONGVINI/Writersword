using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Dock.Model.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
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

            _tabCollection.ActiveTabChanged += tab =>
            {
                if (tab != null)
                    OnTabActivated(tab);

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
        /// Просто показывает UI активной вкладки из её WorkspaceController
        /// Вся логика управления находится в WorkspaceController
        /// </summary>
        public void OnTabActivated(DocumentTabViewModel tab)
        {
            _logger.LogDebug("Tab activated: {Title}", tab.Title);

            if (tab.Workspace == null)
            {
                _logger.LogWarning("Workspace not initialized for tab: {Title}", tab.Title);
                return;
            }

            DockLayout = tab.Workspace.GetCurrentLayout();

            var workModes = tab.Workspace.GetAvailableWorkModes();
            WorkModeBar.LoadWorkModes(workModes);

            var activeWorkMode = tab.Workspace.GetActiveWorkMode();
            if (activeWorkMode != null)
            {
                ModulePanel.LoadModulesForWorkMode(activeWorkMode);
            }

            UpdateWorkModeMenuItems();
            UpdateModuleMenuItems();

            // ВАЖНО: В Compare mode отключаем автосохранение кеша
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

            // Уведомляем WorkspaceAutoSave что изменилась конфигурация
            // Сохранение произойдёт через 5 секунд (debounce)
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

            _cacheUpdateService.Stop();
        }

        /// <summary>
        /// Очистить UI когда не осталось вкладок
        /// Вызывается из TabBarViewModel после удаления последней вкладки
        /// </summary>
        public void ClearUIWhenNoTabs()
        {
            _logger.LogDebug("ClearUIWhenNoTabs called");

            _cacheUpdateService.Stop();
            _logger.LogDebug("Caching stopped (no tabs)");

            DockLayout = null;

            WorkModeBar.LoadWorkModes(new List<WorkMode>());

            ModulePanel.Clear();

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

            // Очищаем старые кнопки WorkMode чтобы не было дубликатов
            AllWorkModes.Clear();

            OnTabActivated(tab);
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
                // Получаем WorkModeService из активной вкладки через её Workspace
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
        /// Открыть модуль через меню (делегирует в ModulePanel)
        /// Повторный клик АКТИВИРУЕТ модуль, но НЕ закрывает
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

            ModulePanel.OpenModule(moduleId);

            var moduleItem = ModulePanel.AvailableModules.FirstOrDefault(m => m.ModuleId == moduleId);
            if (moduleItem?.IsActive == true)
            {
                FocusModule(moduleId);
            }

            UpdateModuleMenuItems();
        }

        /// <summary>
        /// Найти и активировать вкладку модуля в UI
        /// </summary>
        private void FocusModule(string moduleId)
        {
            if (DockLayout == null) return;

            string documentId = $"Module_{moduleId}";
            FocusDocumentRecursive(DockLayout, documentId);
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
        }

        /// <summary>Обновить состояние элементов меню модулей</summary>
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

            foreach (var menuItem in AllModules)
            {
                var moduleInPanel = ModulePanel.AvailableModules.FirstOrDefault(m => m.ModuleId == menuItem.ModuleId);

                if (moduleInPanel != null)
                {
                    menuItem.IsEnabled = true;
                    menuItem.IsChecked = moduleInPanel.IsActive;
                }
                else
                {
                    menuItem.IsEnabled = false;
                    menuItem.IsChecked = false;
                }
            }
        }

        /// <summary>
        /// Обработчик закрытия модуля пользователем через крестик в Dock
        /// Делегирует в WorkspaceController активной вкладки
        /// </summary>
        public void HandleModuleClosedInDock(string moduleId)
        {
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

        /// <summary>
        /// Принудительно сохранить workspace.json для АКТИВНОЙ вкладки
        /// Вызывается при закрытии приложения
        /// </summary>
        public async Task SaveActiveWorkspaceConfigurationAsync()
        {
            _logger.LogDebug("Saving workspace for active tab");

            var activeTab = _tabCollection.ActiveTab;
            if (activeTab?.Workspace == null)
            {
                _logger.LogDebug("No active tab with Workspace");
                return;
            }

            _logger.LogDebug("Force saving workspace for: {Title}", activeTab.Title);
            await activeTab.Workspace.SaveWorkspaceAsync();
        }
    }
}