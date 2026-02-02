using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Writersword.Core.Models.Settings;
using Writersword.Src.Core.Interfaces.Services;
using Writersword.Src.Core.Interfaces.Services.Storage;
using Writersword.Src.Core.Interfaces.Services.UI;
using Writersword.Src.Core.Interfaces.WorkFlows;
using Writersword.Src.Core.Interfaces.WorkModes;
using Writersword.Src.ProjectTypes.Common;
using Writersword.Views;

namespace Writersword.ViewModels.Components
{
    /// <summary>
    /// ViewModel для главного меню приложения (File, Edit, View)
    /// Отвечает за команды работы с проектами
    /// </summary>
    public class MenuBarViewModel : ViewModelBase
    {
        private readonly IProjectWorkflow _projectWorkflow;
        private readonly ISettingsService _settingsService;
        private readonly ITabCollection _tabCollection;
        private readonly IWorkModeConfigurationService _workModeConfigService;
        private readonly INotificationService _notificationService;
        private readonly IDialogService _dialogService;
        private readonly IWorkspaceConfigService _workspaceConfigService;
        private readonly ProjectTypeRegistry _projectTypeRegistry;

        // Провайдер для доступа к MainWindowViewModel (для меню View)
        private Func<MainWindowViewModel>? _mainViewModelProvider;

        /// <summary>Список недавних проектов</summary>
        public ObservableCollection<RecentProjectItem> RecentProjects { get; } = new();

        /// <summary>Список всех доступных WorkModes (для меню View)</summary>
        public ObservableCollection<MainWindowViewModel.WorkModeMenuItem> AllWorkModes
        {
            get
            {
                var mainVM = _mainViewModelProvider?.Invoke();
                return mainVM?.AllWorkModes ?? new ObservableCollection<MainWindowViewModel.WorkModeMenuItem>();
            }
        }

        /// <summary>Список всех доступных модулей (для меню View)</summary>
        public ObservableCollection<MainWindowViewModel.ModuleMenuItem> AllModules
        {
            get
            {
                var mainVM = _mainViewModelProvider?.Invoke();
                return mainVM?.AllModules ?? new ObservableCollection<MainWindowViewModel.ModuleMenuItem>();
            }
        }

        /// <summary>Команда переключения WorkMode</summary>
        public ReactiveCommand<string, Unit> ToggleWorkModeCommand
        {
            get
            {
                var mainVM = _mainViewModelProvider?.Invoke();
                return mainVM?.ToggleWorkModeCommand ?? ReactiveCommand.Create<string>(_ => { });
            }
        }

        /// <summary>Команда переключения модуля</summary>
        public ReactiveCommand<string, Unit> ToggleModuleCommand
        {
            get
            {
                var mainVM = _mainViewModelProvider?.Invoke();
                return mainVM?.ToggleModuleCommand ?? ReactiveCommand.Create<string>(_ => { });
            }
        }

        /// <summary>Команда создания нового проекта (Ctrl+N)</summary>
        public ReactiveCommand<Unit, Unit> NewProjectCommand { get; }

        /// <summary>Команда открытия проекта (Ctrl+O)</summary>
        public ReactiveCommand<Unit, Unit> OpenProjectCommand { get; }

        /// <summary>Команда открытия недавнего проекта</summary>
        public ReactiveCommand<string, Unit> OpenRecentProjectCommand { get; }

        /// <summary>Команда сохранения проекта (Ctrl+S)</summary>
        public ReactiveCommand<Unit, Unit> SaveProjectCommand { get; }

        /// <summary>Команда "Сохранить как..." (Ctrl+Shift+S)</summary>
        public ReactiveCommand<Unit, Unit> SaveAsProjectCommand { get; }

        /// <summary>Команда выхода из приложения</summary>
        public ReactiveCommand<Unit, Unit> ExitCommand { get; }

        /// <summary>Команда сохранения конфигурации глобально (для типа проекта)</summary>
        public ReactiveCommand<Unit, Unit> SaveWorkspaceGlobalCommand { get; }

        /// <summary>Команда сброса до глобальной конфигурации</summary>
        public ReactiveCommand<Unit, Unit> ResetWorkspaceToGlobalCommand { get; }

        /// <summary>Команда сброса до дефолтной конфигурации</summary>
        public ReactiveCommand<Unit, Unit> ResetWorkspaceToDefaultCommand { get; }

        /// <summary>Есть ли активная вкладка (для IsEnabled кнопок)</summary>
        //public bool HasActiveTab => _getActiveTab?.Invoke() != null;

        private bool _hasActiveTab;

        /// <summary>Есть ли активная вкладка (для IsEnabled кнопок)</summary>
        public bool HasActiveTab
        {
            get => _hasActiveTab;
            private set => this.RaiseAndSetIfChanged(ref _hasActiveTab, value);
        }

        /// <summary>Обновить состояние HasActiveTab</summary>
        public void UpdateHasActiveTab()
        {
            HasActiveTab = _getActiveTab?.Invoke() != null;
        }

        /// <summary>Функция для получения активной вкладки (передаётся извне)</summary>
        private Func<DocumentTabViewModel?>? _getActiveTab;

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
            _projectWorkflow = projectWorkflow;
            _settingsService = settingsService;
            _tabCollection = tabCollection;
            _workModeConfigService = workModeConfigService;
            _notificationService = notificationService;
            _dialogService = dialogService;
            _workspaceConfigService = workspaceConfigService;
            _projectTypeRegistry = projectTypeRegistry;

            // Создаём команды
            NewProjectCommand = ReactiveCommand.Create(NewProject);
            OpenProjectCommand = ReactiveCommand.CreateFromTask(OpenProject);
            OpenRecentProjectCommand = ReactiveCommand.CreateFromTask<string>(OpenRecentProject);
            SaveProjectCommand = ReactiveCommand.CreateFromTask(SaveProject);
            SaveAsProjectCommand = ReactiveCommand.CreateFromTask(SaveAsProject);
            ExitCommand = ReactiveCommand.Create(Exit);
            SaveWorkspaceGlobalCommand = ReactiveCommand.CreateFromTask(SaveWorkspaceGlobal);
            ResetWorkspaceToGlobalCommand = ReactiveCommand.CreateFromTask(ResetWorkspaceToGlobal);
            ResetWorkspaceToDefaultCommand = ReactiveCommand.CreateFromTask(ResetWorkspaceToDefault);

            // Загружаем список недавних проектов
            LoadRecentProjects();

            Console.WriteLine("[MenuBarViewModel] Initialized");
        }

        /// <summary>
        /// Загрузить список недавних проектов из настроек
        /// </summary>
        private void LoadRecentProjects()
        {
            RecentProjects.Clear();

            var recentProjects = _settingsService.RecentProjects;

            foreach (var recent in recentProjects.Take(10)) // Максимум 10 проектов
            {
                if (File.Exists(recent.Path))
                {
                    RecentProjects.Add(new RecentProjectItem
                    {
                        FilePath = recent.Path,
                        ProjectName = recent.Name // Используем имя из настроек!
                    });
                }
            }

            Console.WriteLine($"[MenuBarViewModel] Loaded {RecentProjects.Count} recent projects");
        }

        /// <summary>
        /// Установить функцию получения активной вкладки
        /// Вызывается из MainWindowViewModel после создания
        /// </summary>
        public void SetActiveTabProvider(Func<DocumentTabViewModel?> getActiveTab)
        {
            _getActiveTab = getActiveTab;
        }

        /// <summary>Создать новый проект (показывает Welcome окно)</summary>
        private async void NewProject()
        {
            Console.WriteLine("[MenuBarViewModel] NewProject clicked");

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow != null)
            {
                await App.ShowWelcomeScreen(desktop.MainWindow);
            }
        }

        /// <summary>Открыть существующий проект (показывает Welcome окно)</summary>
        private async Task OpenProject()
        {
            Console.WriteLine("[MenuBarViewModel] OpenProject clicked");

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow != null)
            {
                await App.ShowWelcomeScreen(desktop.MainWindow);
            }
        }

        /// <summary>Открыть недавний проект</summary>
        private async Task OpenRecentProject(string filePath)
        {
            Console.WriteLine($"[MenuBarViewModel] Opening recent project: {filePath}");

            // Проверяем существует ли файл
            if (!File.Exists(filePath))
            {
                Console.WriteLine($"[MenuBarViewModel] File not found: {filePath}");

                // Удаляем из списка недавних
                var item = RecentProjects.FirstOrDefault(r => r.FilePath == filePath);
                if (item != null)
                {
                    RecentProjects.Remove(item);
                }

                return;
            }

            // Проверяем не открыт ли уже
            var existingTab = _tabCollection.FindByPath(filePath);
            if (existingTab != null)
            {
                Console.WriteLine($"[MenuBarViewModel] Project already open, activating tab");
                _tabCollection.ActiveTab = existingTab;
                return;
            }

            // Открываем проект
            var tab = await _projectWorkflow.OpenDocumentAsync(filePath);
            if (tab != null)
            {
                _tabCollection.Add(tab);
                _tabCollection.ActiveTab = tab;
                _settingsService.AddRecentProject(filePath);

                // Обновляем список
                LoadRecentProjects();
            }
        }

        /// <summary>Сохранить активный проект</summary>
        private async Task SaveProject()
        {
            var activeTab = _getActiveTab?.Invoke();

            if (activeTab == null)
            {
                Console.WriteLine("[MenuBarViewModel] SaveProject: No active tab");
                return;
            }

            Console.WriteLine($"[MenuBarViewModel] SaveProject: {activeTab.Title}");
            await _projectWorkflow.SaveDocumentAsync(activeTab);
        }

        /// <summary>Сохранить активный проект как...</summary>
        private async Task SaveAsProject()
        {
            var activeTab = _getActiveTab?.Invoke();

            if (activeTab == null)
            {
                Console.WriteLine("[MenuBarViewModel] SaveAsProject: No active tab");
                return;
            }

            Console.WriteLine($"[MenuBarViewModel] SaveAsProject: {activeTab.Title}");
            await _projectWorkflow.SaveAsDocumentAsync(activeTab);

            // Обновляем список недавних проектов
            LoadRecentProjects();
        }

        /// <summary>Выход из приложения</summary>
        private void Exit()
        {
            Console.WriteLine("[MenuBarViewModel] Exit clicked");

            // Просто закрываем главное окно - это триггернёт OnClosing
            // OnClosing сам проверит несохранённые изменения в каждой вкладке
            if (Application.Current?.ApplicationLifetime
                is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow != null)
            {
                desktop.MainWindow.Close();
            }
        }

        /// <summary>
        /// Установить провайдер MainWindowViewModel для доступа к AllWorkModes/AllModules
        /// Вызывается из MainWindowViewModel после создания
        /// </summary>
        public void SetMainViewModelProvider(Func<MainWindowViewModel> provider)
        {
            _mainViewModelProvider = provider;
            Console.WriteLine("[MenuBarViewModel] MainViewModel provider set");
        }

        /// <summary>
        /// Сохранить конфигурацию глобально (для всех проектов данного типа)
        /// Применится ко ВСЕМ будущим проектам типа "Novel", "Screenplay" и т.д.
        /// </summary>
        private async Task SaveWorkspaceGlobal()
        {
            var activeTab = _getActiveTab?.Invoke();
            if (activeTab == null)
            {
                Console.WriteLine("[MenuBarViewModel] SaveWorkspaceGlobal: No active tab");
                return;
            }

            try
            {
                var project = activeTab.GetProject();
                var projectTypeObj = _projectTypeRegistry.GetById(project.Type);
                string displayName = projectTypeObj?.DisplayName ?? project.Type;

                // Показываем диалог подтверждения
                var result = await _dialogService.ShowMessageAsync(
                    "Сохранить как глобальные настройки?",
                    $"Текущая конфигурация будет применена ко всем новым проектам типа \"{displayName}\". Предыдущие глобальные настройки будут перезаписаны. Продолжить?",
                    MessageBoxType.Warning,
                    MessageBoxButtons.YesNo
                );

                if (result != MessageBoxResult.Yes)
                {
                    Console.WriteLine("[MenuBarViewModel] Save global cancelled");
                    return;
                }

                // Получаем текущие WorkModes
                var workModeService = App.Services.GetRequiredService<IWorkModeService>();
                var currentWorkModes = workModeService.GetAllWorkModes();

                // Создаём конфигурацию
                var config = new WorkspaceConfig
                {
                    ProjectType = project.Type,
                    Name = $"{project.Type} Configuration",
                    WorkModes = currentWorkModes
                };

                // Сохраняем через SettingsService
                _settingsService.SaveWorkspaceConfig(project.Type, config);

                _notificationService.ShowSuccess($"Конфигурация сохранена для типа {displayName}");
                Console.WriteLine($"[MenuBarViewModel] Workspace saved globally for: {project.Type}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MenuBarViewModel] Error saving global workspace: {ex.Message}");
            }
        }

        /// <summary>
        /// Сбросить конфигурацию до глобальной
        /// Удаляет workspace.json из ZIP и перезагружает глобальную конфигурацию
        /// </summary>
        private async Task ResetWorkspaceToGlobal()
        {
            var activeTab = _getActiveTab?.Invoke();
            if (activeTab == null)
            {
                Console.WriteLine("[MenuBarViewModel] ResetWorkspaceToGlobal: No active tab");
                return;
            }

            try
            {
                // Показываем диалог подтверждения
                var result = await _dialogService.ShowMessageAsync(
                    "Восстановить из глобальных настроек?",
                    "Локальная конфигурация будет удалена. Продолжить?",
                    MessageBoxType.Warning,
                    MessageBoxButtons.YesNo
                );

                if (result != MessageBoxResult.Yes)
                {
                    Console.WriteLine("[MenuBarViewModel] Reset to global cancelled");
                    return;
                }

                var project = activeTab.GetProject();
                var fileStorage = activeTab.Context.FileStorage;

                if (fileStorage == null)
                {
                    Console.WriteLine("[MenuBarViewModel] No FileStorage available");
                    return;
                }

                // ЗАКРЫВАЕМ ВСЕ ФЛОАТ ОКНА 
                var mainVM = _mainViewModelProvider?.Invoke();
                if (mainVM?.DockLayout?.Windows != null)
                {
                    Console.WriteLine($"[MenuBarViewModel] Closing {mainVM.DockLayout.Windows.Count} float windows");

                    foreach (var window in mainVM.DockLayout.Windows.ToList())
                    {
                        if (window.Host is Writersword.Src.Infrastructure.Dock.HostWindow hostWindow)
                        {
                            hostWindow.Exit();
                            Console.WriteLine($"[MenuBarViewModel] Closed float window: {window.Id}");
                        }
                    }

                    mainVM.DockLayout.Windows.Clear();
                }

                // Удаляем workspace.json из ZIP
                _workspaceConfigService.DeleteFromZip(fileStorage);

                // Загружаем глобальную конфигурацию
                var globalWorkModes = _workModeConfigService.LoadConfiguration(project.Type, null);

                // Обновляем WorkModeService
                var workModeService = App.Services.GetRequiredService<IWorkModeService>();
                workModeService.InitializeWorkModes(project.Type, globalWorkModes);

                // Перезагружаем UI
                mainVM?.InitializeWorkModesForTab(activeTab);

                _notificationService.ShowSuccess("Конфигурация восстановлена из глобальных настроек");
                Console.WriteLine("[MenuBarViewModel] Workspace reset to global");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MenuBarViewModel] Error resetting to global: {ex.Message}");
            }
        }

        /// <summary>
        /// Сбросить конфигурацию до дефолтной
        /// Удаляет workspace.json из ZIP и загружает hardcoded дефолт
        /// </summary>
        private async Task ResetWorkspaceToDefault()
        {
            Console.WriteLine("[MenuBarViewModel] ResetWorkspaceToDefault CALLED!");

            var activeTab = _getActiveTab?.Invoke();
            if (activeTab == null)
            {
                Console.WriteLine("[MenuBarViewModel] No active tab");
                return;
            }

            try
            {
                var result = await _dialogService.ShowMessageAsync(
                    "Сбросить до дефолта?",
                    "Все настройки рабочего пространства будут сброшены. Продолжить?",
                    MessageBoxType.Warning,
                    MessageBoxButtons.YesNo
                );

                Console.WriteLine($"[MenuBarViewModel] User choice: {result}");

                if (result != MessageBoxResult.Yes)
                {
                    Console.WriteLine("[MenuBarViewModel] Cancelled");
                    return;
                }

                var project = activeTab.GetProject();
                var fileStorage = activeTab.Context.FileStorage;

                if (fileStorage == null)
                {
                    Console.WriteLine("[MenuBarViewModel] No FileStorage");
                    return;
                }

                // ЗАКРЫВАЕМ ВСЕ ФЛОАТ ОКНА 
                var mainVM = _mainViewModelProvider?.Invoke();
                if (mainVM?.DockLayout?.Windows != null)
                {
                    Console.WriteLine($"[MenuBarViewModel] Closing {mainVM.DockLayout.Windows.Count} float windows");

                    foreach (var window in mainVM.DockLayout.Windows.ToList())
                    {
                        if (window.Host is Writersword.Src.Infrastructure.Dock.HostWindow hostWindow)
                        {
                            hostWindow.Exit();
                            Console.WriteLine($"[MenuBarViewModel] Closed float window: {window.Id}");
                        }
                    }

                    mainVM.DockLayout.Windows.Clear();
                }

                // 1. Удаляем LOCAL workspace.json из ZIP
                _workspaceConfigService.DeleteFromZip(fileStorage);

                // 3. Загружаем DEFAULT конфигурацию явно
                var defaultWorkModes = _workModeConfigService.LoadConfiguration(project.Type, fileStorage);
                project.WorkModes = defaultWorkModes;

                Console.WriteLine($"[MenuBarViewModel] Loaded {defaultWorkModes.Count} default WorkModes");

                // 4. Пересоздаём Workspace с новыми WorkModes
                activeTab.InitializeWorkspace(defaultWorkModes);

                // 5. Обновляем UI
                mainVM?.InitializeWorkModesForTab(activeTab);

                // 4. Теперь InitializeWorkModesForTab загрузит DEFAULT!
                mainVM?.InitializeWorkModesForTab(activeTab);

                _notificationService.ShowSuccess("Конфигурация сброшена до дефолта");
                Console.WriteLine("[MenuBarViewModel] Reset to default complete");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MenuBarViewModel] ERROR: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Элемент списка недавних проектов
    /// </summary>
    public class RecentProjectItem
    {
        /// <summary>Полный путь к файлу проекта</summary>
        public string FilePath { get; set; } = "";

        /// <summary>Имя файла для отображения в меню</summary>
        public string ProjectName { get; set; } = "";
    }
}