using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using Writersword.Core.Enums;
using Writersword.Core.Models.Project;
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

        // Команды для меню
        public ReactiveCommand<Unit, Unit> NewProjectCommand { get; }
        public ReactiveCommand<Unit, Unit> OpenProjectCommand { get; }
        public ReactiveCommand<Unit, Unit> SaveProjectCommand { get; }
        public ReactiveCommand<Unit, Unit> SaveAsProjectCommand { get; }
        public ReactiveCommand<Unit, Unit> ExitCommand { get; }
        public ReactiveCommand<Unit, Unit> CreateNewTabCommand { get; }

        public MainWindowViewModel(
            IDialogService dialogService,
            ISettingsService settingsService,
            IProjectService projectService)
        {
            _dialogService = dialogService;
            _settingsService = settingsService;
            _projectService = projectService;

            OpenTabs = new ObservableCollection<DocumentTabViewModel>();

            // Создаём команды
            NewProjectCommand = ReactiveCommand.Create(NewProject);
            OpenProjectCommand = ReactiveCommand.CreateFromTask(OpenProject);
            SaveProjectCommand = ReactiveCommand.CreateFromTask(SaveProject);
            SaveAsProjectCommand = ReactiveCommand.CreateFromTask(SaveAsProject);
            ExitCommand = ReactiveCommand.Create(Exit);
            CreateNewTabCommand = ReactiveCommand.Create(CreateNewTab);

            _settingsService.Load();
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

        ///// <summary>Обновить UI после загрузки проекта</summary>
        //public void RefreshAfterProjectLoad()
        //{
        //    var stackTrace = new System.Diagnostics.StackTrace(true);
        //    Console.WriteLine($"[RefreshAfterProjectLoad] CALLED FROM:\n{stackTrace}");
        //    Console.WriteLine($"[RefreshAfterProjectLoad] OpenTabs count: {OpenTabs.Count}");

        //    // ИСПРАВЛЕНИЕ: Показываем Welcome ТОЛЬКО если совсем нет вкладок
        //    if (OpenTabs.Count == 0)
        //    {
        //        Console.WriteLine("[RefreshAfterProjectLoad] No tabs, showing welcome screen");
        //        ShowWelcomeIfNoTabs();
        //    }
        //    else
        //    {
        //        // Есть вкладки - показываем редактор
        //        if (ActiveTab != null)
        //        {
        //            Console.WriteLine($"[RefreshAfterProjectLoad] Active tab exists: {ActiveTab.Title}, showing editor");
        //            ShowTextEditor();
        //        }
        //        else
        //        {
        //            // Нет активной вкладки - активируем первую
        //            Console.WriteLine("[RefreshAfterProjectLoad] No ActiveTab, activating first tab");
        //            if (OpenTabs.Count > 0)
        //            {
        //                ActivateTab(OpenTabs[0]);
        //            }
        //        }
        //    }
        //}

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
    }
}