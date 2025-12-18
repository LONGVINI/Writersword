using ReactiveUI;

namespace Writersword.Modules.Notes.ViewModels
{
    /// <summary>
    /// ViewModel для модуля заметок
    /// Управляет текстом заметок
    /// </summary>
    public class NotesViewModel : ReactiveObject
    {
        /// <summary>Текст заметок</summary>
        private string _noteText = string.Empty;

        /// <summary>
        /// Текст заметок (привязан к TextBox)
        /// При изменении автоматически уведомляет UI
        /// </summary>
        public string NoteText
        {
            get => _noteText;
            set => this.RaiseAndSetIfChanged(ref _noteText, value);
        }

        /// <summary>
        /// Конструктор - задаём текст по умолчанию
        /// </summary>
        public NotesViewModel()
        {
            NoteText = "Быстрые заметки...";
        }
    }
}