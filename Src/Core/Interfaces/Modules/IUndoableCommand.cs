namespace Writersword.Core.Interfaces.Modules
{
    /// <summary>
    /// Команда которая умеет выполниться и отменить себя.
    /// Реализуй для каждого действия пользователя в модуле.
    /// </summary>
    public interface IUndoableCommand
    {
        /// <summary>Описание для отображения в меню: "Отменить: Вставка текста"</summary>
        string Description { get; }

        void Execute();
        void Undo();
    }
}