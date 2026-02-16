using Microsoft.Extensions.Logging;
using Writersword.Src.Modules.Notes.Models;

namespace Writersword.Src.Modules.Notes.Services
{
    /// <summary>
    /// Сервис для работы с заметками
    /// Пока минимальная реализация
    /// </summary>
    public class NotesService
    {
        private readonly ILogger<NotesService> _logger;

        /// <summary>
        /// Конструктор сервиса заметок
        /// </summary>
        public NotesService(ILogger<NotesService> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Создать новую пустую заметку
        /// </summary>
        public Note CreateNote()
        {
            _logger.LogDebug("Created new note");
            return new Note();
        }

        /// <summary>
        /// Проверить валидность заметки
        /// </summary>
        public bool IsValid(Note note)
        {
            return note != null;
        }
    }
}