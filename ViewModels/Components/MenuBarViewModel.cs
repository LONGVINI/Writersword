using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Threading.Tasks;
using Writersword.Src.Core.Interfaces.WorkFlows;

namespace Writersword.ViewModels.Components
{
    /// <summary>
    /// ViewModel для главного меню приложения (File, Edit, View)
    /// Отвечает за команды работы с проектами
    /// </summary>
    public class MenuBarViewModel : ViewModelBase
    {
        private readonly IProjectWorkflow _projectWorkflow;

        // Провайдер для доступа к MainWindowViewModel (для меню View)
        private Func<MainWindowViewModel>? _mainViewModelProvider;

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

        /// <summary>Команда сохранения проекта (Ctrl+S)</summary>
        public ReactiveCommand<Unit, Unit> SaveProjectCommand { get; }

        /// <summary>Команда "Сохранить как..." (Ctrl+Shift+S)</summary>
        public ReactiveCommand<Unit, Unit> SaveAsProjectCommand { get; }

        /// <summary>Команда выхода из приложения</summary>
        public ReactiveCommand<Unit, Unit> ExitCommand { get; }

        /// <summary>Функция для получения активной вкладки (передаётся извне)</summary>
        private Func<DocumentTabViewModel?>? _getActiveTab;

        public MenuBarViewModel(IProjectWorkflow projectWorkflow)
        {
            _projectWorkflow = projectWorkflow;

            // Создаём команды
            NewProjectCommand = ReactiveCommand.Create(NewProject);
            OpenProjectCommand = ReactiveCommand.CreateFromTask(OpenProject);
            SaveProjectCommand = ReactiveCommand.CreateFromTask(SaveProject);
            SaveAsProjectCommand = ReactiveCommand.CreateFromTask(SaveAsProject);
            ExitCommand = ReactiveCommand.Create(Exit);

            Console.WriteLine("[MenuBarViewModel] Initialized");
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
        }

        /// <summary>Выход из приложения</summary>
        private void Exit()
        {
            Console.WriteLine("[MenuBarViewModel] Exit clicked");
            Environment.Exit(0);
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
    }
}