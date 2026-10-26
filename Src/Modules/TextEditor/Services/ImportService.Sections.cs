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
    /// Разделы Word. Каждый раздел описан w:sectPr у последнего абзаца раздела, а
    /// последний — w:sectPr в конце тела. У нас раздел кончается разрывом раздела
    /// (BreakBlock с параметрами своего раздела); тип разрыва — то, как начинается
    /// следующий раздел: w:type у Word принадлежит разделу, который им начинается.
    /// Последний раздел описывают параметры страницы и колонки документа и
    /// DocumentModel.FinalSection.
    /// </summary>
    public sealed partial class ImportService
    {
        /// <summary>Зеркальные поля документа (w:mirrorMargins в настройках Word).</summary>
        private static bool ReadMirrorMargins(DocumentFormat.OpenXml.Packaging.MainDocumentPart mainPart)
        {
            var mirror = mainPart.DocumentSettingsPart?.Settings?.GetFirstChild<W.MirrorMargins>();
            return mirror is not null && (mirror.Val is null || mirror.Val.Value);
        }

        // Параметры всех разделов документа по порядку: внутренние, затем последний.
        private readonly List<W.SectionProperties?> _documentSections = new();

        // Сколько внутренних разрывов раздела уже встретилось при разборе тела.
        private int _sectionOrdinal;

        /// <summary>Собирает параметры всех разделов тела до разбора абзацев.</summary>
        private void CollectDocumentSections(W.Body body)
        {
            _documentSections.Clear();
            _sectionOrdinal = 0;

            foreach (var paragraph in body.Descendants<W.Paragraph>())
                if (paragraph.ParagraphProperties?.SectionProperties is { } own)
                    _documentSections.Add(own);

            _documentSections.Add(body.GetFirstChild<W.SectionProperties>());
        }

        /// <summary>
        /// Разрыв раздела, которым кончается раздел с этими параметрами: тип — по началу
        /// следующего раздела, параметры — своего.
        /// </summary>
        private BreakBlock BuildSectionBreak(W.SectionProperties sectPr)
        {
            int next = _sectionOrdinal + 1;
            _sectionOrdinal++;

            var nextSectPr = next < _documentSections.Count ? _documentSections[next] : null;
            var nextStart = ReadSectionStart(nextSectPr);

            var sectionEnd = new BreakBlock
            {
                BreakType = BreakTypeOf(nextStart),
                Section = ReadSectionLayout(sectPr, withPage: true)
            };

            // Смена параметров раздела правкой (w:sectPrChange).
            if (ReadPreviousSection(sectPr) is { } previous)
            {
                var layout = sectionEnd.Section;
                string now = SectionFrame.CaptureLayout(layout, layout.Page!, layout.Columns!);
                string before = SectionFrame.CaptureLayout(
                    previous.Layout, previous.Layout.Page!, previous.Layout.Columns!);
                if (SectionFrame.Differs(now, before, forFile: true))
                    layout.FormatChange = new ObjectChange { Info = previous.Info, Previous = before };
            }

            return sectionEnd;
        }

        /// <summary>
        /// Прежние параметры раздела из w:sectPrChange с автором и временем правки;
        /// null — правки параметров нет.
        /// </summary>
        private static (SectionLayout Layout, Models.Inline.RevisionInfo Info)? ReadPreviousSection(W.SectionProperties sectPr)
        {
            if (sectPr.GetFirstChild<W.SectionPropertiesChange>() is not { } change) return null;
            if (change.GetFirstChild<W.PreviousSectionProperties>() is not { } before) return null;

            var previous = new W.SectionProperties();
            foreach (var child in before.ChildElements)
                previous.AppendChild(child.CloneNode(true));

            return (ReadSectionLayout(previous, withPage: true), ReadRevisionInfo(change));
        }

        /// <summary>
        /// Смена параметров последнего раздела правкой (w:sectPrChange у w:sectPr тела)
        /// против параметров документа, уже прочитанных из файла; null — правки нет.
        /// </summary>
        private static ObjectChange? ReadFinalSectionChange(W.Body body, DocumentModel doc)
        {
            if (body.GetFirstChild<W.SectionProperties>() is not { } sectPr) return null;
            if (ReadPreviousSection(sectPr) is not { } previous) return null;

            string now = SectionFrame.CaptureFinal(doc);
            string before = SectionFrame.CaptureLayout(previous.Layout, previous.Layout.Page!, previous.Layout.Columns!);
            return SectionFrame.Differs(now, before, forFile: true)
                ? new ObjectChange { Info = previous.Info, Previous = before }
                : null;
        }

        /// <summary>Тип разрыва раздела по тому, как начинается следующий раздел.</summary>
        internal static BreakType BreakTypeOf(SectionStart start) => start switch
        {
            SectionStart.Continuous => BreakType.SectionContinuous,
            SectionStart.EvenPage => BreakType.SectionEvenPage,
            SectionStart.OddPage => BreakType.SectionOddPage,
            SectionStart.NextColumn => BreakType.SectionNextColumn,
            _ => BreakType.SectionNextPage
        };

        /// <summary>Начало раздела (w:type); без него Word начинает раздел со следующей страницы.</summary>
        private static SectionStart ReadSectionStart(W.SectionProperties? sectPr)
        {
            var type = sectPr?.GetFirstChild<W.SectionType>()?.Val;
            if (type is null || !type.HasValue) return SectionStart.NextPage;

            var value = type.Value;
            if (value == W.SectionMarkValues.Continuous) return SectionStart.Continuous;
            if (value == W.SectionMarkValues.EvenPage) return SectionStart.EvenPage;
            if (value == W.SectionMarkValues.OddPage) return SectionStart.OddPage;
            if (value == W.SectionMarkValues.NextColumn) return SectionStart.NextColumn;
            return SectionStart.NextPage;
        }

        /// <summary>
        /// Раздел Word в модели. withPage — читать и лист с колонками (у внутренних
        /// разделов); у последнего их несут параметры страницы и колонки документа.
        /// </summary>
        private static SectionLayout ReadSectionLayout(W.SectionProperties sectPr, bool withPage)
        {
            var layout = new SectionLayout
            {
                Start = ReadSectionStart(sectPr)
            };

            if (withPage)
                layout.Page = ReadSectionPage(sectPr);

            layout.Columns = ReadSectionColumns(sectPr);

            var titlePg = sectPr.GetFirstChild<W.TitlePage>();
            layout.TitlePage = titlePg is not null && (titlePg.Val is null || titlePg.Val.Value);

            var vAlign = sectPr.GetFirstChild<W.VerticalTextAlignmentOnPage>()?.Val;
            if (vAlign is not null && vAlign.HasValue)
            {
                var value = vAlign.Value;
                layout.VerticalAlignment = value == W.VerticalJustificationValues.Center ? SectionVerticalAlignment.Center
                    : value == W.VerticalJustificationValues.Bottom ? SectionVerticalAlignment.Bottom
                    : value == W.VerticalJustificationValues.Both ? SectionVerticalAlignment.Justify
                    : SectionVerticalAlignment.Top;
            }

            layout.PageBorders = ReadPageBorders(sectPr.GetFirstChild<W.PageBorders>());
            layout.LineNumbering = ReadLineNumbering(sectPr.GetFirstChild<W.LineNumberType>());

            var pgNum = sectPr.GetFirstChild<W.PageNumberType>();
            if (pgNum?.Start?.Value is int start) layout.PageNumberStart = start;
            if (pgNum?.Format is { HasValue: true } format)
                layout.PageNumberFormat = MapPageNumberFormat(format.Value);

            return layout;
        }

        /// <summary>
        /// Лист и поля раздела. Чего раздел не говорит, то берётся по умолчаниям Word,
        /// а не у соседнего раздела: так раздел без w:pgMar открывает и сам Word.
        /// </summary>
        private static TextEditorPageSettings ReadSectionPage(W.SectionProperties sectPr)
        {
            var page = new TextEditorPageSettings();
            page.ApplyPaperSize(Core.Models.Print.PaperSize.A4);
            page.Orientation = Core.Models.Print.PageOrientation.Portrait;
            page.MarginTopMm = 20;
            page.MarginBottomMm = 20;
            page.MarginLeftMm = 30;
            page.MarginRightMm = 15;
            page.MarginGutterMm = 0;
            page.HeaderDistanceMm = 12.5;
            page.FooterDistanceMm = 12.5;

            var pageSize = sectPr.GetFirstChild<W.PageSize>();
            if (pageSize is not null)
            {
                double widthMm = (pageSize.Width?.Value ?? 11906) / TwipsPerMm;
                double heightMm = (pageSize.Height?.Value ?? 16838) / TwipsPerMm;
                bool landscape = pageSize.Orient?.Value == W.PageOrientationValues.Landscape;

                // Ширина и высота листа хранятся книжными, ориентация — отдельно, как у
                // параметров страницы документа.
                page.PaperSize = Core.Models.Print.PaperSize.Custom;
                page.WidthMm = Math.Round(landscape ? heightMm : widthMm, 1);
                page.HeightMm = Math.Round(landscape ? widthMm : heightMm, 1);
                page.Orientation = landscape
                    ? Core.Models.Print.PageOrientation.Landscape
                    : Core.Models.Print.PageOrientation.Portrait;
            }

            var margin = sectPr.GetFirstChild<W.PageMargin>();
            if (margin is not null)
            {
                if (margin.Top is not null) page.MarginTopMm = Math.Round(Math.Abs(margin.Top.Value) / TwipsPerMm, 1);
                if (margin.Bottom is not null) page.MarginBottomMm = Math.Round(Math.Abs(margin.Bottom.Value) / TwipsPerMm, 1);
                if (margin.Left is not null) page.MarginLeftMm = Math.Round(margin.Left.Value / TwipsPerMm, 1);
                if (margin.Right is not null) page.MarginRightMm = Math.Round(margin.Right.Value / TwipsPerMm, 1);
                if (margin.Gutter is not null) page.MarginGutterMm = Math.Round(margin.Gutter.Value / TwipsPerMm, 1);
                if (margin.Header is not null) page.HeaderDistanceMm = Math.Round(margin.Header.Value / TwipsPerMm, 1);
                if (margin.Footer is not null) page.FooterDistanceMm = Math.Round(margin.Footer.Value / TwipsPerMm, 1);
            }

            return page;
        }

        /// <summary>
        /// Колонки раздела. Промежуток по умолчанию у Word — полдюйма (720 twips).
        /// Колонки разной ширины перечислены в w:col.
        /// </summary>
        private static SectionColumns ReadSectionColumns(W.SectionProperties sectPr)
        {
            var columns = new SectionColumns { Count = 1, GapMm = Math.Round(720 / TwipsPerMm, 1) };

            var cols = sectPr.GetFirstChild<W.Columns>();
            if (cols is null) return columns;

            var own = cols.Elements<W.Column>().ToList();
            int count = Math.Max((int)(cols.ColumnCount?.Value ?? 1), 1);

            if (TryTwips(cols.Space?.InnerText, out double spaceTwips))
                columns.GapMm = Math.Round(spaceTwips / TwipsPerMm, 2);

            columns.Separator = cols.Separator?.Value == true;
            columns.SeparatorColor = ExportService.ReadSeparatorColor(cols);

            bool equal = cols.EqualWidth is null || cols.EqualWidth.Value;
            if (!equal && own.Count > 0)
            {
                columns.Columns = new List<SectionColumn>(own.Count);
                foreach (var column in own)
                {
                    TryTwips(column.Width?.InnerText, out double widthTwips);
                    TryTwips(column.Space?.InnerText, out double afterTwips);
                    columns.Columns.Add(new SectionColumn
                    {
                        WidthMm = Math.Round(widthTwips / TwipsPerMm, 2),
                        SpaceAfterMm = Math.Round(afterTwips / TwipsPerMm, 2)
                    });
                }
                count = own.Count;
            }

            columns.Count = count;
            return columns;
        }

        private static bool TryTwips(string? raw, out double twips)
        {
            twips = 0;
            return !string.IsNullOrEmpty(raw)
                && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out twips);
        }

        /// <summary>Рамка страницы раздела (w:pgBorders); null — рамки нет.</summary>
        private static SectionPageBorders? ReadPageBorders(W.PageBorders? borders)
        {
            if (borders is null) return null;

            var result = new SectionPageBorders
            {
                FromPageEdge = borders.OffsetFrom?.Value == W.PageBorderOffsetValues.Page,
                Behind = borders.ZOrder?.Value == W.PageBorderZOrderValues.Back
            };

            var display = borders.Display?.Value;
            if (display == W.PageBorderDisplayValues.FirstPage) result.Display = PageBorderDisplay.FirstPage;
            else if (display == W.PageBorderDisplayValues.NotFirstPage) result.Display = PageBorderDisplay.NotFirstPage;

            result.Top = ReadBorderSide(borders.TopBorder);
            result.Left = ReadBorderSide(borders.LeftBorder);
            result.Bottom = ReadBorderSide(borders.BottomBorder);
            result.Right = ReadBorderSide(borders.RightBorder);

            if (result.Top is null && result.Left is null && result.Bottom is null && result.Right is null)
                return null;

            return result;
        }

        private static PageBorderSide? ReadBorderSide(W.BorderType? border)
        {
            if (border?.Val is null || !border.Val.HasValue) return null;

            string style = border.Val.InnerText ?? "single";
            if (style is "nil" or "none") return null;

            return new PageBorderSide
            {
                Style = style,
                // w:sz — восьмые доли пункта.
                WidthPt = (border.Size?.Value ?? 4) / 8.0,
                SpacePt = border.Space?.Value ?? 0,
                Color = NormalizeHexColor(border.Color?.Value),
                Shadow = border.Shadow?.Value == true
            };
        }

        /// <summary>Номера строк раздела (w:lnNumType); null — номеров нет.</summary>
        private static SectionLineNumbering? ReadLineNumbering(W.LineNumberType? lnNum)
        {
            if (lnNum is null) return null;

            var numbering = new SectionLineNumbering
            {
                CountBy = Math.Max(1, (int)(lnNum.CountBy?.Value ?? 1)),

                // w:start у Word считается от нуля: «начать с 1» записано нулём.
                Start = Math.Max(0, (int)(lnNum.Start?.Value ?? 0)) + 1
            };

            if (TryTwips(lnNum.Distance?.InnerText, out double distanceTwips))
                numbering.DistanceMm = Math.Round(distanceTwips / TwipsPerMm, 2);

            var restart = lnNum.Restart?.Value;
            if (restart == W.LineNumberRestartValues.NewSection) numbering.Restart = LineNumberRestart.NewSection;
            else if (restart == W.LineNumberRestartValues.Continuous) numbering.Restart = LineNumberRestart.Continuous;

            return numbering;
        }

        /// <summary>
        /// Последний раздел документа сверх листа и колонок. Null — в нём нет ничего
        /// сверх одного раздела с равными колонками.
        /// </summary>
        private static SectionLayout? ReadFinalSection(W.Body body)
        {
            var sectPr = body.GetFirstChild<W.SectionProperties>();
            if (sectPr is null) return null;

            var layout = ReadSectionLayout(sectPr, withPage: false);

            bool plain = layout.Start == SectionStart.NextPage
                && !layout.TitlePage
                && layout.VerticalAlignment == SectionVerticalAlignment.Top
                && layout.PageBorders is null
                && layout.LineNumbering is null
                && layout.PageNumberStart is null
                && layout.PageNumberFormat is null
                && layout.Columns?.Columns is null;

            return plain ? null : layout;
        }
    }
}
