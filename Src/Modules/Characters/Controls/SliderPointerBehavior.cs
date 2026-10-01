using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Writersword.Modules.Characters.Controls
{
    /// <summary>
    /// Перетаскивание штатного ползунка мышью.
    ///
    /// В этой сборке штатный Slider за бегунок не тащится: нажимается, сереет
    /// и стоит на месте. Редактор цвета обходит это своими обработчиками
    /// (ColorEditorOverlay.EnableSliderJump); здесь то же решение, но
    /// подключаемое одним свойством из разметки. Нажатие в любом месте
    /// ползунка — на полосе или на бегунке — ставит значение по точке под
    /// курсором и захватывает мышь: пока кнопка зажата, значение идёт за
    /// курсором. Считается всегда от точки, а не от шагов перетаскивания,
    /// поэтому шаг шкалы ползунку не мешает.
    /// </summary>
    public static class SliderPointerBehavior
    {
        public static readonly AttachedProperty<bool> IsEnabledProperty =
            AvaloniaProperty.RegisterAttached<Slider, bool>("IsEnabled", typeof(SliderPointerBehavior));

        static SliderPointerBehavior()
        {
            IsEnabledProperty.Changed.AddClassHandler<Slider>(OnIsEnabledChanged);
        }

        public static bool GetIsEnabled(Slider slider) => slider.GetValue(IsEnabledProperty);
        public static void SetIsEnabled(Slider slider, bool value) => slider.SetValue(IsEnabledProperty, value);

        private static readonly AttachedProperty<bool> IsDraggingProperty =
            AvaloniaProperty.RegisterAttached<Slider, bool>("IsDragging", typeof(SliderPointerBehavior));

        private static void OnIsEnabledChanged(Slider slider, AvaloniaPropertyChangedEventArgs e)
        {
            slider.RemoveHandler(InputElement.PointerPressedEvent, OnPressed);
            slider.RemoveHandler(InputElement.PointerMovedEvent, OnMoved);
            slider.RemoveHandler(InputElement.PointerReleasedEvent, OnReleased);
            slider.RemoveHandler(InputElement.PointerCaptureLostEvent, OnCaptureLost);

            if (e.NewValue is not true) return;

            // Туннелем и с разобранными тоже: событие должно дойти сюда раньше,
            // чем его заберут бегунок и кнопки полосы.
            slider.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, true);
            slider.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, true);
            slider.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel, true);
            // Потеря захвата идёт прямо элементу, туннеля у неё нет.
            slider.AddHandler(InputElement.PointerCaptureLostEvent, OnCaptureLost);
        }

        private static void OnPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Slider slider || !slider.IsEnabled) return;

            var point = e.GetCurrentPoint(slider);
            if (!point.Properties.IsLeftButtonPressed) return;

            slider.SetValue(IsDraggingProperty, true);
            MoveToPoint(slider, point.Position);

            // Мышь захватывается на сам ползунок, а событие помечается
            // разобранным: иначе бегунок или кнопка полосы под курсором заберут
            // мышь себе, и значение перестанет идти за ней.
            e.Pointer.Capture(slider);
            slider.Focus();
            e.Handled = true;
        }

        private static void OnMoved(object? sender, PointerEventArgs e)
        {
            if (sender is not Slider slider || !slider.GetValue(IsDraggingProperty)) return;

            var point = e.GetCurrentPoint(slider);
            if (!point.Properties.IsLeftButtonPressed)
            {
                EndDrag(slider, e.Pointer);
                return;
            }

            MoveToPoint(slider, point.Position);
            e.Handled = true;
        }

        private static void OnReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (sender is not Slider slider || !slider.GetValue(IsDraggingProperty)) return;

            EndDrag(slider, e.Pointer);
            e.Handled = true;
        }

        private static void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        {
            if (sender is Slider slider)
                slider.SetValue(IsDraggingProperty, false);
        }

        private static void EndDrag(Slider slider, IPointer pointer)
        {
            slider.SetValue(IsDraggingProperty, false);
            pointer.Capture(null);
        }

        /// <summary>Поставить значение по точке в координатах ползунка.</summary>
        private static void MoveToPoint(Slider slider, Point position)
        {
            var horizontal = slider.Orientation == Avalonia.Layout.Orientation.Horizontal;

            // Ход бегунка меряется по полосе, а не по всему ползунку: у шаблона
            // вокруг полосы бывают поля.
            Visual track = (Visual?)slider.GetVisualDescendants().OfType<Track>().FirstOrDefault() ?? slider;
            var origin = track.TranslatePoint(new Point(0, 0), slider) ?? new Point(0, 0);

            var length = horizontal ? track.Bounds.Width : track.Bounds.Height;

            var thumb = slider.GetVisualDescendants().OfType<Thumb>().FirstOrDefault();
            var thumbLength = thumb is null ? 0 : (horizontal ? thumb.Bounds.Width : thumb.Bounds.Height);

            var usable = length - thumbLength;
            if (usable <= 0) return;

            var pos = horizontal ? position.X - origin.X : position.Y - origin.Y;
            var t = Math.Clamp((pos - thumbLength / 2) / usable, 0, 1);

            var flip = horizontal ? slider.IsDirectionReversed : !slider.IsDirectionReversed;
            if (flip) t = 1 - t;

            var value = slider.Minimum + t * (slider.Maximum - slider.Minimum);

            // Шаг ползунка — его SmallChange: значение округляется до него,
            // чтобы у шкалы с шагом не появлялись дробные числа.
            var step = slider.SmallChange;
            if (step > 0)
                value = slider.Minimum + Math.Round((value - slider.Minimum) / step) * step;

            value = Math.Clamp(value, slider.Minimum, Math.Max(slider.Minimum, slider.Maximum));

            if (Math.Abs(value - slider.Value) > double.Epsilon)
                slider.SetCurrentValue(RangeBase.ValueProperty, value);
        }
    }
}
