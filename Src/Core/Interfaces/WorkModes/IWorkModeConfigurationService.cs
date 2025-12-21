using System.Collections.Generic;
using Writersword.Core.Enums;
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
        List<WorkMode> LoadConfiguration(ProjectType projectType, List<WorkMode>? projectWorkModes);

        /// <summary>Сохранить конфигурацию глобально (для всех проектов данного типа)</summary>
        void SaveGlobalConfiguration(ProjectType projectType, List<WorkMode> workModes);

        /// <summary>Удалить глобальную конфигурацию (вернуться к дефолтной)</summary>
        void DeleteGlobalConfiguration(ProjectType projectType);

        /// <summary>Загрузить дефолтную конфигурацию (без сохранения)</summary>
        List<WorkMode> LoadDefaultConfiguration(ProjectType projectType);

        /// <summary>Проверить можно ли удалить модуль из режима</summary>
        bool CanRemoveModule(WorkModeType workModeType, ModuleType moduleType);

        /// <summary>Получить обязательные модули для режима</summary>
        List<ModuleType> GetRequiredModules(WorkModeType workModeType);

        /// <summary>Клонировать WorkModes (глубокое копирование)</summary>
        List<WorkMode> CloneWorkModes(List<WorkMode> source);
    }
}