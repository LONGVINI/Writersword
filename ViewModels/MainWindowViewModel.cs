using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using Writersword.Core.Enums;
using Writersword.Core.Models.Project;
using Writersword.Core.Models.Settings;
using Writersword.Core.Models.WorkModes;
using Writersword.Core.Services.Interfaces;
using Writersword.Modules.TextEditor.ViewModels;
using Writersword.Services;
using Writersword.Services.Interfaces;

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

        private WorkMode? _activeWorkMode;
        private string _title = "Writersword";
        private object? _currentModule;
        private DocumentTabViewModel? _activeTab;

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

        /// <summary>Открытые вкладки документов</summary>
        public ObservableCollection<DocumentTabViewModel> OpenTabs { get; }

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

        public MainWindowViewModel(
            IDialogService dialogService,
            ISettingsService settingsService,
            IProjectService projectService,
            IHotKeyService hotKeyService,
            IWorkModeConfigurationService workModeConfigService,
            IWorkModeService workModeService)
        {
            _dialogService = dialogService;
            _settingsService = settingsService;
            _projectService = projectService;
            _hotKeyService = hotKeyService;
            _workModeConfigService = workModeConfigService; 
            _workModeService = workModeService;              

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

            _settingsService.Load();

             RegisterHotKeys();
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

        /// <summary>Активировать вкладку</summary>
        public void ActivateTab(DocumentTabViewModel tab)
        {
            // Деактивируем все вкладки
            foreach (var t in OpenTabs)
            {
                t.IsActive = false;
            }

            // Активируем выбранную
            tab.IsActive = true;
            ActiveTab = tab;

            InitializeWorkModesForTab(tab);

            ShowTextEditor();
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
                ReactiveCommand.Create(() => {
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

            //ShowPlaceholderForWorkMode(workMode); // ЗАГЛУШКА!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!
           ShowModulesForWorkMode(workMode);
        }

        /// <summary>Показать заглушку для WorkMode</summary>
        private void ShowPlaceholderForWorkMode(WorkMode workMode)
        {
            // Создаём простой TextBlock как заглушку
            var placeholder = new Avalonia.Controls.TextBlock
            {
                Text = $"WorkMode: {workMode.Title}\n\nМодулей: {workMode.ModuleSlots.Count}\n\n" +
                       string.Join("\n", workMode.ModuleSlots.Select(ms => $"- {ms.ModuleType}")),
                FontSize = 16,
                Foreground = Avalonia.Media.Brushes.White,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };

            CurrentModule = placeholder;
        }

        /// <summary>Показать модули для WorkMode</summary>
        private void ShowModulesForWorkMode(WorkMode workMode)
        {
            Console.WriteLine($"[ShowModulesForWorkMode] Showing modules for: {workMode.Title}");

            // Пока просто показываем текстовый редактор
            // TODO: В будущем здесь будет Grid с модулями
            ShowTextEditor();
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
            }

            Console.WriteLine($"[MainWindowViewModel] Initialized {workModes.Count} WorkModes for tab");
        }
    }
}