using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Writersword.Infrastructure.Behaviours
{
    /// <summary>
    /// При получении фокуса полем ввода выделяет весь его текст, чтобы можно было сразу
    /// ввести новое значение без предварительного ручного выделения. Выделение откладывается
    /// до конца обработки клика, иначе установка каретки по нажатию сбросила бы его.
    /// </summary>
    public static class SelectAllBehavior
    {
        public static readonly AttachedProperty<bool> EnabledProperty =
            AvaloniaProperty.RegisterAttached<Control, bool>(
                "Enabled",
                typeof(SelectAllBehavior));

        private static readonly EventHandler<RoutedEventArgs> FocusHandler = OnGotFocus;

        static SelectAllBehavior()
        {
            EnabledProperty.Changed.AddClassHandler<Control>(OnEnabledChanged);
        }

        public static bool GetEnabled(Control element) => element.GetValue(EnabledProperty);
        public static void SetEnabled(Control element, bool value) => element.SetValue(EnabledProperty, value);

        private static void OnEnabledChanged(Control control, AvaloniaPropertyChangedEventArgs e)
        {
            if (control is not TextBox tb) return;

            if (e.NewValue is true)
                tb.AddHandler(InputElement.GotFocusEvent, FocusHandler, RoutingStrategies.Bubble);
            else
                tb.RemoveHandler(InputElement.GotFocusEvent, FocusHandler);
        }

        private static void OnGotFocus(object? sender, RoutedEventArgs e)
        {
            if (sender is not TextBox tb) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (tb.IsFocused) tb.SelectAll();
            }, DispatcherPriority.Input);
        }
    }
}
