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

        // Вложенные таблицы ячейки и их места среди абзацев. Абзацы шаг возвращает
        // копиями с новыми идентификаторами, поэтому привязка таблицы к абзацу по
        // идентификатору отката не переживает — место запоминается номером абзаца.
        // Сами таблицы хранятся ссылками: их содержимое этим шагом не меняется.
        private readonly List<NestedAnchor>? _nestedBefore;
        private List<NestedAnchor>? _nestedAfter;

        private readonly record struct NestedAnchor(NestedTable Nested, int Position);

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
            _nestedBefore = CaptureNested(cell);
            _caretParaBefore = caretParaIdx;
            _caretCharBefore = caretChar;
            Description = description;
        }

        public void Commit(int caretParaIdx, int caretChar)
        {
            _after = CloneList(_cell.Paragraphs);
            _nestedAfter = CaptureNested(_cell);
            _caretParaAfter = caretParaIdx;
            _caretCharAfter = caretChar;
        }

        public void Apply(DocumentModel doc)
        {
            if (_after is null) return;
            SetParagraphs(CloneList(_after));
            SetNestedTables(_nestedAfter);
            AfterChange?.Invoke(_cell, _caretParaAfter, _caretCharAfter);
        }

        public void Revert(DocumentModel doc)
        {
            SetParagraphs(CloneList(_before));
            SetNestedTables(_nestedBefore);
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

        private static List<NestedAnchor>? CaptureNested(TableCell cell)
        {
            if (cell.NestedTables is not { Count: > 0 } nestedTables) return null;

            var anchors = new List<NestedAnchor>(nestedTables.Count);
            foreach (var nested in nestedTables)
                anchors.Add(new NestedAnchor(nested, cell.NestedTablePosition(nested)));
            return anchors;
        }

        // Вызывается после SetParagraphs: таблицы привязываются к абзацам, которые
        // только что встали в ячейку.
        private void SetNestedTables(List<NestedAnchor>? anchors)
        {
            if (anchors is not { Count: > 0 })
            {
                _cell.NestedTables = null;
                return;
            }

            var nestedTables = new List<NestedTable>(anchors.Count);
            foreach (var anchor in anchors)
            {
                int position = Math.Clamp(anchor.Position, 0, _cell.Paragraphs.Count);
                anchor.Nested.BeforeParagraphIndex = position;
                anchor.Nested.BeforeParagraphId = position < _cell.Paragraphs.Count
                    ? _cell.Paragraphs[position].Id
                    : Guid.Empty;
                nestedTables.Add(anchor.Nested);
            }

            _cell.NestedTables = nestedTables;
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
