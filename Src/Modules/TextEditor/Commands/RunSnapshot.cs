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

        public RunSnapshot(string text, RunProperties? properties)
        {
            Text = text;
            Properties = properties?.Clone();
        }

        /// <summary>Клонировать снапшот.</summary>
        public RunSnapshot Clone() => new(Text, Properties);
    }
}
