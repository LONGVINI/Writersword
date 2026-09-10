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
        public TextEditorPageSettings PageSettings { get; set; } = new();

        /// <summary>Настройки колонок по умолчанию.</summary>
        public ColumnSettings ColumnSettings { get; set; } = new();

        /// <summary>
        /// Визуальные настройки листа (цвет фона, текста).
        /// Не влияют на экспорт/печать.
        ///
        /// В файл не пишется: см. пояснение у <see cref="ViewMode"/>.
        /// </summary>
        [JsonIgnore]
        public CanvasSettings CanvasSettings { get; set; } = new();

        /// <summary>
        /// Последний активный режим отображения.
        ///
        /// В файл рукописи не пишется, и это осознанно. Режим показа, масштаб и цвет
        /// листа — это про глаза человека и его экран, а не про книгу: на ноутбуке в
        /// поезде удобен один масштаб, за столом другой, и рукопись тут ни при чём.
        /// Хранятся они в общих настройках программы и потому одинаковы во всех
        /// документах; в файле их значения остались бы от чужого монитора и меняли бы
        /// вид книги при каждом открытии.
        ///
        /// Поле не удалено, чтобы старые файлы читались без ошибок: их значения просто
        /// пропускаются при разборе.
        /// </summary>
        [JsonIgnore]
        public EditorViewMode ViewMode { get; set; } = EditorViewMode.Page;

        /// <summary>
        /// Последний активный масштаб (0.25 – 5.0). В файл не пишется — см. выше.
        /// </summary>
        [JsonIgnore]
        public double Zoom { get; set; } = 1.0;

        /// <summary>
        /// Шаг табуляции по умолчанию в пунктах. Символ табуляции в абзаце, которому
        /// своих позиций не задано, уходит к ближайшей отметке этого шага.
        ///
        /// 35.4 пункта — это 1.25 см, шаг Word по умолчанию: рукопись, приехавшая
        /// оттуда, не должна расползаться от одной только смены шага.
        /// </summary>
        public double DefaultTabStopPt { get; set; } = 35.4;

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
        /// Оглавления рукописи. Обычно одно, но их может быть несколько — общее в начале
        /// и частные по частям книги, — поэтому список.
        ///
        /// Здесь лежат только настройки. Сами строки оглавления живут в потоке документа
        /// обычными абзацами с пометкой TocOwnerId: держать их ещё и тут значило бы иметь
        /// две правды об одном и том же.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<Toc.TocSettings>? TableOfContents { get; set; }

        /// <summary>
        /// Правила автозамены, специфичные для этого документа.
        /// Дополняют глобальные правила из TextEditorSettings.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<AutoReplaceRule>? DocumentAutoReplaceRules { get; set; }

        /// <summary>
        /// Виды чтения, приложенные к рукописи. Уезжают вместе с ней: открывший
        /// документ увидит книгу такой, какой её задумал автор, даже если у него
        /// самого этого вида не заведено.
        ///
        /// На содержание, печать и экспорт не влияют — это оформление чтения.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<Settings.ReadingTheme>? ReadingThemes { get; set; }

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
