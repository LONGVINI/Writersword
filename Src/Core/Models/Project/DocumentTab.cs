using System;

namespace Writersword.Core.Models.Project
{
    /// <summary>
    /// Документ (вкладка) в проекте
    /// Каждая вкладка = отдельный документ для письма
    /// </summary>
    public class DocumentTab
    {
        /// <summary>Уникальный ID документа</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>Название вкладки</summary>
        public string Title { get; set; } = "Document";

        /// <summary>Содержимое документа (текст)</summary>
        public string Content { get; set; } = string.Empty;

        /// <summary>Активная вкладка (выбрана сейчас)</summary>
        public bool IsActive { get; set; } = false;

        /// <summary>Путь к файлу документа</summary>
        public string? FilePath { get; set; }
    }
}