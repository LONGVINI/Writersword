using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Writersword.Core.Models.Rendering;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Rendering;

namespace Writersword.Modules.TextEditor.Views.Shared
{
    /// <summary>
    /// Образец линии границы для ленты: горизонтальная черта выбранного вида во всю
    /// ширину контрола. Рисунок берётся оттуда же, откуда его берёт документ
    /// (<see cref="SKBorderLineShape"/>): сколько черт, какой они толщины, как идут
    /// штрихи. По образцу видно, какой линия ляжет в таблицу, ещё до нажатия.
    ///
    /// Черты составных линий идут сверху вниз в том же порядке, что в документе: у
    /// «тонкой и толстой» первой толстая черта, у «толстой и тонкой» — тонкая.
    /// </summary>
    public sealed class BorderLineSample : Control
    {
        /// <summary>Вид линии.</summary>
        public static readonly StyledProperty<BorderStyle> LineStyleProperty =
            AvaloniaProperty.Register<BorderLineSample, BorderStyle>(nameof(LineStyle), BorderStyle.Single);

        /// <summary>Толщина основной черты образца в единицах экрана.</summary>
        public static readonly StyledProperty<double> LineWidthProperty =
            AvaloniaProperty.Register<BorderLineSample, double>(nameof(LineWidth), 1.5);

        /// <summary>Кисть линии.</summary>
        public static readonly StyledProperty<IBrush?> ForegroundProperty =
            AvaloniaProperty.Register<BorderLineSample, IBrush?>(nameof(Foreground));

        static BorderLineSample()
        {
            AffectsRender<BorderLineSample>(LineStyleProperty, LineWidthProperty, ForegroundProperty);
        }

        public BorderStyle LineStyle
        {
            get => GetValue(LineStyleProperty);
            set => SetValue(LineStyleProperty, value);
        }

        public double LineWidth
        {
            get => GetValue(LineWidthProperty);
            set => SetValue(LineWidthProperty, value);
        }

        public IBrush? Foreground
        {
            get => GetValue(ForegroundProperty);
            set => SetValue(ForegroundProperty, value);
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);

            var brush = Foreground;
            double width = Bounds.Width;
            double height = Bounds.Height;
            var style = LineStyle;
            if (brush is null || width <= 0 || height <= 0 || style == BorderStyle.None) return;

            int code = BorderLineCodes.Of(style);
            float line = (float)Math.Max(0.5, LineWidth);
            double centerY = height / 2.0;

            if (SKBorderLineShape.Strands(code, line) is { } strands)
            {
                DrawStrands(context, brush, strands, SKBorderLineShape.SpanPt(code, line), width, centerY);
                return;
            }

            if (code == SKBorderLineShape.Wave)
            {
                DrawWave(context, brush, line, width, centerY);
                return;
            }

            if (code == SKBorderLineShape.DoubleWave)
            {
                // Две тонкие волны вплотную одна к другой.
                float waveLine = SKBorderLineShape.DoubleWaveStrokePt(line);
                double offset = SKBorderLineShape.WaveSpanPt(waveLine) / 2.0;
                DrawWave(context, brush, waveLine, width, centerY - offset);
                DrawWave(context, brush, waveLine, width, centerY + offset);
                return;
            }

            if (code == SKBorderLineShape.DashDotStroked)
            {
                DrawStroked(context, brush, line, width, centerY);
                return;
            }

            if (SKBorderLineShape.IsThreeD(code))
            {
                DrawThreeD(context, brush, code, line, width, centerY);
                return;
            }

            if (SKBorderLineShape.DashUnits(code) is { } dashUnits)
            {
                var dashes = new double[dashUnits.Length];
                for (int i = 0; i < dashUnits.Length; i++) dashes[i] = dashUnits[i];

                var dashed = new Pen(brush, line, new DashStyle(dashes, 0));
                context.DrawLine(dashed, new Point(0, centerY), new Point(width, centerY));
                return;
            }

            context.FillRectangle(brush, new Rect(0, centerY - line / 2.0, width, line));
        }

        /// <summary>Сплошные черты одна под другой, первая — сверху.</summary>
        private static void DrawStrands(
            DrawingContext context, IBrush brush, float[] strands, float span, double width, double centerY)
        {
            double top = centerY - span / 2.0;

            for (int i = 0; i + 1 < strands.Length; i += 2)
            {
                double strandWidth = Math.Max(0.75, strands[i + 1]);
                double strandCenter = top + strands[i];
                context.FillRectangle(brush,
                    new Rect(0, strandCenter - strandWidth / 2.0, width, strandWidth));
            }
        }

        /// <summary>Волна: зигзаг с размахом и шагом, как у рамки в документе.</summary>
        private static void DrawWave(
            DrawingContext context, IBrush brush, float line, double width, double centerY)
        {
            double stroke = Math.Max(0.75, SKBorderLineShape.WaveStrokePt(line));
            double amplitude = Math.Max(0.0, SKBorderLineShape.WaveSpanPt(line) - stroke) / 2.0;
            double period = SKBorderLineShape.WavePeriodPt(line);

            int halves = Math.Max(1, (int)Math.Round(width / period)) * 2;
            double step = width / halves;

            var geometry = new StreamGeometry();
            using (var sink = geometry.Open())
            {
                for (int i = 0; i <= halves; i++)
                {
                    var point = new Point(step * i, centerY + (i % 2 == 0 ? -amplitude : amplitude));
                    if (i == 0) sink.BeginFigure(point, false);
                    else sink.LineTo(point);
                }

                sink.EndFigure(false);
            }

            var pen = new Pen(brush, stroke, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            context.DrawGeometry(null, pen, geometry);
        }

        /// <summary>Полоса из наклонных штрихов: длинный и короткий по очереди.</summary>
        private static void DrawStroked(
            DrawingContext context, IBrush brush, float line, double width, double centerY)
        {
            double half = line / 2.0;
            double longStroke = line * SKBorderLineShape.StrokedLongShare;
            double shortStroke = line * SKBorderLineShape.StrokedShortShare;
            double gap = shortStroke;
            double period = longStroke + gap + shortStroke + gap;

            var geometry = new StreamGeometry();
            using (var sink = geometry.Open())
            {
                for (double at = -line; at < width; at += period)
                {
                    AddStroke(sink, at, longStroke, line, centerY, half);
                    AddStroke(sink, at + longStroke + gap, shortStroke, line, centerY, half);
                }
            }

            using (context.PushClip(new Rect(0, centerY - half, width, line)))
            {
                context.DrawGeometry(brush, null, geometry);
            }
        }

        private static void AddStroke(
            StreamGeometryContext sink, double from, double length, double slant, double centerY, double half)
        {
            double to = from + length;
            sink.BeginFigure(new Point(from + slant, centerY - half), true);
            sink.LineTo(new Point(to + slant, centerY - half));
            sink.LineTo(new Point(to, centerY + half));
            sink.LineTo(new Point(from, centerY + half));
            sink.EndFigure(true);
        }

        /// <summary>
        /// Объёмные линии, как в документе. Выпуклая и вдавленная (outset, inset) в
        /// таблице — обычная сплошная линия. Объёмные выпуклая и вдавленная (threeDEmboss,
        /// threeDEngrave) — основная полоса с краями в половину толщины: у вдавленной
        /// тёмный край сверху и светлый снизу, у выпуклой наоборот.
        /// </summary>
        private static void DrawThreeD(
            DrawingContext context, IBrush brush, int code, float line, double width, double centerY)
        {
            if (code == SKBorderLineShape.Outset || code == SKBorderLineShape.Inset)
            {
                context.FillRectangle(brush, new Rect(0, centerY - line / 2.0, width, line));
                return;
            }

            bool engrave = code == SKBorderLineShape.ThreeDEngrave;
            double edge = Math.Max(0.75, line / 2.0);
            double top = centerY - line / 2.0 - edge;

            IBrush dark = Faded(brush, 1.0);
            IBrush light = Faded(brush, 0.45);

            context.FillRectangle(engrave ? dark : light, new Rect(0, top, width, edge));
            context.FillRectangle(Faded(brush, 0.75), new Rect(0, top + edge, width, line));
            context.FillRectangle(engrave ? light : dark, new Rect(0, top + edge + line, width, edge));
        }

        /// <summary>Та же кисть, но бледнее: светлая часть объёмной линии.</summary>
        private static IBrush Faded(IBrush brush, double opacity)
            => brush is ISolidColorBrush solid
                ? new SolidColorBrush(solid.Color, solid.Opacity * opacity)
                : brush;
    }
}
