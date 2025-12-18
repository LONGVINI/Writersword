using System.Collections.Generic;
using Writersword.Core.Enums;

namespace Writersword.Core.Models.WorkModes
{
    /// <summary>
    /// Пресет WorkModes для типа проекта
    /// Определяет какие режимы работы создаются по умолчанию при создании проекта
    /// Это ШАБЛОН (не сохраняется в файлах, hardcoded в коде)
    /// </summary>
    public class WorkModePreset
    {
        /// <summary>Тип проекта (Novel, Screenplay и т.д.)</summary>
        public ProjectType ProjectType { get; set; }

        /// <summary>Список шаблонов WorkModes которые создаются по умолчанию</summary>
        public List<WorkModeTemplate> WorkModes { get; set; } = new();
    }
}