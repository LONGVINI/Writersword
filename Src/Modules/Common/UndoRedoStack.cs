using System.Collections.Generic;
using System.Linq;
using Writersword.Core.Interfaces.Modules;

namespace Writersword.Modules.Common
{
    /// <summary>
    /// Готовый стек Undo/Redo для использования внутри модуля.
    /// Просто создай экземпляр и делегируй в него IUndoableModule.
    /// </summary>
    public class UndoRedoStack
    {
        private readonly int _maxSteps;
        private readonly Stack<IUndoableCommand> _undoStack = new();
        private readonly Stack<IUndoableCommand> _redoStack = new();

        public UndoRedoStack(int maxSteps = 50)
        {
            _maxSteps = maxSteps;
        }

        public bool CanUndo => _undoStack.Count > 0;
        public bool CanRedo => _redoStack.Count > 0;

        public string? UndoDescription => CanUndo ? _undoStack.Peek().Description : null;
        public string? RedoDescription => CanRedo ? _redoStack.Peek().Description : null;

        /// <summary>
        /// Выполнить команду и положить в стек.
        /// Сбрасывает RedoStack — новое действие отменяет историю повторов.
        /// </summary>
        public void Push(IUndoableCommand command)
        {
            command.Execute();
            _undoStack.Push(command);
            _redoStack.Clear();

            if (_undoStack.Count > _maxSteps)
                TrimStack();
        }

        public void Undo()
        {
            if (!CanUndo) return;
            var cmd = _undoStack.Pop();
            cmd.Undo();
            _redoStack.Push(cmd);
        }

        public void Redo()
        {
            if (!CanRedo) return;
            var cmd = _redoStack.Pop();
            cmd.Execute();
            _undoStack.Push(cmd);
        }

        public void Clear()
        {
            _undoStack.Clear();
            _redoStack.Clear();
        }

        private void TrimStack()
        {
            var items = _undoStack.ToArray().Take(_maxSteps).ToArray();
            _undoStack.Clear();
            // ToArray даёт от вершины — разворачиваем чтобы порядок сохранился
            foreach (var item in items.Reverse())
                _undoStack.Push(item);
        }
    }
}