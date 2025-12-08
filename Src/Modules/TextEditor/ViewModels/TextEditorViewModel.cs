using ReactiveUI;
using System.Collections.Generic;
using Writersword.Modules.TextEditor.Models;

namespace Writersword.Modules.TextEditor.ViewModels
{
    /// <summary>
    /// ViewModel для текстового редактора
    /// Управляет документом и его содержимым
    /// </summary>
    public class TextEditorViewModel : ReactiveObject
    {
        private EditorDocument _document;
        private string _plainText = string.Empty; // Для простого TextBox (пока без форматирования)

        /// <summary>Простой текст для отображения в TextBox</summary>
        public string PlainText
        {
            get => _plainText;
            set
            {
                this.RaiseAndSetIfChanged(ref _plainText, value);
                _document.IsModified = true; // Отмечаем что документ изменён
            }
        }

        /// <summary>Название документа</summary>
        public string Title => _document.Title;

        /// <summary>Есть несохранённые изменения</summary>
        public bool IsModified => _document.IsModified;

        public TextEditorViewModel()
        {
            _document = new EditorDocument
            {
                Title = "Untitled"
            };
        }

        /// <summary>
        /// Создать новый документ
        /// </summary>
        public void NewDocument()
        {
            _document = new EditorDocument();
            PlainText = string.Empty;
        }

        /// <summary>
        /// Получить модель документа для сохранения
        /// </summary>
        public EditorDocument GetDocument()
        {
            // Пока храним как простой текст в одном параграфе
            _document.Paragraphs.Clear();
            _document.Paragraphs.Add(new Paragraph
            {
                Fragments = new()
                {
                    new TextFragment { Text = PlainText }
                }
            });

            return _document;
        }
    }
}