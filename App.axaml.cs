using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.IO;
using Writersword.Services;
using Writersword.Services.Interfaces;
using Writersword.ViewModels;
using Writersword.Views;

namespace Writersword
{
    public partial class App : Application
    {
        //Глобавльный DI контейнер
        public static IServiceProvider Services { get; private set; } = null!;
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            // ========================================
            // Настройка DI контейнера
            // ========================================
            var services = new ServiceCollection();

            // Регистрация сервисов
            services.AddSingleton<ISettingsService, SettingsService>();
            services.AddSingleton<IDialogService, DialogService>();
            services.AddSingleton<IProjectService, ProjectService>();

            // Регистрация ViewModels
            services.AddSingleton<MainWindowViewModel>();
            services.AddTransient<WelcomeViewModel>();

            // Создаём контейнер
            Services = services.BuildServiceProvider();

            // ========================================
            // Создание главного окна
            // ========================================
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Загружаем настройки
                var settingsService = Services.GetRequiredService<ISettingsService>();
                settingsService.Load();

                // Создаём главное окно
                var mainViewModel = Services.GetRequiredService<MainWindowViewModel>();
                var mainWindow = new MainWindow
                {
                    DataContext = mainViewModel
                };

                // Регистрируем в DialogService
                var dialogService = Services.GetRequiredService<IDialogService>() as DialogService;
                dialogService?.SetMainWindow(mainWindow);

                desktop.MainWindow = mainWindow;

                // Проверяем есть ли последний открытый проект
                mainWindow.Opened += async (s, e) =>
                {
                    var lastProject = settingsService.LastOpenedProject;

                    // Если есть последний проект И файл существует
                    if (!string.IsNullOrEmpty(lastProject) && File.Exists(lastProject))
                    {
                        mainViewModel.LoadProject(lastProject);
                    }
                    else
                    {
                        // Нет проекта - показываем Welcome
                        await ShowWelcomeScreen(mainWindow);
                    }
                };
            }

            base.OnFrameworkInitializationCompleted();
        }

        /// <summary>Показать экран приветствия (доступен из любого места)</summary>
        public static async System.Threading.Tasks.Task ShowWelcomeScreen(Window owner)
        {
            var welcomeViewModel = Services.GetRequiredService<WelcomeViewModel>();
            var welcomeWindow = new WelcomeView
            {
                DataContext = welcomeViewModel
            };

            await welcomeWindow.ShowDialog(owner);

            // После закрытия Welcome - обновляем MainWindow
            var mainViewModel = Services.GetRequiredService<MainWindowViewModel>();
            mainViewModel.RefreshAfterProjectLoad();
        }
    }
}