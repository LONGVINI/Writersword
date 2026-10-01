using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Page;
using Writersword.Modules.TextEditor.Models.Styles;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Колонтитулы и нумерация страниц из .docx.
    ///
    /// В Word колонтитулы живут в разделах: у каждого раздела свои (или унаследованные
    /// от предыдущего), своя «особая первая», свой вид номера и своё начало счёта. У нас
    /// колонтитулы одни на документ, а исключения — правила листов. Перевод такой:
    ///   • текст колонтитулов берётся у первого раздела, где он вообще есть;
    ///   • разделы без колонтитулов или без номера становятся правилами «дальше без
    ///     колонтитулов» / «дальше без номера» от первого абзаца раздела — правило
    ///     привязано меткой к абзацу и едет вместе с текстом;
    ///   • начало счёта и вид номера раздела — правила «счёт отсюда» и «вид номера отсюда»;
    ///   • пустая «особая первая» у раздела в середине — правило одного листа.
    /// Разделы с другим текстом колонтитулов сводятся к общему тексту — об этом
    /// предупреждение.
    ///
    /// Места колонтитула — лево, центр, право — берутся по табуляциям абзаца (так
    /// колонтитулы делает Word: центральная и правая позиции стиля «Верхний колонтитул»),
    /// по выравнивающим табуляциям (w:ptab) и по выравниванию абзаца. Поля PAGE и
    /// NUMPAGES становятся живыми полями номера.
    /// </summary>
    public sealed partial class ImportService
    {
        /// <summary>Внутренние разрывы разделов: параметры раздела и индекс блока, с которого начинается следующий раздел.</summary>
        private readonly List<(W.SectionProperties SectPr, int NextStartBlock)> _sectionBreaks = new();

        /// <summary>Полоса колонтитула Word, разобранная на три места.</summary>
        private sealed class DocxBand
        {
            public HeaderFooterBand Band { get; } = new();

            /// <summary>Первый ран с текстом — по нему берётся шрифт колонтитулов.</summary>
            public W.Run? FirstTextRun { get; set; }

            /// <summary>Абзац первого рана с текстом.</summary>
            public W.Paragraph? FirstTextParagraph { get; set; }

            public bool IsEmpty => Band.IsEmpty;

            public bool HasPage => Band.HasPageNumber;

            public static DocxBand Empty { get; } = new();
        }

        /// <summary>Раздел Word глазами колонтитулов.</summary>
        private sealed class DocxSection
        {
            public int StartBlock { get; init; }
            public W.SectionProperties? SectPr { get; init; }

            public DocxBand DefaultHeader { get; set; } = DocxBand.Empty;
            public DocxBand DefaultFooter { get; set; } = DocxBand.Empty;
            public DocxBand FirstHeader { get; set; } = DocxBand.Empty;
            public DocxBand FirstFooter { get; set; } = DocxBand.Empty;
            public DocxBand EvenHeader { get; set; } = DocxBand.Empty;
            public DocxBand EvenFooter { get; set; } = DocxBand.Empty;

            public bool TitlePage { get; set; }
            public PageNumberFormat Format { get; set; } = PageNumberFormat.Arabic;
            public int? Start { get; set; }

            public bool DefaultEmpty => DefaultHeader.IsEmpty && DefaultFooter.IsEmpty;
            public bool DefaultHasPage => DefaultHeader.HasPage || DefaultFooter.HasPage;
        }

        /// <summary>Сброс сведений о разделах перед новым импортом.</summary>
        private void BeginSectionTracking() => _sectionBreaks.Clear();

        /// <summary>
        /// Абзац с параметрами раздела закрывает раздел. Разрыв «со следующей страницы»
        /// (и «с чётной», «с нечётной») становится разрывом страницы; непрерывный —
        /// ничем: текст идёт дальше на том же листе.
        /// </summary>
        private void NoteSectionBreak(W.Paragraph p, SectionModel section)
        {
            var sectPr = p.ParagraphProperties?.SectionProperties;
            if (sectPr is null) return;

            var type = sectPr.GetFirstChild<W.SectionType>()?.Val?.Value;
            bool continuous = type == W.SectionMarkValues.Continuous;

            if (!continuous)
                section.Blocks.Add(new BreakBlock { BreakType = BreakType.Page });

            _sectionBreaks.Add((sectPr, section.Blocks.Count));
        }

        /// <summary>
        /// Колонтитулы документа из разделов Word. Null — ни у одного раздела нет ни
        /// колонтитулов, ни своего счёта.
        /// </summary>
        private HeaderFooterSettings? ImportHeaderFooter(
            W.Body body, MainDocumentPart mainPart, SectionModel section,
            DocxFormatResolver resolver, List<string> warnings)
        {
            var sections = new List<DocxSection>();

            int start = 0;
            foreach (var (sectPr, nextStart) in _sectionBreaks)
            {
                sections.Add(new DocxSection { StartBlock = start, SectPr = sectPr });
                start = nextStart;
            }
            sections.Add(new DocxSection { StartBlock = start, SectPr = body.GetFirstChild<W.SectionProperties>() });

            var cache = new Dictionary<string, DocxBand>(StringComparer.Ordinal);

            // Ссылки на колонтитулы наследуются: раздел без своей ссылки берёт полосу
            // предыдущего раздела того же вида.
            DocxBand dh = DocxBand.Empty, df = DocxBand.Empty;
            DocxBand fh = DocxBand.Empty, ff = DocxBand.Empty;
            DocxBand eh = DocxBand.Empty, ef = DocxBand.Empty;

            foreach (var s in sections)
            {
                var sectPr = s.SectPr;
                if (sectPr is not null)
                {
                    foreach (var reference in sectPr.Elements<W.HeaderReference>())
                    {
                        var band = ReadBandPart(mainPart, reference.Id?.Value, resolver, cache);
                        var kind = reference.Type?.Value;
                        if (kind == W.HeaderFooterValues.First) fh = band;
                        else if (kind == W.HeaderFooterValues.Even) eh = band;
                        else dh = band;
                    }

                    foreach (var reference in sectPr.Elements<W.FooterReference>())
                    {
                        var band = ReadBandPart(mainPart, reference.Id?.Value, resolver, cache);
                        var kind = reference.Type?.Value;
                        if (kind == W.HeaderFooterValues.First) ff = band;
                        else if (kind == W.HeaderFooterValues.Even) ef = band;
                        else df = band;
                    }

                    var titlePg = sectPr.GetFirstChild<W.TitlePage>();
                    s.TitlePage = titlePg is not null && (titlePg.Val is null || titlePg.Val.Value);

                    var pgNum = sectPr.GetFirstChild<W.PageNumberType>();
                    s.Format = MapPageNumberFormat(pgNum?.Format?.Value);
                    s.Start = pgNum?.Start?.Value;
                }

                s.DefaultHeader = dh;
                s.DefaultFooter = df;
                s.FirstHeader = fh;
                s.FirstFooter = ff;
                s.EvenHeader = eh;
                s.EvenFooter = ef;
            }

            var evenAndOdd = mainPart.DocumentSettingsPart?.Settings?.GetFirstChild<W.EvenAndOddHeaders>();
            bool differentOddEven = evenAndOdd is not null && (evenAndOdd.Val is null || evenAndOdd.Val.Value);

            bool anyText = sections.Any(s => !s.DefaultEmpty
                || (s.TitlePage && !(s.FirstHeader.IsEmpty && s.FirstFooter.IsEmpty))
                || (differentOddEven && !(s.EvenHeader.IsEmpty && s.EvenFooter.IsEmpty)));
            bool anyNumbering = sections.Any(s => s.Start is not null || s.Format != PageNumberFormat.Arabic);

            if (!anyText && !anyNumbering) return null;

            // Раздел-образец: первый, где у обычных листов есть колонтитулы.
            int baseIndex = sections.FindIndex(s => !s.DefaultEmpty);
            if (baseIndex < 0) baseIndex = 0;
            var baseSection = sections[baseIndex];
            var first = sections[0];

            var settings = new HeaderFooterSettings
            {
                Header = baseSection.DefaultHeader.Band.Clone(),
                Footer = baseSection.DefaultFooter.Band.Clone(),
                EvenHeader = baseSection.EvenHeader.Band.Clone(),
                EvenFooter = baseSection.EvenFooter.Band.Clone(),

                // Первая страница документа — первая страница первого раздела.
                DifferentFirstPage = first.TitlePage,
                FirstHeader = first.FirstHeader.Band.Clone(),
                FirstFooter = first.FirstFooter.Band.Clone(),

                DifferentOddEven = differentOddEven,
                NumberFormat = baseSection.Format,
                StartNumber = first.Start ?? 1
            };

            ApplyBandFont(settings, baseSection, first, resolver);

            // Правила разделов: каждое изменение против предыдущего раздела — правило
            // от первого абзаца раздела.
            bool visible = true;
            bool number = baseSection.DefaultHasPage;
            var format = baseSection.Format;
            bool textDiffers = false;
            bool anchorsLost = false;

            for (int i = 0; i < sections.Count; i++)
            {
                var s = sections[i];
                Guid? anchor = i == 0 ? null : FirstParagraphId(section, s.StartBlock);

                if (i > 0 && anchor is null)
                {
                    anchorsLost = true;
                    continue;
                }

                bool sVisible = !s.DefaultEmpty;
                if (sVisible != visible)
                {
                    settings.Rules.Add(SectionRule(anchor, true,
                        sVisible ? PageRuleAction.ShowHeaderFooter : PageRuleAction.HideHeaderFooter));
                    visible = sVisible;
                }

                if (sVisible)
                {
                    bool sNumber = s.DefaultHasPage;
                    if (sNumber != number)
                    {
                        settings.Rules.Add(SectionRule(anchor, true,
                            sNumber ? PageRuleAction.ShowNumber : PageRuleAction.HideNumber));
                        number = sNumber;
                    }

                    if (!HeaderFooterBand.Same(s.DefaultHeader.Band, settings.Header)
                        || !HeaderFooterBand.Same(s.DefaultFooter.Band, settings.Footer))
                        textDiffers = true;
                }

                if (s.Format != format)
                {
                    var rule = SectionRule(anchor, true, PageRuleAction.SetFormat);
                    rule.Format = s.Format;
                    settings.Rules.Add(rule);
                    format = s.Format;
                }

                if (i > 0 && s.Start is int restart)
                {
                    var rule = SectionRule(anchor, true, PageRuleAction.RestartNumbering);
                    rule.StartNumber = restart;
                    settings.Rules.Add(rule);
                }

                // «Особая первая» раздела в середине документа: пустая — лист без
                // колонтитулов; с текстом — у нас одна «первая» на документ.
                if (i > 0 && s.TitlePage && sVisible)
                {
                    if (s.FirstHeader.IsEmpty && s.FirstFooter.IsEmpty)
                        settings.Rules.Add(SectionRule(anchor, false, PageRuleAction.HideHeaderFooter));
                    else if (!HeaderFooterBand.Same(s.FirstHeader.Band, s.DefaultHeader.Band)
                             || !HeaderFooterBand.Same(s.FirstFooter.Band, s.DefaultFooter.Band))
                        textDiffers = true;
                }
            }

            if (textDiffers)
                warnings.Add("У разделов документа разный текст колонтитулов — он сведён к колонтитулам " +
                             "первого раздела с колонтитулами. Скрытие колонтитулов и номеров, начало " +
                             "счёта и вид номера по разделам перенесены правилами страниц.");

            if (anchorsLost)
                warnings.Add("Часть разделов начинается не абзацем — их правила колонтитулов не перенесены.");

            return settings;
        }

        /// <summary>Правило раздела: для первого раздела — от первого листа, иначе — от метки у абзаца.</summary>
        private static PageRule SectionRule(Guid? anchor, bool range, PageRuleAction action)
        {
            if (anchor is Guid id)
            {
                return new PageRule
                {
                    Scope = range ? PageRuleScope.FromAnchor : PageRuleScope.Anchor,
                    AnchorParagraphId = id,
                    PageIndex = 0,
                    Action = action
                };
            }

            return new PageRule
            {
                Scope = range ? PageRuleScope.FromPage : PageRuleScope.Page,
                PageIndex = 0,
                Action = action
            };
        }

        /// <summary>Первый абзац потока, начиная с блока <paramref name="startBlock"/>.</summary>
        private static Guid? FirstParagraphId(SectionModel section, int startBlock)
        {
            for (int i = Math.Max(startBlock, 0); i < section.Blocks.Count; i++)
            {
                if (section.Blocks[i] is ParagraphBlock paragraph)
                    return paragraph.Id;
            }
            return null;
        }

        private static PageNumberFormat MapPageNumberFormat(W.NumberFormatValues? format)
        {
            if (format is null) return PageNumberFormat.Arabic;
            var f = format.Value;

            if (f == W.NumberFormatValues.LowerRoman) return PageNumberFormat.RomanLower;
            if (f == W.NumberFormatValues.UpperRoman) return PageNumberFormat.RomanUpper;
            if (f == W.NumberFormatValues.LowerLetter) return PageNumberFormat.LetterLower;
            if (f == W.NumberFormatValues.UpperLetter) return PageNumberFormat.LetterUpper;
            return PageNumberFormat.Arabic;
        }

        /// <summary>Шрифт колонтитулов — по первому рану с текстом в полосах образца.</summary>
        private static void ApplyBandFont(
            HeaderFooterSettings settings, DocxSection baseSection, DocxSection first, DocxFormatResolver resolver)
        {
            DocxBand? source = null;
            foreach (var band in new[]
                     {
                         baseSection.DefaultFooter, baseSection.DefaultHeader,
                         first.FirstFooter, first.FirstHeader,
                         baseSection.EvenFooter, baseSection.EvenHeader
                     })
            {
                if (band.FirstTextRun is not null && band.FirstTextParagraph is not null)
                {
                    source = band;
                    break;
                }
            }

            if (source is null) return;

            var effPara = resolver.ResolveEffectiveParagraph(source.FirstTextParagraph!);
            var run = resolver.ResolveEffectiveRun(source.FirstTextRun!, effPara);

            if (!string.IsNullOrWhiteSpace(run.FontFamily)) settings.FontFamily = run.FontFamily;
            if (run.FontSizePt is double size && size > 0) settings.FontSizePt = size;
            settings.IsBold = run.Bold ?? false;
            settings.IsItalic = run.Italic ?? false;
            if (!string.IsNullOrWhiteSpace(run.TextColor)) settings.TextColor = run.TextColor;
        }

        // ── Разбор части колонтитула ──────────────────────────────────────

        private static DocxBand ReadBandPart(
            MainDocumentPart mainPart, string? relationshipId, DocxFormatResolver resolver,
            Dictionary<string, DocxBand> cache)
        {
            if (string.IsNullOrEmpty(relationshipId)) return DocxBand.Empty;
            if (cache.TryGetValue(relationshipId, out var cached)) return cached;

            OpenXmlElement? root = null;
            try
            {
                var part = mainPart.GetPartById(relationshipId);
                root = part switch
                {
                    HeaderPart header => header.Header,
                    FooterPart footer => footer.Footer,
                    _ => null
                };
            }
            catch (ArgumentOutOfRangeException)
            {
                root = null;
            }
            catch (KeyNotFoundException)
            {
                root = null;
            }

            var band = root is null ? DocxBand.Empty : ParseBand(root, resolver);
            cache[relationshipId] = band;
            return band;
        }

        /// <summary>Строки трёх мест полосы: абзацы колонтитула раскладываются по местам.</summary>
        private static DocxBand ParseBand(OpenXmlElement root, DocxFormatResolver resolver)
        {
            var result = new DocxBand();
            var lines = new[] { new List<string>(), new List<string>(), new List<string>() };

            foreach (var p in root.Descendants<W.Paragraph>())
            {
                // Надпись в фигуре Word пишет дважды: для новых программ и запасной
                // копией для старых. Запасная копия пропускается.
                if (p.Ancestors<AlternateContentFallback>().Any()) continue;

                var effPara = resolver.ResolveEffectiveParagraph(p);
                var segments = ReadParagraphSegments(p, out var firstRun);

                if (firstRun is not null && result.FirstTextRun is null)
                {
                    result.FirstTextRun = firstRun;
                    result.FirstTextParagraph = p;
                }

                var slots = AssignSlots(p, effPara, segments);
                for (int slot = 0; slot < 3; slot++)
                {
                    string text = slots[slot].Trim();
                    if (text.Length > 0) lines[slot].Add(text);
                }
            }

            result.Band.Left = string.Join("\n", lines[0]);
            result.Band.Center = string.Join("\n", lines[1]);
            result.Band.Right = string.Join("\n", lines[2]);
            return result;
        }

        /// <summary>Кусок абзаца между табуляциями. Slot — место, заданное выравнивающей табуляцией.</summary>
        private sealed class BandSegment
        {
            public StringBuilder Text { get; } = new();
            public int? Slot { get; init; }
        }

        /// <summary>Открытое поле внутри абзаца колонтитула.</summary>
        private sealed class BandField
        {
            public StringBuilder Code { get; } = new();
            public bool InResult { get; set; }
            public string? Token { get; set; }
            public bool Emitted { get; set; }
        }

        /// <summary>
        /// Текст абзаца кусками между табуляциями. Поля PAGE и NUMPAGES — живыми полями,
        /// прочие поля — их последним результатом.
        /// </summary>
        private static List<BandSegment> ReadParagraphSegments(W.Paragraph p, out W.Run? firstTextRun)
        {
            var segments = new List<BandSegment> { new() };
            var fields = new List<BandField>();
            W.Run? first = null;

            void Append(string text)
            {
                // Код поля и результат живого поля в текст не идут.
                foreach (var f in fields)
                    if (!f.InResult || f.Token is not null) return;

                segments[^1].Text.Append(text);
            }

            void Walk(OpenXmlElement element)
            {
                foreach (var child in element.ChildElements)
                {
                    switch (child)
                    {
                        case W.ParagraphProperties:
                        case W.RunProperties:
                            break;

                        case W.SimpleField simple:
                        {
                            string? token = FieldToken(simple.Instruction?.Value);
                            if (token is not null)
                            {
                                Append(token);
                            }
                            else
                            {
                                Walk(simple);
                            }
                            break;
                        }

                        case W.Run run:
                            foreach (var rc in run.ChildElements)
                            {
                                switch (rc)
                                {
                                    case W.FieldChar fc when fc.FieldCharType?.Value == W.FieldCharValues.Begin:
                                        fields.Add(new BandField());
                                        break;

                                    case W.FieldChar fc when fc.FieldCharType?.Value == W.FieldCharValues.Separate:
                                        if (fields.Count > 0)
                                        {
                                            var top = fields[^1];
                                            top.Token = FieldToken(top.Code.ToString());
                                            top.InResult = true;
                                            if (top.Token is not null && !top.Emitted)
                                            {
                                                top.Emitted = true;
                                                fields.RemoveAt(fields.Count - 1);
                                                Append(top.Token);
                                                fields.Add(top);
                                            }
                                        }
                                        break;

                                    case W.FieldChar fc when fc.FieldCharType?.Value == W.FieldCharValues.End:
                                        if (fields.Count > 0)
                                        {
                                            var top = fields[^1];
                                            fields.RemoveAt(fields.Count - 1);

                                            // Поле без результата: токен ставится по коду.
                                            if (!top.Emitted)
                                            {
                                                string? token = top.Token ?? FieldToken(top.Code.ToString());
                                                if (token is not null) Append(token);
                                            }
                                        }
                                        break;

                                    case W.FieldCode code:
                                        if (fields.Count > 0 && !fields[^1].InResult)
                                            fields[^1].Code.Append(code.Text);
                                        break;

                                    case W.Text text:
                                        if (text.Text.Length > 0)
                                        {
                                            int before = segments[^1].Text.Length;
                                            Append(text.Text);
                                            if (first is null && segments[^1].Text.Length > before
                                                && text.Text.Trim().Length > 0)
                                                first = run;
                                        }
                                        break;

                                    case W.TabChar:
                                        if (fields.Count == 0 || fields.TrueForAll(f => f.InResult && f.Token is null))
                                            segments.Add(new BandSegment());
                                        break;

                                    case W.PositionalTab ptab:
                                    {
                                        var alignment = ptab.Alignment?.Value;
                                        int slot = alignment == W.AbsolutePositionTabAlignmentValues.Center ? 1
                                            : alignment == W.AbsolutePositionTabAlignmentValues.Right ? 2
                                            : 0;
                                        segments.Add(new BandSegment { Slot = slot });
                                        break;
                                    }

                                    case W.Break br when br.Type is null || br.Type.Value == W.BreakValues.TextWrapping:
                                        Append("\n");
                                        break;
                                }
                            }
                            break;

                        case W.Paragraph:
                            // Абзацы надписей внутри абзаца разбираются отдельно.
                            break;

                        default:
                            if (child.HasChildren) Walk(child);
                            break;
                    }
                }
            }

            Walk(p);
            firstTextRun = first;
            return segments;
        }

        /// <summary>Токен поля по коду: PAGE — номер, NUMPAGES и SECTIONPAGES — всего страниц.</summary>
        private static string? FieldToken(string? instruction)
        {
            if (string.IsNullOrWhiteSpace(instruction)) return null;

            string name = instruction.Trim().Split(' ', '\t', '\\')[0].Trim().ToUpperInvariant();
            return name switch
            {
                "PAGE" => HeaderFooterSettings.PageToken,
                "NUMPAGES" => HeaderFooterSettings.PagesToken,
                "SECTIONPAGES" => HeaderFooterSettings.PagesToken,
                _ => null
            };
        }

        /// <summary>
        /// Раскладывает куски абзаца по местам. Ячейки таблицы — по номеру столбца;
        /// выравнивающие табуляции — по своему выравниванию; обычные табуляции — по
        /// позициям табуляции абзаца; абзац без табуляций — по выравниванию.
        /// </summary>
        private static string[] AssignSlots(W.Paragraph p, EffectiveParagraph effPara, List<BandSegment> segments)
        {
            var slots = new[] { new StringBuilder(), new StringBuilder(), new StringBuilder() };

            void Put(int slot, string text)
            {
                if (text.Length == 0) return;
                var sb = slots[Math.Clamp(slot, 0, 2)];
                if (sb.Length > 0 && sb[^1] != '\n') sb.Append(' ');
                sb.Append(text);
            }

            var cell = p.Ancestors<W.TableCell>().FirstOrDefault();
            if (cell?.Parent is W.TableRow row)
            {
                var cells = row.Elements<W.TableCell>().ToList();
                int index = cells.IndexOf(cell);
                int count = cells.Count;

                int cellSlot = count <= 1
                    ? JustificationSlot(effPara)
                    : index == 0 ? 0 : index == count - 1 ? 2 : 1;

                foreach (var segment in segments)
                    Put(cellSlot, segment.Text.ToString());

                return slots.Select(s => s.ToString()).ToArray();
            }

            if (segments.Count == 1)
            {
                Put(JustificationSlot(effPara), segments[0].Text.ToString());
                return slots.Select(s => s.ToString()).ToArray();
            }

            // Позиции табуляции абзаца по порядку: k-я табуляция ведёт к k-й позиции.
            var stops = (effPara.Format.TabStops ?? new List<TabStop>())
                .OrderBy(t => t.PositionPt)
                .ToList();

            int tabs = segments.Count(s => s.Slot is null) - 1;
            int ordinal = 0;
            int currentSlot = 0;

            for (int i = 0; i < segments.Count; i++)
            {
                var segment = segments[i];

                if (i > 0)
                {
                    if (segment.Slot is int explicitSlot)
                    {
                        currentSlot = explicitSlot;
                    }
                    else
                    {
                        ordinal++;
                        if (ordinal - 1 < stops.Count)
                        {
                            currentSlot = stops[ordinal - 1].Alignment switch
                            {
                                TabAlignment.Center => 1,
                                TabAlignment.Right => 2,
                                TabAlignment.Decimal => 2,
                                _ => tabs == 1 ? 2 : Math.Min(ordinal, 2)
                            };
                        }
                        else
                        {
                            currentSlot = tabs == 1 ? 2 : Math.Min(ordinal, 2);
                        }
                    }
                }
                else
                {
                    currentSlot = segment.Slot ?? 0;
                }

                Put(currentSlot, segment.Text.ToString());
            }

            return slots.Select(s => s.ToString()).ToArray();
        }

        private static int JustificationSlot(EffectiveParagraph effPara)
        {
            var jc = effPara.Format.Justification;
            if (jc is null) return 0;

            var value = jc.Value;
            if (value == W.JustificationValues.Center) return 1;
            if (value == W.JustificationValues.Right || value == W.JustificationValues.End) return 2;
            return 0;
        }
    }
}
