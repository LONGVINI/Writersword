using Writersword.Modules.TextEditor.Models.Inline;

namespace Writersword.Modules.TextEditor.Commands
{
    /// <summary>
    /// Снапшот одного Run — текст и его форматирование.
    /// Используется командами для хранения минимально необходимых данных
    /// для точного восстановления удалённого или изменённого текста.
    /// </summary>
    public sealed class RunSnapshot
    {
        /// <summary>Текстовое содержимое run.</summary>
        public string Text { get; }

        /// <summary>Форматирование run. Null означает форматирование по умолчанию.</summary>
        public RunProperties? Properties { get; }

        /// <summary>
        /// Id встроенной картинки, если снапшот описывает объект в строке.
        /// Без него восстановление вернуло бы на место голый символ-заполнитель.
        /// </summary>
        public System.Guid? InlineImageId { get; }

        public RunSnapshot(string text, RunProperties? properties,
            System.Guid? inlineImageId = null)
        {
            Text = text;
            Properties = properties?.Clone();
            InlineImageId = inlineImageId;
        }

        /// <summary>Клонировать снапшот.</summary>
        public RunSnapshot Clone() => new(Text, Properties, InlineImageId);
    }
}
