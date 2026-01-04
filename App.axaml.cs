using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Interfaces.Services;
using Writersword.Infrastructure.Services.Modules;
using Writersword.Modules.Common;
using Writersword.Services;
using Writersword.Src.Core.Interfaces.Services.Input;
using Writersword.Src.Core.Interfaces.Services.Storage;
using Writersword.Src.Core.Interfaces.Services.UI;
using Writersword.Src.Core.Interfaces.WorkFlows;
using Writersword.Src.Core.Interfaces.WorkModes;
using Writersword.Src.Infrastructure.Services.Input;
using Writersword.Src.Infrastructure.Services.Modules;
using Writersword.Src.Infrastructure.Services.Project;
using Writersword.Src.Infrastructure.Services.Storage;
using Writersword.Src.Infrastructure.Services.Tabs;
using Writersword.Src.Infrastructure.Services.UI;
using Writersword.Src.Infrastructure.Services.WorkModes;
using Writersword.Src.ProjectTypes.Common;
using Writersword.Src.WorkModes.Common;
using Writersword.ViewModels;
using Writersword.ViewModels.Components;
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

            // Сервис всплывающих уведомлений (toast notifications)
            services.AddSingleton<INotificationService, NotificationService>();

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

            // Сервис кеширования данных (.wsasd файлы)
            services.AddSingleton<ICacheService, CacheService>();

            // Сервис автосохранения проектов (каждая вкладка получает свой экземпляр)
            services.AddTransient<IAutoSaveService, AutoSaveService>();

            // --- МОДУЛЬНАЯ СИСТЕМА ---
            // Фабрика для создания экземпляров модулей
            services.AddSingleton<ModuleFactory>();

            // Реестр всех зарегистрированных модулей
            services.AddSingleton<ModuleRegistry>();

            // Сервис для сбора состояний модулей (используется при автосохранении)
            services.AddSingleton<IModuleStateCollectorService, ModuleStateCollectorService>();

            // Сервис управления жизненным циклом модулей (открытие/закрытие с сохранением)
            services.AddSingleton<IModuleLifecycleService, ModuleLifecycleService>();

            // --- WORKMODE СИСТЕМА ---
            // Фабрика для создания экземпляров WorkMode
            services.AddSingleton<WorkModeFactory>();

            // Реестр всех зарегистрированных WorkMode
            services.AddSingleton<WorkModeRegistry>();

            // --- СИСТЕМА ТИПОВ ПРОЕКТОВ ---
            // Реестр всех типов проектов (Novel, Screenplay, etc)
            services.AddSingleton<ProjectTypeRegistry>(sp =>
            {
                var workModeRegistry = sp.GetRequiredService<WorkModeRegistry>();
                return new ProjectTypeRegistry(workModeRegistry);
            });

            // --- VIEWMODELS ---
            // ViewModel главного окна
            services.AddSingleton<MainWindowViewModel>();

            // --- КОМПОНЕНТЫ ГЛАВНОГО ОКНА ---
            // ViewModel компонента главного меню
            services.AddSingleton<MenuBarViewModel>();

            // ViewModel панели вкладок
            services.AddSingleton<TabBarViewModel>();

            // ViewModel панели режимов работы
            services.AddSingleton<WorkModeBarViewModel>();

            // ViewModel панели модулей
            services.AddSingleton<ModulePanelViewModel>();

            // ViewModel экрана приветствия (создаётся каждый раз новый)
            services.AddTransient<WelcomeViewModel>();

            // --- DOCK СИСТЕМА ---
            // Фабрика для создания dock layout'ов
            services.AddSingleton<Src.Infrastructure.Dock.DockFactory>();

            // --- УПРАВЛЕНИЕ ВКЛАДКАМИ ---
            // Сервис управления коллекцией вкладок
            services.AddSingleton<ITabCollection>(sp =>
            {
                var settingsService = sp.GetRequiredService<ISettingsService>();
                return new TabCollection(settingsService);
            });

            // Сервис управления жизненным циклом проектов
            services.AddSingleton<IProjectWorkflow, ProjectWorkflow>();

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
                    moduleFactory.Register(instance.ModuleId, () =>
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
            // АВТОМАТИЧЕСКАЯ РЕГИСТРАЦИЯ ТИПОВ ПРОЕКТОВ
            // Все классы наследующие BaseProjectType регистрируются автоматически
            // ========================================
            var projectTypeRegistry = Services.GetRequiredService<ProjectTypeRegistry>();
            projectTypeRegistry.LoadAll();
            Console.WriteLine($"[App] All ProjectTypes registered successfully! Total: {projectTypeRegistry.GetAll().Count}");

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
                // ОБРАБОТКА ЗАКРЫТИЯ ОКНА
                // Спрашиваем про несохранённые изменения
                // ========================================
                mainWindow.Closing += async (s, e) =>
                {
                    Console.WriteLine("[App] MainWindow closing...");

                    var tabCollection = Services.GetRequiredService<ITabCollection>();
                    var projectWorkflow = Services.GetRequiredService<IProjectWorkflow>();

                    // Проверяем каждую вкладку на несохранённые изменения
                    foreach (var tab in tabCollection.Tabs.ToList())
                    {
                        if (projectWorkflow.HasUnsavedChanges(tab))
                        {
                            // Отменяем закрытие
                            e.Cancel = true;

                            Console.WriteLine($"[App] Tab {tab.Title} has unsaved changes");

                            // Пытаемся закрыть вкладку (спросит про сохранение)
                            var closed = await projectWorkflow.CloseDocumentAsync(tab);

                            if (!closed)
                            {
                                // Пользователь отменил - не закрываем приложение
                                Console.WriteLine("[App] User cancelled closing");
                                return;
                            }
                        }
                    }

                    // Если дошли сюда - все вкладки закрыты, можно выходить
                    if (!e.Cancel)
                    {
                        Console.WriteLine("[App] All tabs closed, exiting");
                    }
                };


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
                        var projectWorkflow = Services.GetRequiredService<IProjectWorkflow>();
                        var tabCollection = Services.GetRequiredService<ITabCollection>();

                        Console.WriteLine($"[App] Restoring {openProjects.Count} projects...");

                        // Загружаем каждый проект через ProjectWorkflow
                        foreach (var projectPath in openProjects)
                        {
                            if (File.Exists(projectPath))
                            {
                                Console.WriteLine($"[App] Loading project: {projectPath}");

                                // Открываем через ProjectWorkflow
                                var tab = await projectWorkflow.OpenDocumentAsync(projectPath);

                                if (tab != null)
                                {
                                    // Добавляем БЕЗ автоматической активации
                                    tabCollection.Add(tab);
                                    Console.WriteLine($"[App] Added tab: {tab.Title}");
                                }
                            }
                            else
                            {
                                Console.WriteLine($"[App] WARNING: Project file not found: {projectPath}");
                            }
                        }

                        // Активируем первую вкладку (это вызовет ActivateTab ОДИН РАЗ)
                        if (tabCollection.Tabs.Count > 0)
                        {
                            Console.WriteLine($"[App] All projects loaded. Total tabs: {tabCollection.Tabs.Count}");
                            tabCollection.ActiveTab = tabCollection.Tabs[0];
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