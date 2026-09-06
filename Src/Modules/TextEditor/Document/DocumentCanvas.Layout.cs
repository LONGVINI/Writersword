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
            List<PageRect> currentPages;
            lock (_renderLock)
            {
                current = _layouts;
                currentImages = _images;
                currentShapes = _shapes;
                currentPages = _pages;
            }

            // Фигуры обтекаются наравне с картинками, поэтому быстрый путь обязан
            // видеть и их: иначе при наборе текст лез бы на фигуру до ближайшего
            // полного пересбора.
            var currentFloats = BuildFloatSource(currentImages, currentShapes);

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
                        float cw = (float)(_canvasWidth * PxToPt);
                        return Math.Max(ReadingColumnWidthPt(cw) - DraftPadWPt * 2f, 1f);
                    }
                default:
                    return Math.Max((float)(_canvasWidth * PxToPt) - DraftPadWPt * 2f, 1f);
            }
        }

        // Источник зон обтекания для текущего прохода пагинации. null — зоны берутся
        // из картинок, накопленных по ходу прохода (только блоки, встреченные раньше
        // абзаца). Второй проход подставляет сюда ПОЛНЫЙ список картинок первого
        // прохода — обтекание работает и для абзацев, стоящих в документе до блока.
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
            int maxWrapIterations =
                (_imageDragging || _imageResizing || _imageRotating
                 || _shapeDragging || _shapeResizing || _shapeRotating) ? 1 : 4;
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

                if (converged) break;
            }
        }

        /// <summary>
        /// Подпись прошлой пробы вёрстки. Проба пишется не один раз за запуск, а при
        /// каждом изменении её содержимого: импорт документа меняет лист и интервалы
        /// уже после первой раскладки, и одноразовая запись его не застаёт.
        /// </summary>
        private string? _paginationProbeSignature;

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

            // Картинки с жёсткой привязкой к странице: позиционируются после основного
            // потока, когда известно общее число страниц и достроены недостающие.
            var pinnedImages = new List<ImageBlock>();

            // Фигуры с жёсткой привязкой к странице — та же отложенная обработка:
            // их лист может быть ещё не создан, пока идёт основной поток.
            var pinnedShapes = new List<ShapeBlock>();

            newPages.Add(new PageRect(pageYPt, pageWidthPt, pageHeightPt, pageXPt, mt, ml, mb));

            float pageOffsetXPx = pageXPt * PtToPx * (float)Zoom
                - (float)(_parentScrollViewer?.Offset.X ?? 0);
            _lastPageOffsetXPx = pageOffsetXPx;
            PageOffsetXChanged?.Invoke(pageOffsetXPx);

            var blocks = DocVm!.Document.Sections[0].Blocks;

            // Нумерация списков за один проход по блокам в порядке следования.
            var markerMap = Rendering.ListNumberingEngine.Compute(blocks);

            // Абзацы в ячейках таблиц в blocks не входят, поэтому маркеры для них
            // считаются отдельно и кладутся прямо в модель: раскладка ячеек строится
            // ниже, и к этому моменту текст маркера должен быть готов.
            _cellListMarkers.Clear();
            ApplyListMarkerTextsInTables(blocks, GetCurrentTextWidthPt());

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

                                // Продолжение таблицы встало вверху новой страницы, где может
                                // лежать обтекаемая картинка. Проверка перед циклом сделана для
                                // исходной позиции в потоке и к этому месту отношения не имеет.
                                ResolveTableTop(
                                    tableZoneSource, ref contentYPt,
                                    tableXPt, tableLayout.TotalWidthPt,
                                    RemainingTableHeightPt(tableLayout, ri, nextOffset),
                                    textXPt, textWidthPt, pageBottomPt, newPages);

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

                                // Строка ушла на новую страницу целиком — её новое место
                                // проверяется на обтекание заново.
                                ResolveTableTop(
                                    tableZoneSource, ref contentYPt,
                                    tableXPt, tableLayout.TotalWidthPt,
                                    RemainingTableHeightPt(tableLayout, ri, 0f),
                                    textXPt, textWidthPt, pageBottomPt, newPages);

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

                                // Перенос без снапа: строка целиком уезжает на новую страницу,
                                // где её положение так же может попасть под картинку.
                                ResolveTableTop(
                                    tableZoneSource, ref contentYPt,
                                    tableXPt, tableLayout.TotalWidthPt,
                                    RemainingTableHeightPt(tableLayout, ri, sliceFirstRowOffset),
                                    textXPt, textWidthPt, pageBottomPt, newPages);

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

                if (block is ShapeBlock shapeBlock)
                {
                    float shapeWpt = (float)Math.Max(shapeBlock.WidthPt, ShapeMinSidePt);
                    float shapeHpt = (float)Math.Max(shapeBlock.HeightPt, ShapeMinSidePt);

                    if (shapeBlock.WrapMode == WrapMode.Inline)
                    {
                        // Фигура-блок занимает собственную строку и сдвигает текст ниже —
                        // ровно как картинка «в тексте». Повёрнутая занимает свой AABB.
                        double shapeRad = shapeBlock.RotationDeg * Math.PI / 180.0;
                        float shapeCos = (float)Math.Abs(Math.Cos(shapeRad));
                        float shapeSin = (float)Math.Abs(Math.Sin(shapeRad));
                        float shapeBoxW = shapeWpt * shapeCos + shapeHpt * shapeSin;
                        float shapeBoxH = shapeWpt * shapeSin + shapeHpt * shapeCos;

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
                        // Плавающая. Как и у картинки, проходы сходимости обязаны видеть
                        // фигуру ТАМ ЖЕ, где по ней построены зоны: иначе обтекание
                        // вытесняет текст, поток над фигурой становится выше, фигура на
                        // следующем проходе встаёт относительно другой страницы — и
                        // раскладка перестаёт сходиться.
                        ShapeEntry? frozenShape = null;
                        if (_wrapZoneShapesOverride is { } frozenShapes)
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
                            newShapes.Add(BuildShapeEntry(
                                shapeBlock, pageXPt, pageYPt, ml, mt, newPages, pageIdx));
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
                            // на реальную высоту с учётом угла.
                            double rotRad = imageBlock.RotationDeg * Math.PI / 180.0;
                            float absCos = (float)Math.Abs(Math.Cos(rotRad));
                            float absSin = (float)Math.Abs(Math.Sin(rotRad));
                            float boxWpt = imgWpt * absCos + imgHpt * absSin;
                            float boxHpt = imgWpt * absSin + imgHpt * absCos;

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
                            // Плавающая: позиция по смещению относительно области страницы.
                            float fx = pageXPt + ml + (float)imageBlock.OffsetXPt;
                            float fy = pageYPt + mt + (float)imageBlock.OffsetYPt;

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
                            ImageEntry? frozen = null;
                            if (_wrapZoneImagesOverride is { } frozenImages)
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
                                newImages.Add(new ImageEntry(
                                    imageBlock, fz.Ypt, fz.XPt, imgWpt, imgHpt, fz.PageIndex));
                            }
                            else
                            {
                                // Страницу определяем СРАЗУ, а не пересчётом в конце прохода.
                                // Зоны обтекания строятся по ходу дела и берут страницу из
                                // записи: если она там ещё «страница блока в потоке», а к концу
                                // прохода станет другой, картинка рисуется на одной странице,
                                // а текст сдвигает на другой — ровно то, чего быть не должно.
                                newImages.Add(new ImageEntry(
                                    imageBlock, fy, fx, imgWpt, imgHpt,
                                    ResolveFloatingObjectPage(
                                        fx, fy, imgWpt, imgHpt, newPages, pageIdx)));
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

                // Обтекание текстом: если рядом с вертикалью параграфа лежит плавающая
                // картинка в режиме Square/Tight — строим раскладку с зонами исключения,
                // строки обходят габарит картинки. Такой лейаут не кешируется.
                var zoneSource = BuildFloatSource(
                    _wrapZoneImagesOverride ?? newImages,
                    _wrapZoneShapesOverride ?? newShapes);

                // Верх первой строки абзаца в координатах документа. Зоны обтекания
                // должны считаться именно от него, а не от contentYPt:
                //   contentYPt — позиция до разбивки на страницы и без SpaceBefore;
                //   цикл по строкам ниже может перенести абзац на следующую страницу
                //   (contentYPt = pageYPt + mt), и тогда зоны, посчитанные от старого
                //   значения, описывают полосу этажом выше реального места абзаца.
                // Разрыв предсказывается тем же условием, что и в цикле, по раскладке
                // без зон: высота строки задаётся шрифтом и от ширины полосы не зависит.
                var probeLayout = GetOrBuildLayout(pvm, textWidthPt);
                float paraStartYPt = contentYPt + probeLayout.SpaceBeforePt;
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
                bool isBeforeTableAnchor = string.IsNullOrEmpty(pvm.PlainText)
                    && bi + 1 < blocks.Count && blocks[bi + 1] is TableBlock
                    && !(bi > 0 && blocks[bi - 1] is TableBlock);
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

                contentYPt += layout.SpaceBeforePt;
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
                            double throwRad = ie.Block.RotationDeg * Math.PI / 180.0;
                            float throwCos = (float)Math.Abs(Math.Cos(throwRad));
                            float throwSin = (float)Math.Abs(Math.Sin(throwRad));
                            float throwBoxW = ie.WidthPt * throwCos + ie.HeightPt * throwSin;
                            float throwBoxH = ie.WidthPt * throwSin + ie.HeightPt * throwCos;
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
            }

            // Сверка вёрстки с внешним редактором: лист, строки и незанятое место
            // внизу страниц. Запись обновляется, когда меняется что-то из измеряемого.
            if (newLayouts.Count > 0)
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

                newImages.Add(new ImageEntry(
                    pinned,
                    pinnedPage.Ypt + pinnedPage.PadTopPt + (float)pinned.OffsetYPt,
                    pinnedPage.PadLeftPt + pinnedPage.MarginLeftPt + (float)pinned.OffsetXPt,
                    pinnedW, pinnedH, pinnedIdx));
            }

            // Привязанные фигуры — по тому же правилу: отсчёт от краёв СВОЕЙ страницы.
            foreach (var pinnedShape in pinnedShapes)
            {
                int pinnedShapeIdx = Math.Clamp(
                    pinnedShape.PinnedPage - 1, 0, Math.Max(0, newPages.Count - 1));
                if (pinnedShapeIdx >= newPages.Count) continue;

                var pinnedShapePage = newPages[pinnedShapeIdx];
                newShapes.Add(new ShapeEntry(
                    pinnedShape,
                    pinnedShapePage.Ypt + pinnedShapePage.PadTopPt + (float)pinnedShape.OffsetYPt,
                    pinnedShapePage.PadLeftPt + pinnedShapePage.MarginLeftPt
                        + (float)pinnedShape.OffsetXPt,
                    (float)Math.Max(pinnedShape.WidthPt, ShapeMinSidePt),
                    (float)Math.Max(pinnedShape.HeightPt, ShapeMinSidePt),
                    pinnedShapeIdx));
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

            float newCanvasH = pageYPt + pageHeightPt + PageGapPt;

            // Страницы рядом: высота канваса определяется числом визуальных рядов,
            // а не логическим столбиком страниц.
            if (_pagesPerRow > 1 && newPages.Count > 0)
            {
                int rows = (newPages.Count + _pagesPerRow - 1) / _pagesPerRow;
                newCanvasH = PageGapPt + rows * (newPages[0].HeightPt + PageGapPt);
            }

            // Результат прохода: промежуточные проходы сходимости обтекания его только
            // копят, наружу уходит последний. Иначе рендер успевает поймать промежуточный
            // кадр — в первом проходе абзац ещё не знает про картинку и верстается во всю
            // ширину, и первая строка мигает полной шириной на каждой пересборке.
            _passLayouts = newLayouts;
            _passPages = newPages;
            _passTables = newTables;
            _passImages = newImages;
            _passShapes = newShapes;
            _passInlineTransferred = newInlineTransferred;
            _passCanvasHeightPt = newCanvasH;

            if (_publishPassResults) PublishPassResults();
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
            lock (_renderLock)
            {
                _layouts = _passLayouts;
                _pages = _passPages;
                _tables = _passTables;
                _images = _passImages;
                _shapes = _passShapes;
                _inlineTransferredImages = _passInlineTransferred;
                _canvasHeightPt = _passCanvasHeightPt;
                _canvasHeight = _passCanvasHeightPt * PtToPx;
            }

            // Число страниц и строк меняется ровно здесь, вместе с видимой раскладкой.
            // Уведомление идёт за пределами замка: получатель работает со строкой
            // состояния, и держать на нём замок рендера незачем.
            NotifyPagination();
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

                        if (seg.InlineImageId is Guid inlineId)
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
                                    imgW, imgH, pl.PageIndex, InLine: true));
                            }
                        }

                        if (extraPerSpace > 0f)
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

            // Нумерация списков за один проход по блокам в порядке следования.
            var markerMap = Rendering.ListNumberingEngine.Compute(blocks);

            // Абзацы в ячейках таблиц в blocks не входят, поэтому маркеры для них
            // считаются отдельно и кладутся прямо в модель: раскладка ячеек строится
            // ниже, и к этому моменту текст маркера должен быть готов.
            _cellListMarkers.Clear();
            ApplyListMarkerTextsInTables(blocks, GetCurrentTextWidthPt());

            var pvmByBlock = new Dictionary<ParagraphBlock, ParagraphViewModel>(DocVm.Paragraphs.Count);
            foreach (var p in DocVm.Paragraphs)
                if (p.Model is not null) pvmByBlock[p.Model] = p;

            // Картинки-блоки, встающие в поток. Собираются отдельно от встроенных в
            // строку: те рисует рендер текста на их месте в строке.
            var newFlowBlockImages = new List<ImageEntry>();

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

                // Картинка в потоке. Раньше её здесь просто не было: поток верстал
                // только текст, и всякая картинка-блок в «Ленте» и в черновике
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

                    // Картинка шире колонки ужимается по ширине: колонка в потоке
                    // задаётся окном, и вылезшая за неё картинка попала бы под обрез.
                    if (fiW > textWidthPt)
                    {
                        float k = textWidthPt / fiW;
                        fiW = textWidthPt;
                        fiH *= k;
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
                        AbsXPt: padWPt, Marker: paraMarker));
                    yPt += emptyH;
                    continue;
                }

                float hPt = Math.Max(layout.TotalHeightPt, FallbackLinePt);
                newLayouts.Add(new ParaLayout(
                    pvm, layout,
                    yPt + layout.SpaceBeforePt, hPt,
                    0, 0, layout.Lines.Count,
                    AbsXPt: padWPt, Marker: paraMarker));
                yPt += layout.BlockHeightPt;
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

                // Черновик страниц не рисует, а фигура живёт координатами листа —
                // показывать её здесь негде.
                _shapes = new List<ShapeEntry>();
                _canvasHeightPt = newCanvasH;
                _canvasHeight = newCanvasH * PtToPx;
            }

            // Черновик листов не считает, и строка состояния должна об этом узнать:
            // иначе там осталось бы число страниц от прошлой постраничной раскладки.
            NotifyPagination();
        }
    }
}