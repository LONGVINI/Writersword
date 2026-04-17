using System;
using System.Collections.Generic;
using Writersword.Core.Models.Rendering;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Document
{
    public sealed partial class DocumentCanvas
    {
        // ── Таблица — структурные операции ────────────────────────────────
        private void ExecuteTableAddRow(bool above)
        {
            if (_activeTableBlock is null) return;
            int insertRow = above ? _activeCellRow : _activeCellRow + 1;
            foreach (var cell in _activeTableBlock.Cells)
                if (cell.Row >= insertRow) cell.Row++;
            for (int c = 0; c < _activeTableBlock.ColumnCount; c++)
                _activeTableBlock.Cells.Add(new TableCell { Row = insertRow, Column = c });
            _activeTableBlock.RowCount++;
            if (above) _activeCellRow++;
            _cellLayoutCache.Clear();
            RebuildLayouts();
            InvalidateFull();
        }

        private void ExecuteTableDeleteRow()
        {
            if (_activeTableBlock is null) return;
            int deleteRow = _activeCellRow;
            _activeTableBlock.Cells.RemoveAll(c => c.Row == deleteRow);
            foreach (var cell in _activeTableBlock.Cells)
                if (cell.Row > deleteRow) cell.Row--;
            _activeTableBlock.RowCount--;
            if (_activeTableBlock.RowCount <= 0) { ExecuteTableDelete(); return; }
            _activeCellRow = Clamp(_activeCellRow, 0, _activeTableBlock.RowCount - 1);
            _cellLayoutCache.Clear();
            RebuildLayouts();
            InvalidateFull();
        }

        private void ExecuteTableAddColumn(bool left)
        {
            if (_activeTableBlock is null) return;
            int insertCol = left ? _activeCellCol : _activeCellCol + 1;
            foreach (var cell in _activeTableBlock.Cells)
                if (cell.Column >= insertCol) cell.Column++;
            for (int r = 0; r < _activeTableBlock.RowCount; r++)
                _activeTableBlock.Cells.Add(new TableCell { Row = r, Column = insertCol });
            var colDef = new TableColumnDefinition { WidthType = TableColumnWidthType.Auto };
            if (insertCol < _activeTableBlock.Columns.Count)
                _activeTableBlock.Columns.Insert(insertCol, colDef);
            else
                _activeTableBlock.Columns.Add(colDef);
            _activeTableBlock.ColumnCount++;
            if (left) _activeCellCol++;
            _cellLayoutCache.Clear();
            RebuildLayouts();
            InvalidateFull();
        }

        private void ExecuteTableDeleteColumn()
        {
            if (_activeTableBlock is null) return;
            int deleteCol = _activeCellCol;
            _activeTableBlock.Cells.RemoveAll(c => c.Column == deleteCol);
            foreach (var cell in _activeTableBlock.Cells)
                if (cell.Column > deleteCol) cell.Column--;
            if (deleteCol < _activeTableBlock.Columns.Count)
                _activeTableBlock.Columns.RemoveAt(deleteCol);
            _activeTableBlock.ColumnCount--;
            if (_activeTableBlock.ColumnCount <= 0) { ExecuteTableDelete(); return; }
            _activeCellCol = Clamp(_activeCellCol, 0, _activeTableBlock.ColumnCount - 1);
            _cellLayoutCache.Clear();
            RebuildLayouts();
            InvalidateFull();
        }

        private void ExecuteTableDelete()
        {
            if (_activeTableBlock is null || DocVm is null) return;
            DocVm.Document.Sections[0].Blocks.Remove(_activeTableBlock);
            _cellVmCache.Clear();
            _cellLayoutCache.Clear();
            DocVm.RebuildParagraphViewModelsPublic();
            NotifyLeftCell();
            _caretPara = Clamp(_caretPara, 0, Math.Max(0, _layouts.Count - 1));
            _caretChar = 0;
            RebuildLayouts();
            InvalidateFull();
        }

        // ── Вертикальная навигация ────────────────────────────────────────
        private void MoveCaretVertically(int dir)
        {
            bool inCell = IsInCell(_caretPara);
            var layout = GetLayoutAt(_caretPara);
            if (layout is null)
            {
                if (!inCell)
                {
                    _caretPara = Clamp(_caretPara + dir, 0, _layouts.Count - 1);
                    _caretChar = Clamp(_caretChar, 0, GetVmAt(_caretPara)?.PlainText?.Length ?? 0);
                }
                return;
            }

            int lineIdx = layout.GetLineIndexForChar(_caretChar);
            int targetLine = lineIdx + dir;

            if (targetLine >= 0 && targetLine < layout.Lines.Count)
            {
                _caretChar = layout.GetCharIndexForVerticalMove(
                    _caretChar, dir, _preferredCaretXPt);
                return;
            }

            if (inCell)
            {
                // В ячейке: переходим на параграф выше/ниже в той же ячейке
                var cell = GetCurrentCell()!;
                int newParaIdx = _caretPara + dir;
                if (newParaIdx >= 0 && newParaIdx < _layouts.Count)
                {
                    var next = _layouts[newParaIdx];
                    if (next.Cell?.Cell == cell.Cell)
                    {
                        _caretPara = newParaIdx;
                        var nextLayout = next.Layout;
                        if (nextLayout.Lines.Count > 0)
                        {
                            var fl = dir > 0 ? nextLayout.Lines[0] : nextLayout.Lines[^1];
                            var hit = nextLayout.HitTestPoint(
                                _preferredCaretXPt - nextLayout.LeftIndentPt,
                                fl.Y + fl.Height * 0.5f);
                            _caretChar = hit.CharIndex;
                        }
                        else _caretChar = 0;
                        return;
                    }
                }
                // Упёрлись в край ячейки — переходим на параграф вне таблицы.
                // Ищем ближайший layout который не принадлежит этой таблице.
                var tableBlock = cell.Table;
                if (dir < 0)
                {
                    // Ищем вверх — первый layout не из этой таблицы
                    for (int i = _caretPara - 1; i >= 0; i--)
                    {
                        if (_layouts[i].Cell?.Table != tableBlock)
                        {
                            _caretPara = i;
                            var lyt = _layouts[i].Layout;
                            if (lyt is not null && lyt.Lines.Count > 0)
                                _caretChar = lyt.Lines[^1].LastCharIndex + 1;
                            else
                                _caretChar = GetVmAt(i)?.PlainText?.Length ?? 0;
                            return;
                        }
                    }
                }
                else
                {
                    // Ищем вниз — первый layout не из этой таблицы
                    for (int i = _caretPara + 1; i < _layouts.Count; i++)
                    {
                        if (_layouts[i].Cell?.Table != tableBlock)
                        {
                            _caretPara = i;
                            var lyt = _layouts[i].Layout;
                            if (lyt is not null && lyt.Lines.Count > 0)
                                _caretChar = lyt.Lines[0].FirstCharIndex;
                            else
                                _caretChar = 0;
                            return;
                        }
                    }
                }
                return;
            }

            // Обычный параграф
            if (dir < 0 && _caretPara > 0)
            {
                _caretPara--;
                var prev = GetLayoutAt(_caretPara);
                if (prev is not null && prev.Lines.Count > 0)
                {
                    var ll = prev.Lines[^1];
                    var hit = prev.HitTestPoint(
                        _preferredCaretXPt - prev.LeftIndentPt,
                        ll.Y + ll.Height * 0.5f);
                    _caretChar = hit.CharIndex;
                }
                else _caretChar = GetVmAt(_caretPara)?.PlainText?.Length ?? 0;
            }
            else if (dir > 0 && _caretPara < _layouts.Count - 1)
            {
                _caretPara++;
                var next = GetLayoutAt(_caretPara);
                if (next is not null && next.Lines.Count > 0)
                {
                    var fl = next.Lines[0];
                    var hit = next.HitTestPoint(
                        _preferredCaretXPt - next.LeftIndentPt,
                        fl.Y + fl.Height * 0.5f);
                    _caretChar = hit.CharIndex;
                }
                else _caretChar = 0;
            }
        }

        private void ClampCaret()
        {
            _caretPara = Clamp(_caretPara, 0, Math.Max(0, _layouts.Count - 1));
            _caretChar = Clamp(_caretChar, 0, GetVmAt(_caretPara)?.PlainText?.Length ?? 0);
        }

        private void UpdatePreferredX()
        {
            var layout = GetLayoutAt(_caretPara);
            if (layout is null) return;
            var caret = layout.HitTestPosition(_caretChar);
            _preferredCaretXPt = caret.X;
        }

        private void SnapCaretToCorrectSlice()
        {
            if (_layouts.Count == 0) return;
            _caretPara = Clamp(_caretPara, 0, _layouts.Count - 1);

            var targetVm = GetVmAt(_caretPara);
            if (targetVm is null) return;

            var layout = GetLayoutAt(_caretPara);
            if (layout is null) return;

            int lineIdx = layout.GetLineIndexForChar(_caretChar);

            // Для ByCell-split ячейки в _layouts присутствуют два слайса с одинаковым VM,
            // LineFrom=0 и LineTo=lineCount — стандартное совпадение по диапазону строк
            // всегда выбирает первый (страница 1). Используем clip-Y как тай-брейкер:
            // правильным является тот слайс, в чьём clip-прямоугольнике находится каретка.
            bool InClip(int idx)
            {
                var pl = _layouts[idx];
                if (pl.Cell == null) return true; // обычный параграф — всегда подходит
                int pos = Clamp(_caretChar, 0, pl.Vm.PlainText?.Length ?? 0);
                var caretRect = pl.Layout.HitTestPosition(pos);
                // RenderParagraphLines рендерит строки со смещением (line.Y - lines[lineFrom].Y).
                // Нужно применять тот же yBase иначе caretAbsY указывает на неправильную
                // страницу и SnapCaretToCorrectSlice выбирает слайс первой страницы вместо второй.
                float yBase = pl.LineFrom < pl.Layout.Lines.Count
                    ? pl.Layout.Lines[pl.LineFrom].Y : 0f;
                float caretAbsY = pl.Ypt + (caretRect.Y - yBase);
                return caretAbsY >= pl.Cell.ClipY - 0.5f
                    && caretAbsY < pl.Cell.ClipY + pl.Cell.ClipH + 0.5f;
            }

            void CommitSlice(int idx)
            {
                _caretPara = idx;
                var snapped = _layouts[idx];
                int pos = Clamp(_caretChar, 0, snapped.Vm.PlainText?.Length ?? 0);
                int actualLine = snapped.Layout.GetLineIndexForChar(pos);

                // GetLineIndexForChar для pos = line[N].LastCharIndex + 1 возвращает N+1,
                // потому что позиция формально принадлежит следующей строке.
                // Но визуально каретка должна стоять в КОНЦЕ строки N, а не в начале N+1.
                // Проверяем: если предыдущая строка заканчивается ровно на pos-1 и не является
                // последней — возвращаемся на неё. DrawCaret с таким hint рисует в конце строки N.
                if (actualLine > 0 && actualLine < snapped.Layout.Lines.Count)
                {
                    var prevLine = snapped.Layout.Lines[actualLine - 1];
                    if (!prevLine.IsLastLine && pos == prevLine.LastCharIndex + 1)
                        actualLine--;
                }

                _caretLineHint = (actualLine >= snapped.LineFrom && actualLine < snapped.LineTo)
                    ? actualLine
                    : (snapped.LineFrom < snapped.LineTo ? snapped.LineFrom : -1);
            }

            if (_caretLineHint >= 0)
            {
                int firstHintMatch = -1;
                for (int i = 0; i < _layouts.Count; i++)
                {
                    var pl = _layouts[i];
                    if (pl.Vm != targetVm) continue;
                    if (_caretLineHint < pl.LineFrom || _caretLineHint >= pl.LineTo) continue;

                    if (firstHintMatch < 0) firstHintMatch = i;
                    if (InClip(i)) { CommitSlice(i); return; }
                }
                if (firstHintMatch >= 0) { CommitSlice(firstHintMatch); return; }
            }

            // Проверяем текущий слайс — если он уже правильный и каретка в его clip, выходим.
            var currentPl = _layouts[_caretPara];
            if (currentPl.Vm == targetVm
                && lineIdx >= currentPl.LineFrom && lineIdx < currentPl.LineTo
                && InClip(_caretPara))
            {
                _caretLineHint = lineIdx;
                return;
            }

            // Полный перебор: ищем слайс с совпадением по диапазону строк и clip-Y.
            int firstLineMatch = -1;
            for (int i = 0; i < _layouts.Count; i++)
            {
                var pl = _layouts[i];
                if (pl.Vm != targetVm) continue;
                if (lineIdx < pl.LineFrom || lineIdx >= pl.LineTo) continue;

                if (firstLineMatch < 0) firstLineMatch = i;
                if (InClip(i)) { _caretPara = i; _caretLineHint = lineIdx; return; }
            }
            if (firstLineMatch >= 0) { _caretPara = firstLineMatch; _caretLineHint = lineIdx; }
        }

    }
}