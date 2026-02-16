using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReactiveUI;

namespace Writersword.Modules.Notes.ViewModels
{
    /// <summary>
    /// ViewModel для модуля заметок
    /// </summary>
    public class NotesViewModel : ReactiveObject
    {
        private readonly ILogger<NotesViewModel> _logger;
        private string _noteText = string.Empty;

        /// <summary>
        /// Конструктор ViewModel заметок
        /// </summary>
        public NotesViewModel()
        {
            _logger = App.Services.GetService<ILogger<NotesViewModel>>()!;
        }

        /// <summary>
        /// Текст заметки
        /// </summary>
        public string NoteText
        {
            get => _noteText;
            set => this.RaiseAndSetIfChanged(ref _noteText, value);
        }

        /// <summary>
        /// Загрузить текст заметки
        /// Используется при загрузке данных из проекта
        /// НЕ вызывает события изменения для подписчиков
        /// </summary>
        public void LoadNotes(string text)
        {
            _noteText = text ?? string.Empty;
            _logger.LogDebug("Loaded {Length} characters", _noteText.Length);
        }
    }
}