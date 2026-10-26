using Avalonia;
using Avalonia.Controls;
using System;
using System.Collections.Generic;

namespace Writersword.Modules.Characters.Controls
{
    /// <summary>
    /// Плитки рядами во всю ширину. Колонок столько, сколько влезает
    /// плиток не уже MinItemWidth, и ширина делится между ними поровну — справа
    /// не остаётся дыры, как у обычного WrapPanel с плитками постоянной
    /// ширины. Высота ряда — по самой высокой плитке в нём, плитки прижаты
    /// к верху ряда.
    ///
    /// Дети нулевого размера места не занимают: так ведут себя блоки,
    /// спрятанные поиском (контейнер остаётся, а содержимое невидимо).
    /// </summary>
    public class StretchWrapPanel : Panel
    {
        public static readonly StyledProperty<double> MinItemWidthProperty =
            AvaloniaProperty.Register<StretchWrapPanel, double>(nameof(MinItemWidth), 380);

        static StretchWrapPanel()
        {
            AffectsMeasure<StretchWrapPanel>(MinItemWidthProperty);
        }

        /// <summary>Самая узкая плитка: уже ряд не ужимается, лишняя колонка уходит.</summary>
        public double MinItemWidth
        {
            get => GetValue(MinItemWidthProperty);
            set => SetValue(MinItemWidthProperty, value);
        }

        private int ColumnsFor(double width)
        {
            var min = Math.Max(1, MinItemWidth);
            if (double.IsInfinity(width) || double.IsNaN(width)) return int.MaxValue;
            return Math.Max(1, (int)Math.Floor(width / min));
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var columns = ColumnsFor(availableSize.Width);
            var itemWidth = columns == int.MaxValue ? MinItemWidth : availableSize.Width / columns;

            double total = 0, rowHeight = 0, widest = 0;
            int column = 0;

            foreach (var child in Children)
            {
                child.Measure(new Size(itemWidth, double.PositiveInfinity));
                if (IsEmpty(child)) continue;

                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
                column++;
                widest = Math.Max(widest, column * itemWidth);

                if (column >= columns)
                {
                    total += rowHeight;
                    rowHeight = 0;
                    column = 0;
                }
            }

            total += rowHeight;

            var width = double.IsInfinity(availableSize.Width) ? widest : availableSize.Width;
            return new Size(width, total);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var columns = ColumnsFor(finalSize.Width);
            var itemWidth = columns == int.MaxValue ? MinItemWidth : finalSize.Width / columns;

            var row = new List<Control>();
            double y = 0;

            void FlushRow()
            {
                double height = 0;
                foreach (var c in row) height = Math.Max(height, c.DesiredSize.Height);

                for (int i = 0; i < row.Count; i++)
                    row[i].Arrange(new Rect(i * itemWidth, y, itemWidth, row[i].DesiredSize.Height));

                y += height;
                row.Clear();
            }

            foreach (var child in Children)
            {
                if (IsEmpty(child))
                {
                    child.Arrange(new Rect(0, y, 0, 0));
                    continue;
                }

                row.Add(child);
                if (row.Count >= columns) FlushRow();
            }

            if (row.Count > 0) FlushRow();

            return finalSize;
        }

        private static bool IsEmpty(Control child) =>
            !child.IsVisible || (child.DesiredSize.Width <= 0 && child.DesiredSize.Height <= 0);
    }
}
