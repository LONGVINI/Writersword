using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Writersword.Core.Models.WorkModes;

namespace Writersword.Core.Models.Project
{
    /// <summary>
    /// Модель файла проекта (.writersword)
    /// Один проект = один документ
    /// Хранится в виде JSON внутри ZIP архива (файл project.json)
    /// Содержит только метаданные проекта и данные модулей
    /// Конфигурация UI (WorkModes, размеры окон) хранится в workspace.json
    /// </summary>
    public class ProjectFile
    {
        /// <summary>Название проекта</summary>
        [JsonProperty("Title")]
        public string Title { get; set; } = "Untitled";

        /// <summary>Тип проекта (Novel, Screenplay, etc)</summary>
        [JsonProperty("Type")]
        public string Type { get; set; } = "Undefined";

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
        /// Данные модулей (ТОЛЬКО CustomData)
        /// Ключ = тип модуля (TextEditor, Notes, Timer, Synonyms...)
        /// Значение = CustomData модуля (может быть строка, объект, массив - модуль сам решает)
        /// 
        /// Важно хранить ТОЛЬКО основные данные (CustomData)
        /// SessionData (курсор, скролл) НЕ сохраняется в .writersword - только в .wsasd кеш
        /// 
        /// Примеры:
        /// - TextEditor: строка с текстом документа
        /// - Notes: строка с текстом заметок
        /// - Timer: объект с временем и статусом
        /// - Synonyms: не сохраняет данные (вспомогательный модуль)
        /// </summary>
        [JsonProperty("ModulesData")]
        public Dictionary<string, object?> ModulesData { get; set; } = new();

        /// <summary>
        /// Загруженная конфигурация WorkModes для этого проекта
        /// НЕ сериализуется в project.json - загружается из workspace.json при открытии проекта
        /// Приоритет загрузки: LOCAL (workspace.json в ZIP) → GLOBAL (Settings.json) → DEFAULT (hardcoded)
        /// Заполняется в ProjectWorkflow.OpenDocumentAsync() через IWorkModeConfigurationService
        /// </summary>
        [JsonIgnore]
        public List<WorkMode> WorkModes { get; set; } = new();
    }
}