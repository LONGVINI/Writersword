namespace Writersword.Core.Models.Rendering
{
    /// <summary>
    /// Точка разрыва страницы для инкрементальной пагинации.
    /// Хранит состояние вёрстки в начале страницы —
    /// позволяет пересчитывать пагинацию только с изменённого места
    /// не трогая параграфы выше.
    /// </summary>
    public sealed class PageBreakpoint
    {
        /// <summary>Индекс параграфа в DocVm.Paragraphs с которого начинается страница.</summary>
        public int ParagraphIndex { get; init; }

        /// <summary>
        /// Индекс строки внутри параграфа с которой начинается страница.
        /// 0 если страница начинается с начала параграфа.
        /// </summary>
        public int LineIndex { get; init; }

        /// <summary>Индекс страницы в _pages.</summary>
        public int PageIndex { get; init; }

        /// <summary>Y-позиция верхнего края страницы в pt.</summary>
        public float PageYPt { get; init; }

        /// <summary>Y-позиция начала контента (после MarginTop) в pt.</summary>
        public float ContentYPt { get; init; }
    }
}