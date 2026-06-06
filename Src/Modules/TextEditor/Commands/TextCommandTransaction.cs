using System;
using System.Collections.Generic;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Commands
{
    /// <summary>
    /// Транзакция для пакетного формирования составной команды.
    /// Использование: using (var tx = stack.BeginTransaction(...)) { tx.Add(...); }
    /// Dispose автоматически коммитит CompositeCommand в стек.
    /// Если в транзакцию не добавлено ни одной команды — коммит не происходит.
    /// </summary>
    public sealed class TextCommandTransaction : IDisposable
    {
        private readonly TextUndoRedoStack _stack;
        private readonly DocumentModel _doc;
        private readonly string _description;
        private readonly List<ITextCommand> _pending = new();
        private bool _committed;
        private bool _disposed;

        internal TextCommandTransaction(TextUndoRedoStack stack, DocumentModel doc, string description)
        {
            _stack = stack;
            _doc = doc;
            _description = description;
        }

        /// <summary>
        /// Добавить команду в транзакцию и немедленно применить её к документу.
        /// </summary>
        public void Add(ITextCommand command)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TextCommandTransaction));
            command.Apply(_doc);
            _pending.Add(command);
        }

        /// <summary>
        /// Явный коммит транзакции.
        /// После коммита дальнейшие Add недопустимы.
        /// </summary>
        public void Commit()
        {
            if (_committed || _disposed) return;
            _committed = true;
            PushToStack();
        }

        /// <summary>
        /// Откатить все уже применённые команды без добавления в стек.
        /// Используется при отмене операции пользователем внутри транзакции.
        /// </summary>
        public void Rollback()
        {
            if (_committed || _disposed) return;
            _committed = true;
            for (int i = _pending.Count - 1; i >= 0; i--)
                _pending[i].Revert(_doc);
            _pending.Clear();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (!_committed)
                PushToStack();
        }

        private void PushToStack()
        {
            if (_pending.Count == 0) return;

            if (_pending.Count == 1)
                _stack.Push(_pending[0]);
            else
                _stack.Push(new CompositeCommand(_description, new List<ITextCommand>(_pending)));
        }
    }
}
