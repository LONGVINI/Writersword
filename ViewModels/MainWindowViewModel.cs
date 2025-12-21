using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Dock.Avalonia.Controls;
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


namespace Writersword.ViewModels
{
    /// <summary>
    /// ViewModel главного окна приложения
    /// Управляет меню, командами, текущим модулем
    /// </summary>
    public class MainWindowViewModel : ViewModelBase
    {
        private readonly IDialogService _dialogService;
        private readonly ISettingsService _settingsService;
        private readonly IProjectService _projectService;
        private readonly IHotKeyService _hotKeyService;
        private readonly IWorkModeConfigurationService _workModeConfigService;
        private readonly IWorkModeService _workModeService;
        private readonly Src.Infrastructure.Dock.DockFactory _dockFactory;
        private readonly Dictionary<string, IRootDock> _tabLayouts = new();

        private WorkMode? _activeWorkMode;
        private string _title = "Writersword";
        private object? _currentModule;
        private DocumentTabViewModel? _activeTab;
        private IRootDock? _dockLayout;

        private readonly ModuleRegistry _moduleRegistry; // Реестр модулей
        private List<IModuleMetadata>? _cachedModuleMetadata; // Кэш всех метаданных модулей
        private readonly List<IDisposable> _slotSubscriptions = new(); // Подписки на изменения слотов
        private readonly Dictionary<ModuleType, IDockable> _floatingDocuments = new(); // Отслеживание Float окон

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

        /// <summary>Открытые вкладки документов</summary>
        public ObservableCollection<DocumentTabViewModel> OpenTabs { get; }

        /// <summary>Список всех доступных типов модулей с их метаданными</summary>
        public ObservableCollection<ModuleMenuItem> AllModules { get; } = new();

        /// <summary>Список всех доступных WorkMode типов с их метаданными</summary>
        public ObservableCollection<WorkModeMenuItem> AllWorkModes { get; } = new();

        /// <summary>Активная вкладка</summary>
        public DocumentTabViewModel? ActiveTab
        {
            get => _activeTab;
            set => this.RaiseAndSetIfChanged(ref _activeTab, value);
        }

        /// <summary>Активный режим работы</summary>
        public WorkMode? ActiveWorkMode
        {
            get => _activeWorkMode;
            set => this.RaiseAndSetIfChanged(ref _activeWorkMode, value);
        }

        /// <summary>Доступные режимы работы для текущей вкладки</summary>
        public ObservableCollection<WorkMode> AvailableWorkModes { get; } = new();

        // Команды для меню
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
        public ReactiveCommand<WorkModeType, Unit> ToggleWorkModeCommand { get; }
        public ReactiveCommand<ModuleType, Unit> ToggleModuleCommand { get; }

        public MainWindowViewModel(
            IDialogService dialogService,
            ISettingsService settingsService,
            IProjectService projectService,
            IHotKeyService hotKeyService,
            IWorkModeConfigurationService workModeConfigService,
            IWorkModeService workModeService,
            Src.Infrastructure.Dock.DockFactory dockFactory)
        {
            _dialogService = dialogService;
            _settingsService = settingsService;
            _projectService = projectService;
            _hotKeyService = hotKeyService;
            _workModeConfigService = workModeConfigService;
            _workModeService = workModeService;
            _dockFactory = dockFactory;
            _moduleRegistry = App.Services.GetRequiredService<ModuleRegistry>();
            _cachedModuleMetadata = _moduleRegistry.GetAllModuleMetadata().ToList();

            OpenTabs = new ObservableCollection<DocumentTabViewModel>();

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
            ToggleWorkModeCommand = ReactiveCommand.Create<WorkModeType>(ToggleWorkMode);
            ToggleModuleCommand = ReactiveCommand.Create<ModuleType>(ToggleModule);

            _settingsService.Load();

            RegisterHotKeys(); // Регистрация горячих клавиш
            InitializeDockFactory(); // Инициализация Dock фабрики
            InitializeMenuItems(); // Инициализация AllModules и AllWorkModes
            UpdateWorkModeMenuItems();
            UpdateModuleMenuItems();
        }

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

        /// <summary>Получить проект для вкладки</summary>
        private ProjectFile? GetProjectForTab(DocumentTabViewModel tab)
        {
            var filePath = tab.GetModel().FilePath;
            if (string.IsNullOrEmpty(filePath)) return null;

            var project = _projectService.GetProjectByPath(filePath);

            if (project != null)
            {
                Console.WriteLine($"[GetProjectForTab] Found project: {project.Title}, Documents: {project.Documents.Count}");
            }
            else
            {
                Console.WriteLine($"[GetProjectForTab] Project NOT found for path: {filePath}");
            }

            return project;
        }

        /// <summary>Создать новый проект (показывает Welcome окно)</summary>
        private async void NewProject()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                await App.ShowWelcomeScreen(desktop.MainWindow!);
            }
        }

        /// <summary>Открыть существующий проект</summary>
        private async System.Threading.Tasks.Task OpenProject()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                await App.ShowWelcomeScreen(desktop.MainWindow!);
            }
        }

        /// <summary>Сохранить активный проект</summary>
        private async System.Threading.Tasks.Task SaveProject()
        {
            if (ActiveTab == null) return;

            var project = GetProjectForTab(ActiveTab);
            if (project == null) return;

            // Синхронизируем содержимое вкладки с проектом
            SyncTabToProject(ActiveTab, project);

            var filePath = _projectService.GetProjectPath(project);
            if (string.IsNullOrEmpty(filePath))
            {
                // Проект ещё не сохранён - вызываем Save As
                await SaveAsProject();
                return;
            }

            // Сохраняем в существующий файл
            await _projectService.SaveAsync(project, filePath);
        }

        /// <summary>Сохранить активный проект как...</summary>
        private async System.Threading.Tasks.Task SaveAsProject()
        {
            if (ActiveTab == null) return;

            var project = GetProjectForTab(ActiveTab);
            if (project == null) return;

            // Синхронизируем содержимое вкладки с проектом
            SyncTabToProject(ActiveTab, project);

            var path = await _dialogService.SaveFileAsync();
            if (path != null)
            {
                var success = await _projectService.SaveAsync(project, path);
                if (success)
                {
                    Title = $"Writersword - {project.Title}";

                    // Обновляем FilePath вкладки
                    ActiveTab.GetModel().FilePath = path;
                }
            }
        }

        /// <summary>Синхронизировать содержимое вкладки с проектом</summary>
        private void SyncTabToProject(DocumentTabViewModel tab, ProjectFile project)
        {
            // Находим документ в проекте
            var doc = project.Documents.FirstOrDefault(d => d.Id == tab.Id);
            if (doc != null)
            {
                doc.Content = tab.Content;
                doc.Title = tab.Title;
                doc.IsActive = tab.IsActive;
            }
            else
            {
                // Документа нет - добавляем
                project.Documents.Add(tab.GetModel());
            }
        }

        /// <summary>Выход из приложения</summary>
        private void Exit()
        {
            System.Environment.Exit(0);
        }

        /// <summary>Загрузить проект при старте приложения</summary>
        public async void LoadProject(string filePath)
        {
            Console.WriteLine($"[LoadProject] START Loading: {filePath}");

            var project = await _projectService.LoadAsync(filePath);

            if (project == null)
            {
                Console.WriteLine("[LoadProject] Project is null!");
                return;
            }

            Console.WriteLine($"[LoadProject] Loaded project: {project.Title}, Documents: {project.Documents.Count}");

            if (project.Documents.Count > 0)
            {
                // Проверяем не открыты ли уже вкладки из этого проекта
                var hasOpenTabs = OpenTabs.Any(t => t.GetModel().FilePath == filePath);

                if (hasOpenTabs)
                {
                    Console.WriteLine($"[LoadProject] Tabs already open for this project, skipping duplicate load");

                    // Если вкладки уже открыты - просто активируем первую
                    var firstTab = OpenTabs.First(t => t.GetModel().FilePath == filePath);
                    ActivateTab(firstTab);
                    return;
                }

                Console.WriteLine($"[LoadProject] Adding {project.Documents.Count} tabs");

                var docs = project.Documents.ToList();

                foreach (var doc in docs)
                {
                    doc.FilePath = filePath;
                    var tabVM = new DocumentTabViewModel(doc, CloseTab);
                    OpenTabs.Add(tabVM);
                    Console.WriteLine($"[LoadProject] Added tab: {doc.Title}");

                    if (doc.IsActive && ActiveTab == null)
                    {
                        ActiveTab = tabVM;
                    }
                }

                // Если нет активной вкладки - делаем первую активной
                if (ActiveTab == null && OpenTabs.Count > 0)
                {
                    Console.WriteLine($"[LoadProject] No active tab, setting first as active");
                    ActiveTab = OpenTabs[0];
                    ActiveTab.IsActive = true;
                }

                Title = $"Writersword - {project.Title}";

                if (ActiveTab != null)
                {
                    Console.WriteLine($"[LoadProject] Showing text editor for active tab: {ActiveTab.Title}");
                    ShowTextEditor();
                }
            }
            else
            {
                Console.WriteLine("[LoadProject] No documents in project");
            }

            // Добавляем в недавние
            _settingsService.AddRecentProject(filePath);

            Console.WriteLine($"[LoadProject] FINISHED. Total tabs: {OpenTabs.Count}");
        }

        /// <summary>Создать новую вкладку - открывает Welcome окно</summary>
        public async void CreateNewTab()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow != null)
            {
                await App.ShowWelcomeScreen(desktop.MainWindow);
            }
        }

        /// <summary>Добавить новую вкладку в приложение</summary>
        public void AddNewTab(string title, string content, string? filePath)
        {
            // ПРОВЕРКА: Если вкладка с таким FilePath уже существует - активируем её
            if (!string.IsNullOrEmpty(filePath))
            {
                var existingTab = OpenTabs.FirstOrDefault(t => t.GetModel().FilePath == filePath && t.Title == title);
                if (existingTab != null)
                {
                    ActivateTab(existingTab);
                    return;
                }
            }

            // Деактивируем все вкладки
            foreach (var tab in OpenTabs)
            {
                tab.IsActive = false;
            }

            // Получаем проект по FilePath
            var project = !string.IsNullOrEmpty(filePath) ? _projectService.GetProjectByPath(filePath) : null;

            // Создаём новый документ
            var newDoc = new DocumentTab
            {
                Title = title,
                Content = content,
                IsActive = true,
                FilePath = filePath
            };

            // Добавляем документ в проект ТОЛЬКО если его там ещё нет
            if (project != null)
            {
                var existingDoc = project.Documents.FirstOrDefault(d => d.Title == title && d.FilePath == filePath);
                if (existingDoc == null)
                {
                    project.Documents.Add(newDoc);
                    // Сохраняем проект с новым документом
                    _ = _projectService.SaveAsync(project, filePath!);
                }
                else
                {
                    // Используем существующий документ вместо создания нового
                    newDoc = existingDoc;
                }
            }

            var tabVM = new DocumentTabViewModel(newDoc, CloseTab);
            OpenTabs.Add(tabVM);
            ActiveTab = tabVM;

            InitializeWorkModesForTab(tabVM);

            ShowTextEditor();
        }

        // Измени метод ActivateTab:
        public void ActivateTab(DocumentTabViewModel tab)
        {
            foreach (var t in OpenTabs)
            {
                t.IsActive = false;
            }

            tab.IsActive = true;
            ActiveTab = tab;

            // Не инициализируем WorkModes заново, используем существующие
            var project = GetProjectForTab(tab);
            if (project != null)
            {
                string tabKey = tab.GetModel().FilePath ?? tab.Id.ToString();

                // Если layout уже создан для этой вкладки - переиспользуем его
                if (_tabLayouts.ContainsKey(tabKey))
                {
                    Console.WriteLine($"[ActivateTab] Reusing existing layout for tab: {tab.Title}");
                    DockLayout = _tabLayouts[tabKey];
                }
                else
                {
                    Console.WriteLine($"[ActivateTab] Creating new layout for tab: {tab.Title}");
                    InitializeWorkModesForTab(tab);

                    // Сохраняем созданный layout
                    if (DockLayout != null)
                    {
                        _tabLayouts[tabKey] = DockLayout;
                    }
                }
            }
        }

        /// <summary>Закрыть вкладку</summary>
        public void CloseTab(DocumentTabViewModel tab)
        {
            Console.WriteLine($"[CloseTab] Closing tab: {tab.Title}");

            // Получаем проект для вкладки
            var project = GetProjectForTab(tab);

            if (project != null)
            {
                // Синхронизируем содержимое вкладки с документом в проекте
                var doc = project.Documents.FirstOrDefault(d => d.Id == tab.Id);
                if (doc != null)
                {
                    doc.Content = tab.Content;
                    doc.Title = tab.Title;
                    doc.IsActive = false;
                }

                // Сохраняем проект
                var filePath = _projectService.GetProjectPath(project);
                if (filePath != null)
                {
                    _ = _projectService.SaveAsync(project, filePath);
                }
            }

            // Удаляем из коллекции UI
            OpenTabs.Remove(tab);

            // Если были другие вкладки - активируем первую
            if (OpenTabs.Count > 0)
            {
                ActivateTab(OpenTabs[0]);
            }
            else
            {
                // ИСПРАВЛЕНИЕ: Очищаем список открытых проектов когда закрыли последнюю вкладку
                Console.WriteLine("[CloseTab] Last tab closed, clearing open projects list");
                _settingsService.SaveOpenProjects(new List<string>());

                ActiveTab = null;
                CurrentModule = null;
                ShowWelcomeIfNoTabs();
            }
        }

        /// <summary>Показать Welcome если нет вкладок</summary>
        private async void ShowWelcomeIfNoTabs()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow != null)
            {
                await App.ShowWelcomeScreen(desktop.MainWindow);
            }
        }

        private void RegisterHotKeys()
        {
            // Ctrl+N - Новый проект
            _hotKeyService.Register(
                "file.new",
                new HotKey
                {
                    DisplayNameKey = "HotKey_File_New",  // ← Ключ для локализации
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
                ReactiveCommand.Create(() =>
                {
                    if (ActiveTab != null)
                        CloseTab(ActiveTab);
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

        /// <summary>Переключить режим работы</summary>
        private void SwitchWorkMode(WorkMode workMode)
        {
            if (ActiveTab == null) return;

            var docModel = ActiveTab.GetModel();
            docModel.SetActiveWorkMode(workMode);

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

                _slotSubscriptions.Add(subscription); // ← СОХРАНЯЕМ!
            }

            Console.WriteLine($"[ShowModulesForWorkMode] Subscribed to {workMode.ModuleSlots.Count} slot changes");
        }

        /// <summary>Найти DocumentDock в структуре</summary>
        private DocumentDock? FindDocumentDock(IDockable? root)
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

        /// <summary>Создать View для модуля</summary>
        private Avalonia.Controls.Control? CreateModuleView(ModuleType moduleType, object? viewModel)
        {
            if (viewModel == null) return null;

            return moduleType switch
            {
                ModuleType.TextEditor => new Modules.TextEditor.Views.TextEditorView { DataContext = viewModel },
                ModuleType.Synonyms => new Modules.Synonyms.Views.SynonymsView { DataContext = viewModel },
                ModuleType.Notes => new Modules.Notes.Views.NotesView { DataContext = viewModel },
                ModuleType.Timer => new Modules.Timer.Views.TimerView { DataContext = viewModel },
                _ => null
            };
        }

        /// <summary>Сохранить настройки для этого проекта</summary>
        private async System.Threading.Tasks.Task SaveWorkspaceForProject()
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
                var docModel = ActiveTab.GetModel();
                docModel.WorkModes = _workModeService.GetAllWorkModes();

                // Сохраняем проект
                var project = GetProjectForTab(ActiveTab);
                if (project != null)
                {
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
        private async System.Threading.Tasks.Task SaveWorkspaceGlobally()
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
        private async System.Threading.Tasks.Task LoadDefaultWorkspace()
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
        private void InitializeWorkModesForTab(DocumentTabViewModel tab)
        {
            var project = GetProjectForTab(tab);
            if (project == null) return;

            var docModel = tab.GetModel();
            var workModes = _workModeService.InitializeWorkModes(project.Type, docModel.WorkModes);

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

            public WorkModeType Type { get; set; }
            public string Name { get; set; } = "";
            public string Icon { get; set; } = "";

            /// <summary>Открыт ли WorkMode</summary>
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
                    IsEnabled = false,  // Обновится позже при переключении WorkMode
                    IsChecked = false   // Обновится позже
                });
            }

            Console.WriteLine($"[InitializeMenuItems] Loaded {AllModules.Count} modules from metadata");

            // ===== АВТОМАТИЧЕСКАЯ ЗАГРУЗКА WORKMODES =====
            var allWorkModeMetadata = WorkModeMetadataRegistry.GetAll();

            foreach (var metadata in allWorkModeMetadata)
            {
                AllWorkModes.Add(new WorkModeMenuItem
                {
                    Type = metadata.Type,
                    Name = metadata.DisplayName,
                    Icon = metadata.Icon,
                    IsChecked = false  // Обновится позже
                });
            }

            Console.WriteLine($"[InitializeMenuItems] Loaded {AllWorkModes.Count} WorkModes from metadata");
        }

        /// <summary>Открыть/переключить WorkMode</summary>
        private void ToggleWorkMode(WorkModeType workModeType)
        {
            Console.WriteLine($"[ToggleWorkMode] Toggling: {workModeType}");

            // Ищем WorkMode по типу
            var existingWorkMode = AvailableWorkModes.FirstOrDefault(wm => wm.Type == workModeType);

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

                // Получаем метаданные
                var metadata = WorkModeMetadataRegistry.Get(workModeType);
                if (metadata == null)
                {
                    Console.WriteLine($"[ToggleWorkMode] Metadata not found for {workModeType}");
                    return;
                }

                // Создаём WorkMode
                var newWorkMode = _workModeService.AddWorkMode(
                    workModeType,
                    metadata.DisplayName,
                    metadata.Icon
                );

                newWorkMode.IsCloseable = metadata.IsCloseable;
                newWorkMode.Order = metadata.Order;

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

            var documentDock = FindDocumentDock(DockLayout);
            if (documentDock == null)
            {
                Console.WriteLine("[ToggleModule] DocumentDock not found!");
                return;
            }

            var docId = $"Module_{moduleType}";

            // Проверяем в основном Dock
            var existingDoc = documentDock.VisibleDockables?.FirstOrDefault(d => d.Id == docId);

            if (existingDoc != null)
            {
                documentDock.ActiveDockable = existingDoc;
                Console.WriteLine($"[ToggleModule] Focused on existing document: {moduleType}");
                return;
            }

            // ДИАГНОСТИКА: Смотрим что в RootDock  // ← ЭТИ СТРОКИ ДОЛЖНЫ БЫТЬ!
            Console.WriteLine($"[ToggleModule] DockLayout.Windows count: {DockLayout.Windows?.Count ?? 0}");
            if (DockLayout.Windows != null)
            {
                foreach (var window in DockLayout.Windows)
                {
                    Console.WriteLine($"  Window: {window.Id}, Layout: {window.Layout?.Id}");
                    if (window.Layout?.VisibleDockables != null)
                    {
                        foreach (var d in window.Layout.VisibleDockables)
                        {
                            Console.WriteLine($"    Dockable: {d.Id}");
                        }
                    }
                }
            }

            // Проверяем в Float окнах через RootDock
            var floatingDoc = FindFloatingDocument(DockLayout, docId);
            if (floatingDoc != null)
            {
                Console.WriteLine($"[ToggleModule] Module is floating, focusing window: {moduleType}");

                // Вызываем статический метод напрямую
                Src.Infrastructure.Dock.HostWindow.ActivateWindow(docId);
                Console.WriteLine($"[ToggleModule] Activated Float window for: {moduleType}");

                return;
            }

            // Модуль закрыт → открываем
            var existingSlot = ActiveWorkMode.ModuleSlots.FirstOrDefault(s => s.ModuleType == moduleType);

            if (existingSlot != null)
            {
                existingSlot.IsVisible = true;

                // Если Float окно открыто → закрываем его ПЕРЕД добавлением в Dock
                if (Src.Infrastructure.Dock.HostWindow.IsWindowOpen(docId))
                {
                    Console.WriteLine($"[ToggleModule] Closing Float window before adding to Dock: {moduleType}");
                    Src.Infrastructure.Dock.HostWindow.CloseWindow(docId);
                }

                var doc = _dockFactory.CreateModuleDocument(existingSlot);
                if (doc != null && documentDock.VisibleDockables != null)
                {
                    documentDock.VisibleDockables.Add(doc);
                    documentDock.ActiveDockable = doc;
                    Console.WriteLine($"[ToggleModule] Added document: {moduleType}");
                }
            }
            else
            {
                var position = FindFreePositionForModule();

                var newSlot = new ModuleSlot
                {
                    ModuleType = moduleType,
                    Position = position,
                    Size = new WorkModeGridSize(1, 1),
                    IsVisible = true,
                    IsCloseable = !WorkModeRules.GetRequiredModules(ActiveWorkMode.Type).Contains(moduleType),
                    MinWidth = 200,
                    MinHeight = 150
                };

                ActiveWorkMode.ModuleSlots.Add(newSlot);
                Console.WriteLine($"[ToggleModule] Added slot: {moduleType}");

                var doc = _dockFactory.CreateModuleDocument(newSlot);
                if (doc != null && documentDock.VisibleDockables != null)
                {
                    documentDock.VisibleDockables.Add(doc);
                    documentDock.ActiveDockable = doc;
                    Console.WriteLine($"[ToggleModule] Added new document: {moduleType}");
                }
            }
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

        /// <summary>Найти свободную позицию для нового модуля (справа от последнего)</summary>
        private WorkModeGridPosition FindFreePositionForModule()
        {
            if (ActiveWorkMode == null || ActiveWorkMode.ModuleSlots.Count == 0)
            {
                return new WorkModeGridPosition { Row = 0, Column = 0 };
            }

            // Находим максимальный Column среди видимых модулей
            var maxColumn = ActiveWorkMode.ModuleSlots
                .Where(s => s.IsVisible)
                .Max(s => s.Position.Column + s.Size.ColumnSpan - 1);

            // Новый модуль справа от последнего
            return new WorkModeGridPosition
            {
                Row = 0,
                Column = maxColumn + 1
            };
        }

        /// <summary>Обновить состояние элементов меню WorkMode</summary>
        private void UpdateWorkModeMenuItems()
        {
            foreach (var menuItem in AllWorkModes)
            {
                menuItem.IsChecked = AvailableWorkModes.Any(wm => wm.Type == menuItem.Type);
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
                // Проверяем открыт ли модуль В DOCK (не в slot.IsVisible!)
                if (documentDock?.VisibleDockables != null)
                {
                    var docId = $"Module_{menuItem.Type}";
                    menuItem.IsChecked = documentDock.VisibleDockables.Any(d => d.Id == docId);
                }
                else
                {
                    menuItem.IsChecked = false;
                }

                // Модуль доступен если универсальный или разрешён для текущего WorkMode
                if (menuItem.IsUniversal)
                {
                    menuItem.IsEnabled = true;
                }
                else
                {
                    var moduleMetadata = _cachedModuleMetadata?
                        .FirstOrDefault(m => m.ModuleType == menuItem.Type);

                    menuItem.IsEnabled = moduleMetadata?.AvailableInWorkModes
                        .Contains(ActiveWorkMode.Type) ?? false;
                }

                Console.WriteLine($"  {menuItem.Icon} {menuItem.Name}: Enabled={menuItem.IsEnabled}, Checked={menuItem.IsChecked}");
            }
        }

        /// <summary>Получить отображаемое имя модуля</summary>
        private string GetModuleDisplayName(ModuleType type)
        {
            var menuItem = AllModules.FirstOrDefault(m => m.Type == type);
            return menuItem?.Name ?? type.ToString();
        }
    }
}