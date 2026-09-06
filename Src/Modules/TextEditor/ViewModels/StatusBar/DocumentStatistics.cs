namespace Writersword.Modules.TextEditor.ViewModels.StatusBar
{
    /// <summary>
    /// Снимок статистики документа для окна «Статистика». Собирается из строки
    /// состояния в момент открытия окна: считать те же величины заново окно не может —
    /// число страниц и строк известно только раскладке.
    /// </summary>
    public sealed record DocumentStatistics(
        int Pages,
        int Words,
        int CharsNoSpaces,
        int CharsWithSpaces,
        int Paragraphs,
        int Lines);
}
