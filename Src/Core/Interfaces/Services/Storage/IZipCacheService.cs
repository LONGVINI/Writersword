using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Writersword.Core.Interfaces.Services
{
    /// <summary>
    /// Интерфейс сервиса для работы с кешем проектов
    /// Кеш хранится в .writersword.wsasd файле (ZIP архив)
    /// </summary>
    public interface IZipCacheService
    {
        /// <summary>Проверить существует ли кеш для проекта</summary>
        bool HasCache(string projectPath);

        /// <summary>Получить дату создания кеша</summary>
        DateTime? GetCacheDate(string projectPath);

        /// <summary>
        /// Загрузить CustomData из кеша
        /// Возвращает словарь ModuleId → CustomData
        /// SessionData НЕ возвращается (он нужен только внутри системы)
        /// </summary>
        Dictionary<string, object?>? LoadCache(string projectPath);

        /// <summary>
        /// Сохранить кеш проекта
        /// Принимает ДВА словаря: CustomData и SessionData
        /// </summary>
        /// <param name="projectPath">Путь к файлу проекта</param>
        /// <param name="projectId">ID проекта</param>
        /// <param name="customDataDict">Словарь CustomData (ModuleId → данные)</param>
        /// <param name="sessionDataDict">Словарь SessionData (ModuleId → данные)</param>
        Task SaveCacheAsync(string projectPath, string projectId, Dictionary<string, object?> customDataDict, Dictionary<string, object?> sessionDataDict);

        /// <summary>
        /// Получить CustomData конкретного модуля из кеша
        /// </summary>
        object? GetModuleCustomData(string projectPath, string moduleId);

        /// <summary>Удалить кеш проекта</summary>
        void DeleteCache(string projectPath);

        /// <summary>
        /// Прочитать данные проекта из ZIP БЕЗ блокировки файла
        /// Используется для сравнения данных
        /// </summary>
        Dictionary<string, object?>? ReadProjectDataWithoutLock(string projectPath);
    }
}