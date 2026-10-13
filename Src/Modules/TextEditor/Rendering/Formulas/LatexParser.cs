using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Writersword.Modules.TextEditor.Rendering.Formulas
{
    /// <summary>Ошибка в записи формулы: что не так и где.</summary>
    public sealed class FormulaSyntaxException : Exception
    {
        public FormulaSyntaxException(string message, int position) : base(message)
        {
            Position = position;
        }

        /// <summary>Позиция в записи, на которой разбор остановился.</summary>
        public int Position { get; }
    }

    /// <summary>
    /// Разбор формулы, записанной командами LaTeX, в узлы формулы — те же, в которые
    /// разбирается формула Word. Из узлов формула пишется в XML Word (OmmlWriter) и
    /// верстается тем же набором, что и формула из .docx.
    ///
    /// Понимается то, чем набирают формулы в тексте: дроби, степени и индексы, корни,
    /// суммы и интегралы с пределами, скобки по высоте содержимого, функции, пределы,
    /// надстрочные знаки, черты и фигурные скобки сверху и снизу, рамки, матрицы,
    /// системы, обычный текст внутри формулы, начертания, греческие буквы и знаки.
    /// Знак Юникода можно вводить и напрямую: «α», «≤», «∞» читаются как есть.
    /// </summary>
    internal sealed class LatexParser
    {
        private readonly string _source;
        private int _pos;

        private LatexParser(string source)
        {
            _source = source ?? string.Empty;
        }

        /// <summary>
        /// Разбирает запись формулы. Ошибка записи — <see cref="FormulaSyntaxException"/>
        /// с позицией, на которой разбор остановился.
        /// </summary>
        public static MathRow Parse(string source)
        {
            var parser = new LatexParser(source);
            var rows = parser.ParseLines(isEnvironment: false, out _);

            if (parser._pos < parser._source.Length)
            {
                if (parser.Peek() == '}')
                    throw new FormulaSyntaxException("Лишняя закрывающая скобка «}».", parser._pos);
                throw new FormulaSyntaxException("Запись формулы не разобрана до конца.", parser._pos);
            }

            if (rows.Count == 1) return Merge(rows[0][0]);

            // Несколько строк через \\ — формула из строк одна под другой.
            var array = new MathMatrixNode { IsEquationArray = true };
            foreach (var line in rows)
                array.Rows.Add(new List<MathRow> { Merge(line[0]) });

            var result = new MathRow();
            result.Items.Add(array);
            return result;
        }

        // ── Строки и ячейки ─────────────────────────────────────────────────

        /// <summary>
        /// Строки, разделённые \\, и ячейки, разделённые &amp;. Вне окружения ячейки не
        /// делятся: &amp; там — точка выравнивания строк, на вид не влияющая.
        /// </summary>
        private List<List<MathRow>> ParseLines(bool isEnvironment, out bool endedByEnd)
        {
            var lines = new List<List<MathRow>>();
            var cells = new List<MathRow> { new MathRow() };

            while (true)
            {
                var part = ParseRow(stopAtCell: true);
                cells[^1].Items.AddRange(part.Items);

                SkipSpaces();
                if (TryConsume("&"))
                {
                    // Вне матрицы &amp; — точка выравнивания: строка продолжается.
                    if (isEnvironment) cells.Add(new MathRow());
                    continue;
                }

                if (TryConsume("\\\\"))
                {
                    SkipOptionalLength();
                    lines.Add(cells);
                    cells = new List<MathRow> { new MathRow() };
                    continue;
                }

                lines.Add(cells);
                endedByEnd = LookingAtCommand("end");
                return lines;
            }
        }

        /// <summary>
        /// Ряд узлов до конца записи, закрывающей скобки группы или границы ячейки,
        /// строки, \right, \middle и \end.
        /// </summary>
        private MathRow ParseRow(bool stopAtCell)
        {
            var row = new MathRow();

            while (true)
            {
                SkipSpaces();
                if (AtEnd) return row;

                char c = Peek();
                if (c == '}') return row;
                if (stopAtCell && (c == '&' || LookingAt("\\\\"))) return row;
                if (LookingAtCommand("right") || LookingAtCommand("middle") || LookingAtCommand("end")) return row;

                if (c == '^' || c == '_')
                {
                    ParseScripts(row);
                    continue;
                }

                if (c == '\'')
                {
                    // Штрих — верхний индекс со знаком штриха, как у LaTeX.
                    int count = 0;
                    while (!AtEnd && Peek() == '\'') { _pos++; count++; }
                    string primes = count switch
                    {
                        1 => "\u2032",
                        2 => "\u2033",
                        3 => "\u2034",
                        _ => new string('\u2032', count)
                    };
                    AttachScript(row, sub: null, sup: RowOf(Run(primes, MathLetterStyle.Plain)));
                    continue;
                }

                ParseAtomInto(row);
            }
        }

        /// <summary>Индексы ^ и _ к последнему узлу ряда.</summary>
        private void ParseScripts(MathRow row)
        {
            MathRow? sub = null;
            MathRow? sup = null;

            while (true)
            {
                SkipSpaces();
                if (AtEnd) break;

                char c = Peek();
                if (c == '_')
                {
                    if (sub is not null) throw new FormulaSyntaxException("Второй нижний индекс подряд: объедините их в {…}.", _pos);
                    _pos++;
                    sub = ParseArgument("нижнего индекса");
                }
                else if (c == '^')
                {
                    if (sup is not null) throw new FormulaSyntaxException("Второй верхний индекс подряд: объедините их в {…}.", _pos);
                    _pos++;
                    sup = ParseArgument("верхнего индекса");
                }
                else
                {
                    break;
                }
            }

            AttachScript(row, sub, sup);
        }

        /// <summary>
        /// Прицепляет индексы к последнему узлу ряда: у суммы и интеграла они становятся
        /// пределами, у предела функции — его нижней строкой, у остального — индексами.
        /// </summary>
        private static void AttachScript(MathRow row, MathRow? sub, MathRow? sup)
        {
            MathNode? last = row.Items.Count > 0 ? row.Items[^1] : null;

            if (last is MathNaryNode nary && nary.Body.IsEmpty)
            {
                row.Items[^1] = new MathNaryNode
                {
                    OperatorChar = nary.OperatorChar,
                    LimitsAboveBelow = nary.LimitsAboveBelow,
                    Grow = nary.Grow,
                    Sub = MergeScripts(nary.Sub, sub),
                    Sup = MergeScripts(nary.Sup, sup),
                    Body = nary.Body
                };
                return;
            }

            if (last is MathScriptNode existing && !existing.Pre
                && ((sub is null || existing.Sub is null) && (sup is null || existing.Sup is null)))
            {
                row.Items[^1] = new MathScriptNode
                {
                    Base = existing.Base,
                    Sub = existing.Sub ?? sub,
                    Sup = existing.Sup ?? sup
                };
                return;
            }

            var baseRow = new MathRow();
            if (last is not null)
            {
                row.Items.RemoveAt(row.Items.Count - 1);
                if (last is MathRow group) baseRow = group;
                else baseRow.Items.Add(last);
            }

            row.Items.Add(new MathScriptNode { Base = baseRow, Sub = sub, Sup = sup });
        }

        private static MathRow? MergeScripts(MathRow? existing, MathRow? added)
        {
            if (added is null) return existing;
            if (existing is null) return added;
            var merged = new MathRow();
            merged.Items.AddRange(existing.Items);
            merged.Items.AddRange(added.Items);
            return merged;
        }

        // ── Атомы ──────────────────────────────────────────────────────────

        /// <summary>Аргумент команды или индекса: группа в {…} либо один знак или команда.</summary>
        private MathRow ParseArgument(string what)
        {
            SkipSpaces();
            if (AtEnd) throw new FormulaSyntaxException($"Нет {what}.", _pos);

            if (Peek() == '{')
            {
                _pos++;
                var group = ParseRow(stopAtCell: false);
                Expect('}', what);
                return Merge(group);
            }

            if (Peek() == '}' || Peek() == '^' || Peek() == '_' || Peek() == '&')
                throw new FormulaSyntaxException($"Нет {what}.", _pos);

            var row = new MathRow();
            ParseAtomInto(row);
            return Merge(row);
        }

        /// <summary>Аргумент в квадратных скобках, если он есть.</summary>
        private MathRow? ParseOptionalArgument()
        {
            SkipSpaces();
            if (AtEnd || Peek() != '[') return null;

            _pos++;
            var row = new MathRow();
            while (true)
            {
                SkipSpaces();
                if (AtEnd) throw new FormulaSyntaxException("Нет закрывающей «]».", _pos);
                if (Peek() == ']') { _pos++; break; }
                if (Peek() == '^' || Peek() == '_') { ParseScripts(row); continue; }
                ParseAtomInto(row);
            }
            return Merge(row);
        }

        /// <summary>Текст в {…} без разбора — имя окружения, цвет, текст.</summary>
        private string ParseRawGroup(string what)
        {
            SkipSpaces();
            if (AtEnd || Peek() != '{') throw new FormulaSyntaxException($"Ожидается {{…}} с {what}.", _pos);

            int start = ++_pos;
            int depth = 1;
            while (!AtEnd)
            {
                char c = _source[_pos];
                if (c == '\\' && _pos + 1 < _source.Length) { _pos += 2; continue; }
                if (c == '{') depth++;
                else if (c == '}' && --depth == 0) break;
                _pos++;
            }

            if (AtEnd) throw new FormulaSyntaxException("Нет закрывающей скобки «}».", start - 1);
            string text = _source.Substring(start, _pos - start);
            _pos++;
            return text;
        }

        /// <summary>Один атом: группа, команда или знак.</summary>
        private void ParseAtomInto(MathRow row)
        {
            SkipSpaces();
            if (AtEnd) return;

            char c = Peek();

            if (c == '{')
            {
                _pos++;
                var group = ParseRow(stopAtCell: false);
                Expect('}', "группы");
                row.Items.Add(Merge(group));
                return;
            }

            if (c == '\\')
            {
                ParseCommandInto(row);
                return;
            }

            if (c == '~')
            {
                _pos++;
                row.Items.Add(Run("\u00A0", MathLetterStyle.Plain));
                return;
            }

            // Знак, в том числе вне основной плоскости Юникода.
            string text = char.IsHighSurrogate(c) && _pos + 1 < _source.Length
                ? _source.Substring(_pos, 2)
                : c.ToString();
            _pos += text.Length;

            if (text == "-") text = "\u2212";
            row.Items.Add(Run(text, MathLetterStyle.Italic));
        }

        private void ParseCommandInto(MathRow row)
        {
            int start = _pos;
            _pos++;
            if (AtEnd) throw new FormulaSyntaxException("Обратная косая черта в конце записи.", start);

            char first = Peek();
            if (!char.IsLetter(first))
            {
                _pos++;
                ParseSymbolCommand(first, row, start);
                return;
            }

            int nameStart = _pos;
            while (!AtEnd && char.IsLetter(Peek())) _pos++;
            string name = _source.Substring(nameStart, _pos - nameStart);

            if (LatexSymbols.TryGet(name, out var symbol))
            {
                // Знак из команды — как тот же знак, введённый напрямую: буквы
                // курсивом, остальное начертания не имеет.
                row.Items.Add(Run(symbol, MathLetterStyle.Italic));
                return;
            }

            if (LatexSymbols.NaryOperators.TryGetValue(name, out int naryChar))
            {
                ParseNary(row, naryChar);
                return;
            }

            if (LatexSymbols.Functions.Contains(name))
            {
                ParseFunction(row, RowOf(Run(name, MathLetterStyle.Plain)), LatexSymbols.LimitFunctions.Contains(name));
                return;
            }

            if (LatexSymbols.Accents.TryGetValue(name, out int accentChar))
            {
                row.Items.Add(new MathAccentNode { AccentChar = accentChar, Base = ParseArgument($"аргумента \\{name}") });
                return;
            }

            if (LatexSymbols.Spaces.TryGetValue(name, out var space))
            {
                if (space.Length > 0) row.Items.Add(Run(space, MathLetterStyle.Italic));
                return;
            }

            switch (name)
            {
                case "frac":
                case "dfrac":
                case "tfrac":
                case "cfrac":
                {
                    var numerator = ParseArgument("числителя");
                    var denominator = ParseArgument("знаменателя");
                    row.Items.Add(new MathFractionNode { Numerator = numerator, Denominator = denominator });
                    return;
                }

                case "sfrac":
                {
                    var numerator = ParseArgument("числителя");
                    var denominator = ParseArgument("знаменателя");
                    row.Items.Add(new MathFractionNode
                    {
                        Kind = MathFractionKind.Skewed,
                        Numerator = numerator,
                        Denominator = denominator
                    });
                    return;
                }

                case "binom":
                case "dbinom":
                case "tbinom":
                {
                    var top = ParseArgument("верхней строки");
                    var bottom = ParseArgument("нижней строки");
                    var delimiter = new MathDelimiterNode { OpenChar = '(', CloseChar = ')' };
                    delimiter.Items.Add(RowOf(new MathFractionNode
                    {
                        Kind = MathFractionKind.NoBar,
                        Numerator = top,
                        Denominator = bottom
                    }));
                    row.Items.Add(delimiter);
                    return;
                }

                case "genfrac":
                {
                    string left = ParseRawGroup("левой скобкой").Trim();
                    string right = ParseRawGroup("правой скобкой").Trim();
                    string thickness = ParseRawGroup("толщиной черты").Trim();
                    ParseRawGroup("стилем");
                    var numerator = ParseArgument("числителя");
                    var denominator = ParseArgument("знаменателя");

                    var fraction = new MathFractionNode
                    {
                        Kind = IsZeroLength(thickness) ? MathFractionKind.NoBar : MathFractionKind.Bar,
                        Numerator = numerator,
                        Denominator = denominator
                    };

                    if (left.Length == 0 && right.Length == 0)
                    {
                        row.Items.Add(fraction);
                    }
                    else
                    {
                        var delimiter = new MathDelimiterNode
                        {
                            OpenChar = DelimiterChar(left, start),
                            CloseChar = DelimiterChar(right, start)
                        };
                        delimiter.Items.Add(RowOf(fraction));
                        row.Items.Add(delimiter);
                    }
                    return;
                }

                case "sqrt":
                {
                    var degree = ParseOptionalArgument();
                    var radicand = ParseArgument("подкоренного выражения");
                    row.Items.Add(new MathRadicalNode
                    {
                        Radicand = radicand,
                        // Пустые квадратные скобки — показатель, который ещё не вписан:
                        // в окне формулы на его месте квадратик, на листе — ничего.
                        Degree = degree
                    });
                    return;
                }

                case "operatorname":
                {
                    string functionName = ParseRawGroup("именем функции").Trim();
                    ParseFunction(row, RowOf(Run(functionName, MathLetterStyle.Plain)), limitsBelow: false);
                    return;
                }

                case "overline":
                    row.Items.Add(new MathBarNode { Top = true, Base = ParseArgument("аргумента \\overline") });
                    return;

                case "underline":
                    row.Items.Add(new MathBarNode { Top = false, Base = ParseArgument("аргумента \\underline") });
                    return;

                case "overbrace":
                case "underbrace":
                case "overbracket":
                case "underbracket":
                case "overparen":
                case "underparen":
                {
                    bool top = name.StartsWith("over", StringComparison.Ordinal);
                    int groupChar = name switch
                    {
                        "overbrace" => 0x23DE,
                        "underbrace" => 0x23DF,
                        "overbracket" => 0x23B4,
                        "underbracket" => 0x23B5,
                        "overparen" => 0x23DC,
                        _ => 0x23DD
                    };

                    var group = new MathGroupCharNode
                    {
                        Char = groupChar,
                        Top = top,
                        Base = ParseArgument($"аргумента \\{name}")
                    };

                    // Подпись к скобке: ^{…} над верхней, _{…} под нижней.
                    SkipSpaces();
                    if (!AtEnd && Peek() == (top ? '^' : '_'))
                    {
                        _pos++;
                        var label = ParseArgument("подписи");
                        row.Items.Add(new MathLimitNode { Base = RowOf(group), Limit = label, Upper = top });
                    }
                    else
                    {
                        row.Items.Add(group);
                    }
                    return;
                }

                case "overset":
                case "stackrel":
                {
                    var over = ParseArgument("верхней строки");
                    var baseRow = ParseArgument("основания");
                    row.Items.Add(new MathLimitNode { Base = baseRow, Limit = over, Upper = true });
                    return;
                }

                case "underset":
                {
                    var under = ParseArgument("нижней строки");
                    var baseRow = ParseArgument("основания");
                    row.Items.Add(new MathLimitNode { Base = baseRow, Limit = under, Upper = false });
                    return;
                }

                case "prescript":
                {
                    var sup = ParseArgument("левого верхнего индекса");
                    var sub = ParseArgument("левого нижнего индекса");
                    var baseRow = ParseArgument("основания");
                    row.Items.Add(new MathScriptNode
                    {
                        Base = baseRow,
                        Sub = sub.IsEmpty ? null : sub,
                        Sup = sup.IsEmpty ? null : sup,
                        Pre = true
                    });
                    return;
                }

                case "boxed":
                case "fbox":
                    row.Items.Add(new MathBoxNode
                    {
                        Base = ParseArgument("содержимого рамки"),
                        BorderTop = true,
                        BorderBottom = true,
                        BorderLeft = true,
                        BorderRight = true
                    });
                    return;

                case "cancel":
                case "bcancel":
                case "xcancel":
                    row.Items.Add(new MathBoxNode
                    {
                        Base = ParseArgument("зачёркиваемого"),
                        StrikeDiagonalUp = name != "bcancel",
                        StrikeDiagonalDown = name != "cancel"
                    });
                    return;

                case "phantom":
                    row.Items.Add(new MathPhantomNode { Base = ParseArgument("аргумента \\phantom"), Show = false });
                    return;

                case "hphantom":
                    row.Items.Add(new MathPhantomNode
                    {
                        Base = ParseArgument("аргумента \\hphantom"),
                        Show = false,
                        ZeroAscent = true,
                        ZeroDescent = true
                    });
                    return;

                case "vphantom":
                    row.Items.Add(new MathPhantomNode
                    {
                        Base = ParseArgument("аргумента \\vphantom"),
                        Show = false,
                        ZeroWidth = true
                    });
                    return;

                case "text":
                case "textrm":
                case "textnormal":
                case "mbox":
                case "textup":
                    row.Items.Add(TextRun(ParseRawGroup("текстом"), MathLetterStyle.Plain));
                    return;

                case "textit":
                    row.Items.Add(TextRun(ParseRawGroup("текстом"), MathLetterStyle.Italic));
                    return;

                case "textbf":
                    row.Items.Add(TextRun(ParseRawGroup("текстом"), MathLetterStyle.Bold));
                    return;

                case "mathrm":
                case "mathup":
                case "rm":
                    row.Items.Add(Restyle(ParseArgument($"аргумента \\{name}"), MathLetterStyle.Plain, null));
                    return;

                case "mathit":
                    row.Items.Add(Restyle(ParseArgument($"аргумента \\{name}"), MathLetterStyle.Italic, null));
                    return;

                case "mathbf":
                    row.Items.Add(Restyle(ParseArgument($"аргумента \\{name}"), MathLetterStyle.Bold, null));
                    return;

                case "boldsymbol":
                case "bm":
                    row.Items.Add(Restyle(ParseArgument($"аргумента \\{name}"), MathLetterStyle.BoldItalic, null));
                    return;

                case "mathbb":
                    row.Items.Add(Restyle(ParseArgument($"аргумента \\{name}"), MathLetterStyle.Plain, MathScript.DoubleStruck));
                    return;

                case "mathcal":
                case "mathscr":
                    row.Items.Add(Restyle(ParseArgument($"аргумента \\{name}"), MathLetterStyle.Plain, MathScript.Script));
                    return;

                case "mathfrak":
                    row.Items.Add(Restyle(ParseArgument($"аргумента \\{name}"), MathLetterStyle.Plain, MathScript.Fraktur));
                    return;

                case "mathsf":
                    row.Items.Add(Restyle(ParseArgument($"аргумента \\{name}"), MathLetterStyle.Plain, MathScript.SansSerif));
                    return;

                case "mathtt":
                    row.Items.Add(Restyle(ParseArgument($"аргумента \\{name}"), MathLetterStyle.Plain, MathScript.Monospace));
                    return;

                case "color":
                case "textcolor":
                {
                    string color = ParseRawGroup("цветом").Trim();
                    string? hex = LatexSymbols.ColorHex(color)
                        ?? throw new FormulaSyntaxException($"Неизвестный цвет «{color}».", start);
                    row.Items.Add(Recolor(ParseArgument("окрашиваемого"), hex));
                    return;
                }

                case "left":
                    ParseLeftRight(row, start);
                    return;

                case "big":
                case "Big":
                case "bigg":
                case "Bigg":
                case "bigl":
                case "bigr":
                case "Bigl":
                case "Bigr":
                case "biggl":
                case "biggr":
                case "Biggl":
                case "Biggr":
                case "bigm":
                case "Bigm":
                {
                    // Размер скобки задаёт её содержимое: сама скобка остаётся знаком.
                    int delimiter = ReadDelimiter(start);
                    if (delimiter != 0) row.Items.Add(Run(char.ConvertFromUtf32(delimiter), MathLetterStyle.Italic));
                    return;
                }

                case "not":
                {
                    var negated = new MathRow();
                    ParseAtomInto(negated);
                    if (negated.Items.Count == 1 && negated.Items[0] is MathRunNode negatedRun)
                    {
                        row.Items.Add(Run(Negate(negatedRun.Text), MathLetterStyle.Italic));
                        return;
                    }
                    throw new FormulaSyntaxException("После \\not ожидается один знак.", start);
                }

                case "begin":
                    ParseEnvironment(row, start);
                    return;

                case "limits":
                case "nolimits":
                case "displaystyle":
                case "textstyle":
                case "scriptstyle":
                case "scriptscriptstyle":
                case "nonumber":
                case "notag":
                    return;

                case "right":
                case "middle":
                    throw new FormulaSyntaxException($"\\{name} без парной \\left.", start);

                case "end":
                    throw new FormulaSyntaxException("\\end без парной \\begin.", start);
            }

            throw new FormulaSyntaxException($"Неизвестная команда \\{name}.", start);
        }

        /// <summary>Команда из одного знака: \{, \|, \, и подобные.</summary>
        private void ParseSymbolCommand(char c, MathRow row, int start)
        {
            switch (c)
            {
                case '{': row.Items.Add(Run("{", MathLetterStyle.Italic)); return;
                case '}': row.Items.Add(Run("}", MathLetterStyle.Italic)); return;
                case '|': row.Items.Add(Run("\u2016", MathLetterStyle.Italic)); return;
                case '%': row.Items.Add(Run("%", MathLetterStyle.Italic)); return;
                case '$': row.Items.Add(Run("$", MathLetterStyle.Italic)); return;
                case '#': row.Items.Add(Run("#", MathLetterStyle.Italic)); return;
                case '&': row.Items.Add(Run("&", MathLetterStyle.Italic)); return;
                case '_': row.Items.Add(Run("_", MathLetterStyle.Italic)); return;
                case ',': row.Items.Add(Run("\u2009", MathLetterStyle.Italic)); return;
                case ':':
                case '>': row.Items.Add(Run("\u205F", MathLetterStyle.Italic)); return;
                case ';': row.Items.Add(Run("\u2004", MathLetterStyle.Italic)); return;
                case ' ': row.Items.Add(Run(" ", MathLetterStyle.Italic)); return;
                case '!': return;
                case '\\': throw new FormulaSyntaxException("Перенос строки \\\\ здесь не к месту.", start);
            }

            throw new FormulaSyntaxException($"Неизвестная команда \\{c}.", start);
        }

        /// <summary>Сумма, произведение, интеграл: пределы и выражение под знаком.</summary>
        private void ParseNary(MathRow row, int naryChar)
        {
            bool integral = naryChar >= 0x222B && naryChar <= 0x2233;
            bool limitsAboveBelow = !integral;

            MathRow? sub = null;
            MathRow? sup = null;

            while (true)
            {
                SkipSpaces();
                if (TryConsumeCommand("limits")) { limitsAboveBelow = true; continue; }
                if (TryConsumeCommand("nolimits")) { limitsAboveBelow = false; continue; }
                if (TryConsumeCommand("displaystyle")) continue;

                if (!AtEnd && Peek() == '_')
                {
                    if (sub is not null) throw new FormulaSyntaxException("Второй нижний предел подряд.", _pos);
                    _pos++;
                    sub = ParseArgument("нижнего предела");
                    continue;
                }

                if (!AtEnd && Peek() == '^')
                {
                    if (sup is not null) throw new FormulaSyntaxException("Второй верхний предел подряд.", _pos);
                    _pos++;
                    sup = ParseArgument("верхнего предела");
                    continue;
                }

                break;
            }

            // Выражение под знаком — следующий атом вместе с его индексами. Остальное
            // идёт за ним в той же строке и на вид от этого не отличается.
            var body = ParseOperand();

            row.Items.Add(new MathNaryNode
            {
                OperatorChar = naryChar,
                LimitsAboveBelow = limitsAboveBelow,
                Sub = sub,
                Sup = sup,
                Body = body
            });
        }

        /// <summary>Функция (sin, log, lim…) и её аргумент — следующий атом с индексами.</summary>
        private void ParseFunction(MathRow row, MathRow name, bool limitsBelow)
        {
            SkipSpaces();
            TryConsumeCommand("limits");
            TryConsumeCommand("nolimits");
            SkipSpaces();

            MathRow functionName = name;

            if (!AtEnd && (Peek() == '_' || Peek() == '^'))
            {
                if (limitsBelow)
                {
                    // Предел под именем функции: lim, max, min.
                    bool upper = Peek() == '^';
                    _pos++;
                    var limit = ParseArgument("предела");
                    functionName = RowOf(new MathLimitNode { Base = name, Limit = limit, Upper = upper });
                }
                else
                {
                    var scripted = new MathRow();
                    scripted.Items.Add(name);
                    ParseScripts(scripted);
                    functionName = scripted;
                }
            }

            var argument = ParseOperand();
            row.Items.Add(new MathFunctionNode { Name = Merge(functionName), Argument = argument });
        }

        /// <summary>
        /// Операнд знака суммы или функции: следующий атом с его индексами. Пусто,
        /// если дальше ничего нет.
        /// </summary>
        private MathRow ParseOperand()
        {
            var operand = new MathRow();
            SkipSpaces();

            if (AtEnd || Peek() == '}' || Peek() == '&' || LookingAt("\\\\")
                || LookingAtCommand("right") || LookingAtCommand("middle") || LookingAtCommand("end"))
                return operand;

            if (Peek() == '{')
            {
                _pos++;
                var group = ParseRow(stopAtCell: false);
                Expect('}', "группы");
                operand = Merge(group);
            }
            else
            {
                ParseAtomInto(operand);
            }

            SkipSpaces();
            while (!AtEnd && (Peek() == '^' || Peek() == '_' || Peek() == '\''))
            {
                if (Peek() == '\'')
                {
                    _pos++;
                    AttachScript(operand, sub: null, sup: RowOf(Run("\u2032", MathLetterStyle.Plain)));
                }
                else
                {
                    ParseScripts(operand);
                }
                SkipSpaces();
            }

            return Merge(operand);
        }

        /// <summary>\left … \middle … \right — скобки по высоте содержимого.</summary>
        private void ParseLeftRight(MathRow row, int start)
        {
            int open = ReadDelimiter(start);
            var node = new MathDelimiterNode { OpenChar = open, CloseChar = 0 };
            int separator = '|';

            while (true)
            {
                var item = ParseRow(stopAtCell: false);
                node.Items.Add(Merge(item));
                SkipSpaces();

                if (TryConsumeCommand("middle"))
                {
                    separator = ReadDelimiter(_pos);
                    continue;
                }

                if (TryConsumeCommand("right"))
                {
                    int close = ReadDelimiter(_pos);
                    row.Items.Add(new MathDelimiterNode
                    {
                        OpenChar = open,
                        CloseChar = close,
                        SeparatorChar = separator,
                        Grow = true
                    }.WithItems(node.Items));
                    return;
                }

                throw new FormulaSyntaxException("\\left без парной \\right.", start);
            }
        }

        /// <summary>Знак скобки после \left, \right, \middle, \big: «.» — скобки нет.</summary>
        private int ReadDelimiter(int start)
        {
            SkipSpaces();
            if (AtEnd) throw new FormulaSyntaxException("Нет знака скобки.", start);

            char c = Peek();
            if (c == '.') { _pos++; return 0; }

            if (c == '\\')
            {
                int commandStart = _pos;
                _pos++;
                if (AtEnd) throw new FormulaSyntaxException("Нет знака скобки.", start);

                if (!char.IsLetter(Peek()))
                {
                    char symbol = Peek();
                    _pos++;
                    return symbol switch
                    {
                        '{' => '{',
                        '}' => '}',
                        '|' => 0x2016,
                        _ => throw new FormulaSyntaxException($"\\{symbol} — не скобка.", commandStart)
                    };
                }

                int nameStart = _pos;
                while (!AtEnd && char.IsLetter(Peek())) _pos++;
                string name = _source.Substring(nameStart, _pos - nameStart);

                if (LatexSymbols.Delimiters.TryGetValue(name, out int delimiter)) return delimiter;
                throw new FormulaSyntaxException($"\\{name} — не скобка.", commandStart);
            }

            if (c == '<') { _pos++; return 0x27E8; }
            if (c == '>') { _pos++; return 0x27E9; }

            int codepoint = char.ConvertToUtf32(_source, _pos);
            _pos += char.IsHighSurrogate(c) ? 2 : 1;
            return codepoint;
        }

        private static int DelimiterChar(string text, int position)
        {
            if (text.Length == 0 || text == ".") return 0;
            if (text.StartsWith("\\", StringComparison.Ordinal))
            {
                string name = text.Substring(1);
                if (name == "{") return '{';
                if (name == "}") return '}';
                if (LatexSymbols.Delimiters.TryGetValue(name, out int delimiter)) return delimiter;
                throw new FormulaSyntaxException($"{text} — не скобка.", position);
            }
            return char.ConvertToUtf32(text, 0);
        }

        /// <summary>\begin{…} … \end{…}: матрицы, системы, строки формулы.</summary>
        private void ParseEnvironment(MathRow row, int start)
        {
            string name = ParseRawGroup("именем окружения").Trim();

            // Описание столбцов у array: {cc|c} — на вид формулы не влияет.
            if (name == "array" || name == "alignat" || name == "alignat*")
                ParseRawGroup("описанием столбцов");

            var lines = ParseLines(isEnvironment: true, out bool endedByEnd);
            if (!endedByEnd) throw new FormulaSyntaxException($"\\begin{{{name}}} без парной \\end{{{name}}}.", start);

            TryConsumeCommand("end");
            string endName = ParseRawGroup("именем окружения").Trim();
            if (endName != name)
                throw new FormulaSyntaxException($"\\begin{{{name}}} закрыт \\end{{{endName}}}.", start);

            // Пустая последняя строка после завершающего \\ в счёт не идёт.
            if (lines.Count > 1 && lines[^1].TrueForAll(cell => cell.IsEmpty)) lines.RemoveAt(lines.Count - 1);

            switch (name)
            {
                case "aligned":
                case "align":
                case "align*":
                case "alignat":
                case "alignat*":
                case "gathered":
                case "gather":
                case "gather*":
                case "split":
                case "eqnarray":
                case "eqnarray*":
                case "multline":
                case "multline*":
                {
                    var array = new MathMatrixNode { IsEquationArray = true };
                    foreach (var line in lines)
                    {
                        var joined = new MathRow();
                        foreach (var cell in line) joined.Items.AddRange(cell.Items);
                        array.Rows.Add(new List<MathRow> { Merge(joined) });
                    }
                    row.Items.Add(array);
                    return;
                }

                case "cases":
                case "dcases":
                case "rcases":
                {
                    var array = new MathMatrixNode { IsEquationArray = true };
                    foreach (var line in lines)
                    {
                        var joined = new MathRow();
                        for (int i = 0; i < line.Count; i++)
                        {
                            if (i > 0) joined.Items.Add(Run("\u2003", MathLetterStyle.Italic));
                            joined.Items.AddRange(line[i].Items);
                        }
                        array.Rows.Add(new List<MathRow> { Merge(joined) });
                    }

                    var delimiter = new MathDelimiterNode
                    {
                        OpenChar = name == "rcases" ? 0 : '{',
                        CloseChar = name == "rcases" ? '}' : 0
                    };
                    delimiter.Items.Add(RowOf(array));
                    row.Items.Add(delimiter);
                    return;
                }

                case "matrix":
                case "smallmatrix":
                case "array":
                case "pmatrix":
                case "bmatrix":
                case "Bmatrix":
                case "vmatrix":
                case "Vmatrix":
                {
                    var matrix = new MathMatrixNode();
                    int columns = 0;
                    foreach (var line in lines) columns = Math.Max(columns, line.Count);
                    foreach (var line in lines)
                    {
                        var cells = new List<MathRow>();
                        foreach (var cell in line) cells.Add(Merge(cell));
                        while (cells.Count < columns) cells.Add(new MathRow());
                        matrix.Rows.Add(cells);
                    }

                    (int open, int close) = name switch
                    {
                        "pmatrix" => ('(', ')'),
                        "bmatrix" => ('[', ']'),
                        "Bmatrix" => ('{', '}'),
                        "vmatrix" => ('|', '|'),
                        "Vmatrix" => (0x2016, 0x2016),
                        _ => (-1, -1)
                    };

                    if (open < 0)
                    {
                        row.Items.Add(matrix);
                    }
                    else
                    {
                        var delimiter = new MathDelimiterNode { OpenChar = open, CloseChar = close };
                        delimiter.Items.Add(RowOf(matrix));
                        row.Items.Add(delimiter);
                    }
                    return;
                }
            }

            throw new FormulaSyntaxException($"Неизвестное окружение «{name}».", start);
        }

        // ── Узлы ───────────────────────────────────────────────────────────

        private static MathRunNode Run(string text, MathLetterStyle style)
            => new() { Text = text, Style = style };

        private static MathRunNode TextRun(string text, MathLetterStyle style)
            => new() { Text = UnescapeText(text), Style = style, IsNormalText = true };

        private static MathRow RowOf(MathNode node)
        {
            var row = new MathRow();
            row.Items.Add(node);
            return row;
        }

        /// <summary>Знак с перечёркнутым вариантом: = → ≠, ∈ → ∉. Иначе — знак с косой чертой поверх.</summary>
        private static string Negate(string text)
        {
            return text switch
            {
                "=" => "\u2260",
                "\u2208" => "\u2209",
                "\u2282" => "\u2284",
                "\u2283" => "\u2285",
                "\u2286" => "\u2288",
                "\u2287" => "\u2289",
                "<" => "\u226E",
                ">" => "\u226F",
                "\u2264" => "\u2270",
                "\u2265" => "\u2271",
                "\u2261" => "\u2262",
                "\u223C" => "\u2241",
                "\u2248" => "\u2249",
                "\u2203" => "\u2204",
                _ => text + "\u0338"
            };
        }

        /// <summary>Начертание всех ранов аргумента.</summary>
        private static MathNode Restyle(MathRow row, MathLetterStyle style, MathScript? script)
        {
            var result = new MathRow();
            foreach (var item in row.Items) result.Items.Add(RestyleNode(item, style, script));
            return result;
        }

        private static MathNode RestyleNode(MathNode node, MathLetterStyle style, MathScript? script)
        {
            switch (node)
            {
                case MathRunNode run when !run.IsNormalText:
                    return new MathRunNode
                    {
                        Text = run.Text,
                        Style = style,
                        Script = script ?? run.Script,
                        Color = run.Color
                    };

                case MathRow nested:
                {
                    var result = new MathRow();
                    foreach (var item in nested.Items) result.Items.Add(RestyleNode(item, style, script));
                    return result;
                }

                case MathScriptNode scripted:
                    return new MathScriptNode
                    {
                        Base = (MathRow)RestyleNode(scripted.Base, style, script),
                        Sub = scripted.Sub,
                        Sup = scripted.Sup,
                        Pre = scripted.Pre
                    };

                default:
                    return node;
            }
        }

        /// <summary>Цвет всех ранов аргумента.</summary>
        private static MathNode Recolor(MathRow row, string hex)
        {
            var result = new MathRow();
            foreach (var item in row.Items) result.Items.Add(RecolorNode(item, hex));
            return result;
        }

        private static MathNode RecolorNode(MathNode node, string hex)
        {
            switch (node)
            {
                case MathRunNode run:
                    return new MathRunNode
                    {
                        Text = run.Text,
                        Style = run.Style,
                        Script = run.Script,
                        IsNormalText = run.IsNormalText,
                        Color = hex
                    };

                case MathRow nested:
                {
                    var result = new MathRow();
                    foreach (var item in nested.Items) result.Items.Add(RecolorNode(item, hex));
                    return result;
                }

                default:
                    return node;
            }
        }

        /// <summary>
        /// Склеивает соседние раны одного вида в один, как их пишет Word, и
        /// разворачивает ряд из одного вложенного ряда.
        /// </summary>
        internal static MathRow Merge(MathRow row)
        {
            var merged = new MathRow();

            foreach (var item in row.Items)
            {
                if (item is MathRow nested && row.Items.Count == 1)
                    return Merge(nested);

                if (item is MathRunNode run
                    && merged.Items.Count > 0
                    && merged.Items[^1] is MathRunNode previous
                    && previous.Style == run.Style
                    && previous.Script == run.Script
                    && previous.IsNormalText == run.IsNormalText
                    && previous.Color == run.Color
                    && SameRunClass(previous.Text, run.Text))
                {
                    merged.Items[^1] = new MathRunNode
                    {
                        Text = previous.Text + run.Text,
                        Style = run.Style,
                        Script = run.Script,
                        IsNormalText = run.IsNormalText,
                        Color = run.Color
                    };
                    continue;
                }

                merged.Items.Add(item);
            }

            return merged;
        }

        /// <summary>
        /// Раны склеиваются только в пределах одного рода знаков: буквы с буквами и
        /// цифрами, знаки действий отдельно, — как Word делит набранную формулу.
        /// </summary>
        private static bool SameRunClass(string a, string b)
        {
            if (a.Length == 0 || b.Length == 0) return false;
            return IsWordChar(a[^1]) && IsWordChar(b[0]);
        }

        private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '.' || c == ',';

        private static string UnescapeText(string text)
        {
            var builder = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\\' && i + 1 < text.Length && "{}%$#&_ ".IndexOf(text[i + 1]) >= 0)
                {
                    builder.Append(text[i + 1]);
                    i++;
                    continue;
                }
                if (c == '~') { builder.Append('\u00A0'); continue; }
                builder.Append(c);
            }
            return builder.ToString();
        }

        private static bool IsZeroLength(string text)
        {
            string trimmed = text.Trim();
            if (trimmed.Length == 0) return false;

            int unit = 0;
            while (unit < trimmed.Length && (char.IsDigit(trimmed[unit]) || trimmed[unit] == '.')) unit++;
            return double.TryParse(trimmed.Substring(0, unit), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                   && value == 0.0;
        }

        // ── Чтение записи ──────────────────────────────────────────────────

        private bool AtEnd => _pos >= _source.Length;

        private char Peek() => _source[_pos];

        private void SkipSpaces()
        {
            while (!AtEnd && char.IsWhiteSpace(_source[_pos])) _pos++;
        }

        private bool LookingAt(string text)
            => string.CompareOrdinal(_source, _pos, text, 0, text.Length) == 0;

        private bool LookingAtCommand(string name)
        {
            if (!LookingAt("\\" + name)) return false;
            int after = _pos + 1 + name.Length;
            return after >= _source.Length || !char.IsLetter(_source[after]);
        }

        private bool TryConsume(string text)
        {
            if (!LookingAt(text)) return false;
            _pos += text.Length;
            return true;
        }

        private bool TryConsumeCommand(string name)
        {
            SkipSpaces();
            if (!LookingAtCommand(name)) return false;
            _pos += 1 + name.Length;
            return true;
        }

        /// <summary>Необязательная высота после \\: \\[2pt].</summary>
        private void SkipOptionalLength()
        {
            int save = _pos;
            SkipSpaces();
            if (AtEnd || Peek() != '[') { _pos = save; return; }

            int close = _source.IndexOf(']', _pos);
            if (close < 0) { _pos = save; return; }
            _pos = close + 1;
        }

        private void Expect(char c, string what)
        {
            SkipSpaces();
            if (AtEnd || Peek() != c)
                throw new FormulaSyntaxException($"Не закрыта скобка {what}: нужна «{c}».", _pos);
            _pos++;
        }
    }

    internal static class MathDelimiterNodeExtensions
    {
        /// <summary>Узел скобок с перенесёнными аргументами.</summary>
        public static MathDelimiterNode WithItems(this MathDelimiterNode node, List<MathRow> items)
        {
            node.Items.AddRange(items);
            return node;
        }
    }
}
