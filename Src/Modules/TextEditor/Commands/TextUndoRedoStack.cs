using System.Collections.Generic;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Commands
{
    /// <summary>
    /// Стек undo/redo для операционной системы команд.
    /// Заменяет UndoRedoStack + DocumentSnapshotCommand.
    /// Хранит легковесные ITextCommand вместо полных снапшотов документа.
    /// Ограничен по размеру — при переполнении выбрасывает самые старые записи.
    /// </summary>
    public sealed class TextUndoRedoStack
    {
        private readonly int _maxSize;
        private readonly LinkedList<ITextCommand> _undoStack = new();
        private readonly Stack<ITextCommand> _redoStack = new();

        /// <summary>Можно ли выполнить Undo.</summary>
        public bool CanUndo => _undoStack.Count > 0;

        /// <summary>Можно ли выполнить Redo.</summary>
        public bool CanRedo => _redoStack.Count > 0;

        /// <summary>Описание следующей операции Undo (для отображения в меню).</summary>
        public string? UndoDescription => _undoStack.Last?.Value.Description;

        /// <summary>Описание следующей операции Redo (для отображения в меню).</summary>
        public string? RedoDescription => _redoStack.Count > 0 ? _redoStack.Peek().Description : null;

        public TextUndoRedoStack(int maxSize = 100)
        {
            _maxSize = maxSize;
        }

        /// <summary>
        /// Добавить команду в стек.
        /// Пытается слить с последней командой через TryMerge перед добавлением.
        /// Очищает redo-стек — после новой операции redo недоступен.
        /// При переполнении удаляет самую старую запись.
        /// </summary>
        public bool Push(ITextCommand command)
        {
            // Пробуем слить с последней командой (например, последовательный ввод символов).
            // При слиянии новая запись не добавляется — возвращаем false, чтобы вызывающий
            // не фиксировал её в общем порядке отмены отдельным шагом.
            if (_undoStack.Last != null && _undoStack.Last.Value.TryMerge(command))
                return false;

            _undoStack.AddLast(command);
            _redoStack.Clear();

            // Ограничение размера — выбрасываем самую старую запись.
            if (_undoStack.Count > _maxSize)
                _undoStack.RemoveFirst();
            return true;
        }

        /// <summary>
        /// Откатить последнюю операцию.
        /// Откатившуюся команду помещает в redo-стек.
        /// </summary>
        public void Undo(DocumentModel doc)
        {
            if (!CanUndo) return;
            var cmd = _undoStack.Last!.Value;
            _undoStack.RemoveLast();
            cmd.Revert(doc);
            _redoStack.Push(cmd);
        }

        /// <summary>
        /// Повторить последнюю отменённую операцию.
        /// </summary>
        public void Redo(DocumentModel doc)
        {
            if (!CanRedo) return;
            var cmd = _redoStack.Pop();
            cmd.Apply(doc);
            _undoStack.AddLast(cmd);
        }

        /// <summary>
        /// Начать транзакцию — несколько команд будут объединены в один шаг undo.
        /// Команды применяются к документу немедленно при добавлении через tx.Add().
        /// Коммит происходит при Dispose (using-блок).
        /// </summary>
        public TextCommandTransaction BeginTransaction(DocumentModel doc, string description)
            => new TextCommandTransaction(this, doc, description);

        /// <summary>Полная очистка стека (например, при закрытии документа).</summary>
        public void Clear()
        {
            _undoStack.Clear();
            _redoStack.Clear();
        }
    }
}