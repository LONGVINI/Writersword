using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using System;

namespace Writersword.Modules.TextEditor.Views.StatusBar
{
    public partial class ZoomSlider : UserControl
    {
        public static readonly StyledProperty<double> ValueProperty =
            AvaloniaProperty.Register<ZoomSlider, double>(nameof(Value), 100.0);

        public static readonly StyledProperty<double> MinimumProperty =
            AvaloniaProperty.Register<ZoomSlider, double>(nameof(Minimum), 25.0);

        public static readonly StyledProperty<double> MaximumProperty =
            AvaloniaProperty.Register<ZoomSlider, double>(nameof(Maximum), 500.0);

        private const double TrackPad = 5;
        public double Value
        {
            get => GetValue(ValueProperty);
            set => SetValue(ValueProperty, value < Minimum ? Minimum : value > Maximum ? Maximum : value);
        }

        public double Minimum
        {
            get => GetValue(MinimumProperty);
            set => SetValue(MinimumProperty, value);
        }

        public double Maximum
        {
            get => GetValue(MaximumProperty);
            set => SetValue(MaximumProperty, value);
        }

        private Border? _fill;
        private Ellipse? _thumb;

        static ZoomSlider()
        {
            // Перерисовываем когда Value меняется через биндинг
            ValueProperty.Changed.AddClassHandler<ZoomSlider>((s, _) => s.UpdateVisual());
        }

        public ZoomSlider()
        {
            InitializeComponent();
            Cursor = new Cursor(StandardCursorType.Hand);
        }

        protected override void OnLoaded(Avalonia.Interactivity.RoutedEventArgs e)
        {
            base.OnLoaded(e);
            _fill = this.FindControl<Border>("FillTrack");
            _thumb = this.FindControl<Ellipse>("Thumb");
            UpdateVisual();
        }

        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            UpdateVisual();
        }

        private void UpdateVisual()
        {
            if (_fill is null || _thumb is null) return;
            double w = Math.Max(Bounds.Width - TrackPad * 2, 1);
            double ratio = (Value - Minimum) / (Maximum - Minimum);
            ratio = ratio < 0 ? 0 : ratio > 1 ? 1 : ratio;
            double fillW = ratio * w;

            _fill.Width = fillW;
            _thumb.Margin = new Thickness(fillW - 5, 0, 0, 0);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            SetFromPoint(e.GetPosition(this).X);
            e.Pointer.Capture(this);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            SetFromPoint(e.GetPosition(this).X);
            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            e.Pointer.Capture(null);
        }

        private void SetFromPoint(double x)
        {
            double w = Math.Max(Bounds.Width - TrackPad * 2, 1);
            double ratio = (x - TrackPad) / w;
            ratio = ratio < 0 ? 0 : ratio > 1 ? 1 : ratio;
            SetCurrentValue(ValueProperty, Minimum + ratio * (Maximum - Minimum));
        }
    }
}