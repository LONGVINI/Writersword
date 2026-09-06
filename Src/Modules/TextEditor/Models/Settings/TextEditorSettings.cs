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

        // ── Перенос строк ─────────────────────────────────────────────────

        /// <summary>
        /// Разрешать перенос строки после дефиса внутри слова: «чьего-то»
        /// разбивается на «чьего-» и «то».
        ///
        /// Включено по умолчанию — так верстают Word и типографика вообще.
        /// Без этого слово с дефисом уезжает на следующую строку целиком, абзац
        /// занимает лишнюю строку, и на длинной рукописи расхождение с исходным
        /// документом накапливается в целые страницы.
        ///
        /// Выключать имеет смысл, когда дефисы в тексте значимы сами по себе
        /// (шифры, коды, формулы) и рвать их по строкам нельзя.
        /// </summary>
        public bool BreakOnHyphen { get; set; } = true;

        // ── Знаки, которых нет в выбранной гарнитуре ──────────────────────

        /// <summary>
        /// Подставлять другой шрифт вместо знаков, которых в выбранной гарнитуре
        /// нет.
        ///
        /// Выключено по умолчанию, и это осознанный выбор, а не осторожность.
        /// Подстановка удобна ровно до того момента, когда человек узнаёт о ней
        /// из вёрстки: он набирал латинской гарнитурой, кириллицу молча дорисовал
        /// кто-то другой, и половина рукописи набрана не тем, что выбрано в ленте.
        /// Без подстановки знак рисуется так, как его отдаёт сама гарнитура —
        /// пустым прямоугольником, ровно как в любом другом редакторе. Это видно
        /// сразу и чинится выбором подходящего шрифта.
        ///
        /// Включённой настройкой распоряжаются SubstituteFontFamily и ScriptFontMap.
        /// </summary>
        public bool SubstituteMissingGlyphs { get; set; }

        /// <summary>
        /// Шрифт подстановки — берётся, когда SubstituteMissingGlyphs включено, а
        /// письмо знака в ScriptFontMap не описано.
        ///
        /// Если и в нём знака нет, подстановки не будет: рисовать пустой
        /// прямоугольник чужой гарнитурой бессмысленно — квадрат тот же, а кусок
        /// текста молча сменил шрифт.
        /// </summary>
        public string SubstituteFontFamily { get; set; } = "Times New Roman";

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

        /// <summary>
        /// Виды чтения, общие для всех проектов. Тот же вид может лежать и в
        /// документе — тогда он и уедет с рукописью, и останется под рукой здесь.
        /// </summary>
        public List<ReadingTheme> ReadingThemes { get; set; } = new();

        // ── Чтение ────────────────────────────────────────────────────────
        // Личные предпочтения читателя, а не свойства рукописи. Живут здесь, а не в
        // сессии проекта: человек, привыкший читать лентой, ждёт ленту в любом
        // документе и в любом проекте, а не только в том, где её однажды включил.

        /// <summary>Подача: разворот, один лист или лента.</summary>
        public ReadingFlow ReadingFlow { get; set; } = ReadingFlow.Spread;

        /// <summary>Пропорции листа при чтении.</summary>
        public ReadingSheetFormat ReadingSheetFormat { get; set; } = ReadingSheetFormat.Document;

        /// <summary>Опознаватель выбранного вида чтения.</summary>
        public string ReadingThemeId { get; set; } = ReadingTheme.CreamId;

        /// <summary>Рисовать свои номера страниц при чтении.</summary>
        public bool ReadingShowPageNumbers { get; set; } = true;

        /// <summary>Ужимать картинки и таблицы вместе с листом чтения.</summary>
        public bool ReadingScaleContent { get; set; } = true;

        /// <summary>Ступень размера текста при чтении.</summary>
        public int ReadingFontStep { get; set; }

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

        // ── Шрифты по языковым скриптам ──────────────────────────────────

        /// <summary>
        /// Карта "Unicode-скрипт → шрифт" для автоматического фолбэка.
        /// Используется SKTextRenderer когда выбранный шрифт не содержит нужного глифа.
        /// Ключи: Cyrillic, Greek, Arabic, Hebrew, CJK, Korean, Japanese, Devanagari, Thai.
        /// </summary>
        public Dictionary<string, string> ScriptFontMap { get; set; } = new()
        {
            { "Cyrillic",   "Times New Roman" },
            { "Greek",      "Times New Roman" },
            { "Arabic",     "Arial"           },
            { "Hebrew",     "Arial"           },
            { "CJK",        "SimSun"          },
            { "Korean",     "Malgun Gothic"   },
            { "Japanese",   "MS Gothic"       },
            { "Devanagari", "Mangal"          },
            { "Thai",       "Tahoma"          }
        };

        /// <summary>
        /// Создаёт набор встроенных правил автозамены по умолчанию.
        /// </summary>
        private static List<AutoReplaceRule> CreateDefaultRules()
        {
            return new List<AutoReplaceRule>
            {
                new() { From = "--",   To = "\u2014", IsBuiltIn = true },
                new() { From = "...",  To = "\u2026", IsBuiltIn = true },
                new() { From = "(c)",  To = "\u00A9", IsBuiltIn = true },
                new() { From = "(r)",  To = "\u00AE", IsBuiltIn = true },
                new() { From = "(tm)", To = "\u2122", IsBuiltIn = true },
                new() { From = "->",   To = "\u2192", IsBuiltIn = true },
                new() { From = "<-",   To = "\u2190", IsBuiltIn = true },
                new() { From = "<->",  To = "\u2194", IsBuiltIn = true },
            };
        }
    }
}