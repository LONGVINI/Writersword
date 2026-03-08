using System.Text.Json.Serialization;
using Writersword.Modules.TextEditor.Models.Styles;

namespace Writersword.Modules.TextEditor.Models.Document
{
    /// <summary>
    /// Режим обтекания плавающего объекта текстом.
    /// </summary>
    public enum WrapMode
    {
        /// <summary>Объект встроен в строку как символ.</summary>
        Inline = 0,
        /// <summary>Текст обтекает объект со всех сторон.</summary>
        Square = 1,
        /// <summary>Текст обтекает по контуру объекта.</summary>
        Tight = 2,
        /// <summary>Объект поверх текста.</summary>
        InFront = 3,
        /// <summary>Объект за текстом.</summary>
        Behind = 4
    }

    /// <summary>
    /// Якорь привязки плавающего объекта.
    /// </summary>
    public enum FloatAnchor
    {
        /// <summary>Позиция относительно страницы.</summary>
        Page = 0,
        /// <summary>Позиция относительно абзаца-якоря.</summary>
        Paragraph = 1,
        /// <summary>Позиция относительно символа-якоря.</summary>
        Character = 2
    }

    /// <summary>
    /// Изображение в документе.
    /// Файл изображения хранится в ZIP по пути TextEditor/Images/{ImageFileName}.
    /// </summary>
    public sealed class ImageBlock : BlockModel
    {
        public override BlockType BlockType => BlockType.Image;

        /// <summary>
        /// Имя файла изображения внутри ZIP (например "img_abc123.png").
        /// Полный путь в ZIP: TextEditor/Images/{ImageFileName}.
        /// </summary>
        public string ImageFileName { get; set; } = string.Empty;

        /// <summary>Ширина изображения в пунктах (пользовательски заданная).</summary>
        public double WidthPt { get; set; }

        /// <summary>Высота изображения в пунктах (пользовательски заданная).</summary>
        public double HeightPt { get; set; }

        /// <summary>Блокировка пропорций при изменении размера.</summary>
        public bool LockAspectRatio { get; set; } = true;

        /// <summary>Режим обтекания текстом.</summary>
        public WrapMode WrapMode { get; set; } = WrapMode.Inline;

        /// <summary>Якорь привязки при WrapMode != Inline.</summary>
        public FloatAnchor Anchor { get; set; } = FloatAnchor.Paragraph;

        /// <summary>Горизонтальное смещение от якоря в пунктах.</summary>
        public double OffsetXPt { get; set; }

        /// <summary>Вертикальное смещение от якоря в пунктах.</summary>
        public double OffsetYPt { get; set; }

        /// <summary>Z-порядок среди плавающих объектов (больше = поверх).</summary>
        public int ZOrder { get; set; }

        /// <summary>Альтернативный текст для доступности.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? AltText { get; set; }
    }

    /// <summary>
    /// Тип фигуры.
    /// </summary>
    public enum ShapeType
    {
        Rectangle = 0,
        Ellipse = 1,
        Line = 2,
        Arrow = 3,
        Callout = 4
    }

    /// <summary>
    /// Геометрическая фигура или стрелка.
    /// Всегда плавающая — не встраивается в поток текста как Inline.
    /// </summary>
    public sealed class ShapeBlock : BlockModel
    {
        public override BlockType BlockType => BlockType.Shape;

        public ShapeType ShapeType { get; set; }

        public double XPt { get; set; }
        public double YPt { get; set; }
        public double WidthPt { get; set; }
        public double HeightPt { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? FillColor { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? StrokeColor { get; set; }

        public double StrokeThicknessPt { get; set; } = 1.0;

        public FloatAnchor Anchor { get; set; } = FloatAnchor.Page;

        public int ZOrder { get; set; }

        /// <summary>Текст внутри фигуры (для прямоугольников, выносок).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? InnerText { get; set; }

        public bool IsGrouped { get; set; }

        /// <summary>Id группы если объект входит в группу.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? GroupId { get; set; }
    }

    /// <summary>
    /// Плавающая надпись — текстовый блок в произвольном месте страницы.
    /// Содержит параграфы как обычный поток документа.
    /// </summary>
    public sealed class FloatingTextBlock : BlockModel
    {
        public override BlockType BlockType => BlockType.FloatingText;

        public double XPt { get; set; }
        public double YPt { get; set; }
        public double WidthPt { get; set; }
        public double HeightPt { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? BackgroundColor { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? BorderColor { get; set; }

        public double BorderThicknessPt { get; set; }

        public FloatAnchor Anchor { get; set; } = FloatAnchor.Page;

        public int ZOrder { get; set; }

        public System.Collections.Generic.List<ParagraphBlock> Paragraphs { get; set; } = new()
        {
            new ParagraphBlock()
        };

        public bool IsGrouped { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? GroupId { get; set; }
    }
}
