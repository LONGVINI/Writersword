using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.IO;
using Writersword.Core.Enums;
using Writersword.Core.Services.WorkModes;
using Writersword.Modules.Common;
using Writersword.Modules.TextEditor;
using Writersword.Modules.Synonyms;
using Writersword.Modules.Notes;
using Writersword.Modules.Timer;
using Writersword.Services;
using Writersword.Services.Interfaces;
using Writersword.ViewModels;
using Writersword.Views;
using Writersword.Src.Core.Interfaces.WorkModes;

namespace Writersword
{
    /// <summary>
    /// Главный класс приложения
    /// Отвечает за инициализацию DI контейнера, регистрацию сервисов и модулей
    /// </summary>
    public partial class App : Application
    {
        /// <summary>
        /// Глобальный DI контейнер
        /// Доступен из любого места приложения через App.Services
        /// </summary>
        public static IServiceProvider Services { get; private set; } = null!;

        /// <summary>
        /// Инициализация Avalonia - загрузка XAML ресурсов
        /// Вызывается автоматически при запуске приложения
        /// </summary>
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        /// <summary>
        /// Основная инициализация приложения
        /// Здесь настраивается DI, создаётся главное окно, регистрируются модули
        /// </summary>
        public override void OnFrameworkInitializationCompleted()
        {
            // ========================================
            // НАСТРОЙКА DI КОНТЕЙНЕРА
            // Dependency Injection - все сервисы регистрируются здесь
            // ========================================
            var services = new ServiceCollection();

            // --- ОСНОВНЫЕ СЕРВИСЫ ---
            // Сервис настроек (сохранение/загрузка settings.json)
            services.AddSingleton<ISettingsService, SettingsService>();

            // Сервис диалоговых окон (сохранение файлов, MessageBox)
            services.AddSingleton<IDialogService, DialogService>();

            // Сервис работы с проектами (.writersword файлы)
            services.AddSingleton<IProjectService, ProjectService>();

            // Сервис локализации (переключение языков)
            services.AddSingleton<ILocalizationService, LocalizationService>();

            // Сервис горячих клавиш
            services.AddSingleton<IHotKeyService, HotKeyService>();

            // --- СЕРВИСЫ WORKMODES ---
            // Сервис конфигурации WorkModes (загрузка из файлов)
            services.AddSingleton<IWorkModeConfigurationService, WorkModeConfigurationService>();

            // Сервис управления WorkModes (переключение режимов)
            services.AddSingleton<IWorkModeService, WorkModeService>();

            // --- МОДУЛЬНАЯ СИСТЕМА ---
            services.AddSingleton<ModuleFactory>();
            services.AddSingleton<ModuleRegistry>();
            services.AddSingleton<MainWindowViewModel>();
            services.AddSingleton<Src.Infrastructure.Dock.DockFactory>();


            services.AddSingleton<MainWindowViewModel>();
            services.AddTransient<WelcomeViewModel>();

            // ========================================
            // СОЗДАНИЕ КОНТЕЙНЕРА
            // После этого можно получать сервисы через App.Services
            // ========================================
            Services = services.BuildServiceProvider();

            // ========================================
            // РЕГИСТРАЦИЯ МОДУЛЕЙ В ФАБРИКЕ
            // Говорим ModuleFactory как создавать каждый тип модуля
            // ========================================
            var moduleFactory = Services.GetRequiredService<ModuleFactory>();

            // TextEditor - основной модуль редактирования текста
            moduleFactory.Register(
                ModuleType.TextEditor,
                () => new TextEditorModule()
            );

            // Synonyms - помощник поиска синонимов
            moduleFactory.Register(
                ModuleType.Synonyms,
                () => new SynonymsModule()
            );

            // Notes - быстрые заметки
            moduleFactory.Register(
                ModuleType.Notes,
                () => new NotesModule()
            );

            // Timer - таймер работы
            moduleFactory.Register(
                ModuleType.Timer,
                () => new TimerModule()
            );

            Console.WriteLine("[App] All modules registered successfully!");

            // ========================================
            // СОЗДАНИЕ ГЛАВНОГО ОКНА
            // ========================================
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // --- ЗАГРУЗКА НАСТРОЕК ---
                var settingsService = Services.GetRequiredService<ISettingsService>();
                settingsService.Load();
                Console.WriteLine("[App] Settings loaded");

                // --- СОЗДАНИЕ ГЛАВНОГО ОКНА ---
                var mainViewModel = Services.GetRequiredService<MainWindowViewModel>();
                var mainWindow = new MainWindow
                {
                    DataContext = mainViewModel
                };

#if DEBUG
                // В режиме отладки добавляем DevTools (F12 для открытия)
                mainWindow.AttachDevTools();
                Console.WriteLine("[App] DevTools attached (press F12)");
#endif

                // --- РЕГИСТРАЦИЯ ОКНА В DIALOGSERVICE ---
                // Нужно для показа диалогов (Save, Open и т.д.)
                var dialogService = Services.GetRequiredService<IDialogService>() as DialogService;
                dialogService?.SetMainWindow(mainWindow);

                // Устанавливаем главное окно приложения
                desktop.MainWindow = mainWindow;

                // ========================================
                // ВОССТАНОВЛЕНИЕ ПРОЕКТОВ ИЗ ПРОШЛОЙ СЕССИИ
                // Срабатывает когда главное окно открылось
                // ========================================
                mainWindow.Opened += async (s, e) =>
                {
                    // Получаем список открытых проектов из прошлой сессии
                    var openProjects = settingsService.OpenProjectPaths;
                    Console.WriteLine($"[App] Open projects from last session: {openProjects.Count}");

                    // --- ЕСТЬ ОТКРЫТЫЕ ПРОЕКТЫ? ---
                    if (openProjects.Count > 0)
                    {
                        var projectService = Services.GetRequiredService<IProjectService>();
                        Console.WriteLine($"[App] Restoring {openProjects.Count} projects...");

                        // Загружаем каждый проект
                        foreach (var projectPath in openProjects)
                        {
                            // Проверяем существует ли файл
                            if (File.Exists(projectPath))
                            {
                                Console.WriteLine($"[App] Loading project: {projectPath}");

                                // Загружаем проект
                                var project = await projectService.LoadAsync(projectPath);

                                // Если загрузился успешно - создаём вкладки
                                if (project != null && project.Documents.Count > 0)
                                {
                                    // Для каждого документа в проекте создаём вкладку
                                    foreach (var doc in project.Documents)
                                    {
                                        doc.FilePath = projectPath;
                                        var tabVM = new DocumentTabViewModel(doc, mainViewModel.CloseTab);
                                        mainViewModel.OpenTabs.Add(tabVM);
                                        Console.WriteLine($"[App] Added tab: {doc.Title}");
                                    }
                                }
                            }
                            else
                            {
                                Console.WriteLine($"[App] WARNING: Project file not found: {projectPath}");
                            }
                        }

                        // Активируем первую вкладку
                        if (mainViewModel.OpenTabs.Count > 0)
                        {
                            Console.WriteLine($"[App] All projects loaded. Total tabs: {mainViewModel.OpenTabs.Count}");
                            mainViewModel.ActivateTab(mainViewModel.OpenTabs[0]);
                        }
                        else
                        {
                            // Ни один проект не загрузился - показываем Welcome
                            Console.WriteLine("[App] No projects loaded, showing welcome");
                            await ShowWelcomeScreen(mainWindow);
                        }
                    }
                    else
                    {
                        // --- НЕТ ОТКРЫТЫХ ПРОЕКТОВ - ПОКАЗЫВАЕМ WELCOME ---
                        Console.WriteLine("[App] No projects from last session, showing welcome");
                        await ShowWelcomeScreen(mainWindow);
                    }
                };
            }

            base.OnFrameworkInitializationCompleted();
        }

        /// <summary>
        /// Показать экран приветствия (Welcome screen)
        /// Можно вызвать из любого места приложения
        /// </summary>
        /// <param name="owner">Родительское окно (для модального отображения)</param>
        public static async System.Threading.Tasks.Task ShowWelcomeScreen(Window owner)
        {
            Console.WriteLine("[App] Showing welcome screen");

            // Создаём ViewModel и View
            var welcomeViewModel = Services.GetRequiredService<WelcomeViewModel>();
            var welcomeWindow = new WelcomeView
            {
                DataContext = welcomeViewModel
            };

            // Показываем модально (блокирует главное окно)
            await welcomeWindow.ShowDialog(owner);
        }
    }
}