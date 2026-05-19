using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.Avalonia;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Writersword.Core.Enums;
using Writersword.Core.Interfaces.Services.Storage;
using Writersword.Core.Interfaces.Services.UI;
using Writersword.Core.Interfaces.WorkFlows;
using Writersword.Core.Models.Project;
using Writersword.ProjectTypes.Common;
using Writersword.Resources.Localization;
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

        /// <summary>Список доступных типов проектов</summary>
        public ObservableCollection<ProjectTypeItem> ProjectTypes { get; } = new();

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
                outputScheduler: AvaloniaScheduler.Instance
            );

            OpenProjectCommand = ReactiveCommand.CreateFromTask(
                OpenExistingProject,
                outputScheduler: AvaloniaScheduler.Instance
            );

            OpenRecentCommand = ReactiveCommand.Create<RecentProject>(
                OpenRecentProject,
                outputScheduler: AvaloniaScheduler.Instance
            );

            RecentProjects = new ObservableCollection<RecentProject>();

            LoadProjectTypes();
            LoadRecentProjects();
        }

        /// <summary>Загрузить типы проектов из реестра</summary>
        private void LoadProjectTypes()
        {
            var registry = App.Services.GetRequiredService<ProjectTypeRegistry>();

            foreach (var type in registry.GetAll())
            {
                var item = new ProjectTypeItem
                {
                    Id = type.Id,
                    DisplayName = type.DisplayName,
                    Icon = type.Icon,
                    IsSelected = type.Id == _selectedProjectType
                };

                item.WhenAnyValue(x => x.IsSelected)
                    .Subscribe(selected =>
                    {
                        if (selected)
                            SelectedProjectType = item.Id;
                    });

                ProjectTypes.Add(item);
            }

            _logger.LogDebug("Loaded {Count} project types", ProjectTypes.Count);
        }

        /// <summary>Создать новый проект</summary>
        private async Task CreateNewProject()
        {
            var savePath = await _dialogService.SaveFileAsync();
            if (string.IsNullOrEmpty(savePath))
                return;

            var mainViewModel = App.Services.GetRequiredService<MainWindowViewModel>();
            var tabCollection = App.Services.GetRequiredService<ITabCollection>();

            var existingTab = tabCollection.FindByPath(savePath);
            if (existingTab != null)
            {
                _logger.LogDebug("Project already open: {Path}", savePath);
                tabCollection.ActiveTab = existingTab;
                ProjectSelected?.Invoke();
                return;
            }

            var projectName = Path.GetFileNameWithoutExtension(savePath);
            var project = _projectService.CreateNew(projectName, SelectedProjectType);

            await _projectService.SaveAsync(project, savePath);

            var tabVM = new DocumentTabViewModel(project, savePath);

            _projectWorkflow.RegisterStorage(savePath, tabVM);

            tabCollection.Add(tabVM);
            tabCollection.ActiveTab = tabVM;

            mainViewModel.InitializeWorkModesForTab(tabVM);

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

            var existingTab = tabCollection.FindByPath(path);
            if (existingTab != null)
            {
                _logger.LogDebug("Project already open: {Path}", path);
                tabCollection.ActiveTab = existingTab;
                ProjectSelected?.Invoke();
                return;
            }

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

                var existingTab = tabCollection.FindByPath(recent.Path);
                if (existingTab != null)
                {
                    _logger.LogDebug("Recent project already open");
                    tabCollection.ActiveTab = existingTab;
                    ProjectSelected?.Invoke();
                    return;
                }

                var tab = await _projectWorkflow.OpenDocumentAsync(recent.Path);
                if (tab != null)
                {
                    tabCollection.Add(tab);
                    tabCollection.ActiveTab = tab;
                    _settingsService.AddRecentProject(recent.Path);

                    _logger.LogInformation("Opened recent project: {Name}", recent.Name);

                    LoadRecentProjects();

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

    /// <summary>
    /// Элемент списка типов проектов для WelcomeView
    /// </summary>
    public class ProjectTypeItem : ReactiveObject
    {
        private bool _isSelected;

        /// <summary>Уникальный идентификатор типа проекта</summary>
        public string Id { get; set; } = "";

        /// <summary>Локализованное название</summary>
        public string DisplayName { get; set; } = "";

        /// <summary>Иконка</summary>
        public string Icon { get; set; } = "";

        /// <summary>Выбран ли этот тип</summary>
        public bool IsSelected
        {
            get => _isSelected;
            set => this.RaiseAndSetIfChanged(ref _isSelected, value);
        }
    }
}