using SkiaSharp;

namespace Writersword.Core.Models.Rendering
{
    /// <summary>
    /// Метрики одного глифа (символа) в строке.
    /// Используется для точного позиционирования каретки и выделения.
    /// Координаты в points (pt) относительно начала строки.
    /// </summary>
    public readonly struct SKGlyphMetrics
    {
        /// <summary>
        /// Индекс символа в тексте параграфа (глобальный, не в Run).
        /// Используется для сопоставления позиции клика с позицией в PlainText.
        /// </summary>
        public int CharIndex { get; init; }

        /// <summary>X-позиция левого края глифа в pt относительно начала строки.</summary>
        public float X { get; init; }

        /// <summary>Ширина глифа в pt.</summary>
        public float Width { get; init; }

        /// <summary>X-позиция правого края глифа в pt.</summary>
        public float Right => X + Width;

        /// <summary>
        /// Середина глифа по X в pt.
        /// Если клик левее MidX — каретка ставится перед символом (CharIndex),
        /// если правее — после символа (CharIndex + 1).
        /// </summary>
        public float MidX => X + Width * 0.5f;
    }

    /// <summary>
    /// Результат HitTest по точке — позиция каретки и метаданные попадания.
    /// </summary>
    public readonly struct SKHitTestResult
    {
        /// <summary>
        /// Позиция каретки в тексте параграфа (индекс символа).
        /// Каретка стоит ПЕРЕД символом с этим индексом.
        /// Значение равно длине текста если клик был после последнего символа.
        /// </summary>
        public int CharIndex { get; init; }

        /// <summary>
        /// True — точка попала непосредственно в глиф.
        /// False — точка была за пределами текста, взята ближайшая позиция.
        /// </summary>
        public bool IsInside { get; init; }

        /// <summary>
        /// True — позиция находится после последнего символа строки.
        /// DocumentCanvas использует это для перехода фокуса на следующую строку
        /// при навигации стрелками вниз.
        /// </summary>
        public bool IsTrailingEdge { get; init; }
    }

    /// <summary>
    /// Прямоугольник каретки в координатах параграфа (pt).
    /// DocumentCanvas использует это для отрисовки мигающей вертикальной черты.
    /// </summary>
    public readonly struct SKCaretRect
    {
        /// <summary>
        /// X-позиция каретки в pt относительно начала текстовой области страницы.
        /// Уже включает отступы параграфа и страницы.
        /// </summary>
        public float X { get; init; }

        /// <summary>
        /// Y-позиция верхнего края каретки в pt
        /// относительно начала текстовой области страницы.
        /// </summary>
        public float Y { get; init; }

        /// <summary>Высота каретки в pt — равна высоте строки.</summary>
        public float Height { get; init; }

        /// <summary>
        /// Y-позиция baseline строки в pt.
        /// Используется для выравнивания каретки по базовой линии текста.
        /// </summary>
        public float Baseline { get; init; }
    }

    /// <summary>
    /// Прямоугольник выделения текста в координатах страницы (pt).
    /// Один параграф может давать несколько прямоугольников
    /// если выделение охватывает несколько строк.
    /// </summary>
    public readonly struct SKSelectionRect
    {
        /// <summary>
        /// Прямоугольник в pt относительно начала текстовой области страницы.
        /// Уже включает отступы параграфа, поля страницы и Y-смещение параграфа.
        /// </summary>
        public SKRect Rect { get; init; }

        /// <summary>
        /// Индекс строки внутри параграфа которой принадлежит этот прямоугольник.
        /// Используется для отладки и для корректного порядка отрисовки.
        /// </summary>
        public int LineIndex { get; init; }

        /// <summary>
        /// Отрезок строки, к которому относится прямоугольник (см. SKLineLayout.WrapFragments).
        /// У строки, разорванной обтекаемым объектом, выделение состоит из нескольких
        /// прямоугольников, и растяжку по ширине каждый обязан считать по своему отрезку.
        /// </summary>
        public int FragmentIndex { get; init; }
    }
}