using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Writersword.Infrastructure.Behaviours
{
    public static class FocusOnVisibleBehavior
    {
        public static readonly AttachedProperty<bool> EnabledProperty =
            AvaloniaProperty.RegisterAttached<TextBox, bool>(
                "Enabled",
                typeof(FocusOnVisibleBehavior));

        static FocusOnVisibleBehavior()
        {
            EnabledProperty.Changed.AddClassHandler<TextBox>(OnEnabledChanged);
        }

        public static bool GetEnabled(TextBox element) => element.GetValue(EnabledProperty);
        public static void SetEnabled(TextBox element, bool value) => element.SetValue(EnabledProperty, value);

        private static void OnEnabledChanged(TextBox textBox, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.NewValue is true && textBox.IsVisible)
                FocusAndMoveCaret(textBox);
            else
                textBox.PropertyChanged -= OnTextBoxPropertyChanged;

            if (e.NewValue is true)
                textBox.PropertyChanged += OnTextBoxPropertyChanged;
        }

        private static void OnTextBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (sender is not TextBox textBox) return;
            if (e.Property != Visual.IsVisibleProperty) return;
            if (e.NewValue is not true) return;

            FocusAndMoveCaret(textBox);
        }

        private static void FocusAndMoveCaret(TextBox textBox)
        {
            // Dispatcher.UIThread.Post — даём Avalonia завершить layout-проход
            // прежде чем запрашивать фокус, иначе Focus() игнорируется
            Dispatcher.UIThread.Post(() =>
            {
                if (!textBox.IsVisible) return;
                textBox.Focus();
                textBox.CaretIndex = textBox.Text?.Length ?? 0;
            }, DispatcherPriority.Loaded);
        }
    }
}