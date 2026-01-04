using System;
using System.Collections.Generic;
using Writersword.Core.Interfaces.Modules;

namespace Writersword.Core.Interfaces.Services
{
    /// <summary>
    /// Сервис автоматического сохранения SessionData модулей в кеш
    /// </summary>
    public interface IAutoSaveService
    {
        /// <summary>Событие завершения автосохранения</summary>
        event EventHandler? AutoSaveCompleted;

        /// <summary>
        /// Запустить автосохранение для проекта
        /// </summary>
        /// <param name="projectPath">Путь к проекту</param>
        /// <param name="getActiveModules">Функция получения активных модулей</param>
        void Start(string projectPath, Func<IEnumerable<IModule>> getActiveModules);

        /// <summary>Остановить автосохранение</summary>
        void Stop();

        /// <summary>Принудительно запустить сохранение</summary>
        void TriggerSave();

        /// <summary>Установить интервал автосохранения</summary>
        void SetInterval(TimeSpan interval);
    }
}