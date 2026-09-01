using Writersword.Core.Interfaces.Services;
using Writersword.Core.Models.Settings;

namespace Writersword.Core.Interfaces.Services
{
    /// <summary>
    /// Интерфейс сервиса управления локальной конфигурацией workspace
    /// Читает/пишет workspace.json внутри ZIP проекта
    /// </summary>
    public interface IWorkspaceConfigService
    {
        /// <summary>
        /// Загрузить локальную конфигурацию из workspace.json в ZIP
        /// Возвращает null если файл не найден или ошибка чтения
        /// </summary>
        WorkspaceLocalConfig? LoadFromStorage(IProjectFileStorage fileStorage);

        /// <summary>
        /// Сохранить локальную конфигурацию в workspace.json в ZIP
        /// </summary>
        bool SaveToStorage(IProjectFileStorage fileStorage, WorkspaceLocalConfig config);

        /// <summary>
        /// Удалить workspace.json из ZIP
        /// Используется при сбросе к дефолтным настройкам
        /// </summary>
        bool DeleteFromZip(IProjectFileStorage fileStorage);
    }
}