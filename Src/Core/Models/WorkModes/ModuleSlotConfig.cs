using Writersword.Core.Enums;

namespace Writersword.Core.Models.WorkModes
{
    /// <summary>
    /// Конфигурация слота модуля - описывает какой модуль, где расположен и его категорию
    /// </summary>
    public class ModuleSlotConfig
    {
        /// <summary>Тип модуля</summary>
        public ModuleType ModuleType { get; set; }

        /// <summary>Минимальная ширина модуля (px)</summary>
        public double MinWidth { get; set; } = 200;

        /// <summary>Минимальная высота модуля (px)</summary>
        public double MinHeight { get; set; } = 150;

        /// <summary>Видим ли модуль по умолчанию при загрузке</summary>
        public bool IsVisible { get; set; } = true;

        /// <summary>Категория модуля в этом WorkMode</summary>
        public ModuleCategory Category { get; set; } = ModuleCategory.Optional;

        /// <summary>Предпочтительное расположение модуля при докировании</summary>
        public PreferredDockPosition PreferredPosition { get; set; } = PreferredDockPosition.RightAsTab;
    }
}