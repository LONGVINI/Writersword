using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.Rendering;

namespace Writersword.Modules.TextEditor.Views.Shared
{
    /// <summary>
    /// Образец линии подчёркивания для меню и кнопки ленты. Рисунок берёт из
    /// <see cref="UnderlineShape"/> — того же описания, по которому линия ложится под
    /// текст документа, поэтому образец совпадает с результатом.
    ///
    /// Толщина рисунка подбирается по высоте контрола: образец в плитке меню и черта
    /// под буквой на кнопке рисуются одним контролом разного размера.
    /// </summary>
    public sealed class UnderlineSample : Control
    {
        /// <summary>Вид подчёркивания, который показывает образец.</summary>
        public static readonly StyledProperty<UnderlineStyle> KindProperty =
            AvaloniaProperty.Register<UnderlineSample, UnderlineStyle>(nameof(Kind), UnderlineStyle.Single);

        /// <summary>Кисть линии.</summary>
        public static readonly StyledProperty<IBrush?> StrokeProperty =
            AvaloniaProperty.Register<UnderlineSample, IBrush?>(nameof(Stroke));

        static UnderlineSample()
        {
            AffectsRender<UnderlineSample>(KindProperty, StrokeProperty);
        }

        public UnderlineStyle Kind
        {
            get => GetValue(KindProperty);
            set => SetValue(KindProperty, value);
        }

        public IBrush? Stroke
        {
            get => GetValue(StrokeProperty);
            set => SetValue(StrokeProperty, value);
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);

            var brush = Stroke;
            double width = Bounds.Width;
            double height = Bounds.Height;
            if (brush is null || width <= 0 || height <= 0 || Kind == UnderlineStyle.None) return;

            var shape = UnderlineShape.Of(Kind);

            // Базовая толщина — седьмая часть высоты, но не тоньше точки экрана:
            // в плитке меню высотой 10 это полторы точки, жирная линия — три.
            double thickness = Math.Max(1.0, height / 7.0);
            double stroke = thickness * shape.Weight;

            IDashStyle? dash = null;
            if (shape.Dash is { Length: > 0 } pattern)
            {
                // Штрихи у пера Avalonia меряются в толщинах самого пера, а в описании
                // линии — в базовых толщинах.
                var dashes = new double[pattern.Length];
                for (int i = 0; i < pattern.Length; i++)
                    dashes[i] = pattern[i] / shape.Weight;
                dash = new DashStyle(dashes, 0);
            }

            var pen = new Pen(brush, stroke, dash, PenLineCap.Flat);
            double centerY = height / 2.0;

            if (shape.IsWave)
            {
                double amplitude = shape.WaveAmplitude * thickness;
                double length = shape.WaveLength * thickness;

                if (shape.IsDouble)
                {
                    double shift = (UnderlineShape.WaveDoubleGap * thickness + amplitude) / 2.0;
                    DrawWave(context, pen, width, centerY - shift, amplitude, length);
                    DrawWave(context, pen, width, centerY + shift, amplitude, length);
                }
                else
                {
                    DrawWave(context, pen, width, centerY, amplitude, length);
                }
                return;
            }

            if (shape.IsDouble)
            {
                double shift = UnderlineShape.DoubleGap * thickness / 2.0;
                DrawStraight(context, pen, shape, width, centerY - shift);
                DrawStraight(context, pen, shape, width, centerY + shift);
                return;
            }

            DrawStraight(context, pen, shape, width, centerY);
        }

        /// <summary>
        /// Прямая линия во всю ширину. «Только слова» рисуется двумя отрезками с
        /// просветом посередине — как два подчёркнутых слова и пробел между ними.
        /// </summary>
        private static void DrawStraight(DrawingContext context, IPen pen, UnderlineShape shape,
            double width, double y)
        {
            if (shape.WordsOnly)
            {
                context.DrawLine(pen, new Point(0, y), new Point(width * 0.42, y));
                context.DrawLine(pen, new Point(width * 0.58, y), new Point(width, y));
                return;
            }

            context.DrawLine(pen, new Point(0, y), new Point(width, y));
        }

        private static void DrawWave(DrawingContext context, IPen pen,
            double width, double centerY, double amplitude, double length)
        {
            if (length <= 0) return;

            double step = length / 12.0;
            double k = 2.0 * Math.PI / length;

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                double x = 0;
                ctx.BeginFigure(new Point(x, centerY + amplitude * Math.Sin(x * k)), false);
                while (x < width)
                {
                    x = Math.Min(x + step, width);
                    ctx.LineTo(new Point(x, centerY + amplitude * Math.Sin(x * k)));
                }
                ctx.EndFigure(false);
            }

            context.DrawGeometry(null, pen, geometry);
        }
    }
}
