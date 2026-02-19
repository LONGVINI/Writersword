using Newtonsoft.Json;
using Writersword.Core.Enums;

namespace Writersword.Core.Models.WorkModes
{
    /// <summary>
    /// Слот модуля в WorkMode
    /// Хранит конфигурацию модуля: тип, категорию и предпочтительную позицию
    /// InstanceId убран — идентификатор модуля в рамках проекта = ModuleType
    /// </summary>
    public class ModuleSlot
    {
        /// <summary>
        /// Тип модуля (например: TextEditor, Synonyms, Timer)
        /// Является уникальным ключом модуля в рамках одного проекта
        /// </summary>
        [JsonProperty("moduleType")]
        public string ModuleType { get; set; } = string.Empty;

        /// <summary>
        /// Категория модуля (Required/Optional/Unwanted/Forbidden)
        /// Определяет можно ли закрыть модуль и его поведение
        /// </summary>
        [JsonProperty("category")]
        public ModuleCategory Category { get; set; }

        /// <summary>
        /// Позиция модуля при первом создании layout
        /// Используется только когда SerializedDockLayout == null
        /// </summary>
        [JsonProperty("preferredPosition")]
        public PreferredDockPosition PreferredPosition { get; set; }

        /// <summary>
        /// Вычисляемое свойство: можно ли закрыть модуль
        /// Required модули нельзя закрыть
        /// </summary>
        [JsonIgnore]
        public bool IsCloseable => Category != ModuleCategory.Required;
    }
}