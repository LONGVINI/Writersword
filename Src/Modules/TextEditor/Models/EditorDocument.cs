using System.Collections.Generic;
using Writersword.Modules.TextEditor.Models;

namespace Writersword.Modules.TextEditor.Models
{
    /// <summary>
    /// Документ - полная структура текста с форматированием
    /// </summary>
    public class EditorDocument
    {
        /// <summary>Название документа</summary>
        public string Title { get; set; } = "Untitled";

        /// <summary>Список всех абзацев документа</summary>
        public List<Paragraph> Paragraphs { get; set; } = new();

        /// <summary>Путь к файлу на диске</summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>Флаг несохранённых изменений</summary>
        public bool IsModified { get; set; } = false;
    }
}