using System.Collections.Generic;

namespace Writersword.Core.Models.WorkModes
{
    /// <summary>
    /// Настройки режима работы (раскладка модулей, тема и т.д.)
    /// Сохраняются вместе с WorkMode
    /// </summary>
    public class WorkModeSettings
    {
        /// <summary>Тип раскладки модулей: Grid, Stack, Tabs</summary>
        public string LayoutType { get; set; } = "Grid";

        /// <summary>Количество столбцов в Grid (если используется)</summary>
        public int GridColumns { get; set; } = 4;

        /// <summary>Количество строк в Grid (если используется)</summary>
        public int GridRows { get; set; } = 3;

        /// <summary>Дополнительные настройки (JSON)</summary>
        public Dictionary<string, object> CustomSettings { get; set; } = new();
    }
}