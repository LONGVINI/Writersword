using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace Writersword.Infrastructure.Converters
{
    /// <summary>
    /// Растягивает элемент на две колонки сетки, когда условие истинно, и
    /// оставляет в одной, когда ложно.
    ///
    /// Нужен там, где соседняя колонка в одном из режимов пустует: занять её
    /// объединением — единственный способ встать по центру строки, не завися
    /// от того, сколько места осталось соседям. Подобранные вручную отступы
    /// такой независимости не дают: их приходится пересчитывать при каждой
    /// смене ширины панели или полосы прокрутки.
    /// </summary>
    public sealed class BoolToColumnSpanConverter : IValueConverter
    {
        public static readonly BoolToColumnSpanConverter Instance = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is true ? 2 : 1;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => Avalonia.Data.BindingOperations.DoNothing;
    }
}
