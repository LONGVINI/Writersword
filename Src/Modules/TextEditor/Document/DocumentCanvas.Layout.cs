using Serilog;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
        /// <remarks>
        /// Таблицы, вложенные в ячейки, добавляются сюда же: каждая встаёт в newTables
        /// отдельной записью сразу за своей таблицей-хозяйкой, а абзацы её ячеек — в
        /// newLayouts перед тем абзацем хозяйской ячейки, перед которым она стоит. Так
        /// вложенная таблица рисуется, принимает каретку и выделение тем же кодом, что
        /// обычная.
        /// </remarks>
        private void AddCellParasToLayouts(
            List<ParaLayout> newLayouts,
            List<TableEntry> newTables,
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
                    maxCellPadTop = Math.Max(maxCellPadTop, cl.PadTopPt + cl.TopInsetPt);
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

                    float cellBT = cellLayout.TopInsetPt;
                    float cellBB = cellLayout.BottomInsetPt;
                    float cellPadTopTotal = cellBT + cellLayout.PadTopPt;
                    float cellPadBotTotal = cellBB + cellLayout.PadBottomPt;

                    float cellContentX = tableXPt + cellLayout.Xpt + cellLayout.ContentInsetLeftPt;

                    // cellBaseY — Y верха этой строки на текущей странице.
                    // Для строк после rowFrom: строка rowFrom имеет effectiveRowH увеличенный
                    // на maxCellPadTop (см. RenderTableStructureOnly), поэтому сдвигаем на
                    // (firstRowOffset - maxCellPadTop) вместо firstRowOffset.
                    float extraOffset = rowLayout.Row != rowFrom && firstRowOffset > 0f
                        ? firstRowOffset - maxCellPadTop : 0f;
                    float cellBaseY = tableYPt + cellLayout.Ypt - rowOffsetY - extraOffset;

                    // Боковая рамка рисуется по краю ячейки: внутри ячейки лежит её половина.
                    float clipX = tableXPt + cellLayout.Xpt + cellLayout.Borders.Left.SpanPt / 2f;
                    float clipW = cellLayout.WidthPt
                        - (cellLayout.Borders.Left.SpanPt + cellLayout.Borders.Right.SpanPt) / 2f;

                    // pageVisibleRow — высота строки, видимая на этой странице (в координатах строки).
                    float pageVisibleRow = isByCellSplit
                        ? lastRowVisibleH
                        : (rowLayout.HeightPt - effectiveOffset);

                    // Объединённая по вертикали ячейка занимает все накрытые строки:
                    // клип по высоте одной строки резал её текст на высоте первой.
                    pageVisibleRow = CellSpanHeightPt(
                        tableLayout, cellLayout, effectiveRowTo, lastRowVisibleH, pageVisibleRow);

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

                    // Повёрнутая ячейка раскладывается целиком и только там, где начинается
                    // её строка: построчный разрез по страницам идёт поперёк её строк и для
                    // неё не имеет смысла, а остаток на следующей странице покажет клип.
                    if (cellLayout.IsRotated)
                    {
                        if (effectiveOffset <= 0f)
                            AddRotatedCellParasToLayouts(newLayouts, tableBlock, cellLayout, modelCell,
                                tableEntryIdx, pageIdx, cellContentX, cellBaseY + cellPadTopTotal,
                                clipX, clipY, clipW, clipH);
                        continue;
                    }

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
                    // Строки абзаца лежат под его интервалом «перед»: верх текста —
                    // Ypt + SpaceBeforePt. Без интервала разрез страницы считался выше
                    // настоящих строк, и на странице оставалась половина строки.
                    int lastVisiblePi = -1;
                    for (int pi = cellLayout.Paragraphs.Count - 1; pi >= 0; pi--)
                    {
                        var cp = cellLayout.Paragraphs[pi];
                        float pcY = contentOffsetY + cp.Ypt + cp.Layout.SpaceBeforePt;
                        if (cp.Layout.Lines.Count == 0)
                        {
                            if (pcY > P) { lastVisiblePi = pi; break; }
                            continue;
                        }
                        var ll = cp.Layout.Lines[^1];
                        if (pcY + ll.Y + ll.Height > P + CellCutTolerancePt) { lastVisiblePi = pi; break; }
                    }

                    // Таблицы внутри ячейки, стоящие перед абзацем beforeParagraph (число,
                    // равное количеству абзацев, — после последнего).
                    //
                    // Вложенная таблица между страницами не делится: она встаёт на ту
                    // страницу, где помещается целиком. Место разреза строки-хозяйки
                    // всегда лежит на границе строк текста, поэтому таблица оказывается
                    // либо выше разреза, либо ниже.
                    void AddNestedTables(int beforeParagraph)
                    {
                        if (cellLayout.NestedTables.Count == 0) return;
                        if (modelCell?.NestedTables is not { Count: > 0 } modelNested) return;

                        int paragraphCount = cellLayout.Paragraphs.Count;

                        foreach (var nested in cellLayout.NestedTables)
                        {
                            bool standsHere = beforeParagraph >= paragraphCount
                                ? nested.BeforeParagraphIndex >= paragraphCount
                                : nested.BeforeParagraphIndex == beforeParagraph;
                            if (!standsHere) continue;
                            if (nested.SourceIndex < 0 || nested.SourceIndex >= modelNested.Count) continue;

                            // Верх и низ таблицы в координатах содержимого ячейки.
                            float nestedTop = contentOffsetY + nested.Ypt;
                            float nestedBottom = nestedTop + nested.Layout.TotalHeightPt;

                            // Уже показана на прошлой странице.
                            if (nestedBottom <= P + CellCutTolerancePt) continue;

                            // Не помещается до разреза — уходит на следующую страницу.
                            if (contentCutY < float.MaxValue
                                && nestedBottom > contentCutY + CellCutTolerancePt) continue;

                            // Положение на листе — как у абзаца ячейки на этом же месте.
                            float nestedYPt = cellContentY + nestedTop;
                            if (effectiveOffset > 0f)
                            {
                                float consumedBefore = effectiveOffset - cellPadTopTotal;
                                nestedYPt += effectiveOffset - Math.Min(nested.Ypt, consumedBefore);
                            }

                            float nestedXPt = cellContentX + nested.Xpt;
                            var nestedBlock = modelNested[nested.SourceIndex].Table;

                            int nestedEntryIdx = newTables.Count;
                            newTables.Add(new TableEntry(
                                nestedBlock, nested.Layout, nestedYPt, nestedXPt, pageIdx));
                            AddCellParasToLayouts(
                                newLayouts, newTables, nestedBlock, nested.Layout,
                                nestedEntryIdx, nestedXPt, nestedYPt, pageIdx);
                        }
                    }

                    for (int pi = 0; pi < cellLayout.Paragraphs.Count; pi++)
                    {
                        AddNestedTables(pi);

                        var cellPara = cellLayout.Paragraphs[pi];
                        var paraBlock = pi < modelCell.Paragraphs.Count
                            ? modelCell.Paragraphs[pi] : null;
                        if (paraBlock is null) continue;

                        if (!_cellVmCache.TryGetValue(paraBlock, out var vm))
                        {
                            vm = new ParagraphViewModel(paraBlock);
                            _cellVmCache[paraBlock] = vm;
                        }

                        // Верх текста абзаца в координатах содержимого ячейки: под
                        // интервалом «перед».
                        float paraContentY = contentOffsetY + cellPara.Ypt + cellPara.Layout.SpaceBeforePt;

                        // Пропускаем параграфы целиком до или после видимой области.
                        // Место разреза — низ строки, посчитанный в проходе раскладки
                        // тем же сложением в другом порядке: сравнение идёт с допуском,
                        // иначе строка на самом разрезе показалась бы на обеих страницах.
                        if (cellPara.Layout.Lines.Count > 0)
                        {
                            var fl = cellPara.Layout.Lines[0];
                            var ll = cellPara.Layout.Lines[^1];
                            if (paraContentY + ll.Y + ll.Height <= P + CellCutTolerancePt) continue;
                            if (contentCutY < float.MaxValue
                                && paraContentY + fl.Y >= contentCutY - CellCutTolerancePt) continue;
                        }

                        // lineFrom: первая строка, заканчивающаяся после P (видимая на этой странице).
                        int lineFrom = 0;
                        if (P > 0f)
                        {
                            for (int li = 0; li < cellPara.Layout.Lines.Count; li++)
                            {
                                var ln = cellPara.Layout.Lines[li];
                                if (paraContentY + ln.Y + ln.Height > P + CellCutTolerancePt) { lineFrom = li; break; }
                                lineFrom = li + 1;
                            }
                        }

                        // lineTo: последняя строка, кончающаяся до contentCutY.
                        int lineTo = cellPara.Layout.Lines.Count;
                        if (contentCutY < float.MaxValue)
                        {
                            lineTo = lineFrom;
                            for (int li = lineFrom; li < cellPara.Layout.Lines.Count; li++)
                            {
                                var ln = cellPara.Layout.Lines[li];
                                if (paraContentY + ln.Y + ln.Height <= contentCutY + CellCutTolerancePt)
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

                        // Маркер списка ячейки: значок рисуется по этому полю, а не по
                        // тексту в модели. Без него элемент списка в ячейке выглядел
                        // как обычный абзац с отступом.
                        Rendering.ListMarkerInfo? cellMarker =
                            _cellListMarkers.TryGetValue(paraBlock, out var mi) ? mi : null;

                        newLayouts.Add(new ParaLayout(
                            vm,
                            cellPara.Layout,
                            absParaY,
                            paraHeight,
                            pageIdx,
                            lineFrom,
                            lineTo > 0 ? lineTo : cellPara.Layout.Lines.Count,
                            AbsXPt: cellContentX,
                            Cell: info,
                            Marker: cellMarker));
                    }

                    // Таблицы после последнего абзаца ячейки.
                    AddNestedTables(cellLayout.Paragraphs.Count);
                }
            }
        }

        /// <summary>
        /// Абзацы повёрнутой ячейки в _layouts. Координаты абзацев — раскладка обычного
        /// горизонтального текста с началом в левом верхнем углу области содержимого;
        /// на лист их переводит матрица ячейки (SKTextRenderer.RotatedCellMatrix).
        /// </summary>
        private void AddRotatedCellParasToLayouts(
            List<ParaLayout> newLayouts,
            TableBlock tableBlock,
            SKTableCellLayout cellLayout,
            TableCell modelCell,
            int tableEntryIdx,
            int pageIdx,
            float contentLeftPt,
            float contentTopPt,
            float clipX, float clipY, float clipW, float clipH)
        {
            float contentWidthPt = cellLayout.ContentAreaWidthPt;
            float contentHeightPt = cellLayout.HeightPt
                - cellLayout.PadTopPt - cellLayout.PadBottomPt
                - cellLayout.TopInsetPt - cellLayout.BottomInsetPt;

            var rotation = SKTextRenderer.RotatedCellMatrix(
                cellLayout.TextDirection, contentLeftPt, contentTopPt,
                contentWidthPt, contentHeightPt);

            float stackTopPt = contentTopPt
                + SKTextRenderer.RotatedCellStackOffset(cellLayout, contentWidthPt);

            for (int pi = 0; pi < cellLayout.Paragraphs.Count; pi++)
            {
                var cellPara = cellLayout.Paragraphs[pi];
                var paraBlock = pi < modelCell.Paragraphs.Count ? modelCell.Paragraphs[pi] : null;
                if (paraBlock is null) continue;

                if (!_cellVmCache.TryGetValue(paraBlock, out var vm))
                {
                    vm = new ParagraphViewModel(paraBlock);
                    _cellVmCache[paraBlock] = vm;
                }

                var info = new CellInfo(
                    tableBlock, modelCell, paraBlock, pi, tableEntryIdx,
                    contentLeftPt, stackTopPt,
                    clipX, clipY, clipW, clipH,
                    cellLayout.TextDirection, rotation);

                float absParaY = stackTopPt + cellPara.Ypt + cellPara.Layout.SpaceBeforePt;

                float paraHeight = cellPara.Layout.TotalHeightPt;
                if (pi + 1 < cellLayout.Paragraphs.Count)
                {
                    var next = cellLayout.Paragraphs[pi + 1];
                    float nextAbsY = stackTopPt + next.Ypt + next.Layout.SpaceBeforePt;
                    paraHeight = Math.Max(paraHeight, nextAbsY - absParaY);
                }

                Rendering.ListMarkerInfo? cellMarker =
                    _cellListMarkers.TryGetValue(paraBlock, out var mi) ? mi : null;

                newLayouts.Add(new ParaLayout(
                    vm,
                    cellPara.Layout,
                    absParaY,
                    paraHeight,
                    pageIdx,
                    0,
                    cellPara.Layout.Lines.Count,
                    AbsXPt: contentLeftPt,
                    Cell: info,
                    Marker: cellMarker));
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

            // Состояние гистерезиса обтекания живёт по тем же правилам, что и кеш:
            // без чистки словарь удерживал бы ссылки на удалённые ParagraphBlock.
            if (_wrapPushState.Count > 0)
            {
                var aliveBlocks = new HashSet<ParagraphBlock>();
                foreach (var p in DocVm.Paragraphs)
                    if (p.Model is not null) aliveBlocks.Add(p.Model);

                var deadBlocks = new List<ParagraphBlock>();
                foreach (var key in _wrapPushState.Keys)
                    if (!aliveBlocks.Contains(key)) deadBlocks.Add(key);

                foreach (var key in deadBlocks)
                    _wrapPushState.Remove(key);
            }
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
                _styleResolver = CreateStyleResolver();
            if (_styleResolver is null) return;

            float widthPt = GetCurrentTextWidthPt();

            // Обновляем _layouts без замены всего списка.
            // Читаем снимок под lock, строим новый список вне lock, меняем под lock.
            List<ParaLayout> current;
            List<ImageEntry> currentImages;
            List<ShapeEntry> currentShapes;
            List<TableEntry> currentTables;
            List<PageRect> currentPages;
            lock (_renderLock)
            {
                current = _layouts;
                currentImages = _images;
                currentShapes = _shapes;
                currentTables = _tables;
                currentPages = _pages;
            }

            // Фигуры обтекаются наравне с картинками, поэтому быстрый путь обязан
            // видеть и их: иначе при наборе текст лез бы на фигуру до ближайшего
            // полного пересбора.
            var currentFloats = BuildFloatSource(currentImages, currentShapes);

            // Таблицы с обтеканием — тоже: абзац рядом с такой таблицей при наборе
            // должен обходить её так же, как в полном проходе.
            AppendFloatingTablesBefore(currentFloats, currentTables, pvm.Model);

            // Верх и левый край абзаца берём из текущей записи раскладки: по ним
            // считаются зоны обтекания. Без них быстрый путь строил абзац без учёта
            // плавающих картинок — при наборе текст ложился поверх картинки и выходил
            // за полосу обтекания, пока не срабатывал отложенный полный пересбор.
            // Страница абзаца нужна там же: по ней строится геометрия вытеснения.
            float paraTopPt = 0f;
            float paraLeftPt = 0f;
            int paraPageIdx = -1;
            bool hasEntry = false;
            for (int i = 0; i < current.Count; i++)
            {
                if (current[i].Vm != pvm || current[i].Cell is not null) continue;
                paraTopPt = current[i].Ypt;
                paraLeftPt = current[i].AbsXPt;
                paraPageIdx = current[i].PageIndex;
                hasEntry = true;
                break;
            }

            // Страницы идут вместе с картинками: по ним габарит обтекаемого объекта
            // обрезается краями ЕГО страницы. Без них зона жила в координатах документа
            // и дотягивалась до следующей страницы — текст там обтекал картинку, которой
            // на листе не видно: строки расходились двумя колонками вокруг пустого
            // коридора, а низ страницы оставался незаполненным. Полный пересбор страницы
            // передаёт; быстрый путь, работающий на каждое нажатие клавиши, — не
            // передавал, поэтому расхождение набегало по ходу набора.
            // Окно поиска зон — как в полном проходе: высота самого абзаца плюс шаг
            // страницы. Раздавать зоны абзацу на пол-документа вперёд нельзя: строка,
            // перешедшая через границу листа, попадала бы в зону чужой страницы.
            float quickPageStepPt = currentPages.Count > 0
                ? currentPages[0].HeightPt + PageGapPt
                : GetPageHeightPt() + PageGapPt;
            float quickLookAheadPt = GetOrBuildLayout(pvm, widthPt).TotalHeightPt + quickPageStepPt;

            // Страница абзаца здесь известна точно — она записана в раскладке, — поэтому
            // зоны берутся только со своего листа и следующего.
            var wrapZones = hasEntry && DocVm?.ViewMode == EditorViewMode.Page
                ? ComputeWrapZones(currentFloats, paraTopPt, paraLeftPt, widthPt, currentPages,
                    pageIndex: paraPageIdx >= 0 ? paraPageIdx : null,
                    lookAheadPt: quickLookAheadPt,
                    maxPageIndex: paraPageIdx >= 0 ? paraPageIdx + 1 : null)
                : null;

            // Геометрия страницы абзаца: без неё строка, вытесненная под картинку,
            // переезжала нижний край листа, хотя за ним начинается следующая страница,
            // картинки этой страницы там уже нет и вытеснять не за чем — перенос делает
            // пагинация.
            Rendering.SKTextRenderer.WrapPageContext? wrapPages = null;
            if (wrapZones is not null && paraPageIdx >= 0 && paraPageIdx < currentPages.Count)
            {
                var paraPage = currentPages[paraPageIdx];
                float pageStepPt = paraPage.HeightPt + PageGapPt;
                wrapPages = new Rendering.SKTextRenderer.WrapPageContext(
                    ParaStartYPt: paraTopPt,
                    PageBottomPt: paraPage.Ypt + paraPage.HeightPt - paraPage.PadBottomPt,
                    NextPageTopPt: paraPage.Ypt + pageStepPt + paraPage.PadTopPt
                                 + PageContinuationTopPadPt,
                    PageStepPt: pageStepPt);
            }

            // Строим layout для одного параграфа.
            // _layoutCache для этого pvm уже был удалён в ScheduleRebuild,
            // поэтому GetOrBuildLayout гарантированно пересчитывает.
            // Раскладка с зонами обтекания не кешируется — зоны зависят от позиций
            // плавающих объектов, а ключ кеша (текст, ширина) их не учитывает.
            var newLayout = wrapZones is null
                ? GetOrBuildLayout(pvm, widthPt)
                : BuildWrappedLayout(pvm, widthPt, wrapZones, wrapPages);

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
                    // Нижней отсечки к FallbackLinePt для непустого абзаца быть не должно:
                    // полный пересбор её не применяет, и при строке чуть ниже FallbackLinePt
                    // newH оказывался больше сохранённого HeightPt — каждое нажатие давало
                    // ложный yShift ~1px и весь текст ниже дёргался. FallbackLinePt нужен
                    // только для пустого абзаца (строк нет).
                    float newH = newLayout.Lines.Count == 0
                        ? FallbackLinePt
                        : newLayout.TotalHeightPt + newLayout.SpaceAfterPt;
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

            // Книжный разворот верстается страницами, а не потоком: ширина текста
            // берётся с виртуального листа. Без этой ветки прогрев кеша шейпил абзацы
            // под колонку чтения, пересчёт просил другую ширину, кеш никогда не
            // сходился — и полный проход раскладки не выполнялся вовсе.
            if (DocVm.IsSpreadReading)
            {
                float spreadW = GetPageWidthPt();
                var (sl, _, sr, _) = GetPagePaddingPt();
                return Math.Max(spreadW - sl - sr, 1f);
            }

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
                        // Лента верстается страницами документа, а не колонкой по
                        // ширине окна: ширина текста у неё та же, что на бумаге.
                        // Без этой ветки прогрев кеша шейпил абзацы под колонку,
                        // пересчёт просил ширину листа, и кеш не сходился никогда.
                        float rw = GetPageWidthPt();
                        var (rl, _, rr, _) = GetPagePaddingPt();
                        return Math.Max(rw - rl - rr, 1f);
                    }
                default:
                    return Math.Max((float)(_canvasWidth * PxToPt) - DraftPadWPt * 2f, 1f);
            }
        }

        // Источник зон обтекания для текущего прохода пагинации. null — зоны берутся
        // из картинок, накопленных по ходу прохода (только блоки, встреченные раньше
        // абзаца). Каждый следующий проход подставляет сюда ПОЛНЫЙ список картинок
        // прошлого прохода — обтекание работает и для абзацев, стоящих в документе до
        // блока. Замороженные записи переходят из прохода в проход как есть, поэтому
        // у них это положение первого прохода; картинка, идущая за своим абзацем
        // (FollowsAnchorParagraph), приходит сюда с места, где абзац стоял в прошлом.
        private List<ImageEntry>? _wrapZoneImagesOverride;

        // То же для фигур: их смещения отсчитываются от страницы блока в потоке, а
        // она между проходами может съехать. Без заморозки зона фигуры и сама фигура
        // разъезжаются ровно так же, как это было у картинок.
        private List<ShapeEntry>? _wrapZoneShapesOverride;

        // Итеративная сходимость обтекания. Проход строит зоны от предсказанного верха
        // абзаца, но на стыке страниц предсказание промахивается: абзац рисуется этажом
        // ниже, чем посчитаны зоны, и текст ложится на картинку либо не перебрасывается.
        // Решение — фиксированная точка: каждый следующий проход берёт якорь абзаца из
        // РЕАЛЬНО измеренной позиции его первой строки в предыдущем проходе. Через
        // несколько итераций позиции перестают меняться.
        //   In  — якоря, от которых текущий проход строит зоны (пусто = брать предсказание).
        //   Out — позиции первых строк, замеренные текущим проходом; вход для следующего.
        private Dictionary<ParagraphBlock, float> _wrapAnchorIn = new();
        private Dictionary<ParagraphBlock, float> _wrapAnchorOut = new();

        private void RebuildPageMode()
        {
            // Первый проход: без якорей и без полного набора картинок (они собираются
            // по ходу). Даёт стартовые позиции абзацев и полный список картинок.
            _wrapZoneImagesOverride = null;
            _wrapZoneShapesOverride = null;
            _wrapAnchorIn.Clear();
            _wrapAnchorOut = new Dictionary<ParagraphBlock, float>();

            // Ни один проход не публикуется по ходу дела: в первом проходе абзацы ещё не
            // знают про картинку (её зоны собираются по ходу) и верстаются во всю ширину.
            // Стоит показать этот кадр — и первая строка мигает полной шириной на каждой
            // пересборке. Наружу уходит только итоговая раскладка.
            _publishPassResults = false;
            try
            {
                RebuildPageModeConverge();

                // Колонтитулы печатаются не на всех листах — место под них у каждого
                // листа своё и известно только по готовой раскладке. Вышло другим, чем
                // то, с которым раскладка строилась, — она повторяется. Потолок проходов
                // защищает от дребезга листа на границе.
                for (int bandPass = 0; bandPass < 2 && RefreshBandReserveFromPass(); bandPass++)
                {
                    _wrapZoneImagesOverride = null;
                    _wrapZoneShapesOverride = null;
                    _wrapAnchorIn.Clear();
                    _wrapAnchorOut = new Dictionary<ParagraphBlock, float>();

                    RebuildPageModeConverge();
                }
            }
            finally
            {
                // Итоговая раскладка отдаётся рендеру ровно один раз — в том числе если
                // проход упал: иначе канвас остался бы с раскладкой прошлой пересборки,
                // а флаг публикации навсегда выключенным.
                _publishPassResults = true;
                PublishPassResults();
                _wrapZoneImagesOverride = null;
                _wrapZoneShapesOverride = null;
            }
        }

        /// <summary>
        /// Проходы раскладки страниц до сходимости обтекания. Результат остаётся
        /// в полях прохода — публикует его вызывающий.
        /// </summary>
        private void RebuildPageModeConverge()
        {
            RebuildPageModePass();

            var firstPassImages = _passImages;
            var firstPassShapes = _passShapes;

            bool hasWrapImages = false;
            foreach (var ie in firstPassImages)
            {
                if (ie.Block.WrapMode is WrapMode.Square or WrapMode.Tight)
                {
                    hasWrapImages = true;
                    break;
                }
            }

            if (!hasWrapImages)
            {
                foreach (var se in firstPassShapes)
                {
                    if (se.Block.WrapMode is WrapMode.Square or WrapMode.Tight)
                    {
                        hasWrapImages = true;
                        break;
                    }
                }
            }

            if (!hasWrapImages) return;

            // Итерации до сходимости: якорь каждого абзаца берём из позиции, замеренной
            // прошлым проходом, и повторяем, пока позиции не перестанут двигаться.
            // Потолок итераций защищает от возможного дребезга картинки ровно на границе.
            // ВО ВРЕМЯ ДРАГА картинки — один проход: при неполной сходимости результат
            // прыгает между двумя состояниями по чётности итерации, и соседний контент
            // (в т.ч. inline-картинка) дёргается. Точная сходимость нужна в покое; на
            // отпускании кнопки идёт обычная пересборка со всеми итерациями.
            _wrapZoneImagesOverride = firstPassImages;
            _wrapZoneShapesOverride = firstPassShapes;
            // Частичный проход при сдвиге поля — тоже жест: итог всё равно пересчитает
            // полный проход после отпускания.
            // Линию таблицы тоже тянут жестом: каждое движение мыши пересобирает
            // раскладку, и полная сходимость на каждом из них делала жест вязким.
            // Итог пересчитывается полностью на отпускании кнопки (FinishTableDrag).
            int maxWrapIterations =
                (_imageDragging || _imageResizing || _imageRotating
                 || _shapeDragging || _shapeResizing || _shapeRotating
                 || _tableDragMode != TableDragMode.None
                 || _partialFromBlock >= 0) ? 1 : 4;
            const float ConvergedTolPt = 0.5f;

            for (int iter = 0; iter < maxWrapIterations; iter++)
            {
                // Выход прошлого прохода становится входом текущего.
                (_wrapAnchorIn, _wrapAnchorOut) = (_wrapAnchorOut, _wrapAnchorIn);
                _wrapAnchorOut.Clear();

                RebuildPageModePass();

                // Сошлось, если каждый замер этого прохода совпал с поданным якорем.
                bool converged = true;
                foreach (var kv in _wrapAnchorOut)
                {
                    if (!_wrapAnchorIn.TryGetValue(kv.Key, out float prev)
                        || Math.Abs(prev - kv.Value) > ConvergedTolPt)
                    {
                        converged = false;
                        break;
                    }
                }

                // Обтекаемые объекты, идущие за своим абзацем, тоже обязаны встать на
                // место: зоны этого прохода построены по их положению в прошлом.
                if (converged && !FlowFloatsSettled(
                        _wrapZoneImagesOverride!, _passImages,
                        _wrapZoneShapesOverride!, _passShapes, ConvergedTolPt))
                {
                    converged = false;
                }

                // Следующий проход строит зоны по местам, где объекты стоят сейчас.
                // Замороженные записи переходят из прохода в проход без изменений,
                // поэтому для них это всё то же положение первого прохода.
                _wrapZoneImagesOverride = _passImages;
                _wrapZoneShapesOverride = _passShapes;

                if (converged) break;
            }
        }

        /// <summary>
        /// Идёт ли плавающий объект из Word за своим абзацем от прохода к проходу.
        ///
        /// Объект, отсчитанный по вертикали от абзаца (relativeFrom="paragraph" или
        /// "line"), обязан стоять там, где абзац оказался в итоге, а не там, где он был
        /// в первом проходе: обтекание выше по документу раздвигает текст, и абзац
        /// уезжает вниз вместе со всем, что к нему привязано.
        ///
        /// Заморозка остаётся только у обтекаемого объекта, поднятого над своим абзацем
        /// (отрицательное смещение): он способен вытеснить текст ВЫШЕ своего абзаца, тот
        /// сдвигает абзац, абзац — объект, и раскладка перестаёт сходиться. Объект на
        /// уровне абзаца или ниже на текст до абзаца не влияет, и обратной связи нет.
        /// Объект без обтекания текст не двигает вовсе.
        /// </summary>
        private static bool FollowsAnchorParagraph(IFloatingObject floating, TableFloatPosition? anchor)
        {
            if (anchor is not { VerticalAnchor: TableFloatAnchor.Text }) return false;
            if (floating.WrapMode is not (WrapMode.Square or WrapMode.Tight)) return true;
            return anchor.YPt >= 0.0;
        }

        /// <summary>
        /// Стоят ли обтекаемые объекты, идущие за абзацем, там же, где в прошлом
        /// проходе. Объект без обтекания зон не строит и на сходимость не влияет.
        /// </summary>
        private static bool FlowFloatsSettled(
            List<ImageEntry> previousImages, List<ImageEntry> images,
            List<ShapeEntry> previousShapes, List<ShapeEntry> shapes,
            float tolerancePt)
        {
            foreach (var entry in images)
            {
                if (entry.InLine) continue;
                if (entry.Block.WrapMode is not (WrapMode.Square or WrapMode.Tight)) continue;
                if (!FollowsAnchorParagraph(entry.Block, entry.Block.AnchorPosition)) continue;

                bool found = false;
                foreach (var previous in previousImages)
                {
                    if (!ReferenceEquals(previous.Block, entry.Block)) continue;
                    found = true;
                    if (previous.PageIndex != entry.PageIndex
                        || Math.Abs(previous.Ypt - entry.Ypt) > tolerancePt
                        || Math.Abs(previous.XPt - entry.XPt) > tolerancePt)
                        return false;
                    break;
                }
                if (!found) return false;
            }

            foreach (var entry in shapes)
            {
                if (entry.Block.WrapMode is not (WrapMode.Square or WrapMode.Tight)) continue;
                if (!FollowsAnchorParagraph(entry.Block, entry.Block.AnchorPosition)) continue;

                bool found = false;
                foreach (var previous in previousShapes)
                {
                    if (!ReferenceEquals(previous.Block, entry.Block)) continue;
                    found = true;
                    if (previous.PageIndex != entry.PageIndex
                        || Math.Abs(previous.Ypt - entry.Ypt) > tolerancePt
                        || Math.Abs(previous.XPt - entry.XPt) > tolerancePt)
                        return false;
                    break;
                }
                if (!found) return false;
            }

            return true;
        }

        /// <summary>
        /// Подпись прошлой пробы вёрстки. Проба пишется не один раз за запуск, а при
        /// каждом изменении её содержимого: импорт документа меняет лист и интервалы
        /// уже после первой раскладки, и одноразовая запись его не застаёт.
        /// </summary>
        private string? _paginationProbeSignature;

        /// <summary>
        /// Снимать ли пробу вёрстки. Выключена: проба нужна при разборе расхождений с
        /// внешним редактором, а в обычной работе она пишется на каждую пересборку
        /// раскладки и топит журнал так, что рядом ничего не видно.
        /// </summary>
        private static readonly bool PaginationProbeEnabled = false;

        /// <summary>
        /// Пишет в журнал всё, от чего зависит разбивка на страницы: лист, поля,
        /// раскладку строк по всему документу и — главное — сколько места остаётся
        /// незанятым внизу страниц. Пустой остаток и есть разница с внешним
        /// редактором: если он близок к высоте строки, строки уводят вниз правила
        /// переноса, если близок к нулю — строки просто выше вордовских.
        /// </summary>
        private void LogPaginationProbe(
            List<ParaLayout> layouts,
            List<PageRect> pages,
            float pageWidthPt, float pageHeightPt,
            float ml, float mt, float mr, float mb,
            float textWidthPt)
        {
            if (!PaginationProbeEnabled) return;

            try
            {
                if (pages.Count == 0 || layouts.Count == 0) return;

                var styles = _styleResolver ?? CreateStyleResolver();

                int totalLines = 0;
                var lineHeights = new List<float>(8192);

                // Нижняя занятая граница каждой страницы. NaN — на странице нет текста.
                var usedBottom = new float[pages.Count];
                for (int i = 0; i < usedBottom.Length; i++) usedBottom[i] = float.NaN;

                // Сколько строк набрано каждым сочетанием «гарнитура, кегль».
                var linesByFont = new Dictionary<string, int>(StringComparer.Ordinal);

                foreach (var pl in layouts)
                {
                    int lines = pl.LineTo - pl.LineFrom;
                    if (lines <= 0) lines = 1;
                    totalLines += lines;

                    if (pl.PageIndex >= 0 && pl.PageIndex < usedBottom.Length)
                    {
                        float bottom = pl.Ypt + pl.HeightPt;
                        if (float.IsNaN(usedBottom[pl.PageIndex]) || bottom > usedBottom[pl.PageIndex])
                            usedBottom[pl.PageIndex] = bottom;
                    }

                    var layout = pl.Layout;
                    if (layout is null || layout.Lines.Count == 0) continue;

                    int from = Math.Max(0, pl.LineFrom);
                    int to = Math.Min(pl.LineTo, layout.Lines.Count);
                    for (int li = from; li < to; li++)
                        lineHeights.Add(layout.Lines[li].Height);

                    string fontKey = DescribeParagraphFont(pl, styles);
                    linesByFont.TryGetValue(fontKey, out int had);
                    linesByFont[fontKey] = had + Math.Max(1, to - from);
                }

                if (lineHeights.Count == 0) return;

                lineHeights.Sort();
                float medianLinePt = lineHeights[lineHeights.Count / 2];

                // Остаток внизу страницы: сколько ещё оставалось до нижнего поля после
                // последней строки. Последняя страница не в счёт — она не заполнена
                // по построению.
                var slack = new List<float>(pages.Count);
                for (int i = 0; i < pages.Count - 1; i++)
                {
                    if (float.IsNaN(usedBottom[i])) continue;
                    float bottomPt = pages[i].Ypt + pages[i].HeightPt - mb;
                    slack.Add(bottomPt - usedBottom[i]);
                }

                float slackAvgPt = 0f, slackMedianPt = 0f;
                int pagesWithSpareLine = 0;
                if (slack.Count > 0)
                {
                    double sum = 0;
                    foreach (float s in slack)
                    {
                        sum += s;
                        if (s >= medianLinePt) pagesWithSpareLine++;
                    }
                    slackAvgPt = (float)(sum / slack.Count);

                    var sorted = new List<float>(slack);
                    sorted.Sort();
                    slackMedianPt = sorted[sorted.Count / 2];
                }

                // Три самых частых сочетания «гарнитура, кегль» по числу строк.
                var topFonts = new List<KeyValuePair<string, int>>(linesByFont);
                topFonts.Sort((a, b) => b.Value.CompareTo(a.Value));
                var fontsText = new StringBuilder();
                for (int i = 0; i < topFonts.Count && i < 3; i++)
                {
                    if (i > 0) fontsText.Append("; ");
                    fontsText.Append(topFonts[i].Key).Append(" — ").Append(topFonts[i].Value).Append(" стр.");
                }

                float textHeightPt = pageHeightPt - mt - mb;
                float linesPerPage = (float)totalLines / pages.Count;

                string signature = string.Join('|',
                    pageWidthPt, pageHeightPt, ml, mt, mr, mb, textWidthPt,
                    pages.Count, totalLines, medianLinePt, slackAvgPt, fontsText.ToString());

                if (signature == _paginationProbeSignature) return;
                _paginationProbeSignature = signature;

                _logger.Information(
                    "[PAGINATION PROBE] лист {PW}x{PH} pt, поля Л{ML} В{MT} П{MR} Н{MB} pt, текст {TW}x{TH} pt | " +
                    "страниц {Pages}, строк {Lines}, строк на страницу {PerPage} | " +
                    "высота строки: медиана {LineMed} pt, минимум {LineMin} pt, максимум {LineMax} pt | " +
                    "пусто внизу страницы: в среднем {SlackAvg} pt, медиана {SlackMed} pt, " +
                    "страниц с местом под ещё одну строку: {Spare} из {Counted} | " +
                    "верхний отступ продолжения {ContPad} pt | шрифты: {Fonts}",
                    pageWidthPt.ToString("F1"), pageHeightPt.ToString("F1"),
                    ml.ToString("F1"), mt.ToString("F1"), mr.ToString("F1"), mb.ToString("F1"),
                    textWidthPt.ToString("F1"), textHeightPt.ToString("F1"),
                    pages.Count, totalLines, linesPerPage.ToString("F2"),
                    medianLinePt.ToString("F2"),
                    lineHeights[0].ToString("F2"),
                    lineHeights[lineHeights.Count - 1].ToString("F2"),
                    slackAvgPt.ToString("F2"), slackMedianPt.ToString("F2"),
                    pagesWithSpareLine, slack.Count,
                    PageContinuationTopPadPt.ToString("F1"),
                    fontsText.ToString());
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[PAGINATION PROBE] не удалось снять пробу вёрстки");
            }
        }

        /// <summary>
        /// Гарнитура и кегль абзаца так, как их видит раскладка: из первого явно
        /// оформленного отрезка, а без него — из стиля абзаца.
        /// </summary>
        private static string DescribeParagraphFont(ParaLayout pl, Rendering.StyleResolver styles)
        {
            var para = pl.Vm.Model;

            Models.Inline.RunProperties? runProps = null;
            foreach (var chunk in para.Chunks)
            {
                foreach (var run in chunk.Runs)
                    if (run.Properties is not null) { runProps = run.Properties; break; }

                if (runProps is not null) break;
            }

            string styleName = para.Properties.StyleName ?? "Normal";

            string family = !string.IsNullOrEmpty(runProps?.FontFamily)
                ? runProps!.FontFamily!
                : styles.ResolveFontFamily(styleName);

            float sizePt = runProps?.FontSize.HasValue == true
                ? (float)runProps.FontSize.Value
                : styles.ResolveFontSize(styleName);

            return family + " " + sizePt.ToString("F1") + " pt";
        }

        private void RebuildPageModePass()
        {
            // Удаляем из кеша записи параграфов которых больше нет в документе.
            // Без этого словарь растёт вечно: при split/delete старый ParagraphViewModel
            // удаляется из DocVm.Paragraphs но сильная ссылка в _layoutCache не даёт GC его собрать.
            PurgeDeadLayoutCacheEntries();

            // Маркер предпросмотра переполнения выставляется заново на каждом пересборе:
            // если картинка больше не переполняет страницу (или драг завершён) — сбрасывается.
            _imageOverflowPreviewBlock = null;

            float pageWidthPt = GetPageWidthPt();
            float pageHeightPt = GetPageHeightPt();
            var (ml, mt, mr, mb) = GetPagePaddingPt();

            // Поле листа без колонтитулов. Верх и низ каждого листа уточняются по его
            // колонтитулам (DocumentCanvas.BandReserve): mt и mb ниже — поля того листа,
            // который сейчас заполняется.
            float baseMt = mt, baseMb = mb;
            (mt, mb) = PagePaddingForPage(0, baseMt, baseMb);

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
            var newShapes = new List<ShapeEntry>();
            var newInlineTransferred = new HashSet<ImageBlock>();

            // Места явных разрывов страницы — для их отметки при показе непечатаемых
            // знаков (DocumentCanvas.FormattingMarks).
            var newBreakMarks = new List<PageBreakMark>();

            // Картинки с жёсткой привязкой к странице: позиционируются после основного
            // потока, когда известно общее число страниц и достроены недостающие.
            var pinnedImages = new List<ImageBlock>();

            // Фигуры с жёсткой привязкой к странице — та же отложенная обработка:
            // их лист может быть ещё не создан, пока идёт основной поток.
            var pinnedShapes = new List<ShapeBlock>();

            // Частичный проход — живой сдвиг поля страницы (DocumentCanvas.MarginPreview).
            // Листы до стартового стоят на прежних местах и несут прежнее содержимое,
            // поток начинается с верха стартового листа и идёт только до листа
            // _partialToPage. Полный проход начинается, как всегда, с первого листа.
            bool partialPass = _partialFromBlock >= 0;
            if (partialPass)
            {
                SeedPartialPass(
                    newPages, newLayouts, newTables, newImages, newShapes, newInlineTransferred,
                    newBreakMarks,
                    pageWidthPt, pageHeightPt, pageXPt, mt, ml, mb);

                // Листы, перенесённые из прошлой раскладки, получают поля своих
                // колонтитулов, а не общие.
                for (int seeded = 0; seeded < newPages.Count; seeded++)
                {
                    var (seedTop, seedBottom) = PagePaddingForPage(seeded, baseMt, baseMb);
                    newPages[seeded] = newPages[seeded] with { PadTopPt = seedTop, PadBottomPt = seedBottom };
                }

                pageIdx = _partialFromPage;
                (mt, mb) = PagePaddingForPage(pageIdx, baseMt, baseMb);
                pageYPt = newPages[pageIdx].Ypt;
                pageBottomPt = pageYPt + pageHeightPt - mb;
                contentYPt = pageYPt + mt;
            }
            else
            {
                newPages.Add(new PageRect(pageYPt, pageWidthPt, pageHeightPt, pageXPt, mt, ml, mb));
            }

            // Первичная отправка идёт по левому краю потока: раскладка ещё строится, и
            // на какой лист попадёт каретка, пока неизвестно. Точное значение по листу
            // каретки уходит в линейку в PublishPassResults, когда страницы посчитаны.
            //
            // Делается это только на самой первой сборке. Дальше страницы уже посчитаны,
            // и слать левый край потока значит на каждом кадре уводить линейку на первый
            // лист и возвращать обратно. При двух листах в ряду это било в глаза больше
            // всего: перетаскивание любой стрелки пересобирает раскладку десятки раз в
            // секунду, и линейка на каждом кадре срывалась к середине.
            if (_pages.Count == 0)
            {
                float pageOffsetXPx = pageXPt * PtToPx * (float)Zoom
                    - (float)(_parentScrollViewer?.Offset.X ?? 0);
                _lastPageOffsetXPx = pageOffsetXPx;
                PageOffsetXChanged?.Invoke(pageOffsetXPx);
            }

            var blocks = DocVm!.Document.Sections[0].Blocks;

            // Схлопывание интервалов между абзацами (см. DocumentModel.CollapseParagraphSpacing):
            // сколько интервала после оставил предыдущий абзац и где он кончился. Абзац,
            // начавшийся ровно там же, забирает из своего интервала до уже пройденную часть.
            bool collapseSpacing = DocVm.Document.CollapseParagraphSpacing;
            float collapsePrevAfterPt = 0f;
            float collapseEndYPt = float.NaN;

            // Нумерация списков за один проход по блокам в порядке следования.
            var markerMap = Rendering.ListNumberingEngine.Compute(blocks);

            // Интервалы между абзацами одного стиля: вывод о соседях — до раскладки.
            ApplyContextualSpacing(blocks);

            // Абзацы в ячейках таблиц в blocks не входят, поэтому маркеры для них
            // считаются отдельно и кладутся прямо в модель: раскладка ячеек строится
            // ниже, и к этому моменту текст маркера должен быть готов.
            _cellListMarkers.Clear();
            ApplyListMarkerTextsInTables(blocks, GetCurrentTextWidthPt(), markerMap);

            // O(1) поиск ParagraphViewModel по ParagraphBlock.
            // Без этого словаря был O(n²): для каждого из N блоков — O(n) перебор Paragraphs.
            var pvmByBlock = new Dictionary<ParagraphBlock, ParagraphViewModel>(DocVm.Paragraphs.Count);
            foreach (var p in DocVm.Paragraphs)
                if (p.Model is not null) pvmByBlock[p.Model] = p;

            // Отслеживаем позицию последней обработанной таблицы для позиционирования якоря после неё.
            float lastTableXPt = textXPt;
            float lastTableRightPt = textXPt;
            float lastTableBotPt = contentYPt;

            for (int bi = partialPass ? _partialFromBlock : 0; bi < blocks.Count; bi++)
            {
                // Частичный проход кончается, как только поток ушёл за последний
                // нужный лист: дальше видимой области вёрстка не нужна.
                if (partialPass && pageIdx > _partialToPage) break;

                var block = blocks[bi];

                // Раздел под свёрнутым заголовком на лист не ложится
                // (DocumentCanvas.HeadingCollapse).
                if (IsCollapsedBlock(block)) continue;

                if (block is BreakBlock bb && bb.BreakType == BreakType.Page)
                {
                    // Разрыв внутри абзаца Word отмечается на последней строке куска до
                    // разрыва; если этого куска на листе нет (свёрнут под заголовком или
                    // перед разрывом стоит не абзац), отметка остаётся отдельной строкой.
                    ParagraphBlock? breakTail = bb.InParagraph && bi > 0
                        && blocks[bi - 1] is ParagraphBlock tailBlock && !IsCollapsedBlock(tailBlock)
                        ? tailBlock
                        : null;

                    newBreakMarks.Add(new PageBreakMark(
                        pageIdx, contentYPt, breakTail, bb.ContinuesParagraph, bb.FromColumnBreak));

                    pageYPt = pageYPt + pageHeightPt + PageGapPt;
                    (mt, mb) = PagePaddingForPage(pageIdx + 1, baseMt, baseMb);
                    pageBottomPt = pageYPt + pageHeightPt - mb;
                    contentYPt = pageYPt + mt;
                    pageIdx++;
                    newPages.Add(new PageRect(pageYPt, pageWidthPt, pageHeightPt, pageXPt, mt, ml, mb));
                    continue;
                }

                if (block is TableBlock tableBlock)
                {
                    var tableLayout = GetOrBuildTableLayout(tableBlock, textWidthPt);

                    // Таблица с обтеканием текстом стоит в своей точке листа и строку в
                    // потоке не занимает: текст следующих абзацев обходит её по зоне
                    // обтекания, как плавающую картинку (DocumentCanvas.FloatingTables).
                    if (tableBlock.FloatPosition is { } floatPosition)
                    {
                        float floatWPt = tableLayout.TotalWidthPt;
                        float floatHPt = tableLayout.GetTotalHeightPt();

                        // Таблица, привязанная к тексту, уходит на следующую страницу вместе
                        // со своим абзацем, когда под ним ей не хватает места. Таблицу выше
                        // листа переносить некуда — она остаётся где стоит.
                        bool floatAtPageTop = contentYPt <= pageYPt + mt + 0.5f;
                        if (floatPosition.VerticalAnchor == TableFloatAnchor.Text
                            && !floatAtPageTop
                            && contentYPt + (float)floatPosition.YPt + floatHPt > pageBottomPt
                            && floatHPt <= pageBottomPt - (pageYPt + mt))
                        {
                            pageYPt = pageYPt + pageHeightPt + PageGapPt;
                            (mt, mb) = PagePaddingForPage(pageIdx + 1, baseMt, baseMb);
                            pageBottomPt = pageYPt + pageHeightPt - mb;
                            contentYPt = pageYPt + mt;
                            pageIdx++;
                            newPages.Add(new PageRect(pageYPt, pageWidthPt, pageHeightPt, pageXPt, mt, ml, mb));
                        }

                        var (floatXPt, floatYPt) = FloatingTableOrigin(
                            floatPosition, floatWPt, floatHPt,
                            textXPt, textWidthPt, pageXPt, pageWidthPt,
                            pageYPt, pageHeightPt, mt, mb, contentYPt);

                        int floatEntryIdx = newTables.Count;
                        newTables.Add(new TableEntry(tableBlock, tableLayout, floatYPt, floatXPt, pageIdx));
                        AddCellParasToLayouts(newLayouts, newTables, tableBlock, tableLayout,
                            floatEntryIdx, floatXPt, floatYPt, pageIdx,
                            0, -1, 0f, -1f);

                        // Высоту в потоке таблица не занимает: contentYPt остаётся на месте,
                        // и абзац под таблицей начинается там же, где начался бы без неё.
                        continue;
                    }

                    float tableXPt = textXPt
                        + (float)tableBlock.ResolveLeftOffsetPt(textWidthPt, tableLayout.TotalWidthPt);
                    bool byCell = tableBlock.SplitMode == TableSplitMode.ByCell;
                    float fullPageH = pageHeightPt - mt - mb;

                    // Строка-шапка повторяется над продолжением таблицы на каждой следующей
                    // странице, как у Word. Место под неё отнимается у страницы до строк
                    // куска, а рисуется она над куском (TableEntry.RepeatedHeaderHeightPt):
                    // это копия для чтения, абзацев у неё в раскладке страницы нет, и
                    // правится шапка в первой строке таблицы.
                    //
                    // Шапка не повторяется, когда она выше трети листа — под строки не
                    // осталось бы места — и когда её ячейка объединена со строками ниже:
                    // копия одной строки оборвала бы такую ячейку посередине.
                    float repeatedHeaderH = 0f;
                    if (tableBlock.RepeatHeader && tableLayout.Rows.Count > 1)
                    {
                        var headerRow = tableLayout.Rows[0];
                        bool headerSpansDown = false;
                        foreach (var headerCell in headerRow.Cells)
                        {
                            if (headerCell.RowSpan > 1) { headerSpansDown = true; break; }
                        }

                        if (!headerSpansDown && headerRow.HeightPt > 0f && headerRow.HeightPt <= fullPageH / 3f)
                            repeatedHeaderH = headerRow.HeightPt;
                    }

                    // Высота шапки над текущим куском таблицы: у первого куска её нет.
                    float sliceHeaderH = 0f;

                    // Картинка с обтеканием не должна ложиться на таблицу. Текст обходит
                    // её зону построчно, картинка в потоке встаёт сбоку, но таблица не
                    // умеет ни того, ни другого: её ширина и левый край фиксированы.
                    // Поэтому при перекрытии таблица уходит целиком под картинку.
                    var tableZoneSource = BuildFloatSource(
                        _wrapZoneImagesOverride ?? newImages,
                        _wrapZoneShapesOverride ?? newShapes);
                    ResolveTableTop(
                        tableZoneSource, ref contentYPt,
                        tableXPt, tableLayout.TotalWidthPt, tableLayout.GetTotalHeightPt(),
                        textXPt, textWidthPt, pageBottomPt, newPages);

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

                        // Под повторённой шапкой строка стоит так же «вверху страницы», как
                        // и без неё: выше шапки ей подняться некуда.
                        bool atPageTop = contentYPt <= pageYPt + mt + sliceHeaderH + 0.5f;

                        // Строка встаёт на страницу, если помещается в оставшееся место, и может
                        // стоять вплотную к нижнему полю — как у Word. Запас под строкой
                        // (TableRowEndGapPt) задаётся в одном месте и сейчас нулевой: с запасом
                        // на страницу входило на строку меньше, чем у Word, и длинная таблица
                        // разбивалась по страницам иначе.
                        float fittingAvailable = atPageTop ? available : available - TableRowEndGapPt;

                        if (effectiveH > fittingAvailable && (!atPageTop || sliceFirstRowOffset > 0f || effectiveH > fullPageH - sliceHeaderH))
                        {
                            // ByRow: строка целиком переносится на следующую страницу.
                            //   Исключение: если строка выше целой страницы — разрывается постранично.
                            // ByCell: все строки разрываются постранично.
                            // ri > 0: строки 1+ никогда не уходят на следующую страницу целиком —
                            // только режутся по ячейкам. Уйти может только строка 0 (в режиме ByRow).
                            // sliceFirstRowOffset > 0: продолжение ByCell, нельзя сбрасывать offset через ByRow.
                            bool forceByCell = byCell || effectiveH > fullPageH - sliceHeaderH || sliceFirstRowOffset > 0f || ri > 0;

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

                                    // Строки повёрнутой ячейки идут поперёк разреза страницы
                                    // и опорой для него быть не могут: берём самую высокую
                                    // из обычных ячеек строки, если такая есть.
                                    if (refCell.IsRotated)
                                    {
                                        SKTableCellLayout? plain = null;
                                        foreach (var candidate in row.Cells)
                                        {
                                            if (candidate.IsRotated) continue;
                                            if (plain is null || candidate.ContentHeightPt > plain.ContentHeightPt)
                                                plain = candidate;
                                        }
                                        refCell = plain;
                                    }
                                }
                                if (refCell != null)
                                {
                                    float cellPadTop = refCell.PadTopPt + refCell.TopInsetPt;
                                    float cellPadBottom = refCell.PadBottomPt + refCell.BottomInsetPt;
                                    // На странице продолжения рендер добавляет cellPadTop сверху
                                    // (cellContentY += PadTop + Border_top в AddCellParasToLayouts).
                                    // Снап считает в координатах строки (без этого сдвига), поэтому
                                    // нужно уменьшить доступное пространство на cellPadTop,
                                    // иначе строки переполнят страницу.
                                    float snapAvailable = sliceFirstRowOffset > 0f
                                        ? fittingAvailable - cellPadTop
                                        : fittingAvailable;
                                    foreach (var para in refCell.Paragraphs)
                                    {
                                        var paraLines = para.Layout.Lines;
                                        if (paraLines.Count == 0) continue;

                                        // Верх текста абзаца в координатах куска строки. Строки
                                        // лежат под интервалом «перед» абзаца: без него низ строки
                                        // считался выше настоящего, разрез проходил посреди
                                        // следующей строки, и та оставалась на странице половиной.
                                        float textTop = cellPadTop
                                            + para.Ypt + para.Layout.SpaceBeforePt
                                            - sliceFirstRowOffset;

                                        // shownBefore — строки абзаца, оставшиеся на прошлых
                                        // страницах; fitting — строки до низа этой страницы.
                                        int shownBefore = 0;
                                        int fitting = 0;
                                        for (int li = 0; li < paraLines.Count; li++)
                                        {
                                            float lineBottom = textTop + paraLines[li].Y + paraLines[li].Height;

                                            if (sliceFirstRowOffset > 0f && lineBottom <= CellCutTolerancePt)
                                            {
                                                shownBefore = li + 1;
                                                fitting = li + 1;
                                                continue;
                                            }

                                            if (lineBottom + cellPadBottom <= snapAvailable) fitting = li + 1;
                                            else break;
                                        }

                                        if (fitting >= paraLines.Count)
                                        {
                                            // Абзац помещается целиком: разрез не выше его низа.
                                            if (fitting > shownBefore)
                                                snapH = textTop + paraLines[^1].Y + paraLines[^1].Height;
                                            continue;
                                        }

                                        // Абзац рвётся на этой странице. Запрет висячих строк —
                                        // тот же, что у абзацев вне таблицы, и у Word он работает
                                        // в ячейке так же: одна последняя строка не уезжает на
                                        // следующую страницу, одна первая не остаётся внизу этой.
                                        int linesHere = fitting - shownBefore;

                                        if (paraLines.Count - fitting == 1 && linesHere >= 2)
                                        {
                                            fitting--;
                                            linesHere--;
                                        }

                                        if (shownBefore == 0 && linesHere == 1)
                                        {
                                            fitting = 0;
                                            linesHere = 0;
                                        }

                                        if (linesHere > 0)
                                            snapH = textTop + paraLines[fitting - 1].Y + paraLines[fitting - 1].Height;

                                        break;
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
                                    splitCellPadBottom = sc.PadBottomPt + sc.BottomInsetPt;
                                    if (sliceFirstRowOffset > 0f)
                                        splitCellPadTop = sc.PadTopPt + sc.TopInsetPt;
                                }
                                float visibleH = snapH + splitCellPadBottom + splitCellPadTop;
                                float nextOffset = sliceFirstRowOffset + snapH;

                                int teIdx = newTables.Count;
                                newTables.Add(new TableEntry(tableBlock, tableLayout,
                                    sliceStartY, tableXPt, pageIdx,
                                    RowFrom: rowFrom, RowTo: ri + 1,
                                    LastRowVisibleHeightPt: visibleH,
                                    FirstRowContentOffsetPt: sliceStartOffset,
                                    IsContinuation: !isFirstSlice,
                                    RepeatedHeaderHeightPt: sliceHeaderH));
                                AddCellParasToLayouts(newLayouts, newTables, tableBlock, tableLayout,
                                    teIdx, tableXPt, sliceStartY, pageIdx,
                                    rowFrom, ri + 1, sliceStartOffset, visibleH);

                                pageYPt = pageYPt + pageHeightPt + PageGapPt;
                                (mt, mb) = PagePaddingForPage(pageIdx + 1, baseMt, baseMb);
                                pageBottomPt = pageYPt + pageHeightPt - mb;
                                pageIdx++;
                                newPages.Add(new PageRect(pageYPt, pageWidthPt, pageHeightPt, pageXPt, mt, ml, mb));
                                contentYPt = pageYPt + mt;

                                // Продолжение таблицы встало вверху новой страницы, где может
                                // лежать обтекаемая картинка. Проверка перед циклом сделана для
                                // исходной позиции в потоке и к этому месту отношения не имеет.
                                ResolveTableTop(
                                    tableZoneSource, ref contentYPt,
                                    tableXPt, tableLayout.TotalWidthPt,
                                    RemainingTableHeightPt(tableLayout, ri, nextOffset),
                                    textXPt, textWidthPt, pageBottomPt, newPages);

                                // Над продолжением — шапка: строки куска встают под ней.
                                sliceHeaderH = ri > 0 ? repeatedHeaderH : 0f;
                                contentYPt += sliceHeaderH;

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
                                        IsContinuation: !isFirstSlice,
                                        RepeatedHeaderHeightPt: sliceHeaderH));
                                    AddCellParasToLayouts(newLayouts, newTables, tableBlock, tableLayout,
                                        teIdx, tableXPt, sliceStartY, pageIdx,
                                        rowFrom, ri, sliceStartOffset, -1f);
                                }

                                pageYPt = pageYPt + pageHeightPt + PageGapPt;
                                (mt, mb) = PagePaddingForPage(pageIdx + 1, baseMt, baseMb);
                                pageBottomPt = pageYPt + pageHeightPt - mb;
                                pageIdx++;
                                newPages.Add(new PageRect(pageYPt, pageWidthPt, pageHeightPt, pageXPt, mt, ml, mb));
                                contentYPt = pageYPt + mt;

                                // Строка ушла на новую страницу целиком — её новое место
                                // проверяется на обтекание заново.
                                ResolveTableTop(
                                    tableZoneSource, ref contentYPt,
                                    tableXPt, tableLayout.TotalWidthPt,
                                    RemainingTableHeightPt(tableLayout, ri, 0f),
                                    textXPt, textWidthPt, pageBottomPt, newPages);

                                // Кусок, начатый самой шапкой, копии шапки над собой не несёт.
                                sliceHeaderH = ri > 0 ? repeatedHeaderH : 0f;
                                contentYPt += sliceHeaderH;

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
                                        IsContinuation: !isFirstSlice,
                                        RepeatedHeaderHeightPt: sliceHeaderH));
                                    AddCellParasToLayouts(newLayouts, newTables, tableBlock, tableLayout,
                                        teIdx, tableXPt, sliceStartY, pageIdx,
                                        rowFrom, ri, sliceStartOffset, -1f);
                                    // rowFrom обновляем до ri, иначе финальный слайс повторно
                                    // включит те же строки и контент задублируется.
                                    rowFrom = ri;
                                    sliceStartOffset = sliceFirstRowOffset;
                                    isFirstSlice = false;
                                }
                                pageYPt = pageYPt + pageHeightPt + PageGapPt;
                                (mt, mb) = PagePaddingForPage(pageIdx + 1, baseMt, baseMb);
                                pageBottomPt = pageYPt + pageHeightPt - mb;
                                pageIdx++;
                                newPages.Add(new PageRect(pageYPt, pageWidthPt, pageHeightPt, pageXPt, mt, ml, mb));
                                contentYPt = pageYPt + mt;

                                // Перенос без снапа: строка целиком уезжает на новую страницу,
                                // где её положение так же может попасть под картинку.
                                ResolveTableTop(
                                    tableZoneSource, ref contentYPt,
                                    tableXPt, tableLayout.TotalWidthPt,
                                    RemainingTableHeightPt(tableLayout, ri, sliceFirstRowOffset),
                                    textXPt, textWidthPt, pageBottomPt, newPages);

                                // Над продолжением — шапка: строки куска встают под ней.
                                sliceHeaderH = ri > 0 ? repeatedHeaderH : 0f;
                                contentYPt += sliceHeaderH;

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
                                    float cPadTop = cell.PadTopPt + cell.TopInsetPt;
                                    float cPadBot = cell.PadBottomPt + cell.BottomInsetPt;
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
                            IsContinuation: !isFirstSlice,
                            RepeatedHeaderHeightPt: sliceHeaderH));
                        AddCellParasToLayouts(newLayouts, newTables, tableBlock, tableLayout,
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

                if (block is ShapeBlock shapeBlock)
                {
                    // Габарит берётся через общий пересчёт чтения — тем же путём, что
                    // и у картинки: лист чтения меньше печатного, и фигура в исходном
                    // размере на нём непропорционально крупная.
                    var (shapeWpt, shapeHpt) = ReadingShapeSize(shapeBlock);

                    if (shapeBlock.WrapMode == WrapMode.Inline)
                    {
                        // Фигура-блок занимает собственную строку и сдвигает текст ниже —
                        // ровно как картинка «в тексте». Повёрнутая занимает свой AABB,
                        // фигура из Word — свою рамку с полями обрамления (FloatingObjectBox).
                        var (shapeBoxW, shapeBoxH) = FloatingObjectBox.Of(shapeBlock, shapeWpt, shapeHpt);

                        var shapeZoneSource = BuildFloatSource(
                            _wrapZoneImagesOverride ?? newImages,
                            _wrapZoneShapesOverride ?? newShapes);

                        ResolveInlineImageBand(
                            shapeZoneSource, ref contentYPt, shapeBoxW, shapeBoxH,
                            textXPt, textWidthPt, pageBottomPt,
                            out float shapeBandLeftPt, out float shapeBandRightPt,
                            newPages, pageIdx);

                        // Не влезает в остаток листа — уходит на следующий, как блок текста.
                        bool shapeAtPageTop = contentYPt <= pageYPt + mt + 0.5f;
                        if (shapeBoxH > pageBottomPt - contentYPt && !shapeAtPageTop)
                        {
                            pageYPt = pageYPt + pageHeightPt + PageGapPt;
                            (mt, mb) = PagePaddingForPage(pageIdx + 1, baseMt, baseMb);
                            pageBottomPt = pageYPt + pageHeightPt - mb;
                            contentYPt = pageYPt + mt;
                            pageIdx++;
                            newPages.Add(new PageRect(pageYPt, pageWidthPt, pageHeightPt, pageXPt, mt, ml, mb));

                            ResolveInlineImageBand(
                                shapeZoneSource, ref contentYPt, shapeBoxW, shapeBoxH,
                                textXPt, textWidthPt, pageBottomPt,
                                out shapeBandLeftPt, out shapeBandRightPt,
                                newPages, pageIdx);
                        }

                        float shapeBandWidthPt = shapeBandRightPt - shapeBandLeftPt;
                        float shapeBoxXPt = shapeBandLeftPt;
                        float shapeSlackPt = shapeBandWidthPt - shapeBoxW;
                        if (shapeSlackPt > 0f)
                        {
                            shapeBoxXPt = shapeBlock.Alignment switch
                            {
                                Models.Styles.TextAlignment.Center => shapeBandLeftPt + shapeSlackPt / 2f,
                                Models.Styles.TextAlignment.Right => shapeBandLeftPt + shapeSlackPt,
                                _ => shapeBandLeftPt
                            };
                        }

                        // Запись хранит неповёрнутый прямоугольник, центрированный в AABB:
                        // рендер поворачивает вокруг его центра, и фигура остаётся в боксе.
                        newShapes.Add(new ShapeEntry(
                            shapeBlock,
                            contentYPt + (shapeBoxH - shapeHpt) / 2f,
                            shapeBoxXPt + (shapeBoxW - shapeWpt) / 2f,
                            shapeWpt, shapeHpt, pageIdx));
                        contentYPt += shapeBoxH;
                    }
                    else if (shapeBlock.PinnedPage > 0)
                    {
                        // Привязанная к странице: позиция считается позже, когда станут
                        // известны все страницы — её листа может ещё не быть.
                        pinnedShapes.Add(shapeBlock);
                    }
                    else
                    {
                        // Фигура из Word, обтекаемая и отсчитанная от абзаца, не
                        // помещается под ним до низа листа — абзац с ней уходит на
                        // следующий (см. AnchoredFloatsNeedPt).
                        if (contentYPt > pageYPt + mt + 0.5f)
                        {
                            float shapeFloatsNeedPt = AnchoredFloatsNeedPt(blocks, bi);
                            if (shapeFloatsNeedPt > 0f
                                && contentYPt + shapeFloatsNeedPt > pageBottomPt
                                && shapeFloatsNeedPt <= pageBottomPt - (pageYPt + mt))
                            {
                                pageYPt = pageYPt + pageHeightPt + PageGapPt;
                                (mt, mb) = PagePaddingForPage(pageIdx + 1, baseMt, baseMb);
                                pageBottomPt = pageYPt + pageHeightPt - mb;
                                contentYPt = pageYPt + mt;
                                pageIdx++;
                                newPages.Add(new PageRect(pageYPt, pageWidthPt, pageHeightPt, pageXPt, mt, ml, mb));
                            }
                        }

                        // Плавающая. Как и у картинки, проходы сходимости обязаны видеть
                        // фигуру ТАМ ЖЕ, где по ней построены зоны: иначе обтекание
                        // вытесняет текст, поток над фигурой становится выше, фигура на
                        // следующем проходе встаёт относительно другой страницы — и
                        // раскладка перестаёт сходиться.
                        //
                        // Фигура из Word, отсчитанная от своего абзаца, не замораживается:
                        // она обязана идти за абзацем, когда текст выше него раздвигается
                        // на следующих проходах (см. FollowsAnchorParagraph).
                        ShapeEntry? frozenShape = null;
                        if (_wrapZoneShapesOverride is { } frozenShapes
                            && !FollowsAnchorParagraph(shapeBlock, shapeBlock.AnchorPosition))
                        {
                            foreach (var fs in frozenShapes)
                            {
                                if (!ReferenceEquals(fs.Block, shapeBlock)) continue;
                                frozenShape = fs;
                                break;
                            }
                        }

                        if (frozenShape is { } fzs)
                        {
                            newShapes.Add(new ShapeEntry(
                                shapeBlock, fzs.Ypt, fzs.XPt, shapeWpt, shapeHpt, fzs.PageIndex));
                        }
                        else
                        {
                            // У фигуры из Word точка отсчёта своя: лист, поля или верх её
                            // абзаца — блок фигуры стоит в потоке прямо перед ним.
                            (float XPt, float YPt)? shapeOrigin = null;
                            if (shapeBlock.AnchorPosition is { } shapeAnchor)
                            {
                                shapeOrigin = shapeAnchor.ResolveOrigin(
                                    shapeWpt, shapeHpt,
                                    textXPt, textWidthPt, pageXPt, pageWidthPt,
                                    pageYPt, pageHeightPt, mt, mb, contentYPt);
                            }

                            var built = BuildShapeEntry(
                                shapeBlock, pageXPt, pageYPt, ml, mt, newPages, pageIdx, shapeOrigin);

                            // Поправка на сторону переплёта — как у картинки.
                            float shapeGutterPt = LayoutGutterCompensationPt(
                                shapeBlock.AnchorPosition, built.PageIndex);
                            if (shapeGutterPt != 0f)
                                built = built with { XPt = built.XPt + shapeGutterPt };

                            var (avX, avY) = AvoidReadingOverlap(
                                built.XPt, built.Ypt, built.WidthPt, built.HeightPt,
                                shapeBlock.RotationDeg, built.PageIndex,
                                newPages, newImages, newShapes, newTables, shapeBlock);

                            newShapes.Add(built with { XPt = avX, Ypt = avY });
                        }
                    }
                    continue;
                }

                if (block is ImageBlock imageBlock)
                {
                    // Габарит берётся через общий пересчёт чтения: лист чтения меньше
                    // печатного, и картинка в исходном размере вылезала бы за колонку.
                    var (imgWpt, imgHpt) = ReadingImageSize(imageBlock);
                    if (imgWpt > 0f && imgHpt > 0f)
                    {
                        if (imageBlock.WrapMode == WrapMode.Inline)
                        {
                            // Блок: занимает собственную строку, сдвигает текст ниже.
                            // Повёрнутая картинка занимает в потоке свой AABB — габарит
                            // повёрнутого прямоугольника, поэтому текст ниже сдвигается
                            // на реальную высоту с учётом угла. Картинка из Word занимает
                            // свою рамку с полями обрамления, как у Word (FloatingObjectBox).
                            var (boxWpt, boxHpt) = FloatingObjectBox.Of(imageBlock, imgWpt, imgHpt);

                            // Обтекание: картинка в потоке обходит соседнюю обтекаемую
                            // картинку так же, как текст. Полоса может сузиться (встанем
                            // сбоку) или картинка уедет ниже зоны — тогда contentYPt
                            // сдвигается здесь же.
                            var imageZoneSource = BuildFloatSource(
                                _wrapZoneImagesOverride ?? newImages,
                                _wrapZoneShapesOverride ?? newShapes);
                            ResolveInlineImageBand(
                                imageZoneSource, ref contentYPt, boxWpt, boxHpt,
                                textXPt, textWidthPt, pageBottomPt,
                                out float bandLeftPt, out float bandRightPt, newPages, pageIdx);

                            // Перенос на новую страницу, если не влезает в остаток.
                            float available = pageBottomPt - contentYPt;
                            bool atPageTop = contentYPt <= pageYPt + mt + 0.5f;
                            bool overflowsPage = boxHpt > available && !atPageTop;
                            bool previewSelected = _imageOverflowPreviewMode
                                && ReferenceEquals(imageBlock, _selectedImage);

                            // Во время драга страница картинки заморожена как на момент
                            // нажатия: она не уходит на следующую страницу и не
                            // возвращается на предыдущую, пока кнопка не отпущена.
                            bool doTransfer = previewSelected
                                ? _imagePreviewStartTransferred && !atPageTop
                                : overflowsPage;

                            if (previewSelected && overflowsPage && !doTransfer)
                            {
                                // Предпросмотр: не влезает, но остаётся на месте,
                                // выходит за нижний край листа и рисуется серой
                                // (см. _paintImageDrawOverflow).
                                _imageOverflowPreviewBlock = imageBlock;
                            }

                            if (doTransfer)
                            {
                                pageYPt = pageYPt + pageHeightPt + PageGapPt;
                                (mt, mb) = PagePaddingForPage(pageIdx + 1, baseMt, baseMb);
                                pageBottomPt = pageYPt + pageHeightPt - mb;
                                contentYPt = pageYPt + mt;
                                pageIdx++;
                                newPages.Add(new PageRect(pageYPt, pageWidthPt, pageHeightPt, pageXPt, mt, ml, mb));
                                newInlineTransferred.Add(imageBlock);

                                // На новой странице зоны обтекания другие — полосу
                                // ищем заново от нового верха.
                                ResolveInlineImageBand(
                                    imageZoneSource, ref contentYPt, boxWpt, boxHpt,
                                    textXPt, textWidthPt, pageBottomPt,
                                    out bandLeftPt, out bandRightPt, newPages, pageIdx);
                            }

                            // Горизонтальное выравнивание бокса картинки внутри свободной
                            // полосы: без обтекающих соседей это вся текстовая колонка.
                            float bandWidthPt = bandRightPt - bandLeftPt;
                            float boxXPt = bandLeftPt;
                            float slackPt = bandWidthPt - boxWpt;
                            if (slackPt > 0f)
                            {
                                boxXPt = imageBlock.Alignment switch
                                {
                                    Models.Styles.TextAlignment.Center => bandLeftPt + slackPt / 2f,
                                    Models.Styles.TextAlignment.Right => bandLeftPt + slackPt,
                                    _ => bandLeftPt
                                };
                            }

                            // ImageEntry хранит неповёрнутый прямоугольник, центрированный
                            // в AABB: рендер поворачивает вокруг центра этого прямоугольника,
                            // поэтому картинка остаётся внутри выделенного ей бокса.
                            float imgXPt = boxXPt + (boxWpt - imgWpt) / 2f;
                            float imgYPt = contentYPt + (boxHpt - imgHpt) / 2f;

                            newImages.Add(new ImageEntry(imageBlock, imgYPt, imgXPt, imgWpt, imgHpt, pageIdx));
                            contentYPt += boxHpt;
                        }
                        else if (imageBlock.PinnedPage > 0)
                        {
                            // Привязанная к странице: позицию считаем позже, когда станут
                            // известны все страницы (её может ещё не существовать).
                            pinnedImages.Add(imageBlock);
                        }
                        else
                        {
                            // Картинка из Word, обтекаемая и отсчитанная от абзаца, не
                            // помещается под ним до низа листа. Word в этом случае уносит
                            // абзац вместе с картинкой на следующий лист, а не оставляет
                            // её свисать за нижнее поле; цепочка «не отрывать от
                            // следующего» над абзацем уезжает с ним (KeepWithNextChainHeight).
                            if (contentYPt > pageYPt + mt + 0.5f)
                            {
                                float imageFloatsNeedPt = AnchoredFloatsNeedPt(blocks, bi);
                                if (imageFloatsNeedPt > 0f
                                    && contentYPt + imageFloatsNeedPt > pageBottomPt
                                    && imageFloatsNeedPt <= pageBottomPt - (pageYPt + mt))
                                {
                                    pageYPt = pageYPt + pageHeightPt + PageGapPt;
                                    (mt, mb) = PagePaddingForPage(pageIdx + 1, baseMt, baseMb);
                                    pageBottomPt = pageYPt + pageHeightPt - mb;
                                    contentYPt = pageYPt + mt;
                                    pageIdx++;
                                    newPages.Add(new PageRect(pageYPt, pageWidthPt, pageHeightPt, pageXPt, mt, ml, mb));
                                }
                            }

                            // Плавающая: позиция по смещению относительно области страницы.
                            // Смещение приводится к листу чтения тем же множителем, что
                            // и размер: поля ужаты, лист уже, и печатное смещение уводило
                            // картинку за обрез.
                            // У картинки из Word точка отсчёта своя: лист, поля или верх её
                            // абзаца — блок картинки стоит в потоке прямо перед ним, и поток
                            // сейчас как раз у его верха. Смещения прибавляются к ней так же.
                            float floatOriginXPt = pageXPt + ml;
                            float floatOriginYPt = pageYPt + mt;
                            if (imageBlock.AnchorPosition is { } imageAnchor)
                            {
                                (floatOriginXPt, floatOriginYPt) = imageAnchor.ResolveOrigin(
                                    imgWpt, imgHpt,
                                    textXPt, textWidthPt, pageXPt, pageWidthPt,
                                    pageYPt, pageHeightPt, mt, mb, contentYPt);
                            }

                            float fx = floatOriginXPt + ReadingOffsetXPt(imageBlock.OffsetXPt);
                            float fy = floatOriginYPt + ReadingOffsetYPt(imageBlock.OffsetYPt);

                            // Проходы сходимости обтекания обязаны видеть картинку ТАМ ЖЕ,
                            // где по ней построены зоны, то есть на позиции первого прохода.
                            //
                            // Иначе получается петля: обтекание вытесняет текст вниз →
                            // поток над картинкой становится выше → на следующем проходе
                            // картинка встаёт относительно уже ДРУГОЙ страницы и уезжает
                            // на неё целиком → зоны, посчитанные по прошлому положению,
                            // остаются на прежнем месте → текст обтекает пустоту, а
                            // картинка стоит там, где текста нет. Раскладка при этом не
                            // сходится и скачет между проходами по чётности итерации.
                            //
                            // Во время перетаскивания проход всего один, картинка
                            // пересчитывается как обычно и следует за мышью.
                            //
                            // Картинка из Word, отсчитанная от своего абзаца, сюда не
                            // относится: её место задаёт абзац, а не страница. Замороженная,
                            // она оставалась там, где абзац стоял в первом проходе, и после
                            // того как обтекание выше раздвигало текст, оказывалась над
                            // чужими строками (см. FollowsAnchorParagraph).
                            ImageEntry? frozen = null;
                            if (_wrapZoneImagesOverride is { } frozenImages
                                && !FollowsAnchorParagraph(imageBlock, imageBlock.AnchorPosition))
                            {
                                foreach (var fe in frozenImages)
                                {
                                    if (!ReferenceEquals(fe.Block, imageBlock)) continue;
                                    frozen = fe;
                                    break;
                                }
                            }

                            if (frozen is { } fz)
                            {
                                var frozenSheet = newPages[Math.Clamp(
                                    fz.PageIndex, 0, Math.Max(0, newPages.Count - 1))];
                                var (fzX, fzY, fzW, fzH) = FitFloatingToSheet(
                                    fz.XPt, fz.Ypt, imgWpt, imgHpt, imageBlock.RotationDeg,
                                    frozenSheet.PadLeftPt, frozenSheet.Ypt,
                                    frozenSheet.WidthPt, frozenSheet.HeightPt);

                                newImages.Add(new ImageEntry(
                                    imageBlock, fzY, fzX, fzW, fzH, fz.PageIndex));
                            }
                            else
                            {
                                // Страницу определяем СРАЗУ, а не пересчётом в конце прохода.
                                // Зоны обтекания строятся по ходу дела и берут страницу из
                                // записи: если она там ещё «страница блока в потоке», а к концу
                                // прохода станет другой, картинка рисуется на одной странице,
                                // а текст сдвигает на другой — ровно то, чего быть не должно.
                                int floatPageIdx = ResolveFloatingObjectPage(
                                    fx, fy, imgWpt, imgHpt, newPages, pageIdx);

                                // В книге объект загоняется в лист: он меньше печатного,
                                // и картинка, стоявшая у края бумаги, иначе уходит за
                                // обрез или наезжает на соседнее содержимое.
                                var floatSheet = newPages[Math.Clamp(
                                    floatPageIdx, 0, Math.Max(0, newPages.Count - 1))];
                                var (ffX, ffY, ffW, ffH) = FitFloatingToSheet(
                                    fx, fy, imgWpt, imgHpt, imageBlock.RotationDeg,
                                    floatSheet.PadLeftPt, floatSheet.Ypt,
                                    floatSheet.WidthPt, floatSheet.HeightPt);

                                // Лист с переплётом справа показ сдвинет влево целиком; объект
                                // от края листа или от бокового поля встаёт с поправкой.
                                ffX += LayoutGutterCompensationPt(imageBlock.AnchorPosition, floatPageIdx);

                                // Соседей по листу объект не знает — знание приходит
                                // отсюда: он уступает уже размещённым и отходит вниз.
                                (ffX, ffY) = AvoidReadingOverlap(
                                    ffX, ffY, ffW, ffH, imageBlock.RotationDeg,
                                    floatPageIdx, newPages, newImages, newShapes, newTables,
                                    imageBlock);

                                newImages.Add(new ImageEntry(
                                    imageBlock, ffY, ffX, ffW, ffH, floatPageIdx));
                            }
                        }
                    }
                    continue;
                }

                if (block is not ParagraphBlock paraBlock) continue;

                if (!pvmByBlock.TryGetValue(paraBlock, out var pvm)) continue;

                Rendering.ListMarkerInfo? paraMarker =
                    markerMap.TryGetValue(paraBlock, out var _mi) ? _mi : null;
                // Кладём текст маркера в модель ДО построения раскладки — BuildLayout меряет его
                // ширину и отодвигает текст первой строки на зазор после цифры.
                if (paraBlock.ListProperties is not null)
                {
                    paraBlock.ListProperties.ComputedMarkerText = paraMarker?.Text;
                    MigrateCorruptListMarker(paraBlock, textWidthPt);
                }

                // Интервал между абзацами — больший из «после» и «до», а не сумма: часть
                // интервала до, уже пройденная интервалом после предыдущего абзаца, не
                // добавляется второй раз. Только если абзац встаёт прямо за предыдущим:
                // после таблицы, картинки или перехода на новый лист складывать нечего.
                float collapseAppliedPt = 0f;
                if (collapseSpacing && !float.IsNaN(collapseEndYPt)
                    && Math.Abs(contentYPt - collapseEndYPt) < 0.01f)
                {
                    float collapseBeforePt = CollapsibleSpaceBeforePt(GetOrBuildLayout(pvm, textWidthPt));
                    collapseAppliedPt = Math.Min(collapsePrevAfterPt, collapseBeforePt);
                    contentYPt -= collapseAppliedPt;
                }
                collapseEndYPt = float.NaN;

                // Правила Word, по которым абзац начинает новую страницу, ещё не дойдя
                // до её низа. На верху листа оба ничего не делают: абзац уже там.
                //
                // «С новой страницы» (w:pageBreakBefore) — абзац всегда открывает лист.
                // Так устроены, например, заголовки частей: у них это записано в стиле,
                // и без этого правила часть начиналась посреди страницы с хвостом
                // предыдущей.
                //
                // «Не отрывать от следующего» (w:keepNext) — абзац стоит на одной
                // странице с началом следующего. Заголовок не остаётся один внизу листа,
                // а переезжает вместе с текстом, который он озаглавливает. Цепочка
                // таких абзацев переезжает целиком. Если цепочка не помещается даже на
                // пустой лист, правило не соблюсти — абзац остаётся где был, как в Word.
                // Исключение — цепочка, которую разорвать законно негде (см. ниже).
                //
                // Интервал перед абзацем на верху листа. Абзац, который на новый лист
                // привела сама вёрстка (не поместился или ушёл за следующим), Word ставит
                // вплотную к верхнему полю: интервал «до» у него не отсчитывается. Абзац,
                // открывающий лист по своему свойству «с новой страницы» или после
                // вставленного разрыва страницы, интервал сохраняет.
                bool spaceBeforeDropped = false;

                bool paraAtPageTop = contentYPt <= pageYPt + mt + 0.5f;
                if (!paraAtPageTop)
                {
                    bool breakBefore = paraBlock.Properties.PageBreakBefore;

                    if (!breakBefore && paraBlock.Properties.KeepWithNext)
                    {
                        float chainPt = KeepWithNextChainHeight(blocks, bi, pvmByBlock, textWidthPt)
                            - collapseAppliedPt;
                        float pageTextPt = pageBottomPt - (pageYPt + mt);
                        bool chainOverflowsPage = chainPt > pageBottomPt - contentYPt;
                        breakBefore = chainOverflowsPage && chainPt <= pageTextPt;

                        // Цепочка длиннее пустого листа, но разорвать её законно негде:
                        // все её абзацы короче четырёх строк (запрет висячих строк их не
                        // делит) или помечены «не разрывать», а лист переполняет начало
                        // замыкающего блока — высокая картинка, неделимая строка таблицы.
                        // Word в этом случае всё равно уносит начало цепочки на новый
                        // лист: заголовок и подпись к картинке выше страницы стоят у него
                        // на отдельном листе, а сама картинка — на следующем. Решение
                        // принимает только первый абзац цепочки: её продолжение,
                        // оказавшись на верху листа вслед за ним, правило уже не дробит.
                        if (chainOverflowsPage && !breakBefore
                            && !ContinuesKeepWithNextChain(blocks, bi))
                        {
                            var (chainHeadPt, chainHeadRigid) = KeepWithNextChainHead(
                                blocks, bi, pvmByBlock, textWidthPt);
                            breakBefore = chainHeadRigid
                                && chainHeadPt - collapseAppliedPt <= pageTextPt;
                        }

                        spaceBeforeDropped = breakBefore;
                    }

                    if (breakBefore)
                    {
                        pageYPt = pageYPt + pageHeightPt + PageGapPt;
                        (mt, mb) = PagePaddingForPage(pageIdx + 1, baseMt, baseMb);
                        pageBottomPt = pageYPt + pageHeightPt - mb;
                        contentYPt = pageYPt + mt;
                        pageIdx++;
                        newPages.Add(new PageRect(pageYPt, pageWidthPt, pageHeightPt, pageXPt, mt, ml, mb));
                    }
                }

                // Обтекание текстом: если рядом с вертикалью параграфа лежит плавающая
                // картинка в режиме Square/Tight — строим раскладку с зонами исключения,
                // строки обходят габарит картинки. Такой лейаут не кешируется.
                var zoneSource = BuildFloatSource(
                    _wrapZoneImagesOverride ?? newImages,
                    _wrapZoneShapesOverride ?? newShapes);

                // Таблицы с обтеканием, уже встреченные в потоке: абзац обходит их так же.
                AppendFloatingTables(zoneSource, newTables);

                // Верх первой строки абзаца в координатах документа. Зоны обтекания
                // должны считаться именно от него, а не от contentYPt:
                //   contentYPt — позиция до разбивки на страницы и без SpaceBefore;
                //   цикл по строкам ниже может перенести абзац на следующую страницу
                //   (contentYPt = pageYPt + mt), и тогда зоны, посчитанные от старого
                //   значения, описывают полосу этажом выше реального места абзаца.
                // Разрыв предсказывается тем же условием, что и в цикле, по раскладке
                // без зон: высота строки задаётся шрифтом и от ширины полосы не зависит.
                var probeLayout = GetOrBuildLayout(pvm, textWidthPt);
                float paraStartYPt = contentYPt + probeLayout.SpaceBeforePt
                    - (spaceBeforeDropped ? CollapsibleSpaceBeforePt(probeLayout) : 0f);
                float probeFirstLineHPt = probeLayout.Lines.Count > 0
                    ? probeLayout.Lines[0].Height
                    : FallbackLinePt;

                // Сколько страниц абзац перешагнул ещё до первой строки: от этого
                // зависит, какая граница страницы актуальна для его разрывов.
                int paraPageAdvance = 0;

                if (paraStartYPt + probeFirstLineHPt > pageBottomPt
                    && paraStartYPt > pageYPt + mt)
                {
                    paraStartYPt = pageYPt + pageHeightPt + PageGapPt
                                 + mt + PageContinuationTopPadPt;
                    paraPageAdvance = 1;
                }

                float pageStepPt = pageHeightPt + PageGapPt;

                // Итеративная сходимость: где абзац окажется на самом деле, известно
                // только из прошлого прохода. Предсказание выше считается по раскладке
                // БЕЗ зон и про вытеснение обтеканием ничего не знает.
                if (_wrapAnchorIn.TryGetValue(paraBlock, out float anchoredTopPt))
                {
                    // Замер прошлого прохода применяется БЕЗ порогов. Это единственная
                    // измеренная величина: предсказание считается по раскладке без зон,
                    // и любое расхождение — хоть в один пункт — уводит границы страниц,
                    // которые рендерер получает в WrapPageContext. Он тогда считает, что
                    // строка ещё на своём листе, обтекает по ней картинку, а пагинация
                    // кладёт эту строку на следующий лист вместе с готовым коридором.
                    // Пороги здесь и оставляли ту щель, в которую пролезал дефект.
                    //
                    // Сходимость это гасит: проходов до четырёх, и как только замер
                    // совпадает с поданным якорем, раскладка объявляется устоявшейся.
                    paraStartYPt = anchoredTopPt;
                    paraPageAdvance = PageAdvanceOf(anchoredTopPt, pageYPt + mt, pageStepPt);
                }

                // newPages нужен зонам, чтобы обрезать габарит картинки её собственной
                // страницей: свисающая за нижний край картинка не должна двигать текст
                // на следующей странице, где её не видно.
                //
                // Окно поиска обтекаемых объектов: собственная высота абзаца плюс шаг
                // страницы. Больше него абзац занять не может даже когда хвост уезжает
                // на следующий лист, а зоны, лежащие дальше, ему не принадлежат.
                float wrapLookAheadPt = probeLayout.TotalHeightPt + pageStepPt;

                // Страницы, чьи картинки этот абзац вправе обтекать: своя и следующая,
                // на которую может уйти хвост. Своя берётся из ЗАМЕРА прошлого прохода,
                // если он есть: предсказание считается по раскладке без зон, вытеснения
                // не знает и у абзаца, уехавшего вниз, показывает лист выше. Именно так
                // абзац на втором листе получал зону картинки с первого и приходил туда
                // уже разрезанным полосой — коридор посреди текста, рядом с которым
                // никакой картинки нет.
                int paraFirstPage = pageIdx + (_wrapAnchorIn.TryGetValue(paraBlock, out float anchorForPage)
                    ? PageAdvanceOf(anchorForPage, pageYPt + mt, pageStepPt)
                    : paraPageAdvance);

                var wrapZones = ComputeWrapZones(
                    zoneSource, paraStartYPt, textXPt, textWidthPt, newPages,
                    pageIndex: paraFirstPage,
                    lookAheadPt: wrapLookAheadPt,
                    maxPageIndex: paraFirstPage + 1);

                // Геометрия страниц для абзаца: если он не поместится целиком,
                // строки после разрыва должны сравниваться с зонами от своего
                // настоящего места на следующей странице, а не от накопленной
                // высоты внутри абзаца.
                var wrapPages = new Rendering.SKTextRenderer.WrapPageContext(
                    ParaStartYPt: paraStartYPt,
                    PageBottomPt: pageBottomPt + paraPageAdvance * pageStepPt,
                    NextPageTopPt: pageYPt + pageStepPt + mt + PageContinuationTopPadPt
                                 + paraPageAdvance * pageStepPt,
                    PageStepPt: pageStepPt);

                var layout = wrapZones is null
                    ? probeLayout
                    : BuildWrappedLayout(pvm, textWidthPt, wrapZones, wrapPages);

                // Якорь перед таблицей: пустой параграф, следующий блок — таблица.
                // Но если предыдущий блок тоже таблица, это разделитель между двумя
                // таблицами, и он идёт ветке ниже — якорем ПОСЛЕ верхней. Разница в том,
                // что здесь якорь занимает строку (contentYPt += FallbackLinePt), а там нет:
                // абзац между таблицами подходит под оба условия, попадал в это, первое, и
                // раздвигал таблицы на пустую строку, убрать которую было нечем.
                // Таблица, скрытая свёрнутым заголовком, соседом не считается: её нет на листе.
                bool isBeforeTableAnchor = string.IsNullOrEmpty(pvm.PlainText)
                    && bi + 1 < blocks.Count && blocks[bi + 1] is TableBlock
                    && !IsFloatingTable(blocks[bi + 1])
                    && !IsCollapsedBlock(blocks[bi + 1])
                    && !(bi > 0 && blocks[bi - 1] is TableBlock && !IsCollapsedBlock(blocks[bi - 1]));
                if (isBeforeTableAnchor)
                {
                    float anchorXPt = textXPt + (float)((TableBlock)blocks[bi + 1]).LeftIndentPt;
                    // Сдвигаем каретку чуть левее таблицы чтобы она не перекрывалась рамкой.
                    newLayouts.Add(new ParaLayout(
                        pvm, layout, contentYPt, FallbackLinePt,
                        pageIdx, 0, 0,
                        AbsXPt: anchorXPt - AnchorMarginPt));

                    // Якорь занимает строку так же, как любой пустой параграф. Без сдвига
                    // он был нулевой высоты, и таблица начиналась вплотную к предыдущему
                    // блоку: у двух таблиц подряд между ними стоит один общий якорь, и
                    // отступ между ними пропадал совсем.
                    contentYPt += FallbackLinePt;
                    continue;
                }

                // Якорь после таблицы: пустой параграф, предыдущий блок — таблица.
                bool isAfterTableAnchor = string.IsNullOrEmpty(pvm.PlainText)
                    && bi > 0 && blocks[bi - 1] is TableBlock
                    && !IsFloatingTable(blocks[bi - 1])
                    && !IsCollapsedBlock(blocks[bi - 1]);
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
                // contentYPt уже абсолютная координата документа (её начальное значение —
                // pageYPt + mt). Прибавка pageYPt здесь удваивала верх страницы: пустой
                // абзац уезжал вниз на целый лист, а вместе с ним и каретка, которая на
                // нём стоит. Прокрутка к каретке уводила вид в пустоту под документом —
                // над первым листом оставалось пол-экрана серого поля.
                if (layout.Lines.Count == 0)
                {
                    newLayouts.Add(new ParaLayout(
                        pvm, layout,
                        contentYPt, FallbackLinePt,
                        pageIdx, 0, 0,
                        AbsXPt: textXPt, Marker: paraMarker));
                    contentYPt += FallbackLinePt;
                    continue;
                }

                // Рамка абзаца своё место сверху занимает и на верху листа: снимается
                // только сам интервал.
                contentYPt += layout.SpaceBeforePt
                    - (spaceBeforeDropped ? CollapsibleSpaceBeforePt(layout) : 0f);
                int lineFrom = 0;
                float lineGroupYPt = contentYPt;

                // Реальная позиция ПЕРВОЙ строки абзаца (без её собственного вытеснения
                // под картинку) — вход для итеративной сходимости зон. lineGroupYPt в
                // конце цикла относится к последнему куску разрезанного абзаца и не годится.
                float firstLineTopPt = float.NaN;

                // Правила разрыва страницы как в Word. Запрет висячих строк там включён
                // по умолчанию и работает на каждый абзац: одна строка абзаца не остаётся
                // внизу страницы и одна не уезжает на следующую. «Не разрывать абзац»
                // приходит из свойств абзаца.
                bool keepParagraphTogether = paraBlock.Properties.KeepTogether;
                bool paragraphMovedWhole = false;
                bool lastLinePulled = false;

                for (int li = 0; li < layout.Lines.Count; li++)
                {
                    var line = layout.Lines[li];
                    bool isLast = li == layout.Lines.Count - 1;

                    // Зазор вытеснения строки под обтекаемый объект входит в высоту
                    // параграфа — без него следующий блок наезжал бы на текст.
                    // Если гэп у первой строки слайса — сдвигаем и якорь слайса:
                    // рендер вычитает yBase = Lines[LineFrom].Y, в котором гэп уже учтён.
                    contentYPt += line.WrapExtraTopPt;
                    if (li == lineFrom) lineGroupYPt = contentYPt;
                    if (li == 0) firstLineTopPt = contentYPt - line.WrapExtraTopPt;

                    if (contentYPt + line.Height > pageBottomPt
                        && contentYPt > pageYPt + mt)
                    {
                        int linesOnPage = li - lineFrom;
                        float pageTextHeightPt = pageBottomPt - (pageYPt + mt) - PageContinuationTopPadPt;

                        // Абзац целиком уходит на следующую страницу: он либо помечен
                        // «не разрывать», либо оставил бы внизу единственную строку.
                        // Повторно не переносим — на новой странице места уже не больше,
                        // и абзац зациклился бы между листами.
                        bool moveWholeParagraph = !paragraphMovedWhole
                            && lineFrom == 0
                            && layout.Lines.Count > 1
                            && (keepParagraphTogether
                                ? layout.TotalHeightPt <= pageTextHeightPt
                                : linesOnPage == 1
                                  && layout.Lines[0].Height + layout.Lines[1].Height <= pageTextHeightPt);

                        // На следующую страницу уезжала бы одна последняя строка —
                        // забираем вместе с ней предыдущую.
                        bool pullPreviousLine = !moveWholeParagraph
                            && !lastLinePulled
                            && isLast
                            && linesOnPage >= 2
                            && layout.Lines[li - 1].Height + line.Height <= pageTextHeightPt;

                        if (moveWholeParagraph || pullPreviousLine)
                        {
                            int sliceEnd = moveWholeParagraph ? lineFrom : li - 1;

                            if (sliceEnd > lineFrom)
                            {
                                var lastKeptLine = layout.Lines[sliceEnd];
                                float sliceBottomPt = contentYPt
                                    - lastKeptLine.Height - lastKeptLine.WrapExtraTopPt;

                                newLayouts.Add(new ParaLayout(
                                    pvm, layout, lineGroupYPt,
                                    sliceBottomPt - lineGroupYPt,
                                    pageIdx, lineFrom, sliceEnd,
                                    AbsXPt: absXPt, Marker: paraMarker));
                            }

                            pageYPt = pageYPt + pageHeightPt + PageGapPt;
                            (mt, mb) = PagePaddingForPage(pageIdx + 1, baseMt, baseMb);
                            pageBottomPt = pageYPt + pageHeightPt - mb;
                            contentYPt = pageYPt + mt + PageContinuationTopPadPt;
                            pageIdx++;
                            newPages.Add(new PageRect(pageYPt, pageWidthPt, pageHeightPt, pageXPt, mt, ml, mb));

                            lineFrom = sliceEnd;
                            lineGroupYPt = contentYPt;

                            if (moveWholeParagraph) paragraphMovedWhole = true;
                            else lastLinePulled = true;

                            // Строки, уехавшие на новую страницу, раскладываются заново
                            // от её верха: их прежние позиции считались для прошлого листа.
                            li = sliceEnd - 1;
                            continue;
                        }

                        if (li > lineFrom)
                        {
                            newLayouts.Add(new ParaLayout(
                                pvm, layout, lineGroupYPt,
                                contentYPt - lineGroupYPt,
                                pageIdx, lineFrom, li,
                                AbsXPt: absXPt, Marker: paraMarker));
                        }

                        pageYPt = pageYPt + pageHeightPt + PageGapPt;
                        (mt, mb) = PagePaddingForPage(pageIdx + 1, baseMt, baseMb);
                        pageBottomPt = pageYPt + pageHeightPt - mb;
                        contentYPt = pageYPt + mt;
                        pageIdx++;
                        newPages.Add(new PageRect(pageYPt, pageWidthPt, pageHeightPt, pageXPt, mt, ml, mb));

                        lineFrom = li;
                        // Небольшой отступ чтобы первая строка продолжения не прилипала к верхнему полю.
                        contentYPt += PageContinuationTopPadPt;
                        lineGroupYPt = contentYPt;
                    }

                    // Переброс по ФАКТИЧЕСКОЙ позиции. Если строка реально легла на
                    // картинку (расчёт обтекания в вёрстке недожал на стыке страниц —
                    // строку рассчитали для одной страницы, а пагинация положила на
                    // другую, где картинка), выбрасываем её и всё продолжение абзаца под
                    // низ картинки новым слайсом. Срабатывает только при реальном
                    // наложении: строки, честно обтёкшие картинку сбоку, сюда не попадают.
                    {
                        float lnTop = contentYPt;
                        float lnBot = contentYPt + line.Height;
                        float throwToPt = float.NaN;
                        foreach (var ie in zoneSource)
                        {
                            if (ie.Block.WrapMode is not (WrapMode.Square or WrapMode.Tight)) continue;

                            // Только картинки ЭТОЙ страницы. Прямоугольники сравниваются
                            // в координатах документа, а они сквозные: картинка, свисающая
                            // за низ своей страницы, дотягивалась ими до строк следующей
                            // и перебрасывала их вниз — на второй странице появлялся провал
                            // от картинки, которой там не видно. Чем сильнее повёрнута
                            // картинка, тем дальше вниз уходил её габарит и тем заметнее это.
                            if (ie.PageIndex != pageIdx) continue;

                            // Габарит берётся ПОВЁРНУТЫЙ — тот же, по которому построена
                            // зона обтекания. Прежде здесь стоял прямоугольник картинки
                            // без учёта угла, и у повёрнутой картинки два прямоугольника
                            // расходились: при 270 градусах зона занимает по вертикали
                            // ширину картинки, а этот код считал по высоте. Строки, честно
                            // обтёкшие картинку сбоку, попадали в разницу и перебрасывались
                            // под низ несуществующего габарита — на месте картинки
                            // оставалась пустота, а под ней шли разорванные строки.
                            //
                            // У объекта из Word габарит — рамка с полями обрамления, как и
                            // у его зоны (FloatingObjectBox).
                            var (throwBoxW, throwBoxH) = FloatingObjectBox.Of(ie.Block, ie.WidthPt, ie.HeightPt);
                            float throwCx = ie.XPt + ie.WidthPt / 2f;
                            float throwCy = ie.Ypt + ie.HeightPt / 2f;

                            float iT = throwCy - throwBoxH / 2f;
                            float iB = throwCy + throwBoxH / 2f;
                            float iL = throwCx - throwBoxW / 2f;
                            float iR = throwCx + throwBoxW / 2f;
                            if (lnBot <= iT + 0.5f || lnTop >= iB - 0.5f) continue;

                            // Строка, разорванная объектом, занимает несколько отрезков:
                            // её ширина включает прыжок через картинку, и проверка «от
                            // начала до конца строки» всегда видела бы наложение. Каждый
                            // отрезок проверяется отдельно.
                            bool overlaps = false;
                            if (line.HasWrapFragments)
                            {
                                foreach (var fragment in line.WrapFragments)
                                {
                                    float fL = absXPt + fragment.LeftPt;
                                    float fR = fL + fragment.WidthPt;
                                    if (fR > iL + 0.5f && fL < iR - 0.5f) { overlaps = true; break; }
                                }
                            }
                            else
                            {
                                float lnLeft = absXPt + line.WrapLeftPt;
                                float lnRight = lnLeft + line.TextWidth;
                                overlaps = lnRight > iL + 0.5f && lnLeft < iR - 0.5f;
                            }

                            if (overlaps && (float.IsNaN(throwToPt) || iB > throwToPt))
                                throwToPt = iB;
                        }

                        if (!float.IsNaN(throwToPt) && throwToPt + WrapThrowGapPt > contentYPt + 0.5f)
                        {
                            // Закрываем текущий слайс до этой строки — как при разрыве.
                            if (li > lineFrom)
                            {
                                newLayouts.Add(new ParaLayout(
                                    pvm, layout, lineGroupYPt,
                                    contentYPt - lineGroupYPt,
                                    pageIdx, lineFrom, li,
                                    AbsXPt: absXPt, Marker: paraMarker));
                            }

                            contentYPt = throwToPt + WrapThrowGapPt;

                            // Переброс мог увести строку за низ страницы — тогда обычный
                            // разрыв на следующую страницу.
                            if (contentYPt + line.Height > pageBottomPt
                                && contentYPt > pageYPt + mt)
                            {
                                pageYPt = pageYPt + pageHeightPt + PageGapPt;
                                (mt, mb) = PagePaddingForPage(pageIdx + 1, baseMt, baseMb);
                                pageBottomPt = pageYPt + pageHeightPt - mb;
                                contentYPt = pageYPt + mt + PageContinuationTopPadPt;
                                pageIdx++;
                                newPages.Add(new PageRect(pageYPt, pageWidthPt, pageHeightPt, pageXPt, mt, ml, mb));
                            }

                            lineFrom = li;
                            lineGroupYPt = contentYPt;
                        }
                    }

                    contentYPt += line.Height;
                    if (isLast) contentYPt += layout.SpaceAfterPt;
                }

                // Замер реальной позиции первой строки — вход для следующей итерации
                // сходимости зон. Только для обтекаемых абзацев: остальным якорь не нужен.
                if (wrapZones is not null && !float.IsNaN(firstLineTopPt))
                    _wrapAnchorOut[paraBlock] = firstLineTopPt;

                newLayouts.Add(new ParaLayout(
                    pvm, layout, lineGroupYPt,
                    contentYPt - lineGroupYPt,
                    pageIdx, lineFrom, layout.Lines.Count,
                    AbsXPt: absXPt, Marker: paraMarker));

                collapsePrevAfterPt = CollapsibleSpaceAfterPt(layout);
                collapseEndYPt = contentYPt;
            }

            // Сверка вёрстки с внешним редактором: лист, строки и незанятое место
            // внизу страниц. Запись обновляется, когда меняется что-то из измеряемого.
            // Частичный проход видит лишь кусок документа — сверять его не с чем.
            if (newLayouts.Count > 0 && !partialPass)
                LogPaginationProbe(newLayouts, newPages, pageWidthPt, pageHeightPt, ml, mt, mr, mb, textWidthPt);

            // ── Картинки с жёсткой привязкой к странице ──────────────────────
            // Документ обязан держать столько страниц, чтобы привязанная страница
            // существовала: удаление текста не утаскивает такую картинку выше, страницы
            // до неё просто остаются пустыми. Пустые листы достраиваются здесь, ПОСЛЕ
            // основного потока — только он знает, сколько страниц вышло по тексту.
            int maxPinnedPage = 0;
            foreach (var pinned in pinnedImages)
                if (pinned.PinnedPage > maxPinnedPage) maxPinnedPage = pinned.PinnedPage;
            foreach (var pinnedShape in pinnedShapes)
                if (pinnedShape.PinnedPage > maxPinnedPage) maxPinnedPage = pinnedShape.PinnedPage;

            while (newPages.Count < maxPinnedPage)
            {
                pageYPt = pageYPt + pageHeightPt + PageGapPt;
                (mt, mb) = PagePaddingForPage(newPages.Count, baseMt, baseMb);
                newPages.Add(new PageRect(pageYPt, pageWidthPt, pageHeightPt, pageXPt, mt, ml, mb));
            }

            // Позиция привязанной картинки отсчитывается от краёв ЕЁ страницы, а не от
            // страницы её места в потоке: в этом и смысл привязки.
            foreach (var pinned in pinnedImages)
            {
                int pinnedIdx = Math.Clamp(pinned.PinnedPage - 1, 0, Math.Max(0, newPages.Count - 1));
                if (pinnedIdx >= newPages.Count) continue;

                var pinnedPage = newPages[pinnedIdx];
                var (pinnedW, pinnedH) = ReadingImageSize(pinned);
                if (pinnedW <= 0f || pinnedH <= 0f) continue;

                var (pinX, pinY, pinW, pinH) = FitFloatingToSheet(
                    pinnedPage.PadLeftPt + pinnedPage.MarginLeftPt
                        + ReadingOffsetXPt(pinned.OffsetXPt),
                    pinnedPage.Ypt + pinnedPage.PadTopPt + ReadingOffsetYPt(pinned.OffsetYPt),
                    pinnedW, pinnedH, pinned.RotationDeg,
                    pinnedPage.PadLeftPt, pinnedPage.Ypt,
                    pinnedPage.WidthPt, pinnedPage.HeightPt);

                (pinX, pinY) = AvoidReadingOverlap(
                    pinX, pinY, pinW, pinH, pinned.RotationDeg,
                    pinnedIdx, newPages, newImages, newShapes, newTables, pinned);

                newImages.Add(new ImageEntry(pinned, pinY, pinX, pinW, pinH, pinnedIdx));
            }

            // Привязанные фигуры — по тому же правилу: отсчёт от краёв СВОЕЙ страницы.
            foreach (var pinnedShape in pinnedShapes)
            {
                int pinnedShapeIdx = Math.Clamp(
                    pinnedShape.PinnedPage - 1, 0, Math.Max(0, newPages.Count - 1));
                if (pinnedShapeIdx >= newPages.Count) continue;

                var pinnedShapePage = newPages[pinnedShapeIdx];
                var (pinnedShapeW, pinnedShapeH) = ReadingShapeSize(pinnedShape);
                var (pinShX, pinShY, pinShW, pinShH) = FitFloatingToSheet(
                    pinnedShapePage.PadLeftPt + pinnedShapePage.MarginLeftPt
                        + ReadingOffsetXPt(pinnedShape.OffsetXPt),
                    pinnedShapePage.Ypt + pinnedShapePage.PadTopPt
                        + ReadingOffsetYPt(pinnedShape.OffsetYPt),
                    pinnedShapeW, pinnedShapeH, pinnedShape.RotationDeg,
                    pinnedShapePage.PadLeftPt, pinnedShapePage.Ypt,
                    pinnedShapePage.WidthPt, pinnedShapePage.HeightPt);

                (pinShX, pinShY) = AvoidReadingOverlap(
                    pinShX, pinShY, pinShW, pinShH, pinnedShape.RotationDeg,
                    pinnedShapeIdx, newPages, newImages, newShapes, newTables, pinnedShape);

                newShapes.Add(new ShapeEntry(
                    pinnedShape, pinShY, pinShX, pinShW, pinShH, pinnedShapeIdx));
            }

            // Страницы, которые держат сами картинки. Перетащенная на следующий лист
            // картинка становится его содержимым — и лист обязан существовать, даже
            // если текста на него не хватило. Иначе выходила петля: текст без обтекания
            // умещался на одну страницу, вторая пропадала, картинке было некуда встать,
            // она возвращалась — и всё повторялось, отсюда дёрганье при перетаскивании.
            //
            // Петли здесь нет: положение картинки считается от её собственного якоря и
            // от числа страниц не зависит — зависимость односторонняя.
            float imagePageStepPt = pageHeightPt + PageGapPt;
            int neededPages = 0;
            foreach (var ie in newImages)
            {
                if (ie.Block.WrapMode == WrapMode.Inline || ie.InLine) continue;

                double rotRad = ie.Block.RotationDeg * Math.PI / 180.0;
                float boxH = ie.WidthPt * (float)Math.Abs(Math.Sin(rotRad))
                           + ie.HeightPt * (float)Math.Abs(Math.Cos(rotRad));
                float topPt = ie.Ypt + ie.HeightPt / 2f - boxH / 2f;

                // Номер листа, на который попадает верх картинки.
                int page = (int)Math.Floor((topPt - PageGapPt) / imagePageStepPt) + 1;
                if (page > neededPages) neededPages = page;
            }

            while (newPages.Count < neededPages)
            {
                pageYPt = pageYPt + pageHeightPt + PageGapPt;
                (mt, mb) = PagePaddingForPage(newPages.Count, baseMt, baseMb);
                newPages.Add(new PageRect(pageYPt, pageWidthPt, pageHeightPt, pageXPt, mt, ml, mb));
            }

            // Плавающая картинка без привязки могла быть перетащена за пределы страницы
            // своего блока — переопределяем её страницу по центру: рисоваться и
            // клиповаться она должна там, где реально находится, а не «под» чужим листом.
            for (int ii = 0; ii < newImages.Count; ii++)
            {
                var ie = newImages[ii];
                if (ie.Block.WrapMode == WrapMode.Inline) continue;
                if (ie.Block.PinnedPage > 0) continue;   // привязанная страницу не меняет

                // Страница уже определена при создании записи — здесь только уточняем её
                // для картинок, которым на тот момент не хватало страниц (документ вырос
                // по ходу прохода). Правило то же: страница по верхнему краю габарита.
                int resolvedPage = ResolveFloatingObjectPage(
                    ie.XPt, ie.Ypt, ie.WidthPt, ie.HeightPt, newPages, ie.PageIndex);

                if (resolvedPage != ie.PageIndex)
                    newImages[ii] = ie with { PageIndex = resolvedPage };
            }

            // Фигура так же могла быть перетащена за пределы страницы своего блока.
            // Правило то же, что и у картинки: страница по центру габарита, чтобы
            // фигура рисовалась и обрезалась тем листом, на котором её видно.
            for (int si = 0; si < newShapes.Count; si++)
            {
                var se = newShapes[si];
                if (se.Block.WrapMode == WrapMode.Inline) continue;
                if (se.Block.PinnedPage > 0) continue;   // привязанная страницу не меняет

                int resolvedShapePage = ResolveFloatingObjectPage(
                    se.XPt, se.Ypt, se.WidthPt, se.HeightPt, newPages, se.PageIndex);

                if (resolvedShapePage != se.PageIndex)
                    newShapes[si] = se with { PageIndex = resolvedShapePage };
            }

            // Картинки в строках текста: регистрируем после вёрстки абзацев — их позиция
            // известна только по готовым строкам.
            CollectInlineImageEntries(newLayouts, newImages);

            // Частичный проход не знает, сколько листов выйдет у всего документа.
            // Листы за его концом остаются пустыми, но остаются: иначе холст на время
            // жеста укоротился бы, и прокрутка прыгнула бы вверх.
            if (partialPass)
            {
                int keepPages;
                lock (_renderLock) keepPages = _pages.Count;

                while (newPages.Count < keepPages)
                {
                    pageYPt = pageYPt + pageHeightPt + PageGapPt;
                    (mt, mb) = PagePaddingForPage(newPages.Count, baseMt, baseMb);
                    newPages.Add(new PageRect(pageYPt, pageWidthPt, pageHeightPt, pageXPt, mt, ml, mb));
                }
            }

            float newCanvasH = pageYPt + pageHeightPt + PageGapPt;

            // Страницы рядом: высота канваса определяется числом визуальных рядов,
            // а не логическим столбиком страниц.
            if (_pagesPerRow > 1 && newPages.Count > 0)
            {
                int rows = (newPages.Count + _pagesPerRow - 1) / _pagesPerRow;
                newCanvasH = PageGapPt + rows * (newPages[0].HeightPt + PageGapPt);
            }

            // Лента: страницы склеены встык, и холст ровно такой, сколько занимает вся
            // склейка. Ни межстраничных зазоров, ни постраничных полей в этой высоте
            // нет — иначе внизу осталась бы пустая полоса высотой в поля всех страниц.
            //
            // Полоса каждой страницы меряется занятой высотой, а не полем листа:
            // пагинация оставляет внизу остаток, на который не влезла строка или
            // строка таблицы, и в ленте этот остаток был бы разрывом — таблица,
            // перенесённая на следующий лист, расходилась на два куска.
            _passRibbonBandHeights = ReadingRibbon && newPages.Count > 0
                ? BuildRibbonBandHeights(newPages, newLayouts, newTables, newImages, newShapes)
                : Array.Empty<float>();

            if (_passRibbonBandHeights.Length > 0)
                newCanvasH = ReadingRibbonHeightPt(newPages, _passRibbonBandHeights);

            // Высота холста во время частичного прохода не уменьшается — по той же причине.
            if (partialPass)
                newCanvasH = Math.Max(newCanvasH, _canvasHeightPt);

            // Результат прохода: промежуточные проходы сходимости обтекания его только
            // копят, наружу уходит последний. Иначе рендер успевает поймать промежуточный
            // кадр — в первом проходе абзац ещё не знает про картинку и верстается во всю
            // ширину, и первая строка мигает полной шириной на каждой пересборке.
            _passLayouts = newLayouts;
            _passPages = newPages;
            _passTables = newTables;

            // Стороны переплёта по этому проходу — для мест плавающих объектов в
            // следующем (см. LayoutGutterCompensationPt).
            RememberGutterSides(newLayouts, newPages.Count);
            _passImages = newImages;
            _passShapes = newShapes;
            _passInlineTransferred = newInlineTransferred;
            _passBreakMarks = newBreakMarks;
            _passCanvasHeightPt = newCanvasH;

            if (_publishPassResults) PublishPassResults();
        }

        // Зазор до нижнего поля, который раскладка таблицы оставляет под строкой, не
        // стоящей вверху листа. Им пользуются и проход раскладки, и цепочка «не отрывать
        // от следующего»: иначе цепочка сочла бы строку поместившейся, а проход увёл бы
        // её на следующий лист. У Word такого зазора нет — строка встаёт вплотную к
        // нижнему полю, поэтому он нулевой.
        private const float TableRowEndGapPt = 0f;

        /// <summary>
        /// Допуск при сравнении строки ячейки с местом разреза страницы, pt. Место разреза
        /// и низ строки получаются одним и тем же сложением в разном порядке и расходятся
        /// на ошибку округления.
        /// </summary>
        private const float CellCutTolerancePt = 0.01f;

        /// <summary>
        /// Сколько места нужно началу строки таблицы, которую можно разрывать между
        /// страницами: до низа первой строки текста в самой высокой её ячейке — по ней
        /// же проход раскладки ищет место разреза. Строка без текста берётся целиком.
        /// </summary>
        private static float SplittableRowLeadHeightPt(SKTableRowLayout row)
        {
            SKTableCellLayout? tallest = null;
            foreach (var cell in row.Cells)
            {
                if (cell.IsRotated) continue;
                if (tallest is null || cell.ContentHeightPt > tallest.ContentHeightPt)
                    tallest = cell;
            }

            if (tallest is null) return row.HeightPt;

            foreach (var para in tallest.Paragraphs)
            {
                var lines = para.Layout.Lines;
                if (lines.Count == 0) continue;

                // Сколько строк первого абзаца должно встать на страницу, чтобы проход
                // раскладки разрезал строку таблицы здесь же: запрет висячих строк не
                // оставит внизу одну строку, а абзац в две или три строки не рвёт вовсе.
                int leadLines = lines.Count <= 3 ? lines.Count : 2;
                var lastLeadLine = lines[leadLines - 1];

                float leadPt = tallest.PadTopPt + tallest.TopInsetPt
                    + para.Ypt + para.Layout.SpaceBeforePt
                    + lastLeadLine.Y + lastLeadLine.Height
                    + tallest.PadBottomPt + tallest.BottomInsetPt
                    + TableRowEndGapPt;

                return leadPt;
            }

            return row.HeightPt;
        }

        // Сколько абзацев подряд с «не отрывать от следующего» проверяется за раз.
        // Длиннее цепочки в живых документах не бывает, а потолок не даёт проверке
        // уйти по всему документу, где правило стоит у каждого абзаца.
        private const int MaxKeepWithNextChain = 16;

        /// <summary>
        /// Высота, которая должна поместиться на странице вместе с абзацем bi, чтобы
        /// «не отрывать от следующего» было соблюдено: сам абзац и все следующие за
        /// ним абзацы цепочки целиком, плюс начало абзаца, который цепочку замыкает.
        ///
        /// Начало — две первые строки, а не одна: абзац, от которого на странице
        /// осталась бы одна строка, пагинация и так уносит на следующий лист целиком
        /// (запрет висячих строк), и заголовок снова остался бы один. Абзац из одной
        /// или двух строк берётся целиком. За таблицей — её первая строка.
        ///
        /// Разрыв страницы или абзац «с новой страницы» внутри цепочки её обрывают:
        /// следующий текст всё равно начнёт новый лист, и держаться не за что — тогда
        /// правило ничего не требует, и возвращается ноль.
        /// </summary>
        private float KeepWithNextChainHeight(
            IReadOnlyList<BlockModel> blocks,
            int startIndex,
            Dictionary<ParagraphBlock, ParagraphViewModel> pvmByBlock,
            float textWidthPt)
        {
            float total = 0f;

            // Нижний край обтекаемых объектов из Word, отсчитанных от абзаца цепочки,
            // от её начала: такой объект должен поместиться на листе вместе с абзацем,
            // иначе Word уносит абзац на следующий (см. AnchoredFloatsNeedPt), а с ним
            // и всю цепочку.
            float floatsBottomPt = 0f;
            bool collapseSpacing = DocVm?.Document.CollapseParagraphSpacing == true;
            float chainPrevAfterPt = float.NaN;

            // Скрытые свёрнутым заголовком блоки в цепочку не входят: на листе их нет, и
            // заголовок держится за то, что под ним видно. Длина цепочки считается по
            // видимым блокам.
            int chainSeen = 0;
            for (int k = startIndex; k < blocks.Count && chainSeen < MaxKeepWithNextChain; k++)
            {
                var block = blocks[k];

                if (k > startIndex && IsCollapsedBlock(block)) continue;
                chainSeen++;

                if (block is BreakBlock) return 0f;

                // Плавающая картинка или фигура высоты в потоке не занимает: цепочка
                // держится за абзац под ней. Но обтекаемая, отсчитанная от абзаца,
                // требует под ним места на своём листе.
                if (block is IFloatingObject { WrapMode: not WrapMode.Inline })
                {
                    float floatsNeedPt = AnchoredFloatsNeedPt(blocks, k);
                    if (floatsNeedPt > 0f && total + floatsNeedPt > floatsBottomPt)
                        floatsBottomPt = total + floatsNeedPt;
                    continue;
                }

                if (block is TableBlock table)
                {
                    // Таблица с обтеканием высоты в потоке не занимает: цепочка держится
                    // за абзац под ней.
                    if (table.FloatPosition is not null) continue;

                    var tableLayout = GetOrBuildTableLayout(table, textWidthPt);
                    if (tableLayout.Rows.Count > 0)
                    {
                        // Строку, которую можно разрывать между страницами, держать целиком
                        // незачем: абзацу хватает её начала — первой строки текста. Иначе
                        // заголовок над таблицей из одной высокой строки уезжал на новый
                        // лист вместе со всей таблицей, хотя у Word остаётся на своём.
                        total += table.SplitMode == TableSplitMode.ByCell
                            ? SplittableRowLeadHeightPt(tableLayout.Rows[0])
                            : tableLayout.Rows[0].HeightPt;
                    }
                    return Math.Max(total, floatsBottomPt);
                }

                if (block is not ParagraphBlock para) return Math.Max(total, floatsBottomPt);
                if (k > startIndex && para.Properties.PageBreakBefore) return 0f;
                if (!pvmByBlock.TryGetValue(para, out var vm)) return Math.Max(total, floatsBottomPt);

                var layout = GetOrBuildLayout(vm, textWidthPt);
                bool continuesChain = k == startIndex || para.Properties.KeepWithNext;

                // Интервал до, уже покрытый интервалом после предыдущего абзаца цепочки,
                // при схлопывании второй раз не считается — как и в самой вёрстке.
                float chainBeforePt = layout.SpaceBeforePt;
                if (collapseSpacing && !float.IsNaN(chainPrevAfterPt))
                    chainBeforePt = Math.Max(0f,
                        chainBeforePt - Math.Min(chainPrevAfterPt, CollapsibleSpaceBeforePt(layout)));
                total += chainBeforePt;

                if (layout.Lines.Count == 0)
                {
                    total += FallbackLinePt;
                    if (!continuesChain || !para.Properties.KeepWithNext) return Math.Max(total, floatsBottomPt);
                    total += layout.SpaceAfterPt;
                    chainPrevAfterPt = float.NaN;
                    continue;
                }

                if (k > startIndex && !para.Properties.KeepWithNext)
                {
                    // Замыкающий абзац: только его начало.
                    int lines = layout.Lines.Count <= 2 ? layout.Lines.Count : 2;
                    for (int li = 0; li < lines; li++)
                        total += layout.Lines[li].Height;
                    return Math.Max(total, floatsBottomPt);
                }

                foreach (var line in layout.Lines)
                    total += line.Height;
                total += layout.SpaceAfterPt;
                chainPrevAfterPt = CollapsibleSpaceAfterPt(layout);

                if (!continuesChain) return Math.Max(total, floatsBottomPt);
            }

            return Math.Max(total, floatsBottomPt);
        }

        /// <summary>
        /// Сколько места от верха абзаца-опоры требуют обтекаемые объекты из Word,
        /// отсчитанные от него по вертикали, — от блока index до самого абзаца (объекты
        /// стоят в потоке прямо перед своим абзацем). Ноль — таких объектов нет или за
        /// ними не абзац.
        ///
        /// Word держит такой объект на одном листе со своим абзацем: если от верха
        /// абзаца до низа объекта не хватает места до нижнего поля, абзац вместе с
        /// объектом уходит на следующий лист, а не оставляет картинку свисать за поле.
        /// Объекты поверх текста и за текстом листа не делят и сюда не входят.
        /// </summary>
        private float AnchoredFloatsNeedPt(IReadOnlyList<BlockModel> blocks, int index)
        {
            float needPt = 0f;

            for (int k = index; k < blocks.Count; k++)
            {
                var block = blocks[k];
                if (IsCollapsedBlock(block)) continue;

                if (block is not IFloatingObject { WrapMode: not WrapMode.Inline } floating)
                    return block is ParagraphBlock ? needPt : 0f;

                if (floating.WrapMode is not (WrapMode.Square or WrapMode.Tight)) continue;
                if (floating.PinnedPage > 0) continue;

                TableFloatPosition? anchor;
                float widthPt;
                float heightPt;
                switch (floating)
                {
                    case ImageBlock image:
                        anchor = image.AnchorPosition;
                        (widthPt, heightPt) = ReadingImageSize(image);
                        break;
                    case ShapeBlock shape:
                        anchor = shape.AnchorPosition;
                        (widthPt, heightPt) = ReadingShapeSize(shape);
                        break;
                    default:
                        continue;
                }

                if (anchor is not { VerticalAnchor: TableFloatAnchor.Text }) continue;
                if (widthPt <= 0f || heightPt <= 0f) continue;

                // Габарит на листе — рамка Word с полями обрамления либо повёрнутый
                // прямоугольник; он центрирован на месте самого объекта.
                var (_, boxHeightPt) = FloatingObjectBox.Of(floating, widthPt, heightPt);
                float topPt = (float)anchor.YPt + ReadingOffsetYPt(floating.OffsetYPt)
                              + (heightPt - boxHeightPt) / 2f;
                float bottomPt = topPt + boxHeightPt;

                if (bottomPt > needPt) needPt = bottomPt;
            }

            return 0f;
        }

        /// <summary>
        /// Стоит ли абзац bi внутри цепочки «не отрывать от следующего», а не в её
        /// начале: ближайший видимый блок над ним — абзац с тем же правилом. Плавающие
        /// объекты высоты в потоке не занимают и цепочку не прерывают.
        /// </summary>
        private bool ContinuesKeepWithNextChain(IReadOnlyList<BlockModel> blocks, int index)
        {
            for (int k = index - 1; k >= 0; k--)
            {
                var block = blocks[k];
                if (IsCollapsedBlock(block)) continue;
                if (block is IFloatingObject { WrapMode: not WrapMode.Inline }) continue;
                if (block is TableBlock { FloatPosition: not null }) continue;

                return block is ParagraphBlock { Properties.KeepWithNext: true };
            }

            return false;
        }

        /// <summary>
        /// Голова цепочки «не отрывать от следующего»: абзацы цепочки до замыкающего
        /// блока, без него самого. Высота считается так же, как в
        /// <see cref="KeepWithNextChainHeight"/>.
        ///
        /// Rigid — разорвать голову законно негде: каждый её абзац короче четырёх строк
        /// (запрет висячих строк не оставляет на листе одну строку и не уносит одну, и
        /// такой абзац не делится) или помечен «не разрывать», а за головой действительно
        /// стоит замыкающий блок. Обрыв цепочки разрывом страницы, потолок длины или
        /// абзац, который делится, дают false — там место для разрыва есть.
        /// </summary>
        private (float HeadPt, bool Rigid) KeepWithNextChainHead(
            IReadOnlyList<BlockModel> blocks,
            int startIndex,
            Dictionary<ParagraphBlock, ParagraphViewModel> pvmByBlock,
            float textWidthPt)
        {
            float head = 0f;
            bool collapseSpacing = DocVm?.Document.CollapseParagraphSpacing == true;
            float chainPrevAfterPt = float.NaN;

            int chainSeen = 0;
            for (int k = startIndex; k < blocks.Count && chainSeen < MaxKeepWithNextChain; k++)
            {
                var block = blocks[k];

                if (k > startIndex && IsCollapsedBlock(block)) continue;
                chainSeen++;

                if (block is BreakBlock) return (head, false);

                if (block is IFloatingObject { WrapMode: not WrapMode.Inline }) continue;

                if (block is TableBlock table)
                {
                    if (table.FloatPosition is not null) continue;

                    // Таблица замыкает цепочку: голова кончилась перед ней.
                    return (head, k > startIndex);
                }

                if (block is not ParagraphBlock para) return (head, false);
                if (k > startIndex && para.Properties.PageBreakBefore) return (head, false);

                // Замыкающий абзац в голову не входит.
                if (k > startIndex && !para.Properties.KeepWithNext) return (head, true);

                if (!pvmByBlock.TryGetValue(para, out var vm)) return (head, false);

                var layout = GetOrBuildLayout(vm, textWidthPt);

                // Абзац, который делится между листами, даёт законное место для разрыва.
                if (layout.Lines.Count > 3 && !para.Properties.KeepTogether) return (head, false);

                float chainBeforePt = layout.SpaceBeforePt;
                if (collapseSpacing && !float.IsNaN(chainPrevAfterPt))
                    chainBeforePt = Math.Max(0f,
                        chainBeforePt - Math.Min(chainPrevAfterPt, CollapsibleSpaceBeforePt(layout)));
                head += chainBeforePt;

                if (layout.Lines.Count == 0)
                {
                    head += FallbackLinePt + layout.SpaceAfterPt;
                    chainPrevAfterPt = float.NaN;
                }
                else
                {
                    foreach (var line in layout.Lines)
                        head += line.Height;
                    head += layout.SpaceAfterPt;
                    chainPrevAfterPt = CollapsibleSpaceAfterPt(layout);
                }

                // Начало цепочки без своего правила — цепочки нет.
                if (!para.Properties.KeepWithNext) return (head, false);
            }

            return (head, false);
        }

        // Результат последнего выполненного прохода раскладки страниц.
        private List<ParaLayout> _passLayouts = new();
        private List<PageRect> _passPages = new();
        private List<TableEntry> _passTables = new();
        private List<ImageEntry> _passImages = new();
        private HashSet<ImageBlock> _passInlineTransferred = new();
        private float _passCanvasHeightPt;

        // Публиковать ли результат каждого прохода сразу. Выключается на время итераций
        // сходимости обтекания.
        private bool _publishPassResults = true;

        /// <summary>
        /// Отдаёт результат последнего прохода рендеру. Единственное место, где меняется
        /// видимая раскладка страниц.
        /// </summary>
        private void PublishPassResults()
        {
            // Сторона переплёта: проход раскладывает все листы одинаково, а на листах,
            // где переплёт справа, содержимое уезжает влево уже здесь, перед показом.
            var (publishLayouts, publishPages, publishTables, publishImages, publishShapes) =
                WithGutterSides(_passLayouts, _passPages, _passTables, _passImages, _passShapes);

            lock (_renderLock)
            {
                // Полосы и сдвиги ленты кладутся здесь же, под тем же замком: они
                // описывают именно этот набор страниц, и разъехаться с ним не должны.
                bool ribbonNow = ReadingRibbon
                    && _passRibbonBandHeights.Length == _passPages.Count
                    && _passPages.Count > 0;

                _ribbonBandHeights = ribbonNow ? _passRibbonBandHeights : Array.Empty<float>();
                _ribbonPageDy = ribbonNow
                    ? BuildRibbonPageDeltas(_passPages, _passRibbonBandHeights)
                    : Array.Empty<float>();

                _layouts = publishLayouts;
                _pages = publishPages;
                _tables = publishTables;
                _images = publishImages;
                _shapes = publishShapes;
                _inlineTransferredImages = _passInlineTransferred;
                _breakMarks = _passBreakMarks;
                _canvasHeightPt = _passCanvasHeightPt;
                _canvasHeight = _passCanvasHeightPt * PtToPx;
            }

            // Страницы посчитаны — можно сказать линейке, где стоит лист каретки. При
            // листах в ряд он уехал в свою колонку, и разметка линейки должна уехать с ним.
            NotifyPageOffsetX();

            // Число страниц и строк меняется ровно здесь, вместе с видимой раскладкой.
            // Уведомление идёт за пределами замка: получатель работает со строкой
            // состояния, и держать на нём замок рендера незачем.
            //
            // Частичный проход числа страниц не знает: строка состояния ждёт полного.
            if (_partialFromBlock < 0)
            {
                NotifyPagination();
                LogFirstPagesComposition();

                // Документ разложен целиком — можно вернуть человека туда, где он был.
                // Отдельным шагом: сейчас идёт пересборка, и каретку она ещё двигает.
                if (_pendingViewRestore is not null)
                    Avalonia.Threading.Dispatcher.UIThread.Post(
                        ApplyPendingViewRestore, Avalonia.Threading.DispatcherPriority.Loaded);
            }
        }

        /// <summary>
        /// Регистрирует встроенные в строку картинки как записи списка картинок.
        /// Рисует такую картинку рендер текста, но выделение, маркеры размера, поворот
        /// и обрезка работают по единому списку — без записи по картинке в строке
        /// нельзя было бы даже кликнуть.
        ///
        /// Геометрия повторяет SKTextRenderer.RenderParagraphLines символ в символ:
        /// тот же сдвиг выравнивания, та же накопленная добавка растяжки по ширине
        /// и та же база строки. Любое расхождение развело бы рамку выделения
        /// с самой картинкой.
        /// </summary>
        private void CollectInlineImageEntries(List<ParaLayout> layouts, List<ImageEntry> target)
        {
            foreach (var pl in layouts)
            {
                var layout = pl.Layout;
                if (layout is null || layout.Lines.Count == 0) continue;

                // Картинка в повёрнутой ячейке рисуется вместе с её текстом, повёрнутой.
                // Запись с прямоугольной рамкой и маркерами размера легла бы на лист не
                // там и не той стороной, поэтому такую картинку правят через её абзац.
                if (pl.Cell is { IsRotated: true }) continue;

                int lineFrom = Math.Max(0, pl.LineFrom);
                int lineTo = Math.Min(pl.LineTo, layout.Lines.Count);
                if (lineFrom >= lineTo) continue;

                float paraX = pl.AbsXPt + layout.LeftIndentPt;
                float yBase = layout.Lines[lineFrom].Y;

                for (int li = lineFrom; li < lineTo; li++)
                {
                    var line = layout.Lines[li];
                    float lineY = pl.Ypt + (line.Y - yBase);
                    float lineShift = SKTextRenderer.LineAlignShift(layout, li);
                    int justifyFragment = 0;
                    float extraPerSpace = SKTextRenderer.JustifyExtraPerSpace(layout, li, 0);
                    float justifyShift = 0f;

                    foreach (var seg in line.Segments)
                    {
                        // Разорванная объектом строка растягивается по отрезкам —
                        // повторяем логику рендера, иначе рамка картинки в строке
                        // разъедется с самой картинкой.
                        if (seg.WrapFragmentIndex != justifyFragment)
                        {
                            justifyFragment = seg.WrapFragmentIndex;
                            extraPerSpace = SKTextRenderer.JustifyExtraPerSpace(
                                layout, li, justifyFragment);
                            justifyShift = 0f;
                        }

                        if (seg.InlineImageId is Guid inlineId && !seg.IsHidden)
                        {
                            var block = FindInlineImage(inlineId);
                            if (block is not null && block.WidthPt > 0.0 && block.HeightPt > 0.0)
                            {
                                float segX = paraX + seg.X + lineShift + justifyShift;
                                float baseY = lineY + line.Baseline;

                                // Бокс сегмента — AABB повёрнутой картинки, сама картинка
                                // центрирована в нём (так же считает DrawInlineImageSegment).
                                float boxW = seg.ObjectWidthPt;
                                float boxH = seg.ObjectHeightPt;
                                var (imgW, imgH) = ReadingImageSize(block);

                                target.Add(new ImageEntry(
                                    block,
                                    baseY - boxH + (boxH - imgH) / 2f,
                                    segX + (boxW - imgW) / 2f,
                                    imgW, imgH, pl.PageIndex, InLine: true, InCell: pl.Cell is not null));
                            }
                        }

                        if (extraPerSpace != 0f)
                        {
                            int segSpaces = 0;
                            foreach (var c in seg.Text)
                                if (c == ' ' || c == '\t') segSpaces++;
                            justifyShift += segSpaces * extraPerSpace;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// На сколько страниц координата документа отстоит от листа, с которого идёт
        /// отсчёт. Листы раздела одного размера и идут с постоянным шагом, поэтому
        /// номер считается делением, а не поиском по списку.
        /// </summary>
        private static int PageAdvanceOf(float yPt, float originPt, float pageStepPt)
        {
            if (pageStepPt <= 0f) return 0;

            float advance = (yPt - originPt) / pageStepPt;
            return advance <= 0f ? 0 : (int)MathF.Floor(advance + 0.001f);
        }

        /// <summary>
        /// Страница плавающей картинки — та, где находится её ЦЕНТР. Центр выбран
        /// потому, что при вращении он неподвижен: край габарита ездит на десятки
        /// пунктов, и картинка меняла страницу прямо во время поворота.
        ///
        /// Единая точка для всей раскладки: и зоны обтекания по ходу прохода, и отрисовка
        /// обязаны видеть у картинки ОДНУ И ТУ ЖЕ страницу.
        /// </summary>
        private static int ResolveFloatingObjectPage(
            float objXPt, float objYPt, float objWpt, float objHpt,
            List<PageRect> pages, int fallbackPage)
        {
            if (pages.Count == 0) return fallbackPage;

            // Точка привязки — ЦЕНТР картинки. При вращении он стоит на месте, а верх
            // повёрнутого габарита ездит на десятки пунктов: у картинки 355×163 поворот
            // на прямой угол поднимает верх почти на сотню. Стоило ему перескочить край
            // листа, картинка доставалась соседней странице, клипалась по ней и пропадала
            // с экрана прямо во время вращения.
            float cxPt = objXPt + objWpt / 2f;
            float cyPt = objYPt + objHpt / 2f;

            // Горизонталь обязательна: при нескольких страницах в ряду соседние листы
            // имеют ОДИН И ТОТ ЖЕ диапазон Y, и по вертикали они неразличимы. Прежний
            // расчёт возвращал первый лист ряда, картинку приписывало чужой странице,
            // клипало по ней и она пропадала с экрана.
            int nearest = -1;
            float nearestDist = float.MaxValue;

            for (int p = 0; p < pages.Count; p++)
            {
                var pg = pages[p];
                float left = pg.PadLeftPt;
                float right = left + pg.WidthPt;
                float top = pg.Ypt;
                float bottom = top + pg.HeightPt;

                if (cxPt >= left && cxPt <= right && cyPt >= top && cyPt <= bottom)
                    return p;

                // Расстояние от точки привязки до листа: ноль по той оси, вдоль которой
                // точка уже внутри его границ. Так выбирается лист, к которому картинка
                // ближе всего, когда её центр попал в межстраничный зазор или за край.
                float dx = cxPt < left ? left - cxPt : (cxPt > right ? cxPt - right : 0f);
                float dy = cyPt < top ? top - cyPt : (cyPt > bottom ? cyPt - bottom : 0f);
                float dist = dx * dx + dy * dy;

                if (dist < nearestDist) { nearestDist = dist; nearest = p; }
            }

            // Точка привязки не попала ни в один лист: она в межстраничном зазоре или за
            // краем, и страница выбирается по близости. Именно здесь картинка, стоящая у
            // границы, может достаться соседней странице — а вместе со страницей уезжает
            // и зона обтекания, которую по ней обрезают.
            return nearest >= 0 ? nearest : fallbackPage;
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

            // Схлопывание интервалов между абзацами (см. DocumentModel.CollapseParagraphSpacing):
            // сколько интервала после оставил предыдущий абзац и где он кончился. Абзац,
            // начавшийся ровно там же, забирает из своего интервала до уже пройденную часть.
            bool collapseSpacing = DocVm.Document.CollapseParagraphSpacing;
            float collapsePrevAfterPt = 0f;
            float collapseEndYPt = float.NaN;

            // Нумерация списков за один проход по блокам в порядке следования.
            var markerMap = Rendering.ListNumberingEngine.Compute(blocks);

            // Интервалы между абзацами одного стиля: вывод о соседях — до раскладки.
            ApplyContextualSpacing(blocks);

            // Абзацы в ячейках таблиц в blocks не входят, поэтому маркеры для них
            // считаются отдельно и кладутся прямо в модель: раскладка ячеек строится
            // ниже, и к этому моменту текст маркера должен быть готов.
            _cellListMarkers.Clear();
            ApplyListMarkerTextsInTables(blocks, GetCurrentTextWidthPt(), markerMap);

            var pvmByBlock = new Dictionary<ParagraphBlock, ParagraphViewModel>(DocVm.Paragraphs.Count);
            foreach (var p in DocVm.Paragraphs)
                if (p.Model is not null) pvmByBlock[p.Model] = p;

            // Картинки-блоки, встающие в поток. Собираются отдельно от встроенных в
            // строку: те рисует рендер текста на их месте в строке.
            var newFlowBlockImages = new List<ImageEntry>();

            // Фигуры потока. Прежде поток не строил их вовсе — список фигур уходил
            // наружу пустым, и рамка, стрелка или ромб в черновике просто пропадали:
            // объект в рукописи есть, а на экране его нет. Плавать в потоке фигуре
            // негде, поэтому она встаёт на месте своего блока — как картинка.
            var newFlowShapes = new List<ShapeEntry>();

            for (int bi = 0; bi < blocks.Count; bi++)
            {
                var block = blocks[bi];

                // Раздел под свёрнутым заголовком в поток не ложится
                // (DocumentCanvas.HeadingCollapse).
                if (IsCollapsedBlock(block)) continue;

                if (block is TableBlock tableBlock)
                {
                    var tableLayout = GetOrBuildTableLayout(tableBlock, textWidthPt);
                    float tableXPt = padWPt
                        + (float)tableBlock.ResolveLeftOffsetPt(textWidthPt, tableLayout.TotalWidthPt);
                    int teIdx = newTables.Count;
                    newTables.Add(new TableEntry(tableBlock, tableLayout, yPt, tableXPt, 0));
                    AddCellParasToLayouts(newLayouts, newTables, tableBlock, tableLayout,
                        teIdx, tableXPt, yPt, 0);

                    lastTableRightPt = tableXPt + tableLayout.TotalWidthPt;
                    lastTableBotPt = yPt + tableLayout.TotalHeightPt;
                    yPt += tableLayout.TotalHeightPt;
                    continue;
                }

                // Фигура в потоке: занимает свою высоту и сдвигает текст ниже, встаёт
                // по своему выравниванию. Обтекать в потоке нечего — колонка одна.
                if (block is ShapeBlock flowShape)
                {
                    var (fsW, fsH) = ReadingShapeSize(flowShape);
                    if (fsW <= 0f || fsH <= 0f) continue;

                    double fsRad = flowShape.RotationDeg * Math.PI / 180.0;
                    float fsBoxW = fsW * (float)Math.Abs(Math.Cos(fsRad))
                                 + fsH * (float)Math.Abs(Math.Sin(fsRad));
                    float fsBoxH = fsW * (float)Math.Abs(Math.Sin(fsRad))
                                 + fsH * (float)Math.Abs(Math.Cos(fsRad));

                    float fsSlack = textWidthPt - fsBoxW;
                    float fsBoxX = padWPt;
                    if (fsSlack > 0f)
                    {
                        fsBoxX += flowShape.Alignment switch
                        {
                            Models.Styles.TextAlignment.Center => fsSlack / 2f,
                            Models.Styles.TextAlignment.Right => fsSlack,
                            _ => 0f
                        };
                    }

                    // Запись хранит неповёрнутый прямоугольник, центрированный в
                    // габарите: поворот делает сам рендерер фигуры.
                    newFlowShapes.Add(new ShapeEntry(
                        flowShape,
                        yPt + (fsBoxH - fsH) / 2f,
                        fsBoxX + (fsBoxW - fsW) / 2f,
                        fsW, fsH, 0));

                    yPt += fsBoxH;
                    continue;
                }

                // Картинка в потоке. Раньше её здесь просто не было: поток верстал
                // только текст, и всякая картинка-блок в черновике и веб-режиме
                // пропадала — объект в документе есть, а на экране его нет.
                //
                // Обтекания в потоке быть не может: колонка одна, страниц нет, и
                // привязывать картинку не к чему. Поэтому любая — плавающая,
                // привязанная к странице, обтекаемая — встаёт в поток на месте своего
                // блока и занимает свою высоту. Ровно так поступают читалки с
                // перевёрстываемым текстом.
                if (block is ImageBlock flowImage)
                {
                    var (fiW, fiH) = ReadingImageSize(flowImage);
                    if (fiW <= 0f || fiH <= 0f) continue;

                    // Картинка шире колонки ужимается под неё с сохранением пропорций:
                    // листа в потоке нет, обрезать её нечем, и без этого она уходила бы
                    // за край холста.
                    if (fiW > textWidthPt)
                    {
                        float fiFit = textWidthPt / fiW;
                        fiW *= fiFit;
                        fiH *= fiFit;
                    }

                    double fiRad = flowImage.RotationDeg * Math.PI / 180.0;
                    float fiBoxW = fiW * (float)Math.Abs(Math.Cos(fiRad))
                                 + fiH * (float)Math.Abs(Math.Sin(fiRad));
                    float fiBoxH = fiW * (float)Math.Abs(Math.Sin(fiRad))
                                 + fiH * (float)Math.Abs(Math.Cos(fiRad));

                    float fiSlack = textWidthPt - fiBoxW;
                    float fiBoxX = padWPt;
                    if (fiSlack > 0f)
                    {
                        fiBoxX += flowImage.Alignment switch
                        {
                            Models.Styles.TextAlignment.Center => fiSlack / 2f,
                            Models.Styles.TextAlignment.Right => fiSlack,
                            _ => 0f
                        };
                    }

                    // ImageEntry хранит неповёрнутый прямоугольник, центрированный в
                    // габарите: рендер поворачивает его вокруг центра.
                    newFlowBlockImages.Add(new ImageEntry(
                        flowImage,
                        yPt + (fiBoxH - fiH) / 2f,
                        fiBoxX + (fiBoxW - fiW) / 2f,
                        fiW, fiH, 0));

                    yPt += fiBoxH;
                    continue;
                }

                if (block is not ParagraphBlock paraBlock) continue;

                if (!pvmByBlock.TryGetValue(paraBlock, out var pvm)) continue;

                Rendering.ListMarkerInfo? paraMarker =
                    markerMap.TryGetValue(paraBlock, out var _mi) ? _mi : null;
                if (paraBlock.ListProperties is not null)
                {
                    paraBlock.ListProperties.ComputedMarkerText = paraMarker?.Text;
                    MigrateCorruptListMarker(paraBlock, textWidthPt);
                }

                var layout = GetOrBuildLayout(pvm, textWidthPt);

                // Якорь перед таблицей
                if (string.IsNullOrEmpty(pvm.PlainText) && bi + 1 < blocks.Count && blocks[bi + 1] is TableBlock nextFlowTb
                    && !IsCollapsedBlock(nextFlowTb))
                {
                    float anchorX = padWPt + (float)nextFlowTb.LeftIndentPt - AnchorMarginPt;
                    newLayouts.Add(new ParaLayout(pvm, layout, yPt, FallbackLinePt,
                        0, 0, 0, AbsXPt: anchorX));
                    continue;
                }

                // Якорь после таблицы
                if (string.IsNullOrEmpty(pvm.PlainText) && bi > 0 && blocks[bi - 1] is TableBlock prevFlowTb
                    && !IsCollapsedBlock(prevFlowTb))
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
                        AbsXPt: padWPt, Marker: paraMarker));
                    yPt += emptyH;
                    continue;
                }

                // Схлопывание интервалов — как в режиме страниц.
                if (collapseSpacing && !float.IsNaN(collapseEndYPt)
                    && Math.Abs(yPt - collapseEndYPt) < 0.01f)
                    yPt -= Math.Min(collapsePrevAfterPt, CollapsibleSpaceBeforePt(layout));

                float hPt = Math.Max(layout.TotalHeightPt, FallbackLinePt);
                newLayouts.Add(new ParaLayout(
                    pvm, layout,
                    yPt + layout.SpaceBeforePt, hPt,
                    0, 0, layout.Lines.Count,
                    AbsXPt: padWPt, Marker: paraMarker));
                yPt += layout.BlockHeightPt;

                collapsePrevAfterPt = CollapsibleSpaceAfterPt(layout);
                collapseEndYPt = yPt;
            }

            float newCanvasH = yPt + padHPt;

            // Картинки потока: собранные выше блоки плюс встроенные в строку.
            var newFlowImages = new List<ImageEntry>(newFlowBlockImages);
            CollectInlineImageEntries(newLayouts, newFlowImages);

            lock (_renderLock)
            {
                _layouts = newLayouts;
                _pages = new List<PageRect>();
                _tables = newTables;
                _images = newFlowImages;

                _shapes = newFlowShapes;
                _canvasHeightPt = newCanvasH;
                _canvasHeight = newCanvasH * PtToPx;
            }

            // Черновик листов не считает, и строка состояния должна об этом узнать:
            // иначе там осталось бы число страниц от прошлой постраничной раскладки.
            NotifyPagination();
        }
    }
}