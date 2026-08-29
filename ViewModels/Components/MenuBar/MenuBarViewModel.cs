using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using Writersword.Core.Interfaces.Services;
using Writersword.Core.Interfaces.Services.Storage;
using Writersword.Core.Interfaces.Services.UI;
using Writersword.Core.Interfaces.WorkFlows;
using Writersword.Core.Interfaces.WorkModes;
using Writersword.ProjectTypes.Common;
using Writersword.ViewModels.Components;
using Writersword.WorkModes.Common;

namespace Writersword.ViewModels.Components.MenuBar
{
    public partial class MenuBarViewModel : ViewModelBase
    {
        private readonly ILogger<MenuBarViewModel> _logger;
        private readonly IProjectWorkflow _projectWorkflow;
        private readonly ISettingsService _settingsService;
        private readonly ITabCollection _tabCollection;
        private readonly IWorkModeConfigurationService _workModeConfigService;
        private readonly INotificationService _notificationService;
        private readonly IDialogService _dialogService;
        private readonly IWorkspaceConfigService _workspaceConfigService;
        private readonly ProjectTypeRegistry _projectTypeRegistry;

        private Func<MainWindowViewModel>? _mainViewModelProvider;
        private Func<DocumentTabViewModel?>? _getActiveTab;

        private bool _hasActiveTab;
        private bool _isFullscreen;

        // ── Коллекции ──────────────────────────────────────────────────────────

        public ObservableCollection<RecentProjectItem> RecentProjects { get; } = new();

        // ── Свойства ───────────────────────────────────────────────────────────

        public bool HasActiveTab
        {
            get => _hasActiveTab;
            private set => this.RaiseAndSetIfChanged(ref _hasActiveTab, value);
        }

        public bool IsFullscreen
        {
            get => _isFullscreen;
            private set => this.RaiseAndSetIfChanged(ref _isFullscreen, value);
        }

        // ── Команды: File ──────────────────────────────────────────────────────

        public ReactiveCommand<Unit, Unit> NewProjectCommand { get; }
        public ReactiveCommand<Unit, Unit> OpenProjectCommand { get; }
        public ReactiveCommand<string, Unit> OpenRecentProjectCommand { get; }
        public ReactiveCommand<Unit, Unit> SaveProjectCommand { get; }
        public ReactiveCommand<Unit, Unit> SaveAsProjectCommand { get; }
        public ReactiveCommand<Unit, Unit> SaveAllProjectsCommand { get; }
        public ReactiveCommand<Unit, Unit> CloseTabCommand { get; }
        public ReactiveCommand<Unit, Unit> CloseAllTabsCommand { get; }
        public ReactiveCommand<Unit, Unit> CloseOtherTabsCommand { get; }
        public ReactiveCommand<Unit, Unit> OpenSettingsCommand { get; }
        public ReactiveCommand<Unit, Unit> ExitCommand { get; }

        // ── Команды: Tools ─────────────────────────────────────────────────────

        public ReactiveCommand<Unit, Unit> CompactProjectCommand { get; }

        /// <summary>
        /// Проверить, что весь проект лежит внутри своего файла, и уложить в
        /// него то, что осталось снаружи. Нужна перед передачей проекта другому
        /// человеку: у него нет ни вашей библиотеки, ни ваших общих папок.
        /// </summary>
        public ReactiveCommand<Unit, Unit> PrepareForSharingCommand { get; }
        public ReactiveCommand<Unit, Unit> OpenSyncSettingsCommand { get; }
        public ReactiveCommand<Unit, Unit> PushToStorageCommand { get; }
        public ReactiveCommand<Unit, Unit> PullFromStorageCommand { get; }

        // ── Команды: View ──────────────────────────────────────────────────────

        public ReactiveCommand<Unit, Unit> ToggleFullscreenCommand { get; }

        // ── Команды: Workspace ─────────────────────────────────────────────────

        public ReactiveCommand<Unit, Unit> SaveWorkspaceGlobalCommand { get; }
        public ReactiveCommand<Unit, Unit> ResetWorkspaceToGlobalCommand { get; }
        public ReactiveCommand<Unit, Unit> ResetWorkspaceToDefaultCommand { get; }

        // ── Конструктор ────────────────────────────────────────────────────────

        public MenuBarViewModel(
            IProjectWorkflow projectWorkflow,
            ISettingsService settingsService,
            ITabCollection tabCollection,
            IWorkModeConfigurationService workModeConfigService,
            INotificationService notificationService,
            IDialogService dialogService,
            IWorkspaceConfigService workspaceConfigService,
            ProjectTypeRegistry projectTypeRegistry)
        {
            _logger = App.Services.GetService<ILogger<MenuBarViewModel>>()!;
            _projectWorkflow = projectWorkflow;
            _settingsService = settingsService;
            _tabCollection = tabCollection;
            _workModeConfigService = workModeConfigService;
            _notificationService = notificationService;
            _dialogService = dialogService;
            _workspaceConfigService = workspaceConfigService;
            _projectTypeRegistry = projectTypeRegistry;

            NewProjectCommand = ReactiveCommand.Create(NewProject);
            OpenProjectCommand = ReactiveCommand.CreateFromTask(OpenProject);
            OpenRecentProjectCommand = ReactiveCommand.CreateFromTask<string>(OpenRecentProject);
            SaveProjectCommand = ReactiveCommand.CreateFromTask(SaveProject);
            SaveAsProjectCommand = ReactiveCommand.CreateFromTask(SaveAsProject);
            SaveAllProjectsCommand = ReactiveCommand.CreateFromTask(SaveAllProjects);
            CloseTabCommand = ReactiveCommand.CreateFromTask(CloseTab);
            CloseAllTabsCommand = ReactiveCommand.CreateFromTask(CloseAllTabs);
            CloseOtherTabsCommand = ReactiveCommand.CreateFromTask(CloseOtherTabs);
            OpenSettingsCommand = ReactiveCommand.CreateFromTask(OpenSettings);
            ExitCommand = ReactiveCommand.Create(Exit);
            CompactProjectCommand = ReactiveCommand.CreateFromTask(CompactProject);
            PrepareForSharingCommand = ReactiveCommand.CreateFromTask(PrepareProjectForSharing);
            OpenSyncSettingsCommand = ReactiveCommand.CreateFromTask(OpenSyncSettings);
            PushToStorageCommand = ReactiveCommand.CreateFromTask(PushToStorage);
            PullFromStorageCommand = ReactiveCommand.CreateFromTask(PullFromStorage);
            ToggleFullscreenCommand = ReactiveCommand.Create(ToggleFullscreen);
            SaveWorkspaceGlobalCommand = ReactiveCommand.CreateFromTask(SaveWorkspaceGlobal);
            ResetWorkspaceToGlobalCommand = ReactiveCommand.CreateFromTask(ResetWorkspaceToGlobal);
            ResetWorkspaceToDefaultCommand = ReactiveCommand.CreateFromTask(ResetWorkspaceToDefault);

#if DEBUG
            OpenSettingsCommand.CanExecute.Subscribe(can =>
            {
                System.Diagnostics.Debug.WriteLine($"[MENU] OpenSettingsCommand CanExecute: {can}");
                _logger.LogDebug("OpenSettingsCommand CanExecute: {Can}", can);
            });
            OpenSettingsCommand.Subscribe(_ =>
                System.Diagnostics.Debug.WriteLine("[MENU] OpenSettingsCommand EXECUTED"));
            SaveProjectCommand.CanExecute.Subscribe(can =>
                System.Diagnostics.Debug.WriteLine($"[MENU] SaveProjectCommand CanExecute: {can}"));
            CloseTabCommand.CanExecute.Subscribe(can =>
                System.Diagnostics.Debug.WriteLine($"[MENU] CloseTabCommand CanExecute: {can}"));
#endif

            LoadRecentProjects();

            _logger.LogDebug("Initialized");
        }

        // ── Вспомогательные ───────────────────────────────────────────────────

        private void LoadRecentProjects()
        {
            RecentProjects.Clear();

            foreach (var recent in _settingsService.RecentProjects.Take(10))
            {
                if (File.Exists(recent.Path))
                    RecentProjects.Add(new RecentProjectItem { FilePath = recent.Path, ProjectName = recent.Name });
            }

            _logger.LogDebug("Loaded {Count} recent projects", RecentProjects.Count);
        }

        public void SetActiveTabProvider(Func<DocumentTabViewModel?> getActiveTab)
            => _getActiveTab = getActiveTab;

        public void SetMainViewModelProvider(Func<MainWindowViewModel> provider)
        {
            _mainViewModelProvider = provider;
            _logger.LogDebug("MainViewModel provider set");
        }

        public void UpdateHasActiveTab()
            => HasActiveTab = _getActiveTab?.Invoke() != null;
    }

    /// <summary>Элемент списка недавних проектов</summary>
    public class RecentProjectItem
    {
        public string FilePath { get; set; } = "";
        public string ProjectName { get; set; } = "";
    }
}