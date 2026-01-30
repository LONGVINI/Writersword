using System.Collections.Generic;
using Writersword.Core.Interfaces.Modules;

namespace Writersword.Core.Interfaces.Services
{
    /// <summary>
    /// Сервис для сбора данных из модулей
    /// Используется при кешировании, сохранении проекта и переключении вкладок
    /// </summary>
    public interface IModuleStateCollectorService
    {
        /// <summary>
        /// Собрать ТОЛЬКО CustomData из всех модулей
        /// Используется при сохранении в .writersword файл (Ctrl+S)
        /// Возвращает словарь: ModuleId → CustomData
        /// Модули без данных НЕ включаются в результат
        /// </summary>
        /// <param name="modules">Список модулей для обработки</param>
        /// <returns>Словарь ModuleId → CustomData (только непустые)</returns>
        Dictionary<string, object?> CollectCustomData(IEnumerable<IModule> modules);

        /// <summary>
        /// Собрать ТОЛЬКО SessionData из всех модулей
        /// Используется редко, в основном для отладки
        /// Возвращает словарь: ModuleId → SessionData
        /// </summary>
        /// <param name="modules">Список модулей для обработки</param>
        /// <returns>Словарь ModuleId → SessionData</returns>
        Dictionary<string, object?> CollectSessionData(IEnumerable<IModule> modules);

        /// <summary>
        /// Собрать CustomData И SessionData из всех модулей
        /// Используется при кешировании (.wsasd) и переключении вкладок
        /// Возвращает ДВА словаря в виде кортежа
        /// </summary>
        /// <param name="modules">Список модулей для обработки</param>
        /// <returns>Кортеж (CustomData словарь, SessionData словарь)</returns>
        (Dictionary<string, object?> CustomData, Dictionary<string, object?> SessionData) CollectAllData(IEnumerable<IModule> modules);
    }
}