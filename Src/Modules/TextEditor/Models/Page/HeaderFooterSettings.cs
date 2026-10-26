using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Writersword.Modules.TextEditor.Models.Page
{
    /// <summary>Вид номера страницы.</summary>
    public enum PageNumberFormat
    {
        /// <summary>1, 2, 3.</summary>
        Arabic = 0,

        /// <summary>i, ii, iii.</summary>
        RomanLower = 1,

        /// <summary>I, II, III.</summary>
        RomanUpper = 2,

        /// <summary>a, b, c … z, aa, bb.</summary>
        LetterLower = 3,

        /// <summary>A, B, C … Z, AA, BB.</summary>
        LetterUpper = 4
    }

    /// <summary>
    /// Где действует правило страницы.
    ///
    /// Правило привязывается либо к листу, либо к абзацу. Лист — это «страница 7»,
    /// сколько бы текста выше ни прибавилось. Абзац — метка в тексте: правило
    /// срабатывает на странице, куда этот абзац попал, и едет вместе с ним.
    /// </summary>
    public enum PageRuleScope
    {
        /// <summary>Только лист с номером <see cref="PageRule.PageIndex"/>.</summary>
        Page = 0,

        /// <summary>С листа <see cref="PageRule.PageIndex"/> и до следующего правила того же рода.</summary>
        FromPage = 1,

        /// <summary>Только лист, на котором начинается абзац-метка.</summary>
        Anchor = 2,

        /// <summary>С листа, где начинается абзац-метка, и дальше.</summary>
        FromAnchor = 3
    }

    /// <summary>Что правило делает с листом.</summary>
    public enum PageRuleAction
    {
        /// <summary>Номер не печатается. В счёте лист остаётся.</summary>
        HideNumber = 0,

        /// <summary>Номер снова печатается — отменяет «скрыть» от более раннего правила.</summary>
        ShowNumber = 1,

        /// <summary>Колонтитулы листа не печатаются целиком. В счёте лист остаётся.</summary>
        HideHeaderFooter = 2,

        /// <summary>Колонтитулы снова печатаются.</summary>
        ShowHeaderFooter = 3,

        /// <summary>Счёт с этого листа начинается заново с <see cref="PageRule.StartNumber"/>.</summary>
        RestartNumbering = 4,

        /// <summary>На листе вместо номера стоит набранный руками текст. Счёт не меняется.</summary>
        ManualNumber = 5,

        /// <summary>С этого листа номер печатается другим видом (<see cref="PageRule.Format"/>).</summary>
        SetFormat = 6,

        /// <summary>
        /// С этого листа (или на одном листе) колонтитулы печатаются своим текстом
        /// (<see cref="PageRule.Texts"/>), а не общим шаблоном. Так приходят разделы Word
        /// со своими колонтитулами: глава с бегущим заголовком, приложение со своей
        /// шапкой. Номер, его вид и счёт по-прежнему решают остальные правила.
        /// </summary>
        SetText = 7
    }

    /// <summary>
    /// Свой текст колонтитулов для правила <see cref="PageRuleAction.SetText"/>: обычные
    /// полосы и полосы чётных листов (при <see cref="HeaderFooterSettings.DifferentOddEven"/>).
    /// </summary>
    public sealed class HeaderFooterTexts
    {
        public HeaderFooterBand Header { get; set; } = new();
        public HeaderFooterBand Footer { get; set; } = new();
        public HeaderFooterBand EvenHeader { get; set; } = new();
        public HeaderFooterBand EvenFooter { get; set; } = new();

        /// <summary>Во всех полосах пусто.</summary>
        [JsonIgnore]
        public bool IsEmpty => Header.IsEmpty && Footer.IsEmpty && EvenHeader.IsEmpty && EvenFooter.IsEmpty;

        public HeaderFooterTexts Clone() => new()
        {
            Header = Header.Clone(),
            Footer = Footer.Clone(),
            EvenHeader = EvenHeader.Clone(),
            EvenFooter = EvenFooter.Clone()
        };

        /// <summary>Тот же текст во всех полосах.</summary>
        public static bool Same(HeaderFooterTexts? a, HeaderFooterTexts? b)
        {
            if (a is null || b is null) return a is null && b is null;
            return HeaderFooterBand.Same(a.Header, b.Header)
                && HeaderFooterBand.Same(a.Footer, b.Footer)
                && HeaderFooterBand.Same(a.EvenHeader, b.EvenHeader)
                && HeaderFooterBand.Same(a.EvenFooter, b.EvenFooter);
        }
    }

    /// <summary>
    /// Исключение из общего вида колонтитулов для одного листа или отрезка листов.
    ///
    /// Word для того же требует разрыва раздела, отвязки колонтитула от предыдущего и
    /// отдельного окна «формат номера». Здесь исключение — просто запись в списке: оно
    /// не трогает ни текст, ни шаблон колонтитула и снимается одним щелчком.
    /// </summary>
    public sealed class PageRule
    {
        /// <summary>Опознаватель правила — по нему правило снимают из списка.</summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>Где действует.</summary>
        public PageRuleScope Scope { get; set; }

        /// <summary>Что делает.</summary>
        public PageRuleAction Action { get; set; }

        /// <summary>Лист с нуля — для <see cref="PageRuleScope.Page"/> и <see cref="PageRuleScope.FromPage"/>.</summary>
        public int PageIndex { get; set; }

        /// <summary>Абзац-метка — для <see cref="PageRuleScope.Anchor"/> и <see cref="PageRuleScope.FromAnchor"/>.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Guid? AnchorParagraphId { get; set; }

        /// <summary>С какого числа начать счёт — для <see cref="PageRuleAction.RestartNumbering"/>.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? StartNumber { get; set; }

        /// <summary>Набранный руками номер — для <see cref="PageRuleAction.ManualNumber"/>.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ManualText { get; set; }

        /// <summary>Вид номера — для <see cref="PageRuleAction.SetFormat"/>.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public PageNumberFormat? Format { get; set; }

        /// <summary>Свой текст колонтитулов — для <see cref="PageRuleAction.SetText"/>.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public HeaderFooterTexts? Texts { get; set; }

        /// <summary>Правило действует и на листы после своего.</summary>
        [JsonIgnore]
        public bool IsRange => Scope == PageRuleScope.FromPage || Scope == PageRuleScope.FromAnchor;

        /// <summary>Правило привязано к абзацу, а не к листу.</summary>
        [JsonIgnore]
        public bool IsAnchored => Scope == PageRuleScope.Anchor || Scope == PageRuleScope.FromAnchor;

        public PageRule Clone()
        {
            var copy = (PageRule)MemberwiseClone();
            copy.Texts = Texts?.Clone();
            return copy;
        }
    }

    /// <summary>
    /// Оформление текста колонтитула поверх того, что под ним: у полосы — поверх
    /// шрифта колонтитулов документа, у куска места — поверх оформления полосы.
    /// Null в поле — как у того, что под ним.
    ///
    /// Так приходят колонтитулы Word, где у верхнего и нижнего свои кегль и цвет, а в
    /// одном месте жирное название стоит рядом с обычным «стр. 7».
    /// </summary>
    public sealed class HeaderFooterStyle
    {
        /// <summary>Гарнитура.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? FontFamily { get; set; }

        /// <summary>Кегль в пунктах.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? FontSizePt { get; set; }

        /// <summary>Жирный.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IsBold { get; set; }

        /// <summary>Курсив.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IsItalic { get; set; }

        /// <summary>Цвет HEX.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? TextColor { get; set; }

        /// <summary>Ни одно поле не задано.</summary>
        [JsonIgnore]
        public bool IsEmpty
            => string.IsNullOrWhiteSpace(FontFamily)
               && FontSizePt is null
               && IsBold is null
               && IsItalic is null
               && string.IsNullOrWhiteSpace(TextColor);

        public HeaderFooterStyle Clone() => (HeaderFooterStyle)MemberwiseClone();

        /// <summary>Снимает выбранные поля.</summary>
        public void Clear(bool family, bool size, bool bold, bool italic, bool color)
        {
            if (family) FontFamily = null;
            if (size) FontSizePt = null;
            if (bold) IsBold = null;
            if (italic) IsItalic = null;
            if (color) TextColor = null;
        }

        /// <summary>
        /// Поля, которыми <paramref name="font"/> отличается от <paramref name="under"/>.
        /// Null — ничем.
        /// </summary>
        public static HeaderFooterStyle? Difference(HeaderFooterFont font, HeaderFooterFont under)
        {
            var style = new HeaderFooterStyle
            {
                FontFamily = string.Equals(font.FontFamily, under.FontFamily, StringComparison.Ordinal) ? null : font.FontFamily,
                FontSizePt = font.FontSizePt > 0 && Math.Abs(font.FontSizePt - under.FontSizePt) > 0.001 ? font.FontSizePt : null,
                IsBold = font.IsBold == under.IsBold ? null : font.IsBold,
                IsItalic = font.IsItalic == under.IsItalic ? null : font.IsItalic,
                TextColor = string.Equals(font.TextColor, under.TextColor, StringComparison.OrdinalIgnoreCase) ? null : font.TextColor
            };

            return style.IsEmpty ? null : style;
        }

        /// <summary>Одинаковое оформление. Null равен пустому.</summary>
        public static bool Same(HeaderFooterStyle? a, HeaderFooterStyle? b)
        {
            bool aEmpty = a is null || a.IsEmpty;
            bool bEmpty = b is null || b.IsEmpty;
            if (aEmpty || bEmpty) return aEmpty && bEmpty;

            return string.Equals(a!.FontFamily, b!.FontFamily, StringComparison.Ordinal)
                && Nullable.Equals(a.FontSizePt, b.FontSizePt)
                && a.IsBold == b.IsBold
                && a.IsItalic == b.IsItalic
                && string.Equals(a.TextColor, b.TextColor, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Оформление куска колонтитула со всеми полями: шрифт документа, поверх него
    /// оформление полосы, поверх — куска.
    /// </summary>
    public readonly record struct HeaderFooterFont(
        string? FontFamily,
        double FontSizePt,
        bool IsBold,
        bool IsItalic,
        string? TextColor)
    {
        /// <summary>Это оформление с полями <paramref name="style"/> поверх.</summary>
        public HeaderFooterFont With(HeaderFooterStyle? style)
        {
            if (style is null) return this;

            return new HeaderFooterFont(
                string.IsNullOrWhiteSpace(style.FontFamily) ? FontFamily : style.FontFamily,
                style.FontSizePt is double size && size > 0 ? size : FontSizePt,
                style.IsBold ?? IsBold,
                style.IsItalic ?? IsItalic,
                string.IsNullOrWhiteSpace(style.TextColor) ? TextColor : style.TextColor);
        }
    }

    /// <summary>
    /// Кусок места колонтитула со своим оформлением. Куски одного места по порядку
    /// складываются в его текст; не сложились (текст места правили мимо полосы) — место
    /// печатается оформлением полосы.
    /// </summary>
    public sealed class HeaderFooterRun
    {
        /// <summary>Место: 0 — лево, 1 — центр, 2 — право.</summary>
        public int Slot { get; set; }

        /// <summary>Текст куска, с полями номера, как в тексте места.</summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>Оформление поверх оформления полосы. Null — как у полосы.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public HeaderFooterStyle? Style { get; set; }

        public HeaderFooterRun Clone() => new()
        {
            Slot = Slot,
            Text = Text,
            Style = Style?.Clone()
        };
    }

    /// <summary>
    /// Одна полоса колонтитула — верхняя или нижняя — в трёх местах: у левого поля,
    /// по центру и у правого поля. В тексте живут поля номера:
    /// <see cref="HeaderFooterSettings.PageToken"/> и <see cref="HeaderFooterSettings.PagesToken"/>.
    /// Перевод строки внутри места — несколько строк колонтитула.
    /// </summary>
    public sealed class HeaderFooterBand
    {
        /// <summary>Текст у левого поля.</summary>
        public string Left { get; set; } = string.Empty;

        /// <summary>Текст по центру.</summary>
        public string Center { get; set; } = string.Empty;

        /// <summary>Текст у правого поля.</summary>
        public string Right { get; set; } = string.Empty;

        /// <summary>
        /// Оформление полосы поверх шрифта колонтитулов документа. Null — как у
        /// колонтитулов документа.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public HeaderFooterStyle? Style { get; set; }

        /// <summary>
        /// Куски мест со своим оформлением поверх оформления полосы. Место без кусков
        /// печатается оформлением полосы целиком.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<HeaderFooterRun>? Runs { get; set; }

        /// <summary>Во всех трёх местах пусто.</summary>
        [JsonIgnore]
        public bool IsEmpty
            => string.IsNullOrEmpty(Left) && string.IsNullOrEmpty(Center) && string.IsNullOrEmpty(Right);

        /// <summary>Текст места: 0 — лево, 1 — центр, 2 — право.</summary>
        public string GetSlot(int slot) => slot switch
        {
            0 => Left ?? string.Empty,
            1 => Center ?? string.Empty,
            _ => Right ?? string.Empty
        };

        /// <summary>
        /// Меняет текст места: 0 — лево, 1 — центр, 2 — право. Оформление кусков едет
        /// вместе с текстом: правка посередине берёт оформление знака перед собой.
        /// </summary>
        public void SetSlot(int slot, string text)
        {
            text ??= string.Empty;

            string current = GetSlot(slot);
            if (!string.Equals(current, text, StringComparison.Ordinal))
                RebaseRuns(slot, current, text);

            switch (slot)
            {
                case 0: Left = text; break;
                case 1: Center = text; break;
                default: Right = text; break;
            }
        }

        /// <summary>В каком-то месте стоит поле номера страницы.</summary>
        [JsonIgnore]
        public bool HasPageNumber
            => GetSlot(0).Contains(HeaderFooterSettings.PageToken, StringComparison.Ordinal)
               || GetSlot(1).Contains(HeaderFooterSettings.PageToken, StringComparison.Ordinal)
               || GetSlot(2).Contains(HeaderFooterSettings.PageToken, StringComparison.Ordinal);

        /// <summary>Оформление полосы поверх шрифта колонтитулов документа.</summary>
        public HeaderFooterFont FontOf(HeaderFooterFont documentFont) => documentFont.With(Style);

        /// <summary>
        /// Текст места кусками одного оформления, по порядку. Пустое место — ни одного
        /// куска. Соседние куски с одинаковым оформлением склеены.
        /// </summary>
        public List<(string Text, HeaderFooterFont Font)> SlotPieces(int slot, HeaderFooterFont documentFont)
        {
            var result = new List<(string Text, HeaderFooterFont Font)>();

            string text = GetSlot(slot);
            if (text.Length == 0) return result;

            var font = FontOf(documentFont);
            var runs = SlotRuns(slot);
            if (runs is null)
            {
                result.Add((text, font));
                return result;
            }

            foreach (var run in runs)
            {
                string piece = run.Text ?? string.Empty;
                if (piece.Length == 0) continue;

                var pieceFont = font.With(run.Style);
                if (result.Count > 0 && result[^1].Font == pieceFont)
                    result[^1] = (result[^1].Text + piece, pieceFont);
                else
                    result.Add((piece, pieceFont));
            }

            return result;
        }

        /// <summary>
        /// Куски места, если они складываются в его текст. Null — кусков нет или они
        /// от другого текста.
        /// </summary>
        private List<HeaderFooterRun>? SlotRuns(int slot)
        {
            if (Runs is not { Count: > 0 }) return null;

            string text = GetSlot(slot);
            List<HeaderFooterRun>? list = null;
            int length = 0;

            foreach (var run in Runs)
            {
                if (run is null || run.Slot != slot) continue;
                (list ??= new List<HeaderFooterRun>()).Add(run);
                length += run.Text?.Length ?? 0;
            }

            if (list is null || length != text.Length) return null;

            int at = 0;
            foreach (var run in list)
            {
                string piece = run.Text ?? string.Empty;
                if (string.CompareOrdinal(text, at, piece, 0, piece.Length) != 0) return null;
                at += piece.Length;
            }

            return list;
        }

        /// <summary>
        /// Переносит оформление кусков места на новый текст: общее начало и общий конец
        /// оставляют своё оформление, вставленное берёт оформление знака перед собой.
        /// </summary>
        private void RebaseRuns(int slot, string oldText, string newText)
        {
            if (Runs is not { Count: > 0 }) return;

            var runs = SlotRuns(slot);
            Runs.RemoveAll(r => r is null || r.Slot == slot);

            if (runs is not null && oldText.Length > 0 && newText.Length > 0)
            {
                var styles = new HeaderFooterStyle?[oldText.Length];
                int at = 0;
                foreach (var run in runs)
                {
                    string piece = run.Text ?? string.Empty;
                    for (int k = 0; k < piece.Length && at < styles.Length; k++)
                        styles[at++] = run.Style;
                }

                int common = Math.Min(oldText.Length, newText.Length);
                int prefix = 0;
                while (prefix < common && oldText[prefix] == newText[prefix]) prefix++;

                int suffix = 0;
                while (suffix < common - prefix
                       && oldText[oldText.Length - 1 - suffix] == newText[newText.Length - 1 - suffix])
                    suffix++;

                var inserted = prefix > 0 ? styles[prefix - 1] : styles[Math.Min(prefix, styles.Length - 1)];

                var rebuilt = new List<HeaderFooterRun>();
                var sb = new System.Text.StringBuilder();
                HeaderFooterStyle? currentStyle = null;
                bool open = false;

                void Put(char ch, HeaderFooterStyle? style)
                {
                    if (open && HeaderFooterStyle.Same(currentStyle, style))
                    {
                        sb.Append(ch);
                        return;
                    }

                    if (open)
                        rebuilt.Add(new HeaderFooterRun { Slot = slot, Text = sb.ToString(), Style = currentStyle?.Clone() });

                    sb.Clear();
                    sb.Append(ch);
                    currentStyle = style;
                    open = true;
                }

                for (int i = 0; i < prefix; i++)
                    Put(newText[i], styles[i]);

                for (int i = prefix; i < newText.Length - suffix; i++)
                    Put(newText[i], inserted);

                for (int k = 0; k < suffix; k++)
                    Put(newText[newText.Length - suffix + k], styles[oldText.Length - suffix + k]);

                if (open)
                    rebuilt.Add(new HeaderFooterRun { Slot = slot, Text = sb.ToString(), Style = currentStyle?.Clone() });

                // Один кусок без своего оформления — то же, что место без кусков.
                if (!(rebuilt.Count == 1 && (rebuilt[0].Style is null || rebuilt[0].Style!.IsEmpty)))
                    Runs.AddRange(rebuilt);
            }

            if (Runs.Count == 0) Runs = null;
        }

        /// <summary>
        /// Снимает выбранные поля своего оформления полосы и её кусков: их снова
        /// решает шрифт колонтитулов документа.
        /// </summary>
        public void ClearOwnFormatting(bool family, bool size, bool bold, bool italic, bool color)
        {
            if (Style is not null)
            {
                Style.Clear(family, size, bold, italic, color);
                if (Style.IsEmpty) Style = null;
            }

            if (Runs is null) return;

            foreach (var run in Runs)
            {
                if (run?.Style is null) continue;
                run.Style.Clear(family, size, bold, italic, color);
                if (run.Style.IsEmpty) run.Style = null;
            }

            if (Runs.TrueForAll(r => r is null || r.Style is null))
                Runs = null;
        }

        public HeaderFooterBand Clone()
        {
            List<HeaderFooterRun>? runs = null;
            if (Runs is not null)
            {
                runs = new List<HeaderFooterRun>(Runs.Count);
                foreach (var run in Runs)
                    if (run is not null) runs.Add(run.Clone());
            }

            return new HeaderFooterBand
            {
                Left = Left,
                Center = Center,
                Right = Right,
                Style = Style?.Clone(),
                Runs = runs
            };
        }

        /// <summary>
        /// Полосы с одинаковым текстом во всех трёх местах и одинаковым оформлением
        /// каждого куска.
        /// </summary>
        public static bool Same(HeaderFooterBand? a, HeaderFooterBand? b)
        {
            bool aEmpty = a is null || a.IsEmpty;
            bool bEmpty = b is null || b.IsEmpty;
            if (aEmpty && bEmpty) return true;
            if (aEmpty != bEmpty) return false;

            if (!string.Equals(a!.GetSlot(0), b!.GetSlot(0), StringComparison.Ordinal)
                || !string.Equals(a.GetSlot(1), b.GetSlot(1), StringComparison.Ordinal)
                || !string.Equals(a.GetSlot(2), b.GetSlot(2), StringComparison.Ordinal))
                return false;

            // Оформление сравнивается кусками поверх пустого шрифта: полоса с жирным
            // оформлением и полоса, где жирный весь единственный кусок, — одно и то же.
            var neutral = default(HeaderFooterFont);
            for (int slot = 0; slot < 3; slot++)
            {
                var left = a.SlotPieces(slot, neutral);
                var right = b.SlotPieces(slot, neutral);
                if (left.Count != right.Count) return false;

                for (int i = 0; i < left.Count; i++)
                {
                    if (!string.Equals(left[i].Text, right[i].Text, StringComparison.Ordinal)) return false;
                    if (left[i].Font != right[i].Font) return false;
                }
            }

            // Пустое место не печатается: оформление полосы, которое не досталось ни
            // одному куску, ничего не меняет.
            return true;
        }
    }

    /// <summary>Куда поставить номер страницы кнопкой «Номер страницы».</summary>
    public enum PageNumberPosition
    {
        /// <summary>Вверху слева.</summary>
        TopLeft = 0,

        /// <summary>Вверху по центру.</summary>
        TopCenter = 1,

        /// <summary>Вверху справа.</summary>
        TopRight = 2,

        /// <summary>Вверху у внешнего края: на нечётных справа, на чётных слева.</summary>
        TopOutside = 3,

        /// <summary>Внизу слева.</summary>
        BottomLeft = 4,

        /// <summary>Внизу по центру.</summary>
        BottomCenter = 5,

        /// <summary>Внизу справа.</summary>
        BottomRight = 6,

        /// <summary>Внизу у внешнего края: на нечётных справа, на чётных слева.</summary>
        BottomOutside = 7,

        /// <summary>Вверху у внутреннего края (у корешка): на нечётных слева, на чётных справа.</summary>
        TopInside = 8,

        /// <summary>Внизу у внутреннего края (у корешка): на нечётных слева, на чётных справа.</summary>
        BottomInside = 9
    }

    /// <summary>Какой вариант колонтитула достался листу.</summary>
    public enum HeaderFooterVariant
    {
        /// <summary>Обычный.</summary>
        Default = 0,

        /// <summary>Первой страницы документа.</summary>
        First = 1,

        /// <summary>Чётных страниц.</summary>
        Even = 2
    }

    /// <summary>
    /// Колонтитулы и нумерация страниц документа.
    ///
    /// Устроены иначе, чем в Word, и намеренно. Там номер — поле внутри колонтитула,
    /// колонтитул один на раздел, и любое исключение для одного листа требует разрыва
    /// раздела с отвязкой от предыдущего. Здесь шаблон колонтитула один на документ
    /// (с вариантами для первой и чётных страниц), а исключения — отдельный список
    /// правил: «на этой странице номер не нужен», «дальше не надо», «отсюда счёт с
    /// десяти». Правило не трогает ни текст, ни шаблон и снимается одним щелчком.
    ///
    /// Скрытый номер из счёта лист не выбрасывает: у титула номера нет, но он первый.
    /// </summary>
    public sealed class HeaderFooterSettings
    {
        /// <summary>Поле «номер этой страницы».</summary>
        public const string PageToken = "{PAGE}";

        /// <summary>Поле «всего страниц».</summary>
        public const string PagesToken = "{PAGES}";

        /// <summary>Верхний колонтитул — обычный.</summary>
        public HeaderFooterBand Header { get; set; } = new();

        /// <summary>Нижний колонтитул — обычный.</summary>
        public HeaderFooterBand Footer { get; set; } = new();

        /// <summary>Верхний колонтитул первой страницы (при <see cref="DifferentFirstPage"/>).</summary>
        public HeaderFooterBand FirstHeader { get; set; } = new();

        /// <summary>Нижний колонтитул первой страницы (при <see cref="DifferentFirstPage"/>).</summary>
        public HeaderFooterBand FirstFooter { get; set; } = new();

        /// <summary>Верхний колонтитул чётных страниц (при <see cref="DifferentOddEven"/>).</summary>
        public HeaderFooterBand EvenHeader { get; set; } = new();

        /// <summary>Нижний колонтитул чётных страниц (при <see cref="DifferentOddEven"/>).</summary>
        public HeaderFooterBand EvenFooter { get; set; } = new();

        /// <summary>
        /// У первой страницы документа свои колонтитулы — обычно пустые: титул.
        /// В счёте она остаётся первой.
        /// </summary>
        public bool DifferentFirstPage { get; set; }

        /// <summary>
        /// У чётных страниц свои колонтитулы — для разворота книги, где номер стоит у
        /// внешнего края. Чётность берётся по номеру, который печатается, как в Word.
        /// </summary>
        public bool DifferentOddEven { get; set; }

        /// <summary>Вид номера по умолчанию.</summary>
        public PageNumberFormat NumberFormat { get; set; } = PageNumberFormat.Arabic;

        /// <summary>Номер первой страницы документа.</summary>
        public int StartNumber { get; set; } = 1;

        /// <summary>
        /// На листах, где начинается глава (заголовок первого уровня), номер не
        /// печатается. Так верстают книги: у начала главы номер либо внизу, либо его нет.
        /// </summary>
        public bool HideNumberOnChapterStart { get; set; }

        /// <summary>На листах, где начинается глава, колонтитулы не печатаются целиком.</summary>
        public bool HideHeaderFooterOnChapterStart { get; set; }

        /// <summary>Гарнитура колонтитулов. Null — как у стиля «Обычный».</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? FontFamily { get; set; }

        /// <summary>Кегль колонтитулов в пунктах.</summary>
        public double FontSizePt { get; set; } = 10;

        /// <summary>Жирный текст колонтитулов.</summary>
        public bool IsBold { get; set; }

        /// <summary>Курсивный текст колонтитулов.</summary>
        public bool IsItalic { get; set; }

        /// <summary>Цвет текста колонтитулов HEX. Null — цвет текста листа.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? TextColor { get; set; }

        /// <summary>
        /// Где стоит середина среднего места, мм от левого края текста. Null — посередине
        /// текста. Как позиция табуляции «по центру» в колонтитуле Word: её двигают на
        /// линейке, пока пишут в колонтитуле.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? CenterTabMm { get; set; }

        /// <summary>
        /// Где кончается правое место, мм от левого края текста. Null — у правого края
        /// текста. Как позиция табуляции «по правому краю» в колонтитуле Word.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? RightTabMm { get; set; }

        /// <summary>Исключения для отдельных листов и отрезков.</summary>
        public List<PageRule> Rules { get; set; } = new();

        /// <summary>Ни в одном варианте колонтитула и ни в одном правиле со своим текстом нет текста.</summary>
        [JsonIgnore]
        public bool IsEmpty
            => Header.IsEmpty && Footer.IsEmpty
               && (!DifferentFirstPage || (FirstHeader.IsEmpty && FirstFooter.IsEmpty))
               && (!DifferentOddEven || (EvenHeader.IsEmpty && EvenFooter.IsEmpty))
               && !Rules.Exists(r => r.Action == PageRuleAction.SetText && r.Texts is { IsEmpty: false });

        /// <summary>Шрифт колонтитулов документа: под ним оформление полос и их кусков.</summary>
        [JsonIgnore]
        public HeaderFooterFont BaseFont => new(FontFamily, FontSizePt, IsBold, IsItalic, TextColor);

        /// <summary>Все полосы: варианты шаблона и свой текст правил.</summary>
        public IEnumerable<HeaderFooterBand> AllBands()
        {
            yield return Header;
            yield return Footer;
            yield return FirstHeader;
            yield return FirstFooter;
            yield return EvenHeader;
            yield return EvenFooter;

            foreach (var rule in Rules)
            {
                if (rule?.Texts is not { } texts) continue;
                yield return texts.Header;
                yield return texts.Footer;
                yield return texts.EvenHeader;
                yield return texts.EvenFooter;
            }
        }

        /// <summary>
        /// Снимает выбранные поля своего оформления у всех полос и их кусков: шрифт,
        /// выбранный для колонтитулов документа, действует во всех.
        /// </summary>
        public void ClearOwnFormatting(bool family, bool size, bool bold, bool italic, bool color)
        {
            foreach (var band in AllBands())
                band?.ClearOwnFormatting(family, size, bold, italic, color);
        }

        /// <summary>Полоса варианта: верхняя или нижняя.</summary>
        public HeaderFooterBand GetBand(HeaderFooterVariant variant, bool header) => variant switch
        {
            HeaderFooterVariant.First => header ? FirstHeader : FirstFooter,
            HeaderFooterVariant.Even => header ? EvenHeader : EvenFooter,
            _ => header ? Header : Footer
        };

        /// <summary>Глубокая копия — для шага отмены и снимка документа.</summary>
        public HeaderFooterSettings Clone()
        {
            var copy = (HeaderFooterSettings)MemberwiseClone();
            copy.Header = Header.Clone();
            copy.Footer = Footer.Clone();
            copy.FirstHeader = FirstHeader.Clone();
            copy.FirstFooter = FirstFooter.Clone();
            copy.EvenHeader = EvenHeader.Clone();
            copy.EvenFooter = EvenFooter.Clone();
            copy.Rules = new List<PageRule>(Rules.Count);
            foreach (var rule in Rules)
                copy.Rules.Add(rule.Clone());
            return copy;
        }
    }
}
