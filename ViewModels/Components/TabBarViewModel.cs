using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Threading.Tasks;
using Writersword.Src.Core.Interfaces.WorkFlows;
using Writersword.ViewModels;

namespace Writersword.ViewModels.Components
{
    /// <summary>
    /// ViewModel для панели вкладок
    /// Управляет открытыми документами и их переключением
    /// </summary>
    public class TabBarViewModel : ViewModelBase
    {
        private readonly ITabCollection _tabCollection;
        private readonly IProjectWorkflow _projectWorkflow;

        /// <summary>Список открытых вкладок (из TabCollection)</summary>
        public ObservableCollection<DocumentTabViewModel> Tabs => _tabCollection.Tabs;

        /// <summary>Активная вкладка</summary>
        public DocumentTabViewModel? ActiveTab
        {
            get => _tabCollection.ActiveTab;
            set => _tabCollection.ActiveTab = value;
        }

        /// <summary>Команда создания новой вкладки (показывает Welcome screen)</summary>
        public ReactiveCommand<Unit, Unit> CreateNewTabCommand { get; }

        /// <summary>Команда активации вкладки</summary>
        public ReactiveCommand<DocumentTabViewModel, Unit> ActivateTabCommand { get; }

        /// <summary>Команда закрытия вкладки</summary>
        public ReactiveCommand<DocumentTabViewModel, Unit> CloseTabCommand { get; }

        /// <summary>Функция активации вкладки (передаётся из MainWindowViewModel)</summary>
        private Action<DocumentTabViewModel>? _onTabActivated;

        public TabBarViewModel(
            ITabCollection tabCollection,
            IProjectWorkflow projectWorkflow)
        {
            _tabCollection = tabCollection;
            _projectWorkflow = projectWorkflow;

            // Команда создания новой вкладки
            CreateNewTabCommand = ReactiveCommand.Create(CreateNewTab);

            // Команда активации вкладки
            ActivateTabCommand = ReactiveCommand.Create<DocumentTabViewModel>(ActivateTab);

            // Команда закрытия вкладки
            CloseTabCommand = ReactiveCommand.CreateFromTask<DocumentTabViewModel>(CloseTabAsync);

            // Подписываемся на изменения активной вкладки
            _tabCollection.ActiveTabChanged += OnActiveTabChanged;

            Console.WriteLine("[TabBarViewModel] Initialized");
        }

        /// <summary>
        /// Установить обработчик активации вкладки
        /// Вызывается из MainWindowViewModel после создания
        /// </summary>
        public void SetTabActivatedHandler(Action<DocumentTabViewModel> handler)
        {
            _onTabActivated = handler;
            Console.WriteLine("[TabBarViewModel] Tab activation handler set");
        }

        /// <summary>Создать новую вкладку (показывает Welcome screen)</summary>
        private async void CreateNewTab()
        {
            Console.WriteLine("[TabBarViewModel] CreateNewTab clicked");

            if (Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow != null)
            {
                await App.ShowWelcomeScreen(desktop.MainWindow);
            }
        }

        /// <summary>Активировать вкладку</summary>
        private void ActivateTab(DocumentTabViewModel tab)
        {
            Console.WriteLine($"[TabBarViewModel] Activating tab: {tab.Title}");

            // Устанавливаем активную вкладку (это вызовет событие ActiveTabChanged)
            ActiveTab = tab;
        }

        /// <summary>Закрыть вкладку</summary>
        private async Task CloseTabAsync(DocumentTabViewModel tab)
        {
            Console.WriteLine($"[TabBarViewModel] Closing tab: {tab.Title}");

            // Закрываем через ProjectWorkflow (проверит несохранённые изменения)
            bool closed = await _projectWorkflow.CloseDocumentAsync(tab);

            if (!closed)
            {
                Console.WriteLine($"[TabBarViewModel] Close cancelled by user");
                return; // Пользователь отменил - НЕ УДАЛЯЕМ вкladку!
            }

            // Удаляем вкладку из коллекции
            _tabCollection.Remove(tab);
            Console.WriteLine($"[TabBarViewModel] Tab removed from collection");
        }

        /// <summary>
        /// Обработчик изменения активной вкладки
        /// Вызывается когда TabCollection.ActiveTab изменилось
        /// </summary>
        private void OnActiveTabChanged(DocumentTabViewModel? tab)
        {
            // Уведомляем UI об изменении
            this.RaisePropertyChanged(nameof(ActiveTab));

            // Вызываем обработчик из MainWindowViewModel
            if (tab != null)
            {
                _onTabActivated?.Invoke(tab);
                Console.WriteLine($"[TabBarViewModel] Active tab changed: {tab.Title}");
            }
        }
    }
}