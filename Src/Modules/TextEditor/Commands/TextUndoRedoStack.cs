using System;
using System.Collections.Generic;
using Writersword.Modules.Common;
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

        // Номера состояний истории — параллельно командам: состояние ПОСЛЕ каждой
        // команды. Номера сквозные с UndoRedoStack (UndoRedoStack.NextStateId), поэтому
        // возврат по Ctrl+Z в прежнюю позицию даёт прежний номер, и модуль видит,
        // что правки откачены до сохранённого состояния.
        private readonly LinkedList<long> _undoStates = new();
        private readonly Stack<long> _redoStates = new();
        private long _baseState;
        private long _currentState;

        /// <summary>Номер текущего состояния истории.</summary>
        public long StateId => _currentState;

        /// <summary>История сдвинулась: новая команда, слияние с последней, Undo или Redo.</summary>
        public event Action? StateChanged;

        /// <summary>Можно ли выполнить Undo.</summary>
        public bool CanUndo => _undoStack.Count > 0;

        /// <summary>Можно ли выполнить Redo.</summary>
        public bool CanRedo => _redoStack.Count > 0;

        /// <summary>Описание следующей операции Undo (для отображения в меню).</summary>
        public string? UndoDescription => _undoStack.Last?.Value.Description;

        /// <summary>Описание следующей операции Redo (для отображения в меню).</summary>
        public string? RedoDescription => _redoStack.Count > 0 ? _redoStack.Peek().Description : null;

        /// <summary>
        /// Команда, которую откатит ближайший Undo, без её выполнения. Нужна вызывающему,
        /// чтобы понять, какое обновление вида потребуется: правка текста обходится
        /// перерисовкой абзаца, а правка таблицы требует пересборки раскладки.
        /// </summary>
        public ITextCommand? PeekUndo => _undoStack.Last?.Value;

        /// <summary>Команда, которую применит ближайший Redo, без её выполнения.</summary>
        public ITextCommand? PeekRedo => _redoStack.Count > 0 ? _redoStack.Peek() : null;

        public TextUndoRedoStack(int maxSize = 100)
        {
            _maxSize = maxSize;
            _baseState = UndoRedoStack.NextStateId();
            _currentState = _baseState;
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
            {
                // Слитая команда изменила содержимое последнего шага: это новое
                // состояние документа, хотя число шагов в истории прежнее.
                _currentState = UndoRedoStack.NextStateId();
                _undoStates.Last!.Value = _currentState;
                StateChanged?.Invoke();
                return false;
            }

            _currentState = UndoRedoStack.NextStateId();
            _undoStack.AddLast(command);
            _undoStates.AddLast(_currentState);
            _redoStack.Clear();
            _redoStates.Clear();

            // Ограничение размера — выбрасываем самую старую запись. Отмена всех
            // оставшихся теперь приводит в состояние после выброшенной записи.
            if (_undoStack.Count > _maxSize)
            {
                _undoStack.RemoveFirst();
                _baseState = _undoStates.First!.Value;
                _undoStates.RemoveFirst();
            }

            StateChanged?.Invoke();
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
            var stateAfter = _undoStates.Last!.Value;
            _undoStack.RemoveLast();
            _undoStates.RemoveLast();
            cmd.Revert(doc);
            _redoStack.Push(cmd);
            _redoStates.Push(stateAfter);

            _currentState = _undoStates.Last?.Value ?? _baseState;
            StateChanged?.Invoke();
        }

        /// <summary>
        /// Повторить последнюю отменённую операцию.
        /// </summary>
        public void Redo(DocumentModel doc)
        {
            if (!CanRedo) return;
            var cmd = _redoStack.Pop();
            var stateAfter = _redoStates.Pop();
            cmd.Apply(doc);
            _undoStack.AddLast(cmd);
            _undoStates.AddLast(stateAfter);

            _currentState = stateAfter;
            StateChanged?.Invoke();
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
            _undoStates.Clear();
            _redoStates.Clear();

            // Очистка истории данных не меняет — номер текущего состояния остаётся.
            _baseState = _currentState;
        }
    }
}