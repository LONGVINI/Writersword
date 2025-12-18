using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace Writersword.Converters
{
    /// <summary>
    /// Конвертер: bool → Color для кнопок WorkMode
    /// true = #007ACC (синий), false = #3E3E42 (серый)
    /// </summary>
    public class BoolToColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool isActive && isActive)
            {
                return Color.Parse("#007ACC"); // Синий для активной
            }
            return Color.Parse("#3E3E42"); // Серый для неактивной
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}