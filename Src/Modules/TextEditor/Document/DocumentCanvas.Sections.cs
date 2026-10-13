using System;
using System.Collections.Generic;
using Writersword.Core.Models.Print;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Page;
using Writersword.Modules.TextEditor.Rendering;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Разделы документа на листах: у каждого раздела свой лист и свои поля, как у
    /// Word. Раздел кончается разрывом раздела с его параметрами
    /// (<see cref="BreakBlock.Section"/>); последний раздел живёт по параметрам
    /// страницы документа.
    ///
    /// Раскладка листов (RebuildPageModePass) берёт лист раздела на каждом разрыве,
    /// который начинает новую страницу; непрерывный разрыв меняет поля слева и справа
    /// на том же листе, а другой размер листа и у него начинает новый лист — так
    /// делает и Word. В книге и в чтении у листа свой размер, и разделы там лист не
    /// меняют, а колонок нет.
    ///
    /// Колонки раздела (DocumentCanvas.Columns) — тоже часть его листа: где стоит каждая
    /// колонка по ширине текстовой области и какой она ширины.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        /// <summary>
        /// Лист раздела: размер и поля в пунктах (левое — вместе с переплётом), колонки —
        /// левые края от края текстовой области и ширины.
        /// </summary>
        private readonly record struct SectionGeometry(
            float PageWidthPt,
            float PageHeightPt,
            float LeftPt,
            float TopPt,
            float RightPt,
            float BottomPt,
            float[] ColumnOffsetsPt,
            float[] ColumnWidthsPt,
            bool ColumnSeparator)
        {
            public float TextWidthPt => Math.Max(PageWidthPt - LeftPt - RightPt, 1f);

            public int ColumnCount => ColumnOffsetsPt.Length;

            public bool SameSheet(SectionGeometry other)
                => Math.Abs(PageWidthPt - other.PageWidthPt) < 0.5f
                   && Math.Abs(PageHeightPt - other.PageHeightPt) < 0.5f;
        }

        // Ширина, по которой раскладка листов сверстала абзац, когда она не та, что у
        // листа документа: раздел альбомный, со своими полями или в колонках. Нужна
        // быстрой перевёрстке абзаца при наборе: иначе она мерила бы абзац по ширине
        // листа документа, и абзац в колонке на миг растягивался бы на весь лист.
        private Dictionary<BlockModel, float> _sectionTextWidths = new();

        // Самый широкий лист документа: холст должен вмещать альбомный лист раздела.
        private float _widestSectionPageWidthPt;

        /// <summary>Листы разделы задают только на листах правки.</summary>
        private bool SectionSheetsApply => !SpreadMode && !ReadingActive && DocVm?.ViewMode == EditorViewMode.Page;

        /// <summary>
        /// Лист раздела по его параметрам страницы (null — лист документа) и его колонки
        /// (null — одна колонка).
        /// </summary>
        private SectionGeometry GeometryOf(TextEditorPageSettings? page, SectionColumns? columns)
        {
            float width, height, left, top, right, bottom;

            if (page is null || !SectionSheetsApply)
            {
                (left, top, right, bottom) = GetPagePaddingPt();
                width = GetPageWidthPt();
                height = GetPageHeightPt();
            }
            else
            {
                bool landscape = page.Orientation == PageOrientation.Landscape;
                width = MmToPt(landscape ? page.HeightMm : page.WidthMm);
                height = MmToPt(landscape ? page.WidthMm : page.HeightMm);
                left = MmToPt(page.MarginLeftMm + page.MarginGutterMm);
                top = MmToPt(page.MarginTopMm);
                right = MmToPt(page.MarginRightMm);
                bottom = MmToPt(page.MarginBottomMm);
            }

            float textWidth = Math.Max(width - left - right, 1f);
            var (offsets, widths) = ColumnFrames.Compute(SectionSheetsApply ? columns : null, textWidth);
            bool separator = SectionSheetsApply && columns is { Separator: true } && offsets.Length > 1;

            return new SectionGeometry(width, height, left, top, right, bottom, offsets, widths, separator);
        }

        /// <summary>
        /// Разделы потока: с какого блока начинается каждый и какой у него лист.
        /// Последний раздел — по параметрам страницы документа.
        /// </summary>
        private List<(int FirstBlock, SectionGeometry Geometry)> BuildSectionGeometries(IReadOnlyList<BlockModel> blocks)
        {
            var sections = new List<(int, SectionGeometry)>();
            var document = DocVm!.Document;

            var documentGeometry = GeometryOf(null, ColumnFrames.FinalColumns(document));
            float widest = documentGeometry.PageWidthPt;

            int start = 0;
            for (int i = 0; i < blocks.Count; i++)
            {
                if (blocks[i] is not BreakBlock { IsSectionBreak: true } sectionBreak) continue;

                var geometry = GeometryOf(sectionBreak.Section?.Page, ColumnFrames.ColumnsOf(document, sectionBreak));
                sections.Add((start, geometry));
                widest = Math.Max(widest, geometry.PageWidthPt);

                start = i + 1;
            }

            sections.Add((start, documentGeometry));

            _widestSectionPageWidthPt = widest;
            return sections;
        }

        /// <summary>Номер раздела, в который входит блок с этим индексом.</summary>
        private static int SectionIndexOf(List<(int FirstBlock, SectionGeometry Geometry)> sections, int blockIndex)
        {
            int index = 0;
            for (int s = 1; s < sections.Count; s++)
            {
                if (sections[s].FirstBlock > blockIndex) break;
                index = s;
            }
            return index;
        }

        /// <summary>
        /// Ширина текста для абзаца: его раздела, если раздел живёт не по листу
        /// документа, иначе — листа документа.
        /// </summary>
        private float TextWidthForBlock(BlockModel? block)
        {
            if (block is not null && SectionSheetsApply && _sectionTextWidths.TryGetValue(block, out float width))
                return width;
            return GetCurrentTextWidthPt();
        }

        /// <summary>Самый широкий лист документа — для ширины холста.</summary>
        private float WidestPageWidthPt()
        {
            float own = GetPageWidthPt();
            return SectionSheetsApply ? Math.Max(own, _widestSectionPageWidthPt) : own;
        }
    }
}
