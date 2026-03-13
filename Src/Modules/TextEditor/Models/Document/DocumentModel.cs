using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.Models.Page;
using Writersword.Modules.TextEditor.Models.Styles;

namespace Writersword.Modules.TextEditor.Models.Document
{
    /// <summary>
    /// Режим отображения документа в редакторе.
    /// </summary>
    public enum EditorViewMode
    {
        /// <summary>Листы с полями, физический размер бумаги — как в Word.</summary>
        Page = 0,
        /// <summary>Черновик — вся ширина без листов.</summary>
        Draft = 1,
        /// <summary>Веб-документ — вся ширина с переносом слов.</summary>
        Web = 2,
        /// <summary>Режим чтения — узкая колонка по центру, комфортная для чтения.</summary>
        Reading = 3
    }

    /// <summary>
    /// Корневая модель документа.
    /// Сериализуется в JSON и хранится в ZIP по пути TextEditor/document.json.
    /// </summary>
    public sealed class DocumentModel
    {
        // --- Метаданные ---

        /// <summary>Уникальный идентификатор документа.</summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>Заголовок документа (для оглавления и отображения).</summary>
        public string Title { get; set; } = "Untitled";

        /// <summary>Версия схемы для совместимости при обновлениях формата.</summary>
        public int SchemaVersion { get; set; } = 1;

        // --- Провода для совместной работы (логика не реализована) ---

        /// <summary>Id автора последней правки.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? AuthorId { get; set; }

        /// <summary>Инкрементальная версия для разрешения конфликтов при слиянии.</summary>
        public long RevisionId { get; set; }

        /// <summary>UTC-время последней синхронизации с сервером.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTime? LastSyncedAt { get; set; }

        // --- Настройки документа ---

        /// <summary>Настройки страницы по умолчанию (переопределяются на уровне раздела).</summary>
        public PageSettings PageSettings { get; set; } = new();

        /// <summary>Настройки колонок по умолчанию.</summary>
        public ColumnSettings ColumnSettings { get; set; } = new();

        /// <summary>
        /// Визуальные настройки листа (цвет фона, текста).
        /// Не влияют на экспорт/печать.
        /// </summary>
        public CanvasSettings CanvasSettings { get; set; } = new();

        /// <summary>Последний активный режим отображения.</summary>
        public EditorViewMode ViewMode { get; set; } = EditorViewMode.Page;

        /// <summary>Последний активный масштаб (0.25 – 5.0).</summary>
        public double Zoom { get; set; } = 1.0;

        // --- Стили ---

        /// <summary>
        /// Все стили документа: встроенные + пользовательские.
        /// Инициализируются из <see cref="DocumentStyle.CreateBuiltInStyles"/> при создании нового документа.
        /// </summary>
        public List<DocumentStyle> Styles { get; set; } = new();

        // --- Содержимое ---

        /// <summary>
        /// Разделы документа. Минимум один раздел.
        /// Порядок разделов определяет порядок отображения.
        /// </summary>
        public List<SectionModel> Sections { get; set; } = new();

        /// <summary>
        /// Аннотации документа: выделения, метки персонажей, закладки, сноски и т.д.
        /// Хранятся отдельным слоем — могут перекрывать границы параграфов и чанков.
        /// </summary>
        public List<InlineAnnotation> Annotations { get; set; } = new();

        /// <summary>
        /// Правила автозамены, специфичные для этого документа.
        /// Дополняют глобальные правила из TextEditorSettings.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<AutoReplaceRule>? DocumentAutoReplaceRules { get; set; }

        /// <summary>
        /// Создаёт новый документ с одним разделом и одним пустым параграфом.
        /// </summary>
        public static DocumentModel CreateNew(string title = "Untitled")
        {
            var doc = new DocumentModel
            {
                Title = title,
                Styles = new List<DocumentStyle>(DocumentStyle.CreateBuiltInStyles())
            };

            var section = new SectionModel();
            section.Blocks.Add(new ParagraphBlock());
            doc.Sections.Add(section);

            return doc;
        }

        /// <summary>
        /// Возвращает стиль по имени или null если стиль не найден.
        /// </summary>
        public DocumentStyle? FindStyle(string name)
        {
            foreach (var style in Styles)
                if (style.Name == name) return style;
            return null;
        }
    }

    /// <summary>
    /// Правило автозамены текста.
    /// </summary>
    public sealed class AutoReplaceRule
    {
        /// <summary>Исходная строка (например "--").</summary>
        public string From { get; set; } = string.Empty;

        /// <summary>Строка замены (например "—").</summary>
        public string To { get; set; } = string.Empty;

        /// <summary>Правило активно.</summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>Правило является встроенным и не может быть удалено.</summary>
        public bool IsBuiltIn { get; set; }
    }
}
