using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using SkiaSharp;
using System;
using System.Collections.Generic;
using Writersword.Core.Models.Rendering;
using Writersword.Modules.TextEditor.Rendering;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.ViewModels;

namespace Writersword.Modules.TextEditor.Document
{
    public sealed partial class DocumentCanvas
    {
        // ── Render ────────────────────────────────────────────────────────
        public override void Render(DrawingContext ctx)
        {
            ctx.Custom(new CanvasSKDrawOperation(
                this, new Rect(0, 0, Bounds.Width, Bounds.Height)));
        }

        internal void RenderWithSKCanvas(SKCanvas canvas)
        {
            if (_isTransitioning) return;

            // Дренируем очередь битмапов ожидающих удаления.
            while (_bitmapDisposeQueue.TryDequeue(out var stale))
                stale?.Dispose();

            List<ParaLayout> layouts;
            List<PageRect> pages;
            List<TableEntry> tables;
            List<ImageEntry> images;
            float canvasHeightPt;
            double canvasWidth;

            lock (_renderLock)
            {
                layouts = _layouts;
                pages = _pages;
                tables = _tables;
                images = _images;
                canvasHeightPt = _canvasHeightPt;
                canvasWidth = _canvasWidth;
            }

            double zoom = Zoom;
            float scale = (float)(PtToPx * zoom);

            int pixelW = (int)Math.Max(Bounds.Width, 1);

            // Рендерим только видимый viewport а не весь документ.
            // _viewportHeight обновляется в OnScrollViewerPropertyChanged.
            // Без ScrollViewer — fallback на Bounds.Height.
            float viewportPx = _viewportHeight > 0 ? (float)_viewportHeight : (float)Bounds.Height;

            // Overscan: рендерим ±viewport выше и ниже видимой области.
            // Когда compositor показывает стale-кадр во время скролла, старый битмап
            // уже покрывает новую позицию → нет чёрных полос.
            float scrollY = (float)_scrollOffsetY;
            float overlapPx = viewportPx;
            float docHeightPx = (float)Bounds.Height;

            // bitmapTopY — верхняя граница рендерируемой области в пикселях документа.
            float bitmapTopY = Math.Max(scrollY - overlapPx, 0f);
            // Нижняя граница не выходит за документ.
            float bitmapBotY = Math.Min(bitmapTopY + viewportPx + overlapPx * 2f, docHeightPx);
            // Если у нижней границы документа не хватает места снизу — сдвигаем верх вниз.
            bitmapTopY = Math.Max(bitmapBotY - viewportPx - overlapPx * 2f, 0f);

            int pixelH = (int)Math.Max(bitmapBotY - bitmapTopY, 1);
            float bitmapTopYInPts = scale > 0f ? bitmapTopY / scale : 0f;

            // scrollYInPts нужен только для DrawCaret (рисуется в координатах документа).
            float scrollYInPts = scale > 0f ? scrollY / scale : 0f;

            if (_caretOnlyRedraw)
            {
                SKBitmap? cached;
                int cachedW, cachedH;
                float cachedScrollY;
                lock (_bitmapLock)
                {
                    cached = _lastFullRenderBitmap;
                    cachedW = _lastFullRenderWidth;
                    cachedH = _lastFullRenderHeight;
                    cachedScrollY = _lastFullRenderScrollY;
                }

                // Кеш валиден если scroll находится внутри overscan-диапазона битмапа.
                // cachedScrollY хранит bitmapTopY — верхний край последнего рендера.
                // Если пользователь проскроллил так что viewport ещё внутри битмапа —
                // можно переиспользовать битмап без перерисовки.
                bool scrollInRange = cached is not null
                    && cachedW == pixelW
                    && scrollY >= cachedScrollY - 0.5f
                    && scrollY + viewportPx <= cachedScrollY + cachedH + 0.5f;

                if (scrollInRange)
                {
                    _caretOnlyRedraw = false;

                    // Битмап рисуем по его реальному bitmapTopY (не scrollY).
                    canvas.DrawBitmap(cached!, 0, cachedScrollY);

                    if (_caretVisible && !_zooming)
                    {
                        canvas.Save();
                        canvas.Scale(scale, scale);
                        DrawCaretOnCanvas(canvas, layouts, pages, canvasWidth);
                        canvas.Restore();
                    }
                    return;
                }
                _caretOnlyRedraw = false;
            }

            _caretOnlyRedraw = false;

            while (_bitmapDisposeQueue.TryDequeue(out var stale))
                stale?.Dispose();

            // Получаем или создаём render-bitmap нужного размера.
            // Если размер не изменился — переиспользуем существующий (0 аллокаций).
            // Если изменился — создаём новый и откладываем старый в очередь.
            SKBitmap? renderTarget;
            lock (_bitmapLock)
            {
                if (_renderBitmap is null || _renderBitmap.Width != pixelW || _renderBitmap.Height != pixelH)
                {
                    if (_renderBitmap is not null) _bitmapDisposeQueue.Enqueue(_renderBitmap);
                    if (_displayBitmap is not null) _bitmapDisposeQueue.Enqueue(_displayBitmap);
                    _renderBitmap = new SKBitmap(pixelW, pixelH, SKColorType.Bgra8888, SKAlphaType.Premul);
                    _displayBitmap = new SKBitmap(pixelW, pixelH, SKColorType.Bgra8888, SKAlphaType.Premul);
                    _bitmapW = pixelW;
                    _bitmapH = pixelH;
                }
                renderTarget = _renderBitmap;
            }

            if (renderTarget is not null)
            {
                // Рисуем прямо в SKBitmap — без SKSurface.Create и SKBitmap.FromImage.
                // SKCanvas(bitmap) использует уже выделенную память битмапа.
                using var offscreen = new SKCanvas(renderTarget);
                offscreen.Clear(SKColors.Transparent);
                offscreen.Save();
                offscreen.Scale(scale, scale);
                // Трансляция -bitmapTopYInPts сдвигает координаты документа
                // так что область [bitmapTopY, bitmapTopY+pixelH] ложится в битмап.
                offscreen.Translate(0f, -bitmapTopYInPts);

                var mode = DocVm?.ViewMode ?? EditorViewMode.Draft;
                if (mode == EditorViewMode.Page)
                    RenderPageMode(offscreen, layouts, pages, tables, images, canvasHeightPt, canvasWidth, false);
                else
                    RenderFlowMode(offscreen, mode, layouts, tables, canvasHeightPt, canvasWidth, false);

                offscreen.Restore();

                // Атомарно свапаем render и display буферы.
                SKBitmap? toDisplay;
                lock (_bitmapLock)
                {
                    (_renderBitmap, _displayBitmap) = (_displayBitmap, _renderBitmap);
                    // Сохраняем bitmapTopY — верхний край отрисованного битмапа.
                    // Используется в cache check: scroll внутри [bitmapTopY, bitmapTopY+H]?
                    _lastFullRenderScrollY = bitmapTopY;
                    toDisplay = _displayBitmap;
                }

                if (toDisplay is not null)
                    canvas.DrawBitmap(toDisplay, 0, bitmapTopY);

                if (_caretVisible && !_zooming)
                {
                    canvas.Save();
                    canvas.Scale(scale, scale);
                    DrawCaretOnCanvas(canvas, layouts, pages, canvasWidth);
                    canvas.Restore();
                }
            }
            else
            {
                // Fallback: рендерим напрямую в lease canvas (весь документ).
                canvas.Save();
                canvas.Scale(scale, scale);
                var mode = DocVm?.ViewMode ?? EditorViewMode.Draft;
                if (mode == EditorViewMode.Page)
                    RenderPageMode(canvas, layouts, pages, tables, images, canvasHeightPt, canvasWidth, _caretVisible && !_zooming);
                else
                    RenderFlowMode(canvas, mode, layouts, tables, canvasHeightPt, canvasWidth, _caretVisible && !_zooming);
                canvas.Restore();
            }
        }

        // Рисует только рамки и фон таблицы (без параграфов — они в _layouts).
        private void RenderTableStructureOnly(
            SKCanvas canvas, SKTableLayout tableLayout, float tableX, float tableY,
            int rowFrom = 0, int rowTo = -1,
            float lastRowVisibleHeightPt = -1f, float firstRowContentOffsetPt = 0f,
            bool isContinuation = false)
        {
            var m = canvas.TotalMatrix;
            float canvasScale = MathF.Sqrt(m.ScaleX * m.ScaleX + m.SkewY * m.SkewY);
            if (canvasScale < 0.01f) canvasScale = 1f;

            int effectiveRowTo = rowTo < 0 ? tableLayout.Rows.Count : rowTo;
            float rowOffsetY = rowFrom > 0 && rowFrom < tableLayout.Rows.Count
                ? tableLayout.Rows[rowFrom].Ypt : 0f;

            // maxPadTop — верхний паддинг первой строки продолжения.
            // Используется сразу в двух местах: увеличивает effectiveRowH строки rowFrom
            // (чтобы нижний паддинг был виден) и уменьшает extraShift последующих строк
            // (чтобы они не отрывались от первой строки). Оба изменения должны быть одинаковыми.
            float maxPadTop = 0f;
            if (isContinuation && firstRowContentOffsetPt > 0f && rowFrom < tableLayout.Rows.Count)
            {
                foreach (var cell in tableLayout.Rows[rowFrom].Cells)
                    maxPadTop = Math.Max(maxPadTop, cell.PadTopPt + cell.Borders.Top.WidthPt);
            }

            foreach (var row in tableLayout.Rows)
            {
                if (row.Row < rowFrom || row.Row >= effectiveRowTo) continue;

                bool isFirstRow = row.Row == rowFrom;
                bool isLastRow = row.Row == effectiveRowTo - 1;
                float rowShift = isFirstRow ? firstRowContentOffsetPt : 0f;
                float effectiveRowH = isFirstRow
                    ? row.HeightPt - rowShift + maxPadTop
                    : row.HeightPt;

                float visibleH = (isLastRow && lastRowVisibleHeightPt >= 0f)
                    ? lastRowVisibleHeightPt
                    : effectiveRowH;

                // Для строк после rowFrom сдвигаем вверх на (firstRowContentOffsetPt - maxPadTop),
                // чтобы верхняя граница строки N совпадала с нижней границей строки rowFrom.
                float extraShift = isFirstRow ? 0f : (firstRowContentOffsetPt - maxPadTop);

                foreach (var cell in row.Cells)
                {
                    float cellX = tableX + cell.Xpt;
                    float cellY = tableY + cell.Ypt - rowOffsetY - rowShift - extraShift;

                    if (!string.IsNullOrEmpty(cell.BackgroundColor)
                        && SKColor.TryParse(cell.BackgroundColor, out var bgColor))
                    {
                        // Мутируем Color кешированного паинта — безопасно на compositor-треде.
                        _paintCellBg.Color = bgColor;
                        canvas.DrawRect(cellX, cellY + rowShift, cell.WidthPt, visibleH, _paintCellBg);
                    }

                    float visibleCellY = cellY + rowShift;

                    SKTextRenderer.RenderCellBordersPublic(canvas, cell, cellX, visibleCellY,
                        visibleH, canvasScale, false, false);
                }
            }
        }

        private void RenderTableSelection(SKCanvas canvas, List<TableEntry> tables,
            TableBlock table, int startRow, int startCol, int endRow, int endCol)
        {
            int minRow = Math.Min(startRow, endRow);
            int maxRow = Math.Max(startRow, endRow);
            int minCol = Math.Min(startCol, endCol);
            int maxCol = Math.Max(startCol, endCol);

            foreach (var te in tables)
            {
                if (te.Table != table) continue;

                int effectiveRowTo = te.RowTo < 0 ? te.Layout.Rows.Count : te.RowTo;
                float rowOffsetY = te.RowFrom > 0 && te.RowFrom < te.Layout.Rows.Count
                    ? te.Layout.Rows[te.RowFrom].Ypt : 0f;

                float maxPadTop = 0f;
                if (te.IsContinuation && te.FirstRowContentOffsetPt > 0f
                    && te.RowFrom < te.Layout.Rows.Count)
                {
                    foreach (var cell in te.Layout.Rows[te.RowFrom].Cells)
                        maxPadTop = Math.Max(maxPadTop, cell.PadTopPt + cell.Borders.Top.WidthPt);
                }

                foreach (var row in te.Layout.Rows)
                {
                    if (row.Row < te.RowFrom || row.Row >= effectiveRowTo) continue;
                    if (row.Row < minRow || row.Row > maxRow) continue;

                    bool isFirstRow = row.Row == te.RowFrom;
                    float rowShift = isFirstRow ? te.FirstRowContentOffsetPt : 0f;
                    float extraShift = isFirstRow ? 0f : (te.FirstRowContentOffsetPt - maxPadTop);
                    float effectiveRowH = isFirstRow
                        ? row.HeightPt - rowShift + maxPadTop
                        : row.HeightPt;

                    float visibleH = (row.Row == effectiveRowTo - 1 && te.LastRowVisibleHeightPt >= 0f)
                        ? te.LastRowVisibleHeightPt
                        : effectiveRowH;

                    foreach (var cell in row.Cells)
                    {
                        if (cell.Column < minCol || cell.Column > maxCol) continue;
                        float cellX = te.XPt + cell.Xpt;
                        float cellY = te.Ypt + cell.Ypt - rowOffsetY - rowShift - extraShift;
                        canvas.DrawRect(cellX, cellY + rowShift, cell.WidthPt, visibleH, _paintSelection);
                    }
                }
            }
        }

        private void RenderFrozenTableSelection(SKCanvas canvas, List<TableEntry> tables, FrozenTableSelection frozen)
            => RenderTableSelection(canvas, tables, frozen.Table, frozen.StartRow, frozen.StartCol, frozen.EndRow, frozen.EndCol);

        // Цвета ручек
        private static readonly SKColor HandleFill = new(0x22, 0x99, 0xFF, 0xCC);
        private static readonly SKColor HandleStroke = new(0xFF, 0xFF, 0xFF, 0xCC);

        /// <summary>
        /// Рисует ↔-ручки на внутренних границах колонок и правом крае таблицы (по центру высоты),
        /// и ↕-ручку на нижнем крае таблицы (по центру ширины).
        /// Ручки рисуются только для активной таблицы (где стоит каретка).
        /// </summary>
        private void RenderTableHandles(SKCanvas canvas, TableEntry te)
        {
            if (!ReferenceEquals(te.Table, _activeTableBlock)) return;

            var layout = te.Layout;
            float tableX = te.XPt;
            float tableY = te.Ypt;
            float tableW = layout.TotalWidthPt;

            // Высота видимого слайса — от tableY до конца последней видимой строки.
            // Используем реальную высоту слайса, а не полную высоту таблицы,
            // чтобы ручки не выходили за пределы видимой области.
            float sliceH = 0f;
            int effectiveRowTo = te.RowTo < 0 ? layout.Rows.Count : te.RowTo;
            for (int ri = te.RowFrom; ri < effectiveRowTo && ri < layout.Rows.Count; ri++)
            {
                float rowH = layout.Rows[ri].HeightPt;
                if (ri == te.RowFrom) rowH -= te.FirstRowContentOffsetPt;
                if (ri == effectiveRowTo - 1 && te.LastRowVisibleHeightPt >= 0f)
                    rowH = te.LastRowVisibleHeightPt;
                sliceH += rowH;
            }
            if (sliceH <= 0f) return;

            const float HW = 6f;
            const float HH = 4f;


            // ↔ на каждой внутренней и внешней правой границе колонки (по центру Y слайса)
            float midY = tableY + sliceH / 2f;
            float accX = tableX;
            for (int i = 0; i < layout.ColumnWidthsPt.Count; i++)
            {
                accX += layout.ColumnWidthsPt[i];
                DrawHandle(canvas, accX, midY, HW, HH, _paintHandleFill, _paintHandleStroke, horizontal: true);
            }

            // ↕ на нижнем краю слайса по центру ширины
            float midX = tableX + tableW / 2f;
            DrawHandle(canvas, midX, tableY + sliceH, HH, HW, _paintHandleFill, _paintHandleStroke, horizontal: false);

            // ↔ на левом крае (для сдвига всей таблицы)
            DrawHandle(canvas, tableX, midY, HW, HH, _paintHandleFill, _paintHandleStroke, horizontal: true);
        }

        private void DrawHandle(SKCanvas canvas,
            float cx, float cy, float hw, float hh,
            SKPaint fill, SKPaint stroke, bool horizontal)
        {
            var rect = new SKRect(cx - hw, cy - hh, cx + hw, cy + hh);
            canvas.DrawRoundRect(rect, 2f, 2f, fill);
            canvas.DrawRoundRect(rect, 2f, 2f, stroke);

            // Стрелочки внутри
            if (horizontal)
            {
                // ←
                canvas.DrawLine(cx - hw + 1.5f, cy, cx - 1f, cy, _paintHandleArrow);
                canvas.DrawLine(cx - hw + 1.5f, cy, cx - hw + 3.5f, cy - 2f, _paintHandleArrow);
                canvas.DrawLine(cx - hw + 1.5f, cy, cx - hw + 3.5f, cy + 2f, _paintHandleArrow);
                // →
                canvas.DrawLine(cx + hw - 1.5f, cy, cx + 1f, cy, _paintHandleArrow);
                canvas.DrawLine(cx + hw - 1.5f, cy, cx + hw - 3.5f, cy - 2f, _paintHandleArrow);
                canvas.DrawLine(cx + hw - 1.5f, cy, cx + hw - 3.5f, cy + 2f, _paintHandleArrow);
            }
            else
            {
                // ↑
                canvas.DrawLine(cx, cy - hh + 1.5f, cx, cy - 1f, _paintHandleArrow);
                canvas.DrawLine(cx, cy - hh + 1.5f, cx - 2f, cy - hh + 3.5f, _paintHandleArrow);
                canvas.DrawLine(cx, cy - hh + 1.5f, cx + 2f, cy - hh + 3.5f, _paintHandleArrow);
                // ↓
                canvas.DrawLine(cx, cy + hh - 1.5f, cx, cy + 1f, _paintHandleArrow);
                canvas.DrawLine(cx, cy + hh - 1.5f, cx - 2f, cy + hh - 3.5f, _paintHandleArrow);
                canvas.DrawLine(cx, cy + hh - 1.5f, cx + 2f, cy + hh - 3.5f, _paintHandleArrow);
            }
        }

        private void RenderPageMode(
            SKCanvas canvas,
            List<ParaLayout> layouts,
            List<PageRect> pages,
            List<TableEntry> tables,
            List<ImageEntry> images,
            float canvasHeightPt,
            double canvasWidth,
            bool drawCaret)
        {
            float canvasWPt = (float)(canvasWidth * PxToPt);

            // Фон заливаем не по canvasWPt (это viewportW/zoom), а по реальным границам канваса:
            // при увеличении масштаба Bounds шире viewportW, и правый/нижний край оставался бы
            // незакрашенным — оттуда и проступал чёрный фон при зуме. Переводим Bounds (px экрана)
            // в координаты рисования (pt до масштаба): экран = pt * PtToPx * zoom, поэтому
            // pt = px / (PtToPx * zoom). Берём максимум и небольшой запас, чтобы не мерцал край.
            double z = Math.Max(Zoom, 0.01);
            float boundsWPt = (float)(Bounds.Width / (PtToPx * z));
            float boundsHPt = (float)(Bounds.Height / (PtToPx * z));
            float bgWPt = Math.Max(canvasWPt, boundsWPt) + 2f;
            float bgHPt = Math.Max(canvasHeightPt, boundsHPt) + 2f;

            canvas.DrawRect(0, 0, bgWPt, bgHPt, _paintCanvasBg);

            // Горизонтальное до-центрирование без пересборки раскладки. pageXPt запечён в позиции
            // абзацев/страниц при последнем пересчёте (под _canvasWidth того момента). Здесь
            // считаем центр по ЖИВОМУ _canvasWidth и сдвигаем весь контент на разницу. Во время
            // зум-жеста (раскладка не пересобирается) это держит лист по центру; когда пересчёт
            // прошёл — _layoutPageXPt совпадает с текущим, сдвиг нулевой. Фон уже залит выше и не
            // сдвигается, поэтому правый край не оголяется.
            float curPageXPt = Math.Max((canvasWPt - GetPageWidthPt()) / 2f, 0f);
            float pageXShiftPt = curPageXPt - _layoutPageXPt;
            if (MathF.Abs(pageXShiftPt) > 0.01f)
                canvas.Translate(pageXShiftPt, 0);

            var (firstPage, lastPage) = GetVisiblePageRange(pages);

            for (int pi = firstPage; pi <= lastPage && pi < pages.Count; pi++)
            {
                var page = pages[pi];
                canvas.DrawRect(page.PadLeftPt + 3, page.Ypt + 3, page.WidthPt, page.HeightPt, _paintPageShadow);
                canvas.DrawRect(page.PadLeftPt, page.Ypt, page.WidthPt, page.HeightPt, _paintPageWhite);
            }

            // Изображения-блоки (рисуются поверх белого листа, в координатах в пунктах).
            // Картинки за текстом (Behind) и блок-картинки (Inline) — рисуются до текста.
            foreach (var ie in images)
            {
                if (ie.PageIndex < firstPage || ie.PageIndex > lastPage) continue;
                var wm = ie.Block.WrapMode;
                if (wm == WrapMode.InFront || wm == WrapMode.Square || wm == WrapMode.Tight) continue;
                var skImg = GetImageBitmap(ie.Block.ImageFileName);
                if (skImg is null) continue;
                canvas.DrawImage(skImg, new SKRect(ie.XPt, ie.Ypt, ie.XPt + ie.WidthPt, ie.Ypt + ie.HeightPt));
            }

            // Рисуем рамки таблиц (без содержимого) — клипуем по правому краю страницы
            foreach (var te in tables)
            {
                if (te.PageIndex < firstPage || te.PageIndex > lastPage) continue;
                // Клип по правому краю страницы: таблица может выходить за край,
                // но видна только в пределах страницы.
                // По вертикали клипуем по полной высоте страницы (включая поля),
                // а не по текстовой зоне — иначе нижняя граница последней ByRow-строки
                // (расположенная точно на textBottom) обрезается исключающим клипом.
                // Корректное отсечение лишних линий обеспечивают suppressBottom/visibleH.
                if (te.PageIndex < pages.Count)
                {
                    var pg = pages[te.PageIndex];
                    float pageRight = pg.PadLeftPt + pg.WidthPt;
                    float pageTop = pg.Ypt;
                    float pageBottom = pg.Ypt + pg.HeightPt;
                    canvas.Save();
                    canvas.ClipRect(new SKRect(0, pageTop, pageRight, pageBottom));
                    RenderTableStructureOnly(canvas, te.Layout, te.XPt, te.Ypt,
                        te.RowFrom, te.RowTo,
                        te.LastRowVisibleHeightPt, te.FirstRowContentOffsetPt,
                        te.IsContinuation);
                    canvas.Restore();
                }
                else
                {
                    RenderTableStructureOnly(canvas, te.Layout, te.XPt, te.Ypt,
                        te.RowFrom, te.RowTo,
                        te.LastRowVisibleHeightPt, te.FirstRowContentOffsetPt,
                        te.IsContinuation);
                }
            }

            for (int i = 0; i < layouts.Count; i++)
            {
                var pl = layouts[i];
                if (pl.PageIndex < firstPage || pl.PageIndex > lastPage) continue;

                // Для ячеек таблицы дополнительно клипуем по правому краю страницы
                // (ячейка может выходить за край, текст должен быть обрезан).
                if (pl.Cell != null && pl.PageIndex < pages.Count)
                {
                    var pg = pages[pl.PageIndex];
                    float pageRight = pg.PadLeftPt + pg.WidthPt;
                    float textTop = pg.Ypt + pg.PadTopPt;
                    float textBottom = pg.Ypt + pg.HeightPt - pg.PadBottomPt;
                    canvas.Save();
                    canvas.ClipRect(new SKRect(0, textTop, pageRight, textBottom));
                    RenderParaLayout(canvas, i, pl, layouts, drawCaret);
                    canvas.Restore();
                }
                else
                {
                    RenderParaLayout(canvas, i, pl, layouts, drawCaret);
                }
            }

            // Картинки поверх текста (InFront / Square / Tight) — рисуются после текста.
            foreach (var ie in images)
            {
                if (ie.PageIndex < firstPage || ie.PageIndex > lastPage) continue;
                var wm = ie.Block.WrapMode;
                if (wm != WrapMode.InFront && wm != WrapMode.Square && wm != WrapMode.Tight) continue;
                var skImg = GetImageBitmap(ie.Block.ImageFileName);
                if (skImg is null) continue;
                canvas.DrawImage(skImg, new SKRect(ie.XPt, ie.Ypt, ie.XPt + ie.WidthPt, ie.Ypt + ie.HeightPt));
            }

            // Рамка выделенной картинки — поверх всего.
            if (_selectedImage is not null)
            {
                foreach (var ie in images)
                {
                    if (!ReferenceEquals(ie.Block, _selectedImage)) continue;
                    if (ie.PageIndex < firstPage || ie.PageIndex > lastPage) continue;
                    canvas.DrawRect(
                        new SKRect(ie.XPt, ie.Ypt, ie.XPt + ie.WidthPt, ie.Ypt + ie.HeightPt),
                        _paintImageSelection);
                }
            }

            // Рисуем выделения всех таблиц из единого словаря.
            foreach (var kv in _tableSelections)
                RenderTableSelection(canvas, tables, kv.Key, kv.Value.sr, kv.Value.sc, kv.Value.er, kv.Value.ec);

            // Полностью попавшие в поток ячейки — заливаем целиком.
            RenderCellFlowFull(canvas, tables);
        }

        // Загружает и кеширует декодированное изображение по имени файла внутри проекта.
        private SKImage? GetImageBitmap(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return null;
            if (_imageCache.TryGetValue(fileName, out var cached)) return cached;

            SKImage? img = null;
            try
            {
                var ctx = Writersword.Core.Services.CoreServices
                    .GetService<Writersword.Core.Interfaces.WorkFlows.ITabCollection>()?.ActiveTab?.Context;
                var bytes = ctx?.ReadFile($"TextEditor/Images/{fileName}");
                if (bytes is { Length: > 0 })
                    img = SKImage.FromEncodedData(bytes);
            }
            catch { img = null; }

            // Кешируем только удачную загрузку: если файл временно отсутствует
            // (например, во время операции), при следующем кадре попробуем снова.
            if (img is not null) _imageCache[fileName] = img;
            return img;
        }

        // Заливает целиком ячейки из _cellFlowFull (полностью попавшие в потоковое выделение).
        // Геометрия — как в RenderTableSelection, но отбор по принадлежности множеству, а не по
        // прямоугольному диапазону строк/колонок.
        private void RenderCellFlowFull(SKCanvas canvas, List<TableEntry> tables)
        {
            if (_cellFlowFull.Count == 0) return;

            foreach (var te in tables)
            {
                int effectiveRowTo = te.RowTo < 0 ? te.Layout.Rows.Count : te.RowTo;
                float rowOffsetY = te.RowFrom > 0 && te.RowFrom < te.Layout.Rows.Count
                    ? te.Layout.Rows[te.RowFrom].Ypt : 0f;

                float maxPadTop = 0f;
                if (te.IsContinuation && te.FirstRowContentOffsetPt > 0f
                    && te.RowFrom < te.Layout.Rows.Count)
                {
                    foreach (var cell in te.Layout.Rows[te.RowFrom].Cells)
                        maxPadTop = Math.Max(maxPadTop, cell.PadTopPt + cell.Borders.Top.WidthPt);
                }

                foreach (var row in te.Layout.Rows)
                {
                    if (row.Row < te.RowFrom || row.Row >= effectiveRowTo) continue;

                    bool isFirstRow = row.Row == te.RowFrom;
                    float rowShift = isFirstRow ? te.FirstRowContentOffsetPt : 0f;
                    float extraShift = isFirstRow ? 0f : (te.FirstRowContentOffsetPt - maxPadTop);
                    float effectiveRowH = isFirstRow
                        ? row.HeightPt - rowShift + maxPadTop
                        : row.HeightPt;
                    float visibleH = (row.Row == effectiveRowTo - 1 && te.LastRowVisibleHeightPt >= 0f)
                        ? te.LastRowVisibleHeightPt
                        : effectiveRowH;

                    foreach (var cell in row.Cells)
                    {
                        if (!_cellFlowFull.Contains((te.Table, row.Row, cell.Column))) continue;
                        float cellX = te.XPt + cell.Xpt;
                        float cellY = te.Ypt + cell.Ypt - rowOffsetY - rowShift - extraShift;
                        canvas.DrawRect(cellX, cellY + rowShift, cell.WidthPt, visibleH, _paintSelection);
                    }
                }
            }
        }

        private void RenderFlowMode(
            SKCanvas canvas,
            EditorViewMode mode,
            List<ParaLayout> layouts,
            List<TableEntry> tables,
            float canvasHeightPt,
            double canvasWidth,
            bool drawCaret)
        {
            float canvasWPt = (float)(canvasWidth * PxToPt);

            canvas.DrawRect(0, 0, canvasWPt, canvasHeightPt, _paintTransparent);

            float zoom2 = (float)Zoom;
            float viewTopPt = (float)(_scrollOffsetY / zoom2 * PxToPt) - FallbackLinePt * 5f;
            float viewBotPt = (float)((_scrollOffsetY + Math.Max(_viewportHeight, 100))
                / zoom2 * PxToPt) + FallbackLinePt * 5f;

            foreach (var te in tables)
            {
                if (te.Ypt + te.Layout.TotalHeightPt < viewTopPt) continue;
                if (te.Ypt > viewBotPt) break;
                RenderTableStructureOnly(canvas, te.Layout, te.XPt, te.Ypt);
            }

            for (int i = 0; i < layouts.Count; i++)
            {
                var pl = layouts[i];
                if (pl.Ypt + pl.HeightPt < viewTopPt) continue;
                if (pl.Ypt > viewBotPt) break;

                RenderParaLayout(canvas, i, pl, layouts, drawCaret);
            }
        }

        /// <summary>
        /// Рисует один параграф (обычный или в ячейке таблицы).
        /// Для ячейки применяет clip-rect.
        /// </summary>
        private void RenderParaLayout(
            SKCanvas canvas, int idx, ParaLayout pl,
            List<ParaLayout> layouts, bool drawCaret)
        {
            float absX = pl.AbsXPt;
            float absY = pl.Ypt;

            bool isCell = pl.Cell != null;

            if (isCell)
            {
                canvas.Save();
                var clip = pl.Cell!;
                canvas.ClipRect(new SKRect(clip.ClipX, clip.ClipY,
                    clip.ClipX + clip.ClipW, clip.ClipY + clip.ClipH));
            }

            var renderLayout = GetRenderLayout(pl, (float)(_canvasWidth * PxToPt));

            SKTextRenderer.RenderParagraphLines(
                canvas, renderLayout,
                absX + renderLayout.LeftIndentPt,
                absY,
                pl.LineFrom, pl.LineTo);

            // Выделение рисуем ПОВЕРХ содержимого (после заливки текста и глифов), иначе
            // непрозрачная заливка HighlightColor перекрывает полупрозрачную подсветку
            // выделения и выделенный текст на залитом фоне становится не виден.
            DrawSelectionForSlice(canvas, idx, pl, absX, absY, layouts, renderLayout);

            if (drawCaret && _caretPara == idx)
                DrawCaret(canvas, pl, absX, absY, renderLayout);

            if (isCell)
                canvas.Restore();
        }

        private void DrawCaretOnCanvas(
            SKCanvas canvas,
            List<ParaLayout> layouts,
            List<PageRect> pages,
            double canvasWidth)
        {
            if (!_caretVisible) return;
            if (_caretPara < 0 || _caretPara >= layouts.Count) return;

            var pl = layouts[_caretPara];
            var caretLayout = GetRenderLayout(pl, (float)(_canvasWidth * PxToPt));
            float xPt = pl.AbsXPt;

            // В page-режиме применяем page-level клип (как RenderPageMode делает для параграфов),
            // иначе каретка на последней строке может выходить за нижнюю рамку таблицы/страницы.
            bool hasPageClip = pages.Count > 0 && pl.PageIndex < pages.Count;
            if (hasPageClip)
            {
                var pg = pages[pl.PageIndex];
                canvas.Save();
                canvas.ClipRect(new SKRect(0, pg.Ypt, pg.PadLeftPt + pg.WidthPt, pg.Ypt + pg.HeightPt));
            }

            bool isCell = pl.Cell != null;
            if (isCell)
            {
                if (!hasPageClip) canvas.Save();
                var c = pl.Cell!;
                canvas.ClipRect(new SKRect(c.ClipX, c.ClipY, c.ClipX + c.ClipW, c.ClipY + c.ClipH));
            }

            DrawCaret(canvas, pl, xPt, pl.Ypt, caretLayout);

            if (isCell || hasPageClip) canvas.Restore();
        }

        private (int first, int last) GetVisiblePageRange(List<PageRect> pages)
        {
            if (pages.Count == 0) return (0, 0);
            double zoom2 = Zoom;
            float viewTopPt = (float)(_scrollOffsetY / zoom2 * PxToPt);
            float viewBotPt = (float)((_scrollOffsetY + Math.Max(_viewportHeight, 100)) / zoom2 * PxToPt);
            float bufferPt = (pages.Count > 0 ? pages[0].HeightPt : 842f) + PageGapPt;
            viewTopPt -= bufferPt;
            viewBotPt += bufferPt;

            int first = 0, last = pages.Count - 1;
            for (int i = 0; i < pages.Count; i++)
                if (pages[i].Ypt + pages[i].HeightPt >= viewTopPt) { first = i; break; }
            for (int i = first; i < pages.Count; i++)
            {
                last = i;
                if (pages[i].Ypt > viewBotPt) break;
            }
            return (first, last);
        }

        private void DrawSelectionForSlice(
            SKCanvas canvas, int sliceIdx, ParaLayout pl,
            float xPt, float yPt, List<ParaLayout> layouts,
            SKTextLayout? resolvedLayout = null)
        {
            var sl = resolvedLayout ?? pl.Layout;
            if (sl is null) return;
            int len = pl.Vm?.PlainText?.Length ?? 0;

            // Потоковое выделение активно: ячейки этой таблицы рисуем строго по потоку —
            // частичные края по тексту (из _cellFlowRanges), полные заливает RenderCellFlowFull,
            // ячейки вне потока не рисуем (иначе линейный путь дал бы полоски).
            if (pl.Cell != null && (_cellFlowRanges.Count > 0 || _cellFlowFull.Count > 0))
            {
                if (pl.Vm?.Model is ParagraphBlock cellModel
                    && _cellFlowRanges.TryGetValue(cellModel, out var flow))
                {
                    int ff = Clamp(flow.from, 0, len);
                    int tt = Clamp(flow.to, 0, len);
                    if (tt > ff || len == 0)
                        DrawSelectionRangeOnSlice(canvas, pl, sl, ff, tt, len, xPt, yPt);
                }
                return;
            }

            // Ячейки при активном cell-range или табличном выделении: рисуется через RenderTableSelection.
            if (pl.Cell != null && (_isCellRangeSelecting || _tableSelections.ContainsKey(pl.Cell.Table))) return;

            if (!HasSel()) return;

            var (sp, sc, ep, ec) = NormalizeSelection();
            if (sliceIdx < sp || sliceIdx > ep) return;

            // Пустые параграфы-якоря вокруг таблиц при активном табличном/потоковом выделении
            // пропускаем — они дают тонкую полосу выделения вплотную к рамке таблицы.
            bool tableSelActive = _tableSelections.Count > 0
                || _cellFlowFull.Count > 0 || _cellFlowRanges.Count > 0;
            if (len == 0 && tableSelActive && pl.Vm?.Model is { } anchorModel
                && (IsBlockBeforeTable(anchorModel) || IsBlockAfterTable(anchorModel)))
                return;

            int from = sliceIdx == sp ? sc : 0;
            int to = sliceIdx == ep ? ec : len;

            from = Clamp(from, 0, len);
            to = Clamp(to, 0, len);
            if (from >= to && !(from == 0 && len == 0)) return;

            DrawSelectionRangeOnSlice(canvas, pl, sl, from, to, len, xPt, yPt);
        }

        // Рисует выделение диапазона [from, to) одного слайса (абзаца) с группировкой по «голубизне»
        // заливки. Вынесено из DrawSelectionForSlice, чтобы использовать и для потокового выделения
        // ячеек, и для обычного выделения.
        private void DrawSelectionRangeOnSlice(
            SKCanvas canvas, ParaLayout pl, SKTextLayout sl,
            int from, int to, int len, float xPt, float yPt)
        {
            if (from == to && len == 0)
            {
                float lineH = sl.Lines.Count > 0 ? sl.Lines[0].Height : FallbackLinePt;
                canvas.DrawRect(xPt, yPt, 5f, lineH, SelectionPaintAt(pl, from));
                return;
            }

            var rects = sl.HitTestRange(from, to);
            if (rects.Count == 0) return;

            // RenderParagraphLines рендерит строки со смещением line.Y - lines[lineFrom].Y,
            // и каретка использует тот же yBase. Подсветка выделения обязана делать так же,
            // иначе на странице продолжения прямоугольники уезжают вниз на высоту строк,
            // оставшихся на предыдущей странице.
            float yBase = pl.LineFrom < sl.Lines.Count ? sl.Lines[pl.LineFrom].Y : 0f;

            foreach (var r in rects)
            {
                if (r.LineIndex < pl.LineFrom || r.LineIndex >= pl.LineTo) continue;
                if (r.LineIndex >= sl.Lines.Count) continue;

                var ln = sl.Lines[r.LineIndex];
                int lineSelStart = Math.Max(from, ln.FirstCharIndex);
                int lineSelEnd = Math.Min(to, ln.LastCharIndex + 1);

                // Хвостовые пробелы в конце визуальной строки не подсвечиваем: обрезаем правый
                // край выделения по последнему непробельному символу строки (как в Word).
                int lastContentEnd = LastContentCharEnd(ln);
                if (lastContentEnd >= 0 && lineSelEnd > lastContentEnd)
                {
                    lineSelEnd = Math.Max(lineSelStart, lastContentEnd);
                    if (lineSelEnd <= lineSelStart) continue;
                }

                // Вырожденный случай (например выделение пустой строки / только переноса):
                // рисуем прямоугольник как есть одной кистью по началу фрагмента.
                if (lineSelEnd <= lineSelStart)
                {
                    DrawSelectionRect(canvas, sl, r.LineIndex, r.Rect.Left, r.Rect.Width,
                        r.Rect.Top, r.Rect.Height, lineSelStart, lineSelEnd,
                        xPt, yPt, yBase, SelectionPaintAt(pl, lineSelStart));
                    continue;
                }

                // Режем выделенный фрагмент строки на группы по «голубизне» заливки и красим
                // каждую своей кистью. Иначе при смене заливки внутри строки (голубое -> белое)
                // весь фрагмент красился бы цветом первого символа — и белый участок выделялся
                // бы янтарным вместо обычного голубого.
                var para = pl.Vm?.Model as ParagraphBlock;
                int segStart = lineSelStart;
                bool curBlue = IsBlueishAt(para, lineSelStart);
                for (int pos = lineSelStart + 1; pos < lineSelEnd; pos++)
                {
                    bool b = IsBlueishAt(para, pos);
                    if (b == curBlue) continue;
                    DrawSelectionSubRange(canvas, sl, r.LineIndex, segStart, pos,
                        xPt, yPt, yBase, curBlue ? _paintSelectionAlt : _paintSelection);
                    segStart = pos;
                    curBlue = b;
                }
                DrawSelectionSubRange(canvas, sl, r.LineIndex, segStart, lineSelEnd,
                    xPt, yPt, yBase, curBlue ? _paintSelectionAlt : _paintSelection);
            }
        }

        // Рисует выделение для под-диапазона [subFrom, subTo) на одной визуальной строке указанной
        // кистью. Координаты считаются так же, как у глифов и каретки (сдвиг выравнивания, абзацный
        // отступ первой строки, накопленная растяжка justify) — чтобы подсветка совпадала с текстом.
        private void DrawSelectionSubRange(
            SKCanvas canvas, SKTextLayout sl, int lineIndex, int subFrom, int subTo,
            float xPt, float yPt, float yBase, SKPaint paint)
        {
            if (subTo <= subFrom) return;
            foreach (var rr in sl.HitTestRange(subFrom, subTo))
            {
                if (rr.LineIndex != lineIndex) continue;
                DrawSelectionRect(canvas, sl, lineIndex, rr.Rect.Left, rr.Rect.Width,
                    rr.Rect.Top, rr.Rect.Height, subFrom, subTo, xPt, yPt, yBase, paint);
            }
        }

        // Общая отрисовка одного прямоугольника выделения с приведением координат к тексту.
        private void DrawSelectionRect(
            SKCanvas canvas, SKTextLayout sl, int lineIndex,
            float rectLeft, float rectWidth, float rectTop, float rectHeight,
            int selFrom, int selTo, float xPt, float yPt, float yBase, SKPaint paint)
        {
            float firstLineBaked = (lineIndex == 0) ? sl.FirstLineIndentPt : 0f;
            float left = xPt + rectLeft - firstLineBaked + LineAlignShift(sl, lineIndex);
            float width = rectWidth;

            float extra = SKTextRenderer.JustifyExtraPerSpace(sl, lineIndex);
            if (extra > 0f && lineIndex < sl.Lines.Count)
            {
                float leftShift = JustifyShiftBeforeChar(sl, lineIndex, selFrom);
                float rightShift = JustifyShiftBeforeChar(sl, lineIndex, selTo);
                left += leftShift;
                width += rightShift - leftShift;
            }

            canvas.DrawRect(left, yPt + (rectTop - yBase), width, rectHeight, paint);
        }

        // Голубая ли заливка в символе с локальным смещением offset.
        private static bool IsBlueishAt(ParagraphBlock? para, int offset)
            => para is not null && IsBlueishHighlight(HighlightAt(para, offset));

        // Выбирает кисть выделения по заливке текста под выделением в данной точке: для
        // голубых/циановых заливок берём контрастную тёплую кисть, иначе обычную голубую.
        private SKPaint SelectionPaintAt(ParaLayout pl, int localOffset)
        {
            if (pl.Vm?.Model is ParagraphBlock para
                && IsBlueishHighlight(HighlightAt(para, localOffset)))
                return _paintSelectionAlt;
            return _paintSelection;
        }

        // Цвет заливки (HighlightColor строкой) в символе с локальным смещением offset, либо null.
        private static string? HighlightAt(ParagraphBlock para, int offset)
        {
            int acc = 0;
            foreach (var chunk in para.Chunks)
            {
                foreach (var run in chunk.Runs)
                {
                    int rl = run.Text?.Length ?? 0;
                    if (offset < acc + rl || rl == 0)
                        return run.Properties?.HighlightColor;
                    acc += rl;
                }
            }
            return null;
        }

        // Голубой/циановый оттенок: синий канал заметно преобладает над красным.
        private static bool IsBlueishHighlight(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return false;
            var s = hex.Trim().TrimStart('#');
            if (s.Length == 8) s = s.Substring(2);      // отбрасываем альфу AARRGGBB
            else if (s.Length == 4) s = s.Substring(1); // короткая ARGB -> RGB ниже
            if (s.Length == 3)
                s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
            if (s.Length != 6) return false;
            try
            {
                int r = Convert.ToInt32(s.Substring(0, 2), 16);
                int g = Convert.ToInt32(s.Substring(2, 2), 16);
                int b = Convert.ToInt32(s.Substring(4, 2), 16);
                return b > 110 && b >= g && (b - r) >= 40;
            }
            catch
            {
                return false;
            }
        }

        private void DrawCaret(SKCanvas canvas, ParaLayout pl, float xPt, float yPt, SKTextLayout? resolvedLayout = null)
        {
            var layout = resolvedLayout ?? pl.Layout;
            if (layout is null) return;
            // Якорный параграф (пустой текст) — например, только что созданный по Enter
            // абзац. HitTestPosition(0) для пустого абзаца возвращает X с учётом левого
            // отступа и отступа первой строки и реальную высоту строки, поэтому каретка
            // встаёт на абзацный отступ, а не у левого края страницы.
            if (string.IsNullOrEmpty(pl.Vm.PlainText))
            {
                var emptyCaret = layout.HitTestPosition(0);
                float emptyYBase = layout.Lines.Count > 0 ? layout.Lines[0].Y : 0f;
                // HitTestPosition не применяет сдвиг выравнивания (он есть только в рендере
                // строк), поэтому для центрированного/правого пустого абзаца добавляем тот же
                // сдвиг вручную. Для пустой строки её ширина = 0, поэтому Center даёт половину
                // текстовой области, Right — всю.
                float alignOffset = layout.Alignment switch
                {
                    Writersword.Core.Models.Rendering.TextAlignment.Center => layout.TextAreaWidthPt / 2f,
                    Writersword.Core.Models.Rendering.TextAlignment.Right => layout.TextAreaWidthPt,
                    _ => 0f
                };
                float ex = xPt + emptyCaret.X + alignOffset;
                float ey = yPt + (emptyCaret.Y - emptyYBase);
                float eh = emptyCaret.Height > 0.01f ? emptyCaret.Height : FallbackLinePt;
                canvas.DrawLine(ex, ey, ex, ey + eh, _paintCaret);
                return;
            }

            int pos = Clamp(_caretChar, 0, pl.Vm.PlainText?.Length ?? 0);

            int drawLineIdx;
            SKCaretRect caret;

            if (_caretLineHint >= 0
                && _caretLineHint >= pl.LineFrom
                && _caretLineHint < Math.Min(pl.LineTo, layout.Lines.Count))
            {
                var hintLine = layout.Lines[_caretLineHint];
                if (pos > hintLine.LastCharIndex && !hintLine.IsLastLine)
                {
                    var lastSeg = hintLine.Segments.Count > 0 ? hintLine.Segments[^1] : null;
                    float hintLineExtra = (_caretLineHint == 0) ? layout.FirstLineIndentPt : 0f;
                    caret = new SKCaretRect
                    {
                        X = lastSeg != null
                            ? layout.LeftIndentPt + hintLineExtra + lastSeg.X + lastSeg.Width
                            : layout.LeftIndentPt + hintLineExtra,
                        Y = hintLine.Y,
                        Height = hintLine.Height,
                        Baseline = hintLine.Baseline
                    };
                    drawLineIdx = _caretLineHint;
                }
                else
                {
                    caret = layout.HitTestPosition(pos);
                    drawLineIdx = _caretLineHint;
                }
            }
            else
            {
                caret = layout.HitTestPosition(pos);
                drawLineIdx = layout.GetLineIndexForChar(pos);
            }

            // Хвостовые пробелы на переносимой (не последней) строке — висячие: каретку прижимаем
            // к концу содержимого строки, чтобы она не уезжала в пустоту за последним словом.
            if (drawLineIdx >= 0 && drawLineIdx < layout.Lines.Count
                && !layout.Lines[drawLineIdx].IsLastLine)
            {
                int contentEnd = LastContentCharEnd(layout.Lines[drawLineIdx]);
                if (contentEnd >= 0 && pos > contentEnd)
                {
                    caret = layout.HitTestPosition(contentEnd);
                    pos = contentEnd;
                }
            }

            // Стык строк: позиция стоит ровно в начале строки, но по смыслу это конец предыдущей
            // переносимой строки с хвостовыми пробелами (например только что напечатали пробел,
            // ставший последним на строке). Рисуем каретку в конце содержимого предыдущей строки,
            // иначе курсор «прыгает» на следующую строку.
            else if (drawLineIdx > 0 && drawLineIdx < layout.Lines.Count
                && pos == layout.Lines[drawLineIdx].FirstCharIndex
                && !layout.Lines[drawLineIdx - 1].IsLastLine)
            {
                int prevContentEnd = LastContentCharEnd(layout.Lines[drawLineIdx - 1]);
                if (prevContentEnd >= 0 && prevContentEnd < pos)
                {
                    caret = layout.HitTestPosition(prevContentEnd);
                    drawLineIdx -= 1;
                    pos = prevContentEnd;
                }
            }

            // RenderParagraphLines рендерит строки со смещением: line.Y - lines[lineFrom].Y.
            // DrawCaret должен использовать тот же yBase, иначе каретка окажется ниже текста
            // на величину lines[lineFrom].Y — что особенно заметно на страницах продолжения.
            float yBase = pl.LineFrom < layout.Lines.Count
                ? layout.Lines[pl.LineFrom].Y : 0f;

            // HitTestPosition даёт X с учётом левого отступа и абзацного отступа первой строки,
            // но БЕЗ сдвига выравнивания. Приводим каретку к тем же координатам, что и рендер
            // глифов: убираем абзацный отступ, уже заложенный HitTestPosition, и прибавляем общий
            // сдвиг строки LineAlignShift (он сам вернёт отступ для левого/по-ширине). Для Justify
            // добавляем накопленную растяжку пробелов до каретки, иначе она отстаёт от текста.
            float firstLineBaked = (drawLineIdx == 0) ? layout.FirstLineIndentPt : 0f;
            float caretAlignOffset = LineAlignShift(layout, drawLineIdx) - firstLineBaked
                + JustifyShiftBeforeChar(layout, drawLineIdx, pos);

            float cx = xPt + caret.X + caretAlignOffset;
            float cy = yPt + (caret.Y - yBase);
            canvas.DrawLine(cx, cy, cx, cy + caret.Height, _paintCaret);
        }

        // Общий сдвиг строки по выравниванию (центр/право + абзацный отступ первой строки для
        // левого/по-ширине). Тот же расчёт, что и в SKTextRenderer.RenderParagraphLines —
        // используется кареткой и хит-тестом, чтобы они совпадали с отрисованным текстом.
        // Общий сдвиг строки по выравниванию — единый расчёт в SKTextRenderer (вордовская
        // модель), чтобы каретка, хит-тест и выделение совпадали с отрисованным текстом.
        private static float LineAlignShift(SKTextLayout layout, int lineIndex)
            => SKTextRenderer.LineAlignShift(layout, lineIndex);

        // Преобразует X клика (в координатах с растяжкой justify, относительно начала строки)
        // в X без растяжки — для передачи в HitTestPoint, который считает по нерастянутым
        // координатам сегментов. Для не-Justify/последней строки возвращает X как есть.
        // Идёт по сегментам, накапливая растяжку как при отрисовке: если клик попал на сегмент —
        // отдаёт его нерастянутую позицию; если в раздвинутый зазор между словами — к началу
        // сегмента (ближайшая граница слова).
        private static float UnstretchJustifyX(SKTextLayout layout, int lineIndex, float stretchedX)
        {
            float extra = SKTextRenderer.JustifyExtraPerSpace(layout, lineIndex);
            if (extra <= 0f) return stretchedX;
            if (lineIndex < 0 || lineIndex >= layout.Lines.Count) return stretchedX;
            var line = layout.Lines[lineIndex];

            float cumStretch = 0f;
            foreach (var seg in line.Segments)
            {
                float stretchedLeft = seg.X + cumStretch;
                if (stretchedX < stretchedLeft)
                    return seg.X;
                if (stretchedX <= stretchedLeft + seg.Width)
                    return seg.X + (stretchedX - stretchedLeft);

                int spaces = 0;
                foreach (var c in seg.Text)
                    if (c == ' ' || c == '\t') spaces++;
                cumStretch += spaces * extra;
            }
            return line.TextWidth;
        }

        // Накопленная добавка растяжки по ширине для пробелов строки, расположенных до символа
        // globalCharIndex. Хвостовые пробелы строки исключаются — как и в JustifyExtraPerSpace,
        // иначе их (несуществующая) растяжка уводит каретку и выделение за правый край.
        // Для не-Justify и последней строки даёт 0.
        private static float JustifyShiftBeforeChar(SKTextLayout layout, int lineIndex, int globalCharIndex)
        {
            float extra = SKTextRenderer.JustifyExtraPerSpace(layout, lineIndex);
            if (extra <= 0f) return 0f;
            var line = layout.Lines[lineIndex];

            // Граница последнего слова: пробелы за ней (хвостовые) растяжки не получают.
            int lastWordEnd = -1;
            foreach (var s in line.Segments)
                for (int k = 0; k < s.Text.Length; k++)
                {
                    char c = s.Text[k];
                    if (c != ' ' && c != '\t') lastWordEnd = s.GlobalCharOffset + k + 1;
                }
            if (lastWordEnd < 0) return 0f;

            int limit = Math.Min(globalCharIndex, lastWordEnd);
            int spacesBefore = 0;
            foreach (var s in line.Segments)
                for (int k = 0; k < s.Text.Length; k++)
                {
                    if (s.GlobalCharOffset + k >= limit) return spacesBefore * extra;
                    char c = s.Text[k];
                    if (c == ' ' || c == '\t') spacesBefore++;
                }
            return spacesBefore * extra;
        }

        // Глобальный индекс сразу за последним непробельным символом визуальной строки.
        // Возвращает -1, если на строке нет непробельных символов (пустая строка/только пробелы).
        private static int LastContentCharEnd(SKLineLayout line)
        {
            int last = -1;
            foreach (var s in line.Segments)
                for (int k = 0; k < s.Text.Length; k++)
                {
                    char c = s.Text[k];
                    if (c != ' ' && c != '\t') last = s.GlobalCharOffset + k + 1;
                }
            return last;
        }

    }
}