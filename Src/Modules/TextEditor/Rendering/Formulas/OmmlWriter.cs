using System.Globalization;
using System.Xml.Linq;

namespace Writersword.Modules.TextEditor.Rendering.Formulas
{
    /// <summary>
    /// Запись узлов формулы в XML формулы Word (m:oMath). В таком виде формула
    /// хранится в документе, верстается на листе и уходит в .docx без перевода:
    /// Word открывает её своей формулой, которую можно править у него.
    /// </summary>
    internal static class OmmlWriter
    {
        private static readonly XNamespace M = "http://schemas.openxmlformats.org/officeDocument/2006/math";
        private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

        /// <summary>Шрифт формул Word: им набраны раны формулы.</summary>
        private const string MathFontFamily = "Cambria Math";

        /// <summary>XML формулы m:oMath с объявлениями пространств имён.</summary>
        public static string Write(MathRow row)
        {
            var math = new XElement(M + "oMath",
                new XAttribute(XNamespace.Xmlns + "m", M.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "w", W.NamespaceName));

            AppendRow(math, row);
            return math.ToString(SaveOptions.DisableFormatting);
        }

        private static void AppendRow(XElement parent, MathRow? row)
        {
            if (row is null) return;
            foreach (var node in row.Items) AppendNode(parent, node);
        }

        private static XElement Argument(string name, MathRow? row)
        {
            var element = new XElement(M + name);
            AppendRow(element, row);
            return element;
        }

        private static XElement Value(string name, string value)
            => new(M + name, new XAttribute(M + "val", value));

        private static XElement On(string name) => Value(name, "1");

        private static string Char(int codepoint)
            => codepoint == 0 ? string.Empty : char.ConvertFromUtf32(codepoint);

        private static void AppendNode(XElement parent, MathNode node)
        {
            switch (node)
            {
                case MathRow nested:
                    AppendRow(parent, nested);
                    return;

                case MathRunNode run:
                    parent.Add(BuildRun(run));
                    return;

                case MathFractionNode fraction:
                {
                    var element = new XElement(M + "f");
                    string? type = fraction.Kind switch
                    {
                        MathFractionKind.NoBar => "noBar",
                        MathFractionKind.Skewed => "skw",
                        MathFractionKind.Linear => "lin",
                        _ => null
                    };
                    if (type is not null) element.Add(new XElement(M + "fPr", Value("type", type)));
                    element.Add(Argument("num", fraction.Numerator), Argument("den", fraction.Denominator));
                    parent.Add(element);
                    return;
                }

                case MathScriptNode script:
                {
                    if (script.Pre)
                    {
                        parent.Add(new XElement(M + "sPre",
                            Argument("sub", script.Sub), Argument("sup", script.Sup), Argument("e", script.Base)));
                        return;
                    }

                    if (script.Sub is not null && script.Sup is not null)
                    {
                        parent.Add(new XElement(M + "sSubSup",
                            Argument("e", script.Base), Argument("sub", script.Sub), Argument("sup", script.Sup)));
                    }
                    else if (script.Sup is not null)
                    {
                        parent.Add(new XElement(M + "sSup", Argument("e", script.Base), Argument("sup", script.Sup)));
                    }
                    else if (script.Sub is not null)
                    {
                        parent.Add(new XElement(M + "sSub", Argument("e", script.Base), Argument("sub", script.Sub)));
                    }
                    else
                    {
                        AppendRow(parent, script.Base);
                    }
                    return;
                }

                case MathRadicalNode radical:
                {
                    var element = new XElement(M + "rad");
                    if (radical.Degree is null) element.Add(new XElement(M + "radPr", On("degHide")));
                    element.Add(Argument("deg", radical.Degree), Argument("e", radical.Radicand));
                    parent.Add(element);
                    return;
                }

                case MathNaryNode nary:
                {
                    var properties = new XElement(M + "naryPr");
                    if (nary.OperatorChar != 0x222B) properties.Add(Value("chr", Char(nary.OperatorChar)));
                    properties.Add(Value("limLoc", nary.LimitsAboveBelow ? "undOvr" : "subSup"));
                    if (!nary.Grow) properties.Add(Value("grow", "0"));
                    if (nary.Sub is null) properties.Add(On("subHide"));
                    if (nary.Sup is null) properties.Add(On("supHide"));

                    parent.Add(new XElement(M + "nary",
                        properties,
                        Argument("sub", nary.Sub),
                        Argument("sup", nary.Sup),
                        Argument("e", nary.Body)));
                    return;
                }

                case MathDelimiterNode delimiter:
                {
                    var properties = new XElement(M + "dPr");
                    if (delimiter.OpenChar != '(') properties.Add(Value("begChr", Char(delimiter.OpenChar)));
                    if (delimiter.SeparatorChar != '|') properties.Add(Value("sepChr", Char(delimiter.SeparatorChar)));
                    if (delimiter.CloseChar != ')') properties.Add(Value("endChr", Char(delimiter.CloseChar)));
                    if (!delimiter.Grow) properties.Add(Value("grow", "0"));

                    var element = new XElement(M + "d");
                    if (properties.HasElements) element.Add(properties);
                    foreach (var item in delimiter.Items) element.Add(Argument("e", item));
                    if (delimiter.Items.Count == 0) element.Add(new XElement(M + "e"));
                    parent.Add(element);
                    return;
                }

                case MathFunctionNode function:
                    parent.Add(new XElement(M + "func",
                        Argument("fName", function.Name), Argument("e", function.Argument)));
                    return;

                case MathLimitNode limit:
                    parent.Add(new XElement(M + (limit.Upper ? "limUpp" : "limLow"),
                        Argument("e", limit.Base), Argument("lim", limit.Limit)));
                    return;

                case MathAccentNode accent:
                {
                    var element = new XElement(M + "acc");
                    if (accent.AccentChar != 0x0302)
                        element.Add(new XElement(M + "accPr", Value("chr", Char(accent.AccentChar))));
                    element.Add(Argument("e", accent.Base));
                    parent.Add(element);
                    return;
                }

                case MathBarNode bar:
                {
                    var element = new XElement(M + "bar");
                    if (bar.Top) element.Add(new XElement(M + "barPr", Value("pos", "top")));
                    element.Add(Argument("e", bar.Base));
                    parent.Add(element);
                    return;
                }

                case MathGroupCharNode group:
                {
                    var properties = new XElement(M + "groupChrPr");
                    if (group.Char != 0x23DF) properties.Add(Value("chr", Char(group.Char)));
                    if (group.Top)
                    {
                        properties.Add(Value("pos", "top"));
                        properties.Add(Value("vertJc", "bot"));
                    }

                    var element = new XElement(M + "groupChr");
                    if (properties.HasElements) element.Add(properties);
                    element.Add(Argument("e", group.Base));
                    parent.Add(element);
                    return;
                }

                case MathBoxNode box:
                {
                    bool anyBorder = box.BorderTop || box.BorderBottom || box.BorderLeft || box.BorderRight;
                    bool anyStrike = box.StrikeHorizontal || box.StrikeVertical
                                     || box.StrikeDiagonalUp || box.StrikeDiagonalDown;

                    if (!anyBorder && !anyStrike)
                    {
                        parent.Add(new XElement(M + "box", Argument("e", box.Base)));
                        return;
                    }

                    var properties = new XElement(M + "borderBoxPr");
                    if (!box.BorderTop) properties.Add(On("hideTop"));
                    if (!box.BorderBottom) properties.Add(On("hideBot"));
                    if (!box.BorderLeft) properties.Add(On("hideLeft"));
                    if (!box.BorderRight) properties.Add(On("hideRight"));
                    if (box.StrikeHorizontal) properties.Add(On("strikeH"));
                    if (box.StrikeVertical) properties.Add(On("strikeV"));
                    if (box.StrikeDiagonalUp) properties.Add(On("strikeBLTR"));
                    if (box.StrikeDiagonalDown) properties.Add(On("strikeTLBR"));

                    var element = new XElement(M + "borderBox");
                    if (properties.HasElements) element.Add(properties);
                    element.Add(Argument("e", box.Base));
                    parent.Add(element);
                    return;
                }

                case MathMatrixNode matrix when matrix.IsEquationArray:
                {
                    var element = new XElement(M + "eqArr");
                    foreach (var line in matrix.Rows)
                    {
                        var cell = new XElement(M + "e");
                        foreach (var part in line) AppendRow(cell, part);
                        element.Add(cell);
                    }
                    if (matrix.Rows.Count == 0) element.Add(new XElement(M + "e"));
                    parent.Add(element);
                    return;
                }

                case MathMatrixNode matrix:
                {
                    int columns = 1;
                    foreach (var line in matrix.Rows)
                        if (line.Count > columns) columns = line.Count;

                    var element = new XElement(M + "m",
                        new XElement(M + "mPr",
                            new XElement(M + "mcs",
                                new XElement(M + "mc",
                                    new XElement(M + "mcPr",
                                        Value("count", columns.ToString(CultureInfo.InvariantCulture)),
                                        Value("mcJc", "center"))))));

                    foreach (var line in matrix.Rows)
                    {
                        var matrixRow = new XElement(M + "mr");
                        for (int i = 0; i < columns; i++)
                            matrixRow.Add(Argument("e", i < line.Count ? line[i] : null));
                        element.Add(matrixRow);
                    }

                    if (matrix.Rows.Count == 0)
                        element.Add(new XElement(M + "mr", new XElement(M + "e")));

                    parent.Add(element);
                    return;
                }

                case MathPhantomNode phantom:
                {
                    var properties = new XElement(M + "phantPr");
                    if (!phantom.Show) properties.Add(Value("show", "0"));
                    if (phantom.ZeroWidth) properties.Add(On("zeroWid"));
                    if (phantom.ZeroAscent) properties.Add(On("zeroAsc"));
                    if (phantom.ZeroDescent) properties.Add(On("zeroDesc"));

                    var element = new XElement(M + "phant");
                    if (properties.HasElements) element.Add(properties);
                    element.Add(Argument("e", phantom.Base));
                    parent.Add(element);
                    return;
                }
            }
        }

        private static XElement BuildRun(MathRunNode run)
        {
            var element = new XElement(M + "r");

            var mathProperties = new XElement(M + "rPr");
            if (run.IsNormalText)
            {
                mathProperties.Add(new XElement(M + "nor"));
            }
            else
            {
                string? script = run.Script switch
                {
                    MathScript.Script => "script",
                    MathScript.Fraktur => "fraktur",
                    MathScript.DoubleStruck => "double-struck",
                    MathScript.SansSerif => "sans-serif",
                    MathScript.Monospace => "monospace",
                    _ => null
                };
                if (script is not null) mathProperties.Add(Value("scr", script));

                string? style = run.Style switch
                {
                    MathLetterStyle.Plain => "p",
                    MathLetterStyle.Bold => "b",
                    MathLetterStyle.BoldItalic => "bi",
                    _ => null
                };
                if (style is not null) mathProperties.Add(Value("sty", style));
            }

            if (mathProperties.HasElements) element.Add(mathProperties);

            var wordProperties = new XElement(W + "rPr");
            if (!run.IsNormalText)
            {
                wordProperties.Add(new XElement(W + "rFonts",
                    new XAttribute(W + "ascii", MathFontFamily),
                    new XAttribute(W + "hAnsi", MathFontFamily)));
            }
            else if (run.Style is MathLetterStyle.Bold or MathLetterStyle.BoldItalic)
            {
                wordProperties.Add(new XElement(W + "b"));
            }

            if (!string.IsNullOrEmpty(run.Color))
                wordProperties.Add(new XElement(W + "color", new XAttribute(W + "val", run.Color)));

            if (wordProperties.HasElements) element.Add(wordProperties);

            element.Add(new XElement(M + "t",
                new XAttribute(XNamespace.Xml + "space", "preserve"),
                run.Text));

            return element;
        }
    }
}
