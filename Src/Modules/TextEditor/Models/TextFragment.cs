namespace Writersword.Modules.TextEditor.Models
{
    /// <summary>
    /// Фрагмент текста с единым форматированием
    /// Например: "обычный текст ЖИРНЫЙ текст" - это 2 фрагмента
    /// </summary>
    public class TextFragment
    {
        /// <summary>Текст фрагмента</summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>Стиль форматирования (шрифт, размер, цвет)</summary>
        public TextStyle Style { get; set; } = new();
    }
}
