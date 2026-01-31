using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Dock.Model.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Microsoft.Extensions.DependencyInjection;
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
        private readonly IWorkModeService _workModeService;
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
            IWorkModeService workModeService,
            IZipCacheService cacheService,
            DockFactory dockFactory)
        {
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
            _workModeService = workModeService;
            _dockFactory = dockFactory;
            _cacheService = cacheService;
            _cacheUpdateService = App.Services.GetRequiredService<ICacheUpdateService>();

            MenuBar.SetMainViewModelProvider(() => this);
            MenuBar.SetActiveTabProvider(() => TabBar.ActiveTab);
            WorkModeBar.SetWorkModeSwitchedHandler(OnWorkModeSwitched);
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
            Console.WriteLine("[MainWindowViewModel] Initialized with components");
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
            Console.WriteLine($"[MainWindowViewModel] Tab activated: {tab.Title}");

            if (tab.Workspace == null)
            {
                Console.WriteLine($"[MainWindowViewModel] WARNING: Workspace not initialized for tab: {tab.Title}");
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

            if (!string.IsNullOrEmpty(tab.FilePath) && !tab.Context.IsInCompareMode)
            {
                _cacheUpdateService.Stop();
                _cacheUpdateService.Start(tab.FilePath, () => tab.Workspace.GetActiveModules());
            }

            Console.WriteLine($"[MainWindowViewModel] Tab UI updated");
        }

        /// <summary>
        /// Обработчик переключения WorkMode (из WorkModeBar)
        /// Делегирует в WorkspaceController активной вкладки
        /// </summary>
        private async Task OnWorkModeSwitched(WorkMode newWorkMode)
        {
            Console.WriteLine($"[MainWindowViewModel] WorkMode switch requested: {newWorkMode.Title}");

            var activeTab = TabBar.ActiveTab;
            if (activeTab?.Workspace == null)
            {
                Console.WriteLine($"[MainWindowViewModel] No active tab with Workspace");
                return;
            }

            activeTab.Workspace.SwitchWorkMode(newWorkMode);

            DockLayout = activeTab.Workspace.GetCurrentLayout();
            ModulePanel.LoadModulesForWorkMode(newWorkMode);

            UpdateWorkModeMenuItems();
            UpdateModuleMenuItems();

            Console.WriteLine($"[MainWindowViewModel] WorkMode switched in UI");

            await Task.CompletedTask;
        }

        /// <summary>
        /// Обработчик добавления модуля (из ModulePanel)
        /// Делегирует в WorkspaceController активной вкладки
        /// </summary>
        private void OnModuleAdded(string moduleId)
        {
            Console.WriteLine($"[MainWindowViewModel] Module add requested: {moduleId}");

            var activeTab = TabBar.ActiveTab;
            if (activeTab?.Workspace == null)
            {
                Console.WriteLine($"[MainWindowViewModel] No active tab with Workspace");
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

            Console.WriteLine($"[MainWindowViewModel] Module added in UI");
        }

        /// <summary>
        /// Обработчик удаления модуля (из ModulePanel)
        /// Делегирует в WorkspaceController активной вкладки
        /// </summary>
        private void OnModuleRemoved(string moduleId)
        {
            Console.WriteLine($"[MainWindowViewModel] Module remove requested: {moduleId}");

            var activeTab = TabBar.ActiveTab;
            if (activeTab?.Workspace == null)
            {
                Console.WriteLine($"[MainWindowViewModel] No active tab with Workspace");
                return;
            }

            activeTab.Workspace.RemoveModule(moduleId);

            var moduleItem = ModulePanel.AvailableModules.FirstOrDefault(m => m.ModuleId == moduleId);
            if (moduleItem != null)
            {
                moduleItem.IsActive = false;
                Console.WriteLine($"[MainWindowViewModel] Set IsActive=false for {moduleId}");
            }

            UpdateModuleMenuItems();

            Console.WriteLine($"[MainWindowViewModel] Module removed in UI");
        }

        // ========================================
        // ОБРАБОТЧИКИ СОБЫТИЙ СЕРВИСОВ
        // ========================================

        /// <summary>Обработчик открытия проекта</summary>
        private void OnProjectOpened(DocumentTabViewModel tab)
        {
            Console.WriteLine($"[MainWindowViewModel] Project opened: {tab.Title}");
        }

        /// <summary>Обработчик сохранения проекта</summary>
        private void OnProjectSaved(DocumentTabViewModel tab)
        {
            Console.WriteLine($"[MainWindowViewModel] Project saved: {tab.Title}");
        }

        /// <summary>Обработчик закрытия проекта</summary>
        private void OnProjectClosed(DocumentTabViewModel tab)
        {
            Console.WriteLine($"[MainWindowViewModel] Project closed: {tab.Title}");

            if (!string.IsNullOrEmpty(tab.FilePath) && tab.Workspace != null)
            {
                Console.WriteLine($"[MainWindowViewModel] Saving workspace before closing project");
                tab.Workspace.SaveWorkspaceAsync().Wait();
                Console.WriteLine($"[MainWindowViewModel] Workspace saved for: {tab.Title}");
            }

            _cacheUpdateService.Stop();
        }

        /// <summary>
        /// Очистить UI когда не осталось вкладок
        /// Вызывается из TabBarViewModel после удаления последней вкладки
        /// </summary>
        public void ClearUIWhenNoTabs()
        {
            Console.WriteLine("[MainWindowViewModel] ClearUIWhenNoTabs called");

            _cacheUpdateService.Stop();
            Console.WriteLine("[MainWindowViewModel] Caching stopped (no tabs)");

            DockLayout = null;

            WorkModeBar.LoadWorkModes(new List<WorkMode>());

            ModulePanel.Clear();

            Console.WriteLine("[MainWindowViewModel] UI completely cleared - EMPTY!");
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
                Console.WriteLine($"[InitializeWorkModesForTab] WARNING: Workspace not initialized");
                return;
            }

            Console.WriteLine($"[InitializeWorkModesForTab] Updating UI for: {tab.Title}");

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
                Console.WriteLine("[GetActiveModules] No active tab with Workspace");
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
            Console.WriteLine("[MainWindowViewModel] Dock factory initialized");
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

            Console.WriteLine("[MainWindowViewModel] Hot keys registered");
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
            Console.WriteLine($"[LoadProject] Loading: {filePath}");

            var existingTab = _tabCollection.FindByPath(filePath);
            if (existingTab != null)
            {
                Console.WriteLine($"[LoadProject] Project already open");
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

            Console.WriteLine($"[InitializeMenuItems] Loaded {AllModules.Count} modules from metadata");

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

            Console.WriteLine($"[InitializeMenuItems] Loaded {AllWorkModes.Count} WorkModes from registry");
        }

        /// <summary>Открыть/переключить WorkMode</summary>
        private void ToggleWorkMode(string workModeId)
        {
            Console.WriteLine($"[ToggleWorkMode] Toggling: {workModeId}");

            var activeTab = TabBar.ActiveTab;
            if (activeTab?.Workspace == null)
            {
                Console.WriteLine("[ToggleWorkMode] No active tab with Workspace");
                return;
            }

            var workModeBar = WorkModeBar;
            var existingWorkMode = workModeBar.WorkModes.FirstOrDefault(wm => wm.WorkModeId == workModeId);

            if (existingWorkMode != null)
            {
                Console.WriteLine($"[ToggleWorkMode] WorkMode exists, switching to it");
                workModeBar.SwitchWorkModeCommand.Execute(existingWorkMode).Subscribe();
            }
            else
            {
                Console.WriteLine($"[ToggleWorkMode] WorkMode not found, creating new");

                var project = GetProjectForTab(activeTab);
                if (project == null)
                {
                    Console.WriteLine("[ToggleWorkMode] No active project");
                    return;
                }

                var workModeRegistry = App.Services.GetRequiredService<Writersword.Src.WorkModes.Common.WorkModeRegistry>();
                var workModeInstance = workModeRegistry.GetWorkMode(workModeId);

                if (workModeInstance == null)
                {
                    Console.WriteLine($"[ToggleWorkMode] WorkMode not found in registry: {workModeId}");
                    return;
                }

                var newWorkMode = _workModeService.AddWorkMode(
                    workModeId,
                    workModeInstance.DisplayName,
                    workModeInstance.Icon
                );

                newWorkMode.IsCloseable = workModeInstance.IsCloseable;
                newWorkMode.Order = workModeInstance.Order;

                WorkModeBar.LoadWorkModes(_workModeService.GetAllWorkModes());
                workModeBar.SwitchWorkModeCommand.Execute(newWorkMode).Subscribe();

                Console.WriteLine($"[ToggleWorkMode] Created and switched to: {newWorkMode.Title}");
            }

            UpdateWorkModeMenuItems();
        }

        /// <summary>
        /// Открыть модуль через меню (делегирует в ModulePanel)
        /// Повторный клик АКТИВИРУЕТ модуль, но НЕ закрывает
        /// </summary>
        private void ToggleModule(string moduleId)
        {
            Console.WriteLine($"[ToggleModule] Menu clicked: {moduleId}");

            var activeTab = TabBar.ActiveTab;
            if (activeTab?.Workspace == null)
            {
                Console.WriteLine($"[ToggleModule] No active tab with Workspace");
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
                    Console.WriteLine($"[FocusDocumentRecursive] Found and focusing: {documentId}");
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
                Console.WriteLine("[UpdateModuleMenuItems] No active WorkMode - all disabled");
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
                Console.WriteLine("[UpdateModuleMenuItems] No active WorkMode - all disabled");
                foreach (var menuItem in AllModules)
                {
                    menuItem.IsEnabled = false;
                    menuItem.IsChecked = false;
                }
                return;
            }

            Console.WriteLine($"[UpdateModuleMenuItems] Updating for WorkMode: {activeWorkMode.Title}");

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

                Console.WriteLine($"  {menuItem.Icon} {menuItem.Name}: Enabled={menuItem.IsEnabled}, Checked={menuItem.IsChecked}");
            }
        }

        /// <summary>
        /// Обработчик закрытия модуля пользователем через крестик в Dock
        /// Делегирует в WorkspaceController активной вкладки
        /// </summary>
        public void HandleModuleClosedInDock(string moduleId)
        {
            Console.WriteLine($"[MainWindowViewModel] Module closed in dock: {moduleId}");

            var activeTab = TabBar.ActiveTab;
            if (activeTab?.Workspace == null)
            {
                Console.WriteLine($"[MainWindowViewModel] No active tab with Workspace");
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
            Console.WriteLine("[MainWindowViewModel] Saving workspace for ACTIVE tab");

            var activeTab = _tabCollection.ActiveTab;
            if (activeTab?.Workspace == null)
            {
                Console.WriteLine("[MainWindowViewModel] No active tab with Workspace");
                return;
            }

            Console.WriteLine($"[MainWindowViewModel] Force saving workspace for: {activeTab.Title}");
            await activeTab.Workspace.SaveWorkspaceAsync();
        }
    }
}