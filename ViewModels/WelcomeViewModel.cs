using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using Writersword.Core.Models.Project;
using Writersword.Resources.Localization;
using Writersword.Src.Core.Interfaces.Services.Storage;
using Writersword.Src.Core.Interfaces.Services.UI;
using Writersword.Src.Core.Interfaces.WorkFlows;
using Writersword.Views;

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
        private readonly IProjectWorkflow _projectWorkflow;

        private string _selectedProjectType = "Novel";
        private bool _isProcessing = false;

        /// <summary>Выбранный тип проекта</summary>
        public string SelectedProjectType
        {
            get => _selectedProjectType;
            set => this.RaiseAndSetIfChanged(ref _selectedProjectType, value);
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
            IProjectService projectService,
            IProjectWorkflow projectWorkflow)
        {
            _dialogService = dialogService;
            _settingsService = settingsService;
            _projectService = projectService;
            _projectWorkflow = projectWorkflow;

            _settingsService.Load();

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
            var savePath = await _dialogService.SaveFileAsync();
            if (string.IsNullOrEmpty(savePath))
                return;

            var mainViewModel = App.Services.GetRequiredService<MainWindowViewModel>();
            var tabCollection = App.Services.GetRequiredService<ITabCollection>();

            // Проверяем не открыт ли уже проект с таким путём
            var existingTab = tabCollection.FindByPath(savePath);
            if (existingTab != null)
            {
                Console.WriteLine($"[CreateNewProject] Project already open: {savePath}");
                tabCollection.ActiveTab = existingTab;
                ProjectSelected?.Invoke();
                return;
            }

            // Создаём новый проект
            var projectName = System.IO.Path.GetFileNameWithoutExtension(savePath);
            var project = _projectService.CreateNew(projectName, SelectedProjectType);

            // Сохраняем проект
            await _projectService.SaveAsync(project, savePath);

            // Создаём вкладку
            var tabVM = new DocumentTabViewModel(project, savePath);
            tabCollection.Add(tabVM);
            tabCollection.ActiveTab = tabVM;

            // Инициализируем WorkModes
            mainViewModel.InitializeWorkModesForTab(tabVM);

            // Добавляем в недавние
            _settingsService.AddRecentProject(savePath);

            ProjectSelected?.Invoke();
        }

        /// <summary>Открыть существующий проект</summary>
        private async System.Threading.Tasks.Task OpenExistingProject()
        {
            var path = await _dialogService.OpenFileAsync();
            if (string.IsNullOrEmpty(path))
                return;

            var tabCollection = App.Services.GetRequiredService<ITabCollection>();
            var mainViewModel = App.Services.GetRequiredService<MainWindowViewModel>();

            // Проверяем не открыт ли уже
            var existingTab = tabCollection.FindByPath(path);
            if (existingTab != null)
            {
                Console.WriteLine($"[OpenExistingProject] Already open: {path}");
                tabCollection.ActiveTab = existingTab;
                ProjectSelected?.Invoke();
                return;
            }

            // Открываем через ProjectWorkflow
            var tab = await _projectWorkflow.OpenDocumentAsync(path);
            if (tab != null)
            {
                tabCollection.Add(tab);
                tabCollection.ActiveTab = tab;
                _settingsService.AddRecentProject(path);
            }

            ProjectSelected?.Invoke();
        }

        /// <summary>Открыть недавний проект</summary>
        private async void OpenRecentProject(RecentProject recent)
        {
            await OpenRecentProjectDirect(recent);
        }

        /// <summary>Открыть недавний проект напрямую</summary>
        public async System.Threading.Tasks.Task OpenRecentProjectDirect(RecentProject recent)
        {
            if (_isProcessing)
            {
                Console.WriteLine($"[OpenRecentProjectDirect] Already processing");
                return;
            }

            _isProcessing = true;

            try
            {
                Console.WriteLine($"[OpenRecentProjectDirect] Opening: {recent.Name}");

                // Проверяем существует ли файл
                if (!System.IO.File.Exists(recent.Path))
                {
                    Console.WriteLine($"[OpenRecentProjectDirect] File not found: {recent.Path}");

                    await _dialogService.ShowMessageAsync(
                        Strings.MessageBox_Error_ProjectNotFound_Title,
                        $"{Strings.MessageBox_Error_ProjectNotFound_Message}\n\n{recent.Path}",
                        MessageBoxType.Error,
                        MessageBoxButtons.OK
                    );

                    _settingsService.RecentProjects.Remove(recent);
                    RecentProjects.Remove(recent);
                    _settingsService.Save();
                    return;
                }

                var tabCollection = App.Services.GetRequiredService<ITabCollection>();
                var mainViewModel = App.Services.GetRequiredService<MainWindowViewModel>();

                // Проверяем не открыт ли уже
                var existingTab = tabCollection.FindByPath(recent.Path);
                if (existingTab != null)
                {
                    Console.WriteLine($"[OpenRecentProjectDirect] Already open");
                    tabCollection.ActiveTab = existingTab;
                    ProjectSelected?.Invoke();
                    return;
                }

                // Открываем через ProjectWorkflow
                var tab = await _projectWorkflow.OpenDocumentAsync(recent.Path);
                if (tab != null)
                {
                    tabCollection.Add(tab);
                    tabCollection.ActiveTab = tab;
                    _settingsService.AddRecentProject(recent.Path);
                    ProjectSelected?.Invoke();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OpenRecentProjectDirect] ERROR: {ex.Message}");

                await _dialogService.ShowMessageAsync(
                    Strings.MessageBox_Error_ProjectLoadFailed_Title,
                    $"{Strings.MessageBox_Error_ProjectLoadFailed_Message}\n\n{recent.Path}",
                    MessageBoxType.Error,
                    MessageBoxButtons.OK
                );
            }
            finally
            {
                _isProcessing = false;
            }
        }

        /// <summary>Загрузить список недавних проектов</summary>
        private void LoadRecentProjects()
        {
            RecentProjects.Clear();

            foreach (var recent in _settingsService.RecentProjects)
            {
                RecentProjects.Add(recent);
            }

            Console.WriteLine($"Loaded {RecentProjects.Count} recent projects");
        }
    }
}