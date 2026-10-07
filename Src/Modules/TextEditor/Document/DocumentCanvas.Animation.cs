using Avalonia.Threading;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Анимированные картинки на листе: GIF и WebP из нескольких кадров проигрываются,
    /// как в Word, а не стоят на первом кадре.
    ///
    /// Кадры раскодируются один раз, при загрузке картинки, в готовые растры: каждый
    /// кадр собирается поверх того, от которого он зависит (кадр GIF часто хранит
    /// только изменившийся кусок). Какой кадр показывать, решает время, прошедшее с
    /// загрузки, — все копии одной картинки идут синхронно.
    ///
    /// Перерисовка просится только тогда, когда на экране действительно стоит
    /// анимированная картинка и у неё сменился кадр: рендер отмечает, какой кадр он
    /// нарисовал, таймер сверяет его с текущим. Картинка ушла с экрана — отметок
    /// нет, и таймер засыпает. Печать, PDF и выгрузка берут картинку из файла и
    /// получают первый кадр, как и Word при печати.
    ///
    /// Смена кадра не пересобирает снимок листов. Снимок — это весь текст и все
    /// картинки трёх экранов, и пересобирать его десять раз в секунду значило
    /// держать полный рендер на каждом кадре прокрутки: рядом с анимацией прокрутка
    /// проседала, а память шла вверх сотнями мегабайт. Поэтому картинка, над которой
    /// ничего не лежит (поверх текста, с обтеканием, в строке), в снимок не идёт —
    /// её кадр рисуется поверх готового снимка, как каретка (DrawAnimationOverlay).
    /// В снимок запекается только та, что лежит под чем-то: за текстом, под другой
    /// картинкой или фигурой, выделенная, в предпросмотре обрезки; её кадр, как и
    /// прежде, пересобирает снимок.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        /// <summary>
        /// Предел памяти под кадры одной картинки, байт. Больше — картинка остаётся
        /// на первом кадре: длинный ролик в документе не должен съедать память.
        /// </summary>
        private const long MaxAnimationBytes = 128L * 1024 * 1024;

        /// <summary>
        /// Кадр короче этого (мс) считается записанным без задержки — так делают и
        /// браузеры, и Word: у таких файлов показ с нулевой задержкой превращался бы
        /// в мельтешение.
        /// </summary>
        private const int MinFrameDurationMs = 20;

        /// <summary>Задержка кадра, записанного без неё, мс.</summary>
        private const int DefaultFrameDurationMs = 100;

        /// <summary>Шаг проверки смены кадра, мс.</summary>
        private const int AnimationTickMs = 15;

        /// <summary>Сколько таймер ждёт без нарисованных анимаций, прежде чем уснуть, мс.</summary>
        private const int AnimationIdleStopMs = 2000;

        private sealed class AnimatedImage
        {
            public AnimatedImage(SKImage[] frames, SKBitmap[] bitmaps, int[] durationsMs, int repetitions)
            {
                Frames = frames;
                Bitmaps = bitmaps;
                DurationsMs = durationsMs;
                Repetitions = repetitions;

                int total = 0;
                foreach (int d in durationsMs) total += d;
                TotalMs = Math.Max(total, 1);

                Clock = Stopwatch.StartNew();
            }

            /// <summary>Образы кадров.</summary>
            public SKImage[] Frames { get; }

            /// <summary>
            /// Растры, с которых сняты образы: держатся живыми столько же, сколько
            /// образы (SKImage.FromBitmap вправе не копировать пиксели).
            /// </summary>
            public SKBitmap[] Bitmaps { get; }

            /// <summary>Задержки кадров, мс.</summary>
            public int[] DurationsMs { get; }

            /// <summary>Сколько раз проигрывается ролик; −1 — бесконечно.</summary>
            public int Repetitions { get; }

            /// <summary>Длина одного проигрывания, мс.</summary>
            public int TotalMs { get; }

            /// <summary>Часы ролика: идут с загрузки картинки.</summary>
            public Stopwatch Clock { get; }

            /// <summary>
            /// Ролик доиграл положенное число раз и стоит на последнем кадре: кадр
            /// больше не сменится, и перерисовывать ради него нечего.
            /// </summary>
            public bool IsFinished
                => Repetitions >= 0 && Clock.ElapsedMilliseconds >= (long)TotalMs * (Repetitions + 1L);

            /// <summary>Номер кадра на этот момент.</summary>
            public int CurrentFrameIndex()
            {
                long elapsed = Clock.ElapsedMilliseconds;

                // Ограниченное число проигрываний: после последнего ролик стоит на
                // последнем кадре.
                if (Repetitions >= 0 && elapsed >= (long)TotalMs * (Repetitions + 1L))
                    return Frames.Length - 1;

                long position = elapsed % TotalMs;
                for (int i = 0; i < DurationsMs.Length; i++)
                {
                    if (position < DurationsMs[i]) return i;
                    position -= DurationsMs[i];
                }

                return Frames.Length - 1;
            }
        }

        // Анимированные картинки по имени файла. Под _imageCacheLock, как и кэш картинок.
        private readonly Dictionary<string, AnimatedImage> _animatedImages = new(StringComparer.Ordinal);

        // Какой кадр каждой анимации нарисован последним кадром экрана. Пишет рендер,
        // читает таймер. Под _imageCacheLock.
        private readonly Dictionary<string, int> _animationFramesDrawn = new(StringComparer.Ordinal);

        // Какие анимации последний кадр экрана запёк в снимок, а не нарисовал поверх.
        // Под _imageCacheLock.
        private readonly HashSet<string> _animationBakedDrawn = new(StringComparer.Ordinal);

        private DispatcherTimer? _animationTimer;
        private readonly Stopwatch _animationIdleClock = new();

        /// <summary>
        /// Раскодирует все кадры анимированной картинки. Null — кадр один, формат
        /// анимации не знает или кадры не помещаются в предел памяти.
        /// </summary>
        private AnimatedImage? DecodeAnimation(byte[] bytes, string fileName)
        {
            SKBitmap[]? bitmaps = null;
            int decoded = 0;

            try
            {
                using var data = SKData.CreateCopy(bytes);
                using var codec = SKCodec.Create(data);
                if (codec is null) return null;

                int count = codec.FrameCount;
                if (count <= 1) return null;

                var frameInfos = codec.FrameInfo;
                if (frameInfos is null || frameInfos.Length < count) return null;

                var info = new SKImageInfo(
                    codec.Info.Width, codec.Info.Height, SKImageInfo.PlatformColorType, SKAlphaType.Premul);
                if (info.Width <= 0 || info.Height <= 0) return null;

                long frameBytes = (long)info.RowBytes * info.Height;
                if (frameBytes * count > MaxAnimationBytes) return null;

                bitmaps = new SKBitmap[count];
                var durations = new int[count];
                byte[]? carry = null;

                for (int i = 0; i < count; i++)
                {
                    var bitmap = new SKBitmap(info);
                    bitmaps[i] = bitmap;
                    decoded = i + 1;

                    // Кадр, на котором этот строится: его пиксели кладутся в растр
                    // заранее, и декодер дорисовывает поверх только изменённое.
                    int required = frameInfos[i].RequiredFrame;
                    SKCodecOptions options;
                    if (required >= 0 && required < i)
                    {
                        carry ??= new byte[frameBytes];
                        Marshal.Copy(bitmaps[required].GetPixels(), carry, 0, (int)frameBytes);
                        Marshal.Copy(carry, 0, bitmap.GetPixels(), (int)frameBytes);
                        options = new SKCodecOptions(i, required);
                    }
                    else
                    {
                        bitmap.Erase(SKColors.Transparent);
                        options = new SKCodecOptions(i);
                    }

                    var result = codec.GetPixels(info, bitmap.GetPixels(), options);
                    if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
                        throw new InvalidOperationException($"Кадр {i} не раскодирован: {result}");

                    bitmap.NotifyPixelsChanged();

                    int duration = frameInfos[i].Duration;
                    durations[i] = duration < MinFrameDurationMs ? DefaultFrameDurationMs : duration;
                }

                var frames = new SKImage[count];
                for (int i = 0; i < count; i++)
                    frames[i] = SKImage.FromBitmap(bitmaps[i]);

                _logger.Debug("[IMG] Анимация {File}: {Count} кадров, {W}×{H}",
                    fileName, count, info.Width, info.Height);

                return new AnimatedImage(frames, bitmaps, durations, codec.RepetitionCount);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[IMG] Кадры анимации не раскодированы: {File}", fileName);

                if (bitmaps is not null)
                    for (int i = 0; i < decoded; i++)
                        bitmaps[i]?.Dispose();

                return null;
            }
        }

        /// <summary>
        /// Текущий кадр анимированной картинки и отметка, что он нарисован. Вызывается
        /// под _imageCacheLock. False — картинка не анимирована.
        /// </summary>
        private bool TryGetAnimationFrame(string fileName, out SKImage frame)
        {
            frame = null!;
            if (!_animatedImages.TryGetValue(fileName, out var animation)) return false;

            int index = animation.CurrentFrameIndex();
            frame = animation.Frames[index];
            _animationFramesDrawn[fileName] = index;
            _animationBakedDrawn.Add(fileName);

            if (_animationTimer is null || !_animationTimer.IsEnabled)
                Dispatcher.UIThread.Post(EnsureAnimationTimer);

            return true;
        }

        /// <summary>Запускает таймер смены кадров, если он стоит.</summary>
        private void EnsureAnimationTimer()
        {
            _animationIdleClock.Restart();

            if (_animationTimer is null)
            {
                _animationTimer = new DispatcherTimer(DispatcherPriority.Render)
                {
                    Interval = TimeSpan.FromMilliseconds(AnimationTickMs)
                };
                _animationTimer.Tick += OnAnimationTick;
            }

            if (!_animationTimer.IsEnabled) _animationTimer.Start();
        }

        private void OnAnimationTick(object? sender, EventArgs e)
        {
            bool frameChanged = false;
            bool bakedChanged = false;
            bool anyDrawn = false;

            lock (_imageCacheLock)
            {
                foreach (var (fileName, drawnIndex) in _animationFramesDrawn)
                {
                    if (!_animatedImages.TryGetValue(fileName, out var animation)) continue;

                    int currentIndex = animation.CurrentFrameIndex();
                    if (currentIndex == drawnIndex)
                    {
                        // Доигравший ролик на экране таймер не держит.
                        if (!animation.IsFinished) anyDrawn = true;
                        continue;
                    }

                    frameChanged = true;

                    // Кадр картинки, запечённой в снимок, меняется только пересборкой
                    // снимка; кадр картинки поверх снимка — одной перерисовкой поверх.
                    if (_animationBakedDrawn.Contains(fileName)) bakedChanged = true;
                }

                // Отметки снимаются: следующий кадр экрана поставит их заново только
                // тем анимациям, которые на нём действительно есть.
                if (frameChanged)
                {
                    _animationFramesDrawn.Clear();
                    _animationBakedDrawn.Clear();
                }
            }

            if (frameChanged)
            {
                _animationIdleClock.Restart();

                if (bakedChanged)
                {
                    _contentDirty = true;
                }
                else
                {
                    // Все сменившиеся кадры рисуются поверх снимка: снимок остаётся,
                    // кадр экрана — блит снимка и сами картинки (DrawAnimationOverlay).
                    // _contentDirty не трогаем: если полный рендер уже заказан, он и
                    // выполнится.
                    _caretOnlyRedraw = true;
                }

                InvalidateVisual();
                return;
            }

            if (anyDrawn) _animationIdleClock.Restart();

            if (_animationIdleClock.ElapsedMilliseconds > AnimationIdleStopMs)
                _animationTimer?.Stop();
        }
    
        // ── Анимация поверх снимка ────────────────────────────────────────

        /// <summary>
        /// Кадры анимаций рисуются поверх снимка в виде страниц — и по одной, и
        /// рядом: наложение переносит кадр на место листа в показе тем же сдвигом,
        /// что и проход листов (PageVisualDelta). В книге и ленте снимок свой, со
        /// своими переворотами и склейкой, и кадр там, как и прежде, пересобирает
        /// снимок. При выгрузке анимаций нет вовсе.
        ///
        /// Страницы рядом выпадать отсюда не должны: там снимок — это дюжина листов, и
        /// пересборка на каждой смене кадра стоила около 140 мс — прокрутка рядом с
        /// анимацией шла рывками по четыре-пять раз в секунду.
        /// </summary>
        private bool AnimationOverlayMode
            => !ExportPassActive
               && !SpreadMode
               && !ReadingRibbon
               && (DocVm?.ViewMode ?? EditorViewMode.Draft) == EditorViewMode.Page;

        private bool IsAnimatedFile(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            lock (_imageCacheLock) return _animatedImages.ContainsKey(fileName);
        }

        /// <summary>
        /// Картинку рисует проход поверх снимка, а в сам снимок она не идёт. Решение
        /// одно на оба прохода: снимок пропускает ровно то, что потом рисует
        /// наложение, — иначе картинка пропала бы или легла дважды.
        ///
        /// Поверх снимка рисуется только то, над чем в снимке ничего нет: иначе кадр
        /// закрыл бы лежащее сверху. Поэтому не идут:
        ///   картинка за текстом — над ней текст;
        ///   картинка, которую перекрывает картинка или фигура, нарисованная позже;
        ///   выделенная, в предпросмотре обрезки или переполнения, попавшая в
        ///   выделение текста — поверх неё в снимке рамка, маркеры или заливка;
        ///   картинка мимо своего листа — она бледная и заштрихована;
        ///   картинка в строке, срезанная по высоте строки, и картинка в ячейке —
        ///   их клип наложению неизвестен.
        /// </summary>
        private bool IsOverlayAnimation(ImageEntry entry, List<ImageEntry> images, List<PageRect> pages)
        {
            if (!AnimationOverlayMode) return false;

            var block = entry.Block;
            if (!IsAnimatedFile(block.ImageFileName)) return false;

            if (ReferenceEquals(block, _selectedImage)) return false;
            if (_imageCropMode && ReferenceEquals(block, _cropImage)) return false;
            if (_imageOverflowPreviewMode && ReferenceEquals(block, _imageOverflowPreviewBlock)) return false;
            if (_imagesInTextSelection.Contains(block)) return false;

            if (entry.PageIndex < 0 || entry.PageIndex >= pages.Count) return false;

            if (entry.InLine)
            {
                if (entry.InCell) return false;
                if (OverflowsLineBox(block)) return false;
            }
            else
            {
                var wrap = block.WrapMode;
                if (wrap != WrapMode.InFront && wrap != WrapMode.Square && wrap != WrapMode.Tight) return false;
                if (OffPageMarkersVisible && IsImageOffItsPage(entry, pages[entry.PageIndex])) return false;
            }

            var bounds = AnimationOverlayBounds(entry.XPt, entry.Ypt, entry.WidthPt, entry.HeightPt);

            // Картинки поверх текста рисуются по порядку списка: всё, что идёт после
            // этой и задевает её, лежит над ней.
            bool after = false;
            foreach (var other in images)
            {
                if (ReferenceEquals(other, entry)) { after = true; continue; }
                if (other.PageIndex != entry.PageIndex) continue;

                bool drawnAbove = entry.InLine
                    ? !other.InLine && other.Block.WrapMode != WrapMode.Behind
                    : after && !other.InLine
                      && other.Block.WrapMode is WrapMode.InFront or WrapMode.Square or WrapMode.Tight;
                if (!drawnAbove) continue;

                if (bounds.IntersectsWith(AnimationOverlayBounds(other.XPt, other.Ypt, other.WidthPt, other.HeightPt)))
                    return false;
            }

            // Фигуры поверх текста рисуются после картинок.
            List<ShapeEntry> shapes;
            lock (_renderLock) { shapes = _shapes; }
            foreach (var shape in shapes)
            {
                if (shape.PageIndex != entry.PageIndex) continue;
                if (shape.Block.WrapMode == WrapMode.Behind) continue;

                if (bounds.IntersectsWith(AnimationOverlayBounds(shape.XPt, shape.Ypt, shape.WidthPt, shape.HeightPt)))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Картинка в строке: решение по её записи в списке картинок — тот же ответ,
        /// что получит наложение. Записи нет (картинка ещё не разложена) — картинка
        /// идёт в снимок.
        /// </summary>
        private bool IsOverlayInlineAnimation(ImageBlock block)
        {
            if (!AnimationOverlayMode || !IsAnimatedFile(block.ImageFileName)) return false;

            List<ImageEntry> images;
            List<PageRect> pages;
            lock (_renderLock)
            {
                images = _images;
                pages = _pages;
            }

            foreach (var entry in images)
            {
                if (!entry.InLine || !ReferenceEquals(entry.Block, block)) continue;
                return IsOverlayAnimation(entry, images, pages);
            }

            return false;
        }

        /// <summary>
        /// Габарит, в котором объект может оказаться при любом повороте: круг по
        /// диагонали. Проверке перекрытия хватает и его — лишний отказ значит лишь,
        /// что картинка пойдёт в снимок, как раньше.
        /// </summary>
        private static SKRect AnimationOverlayBounds(float xPt, float yPt, float widthPt, float heightPt)
        {
            float cx = xPt + widthPt / 2f;
            float cy = yPt + heightPt / 2f;
            float r = MathF.Sqrt(widthPt * widthPt + heightPt * heightPt) / 2f + 1f;
            return new SKRect(cx - r, cy - r, cx + r, cy + r);
        }

        /// <summary>
        /// Текущий кадр для наложения: отметка, что он нарисован, — но не в снимке.
        /// </summary>
        private bool TryGetOverlayAnimationFrame(string fileName, out SKImage frame)
        {
            frame = null!;

            lock (_imageCacheLock)
            {
                if (!_animatedImages.TryGetValue(fileName, out var animation)) return false;

                int index = animation.CurrentFrameIndex();
                frame = animation.Frames[index];
                _animationFramesDrawn[fileName] = index;
            }

            if (_animationTimer is null || !_animationTimer.IsEnabled)
                Dispatcher.UIThread.Post(EnsureAnimationTimer);

            return true;
        }

        // Своя кисть наложения: кисть картинок общая с проходом снимка, а его может
        // в это же время рисовать фоновый поток прокрутки.
        private readonly SKPaint _paintAnimationOverlay = new() { IsAntialias = true };

        /// <summary>
        /// Кадры анимаций поверх снимка. Холст — в точках документа (масштаб уже
        /// поставлен), как у наложения выделения.
        /// </summary>
        private void DrawAnimationOverlay(SKCanvas canvas, List<PageRect> pages, List<ImageEntry> images, double canvasWidth)
        {
            if (!AnimationOverlayMode || images.Count == 0) return;

            lock (_imageCacheLock)
            {
                if (_animatedImages.Count == 0) return;
            }

            canvas.Save();

            // Тот же сдвиг до центра, что у прохода листов (RenderPageModeCore): он есть
            // только у листов по одному.
            if (_pagesPerRow <= 1)
            {
                float canvasWPt = (float)(canvasWidth * PxToPt);
                float curPageXPt = Math.Max((canvasWPt - GetPageWidthPt()) / 2f, 0f);
                float pageXShiftPt = curPageXPt - _layoutPageXPt;
                if (MathF.Abs(pageXShiftPt) > 0.01f) canvas.Translate(pageXShiftPt, 0f);
            }

            var (firstPage, lastPage) = GetVisiblePageRange(pages);

            foreach (var entry in images)
            {
                if (entry.PageIndex < firstPage || entry.PageIndex > lastPage) continue;
                if (!IsAnimatedFile(entry.Block.ImageFileName)) continue;
                if (!IsOverlayAnimation(entry, images, pages)) continue;
                if (!TryGetOverlayAnimationFrame(entry.Block.ImageFileName, out var frame)) continue;

                var page = pages[entry.PageIndex];
                var block = entry.Block;

                canvas.Save();

                // Лист на своём месте в показе: при листах рядом содержимое страницы
                // переносится туда, как в проходе листов. По одному сдвиг нулевой.
                var (dxPt, dyPt) = PageVisualDelta(entry.PageIndex, pages);
                if (dxPt != 0f || dyPt != 0f) canvas.Translate(dxPt, dyPt);

                // Клип листа: у плавающей — весь лист, как в проходе снимка; у картинки в
                // строке — высота листа, как у ClipInlineObjectToPage.
                if (entry.InLine)
                {
                    const float unboundedPt = 1_000_000f;
                    canvas.ClipRect(new SKRect(-unboundedPt, page.Ypt, unboundedPt, page.Ypt + page.HeightPt));
                }
                else
                {
                    canvas.ClipRect(new SKRect(
                        page.PadLeftPt, page.Ypt, page.PadLeftPt + page.WidthPt, page.Ypt + page.HeightPt));
                }

                float rotDeg = (float)block.RotationDeg;
                float cx = entry.XPt + entry.WidthPt / 2f;
                float cy = entry.Ypt + entry.HeightPt / 2f;
                if (rotDeg != 0f) canvas.RotateDegrees(rotDeg, cx, cy);
                if (block.FlipHorizontal || block.FlipVertical)
                    canvas.Scale(block.FlipHorizontal ? -1f : 1f, block.FlipVertical ? -1f : 1f, cx, cy);

                var rect = new SKRect(entry.XPt, entry.Ypt, entry.XPt + entry.WidthPt, entry.Ypt + entry.HeightPt);
                DrawAnimationFrame(canvas, block, frame, rect);

                canvas.Restore();
            }

            canvas.Restore();
        }

        /// <summary>
        /// Кадр картинки на её месте: обрезка, форма, непрозрачность и рамка — так же,
        /// как картинку рисует проход снимка.
        /// </summary>
        private void DrawAnimationFrame(SKCanvas canvas, ImageBlock block, SKImage frame, SKRect rect)
        {
            byte alpha = (byte)Math.Clamp(block.Opacity * 255.0, 0.0, 255.0);
            _paintAnimationOverlay.Color = new SKColor(0xFF, 0xFF, 0xFF, alpha);

            float srcW = frame.Width;
            float srcH = frame.Height;
            var src = new SKRect(
                srcW * (float)Math.Clamp(block.CropLeftFrac, 0.0, 0.95),
                srcH * (float)Math.Clamp(block.CropTopFrac, 0.0, 0.95),
                srcW * (float)(1.0 - Math.Clamp(block.CropRightFrac, 0.0, 0.95)),
                srcH * (float)(1.0 - Math.Clamp(block.CropBottomFrac, 0.0, 0.95)));
            if (src.Right <= src.Left + 1f) src.Right = src.Left + 1f;
            if (src.Bottom <= src.Top + 1f) src.Bottom = src.Top + 1f;

            bool shapeClip = PushImageShapeClip(canvas, block, rect);

            // Кадр — готовой копией экранного размера: пересчёт кадра из файла на
            // каждом кадре экрана стоил бы столько же, сколько в снимке.
            bool imageCacheBefore = Rendering.ScreenImageCache.Enter(!ExportPassActive);
            try
            {
                Rendering.ScreenImageCache.DrawImage(canvas, frame, src, rect, _paintAnimationOverlay);
            }
            finally
            {
                Rendering.ScreenImageCache.Exit(imageCacheBefore);
            }

            if (shapeClip) canvas.Restore();

            DrawImageBorder(canvas, rect, block, alpha);
        }
    }
}
