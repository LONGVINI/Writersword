using System;
using System.Collections.Generic;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Вставка текста в ячейки таблицы, как в Word и Excel.
    ///
    /// Текст с табуляциями и переводами строк (так его кладут в буфер Excel, Word и
    /// таблицы браузера) раскладывается по ячейкам: строка текста — строка таблицы,
    /// табуляция — переход в соседний столбец. Отсчёт идёт от левой верхней ячейки
    /// выделения, без выделения — от ячейки с кареткой. Не хватает строк — таблица
    /// растёт вниз, лишние столбцы обрезаются.
    ///
    /// Одно значение при выделенных нескольких ячейках встаёт в каждую из них.
    ///
    /// Вставленное получает оформление ячейки, в которую легло: шрифт и абзац берутся
    /// у её первого абзаца. Вся вставка — один шаг отмены.
    /// </summary>
    public partial class DocumentCanvas
    {
        /// <summary>
        /// Ячейка, от которой ложится вставка, и прямоугольник выделения: левая верхняя
        /// ячейка выделенного диапазона или ячейка с кареткой (прямоугольник 1 на 1).
        /// </summary>
        private (TableBlock Table, int Row, int Column, int Rows, int Columns)? CellPasteAnchor()
        {
            foreach (var (table, sel) in _tableSelections)
            {
                int r0 = Math.Min(sel.sr, sel.er), r1 = Math.Max(sel.sr, sel.er);
                int c0 = Math.Min(sel.sc, sel.ec), c1 = Math.Max(sel.sc, sel.ec);
                return (table, r0, c0, r1 - r0 + 1, c1 - c0 + 1);
            }

            if (GetCurrentCell() is { } info)
                return (info.Table, info.Cell.Row, info.Cell.Column, 1, 1);

            return null;
        }

        /// <summary>
        /// Раскладывает текст по ячейкам. False — раскладывать нечего: одно значение в
        /// одну ячейку вставляется обычным путём, в место каретки внутри текста ячейки.
        /// </summary>
        private bool TryPasteTextGridIntoCells(string text)
        {
            if (TextUndoStack is null || DocVm is null) return false;
            if (CellPasteAnchor() is not { } anchor) return false;

            string normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
            if (normalized.EndsWith('\n')) normalized = normalized[..^1];

            var lines = normalized.Split('\n');
            var grid = new List<string[]>(lines.Length);
            foreach (var line in lines) grid.Add(line.Split('\t'));

            bool single = grid.Count == 1 && grid[0].Length == 1;
            bool rangeSelected = anchor.Rows > 1 || anchor.Columns > 1;
            if (single && !rangeSelected) return false;

            var table = anchor.Table;
            var source = new List<(int r, int c, List<ParagraphBlock> paras, string? bg)>();

            void Put(int r, int c, string value)
            {
                var target = table.GetCell(anchor.Row + r, anchor.Column + c);
                source.Add((r, c, new List<ParagraphBlock> { CellParagraphWithText(target, value) }, target?.BackgroundColor));
            }

            if (single)
            {
                for (int r = 0; r < anchor.Rows; r++)
                    for (int c = 0; c < anchor.Columns; c++)
                        Put(r, c, grid[0][0]);
            }
            else
            {
                for (int r = 0; r < grid.Count; r++)
                    for (int c = 0; c < grid[r].Length; c++)
                        Put(r, c, grid[r][c]);
            }

            if (source.Count == 0) return false;

            var anchorCell = table.GetCell(anchor.Row, anchor.Column);
            var cmd = new Commands.PasteCellsCommand(table, anchor.Row, anchor.Column, source);
            cmd.AfterChange = () => AfterCellPaste(anchorCell);
            cmd.Apply(DocVm.Document);
            PushTextCommand(cmd);
            return true;
        }

        /// <summary>
        /// Абзац с текстом в оформлении ячейки: копия её первого абзаца, текст которой
        /// заменён вставляемым, — шрифт и абзац остаются как были у ячейки.
        /// </summary>
        private static ParagraphBlock CellParagraphWithText(TableCell? cell, string value)
        {
            if (cell is { Paragraphs.Count: > 0 }
                && Services.DocumentCloner.CloneBlock(cell.Paragraphs[0]) is ParagraphBlock copy)
            {
                copy.SpliceText(0, copy.GetPlainText().Length, value);
                return copy;
            }

            var paragraph = new ParagraphBlock();
            paragraph.SetPlainText(value);
            return paragraph;
        }

        /// <summary>После вставки в ячейки: пересборка и каретка в начало первой ячейки вставки.</summary>
        private void AfterCellPaste(TableCell? anchorCell)
        {
            InvalidateCellLayoutCaches();
            _cellVmCache.Clear();
            RebuildLayouts();

            if (anchorCell is not null)
            {
                for (int i = 0; i < _layouts.Count; i++)
                {
                    if (ReferenceEquals(_layouts[i].Cell?.Cell, anchorCell))
                    {
                        _caretPara = i;
                        _caretChar = 0;
                        break;
                    }
                }
            }

            SnapCaretToCorrectSlice();
            NotifyCaretEnteredTableCallback();
            SyncSel();
            ResetCaret();
            InvalidateFull();
        }
    }
}
