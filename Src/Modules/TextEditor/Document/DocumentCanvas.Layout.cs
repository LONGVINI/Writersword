using Serilog;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using Writersword.Core.Models.Rendering;
using Writersword.Modules.TextEditor.Rendering;
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

        // ── Очистка кеша от мёртвых ParagraphViewModel ───────────────────
        //
        // Вызывается в начале каждого полного RebuildPageMode/RebuildFlowMode.
        // Удаляет записи PVM которых нет в DocVm.Paragraphs — они могли накопиться
        // после split/delete/undo операций. Без очистки Dictionary держит сильную
        // ссылку на мёртвые PVM и их SKTextLayout, не давая GC их собрать.
        private void PurgeDeadLayoutCacheEntries()
        {
            if (DocVm is null || _layoutCache.Count == 0) return;

            var alive = new HashSet<ParagraphViewModel>(DocVm.Paragraphs);
            var dead = new List<ParagraphViewModel>();

            foreach (var key in _layoutCache.Keys)
                if (!alive.Contains(key)) dead.Add(key);

            foreach (var key in dead)
                _layoutCache.Remove(key);
        }

        // ── Быстрое обновление одного параграфа (Phase 1) ───────────────
        //
        // Перестраивает layout ТОЛЬКО для одного ParagraphViewModel и немедленно
        // обновляет затронутые записи в _layouts через record-with.
        // Y-позиции параграфов после изменённого корректируются на дельту высоты.
        // Таблицы и ячейки не трогаем — их пересчитает полный RebuildLayouts (Phase 2).
        //
        // Вызывается из ScheduleRebuild ДО того как InvalidateFull() покажет кадр,
        // поэтому пользователь видит новый символ мгновенно.
        /// <summary>
        /// Быстрая вставка нового параграфа в _layouts без полного rebuild.
        /// Используется при Enter: параграф вставляется с оценочной высотой FallbackLinePt,
        /// последующие параграфы сдвигаются вниз. _canvasHeight обновляется немедленно.
        /// ScrollToCaret может найти позицию нового параграфа сразу после вставки.
        /// Background rebuild заменит оценку точными данными.
        /// </summary>
        private void QuickInsertParagraphLayout(int insertIdx, ParagraphViewModel newPvm)
        {
            var current = _layouts;
            if (current.Count == 0) { InvalidateMeasure(); return; }

            // Находим позицию вставки по индексу параграфа в DocVm.
            // Ищем первый ненулевой layout с индексом >= insertIdx-1 чтобы взять его Y+H.
            float insertYPt = 0f;
            int layoutInsertPos = current.Count;

            int docIdx = 0;
            for (int i = 0; i < current.Count; i++)
            {
                var pl = current[i];
                if (pl.Cell is not null) continue;
                if (docIdx == insertIdx)
                {
                    // Вставляем ПЕРЕД этим параграфом.
                    insertYPt = pl.Ypt;
                    layoutInsertPos = i;
                    break;
                }
                if (docIdx == insertIdx - 1)
                {
                    // Вставляем ПОСЛЕ этого параграфа.
                    insertYPt = pl.Ypt + pl.HeightPt;
                    layoutInsertPos = i + 1;
                }
                docIdx++;
            }

            float newH = FallbackLinePt;
            var newEntry = new ParaLayout(newPvm, null, insertYPt, newH, 0, 0, 0, AbsXPt: current[0].AbsXPt);

            var updated = new List<ParaLayout>(current.Count + 1);
            for (int i = 0; i < current.Count; i++)
            {
                if (i == layoutInsertPos)
                    updated.Add(newEntry);
                var pl = current[i];
                if (i >= layoutInsertPos && pl.Cell is null)
                    updated.Add(pl with { Ypt = pl.Ypt + newH });
                else
                    updated.Add(pl);
            }
            if (layoutInsertPos >= current.Count)
                updated.Add(newEntry);

            lock (_renderLock)
            {
                _layouts = updated;
                _canvasHeightPt += newH;
                _canvasHeight = _canvasHeightPt * PtToPx;
            }
            InvalidateMeasure();
            ScrollToCaret();
        }

        private void QuickUpdateParagraphLayout(ParagraphViewModel pvm)
        {
            if (_styleResolver is null && DocVm is not null)
                _styleResolver = new StyleResolver(DocVm.Document.Styles, _scriptFontMap);
            if (_styleResolver is null) return;

            float widthPt = GetCurrentTextWidthPt();

            // Строим layout для одного параграфа.
            // _layoutCache для этого pvm уже был удалён в ScheduleRebuild,
            // поэтому GetOrBuildLayout гарантированно пересчитывает.
            var newLayout = GetOrBuildLayout(pvm, widthPt);

            // Обновляем _layouts без замены всего списка.
            // Читаем снимок под lock, строим новый список вне lock, меняем под lock.
            List<ParaLayout> current;
            lock (_renderLock) { current = _layouts; }

            float yShift = 0f;
            bool seenPvm = false;
            var updated = new List<ParaLayout>(current.Count);

            for (int i = 0; i < current.Count; i++)
            {
                var pl = current[i];

                if (pl.Vm == pvm)
                {
                    // Высота как в полном пересборе page-режима: строки + интервал ПОСЛЕ.
                    // Интервал «перед» — это отступ до абзаца, в высоту записи не входит,
                    // иначе при наборе абзац «толстеет» на Space Before и текст прыгает.
                    float newH = Math.Max(newLayout.TotalHeightPt + newLayout.SpaceAfterPt, FallbackLinePt);
                    if (!seenPvm)
                    {
                        // Считаем дельту по первому вхождению этого pvm.
                        yShift = newH - pl.HeightPt;
                        seenPvm = true;
                    }
                    // Обновляем Layout и LineTo; Y и HeightPt берём из нового layout.
                    updated.Add(pl with
                    {
                        Layout = newLayout,
                        HeightPt = newH,
                        LineTo = newLayout.Lines.Count
                    });
                }
                else if (seenPvm && pl.Cell is null && yShift != 0f)
                {
                    // Сдвигаем параграфы без привязки к ячейке — они идут после изменённого.
                    // Параграфы внутри ячеек (pl.Cell != null) не трогаем: их пересчитает
                    // полный rebuild, а временная неточность в Y-позиции ячеек не критична.
                    updated.Add(pl with { Ypt = pl.Ypt + yShift });
                }
                else
                {
                    updated.Add(pl);
                }
            }

            if (seenPvm)
            {
                lock (_renderLock)
                {
                    _layouts = updated;
                    if (yShift != 0f)
                    {
                        _canvasHeightPt += yShift;
                        _canvasHeight = _canvasHeightPt * PtToPx;
                    }
                }
                // Если высота абзаца не изменилась (обычный набор без переноса строки) —
                // достаточно перерисовки. InvalidateMeasure дёргает MeasureOverride, а тот
                // пересобирает ВЕСЬ документ, поэтому на каждую клавишу шёл полный пересбор
                // всех абзацев — отсюда тормоза и моргание. Полный layout-pass нужен только
                // когда высота абзаца реально изменилась (перенос строки), чтобы обновить
                // скроллбар и сдвинуть последующие абзацы.
                if (yShift != 0f)
                    InvalidateMeasure();
                else
                    InvalidateFull();
            }
        }

        // Возвращает ширину текстовой зоны в точках для текущего режима и размера канваса.
        // Повторяет логику RebuildPageMode/RebuildFlowMode — нужно для QuickUpdateParagraphLayout.
        private float GetCurrentTextWidthPt()
        {
            if (DocVm is null) return 400f;
            switch (DocVm.ViewMode)
            {
                case EditorViewMode.Page:
                    {
                        float pw = GetPageWidthPt();
                        var (ml, _, mr, _) = GetPagePaddingPt();
                        return Math.Max(pw - ml - mr, 1f);
                    }
                case EditorViewMode.Reading:
                    {
                        float cw = (float)(_canvasWidth * PxToPt);
                        return Math.Max(Math.Min(cw, ReadingMaxPt) - DraftPadWPt * 2f, 1f);
                    }
                default:
                    return Math.Max((float)(_canvasWidth * PxToPt) - DraftPadWPt * 2f, 1f);
            }
        }

        private void RebuildPageMode()
        {
            // Удаляем из кеша записи параграфов которых больше нет в документе.
            // Без этого словарь растёт вечно: при split/delete старый ParagraphViewModel
            // удаляется из DocVm.Paragraphs но сильная ссылка в _layoutCache не даёт GC его собрать.
            PurgeDeadLayoutCacheEntries();

            float pageWidthPt = GetPageWidthPt();
            float pageHeightPt = GetPageHeightPt();
            var (ml, mt, mr, mb) = GetPagePaddingPt();
            float textWidthPt = Math.Max(pageWidthPt - ml - mr, 1f);
            float canvasWPt = (float)(_canvasWidth * PxToPt);
            float pageXPt = Math.Max((canvasWPt - pageWidthPt) / 2f, 0f);
            _layoutPageXPt = pageXPt;
            float textXPt = pageXPt + ml;

            float pageYPt = PageGapPt;
            float pageBottomPt = pageYPt + pageHeightPt - mb;
            float contentYPt = pageYPt + mt;
            int pageIdx = 0;

            var newLayouts = new List<ParaLayout>();
            var newPages = new List<PageRect>();
            var newTables = new List<TableEntry>();
            var newImages = new List<ImageEntry>();

            newPages.Add(new PageRect(pageYPt, pageWidthPt, pageHeightPt, pageXPt, mt, ml, mb));

            float pageOffsetXPx = pageXPt * PtToPx * (float)Zoom
                - (float)(_parentScrollViewer?.Offset.X ?? 0);
            _lastPageOffsetXPx = pageOffsetXPx;
            PageOffsetXChanged?.Invoke(pageOffsetXPx);

            var blocks = DocVm!.Document.Sections[0].Blocks;

            // O(1) поиск ParagraphViewModel по ParagraphBlock.
            // Без этого словаря был O(n²): для каждого из N блоков — O(n) перебор Paragraphs.
            var pvmByBlock = new Dictionary<ParagraphBlock, ParagraphViewModel>(DocVm.Paragraphs.Count);
            foreach (var p in DocVm.Paragraphs)
                if (p.Model is not null) pvmByBlock[p.Model] = p;

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
                    var tableLayout = GetOrBuildTableLayout(tableBlock, textWidthPt);
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
                                // visibleH включает PadBottom + Border_bottom для корректной рамки.
                                // Для страниц продолжения (sliceFirstRowOffset > 0) snapAvailable уже
                                // резервировал cellPadTop — теперь добавляем его в visibleH, чтобы
                                // нижний паддинг был виден (без этого gap = 0 из-за yBase offset).
                                // nextOffset основан только на snapH — без cellPadBottom/Top,
                                // чтобы продолжение на следующей странице корректно выровнялось.
                                float splitCellPadBottom = 0f;
                                float splitCellPadTop = 0f;
                                if (row.Cells.Count > 0)
                                {
                                    var sc = row.Cells[0];
                                    splitCellPadBottom = sc.PadBottomPt + sc.Borders.Bottom.WidthPt;
                                    if (sliceFirstRowOffset > 0f)
                                        splitCellPadTop = sc.PadTopPt + sc.Borders.Top.WidthPt;
                                }
                                float visibleH = snapH + splitCellPadBottom + splitCellPadTop;
                                float nextOffset = sliceFirstRowOffset + snapH;

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

                                rowFrom = ri;
                                sliceFirstRowOffset = nextOffset;
                                isFirstSlice = false;
                                ri--;
                                continue;
                            }
                            else if (!forceByCell)
                            {
                                // ByRow: только строка 0 может уйти на следующую страницу целиком.
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

                    // Зазор после таблицы не добавляется: расстояние до следующего блока
                    // управляется интервалом перед следующего параграфа. Печатная раскладка
                    // (BuildPageLayout) ведёт себя так же.

                    // Запоминаем позицию этой таблицы для якоря после неё.
                    lastTableXPt = tableXPt;
                    lastTableRightPt = tableXPt + tableLayout.TotalWidthPt;
                    lastTableBotPt = contentYPt; // истинный нижний край таблицы
                    continue;
                }

                if (block is ImageBlock imageBlock)
                {
                    float imgWpt = (float)imageBlock.WidthPt;
                    float imgHpt = (float)imageBlock.HeightPt;
                    if (imgWpt > 0f && imgHpt > 0f)
                    {
                        if (imageBlock.WrapMode == WrapMode.Inline)
                        {
                            // Блок: занимает собственную строку, сдвигает текст ниже.
                            // Перенос на новую страницу, если не влезает в остаток.
                            float available = pageBottomPt - contentYPt;
                            bool atPageTop = contentYPt <= pageYPt + mt + 0.5f;
                            if (imgHpt > available && !atPageTop)
                            {
                                pageYPt = pageYPt + pageHeightPt + PageGapPt;
                                pageBottomPt = pageYPt + pageHeightPt - mb;
                                contentYPt = pageYPt + mt;
                                pageIdx++;
                                newPages.Add(new PageRect(pageYPt, pageWidthPt, pageHeightPt, pageXPt, mt, ml, mb));
                            }

                            newImages.Add(new ImageEntry(imageBlock, contentYPt, textXPt, imgWpt, imgHpt, pageIdx));
                            contentYPt += imgHpt;
                        }
                        else
                        {
                            // Плавающая: позиция по смещению относительно области страницы,
                            // текст не сдвигается (обтекание пока не реализовано).
                            float fx = pageXPt + ml + (float)imageBlock.OffsetXPt;
                            float fy = pageYPt + mt + (float)imageBlock.OffsetYPt;
                            newImages.Add(new ImageEntry(imageBlock, fy, fx, imgWpt, imgHpt, pageIdx));
                        }
                    }
                    continue;
                }

                if (block is not ParagraphBlock paraBlock) continue;

                if (!pvmByBlock.TryGetValue(paraBlock, out var pvm)) continue;

                var layout = GetOrBuildLayout(pvm, textWidthPt);

                // Якорь перед таблицей: пустой параграф, следующий блок — таблица.
                bool isBeforeTableAnchor = string.IsNullOrEmpty(pvm.PlainText)
                    && bi + 1 < blocks.Count && blocks[bi + 1] is TableBlock;
                if (isBeforeTableAnchor)
                {
                    float anchorXPt = textXPt + (float)((TableBlock)blocks[bi + 1]).LeftIndentPt;
                    // Сдвигаем каретку чуть левее таблицы чтобы она не перекрывалась рамкой.
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
                    newLayouts.Add(new ParaLayout(
                        pvm, layout, anchorY, FallbackLinePt,
                        pageIdx, 0, 0,
                        AbsXPt: lastTableRightPt + AnchorMarginPt));
                    continue;
                }

                float absXPt = textXPt;

                // Пустой параграф в page mode — отдаём высоту одной строки.
                if (layout.Lines.Count == 0)
                {
                    newLayouts.Add(new ParaLayout(
                        pvm, layout,
                        pageYPt + contentYPt, FallbackLinePt,
                        pageIdx, 0, 0,
                        AbsXPt: textXPt));
                    contentYPt += FallbackLinePt;
                    continue;
                }

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
                _images = newImages;
                _canvasHeightPt = newCanvasH;
                _canvasHeight = newCanvasH * PtToPx;
            }
        }

        private void RebuildFlowMode(float maxWidthPt, float padHPt, float padWPt)
        {
            PurgeDeadLayoutCacheEntries();

            float textWidthPt = Math.Max(maxWidthPt - padWPt * 2f, 1f);
            float yPt = padHPt;

            var newLayouts = new List<ParaLayout>();
            var newTables = new List<TableEntry>();

            float lastTableRightPt = padWPt;
            float lastTableBotPt = padHPt;

            var blocks = DocVm!.Document.Sections[0].Blocks;

            var pvmByBlock = new Dictionary<ParagraphBlock, ParagraphViewModel>(DocVm.Paragraphs.Count);
            foreach (var p in DocVm.Paragraphs)
                if (p.Model is not null) pvmByBlock[p.Model] = p;

            for (int bi = 0; bi < blocks.Count; bi++)
            {
                var block = blocks[bi];

                if (block is TableBlock tableBlock)
                {
                    var tableLayout = GetOrBuildTableLayout(tableBlock, textWidthPt);
                    float tableXPt = padWPt + (float)tableBlock.LeftIndentPt;
                    int teIdx = newTables.Count;
                    newTables.Add(new TableEntry(tableBlock, tableLayout, yPt, tableXPt, 0));
                    AddCellParasToLayouts(newLayouts, tableBlock, tableLayout,
                        teIdx, tableXPt, yPt, 0);

                    lastTableRightPt = tableXPt + tableLayout.TotalWidthPt;
                    lastTableBotPt = yPt + tableLayout.TotalHeightPt;
                    yPt += tableLayout.TotalHeightPt;
                    continue;
                }

                if (block is not ParagraphBlock paraBlock) continue;

                if (!pvmByBlock.TryGetValue(paraBlock, out var pvm)) continue;

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

                // Пустой параграф (Enter в конце текста) — без строк в layout.
                // Не пропускаем: даём высоту одной строки чтобы yPt рос
                // и новые страницы создавались при нажатии Enter.
                if (layout.Lines.Count == 0)
                {
                    float emptyH = FallbackLinePt;
                    newLayouts.Add(new ParaLayout(
                        pvm, layout,
                        yPt, emptyH,
                        0, 0, 0,
                        AbsXPt: padWPt));
                    yPt += emptyH;
                    continue;
                }

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