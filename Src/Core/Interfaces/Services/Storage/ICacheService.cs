using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Writersword.Core.Models.Cache;
using Writersword.Core.Models.Modules;

namespace Writersword.Core.Interfaces.Services
{
    /// <summary>
    /// Сервис для работы с кешем проекта (.wsasd файлы)
    /// Кеш хранит состояния модулей для быстрого восстановления сессии
    /// </summary>
    public interface ICacheService
    {
        /// <summary>Проверить существует ли кеш для проекта</summary>
        bool HasCache(string projectPath);

        /// <summary>Получить дату создания кеша</summary>
        DateTime? GetCacheDate(string projectPath);

        /// <summary>
        /// Загрузить весь кеш проекта
        /// Возвращает словарь ModuleType → ModuleState
        /// </summary>
        Dictionary<string, ModuleState>? LoadCache(string projectPath);

        /// <summary>
        /// Сохранить весь кеш проекта
        /// Принимает словарь ModuleType → ModuleState
        /// </summary>
        Task SaveCacheAsync(string projectPath, Dictionary<string, ModuleState> moduleStates);

        /// <summary>
        /// Получить состояние конкретного модуля из кеша
        /// Возвращает null если кеша нет или модуль не найден
        /// </summary>
        ModuleState? GetModuleState(string projectPath, string moduleType);

        /// <summary>
        /// Сохранить состояние конкретного модуля в кеш
        /// Обновляет существующий кеш или создаёт новый
        /// </summary>
        Task SaveModuleStateAsync(string projectPath, string moduleType, ModuleState state);

        /// <summary>Удалить кеш проекта</summary>
        void DeleteCache(string projectPath);
    }
}