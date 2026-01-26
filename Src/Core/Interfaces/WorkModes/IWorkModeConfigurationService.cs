using System.Collections.Generic;
using Writersword.Core.Interfaces.Services;
using Writersword.Core.Models.WorkModes;

namespace Writersword.Src.Core.Interfaces.Services
{
    /// <summary>
    /// Интерфейс сервиса управления конфигурациями WorkModes
    /// Определяет приоритет: LOCAL (workspace.json в ZIP) → GLOBAL (Settings.json) → DEFAULT (hardcoded)
    /// </summary>
    public interface IWorkModeConfigurationService
    {
        /// <summary>
        /// Загрузить конфигурацию для проекта
        /// Приоритет: LOCAL (workspace.json в ZIP) → GLOBAL (Settings.json) → DEFAULT (hardcoded)
        /// </summary>
        /// <param name="projectType">Тип проекта (Novel, Translation, и т.д.)</paramф>
        /// <param name="fileStorage">Хранилище файлов проекта (ZIP) для загрузки локальной конфигурации</param>
        /// <returns>Список настроенных WorkMode</returns>
        List<WorkMode> LoadConfiguration(string projectType, IProjectFileStorage? fileStorage = null);

        /// <summary>
        /// Загрузить дефолтную конфигурацию из реестра WorkMode (hardcoded)
        /// Использует GetDefaultConfig() каждого зарегистрированного WorkMode
        /// </summary>
        /// <param name="projectType">Тип проекта (Novel, Translation, и т.д.)</param>
        /// <returns>Список WorkMode с дефолтными настройками</returns>
        List<WorkMode> LoadDefaultConfiguration(string projectType);

        /// <summary>
        /// Проверить можно ли удалить модуль из WorkMode
        /// Проверяет дефолтную конфигурацию - если модуль Required, то нельзя удалить
        /// </summary>
        /// <param name="projectType">Тип проекта (Novel, Translation, и т.д.)</param>
        /// <param name="workModeId">ID режима работы (editor, timeline, и т.д.)</param>
        /// <param name="moduleId">ID модуля (TextEditor, Notes, и т.д.)</param>
        /// <returns>true если можно удалить, false если нельзя</returns>
        bool CanRemoveModule(string projectType, string workModeId, string moduleId);
    }
}