using System;
using System.Collections.Generic;
using SkiaSharp;

namespace Writersword.Modules.TextEditor.Rendering
{
    /// <summary>
    /// Картинки документа, уже приведённые к размеру, в котором они стоят на экране.
    ///
    /// Снимок листов рисуется на процессоре, и всякий раз — заново целиком: при
    /// прокрутке новый снимок на три экрана готовится через каждые полэкрана.
    /// Картинка в нём каждый раз пересчитывалась из файла в свой экранный размер —
    /// кубическим фильтром (шестнадцать точек файла на точку экрана) или усреднением
    /// по уменьшенным копиям, которые для большой картинки ещё и строились заново,
    /// как только вытеснялись из кэша Skia. Пока картинок на листе две-три, этого не
    /// видно; когда их десятки, снимок не успевает за прокруткой, и кадры
    /// проседают — тем сильнее, чем больше картинок.
    ///
    /// Здесь пересчёт делается один раз на картинку и её экранный размер, тем же
    /// фильтром, что и прежде, — качество не меняется. Дальше на снимок ложится уже
    /// готовая копия точка в точку, и это простое копирование. Размер меняется
    /// только с масштабом; копии прежних масштабов вытесняются по давности, общий
    /// объём ограничен.
    ///
    /// Кэш работает только для экрана: проход содержимого включает его на время
    /// отрисовки (<see cref="Enter"/>). Печать и PDF рисуют картинку из файла в
    /// полном разрешении, как и раньше, — для них кэш выключен.
    /// </summary>
    public static class ScreenImageCache
    {
        /// <summary>Предел памяти под готовые копии, байт.</summary>
        private const long BudgetBytes = 192L * 1024 * 1024;

        /// <summary>Больше этого по стороне копия не делается — картинка рисуется как прежде.</summary>
        private const int MaxSidePx = 8192;

        /// <summary>Больше этого точек копия не делается — картинка рисуется как прежде.</summary>
        private const long MaxPixels = 24L * 1024 * 1024;

        /// <summary>Готовая копия рисуется на экран точка в точку: линейной выборки хватает.</summary>
        private static readonly SKSamplingOptions BlitSampling = new(SKFilterMode.Linear, SKMipmapMode.None);

        private readonly record struct Key(uint ImageId, int SrcLeft, int SrcTop, int SrcRight, int SrcBottom, int Width, int Height);

        private sealed class Entry
        {
            public Entry(SKImage image, long bytes, LinkedListNode<Key> node)
            {
                Image = image;
                Bytes = bytes;
                Node = node;
            }

            public SKImage Image { get; }
            public long Bytes { get; }
            public LinkedListNode<Key> Node { get; }
        }

        private static readonly object Sync = new();
        private static readonly Dictionary<Key, Entry> Entries = new();
        private static readonly LinkedList<Key> Recent = new();
        private static long _totalBytes;

        // Копии, вытесненные, пока их мог рисовать другой поток. Освобождаются, когда
        // рисующих не остаётся: освободить образ посреди чужой отрисовки — падение в
        // нативном коде.
        private static readonly List<SKImage> PendingDispose = new();
        private static int _drawsInFlight;

        // Кэш включён на этом потоке: идёт проход содержимого экрана.
        [ThreadStatic] private static bool t_active;

        /// <summary>
        /// Включает кэш на время прохода содержимого экрана. Возвращает прежнее
        /// состояние — его отдают в <see cref="Exit"/>.
        /// </summary>
        public static bool Enter(bool active)
        {
            bool previous = t_active;
            t_active = active;
            return previous;
        }

        /// <summary>Возвращает состояние, бывшее до <see cref="Enter"/>.</summary>
        public static void Exit(bool previous) => t_active = previous;

        /// <summary>
        /// Рисует часть картинки src в прямоугольник dst. На экране — готовой копией
        /// экранного размера; вне экрана (печать, PDF) и для слишком больших копий — из
        /// самой картинки, как прежде.
        /// </summary>
        public static void DrawImage(SKCanvas canvas, SKImage image, SKRect src, SKRect dst, SKPaint? paint)
        {
            if (!t_active || !TryTargetSize(canvas, dst, out int width, out int height))
            {
                canvas.DrawImage(image, src, dst, FloatingObjectRenderer.SamplingFor(canvas, src, dst), paint);
                return;
            }

            var key = new Key(
                image.UniqueId,
                (int)MathF.Round(src.Left), (int)MathF.Round(src.Top),
                (int)MathF.Round(src.Right), (int)MathF.Round(src.Bottom),
                width, height);

            SKImage? scaled = AcquireOrCreate(key, image, src, width, height);
            if (scaled is null)
            {
                canvas.DrawImage(image, src, dst, FloatingObjectRenderer.SamplingFor(canvas, src, dst), paint);
                return;
            }

            try
            {
                canvas.DrawImage(scaled, new SKRect(0f, 0f, width, height), dst, BlitSampling, paint);
            }
            finally
            {
                Release();
            }
        }

        /// <summary>Размер картинки на экране в точках устройства.</summary>
        private static bool TryTargetSize(SKCanvas canvas, SKRect dst, out int width, out int height)
        {
            var matrix = canvas.TotalMatrix;
            float scaleX = MathF.Sqrt(matrix.ScaleX * matrix.ScaleX + matrix.SkewY * matrix.SkewY);
            float scaleY = MathF.Sqrt(matrix.ScaleY * matrix.ScaleY + matrix.SkewX * matrix.SkewX);

            width = (int)MathF.Round(Math.Abs(dst.Width) * scaleX);
            height = (int)MathF.Round(Math.Abs(dst.Height) * scaleY);

            return width >= 1 && height >= 1
                && width <= MaxSidePx && height <= MaxSidePx
                && (long)width * height <= MaxPixels;
        }

        /// <summary>
        /// Готовая копия по ключу — найденная или сделанная. Копия отмечается как
        /// рисуемая: до <see cref="Release"/> её не освободят. Null — копию сделать не
        /// удалось.
        /// </summary>
        private static SKImage? AcquireOrCreate(Key key, SKImage image, SKRect src, int width, int height)
        {
            lock (Sync)
            {
                if (Entries.TryGetValue(key, out var found))
                {
                    Recent.Remove(found.Node);
                    Recent.AddFirst(found.Node);
                    _drawsInFlight++;
                    return found.Image;
                }
            }

            // Копия делается вне замка: это и есть вся дорогая работа, и другие потоки
            // в это время рисуют своё.
            SKImage? created = CreateScaled(image, src, width, height);
            if (created is null) return null;

            lock (Sync)
            {
                if (Entries.TryGetValue(key, out var raced))
                {
                    // Ту же копию успел сделать другой поток — берётся его.
                    created.Dispose();
                    Recent.Remove(raced.Node);
                    Recent.AddFirst(raced.Node);
                    _drawsInFlight++;
                    return raced.Image;
                }

                long bytes = (long)width * height * 4;
                var node = Recent.AddFirst(key);
                Entries[key] = new Entry(created, bytes, node);
                _totalBytes += bytes;
                _drawsInFlight++;

                TrimLocked(keep: key);
                return created;
            }
        }

        private static void Release()
        {
            lock (Sync)
            {
                _drawsInFlight--;
                if (_drawsInFlight > 0 || PendingDispose.Count == 0) return;

                foreach (var stale in PendingDispose) stale.Dispose();
                PendingDispose.Clear();
            }
        }

        /// <summary>Вытесняет самые давние копии, пока объём выше предела.</summary>
        private static void TrimLocked(Key keep)
        {
            while (_totalBytes > BudgetBytes && Recent.Last is { } last)
            {
                if (last.Value.Equals(keep)) break;

                Recent.RemoveLast();
                if (!Entries.Remove(last.Value, out var evicted)) continue;

                _totalBytes -= evicted.Bytes;
                PendingDispose.Add(evicted.Image);
            }
        }

        /// <summary>
        /// Копия части картинки в экранном размере — тем же фильтром, каким картинка
        /// рисовалась на экран прежде.
        /// </summary>
        private static SKImage? CreateScaled(SKImage image, SKRect src, int width, int height)
        {
            try
            {
                var info = new SKImageInfo(width, height, SKImageInfo.PlatformColorType, SKAlphaType.Premul);
                using var bitmap = new SKBitmap(info);
                if (bitmap.GetPixels() == IntPtr.Zero) return null;

                var target = new SKRect(0f, 0f, width, height);

                using (var canvas = new SKCanvas(bitmap))
                {
                    canvas.Clear(SKColors.Transparent);
                    canvas.DrawImage(image, src, target, FloatingObjectRenderer.SamplingFor(canvas, src, target));
                }

                return SKImage.FromPixelCopy(info, bitmap.GetPixels(), bitmap.RowBytes);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Освобождает все копии — при закрытии документа. Рисуемые сейчас дождутся
        /// конца своей отрисовки.
        /// </summary>
        public static void Clear()
        {
            lock (Sync)
            {
                foreach (var entry in Entries.Values) PendingDispose.Add(entry.Image);
                Entries.Clear();
                Recent.Clear();
                _totalBytes = 0;

                if (_drawsInFlight > 0) return;

                foreach (var stale in PendingDispose) stale.Dispose();
                PendingDispose.Clear();
            }
        }
    }
}
