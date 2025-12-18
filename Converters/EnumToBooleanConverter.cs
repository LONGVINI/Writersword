using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace Writersword.Converters
{
    /// <summary>
    /// Конвертер для привязки RadioButton к Enum
    /// </summary>
    public class EnumToBooleanConverter : IValueConverter
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
                return Enum.Parse(targetType, parameter.ToString()!);
            }

            return Avalonia.Data.BindingOperations.DoNothing;
        }
    }
}