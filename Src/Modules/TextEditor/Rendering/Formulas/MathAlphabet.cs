namespace Writersword.Modules.TextEditor.Rendering.Formulas
{
    /// <summary>
    /// Роль знака в формуле: от пары соседних ролей зависит просвет между знаками.
    /// </summary>
    internal enum MathAtomClass
    {
        /// <summary>Обычный знак: буква, цифра.</summary>
        Ordinary = 0,
        /// <summary>Большой оператор и имя функции.</summary>
        Operator = 1,
        /// <summary>Знак действия между двумя операндами: плюс, минус, умножение.</summary>
        Binary = 2,
        /// <summary>Знак отношения: равно, меньше, стрелка.</summary>
        Relation = 3,
        Open = 4,
        Close = 5,
        Punctuation = 6,
        /// <summary>Дробь и выражение в скобках.</summary>
        Inner = 7,
        /// <summary>Знака нет: начало или конец ряда.</summary>
        None = 8
    }

    /// <summary>
    /// Буквы и знаки формулы: какой знак Юникода рисуется для буквы в заданном
    /// начертании и какую роль знак играет в формуле.
    ///
    /// Курсивные, жирные, рукописные и прочие буквы формул в Юникоде — отдельные
    /// знаки блока «Математические буквенно-цифровые символы», и математический
    /// шрифт рисует их своими глифами. Обычная «a» в Cambria Math прямая; курсивная
    /// «a» переменной — это знак U+1D44E.
    /// </summary>
    internal static class MathAlphabet
    {
        /// <summary>Знак, которым буква или цифра рисуется в заданном начертании.</summary>
        public static int Map(int codepoint, MathLetterStyle style, MathScript script)
        {
            bool bold = style is MathLetterStyle.Bold or MathLetterStyle.BoldItalic;
            bool italic = style is MathLetterStyle.Italic or MathLetterStyle.BoldItalic;

            if (codepoint >= 'A' && codepoint <= 'Z')
                return MapLatin(codepoint - 'A', upper: true, bold, italic, script, codepoint);

            if (codepoint >= 'a' && codepoint <= 'z')
                return MapLatin(codepoint - 'a', upper: false, bold, italic, script, codepoint);

            if (codepoint >= '0' && codepoint <= '9')
            {
                int digit = codepoint - '0';
                return script switch
                {
                    MathScript.DoubleStruck => 0x1D7D8 + digit,
                    MathScript.SansSerif => (bold ? 0x1D7EC : 0x1D7E2) + digit,
                    MathScript.Monospace => 0x1D7F6 + digit,
                    _ => bold ? 0x1D7CE + digit : codepoint
                };
            }

            if (script != MathScript.Roman) return codepoint;

            // Греческие буквы: строчные в формуле курсивные, прописные — прямые,
            // пока начертание не жирное.
            if (codepoint >= 0x03B1 && codepoint <= 0x03C9)
            {
                int index = codepoint - 0x03B1;
                if (bold && italic) return 0x1D736 + index;
                if (bold) return 0x1D6C2 + index;
                if (italic) return 0x1D6FC + index;
                return codepoint;
            }

            if (codepoint >= 0x0391 && codepoint <= 0x03A9 && codepoint != 0x03A2)
            {
                int index = codepoint - 0x0391;
                if (bold && italic) return 0x1D71C + index;
                if (bold) return 0x1D6A8 + index;
                return codepoint;
            }

            if (italic && !bold)
            {
                switch (codepoint)
                {
                    case 0x2202: return 0x1D715;
                    case 0x03F5: return 0x1D716;
                    case 0x03D1: return 0x1D717;
                    case 0x03F0: return 0x1D718;
                    case 0x03D5: return 0x1D719;
                    case 0x03F1: return 0x1D71A;
                    case 0x03D6: return 0x1D71B;
                }
            }

            return codepoint;
        }

        private static int MapLatin(int index, bool upper, bool bold, bool italic, MathScript script, int original)
        {
            switch (script)
            {
                case MathScript.Script:
                    if (bold) return (upper ? 0x1D4D0 : 0x1D4EA) + index;
                    if (upper)
                    {
                        return (char)original switch
                        {
                            'B' => 0x212C,
                            'E' => 0x2130,
                            'F' => 0x2131,
                            'H' => 0x210B,
                            'I' => 0x2110,
                            'L' => 0x2112,
                            'M' => 0x2133,
                            'R' => 0x211B,
                            _ => 0x1D49C + index
                        };
                    }
                    return (char)original switch
                    {
                        'e' => 0x212F,
                        'g' => 0x210A,
                        'o' => 0x2134,
                        _ => 0x1D4B6 + index
                    };

                case MathScript.Fraktur:
                    if (bold) return (upper ? 0x1D56C : 0x1D586) + index;
                    if (upper)
                    {
                        return (char)original switch
                        {
                            'C' => 0x212D,
                            'H' => 0x210C,
                            'I' => 0x2111,
                            'R' => 0x211C,
                            'Z' => 0x2128,
                            _ => 0x1D504 + index
                        };
                    }
                    return 0x1D51E + index;

                case MathScript.DoubleStruck:
                    if (upper)
                    {
                        return (char)original switch
                        {
                            'C' => 0x2102,
                            'H' => 0x210D,
                            'N' => 0x2115,
                            'P' => 0x2119,
                            'Q' => 0x211A,
                            'R' => 0x211D,
                            'Z' => 0x2124,
                            _ => 0x1D538 + index
                        };
                    }
                    return 0x1D552 + index;

                case MathScript.SansSerif:
                    if (bold && italic) return (upper ? 0x1D63C : 0x1D656) + index;
                    if (bold) return (upper ? 0x1D5D4 : 0x1D5EE) + index;
                    if (italic) return (upper ? 0x1D608 : 0x1D622) + index;
                    return (upper ? 0x1D5A0 : 0x1D5BA) + index;

                case MathScript.Monospace:
                    return (upper ? 0x1D670 : 0x1D68A) + index;
            }

            if (bold && italic) return (upper ? 0x1D468 : 0x1D482) + index;
            if (bold) return (upper ? 0x1D400 : 0x1D41A) + index;
            if (italic)
            {
                // У курсивной «h» своё место в Юникоде — постоянная Планка.
                if (!upper && original == 'h') return 0x210E;
                return (upper ? 0x1D434 : 0x1D44E) + index;
            }

            return original;
        }

        /// <summary>
        /// Знак, которым символ показывается в формуле: дефис с клавиатуры — минус,
        /// звёздочка — знак умножения-звёздочка, как у Word.
        /// </summary>
        public static int DisplayChar(int codepoint)
        {
            return codepoint switch
            {
                '-' => 0x2212,
                '*' => 0x2217,
                _ => codepoint
            };
        }

        /// <summary>Пробельный знак: в формуле он даёт просвет и роли не имеет.</summary>
        public static bool IsSpace(int codepoint)
        {
            return codepoint == ' ' || codepoint == 0x00A0
                || (codepoint >= 0x2000 && codepoint <= 0x200B)
                || codepoint == 0x205F || codepoint == 0x3000;
        }

        /// <summary>
        /// Невидимые знаки формул: применение функции, невидимое умножение,
        /// невидимый разделитель и плюс. Места не занимают.
        /// </summary>
        public static bool IsInvisible(int codepoint)
            => codepoint >= 0x2061 && codepoint <= 0x2064;

        /// <summary>Роль знака в формуле.</summary>
        public static MathAtomClass ClassOf(int codepoint)
        {
            switch (codepoint)
            {
                case '+':
                case '-':
                case '*':
                case 0x00B1:
                case 0x00D7:
                case 0x00F7:
                case 0x2022:
                case 0x2212:
                case 0x2213:
                case 0x2214:
                case 0x2215:
                case 0x2216:
                case 0x2217:
                case 0x2218:
                case 0x2219:
                case 0x2227:
                case 0x2228:
                case 0x2229:
                case 0x222A:
                case 0x2238:
                case 0x2240:
                case 0x228C:
                case 0x228D:
                case 0x228E:
                case 0x22C4:
                case 0x22C5:
                case 0x22C6:
                case 0x22C7:
                case 0x2A2F:
                    return MathAtomClass.Binary;

                case '=':
                case '<':
                case '>':
                case ':':
                case 0x2223:
                case 0x2224:
                case 0x2225:
                case 0x2226:
                case 0x221D:
                    return MathAtomClass.Relation;

                case ',':
                case ';':
                    return MathAtomClass.Punctuation;

                case '(':
                case '[':
                case '{':
                case 0x2308:
                case 0x230A:
                case 0x27E6:
                case 0x27E8:
                case 0x27EA:
                    return MathAtomClass.Open;

                case ')':
                case ']':
                case '}':
                case 0x2309:
                case 0x230B:
                case 0x27E7:
                case 0x27E9:
                case 0x27EB:
                    return MathAtomClass.Close;
            }

            if (IsLargeOperator(codepoint)) return MathAtomClass.Operator;

            // Многоточия (⋮ ⋯ ⋰ ⋱) — обычные знаки, хоть и стоят среди отношений:
            // знаки действий по сторонам от них остаются действиями.
            if (codepoint >= 0x22EE && codepoint <= 0x22F1) return MathAtomClass.Ordinary;

            // Стрелки.
            if ((codepoint >= 0x2190 && codepoint <= 0x21FF)
                || (codepoint >= 0x27F0 && codepoint <= 0x27FF)
                || (codepoint >= 0x2900 && codepoint <= 0x297F))
                return MathAtomClass.Relation;

            // Принадлежность и содержание.
            if (codepoint >= 0x2208 && codepoint <= 0x220D) return MathAtomClass.Relation;

            // Действия в кружке и квадрате.
            if (codepoint >= 0x2293 && codepoint <= 0x22A1) return MathAtomClass.Binary;
            if (codepoint >= 0x22BA && codepoint <= 0x22BD) return MathAtomClass.Binary;

            // Сравнения, подобия, включения, следования.
            if (codepoint >= 0x223C && codepoint <= 0x228B) return MathAtomClass.Relation;
            if (codepoint >= 0x228F && codepoint <= 0x2292) return MathAtomClass.Relation;
            if (codepoint >= 0x22A2 && codepoint <= 0x22B8) return MathAtomClass.Relation;
            if (codepoint >= 0x22C8 && codepoint <= 0x22FF) return MathAtomClass.Relation;
            if (codepoint >= 0x2A66 && codepoint <= 0x2AFF) return MathAtomClass.Relation;
            if (codepoint >= 0x2A22 && codepoint <= 0x2A65) return MathAtomClass.Binary;

            return MathAtomClass.Ordinary;
        }

        /// <summary>Большой оператор: сумма, произведение, интеграл, объединение.</summary>
        public static bool IsLargeOperator(int codepoint)
        {
            return (codepoint >= 0x220F && codepoint <= 0x2211)
                || (codepoint >= 0x222B && codepoint <= 0x2233)
                || (codepoint >= 0x22C0 && codepoint <= 0x22C3)
                || (codepoint >= 0x2A00 && codepoint <= 0x2A1C);
        }
    }
}
