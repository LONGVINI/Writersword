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
    public sealed partial class ExportService
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
            Func<string, byte[]?>? resolveImage = null,
            PageFacts? pageFacts = null)
        {
            return Task.Run(() =>
            {
                try
                {
                    return ExportToDocxCore(document, outputPath, resolveImage, pageFacts);
                }
                catch (Exception ex)
                {
                    return ExportResult.Fail(ex.Message);
                }
            });
        }

        private ExportResult ExportToDocxCore(
            DocumentModel document, string outputPath, Func<string, byte[]?>? resolveImage, PageFacts? pageFacts)
        {
            using (var wordDoc = WordprocessingDocument.Create(outputPath, WordprocessingDocumentType.Document))
            {
                var mainPart = wordDoc.AddMainDocumentPart();
                var body = new W.Body();
                mainPart.Document = new W.Document(body);

                // Эффекты букв Word 2010+ (w14) и полные настройки Writersword (wsx).
                DocxTextEffects.DeclareNamespaces(mainPart.Document);
                _writerswordOnlyEffectsWritten = false;

                var ctx = new DocxWriteContext
                {
                    MainPart = mainPart,
                    ResolveImage = resolveImage
                };

                WriteStyleDefinitions(mainPart, document);

                ctx.Numbering = new DocxNumberingBuilder();
                ctx.Numbering.Collect(document);
                ctx.Numbering.Write(mainPart);

                // Колонтитулы: разделы Word нарезаются по оформлению листов
                // (ExportService.HeaderFooter).
                var hfPlan = BuildHeaderFooterPlan(document, pageFacts, ctx);
                if (hfPlan is not null)
                    WriteHeaderFooterSettingsPart(mainPart, hfPlan);

                // Запись исправлений, включённая в документе (ExportService.Revisions).
                WriteTrackRevisionsSetting(mainPart, document);

                for (int s = 0; s < document.Sections.Count; s++)
                {
                    var section = document.Sections[s];
                    bool isFinal = s == document.Sections.Count - 1;

                    ctx.InlineObjects = BuildInlineObjectMap(section);
                    ctx.InlineShapes = BuildInlineShapeMap(section);

                    if (section.FloatingObjects.Count > 0)
                        ctx.Warnings.Add(
                            "Плавающие объекты (картинки с обтеканием, фигуры, надписи) не переносятся в .docx.");

                    if (section.Header.IsEnabled || section.Footer.IsEnabled)
                        ctx.Warnings.Add("Колонтитулы не переносятся в .docx.");

                    W.Paragraph? lastParagraph = null;

                    // Последний абзац — разрыв страницы: разрыв раздела Word встаёт на
                    // его место, иначе между разделами появился бы пустой лист.
                    bool lastIsPageBreak = false;

                    // Разрыв страницы пишется внутрь абзаца перед ним — туда, где рисуется его
                    // отметка: так у Word один абзац с разрывом, а не абзац и за ним ещё один,
                    // из одного разрыва. Иначе каждый круг экспорта и импорта прибавлял бы
                    // перед разрывом пустой абзац, а текст, продолжавший абзац за разрывом,
                    // становился бы новым абзацем.
                    //   lastIsFlowParagraph — последний абзац тела собран из абзаца модели
                    //     (а не заглушка за таблицей и не отдельный абзац разрыва);
                    //   lastBreakRun — ран разрыва, дописанный в такой абзац;
                    //   continueIntoLast — за разрывом продолжается тот же абзац Word, и
                    //     следующий абзац модели дописывается в него же.
                    bool lastIsFlowParagraph = false;
                    W.Run? lastBreakRun = null;
                    bool continueIntoLast = false;

                    // Плавающие картинки и фигуры, встреченные в потоке: у Word они живут
                    // внутри абзаца, поэтому ждут здесь абзаца, перед которым стоят, и
                    // встают якорями в его начало.
                    var pendingFloatRuns = new List<W.Run>();

                    // Плавающим объектам не досталось абзаца (за ними таблица или конец
                    // раздела): им отдаётся собственный пустой абзац.
                    void FlushPendingFloats()
                    {
                        if (pendingFloatRuns.Count == 0) return;

                        var holder = new W.Paragraph();
                        foreach (var floatRun in pendingFloatRuns) holder.AppendChild(floatRun);
                        pendingFloatRuns.Clear();

                        body.AppendChild(holder);
                        lastParagraph = holder;
                        lastIsPageBreak = false;
                        lastBreakRun = null;
                        lastIsFlowParagraph = false;
                        continueIntoLast = false;
                    }

                    foreach (var block in section.Blocks)
                    {
                        switch (block)
                        {
                            case ParagraphBlock para:
                                bool startsWordSection = hfPlan is not null && hfPlan.SectionStarts.ContainsKey(para.Id);

                                if (continueIntoLast && lastParagraph is not null && !startsWordSection)
                                {
                                    // Продолжение абзаца за разрывом: его раны и прочее содержимое
                                    // встают в тот же w:p, свойства абзаца остаются от начала.
                                    foreach (var floatRun in pendingFloatRuns) lastParagraph.AppendChild(floatRun);
                                    pendingFloatRuns.Clear();

                                    var continuation = BuildParagraph(para, ctx);
                                    var moved = new List<OpenXmlElement>();
                                    foreach (var child in continuation.ChildElements)
                                    {
                                        if (child is W.ParagraphProperties) continue;
                                        moved.Add(child);
                                    }

                                    foreach (var child in moved)
                                    {
                                        child.Remove();
                                        lastParagraph.AppendChild(child);
                                    }

                                    continueIntoLast = false;
                                    lastIsPageBreak = false;
                                    lastBreakRun = null;
                                    lastIsFlowParagraph = true;
                                    break;
                                }

                                continueIntoLast = false;

                                if (hfPlan is not null && hfPlan.SectionStarts.TryGetValue(para.Id, out int wordSection))
                                {
                                    if (lastParagraph is not null)
                                    {
                                        var breakSectPr = BuildSectionProperties(section, document);
                                        ApplyHeaderFooterToSection(breakSectPr, hfPlan, ctx);

                                        // Разрыв раздела встаёт на место разрыва страницы. Разрыв
                                        // внутри абзаца с текстом убирается один, текст остаётся.
                                        if (lastIsPageBreak)
                                        {
                                            if (lastBreakRun is not null)
                                                lastBreakRun.Remove();
                                            else
                                                lastParagraph.RemoveAllChildren<W.Run>();
                                        }

                                        var breakPPr = lastParagraph.GetFirstChild<W.ParagraphProperties>();
                                        if (breakPPr is null)
                                        {
                                            breakPPr = new W.ParagraphProperties();
                                            lastParagraph.InsertAt(breakPPr, 0);
                                        }
                                        breakPPr.AppendChild(breakSectPr);
                                    }

                                    hfPlan.Current = wordSection;
                                }

                                lastParagraph = BuildParagraph(para, ctx);

                                // Якоря плавающих объектов — в начало абзаца, сразу за его свойствами.
                                if (pendingFloatRuns.Count > 0)
                                {
                                    int floatInsertAt = lastParagraph.GetFirstChild<W.ParagraphProperties>() is null ? 0 : 1;
                                    foreach (var floatRun in pendingFloatRuns)
                                        lastParagraph.InsertAt(floatRun, floatInsertAt++);
                                    pendingFloatRuns.Clear();
                                }

                                body.AppendChild(lastParagraph);
                                lastIsPageBreak = false;
                                lastBreakRun = null;
                                lastIsFlowParagraph = true;
                                break;

                            case TableBlock table:
                                FlushPendingFloats();
                                body.AppendChild(BuildTable(table, ctx));

                                // После таблицы в OOXML обязан идти абзац, иначе Word
                                // считает файл повреждённым.
                                lastParagraph = new W.Paragraph();
                                body.AppendChild(lastParagraph);
                                lastIsPageBreak = false;
                                lastBreakRun = null;
                                lastIsFlowParagraph = false;
                                continueIntoLast = false;
                                break;

                            case BreakBlock brk:
                                if (lastIsFlowParagraph && lastParagraph is not null
                                    && brk.BreakType is BreakType.Page or BreakType.Column)
                                {
                                    lastBreakRun = BuildBreakRun(brk);
                                    lastParagraph.AppendChild(lastBreakRun);
                                    continueIntoLast = brk.InParagraph && brk.ContinuesParagraph;
                                }
                                else
                                {
                                    lastParagraph = BuildBreakParagraph(brk);
                                    body.AppendChild(lastParagraph);
                                    lastBreakRun = null;
                                    lastIsFlowParagraph = false;
                                    continueIntoLast = false;
                                }

                                lastIsPageBreak = brk.BreakType != BreakType.Column;
                                break;

                            case ImageBlock:
                            case ShapeBlock:
                            {
                                var objectRun = BuildFlowObjectRun(block, ctx);
                                if (objectRun is null)
                                {
                                    ctx.Warnings.Add("Картинка без файла в .docx не перенесена.");
                                    break;
                                }

                                // Плавающий объект ждёт своего абзаца. Объект «сверху и снизу»
                                // из Word — тоже: он уходит якорем, как был у Word.
                                if (block is IFloatingObject { WrapMode: not WrapMode.Inline }
                                    or IFloatingObject { WordDrawing.WrapTopAndBottom: true })
                                {
                                    pendingFloatRuns.Add(objectRun);
                                    break;
                                }

                                // Объект на собственной полосе — отдельный абзац с его
                                // выравниванием и рисунком в строке.
                                FlushPendingFloats();

                                var objectParagraph = new W.Paragraph();
                                var objectAlignment = ((IFloatingObject)block).Alignment;
                                if (objectAlignment is TextAlignment.Center or TextAlignment.Right)
                                {
                                    objectParagraph.AppendChild(new W.ParagraphProperties(
                                        new W.Justification
                                        {
                                            Val = new EnumValue<W.JustificationValues>(
                                                objectAlignment == TextAlignment.Center
                                                    ? W.JustificationValues.Center
                                                    : W.JustificationValues.Right)
                                        }));
                                }
                                objectParagraph.AppendChild(objectRun);

                                body.AppendChild(objectParagraph);
                                lastParagraph = objectParagraph;
                                lastIsPageBreak = false;
                                lastBreakRun = null;
                                lastIsFlowParagraph = false;
                                continueIntoLast = false;
                                break;
                            }

                            case FloatingTextBlock:
                                ctx.Warnings.Add("Надписи старого вида не переносятся в .docx.");
                                break;
                        }
                    }

                    FlushPendingFloats();

                    var sectPr = BuildSectionProperties(section, document);
                    if (hfPlan is not null)
                        ApplyHeaderFooterToSection(sectPr, hfPlan, ctx);

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

                if (_writerswordOnlyEffectsWritten)
                    ctx.Warnings.Add(
                        "Эффекты, которых нет в Word (контур снаружи букв, длинная тень), Word покажет упрощённо. " +
                        "Writersword при открытии этого файла восстановит их полностью.");

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

        /// <summary>Фигуры, стоящие в строке текста, по Id — на них ссылаются run-ы абзацев.</summary>
        private static Dictionary<Guid, ShapeBlock> BuildInlineShapeMap(SectionModel section)
        {
            var map = new Dictionary<Guid, ShapeBlock>();
            foreach (var obj in section.InlineObjects)
                if (obj is ShapeBlock shape)
                    map[shape.Id] = shape;
            return map;
        }

        /// <summary>
        /// Рисунок Word для фигуры в строке текста: фигура стоит среди букв своего
        /// абзаца, как у Word (wp:inline с wps:wsp).
        /// </summary>
        private W.Drawing? BuildInlineShapeDrawing(Guid shapeId, DocxWriteContext ctx)
        {
            if (!ctx.InlineShapes.TryGetValue(shapeId, out var shape))
                return null;

            long cx = (long)Math.Round(Math.Max(shape.WidthPt, 0) * EmuPerPoint);
            long cy = (long)Math.Round(Math.Max(shape.HeightPt, 0) * EmuPerPoint);

            uint drawingId = ctx.NextDrawingId++;
            string name = "Shape " + drawingId.ToString(CultureInfo.InvariantCulture);

            string? fillRelationshipId = string.IsNullOrEmpty(shape.FillImageFileName)
                ? null
                : EnsureImageFilePart(shape.FillImageFileName!, ctx);

            var graphic = new Dr.Graphic(
                new Dr.GraphicData(XmlElement(BuildShapeXml(shape, cx, cy, fillRelationshipId, ctx)))
                {
                    Uri = WordprocessingShapeNamespace
                });

            return BuildObjectDrawing(
                shape, null, cx, cy, drawingId, name, shape.AltText, graphic, topAndBottomAnchor: false);
        }

        // ── docx: стили ─────────────────────────────────────────────────────

        private void WriteStyleDefinitions(MainDocumentPart mainPart, DocumentModel document)
        {
            var stylePart = mainPart.AddNewPart<StyleDefinitionsPart>();
            var styles = new W.Styles();
            DocxTextEffects.DeclareNamespaces(styles);

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
        /// pStyle, keepNext, keepLines, pageBreakBefore, numPr, pBdr, tabs, bidi, spacing, ind,
        /// contextualSpacing, jc, outlineLvl.
        ///
        /// Порядок не вкусовой: Word читает свойства абзаца строго по схеме и, встретив
        /// элемент не на своём месте, объявляет файл повреждённым. Позиции табуляции
        /// раньше стояли в самом конце, после outlineLvl, — там им не место.
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
                int? numId = numbering.GetNumberingId(list);
                if (numId is int nid)
                {
                    elements.Add(new W.NumberingProperties(
                        new W.NumberingLevelReference { Val = Math.Clamp(list.Level, 0, 8) },
                        new W.NumberingId { Val = nid }));
                }
            }

            var borders = BuildParagraphBorders(props?.Borders);
            if (borders is not null) elements.Add(borders);

            // Заливка абзаца: по схеме w:shd идёт сразу за w:pBdr. Узор пишется своим
            // именем в w:val и своим цветом в w:color, цвет фона — в w:fill; без узора
            // это clear и одна заливка.
            string? shadingFill = props?.ShadingColor is { Length: > 0 } fillColor ? fillColor : null;
            string? shadingPattern = props?.ShadingPattern is { Length: > 0 } patternName ? patternName : null;
            if (shadingFill is not null || shadingPattern is not null)
            {
                var shd = new W.Shading
                {
                    Color = shadingPattern is not null && props!.ShadingPatternColor is { Length: > 0 } patternColor
                        ? patternColor.TrimStart('#').ToUpperInvariant()
                        : "auto",
                    Fill = shadingFill is not null
                        ? shadingFill.TrimStart('#').ToUpperInvariant()
                        : "auto"
                };

                if (shadingPattern is not null)
                    shd.Val = new EnumValue<W.ShadingPatternValues> { InnerText = shadingPattern };
                else
                    shd.Val = W.ShadingPatternValues.Clear;

                elements.Add(shd);
            }

            var tabs = BuildTabs(props);
            if (tabs is not null) elements.Add(tabs);

            // Абзац справа налево: по схеме w:bidi стоит после w:tabs, перед w:spacing.
            if (props?.RightToLeft == true) elements.Add(new W.BiDi());

            var spacing = BuildSpacing(props);
            if (spacing is not null) elements.Add(spacing);

            var indentation = BuildIndentation(props, list);
            if (indentation is not null) elements.Add(indentation);

            // «Не добавлять интервал между абзацами одного стиля»: по схеме — сразу за w:ind.
            if (props?.ContextualSpacing == true) elements.Add(new W.ContextualSpacing());

            if (props?.Alignment is TextAlignment alignment)
                elements.Add(new W.Justification { Val = new EnumValue<W.JustificationValues>(MapAlignment(alignment)) });

            // Выравнивание знаков по высоте строки: по схеме — после w:jc, перед w:outlineLvl.
            string? lineTextAlign = props?.LineTextAlignment switch
            {
                Models.Styles.LineTextAlignment.Baseline => "baseline",
                Models.Styles.LineTextAlignment.Top => "top",
                Models.Styles.LineTextAlignment.Center => "center",
                Models.Styles.LineTextAlignment.Bottom => "bottom",
                _ => null
            };
            if (lineTextAlign is not null)
                elements.Add(new W.TextAlignment { Val = new EnumValue<W.VerticalTextAlignmentValues> { InnerText = lineTextAlign } });

            int? outline = outlineLevelOverride;
            if (outline is null && props is not null && props.OutlineLevel > 0)
                outline = Math.Clamp(props.OutlineLevel - 1, 0, 8);

            if (outline is int outlineValue)
                elements.Add(new W.OutlineLevel { Val = outlineValue });

            return elements;
        }

        /// <summary>
        /// Рамка абзаца (w:pBdr). Стороны идут в порядке схемы: top, left, bottom,
        /// right. Толщина у Word в восьмых пункта, зазор — в целых пунктах; пустой
        /// цвет — «авто», цвет текста.
        /// </summary>
        private static W.ParagraphBorders? BuildParagraphBorders(Models.Styles.ParagraphBorders? borders)
        {
            if (borders is null || borders.IsEmpty) return null;

            var result = new W.ParagraphBorders();

            if (borders.Top is { IsVisible: true } top)
                result.AppendChild(FillBorder(new W.TopBorder(), top));
            if (borders.Left is { IsVisible: true } left)
                result.AppendChild(FillBorder(new W.LeftBorder(), left));
            if (borders.Bottom is { IsVisible: true } bottom)
                result.AppendChild(FillBorder(new W.BottomBorder(), bottom));
            if (borders.Right is { IsVisible: true } right)
                result.AppendChild(FillBorder(new W.RightBorder(), right));

            return result;
        }

        private static T FillBorder<T>(T element, Models.Styles.ParagraphBorderLine line) where T : W.BorderType
        {
            element.Val = new EnumValue<W.BorderValues>(line.Style switch
            {
                BorderStyle.Double => W.BorderValues.Double,
                BorderStyle.Dashed => W.BorderValues.Dashed,
                BorderStyle.Dotted => W.BorderValues.Dotted,
                BorderStyle.Thick => W.BorderValues.Thick,
                BorderStyle.Triple => W.BorderValues.Triple,
                BorderStyle.Wave => W.BorderValues.Wave,
                _ => W.BorderValues.Single
            });

            element.Size = (UInt32Value)(uint)Math.Clamp(Math.Round(line.WidthPt * 8.0), 2.0, 96.0);
            element.Space = (UInt32Value)(uint)Math.Clamp(Math.Round(line.SpacePt), 0.0, 31.0);
            element.Color = string.IsNullOrWhiteSpace(line.Color)
                ? "auto"
                : line.Color!.TrimStart('#').ToUpperInvariant();

            return element;
        }

        /// <summary>
        /// Позиции табуляции абзаца.
        ///
        /// Без них оглавление уезжает в Word ровно так же, как приезжало оттуда до
        /// починки импорта: номер страницы отрывается от правого поля и прилипает к
        /// названию главы, а точки между ними исчезают — их рисует не текст, а
        /// заполнитель позиции.
        /// </summary>
        private static W.Tabs? BuildTabs(Models.Styles.ParagraphProperties? props)
        {
            if (props?.TabStops is not { Count: > 0 } stops) return null;

            var tabs = new W.Tabs();

            foreach (var stop in stops)
            {
                var tab = new W.TabStop
                {
                    Val = new EnumValue<W.TabStopValues>(MapTabAlignment(stop.Alignment)),
                    Position = (int)Math.Round(stop.PositionPt * TwipsPerPoint)
                };

                var leader = MapTabLeader(stop.Leader);
                if (leader is not null)
                    tab.Leader = new EnumValue<W.TabStopLeaderCharValues>(leader.Value);

                tabs.Append(tab);
            }

            return tabs;
        }

        private static W.TabStopValues MapTabAlignment(Models.Styles.TabAlignment alignment) => alignment switch
        {
            Models.Styles.TabAlignment.Right => W.TabStopValues.Right,
            Models.Styles.TabAlignment.Center => W.TabStopValues.Center,
            Models.Styles.TabAlignment.Decimal => W.TabStopValues.Decimal,
            Models.Styles.TabAlignment.Bar => W.TabStopValues.Bar,
            _ => W.TabStopValues.Left
        };

        private static W.TabStopLeaderCharValues? MapTabLeader(Models.Styles.TabLeaderStyle leader) => leader switch
        {
            Models.Styles.TabLeaderStyle.Dots => W.TabStopLeaderCharValues.Dot,
            Models.Styles.TabLeaderStyle.Dashes => W.TabStopLeaderCharValues.Hyphen,
            Models.Styles.TabLeaderStyle.Line => W.TabStopLeaderCharValues.Underscore,
            _ => null
        };

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
        /// rFonts, b, bCs, i, iCs, caps, smallCaps, strike, color, sz, szCs, u, shd, vertAlign, lang.
        /// </summary>
        /// <param name="props">Свойства рана или стиля. Null — элементов нет.</param>
        /// <param name="writeExplicitToggles">
        /// Писать выключенные начертания явно (w:val="0"). Нужно для ранов: без явного
        /// выключения не жирный текст внутри жирного стиля абзаца стал бы в Word жирным.
        /// Жирность и курсив пишутся выключенными, только когда их сняли руками
        /// (false); не заданные (null) не пишутся вовсе — такой ран берёт их от стиля,
        /// и слово в заголовке, которому поменяли один цвет, остаётся в Word жирным.
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

            // Жирность и курсив пишутся и для сложных письменностей (w:bCs, w:iCs), как
            // это делает сам Word по Ctrl+B и Ctrl+I: без них иврит и арабский в Word
            // остались бы прямыми и не жирными, хотя у нас они такие. По схеме w:bCs
            // идёт сразу за w:b, w:iCs — за w:i.
            if (props.IsBold == true)
            {
                elements.Add(new W.Bold());
                elements.Add(new W.BoldComplexScript());
            }
            else if (props.IsBold == false && writeExplicitToggles)
            {
                elements.Add(new W.Bold { Val = false });
                elements.Add(new W.BoldComplexScript { Val = false });
            }

            if (props.IsItalic == true)
            {
                elements.Add(new W.Italic());
                elements.Add(new W.ItalicComplexScript());
            }
            else if (props.IsItalic == false && writeExplicitToggles)
            {
                elements.Add(new W.Italic { Val = false });
                elements.Add(new W.ItalicComplexScript { Val = false });
            }

            if (props.IsAllCaps) elements.Add(new W.Caps());
            else if (writeExplicitToggles) elements.Add(new W.Caps { Val = false });

            if (props.IsSmallCaps) elements.Add(new W.SmallCaps());
            else if (writeExplicitToggles) elements.Add(new W.SmallCaps { Val = false });

            if (props.IsStrikethrough) elements.Add(new W.Strike());
            else if (writeExplicitToggles) elements.Add(new W.Strike { Val = false });

            // Двойное зачёркивание — сразу за одинарным, как требует схема CT_RPr.
            if (props.IsDoubleStrikethrough) elements.Add(new W.DoubleStrike());
            else if (writeExplicitToggles) elements.Add(new W.DoubleStrike { Val = false });

            // Эффекты букв и скрытый текст — дальше по схеме: w:outline, w:shadow,
            // w:emboss, w:imprint, затем w:vanish.
            if (props.IsOutline) elements.Add(new W.Outline());
            else if (writeExplicitToggles) elements.Add(new W.Outline { Val = false });

            if (props.IsShadow) elements.Add(new W.Shadow());
            else if (writeExplicitToggles) elements.Add(new W.Shadow { Val = false });

            if (props.IsEmboss) elements.Add(new W.Emboss());
            else if (writeExplicitToggles) elements.Add(new W.Emboss { Val = false });

            if (props.IsImprint) elements.Add(new W.Imprint());
            else if (writeExplicitToggles) elements.Add(new W.Imprint { Val = false });

            if (props.IsHidden) elements.Add(new W.Vanish());
            else if (writeExplicitToggles) elements.Add(new W.Vanish { Val = false });

            string? textColor = HexWithoutHash(props.TextColor);
            if (textColor is not null) elements.Add(new W.Color { Val = textColor });

            // Разрядка — сразу за цветом, как требует схема CT_RPr. В файле она в
            // двадцатых долях пункта.
            if (props.CharacterSpacing is double spacing && Math.Abs(spacing) > 0.001)
                elements.Add(new W.Spacing { Val = (int)Math.Round(spacing * 20.0) });

            // Масштаб по ширине и смещение от базовой линии — за разрядкой, в порядке
            // схемы CT_RPr (w:spacing, w:w, w:kern, w:position, w:sz).
            if (props.CharacterScale is int scale && scale > 0 && scale != 100)
                elements.Add(new W.CharacterScale { Val = scale });

            if (props.BaselineOffset is double offset && Math.Abs(offset) > 0.001)
                elements.Add(new W.Position
                {
                    Val = Math.Round(offset * HalfPointsPerPoint).ToString(CultureInfo.InvariantCulture)
                });

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
                var underline = new W.Underline
                {
                    Val = new EnumValue<W.UnderlineValues>(UnderlineToOoxml(props.UnderlineStyle))
                };

                // Цвет линии — атрибутом того же элемента. Без него Word рисует линию
                // цветом букв, как и «авто» в модели.
                string? underlineColor = HexWithoutHash(props.UnderlineColor);
                if (underlineColor is not null) underline.Color = underlineColor;

                elements.Add(underline);
            }
            else if (writeExplicitToggles)
            {
                elements.Add(new W.Underline { Val = new EnumValue<W.UnderlineValues>(W.UnderlineValues.None) });
            }

            // Рамка вокруг знаков — перед заливкой рана, как требует схема (w:bdr, w:shd).
            if (props.CharBorderWidthPt is double borderWidth && borderWidth > 0)
            {
                elements.Add(new W.Border
                {
                    Val = new EnumValue<W.BorderValues>(props.CharBorderStyle switch
                    {
                        CharBorderStyle.Double => W.BorderValues.Double,
                        CharBorderStyle.Dotted => W.BorderValues.Dotted,
                        CharBorderStyle.Dashed => W.BorderValues.Dashed,
                        CharBorderStyle.Thick => W.BorderValues.Thick,
                        _ => W.BorderValues.Single
                    }),
                    Size = (uint)Math.Max(2, Math.Round(borderWidth * 8.0)),
                    Space = 0,
                    Color = HexWithoutHash(props.CharBorderColor) ?? "auto"
                });
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

            // Знак ударения — за вертикальным положением, перед языком (w:em, w:lang).
            if (props.EmphasisMark != EmphasisMark.None)
            {
                elements.Add(new W.Emphasis
                {
                    Val = new EnumValue<W.EmphasisMarkValues>(props.EmphasisMark switch
                    {
                        EmphasisMark.Comma => W.EmphasisMarkValues.Comma,
                        EmphasisMark.Circle => W.EmphasisMarkValues.Circle,
                        EmphasisMark.UnderDot => W.EmphasisMarkValues.UnderDot,
                        _ => W.EmphasisMarkValues.Dot
                    })
                });
            }

            if (!string.IsNullOrWhiteSpace(props.Language))
                elements.Add(new W.Languages { Val = props.Language });

            // Настраиваемые эффекты — после всех элементов w:, элементами w14 (Word
            // 2010+), а то, чего Word не умеет, — ещё и полными настройками wsx:effects.
            if (props.Effects is { } effects && !effects.IsEmpty)
            {
                elements.AddRange(DocxTextEffects.Write(effects));
                if (effects.HasWriterswordOnlyParts) _writerswordOnlyEffectsWritten = true;
            }

            return elements;
        }

        // Выгрузка встретила эффекты, которых нет у Word, — для предупреждения в итоге.
        private bool _writerswordOnlyEffectsWritten;

        // ── docx: абзацы, раны, картинки ────────────────────────────────────

        private W.Paragraph BuildParagraph(ParagraphBlock para, DocxWriteContext ctx)
        {
            var result = new W.Paragraph();

            var propertyElements = BuildParagraphPropertyElements(
                para.Properties, para.ListProperties, ctx.Numbering, null);

            if (propertyElements.Count > 0 || para.Properties.HasRevision)
            {
                var pPr = new W.ParagraphProperties();
                foreach (var element in propertyElements) pPr.AppendChild(element);

                // Правки знака абзаца и его оформления (ExportService.Revisions).
                AppendParagraphRevisions(pPr, para, ctx);

                result.AppendChild(pPr);
            }

            // Раны собираются вместе со своими правками: подряд идущие раны одной
            // правки уходят одним w:ins или w:del.
            var runElements = new List<(RunModel Run, List<OpenXmlElement> Elements)>();
            foreach (var chunk in para.Chunks)
            {
                foreach (var run in chunk.Runs)
                    runElements.Add((run, BuildRunElements(run, ctx)));
            }

            foreach (var element in WrapRunRevisions(runElements, ctx))
                result.AppendChild(element);

            return result;
        }

        private List<OpenXmlElement> BuildRunElements(RunModel run, DocxWriteContext ctx)
        {
            var elements = new List<OpenXmlElement>();

            if (run.InlineImageId is Guid imageId)
            {
                // Объект строки — картинка или фигура: обе стоят среди букв абзаца.
                var drawing = BuildImageDrawing(imageId, ctx) ?? BuildInlineShapeDrawing(imageId, ctx);
                if (drawing is null) return elements;

                var imageRun = new W.Run();
                var imageProps = BuildRunPropertyElements(run.Properties, writeExplicitToggles: true);
                if (imageProps.Count > 0 || run.Properties?.FormatChange is not null)
                {
                    var rPr = new W.RunProperties();
                    foreach (var element in imageProps) rPr.AppendChild(element);
                    if (run.Properties?.FormatChange is { } imageFormatChange)
                        AppendRunFormatChange(rPr, imageFormatChange, ctx);
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
            if (runProperties.Count > 0 || run.Properties?.FormatChange is not null)
            {
                var rPr = new W.RunProperties();
                foreach (var element in runProperties) rPr.AppendChild(element);

                // Смена оформления под рецензированием — последним элементом w:rPr.
                if (run.Properties?.FormatChange is { } formatChange)
                    AppendRunFormatChange(rPr, formatChange, ctx);

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
            return new W.Paragraph(BuildBreakRun(block));
        }

        /// <summary>
        /// Ран с разрывом: страницы, а у разрыва колонки — колонки. Отдельным абзацем
        /// (BuildBreakParagraph) или внутри абзаца перед разрывом.
        /// </summary>
        private static W.Run BuildBreakRun(BreakBlock block)
        {
            // Разрывы раздела в потоке блоков переносятся как разрыв страницы:
            // разделы документа описываются списком SectionModel, и импорт
            // приводит внутренние разрывы разделов Word к тем же разрывам страницы.
            // Разрыв колонки из раздела в одну колонку работает как разрыв страницы, но
            // в .docx возвращается тем, чем был в исходнике.
            var breakType = block.BreakType == BreakType.Column || block.FromColumnBreak
                ? W.BreakValues.Column
                : W.BreakValues.Page;

            var brk = new W.Break
            {
                Type = new EnumValue<W.BreakValues>(breakType)
            };

            // Разрыв, поставленный в самом редакторе, помечается в пропускаемом
            // пространстве wsx: Word пометку не видит, а Writersword при открытии файла
            // снова рисует у такого разрыва свою отметку, а не вордовскую.
            if (!block.InParagraph)
                brk.SetAttribute(new OpenXmlAttribute(
                    "wsx", EditorBreakAttribute, DocxTextEffects.WsxNamespace, "1"));

            return new W.Run(brk);
        }

        /// <summary>
        /// Имя пометки wsx у w:br, поставленного в редакторе Writersword, а не пришедшего
        /// из Word. Её читает импорт (ImportService).
        /// </summary>
        internal const string EditorBreakAttribute = "editorBreak";

        private W.Drawing? BuildImageDrawing(Guid imageId, DocxWriteContext ctx)
        {
            if (!ctx.InlineObjects.TryGetValue(imageId, out var image))
                return null;

            string? relationshipId = EnsureImagePart(image, ctx);
            if (relationshipId is null) return null;

            // Нулевой габарит — рисунок Word без места на листе: уходит таким же.
            long cx = (long)Math.Round(Math.Max(image.WidthPt, 0) * EmuPerPoint);
            long cy = (long)Math.Round(Math.Max(image.HeightPt, 0) * EmuPerPoint);

            uint drawingId = ctx.NextDrawingId++;
            string name = string.IsNullOrWhiteSpace(image.ImageFileName)
                ? "Picture " + drawingId.ToString(CultureInfo.InvariantCulture)
                : image.ImageFileName;

            var picture = BuildPictureElement(image, relationshipId, cx, cy, name, ctx);

            var graphic = new Dr.Graphic(
                new Dr.GraphicData(picture)
                {
                    Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture"
                });

            // Картинка в строке текста — всегда рисунок в строке: «сверху и снизу» у
            // неё быть не может, она стоит среди букв своего абзаца.
            return BuildObjectDrawing(
                image, null, cx, cy, drawingId, name, image.AltText, graphic, topAndBottomAnchor: false);
        }

        /// <summary>
        /// Заливка картинки: ссылка на файл и обрезка (a:srcRect) — доли исходного
        /// размера в тысячных долях процента, как их хранит Word.
        /// </summary>
        private static Pic.BlipFill BuildPictureFill(ImageBlock image, string relationshipId)
        {
            var blip = new Dr.Blip { Embed = relationshipId };

            // Непрозрачность картинки у Word — a:alphaModFix в стотысячных.
            if (image.Opacity < 0.9999)
            {
                blip.AppendChild(new Dr.AlphaModulationFixed
                {
                    Amount = (int)Math.Round(Math.Clamp(image.Opacity, 0.0, 1.0) * 100000.0)
                });
            }

            var fill = new Pic.BlipFill(blip);

            bool cropped = image.CropLeftFrac > 0 || image.CropTopFrac > 0
                || image.CropRightFrac > 0 || image.CropBottomFrac > 0;
            if (cropped)
            {
                fill.AppendChild(new Dr.SourceRectangle
                {
                    Left = CropUnits(image.CropLeftFrac),
                    Top = CropUnits(image.CropTopFrac),
                    Right = CropUnits(image.CropRightFrac),
                    Bottom = CropUnits(image.CropBottomFrac)
                });
            }

            fill.AppendChild(new Dr.Stretch(new Dr.FillRectangle()));
            return fill;
        }

        private static int CropUnits(double fraction) =>
            (int)Math.Round(Math.Clamp(fraction, 0.0, 0.95) * 100000.0);

        /// <summary>
        /// Размер картинки вместе с поворотом и отражением (a:xfrm): угол — в
        /// 60000-х долях градуса по часовой стрелке. Размер самой картинки, когда у
        /// Word он отличался от габарита рисунка, уходит прежним, пока рисунок не
        /// меняли в размере.
        /// </summary>
        private static Dr.Transform2D BuildPictureTransform(ImageBlock image, long cx, long cy)
        {
            if (image.WordDrawing is { PictureWidthPt: > 0, PictureHeightPt: > 0 } word
                && Math.Abs(image.WidthPt - word.EffectForWidthPt) < 0.01
                && Math.Abs(image.HeightPt - word.EffectForHeightPt) < 0.01)
            {
                cx = (long)Math.Round(word.PictureWidthPt * EmuPerPoint);
                cy = (long)Math.Round(word.PictureHeightPt * EmuPerPoint);
            }

            var transform = new Dr.Transform2D(
                new Dr.Offset { X = 0L, Y = 0L },
                new Dr.Extents { Cx = cx, Cy = cy });

            double degrees = image.RotationDeg % 360.0;
            if (degrees < 0) degrees += 360.0;
            if (degrees != 0.0)
                transform.Rotation = (int)Math.Round(degrees * 60000.0);

            if (image.FlipHorizontal) transform.HorizontalFlip = true;
            if (image.FlipVertical) transform.VerticalFlip = true;

            return transform;
        }

        /// <summary>
        /// Кладёт файл картинки в пакет один раз и возвращает id связи.
        /// Повторные ссылки на ту же картинку переиспользуют готовую связь.
        ///
        /// Картинка, которую лист показывает перекодированной (TIFF, EMF, WMF из .docx),
        /// уходит своим исходником (<see cref="ImageBlock.SourceImageFileName"/>): тот же
        /// вектор и тот же формат, что был у Word.
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

            string? relationshipId = null;

            if (!string.IsNullOrWhiteSpace(image.SourceImageFileName))
                relationshipId = EnsureImageFilePart(image.SourceImageFileName!, ctx, quiet: true);

            relationshipId ??= EnsureImageFilePart(image.ImageFileName, ctx);
            if (relationshipId is null) return null;

            ctx.ImageRelationshipIds[image.Id] = relationshipId;
            return relationshipId;
        }

        /// <summary>
        /// Кладёт файл проекта в пакет один раз и возвращает id связи. Один и тот же
        /// файл, на который ссылаются несколько рисунков, уходит одной частью — как у
        /// Word, где два рисунка ссылаются на одну связь. Null — файла нет или Word его
        /// формат не знает; quiet — без предупреждения (вызывающий попробует иначе).
        /// </summary>
        private string? EnsureImageFilePart(string fileName, DocxWriteContext ctx, bool quiet = false)
        {
            if (ctx.ImageRelationshipIdsByFile.TryGetValue(fileName, out var known))
                return known;

            if (ctx.ResolveImage is null)
            {
                if (!quiet) ctx.Warnings.Add("Картинки не встроены: содержимое файлов недоступно.");
                return null;
            }

            byte[]? data;
            try
            {
                data = ctx.ResolveImage(fileName);
            }
            catch
            {
                data = null;
            }

            if (data is null || data.Length == 0)
            {
                if (!quiet) ctx.Warnings.Add($"Картинка \"{fileName}\" не найдена и пропущена.");
                return null;
            }

            ImagePart imagePart;
            var partType = ImagePartTypeFor(fileName);
            if (partType is not null)
            {
                imagePart = ctx.MainPart.AddImagePart(partType.Value);
            }
            else if (Path.GetExtension(fileName).Equals(".webp", StringComparison.OrdinalIgnoreCase))
            {
                // WebP Word читает с версии 2019; в наборе типов SDK его нет.
                imagePart = ctx.MainPart.AddImagePart("image/webp");
            }
            else
            {
                if (!quiet)
                    ctx.Warnings.Add($"Картинка \"{fileName}\" в неподдерживаемом Word формате и пропущена.");
                return null;
            }

            using (var stream = new MemoryStream(data, false))
            {
                imagePart.FeedData(stream);
            }

            string relationshipId = ctx.MainPart.GetIdOfPart(imagePart);
            ctx.ImageRelationshipIdsByFile[fileName] = relationshipId;
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

            // Таблица «справа налево» хранит колонки в видимом порядке; Word ждёт
            // логический — первая колонка в разметке стоит у правого края.
            bool bidiVisual = table.BidiVisual;

            // Положение таблицы с обтеканием берётся до перестановки колонок: копия
            // с логическим порядком колонок его не несёт.
            var floatPosition = table.FloatPosition;

            if (bidiVisual)
                table = LogicalColumnOrder(table);

            int columnCount = Math.Max(table.ColumnCount, 1);
            int rowCount = Math.Max(table.RowCount, 1);

            // Все колонки заданы в мм (таблица из Word с сеткой или колонки, растянутые
            // руками): таблица уходит с той же шириной в twips, какая у неё на листе.
            // Иначе ширина — доля полосы набора, а сетка задаёт пропорции между столбцами
            // на условной ширине страницы.
            bool absoluteWidths = table.Columns.Count >= columnCount
                && table.Columns.Take(columnCount).All(c => c.WidthType == TableColumnWidthType.Fixed && c.WidthValue > 0);
            double gridTotalTwips = absoluteWidths
                ? Math.Max(Math.Round(TotalFixedWidthMm(table) * TwipsPerMm), 1.0)
                : 9360.0; // ширина текста на A4 при полях по умолчанию

            var tableWidth = absoluteWidths
                ? new W.TableWidth
                {
                    Type = new EnumValue<W.TableWidthUnitValues>(W.TableWidthUnitValues.Dxa),
                    Width = Math.Round(gridTotalTwips).ToString(CultureInfo.InvariantCulture)
                }
                : new W.TableWidth
                {
                    Type = new EnumValue<W.TableWidthUnitValues>(W.TableWidthUnitValues.Pct),
                    Width = Math.Round(Math.Clamp(table.WidthPercent, 1, 100) * 50)
                        .ToString(CultureInfo.InvariantCulture)
                };

            // Поля ячеек таблицы — по первой ячейке; ячейки с другими полями пишут свои
            // (w:tcMar). По схеме w:tblCellMar стоит после w:tblBorders, перед w:tblLook.
            var firstCell = table.Cells.Count > 0 ? table.Cells[0] : null;
            var tableProperties = new W.TableProperties(tableWidth);

            // Выравнивание таблицы: по схеме w:jc стоит сразу после w:tblW, перед w:tblInd.
            if (table.Alignment != TableBlockAlignment.Left)
            {
                tableProperties.AppendChild(new W.TableJustification
                {
                    Val = table.Alignment == TableBlockAlignment.Center
                        ? W.TableRowAlignmentValues.Center
                        : W.TableRowAlignmentValues.Right
                });
            }

            // Отступ таблицы от поля: по схеме w:tblInd стоит после w:tblW, перед w:tblBorders.
            if (Math.Abs(table.LeftIndentPt) > 0.01)
            {
                tableProperties.AppendChild(new W.TableIndentation
                {
                    Width = (int)Math.Round(table.LeftIndentPt * 20.0),
                    Type = W.TableWidthUnitValues.Dxa
                });
            }

            tableProperties.AppendChild(BuildTableBorders(table));

            if (firstCell is not null)
                tableProperties.AppendChild(BuildTableCellMargins(firstCell));

            tableProperties.AppendChild(new W.TableLook { Val = "04A0" });

            // По схеме w:bidiVisual стоит перед w:tblW.
            if (bidiVisual)
                tableProperties.PrependChild(new W.BiDiVisual());

            // Таблица с обтеканием текстом: по схеме w:tblpPr стоит первым, перед
            // w:bidiVisual и w:tblW.
            if (floatPosition is not null)
                tableProperties.PrependChild(BuildTablePosition(floatPosition));

            result.AppendChild(tableProperties);
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

                // Свойства строки: высота «не менее» (w:trHeight) и повтор заголовка. По
                // схеме CT_TrPr порядок элементов свободный.
                double minHeightPt = table.GetRowMinHeightPt(row);
                bool repeatHeader = table.RepeatHeader && row == 0;

                // Первая строка, которую таблица переносит на новую страницу целиком,
                // у Word — строка с запретом разрыва (w:cantSplit).
                bool cantSplit = table.SplitMode == TableSplitMode.ByRow && row == 0;
                if (minHeightPt > 0 || repeatHeader || cantSplit)
                {
                    var rowProperties = new W.TableRowProperties();
                    if (cantSplit) rowProperties.AppendChild(new W.CantSplit());
                    if (minHeightPt > 0)
                    {
                        rowProperties.AppendChild(new W.TableRowHeight
                        {
                            Val = (UInt32Value)(uint)Math.Round(minHeightPt * 20.0),
                            HeightType = table.IsRowHeightExact(row)
                                ? W.HeightRuleValues.Exact
                                : W.HeightRuleValues.AtLeast
                        });
                    }
                    if (repeatHeader) rowProperties.AppendChild(new W.TableHeader());
                    tableRow.AppendChild(rowProperties);
                }

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
                            cell, span, isVerticalMergeContinuation: false, columnShares, column, gridTotalTwips, ctx,
                            firstCell));
                    }
                    else if (cell.Column == column)
                    {
                        // Продолжение вертикального объединения: ячейка есть, но
                        // содержимое лежит в её «главной» строке.
                        tableRow.AppendChild(BuildTableCell(
                            cell, span, isVerticalMergeContinuation: true, columnShares, column, gridTotalTwips, ctx,
                            firstCell));
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

        /// <summary>Поля таблицы (w:tblCellMar) по полям ячейки, в twips.</summary>
        private static W.TableCellMarginDefault BuildTableCellMargins(Models.Document.TableCell cell)
        {
            return new W.TableCellMarginDefault(
                new W.TopMargin { Width = MarginTwips(cell.PaddingTopPt), Type = W.TableWidthUnitValues.Dxa },
                new W.TableCellLeftMargin { Width = (short)Math.Clamp(Math.Round(cell.PaddingLeftPt * 20.0), 0, short.MaxValue), Type = W.TableWidthValues.Dxa },
                new W.BottomMargin { Width = MarginTwips(cell.PaddingBottomPt), Type = W.TableWidthUnitValues.Dxa },
                new W.TableCellRightMargin { Width = (short)Math.Clamp(Math.Round(cell.PaddingRightPt * 20.0), 0, short.MaxValue), Type = W.TableWidthValues.Dxa });
        }

        /// <summary>Поля одной ячейки (w:tcMar), в twips.</summary>
        private static W.TableCellMargin BuildCellMargins(Models.Document.TableCell cell)
        {
            return new W.TableCellMargin(
                new W.TopMargin { Width = MarginTwips(cell.PaddingTopPt), Type = W.TableWidthUnitValues.Dxa },
                new W.LeftMargin { Width = MarginTwips(cell.PaddingLeftPt), Type = W.TableWidthUnitValues.Dxa },
                new W.BottomMargin { Width = MarginTwips(cell.PaddingBottomPt), Type = W.TableWidthUnitValues.Dxa },
                new W.RightMargin { Width = MarginTwips(cell.PaddingRightPt), Type = W.TableWidthUnitValues.Dxa });
        }

        private static string MarginTwips(double pt) =>
            Math.Max(Math.Round(pt * 20.0), 0).ToString(CultureInfo.InvariantCulture);

        private static bool SameMargins(Models.Document.TableCell a, Models.Document.TableCell b) =>
            Math.Abs(a.PaddingTopPt - b.PaddingTopPt) < 0.01
            && Math.Abs(a.PaddingBottomPt - b.PaddingBottomPt) < 0.01
            && Math.Abs(a.PaddingLeftPt - b.PaddingLeftPt) < 0.01
            && Math.Abs(a.PaddingRightPt - b.PaddingRightPt) < 0.01;

        private static double TotalFixedWidthMm(TableBlock table)
        {
            double total = 0;
            foreach (var column in table.Columns)
                if (column.WidthType == TableColumnWidthType.Fixed)
                    total += column.WidthValue;
            return total;
        }

        /// <summary>
        /// Положение таблицы с обтеканием текстом (w:tblpPr). Атрибуты пишутся по именам:
        /// расстояния до текста, опоры по обеим осям и смещение или сторона опоры.
        /// </summary>
        private static W.TablePositionProperties BuildTablePosition(TableFloatPosition position)
        {
            const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

            var result = new W.TablePositionProperties();

            void Set(string name, string value)
                => result.SetAttribute(new OpenXmlAttribute("w", name, WordNamespace, value));

            static string Twips(double points)
                => ((long)Math.Round(points * 20.0)).ToString(CultureInfo.InvariantCulture);

            static string AnchorName(TableFloatAnchor anchor) => anchor switch
            {
                TableFloatAnchor.Margin => "margin",
                TableFloatAnchor.Page => "page",
                _ => "text"
            };

            if (position.LeftFromTextPt > 0) Set("leftFromText", Twips(position.LeftFromTextPt));
            if (position.RightFromTextPt > 0) Set("rightFromText", Twips(position.RightFromTextPt));
            if (position.TopFromTextPt > 0) Set("topFromText", Twips(position.TopFromTextPt));
            if (position.BottomFromTextPt > 0) Set("bottomFromText", Twips(position.BottomFromTextPt));

            Set("vertAnchor", AnchorName(position.VerticalAnchor));
            Set("horzAnchor", AnchorName(position.HorizontalAnchor));

            // Сторона опоры и смещение взаимно исключают друг друга: при стороне Word
            // смещение не читает.
            switch (position.HorizontalAlign)
            {
                case TableFloatAlign.Start: Set("tblpXSpec", "left"); break;
                case TableFloatAlign.Center: Set("tblpXSpec", "center"); break;
                case TableFloatAlign.End: Set("tblpXSpec", "right"); break;
                default: Set("tblpX", Twips(position.XPt)); break;
            }

            // От текста у Word есть только смещение. Ноль Word читает как «таблица в
            // потоке», поэтому наименьшее смещение — одна двадцатая пункта.
            if (position.VerticalAnchor == TableFloatAnchor.Text || position.VerticalAlign == TableFloatAlign.Offset)
            {
                long yTwips = (long)Math.Round(position.YPt * 20.0);
                if (yTwips == 0 && position.VerticalAnchor == TableFloatAnchor.Text) yTwips = 1;
                Set("tblpY", yTwips.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                Set("tblpYSpec", position.VerticalAlign switch
                {
                    TableFloatAlign.Start => "top",
                    TableFloatAlign.Center => "center",
                    _ => "bottom"
                });
            }

            return result;
        }

        /// <summary>
        /// Копия таблицы «справа налево» с колонками в логическом порядке Word. Ячейки
        /// новые, но абзацы у них те же самые объекты: выгрузка списков и картинок
        /// узнаёт абзацы по ссылке. Выравнивание переводится обратно в логическое:
        /// у правого поля — «к началу», у левого — «к концу».
        /// </summary>
        private static TableBlock LogicalColumnOrder(TableBlock table)
        {
            var logical = new TableBlock
            {
                Id = table.Id,
                RowCount = table.RowCount,
                ColumnCount = table.ColumnCount,
                StyleName = table.StyleName,
                WidthPercent = table.WidthPercent,
                LeftIndentPt = 0,
                Alignment = table.Alignment switch
                {
                    TableBlockAlignment.Right => TableBlockAlignment.Left,
                    TableBlockAlignment.Left => TableBlockAlignment.Right,
                    _ => TableBlockAlignment.Center
                },
                BidiVisual = true,
                RepeatHeader = table.RepeatHeader,
                SplitMode = table.SplitMode,
                BreakLabel = table.BreakLabel,
                ContinuationLabel = table.ContinuationLabel,
                RowMinHeightsPt = new List<double>(table.RowMinHeightsPt),
                ExactHeightRows = table.ExactHeightRows is null ? null : new List<int>(table.ExactHeightRows)
            };

            foreach (var column in table.Columns)
                logical.Columns.Add(new TableColumnDefinition
                {
                    WidthType = column.WidthType,
                    WidthValue = column.WidthValue
                });

            foreach (var cell in table.Cells)
            {
                logical.Cells.Add(new Models.Document.TableCell
                {
                    Id = cell.Id,
                    Paragraphs = cell.Paragraphs,
                    NestedTables = cell.NestedTables,
                    Floats = cell.Floats,
                    Row = cell.Row,
                    Column = cell.Column,
                    RowSpan = cell.RowSpan,
                    ColSpan = cell.ColSpan,
                    BackgroundColor = cell.BackgroundColor,
                    ShadingPattern = cell.ShadingPattern,
                    ShadingPatternColor = cell.ShadingPatternColor,
                    Borders = cell.Borders.Clone(),
                    VerticalAlignment = cell.VerticalAlignment,
                    TextDirection = cell.TextDirection,
                    PaddingTopPt = cell.PaddingTopPt,
                    PaddingBottomPt = cell.PaddingBottomPt,
                    PaddingLeftPt = cell.PaddingLeftPt,
                    PaddingRightPt = cell.PaddingRightPt
                });
            }

            logical.MirrorColumns();
            return logical;
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

        /// <param name="tableMarginsCell">
        /// Ячейка, чьи поля записаны полями таблицы (w:tblCellMar): у ячейки с такими же
        /// полями своих полей нет.
        /// </param>
        private W.TableCell BuildTableCell(
            Models.Document.TableCell cell,
            int span,
            bool isVerticalMergeContinuation,
            double[] shares,
            int column,
            double gridTotalTwips,
            DocxWriteContext ctx,
            Models.Document.TableCell? tableMarginsCell = null)
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

            // Заливка: цвет фона и узор поверх него (pct25, diagStripe…) своим цветом.
            string? background = HexWithoutHash(cell.BackgroundColor);
            string? cellPattern = string.IsNullOrWhiteSpace(cell.ShadingPattern) ? null : cell.ShadingPattern;
            if (background is not null || cellPattern is not null)
            {
                var shading = new W.Shading
                {
                    Color = cellPattern is not null ? HexWithoutHash(cell.ShadingPatternColor) ?? "auto" : "auto",
                    Fill = background ?? "auto"
                };

                if (cellPattern is not null)
                    shading.Val = new EnumValue<W.ShadingPatternValues> { InnerText = cellPattern };
                else
                    shading.Val = new EnumValue<W.ShadingPatternValues>(W.ShadingPatternValues.Clear);

                properties.AppendChild(shading);
            }

            // Свои поля ячейки, если они не те, что у таблицы. По схеме — после w:shd.
            if (tableMarginsCell is not null && !SameMargins(cell, tableMarginsCell))
                properties.AppendChild(BuildCellMargins(cell));

            // Направление текста: по схеме w:textDirection идёт после w:tcMar, перед w:vAlign.
            if (cell.TextDirection != CellTextDirection.Horizontal)
            {
                properties.AppendChild(new W.TextDirection
                {
                    Val = cell.TextDirection == CellTextDirection.BottomToTop
                        ? W.TextDirectionValues.BottomToTopLeftToRight
                        : W.TextDirectionValues.TopToBottomRightToLeft
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

            // Абзацы ячейки и, перед своими абзацами, вложенные таблицы.
            for (int pi = 0; pi <= cell.Paragraphs.Count; pi++)
            {
                if (cell.NestedTables is { Count: > 0 } nestedTables)
                {
                    foreach (var nested in nestedTables)
                    {
                        if (cell.NestedTablePosition(nested) != pi) continue;
                        result.AppendChild(BuildTable(nested.Table, ctx));
                    }
                }

                if (pi < cell.Paragraphs.Count)
                {
                    var cellParagraph = BuildParagraph(cell.Paragraphs[pi], ctx);

                    // Плавающие объекты ячейки — якорями в начало своего абзаца, сразу
                    // за его свойствами, с привязкой к ячейке (layoutInCell), как у Word.
                    if (cell.Floats is { Count: > 0 } cellFloats)
                    {
                        int floatInsertAt = cellParagraph.GetFirstChild<W.ParagraphProperties>() is null ? 0 : 1;
                        foreach (var cellFloat in cellFloats)
                        {
                            if (cell.FloatParagraphIndex(cellFloat) != pi) continue;

                            var floatRun = BuildFlowObjectRun(cellFloat.Object, ctx);
                            if (floatRun is null)
                            {
                                ctx.Warnings.Add("Картинка без файла в .docx не перенесена.");
                                continue;
                            }

                            cellParagraph.InsertAt(floatRun, floatInsertAt++);
                        }
                    }

                    result.AppendChild(cellParagraph);
                }
            }

            // Ячейка у Word обязана кончаться абзацем: пустая ячейка и ячейка, где за
            // вложенной таблицей ничего нет, получают пустой абзац.
            if (result.LastChild is not W.Paragraph)
                result.AppendChild(new W.Paragraph());

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

            // У каждой стороны свои цвет и толщина, если заданы (таблицы из Word).
            var borders = new W.TableCellBorders();
            ApplyBorder(new W.TopBorder(), source.Top, source.EffectiveTopThicknessPt(), source.EffectiveTopColor(), borders);
            ApplyBorder(new W.LeftBorder(), source.Left, source.EffectiveLeftThicknessPt(), source.EffectiveLeftColor(), borders);
            ApplyBorder(new W.BottomBorder(), source.Bottom, source.EffectiveBottomThicknessPt(), source.EffectiveBottomColor(), borders);
            ApplyBorder(new W.RightBorder(), source.Right, source.EffectiveRightThicknessPt(), source.EffectiveRightColor(), borders);
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
            BorderStyle.Triple => W.BorderValues.Triple,
            BorderStyle.Wave => W.BorderValues.Wave,
            BorderStyle.ThreeDEmboss => W.BorderValues.ThreeDEmboss,
            BorderStyle.ThreeDEngrave => W.BorderValues.ThreeDEngrave,
            BorderStyle.Outset => W.BorderValues.Outset,
            BorderStyle.Inset => W.BorderValues.Inset,
            BorderStyle.DotDash => W.BorderValues.DotDash,
            BorderStyle.DotDotDash => W.BorderValues.DotDotDash,
            BorderStyle.DashSmallGap => W.BorderValues.DashSmallGap,
            BorderStyle.DashDotStroked => W.BorderValues.DashDotStroked,
            BorderStyle.ThinThickSmallGap => W.BorderValues.ThinThickSmallGap,
            BorderStyle.ThickThinSmallGap => W.BorderValues.ThickThinSmallGap,
            BorderStyle.ThinThickThinSmallGap => W.BorderValues.ThinThickThinSmallGap,
            BorderStyle.ThinThickMediumGap => W.BorderValues.ThinThickMediumGap,
            BorderStyle.ThickThinMediumGap => W.BorderValues.ThickThinMediumGap,
            BorderStyle.ThinThickThinMediumGap => W.BorderValues.ThinThickThinMediumGap,
            BorderStyle.ThinThickLargeGap => W.BorderValues.ThinThickLargeGap,
            BorderStyle.ThickThinLargeGap => W.BorderValues.ThickThinLargeGap,
            BorderStyle.ThinThickThinLargeGap => W.BorderValues.ThinThickThinLargeGap,
            BorderStyle.DoubleWave => W.BorderValues.DoubleWave,
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
        /// плавающие объекты, поворот и обрезка картинок. Колонтитулы и номера страниц
        /// рисуются тем же расчётом листов, что у полотна.
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
            TextAlignment.Distribute => W.JustificationValues.Distribute,
            _ => W.JustificationValues.Left
        };

        /// <summary>Вид подчёркивания модели в значение w:u Word.</summary>
        internal static W.UnderlineValues UnderlineToOoxml(UnderlineStyle style) => style switch
        {
            UnderlineStyle.Words => W.UnderlineValues.Words,
            UnderlineStyle.Double => W.UnderlineValues.Double,
            UnderlineStyle.Thick => W.UnderlineValues.Thick,
            UnderlineStyle.Dotted => W.UnderlineValues.Dotted,
            UnderlineStyle.DottedHeavy => W.UnderlineValues.DottedHeavy,
            UnderlineStyle.Dash => W.UnderlineValues.Dash,
            UnderlineStyle.DashedHeavy => W.UnderlineValues.DashedHeavy,
            UnderlineStyle.DashLong => W.UnderlineValues.DashLong,
            UnderlineStyle.DashLongHeavy => W.UnderlineValues.DashLongHeavy,
            UnderlineStyle.DotDash => W.UnderlineValues.DotDash,
            UnderlineStyle.DashDotHeavy => W.UnderlineValues.DashDotHeavy,
            UnderlineStyle.DotDotDash => W.UnderlineValues.DotDotDash,
            UnderlineStyle.DashDotDotHeavy => W.UnderlineValues.DashDotDotHeavy,
            UnderlineStyle.Wave => W.UnderlineValues.Wave,
            UnderlineStyle.WavyHeavy => W.UnderlineValues.WavyHeavy,
            UnderlineStyle.WavyDouble => W.UnderlineValues.WavyDouble,
            UnderlineStyle.None => W.UnderlineValues.None,
            _ => W.UnderlineValues.Single
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

                    bool bold = run.Properties.IsBold == true;
                    bool italic = run.Properties.IsItalic == true;
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
        public Dictionary<Guid, ShapeBlock> InlineShapes = new();
        public readonly Dictionary<Guid, string> ImageRelationshipIds = new();
        public readonly Dictionary<string, string> ImageRelationshipIdsByFile = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Warnings = new();
        public uint NextDrawingId = 1;

        /// <summary>Следующий номер правки рецензирования (w:id): номера не повторяются по всему документу.</summary>
        public int NextRevisionId = 1;
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

        /// <summary>
        /// Абзацы, которым нужен свой экземпляр нумерации (w:num) на общем определении
        /// списка: перезапуск счёта (w:startOverride) или уровень Word, отличный от
        /// уровня определения (w:lvlOverride с w:lvl). У Word счёт ведёт определение,
        /// поэтому такой экземпляр продолжает общий счёт — как было в исходном файле.
        /// </summary>
        private readonly List<ListProperties> _overrideParagraphs = new();

        /// <summary>Экземпляр нумерации абзаца с переопределением (по ссылке на его свойства списка).</summary>
        private readonly Dictionary<ListProperties, int> _numberingIdByParagraph = new(ReferenceEqualityComparer.Instance);

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
                            foreach (var paragraph in cell.ParagraphsDeep())
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

            if (!list.ContinueNumbering || DiffersFromDefinition(list, levels))
                _overrideParagraphs.Add(list);
        }

        /// <summary>
        /// Уровни Word абзаца расходятся с уровнями первого абзаца списка, по которым
        /// строится определение: у абзаца свой вид номера (переопределение экземпляра).
        /// </summary>
        private static bool DiffersFromDefinition(ListProperties list, SortedDictionary<int, ListProperties> levels)
        {
            if (list.WordLevels is null) return false;

            var definition = DefinitionWordLevels(levels);
            if (definition is null) return false;

            for (int i = 0; i < list.WordLevels.Count; i++)
            {
                var own = list.WordLevels[i];
                var shared = i < definition.Count ? definition[i] : null;
                if (!own.SameAs(shared)) return true;
            }

            return false;
        }

        /// <summary>Уровни Word, по которым строится определение списка: у первого его абзаца.</summary>
        private static List<WordListLevel>? DefinitionWordLevels(SortedDictionary<int, ListProperties> levels)
        {
            foreach (var pair in levels)
            {
                if (pair.Value.WordLevels is not null) return pair.Value.WordLevels;
            }

            return null;
        }

        public int? GetNumberingId(Guid listId) =>
            _numberingIdByList.TryGetValue(listId, out var value) ? value : null;

        /// <summary>
        /// Экземпляр нумерации абзаца: свой, если у абзаца переопределение, иначе общий
        /// экземпляр его списка.
        /// </summary>
        public int? GetNumberingId(ListProperties list) =>
            _numberingIdByParagraph.TryGetValue(list, out var own) ? own : GetNumberingId(list.ListId);

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

                // Список из Word уходит всеми своими уровнями: номер «%1.%2.» второго
                // уровня собирается и из первого, даже если пунктов первого уровня нет.
                var definitionLevels = DefinitionWordLevels(pair.Value);
                if (definitionLevels is not null)
                    maxLevel = Math.Max(maxLevel, definitionLevels.Count - 1);

                for (int level = 0; level <= maxLevel; level++)
                {
                    var properties = pair.Value.TryGetValue(level, out var found) ? found : previous;

                    // Уровень Word без своих пунктов выше первого пункта списка: отступы
                    // берутся у первого пункта, вид номера — у самого уровня.
                    if (properties is null && definitionLevels is not null && level < definitionLevels.Count)
                        properties = pair.Value.Values.First();
                    if (properties is null) continue;

                    previous = properties;
                    var wordLevel = definitionLevels is not null && level < definitionLevels.Count
                        ? definitionLevels[level]
                        : null;
                    abstractNum.AppendChild(BuildLevel(properties, level, wordLevel));
                }

                numbering.AppendChild(abstractNum);

                instances.Add(new W.NumberingInstance(new W.AbstractNumId { Val = id })
                {
                    NumberID = id
                });

                _numberingIdByList[pair.Key] = id;
                id++;
            }

            // Экземпляры с переопределениями идут на определениях своих списков.
            foreach (var list in _overrideParagraphs)
            {
                if (!_numberingIdByList.TryGetValue(list.ListId, out int abstractId)) continue;

                var instance = new W.NumberingInstance(new W.AbstractNumId { Val = abstractId }) { NumberID = id };
                var definition = DefinitionWordLevels(_levelsByList[list.ListId]);
                int ownLevel = Math.Clamp(list.Level, 0, 8);
                int levelCount = Math.Max(list.WordLevels?.Count ?? 0, ownLevel + 1);

                for (int level = 0; level < levelCount; level++)
                {
                    bool restart = !list.ContinueNumbering && level == ownLevel;
                    var own = list.WordLevelAt(level);
                    var shared = definition is not null && level < definition.Count ? definition[level] : null;
                    bool differs = own is not null && !own.SameAs(shared);
                    if (!restart && !differs) continue;

                    // По схеме CT_NumLvl: сначала w:startOverride, затем w:lvl.
                    var levelOverride = new W.LevelOverride { LevelIndex = level };
                    if (restart)
                        levelOverride.AppendChild(new W.StartOverrideNumberingValue { Val = list.StartAt });
                    if (differs)
                        levelOverride.AppendChild(BuildLevel(list, level, own));

                    instance.AppendChild(levelOverride);
                }

                instances.Add(instance);
                _numberingIdByParagraph[list] = id;
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
        /// <param name="wordLevel">
        /// Уровень Word, пришедший из .docx: вид номера пишется им, как был в исходнике.
        /// null — уровень списка Writersword.
        /// </param>
        private W.Level BuildLevel(ListProperties properties, int level, WordListLevel? wordLevel = null)
        {
            var (format, text) = wordLevel is not null
                ? (MapWordFormat(wordLevel), wordLevel.Text)
                : MapMarker(properties, level);

            var result = new W.Level { LevelIndex = level };

            int start = wordLevel?.Start ?? Math.Max(properties.StartAt, 1);
            result.AppendChild(new W.StartNumberingValue { Val = start });
            result.AppendChild(new W.NumberingFormat { Val = new EnumValue<W.NumberFormatValues>(format) });

            // Разделитель за номером: у Word по умолчанию табуляция, её не пишем.
            if (wordLevel is not null && wordLevel.Suffix is ListMarkerSuffix.Space or ListMarkerSuffix.Nothing)
            {
                result.AppendChild(new W.LevelSuffix
                {
                    Val = new EnumValue<W.LevelSuffixValues>(wordLevel.Suffix == ListMarkerSuffix.Space
                        ? W.LevelSuffixValues.Space
                        : W.LevelSuffixValues.Nothing)
                });
            }

            result.AppendChild(new W.LevelText { Val = text });
            result.AppendChild(new W.LevelJustification
            {
                Val = new EnumValue<W.LevelJustificationValues>((wordLevel?.Alignment ?? ListMarkerAlignment.Left) switch
                {
                    ListMarkerAlignment.Right => W.LevelJustificationValues.Right,
                    ListMarkerAlignment.Center => W.LevelJustificationValues.Center,
                    _ => W.LevelJustificationValues.Left
                })
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

            // Шрифт маркера уровня (Symbol, Wingdings, Courier New): по схеме — за w:pPr.
            if (!string.IsNullOrWhiteSpace(wordLevel?.FontFamily))
            {
                result.AppendChild(new W.NumberingSymbolRunProperties(new W.RunFonts
                {
                    Ascii = wordLevel!.FontFamily,
                    HighAnsi = wordLevel.FontFamily,
                    Hint = new EnumValue<W.FontTypeHintValues>(W.FontTypeHintValues.Default)
                }));
            }

            return result;
        }

        /// <summary>Формат номера уровня Word в w:numFmt.</summary>
        private static W.NumberFormatValues MapWordFormat(WordListLevel wordLevel) => wordLevel.Format switch
        {
            ListMarkerType.Decimal => W.NumberFormatValues.Decimal,
            ListMarkerType.DecimalLeadingZero => W.NumberFormatValues.DecimalZero,
            ListMarkerType.LowerAlpha => W.NumberFormatValues.LowerLetter,
            ListMarkerType.UpperAlpha => W.NumberFormatValues.UpperLetter,
            ListMarkerType.LowerRoman => W.NumberFormatValues.LowerRoman,
            ListMarkerType.UpperRoman => W.NumberFormatValues.UpperRoman,
            ListMarkerType.Ordinal => W.NumberFormatValues.Ordinal,
            ListMarkerType.CardinalText => W.NumberFormatValues.CardinalText,
            ListMarkerType.OrdinalText => W.NumberFormatValues.OrdinalText,
            ListMarkerType.RussianLower => W.NumberFormatValues.RussianLower,
            ListMarkerType.RussianUpper => W.NumberFormatValues.RussianUpper,
            ListMarkerType.Chicago => W.NumberFormatValues.Chicago,
            _ => wordLevel.IsNumbered ? W.NumberFormatValues.Decimal : W.NumberFormatValues.Bullet
        };

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
                ListMarkerType.Ordinal => (W.NumberFormatValues.Ordinal, counted),
                ListMarkerType.CardinalText => (W.NumberFormatValues.CardinalText, counted),
                ListMarkerType.OrdinalText => (W.NumberFormatValues.OrdinalText, counted),
                ListMarkerType.RussianLower => (W.NumberFormatValues.RussianLower, counted),
                ListMarkerType.RussianUpper => (W.NumberFormatValues.RussianUpper, counted),
                ListMarkerType.Chicago => (W.NumberFormatValues.Chicago, counted),
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
        public UnderlineStyle UnderlineKind;
        public string? UnderlineColor;
        public bool Strikethrough;
        public bool DoubleStrikethrough;
        public bool Superscript;
        public bool Subscript;
        public bool AllCaps;
        public bool SmallCaps;
        public string? TextColor;
        public string? HighlightColor;

        public ResolvedRunStyle Clone() => (ResolvedRunStyle)MemberwiseClone();

        /// <summary>
        /// Свойства рана переопределяют значения стиля: незаданными остаются
        /// шрифт, размер и цвета (у них есть состояние «унаследовать»), а флаги
        /// начертания объект несёт целиком — как и указано в модели документа.
        ///
        /// Жирность и курсив трёхзначные. У стиля не заданное значение читается как
        /// «выключено» — так же, как его читает StyleResolver.ResolveBold, иначе экспорт
        /// разошёлся бы с тем, что на экране. У рана не заданное значение оставляет то,
        /// что пришло от стиля (<paramref name="unsetInherits"/> = true).
        /// </summary>
        public void Apply(Models.Inline.RunProperties? properties, bool unsetInherits = false)
        {
            if (properties is null) return;

            if (!string.IsNullOrWhiteSpace(properties.FontFamily)) FontFamily = properties.FontFamily!;
            if (properties.FontSize is double size && size > 0) FontSize = size;
            if (properties.TextColor is not null) TextColor = properties.TextColor;
            if (properties.HighlightColor is not null) HighlightColor = properties.HighlightColor;

            Bold = properties.IsBold ?? (unsetInherits && Bold);
            Italic = properties.IsItalic ?? (unsetInherits && Italic);
            Underline = properties.IsUnderline;
            UnderlineKind = properties.UnderlineStyle;
            UnderlineColor = properties.UnderlineColor;
            Strikethrough = properties.IsStrikethrough;
            DoubleStrikethrough = properties.IsDoubleStrikethrough;
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
        public bool ContextualSpacing;
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
            if (properties.ContextualSpacing is bool contextual) ContextualSpacing = contextual;

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
            result.Apply(run.Properties, unsetInherits: true);
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

        // ── Колонтитулы ─────────────────────────────────────────────────────
        //
        // Колонтитулу нужно знать, сколько всего листов и где начинаются главы и
        // метки правил, а PDF пишется потоком: лист, закрытый однажды, уже не
        // дорисовать. Поэтому при колонтитулах документ раскладывается дважды —
        // первый раз вхолостую, только чтобы узнать листы, второй раз набело.

        // Лист, который сейчас рисуется, с нуля.
        private int _pageIndex = -1;

        // Холостой проход: листы считаются, колонтитулы не рисуются.
        private bool _countingPass;

        // Абзац → лист, где он начинается; листы с началом главы. Собираются в
        // холостом проходе.
        private readonly Dictionary<Guid, int> _paragraphStartPages = new();
        private readonly HashSet<int> _chapterStartPages = new();

        // Оформление листов для чистового прохода. Пусто — колонтитулов нет.
        private PageDecoration[] _decorations = Array.Empty<PageDecoration>();

        public PdfExportEngine(DocumentModel document, Func<string, byte[]?>? resolveImage)
        {
            _document = document;
            _resolveImage = resolveImage;
            _styles = new DocumentStyleResolver(document);
        }

        public IReadOnlyList<string> Warnings => _warnings;

        public void Export(string outputPath)
        {
            var headerFooter = _document.HeaderFooter;
            if (headerFooter is not null && !headerFooter.IsEmpty)
            {
                _countingPass = true;
                RenderDocument(Stream.Null);
                _countingPass = false;

                _decorations = PageNumbering.Compute(headerFooter, _pageIndex + 1,
                    _paragraphStartPages, _chapterStartPages);

                ResetPassState();
            }

            using var stream = File.Create(outputPath);
            RenderDocument(stream);
        }

        /// <summary>Возвращает раскладку к началу документа перед вторым проходом.</summary>
        private void ResetPassState()
        {
            _pdf?.Dispose();
            _pdf = null;
            _canvas = null;
            _pageOpen = false;
            _y = 0f;
            _pageIndex = -1;
            _insideCell = false;
            _listCounters.Clear();
        }

        private void RenderDocument(Stream stream)
        {
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

                var columnSettings = section.ColumnSettings ?? _document.ColumnSettings;
                if (columnSettings.ColumnCount > 1)
                    _warnings.Add("Многоколоночная вёрстка в PDF не воспроизводится: текст идёт одной колонкой.");

                BeginPage();

                // Интервалы между абзацами одного стиля снимаются по соседству — вывод
                // до раскладки, как у редактора.
                Writersword.Modules.TextEditor.Rendering.ContextualSpacingRules.Apply(section.Blocks,
                    paragraph => _styles.ResolveParagraph(paragraph).ContextualSpacing);

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

            // Колонтитул выше поля листа отодвигает текст, как на полотне и в печати.
            var (reserveTop, reserveBottom) = Rendering.HeaderFooterPainter.BodyReservePt(_document.HeaderFooter,
                (float)(settings.HeaderDistanceMm * PointsPerMm),
                (float)(settings.FooterDistanceMm * PointsPerMm),
                GetTypeface, _styles.ResolveParagraph(new ParagraphBlock()).BaseRun.FontFamily);
            _top = Math.Max(_top, reserveTop);
            _bottom = Math.Min(_bottom, _pageHeight - reserveBottom);

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
            _pageIndex++;
        }

        private void EndPage()
        {
            if (!_pageOpen) return;

            DrawHeaderFooter();

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

            float spaceBefore = paragraph.SuppressSpaceBefore ? 0f : (float)style.SpaceBefore;
            float spaceAfter = paragraph.SuppressSpaceAfter ? 0f : (float)style.SpaceAfter;
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

                if (i == 0 && !_insideCell && _countingPass)
                    NoteParagraphStart(paragraph);

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

        /// <summary>
        /// Холостой проход: абзац начался на текущем листе. По этим записям считаются
        /// метки правил и начала глав.
        /// </summary>
        private void NoteParagraphStart(ParagraphBlock paragraph)
        {
            if (_pageIndex < 0) return;

            _paragraphStartPages.TryAdd(paragraph.Id, _pageIndex);
            if (HeadingCollapseService.LevelOf(paragraph, _document) == 1)
                _chapterStartPages.Add(_pageIndex);
        }

        /// <summary>Колонтитулы закрываемого листа — тем же художником, что на полотне.</summary>
        private void DrawHeaderFooter()
        {
            if (_countingPass || _canvas is null) return;

            var settings = _document.HeaderFooter;
            if (settings is null || _pageIndex < 0 || _pageIndex >= _decorations.Length) return;

            var pageSettings = _document.PageSettings;
            var box = new Rendering.HeaderFooterPageBox(
                0f, 0f, _pageWidth, _pageHeight,
                _left, _pageWidth - _right, _top, _pageHeight - _bottom,
                (float)(pageSettings.HeaderDistanceMm * PointsPerMm),
                (float)(pageSettings.FooterDistanceMm * PointsPerMm));

            var color = ParseColor(ExportService.HexWithoutHash(settings.TextColor), new SKColor(0x1A, 0x1A, 0x1A));
            string family = _styles.ResolveParagraph(new ParagraphBlock()).BaseRun.FontFamily;

            Rendering.HeaderFooterPainter.DrawPage(_canvas, settings, _decorations[_pageIndex], box, color,
                GetTypeface, family);
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

                    // Скрытый текст не печатается.
                    if (run.Properties?.IsHidden == true) continue;

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

            // Рисунок нулевого размера у Word не виден — не виден и в PDF.
            if (image.WidthPt <= 0 || image.HeightPt <= 0) return null;

            float maxWidth = _right - _left;
            float width = (float)image.WidthPt;
            float height = (float)image.HeightPt;

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
            // Упрощённая вёрстка PDF растянутое выравнивание тянет как по ширине: разводки
            // букв последней строки у неё нет.
            if ((alignment == TextAlignment.Justify || alignment == TextAlignment.Distribute) && !line.IsLast)
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

                    if (piece.Style.Underline)
                        DrawPieceUnderline(piece, cursor, pieceBaseline,
                            extraPerSpace > 0f && i < line.Atoms.Count - 1 && atom.EndsWithSpace
                                && ReferenceEquals(piece, atom.Pieces[^1])
                                ? extraPerSpace
                                : 0f);

                    // Зачёркивание — тем же художником, что и на экране: высота линии из
                    // метрик шрифта, двойное — двумя линиями.
                    if (piece.Style.Strikethrough || piece.Style.DoubleStrikethrough)
                    {
                        Rendering.StrikePainter.Draw(_canvas, piece.Style.DoubleStrikethrough,
                            cursor, piece.Width, pieceBaseline, (float)piece.Style.FontSize,
                            piece.Font, _textPaint.Color, null);
                    }

                    cursor += piece.Width;
                }

                if (extraPerSpace > 0f && i < line.Atoms.Count - 1 && atom.EndsWithSpace)
                    cursor += extraPerSpace;
            }
        }

        /// <summary>
        /// Подчёркивание куска строки тем же рисунком, что и на экране.
        /// </summary>
        /// <param name="piece">Кусок текста.</param>
        /// <param name="x">Левый край куска.</param>
        /// <param name="baseline">Базовая линия куска.</param>
        /// <param name="justifyExtra">
        /// Добавка растяжки за пробелом в конце куска при выравнивании по ширине: линия
        /// тянется и под неё, иначе между подчёркнутыми словами остались бы щели.
        /// </param>
        private void DrawPieceUnderline(PdfPiece piece, float x, float baseline, float justifyExtra)
        {
            if (_canvas is null) return;

            var style = piece.Style.UnderlineKind == UnderlineStyle.None
                ? UnderlineStyle.Single
                : piece.Style.UnderlineKind;

            float width = piece.Width + justifyExtra;

            // «Только слова»: пробелы по краям слова и растяжка за ним не подчёркиваются.
            if (Rendering.UnderlineShape.Of(style).WordsOnly)
            {
                string word = piece.Text.TrimEnd(' ', '\t');
                if (word.Trim(' ', '\t').Length == 0) return;
                width = word.Length == piece.Text.Length
                    ? piece.Width
                    : piece.Font.MeasureText(word);
            }

            SKColor color = _textPaint.Color;
            string? ownColor = ExportService.HexWithoutHash(piece.Style.UnderlineColor);
            if (ownColor is not null) color = ParseColor(ownColor, color);

            Rendering.UnderlinePainter.Draw(_canvas, style, x, width, baseline,
                (float)piece.Style.FontSize, color, null, piece.Style.Bold);
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
            var rowHeights = MeasureRowHeights(table, rowCount, columnWidths);

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

        /// <summary>
        /// Высоты строк таблицы при заданных ширинах колонок: по самой высокой ячейке
        /// строки, не ниже заданной высоты строки.
        /// </summary>
        private float[] MeasureRowHeights(TableBlock table, int rowCount, float[] columnWidths)
        {
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

            return rowHeights;
        }

        /// <summary>
        /// Высота таблицы, вложенной в ячейку, в области содержимого заданной ширины —
        /// тем же счётом ширины и строк, каким её рисует DrawTable.
        /// </summary>
        private float MeasureNestedTableHeight(TableBlock table, float availableWidth)
        {
            int rowCount = Math.Max(table.RowCount, 1);
            int columnCount = Math.Max(table.ColumnCount, 1);

            float tableWidth = availableWidth * (float)Math.Clamp(table.WidthPercent, 1, 100) / 100f;
            float leftIndent = (float)Math.Max(table.LeftIndentPt, 0);

            if (leftIndent + tableWidth > availableWidth)
                tableWidth = Math.Max(availableWidth - leftIndent, 36f);

            var columnWidths = ComputeColumnWidths(table, columnCount, tableWidth);
            return MeasureRowHeights(table, rowCount, columnWidths).Sum();
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

            // Абзацы ячейки и, перед своими абзацами, вложенные таблицы. Таблица рисуется
            // в области содержимого ячейки и сдвигает то, что стоит под ней.
            for (int pi = 0; pi <= cell.Paragraphs.Count; pi++)
            {
                if (cell.NestedTables is { Count: > 0 } nestedTables)
                {
                    foreach (var nested in nestedTables)
                        if (cell.NestedTablePosition(nested) == pi) DrawTable(nested.Table);
                }

                if (pi < cell.Paragraphs.Count)
                    DrawParagraph(cell.Paragraphs[pi], null);
            }

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

                float spaceBefore = paragraph.SuppressSpaceBefore ? 0f : (float)style.SpaceBefore;
                float spaceAfter = paragraph.SuppressSpaceAfter ? 0f : (float)style.SpaceAfter;
                total += spaceBefore + lines.Sum(line => line.Height) + spaceAfter;
            }

            // Таблицы внутри ячейки занимают в ней свою высоту.
            if (cell.NestedTables is { Count: > 0 } nestedTables)
            {
                foreach (var nested in nestedTables)
                    total += MeasureNestedTableHeight(nested.Table, width);
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
