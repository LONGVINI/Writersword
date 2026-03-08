using System.Text.Json.Serialization;

namespace Writersword.Modules.TextEditor.Models.Inline
{
    /// <summary>
    /// Набор всех свойств форматирования одного фрагмента текста (Run).
    /// Хранится в JSON внутри ZIP-проекта.
    /// </summary>
    public sealed class RunProperties
    {
        /// <summary>Название шрифта. Null означает "унаследовать от стиля абзаца".</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? FontFamily { get; set; }

        /// <summary>Размер шрифта в пунктах. Null — унаследовать.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? FontSize { get; set; }

        /// <summary>Жирный.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IsBold { get; set; }

        /// <summary>Курсив.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IsItalic { get; set; }

        /// <summary>Подчёркивание.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IsUnderline { get; set; }

        /// <summary>Зачёркивание.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IsStrikethrough { get; set; }

        /// <summary>Надстрочный (x²).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IsSuperscript { get; set; }

        /// <summary>Подстрочный (H₂O).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IsSubscript { get; set; }

        /// <summary>Все символы в верхнем регистре при отображении.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IsAllCaps { get; set; }

        /// <summary>Малые заглавные (Small Caps).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IsSmallCaps { get; set; }

        /// <summary>Цвет текста в формате #RRGGBB или #AARRGGBB. Null — унаследовать.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? TextColor { get; set; }

        /// <summary>Цвет маркера (фон под текстом). Null — нет маркера.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? HighlightColor { get; set; }

        /// <summary>
        /// Код языка фрагмента (ru, uk, en и т.д.).
        /// Используется для выбора словаря орфографии.
        /// Null — унаследовать от настроек документа.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Language { get; set; }

        /// <summary>Создаёт копию свойств.</summary>
        public RunProperties Clone() => (RunProperties)MemberwiseClone();

        /// <summary>
        /// Возвращает true если все поля имеют значения по умолчанию
        /// (нет явного форматирования).
        /// </summary>
        public bool IsDefault()
        {
            return FontFamily is null
                && FontSize is null
                && !IsBold
                && !IsItalic
                && !IsUnderline
                && !IsStrikethrough
                && !IsSuperscript
                && !IsSubscript
                && !IsAllCaps
                && !IsSmallCaps
                && TextColor is null
                && HighlightColor is null
                && Language is null;
        }
    }
}
