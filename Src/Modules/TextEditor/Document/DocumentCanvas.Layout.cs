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

            // Верхний паддинг строки rowFrom — синхронно с RenderTableStructureOnly.
            // Используется для корректировки позиций строк ПОСЛЕ rowFrom: они сдвигаются вверх
            // не на firstRowOffset, а на (firstRowOffset - maxCellPadTop), что соответствует
            // увеличенной effectiveRowH строки rowFrom (она выше на maxCellPadTop).
            float maxCellPadTop = 0f;
            if (firstRowOffset > 0f && rowFrom < tableLayout.Rows.Count)
            {
                foreach (var cl in tableLayout.Rows[rowFrom].Cells)
                    maxCellPadTop = Math.Max(maxCellPadTop, cl.PadTopPt + cl.Borders.Top.WidthPt);
            }

            _logger.Debug(
                "[CELLS] tableYPt={TY:F1} rowFrom={RF} rowTo={RT} effectiveRowTo={ERT} rowOffsetY={ROY:F1} firstRowOffset={FRO:F1} lastRowVisH={LRV:F1}",
                tableYPt, rowFrom, rowTo, effectiveRowTo, rowOffsetY, firstRowOffset, lastRowVisibleH);

            foreach (var rowLayout in tableLayout.Rows)
            {
                if (rowLayout.Row < rowFrom || rowLayout.Row >= effectiveRowTo) continue;

                bool isLastRow = rowLayout.Row == effectiveRowTo - 1;
                bool isByCellSplit = isLastRow && lastRowVisibleH >= 0f;
                bool isContinuationFirstRow = rowLayout.Row == rowFrom && firstRowOffset > 0f;

                // effectiveOffset — смещение контента уже показанного на предыдущих страницах.
                // Актуально ТОЛЬКО для первой строки слайса (rowFrom): она является продолжением
                // разрыва ByCell. Все строки после rowFrom начинаются с нуля — применение
                // firstRowOffset к ним ломает clipH и P, делая их контент невидимым.
                float effectiveOffset = isContinuationFirstRow ? firstRowOffset : 0f;

                foreach (var cellLayout in rowLayout.Cells)
                {
                    if (cellLayout.Row != rowLayout.Row) continue;

                    _logger.Debug(
                        "[CELLS] row={R} col={C} isCont={IC} isSplit={IS} cellYpt={CYT:F1} rowOffsetY={ROY:F1}",
                        rowLayout.Row, cellLayout.Column,
                        isContinuationFirstRow, isByCellSplit, cellLayout.Ypt, rowOffsetY);

                    float cellBT = cellLayout.Borders.Top.WidthPt;
                    float cellBB = cellLayout.Borders.Bottom.WidthPt;
                    float cellPadTopTotal = cellBT + cellLayout.PadTopPt;
                    float cellPadBotTotal = cellBB + cellLayout.PadBottomPt;

                    float cellContentX = tableXPt + cellLayout.Xpt
                        + cellLayout.PadLeftPt + cellLayout.Borders.Left.WidthPt;

                    // cellBaseY — Y верха этой строки на текущей странице.
                    // Для строк после rowFrom: строка rowFrom имеет effectiveRowH увеличенный
                    // на maxCellPadTop (см. RenderTableStructureOnly), поэтому сдвигаем на
                    // (firstRowOffset - maxCellPadTop) вместо firstRowOffset.
                    float extraOffset = rowLayout.Row != rowFrom && firstRowOffset > 0f
                        ? firstRowOffset - maxCellPadTop : 0f;
                    float cellBaseY = tableYPt + cellLayout.Ypt - rowOffsetY - extraOffset;

                    float clipX = tableXPt + cellLayout.Xpt + cellLayout.Borders.Left.WidthPt;
                    float clipW = cellLayout.WidthPt
                        - cellLayout.Borders.Left.WidthPt - cellLayout.Borders.Right.WidthPt;

                    // pageVisibleRow — высота строки, видимая на этой странице (в координатах строки).
                    float pageVisibleRow = isByCellSplit
                        ? lastRowVisibleH
                        : (rowLayout.HeightPt - effectiveOffset);

                    // clipY — начало видимой области контента (за верхней рамкой).
                    // clipH покрывает текст: pageVisibleRow за вычетом рамок.
                    // Паддинги (top/bottom) не включаются в clip — там нет текста,
                    // только пустое пространство которое создаётся смещением absParaY и границами рамки.
                    float clipY = cellBaseY + cellBT;
                    float clipH = Math.Max(0f, pageVisibleRow - cellBT - cellBB);

                    // P — нижняя граница видимости в координатах контента ячейки (0 = верх контента).
                    // Строки ЗАКАНЧИВАЮЩИЕСЯ до P были показаны на предыдущих страницах.
                    // Отрицательное P на первой странице означает "все строки видны снизу".
                    float P = effectiveOffset - cellPadTopTotal;

                    // contentCutY — верхняя граница видимости (в координатах контента).
                    // Строки НАЧИНАЮЩИЕСЯ после contentCutY уйдут на следующую страницу.
                    // Вычитаем cellPadBotTotal: PadBottom — пустое пространство, строк там нет.
                    float contentCutY = isByCellSplit
                        ? P + pageVisibleRow - cellPadBotTotal
                        : float.MaxValue;

                    var modelCell = tableBlock.GetCell(cellLayout.Row, cellLayout.Column);
                    if (modelCell is null) continue;

                    // Вертикальное выравнивание.
                    float contentAreaH = cellLayout.HeightPt
                        - cellLayout.PadTopPt - cellLayout.PadBottomPt
                        - cellBT - cellBB;
                    float contentOffsetY = cellLayout.VerticalAlignment switch
                    {
                        1 => Math.Max(0f, (contentAreaH - cellLayout.ContentHeightPt) / 2f),
                        2 => Math.Max(0f, contentAreaH - cellLayout.ContentHeightPt),
                        _ => 0f
                    };

                    // Базовый Y контента на странице:
                    // верх строки → cellBaseY, контент-область → + cellPadTopTotal,
                    // предыдущие страницы → - effectiveOffset (только для строки rowFrom).
                    float cellContentY = cellBaseY - effectiveOffset + cellPadTopTotal;

                    float cellBottom = clipY + clipH;

                    // Ищем последний параграф, хоть одна строка которого видна на этой странице.
                    int lastVisiblePi = -1;
                    for (int pi = cellLayout.Paragraphs.Count - 1; pi >= 0; pi--)
                    {
                        var cp = cellLayout.Paragraphs[pi];
                        float pcY = contentOffsetY + cp.Ypt;
                        if (cp.Layout.Lines.Count == 0)
                        {
                            if (pcY > P) { lastVisiblePi = pi; break; }
                            continue;
                        }
                        var ll = cp.Layout.Lines[^1];
                        if (pcY + ll.Y + ll.Height > P) { lastVisiblePi = pi; break; }
                    }

                    for (int pi = 0; pi < cellLayout.Paragraphs.Count; pi++)
                    {
                        var cellPara = cellLayout.Paragraphs[pi];
                        var paraBlock = pi < modelCell.Paragraphs.Count
                            ? modelCell.Paragraphs[pi] : null;
                        if (paraBlock is null) continue;

                        if (!_cellVmCache.TryGetValue(paraBlock, out var vm))
                        {
                            vm = new ParagraphViewModel(paraBlock);
                            _cellVmCache[paraBlock] = vm;
                        }

                        float paraContentY = contentOffsetY + cellPara.Ypt;

                        // Пропускаем параграфы целиком до или после видимой области.
                        if (cellPara.Layout.Lines.Count > 0)
                        {
                            var fl = cellPara.Layout.Lines[0];
                            var ll = cellPara.Layout.Lines[^1];
                            if (paraContentY + ll.Y + ll.Height <= P) continue;
                            if (contentCutY < float.MaxValue && paraContentY + fl.Y >= contentCutY) continue;
                        }

                        // lineFrom: первая строка, заканчивающаяся после P (видимая на этой странице).
                        int lineFrom = 0;
                        if (P > 0f)
                        {
                            for (int li = 0; li < cellPara.Layout.Lines.Count; li++)
                            {
                                var ln = cellPara.Layout.Lines[li];
                                if (paraContentY + ln.Y + ln.Height > P) { lineFrom = li; break; }
                                lineFrom = li + 1;
                            }
                        }

                        // lineTo: последняя строка, начинающаяся до contentCutY.
                        int lineTo = cellPara.Layout.Lines.Count;
                        if (contentCutY < float.MaxValue)
                        {
                            lineTo = lineFrom;
                            for (int li = lineFrom; li < cellPara.Layout.Lines.Count; li++)
                            {
                                var ln = cellPara.Layout.Lines[li];
                                if (paraContentY + ln.Y + ln.Height <= contentCutY)
                                    lineTo = li + 1;
                                else
                                    break;
                            }
                        }

                        if (lineFrom >= lineTo && cellPara.Layout.Lines.Count > 0) continue;

                        var info = new CellInfo(
                            tableBlock, modelCell, paraBlock, pi, tableEntryIdx,
                            cellContentX, cellContentY + contentOffsetY,
                            clipX, clipY, clipW, clipH);

                        // SpaceBefore подавляем только если параграф срезан сверху (lineFrom > 0):
                        // SpaceBefore этого параграфа был показан на предыдущей странице.
                        float spaceBefore = lineFrom > 0 ? 0f : cellPara.Layout.SpaceBeforePt;

                        float absParaY = cellContentY + contentOffsetY + cellPara.Ypt + spaceBefore;

                        // На странице продолжения текст начинается в tableY + cellPadTopTotal
                        // (за верхней рамкой + верхний паддинг). effectiveRowH строки rowFrom
                        // увеличен на cellPadTopTotal (в RenderTableStructureOnly), поэтому
                        // нижний паддинг cellPadBotTotal тоже полностью виден.
                        if (effectiveOffset > 0f)
                        {
                            float consumedContent = effectiveOffset - cellPadTopTotal;
                            absParaY += effectiveOffset - Math.Min(cellPara.Ypt, consumedContent);
                        }

                        _logger.Debug(
                            "[CELLS]   pi={PI} P={P:F1} cCutY={CCY:F1} lineFrom={LF} lineTo={LT} absParaY={APY:F1}",
                            pi, P, contentCutY < float.MaxValue ? contentCutY : -1f, lineFrom, lineTo, absParaY);

                        float paraHeight;
                        if (pi == lastVisiblePi)
                        {
                            paraHeight = Math.Max(cellPara.Layout.TotalHeightPt, cellBottom - absParaY);
                        }
                        else if (pi + 1 < cellLayout.Paragraphs.Count)
                        {
                            var next = cellLayout.Paragraphs[pi + 1];
                            float nextAbsY = cellContentY + contentOffsetY + next.Ypt + next.Layout.SpaceBeforePt;
                            paraHeight = Math.Max(cellPara.Layout.TotalHeightPt, nextAbsY - absParaY);
                        }
                        else
                        {
                            paraHeight = cellPara.Layout.TotalHeightPt;
                        }

                        newLayouts.Add(new ParaLayout(
                            vm,
                            cellPara.Layout,
                            absParaY,
                            paraHeight,
                            pageIdx,
                            lineFrom,
                            lineTo > 0 ? lineTo : cellPara.Layout.Lines.Count,
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

                        // Минимальный зазор снизу страницы: строка не прижимается вплотную к краю.
                        // На верхней позиции страницы зазор не требуется — строка уже некуда двигать.
                        const float MinRowEndGapPt = 8f;
                        float fittingAvailable = atPageTop ? available : available - MinRowEndGapPt;

                        _logger.Debug(
                            "[TBL] row={RI} rowH={RH:F1} offset={OF:F1} effectiveH={EH:F1} " +
                            "available={AV:F1} fittingAvailable={FA:F1} atPageTop={AT} contentY={CY:F1} pageBottom={PB:F1}",
                            ri, row.HeightPt, sliceFirstRowOffset, effectiveH,
                            available, fittingAvailable, atPageTop, contentYPt, pageBottomPt);

                        if (effectiveH > fittingAvailable && (!atPageTop || sliceFirstRowOffset > 0f || effectiveH > fullPageH))
                        {
                            // ByRow: строка целиком переносится на следующую страницу.
                            //   Исключение: если строка выше целой страницы — разрывается постранично.
                            // ByCell: все строки разрываются постранично.
                            // ri > 0: строки 1+ никогда не уходят на следующую страницу целиком —
                            // только режутся по ячейкам. Уйти может только строка 0 (в режиме ByRow).
                            // sliceFirstRowOffset > 0: продолжение ByCell, нельзя сбрасывать offset через ByRow.
                            bool forceByCell = byCell || effectiveH > fullPageH || sliceFirstRowOffset > 0f || ri > 0;

                            // Снап по строкам текста: ищем последнюю строку, целиком умещающуюся
                            // в fittingAvailable. Если ни одна строка не влезает — снап не найден (snapH=0).
                            // visibleH устанавливается ТОЛЬКО при найденном снапе: это защита от того
                            // чтобы nextOffset не вышел за пределы row.HeightPt и не дал отрицательный
                            // effectiveH на следующей странице, что ломает contentYPt.
                            float snapH = 0f;
                            if (forceByCell && fittingAvailable > 5f)
                            {
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
                                if (refCell != null)
                                {
                                    float cellPadTop = refCell.PadTopPt + refCell.Borders.Top.WidthPt;
                                    float cellPadBottom = refCell.PadBottomPt + refCell.Borders.Bottom.WidthPt;
                                    // На странице продолжения рендер добавляет cellPadTop сверху
                                    // (cellContentY += PadTop + Border_top в AddCellParasToLayouts).
                                    // Снап считает в координатах строки (без этого сдвига), поэтому
                                    // нужно уменьшить доступное пространство на cellPadTop,
                                    // иначе строки переполнят страницу.
                                    float snapAvailable = sliceFirstRowOffset > 0f
                                        ? fittingAvailable - cellPadTop
                                        : fittingAvailable;
                                    bool snapDone = false;
                                    foreach (var para in refCell.Paragraphs)
                                    {
                                        foreach (var line in para.Layout.Lines)
                                        {
                                            float lineBottom = cellPadTop
                                                + para.Ypt + line.Y + line.Height
                                                - sliceFirstRowOffset;
                                            if (lineBottom + cellPadBottom <= snapAvailable)
                                                snapH = lineBottom;
                                            else { snapDone = true; break; }
                                        }
                                        if (snapDone) break;
                                    }
                                }
                            }

                            if (forceByCell && snapH > 5f)
                            {
                                // Нашли строку текста для разреза — выполняем ByCell split.
                                // visibleH включает PadBottom+Border_bottom для корректной рамки таблицы.
                                // nextOffset основан только на snapH — без cellPadBottom,
                                // чтобы продолжение на следующей странице корректно выровнялось.
                                float splitCellPadBottom = 0f;
                                if (row.Cells.Count > 0)
                                {
                                    var sc = row.Cells[0];
                                    splitCellPadBottom = sc.PadBottomPt + sc.Borders.Bottom.WidthPt;
                                }
                                float visibleH = snapH + splitCellPadBottom;
                                float nextOffset = sliceFirstRowOffset + snapH;

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
                                ri--;
                                continue;
                            }
                            else if (!forceByCell)
                            {
                                // ByRow: только строка 0 может уйти на следующую страницу целиком.
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
                            else if (!atPageTop)
                            {
                                // forceByCell=true, но ни одна строка не влезла (snapH=0) или места < 5pt.
                                // Переносим на следующую страницу без создания пустого слайса.
                                if (ri > rowFrom)
                                {
                                    // Перед сменой страницы фиксируем строки rowFrom..ri-1 на текущей.
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
                                    // rowFrom обновляем до ri, иначе финальный слайс повторно
                                    // включит те же строки и контент задублируется.
                                    rowFrom = ri;
                                    sliceStartOffset = sliceFirstRowOffset;
                                    isFirstSlice = false;
                                }
                                pageYPt = pageYPt + pageHeightPt + PageGapPt;
                                pageBottomPt = pageYPt + pageHeightPt - mb;
                                pageIdx++;
                                newPages.Add(new PageRect(pageYPt, pageWidthPt, pageHeightPt, pageXPt, mt, ml, mb));
                                contentYPt = pageYPt + mt;
                                sliceStartY = contentYPt;
                                ri--;
                                continue;
                            }
                            // else: atPageTop — некуда двигаться, строка рендерится как есть (overflow)
                        }
                        else
                        {
                            // Финальное размещение: если это продолжение ByCell — ограничиваем
                            // высоту реальным контентом (max по ячейкам), иначе таблица занимает
                            // всё свободное место вместо того чтобы закончиться после контента.
                            if (sliceFirstRowOffset > 0f)
                            {
                                // maxCellH = cellPadTop + remaining + cellPadBottom.
                                // effectiveH = remaining + cellPadBottom (без cellPadTop).
                                // Поэтому maxCellH ВСЕГДА > effectiveH — проверка < effectiveH никогда
                                // не срабатывала. Используем maxCellH безусловно: это правильная
                                // визуальная высота строки (включает верхние рамку+паддинг).
                                float maxCellH = 0f;
                                foreach (var cell in row.Cells)
                                {
                                    float cPadTop = cell.PadTopPt + cell.Borders.Top.WidthPt;
                                    float cPadBot = cell.PadBottomPt + cell.Borders.Bottom.WidthPt;
                                    float consumed = Math.Max(0f, sliceStartOffset - cPadTop);
                                    float cellRemaining = Math.Max(0f, cell.ContentHeightPt - consumed);
                                    if (cellRemaining > 0f)
                                        maxCellH = Math.Max(maxCellH, cPadTop + cellRemaining + cPadBot);
                                }
                                if (maxCellH > 0f)
                                    effectiveH = maxCellH;
                            }
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
                        // Небольшой отступ чтобы первая строка продолжения не прилипала к верхнему полю.
                        contentYPt += PageContinuationTopPadPt;
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