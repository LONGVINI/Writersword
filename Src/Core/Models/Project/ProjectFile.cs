using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Writersword.Core.Models.Project
{
    /// <summary>
    /// Модель файла проекта (.writersword)
    /// Один проект = один документ
    /// </summary>
    public class ProjectFile
    {
        /// <summary>Название проекта</summary>
        [JsonProperty("Title")]
        public string Title { get; set; } = "Untitled";

        /// <summary>Тип проекта (Novel, Screenplay, etc)</summary>
        [JsonProperty("Type")]
        public string Type { get; set; } = "Novel";

        /// <summary>Версия формата файла</summary>
        [JsonProperty("FormatVersion")]
        public string FormatVersion { get; set; } = "2.0";

        /// <summary>Дата создания проекта</summary>
        [JsonProperty("CreatedAt")]
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        /// <summary>Дата последнего изменения</summary>
        [JsonProperty("LastModified")]
        public DateTime LastModified { get; set; } = DateTime.Now;

        /// <summary>
        /// Данные модулей
        /// Ключ = тип модуля (TextEditor, Characters, Timeline...)
        /// Значение = JSON данные модуля
        /// </summary>
        [JsonProperty("ModulesData")]
        public Dictionary<string, object?> ModulesData { get; set; } = new();

        /// <summary>Конфигурация пользователя (WorkModes, размеры окон)</summary>
        [JsonProperty("UserConfig")]
        public UserConfiguration? UserConfig { get; set; }
    }

    /// <summary>
    /// Конфигурация пользователя для проекта
    /// Содержит настройки интерфейса (WorkModes, размеры окон)
    /// </summary>
    public class UserConfiguration
    {
        /// <summary>Включены ли пользовательские настройки</summary>
        [JsonProperty("IsEnabled")]
        public bool IsEnabled { get; set; } = true;

        /// <summary>Настройки WorkModes</summary>
        [JsonProperty("WorkModes")]
        public List<UserWorkModeConfig> WorkModes { get; set; } = new();

        /// <summary>ID активного WorkMode</summary>
        [JsonProperty("ActiveWorkModeId")]
        public string? ActiveWorkModeId { get; set; }

        /// <summary>Настройки размеров окон</summary>
        [JsonProperty("WindowLayout")]
        public UserWindowLayoutConfig? WindowLayout { get; set; }
    }

    /// <summary>
    /// Конфигурация WorkMode
    /// </summary>
    public class UserWorkModeConfig
    {
        [JsonProperty("Id")]
        public string Id { get; set; } = "";

        [JsonProperty("Title")]
        public string Title { get; set; } = "";

        [JsonProperty("IsActive")]
        public bool IsActive { get; set; }

        [JsonProperty("ModuleSlots")]
        public List<UserModuleSlotConfig> ModuleSlots { get; set; } = new();
    }

    /// <summary>
    /// Конфигурация слота модуля
    /// </summary>
    public class UserModuleSlotConfig
    {
        [JsonProperty("ModuleType")]
        public string ModuleType { get; set; } = "";

        [JsonProperty("IsVisible")]
        public bool IsVisible { get; set; }

        [JsonProperty("Position")]
        public string? Position { get; set; }
    }

    /// <summary>
    /// Конфигурация размеров окон
    /// </summary>
    public class UserWindowLayoutConfig
    {
        [JsonProperty("Width")]
        public int Width { get; set; }

        [JsonProperty("Height")]
        public int Height { get; set; }

        [JsonProperty("SplitterPositions")]
        public List<double> SplitterPositions { get; set; } = new();
    }
}