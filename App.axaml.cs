//using Avalonia;
//using Avalonia.Controls.ApplicationLifetimes;
//using Avalonia.Markup.Xaml;
//using Microsoft.Extensions.DependencyInjection;
//using System;
//using Writersword.ViewModels;
//using Writersword.Views;
//using Writersword.Services;
//using Writersword.Layout;
//using Writersword.ViewModels.Docks;
//using Writersword.ViewModels.Documents;

//// Модули
//using Writersword.Modules.Characters.Services;
//using Writersword.Modules.Timeline.Services;
//using Writersword.Modules.Synonyms.Services;
//using Writersword.Modules.Poetry.Services;
//using Writersword.Modules.Dialogues.Services;
//using Writersword.Modules.GameEconomy.Services;
//using Writersword.Modules.Statistics.Services;
//using Writersword.Modules.Music.Services;
//using Writersword.Modules.Notes.Services;

//// Core и Infrastructure
//using Writersword.Core.Interfaces.Services;
//using Writersword.Infrastructure.Services.Document;
//using Writersword.Infrastructure.Services.Storage;
//using Writersword.Infrastructure.Services.Serialization;
//using Writersword.Infrastructure.Services.Migration;
//using Writersword.Infrastructure.Repositories;

//namespace Writersword;

//public partial class App : Application
//{
//    /// <summary>
//    /// Глобальный DI контейнер
//    /// Доступен из любого места приложения
//    /// </summary>
//    public static IServiceProvider Services { get; private set; } = null!;

//    public override void Initialize()
//    {
//        AvaloniaXamlLoader.Load(this);
//    }

//    public override void OnFrameworkInitializationCompleted()
//    {
//        // ========================================
//        // Настройка Dependency Injection
//        // ========================================

//        var services = new ServiceCollection();
//        ConfigureServices(services);
//        Services = services.BuildServiceProvider();

//        // ========================================
//        // Загрузка настроек
//        // ========================================

//        var settingsService = Services.GetRequiredService<ISettingsService>();
//        settingsService.Load();

//        // ========================================
//        // Установка языка
//        // ========================================

//        var localizationService = Services.GetRequiredService<LocalizationService>();
//        localizationService.SetLanguage(settingsService.Language);

//        // ========================================
//        // Установка темы
//        // ========================================

//        var themeService = Services.GetRequiredService<IThemeService>();
//        themeService.SetTheme(settingsService.Theme);

//        // ========================================
//        // Создание главного окна
//        // ========================================

//        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
//        {
//            var mainViewModel = Services.GetRequiredService<MainViewModel>();

//            var mainWindow = new MainWindow
//            {
//                DataContext = mainViewModel
//            };

//            // Регистрируем окно в DialogService (для диалогов)
//            var dialogService = Services.GetRequiredService<IDialogService>() as DialogService;
//            dialogService?.SetMainWindow(mainWindow);

//            desktop.MainWindow = mainWindow;
//        }

//        base.OnFrameworkInitializationCompleted();
//    }

//    /// <summary>
//    /// Регистрация всех сервисов в DI контейнере
//    /// </summary>
//    private void ConfigureServices(IServiceCollection services)
//    {
//        // ========================================
//        // UI Services (сервисы интерфейса)
//        // ========================================

//        services.AddSingleton<IDialogService, DialogService>();
//        services.AddSingleton<IThemeService, ThemeService>();
//        services.AddSingleton<LocalizationService>();
//        services.AddSingleton<ISettingsService, SettingsService>();

//        // ========================================
//        // Dock System
//        // ========================================

//        services.AddSingleton<DockFactory>();
//        services.AddSingleton<LayoutSerializer>();

//        // ========================================
//        // ViewModels - Main
//        // ========================================

//        services.AddSingleton<MainViewModel>();

//        // ========================================
//        // ViewModels - Docks (панели)
//        // ========================================

//        services.AddTransient<CharactersDockViewModel>();
//        services.AddTransient<TimelineDockViewModel>();
//        services.AddTransient<NotesDockViewModel>();
//        services.AddTransient<PropertiesDockViewModel>();
//        services.AddTransient<StatisticsDockViewModel>();
//        services.AddTransient<OutputDockViewModel>();

//        // ========================================
//        // ViewModels - Documents (документы)
//        // ========================================

//        services.AddTransient<EditorDocumentViewModel>();
//        services.AddTransient<CharacterEditorViewModel>();
//        services.AddTransient<TimelineEditorViewModel>();

//        // ========================================
//        // Core Services (интерфейсы)
//        // ========================================

//        services.AddSingleton<IDocumentService, DocumentService>();

//        // ========================================
//        // Module Services (модули)
//        // ========================================

//        services.AddSingleton<CharacterService>();
//        services.AddSingleton<TimelineService>();
//        services.AddSingleton<SynonymService>();
//        services.AddSingleton<PoetryService>();
//        services.AddSingleton<DialogueService>();
//        services.AddSingleton<EconomyCalculator>();
//        services.AddSingleton<StatisticsService>();
//        services.AddSingleton<MusicPlayer>();
//        services.AddSingleton<NotesService>();

//        // ========================================
//        // Infrastructure Services (реализации)
//        // ========================================

//        services.AddSingleton<IFileStorage, FileStorage>();
//        services.AddSingleton<IJsonSerializer, JsonSerializerService>();
//        services.AddSingleton<IVersionMigrator, VersionMigrator>();

//        // ========================================
//        // Repositories
//        // ========================================

//        services.AddSingleton<IDocumentRepository, DocumentRepository>();
//        services.AddSingleton<IProjectRepository, ProjectRepository>();
//    }
//}

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using System;
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

            // Регистрация ViewModels
            services.AddSingleton<MainWindowViewModel>();

            // Создаём контейнер
            Services = services.BuildServiceProvider();

            // ========================================
            // Создание главного окна
            // ========================================
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var mainViewModel = Services.GetRequiredService<MainWindowViewModel>();
                var mainWindow = new MainWindow
                {
                    DataContext = mainViewModel
                };

                // Регистрируем окно в DialogService
                var dialogService = Services.GetRequiredService<IDialogService>() as DialogService;
                dialogService?.SetMainWindow(mainWindow);

                desktop.MainWindow = mainWindow;
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
