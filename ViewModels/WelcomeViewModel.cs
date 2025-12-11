using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.Reactive;
using Writersword.Core.Enums;
using Writersword.Core.Models.Project;
using Writersword.Services;
using Writersword.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Writersword.ViewModels
{
    /// <summary>
    /// ViewModel экрана приветствия
    /// Выбор типа нового проекта или открытие существующего
    /// </summary>
    public class WelcomeViewModel : ViewModelBase
    {
        private readonly IDialogService _dialogService;
        private readonly ISettingsService _settingsService;
        private readonly IProjectService _projectService;

        private ProjectType _selectedProjectType = ProjectType.Novel;
        private bool _canClose;

        /// <summary>Выбранный тип проекта</summary>
        public ProjectType SelectedProjectType
        {
            get => _selectedProjectType;
            set => this.RaiseAndSetIfChanged(ref _selectedProjectType, value);
        }

        /// <summary>Можно ли закрыть окно (есть ли открытый проект с вкладками)</summary>
        public bool CanClose
        {
            get => _canClose;
            set => this.RaiseAndSetIfChanged(ref _canClose, value);
        }

        /// <summary>Список недавних проектов</summary>
        public ObservableCollection<RecentProject> RecentProjects { get; }

        /// <summary>Команда создания нового проекта</summary>
        public ReactiveCommand<Unit, Unit> NewProjectCommand { get; }

        /// <summary>Команда открытия существующего проекта</summary>
        public ReactiveCommand<Unit, Unit> OpenProjectCommand { get; }

        /// <summary>Команда открытия недавнего проекта</summary>
        public ReactiveCommand<RecentProject, Unit> OpenRecentCommand { get; }

        /// <summary>Событие: проект выбран, нужно закрыть окно</summary>
        public event Action? ProjectSelected;

        public WelcomeViewModel(
            IDialogService dialogService,
            ISettingsService settingsService,
            IProjectService projectService)
        {
            _dialogService = dialogService;
            _settingsService = settingsService;
            _projectService = projectService;

            _settingsService.Load();

            // CanClose = true только если есть проект И есть хотя бы одна вкладка
            _canClose = _projectService.CurrentProject != null &&
                        _projectService.CurrentProject.Documents.Count > 0;

            // Создаём команды
            NewProjectCommand = ReactiveCommand.CreateFromTask(
                CreateNewProject,
                outputScheduler: RxApp.MainThreadScheduler
            );

            OpenProjectCommand = ReactiveCommand.CreateFromTask(
                OpenExistingProject,
                outputScheduler: RxApp.MainThreadScheduler
            );

            OpenRecentCommand = ReactiveCommand.Create<RecentProject>(
                OpenRecentProject,
                outputScheduler: RxApp.MainThreadScheduler
            );

            RecentProjects = new ObservableCollection<RecentProject>();
            LoadRecentProjects();
        }

        /// <summary>Создать новый проект</summary>
        private async System.Threading.Tasks.Task CreateNewProject()
        {
            // Спрашиваем где сохранить
            var savePath = await _dialogService.SaveFileAsync();

            if (string.IsNullOrEmpty(savePath))
            {
                return; // Отменено
            }

            // Имя проекта = имя файла
            var projectName = System.IO.Path.GetFileNameWithoutExtension(savePath);

            // Если это ПЕРВЫЙ проект (нет текущего проекта)
            if (_projectService.CurrentProject == null)
            {
                // Создаём новый проект
                var project = _projectService.CreateNew(projectName, SelectedProjectType);

                // Сохраняем
                var success = await _projectService.SaveAsync(project, savePath);

                if (success)
                {
                    _settingsService.LastOpenedProject = savePath;
                }
            }
            else
            {
                // Проект уже существует - добавляем новую вкладку через MainWindowViewModel
                var mainViewModel = App.Services.GetRequiredService<MainWindowViewModel>();
                mainViewModel?.AddNewTab(projectName, "", savePath);
            }

            // Закрываем окно
            ProjectSelected?.Invoke();
        }

        /// <summary>Открыть существующий проект</summary>
        private async System.Threading.Tasks.Task OpenExistingProject()
        {
            var path = await _dialogService.OpenFileAsync();
            if (!string.IsNullOrEmpty(path))
            {
                var fileName = System.IO.Path.GetFileNameWithoutExtension(path);

                // Если это ПЕРВЫЙ проект (нет текущего проекта)
                if (_projectService.CurrentProject == null)
                {
                    var project = await _projectService.LoadAsync(path);
                    if (project != null)
                    {
                        _settingsService.LastOpenedProject = path;
                    }
                }
                else
                {
                    // Проект уже существует - добавляем новую вкладку с содержимым файла
                    var fileContent = System.IO.File.Exists(path)
                        ? await System.IO.File.ReadAllTextAsync(path)
                        : "";

                    var mainViewModel = App.Services.GetRequiredService<MainWindowViewModel>();
                    mainViewModel?.AddNewTab(fileName, fileContent, path);
                }

                // Закрываем окно
                ProjectSelected?.Invoke();
            }
        }

        /// <summary>Открыть недавний проект</summary>
        private async void OpenRecentProject(RecentProject recent)
        {
            var fileName = System.IO.Path.GetFileNameWithoutExtension(recent.Path);

            // Если это ПЕРВЫЙ проект (нет текущего проекта)
            if (_projectService.CurrentProject == null)
            {
                var project = await _projectService.LoadAsync(recent.Path);
                if (project != null)
                {
                    _settingsService.LastOpenedProject = recent.Path;
                }
            }
            else
            {
                // Проект уже существует - добавляем новую вкладку
                var fileContent = System.IO.File.Exists(recent.Path)
                    ? await System.IO.File.ReadAllTextAsync(recent.Path)
                    : "";

                var mainViewModel = App.Services.GetRequiredService<MainWindowViewModel>();
                mainViewModel?.AddNewTab(fileName, fileContent, recent.Path);
            }

            // Закрываем окно
            ProjectSelected?.Invoke();
        }

        /// <summary>Загрузить список недавних проектов</summary>
        private void LoadRecentProjects()
        {
            // TODO: Загрузить из файла
            RecentProjects.Clear();
        }
    }
}