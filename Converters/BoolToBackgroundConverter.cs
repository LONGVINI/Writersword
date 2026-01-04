using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace Writersword.Converters
{
    /// <summary>
    /// Конвертер для преобразования bool в цвет фона
    /// Используется для подсветки активной вкладки
    /// </summary>
    public class BoolToBackgroundConverter : IValueConverter
    {
        /// <summary>
        /// Преобразование bool → Brush
        /// true → #3E3E42 (активная вкладка - светлее)
        /// false → #2D2D30 (неактивная вкладка - темнее)
        /// </summary>
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool isActive)
            {
                // Активная вкладка - светлый фон
                if (isActive)
                    return new SolidColorBrush(Color.Parse("#3E3E42"));

                // Неактивная вкладка - тёмный фон
                return new SolidColorBrush(Color.Parse("#2D2D30"));
            }

            // Значение по умолчанию
            return new SolidColorBrush(Color.Parse("#2D2D30"));
        }

        /// <summary>
        /// Обратное преобразование (не используется)
        /// </summary>
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}