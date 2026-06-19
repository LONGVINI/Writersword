using Avalonia;
using Avalonia.Input;
using Avalonia.Threading;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Writersword.Core.Models.Rendering;
using Writersword.Modules.TextEditor.Rendering;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.Commands;
using Writersword.Modules.TextEditor.ViewModels;

namespace Writersword.Modules.TextEditor.Document
{
    public sealed partial class DocumentCanvas
    {
        // ── Pointer ───────────────────────────────────────────────────────
        // ── Ручки таблицы — HitTest ───────────────────────────────────────

        private enum TableHandleType { None, ColResize, TableMove, RowResize }

        private struct TableHandleHit
        {
            public TableHandleType Type;
            public int EntryIdx;
            public int ColIndex;   // ColResize: индекс колонки; RowResize: индекс строки
        }

        // Hit-зона вокруг линии в pt (~4px при zoom=1)
        private const float TableLineHitPt = 4f * PxToPt;

        private TableHandleHit HitTestTableHandle(float xPt, float yPt)
        {
            List<TableEntry> tables;
            lock (_renderLock) { tables = _tables; }

            for (int ti = 0; ti < tables.Count; ti++)
            {
                var te = tables[ti];
                float tX = te.XPt;
                float tY = te.Ypt;
                float tW = te.Layout.TotalWidthPt;
                float r = TableLineHitPt;

                // Вычисляем реальную высоту слайса (не полную высоту таблицы).
                int effectiveRowTo = te.RowTo < 0 ? te.Layout.Rows.Count : te.RowTo;
                float sliceH = 0f;
                for (int ri = te.RowFrom; ri < effectiveRowTo && ri < te.Layout.Rows.Count; ri++)
                {
                    float rowH = te.Layout.Rows[ri].HeightPt;
                    if (ri == te.RowFrom) rowH -= te.FirstRowContentOffsetPt;
                    if (ri == effectiveRowTo - 1 && te.LastRowVisibleHeightPt >= 0f)
                        rowH = te.LastRowVisibleHeightPt;
                    sliceH += rowH;
                }

                // Грубая проверка по слайсу.
                if (yPt < tY - r || yPt > tY + sliceH + r) continue;
                if (xPt < tX - r || xPt > tX + tW + r) continue;

                // Вертикальные линии (ColResize, TableMove) — по всей высоте слайса.
                bool onTableY = yPt >= tY - r && yPt <= tY + sliceH + r;
                if (onTableY)
                {
                    if (Math.Abs(xPt - tX) <= r)
                        return new TableHandleHit { Type = TableHandleType.TableMove, EntryIdx = ti };

                    float accX = tX;
                    for (int i = 0; i < te.Layout.ColumnWidthsPt.Count; i++)
                    {
                        accX += te.Layout.ColumnWidthsPt[i];
                        if (Math.Abs(xPt - accX) <= r)
                            return new TableHandleHit
                            {
                                Type = TableHandleType.ColResize,
                                EntryIdx = ti,
                                ColIndex = i
                            };
                    }
                }

                // Горизонтальные линии (RowResize) — позиции строк с учётом смещения слайса.
                bool onTableX = xPt >= tX - r && xPt <= tX + tW + r;
                if (onTableX)
                {
                    float accY = tY;
                    for (int ri = te.RowFrom; ri < effectiveRowTo && ri < te.Layout.Rows.Count; ri++)
                    {
                        float rowH = te.Layout.Rows[ri].HeightPt;
                        if (ri == te.RowFrom) rowH -= te.FirstRowContentOffsetPt;
                        if (ri == effectiveRowTo - 1 && te.LastRowVisibleHeightPt >= 0f)
                            rowH = te.LastRowVisibleHeightPt;
                        accY += rowH;
                        if (Math.Abs(yPt - accY) <= r)
                            return new TableHandleHit
                            {
                                Type = TableHandleType.RowResize,
                                EntryIdx = ti,
                                ColIndex = te.Layout.Rows[ri].Row
                            };
                    }
                }
            }
            return new TableHandleHit { Type = TableHandleType.None };
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            Focus();

            var pt = e.GetPosition(this);
            double zoom = Zoom;
            float xPt = (float)(pt.X / zoom * PxToPt);
            float yPt = (float)(pt.Y / zoom * PxToPt);

            // ── Проверяем ручки таблицы ПЕРВЫМИ ─────────────────────────
            var handleHit = HitTestTableHandle(xPt, yPt);
            if (handleHit.Type != TableHandleType.None)
            {
                _tableDragMode = (TableDragMode)(int)handleHit.Type;
                _tableDragEntryIdx = handleHit.EntryIdx;
                _tableDragColIndex = handleHit.ColIndex;
                _tableDragStartXPt = xPt;

                var te = _tables[handleHit.EntryIdx];
                if (handleHit.Type == TableHandleType.ColResize)
                {
                    // Берём фактическую ширину из layout (может быть Auto → вычисленная)
                    // и конвертируем pt → мм
                    float colWidthPt = handleHit.ColIndex < te.Layout.ColumnWidthsPt.Count
                        ? te.Layout.ColumnWidthsPt[handleHit.ColIndex]
                        : 20f;
                    _tableDragStartVal = (float)(colWidthPt * 25.4 / 72.0); // мм
                }
                else
                {
                    _tableDragStartVal = (float)te.Table.LeftIndentPt; // pt
                }

                // Входим в таблицу если ещё не там
                if (_activeTableBlock == null || !ReferenceEquals(_activeTableBlock, te.Table))
                {
                    _activeTableBlock = te.Table;
                    _activeCellTableEntryIdx = handleHit.EntryIdx;
                    if (DocVm is not null) DocVm.ActiveTable = te.Table;
                }

                Cursor = new Cursor(handleHit.Type == TableHandleType.RowResize
                    ? StandardCursorType.SizeNorthSouth
                    : StandardCursorType.SizeWestEast);
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }

            var (pi, ci) = HitTest(pt);

            // Определяем: это ячейка таблицы?
            bool wasInCell = IsInCell(_caretPara);
            bool nowInCell = pi >= 0 && pi < _layouts.Count && _layouts[pi].Cell != null;

            // Определяем ячейку нажатия через геометрический поиск — он корректно
            // находит пустые ячейки (нет layout-записи) и ячейки с курсором за краем текста.
            _pressCellTable = null; _pressCellRow = -1; _pressCellCol = -1;
            {
                double zoom2 = Zoom;
                float xPtPress = (float)(pt.X / zoom2 * PxToPt);
                float yPtPress = (float)(pt.Y / zoom2 * PxToPt);
                var geoPress = HitTestTableCellGeometric(xPtPress, yPtPress);
                if (geoPress.HasValue)
                {
                    _pressCellTable = geoPress.Value.table;
                    _pressCellRow = geoPress.Value.row;
                    _pressCellCol = geoPress.Value.col;
                }
                else if (nowInCell)
                {
                    var c = _layouts[pi].Cell!;
                    _pressCellTable = c.Table;
                    _pressCellRow = c.Cell.Row;
                    _pressCellCol = c.Cell.Column;
                }
            }

            _caretPara = pi;
            _caretChar = ci;
            _selStartPara = pi; _selStartChar = ci;
            _selEndPara = pi; _selEndChar = ci;
            _isSelecting = true;
            _isCellRangeSelecting = false;
            _cellSelTable = null;
            _tableSelections.Clear();

            SnapCaretToCorrectSlice();
            UpdatePreferredX();

            // Уведомляем о смене контекста (ячейка / параграф)
            UpdateCellContext(wasInCell, nowInCell);

            // Обновляем активный параграф для риббона
            if (!nowInCell)
            {
                var pvm = GetVmAt(_caretPara);
                if (pvm is not null) DocVm?.SetActiveParagraph(pvm);
            }
            else
            {
                var cell = _layouts[_caretPara].Cell!;
                DocVm?.FireTableCellCursorContext(cell.ParaBlock);
            }

            UpdateSelectionContext();
            ResetCaretNoScroll(); InvalidateFull();
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);

            double zoom = Zoom;
            var rawPt = e.GetPosition(this);
            float xPt = (float)(rawPt.X / zoom * PxToPt);
            float yPt = (float)(rawPt.Y / zoom * PxToPt);

            // ── Drag ручки таблицы ────────────────────────────────────────
            if (_tableDragMode != TableDragMode.None)
            {
                if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                {
                    FinishTableDrag();
                    return;
                }

                float deltaPt = xPt - _tableDragStartXPt;

                if (_tableDragMode == TableDragMode.TableMove)
                {
                    // Сдвигаем всю таблицу: LeftIndentPt += delta (без ограничений)
                    if (_activeTableBlock is not null)
                    {
                        _activeTableBlock.LeftIndentPt = _tableDragStartVal + deltaPt;
                        if (DocVm is not null) DocVm.ActiveTable = _activeTableBlock;
                        _cellLayoutCache.Clear();
                        RebuildLayouts();
                        NotifyCaretEnteredTableCallback();
                        InvalidateFull();
                    }
                }
                else if (_tableDragMode == TableDragMode.ColResize)
                {
                    // Изменяем ширину колонки: WidthValue (мм) + delta (pt → мм)
                    if (_activeTableBlock is not null
                        && _tableDragColIndex >= 0
                        && _tableDragColIndex < _activeTableBlock.Columns.Count)
                    {
                        double deltaMm = deltaPt * 25.4 / 72.0;
                        // _tableDragStartVal = ширина колонки в мм на момент нажатия
                        double newMm = Math.Max(5.0, _tableDragStartVal + deltaMm);
                        _activeTableBlock.Columns[_tableDragColIndex].WidthType = TableColumnWidthType.Fixed;
                        _activeTableBlock.Columns[_tableDragColIndex].WidthValue = newMm;
                        if (DocVm is not null) DocVm.ActiveTable = _activeTableBlock;
                        _cellLayoutCache.Clear();
                        RebuildLayouts();
                        NotifyCaretEnteredTableCallback();
                        InvalidateFull();
                    }
                }
                else if (_tableDragMode == TableDragMode.RowResize)
                {
                    // Изменяем высоту строки по вертикальному drag (Y delta)
                    float deltaYPt = yPt - _tableDragStartXPt; // используем StartXPt как startYPt
                    if (_activeTableBlock is not null
                        && _tableDragColIndex >= 0
                        && _activeCellTableEntryIdx >= 0
                        && _activeCellTableEntryIdx < _tables.Count)
                    {
                        var te = _tables[_activeCellTableEntryIdx];
                        if (_tableDragColIndex < te.Layout.Rows.Count)
                        {
                            // RowHeight задаём через свойство RowHeight на модели
                            // Сохраняем min 5pt
                            double newHeightPt = Math.Max(5.0, _tableDragStartVal + deltaYPt);
                            // Применяем ко всем ячейкам строки через RowHeightPt в TableBlock
                            // (если нет отдельного поля — пока пропускаем, только rebuild)
                        }
                    }
                }

                e.Handled = true;
                return;
            }

            // ── Курсор при наведении на ручки ─────────────────────────────
            if (!_isSelecting)
            {
                var handleHit = HitTestTableHandle(xPt, yPt);
                Cursor = handleHit.Type switch
                {
                    TableHandleType.RowResize => new Cursor(StandardCursorType.SizeNorthSouth),
                    TableHandleType.ColResize or TableHandleType.TableMove
                        => new Cursor(StandardCursorType.SizeWestEast),
                    _ => new Cursor(StandardCursorType.Ibeam)
                };
            }

            if (!_isSelecting) return;
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

            var (pi, ci) = HitTest(rawPt);
            bool nowInCell = pi >= 0 && pi < _layouts.Count && _layouts[pi].Cell != null;

            // Нажатие было в ячейке таблицы.
            if (_pressCellTable != null)
            {
                var geoEnd = HitTestTableCellGeometric(xPt, yPt);
                bool sameCell = geoEnd.HasValue
                    && geoEnd.Value.table == _pressCellTable
                    && geoEnd.Value.row == _pressCellRow
                    && geoEnd.Value.col == _pressCellCol;

                if (sameCell)
                {
                    // Курсор в той же ячейке — обычное текстовое выделение внутри ячейки.
                    _isCellRangeSelecting = false;
                    _cellSelTable = null;
                    _tableSelections.Clear();
                    _selEndPara = pi; _selEndChar = ci;
                    _caretPara = pi; _caretChar = ci;
                    UpdateSelectionContext();
                    InvalidateFull();
                    e.Handled = true;
                    return;
                }

                if (geoEnd.HasValue && geoEnd.Value.table == _pressCellTable)
                {
                    // Курсор перешёл в другую ячейку той же таблицы — cell-range.
                    _isCellRangeSelecting = true;
                    _cellSelTable = _pressCellTable;
                    _tableSelections.Clear();
                    _tableSelections[_pressCellTable] = (
                        _pressCellRow, _pressCellCol,
                        geoEnd.Value.row, geoEnd.Value.col);
                    _cellSelStartRow = _pressCellRow;
                    _cellSelStartCol = _pressCellCol;
                    _cellSelEndRow = geoEnd.Value.row;
                    _cellSelEndCol = geoEnd.Value.col;
                    InvalidateFull();
                    e.Handled = true;
                    return;
                }

                // Курсор вышел за пределы таблицы — переходим в параграфный режим.
                _isCellRangeSelecting = false;
                _cellSelTable = null;
                _tableSelections.Clear();
                // Не делаем return — продолжаем в Y-range блок.
            }
            // Drag начался вне таблицы — все таблицы в Y-диапазоне выделяются целиком.
            {
                float selStartYPt = _selStartPara >= 0 && _selStartPara < _layouts.Count
                    ? _layouts[_selStartPara].Ypt : 0f;

                // Если курсор сейчас внутри ячейки, Y нижней границы параграфа
                // совпадает с tblMaxY — перекрытие обнуляется и таблица ошибочно
                // выбрасывается из выделения. Используем верхний Y параграфа (без HeightPt):
                // так selEndYPt оказывается внутри таблицы, а не у её дна.
                float selEndYPt;
                if (nowInCell && pi >= 0 && pi < _layouts.Count)
                    selEndYPt = _layouts[pi].Ypt;
                else if (pi >= 0 && pi < _layouts.Count)
                    selEndYPt = _layouts[pi].Ypt + _layouts[pi].HeightPt;
                else
                    selEndYPt = 0f;

                float selMinY = Math.Min(selStartYPt, selEndYPt);
                float selMaxY = Math.Max(selStartYPt, selEndYPt);

                List<TableEntry> allTables;
                lock (_renderLock) { allTables = _tables; }

                var seenTables = new HashSet<TableBlock>();
                var toRemove = new List<TableBlock>();

                foreach (var te in allTables)
                {
                    if (!seenTables.Add(te.Table)) continue;

                    float tblMinY = float.MaxValue;
                    float tblMaxY = float.MinValue;
                    foreach (var t2 in allTables)
                    {
                        if (t2.Table != te.Table) continue;
                        int effectiveRowTo = t2.RowTo < 0 ? t2.Layout.Rows.Count : t2.RowTo;
                        float sliceH = 0f;
                        for (int ri = t2.RowFrom; ri < effectiveRowTo && ri < t2.Layout.Rows.Count; ri++)
                        {
                            float rh = t2.Layout.Rows[ri].HeightPt;
                            if (ri == t2.RowFrom) rh -= t2.FirstRowContentOffsetPt;
                            if (ri == effectiveRowTo - 1 && t2.LastRowVisibleHeightPt >= 0f) rh = t2.LastRowVisibleHeightPt;
                            sliceH += rh;
                        }
                        tblMinY = Math.Min(tblMinY, t2.Ypt);
                        tblMaxY = Math.Max(tblMaxY, t2.Ypt + sliceH);
                    }

                    // Порог применяется только когда drag начался у верхнего края таблицы
                    // (якорный параграф нулевой высоты над таблицей): в этом случае
                    // требуем реального вхождения внутрь таблицы перед выделением.
                    // Если drag начался под таблицей или внутри неё — порог минимальный
                    // (только защита от floating-point), иначе выделение снизу вверх
                    // не захватывает таблицу пока мышь в нижних строках.
                    float overlapThreshold = selStartYPt <= tblMinY ? 5f : 0.5f;
                    float overlapStart = Math.Max(tblMinY, selMinY);
                    float overlapEnd = Math.Min(tblMaxY, selMaxY);
                    if (overlapEnd - overlapStart < overlapThreshold)
                        toRemove.Add(te.Table);
                    else
                        _tableSelections[te.Table] = (0, 0, te.Table.RowCount - 1, te.Table.ColumnCount - 1);
                }

                foreach (var t in toRemove) _tableSelections.Remove(t);

                var knownTables = new HashSet<TableBlock>(allTables.Select(t => t.Table));
                foreach (var t in _tableSelections.Keys.Where(k => !knownTables.Contains(k)).ToList())
                    _tableSelections.Remove(t);
            }

            _isCellRangeSelecting = false;
            _cellSelTable = null;
            _selEndPara = pi; _selEndChar = ci;
            _caretPara = pi; _caretChar = ci;
            UpdateSelectionContext();
            InvalidateFull();
            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);

            if (_tableDragMode != TableDragMode.None)
            {
                FinishTableDrag();
                e.Pointer.Capture(null);
                Cursor = new Cursor(StandardCursorType.Ibeam);
                e.Handled = true;
                return;
            }

            _isSelecting = false;
            UpdateSelectionContext();
        }

        private void FinishTableDrag()
        {
            if (_tableDragMode == TableDragMode.ColResize && _activeTableBlock is not null)
            {
                // Фиксируем ВСЕ колонки по текущим ширинам из layout
                if (_activeCellTableEntryIdx >= 0 && _activeCellTableEntryIdx < _tables.Count)
                {
                    var te = _tables[_activeCellTableEntryIdx];
                    for (int i = 0; i < _activeTableBlock.Columns.Count
                                    && i < te.Layout.ColumnWidthsPt.Count; i++)
                    {
                        _activeTableBlock.Columns[i].WidthType = TableColumnWidthType.Fixed;
                        _activeTableBlock.Columns[i].WidthValue = te.Layout.ColumnWidthsPt[i] * 25.4 / 72.0;
                    }
                }
            }

            _tableDragMode = TableDragMode.None;
            _tableDragEntryIdx = -1;
            _tableDragColIndex = -1;
        }

        // ── Keyboard ─────────────────────────────────────────────────────
        protected override void OnTextInput(TextInputEventArgs e)
        {
            base.OnTextInput(e);
            if (string.IsNullOrEmpty(e.Text)) return;
            _caretLineHint = -1;

            if (_isCellRangeSelecting)
            {
                bool singleCell = _tableSelections.Count == 1 &&
                    _tableSelections.Values.First() is var sel &&
                    sel.sr == sel.er && sel.sc == sel.ec;

                if (singleCell && IsInCell(_caretPara))
                {
                    var cell = GetCurrentCell();
                    if (cell != null)
                    {
                        BeginEdit("Replace cell text");
                        while (cell.Cell.Paragraphs.Count > 1)
                            cell.Cell.Paragraphs.RemoveAt(cell.Cell.Paragraphs.Count - 1);
                        SetCellParaText(cell.Cell, 0, e.Text);
                        _caretChar = e.Text.Length;
                        CommitEdit();
                        _isCellRangeSelecting = false;
                        _cellSelTable = null;
                        _tableSelections.Clear();
                        RebuildAfterCellEdit();
                    }
                }
                else
                {
                    _isCellRangeSelecting = false;
                    _cellSelTable = null;
                    _tableSelections.Clear();
                    SyncSel();
                    ResetCaret();
                    InvalidateFull();
                }

                e.Handled = true;
                return;
            }

            InsertText(e.Text);
            e.Handled = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (_hotKeyService is not null)
            {
                var gesture = new KeyGesture(e.Key, e.KeyModifiers);
                if (_hotKeyService.HandleKeyPress(gesture, "TextEditor"))
                {
                    e.Handled = true;
                    return;
                }
            }

            HandleKeyFallback(e);
        }

        private void HandleKeyFallback(KeyEventArgs e)
        {
            bool shft = e.KeyModifiers == KeyModifiers.Shift;
            bool ctrl = e.KeyModifiers == KeyModifiers.Control;

            // Tab: навигация по ячейкам
            if (e.Key == Key.Tab && IsInCell(_caretPara))
            {
                if (shft) NavigateCellPrev(); else NavigateCellNext();
                e.Handled = true;
                return;
            }

            switch (e.Key)
            {
                case Key.Back: ExecuteDeleteBackSmart(); e.Handled = true; break;
                case Key.Delete: ExecuteDeleteForwardSmart(); e.Handled = true; break;
                case Key.Enter: ExecuteNewParagraphSmart(); e.Handled = true; break;

                case Key.Left: ExecuteNavLeft(shft); e.Handled = true; break;
                case Key.Right: ExecuteNavRight(shft); e.Handled = true; break;
                case Key.Up: ExecuteNavUp(shft); e.Handled = true; break;
                case Key.Down: ExecuteNavDown(shft); e.Handled = true; break;

                case Key.Home: ExecuteHome(ctrl, shft); e.Handled = true; break;
                case Key.End: ExecuteEnd(ctrl, shft); e.Handled = true; break;

                case Key.C when ctrl: ExecuteCopy(); e.Handled = true; break;
                case Key.X when ctrl: ExecuteCut(); e.Handled = true; break;
                case Key.V when ctrl: ExecutePaste(); e.Handled = true; break;
                case Key.A when ctrl: ExecuteSelectAll(); e.Handled = true; break;

                case Key.Z when ctrl: ExecuteUndo(); e.Handled = true; break;
                case Key.Y when ctrl: ExecuteRedo(); e.Handled = true; break;

                case Key.Escape when IsInCell(_caretPara):
                    // Escape из ячейки — перемещаем каретку на параграф после таблицы
                    EscapeCell();
                    e.Handled = true;
                    break;
            }
        }

        // ── Cell navigation ───────────────────────────────────────────────

        private bool IsInCell(int layoutIdx)
            => layoutIdx >= 0 && layoutIdx < _layouts.Count && _layouts[layoutIdx].Cell != null;

        private CellInfo? GetCurrentCell()
            => IsInCell(_caretPara) ? _layouts[_caretPara].Cell : null;

        /// <summary>
        /// Обновляет _activeTableBlock и уведомляет линейку о входе/выходе из ячейки.
        /// </summary>
        private void UpdateCellContext(bool wasInCell, bool nowInCell)
        {
            if (!wasInCell && !nowInCell) return;

            if (!nowInCell && wasInCell)
            {
                NotifyLeftCell();
                return;
            }

            if (nowInCell)
            {
                var cell = _layouts[_caretPara].Cell!;
                _activeTableBlock = cell.Table;
                _activeCellRow = cell.Cell.Row;
                _activeCellCol = cell.Cell.Column;
                _activeCellTableEntryIdx = cell.TableEntryIdx;

                // Сообщаем DocumentViewModel об активной таблице — линейка использует
                // это чтобы применять изменения ширины/отступа к правильной таблице.
                if (DocVm is not null) DocVm.ActiveTable = cell.Table;

                // Регистрируем делегаты структурных операций
                if (DocVm is { } vm)
                {
                    vm.TableAddRowDelegate = ExecuteTableAddRow;
                    vm.TableAddColDelegate = ExecuteTableAddColumn;
                    vm.TableDeleteRowDelegate = ExecuteTableDeleteRow;
                    vm.TableDeleteColDelegate = ExecuteTableDeleteColumn;
                    vm.TableDeleteDelegate = ExecuteTableDelete;
                    vm.TableSetCellBackgroundDelegate = ExecuteTableSetCellBackground;
                    vm.TableSetLeftEdgeDelegate = leftIndentPt =>
                    {
                        if (_activeTableBlock is null) return;
                        _activeTableBlock.LeftIndentPt = leftIndentPt; // без ограничений
                        _cellLayoutCache.Clear();
                        RebuildLayouts();
                        NotifyCaretEnteredTableCallback();
                        InvalidateFull();
                    };
                }

                NotifyCaretEnteredTableCallback();
            }
        }

        private void NotifyLeftCell()
        {
            if (_activeTableBlock is null) return;
            _activeTableBlock = null;
            _activeCellTableEntryIdx = -1;

            if (DocVm is { } vm)
            {
                vm.ActiveTable = null;
                vm.TableAddRowDelegate = null;
                vm.TableAddColDelegate = null;
                vm.TableDeleteRowDelegate = null;
                vm.TableDeleteColDelegate = null;
                vm.TableDeleteDelegate = null;
                vm.TableSetCellBackgroundDelegate = null;
                vm.TableSetLeftEdgeDelegate = null;
            }

            DocVm?.SetActiveParagraph(GetVmAt(_caretPara) ?? DocVm.Paragraphs.FirstOrDefault()!);
            CaretLeftTable?.Invoke();
        }

        private void NotifyCaretEnteredTableCallback()
        {
            if (_activeCellTableEntryIdx < 0 || _activeCellTableEntryIdx >= _tables.Count) return;

            var te = _tables[_activeCellTableEntryIdx];
            var offsets = new List<double>();
            var widths = new List<double>();

            foreach (var w in te.Layout.ColumnWidthsPt) widths.Add(PtToMm(w));
            foreach (var o in te.Layout.ColumnOffsetsPt) offsets.Add(PtToMm(o));

            double tableOffsetMm = PtToMm(te.XPt);
            if (_pages.Count > 0 && te.PageIndex < _pages.Count)
            {
                var pg = _pages[te.PageIndex];
                tableOffsetMm = PtToMm(te.XPt - (pg.PadLeftPt + pg.MarginLeftPt));
            }

            CaretEnteredTable?.Invoke(offsets, widths, tableOffsetMm, _activeCellCol);
        }

        /// <summary>Tab — следующая ячейка.</summary>
        private void NavigateCellNext()
        {
            // Ищем следующий layout entry в другой ячейке той же таблицы
            var curCell = GetCurrentCell();
            if (curCell is null) return;

            for (int i = _caretPara + 1; i < _layouts.Count; i++)
            {
                var pl = _layouts[i];
                if (pl.Cell?.Table != curCell.Table)
                {
                    // Вышли за пределы таблицы — переходим на первый не-ячеечный элемент
                    _caretPara = i;
                    _caretChar = 0;
                    bool wasInCell = true;
                    UpdateCellContext(wasInCell, false);
                    SyncSel(); ResetCaret(); InvalidateFull();
                    return;
                }
                // Первый параграф следующей ячейки
                if (pl.Cell?.Cell != curCell.Cell && pl.Cell?.CellParaIndex == 0)
                {
                    _caretPara = i;
                    _caretChar = 0;
                    UpdateCellContext(true, true);
                    if (DocVm is not null) DocVm.FireTableCellCursorContext(pl.Cell!.ParaBlock);
                    SyncSel(); ResetCaret(); InvalidateFull();
                    return;
                }
            }

            // Конец таблицы — ищем первый параграф после таблицы в _layouts
            for (int i = _caretPara + 1; i < _layouts.Count; i++)
            {
                if (_layouts[i].Cell == null)
                {
                    _caretPara = i; _caretChar = 0;
                    UpdateCellContext(true, false);
                    SyncSel(); ResetCaret(); InvalidateFull();
                    return;
                }
            }
        }

        /// <summary>Shift+Tab — предыдущая ячейка.</summary>
        private void NavigateCellPrev()
        {
            var curCell = GetCurrentCell();
            if (curCell is null) return;

            // Ищем первый параграф ячейки (CellParaIndex == 0) идущей перед текущей
            int prevCellStart = -1;

            for (int i = _caretPara - 1; i >= 0; i--)
            {
                var pl = _layouts[i];
                if (pl.Cell?.Table != curCell.Table)
                {
                    // Вышли за пределы таблицы
                    _caretPara = i; _caretChar = GetVmAt(i)?.PlainText?.Length ?? 0;
                    UpdateCellContext(true, false);
                    SyncSel(); ResetCaret(); InvalidateFull();
                    return;
                }
                if (pl.Cell?.Cell != curCell.Cell && pl.Cell?.CellParaIndex == 0)
                {
                    prevCellStart = i;
                    break;
                }
            }

            if (prevCellStart >= 0)
            {
                _caretPara = prevCellStart;
                _caretChar = GetVmAt(prevCellStart)?.PlainText?.Length ?? 0;
                UpdateCellContext(true, true);
                if (DocVm is not null) DocVm.FireTableCellCursorContext(_layouts[prevCellStart].Cell!.ParaBlock);
                SyncSel(); ResetCaret(); InvalidateFull();
            }
        }

        private void EscapeCell()
        {
            // Ищем первый не-ячеечный элемент после таблицы
            for (int i = _caretPara + 1; i < _layouts.Count; i++)
            {
                if (_layouts[i].Cell == null)
                {
                    _caretPara = i; _caretChar = 0;
                    UpdateCellContext(true, false);
                    SyncSel(); ResetCaret(); InvalidateFull();
                    return;
                }
            }
        }

        // ── Вставка / удаление текста (умные — работают в ячейке и вне) ──

        /// <summary>
        /// Удаляет выделенный текст внутри одной ячейки.
        /// Возвращает true если выделение было и удаление выполнено.
        /// </summary>
        private bool CellDeleteSelection()
        {
            if (!HasSel()) return false;

            var (sp, sc, ep, ec) = NormalizeSelection();

            // Работаем только если оба конца выделения в одной ячейке
            var startCell = sp >= 0 && sp < _layouts.Count ? _layouts[sp].Cell : null;
            var endCell = ep >= 0 && ep < _layouts.Count ? _layouts[ep].Cell : null;
            if (startCell is null || endCell is null) return false;
            if (startCell.Cell != endCell.Cell) return false;

            if (sp == ep)
            {
                // Выделение внутри одного параграфа ячейки
                string t = startCell.ParaBlock.GetPlainText();
                int s2 = Clamp(sc, 0, t.Length);
                int e2 = Clamp(ec, 0, t.Length);
                SetCellParaText(startCell.Cell, startCell.CellParaIndex, t[..s2] + t[e2..]);
                _caretChar = s2;
                _caretPara = sp;
            }
            else
            {
                // Выделение через несколько параграфов одной ячейки
                var startPara = startCell.Cell.Paragraphs[startCell.CellParaIndex];
                var endPara = endCell.Cell.Paragraphs[endCell.CellParaIndex];
                string st = startPara.GetPlainText();
                string et = endPara.GetPlainText();
                int s2 = Clamp(sc, 0, st.Length);
                int e2 = Clamp(ec, 0, et.Length);

                // Оставляем начало первого параграфа + конец последнего
                SetCellParaText(startCell.Cell, startCell.CellParaIndex, st[..s2] + et[e2..]);

                // Удаляем промежуточные и последний параграфы
                int fromIdx = startCell.CellParaIndex + 1;
                int toIdx = endCell.CellParaIndex;
                for (int i = toIdx; i >= fromIdx; i--)
                    startCell.Cell.Paragraphs.RemoveAt(i);

                _caretChar = s2;
                _caretPara = sp;
            }

            SyncSel();
            return true;
        }

        private void InsertText(string text)
        {
            if (IsInCell(_caretPara))
            {
                CellInsertText(text);
                return;
            }

            // Быстрый путь: нет выделения и есть лёгкий стек операций.
            // Каждая запись undo хранит только позицию и текст вместо полного JSON документа.
            if (TextUndoStack != null && !HasSel())
            {
                var pvm = GetVmAt(_caretPara);
                if (pvm is null) return;

                string t = pvm.PlainText ?? "";
                int pos = Clamp(_caretChar, 0, t.Length);

                pvm.Model.SpliceText(pos, pos, text);
                pvm.RefreshPlainTextFromModel();
                _caretChar = pos + text.Length;

                var cmd = new Writersword.Modules.TextEditor.Commands.InsertTextCommand(
                    pvm.Model.Id, pos, text);

                // Callback восстанавливает каретку и обновляет VM после Undo/Redo.
                cmd.RestoreCaretCallback = (paraId, charPos) =>
                {
                    for (int i = 0; i < _layouts.Count; i++)
                    {
                        if (_layouts[i].Cell is null && _layouts[i].Vm?.Model?.Id == paraId)
                        {
                            _caretPara = i;
                            _caretChar = charPos;
                            _layouts[i].Vm?.RefreshPlainTextFromModel();
                            break;
                        }
                    }
                    SnapCaretToCorrectSlice();
                    SyncSel();
                    ResetCaret();
                    InvalidateFull();
                };

                TextUndoStack.Push(cmd);
                UpdatePreferredX();
                SyncSel(); ResetCaret();
                return;
            }

            // Legacy путь: есть выделение или TextUndoStack не установлен.
            BeginEdit("Type text");
            DeleteSelection();

            var pvmLegacy = GetVmAt(_caretPara);
            if (pvmLegacy is null) return;

            string tLegacy = pvmLegacy.PlainText ?? "";
            int posLegacy = Clamp(_caretChar, 0, tLegacy.Length);
            pvmLegacy.Model.SpliceText(posLegacy, posLegacy, text);
            pvmLegacy.RefreshPlainTextFromModel();
            _caretChar = posLegacy + text.Length;

            CommitEdit();
            UpdatePreferredX();
            SyncSel(); ResetCaret();
        }

        private void CellInsertText(string text)
        {
            var cell = GetCurrentCell();
            if (cell is null) return;

            BeginEdit("Type text");

            if (HasSel()) { CellDeleteSelection(); RebuildAfterCellEdit(); }

            cell = GetCurrentCell();
            if (cell is null) { CommitEdit(); return; }

            string t = cell.ParaBlock.GetPlainText();
            int pos = Clamp(_caretChar, 0, t.Length);
            SetCellParaText(cell.Cell, cell.CellParaIndex, t[..pos] + text + t[pos..]);
            _caretChar = pos + text.Length;

            CommitEdit();
            RebuildAfterCellEdit();
        }

        public void ExecuteDeleteBackSmart()
        {
            _caretLineHint = -1;

            if (_isCellRangeSelecting)
            {
                ClearCellRangeSelection();
                return;
            }

            // Параграфное выделение захватило таблицы — удаляем их из документа.
            if (_tableSelections.Count > 0 && DocVm is not null)
            {
                DeleteSelectedTablesAndText();
                return;
            }

            if (IsInCell(_caretPara))
            {
                CellDeleteBack();
                return;
            }
            ExecuteDeleteBack();
        }

        public void ExecuteDeleteForwardSmart()
        {
            _caretLineHint = -1;

            if (_isCellRangeSelecting)
            {
                ClearCellRangeSelection();
                return;
            }

            // Параграфное выделение захватило таблицы — удаляем их из документа.
            if (_tableSelections.Count > 0 && DocVm is not null)
            {
                DeleteSelectedTablesAndText();
                return;
            }

            if (IsInCell(_caretPara))
            {
                CellDeleteForward();
                return;
            }
            ExecuteDeleteForward();
        }

        public void ExecuteNewParagraphSmart()
        {
            if (IsInCell(_caretPara))
            {
                CellNewParagraph();
                return;
            }
            ExecuteNewParagraph();
        }

        private void CellDeleteBack()
        {
            var cell = GetCurrentCell();
            if (cell is null) return;

            BeginEdit("Delete");

            if (HasSel())
            {
                CellDeleteSelection();
                CommitEdit();
                RebuildAfterCellEdit();
                return;
            }

            string t = cell.ParaBlock.GetPlainText();

            if (_caretChar > 0 && t.Length > 0)
            {
                int p = Clamp(_caretChar, 1, t.Length);
                SetCellParaText(cell.Cell, cell.CellParaIndex, t[..(p - 1)] + t[p..]);
                _caretChar = p - 1;
            }
            else if (cell.CellParaIndex > 0)
            {
                var prev = cell.Cell.Paragraphs[cell.CellParaIndex - 1];
                string pt = prev.GetPlainText();
                SetCellParaText(cell.Cell, cell.CellParaIndex - 1, pt + t);
                cell.Cell.Paragraphs.RemoveAt(cell.CellParaIndex);
                _caretChar = pt.Length;
            }
            // else: начало первого параграфа ячейки — блокируем (нельзя выйти)

            CommitEdit();
            RebuildAfterCellEdit();
        }

        private void CellDeleteForward()
        {
            var cell = GetCurrentCell();
            if (cell is null) return;

            BeginEdit("Delete");

            if (HasSel())
            {
                CellDeleteSelection();
                CommitEdit();
                RebuildAfterCellEdit();
                return;
            }

            string t = cell.ParaBlock.GetPlainText();

            if (_caretChar < t.Length)
            {
                int p = Clamp(_caretChar, 0, t.Length - 1);
                SetCellParaText(cell.Cell, cell.CellParaIndex, t[..p] + t[(p + 1)..]);
            }
            else if (cell.CellParaIndex < cell.Cell.Paragraphs.Count - 1)
            {
                var next = cell.Cell.Paragraphs[cell.CellParaIndex + 1];
                string nt = next.GetPlainText();
                SetCellParaText(cell.Cell, cell.CellParaIndex, t + nt);
                cell.Cell.Paragraphs.RemoveAt(cell.CellParaIndex + 1);
            }
            // else: конец последнего параграфа ячейки — блокируем

            CommitEdit();
            RebuildAfterCellEdit();
        }

        private void CellNewParagraph()
        {
            BeginEdit("New paragraph");

            if (HasSel())
            {
                CellDeleteSelection();
                RebuildAfterCellEdit();
            }

            var cell = GetCurrentCell();
            if (cell is null) { CommitEdit(); return; }

            string t = cell.ParaBlock.GetPlainText();
            int pos = Clamp(_caretChar, 0, t.Length);

            SetCellParaText(cell.Cell, cell.CellParaIndex, t[..pos]);

            var newPara = new ParagraphBlock();
            if (pos < t.Length)
            {
                var chunk = new TextChunk();
                chunk.Runs.Add(new RunModel { Text = t[pos..] });
                newPara.Chunks.Add(chunk);
            }
            cell.Cell.Paragraphs.Insert(cell.CellParaIndex + 1, newPara);
            _caretChar = 0;

            CommitEdit();
            RebuildAfterCellEdit(newPara);
        }

        private void RebuildAfterCellEdit(ParagraphBlock? explicitTarget = null)
        {
            // Запоминаем параграф ячейки для снапа после rebuild.
            // Если передан явный целевой параграф (например новый после Enter) — используем его.
            ParagraphBlock? targetBlock = explicitTarget;
            if (targetBlock is null && IsInCell(_caretPara))
            {
                var cell = GetCurrentCell()!;
                // После удаления/вставки нужно снапнуться на правильный параграф.
                // Если текущий параграф ячейки всё ещё существует — на него.
                // Если нет (был удалён через merge) — на предыдущий.
                int idx = cell.CellParaIndex;
                if (idx < cell.Cell.Paragraphs.Count)
                    targetBlock = cell.Cell.Paragraphs[idx];
                else if (cell.Cell.Paragraphs.Count > 0)
                    targetBlock = cell.Cell.Paragraphs[cell.Cell.Paragraphs.Count - 1];
            }

            _cellLayoutCache.Clear();
            _cellVmCache.Clear();
            double oldCanvasH = _canvasHeight;
            RebuildLayouts();

            if (Math.Abs(_canvasHeight - oldCanvasH) > 0.5)
                InvalidateMeasure();

            // Snap: находим нужный слайс через SnapCaretToCorrectSlice.
            // Ищет слайс по диапазону строк (LineFrom/LineTo), а не по ClipY/ClipH,
            // поэтому корректно работает при переходе на новую страницу во время печати.
            // Предварительно устанавливаем _caretPara на любой слайс нужной VM —
            // SnapCaretToCorrectSlice начинает поиск от targetVm = GetVmAt(_caretPara).
            if (targetBlock != null && _cellVmCache.TryGetValue(targetBlock, out var targetVm))
            {
                for (int i = 0; i < _layouts.Count; i++)
                {
                    if (_layouts[i].Vm == targetVm) { _caretPara = i; break; }
                }
            }
            SnapCaretToCorrectSlice();

            NotifyCaretEnteredTableCallback();

            // Контекст ячейки для линейки
            if (IsInCell(_caretPara) && DocVm is not null)
                DocVm.FireTableCellCursorContext(_layouts[_caretPara].Cell!.ParaBlock);

            SyncSel(); ResetCaret(); InvalidateFull();
        }

        private void SetCellParaText(TableCell cell, int paraIdx, string text)
        {
            if (paraIdx >= cell.Paragraphs.Count) return;
            var para = cell.Paragraphs[paraIdx];
            if (para.Chunks.Count == 0)
            {
                var chunk = new TextChunk();
                chunk.Runs.Add(new RunModel { Text = text });
                para.Chunks.Add(chunk);
            }
            else
            {
                para.Chunks[0].Runs.Clear();
                para.Chunks[0].Runs.Add(new RunModel { Text = text });
                para.Chunks[0].InvalidateLength();
            }

            // Синхронизируем VM из кеша — иначе PlainText?.Length будет устаревшим,
            // что приводит к неправильному Clamp(_caretChar) в DrawCaret и навигации.
            // vm.PlainText setter записывает в модель повторно (безопасно) и обновляет _plainText.
            if (_cellVmCache.TryGetValue(para, out var vm))
                vm.PlainText = text;
        }

        // ── Публичные команды ─────────────────────────────────────────────
        public void ExecuteDeleteBack()
        {
            _caretLineHint = -1;
            var pvm = GetVmAt(_caretPara);
            if (pvm is null) return;
            string text = pvm.PlainText ?? "";
            BeginEdit("Delete");
            if (HasSel()) { DeleteSelection(); CommitEdit(); ResetCaret(); InvalidateFull(); return; }
            if (_caretChar > 0 && text.Length > 0)
            {
                int p = Clamp(_caretChar, 1, text.Length);
                pvm.Model.SpliceText(p - 1, p, string.Empty);
                pvm.RefreshPlainTextFromModel();
                _caretChar = p - 1;
            }
            else if (_caretChar == 0 && _caretPara > 0 && !IsInCell(_caretPara))
            {
                if (string.IsNullOrEmpty(text) && IsBreakAnchor(pvm.Model))
                {
                    // Backspace на якоре разрыва страницы — удаляем разрыв.
                    DocVm?.DeleteBreakWithAnchor(pvm);
                }
                else if (IsBlockAfterTable(pvm.Model))
                {
                    // Правый якорь таблицы — защищён полностью, Backspace ничего не делает.
                    // Он нужен только для позиционирования каретки, не для редактирования.
                }
                else if (string.IsNullOrEmpty(text) && IsBlockBeforeTable(pvm.Model))
                {
                    // Левый якорь таблицы — переходим в предыдущий параграф
                    // и удаляем там последний символ, сам якорь не трогаем.
                    var prevVm = GetVmAt(_caretPara - 1);
                    if (prevVm is not null)
                    {
                        string prevText = prevVm.PlainText ?? "";
                        if (prevText.Length > 0)
                        {
                            prevVm.Model.SpliceText(prevText.Length - 1, prevText.Length, string.Empty);
                            prevVm.RefreshPlainTextFromModel();
                            _caretPara--;
                            _caretChar = prevVm.PlainText?.Length ?? 0;
                        }
                        else
                        {
                            // Предыдущий тоже пустой — просто переходим туда.
                            _caretPara--;
                            _caretChar = 0;
                        }
                    }
                }
                else
                {
                    DocVm?.MergeParagraphWithPrevious(pvm, text);
                }
            }
            CommitEdit();
            SnapCaretToCorrectSlice();
            UpdatePreferredX();
            SyncSel(); ResetCaret(); InvalidateFull();
        }

        public void ExecuteDeleteForward()
        {
            _caretLineHint = -1;
            var pvm = GetVmAt(_caretPara);
            if (pvm is null) return;
            string text = pvm.PlainText ?? "";
            BeginEdit("Delete");
            if (HasSel()) { DeleteSelection(); CommitEdit(); ResetCaret(); InvalidateFull(); return; }
            if (_caretChar < text.Length)
            {
                int p = Clamp(_caretChar, 0, text.Length - 1);
                pvm.Model.SpliceText(p, p + 1, string.Empty);
                pvm.RefreshPlainTextFromModel();
            }
            else if (_caretPara < _layouts.Count - 1 && !IsInCell(_caretPara))
            {
                var next = GetVmAt(_caretPara + 1);
                // Не сливаем параграф со следующим если следующий — якорь после таблицы.
                bool nextIsPostTableAnchor = next is not null
                    && string.IsNullOrEmpty(next.PlainText)
                    && DocVm is not null
                    && IsBlockAfterTable(next.Model);
                // Delete в конце параграфа перед разрывом страницы = удаление разрыва.
                bool nextIsBreakAnchor = next is not null
                    && string.IsNullOrEmpty(next.PlainText)
                    && DocVm is not null
                    && IsBreakAnchor(next.Model);
                // Текущий параграф — правый якорь таблицы: Delete ничего не делает.
                // Он защищён симметрично Backspace — нельзя слить его со следующим.
                bool currentIsPostTableAnchor = string.IsNullOrEmpty(text)
                    && DocVm is not null
                    && IsBlockAfterTable(pvm.Model);
                // Текущий параграф — левый якорь таблицы: Delete ничего не делает,
                // следующий блок — таблица, к ней нельзя ничего присоединять.
                bool currentIsPreTableAnchor = string.IsNullOrEmpty(text)
                    && DocVm is not null
                    && IsBlockBeforeTable(pvm.Model);
                if (currentIsPostTableAnchor || currentIsPreTableAnchor)
                {
                    // Delete на любом якоре таблицы ничего не делает.
                }
                else if (nextIsBreakAnchor)
                    DocVm?.DeleteBreakWithAnchor(next!);
                else if (next is not null && !IsInCell(_caretPara + 1) && !nextIsPostTableAnchor)
                {
                    // Сливаем следующий параграф в текущий, сохраняя форматирование обоих.
                    string nextText = next.PlainText ?? "";
                    pvm.Model.SpliceText(text.Length, text.Length, nextText);
                    pvm.RefreshPlainTextFromModel();
                    DocVm?.DeleteParagraph(next);
                }
            }
            CommitEdit();
            SnapCaretToCorrectSlice();
            UpdatePreferredX();
            SyncSel(); ResetCaret(); InvalidateFull();
        }

        public void ExecuteNewParagraph()
        {
            var pvm = GetVmAt(_caretPara);
            if (pvm is null) return;
            BeginEdit("New paragraph");
            DeleteSelection();
            string text = pvm.PlainText ?? "";
            int cp = Clamp(_caretChar, 0, text.Length);
            // Форматирование рана в точке каретки — чтобы новый абзац продолжал шрифт и
            // начертание, даже если переброс делается в конце строки (новый абзац пустой).
            var caretRunProps = GetRunPropertiesAt(pvm.Model, cp);
            int plainLen = pvm.Model.GetPlainText().Length;
            int cutPos = Clamp(cp, 0, plainLen);
            // Забираем хвост абзаца ВМЕСТЕ с форматированием каждого рана и удаляем его из
            // исходного. Перенос ранами, а не строкой: иначе разнобойное форматирование хвоста
            // (где-то жирный, другой шрифт) сбрасывалось бы на одно, хотя текст не трогали.
            var tailRuns = DocumentModelHelper.DeleteRange(pvm.Model, cutPos, plainLen - cutPos);
            pvm.RefreshPlainTextFromModel();
            var newVm = DocVm?.AddParagraphAfter(pvm);
            if (newVm is not null)
            {
                // Восстанавливаем раны хвоста в новом абзаце с их исходными свойствами.
                if (tailRuns.Length > 0)
                    DocumentModelHelper.RestoreRuns(newVm.Model, 0, tailRuns);
                newVm.RefreshPlainTextFromModel();

                // Если новый абзац пуст (Enter в конце строки) — переносим форматирование
                // каретки в его пустой ран, чтобы последующий ввод шёл тем же шрифтом.
                if (string.IsNullOrEmpty(newVm.PlainText) && caretRunProps is not null
                    && newVm.Model.Chunks.Count > 0 && newVm.Model.Chunks[0].Runs.Count > 0)
                    newVm.Model.Chunks[0].Runs[0].Properties = caretRunProps.Clone();

                _rebuildCts.Cancel();
                _rebuildCts = new System.Threading.CancellationTokenSource();
                RebuildLayouts();
                for (int i = 0; i < _layouts.Count; i++)
                    if (_layouts[i].Vm == newVm) { _caretPara = i; _caretChar = 0; break; }
            }
            CommitEdit();
            SnapCaretToCorrectSlice();
            UpdatePreferredX();
            SyncSel(); ResetCaret(); InvalidateFull();
        }

        // Возвращает форматирование рана в позиции каретки: берём символ ПЕРЕД кареткой
        // (его продолжит ввод), либо символ в самой позиции. Для пустого абзаца — null.
        private static RunProperties? GetRunPropertiesAt(ParagraphBlock block, int charIndex)
        {
            int idx = 0;
            RunProperties? atPos = null;
            RunProperties? beforePos = null;
            foreach (var chunk in block.Chunks)
                foreach (var run in chunk.Runs)
                    foreach (var _ in run.Text)
                    {
                        if (idx == charIndex) atPos = run.Properties;
                        if (idx == charIndex - 1) beforePos = run.Properties;
                        idx++;
                    }
            return beforePos ?? atPos;
        }

        public void ExecuteNavLeft(bool extend)
        {
            _caretLineHint = -1;
            bool inCell = IsInCell(_caretPara);

            if (HasSel() && !extend)
            { var (sp, sc, _, _) = NormalizeSelection(); _caretPara = sp; _caretChar = sc; }
            else if (_caretChar > 0)
                _caretChar--;
            else if (_caretPara > 0 && !inCell)
            { _caretPara--; _caretChar = GetVmAt(_caretPara)?.PlainText?.Length ?? 0; }
            // В ячейке: не выходим за начало через стрелки

            SnapCaretToCorrectSlice();
            if (!extend) SyncSel(); else ExtendSel();
            UpdatePreferredX();
            ResetCaret(); InvalidateFull();
        }

        public void ExecuteNavRight(bool extend)
        {
            _caretLineHint = -1;
            bool inCell = IsInCell(_caretPara);
            int len = GetVmAt(_caretPara)?.PlainText?.Length ?? 0;

            if (HasSel() && !extend)
            { var (_, _, ep, ec) = NormalizeSelection(); _caretPara = ep; _caretChar = ec; }
            else if (_caretChar < len)
                _caretChar++;
            else if (_caretPara < _layouts.Count - 1 && !inCell)
            { _caretPara++; _caretChar = 0; }
            // В ячейке: не выходим за конец через стрелки

            // Если шаг вправо привёл ровно на мягкий перенос (конец визуальной строки, где висит
            // хвостовой пробел, а слово ушло на следующую строку), привязываем каретку к концу
            // этой строки. Иначе она перепрыгнет на начало следующей строки (тот же офсет
            // принадлежит обеим). Hint ставится до Snap — дальше CommitSlice отработает сам.
            int wrapLine = WrapBoundaryLineForChar(
                GetLayoutAt(_caretPara), GetVmAt(_caretPara)?.PlainText, _caretChar);
            if (wrapLine >= 0) _caretLineHint = wrapLine;

            SnapCaretToCorrectSlice();
            if (!extend) SyncSel(); else ExtendSel();
            UpdatePreferredX();
            ResetCaret(); InvalidateFull();
        }

        // Индекс визуальной строки, у которой charPos == LastCharIndex + 1 и которая не последняя,
        // то есть позиция стоит ровно на мягком переносе. Привязку к концу строки даём только если
        // строка заканчивается пробелом (хвостовой пробел висит на ней, слово ушло дальше) — для
        // переноса в середине длинного слова оставляем начало следующей строки. -1 если нет.
        private static int WrapBoundaryLineForChar(SKTextLayout? layout, string? text, int charPos)
        {
            if (layout is null) return -1;
            for (int i = 0; i < layout.Lines.Count; i++)
            {
                var ln = layout.Lines[i];
                if (ln.IsLastLine || charPos != ln.LastCharIndex + 1) continue;
                int lastIdx = ln.LastCharIndex;
                if (text != null && lastIdx >= 0 && lastIdx < text.Length
                    && char.IsWhiteSpace(text[lastIdx]))
                    return i;
                return -1;
            }
            return -1;
        }

        public void ExecuteNavUp(bool extend)
        {
            _caretLineHint = -1;
            MoveCaretVertically(-1);
            SnapCaretToCorrectSlice();
            if (!extend) SyncSel(); else ExtendSel();
            ResetCaret(); InvalidateFull();
        }

        public void ExecuteNavDown(bool extend)
        {
            _caretLineHint = -1;
            MoveCaretVertically(+1);
            SnapCaretToCorrectSlice();
            if (!extend) SyncSel(); else ExtendSel();
            ResetCaret(); InvalidateFull();
        }

        public void ExecuteHome(bool document, bool extend)
        {
            if (document) { _caretPara = 0; _caretChar = 0; }
            else
            {
                var layout = GetLayoutAt(_caretPara);
                if (layout is not null)
                {
                    int li = layout.GetLineIndexForChar(_caretChar);
                    _caretChar = li >= 0 && li < layout.Lines.Count
                        ? layout.Lines[li].FirstCharIndex : 0;
                }
                else _caretChar = 0;
            }
            SnapCaretToCorrectSlice();
            if (!extend) SyncSel(); else ExtendSel();
            UpdatePreferredX();
            ResetCaret(); InvalidateFull();
        }

        public void ExecuteEnd(bool document, bool extend)
        {
            if (document)
            {
                _caretPara = _layouts.Count - 1;
                _caretChar = GetVmAt(_caretPara)?.PlainText?.Length ?? 0;
            }
            else
            {
                int len = GetVmAt(_caretPara)?.PlainText?.Length ?? 0;
                var layout = GetLayoutAt(_caretPara);
                if (layout is not null)
                {
                    int li = layout.GetLineIndexForChar(_caretChar);
                    _caretChar = li >= 0 && li < layout.Lines.Count
                        ? layout.Lines[li].LastCharIndex + 1 : len;
                }
                else _caretChar = len;
            }
            SnapCaretToCorrectSlice();
            if (!extend) SyncSel(); else ExtendSel();
            UpdatePreferredX();
            ResetCaret(); InvalidateFull();
        }

        public void ExecuteSelectAll()
        {
            if (_layouts.Count == 0) return;
            _selStartPara = 0; _selStartChar = 0;
            _selEndPara = _layouts.Count - 1;
            _selEndChar = GetVmAt(_layouts.Count - 1)?.PlainText?.Length ?? 0;
            _caretPara = _selEndPara; _caretChar = _selEndChar;
            SnapCaretToCorrectSlice();
            UpdatePreferredX();
            InvalidateFull();
        }

        public void ExecuteCopy() => _ = CopyAsync();
        public void ExecuteCut() => _ = CutAsync();
        public void ExecutePaste() => _ = PasteAsync();

        public void ExecuteUndo()
        {
            if (TextUndoStack is not null && TextUndoStack.CanUndo && DocVm is not null)
            {
                _logger.Debug("[UNDO] ExecuteUndo (text): '{D}'", TextUndoStack.UndoDescription);
                TextUndoStack.Undo(DocVm.Document);
                return;
            }
            if (UndoStack is null) { _logger.Warning("[UNDO] ExecuteUndo: UndoStack is null"); return; }
            if (!UndoStack.CanUndo) { _logger.Debug("[UNDO] ExecuteUndo: nothing to undo"); return; }
            _logger.Debug("[UNDO] ExecuteUndo: '{D}'", UndoStack.UndoDescription);
            _cellLayoutCache.Clear();
            _cellVmCache.Clear();
            UndoStack.Undo();
            RebuildLayouts();
            _caretPara = Clamp(_caretPara, 0, Math.Max(0, _layouts.Count - 1));
            _caretChar = Clamp(_caretChar, 0, GetVmAt(_caretPara)?.PlainText?.Length ?? 0);
            SyncSel(); ResetCaret(); InvalidateFull();
        }

        public void ExecuteRedo()
        {
            if (TextUndoStack is not null && TextUndoStack.CanRedo && DocVm is not null)
            {
                _logger.Debug("[UNDO] ExecuteRedo (text): '{D}'", TextUndoStack.RedoDescription);
                TextUndoStack.Redo(DocVm.Document);
                return;
            }
            if (UndoStack is null) { _logger.Warning("[UNDO] ExecuteRedo: UndoStack is null"); return; }
            if (!UndoStack.CanRedo) { _logger.Debug("[UNDO] ExecuteRedo: nothing to redo"); return; }
            _logger.Debug("[UNDO] ExecuteRedo: '{D}'", UndoStack.RedoDescription);
            _cellLayoutCache.Clear();
            _cellVmCache.Clear();
            UndoStack.Redo();
            RebuildLayouts();
            _caretPara = Clamp(_caretPara, 0, Math.Max(0, _layouts.Count - 1));
            _caretChar = Clamp(_caretChar, 0, GetVmAt(_caretPara)?.PlainText?.Length ?? 0);
            SyncSel(); ResetCaret(); InvalidateFull();
        }

        /// <summary>
        /// Геометрический поиск ячейки таблицы по точке в pt.
        /// Работает напрямую через TableEntry — не зависит от clip-rect параграфов.
        /// Используется при drag-выделении когда HitTest промахивается
        /// (курсор правее текста, на продолжении страницы и т.п.).
        /// </summary>
        private (TableBlock table, int row, int col, int entryIdx)? HitTestTableCellGeometric(float xPt, float yPt)
        {
            List<TableEntry> tables;
            lock (_renderLock) { tables = _tables; }

            for (int ti = 0; ti < tables.Count; ti++)
            {
                var te = tables[ti];
                float tableRight = te.XPt + te.Layout.TotalWidthPt;

                // Курсор левее или правее таблицы — зажимаем X к ближайшему краю.
                // Это гарантирует что при drag за пределами страницы таблицы всё равно
                // попадают в выделение по Y-диапазону строк.
                float effectiveX = xPt < te.XPt ? te.XPt + 0.001f
                                 : xPt > tableRight ? tableRight - 0.001f
                                 : xPt;

                int effectiveRowTo = te.RowTo < 0 ? te.Layout.Rows.Count : te.RowTo;
                float rowOffsetY = te.RowFrom > 0 && te.RowFrom < te.Layout.Rows.Count
                    ? te.Layout.Rows[te.RowFrom].Ypt : 0f;

                float maxPadTop = 0f;
                if (te.IsContinuation && te.FirstRowContentOffsetPt > 0f
                    && te.RowFrom < te.Layout.Rows.Count)
                {
                    foreach (var c in te.Layout.Rows[te.RowFrom].Cells)
                        maxPadTop = Math.Max(maxPadTop, c.PadTopPt + c.Borders.Top.WidthPt);
                }

                float accY = te.Ypt;
                for (int ri = te.RowFrom; ri < effectiveRowTo && ri < te.Layout.Rows.Count; ri++)
                {
                    var row = te.Layout.Rows[ri];
                    bool isFirstRow = ri == te.RowFrom;
                    float rowShift = isFirstRow ? te.FirstRowContentOffsetPt : 0f;
                    float extraShift = isFirstRow ? 0f : (te.FirstRowContentOffsetPt - maxPadTop);
                    float rowH = isFirstRow
                        ? row.HeightPt - rowShift + maxPadTop
                        : row.HeightPt;
                    if (ri == effectiveRowTo - 1 && te.LastRowVisibleHeightPt >= 0f)
                        rowH = te.LastRowVisibleHeightPt;

                    float rowY = te.Ypt + row.Ypt - rowOffsetY - rowShift - extraShift + rowShift;
                    float rowBottom = rowY + rowH;

                    if (yPt < rowY || yPt > rowBottom) { accY += rowH; continue; }

                    // Курсор по Y попал в эту строку — ищем столбец по X.
                    float accX = te.XPt;
                    for (int ci = 0; ci < te.Layout.ColumnWidthsPt.Count; ci++)
                    {
                        float colRight = accX + te.Layout.ColumnWidthsPt[ci];
                        if (effectiveX <= colRight)
                        {
                            var cell = te.Table.GetCell(row.Row, ci);
                            if (cell != null)
                                return (te.Table, cell.Row, cell.Column, ti);
                            return (te.Table, row.Row, ci, ti);
                        }
                        accX = colRight;
                    }
                    // Правее последней колонки — возвращаем последнюю ячейку строки.
                    int lastCol = te.Layout.ColumnWidthsPt.Count - 1;
                    var lastCell = te.Table.GetCell(row.Row, lastCol);
                    if (lastCell != null)
                        return (te.Table, lastCell.Row, lastCell.Column, ti);
                    return (te.Table, row.Row, lastCol, ti);
                }
            }
            return null;
        }

        private void DeleteSelectedTablesAndText()
        {
            if (DocVm is null) return;
            BeginEdit("Delete");

            var blocks = DocVm.Document.Sections[0].Blocks;

            // Удаляем все выделенные таблицы.
            foreach (var tbl in _tableSelections.Keys.ToList())
                blocks.Remove(tbl);

            // Удаляем выделенный текст параграфов (если есть параграфное выделение).
            if (HasSel())
                DeleteSelection();

            _tableSelections.Clear();
            _isCellRangeSelecting = false;
            _cellSelTable = null;

            CommitEdit();
            _cellVmCache.Clear();
            _cellLayoutCache.Clear();
            DocVm.RebuildParagraphViewModelsPublic();
            _caretPara = Clamp(_caretPara, 0, Math.Max(0, _layouts.Count - 1));
            _caretChar = 0;
            RebuildLayouts();
            SyncSel();
            ResetCaret();
            InvalidateFull();
        }

        private int FindFirstCellLayoutOf(TableBlock table)
        {
            for (int i = 0; i < _layouts.Count; i++)
                if (_layouts[i].Cell?.Table == table) return i;
            return -1;
        }

        private void PruneTableSelectionsByParaRange(int endPi)
        {
            int selMin = Math.Min(_selStartPara, endPi);
            int selMax = Math.Max(_selStartPara, endPi);

            var toRemove = new List<TableBlock>();
            foreach (var tbl in _tableSelections.Keys)
            {
                int tblFirst = -1;
                int tblLast = -1;
                for (int i = 0; i < _layouts.Count; i++)
                {
                    if (_layouts[i].Cell?.Table != tbl) continue;
                    if (tblFirst < 0) tblFirst = i;
                    tblLast = i;
                }
                if (tblFirst < 0) { toRemove.Add(tbl); continue; }

                if (tblLast < selMin || tblFirst > selMax)
                {
                    toRemove.Add(tbl);
                    continue;
                }

                // Таблица целиком вошла в диапазон — снапаем до полного охвата.
                if (tblFirst >= selMin && tblLast <= selMax)
                    _tableSelections[tbl] = (0, 0, tbl.RowCount - 1, tbl.ColumnCount - 1);
            }
            foreach (var t in toRemove)
                _tableSelections.Remove(t);
        }

    }
}