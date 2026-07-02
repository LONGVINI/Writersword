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
                    float radius = 0.5f * Math.Max(rect.Width, rect.Height);
                    if (radius <= 0f) radius = 1f;
                    return SKShader.CreateRadialGradient(
                        new SKPoint(cx, cy), radius, colors, positions, SKShaderTileMode.Clamp);
                }

                case GradientKind.Conic:
                {
                    var sweep = SKShader.CreateSweepGradient(new SKPoint(cx, cy), colors, positions);
                    if (Math.Abs(spec.AngleDeg) > 0.001)
                        sweep = sweep.WithLocalMatrix(SKMatrix.CreateRotationDegrees((float)spec.AngleDeg, cx, cy));
                    return sweep;
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
