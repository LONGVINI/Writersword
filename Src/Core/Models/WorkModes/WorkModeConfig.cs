using System.Collections.Generic;

namespace Writersword.Core.Models.WorkModes
{
    /// <summary>
    /// Конфигурация WorkMode - описывает порядок отображения и модули
    /// Используется в трёх уровнях: DEFAULT (hardcoded) → GLOBAL (settings.json) → PROJECT (project.writersword)
    /// </summary>
    public class WorkModeConfig
    {
        /// <summary>Порядок отображения кнопки WorkMode</summary>
        public int Order { get; set; }

        /// <summary>Список слотов модулей с их конфигурацией</summary>
        public List<ModuleSlotConfig> ModuleSlots { get; set; } = new();

        /// <summary>Конфигурация Dock-раскладки (как модули расположены на экране)</summary>
        public DockLayoutConfig DockLayout { get; set; } = new();
    }
}