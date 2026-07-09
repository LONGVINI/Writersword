using System;
using System.Collections.Generic;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;

namespace Writersword.Modules.TextEditor.Commands
{
    /// <summary>
    /// Обратимая структурная правка ОДНОЙ ячейки таблицы (Enter, слияние абзацев, удаление
    /// выделения, ввод поверх выделения). Снимает копию списка абзацев только этой ячейки до и
    /// после операции — дёшево, без сериализации всего документа и пересоздания всех ViewModel
    /// (в отличие от DocumentSnapshotCommand, из-за которого Ctrl+Z в таблице тормозил).
    /// Undo/Redo просто подменяют список абзацев ячейки и восстанавливают позицию каретки.
    /// </summary>
    public sealed class CellParagraphsCommand : ITextCommand
    {
        private readonly TableCell _cell;
        private readonly List<ParagraphBlock> _before;
        private List<ParagraphBlock>? _after;

        private readonly int _caretParaBefore;
        private readonly int _caretCharBefore;
        private int _caretParaAfter;
        private int _caretCharAfter;

        public string Description { get; }

        /// <summary>
        /// (cell, caretParaIdx, caretChar) — канвас пересобирает раскладку ячейки и ставит каретку.
        /// </summary>
        public Action<TableCell, int, int>? AfterChange { get; set; }

        public CellParagraphsCommand(TableCell cell, string description, int caretParaIdx, int caretChar)
        {
            _cell = cell;
            _before = CloneList(cell.Paragraphs);
            _caretParaBefore = caretParaIdx;
            _caretCharBefore = caretChar;
            Description = description;
        }

        public void Commit(int caretParaIdx, int caretChar)
        {
            _after = CloneList(_cell.Paragraphs);
            _caretParaAfter = caretParaIdx;
            _caretCharAfter = caretChar;
        }

        public void Apply(DocumentModel doc)
        {
            if (_after is null) return;
            SetParagraphs(CloneList(_after));
            AfterChange?.Invoke(_cell, _caretParaAfter, _caretCharAfter);
        }

        public void Revert(DocumentModel doc)
        {
            SetParagraphs(CloneList(_before));
            AfterChange?.Invoke(_cell, _caretParaBefore, _caretCharBefore);
        }

        public bool TryMerge(ITextCommand next) => false;

        private void SetParagraphs(List<ParagraphBlock> paras)
        {
            _cell.Paragraphs.Clear();
            foreach (var p in paras)
                _cell.Paragraphs.Add(p);
            if (_cell.Paragraphs.Count == 0)
                _cell.Paragraphs.Add(new ParagraphBlock());
        }

        private static List<ParagraphBlock> CloneList(List<ParagraphBlock> src)
        {
            var list = new List<ParagraphBlock>(src.Count);
            foreach (var p in src)
                list.Add(ClonePara(p));
            return list;
        }

        private static ParagraphBlock ClonePara(ParagraphBlock src)
        {
            var dst = new ParagraphBlock
            {
                Properties = src.Properties.Clone(),
                ListProperties = src.ListProperties?.Clone()
            };
            dst.Chunks.Clear();
            foreach (var chunk in src.Chunks)
            {
                var c = new TextChunk();
                foreach (var run in chunk.Runs)
                    c.Runs.Add(run.Clone());
                dst.Chunks.Add(c);
            }
            if (dst.Chunks.Count == 0)
                dst.Chunks.Add(new TextChunk());
            return dst;
        }
    }
}
