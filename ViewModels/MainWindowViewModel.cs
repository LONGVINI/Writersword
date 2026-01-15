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
using Writersword.Core.Models;
using Writersword.Core.Models.WorkModes;
using Writersword.Modules.Common;
using Writersword.Src.Core.Interfaces.Services.Input;
using Writersword.Src.Core.Interfaces.Services.Storage;
using Writersword.Src.Core.Interfaces.Services.UI;
using Writersword.Src.Core.Interfaces.WorkFlows;
using Writersword.Src.Core.Interfaces.WorkModes;
using Writersword.Src.Infrastructure.Dock;
using Writersword.ViewModels.Components;
using Writersword.Core.Models.Settings;

namespace Writersword.ViewModels
{
    /// <summary>
    /// ViewModel главного окна приложения
    /// Координирует компоненты UI и управляет Dock системой
    /// 
    /// АРХИТЕКТУРА:
    /// - MenuBar: управление файлами (New, Open, Save)
    /// - TabBar: управление вкладками документов
    /// - WorkModeBar: переключение режимов работы
    /// - ModulePanel: добавление/удаление модулей
    /// - NotificationService: всплывающие уведомления
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
        private readonly ModuleRegistry _moduleRegistry;
        private readonly ICacheService _cacheService;

        // ========================================
        // СОСТОЯНИЕ
        // ========================================

        private readonly Dictionary<string, IRootDock> _tabLayouts = new();
        /// <summary>Список всех доступных типов модулей с их метаданными</summary>
        public ObservableCollection<ModuleMenuItem> AllModules { get; } = new();

        /// <summary>Список всех доступных WorkMode типов с их метаданными</summary>
        public ObservableCollection<WorkModeMenuItem> AllWorkModes { get; } = new();
        private WorkMode? _activeWorkMode;
        private string _title = "Writersword";
        private IRootDock? _dockLayout;

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

        /// <summary>Активный режим работы</summary>
        public WorkMode? ActiveWorkMode
        {
            get => _activeWorkMode;
            set => this.RaiseAndSetIfChanged(ref _activeWorkMode, value);
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
            ICacheService cacheService,
            DockFactory dockFactory)
        {
            // Инициализация компонентов
            MenuBar = menuBar;
            TabBar = tabBar;
            WorkModeBar = workModeBar;
            ModulePanel = modulePanel;

            // Инициализация сервисов
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
            _moduleRegistry = App.Services.GetRequiredService<ModuleRegistry>();

            // Связываем компоненты с MainWindow
            MenuBar.SetMainViewModelProvider(() => this);
            MenuBar.SetActiveTabProvider(() => TabBar.ActiveTab);
            WorkModeBar.SetWorkModeSwitchedHandler(OnWorkModeSwitched);
            ModulePanel.SetModuleHandlers(OnModuleAdded, OnModuleRemoved);

            // Перенаправляем команды на компоненты (для горячих клавиш)
            NewProjectCommand = MenuBar.NewProjectCommand;
            OpenProjectCommand = MenuBar.OpenProjectCommand;
            SaveProjectCommand = MenuBar.SaveProjectCommand;
            SaveAsProjectCommand = MenuBar.SaveAsProjectCommand;
            ExitCommand = MenuBar.ExitCommand;
            CreateNewTabCommand = TabBar.CreateNewTabCommand;

            // Команды для меню
            ToggleWorkModeCommand = ReactiveCommand.Create<string>(ToggleWorkMode);
            ToggleModuleCommand = ReactiveCommand.Create<string>(ToggleModule);

            // Подписываемся на события сервисов
            _projectWorkflow.ProjectOpened += OnProjectOpened;
            _projectWorkflow.ProjectSaved += OnProjectSaved;
            _projectWorkflow.ProjectClosed += OnProjectClosed;

            _tabCollection.ActiveTabChanged += tab =>
            {
                if (tab != null)
                    OnTabActivated(tab);
            };

            // Инициализация
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
        /// Восстанавливает WorkModes и Dock layout для вкладки
        /// </summary>
        private void OnTabActivated(DocumentTabViewModel tab)
        {
            Console.WriteLine($"[MainWindowViewModel] Tab activated: {tab.Title}");

            var project = GetProjectForTab(tab);
            if (project == null) return;

            string tabKey = tab.FilePath ?? tab.Id;

            // Проверяем есть ли уже созданный layout для этой вкладки
            if (_tabLayouts.TryGetValue(tabKey, out var existingLayout))
            {
                Console.WriteLine($"[MainWindowViewModel] Reusing existing layout for tab: {tab.Title}");
                DockLayout = existingLayout;

                // Загружаем WorkModes в компонент
                var workModes = _workModeService.GetAllWorkModes();
                WorkModeBar.LoadWorkModes(workModes);

                RefreshWorkModeUI();

                return;
            }

            // Создаём новый layout для вкладки
            Console.WriteLine($"[MainWindowViewModel] Creating new layout for tab: {tab.Title}");
            InitializeWorkModesForTab(tab);

            if (DockLayout != null)
            {
                _tabLayouts[tabKey] = DockLayout;
            }
        }

        /// <summary>
        /// Обработчик переключения WorkMode (из WorkModeBar)
        /// Показывает модули нового режима
        /// </summary>
        private async Task OnWorkModeSwitched(WorkMode newWorkMode)
        {
            Console.WriteLine($"[MainWindowViewModel] WorkMode switched: {newWorkMode.Title}");

            ActiveWorkMode = newWorkMode;

            // Показываем модули нового WorkMode
            var layout = _dockFactory.CreateLayout(newWorkMode);
            DockLayout = layout;

            // Обновляем панель модулей
            ModulePanel.LoadModulesForWorkMode(newWorkMode);

            Console.WriteLine($"[MainWindowViewModel] Loaded {newWorkMode.ModuleSlots.Count} modules for WorkMode");

            // Обновляем состояние меню
            UpdateWorkModeMenuItems();
            UpdateModuleMenuItems();
        }

        /// <summary>
        /// Обработчик добавления модуля (из ModulePanel)
        /// Добавляет модуль динамически в существующий layout
        /// </summary>
        private void OnModuleAdded(string moduleId)
        {
            Console.WriteLine($"[MainWindowViewModel] Module added: {moduleId}");

            if (ActiveWorkMode == null || DockLayout == null) return;

            // 1. Ищем/создаём слот
            var existingSlot = ActiveWorkMode.ModuleSlots.FirstOrDefault(s => s.ModuleId == moduleId);

            if (existingSlot != null)
            {
                existingSlot.IsVisible = true;
            }
            else
            {
                var newSlot = new ModuleSlot
                {
                    ModuleId = moduleId,
                    IsVisible = true,
                    IsCloseable = _workModeConfigService.CanRemoveModule(ActiveWorkMode.WorkModeId, moduleId),
                    MinWidth = 200,
                    MinHeight = 150,
                    PreferredPosition = PreferredDockPosition.RightAsTab
                };

                ActiveWorkMode.ModuleSlots.Add(newSlot);
                existingSlot = newSlot;
            }

            // 2. ПРОВЕРЯЕМ: Есть ли хоть один видимый модуль?
            var hasVisibleModules = ActiveWorkMode.ModuleSlots.Any(s => s.IsVisible && s.ModuleId != moduleId);

            if (!hasVisibleModules)
            {
                // НЕТ видимых модулей - ПЕРЕСОЗДАЁМ layout!
                Console.WriteLine($"[OnModuleAdded] No visible modules - recreating layout");
                var newLayout = _dockFactory.CreateLayout(ActiveWorkMode);
                DockLayout = newLayout;
            }
            else
            {
                // Есть видимые - добавляем динамически
                Console.WriteLine($"[OnModuleAdded] Adding module dynamically");
                _dockFactory.InsertModuleByPreference(DockLayout, existingSlot);
            }

            // 3. Обновляем UI
            var moduleItem = ModulePanel.AvailableModules.FirstOrDefault(m => m.ModuleId == moduleId);
            if (moduleItem != null)
            {
                moduleItem.IsActive = true;
            }

            UpdateModuleMenuItems();
            FocusModule(moduleId);
        }

        /// <summary>
        /// Обработчик удаления модуля (из ModulePanel)
        /// ДИНАМИЧЕСКИ скрывает модуль БЕЗ пересоздания layout
        /// </summary>
        private void OnModuleRemoved(string moduleId)
        {
            Console.WriteLine($"[MainWindowViewModel] Module removed: {moduleId}");

            if (ActiveWorkMode == null || DockLayout == null) return;

            var slot = ActiveWorkMode.ModuleSlots.FirstOrDefault(s => s.ModuleId == moduleId);
            if (slot != null)
            {
                slot.IsVisible = false;

                // Находим Document и закрываем его
                RemoveModuleFromLayout(DockLayout, moduleId);

                // КРИТИЧНО: Обновляем IsActive в ModulePanel ПЕРЕД обновлением меню!
                var moduleItem = ModulePanel.AvailableModules.FirstOrDefault(m => m.ModuleId == moduleId);
                if (moduleItem != null)
                {
                    moduleItem.IsActive = false;
                    Console.WriteLine($"[OnModuleRemoved] Set IsActive=false for {moduleId}");
                }

                // Обновляем меню (теперь увидит правильный IsActive)
                UpdateModuleMenuItems();
            }
        }

        /// <summary>
        /// Найти и удалить модуль из layout
        /// </summary>
        private void RemoveModuleFromLayout(IRootDock rootDock, string moduleId)
        {
            string documentId = $"Module_{moduleId}";
            Console.WriteLine($"[RemoveModuleFromLayout] Searching for: {documentId}");

            RemoveDocumentRecursive(rootDock, documentId);
        }

        /// <summary>
        /// Рекурсивно найти и удалить Document
        /// </summary>
        private bool RemoveDocumentRecursive(IDockable dockable, string documentId)
        {
            if (dockable is IDock dock && dock.VisibleDockables != null)
            {
                // Ищем документ с нужным ID
                var document = dock.VisibleDockables.FirstOrDefault(d => d.Id == documentId);
                if (document != null)
                {
                    Console.WriteLine($"[RemoveDocumentRecursive] Found document, removing from {dock.Id}");
                    dock.VisibleDockables.Remove(document);

                    // Если это был активный документ - активируем другой
                    if (dock.ActiveDockable == document)
                    {
                        dock.ActiveDockable = dock.VisibleDockables.FirstOrDefault();
                    }

                    return true;
                }

                // Рекурсивно ищем в дочерних элементах
                foreach (var child in dock.VisibleDockables.ToList())
                {
                    if (RemoveDocumentRecursive(child, documentId))
                    {
                        return true;
                    }
                }
            }

            return false;
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

            // Удаляем layout вкладки
            string tabKey = tab.FilePath ?? tab.Id;
            _tabLayouts.Remove(tabKey);
        }

        /// <summary>
        /// Очистить UI когда не осталось вкладок
        /// Вызывается из TabBarViewModel после удаления последней вкладки
        /// </summary>
        public void ClearUIWhenNoTabs()
        {
            Console.WriteLine("[MainWindowViewModel] ClearUIWhenNoTabs called");

            // ПОЛНАЯ ОЧИСТКА UI!
            ActiveWorkMode = null;
            DockLayout = null;

            // Очищаем WorkModeBar
            WorkModeBar.LoadWorkModes(new System.Collections.Generic.List<Core.Models.WorkModes.WorkMode>());

            // Очищаем ModulePanel
            ModulePanel.Clear();

            Console.WriteLine("[MainWindowViewModel] UI completely cleared - EMPTY!");
        }

        // ========================================
        // ИНИЦИАЛИЗАЦИЯ WORKMODES
        // ========================================

        /// <summary>
        /// Инициализировать WorkModes для вкладки
        /// Вызывается при первом открытии вкладки
        /// </summary>
        public void InitializeWorkModesForTab(DocumentTabViewModel tab)
        {
            var project = GetProjectForTab(tab);
            if (project == null) return;

            Console.WriteLine($"[InitializeWorkModesForTab] Initializing for: {tab.Title}");

            // Загружаем WorkModes из проекта или глобальных настроек
            List<WorkMode>? savedWorkModes = null; // TODO: Загрузка из UserConfig
            var workModes = _workModeService.InitializeWorkModes(project.Type, savedWorkModes);

            // Загружаем в компонент WorkModeBar
            WorkModeBar.LoadWorkModes(workModes);

            // Устанавливаем провайдер модулей для переключения WorkMode
            WorkModeBar.SetActiveModulesProvider(() => GetActiveModules(), tab.FilePath);

            // Активируем первый WorkMode
            var activeWM = workModes.FirstOrDefault(wm => wm.IsActive) ?? workModes.FirstOrDefault();
            if (activeWM != null)
            {
                ActiveWorkMode = activeWM;

                // Создаём Dock layout
                var layout = _dockFactory.CreateLayout(activeWM);
                DockLayout = layout;

                // Загружаем модули в панель
                ModulePanel.LoadModulesForWorkMode(activeWM);

                Console.WriteLine($"[InitializeWorkModesForTab] Activated WorkMode: {activeWM.Title}");

                // Обновляем состояние меню
                UpdateWorkModeMenuItems();
                UpdateModuleMenuItems();
            }
        }

        // ========================================
        // ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ
        // ========================================

        /// <summary>
        /// Получить список активных модулей из текущего DockLayout
        /// Используется при переключении WorkMode
        /// </summary>
        public List<IModule> GetActiveModules()
        {
            var modules = new List<IModule>();

            if (DockLayout == null)
            {
                Console.WriteLine("[GetActiveModules] No DockLayout");
                return modules;
            }

            CollectModulesFromDockable(DockLayout, modules);

            Console.WriteLine($"[GetActiveModules] Found {modules.Count} active modules");
            return modules;
        }

        /// <summary>
        /// Рекурсивно собрать модули из Dockable структуры
        /// С ЗАЩИТОЙ ОТ ЦИКЛИЧЕСКИХ ССЫЛОК
        /// </summary>
        private void CollectModulesFromDockable(IDockable dockable, List<IModule> modules)
        {
            // Создаём HashSet для отслеживания посещённых элементов (защита от циклов)
            var visited = new HashSet<IDockable>();
            CollectModulesFromDockableInternal(dockable, modules, visited);
        }

        /// <summary>Внутренний метод с отслеживанием посещённых элементов</summary>
        private void CollectModulesFromDockableInternal(IDockable dockable, List<IModule> modules, HashSet<IDockable> visited)
        {
            if (!visited.Add(dockable))
            {
                Console.WriteLine($"[CollectModules] CYCLE DETECTED: {dockable.Id} already visited!");
                return;
            }

            if (visited.Count > 100)
            {
                Console.WriteLine($"[CollectModules] MAX DEPTH REACHED: stopping at 100 elements");
                return;
            }

            // Если это Document с модулем - получаем модуль через View
            if (dockable is Document document && document.Id?.StartsWith("Module_") == true)
            {
                // Получаем View из Document.Content
                if (document.Content is Avalonia.Controls.Control control &&
                    control.DataContext is object viewModel)
                {
                    // Ищем модуль по ViewModel (более надёжно чем по ModuleId)
                    foreach (var module in _moduleRegistry.GetAllModules())
                    {
                        if (module.ViewModel == viewModel)
                        {
                            modules.Add(module);
                            Console.WriteLine($"[CollectModules] Added module: {module.ModuleId} (Instance: {module.InstanceId})");
                            break;
                        }
                    }
                }
            }

            // Рекурсивно обходим дочерние элементы
            if (dockable is IDock dock && dock.VisibleDockables != null)
            {
                foreach (var child in dock.VisibleDockables)
                {
                    CollectModulesFromDockableInternal(child, modules, visited);
                }
            }
        }

        /// <summary>Получить проект для вкладки</summary>
        private ProjectFile? GetProjectForTab(DocumentTabViewModel tab)
        {
            var filePath = tab.FilePath;
            if (string.IsNullOrEmpty(filePath)) return null;

            return _projectService.GetProjectByPath(filePath);
        }

        /// <summary>Показать Welcome если нет вкладок</summary>
        private static async void ShowWelcomeIfNoTabs()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow != null)
            {
                await App.ShowWelcomeScreen(desktop.MainWindow);
            }
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
        /// ПОЛНОСТЬЮ автоматическая загрузка из метаданных
        /// </summary>
        private void InitializeMenuItems()
        {
            var moduleRegistry = App.Services.GetRequiredService<ModuleRegistry>();
            var workModeRegistry = App.Services.GetRequiredService<Writersword.Src.WorkModes.Common.WorkModeRegistry>();

            // ===== АВТОМАТИЧЕСКАЯ ЗАГРУЗКА МОДУЛЕЙ =====
            var allModuleMetadata = moduleRegistry.GetAllModuleMetadata();

            foreach (var metadata in allModuleMetadata)
            {
                AllModules.Add(new ModuleMenuItem
                {
                    ModuleId = metadata.ModuleId,
                    Name = metadata.DisplayName,
                    Icon = metadata.Icon,
                    IsUniversal = metadata.IsUniversal,
                    IsEnabled = false,
                    IsChecked = false
                });
            }

            Console.WriteLine($"[InitializeMenuItems] Loaded {AllModules.Count} modules from metadata");

            // ===== АВТОМАТИЧЕСКАЯ ЗАГРУЗКА WORKMODES =====
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

            if (ActiveWorkMode == null) return;

            // Ищем WorkMode в доступных
            var workModeBar = WorkModeBar;
            var existingWorkMode = workModeBar.WorkModes.FirstOrDefault(wm => wm.WorkModeId == workModeId);

            if (existingWorkMode != null)
            {
                // WorkMode уже открыт - переключаемся на него
                Console.WriteLine($"[ToggleWorkMode] WorkMode exists, switching to it");
                workModeBar.SwitchWorkModeCommand.Execute(existingWorkMode).Subscribe();
            }
            else
            {
                // WorkMode не открыт - создаём новый
                Console.WriteLine($"[ToggleWorkMode] WorkMode not found, creating new");

                var project = TabBar.ActiveTab != null ? GetProjectForTab(TabBar.ActiveTab) : null;
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

            if (ActiveWorkMode == null)
            {
                Console.WriteLine($"[ToggleModule] ERROR: No active WorkMode!");
                return;
            }

            // Делегируем в ModulePanel
            ModulePanel.OpenModule(moduleId);

            // Если модуль уже открыт - фокусируем
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
                // Ищем документ с нужным ID
                var document = dock.VisibleDockables.FirstOrDefault(d => d.Id == documentId);
                if (document != null)
                {
                    Console.WriteLine($"[FocusDocumentRecursive] Found and focusing: {documentId}");
                    dock.ActiveDockable = document;
                    return true;
                }

                // Рекурсивно ищем в дочерних элементах
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
            if (ActiveWorkMode == null)
            {
                Console.WriteLine("[UpdateModuleMenuItems] No active WorkMode - all disabled");
                foreach (var menuItem in AllModules)
                {
                    menuItem.IsEnabled = false;
                    menuItem.IsChecked = false;
                }
                return;
            }

            Console.WriteLine($"[UpdateModuleMenuItems] Updating for WorkMode: {ActiveWorkMode.Title}");

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
        /// Полностью перезагрузить UI для текущего WorkMode
        /// ИСПОЛЬЗУЕТСЯ ТОЛЬКО при переключении WorkMode или создании вкладки!
        /// НЕ использовать для обновления после добавления/удаления модулей!
        /// </summary>
        private void RefreshWorkModeUI()
        {
            if (ActiveWorkMode == null)
            {
                Console.WriteLine("[RefreshWorkModeUI] No active WorkMode");
                return;
            }

            Console.WriteLine($"[RefreshWorkModeUI] FULL REFRESH for: {ActiveWorkMode.Title}");

            // ПОЛНАЯ перезагрузка панели модулей (сбрасывает все IsActive!)
            ModulePanel.LoadModulesForWorkMode(ActiveWorkMode);

            // Обновляем состояние меню
            UpdateWorkModeMenuItems();
            UpdateModuleMenuItems();
        }

        /// <summary>
        /// Обработчик закрытия модуля пользователем через крестик в Dock
        /// ВАЖНО: Вызывается из DockFactory когда Document.Owner становится null
        /// </summary>
        public void HandleModuleClosedInDock(string moduleId)
        {
            Console.WriteLine($"[MainWindowViewModel] Module closed in dock: {moduleId}");

            if (ActiveWorkMode == null) return;

            // 1. Уведомляем ModulePanel (снимает IsActive для модуля)
            ModulePanel.MarkModuleAsClosed(moduleId);

            // 2. Обновляем ТОЛЬКО меню (БЕЗ перезагрузки панели!)
            UpdateModuleMenuItems();

            Console.WriteLine($"[MainWindowViewModel] UI updated after dock close");
        }
    }
}