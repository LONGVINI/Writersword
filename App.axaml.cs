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
        // Глобальный DI контейнер
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
            services.AddSingleton<ILocalizationService, LocalizationService>();

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

#if DEBUG
                // Добавляем DevTools - нажмите F12 для открытия инспектора
                mainWindow.AttachDevTools();
#endif

                // Регистрируем в DialogService
                var dialogService = Services.GetRequiredService<IDialogService>() as DialogService;
                dialogService?.SetMainWindow(mainWindow);

                desktop.MainWindow = mainWindow;

                // Проверяем есть ли открытые проекты из последней сессии
                mainWindow.Opened += async (s, e) =>
                {
                    var openProjects = settingsService.OpenProjectPaths;
                    Console.WriteLine($"MainWindow opened. Open projects from last session: {openProjects.Count}");

                    if (openProjects.Count > 0)
                    {
                        // Получаем ProjectService
                        var projectService = Services.GetRequiredService<IProjectService>();

                        // Загружаем все открытые проекты из последней сессии
                        Console.WriteLine($"Restoring {openProjects.Count} projects from last session");

                        foreach (var projectPath in openProjects)
                        {
                            if (File.Exists(projectPath))
                            {
                                Console.WriteLine($"Loading project: {projectPath}");

                                var project = await projectService.LoadAsync(projectPath);

                                if (project != null && project.Documents.Count > 0)
                                {
                                    // Добавляем вкладки
                                    foreach (var doc in project.Documents)
                                    {
                                        doc.FilePath = projectPath;
                                        var tabVM = new DocumentTabViewModel(doc, mainViewModel.CloseTab);
                                        mainViewModel.OpenTabs.Add(tabVM);
                                        Console.WriteLine($"Added tab: {doc.Title}");
                                    }
                                }
                            }
                            else
                            {
                                Console.WriteLine($"Project file not found: {projectPath}");
                            }
                        }

                        // Активируем первую вкладку после загрузки всех проектов
                        if (mainViewModel.OpenTabs.Count > 0)
                        {
                            Console.WriteLine($"All projects loaded. Total tabs: {mainViewModel.OpenTabs.Count}");
                            mainViewModel.ActivateTab(mainViewModel.OpenTabs[0]);
                        }
                        else
                        {
                            // Если ни один проект не загрузился - показываем Welcome
                            Console.WriteLine("No projects loaded, showing welcome");
                            await ShowWelcomeScreen(mainWindow);
                        }
                    }
                    else
                    {
                        // НЕТ ПРОЕКТОВ - ПОКАЗЫВАЕМ WELCOME
                        Console.WriteLine("No projects from last session, showing welcome");
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

            // Показываем модально
            await welcomeWindow.ShowDialog(owner);
        }
    }
}