using System.Collections.Generic;
using System.Threading.Tasks;
using Writersword.Core.Models.Project;
using Writersword.Core.Enums;

namespace Writersword.Services
{
    /// <summary>
    /// Интерфейс сервиса работы с проектами
    /// </summary>
    public interface IProjectService
    {
        /// <summary>Получить все открытые проекты</summary>
        IReadOnlyList<ProjectFile> OpenProjects { get; }

        /// <summary>Найти проект по пути к файлу</summary>
        ProjectFile? GetProjectByPath(string filePath);

        /// <summary>Получить путь к файлу проекта</summary>
        string? GetProjectPath(ProjectFile project);

        /// <summary>Создать новый проект</summary>
        ProjectFile CreateNew(string title, ProjectType type);

        /// <summary>Загрузить проект из файла</summary>
        Task<ProjectFile?> LoadAsync(string filePath);

        /// <summary>Сохранить проект в файл</summary>
        Task<bool> SaveAsync(ProjectFile project, string filePath);

        /// <summary>Закрыть проект</summary>
        void CloseProject(ProjectFile project);
    }
}