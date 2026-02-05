using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
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
        private readonly ILogger<WelcomeViewModel> _logger;
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
            _logger = App.Services.GetService<ILogger<WelcomeViewModel>>()!;
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
        private async Task CreateNewProject()
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
                _logger.LogDebug("Project already open: {Path}", savePath);
                tabCollection.ActiveTab = existingTab;
                ProjectSelected?.Invoke();
                return;
            }

            // Создаём новый проект
            var projectName = Path.GetFileNameWithoutExtension(savePath);
            var project = _projectService.CreateNew(projectName, SelectedProjectType);

            // Сохраняем его
            await _projectService.SaveAsync(project, savePath);

            // Создаём вкладку
            var tabVM = new DocumentTabViewModel(project, savePath);

            // Регистрируем хранилище
            _projectWorkflow.RegisterStorage(savePath, tabVM);

            // Добавляем вкладку в коллекцию и делаем её активной
            tabCollection.Add(tabVM);
            tabCollection.ActiveTab = tabVM;

            mainViewModel.InitializeWorkModesForTab(tabVM);

            // Добавляем в недавние
            _settingsService.AddRecentProject(savePath);

            LoadRecentProjects();

            _logger.LogInformation("New project created: {Name} ({Type})", projectName, SelectedProjectType);

            ProjectSelected?.Invoke();
        }

        /// <summary>Открыть существующий проект</summary>
        private async Task OpenExistingProject()
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
                _logger.LogDebug("Project already open: {Path}", path);
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

                _logger.LogInformation("Opened existing project: {Path}", path);

                LoadRecentProjects();
            }

            ProjectSelected?.Invoke();
        }

        /// <summary>Открыть недавний проект</summary>
        private async void OpenRecentProject(RecentProject recent)
        {
            await OpenRecentProjectDirect(recent);
        }

        /// <summary>Открыть недавний проект напрямую</summary>
        public async Task OpenRecentProjectDirect(RecentProject recent)
        {
            if (_isProcessing)
            {
                _logger.LogDebug("Already processing project open");
                return;
            }

            _isProcessing = true;

            try
            {
                _logger.LogDebug("Opening recent project: {Name}", recent.Name);

                // Проверяем существует ли файл
                if (!System.IO.File.Exists(recent.Path))
                {
                    _logger.LogWarning("Recent project file not found: {Path}", recent.Path);

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
                    _logger.LogDebug("Recent project already open");
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

                    _logger.LogInformation("Opened recent project: {Name}", recent.Name);

                    LoadRecentProjects(); // Обновляем список в UI

                    ProjectSelected?.Invoke();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to open recent project: {Name}", recent.Name);

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

            _logger.LogDebug("Loaded {Count} recent projects", RecentProjects.Count);
        }
    }
}