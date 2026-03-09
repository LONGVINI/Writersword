using System;
using System.Collections.Generic;
using System.Windows.Input;
using ReactiveUI;
using Writersword.Modules.TextEditor.Models;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.ViewModels
{
    /// <summary>
    /// ViewModel окна настроек модуля TextEditor.
    /// Отображает и редактирует <see cref="TextEditorSettings"/>.
    /// Инфраструктура получает настройки через <see cref="GetSettings"/>.
    /// </summary>
    public sealed class TextEditorSettingsViewModel : ReactiveObject
    {
        private double _fontSize;
        private string _fontFamily;
        private bool _spellCheckEnabled;
        private string _defaultLanguage;
        private bool _showSpellErrors;
        private bool _autoReplaceEnabled;
        private bool _showRuler;
        private bool _showFormattingMarks;
        private EditorViewMode _defaultViewMode;
        private double _defaultZoom;
        private int _autoSaveIntervalSeconds;

        // --- Основные параметры ---

        /// <summary>Размер шрифта по умолчанию для новых документов.</summary>
        public double FontSize
        {
            get => _fontSize;
            set => this.RaiseAndSetIfChanged(ref _fontSize, value);
        }

        /// <summary>Семейство шрифта по умолчанию.</summary>
        public string FontFamily
        {
            get => _fontFamily;
            set => this.RaiseAndSetIfChanged(ref _fontFamily, value);
        }

        // --- Орфография ---

        /// <summary>Включить проверку орфографии.</summary>
        public bool SpellCheckEnabled
        {
            get => _spellCheckEnabled;
            set => this.RaiseAndSetIfChanged(ref _spellCheckEnabled, value);
        }

        /// <summary>Язык по умолчанию (ru, uk, en...).</summary>
        public string DefaultLanguage
        {
            get => _defaultLanguage;
            set => this.RaiseAndSetIfChanged(ref _defaultLanguage, value);
        }

        /// <summary>Подчёркивать ошибки в тексте.</summary>
        public bool ShowSpellErrors
        {
            get => _showSpellErrors;
            set => this.RaiseAndSetIfChanged(ref _showSpellErrors, value);
        }

        // --- Автозамена ---

        /// <summary>Включить автозамену.</summary>
        public bool AutoReplaceEnabled
        {
            get => _autoReplaceEnabled;
            set => this.RaiseAndSetIfChanged(ref _autoReplaceEnabled, value);
        }

        // --- Отображение ---

        /// <summary>Показывать горизонтальную линейку.</summary>
        public bool ShowRuler
        {
            get => _showRuler;
            set => this.RaiseAndSetIfChanged(ref _showRuler, value);
        }

        /// <summary>Показывать непечатаемые символы.</summary>
        public bool ShowFormattingMarks
        {
            get => _showFormattingMarks;
            set => this.RaiseAndSetIfChanged(ref _showFormattingMarks, value);
        }

        /// <summary>Режим отображения по умолчанию для новых документов.</summary>
        public EditorViewMode DefaultViewMode
        {
            get => _defaultViewMode;
            set => this.RaiseAndSetIfChanged(ref _defaultViewMode, value);
        }

        /// <summary>Масштаб по умолчанию (1.0 = 100%).</summary>
        public double DefaultZoom
        {
            get => _defaultZoom;
            set
            {
                this.RaiseAndSetIfChanged(ref _defaultZoom, value);
                this.RaisePropertyChanged(nameof(ZoomPercent));
            }
        }

        /// <summary>Масштаб в процентах для отображения рядом со слайдером.</summary>
        public int ZoomPercent => (int)Math.Round(_defaultZoom * 100);

        // --- Автосохранение ---

        /// <summary>Интервал автосохранения в секундах (0 — отключено).</summary>
        public int AutoSaveIntervalSeconds
        {
            get => _autoSaveIntervalSeconds;
            set => this.RaiseAndSetIfChanged(ref _autoSaveIntervalSeconds, value);
        }

        // --- Доступные значения для ComboBox ---

        /// <summary>Список доступных шрифтов.</summary>
        public IReadOnlyList<string> AvailableFonts { get; } = new[]
        {
            "Arial",
            "Times New Roman",
            "Calibri",
            "Georgia",
            "Verdana",
            "Tahoma",
            "Trebuchet MS",
            "Consolas",
            "Courier New"
        };

        /// <summary>Список доступных языков.</summary>
        public IReadOnlyList<string> AvailableLanguages { get; } = new[]
        {
            "ru", "uk", "en"
        };

        /// <summary>Список режимов отображения.</summary>
        public IReadOnlyList<EditorViewMode> ViewModes { get; } = new[]
        {
            EditorViewMode.Page,
            EditorViewMode.Draft
        };

        /// <summary>Команда сброса к настройкам по умолчанию.</summary>
        public ICommand ResetToDefaultsCommand { get; }

        public TextEditorSettingsViewModel(TextEditorSettings settings)
        {
            _fontSize               = settings.FontSize;
            _fontFamily             = settings.FontFamily;
            _spellCheckEnabled      = settings.SpellCheckEnabled;
            _defaultLanguage        = settings.DefaultLanguage;
            _showSpellErrors        = settings.ShowSpellErrors;
            _autoReplaceEnabled     = settings.AutoReplaceEnabled;
            _showRuler              = settings.ShowRuler;
            _showFormattingMarks    = settings.ShowFormattingMarks;
            _defaultViewMode        = settings.DefaultViewMode;
            _defaultZoom            = settings.DefaultZoom;
            _autoSaveIntervalSeconds = settings.AutoSaveIntervalSeconds;

            ResetToDefaultsCommand = ReactiveCommand.Create(ResetToDefaults);
        }

        /// <summary>
        /// Возвращает объект настроек с текущими значениями.
        /// Вызывается инфраструктурой при сохранении настроек.
        /// </summary>
        public TextEditorSettings GetSettings()
        {
            return new TextEditorSettings
            {
                FontSize               = _fontSize,
                FontFamily             = _fontFamily,
                SpellCheckEnabled      = _spellCheckEnabled,
                DefaultLanguage        = _defaultLanguage,
                ShowSpellErrors        = _showSpellErrors,
                AutoReplaceEnabled     = _autoReplaceEnabled,
                ShowRuler              = _showRuler,
                ShowFormattingMarks    = _showFormattingMarks,
                DefaultViewMode        = _defaultViewMode,
                DefaultZoom            = _defaultZoom,
                AutoSaveIntervalSeconds = _autoSaveIntervalSeconds
            };
        }

        private void ResetToDefaults()
        {
            var defaults = new TextEditorSettings();
            FontSize               = defaults.FontSize;
            FontFamily             = defaults.FontFamily;
            SpellCheckEnabled      = defaults.SpellCheckEnabled;
            DefaultLanguage        = defaults.DefaultLanguage;
            ShowSpellErrors        = defaults.ShowSpellErrors;
            AutoReplaceEnabled     = defaults.AutoReplaceEnabled;
            ShowRuler              = defaults.ShowRuler;
            ShowFormattingMarks    = defaults.ShowFormattingMarks;
            DefaultViewMode        = defaults.DefaultViewMode;
            DefaultZoom            = defaults.DefaultZoom;
            AutoSaveIntervalSeconds = defaults.AutoSaveIntervalSeconds;
        }
    }
}
