using System;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Commands
{
    /// <summary>
    /// Команда для структурных операций с таблицами:
    /// AddRow, DeleteRow, AddColumn, DeleteColumn, DeleteTable, InsertTable,
    /// ResizeColumn, MoveTable, SetCellProperty.
    /// Таблицы структурно сложны и редко изменяются — используем делегаты
    /// apply/revert которые передаёт вызывающий код с нужным замыканием.
    /// Это позволяет не дублировать логику мутации таблицы внутри команды.
    /// </summary>
    public sealed class TableStructureCommand : ITextCommand
    {
        private readonly Action<DocumentModel> _apply;
        private readonly Action<DocumentModel> _revert;

        public string Description { get; }

        /// <param name="apply">Делегат применения операции к документу.</param>
        /// <param name="revert">Делегат отката операции.</param>
        /// <param name="description">Описание для UI (например, "Add row", "Delete column").</param>
        public TableStructureCommand(
            Action<DocumentModel> apply,
            Action<DocumentModel> revert,
            string description)
        {
            _apply = apply;
            _revert = revert;
            Description = description;
        }

        public void Apply(DocumentModel doc) => _apply(doc);

        public void Revert(DocumentModel doc) => _revert(doc);

        public bool TryMerge(ITextCommand next) => false;
    }
}
