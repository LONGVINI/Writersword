using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Writersword.Modules.Characters.ViewModels.Tabs;

namespace Writersword.Modules.Characters.Views.Fields
{
    /// <summary>
    /// Поле анкеты: название, значение в виде, заданном анкетой, примечание и
    /// отметка «не относится». Общее для карточки персонажа и конструктора
    /// анкет; в конструкторе у полей ввода есть ручка ширины.
    /// </summary>
    public partial class ParameterFieldView : UserControl
    {
        public ParameterFieldView() => InitializeComponent();

        // Ширина ведётся своей обработкой указателя, а не Thumb: штатное
        // перетаскивание в этой сборке ненадёжно (ползунки из-за него стояли).
        // Считается от точки нажатия, а не от шагов, — поле не «плывёт».
        private double _gripStartX;
        private double _gripStartWidth;
        private bool _gripDragging;

        private void OnWidthGripPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Control grip || DataContext is not CharacterParameterItemViewModel vm) return;
            if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;

            _gripDragging = true;
            _gripStartX = e.GetPosition(this).X;
            _gripStartWidth = vm.InputWidth;
            e.Pointer.Capture(grip);
            e.Handled = true;
        }

        private void OnWidthGripMoved(object? sender, PointerEventArgs e)
        {
            if (!_gripDragging || DataContext is not CharacterParameterItemViewModel vm) return;

            vm.InputWidth = _gripStartWidth + (e.GetPosition(this).X - _gripStartX);
            e.Handled = true;
        }

        /// <summary>Стрелки вверх и вниз в числе — шаг по правилу анкеты.</summary>
        private void OnNumberKeyDown(object? sender, KeyEventArgs e)
        {
            if (sender is not TextBox box || DataContext is not CharacterParameterItemViewModel vm) return;

            var direction = e.Key switch
            {
                Key.Up => 1,
                Key.Down => -1,
                _ => 0
            };

            if (e.Key == Key.Enter)
            {
                vm.CommitNumber(Equals(box.Tag, "to"));
                e.Handled = true;
                return;
            }

            if (direction == 0) return;

            vm.StepNumber(direction, Equals(box.Tag, "to"));
            box.CaretIndex = box.Text?.Length ?? 0;
            e.Handled = true;
        }

        /// <summary>Поле числа отпустили — вписанное приводится к пределам и шагу.</summary>
        private void OnNumberLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (sender is TextBox box && DataContext is CharacterParameterItemViewModel vm)
                vm.CommitNumber(Equals(box.Tag, "to"));
        }

        private void OnWidthGripReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (!_gripDragging) return;

            _gripDragging = false;
            e.Pointer.Capture(null);
            (DataContext as CharacterParameterItemViewModel)?.CommitInputWidth();
            e.Handled = true;
        }
    }
}
