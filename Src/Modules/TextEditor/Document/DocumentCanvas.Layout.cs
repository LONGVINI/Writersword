using Serilog;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using Writersword.Core.Models.Rendering;
using Writersword.Infrastructure.Rendering;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.ViewModels;
using Writersword.Modules.TextEditor.ViewModels.Blocks;

namespace Writersword.Modules.TextEditor.Document
{
    public sealed partial class DocumentCanvas
    {
        // ── Добавление параграфов ячейки в _layouts ───────────────────────

        /// <param name="rowFrom">Первая строка слайса (включительно).</param>
        /// <param name="rowTo">Последняя строка слайса (не включительно). -1 = до конца.</param>
        /// <param name="firstRowOffset">Смещение контента первой строки (ByCell).</param>
        /// <param name="lastRowVisibleH">Видимая высота последней строки (ByCell). -1 = целая.</param>
        private void AddCellParasToLayouts(
            List<ParaLayout> newLayouts,
            TableBlock tableBlock,
            SKTableLayout tableLayout,
            int tableEntryIdx,
            float tableXPt,
            float tableYPt,
            int pageIdx,
            int rowFrom = 0,
            int rowTo = -1,
            float firstRowOffset = 0f,
            float lastRowVisibleH = -1f)
        {
            int effectiveRowTo = rowTo < 0 ? tableLayout.Rows.Count : rowTo;
            float rowOffsetY = rowFrom > 0 && rowFrom < tableLayout.Rows.Count
                ? tableLayout.Rows[rowFrom].Ypt : 0f;

            _logger.Debug(
                "[CELLS] tableYPt={TY:F1} rowFrom={RF} rowTo={RT} effectiveRowTo={ERT} rowOffsetY={ROY:F1} firstRowOffset={FRO:F1} lastRowVisH={LRV:F1}",
                tableYPt, rowFrom, rowTo, effectiveRowTo, rowOffsetY, firstRowOffset, lastRowVisibleH);

            foreach (var rowLayout in tableLayout.Rows)
            {
                if (rowLayout.Row < rowFrom || rowLayout.Row >= effectiveRowTo) continue;

                bool isLastRow = rowLayout.Row == effectiveRowTo - 1;
                bool isByCellSplit = isLastRow && lastRowVisibleH >= 0f;
                bool isContinuationFirstRow = rowLayout.Row == rowFrom && firstRowOffset > 0f;

                foreach (var cellLayout in rowLayout.Cells)
                {
                    if (cellLayout.Row != rowLayout.Row) continue; // пропускаем дубли объединённых ячеек

                    _logger.Debug(
                        "[CELLS] row={R} col={C} isCont={IC} isSplit={IS} cellYpt={CYT:F1} rowOffsetY={ROY:F1}",
                        rowLayout.Row, cellLayout.Column,
                        isContinuationFirstRow, isByCellSplit, cellLayout.Ypt, rowOffsetY);

                    float cellContentX = tableXPt + cellLayout.Xpt
                        + cellLayout.PadLeftPt + cellLayout.Borders.Left.WidthPt;

                    // Базовая Y ячейки относительно начала этого слайса на странице.
                    bool isRowFrom = rowLayout.Row == rowFrom;
                    float cellBaseY = tableYPt + cellLayout.Ypt - rowOffsetY;

                    // Для всех строк в продолжении (включая строки после rowFrom)
                    // контент смещён вверх на firstRowOffset.
                    float cellContentY = cellBaseY - firstRowOffset
                        + cellLayout.PadTopPt + cellLayout.Borders.Top.WidthPt;

                    float clipX = tableXPt + cellLayout.Xpt + cellLayout.Borders.Left.WidthPt;
                    float clipW = cellLayout.WidthPt
                        - cellLayout.Borders.Left.WidthPt - cellLayout.Borders.Right.WidthPt;

                    float clipY;
                    float clipH;

                    if (isContinuationFirstRow)
                    {
                        // Продолжение ByCell: видимая область начинается прямо от tableYPt.
                        float remaining = isByCellSplit
                            ? lastRowVisibleH
                            : rowLayout.HeightPt - firstRowOffset;
                        clipY = tableYPt + cellLayout.Borders.Top.WidthPt;
                        clipH = Math.Max(0f, remaining
                            - cellLayout.Borders.Top.WidthPt - cellLayout.Borders.Bottom.WidthPt);
                    }
                    else if (isByCellSplit)
                    {
                        // Последняя разорванная строка: ограничиваем снизу.
                        // Для строк после rowFrom учитываем firstRowOffset в clipY.
                        clipY = cellBaseY - firstRowOffset + cellLayout.Borders.Top.WidthPt;
                        clipH = Math.Max(0f, lastRowVisibleH
                            - cellLayout.Borders.Top.WidthPt - cellLayout.Borders.Bottom.WidthPt);
                    }
                    else
                    {
                        // Обычная строка или строки после rowFrom в продолжении.
                        // Для строк после rowFrom: clipY учитывает firstRowOffset.
                        clipY = cellBaseY - firstRowOffset + cellLayout.Borders.Top.WidthPt;
                        clipH = Math.Max(0f, cellLayout.HeightPt
                            - cellLayout.Borders.Top.WidthPt - cellLayout.Borders.Bottom.WidthPt);
                    }

                    // Получаем оригинальную ячейку модели
                    var modelCell = tableBlock.GetCell(cellLayout.Row, cellLayout.Column);
                    if (modelCell is null) continue;

                    for (int pi = 0; pi < cellLayout.Paragraphs.Count; pi++)
                    {
                        var cellPara = cellLayout.Paragraphs[pi];
                        var paraBlock = (pi < modelCell.Paragraphs.Count)
                            ? modelCell.Paragraphs[pi]
                            : null;
                        if (paraBlock is null) continue;

                        // Стабильный VM — переиспользуется между rebuild'ами
                        if (!_cellVmCache.TryGetValue(paraBlock, out var vm))
                        {
                            vm = new ParagraphViewModel(paraBlock);
                            _cellVmCache[paraBlock] = vm;
                        }

                        var info = new CellInfo(
                                tableBlock, modelCell, paraBlock, pi, tableEntryIdx,
                                cellContentX, cellContentY,
                                clipX, clipY, clipW, clipH);

                        // Вертикальное выравнивание — копируем из SKTableLayout
                        float contentAreaH = cellLayout.HeightPt
                            - cellLayout.PadTopPt - cellLayout.PadBottomPt
                            - cellLayout.Borders.Top.WidthPt - cellLayout.Borders.Bottom.WidthPt;
                        float contentOffsetY = cellLayout.VerticalAlignment switch
                        {
                            1 => Math.Max(0f, (contentAreaH - cellLayout.ContentHeightPt) / 2f),
                            2 => Math.Max(0f, contentAreaH - cellLayout.ContentHeightPt),
                            _ => 0f
                        };

                        float absParaY = cellContentY
                            + contentOffsetY
                            + cellPara.Ypt
                            + cellPara.Layout.SpaceBeforePt;

                        _logger.Debug(
                            "[CELLS]   pi={PI} cellContentY={CCY:F1} clipY={CY:F1} clipH={CH:F1} absParaY={APY:F1}",
                            pi, cellContentY, clipY, clipH, absParaY);

                        // Параграфы ячейки должны покрывать весь Y-диапазон без зазоров.
                        // Зазор возникает из-за SpaceBeforePt следующего параграфа: absParaY
                        // включает SpaceBefore текущего, но HeightPt его не включает, поэтому
                        // [absParaY + HeightPt .. nextAbsParaY] остаётся непокрытым.
                        // Для промежуточных параграфов растягиваем высоту до nextAbsParaY.
                        // Для последнего — до нижнего края клип-прямоугольника ячейки.
                        bool isLastInCell = (pi == cellLayout.Paragraphs.Count - 1);
                        float cellBottom = clipY + clipH;
                        float paraHeight;
                        if (isLastInCell)
                        {
                            paraHeight = Math.Max(cellPara.Layout.TotalHeightPt, cellBottom - absParaY);
                        }
                        else
                        {
                            var nextCellPara = cellLayout.Paragraphs[pi + 1];
                            float nextAbsParaY = cellContentY
                                + contentOffsetY
                                + nextCellPara.Ypt
                                + nextCellPara.Layout.SpaceBeforePt;
                            paraHeight = Math.Max(cellPara.Layout.TotalHeightPt, nextAbsParaY - absParaY);
                        }

                        newLayouts.Add(new ParaLayout(
                            vm,
                            cellPara.Layout,
                            absParaY,
                            paraHeight,
                            pageIdx,
                            0,
                            cellPara.Layout.Lines.Count,
                            AbsXPt: cellContentX,
                            Cell: info));
                    }
                }
            }
        }

        private void RebuildPageMode()
        {
            float pageWidthPt = GetPageWidthPt();
            float pageHeightPt = GetPageHeightPt();
            var (ml, mt, mr, mb) = GetPagePaddingPt();
            float textWidthPt = Math.Max(pageWidthPt - ml - mr, 1f);
            float canvasWPt = (float)(_canvasWidth * PxToPt);
            float pageXPt = Math.Max((canvasWPt - pageWidthPt) / 2f, 0f);
            float textXPt = pageXPt + ml;

            float pageYPt = PageGapPt;
            float pageBottomPt = pageYPt + pageHeightPt - mb;
            float contentYPt = pageYPt + mt;
            int pageIdx = 0;

            var newLayouts = new List<ParaLayout>();
            var newPages = new List<PageRect>();
            var newTables = new List<TableEntry>();

            newPages.Add(new PageRect(pageYPt, pageWidthPt, pageHeightPt, pageXPt, mt, ml, mb));

            float pageOffsetXPx = pageXPt * PtToPx * (float)Zoom
                - (float)(_parentScrollViewer?.Offset.X ?? 0);
            _lastPageOffsetXPx = pageOffsetXPx;
            PageOffsetXChanged?.Invoke(pageOffsetXPx);

            var blocks = DocVm!.Document.Sections[0].Blocks;

            // Отслеживаем позицию последней обработанной таблицы для позиционирования якоря после неё.
            float lastTableXPt = textXPt;
            float lastTableRightPt = textXPt;
            float lastTableBotPt = contentYPt;

            for (int bi = 0; bi < blocks.Count; bi++)
            {
                var block = blocks[bi];

                if (block is BreakBlock bb && bb.BreakType == BreakType.Page)
                {
                    pageYPt = pageYPt + pageHeightPt + PageGapPt;
                    pageBottomPt = pageYPt + pageHeightPt - mb;
                    contentYPt = pageYPt + mt;
                    pageIdx++;
                    newPages.Add(new PageRect(pageYPt, pageWidthPt, pageHeightPt, pageXPt, mt, ml, mb));
                    continue;
                }

                if (block is TableBlock tableBlock)
                {
                    var tableLayout = _renderer.BuildTableLayout(tableBlock, textWidthPt, _styleResolver!);
                    float tableXPt = textXPt + (float)tableBlock.LeftIndentPt;
                    bool byCell = tableBlock.SplitMode == TableSplitMode.ByCell;
                    float fullPageH = pageHeightPt - mt - mb;

                    // Таблица НИКОГДА не переносится целиком на другую страницу.
                    // Она всегда начинается там где поставлена.

                    float sliceFirstRowOffset = 0f;
                    float sliceStartOffset = 0f;
                    int rowFrom = 0;
                    float sliceStartY = contentYPt;
                    bool isFirstSlice = true;

                    _logger.Debug(
                        "[TBL] START rows={R} totalH={H:F1} contentY={CY:F1} pageBottom={PB:F1} pageIdx={PI} byCell={BC}",
                        tableLayout.Rows.Count, tableLayout.TotalHeightPt,
                        contentYPt, pageBottomPt, pageIdx, byCell);

                    for (int ri = 0; ri < tableLayout.Rows.Count; ri++)
                    {
                        var row = tableLayout.Rows[ri];
                        float effectiveH = row.HeightPt - sliceFirstRowOffset;

                        float available = pageBottomPt - contentYPt;
                        bool atPageTop = contentYPt <= pageYPt + mt + 0.5f;

                        _logger.Debug(
                            "[TBL] row={RI} rowH={RH:F1} offset={OF:F1} effectiveH={EH:F1} " +
                            "available={AV:F1} atPageTop={AT} contentY={CY:F1} pageBottom={PB:F1}",
                            ri, row.HeightPt, sliceFirstRowOffset, effectiveH,
                            available, atPageTop, contentYPt, pageBottomPt);

                        if (effectiveH > available && (!atPageTop || sliceFirstRowOffset > 0f || effectiveH > fullPageH))
                        {
                            // Строка 0 всегда остаётся на месте — разрывается постранично (ByCell-стиль).
                            // Строки 1+ в ByRow: переносятся целиком на следующую страницу.
                            //   Исключение: если строка выше целой страницы — разрывается постранично.
                            // Строки 1+ в ByCell: разрываются постранично.
                            bool forceByCell = ri == 0 || byCell || effectiveH > fullPageH;

                            if (forceByCell && available > 5f)
                            {
                                // Разрыв по границе абзаца — находим последний абзац,
                                // который целиком помещается в available.
                                // Берём первую ячейку строки как референс высот параграфов.
                                float visibleH = available;
                                // Используем ячейку с наибольшим содержимым как референс для snap.
                                // Cells[0] может быть пустой — тогда snap вернёт высоту пустого
                                // параграфа (~23.5pt) и отрежет в неправильном месте.
                                SKTableCellLayout? refCell = null;
                                if (row.Cells.Count > 0)
                                {
                                    refCell = row.Cells[0];
                                    for (int ci = 1; ci < row.Cells.Count; ci++)
                                    {
                                        if (row.Cells[ci].ContentHeightPt > refCell.ContentHeightPt)
                                            refCell = row.Cells[ci];
                                    }
                                }
                                if (refCell != null && refCell.Paragraphs.Count > 0)
                                {
                                    // para.Ypt в координатах контента ячейки (после PadTop+BorderTop).
                                    // available и sliceFirstRowOffset — в координатах строки (от верха строки).
                                    // Чтобы сравнивать в одних координатах добавляем cellPadTop.
                                    float cellPadTop = refCell.PadTopPt + refCell.Borders.Top.WidthPt;
                                    float paraSnapH = 0f;
                                    foreach (var para in refCell.Paragraphs)
                                    {
                                        float paraBottom = cellPadTop
                                            + para.Ypt + para.Layout.BlockHeightPt
                                            - sliceFirstRowOffset;
                                        if (paraBottom <= available)
                                            paraSnapH = paraBottom;
                                        else
                                            break;
                                    }
                                    if (paraSnapH > 5f)
                                        visibleH = paraSnapH;
                                }

                                float nextOffset = sliceFirstRowOffset + visibleH;

                                _logger.Debug(
                                    "[TBL] BYCELL-SPLIT ri={RI} sliceStartY={SSY:F1} rowFrom={RF} rowTo={RT} " +
                                    "visibleH={VH:F1} nextOffset={NO:F1} pageIdx={PI}",
                                    ri, sliceStartY, rowFrom, ri + 1, visibleH, nextOffset, pageIdx);

                                int teIdx = newTables.Count;
                                newTables.Add(new TableEntry(tableBlock, tableLayout,
                                    sliceStartY, tableXPt, pageIdx,
                                    RowFrom: rowFrom, RowTo: ri + 1,
                                    LastRowVisibleHeightPt: visibleH,
                                    FirstRowContentOffsetPt: sliceStartOffset,
                                    IsContinuation: !isFirstSlice));
                                AddCellParasToLayouts(newLayouts, tableBlock, tableLayout,
                                    teIdx, tableXPt, sliceStartY, pageIdx,
                                    rowFrom, ri + 1, sliceStartOffset, visibleH);

                                pageYPt = pageYPt + pageHeightPt + PageGapPt;
                                pageBottomPt = pageYPt + pageHeightPt - mb;
                                pageIdx++;
                                newPages.Add(new PageRect(pageYPt, pageWidthPt, pageHeightPt, pageXPt, mt, ml, mb));
                                contentYPt = pageYPt + mt;
                                sliceStartY = contentYPt;
                                sliceStartOffset = nextOffset;

                                _logger.Debug(
                                    "[TBL] BYCELL-NEWPAGE pageIdx={PI} newContentY={CY:F1} sliceStartY={SSY:F1} nextOffset={NO:F1}",
                                    pageIdx, contentYPt, sliceStartY, nextOffset);

                                rowFrom = ri;
                                sliceFirstRowOffset = nextOffset;
                                isFirstSlice = false;
                                ri--;  // повторяем строку ri на новой странице
                                continue;
                            }
                            else if (!forceByCell)
                            {
                                // ByRow (строки 1+): переносим строку ri целиком на следующую страницу.
                                _logger.Debug(
                                    "[TBL] BYROW-SPLIT ri={RI} sliceStartY={SSY:F1} rowFrom={RF} pageIdx={PI}",
                                    ri, sliceStartY, rowFrom, pageIdx);
                                if (ri > rowFrom)
                                {
                                    int teIdx = newTables.Count;
                                    newTables.Add(new TableEntry(tableBlock, tableLayout,
                                        sliceStartY, tableXPt, pageIdx,
                                        RowFrom: rowFrom, RowTo: ri,
                                        LastRowVisibleHeightPt: -1f,
                                        FirstRowContentOffsetPt: sliceStartOffset,
                                        IsContinuation: !isFirstSlice));
                                    AddCellParasToLayouts(newLayouts, tableBlock, tableLayout,
                                        teIdx, tableXPt, sliceStartY, pageIdx,
                                        rowFrom, ri, sliceStartOffset, -1f);
                                }

                                pageYPt = pageYPt + pageHeightPt + PageGapPt;
                                pageBottomPt = pageYPt + pageHeightPt - mb;
                                pageIdx++;
                                newPages.Add(new PageRect(pageYPt, pageWidthPt, pageHeightPt, pageXPt, mt, ml, mb));
                                contentYPt = pageYPt + mt;
                                sliceStartY = contentYPt;
                                sliceStartOffset = 0f;

                                rowFrom = ri;
                                sliceFirstRowOffset = 0f;
                                isFirstSlice = false;
                            }
                            // else: available <= 5f — нет места для разрыва, строка просто рендерится как есть
                        }
                        else
                        {
                            sliceFirstRowOffset = 0f;
                        }

                        contentYPt += effectiveH;
                    }

                    // Финальный слайс
                    _logger.Debug(
                        "[TBL] FINAL sliceStartY={SSY:F1} rowFrom={RF} contentY={CY:F1} pageIdx={PI}",
                        sliceStartY, rowFrom, contentYPt, pageIdx);
                    if (rowFrom < tableLayout.Rows.Count)
                    {
                        int teIdx = newTables.Count;
                        newTables.Add(new TableEntry(tableBlock, tableLayout,
                            sliceStartY, tableXPt, pageIdx,
                            RowFrom: rowFrom, RowTo: -1,
                            LastRowVisibleHeightPt: -1f,
                            FirstRowContentOffsetPt: sliceStartOffset,
                            IsContinuation: !isFirstSlice));
                        AddCellParasToLayouts(newLayouts, tableBlock, tableLayout,
                            teIdx, tableXPt, sliceStartY, pageIdx,
                            rowFrom, -1, sliceStartOffset, -1f);
                    }

                    contentYPt += FallbackLinePt;

                    // Запоминаем позицию этой таблицы для якоря после неё.
                    lastTableXPt = tableXPt;
                    lastTableRightPt = tableXPt + tableLayout.TotalWidthPt;
                    lastTableBotPt = contentYPt - FallbackLinePt; // истинный нижний край таблицы
                    continue;
                }

                if (block is not ParagraphBlock paraBlock) continue;

                ParagraphViewModel? pvm = null;
                foreach (var p in DocVm.Paragraphs)
                    if (p.Model == paraBlock) { pvm = p; break; }
                if (pvm is null) continue;

                var layout = GetOrBuildLayout(pvm, textWidthPt);

                // Якорь перед таблицей: пустой параграф, следующий блок — таблица.
                bool isBeforeTableAnchor = string.IsNullOrEmpty(pvm.PlainText)
                    && bi + 1 < blocks.Count && blocks[bi + 1] is TableBlock;
                if (isBeforeTableAnchor)
                {
                    float anchorXPt = textXPt + (float)((TableBlock)blocks[bi + 1]).LeftIndentPt;
                    // Сдвигаем каретку чуть левее таблицы чтобы она не перекрывалась рамкой.
                    _logger.Debug("[ANCHOR] BEFORE table bi={BI} Ypt={Y:F1} AbsXPt={X:F1}", bi, contentYPt, anchorXPt - AnchorMarginPt);
                    newLayouts.Add(new ParaLayout(
                        pvm, layout, contentYPt, FallbackLinePt,
                        pageIdx, 0, 0,
                        AbsXPt: anchorXPt - AnchorMarginPt));
                    continue;
                }

                // Якорь после таблицы: пустой параграф, предыдущий блок — таблица.
                bool isAfterTableAnchor = string.IsNullOrEmpty(pvm.PlainText)
                    && bi > 0 && blocks[bi - 1] is TableBlock;
                if (isAfterTableAnchor)
                {
                    float anchorY = lastTableBotPt - FallbackLinePt;
                    // Сдвигаем каретку чуть правее таблицы чтобы она не перекрывалась рамкой.
                    _logger.Debug("[ANCHOR] AFTER table bi={BI} Ypt={Y:F1} AbsXPt={X:F1} lastTableBotPt={B:F1}", bi, anchorY, lastTableRightPt + AnchorMarginPt, lastTableBotPt);
                    newLayouts.Add(new ParaLayout(
                        pvm, layout, anchorY, FallbackLinePt,
                        pageIdx, 0, 0,
                        AbsXPt: lastTableRightPt + AnchorMarginPt));
                    continue;
                }

                float absXPt = textXPt;

                if (layout.Lines.Count == 0) continue;

                contentYPt += layout.SpaceBeforePt;
                int lineFrom = 0;
                float lineGroupYPt = contentYPt;

                for (int li = 0; li < layout.Lines.Count; li++)
                {
                    var line = layout.Lines[li];
                    bool isLast = li == layout.Lines.Count - 1;

                    if (contentYPt + line.Height > pageBottomPt
                        && contentYPt > pageYPt + mt)
                    {
                        if (li > lineFrom)
                        {
                            newLayouts.Add(new ParaLayout(
                                pvm, layout, lineGroupYPt,
                                contentYPt - lineGroupYPt,
                                pageIdx, lineFrom, li,
                                AbsXPt: absXPt));
                        }

                        pageYPt = pageYPt + pageHeightPt + PageGapPt;
                        pageBottomPt = pageYPt + pageHeightPt - mb;
                        contentYPt = pageYPt + mt;
                        pageIdx++;
                        newPages.Add(new PageRect(pageYPt, pageWidthPt, pageHeightPt, pageXPt, mt, ml, mb));

                        lineFrom = li;
                        lineGroupYPt = contentYPt;
                    }

                    contentYPt += line.Height;
                    if (isLast) contentYPt += layout.SpaceAfterPt;
                }

                newLayouts.Add(new ParaLayout(
                    pvm, layout, lineGroupYPt,
                    contentYPt - lineGroupYPt,
                    pageIdx, lineFrom, layout.Lines.Count,
                    AbsXPt: absXPt));
            }

            float newCanvasH = pageYPt + pageHeightPt + PageGapPt;

            lock (_renderLock)
            {
                _layouts = newLayouts;
                _pages = newPages;
                _tables = newTables;
                _canvasHeightPt = newCanvasH;
                _canvasHeight = newCanvasH * PtToPx;
            }
        }

        private void RebuildFlowMode(float maxWidthPt, float padHPt, float padWPt)
        {
            float textWidthPt = Math.Max(maxWidthPt - padWPt * 2f, 1f);
            float yPt = padHPt;

            var newLayouts = new List<ParaLayout>();
            var newTables = new List<TableEntry>();

            float lastTableRightPt = padWPt;
            float lastTableBotPt = padHPt;

            var blocks = DocVm!.Document.Sections[0].Blocks;
            for (int bi = 0; bi < blocks.Count; bi++)
            {
                var block = blocks[bi];

                if (block is TableBlock tableBlock)
                {
                    var tableLayout = _renderer.BuildTableLayout(tableBlock, textWidthPt, _styleResolver!);
                    float tableXPt = padWPt + (float)tableBlock.LeftIndentPt;
                    int teIdx = newTables.Count;
                    newTables.Add(new TableEntry(tableBlock, tableLayout, yPt, tableXPt, 0));
                    AddCellParasToLayouts(newLayouts, tableBlock, tableLayout,
                        teIdx, tableXPt, yPt, 0);

                    lastTableRightPt = tableXPt + tableLayout.TotalWidthPt;
                    lastTableBotPt = yPt + tableLayout.TotalHeightPt;
                    yPt += tableLayout.TotalHeightPt + FallbackLinePt;
                    continue;
                }

                if (block is not ParagraphBlock paraBlock) continue;

                ParagraphViewModel? pvm = null;
                foreach (var p in DocVm.Paragraphs)
                    if (p.Model == paraBlock) { pvm = p; break; }
                if (pvm is null) continue;

                var layout = GetOrBuildLayout(pvm, textWidthPt);

                // Якорь перед таблицей
                if (string.IsNullOrEmpty(pvm.PlainText) && bi + 1 < blocks.Count && blocks[bi + 1] is TableBlock nextFlowTb)
                {
                    float anchorX = padWPt + (float)nextFlowTb.LeftIndentPt - AnchorMarginPt;
                    newLayouts.Add(new ParaLayout(pvm, layout, yPt, FallbackLinePt,
                        0, 0, 0, AbsXPt: anchorX));
                    continue;
                }

                // Якорь после таблицы
                if (string.IsNullOrEmpty(pvm.PlainText) && bi > 0 && blocks[bi - 1] is TableBlock)
                {
                    newLayouts.Add(new ParaLayout(pvm, layout,
                        lastTableBotPt - FallbackLinePt, FallbackLinePt,
                        0, 0, 0, AbsXPt: lastTableRightPt + AnchorMarginPt));
                    continue;
                }

                if (layout.Lines.Count == 0) continue;

                float hPt = Math.Max(layout.TotalHeightPt, FallbackLinePt);
                newLayouts.Add(new ParaLayout(
                    pvm, layout,
                    yPt + layout.SpaceBeforePt, hPt,
                    0, 0, layout.Lines.Count,
                    AbsXPt: padWPt));
                yPt += layout.BlockHeightPt;
            }

            float newCanvasH = yPt + padHPt;

            lock (_renderLock)
            {
                _layouts = newLayouts;
                _pages = new List<PageRect>();
                _tables = newTables;
                _canvasHeightPt = newCanvasH;
                _canvasHeight = newCanvasH * PtToPx;
            }
        }
    }
}