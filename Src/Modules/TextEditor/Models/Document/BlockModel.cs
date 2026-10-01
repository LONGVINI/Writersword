using System;
using System.Text.Json.Serialization;

namespace Writersword.Modules.TextEditor.Models.Document
{
    /// <summary>
    /// Тип разрыва в документе.
    /// </summary>
    public enum BreakType
    {
        /// <summary>Нет разрыва.</summary>
        None = 0,
        /// <summary>Разрыв страницы.</summary>
        Page = 1,
        /// <summary>Разрыв колонки.</summary>
        Column = 2,
        /// <summary>Разрыв раздела — новый раздел начинается со следующей страницы.</summary>
        SectionNextPage = 3,
        /// <summary>Разрыв раздела — новый раздел начинается на той же странице (непрерывный).</summary>
        SectionContinuous = 4
    }

    /// <summary>
    /// Тип блока. Используется для полиморфной десериализации JSON.
    /// </summary>
    public enum BlockType
    {
        Paragraph = 0,
        Table = 1,
        Image = 2,
        Shape = 3,
        FloatingText = 4,
        Break = 5
    }

    /// <summary>
    /// Базовый класс для всех блоков документа.
    /// Блок — единица потока документа: параграф, таблица, изображение, фигура или разрыв.
    /// Каждый блок имеет стабильный Id для дельта-кеша.
    /// </summary>
    [JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
    [JsonDerivedType(typeof(ParagraphBlock), typeDiscriminator: "paragraph")]
    [JsonDerivedType(typeof(TableBlock), typeDiscriminator: "table")]
    [JsonDerivedType(typeof(ImageBlock), typeDiscriminator: "image")]
    [JsonDerivedType(typeof(ShapeBlock), typeDiscriminator: "shape")]
    [JsonDerivedType(typeof(FloatingTextBlock), typeDiscriminator: "floatingText")]
    [JsonDerivedType(typeof(BreakBlock), typeDiscriminator: "break")]
    public abstract class BlockModel
    {
        /// <summary>
        /// Уникальный идентификатор блока. Стабилен между сессиями.
        /// Используется как ключ в дельта-кеше.
        /// </summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>Тип блока. Дублирует discriminator для удобства в коде.</summary>
        [JsonIgnore]
        public abstract BlockType BlockType { get; }

        /// <summary>
        /// Хеш SHA-256 содержимого блока (верхнего уровня, без чанков).
        /// Для параграфов хешируются только свойства уровня параграфа,
        /// не текст (текст хешируется на уровне чанков).
        /// </summary>
        [JsonIgnore]
        public string Hash { get; set; } = string.Empty;
    }

    /// <summary>
    /// Блок-разрыв: страницы, колонки или раздела.
    /// Не содержит текста. Хранится как отдельный блок в потоке документа.
    /// </summary>
    public sealed class BreakBlock : BlockModel
    {
        public override BlockType BlockType => BlockType.Break;

        /// <summary>Тип разрыва.</summary>
        public BreakType BreakType { get; set; }

        /// <summary>
        /// Разрыв стоит внутри абзаца, который идёт перед ним: в Word это один абзац с
        /// разрывом, а модель режет его на кусок до разрыва, сам разрыв и кусок после.
        /// Отметка такого разрыва рисуется, как у Word, на последней строке куска до
        /// разрыва, сразу за его текстом, а не отдельной строкой под ним.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool InParagraph { get; set; }

        /// <summary>
        /// За разрывом продолжается тот же абзац Word: знака абзаца на строке разрыва
        /// нет, и отметка тянется до правого края текста. Без продолжения абзац
        /// кончается разрывом, и за короткой отметкой стоит знак абзаца.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool ContinuesParagraph { get; set; }

        /// <summary>
        /// Разрыв пришёл из разрыва колонки Word (w:br type="column") в разделе из одной
        /// колонки. Word ведёт такой разрыв на следующую страницу, поэтому он работает как
        /// разрыв страницы, но подписан «Разрыв столбца» и в .docx уходит разрывом колонки.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool FromColumnBreak { get; set; }
    }
}
