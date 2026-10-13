using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DocumentFormat.OpenXml;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Page;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Разделы в .docx: w:sectPr каждого внутреннего раздела — у последнего абзаца
    /// раздела, последнего — в конце тела. Раздел в модели кончается разрывом раздела
    /// со своими параметрами (BreakBlock.Section); тип w:type пишется разделу по тому,
    /// как он начинается, — это вид разрыва перед ним.
    /// </summary>
    public sealed partial class ExportService
    {
        /// <summary>Как начинается раздел за разрывом этого вида.</summary>
        private static SectionStart StartOf(BreakType type) => type switch
        {
            BreakType.SectionContinuous => SectionStart.Continuous,
            BreakType.SectionEvenPage => SectionStart.EvenPage,
            BreakType.SectionOddPage => SectionStart.OddPage,
            BreakType.SectionNextColumn => SectionStart.NextColumn,
            _ => SectionStart.NextPage
        };

        /// <summary>Начало первого раздела документа: так, как его записал файл.</summary>
        private static SectionStart FirstSectionStart(DocumentModel document, SectionModel section)
        {
            foreach (var block in section.Blocks)
                if (block is BreakBlock { IsSectionBreak: true } first)
                    return first.Section?.Start ?? SectionStart.NextPage;

            return document.FinalSection?.Start ?? SectionStart.NextPage;
        }

        /// <summary>
        /// w:sectPr раздела: начало, лист, поля, рамка, номера строк, счёт страниц,
        /// колонки, выравнивание по высоте, особая первая. Ссылки на колонтитулы
        /// дописываются потом (ApplyHeaderFooterToSection), порядок выправляет
        /// <see cref="NormalizeSectionProperties"/>.
        /// </summary>
        private static W.SectionProperties BuildSectionLayoutProperties(
            SectionLayout? layout, TextEditorPageSettings page, SectionColumns columns, SectionStart start)
        {
            var result = new W.SectionProperties();

            result.AppendChild(new W.SectionType { Val = ToOoxml(start) });

            bool landscape = page.Orientation == Core.Models.Print.PageOrientation.Landscape;
            var pageSize = new W.PageSize
            {
                Width = (UInt32Value)(uint)Math.Round(page.GetPhysicalWidthMm() * TwipsPerMm),
                Height = (UInt32Value)(uint)Math.Round(page.GetPhysicalHeightMm() * TwipsPerMm)
            };
            if (landscape)
                pageSize.Orient = new EnumValue<W.PageOrientationValues>(W.PageOrientationValues.Landscape);
            result.AppendChild(pageSize);

            result.AppendChild(new W.PageMargin
            {
                Top = (int)Math.Round(page.MarginTopMm * TwipsPerMm),
                Bottom = (int)Math.Round(page.MarginBottomMm * TwipsPerMm),
                Left = (UInt32Value)(uint)Math.Round(page.MarginLeftMm * TwipsPerMm),
                Right = (UInt32Value)(uint)Math.Round(page.MarginRightMm * TwipsPerMm),
                Gutter = (UInt32Value)(uint)Math.Round(page.MarginGutterMm * TwipsPerMm),
                Header = (UInt32Value)(uint)Math.Round(page.HeaderDistanceMm * TwipsPerMm),
                Footer = (UInt32Value)(uint)Math.Round(page.FooterDistanceMm * TwipsPerMm)
            });

            if (layout?.PageBorders is { } borders)
                result.AppendChild(BuildPageBorders(borders));

            if (layout?.LineNumbering is { } lines)
            {
                var lnNum = new W.LineNumberType
                {
                    CountBy = (Int16Value)(short)Math.Clamp(lines.CountBy, 1, short.MaxValue),
                    Start = (Int16Value)(short)Math.Clamp(lines.Start - 1, 0, short.MaxValue)
                };
                if (lines.DistanceMm > 0)
                    lnNum.Distance = Math.Round(lines.DistanceMm * TwipsPerMm).ToString(CultureInfo.InvariantCulture);
                if (lines.Restart == LineNumberRestart.NewSection)
                    lnNum.Restart = W.LineNumberRestartValues.NewSection;
                else if (lines.Restart == LineNumberRestart.Continuous)
                    lnNum.Restart = W.LineNumberRestartValues.Continuous;
                result.AppendChild(lnNum);
            }

            if (layout is not null && (layout.PageNumberStart is not null || layout.PageNumberFormat is not null))
            {
                var pageNumber = new W.PageNumberType();
                if (layout.PageNumberStart is int numberStart) pageNumber.Start = numberStart;
                if (layout.PageNumberFormat is { } numberFormat && FormatToOoxml(numberFormat) is { } ooxml)
                    pageNumber.Format = ooxml;
                result.AppendChild(pageNumber);
            }

            result.AppendChild(BuildColumns(columns));

            if (layout is not null && layout.VerticalAlignment != SectionVerticalAlignment.Top)
            {
                result.AppendChild(new W.VerticalTextAlignmentOnPage
                {
                    Val = layout.VerticalAlignment switch
                    {
                        SectionVerticalAlignment.Center => W.VerticalJustificationValues.Center,
                        SectionVerticalAlignment.Bottom => W.VerticalJustificationValues.Bottom,
                        _ => W.VerticalJustificationValues.Both
                    }
                });
            }

            if (layout?.TitlePage == true)
                result.AppendChild(new W.TitlePage());

            return result;
        }

        private static W.SectionMarkValues ToOoxml(SectionStart start) => start switch
        {
            SectionStart.Continuous => W.SectionMarkValues.Continuous,
            SectionStart.EvenPage => W.SectionMarkValues.EvenPage,
            SectionStart.OddPage => W.SectionMarkValues.OddPage,
            SectionStart.NextColumn => W.SectionMarkValues.NextColumn,
            _ => W.SectionMarkValues.NextPage
        };

        private static W.Columns BuildColumns(SectionColumns columns)
        {
            var result = new W.Columns
            {
                // w:num в SDK типизирован как Int16Value — приведение обязательно.
                ColumnCount = (Int16Value)(short)Math.Clamp(columns.Count, 1, (int)short.MaxValue),
                Space = Math.Round(columns.GapMm * TwipsPerMm).ToString(CultureInfo.InvariantCulture)
            };

            if (columns.Count > 1 && columns.Separator)
                result.Separator = true;

            if (columns.Columns is { Count: > 0 } own)
            {
                result.EqualWidth = false;
                result.ColumnCount = (Int16Value)(short)Math.Clamp(own.Count, 1, (int)short.MaxValue);
                foreach (var column in own)
                {
                    result.AppendChild(new W.Column
                    {
                        Width = Math.Round(column.WidthMm * TwipsPerMm).ToString(CultureInfo.InvariantCulture),
                        Space = Math.Round(column.SpaceAfterMm * TwipsPerMm).ToString(CultureInfo.InvariantCulture)
                    });
                }
            }

            return result;
        }

        private static W.PageBorders BuildPageBorders(SectionPageBorders borders)
        {
            var result = new W.PageBorders();

            if (borders.FromPageEdge) result.OffsetFrom = W.PageBorderOffsetValues.Page;
            if (borders.Behind) result.ZOrder = W.PageBorderZOrderValues.Back;
            if (borders.Display == PageBorderDisplay.FirstPage) result.Display = W.PageBorderDisplayValues.FirstPage;
            else if (borders.Display == PageBorderDisplay.NotFirstPage) result.Display = W.PageBorderDisplayValues.NotFirstPage;

            if (borders.Top is { } top) result.TopBorder = FillBorder(new W.TopBorder(), top);
            if (borders.Left is { } left) result.LeftBorder = FillBorder(new W.LeftBorder(), left);
            if (borders.Bottom is { } bottom) result.BottomBorder = FillBorder(new W.BottomBorder(), bottom);
            if (borders.Right is { } right) result.RightBorder = FillBorder(new W.RightBorder(), right);

            return result;
        }

        private static T FillBorder<T>(T border, PageBorderSide side) where T : W.BorderType
        {
            border.Val = new EnumValue<W.BorderValues>(new W.BorderValues(side.Style));
            border.Size = (UInt32Value)(uint)Math.Clamp(Math.Round(side.WidthPt * 8), 2, 96);
            border.Space = (UInt32Value)(uint)Math.Clamp(Math.Round(side.SpacePt), 0, 31);
            border.Color = string.IsNullOrEmpty(side.Color) ? "auto" : side.Color!.TrimStart('#');
            if (side.Shadow) border.Shadow = true;
            return border;
        }

        // Порядок детей w:sectPr по схеме (CT_SectPr).
        private static readonly Type[] SectPrOrder =
        {
            typeof(W.FootnoteProperties), typeof(W.EndnoteProperties), typeof(W.SectionType),
            typeof(W.PageSize), typeof(W.PageMargin), typeof(W.PaperSource), typeof(W.PageBorders),
            typeof(W.LineNumberType), typeof(W.PageNumberType), typeof(W.Columns), typeof(W.FormProtection),
            typeof(W.VerticalTextAlignmentOnPage), typeof(W.NoEndnote), typeof(W.TitlePage),
            typeof(W.TextDirection), typeof(W.BiDi), typeof(W.GutterOnRight), typeof(W.DocGrid),
            typeof(W.PrinterSettingsReference), typeof(W.SectionPropertiesChange)
        };

        /// <summary>
        /// Выправляет w:sectPr после того, как колонтитулы дописали свои ссылки, счёт и
        /// «особую первую»: одиночные элементы — по одному (последний записанный, у
        /// колонтитулов он свежее), порядок — по схеме. Word файл с другим порядком
        /// считает повреждённым.
        /// </summary>
        private static void NormalizeSectionProperties(W.SectionProperties sectPr)
        {
            var children = sectPr.ChildElements.ToList();
            var keep = new List<OpenXmlElement>(children.Count);

            for (int i = 0; i < children.Count; i++)
            {
                var child = children[i];
                bool single = child is not (W.HeaderReference or W.FooterReference);
                if (single && children.Skip(i + 1).Any(c => c.GetType() == child.GetType()))
                {
                    // Счёт страниц из раздела и из колонтитулов сливается: начало и вид
                    // берутся у того, у кого они есть.
                    if (child is W.PageNumberType mine
                        && children.Skip(i + 1).OfType<W.PageNumberType>().LastOrDefault() is { } later)
                    {
                        later.Start ??= mine.Start;
                        later.Format ??= mine.Format;
                    }
                    continue;
                }
                keep.Add(child);
            }

            int Rank(OpenXmlElement element)
            {
                if (element is W.HeaderReference or W.FooterReference) return -1;
                int index = Array.IndexOf(SectPrOrder, element.GetType());
                return index < 0 ? SectPrOrder.Length : index;
            }

            var ordered = keep
                .Select((element, index) => (element, index))
                .OrderBy(pair => Rank(pair.element))
                .ThenBy(pair => pair.index)
                .Select(pair => pair.element)
                .ToList();

            sectPr.RemoveAllChildren();
            foreach (var element in ordered) sectPr.AppendChild(element);
        }

        /// <summary>Колонки раздела в модели разделов из колонок документа.</summary>
        private static SectionColumns ToSectionColumns(ColumnSettings columns) => new()
        {
            Count = Math.Max(1, columns.ColumnCount),
            GapMm = columns.GapMm,
            Separator = columns.ShowSeparator
        };
    }
}
