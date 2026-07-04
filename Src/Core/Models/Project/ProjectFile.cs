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
        /// Доп. функция: включено ли кольцо вокруг аватара у ВСЕХ персонажей.
        /// Переключается одной кнопкой в редакторе цвета.
        /// </summary>
        [JsonProperty("AvatarRingsAll")]
        public bool AvatarRingsAll { get; set; } = false;

        /// <summary>Локальные (проектные) именованные палитры цветов.</summary>
        [JsonProperty("ProjectPalettes")]
        public List<ColorPalette> ProjectPalettes { get; set; } = new();

        /// <summary>
        /// Порядок отображения глобальных палитр в этом проекте: Id палитры ->
        /// позиция в общем списке. Лёгкие ссылки, чтобы перестановка глобальных
        /// палитр в одном проекте не смещала их в других. Ссылка на палитру,
        /// удалённую в другом проекте, вычищается при первом ненахождении.
        /// </summary>
        [JsonProperty("GlobalPaletteOrder")]
        public Dictionary<string, double> GlobalPaletteOrder { get; set; } = new();

        /// <summary>
        /// Конфигурация WorkModes.
        /// НЕ сериализуется — загружается из workspace.json при открытии.
        /// </summary>
        [JsonIgnore]
        public List<WorkMode> WorkModes { get; set; } = new();
    }
}