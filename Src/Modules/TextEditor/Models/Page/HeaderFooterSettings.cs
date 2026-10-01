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
        SetFormat = 6
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

        /// <summary>Правило действует и на листы после своего.</summary>
        [JsonIgnore]
        public bool IsRange => Scope == PageRuleScope.FromPage || Scope == PageRuleScope.FromAnchor;

        /// <summary>Правило привязано к абзацу, а не к листу.</summary>
        [JsonIgnore]
        public bool IsAnchored => Scope == PageRuleScope.Anchor || Scope == PageRuleScope.FromAnchor;

        public PageRule Clone() => (PageRule)MemberwiseClone();
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

        /// <summary>Во всех трёх местах пусто.</summary>
        [JsonIgnore]
        public bool IsEmpty
            => string.IsNullOrEmpty(Left) && string.IsNullOrEmpty(Center) && string.IsNullOrEmpty(Right);

        /// <summary>Текст места: 0 — лево, 1 — центр, 2 — право.</summary>
        public string GetSlot(int slot) => slot switch
        {
            0 => Left,
            1 => Center,
            _ => Right
        };

        /// <summary>Меняет текст места: 0 — лево, 1 — центр, 2 — право.</summary>
        public void SetSlot(int slot, string text)
        {
            text ??= string.Empty;
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
            => Left.Contains(HeaderFooterSettings.PageToken, StringComparison.Ordinal)
               || Center.Contains(HeaderFooterSettings.PageToken, StringComparison.Ordinal)
               || Right.Contains(HeaderFooterSettings.PageToken, StringComparison.Ordinal);

        public HeaderFooterBand Clone() => new()
        {
            Left = Left,
            Center = Center,
            Right = Right
        };

        /// <summary>Полосы с одинаковым текстом во всех трёх местах.</summary>
        public static bool Same(HeaderFooterBand? a, HeaderFooterBand? b)
        {
            bool aEmpty = a is null || a.IsEmpty;
            bool bEmpty = b is null || b.IsEmpty;
            if (aEmpty && bEmpty) return true;
            if (aEmpty != bEmpty) return false;

            return string.Equals(a!.Left, b!.Left, StringComparison.Ordinal)
                && string.Equals(a.Center, b.Center, StringComparison.Ordinal)
                && string.Equals(a.Right, b.Right, StringComparison.Ordinal);
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

        /// <summary>Ни в одном варианте колонтитула нет текста.</summary>
        [JsonIgnore]
        public bool IsEmpty
            => Header.IsEmpty && Footer.IsEmpty
               && (!DifferentFirstPage || (FirstHeader.IsEmpty && FirstFooter.IsEmpty))
               && (!DifferentOddEven || (EvenHeader.IsEmpty && EvenFooter.IsEmpty));

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
