using Newtonsoft.Json;
using ReactiveUI;
using System;
using System.Collections.Generic;
using Writersword.Core.Enums;

namespace Writersword.Core.Models.WorkModes
{
    /// <summary>
    /// Режим работы (WorkMode)
    /// Определяет набор доступных модулей и их конфигурацию для конкретного типа работы
    /// Например: "Редактор", "Черновик", "Анализ"
    /// </summary>
    public class WorkMode : ReactiveObject
    {
        private bool _isActive;

        /// <summary>
        /// Уникальный идентификатор экземпляра WorkMode
        /// Генерируется автоматически при создании
        /// </summary>
        [JsonProperty("Id")]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>
        /// Идентификатор типа WorkMode (например: "editor", "draft", "analysis")
        /// Используется для связи с зарегистрированным WorkMode в реестре
        /// </summary>
        [JsonProperty("WorkModeId")]
        public string WorkModeId { get; set; } = "Unknown";

        /// <summary>
        /// Отображаемое название WorkMode в UI
        /// </summary>
        [JsonProperty("Title")]
        public string Title { get; set; } = "Unknown";

        /// <summary>
        /// Иконка WorkMode (emoji или путь к изображению)
        /// </summary>
        [JsonProperty("Icon")]
        public string Icon { get; set; } = "❌";

        /// <summary>
        /// Флаг активности WorkMode
        /// Только один WorkMode может быть активен в любой момент времени
        /// </summary>
        [JsonProperty("IsActive")]
        public bool IsActive
        {
            get => _isActive;
            set => this.RaiseAndSetIfChanged(ref _isActive, value);
        }

        /// <summary>
        /// Порядок отображения WorkMode в UI (сортировка)
        /// Меньшее значение = выше в списке
        /// </summary>
        [JsonProperty("Order")]
        public int Order { get; set; }

        /// <summary>
        /// Можно ли закрыть (удалить) этот WorkMode
        /// false = обязательный WorkMode (например, "Редактор")
        /// </summary>
        [JsonProperty("IsCloseable")]
        public bool IsCloseable { get; set; } = true;

        /// <summary>
        /// Категории модулей для этого WorkMode
        /// Словарь: moduleType -> ModuleCategory
        /// Определяет какие модули доступны для добавления и их статус:
        /// - Required: обязательный, создаётся автоматически, нельзя удалить
        /// - Optional: можно добавить/удалить по желанию
        /// - Unwanted: можно добавить, но не рекомендуется (показывается в конце списка)
        /// - Forbidden: нельзя добавить в этот WorkMode (заблокирован в меню)
        /// НЕ сохраняется в workspace.json (берётся из дефолтной конфигурации)
        /// </summary>
        [JsonIgnore]
        public Dictionary<string, ModuleCategory> ModuleCategories { get; set; } = new Dictionary<string, ModuleCategory>();

        /// <summary>
        /// Слоты модулей (модули открытые в данный момент)
        /// Создаются динамически при добавлении модулей пользователем
        /// Сохраняются в workspace.json для восстановления состояния между сессиями
        /// </summary>
        [JsonProperty("ModuleSlots")]
        public List<ModuleSlot> ModuleSlots { get; set; } = new();

        /// <summary>
        /// Сериализованная структура Dock layout (JSON строка от Dock.Serializer)
        /// Содержит информацию о панелях, сплиттерах, float окнах
        /// Сохраняется в workspace.json для точного восстановления UI
        /// При создании дефолтной конфигурации может быть null (создаётся динамически из PreferredPosition)
        /// </summary>
        [JsonProperty("serializedDockLayout")]
        public string? SerializedDockLayout { get; set; }
    }
}