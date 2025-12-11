using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using ReactiveUI;
using System.Reactive;
using Writersword.Core.Enums;
using Writersword.Modules.TextEditor.ViewModels;
using Writersword.Services;
using Writersword.Services.Interfaces;
using System.Collections.ObjectModel;
using System.Linq;
using System;
using System.Reactive.Linq;
using Writersword.Core.Models.Project;

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
            if (ActiveTab == null) return;

            var viewModel = new TextEditorViewModel();
            viewModel.LoadDocument(ActiveTab.Content);

            // Подписываемся на изменения текста
            viewModel.WhenAnyValue(x => x.PlainText)
                .Subscribe(text =>
                {
                    if (ActiveTab != null)
                    {
                        ActiveTab.Content = text;
                    }
                });

            var view = new Modules.TextEditor.Views.TextEditorView
            {
                DataContext = viewModel
            };
            CurrentModule = view;
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

        /// <summary>Сохранить проект (если не сохранён - вызывает Save As)</summary>
        private async System.Threading.Tasks.Task SaveProject()
        {
            if (_projectService.CurrentProject == null) return;

            // Синхронизируем вкладки с проектом перед сохранением
            SyncTabsToProject();

            // Если проект новый и ещё не сохранён
            if (string.IsNullOrEmpty(_projectService.CurrentFilePath))
            {
                await SaveAsProject();
                return;
            }

            // Сохраняем в существующий файл
            await _projectService.SaveAsync(
                _projectService.CurrentProject,
                _projectService.CurrentFilePath!
            );
        }

        /// <summary>Сохранить проект как... (выбор нового пути)</summary>
        private async System.Threading.Tasks.Task SaveAsProject()
        {
            if (_projectService.CurrentProject == null) return;

            // Синхронизируем вкладки с проектом перед сохранением
            SyncTabsToProject();

            var path = await _dialogService.SaveFileAsync();
            if (path != null)
            {
                var success = await _projectService.SaveAsync(_projectService.CurrentProject, path);
                if (success)
                {
                    Title = $"Writersword - {_projectService.CurrentProject.Title}";
                    _settingsService.LastOpenedProject = path;
                }
            }
        }

        /// <summary>Синхронизировать вкладки с проектом перед сохранением</summary>
        private void SyncTabsToProject()
        {
            if (_projectService.CurrentProject == null) return;

            // Очищаем старые документы
            _projectService.CurrentProject.Documents.Clear();

            // Добавляем текущие вкладки
            foreach (var tab in OpenTabs)
            {
                _projectService.CurrentProject.Documents.Add(tab.GetModel());
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
            var project = await _projectService.LoadAsync(filePath);
            if (project != null)
            {
                Title = $"Writersword - {project.Title}";
                RefreshAfterProjectLoad();
            }
        }

        /// <summary>Обновить UI после загрузки проекта</summary>
        public void RefreshAfterProjectLoad()
        {
            if (_projectService.CurrentProject == null) return;

            // Обновляем заголовок окна
            Title = $"Writersword - {_projectService.CurrentProject.Title}";

            // Загружаем вкладки документов
            LoadDocumentsFromProject();

            // Показываем текстовый редактор если есть активная вкладка
            if (ActiveTab != null)
            {
                ShowTextEditor();
            }
        }

        /// <summary>Загрузить вкладки из проекта</summary>
        private void LoadDocumentsFromProject()
        {
            if (_projectService.CurrentProject == null) return;

            OpenTabs.Clear();
            ActiveTab = null;

            foreach (var doc in _projectService.CurrentProject.Documents)
            {
                var tabVM = new DocumentTabViewModel(doc, CloseTab);
                OpenTabs.Add(tabVM);

                if (doc.IsActive)
                {
                    ActiveTab = tabVM;
                }
            }

            // Если есть вкладки но нет активной - активируем первую
            if (OpenTabs.Count > 0 && ActiveTab == null)
            {
                ActiveTab = OpenTabs[0];
                ActiveTab.IsActive = true;
            }
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

        /// <summary>Добавить новую вкладку в проект</summary>
        public void AddNewTab(string title, string content, string? filePath)
        {
            if (_projectService.CurrentProject == null) return;

            // ПРОВЕРКА: Если вкладка с таким FilePath уже существует - активируем её
            if (!string.IsNullOrEmpty(filePath))
            {
                var existingTab = OpenTabs.FirstOrDefault(t => t.GetModel().FilePath == filePath);
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

            // Создаём новый документ
            var newDoc = new DocumentTab
            {
                Title = title,
                Content = content,
                IsActive = true,
                FilePath = filePath
            };

            _projectService.CurrentProject.Documents.Add(newDoc);

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
            if (_projectService.CurrentProject == null) return;

            // Удаляем из коллекции UI
            OpenTabs.Remove(tab);

            // Удаляем из проекта
            var docToRemove = _projectService.CurrentProject.Documents
                .FirstOrDefault(d => d.Id == tab.Id);
            if (docToRemove != null)
            {
                _projectService.CurrentProject.Documents.Remove(docToRemove);
            }

            // Если были другие вкладки - активируем первую
            if (OpenTabs.Count > 0)
            {
                ActivateTab(OpenTabs[0]);
            }
            else
            {
                // Последняя вкладка закрыта - очищаем редактор и показываем Welcome
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