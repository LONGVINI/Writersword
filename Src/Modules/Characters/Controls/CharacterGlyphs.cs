using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Serilog;
using Writersword.Modules.Characters.Models;

namespace Writersword.Modules.Characters.Controls
{
    /// <summary>
    /// Значки полей: у подписи слева и у оценки вместо шариков. Значок —
    /// строка одного из трёх видов:
    ///   «heart»      — ключ из CharacterAnketaIcons;
    ///   «path:M0 0…» — геометрия, например из SVG-файла;
    ///   «png:…»      — картинка в base64, уменьшенная до 64 точек.
    /// Строкой — затем, чтобы значок жил прямо в анкете и уезжал вместе с её
    /// файлом, без отдельного хранилища картинок.
    /// </summary>
    public static class CharacterGlyphs
    {
        private static readonly ILogger _logger = Log.ForContext(typeof(CharacterGlyphs));

        public const string PathPrefix = "path:";
        public const string PngPrefix = "png:";

        /// <summary>Сторона картинки своего значка: больше для значка не нужно.</summary>
        public const int ImageSide = 64;

        private static readonly Dictionary<string, Geometry?> GeometryCache = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, Bitmap?> BitmapCache = new(StringComparer.Ordinal);

        public static bool IsEmpty(string? glyph) => string.IsNullOrWhiteSpace(glyph);

        public static bool IsImage(string? glyph) =>
            glyph != null && glyph.StartsWith(PngPrefix, StringComparison.Ordinal);

        /// <summary>Геометрия значка; null — значок картинкой или пустой.</summary>
        public static Geometry? GetGeometry(string? glyph)
        {
            if (IsEmpty(glyph) || IsImage(glyph)) return null;

            if (GeometryCache.TryGetValue(glyph!, out var cached)) return cached;

            Geometry? geometry = null;
            try
            {
                geometry = glyph!.StartsWith(PathPrefix, StringComparison.Ordinal)
                    ? Geometry.Parse(glyph.Substring(PathPrefix.Length))
                    : AnketaIconBadge.GetGeometry(glyph);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Glyph geometry is broken");
            }

            GeometryCache[glyph!] = geometry;
            return geometry;
        }

        /// <summary>Картинка значка; null — значок геометрией или пустой.</summary>
        public static Bitmap? GetBitmap(string? glyph)
        {
            if (!IsImage(glyph)) return null;

            if (BitmapCache.TryGetValue(glyph!, out var cached)) return cached;

            Bitmap? bitmap = null;
            try
            {
                var bytes = Convert.FromBase64String(glyph!.Substring(PngPrefix.Length));
                using var stream = new MemoryStream(bytes);
                bitmap = new Bitmap(stream);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Glyph image is broken");
            }

            BitmapCache[glyph!] = bitmap;
            return bitmap;
        }

        /// <summary>
        /// Нарисовать значок в квадрат: геометрию — кистью, по своим настоящим
        /// границам и по центру, картинку — с заданной прозрачностью.
        /// </summary>
        public static void Draw(DrawingContext context, Rect rect, string? glyph, IBrush? fill, double imageOpacity = 1)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return;

            var bitmap = GetBitmap(glyph);
            if (bitmap != null)
            {
                var size = bitmap.Size;
                var scale = Math.Min(rect.Width / size.Width, rect.Height / size.Height);
                var w = size.Width * scale;
                var h = size.Height * scale;
                var target = new Rect(rect.X + (rect.Width - w) / 2, rect.Y + (rect.Height - h) / 2, w, h);
                using (context.PushOpacity(imageOpacity))
                    context.DrawImage(bitmap, new Rect(size), target);
                return;
            }

            var geometry = GetGeometry(glyph);
            if (geometry == null || fill == null) return;

            var bounds = geometry.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0) return;

            var k = Math.Min(rect.Width / bounds.Width, rect.Height / bounds.Height);
            var matrix =
                Matrix.CreateTranslation(-bounds.X - bounds.Width / 2, -bounds.Y - bounds.Height / 2) *
                Matrix.CreateScale(k, k) *
                Matrix.CreateTranslation(rect.Center.X, rect.Center.Y);

            using (context.PushTransform(matrix))
                context.DrawGeometry(fill, null, geometry);
        }

        /// <summary>Значок из файла: SVG — геометрией, остальное — картинкой.</summary>
        public static string? FromFile(byte[] data, string fileName)
        {
            if (data == null || data.Length == 0) return null;

            return string.Equals(Path.GetExtension(fileName), ".svg", StringComparison.OrdinalIgnoreCase)
                ? FromSvg(Encoding.UTF8.GetString(data))
                : FromImage(data);
        }

        private static readonly Regex PathData = new("<path[^>]*?\\sd\\s*=\\s*\"([^\"]+)\"",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        /// <summary>
        /// Геометрия из SVG: все пути файла одной фигурой. Преобразования и
        /// заливки SVG не переносятся — значок красится цветом поля, как и
        /// встроенные.
        /// </summary>
        public static string? FromSvg(string svg)
        {
            if (string.IsNullOrWhiteSpace(svg)) return null;

            var parts = new List<string>();
            foreach (Match match in PathData.Matches(svg))
                parts.Add(match.Groups[1].Value.Trim());

            if (parts.Count == 0) return null;

            var data = string.Join(" ", parts);
            try
            {
                Geometry.Parse(data);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "SVG path data is not supported");
                return null;
            }

            return PathPrefix + data;
        }

        /// <summary>Картинка, уменьшенная до стороны ImageSide и сохранённая как PNG.</summary>
        public static string? FromImage(byte[] data)
        {
            try
            {
                using var input = new MemoryStream(data);
                using var source = new Bitmap(input);

                var size = source.PixelSize;
                var scale = Math.Min(1.0, ImageSide / (double)Math.Max(size.Width, size.Height));
                var target = new PixelSize(Math.Max(1, (int)Math.Round(size.Width * scale)),
                                           Math.Max(1, (int)Math.Round(size.Height * scale)));

                using var scaled = source.CreateScaledBitmap(target, BitmapInterpolationMode.HighQuality);
                using var output = new MemoryStream();
                scaled.Save(output, PngBitmapEncoderOptions.Default);
                return PngPrefix + Convert.ToBase64String(output.ToArray());
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Glyph image cannot be read");
                return null;
            }
        }
    }

    /// <summary>Значок поля одной штукой — у подписи и в выборе значка.</summary>
    public class GlyphIcon : Control
    {
        public static readonly StyledProperty<string?> GlyphProperty =
            AvaloniaProperty.Register<GlyphIcon, string?>(nameof(Glyph));

        public static readonly StyledProperty<IBrush?> FillProperty =
            AvaloniaProperty.Register<GlyphIcon, IBrush?>(nameof(Fill));

        public static readonly StyledProperty<double> SizeProperty =
            AvaloniaProperty.Register<GlyphIcon, double>(nameof(Size), 14d);

        static GlyphIcon()
        {
            AffectsRender<GlyphIcon>(GlyphProperty, FillProperty, SizeProperty);
            AffectsMeasure<GlyphIcon>(SizeProperty);
        }

        public string? Glyph
        {
            get => GetValue(GlyphProperty);
            set => SetValue(GlyphProperty, value);
        }

        public IBrush? Fill
        {
            get => GetValue(FillProperty);
            set => SetValue(FillProperty, value);
        }

        public double Size
        {
            get => GetValue(SizeProperty);
            set => SetValue(SizeProperty, value);
        }

        protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

        public override void Render(DrawingContext context)
        {
            var side = Math.Min(Bounds.Width, Bounds.Height);
            var rect = new Rect((Bounds.Width - side) / 2, (Bounds.Height - side) / 2, side, side);

            // Без своего цвета значок — цвета подписи по теме.
            var fill = Fill ??
                       (this.TryFindResource("TextSecondaryBrush", ActualThemeVariant, out var value) && value is IBrush brush
                           ? brush
                           : Brushes.Gray);
            CharacterGlyphs.Draw(context, rect, Glyph, fill);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property.Name == "ActualThemeVariant") InvalidateVisual();
        }
    }

    /// <summary>
    /// Оценка своими значками — звёздами, сердечками, черепами или
    /// картинкой из файла. Ведёт себя как шарики: заполняется до выбранного,
    /// под курсором показывает, что будет после щелчка.
    /// </summary>
    public class GlyphScale : ScaleControlBase
    {
        public static readonly StyledProperty<string?> GlyphProperty =
            AvaloniaProperty.Register<GlyphScale, string?>(nameof(Glyph), "star");

        public static readonly StyledProperty<double> GlyphSizeProperty =
            AvaloniaProperty.Register<GlyphScale, double>(nameof(GlyphSize), 18d);

        public static readonly StyledProperty<double> SpacingProperty =
            AvaloniaProperty.Register<GlyphScale, double>(nameof(Spacing), 4d);

        static GlyphScale()
        {
            AffectsRender<GlyphScale>(GlyphProperty, GlyphSizeProperty, SpacingProperty);
            AffectsMeasure<GlyphScale>(GlyphSizeProperty, SpacingProperty);
        }

        public string? Glyph
        {
            get => GetValue(GlyphProperty);
            set => SetValue(GlyphProperty, value);
        }

        public double GlyphSize
        {
            get => GetValue(GlyphSizeProperty);
            set => SetValue(GlyphSizeProperty, value);
        }

        public double Spacing
        {
            get => GetValue(SpacingProperty);
            set => SetValue(SpacingProperty, value);
        }

        private const double HoverGrow = 2;

        private double Pitch => GlyphSize + Spacing;

        protected override Size MeasureOverride(Size availableSize)
        {
            var count = Math.Max(0, Count);
            var width = count * GlyphSize + Math.Max(0, count - 1) * Spacing + HoverGrow * 2;
            return new Size(width, GlyphSize + HoverGrow * 2);
        }

        protected override int HitIndex(Point point)
        {
            var count = Math.Max(0, Count);
            if (count == 0) return 0;

            var x = point.X - HoverGrow + Spacing / 2;
            var index = (int)Math.Floor(x / Pitch) + 1;
            return Math.Clamp(index, 1, count);
        }

        public override void Render(DrawingContext context)
        {
            context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

            var count = Math.Max(0, Count);
            if (count == 0) return;

            var glyph = string.IsNullOrWhiteSpace(Glyph) ? "star" : Glyph;
            var accent = ResolveAccent();
            var empty = ResolveEmptyStroke();
            var preview = WithOpacity(accent, 0.5);
            var fading = WithOpacity(accent, 0.35);

            var value = ClampedValue;
            var hover = HoverIndex;

            for (int i = 1; i <= count; i++)
            {
                var grow = i == hover ? HoverGrow : 0;
                var x = HoverGrow + (i - 1) * Pitch - grow;
                var rect = new Rect(x, HoverGrow - grow, GlyphSize + grow * 2, GlyphSize + grow * 2);

                bool marked = i <= value;
                bool willMark = hover > 0 && i <= hover;

                IBrush brush;
                double opacity;
                if (hover > 0)
                {
                    if (willMark && marked) { brush = accent; opacity = 1; }
                    else if (willMark) { brush = preview; opacity = 0.6; }
                    else if (marked) { brush = fading; opacity = 0.45; }
                    else { brush = empty; opacity = 0.25; }
                }
                else if (marked) { brush = accent; opacity = 1; }
                else { brush = empty; opacity = 0.25; }

                CharacterGlyphs.Draw(context, rect, glyph, brush, opacity);
            }
        }
    }
}
