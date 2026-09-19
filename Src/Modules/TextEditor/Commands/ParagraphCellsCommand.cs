using System;
using System.Collections.Generic;
using Writersword.Core.Interfaces.Modules;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.ViewModels;

namespace Writersword.Modules.TextEditor.Commands
{
    /// <summary>
    /// Шаг отмены правки, которая меняет содержимое нескольких абзацев и не трогает
    /// состав блоков.
    ///
    /// Такова, например, переноска картинки из одной строки в другую: символ картинки
    /// уходит из абзаца-источника и встаёт в абзац-приёмник, а больше в рукописи не
    /// меняется ничего. Описывать это снимком всей книги значит платить за триста
    /// страниц за перестановку одного знака.
    ///
    /// Шаг хранит по два состояния на каждый затронутый абзац — до правки и после, —
    /// посимвольно, со всем форматированием и картинками в строке.
    /// </summary>
    public sealed class ParagraphCellsCommand : IUndoableCommand, ITextCommand
    {
        /// <summary>Один абзац: чем он был и чем стал.</summary>
        public sealed class Entry
        {
            public Guid ParaId { get; set; }

            public List<ParagraphBlock.CharCell> Before { get; set; } = new();

            public List<ParagraphBlock.CharCell> After { get; set; } = new();
        }

        private readonly DocumentViewModel _docVm;
        private readonly List<Entry> _entries;

        public ParagraphCellsCommand(
            DocumentViewModel docVm, List<Entry> entries, string description)
        {
            _docVm = docVm;
            _entries = entries;
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

        /// <summary>Место каретки после отката.</summary>
        public Guid CaretParaBefore { get; set; }

        public int CaretCharAfter { get; set; }

        public int CaretCharBefore { get; set; }

        /// <summary>Повтор: вернуть абзацам состояние после правки.</summary>
        public void Execute()
        {
            foreach (var entry in _entries)
                _docVm.ApplyParagraphCells(entry.ParaId, entry.After);

            _docVm.RaiseStructureChanged();
            RestoreCaretCallback?.Invoke(CaretParaAfter, CaretCharAfter);
        }

        /// <summary>Отмена: вернуть абзацам состояние до правки.</summary>
        public void Undo()
        {
            foreach (var entry in _entries)
                _docVm.ApplyParagraphCells(entry.ParaId, entry.Before);

            _docVm.RaiseStructureChanged();
            RestoreCaretCallback?.Invoke(CaretParaBefore, CaretCharBefore);
        }

        void ITextCommand.Apply(DocumentModel doc) => Execute();

        void ITextCommand.Revert(DocumentModel doc) => Undo();

        bool ITextCommand.TryMerge(ITextCommand next) => false;
    }
}
