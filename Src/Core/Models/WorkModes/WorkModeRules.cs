using System.Collections.Generic;
using Writersword.Core.Enums;

namespace Writersword.Core.Models.WorkModes
{
    /// <summary>
    /// Правила для режимов работы
    /// Определяет какие модули обязательны, а какие можно удалить
    /// </summary>
    public static class WorkModeRules
    {
        /// <summary>Получить обязательные модули для режима</summary>
        public static List<ModuleType> GetRequiredModules(WorkModeType workModeType)
        {
            return workModeType switch
            {
                WorkModeType.Editor => new List<ModuleType> { ModuleType.TextEditor }, // TextEditor обязателен
                WorkModeType.Timeline => new List<ModuleType> { ModuleType.Timeline },
                WorkModeType.Characters => new List<ModuleType> { ModuleType.Characters },
                WorkModeType.Maps => new List<ModuleType> { ModuleType.Maps },
                WorkModeType.GameEconomy => new List<ModuleType> { ModuleType.GameEconomy },
                WorkModeType.Dialogues => new List<ModuleType> { ModuleType.Dialogues },
                WorkModeType.Poetry => new List<ModuleType> { ModuleType.Poetry },
                _ => new List<ModuleType>()
            };
        }

        /// <summary>Можно ли удалить модуль из режима</summary>
        public static bool CanRemoveModule(WorkModeType workModeType, ModuleType moduleType)
        {
            var required = GetRequiredModules(workModeType);
            return !required.Contains(moduleType);
        }
    }
}