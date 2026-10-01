using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Page;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Колонтитулы и нумерация страниц в .docx.
    ///
    /// У нас колонтитулы одни на документ, а исключения — правила листов. Word умеет
    /// только разделы: у раздела свои колонтитулы, «особая первая», вид номера и
    /// начало счёта. Выгрузка считает оформление каждого листа тем же проходом, что
    /// полотно, и режет документ на разделы там, где лист перестаёт совпадать с
    /// предыдущими:
    ///   • лист-исключение в начале раздела — «особая первая страница» раздела, так
    ///     одиночное правило стоит одного разрыва, а не двух;
    ///   • разрыв в счёте или смена вида номера — новый раздел со своим pgNumType;
    ///   • разрыв раздела ставится перед абзацем, открывающим лист. Лист, открытый
    ///     продолжением абзаца или таблицей, разрыва не получает — его оформление
    ///     совпадёт с соседями, об этом предупреждение.
    /// Разрыв страницы, стоявший перед таким листом, заменяется разрывом раздела
    /// «со следующей страницы» — иначе Word вставил бы пустой лист.
    ///
    /// Без раскладки (сведений о листах нет) выгружаются только шаблоны: обычные,
    /// первой страницы и чётных.
    /// </summary>
    public sealed partial class ExportService
    {
        /// <summary>Вид колонтитулов листа в понятиях Word: текст мест с полями.</summary>
        private sealed class DocxHfLook
        {
            public DocxHfLook(string[] header, string[] footer)
            {
                Header = header;
                Footer = footer;
                Key = string.Join("\u0001", header) + "\u0002" + string.Join("\u0001", footer);
            }

            public string[] Header { get; }
            public string[] Footer { get; }
            public string Key { get; }

            public bool SameAs(DocxHfLook? other) => other is not null && string.Equals(Key, other.Key, StringComparison.Ordinal);
        }

        /// <summary>Раздел Word, нарезанный из листов.</summary>
        private sealed class DocxHfSection
        {
            public int StartPage { get; init; }
            public DocxHfLook Odd { get; set; } = null!;
            public DocxHfLook? Even { get; set; }
            public DocxHfLook? First { get; set; }
            public bool TitlePage { get; set; }
            public int? Start { get; set; }
            public PageNumberFormat Format { get; set; }
        }

        /// <summary>План выгрузки колонтитулов: разделы Word и абзацы, с которых они начинаются.</summary>
        private sealed class DocxHfPlan
        {
            public HeaderFooterSettings Settings { get; init; } = null!;
            public List<DocxHfSection> Sections { get; } = new();

            /// <summary>Абзац → раздел Word, который с него начинается (кроме первого).</summary>
            public Dictionary<Guid, int> SectionStarts { get; } = new();

            /// <summary>Раздел Word, в котором сейчас идёт запись.</summary>
            public int Current { get; set; }

            /// <summary>Разделы, чьё начало уже записано: продолжение раздела после разрыва раздела модели начала счёта не повторяет.</summary>
            public HashSet<int> Written { get; } = new();

            public Dictionary<string, string> HeaderIds { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, string> FooterIds { get; } = new(StringComparer.Ordinal);

            /// <summary>Ширина текста листа в twips — для позиций табуляции центра и правого края.</summary>
            public int ContentWidthTwips { get; init; }
        }

        /// <summary>
        /// План колонтитулов документа. Null — колонтитулов нет, выгружать нечего.
        /// </summary>
        private static DocxHfPlan? BuildHeaderFooterPlan(DocumentModel document, PageFacts? facts, DocxWriteContext ctx)
        {
            var settings = document.HeaderFooter;
            if (settings is null || settings.IsEmpty) return null;

            var ps = document.PageSettings;
            double contentMm = ps.GetPhysicalWidthMm() - ps.MarginLeftMm - ps.MarginRightMm - ps.MarginGutterMm;

            var plan = new DocxHfPlan
            {
                Settings = settings,
                ContentWidthTwips = (int)Math.Round(Math.Max(contentMm, 20) * TwipsPerMm)
            };

            if (facts is null || facts.PageCount <= 0)
            {
                plan.Sections.Add(new DocxHfSection
                {
                    StartPage = 0,
                    Odd = TemplateLook(settings.Header, settings.Footer),
                    Even = settings.DifferentOddEven ? TemplateLook(settings.EvenHeader, settings.EvenFooter) : null,
                    First = settings.DifferentFirstPage ? TemplateLook(settings.FirstHeader, settings.FirstFooter) : null,
                    TitlePage = settings.DifferentFirstPage,
                    Start = settings.StartNumber,
                    Format = settings.NumberFormat
                });

                if (settings.Rules.Count > 0 || settings.HideNumberOnChapterStart || settings.HideHeaderFooterOnChapterStart)
                    ctx.Warnings.Add("Правила страниц не перенесены в .docx: раскладка листов недоступна.");

                return plan;
            }

            int count = facts.PageCount;
            var decorations = PageNumbering.Compute(settings, count, facts.ParagraphStartPages, facts.ChapterStartPages);
            if (decorations.Length == 0) return null;

            var looks = new DocxHfLook[decorations.Length];
            for (int k = 0; k < decorations.Length; k++)
                looks[k] = PageLook(settings, decorations[k]);

            int Parity(int k) => settings.DifferentOddEven && decorations[k].Number % 2 == 0 ? 1 : 0;

            var regular = new DocxHfLook?[2];
            var current = new DocxHfSection { StartPage = 0 };
            bool approximated = false;

            for (int k = 1; k < decorations.Length; k++)
            {
                var d = decorations[k];
                var prev = decorations[k - 1];

                bool needBreak = d.Format != prev.Format || d.Number != prev.Number + 1;
                int parity = Parity(k);

                if (!needBreak)
                {
                    if (regular[parity] is null)
                        regular[parity] = looks[k];
                    else if (!regular[parity]!.SameAs(looks[k]))
                        needBreak = true;
                }

                if (!needBreak) continue;

                Guid? opening = k < facts.PageOpeningParagraphs.Length ? facts.PageOpeningParagraphs[k] : null;
                if (opening is Guid paragraphId && !plan.SectionStarts.ContainsKey(paragraphId))
                {
                    CloseSection(current, regular, looks, decorations, settings, Parity);
                    plan.Sections.Add(current);
                    plan.SectionStarts[paragraphId] = plan.Sections.Count;

                    current = new DocxHfSection { StartPage = k };
                    regular = new DocxHfLook?[2];
                    continue;
                }

                // Лист открыт продолжением абзаца или таблицей: раздел перед ним не
                // начать. Он остаётся в разделе соседей и получает их оформление.
                approximated = true;
                regular[parity] ??= looks[k];
            }

            CloseSection(current, regular, looks, decorations, settings, Parity);
            plan.Sections.Add(current);

            if (approximated)
                ctx.Warnings.Add("Часть исключений для страниц не перенесена в .docx точно: лист начинается " +
                                 "продолжением абзаца или таблицей, и разрыв раздела перед ним не поставить.");

            return plan;
        }

        /// <summary>
        /// Завершает раздел: оформление первого листа, «особая первая», начало счёта.
        /// </summary>
        private static void CloseSection(
            DocxHfSection section, DocxHfLook?[] regular, DocxHfLook[] looks,
            PageDecoration[] decorations, HeaderFooterSettings settings, Func<int, int> parity)
        {
            int start = section.StartPage;
            var startLook = looks[start];
            int p = parity(start);

            regular[p] ??= startLook;

            section.TitlePage = !regular[p]!.SameAs(startLook);
            section.First = section.TitlePage ? startLook : null;
            section.Odd = regular[0] ?? regular[1]!;
            section.Even = settings.DifferentOddEven ? regular[1] ?? regular[0] : null;
            section.Format = decorations[start].Format;

            var d = decorations[start];
            if (start == 0)
                section.Start = d.Number;
            else if (d.Number != decorations[start - 1].Number + 1)
                section.Start = d.Number;
        }

        /// <summary>Вид листа: шаблоны мест с полями, как их напечатает Word.</summary>
        private static DocxHfLook PageLook(HeaderFooterSettings settings, PageDecoration decoration)
        {
            return new DocxHfLook(
                PageSlots(settings, decoration, true),
                PageSlots(settings, decoration, false));
        }

        private static string[] PageSlots(HeaderFooterSettings settings, PageDecoration decoration, bool header)
        {
            var result = new[] { string.Empty, string.Empty, string.Empty };
            if (!decoration.BandsVisible) return result;

            var band = settings.GetBand(decoration.Variant, header);
            for (int slot = 0; slot < 3; slot++)
            {
                string template = band.GetSlot(slot);
                if (string.IsNullOrEmpty(template)) continue;

                // Место с номером, который на листе не печатается, пустеет целиком —
                // так же его рисует полотно.
                if (template.Contains(HeaderFooterSettings.PageToken, StringComparison.Ordinal))
                {
                    if (!decoration.NumberVisible) continue;

                    // Набранный руками номер — просто текст: поле на этом листе не живёт.
                    if (decoration.ManualText is not null)
                        template = template.Replace(HeaderFooterSettings.PageToken, decoration.ManualText, StringComparison.Ordinal);
                }

                result[slot] = template;
            }

            return result;
        }

        private static DocxHfLook TemplateLook(HeaderFooterBand header, HeaderFooterBand footer)
            => new(
                new[] { header.Left, header.Center, header.Right },
                new[] { footer.Left, footer.Center, footer.Right });

        // ── Запись ────────────────────────────────────────────────────────

        /// <summary>Настройки документа Word: чётные и нечётные колонтитулы различаются.</summary>
        private static void WriteHeaderFooterSettingsPart(MainDocumentPart mainPart, DocxHfPlan plan)
        {
            if (!plan.Settings.DifferentOddEven) return;

            var part = mainPart.DocumentSettingsPart ?? mainPart.AddNewPart<DocumentSettingsPart>();
            part.Settings ??= new W.Settings();

            if (part.Settings.GetFirstChild<W.EvenAndOddHeaders>() is null)
                part.Settings.AppendChild(new W.EvenAndOddHeaders());

            part.Settings.Save();
        }

        /// <summary>
        /// Дописывает в параметры раздела ссылки на колонтитулы текущего раздела Word,
        /// вид номера, начало счёта и «особую первую».
        /// </summary>
        private void ApplyHeaderFooterToSection(W.SectionProperties sectPr, DocxHfPlan plan, DocxWriteContext ctx)
        {
            if (plan.Current < 0 || plan.Current >= plan.Sections.Count) return;

            var section = plan.Sections[plan.Current];
            bool beginning = plan.Written.Add(plan.Current);

            // Ссылки — первыми детьми w:sectPr: так требует схема.
            var references = new List<OpenXmlElement>
            {
                new W.HeaderReference
                {
                    Type = W.HeaderFooterValues.Default,
                    Id = EnsureBandPart(plan, section.Odd, true, ctx)
                }
            };

            if (section.Even is not null)
                references.Add(new W.HeaderReference
                {
                    Type = W.HeaderFooterValues.Even,
                    Id = EnsureBandPart(plan, section.Even, true, ctx)
                });

            if (beginning && section.TitlePage && section.First is not null)
                references.Add(new W.HeaderReference
                {
                    Type = W.HeaderFooterValues.First,
                    Id = EnsureBandPart(plan, section.First, true, ctx)
                });

            references.Add(new W.FooterReference
            {
                Type = W.HeaderFooterValues.Default,
                Id = EnsureBandPart(plan, section.Odd, false, ctx)
            });

            if (section.Even is not null)
                references.Add(new W.FooterReference
                {
                    Type = W.HeaderFooterValues.Even,
                    Id = EnsureBandPart(plan, section.Even, false, ctx)
                });

            if (beginning && section.TitlePage && section.First is not null)
                references.Add(new W.FooterReference
                {
                    Type = W.HeaderFooterValues.First,
                    Id = EnsureBandPart(plan, section.First, false, ctx)
                });

            for (int i = references.Count - 1; i >= 0; i--)
                sectPr.InsertAt(references[i], 0);

            // w:pgNumType — перед w:cols, w:titlePg — после.
            var pageNumber = new W.PageNumberType();
            bool hasPageNumber = false;

            var format = FormatToOoxml(section.Format);
            if (format is not null)
            {
                pageNumber.Format = format.Value;
                hasPageNumber = true;
            }

            if (beginning && section.Start is int start)
            {
                pageNumber.Start = start;
                hasPageNumber = true;
            }

            var columns = sectPr.GetFirstChild<W.Columns>();
            if (hasPageNumber)
            {
                if (columns is not null) sectPr.InsertBefore(pageNumber, columns);
                else sectPr.AppendChild(pageNumber);
            }

            if (beginning && section.TitlePage)
            {
                var titlePage = new W.TitlePage();
                if (columns is not null) sectPr.InsertAfter(titlePage, columns);
                else sectPr.AppendChild(titlePage);
            }
        }

        private static W.NumberFormatValues? FormatToOoxml(PageNumberFormat format) => format switch
        {
            PageNumberFormat.RomanLower => W.NumberFormatValues.LowerRoman,
            PageNumberFormat.RomanUpper => W.NumberFormatValues.UpperRoman,
            PageNumberFormat.LetterLower => W.NumberFormatValues.LowerLetter,
            PageNumberFormat.LetterUpper => W.NumberFormatValues.UpperLetter,
            _ => null
        };

        /// <summary>Часть колонтитула для вида листа. Одинаковые виды пишутся одной частью.</summary>
        private static string EnsureBandPart(DocxHfPlan plan, DocxHfLook look, bool header, DocxWriteContext ctx)
        {
            var ids = header ? plan.HeaderIds : plan.FooterIds;
            string key = string.Join("\u0001", header ? look.Header : look.Footer);
            if (ids.TryGetValue(key, out var existing)) return existing;

            var slots = header ? look.Header : look.Footer;
            var paragraphs = BuildBandParagraphs(slots, plan);

            string id;
            if (header)
            {
                var part = ctx.MainPart.AddNewPart<HeaderPart>();
                part.Header = new W.Header(paragraphs);
                DocxTextEffects.DeclareNamespaces(part.Header);
                part.Header.Save();
                id = ctx.MainPart.GetIdOfPart(part);
            }
            else
            {
                var part = ctx.MainPart.AddNewPart<FooterPart>();
                part.Footer = new W.Footer(paragraphs);
                DocxTextEffects.DeclareNamespaces(part.Footer);
                part.Footer.Save();
                id = ctx.MainPart.GetIdOfPart(part);
            }

            ids[key] = id;
            return id;
        }

        /// <summary>
        /// Абзацы полосы: строка на абзац, места разведены табуляциями по центру и у
        /// правого края — так колонтитулы строит сам Word.
        /// </summary>
        private static List<OpenXmlElement> BuildBandParagraphs(string[] slots, DocxHfPlan plan)
        {
            var lines = slots.Select(s => (s ?? string.Empty).Split('\n')).ToArray();
            int lineCount = Math.Max(1, lines.Max(l => l.Length));

            var result = new List<OpenXmlElement>(lineCount);
            for (int i = 0; i < lineCount; i++)
            {
                string left = i < lines[0].Length ? lines[0][i] : string.Empty;
                string center = i < lines[1].Length ? lines[1][i] : string.Empty;
                string right = i < lines[2].Length ? lines[2][i] : string.Empty;

                var paragraph = new W.Paragraph();

                var pPr = new W.ParagraphProperties();
                pPr.AppendChild(new W.Tabs(
                    new W.TabStop { Val = W.TabStopValues.Center, Position = TabTwips(plan.Settings.CenterTabMm, plan.ContentWidthTwips / 2) },
                    new W.TabStop { Val = W.TabStopValues.Right, Position = TabTwips(plan.Settings.RightTabMm, plan.ContentWidthTwips) }));
                pPr.AppendChild(new W.SpacingBetweenLines
                {
                    Before = "0",
                    After = "0",
                    Line = "240",
                    LineRule = W.LineSpacingRuleValues.Auto
                });
                paragraph.AppendChild(pPr);

                AppendBandText(paragraph, left, plan.Settings);

                if (center.Length > 0 || right.Length > 0)
                {
                    paragraph.AppendChild(BandRun(plan.Settings, new W.TabChar()));
                    AppendBandText(paragraph, center, plan.Settings);
                }

                if (right.Length > 0)
                {
                    paragraph.AppendChild(BandRun(plan.Settings, new W.TabChar()));
                    AppendBandText(paragraph, right, plan.Settings);
                }

                result.Add(paragraph);
            }

            return result;
        }

        /// <summary>Позиция табуляции колонтитула в twips: своя, если задана, иначе обычная.</summary>
        private static int TabTwips(double? mm, int fallback)
            => mm is double value ? (int)Math.Round(value * TwipsPerMm) : fallback;

        /// <summary>Текст места: обычный текст — ранами, поля номера — полями Word.</summary>
        private static void AppendBandText(W.Paragraph paragraph, string text, HeaderFooterSettings settings)
        {
            int i = 0;
            int textStart = 0;

            void FlushText(int end)
            {
                if (end <= textStart) return;
                paragraph.AppendChild(BandRun(settings, new W.Text(text.Substring(textStart, end - textStart))
                {
                    Space = SpaceProcessingModeValues.Preserve
                }));
            }

            while (i < text.Length)
            {
                string? field = null;
                int length = 0;

                if (string.CompareOrdinal(text, i, HeaderFooterSettings.PageToken, 0, HeaderFooterSettings.PageToken.Length) == 0)
                {
                    field = " PAGE ";
                    length = HeaderFooterSettings.PageToken.Length;
                }
                else if (string.CompareOrdinal(text, i, HeaderFooterSettings.PagesToken, 0, HeaderFooterSettings.PagesToken.Length) == 0)
                {
                    field = " NUMPAGES ";
                    length = HeaderFooterSettings.PagesToken.Length;
                }

                if (field is null)
                {
                    i++;
                    continue;
                }

                FlushText(i);

                // Результат поля — «1»: Word пересчитает его при открытии.
                var simple = new W.SimpleField { Instruction = field };
                simple.AppendChild(BandRun(settings, new W.Text("1")));
                paragraph.AppendChild(simple);

                i += length;
                textStart = i;
            }

            FlushText(text.Length);
        }

        private static W.Run BandRun(HeaderFooterSettings settings, OpenXmlElement content)
        {
            var rPr = new W.RunProperties();

            if (!string.IsNullOrWhiteSpace(settings.FontFamily))
            {
                rPr.AppendChild(new W.RunFonts
                {
                    Ascii = settings.FontFamily,
                    HighAnsi = settings.FontFamily,
                    ComplexScript = settings.FontFamily,
                    EastAsia = settings.FontFamily
                });
            }

            if (settings.IsBold) rPr.AppendChild(new W.Bold());
            if (settings.IsItalic) rPr.AppendChild(new W.Italic());

            string? color = HexWithoutHash(settings.TextColor);
            if (color is not null) rPr.AppendChild(new W.Color { Val = color });

            if (settings.FontSizePt > 0)
            {
                string halfPoints = Math.Round(settings.FontSizePt * HalfPointsPerPoint).ToString(CultureInfo.InvariantCulture);
                rPr.AppendChild(new W.FontSize { Val = halfPoints });
                rPr.AppendChild(new W.FontSizeComplexScript { Val = halfPoints });
            }

            return new W.Run(rPr, content);
        }
    }
}
