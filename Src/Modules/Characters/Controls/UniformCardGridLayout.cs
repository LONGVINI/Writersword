using System;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Writersword.Modules.Characters.Controls
{
    /// <summary>
    /// Равномерная сетка карточек для ItemsRepeater без виртуализации по
    /// вьюпорту. Все элементы папки реализуются и раскладываются напрямую:
    /// раскладка не читает EffectiveViewport, поэтому несколько репитеров в
    /// общем StackPanel не инвалидируют друг друга по кругу (перекрёстный
    /// цикл measure, найденный телеметрией: папки с одной карточкой
    /// перемерялись по 1300+ раз в секунду). Изменения коллекции не проходят
    /// через нереализованный ClearElementOnDataSourceChange из Avalonia 12
    /// (NotImplementedException при перетаскивании в прокрученном списке) —
    /// выполняется только инвалидация. Элементы не переиспользуются (recycle),
    /// DataContext карточек стабилен, «дыры» и прыжки прокрутки невозможны.
    /// Прогрессивная загрузка вьюмодели (батчи) сохраняет плавное появление
    /// карточек: коллекция растёт постепенно, элементы создаются по мере роста.
    /// Геометрия повторяет UniformGridLayout с ItemsStretch=Fill: число
    /// колонок определяется MinItemWidth и ограничивается MaxColumns, ширина
    /// ячейки = ширина контейнера / число колонок.
    /// </summary>
    public class UniformCardGridLayout : VirtualizingLayout
    {
        public static readonly StyledProperty<double> MinItemWidthProperty =
            AvaloniaProperty.Register<UniformCardGridLayout, double>(nameof(MinItemWidth), 152.0);

        public static readonly StyledProperty<int> MaxColumnsProperty =
            AvaloniaProperty.Register<UniformCardGridLayout, int>(nameof(MaxColumns), 20);

        public double MinItemWidth
        {
            get => GetValue(MinItemWidthProperty);
            set => SetValue(MinItemWidthProperty, value);
        }

        public int MaxColumns
        {
            get => GetValue(MaxColumnsProperty);
            set => SetValue(MaxColumnsProperty, value);
        }

        // Геометрия последнего измерения. Экземпляр раскладки создаётся
        // шаблоном на каждый репитер, поэтому состояние здесь не разделяется
        // между папками.
        private double _itemWidth = 152.0;
        private double _rowHeight;
        private int _columns = 1;

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == MinItemWidthProperty || change.Property == MaxColumnsProperty)
                InvalidateMeasure();
        }

        protected override void OnItemsChangedCore(
            VirtualizingLayoutContext context, object? source, NotifyCollectionChangedEventArgs args)
        {
            InvalidateMeasure();
        }

        protected override Size MeasureOverride(
            VirtualizingLayoutContext context, Size availableSize)
        {
            int count = context.ItemCount;
            if (count == 0)
            {
                _rowHeight = 0;
                return new Size(0, 0);
            }

            double minW = Math.Max(1.0, MinItemWidth);
            double availW = availableSize.Width;
            if (double.IsInfinity(availW) || availW <= 0)
                availW = minW;

            _columns = Math.Max(1, Math.Min(Math.Max(1, MaxColumns), (int)(availW / minW)));
            _itemWidth = availW / _columns;

            // Высота строки едина для всех ячеек — карточки одного шаблона
            // с одинаковыми привязанными размерами.
            // SuppressAutoRecycle обязателен: без него менеджер вьюпорта
            // репитера сам рециклит элементы за пределами видимой области
            // сразу после измерения — карточки превращались в пустые
            // «призраки», а перестановки и анимации ломались. ForceCreate
            // недопустим: он создаёт новый элемент даже при живом
            // существующем, что на каждом проходе плодило дубликаты.
            double rowHeight = 0;
            for (int i = 0; i < count; i++)
            {
                var element = context.GetOrCreateElementAt(
                    i, ElementRealizationOptions.SuppressAutoRecycle);
                element.Measure(new Size(_itemWidth, double.PositiveInfinity));
                if (element.DesiredSize.Height > rowHeight)
                    rowHeight = element.DesiredSize.Height;
            }
            _rowHeight = rowHeight;

            int rows = (count + _columns - 1) / _columns;
            return new Size(availW, rows * rowHeight);
        }

        protected override Size ArrangeOverride(
            VirtualizingLayoutContext context, Size finalSize)
        {
            int count = context.ItemCount;
            if (count == 0) return finalSize;

            for (int i = 0; i < count; i++)
            {
                var element = context.GetOrCreateElementAt(
                    i, ElementRealizationOptions.SuppressAutoRecycle);
                int row = i / _columns;
                int col = i % _columns;
                element.Arrange(new Rect(
                    col * _itemWidth, row * _rowHeight, _itemWidth, _rowHeight));
            }
            return finalSize;
        }
    }
}
