using System.Threading.Tasks;
using Writersword.Core.Models.Project;
using Writersword.Core.Enums;

namespace Writersword.Services
{
    /// <summary>
    /// Сервис для работы с проектами (.writersword файлами)
    /// </summary>
    public interface IProjectService
    {
        /// <summary>Создать новый проект</summary>
        ProjectFile CreateNew(string title, ProjectType type);

        /// <summary>Загрузить проект из файла</summary>
        Task<ProjectFile?> LoadAsync(string filePath);

        /// <summary>Сохранить проект в файл</summary>
        Task<bool> SaveAsync(ProjectFile project, string filePath);

        /// <summary>Текущий открытый проект</summary>
        ProjectFile? CurrentProject { get; }

        /// <summary>Путь к текущему файлу проекта</summary>
        string? CurrentFilePath { get; }
    }
}