using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Writersword.Infrastructure.Behaviours
{
    /// <summary>
    /// Отбирает колесо мыши у элементов выбора и передаёт прокрутку странице.
    ///
    /// По умолчанию Avalonia отдаёт колесо самому элементу: ComboBox листает
    /// выбранное значение, NumericUpDown крутит число. На длинной странице
    /// настроек это ловушка — пользователь прокручивает страницу, курсор
    /// проходит над полем, и настройка меняется молча, без единого щелчка.
    ///
    /// Обработчик стоит в туннельной фазе, то есть срабатывает раньше самого
    /// элемента, помечает событие обработанным и вместо изменения значения
    /// сдвигает ближайший ScrollViewer.
    /// </summary>
    public static class NoWheelBehavior
    {
        public static readonly AttachedProperty<bool> EnabledProperty =
            AvaloniaProperty.RegisterAttached<Control, bool>(
                "Enabled",
                typeof(NoWheelBehavior));

        private static readonly EventHandler<PointerWheelEventArgs> WheelHandler = OnWheel;

        /// <summary>Сколько пикселей проходит страница за один щелчок колеса.</summary>
        private const double ScrollStep = 60;

        static NoWheelBehavior()
        {
            EnabledProperty.Changed.AddClassHandler<Control>(OnEnabledChanged);
        }

        public static bool GetEnabled(Control element) => element.GetValue(EnabledProperty);
        public static void SetEnabled(Control element, bool value) => element.SetValue(EnabledProperty, value);

        private static void OnEnabledChanged(Control control, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.NewValue is true)
                control.AddHandler(InputElement.PointerWheelChangedEvent, WheelHandler, RoutingStrategies.Tunnel);
            else
                control.RemoveHandler(InputElement.PointerWheelChangedEvent, WheelHandler);
        }

        private static void OnWheel(object? sender, PointerWheelEventArgs e)
        {
            if (sender is not Visual visual) return;

            // В раскрытом списке колесо нужно по прямому назначению — прокрутить
            // сам список вариантов.
            if (sender is ComboBox { IsDropDownOpen: true }) return;

            var scroll = visual.FindAncestorOfType<ScrollViewer>();

            if (scroll == null)
            {
                // Прокручивать нечего, но значение менять всё равно не нужно:
                // в неподвижном окне это была бы та же незаметная правка.
                e.Handled = true;
                return;
            }

            double maxOffset = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
            double target = scroll.Offset.Y - e.Delta.Y * ScrollStep;

            scroll.Offset = new Vector(scroll.Offset.X, Math.Clamp(target, 0, maxOffset));
            e.Handled = true;
        }
    }
}
