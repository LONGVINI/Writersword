using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Writersword.Core.Services.WorkModes;
using Writersword.Modules.Common;
using Writersword.Services;
using Writersword.Services.Interfaces;
using Writersword.Src.Core.Interfaces.WorkModes;
using Writersword.Src.WorkModes.Common;
using Writersword.ViewModels;
using Writersword.Views;

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

            // Сервис кеширования данных
            services.AddSingleton<ICacheService, CacheService>();

            // Сервис автосохранения проектов
            services.AddSingleton<IAutoSaveService, AutoSaveService>();

            // --- МОДУЛЬНАЯ СИСТЕМА ---
            services.AddSingleton<ModuleFactory>();
            services.AddSingleton<ModuleRegistry>();

            // --- WORKMODE СИСТЕМА ---
            services.AddSingleton<WorkModeFactory>();
            services.AddSingleton<WorkModeRegistry>();

            services.AddSingleton<MainWindowViewModel>();
            services.AddSingleton<Src.Infrastructure.Dock.DockFactory>();
            services.AddTransient<WelcomeViewModel>();

            // ========================================
            // СОЗДАНИЕ КОНТЕЙНЕРА
            // После этого можно получать сервисы через App.Services
            // ========================================
            Services = services.BuildServiceProvider();

            // ========================================
            // АВТОМАТИЧЕСКАЯ РЕГИСТРАЦИЯ МОДУЛЕЙ
            // Все классы наследующие BaseModule регистрируются автоматически
            // ========================================
            var moduleFactory = Services.GetRequiredService<ModuleFactory>();
            var assembly = Assembly.GetExecutingAssembly();

            // Находим все классы которые наследуют BaseModule и не являются абстрактными
            var moduleTypes = assembly.GetTypes()
                .Where(t => typeof(BaseModule).IsAssignableFrom(t) && !t.IsAbstract);

            Console.WriteLine("[App] Starting automatic module registration...");

            foreach (var moduleType in moduleTypes)
            {
                // Создаём экземпляр модуля для получения его метаданных
                var instance = Activator.CreateInstance(moduleType) as BaseModule;
                if (instance != null)
                {
                    // Регистрируем фабричный метод создания модуля
                    moduleFactory.Register(instance.ModuleType, () =>
                        Activator.CreateInstance(moduleType) as BaseModule 
                        ?? throw new InvalidOperationException($"Failed to create module {moduleType.Name}"));

                    Console.WriteLine($"[App] ✓ Auto-registered module: {instance.Metadata.DisplayName} ({instance.Metadata.Icon})");
                }
            }

            Console.WriteLine($"[App] All modules registered successfully! Total: {moduleTypes.Count()}");

            // ========================================
            // АВТОМАТИЧЕСКАЯ РЕГИСТРАЦИЯ WORKMODES
            // Все классы реализующие IWorkMode регистрируются автоматически
            // ========================================
            var workModeFactory = Services.GetRequiredService<WorkModeFactory>();
            var workModeRegistry = Services.GetRequiredService<WorkModeRegistry>();

            // Находим все классы которые реализуют IWorkMode (но не сам интерфейс)
            var workModeTypes = assembly.GetTypes()
                .Where(t => typeof(IWorkMode).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

            Console.WriteLine("[App] Starting automatic WorkMode registration...");

            foreach (var workModeType in workModeTypes)
            {
                // Создаём экземпляр WorkMode
                var instance = Activator.CreateInstance(workModeType) as IWorkMode;
                if (instance != null)
                {
                    // Регистрируем в фабрике и реестре
                    RegisterWorkMode(workModeFactory, workModeRegistry, instance);
                    Console.WriteLine($"[App] ✓ Auto-registered WorkMode: {instance.DisplayName} ({instance.Icon})");
                }
            }

            Console.WriteLine($"[App] All WorkModes registered successfully! Total: {workModeTypes.Count()}");

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

                                        // Устанавливаем активную вкладку
                                        if (doc.IsActive)
                                        {
                                            mainViewModel.ActiveTab = tabVM;
                                        }
                                    }

                                    if (mainViewModel.OpenTabs.Count > 0)
                                    {
                                        Console.WriteLine($"[App] All projects loaded. Total tabs: {mainViewModel.OpenTabs.Count}");
                                        mainViewModel.ActivateTab(mainViewModel.OpenTabs[0]);

                                        // КРИТИЧНО: Показываем редактор!
                                        mainViewModel.ShowTextEditor();
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
        /// Регистрирует WorkMode в фабрике и реестре
        /// Вызывается автоматически для всех найденных WorkMode классов
        /// </summary>
        private void RegisterWorkMode(WorkModeFactory factory, WorkModeRegistry registry, IWorkMode workMode)
        {
            factory.Register(workMode.Id, () => workMode);
            registry.Register(workMode);
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