using SkiaSharp;
using System;
using System.Collections.Generic;
using Writersword.Core.Models.Project;

namespace Writersword.Modules.TextEditor.Rendering
{
    // Строит SKShader из универсального описания цвета для отрисовки текста и
    // задников через SkiaSharp. Прямоугольник rect задаёт область, по которой
    // натягивается градиент: для режима «весь блок» сюда передают bounds абзаца,
    // для «построчно» — bounds конкретной строки.
    public static class GradientShaderFactory
    {
        public static bool IsGradient(GradientSpec? spec) => spec != null && !spec.IsSolid;

        public static SKColor SolidColor(GradientSpec? spec)
        {
            if (spec == null) return SKColors.Black;
            return ParseColor(spec.SolidHex);
        }

        // Возвращает шейдер для многоцветного спека либо null для одноцвета
        // (в этом случае вызывающий код просто красит плоским цветом).
        public static SKShader? BuildShader(GradientSpec? spec, SKRect rect)
        {
            if (spec == null || spec.IsSolid)
                return null;

            BuildStops(spec, out var colors, out var positions);

            float cx = rect.MidX;
            float cy = rect.MidY;

            switch (spec.Kind)
            {
                case GradientKind.Radial:
                {
                    // Как у подборщика цвета (RadialGradientBrush с радиусами 50% на 50%):
                    // эллипс, вписанный в прямоугольник, а не круг по большей стороне.
                    float radiusX = Math.Max(0.5f * rect.Width, 0.5f);
                    float radiusY = Math.Max(0.5f * rect.Height, 0.5f);
                    var radial = SKShader.CreateRadialGradient(
                        new SKPoint(cx, cy), radiusX, colors, positions, SKShaderTileMode.Clamp);
                    if (Math.Abs(radiusX - radiusY) <= 0.01f) return radial;
                    using (radial)
                        return radial.WithLocalMatrix(SKMatrix.CreateScale(1f, radiusY / radiusX, cx, cy));
                }

                case GradientKind.Conic:
                {
                    // У Skia нулевой угол развёртки смотрит вправо, а у подборщика цвета
                    // (ConicGradientBrush, как в CSS) — вверх. Без сдвига на 90 градусов
                    // градиент на листе и в диаграмме был повёрнут против часовой
                    // относительно образца: то, что в образце внизу, оказывалось слева.
                    using var sweep = SKShader.CreateSweepGradient(new SKPoint(cx, cy), colors, positions);
                    return sweep.WithLocalMatrix(SKMatrix.CreateRotationDegrees((float)spec.AngleDeg - 90f, cx, cy));
                }

                default:
                {
                    var (start, end) = AnglePoints(spec.AngleDeg, rect);
                    return SKShader.CreateLinearGradient(
                        start, end, colors, positions, SKShaderTileMode.Clamp);
                }
            }
        }

        private static void BuildStops(GradientSpec spec, out SKColor[] colors, out float[] positions)
        {
            var sorted = spec.SortedStops();
            colors = new SKColor[sorted.Count];
            positions = new float[sorted.Count];
            for (int i = 0; i < sorted.Count; i++)
            {
                colors[i] = ParseColor(sorted[i].Hex);
                positions[i] = (float)sorted[i].Position;
            }
        }

        private static SKColor ParseColor(string hex)
            => SKColor.TryParse(hex, out var c) ? c : SKColors.Black;

        // Угол в две точки внутри rect: 0 — слева направо, 90 — снизу вверх
        // (ось Y направлена вниз, поэтому верх — это меньшее значение Y).
        private static (SKPoint start, SKPoint end) AnglePoints(double deg, SKRect rect)
        {
            var rad = deg * Math.PI / 180.0;
            var cos = (float)Math.Cos(rad);
            var sin = (float)Math.Sin(rad);

            float cx = rect.MidX;
            float cy = rect.MidY;
            float hw = rect.Width * 0.5f;
            float hh = rect.Height * 0.5f;

            var start = new SKPoint(cx - hw * cos, cy + hh * sin);
            var end = new SKPoint(cx + hw * cos, cy - hh * sin);
            return (start, end);
        }
    }
}
