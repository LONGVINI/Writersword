using System.Collections.Generic;
using Writersword.Core.Models.WorkModes;

namespace Writersword.Src.Core.Interfaces.WorkModes
{
    /// <summary>
    /// Сервис управления конфигурациями WorkModes
    /// Определяет приоритет: Проект → Глобальная → Дефолтная
    /// </summary>
    public interface IWorkModeConfigurationService
    {
        /// <summary>
        /// Загрузить конфигурацию для проекта
        /// Приоритет: если в проекте есть WorkModes → используем их
        /// Если нет → берём глобальную конфигурацию
        /// Если нет → берём дефолтную
        /// </summary>
        List<WorkMode> LoadConfiguration(string projectType, List<WorkMode>? projectWorkModes);

        /// <summary>Сохранить конфигурацию глобально (для всех проектов данного типа)</summary>
        void SaveGlobalConfiguration(string projectType, List<WorkMode> workModes);

        /// <summary>Удалить глобальную конфигурацию (вернуться к дефолтной)</summary>
        void DeleteGlobalConfiguration(string projectType);

        /// <summary>Загрузить дефолтную конфигурацию (без сохранения)</summary>
        List<WorkMode> LoadDefaultConfiguration(string projectType);

        /// <summary>Проверить можно ли удалить модуль из режима</summary>
        bool CanRemoveModule(string workModeId, string moduleId);

        /// <summary>Получить обязательные модули для режима</summary>
        List<string> GetRequiredModules(string workModeId);

        /// <summary>Клонировать WorkModes (глубокое копирование)</summary>
        List<WorkMode> CloneWorkModes(List<WorkMode> source);
    }
}