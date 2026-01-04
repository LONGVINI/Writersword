using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace Writersword.Converters
{
    /// <summary>
    /// Конвертер для преобразования bool в цвет текста/иконки
    /// Используется для подсветки активного WorkMode
    /// </summary>
    public class BoolToForegroundConverter : IValueConverter
    {
        /// <summary>
        /// Преобразование bool → Brush
        /// true → #FFFFFF (активный - белый)
        /// false → #999999 (неактивный - серый)
        /// </summary>
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool isActive)
            {
                // Активный элемент - белый текст
                if (isActive)
                    return new SolidColorBrush(Color.Parse("#FFFFFF"));

                // Неактивный элемент - серый текст
                return new SolidColorBrush(Color.Parse("#999999"));
            }

            // Значение по умолчанию
            return new SolidColorBrush(Color.Parse("#999999"));
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