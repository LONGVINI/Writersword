using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Threading.Tasks;
using Writersword.Src.Core.Interfaces.WorkFlows;
using Writersword.ViewModels;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.DependencyInjection;

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

        /// <summary>Есть ли RecoveryBanner у активной вкладки</summary>
        public bool HasRecoveryBanner => ActiveTab?.HasRecoveryBanner ?? false;

        /// <summary>Команда создания новой вкладки (показывает Welcome screen)</summary>
        public ReactiveCommand<Unit, Unit> CreateNewTabCommand { get; }

        /// <summary>Команда активации вкладки</summary>
        public ReactiveCommand<DocumentTabViewModel, Unit> ActivateTabCommand { get; }

        /// <summary>Команда закрытия вкладки</summary>
        public ReactiveCommand<DocumentTabViewModel, Unit> CloseTabCommand { get; }

        public TabBarViewModel(
            ITabCollection tabCollection,
            IProjectWorkflow projectWorkflow)
        {
            _tabCollection = tabCollection;
            _projectWorkflow = projectWorkflow;

            // Команда создания новой вкладки
            CreateNewTabCommand = ReactiveCommand.Create(CreateNewTab);

            // Команда активации вкладки (в UI потоке)
            ActivateTabCommand = ReactiveCommand.Create<DocumentTabViewModel>(ActivateTab);

            // Команда закрытия вкладки
            CloseTabCommand = ReactiveCommand.CreateFromTask<DocumentTabViewModel>(CloseTabAsync);

            // Подписываемся на изменения активной вкладки
            _tabCollection.ActiveTabChanged += OnActiveTabChanged;

            // Обновляем HasRecoveryBanner при изменении вкладки
            _tabCollection.ActiveTabChanged += _ =>
            {
                this.RaisePropertyChanged(nameof(HasRecoveryBanner));
            };

            Console.WriteLine("[TabBarViewModel] Initialized");
        }

        /// <summary>Создать новую вкладку (показывает Welcome screen)</summary>
        private async void CreateNewTab()
        {
            Console.WriteLine("[TabBarViewModel] CreateNewTab clicked");

            if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow != null)
            {
                await App.ShowWelcomeScreen(desktop.MainWindow);
            }
        }

        /// <summary>
        /// Активировать вкладку
        /// Сохраняет workspace.json старой вкладки НЕМЕДЛЕННО перед переключением
        /// Сохраняет кеш старой вкладки
        /// </summary>
        private async void ActivateTab(DocumentTabViewModel tab)
        {
            Console.WriteLine($"[TabBarViewModel] Activating tab: {tab.Title}");

            var oldTab = ActiveTab;

            // Если есть старая вкладка - деактивируем её
            if (oldTab != null && oldTab != tab)
            {
                Console.WriteLine($"[TabBarViewModel] Deactivating old tab: {oldTab.Title}");

                // Сохраняем workspace.json НЕМЕДЛЕННО если есть pending changes!
                if (!string.IsNullOrEmpty(oldTab.FilePath))
                {
                    var workflow = App.Services.GetRequiredService<IProjectWorkflow>();
                    var autoSave = workflow.GetAutoSaveServiceForProject(oldTab.FilePath);

                    if (autoSave != null)
                    {
                        await autoSave.SaveNowAsync();  // ← СОХРАНЯЕМ СРАЗУ!
                        Console.WriteLine($"[TabBarViewModel] workspace.json saved immediately for: {oldTab.Title}");
                    }
                }

                // Получаем функцию для получения активных модулей
                var mainViewModel = App.Services.GetRequiredService<MainWindowViewModel>();

                // Сохраняем кеш асинхронно
                await oldTab.SaveToCacheAsync(() => mainViewModel.GetActiveModules());

                Console.WriteLine($"[TabBarViewModel] Old tab saved to cache");
            }

            // Устанавливаем активную вкладку
            // MainWindowViewModel подпишется на ActiveTabChanged и запустит кеширование
            ActiveTab = tab;
        }

        /// <summary>Закрыть вкладку</summary>
        private async Task CloseTabAsync(DocumentTabViewModel tab)
        {
            Console.WriteLine($"[TabBarViewModel] Closing tab: {tab.Title}");

            bool closed = await _projectWorkflow.CloseDocumentAsync(tab);

            if (!closed)
            {
                Console.WriteLine($"[TabBarViewModel] Close cancelled by user");
                return;
            }

            // Очищаем RecoveryBanner перед удалением вкладки
            tab.RecoveryBanner = null;
            Console.WriteLine($"[TabBarViewModel] RecoveryBanner cleared before removal");

            _tabCollection.Remove(tab);
            Console.WriteLine($"[TabBarViewModel] Tab removed from collection");

            // Если не осталось вкладок - очищаем UI и показываем Welcome
            if (_tabCollection.Tabs.Count == 0)
            {
                Console.WriteLine("[TabBarViewModel] No tabs left - clearing UI and showing Welcome");

                var mainViewModel = App.Services.GetRequiredService<MainWindowViewModel>();
                mainViewModel.ClearUIWhenNoTabs();

                if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                    && desktop.MainWindow != null)
                {
                    await App.ShowWelcomeScreen(desktop.MainWindow);
                }
            }
        }

        /// <summary>
        /// Обработчик изменения активной вкладки
        /// Вызывается когда TabCollection.ActiveTab изменилось
        /// </summary>
        private void OnActiveTabChanged(DocumentTabViewModel? tab)
        {
            // Уведомляем UI об изменении
            this.RaisePropertyChanged(nameof(ActiveTab));

            if (tab != null)
            {
                Console.WriteLine($"[TabBarViewModel] Active tab changed: {tab.Title}");
            }
        }
    }
}