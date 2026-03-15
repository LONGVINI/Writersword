using Avalonia.Data.Converters;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Writersword.Infrastructure.Converters
{
    /// <summary>
    /// Конвертер строки горячих клавиш в список групп для отображения.
    /// Формат входной строки:
    ///   "Ctrl+L | Ctrl+A"        — одна комбинация из двух клавиш через |
    ///   "Ctrl+1 ;; Ctrl+Shift+P" — две разные комбинации через ;;
    /// Результат — список списков строк:
    ///   [["Ctrl", "L", "Ctrl", "A"], ["Ctrl", "Shift", "P"]]
    /// </summary>
    public class HotKeyConverter : IValueConverter
    {
        /// <summary>Глобальный экземпляр конвертера для использования в XAML.</summary>
        public static readonly HotKeyConverter Instance = new();

        /// <summary>
        /// Преобразует строку горячих клавиш в список групп клавиш.
        /// Каждая группа — одна комбинация, внутри группы — отдельные клавиши.
        /// </summary>
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not string raw || string.IsNullOrWhiteSpace(raw))
                return null;

            var combinations = raw.Split(new[] { ";;" }, StringSplitOptions.RemoveEmptyEntries);
            var result = new List<List<string>>();

            foreach (var combination in combinations)
            {
                var keys = combination.Trim().Split(
                    new[] { "|", "+" }, StringSplitOptions.RemoveEmptyEntries);

                var keyList = new List<string>();
                foreach (var key in keys)
                {
                    string trimmed = key.Trim();
                    if (!string.IsNullOrEmpty(trimmed))
                        keyList.Add(trimmed);
                }

                if (keyList.Count > 0)
                    result.Add(keyList);
            }

            return result.Count > 0 ? result : null;
        }

        /// <summary>Обратное преобразование не используется.</summary>
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}