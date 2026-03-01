using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reactive;
using Writersword.Resources.Localization;
using Writersword.Src.Core.Interfaces.Services.Storage;
using Writersword.Src.Core.Interfaces.Services.UI;

namespace Writersword.ViewModels.Settings
{
    /// <summary>
    /// ViewModel общих настроек приложения
    /// Управляет выбором языка и темы интерфейса
    /// </summary>
    public class GeneralSettingsViewModel : ReactiveObject
    {
        private readonly ILogger<GeneralSettingsViewModel> _logger;
        private readonly ISettingsService _settingsService;
        private readonly ILocalizationService _localizationService;
        private readonly IThemeService _themeService;
        private readonly string _initialLanguage;
        private LanguageOption _selectedLanguage;
        private ThemeOption _selectedTheme;
        private bool _restartRequired;

        /// <summary>Доступные языки интерфейса — название на родном языке и код</summary>
        public List<LanguageOption> AvailableLanguages { get; } = new()
        {
            new LanguageOption("English", "en"),
            new LanguageOption("Русский", "ru"),
            new LanguageOption("Українська", "uk"),
            new LanguageOption("Հայերեն", "hy")
        };

        /// <summary>Доступные темы оформления</summary>
        public List<ThemeOption> AvailableThemes { get; } = new()
        {
            new ThemeOption(Strings.Settings_General_Theme_Dark, "Dark"),
            new ThemeOption(Strings.Settings_General_Theme_Light, "Light"),
            new ThemeOption(Strings.Settings_General_Theme_Sepia, "Sepia")
        };

        /// <summary>Текущий выбранный язык</summary>
        public LanguageOption SelectedLanguage
        {
            get => _selectedLanguage;
            set
            {
                if (_selectedLanguage == value) return;
                this.RaiseAndSetIfChanged(ref _selectedLanguage, value);
                ApplyLanguage(value.Code);
            }
        }

        /// <summary>Текущая выбранная тема</summary>
        public ThemeOption SelectedTheme
        {
            get => _selectedTheme;
            set
            {
                if (_selectedTheme == value) return;
                this.RaiseAndSetIfChanged(ref _selectedTheme, value);
                ApplyTheme(value.Code);
            }
        }

        /// <summary>Нужен ли перезапуск для применения языка</summary>
        public bool RestartRequired
        {
            get => _restartRequired;
            set => this.RaiseAndSetIfChanged(ref _restartRequired, value);
        }

        /// <summary>Команда немедленного перезапуска приложения</summary>
        public ReactiveCommand<Unit, Unit> RestartNowCommand { get; }

        public GeneralSettingsViewModel()
        {
            _logger = App.Services.GetService<ILogger<GeneralSettingsViewModel>>()!;
            _settingsService = App.Services.GetRequiredService<ISettingsService>();
            _localizationService = App.Services.GetRequiredService<ILocalizationService>();
            _themeService = App.Services.GetRequiredService<IThemeService>();

            _initialLanguage = _localizationService.CurrentLanguage;

            var currentLanguageCode = _settingsService.Language;
            _selectedLanguage = AvailableLanguages.Find(l => l.Code == currentLanguageCode)
                                ?? AvailableLanguages[0];

            var currentThemeCode = _settingsService.Theme;
            _selectedTheme = AvailableThemes.Find(t => t.Code == currentThemeCode)
                             ?? AvailableThemes[0];

            _restartRequired = false;

            RestartNowCommand = ReactiveCommand.Create(RestartApplication);
        }

        /// <summary>Применить выбранный язык — сохранить в настройки и показать предупреждение если язык изменился</summary>
        private void ApplyLanguage(string languageCode)
        {
            _settingsService.Language = languageCode;
            RestartRequired = languageCode != _initialLanguage;
            _logger.LogDebug("Language selected: {Language}, restart required: {RestartRequired}", languageCode, RestartRequired);
        }

        /// <summary>Применить выбранную тему — сменить сразу без перезапуска</summary>
        private void ApplyTheme(string themeCode)
        {
            _settingsService.Theme = themeCode;
            _themeService.SetTheme(themeCode);
            _logger.LogDebug("Theme changed to: {Theme}", themeCode);
        }

        /// <summary>
        /// Перезапустить приложение — сначала запускает новый процесс,
        /// затем закрывает главное окно через штатный механизм.
        /// OnClosing сам проверит несохранённые изменения и предложит сохранить.
        /// Если пользователь отменит закрытие — новый процесс останется висеть,
        /// поэтому запускаем его только после того как окно начало закрываться.
        /// </summary>
        private void RestartApplication()
        {
            _logger.LogDebug("Restart requested for language change");

            if (Application.Current?.ApplicationLifetime
                is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow != null)
            {
                // Сохраняем путь до закрытия процесса
                var executablePath = Process.GetCurrentProcess().MainModule?.FileName;

                // Подписываемся на закрытие окна — новый процесс запустим только когда окно реально закроется
                desktop.MainWindow.Closed += (_, _) =>
                {
                    if (!string.IsNullOrEmpty(executablePath))
                    {
                        _logger.LogDebug("Launching new process: {Path}", executablePath);
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = executablePath,
                            UseShellExecute = true
                        });
                    }
                };

                // Закрываем окно через штатный механизм — OnClosing проверит несохранённые изменения
                desktop.MainWindow.Close();
            }
        }
    }

    /// <summary>
    /// Опция языка интерфейса
    /// </summary>
    public class LanguageOption
    {
        /// <summary>Название языка на родном языке</summary>
        public string DisplayName { get; }

        /// <summary>Код языка (en, ru, uk)</summary>
        public string Code { get; }

        public LanguageOption(string displayName, string code)
        {
            DisplayName = displayName;
            Code = code;
        }
    }

    /// <summary>
    /// Опция темы оформления
    /// </summary>
    public class ThemeOption
    {
        /// <summary>Название темы</summary>
        public string DisplayName { get; }

        /// <summary>Код темы (Dark, Light, Sepia)</summary>
        public string Code { get; }

        public ThemeOption(string displayName, string code)
        {
            DisplayName = displayName;
            Code = code;
        }
    }
}