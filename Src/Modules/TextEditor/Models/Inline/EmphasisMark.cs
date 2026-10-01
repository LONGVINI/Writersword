namespace Writersword.Modules.TextEditor.Models.Inline
{
    /// <summary>
    /// Знак ударения над или под буквами — «Знак ударения» в окне шрифта Word (w:em).
    ///
    /// Числа закреплены: ими же вид передаётся отрисовке (SKRunSegment.EmphasisMark)
    /// и хранится в снимках, поэтому новые виды добавляются только в конец.
    /// </summary>
    public enum EmphasisMark
    {
        /// <summary>Знака нет.</summary>
        None = 0,

        /// <summary>Точка над каждой буквой.</summary>
        Dot = 1,

        /// <summary>Запятая над каждой буквой.</summary>
        Comma = 2,

        /// <summary>Кружок над каждой буквой.</summary>
        Circle = 3,

        /// <summary>Точка под каждой буквой.</summary>
        UnderDot = 4
    }
}
