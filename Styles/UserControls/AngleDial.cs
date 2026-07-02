using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Writersword.Styles.UserControls
{
    // Стандартный циферблат выбора угла: 0 сверху, отсчёт по часовой, засечки по шагу
    // и подписи сторон (0/90/180/270). Значение Angle — в той же системе (0..360,
    // 0 — вверх, по часовой). Переиспользуется везде, где нужен быстрый выбор
    // направления. Программная установка Angle событие UserAngleChanged не шлёт.
    public class AngleDial : Control
    {
        public static readonly StyledProperty<double> AngleProperty =
            AvaloniaProperty.Register<AngleDial, double>(nameof(Angle));

        public static readonly StyledProperty<double> StepProperty =
            AvaloniaProperty.Register<AngleDial, double>(nameof(Step), 45.0);

        // Пользователь изменил угол мышью.
        public event Action? UserAngleChanged;

        static AngleDial()
        {
            AffectsRender<AngleDial>(AngleProperty);
        }

        public AngleDial()
        {
            Width = 48;
            Height = 48;
        }

        public double Angle
        {
            get => GetValue(AngleProperty);
            set => SetValue(AngleProperty, value);
        }

        public double Step
        {
            get => GetValue(StepProperty);
            set => SetValue(StepProperty, value);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            SetFromPoint(e.GetPosition(this));
            e.Pointer.Capture(this);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (ReferenceEquals(e.Pointer.Captured, this))
                SetFromPoint(e.GetPosition(this));
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            e.Pointer.Capture(null);
            e.Handled = true;
        }

        private void SetFromPoint(Point p)
        {
            double cx = Bounds.Width / 2, cy = Bounds.Height / 2;
            double dx = p.X - cx, dy = p.Y - cy;
            if (Math.Abs(dx) < 0.01 && Math.Abs(dy) < 0.01) return;

            // 0 сверху, по часовой: atan2(dx, -dy).
            double deg = Math.Atan2(dx, -dy) * 180.0 / Math.PI;
            if (deg < 0) deg += 360;
            if (Step > 0) deg = Math.Round(deg / Step) * Step;
            deg %= 360;

            Angle = deg;
            UserAngleChanged?.Invoke();
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);

            double w = Bounds.Width, h = Bounds.Height;
            if (w <= 0 || h <= 0) return;

            // Прозрачная подложка на все границы — чтобы контрол ловил указатель везде.
            context.FillRectangle(Brushes.Transparent, new Rect(0, 0, w, h));

            double cx = w / 2, cy = h / 2;
            double radius = Math.Min(w, h) / 2 - 1;

            var bg = Res("BgSurfaceBrush", Brushes.Transparent);
            var border = Res("BorderDefaultBrush", Brushes.Gray);
            var tick = Res("TextSecondaryBrush", Brushes.Gray);
            var accent = Res("AccentDefaultBrush", Brushes.Orange);

            context.DrawEllipse(bg, new Pen(border, 1), new Point(cx, cy), radius, radius);

            double step = Step > 0 ? Step : 45;
            for (double a = 0; a < 360; a += step)
            {
                double rad = a * Math.PI / 180.0;
                double ox = Math.Sin(rad), oy = -Math.Cos(rad);
                bool cardinal = Math.Abs(a % 90) < 1e-6;
                double inner = radius - (cardinal ? 5 : 3);
                context.DrawLine(new Pen(tick, cardinal ? 1.4 : 1),
                    new Point(cx + ox * inner, cy + oy * inner),
                    new Point(cx + ox * radius, cy + oy * radius));
            }

            DrawLabel(context, tick, "0", 0, cx, cy, radius);
            DrawLabel(context, tick, "90", 90, cx, cy, radius);
            DrawLabel(context, tick, "180", 180, cx, cy, radius);
            DrawLabel(context, tick, "270", 270, cx, cy, radius);

            double hrad = Angle * Math.PI / 180.0;
            double hx = cx + Math.Sin(hrad) * (radius - 5);
            double hy = cy - Math.Cos(hrad) * (radius - 5);
            context.DrawLine(new Pen(accent, 1.5), new Point(cx, cy), new Point(hx, hy));
            context.DrawEllipse(accent, null, new Point(hx, hy), 3, 3);
            context.DrawEllipse(tick, null, new Point(cx, cy), 1.5, 1.5);
        }

        private static void DrawLabel(DrawingContext ctx, IBrush brush, string text, double compassDeg,
            double cx, double cy, double radius)
        {
            var ft = new FormattedText(text, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Typeface.Default, 8, brush);
            double rad = compassDeg * Math.PI / 180.0;
            double ox = Math.Sin(rad), oy = -Math.Cos(rad);
            double rLabel = radius - 9;
            var origin = new Point(cx + ox * rLabel - ft.Width / 2, cy + oy * rLabel - ft.Height / 2);
            ctx.DrawText(ft, origin);
        }

        private IBrush Res(string key, IBrush fallback)
            => this.TryFindResource(key, out var v) && v is IBrush b ? b : fallback;
    }
}
