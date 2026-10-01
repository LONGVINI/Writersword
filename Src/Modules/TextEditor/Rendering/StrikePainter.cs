using System;
using SkiaSharp;

namespace Writersword.Modules.TextEditor.Rendering
{
    /// <summary>
    /// Рисует зачёркивание фрагмента — одинарное или двойное — на SKCanvas: в документе,
    /// в режиме чтения и при печати в PDF.
    ///
    /// Высота линии берётся из метрик шрифта (положение зачёркивания, которое задал
    /// автор шрифта), как у Word. Доля кегля годится только запасным путём: у разных
    /// гарнитур середина строчных букв на разной высоте, и линия на постоянной доле
    /// кегля уходила к верху букв.
    /// </summary>
    public static class StrikePainter
    {
        /// <summary>
        /// Расстояние между средними линиями двойного зачёркивания в толщинах линии.
        /// Как у Word: две тонкие линии с просветом в одну линию.
        /// </summary>
        public const float DoubleGap = 2f;

        /// <summary>
        /// Высота зачёркивания над базовой линией в долях кегля — если шрифт своей
        /// не сообщает.
        /// </summary>
        private const float FallbackPositionFactor = 0.3f;

        /// <summary>
        /// Рисует зачёркивание под отрезком строки.
        /// </summary>
        /// <param name="canvas">Холст.</param>
        /// <param name="isDouble">Двойное зачёркивание вместо одинарного.</param>
        /// <param name="x">Левый край отрезка.</param>
        /// <param name="width">Ширина отрезка.</param>
        /// <param name="baselineY">Базовая линия текста отрезка.</param>
        /// <param name="fontSizePt">Кегль отрезка — от него толщина линии.</param>
        /// <param name="font">Шрифт отрезка — из его метрик высота линии.</param>
        /// <param name="color">Цвет линии.</param>
        /// <param name="shader">Градиент линии (как у букв) либо null.</param>
        public static void Draw(
            SKCanvas canvas, bool isDouble,
            float x, float width, float baselineY, float fontSizePt,
            SKFont font, SKColor color, SKShader? shader)
        {
            if (width <= 0f) return;

            float thickness = Math.Max(0.5f, fontSizePt * 0.05f);

            font.GetFontMetrics(out var metrics);
            float centerY = metrics.StrikeoutPosition is float position && position < 0f
                ? baselineY + position
                : baselineY - fontSizePt * FallbackPositionFactor;

            float strokeWidth = thickness;
            float halfGap = thickness * DoubleGap / 2f;
            float firstY = isDouble ? centerY - halfGap : centerY;
            float secondY = centerY + halfGap;

            // Линия ставится на сетку пикселей, как подчёркивание (UnderlinePainter):
            // иначе линия в долю пикселя расплывается на два бледных ряда, а две линии
            // двойного зачёркивания сливаются в одну серую полосу.
            var matrix = canvas.TotalMatrix;
            if (UnderlinePainter.IsAxisAligned(matrix))
            {
                float strokePx = MathF.Max(1f, MathF.Round(thickness * matrix.ScaleY));
                strokeWidth = strokePx / matrix.ScaleY;

                if (isDouble)
                {
                    float gapPx = MathF.Max(strokePx + 1f,
                        MathF.Round(thickness * DoubleGap * matrix.ScaleY));
                    firstY = UnderlinePainter.SnapLineY(
                        centerY - gapPx / 2f / matrix.ScaleY, strokePx, matrix);
                    secondY = firstY + gapPx / matrix.ScaleY;
                }
                else
                {
                    firstY = UnderlinePainter.SnapLineY(centerY, strokePx, matrix);
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

            canvas.DrawLine(x, firstY, x + width, firstY, paint);
            if (isDouble)
                canvas.DrawLine(x, secondY, x + width, secondY, paint);
        }
    }
}
