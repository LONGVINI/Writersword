using Avalonia;
using Avalonia.Input;
using Avalonia.Threading;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Writersword.Core.Models.Rendering;
using Writersword.Infrastructure.Rendering;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
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

            _caretPara = pi;
            _caretChar = ci;
            _selStartPara = pi; _selStartChar = ci;
            _selEndPara = pi; _selEndChar = ci;
            _isSelecting = true;

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
            ResetCaret(); InvalidateFull();
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

            // Выделение только внутри одной ячейки (или только вне ячеек)
            bool startInCell = IsInCell(_selStartPara);
            bool nowInCell = pi >= 0 && pi < _layouts.Count && _layouts[pi].Cell != null;
            if (startInCell != nowInCell) { e.Handled = true; return; }
            if (startInCell && nowInCell)
            {
                var startCell = _layouts[_selStartPara].Cell;
                var endCell = _layouts[pi].Cell;
                if (startCell?.Cell != endCell?.Cell) { e.Handled = true; return; }
            }

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

            BeginEdit("Type text");
            DeleteSelection();

            var pvm = GetVmAt(_caretPara);
            if (pvm is null) return;

            string t = pvm.PlainText ?? "";
            int pos = Clamp(_caretChar, 0, t.Length);
            pvm.PlainText = t[..pos] + text + t[pos..];
            _caretChar = pos + text.Length;

            CommitEdit();
            UpdatePreferredX();
            SyncSel(); ResetCaret();
        }

        private void CellInsertText(string text)
        {
            var cell = GetCurrentCell();
            if (cell is null) return;

            // Сначала удаляем выделение если оно есть
            if (HasSel()) { CellDeleteSelection(); RebuildAfterCellEdit(); }

            // Перечитываем cell после rebuild
            cell = GetCurrentCell();
            if (cell is null) return;

            string t = cell.ParaBlock.GetPlainText();
            int pos = Clamp(_caretChar, 0, t.Length);
            SetCellParaText(cell.Cell, cell.CellParaIndex, t[..pos] + text + t[pos..]);
            _caretChar = pos + text.Length;

            RebuildAfterCellEdit();
        }

        public void ExecuteDeleteBackSmart()
        {
            _caretLineHint = -1;

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

            if (HasSel())
            {
                CellDeleteSelection();
                RebuildAfterCellEdit();
                return;
            }

            string t = cell.ParaBlock.GetPlainText();

            if (_caretChar > 0)
            {
                int p = Clamp(_caretChar, 1, t.Length);
                SetCellParaText(cell.Cell, cell.CellParaIndex, t[..(p - 1)] + t[p..]);
                _caretChar = p - 1;
            }
            else if (cell.CellParaIndex > 0)
            {
                // Объединяем с предыдущим параграфом той же ячейки
                var prev = cell.Cell.Paragraphs[cell.CellParaIndex - 1];
                string pt = prev.GetPlainText();
                SetCellParaText(cell.Cell, cell.CellParaIndex - 1, pt + t);
                cell.Cell.Paragraphs.RemoveAt(cell.CellParaIndex);
                _caretChar = pt.Length;
                // Обновляем cell paraIndex → после rebuild snap найдёт нужный слайс
            }
            // else: начало первого параграфа ячейки — блокируем (нельзя выйти)

            RebuildAfterCellEdit();
        }

        private void CellDeleteForward()
        {
            var cell = GetCurrentCell();
            if (cell is null) return;

            if (HasSel())
            {
                CellDeleteSelection();
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
                // Объединяем со следующим параграфом ячейки
                var next = cell.Cell.Paragraphs[cell.CellParaIndex + 1];
                string nt = next.GetPlainText();
                SetCellParaText(cell.Cell, cell.CellParaIndex, t + nt);
                cell.Cell.Paragraphs.RemoveAt(cell.CellParaIndex + 1);
            }
            // else: конец последнего параграфа ячейки — блокируем

            RebuildAfterCellEdit();
        }

        private void CellNewParagraph()
        {
            // Сначала удаляем выделение если оно есть
            if (HasSel())
            {
                CellDeleteSelection();
                RebuildAfterCellEdit();
            }

            var cell = GetCurrentCell();
            if (cell is null) return;

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

            // Снапимся явно на новый параграф, а не на текущий.
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
            double oldCanvasH = _canvasHeight;
            RebuildLayouts();

            // Если высота канваса изменилась (таблица выросла или уменьшилась) —
            // нужно уведомить ScrollViewer о новом размере.
            if (Math.Abs(_canvasHeight - oldCanvasH) > 0.5)
                InvalidateMeasure();

            // Snap: найти слайс с targetBlock.
            // Для ByCell-разреза одна и та же VM присутствует в двух слайсах (страница 1
            // и страница 2). Берём слайс, в clip-прямоугольник которого попадает каретка,
            // иначе ResetCaret → ScrollToCaret прыгает на страницу 1 вместо страницы 2.
            if (targetBlock != null && _cellVmCache.TryGetValue(targetBlock, out var targetVm))
            {
                int bestSlice = -1;
                for (int i = 0; i < _layouts.Count; i++)
                {
                    if (_layouts[i].Vm != targetVm) continue;
                    if (bestSlice < 0) bestSlice = i; // запасной: первый найденный слайс

                    var pl = _layouts[i];
                    if (pl.Cell != null)
                    {
                        // Проверяем, попадает ли каретка в видимую область данного слайса.
                        int pos = Clamp(_caretChar, 0, pl.Vm.PlainText?.Length ?? 0);
                        var caretRect = pl.Layout.HitTestPosition(pos);
                        float yBase = pl.LineFrom < pl.Layout.Lines.Count
                            ? pl.Layout.Lines[pl.LineFrom].Y : 0f;
                        float caretAbsY = pl.Ypt + (caretRect.Y - yBase);
                        if (caretAbsY >= pl.Cell.ClipY - 0.5f
                            && caretAbsY < pl.Cell.ClipY + pl.Cell.ClipH + 0.5f)
                        {
                            bestSlice = i;
                            break;
                        }
                    }
                    else
                    {
                        break; // не ячейка — первый слайс однозначно верный
                    }
                }
                if (bestSlice >= 0) _caretPara = bestSlice;
            }

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
                pvm.PlainText = text[..(p - 1)] + text[p..];
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
                            prevVm.PlainText = prevText[..^1];
                            _caretPara--;
                            _caretChar = prevVm.PlainText.Length;
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
                pvm.PlainText = text[..p] + text[(p + 1)..];
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
                { pvm.PlainText += next.PlainText; DocVm?.DeleteParagraph(next); }
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
            pvm.PlainText = text[..cp];
            var newVm = DocVm?.AddParagraphAfter(pvm);
            if (newVm is not null)
            {
                newVm.PlainText = text[cp..];
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

            SnapCaretToCorrectSlice();
            if (!extend) SyncSel(); else ExtendSel();
            UpdatePreferredX();
            ResetCaret(); InvalidateFull();
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
            UndoStack?.Undo();
            ClampCaret(); SyncSel(); ResetCaret(); InvalidateFull();
        }

        public void ExecuteRedo()
        {
            UndoStack?.Redo();
            ClampCaret(); SyncSel(); ResetCaret(); InvalidateFull();
        }

    }
}