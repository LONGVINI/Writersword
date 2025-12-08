namespace Writersword.Modules.TextEditor.Models
{
    /// <summary>
    /// Стиль форматирования текста
    /// </summary>
    public class TextStyle
    {
        /// <summary>Название шрифта (Arial, Times New Roman и т.д.)</summary>
        public string FontFamily { get; set; } = "Arial";

        /// <summary>Размер шрифта</summary>
        public double FontSize { get; set; } = 14;

        /// <summary>Жирный текст</summary>
        public bool IsBold { get; set; } = false;

        /// <summary>Курсив</summary>
        public bool IsItalic { get; set; } = false;

        /// <summary>Подчёркивание</summary>
        public bool IsUnderline { get; set; } = false;

        /// <summary>Цвет текста (HEX формат, например #000000)</summary>
        public string TextColor { get; set; } = "#000000";

        /// <summary>Цвет фона текста (HEX или transparent)</summary>
        public string BackgroundColor { get; set; } = "transparent";
    }
}