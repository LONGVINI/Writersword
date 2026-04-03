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
using Writersword.Infrastructure.Rendering;
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
            List<ParaLayout> layouts;
            List<PageRect> pages;
            List<TableEntry> tables;
            float canvasHeightPt;
            double canvasWidth;

            lock (_renderLock)
            {
                layouts = _layouts;
                pages = _pages;
                tables = _tables;
                canvasHeightPt = _canvasHeightPt;
                canvasWidth = _canvasWidth;
            }

            double zoom = Zoom;
            float scale = (float)(PtToPx * zoom);

            int pixelW = (int)Math.Max(Bounds.Width, 1);
            int pixelH = (int)Math.Max(Bounds.Height, 1);

            if (_caretOnlyRedraw
                && _lastFullRenderBitmap is not null
                && _lastFullRenderWidth == pixelW
                && _lastFullRenderHeight == pixelH)
            {
                _caretOnlyRedraw = false;
                canvas.DrawBitmap(_lastFullRenderBitmap, 0, 0);

                if (_caretVisible)
                {
                    canvas.Save();
                    canvas.Scale(scale, scale);
                    DrawCaretOnCanvas(canvas, layouts, pages, canvasWidth);
                    canvas.Restore();
                }
                return;
            }

            _caretOnlyRedraw = false;

            using var surface = SKSurface.Create(
                new SKImageInfo(pixelW, pixelH, SKColorType.Bgra8888, SKAlphaType.Premul));

            if (surface is not null)
            {
                var offscreen = surface.Canvas;
                offscreen.Save();
                offscreen.Scale(scale, scale);

                var mode = DocVm?.ViewMode ?? EditorViewMode.Draft;
                if (mode == EditorViewMode.Page)
                    RenderPageMode(offscreen, layouts, pages, tables, canvasHeightPt, canvasWidth, false);
                else
                    RenderFlowMode(offscreen, mode, layouts, tables, canvasHeightPt, canvasWidth, false);

                offscreen.Restore();

                using var snapshot = surface.Snapshot();
                _lastFullRenderBitmap?.Dispose();
                _lastFullRenderBitmap = SKBitmap.FromImage(snapshot);
                _lastFullRenderWidth = pixelW;
                _lastFullRenderHeight = pixelH;

                canvas.DrawBitmap(_lastFullRenderBitmap, 0, 0);

                if (_caretVisible)
                {
                    canvas.Save();
                    canvas.Scale(scale, scale);
                    DrawCaretOnCanvas(canvas, layouts, pages, canvasWidth);
                    canvas.Restore();
                }
            }
            else
            {
                canvas.Save();
                canvas.Scale(scale, scale);
                var mode = DocVm?.ViewMode ?? EditorViewMode.Draft;
                if (mode == EditorViewMode.Page)
                    RenderPageMode(canvas, layouts, pages, tables, canvasHeightPt, canvasWidth, _caretVisible);
                else
                    RenderFlowMode(canvas, mode, layouts, tables, canvasHeightPt, canvasWidth, _caretVisible);
                canvas.Restore();
            }
        }

        // Рисует только рамки и фон таблицы (без параграфов — они в _layouts).
        private static void RenderTableStructureOnly(
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

            foreach (var row in tableLayout.Rows)
            {
                if (row.Row < rowFrom || row.Row >= effectiveRowTo) continue;

                bool isFirstRow = row.Row == rowFrom;
                bool isLastRow = row.Row == effectiveRowTo - 1;
                float rowShift = isFirstRow ? firstRowContentOffsetPt : 0f;
                // Эффективная высота строки после вычета уже показанной части сверху.
                float effectiveRowH = isFirstRow ? row.HeightPt - rowShift : row.HeightPt;
                // Для последней строки слайса с ByCell-разрывом — ограничиваем снизу.
                // lastRowVisibleHeightPt уже выражен как высота видимого окна на этой странице,
                // без вычета firstRowShift, поэтому просто берём его напрямую.
                float visibleH = (isLastRow && lastRowVisibleHeightPt >= 0f)
                    ? lastRowVisibleHeightPt
                    : effectiveRowH;

                foreach (var cell in row.Cells)
                {
                    float cellX = tableX + cell.Xpt;
                    float cellY = tableY + cell.Ypt - rowOffsetY - rowShift;

                    // Фон — только в пределах видимой части строки
                    if (!string.IsNullOrEmpty(cell.BackgroundColor)
                        && SKColor.TryParse(cell.BackgroundColor, out var bgColor))
                    {
                        using var bgPaint = new SKPaint { Color = bgColor };
                        canvas.DrawRect(cellX, cellY + rowShift, cell.WidthPt, visibleH, bgPaint);
                    }

                    // Видимый верхний край (для первой строки продолжения cellY сдвинут вверх).
                    float visibleCellY = cellY + rowShift;
                    bool suppressBottom = isLastRow && lastRowVisibleHeightPt >= 0f;
                    SKTextRenderer.RenderCellBordersPublic(canvas, cell, cellX, visibleCellY,
                        visibleH, canvasScale, false, suppressBottom);
                }
            }
        }

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
            float tableH = layout.TotalHeightPt;
            float tableW = layout.TotalWidthPt;

            const float HW = 6f;   // half-width ручки в pt
            const float HH = 4f;   // half-height ручки в pt

            using var fill = new SKPaint { Color = HandleFill, IsAntialias = true };
            using var stroke = new SKPaint { Color = HandleStroke, StrokeWidth = 1f, IsStroke = true, IsAntialias = true };

            // ↔ на каждой внутренней и внешней правой границе колонки (по центру Y таблицы)
            float midY = tableY + tableH / 2f;
            float accX = tableX;
            for (int i = 0; i < layout.ColumnWidthsPt.Count; i++)
            {
                accX += layout.ColumnWidthsPt[i];
                float hx = accX;
                float hy = midY;
                DrawHandle(canvas, hx, hy, HW, HH, fill, stroke, horizontal: true);
            }

            // ↕ на нижнем краю по центру ширины
            float midX = tableX + tableW / 2f;
            DrawHandle(canvas, midX, tableY + tableH, HH, HW, fill, stroke, horizontal: false);

            // ↔ на левом крае (для сдвига всей таблицы)
            DrawHandle(canvas, tableX, midY, HW, HH, fill, stroke, horizontal: true);
        }

        private static void DrawHandle(SKCanvas canvas,
            float cx, float cy, float hw, float hh,
            SKPaint fill, SKPaint stroke, bool horizontal)
        {
            var rect = new SKRect(cx - hw, cy - hh, cx + hw, cy + hh);
            canvas.DrawRoundRect(rect, 2f, 2f, fill);
            canvas.DrawRoundRect(rect, 2f, 2f, stroke);

            // Стрелочки внутри
            using var arrow = new SKPaint
            { Color = SKColors.White, StrokeWidth = 1f, IsStroke = true, IsAntialias = true };
            if (horizontal)
            {
                // ←
                canvas.DrawLine(cx - hw + 1.5f, cy, cx - 1f, cy, arrow);
                canvas.DrawLine(cx - hw + 1.5f, cy, cx - hw + 3.5f, cy - 2f, arrow);
                canvas.DrawLine(cx - hw + 1.5f, cy, cx - hw + 3.5f, cy + 2f, arrow);
                // →
                canvas.DrawLine(cx + hw - 1.5f, cy, cx + 1f, cy, arrow);
                canvas.DrawLine(cx + hw - 1.5f, cy, cx + hw - 3.5f, cy - 2f, arrow);
                canvas.DrawLine(cx + hw - 1.5f, cy, cx + hw - 3.5f, cy + 2f, arrow);
            }
            else
            {
                // ↑
                canvas.DrawLine(cx, cy - hh + 1.5f, cx, cy - 1f, arrow);
                canvas.DrawLine(cx, cy - hh + 1.5f, cx - 2f, cy - hh + 3.5f, arrow);
                canvas.DrawLine(cx, cy - hh + 1.5f, cx + 2f, cy - hh + 3.5f, arrow);
                // ↓
                canvas.DrawLine(cx, cy + hh - 1.5f, cx, cy + 1f, arrow);
                canvas.DrawLine(cx, cy + hh - 1.5f, cx - 2f, cy + hh - 3.5f, arrow);
                canvas.DrawLine(cx, cy + hh - 1.5f, cx + 2f, cy + hh - 3.5f, arrow);
            }
        }

        private void RenderPageMode(
            SKCanvas canvas,
            List<ParaLayout> layouts,
            List<PageRect> pages,
            List<TableEntry> tables,
            float canvasHeightPt,
            double canvasWidth,
            bool drawCaret)
        {
            float canvasWPt = (float)(canvasWidth * PxToPt);

            using var bgPaint = new SKPaint { Color = CanvasBgColor };
            canvas.DrawRect(0, 0, canvasWPt, canvasHeightPt, bgPaint);

            var (firstPage, lastPage) = GetVisiblePageRange(pages);

            for (int pi = firstPage; pi <= lastPage && pi < pages.Count; pi++)
            {
                var page = pages[pi];
                using var sh = new SKPaint { Color = PageShadowColor };
                canvas.DrawRect(page.PadLeftPt + 3, page.Ypt + 3, page.WidthPt, page.HeightPt, sh);
                using var pg = new SKPaint { Color = SKColors.White };
                canvas.DrawRect(page.PadLeftPt, page.Ypt, page.WidthPt, page.HeightPt, pg);
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

            using var bgPaint = new SKPaint { Color = SKColors.Transparent };
            canvas.DrawRect(0, 0, canvasWPt, canvasHeightPt, bgPaint);

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

            DrawSelectionForSlice(canvas, idx, pl, absX, absY, layouts);

            SKTextRenderer.RenderParagraphLines(
                canvas, pl.Layout,
                absX + pl.Layout.LeftIndentPt,
                absY,
                pl.LineFrom, pl.LineTo);

            if (drawCaret && _caretPara == idx)
                DrawCaret(canvas, pl, absX, absY);

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
            float xPt = pl.AbsXPt;

            bool isCell = pl.Cell != null;
            if (isCell)
            {
                canvas.Save();
                var c = pl.Cell!;
                canvas.ClipRect(new SKRect(c.ClipX, c.ClipY, c.ClipX + c.ClipW, c.ClipY + c.ClipH));
            }

            DrawCaret(canvas, pl, xPt, pl.Ypt);

            if (isCell) canvas.Restore();
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
            float xPt, float yPt, List<ParaLayout> layouts)
        {
            if (!HasSel()) return;

            var (sp, sc, ep, ec) = NormalizeSelection();
            if (sliceIdx < sp || sliceIdx > ep) return;

            int len = pl.Vm.PlainText?.Length ?? 0;
            int from = sliceIdx == sp ? sc : 0;
            int to = sliceIdx == ep ? ec : len;

            from = Clamp(from, 0, len);
            to = Clamp(to, 0, len);
            if (from >= to && !(from == 0 && len == 0)) return;

            if (from == to && len == 0)
            {
                float lineH = pl.Layout.Lines.Count > 0 ? pl.Layout.Lines[0].Height : FallbackLinePt;
                float yBase = pl.LineFrom < pl.Layout.Lines.Count ? pl.Layout.Lines[pl.LineFrom].Y : 0f;
                using var ep2 = new SKPaint { Color = SelectionColor };
                canvas.DrawRect(xPt, yPt + (0 - yBase), 5f, lineH, ep2);
                return;
            }

            var rects = pl.Layout.HitTestRange(from, to);
            if (rects.Count == 0) return;

            float yBase2 = pl.LineFrom < pl.Layout.Lines.Count
                ? pl.Layout.Lines[pl.LineFrom].Y : 0f;

            using var paint = new SKPaint { Color = SelectionColor };
            foreach (var r in rects)
            {
                if (r.LineIndex < pl.LineFrom || r.LineIndex >= pl.LineTo) continue;
                canvas.DrawRect(
                    xPt + r.Rect.Left,
                    yPt + (r.Rect.Top - yBase2),
                    r.Rect.Width, r.Rect.Height, paint);
            }
        }

        private void DrawCaret(SKCanvas canvas, ParaLayout pl, float xPt, float yPt)
        {
            // Якорный параграф (пустой текст) — рисуем каретку напрямую в его позиции.
            if (string.IsNullOrEmpty(pl.Vm.PlainText))
            {
                using var ap = new SKPaint { Color = SKColors.Black, StrokeWidth = 1.1f, IsAntialias = false };
                canvas.DrawLine(xPt, yPt, xPt, yPt + FallbackLinePt, ap);
                return;
            }

            int pos = Clamp(_caretChar, 0, pl.Vm.PlainText?.Length ?? 0);

            float yBase = pl.LineFrom < pl.Layout.Lines.Count
                ? pl.Layout.Lines[pl.LineFrom].Y : 0f;

            int drawLineIdx;
            SKCaretRect caret;

            if (_caretLineHint >= 0
                && _caretLineHint >= pl.LineFrom
                && _caretLineHint < Math.Min(pl.LineTo, pl.Layout.Lines.Count))
            {
                var hintLine = pl.Layout.Lines[_caretLineHint];
                if (pos > hintLine.LastCharIndex && !hintLine.IsLastLine)
                {
                    var lastSeg = hintLine.Segments.Count > 0 ? hintLine.Segments[^1] : null;
                    float hintLineExtra = (_caretLineHint == 0) ? pl.Layout.FirstLineIndentPt : 0f;
                    caret = new SKCaretRect
                    {
                        X = lastSeg != null
                            ? pl.Layout.LeftIndentPt + hintLineExtra + lastSeg.X + lastSeg.Width
                            : pl.Layout.LeftIndentPt + hintLineExtra,
                        Y = hintLine.Y,
                        Height = hintLine.Height,
                        Baseline = hintLine.Baseline
                    };
                    drawLineIdx = _caretLineHint;
                }
                else
                {
                    caret = pl.Layout.HitTestPosition(pos);
                    drawLineIdx = _caretLineHint;
                }
            }
            else
            {
                caret = pl.Layout.HitTestPosition(pos);
                drawLineIdx = pl.Layout.GetLineIndexForChar(pos);
            }

            // caret.X уже включает FirstLineIndentPt для строки 0 (из HitTestPosition).
            using var paint = new SKPaint { Color = SKColors.Black, StrokeWidth = 1.1f, IsAntialias = false };
            float cx = xPt + caret.X;
            float cy = yPt + (caret.Y - yBase);
            canvas.DrawLine(cx, cy, cx, cy + caret.Height, paint);
        }

    }
}