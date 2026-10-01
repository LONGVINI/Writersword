using System;
using System.Collections.Generic;
using System.Text;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Rendering
{
    /// <summary>
    /// Вычисленный маркер одного элемента списка.
    /// Text — готовая строка маркера («1.», «a)», «•», пользовательский символ).
    /// Геометрия (позиция маркера/текста) берётся из ListProperties в момент отрисовки.
    /// </summary>
    public readonly struct ListMarkerInfo
    {
        public string Text { get; }
        public bool IsNumbered { get; }

        public ListMarkerInfo(string text, bool isNumbered)
        {
            Text = text;
            IsNumbered = isNumbered;
        }
    }

    /// <summary>
    /// Движок нумерации списков.
    /// За один проход по блокам документа в порядке следования вычисляет строку
    /// маркера для каждого параграфа-элемента списка.
    /// Счётчики ведутся отдельно по каждому ListId и по каждому уровню вложенности:
    /// элементы с одним ListId образуют единую нумерацию даже если между ними
    /// стоят обычные параграфы. Появление элемента более мелкого уровня сбрасывает
    /// счётчики всех более глубоких уровней этого списка.
    /// Чистая функция без состояния — вызывается на каждый пересчёт раскладки.
    /// </summary>
    public static class ListNumberingEngine
    {
        private const int MaxLevel = 8;

        /// <summary>
        /// Строит карту «параграф → маркер» для всех элементов списков в наборе блоков.
        /// Параграфы вне списков (ListProperties == null или MarkerType == None) в карту не попадают.
        /// </summary>
        public static Dictionary<ParagraphBlock, ListMarkerInfo> Compute(IReadOnlyList<BlockModel> blocks)
        {
            var result = new Dictionary<ParagraphBlock, ListMarkerInfo>();
            if (blocks is null || blocks.Count == 0) return result;

            // Счётчики нумерованных элементов по каждому списку и уровню.
            var counters = new Dictionary<Guid, int[]>();

            foreach (var block in blocks)
            {
                // Списки из Word считаются, как у Word, в порядке документа и сквозь
                // таблицы: пункт в ячейке продолжает счёт своего списка из основного
                // текста. Списки Writersword в ячейках по-прежнему считаются каждой
                // ячейкой отдельно (DocumentCanvas.ApplyListMarkerTextsInTables).
                if (block is TableBlock table)
                {
                    foreach (var cell in OrderedCells(table))
                        foreach (var cellPara in cell.Paragraphs)
                            if (cellPara.ListProperties?.WordLevels is not null)
                                ComputeParagraph(cellPara, counters, result);
                    continue;
                }

                if (block is not ParagraphBlock para) continue;

                ComputeParagraph(para, counters, result);
            }

            return result;
        }

        /// <summary>Ячейки таблицы в порядке Word: по строкам, в строке слева направо.</summary>
        private static List<TableCell> OrderedCells(TableBlock table)
        {
            var cells = new List<TableCell>(table.Cells);
            cells.Sort((a, b) => a.Row != b.Row ? a.Row.CompareTo(b.Row) : a.Column.CompareTo(b.Column));
            return cells;
        }

        /// <summary>Маркер одного абзаца; счётчики его списка сдвигаются.</summary>
        private static void ComputeParagraph(
            ParagraphBlock para, Dictionary<Guid, int[]> counters, Dictionary<ParagraphBlock, ListMarkerInfo> result)
        {
            var lp = para.ListProperties;
            if (lp is null || lp.MarkerType == ListMarkerType.None) return;

            // Тип маркера текущего уровня (для многоуровневого списка каждый уровень свой).
            var effType = lp.EffectiveMarkerTypeForLevel();
            bool numbered = (int)effType >= 10;

            if (!numbered)
            {
                // Маркер уровня Word — сам знак из его шаблона, пустой шаблон — без маркера.
                string bullet = lp.WordLevelAt(lp.Level) is { } bulletLevel
                    ? bulletLevel.Text
                    : BuildBulletMarker(lp, effType);
                result[para] = new ListMarkerInfo(bullet, isNumbered: false);
                return;
            }

            if (!counters.TryGetValue(lp.ListId, out var levels))
            {
                levels = new int[MaxLevel + 1];
                counters[lp.ListId] = levels;
            }

            int level = Math.Clamp(lp.Level, 0, MaxLevel);

            // Появление элемента уровня level сбрасывает более глубокие уровни.
            for (int d = level + 1; d <= MaxLevel; d++)
                levels[d] = 0;

            var wordLevel = lp.WordLevelAt(level);

            if (!lp.ContinueNumbering)
                levels[level] = lp.StartAt;              // Явный перезапуск нумерации.
            else if (levels[level] == 0)
                levels[level] = wordLevel?.Start ?? lp.StartAt; // Первый элемент этого уровня.
            else
                levels[level] += 1;

            string marker = wordLevel is not null
                ? BuildWordMarker(lp, wordLevel, levels)
                : BuildNumberMarker(lp, effType, levels[level]);

            result[para] = new ListMarkerInfo(marker, isNumbered: true);
        }

        // ── Шаблон Word ──────────────────────────────────────────────────

        /// <summary>
        /// Номер по шаблону уровня Word: %1…%9 заменяются номерами уровней 1…9 в формате
        /// каждого из них, остальной текст остаётся как написан. «%1.%2.» даёт «8.1.»,
        /// «Глава %1.» — «Глава 1.», шаблон без знака процента («§») — сам себя.
        /// Уровень, у которого ещё не было пункта, подставляет свой начальный номер.
        /// </summary>
        private static string BuildWordMarker(ListProperties lp, WordListLevel wordLevel, int[] levels)
        {
            string template = wordLevel.Text;
            if (template.IndexOf('%') < 0) return template;

            var sb = new StringBuilder(template.Length + 8);
            for (int i = 0; i < template.Length; i++)
            {
                char c = template[i];
                if (c == '%' && i + 1 < template.Length && template[i + 1] >= '1' && template[i + 1] <= '9')
                {
                    int refLevel = template[i + 1] - '1';
                    var refDef = lp.WordLevelAt(refLevel);
                    int value = levels[refLevel] > 0 ? levels[refLevel] : (refDef?.Start ?? 1);
                    var refFormat = refDef?.Format ?? ListMarkerType.Decimal;
                    sb.Append(FormatWordNumber(value, refFormat, lp.NumberLanguage));
                    i++;
                    continue;
                }

                sb.Append(c);
            }

            return sb.ToString();
        }

        /// <summary>Номер в формате уровня Word, включая форматы, которых нет у списков Writersword.</summary>
        private static string FormatWordNumber(int number, ListMarkerType format, string? language)
        {
            bool russian = language is null || language.StartsWith("ru", StringComparison.OrdinalIgnoreCase);

            return format switch
            {
                ListMarkerType.Ordinal => russian
                    ? number.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-й"
                    : number.ToString(System.Globalization.CultureInfo.InvariantCulture) + EnglishOrdinalSuffix(number),
                ListMarkerType.CardinalText => Capitalize(russian ? RussianCardinal(number) : EnglishCardinal(number)),
                ListMarkerType.OrdinalText => Capitalize(russian ? RussianOrdinal(number) : EnglishOrdinal(number)),
                ListMarkerType.RussianLower => RussianLetters(number, upper: false),
                ListMarkerType.RussianUpper => RussianLetters(number, upper: true),
                ListMarkerType.Chicago => ChicagoMark(number),
                ListMarkerType.LowerAlpha => WordLetters(number, upper: false),
                ListMarkerType.UpperAlpha => WordLetters(number, upper: true),
                // Маркированный уровень в номере чужого уровня Word пишет цифрой.
                ListMarkerType.Bullet or ListMarkerType.Custom or ListMarkerType.Dash
                    or ListMarkerType.Square or ListMarkerType.Circle or ListMarkerType.Arrow
                    => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => FormatNumber(number, format)
            };
        }

        /// <summary>Буквы Word: a…z, затем aa, bb… — буква повторяется, а не идёт двузначный счёт.</summary>
        private static string WordLetters(int number, bool upper)
        {
            if (number < 1) number = 1;
            char letter = (char)((upper ? 'A' : 'a') + (number - 1) % 26);
            return new string(letter, (number - 1) / 26 + 1);
        }

        // Русский алфавит нумерации Word: без ё, й, ъ, ы, ь.
        private const string RussianNumberingLetters = "абвгдежзиклмнопрстуфхцчшщэюя";

        private static string RussianLetters(int number, bool upper)
        {
            if (number < 1) number = 1;
            char letter = RussianNumberingLetters[(number - 1) % RussianNumberingLetters.Length];
            if (upper) letter = char.ToUpperInvariant(letter);
            return new string(letter, (number - 1) / RussianNumberingLetters.Length + 1);
        }

        // Знаки Чикагского руководства; после четвёртого идут удвоенные, затем утроенные.
        private static readonly string[] ChicagoMarks = { "*", "\u2020", "\u2021", "\u00A7" };

        private static string ChicagoMark(int number)
        {
            if (number < 1) number = 1;
            string mark = ChicagoMarks[(number - 1) % ChicagoMarks.Length];
            int repeat = (number - 1) / ChicagoMarks.Length + 1;
            var sb = new StringBuilder(mark.Length * repeat);
            for (int i = 0; i < repeat; i++) sb.Append(mark);
            return sb.ToString();
        }

        private static string Capitalize(string text)
            => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);

        private static string EnglishOrdinalSuffix(int number)
        {
            int lastTwo = number % 100;
            if (lastTwo is >= 11 and <= 13) return "th";
            return (number % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        }

        private static readonly string[] RussianUnits =
            { "", "один", "два", "три", "четыре", "пять", "шесть", "семь", "восемь", "девять" };
        private static readonly string[] RussianTeens =
            { "десять", "одиннадцать", "двенадцать", "тринадцать", "четырнадцать", "пятнадцать",
              "шестнадцать", "семнадцать", "восемнадцать", "девятнадцать" };
        private static readonly string[] RussianTens =
            { "", "", "двадцать", "тридцать", "сорок", "пятьдесят", "шестьдесят", "семьдесят", "восемьдесят", "девяносто" };
        private static readonly string[] RussianHundreds =
            { "", "сто", "двести", "триста", "четыреста", "пятьсот", "шестьсот", "семьсот", "восемьсот", "девятьсот" };

        /// <summary>Количественное числительное по-русски до 999 999; больше — цифрами.</summary>
        private static string RussianCardinal(int number)
        {
            if (number <= 0 || number > 999_999) return number.ToString(System.Globalization.CultureInfo.InvariantCulture);

            var parts = new List<string>();
            int thousands = number / 1000;
            if (thousands > 0)
            {
                parts.Add(RussianBelowThousand(thousands, feminine: true));
                int lastTwo = thousands % 100, last = thousands % 10;
                parts.Add(lastTwo is >= 11 and <= 14 ? "тысяч" : last == 1 ? "тысяча" : last is >= 2 and <= 4 ? "тысячи" : "тысяч");
            }

            int rest = number % 1000;
            if (rest > 0) parts.Add(RussianBelowThousand(rest, feminine: false));

            return string.Join(" ", parts);
        }

        private static string RussianBelowThousand(int number, bool feminine)
        {
            var parts = new List<string>();
            if (number / 100 > 0) parts.Add(RussianHundreds[number / 100]);

            int lastTwo = number % 100;
            if (lastTwo >= 10 && lastTwo < 20)
            {
                parts.Add(RussianTeens[lastTwo - 10]);
            }
            else
            {
                if (lastTwo / 10 > 0) parts.Add(RussianTens[lastTwo / 10]);
                int unit = lastTwo % 10;
                if (unit > 0)
                    parts.Add(feminine && unit == 1 ? "одна" : feminine && unit == 2 ? "две" : RussianUnits[unit]);
            }

            return string.Join(" ", parts);
        }

        private static readonly string[] RussianOrdinalUnits =
            { "", "первый", "второй", "третий", "четвёртый", "пятый", "шестой", "седьмой", "восьмой", "девятый" };
        private static readonly string[] RussianOrdinalTeens =
            { "десятый", "одиннадцатый", "двенадцатый", "тринадцатый", "четырнадцатый", "пятнадцатый",
              "шестнадцатый", "семнадцатый", "восемнадцатый", "девятнадцатый" };
        private static readonly string[] RussianOrdinalTens =
            { "", "", "двадцатый", "тридцатый", "сороковой", "пятидесятый", "шестидесятый", "семидесятый",
              "восьмидесятый", "девяностый" };
        private static readonly string[] RussianOrdinalHundreds =
            { "", "сотый", "двухсотый", "трёхсотый", "четырёхсотый", "пятисотый", "шестисотый", "семисотый",
              "восьмисотый", "девятисотый" };

        /// <summary>
        /// Порядковое числительное по-русски до 999: порядковым становится последнее слово,
        /// остальные остаются количественными («двадцать первый», «сто второй»).
        /// Больше — «N-й».
        /// </summary>
        private static string RussianOrdinal(int number)
        {
            if (number <= 0 || number > 999)
                return number.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-й";

            int hundreds = number / 100, lastTwo = number % 100, tens = lastTwo / 10, unit = lastTwo % 10;
            var parts = new List<string>();

            if (lastTwo == 0)
            {
                parts.Add(RussianOrdinalHundreds[hundreds]);
                return string.Join(" ", parts);
            }

            if (hundreds > 0) parts.Add(RussianHundreds[hundreds]);

            if (lastTwo >= 10 && lastTwo < 20)
            {
                parts.Add(RussianOrdinalTeens[lastTwo - 10]);
            }
            else if (unit == 0)
            {
                parts.Add(RussianOrdinalTens[tens]);
            }
            else
            {
                if (tens > 0) parts.Add(RussianTens[tens]);
                parts.Add(RussianOrdinalUnits[unit]);
            }

            return string.Join(" ", parts);
        }

        private static readonly string[] EnglishUnits =
            { "", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven",
              "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen" };
        private static readonly string[] EnglishTens =
            { "", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety" };

        /// <summary>Количественное числительное по-английски до 999 999; больше — цифрами.</summary>
        private static string EnglishCardinal(int number)
        {
            if (number <= 0 || number > 999_999) return number.ToString(System.Globalization.CultureInfo.InvariantCulture);

            var parts = new List<string>();
            if (number / 1000 > 0)
            {
                parts.Add(EnglishBelowThousand(number / 1000));
                parts.Add("thousand");
            }

            if (number % 1000 > 0) parts.Add(EnglishBelowThousand(number % 1000));
            return string.Join(" ", parts);
        }

        private static string EnglishBelowThousand(int number)
        {
            var parts = new List<string>();
            if (number / 100 > 0)
            {
                parts.Add(EnglishUnits[number / 100]);
                parts.Add("hundred");
            }

            int lastTwo = number % 100;
            if (lastTwo > 0 && lastTwo < 20) parts.Add(EnglishUnits[lastTwo]);
            else if (lastTwo >= 20)
                parts.Add(lastTwo % 10 == 0 ? EnglishTens[lastTwo / 10] : EnglishTens[lastTwo / 10] + "-" + EnglishUnits[lastTwo % 10]);

            return string.Join(" ", parts);
        }

        /// <summary>Порядковое числительное по-английски: меняется последнее слово.</summary>
        private static string EnglishOrdinal(int number)
        {
            string cardinal = EnglishCardinal(number);
            if (cardinal.Length == 0 || char.IsDigit(cardinal[0]))
                return number.ToString(System.Globalization.CultureInfo.InvariantCulture) + EnglishOrdinalSuffix(number);

            int split = Math.Max(cardinal.LastIndexOf(' '), cardinal.LastIndexOf('-')) + 1;
            string head = cardinal.Substring(0, split);
            string last = cardinal.Substring(split);

            string ordinalLast = last switch
            {
                "one" => "first",
                "two" => "second",
                "three" => "third",
                "five" => "fifth",
                "eight" => "eighth",
                "nine" => "ninth",
                "twelve" => "twelfth",
                _ when last.EndsWith("y", StringComparison.Ordinal) => last.Substring(0, last.Length - 1) + "ieth",
                _ => last + "th"
            };

            return head + ordinalLast;
        }

        // ── Маркированные ─────────────────────────────────────────────────

        private static string BuildBulletMarker(ListProperties lp, ListMarkerType type)
        {
            return type switch
            {
                ListMarkerType.Bullet => "•",   // •
                ListMarkerType.Dash => "–",     // –
                ListMarkerType.Arrow => "➤",    // ➤
                ListMarkerType.Square => "▪",   // ▪
                ListMarkerType.Circle => "◦",   // ◦
                ListMarkerType.Custom => string.IsNullOrEmpty(lp.CustomMarker)
                    ? "•" : lp.CustomMarker!,
                _ => "•"
            };
        }

        // ── Нумерованные ──────────────────────────────────────────────────

        private static string BuildNumberMarker(ListProperties lp, ListMarkerType type, int number)
        {
            // Пользовательская последовательность символов: элемент N берёт символ по индексу.
            if (type == ListMarkerType.CustomSequence)
                return BuildSequenceMarker(lp, number);

            string prefix = lp.NumberPrefix ?? string.Empty;
            string suffix = lp.NumberSuffix ?? ".";
            return prefix + FormatNumber(number, type) + suffix;
        }

        private static string BuildSequenceMarker(ListProperties lp, int number)
        {
            var seq = lp.CustomSequence;
            if (seq is null || seq.Count == 0) return "•";

            int idx = number - 1;            // number начинается с StartAt (обычно 1) → индекс 0
            if (idx < 0) idx = 0;

            string sym;
            if (idx < seq.Count)
                sym = seq[idx];
            else if (lp.SequenceWrap)
                sym = seq[idx % seq.Count];  // повтор сначала
            else
                sym = seq[seq.Count - 1];    // остановка на последнем символе

            string prefix = lp.NumberPrefix ?? string.Empty;
            string suffix = lp.NumberSuffix ?? string.Empty; // для последовательности по умолчанию без разделителя
            return prefix + sym + suffix;
        }

        private static string FormatNumber(int number, ListMarkerType type)
        {
            if (number < 1) number = 1;
            return type switch
            {
                ListMarkerType.Decimal => number.ToString(),
                ListMarkerType.DecimalLeadingZero => number < 10
                    ? "0" + number.ToString() : number.ToString(),
                ListMarkerType.LowerAlpha => ToAlpha(number, upper: false),
                ListMarkerType.UpperAlpha => ToAlpha(number, upper: true),
                ListMarkerType.LowerRoman => ToRoman(number, upper: false),
                ListMarkerType.UpperRoman => ToRoman(number, upper: true),
                _ => number.ToString()
            };
        }

        // 1→a, 26→z, 27→aa, 28→ab …
        private static string ToAlpha(int number, bool upper)
        {
            var sb = new StringBuilder();
            int n = number;
            while (n > 0)
            {
                n--;
                char c = (char)('a' + n % 26);
                sb.Insert(0, c);
                n /= 26;
            }
            string s = sb.ToString();
            return upper ? s.ToUpperInvariant() : s;
        }

        private static readonly (int Value, string Symbol)[] RomanTable =
        {
            (1000, "m"), (900, "cm"), (500, "d"), (400, "cd"),
            (100, "c"), (90, "xc"), (50, "l"), (40, "xl"),
            (10, "x"), (9, "ix"), (5, "v"), (4, "iv"), (1, "i")
        };

        private static string ToRoman(int number, bool upper)
        {
            if (number <= 0 || number >= 4000) return number.ToString();
            var sb = new StringBuilder();
            int n = number;
            foreach (var (value, symbol) in RomanTable)
            {
                while (n >= value)
                {
                    sb.Append(symbol);
                    n -= value;
                }
            }
            string s = sb.ToString();
            return upper ? s.ToUpperInvariant() : s;
        }
    }
}
