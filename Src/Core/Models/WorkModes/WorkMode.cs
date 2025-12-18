using System;
using System.Collections.Generic;
using Writersword.Core.Enums;

namespace Writersword.Core.Models.WorkModes
{
    /// <summary>
    /// Режим работы (WorkMode) - большая кнопка под вкладкой документа
    /// Например: "Редактор", "Таймлайн", "Персонажи"
    /// Содержит список модулей, которые отображаются в этом режиме
    /// Сохраняется в файле проекта (.writersword) или в settings.json
    /// </summary>
    public class WorkMode
    {
        /// <summary>Уникальный ID экземпляра режима</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>Тип режима работы</summary>
        public WorkModeType Type { get; set; }

        /// <summary>Название режима (отображается в UI)</summary>
        public string Title { get; set; } = "Work Mode";

        /// <summary>Иконка режима (Unicode символ или путь)</summary>
        public string Icon { get; set; } = "📝";

        /// <summary>Активен ли этот режим сейчас</summary>
        public bool IsActive { get; set; } = false;

        /// <summary>Порядок отображения кнопки</summary>
        public int Order { get; set; } = 0;

        /// <summary>Можно ли закрыть этот режим (Editor нельзя закрыть)</summary>
        public bool IsCloseable { get; set; } = true;

        /// <summary>Список модулей в этом режиме</summary>
        public List<ModuleSlot> ModuleSlots { get; set; } = new();

        /// <summary>Дата создания режима</summary>
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        /// <summary>Дата последнего использования</summary>
        public DateTime LastAccessedAt { get; set; } = DateTime.Now;

        /// <summary>Настройки режима (раскладка, тема)</summary>
        public WorkModeSettings Settings { get; set; } = new();
    }
}