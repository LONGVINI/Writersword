using System.Collections.Generic;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Models.Modules;

namespace Writersword.Core.Interfaces.Modules
{
    /// <summary>
    /// Сервис для сбора состояний модулей
    /// Используется при автосохранении, переключении WorkMode и сохранении проекта
    /// </summary>
    public interface IModuleStateCollectorService
    {
        /// <summary>
        /// Собрать ПОЛНЫЕ состояния всех модулей (CustomData + SessionData)
        /// Используется при переключении WorkMode
        /// </summary>
        /// <param name="modules">Список активных модулей</param>
        /// <returns>Словарь ModuleType → ModuleState</returns>
        Dictionary<string, ModuleState> CollectAllStates(IEnumerable<IModule> modules);

        /// <summary>
        /// Собрать ТОЛЬКО CustomData всех модулей (для сохранения в .writersword)
        /// Используется при Ctrl+S
        /// </summary>
        /// <param name="modules">Список всех модулей</param>
        /// <returns>Словарь ModuleType → CustomData</returns>
        Dictionary<string, object?> CollectCustomData(IEnumerable<IModule> modules);

        /// <summary>
        /// Собрать ТОЛЬКО SessionData всех модулей (для автосохранения в .wsasd)
        /// Используется при автосохранении каждые 10 секунд
        /// </summary>
        /// <param name="modules">Список активных модулей</param>
        /// <returns>Словарь ModuleType → SessionData</returns>
        Dictionary<string, object?> CollectSessionData(IEnumerable<IModule> modules);

        /// <summary>
        /// Собрать состояние одного модуля
        /// </summary>
        /// <param name="module">Модуль для сбора</param>
        /// <returns>ModuleState или null</returns>
        ModuleState? CollectModuleState(IModule module);
    }
}