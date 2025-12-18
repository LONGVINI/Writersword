using System.Collections.Generic;
using Writersword.Core.Enums;

namespace Writersword.Core.Models.WorkModes
{
    /// <summary>
    /// Шаблон для создания WorkMode
    /// Это НЕ сам WorkMode, а инструкция как его создать
    /// Используется в пресетах (hardcoded)
    /// </summary>
    public class WorkModeTemplate
    {
        /// <summary>Тип режима работы</summary>
        public WorkModeType Type { get; set; }

        /// <summary>Название режима</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>Иконка режима (Unicode символ)</summary>
        public string Icon { get; set; } = string.Empty;

        /// <summary>Порядок отображения кнопки</summary>
        public int Order { get; set; }

        /// <summary>Можно ли закрыть этот режим (Editor нельзя)</summary>
        public bool IsCloseable { get; set; } = true;

        /// <summary>Какие модули должны быть в этом режиме</summary>
        public List<ModuleSlotTemplate> ModuleSlots { get; set; } = new();
    }
}