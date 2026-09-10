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
        /// Высота строки по метрикам шрифта. Word считает одинарный интервал как
        /// сумму подъёма, спуска и межстрочного зазора гарнитуры: у Times New Roman
        /// 12 пт это 13,8 пт, а без зазора выходит 13,3 — на четыре процента ниже.
        /// На трёхстах страницах такая недостача сокращает документ на полтора
        /// десятка листов, поэтому зазор берётся из метрик Skia как есть.
        /// </summary>
        public float Resolve(float ascent, float descent, float leading)
        {
            float natural = ascent + descent + Math.Max(leading, 0f);

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
            float natural = ascent + descent + Math.Max(leading, 0f);
            return Math.Max(Resolve(ascent, descent, leading), natural);
        }
    }
}
