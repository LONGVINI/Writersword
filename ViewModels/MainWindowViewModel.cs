using Avalonia.Input;
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
using Writersword.Resources.Localization;
using Writersword.Core.Interfaces.Services.Input;
using Writersword.Core.Interfaces.Services.Storage;
using Writersword.Core.Interfaces.Services.UI;
using Writersword.Core.Interfaces.WorkFlows;
using Writersword.Infrastructure.Dock;
using Writersword.ViewModels.Components;
using Writersword.ViewModels.Components.MenuBar;
using Writersword.WorkModes.Common;

namespace Writersword.ViewModels
{
    /// <summary>
    /// ViewModel главного окна приложения
    /// Координирует компоненты UI и делегирует управление в WorkspaceController
    /// Не управляет DockLayout напрямую — только реагирует на событие WorkspaceChanged
    /// </summary>
    public class MainWindowViewModel : ViewModelBase
    {
        private readonly ILogger<MainWindowViewModel> _logger;

        public MenuBarViewModel MenuBar { get; }
        public TabBarViewModel TabBar { get; }
        public WorkModeBarViewModel WorkModeBar { get; }
        public ModulePanelViewModel ModulePanel { get; }

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

        private string _title = "Writersword";
        private IRootDock? _dockLayout;

        private string? _lastFocusedModuleType;

        public ObservableCollection<ModuleMenuItem> AllModules { get; } = new();
        public ObservableCollection<WorkModeMenuItem> AllWorkModes { get; } = new();

        public string Title
        {
            get => _title;
            set => this.RaiseAndSetIfChanged(ref _title, value);
        }

        public IRootDock? DockLayout
        {
            get => _dockLayout;
            set => this.RaiseAndSetIfChanged(ref _dockLayout, value);
        }

        public ReactiveCommand<Unit, Unit> NewProjectCommand { get; }
        public ReactiveCommand<Unit, Unit> OpenProjectCommand { get; }
        public ReactiveCommand<Unit, Unit> SaveProjectCommand { get; }
        public ReactiveCommand<Unit, Unit> SaveAsProjectCommand { get; }
        public ReactiveCommand<Unit, Unit> ExitCommand { get; }
        public ReactiveCommand<Unit, Unit> CreateNewTabCommand { get; }
        public ReactiveCommand<string, Unit> ToggleWorkModeCommand { get; }
        public ReactiveCommand<string, Unit> ToggleModuleCommand { get; }

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
                moduleType => FindModuleInstance(moduleType).module != null,
                moduleType => FocusModule(moduleType)
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
                if (newTab is DocumentTabViewModel tab)
                    _ = OnTabActivatedAsync(tab, previousTab as DocumentTabViewModel);

                MenuBar.UpdateHasActiveTab();
            };

            _settingsService.Load();
            RegisterHotKeys();
            InitializeDockFactory();
            InitializeMenuItems();

            _logger.LogDebug("MainWindowViewModel initialized");
        }

        /// <summary>
        /// Обработчик активации вкладки
        /// Сохраняет и деактивирует предыдущую, инициализирует и активирует новую
        /// DockLayout обновляется только здесь и в OnWorkspaceChanged
        /// </summary>
        public async Task OnTabActivatedAsync(DocumentTabViewModel tab, DocumentTabViewModel? previousTab)
        {
            try
            {
                _logger.LogDebug("Tab activated: {Title}, previous: {PreviousTitle}",
                    tab.Title, previousTab?.Title ?? "none");

                // Сохраняем ДО инициализации новой вкладки — но деактивируем ПОСЛЕ.
                // Если пользователь нажмёт Cancel в диалоге восстановления,
                // предыдущая вкладка останется живой и не нужно будет
                // пересоздавать все модули (что вешало UI на 78K слов JSON).
                if (previousTab != null && previousTab != tab && previousTab.Workspace != null)
                    await previousTab.Workspace.SaveWorkspaceAsync();

                if (!tab.IsLoaded)
                {
                    _logger.LogDebug("Initializing workspace for: {Title}", tab.Title);

                    bool success = await _projectWorkflow.EnsureWorkspaceInitialized(tab);

                    if (!success)
                    {
                        _logger.LogDebug("[Cancel] Step 1: cancel detected");
                        if (previousTab != null && _tabCollection.Tabs.Contains(previousTab))
                        {
                            _logger.LogDebug("[Cancel] Step 2: SilentRevertActiveTab");
                            _tabCollection.SilentRevertActiveTab(previousTab);
                            _logger.LogDebug("[Cancel] Step 3: SilentRevert done");

                            if (previousTab.Workspace != null)
                            {
                                _logger.LogDebug("[Cancel] Step 4: GetCurrentLayout");
                                DockLayout = previousTab.Workspace.GetCurrentLayout();
                                _logger.LogDebug("[Cancel] Step 5: LoadWorkModes");
                                WorkModeBar.LoadWorkModes(previousTab.Workspace.GetAvailableWorkModes());
                                _logger.LogDebug("[Cancel] Step 6: LoadModulesForWorkMode");
                                var wm = previousTab.Workspace.GetActiveWorkMode();
                                if (wm != null) ModulePanel.LoadModulesForWorkMode(wm);
                                _logger.LogDebug("[Cancel] Step 7: UpdateMenuItems");
                                UpdateWorkModeMenuItems();
                                UpdateModuleMenuItems();
                                _logger.LogDebug("[Cancel] Step 8: DONE");
                            }
                            else
                            {
                                _logger.LogWarning("[Cancel] previousTab.Workspace is NULL");
                            }
                        }
                        else
                        {
                            _logger.LogWarning("[Cancel] previousTab is null or not in tabs");
                        }
                        _logger.LogDebug("[Cancel] returning");
                        return;
                    }
                }

                // Инициализация прошла успешно — теперь деактивируем предыдущую вкладку.
                if (previousTab != null && previousTab != tab && previousTab.Workspace != null)
                {
                    _logger.LogDebug("Deactivating previous tab: {Title}", previousTab.Title);
                    previousTab.Workspace.Deactivate();
                    _ = System.Threading.Tasks.Task.Run(() =>
                    {
                        // blocking: true — ждём завершения каждого прохода.
                        // Выполняется в Task.Run, UI не блокируется.
                        // Порядок важен: сначала собираем объекты, затем ждём
                        // финализаторов (SKBitmap, SKFont, SKTypeface),
                        // затем собираем то что финализаторы освободили.
                        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                        GC.WaitForPendingFinalizers();
                        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                    });
                    _logger.LogDebug("Previous tab deactivated");
                }

                DockLayout = null;
                _logger.LogDebug("DockLayout cleared before activation");

                if (tab.Workspace == null)
                {
                    _logger.LogWarning("Workspace still null after initialization: {Title}", tab.Title);
                    return;
                }

                tab.EnsureWorkspaceActivated();

                DockLayout = tab.Workspace.GetCurrentLayout();
                _logger.LogDebug("DockLayout assigned for tab: {Title}", tab.Title);

                // При первом присвоении DockLayout Dock создаёт ContentPresenter-ы,
                // но VisualParent у view-шек может указывать на старые orphaned CP.
                // RecreateAllDocumentViews чинит VisualParent через GetOrCreateView()
                // и показывает loading indicator вместо чёрного экрана.
                var capturedTabForLoaded = tab;
                var capturedLayoutForLoaded = DockLayout;
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (DockLayout == capturedLayoutForLoaded
                        && DockLayout != null
                        && capturedTabForLoaded.Workspace != null)
                    {
                        _dockFactory.RecreateAllDocumentViews(
                            DockLayout, capturedTabForLoaded);
                    }
                }, Avalonia.Threading.DispatcherPriority.Loaded);

                WorkModeBar.LoadWorkModes(tab.Workspace.GetAvailableWorkModes());

                var activeWorkMode = tab.Workspace.GetActiveWorkMode();
                if (activeWorkMode != null)
                    ModulePanel.LoadModulesForWorkMode(activeWorkMode);

                UpdateWorkModeMenuItems();
                UpdateModuleMenuItems();

                tab.Workspace.WorkspaceChanged -= OnWorkspaceChanged;
                tab.Workspace.WorkspaceChanged += OnWorkspaceChanged;

                if (tab.Context.IsInCompareMode)
                {
                    _cacheUpdateService.Stop();
                    tab.Workspace.RefreshModulesFromContext();
                    _logger.LogDebug("Compare mode active — cache disabled, modules read-only");
                }
                else if (!string.IsNullOrEmpty(tab.FilePath))
                {
                    _cacheUpdateService.Stop();
                    _cacheUpdateService.Start(tab.FilePath, () => tab.Workspace?.GetActiveModules() ?? new List<IModule>());
                }

                _logger.LogDebug("Tab UI updated: {Title}", tab.Title);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error activating tab: {Title}", tab.Title);
            }
        }

        /// <summary>
        /// Обработчик изменений Workspace.
        /// DockLayout сбрасывается только если layout объект сменился или поднят флаг
        /// ConsumeNeedsFullLayoutRefresh (структурные изменения вроде CleanupEmptyContainers).
        /// Без флага — не трогаем вьюшки, нет мигания и потери контекста модулей.
        /// </summary>
        private void OnWorkspaceChanged(object? sender, EventArgs e)
        {
            _logger.LogDebug("Workspace changed, updating UI");

            var activeTab = TabBar.ActiveTab;
            if (activeTab?.Workspace == null) return;

            var newLayout = activeTab.Workspace.GetCurrentLayout();
            bool forceRefresh = activeTab.Workspace.ConsumeNeedsFullLayoutRefresh();

            if (forceRefresh || DockLayout != newLayout)
            {
                DockLayout = null;
                DockLayout = newLayout;
                _logger.LogDebug("DockLayout updated (forceRefresh={Force})", forceRefresh);

                // После null+set Dock создаёт новые ContentPresenter-ы.
                // Чиним VisualParent и показываем loading для всех модулей.
                var capturedLayoutWs = newLayout;
                var capturedTabWs = activeTab as DocumentTabViewModel;
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (DockLayout == capturedLayoutWs
                        && DockLayout != null
                        && capturedTabWs?.Workspace != null)
                    {
                        _dockFactory.RecreateAllDocumentViews(
                            DockLayout, capturedTabWs);
                    }
                }, Avalonia.Threading.DispatcherPriority.Loaded);
            }

            var activeWorkMode = activeTab.Workspace.GetActiveWorkMode();
            if (activeWorkMode != null)
            {
                ModulePanel.LoadModulesForWorkMode(activeWorkMode);
                WorkModeBar.LoadWorkModes(activeTab.Workspace.GetAvailableWorkModes());
            }

            UpdateWorkModeMenuItems();
            UpdateModuleMenuItems();
        }

        /// <summary>
        /// Обработчик переключения WorkMode
        /// Делегирует в WorkspaceController, DockLayout обновляется через WorkspaceChanged
        /// </summary>
        private async Task OnWorkModeSwitched(WorkMode newWorkMode)
        {
            _logger.LogDebug("WorkMode switch requested: {Title}", newWorkMode.Title);

            var activeTab = TabBar.ActiveTab;
            if (activeTab?.Workspace == null) return;

            activeTab.Workspace.SwitchWorkMode(newWorkMode);

            ModulePanel.LoadModulesForWorkMode(newWorkMode);
            UpdateWorkModeMenuItems();
            UpdateModuleMenuItems();

            await Task.CompletedTask;
        }

        /// <summary>
        /// Обработчик сохранения порядка WorkModes после drag-and-drop
        /// </summary>
        private void OnWorkModesReordered()
        {
            _logger.LogDebug("WorkModes reordered, saving workspace");

            var activeTab = TabBar.ActiveTab;
            if (activeTab?.Workspace == null) return;

            _ = activeTab.Workspace.SaveWorkspaceAsync();
        }

        /// <summary>
        /// Обработчик добавления модуля
        /// </summary>
        private void OnModuleAdded(string moduleType)
        {
            _logger.LogDebug("Module add requested: {moduleType}", moduleType);

            var activeTab = TabBar.ActiveTab;
            if (activeTab?.Workspace == null) return;

            activeTab.Workspace.AddModule(moduleType);

            var moduleItem = ModulePanel.AvailableModules.FirstOrDefault(m => m.moduleType == moduleType);
            if (moduleItem != null)
                moduleItem.IsActive = true;

            UpdateModuleMenuItems();
            FocusModule(moduleType);
        }

        /// <summary>
        /// Обработчик удаления модуля
        /// </summary>
        private void OnModuleRemoved(string moduleType)
        {
            _logger.LogDebug("Module remove requested: {moduleType}", moduleType);

            var activeTab = TabBar.ActiveTab;
            if (activeTab?.Workspace == null) return;

            activeTab.Workspace.RemoveModule(moduleType);

            var moduleItem = ModulePanel.AvailableModules.FirstOrDefault(m => m.moduleType == moduleType);
            if (moduleItem != null)
                moduleItem.IsActive = false;

            UpdateModuleMenuItems();
        }

        /// <summary>
        /// Обработчик открытия проекта
        /// </summary>
        private void OnProjectOpened(IDocumentTab tab)
        {
            if (tab is not DocumentTabViewModel vmTab) return;
            _logger.LogInformation("Project opened: {Title}", vmTab.Title);
        }

        /// <summary>
        /// Обработчик сохранения проекта
        /// </summary>
        private void OnProjectSaved(IDocumentTab tab)
        {
            if (tab is not DocumentTabViewModel vmTab) return;
            _logger.LogInformation("Project saved: {Title}", vmTab.Title);
        }

        /// <summary>
        /// Обработчик закрытия проекта
        /// SaveWorkspaceAsync вызывается асинхронно — без блокирующего .Wait() на UI потоке
        /// </summary>
        private void OnProjectClosed(IDocumentTab tab)
        {
            if (tab is not DocumentTabViewModel vmTab) return;
            _logger.LogDebug("Project closed: {Title}", vmTab.Title);

            if (!string.IsNullOrEmpty(vmTab.FilePath) && vmTab.Workspace != null)
            {
                _ = vmTab.Workspace.SaveWorkspaceAsync();
            }

            _cacheUpdateService.Stop();
        }

        /// <summary>
        /// Очистить UI когда не осталось вкладок
        /// </summary>
        public void ClearUIWhenNoTabs()
        {
            _logger.LogDebug("ClearUIWhenNoTabs called");

            _cacheUpdateService.Stop();

            DockLayout = null;
            WorkModeBar.LoadWorkModes(new List<WorkMode>());
            ModulePanel.Clear();

            _logger.LogDebug("UI cleared");
        }

        /// <summary>
        /// Инициализировать WorkModes для вкладки (обновляет только UI)
        /// </summary>
        public void InitializeWorkModesForTab(DocumentTabViewModel tab)
        {
            if (tab.Workspace == null)
            {
                _logger.LogWarning("Workspace not initialized for tab: {Title}", tab.Title);
                return;
            }

            AllWorkModes.Clear();

            _ = OnTabActivatedAsync(tab, null);
        }

        /// <summary>
        /// Получить список активных модулей текущей вкладки
        /// </summary>
        public List<IModule> GetActiveModules()
        {
            var activeTab = TabBar.ActiveTab;
            if (activeTab?.Workspace == null)
                return new List<IModule>();

            return activeTab.Workspace.GetActiveModules();
        }

        /// <summary>
        /// Получить сфокусированный модуль если он поддерживает Undo/Redo
        /// </summary>
        public IUndoableModule? GetFocusedUndoableModule()
        {
            if (_lastFocusedModuleType == null) return null;

            var activeTab = TabBar.ActiveTab;
            return activeTab?.ModuleContext.GetModule(_lastFocusedModuleType) as IUndoableModule;
        }


        /// <summary>
        /// Найти модуль по moduleType (dock + float)
        /// Использует ProjectModuleContext — не сканирует View иерархию
        /// </summary>
        private (IModule? module, bool isFloat) FindModuleInstance(string moduleType)
        {
            var activeTab = TabBar.ActiveTab;
            if (activeTab?.Workspace == null)
                return (null, false);

            var openIds = activeTab.Workspace.GetOpenModuleIds();
            if (!openIds.Contains(moduleType))
                return (null, false);

            var module = activeTab.ModuleContext.GetModule(moduleType);
            if (module == null)
                return (null, false);

            bool isFloat = IsModuleInFloatWindow(moduleType);

            return (module, isFloat);
        }

        private bool IsModuleInFloatWindow(string moduleType)
        {
            if (DockLayout?.Windows == null) return false;

            string documentId = $"Module_{moduleType}";

            foreach (var window in DockLayout.Windows)
            {
                if (window.Layout is IDock floatLayout && FindDocumentRecursive(floatLayout, documentId))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Получить проект для вкладки
        /// </summary>
        private ProjectFile? GetProjectForTab(DocumentTabViewModel tab)
        {
            if (string.IsNullOrEmpty(tab.FilePath)) return null;
            return _projectService.GetProjectByPath(tab.FilePath);
        }

        /// <summary>
        /// Инициализировать Dock фабрику
        /// </summary>
        private void InitializeDockFactory()
        {
            _dockFactory.Initialize();

            _dockFactory.OnModuleFocused = moduleType =>
            {
                _lastFocusedModuleType = moduleType;
                _logger.LogDebug("Last focused module: {moduleType}", moduleType);
            };

            _dockFactory.OnNeedRerender = () =>
            {
                var layout = TabBar.ActiveTab?.Workspace?.GetCurrentLayout();
                if (layout == null) return;
                var capturedTab = TabBar.ActiveTab as DocumentTabViewModel;
                var capturedLayout = layout;
                DockLayout = null;
                DockLayout = layout;
                _dockFactory.NormalizeAfterRerender(layout);
                _logger.LogDebug("DockLayout rerendered after module move");
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (DockLayout == capturedLayout && DockLayout != null && capturedTab?.Workspace != null)
                        _dockFactory.RecreateAllDocumentViews(DockLayout, capturedTab);
                }, Avalonia.Threading.DispatcherPriority.Loaded);
            };

            _logger.LogDebug("Dock factory initialized");
        }

        /// <summary>
        /// Регистрация глобальных горячих клавиш приложения
        /// Клавиши модулей регистрируются через RegisterModule при их инициализации
        /// </summary>
        private void RegisterHotKeys()
        {
            _hotKeyService.Register("HotKey_File_New", new HotKey
            {
                DisplayNameKey = Strings.HotKey_File_New,
                Category = HotKeyCategory.File,
                Scope = HotKeyScope.Global,
                DefaultGesture = new HotKeyGesture(new KeyGesture(Key.N, KeyModifiers.Control))
            }, NewProjectCommand);

            _hotKeyService.Register("HotKey_File_Open", new HotKey
            {
                DisplayNameKey = Strings.HotKey_File_Open,
                Category = HotKeyCategory.File,
                Scope = HotKeyScope.Global,
                DefaultGesture = new HotKeyGesture(new KeyGesture(Key.O, KeyModifiers.Control))
            }, OpenProjectCommand);

            _hotKeyService.Register("HotKey_File_Save", new HotKey
            {
                DisplayNameKey = Strings.HotKey_File_Save,
                Category = HotKeyCategory.File,
                Scope = HotKeyScope.Global,
                DefaultGesture = new HotKeyGesture(new KeyGesture(Key.S, KeyModifiers.Control))
            }, SaveProjectCommand);

            _hotKeyService.Register("HotKey_File_SaveAs", new HotKey
            {
                DisplayNameKey = Strings.HotKey_File_SaveAs,
                Category = HotKeyCategory.File,
                Scope = HotKeyScope.Global,
                DefaultGesture = new HotKeyGesture(new KeyGesture(Key.S, KeyModifiers.Control | KeyModifiers.Shift))
            }, SaveAsProjectCommand);

            _hotKeyService.Register("HotKey_File_SaveAll", new HotKey
            {
                DisplayNameKey = Strings.HotKey_File_SaveAll,
                Category = HotKeyCategory.File,
                Scope = HotKeyScope.Global,
                DefaultGesture = new HotKeyGesture(new KeyGesture(Key.S, KeyModifiers.Control | KeyModifiers.Alt))
            }, MenuBar.SaveAllProjectsCommand);

            _hotKeyService.Register("HotKey_File_CloseTab", new HotKey
            {
                DisplayNameKey = Strings.HotKey_File_CloseTab,
                Category = HotKeyCategory.File,
                Scope = HotKeyScope.Global,
                DefaultGesture = new HotKeyGesture(new KeyGesture(Key.W, KeyModifiers.Control))
            }, MenuBar.CloseTabCommand);

            _hotKeyService.Register("HotKey_File_CloseAllTabs", new HotKey
            {
                DisplayNameKey = Strings.HotKey_File_CloseAllTabs,
                Category = HotKeyCategory.File,
                Scope = HotKeyScope.Global,
                DefaultGesture = null
            }, MenuBar.CloseAllTabsCommand);

            _hotKeyService.Register("HotKey_File_CloseOtherTabs", new HotKey
            {
                DisplayNameKey = Strings.HotKey_File_CloseOtherTabs,
                Category = HotKeyCategory.File,
                Scope = HotKeyScope.Global,
                DefaultGesture = null
            }, MenuBar.CloseOtherTabsCommand);

            _hotKeyService.Register("HotKey_File_Settings", new HotKey
            {
                DisplayNameKey = Strings.HotKey_File_Settings,
                Category = HotKeyCategory.File,
                Scope = HotKeyScope.Global,
                DefaultGesture = new HotKeyGesture(new KeyGesture(Key.OemComma, KeyModifiers.Control))
            }, MenuBar.OpenSettingsCommand);

            _hotKeyService.Register("HotKey_File_NewTab", new HotKey
            {
                DisplayNameKey = Strings.HotKey_File_NewTab,
                Category = HotKeyCategory.Navigation,
                Scope = HotKeyScope.Global,
                DefaultGesture = new HotKeyGesture(new KeyGesture(Key.T, KeyModifiers.Control))
            }, CreateNewTabCommand);

            _hotKeyService.Register("HotKey_File_Exit", new HotKey
            {
                DisplayNameKey = Strings.HotKey_File_Exit,
                Category = HotKeyCategory.File,
                Scope = HotKeyScope.Global,
                DefaultGesture = null
            }, MenuBar.ExitCommand);

            _hotKeyService.Register("HotKey_View_Fullscreen", new HotKey
            {
                DisplayNameKey = Strings.HotKey_View_Fullscreen,
                Category = HotKeyCategory.View,
                Scope = HotKeyScope.Global,
                DefaultGesture = new HotKeyGesture(new KeyGesture(Key.F11))
            }, MenuBar.ToggleFullscreenCommand);

            _hotKeyService.LoadSettings();

            var moduleFactory = App.Services.GetRequiredService<ModuleFactory>();
            moduleFactory.RegisterAllHotKeys();

            _logger.LogDebug("Global hotkeys registered");
        }

        /// <summary>
        /// Загрузить проект при старте приложения
        /// </summary>
        public async void LoadProject(string filePath)
        {
            try
            {
                _logger.LogDebug("Loading project: {Path}", filePath);

                var existingTab = _tabCollection.FindByPath(filePath);
                if (existingTab != null)
                {
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading project: {Path}", filePath);
            }
        }

        /// <summary>
        /// Инициализировать элементы меню для модулей и WorkMode
        /// </summary>
        private void InitializeMenuItems()
        {
            var moduleFactory = App.Services.GetRequiredService<ModuleFactory>();
            var workModeRegistry = App.Services.GetRequiredService<WorkModeRegistry>();

            foreach (var metadata in moduleFactory.GetAllModuleMetadata())
            {
                AllModules.Add(new ModuleMenuItem
                {
                    ModuleType = metadata.ModuleType,
                    Name = metadata.DisplayName,
                    IsEnabled = false,
                    IsChecked = false
                });
            }

            foreach (var workMode in workModeRegistry.GetAll())
            {
                AllWorkModes.Add(new WorkModeMenuItem
                {
                    WorkModeId = workMode.Id,
                    Name = workMode.DisplayName,
                    Icon = workMode.Icon,
                    IsChecked = false
                });
            }

            _logger.LogDebug("Menu items initialized: {ModulesCount} modules, {WorkModesCount} WorkModes",
                AllModules.Count, AllWorkModes.Count);
        }

        /// <summary>
        /// Открыть/переключить WorkMode через меню
        /// </summary>
        private void ToggleWorkMode(string workModeId)
        {
            _logger.LogDebug("Toggling WorkMode: {WorkModeId}", workModeId);

            var activeTab = TabBar.ActiveTab;
            if (activeTab?.Workspace == null) return;

            var existingWorkMode = WorkModeBar.WorkModes.FirstOrDefault(wm => wm.WorkModeId == workModeId);

            if (existingWorkMode != null)
            {
                WorkModeBar.SwitchWorkModeCommand.Execute(existingWorkMode).Subscribe();
            }
            else
            {
                var project = GetProjectForTab(activeTab);
                if (project == null) return;

                var workModeRegistry = App.Services.GetRequiredService<WorkModeRegistry>();
                var workModeInstance = workModeRegistry.GetWorkMode(workModeId);

                if (workModeInstance == null)
                {
                    _logger.LogWarning("WorkMode not found in registry: {WorkModeId}", workModeId);
                    return;
                }

                var workModeService = activeTab.Workspace.GetWorkModeService();
                if (workModeService == null) return;

                var newWorkMode = workModeService.AddWorkMode(
                    workModeId,
                    workModeInstance.DisplayName,
                    workModeInstance.Icon
                );

                newWorkMode.IsCloseable = workModeInstance.IsCloseable;
                newWorkMode.Order = workModeInstance.Order;

                WorkModeBar.LoadWorkModes(workModeService.GetAllWorkModes());
                WorkModeBar.SwitchWorkModeCommand.Execute(newWorkMode).Subscribe();

                _logger.LogInformation("Created and switched to WorkMode: {Title}", newWorkMode.Title);
            }

            UpdateWorkModeMenuItems();
        }

        /// <summary>
        /// Открыть модуль через меню
        /// Если уже открыт — фокусирует, если нет — создаёт
        /// </summary>
        private void ToggleModule(string moduleType)
        {
            _logger.LogDebug("Toggle module: {moduleType}", moduleType);

            var activeTab = TabBar.ActiveTab;
            if (activeTab?.Workspace == null) return;

            var (existingModule, _) = FindModuleInstance(moduleType);

            if (existingModule != null)
            {
                FocusModule(moduleType);
                return;
            }

            ModulePanel.OpenModule(moduleType);
            FocusModule(moduleType);
            UpdateModuleMenuItems();
        }

        /// <summary>
        /// Найти и активировать вкладку модуля в UI (dock + float)
        /// </summary>
        private void FocusModule(string moduleType)
        {
            if (DockLayout == null) return;

            string documentId = $"Module_{moduleType}";

            if (FocusDocumentInFloatWindow(DockLayout, documentId))
            {
                _logger.LogDebug("Module focused in float window: {moduleType}", moduleType);
                return;
            }

            if (FocusDocumentRecursive(DockLayout, documentId))
            {
                _logger.LogDebug("Module focused in dock: {moduleType}", moduleType);
                return;
            }

            _logger.LogWarning("Module not found in UI: {moduleType}", moduleType);
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
                if (window.Layout is IDock floatLayout && FocusDocumentRecursive(floatLayout, documentId))
                {
                    if (window.Host is HostWindow hostWindow)
                    {
                        try
                        {
                            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                            {
                                hostWindow.GetWindow()?.Activate();
                            }
                            else
                            {
                                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                                    hostWindow.GetWindow()?.Activate());
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

            return false;
        }

        /// <summary>
        /// Рекурсивно найти Document и установить его как ActiveDockable
        /// </summary>
        private bool FocusDocumentRecursive(IDockable dockable, string documentId)
        {
            if (dockable is IDock dock && dock.VisibleDockables != null)
            {
                var document = dock.VisibleDockables.FirstOrDefault(d => d.Id == documentId);
                if (document != null)
                {
                    dock.ActiveDockable = document;
                    return true;
                }

                foreach (var child in dock.VisibleDockables)
                {
                    if (FocusDocumentRecursive(child, documentId))
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Рекурсивно найти Document (без фокусировки)
        /// </summary>
        private bool FindDocumentRecursive(IDockable dockable, string documentId)
        {
            if (dockable is IDock dock && dock.VisibleDockables != null)
            {
                if (dock.VisibleDockables.Any(d => d.Id == documentId))
                    return true;

                foreach (var child in dock.VisibleDockables)
                {
                    if (FindDocumentRecursive(child, documentId))
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Обновить состояние элементов меню WorkMode
        /// </summary>
        public void UpdateWorkModeMenuItems()
        {
            var workModes = WorkModeBar.WorkModes;

            foreach (var menuItem in AllWorkModes)
                menuItem.IsChecked = workModes.Any(wm => wm.WorkModeId == menuItem.WorkModeId);

            var sorted = AllWorkModes
                .OrderBy(wm => workModes.FirstOrDefault(w => w.WorkModeId == wm.WorkModeId)?.Order ?? int.MaxValue)
                .ToList();

            AllWorkModes.Clear();
            foreach (var item in sorted)
                AllWorkModes.Add(item);
        }

        /// <summary>
        /// Обновить состояние элементов меню модулей
        /// </summary>
        public void UpdateModuleMenuItems()
        {
            var activeTab = TabBar.ActiveTab;
            if (activeTab?.Workspace == null)
            {
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
                foreach (var menuItem in AllModules)
                {
                    menuItem.IsEnabled = false;
                    menuItem.IsChecked = false;
                }
                return;
            }

            ModulePanel.RefreshModuleStates();
            UpdateModuleMenuItemsInternal();
        }

        /// <summary>
        /// Внутренний метод обновления меню модулей
        /// </summary>
        private void UpdateModuleMenuItemsInternal()
        {
            var activeTab = TabBar.ActiveTab;
            if (activeTab?.Workspace == null) return;

            var activeWorkMode = activeTab.Workspace.GetActiveWorkMode();
            if (activeWorkMode == null) return;

            var openModuleIds = activeTab.Workspace.GetOpenModuleIds();

            foreach (var menuItem in AllModules)
            {
                ModuleCategory category = activeWorkMode.ModuleCategories.TryGetValue(menuItem.ModuleType, out var explicitCategory)
                    ? explicitCategory
                    : ModuleCategory.Optional;

                var slot = activeWorkMode.ModuleSlots.FirstOrDefault(s => s.ModuleType == menuItem.ModuleType);

                switch (category)
                {
                    case ModuleCategory.Required:
                        menuItem.IsEnabled = true;
                        menuItem.IsChecked = true;
                        break;

                    case ModuleCategory.Optional:
                    case ModuleCategory.Unwanted:
                        menuItem.IsEnabled = true;
                        menuItem.IsChecked = slot != null && openModuleIds.Contains(menuItem.ModuleType);
                        break;

                    case ModuleCategory.Forbidden:
                    default:
                        menuItem.IsEnabled = false;
                        menuItem.IsChecked = false;
                        break;
                }
            }
        }

        /// <summary>
        /// Элемент меню для модуля
        /// </summary>
        public class ModuleMenuItem : ReactiveObject
        {
            private bool _isEnabled;
            private bool _isChecked;

            public string ModuleType { get; set; } = "";
            public string Name { get; set; } = "";

            public bool IsEnabled
            {
                get => _isEnabled;
                set => this.RaiseAndSetIfChanged(ref _isEnabled, value);
            }

            public bool IsChecked
            {
                get => _isChecked;
                set => this.RaiseAndSetIfChanged(ref _isChecked, value);
            }
        }

        /// <summary>
        /// Элемент меню для WorkMode
        /// </summary>
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
    }
}