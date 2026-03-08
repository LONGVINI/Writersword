using System;
using System.Text.Json.Serialization;

namespace Writersword.Modules.TextEditor.Models.Inline
{
    /// <summary>
    /// Тип аннотации поверх текста.
    /// Аннотации хранятся отдельным слоем и могут перекрывать границы параграфов.
    /// </summary>
    public enum InlineAnnotationType
    {
        /// <summary>Цветовое выделение (маркер) поверх нескольких параграфов.</summary>
        Highlight = 0,

        /// <summary>Привязка к персонажу из модуля Characters.</summary>
        CharacterMark = 1,

        /// <summary>Привязка к событию таймлайна.</summary>
        TimelineMark = 2,

        /// <summary>Ключевое слово / термин мира.</summary>
        KeywordMark = 3,

        /// <summary>Привязка к локации или предмету.</summary>
        WorldItemMark = 4,

        /// <summary>Комментарий на полях.</summary>
        Comment = 5,

        /// <summary>Именованная закладка.</summary>
        Bookmark = 6,

        /// <summary>Сноска (ссылка на текст внизу страницы).</summary>
        Footnote = 7,

        /// <summary>Концевая сноска (ссылка на текст в конце документа/главы).</summary>
        Endnote = 8,

        /// <summary>Гиперссылка.</summary>
        Hyperlink = 9,

        /// <summary>Перекрёстная ссылка на другой раздел документа.</summary>
        CrossReference = 10
    }

    /// <summary>
    /// Позиция внутри документа — указывает на конкретный символ в конкретном чанке параграфа.
    /// </summary>
    public sealed class DocumentPosition
    {
        /// <summary>Id параграфа (ParagraphBlock.Id).</summary>
        public Guid BlockId { get; set; }

        /// <summary>Id чанка внутри параграфа (TextChunk.Id).</summary>
        public Guid ChunkId { get; set; }

        /// <summary>Смещение символа внутри чанка (0-based).</summary>
        public int Offset { get; set; }
    }

    /// <summary>
    /// Аннотация — разметка поверх текста.
    /// Может перекрывать границы параграфов и чанков.
    /// Хранится в отдельном разделе документа, не внутри параграфов.
    /// </summary>
    public sealed class InlineAnnotation
    {
        /// <summary>Уникальный идентификатор аннотации. Стабилен между сессиями.</summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>SHA-256 хеш содержимого аннотации для дельта-кеша.</summary>
        [JsonIgnore]
        public string Hash { get; set; } = string.Empty;

        /// <summary>Тип аннотации.</summary>
        public InlineAnnotationType Type { get; set; }

        /// <summary>Начало диапазона аннотации.</summary>
        public DocumentPosition Start { get; set; } = new();

        /// <summary>Конец диапазона аннотации (не включительно).</summary>
        public DocumentPosition End { get; set; } = new();

        // --- Данные в зависимости от типа ---

        /// <summary>
        /// Цвет выделения (#RRGGBB). Используется для Highlight.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Color { get; set; }

        /// <summary>
        /// Id связанного объекта в другом модуле (персонаж, событие таймлайна и т.д.).
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LinkedEntityId { get; set; }

        /// <summary>
        /// Отображаемая метка для аннотаций таймлайна (например "A2 — выполнен").
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? DisplayLabel { get; set; }

        /// <summary>
        /// Текст комментария или сноски.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Content { get; set; }

        /// <summary>URL для гиперссылок.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Url { get; set; }

        /// <summary>Имя закладки.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? BookmarkName { get; set; }

        /// <summary>
        /// Id автора аннотации (провод для совместной работы).
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? AuthorId { get; set; }

        /// <summary>Дата создания аннотации.</summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
