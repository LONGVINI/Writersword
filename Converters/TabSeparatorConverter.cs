using Avalonia.Data.Converters;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Writersword.ViewModels;
using Writersword.ViewModels.Components;

namespace Writersword.Converters
{
    /// <summary>
    /// Конвертер для определения видимости разделителя между вкладками
    /// Логика как в Chrome:
    /// - Разделитель показывается между двумя неактивными вкладками
    /// - Разделитель НЕ показывается если хотя бы одна из соседних вкладок активна
    /// - Разделитель перед "+" показывается только если последняя вкладка неактивна
    /// </summary>
    public class TabSeparatorConverter : IMultiValueConverter
    {
        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            // Проверяем параметр - если "LastTab", то это разделитель перед кнопкой "+"
            if (parameter is string param && param == "LastTab")
            {
                // values[0] = Tabs (ObservableCollection)
                // values[1] = ActiveTab

                if (values[0] is not IEnumerable<DocumentTabViewModel> tabs ||
                    values[1] is not DocumentTabViewModel active)
                    return false;

                var lastTab = tabs.LastOrDefault();
                if (lastTab == null)
                    return false;

                // Показываем разделитель только если последняя вкладка НЕ активна
                return lastTab != active;
            }

            // Обычный разделитель между вкладками
            // values[0] = текущая вкладка (DocumentTabViewModel)
            // values[1] = все вкладки (ObservableCollection)
            // values[2] = активная вкладка (DocumentTabViewModel)

            if (values[0] is not DocumentTabViewModel currentTab ||
                values[1] is not IEnumerable<DocumentTabViewModel> allTabs ||
                values[2] is not DocumentTabViewModel activeTab)
                return false;

            var tabsList = allTabs.ToList();
            var currentIndex = tabsList.IndexOf(currentTab);

            if (currentIndex == -1)
                return false;

            // Если текущая вкладка активна - разделитель не нужен
            if (currentTab == activeTab)
                return false;

            // Если это последняя вкладка - разделитель не нужен (будет перед "+")
            if (currentIndex == tabsList.Count - 1)
                return false;

            // Проверяем следующую вкладку
            var nextTab = tabsList[currentIndex + 1];

            // Показываем разделитель только если следующая вкладка тоже НЕ активна
            return nextTab != activeTab;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}