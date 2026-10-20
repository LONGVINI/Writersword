using System;
using System.Collections.Generic;
using Writersword.Modules.TextEditor.Models.Document;
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
    ///
    /// Переплёт у каждого раздела свой (поле w:gutter его w:sectPr): лист раздела
    /// сдвигается на переплёт своего раздела, а не документа. Раньше брался переплёт
    /// документа на всех листах, и на альбомном листе раздела без переплёта текст
    /// уезжал влево на чужие 7 мм — к самому краю листа.
    ///
    /// Зеркальные поля (TextEditorPageSettings.MirrorMargins) ведут себя так же, но на
    /// листе с переплётом справа местами меняются и сами поля: левое поле — внутреннее,
    /// и на чётном листе оно стоит справа. Ширина текста и тут не меняется, поэтому
    /// это тот же сдвиг листа, только на переплёт и разницу полей
    /// (<see cref="SheetShiftPt"/>).
    ///
    /// Исключение — плавающий объект из Word, отсчитанный от края листа или от бокового
    /// поля: край бумаги с переплётом не двигается, и объект от листа стоит на месте,
    /// а от середины поля уходит на половину сдвига. Проход ставит такой объект на
    /// листе с переплётом справа заранее с поправкой (<see cref="LayoutGutterCompensationPt"/>),
    /// и общий сдвиг при показе приводит его ровно туда, где его ставит Word. Зоны
    /// обтекания при этом строятся по тому же месту, что и рисуется картинка.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        // Листы с переплётом справа по последнему проходу. Проход ставит плавающие
        // объекты раньше, чем станут известны номера листов, поэтому сторона берётся
        // из прошлого прохода; сходимость обтекания повторяет проход, и к концу она
        // уже своя.
        private bool[] _gutterRightHint = Array.Empty<bool>();

        /// <summary>
        /// Запоминает стороны переплёта по готовому проходу для следующего.
        /// </summary>
        private void RememberGutterSides(List<ParaLayout> layouts, int pageCount)
        {
            _gutterRightHint = GutterAlternates && (AnyGutter() || MirrorMargins) && pageCount > 0
                ? GutterRightPages(layouts, pageCount)
                : Array.Empty<bool>();
        }

        /// <summary>
        /// Стоит ли переплёт справа на листе pageIndex по последнему проходу. Лист,
        /// которого в нём ещё не было, продолжает чередование последнего известного.
        /// </summary>
        private bool GutterRightHint(int pageIndex)
        {
            if (pageIndex < 0) return false;

            var hint = _gutterRightHint;
            if (pageIndex < hint.Length) return hint[pageIndex];
            if (hint.Length == 0) return (pageIndex + 1) % 2 == 0;

            int last = hint.Length - 1;
            return hint[last] ^ ((pageIndex - last) % 2 == 1);
        }

        /// <summary>
        /// Поправка к месту плавающего объекта из Word на листе pageIndex, в пунктах
        /// раскладки прохода. Показ сдвигает весь лист с переплётом справа на ширину
        /// переплёта; объект, который за полосой набора уходит не целиком, ставится
        /// заранее правее на ту часть сдвига, которую ему проходить не нужно.
        /// </summary>
        private float LayoutGutterCompensationPt(TableFloatPosition? anchor, int pageIndex)
        {
            if (anchor is null) return 0f;
            if (!GutterAlternates) return 0f;

            // Раздел листа — по показанной раскладке: проход ставит объект раньше, чем
            // известны его листы, как и сторону переплёта.
            float shiftPt = SheetShiftPt(SectionSheetOfPage(pageIndex));
            if (shiftPt == 0f || !GutterRightHint(pageIndex)) return 0f;

            return shiftPt * (1f - anchor.HorizontalTextFollow());
        }

        /// <summary>
        /// Переплёт меняет сторону по листам: режим листов и разные колонтитулы чётных и
        /// нечётных страниц или зеркальные поля.
        /// </summary>
        private bool GutterAlternates
            => HeaderFootersDrawable && (DocVm?.HeaderFooter?.DifferentOddEven == true || MirrorMargins);

        /// <summary>Зеркальные поля документа.</summary>
        private bool MirrorMargins => DocVm?.Document.PageSettings.MirrorMargins == true;

        /// <summary>
        /// На сколько левее встаёт текст листа с переплётом справа, в пунктах: переплёт,
        /// а при зеркальных полях ещё и разница левого (внутреннего) и правого
        /// (внешнего) поля — они меняются местами. Может быть и меньше нуля: внешнее
        /// поле шире внутреннего, и текст уходит вправо.
        /// </summary>
        private float SheetShiftPt(Models.Page.TextEditorPageSettings? sheet)
        {
            float shiftPt = SheetGutterPt(sheet);
            if (!MirrorMargins) return shiftPt;

            var page = SectionSheetsApply ? sheet ?? DocVm?.Document.PageSettings : DocVm?.Document.PageSettings;
            if (page is null) return shiftPt;

            return shiftPt + MmToPt(page.MarginLeftMm - page.MarginRightMm);
        }

        /// <summary>
        /// Переплёт листа, в пунктах: раздела со своим листом или, без него, документа.
        /// </summary>
        private float SheetGutterPt(Models.Page.TextEditorPageSettings? sheet)
        {
            var page = SectionSheetsApply ? sheet ?? DocVm?.Document.PageSettings : DocVm?.Document.PageSettings;
            return page is null || page.MarginGutterMm <= 0 ? 0f : MmToPt(page.MarginGutterMm);
        }

        /// <summary>Переплёт есть хотя бы у документа или у одного из разделов.</summary>
        private bool AnyGutter()
        {
            var document = DocVm?.Document;
            if (document is null) return false;
            if (document.PageSettings.MarginGutterMm > 0) return true;
            if (!SectionSheetsApply || document.Sections.Count == 0) return false;

            foreach (var block in document.Sections[0].Blocks)
                if (block is BreakBlock { IsSectionBreak: true, Section.Page.MarginGutterMm: > 0 })
                    return true;

            return false;
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
            if (!GutterAlternates || !(AnyGutter() || MirrorMargins) || pages.Count == 0)
                return (layouts, pages, tables, images, shapes);

            bool[] right = GutterRightPages(layouts, pages.Count);

            // Сдвиг каждого листа — переплёт его раздела.
            var sheets = SectionSheetsApply
                ? BuildPageSections(layouts, tables, pages.Count, layout => layout?.Page is not null)
                : null;

            var shift = new float[pages.Count];
            bool anyShift = false;
            for (int p = 0; p < pages.Count; p++)
            {
                if (!right[p]) continue;
                shift[p] = -SheetShiftPt(sheets?[p].Page);
                anyShift |= shift[p] != 0f;
            }
            if (!anyShift) return (layouts, pages, tables, images, shapes);

            float ShiftOf(int pageIndex)
                => pageIndex >= 0 && pageIndex < shift.Length ? shift[pageIndex] : 0f;

            var newPages = new List<PageRect>(pages.Count);
            for (int p = 0; p < pages.Count; p++)
            {
                var page = pages[p];
                newPages.Add(shift[p] != 0f
                    ? page with { MarginLeftPt = page.MarginLeftPt + shift[p], GutterShiftPt = shift[p] }
                    : page);
            }

            var newLayouts = new List<ParaLayout>(layouts.Count);
            foreach (var pl in layouts)
            {
                float s = ShiftOf(pl.PageIndex);
                newLayouts.Add(s != 0f
                    ? pl with { AbsXPt = pl.AbsXPt + s, Cell = pl.Cell?.ShiftedX(s) }
                    : pl);
            }

            var newTables = new List<TableEntry>(tables.Count);
            foreach (var te in tables)
            {
                float s = ShiftOf(te.PageIndex);
                newTables.Add(s != 0f ? te with { XPt = te.XPt + s } : te);
            }

            var newImages = new List<ImageEntry>(images.Count);
            foreach (var ie in images)
            {
                float s = ShiftOf(ie.PageIndex);
                newImages.Add(s != 0f ? ie with { XPt = ie.XPt + s } : ie);
            }

            var newShapes = new List<ShapeEntry>(shapes.Count);
            foreach (var se in shapes)
            {
                float s = ShiftOf(se.PageIndex);
                newShapes.Add(s != 0f ? se with { XPt = se.XPt + s } : se);
            }

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
