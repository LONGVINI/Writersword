using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace Writersword.Src.Infrastructure.Converters
{
    /// <summary>
    /// Конвертер для привязки RadioButton к String
    /// </summary>
    public class StringToBooleanConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value == null || parameter == null)
                return false;

            return value.ToString() == parameter.ToString();
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool isChecked && isChecked && parameter != null)
            {
                return parameter.ToString();
            }

            return Avalonia.Data.BindingOperations.DoNothing;
        }
    }
}