using System.Globalization;

namespace Writersword.Modules.Characters.Models
{
    /// <summary>
    /// Вычисление символа-заглушки для аватара без фото.
    /// Явно заданная иконка имеет приоритет; «?» считается незаданной —
    /// это историческое значение по умолчанию в модели персонажа.
    /// Без иконки берётся первая буква имени в верхнем регистре
    /// (текстовый элемент целиком, поэтому эмодзи и суррогатные пары
    /// в имени не режутся); для пустого имени остаётся «?».
    /// </summary>
    public static class CharacterGlyph
    {
        public static string Resolve(string? fallbackIcon, string? name)
        {
            if (!string.IsNullOrWhiteSpace(fallbackIcon) && fallbackIcon.Trim() != "?")
                return fallbackIcon.Trim();

            var trimmedName = name?.Trim();
            if (string.IsNullOrEmpty(trimmedName)) return "?";

            var first = StringInfo.GetNextTextElement(trimmedName);
            return first.ToUpper(CultureInfo.CurrentCulture);
        }
    }
}
