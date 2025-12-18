using System;
using System.Collections.Generic;
using Writersword.Core.Enums;
using Writersword.Core.Models.WorkModes;

namespace Writersword.Core.Models.Settings
{
    /// <summary>
    /// Сохранённая конфигурация рабочего пространства для типа проекта
    /// Хранится в settings.json как глобальная настройка пользователя
    /// Приоритет: Проект → Глобальная (это) → Дефолтная
    /// </summary>
    public class WorkspaceConfig
    {
        /// <summary>Тип проекта для которого эта конфигурация</summary>
        public ProjectType ProjectType { get; set; }

        /// <summary>Название конфигурации (может быть изменено пользователем)</summary>
        public string Name { get; set; } = "My Configuration";

        /// <summary>Дата создания конфигурации</summary>
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        /// <summary>Дата последнего изменения</summary>
        public DateTime LastModified { get; set; } = DateTime.Now;

        /// <summary>
        /// Список WorkModes с их модулями
        /// Это полная копия того, что пользователь настроил
        /// </summary>
        public List<WorkMode> WorkModes { get; set; } = new();

        /// <summary>Версия формата (для совместимости при обновлениях)</summary>
        public string FormatVersion { get; set; } = "1.0";
    }
}