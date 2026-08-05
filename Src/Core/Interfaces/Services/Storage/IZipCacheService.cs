using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Writersword.Core.Models.Cache;

namespace Writersword.Core.Interfaces.Services
{
    /// <summary>
    /// Интерфейс сервиса кеширования данных модулей
    /// Кеш хранится в .writersword.wsasd файле рядом с проектом
    /// Ключ данных модуля — moduleType (строка), не InstanceId
    /// </summary>
    public interface IZipCacheService
    {
        /// <summary>
        /// Проверить существование кеша для проекта
        /// </summary>
        bool HasCache(string projectPath);

        /// <summary>
        /// Получить дату создания кеша
        /// </summary>
        DateTime? GetCacheDate(string projectPath);

        /// <summary>
        /// Загрузить CustomData из кеша
        /// Возвращает словарь moduleType → CustomData
        /// Проверяет ProjectId: если не совпадает — возвращает null
        /// </summary>
        Dictionary<string, object?>? LoadCache(string projectPath, string? expectedProjectId = null);

        /// <summary>
        /// Загрузить CustomData И SessionData из кеша одним чтением архива.
        /// Возвращает два словаря: moduleType → CustomData, moduleType → SessionData.
        /// SessionData может быть null для модулей у которых нет сессионных данных.
        /// </summary>
        (Dictionary<string, object?> CustomData, Dictionary<string, object?> SessionData)?
            LoadCacheWithSession(string projectPath, string? expectedProjectId = null);

        /// <summary>
        /// Загрузить метаданные кеша без загрузки данных модулей
        /// </summary>
        ModuleCacheMetadata? LoadCacheMetadata(string projectPath);

        /// <summary>
        /// Сохранить кеш проекта
        /// Ключ в словарях — moduleType
        /// </summary>
        /// <param name="projectPath">Путь к .writersword файлу</param>
        /// <param name="projectId">ID проекта — записывается в метадату для верификации при загрузке</param>
        /// <param name="customDataDict">Данные модулей: moduleType → data</param>
        /// <param name="sessionDataDict">Сессионные данные: moduleType → data</param>
        Task SaveCacheAsync(
            string projectPath,
            string projectId,
            Dictionary<string, object?> customDataDict,
            Dictionary<string, object?> sessionDataDict);

        /// <summary>
        /// Получить CustomData конкретного модуля из кеша
        /// </summary>
        object? GetModuleCustomData(string projectPath, string moduleType);

        /// <summary>
        /// Удалить кеш проекта вместе с резервной копией
        /// </summary>
        void DeleteCache(string projectPath);

        /// <summary>
        /// Убрать основной файл кеша, сохранив его как резервную копию.
        /// Для мест, где кеш должен временно исчезнуть из виду (переключение
        /// воркмода), но точка восстановления обязана пережить аварию.
        /// </summary>
        void MoveCacheToBackup(string projectPath);

        /// <summary>
        /// Прочитать ModulesData из project.json без эксклюзивной блокировки файла
        /// Используется для сравнения при принятии решения о записи кеша
        /// </summary>
        Dictionary<string, object?>? ReadProjectDataWithoutLock(string projectPath);
    }
}