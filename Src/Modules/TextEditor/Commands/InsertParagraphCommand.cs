using System;
using Writersword.Core.Interfaces.Modules;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.ViewModels;

namespace Writersword.Modules.TextEditor.Commands
{
    /// <summary>
    /// Шаг отмены появления в потоке одного готового абзаца.
    ///
    /// Сейчас так вставляется абзац перед таблицей: Enter в самом начале первой ячейки
    /// даёт пустую строку над таблицей — единственный способ добраться до места, куда
    /// мышью не дотянуться. Правка состоит из одного блока, а шла снимком всей рукописи.
    ///
    /// Абзац хранится живым объектом: пока шаг откачен, из документа он вынут и держит
    /// его только этот шаг.
    /// </summary>
    public sealed class InsertParagraphCommand : IUndoableCommand, ITextCommand
    {
        private readonly DocumentViewModel _docVm;
        private readonly ParagraphBlock _para;
        private readonly int _blockIndex;

        public InsertParagraphCommand(
            DocumentViewModel docVm, ParagraphBlock para, int blockIndex, string description)
        {
            _docVm = docVm;
            _para = para;
            _blockIndex = blockIndex;
            Description = description;
        }

        public string Description { get; }

        /// <summary>
        /// Куда поставить каретку после отката или повтора: опознаватель абзаца и место
        /// в нём. Полотно назначает обработчик при укладке шага в стек.
        /// </summary>
        public Action<Guid, int>? RestoreCaretCallback { get; set; }

        /// <summary>Абзац, на который уходит каретка после отката — вставленного уже нет.</summary>
        public Guid CaretParaAfterUndo { get; set; }

        /// <summary>Повтор: поставить абзац обратно.</summary>
        public void Execute()
        {
            if (!_docVm.InsertFlowParagraph(_para, _blockIndex)) return;

            RestoreCaretCallback?.Invoke(_para.Id, 0);
        }

        /// <summary>Отмена: снять вставленный абзац.</summary>
        public void Undo()
        {
            if (!_docVm.RemoveFlowParagraph(_para)) return;

            if (CaretParaAfterUndo != Guid.Empty)
                RestoreCaretCallback?.Invoke(CaretParaAfterUndo, 0);
        }

        void ITextCommand.Apply(DocumentModel doc) => Execute();

        void ITextCommand.Revert(DocumentModel doc) => Undo();

        bool ITextCommand.TryMerge(ITextCommand next) => false;
    }
}
