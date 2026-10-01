using System;
using SkiaSharp;
using Writersword.Modules.TextEditor.Models.Inline;

namespace Writersword.Modules.TextEditor.Rendering
{
    /// <summary>
    /// Рисует подчёркивание фрагмента на SKCanvas по его виду — в документе, в режиме
    /// чтения и при печати в PDF.
    ///
    /// Рисунок прерывистых линий и волны отсчитывается от левого края строки, а не от
    /// начала фрагмента: слово, разбитое на сегменты (пробелы, смена цвета), получает
    /// непрерывный узор без сбоя на стыках.
    /// </summary>
    public static class UnderlinePainter
    {
        /// <summary>
        /// Рисует подчёркивание под отрезком строки.
        /// </summary>
        /// <param name="canvas">Холст.</param>
        /// <param name="style">Вид подчёркивания. None — ничего не рисуется.</param>
        /// <param name="x">Левый край отрезка.</param>
        /// <param name="width">Ширина отрезка.</param>
        /// <param name="baselineY">Базовая линия текста отрезка.</param>
        /// <param name="fontSizePt">Кегль отрезка — от него толщина и размер рисунка.</param>
        /// <param name="color">Цвет линии.</param>
        /// <param name="shader">Градиент линии (как у букв) либо null.</param>
        /// <param name="bold">Фрагмент жирный: линия толще, как у Word; рисунок линии прежний.</param>
        public static void Draw(
            SKCanvas canvas, UnderlineStyle style,
            float x, float width, float baselineY, float fontSizePt,
            SKColor color, SKShader? shader, bool bold = false)
        {
            if (style == UnderlineStyle.None || width <= 0f) return;

            var shape = UnderlineShape.Of(style);

            // Рисунок линии меряется базовой толщиной, сама линия — с учётом жирности:
            // Word у жирного текста утолщает линию, но точки, штрихи и просветы
            // оставляет прежней длины.
            float baseThickness = UnderlineShape.BaseThickness(fontSizePt);
            float strokeWidth = UnderlineShape.BaseThickness(fontSizePt, bold) * shape.Weight;
            float lineY = baselineY + fontSizePt * UnderlineShape.OffsetFromBaseline;
            float secondY = lineY + UnderlineShape.DoubleGap * baseThickness;
            float right = x + width;

            // Прямая ставится на сетку пикселей: толщина, штрихи и просветы — целое
            // число точек экрана, не меньше одной, а сама линия — ровно на ряды
            // пикселей. Иначе линия в долю пикселя расплывается сглаживанием на два
            // бледных ряда, а точка пунктира — в бледную сплошную полосу, тогда как у
            // Word линии чёткие. Волна на сетку не ставится: она и должна быть гладкой.
            var matrix = canvas.TotalMatrix;
            bool snap = !shape.IsWave && IsAxisAligned(matrix);

            if (snap)
            {
                float strokePx = MathF.Max(1f, MathF.Round(strokeWidth * matrix.ScaleY));
                strokeWidth = strokePx / matrix.ScaleY;
                lineY = SnapLineY(lineY, strokePx, matrix);

                if (shape.IsDouble)
                {
                    // Просвет двойной линии — тоже целое число пикселей, не меньше
                    // одного: иначе две линии сливались бы в одну толстую.
                    float gapPx = MathF.Max(strokePx + 1f,
                        MathF.Round(UnderlineShape.DoubleGap * baseThickness * matrix.ScaleY));
                    secondY = lineY + gapPx / matrix.ScaleY;
                }
            }

            using var paint = new SKPaint
            {
                Color = color,
                StrokeWidth = strokeWidth,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeCap = SKStrokeCap.Butt
            };
            if (shader != null) paint.Shader = shader;

            SKPathEffect? dashEffect = null;
            if (shape.Dash is { Length: > 0 } pattern)
            {
                var intervals = new float[pattern.Length];
                float phase;

                if (snap)
                {
                    float scaleX = matrix.ScaleX;
                    float periodPx = 0f;
                    for (int i = 0; i < pattern.Length; i++)
                    {
                        float px = MathF.Max(1f, MathF.Round(pattern[i] * baseThickness * scaleX));
                        intervals[i] = px / scaleX;
                        periodPx += px;
                    }

                    // Начала штрихов ложатся на кратные периоду точки экрана: узор
                    // соседних сегментов продолжается, а каждая точка — на целом пикселе.
                    float xPx = scaleX * x + matrix.TransX;
                    float phasePx = periodPx > 0f ? ((xPx % periodPx) + periodPx) % periodPx : 0f;
                    phase = phasePx / scaleX;
                }
                else
                {
                    float period = 0f;
                    for (int i = 0; i < pattern.Length; i++)
                    {
                        intervals[i] = pattern[i] * baseThickness;
                        period += intervals[i];
                    }

                    // Узор идёт от нулевой отметки холста: у соседних сегментов он
                    // продолжается, а не начинается заново.
                    phase = period > 0f ? ((x % period) + period) % period : 0f;
                }

                dashEffect = SKPathEffect.CreateDash(intervals, phase);
                paint.PathEffect = dashEffect;
            }

            try
            {
                if (shape.IsWave)
                {
                    float amplitude = shape.WaveAmplitude * baseThickness;
                    float length = shape.WaveLength * baseThickness;
                    float centerY = lineY + amplitude * 0.5f;

                    DrawWave(canvas, paint, x, right, centerY, amplitude, length);
                    if (shape.IsDouble)
                        DrawWave(canvas, paint, x, right,
                            centerY + UnderlineShape.WaveDoubleGap * baseThickness + amplitude,
                            amplitude, length);
                    return;
                }

                canvas.DrawLine(x, lineY, right, lineY, paint);
                if (shape.IsDouble)
                    canvas.DrawLine(x, secondY, right, secondY, paint);
            }
            finally
            {
                dashEffect?.Dispose();
            }
        }

        /// <summary>
        /// Середина линии толщиной <paramref name="strokePx"/> точек экрана, поставленной
        /// ровно на ряды пикселей рядом с <paramref name="y"/>.
        /// </summary>
        internal static float SnapLineY(float y, float strokePx, SKMatrix matrix)
        {
            float yPx = matrix.ScaleY * y + matrix.TransY;
            float topPx = MathF.Round(yPx - strokePx * 0.5f);
            return (topPx + strokePx * 0.5f - matrix.TransY) / matrix.ScaleY;
        }

        /// <summary>
        /// Волна по синусоиде. Фаза берётся от абсолютного X, поэтому волна соседних
        /// сегментов смыкается без излома.
        /// </summary>
        private static void DrawWave(
            SKCanvas canvas, SKPaint paint,
            float left, float right, float centerY, float amplitude, float length)
        {
            if (length <= 0f || right <= left) return;

            float step = length / 12f;
            float k = 2f * MathF.PI / length;

            using var path = new SKPath();
            float px = left;
            path.MoveTo(px, centerY + amplitude * MathF.Sin(px * k));
            while (px < right)
            {
                px = Math.Min(px + step, right);
                path.LineTo(px, centerY + amplitude * MathF.Sin(px * k));
            }

            canvas.DrawPath(path, paint);
        }

        /// <summary>
        /// Холст без поворота, наклона и перспективы, с положительным масштабом: только
        /// тогда пункт переводится в точки экрана простым умножением и линию можно
        /// поставить на сетку пикселей.
        /// </summary>
        internal static bool IsAxisAligned(SKMatrix m)
            => m.ScaleX > 0.01f && m.ScaleY > 0.01f
               && MathF.Abs(m.SkewX) < 1e-4f && MathF.Abs(m.SkewY) < 1e-4f
               && MathF.Abs(m.Persp0) < 1e-6f && MathF.Abs(m.Persp1) < 1e-6f;

        /// <summary>
        /// Цвет линии подчёркивания: свой цвет фрагмента, если задан, иначе цвет букв.
        /// </summary>
        public static bool TryParseLineColor(string? code, out SKColor color)
        {
            color = SKColors.Black;
            if (string.IsNullOrWhiteSpace(code)) return false;

            string value = code.Trim();
            if (!value.StartsWith("#", StringComparison.Ordinal)) value = "#" + value;
            return SKColor.TryParse(value, out color);
        }
    }
}
