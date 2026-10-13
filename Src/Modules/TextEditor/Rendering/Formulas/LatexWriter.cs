using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Writersword.Modules.TextEditor.Rendering.Formulas
{
    /// <summary>
    /// Запись узлов формулы командами LaTeX — обратная разбору (LatexParser). Так
    /// любая формула документа, в том числе пришедшая из Word, открывается в окне
    /// правки формулы текстом, который разбор прочтёт в ту же формулу.
    ///
    /// Греческие буквы и привычные знаки остаются знаками: «α ≤ β» читается легче,
    /// чем «\alpha \le \beta», а разбор понимает оба вида.
    /// </summary>
    internal static class LatexWriter
    {
        public static string Write(MathRow row)
        {
            // Формула из строк одна под другой: строки через \\, без окружения.
            if (row.Items.Count == 1 && row.Items[0] is MathMatrixNode { IsEquationArray: true } array)
            {
                var lines = new List<string>(array.Rows.Count);
                foreach (var line in array.Rows) lines.Add(WriteCells(line).Trim());
                return string.Join(" \\\\ ", lines);
            }

            return Clean(WriteRow(row));
        }

        private static string WriteCells(List<MathRow> cells)
        {
            var builder = new StringBuilder();
            foreach (var cell in cells) builder.Append(WriteRow(cell));
            return Clean(builder.ToString());
        }

        private static string WriteRow(MathRow? row)
        {
            if (row is null) return string.Empty;

            var builder = new StringBuilder();
            foreach (var node in row.Items) Append(builder, WriteNode(node));
            return builder.ToString();
        }

        /// <summary>
        /// Приставляет кусок записи: между командой и буквой за ней нужен пробел,
        /// иначе разбор прочтёт их одной командой.
        /// </summary>
        private static void Append(StringBuilder builder, string piece)
        {
            if (piece.Length == 0) return;

            if (builder.Length > 0 && char.IsLetter(piece[0]) && EndsWithCommand(builder))
                builder.Append(' ');

            builder.Append(piece);
        }

        private static bool EndsWithCommand(StringBuilder builder)
        {
            int i = builder.Length - 1;
            if (i < 0 || !char.IsLetter(builder[i])) return false;
            while (i >= 0 && char.IsLetter(builder[i])) i--;
            return i >= 0 && builder[i] == '\\';
        }

        /// <summary>Аргумент в фигурных скобках.</summary>
        private static string Group(MathRow? row) => "{" + Clean(WriteRow(row)) + "}";

        /// <summary>
        /// Основание или индекс: один знак — без скобок, остальное — в скобках.
        /// </summary>
        private static string Operand(MathRow? row)
        {
            string text = Clean(WriteRow(row));
            if (text.Length == 0) return "{}";

            bool single = text.Length == 1
                || (text.Length == 2 && char.IsSurrogatePair(text, 0))
                || IsSingleCommand(text);
            return single ? text : "{" + text + "}";
        }

        private static bool IsSingleCommand(string text)
        {
            if (text.Length < 2 || text[0] != '\\') return false;
            for (int i = 1; i < text.Length; i++)
                if (!char.IsLetter(text[i])) return false;
            return true;
        }

        private static string Clean(string text) => text.Trim();

        private static string WriteNode(MathNode node)
        {
            switch (node)
            {
                case MathRow nested:
                    return "{" + Clean(WriteRow(nested)) + "}";

                case MathRunNode run:
                    return WriteRun(run);

                case MathFractionNode fraction:
                    return fraction.Kind switch
                    {
                        MathFractionKind.NoBar => "\\genfrac{}{}{0pt}{}" + Group(fraction.Numerator) + Group(fraction.Denominator),
                        MathFractionKind.Skewed => "\\sfrac" + Group(fraction.Numerator) + Group(fraction.Denominator),
                        MathFractionKind.Linear => Operand(fraction.Numerator) + "/" + Operand(fraction.Denominator),
                        _ => "\\frac" + Group(fraction.Numerator) + Group(fraction.Denominator)
                    };

                case MathScriptNode script:
                {
                    if (script.Pre)
                        return "\\prescript" + Group(script.Sup) + Group(script.Sub) + Group(script.Base);

                    var builder = new StringBuilder(Operand(script.Base));
                    if (script.Sub is not null) builder.Append('_').Append(Operand(script.Sub));
                    if (script.Sup is not null)
                    {
                        string sup = Clean(WriteRow(script.Sup));
                        if (sup == "′") builder.Append('\'');
                        else if (sup == "″") builder.Append("''");
                        else builder.Append('^').Append(Operand(script.Sup));
                    }
                    return builder.ToString();
                }

                case MathRadicalNode radical:
                    return radical.Degree is not null
                        ? "\\sqrt[" + Clean(WriteRow(radical.Degree)) + "]" + Group(radical.Radicand)
                        : "\\sqrt" + Group(radical.Radicand);

                case MathNaryNode nary:
                {
                    string command = LatexSymbols.NaryCommand(nary.OperatorChar);
                    var builder = new StringBuilder();

                    if (command.Length > 0)
                    {
                        builder.Append('\\').Append(command);
                        bool integral = nary.OperatorChar >= 0x222B && nary.OperatorChar <= 0x2233;
                        if (integral && nary.LimitsAboveBelow) builder.Append("\\limits");
                        if (!integral && !nary.LimitsAboveBelow) builder.Append("\\nolimits");
                    }
                    else
                    {
                        // Знак без своей команды: обычным знаком с индексами.
                        builder.Append(char.ConvertFromUtf32(nary.OperatorChar));
                    }

                    if (nary.Sub is not null) builder.Append('_').Append(Group(nary.Sub));
                    if (nary.Sup is not null) builder.Append('^').Append(Group(nary.Sup));
                    // Пустое выражение под знаком — пустые скобки: место для Tab в окне формулы.
                    builder.Append(nary.Body.IsEmpty ? "{}" : Group(nary.Body));
                    return builder.ToString();
                }

                case MathDelimiterNode delimiter:
                    return WriteDelimiter(delimiter);

                case MathFunctionNode function:
                    return WriteFunction(function);

                case MathLimitNode limit:
                {
                    if (limit.Base.Items.Count == 1 && limit.Base.Items[0] is MathGroupCharNode group
                        && group.Top == limit.Upper && GroupCommand(group.Char) is { } groupCommand)
                    {
                        return "\\" + groupCommand + Group(group.Base) + (limit.Upper ? "^" : "_") + Group(limit.Limit);
                    }

                    return (limit.Upper ? "\\overset" : "\\underset") + Group(limit.Limit) + Group(limit.Base);
                }

                case MathAccentNode accent:
                {
                    string? command = LatexSymbols.AccentCommand(accent.AccentChar);
                    return command is not null
                        ? "\\" + command + Group(accent.Base)
                        : "\\overset{" + char.ConvertFromUtf32(accent.AccentChar) + "}" + Group(accent.Base);
                }

                case MathBarNode bar:
                    return (bar.Top ? "\\overline" : "\\underline") + Group(bar.Base);

                case MathGroupCharNode group:
                {
                    if (GroupCommand(group.Char) is { } command)
                        return "\\" + command + Group(group.Base);

                    return (group.Top ? "\\overset{" : "\\underset{")
                        + char.ConvertFromUtf32(group.Char) + "}" + Group(group.Base);
                }

                case MathBoxNode box:
                {
                    bool allBorders = box.BorderTop && box.BorderBottom && box.BorderLeft && box.BorderRight;
                    bool anyBorder = box.BorderTop || box.BorderBottom || box.BorderLeft || box.BorderRight;

                    if (!anyBorder && box.StrikeDiagonalUp && box.StrikeDiagonalDown) return "\\xcancel" + Group(box.Base);
                    if (!anyBorder && box.StrikeDiagonalUp) return "\\cancel" + Group(box.Base);
                    if (!anyBorder && box.StrikeDiagonalDown) return "\\bcancel" + Group(box.Base);
                    if (allBorders) return "\\boxed" + Group(box.Base);
                    return Group(box.Base);
                }

                case MathMatrixNode matrix:
                    return WriteMatrix(matrix, matrix.IsEquationArray ? "aligned" : "matrix");

                case MathPhantomNode phantom:
                {
                    if (phantom.Show) return Group(phantom.Base);
                    if (phantom.ZeroWidth) return "\\vphantom" + Group(phantom.Base);
                    if (phantom.ZeroAscent && phantom.ZeroDescent) return "\\hphantom" + Group(phantom.Base);
                    return "\\phantom" + Group(phantom.Base);
                }
            }

            return string.Empty;
        }

        private static string? GroupCommand(int codepoint) => codepoint switch
        {
            0x23DE => "overbrace",
            0x23DF => "underbrace",
            0x23B4 => "overbracket",
            0x23B5 => "underbracket",
            0x23DC => "overparen",
            0x23DD => "underparen",
            _ => null
        };

        private static string WriteMatrix(MathMatrixNode matrix, string environment)
        {
            var lines = new List<string>(matrix.Rows.Count);
            foreach (var line in matrix.Rows)
            {
                var cells = new List<string>(line.Count);
                // Пустая клетка пишется пустыми скобками: иначе пустая последняя строка
                // читалась бы как лишний перенос и пропадала, а Tab в окне формулы не
                // находил бы её места.
                foreach (var cell in line) cells.Add(Slot(Clean(WriteRow(cell))));
                lines.Add(string.Join(" & ", cells));
            }

            return "\\begin{" + environment + "} " + string.Join(" \\\\ ", lines) + " \\end{" + environment + "}";
        }

        /// <summary>Пустое место конструкции — пустые скобки, непустое — как есть.</summary>
        private static string Slot(string text) => text.Length == 0 ? "{}" : text;

        private static string WriteDelimiter(MathDelimiterNode delimiter)
        {
            // Скобки вокруг одной матрицы — окружение матрицы со своими скобками.
            if (delimiter.Items.Count == 1
                && delimiter.Items[0].Items.Count == 1
                && delimiter.Items[0].Items[0] is MathMatrixNode matrix)
            {
                if (!matrix.IsEquationArray)
                {
                    string? environment = (delimiter.OpenChar, delimiter.CloseChar) switch
                    {
                        ('(', ')') => "pmatrix",
                        ('[', ']') => "bmatrix",
                        ('{', '}') => "Bmatrix",
                        ('|', '|') => "vmatrix",
                        (0x2016, 0x2016) => "Vmatrix",
                        _ => null
                    };
                    if (environment is not null) return WriteMatrix(matrix, environment);
                }
                else if (delimiter.OpenChar == '{' && delimiter.CloseChar == 0)
                {
                    return WriteMatrix(matrix, "cases");
                }
                else if (delimiter.OpenChar == 0 && delimiter.CloseChar == '}')
                {
                    return WriteMatrix(matrix, "rcases");
                }
            }

            // Биномиальный коэффициент: дробь без черты в круглых скобках.
            if (delimiter.OpenChar == '(' && delimiter.CloseChar == ')'
                && delimiter.Items.Count == 1
                && delimiter.Items[0].Items.Count == 1
                && delimiter.Items[0].Items[0] is MathFractionNode { Kind: MathFractionKind.NoBar } binomial)
            {
                return "\\binom" + Group(binomial.Numerator) + Group(binomial.Denominator);
            }

            var builder = new StringBuilder();
            builder.Append("\\left").Append(LatexSymbols.DelimiterText(delimiter.OpenChar)).Append(' ');
            for (int i = 0; i < delimiter.Items.Count; i++)
            {
                if (i > 0)
                    builder.Append(" \\middle").Append(LatexSymbols.DelimiterText(delimiter.SeparatorChar)).Append(' ');
                builder.Append(Slot(Clean(WriteRow(delimiter.Items[i]))));
            }
            builder.Append(" \\right").Append(LatexSymbols.DelimiterText(delimiter.CloseChar));
            return builder.ToString();
        }

        private static string WriteFunction(MathFunctionNode function)
        {
            string name;

            if (function.Name.Items.Count == 1
                && function.Name.Items[0] is MathLimitNode limit
                && PlainName(limit.Base) is { } limitName
                && LatexSymbols.Functions.Contains(limitName))
            {
                name = "\\" + limitName + (limit.Upper ? "^" : "_") + Group(limit.Limit);
            }
            else if (function.Name.Items.Count == 1
                     && function.Name.Items[0] is MathScriptNode { Pre: false } scriptedName
                     && PlainName(scriptedName.Base) is { } scriptedPlain
                     && LatexSymbols.Functions.Contains(scriptedPlain))
            {
                // Имя функции с индексом: log₂.
                var builder = new StringBuilder("\\" + scriptedPlain);
                if (scriptedName.Sub is not null) builder.Append('_').Append(Operand(scriptedName.Sub));
                if (scriptedName.Sup is not null) builder.Append('^').Append(Operand(scriptedName.Sup));
                name = builder.ToString();
            }
            else if (PlainName(function.Name) is { } plain)
            {
                name = LatexSymbols.Functions.Contains(plain)
                    ? "\\" + plain
                    : "\\operatorname{" + plain + "}";
            }
            else
            {
                name = "\\operatorname" + Group(function.Name);
            }

            return function.Argument.IsEmpty ? name : name + Group(function.Argument);
        }

        /// <summary>Имя функции, если оно — один ран букв.</summary>
        private static string? PlainName(MathRow row)
        {
            if (row.Items.Count != 1 || row.Items[0] is not MathRunNode run) return null;
            foreach (char c in run.Text)
                if (!char.IsLetter(c)) return null;
            return run.Text;
        }

        private static string WriteRun(MathRunNode run)
        {
            if (run.IsNormalText)
                return Colored(run.Color, "\\text{" + EscapeText(run.Text) + "}");

            string body = EscapeMath(run.Text);

            string? wrapper = run.Script switch
            {
                MathScript.DoubleStruck => "\\mathbb",
                MathScript.Script => "\\mathcal",
                MathScript.Fraktur => "\\mathfrak",
                MathScript.SansSerif => "\\mathsf",
                MathScript.Monospace => "\\mathtt",
                _ => null
            };

            if (wrapper is null)
            {
                wrapper = run.Style switch
                {
                    MathLetterStyle.Bold => "\\mathbf",
                    MathLetterStyle.BoldItalic => "\\boldsymbol",
                    MathLetterStyle.Plain when HasLatinLetters(run.Text) => "\\mathrm",
                    _ => null
                };
            }

            string text = wrapper is null ? body : wrapper + "{" + body + "}";
            return Colored(run.Color, text);
        }

        private static string Colored(string? color, string text)
            => string.IsNullOrEmpty(color) ? text : "\\color{#" + color + "}{" + text + "}";

        private static bool HasLatinLetters(string text)
        {
            foreach (char c in text)
                if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z') return true;
            return false;
        }

        private static string EscapeMath(string text)
        {
            var builder = new StringBuilder(text.Length);
            var enumerator = StringInfo.GetTextElementEnumerator(text);

            while (enumerator.MoveNext())
            {
                string element = enumerator.GetTextElement();

                switch (element)
                {
                    case "−": Append(builder, "-"); continue;
                    case " ": Append(builder, "\\ "); continue;
                    case " ": Append(builder, "~"); continue;
                    case " ": Append(builder, "\\quad "); continue;
                    case " ": Append(builder, "\\enspace "); continue;
                    case " ": Append(builder, "\\,"); continue;
                    case " ": Append(builder, "\\:"); continue;
                    case " ": Append(builder, "\\;"); continue;
                    case "%": Append(builder, "\\%"); continue;
                    case "$": Append(builder, "\\$"); continue;
                    case "#": Append(builder, "\\#"); continue;
                    case "&": Append(builder, "\\&"); continue;
                    case "_": Append(builder, "\\_"); continue;
                    case "^": Append(builder, "\\text{^}"); continue;
                    case "~": Append(builder, "\\text{~}"); continue;
                    case "'": Append(builder, "\\prime "); continue;
                }

                if (LatexSymbols.CommandFor(element) is { } command)
                {
                    Append(builder, command);
                    continue;
                }

                Append(builder, element);
            }

            return builder.ToString();
        }

        private static string EscapeText(string text)
        {
            var builder = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                switch (c)
                {
                    case '{': builder.Append("\\{"); break;
                    case '}': builder.Append("\\}"); break;
                    case ' ': builder.Append('~'); break;
                    default: builder.Append(c); break;
                }
            }
            return builder.ToString();
        }
    }
}
