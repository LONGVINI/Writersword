using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace Writersword.Converters
{
    /// <summary>Конвертер bool в строку "active" для Tag</summary>
    public class BoolToActiveTagConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool isActive && isActive)
                return "active";
            return null;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}