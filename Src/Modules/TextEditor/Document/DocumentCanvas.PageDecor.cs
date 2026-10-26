using System;
using System.Collections.Generic;
using System.Globalization;
using SkiaSharp;
using Writersword.Core.Models.Print;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Page;
using Writersword.Modules.TextEditor.Rendering;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Оформление листа, которое раздел Word задаёт сверх полей и колонок: рамка
    /// страницы (w:pgBorders) и номера строк (w:lnNumType). Рисуется на листах правки
    /// поверх раскладки и её не трогает.
    ///
    /// Раздел листа — раздел первого блока, который на листе стоит. Лист без блоков
    /// (пустой лист перед разделом с чётной или нечётной страницы) — раздела листа
    /// перед ним, как у Word. Первый лист раздела — тот, где раздел сменился.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        /// <summary>Раздел листа: его параметры, лист, номер и первый ли это лист раздела.</summary>
        private sealed record PageSectionInfo(
            SectionLayout? Layout,
            TextEditorPageSettings Page,
            int SectionIndex,
            bool FirstPageOfSection,
            BreakBlock? Break = null);

        // Разделы листов последней раскладки и признак, что они собраны (null при
        // собранных — оформлять нечего). Сбрасываются вместе с раскладкой.
        private PageSectionInfo[]? _pageSections;
        private bool _pageSectionsReady;

        // Расстояние от номера строки до текста, когда раздел его не задал: у Word «авто» —
        // четверть дюйма.
        private const float LineNumberDefaultDistancePt = 18f;

        private const float LineNumberFontSizePt = 9f;

        /// <summary>Разделы листов; null — оформлять нечего (не лист правки или нет разделов с оформлением).</summary>
        private PageSectionInfo[]? PageSections(List<ParaLayout> layouts, List<TableEntry> tables, int pageCount)
        {
            if (_pageSectionsReady && (_pageSections is null || _pageSections.Length == pageCount)) return _pageSections;
            if (DocVm is null || pageCount <= 0 || !SectionSheetsApply) return null;

            _pageSectionsReady = true;
            _pageSections = BuildPageSections(layouts, tables, pageCount, HasPageDecor);
            return _pageSections;
        }

        /// <summary>
        /// Раздел каждого листа. Null — ни один раздел не нужен <paramref name="relevant"/>:
        /// считать незачем.
        /// </summary>
        private PageSectionInfo[]? BuildPageSections(
            List<ParaLayout> layouts, List<TableEntry> tables, int pageCount, Func<SectionLayout?, bool> relevant)
        {
            if (DocVm is null || pageCount <= 0) return null;

            var document = DocVm.Document;
            if (document.Sections.Count == 0) return null;
            var blocks = document.Sections[0].Blocks;

            // Разделы потока: первый блок и параметры. Последний — параметры документа.
            var sections = new List<(int FirstBlock, SectionLayout? Layout, TextEditorPageSettings Page)>();
            var breaks = new List<BreakBlock?>();
            bool anyDecor = false;
            int start = 0;
            // В «Исходном документе» у разделов со сменой параметров — прежние рамка,
            // номера строк, выравнивание по высоте и лист (RevisionDisplay.DisplaySection).
            var view = document.RevisionView;
            for (int i = 0; i < blocks.Count; i++)
            {
                if (blocks[i] is not BreakBlock { IsSectionBreak: true } sectionBreak) continue;
                var layout = RevisionDisplay.DisplaySection(sectionBreak, view);
                sections.Add((start, layout, layout?.Page ?? document.PageSettings));
                breaks.Add(sectionBreak);
                anyDecor |= relevant(layout);
                start = i + 1;
            }

            var previousFinal = RevisionDisplay.DisplayFinalSection(document, view);
            var finalLayout = previousFinal?.Layout ?? document.FinalSection;
            sections.Add((start, finalLayout, previousFinal?.Page ?? document.PageSettings));
            breaks.Add(null);
            anyDecor |= relevant(finalLayout);

            if (!anyDecor) return null;

            var indexOf = new Dictionary<BlockModel, int>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < blocks.Count; i++) indexOf[blocks[i]] = i;

            // Первый блок каждого листа.
            var firstBlock = new int[pageCount];
            Array.Fill(firstBlock, int.MaxValue);

            foreach (var pl in layouts)
            {
                if (pl.PageIndex < 0 || pl.PageIndex >= pageCount) continue;
                BlockModel? block = pl.Cell is { } cell ? cell.Table : pl.Vm?.Model;
                if (block is null || !indexOf.TryGetValue(block, out int index)) continue;
                if (index < firstBlock[pl.PageIndex]) firstBlock[pl.PageIndex] = index;
            }

            foreach (var table in tables)
            {
                if (table.PageIndex < 0 || table.PageIndex >= pageCount) continue;
                if (!indexOf.TryGetValue(table.Table, out int index)) continue;
                if (index < firstBlock[table.PageIndex]) firstBlock[table.PageIndex] = index;
            }

            var result = new PageSectionInfo[pageCount];
            int previous = -1;
            for (int p = 0; p < pageCount; p++)
            {
                int section = firstBlock[p] == int.MaxValue
                    ? Math.Max(previous, 0)
                    : SectionOfBlock(sections, firstBlock[p]);

                var (_, layout, page) = sections[section];
                result[p] = new PageSectionInfo(layout, page, section, section != previous, breaks[section]);
                previous = section;
            }

            return result;
        }

        // Разделы всех листов — для линейки; собираются по требованию, сбрасываются
        // вместе с раскладкой.
        private PageSectionInfo[]? _allPageSections;
        private bool _allPageSectionsReady;

        /// <summary>Раздел листа для линейки: всё, что о нём нужно; null — раскладки ещё нет.</summary>
        private PageSectionInfo? SectionInfoOfPage(int pageIndex)
        {
            if (!SectionSheetsApply || DocVm is null) return null;

            List<ParaLayout> layouts;
            List<TableEntry> tables;
            int pageCount;
            lock (_renderLock) { layouts = _layouts; tables = _tables; pageCount = _pages.Count; }

            if (_rulerPageSections is null || _rulerPageSections.Length != pageCount)
                _rulerPageSections = BuildPageSections(layouts, tables, pageCount, _ => true);

            return _rulerPageSections is not null && pageIndex >= 0 && pageIndex < _rulerPageSections.Length
                ? _rulerPageSections[pageIndex]
                : null;
        }

        // Разделы всех листов без отбора — для листа под кареткой на линейке.
        private PageSectionInfo[]? _rulerPageSections;

        // Последний лист, показанный линейке: повторно о том же не сообщается.
        private (object? Section, TextEditorPageSettings? Page, string Gaps)? _lastRulerSheet;

        /// <summary>Лист под кареткой изменился: линейке его размер, поля и колонки.</summary>
        public Action<ViewModels.Components.RulerSheet>? RulerSheetChanged { get; set; }

        private void NotifyRulerSheet()
        {
            if (RulerSheetChanged is null || DocVm is null) return;

            int pageIndex = _caretPara >= 0 && _caretPara < _layouts.Count ? _layouts[_caretPara].PageIndex : 0;
            var info = SectionInfoOfPage(pageIndex);

            var document = DocVm.Document;
            var page = info?.Page ?? document.PageSettings;
            bool own = info is not null && !ReferenceEquals(page, document.PageSettings);

            // Лист «Исходного документа» с прежними параметрами раздела — двойник
            // (RevisionDisplay.DisplaySection): линейка показывает его поля, а сдвинутые
            // на ней края уходят в сам раздел, у последнего раздела — в документ.
            var realLayout = info?.Break is { } realBreak ? realBreak.Section : document.FinalSection;
            bool previousSheet = info?.Layout is not null && !ReferenceEquals(info.Layout, realLayout);
            object? section = !own ? null
                : previousSheet ? (info!.Break is not null ? realLayout : null)
                : info!.Layout;

            // Колонки — раздела, в котором стоит каретка. Раздел листа считается по
            // первому блоку на листе, а раздел в колонках после непрерывного разрыва
            // начинается посреди листа: по разделу листа линейка показывала колонки
            // раздела над ним (обычно одну) и промежутков не было, пока каретка в
            // колонках. Word показывает их, как только каретка входит в раздел.
            // Каретка в разделе листа — как раньше: раздела или документа, на прежнем
            // листе — прежние.
            var caretBreak = CaretSectionBreak(document, out bool caretFound);
            bool caretOtherSection = info is not null && caretFound && !ReferenceEquals(caretBreak, info.Break);
            var columns = caretOtherSection
                ? (caretBreak is not null ? ColumnFrames.ColumnsOf(document, caretBreak) : ColumnFrames.FinalColumns(document))
                : previousSheet && info!.Layout!.Columns is { } previousColumns
                ? previousColumns
                : info?.Break is { } sectionBreak
                ? ColumnFrames.ColumnsOf(document, sectionBreak)
                : ColumnFrames.FinalColumns(document);

            double textWidthMm = page.GetPhysicalWidthMm() - page.MarginLeftMm - page.MarginGutterMm - page.MarginRightMm;
            var (offsets, widths) = ColumnFrames.Compute(columns, MmToPt(Math.Max(textWidthMm, 1)));
            var gaps = new List<(double FromMm, double ToMm)>();
            for (int c = 0; c + 1 < offsets.Length; c++)
                gaps.Add(((offsets[c] + widths[c]) * 25.4 / 72.0, offsets[c + 1] * 25.4 / 72.0));

            // Ключ — значения, а не объект листа: поля раздела меняются на месте.
            // Лист с переплётом справа (GutterSide): на линейке поля так, как они стоят на
            // этом листе. При зеркальных полях они меняются местами, иначе к правому
            // полю прибавляется переплёт.
            bool shifted = pageIndex >= 0 && pageIndex < _pages.Count && _pages[pageIndex].GutterShiftPt != 0f;
            bool mirrored = shifted && document.PageSettings.MirrorMargins;
            double rightGutterMm = shifted && !mirrored ? page.MarginGutterMm : 0.0;

            double shownLeftMm = mirrored ? page.MarginRightMm
                : rightGutterMm > 0 ? page.MarginLeftMm
                : page.MarginLeftMm + page.MarginGutterMm;
            double shownRightMm = mirrored ? page.MarginLeftMm + page.MarginGutterMm
                : page.MarginRightMm + rightGutterMm;

            string gapsKey = string.Join(";", gaps) + "|" + page.GetPhysicalWidthMm() + "x" + page.GetPhysicalHeightMm()
                + "|" + page.MarginLeftMm + "," + page.MarginGutterMm + "," + page.MarginRightMm
                + "," + page.MarginTopMm + "," + page.MarginBottomMm
                + "|" + mirrored + "," + rightGutterMm;
            if (_lastRulerSheet is { } last && ReferenceEquals(last.Section, section)
                && ReferenceEquals(last.Page, page) && last.Gaps == gapsKey)
                return;
            _lastRulerSheet = (section, page, gapsKey);

            RulerSheetChanged.Invoke(new ViewModels.Components.RulerSheet(
                section,
                page.GetPhysicalWidthMm(), page.GetPhysicalHeightMm(),
                shownLeftMm, shownRightMm,
                page.MarginTopMm, page.MarginBottomMm,
                gaps,
                mirrored,
                rightGutterMm));
        }

        /// <summary>
        /// Разрыв, которым кончается раздел абзаца под кареткой; null — последний раздел
        /// или каретки нет. <paramref name="found"/> — блок каретки найден в потоке
        /// документа (у абзаца во вложенной таблице его там нет).
        /// </summary>
        private BreakBlock? CaretSectionBreak(DocumentModel document, out bool found)
        {
            found = false;
            if (_caretPara < 0 || _caretPara >= _layouts.Count || document.Sections.Count == 0) return null;

            var pl = _layouts[_caretPara];
            BlockModel? block = pl.Cell is { } cell ? cell.Table : pl.Vm?.Model;
            if (block is null) return null;

            var blocks = document.Sections[0].Blocks;
            int index = 0;
            while (index < blocks.Count && !ReferenceEquals(blocks[index], block)) index++;
            if (index == blocks.Count) return null;

            found = true;
            for (; index < blocks.Count; index++)
                if (blocks[index] is BreakBlock { IsSectionBreak: true } sectionBreak)
                    return sectionBreak;

            return null;
        }

        /// <summary>
        /// Лист и поля раздела, если лист живёт не по параметрам страницы документа;
        /// null — лист документа.
        /// </summary>
        private TextEditorPageSettings? SectionSheetOfPage(int pageIndex)
        {
            if (!SectionSheetsApply || DocVm is null) return null;

            List<ParaLayout> layouts;
            List<TableEntry> tables;
            int pageCount;
            lock (_renderLock) { layouts = _layouts; tables = _tables; pageCount = _pages.Count; }

            if (!_allPageSectionsReady || (_allPageSections is not null && _allPageSections.Length != pageCount))
            {
                _allPageSections = BuildPageSections(layouts, tables, pageCount, layout => layout?.Page is not null);
                _allPageSectionsReady = true;
            }

            if (_allPageSections is null || pageIndex < 0 || pageIndex >= _allPageSections.Length) return null;

            var page = _allPageSections[pageIndex].Page;
            return ReferenceEquals(page, DocVm.Document.PageSettings) ? null : page;
        }

        private static bool HasPageDecor(SectionLayout? layout)
            => layout is not null && (layout.PageBorders is not null || layout.LineNumbering is not null);

        private static int SectionOfBlock(List<(int FirstBlock, SectionLayout? Layout, TextEditorPageSettings Page)> sections, int block)
        {
            int index = 0;
            for (int s = 1; s < sections.Count; s++)
            {
                if (sections[s].FirstBlock > block) break;
                index = s;
            }
            return index;
        }

        // ── Выравнивание по высоте листа ──────────────────────────────────

        /// <summary>
        /// Выравнивание текста раздела по высоте листа (w:vAlign): по центру или по
        /// низу — текст листа опускается так, чтобы встать посередине или у нижнего поля
        /// места под текст. Расчёт идёт от места, а не от прежнего сдвига, поэтому
        /// повторный проход над уже выровненным листом ничего не меняет.
        ///
        /// Выравниваются листы из одних абзацев (с картинками в строке): таблицы,
        /// плавающие картинки и сноски держат свои места сами, и лист с ними остаётся
        /// как есть. «По высоте» (justify) у Word растягивает промежутки — здесь такой
        /// лист стоит сверху.
        /// </summary>
        private List<ParaLayout> AlignSectionPagesVertically(
            List<ParaLayout> layouts, List<PageRect> pages, List<TableEntry> tables,
            List<ImageEntry> images, List<PageBreakMark> breakMarks, Func<int, bool> pageHasNotes)
        {
            if (!SectionSheetsApply || pages.Count == 0) return layouts;

            var info = BuildPageSections(layouts, tables, pages.Count, layout =>
                layout?.VerticalAlignment is SectionVerticalAlignment.Center or SectionVerticalAlignment.Bottom);
            if (info is null) return layouts;

            var blocked = new bool[pages.Count];
            foreach (var table in tables)
                if (table.PageIndex >= 0 && table.PageIndex < blocked.Length) blocked[table.PageIndex] = true;
            foreach (var image in images)
                if (!image.InLine && image.PageIndex >= 0 && image.PageIndex < blocked.Length) blocked[image.PageIndex] = true;

            var top = new float[pages.Count];
            var bottom = new float[pages.Count];
            Array.Fill(top, float.MaxValue);
            Array.Fill(bottom, float.MinValue);

            foreach (var pl in layouts)
            {
                if (pl.PageIndex < 0 || pl.PageIndex >= pages.Count) continue;
                if (pl.Cell is not null) { blocked[pl.PageIndex] = true; continue; }
                top[pl.PageIndex] = Math.Min(top[pl.PageIndex], pl.Ypt);
                bottom[pl.PageIndex] = Math.Max(bottom[pl.PageIndex], pl.Ypt + pl.HeightPt);
            }

            var shift = new float[pages.Count];
            bool any = false;
            for (int p = 0; p < pages.Count; p++)
            {
                var align = info[p].Layout?.VerticalAlignment ?? SectionVerticalAlignment.Top;
                if (align is not (SectionVerticalAlignment.Center or SectionVerticalAlignment.Bottom)) continue;
                if (blocked[p] || top[p] == float.MaxValue || pageHasNotes(p)) continue;

                var page = pages[p];
                float areaTop = page.Ypt + page.PadTopPt;
                float areaBottom = page.Ypt + page.HeightPt - page.PadBottomPt;
                float height = bottom[p] - top[p];
                float free = areaBottom - areaTop - height;
                if (free <= 0.5f) continue;

                float wantedTop = areaTop + (align == SectionVerticalAlignment.Center ? free / 2f : free);
                float delta = wantedTop - top[p];
                if (Math.Abs(delta) < 0.05f) continue;

                shift[p] = delta;
                any = true;
            }

            if (!any) return layouts;

            var result = new List<ParaLayout>(layouts.Count);
            foreach (var pl in layouts)
            {
                bool moved = pl.PageIndex >= 0 && pl.PageIndex < shift.Length && shift[pl.PageIndex] != 0f;
                result.Add(moved ? pl with { Ypt = pl.Ypt + shift[pl.PageIndex] } : pl);
            }

            for (int i = 0; i < images.Count; i++)
            {
                var image = images[i];
                if (!image.InLine || image.PageIndex < 0 || image.PageIndex >= shift.Length) continue;
                if (shift[image.PageIndex] == 0f) continue;
                images[i] = image with { Ypt = image.Ypt + shift[image.PageIndex] };
            }

            // Отметки разрывов идут вместе с текстом листа: разрыв раздела в конце
            // выровненного листа стоит под его последним абзацем, а не там, где
            // кончился бы текст, прижатый к верху.
            for (int i = 0; i < breakMarks.Count; i++)
            {
                var mark = breakMarks[i];
                if (mark.PageIndex < 0 || mark.PageIndex >= shift.Length) continue;
                if (shift[mark.PageIndex] == 0f) continue;
                breakMarks[i] = mark with { Ypt = mark.Ypt + shift[mark.PageIndex] };
            }

            return result;
        }


        // ── Рамка страницы ────────────────────────────────────────────────

        /// <summary>
        /// Рамки страниц листов [first..last]. <paramref name="behind"/> — проход под
        /// текстом: рисуются рамки «за текстом», иначе — остальные.
        /// </summary>
        private void DrawPageBorders(
            SKCanvas canvas, List<ParaLayout> layouts, List<TableEntry> tables, List<PageRect> pages,
            int firstPage, int lastPage, bool behind)
        {
            if (ReadingActive || pages.Count == 0) return;
            var info = PageSections(layouts, tables, pages.Count);
            if (info is null || info.Length != pages.Count) return;

            for (int p = Math.Max(firstPage, 0); p <= lastPage && p < pages.Count; p++)
            {
                var section = info[p];
                if (section.Layout?.PageBorders is not { } borders) continue;
                if (borders.Behind != behind) continue;

                bool show = borders.Display switch
                {
                    PageBorderDisplay.FirstPage => section.FirstPageOfSection,
                    PageBorderDisplay.NotFirstPage => !section.FirstPageOfSection,
                    _ => true
                };
                if (!show) continue;

                var page = pages[p];
                var ps = section.Page;

                float left = page.PadLeftPt;
                float top = page.Ypt;
                float right = page.PadLeftPt + page.WidthPt;
                float bottom = page.Ypt + page.HeightPt;

                if (!borders.FromPageEdge)
                {
                    // От текста: линии стоят снаружи текстовой области на свой отступ.
                    left = page.PadLeftPt + page.MarginLeftPt;
                    right = page.PadLeftPt + page.WidthPt - (MmToPt(ps.MarginRightMm) - page.GutterShiftPt);
                    top = page.Ypt + MmToPt(ps.MarginTopMm);
                    bottom = page.Ypt + page.HeightPt - MmToPt(ps.MarginBottomMm);
                }

                PageBorderPainter.DrawFrame(canvas, borders, left, top, right, bottom, screenInk: true);
            }
        }

        // ── Номера строк ──────────────────────────────────────────────────

        /// <summary>
        /// Номера строк разделов с w:lnNumType — у левого края текста, на базовой линии
        /// строки. Нумеруются строки абзацев потока; ячейки таблиц, как у Word, нет.
        /// Счёт начинается заново по листу, по разделу или идёт сквозной.
        /// </summary>
        private void DrawLineNumbers(
            SKCanvas canvas, List<ParaLayout> layouts, List<TableEntry> tables, List<PageRect> pages,
            int firstPage, int lastPage)
        {
            if (ReadingActive || pages.Count == 0) return;
            var info = PageSections(layouts, tables, pages.Count);
            if (info is null || info.Length != pages.Count) return;

            bool any = false;
            foreach (var page in info)
                if (page.Layout?.LineNumbering is not null) { any = true; break; }
            if (!any) return;

            using var font = new SKFont(SKTextRenderer.ResolveTypeface(HeaderFooterFallbackFamily(), false, false), LineNumberFontSizePt);
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
                Color = SKTextRenderer.ResolveInk(new SKColor(0x40, 0x40, 0x40))
            };

            // Счёт идёт от начала документа: номер строки на листе зависит от всех строк
            // раздела выше, даже если рисуется только видимая часть.
            int counter = 0;
            int currentPage = -1;
            int currentSection = -1;
            bool previousNumbered = false;

            foreach (var pl in layouts)
            {
                if (pl.Cell is not null || pl.Layout is not { } sl) continue;
                if (pl.PageIndex < 0 || pl.PageIndex >= info.Length) continue;

                var section = info[pl.PageIndex];
                bool newSection = section.SectionIndex != currentSection;
                bool newPage = pl.PageIndex != currentPage;
                currentPage = pl.PageIndex;
                currentSection = section.SectionIndex;

                if (section.Layout?.LineNumbering is not { } numbering)
                {
                    previousNumbered = false;
                    continue;
                }

                // Сквозной счёт продолжает предыдущий раздел с номерами строк; иначе
                // раздел начинает счёт со своего начала, а «по листу» — и каждый лист.
                if (newSection)
                {
                    if (!(numbering.Restart == LineNumberRestart.Continuous && previousNumbered))
                        counter = Math.Max(numbering.Start, 1) - 1;
                }
                else if (newPage && numbering.Restart == LineNumberRestart.NewPage)
                {
                    counter = Math.Max(numbering.Start, 1) - 1;
                }

                previousNumbered = true;

                bool visible = pl.PageIndex >= firstPage && pl.PageIndex <= lastPage;
                int countBy = Math.Max(numbering.CountBy, 1);
                float distance = numbering.DistanceMm > 0 ? MmToPt(numbering.DistanceMm) : LineNumberDefaultDistancePt;

                float yBase = pl.LineFrom < sl.Lines.Count ? sl.Lines[pl.LineFrom].Y : 0f;
                for (int li = pl.LineFrom; li < pl.LineTo && li < sl.Lines.Count; li++)
                {
                    counter++;
                    if (!visible || counter % countBy != 0) continue;

                    var line = sl.Lines[li];
                    float baseline = pl.Ypt + (line.Y - yBase) + line.Baseline;
                    string text = counter.ToString(CultureInfo.InvariantCulture);
                    float width = font.MeasureText(text);
                    canvas.DrawText(text, pl.AbsXPt - distance - width, baseline, SKTextAlign.Left, font, paint);
                }
            }
        }
    }
}
