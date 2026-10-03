using Writersword.Core.Models.Rendering;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Rendering
{
    /// <summary>
    /// Вид границы из модели документа — в вид линии для отрисовки
    /// (<see cref="SKBorderLineShape"/>). Один перевод на всех: вёрстка таблицы, её
    /// отрисовка и образцы линий в ленте берут рисунок линии по одному и тому же числу.
    /// </summary>
    public static class BorderLineCodes
    {
        /// <summary>Вид линии для отрисовки по виду границы из модели.</summary>
        public static int Of(BorderStyle style) => style switch
        {
            BorderStyle.None => SKBorderLineShape.None,
            BorderStyle.Dashed => SKBorderLineShape.Dashed,
            BorderStyle.Double => SKBorderLineShape.Double,
            BorderStyle.Dotted => SKBorderLineShape.Dotted,
            BorderStyle.Triple => SKBorderLineShape.Triple,
            BorderStyle.Wave => SKBorderLineShape.Wave,
            BorderStyle.ThreeDEmboss => SKBorderLineShape.ThreeDEmboss,
            BorderStyle.ThreeDEngrave => SKBorderLineShape.ThreeDEngrave,
            BorderStyle.Outset => SKBorderLineShape.Outset,
            BorderStyle.Inset => SKBorderLineShape.Inset,
            BorderStyle.DotDash => SKBorderLineShape.DotDash,
            BorderStyle.DotDotDash => SKBorderLineShape.DotDotDash,
            BorderStyle.DashSmallGap => SKBorderLineShape.DashSmallGap,
            BorderStyle.DashDotStroked => SKBorderLineShape.DashDotStroked,
            BorderStyle.ThinThickSmallGap => SKBorderLineShape.ThinThickSmallGap,
            BorderStyle.ThickThinSmallGap => SKBorderLineShape.ThickThinSmallGap,
            BorderStyle.ThinThickThinSmallGap => SKBorderLineShape.ThinThickThinSmallGap,
            BorderStyle.ThinThickMediumGap => SKBorderLineShape.ThinThickMediumGap,
            BorderStyle.ThickThinMediumGap => SKBorderLineShape.ThickThinMediumGap,
            BorderStyle.ThinThickThinMediumGap => SKBorderLineShape.ThinThickThinMediumGap,
            BorderStyle.ThinThickLargeGap => SKBorderLineShape.ThinThickLargeGap,
            BorderStyle.ThickThinLargeGap => SKBorderLineShape.ThickThinLargeGap,
            BorderStyle.ThinThickThinLargeGap => SKBorderLineShape.ThinThickThinLargeGap,
            BorderStyle.DoubleWave => SKBorderLineShape.DoubleWave,
            _ => SKBorderLineShape.Solid
        };

        /// <summary>
        /// Старшинство вида линии в споре двух ячеек за общую границу: чем больше, тем
        /// сильнее. Порядок — как у Word: объёмные и волнистые старше составных, составные
        /// старше двойной и сплошной. Точки и штрихи у Word уступают сплошной линии той
        /// же толщины, поэтому стоят ниже неё.
        /// </summary>
        public static double ConflictRank(BorderStyle style) => style switch
        {
            BorderStyle.None => 0.0,
            BorderStyle.Dotted => 0.3,
            BorderStyle.Dashed => 0.6,
            BorderStyle.Single => 1.0,
            BorderStyle.Thick => 2.0,
            BorderStyle.Double => 3.0,
            BorderStyle.DotDash => 6.0,
            BorderStyle.DotDotDash => 7.0,
            BorderStyle.Triple => 8.0,
            BorderStyle.ThinThickSmallGap => 9.0,
            BorderStyle.ThickThinSmallGap => 10.0,
            BorderStyle.ThinThickThinSmallGap => 11.0,
            BorderStyle.ThinThickMediumGap => 12.0,
            BorderStyle.ThickThinMediumGap => 13.0,
            BorderStyle.ThinThickThinMediumGap => 14.0,
            BorderStyle.ThinThickLargeGap => 15.0,
            BorderStyle.ThickThinLargeGap => 16.0,
            BorderStyle.ThinThickThinLargeGap => 17.0,
            BorderStyle.Wave => 18.0,
            BorderStyle.DoubleWave => 19.0,
            BorderStyle.DashSmallGap => 20.0,
            BorderStyle.DashDotStroked => 21.0,
            BorderStyle.ThreeDEmboss => 22.0,
            BorderStyle.ThreeDEngrave => 23.0,
            BorderStyle.Outset => 24.0,
            BorderStyle.Inset => 25.0,
            _ => 1.0
        };

        /// <summary>Сила линии: старшинство вида на толщину (см. <see cref="Stronger"/>).</summary>
        public static float Weight(BorderStyle style, double thicknessPt)
            => style == BorderStyle.None ? 0f : (float)(ConflictRank(style) * thicknessPt);

        /// <summary>
        /// Какая из двух линий остаётся на общей границе двух ячеек. Линии нет — остаётся
        /// другая. Иначе сильнее та, у которой больше произведение старшинства вида на
        /// толщину; при равенстве — вид, стоящий в порядке раньше, затем более тёмный
        /// цвет. Совсем равные линии — остаётся первая.
        /// </summary>
        public static (BorderStyle Style, double ThicknessPt, string? Color) Stronger(
            (BorderStyle Style, double ThicknessPt, string? Color) current,
            (BorderStyle Style, double ThicknessPt, string? Color) candidate)
        {
            if (candidate.Style == BorderStyle.None) return current;
            if (current.Style == BorderStyle.None) return candidate;

            double currentRank = ConflictRank(current.Style);
            double candidateRank = ConflictRank(candidate.Style);

            double currentWeight = currentRank * current.ThicknessPt;
            double candidateWeight = candidateRank * candidate.ThicknessPt;

            if (candidateWeight > currentWeight + 1e-6) return candidate;
            if (currentWeight > candidateWeight + 1e-6) return current;

            if (candidateRank < currentRank) return candidate;
            if (currentRank < candidateRank) return current;

            return ColorBrightness(candidate.Color) < ColorBrightness(current.Color) ? candidate : current;
        }

        /// <summary>
        /// Яркость цвета линии для выбора более тёмной: красный и синий по разу, зелёный
        /// дважды. Цвет «авто» и нечитаемый код считаются чёрным.
        /// </summary>
        private static int ColorBrightness(string? color)
        {
            if (string.IsNullOrWhiteSpace(color)) return 0;

            string hex = color.Trim().TrimStart('#');
            if (hex.Length != 6
                || !int.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out int rgb))
                return 0;

            int red = (rgb >> 16) & 0xFF;
            int green = (rgb >> 8) & 0xFF;
            int blue = rgb & 0xFF;
            return red + blue + green * 2;
        }

        /// <summary>
        /// Сколько места граница занимает поперёк себя в pt: по этому числу строка
        /// таблицы отводит место под рамку.
        /// </summary>
        public static float SpanPt(BorderStyle style, double thicknessPt)
            => style == BorderStyle.None
                ? 0f
                : SKBorderLineShape.SpanPt(Of(style), (float)thicknessPt);
    }
}
