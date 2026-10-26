using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reactive;
using Writersword.Resources.Localization;
using Writersword.Core.Interfaces.Services.Storage;
using Writersword.Core.Interfaces.Services.UI;
using Writersword.Core.Interfaces.Services;
using Writersword.Core.Models.Settings;
using Writersword.Infrastructure.Services.Memory;

namespace Writersword.ViewModels.Settings
{
    /// <summary>
    /// ViewModel вкладки General в настройках приложения.
    /// Управляет языком интерфейса, темой оформления,
    /// параметрами автосохранения и фонового кеширования.
    /// Все изменения применяются немедленно — без кнопки Apply.
    /// </summary>
    public class GeneralSettingsViewModel : ReactiveObject
    {
        private readonly ILogger<GeneralSettingsViewModel> _logger;
        private readonly ISettingsService _settingsService;
        private readonly ILocalizationService _localizationService;
        private readonly IThemeService _themeService;

        /// <summary>Язык интерфейса на момент открытия настроек — нужен для определения нужен ли рестарт.</summary>
        private readonly string _initialLanguage;

        private LanguageOption _selectedLanguage;
        private ThemeOption _selectedTheme;
        private bool _restartRequired;

        private bool _cachingEnabled;
        private string _cachingInterval;
        private bool _autoSaveEnabled;
        private string _autoSaveInterval;

        private MemoryLevelOption _selectedMemoryLevel;
        private bool _customMemoryLimitEnabled;
        private int _customMemoryLimitMb;

        // ── Язык ─────────────────────────────────────────────────────────

        /// <summary>Доступные языки интерфейса — название на родном языке и код.</summary>
        public List<LanguageOption> AvailableLanguages { get; } = new()
        {
            new LanguageOption("English", "en"),
            new LanguageOption("Русский", "ru"),
            new LanguageOption("Українська", "uk"),
            new LanguageOption("Հայերեն", "hy")
        };

        /// <summary>Текущий выбранный язык. При изменении применяется немедленно.</summary>
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

        /// <summary>Показывать ли баннер о необходимости перезапуска.</summary>
        public bool RestartRequired
        {
            get => _restartRequired;
            set => this.RaiseAndSetIfChanged(ref _restartRequired, value);
        }

        /// <summary>Команда немедленного перезапуска приложения.</summary>
        public ReactiveCommand<Unit, Unit> RestartNowCommand { get; }

        // ── Тема ──────────────────────────────────────────────────────────

        /// <summary>Доступные темы оформления.</summary>
        public List<ThemeOption> AvailableThemes { get; } = new()
        {
            new ThemeOption(Strings.Settings_General_Theme_Dark, "Dark"),
            new ThemeOption(Strings.Settings_General_Theme_Light, "Light"),
            new ThemeOption(Strings.Settings_General_Theme_Sepia, "Sepia")
        };

        /// <summary>Текущая выбранная тема. При изменении применяется немедленно без перезапуска.</summary>
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

        // ── Кеширование / Автосохранение ──────────────────────────────────

        /// <summary>Предустановленные значения интервала в секундах для ComboBox.</summary>
        public IReadOnlyList<string> IntervalPresets { get; } = new[]
        {
            "5", "10", "15", "20", "30", "45", "60", "90", "120", "180"
        };

        /// <summary>
        /// Включено ли фоновое кеширование.
        /// При выключении немедленно останавливает CacheUpdateService.
        /// </summary>
        public bool CachingEnabled
        {
            get => _cachingEnabled;
            set
            {
                this.RaiseAndSetIfChanged(ref _cachingEnabled, value);
                ApplyCachingEnabled(value);
                SavePerformanceSettings();
            }
        }

        /// <summary>
        /// Интервал кеширования в секундах (строка — поддерживает ручной ввод).
        /// При изменении передаётся в CacheUpdateService.SetInterval().
        /// Вступает в силу при следующем запуске кеша (переключение вкладки).
        /// </summary>
        public string CachingInterval
        {
            get => _cachingInterval;
            set
            {
                this.RaiseAndSetIfChanged(ref _cachingInterval, value);
                ApplyCachingInterval(value);
                SavePerformanceSettings();
            }
        }

        /// <summary>
        /// Включено ли автосохранение.
        /// При изменении немедленно применяется к IAutoSaveService.
        /// </summary>
        public bool AutoSaveEnabled
        {
            get => _autoSaveEnabled;
            set
            {
                this.RaiseAndSetIfChanged(ref _autoSaveEnabled, value);
                ApplyAutoSaveEnabled(value);
                SavePerformanceSettings();
            }
        }

        /// <summary>
        /// Интервал автосохранения в секундах (строка — поддерживает ручной ввод).
        /// При изменении передаётся в IAutoSaveService.SetInterval().
        /// </summary>
        public string AutoSaveInterval
        {
            get => _autoSaveInterval;
            set
            {
                this.RaiseAndSetIfChanged(ref _autoSaveInterval, value);
                ApplyAutoSaveInterval(value);
                SavePerformanceSettings();
            }
        }

        // ── Память ────────────────────────────────────────────────────────

        /// <summary>Уровни расхода оперативной памяти.</summary>
        public List<MemoryLevelOption> MemoryLevels { get; } = new()
        {
            new MemoryLevelOption(Strings.Settings_General_Memory_Economy, MemoryLevel.Economy),
            new MemoryLevelOption(Strings.Settings_General_Memory_Normal, MemoryLevel.Normal),
            new MemoryLevelOption(Strings.Settings_General_Memory_Fast, MemoryLevel.Fast)
        };

        /// <summary>
        /// Выбранный уровень. Применяется сразу: срок жизни неактивных вкладок и
        /// посчитанный лимит меняются на следующем тике выгрузки.
        /// </summary>
        public MemoryLevelOption SelectedMemoryLevel
        {
            get => _selectedMemoryLevel;
            set
            {
                if (value == null || _selectedMemoryLevel == value) return;
                this.RaiseAndSetIfChanged(ref _selectedMemoryLevel, value);
                SavePerformanceSettings();
                RaiseMemoryLimitChanged();
            }
        }

        /// <summary>
        /// Свой лимит памяти (дополнительная настройка). Пока выключено, ползунок
        /// показывает лимит, посчитанный от объёма памяти и уровня. При включении
        /// ползунок начинает с этого же значения.
        /// </summary>
        public bool CustomMemoryLimitEnabled
        {
            get => _customMemoryLimitEnabled;
            set
            {
                if (_customMemoryLimitEnabled == value) return;
                if (value && _customMemoryLimitMb < MemoryBudget.MinLimitMb)
                    _customMemoryLimitMb = MemoryBudget.AutoLimitMb(_selectedMemoryLevel.Level);

                this.RaiseAndSetIfChanged(ref _customMemoryLimitEnabled, value);
                SavePerformanceSettings();
                RaiseMemoryLimitChanged();
            }
        }

        /// <summary>Наименьшее значение ползунка лимита, МБ.</summary>
        public double MemoryLimitMinimum => MemoryBudget.MinLimitMb;

        /// <summary>Наибольшее значение ползунка лимита — вся память компьютера, МБ.</summary>
        public double MemoryLimitMaximum => MemoryBudget.SliderMaximumMb;

        /// <summary>Шаг ползунка лимита, МБ.</summary>
        public double MemoryLimitStep => MemoryBudget.LimitStepMb;

        /// <summary>
        /// Значение ползунка: свой лимит, если он включён, иначе посчитанный.
        /// Двигать ползунок можно только при включённом своём лимите.
        /// </summary>
        public double MemoryLimitMb
        {
            get => _customMemoryLimitEnabled
                ? _customMemoryLimitMb
                : MemoryBudget.AutoLimitMb(_selectedMemoryLevel.Level);
            set
            {
                if (!_customMemoryLimitEnabled) return;

                int mb = MemoryBudget.RoundToStep((int)Math.Round(value));
                mb = Math.Clamp(mb, MemoryBudget.MinLimitMb, MemoryBudget.SliderMaximumMb);
                if (mb == _customMemoryLimitMb) return;

                _customMemoryLimitMb = mb;
                SavePerformanceSettings();
                RaiseMemoryLimitChanged();
            }
        }

        /// <summary>Подпись значения лимита: «1024 МБ» или «1024 МБ · авто».</summary>
        public string MemoryLimitText
        {
            get
            {
                var text = string.Format(Strings.Settings_General_Memory_LimitFormat, (int)MemoryLimitMb);
                return _customMemoryLimitEnabled
                    ? text
                    : text + " · " + Strings.Settings_General_Memory_Auto;
            }
        }

        private void RaiseMemoryLimitChanged()
        {
            this.RaisePropertyChanged(nameof(MemoryLimitMb));
            this.RaisePropertyChanged(nameof(MemoryLimitText));
        }

        // ── Конструктор ───────────────────────────────────────────────────

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

            // Загружаем сохранённые настройки производительности, либо дефолты.
            var perf = _settingsService.GetModuleSettings<PerformanceSettings>("performance")
                       ?? new PerformanceSettings();

            _cachingEnabled = perf.CachingEnabled;
            _cachingInterval = perf.CachingIntervalSeconds.ToString();
            _autoSaveEnabled = perf.AutoSaveEnabled;
            _autoSaveInterval = perf.AutoSaveIntervalSeconds.ToString();

            _selectedMemoryLevel = MemoryLevels.Find(o => (int)o.Level == perf.MemoryLevel)
                                   ?? MemoryLevels.Find(o => o.Level == MemoryLevel.Normal)!;
            _customMemoryLimitEnabled = perf.CustomMemoryLimitEnabled;
            _customMemoryLimitMb = perf.CustomMemoryLimitMb;
        }

        // ── Применение язык / тема ────────────────────────────────────────

        /// <summary>Сохраняет язык и устанавливает флаг перезапуска если язык изменился.</summary>
        private void ApplyLanguage(string languageCode)
        {
            _settingsService.Language = languageCode;
            RestartRequired = languageCode != _initialLanguage;
            _logger.LogDebug("Language selected: {Language}, restart required: {RestartRequired}",
                languageCode, RestartRequired);
        }

        /// <summary>Применяет тему немедленно без перезапуска.</summary>
        private void ApplyTheme(string themeCode)
        {
            _settingsService.Theme = themeCode;
            _themeService.SetTheme(themeCode);
            _logger.LogDebug("Theme changed to: {Theme}", themeCode);
        }

        // ── Применение кеширования ────────────────────────────────────────

        /// <summary>
        /// При выключении останавливает текущую сессию кеширования.
        /// При включении — кеш запустится автоматически при следующей активации вкладки.
        /// </summary>
        private void ApplyCachingEnabled(bool enabled)
        {
            try
            {
                var cacheService = App.Services.GetService<ICacheUpdateService>();
                if (cacheService is null) return;

                if (!enabled)
                    cacheService.Stop();

                _logger.LogDebug("Caching enabled set to: {Value}", enabled);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error applying caching enabled");
            }
        }

        /// <summary>
        /// Передаёт новый интервал в CacheUpdateService.
        /// Вступает в силу при следующем Start() (переключение вкладки).
        /// </summary>
        private void ApplyCachingInterval(string value)
        {
            try
            {
                int seconds = ParseInterval(value, 10);
                var cacheService = App.Services.GetService<ICacheUpdateService>();
                cacheService?.SetInterval(TimeSpan.FromSeconds(seconds));
                _logger.LogDebug("Caching interval set to: {Seconds}s", seconds);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error applying caching interval");
            }
        }

        // ── Применение автосохранения ─────────────────────────────────────

        /// <summary>Включает или выключает автосохранение немедленно.</summary>
        private void ApplyAutoSaveEnabled(bool enabled)
        {
            try
            {
                var autoSave = App.Services.GetService<IAutoSaveService>();
                if (autoSave is null) return;

                autoSave.IsEnabled = enabled;
                _logger.LogDebug("AutoSave enabled set to: {Value}", enabled);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error applying autosave enabled");
            }
        }

        /// <summary>Передаёт новый интервал в IAutoSaveService.</summary>
        private void ApplyAutoSaveInterval(string value)
        {
            try
            {
                int seconds = ParseInterval(value, 120);
                var autoSave = App.Services.GetService<IAutoSaveService>();
                autoSave?.SetInterval(TimeSpan.FromSeconds(seconds));
                _logger.LogDebug("AutoSave interval set to: {Seconds}s", seconds);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error applying autosave interval");
            }
        }

        // ── Сохранение ────────────────────────────────────────────────────

        /// <summary>
        /// Сохраняет текущие значения производительности в ISettingsService.
        /// Вызывается после каждого изменения свойства.
        /// </summary>
        private void SavePerformanceSettings()
        {
            try
            {
                var settings = new PerformanceSettings
                {
                    CachingEnabled = _cachingEnabled,
                    CachingIntervalSeconds = ParseInterval(_cachingInterval, 10),
                    AutoSaveEnabled = _autoSaveEnabled,
                    AutoSaveIntervalSeconds = ParseInterval(_autoSaveInterval, 120),
                    MemoryLevel = (int)_selectedMemoryLevel.Level,
                    CustomMemoryLimitEnabled = _customMemoryLimitEnabled,
                    CustomMemoryLimitMb = _customMemoryLimitMb
                };
                _settingsService.SaveModuleSettings("performance", settings);
                _settingsService.Save();

                // Бюджет памяти применяется сразу: выгрузка вкладок читает его на каждом тике.
                MemoryBudget.Apply(settings);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving performance settings");
            }
        }

        // ── Перезапуск ────────────────────────────────────────────────────

        /// <summary>
        /// Перезапускает приложение: сначала запускает новый процесс,
        /// затем закрывает главное окно через штатный механизм.
        /// OnClosing проверит несохранённые изменения перед закрытием.
        /// </summary>
        private void RestartApplication()
        {
            _logger.LogDebug("Restart requested for language change");

            if (Application.Current?.ApplicationLifetime
                is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow != null)
            {
                var executablePath = Process.GetCurrentProcess().MainModule?.FileName;

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

                desktop.MainWindow.Close();
            }
        }

        // ── Хелперы ───────────────────────────────────────────────────────

        /// <summary>
        /// Парсит строку в секунды.
        /// Возвращает fallback если значение не является положительным числом.
        /// </summary>
        private static int ParseInterval(string? value, int fallback)
        {
            if (int.TryParse(value?.Trim(), out int result) && result > 0)
                return result;
            return fallback;
        }
    }

    /// <summary>Опция языка интерфейса — название на родном языке и ISO-код.</summary>
    public class LanguageOption
    {
        public string DisplayName { get; }
        public string Code { get; }

        public LanguageOption(string displayName, string code)
        {
            DisplayName = displayName;
            Code = code;
        }
    }

    /// <summary>Уровень расхода памяти — отображаемое название и значение.</summary>
    public class MemoryLevelOption
    {
        public string DisplayName { get; }
        public MemoryLevel Level { get; }

        public MemoryLevelOption(string displayName, MemoryLevel level)
        {
            DisplayName = displayName;
            Level = level;
        }
    }

    /// <summary>Опция темы оформления — отображаемое название и внутренний код.</summary>
    public class ThemeOption
    {
        public string DisplayName { get; }
        public string Code { get; }

        public ThemeOption(string displayName, string code)
        {
            DisplayName = displayName;
            Code = code;
        }
    }
}