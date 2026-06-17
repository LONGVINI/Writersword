using System.Collections.Generic;
using SkiaSharp;
using Writersword.Core.Models.Rendering;

namespace Writersword.Core.Models.Rendering
{
    /// <summary>
    /// Один сегмент строки — часть текста с одинаковым форматированием.
    /// Соответствует одному Run из модели документа после вёрстки.
    /// Координаты в points (pt) относительно начала строки.
    /// </summary>
    public sealed class SKRunSegment
    {
        /// <summary>Текст сегмента (может содержать пробелы).</summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>Гарнитура шрифта.</summary>
        public string FontFamily { get; init; } = "Times New Roman";

        /// <summary>Размер шрифта в pt.</summary>
        public float FontSizePt { get; init; } = 12f;

        /// <summary>Жирный.</summary>
        public bool IsBold { get; init; }

        /// <summary>Курсив.</summary>
        public bool IsItalic { get; init; }

        /// <summary>Подчёркнутый.</summary>
        public bool IsUnderline { get; init; }

        /// <summary>Зачёркнутый.</summary>
        public bool IsStrikethrough { get; init; }

        /// <summary>Цвет текста.</summary>
        public SKColor Color { get; init; } = SKColors.Black;

        /// <summary>Цвет выделения (highlight). Transparent — нет выделения.</summary>
        public SKColor HighlightColor { get; init; } = SKColors.Transparent;

        /// <summary>
        /// X-позиция начала сегмента в pt относительно начала строки.
        /// Устанавливается при вёрстке строки.
        /// </summary>
        public float X { get; set; }

        /// <summary>
        /// Измеренная ширина сегмента в pt.
        /// Устанавливается при вёрстке строки через SKFont.MeasureText.
        /// </summary>
        public float Width { get; set; }

        /// <summary>
        /// Метрики глифов сегмента — по одной на каждый символ Text.
        /// Заполняется при вёрстке через SKFont.GetGlyphWidths.
        /// Используется для посимвольного HitTest и позиционирования каретки.
        /// </summary>
        public SKGlyphMetrics[] GlyphMetrics { get; set; } = System.Array.Empty<SKGlyphMetrics>();

        /// <summary>
        /// Глобальный индекс первого символа этого сегмента в PlainText параграфа.
        /// Используется для сопоставления позиции клика с позицией в модели.
        /// </summary>
        public int GlobalCharOffset { get; init; }

        /// <summary>
        /// Вертикальное смещение базовой линии сегмента в pt относительно базовой линии строки.
        /// Положительное — вверх (надстрочный), отрицательное — вниз (подстрочный), 0 — обычный.
        /// Устанавливается при сборке сегмента из RunProperties (надстрочный/подстрочный текст).
        /// </summary>
        public float BaselineShiftPt { get; init; }
    }

    /// <summary>
    /// Одна строка параграфа после вёрстки.
    /// Содержит список сегментов и метрики строки.
    /// Координаты в points (pt) относительно начала параграфа.
    /// </summary>
    public sealed class SKLineLayout
    {
        /// <summary>Сегменты строки в порядке следования слева направо.</summary>
        public List<SKRunSegment> Segments { get; } = new();

        /// <summary>
        /// Y-позиция верхнего края строки в pt относительно начала параграфа.
        /// Устанавливается при вёрстке параграфа.
        /// </summary>
        public float Y { get; set; }

        /// <summary>Высота строки в pt — включает межстрочный интервал.</summary>
        public float Height { get; set; }

        /// <summary>
        /// Расстояние от верхнего края строки до baseline в pt.
        /// Используется для выравнивания текста разного размера в одной строке.
        /// </summary>
        public float Baseline { get; set; }

        /// <summary>
        /// Суммарная ширина текста строки без учёта trailing whitespace.
        /// Используется для выравнивания (center, right, justify).
        /// </summary>
        public float TextWidth { get; set; }

        /// <summary>
        /// Индекс первого символа строки в PlainText параграфа.
        /// Используется для навигации стрелками вверх/вниз.
        /// </summary>
        public int FirstCharIndex { get; set; }

        /// <summary>
        /// Индекс последнего символа строки в PlainText параграфа (включительно).
        /// Используется для навигации и выделения.
        /// </summary>
        public int LastCharIndex { get; set; }

        /// <summary>
        /// True — последняя строка параграфа.
        /// Justify не применяется к последней строке параграфа.
        /// </summary>
        public bool IsLastLine { get; set; }

        /// <summary>
        /// Количество символов в строке включая пробелы между словами.
        /// </summary>
        public int CharCount => LastCharIndex - FirstCharIndex + 1;
    }
}