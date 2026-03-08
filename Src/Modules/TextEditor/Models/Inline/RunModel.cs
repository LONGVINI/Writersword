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

        /// <summary>Создаёт глубокую копию Run.</summary>
        public RunModel Clone()
        {
            return new RunModel
            {
                Id = Guid.NewGuid(),
                Text = Text,
                Properties = Properties?.Clone()
            };
        }
    }
}
