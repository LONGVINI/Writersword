using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Writersword.Core.Models.Project
{
    /// <summary>
    /// Модель файла проекта (.writersword)
    /// Один проект = один документ
    /// Хранится в виде JSON внутри ZIP архива
    /// </summary>
    public class ProjectFile
    {
        /// <summary>Название проекта</summary>
        [JsonProperty("Title")]
        public string Title { get; set; } = "Untitled";

        /// <summary>Тип проекта (Novel, Screenplay, etc)</summary>
        [JsonProperty("Type")]
        public string Type { get; set; } = "Novel";

        /// <summary>Версия формата файла проекта</summary>
        [JsonProperty("FormatVersion")]
        public string FormatVersion { get; set; } = "1.0";

        /// <summary>
        /// Уникальный идентификатор проекта (GUID)
        /// Используется в кеше для защиты от путаницы проектов
        /// Генерируется автоматически при создании проекта
        /// </summary>
        [JsonProperty("Id")]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>Дата создания проекта</summary>
        [JsonProperty("CreatedAt")]
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        /// <summary>Дата последнего изменения</summary>
        [JsonProperty("LastModified")]
        public DateTime LastModified { get; set; } = DateTime.Now;

        /// <summary>
        /// Данные модулей
        /// Ключ = тип модуля (TextEditor, Characters, Timeline...)
        /// Значение = данные модуля (может быть строка, объект, массив)
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

        /// <summary>Настройки WorkModes для этого проекта</summary>
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
    /// Конфигурация WorkMode (режима работы)
    /// Содержит информацию о том какие модули открыты и где расположены
    /// </summary>
    public class UserWorkModeConfig
    {
        /// <summary>ID WorkMode (например "Writing", "Editing")</summary>
        [JsonProperty("Id")]
        public string Id { get; set; } = "";

        /// <summary>Название WorkMode</summary>
        [JsonProperty("Title")]
        public string Title { get; set; } = "";

        /// <summary>Активен ли этот WorkMode</summary>
        [JsonProperty("IsActive")]
        public bool IsActive { get; set; }

        /// <summary>Слоты модулей (какие модули открыты и где)</summary>
        [JsonProperty("ModuleSlots")]
        public List<UserModuleSlotConfig> ModuleSlots { get; set; } = new();
    }

    /// <summary>
    /// Конфигурация слота модуля
    /// Определяет какой модуль открыт и где он расположен в интерфейсе
    /// </summary>
    public class UserModuleSlotConfig
    {
        /// <summary>Тип модуля (TextEditor, Timer, Characters...)</summary>
        [JsonProperty("ModuleType")]
        public string ModuleType { get; set; } = "";

        /// <summary>Виден ли модуль</summary>
        [JsonProperty("IsVisible")]
        public bool IsVisible { get; set; }

        /// <summary>Позиция модуля (Left, Right, Center, Float)</summary>
        [JsonProperty("Position")]
        public string? Position { get; set; }
    }

    /// <summary>
    /// Конфигурация размеров окон
    /// Сохраняет размеры и позиции разделителей (splitters)
    /// </summary>
    public class UserWindowLayoutConfig
    {
        /// <summary>Ширина окна в пикселях</summary>
        [JsonProperty("Width")]
        public int Width { get; set; }

        /// <summary>Высота окна в пикселях</summary>
        [JsonProperty("Height")]
        public int Height { get; set; }

        /// <summary>Позиции разделителей (splitters) в процентах</summary>
        [JsonProperty("SplitterPositions")]
        public List<double> SplitterPositions { get; set; } = new();
    }
}