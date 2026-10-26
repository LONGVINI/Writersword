using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;

namespace Writersword.Modules.Characters.Views.Fields
{
    /// <summary>
    /// Шрифты системы для выбора в особом виде анкеты и поля. Список
    /// собирается один раз при первом обращении.
    /// </summary>
    public static class FontChoices
    {
        private static readonly Lazy<IReadOnlyList<string>> _names = new(() =>
        {
            try
            {
                return FontManager.Current.SystemFonts
                    .Select(f => f.Name)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }
        });

        /// <summary>Названия шрифтов системы по алфавиту.</summary>
        public static IReadOnlyList<string> Names => _names.Value;
    }
}
