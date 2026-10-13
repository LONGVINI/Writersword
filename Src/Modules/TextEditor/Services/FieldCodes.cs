using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>Вид живого поля.</summary>
    public enum LiveFieldKind
    {
        /// <summary>Поле не живое: значение остаётся текстом.</summary>
        None = 0,

        /// <summary>DATE — сегодняшняя дата.</summary>
        Date = 1,

        /// <summary>TIME — текущее время.</summary>
        Time = 2,

        /// <summary>PAGE — номер листа, на котором стоит поле.</summary>
        Page = 3,

        /// <summary>NUMPAGES, SECTIONPAGES — число листов документа.</summary>
        NumPages = 4
    }

    /// <summary>
    /// Коды полей Word: какое поле живое и как выглядит его значение. Дата и время —
    /// по шаблону ключа \@ (буквы те же, что у Word: d, dd, ddd, dddd, M…MMMM, yy,
    /// yyyy, h, hh, H, HH, m, mm, s, ss, AM/PM, текст в апострофах); номера — по ключу
    /// \* (ARABIC, ROMAN, roman, ALPHABETIC, alphabetic).
    /// </summary>
    public static class FieldCodes
    {
        private static readonly Regex DatePicture =
            new(@"\\@\s*(?:""(?<p>[^""]*)""|(?<p>\S+))", RegexOptions.CultureInvariant);

        private static readonly Regex NumberFormat =
            new(@"\\\*\s*(?<f>\w+)", RegexOptions.CultureInvariant);

        /// <summary>Вид живого поля по его коду.</summary>
        public static LiveFieldKind KindOf(string? instruction)
        {
            string name = FieldName(instruction);
            return name switch
            {
                "DATE" => LiveFieldKind.Date,
                "TIME" => LiveFieldKind.Time,
                "PAGE" => LiveFieldKind.Page,
                "NUMPAGES" => LiveFieldKind.NumPages,
                "SECTIONPAGES" => LiveFieldKind.NumPages,
                _ => LiveFieldKind.None
            };
        }

        /// <summary>Имя поля — первое слово кода, заглавными.</summary>
        public static string FieldName(string? instruction)
        {
            if (string.IsNullOrWhiteSpace(instruction)) return string.Empty;

            var trimmed = instruction.TrimStart();
            int end = 0;
            while (end < trimmed.Length && !char.IsWhiteSpace(trimmed[end]) && trimmed[end] != '\\') end++;
            return trimmed.Substring(0, end).ToUpperInvariant();
        }

        /// <summary>
        /// Дата или время по коду поля. Язык — языка текста, в котором стоит поле:
        /// «27 сентября 2026» у русского текста, «September 27, 2026» у английского.
        /// </summary>
        public static string FormatDateTime(string instruction, DateTime moment, string? language)
        {
            var culture = CultureFor(language);
            var kind = KindOf(instruction);

            var match = DatePicture.Match(instruction ?? string.Empty);
            if (!match.Success)
            {
                return kind == LiveFieldKind.Time
                    ? moment.ToString(culture.DateTimeFormat.ShortTimePattern, culture)
                    : moment.ToString(culture.DateTimeFormat.ShortDatePattern, culture);
            }

            return moment.ToString(ToNetPattern(match.Groups["p"].Value), culture);
        }

        /// <summary>Номер по коду поля: арабскими, римскими или буквами.</summary>
        public static string FormatNumber(string instruction, int number)
        {
            var match = NumberFormat.Match(instruction ?? string.Empty);
            string format = match.Success ? match.Groups["f"].Value : "ARABIC";

            return format switch
            {
                "ROMAN" => NoteNumbering.Format(number, Models.Document.NoteNumberFormat.UpperRoman),
                "roman" => NoteNumbering.Format(number, Models.Document.NoteNumberFormat.LowerRoman),
                "ALPHABETIC" => NoteNumbering.Format(number, Models.Document.NoteNumberFormat.UpperLetter),
                "alphabetic" => NoteNumbering.Format(number, Models.Document.NoteNumberFormat.LowerLetter),
                _ => number.ToString(CultureInfo.InvariantCulture)
            };
        }

        private static CultureInfo CultureFor(string? language)
        {
            if (!string.IsNullOrWhiteSpace(language))
            {
                try { return CultureInfo.GetCultureInfo(language!); }
                catch (CultureNotFoundException) { }
            }

            return CultureInfo.CurrentCulture;
        }

        /// <summary>
        /// Шаблон даты Word в шаблон .NET. Буквы шаблона у них почти общие; отличия —
        /// AM/PM (у .NET tt), текст в апострофах (у .NET тоже апострофы, но служебные
        /// буквы внутри другие) и знаки, которые .NET считает служебными, а Word нет.
        /// </summary>
        private static string ToNetPattern(string word)
        {
            var builder = new StringBuilder(word.Length + 8);

            for (int i = 0; i < word.Length; i++)
            {
                char c = word[i];

                if (c == '\'')
                {
                    int close = word.IndexOf('\'', i + 1);
                    string literal = close < 0 ? word.Substring(i + 1) : word.Substring(i + 1, close - i - 1);
                    builder.Append('\'').Append(literal.Replace("\\", "\\\\")).Append('\'');
                    i = close < 0 ? word.Length : close;
                    continue;
                }

                if (string.Compare(word, i, "AM/PM", 0, 5, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    builder.Append("tt");
                    i += 4;
                    continue;
                }

                if (string.Compare(word, i, "am/pm", 0, 5, StringComparison.Ordinal) == 0)
                {
                    builder.Append("tt");
                    i += 4;
                    continue;
                }

                switch (c)
                {
                    case 'd': case 'M': case 'y': case 'h': case 'H': case 'm': case 's':
                        builder.Append(c);
                        break;

                    // Служебные у .NET знаки, которые у Word — обычный текст.
                    case '%': case '"': case '\\': case 'f': case 'F': case 'g': case 'K': case 'z': case 't':
                    case ':': case '/':
                        builder.Append('\\').Append(c);
                        break;

                    default:
                        if (char.IsLetter(c)) builder.Append('\\').Append(c);
                        else builder.Append(c);
                        break;
                }
            }

            return builder.ToString();
        }
    }
}
