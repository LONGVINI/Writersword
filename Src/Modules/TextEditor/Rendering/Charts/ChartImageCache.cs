using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using SkiaSharp;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Rendering.Charts
{
    /// <summary>
    /// Растры нарисованных диаграмм. Лист рисует диаграмму тем же путём, что и картинку
    /// из файла, — образом в прямоугольник: так обрезка, поворот, отражение, рамка и
    /// прозрачность у диаграммы работают сами, без второй ветки в каждом месте отрисовки.
    ///
    /// Модель диаграммы после создания не меняется (правка собирает новую), поэтому
    /// растр узнаётся по самому объекту модели и размеру в точках. Пропавшая модель
    /// уносит свои растры с собой.
    /// </summary>
    public static class ChartImageCache
    {
        /// <summary>
        /// Точек растра на пункт для экрана. Растр на экране — заместитель: лист рисует
        /// диаграмму вектором (<see cref="TryDrawVector"/>), а сам растр виден только там,
        /// где картинка рисуется в обход общего пути, — в режиме обрезки.
        /// </summary>
        public const float ScreenPixelsPerPoint = 2f;

        /// <summary>Точек растра на пункт для печати и PDF: около 300 точек на дюйм.</summary>
        public const float PrintPixelsPerPoint = 4f;

        /// <summary>Сколько размеров одной диаграммы держать: при перетаскивании ручек их много.</summary>
        private const int SizesPerChart = 3;

        /// <summary>Наибольшая сторона растра в точках — защита от диаграммы на гектар.</summary>
        private const int MaxSidePixels = 6000;

        private sealed class Entry
        {
            public readonly List<(int Width, int Height, SKImage Image)> Images = new();
        }

        private static readonly ConditionalWeakTable<ChartModel, Entry> Cache = new();
        private static readonly object Gate = new();

        /// <summary>Чей растр: модель и размер диаграммы в пунктах по номеру образа.</summary>
        private static readonly Dictionary<uint, (WeakReference<ChartModel> Chart, float WidthPt, float HeightPt, Func<string, SKImage?>? Images)> Owners = new();

        /// <summary>
        /// Рисует вместо растра диаграммы саму диаграмму — вектором, в той же части и в
        /// тот же прямоугольник, что и растр: обрезка, поворот, отражение и
        /// прозрачность остаются за вызывающим. false — образ не растр диаграммы.
        /// </summary>
        public static bool TryDrawVector(SKCanvas canvas, SKImage image, SKRect src, SKRect dst, SKPaint? paint)
        {
            if (canvas is null || image is null) return false;

            ChartModel? chart;
            float widthPt, heightPt;
            Func<string, SKImage?>? images;
            lock (Gate)
            {
                if (!Owners.TryGetValue(image.UniqueId, out var owner)) return false;
                if (!owner.Chart.TryGetTarget(out chart)) return false;
                widthPt = owner.WidthPt;
                heightPt = owner.HeightPt;
                images = owner.Images;
            }

            if (src.Width <= 0 || src.Height <= 0 || dst.Width == 0 || dst.Height == 0) return true;

            byte alpha = paint?.Color.Alpha ?? (byte)255;
            if (alpha == 0) return true;

            canvas.Save();
            try
            {
                canvas.ClipRect(dst, SKClipOperation.Intersect, antialias: true);

                if (alpha < 255)
                {
                    using var layer = new SKPaint { Color = new SKColor(0xFF, 0xFF, 0xFF, alpha) };
                    canvas.SaveLayer(dst, layer);
                }

                // Часть растра src ложится в dst; растр — диаграмма в пунктах, растянутая
                // на его точки.
                canvas.Translate(dst.Left, dst.Top);
                canvas.Scale(dst.Width / src.Width, dst.Height / src.Height);
                canvas.Translate(-src.Left, -src.Top);
                canvas.Scale(image.Width / widthPt, image.Height / heightPt);

                ChartRenderer.Draw(canvas, chart, new SKRect(0, 0, widthPt, heightPt), images);

                if (alpha < 255) canvas.Restore();
            }
            finally
            {
                canvas.Restore();
            }

            return true;
        }

        private static void RegisterOwner(SKImage image, ChartModel chart, float widthPt, float heightPt, Func<string, SKImage?>? images)
        {
            // Записи о собранных моделях вычищаются, когда их набирается много.
            if (Owners.Count > 512)
            {
                var dead = new List<uint>();
                foreach (var pair in Owners)
                    if (!pair.Value.Chart.TryGetTarget(out _)) dead.Add(pair.Key);
                foreach (var id in dead) Owners.Remove(id);
            }

            Owners[image.UniqueId] = (new WeakReference<ChartModel>(chart), widthPt, heightPt, images);
        }

        /// <summary>
        /// Растр диаграммы размером widthPt × heightPt пунктов. Null — размер нулевой или
        /// растр не удалось создать. images — картинки проекта для фона диаграммы; null —
        /// источник печати.
        /// </summary>
        public static SKImage? Get(ChartModel chart, double widthPt, double heightPt, float pixelsPerPoint,
            Func<string, SKImage?>? images = null)
        {
            if (chart is null || widthPt <= 0 || heightPt <= 0 || double.IsNaN(widthPt) || double.IsNaN(heightPt))
                return null;

            float scale = pixelsPerPoint;
            double longest = Math.Max(widthPt, heightPt) * scale;
            if (longest > MaxSidePixels) scale = (float)(MaxSidePixels / Math.Max(widthPt, heightPt));

            int width = Math.Max(1, (int)Math.Ceiling(widthPt * scale));
            int height = Math.Max(1, (int)Math.Ceiling(heightPt * scale));

            lock (Gate)
            {
                var entry = Cache.GetOrCreateValue(chart);
                foreach (var (w, h, image) in entry.Images)
                    if (w == width && h == height) return image;
            }

            // Картинка фона ещё грузится: растр без неё не запоминается, следующий
            // кадр нарисует диаграмму снова.
            bool complete = ChartRenderer.BackgroundReady(chart, images);

            var rendered = Render(chart, (float)widthPt, (float)heightPt, width, height, images);
            if (rendered is null) return null;
            if (!complete)
            {
                lock (Gate) RegisterOwner(rendered, chart, (float)widthPt, (float)heightPt, images);
                return rendered;
            }

            lock (Gate)
            {
                var entry = Cache.GetOrCreateValue(chart);
                foreach (var (w, h, image) in entry.Images)
                    if (w == width && h == height) return image;

                // Старые размеры выпадают из списка, но не освобождаются здесь: образ мог
                // уйти в отрисовку другого потока, и освобождённый под ним образ роняет
                // процесс в нативном коде. Его соберёт сборщик мусора.
                if (entry.Images.Count >= SizesPerChart) entry.Images.RemoveAt(0);
                entry.Images.Add((width, height, rendered));
                RegisterOwner(rendered, chart, (float)widthPt, (float)heightPt, images);
                return rendered;
            }
        }

        /// <summary>
        /// Рисует диаграмму в растр. Пиксели копируются в образ: образ поверх растра,
        /// который уже освобождён, роняет процесс при первой отрисовке.
        /// </summary>
        private static SKImage? Render(ChartModel chart, float widthPt, float heightPt, int width, int height,
            Func<string, SKImage?>? images)
        {
            try
            {
                var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
                using var bitmap = new SKBitmap(info);
                using (var canvas = new SKCanvas(bitmap))
                {
                    canvas.Clear(SKColors.Transparent);
                    canvas.Scale(width / widthPt, height / heightPt);
                    ChartRenderer.Draw(canvas, chart, new SKRect(0, 0, widthPt, heightPt), images);
                    canvas.Flush();
                }

                using var pixels = bitmap.PeekPixels();
                return pixels is null ? null : SKImage.FromPixelCopy(pixels);
            }
            catch
            {
                return null;
            }
        }
    }
}
