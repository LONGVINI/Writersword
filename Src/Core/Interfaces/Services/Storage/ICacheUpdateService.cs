using System;
using System.Collections.Generic;
using Writersword.Core.Interfaces.Modules;

namespace Writersword.Core.Interfaces.Services.Storage
{
    /// <summary>
    /// Сервис фонового кеширования состояния модулей
    /// Периодически сохраняет данные в .wsasd для recovery и переключения вкладок
    /// </summary>
    public interface ICacheUpdateService
    {
        /// <summary>Событие завершения кеширования</summary>
        event EventHandler? CacheSaved;

        /// <summary>
        /// Запустить фоновое кеширование для проекта
        /// </summary>
        /// <param name="projectPath">Путь к проекту</param>
        /// <param name="getActiveModules">Функция получения активных модулей</param>
        void Start(string projectPath, Func<IEnumerable<IModule>> getActiveModules);

        /// <summary>Остановить фоновое кеширование</summary>
        void Stop();

        /// <summary>Принудительно сохранить в кеш СЕЙЧАС (например, при переключении вкладок)</summary>
        void SaveToCache();

        /// <summary>Установить интервал кеширования (по умолчанию 10 секунд)</summary>
        void SetInterval(TimeSpan interval);
    }
}