using ReactiveUI;
using System;
using System.Collections.Generic;
using Writersword.Core.Enums;
using Writersword.Core.Models.Settings;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Settings;

namespace Writersword.Modules.TextEditor.ViewModels
{
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
            MonitorSizeInches = MonitorSizeInches.Value
        };
    }
}