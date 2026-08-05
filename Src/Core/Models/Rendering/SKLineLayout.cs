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
        /// Исходный код цвета текста (hex либо код градиента "grad|..."). Если это
        /// градиент — при отрисовке строится SKShader, иначе используется плоский Color.
        /// </summary>
        public string? ColorCode { get; init; }

        /// <summary>Исходный код цвета выделения (hex либо код градиента).</summary>
        public string? HighlightCode { get; init; }

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

        /// <summary>
        /// Id встроенной картинки, если сегмент — объект в строке, а не текст.
        /// Такой сегмент всегда состоит ровно из одного символа-заполнителя:
        /// ширина берётся из <see cref="ObjectWidthPt"/>, а не из шрифта, и
        /// посимвольная логика строки работает с ним как с обычным глифом.
        /// </summary>
        public System.Guid? InlineImageId { get; init; }

        /// <summary>Ширина объекта в pt. Значима только для сегмента-объекта.</summary>
        public float ObjectWidthPt { get; init; }

        /// <summary>Высота объекта в pt. Значима только для сегмента-объекта.</summary>
        public float ObjectHeightPt { get; init; }

        /// <summary>Сегмент описывает объект в строке, а не текст.</summary>
        public bool IsInlineObject => InlineImageId.HasValue;

        /// <summary>
        /// Номер отрезка строки, в котором лежит сегмент (см. SKLineLayout.WrapFragments).
        /// 0 — обычная строка в одной полосе. Нужен растяжке по ширине и выделению:
        /// они считаются по каждому отрезку отдельно.
        /// </summary>
        public int WrapFragmentIndex { get; init; }
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
        /// Подъём над базовой линией по метрикам ОДНОГО ТЕКСТА строки, pt — без учёта
        /// габарита встроенных картинок. По нему рисуется каретка: рядом с крупной
        /// картинкой она должна оставаться высотой в кегль текста, а не во всю строку.
        /// </summary>
        public float TextAscentPt { get; set; }

        /// <summary>Спуск под базовую линию по метрикам одного текста строки, pt.</summary>
        public float TextDescentPt { get; set; }

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

        /// <summary>
        /// Левый сдвиг полосы строки внутри текстовой области при обтекании объекта, pt.
        /// 0 — строка начинается от левого края области.
        /// Совпадает с левым краем первого отрезка (<see cref="WrapFragments"/>).
        /// </summary>
        public float WrapLeftPt { get; set; }

        /// <summary>
        /// Ширина доступной полосы строки при обтекании, pt.
        /// 0 — обтекания нет, строка располагается во всей текстовой области.
        /// Совпадает с шириной первого отрезка.
        /// </summary>
        public float WrapAreaWidthPt { get; set; }

        /// <summary>
        /// Свободные отрезки строки при обтекании с двух сторон: строка заполняет первый,
        /// перескакивает через объект и продолжается в следующем. Координаты — от левого
        /// края текстовой области, pt.
        ///
        /// Пусто или один элемент — обычная строка в одной полосе, и весь код может
        /// работать по <see cref="WrapLeftPt"/> и <see cref="WrapAreaWidthPt"/>.
        /// Больше одного — строка разорвана объектом, и выравнивание, растяжка по ширине
        /// и подсветка выделения обязаны считаться по отрезкам, а не по строке целиком.
        /// </summary>
        public List<SKWrapFragment> WrapFragments { get; } = new();

        /// <summary>Строка разорвана объектом и идёт по нескольким отрезкам.</summary>
        public bool HasWrapFragments => WrapFragments.Count > 1;

        /// <summary>
        /// Дополнительный вертикальный сдвиг перед строкой, pt: строка вытеснена
        /// под обтекаемый объект, потому что рядом с ним не осталось места.
        /// </summary>
        public float WrapExtraTopPt { get; set; }
    }

    /// <summary>
    /// С какой стороны от объекта разрешено идти тексту.
    /// </summary>
    public enum SKWrapSide
    {
        /// <summary>Только по той стороне, где больше свободного места.</summary>
        LargestOnly = 0,
        /// <summary>С обеих сторон: строка идёт слева от объекта и продолжается справа.</summary>
        BothSides = 1,
        /// <summary>Только слева от объекта.</summary>
        LeftOnly = 2,
        /// <summary>Только справа от объекта.</summary>
        RightOnly = 3
    }

    /// <summary>
    /// Свободный отрезок строки при обтекании: левый край и ширина, pt,
    /// от левого края текстовой области параграфа.
    /// </summary>
    public readonly struct SKWrapFragment
    {
        public SKWrapFragment(float leftPt, float widthPt)
        {
            LeftPt = leftPt;
            WidthPt = widthPt;
        }

        public float LeftPt { get; }
        public float WidthPt { get; }
        public float RightPt => LeftPt + WidthPt;
    }

    /// <summary>
    /// Зона исключения при обтекании текстом — габарит плавающего объекта с полями.
    /// Координаты в pt: Y — относительно верха первой строки параграфа,
    /// X — относительно левого края текстовой области параграфа.
    /// </summary>
    public readonly struct SKWrapZone
    {
        public SKWrapZone(float topPt, float bottomPt, float leftPt, float rightPt,
            SKWrapSide side = SKWrapSide.LargestOnly)
        {
            TopPt = topPt;
            BottomPt = bottomPt;
            LeftPt = leftPt;
            RightPt = rightPt;
            Side = side;
        }

        /// <summary>С какой стороны от этого объекта разрешено идти тексту.</summary>
        public SKWrapSide Side { get; }

        public float TopPt { get; }
        public float BottomPt { get; }
        public float LeftPt { get; }
        public float RightPt { get; }
    }
}