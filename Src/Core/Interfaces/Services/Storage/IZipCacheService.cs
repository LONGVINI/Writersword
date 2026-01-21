using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Writersword.Core.Models.Cache;
using Writersword.Core.Models.Modules;

namespace Writersword.Core.Interfaces.Services
{
    /// <summary>
    /// Сервис для работы с кешем проекта в формате ZIP (.writersword.wsasd файлы)
    /// Кеш хранит состояния модулей для быстрого восстановления сессии
    /// Использует ZIP архив с метаданными и хешированием для оптимизации
    /// </summary>
    public interface IZipCacheService
    {
        /// <summary>Проверить существует ли кеш для проекта</summary>
        /// <param name="projectPath">Путь к файлу проекта (.writersword)</param>
        bool HasCache(string projectPath);

        /// <summary>Получить дату создания кеша</summary>
        /// <param name="projectPath">Путь к файлу проекта (.writersword)</param>
        DateTime? GetCacheDate(string projectPath);

        /// <summary>
        /// Загрузить весь кеш проекта
        /// Возвращает словарь ModuleType → ModuleState
        /// </summary>
        /// <param name="projectPath">Путь к файлу проекта (.writersword)</param>
        Dictionary<string, ModuleState>? LoadCache(string projectPath);

        /// <summary>
        /// Сохранить весь кеш проекта
        /// Принимает словарь ModuleType → ModuleState
        /// Использует хеширование для проверки изменений
        /// </summary>
        /// <param name="projectPath">Путь к файлу проекта (.writersword)</param>
        /// <param name="projectId">GUID проекта для защиты от путаницы</param>
        /// <param name="moduleStates">Словарь состояний модулей</param>
        Task SaveCacheAsync(string projectPath, string projectId, Dictionary<string, ModuleState> moduleStates);

        /// <summary>
        /// Получить состояние конкретного модуля из кеша
        /// Возвращает null если кеша нет или модуль не найден
        /// </summary>
        /// <param name="projectPath">Путь к файлу проекта (.writersword)</param>
        /// <param name="moduleType">Тип модуля (например "TextEditor")</param>
        ModuleState? GetModuleState(string projectPath, string moduleType);

        /// <summary>
        /// Сохранить состояние конкретного модуля в кеш
        /// Обновляет существующий кеш или создаёт новый
        /// </summary>
        /// <param name="projectPath">Путь к файлу проекта (.writersword)</param>
        /// <param name="projectId">GUID проекта</param>
        /// <param name="moduleType">Тип модуля</param>
        /// <param name="state">Состояние модуля</param>
        Task SaveModuleStateAsync(string projectPath, string projectId, string moduleType, ModuleState state);

        /// <summary>
        /// Прочитать данные проекта из ZIP БЕЗ блокировки файла
        /// Используется для сравнения данных в CacheUpdateService
        /// </summary>
        /// <param name="projectPath">Путь к файлу проекта (.writersword)</param>
        /// <returns>ModulesData из проекта или null если ошибка</returns>
        Dictionary<string, object?>? ReadProjectDataWithoutLock(string projectPath);

        /// <summary>Удалить кеш проекта</summary>
        /// <param name="projectPath">Путь к файлу проекта (.writersword)</param>
        void DeleteCache(string projectPath);
    }
}