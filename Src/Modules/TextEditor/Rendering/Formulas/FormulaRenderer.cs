using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using SkiaSharp;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Rendering.Formulas
{
    /// <summary>
    /// Готовая картинка формулы: размер в точках, файл PNG и высота базовой линии
    /// от верха картинки в точках.
    /// </summary>
    public sealed record FormulaImage(int WidthPx, int HeightPx, byte[] Png, float BaselinePx);

    /// <summary>Размеры формулы в строке: ширина, подъём над базовой линией и спуск под неё.</summary>
    public readonly record struct FormulaMetrics(float WidthPt, float AscentPt, float DescentPt)
    {
        public float HeightPt => AscentPt + DescentPt;
    }

    /// <summary>
    /// Вёрстка и рисование формул Word. Формула верстается один раз и запоминается у
    /// своего блока; заново — когда изменились её XML, кегль или гарнитура.
    /// </summary>
    public static class FormulaRenderer
    {
        private sealed class CachedLayout
        {
            public string Omml = string.Empty;
            public string Family = string.Empty;
            public float SizePt;
            public bool Display;
            public MathBox Box = MathBox.Empty;
        }

        private static readonly ConditionalWeakTable<FormulaBlock, CachedLayout> Layouts = new();
        private static readonly Dictionary<string, SkiaMathFont> Fonts = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object Gate = new();

        /// <summary>
        /// Размеры формулы. fontScale — множитель кегля (чтение укрупняет и ужимает
        /// текст вместе с формулами); 1 — как в документе.
        /// </summary>
        public static FormulaMetrics Measure(FormulaBlock formula, float fontScale = 1f)
        {
            var box = LayoutOf(formula, fontScale, out _);
            return new FormulaMetrics(box.Width, box.Ascent, box.Descent);
        }

        /// <summary>
        /// Рисует формулу: x — левый край, baselineY — базовая линия строки, color —
        /// цвет текста, которым формула набрана.
        /// </summary>
        public static void Draw(
            SKCanvas canvas, FormulaBlock formula, float x, float baselineY, SKColor color, float fontScale = 1f)
        {
            var box = LayoutOf(formula, fontScale, out var font);
            if (box.Items.Count == 0) return;

            float scale = CanvasScale(canvas);

            using var paint = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Fill };
            lock (font.Sync)
            {
                DrawBox(canvas, box, x, baselineY, font, paint, color, scale);
            }
        }

        private static MathBox LayoutOf(FormulaBlock formula, float fontScale, out SkiaMathFont font)
        {
            string family = string.IsNullOrWhiteSpace(formula.FontFamily)
                ? FormulaBlock.DefaultFontFamily
                : formula.FontFamily;

            float sizePt = (float)formula.FontSizePt;
            if (sizePt < 1f) sizePt = 11f;
            if (fontScale > 0f && Math.Abs(fontScale - 1f) > 0.0005f) sizePt *= fontScale;

            lock (Gate)
            {
                if (!Fonts.TryGetValue(family, out var known))
                {
                    known = new SkiaMathFont(family);
                    Fonts[family] = known;
                }
                font = known;
            }

            var cached = Layouts.GetOrCreateValue(formula);
            lock (cached)
            {
                bool fresh = ReferenceEquals(cached.Omml, formula.Omml) || cached.Omml == formula.Omml;
                if (fresh && cached.Family == family && cached.Display == formula.IsDisplay
                    && Math.Abs(cached.SizePt - sizePt) < 0.001f && cached.SizePt > 0f)
                    return cached.Box;

                MathBox box;
                lock (font.Sync)
                {
                    var row = OmmlParser.Parse(formula.Omml);
                    box = new MathTypesetter(font, sizePt).Typeset(row, formula.IsDisplay);
                }

                cached.Omml = formula.Omml;
                cached.Family = family;
                cached.SizePt = sizePt;
                cached.Display = formula.IsDisplay;
                cached.Box = box;
                return box;
            }
        }

        private static void DrawBox(
            SKCanvas canvas, MathBox box, float originX, float originY,
            SkiaMathFont font, SKPaint paint, SKColor baseColor, float canvasScale)
        {
            foreach (var item in box.Items)
            {
                switch (item)
                {
                    case MathBoxItem nested:
                        DrawBox(canvas, nested.Box, originX + nested.X, originY + nested.Y,
                            font, paint, baseColor, canvasScale);
                        break;

                    case MathGlyphItem glyph:
                        DrawGlyph(canvas, glyph, originX + glyph.X, originY + glyph.Y, font, paint, baseColor);
                        break;

                    case MathRuleItem rule:
                        DrawRule(canvas, originX + rule.X, originY + rule.Y, rule.Width, rule.Height,
                            paint, baseColor, canvasScale);
                        break;

                    case MathPlaceholderItem placeholder:
                        DrawPlaceholder(canvas, originX + placeholder.X, originY + placeholder.Y,
                            placeholder.Width, placeholder.Height, baseColor, canvasScale);
                        break;

                    case MathLineItem line:
                    {
                        using var stroke = new SKPaint
                        {
                            Color = baseColor,
                            IsAntialias = true,
                            IsStroke = true,
                            StrokeWidth = Math.Max(line.Thickness, 1f / canvasScale)
                        };
                        canvas.DrawLine(
                            originX + line.X, originY + line.Y,
                            originX + line.X2, originY + line.Y2, stroke);
                        break;
                    }
                }
            }
        }

        private static void DrawGlyph(
            SKCanvas canvas, MathGlyphItem glyph, float x, float y,
            SkiaMathFont font, SKPaint paint, SKColor baseColor)
        {
            var skFont = font.DrawingFont(glyph.Face, glyph.SizePt);

            paint.Color = glyph.Color is not null && SKColor.TryParse("#" + glyph.Color, out var own)
                ? own.WithAlpha(baseColor.Alpha)
                : baseColor;

            bool scaled = Math.Abs(glyph.ScaleX - 1f) > 0.0005f || Math.Abs(glyph.ScaleY - 1f) > 0.0005f;

            Span<ushort> glyphs = stackalloc ushort[1];
            Span<float> positions = stackalloc float[1];
            glyphs[0] = glyph.Glyph;
            positions[0] = 0f;

            using var builder = new SKTextBlobBuilder();
            var run = builder.AllocateHorizontalRun(skFont, 1, 0f);
            run.SetGlyphs(glyphs);
            run.SetPositions(positions);

            using var blob = builder.Build();
            if (blob is null) return;

            if (!scaled)
            {
                canvas.DrawText(blob, x, y, paint);
                return;
            }

            // Растянутый знак (скобка, корень, знак суммы) растёт от своей точки
            // отсчёта на базовой линии.
            canvas.Save();
            canvas.Translate(x, y);
            canvas.Scale(glyph.ScaleX, glyph.ScaleY);
            canvas.DrawText(blob, 0f, 0f, paint);
            canvas.Restore();
        }

        /// <summary>
        /// Черта дроби или корня. Тонкая горизонтальная черта ставится на целые
        /// пиксели экрана и не бывает тоньше одного: иначе она расплывается в серую
        /// полосу или пропадает на мелком масштабе.
        /// </summary>
        private static void DrawRule(
            SKCanvas canvas, float x, float y, float width, float height,
            SKPaint paint, SKColor color, float canvasScale)
        {
            if (width <= 0f || height <= 0f) return;

            paint.Color = color;

            float heightPx = height * canvasScale;
            float widthPx = width * canvasScale;

            if (width >= height && heightPx < 3f)
            {
                float snappedHeightPx = Math.Max(1f, (float)Math.Round(heightPx));
                float centerPx = (y + height / 2f) * canvasScale;
                float topPx = (float)Math.Round(centerPx - snappedHeightPx / 2f);
                canvas.DrawRect(x, topPx / canvasScale, width, snappedHeightPx / canvasScale, paint);
                return;
            }

            if (height > width && widthPx < 3f)
            {
                float snappedWidthPx = Math.Max(1f, (float)Math.Round(widthPx));
                float centerPx = (x + width / 2f) * canvasScale;
                float leftPx = (float)Math.Round(centerPx - snappedWidthPx / 2f);
                canvas.DrawRect(leftPx / canvasScale, y, snappedWidthPx / canvasScale, height, paint);
                return;
            }

            canvas.DrawRect(x, y, width, height, paint);
        }

        /// <summary>Пустое место конструкции: пунктирный квадратик цветом формулы, бледнее.</summary>
        private static void DrawPlaceholder(
            SKCanvas canvas, float x, float y, float width, float height, SKColor color, float canvasScale)
        {
            if (width <= 0f || height <= 0f) return;

            float stroke = Math.Max(0.6f, 1f / canvasScale);
            float dash = Math.Max(1.2f, height / 9f);

            using var effect = SKPathEffect.CreateDash(new[] { dash, dash * 0.8f }, 0f);
            using var paint = new SKPaint
            {
                Color = color.WithAlpha((byte)(color.Alpha * 0.6f)),
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = stroke,
                PathEffect = effect
            };

            float half = stroke / 2f;
            canvas.DrawRect(x + half, y + half, width - stroke, height - stroke, paint);
        }

        /// <summary>
        /// Картинка формулы для окна формулы и его галерей: формула в записи OMML,
        /// кегль, цвет и масштаб точек. placeholders — пустые места конструкций
        /// пунктирными квадратиками. Картинка обрезается по всему нарисованному, а не по
        /// габариту блока: знак, вылезший за габарит, не срезается. null — рисовать
        /// нечего.
        /// </summary>
        public static FormulaImage? RenderImage(
            string omml, bool display, float sizePt, SKColor color, bool placeholders, float pixelsPerPoint,
            float padPt = 2f, string? fontFamily = null)
        {
            if (string.IsNullOrWhiteSpace(omml) || pixelsPerPoint <= 0f) return null;

            string family = string.IsNullOrWhiteSpace(fontFamily) ? FormulaBlock.DefaultFontFamily : fontFamily!;

            SkiaMathFont font;
            lock (Gate)
            {
                if (!Fonts.TryGetValue(family, out var known))
                {
                    known = new SkiaMathFont(family);
                    Fonts[family] = known;
                }
                font = known;
            }

            lock (font.Sync)
            {
                var row = OmmlParser.Parse(omml);
                var box = new MathTypesetter(font, sizePt, placeholders).Typeset(row, display);
                if (box.Items.Count == 0) return null;

                var bounds = new SKRect(0f, -box.Ascent, Math.Max(box.Width, 0.5f), box.Descent);
                AccumulateInk(box, 0f, 0f, font, ref bounds);

                int widthPx = (int)Math.Ceiling((bounds.Width + padPt * 2f) * pixelsPerPoint);
                int heightPx = (int)Math.Ceiling((bounds.Height + padPt * 2f) * pixelsPerPoint);
                if (widthPx <= 0 || heightPx <= 0 || widthPx > 8000 || heightPx > 8000) return null;

                var info = new SKImageInfo(widthPx, heightPx, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var bitmap = new SKBitmap(info);
                using (var canvas = new SKCanvas(bitmap))
                {
                    canvas.Clear(SKColors.Transparent);
                    canvas.Scale(pixelsPerPoint);

                    float originX = padPt - bounds.Left;
                    float originY = padPt - bounds.Top;

                    using var paint = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Fill };
                    DrawBox(canvas, box, originX, originY, font, paint, color, pixelsPerPoint);
                    canvas.Flush();
                }

                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                if (data is null) return null;

                return new FormulaImage(widthPx, heightPx, data.ToArray(), (padPt - bounds.Top) * pixelsPerPoint);
            }
        }

        /// <summary>Расширяет границы до всего, что рисует блок: знаков, черт и квадратиков.</summary>
        private static void AccumulateInk(MathBox box, float originX, float originY, SkiaMathFont font, ref SKRect bounds)
        {
            foreach (var item in box.Items)
            {
                switch (item)
                {
                    case MathBoxItem nested:
                        AccumulateInk(nested.Box, originX + nested.X, originY + nested.Y, font, ref bounds);
                        break;

                    case MathGlyphItem glyph:
                    {
                        var e = font.Extents(glyph.Glyph, glyph.Face, glyph.SizePt);
                        float gx = originX + glyph.X;
                        float gy = originY + glyph.Y;
                        Union(ref bounds, new SKRect(
                            gx + e.Left * glyph.ScaleX, gy - e.Top * glyph.ScaleY,
                            gx + e.Right * glyph.ScaleX, gy + e.Bottom * glyph.ScaleY));
                        break;
                    }

                    case MathRuleItem rule:
                        Union(ref bounds, new SKRect(
                            originX + rule.X, originY + rule.Y,
                            originX + rule.X + rule.Width, originY + rule.Y + rule.Height));
                        break;

                    case MathPlaceholderItem placeholder:
                        Union(ref bounds, new SKRect(
                            originX + placeholder.X, originY + placeholder.Y,
                            originX + placeholder.X + placeholder.Width, originY + placeholder.Y + placeholder.Height));
                        break;

                    case MathLineItem line:
                    {
                        float half = line.Thickness / 2f;
                        Union(ref bounds, new SKRect(
                            originX + Math.Min(line.X, line.X2) - half, originY + Math.Min(line.Y, line.Y2) - half,
                            originX + Math.Max(line.X, line.X2) + half, originY + Math.Max(line.Y, line.Y2) + half));
                        break;
                    }
                }
            }
        }

        private static void Union(ref SKRect bounds, SKRect rect)
        {
            if (rect.Width <= 0f && rect.Height <= 0f) return;
            bounds = new SKRect(
                Math.Min(bounds.Left, rect.Left), Math.Min(bounds.Top, rect.Top),
                Math.Max(bounds.Right, rect.Right), Math.Max(bounds.Bottom, rect.Bottom));
        }

        private static float CanvasScale(SKCanvas canvas)
        {
            var m = canvas.TotalMatrix;
            float scale = MathF.Sqrt(m.ScaleX * m.ScaleX + m.SkewY * m.SkewY);
            return scale < 0.01f ? 1f : scale;
        }

        /// <summary>
        /// Шрифт формулы поверх Skia: знаки, их размеры и таблица MATH гарнитуры.
        /// Размеры знака меряются один раз на большом кегле и дальше пересчитываются
        /// пропорционально — раскладка формулы не зависит от масштаба показа.
        /// </summary>
        private sealed class SkiaMathFont : IMathFont
        {
            private const float ReferenceSizePt = 200f;

            private readonly SKTypeface[] _typefaces = new SKTypeface[4];
            private readonly SKFont[] _referenceFonts = new SKFont[4];
            private readonly Dictionary<int, ushort>[] _glyphs =
            {
                new(), new(), new(), new()
            };
            private readonly Dictionary<ushort, (float Advance, MathGlyphExtents Extents)>[] _metrics =
            {
                new(), new(), new(), new()
            };
            private readonly Dictionary<(int Face, int SizeKey), SKFont> _drawingFonts = new();

            /// <summary>Замок на все обращения к шрифту: объекты Skia не делятся между потоками.</summary>
            public object Sync { get; } = new();

            public OpenTypeMathTable? MathTable { get; }

            public SkiaMathFont(string family)
            {
                _typefaces[(int)MathFace.Regular] = SKTextRenderer.ResolveTypeface(family, false, false);
                _typefaces[(int)MathFace.Italic] = SKTextRenderer.ResolveTypeface(family, false, true);
                _typefaces[(int)MathFace.Bold] = SKTextRenderer.ResolveTypeface(family, true, false);
                _typefaces[(int)MathFace.BoldItalic] = SKTextRenderer.ResolveTypeface(family, true, true);

                for (int i = 0; i < 4; i++)
                {
                    _referenceFonts[i] = new SKFont(_typefaces[i], ReferenceSizePt)
                    {
                        Subpixel = true,
                        LinearMetrics = true
                    };
                }

                var regular = _typefaces[(int)MathFace.Regular];
                try
                {
                    // Тег таблицы — четыре буквы «MATH» одним числом.
                    const uint mathTag = ((uint)'M' << 24) | ((uint)'A' << 16) | ((uint)'T' << 8) | 'H';
                    if (regular.TryGetTableData(mathTag, out byte[] data))
                        MathTable = OpenTypeMathTable.Parse(data, regular.UnitsPerEm);
                }
                catch (Exception)
                {
                    // Шрифт без читаемой таблицы: формула верстается по запасным величинам.
                    MathTable = null;
                }
            }

            public ushort GlyphOf(int codepoint, MathFace face)
            {
                var cache = _glyphs[(int)face];
                if (cache.TryGetValue(codepoint, out ushort glyph)) return glyph;

                glyph = _typefaces[(int)face].GetGlyph(codepoint);
                cache[codepoint] = glyph;
                return glyph;
            }

            public float Advance(ushort glyph, MathFace face, float sizePt)
                => Measure(glyph, face).Advance * sizePt / ReferenceSizePt;

            public MathGlyphExtents Extents(ushort glyph, MathFace face, float sizePt)
            {
                var e = Measure(glyph, face).Extents;
                float k = sizePt / ReferenceSizePt;
                return new MathGlyphExtents(e.Top * k, e.Bottom * k, e.Left * k, e.Right * k);
            }

            private (float Advance, MathGlyphExtents Extents) Measure(ushort glyph, MathFace face)
            {
                var cache = _metrics[(int)face];
                if (cache.TryGetValue(glyph, out var known)) return known;

                Span<ushort> glyphs = stackalloc ushort[1];
                Span<float> widths = stackalloc float[1];
                Span<SKRect> bounds = stackalloc SKRect[1];
                glyphs[0] = glyph;

                _referenceFonts[(int)face].GetGlyphWidths(glyphs, widths, bounds);

                // У Skia ось Y идёт вниз: верх рисунка знака — отрицательный.
                var rect = bounds[0];
                var measured = (widths[0], new MathGlyphExtents(-rect.Top, rect.Bottom, rect.Left, rect.Right));
                cache[glyph] = measured;
                return measured;
            }

            /// <summary>Шрифт Skia для рисования знаков заданного начертания и кегля.</summary>
            public SKFont DrawingFont(MathFace face, float sizePt)
            {
                var key = ((int)face, (int)Math.Round(sizePt * 1000f));
                if (_drawingFonts.TryGetValue(key, out var font)) return font;

                // Кешу незачем расти без конца: кегли формул в документе наперечёт,
                // но чтение и масштаб печати добавляют свои.
                if (_drawingFonts.Count > 256)
                {
                    foreach (var old in _drawingFonts.Values) old.Dispose();
                    _drawingFonts.Clear();
                }

                font = new SKFont(_typefaces[(int)face], sizePt)
                {
                    Subpixel = true,
                    LinearMetrics = true,
                    Edging = SKFontEdging.SubpixelAntialias
                };
                _drawingFonts[key] = font;
                return font;
            }
        }
    }
}
