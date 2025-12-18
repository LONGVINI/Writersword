using Writersword.Core.Enums;

namespace Writersword.Core.Models.WorkModes
{
    /// <summary>
    /// Шаблон для создания ModuleSlot
    /// Описывает какой модуль, где и какого размера должен быть
    /// Используется в пресетах для создания дефолтных конфигураций
    /// </summary>
    public class ModuleSlotTemplate
    {
        /// <summary>Тип модуля</summary>
        public ModuleType ModuleType { get; set; }

        /// <summary>Позиция в сетке (строка, столбец)</summary>
        public WorkModeGridPosition Position { get; set; } = new();

        /// <summary>Размер в сетке (сколько ячеек занимает)</summary>
        public WorkModeGridSize Size { get; set; } = new(1, 1);

        /// <summary>Минимальная ширина</summary>
        public double MinWidth { get; set; } = 200;

        /// <summary>Минимальная высота</summary>
        public double MinHeight { get; set; } = 150;

        /// <summary>Видим ли модуль по умолчанию</summary>
        public bool IsVisible { get; set; } = true;
    }
}