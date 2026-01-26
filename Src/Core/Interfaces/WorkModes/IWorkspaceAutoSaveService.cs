using System;
using System.Threading.Tasks;
using Writersword.Core.Models.Project;

namespace Writersword.Src.Core.Interfaces.Services
{
    /// <summary>
    /// Интерфейс сервиса автосохранения workspace конфигурации
    /// </summary>
    public interface IWorkspaceAutoSaveService : IDisposable
    {
        /// <summary>Запустить автосохранение для проекта</summary>
        void Start(string projectPath, ProjectFile project);

        /// <summary>Остановить автосохранение</summary>
        void Stop();

        /// <summary>Уведомить об изменении (запускает таймер 5 секунд)</summary>
        void NotifyChange();

        /// <summary>Принудительно сохранить СЕЙЧАС</summary>
        Task SaveNowAsync();
    }
}