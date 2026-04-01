using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using System;
using System.Reactive.Linq;

namespace Writersword.Infrastructure.Behaviours
{
    public sealed class FocusOnVisibleBehavior
    {
        public static readonly AttachedProperty<bool> EnabledProperty =
            AvaloniaProperty.RegisterAttached<FocusOnVisibleBehavior, Control, bool>("Enabled");

        static FocusOnVisibleBehavior()
        {
            EnabledProperty.Changed.AddClassHandler<Control>((ctrl, e) =>
            {
                if (e.NewValue is not true) return;
                ctrl.GetObservable(Visual.IsVisibleProperty)
                    .DistinctUntilChanged()
                    .Subscribe(isVisible =>
                    {
                        if (!isVisible) return;
                        Dispatcher.UIThread.Post(() =>
                        {
                            ctrl.Focus();
                            if (ctrl is TextBox tb)
                                tb.CaretIndex = tb.Text?.Length ?? 0;
                        }, DispatcherPriority.Input);
                    });
            });
        }

        public static bool GetEnabled(Control ctrl) => ctrl.GetValue(EnabledProperty);
        public static void SetEnabled(Control ctrl, bool value) => ctrl.SetValue(EnabledProperty, value);
    }
}