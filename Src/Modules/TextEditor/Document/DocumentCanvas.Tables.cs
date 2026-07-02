using System;
using System.Collections.Generic;
using Writersword.Core.Models.Rendering;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Document
{
    public sealed partial class DocumentCanvas
    {
        // Строит потоковое выделение ячеек по порядку чтения (Row*ColumnCount+Col) от точки
        // нажатия (_selStartPara/_selStartChar) до курсора (cursorSlice/cursorChar). Заполняет
        // Добавляет в _cellFlowFull все ячейки таблицы с reading-index в [loIdx, hiIdx] целиком.
        // Словари должны быть очищены вызывающим.
        private bool BuildCellFlowWhole(TableBlock table, int loIdx, int hiIdx)
        {
            int colCount = Math.Max(1, table.ColumnCount);
            foreach (var cell in table.Cells)
            {
                int idx = cell.Row * colCount + cell.Column;
                if (idx < loIdx || idx > hiIdx) continue;
                _cellFlowFull.Add((table, cell.Row, cell.Column));
            }
            return _cellFlowFull.Count > 0;
        }

        // Поток при входе текстового выделения в таблицу извне: от верхнего-левого края (fromTop)
        // или нижнего-правого до ячейки курсора включительно. Только ЦЕЛЬНЫЕ ячейки.
        private bool BuildCellFlowFromEdge(TableBlock table, int cursorSlice, int cursorChar, bool fromTop)
        {
            _cellFlowRanges.Clear();
            _cellFlowFull.Clear();
            if (cursorSlice < 0 || cursorSlice >= _layouts.Count) return false;
            var cursorCell = _layouts[cursorSlice].Cell;
            if (cursorCell is null || cursorCell.Table != table) return false;

            int colCount = Math.Max(1, table.ColumnCount);
            int cIdx = cursorCell.Cell.Row * colCount + cursorCell.Cell.Column;
            int maxIdx = (table.RowCount - 1) * colCount + (table.ColumnCount - 1);

            int loIdx = fromTop ? 0 : cIdx;
            int hiIdx = fromTop ? cIdx : maxIdx;
            return BuildCellFlowWhole(table, loIdx, hiIdx);
        }

        // ── Таблица — структурные операции ────────────────────────────────
        private void ExecuteTableAddRow(bool above)
        {
            if (_activeTableBlock is null) return;
            BeginEdit("Add row");
            int insertRow = above ? _activeCellRow : _activeCellRow + 1;
            foreach (var cell in _activeTableBlock.Cells)
                if (cell.Row >= insertRow) cell.Row++;
            for (int c = 0; c < _activeTableBlock.ColumnCount; c++)
                _activeTableBlock.Cells.Add(new TableCell { Row = insertRow, Column = c });
            _activeTableBlock.RowCount++;
            if (above) _activeCellRow++;
            CommitEdit();
            _cellLayoutCache.Clear();
            RebuildLayouts();
            InvalidateFull();
        }

        // Цвет фона ячеек. Применяется к выделенному диапазону ячеек (_tableSelections), а если
        // диапазона нет — к активной ячейке под кареткой. color == null/пусто снимает заливку.
        private void ExecuteTableSetCellBackground(string? color)
        {
            string? value = string.IsNullOrWhiteSpace(color) ? null : color;

            var targets = new List<TableCell>();
            if (_tableSelections.Count > 0)
            {
                foreach (var kv in _tableSelections)
                {
                    int minRow = Math.Min(kv.Value.sr, kv.Value.er);
                    int maxRow = Math.Max(kv.Value.sr, kv.Value.er);
                    int minCol = Math.Min(kv.Value.sc, kv.Value.ec);
                    int maxCol = Math.Max(kv.Value.sc, kv.Value.ec);
                    foreach (var cell in kv.Key.Cells)
                    {
                        if (cell.Row < minRow || cell.Row > maxRow) continue;
                        if (cell.Column < minCol || cell.Column > maxCol) continue;
                        targets.Add(cell);
                    }
                }
            }
            else if (_activeTableBlock is not null)
            {
                foreach (var cell in _activeTableBlock.Cells)
                {
                    if (cell.Row != _activeCellRow || cell.Column != _activeCellCol) continue;
                    targets.Add(cell);
                    break;
                }
            }

            if (targets.Count == 0) return;

            BeginEdit("Set cell background");
            foreach (var cell in targets) cell.BackgroundColor = value;
            CommitEdit();

            _cellLayoutCache.Clear();
            RebuildLayouts();
            InvalidateFull();
        }

        private void ExecuteTableDeleteRow()
        {
            if (_activeTableBlock is null) return;
            BeginEdit("Delete row");
            int deleteRow = _activeCellRow;
            _activeTableBlock.Cells.RemoveAll(c => c.Row == deleteRow);
            foreach (var cell in _activeTableBlock.Cells)
                if (cell.Row > deleteRow) cell.Row--;
            _activeTableBlock.RowCount--;
            CommitEdit();
            if (_activeTableBlock.RowCount <= 0) { ExecuteTableDelete(); return; }
            _activeCellRow = Clamp(_activeCellRow, 0, _activeTableBlock.RowCount - 1);
            _cellLayoutCache.Clear();
            RebuildLayouts();
            InvalidateFull();
        }

        private void ExecuteTableAddColumn(bool left)
        {
            if (_activeTableBlock is null) return;
            BeginEdit("Add column");
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
            CommitEdit();
            _cellLayoutCache.Clear();
            RebuildLayouts();
            InvalidateFull();
        }

        private void ExecuteTableDeleteColumn()
        {
            if (_activeTableBlock is null) return;
            BeginEdit("Delete column");
            int deleteCol = _activeCellCol;
            _activeTableBlock.Cells.RemoveAll(c => c.Column == deleteCol);
            foreach (var cell in _activeTableBlock.Cells)
                if (cell.Column > deleteCol) cell.Column--;
            if (deleteCol < _activeTableBlock.Columns.Count)
                _activeTableBlock.Columns.RemoveAt(deleteCol);
            _activeTableBlock.ColumnCount--;
            CommitEdit();
            if (_activeTableBlock.ColumnCount <= 0) { ExecuteTableDelete(); return; }
            _activeCellCol = Clamp(_activeCellCol, 0, _activeTableBlock.ColumnCount - 1);
            _cellLayoutCache.Clear();
            RebuildLayouts();
            InvalidateFull();
        }

        private void ExecuteTableDelete()
        {
            if (_activeTableBlock is null || DocVm is null) return;
            BeginEdit("Delete table");
            DocVm.Document.Sections[0].Blocks.Remove(_activeTableBlock);
            CommitEdit();
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
        // Абсолютная геометрия каретки (в pt), согласованная с отрисовкой DrawCaret:
        // absXPt — абсолютный X каретки (с учётом отступа абзаца и выравнивания),
        // lineTopPt — абсолютный Y верха строки каретки, lineHeightPt — высота строки.
        private bool TryGetCaretGeometry(out float absXPt, out float lineTopPt, out float lineHeightPt)
        {
            absXPt = 0f; lineTopPt = 0f; lineHeightPt = 0f;
            if (_caretPara < 0 || _caretPara >= _layouts.Count) return false;
            var pl = _layouts[_caretPara];
            var layout = GetRenderLayout(pl, (float)(_canvasWidth * PxToPt));
            if (layout is null || layout.Lines.Count == 0) return false;

            int pos = Clamp(_caretChar, 0, pl.Vm.PlainText?.Length ?? 0);
            var caret = layout.HitTestPosition(pos);
            int drawLineIdx = layout.GetLineIndexForChar(pos);
            float yBase = pl.LineFrom < layout.Lines.Count ? layout.Lines[pl.LineFrom].Y : 0f;
            float firstLineBaked = (drawLineIdx == 0) ? layout.FirstLineIndentPt : 0f;
            float caretAlignOffset = LineAlignShift(layout, drawLineIdx) - firstLineBaked
                + JustifyShiftBeforeChar(layout, drawLineIdx, pos);

            absXPt = pl.AbsXPt + caret.X + caretAlignOffset;
            lineTopPt = pl.Ypt + (caret.Y - yBase);
            lineHeightPt = caret.Height > 0.01f ? caret.Height : FallbackLinePt;
            return true;
        }

        // Перемещение каретки вверх/вниз — чисто геометрическое, тем же HitTest, что и клик.
        // Берём абсолютную точку каретки, держим целевой X (_preferredCaretXPt, абсолютный) и
        // шагаем по Y за границу текущей строки в направлении dir, пока HitTest не попадёт на
        // другую позицию. Это автоматически работает с разными абзацными отступами, разной
        // высотой строк, межабзацными интервалами и таблицами (вход в нужную колонку, переход
        // между строками таблицы, выход из неё) — без спец-логики «в ячейке/не в ячейке».
        private void MoveCaretVertically(int dir)
        {
            if (_layouts.Count == 0) return;

            if (!TryGetCaretGeometry(out float curXPt, out float lineTopPt, out float lineHeightPt))
            {
                // Нет геометрии (пустой/неинициализированный layout) — индексный фолбэк.
                _caretPara = Clamp(_caretPara + dir, 0, _layouts.Count - 1);
                _caretChar = Clamp(_caretChar, 0, GetVmAt(_caretPara)?.PlainText?.Length ?? 0);
                return;
            }

            // Начало серии вертикальных перемещений — фиксируем столбец из ТЕКУЩЕЙ позиции каретки
            // (живая геометрия, без устаревшего значения). Дальше при Up/Down подряд он держится.
            if (!_vNavActive)
            {
                _preferredCaretXPt = curXPt;
                _vNavActive = true;
            }

            float curCenterPt = lineTopPt + lineHeightPt * 0.5f;
            float lineBottomPt = lineTopPt + lineHeightPt;
            float width = (float)(_canvasWidth * PxToPt);

            float bestCenterPt = float.NaN;

            // Шаг 1: соседняя визуальная строка ВНУТРИ текущего абзаца (многострочный абзац).
            {
                var pl = _layouts[_caretPara];
                var lay = GetRenderLayout(pl, width);
                if (lay is not null && lay.Lines.Count > 0)
                {
                    int lf = Clamp(pl.LineFrom, 0, lay.Lines.Count - 1);
                    float yBase = lay.Lines[lf].Y;
                    int lineTo = Math.Min(pl.LineTo, lay.Lines.Count);
                    float bestDelta = float.MaxValue;

                    for (int li = pl.LineFrom; li < lineTo; li++)
                    {
                        var line = lay.Lines[li];
                        float c = pl.Ypt + (line.Y - yBase) + line.Height * 0.5f;
                        float d = c - curCenterPt;
                        if (dir > 0 && d > 0.5f && d < bestDelta) { bestDelta = d; bestCenterPt = c; }
                        else if (dir < 0 && d < -0.5f && -d < bestDelta) { bestDelta = -d; bestCenterPt = c; }
                    }
                }
            }

            // Шаг 2: соседней строки в текущем абзаце нет — ищем ближайшую по вертикали ДРУГУЮ
            // полосу среди ВСЕХ layout (следующий абзац / ячейка соседней строки таблицы / абзац
            // за таблицей). Сканируем по Ypt/HeightPt без построения раскладки (дёшево), берём
            // ближайший в направлении dir. Окном соседних индексов это не находилось: в таблице
            // ячейка строки ниже идёт в порядке чтения далеко. Конкретную колонку под столбцом
            // определит сам HitTest по _preferredCaretXPt.
            if (float.IsNaN(bestCenterPt))
            {
                int targetIdx = -1;
                float bestEdge = dir > 0 ? float.MaxValue : float.MinValue;

                for (int i = 0; i < _layouts.Count; i++)
                {
                    if (i == _caretPara) continue;
                    var p = _layouts[i];
                    if (dir > 0)
                    {
                        if (p.Ypt >= lineBottomPt - 0.5f && p.Ypt < bestEdge)
                        { bestEdge = p.Ypt; targetIdx = i; }
                    }
                    else
                    {
                        float pBot = p.Ypt + p.HeightPt;
                        if (pBot <= lineTopPt + 0.5f && pBot > bestEdge)
                        { bestEdge = pBot; targetIdx = i; }
                    }
                }

                if (targetIdx >= 0)
                {
                    var tp = _layouts[targetIdx];
                    var tlay = GetRenderLayout(tp, width);
                    if (tlay is not null && tlay.Lines.Count > 0)
                    {
                        int lf = Clamp(tp.LineFrom, 0, tlay.Lines.Count - 1);
                        float yBase = tlay.Lines[lf].Y;
                        int lineTo = Math.Min(tp.LineTo, tlay.Lines.Count);
                        int li = dir > 0 ? lf : Math.Max(lf, lineTo - 1);
                        var line = tlay.Lines[li];
                        bestCenterPt = tp.Ypt + (line.Y - yBase) + line.Height * 0.5f;
                    }
                    else
                    {
                        bestCenterPt = tp.Ypt + tp.HeightPt * 0.5f;
                    }
                }
            }

            if (float.IsNaN(bestCenterPt)) return; // край документа — некуда

            // Бьём тем же HitTest, что и клик, по сохранённому абсолютному X и центру найденной
            // строки — каретка встаёт в тот же экранный столбец на соседней строке/в ячейке.
            float pxPerPt = (float)(PtToPx * Zoom);
            var (pi, ci) = HitTest(new Avalonia.Point(_preferredCaretXPt * pxPerPt, bestCenterPt * pxPerPt));
            if (pi >= 0 && pi < _layouts.Count)
            {
                _caretPara = pi;
                _caretChar = ci;
            }
        }

        private void ClampCaret()
        {
            _caretPara = Clamp(_caretPara, 0, Math.Max(0, _layouts.Count - 1));
            _caretChar = Clamp(_caretChar, 0, GetVmAt(_caretPara)?.PlainText?.Length ?? 0);
        }

        private void UpdatePreferredX()
        {
            // Храним АБСОЛЮТНЫЙ X каретки (с учётом отступа абзаца и выравнивания), а не локальный.
            // Иначе при разных абзацных отступах один и тот же локальный X = разный экранный, и
            // вертикальная навигация съезжала. Геометрия — та же, что у отрисовки каретки.
            if (TryGetCaretGeometry(out float absXPt, out _, out _))
                _preferredCaretXPt = absXPt;

            // Это горизонтальное перемещение/клик/правка — следующая серия Up/Down заново возьмёт
            // столбец из текущей позиции каретки.
            _vNavActive = false;
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
                var snapLayout = pl.Layout
                    ?? GetOrBuildLayout(pl.Vm, (float)(_canvasWidth * PxToPt));
                var caretRect = snapLayout.HitTestPosition(pos);
                // RenderParagraphLines рендерит строки со смещением (line.Y - lines[lineFrom].Y).
                // Нужно применять тот же yBase иначе caretAbsY указывает на неправильную
                // страницу и SnapCaretToCorrectSlice выбирает слайс первой страницы вместо второй.
                float yBase = pl.LineFrom < snapLayout.Lines.Count
                    ? snapLayout.Lines[pl.LineFrom].Y : 0f;
                float caretAbsY = pl.Ypt + (caretRect.Y - yBase);
                return caretAbsY >= pl.Cell.ClipY - 0.5f
                    && caretAbsY < pl.Cell.ClipY + pl.Cell.ClipH + 0.5f;
            }

            void CommitSlice(int idx)
            {
                _caretPara = idx;
                var snapped = _layouts[idx];
                int pos = Clamp(_caretChar, 0, snapped.Vm.PlainText?.Length ?? 0);
                var snappedLayout = snapped.Layout
                    ?? GetOrBuildLayout(snapped.Vm, (float)(_canvasWidth * PxToPt));
                int actualLine = snappedLayout.GetLineIndexForChar(pos);

                // GetLineIndexForChar для pos = line[N].LastCharIndex + 1 возвращает N+1,
                // потому что позиция формально принадлежит следующей строке.
                // Но визуально каретка должна стоять в КОНЦЕ строки N, а не в начале N+1.
                // Проверяем: если предыдущая строка заканчивается ровно на pos-1 и не является
                // последней — возвращаемся на неё. DrawCaret с таким hint рисует в конце строки N.
                if (actualLine > 0 && actualLine < snappedLayout.Lines.Count)
                {
                    var prevLine = snappedLayout.Lines[actualLine - 1];
                    // Применяем коррекцию только если _caretLineHint не указывает на actualLine.
                    // Левый клик: HitTest ставит hint=N и pos=FirstCharIndex_N = LastCharIndex_(N-1)+1.
                    // Без проверки hint коррекция переводила бы на конец строки N-1 вместо начала N.
                    if (!prevLine.IsLastLine && pos == prevLine.LastCharIndex + 1
                        && _caretLineHint != actualLine)
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