using Avalonia.Controls;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Плавная перекладка листов в ряд после жеста масштаба.
    ///
    /// В авто-режиме сетки число листов в ряду зависит от масштаба. Пока масштаб
    /// меняется, оно заморожено, а когда масштаб встал, листы перекладываются в ряды
    /// под новую ширину. Раньше это происходило за один кадр: лист под кареткой
    /// оставался на месте, а все остальные мгновенно оказывались в других рядах и
    /// колонках — на глаз документ прыгал.
    ///
    /// Теперь каждый лист за короткое время переезжает со своего прежнего места на
    /// новое. Содержимое листов берётся из снимка кадра, сделанного до жеста: перекладка
    /// масштаб не меняет, и снимок верен для каждого листа, меняется только его место.
    /// Лист, которого в снимке нет, едет пустым листом. Когда переезд закончен, кадр
    /// рисуется полностью и уже по новой раскладке.
    ///
    /// Любая правка содержимого, новый жест масштаба или смена снимка обрывают переезд:
    /// дальше кадр идёт обычным путём.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        // Длительность переезда листов, мс.
        private const double PageReflowDurationMs = 280.0;

        private sealed class PageReflowState
        {
            /// <summary>Снимок кадра, из которого берутся листы.</summary>
            public required SKImage? Image { get; init; }

            /// <summary>Место каждого листа в снимке, px снимка; пусто — листа в снимке нет.</summary>
            public required SKRect[] SourcePx { get; init; }

            /// <summary>Прежнее место каждого листа, px холста при новой прокрутке.</summary>
            public required SKRect[] FromPx { get; init; }

            /// <summary>Новое место каждого листа, px холста.</summary>
            public required SKRect[] ToPx { get; init; }

            /// <summary>Тень листа, px.</summary>
            public required float ShadowPx { get; init; }

            /// <summary>Поколение содержимого, при котором начат переезд.</summary>
            public required int Generation { get; init; }

            public required long StartTimestamp { get; init; }
        }

        /// <summary>Прежние места листов, снятые до перекладки.</summary>
        private sealed class PageReflowOrigin
        {
            public required SKImage? Image { get; init; }
            public required SKRect[] SourcePx { get; init; }
            public required SKRect[] ViewPx { get; init; }
        }

        // Идущий переезд. Ставится и снимается в UI-потоке, читается отрисовкой.
        private volatile PageReflowState? _pageReflow;

        private bool PageReflowRunning => _pageReflow is not null;

        private readonly SKPaint _paintPageReflowImage = new() { IsAntialias = false };

        /// <summary>
        /// Место листа на холсте, пт: с разнесением в ряд или с до-центрированием
        /// одиночного листа по живой ширине холста — как его ставит отрисовка.
        /// </summary>
        private SKRect PageVisualRectPt(int pageIdx, List<PageRect> pages)
        {
            var page = pages[pageIdx];
            float leftPt;
            float topPt;

            if (_pagesPerRow > 1)
            {
                var (dx, dy) = PageVisualDelta(pageIdx, pages);
                leftPt = page.PadLeftPt + dx;
                topPt = page.Ypt + dy;
            }
            else
            {
                leftPt = page.PadLeftPt + GetPageShiftXPt();
                topPt = page.Ypt;
            }

            return SKRect.Create(leftPt, topPt, page.WidthPt, page.HeightPt);
        }

        /// <summary>
        /// Снимает места листов до перекладки: где каждый лист стоит в окне и где он
        /// лежит в снимке кадра. null — переезд здесь не нужен (не режим страниц, чтение,
        /// листов нет).
        /// </summary>
        private PageReflowOrigin? CapturePageReflowOrigin()
        {
            if (_parentScrollViewer is not { } sv) return null;
            if (DocVm?.ViewMode != EditorViewMode.Page || ReadingActive || SpreadMode) return null;

            List<PageRect> pages;
            lock (_renderLock) { pages = _pages; }
            if (pages.Count == 0) return null;

            double zoom = Math.Max(Zoom, 0.01);
            float scale = (float)(PtToPx * zoom);
            float offsetX = (float)sv.Offset.X;
            float offsetY = (float)sv.Offset.Y;

            SKImage? image;
            double imageZoom;
            double imageLeftPx;
            float imageTopPx;
            lock (_bitmapLock)
            {
                image = _displayImage;
                imageZoom = _displayImageZoom;
                imageLeftPx = _displayImageLeftPx;
                imageTopPx = _lastFullRenderScrollY;
            }

            bool imageUsable = image is not null && !double.IsNaN(imageZoom) && imageZoom > 0;
            float imageScale = imageUsable ? (float)(PtToPx * imageZoom) : 0f;
            var imageBounds = imageUsable ? SKRect.Create(0, 0, image!.Width, image.Height) : SKRect.Empty;

            // Левый край содержимого в той же сетке, что и места листов: разность с ним
            // не зависит от того, по какой ширине холста сетка центрирована.
            float contentLeftPt = (float)(ContentLeftPx(_canvasWidth, 1.0) / PtToPx);

            var view = new SKRect[pages.Count];
            var source = new SKRect[pages.Count];

            for (int i = 0; i < pages.Count; i++)
            {
                var rectPt = PageVisualRectPt(i, pages);

                view[i] = new SKRect(
                    rectPt.Left * scale - offsetX,
                    rectPt.Top * scale - offsetY,
                    rectPt.Right * scale - offsetX,
                    rectPt.Bottom * scale - offsetY);

                if (!imageUsable)
                {
                    source[i] = SKRect.Empty;
                    continue;
                }

                var src = SKRect.Create(
                    (float)(imageLeftPx + (rectPt.Left - contentLeftPt) * imageScale),
                    rectPt.Top * imageScale - imageTopPx,
                    rectPt.Width * imageScale,
                    rectPt.Height * imageScale);

                source[i] = src.IntersectsWith(imageBounds) ? src : SKRect.Empty;
            }

            return new PageReflowOrigin { Image = imageUsable ? image : null, SourcePx = source, ViewPx = view };
        }

        /// <summary>
        /// Запускает переезд листов с прежних мест на новые. Зовётся после пересборки
        /// раскладки и возврата листа привязки на место — прокрутка уже новая.
        /// </summary>
        private void StartPageReflow(PageReflowOrigin origin)
        {
            CancelPageReflow();

            if (_parentScrollViewer is not { } sv) return;

            List<PageRect> pages;
            lock (_renderLock) { pages = _pages; }

            // Пересборка изменила число листов — сопоставить прежние места не с чем.
            if (pages.Count != origin.ViewPx.Length || pages.Count == 0) return;

            double zoom = Math.Max(Zoom, 0.01);
            float scale = (float)(PtToPx * zoom);
            float offsetX = (float)sv.Offset.X;
            float offsetY = (float)sv.Offset.Y;

            var from = new SKRect[pages.Count];
            var to = new SKRect[pages.Count];
            bool moved = false;

            for (int i = 0; i < pages.Count; i++)
            {
                var rectPt = PageVisualRectPt(i, pages);
                to[i] = new SKRect(
                    rectPt.Left * scale, rectPt.Top * scale,
                    rectPt.Right * scale, rectPt.Bottom * scale);

                var v = origin.ViewPx[i];
                from[i] = new SKRect(v.Left + offsetX, v.Top + offsetY, v.Right + offsetX, v.Bottom + offsetY);

                if (Math.Abs(from[i].Left - to[i].Left) > 0.5f || Math.Abs(from[i].Top - to[i].Top) > 0.5f)
                    moved = true;
            }

            if (!moved) return;

            _pageReflow = new PageReflowState
            {
                Image = origin.Image,
                SourcePx = origin.SourcePx,
                FromPx = from,
                ToPx = to,
                ShadowPx = 3f * scale,
                Generation = System.Threading.Interlocked.CompareExchange(ref _contentGeneration, 0, 0),
                StartTimestamp = Stopwatch.GetTimestamp()
            };

            _logger.Debug("[ZOOM] Листы переезжают в новую сетку: {Count} листов, {Duration:F0} мс", pages.Count, PageReflowDurationMs);

            InvalidateVisual();
            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnPageReflowFrame);
        }

        /// <summary>Обрывает переезд; кадр дальше идёт обычным путём.</summary>
        private void CancelPageReflow()
        {
            if (_pageReflow is null) return;
            _pageReflow = null;
            InvalidateFull();
        }

        private void OnPageReflowFrame(TimeSpan timestamp)
        {
            var state = _pageReflow;
            if (state is null) return;

            double elapsedMs = (Stopwatch.GetTimestamp() - state.StartTimestamp) * 1000.0 / Stopwatch.Frequency;
            if (elapsedMs >= PageReflowDurationMs)
            {
                // Переезд закончен: полный кадр по новой раскладке.
                _pageReflow = null;
                InvalidateFull();
                return;
            }

            InvalidateVisual();
            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnPageReflowFrame);
        }

        /// <summary>
        /// Кадр переезда: фон холста, листы на промежуточных местах, содержимое листов —
        /// из снимка. false — переезда нет или он оборван, кадр идёт обычным путём.
        /// </summary>
        private bool TryDrawPageReflow(SKCanvas canvas, float scale, float canvasHeightPt, double canvasWidth)
        {
            var state = _pageReflow;
            if (state is null) return false;

            // Содержимое сменилось после начала переезда — снимок его уже не отражает.
            if (System.Threading.Interlocked.CompareExchange(ref _contentGeneration, 0, 0) != state.Generation)
            {
                _pageReflow = null;
                return false;
            }

            double elapsedMs = (Stopwatch.GetTimestamp() - state.StartTimestamp) * 1000.0 / Stopwatch.Frequency;
            float t = (float)Math.Clamp(elapsedMs / PageReflowDurationMs, 0.0, 1.0);

            // Плавный разгон и плавное торможение.
            float k = t < 0.5f
                ? 4f * t * t * t
                : 1f - MathF.Pow(-2f * t + 2f, 3f) / 2f;

            // Фон холста — тот же, что у подложки.
            double z = Math.Max(Zoom, 0.01);
            float canvasWPt = (float)(canvasWidth * PxToPt);
            float boundsWPt = (float)(Bounds.Width / (PtToPx * z));
            float boundsHPt = (float)(Bounds.Height / (PtToPx * z));
            canvas.Save();
            canvas.Scale(scale, scale);
            DrawCanvasBackdrop(canvas,
                Math.Max(canvasWPt, boundsWPt) + 2f,
                Math.Max(canvasHeightPt, boundsHPt) + 2f);
            canvas.Restore();

            float viewTop = (float)_scrollOffsetY;
            float viewH = _viewportHeight > 0 ? (float)_viewportHeight : (float)Bounds.Height;
            float visibleTop = viewTop - viewH;
            float visibleBottom = viewTop + viewH * 2f;

            var paper = PagePaint();

            lock (_bitmapLock)
            {
                // Снимок подменили или освободили — содержимое листов брать неоткуда,
                // листы едут пустыми.
                var image = state.Image is not null && ReferenceEquals(state.Image, _displayImage)
                    ? state.Image
                    : null;

                for (int i = 0; i < state.ToPx.Length; i++)
                {
                    var from = state.FromPx[i];
                    var to = state.ToPx[i];

                    var dest = new SKRect(
                        from.Left + (to.Left - from.Left) * k,
                        from.Top + (to.Top - from.Top) * k,
                        from.Right + (to.Right - from.Right) * k,
                        from.Bottom + (to.Bottom - from.Bottom) * k);

                    if (dest.Bottom < visibleTop || dest.Top > visibleBottom) continue;

                    var shadow = dest;
                    shadow.Offset(state.ShadowPx, state.ShadowPx);
                    canvas.DrawRect(shadow, _paintPageShadow);
                    canvas.DrawRect(dest, paper);

                    if (image is null) continue;

                    var src = state.SourcePx[i];
                    if (src.IsEmpty) continue;

                    // Часть листа за краем снимка: берётся только то, что в снимке есть,
                    // и кладётся на соответствующую часть листа.
                    var clipped = src;
                    if (!clipped.IntersectsWith(SKRect.Create(0, 0, image.Width, image.Height))) continue;
                    clipped.Intersect(SKRect.Create(0, 0, image.Width, image.Height));

                    float kx = dest.Width / src.Width;
                    float ky = dest.Height / src.Height;
                    var clippedDest = new SKRect(
                        dest.Left + (clipped.Left - src.Left) * kx,
                        dest.Top + (clipped.Top - src.Top) * ky,
                        dest.Left + (clipped.Right - src.Left) * kx,
                        dest.Top + (clipped.Bottom - src.Top) * ky);

                    canvas.DrawImage(image, clipped, clippedDest, ZoomPreviewSampling, _paintPageReflowImage);
                }
            }

            PerfCount("r.reflow");
            return true;
        }
    }
}
