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
        /// <param name="towardPositive">
        /// Вторая линия двойной рамки ложится в сторону роста координаты (вниз или
        /// вправо от первой); иначе — вверх или влево. Вызывающий выбирает сторону так,
        /// чтобы рамка от края листа не уходила за край.
        /// </param>
        /// <param name="screenInk">Цвет переводится в чернила темы полотна; в печати — как есть.</param>
        public static void DrawSide(
            SKCanvas canvas, PageBorderSide side, float x1, float y1, float x2, float y2,
            bool horizontal, bool towardPositive, bool screenInk = false)
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

            bool twoLines = style is "double" or "triple"
                || style.StartsWith("thinThick", StringComparison.Ordinal)
                || style.StartsWith("thickThin", StringComparison.Ordinal);

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
            if (horizontal) canvas.DrawLine(x1, y1 + step, x2, y2 + step, paint);
            else canvas.DrawLine(x1 + step, y1, x2 + step, y2, paint);
        }
    }
}
