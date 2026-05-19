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
            AvaloniaXamlLoader.Load(this);
        }

        /// <summary>
        /// Основная инициализация приложения.
        /// Здесь настраивается DI, создаётся главное окно, регистрируются модули.
        /// </summary>
        public override void OnFrameworkInitializationCompleted()
        {
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
            Core.Services.CoreServices.SetProvider(Services);

            // Принудительно загружаем все Writersword.*.dll из папки приложения.
            // Referenced сборки без прямого обращения к их типам не попадают в AppDomain
            // до первого использования — при переходе на микросервисы модули живут
            // в отдельных dll и GetExecutingAssembly() их не видит.
            var appDir = AppContext.BaseDirectory;
            foreach (var dll in Directory.GetFiles(appDir, "Writersword.*.dll"))
            {
                try { Assembly.LoadFrom(dll); }
                catch (Exception ex)
                {
                    Log.ForContext<App>().Warning(
                        "Could not load assembly {Dll}: {Message}", dll, ex.Message);
                }
            }

            // Собираем все загруженные сборки с префиксом Writersword для сканирования типов.
            var allWriterswordAssemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name?.StartsWith("Writersword") == true)
                .ToList();

            var moduleFactory = Services.GetRequiredService<ModuleFactory>();

            var moduleTypes = allWriterswordAssemblies
                .SelectMany(a =>
                {
                    try { return a.GetTypes(); }
                    catch { return Array.Empty<Type>(); }
                })
                .Where(t => typeof(BaseModule).IsAssignableFrom(t) && !t.IsAbstract)
                .ToList();

            foreach (var moduleType in moduleTypes)
            {
                var instance = Activator.CreateInstance(moduleType) as BaseModule;
                if (instance != null)
                {
                    var capturedType = moduleType;
                    moduleFactory.Register(instance.moduleType, () =>
                        Activator.CreateInstance(capturedType) as BaseModule
                        ?? throw new InvalidOperationException(
                            $"Failed to create module {capturedType.Name}"));
                }
            }

            Log.ForContext<App>().Debug("Registered {Count} modules", moduleTypes.Count);

            var workModeFactory = Services.GetRequiredService<WorkModeFactory>();
            var workModeRegistry = Services.GetRequiredService<WorkModeRegistry>();

            var workModeTypes = allWriterswordAssemblies
                .SelectMany(a =>
                {
                    try { return a.GetTypes(); }
                    catch { return Array.Empty<Type>(); }
                })
                .Where(t => typeof(IWorkMode).IsAssignableFrom(t)
                         && !t.IsInterface
                         && !t.IsAbstract)
                .ToList();

            foreach (var workModeType in workModeTypes)
            {
                var instance = Activator.CreateInstance(workModeType) as IWorkMode;
                if (instance != null)
                    RegisterWorkMode(workModeFactory, workModeRegistry, instance);
            }

            Log.ForContext<App>().Debug("Registered {Count} WorkModes", workModeTypes.Count);

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

                mainWindow.Opened += async (s, e) =>
                {
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