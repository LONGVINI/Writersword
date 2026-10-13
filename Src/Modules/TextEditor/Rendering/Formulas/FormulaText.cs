namespace Writersword.Modules.TextEditor.Rendering.Formulas
{
    /// <summary>
    /// Формула текстом: запись командами LaTeX в XML формулы Word и обратно. Через
    /// неё работает окно правки формулы — человек правит текст, а документ хранит
    /// формулу Word.
    /// </summary>
    public static class FormulaText
    {
        /// <summary>
        /// XML формулы Word по записи формулы. Ошибка записи —
        /// <see cref="FormulaSyntaxException"/> с позицией ошибки.
        /// </summary>
        public static string ToOmml(string latex)
            => OmmlWriter.Write(LatexParser.Parse(latex));

        /// <summary>Запись формулы Word командами LaTeX для правки.</summary>
        public static string FromOmml(string? omml)
            => LatexWriter.Write(OmmlParser.Parse(omml));

        /// <summary>Формула пуста: в ней нет ни одного знака.</summary>
        public static bool IsEmpty(string latex)
            => LatexParser.Parse(latex).IsEmpty;
    }
}
