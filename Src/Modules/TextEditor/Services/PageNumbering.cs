using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Writersword.Modules.TextEditor.Models.Page;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Всё, что колонтитулу нужно знать о листе: какой номер на нём печатается, виден
    /// ли номер и сами колонтитулы, какой вариант шаблона ему достался.
    /// </summary>
    public sealed class PageDecoration
    {
        /// <summary>Лист с нуля.</summary>
        public int PageIndex { get; init; }

        /// <summary>Число в счёте страниц. Скрытый номер из счёта не выпадает.</summary>
        public int Number { get; init; }

        /// <summary>Вид номера на этом листе.</summary>
        public PageNumberFormat Format { get; init; }

        /// <summary>Номер так, как он печатается: «7», «vii», «G».</summary>
        public string NumberText { get; init; } = string.Empty;

        /// <summary>Всего листов в документе.</summary>
        public int TotalPages { get; init; }

        /// <summary>Номер печатается.</summary>
        public bool NumberVisible { get; init; }

        /// <summary>Колонтитулы листа печатаются.</summary>
        public bool BandsVisible { get; init; }

        /// <summary>Какой вариант шаблона достался листу.</summary>
        public HeaderFooterVariant Variant { get; init; }

        /// <summary>Набранный руками номер. Null — номер живой.</summary>
        public string? ManualText { get; init; }

        /// <summary>На листе начинается глава.</summary>
        public bool IsChapterStart { get; init; }

        /// <summary>Текст, который встаёт на место поля номера.</summary>
        public string ShownNumber => ManualText ?? NumberText;
    }

    /// <summary>Место поля в тексте места колонтитула — для правки прямо на листе.</summary>
    public readonly record struct SlotFieldSpan(int Start, int Length, string Token)
    {
        public int End => Start + Length;
    }

    /// <summary>Текст места колонтитула с отметкой, где в нём стоят поля.</summary>
    public sealed class RenderedSlot
    {
        public string Text { get; init; } = string.Empty;
        public List<SlotFieldSpan> Fields { get; init; } = new();
    }

    /// <summary>
    /// Нумерация страниц: один проход по листам, в котором применяются шаблон и
    /// правила. Работает одинаково для полотна, печати, PDF и выгрузки в Word —
    /// отличается только то, откуда берутся число листов, метки и начала глав.
    /// </summary>
    public static class PageNumbering
    {
        /// <summary>
        /// Считает оформление всех листов.
        /// </summary>
        /// <param name="settings">Колонтитулы документа. Null — колонтитулов нет.</param>
        /// <param name="pageCount">Сколько листов.</param>
        /// <param name="anchorPages">Абзац-метка → лист, где он начинается.</param>
        /// <param name="chapterStartPages">Листы, где начинается глава.</param>
        public static PageDecoration[] Compute(
            HeaderFooterSettings? settings,
            int pageCount,
            IReadOnlyDictionary<Guid, int>? anchorPages,
            ICollection<int>? chapterStartPages)
        {
            if (pageCount <= 0) return Array.Empty<PageDecoration>();

            var result = new PageDecoration[pageCount];

            if (settings is null)
            {
                for (int p = 0; p < pageCount; p++)
                    result[p] = new PageDecoration
                    {
                        PageIndex = p,
                        Number = p + 1,
                        Format = PageNumberFormat.Arabic,
                        NumberText = (p + 1).ToString(CultureInfo.InvariantCulture),
                        TotalPages = pageCount,
                        NumberVisible = true,
                        BandsVisible = false,
                        Variant = HeaderFooterVariant.Default
                    };
                return result;
            }

            // Правила раскладываются по листам заранее: так проход идёт один раз, а
            // порядок правил внутри листа остаётся тем, в каком их заводили.
            var rangeRules = new List<PageRule>?[pageCount];
            var singleRules = new List<PageRule>?[pageCount];

            foreach (var rule in settings.Rules)
            {
                int target = TargetPage(rule, anchorPages);
                if (target < 0 || target >= pageCount) continue;

                var bucket = rule.IsRange ? rangeRules : singleRules;
                (bucket[target] ??= new List<PageRule>()).Add(rule);
            }

            bool numberHidden = false;
            bool bandsHidden = false;
            var format = settings.NumberFormat;
            int counter = settings.StartNumber;

            for (int p = 0; p < pageCount; p++)
            {
                string? manual = null;

                if (rangeRules[p] is { } ranges)
                {
                    foreach (var rule in ranges)
                    {
                        switch (rule.Action)
                        {
                            case PageRuleAction.HideNumber: numberHidden = true; break;
                            case PageRuleAction.ShowNumber: numberHidden = false; break;
                            case PageRuleAction.HideHeaderFooter: bandsHidden = true; break;
                            case PageRuleAction.ShowHeaderFooter: bandsHidden = false; break;
                            case PageRuleAction.RestartNumbering: counter = rule.StartNumber ?? 1; break;
                            case PageRuleAction.SetFormat: format = rule.Format ?? format; break;
                            case PageRuleAction.ManualNumber: manual = rule.ManualText; break;
                        }
                    }
                }

                bool pageNumberHidden = numberHidden;
                bool pageBandsHidden = bandsHidden;
                var pageFormat = format;

                bool chapter = chapterStartPages is not null && chapterStartPages.Contains(p);
                if (chapter)
                {
                    if (settings.HideNumberOnChapterStart) pageNumberHidden = true;
                    if (settings.HideHeaderFooterOnChapterStart) pageBandsHidden = true;
                }

                // Правила одного листа идут последними: они сильнее и отрезков, и
                // автоматического скрытия у начала главы. «Вернуть номер на этой
                // странице» обязано вернуть его и там, где глава его спрятала.
                if (singleRules[p] is { } singles)
                {
                    foreach (var rule in singles)
                    {
                        switch (rule.Action)
                        {
                            case PageRuleAction.HideNumber: pageNumberHidden = true; break;
                            case PageRuleAction.ShowNumber: pageNumberHidden = false; break;
                            case PageRuleAction.HideHeaderFooter: pageBandsHidden = true; break;
                            case PageRuleAction.ShowHeaderFooter: pageBandsHidden = false; break;
                            case PageRuleAction.RestartNumbering: counter = rule.StartNumber ?? 1; break;
                            case PageRuleAction.SetFormat: pageFormat = rule.Format ?? pageFormat; break;
                            case PageRuleAction.ManualNumber: manual = rule.ManualText; break;
                        }
                    }
                }

                // Номер, набранный руками, печатается всегда: его набрали, чтобы видеть.
                if (manual is not null) pageNumberHidden = false;

                int number = counter;

                var variant = HeaderFooterVariant.Default;
                if (p == 0 && settings.DifferentFirstPage)
                    variant = HeaderFooterVariant.First;
                else if (settings.DifferentOddEven && number % 2 == 0)
                    variant = HeaderFooterVariant.Even;

                result[p] = new PageDecoration
                {
                    PageIndex = p,
                    Number = number,
                    Format = pageFormat,
                    NumberText = FormatNumber(number, pageFormat),
                    TotalPages = pageCount,
                    NumberVisible = !pageNumberHidden,
                    BandsVisible = !pageBandsHidden,
                    Variant = variant,
                    ManualText = manual,
                    IsChapterStart = chapter
                };

                counter++;
            }

            return result;
        }

        /// <summary>Лист, на который указывает правило. -1 — метка потерялась.</summary>
        public static int TargetPage(PageRule rule, IReadOnlyDictionary<Guid, int>? anchorPages)
        {
            if (!rule.IsAnchored) return rule.PageIndex;
            if (rule.AnchorParagraphId is not Guid id || anchorPages is null) return -1;
            return anchorPages.TryGetValue(id, out int page) ? page : -1;
        }

        // ── Текст мест колонтитула ────────────────────────────────────────

        /// <summary>
        /// Текст места колонтитула для печати. Null — место на этом листе пустое:
        /// колонтитулы скрыты, либо в месте стоит номер, а номер на листе убран.
        ///
        /// Место с номером при скрытом номере гасится целиком, а не одной цифрой: иначе
        /// от «— 7 —» оставались бы висящие тире.
        /// </summary>
        public static string? RenderSlot(string? template, PageDecoration decoration)
        {
            if (!decoration.BandsVisible) return null;
            if (string.IsNullOrEmpty(template)) return null;

            bool hasNumber = template.Contains(HeaderFooterSettings.PageToken, StringComparison.Ordinal);
            if (hasNumber && !decoration.NumberVisible) return null;

            return template
                .Replace(HeaderFooterSettings.PageToken, decoration.ShownNumber, StringComparison.Ordinal)
                .Replace(HeaderFooterSettings.PagesToken,
                    decoration.TotalPages.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }

        /// <summary>
        /// Текст места для правки на листе: поля заменены их значениями, а где они
        /// стоят — записано. Скрытый номер даёт поле нулевой длины: набранное на его
        /// месте число узнаётся как возврат номера.
        /// </summary>
        public static RenderedSlot RenderForEditing(string? template, PageDecoration decoration)
        {
            var fields = new List<SlotFieldSpan>();
            if (string.IsNullOrEmpty(template))
                return new RenderedSlot { Text = string.Empty, Fields = fields };

            var sb = new StringBuilder(template.Length + 8);
            int i = 0;
            while (i < template.Length)
            {
                if (string.CompareOrdinal(template, i, HeaderFooterSettings.PageToken, 0,
                        HeaderFooterSettings.PageToken.Length) == 0)
                {
                    string value = decoration.NumberVisible ? decoration.ShownNumber : string.Empty;
                    fields.Add(new SlotFieldSpan(sb.Length, value.Length, HeaderFooterSettings.PageToken));
                    sb.Append(value);
                    i += HeaderFooterSettings.PageToken.Length;
                    continue;
                }

                if (string.CompareOrdinal(template, i, HeaderFooterSettings.PagesToken, 0,
                        HeaderFooterSettings.PagesToken.Length) == 0)
                {
                    string value = decoration.TotalPages.ToString(CultureInfo.InvariantCulture);
                    fields.Add(new SlotFieldSpan(sb.Length, value.Length, HeaderFooterSettings.PagesToken));
                    sb.Append(value);
                    i += HeaderFooterSettings.PagesToken.Length;
                    continue;
                }

                sb.Append(template[i]);
                i++;
            }

            return new RenderedSlot { Text = sb.ToString(), Fields = fields };
        }

        // ── Вид номера ────────────────────────────────────────────────────

        /// <summary>Число в выбранном виде. Ноль и отрицательные римскими и буквами не пишутся.</summary>
        public static string FormatNumber(int number, PageNumberFormat format)
        {
            if (number <= 0) return number.ToString(CultureInfo.InvariantCulture);

            return format switch
            {
                PageNumberFormat.RomanLower => ToRoman(number).ToLowerInvariant(),
                PageNumberFormat.RomanUpper => ToRoman(number),
                PageNumberFormat.LetterLower => ToLetters(number).ToLowerInvariant(),
                PageNumberFormat.LetterUpper => ToLetters(number),
                _ => number.ToString(CultureInfo.InvariantCulture)
            };
        }

        /// <summary>
        /// Число из набранного текста в виде, которым сейчас печатаются номера. Набранное
        /// арабскими цифрами понимается всегда. Null — это не номер.
        /// </summary>
        public static int? ParseNumber(string? text, PageNumberFormat format)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string value = text.Trim();

            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int arabic))
                return arabic;

            switch (format)
            {
                case PageNumberFormat.RomanLower:
                case PageNumberFormat.RomanUpper:
                    return FromRoman(value);
                case PageNumberFormat.LetterLower:
                case PageNumberFormat.LetterUpper:
                    return FromLetters(value);
                default:
                    return null;
            }
        }

        private static string ToRoman(int number)
        {
            if (number >= 4000) return number.ToString(CultureInfo.InvariantCulture);

            int[] values = { 1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1 };
            string[] symbols = { "M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I" };

            var sb = new StringBuilder();
            for (int i = 0; i < values.Length; i++)
            {
                while (number >= values[i])
                {
                    sb.Append(symbols[i]);
                    number -= values[i];
                }
            }
            return sb.ToString();
        }

        private static int? FromRoman(string text)
        {
            string value = text.ToUpperInvariant();
            int total = 0;
            int previous = 0;

            for (int i = value.Length - 1; i >= 0; i--)
            {
                int digit = value[i] switch
                {
                    'I' => 1,
                    'V' => 5,
                    'X' => 10,
                    'L' => 50,
                    'C' => 100,
                    'D' => 500,
                    'M' => 1000,
                    _ => 0
                };
                if (digit == 0) return null;

                if (digit < previous) total -= digit;
                else
                {
                    total += digit;
                    previous = digit;
                }
            }

            // Запись проверяется обратным переводом: «IIII» и «VX» числами не считаются.
            return total > 0 && ToRoman(total) == value ? total : null;
        }

        /// <summary>Буквенный номер как у Word: A…Z, затем AA…ZZ, затем AAA.</summary>
        private static string ToLetters(int number)
        {
            int repeat = (number - 1) / 26 + 1;
            char letter = (char)('A' + (number - 1) % 26);
            return new string(letter, repeat);
        }

        private static int? FromLetters(string text)
        {
            string value = text.ToUpperInvariant();
            if (value.Length == 0) return null;

            char first = value[0];
            if (first < 'A' || first > 'Z') return null;
            foreach (char c in value)
                if (c != first) return null;

            return (value.Length - 1) * 26 + (first - 'A') + 1;
        }
    }
}
