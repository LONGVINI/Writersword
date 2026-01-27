using System;
using System.Collections.Generic;

namespace Writersword.Core.Models.WorkModes
{
    /// <summary>
    /// Режим работы (WorkMode) - большая кнопка под вкладкой документа
    /// Например: "Редактор", "Таймлайн", "Персонажи"
    /// Содержит список модулей и структуру их расположения
    /// Сохраняется в workspace.json внутри ZIP проекта
    /// </summary>
    public class WorkMode
    {
        /// <summary>Внутренний ID экземпляра (для UI)</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>Уникальный ID типа WorkMode (например: "editor", "timeline", "characters")</summary>
        public string WorkModeId { get; set; } = "Unknown";

        /// <summary>Название режима (отображается в UI)</summary>
        public string Title { get; set; } = "Unknown";

        /// <summary>Иконка режима (Unicode символ или путь)</summary>
        public string Icon { get; set; } = "❌";

        /// <summary>Активен ли этот режим сейчас</summary>
        public bool IsActive { get; set; }

        /// <summary>Порядок отображения кнопки</summary>
        public int Order { get; set; }

        /// <summary>Можно ли закрыть этот режим (Editor нельзя закрыть)</summary>
        public bool IsCloseable { get; set; } = true;

        /// <summary>
        /// Список всех модулей в этом режиме
        /// Включает информацию о расположении каждого модуля (dock или float)
        /// </summary>
        public List<ModuleSlot> ModuleSlots { get; set; } = new();

        /// <summary>
        /// Структура контейнеров (split панелей)
        /// Описывает как разделено пространство на области
        /// Модули привязываются к контейнерам через ContainerId
        /// </summary>
        public List<SplitContainer> Containers { get; set; } = new();
    }
}