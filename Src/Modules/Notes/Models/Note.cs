namespace Writersword.Modules.Notes.Models
{
    /// <summary>
    /// Модель заметки
    /// Пока содержит только текст
    /// </summary>
    public class Note
    {
        /// <summary>Текст заметки</summary>
        public string Text { get; set; } = string.Empty;
    }
}