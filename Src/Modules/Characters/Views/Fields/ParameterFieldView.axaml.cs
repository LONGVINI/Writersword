using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
        public ParameterFieldView()
        {
            InitializeComponent();

            // Поле ввода не выходит за свою колонку: в строке из двух-трёх полей
            // ширина, заданная для поля во всю строку, налезала на соседнее поле,
            // и ручку ширины было уже не достать — она оказывалась под ним.
            ValuePanel.SizeChanged += (_, _) => FitInputs();
            DataContextChanged += (_, _) => Dispatcher.UIThread.Post(FitInputs, DispatcherPriority.Loaded);
        }

        /// <summary>
        /// Ограничить поля ввода местом, которое есть у значения. Ширина, заданная
        /// анкетой, не меняется — поле просто не шире своей колонки.
        /// </summary>
        private void FitInputs()
        {
            double available = ValuePanel.Bounds.Width;
            if (available <= 0) return;

            foreach (var stack in InputRows())
            {
                double max = MaxInputWidthIn(stack, available);
                foreach (var input in Inputs(stack))
                    input.MaxWidth = max;
            }
        }

        /// <summary>Строки с полями ввода и ручкой ширины.</summary>
        private IEnumerable<StackPanel> InputRows() =>
            ValuePanel.GetVisualDescendants()
                .OfType<Border>()
                .Where(b => b.Classes.Contains("width-grip"))
                .Select(b => b.Parent)
                .OfType<StackPanel>()
                .Distinct();

        /// <summary>
        /// Поле ввода в ячейке строки: само поле или обёртка, в которой оно лежит
        /// (у описания под полем ручка высоты).
        /// </summary>
        private static TextBox? InputOf(Control child) => child switch
        {
            TextBox t when t.Classes.Contains("field-input") => t,
            Panel panel => panel.Children.OfType<TextBox>().FirstOrDefault(t => t.Classes.Contains("field-input")),
            _ => null
        };

        private static IEnumerable<TextBox> Inputs(StackPanel stack) =>
            stack.Children
                .Where(c => c.IsVisible)
                .Select(InputOf)
                .Where(t => t is { IsVisible: true })
                .Select(t => t!);

        /// <summary>
        /// Наибольшая ширина одного поля ввода строки: место значения минус
        /// подписи, ручка и промежутки, поровну на видимые поля ввода (у числа
        /// «от — до» их два).
        /// </summary>
        private static double MaxInputWidthIn(StackPanel stack, double available)
        {
            var visible = stack.Children.Where(c => c.IsVisible).ToList();
            var inputs = visible.Where(c => InputOf(c) is { IsVisible: true }).ToList();
            if (inputs.Count == 0) return double.PositiveInfinity;

            double extras = visible.Except(inputs).Sum(c => c.DesiredSize.Width)
                            + stack.Spacing * Math.Max(0, visible.Count - 1);

            double each = (available - extras) / inputs.Count;
            return Math.Max(CharacterParameterItemViewModel.MinInputWidth, each);
        }

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

            // Отсчёт — от ширины, которая видна, а не от заданной анкетой: поле,
            // ужатое колонкой, иначе не уменьшалось бы, пока ручку не протащат
            // на всю разницу.
            var shown = grip.Parent is StackPanel stack
                ? Inputs(stack).Select(t => t.Bounds.Width).DefaultIfEmpty(0).Max()
                : 0;
            _gripStartWidth = shown > 0 ? Math.Min(shown, vm.InputWidth) : vm.InputWidth;

            e.Pointer.Capture(grip);
            e.Handled = true;
        }

        private void OnWidthGripMoved(object? sender, PointerEventArgs e)
        {
            if (!_gripDragging || DataContext is not CharacterParameterItemViewModel vm) return;

            var width = _gripStartWidth + (e.GetPosition(this).X - _gripStartX);

            // Шире своей колонки поле не тянется: дальше — соседнее поле строки.
            if (sender is Control grip && grip.Parent is StackPanel stack)
                width = Math.Min(width, MaxInputWidthIn(stack, ValuePanel.Bounds.Width));

            vm.InputWidth = width;
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

        /// <summary>
        /// Захват указателя пропал без отпускания (вью перестроилась, окно потеряло
        /// фокус). Ширина, до которой дотянули, всё равно записывается в анкету —
        /// иначе она оставалась на экране, но мимо анкеты и мимо Ctrl+Z.
        /// </summary>
        private void OnWidthGripCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        {
            if (!_gripDragging) return;

            _gripDragging = false;
            (DataContext as CharacterParameterItemViewModel)?.CommitInputWidth();
        }

        // ── Высота описания ──────────────────────────────────────────────
        //
        // Так же, как ширина: своей обработкой указателя, от точки нажатия.

        private double _heightGripStartY;
        private double _heightGripStartHeight;
        private bool _heightGripDragging;

        private void OnHeightGripPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Control grip || DataContext is not CharacterParameterItemViewModel vm) return;
            if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;

            _heightGripDragging = true;
            _heightGripStartY = e.GetPosition(this).Y;

            // Отсчёт — от высоты, которая видна: описание без заданной высоты
            // выросло по тексту, и ручка должна начинать с неё.
            var shown = grip.Parent is Panel panel
                ? panel.Children.OfType<TextBox>().Select(t => t.Bounds.Height).DefaultIfEmpty(0).Max()
                : 0;
            _heightGripStartHeight = shown > 0 ? shown : vm.InputHeight;

            e.Pointer.Capture(grip);
            e.Handled = true;
        }

        private void OnHeightGripMoved(object? sender, PointerEventArgs e)
        {
            if (!_heightGripDragging || DataContext is not CharacterParameterItemViewModel vm) return;

            vm.InputHeight = _heightGripStartHeight + (e.GetPosition(this).Y - _heightGripStartY);
            e.Handled = true;
        }

        private void OnHeightGripReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (!_heightGripDragging) return;

            _heightGripDragging = false;
            e.Pointer.Capture(null);
            (DataContext as CharacterParameterItemViewModel)?.CommitInputHeight();
            e.Handled = true;
        }

        private void OnHeightGripCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        {
            if (!_heightGripDragging) return;

            _heightGripDragging = false;
            (DataContext as CharacterParameterItemViewModel)?.CommitInputHeight();
        }
    }
}
