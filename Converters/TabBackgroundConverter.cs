// Создай файл Converters/TabConverters.cs

using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace Writersword.Converters
{
    public class TabBackgroundConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool isActive && isActive)
                return new SolidColorBrush(Color.Parse("#1E1E1E"));
            return new SolidColorBrush(Color.Parse("#2D2D30"));
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class TabForegroundConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool isActive && isActive)
                return new SolidColorBrush(Colors.White);
            return new SolidColorBrush(Color.Parse("#969696"));
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}