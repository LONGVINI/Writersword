using System;
using Writersword.Core.Interfaces.Modules;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.ViewModels;

namespace Writersword.Modules.TextEditor.Commands
{
    /// <summary>
    /// Шаг отмены вставки многострочного текста в поток документа.
    ///
    /// Прежде вставка шла через <see cref="DocumentSnapshotCommand"/>: два прохода
    /// сериализации всей рукописи плюс пересборка вью-моделей всех абзацев на откате.
    /// Вставить абзац в трёхсотстраничную книгу стоило столько же, сколько переписать
    /// её целиком.
    ///
    /// Здесь шаг весит вставленное: содержимое первого абзаца до и после правки, и сами
    /// добавленные абзацы живыми объектами. Пока шаг откачен, из документа они вынуты и
    /// держит их он один — вытеснили шаг из истории, и вернуть вставленное уже нечем,
    /// как и любой другой шаг за пределом стека.
    /// </summary>
    public sealed class InsertParagraphsCommand : IUndoableCommand, ITextCommand
    {
        private readonly DocumentViewModel _docVm;
        private readonly DocumentViewModel.InsertedParagraphsSpan _span;

        public InsertParagraphsCommand(
            DocumentViewModel docVm,
            DocumentViewModel.InsertedParagraphsSpan span,
            string description)
        {
            _docVm = docVm;
            _span = span;
            Description = description;
        }

        public string Description { get; }

        /// <summary>
        /// Куда поставить каретку после отката или повтора. Полотно назначает обработчик
        /// при укладке шага в стек.
        /// </summary>
        public Action<Guid, int>? RestoreCaretCallback { get; set; }

        /// <summary>Место каретки после повтора: абзац и позиция в нём.</summary>
        public Guid CaretParaAfter { get; set; }

        public int CaretCharAfter { get; set; }

        /// <summary>Место каретки после отката — там, откуда вставляли.</summary>
        public int CaretCharBefore { get; set; }

        /// <summary>Повтор: вернуть вставленное на место.</summary>
        public void Execute()
        {
            if (!_docVm.ApplyInsertedParagraphs(_span)) return;

            if (CaretParaAfter != Guid.Empty)
                RestoreCaretCallback?.Invoke(CaretParaAfter, CaretCharAfter);
        }

        /// <summary>
        /// Отмена: снять вставленное. Каретка встаёт туда, откуда вставляли, — на то
        /// место, куда смотрел человек, нажимая Ctrl+V.
        /// </summary>
        public void Undo()
        {
            if (!_docVm.RevertInsertedParagraphs(_span)) return;

            RestoreCaretCallback?.Invoke(_span.FirstParaId, CaretCharBefore);
        }

        void ITextCommand.Apply(DocumentModel doc) => Execute();

        void ITextCommand.Revert(DocumentModel doc) => Undo();

        bool ITextCommand.TryMerge(ITextCommand next) => false;
    }
}
