using System.Collections.Generic;
using System.Linq;

namespace Writersword.Styles.UserControls
{
    /// <summary>
    /// Захардкоженные стандартные цвета. Используются как начальное заполнение
    /// глобального списка и для кнопки «Сбросить».
    /// </summary>
    public static class StandardColors
    {
        /// <summary>Максимум стандартных цветов.</summary>
        public const int MaxCount = 24;

        private static readonly string[] _default =
        {
            "#F44336", "#E91E63", "#9C27B0", "#673AB7", "#3F51B5", "#2196F3",
            "#03A9F4", "#00BCD4", "#009688", "#4CAF50", "#8BC34A", "#FFEB3B",
            "#FFC107", "#FF9800", "#FF5722", "#795548", "#607D8B", "#9E9E9E",
            "#455A64", "#E07B39", "#37474F", "#212121", "#FFFFFF", "#BDBDBD"
        };

        /// <summary>Новая копия списка стандартных цветов.</summary>
        public static List<string> Default() => _default.ToList();
    }
}
