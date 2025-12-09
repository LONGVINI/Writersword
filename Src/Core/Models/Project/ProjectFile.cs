using System;
using System.Collections.Generic;
using Writersword.Core.Enums;

namespace Writersword.Core.Models.Project
{
    /// <summary>
    /// Модель файла проекта .writersword
    /// Всё что сохраняется в JSON
    /// </summary>
    public class ProjectFile
    {
        /// <summary>Название проекта</summary>
        public string Title { get; set; } = "Untitled";

        /// <summary>Тип проекта (Novel, Screenplay и т.д.)</summary>
        public ProjectType Type { get; set; } = ProjectType.Novel;

        /// <summary>Версия формата файла (для совместимости)</summary>
        public string FormatVersion { get; set; } = "1.0";

        /// <summary>Дата создания проекта</summary>
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        /// <summary>Дата последнего изменения</summary>
        public DateTime LastModified { get; set; } = DateTime.Now;

        /// <summary>Открытые документы (вкладки)</summary>
        public List<DocumentTab> Documents { get; set; } = new();

        /// <summary>Данные модулей (персонажи, таймлайн и т.д.)</summary>
        public Dictionary<string, object> ModulesData { get; set; } = new();
    }
}