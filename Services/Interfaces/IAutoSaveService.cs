using System;

namespace Writersword.Services.Interfaces
{
    /// <summary>
    /// Интерфейс сервиса автоматического сохранения
    /// </summary>
    public interface IAutoSaveService
    {
        /// <summary>Запустить автосохранение для проекта</summary>
        void Start(string projectPath);

        /// <summary>Остановить автосохранение</summary>
        void Stop();

        /// <summary>Принудительно запустить сохранение</summary>
        void TriggerSave();

        /// <summary>Установить интервал автосохранения</summary>
        void SetInterval(TimeSpan interval);

        /// <summary>Событие завершения автосохранения</summary>
        event EventHandler? AutoSaveCompleted;
    }
}