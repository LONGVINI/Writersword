using System;
using Writersword.Modules.TextEditor.Models.Styles;

namespace Writersword.Modules.TextEditor.Rendering
{
    /// <summary>
    /// Межстрочный интервал абзаца в том виде, в каком его понимает Word:
    /// правило плюс значение. Без правила значение всегда трактовалось как
    /// множитель, поэтому «Точно 14 пт» превращалось в четырнадцатикратную
    /// высоту строки.
    /// </summary>
    internal readonly struct SKLineSpacing
    {
        /// <summary>Правило вычисления высоты строки.</summary>
        public LineSpacingRule Rule { get; }

        /// <summary>Множитель для <see cref="LineSpacingRule.Auto"/>, пункты для остальных правил.</summary>
        public float Value { get; }

        public SKLineSpacing(LineSpacingRule rule, float value)
        {
            Rule = rule;
            Value = value;
        }

        /// <summary>Одинарный интервал.</summary>
        public static SKLineSpacing Single => new(LineSpacingRule.Auto, 1f);

        /// <summary>
        /// Высота строки по метрикам шрифта. Естественная высота — ascent + descent,
        /// как их отдаёт Skia: на Windows это уже полная высота строки гарнитуры
        /// (usWinAscent/usWinDescent), в которую межстрочный зазор входит. Отдельно
        /// прибавлять leading нельзя — зазор учитывался бы дважды, и строки
        /// оказывались заметно выше вордовских.
        /// </summary>
        public float Resolve(float ascent, float descent, float leading)
        {
            float natural = ascent + descent;

            return Rule switch
            {
                LineSpacingRule.Exact => Math.Max(Value, 1f),
                LineSpacingRule.AtLeast => Math.Max(natural, Value),
                _ => natural * (Value > 0f ? Value : 1f)
            };
        }

        /// <summary>
        /// Верхняя оценка высоты строки для проб обтекания: точная высота известна
        /// только после разбора сегментов строки, но проба не должна занижать её.
        /// </summary>
        public float ResolveProbe(float ascent, float descent, float leading)
        {
            float natural = ascent + descent;
            return Math.Max(Resolve(ascent, descent, leading), natural);
        }
    }
}
