using System;
using SkiaSharp;
using Writersword.Modules.TextEditor.Models.Page;

namespace Writersword.Modules.TextEditor.Rendering
{
    /// <summary>
    /// Сторона рамки страницы раздела (w:pgBorders) — одна для полотна и печати.
    /// Вид линии Word сводится к четырём: сплошная, двойная, точками и штрихами;
    /// рисованные рамки Word (art) идут сплошной линией.
    /// </summary>
    public static class PageBorderPainter
    {
        /// <summary>
        /// Рамка страницы целиком: стороны сходятся в углах прямоугольника, как у
        /// Word, а не тянутся каждая во всю ширину или высоту листа. Края — лист
        /// (рамка от края листа) или текст (рамка от текста); отступ каждой стороны
        /// откладывается внутрь от края листа или наружу от текста.
        /// </summary>
        public static void DrawFrame(
            SKCanvas canvas, SectionPageBorders borders,
            float left, float top, float right, float bottom, bool screenInk = false)
        {
            float sign = borders.FromPageEdge ? 1f : -1f;
            static float Space(PageBorderSide? side) => side is null ? 0f : (float)side.SpacePt;

            float xLeft = left + Space(borders.Left) * sign;
            float xRight = right - Space(borders.Right) * sign;
            float yTop = top + Space(borders.Top) * sign;
            float yBottom = bottom - Space(borders.Bottom) * sign;

            // Вторая линия двойной стороны кончается на второй линии соседней стороны:
            // внутренние линии рамки образуют свой прямоугольник, внешние — свой.
            float leftSecond = SecondLineOffset(borders.Left, borders.FromPageEdge);
            float rightSecond = SecondLineOffset(borders.Right, !borders.FromPageEdge);
            float topSecond = SecondLineOffset(borders.Top, borders.FromPageEdge);
            float bottomSecond = SecondLineOffset(borders.Bottom, !borders.FromPageEdge);

            if (borders.Top is { } t)
                DrawSide(canvas, t, xLeft, yTop, xRight, yTop, true, borders.FromPageEdge, screenInk, leftSecond, rightSecond);
            if (borders.Bottom is { } b)
                DrawSide(canvas, b, xLeft, yBottom, xRight, yBottom, true, !borders.FromPageEdge, screenInk, leftSecond, rightSecond);
            if (borders.Left is { } l)
                DrawSide(canvas, l, xLeft, yTop, xLeft, yBottom, false, borders.FromPageEdge, screenInk, topSecond, bottomSecond);
            if (borders.Right is { } r)
                DrawSide(canvas, r, xRight, yTop, xRight, yBottom, false, !borders.FromPageEdge, screenInk, topSecond, bottomSecond);
        }

        /// <summary>Сдвиг второй линии двойной стороны от первой; 0 — линия одна или стороны нет.</summary>
        private static float SecondLineOffset(PageBorderSide? side, bool towardPositive)
        {
            if (side is null || !IsTwoLines(side.Style ?? "single")) return 0f;
            float width = (float)Math.Clamp(side.WidthPt, 0.25, 12.0);
            float part = Math.Max(width / 3f, 0.4f);
            return part * 2f * (towardPositive ? 1f : -1f);
        }

        private static bool IsTwoLines(string style)
            => style is "double" or "triple"
               || style.StartsWith("thinThick", StringComparison.Ordinal)
               || style.StartsWith("thickThin", StringComparison.Ordinal);

        /// <param name="towardPositive">
        /// Вторая линия двойной рамки ложится в сторону роста координаты (вниз или
        /// вправо от первой); иначе — вверх или влево. Вызывающий выбирает сторону так,
        /// чтобы рамка от края листа не уходила за край.
        /// </param>
        /// <param name="screenInk">Цвет переводится в чернила темы полотна; в печати — как есть.</param>
        /// <param name="secondStart">Сдвиг начала второй линии двойной стороны вдоль неё.</param>
        /// <param name="secondEnd">Сдвиг конца второй линии двойной стороны вдоль неё.</param>
        public static void DrawSide(
            SKCanvas canvas, PageBorderSide side, float x1, float y1, float x2, float y2,
            bool horizontal, bool towardPositive, bool screenInk = false,
            float secondStart = 0f, float secondEnd = 0f)
        {
            string style = side.Style ?? "single";
            if (style is "none" or "nil") return;

            float width = (float)Math.Clamp(side.WidthPt, 0.25, 12.0);

            var color = SKColors.Black;
            if (!string.IsNullOrWhiteSpace(side.Color) && side.Color != "auto"
                && SKColor.TryParse(side.Color.StartsWith('#') ? side.Color : "#" + side.Color, out var own))
                color = own;
            if (screenInk) color = SKTextRenderer.ResolveInk(color);

            using var paint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                Color = color,
                StrokeWidth = width
            };

            bool dotted = style.Contains("dot", StringComparison.OrdinalIgnoreCase)
                && !style.Contains("dash", StringComparison.OrdinalIgnoreCase);

            if (dotted)
            {
                paint.StrokeCap = SKStrokeCap.Round;
                paint.PathEffect = SKPathEffect.CreateDash(new[] { 0.01f, width * 2.2f }, 0f);
            }
            else if (style.Contains("dash", StringComparison.OrdinalIgnoreCase))
            {
                paint.PathEffect = SKPathEffect.CreateDash(new[] { width * 3.5f, width * 2f }, 0f);
            }

            bool twoLines = IsTwoLines(style);

            if (!twoLines)
            {
                canvas.DrawLine(x1, y1, x2, y2, paint);
                return;
            }

            // Двойная: две линии по трети толщины с просветом между ними.
            float part = Math.Max(width / 3f, 0.4f);
            paint.StrokeWidth = part;
            float step = part * 2f * (towardPositive ? 1f : -1f);

            canvas.DrawLine(x1, y1, x2, y2, paint);
            if (horizontal) canvas.DrawLine(x1 + secondStart, y1 + step, x2 + secondEnd, y2 + step, paint);
            else canvas.DrawLine(x1 + step, y1 + secondStart, x2 + step, y2 + secondEnd, paint);
        }
    }
}
