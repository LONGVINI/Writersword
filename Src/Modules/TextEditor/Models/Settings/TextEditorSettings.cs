using System.Collections.Generic;
using Newtonsoft.Json;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Models.Settings
{
    /// <summary>
    /// Глобальные настройки модуля текстового редактора.
    /// Сохраняются в Settings.json (глобально) и в workspace.json (per-проект).
    /// </summary>
    public sealed class TextEditorSettings
    {
        // ── Шрифт по умолчанию ────────────────────────────────────────────

        /// <summary>Размер шрифта по умолчанию для новых документов.</summary>
        public double FontSize { get; set; } = 14;

        /// <summary>Семейство шрифта по умолчанию для новых документов.</summary>
        public string FontFamily { get; set; } = "Times New Roman";

        // ── Орфография ────────────────────────────────────────────────────

        /// <summary>Включить проверку орфографии.</summary>
        public bool SpellCheckEnabled { get; set; } = true;

        /// <summary>
        /// Язык по умолчанию для новых документов.
        /// Используется для выбора словаря NHunspell.
        /// </summary>
        public string DefaultLanguage { get; set; } = "ru";

        /// <summary>Подчёркивать ошибки красным волнистой линией.</summary>
        public bool ShowSpellErrors { get; set; } = true;

        // ── Автозамена ────────────────────────────────────────────────────

        /// <summary>Включить автозамену.</summary>
        public bool AutoReplaceEnabled { get; set; } = true;

        /// <summary>
        /// Глобальные правила автозамены.
        /// Применяются ко всем документам, могут быть переопределены на уровне документа.
        /// </summary>
        [JsonIgnore]
        public List<AutoReplaceRule> AutoReplaceRules { get; set; } = CreateDefaultRules();

        // ── Отображение ───────────────────────────────────────────────────

        /// <summary>Показывать линейку.</summary>
        public bool ShowRuler { get; set; } = true;

        /// <summary>
        /// Единицы измерения линейки.
        /// Centimeters — по умолчанию для большинства стран.
        /// Inches — для США и ряда других стран.
        /// </summary>
        public RulerUnits RulerUnits { get; set; } = RulerUnits.Centimeters;

        /// <summary>Показывать непечатаемые символы (пробелы, переносы строк).</summary>
        public bool ShowFormattingMarks { get; set; }

        /// <summary>Режим отображения по умолчанию для новых документов.</summary>
        public EditorViewMode DefaultViewMode { get; set; } = EditorViewMode.Page;

        /// <summary>Масштаб по умолчанию (1.0 = 100%).</summary>
        public double DefaultZoom { get; set; } = 1.0;

        // ── Автосохранение ────────────────────────────────────────────────

        /// <summary>Интервал автосохранения в кеш (в секундах). 0 — отключено.</summary>
        public int AutoSaveIntervalSeconds { get; set; } = 30;

        // ── Поведение ─────────────────────────────────────────────────────

        /// <summary>Автоматически определять язык при вставке текста.</summary>
        public bool AutoDetectLanguage { get; set; } = true;

        /// <summary>
        /// Физический размер монитора по диагонали в дюймах.
        /// 0 = автоматически (96 DPI).
        /// </summary>
        public double MonitorSizeInches { get; set; } = 0;

        /// <summary>
        /// Создаёт набор встроенных правил автозамены по умолчанию.
        /// </summary>
        private static List<AutoReplaceRule> CreateDefaultRules()
        {
            return new List<AutoReplaceRule>
            {
                new() { From = "--",   To = "\u2014", IsBuiltIn = true },  // -- → —
                new() { From = "...",  To = "\u2026", IsBuiltIn = true },  // ... → …
                new() { From = "(c)",  To = "\u00A9", IsBuiltIn = true },  // (c) → ©
                new() { From = "(r)",  To = "\u00AE", IsBuiltIn = true },  // (r) → ®
                new() { From = "(tm)", To = "\u2122", IsBuiltIn = true },  // (tm) → ™
                new() { From = "->",   To = "\u2192", IsBuiltIn = true },  // -> → →
                new() { From = "<-",   To = "\u2190", IsBuiltIn = true },  // <- → ←
                new() { From = "<->",  To = "\u2194", IsBuiltIn = true },  // <-> → ↔
            };
        }
    }
}