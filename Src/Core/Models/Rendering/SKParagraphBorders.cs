using System;

namespace Writersword.Core.Models.Rendering
{
    /// <summary>
    /// Одна линия рамки абзаца глазами отрисовки: вид, цвет, толщина и зазор до текста.
    ///
    /// Вид — тем же числом, что и у рамок ячеек в модели документа: 0 — нет линии,
    /// 1 — сплошная, 2 — двойная, 3 — штрих, 4 — точки, 5 — жирная.
    /// </summary>
    public sealed class SKParagraphBorderLine
    {
        public const int StyleNone = 0;
        public const int StyleSingle = 1;
        public const int StyleDouble = 2;
        public const int StyleDashed = 3;
        public const int StyleDotted = 4;
        public const int StyleThick = 5;

        /// <summary>Вид линии.</summary>
        public int Style { get; init; } = StyleSingle;

        /// <summary>Цвет HEX. null — «авто»: цвет текста листа.</summary>
        public string? Color { get; init; }

        /// <summary>Толщина одной черты в pt.</summary>
        public float WidthPt { get; init; } = 0.5f;

        /// <summary>Зазор между линией и текстом в pt.</summary>
        public float SpacePt { get; init; }

        /// <summary>Линия рисуется.</summary>
        public bool IsVisible => Style != StyleNone && WidthPt > 0f;

        /// <summary>
        /// Сколько места линия занимает поперёк себя. У двойной — две черты и
        /// просвет между ними той же толщины.
        /// </summary>
        public float DrawnWidthPt => !IsVisible ? 0f : (Style == StyleDouble ? WidthPt * 3f : WidthPt);

        /// <summary>Линия вместе с зазором до текста — столько она отодвигает текст.</summary>
        public float ExtentPt => IsVisible ? SpacePt + DrawnWidthPt : 0f;

        /// <summary>Две линии рисуются одинаково.</summary>
        public static bool Same(SKParagraphBorderLine? a, SKParagraphBorderLine? b)
        {
            bool aOn = a is { IsVisible: true };
            bool bOn = b is { IsVisible: true };
            if (!aOn && !bOn) return true;
            if (aOn != bOn) return false;

            return a!.Style == b!.Style
                && Math.Abs(a.WidthPt - b.WidthPt) < 0.01f
                && Math.Abs(a.SpacePt - b.SpacePt) < 0.01f
                && string.Equals(a.Color ?? string.Empty, b.Color ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Рамка абзаца глазами отрисовки. Строится при вёрстке абзаца из его свойств и
    /// едет вместе с раскладкой: и экран, и печать рисуют рамку по ней, не заглядывая
    /// в модель документа.
    /// </summary>
    public sealed class SKParagraphBorders
    {
        public SKParagraphBorderLine? Top { get; init; }
        public SKParagraphBorderLine? Bottom { get; init; }
        public SKParagraphBorderLine? Left { get; init; }
        public SKParagraphBorderLine? Right { get; init; }

        /// <summary>Нет ни одной видимой линии.</summary>
        public bool IsEmpty =>
            Top is not { IsVisible: true }
            && Bottom is not { IsVisible: true }
            && Left is not { IsVisible: true }
            && Right is not { IsVisible: true };

        /// <summary>
        /// Боковые линии двух абзацев совпадают — между такими абзацами боковая
        /// черта идёт без разрыва, как в Word у абзацев с одинаковой рамкой.
        /// </summary>
        public static bool SameSides(SKParagraphBorders? a, SKParagraphBorders? b)
        {
            if (a is null || b is null) return false;

            bool anySide = a.Left is { IsVisible: true } || a.Right is { IsVisible: true };
            if (!anySide) return false;

            return SKParagraphBorderLine.Same(a.Left, b.Left)
                && SKParagraphBorderLine.Same(a.Right, b.Right);
        }
    }
}
