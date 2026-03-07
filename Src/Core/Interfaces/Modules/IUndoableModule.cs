using Avalonia.Input;
using System.Collections.Generic;

namespace Writersword.Core.Interfaces.Modules
{
    /// <summary>
    /// Опциональный интерфейс для модулей поддерживающих Undo/Redo.
    /// Реализуй в модуле если нужна история действий.
    /// Аналогично IConfigurableModule — подключается по желанию.
    /// </summary>
    public interface IUndoableModule
    {
        bool CanUndo { get; }
        bool CanRedo { get; }

        /// <summary>Описание следующего шага отмены, например "Вставка текста"</summary>
        string? UndoDescription { get; }

        /// <summary>Описание следующего шага повтора</summary>
        string? RedoDescription { get; }

        void Undo();
        void Redo();

        /// <summary>
        /// Добавить команду в стек и выполнить её.
        /// Вызывай из модуля при каждом действии пользователя.
        /// </summary>
        void PushCommand(IUndoableCommand command);

        /// <summary>
        /// Жесты которые модуль перехватывает сам — нативная обработка блокируется.
        /// Например TextEditor блокирует Ctrl+Z чтобы TextBox не обрабатывал его дважды.
        /// </summary>
        IReadOnlyList<KeyGesture> BlockedNativeGestures { get; }
    }
}