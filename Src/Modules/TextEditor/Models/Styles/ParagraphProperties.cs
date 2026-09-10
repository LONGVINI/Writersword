using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Writersword.Modules.TextEditor.Models.Styles
{
    /// <summary>
    /// Способ выравнивания текста в абзаце.
    /// </summary>
    public enum TextAlignment
    {
        Left = 0,
        Center = 1,
        Right = 2,
        Justify = 3
    }

    /// <summary>
    /// Межстрочный интервал.
    /// </summary>
    public enum LineSpacingRule
    {
        /// <summary>Автоматический (зависит от размера шрифта).</summary>
        Auto = 0,
        /// <summary>Точное значение в пунктах.</summary>
        Exact = 1,
        /// <summary>Минимальное значение в пунктах.</summary>
        AtLeast = 2
    }

    /// <summary>
    /// Все свойства форматирования абзаца.
    /// Используется в <see cref="Document.ParagraphBlock"/> и в <see cref="DocumentStyle"/>.
    /// Null-поля означают "унаследовать от базового стиля".
    /// </summary>
    public sealed class ParagraphProperties
    {
        /// <summary>Выравнивание. Null — унаследовать.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public TextAlignment? Alignment { get; set; }

        /// <summary>Отступ первой строки в пунктах (красная строка). Отрицательный — висячий.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? FirstLineIndent { get; set; }

        /// <summary>Левый отступ абзаца в пунктах.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? LeftIndent { get; set; }

        /// <summary>Правый отступ абзаца в пунктах.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? RightIndent { get; set; }

        /// <summary>Интервал до абзаца в пунктах.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? SpaceBefore { get; set; }

        /// <summary>Интервал после абзаца в пунктах.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? SpaceAfter { get; set; }

        /// <summary>Правило вычисления межстрочного интервала.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public LineSpacingRule? LineSpacingRule { get; set; }

        /// <summary>
        /// Значение межстрочного интервала.
        /// Для Auto: множитель (1.0 = одинарный, 1.5, 2.0).
        /// Для Exact/AtLeast: значение в пунктах.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? LineSpacingValue { get; set; }

        /// <summary>Запрет переноса абзаца на другую страницу.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool KeepTogether { get; set; }

        /// <summary>Держать абзац вместе со следующим (для заголовков).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool KeepWithNext { get; set; }

        /// <summary>Начинать абзац с новой страницы.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool PageBreakBefore { get; set; }

        /// <summary>Имя базового стиля абзаца (например "Normal", "Heading1").</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? StyleName { get; set; }

        /// <summary>
        /// Структурный уровень абзаца (Outline Level): 0 — основной текст, 1…9 — уровни.
        /// Не задаёт отступ; это смысловая метка для оглавления и навигатора.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int OutlineLevel { get; set; }

        /// <summary>
        /// Взять абзац в оглавление, даже если он не носит стиль заголовка и уровня ему
        /// не задано.
        ///
        /// Пометка нужна ровно там, где Word заставляет городить стиль ради одной строки:
        /// эпиграф, интерлюдия, письмо посреди главы — вещи, которые в оглавлении видеть
        /// хочется, а выглядеть заголовком они не должны.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IncludeInToc { get; set; }

        /// <summary>
        /// Абзац принадлежит оглавлению с этим опознавателем — это его строка, а не текст
        /// рукописи. По метке оглавление узнаёт своё и пересобирает только себя, не трогая
        /// соседний текст; по ней же строки оглавления не попадают в оглавление сами.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Guid? TocOwnerId { get; set; }

        /// <summary>
        /// Уровень строки внутри оглавления: 0 — название «Оглавление», 1…9 — сами строки.
        /// Значим только вместе с <see cref="TocOwnerId"/>.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int TocEntryLevel { get; set; }

        /// <summary>
        /// Куда ведёт строка оглавления — опознаватель абзаца-заголовка.
        /// Заголовок мог быть удалён: тогда строка просто перестаёт быть ссылкой.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Guid? TocTargetBlockId { get; set; }

        /// <summary>
        /// Позиции табуляции этого абзаца. Null — у абзаца своих позиций нет, и символ
        /// табуляции идёт к ближайшей позиции по умолчанию (шаг задан документом).
        ///
        /// Свои позиции полностью отменяют шаг по умолчанию слева от последней из них —
        /// так же поступает Word: расставив свои, человек описывает строку сам.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<TabStop>? TabStops { get; set; }

        /// <summary>
        /// Создаёт копию свойств.
        ///
        /// Позиции табуляции копируются поимённо, а не ссылкой: почленное копирование
        /// отдало бы копии тот же самый список, и правка табуляции в одном абзаце
        /// расходилась бы по всем, кто произошёл от него через Enter или буфер обмена.
        /// </summary>
        public ParagraphProperties Clone()
        {
            var copy = (ParagraphProperties)MemberwiseClone();

            if (TabStops is not null)
            {
                var tabs = new List<TabStop>(TabStops.Count);
                foreach (var tab in TabStops) tabs.Add(tab.Clone());
                copy.TabStops = tabs;
            }

            return copy;
        }

        /// <summary>
        /// Копирует все свойства из src в текущий экземпляр (не меняя ссылку).
        /// Используется командой отмены форматирования абзаца для восстановления старых значений.
        /// </summary>
        public void CopyFrom(ParagraphProperties src)
        {
            Alignment = src.Alignment;
            FirstLineIndent = src.FirstLineIndent;
            LeftIndent = src.LeftIndent;
            RightIndent = src.RightIndent;
            SpaceBefore = src.SpaceBefore;
            SpaceAfter = src.SpaceAfter;
            LineSpacingRule = src.LineSpacingRule;
            LineSpacingValue = src.LineSpacingValue;
            KeepTogether = src.KeepTogether;
            KeepWithNext = src.KeepWithNext;
            PageBreakBefore = src.PageBreakBefore;
            StyleName = src.StyleName;
            OutlineLevel = src.OutlineLevel;
            IncludeInToc = src.IncludeInToc;

            if (src.TabStops is null)
            {
                TabStops = null;
            }
            else
            {
                var tabs = new List<TabStop>(src.TabStops.Count);
                foreach (var tab in src.TabStops) tabs.Add(tab.Clone());
                TabStops = tabs;
            }

            TocOwnerId = src.TocOwnerId;
            TocEntryLevel = src.TocEntryLevel;
            TocTargetBlockId = src.TocTargetBlockId;
        }
    }
}
