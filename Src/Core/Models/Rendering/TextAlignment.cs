namespace Writersword.Core.Models.Rendering
{
    /// <summary>
    /// Выравнивание текста — общая модель уровня приложения.
    /// Значения совпадают с Writersword.Modules.TextEditor.Models.Styles.TextAlignment
    /// для безопасного приведения типов через (int).
    /// </summary>
    public enum TextAlignment
    {
        Left = 0,
        Center = 1,
        Right = 2,
        Justify = 3
    }
}