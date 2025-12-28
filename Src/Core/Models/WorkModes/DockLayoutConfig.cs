using System.Collections.Generic;

namespace Writersword.Core.Models.WorkModes
{
    /// <summary>
    /// Конфигурация Dock-раскладки для WorkMode
    /// Описывает как модули расположены на экране
    /// </summary>
    public class DockLayoutConfig
    {
        /// <summary>Главная ориентация split (Horizontal = лево/право, Vertical = верх/низ)</summary>
        public DockOrientation MainOrientation { get; set; } = DockOrientation.Horizontal;

        /// <summary>Список панелей в layout</summary>
        public List<DockPanelConfig> Panels { get; set; } = new();
    }

    /// <summary>
    /// Ориентация split панели
    /// </summary>
    public enum DockOrientation
    {
        /// <summary>Горизонтальный split (лево/право)</summary>
        Horizontal,

        /// <summary>Вертикальный split (верх/низ)</summary>
        Vertical
    }
}