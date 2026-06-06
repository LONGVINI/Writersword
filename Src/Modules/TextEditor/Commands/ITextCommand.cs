namespace Writersword.Modules.TextEditor.Commands
{
    /// <summary>
    /// Базовый интерфейс для всех операций редактора.
    /// Каждая команда умеет применить себя и откатить себя.
    /// Метод TryMerge позволяет объединить последовательные однотипные операции
    /// (например, последовательный ввод символов) в одну запись undo.
    /// </summary>
    public interface ITextCommand
    {
        /// <summary>Описание операции для отображения в UI (например, "Type text").</summary>
        string Description { get; }

        /// <summary>Применить операцию к документу.</summary>
        void Apply(Writersword.Modules.TextEditor.Models.Document.DocumentModel doc);

        /// <summary>Откатить операцию.</summary>
        void Revert(Writersword.Modules.TextEditor.Models.Document.DocumentModel doc);

        /// <summary>
        /// Попытаться слить следующую команду с текущей.
        /// Возвращает true если слияние выполнено — тогда next не добавляется в стек отдельно.
        /// Используется для объединения последовательных InsertText в одну запись.
        /// </summary>
        bool TryMerge(ITextCommand next);
    }
}
