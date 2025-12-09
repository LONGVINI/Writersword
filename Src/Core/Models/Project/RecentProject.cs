using Writersword.Core.Enums;

namespace Writersword.Core.Models.Project
{
    /// <summary>
    /// Информация о недавно открытом проекте
    /// </summary>
    public class RecentProject
    {
        /// <summary>Имя файла</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Полный путь к файлу</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>Тип проекта</summary>
        public ProjectType Type { get; set; }

        /// <summary>Дата последнего открытия</summary>
        public System.DateTime LastOpened { get; set; }
    }
}
