using System;
using System.ComponentModel;
using ReactiveUI;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.ViewModels.Blocks
{
    /// <summary>
    /// ViewModel одного параграфа.
    /// Хранит ссылку на модель и предоставляет свойства для привязки в View.
    /// </summary>
    public sealed class ParagraphViewModel : ReactiveObject
    {
        private readonly ParagraphBlock _model;
        private string _plainText;
        private bool _isSelected;
        private bool _isFocused;

        public Guid BlockId => _model.Id;
        public ParagraphBlock Model => _model;

        public bool IsSelected
        {
            get => _isSelected;
            set => this.RaiseAndSetIfChanged(ref _isSelected, value);
        }

        public bool IsFocused
        {
            get => _isFocused;
            set => this.RaiseAndSetIfChanged(ref _isFocused, value);
        }

        /// <summary>
        /// Текущая позиция начала выделения в TextBox.
        /// Синхронизируется View через OnSelectionChanged.
        /// </summary>
        public int SelectionStart { get; set; }

        /// <summary>
        /// Текущая позиция конца выделения в TextBox.
        /// Синхронизируется View через OnSelectionChanged.
        /// </summary>
        public int SelectionEnd { get; set; }

        /// <summary>
        /// Текст параграфа для двусторонней привязки к TextBox.
        /// При изменении обновляет модель.
        /// </summary>
        public string PlainText
        {
            get => _plainText;
            set
            {
                this.RaiseAndSetIfChanged(ref _plainText, value);
                _model.SetPlainText(value);
            }
        }

        /// <summary>
        /// Событие запроса фокуса на этот параграф.
        /// EditorParagraphView подписывается и фокусирует свой TextBox.
        /// </summary>
        public event Action? FocusRequested;

        /// <summary>Запросить фокус на этот параграф.</summary>
        public void RequestFocus() => FocusRequested?.Invoke();

        /// <summary>
        /// Делегат фокуса с установкой каретки на конкретную позицию.
        /// Используется после мержа параграфов чтобы каретка встала в точку слияния.
        /// </summary>
        public Action<int>? RequestFocusAtPosition { get; set; }

        /// <summary>Команда добавления нового параграфа после текущего (Enter).</summary>
        public Func<ParagraphViewModel, ParagraphViewModel>? RequestAddAfter { get; set; }

        /// <summary>Команда удаления текущего параграфа (Backspace на пустом).</summary>
        public Action<ParagraphViewModel>? RequestDelete { get; set; }

        /// <summary>
        /// Команда слияния текущего параграфа с предыдущим.
        /// Вызывается при Backspace в позиции 0.
        /// </summary>
        public Action<ParagraphViewModel, string>? RequestMergeWithPrevious { get; set; }

        /// <summary>
        /// Делегат уведомления DocumentViewModel об активации этого параграфа.
        /// Вызывается из View при получении фокуса TextBox.
        /// </summary>
        public Action<ParagraphViewModel>? OnActivated { get; set; }

        /// <summary>
        /// Делегат уведомления об изменении выделения в TextBox.
        /// Вызывается из View при SelectionChanged.
        /// </summary>
        public Action<ParagraphViewModel>? OnSelectionChanged { get; set; }

        /// <summary>
        /// Делегат выделения всего документа (Ctrl+A).
        /// </summary>
        public Action? RequestSelectAll { get; set; }

        /// <summary>
        /// Делегат снятия выделения со всего документа.
        /// </summary>
        public Action? RequestClearSelection { get; set; }

        /// <summary>
        /// Делегат получения текста всех выделенных блоков.
        /// Возвращает null если нет document-level выделения.
        /// </summary>
        public Func<string?>? RequestGetDocumentSelectedText { get; set; }

        public ParagraphViewModel(ParagraphBlock model)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _plainText = model.GetPlainText();
        }

        /// <summary>
        /// Удаляет все параграфы с IsSelected = true.
        /// Вызывается при Delete/Backspace/Ctrl+X когда есть document-level выделение.
        /// </summary>
        public Action? RequestDeleteSelected { get; set; }
    }
}
