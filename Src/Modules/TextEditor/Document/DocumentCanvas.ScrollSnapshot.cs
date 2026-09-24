using Avalonia.Threading;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Writersword.Core.Models.Rendering;
using Writersword.Modules.TextEditor.Rendering;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.ViewModels;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Фоновая дорисовка снимка при прокрутке.
    ///
    /// Снимок (overscan-битмап) покрывает вьюпорт с запасом по экрану сверху и снизу.
    /// Пока прокрутка внутри него, кадр — это блит готового снимка. Раньше выход за его
    /// край означал полный рендер всех видимых листов прямо в кадре: при нескольких
    /// листах в ряду это 150–250 мс, и прокрутка дёргалась.
    ///
    /// Теперь:
    /// 1. На подходе к краю снимка (запас меньше половины экрана) следующий снимок,
    ///    отцентрованный по текущей прокрутке, начинает рисоваться в фоновом потоке.
    /// 2. Если прокрутка всё же обогнала дорисовку, кадр не ждёт: прежний снимок
    ///    кладётся на своё место, открывшуюся полосу закрывают пустые листы подложки,
    ///    а готовый снимок подменяет прежний следующим кадром.
    ///
    /// Устаревание результата отслеживает поколение содержимого (_contentGeneration):
    /// его сдвигают InvalidateFull и каждый синхронный полный рендер. Снимок, начатый
    /// при другом поколении, другом масштабе или при поднятом _contentDirty, выбрасывается.
    ///
    /// Правка текста, смена масштаба, чтение и книга идут прежним синхронным путём.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        /// <summary>
        /// Замок прохода содержимого. Общий на все канвасы: рендер текста хранит
        /// подмены чтения и обработчик картинок в статических полях.
        /// </summary>
        private static readonly object ContentPassLock = new();

        // Поколение содержимого. Сдвигается при любой правке, после которой снимок
        // обязан быть перерисован заново.
        private int _contentGeneration;

        // Масштаб, при котором снят текущий _displayImage. Прежний снимок кладётся
        // на экран при выходе за его край только при том же масштабе.
        private double _displayImageZoom = double.NaN;

        // 1 — фоновая дорисовка уже идёт; вторая не запускается.
        private int _bgSnapshotBusy;

        // Офскрин-битмап фоновой дорисовки. Им пользуется только фоновая задача, и
        // одновременно она всегда одна.
        private SKBitmap? _bgSnapshotBitmap;

        // Поднят, если после последнего полного рендера кто-то попросил полный кадр
        // простым InvalidateVisual (без _caretOnlyRedraw). Готовый фоновый снимок в
        // таком случае не подменяет запрошенный полный рендер быстрым путём.
        private volatile bool _fullRenderRequested;

        // Окно, по которому проход содержимого выбирает листы и строки. У фоновой
        // дорисовки оно своё — полоса её снимка, а не текущий вьюпорт. Поля потоковые:
        // render-поток и UI-поток видят живую прокрутку, как и раньше.
        [ThreadStatic] private static double? t_contentViewTopPx;
        [ThreadStatic] private static double? t_contentViewHeightPx;

        /// <summary>Верх окна прохода содержимого, px документа.</summary>
        private double ContentScrollTopPx => t_contentViewTopPx ?? _scrollOffsetY;

        /// <summary>Высота окна прохода содержимого, px.</summary>
        private double ContentViewportPx => t_contentViewHeightPx ?? Math.Max(_viewportHeight, 100);

        /// <summary>
        /// Все внутренние запросы перерисовки канваса идут сюда. Запрос без
        /// _caretOnlyRedraw означает «нужен полный кадр» — это запоминается, чтобы
        /// готовый фоновый снимок его не подменил.
        /// </summary>
        private new void InvalidateVisual()
        {
            if (!_caretOnlyRedraw) _fullRenderRequested = true;
            base.InvalidateVisual();
        }

        /// <summary>
        /// Полоса снимка для заданной прокрутки: вьюпорт плюс по экрану сверху и снизу,
        /// прижатая к границам документа. Тот же расчёт, что в RenderWithSKCanvas.
        /// </summary>
        private static (float TopPx, int HeightPx) SnapshotBand(
            float scrollY, float viewportPx, float docHeightPx)
        {
            float overlapPx = viewportPx;
            float topY = Math.Max(scrollY - overlapPx, 0f);
            float botY = Math.Min(topY + viewportPx + overlapPx * 2f, docHeightPx);
            topY = Math.Max(botY - viewportPx - overlapPx * 2f, 0f);
            return (topY, (int)Math.Max(botY - topY, 1));
        }

        /// <summary>
        /// Можно ли вести снимок фоном. Чтение и книга, жест масштаба и переход
        /// остаются на прежнем синхронном пути.
        /// </summary>
        private bool AsyncSnapshotAllowed
            => !_zooming && !_isTransitioning && !SpreadMode && !ReadingActive;

        /// <summary>
        /// Прокрутка вышла за снимок: кладём прежний снимок на его место и запускаем
        /// фоновую дорисовку. false — прежнего снимка нет или он не годится (другая
        /// ширина или масштаб), тогда кадр идёт синхронным полным рендером.
        /// Координаты канваса — px документа, как у блита снимка.
        /// </summary>
        private bool TryDrawStaleSnapshotAndRenderAsync(
            SKCanvas canvas,
            List<ParaLayout> layouts,
            List<PageRect> pages,
            double canvasWidth,
            float scale,
            float scrollY,
            float viewportPx,
            float docHeightPx,
            int pixelW)
        {
            if (!AsyncSnapshotAllowed) return false;

            double zoomNow = Zoom;
            bool drew = false;

            lock (_bitmapLock)
            {
                if (_displayImage is not null
                    && _displayImage.Width == pixelW
                    && Math.Abs(_displayImageZoom - zoomNow) < 1e-9)
                {
                    canvas.DrawImage(_displayImage, 0, _lastFullRenderScrollY);
                    drew = true;
                }
            }

            if (!drew) return false;

            canvas.Save();
            canvas.Scale(scale, scale);
            DrawSelectionOverlay(canvas, layouts, pages, canvasWidth);
            canvas.Restore();

            if (CaretDrawable)
            {
                canvas.Save();
                canvas.Scale(scale, scale);
                DrawCaretOnCanvas(canvas, layouts, pages, canvasWidth);
                canvas.Restore();
            }

            if (_spreadCornerHint > 0.01f)
            {
                canvas.Save();
                canvas.Scale(scale, scale);
                DrawSpreadCornerHint(canvas);
                canvas.Restore();
            }

            PerfCount("r.async.stale");
            StartBackgroundSnapshot(scrollY, viewportPx, docHeightPx, pixelW, scale);
            return true;
        }

        /// <summary>
        /// Снимок ещё покрывает экран, но запас до его края в сторону, где документ
        /// продолжается, меньше половины экрана — следующий снимок готовим заранее.
        /// </summary>
        private void MaybePrefetchSnapshot(
            float scrollY, float viewportPx, float docHeightPx, int pixelW, float scale)
        {
            if (!AsyncSnapshotAllowed) return;
            if (Volatile.Read(ref _bgSnapshotBusy) != 0) return;

            float imageTop;
            float imageBottom;
            lock (_bitmapLock)
            {
                if (_displayImage is null) return;
                imageTop = _lastFullRenderScrollY;
                imageBottom = _lastFullRenderScrollY + _displayImage.Height;
            }

            float threshold = viewportPx * 0.5f;
            bool moreAbove = imageTop > 0.5f;
            bool moreBelow = imageBottom < docHeightPx - 0.5f;

            bool nearTop = moreAbove && scrollY - imageTop < threshold;
            bool nearBottom = moreBelow && imageBottom - (scrollY + viewportPx) < threshold;

            if (!nearTop && !nearBottom) return;

            PerfCount("r.async.prefetch");
            StartBackgroundSnapshot(scrollY, viewportPx, docHeightPx, pixelW, scale);
        }

        /// <summary>
        /// Запуск фоновой дорисовки снимка, отцентрованного по прокрутке scrollY.
        /// Зовётся с render-потока. Одновременно идёт не больше одной дорисовки.
        /// </summary>
        private void StartBackgroundSnapshot(
            float scrollY, float viewportPx, float docHeightPx, int pixelW, float scale)
        {
            if (Interlocked.CompareExchange(ref _bgSnapshotBusy, 1, 0) != 0) return;

            int generation = Volatile.Read(ref _contentGeneration);
            double zoomAtStart = Zoom;
            var mode = DocVm?.ViewMode ?? EditorViewMode.Draft;
            var (bandTopPx, bandHeightPx) = SnapshotBand(scrollY, viewportPx, docHeightPx);

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

            Task.Run(() =>
            {
                long perfTs = PerfNow();
                SKImage? newImage = null;

                try
                {
                    var bitmap = _bgSnapshotBitmap;
                    if (bitmap is null || bitmap.Width != pixelW || bitmap.Height != bandHeightPx)
                    {
                        bitmap?.Dispose();
                        bitmap = new SKBitmap(pixelW, bandHeightPx, SKColorType.Bgra8888, SKAlphaType.Premul);
                        _bgSnapshotBitmap = bitmap;
                    }

                    float bandTopPt = scale > 0f ? bandTopPx / scale : 0f;

                    // Окно прохода — вся полоса снимка: иначе проход взял бы листы вокруг
                    // текущего вьюпорта, а края снимка остались бы пустыми.
                    t_contentViewTopPx = bandTopPx;
                    t_contentViewHeightPx = bandHeightPx;
                    _selectionAsOverlay = true;

                    try
                    {
                        using var offscreen = new SKCanvas(bitmap);
                        offscreen.Clear(SKColors.Transparent);
                        offscreen.Save();
                        offscreen.Scale(scale, scale);
                        offscreen.Translate(0f, -bandTopPt);

                        if (mode == EditorViewMode.Page || SpreadMode || ReadingRibbon)
                            RenderPageMode(offscreen, layouts, pages, tables, images, canvasHeightPt, canvasWidth, false);
                        else
                            RenderFlowMode(offscreen, mode, layouts, tables, images, canvasHeightPt, canvasWidth, false);

                        offscreen.Restore();
                    }
                    finally
                    {
                        _selectionAsOverlay = false;
                        t_contentViewTopPx = null;
                        t_contentViewHeightPx = null;
                    }

                    newImage = SKImage.FromPixelCopy(
                        new SKImageInfo(bitmap.Width, bitmap.Height,
                            SKColorType.Bgra8888, SKAlphaType.Premul),
                        bitmap.GetPixels(),
                        bitmap.RowBytes);

                    bool accepted = false;

                    lock (_bitmapLock)
                    {
                        // Содержимое не менялось с начала дорисовки, масштаб тот же —
                        // снимок верен и подменяет прежний.
                        if (newImage is not null
                            && Volatile.Read(ref _contentGeneration) == generation
                            && !_contentDirty
                            && Math.Abs(Zoom - zoomAtStart) < 1e-9)
                        {
                            var old = _displayImage;
                            _displayImage = newImage;
                            _displayImageZoom = zoomAtStart;
                            _lastFullRenderScrollY = bandTopPx;
                            _displayImageSpreadLeft = -1;
                            newImage = null;
                            accepted = true;

                            // Прежний снимок освобождается на render-потоке в начале
                            // следующего кадра — там, где его гарантированно не рисуют.
                            if (old is not null) _imageDisposeQueue.Enqueue(old);
                        }
                    }

                    PerfTime(accepted ? "r.async.render" : "r.async.dropped", perfTs);

                    if (accepted)
                        Dispatcher.UIThread.Post(OnBackgroundSnapshotReady, DispatcherPriority.Render);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Фоновая дорисовка снимка не удалась");
                }
                finally
                {
                    newImage?.Dispose();
                    Interlocked.Exchange(ref _bgSnapshotBusy, 0);
                }
            });
        }

        /// <summary>
        /// Готовый фоновый снимок — на экран. Быстрым путём, если никто не просил
        /// полного кадра: тогда показ снимка — это блит, а не перерисовка листов.
        /// </summary>
        private void OnBackgroundSnapshotReady()
        {
            if (!_fullRenderRequested && !_contentDirty)
                _caretOnlyRedraw = true;

            base.InvalidateVisual();
        }
    }
}
