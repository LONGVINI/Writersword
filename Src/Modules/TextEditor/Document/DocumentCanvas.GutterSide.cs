using System.Collections.Generic;
using Writersword.Modules.TextEditor.Services;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Сторона переплёта по листам.
    ///
    /// Поле переплёта у документа одно, и обычно оно прибавляется к левому полю каждого
    /// листа. Но когда у чётных и нечётных страниц разные колонтитулы, Word считает
    /// документ двусторонним и кладёт переплёт к корешку: на нечётных листах слева, на
    /// чётных справа. Ширина текста при этом одна и та же — сдвигается только сам текст:
    /// на чётном листе он стоит левее на ширину переплёта. По широкой таблице это видно
    /// сразу: на одном листе она почти упирается в правый край, на следующем отходит
    /// от него.
    ///
    /// Раскладка от стороны переплёта не зависит: строки и разбивка на страницы те же.
    /// Поэтому проход раскладывает все листы одинаково, с переплётом слева, а сдвиг
    /// кладётся один раз, когда готовая раскладка отдаётся на показ
    /// (<see cref="WithGutterSides"/>). Каждый лист помнит свой сдвиг
    /// (PageRect.GutterShiftPt), и его левое поле уже включает этот сдвиг: каретка,
    /// попадание мышью, колонтитулы и линейка берут место текста с листа и сдвига
    /// не замечают.
    ///
    /// Чётность листа — по номеру, который на нём печатается, как и у колонтитулов:
    /// счёт, начатый с двойки, делает первый лист чётным.
    ///
    /// Сдвиг действует в режиме страниц, в том числе при выгрузке листов в PDF. В ленте
    /// чтения и в развороте книги листы условные, и переплёта у них нет.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        /// <summary>
        /// Ширина переплёта, который меняет сторону по листам, в пунктах. Ноль —
        /// переплёта нет, он всегда слева или режим не листовой.
        /// </summary>
        private float BindingGutterPt()
        {
            if (!HeaderFootersDrawable) return 0f;

            var ps = DocVm?.Document.PageSettings;
            if (ps is null || ps.MarginGutterMm <= 0) return 0f;

            if (DocVm?.HeaderFooter?.DifferentOddEven != true) return 0f;

            return MmToPt(ps.MarginGutterMm);
        }

        /// <summary>
        /// На каких листах переплёт справа: на листах с чётным печатаемым номером.
        /// </summary>
        private bool[] GutterRightPages(List<ParaLayout> layouts, int pageCount)
        {
            var result = new bool[pageCount];

            var facts = BuildPageFacts(layouts, pageCount);
            var decorations = PageNumbering.Compute(
                DocVm?.HeaderFooter, pageCount, facts.ParagraphStartPages, facts.ChapterStartPages);

            for (int p = 0; p < pageCount; p++)
            {
                int number = p < decorations.Length ? decorations[p].Number : p + 1;
                result[p] = number % 2 == 0;
            }

            return result;
        }

        /// <summary>
        /// Раскладка прохода со сдвигом листов, у которых переплёт справа. Списки прохода
        /// не меняются: сдвинутое кладётся в новые списки, и повторный показ тех же
        /// списков сдвинет их ровно так же, а не дважды. Без переплёта, меняющего
        /// сторону, списки возвращаются как есть.
        /// </summary>
        private (List<ParaLayout> Layouts, List<PageRect> Pages, List<TableEntry> Tables,
                 List<ImageEntry> Images, List<ShapeEntry> Shapes) WithGutterSides(
            List<ParaLayout> layouts, List<PageRect> pages, List<TableEntry> tables,
            List<ImageEntry> images, List<ShapeEntry> shapes)
        {
            float gutterPt = BindingGutterPt();
            if (gutterPt <= 0f || pages.Count == 0)
                return (layouts, pages, tables, images, shapes);

            bool[] right = GutterRightPages(layouts, pages.Count);

            bool anyRight = false;
            foreach (bool isRight in right)
            {
                if (isRight) { anyRight = true; break; }
            }
            if (!anyRight) return (layouts, pages, tables, images, shapes);

            float shiftPt = -gutterPt;

            bool Shifted(int pageIndex)
                => pageIndex >= 0 && pageIndex < right.Length && right[pageIndex];

            var newPages = new List<PageRect>(pages.Count);
            for (int p = 0; p < pages.Count; p++)
            {
                var page = pages[p];
                newPages.Add(right[p]
                    ? page with { MarginLeftPt = page.MarginLeftPt + shiftPt, GutterShiftPt = shiftPt }
                    : page);
            }

            var newLayouts = new List<ParaLayout>(layouts.Count);
            foreach (var pl in layouts)
            {
                newLayouts.Add(Shifted(pl.PageIndex)
                    ? pl with { AbsXPt = pl.AbsXPt + shiftPt, Cell = pl.Cell?.ShiftedX(shiftPt) }
                    : pl);
            }

            var newTables = new List<TableEntry>(tables.Count);
            foreach (var te in tables)
                newTables.Add(Shifted(te.PageIndex) ? te with { XPt = te.XPt + shiftPt } : te);

            var newImages = new List<ImageEntry>(images.Count);
            foreach (var ie in images)
                newImages.Add(Shifted(ie.PageIndex) ? ie with { XPt = ie.XPt + shiftPt } : ie);

            var newShapes = new List<ShapeEntry>(shapes.Count);
            foreach (var se in shapes)
                newShapes.Add(Shifted(se.PageIndex) ? se with { XPt = se.XPt + shiftPt } : se);

            return (newLayouts, newPages, newTables, newImages, newShapes);
        }

        /// <summary>
        /// Снимает сдвиг переплёта с записей, взятых из показанной раскладки: частичный
        /// проход переносит оттуда листы до стартового, а сам работает без сдвига.
        /// Сдвиг каждой записи — тот, что помнит её лист в показанной раскладке.
        /// </summary>
        private static void WithoutGutterSides(
            List<PageRect> shownPages,
            List<ParaLayout> layouts, List<TableEntry> tables,
            List<ImageEntry> images, List<ShapeEntry> shapes)
        {
            float ShiftOf(int pageIndex)
                => pageIndex >= 0 && pageIndex < shownPages.Count
                    ? shownPages[pageIndex].GutterShiftPt
                    : 0f;

            for (int i = 0; i < layouts.Count; i++)
            {
                var pl = layouts[i];
                float shiftPt = ShiftOf(pl.PageIndex);
                if (shiftPt == 0f) continue;

                layouts[i] = pl with { AbsXPt = pl.AbsXPt - shiftPt, Cell = pl.Cell?.ShiftedX(-shiftPt) };
            }

            for (int i = 0; i < tables.Count; i++)
            {
                var te = tables[i];
                float shiftPt = ShiftOf(te.PageIndex);
                if (shiftPt != 0f) tables[i] = te with { XPt = te.XPt - shiftPt };
            }

            for (int i = 0; i < images.Count; i++)
            {
                var ie = images[i];
                float shiftPt = ShiftOf(ie.PageIndex);
                if (shiftPt != 0f) images[i] = ie with { XPt = ie.XPt - shiftPt };
            }

            for (int i = 0; i < shapes.Count; i++)
            {
                var se = shapes[i];
                float shiftPt = ShiftOf(se.PageIndex);
                if (shiftPt != 0f) shapes[i] = se with { XPt = se.XPt - shiftPt };
            }
        }
    }
}
