using System.Collections.Generic;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Commands
{
    /// <summary>
    /// Составная команда — объединяет произвольное количество подкоманд в один шаг undo/redo.
    /// Используется для операций затрагивающих несколько элементов одновременно:
    /// форматирование нескольких параграфов, вставка таблицы с якорными параграфами и т.д.
    /// Apply выполняет подкоманды по порядку, Revert — в обратном.
    /// </summary>
    public sealed class CompositeCommand : ITextCommand
    {
        private readonly List<ITextCommand> _commands;
        private readonly System.Action? _afterChange;

        public string Description { get; }

        public CompositeCommand(string description, List<ITextCommand> commands,
            System.Action? afterChange = null)
        {
            Description = description;
            _commands = commands;
            _afterChange = afterChange;
        }

        /// <summary>Выполняет все подкоманды в прямом порядке.</summary>
        public void Apply(DocumentModel doc)
        {
            foreach (var cmd in _commands)
                cmd.Apply(doc);
            _afterChange?.Invoke();
        }

        /// <summary>Откатывает все подкоманды в обратном порядке.</summary>
        public void Revert(DocumentModel doc)
        {
            for (int i = _commands.Count - 1; i >= 0; i--)
                _commands[i].Revert(doc);
            _afterChange?.Invoke();
        }

        /// <summary>CompositeCommand не сливается с другими командами.</summary>
        public bool TryMerge(ITextCommand next) => false;

        /// <summary>Количество подкоманд.</summary>
        public int Count => _commands.Count;
    }
}