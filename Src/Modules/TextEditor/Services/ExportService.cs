using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using SkiaSharp;
using Writersword.Core.Models.Print;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.Models.Page;
using Writersword.Modules.TextEditor.Models.Styles;
using W = DocumentFormat.OpenXml.Wordprocessing;
using Wp = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using Dr = DocumentFormat.OpenXml.Drawing;
using Pic = DocumentFormat.OpenXml.Drawing.Pictures;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Результат операции экспорта.
    /// </summary>
    public sealed class ExportResult
    {
        public bool Success { get; set; }

        /// <summary>Сообщение об ошибке. Null при успехе.</summary>
        public string? ErrorMessage { get; set; }

        /// <summary>Путь к сохранённому файлу.</summary>
        public string? OutputPath { get; set; }

        /// <summary>
        /// Предупреждения о потере содержимого или форматирования при экспорте:
        /// плавающие объекты, колонтитулы и прочее, чему нет места в целевом формате.
        /// </summary>
        public string[] Warnings { get; set; } = Array.Empty<string>();

        public static ExportResult Ok(string path) =>
            new() { Success = true, OutputPath = path };

        public static ExportResult Ok(string path, string[] warnings) =>
            new() { Success = true, OutputPath = path, Warnings = warnings ?? Array.Empty<string>() };

        public static ExportResult Fail(string error) => new() { Success = false, ErrorMessage = error };
    }

    /// <summary>
    /// Экспортирует документ в различные форматы.
    /// При экспорте настройки <see cref="Models.Page.CanvasSettings"/> игнорируются —
    /// используются только физические свойства страницы.
    /// </summary>
    public sealed class ExportService
    {
        // Единицы измерения OOXML: twips = 1/20 пункта = 1/1440 дюйма.
        private const double TwipsPerPoint = 20.0;
        private const double TwipsPerMm = 1440.0 / 25.4;
        private const double EmuPerPoint = 12700.0;
        private const double HalfPointsPerPoint = 2.0;
        private const double EighthsPerPoint = 8.0;

        /// <summary>
        /// Экспортирует документ в .txt (plain text, без форматирования).
        /// </summary>
        public async Task<ExportResult> ExportToTxtAsync(DocumentModel document, string outputPath)
        {
            try
            {
                using var writer = new StreamWriter(outputPath, false, Encoding.UTF8);

                foreach (var section in document.Sections)
                {
                    foreach (var block in section.Blocks)
                    {
                        if (block is ParagraphBlock paragraph)
                        {
                            await writer.WriteLineAsync(paragraph.GetPlainText());
                        }
                        else if (block is BreakBlock breakBlock)
                        {
                            if (breakBlock.BreakType == BreakType.Page)
                                await writer.WriteLineAsync("\f"); // form feed
                        }
                    }
                }

                return ExportResult.Ok(outputPath);
            }
            catch (Exception ex)
            {
                return ExportResult.Fail(ex.Message);
            }
        }

        /// <summary>
        /// Экспортирует документ в Markdown (.md).
        /// Заголовки маппируются по стилю абзаца (Heading1 → #, Heading2 → ## и т.д.).
        /// Форматирование символов: жирный → **text**, курсив → *text*.
        /// Writersword-специфичные метки (персонажи, таймлайн) теряются.
        /// </summary>
        public async Task<ExportResult> ExportToMarkdownAsync(DocumentModel document, string outputPath)
        {
            try
            {
                var sb = new StringBuilder();

                foreach (var section in document.Sections)
                {
                    foreach (var block in section.Blocks)
                    {
                        if (block is ParagraphBlock paragraph)
                        {
                            string mdLine = ConvertParagraphToMarkdown(paragraph);
                            sb.AppendLine(mdLine);
                        }
                        else if (block is BreakBlock b && b.BreakType == BreakType.Page)
                        {
                            sb.AppendLine();
                            sb.AppendLine("---");
                            sb.AppendLine();
                        }
                    }
                }

                await File.WriteAllTextAsync(outputPath, sb.ToString(), Encoding.UTF8);
                return ExportResult.Ok(outputPath);
            }
            catch (Exception ex)
            {
                return ExportResult.Fail(ex.Message);
            }
        }

        // ── Экспорт в .docx ─────────────────────────────────────────────────

        /// <summary>
        /// Экспортирует документ в .docx через DocumentFormat.OpenXml.
        /// Стили документа переносятся в styles.xml как есть (вместе с цепочкой BasedOn) —
        /// Word разрешает наследование сам, поэтому свойства абзацев и ранов пишутся
        /// только там, где заданы явно, ровно как они хранятся в модели.
        /// Поддерживается: разделы с собственными параметрами страницы и колонок,
        /// стили, списки (маркированные и нумерованные), таблицы (объединение ячеек,
        /// границы, заливка, выравнивание), картинки в тексте, разрывы страницы и колонки.
        /// Не переносится (с предупреждением в <see cref="ExportResult.Warnings"/>):
        /// плавающие объекты (картинки с обтеканием, фигуры, надписи), колонтитулы,
        /// Writersword-специфичные аннотации (персонажи, таймлайн, закладки).
        /// Внутренние отступы ячеек таблицы и сдвиг таблицы влево остаются словными
        /// по умолчанию: соответствующие атрибуты OOXML при импорте тоже не читаются,
        /// поэтому круговой обход документа от этого не страдает.
        /// </summary>
        /// <param name="document">Экспортируемый документ.</param>
        /// <param name="outputPath">Путь к создаваемому файлу .docx.</param>
        /// <param name="resolveImage">
        /// Возвращает байты картинки по её имени файла
        /// (<see cref="ImageBlock.ImageFileName"/>). Сервис экспорта не имеет доступа
        /// к хранилищу проекта, поэтому картинки достаёт вызывающий код. Null —
        /// картинки не встраиваются (в документе останутся пустые места с предупреждением).
        /// </param>
        public Task<ExportResult> ExportToDocxAsync(
            DocumentModel document,
            string outputPath,
            Func<string, byte[]?>? resolveImage = null)
        {
            return Task.Run(() =>
            {
                try
                {
                    return ExportToDocxCore(document, outputPath, resolveImage);
                }
                catch (Exception ex)
                {
                    return ExportResult.Fail(ex.Message);
                }
            });
        }

        private ExportResult ExportToDocxCore(
            DocumentModel document, string outputPath, Func<string, byte[]?>? resolveImage)
        {
            using (var wordDoc = WordprocessingDocument.Create(outputPath, WordprocessingDocumentType.Document))
            {
                var mainPart = wordDoc.AddMainDocumentPart();
                var body = new W.Body();
                mainPart.Document = new W.Document(body);

                var ctx = new DocxWriteContext
                {
                    MainPart = mainPart,
                    ResolveImage = resolveImage
                };

                WriteStyleDefinitions(mainPart, document);

                ctx.Numbering = new DocxNumberingBuilder();
                ctx.Numbering.Collect(document);
                ctx.Numbering.Write(mainPart);

                for (int s = 0; s < document.Sections.Count; s++)
                {
                    var section = document.Sections[s];
                    bool isFinal = s == document.Sections.Count - 1;

                    ctx.InlineObjects = BuildInlineObjectMap(section);

                    if (section.FloatingObjects.Count > 0)
                        ctx.Warnings.Add(
                            "Плавающие объекты (картинки с обтеканием, фигуры, надписи) не переносятся в .docx.");

                    if (section.Header.IsEnabled || section.Footer.IsEnabled)
                        ctx.Warnings.Add("Колонтитулы не переносятся в .docx.");

                    W.Paragraph? lastParagraph = null;

                    foreach (var block in section.Blocks)
                    {
                        switch (block)
                        {
                            case ParagraphBlock para:
                                lastParagraph = BuildParagraph(para, ctx);
                                body.AppendChild(lastParagraph);
                                break;

                            case TableBlock table:
                                body.AppendChild(BuildTable(table, ctx));

                                // После таблицы в OOXML обязан идти абзац, иначе Word
                                // считает файл повреждённым.
                                lastParagraph = new W.Paragraph();
                                body.AppendChild(lastParagraph);
                                break;

                            case BreakBlock brk:
                                lastParagraph = BuildBreakParagraph(brk);
                                body.AppendChild(lastParagraph);
                                break;

                            case ImageBlock:
                            case ShapeBlock:
                            case FloatingTextBlock:
                                ctx.Warnings.Add(
                                    "Плавающие объекты (картинки с обтеканием, фигуры, надписи) не переносятся в .docx.");
                                break;
                        }
                    }

                    var sectPr = BuildSectionProperties(section, document);

                    if (isFinal)
                    {
                        // Параметры последнего раздела живут прямо в body.
                        body.AppendChild(sectPr);
                    }
                    else
                    {
                        // Параметры остальных разделов — в свойствах последнего абзаца раздела.
                        if (lastParagraph is null)
                        {
                            lastParagraph = new W.Paragraph();
                            body.AppendChild(lastParagraph);
                        }

                        var pPr = lastParagraph.GetFirstChild<W.ParagraphProperties>();
                        if (pPr is null)
                        {
                            pPr = new W.ParagraphProperties();
                            lastParagraph.InsertAt(pPr, 0);
                        }
                        pPr.AppendChild(sectPr);
                    }
                }

                if (!body.Elements<W.Paragraph>().Any() && !body.Elements<W.Table>().Any())
                    body.InsertAt(new W.Paragraph(), 0);

                mainPart.Document.Save();

                return ExportResult.Ok(outputPath, ctx.Warnings.Distinct().ToArray());
            }
        }

        private static Dictionary<Guid, ImageBlock> BuildInlineObjectMap(SectionModel section)
        {
            var map = new Dictionary<Guid, ImageBlock>();
            foreach (var obj in section.InlineObjects)
                if (obj is ImageBlock image)
                    map[image.Id] = image;
            return map;
        }

        // ── docx: стили ─────────────────────────────────────────────────────

        private void WriteStyleDefinitions(MainDocumentPart mainPart, DocumentModel document)
        {
            var stylePart = mainPart.AddNewPart<StyleDefinitionsPart>();
            var styles = new W.Styles();

            foreach (var style in document.Styles)
            {
                if (string.IsNullOrWhiteSpace(style.Name)) continue;
                styles.AppendChild(BuildStyle(style));
            }

            stylePart.Styles = styles;
            stylePart.Styles.Save();
        }

        private W.Style BuildStyle(DocumentStyle style)
        {
            bool isCharacter = style.StyleType == DocumentStyleType.Character;

            var result = new W.Style
            {
                Type = new EnumValue<W.StyleValues>(
                    isCharacter ? W.StyleValues.Character : W.StyleValues.Paragraph),
                StyleId = style.Name,
                CustomStyle = !style.IsBuiltIn
            };

            result.AppendChild(new W.StyleName
            {
                Val = string.IsNullOrWhiteSpace(style.DisplayName) ? style.Name : style.DisplayName
            });

            if (string.Equals(style.Name, "Normal", StringComparison.OrdinalIgnoreCase))
                result.Default = true;

            if (!string.IsNullOrWhiteSpace(style.BasedOn))
                result.AppendChild(new W.BasedOn { Val = style.BasedOn });

            if (!isCharacter)
            {
                var pPr = new W.StyleParagraphProperties();
                foreach (var element in BuildParagraphPropertyElements(
                    style.ParagraphProperties, null, null, StyleOutlineLevel(style)))
                {
                    // Ссылка на стиль внутри самого стиля недопустима.
                    if (element is W.ParagraphStyleId) continue;
                    pPr.AppendChild(element);
                }

                if (pPr.HasChildren) result.AppendChild(pPr);
            }

            var rPr = new W.StyleRunProperties();
            foreach (var element in BuildRunPropertyElements(style.RunProperties))
                rPr.AppendChild(element);

            if (rPr.HasChildren) result.AppendChild(rPr);

            return result;
        }

        /// <summary>
        /// Уровень структуры для стиля заголовка (0-based, как в OOXML) или null.
        /// Встроенные стили Writersword несут уровень в имени (Heading1…Heading6),
        /// пользовательские могут задать его явно через OutlineLevel (1-based).
        /// Уровень нужен, чтобы обратный импорт этого же файла опознал заголовки
        /// по w:outlineLvl — самому надёжному признаку, не зависящему от языка Word.
        /// </summary>
        private static int? StyleOutlineLevel(DocumentStyle style)
        {
            int explicitLevel = style.ParagraphProperties?.OutlineLevel ?? 0;
            if (explicitLevel > 0) return Math.Clamp(explicitLevel - 1, 0, 8);

            var match = System.Text.RegularExpressions.Regex.Match(
                style.Name ?? string.Empty, @"^Heading([1-9])$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (match.Success && int.TryParse(match.Groups[1].Value, out int level))
                return Math.Clamp(level - 1, 0, 8);

            return null;
        }

        // ── docx: свойства абзаца и рана ────────────────────────────────────

        /// <summary>
        /// Элементы w:pPr в порядке, требуемом схемой CT_PPr:
        /// pStyle, keepNext, keepLines, pageBreakBefore, numPr, spacing, ind, jc, outlineLvl.
        /// </summary>
        private List<OpenXmlElement> BuildParagraphPropertyElements(
            Models.Styles.ParagraphProperties? props,
            ListProperties? list,
            DocxNumberingBuilder? numbering,
            int? outlineLevelOverride)
        {
            var elements = new List<OpenXmlElement>();
            if (props is null && list is null && outlineLevelOverride is null) return elements;

            if (props is not null && !string.IsNullOrWhiteSpace(props.StyleName))
                elements.Add(new W.ParagraphStyleId { Val = props.StyleName });

            if (props?.KeepWithNext == true) elements.Add(new W.KeepNext());
            if (props?.KeepTogether == true) elements.Add(new W.KeepLines());
            if (props?.PageBreakBefore == true) elements.Add(new W.PageBreakBefore());

            if (list is not null && numbering is not null)
            {
                int? numId = numbering.GetNumberingId(list.ListId);
                if (numId is int nid)
                {
                    elements.Add(new W.NumberingProperties(
                        new W.NumberingLevelReference { Val = Math.Clamp(list.Level, 0, 8) },
                        new W.NumberingId { Val = nid }));
                }
            }

            var spacing = BuildSpacing(props);
            if (spacing is not null) elements.Add(spacing);

            var indentation = BuildIndentation(props, list);
            if (indentation is not null) elements.Add(indentation);

            if (props?.Alignment is TextAlignment alignment)
                elements.Add(new W.Justification { Val = new EnumValue<W.JustificationValues>(MapAlignment(alignment)) });

            int? outline = outlineLevelOverride;
            if (outline is null && props is not null && props.OutlineLevel > 0)
                outline = Math.Clamp(props.OutlineLevel - 1, 0, 8);

            if (outline is int outlineValue)
                elements.Add(new W.OutlineLevel { Val = outlineValue });

            return elements;
        }

        private W.SpacingBetweenLines? BuildSpacing(Models.Styles.ParagraphProperties? props)
        {
            if (props is null) return null;
            if (props.SpaceBefore is null && props.SpaceAfter is null && props.LineSpacingValue is null)
                return null;

            var spacing = new W.SpacingBetweenLines();

            if (props.SpaceBefore is double before)
                spacing.Before = TwipsString(before);

            if (props.SpaceAfter is double after)
                spacing.After = TwipsString(after);

            if (props.LineSpacingValue is double lineValue)
            {
                var rule = props.LineSpacingRule ?? Models.Styles.LineSpacingRule.Auto;
                if (rule == Models.Styles.LineSpacingRule.Auto)
                {
                    // Множитель хранится в 240-х долях строки: 240 = одинарный.
                    spacing.Line = Math.Round(lineValue * 240.0)
                        .ToString(CultureInfo.InvariantCulture);
                    spacing.LineRule = new EnumValue<W.LineSpacingRuleValues>(W.LineSpacingRuleValues.Auto);
                }
                else
                {
                    spacing.Line = TwipsString(lineValue);
                    spacing.LineRule = new EnumValue<W.LineSpacingRuleValues>(
                        rule == Models.Styles.LineSpacingRule.Exact
                            ? W.LineSpacingRuleValues.Exact
                            : W.LineSpacingRuleValues.AtLeast);
                }
            }

            return spacing;
        }

        private W.Indentation? BuildIndentation(
            Models.Styles.ParagraphProperties? props, ListProperties? list)
        {
            double left = props?.LeftIndent ?? 0;
            double right = props?.RightIndent ?? 0;
            double firstLine = props?.FirstLineIndent ?? 0;

            bool hasAny = props?.LeftIndent is not null
                || props?.RightIndent is not null
                || props?.FirstLineIndent is not null;

            if (list is not null)
            {
                // Отступы элемента списка задаёт сам список: текст сдвинут вправо,
                // маркер висит слева от него.
                left += list.EffectiveTextIndentPt();
                firstLine = -(list.EffectiveTextIndentPt() - list.EffectiveMarkerIndentPt());
                hasAny = true;
            }

            if (!hasAny) return null;

            var indentation = new W.Indentation();

            if (left != 0) indentation.Left = TwipsString(left);
            if (right != 0) indentation.Right = TwipsString(right);

            if (firstLine < 0) indentation.Hanging = TwipsString(-firstLine);
            else if (firstLine > 0) indentation.FirstLine = TwipsString(firstLine);

            return indentation;
        }

        /// <summary>
        /// Элементы w:rPr в порядке, требуемом схемой CT_RPr:
        /// rFonts, b, i, caps, smallCaps, strike, color, sz, szCs, u, shd, vertAlign, lang.
        /// </summary>
        /// <param name="props">Свойства рана или стиля. Null — элементов нет.</param>
        /// <param name="writeExplicitToggles">
        /// Писать выключенные начертания явно (w:val="0"). Нужно для ранов: объект
        /// свойств рана несёт полное состояние начертаний, и без явного выключения
        /// не жирный текст внутри жирного стиля абзаца стал бы в Word жирным.
        /// Для стилей выключенные начертания не пишутся — иначе стиль перебивал бы
        /// то, что задано его базовым стилем.
        /// </param>
        private List<OpenXmlElement> BuildRunPropertyElements(
            Models.Inline.RunProperties? props, bool writeExplicitToggles = false)
        {
            var elements = new List<OpenXmlElement>();
            if (props is null) return elements;

            if (!string.IsNullOrWhiteSpace(props.FontFamily))
            {
                elements.Add(new W.RunFonts
                {
                    Ascii = props.FontFamily,
                    HighAnsi = props.FontFamily,
                    ComplexScript = props.FontFamily
                });
            }

            if (props.IsBold) elements.Add(new W.Bold());
            else if (writeExplicitToggles) elements.Add(new W.Bold { Val = false });

            if (props.IsItalic) elements.Add(new W.Italic());
            else if (writeExplicitToggles) elements.Add(new W.Italic { Val = false });

            if (props.IsAllCaps) elements.Add(new W.Caps());
            else if (writeExplicitToggles) elements.Add(new W.Caps { Val = false });

            if (props.IsSmallCaps) elements.Add(new W.SmallCaps());
            else if (writeExplicitToggles) elements.Add(new W.SmallCaps { Val = false });

            if (props.IsStrikethrough) elements.Add(new W.Strike());
            else if (writeExplicitToggles) elements.Add(new W.Strike { Val = false });

            string? textColor = HexWithoutHash(props.TextColor);
            if (textColor is not null) elements.Add(new W.Color { Val = textColor });

            if (props.FontSize is double size && size > 0)
            {
                string halfPoints = Math.Round(size * HalfPointsPerPoint)
                    .ToString(CultureInfo.InvariantCulture);
                elements.Add(new W.FontSize { Val = halfPoints });
                elements.Add(new W.FontSizeComplexScript { Val = halfPoints });
            }

            if (props.IsUnderline)
            {
                // Значение обязательно: элемент w:u без w:val читается как «подчёркивания нет».
                elements.Add(new W.Underline { Val = new EnumValue<W.UnderlineValues>(W.UnderlineValues.Single) });
            }
            else if (writeExplicitToggles)
            {
                elements.Add(new W.Underline { Val = new EnumValue<W.UnderlineValues>(W.UnderlineValues.None) });
            }

            string? highlight = HexWithoutHash(props.HighlightColor);
            if (highlight is not null)
            {
                // w:highlight принимает лишь фиксированный набор именованных цветов,
                // поэтому произвольный цвет маркера пишется заливкой рана — её же
                // читает импорт, когда w:highlight отсутствует.
                elements.Add(new W.Shading
                {
                    Val = new EnumValue<W.ShadingPatternValues>(W.ShadingPatternValues.Clear),
                    Color = "auto",
                    Fill = highlight
                });
            }

            if (props.IsSuperscript)
            {
                elements.Add(new W.VerticalTextAlignment
                {
                    Val = new EnumValue<W.VerticalPositionValues>(W.VerticalPositionValues.Superscript)
                });
            }
            else if (props.IsSubscript)
            {
                elements.Add(new W.VerticalTextAlignment
                {
                    Val = new EnumValue<W.VerticalPositionValues>(W.VerticalPositionValues.Subscript)
                });
            }
            else if (writeExplicitToggles)
            {
                elements.Add(new W.VerticalTextAlignment
                {
                    Val = new EnumValue<W.VerticalPositionValues>(W.VerticalPositionValues.Baseline)
                });
            }

            if (!string.IsNullOrWhiteSpace(props.Language))
                elements.Add(new W.Languages { Val = props.Language });

            return elements;
        }

        // ── docx: абзацы, раны, картинки ────────────────────────────────────

        private W.Paragraph BuildParagraph(ParagraphBlock para, DocxWriteContext ctx)
        {
            var result = new W.Paragraph();

            var propertyElements = BuildParagraphPropertyElements(
                para.Properties, para.ListProperties, ctx.Numbering, null);

            if (propertyElements.Count > 0)
            {
                var pPr = new W.ParagraphProperties();
                foreach (var element in propertyElements) pPr.AppendChild(element);
                result.AppendChild(pPr);
            }

            foreach (var chunk in para.Chunks)
            {
                foreach (var run in chunk.Runs)
                {
                    foreach (var element in BuildRunElements(run, ctx))
                        result.AppendChild(element);
                }
            }

            return result;
        }

        private List<OpenXmlElement> BuildRunElements(RunModel run, DocxWriteContext ctx)
        {
            var elements = new List<OpenXmlElement>();

            if (run.InlineImageId is Guid imageId)
            {
                var drawing = BuildImageDrawing(imageId, ctx);
                if (drawing is null) return elements;

                var imageRun = new W.Run();
                var imageProps = BuildRunPropertyElements(run.Properties, writeExplicitToggles: true);
                if (imageProps.Count > 0)
                {
                    var rPr = new W.RunProperties();
                    foreach (var element in imageProps) rPr.AppendChild(element);
                    imageRun.AppendChild(rPr);
                }

                imageRun.AppendChild(drawing);
                elements.Add(imageRun);
                return elements;
            }

            string text = run.Text ?? string.Empty;
            if (text.Length == 0)
            {
                return elements;
            }

            var wordRun = new W.Run();
            var runProperties = BuildRunPropertyElements(run.Properties, writeExplicitToggles: true);
            if (runProperties.Count > 0)
            {
                var rPr = new W.RunProperties();
                foreach (var element in runProperties) rPr.AppendChild(element);
                wordRun.AppendChild(rPr);
            }

            // Перевод строки внутри абзаца в модели — обычный символ; в OOXML это
            // отдельный элемент w:br, иначе Word покажет текст одной строкой.
            var segments = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            for (int i = 0; i < segments.Length; i++)
            {
                if (i > 0) wordRun.AppendChild(new W.Break());

                foreach (var piece in SplitByTabs(segments[i]))
                {
                    if (piece.Length == 0) continue;

                    if (piece == "\t")
                    {
                        wordRun.AppendChild(new W.TabChar());
                        continue;
                    }

                    wordRun.AppendChild(new W.Text(piece)
                    {
                        Space = new EnumValue<SpaceProcessingModeValues>(SpaceProcessingModeValues.Preserve)
                    });
                }
            }

            elements.Add(wordRun);
            return elements;
        }

        private static IEnumerable<string> SplitByTabs(string text)
        {
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '\t') continue;

                if (i > start) yield return text.Substring(start, i - start);
                yield return "\t";
                start = i + 1;
            }

            if (start < text.Length) yield return text.Substring(start);
        }

        private W.Paragraph BuildBreakParagraph(BreakBlock block)
        {
            // Разрывы раздела в потоке блоков переносятся как разрыв страницы:
            // разделы документа описываются списком SectionModel, и импорт
            // приводит внутренние разрывы разделов Word к тем же разрывам страницы.
            var breakType = block.BreakType == BreakType.Column
                ? W.BreakValues.Column
                : W.BreakValues.Page;

            return new W.Paragraph(new W.Run(new W.Break
            {
                Type = new EnumValue<W.BreakValues>(breakType)
            }));
        }

        private W.Drawing? BuildImageDrawing(Guid imageId, DocxWriteContext ctx)
        {
            if (!ctx.InlineObjects.TryGetValue(imageId, out var image))
                return null;

            string? relationshipId = EnsureImagePart(image, ctx);
            if (relationshipId is null) return null;

            long cx = (long)Math.Round(Math.Max(image.WidthPt, 1) * EmuPerPoint);
            long cy = (long)Math.Round(Math.Max(image.HeightPt, 1) * EmuPerPoint);

            uint drawingId = ctx.NextDrawingId++;
            string name = string.IsNullOrWhiteSpace(image.ImageFileName)
                ? "Picture " + drawingId.ToString(CultureInfo.InvariantCulture)
                : image.ImageFileName;

            var picture = new Pic.Picture(
                new Pic.NonVisualPictureProperties(
                    new Pic.NonVisualDrawingProperties
                    {
                        Id = (UInt32Value)0U,
                        Name = name,
                        Description = image.AltText ?? string.Empty
                    },
                    new Pic.NonVisualPictureDrawingProperties()),
                new Pic.BlipFill(
                    new Dr.Blip { Embed = relationshipId },
                    new Dr.Stretch(new Dr.FillRectangle())),
                new Pic.ShapeProperties(
                    new Dr.Transform2D(
                        new Dr.Offset { X = 0L, Y = 0L },
                        new Dr.Extents { Cx = cx, Cy = cy }),
                    new Dr.PresetGeometry(new Dr.AdjustValueList())
                    {
                        Preset = new EnumValue<Dr.ShapeTypeValues>(Dr.ShapeTypeValues.Rectangle)
                    }));

            var inline = new Wp.Inline(
                new Wp.Extent { Cx = cx, Cy = cy },
                new Wp.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
                new Wp.DocProperties
                {
                    Id = (UInt32Value)drawingId,
                    Name = name
                },
                new Wp.NonVisualGraphicFrameDrawingProperties(
                    new Dr.GraphicFrameLocks { NoChangeAspect = true }),
                new Dr.Graphic(
                    new Dr.GraphicData(picture)
                    {
                        Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture"
                    }));

            return new W.Drawing(inline);
        }

        /// <summary>
        /// Кладёт файл картинки в пакет один раз и возвращает id связи.
        /// Повторные ссылки на ту же картинку переиспользуют готовую связь.
        /// </summary>
        private string? EnsureImagePart(ImageBlock image, DocxWriteContext ctx)
        {
            if (ctx.ImageRelationshipIds.TryGetValue(image.Id, out var existing))
                return existing;

            if (ctx.ResolveImage is null || string.IsNullOrWhiteSpace(image.ImageFileName))
            {
                ctx.Warnings.Add("Картинки не встроены: содержимое файлов недоступно.");
                return null;
            }

            byte[]? data;
            try
            {
                data = ctx.ResolveImage(image.ImageFileName);
            }
            catch
            {
                data = null;
            }

            if (data is null || data.Length == 0)
            {
                ctx.Warnings.Add($"Картинка \"{image.ImageFileName}\" не найдена и пропущена.");
                return null;
            }

            var partType = ImagePartTypeFor(image.ImageFileName);
            if (partType is null)
            {
                ctx.Warnings.Add(
                    $"Картинка \"{image.ImageFileName}\" в неподдерживаемом Word формате и пропущена.");
                return null;
            }

            var imagePart = ctx.MainPart.AddImagePart(partType.Value);
            using (var stream = new MemoryStream(data, false))
            {
                imagePart.FeedData(stream);
            }

            string relationshipId = ctx.MainPart.GetIdOfPart(imagePart);
            ctx.ImageRelationshipIds[image.Id] = relationshipId;
            return relationshipId;
        }

        private static PartTypeInfo? ImagePartTypeFor(string fileName)
        {
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            return ext switch
            {
                ".png" => ImagePartType.Png,
                ".jpg" => ImagePartType.Jpeg,
                ".jpeg" => ImagePartType.Jpeg,
                ".gif" => ImagePartType.Gif,
                ".bmp" => ImagePartType.Bmp,
                ".tif" => ImagePartType.Tiff,
                ".tiff" => ImagePartType.Tiff,
                ".ico" => ImagePartType.Icon,
                ".emf" => ImagePartType.Emf,
                ".wmf" => ImagePartType.Wmf,
                _ => null
            };
        }

        // ── docx: таблицы ───────────────────────────────────────────────────

        private W.Table BuildTable(TableBlock table, DocxWriteContext ctx)
        {
            var result = new W.Table();

            int columnCount = Math.Max(table.ColumnCount, 1);
            int rowCount = Math.Max(table.RowCount, 1);

            var tableProperties = new W.TableProperties(
                new W.TableWidth
                {
                    Type = new EnumValue<W.TableWidthUnitValues>(W.TableWidthUnitValues.Pct),
                    Width = Math.Round(Math.Clamp(table.WidthPercent, 1, 100) * 50)
                        .ToString(CultureInfo.InvariantCulture)
                },
                BuildTableBorders(table),
                new W.TableLook { Val = "04A0" });

            result.AppendChild(tableProperties);

            // Ширины столбцов OOXML задаёт в twips. Проценты модели раскладываются
            // на условную ширину страницы: реальную ширину задаёт tblW в процентах,
            // а сетка задаёт пропорции между столбцами.
            const double gridTotalTwips = 9360.0; // ширина текста на A4 при полях по умолчанию
            var grid = new W.TableGrid();
            var columnShares = ColumnShares(table, columnCount);

            for (int c = 0; c < columnCount; c++)
            {
                long width = (long)Math.Round(gridTotalTwips * columnShares[c]);
                if (width < 1) width = 1;
                grid.AppendChild(new W.GridColumn { Width = width.ToString(CultureInfo.InvariantCulture) });
            }

            result.AppendChild(grid);

            for (int row = 0; row < rowCount; row++)
            {
                var tableRow = new W.TableRow();

                if (table.RepeatHeader && row == 0)
                    tableRow.AppendChild(new W.TableRowProperties(new W.TableHeader()));

                int column = 0;
                while (column < columnCount)
                {
                    var cell = table.GetCell(row, column);

                    if (cell is null)
                    {
                        tableRow.AppendChild(BuildEmptyCell(columnShares, column, 1, gridTotalTwips));
                        column++;
                        continue;
                    }

                    int span = Math.Clamp(cell.ColSpan, 1, columnCount - column);

                    if (cell.Row == row && cell.Column == column)
                    {
                        tableRow.AppendChild(BuildTableCell(
                            cell, span, isVerticalMergeContinuation: false, columnShares, column, gridTotalTwips, ctx));
                    }
                    else if (cell.Column == column)
                    {
                        // Продолжение вертикального объединения: ячейка есть, но
                        // содержимое лежит в её «главной» строке.
                        tableRow.AppendChild(BuildTableCell(
                            cell, span, isVerticalMergeContinuation: true, columnShares, column, gridTotalTwips, ctx));
                    }
                    else
                    {
                        // Позиция перекрыта горизонтальным объединением соседней ячейки.
                        column++;
                        continue;
                    }

                    column += span;
                }

                if (!tableRow.Elements<W.TableCell>().Any())
                    tableRow.AppendChild(BuildEmptyCell(columnShares, 0, 1, gridTotalTwips));

                result.AppendChild(tableRow);
            }

            return result;
        }

        private static double[] ColumnShares(TableBlock table, int columnCount)
        {
            var shares = new double[columnCount];
            double assigned = 0;
            int autoCount = 0;

            for (int c = 0; c < columnCount; c++)
            {
                var definition = c < table.Columns.Count ? table.Columns[c] : null;

                if (definition is null || definition.WidthType == TableColumnWidthType.Auto)
                {
                    shares[c] = -1;
                    autoCount++;
                    continue;
                }

                double share = definition.WidthType == TableColumnWidthType.Percent
                    ? definition.WidthValue / 100.0
                    : definition.WidthValue / Math.Max(TotalFixedWidthMm(table), 1.0);

                if (share <= 0) { shares[c] = -1; autoCount++; continue; }

                shares[c] = share;
                assigned += share;
            }

            double rest = Math.Max(1.0 - assigned, 0);
            double perAuto = autoCount > 0 ? rest / autoCount : 0;

            for (int c = 0; c < columnCount; c++)
                if (shares[c] < 0)
                    shares[c] = autoCount > 0 && rest > 0 ? perAuto : 1.0 / columnCount;

            double total = shares.Sum();
            if (total <= 0)
            {
                for (int c = 0; c < columnCount; c++) shares[c] = 1.0 / columnCount;
                return shares;
            }

            for (int c = 0; c < columnCount; c++) shares[c] /= total;
            return shares;
        }

        private static double TotalFixedWidthMm(TableBlock table)
        {
            double total = 0;
            foreach (var column in table.Columns)
                if (column.WidthType == TableColumnWidthType.Fixed)
                    total += column.WidthValue;
            return total;
        }

        private W.TableCell BuildEmptyCell(
            double[] shares, int column, int span, double gridTotalTwips)
        {
            var cell = new W.TableCell();
            cell.AppendChild(new W.TableCellProperties(
                new W.TableCellWidth
                {
                    Type = new EnumValue<W.TableWidthUnitValues>(W.TableWidthUnitValues.Dxa),
                    Width = CellWidthTwips(shares, column, span, gridTotalTwips)
                }));
            cell.AppendChild(new W.Paragraph());
            return cell;
        }

        private W.TableCell BuildTableCell(
            Models.Document.TableCell cell,
            int span,
            bool isVerticalMergeContinuation,
            double[] shares,
            int column,
            double gridTotalTwips,
            DocxWriteContext ctx)
        {
            var result = new W.TableCell();

            // Порядок элементов w:tcPr задан схемой: tcW, gridSpan, vMerge, tcBorders, shd, vAlign.
            var properties = new W.TableCellProperties();

            properties.AppendChild(new W.TableCellWidth
            {
                Type = new EnumValue<W.TableWidthUnitValues>(W.TableWidthUnitValues.Dxa),
                Width = CellWidthTwips(shares, column, span, gridTotalTwips)
            });

            if (span > 1)
                properties.AppendChild(new W.GridSpan { Val = span });

            if (cell.RowSpan > 1)
            {
                properties.AppendChild(new W.VerticalMerge
                {
                    Val = new EnumValue<W.MergedCellValues>(
                        isVerticalMergeContinuation ? W.MergedCellValues.Continue : W.MergedCellValues.Restart)
                });
            }

            var borders = BuildCellBorders(cell.Borders);
            if (borders is not null) properties.AppendChild(borders);

            string? background = HexWithoutHash(cell.BackgroundColor);
            if (background is not null)
            {
                properties.AppendChild(new W.Shading
                {
                    Val = new EnumValue<W.ShadingPatternValues>(W.ShadingPatternValues.Clear),
                    Color = "auto",
                    Fill = background
                });
            }

            if (cell.VerticalAlignment != Models.Document.VerticalAlignment.Top)
            {
                properties.AppendChild(new W.TableCellVerticalAlignment
                {
                    Val = new EnumValue<W.TableVerticalAlignmentValues>(
                        cell.VerticalAlignment == Models.Document.VerticalAlignment.Middle
                            ? W.TableVerticalAlignmentValues.Center
                            : W.TableVerticalAlignmentValues.Bottom)
                });
            }

            result.AppendChild(properties);

            if (isVerticalMergeContinuation)
            {
                // Содержимое объединённой ячейки хранится в её первой строке.
                result.AppendChild(new W.Paragraph());
                return result;
            }

            if (cell.Paragraphs.Count == 0)
            {
                result.AppendChild(new W.Paragraph());
                return result;
            }

            foreach (var paragraph in cell.Paragraphs)
                result.AppendChild(BuildParagraph(paragraph, ctx));

            return result;
        }

        private static string CellWidthTwips(double[] shares, int column, int span, double gridTotalTwips)
        {
            double share = 0;
            for (int c = column; c < column + span && c < shares.Length; c++)
                share += shares[c];

            long width = (long)Math.Round(gridTotalTwips * share);
            if (width < 1) width = 1;
            return width.ToString(CultureInfo.InvariantCulture);
        }

        private W.TableBorders BuildTableBorders(TableBlock table)
        {
            // Границы таблицы по умолчанию берутся от первой ячейки: в модели
            // границы живут на ячейках, у таблицы собственного набора нет.
            var sample = table.Cells.Count > 0 ? table.Cells[0].Borders : new CellBorders();

            var borders = new W.TableBorders();
            ApplyBorder(new W.TopBorder(), sample.Top, sample.ThicknessPt, sample.Color, borders);
            ApplyBorder(new W.LeftBorder(), sample.Left, sample.ThicknessPt, sample.Color, borders);
            ApplyBorder(new W.BottomBorder(), sample.Bottom, sample.ThicknessPt, sample.Color, borders);
            ApplyBorder(new W.RightBorder(), sample.Right, sample.ThicknessPt, sample.Color, borders);
            ApplyBorder(new W.InsideHorizontalBorder(), sample.Top, sample.ThicknessPt, sample.Color, borders);
            ApplyBorder(new W.InsideVerticalBorder(), sample.Left, sample.ThicknessPt, sample.Color, borders);
            return borders;
        }

        private W.TableCellBorders? BuildCellBorders(CellBorders? source)
        {
            if (source is null) return null;

            var borders = new W.TableCellBorders();
            ApplyBorder(new W.TopBorder(), source.Top, source.ThicknessPt, source.Color, borders);
            ApplyBorder(new W.LeftBorder(), source.Left, source.ThicknessPt, source.Color, borders);
            ApplyBorder(new W.BottomBorder(), source.Bottom, source.ThicknessPt, source.Color, borders);
            ApplyBorder(new W.RightBorder(), source.Right, source.ThicknessPt, source.Color, borders);
            return borders;
        }

        private static void ApplyBorder(
            W.BorderType border, BorderStyle style, double thicknessPt, string? color, OpenXmlElement parent)
        {
            border.Val = new EnumValue<W.BorderValues>(MapBorderStyle(style));

            uint eighths = (uint)Math.Clamp(Math.Round(thicknessPt * EighthsPerPoint), 2, 96);
            border.Size = (UInt32Value)eighths;
            border.Space = (UInt32Value)0U;
            border.Color = HexWithoutHash(color) ?? "auto";

            parent.AppendChild(border);
        }

        private static W.BorderValues MapBorderStyle(BorderStyle style) => style switch
        {
            BorderStyle.None => W.BorderValues.None,
            BorderStyle.Double => W.BorderValues.Double,
            BorderStyle.Dashed => W.BorderValues.Dashed,
            BorderStyle.Dotted => W.BorderValues.Dotted,
            BorderStyle.Thick => W.BorderValues.Thick,
            _ => W.BorderValues.Single
        };

        // ── docx: параметры раздела ─────────────────────────────────────────

        private W.SectionProperties BuildSectionProperties(SectionModel section, DocumentModel document)
        {
            var pageSettings = section.PageSettings ?? document.PageSettings;
            var columnSettings = section.ColumnSettings ?? document.ColumnSettings;

            var result = new W.SectionProperties();

            // Порядок элементов w:sectPr задан схемой: type, pgSz, pgMar, cols.
            result.AppendChild(new W.SectionType
            {
                Val = new EnumValue<W.SectionMarkValues>(W.SectionMarkValues.NextPage)
            });

            bool landscape = pageSettings.Orientation == PageOrientation.Landscape;

            var pageSize = new W.PageSize
            {
                Width = (UInt32Value)(uint)Math.Round(pageSettings.GetPhysicalWidthMm() * TwipsPerMm),
                Height = (UInt32Value)(uint)Math.Round(pageSettings.GetPhysicalHeightMm() * TwipsPerMm)
            };

            if (landscape)
                pageSize.Orient = new EnumValue<W.PageOrientationValues>(W.PageOrientationValues.Landscape);

            result.AppendChild(pageSize);

            result.AppendChild(new W.PageMargin
            {
                Top = (int)Math.Round(pageSettings.MarginTopMm * TwipsPerMm),
                Bottom = (int)Math.Round(pageSettings.MarginBottomMm * TwipsPerMm),
                Left = (UInt32Value)(uint)Math.Round(pageSettings.MarginLeftMm * TwipsPerMm),
                Right = (UInt32Value)(uint)Math.Round(pageSettings.MarginRightMm * TwipsPerMm),
                Gutter = (UInt32Value)(uint)Math.Round(pageSettings.MarginGutterMm * TwipsPerMm),
                Header = (UInt32Value)(uint)Math.Round(pageSettings.HeaderDistanceMm * TwipsPerMm),
                Footer = (UInt32Value)(uint)Math.Round(pageSettings.FooterDistanceMm * TwipsPerMm)
            });

            var columns = new W.Columns
            {
                // w:num в SDK типизирован как Int16Value — приведение обязательно.
                ColumnCount = (Int16Value)(short)Math.Clamp(columnSettings.ColumnCount, 1, (int)short.MaxValue),
                Space = Math.Round(columnSettings.GapMm * TwipsPerMm).ToString(CultureInfo.InvariantCulture)
            };

            if (columnSettings.ColumnCount > 1 && columnSettings.ShowSeparator)
                columns.Separator = true;

            result.AppendChild(columns);

            return result;
        }

        // ── Экспорт в .pdf ──────────────────────────────────────────────────

        /// <summary>
        /// Экспортирует документ в .pdf собственным движком раскладки поверх SkiaSharp.
        /// Текст размечается заново по свойствам документа: физический размер страницы
        /// и поля раздела, стили абзацев и ранов, выравнивание, отступы, межстрочный
        /// интервал, списки с нумерацией, разрывы страниц, картинки в тексте и таблицы
        /// (включая объединение ячеек, границы, заливку и вертикальное выравнивание).
        /// Не переносится (с предупреждением в <see cref="ExportResult.Warnings"/>):
        /// плавающие объекты, колонтитулы, поворот и обрезка картинок.
        /// Таблица целиком переносится на следующую страницу, если не помещается
        /// на текущей; таблица выше страницы печатается без разбиения.
        /// </summary>
        /// <param name="document">Экспортируемый документ.</param>
        /// <param name="outputPath">Путь к создаваемому файлу .pdf.</param>
        /// <param name="resolveImage">
        /// Возвращает байты картинки по её имени файла
        /// (<see cref="ImageBlock.ImageFileName"/>). Null — картинки не рисуются.
        /// </param>
        public Task<ExportResult> ExportToPdfAsync(
            DocumentModel document,
            string outputPath,
            Func<string, byte[]?>? resolveImage = null)
        {
            return Task.Run(() =>
            {
                try
                {
                    using var engine = new PdfExportEngine(document, resolveImage);
                    engine.Export(outputPath);
                    return ExportResult.Ok(outputPath, engine.Warnings.Distinct().ToArray());
                }
                catch (Exception ex)
                {
                    return ExportResult.Fail(ex.Message);
                }
            });
        }

        // ── Общие вспомогательные методы ────────────────────────────────────

        private static string TwipsString(double points) =>
            Math.Round(points * TwipsPerPoint).ToString(CultureInfo.InvariantCulture);

        private static W.JustificationValues MapAlignment(TextAlignment alignment) => alignment switch
        {
            TextAlignment.Center => W.JustificationValues.Center,
            TextAlignment.Right => W.JustificationValues.Right,
            TextAlignment.Justify => W.JustificationValues.Both,
            _ => W.JustificationValues.Left
        };

        /// <summary>Цвет без ведущего «#»: OOXML хранит шестнадцатеричный цвет без него.</summary>
        internal static string? HexWithoutHash(string? color)
        {
            if (string.IsNullOrWhiteSpace(color)) return null;

            string value = color.TrimStart('#');
            if (value.Length == 8) value = value.Substring(2); // #AARRGGBB → RRGGBB
            if (value.Length != 6) return null;

            foreach (char c in value)
                if (!Uri.IsHexDigit(c)) return null;

            return value.ToUpperInvariant();
        }

        // --- Вспомогательные методы (Markdown) ---

        private static string ConvertParagraphToMarkdown(ParagraphBlock paragraph)
        {
            string? styleName = paragraph.Properties.StyleName;
            string plainText = paragraph.GetPlainText();

            string prefix = styleName switch
            {
                "Heading1" => "# ",
                "Heading2" => "## ",
                "Heading3" => "### ",
                "Heading4" => "#### ",
                "Heading5" => "##### ",
                "Heading6" => "###### ",
                "Quote" => "> ",
                "Code" => "    ",
                _ => string.Empty
            };

            if (string.IsNullOrEmpty(prefix))
            {
                // Применяем inline-форматирование для обычных абзацев.
                return ConvertRunsToMarkdown(paragraph);
            }

            return prefix + plainText;
        }

        private static string ConvertRunsToMarkdown(ParagraphBlock paragraph)
        {
            var sb = new StringBuilder();

            foreach (var chunk in paragraph.Chunks)
            {
                foreach (var run in chunk.Runs)
                {
                    // Картинка в строке: её символ-заполнитель в текстовом экспорте
                    // выглядел бы мусорным глифом. Заменяем ссылкой на альтернативный текст.
                    if (run.IsInlineObject)
                    {
                        sb.Append("![](");
                        sb.Append(run.InlineImageId);
                        sb.Append(')');
                        continue;
                    }

                    string text = run.Text;
                    if (run.Properties is null) { sb.Append(text); continue; }

                    bool bold = run.Properties.IsBold;
                    bool italic = run.Properties.IsItalic;
                    bool code = run.Properties.FontFamily == "Consolas"
                        || run.Properties.FontFamily == "Courier New";

                    if (code) { sb.Append('`').Append(text).Append('`'); continue; }
                    if (bold && italic) { sb.Append("***").Append(text).Append("***"); continue; }
                    if (bold) { sb.Append("**").Append(text).Append("**"); continue; }
                    if (italic) { sb.Append('*').Append(text).Append('*'); continue; }

                    sb.Append(text);
                }
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// Состояние записи одного .docx: часть-владелец, доступ к байтам картинок,
    /// уже созданные связи с картинками и накопленные предупреждения.
    /// </summary>
    internal sealed class DocxWriteContext
    {
        public MainDocumentPart MainPart = null!;
        public Func<string, byte[]?>? ResolveImage;
        public DocxNumberingBuilder Numbering = new();
        public Dictionary<Guid, ImageBlock> InlineObjects = new();
        public readonly Dictionary<Guid, string> ImageRelationshipIds = new();
        public readonly List<string> Warnings = new();
        public uint NextDrawingId = 1;
    }

    /// <summary>
    /// Собирает нумерацию документа и пишет numbering.xml.
    /// Один список Writersword (<see cref="ListProperties.ListId"/>) становится одним
    /// w:num с собственным w:abstractNum; уровни абстрактной нумерации заполняются
    /// по фактически встреченным в документе уровням этого списка.
    /// </summary>
    internal sealed class DocxNumberingBuilder
    {
        private const double TwipsPerPoint = 20.0;

        private readonly Dictionary<Guid, SortedDictionary<int, ListProperties>> _levelsByList = new();
        private readonly Dictionary<Guid, int> _numberingIdByList = new();

        public void Collect(DocumentModel document)
        {
            foreach (var section in document.Sections)
                CollectBlocks(section.Blocks);
        }

        private void CollectBlocks(IEnumerable<BlockModel> blocks)
        {
            foreach (var block in blocks)
            {
                switch (block)
                {
                    case ParagraphBlock paragraph:
                        CollectParagraph(paragraph);
                        break;

                    case TableBlock table:
                        foreach (var cell in table.Cells)
                            foreach (var paragraph in cell.Paragraphs)
                                CollectParagraph(paragraph);
                        break;
                }
            }
        }

        private void CollectParagraph(ParagraphBlock paragraph)
        {
            var list = paragraph.ListProperties;
            if (list is null || list.MarkerType == ListMarkerType.None) return;

            if (!_levelsByList.TryGetValue(list.ListId, out var levels))
            {
                levels = new SortedDictionary<int, ListProperties>();
                _levelsByList[list.ListId] = levels;
            }

            int level = Math.Clamp(list.Level, 0, 8);
            if (!levels.ContainsKey(level)) levels[level] = list;
        }

        public int? GetNumberingId(Guid listId) =>
            _numberingIdByList.TryGetValue(listId, out var value) ? value : null;

        public void Write(MainDocumentPart mainPart)
        {
            if (_levelsByList.Count == 0) return;

            var numberingPart = mainPart.AddNewPart<NumberingDefinitionsPart>();
            var numbering = new W.Numbering();

            // Схема требует, чтобы все w:abstractNum шли раньше всех w:num.
            var instances = new List<W.NumberingInstance>();
            int id = 1;

            foreach (var pair in _levelsByList)
            {
                var abstractNum = new W.AbstractNum { AbstractNumberId = id };
                abstractNum.AppendChild(new W.MultiLevelType
                {
                    Val = new EnumValue<W.MultiLevelValues>(W.MultiLevelValues.HybridMultilevel)
                });

                int maxLevel = pair.Value.Keys.Count > 0 ? pair.Value.Keys.Max() : 0;
                ListProperties? previous = null;

                for (int level = 0; level <= maxLevel; level++)
                {
                    var properties = pair.Value.TryGetValue(level, out var found) ? found : previous;
                    if (properties is null) continue;

                    previous = properties;
                    abstractNum.AppendChild(BuildLevel(properties, level));
                }

                numbering.AppendChild(abstractNum);

                instances.Add(new W.NumberingInstance(new W.AbstractNumId { Val = id })
                {
                    NumberID = id
                });

                _numberingIdByList[pair.Key] = id;
                id++;
            }

            foreach (var instance in instances)
                numbering.AppendChild(instance);

            numberingPart.Numbering = numbering;
            numberingPart.Numbering.Save();
        }

        /// <summary>
        /// Один уровень абстрактной нумерации. Порядок элементов задан схемой CT_Lvl:
        /// start, numFmt, lvlText, lvlJc, pPr.
        /// </summary>
        private W.Level BuildLevel(ListProperties properties, int level)
        {
            var (format, text) = MapMarker(properties, level);

            var result = new W.Level { LevelIndex = level };

            result.AppendChild(new W.StartNumberingValue { Val = Math.Max(properties.StartAt, 1) });
            result.AppendChild(new W.NumberingFormat { Val = new EnumValue<W.NumberFormatValues>(format) });
            result.AppendChild(new W.LevelText { Val = text });
            result.AppendChild(new W.LevelJustification
            {
                Val = new EnumValue<W.LevelJustificationValues>(W.LevelJustificationValues.Left)
            });

            double textIndent = properties.EffectiveTextIndentPt();
            double markerIndent = properties.EffectiveMarkerIndentPt();
            double hanging = Math.Max(textIndent - markerIndent, 0);

            var indentation = new W.Indentation
            {
                Left = Math.Round(textIndent * TwipsPerPoint).ToString(CultureInfo.InvariantCulture),
                Hanging = Math.Round(hanging * TwipsPerPoint).ToString(CultureInfo.InvariantCulture)
            };

            result.AppendChild(new W.PreviousParagraphProperties(indentation));

            return result;
        }

        private static (W.NumberFormatValues Format, string Text) MapMarker(ListProperties properties, int level)
        {
            var type = properties.LevelMarkers is not null
                && level >= 0 && level < properties.LevelMarkers.Count
                    ? properties.LevelMarkers[level]
                    : properties.MarkerType;

            string counted = (properties.NumberPrefix ?? string.Empty)
                + "%" + (level + 1).ToString(CultureInfo.InvariantCulture)
                + (properties.NumberSuffix ?? ".");

            return type switch
            {
                ListMarkerType.Decimal => (W.NumberFormatValues.Decimal, counted),
                ListMarkerType.DecimalLeadingZero => (W.NumberFormatValues.DecimalZero, counted),
                ListMarkerType.LowerAlpha => (W.NumberFormatValues.LowerLetter, counted),
                ListMarkerType.UpperAlpha => (W.NumberFormatValues.UpperLetter, counted),
                ListMarkerType.LowerRoman => (W.NumberFormatValues.LowerRoman, counted),
                ListMarkerType.UpperRoman => (W.NumberFormatValues.UpperRoman, counted),
                ListMarkerType.Dash => (W.NumberFormatValues.Bullet, "–"),
                ListMarkerType.Square => (W.NumberFormatValues.Bullet, "▪"),
                ListMarkerType.Circle => (W.NumberFormatValues.Bullet, "○"),
                ListMarkerType.Arrow => (W.NumberFormatValues.Bullet, "➤"),
                ListMarkerType.Custom => (W.NumberFormatValues.Bullet,
                    string.IsNullOrEmpty(properties.CustomMarker) ? "•" : properties.CustomMarker!),
                ListMarkerType.CustomSequence => (W.NumberFormatValues.Bullet,
                    properties.CustomSequence is { Count: > 0 } sequence ? sequence[0] : "•"),
                _ => (W.NumberFormatValues.Bullet, "•")
            };
        }
    }

    /// <summary>
    /// Полностью разрешённое символьное форматирование: значения, которыми
    /// движок рисует текст, без «унаследовать» — наследование уже применено.
    /// </summary>
    internal sealed class ResolvedRunStyle
    {
        public string FontFamily = "Times New Roman";
        public double FontSize = 12;
        public bool Bold;
        public bool Italic;
        public bool Underline;
        public bool Strikethrough;
        public bool Superscript;
        public bool Subscript;
        public bool AllCaps;
        public bool SmallCaps;
        public string? TextColor;
        public string? HighlightColor;

        public ResolvedRunStyle Clone() => (ResolvedRunStyle)MemberwiseClone();

        /// <summary>
        /// Свойства рана переопределяют значения стиля: незаданными остаются только
        /// шрифт, размер и цвета (у них есть состояние «унаследовать»), а флаги
        /// начертания объект несёт целиком — как и указано в модели документа.
        /// </summary>
        public void Apply(Models.Inline.RunProperties? properties)
        {
            if (properties is null) return;

            if (!string.IsNullOrWhiteSpace(properties.FontFamily)) FontFamily = properties.FontFamily!;
            if (properties.FontSize is double size && size > 0) FontSize = size;
            if (properties.TextColor is not null) TextColor = properties.TextColor;
            if (properties.HighlightColor is not null) HighlightColor = properties.HighlightColor;

            Bold = properties.IsBold;
            Italic = properties.IsItalic;
            Underline = properties.IsUnderline;
            Strikethrough = properties.IsStrikethrough;
            Superscript = properties.IsSuperscript;
            Subscript = properties.IsSubscript;
            AllCaps = properties.IsAllCaps;
            SmallCaps = properties.IsSmallCaps;
        }
    }

    /// <summary>
    /// Полностью разрешённое форматирование абзаца вместе с его базовым
    /// символьным форматированием (<see cref="BaseRun"/>).
    /// </summary>
    internal sealed class ResolvedParagraphStyle
    {
        public TextAlignment Alignment = TextAlignment.Left;
        public double FirstLineIndent;
        public double LeftIndent;
        public double RightIndent;
        public double SpaceBefore;
        public double SpaceAfter;
        public Models.Styles.LineSpacingRule LineSpacingRule = Models.Styles.LineSpacingRule.Auto;
        public double LineSpacingValue = 1.0;
        public bool KeepTogether;
        public bool KeepWithNext;
        public bool PageBreakBefore;
        public ResolvedRunStyle BaseRun = new();

        public void Apply(Models.Styles.ParagraphProperties? properties)
        {
            if (properties is null) return;

            if (properties.Alignment is TextAlignment alignment) Alignment = alignment;
            if (properties.FirstLineIndent is double firstLine) FirstLineIndent = firstLine;
            if (properties.LeftIndent is double left) LeftIndent = left;
            if (properties.RightIndent is double right) RightIndent = right;
            if (properties.SpaceBefore is double before) SpaceBefore = before;
            if (properties.SpaceAfter is double after) SpaceAfter = after;
            if (properties.LineSpacingRule is Models.Styles.LineSpacingRule rule) LineSpacingRule = rule;
            if (properties.LineSpacingValue is double lineValue && lineValue > 0) LineSpacingValue = lineValue;

            KeepTogether = properties.KeepTogether;
            KeepWithNext = properties.KeepWithNext;
            PageBreakBefore = properties.PageBreakBefore;
        }
    }

    /// <summary>
    /// Разрешает именованные стили документа в конкретные значения.
    /// Нужен только рисующему экспорту (PDF): в .docx наследование стилей
    /// разбирает сам Word по тем же правилам BasedOn.
    /// </summary>
    internal sealed class DocumentStyleResolver
    {
        private readonly Dictionary<string, DocumentStyle> _stylesByName =
            new(StringComparer.OrdinalIgnoreCase);

        public DocumentStyleResolver(DocumentModel document)
        {
            foreach (var style in document.Styles)
                if (!string.IsNullOrWhiteSpace(style.Name))
                    _stylesByName[style.Name] = style;
        }

        public ResolvedParagraphStyle ResolveParagraph(ParagraphBlock paragraph)
        {
            var result = new ResolvedParagraphStyle();

            foreach (var style in BuildChain(paragraph.Properties.StyleName))
            {
                result.Apply(style.ParagraphProperties);
                result.BaseRun.Apply(style.RunProperties);
            }

            result.Apply(paragraph.Properties);
            return result;
        }

        public ResolvedRunStyle ResolveRun(RunModel run, ResolvedParagraphStyle paragraphStyle)
        {
            var result = paragraphStyle.BaseRun.Clone();
            result.Apply(run.Properties);
            return result;
        }

        /// <summary>
        /// Цепочка стилей от корня к запрошенному по ссылкам BasedOn.
        /// «Normal» подставляется первым: он задаёт базовый шрифт документа,
        /// даже если запрошенный стиль на него формально не ссылается.
        /// </summary>
        private List<DocumentStyle> BuildChain(string? styleName)
        {
            var chain = new List<DocumentStyle>();

            if (_stylesByName.TryGetValue("Normal", out var normal))
                chain.Add(normal);

            if (string.IsNullOrWhiteSpace(styleName)
                || string.Equals(styleName, "Normal", StringComparison.OrdinalIgnoreCase))
                return chain;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var branch = new List<DocumentStyle>();
            string? current = styleName;

            while (current is not null && seen.Add(current) && _stylesByName.TryGetValue(current, out var style))
            {
                branch.Add(style);
                current = style.BasedOn;
            }

            branch.Reverse();

            foreach (var style in branch)
                if (!string.Equals(style.Name, "Normal", StringComparison.OrdinalIgnoreCase))
                    chain.Add(style);

            return chain;
        }
    }

    /// <summary>
    /// Движок раскладки PDF: заново размечает документ по физическим размерам
    /// страницы раздела и рисует его через SkiaSharp постранично.
    /// </summary>
    internal sealed class PdfExportEngine : IDisposable
    {
        private const double PointsPerMm = 72.0 / 25.4;
        private const float TabWidthPoints = 36f;

        private readonly DocumentModel _document;
        private readonly Func<string, byte[]?>? _resolveImage;
        private readonly DocumentStyleResolver _styles;
        private readonly List<string> _warnings = new();
        private readonly Dictionary<string, SKTypeface> _typefaces = new();
        private readonly Dictionary<string, SKFont> _fonts = new();
        private readonly Dictionary<string, SKImage?> _images = new();
        private readonly Dictionary<Guid, int[]> _listCounters = new();

        private readonly SKPaint _textPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
        private readonly SKPaint _fillPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
        private readonly SKPaint _strokePaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke };

        private Dictionary<Guid, ImageBlock> _inlineObjects = new();

        private SKDocument? _pdf;
        private SKCanvas? _canvas;

        private float _pageWidth = 595f;
        private float _pageHeight = 842f;
        private float _left;
        private float _top;
        private float _right = 595f;
        private float _bottom = 842f;
        private float _y;
        private bool _pageOpen;

        /// <summary>Раскладка внутри ячейки таблицы: перенос страницы запрещён.</summary>
        private bool _insideCell;

        public PdfExportEngine(DocumentModel document, Func<string, byte[]?>? resolveImage)
        {
            _document = document;
            _resolveImage = resolveImage;
            _styles = new DocumentStyleResolver(document);
        }

        public IReadOnlyList<string> Warnings => _warnings;

        public void Export(string outputPath)
        {
            using var stream = File.Create(outputPath);
            _pdf = SKDocument.CreatePdf(stream);

            if (_pdf is null)
                throw new InvalidOperationException("Не удалось создать PDF-документ (SkiaSharp вернул null).");

            foreach (var section in _document.Sections)
            {
                ApplySectionGeometry(section);

                _inlineObjects = new Dictionary<Guid, ImageBlock>();
                foreach (var obj in section.InlineObjects)
                    if (obj is ImageBlock image)
                        _inlineObjects[image.Id] = image;

                if (section.FloatingObjects.Count > 0)
                    _warnings.Add("Плавающие объекты (картинки с обтеканием, фигуры, надписи) не переносятся в PDF.");

                if (section.Header.IsEnabled || section.Footer.IsEnabled)
                    _warnings.Add("Колонтитулы не переносятся в PDF.");

                var columnSettings = section.ColumnSettings ?? _document.ColumnSettings;
                if (columnSettings.ColumnCount > 1)
                    _warnings.Add("Многоколоночная вёрстка в PDF не воспроизводится: текст идёт одной колонкой.");

                BeginPage();

                for (int i = 0; i < section.Blocks.Count; i++)
                {
                    switch (section.Blocks[i])
                    {
                        case ParagraphBlock paragraph:
                            DrawParagraph(paragraph, NextParagraph(section.Blocks, i));
                            break;

                        case TableBlock table:
                            DrawTable(table);
                            break;

                        case BreakBlock brk when brk.BreakType != BreakType.None:
                            BeginPage();
                            break;
                    }
                }

                EndPage();
            }

            if (!_pageOpen && _y == 0f)
            {
                // Пустой документ: PDF без единой страницы не открывается.
                BeginPage();
                EndPage();
            }

            _pdf.Close();
        }

        /// <summary>
        /// Следующий блок потока, если это абзац. Нужен только для «не отрывать
        /// от следующего»: смысл имеет ровно соседний блок, а не ближайший абзац
        /// где-то дальше за таблицей или разрывом.
        /// </summary>
        private static ParagraphBlock? NextParagraph(List<BlockModel> blocks, int index)
        {
            int next = index + 1;
            if (next >= blocks.Count) return null;
            return blocks[next] as ParagraphBlock;
        }

        private void ApplySectionGeometry(SectionModel section)
        {
            var settings = section.PageSettings ?? _document.PageSettings;

            _pageWidth = (float)(settings.GetPhysicalWidthMm() * PointsPerMm);
            _pageHeight = (float)(settings.GetPhysicalHeightMm() * PointsPerMm);

            _left = (float)((settings.MarginLeftMm + settings.MarginGutterMm) * PointsPerMm);
            _right = _pageWidth - (float)(settings.MarginRightMm * PointsPerMm);
            _top = (float)(settings.MarginTopMm * PointsPerMm);
            _bottom = _pageHeight - (float)(settings.MarginBottomMm * PointsPerMm);

            if (_right - _left < 36f) _right = _left + 36f;
            if (_bottom - _top < 36f) _bottom = _top + 36f;
        }

        private void BeginPage()
        {
            if (_pageOpen && _y <= _top) return; // страница только что начата — второй разрыв не нужен

            EndPage();

            _canvas = _pdf!.BeginPage(_pageWidth, _pageHeight);
            _pageOpen = true;
            _y = _top;
        }

        private void EndPage()
        {
            if (!_pageOpen) return;

            _pdf!.EndPage();
            _canvas = null;
            _pageOpen = false;
        }

        // ── Абзацы ──────────────────────────────────────────────────────────

        private void DrawParagraph(ParagraphBlock paragraph, ParagraphBlock? next)
        {
            var style = _styles.ResolveParagraph(paragraph);
            var list = paragraph.ListProperties;

            float leftIndent = (float)style.LeftIndent;
            float firstLineIndent = (float)style.FirstLineIndent;

            if (list is not null && list.MarkerType != ListMarkerType.None)
            {
                leftIndent += (float)list.EffectiveTextIndentPt();
                firstLineIndent = 0f;
            }

            float available = (_right - _left) - leftIndent - (float)style.RightIndent;
            if (available < 24f) available = 24f;

            string? markerText = BuildListMarker(paragraph, list);

            var lines = LayoutParagraph(paragraph, style, available, firstLineIndent);

            if (!_insideCell && style.PageBreakBefore && _y > _top)
                BeginPage();

            float spaceBefore = (float)style.SpaceBefore;
            float spaceAfter = (float)style.SpaceAfter;
            float contentHeight = lines.Sum(line => line.Height);
            float pageHeight = _bottom - _top;

            if (!_insideCell)
            {
                float required = spaceBefore + contentHeight;

                if (style.KeepTogether && required <= pageHeight && _y + required > _bottom)
                    BeginPage();
                else if (style.KeepWithNext && next is not null)
                {
                    // Заголовок не должен остаться внизу страницы один: резервируем
                    // место под первую строку следующего абзаца.
                    float nextFirstLine = EstimateFirstLineHeight(next);
                    if (_y + spaceBefore + contentHeight + nextFirstLine > _bottom
                        && spaceBefore + contentHeight + nextFirstLine <= pageHeight)
                        BeginPage();
                }
            }

            _y += spaceBefore;

            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];

                if (!_insideCell && _y + line.Height > _bottom && _y > _top)
                    BeginPage();

                float x = _left + leftIndent;
                if (i == 0) x += firstLineIndent;

                float lineAvailable = available - (i == 0 ? firstLineIndent : 0f);

                x = ApplyAlignment(x, line, lineAvailable, style.Alignment);

                if (i == 0 && markerText is not null && list is not null)
                    DrawListMarker(markerText, style, list, leftIndent, line);

                DrawLine(line, x, lineAvailable, style.Alignment);
                _y += line.Height;
            }

            _y += spaceAfter;
        }

        private float EstimateFirstLineHeight(ParagraphBlock paragraph)
        {
            var style = _styles.ResolveParagraph(paragraph);
            var font = GetFont(style.BaseRun, 1f);
            var metrics = font.Metrics;
            return LineHeight(-metrics.Ascent, metrics.Descent, metrics.Leading, style);
        }

        private float ApplyAlignment(float x, PdfLine line, float available, TextAlignment alignment)
        {
            float free = available - line.Width;
            if (free <= 0) return x;

            return alignment switch
            {
                TextAlignment.Center => x + free / 2f,
                TextAlignment.Right => x + free,
                _ => x
            };
        }

        private List<PdfLine> LayoutParagraph(
            ParagraphBlock paragraph, ResolvedParagraphStyle style, float available, float firstLineIndent)
        {
            var atoms = BuildAtoms(paragraph, style);
            var lines = new List<PdfLine>();
            var current = new PdfLine();
            float limit = Math.Max(available - firstLineIndent, 24f);

            void CloseLine()
            {
                lines.Add(current);
                current = new PdfLine();
                limit = Math.Max(available, 24f);
            }

            foreach (var atom in atoms)
            {
                if (atom.ForceBreak)
                {
                    CloseLine();
                    continue;
                }

                var pending = atom;

                while (pending is not null)
                {
                    if (current.Atoms.Count > 0 && current.Width + pending.Width > limit)
                    {
                        CloseLine();
                        continue;
                    }

                    if (current.Atoms.Count == 0 && pending.Width > limit)
                    {
                        var (head, tail) = SplitAtom(pending, limit);
                        current.Add(head);
                        CloseLine();
                        pending = tail;
                        continue;
                    }

                    current.Add(pending);
                    pending = null;
                }
            }

            // Последняя строка добавляется, только если в ней что-то есть; у пустого
            // абзаца строка всё равно одна — иначе он не займёт высоты.
            if (current.Atoms.Count > 0 || lines.Count == 0)
                lines.Add(current);

            foreach (var line in lines)
                MeasureLine(line, style);

            lines[lines.Count - 1].IsLast = true;
            return lines;
        }

        private List<PdfAtom> BuildAtoms(ParagraphBlock paragraph, ResolvedParagraphStyle style)
        {
            var atoms = new List<PdfAtom>();
            PdfAtom? current = null;

            void Flush()
            {
                if (current is not null && current.Pieces.Count > 0) atoms.Add(current);
                current = null;
            }

            foreach (var chunk in paragraph.Chunks)
            {
                foreach (var run in chunk.Runs)
                {
                    if (run.InlineImageId is Guid imageId)
                    {
                        Flush();

                        var imageAtom = BuildImageAtom(imageId);
                        if (imageAtom is not null) atoms.Add(imageAtom);
                        continue;
                    }

                    string text = run.Text ?? string.Empty;
                    if (text.Length == 0) continue;

                    var runStyle = _styles.ResolveRun(run, style);
                    if (runStyle.AllCaps) text = text.ToUpperInvariant();

                    var font = GetFont(runStyle, runStyle.Superscript || runStyle.Subscript ? 0.65f : 1f);

                    int i = 0;
                    while (i < text.Length)
                    {
                        char ch = text[i];

                        if (ch == '\n')
                        {
                            Flush();
                            atoms.Add(new PdfAtom { ForceBreak = true });
                            i++;
                            continue;
                        }

                        if (ch == '\r')
                        {
                            i++;
                            continue;
                        }

                        int start = i;
                        while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
                        string word = text.Substring(start, i - start);

                        int spacesStart = i;
                        while (i < text.Length && char.IsWhiteSpace(text[i]) && text[i] != '\n' && text[i] != '\r') i++;
                        string spaces = text.Substring(spacesStart, i - spacesStart);

                        if (word.Length == 0 && spaces.Length == 0)
                        {
                            i++;
                            continue;
                        }

                        current ??= new PdfAtom();

                        if (word.Length > 0)
                            AddPieces(current, word, runStyle, font);

                        if (spaces.Length > 0)
                        {
                            AddPieces(current, spaces, runStyle, font);
                            current.EndsWithSpace = true;
                            Flush();
                        }
                    }
                }
            }

            Flush();
            return atoms;
        }

        private PdfAtom? BuildImageAtom(Guid imageId)
        {
            if (!_inlineObjects.TryGetValue(imageId, out var image)) return null;

            var picture = GetImage(image.ImageFileName);
            if (picture is null) return null;

            float maxWidth = _right - _left;
            float width = (float)Math.Max(image.WidthPt, 1);
            float height = (float)Math.Max(image.HeightPt, 1);

            if (width > maxWidth)
            {
                height *= maxWidth / width;
                width = maxWidth;
            }

            var atom = new PdfAtom();
            atom.Pieces.Add(new PdfPiece
            {
                Image = picture,
                Width = width,
                ImageHeight = height,
                Style = new ResolvedRunStyle()
            });
            atom.Width = width;
            return atom;
        }

        private void AddPieces(PdfAtom atom, string text, ResolvedRunStyle style, SKFont font)
        {
            if (!style.SmallCaps)
            {
                atom.AddPiece(MakePiece(text, style, font));
                return;
            }

            // Малые заглавные: строчные буквы рисуются прописными уменьшенного размера.
            int i = 0;
            while (i < text.Length)
            {
                bool lower = char.IsLower(text[i]);
                int start = i;
                while (i < text.Length && char.IsLower(text[i]) == lower) i++;

                string part = text.Substring(start, i - start);

                if (lower)
                {
                    var smallFont = GetFont(style, style.Superscript || style.Subscript ? 0.52f : 0.8f);
                    atom.AddPiece(MakePiece(part.ToUpperInvariant(), style, smallFont));
                }
                else
                {
                    atom.AddPiece(MakePiece(part, style, font));
                }
            }
        }

        private PdfPiece MakePiece(string text, ResolvedRunStyle style, SKFont font)
        {
            string rendered = text.Replace("\t", "    ");

            float width = rendered.Length == 0 ? 0f : font.MeasureText(rendered);
            if (text.Contains('\t'))
                width = Math.Max(width, TabWidthPoints);

            float shift = 0f;
            if (style.Superscript) shift = -(float)(style.FontSize * 0.33);
            else if (style.Subscript) shift = (float)(style.FontSize * 0.18);

            return new PdfPiece
            {
                Text = rendered,
                Style = style,
                Font = font,
                Width = width,
                BaselineShift = shift
            };
        }

        private (PdfAtom Head, PdfAtom? Tail) SplitAtom(PdfAtom atom, float maxWidth)
        {
            var head = new PdfAtom();
            var tail = new PdfAtom { EndsWithSpace = atom.EndsWithSpace };
            float used = 0f;
            bool overflow = false;

            foreach (var piece in atom.Pieces)
            {
                if (overflow)
                {
                    tail.AddPiece(piece);
                    continue;
                }

                if (used + piece.Width <= maxWidth || piece.Image is not null)
                {
                    head.AddPiece(piece);
                    used += piece.Width;
                    continue;
                }

                // Кусок не помещается целиком — режем по символам.
                int fits = 0;
                float width = 0f;

                for (int i = 1; i <= piece.Text.Length; i++)
                {
                    float candidate = piece.Font.MeasureText(piece.Text.Substring(0, i));
                    if (used + candidate > maxWidth) break;
                    fits = i;
                    width = candidate;
                }

                if (fits == 0 && head.Pieces.Count == 0)
                {
                    fits = 1;
                    width = piece.Font.MeasureText(piece.Text.Substring(0, 1));
                }

                if (fits > 0)
                {
                    head.AddPiece(new PdfPiece
                    {
                        Text = piece.Text.Substring(0, fits),
                        Style = piece.Style,
                        Font = piece.Font,
                        Width = width,
                        BaselineShift = piece.BaselineShift
                    });
                    used += width;
                }

                if (fits < piece.Text.Length)
                {
                    string rest = piece.Text.Substring(fits);
                    tail.AddPiece(new PdfPiece
                    {
                        Text = rest,
                        Style = piece.Style,
                        Font = piece.Font,
                        Width = piece.Font.MeasureText(rest),
                        BaselineShift = piece.BaselineShift
                    });
                }

                overflow = true;
            }

            if (head.Pieces.Count == 0)
            {
                // Ничего не поместилось: отдаём атом целиком, иначе раскладка зациклится.
                return (atom, null);
            }

            head.EndsWithSpace = tail.Pieces.Count == 0 && atom.EndsWithSpace;
            return (head, tail.Pieces.Count > 0 ? tail : null);
        }

        private void MeasureLine(PdfLine line, ResolvedParagraphStyle style)
        {
            float ascent = 0f;
            float descent = 0f;
            float leading = 0f;

            foreach (var atom in line.Atoms)
            {
                foreach (var piece in atom.Pieces)
                {
                    if (piece.Image is not null)
                    {
                        ascent = Math.Max(ascent, piece.ImageHeight);
                        continue;
                    }

                    var metrics = piece.Font.Metrics;
                    ascent = Math.Max(ascent, -metrics.Ascent - piece.BaselineShift);
                    descent = Math.Max(descent, metrics.Descent + piece.BaselineShift);
                    leading = Math.Max(leading, metrics.Leading);
                }
            }

            if (ascent <= 0f && descent <= 0f)
            {
                var font = GetFont(style.BaseRun, 1f);
                var metrics = font.Metrics;
                ascent = -metrics.Ascent;
                descent = metrics.Descent;
                leading = metrics.Leading;
            }

            line.Ascent = ascent;
            line.Descent = descent;
            line.Height = LineHeight(ascent, descent, leading, style);
        }

        private static float LineHeight(
            float ascent, float descent, float leading, ResolvedParagraphStyle style)
        {
            float natural = ascent + descent + Math.Max(leading, 0f);

            return style.LineSpacingRule switch
            {
                Models.Styles.LineSpacingRule.Exact => (float)Math.Max(style.LineSpacingValue, 1),
                Models.Styles.LineSpacingRule.AtLeast => Math.Max(natural, (float)style.LineSpacingValue),
                _ => natural * (float)Math.Max(style.LineSpacingValue, 0.1)
            };
        }

        private void DrawLine(PdfLine line, float x, float available, TextAlignment alignment)
        {
            if (_canvas is null) return;

            float baseline = _y + line.Ascent;

            float extraPerSpace = 0f;
            if (alignment == TextAlignment.Justify && !line.IsLast)
            {
                int gaps = 0;
                for (int i = 0; i < line.Atoms.Count - 1; i++)
                    if (line.Atoms[i].EndsWithSpace) gaps++;

                if (gaps > 0 && available > line.Width)
                    extraPerSpace = (available - line.Width) / gaps;
            }

            float cursor = x;

            for (int i = 0; i < line.Atoms.Count; i++)
            {
                var atom = line.Atoms[i];

                foreach (var piece in atom.Pieces)
                {
                    if (piece.Image is not null)
                    {
                        var destination = SKRect.Create(cursor, baseline - piece.ImageHeight, piece.Width, piece.ImageHeight);
                        _canvas.DrawImage(piece.Image, destination);
                        cursor += piece.Width;
                        continue;
                    }

                    if (piece.Text.Length == 0) continue;

                    float pieceBaseline = baseline + piece.BaselineShift;

                    string? highlight = ExportService.HexWithoutHash(piece.Style.HighlightColor);
                    if (highlight is not null)
                    {
                        _fillPaint.Color = ParseColor(highlight, SKColors.Yellow);
                        var metrics = piece.Font.Metrics;
                        _canvas.DrawRect(
                            SKRect.Create(cursor, pieceBaseline + metrics.Ascent, piece.Width, -metrics.Ascent + metrics.Descent),
                            _fillPaint);
                    }

                    _textPaint.Color = ParseColor(ExportService.HexWithoutHash(piece.Style.TextColor), SKColors.Black);
                    _canvas.DrawText(piece.Text, cursor, pieceBaseline, SKTextAlign.Left, piece.Font, _textPaint);

                    if (piece.Style.Underline || piece.Style.Strikethrough)
                    {
                        _strokePaint.Color = _textPaint.Color;
                        _strokePaint.StrokeWidth = Math.Max((float)piece.Style.FontSize * 0.05f, 0.4f);

                        if (piece.Style.Underline)
                        {
                            float underlineY = pieceBaseline + piece.Font.Metrics.Descent * 0.5f;
                            _canvas.DrawLine(cursor, underlineY, cursor + piece.Width, underlineY, _strokePaint);
                        }

                        if (piece.Style.Strikethrough)
                        {
                            float strikeY = pieceBaseline + piece.Font.Metrics.Ascent * 0.33f;
                            _canvas.DrawLine(cursor, strikeY, cursor + piece.Width, strikeY, _strokePaint);
                        }
                    }

                    cursor += piece.Width;
                }

                if (extraPerSpace > 0f && i < line.Atoms.Count - 1 && atom.EndsWithSpace)
                    cursor += extraPerSpace;
            }
        }

        // ── Списки ──────────────────────────────────────────────────────────

        private string? BuildListMarker(ParagraphBlock paragraph, ListProperties? list)
        {
            if (list is null || list.MarkerType == ListMarkerType.None) return null;

            int level = Math.Clamp(list.Level, 0, 8);

            if (!_listCounters.TryGetValue(list.ListId, out var counters))
            {
                // Список встретился впервые: все уровни начинают с нуля, а первый
                // же элемент уровня подхватит StartAt ниже.
                counters = new int[9];
                _listCounters[list.ListId] = counters;
            }

            if (counters[level] == 0) counters[level] = Math.Max(list.StartAt, 1) - 1;

            counters[level]++;
            for (int i = level + 1; i < counters.Length; i++) counters[i] = 0;

            int number = counters[level];
            var type = list.EffectiveMarkerTypeForLevel();

            if ((int)type >= 10)
            {
                string text = type switch
                {
                    ListMarkerType.DecimalLeadingZero => number.ToString("00", CultureInfo.InvariantCulture),
                    ListMarkerType.LowerAlpha => ToAlphabetic(number).ToLowerInvariant(),
                    ListMarkerType.UpperAlpha => ToAlphabetic(number),
                    ListMarkerType.LowerRoman => ToRoman(number).ToLowerInvariant(),
                    ListMarkerType.UpperRoman => ToRoman(number),
                    ListMarkerType.CustomSequence => FromSequence(list, number),
                    _ => number.ToString(CultureInfo.InvariantCulture)
                };

                if (type == ListMarkerType.CustomSequence) return text;

                return (list.NumberPrefix ?? string.Empty) + text + (list.NumberSuffix ?? ".");
            }

            return type switch
            {
                ListMarkerType.Dash => "–",
                ListMarkerType.Square => "▪",
                ListMarkerType.Circle => "○",
                ListMarkerType.Arrow => "➤",
                ListMarkerType.Custom => string.IsNullOrEmpty(list.CustomMarker) ? "•" : list.CustomMarker!,
                _ => "•"
            };
        }

        private static string FromSequence(ListProperties list, int number)
        {
            if (list.CustomSequence is not { Count: > 0 } sequence) return "•";

            int index = number - 1;
            if (index >= sequence.Count)
                index = list.SequenceWrap ? index % sequence.Count : sequence.Count - 1;

            return sequence[Math.Max(index, 0)];
        }

        private static string ToAlphabetic(int number)
        {
            if (number < 1) number = 1;

            var builder = new StringBuilder();
            while (number > 0)
            {
                number--;
                builder.Insert(0, (char)('A' + number % 26));
                number /= 26;
            }

            return builder.ToString();
        }

        private static string ToRoman(int number)
        {
            if (number < 1) return "I";
            if (number > 3999) return number.ToString(CultureInfo.InvariantCulture);

            int[] values = { 1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1 };
            string[] symbols = { "M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I" };

            var builder = new StringBuilder();
            for (int i = 0; i < values.Length; i++)
            {
                while (number >= values[i])
                {
                    builder.Append(symbols[i]);
                    number -= values[i];
                }
            }

            return builder.ToString();
        }

        private void DrawListMarker(
            string markerText, ResolvedParagraphStyle style, ListProperties list, float leftIndent, PdfLine line)
        {
            if (_canvas is null) return;

            var font = GetFont(style.BaseRun, 1f);
            float markerX = _left + (float)style.LeftIndent + (float)list.EffectiveMarkerIndentPt();
            float baseline = _y + line.Ascent;

            float markerWidth = font.MeasureText(markerText);
            float textStart = _left + leftIndent;
            float minGap = (float)list.MarkerTextMinGapPt;

            // Номер не должен налезать на текст: при нехватке места сдвигаем его влево.
            if (markerX + markerWidth + minGap > textStart)
                markerX = Math.Max(_left, textStart - markerWidth - minGap);

            _textPaint.Color = ParseColor(ExportService.HexWithoutHash(style.BaseRun.TextColor), SKColors.Black);
            _canvas.DrawText(markerText, markerX, baseline, SKTextAlign.Left, font, _textPaint);
        }

        // ── Таблицы ─────────────────────────────────────────────────────────

        private void DrawTable(TableBlock table)
        {
            int rowCount = Math.Max(table.RowCount, 1);
            int columnCount = Math.Max(table.ColumnCount, 1);

            float availableWidth = _right - _left;
            float tableWidth = availableWidth * (float)Math.Clamp(table.WidthPercent, 1, 100) / 100f;
            float tableLeft = _left + (float)Math.Max(table.LeftIndentPt, 0);

            if (tableLeft + tableWidth > _right) tableWidth = Math.Max(_right - tableLeft, 36f);

            var columnWidths = ComputeColumnWidths(table, columnCount, tableWidth);
            var rowHeights = new float[rowCount];

            foreach (var cell in table.Cells)
            {
                if (cell.Row < 0 || cell.Row >= rowCount) continue;

                float contentWidth = SpanWidth(columnWidths, cell.Column, cell.ColSpan)
                    - (float)(cell.PaddingLeftPt + cell.PaddingRightPt);

                float height = MeasureCellHeight(cell, Math.Max(contentWidth, 12f))
                    + (float)(cell.PaddingTopPt + cell.PaddingBottomPt);

                if (cell.RowSpan <= 1)
                    rowHeights[cell.Row] = Math.Max(rowHeights[cell.Row], height);
            }

            for (int row = 0; row < rowCount; row++)
                rowHeights[row] = Math.Max(rowHeights[row], (float)table.GetRowMinHeightPt(row));

            // Объединённая по вертикали ячейка распределяет недостающую высоту
            // равномерно по своим строкам.
            foreach (var cell in table.Cells)
            {
                if (cell.RowSpan <= 1) continue;

                int last = Math.Min(cell.Row + cell.RowSpan, rowCount);
                if (cell.Row >= rowCount || last <= cell.Row) continue;

                float contentWidth = SpanWidth(columnWidths, cell.Column, cell.ColSpan)
                    - (float)(cell.PaddingLeftPt + cell.PaddingRightPt);

                float needed = MeasureCellHeight(cell, Math.Max(contentWidth, 12f))
                    + (float)(cell.PaddingTopPt + cell.PaddingBottomPt);

                float current = 0f;
                for (int row = cell.Row; row < last; row++) current += rowHeights[row];

                if (needed <= current) continue;

                float perRow = (needed - current) / (last - cell.Row);
                for (int row = cell.Row; row < last; row++) rowHeights[row] += perRow;
            }

            float totalHeight = rowHeights.Sum();
            float pageHeight = _bottom - _top;

            // Таблица не разбивается по страницам: если не помещается на текущей,
            // целиком переносится на следующую. Таблица выше страницы печатается как есть.
            if (!_insideCell && _y + totalHeight > _bottom && totalHeight <= pageHeight && _y > _top)
                BeginPage();

            float tableTop = _y;

            foreach (var cell in table.Cells)
            {
                if (cell.Row < 0 || cell.Row >= rowCount) continue;

                float cellX = tableLeft + SpanWidth(columnWidths, 0, cell.Column);
                float cellY = tableTop;
                for (int row = 0; row < cell.Row && row < rowCount; row++) cellY += rowHeights[row];

                float cellWidth = SpanWidth(columnWidths, cell.Column, cell.ColSpan);

                float cellHeight = 0f;
                int lastRow = Math.Min(cell.Row + Math.Max(cell.RowSpan, 1), rowCount);
                for (int row = cell.Row; row < lastRow; row++) cellHeight += rowHeights[row];

                DrawCell(cell, cellX, cellY, cellWidth, cellHeight);
            }

            _y = tableTop + totalHeight;
        }

        private void DrawCell(
            Models.Document.TableCell cell, float x, float y, float width, float height)
        {
            if (_canvas is null) return;

            string? background = ExportService.HexWithoutHash(cell.BackgroundColor);
            if (background is not null)
            {
                _fillPaint.Color = ParseColor(background, SKColors.White);
                _canvas.DrawRect(SKRect.Create(x, y, width, height), _fillPaint);
            }

            DrawCellBorders(cell, x, y, width, height);

            float contentWidth = width - (float)(cell.PaddingLeftPt + cell.PaddingRightPt);
            if (contentWidth < 12f) contentWidth = 12f;

            float contentHeight = MeasureCellHeight(cell, contentWidth);
            float innerHeight = height - (float)(cell.PaddingTopPt + cell.PaddingBottomPt);

            float offset = cell.VerticalAlignment switch
            {
                Models.Document.VerticalAlignment.Middle => Math.Max((innerHeight - contentHeight) / 2f, 0f),
                Models.Document.VerticalAlignment.Bottom => Math.Max(innerHeight - contentHeight, 0f),
                _ => 0f
            };

            float savedLeft = _left;
            float savedRight = _right;
            float savedY = _y;
            bool savedInsideCell = _insideCell;

            _left = x + (float)cell.PaddingLeftPt;
            _right = _left + contentWidth;
            _y = y + (float)cell.PaddingTopPt + offset;
            _insideCell = true;

            _canvas.Save();
            _canvas.ClipRect(SKRect.Create(x, y, width, height));

            foreach (var paragraph in cell.Paragraphs)
                DrawParagraph(paragraph, null);

            _canvas.Restore();

            _left = savedLeft;
            _right = savedRight;
            _y = savedY;
            _insideCell = savedInsideCell;
        }

        private void DrawCellBorders(
            Models.Document.TableCell cell, float x, float y, float width, float height)
        {
            if (_canvas is null) return;

            var borders = cell.Borders;
            var color = ParseColor(ExportService.HexWithoutHash(borders.Color), SKColors.Black);

            DrawBorderLine(borders.Top, x, y, x + width, y, borders.ThicknessPt, color);
            DrawBorderLine(borders.Bottom, x, y + height, x + width, y + height, borders.ThicknessPt, color);
            DrawBorderLine(borders.Left, x, y, x, y + height, borders.ThicknessPt, color);
            DrawBorderLine(borders.Right, x + width, y, x + width, y + height, borders.ThicknessPt, color);
        }

        private void DrawBorderLine(
            BorderStyle style, float x0, float y0, float x1, float y1, double thicknessPt, SKColor color)
        {
            if (_canvas is null || style == BorderStyle.None) return;

            float thickness = (float)Math.Max(thicknessPt, 0.25);
            if (style == BorderStyle.Thick) thickness *= 2f;

            _strokePaint.Color = color;
            _strokePaint.StrokeWidth = thickness;
            _strokePaint.PathEffect?.Dispose();
            _strokePaint.PathEffect = style switch
            {
                BorderStyle.Dashed => SKPathEffect.CreateDash(new[] { 4f, 3f }, 0f),
                BorderStyle.Dotted => SKPathEffect.CreateDash(new[] { 1f, 2f }, 0f),
                _ => null
            };

            _canvas.DrawLine(x0, y0, x1, y1, _strokePaint);

            if (style == BorderStyle.Double)
            {
                float shift = thickness + 1f;
                bool horizontal = Math.Abs(y1 - y0) < 0.01f;

                if (horizontal) _canvas.DrawLine(x0, y0 + shift, x1, y1 + shift, _strokePaint);
                else _canvas.DrawLine(x0 + shift, y0, x1 + shift, y1, _strokePaint);
            }

            _strokePaint.PathEffect?.Dispose();
            _strokePaint.PathEffect = null;
        }

        private float MeasureCellHeight(Models.Document.TableCell cell, float width)
        {
            float total = 0f;

            foreach (var paragraph in cell.Paragraphs)
            {
                var style = _styles.ResolveParagraph(paragraph);

                float indent = (float)(style.LeftIndent + style.RightIndent);
                if (paragraph.ListProperties is { } list && list.MarkerType != ListMarkerType.None)
                    indent += (float)list.EffectiveTextIndentPt();

                float available = Math.Max(width - indent, 12f);
                var lines = LayoutParagraph(paragraph, style, available, (float)style.FirstLineIndent);

                total += (float)style.SpaceBefore + lines.Sum(line => line.Height) + (float)style.SpaceAfter;
            }

            return total;
        }

        private static float[] ComputeColumnWidths(TableBlock table, int columnCount, float tableWidth)
        {
            var widths = new float[columnCount];
            float assigned = 0f;
            int autoCount = 0;

            for (int c = 0; c < columnCount; c++)
            {
                var definition = c < table.Columns.Count ? table.Columns[c] : null;

                if (definition is null || definition.WidthType == TableColumnWidthType.Auto)
                {
                    widths[c] = -1f;
                    autoCount++;
                    continue;
                }

                float value = definition.WidthType == TableColumnWidthType.Percent
                    ? tableWidth * (float)definition.WidthValue / 100f
                    : (float)(definition.WidthValue * PointsPerMm);

                if (value <= 0f)
                {
                    widths[c] = -1f;
                    autoCount++;
                    continue;
                }

                widths[c] = value;
                assigned += value;
            }

            float rest = Math.Max(tableWidth - assigned, 0f);
            float perAuto = autoCount > 0 ? rest / autoCount : 0f;

            for (int c = 0; c < columnCount; c++)
                if (widths[c] < 0f)
                    widths[c] = autoCount > 0 && rest > 0f ? perAuto : tableWidth / columnCount;

            float total = widths.Sum();
            if (total <= 0f)
            {
                for (int c = 0; c < columnCount; c++) widths[c] = tableWidth / columnCount;
                return widths;
            }

            // Итоговая ширина всегда равна ширине таблицы: иначе сетка «поедет».
            float scale = tableWidth / total;
            for (int c = 0; c < columnCount; c++) widths[c] *= scale;

            return widths;
        }

        private static float SpanWidth(float[] widths, int start, int span)
        {
            float total = 0f;
            for (int c = Math.Max(start, 0); c < start + Math.Max(span, 1) && c < widths.Length; c++)
                total += widths[c];
            return total;
        }

        // ── Шрифты, цвета, картинки ─────────────────────────────────────────

        private SKFont GetFont(ResolvedRunStyle style, float scale)
        {
            float size = (float)Math.Max(style.FontSize * scale, 1);
            string key = string.Create(CultureInfo.InvariantCulture,
                $"{style.FontFamily}|{size:F2}|{(style.Bold ? 1 : 0)}|{(style.Italic ? 1 : 0)}");

            if (_fonts.TryGetValue(key, out var cached)) return cached;

            var font = new SKFont
            {
                Typeface = GetTypeface(style.FontFamily, style.Bold, style.Italic),
                Size = size,
                Edging = SKFontEdging.Antialias
            };

            _fonts[key] = font;
            return font;
        }

        private SKTypeface GetTypeface(string family, bool bold, bool italic)
        {
            string key = $"{family}|{(bold ? 1 : 0)}|{(italic ? 1 : 0)}";
            if (_typefaces.TryGetValue(key, out var cached)) return cached;

            var typeface = SKTypeface.FromFamilyName(
                family,
                bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
                SKFontStyleWidth.Normal,
                italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright)
                ?? SKTypeface.Default;

            _typefaces[key] = typeface;
            return typeface;
        }

        private SKImage? GetImage(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;
            if (_images.TryGetValue(fileName, out var cached)) return cached;

            SKImage? picture = null;

            if (_resolveImage is not null)
            {
                byte[]? data;
                try
                {
                    data = _resolveImage(fileName);
                }
                catch
                {
                    data = null;
                }

                if (data is not null && data.Length > 0)
                {
                    try
                    {
                        picture = SKImage.FromEncodedData(data);
                    }
                    catch
                    {
                        picture = null;
                    }
                }
            }

            if (picture is null)
                _warnings.Add($"Картинка \"{fileName}\" не найдена или не читается и пропущена.");

            _images[fileName] = picture;
            return picture;
        }

        private static SKColor ParseColor(string? hexWithoutHash, SKColor fallback)
        {
            if (string.IsNullOrWhiteSpace(hexWithoutHash)) return fallback;

            return SKColor.TryParse("#" + hexWithoutHash, out var color) ? color : fallback;
        }

        public void Dispose()
        {
            foreach (var font in _fonts.Values) font.Dispose();
            _fonts.Clear();

            foreach (var typeface in _typefaces.Values) typeface.Dispose();
            _typefaces.Clear();

            foreach (var picture in _images.Values) picture?.Dispose();
            _images.Clear();

            _textPaint.Dispose();
            _fillPaint.Dispose();
            _strokePaint.Dispose();

            _pdf?.Dispose();
            _pdf = null;
        }

        // ── Внутренние структуры раскладки ──────────────────────────────────

        private sealed class PdfPiece
        {
            public string Text = string.Empty;
            public ResolvedRunStyle Style = new();
            public SKFont Font = null!;
            public float Width;
            public float BaselineShift;
            public SKImage? Image;
            public float ImageHeight;
        }

        private sealed class PdfAtom
        {
            public readonly List<PdfPiece> Pieces = new();
            public float Width;
            public bool EndsWithSpace;
            public bool ForceBreak;

            public void AddPiece(PdfPiece piece)
            {
                Pieces.Add(piece);
                Width += piece.Width;
            }
        }

        private sealed class PdfLine
        {
            public readonly List<PdfAtom> Atoms = new();
            public float Width;
            public float Ascent;
            public float Descent;
            public float Height;
            public bool IsLast;

            public void Add(PdfAtom atom)
            {
                Atoms.Add(atom);
                Width += atom.Width;
            }
        }
    }
}
