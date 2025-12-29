using System;
using System.Threading.Tasks;
using Writersword.Core.Models.Project;

namespace Writersword.Services.Interfaces
{
    /// <summary>
    /// Интерфейс сервиса управления файлами автосохранения
    /// </summary>
    public interface ICacheService
    {
        /// <summary>Проверить существует ли кеш для проекта</summary>
        bool HasCache(string projectPath);

        /// <summary>Сохранить проект в кеш</summary>
        Task SaveToCacheAsync(ProjectFile project, string projectPath);

        /// <summary>Загрузить проект из кеша</summary>
        Task<ProjectFile?> LoadFromCacheAsync(string projectPath);

        /// <summary>Удалить кеш файл</summary>
        void DeleteCache(string projectPath);

        /// <summary>Получить дату создания кеша</summary>
        DateTime? GetCacheDate(string projectPath);

        /// <summary>Получить дату сохранения основного файла</summary>
        DateTime? GetSaveDate(string projectPath);
    }
}