using System;
using System.Text.Json.Serialization;

namespace Writersword.Modules.TextEditor.Models.Inline
{
    /// <summary>
    /// Минимальная единица текста с единым форматированием.
    /// Аналог "run" в OOXML (docx).
    /// Несколько Run подряд с одинаковыми свойствами могут быть объединены при сериализации.
    /// </summary>
    public sealed class RunModel
    {
        /// <summary>Уникальный идентификатор Run. Используется при мерже соседних Run.</summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>Текстовое содержимое.</summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>
        /// Свойства форматирования.
        /// Если IsDefault() == true — объект не сериализуется, экономим место.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public RunProperties? Properties { get; set; }

        /// <summary>
        /// Id встроенной картинки, если этот run — объект в строке, а не текст.
        /// Сама картинка лежит в SectionModel.InlineObjects, а здесь остаётся ссылка.
        /// Text такого run — ровно один символ <see cref="ObjectPlaceholder"/>:
        /// вся посимвольная арифметика редактора (каретка, выделение, отмена ввода,
        /// хеши чанков) продолжает работать без изменений и считает картинку
        /// одним символом.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Guid? InlineImageId { get; set; }

        /// <summary>
        /// Символ-заполнитель объекта в тексте (U+FFFC OBJECT REPLACEMENT CHARACTER).
        /// Записан кодом намеренно: сам символ невидим в редакторе кода.
        /// </summary>
        public const char ObjectPlaceholder = (char)0xFFFC;

        /// <summary>Является ли run встроенным объектом (картинкой).</summary>
        [JsonIgnore]
        public bool IsInlineObject => InlineImageId.HasValue;

        /// <summary>Создаёт глубокую копию Run.</summary>
        public RunModel Clone()
        {
            return new RunModel
            {
                Id = Guid.NewGuid(),
                Text = Text,
                Properties = Properties?.Clone(),
                InlineImageId = InlineImageId
            };
        }
    }
}
