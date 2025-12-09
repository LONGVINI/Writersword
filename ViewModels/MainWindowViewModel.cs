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

        // ========================================
        // Команды для меню
        // ========================================

        public ReactiveCommand<Unit, Unit> NewProjectCommand { get; }
        public ReactiveCommand<Unit, Unit> OpenProjectCommand { get; }
        public ReactiveCommand<Unit, Unit> SaveProjectCommand { get; }
        public ReactiveCommand<Unit, Unit> SaveAsProjectCommand { get; }
        public ReactiveCommand<Unit, Unit> ExitCommand { get; }

        public MainWindowViewModel(
            IDialogService dialogService,
            ISettingsService settingsService,
            IProjectService projectService)
        {
            _dialogService = dialogService;
            _settingsService = settingsService;
            _projectService = projectService;

            // Создаём команды
            NewProjectCommand = ReactiveCommand.Create(NewProject);
            OpenProjectCommand = ReactiveCommand.CreateFromTask(
                OpenProject,
                outputScheduler: RxApp.MainThreadScheduler
            );
            OpenTabs = new ObservableCollection<DocumentTabViewModel>();
            SaveProjectCommand = ReactiveCommand.CreateFromTask(SaveProject);
            SaveAsProjectCommand = ReactiveCommand.CreateFromTask(SaveAsProject);
            ExitCommand = ReactiveCommand.Create(Exit);

            _settingsService.Load();
            ShowTextEditor();
        }

        /// <summary>Открытые вкладки документов</summary>
        public ObservableCollection<DocumentTabViewModel> OpenTabs { get; }

        /// <summary>Активная вкладка</summary>
        private DocumentTabViewModel? _activeTab;
        public DocumentTabViewModel? ActiveTab
        {
            get => _activeTab;
            set => this.RaiseAndSetIfChanged(ref _activeTab, value);
        }

        /// <summary>Показать модуль текстового редактора</summary>
        public void ShowTextEditor()
        {
            var viewModel = new TextEditorViewModel();

            // Загружаем содержимое активной вкладки
            if (ActiveTab != null)
            {
                viewModel.LoadDocument(ActiveTab.Content);

                // Подписываемся на изменения текста
                viewModel.WhenAnyValue(x => x.PlainText)
                    .Subscribe(text => ActiveTab.Content = text);
            }

            var view = new Modules.TextEditor.Views.TextEditorView
            {
                DataContext = viewModel
            };
            CurrentModule = view;
        }

        // ========================================
        // Команды
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
        private async System.Threading.Tasks.Task OpenProject()
        {
            var path = await _dialogService.OpenFileAsync();
            if (path != null)
            {
                var project = await _projectService.LoadAsync(path);
                if (project != null)
                {
                    Title = $"Writersword - {project.Title}";
                    _settingsService.LastOpenedProject = path;

                    // Загружаем вкладки документов
                    // TODO: Отобразить документы проекта
                }
            }
        }

        /// <summary>Сохранить проект (если не сохранён - вызывает Save As)</summary>
        private async System.Threading.Tasks.Task SaveProject()
        {
            // Если проект новый и ещё не сохранён
            if (string.IsNullOrEmpty(_projectService.CurrentFilePath))
            {
                await SaveAsProject();
                return;
            }

            // Сохраняем в существующий файл
            if (_projectService.CurrentProject != null)
            {
                await _projectService.SaveAsync(
                    _projectService.CurrentProject,
                    _projectService.CurrentFilePath!
                );
            }
        }

        /// <summary>Сохранить проект как... (выбор нового пути)</summary>
        private async System.Threading.Tasks.Task SaveAsProject()
        {
            var path = await _dialogService.SaveFileAsync();
            if (path != null && _projectService.CurrentProject != null)
            {
                var success = await _projectService.SaveAsync(_projectService.CurrentProject, path);
                if (success)
                {
                    Title = $"Writersword - {_projectService.CurrentProject.Title}";
                    _settingsService.LastOpenedProject = path;
                }
            }
        }

        /// <summary>Выход из приложения</summary>
        private void Exit()
        {
            System.Environment.Exit(0);
        }

        //// <summary>Загрузить проект при старте приложения</summary>
        public async void LoadProject(string filePath)
        {
            var project = await _projectService.LoadAsync(filePath);
            if (project != null)
            {
                Title = $"Writersword - {project.Title}";

                // ВАЖНО: вызываем RefreshAfterProjectLoad для загрузки вкладок
                RefreshAfterProjectLoad();
            }
        }

        /// <summary>Обновить UI после загрузки проекта</summary>
        public void RefreshAfterProjectLoad()
        {
            if (_projectService.CurrentProject != null)
            {
                // Обновляем заголовок окна
                Title = $"Writersword - {_projectService.CurrentProject.Title}";

                // Загружаем вкладки документов
                LoadDocumentsFromProject();

                // Показываем текстовый редактор
                ShowTextEditor();
            }
        }


        /// <summary>Закрыть вкладку</summary>
        public void CloseTab(DocumentTabViewModel tab)
        {
            OpenTabs.Remove(tab);

            // Если были другие вкладки - активируем первую
            if (OpenTabs.Count > 0)
            {
                ActiveTab = OpenTabs[0];
            }
            else
            {
                // Последняя вкладка закрыта - показываем Welcome
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

        /// <summary>Загрузить вкладки из проекта</summary>
        private void LoadDocumentsFromProject()
        {
            if (_projectService.CurrentProject == null) return;

            OpenTabs.Clear();

            foreach (var doc in _projectService.CurrentProject.Documents)
            {
                // Передаём метод закрытия при создании
                var tabVM = new DocumentTabViewModel(doc, CloseTab);

                OpenTabs.Add(tabVM);

                // Активируем первую вкладку
                if (doc.IsActive || ActiveTab == null)
                {
                    ActiveTab = tabVM;
                }
            }

            // Если нет вкладок - создаём первую
            if (OpenTabs.Count == 0)
            {
                var newDoc = new Writersword.Core.Models.Project.DocumentTab
                {
                    Title = "Document 1",
                    IsActive = true
                };

                _projectService.CurrentProject.Documents.Add(newDoc);

                var tabVM = new DocumentTabViewModel(newDoc, CloseTab);
                OpenTabs.Add(tabVM);
                ActiveTab = tabVM;
            }
        }
    }
}