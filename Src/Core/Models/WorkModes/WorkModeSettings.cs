using System.Collections.Generic;

namespace Writersword.Core.Models.WorkModes
{
    /// <summary>
    /// Настройки режима работы (раскладка модулей, тема и т.д.)
    /// Сохраняются вместе с WorkMode
    /// </summary>
    public class WorkModeSettings
    {
        /// <summary>Дополнительные настройки (JSON)</summary>
        public Dictionary<string, object> CustomSettings { get; set; } = new();
    }
}