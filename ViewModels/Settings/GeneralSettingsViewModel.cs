using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reactive;
using System.Reflection;
using Writersword.Resources.Localization;
using Writersword.Src.Core.Interfaces.Services.Storage;
using Writersword.Src.Core.Interfaces.Services.UI;

namespace Writersword.ViewModels.Settings
{
    /// <summary>
    /// ViewModel общих настроек приложения
    /// Управляет выбором языка интерфейса
    /// </summary>
    public class GeneralSettingsViewModel : ReactiveObject
    {
        private readonly ILogger<GeneralSettingsViewModel> _logger;
        private readonly ISettingsService _settingsService;
        private readonly ILocalizationService _localizationService;
        private readonly string _initialLanguage;
        private LanguageOption _selectedLanguage;
        private bool _restartRequired;

        /// <summary>Доступные языки интерфейса — название на родном языке и код</summary>
        public List<LanguageOption> AvailableLanguages { get; } = new()
        {
            new LanguageOption("English", "en"),
            new LanguageOption("Русский", "ru"),
            new LanguageOption("Українська", "uk")
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

            _initialLanguage = _localizationService.CurrentLanguage;

            var currentCode = _settingsService.Language;
            _selectedLanguage = AvailableLanguages.Find(l => l.Code == currentCode)
                                ?? AvailableLanguages[0];

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

        /// <summary>Перезапустить приложение — запустить новый процесс и завершить текущий</summary>
        private void RestartApplication()
        {
            _logger.LogDebug("Restarting application for language change");

            var executablePath = Process.GetCurrentProcess().MainModule?.FileName;

            if (!string.IsNullOrEmpty(executablePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = executablePath,
                    UseShellExecute = true
                });
            }

            System.Environment.Exit(0);
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
}