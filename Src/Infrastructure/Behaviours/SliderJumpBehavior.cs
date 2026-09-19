using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Writersword.Infrastructure.Behaviours
{
    /// <summary>
    /// Щелчок по любой точке дорожки ставит бегунок туда же и сразу берёт его в руку:
    /// значение продолжает идти за курсором, пока кнопка зажата, — отпускать и заново
    /// цеплять бегунок не нужно.
    ///
    /// Своё поведение, а не штатное, по двум причинам. Половинки дорожки у Slider —
    /// это кнопки (RepeatButton), они перехватывают нажатие раньше самого ползунка и
    /// отрабатывают свой шаг; снять с них хит-тест мало — нажатие тогда просто уходит
    /// в никуда. И порядок: обработчик стоит туннелем, то есть срабатывает раньше всего
    /// содержимого шаблона.
    ///
    /// Ровно это уже работало в подборщике цвета у полос R, G, B — здесь тот же расчёт,
    /// вынесенный в общее поведение, чтобы второй копии не заводить.
    /// </summary>
    public static class SliderJumpBehavior
    {
        public static readonly AttachedProperty<bool> EnabledProperty =
            AvaloniaProperty.RegisterAttached<Control, bool>(
                "Enabled",
                typeof(SliderJumpBehavior));

        private static readonly EventHandler<PointerPressedEventArgs> PressedHandler = OnPressed;
        private static readonly EventHandler<PointerEventArgs> MovedHandler = OnMoved;
        private static readonly EventHandler<PointerReleasedEventArgs> ReleasedHandler = OnReleased;
        private static readonly EventHandler<PointerCaptureLostEventArgs> CaptureLostHandler = OnCaptureLost;

        // Ползунок, который сейчас ведут. Один на всё приложение: указатель один, и
        // вести двумя руками два ползунка разом никто не умеет.
        private static Slider? _dragging;

        static SliderJumpBehavior()
        {
            EnabledProperty.Changed.AddClassHandler<Control>(OnEnabledChanged);
        }

        public static bool GetEnabled(Control element) => element.GetValue(EnabledProperty);
        public static void SetEnabled(Control element, bool value) => element.SetValue(EnabledProperty, value);

        private static void OnEnabledChanged(Control control, AvaloniaPropertyChangedEventArgs e)
        {
            if (control is not Slider slider) return;

            slider.RemoveHandler(InputElement.PointerPressedEvent, PressedHandler);
            slider.RemoveHandler(InputElement.PointerMovedEvent, MovedHandler);
            slider.RemoveHandler(InputElement.PointerReleasedEvent, ReleasedHandler);
            slider.RemoveHandler(InputElement.PointerCaptureLostEvent, CaptureLostHandler);

            if (e.NewValue is not true) return;

            slider.AddHandler(InputElement.PointerPressedEvent, PressedHandler, RoutingStrategies.Tunnel);
            slider.AddHandler(InputElement.PointerMovedEvent, MovedHandler, RoutingStrategies.Tunnel);
            slider.AddHandler(InputElement.PointerReleasedEvent, ReleasedHandler, RoutingStrategies.Tunnel);
            slider.AddHandler(InputElement.PointerCaptureLostEvent, CaptureLostHandler, RoutingStrategies.Tunnel);
        }

        private static void OnPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Slider slider) return;

            var point = e.GetCurrentPoint(slider);
            if (!point.Properties.IsLeftButtonPressed) return;

            // Нажатие по самому бегунку оставляем ему: он и так тащится штатно, а
            // перенос дёрнул бы значение к точке захвата.
            if (e.Source is Visual source
                && source.FindAncestorOfType<Thumb>(includeSelf: true) is not null) return;

            MoveToPoint(slider, point.Position);

            // Захват на сам ползунок: движение сразу продолжает вести значение, без
            // отпускания и повторного захвата за бегунок. Событие помечается
            // разобранным — иначе кнопка дорожки под курсором заберёт указатель себе и
            // начнёт подводить значение шагами.
            _dragging = slider;
            e.Pointer.Capture(slider);
            e.Handled = true;
        }

        private static void OnMoved(object? sender, PointerEventArgs e)
        {
            if (_dragging is null || !ReferenceEquals(_dragging, sender)) return;

            var point = e.GetCurrentPoint(_dragging);
            if (!point.Properties.IsLeftButtonPressed)
            {
                EndDrag(e.Pointer);
                return;
            }

            MoveToPoint(_dragging, point.Position);
            e.Handled = true;
        }

        private static void OnReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_dragging is null || !ReferenceEquals(_dragging, sender)) return;

            EndDrag(e.Pointer);
            e.Handled = true;
        }

        private static void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        {
            if (ReferenceEquals(_dragging, sender)) _dragging = null;
        }

        private static void EndDrag(IPointer pointer)
        {
            pointer.Capture(null);
            _dragging = null;
        }

        /// <summary>Ставит значение по точке в собственных координатах ползунка.</summary>
        private static void MoveToPoint(Slider slider, Point position)
        {
            bool horizontal = slider.Orientation == Avalonia.Layout.Orientation.Horizontal;
            double length = horizontal ? slider.Bounds.Width : slider.Bounds.Height;

            // Бегунок ходит не во всю длину полосы: его центр упирается в половину
            // своей ширины с каждого края. Без этой поправки края недостижимы, а
            // середина сдвинута.
            var thumb = slider.GetVisualDescendants().OfType<Thumb>().FirstOrDefault();
            double thumbLength = thumb is null
                ? 14.0
                : (horizontal ? thumb.Bounds.Width : thumb.Bounds.Height);
            if (thumbLength <= 0) thumbLength = 14.0;

            double usable = length - thumbLength;
            if (usable <= 0) return;

            double pos = horizontal ? position.X : position.Y;
            double t = Math.Clamp((pos - thumbLength / 2) / usable, 0, 1);

            // Тот же разбор направления, что и у самого Slider: вертикальный по
            // умолчанию идёт снизу вверх, а IsDirectionReversed переворачивает ход.
            bool flip = horizontal ? slider.IsDirectionReversed : !slider.IsDirectionReversed;
            if (flip) t = 1 - t;

            slider.SetCurrentValue(RangeBase.ValueProperty,
                slider.Minimum + t * (slider.Maximum - slider.Minimum));
        }
    }
}
