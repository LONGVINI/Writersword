using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Writersword.Infrastructure.Behaviours
{
    public static class FocusOnVisibleBehavior
    {
        public static readonly AttachedProperty<bool> EnabledProperty =
            AvaloniaProperty.RegisterAttached<Control, bool>(
                "Enabled",
                typeof(FocusOnVisibleBehavior));

        static FocusOnVisibleBehavior()
        {
            EnabledProperty.Changed.AddClassHandler<Control>(OnEnabledChanged);
        }

        public static bool GetEnabled(Control element) => element.GetValue(EnabledProperty);
        public static void SetEnabled(Control element, bool value) => element.SetValue(EnabledProperty, value);

        private static void OnEnabledChanged(Control control, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.NewValue is true)
            {
                control.PropertyChanged += OnControlPropertyChanged;
                if (control.IsVisible)
                    FocusAndMoveCaret(control);
            }
            else
            {
                control.PropertyChanged -= OnControlPropertyChanged;
            }
        }

        private static void OnControlPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (sender is not Control control) return;
            if (e.Property != Visual.IsVisibleProperty) return;
            if (e.NewValue is not true) return;
            FocusAndMoveCaret(control);
        }

        private static void FocusAndMoveCaret(Control control)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!control.IsVisible) return;
                control.Focus();
                if (control is TextBox tb)
                    tb.CaretIndex = tb.Text?.Length ?? 0;
            }, DispatcherPriority.Loaded);
        }
    }
}