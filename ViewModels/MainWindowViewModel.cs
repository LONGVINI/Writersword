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
using System.Reactive.Linq;
using System.Threading.Tasks;
using Writersword.Core.Enums;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Models.Project;
using Writersword.Core.Models.Settings;
using Writersword.Core.Models.WorkModes;
using Writersword.Modules.Common;
using Writersword.Modules.TextEditor.ViewModels;
using Writersword.Services;
using Writersword.Services.Interfaces;
using Writersword.Src.Core.Interfaces.WorkModes;
using Writersword.Src.Infrastructure.Dock;
using Writersword.Resources.Localization;

namespace Writersword.ViewModels
{
    /// <summary>
    /// ViewModel главного окна приложения
    /// Координирует UI, WorkMode и модули
    /// Работа с файлами делегируется в IProjectWorkflow
    /// Управление вкладками делегируется в ITabCollection
    /// </summary>
    public class MainWindowViewModel : ViewModelBase
    {
        // === НОВЫЕ СЕРВИСЫ ===
        /// <summary>Сервис управления жизненным циклом проектов</summary>
        private readonly IProjectWorkflow _projectWorkflow;

        /// <summary>Сервис управления коллекцией вкладок</summary>
        private readonly ITabCollection _tabCollection;

        // === СУЩЕСТВУЮЩИЕ СЕРВИСЫ ===
        private readonly IDialogService _dialogService;
        private readonly ISettingsService _settingsService;
        private readonly IProjectService _projectService;
        private readonly IHotKeyService _hotKeyService;
        private readonly IWorkModeConfigurationService _workModeConfigService;
        private readonly IWorkModeService _workModeService;
        private readonly DockFactory _dockFactory;

        // === СОСТОЯНИЕ ===
        private readonly Dictionary<string, IRootDock> _tabLayouts = new();
        private readonly ModuleRegistry _moduleRegistry; // Реестр модулей
        private List<IModuleMetadata>? _cachedModuleMetadata; // Кэш всех метаданных модулей
        private readonly List<IDisposable> _slotSubscriptions = new(); // Подписки на изменения слотов

        private WorkMode? _activeWorkMode;
        private string _title = "Writersword";
        private object? _currentModule;
        private IRootDock? _dockLayout;

        private RecoveryBannerViewModel? _recoveryBanner;
        private readonly ICacheService _cacheService;

        /// <summary>Заголовок окна</summary>
        public string Title
        {
            get => _title;
            set => this.RaiseAndSetIfChanged(ref _title, value);
        }

        /// <summary>Текущий активный модуль (View)</summary>
        public object? CurrentModule
        {
            get => _currentModule;
            set => this.RaiseAndSetIfChanged(ref _currentModule, value);
        }

        /// <summary>Layout для Dock системы</summary>
        public IRootDock? DockLayout
        {
            get => _dockLayout;
            set => this.RaiseAndSetIfChanged(ref _dockLayout, value);
        }

        /// <summary>
        /// Открытые вкладки документов
        /// Делегируем управление в TabCollection
        /// </summary>
        public ObservableCollection<DocumentTabViewModel> OpenTabs
            => _tabCollection.Tabs;

        /// <summary>
        /// Активная вкладка
        /// Делегируем управление в TabCollection
        /// </summary>
        public DocumentTabViewModel? ActiveTab
        {
            get => _tabCollection.ActiveTab;
            set => _tabCollection.ActiveTab = value;
        }

        
/// <summary>ViewModel баннера восстановления (null если баннер скрыт)</summary>
public RecoveryBannerViewModel? RecoveryBanner
{
    get => _recoveryBanner;
    set => this.RaiseAndSetIfChanged(ref _recoveryBanner, value);
}

        /// <summary>Список всех доступных типов модулей с их метаданными</summary>
        public ObservableCollection<ModuleMenuItem> AllModules { get; } = new();

        /// <summary>Список всех доступных WorkMode типов с их метаданными</summary>
        public ObservableCollection<WorkModeMenuItem> AllWorkModes { get; } = new();

        /// <summary>Активный режим работы</summary>
        public WorkMode? ActiveWorkMode
        {
            get => _activeWorkMode;
            set => this.RaiseAndSetIfChanged(ref _activeWorkMode, value);
        }

        /// <summary>Доступные режимы работы для текущей вкладки</summary>
        public ObservableCollection<WorkMode> AvailableWorkModes { get; } = new();

        // === КОМАНДЫ ===
        public ReactiveCommand<Unit, Unit> NewProjectCommand { get; }
        public ReactiveCommand<Unit, Unit> OpenProjectCommand { get; }
        public ReactiveCommand<Unit, Unit> SaveProjectCommand { get; }
        public ReactiveCommand<Unit, Unit> SaveAsProjectCommand { get; }
        public ReactiveCommand<Unit, Unit> ExitCommand { get; }
        public ReactiveCommand<Unit, Unit> CreateNewTabCommand { get; }
        public ReactiveCommand<WorkMode, Unit> SwitchWorkModeCommand { get; }
        public ReactiveCommand<Unit, Unit> SaveWorkspaceForProjectCommand { get; }
        public ReactiveCommand<Unit, Unit> SaveWorkspaceGloballyCommand { get; }
        public ReactiveCommand<Unit, Unit> LoadDefaultWorkspaceCommand { get; }
        public ReactiveCommand<string, Unit> ToggleWorkModeCommand { get; }
        public ReactiveCommand<ModuleType, Unit> ToggleModuleCommand { get; }

        public MainWindowViewModel(
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
            _cachedModuleMetadata = _moduleRegistry.GetAllModuleMetadata().ToList();

            // Создаём команды
            NewProjectCommand = ReactiveCommand.Create(NewProject);
            OpenProjectCommand = ReactiveCommand.CreateFromTask(OpenProject);
            SaveProjectCommand = ReactiveCommand.CreateFromTask(SaveProject);
            SaveAsProjectCommand = ReactiveCommand.CreateFromTask(SaveAsProject);
            ExitCommand = ReactiveCommand.Create(Exit);
            CreateNewTabCommand = ReactiveCommand.Create(CreateNewTab);

            // Команды для работы WorkMode
            SwitchWorkModeCommand = ReactiveCommand.Create<WorkMode>(SwitchWorkMode);
            SaveWorkspaceForProjectCommand = ReactiveCommand.CreateFromTask(SaveWorkspaceForProject);
            SaveWorkspaceGloballyCommand = ReactiveCommand.CreateFromTask(SaveWorkspaceGlobally);
            LoadDefaultWorkspaceCommand = ReactiveCommand.CreateFromTask(LoadDefaultWorkspace);

            // Команды для переключения модулей и режимов
            ToggleWorkModeCommand = ReactiveCommand.Create<string>(ToggleWorkMode);
            ToggleModuleCommand = ReactiveCommand.Create<ModuleType>(ToggleModule);

            // Подписываемся на события сервисов
            _projectWorkflow.ProjectOpened += OnProjectOpened;
            _projectWorkflow.ProjectSaved += OnProjectSaved;
            _projectWorkflow.ProjectClosed += OnProjectClosed;
            _tabCollection.ActiveTabChanged += OnActiveTabChanged;

            _settingsService.Load();

            RegisterHotKeys(); // Регистрация горячих клавиш
            InitializeDockFactory(); // Инициализация Dock фабрики
            InitializeMenuItems(); // Инициализация AllModules и AllWorkModes
            UpdateWorkModeMenuItems();
            UpdateModuleMenuItems();
        }

        // ========================================
        // ОБРАБОТЧИКИ СОБЫТИЙ СЕРВИСОВ
        // ========================================

        /// <summary>Обработчик открытия проекта</summary>
        private void OnProjectOpened(DocumentTabViewModel tab)
        {
            Console.WriteLine($"[MainWindowViewModel] Project opened: {tab.Title}");

            // Проверяем нужен ли баннер
            var filePath = tab.FilePath;
            if (!string.IsNullOrEmpty(filePath) && _cacheService.HasCache(filePath))
            {
                var cacheDate = _cacheService.GetCacheDate(filePath);
                var saveDate = _cacheService.GetSaveDate(filePath);

                if (cacheDate.HasValue && saveDate.HasValue)
                {
                    ShowRecoveryBanner(cacheDate.Value, saveDate.Value);
                }
            }

            InitializeWorkModesForTab(tab);
            ShowTextEditor();
        }

        private void ShowRecoveryBanner(DateTime cacheDate, DateTime saveDate)
        {
            RecoveryBanner = new RecoveryBannerViewModel
            {
                IsViewingCache = false,
                CacheDate = cacheDate,
                SaveDate = saveDate
            };

            RecoveryBanner.SwitchVersionCommand = ReactiveCommand.CreateFromTask(SwitchRecoveryVersion);
            RecoveryBanner.SaveCommand = ReactiveCommand.CreateFromTask(SaveCacheAsMain);
            RecoveryBanner.DiscardCommand = ReactiveCommand.CreateFromTask(DiscardCache);
        }

        private void HideRecoveryBanner()
        {
            RecoveryBanner = null;
        }

        private async Task SwitchRecoveryVersion()
        {
            // TODO: Реализовать переключение
        }

        private async Task SaveCacheAsMain()
        {
            if (ActiveTab != null)
            {
                await _projectWorkflow.SaveDocumentAsync(ActiveTab);
                HideRecoveryBanner();
            }
        }

        private async Task DiscardCache()
        {
            if (ActiveTab == null) return;

            var filePath = ActiveTab.FilePath;
            if (string.IsNullOrEmpty(filePath)) return;

            var result = await _dialogService.ShowMessageAsync(
                "Удалить автосохранение?",
                "Автосохранённая версия будет удалена. Продолжить?",
                Views.MessageBoxType.Warning,
                Views.MessageBoxButtons.YesNo
            );

            if (result == Views.MessageBoxResult.Yes)
            {
                _cacheService.DeleteCache(filePath);
                HideRecoveryBanner();
            }
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

            // Если закрыли последнюю вкладку - показываем Welcome
            if (OpenTabs.Count == 0)
            {
                ActiveWorkMode = null;
                CurrentModule = null;
                ShowWelcomeIfNoTabs();
            }
        }

        /// <summary>Обработчик изменения активной вкладки</summary>
        private void OnActiveTabChanged(DocumentTabViewModel? tab)
        {
            Console.WriteLine($"[MainWindowViewModel] Active tab changed: {tab?.Title ?? "none"}");
            if (tab != null)
            {
                // Уведомляем UI об изменении
                this.RaisePropertyChanged(nameof(ActiveTab));
                ActivateTab(tab);
            }
            else
            {
                // Если нет активных вкладок → показываем Welcome
                Console.WriteLine("[MainWindowViewModel] No active tabs, showing welcome");
                var mainWindow = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                if (mainWindow != null)
                {
                    _ = App.ShowWelcomeScreen(mainWindow);
                }
            }
        }

        // ========================================
        // КОМАНДЫ РАБОТЫ С ПРОЕКТАМИ
        // (делегируют работу в ProjectWorkflow)
        // ========================================

        /// <summary>Создать новый проект (показывает Welcome окно)</summary>
        private async void NewProject()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                await App.ShowWelcomeScreen(desktop.MainWindow!);
            }
        }

        /// <summary>Открыть существующий проект</summary>
        private async Task OpenProject()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                await App.ShowWelcomeScreen(desktop.MainWindow!);
            }
        }

        /// <summary>Сохранить активный проект</summary>
        private async Task SaveProject()
        {
            if (ActiveTab == null)
            {
                Console.WriteLine("[SaveProject] No active tab");
                return;
            }

            await _projectWorkflow.SaveDocumentAsync(ActiveTab);
        }

        /// <summary>Сохранить активный проект как...</summary>
        private async Task SaveAsProject()
        {
            if (ActiveTab == null)
            {
                Console.WriteLine("[SaveAsProject] No active tab");
                return;
            }

            await _projectWorkflow.SaveAsDocumentAsync(ActiveTab);
        }

        /// <summary>Выход из приложения</summary>
        private void Exit()
        {
            System.Environment.Exit(0);
        }

        /// <summary>Создать новую вкладку - открывает Welcome окно</summary>
        private async void CreateNewTab()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow != null)
            {
                await App.ShowWelcomeScreen(desktop.MainWindow);
            }
        }

        // ========================================
        // УПРАВЛЕНИЕ ВКЛАДКАМИ
        // ========================================

        /// <summary>
        /// Загрузить проект при старте приложения
        /// Вызывается из App.axaml.cs при восстановлении сессии
        /// </summary>
        public async void LoadProject(string filePath)
        {
            Console.WriteLine($"[LoadProject] Loading: {filePath}");

            // Проверяем не открыт ли уже этот проект
            var existingTab = _tabCollection.FindByPath(filePath);
            if (existingTab != null)
            {
                Console.WriteLine($"[LoadProject] Project already open, activating tab");
                _tabCollection.ActiveTab = existingTab;
                return;
            }

            // Открываем через workflow
            var tab = await _projectWorkflow.OpenDocumentAsync(filePath);
            if (tab != null)
            {
                _tabCollection.Add(tab);
                _tabCollection.ActiveTab = tab;
                _settingsService.AddRecentProject(filePath);
            }
        }

        /// <summary>Добавить новую вкладку в приложение</summary>
        public void AddNewTab(string title, string content, string? filePath)
        {
            // ПРОВЕРКА: Если вкладка с таким FilePath уже существует - активируем её
            if (!string.IsNullOrEmpty(filePath))
            {
                var existingTab = _tabCollection.FindByPath(filePath);
                if (existingTab != null)
                {
                    _tabCollection.ActiveTab = existingTab;
                    return;
                }
            }

            // Деактивируем все вкладки
            foreach (var tab in OpenTabs)
            {
                tab.IsActive = false;
            }

            // Создаём новый документ
            var newProject = _projectService.CreateNew(title, "Novel");
            var tabVM = new DocumentTabViewModel(newProject, filePath ?? "", CloseTabAsync);
            tabVM.Content = content;

            _tabCollection.Add(tabVM);
            _tabCollection.ActiveTab = tabVM;

            InitializeWorkModesForTab(tabVM);
            ShowTextEditor();
        }

        /// <summary>Активировать вкладку</summary>
        public void ActivateTab(DocumentTabViewModel tab)
        {
            // Деактивируем все вкладки
            foreach (var t in OpenTabs)
            {
                t.IsActive = false;
            }
            tab.IsActive = true;
            _tabCollection.ActiveTab = tab;

            // Восстанавливаем layout для вкладки
            var project = GetProjectForTab(tab);
            if (project != null)
            {
                string tabKey = tab.FilePath ?? tab.Id;

                // Если layout уже создан для этой вкладки - переиспользуем его
                if (_tabLayouts.TryGetValue(tabKey, out var existingLayout))
                {
                    Console.WriteLine($"[ActivateTab] Reusing existing layout for tab: {tab.Title}");
                    DockLayout = existingLayout;
                    return;  // ← ДОБАВЬ RETURN! НЕ СОЗДАВАЙ WORKMODES СНОВА!
                }

                Console.WriteLine($"[ActivateTab] Creating new layout for tab: {tab.Title}");
                InitializeWorkModesForTab(tab);
                if (DockLayout != null)
                {
                    _tabLayouts[tabKey] = DockLayout;
                }
            }
        }

        /// <summary>
        /// Закрыть вкладку
        /// Вызывается из CloseCommand вкладки
        /// </summary>
        public async Task CloseTabAsync(DocumentTabViewModel tab)
        {
            Console.WriteLine($"[CloseTab] Closing tab: {tab.Title}");

            if (await _projectWorkflow.CloseDocumentAsync(tab))
            {
                // Удаляем layout
                string tabKey = tab.FilePath ?? tab.Id;
                _tabLayouts.Remove(tabKey);  

                _tabCollection.Remove(tab);
            }
        }

        /// <summary>Получить проект для вкладки</summary>
        private ProjectFile? GetProjectForTab(DocumentTabViewModel tab)
        {
            var filePath = tab.FilePath;
            if (string.IsNullOrEmpty(filePath)) return null;

            var project = _projectService.GetProjectByPath(filePath);

            if (project != null)
            {
                Console.WriteLine($"[GetProjectForTab] Found project: {project.Title}");
            }
            else
            {
                Console.WriteLine($"[GetProjectForTab] Project NOT found for path: {filePath}");
            }

            return project;
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

        // ========================================
        // ТЕКСТОВЫЙ РЕДАКТОР
        // ========================================

        /// <summary>Показать модуль текстового редактора</summary>
        public void ShowTextEditor()
        {
            if (ActiveTab == null)
            {
                Console.WriteLine("ShowTextEditor: ActiveTab is null!");
                return;
            }

            Console.WriteLine($"ShowTextEditor called for tab: {ActiveTab.Title}");

            var viewModel = new TextEditorViewModel();
            viewModel.LoadDocument(ActiveTab.Content);

            // Подписываемся на изменения текста
            viewModel.WhenAnyValue(x => x.PlainText)
                .Throttle(TimeSpan.FromSeconds(2))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(async text =>
                {
                    if (ActiveTab != null)
                    {
                        ActiveTab.Content = text;

                        // Автосохранение проекта активной вкладки
                        var project = GetProjectForTab(ActiveTab);
                        if (project != null)
                        {
                            var filePath = _projectService.GetProjectPath(project);
                            if (filePath != null)
                            {
                                await _projectService.SaveAsync(project, filePath);
                            }
                        }
                    }
                });

            var view = new Modules.TextEditor.Views.TextEditorView
            {
                DataContext = viewModel
            };

            CurrentModule = view;
            Console.WriteLine($"CurrentModule set to TextEditorView");
        }

        // ========================================
        // ГОРЯЧИЕ КЛАВИШИ
        // ========================================

        /// <summary>Регистрация горячих клавиш</summary>
        private void RegisterHotKeys()
        {
            // Ctrl+N - Новый проект
            _hotKeyService.Register(
                "file.new",
                new HotKey
                {
                    DisplayNameKey = "HotKey_File_New",
                    DefaultGesture = new KeyGesture(Key.N, KeyModifiers.Control)
                },
                NewProjectCommand
            );

            // Ctrl+O - Открыть проект
            _hotKeyService.Register(
                "file.open",
                new HotKey
                {
                    DisplayNameKey = "HotKey_File_Open",
                    DefaultGesture = new KeyGesture(Key.O, KeyModifiers.Control)
                },
                OpenProjectCommand
            );

            // Ctrl+S - Сохранить
            _hotKeyService.Register(
                "file.save",
                new HotKey
                {
                    DisplayNameKey = "HotKey_File_Save",
                    DefaultGesture = new KeyGesture(Key.S, KeyModifiers.Control)
                },
                SaveProjectCommand
            );

            // Ctrl+Shift+S - Сохранить как
            _hotKeyService.Register(
                "file.saveas",
                new HotKey
                {
                    DisplayNameKey = "HotKey_File_SaveAs",
                    DefaultGesture = new KeyGesture(Key.S, KeyModifiers.Control | KeyModifiers.Shift)
                },
                SaveAsProjectCommand
            );

            // Ctrl+W - Закрыть вкладку
            _hotKeyService.Register(
                "file.closetab",
                new HotKey
                {
                    DisplayNameKey = "HotKey_File_CloseTab",
                    DefaultGesture = new KeyGesture(Key.W, KeyModifiers.Control)
                },
                ReactiveCommand.CreateFromTask(async () =>
                {
                    if (ActiveTab != null)
                        await CloseTabAsync(ActiveTab);
                })
            );

            // Ctrl+T - Новая вкладка
            _hotKeyService.Register(
                "file.newtab",
                new HotKey
                {
                    DisplayNameKey = "HotKey_File_NewTab",
                    DefaultGesture = new KeyGesture(Key.T, KeyModifiers.Control)
                },
                CreateNewTabCommand
            );
        }

        // ========================================
        // WORKMODE УПРАВЛЕНИЕ
        // ========================================

        /// <summary>Переключить режим работы</summary>
        private void SwitchWorkMode(WorkMode workMode)
        {
            if (ActiveTab == null) return;

            _workModeService.SetActiveWorkMode(workMode);
            ActiveWorkMode = workMode;

            // Обновляем IsActive для всех WorkModes
            foreach (var wm in AvailableWorkModes)
            {
                wm.IsActive = (wm.Id == workMode.Id);
            }

            Console.WriteLine($"[MainWindowViewModel] Switched to WorkMode: {workMode.Title}");
            Console.WriteLine($"[MainWindowViewModel] Modules in this mode: {workMode.ModuleSlots.Count}");

            ShowModulesForWorkMode(workMode);

            UpdateWorkModeMenuItems();
            UpdateModuleMenuItems();
        }

        /// <summary>Показать модули для выбранного WorkMode</summary>
        private void ShowModulesForWorkMode(WorkMode workMode)
        {
            Console.WriteLine($"[ShowModulesForWorkMode] ===== LOADING MODULES FOR: {workMode.Title} =====");
            Console.WriteLine($"[ShowModulesForWorkMode] Total slots: {workMode.ModuleSlots.Count}");

            foreach (var slot in workMode.ModuleSlots)
            {
                Console.WriteLine($"  Slot: {slot.ModuleType}, IsVisible={slot.IsVisible}");
            }

            var layout = _dockFactory.CreateLayout(workMode);
            DockLayout = layout;

            Console.WriteLine($"[ShowModulesForWorkMode] DockLayout created");

            // ОТПИСЫВАЕМСЯ ОТ СТАРЫХ ПОДПИСОК!
            foreach (var subscription in _slotSubscriptions)
            {
                subscription.Dispose();
            }
            _slotSubscriptions.Clear();
            Console.WriteLine($"[ShowModulesForWorkMode] Cleared {_slotSubscriptions.Count} old subscriptions");

            // Создаём НОВЫЕ подписки
            foreach (var slot in workMode.ModuleSlots)
            {
                var subscription = slot.WhenAnyValue(x => x.IsVisible)
                    .Subscribe(_ =>
                    {
                        Console.WriteLine($"[ShowModulesForWorkMode] Slot.IsVisible changed: {slot.ModuleType} = {slot.IsVisible}");
                        UpdateModuleMenuItems();
                    });

                _slotSubscriptions.Add(subscription);
            }

            Console.WriteLine($"[ShowModulesForWorkMode] Subscribed to {workMode.ModuleSlots.Count} slot changes");
        }

        /// <summary>Найти DocumentDock в структуре</summary>
        private static DocumentDock? FindDocumentDock(IDockable? root)
        {
            if (root is DocumentDock docDock)
                return docDock;

            if (root is IDock dock && dock.VisibleDockables != null)
            {
                foreach (var child in dock.VisibleDockables)
                {
                    var found = FindDocumentDock(child);
                    if (found != null) return found;
                }
            }

            return null;
        }

        /// <summary>Сохранить настройки для этого проекта</summary>
        private async Task SaveWorkspaceForProject()
        {
            if (ActiveTab == null) return;

            var result = await _dialogService.ShowMessageAsync(
                "Сохранить настройки проекта?",
                "ВНИМАНИЕ! Если для этого проекта уже были сохранены настройки окон - они будут УДАЛЕНЫ и заменены текущими.\n\nВы уверены?",
                Views.MessageBoxType.Warning,
                Views.MessageBoxButtons.YesNo
            );

            if (result == Views.MessageBoxResult.Yes)
            {
                // Получаем проект
                var project = GetProjectForTab(ActiveTab);
                if (project != null)
                {
                    // Сохраняем WorkModes в UserConfig проекта
                    if (project.UserConfig == null)
                    {
                        project.UserConfig = new UserConfiguration();
                    }

                    project.UserConfig.WorkModes = _workModeService.GetAllWorkModes()
                        .Select(wm => new UserWorkModeConfig
                        {
                            Id = wm.WorkModeId,
                            Title = wm.Title,
                            IsActive = wm.IsActive,
                            ModuleSlots = wm.ModuleSlots.Select(ms => new UserModuleSlotConfig
                            {
                                ModuleType = ms.ModuleType.ToString(),
                                IsVisible = ms.IsVisible,
                                Position = ms.PreferredPosition.ToString()
                            }).ToList()
                        }).ToList();

                    // Сохраняем проект
                    var filePath = _projectService.GetProjectPath(project);
                    if (filePath != null)
                    {
                        await _projectService.SaveAsync(project, filePath);
                        Console.WriteLine("[MainWindowViewModel] Workspace saved to PROJECT");
                    }
                }
            }
        }

        /// <summary>Сохранить настройки для всех проектов этого типа</summary>
        private async Task SaveWorkspaceGlobally()
        {
            if (ActiveTab == null) return;

            var project = GetProjectForTab(ActiveTab);
            if (project == null) return;

            var result = await _dialogService.ShowMessageAsync(
                "Сохранить глобальные настройки?",
                $"Эти настройки будут применяться для всех НОВЫХ проектов типа '{project.Type}'.\n\nВы всегда сможете вернуться к дефолтным настройкам или настроить каждый проект отдельно.\n\nСохранить?",
                Views.MessageBoxType.Question,
                Views.MessageBoxButtons.YesNo
            );

            if (result == Views.MessageBoxResult.Yes)
            {
                var workModes = _workModeService.GetAllWorkModes();
                _workModeConfigService.SaveGlobalConfiguration(project.Type, workModes);

                await _dialogService.ShowMessageAsync(
                    "Успешно",
                    "Глобальные настройки сохранены!",
                    Views.MessageBoxType.Info,
                    Views.MessageBoxButtons.OK
                );

                Console.WriteLine("[MainWindowViewModel] Workspace saved GLOBALLY");
            }
        }

        /// <summary>Загрузить дефолтные настройки</summary>
        private async Task LoadDefaultWorkspace()
        {
            if (ActiveTab == null) return;

            var project = GetProjectForTab(ActiveTab);
            if (project == null) return;

            var result = await _dialogService.ShowMessageAsync(
                "Загрузить дефолтные настройки?",
                "Текущая раскладка окон будет заменена на дефолтную конфигурацию.\n\nВНИМАНИЕ: Это НЕ удалит ваши сохранённые настройки! Чтобы сохранить дефолтную раскладку, используйте кнопку 'Сохранить настройки для этого проекта' после загрузки.\n\nЗагрузить дефолтные настройки?",
                Views.MessageBoxType.Question,
                Views.MessageBoxButtons.YesNo
            );

            if (result == Views.MessageBoxResult.Yes)
            {
                var defaultWorkModes = _workModeConfigService.LoadDefaultConfiguration(project.Type);
                var workModes = _workModeService.InitializeWorkModes(project.Type, defaultWorkModes);

                AvailableWorkModes.Clear();
                foreach (var wm in workModes)
                {
                    AvailableWorkModes.Add(wm);
                }

                if (workModes.Count > 0)
                {
                    SwitchWorkMode(workModes[0]);
                }

                Console.WriteLine("[MainWindowViewModel] Loaded DEFAULT workspace");
            }
        }

        /// <summary>Инициализировать WorkModes для вкладки</summary>
        public void InitializeWorkModesForTab(DocumentTabViewModel tab)
        {
            var project = GetProjectForTab(tab);
            if (project == null) return;

            // Получаем сохранённые WorkModes из UserConfig проекта
            List<WorkMode>? savedWorkModes = null;
            if (project.UserConfig != null && project.UserConfig.WorkModes.Count > 0)
            {
                // TODO: Конвертировать WorkModeConfig → WorkMode
                // Пока загружаем дефолтные
                savedWorkModes = null;
            }

            var workModes = _workModeService.InitializeWorkModes(project.Type, savedWorkModes);

            AvailableWorkModes.Clear();
            foreach (var wm in workModes)
            {
                AvailableWorkModes.Add(wm);
            }

            // Устанавливаем активный режим
            var activeWM = workModes.FirstOrDefault(wm => wm.IsActive) ?? workModes.FirstOrDefault();
            if (activeWM != null)
            {
                ActiveWorkMode = activeWM;

                // ВАЖНО: Показываем модули для активного WorkMode
                Console.WriteLine($"[InitializeWorkModesForTab] Showing modules for active WorkMode: {activeWM.Title}");
                ShowModulesForWorkMode(activeWM);
            }

            Console.WriteLine($"[InitializeWorkModesForTab] Initialized {workModes.Count} WorkModes for tab");

            UpdateWorkModeMenuItems();
            UpdateModuleMenuItems();
        }

        /// <summary>Инициализировать Dock фабрику один раз</summary>
        private void InitializeDockFactory()
        {
            _dockFactory.Initialize();
            Console.WriteLine("[MainWindowViewModel] Dock factory initialized");
        }

        // ========================================
        // МЕНЮ МОДУЛЕЙ И WORKMODES
        // ========================================

        /// <summary>Элемент меню для модуля</summary>
        public class ModuleMenuItem : ReactiveObject
        {
            private bool _isEnabled;
            private bool _isChecked;

            public ModuleType Type { get; set; }
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
                    Type = metadata.ModuleType,
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

            // Ищем WorkMode по ID
            var existingWorkMode = AvailableWorkModes.FirstOrDefault(wm => wm.WorkModeId == workModeId);

            if (existingWorkMode != null)
            {
                // WorkMode уже открыт - просто переключаемся на него
                Console.WriteLine($"[ToggleWorkMode] WorkMode exists, switching to it");
                SwitchWorkMode(existingWorkMode);
            }
            else
            {
                // WorkMode не открыт - создаём новый
                Console.WriteLine($"[ToggleWorkMode] WorkMode not found, creating new");

                var project = ActiveTab != null ? GetProjectForTab(ActiveTab) : null;
                if (project == null)
                {
                    Console.WriteLine("[ToggleWorkMode] No active project");
                    return;
                }

                // Получаем WorkMode из реестра
                var workModeRegistry = App.Services.GetRequiredService<Writersword.Src.WorkModes.Common.WorkModeRegistry>();
                var workModeInstance = workModeRegistry.GetWorkMode(workModeId);

                if (workModeInstance == null)
                {
                    Console.WriteLine($"[ToggleWorkMode] WorkMode not found in registry: {workModeId}");
                    return;
                }

                // Создаём WorkMode
                var newWorkMode = _workModeService.AddWorkMode(
                    workModeId,
                    workModeInstance.DisplayName,
                    workModeInstance.Icon
                );

                newWorkMode.IsCloseable = workModeInstance.IsCloseable;
                newWorkMode.Order = workModeInstance.Order;

                AvailableWorkModes.Add(newWorkMode);
                SwitchWorkMode(newWorkMode);

                Console.WriteLine($"[ToggleWorkMode] Created and switched to: {newWorkMode.Title}");
            }

            // Обновляем галочки в меню
            UpdateWorkModeMenuItems();
        }

        /// <summary>Открыть модуль или переключиться на него</summary>
        private void ToggleModule(ModuleType moduleType)
        {
            Console.WriteLine($"[ToggleModule] ===== CALLED! Module: {moduleType} =====");

            if (ActiveWorkMode == null)
            {
                Console.WriteLine("[ToggleModule] No active WorkMode");
                return;
            }

            if (DockLayout == null)
            {
                Console.WriteLine("[ToggleModule] No DockLayout");
                return;
            }

            var docId = $"Module_{moduleType}";

            // ===== ШАГ 1: Ищем документ ВО ВСЕЙ dock-структуре =====
            var existingDoc = FindDocumentInEntireLayout(DockLayout, docId);

            if (existingDoc != null)
            {
                Console.WriteLine($"[ToggleModule] Found existing document, focusing: {moduleType}");

                // Фокусируемся на найденном документе
                if (existingDoc.Owner is IDock dock)
                {
                    dock.ActiveDockable = existingDoc;
                }

                return;
            }

            // ===== ШАГ 2: Ищем в Float окнах =====
            var floatingDoc = FindFloatingDocument(DockLayout, docId);
            if (floatingDoc != null)
            {
                Console.WriteLine($"[ToggleModule] Module is floating, focusing window: {moduleType}");
                Src.Infrastructure.Dock.HostWindow.ActivateWindow(docId);
                return;
            }

            // ===== ШАГ 3: Документ не найден - создаём =====
            Console.WriteLine($"[ToggleModule] Document not found, creating new: {moduleType}");

            var documentDock = FindDocumentDock(DockLayout);
            if (documentDock == null)
            {
                Console.WriteLine("[ToggleModule] ERROR: DocumentDock not found!");
                return;
            }

            var existingSlot = ActiveWorkMode.ModuleSlots.FirstOrDefault(s => s.ModuleType == moduleType);

            if (existingSlot != null)
            {
                existingSlot.IsVisible = true;

                var doc = _dockFactory.CreateModuleDocument(existingSlot);
                if (doc != null && documentDock.VisibleDockables != null)
                {
                    documentDock.VisibleDockables.Add(doc);
                    documentDock.ActiveDockable = doc;
                    Console.WriteLine($"[ToggleModule] Created document from existing slot: {moduleType}");
                }
            }
            else
            {
                var newSlot = new ModuleSlot
                {
                    ModuleType = moduleType,
                    IsVisible = true,
                    IsCloseable = _workModeConfigService.CanRemoveModule(ActiveWorkMode.WorkModeId, moduleType),
                    MinWidth = 200,
                    MinHeight = 150,
                    PreferredPosition = PreferredDockPosition.RightAsTab
                };

                ActiveWorkMode.ModuleSlots.Add(newSlot);
                Console.WriteLine($"[ToggleModule] Created NEW slot: {moduleType}");

                var doc = _dockFactory.CreateModuleDocument(newSlot);
                if (doc != null && documentDock.VisibleDockables != null)
                {
                    documentDock.VisibleDockables.Add(doc);
                    documentDock.ActiveDockable = doc;
                    Console.WriteLine($"[ToggleModule] Created NEW document: {moduleType}");
                }
            }
        }

        /// <summary>Найти документ во ВСЕЙ dock-структуре (включая split panels)</summary>
        private static IDockable? FindDocumentInEntireLayout(IDock rootDock, string docId)
        {
            Console.WriteLine($"[FindDocumentInEntireLayout] Searching for: {docId}");

            // Рекурсивный поиск везде
            var result = SearchInDockable(rootDock, docId);

            if (result != null)
            {
                Console.WriteLine($"[FindDocumentInEntireLayout] FOUND: {docId}");
            }
            else
            {
                Console.WriteLine($"[FindDocumentInEntireLayout] NOT FOUND: {docId}");
            }

            return result;
        }

        /// <summary>Рекурсивный поиск в dockable</summary>
        private static IDockable? SearchInDockable(IDockable? dockable, string docId)
        {
            if (dockable == null) return null;

            // Проверяем сам элемент
            if (dockable.Id == docId)
            {
                return dockable;
            }

            // Если это контейнер - ищем в детях
            if (dockable is IDock dock && dock.VisibleDockables != null)
            {
                foreach (var child in dock.VisibleDockables)
                {
                    var found = SearchInDockable(child, docId);
                    if (found != null) return found;
                }
            }

            return null;
        }

        /// <summary>Найти Float документ по ID (рекурсивный поиск)</summary>
        private IDockable? FindFloatingDocument(IRootDock rootDock, string docId)
        {
            if (rootDock.Windows == null) return null;

            foreach (var window in rootDock.Windows)
            {
                Console.WriteLine($"[FindFloatingDocument] Searching in window: {window.Id}");

                if (window.Layout != null)
                {
                    var result = FindInDockable(window.Layout, docId);
                    if (result != null) return result;
                }
            }

            return null;
        }

        /// <summary>Рекурсивный поиск документа в Dockable</summary>
        private IDockable? FindInDockable(IDockable dockable, string docId)
        {
            Console.WriteLine($"[FindInDockable] Checking: {dockable.Id} (Type: {dockable.GetType().Name})");

            // Если это наш документ - возвращаем
            if (dockable.Id == docId)
            {
                Console.WriteLine($"[FindInDockable] FOUND: {docId}");
                return dockable;
            }

            // Если это контейнер - ищем в детях
            if (dockable is IDock dock && dock.VisibleDockables != null)
            {
                foreach (var child in dock.VisibleDockables)
                {
                    var result = FindInDockable(child, docId);
                    if (result != null) return result;
                }
            }

            return null;
        }

        /// <summary>Обновить состояние элементов меню WorkMode</summary>
        private void UpdateWorkModeMenuItems()
        {
            foreach (var menuItem in AllWorkModes)
            {
                menuItem.IsChecked = AvailableWorkModes.Any(wm => wm.WorkModeId == menuItem.WorkModeId);
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

            var documentDock = DockLayout != null ? FindDocumentDock(DockLayout) : null;

            foreach (var menuItem in AllModules)
            {
                // Проверяем открыт ли модуль В DOCK
                if (documentDock?.VisibleDockables != null)
                {
                    var docId = $"Module_{menuItem.Type}";
                    menuItem.IsChecked = documentDock.VisibleDockables.Any(d => d.Id == docId);
                }
                else
                {
                    menuItem.IsChecked = false;
                }

                // Модуль доступен если:
                // 1. Универсальный (доступен везде)
                // 2. ИЛИ НЕ запрещён в текущем WorkMode (проверяем через ConfigService)
                if (menuItem.IsUniversal)
                {
                    menuItem.IsEnabled = true;
                }
                else
                {
                    // Проверяем через WorkModeConfigurationService
                    var canAdd = _workModeConfigService.CanRemoveModule(ActiveWorkMode.WorkModeId, menuItem.Type);
                    menuItem.IsEnabled = true; // Пока разрешаем все, логику Forbidden добавим позже
                }

                Console.WriteLine($"  {menuItem.Icon} {menuItem.Name}: Enabled={menuItem.IsEnabled}, Checked={menuItem.IsChecked}");
            }
        }
    }
}