using System.Collections.Generic;
using Writersword.Core.Enums;

namespace Writersword.Core.Models.WorkModes
{
    /// <summary>
    /// Конфигурация одной панели в Dock
    /// </summary>
    public class DockPanelConfig
    {
        /// <summary>ID панели (например: "LeftPanel", "RightPanel")</summary>
        public string Id { get; set; } = "";

        /// <summary>Пропорция панели (0.0 - 1.0, например 0.7 = 70% ширины)</summary>
        public double Proportion { get; set; } = 0.5;

        /// <summary>Список модулей в этой панели</summary>
        public List<ModuleType> Modules { get; set; } = new();

        /// <summary>Если панель сама содержит split - указываем вложенную конфигурацию</summary>
        public DockLayoutConfig? NestedLayout { get; set; }
    }
}