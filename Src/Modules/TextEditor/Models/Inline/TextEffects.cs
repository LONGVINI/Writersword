using System.Text.Json.Serialization;

namespace Writersword.Modules.TextEditor.Models.Inline
{
    /// <summary>Штрих контура букв — те же виды, что у Word (w14:prstDash).</summary>
    public enum OutlineDash
    {
        Solid = 0,
        Dash = 1,
        Dot = 2,
        DashDot = 3,
        LongDash = 4
    }

    /// <summary>
    /// Где лежит контур относительно края буквы. У Word только «по краю» — линия
    /// делится краем пополам. «Снаружи» есть только у Writersword: буквы сохраняют
    /// свою толщину, контур обводит их снаружи.
    /// </summary>
    public enum OutlinePlacement
    {
        Center = 0,
        Outside = 1
    }

    /// <summary>
    /// Вид линии рамки вокруг знаков (w:bdr). Числа закреплены: ими же вид уходит
    /// отрисовке (SKRunSegment.CharBorderStyle) и хранится в снимках.
    /// </summary>
    public enum CharBorderStyle
    {
        Single = 0,
        Double = 1,
        Dotted = 2,
        Dashed = 3,
        Thick = 4
    }

    /// <summary>
    /// Контур букв с настройками — «Контур текста» Word 2010+ (w14:textOutline).
    /// Цвет — #RRGGBB, толщина — в пунктах.
    /// </summary>
    public sealed record TextOutlineEffect
    {
        public string Color { get; init; } = "#000000";

        public double WidthPt { get; init; } = 0.75;

        public OutlineDash Dash { get; init; } = OutlineDash.Solid;

        /// <summary>«Снаружи» — только Writersword; в Word контур ляжет по краю букв.</summary>
        public OutlinePlacement Placement { get; init; } = OutlinePlacement.Center;

        /// <summary>
        /// Полые буквы: заливки нет, виден только контур (w14:textFill с w14:noFill).
        /// </summary>
        public bool Hollow { get; init; }
    }

    /// <summary>
    /// Тень букв с настройками — «Тень» Word 2010+ (w14:shadow).
    /// Угол — по часовой стрелке от направления вправо, как у Word: 45° — вниз-вправо.
    /// </summary>
    public sealed record TextShadowEffect
    {
        public string Color { get; init; } = "#000000";

        /// <summary>Прозрачность тени: 0 — сплошная, 1 — невидимая.</summary>
        public double Transparency { get; init; } = 0.6;

        public double BlurPt { get; init; } = 3.0;

        public double DistancePt { get; init; } = 2.0;

        public double AngleDeg { get; init; } = 45.0;

        /// <summary>
        /// Длинная тень: сплошной след букв на всё расстояние, без размытия. Только
        /// Writersword; в Word ляжет обычной тенью без размытия на то же расстояние.
        /// </summary>
        public bool IsLong { get; init; }
    }

    /// <summary>Свечение вокруг букв — «Свечение» Word 2010+ (w14:glow).</summary>
    public sealed record TextGlowEffect
    {
        public string Color { get; init; } = "#FFC000";

        /// <summary>Прозрачность свечения: 0 — сплошное, 1 — невидимое.</summary>
        public double Transparency { get; init; } = 0.6;

        public double RadiusPt { get; init; } = 5.0;
    }

    /// <summary>
    /// Отражение букв под строкой — «Отражение» Word 2010+ (w14:reflection):
    /// перевёрнутая копия, тающая книзу.
    /// </summary>
    public sealed record TextReflectionEffect
    {
        /// <summary>Прозрачность у самых букв: 0 — как буквы, 1 — невидимое.</summary>
        public double Transparency { get; init; } = 0.5;

        /// <summary>Какая доля высоты букв видна в отражении, 0..1.</summary>
        public double Size { get; init; } = 0.45;

        /// <summary>Зазор между буквами и отражением, в пунктах.</summary>
        public double DistancePt { get; init; } = 0.0;

        public double BlurPt { get; init; } = 0.5;
    }

    /// <summary>
    /// Настраиваемые эффекты букв фрагмента. Каждый эффект — отдельная запись: null
    /// значит «эффекта нет». Записи неизменяемые и сравниваются по значению, поэтому
    /// одинаковые эффекты соседних фрагментов сливаются в один ран.
    /// </summary>
    public sealed record TextEffects
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public TextOutlineEffect? Outline { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public TextShadowEffect? Shadow { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public TextGlowEffect? Glow { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public TextReflectionEffect? Reflection { get; init; }

        /// <summary>Ни одного эффекта нет.</summary>
        [JsonIgnore]
        public bool IsEmpty => Outline is null && Shadow is null && Glow is null && Reflection is null;

        /// <summary>
        /// Есть то, чего Word не умеет: контур снаружи букв или длинная тень. Такие
        /// эффекты уходят в .docx упрощённо, а полные настройки — отдельным разделом,
        /// который читает только Writersword.
        /// </summary>
        [JsonIgnore]
        public bool HasWriterswordOnlyParts =>
            Outline?.Placement == OutlinePlacement.Outside || Shadow?.IsLong == true;

        /// <summary>Пустой набор превращается в null — фрагмент без эффектов.</summary>
        public static TextEffects? Normalize(TextEffects? effects)
            => effects is null || effects.IsEmpty ? null : effects;
    }

    /// <summary>
    /// Рамка вокруг знаков целиком — для окна настройки эффектов.
    /// Enabled = false — рамки нет, остальное не важно.
    /// </summary>
    public sealed record CharBorderSettings(bool Enabled, string? Color, double WidthPt, CharBorderStyle Style);

    /// <summary>
    /// Всё, что показывает и отдаёт окно «Эффекты текста»: настраиваемые эффекты,
    /// рамка знаков и то, чем рисовать образец (гарнитура и цвет текста под кареткой).
    /// </summary>
    public sealed record TextEffectsDialogState(
        TextEffects? Effects,
        CharBorderSettings Border,
        string FontFamily,
        string TextColor);
}
