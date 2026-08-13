using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Writersword.Core.Interfaces.Services;
using Writersword.Core.Interfaces.Services.UI;
using Writersword.Infrastructure.Logging;
using Writersword.Infrastructure.Services.Modules;
using Writersword.Infrastructure.Services.UI;
using Writersword.Modules.Common;
using Writersword.Core.Interfaces.Services.Input;
using Writersword.Core.Interfaces.Services.Storage;
using Writersword.Core.Interfaces.WorkFlows;
using Writersword.Infrastructure.Dock;
using Writersword.Infrastructure.Services;
using Writersword.Infrastructure.Services.Input;
using Writersword.Infrastructure.Services.Project;
using Writersword.Infrastructure.Services.Storage;
using Writersword.Infrastructure.Services.Tabs;
using Writersword.Infrastructure.Services.WorkModes;
using Writersword.ProjectTypes.Common;
using Writersword.WorkModes.Common;
using Writersword.ViewModels;
using Writersword.ViewModels.Components;
using Writersword.ViewModels.Components.MenuBar;
using Writersword.Views;

namespace Writersword
{
    /// <summary>
    /// Главный класс приложения.
    /// Отвечает за инициализацию DI контейнера, регистрацию сервисов и модулей.
    /// </summary>
    public partial class App : Application
    {
        /// <summary>
        /// Глобальный DI контейнер.
        /// Доступен из любого места приложения через App.Services.
        /// </summary>
        public static IServiceProvider Services { get; private set; } = null!;

        /// <summary>
        /// Главное окно приложения.
        /// Используется модулями для открытия дочерних диалогов и окон,
        /// в том числе PrintPreviewView.
        /// </summary>
        public static Window? MainWindow { get; private set; }

        /// <summary>
        /// Инициализация Avalonia — загрузка XAML ресурсов.
        /// Вызывается автоматически при запуске приложения.
        /// </summary>
        public override void Initialize()
        {
            // Горячая перезагрузка XAML (HotAvalonia) в Debug-сборках включается
            // автоматически для стартового проекта — ручной вызов не нужен.
            AvaloniaXamlLoader.Load(this);
        }

        /// <summary>
        /// Основная инициализация приложения.
        /// Здесь настраивается DI, создаётся главное окно, регистрируются модули.
        /// </summary>
        public override void OnFrameworkInitializationCompleted()
        {
            Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                if (e.Exception is ArgumentException { ParamName: "visual" }
                    && e.Exception.StackTrace?.Contains("DockControl") == true)
                {
                    e.Handled = true;
                    Log.ForContext<App>().Warning(e.Exception,
                        "Dock.Avalonia stale popup reference — caught and ignored");
                    return;
                }

                // Всё остальное раньше проходило через этот обработчик молча: ветка выше
                // отрабатывала свой случай, а прочие исключения не попадали ни в лог, ни
                // куда-либо ещё. Из-за этого сбой в обработчике ввода выглядел как «нажатие
                // ничего не сделало» — без единой строки в логе. Пишем со стеком, не гасим:
                // Handled не ставим, поведение приложения не меняется.
                Log.ForContext<App>().Error(e.Exception,
                    "Необработанное исключение UI-потока");
            };

            // Фоновые потоки и незамеченные задачи: их исключения в лог не попадали вовсе.
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Log.ForContext<App>().Error(e.ExceptionObject as Exception,
                    "Необработанное исключение домена, завершение={Terminating}", e.IsTerminating);

            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Log.ForContext<App>().Error(e.Exception, "Незамеченное исключение задачи");
                e.SetObserved();
            };

            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            Log.Logger = new LoggerConfiguration()
                .ReadFrom.Configuration(configuration)
                .Enrich.With<ShortSourceContextEnricher>()
                .CreateLogger();

            Log.ForContext<App>().Information("Application started");

            var services = new ServiceCollection();

            services.AddLogging(loggingBuilder =>
            {
                loggingBuilder.ClearProviders();
                loggingBuilder.AddSerilog(dispose: true);
            });

            services.AddSingleton<ISettingsService, SettingsService>();
            services.AddSingleton<IDialogService, DialogService>();
            services.AddSingleton<IPrintService, PrintService>();
            services.AddSingleton<INotificationService, NotificationService>();
            services.AddSingleton<IProjectService, ProjectService>();
            services.AddSingleton<IBackupService, BackupService>();
            services.AddSingleton<IBackupTimerService, BackupTimerService>();
            services.AddSingleton<ZipProjectService>();
            services.AddSingleton<ILocalizationService, LocalizationService>();
            services.AddSingleton<IThemeService, ThemeService>();
            services.AddSingleton<IHotKeyService, HotKeyService>();
            services.AddSingleton<IWorkModeConfigurationService, WorkModeConfigurationService>();
            services.AddTransient<IWorkspaceAutoSaveService, WorkspaceAutoSaveService>();
            services.AddSingleton<IWorkspaceConfigService, WorkspaceConfigService>();
            services.AddSingleton<IHashService, HashService>();
            services.AddSingleton<IZipCacheService, ZipCacheService>();
            services.AddSingleton<ICacheUpdateService, CacheUpdateService>();
            services.AddSingleton<IAutoSaveService, AutoSaveService>();
            services.AddSingleton<IDataComparisonService, DataComparisonService>();
            services.AddSingleton<ModuleFactory>();
            services.AddSingleton<IModuleStateCollectorService, ModuleStateCollectorService>();
            services.AddSingleton<WorkModeFactory>();
            services.AddSingleton<WorkModeRegistry>();
            services.AddSingleton<ILocalSettingsStorageService, LocalSettingsStorageService>();

            services.AddSingleton<ProjectTypeRegistry>(sp =>
            {
                var workModeRegistry = sp.GetRequiredService<WorkModeRegistry>();
                return new ProjectTypeRegistry(workModeRegistry);
            });

            services.AddSingleton<MainWindowViewModel>();
            services.AddSingleton<MenuBarViewModel>();
            services.AddSingleton<TabBarViewModel>();
            services.AddSingleton<WorkModeBarViewModel>();
            services.AddSingleton<ModulePanelViewModel>();
            services.AddTransient<WelcomeViewModel>();
            services.AddSingleton<DockFactory>();

            services.AddSingleton<ITabCollection>(sp =>
            {
                var settingsService = sp.GetRequiredService<ISettingsService>();
                return new TabCollection(settingsService);
            });

            services.AddSingleton<IProjectWorkflow, ProjectWorkflow>();

            Services = services.BuildServiceProvider();

            // Инициализируем статический провайдер для модулей.
            // CoreServices используется BaseModule и его наследниками
            // для получения сервисов без прямой зависимости от App.
            // Должно вызываться ДО GetRequiredService<MainWindowViewModel>().
            Writersword.Core.Services.CoreServices.SetProvider(Services);

            var moduleFactory = Services.GetRequiredService<ModuleFactory>();

            // Ищем модули во ВСЕХ загруженных сборках, а не только в GetExecutingAssembly.
            // GetExecutingAssembly() возвращает только Writersword.exe; модули могут
            // быть в отдельных DLL (Writersword.TextEditor.dll и т.д.) которые
            // не попадают в GetTypes() пока сборка не загружена явно.
            var baseModuleType = typeof(BaseModule);

            var executingAssembly = Assembly.GetExecutingAssembly();

            // Модули компилируются в отдельные DLL (Writersword.TextEditor.dll и т.д.).
            // Assembly.GetExecutingAssembly() возвращает только Writersword.exe и не видит их.
            // Явно загружаем все Writersword*.dll из папки приложения, после чего
            // AppDomain.CurrentDomain.GetAssemblies() включает все нужные сборки.
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var dllPath in Directory.GetFiles(appDir, "Writersword*.dll"))
            {
                try { Assembly.LoadFrom(dllPath); }
                catch { }
            }

            var moduleTypes = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a =>
                {
                    try { return a.GetTypes(); }
                    catch (ReflectionTypeLoadException ex)
                    { return ex.Types.Where(t => t != null).Cast<Type>(); }
                    catch { return Array.Empty<Type>(); }
                })
                .Where(t => t != null && !t.IsAbstract && baseModuleType.IsAssignableFrom(t));

            int registeredModules = 0;
            foreach (var moduleType in moduleTypes)
            {
                try
                {
                    var instance = Activator.CreateInstance(moduleType) as BaseModule;
                    if (instance != null)
                    {
                        var capturedType = moduleType;
                        moduleFactory.Register(instance.moduleType, () =>
                            Activator.CreateInstance(capturedType) as BaseModule
                            ?? throw new InvalidOperationException(
                                $"Failed to create module {capturedType.Name}"));
                        registeredModules++;
                        Log.ForContext<App>().Debug(
                            "Module registered: {ModuleType} as {Id}",
                            moduleType.Name, instance.moduleType);
                    }
                    else
                    {
                        Log.ForContext<App>().Warning(
                            "Module instantiation returned null: {Type}", moduleType.Name);
                    }
                }
                catch (Exception ex)
                {
                    Log.ForContext<App>().Error(ex,
                        "Failed to register module: {Type}", moduleType.Name);
                }
            }

            Log.ForContext<App>().Warning("Registered {Count} modules total", registeredModules);

            var workModeFactory = Services.GetRequiredService<WorkModeFactory>();
            var workModeRegistry = Services.GetRequiredService<WorkModeRegistry>();

            var workModeTypes = executingAssembly.GetTypes()
                .Where(t => typeof(IWorkMode).IsAssignableFrom(t)
                         && !t.IsInterface
                         && !t.IsAbstract);

            foreach (var workModeType in workModeTypes)
            {
                var instance = Activator.CreateInstance(workModeType) as IWorkMode;
                if (instance != null)
                    RegisterWorkMode(workModeFactory, workModeRegistry, instance);
            }

            Log.ForContext<App>().Debug("Registered {Count} WorkModes", workModeTypes.Count());

            var projectTypeRegistry = Services.GetRequiredService<ProjectTypeRegistry>();
            projectTypeRegistry.LoadAll();
            Log.ForContext<App>().Debug("Registered {Count} ProjectTypes",
                projectTypeRegistry.GetAll().Count);

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var settingsService = Services.GetRequiredService<ISettingsService>();
                settingsService.Load();

                var localizationService = Services.GetRequiredService<ILocalizationService>();
                localizationService.SetLanguage(settingsService.Language);

                var themeService = Services.GetRequiredService<IThemeService>();
                themeService.SetTheme(settingsService.Theme);

                var mainViewModel = Services.GetRequiredService<MainWindowViewModel>();
                var mainWindow = new MainWindowView
                {
                    DataContext = mainViewModel
                };

                MainWindow = mainWindow;

                var dialogService = Services.GetRequiredService<IDialogService>() as DialogService;
                dialogService?.SetMainWindow(mainWindow);

                desktop.MainWindow = mainWindow;

                desktop.ShutdownRequested += (s, e) =>
                {
                    // Останавливаем AutoSaveService чтобы не запускались новые сохранения
                    // пока приложение уже закрывается.
                    try
                    {
                        var autoSave = Services.GetService<IAutoSaveService>();
                        autoSave?.Disable();

                        Services.GetService<IBackupTimerService>()?.Stop();
                    }
                    catch { }

                    // Кеш .wsasd — страховка от падения, а не хранилище. После штатного
                    // закрытия совпадающий с проектом кеш остаётся лежать и при следующем
                    // запуске подставляется вместо ZIP: allData в SaveDocumentAsync
                    // стартует именно с него. Поэтому чистим здесь.
                    // Ожидание синхронное: ShutdownRequested не поддерживает await, а
                    // после выхода из обработчика приложение уже закрывается. Работа
                    // уходит в пул потоков, UI-контекст не захватывается.
                    try
                    {
                        var workflow = Services.GetRequiredService<IProjectWorkflow>();
                        var tabCollection = Services.GetRequiredService<ITabCollection>();

                        var paths = tabCollection.Tabs?
                            .Select(t => t.FilePath)
                            .Where(p => !string.IsNullOrEmpty(p))
                            .Select(p => p!)
                            .Distinct()
                            .ToList() ?? new List<string>();

                        Task.Run(() => workflow.CleanupCachesAsync(paths)).GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        Log.ForContext<App>().Error(ex, "Cache cleanup on shutdown failed");
                    }

                    // Serilog закрываем здесь а не только в Program.finally —
                    // ShutdownRequested гарантированно вызывается до выхода из main loop.
                    Serilog.Log.CloseAndFlush();
                };

                mainWindow.Opened += async (s, e) =>
                {
                    // Настройки производительности применяются при старте.
                    // Раньше они применялись только из сеттеров окна настроек, то
                    // есть автосохранение в файл проекта не включалось вовсе — пока
                    // человек сам не щёлкал галочку, единственной защитой оставался
                    // кеш .wsasd.
                    try
                    {
                        var perf = settingsService
                            .GetModuleSettings<Writersword.Core.Models.Settings.PerformanceSettings>("performance")
                            ?? new Writersword.Core.Models.Settings.PerformanceSettings();

                        var cacheUpdateService = Services.GetService<ICacheUpdateService>();
                        if (cacheUpdateService != null && perf.CachingIntervalSeconds > 0)
                            cacheUpdateService.SetInterval(TimeSpan.FromSeconds(perf.CachingIntervalSeconds));

                        var autoSaveService = Services.GetService<IAutoSaveService>();
                        if (autoSaveService != null)
                        {
                            autoSaveService.SetInterval(TimeSpan.FromSeconds(
                                Math.Max(0, perf.AutoSaveIntervalSeconds)));
                            autoSaveService.IsEnabled = perf.AutoSaveEnabled;
                        }

                        Log.ForContext<App>().Debug(
                            "Performance settings applied: autoSave={Enabled}/{AutoSaveSec}s, cache={CacheSec}s",
                            perf.AutoSaveEnabled, perf.AutoSaveIntervalSeconds, perf.CachingIntervalSeconds);
                    }
                    catch (Exception ex)
                    {
                        Log.ForContext<App>().Error(ex, "Failed to apply performance settings");
                    }

                    // Таймер истории версий живёт отдельно от автосохранения:
                    // частоту точек задают настройки истории, а не интервал
                    // защиты от падения.
                    try
                    {
                        // Уборка временных копий от прошлых сессий: сравнение
                        // разворачивает точку в полноценный файл проекта, и
                        // после падения он остаётся во временной папке.
                        Services.GetRequiredService<IBackupService>().CleanupTempFiles();

                        Services.GetRequiredService<IBackupTimerService>().Start();
                    }
                    catch (Exception ex)
                    {
                        Log.ForContext<App>().Error(ex, "Failed to start backup timer");
                    }

                    var openProjects = settingsService.OpenProjectPaths;

                    if (openProjects.Count > 0)
                    {
                        var projectWorkflow = Services.GetRequiredService<IProjectWorkflow>();
                        var tabCollection = Services.GetRequiredService<ITabCollection>();

                        Log.ForContext<App>().Debug(
                            "Restoring {Count} projects from last session", openProjects.Count);

                        var tabs = new List<IDocumentTab>();

                        for (int i = 0; i < openProjects.Count; i++)
                        {
                            var projectPath = openProjects[i];

                            if (!File.Exists(projectPath))
                            {
                                Log.ForContext<App>().Warning(
                                    "Project file not found: {Path}", projectPath);
                                continue;
                            }

                            bool initializeWorkspace = (i == 0);

                            var tab = await projectWorkflow.OpenDocumentAsync(
                                projectPath, initializeWorkspace);

                            if (tab != null)
                            {
                                tabs.Add(tab);
                                Log.ForContext<App>().Debug(
                                    "Created tab {Index}/{Total}: {Path} (Initialized: {Init})",
                                    i + 1, openProjects.Count, projectPath, initializeWorkspace);
                            }
                            else
                            {
                                Log.ForContext<App>().Warning(
                                    "Failed to create tab for: {Path}", projectPath);
                            }
                        }

                        foreach (var tab in tabs)
                            tabCollection.Add(tab);

                        if (tabCollection.Tabs.Any())
                        {
                            var firstTab = tabCollection.Tabs.First() as DocumentTabViewModel;
                            tabCollection.ActiveTab = firstTab;
                            Log.ForContext<App>().Debug(
                                "Activated first tab: {Title}", firstTab?.Title ?? "");
                        }
                        else
                        {
                            await ShowWelcomeScreen(mainWindow);
                        }
                    }
                    else
                    {
                        await ShowWelcomeScreen(mainWindow);
                    }
                };
            }

            base.OnFrameworkInitializationCompleted();
        }

        public static ILogger<T> GetLogger<T>()
        {
            if (Services == null)
                return Microsoft.Extensions.Logging.Abstractions.NullLogger<T>.Instance;
            return Services.GetService<ILogger<T>>()
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<T>.Instance;
        }

        /// <summary>
        /// Регистрирует WorkMode в фабрике и реестре.
        /// Вызывается автоматически для всех найденных WorkMode классов.
        /// </summary>
        private void RegisterWorkMode(
            WorkModeFactory factory, WorkModeRegistry registry, IWorkMode workMode)
        {
            factory.Register(workMode.Id, () => workMode);
            registry.Register(workMode);
        }

        /// <summary>
        /// Показать экран приветствия.
        /// Можно вызвать из любого места приложения.
        /// </summary>
        /// <param name="owner">Родительское окно для модального отображения.</param>
        public static async Task ShowWelcomeScreen(Window owner)
        {
            var welcomeViewModel = Services.GetRequiredService<WelcomeViewModel>();
            var welcomeWindow = new WelcomeView
            {
                DataContext = welcomeViewModel
            };

            await welcomeWindow.ShowDialog(owner);
        }
    }
}