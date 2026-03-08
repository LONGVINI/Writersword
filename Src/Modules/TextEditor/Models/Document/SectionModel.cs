using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Writersword.Modules.TextEditor.Models.Page;

namespace Writersword.Modules.TextEditor.Models.Document
{
    /// <summary>
    /// Раздел документа.
    /// Каждый раздел имеет собственные настройки страницы, колонок и колонтитулов
    /// которые переопределяют настройки документа.
    /// Документ содержит минимум один раздел.
    /// </summary>
    public sealed class SectionModel
    {
        /// <summary>Уникальный идентификатор раздела.</summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>Хеш SHA-256 для дельта-кеша (хешируются свойства раздела без блоков).</summary>
        [JsonIgnore]
        public string Hash { get; set; } = string.Empty;

        /// <summary>
        /// Настройки страницы для этого раздела.
        /// Null — унаследовать от DocumentModel.PageSettings.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public PageSettings? PageSettings { get; set; }

        /// <summary>
        /// Настройки колонок для этого раздела.
        /// Null — унаследовать от DocumentModel.ColumnSettings.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ColumnSettings? ColumnSettings { get; set; }

        /// <summary>Верхний колонтитул раздела.</summary>
        public HeaderFooterModel Header { get; set; } = new();

        /// <summary>Нижний колонтитул раздела.</summary>
        public HeaderFooterModel Footer { get; set; } = new();

        /// <summary>
        /// Блоки раздела в порядке следования.
        /// Каждый блок — параграф, таблица, изображение, фигура или разрыв.
        /// </summary>
        public List<BlockModel> Blocks { get; set; } = new();

        /// <summary>
        /// Плавающие объекты раздела (изображения с обтеканием, фигуры, надписи).
        /// Хранятся отдельно от потока блоков.
        /// </summary>
        public List<BlockModel> FloatingObjects { get; set; } = new();
    }
}
