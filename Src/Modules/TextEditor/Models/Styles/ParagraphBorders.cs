using System;
using System.Text.Json.Serialization;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Models.Styles
{
    /// <summary>
    /// Одна линия рамки абзаца: вид, цвет, толщина и зазор до текста.
    ///
    /// Вид — тот же перечень, что у рамок ячеек таблицы: рамка есть рамка, и
    /// заводить для абзаца свой список видов линий значило бы держать два списка
    /// об одном и том же.
    /// </summary>
    public sealed class ParagraphBorderLine
    {
        /// <summary>Вид линии.</summary>
        public BorderStyle Style { get; set; } = BorderStyle.Single;

        /// <summary>
        /// Цвет HEX. null — «авто»: линия берёт цвет текста листа и темнеет или
        /// светлеет вместе с ним при смене вида.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Color { get; set; }

        /// <summary>Толщина линии в пунктах.</summary>
        public double WidthPt { get; set; } = 0.5;

        /// <summary>Зазор между линией и текстом в пунктах.</summary>
        public double SpacePt { get; set; }

        /// <summary>Линия рисуется.</summary>
        [JsonIgnore]
        public bool IsVisible => Style != BorderStyle.None && WidthPt > 0;

        public ParagraphBorderLine Clone() => (ParagraphBorderLine)MemberwiseClone();

        /// <summary>Две линии одинаковы. Невидимые равны друг другу при любых прочих полях.</summary>
        public static bool Same(ParagraphBorderLine? a, ParagraphBorderLine? b)
        {
            bool aOn = a is { IsVisible: true };
            bool bOn = b is { IsVisible: true };
            if (!aOn && !bOn) return true;
            if (aOn != bOn) return false;

            return a!.Style == b!.Style
                && Math.Abs(a.WidthPt - b.WidthPt) < 0.01
                && Math.Abs(a.SpacePt - b.SpacePt) < 0.01
                && string.Equals(a.Color ?? string.Empty, b.Color ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Рамка абзаца — линии сверху, снизу, слева и справа. Черта слева у цитаты,
    /// линия под заголовком, рамка вокруг врезки: всё это рамка абзаца, как в Word.
    ///
    /// Хранится у каждого абзаца своя и рисуется ровно такой, какой записана. Word
    /// сливает соседние абзацы с одинаковой рамкой в одну коробку при показе; здесь
    /// это делается один раз при импорте — у внутренних абзацев группы верхняя и
    /// нижняя линии снимаются. Иначе между абзацами одной врезки появлялись бы
    /// линии, которых в документе не видно. Боковые линии соседних абзацев с
    /// одинаковой рамкой отрисовка соединяет сама, через интервалы между ними.
    /// </summary>
    public sealed class ParagraphBorders
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ParagraphBorderLine? Top { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ParagraphBorderLine? Bottom { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ParagraphBorderLine? Left { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ParagraphBorderLine? Right { get; set; }

        /// <summary>Нет ни одной видимой линии.</summary>
        [JsonIgnore]
        public bool IsEmpty =>
            Top is not { IsVisible: true }
            && Bottom is not { IsVisible: true }
            && Left is not { IsVisible: true }
            && Right is not { IsVisible: true };

        /// <summary>Копия со своими линиями: правка копии не трогает оригинал.</summary>
        public ParagraphBorders Clone() => new()
        {
            Top = Top?.Clone(),
            Bottom = Bottom?.Clone(),
            Left = Left?.Clone(),
            Right = Right?.Clone()
        };

        /// <summary>Рамки совпадают по всем четырём сторонам.</summary>
        public static bool Same(ParagraphBorders? a, ParagraphBorders? b)
        {
            bool aEmpty = a is null || a.IsEmpty;
            bool bEmpty = b is null || b.IsEmpty;
            if (aEmpty && bEmpty) return true;
            if (aEmpty != bEmpty) return false;

            return ParagraphBorderLine.Same(a!.Top, b!.Top)
                && ParagraphBorderLine.Same(a.Bottom, b.Bottom)
                && ParagraphBorderLine.Same(a.Left, b.Left)
                && ParagraphBorderLine.Same(a.Right, b.Right);
        }
    }

    /// <summary>
    /// Стороны рамки абзаца — для кнопки «Рамка» в ленте: какие линии ставить или
    /// снимать и какие из них уже видны у выделенных абзацев.
    /// </summary>
    [Flags]
    public enum ParagraphBorderSides
    {
        None = 0,
        Top = 1,
        Bottom = 2,
        Left = 4,
        Right = 8,

        /// <summary>Рамка со всех сторон вокруг абзаца или группы абзацев.</summary>
        Box = Top | Bottom | Left | Right
    }

    /// <summary>
    /// «Перо» рамки: вид, цвет, толщина и зазор, которыми кнопка «Рамка» ставит новые
    /// линии и перерисовывает уже стоящие. Отдельно от линии потому, что зазор у пера
    /// может быть «как в Word»: у линий сверху и снизу 1 пт, у боковых 4 пт.
    /// </summary>
    public sealed class ParagraphBorderPen
    {
        /// <summary>Зазор «как в Word» для линий сверху и снизу, в пунктах.</summary>
        public const double DefaultVerticalSpacePt = 1.0;

        /// <summary>Зазор «как в Word» для боковых линий, в пунктах.</summary>
        public const double DefaultSideSpacePt = 4.0;

        /// <summary>Вид линии.</summary>
        public BorderStyle Style { get; init; } = BorderStyle.Single;

        /// <summary>Цвет HEX. null — «авто»: цвет текста листа.</summary>
        public string? Color { get; init; }

        /// <summary>Толщина линии в пунктах.</summary>
        public double WidthPt { get; init; } = 0.5;

        /// <summary>Зазор до текста в пунктах. null — «как в Word», свой у каждой стороны.</summary>
        public double? SpacePt { get; init; }

        /// <summary>Линия этим пером для одной стороны абзаца.</summary>
        public ParagraphBorderLine LineFor(ParagraphBorderSides side) => new()
        {
            Style = Style,
            Color = string.IsNullOrWhiteSpace(Color) ? null : Color,
            WidthPt = WidthPt,
            SpacePt = SpacePt ?? (side is ParagraphBorderSides.Top or ParagraphBorderSides.Bottom
                ? DefaultVerticalSpacePt
                : DefaultSideSpacePt)
        };
    }
}
