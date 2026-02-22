namespace Writersword.Src.Modules.TextEditor.Models
{
    /// <summary>
    /// Настройки модуля текстового редактора
    /// Используется как для глобальных так и для локальных настроек проекта
    /// </summary>
    public class TextEditorSettings
    {
        /// <summary>Размер шрифта по умолчанию</summary>
        public double FontSize { get; set; } = 14;

        /// <summary>Семейство шрифта по умолчанию</summary>
        public string FontFamily { get; set; } = "Times New Roman";
    }
}