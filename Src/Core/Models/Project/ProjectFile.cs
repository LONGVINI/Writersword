using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Writersword.Core.Models.WorkModes;

namespace Writersword.Core.Models.Project
{
    public class ProjectFile
    {
        [JsonProperty("Title")]
        public string Title { get; set; } = "Untitled";

        [JsonProperty("Type")]
        public string Type { get; set; } = "Undefined";

        [JsonProperty("FormatVersion")]
        public string FormatVersion { get; set; } = "1.0";

        [JsonProperty("Id")]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [JsonProperty("CreatedAt")]
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        [JsonProperty("LastModified")]
        public DateTime LastModified { get; set; } = DateTime.Now;

        /// <summary>
        /// Данные модулей (ТОЛЬКО CustomData — контент документа).
        /// Ключ = тип модуля, Значение = CustomData модуля.
        /// </summary>
        [JsonProperty("ModulesData")]
        public Dictionary<string, object?> ModulesData { get; set; } = new();

        /// <summary>
        /// Палитра цветов проекта: закреплённые пользователем («+») и недавно
        /// использованные. Сохраняются вместе с проектом для повторного использования.
        /// </summary>
        [JsonProperty("ProjectPinnedColors")]
        public List<string> ProjectPinnedColors { get; set; } = new();

        [JsonProperty("ProjectRecentColors")]
        public List<string> ProjectRecentColors { get; set; } = new();

        /// <summary>
        /// Конфигурация WorkModes.
        /// НЕ сериализуется — загружается из workspace.json при открытии.
        /// </summary>
        [JsonIgnore]
        public List<WorkMode> WorkModes { get; set; } = new();
    }
}