using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Views.Dialogs
{
    /// <summary>
    /// Значок вида диаграммы: маленькая цветная диаграмма этого вида — столбцы,
    /// линии, доли, — цветами палитры Office, как в галерее видов у Word.
    ///
    /// Рисуется в собственной сетке 32 × 24 и растягивается на размер элемента с
    /// сохранением пропорций. Без заданного размера занимает 32 × 24.
    /// </summary>
    public sealed class ChartKindIcon : Control
    {
        public static readonly StyledProperty<ChartKind> KindProperty =
            AvaloniaProperty.Register<ChartKindIcon, ChartKind>(nameof(Kind), ChartKind.Column);

        /// <summary>Комбинированная: столбцы и линия по вспомогательной оси.</summary>
        public static readonly StyledProperty<bool> ComboProperty =
            AvaloniaProperty.Register<ChartKindIcon, bool>(nameof(Combo));

        private const double GridWidth = 32.0;
        private const double GridHeight = 24.0;

        private static readonly IBrush Blue = new SolidColorBrush(Color.FromRgb(0x44, 0x72, 0xC4));
        private static readonly IBrush Orange = new SolidColorBrush(Color.FromRgb(0xED, 0x7D, 0x31));
        private static readonly IBrush Gray = new SolidColorBrush(Color.FromRgb(0xA5, 0xA5, 0xA5));
        private static readonly IBrush Gold = new SolidColorBrush(Color.FromRgb(0xFF, 0xC0, 0x00));
        private static readonly IBrush Axis = new SolidColorBrush(Color.FromArgb(0x99, 0x8C, 0x8C, 0x8C));

        static ChartKindIcon()
        {
            AffectsRender<ChartKindIcon>(KindProperty, ComboProperty);
        }

        public ChartKind Kind
        {
            get => GetValue(KindProperty);
            set => SetValue(KindProperty, value);
        }

        public bool Combo
        {
            get => GetValue(ComboProperty);
            set => SetValue(ComboProperty, value);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double width = double.IsNaN(Width) ? GridWidth : Width;
            double height = double.IsNaN(Height) ? GridHeight : Height;
            return new Size(width, height);
        }

        public override void Render(DrawingContext context)
        {
            var bounds = Bounds;
            if (bounds.Width < 2 || bounds.Height < 2) return;

            double scale = Math.Min(bounds.Width / GridWidth, bounds.Height / GridHeight);
            double offsetX = (bounds.Width - GridWidth * scale) / 2.0;
            double offsetY = (bounds.Height - GridHeight * scale) / 2.0;

            using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offsetX, offsetY)))
            {
                if (Combo)
                {
                    DrawCombo(context);
                    return;
                }

                switch (Kind)
                {
                    case ChartKind.Column: DrawColumns(context); break;
                    case ChartKind.Bar: DrawBars(context); break;
                    case ChartKind.Line: DrawLines(context); break;
                    case ChartKind.Area: DrawAreas(context); break;
                    case ChartKind.Pie: DrawPie(context, hole: 0.0); break;
                    case ChartKind.Doughnut: DrawPie(context, hole: 0.5); break;
                    case ChartKind.Scatter: DrawScatter(context); break;
                    case ChartKind.Bubble: DrawBubbles(context); break;
                    case ChartKind.Radar: DrawRadar(context); break;
                    case ChartKind.Stock: DrawStock(context); break;
                }
            }
        }

        // ── Виды ─────────────────────────────────────────────────────────

        private static void Baseline(DrawingContext context)
            => context.DrawRectangle(Axis, null, new Rect(2, 21.5, 28, 1));

        private static void DrawColumns(DrawingContext context)
        {
            Baseline(context);
            double[] blue = { 11, 15, 8 };
            double[] orange = { 7, 10, 13 };
            for (int i = 0; i < 3; i++)
            {
                double x = 3.5 + i * 9.0;
                context.DrawRectangle(Blue, null, new Rect(x, 21.5 - blue[i], 3.5, blue[i]));
                context.DrawRectangle(Orange, null, new Rect(x + 3.8, 21.5 - orange[i], 3.5, orange[i]));
            }
        }

        private static void DrawBars(DrawingContext context)
        {
            context.DrawRectangle(Axis, null, new Rect(3, 2, 1, 20));
            double[] blue = { 18, 12, 22 };
            double[] orange = { 11, 20, 8 };
            for (int i = 0; i < 3; i++)
            {
                double y = 3.0 + i * 6.6;
                context.DrawRectangle(Blue, null, new Rect(4, y, blue[i], 2.8));
                context.DrawRectangle(Orange, null, new Rect(4, y + 3.0, orange[i], 2.8));
            }
        }

        private static void DrawLines(DrawingContext context)
        {
            Baseline(context);
            Polyline(context, Blue, (3, 16), (10, 9), (17, 12), (24, 5), (29, 7));
            Polyline(context, Orange, (3, 19), (10, 15), (17, 17), (24, 12), (29, 14));
        }

        private static void DrawAreas(DrawingContext context)
        {
            Baseline(context);
            Polygon(context, Blue, (2, 21.5), (2, 12), (9, 6), (16, 10), (23, 4), (30, 8), (30, 21.5));
            Polygon(context, Orange, (2, 21.5), (2, 17), (9, 14), (16, 16), (23, 12), (30, 15), (30, 21.5));
        }

        private static void DrawPie(DrawingContext context, double hole)
        {
            var center = new Point(16, 12);
            const double radius = 10.5;
            double[] shares = { 0.55, 0.22, 0.13, 0.10 };
            IBrush[] colors = { Blue, Orange, Gray, Gold };

            double start = -90.0;
            for (int i = 0; i < shares.Length; i++)
            {
                double sweep = shares[i] * 360.0;
                context.DrawGeometry(colors[i], null, Slice(center, radius, radius * hole, start, sweep));
                start += sweep;
            }
        }

        private static void DrawScatter(DrawingContext context)
        {
            Baseline(context);
            context.DrawRectangle(Axis, null, new Rect(2, 2, 1, 20));
            (double X, double Y)[] blue = { (7, 16), (11, 11), (15, 13), (21, 7), (26, 9) };
            (double X, double Y)[] orange = { (8, 19), (13, 17), (19, 15), (24, 13), (28, 4) };
            foreach (var (x, y) in blue) context.DrawEllipse(Blue, null, new Point(x, y), 1.7, 1.7);
            foreach (var (x, y) in orange) context.DrawEllipse(Orange, null, new Point(x, y), 1.7, 1.7);
        }

        private static void DrawBubbles(DrawingContext context)
        {
            Baseline(context);
            context.DrawEllipse(Blue, null, new Point(9, 14), 5, 5);
            context.DrawEllipse(Orange, null, new Point(20, 8), 4, 4);
            context.DrawEllipse(Gray, null, new Point(25, 16), 2.6, 2.6);
        }

        private static void DrawRadar(DrawingContext context)
        {
            var center = new Point(16, 12.5);
            var grid = new Pen(Axis, 0.8);
            Point Ray(int i, double r)
            {
                double angle = -Math.PI / 2 + i * 2 * Math.PI / 5;
                return new Point(center.X + r * Math.Cos(angle), center.Y + r * Math.Sin(angle));
            }

            var outline = new StreamGeometry();
            using (var g = outline.Open())
            {
                g.BeginFigure(Ray(0, 10.5), false);
                for (int i = 1; i < 5; i++) g.LineTo(Ray(i, 10.5));
                g.EndFigure(true);
            }
            context.DrawGeometry(null, grid, outline);
            for (int i = 0; i < 5; i++) context.DrawLine(grid, center, Ray(i, 10.5));

            double[] blue = { 9.5, 6, 8.5, 4.5, 7 };
            double[] orange = { 5, 9.5, 4, 8, 5 };
            context.DrawGeometry(null, new Pen(Orange, 1.6), Closed(Ray, orange));
            context.DrawGeometry(null, new Pen(Blue, 1.6), Closed(Ray, blue));
        }

        private static void DrawStock(DrawingContext context)
        {
            Baseline(context);
            var ink = new Pen(new SolidColorBrush(Color.FromRgb(0x59, 0x59, 0x59)), 1.1);
            (double X, double High, double Low, double Open, double Close)[] days =
            {
                (6, 5, 18, 14, 9), (13, 8, 19, 10, 15), (20, 3, 14, 11, 6), (27, 6, 16, 8, 12)
            };
            foreach (var (x, high, low, open, close) in days)
            {
                context.DrawLine(ink, new Point(x, high), new Point(x, low));
                double top = Math.Min(open, close);
                double height = Math.Abs(open - close);
                IBrush body = close < open ? Blue : Orange;
                context.DrawRectangle(body, null, new Rect(x - 2.2, top, 4.4, Math.Max(1.5, height)));
            }
        }

        private static void DrawCombo(DrawingContext context)
        {
            Baseline(context);
            double[] heights = { 9, 13, 7, 15 };
            for (int i = 0; i < heights.Length; i++)
                context.DrawRectangle(Blue, null, new Rect(3.5 + i * 7.0, 21.5 - heights[i], 4.2, heights[i]));
            Polyline(context, Orange, (5.6, 15), (12.6, 10), (19.6, 8), (26.6, 3.5));
        }

        // ── Построения ───────────────────────────────────────────────────

        private static void Polyline(DrawingContext context, IBrush brush, params (double X, double Y)[] points)
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(new Point(points[0].X, points[0].Y), false);
                for (int i = 1; i < points.Length; i++) g.LineTo(new Point(points[i].X, points[i].Y));
                g.EndFigure(false);
            }
            context.DrawGeometry(null, new Pen(brush, 1.9, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
        }

        private static void Polygon(DrawingContext context, IBrush brush, params (double X, double Y)[] points)
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(new Point(points[0].X, points[0].Y), true);
                for (int i = 1; i < points.Length; i++) g.LineTo(new Point(points[i].X, points[i].Y));
                g.EndFigure(true);
            }
            context.DrawGeometry(brush, null, geometry);
        }

        private static StreamGeometry Closed(Func<int, double, Point> ray, double[] radii)
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(ray(0, radii[0]), false);
                for (int i = 1; i < radii.Length; i++) g.LineTo(ray(i, radii[i]));
                g.EndFigure(true);
            }
            return geometry;
        }

        /// <summary>Доля круга или кольца: от угла start по часовой на sweep градусов.</summary>
        private static StreamGeometry Slice(Point center, double radius, double inner, double startDeg, double sweepDeg)
        {
            static Point At(Point c, double r, double deg)
            {
                double rad = deg * Math.PI / 180.0;
                return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
            }

            double endDeg = startDeg + sweepDeg;
            bool large = sweepDeg > 180.0;
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                if (inner <= 0.01)
                {
                    g.BeginFigure(center, true);
                    g.LineTo(At(center, radius, startDeg));
                    g.ArcTo(At(center, radius, endDeg), new Size(radius, radius), 0, large, SweepDirection.Clockwise);
                }
                else
                {
                    g.BeginFigure(At(center, radius, startDeg), true);
                    g.ArcTo(At(center, radius, endDeg), new Size(radius, radius), 0, large, SweepDirection.Clockwise);
                    g.LineTo(At(center, inner, endDeg));
                    g.ArcTo(At(center, inner, startDeg), new Size(inner, inner), 0, large, SweepDirection.CounterClockwise);
                }
                g.EndFigure(true);
            }
            return geometry;
        }
    }
}
