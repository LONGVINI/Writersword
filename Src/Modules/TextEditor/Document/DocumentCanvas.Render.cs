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

            // Фоновая подложка-скелет: серый фон холста и пустые белые листы на всю видимую
            // область. Рисуется до всех веток (кэш-блит и полный рендер), поэтому при быстром
            // скролле, когда контентный снимок не успевает за компоновочным сдвигом ScrollViewer,
            // под открывшейся областью уже лежат белые листы, а не прозрачность с фоном
            // ScrollViewer. Контентный снимок кладётся поверх и перекрывает скелет там, где
            // текст и картинки уже готовы.
            {
                var skeletonMode = DocVm?.ViewMode ?? EditorViewMode.Draft;
                canvas.Save();
                canvas.Scale(scale, scale);
                if (skeletonMode == EditorViewMode.Page)
                {
                    RenderPageSkeleton(canvas, pages, canvasHeightPt, canvasWidth);
                }
                else
                {
                    double zBg = Math.Max(Zoom, 0.01);
                    float bgWPt = (float)(Bounds.Width / (PtToPx * zBg)) + 2f;
                    float bgHPt = Math.Max(canvasHeightPt,
                        (float)(Bounds.Height / (PtToPx * zBg))) + 2f;
                    canvas.DrawRect(0, 0, bgWPt, bgHPt, _paintCanvasBg);
                }
                canvas.Restore();
            }

            if (_caretOnlyRedraw && !_contentDirty)
            {
                // Проверка валидности и DrawImage выполняются ПОД ОДНИМ локом:
                // раньше ссылка копировалась под локом, а рисование шло вне его,
                // и параллельное освобождение снимка (detach канваса с UI-потока)
                // попадало в середину DrawImage — access violation в SkiaSharp.
                // Лок приватный и удерживается только на время блита закэшированной
                // GPU-текстуры — конкуренция минимальна.
                bool drewFromCache = false;
                lock (_bitmapLock)
                {
                    // Кеш валиден если scroll находится внутри overscan-диапазона снимка.
                    // _lastFullRenderScrollY хранит bitmapTopY — верхний край последнего
                    // рендера. Если пользователь проскроллил так что viewport ещё внутри
                    // снимка — переиспользуем его без перерисовки. DrawImage иммутабельного
                    // снимка не копирует пиксели: GPU держит текстуру в кэше по uniqueID.
                    bool scrollInRange = _displayImage is not null
                        && _bitmapW == pixelW
                        && scrollY >= _lastFullRenderScrollY - 0.5f
                        && scrollY + viewportPx <= _lastFullRenderScrollY + _bitmapH + 0.5f;

                    if (scrollInRange)
                    {
                        // Снимок рисуем по его реальному bitmapTopY (не scrollY).
                        canvas.DrawImage(_displayImage!, 0, _lastFullRenderScrollY);
                        drewFromCache = true;
                    }
                }

                if (drewFromCache)
                {
                    _caretOnlyRedraw = false;

                    if (CaretDrawable)
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

            // Списанные снимки освобождаются здесь же — на render-потоке рендеры
            // сериализованы, и снимок из очереди гарантированно никем не рисуется.
            while (_imageDisposeQueue.TryDequeue(out var staleImage))
                staleImage?.Dispose();

            // Получаем или создаём render-bitmap нужного размера.
            // Если размер не изменился — переиспользуем существующий (0 аллокаций).
            // Если изменился — создаём новый и откладываем старый в очередь.
            SKBitmap? renderTarget;
            lock (_bitmapLock)
            {
                if (_renderBitmap is null || _renderBitmap.Width != pixelW || _renderBitmap.Height != pixelH)
                {
                    if (_renderBitmap is not null) _bitmapDisposeQueue.Enqueue(_renderBitmap);
                    _renderBitmap = new SKBitmap(pixelW, pixelH, SKColorType.Bgra8888, SKAlphaType.Premul);
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

                // Снимаем иммутабельный снимок (одна копия пикселей за содержательный
                // рендер). Все последующие кадры — мигание каретки, чужие инвалидации
                // окна, скролл в пределах overscan — рисуют этот снимок без копирования:
                // GPU кэширует его текстуру по uniqueID.
                // Именно FromPixelCopy, а не FromBitmap: FromBitmap заворачивает ту же
                // память без смены generation ID, и GPU-кэш продолжает отдавать старую
                // текстуру — на экране остаётся прежний кадр, хотя битмап уже перерисован.
                var newImage = SKImage.FromPixelCopy(
                    new SKImageInfo(renderTarget.Width, renderTarget.Height,
                        SKColorType.Bgra8888, SKAlphaType.Premul),
                    renderTarget.GetPixels(),
                    renderTarget.RowBytes);
                SKImage? oldImage;
                lock (_bitmapLock)
                {
                    oldImage = _displayImage;
                    _displayImage = newImage;
                    // Сохраняем bitmapTopY — верхний край отрисованного снимка.
                    // Используется в cache check: scroll внутри [bitmapTopY, bitmapTopY+H]?
                    _lastFullRenderScrollY = bitmapTopY;
                }
                // Рендер выполняется только на render-треде, старый снимок в этом кадре
                // уже никем не используется — освобождаем сразу.
                oldImage?.Dispose();

                // Полный рендер выполнен — быстрые пути снова могут рисовать из кэша.
                _contentDirty = false;

                if (newImage is not null)
                    canvas.DrawImage(newImage, 0, bitmapTopY);

                if (CaretDrawable)
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
                    RenderPageMode(canvas, layouts, pages, tables, images, canvasHeightPt, canvasWidth, CaretDrawable);
                else
                    RenderFlowMode(canvas, mode, layouts, tables, canvasHeightPt, canvasWidth, CaretDrawable);
                canvas.Restore();
                _contentDirty = false;
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

        // Рисует «скелет» страниц (серый фон холста + белые листы с тенью) прямо в
        // lease-канвас как фоновую подложку под контентным битмапом. При быстром скролле
        // полоса overscan не успевает за компоновочным сдвигом ScrollViewer, и в открывшейся
        // области раньше просвечивал фон ScrollViewer (коричневое). Скелет закрывает всю
        // видимую площадь пустыми белыми листами на нейтральном сером фоне — глаз не режет,
        // а реальный текст и картинки дорисовываются поверх из контентного снимка.
        // Отрисовка ограничена диапазоном ±3 вьюпорта вокруг прокрутки, чтобы на больших
        // документах не перебирать все страницы на каждом кадре.
        private void RenderPageSkeleton(
            SKCanvas canvas,
            List<PageRect> pages,
            float canvasHeightPt,
            double canvasWidth)
        {
            float canvasWPt = (float)(canvasWidth * PxToPt);

            double z = Math.Max(Zoom, 0.01);
            float boundsWPt = (float)(Bounds.Width / (PtToPx * z));
            float boundsHPt = (float)(Bounds.Height / (PtToPx * z));
            float bgWPt = Math.Max(canvasWPt, boundsWPt) + 2f;
            float bgHPt = Math.Max(canvasHeightPt, boundsHPt) + 2f;

            canvas.DrawRect(0, 0, bgWPt, bgHPt, _paintCanvasBg);

            if (pages is null || pages.Count == 0) return;

            float scale = (float)(PtToPx * z);
            float scrollTopPt = scale > 0f ? (float)_scrollOffsetY / scale : 0f;
            float viewPt = scale > 0f
                ? (float)(_viewportHeight > 0 ? _viewportHeight : Bounds.Height) / scale
                : 0f;
            float margin = viewPt * 3f;
            float loPt = scrollTopPt - margin;
            float hiPt = scrollTopPt + viewPt + margin;

            float curPageXPt = Math.Max((canvasWPt - GetPageWidthPt()) / 2f, 0f);
            float pageXShiftPt = curPageXPt - _layoutPageXPt;

            if (_pagesPerRow <= 1)
            {
                canvas.Save();
                if (MathF.Abs(pageXShiftPt) > 0.01f)
                    canvas.Translate(pageXShiftPt, 0);
                for (int pi = 0; pi < pages.Count; pi++)
                {
                    var page = pages[pi];
                    if (page.Ypt + page.HeightPt < loPt || page.Ypt > hiPt) continue;
                    canvas.DrawRect(page.PadLeftPt + 3, page.Ypt + 3, page.WidthPt, page.HeightPt, _paintPageShadow);
                    canvas.DrawRect(page.PadLeftPt, page.Ypt, page.WidthPt, page.HeightPt, _paintPageWhite);
                }
                canvas.Restore();
                return;
            }

            for (int pi = 0; pi < pages.Count; pi++)
            {
                var page = pages[pi];
                var (dxp, dyp) = PageVisualDelta(pi, pages);
                float visX = page.PadLeftPt + dxp;
                float visY = page.Ypt + dyp;
                if (visY + page.HeightPt < loPt || visY > hiPt) continue;
                canvas.DrawRect(visX + 3, visY + 3, page.WidthPt, page.HeightPt, _paintPageShadow);
                canvas.DrawRect(visX, visY, page.WidthPt, page.HeightPt, _paintPageWhite);
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
            if (_pagesPerRow <= 1 && MathF.Abs(pageXShiftPt) > 0.01f)
                canvas.Translate(pageXShiftPt, 0);

            var (firstPage, lastPage) = GetVisiblePageRange(pages);

            if (_pagesPerRow <= 1)
            {
                for (int pi = firstPage; pi <= lastPage && pi < pages.Count; pi++)
                {
                    var page = pages[pi];
                    canvas.DrawRect(page.PadLeftPt + 3, page.Ypt + 3, page.WidthPt, page.HeightPt, _paintPageShadow);
                    canvas.DrawRect(page.PadLeftPt, page.Ypt, page.WidthPt, page.HeightPt, _paintPageWhite);
                }

                RenderPageContent(canvas, layouts, pages, tables, images, firstPage, lastPage, drawCaret);
                return;
            }

            // Страницы рядом: контент каждой страницы переносится на её визуальную
            // позицию (клип по листу с запасом + трансляция). Логические координаты
            // раскладки не меняются — весь режим живёт только в отображении.
            //
            // Листы рисуются все сразу, отдельным проходом до контента: контент страницы
            // может выходить за её пределы (картинка мимо листа), и белый лист соседа,
            // нарисованный позже, закрасил бы уже нарисованное поверх него.
            for (int pi = firstPage; pi <= lastPage && pi < pages.Count; pi++)
            {
                var bgPage = pages[pi];
                var (bgDx, bgDy) = PageVisualDelta(pi, pages);
                float bgX = bgPage.PadLeftPt + bgDx;
                float bgY = bgPage.Ypt + bgDy;

                canvas.DrawRect(bgX + 3, bgY + 3, bgPage.WidthPt, bgPage.HeightPt, _paintPageShadow);
                canvas.DrawRect(bgX, bgY, bgPage.WidthPt, bgPage.HeightPt, _paintPageWhite);
            }

            for (int pi = firstPage; pi <= lastPage && pi < pages.Count; pi++)
            {
                var page = pages[pi];
                var (dxp, dyp) = PageVisualDelta(pi, pages);
                float visX = page.PadLeftPt + dxp;
                float visY = page.Ypt + dyp;

                var pageClipRect = new SKRect(
                    visX - PageGapPt, visY - PageGapPt,
                    visX + page.WidthPt + PageGapPt, visY + page.HeightPt + PageGapPt);

                // Картинка, промахнувшаяся мимо своего листа, рисуется целиком, бледной и
                // заштрихованной: объект в документе есть, и его должно быть видно, чтобы
                // выделить и вернуть. В одностраничном виде так и происходит, а здесь её
                // резал клип листа — на экране оставалась полоска шириной в межстраничный
                // зазор. Клип расширяется до габарита таких картинок вместе с их рамкой
                // выделения и ручками.
                foreach (var ie in images)
                {
                    if (ie.InLine || ie.PageIndex != pi) continue;
                    if (ie.Block.WrapMode == WrapMode.Inline) continue;
                    if (!IsImageOffItsPage(ie, page)) continue;

                    double offRad = ie.Block.RotationDeg * Math.PI / 180.0;
                    float offCos = (float)Math.Abs(Math.Cos(offRad));
                    float offSin = (float)Math.Abs(Math.Sin(offRad));
                    float offBoxW = ie.WidthPt * offCos + ie.HeightPt * offSin;
                    float offBoxH = ie.WidthPt * offSin + ie.HeightPt * offCos;
                    float offCx = ie.XPt + ie.WidthPt / 2f + dxp;
                    float offCy = ie.Ypt + ie.HeightPt / 2f + dyp;

                    // Запас на рамку выделения и ручки размера по краям габарита.
                    const float OffPageClipPadPt = 12f;
                    float offLeft = offCx - offBoxW / 2f - OffPageClipPadPt;
                    float offTop = offCy - offBoxH / 2f - OffPageClipPadPt;
                    float offRight = offCx + offBoxW / 2f + OffPageClipPadPt;
                    float offBottom = offCy + offBoxH / 2f + OffPageClipPadPt;

                    if (offLeft < pageClipRect.Left) pageClipRect.Left = offLeft;
                    if (offTop < pageClipRect.Top) pageClipRect.Top = offTop;
                    if (offRight > pageClipRect.Right) pageClipRect.Right = offRight;
                    if (offBottom > pageClipRect.Bottom) pageClipRect.Bottom = offBottom;
                }

                canvas.Save();
                canvas.ClipRect(pageClipRect);
                canvas.Translate(dxp, dyp);
                RenderPageContent(canvas, layouts, pages, tables, images, pi, pi, drawCaret);
                canvas.Restore();
            }
        }

        // Контентный проход страниц [firstPage..lastPage] в логических координатах:
        // картинки за текстом, рамки таблиц, параграфы, картинки поверх текста,
        // рамка выделенной картинки, табличные выделения и потоковые заливки ячеек.
        private void RenderPageContent(
            SKCanvas canvas,
            List<ParaLayout> layouts,
            List<PageRect> pages,
            List<TableEntry> tables,
            List<ImageEntry> images,
            int firstPage,
            int lastPage,
            bool drawCaret)
        {
            // Отрисовка встроенных в строку картинок: рендер текста статический и
            // общий для всех канвасов, поэтому обработчик ставится перед каждым
            // проходом — он замкнут на документ именно этого канваса.
            SKTextRenderer.DrawInlineObject = DrawInlineImageSegment;

            // Изображения-блоки (рисуются поверх белого листа, в координатах в пунктах).
            // Картинки за текстом (Behind) и блок-картинки (Inline) — рисуются до текста.
            foreach (var ie in images)
            {
                if (ie.PageIndex < firstPage || ie.PageIndex > lastPage) continue;
                // Картинку в строке рисует рендер текста на своём месте в строке —
                // здесь она была бы нарисована второй раз, поверх текста.
                if (ie.InLine) continue;
                var wm = ie.Block.WrapMode;
                if (wm == WrapMode.InFront || wm == WrapMode.Square || wm == WrapMode.Tight) continue;
                var skImg = GetImageBitmap(ie.Block.ImageFileName);
                if (skImg is null) continue;
                // Клип по прямоугольнику своей страницы: часть картинки за пределами
                // листа (в межстраничном зазоре или за краем) обрезается. Предпросмотр
                // переполнения не клипуем — серая часть под листом должна быть видна.
                // Клип работает и во время перетаскивания: по обрезу краем листа сразу
                // видно, какой странице картинка принадлежит и когда она на неё перешла.
                // Без этого страницу приходилось угадывать и узнавать только на отпускании.
                //
                // Снимается он только для картинки, целиком ушедшей мимо своего листа:
                // клип спрятал бы её полностью — объект в документе есть, а на экране
                // его нет, ни найти, ни выделить, ни вернуть. Такая рисуется бледной
                // и заштрихованной.
                bool imgOffPage = ie.PageIndex < pages.Count
                    && IsImageOffItsPage(ie, pages[ie.PageIndex]);

                bool imgClip = ie.PageIndex < pages.Count
                    && !imgOffPage
                    && !(_imageOverflowPreviewMode
                         && ReferenceEquals(ie.Block, _imageOverflowPreviewBlock));
                if (imgClip)
                {
                    var pgc = pages[ie.PageIndex];
                    canvas.Save();
                    canvas.ClipRect(new SKRect(
                        pgc.PadLeftPt, pgc.Ypt,
                        pgc.PadLeftPt + pgc.WidthPt, pgc.Ypt + pgc.HeightPt));
                }
                float rotDeg = (float)ie.Block.RotationDeg;
                float imgCx = ie.XPt + ie.WidthPt / 2f;
                float imgCy = ie.Ypt + ie.HeightPt / 2f;
                bool hasXform = rotDeg != 0f || ie.Block.FlipHorizontal || ie.Block.FlipVertical;
                if (hasXform)
                {
                    canvas.Save();
                    if (rotDeg != 0f) canvas.RotateDegrees(rotDeg, imgCx, imgCy);
                    if (ie.Block.FlipHorizontal || ie.Block.FlipVertical)
                        canvas.Scale(
                            ie.Block.FlipHorizontal ? -1f : 1f,
                            ie.Block.FlipVertical ? -1f : 1f,
                            imgCx, imgCy);
                }
                var imgPaint = _imageOverflowPreviewMode
                    && ReferenceEquals(ie.Block, _imageOverflowPreviewBlock)
                    ? _paintImageDrawOverflow
                    : _paintImageDraw;
                // Непрозрачность картинки: альфа paint-а модулирует пиксели при отрисовке.
                double imgOpacity = imgOffPage ? ie.Block.Opacity * 0.45 : ie.Block.Opacity;
                byte imgAlpha = (byte)Math.Clamp(imgOpacity * 255.0, 0.0, 255.0);
                _paintImageDraw.Color = new SKColor(0xFF, 0xFF, 0xFF, imgAlpha);
                var imgRect = new SKRect(ie.XPt, ie.Ypt, ie.XPt + ie.WidthPt, ie.Ypt + ie.HeightPt);
                // Кадрирование: рисуется только видимая часть исходного изображения.
                float srcW = skImg.Width;
                float srcH = skImg.Height;
                var srcRect = new SKRect(
                    srcW * (float)Math.Clamp(ie.Block.CropLeftFrac, 0.0, 0.95),
                    srcH * (float)Math.Clamp(ie.Block.CropTopFrac, 0.0, 0.95),
                    srcW * (float)(1.0 - Math.Clamp(ie.Block.CropRightFrac, 0.0, 0.95)),
                    srcH * (float)(1.0 - Math.Clamp(ie.Block.CropBottomFrac, 0.0, 0.95)));
                if (srcRect.Right <= srcRect.Left + 1f) srcRect.Right = srcRect.Left + 1f;
                if (srcRect.Bottom <= srcRect.Top + 1f) srcRect.Bottom = srcRect.Top + 1f;
                canvas.DrawImage(skImg, srcRect, imgRect, _imageSampling, imgPaint);
                _paintImageDraw.Color = new SKColor(0xFF, 0xFF, 0xFF, 0xFF);
                // Рамка картинки — в той же системе координат (поворот + отражение).
                if (ie.Block.BorderThicknessPt > 0.0
                    && !string.IsNullOrEmpty(ie.Block.BorderColor)
                    && SKColor.TryParse(ie.Block.BorderColor, out var borderColor)
                    && borderColor.Alpha > 0)
                {
                    _paintImageBorderDraw.Color = borderColor.WithAlpha((byte)(borderColor.Alpha * imgAlpha / 255));
                    _paintImageBorderDraw.StrokeWidth = (float)ie.Block.BorderThicknessPt;
                    canvas.DrawRect(imgRect, _paintImageBorderDraw);
                }

                // Картинка попала в текстовое выделение — заливаем той же кистью, что и текст.
                // Иначе выделение «проходит сквозь» картинку: пользователь не видит, что она
                // тоже будет скопирована и удалена.
                if (_imagesInTextSelection.Contains(ie.Block))
                    canvas.DrawRect(imgRect, _paintSelection);

                // Картинка целиком мимо своего листа: она ничего не делает и не печатается.
                // Помечаем красной штриховкой поверх бледной картинки — видно, что объект
                // есть и где он лежит, но что он вне страницы.
                if (imgOffPage) DrawOffPageHatch(canvas, imgRect);

                if (hasXform) canvas.Restore();
                if (imgClip) canvas.Restore();
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
                if (ie.InLine) continue;
                var wm = ie.Block.WrapMode;
                if (wm != WrapMode.InFront && wm != WrapMode.Square && wm != WrapMode.Tight) continue;
                var skImg = GetImageBitmap(ie.Block.ImageFileName);
                if (skImg is null) continue;
                // Клип по прямоугольнику своей страницы: часть картинки за пределами
                // листа (в межстраничном зазоре или за краем) обрезается. Предпросмотр
                // переполнения не клипуем — серая часть под листом должна быть видна.
                // Клип работает и во время перетаскивания: по обрезу краем листа сразу
                // видно, какой странице картинка принадлежит и когда она на неё перешла.
                // Без этого страницу приходилось угадывать и узнавать только на отпускании.
                //
                // Снимается он только для картинки, целиком ушедшей мимо своего листа:
                // клип спрятал бы её полностью — объект в документе есть, а на экране
                // его нет, ни найти, ни выделить, ни вернуть. Такая рисуется бледной
                // и заштрихованной.
                bool imgOffPage = ie.PageIndex < pages.Count
                    && IsImageOffItsPage(ie, pages[ie.PageIndex]);

                bool imgClip = ie.PageIndex < pages.Count
                    && !imgOffPage
                    && !(_imageOverflowPreviewMode
                         && ReferenceEquals(ie.Block, _imageOverflowPreviewBlock));
                if (imgClip)
                {
                    var pgc = pages[ie.PageIndex];
                    canvas.Save();
                    canvas.ClipRect(new SKRect(
                        pgc.PadLeftPt, pgc.Ypt,
                        pgc.PadLeftPt + pgc.WidthPt, pgc.Ypt + pgc.HeightPt));
                }
                float rotDeg = (float)ie.Block.RotationDeg;
                float imgCx = ie.XPt + ie.WidthPt / 2f;
                float imgCy = ie.Ypt + ie.HeightPt / 2f;
                bool hasXform = rotDeg != 0f || ie.Block.FlipHorizontal || ie.Block.FlipVertical;
                if (hasXform)
                {
                    canvas.Save();
                    if (rotDeg != 0f) canvas.RotateDegrees(rotDeg, imgCx, imgCy);
                    if (ie.Block.FlipHorizontal || ie.Block.FlipVertical)
                        canvas.Scale(
                            ie.Block.FlipHorizontal ? -1f : 1f,
                            ie.Block.FlipVertical ? -1f : 1f,
                            imgCx, imgCy);
                }
                var imgPaint = _imageOverflowPreviewMode
                    && ReferenceEquals(ie.Block, _imageOverflowPreviewBlock)
                    ? _paintImageDrawOverflow
                    : _paintImageDraw;
                // Непрозрачность картинки: альфа paint-а модулирует пиксели при отрисовке.
                double imgOpacity = imgOffPage ? ie.Block.Opacity * 0.45 : ie.Block.Opacity;
                byte imgAlpha = (byte)Math.Clamp(imgOpacity * 255.0, 0.0, 255.0);
                _paintImageDraw.Color = new SKColor(0xFF, 0xFF, 0xFF, imgAlpha);
                var imgRect = new SKRect(ie.XPt, ie.Ypt, ie.XPt + ie.WidthPt, ie.Ypt + ie.HeightPt);
                // Кадрирование: рисуется только видимая часть исходного изображения.
                float srcW = skImg.Width;
                float srcH = skImg.Height;
                var srcRect = new SKRect(
                    srcW * (float)Math.Clamp(ie.Block.CropLeftFrac, 0.0, 0.95),
                    srcH * (float)Math.Clamp(ie.Block.CropTopFrac, 0.0, 0.95),
                    srcW * (float)(1.0 - Math.Clamp(ie.Block.CropRightFrac, 0.0, 0.95)),
                    srcH * (float)(1.0 - Math.Clamp(ie.Block.CropBottomFrac, 0.0, 0.95)));
                if (srcRect.Right <= srcRect.Left + 1f) srcRect.Right = srcRect.Left + 1f;
                if (srcRect.Bottom <= srcRect.Top + 1f) srcRect.Bottom = srcRect.Top + 1f;
                canvas.DrawImage(skImg, srcRect, imgRect, _imageSampling, imgPaint);
                _paintImageDraw.Color = new SKColor(0xFF, 0xFF, 0xFF, 0xFF);
                // Рамка картинки — в той же системе координат (поворот + отражение).
                if (ie.Block.BorderThicknessPt > 0.0
                    && !string.IsNullOrEmpty(ie.Block.BorderColor)
                    && SKColor.TryParse(ie.Block.BorderColor, out var borderColor)
                    && borderColor.Alpha > 0)
                {
                    _paintImageBorderDraw.Color = borderColor.WithAlpha((byte)(borderColor.Alpha * imgAlpha / 255));
                    _paintImageBorderDraw.StrokeWidth = (float)ie.Block.BorderThicknessPt;
                    canvas.DrawRect(imgRect, _paintImageBorderDraw);
                }

                // Картинка попала в текстовое выделение — заливаем той же кистью, что и текст.
                // Иначе выделение «проходит сквозь» картинку: пользователь не видит, что она
                // тоже будет скопирована и удалена.
                if (_imagesInTextSelection.Contains(ie.Block))
                    canvas.DrawRect(imgRect, _paintSelection);

                // Картинка целиком мимо своего листа: она ничего не делает и не печатается.
                // Помечаем красной штриховкой поверх бледной картинки — видно, что объект
                // есть и где он лежит, но что он вне страницы.
                if (imgOffPage) DrawOffPageHatch(canvas, imgRect);

                if (hasXform) canvas.Restore();
                if (imgClip) canvas.Restore();
            }

            // Предпросмотр обрезки — поверх всего: исходная картинка целиком,
            // срезаемые края затемнены, рамка кадрирования с маркерами. Картинка в
            // документе при этом не тронута: срез применяется при выходе из режима.
            if (_imageCropMode && _cropImage is not null)
            {
                foreach (var ie in images)
                {
                    if (!ReferenceEquals(ie.Block, _cropImage)) continue;
                    if (ie.PageIndex < firstPage || ie.PageIndex > lastPage) continue;
                    if (!TryGetCropRects(ie, out var fullRect, out var pendRect)) continue;

                    float visCx = ie.XPt + ie.WidthPt / 2f;
                    float visCy = ie.Ypt + ie.HeightPt / 2f;
                    float cropRot = (float)ie.Block.RotationDeg;

                    canvas.Save();
                    if (cropRot != 0f) canvas.RotateDegrees(cropRot, visCx, visCy);
                    if (ie.Block.FlipHorizontal || ie.Block.FlipVertical)
                        canvas.Scale(
                            ie.Block.FlipHorizontal ? -1f : 1f,
                            ie.Block.FlipVertical ? -1f : 1f,
                            visCx, visCy);

                    var cropImg = GetImageBitmap(ie.Block.ImageFileName);
                    if (cropImg is not null)
                    {
                        _paintImageDraw.Color = new SKColor(0xFF, 0xFF, 0xFF, 0xFF);
                        canvas.DrawImage(cropImg,
                            new SKRect(0f, 0f, cropImg.Width, cropImg.Height),
                            fullRect, _imageSampling, _paintImageDraw);
                    }

                    // Затемняем то, что уйдёт под нож.
                    if (pendRect.Top > fullRect.Top)
                        canvas.DrawRect(new SKRect(fullRect.Left, fullRect.Top, fullRect.Right, pendRect.Top), _paintCropDim);
                    if (pendRect.Bottom < fullRect.Bottom)
                        canvas.DrawRect(new SKRect(fullRect.Left, pendRect.Bottom, fullRect.Right, fullRect.Bottom), _paintCropDim);
                    if (pendRect.Left > fullRect.Left)
                        canvas.DrawRect(new SKRect(fullRect.Left, pendRect.Top, pendRect.Left, pendRect.Bottom), _paintCropDim);
                    if (pendRect.Right < fullRect.Right)
                        canvas.DrawRect(new SKRect(pendRect.Right, pendRect.Top, fullRect.Right, pendRect.Bottom), _paintCropDim);

                    canvas.DrawRect(fullRect, _paintCropOutline);
                    canvas.DrawRect(pendRect, _paintImageSelection);

                    float pcx = (pendRect.Left + pendRect.Right) / 2f;
                    float pcy = (pendRect.Top + pendRect.Bottom) / 2f;
                    DrawImageHandle(canvas, pendRect.Left, pendRect.Top);
                    DrawImageHandle(canvas, pendRect.Right, pendRect.Top);
                    DrawImageHandle(canvas, pendRect.Right, pendRect.Bottom);
                    DrawImageHandle(canvas, pendRect.Left, pendRect.Bottom);
                    DrawImageHandle(canvas, pcx, pendRect.Top);
                    DrawImageHandle(canvas, pendRect.Right, pcy);
                    DrawImageHandle(canvas, pcx, pendRect.Bottom);
                    DrawImageHandle(canvas, pendRect.Left, pcy);

                    canvas.Restore();
                }
            }
            // Рамка выделенной картинки — поверх всего.
            else if (_selectedImage is not null)
            {
                foreach (var ie in images)
                {
                    if (!ReferenceEquals(ie.Block, _selectedImage)) continue;
                    if (ie.PageIndex < firstPage || ie.PageIndex > lastPage) continue;

                    float l = ie.XPt, t = ie.Ypt;
                    float r = ie.XPt + ie.WidthPt, b = ie.Ypt + ie.HeightPt;
                    float cx = (l + r) / 2f, cy = (t + b) / 2f;
                    float rotDeg = (float)ie.Block.RotationDeg;

                    canvas.Save();
                    if (rotDeg != 0f) canvas.RotateDegrees(rotDeg, cx, cy);

                    canvas.DrawRect(new SKRect(l, t, r, b), _paintImageSelection);

                    // Угловые маркеры изменения размера.
                    DrawImageHandle(canvas, l, t);
                    DrawImageHandle(canvas, r, t);
                    DrawImageHandle(canvas, r, b);
                    DrawImageHandle(canvas, l, b);

                    // Боковые маркеры изменения размера.
                    DrawImageHandle(canvas, cx, t);
                    DrawImageHandle(canvas, r, cy);
                    DrawImageHandle(canvas, cx, b);
                    DrawImageHandle(canvas, l, cy);

                    // Маркер поворота: значок круговой стрелки над верхней гранью,
                    // соединённый линией с рамкой.
                    canvas.DrawLine(cx, t, cx, t - ImageRotateHandleOffsetPt + ImageRotateHandleRadiusPt, _paintImageSelection);
                    DrawRotateHandle(canvas, cx, t - ImageRotateHandleOffsetPt);

                    canvas.Restore();
                }
            }

            // Рисуем выделения всех таблиц из единого словаря.
            foreach (var kv in _tableSelections)
                RenderTableSelection(canvas, tables, kv.Key, kv.Value.sr, kv.Value.sc, kv.Value.er, kv.Value.ec);

            // Полностью попавшие в поток ячейки — заливаем целиком.
            RenderCellFlowFull(canvas, tables);
        }

        // Кисти предупреждения: картинка лежит мимо своего листа.
        private static readonly SKPaint _paintOffPageHatch = new()
        {
            Color = new SKColor(0xE0, 0x30, 0x30, 0xE0),
            StrokeWidth = 2f,
            IsStroke = true,
            IsAntialias = true
        };

        // Заливка поверх всего габарита: пометка должна читаться сразу и целиком,
        // а не только по редким диагоналям.
        private static readonly SKPaint _paintOffPageFill = new()
        {
            Color = new SKColor(0xE0, 0x30, 0x30, 0x38),
            IsAntialias = true
        };

        /// <summary>Лежит ли картинка целиком за пределами своей страницы.</summary>
        private static bool IsImageOffItsPage(ImageEntry ie, PageRect page)
        {
            float left = ie.XPt, right = ie.XPt + ie.WidthPt;
            float top = ie.Ypt, bottom = ie.Ypt + ie.HeightPt;

            float pageLeft = page.PadLeftPt, pageRight = page.PadLeftPt + page.WidthPt;
            float pageTop = page.Ypt, pageBottom = page.Ypt + page.HeightPt;

            return right <= pageLeft || left >= pageRight
                || bottom <= pageTop || top >= pageBottom;
        }

        /// <summary>
        /// Косая красная штриховка поверх габарита картинки — метка «объект мимо листа».
        /// Рисуется всегда, независимо от выделения: пользователь должен видеть, что
        /// картинка существует, но на страницу не попадает и ни на что не влияет.
        /// </summary>
        private static void DrawOffPageHatch(SKCanvas canvas, SKRect rect)
        {
            canvas.DrawRect(rect, _paintOffPageFill);
            canvas.DrawRect(rect, _paintOffPageHatch);

            const float StepPt = 9f;
            float span = rect.Width + rect.Height;
            for (float offset = 0f; offset < span; offset += StepPt)
            {
                float x0 = rect.Left + offset;
                float y0 = rect.Top;
                float x1 = rect.Left;
                float y1 = rect.Top + offset;

                // Обрезаем диагональ по прямоугольнику картинки.
                if (x0 > rect.Right)
                {
                    y0 += x0 - rect.Right;
                    x0 = rect.Right;
                }
                if (y1 > rect.Bottom)
                {
                    x1 += y1 - rect.Bottom;
                    y1 = rect.Bottom;
                }
                if (y0 > rect.Bottom || x1 > rect.Right) continue;

                canvas.DrawLine(x0, y0, x1, y1, _paintOffPageHatch);
            }
        }

        /// <summary>
        /// Рисует картинку, встроенную в строку текста. Вызывается из рендера
        /// абзаца через SKTextRenderer.DrawInlineObject: сегмент знает только Id
        /// и габарит, всё остальное берётся из самой картинки.
        /// segX — левый край сегмента, baseY — базовая линия строки: картинка
        /// стоит на ней, как крупный глиф.
        /// </summary>
        private void DrawInlineImageSegment(SKCanvas canvas, SKRunSegment seg, float segX, float baseY)
        {
            if (seg.InlineImageId is not Guid id) return;

            var block = FindInlineImage(id);
            if (block is null) return;

            var skImg = GetImageBitmap(block.ImageFileName);
            if (skImg is null) return;

            // Бокс сегмента — это AABB повёрнутой картинки; сама картинка
            // центрируется в нём, как и в потоке блоков.
            float boxW = seg.ObjectWidthPt;
            float boxH = seg.ObjectHeightPt;
            float imgW = (float)block.WidthPt;
            float imgH = (float)block.HeightPt;

            float left = segX + (boxW - imgW) / 2f;
            float top = baseY - boxH + (boxH - imgH) / 2f;

            float cx = segX + boxW / 2f;
            float cy = baseY - boxH / 2f;

            float rotDeg = (float)block.RotationDeg;
            bool hasXform = rotDeg != 0f || block.FlipHorizontal || block.FlipVertical;

            canvas.Save();

            // Обрезка по своему листу. Плавающие картинки клипует проход по списку
            // картинок, а эту рисует рендер текста — он про страницы ничего не знает,
            // и без клипа картинка, вылезшая за край страницы, продолжает рисоваться
            // по серому фону и залезает в межстраничный зазор.
            //
            // Страницу ищем по НАИБОЛЬШЕМУ перекрытию с габаритом картинки, а не по
            // базовой линии: у высокой строки на стыке страниц базовая линия попадает
            // в межстраничный зазор, где страницы нет вообще — поиск не находил ничего
            // и клип не ставился совсем. Если перекрытия нет ни с одной страницей,
            // берём ближайшую: клип должен стоять всегда.
            if (DocVm?.ViewMode == EditorViewMode.Page)
            {
                List<PageRect> clipPages;
                lock (_renderLock) { clipPages = _pages; }

                float imgTopY = baseY - boxH;
                float imgCenterY = baseY - boxH / 2f;

                int bestPage = -1;
                float bestOverlap = 0f;
                float bestDistance = float.MaxValue;

                for (int p = 0; p < clipPages.Count; p++)
                {
                    float pageTop = clipPages[p].Ypt;
                    float pageBottom = pageTop + clipPages[p].HeightPt;

                    float overlap = Math.Min(baseY, pageBottom) - Math.Max(imgTopY, pageTop);
                    if (overlap > bestOverlap)
                    {
                        bestOverlap = overlap;
                        bestPage = p;
                        continue;
                    }
                    if (bestOverlap > 0f) continue;

                    float distance = imgCenterY < pageTop ? pageTop - imgCenterY
                                   : imgCenterY > pageBottom ? imgCenterY - pageBottom
                                   : 0f;
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestPage = p;
                    }
                }

                if (bestPage >= 0)
                {
                    var pg = clipPages[bestPage];
                    canvas.ClipRect(new SKRect(
                        pg.PadLeftPt, pg.Ypt,
                        pg.PadLeftPt + pg.WidthPt, pg.Ypt + pg.HeightPt));
                }
            }
            if (hasXform)
            {
                if (rotDeg != 0f) canvas.RotateDegrees(rotDeg, cx, cy);
                if (block.FlipHorizontal || block.FlipVertical)
                    canvas.Scale(
                        block.FlipHorizontal ? -1f : 1f,
                        block.FlipVertical ? -1f : 1f,
                        cx, cy);
            }

            byte imgAlpha = (byte)Math.Clamp(block.Opacity * 255.0, 0.0, 255.0);
            _paintImageDraw.Color = new SKColor(0xFF, 0xFF, 0xFF, imgAlpha);

            var dst = new SKRect(left, top, left + imgW, top + imgH);

            float srcW = skImg.Width;
            float srcH = skImg.Height;
            var src = new SKRect(
                srcW * (float)Math.Clamp(block.CropLeftFrac, 0.0, 0.95),
                srcH * (float)Math.Clamp(block.CropTopFrac, 0.0, 0.95),
                srcW * (float)(1.0 - Math.Clamp(block.CropRightFrac, 0.0, 0.95)),
                srcH * (float)(1.0 - Math.Clamp(block.CropBottomFrac, 0.0, 0.95)));
            if (src.Right <= src.Left + 1f) src.Right = src.Left + 1f;
            if (src.Bottom <= src.Top + 1f) src.Bottom = src.Top + 1f;

            canvas.DrawImage(skImg, src, dst, _imageSampling, _paintImageDraw);
            _paintImageDraw.Color = new SKColor(0xFF, 0xFF, 0xFF, 0xFF);

            if (block.BorderThicknessPt > 0.0
                && !string.IsNullOrEmpty(block.BorderColor)
                && SKColor.TryParse(block.BorderColor, out var borderColor)
                && borderColor.Alpha > 0)
            {
                _paintImageBorderDraw.Color =
                    borderColor.WithAlpha((byte)(borderColor.Alpha * imgAlpha / 255));
                _paintImageBorderDraw.StrokeWidth = (float)block.BorderThicknessPt;
                canvas.DrawRect(dst, _paintImageBorderDraw);
            }

            // Выделенная картинка в строке: в режиме разметки рамку, маркеры размера,
            // поворота и обрезки рисует общий проход по списку картинок — он полнее.
            // В потоковом и черновом режимах того прохода нет, поэтому простую рамку
            // с маркерами рисуем здесь.
            if (ReferenceEquals(block, _selectedImage)
                && (DocVm?.ViewMode ?? EditorViewMode.Draft) != EditorViewMode.Page)
            {
                canvas.DrawRect(dst, _paintImageSelection);

                float mx = (dst.Left + dst.Right) / 2f;
                float my = (dst.Top + dst.Bottom) / 2f;
                DrawImageHandle(canvas, dst.Left, dst.Top);
                DrawImageHandle(canvas, dst.Right, dst.Top);
                DrawImageHandle(canvas, dst.Right, dst.Bottom);
                DrawImageHandle(canvas, dst.Left, dst.Bottom);
                DrawImageHandle(canvas, mx, dst.Top);
                DrawImageHandle(canvas, dst.Right, my);
                DrawImageHandle(canvas, mx, dst.Bottom);
                DrawImageHandle(canvas, dst.Left, my);
            }

            canvas.Restore();
        }

        // Рисует один квадратный маркер изменения размера (белая заливка, оранжевая рамка).
        // В режиме обрезки маркеры заливаются акцентным цветом — видно смену режима.
        private void DrawImageHandle(SKCanvas canvas, float cxPt, float cyPt)
        {
            var rect = new SKRect(
                cxPt - ImageHandleHalfPt, cyPt - ImageHandleHalfPt,
                cxPt + ImageHandleHalfPt, cyPt + ImageHandleHalfPt);
            canvas.DrawRect(rect, _imageCropMode ? _paintImageHandleCropFill : _paintImageHandleFill);
            canvas.DrawRect(rect, _paintImageSelection);
        }

        // Рисует маркер поворота: белый круг с оранжевой рамкой и значком
        // круговой стрелки внутри (дуга 270 градусов с наконечником).
        private void DrawRotateHandle(SKCanvas canvas, float cxPt, float cyPt)
        {
            canvas.DrawCircle(cxPt, cyPt, ImageRotateHandleRadiusPt, _paintImageHandleFill);
            canvas.DrawCircle(cxPt, cyPt, ImageRotateHandleRadiusPt, _paintImageSelection);

            // Дуга: старт сверху (-90), по часовой на 270 градусов, конец слева.
            float r = ImageRotateHandleRadiusPt * 0.55f;
            var arcRect = new SKRect(cxPt - r, cyPt - r, cxPt + r, cyPt + r);
            using (var arc = new SKPath())
            {
                arc.AddArc(arcRect, -90f, 270f);
                canvas.DrawPath(arc, _paintRotateArrowStroke);
            }

            // Наконечник на конце дуги (точка слева от центра, движение вверх).
            float ax = cxPt - r;
            float ay = cyPt;
            float ah = ImageRotateHandleRadiusPt * 0.45f;
            using (var head = new SKPath())
            {
                head.MoveTo(ax, ay - ah);
                head.LineTo(ax - ah * 0.75f, ay + ah * 0.35f);
                head.LineTo(ax + ah * 0.75f, ay + ah * 0.35f);
                head.Close();
                canvas.DrawPath(head, _paintRotateArrowFill);
            }
        }

        // Загружает и кеширует декодированное изображение по имени файла внутри проекта.
        // Декодирование выполняется в фоновой задаче: при промахе кеша метод сразу возвращает
        // null, не блокируя render-поток чтением ZIP и SKImage.FromEncodedData. Готовое
        // изображение попадает в кеш, после чего запрашивается перерисовка и картинка
        // появляется на следующем кадре. За счёт этого при быстром скролле текст и лист
        // рисуются мгновенно, а изображения подгружаются постепенно.
        private SKImage? GetImageBitmap(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return null;

            lock (_imageCacheLock)
            {
                if (_imageCache.TryGetValue(fileName, out var cached)) return cached;
                if (_imageLoadsInFlight.Contains(fileName)) return null;
                _imageLoadsInFlight.Add(fileName);
            }

            System.Threading.Tasks.Task.Run(() =>
            {
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

                lock (_imageCacheLock)
                {
                    _imageLoadsInFlight.Remove(fileName);
                    // Кешируем только удачную загрузку: если файл временно отсутствует
                    // (например, во время операции), при следующем кадре попробуем снова.
                    if (img is not null) _imageCache[fileName] = img;
                }

                if (img is not null)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        // Сбрасываем кеш-битмап, чтобы полный ре-рендер отрисовал
                        // только что загруженное изображение, а не старый снимок.
                        _contentDirty = true;
                        InvalidateVisual();
                    });
                }
            });

            return null;
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
            // Отрисовка встроенных в строку картинок: обработчик статический и общий для
            // всех канвасов, поэтому ставится перед каждым проходом — он замкнут на
            // документ именно этого канваса.
            SKTextRenderer.DrawInlineObject = DrawInlineImageSegment;

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

            // Клип по своему листу на весь абзац: за краем страницы бумаги нет, и ничто
            // из его содержимого туда попадать не должно. Раньше клип стоял только на
            // плавающих картинках, и всё, что рисует рендер текста, — в первую очередь
            // картинка в строке — спокойно вылезало за край и рисовалось по серому фону
            // и по межстраничному зазору.
            bool pageClip = false;
            if (DocVm?.ViewMode == EditorViewMode.Page)
            {
                List<PageRect> clipPages;
                lock (_renderLock) { clipPages = _pages; }

                if (pl.PageIndex >= 0 && pl.PageIndex < clipPages.Count)
                {
                    var pg = clipPages[pl.PageIndex];
                    canvas.Save();
                    canvas.ClipRect(new SKRect(
                        pg.PadLeftPt, pg.Ypt,
                        pg.PadLeftPt + pg.WidthPt, pg.Ypt + pg.HeightPt));
                    pageClip = true;
                }
            }

            if (isCell)
            {
                canvas.Save();
                var clip = pl.Cell!;
                canvas.ClipRect(new SKRect(clip.ClipX, clip.ClipY,
                    clip.ClipX + clip.ClipW, clip.ClipY + clip.ClipH));
            }

            var renderLayout = GetRenderLayout(pl, (float)(_canvasWidth * PxToPt));

            // Маркер списка: выступ = фактический левый край текста − позиция маркера (в pt).
            // Левый край берём из раскладки (renderLayout.LeftIndentPt), т.е. с учётом отступа,
            // выставленного линейкой/диалогом — тогда маркер держит выступ при любом отступе текста.
            string? markerText = null;
            float markerHanging = 0f;
            float markerMinGap = 0f;

            if (pl.Marker is Rendering.ListMarkerInfo mi
                && pl.Vm.Model?.ListProperties is { } lp
                && lp.MarkerType != ListMarkerType.None)
            {
                markerText = mi.Text;
                float textLeftPt = renderLayout.LeftIndentPt;

                // Позицию номера берёт раскладка: у правого края текстовой зоны и в узкой
                // ячейке она сдвигает номер влево, чтобы он не лёг на текст первой строки.
                // Раскладка абзаца к этому моменту построена (renderLayout выше), поэтому
                // значение всегда актуально; прежний расчёт по MarkerIndentPt о пределе
                // текста не знал и рисовал номер поверх букв.
                float markerAbsPt = (float)lp.ComputedMarkerIndentPt;
                markerHanging = textLeftPt - markerAbsPt;
                markerMinGap = (float)lp.MarkerTextMinGapPt;
            }

            SKTextRenderer.RenderParagraphLines(
                canvas, renderLayout,
                absX + renderLayout.LeftIndentPt,
                absY,
                pl.LineFrom, pl.LineTo,
                markerText: markerText,
                markerHangingPt: markerHanging,
                markerMinGapPt: markerMinGap);

            // Выделение рисуем ПОВЕРХ содержимого (после заливки текста и глифов), иначе
            // непрозрачная заливка HighlightColor перекрывает полупрозрачную подсветку
            // выделения и выделенный текст на залитом фоне становится не виден.
            DrawSelectionForSlice(canvas, idx, pl, absX, absY, layouts, renderLayout);

            if (drawCaret && _caretPara == idx)
                DrawCaret(canvas, pl, absX, absY, renderLayout);

            if (isCell)
                canvas.Restore();

            if (pageClip)
                canvas.Restore();
        }

        /// <summary>
        /// Можно ли рисовать каретку в этом кадре. Кроме мигания и жеста зума учитывает
        /// момент пересборки раскладки: пока новый индекс слайса ещё не найден, номер
        /// каретки относится к прежней раскладке и указал бы на чужой абзац.
        /// </summary>
        private bool CaretDrawable => _caretVisible && !_zooming && !_caretIndexPending;

        private void DrawCaretOnCanvas(
            SKCanvas canvas,
            List<ParaLayout> layouts,
            List<PageRect> pages,
            double canvasWidth)
        {
            if (!CaretDrawable) return;
            if (_caretPara < 0 || _caretPara >= layouts.Count) return;

            var pl = layouts[_caretPara];
            var caretLayout = GetRenderLayout(pl, (float)(_canvasWidth * PxToPt));
            float xPt = pl.AbsXPt;

            // Страницы рядом: каретка рисуется в визуальной позиции своей страницы.
            var (caretDx, caretDy) = PageVisualDelta(pl.PageIndex, pages);
            bool caretShifted = caretDx != 0f || caretDy != 0f;
            if (caretShifted)
            {
                canvas.Save();
                canvas.Translate(caretDx, caretDy);
            }

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
            if (caretShifted) canvas.Restore();
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

            // Страницы рядом: видимость определяется визуальными рядами.
            if (_pagesPerRow > 1)
            {
                int cols = _pagesPerRow;
                float rowH = pages[0].HeightPt + PageGapPt;
                int lastRowIdx = (pages.Count - 1) / cols;
                int firstRow = Math.Clamp((int)((viewTopPt - PageGapPt) / rowH), 0, lastRowIdx);
                int lastRow = Math.Clamp((int)((viewBotPt - PageGapPt) / rowH), firstRow, lastRowIdx);
                int firstV = firstRow * cols;
                int lastV = Math.Min(pages.Count - 1, lastRow * cols + cols - 1);
                return (firstV, lastV);
            }

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

            // Строка, разорванная обтекаемым объектом, даёт НЕСКОЛЬКО прямоугольников —
            // по одному на отрезок. Разбивка по «голубизне» ниже сама рисует все отрезки
            // строки целиком, поэтому по второму прямоугольнику той же строки её нельзя
            // рисовать повторно: полупрозрачная кисть в наложении даёт двойную заливку,
            // и выделение рядом с картинкой выглядит темнее остального.
            int drawnGroupLine = -1;

            foreach (var r in rects)
            {
                if (r.LineIndex < pl.LineFrom || r.LineIndex >= pl.LineTo) continue;
                if (r.LineIndex >= sl.Lines.Count) continue;

                var ln = sl.Lines[r.LineIndex];
                int lineSelStart = Math.Max(from, ln.FirstCharIndex);
                int lineSelEnd = Math.Min(to, ln.LastCharIndex + 1);

                // Хвостовые пробелы обрезаем только на строках с мягким переносом: там они
                // визуально «висят» на переносе. На последней строке абзаца пробелы стоят
                // в пределах строки и выделяются целиком (как в Word).
                int lastContentEnd = ln.IsLastLine ? -1 : LastContentCharEnd(ln);
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
                        xPt, yPt, yBase, SelectionPaintAt(pl, lineSelStart), r.FragmentIndex);
                    continue;
                }

                // Эта строка уже отрисована по группам — вместе со всеми своими отрезками.
                if (r.LineIndex == drawnGroupLine) continue;
                drawnGroupLine = r.LineIndex;

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
                    rr.Rect.Top, rr.Rect.Height, subFrom, subTo, xPt, yPt, yBase, paint,
                    rr.FragmentIndex);
            }
        }

        // Общая отрисовка одного прямоугольника выделения с приведением координат к тексту.
        private void DrawSelectionRect(
            SKCanvas canvas, SKTextLayout sl, int lineIndex,
            float rectLeft, float rectWidth, float rectTop, float rectHeight,
            int selFrom, int selTo, float xPt, float yPt, float yBase, SKPaint paint,
            int fragmentIndex = 0)
        {
            float firstLineBaked = (lineIndex == 0) ? sl.FirstLineIndentPt : 0f;
            float left = xPt + rectLeft - firstLineBaked + LineAlignShift(sl, lineIndex);
            float width = rectWidth;

            // Растяжка по ширине считается по отрезку, которому принадлежит ЭТОТ
            // прямоугольник, и по символам внутри него. Строка, разорванная объектом,
            // даёт несколько прямоугольников: если каждому подставлять сдвиг, посчитанный
            // по границам всего выделения, правый кусок съезжает влево на растяжку,
            // набранную левым, и накладывается на него — выделение выглядит темнее.
            float extra = lineIndex < sl.Lines.Count
                ? SKTextRenderer.JustifyExtraPerSpace(sl, lineIndex, fragmentIndex)
                : 0f;
            if (extra > 0f && lineIndex < sl.Lines.Count)
            {
                var (fragFrom, fragTo) = FragmentCharRange(sl.Lines[lineIndex], fragmentIndex);
                int shiftFrom = Math.Max(selFrom, fragFrom);
                int shiftTo = Math.Min(selTo, fragTo);
                if (shiftTo < shiftFrom) shiftTo = shiftFrom;

                float leftShift = JustifyShiftBeforeChar(sl, lineIndex, shiftFrom);
                float rightShift = JustifyShiftBeforeChar(sl, lineIndex, shiftTo);
                left += leftShift;
                width += rightShift - leftShift;
            }

            // Запас справа: ширина прямоугольника считается по advance-ширинам глифов,
            // а рисунок последней буквы выступает правее на пару пикселей — без запаса
            // выделение выглядит так, будто не дотягивается до конца текста. Смежные
            // прямоугольники рисуются слева направо и перекрывают запас предыдущего.
            width += SKTextRenderer.HighlightRightOverhangPt;

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
            if (lineIndex < 0 || lineIndex >= layout.Lines.Count) return stretchedX;
            var line = layout.Lines[lineIndex];

            float extra = SKTextRenderer.JustifyExtraPerSpace(layout, lineIndex, 0);
            if (extra <= 0f && !line.HasWrapFragments) return stretchedX;

            // Разорванная объектом строка растягивается по отрезкам: накопленный сдвиг
            // сбрасывается на каждом переходе, и добавка берётся своя.
            int fragment = 0;
            float cumStretch = 0f;
            foreach (var seg in line.Segments)
            {
                if (seg.WrapFragmentIndex != fragment)
                {
                    fragment = seg.WrapFragmentIndex;
                    extra = SKTextRenderer.JustifyExtraPerSpace(layout, lineIndex, fragment);
                    cumStretch = 0f;
                }

                float stretchedLeft = seg.X + cumStretch;
                if (stretchedX < stretchedLeft)
                    return seg.X;
                if (stretchedX <= stretchedLeft + seg.Width)
                    return seg.X + (stretchedX - stretchedLeft);

                if (extra > 0f)
                {
                    int spaces = 0;
                    foreach (var c in seg.Text)
                        if (c == ' ' || c == '\t') spaces++;
                    cumStretch += spaces * extra;
                }
            }
            return line.TextWidth;
        }

        // Накопленная добавка растяжки по ширине для пробелов строки, расположенных до символа
        // globalCharIndex. Хвостовые пробелы строки исключаются — как и в JustifyExtraPerSpace,
        // иначе их (несуществующая) растяжка уводит каретку и выделение за правый край.
        // Для не-Justify и последней строки даёт 0.
        private static float JustifyShiftBeforeChar(SKTextLayout layout, int lineIndex, int globalCharIndex)
        {
            if (lineIndex < 0 || lineIndex >= layout.Lines.Count) return 0f;
            var line = layout.Lines[lineIndex];

            // Строку, разорванную обтекаемым объектом, растягивает каждый отрезок сам:
            // считаем сдвиг внутри того отрезка, где стоит символ, и только по его пробелам.
            int fragment = FragmentOfChar(line, globalCharIndex);

            float extra = SKTextRenderer.JustifyExtraPerSpace(layout, lineIndex, fragment);
            if (extra <= 0f) return 0f;

            bool InFragment(Core.Models.Rendering.SKRunSegment seg)
                => !line.HasWrapFragments || seg.WrapFragmentIndex == fragment;

            // Граница последнего слова отрезка: пробелы за ней растяжки не получают.
            int lastWordEnd = -1;
            foreach (var s in line.Segments)
            {
                if (!InFragment(s)) continue;
                for (int k = 0; k < s.Text.Length; k++)
                {
                    char c = s.Text[k];
                    if (c != ' ' && c != '\t') lastWordEnd = s.GlobalCharOffset + k + 1;
                }
            }
            if (lastWordEnd < 0) return 0f;

            int limit = Math.Min(globalCharIndex, lastWordEnd);
            int spacesBefore = 0;
            foreach (var s in line.Segments)
            {
                if (!InFragment(s)) continue;
                for (int k = 0; k < s.Text.Length; k++)
                {
                    if (s.GlobalCharOffset + k >= limit) return spacesBefore * extra;
                    char c = s.Text[k];
                    if (c == ' ' || c == '\t') spacesBefore++;
                }
            }
            return spacesBefore * extra;
        }

        // Диапазон символов [from, to) отрезка разорванной строки. Для обычной строки —
        // вся строка.
        private static (int From, int To) FragmentCharRange(SKLineLayout line, int fragmentIndex)
        {
            if (!line.HasWrapFragments)
                return (line.FirstCharIndex, line.LastCharIndex + 1);

            int from = int.MaxValue;
            int to = int.MinValue;
            foreach (var s in line.Segments)
            {
                if (s.WrapFragmentIndex != fragmentIndex) continue;
                if (s.GlobalCharOffset < from) from = s.GlobalCharOffset;
                int end = s.GlobalCharOffset + s.Text.Length;
                if (end > to) to = end;
            }

            return from == int.MaxValue
                ? (line.FirstCharIndex, line.FirstCharIndex)
                : (from, to);
        }

        // Отрезок разорванной строки, в котором лежит символ. Для обычной строки — 0.
        private static int FragmentOfChar(SKLineLayout line, int globalCharIndex)
        {
            if (!line.HasWrapFragments) return 0;

            int fragment = 0;
            foreach (var s in line.Segments)
            {
                if (s.GlobalCharOffset > globalCharIndex) break;
                fragment = s.WrapFragmentIndex;
            }
            return fragment;
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