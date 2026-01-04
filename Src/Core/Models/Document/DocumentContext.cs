using Writersword.Core.Models.Project;

namespace Writersword.Core.Models
{
    /// <summary>
    /// Контекст документа - передаётся модулям для доступа к общим данным
    /// Позволяет модулям быть автономными и самостоятельно реагировать на изменения
    /// </summary>
    public class DocumentContext
    {
        /// <summary>Режим просмотра без редактирования (для сравнения версий)</summary>
        public bool IsInCompareMode { get; set; }

        /// <summary>Проект, к которому относится документ</summary>
        public ProjectFile Project { get; set; }

        /// <summary>Путь к файлу проекта</summary>
        public string FilePath { get; set; }

        /// <summary>Конструктор</summary>
        public DocumentContext(ProjectFile project, string filePath)
        {
            Project = project;
            FilePath = filePath;
            IsInCompareMode = false;
        }
    }
}