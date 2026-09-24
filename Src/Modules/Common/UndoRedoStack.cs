using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Writersword.Core.Interfaces.Modules;

namespace Writersword.Modules.Common
{
    /// <summary>
    /// Готовый стек Undo/Redo для использования внутри модуля.
    /// Просто создай экземпляр и делегируй в него IUndoableModule.
    /// <para>
    /// Стек нумерует состояния истории (StateId): каждая новая правка получает новый
    /// номер, а Undo и Redo возвращают номер той позиции, в которую пришли. Модуль
    /// передаёт этот номер в BaseModule.NotifyHistoryChanged, и вкладка видит, что
    /// после Ctrl+Z до сохранённого состояния несохранённых правок снова нет.
    /// </para>
    /// </summary>
    public class UndoRedoStack
    {
        private static long s_nextStateId;

        private readonly int _maxSteps;
        private readonly Stack<IUndoableCommand> _undoStack = new();
        private readonly Stack<IUndoableCommand> _redoStack = new();

        // Состояние ПОСЛЕ каждой команды — параллельно самим командам.
        private readonly Stack<long> _undoStates = new();
        private readonly Stack<long> _redoStates = new();

        // Состояние, в которое приводит отмена всех команд стека.
        private long _baseState;
        private long _currentState;

        public UndoRedoStack(int maxSteps = 100)
        {
            _maxSteps = maxSteps;
            _baseState = NextStateId();
            _currentState = _baseState;
        }

        /// <summary>
        /// Новый номер состояния истории. Номера сквозные для всех стеков приложения,
        /// поэтому состояния разных стеков никогда не совпадают случайно.
        /// </summary>
        public static long NextStateId() => Interlocked.Increment(ref s_nextStateId);

        /// <summary>Номер текущего состояния истории.</summary>
        public long StateId => _currentState;

        /// <summary>
        /// История сдвинулась: новая команда, Undo или Redo. Поднимается после того,
        /// как команда применена.
        /// </summary>
        public event Action? StateChanged;

        public bool CanUndo => _undoStack.Count > 0;
        public bool CanRedo => _redoStack.Count > 0;

        /// <summary>Все команды в истории (undo + redo) — для анализа, например ссылок на ресурсы.</summary>
        public IEnumerable<IUndoableCommand> AllCommands => _undoStack.Concat(_redoStack);

        public string? UndoDescription => CanUndo ? _undoStack.Peek().Description : null;
        public string? RedoDescription => CanRedo ? _redoStack.Peek().Description : null;

        /// <summary>
        /// Положить команду в стек БЕЗ выполнения
        /// Используй когда действие уже применено и нужно только запомнить его для Undo.
        /// </summary>
        public void Push(IUndoableCommand command)
        {
            _currentState = NextStateId();

            _undoStack.Push(command);
            _undoStates.Push(_currentState);

            _redoStack.Clear();
            _redoStates.Clear();

            if (_undoStack.Count > _maxSteps)
                TrimStack();

            StateChanged?.Invoke();
        }

        public void Undo()
        {
            if (!CanUndo) return;

            var cmd = _undoStack.Pop();
            var stateAfter = _undoStates.Pop();

            cmd.Undo();

            _redoStack.Push(cmd);
            _redoStates.Push(stateAfter);

            _currentState = _undoStates.Count > 0 ? _undoStates.Peek() : _baseState;
            StateChanged?.Invoke();
        }

        public void Redo()
        {
            if (!CanRedo) return;

            var cmd = _redoStack.Pop();
            var stateAfter = _redoStates.Pop();

            cmd.Execute();

            _undoStack.Push(cmd);
            _undoStates.Push(stateAfter);

            _currentState = stateAfter;
            StateChanged?.Invoke();
        }

        /// <summary>
        /// Очистить историю. Сами данные при этом не меняются, поэтому номер текущего
        /// состояния сохраняется: очистка не делает документ «изменённым».
        /// </summary>
        public void Clear()
        {
            _undoStack.Clear();
            _redoStack.Clear();
            _undoStates.Clear();
            _redoStates.Clear();
            _baseState = _currentState;
        }

        private void TrimStack()
        {
            // ToArray даёт от вершины. Выбрасываются самые старые команды — те, что
            // дальше maxSteps от вершины. Отмена всех оставшихся теперь приводит в
            // состояние после самой свежей из выброшенных команд — оно и становится
            // базовым.
            var commands = _undoStack.ToArray();
            var states = _undoStates.ToArray();

            _baseState = states[_maxSteps];

            _undoStack.Clear();
            _undoStates.Clear();

            // Разворачиваем чтобы порядок сохранился
            for (int i = _maxSteps - 1; i >= 0; i--)
            {
                _undoStack.Push(commands[i]);
                _undoStates.Push(states[i]);
            }
        }
    }
}
