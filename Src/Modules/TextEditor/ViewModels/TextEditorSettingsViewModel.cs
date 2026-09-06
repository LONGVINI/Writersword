using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Writersword.Core.Enums;
using Writersword.Core.Models.Settings;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Settings;
using Writersword.Modules.TextEditor.Services;

namespace Writersword.Modules.TextEditor.ViewModels
{
    /// <summary>
    /// Одна запись в таблице шрифтов по скриптам.
    /// Реактивна — изменение FontFamily сразу отражается в UI.
    /// </summary>
    public sealed class ScriptFontEntry : ReactiveObject
    {
        private string _fontFamily;

        /// <summary>Системный ключ скрипта (используется в ScriptFontMap).</summary>
        public string ScriptKey { get; }

        /// <summary>Название скрипта для отображения пользователю.</summary>
        public string DisplayName { get; }

        public string FontFamily
        {
            get => _fontFamily;
            set => this.RaiseAndSetIfChanged(ref _fontFamily, value);
        }

        public ScriptFontEntry(string scriptKey, string displayName, string fontFamily)
        {
            ScriptKey = scriptKey;
            DisplayName = displayName;
            _fontFamily = fontFamily;
        }
    }

    public sealed class TextEditorSettingsViewModel : ReactiveObject
    {
        public SettingsFieldContext Context { get; }

        public SettingValue<string> FontFamily { get; }
        public SettingValue<double> FontSize { get; }
        public SettingValue<bool> SpellCheckEnabled { get; }
        public SettingValue<string> DefaultLanguage { get; }
        public SettingValue<bool> ShowSpellErrors { get; }
        public SettingValue<bool> AutoReplaceEnabled { get; }
        public SettingValue<bool> ShowRuler { get; }
        public SettingValue<bool> ShowFormattingMarks { get; }
        public SettingValue<EditorViewMode> DefaultViewMode { get; }
        public SettingValue<double> DefaultZoom { get; }
        public SettingValue<int> AutoSaveIntervalSeconds { get; }
        public SettingValue<double> MonitorSizeInches { get; }
        public SettingValue<bool> BreakOnHyphen { get; }
        public SettingValue<bool> SubstituteMissingGlyphs { get; }
        public SettingValue<string> SubstituteFontFamily { get; }

        /// <summary>
        /// Реактивная коллекция пар "скрипт → шрифт" для редактирования в UI.
        /// При вызове GetSettings() агрегируется обратно в Dictionary.
        /// </summary>
        public ObservableCollection<ScriptFontEntry> ScriptFonts { get; }

        // ── Прокси для NumericUpDown (decimal?) ───────────────────────────

        /// <summary>Масштаб в процентах — для слайдера и NumericUpDown.</summary>
        public decimal? ZoomPercent
        {
            get => (decimal)Math.Round(DefaultZoom.Value * 100);
            set
            {
                if (value is null) return;
                int clamped = Math.Clamp((int)value.Value, 25, 500);
                DefaultZoom.Value = clamped / 100.0;
            }
        }

        /// <summary>Прокси для FontSize — NumericUpDown работает с decimal?.</summary>
        public decimal? FontSizeProxy
        {
            get => (decimal)FontSize.Value;
            set { if (value is not null) FontSize.Value = (double)value.Value; }
        }

        /// <summary>Прокси для AutoSaveIntervalSeconds — NumericUpDown работает с decimal?.</summary>
        public decimal? AutoSaveSecondsProxy
        {
            get => AutoSaveIntervalSeconds.Value;
            set { if (value is not null) AutoSaveIntervalSeconds.Value = (int)value.Value; }
        }

        // ── Справочники ───────────────────────────────────────────────────

        public IReadOnlyList<string> AvailableFonts { get; } = new[]
        {
            "Arial", "Times New Roman", "Calibri", "Georgia",
            "Verdana", "Tahoma", "Trebuchet MS", "Consolas", "Courier New"
        };

        public IReadOnlyList<string> AvailableLanguages { get; } = new[]
        {
            "ru", "uk", "en"
        };

        public IReadOnlyList<EditorViewMode> ViewModes { get; } = new[]
        {
            EditorViewMode.Page,
            EditorViewMode.Draft
        };

        /// <summary>
        /// Список для выбора шрифта подстановки.
        ///
        /// Строится по установленным в системе и вшитым в проект гарнитурам, а не
        /// по короткому списку AvailableFonts: подставлять предлагается то, что на
        /// этой машине действительно есть. Нынешнее значение в список кладётся
        /// всегда, даже если шрифта на машине нет, — иначе ComboBox не нашёл бы
        /// совпадения и молча стёр бы выбор при открытии настроек.
        /// </summary>
        public IReadOnlyList<string> SubstituteFontChoices { get; }

        // ── Конструкторы ──────────────────────────────────────────────────

        /// <summary>Глобальная вкладка — current == global.</summary>
        public TextEditorSettingsViewModel(
            TextEditorSettings hardcoded,
            TextEditorSettings global)
        {
            Context = SettingsFieldContext.Global;

            FontFamily = new SettingValue<string>(hardcoded.FontFamily, global.FontFamily);
            FontSize = new SettingValue<double>(hardcoded.FontSize, global.FontSize);
            SpellCheckEnabled = new SettingValue<bool>(hardcoded.SpellCheckEnabled, global.SpellCheckEnabled);
            DefaultLanguage = new SettingValue<string>(hardcoded.DefaultLanguage, global.DefaultLanguage);
            ShowSpellErrors = new SettingValue<bool>(hardcoded.ShowSpellErrors, global.ShowSpellErrors);
            AutoReplaceEnabled = new SettingValue<bool>(hardcoded.AutoReplaceEnabled, global.AutoReplaceEnabled);
            ShowRuler = new SettingValue<bool>(hardcoded.ShowRuler, global.ShowRuler);
            ShowFormattingMarks = new SettingValue<bool>(hardcoded.ShowFormattingMarks, global.ShowFormattingMarks);
            DefaultViewMode = new SettingValue<EditorViewMode>(hardcoded.DefaultViewMode, global.DefaultViewMode);
            DefaultZoom = new SettingValue<double>(hardcoded.DefaultZoom, global.DefaultZoom);
            AutoSaveIntervalSeconds = new SettingValue<int>(hardcoded.AutoSaveIntervalSeconds, global.AutoSaveIntervalSeconds);
            MonitorSizeInches = new SettingValue<double>(hardcoded.MonitorSizeInches, global.MonitorSizeInches);
            BreakOnHyphen = new SettingValue<bool>(hardcoded.BreakOnHyphen, global.BreakOnHyphen);
            SubstituteMissingGlyphs = new SettingValue<bool>(hardcoded.SubstituteMissingGlyphs, global.SubstituteMissingGlyphs);
            SubstituteFontFamily = new SettingValue<string>(hardcoded.SubstituteFontFamily, global.SubstituteFontFamily);
            ScriptFonts = BuildScriptFonts(global.ScriptFontMap);
            SubstituteFontChoices = ProjectFonts.PickerFamilies(SubstituteFontFamily.Value);

            WireProxies();
        }

        /// <summary>Локальная вкладка — current = локальные настройки проекта.</summary>
        public TextEditorSettingsViewModel(
            TextEditorSettings hardcoded,
            TextEditorSettings global,
            TextEditorSettings current)
        {
            Context = SettingsFieldContext.Local;

            FontFamily = new SettingValue<string>(hardcoded.FontFamily, global.FontFamily, current.FontFamily);
            FontSize = new SettingValue<double>(hardcoded.FontSize, global.FontSize, current.FontSize);
            SpellCheckEnabled = new SettingValue<bool>(hardcoded.SpellCheckEnabled, global.SpellCheckEnabled, current.SpellCheckEnabled);
            DefaultLanguage = new SettingValue<string>(hardcoded.DefaultLanguage, global.DefaultLanguage, current.DefaultLanguage);
            ShowSpellErrors = new SettingValue<bool>(hardcoded.ShowSpellErrors, global.ShowSpellErrors, current.ShowSpellErrors);
            AutoReplaceEnabled = new SettingValue<bool>(hardcoded.AutoReplaceEnabled, global.AutoReplaceEnabled, current.AutoReplaceEnabled);
            ShowRuler = new SettingValue<bool>(hardcoded.ShowRuler, global.ShowRuler, current.ShowRuler);
            ShowFormattingMarks = new SettingValue<bool>(hardcoded.ShowFormattingMarks, global.ShowFormattingMarks, current.ShowFormattingMarks);
            DefaultViewMode = new SettingValue<EditorViewMode>(hardcoded.DefaultViewMode, global.DefaultViewMode, current.DefaultViewMode);
            DefaultZoom = new SettingValue<double>(hardcoded.DefaultZoom, global.DefaultZoom, current.DefaultZoom);
            AutoSaveIntervalSeconds = new SettingValue<int>(hardcoded.AutoSaveIntervalSeconds, global.AutoSaveIntervalSeconds, current.AutoSaveIntervalSeconds);
            MonitorSizeInches = new SettingValue<double>(hardcoded.MonitorSizeInches, global.MonitorSizeInches, current.MonitorSizeInches);
            BreakOnHyphen = new SettingValue<bool>(hardcoded.BreakOnHyphen, global.BreakOnHyphen, current.BreakOnHyphen);
            SubstituteMissingGlyphs = new SettingValue<bool>(hardcoded.SubstituteMissingGlyphs, global.SubstituteMissingGlyphs, current.SubstituteMissingGlyphs);
            SubstituteFontFamily = new SettingValue<string>(hardcoded.SubstituteFontFamily, global.SubstituteFontFamily, current.SubstituteFontFamily);
            ScriptFonts = BuildScriptFonts(current.ScriptFontMap);
            SubstituteFontChoices = ProjectFonts.PickerFamilies(SubstituteFontFamily.Value, global.SubstituteFontFamily);

            WireProxies();
        }

        private void WireProxies()
        {
            DefaultZoom.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SettingValue<double>.Value))
                    this.RaisePropertyChanged(nameof(ZoomPercent));
            };

            FontSize.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SettingValue<double>.Value))
                    this.RaisePropertyChanged(nameof(FontSizeProxy));
            };

            AutoSaveIntervalSeconds.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SettingValue<int>.Value))
                    this.RaisePropertyChanged(nameof(AutoSaveSecondsProxy));
            };
        }

        private static ObservableCollection<ScriptFontEntry> BuildScriptFonts(
            Dictionary<string, string>? map)
        {
            var defaults = new TextEditorSettings().ScriptFontMap;
            var col = new ObservableCollection<ScriptFontEntry>();

            var definitions = new (string Key, string Display)[]
            {
                ("Cyrillic",   "Кириллица (ru, uk, bg…)"),
                ("Greek",      "Греческий"),
                ("Arabic",     "Арабский"),
                ("Hebrew",     "Иврит"),
                ("CJK",        "Китайский (CJK)"),
                ("Korean",     "Корейский"),
                ("Japanese",   "Японский"),
                ("Devanagari", "Деванагари (хинди)"),
                ("Thai",       "Тайский"),
            };

            foreach (var (key, display) in definitions)
            {
                string font = (map != null && map.TryGetValue(key, out var f) && !string.IsNullOrEmpty(f))
                    ? f
                    : (defaults.TryGetValue(key, out var d) ? d : "Times New Roman");

                col.Add(new ScriptFontEntry(key, display, font));
            }

            return col;
        }

        public TextEditorSettings GetSettings() => new()
        {
            FontFamily = FontFamily.Value,
            FontSize = FontSize.Value,
            SpellCheckEnabled = SpellCheckEnabled.Value,
            DefaultLanguage = DefaultLanguage.Value,
            ShowSpellErrors = ShowSpellErrors.Value,
            AutoReplaceEnabled = AutoReplaceEnabled.Value,
            ShowRuler = ShowRuler.Value,
            ShowFormattingMarks = ShowFormattingMarks.Value,
            DefaultViewMode = DefaultViewMode.Value,
            DefaultZoom = DefaultZoom.Value,
            AutoSaveIntervalSeconds = AutoSaveIntervalSeconds.Value,
            MonitorSizeInches = MonitorSizeInches.Value,
            BreakOnHyphen = BreakOnHyphen.Value,
            SubstituteMissingGlyphs = SubstituteMissingGlyphs.Value,
            SubstituteFontFamily = SubstituteFontFamily.Value,
            ScriptFontMap = ScriptFonts.ToDictionary(e => e.ScriptKey, e => e.FontFamily)
        };
    }
}