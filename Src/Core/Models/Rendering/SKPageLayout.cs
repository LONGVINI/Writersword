using System.Collections.Generic;

namespace Writersword.Core.Models.Rendering
{
    /// <summary>
    /// Параграф с его позицией на странице.
    /// Хранит результат вёрстки, Y-смещение и диапазон строк
    /// относительно начала текстовой области страницы.
    /// Один параграф может давать несколько SKPageParagraph
    /// если он разбивается на границе страниц.
    /// </summary>
    public sealed class SKPageParagraph
    {
        /// <summary>Результат вёрстки параграфа.</summary>
        public SKTextLayout Layout { get; init; } = null!;

        /// <summary>
        /// Y-позиция верхнего края первой строки слайса в pt относительно
        /// начала текстовой области страницы (после MarginTop).
        /// Для первого слайса включает SpaceBeforePt, для последующих — нет.
        /// </summary>
        public float Y { get; init; }

        /// <summary>
        /// Индекс первой строки диапазона (включительно) в Layout.Lines.
        /// </summary>
        public int LineFrom { get; init; }

        /// <summary>
        /// Индекс строки за последней строкой диапазона (не включительно) в Layout.Lines.
        /// LineTo == Layout.Lines.Count означает последний слайс параграфа.
        /// </summary>
        public int LineTo { get; init; }

        /// <summary>
        /// Индекс параграфа в документе (в списке Paragraphs).
        /// Используется DocumentCanvas для сопоставления layout с ViewModel.
        /// </summary>
        public int ParagraphIndex { get; init; }
    }

    /// <summary>
    /// Таблица с её позицией на странице для рендеринга.
    /// </summary>
    public sealed class SKPageTable
    {
        public SKTableLayout Layout { get; init; } = null!;
        public float Y { get; init; }
        public float LeftIndentPt { get; init; }
        public int RowFrom { get; init; }
        public int RowTo { get; init; } = -1;
        public int HeaderRowIndex { get; init; } = -1;
        public float HeaderRowHeightPt { get; init; } = 0f;

        // ── ByCell split: последняя строка этого слайса разрывается посередине ──
        /// <summary>
        /// Если ≥ 0 — последняя строка слайса разрывается в ByCell режиме.
        /// Значение = высота видимой части разорванной строки в pt (сколько показываем на этой странице).
        /// -1 = строка целиком (ByRow режим).
        /// </summary>
        public float LastRowVisibleHeightPt { get; init; } = -1f;

        /// <summary>
        /// Для продолжения разорванной строки: смещение Y внутри ячейки откуда начинаем рисовать.
        /// 0 = начало ячейки, >0 = содержимое продолжается с этой точки.
        /// </summary>
        public float LastRowContentOffsetPt { get; init; } = 0f;

        // ── Метки ─────────────────────────────────────────────────────────
        /// <summary>Текст под таблицей перед разрывом страницы. Null = не рисовать.</summary>
        public string? BreakLabel { get; init; }

        /// <summary>Текст над продолжением таблицы. Null = не рисовать.</summary>
        public string? ContinuationLabel { get; init; }

        /// <summary>
        /// True если это продолжение разорванной строки (ByCell).
        /// Первая строка слайса не рисует верхнюю границу — она "продолжает" строку предыдущей страницы.
        /// </summary>
        public bool IsContinuation { get; init; } = false;

        /// <summary>
        /// Смещение контента внутри первой строки слайса-продолжения (ByCell).
        /// При рендеринге первой строки следующей страницы
        /// содержимое ячейки сдвигается вверх на это значение.
        /// 0 = начало строки (нет смещения).
        /// </summary>
        public float FirstRowContentOffsetPt { get; init; } = 0f;
    }

    /// <summary>
    /// Одна страница документа после вёрстки.
    /// Содержит список параграфов которые на неё попали.
    /// </summary>
    public sealed class SKPageContent
    {
        /// <summary>Параграфы страницы в порядке следования сверху вниз.</summary>
        public List<SKPageParagraph> Paragraphs { get; } = new();

        /// <summary>Таблицы страницы в порядке следования сверху вниз.</summary>
        public List<SKPageTable> Tables { get; } = new();

        /// <summary>
        /// Физическая ширина страницы в pt (включая поля).
        /// Используется для рендеринга фона страницы.
        /// </summary>
        public float PageWidthPt { get; init; }

        /// <summary>
        /// Физическая высота страницы в pt (включая поля).
        /// </summary>
        public float PageHeightPt { get; init; }

        /// <summary>Левое поле страницы в pt.</summary>
        public float MarginLeftPt { get; init; }

        /// <summary>Верхнее поле страницы в pt.</summary>
        public float MarginTopPt { get; init; }

        /// <summary>Ширина текстовой области в pt (без полей).</summary>
        public float TextWidthPt { get; init; }

        /// <summary>Высота текстовой области в pt (без полей).</summary>
        public float TextHeightPt { get; init; }
    }

    /// <summary>
    /// Результат вёрстки всего документа — список страниц.
    /// Строится один раз в SKTextRenderer.BuildPageLayout().
    /// Пересчитывается при изменении текста, полей или размера страницы.
    /// Используется и DocumentCanvas (для отображения) и
    /// TextEditorPrintDocument (для рендеринга в PDF).
    /// </summary>
    public sealed class SKPageLayout
    {
        /// <summary>Страницы документа в порядке следования.</summary>
        public List<SKPageContent> Pages { get; } = new();

        /// <summary>Количество страниц.</summary>
        public int PageCount => Pages.Count;

        /// <summary>
        /// Находит страницу и параграф по глобальному индексу параграфа.
        /// Возвращает null если параграф не найден.
        /// Используется DocumentCanvas для определения на какой странице
        /// находится активный параграф.
        /// </summary>
        public (SKPageContent Page, SKPageParagraph Para)? FindParagraph(int paragraphIndex)
        {
            foreach (var page in Pages)
                foreach (var para in page.Paragraphs)
                    if (para.ParagraphIndex == paragraphIndex)
                        return (page, para);
            return null;
        }

        /// <summary>
        /// Возвращает абсолютную Y-позицию параграфа в pt
        /// относительно начала всего документа (сумма высот всех предыдущих страниц).
        /// Используется DocumentCanvas для скролла к активному параграфу.
        /// </summary>
        /// <param name="paragraphIndex">Индекс параграфа в документе.</param>
        /// <param name="pageGapPt">Расстояние между страницами в pt.</param>
        public float GetAbsoluteParaY(int paragraphIndex, float pageGapPt = 20f)
        {
            float absoluteY = 0f;

            for (int i = 0; i < Pages.Count; i++)
            {
                var page = Pages[i];

                foreach (var para in page.Paragraphs)
                {
                    if (para.ParagraphIndex == paragraphIndex)
                        return absoluteY + page.MarginTopPt + para.Y;
                }

                absoluteY += page.PageHeightPt + pageGapPt;
            }

            return 0f;
        }

        /// <summary>
        /// Возвращает абсолютную высоту всего документа в pt включая межстраничные отступы.
        /// Используется DocumentCanvas для задания высоты скроллируемой области.
        /// </summary>
        /// <param name="pageGapPt">Расстояние между страницами в pt.</param>
        public float GetTotalHeightPt(float pageGapPt = 20f)
        {
            if (Pages.Count == 0) return 0f;

            float total = 0f;
            foreach (var page in Pages)
                total += page.PageHeightPt + pageGapPt;

            return total;
        }
    }
}