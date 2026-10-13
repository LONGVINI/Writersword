using System;
using System.Collections.Generic;
using System.Xml.Linq;

namespace Writersword.Modules.TextEditor.Rendering.Formulas
{
    /// <summary>Начертание букв в ране формулы (m:sty).</summary>
    internal enum MathLetterStyle
    {
        /// <summary>Курсив — обычный вид переменных в формуле.</summary>
        Italic,
        /// <summary>Прямое начертание.</summary>
        Plain,
        Bold,
        BoldItalic
    }

    /// <summary>Набор букв рана формулы (m:scr).</summary>
    internal enum MathScript
    {
        Roman,
        Script,
        Fraktur,
        DoubleStruck,
        SansSerif,
        Monospace
    }

    /// <summary>Вид дроби (m:fPr/m:type).</summary>
    internal enum MathFractionKind
    {
        /// <summary>Числитель над знаменателем, между ними черта.</summary>
        Bar,
        /// <summary>Числитель над знаменателем без черты.</summary>
        NoBar,
        /// <summary>Числитель и знаменатель по диагонали через косую черту.</summary>
        Skewed,
        /// <summary>В одну строку через косую черту.</summary>
        Linear
    }

    /// <summary>Узел разобранной формулы Word.</summary>
    internal abstract class MathNode
    {
    }

    /// <summary>Ряд узлов: содержимое формулы или одного её аргумента.</summary>
    internal sealed class MathRow : MathNode
    {
        public List<MathNode> Items { get; } = new();

        public bool IsEmpty => Items.Count == 0;
    }

    /// <summary>Ран формулы: текст одного начертания.</summary>
    internal sealed class MathRunNode : MathNode
    {
        public string Text { get; init; } = string.Empty;
        public MathLetterStyle Style { get; init; }
        public MathScript Script { get; init; }

        /// <summary>Обычный текст внутри формулы (m:nor): буквы стоят как в тексте.</summary>
        public bool IsNormalText { get; init; }

        /// <summary>Свой цвет рана (w:color), шестнадцатеричный без решётки, или null.</summary>
        public string? Color { get; init; }
    }

    internal sealed class MathFractionNode : MathNode
    {
        public MathFractionKind Kind { get; init; }
        public MathRow Numerator { get; init; } = new();
        public MathRow Denominator { get; init; } = new();
    }

    /// <summary>Основание с индексами: верхним, нижним или обоими, справа или слева.</summary>
    internal sealed class MathScriptNode : MathNode
    {
        public MathRow Base { get; init; } = new();
        public MathRow? Sub { get; init; }
        public MathRow? Sup { get; init; }

        /// <summary>Индексы стоят перед основанием (m:sPre).</summary>
        public bool Pre { get; init; }
    }

    internal sealed class MathRadicalNode : MathNode
    {
        public MathRow Radicand { get; init; } = new();

        /// <summary>Показатель корня или null, когда он спрятан либо пуст.</summary>
        public MathRow? Degree { get; init; }
    }

    /// <summary>Большой оператор с пределами: сумма, произведение, интеграл.</summary>
    internal sealed class MathNaryNode : MathNode
    {
        public int OperatorChar { get; init; } = 0x222B;

        /// <summary>Пределы над и под знаком (m:limLoc = undOvr), иначе — индексами.</summary>
        public bool LimitsAboveBelow { get; init; }

        public bool Grow { get; init; } = true;
        public MathRow? Sub { get; init; }
        public MathRow? Sup { get; init; }
        public MathRow Body { get; init; } = new();
    }

    /// <summary>Скобки вокруг одного или нескольких выражений через разделитель.</summary>
    internal sealed class MathDelimiterNode : MathNode
    {
        /// <summary>Открывающий знак; 0 — знака нет.</summary>
        public int OpenChar { get; init; } = '(';
        public int CloseChar { get; init; } = ')';
        public int SeparatorChar { get; init; } = '|';
        public bool Grow { get; init; } = true;
        public List<MathRow> Items { get; } = new();
    }

    /// <summary>Функция: имя и аргумент (m:func).</summary>
    internal sealed class MathFunctionNode : MathNode
    {
        public MathRow Name { get; init; } = new();
        public MathRow Argument { get; init; } = new();
    }

    /// <summary>Основание с пределом под ним или над ним (m:limLow, m:limUpp).</summary>
    internal sealed class MathLimitNode : MathNode
    {
        public MathRow Base { get; init; } = new();
        public MathRow Limit { get; init; } = new();
        public bool Upper { get; init; }
    }

    /// <summary>Знак над выражением: крышка, тильда, стрелка, точка (m:acc).</summary>
    internal sealed class MathAccentNode : MathNode
    {
        public int AccentChar { get; init; } = 0x0302;
        public MathRow Base { get; init; } = new();
    }

    /// <summary>Черта над или под выражением (m:bar).</summary>
    internal sealed class MathBarNode : MathNode
    {
        public bool Top { get; init; }
        public MathRow Base { get; init; } = new();
    }

    /// <summary>Растянутый знак над или под выражением: фигурная скобка, стрелка (m:groupChr).</summary>
    internal sealed class MathGroupCharNode : MathNode
    {
        public int Char { get; init; } = 0x23DF;
        public bool Top { get; init; }
        public MathRow Base { get; init; } = new();
    }

    /// <summary>Выражение в рамке, с зачёркиванием или без видимых линий (m:borderBox, m:box).</summary>
    internal sealed class MathBoxNode : MathNode
    {
        public MathRow Base { get; init; } = new();
        public bool BorderTop { get; init; }
        public bool BorderBottom { get; init; }
        public bool BorderLeft { get; init; }
        public bool BorderRight { get; init; }
        public bool StrikeHorizontal { get; init; }
        public bool StrikeVertical { get; init; }
        public bool StrikeDiagonalUp { get; init; }
        public bool StrikeDiagonalDown { get; init; }
    }

    /// <summary>Матрица или столбец уравнений (m:m, m:eqArr).</summary>
    internal sealed class MathMatrixNode : MathNode
    {
        public List<List<MathRow>> Rows { get; } = new();

        /// <summary>Столбец уравнений: строки выровнены по левому краю.</summary>
        public bool IsEquationArray { get; init; }
    }

    /// <summary>Выражение, занимающее место, но не всегда видимое (m:phant).</summary>
    internal sealed class MathPhantomNode : MathNode
    {
        public MathRow Base { get; init; } = new();
        public bool Show { get; init; } = true;
        public bool ZeroWidth { get; init; }
        public bool ZeroAscent { get; init; }
        public bool ZeroDescent { get; init; }
    }

    /// <summary>
    /// Разбор формулы Word (OMML, элемент m:oMath) в дерево узлов.
    ///
    /// Формула хранится в документе тем же XML, каким пришла из .docx: так она без
    /// потерь возвращается в Word при выгрузке. Здесь из этого XML достаётся только
    /// то, что нужно для вёрстки и рисования.
    /// </summary>
    internal static class OmmlParser
    {
        private static readonly XNamespace M = "http://schemas.openxmlformats.org/officeDocument/2006/math";
        private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

        /// <summary>Разбирает XML формулы. Нечитаемый XML даёт пустую формулу.</summary>
        public static MathRow Parse(string? omml)
        {
            var row = new MathRow();
            if (string.IsNullOrWhiteSpace(omml)) return row;

            XElement root;
            try
            {
                root = XElement.Parse(omml, LoadOptions.PreserveWhitespace);
            }
            catch (System.Xml.XmlException)
            {
                return row;
            }

            // Абзац формул (m:oMathPara) держит одну или несколько формул m:oMath.
            if (root.Name == M + "oMathPara")
            {
                foreach (var math in root.Elements(M + "oMath"))
                    AppendChildren(math, row);
                return row;
            }

            AppendChildren(root, row);
            return row;
        }

        private static MathRow ParseArgument(XElement? element)
        {
            var row = new MathRow();
            if (element is not null) AppendChildren(element, row);
            return row;
        }

        /// <summary>
        /// Часть, которая не спрятана: есть элемент — есть часть, пусть и пустая. На
        /// листе пустая часть не рисуется, а в окне формулы на её месте стоит квадратик.
        /// </summary>
        private static MathRow? ParsePresentArgument(XElement? element)
            => element is null ? null : ParseArgument(element);

        private static MathRow? ParseOptionalArgument(XElement? element)
        {
            if (element is null) return null;
            var row = ParseArgument(element);
            return row.IsEmpty ? null : row;
        }

        private static void AppendChildren(XElement parent, MathRow row)
        {
            foreach (var child in parent.Elements())
            {
                var node = ParseNode(child, row);
                if (node is not null) row.Items.Add(node);
            }
        }

        private static MathNode? ParseNode(XElement element, MathRow row)
        {
            if (element.Name.Namespace != M)
            {
                // Правки рецензирования и прочие обёртки Word внутри формулы: их
                // содержимое — часть формулы.
                if (element.Name.Namespace == W
                    && (element.Name.LocalName == "ins" || element.Name.LocalName == "moveTo"
                        || element.Name.LocalName == "smartTag" || element.Name.LocalName == "sdt"
                        || element.Name.LocalName == "sdtContent"))
                {
                    AppendChildren(element, row);
                }

                return null;
            }

            switch (element.Name.LocalName)
            {
                case "r":
                    return ParseRun(element);

                case "f":
                    return new MathFractionNode
                    {
                        Kind = ReadFractionKind(element.Element(M + "fPr")),
                        Numerator = ParseArgument(element.Element(M + "num")),
                        Denominator = ParseArgument(element.Element(M + "den"))
                    };

                case "sSup":
                    return new MathScriptNode
                    {
                        Base = ParseArgument(element.Element(M + "e")),
                        Sup = ParseArgument(element.Element(M + "sup"))
                    };

                case "sSub":
                    return new MathScriptNode
                    {
                        Base = ParseArgument(element.Element(M + "e")),
                        Sub = ParseArgument(element.Element(M + "sub"))
                    };

                case "sSubSup":
                    return new MathScriptNode
                    {
                        Base = ParseArgument(element.Element(M + "e")),
                        Sub = ParseArgument(element.Element(M + "sub")),
                        Sup = ParseArgument(element.Element(M + "sup"))
                    };

                case "sPre":
                    return new MathScriptNode
                    {
                        Base = ParseArgument(element.Element(M + "e")),
                        Sub = ParseArgument(element.Element(M + "sub")),
                        Sup = ParseArgument(element.Element(M + "sup")),
                        Pre = true
                    };

                case "rad":
                {
                    bool degreeHidden = ReadOnOff(element.Element(M + "radPr")?.Element(M + "degHide"), false);
                    return new MathRadicalNode
                    {
                        Radicand = ParseArgument(element.Element(M + "e")),
                        Degree = degreeHidden ? null : ParsePresentArgument(element.Element(M + "deg"))
                    };
                }

                case "nary":
                {
                    var properties = element.Element(M + "naryPr");
                    int operatorChar = ReadChar(properties?.Element(M + "chr"), 0x222B);
                    bool subHidden = ReadOnOff(properties?.Element(M + "subHide"), false);
                    bool supHidden = ReadOnOff(properties?.Element(M + "supHide"), false);
                    string? limitLocation = (string?)properties?.Element(M + "limLoc")?.Attribute(M + "val");

                    return new MathNaryNode
                    {
                        OperatorChar = operatorChar == 0 ? 0x222B : operatorChar,
                        LimitsAboveBelow = limitLocation is null
                            ? !IsIntegral(operatorChar)
                            : limitLocation == "undOvr",
                        Grow = ReadOnOff(properties?.Element(M + "grow"), true),
                        Sub = subHidden ? null : ParsePresentArgument(element.Element(M + "sub")),
                        Sup = supHidden ? null : ParsePresentArgument(element.Element(M + "sup")),
                        Body = ParseArgument(element.Element(M + "e"))
                    };
                }

                case "d":
                {
                    var properties = element.Element(M + "dPr");
                    var node = new MathDelimiterNode
                    {
                        OpenChar = ReadChar(properties?.Element(M + "begChr"), '('),
                        CloseChar = ReadChar(properties?.Element(M + "endChr"), ')'),
                        SeparatorChar = ReadChar(properties?.Element(M + "sepChr"), '|'),
                        Grow = ReadOnOff(properties?.Element(M + "grow"), true)
                    };

                    foreach (var item in element.Elements(M + "e"))
                        node.Items.Add(ParseArgument(item));
                    if (node.Items.Count == 0) node.Items.Add(new MathRow());
                    return node;
                }

                case "func":
                    return new MathFunctionNode
                    {
                        Name = ParseArgument(element.Element(M + "fName")),
                        Argument = ParseArgument(element.Element(M + "e"))
                    };

                case "limLow":
                case "limUpp":
                    return new MathLimitNode
                    {
                        Base = ParseArgument(element.Element(M + "e")),
                        Limit = ParseArgument(element.Element(M + "lim")),
                        Upper = element.Name.LocalName == "limUpp"
                    };

                case "acc":
                {
                    int accent = ReadChar(element.Element(M + "accPr")?.Element(M + "chr"), 0x0302);
                    return new MathAccentNode
                    {
                        AccentChar = accent == 0 ? 0x0302 : accent,
                        Base = ParseArgument(element.Element(M + "e"))
                    };
                }

                case "bar":
                {
                    string? position = (string?)element.Element(M + "barPr")?.Element(M + "pos")?.Attribute(M + "val");
                    return new MathBarNode
                    {
                        Top = position == "top",
                        Base = ParseArgument(element.Element(M + "e"))
                    };
                }

                case "groupChr":
                {
                    var properties = element.Element(M + "groupChrPr");
                    string? position = (string?)properties?.Element(M + "pos")?.Attribute(M + "val");
                    int groupChar = ReadChar(properties?.Element(M + "chr"), 0x23DF);
                    return new MathGroupCharNode
                    {
                        Char = groupChar == 0 ? 0x23DF : groupChar,
                        Top = position == "top",
                        Base = ParseArgument(element.Element(M + "e"))
                    };
                }

                case "box":
                    return new MathBoxNode { Base = ParseArgument(element.Element(M + "e")) };

                case "borderBox":
                {
                    var properties = element.Element(M + "borderBoxPr");
                    return new MathBoxNode
                    {
                        Base = ParseArgument(element.Element(M + "e")),
                        BorderTop = !ReadOnOff(properties?.Element(M + "hideTop"), false),
                        BorderBottom = !ReadOnOff(properties?.Element(M + "hideBot"), false),
                        BorderLeft = !ReadOnOff(properties?.Element(M + "hideLeft"), false),
                        BorderRight = !ReadOnOff(properties?.Element(M + "hideRight"), false),
                        StrikeHorizontal = ReadOnOff(properties?.Element(M + "strikeH"), false),
                        StrikeVertical = ReadOnOff(properties?.Element(M + "strikeV"), false),
                        StrikeDiagonalUp = ReadOnOff(properties?.Element(M + "strikeBLTR"), false),
                        StrikeDiagonalDown = ReadOnOff(properties?.Element(M + "strikeTLBR"), false)
                    };
                }

                case "m":
                {
                    var matrix = new MathMatrixNode();
                    foreach (var matrixRow in element.Elements(M + "mr"))
                    {
                        var cells = new List<MathRow>();
                        foreach (var cell in matrixRow.Elements(M + "e"))
                            cells.Add(ParseArgument(cell));
                        matrix.Rows.Add(cells);
                    }
                    return matrix;
                }

                case "eqArr":
                {
                    var array = new MathMatrixNode { IsEquationArray = true };
                    foreach (var line in element.Elements(M + "e"))
                        array.Rows.Add(new List<MathRow> { ParseArgument(line) });
                    return array;
                }

                case "phant":
                {
                    var properties = element.Element(M + "phantPr");
                    return new MathPhantomNode
                    {
                        Base = ParseArgument(element.Element(M + "e")),
                        Show = ReadOnOff(properties?.Element(M + "show"), true),
                        ZeroWidth = ReadOnOff(properties?.Element(M + "zeroWid"), false),
                        ZeroAscent = ReadOnOff(properties?.Element(M + "zeroAsc"), false),
                        ZeroDescent = ReadOnOff(properties?.Element(M + "zeroDesc"), false)
                    };
                }

                case "oMath":
                    // Формула внутри формулы: её содержимое встаёт в общий ряд.
                    AppendChildren(element, row);
                    return null;

                default:
                    // Свойства (m:ctrlPr, m:argPr и подобные) вида на листе не имеют.
                    return null;
            }
        }

        private static MathNode? ParseRun(XElement run)
        {
            var text = new System.Text.StringBuilder();
            foreach (var child in run.Elements())
            {
                if (child.Name == M + "t" || child.Name == W + "t")
                    text.Append(child.Value);
            }

            if (text.Length == 0) return null;

            var properties = run.Element(M + "rPr");
            bool normal = ReadOnOff(properties?.Element(M + "nor"), false);
            string? style = (string?)properties?.Element(M + "sty")?.Attribute(M + "val");
            string? script = (string?)properties?.Element(M + "scr")?.Attribute(M + "val");

            string? color = (string?)run.Element(W + "rPr")?.Element(W + "color")?.Attribute(W + "val");
            if (color is not null && (color.Length != 6 || color == "auto")) color = null;

            return new MathRunNode
            {
                Text = text.ToString(),
                IsNormalText = normal,
                Style = style switch
                {
                    "p" => MathLetterStyle.Plain,
                    "b" => MathLetterStyle.Bold,
                    "bi" => MathLetterStyle.BoldItalic,
                    _ => MathLetterStyle.Italic
                },
                Script = script switch
                {
                    "script" => MathScript.Script,
                    "fraktur" => MathScript.Fraktur,
                    "double-struck" => MathScript.DoubleStruck,
                    "sans-serif" => MathScript.SansSerif,
                    "monospace" => MathScript.Monospace,
                    _ => MathScript.Roman
                },
                Color = color
            };
        }

        private static MathFractionKind ReadFractionKind(XElement? properties)
        {
            string? type = (string?)properties?.Element(M + "type")?.Attribute(M + "val");
            return type switch
            {
                "skw" => MathFractionKind.Skewed,
                "lin" => MathFractionKind.Linear,
                "noBar" => MathFractionKind.NoBar,
                _ => MathFractionKind.Bar
            };
        }

        /// <summary>
        /// Включённость свойства Word: элемент без значения включает его, значения
        /// 0, off и false выключают. Нет элемента — берётся умолчание.
        /// </summary>
        private static bool ReadOnOff(XElement? element, bool defaultValue)
        {
            if (element is null) return defaultValue;

            string? value = (string?)element.Attribute(M + "val");
            if (value is null) return true;

            return !(value == "0"
                || string.Equals(value, "off", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Знак из свойства (m:chr, m:begChr и подобных). Нет элемента — умолчание;
        /// пустое значение — знака нет вовсе (ноль).
        /// </summary>
        private static int ReadChar(XElement? element, int defaultChar)
        {
            if (element is null) return defaultChar;

            string? value = (string?)element.Attribute(M + "val");
            if (string.IsNullOrEmpty(value)) return 0;

            return char.ConvertToUtf32(value, 0);
        }

        /// <summary>Знак интеграла: пределы у него по умолчанию стоят сбоку.</summary>
        private static bool IsIntegral(int codepoint)
            => codepoint >= 0x222B && codepoint <= 0x2233;
    }
}
