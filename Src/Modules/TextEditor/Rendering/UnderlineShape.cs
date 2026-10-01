using Writersword.Modules.TextEditor.Models.Inline;

namespace Writersword.Modules.TextEditor.Rendering
{
    /// <summary>
    /// Рисунок подчёркивания без привязки к графике: из скольких линий оно состоит,
    /// какой они толщины, прерывистые ли и волна ли это.
    ///
    /// Один источник на всех, кто рисует линию: документ (SkiaSharp), печать в PDF и
    /// образцы в меню ленты (Avalonia). Иначе образец в меню разошёлся бы с тем, что
    /// ложится под текст.
    ///
    /// Все длины — в долях базовой толщины линии. Базовая толщина зависит от кегля
    /// фрагмента (<see cref="BaseThickness(float)"/>), поэтому рисунок растёт вместе с буквами.
    /// </summary>
    public readonly struct UnderlineShape
    {
        /// <summary>Во сколько раз линия толще базовой: 1 — обычная, 2 — жирная.</summary>
        public float Weight { get; }

        /// <summary>Две линии одна под другой.</summary>
        public bool IsDouble { get; }

        /// <summary>Волна вместо прямой.</summary>
        public bool IsWave { get; }

        /// <summary>
        /// Штрихи и просветы по очереди, начиная со штриха, в долях базовой толщины.
        /// Null — сплошная линия.
        /// </summary>
        public float[]? Dash { get; }

        /// <summary>Пробелы между словами не подчёркиваются.</summary>
        public bool WordsOnly { get; }

        /// <summary>Размах волны от средней линии до гребня, в долях базовой толщины.</summary>
        public float WaveAmplitude { get; }

        /// <summary>Длина одной волны, в долях базовой толщины.</summary>
        public float WaveLength { get; }

        /// <summary>
        /// Расстояние между средними линиями двойной прямой в долях базовой толщины.
        /// Как у Word: две линии в базовую толщину, между ними просвет почти в две.
        /// </summary>
        public const float DoubleGap = 2.7f;

        /// <summary>Расстояние между средними линиями двойной волны в долях базовой толщины.</summary>
        public const float WaveDoubleGap = 2.2f;

        /// <summary>Сдвиг линии под базовую линию текста в долях кегля.</summary>
        public const float OffsetFromBaseline = 0.12f;

        private UnderlineShape(float weight, bool isDouble, bool isWave, float[]? dash,
            bool wordsOnly, float waveAmplitude = 0f, float waveLength = 0f)
        {
            Weight = weight;
            IsDouble = isDouble;
            IsWave = isWave;
            Dash = dash;
            WordsOnly = wordsOnly;
            WaveAmplitude = waveAmplitude;
            WaveLength = waveLength;
        }

        /// <summary>
        /// Во сколько раз толще линия под жирным фрагментом. Word утолщает только саму
        /// линию: длина точек, штрихов и просветов у жирного текста та же, что у обычного.
        /// </summary>
        public const float BoldFactor = 2f;

        /// <summary>
        /// Базовая толщина линии в пунктах для фрагмента данного кегля — та же, какой
        /// подчёркивание рисовалось всегда.
        /// </summary>
        public static float BaseThickness(float fontSizePt)
            => System.Math.Max(0.5f, fontSizePt * 0.05f);

        /// <summary>
        /// Толщина линии с учётом жирности фрагмента: под жирным текстом линия
        /// в <see cref="BoldFactor"/> раз толще. Рисунок линии (точки, штрихи, просветы,
        /// волна, зазор двойной) меряется базовой толщиной без жирности.
        /// </summary>
        public static float BaseThickness(float fontSizePt, bool bold)
            => BaseThickness(fontSizePt) * (bold ? BoldFactor : 1f);

        /// <summary>Рисунок линии для вида подчёркивания.</summary>
        public static UnderlineShape Of(UnderlineStyle style) => style switch
        {
            // Размеры сняты с Word: толщины и рисунок прерывистых линий в долях базовой
            // толщины. Жирные варианты — та же длина точек, штрихов и просветов, только
            // линия толще.
            UnderlineStyle.Words => new UnderlineShape(1f, false, false, null, true),
            UnderlineStyle.Double => new UnderlineShape(1f, true, false, null, false),
            UnderlineStyle.Thick => new UnderlineShape(2.5f, false, false, null, false),

            UnderlineStyle.Dotted => new UnderlineShape(1f, false, false, new[] { 2f, 2f }, false),
            UnderlineStyle.DottedHeavy => new UnderlineShape(2.5f, false, false, new[] { 2f, 2f }, false),

            UnderlineStyle.Dash => new UnderlineShape(1f, false, false, new[] { 10f, 4.5f }, false),
            UnderlineStyle.DashedHeavy => new UnderlineShape(2.5f, false, false, new[] { 10f, 4.5f }, false),

            UnderlineStyle.DashLong => new UnderlineShape(1f, false, false, new[] { 19f, 10f }, false),
            UnderlineStyle.DashLongHeavy => new UnderlineShape(2.5f, false, false, new[] { 19f, 10f }, false),

            UnderlineStyle.DotDash => new UnderlineShape(1f, false, false, new[] { 10f, 3f, 3f, 3f }, false),
            UnderlineStyle.DashDotHeavy => new UnderlineShape(2.5f, false, false, new[] { 10f, 3f, 3f, 3f }, false),

            UnderlineStyle.DotDotDash => new UnderlineShape(1f, false, false, new[] { 7f, 2f, 2f, 2f, 2f, 2f }, false),
            UnderlineStyle.DashDotDotHeavy => new UnderlineShape(2.5f, false, false, new[] { 7f, 2f, 2f, 2f, 2f, 2f }, false),

            UnderlineStyle.Wave => new UnderlineShape(1f, false, true, null, false, 1.2f, 6f),
            UnderlineStyle.WavyHeavy => new UnderlineShape(2f, false, true, null, false, 1.6f, 7f),
            UnderlineStyle.WavyDouble => new UnderlineShape(1f, true, true, null, false, 1f, 6f),

            _ => new UnderlineShape(1f, false, false, null, false)
        };

        /// <summary>Вид по числу, которым его несёт сегмент отрисовки.</summary>
        public static UnderlineStyle StyleFromCode(int code)
            => System.Enum.IsDefined(typeof(UnderlineStyle), code) ? (UnderlineStyle)code : UnderlineStyle.None;
    }
}
