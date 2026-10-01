using SkiaSharp;

namespace Writersword.Core.Models.Rendering
{
    /// <summary>Штрих контура букв при отрисовке.</summary>
    public enum SKOutlineDash
    {
        Solid = 0,
        Dash = 1,
        Dot = 2,
        DashDot = 3,
        LongDash = 4
    }

    /// <summary>
    /// Контур букв при отрисовке. Цвет уже со своей прозрачностью, толщина — в пунктах.
    /// Outside — контур снаружи букв (буквы сохраняют толщину), Hollow — букв без заливки.
    /// </summary>
    public sealed record SKOutlineEffect(SKColor Color, float WidthPt, SKOutlineDash Dash, bool Outside, bool Hollow);

    /// <summary>
    /// Тень букв при отрисовке. Цвет — вместе с прозрачностью. Угол — по часовой
    /// стрелке от направления вправо. IsLong — сплошной след букв на всё расстояние.
    /// </summary>
    public sealed record SKShadowEffect(SKColor Color, float BlurPt, float DistancePt, float AngleDeg, bool IsLong);

    /// <summary>Свечение вокруг букв при отрисовке. Цвет — вместе с прозрачностью.</summary>
    public sealed record SKGlowEffect(SKColor Color, float RadiusPt);

    /// <summary>
    /// Отражение под буквами при отрисовке. StartOpacity — непрозрачность у самих
    /// букв (0..1), Size — видимая доля высоты букв (0..1).
    /// </summary>
    public sealed record SKReflectionEffect(float StartOpacity, float Size, float DistancePt, float BlurPt);

    /// <summary>
    /// Настраиваемые эффекты букв сегмента. Записи сравниваются по значению: сегменты
    /// с одинаковыми эффектами сливаются, с разными — нет.
    /// </summary>
    public sealed record SKTextEffects(
        SKOutlineEffect? Outline,
        SKShadowEffect? Shadow,
        SKGlowEffect? Glow,
        SKReflectionEffect? Reflection);
}
