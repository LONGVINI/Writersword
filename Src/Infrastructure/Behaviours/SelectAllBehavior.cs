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
    /// ввести новое значение или убрать старое целиком, без ручного выделения.
    /// Выделение откладывается до конца обработки клика, иначе установка каретки по
    /// нажатию сбросила бы его. Если фокус пришёл щелчком, выделение ещё раз
    /// проверяется на отпускании кнопки: если к этому моменту от него осталась одна
    /// каретка, текст выделяется заново. Протянутое мышью выделение не трогается —
    /// его сделали нарочно.
    /// Следующие щелчки по уже активному полю ставят каретку как обычно.
    /// </summary>
    public static class SelectAllBehavior
    {
        public static readonly AttachedProperty<bool> EnabledProperty =
            AvaloniaProperty.RegisterAttached<Control, bool>(
                "Enabled",
                typeof(SelectAllBehavior));

        // Фокус пришёл щелчком, и отпускание кнопки ещё не обработано.
        private static readonly AttachedProperty<bool> PendingReleaseProperty =
            AvaloniaProperty.RegisterAttached<Control, bool>(
                "PendingRelease",
                typeof(SelectAllBehavior));

        private static readonly EventHandler<FocusChangedEventArgs> FocusHandler = OnGotFocus;
        private static readonly EventHandler<RoutedEventArgs> LostFocusHandler = OnLostFocus;
        private static readonly EventHandler<PointerReleasedEventArgs> ReleaseHandler = OnPointerReleased;

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
            {
                tb.AddHandler(InputElement.GotFocusEvent, FocusHandler, RoutingStrategies.Bubble);
                tb.AddHandler(InputElement.LostFocusEvent, LostFocusHandler, RoutingStrategies.Bubble);
                tb.AddHandler(InputElement.PointerReleasedEvent, ReleaseHandler, RoutingStrategies.Bubble, handledEventsToo: true);
            }
            else
            {
                tb.RemoveHandler(InputElement.GotFocusEvent, FocusHandler);
                tb.RemoveHandler(InputElement.LostFocusEvent, LostFocusHandler);
                tb.RemoveHandler(InputElement.PointerReleasedEvent, ReleaseHandler);
                tb.ClearValue(PendingReleaseProperty);
            }
        }

        private static void OnGotFocus(object? sender, FocusChangedEventArgs e)
        {
            if (sender is not TextBox tb) return;

            tb.SetValue(PendingReleaseProperty, e.NavigationMethod == NavigationMethod.Pointer);

            Dispatcher.UIThread.Post(() =>
            {
                if (tb.IsFocused) tb.SelectAll();
            }, DispatcherPriority.Input);
        }

        private static void OnLostFocus(object? sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb)
                tb.ClearValue(PendingReleaseProperty);
        }

        private static void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (sender is not TextBox tb) return;
            if (!tb.GetValue(PendingReleaseProperty)) return;

            tb.ClearValue(PendingReleaseProperty);
            if (!tb.IsFocused) return;

            if (tb.SelectionStart == tb.SelectionEnd)
                tb.SelectAll();
        }
    }
}
