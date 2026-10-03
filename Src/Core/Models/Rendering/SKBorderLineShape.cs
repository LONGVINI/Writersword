using System;

namespace Writersword.Core.Models.Rendering
{
    /// <summary>
    /// Рисунок линии рамки без привязки к графике: из каких черт она состоит и сколько
    /// места занимает поперёк себя.
    ///
    /// Один источник на всех, кто рисует или отмеряет рамку: вёрстка таблицы (высота
    /// строки и отступ текста от края ячейки), отрисовка документа и печати и образцы
    /// в меню ленты. Иначе строка таблицы под двойной рамкой вышла бы одной высоты, а
    /// сама рамка нарисовалась бы другой.
    ///
    /// Толщина линии — толщина её основной черты (w:sz у Word). Составные линии шире:
    /// двойная — две черты и просвет между ними, «тонкая и толстая» — основная черта,
    /// просвет и тонкая черта рядом.
    /// </summary>
    public static class SKBorderLineShape
    {
        // Виды линии — те же числа, что несёт SKTableBorderLineLayout.Style.
        public const int Solid = 0;
        public const int Dashed = 1;
        public const int Double = 2;
        public const int None = 3;
        public const int Dotted = 4;
        public const int Triple = 5;
        public const int Wave = 6;
        public const int ThreeDEmboss = 7;
        public const int ThreeDEngrave = 8;
        public const int Outset = 9;
        public const int Inset = 10;
        public const int DotDash = 11;
        public const int DotDotDash = 12;
        public const int DashSmallGap = 13;
        public const int DashDotStroked = 14;
        public const int ThinThickSmallGap = 15;
        public const int ThickThinSmallGap = 16;
        public const int ThinThickThinSmallGap = 17;
        public const int ThinThickMediumGap = 18;
        public const int ThickThinMediumGap = 19;
        public const int ThinThickThinMediumGap = 20;
        public const int ThinThickLargeGap = 21;
        public const int ThickThinLargeGap = 22;
        public const int ThinThickThinLargeGap = 23;
        public const int DoubleWave = 24;

        /// <summary>Толщина тонкой черты и малого просвета у линий «тонкая и толстая», pt.</summary>
        public const float ThinPt = 0.75f;

        /// <summary>Толщина толстой черты у линий с большим просветом, pt.</summary>
        public const float LargeGapThickPt = 1.5f;

        /// <summary>
        /// Наибольшая толщина линии двойной волны, pt: у Word двойная волна бывает
        /// только тонкой, и число из файла на её рисунок не влияет.
        /// </summary>
        public const float DoubleWaveStrokeMaxPt = 0.75f;

        /// <summary>Толщина линии, по которой рисуется каждая из двух волн двойной волны.</summary>
        public static float DoubleWaveStrokePt(float widthPt)
            => Math.Min(widthPt, DoubleWaveStrokeMaxPt);

        /// <summary>
        /// Место под двойную волну в толщинах её линии. Word отводит под неё больше, чем
        /// занимают сами волны: две волны стоят вплотную у наружного края, под ними просвет.
        /// </summary>
        public const float DoubleWaveSpanShare = 7f;

        // Волна Word по замерам: при линии 0,75 пт размах 2 пт и длина волны 4,75 пт,
        // при 1,5 пт — 3,4 и 6,8 пт. Черта волны тонкая при любой толщине линии.

        /// <summary>Размах волны от гребня до впадины, pt.</summary>
        public static float WaveSpanPt(float widthPt) => 0.7f + widthPt * 1.8f;

        /// <summary>Длина одной волны вдоль линии, pt.</summary>
        public static float WavePeriodPt(float widthPt) => 2.7f + widthPt * 2.7f;

        /// <summary>Толщина черты, которой рисуется волна, pt.</summary>
        public static float WaveStrokePt(float widthPt) => Math.Clamp(widthPt * 0.5f, 0.5f, 0.75f);

        // Наклонные штрихи (w:val="dashDotStroked"): длинный штрих и короткий по очереди.
        // У Word на пять долей длинного штриха приходится по одной доле на просвет,
        // короткий штрих и второй просвет; доля — чуть меньше половины толщины линии.
        // Сама полоса штрихов в полтора раза шире толщины линии.

        /// <summary>Ширина полосы наклонных штрихов в толщинах линии.</summary>
        public const float StrokedSpanShare = 1.5f;

        /// <summary>Длина длинного наклонного штриха в толщинах линии.</summary>
        public const float StrokedLongShare = 2.25f;

        /// <summary>Длина короткого наклонного штриха и каждого просвета в толщинах линии.</summary>
        public const float StrokedShortShare = 0.45f;

        /// <summary>
        /// Сколько места линия занимает поперёк себя, pt. Ноль — линии нет.
        /// </summary>
        public static float SpanPt(int style, float widthPt)
        {
            if (style == None || widthPt <= 0f) return 0f;

            float w = widthPt;
            switch (style)
            {
                case Double:
                    return w * 3f;

                case Triple:
                    return w * 5f;

                case Wave:
                    return WaveSpanPt(w);

                case DoubleWave:
                    return DoubleWaveStrokePt(w) * DoubleWaveSpanShare;

                case DashDotStroked:
                    return w * StrokedSpanShare;

                // Объёмные вдавленная и выпуклая: основная полоса и по краю в половину
                // толщины с каждой стороны.
                case ThreeDEmboss:
                case ThreeDEngrave:
                    return w * 2f;

                case ThinThickSmallGap:
                case ThickThinSmallGap:
                    return w + ThinPt * 2f;

                case ThinThickThinSmallGap:
                    return w + ThinPt * 4f;

                case ThinThickMediumGap:
                case ThickThinMediumGap:
                    return w * 2f;

                case ThinThickThinMediumGap:
                    return w * 3f;

                case ThinThickLargeGap:
                case ThickThinLargeGap:
                    return w + LargeGapThickPt + ThinPt;

                case ThinThickThinLargeGap:
                    return w * 2f + LargeGapThickPt + ThinPt * 2f;

                default:
                    return w;
            }
        }

        /// <summary>
        /// Черты составной линии по порядку: пары «середина черты от первого края линии,
        /// толщина черты», pt. Null — линия не из прямых сплошных черт (одна черта,
        /// штрихи, волна, объёмная) и рисуется своим способом.
        ///
        /// Первый край — верхний у горизонтальной линии и левый у вертикальной. Порядок
        /// черт один на всех сторонах ячейки, как у Word: у «тонкой и толстой»
        /// (thinThick) первой идёт толстая черта, у «толстой и тонкой» (thickThin) —
        /// тонкая, и на правой и нижней сторонах они не переворачиваются. Рамку с
        /// толстой чертой снаружи собирают из обеих: сверху и слева одна, снизу и
        /// справа другая.
        /// </summary>
        public static float[]? Strands(int style, float widthPt)
        {
            if (widthPt <= 0f) return null;

            float w = widthPt;
            float t = ThinPt;

            switch (style)
            {
                case Double:
                    return new[] { w * 0.5f, w, w * 2.5f, w };

                case Triple:
                    return new[] { w * 0.5f, w, w * 2.5f, w, w * 4.5f, w };

                // Малый просвет: тонкая черта и просвет по три четверти пункта.
                case ThinThickSmallGap:
                    return new[] { w * 0.5f, w, w + t * 1.5f, t };

                case ThickThinSmallGap:
                    return new[] { t * 0.5f, t, t * 2f + w * 0.5f, w };

                case ThinThickThinSmallGap:
                    return new[] { t * 0.5f, t, t * 2f + w * 0.5f, w, t * 3.5f + w, t };

                // Средний просвет: тонкая черта и просвет в половину толстой.
                case ThinThickMediumGap:
                    return new[] { w * 0.5f, w, w * 1.75f, w * 0.5f };

                case ThickThinMediumGap:
                    return new[] { w * 0.25f, w * 0.5f, w * 1.5f, w };

                case ThinThickThinMediumGap:
                    return new[] { w * 0.25f, w * 0.5f, w * 1.5f, w, w * 2.75f, w * 0.5f };

                // Большой просвет: черты постоянной толщины, просвет — в толщину линии.
                case ThinThickLargeGap:
                    return new[] { LargeGapThickPt * 0.5f, LargeGapThickPt, LargeGapThickPt + w + t * 0.5f, t };

                case ThickThinLargeGap:
                    return new[] { t * 0.5f, t, t + w + LargeGapThickPt * 0.5f, LargeGapThickPt };

                case ThinThickThinLargeGap:
                    return new[]
                    {
                        t * 0.5f, t,
                        t + w + LargeGapThickPt * 0.5f, LargeGapThickPt,
                        t + w + LargeGapThickPt + w + t * 0.5f, t
                    };

                default:
                    return null;
            }
        }

        /// <summary>
        /// Чередование штриха и просвета вдоль линии в её толщинах, начиная со штриха.
        /// Null — линия сплошная либо рисуется не штрихами.
        /// </summary>
        public static float[]? DashUnits(int style) => style switch
        {
            // Длины сняты с линий Word по пикселям: штрих и просвет — целое число
            // толщин линии, какой она выходит на экране.
            Dashed => new[] { 4f, 4f },
            Dotted => new[] { 1f, 1f },
            DashSmallGap => new[] { 4f, 1f },
            DotDash => new[] { 7f, 3f, 3f, 3f },
            DotDotDash => new[] { 6f, 2f, 2f, 2f, 2f, 2f },
            _ => null
        };

        /// <summary>
        /// Линия из нескольких прямых сплошных черт: двойная, тройная, «тонкая и толстая»
        /// во всех видах. В углах рамки черты таких линий сходятся каждая со своей.
        /// </summary>
        public static bool IsStranded(int style)
            => style == Double || style == Triple
               || (style >= ThinThickSmallGap && style <= ThinThickThinLargeGap);

        /// <summary>Линия — волна: одна или две.</summary>
        public static bool IsWave(int style) => style == Wave || style == DoubleWave;

        /// <summary>Объёмная линия: светлая и тёмная половины зависят от стороны ячейки.</summary>
        public static bool IsThreeD(int style) => style >= ThreeDEmboss && style <= Inset;
    }
}
