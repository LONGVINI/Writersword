using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using Writersword.Core.Enums;
using Writersword.Core.Models.Project;
using Writersword.Services;
using Writersword.Services.Interfaces;
using Writersword.Views;
using Writersword.Resources.Localization;

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
        private bool _isProcessing = false; // 

        /// <summary>Выбранный тип проекта</summary>
        public ProjectType SelectedProjectType
        {
            get => _selectedProjectType;
            set => this.RaiseAndSetIfChanged(ref _selectedProjectType, value);
        }

        /// <summary>Можно ли закрыть окно (есть ли открытые вкладки)</summary>
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

            // ИСПРАВЛЕНИЕ: Проверяем CanClose правильно
            UpdateCanClose();

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

        /// <summary>Обновить состояние CanClose</summary>
        private void UpdateCanClose()
        {
            try
            {
                var mainViewModel = App.Services.GetRequiredService<MainWindowViewModel>();
                CanClose = mainViewModel.OpenTabs.Count > 0;
                Console.WriteLine($"[WelcomeViewModel] CanClose updated: {CanClose}, OpenTabs: {mainViewModel.OpenTabs.Count}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WelcomeViewModel] Error updating CanClose: {ex.Message}");
                CanClose = false;
            }
        }

        /// <summary>Создать новый проект</summary>
        private async System.Threading.Tasks.Task CreateNewProject()
        {
            var savePath = await _dialogService.SaveFileAsync();

            if (string.IsNullOrEmpty(savePath))
            {
                return;
            }

            var mainViewModel = App.Services.GetRequiredService<MainWindowViewModel>();

            // ИСПРАВЛЕНИЕ: Проверяем не открыт ли уже проект с таким путём
            var existingTab = mainViewModel.OpenTabs.FirstOrDefault(t => t.GetModel().FilePath == savePath);
            if (existingTab != null)
            {
                Console.WriteLine($"[CreateNewProject] Project already open: {savePath}, activating existing tab");

                // Деактивируем все вкладки
                foreach (var tab in mainViewModel.OpenTabs)
                {
                    tab.IsActive = false;
                }

                // Активируем существующую вкладку
                mainViewModel.ActivateTab(existingTab);
                ProjectSelected?.Invoke();
                return;
            }

            var projectName = System.IO.Path.GetFileNameWithoutExtension(savePath);
            var project = _projectService.CreateNew(projectName, SelectedProjectType);

            var firstDoc = new Writersword.Core.Models.Project.DocumentTab
            {
                Title = projectName,
                Content = "",
                IsActive = true,
                FilePath = savePath
            };

            project.Documents.Add(firstDoc);
            await _projectService.SaveAsync(project, savePath);

            // Добавляем в недавние
            _settingsService.AddRecentProject(savePath);

            if (_projectService.OpenProjects.Count == 1)
            {
                _settingsService.LastOpenedProject = savePath;
            }

            // Деактивируем ВСЕ существующие вкладки
            foreach (var tab in mainViewModel.OpenTabs)
            {
                tab.IsActive = false;
            }

            var tabVM = new DocumentTabViewModel(firstDoc, mainViewModel.CloseTab);
            mainViewModel.OpenTabs.Add(tabVM);
            mainViewModel.ActiveTab = tabVM;
            mainViewModel.ShowTextEditor();

            ProjectSelected?.Invoke();
        }

        /// <summary>Открыть существующий проект</summary>
        private async System.Threading.Tasks.Task OpenExistingProject()
        {
            var path = await _dialogService.OpenFileAsync();

            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            var project = await _projectService.LoadAsync(path);

            if (project == null) return;

            var mainViewModel = App.Services.GetRequiredService<MainWindowViewModel>();

            if (project.Documents.Count > 0)
            {
                var hasOpenTabs = mainViewModel.OpenTabs.Any(t => t.GetModel().FilePath == path);

                if (!hasOpenTabs)
                {
                    // ИСПРАВЛЕНИЕ: Деактивируем ВСЕ существующие вкладки
                    foreach (var tab in mainViewModel.OpenTabs)
                    {
                        tab.IsActive = false;
                    }

                    foreach (var doc in project.Documents)
                    {
                        doc.FilePath = path;
                        var tabVM = new DocumentTabViewModel(doc, mainViewModel.CloseTab);
                        mainViewModel.OpenTabs.Add(tabVM);
                    }
                }
                else
                {
                    // ИСПРАВЛЕНИЕ: Деактивируем ВСЕ вкладки перед активацией нужной
                    foreach (var tab in mainViewModel.OpenTabs)
                    {
                        tab.IsActive = false;
                    }
                }

                var firstTab = mainViewModel.OpenTabs.First(t => t.GetModel().FilePath == path);
                mainViewModel.ActivateTab(firstTab);
                mainViewModel.ShowTextEditor();
            }

            // Добавляем в недавние
            _settingsService.AddRecentProject(path);
            _settingsService.LastOpenedProject = path;

            ProjectSelected?.Invoke();
        }

        /// <summary>Открыть недавний проект</summary>
        private async void OpenRecentProject(RecentProject recent)
        {
            Console.WriteLine($"[OpenRecentProject] Opening: {recent.Name} at {recent.Path}");

            var project = await _projectService.LoadAsync(recent.Path);

            if (project == null)
            {
                Console.WriteLine($"[OpenRecentProject] Failed to load project");
                return;
            }

            Console.WriteLine($"[OpenRecentProject] Project loaded: {project.Title}, Documents: {project.Documents.Count}");

            var mainViewModel = App.Services.GetRequiredService<MainWindowViewModel>();

            if (project.Documents.Count > 0)
            {
                var hasOpenTabs = mainViewModel.OpenTabs.Any(t => t.GetModel().FilePath == recent.Path);

                if (hasOpenTabs)
                {
                    Console.WriteLine($"[OpenRecentProject] Tabs already open, activating first");
                    var firstTab = mainViewModel.OpenTabs.First(t => t.GetModel().FilePath == recent.Path);
                    mainViewModel.ActivateTab(firstTab);
                }
                else
                {
                    Console.WriteLine($"[OpenRecentProject] Adding {project.Documents.Count} new tabs");
                    foreach (var doc in project.Documents)
                    {
                        doc.FilePath = recent.Path;
                        var tabVM = new DocumentTabViewModel(doc, mainViewModel.CloseTab);
                        mainViewModel.OpenTabs.Add(tabVM);
                        Console.WriteLine($"[OpenRecentProject] Added tab: {doc.Title}");
                    }

                    if (mainViewModel.OpenTabs.Count > 0)
                    {
                        var firstTab = mainViewModel.OpenTabs.First(t => t.GetModel().FilePath == recent.Path);
                        mainViewModel.ActivateTab(firstTab);
                        Console.WriteLine($"[OpenRecentProject] Activated tab: {firstTab.Title}");
                    }
                }
            }
            else
            {
                Console.WriteLine($"[OpenRecentProject] No documents, creating new tab");
                var fileName = System.IO.Path.GetFileNameWithoutExtension(recent.Path);
                mainViewModel.AddNewTab(fileName, "", recent.Path);
            }

            // Добавляем в недавние (обновляет время)
            _settingsService.AddRecentProject(recent.Path);
            _settingsService.LastOpenedProject = recent.Path;

            ProjectSelected?.Invoke();
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

        /// <summary>Открыть недавний проект</summary>
        public async void OpenRecentProjectDirect(RecentProject recent)
        {
            // НОВОЕ: Проверяем не обрабатывается ли уже запрос
            if (_isProcessing)
            {
                Console.WriteLine($"[OpenRecentProjectDirect] Already processing, ignoring click");
                return;
            }

            _isProcessing = true; // Блокируем повторные клики

            try
            {
                Console.WriteLine($"[OpenRecentProjectDirect] Opening: {recent.Name} at {recent.Path}");

                // Проверяем существует ли файл
                if (!System.IO.File.Exists(recent.Path))
                {
                    Console.WriteLine($"[OpenRecentProjectDirect] File not found: {recent.Path}");

                    var message = $"{Strings.Error_ProjectNotFound_Message}\n\n{recent.Path}";

                    await _dialogService.ShowMessageAsync(
                        Strings.Error_ProjectNotFound_Title,
                        message,
                        MessageBoxType.Error,
                        MessageBoxButtons.OK
                    );

                    _settingsService.RecentProjects.Remove(recent);
                    RecentProjects.Remove(recent);
                    _settingsService.Save();

                    return;
                }

                var project = await _projectService.LoadAsync(recent.Path);

                if (project == null)
                {
                    Console.WriteLine($"[OpenRecentProjectDirect] Failed to load project");

                    var message = $"{Strings.Error_ProjectLoadFailed_Message}\n\n{recent.Path}";

                    await _dialogService.ShowMessageAsync(
                        Strings.Error_ProjectLoadFailed_Title,
                        message,
                        MessageBoxType.Error,
                        MessageBoxButtons.OK
                    );

                    return;
                }

                Console.WriteLine($"[OpenRecentProjectDirect] Project loaded: {project.Title}, Documents: {project.Documents.Count}");

                var mainViewModel = App.Services.GetRequiredService<MainWindowViewModel>();

                if (project.Documents.Count > 0)
                {
                    var hasOpenTabs = mainViewModel.OpenTabs.Any(t => t.GetModel().FilePath == recent.Path);

                    if (hasOpenTabs)
                    {
                        Console.WriteLine($"[OpenRecentProjectDirect] Tabs already open, activating first");

                        foreach (var tab in mainViewModel.OpenTabs)
                        {
                            tab.IsActive = false;
                        }

                        var firstTab = mainViewModel.OpenTabs.First(t => t.GetModel().FilePath == recent.Path);
                        mainViewModel.ActivateTab(firstTab);
                    }
                    else
                    {
                        Console.WriteLine($"[OpenRecentProjectDirect] Adding {project.Documents.Count} new tabs");

                        DocumentTabViewModel? firstNewTab = null;

                        foreach (var doc in project.Documents)
                        {
                            doc.FilePath = recent.Path;
                            var tabVM = new DocumentTabViewModel(doc, mainViewModel.CloseTab);
                            mainViewModel.OpenTabs.Add(tabVM);
                            Console.WriteLine($"[OpenRecentProjectDirect] Added tab: {doc.Title}");

                            if (firstNewTab == null)
                            {
                                firstNewTab = tabVM;
                            }
                        }

                        if (firstNewTab != null)
                        {
                            foreach (var tab in mainViewModel.OpenTabs)
                            {
                                tab.IsActive = false;
                            }

                            mainViewModel.ActivateTab(firstNewTab);
                            Console.WriteLine($"[OpenRecentProjectDirect] Activated tab: {firstNewTab.Title}");
                        }
                    }
                }
                else
                {
                    Console.WriteLine($"[OpenRecentProjectDirect] No documents, creating new tab");
                    var fileName = System.IO.Path.GetFileNameWithoutExtension(recent.Path);
                    mainViewModel.AddNewTab(fileName, "", recent.Path);
                }

                _settingsService.AddRecentProject(recent.Path);
                _settingsService.LastOpenedProject = recent.Path;

                ProjectSelected?.Invoke();
            }
            finally
            {
                _isProcessing = false;
            }
        }
    }
}
