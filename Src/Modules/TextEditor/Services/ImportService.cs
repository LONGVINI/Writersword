using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.Models.Styles;
using W = DocumentFormat.OpenXml.Wordprocessing;
using Wp = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using Dr = DocumentFormat.OpenXml.Drawing;
using Pic = DocumentFormat.OpenXml.Drawing.Pictures;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Результат импорта документа.
    /// </summary>
    public sealed class ImportResult
    {
        public bool Success { get; set; }
        public DocumentModel? Document { get; set; }
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// Предупреждения о потере форматирования при импорте.
        /// </summary>
        public string[] Warnings { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Байты изображений, извлечённых из импортируемого файла.
        /// Ключ — сгенерированное имя файла (совпадает с <see cref="Models.Document.ImageBlock.ImageFileName"/>
        /// у соответствующей картинки в возвращённом <see cref="Document"/>), значение — содержимое файла.
        /// Импортёр текста никогда не пишет напрямую в хранилище проекта (у него нет доступа
        /// к контексту открытой вкладки) — эти байты обязан сохранить вызывающий код
        /// (см. <see cref="ViewModels.DocumentViewModel.ImportFromFile"/>) через
        /// <c>DocumentContext.WriteFile("TextEditor/Images/{fileName}", bytes)</c> до того,
        /// как документ станет активным — иначе ссылки на картинки повиснут.
        /// </summary>
        public Dictionary<string, byte[]> ExtractedImages { get; set; } = new();

        public static ImportResult Ok(DocumentModel doc, string[] warnings = null!) =>
            new() { Success = true, Document = doc, Warnings = warnings ?? Array.Empty<string>() };

        public static ImportResult Fail(string error) =>
            new() { Success = false, ErrorMessage = error };
    }

    /// <summary>
    /// Импортирует документы из внешних форматов в <see cref="DocumentModel"/>.
    /// Writersword-специфичные метки (персонажи, таймлайн) не могут быть восстановлены
    /// из внешних форматов — только базовое форматирование.
    /// </summary>
    public sealed class ImportService
    {
        // Единицы измерения OOXML: twips = 1/20 пункта = 1/1440 дюйма.
        private const double TwipsPerPoint = 20.0;
        private const double TwipsPerMm = 1440.0 / 25.4;
        private const double EmuPerPoint = 12700.0;
        private const double HalfPointsPerPoint = 2.0;
        private const double EighthsPerPoint = 8.0;

        /// <summary>
        /// Импортирует .docx файл в DocumentModel через DocumentFormat.OpenXml.
        /// Стили Word (по цепочке BasedOn + docDefaults) разрешаются в конкретные
        /// свойства и записываются на параграфы/раны напрямую — так внешний вид
        /// не зависит от произвольных пользовательских стилей исходного файла,
        /// которых нет в наборе встроенных стилей Writersword.
        /// Заголовки (Heading1–6), цитаты и код дополнительно помечаются именем
        /// стиля Writersword — для соответствующей семантики (структура, регистр).
        /// Поддерживается: параграфы, форматирование текста, списки (маркированные
        /// и нумерованные), таблицы (включая объединение ячеек, границы, заливку),
        /// картинки в тексте, разрывы страниц, параметры страницы финального раздела.
        /// Не поддерживается (игнорируется с предупреждением в Warnings):
        /// многораздельные документы (кроме последнего раздела), колонтитулы,
        /// сноски/концевые сноски, комментарии, отслеживание изменений (принимаются
        /// как есть), вложенные таблицы, векторные картинки (WMF/EMF), обтекание
        /// текстом у плавающих объектов (импортируются как обычные картинки в тексте).
        /// </summary>
        public Task<ImportResult> ImportFromDocxAsync(string filePath)
        {
            return Task.Run(() =>
            {
                try
                {
                    return ImportFromDocxCore(filePath);
                }
                catch (Exception ex)
                {
                    return ImportResult.Fail(ex.Message);
                }
            });
        }

        private ImportResult ImportFromDocxCore(string filePath)
        {
            var warnings = new List<string>();
            var extractedImages = new Dictionary<string, byte[]>();

            using var wordDoc = WordprocessingDocument.Open(filePath, false);
            var mainPart = wordDoc.MainDocumentPart;
            var body = mainPart?.Document?.Body;
            if (mainPart is null || body is null)
                return ImportResult.Fail("Файл не содержит тела документа (возможно, повреждён или это не .docx).");

            string? title = null;
            try { title = wordDoc.PackageProperties.Title; } catch { /* нечитаемые метаданные не критичны */ }
            if (string.IsNullOrWhiteSpace(title))
                title = Path.GetFileNameWithoutExtension(filePath);

            var doc = DocumentModel.CreateNew(title!);
            doc.Styles = new List<DocumentStyle>(DocumentStyle.CreateBuiltInStyles());
            var section = doc.Sections[0];
            section.Blocks.Clear();

            var resolver = new DocxFormatResolver(mainPart);
            var numbering = new DocxNumberingMap(mainPart);
            var listIdMap = new Dictionary<int, Guid>();

            ApplyFinalSectionPageSettings(body, doc, resolver, warnings);

            int nonFinalSectionBreaks = 0;
            foreach (var element in body.Elements())
            {
                switch (element)
                {
                    case W.Paragraph p:
                        if (HasOwnSectionProperties(p)) nonFinalSectionBreaks++;
                        ImportParagraphWithBreaks(p, section, resolver, numbering, listIdMap,
                            mainPart, extractedImages, warnings);
                        break;

                    case W.Table t:
                        var tableBlock = ImportTable(t, section, resolver, numbering, listIdMap,
                            mainPart, extractedImages, warnings, depth: 0);
                        if (tableBlock is not null)
                            section.Blocks.Add(tableBlock);
                        break;

                    // W.SectionProperties как прямой потомок Body — параметры финального
                    // раздела, уже учтены в ApplyFinalSectionPageSettings.
                }
            }

            if (section.Blocks.Count == 0)
                section.Blocks.Add(new ParagraphBlock());

            if (nonFinalSectionBreaks > 0)
                warnings.Add(
                    $"Документ содержит {nonFinalSectionBreaks} внутр. разрыв(ов) раздела — " +
                    "импортированы как разрывы страницы; параметры страницы применены только " +
                    "из последнего раздела документа.");

            var result = ImportResult.Ok(doc, warnings.Distinct().ToArray());
            result.ExtractedImages = extractedImages;
            return result;
        }

        // ── Параграфы и разрывы страниц ────────────────────────────────────

        /// <summary>
        /// Импортирует один W.Paragraph. Разрыв страницы (w:br type="page") внутри
        /// параграфа не имеет аналога «в середине абзаца» в модели Writersword —
        /// разрыв там отдельный блок потока. Поэтому параграф режется на несколько
        /// ParagraphBlock, между которыми вставляется BreakBlock(Page).
        /// </summary>
        private void ImportParagraphWithBreaks(
            W.Paragraph p,
            SectionModel section,
            DocxFormatResolver resolver,
            DocxNumberingMap numbering,
            Dictionary<int, Guid> listIdMap,
            MainDocumentPart mainPart,
            Dictionary<string, byte[]> extractedImages,
            List<string> warnings)
        {
            var effPara = resolver.ResolveEffectiveParagraph(p);
            string? styleName = resolver.MapStyleName(p, effPara);

            var segments = SplitRunsByPageBreak(p);

            for (int i = 0; i < segments.Count; i++)
            {
                var para = new ParagraphBlock();
                para.Properties = effPara.ToParagraphProperties(styleName);
                para.ListProperties = numbering.Resolve(p, listIdMap);

                var chunk = new TextChunk();
                para.Chunks.Clear();
                para.Chunks.Add(chunk);
                chunk.Runs.Clear();

                foreach (var runElement in segments[i])
                    AppendRunOrDrawing(runElement, chunk, section, resolver, effPara,
                        mainPart, extractedImages, warnings);

                if (chunk.Runs.Count == 0)
                    chunk.Runs.Add(new RunModel { Text = string.Empty, Properties = effPara.ToRunProperties() });

                chunk.InvalidateLength();
                section.Blocks.Add(para);

                if (i < segments.Count - 1)
                    section.Blocks.Add(new BreakBlock { BreakType = BreakType.Page });
            }
        }

        /// <summary>
        /// Делит содержимое параграфа на сегменты по разрывам страниц (w:br type="page").
        /// Каждый сегмент — список дочерних элементов Run/Hyperlink/... между разрывами.
        /// </summary>
        private static List<List<OpenXmlElement>> SplitRunsByPageBreak(W.Paragraph p)
        {
            var segments = new List<List<OpenXmlElement>> { new() };

            foreach (var child in p.ChildElements)
            {
                if (child is W.ParagraphProperties) continue;

                if (child is W.Run run && run.Elements<W.Break>().Any(b => b.Type?.Value == W.BreakValues.Page))
                {
                    // Ран может содержать текст ДО разрыва и после — в большинстве
                    // документов разрыв страницы занимает ран целиком, но на всякий
                    // случай текст до/после разрыва распределяем по сегментам.
                    var before = new W.Run(run.RunProperties?.CloneNode(true) ?? new W.RunProperties());
                    var after = new W.Run(run.RunProperties?.CloneNode(true) ?? new W.RunProperties());
                    bool seenBreak = false;
                    foreach (var rc in run.ChildElements)
                    {
                        if (rc is W.Break brk && brk.Type?.Value == W.BreakValues.Page)
                        {
                            seenBreak = true;
                            continue;
                        }
                        if (rc is W.RunProperties) continue;
                        (seenBreak ? after : before).AppendChild(rc.CloneNode(true));
                    }

                    if (before.ChildElements.Count > 0) segments[^1].Add(before);
                    segments.Add(new List<OpenXmlElement>());
                    if (after.ChildElements.Count > 0) segments[^1].Add(after);
                    continue;
                }

                segments[^1].Add(child);
            }

            if (segments.Count == 0) segments.Add(new List<OpenXmlElement>());
            return segments;
        }

        private static bool HasOwnSectionProperties(W.Paragraph p) =>
            p.ParagraphProperties?.SectionProperties is not null;

        /// <summary>
        /// Разворачивает один дочерний элемент параграфа (ран, гиперссылка, вставка/удаление
        /// при отслеживании правок) в раны нашей модели, дописывая их в чанк.
        /// </summary>
        private void AppendRunOrDrawing(
            OpenXmlElement element,
            TextChunk chunk,
            SectionModel section,
            DocxFormatResolver resolver,
            EffectiveParagraph effPara,
            MainDocumentPart mainPart,
            Dictionary<string, byte[]> extractedImages,
            List<string> warnings)
        {
            switch (element)
            {
                case W.Run run:
                    AppendRun(run, chunk, section, resolver, effPara, mainPart, extractedImages, warnings);
                    break;

                case W.Hyperlink hyperlink:
                    // Гиперссылка — просто контейнер ранов; сам URL не хранится в модели
                    // Writersword (гиперссылки как отдельная сущность не реализованы),
                    // текст ссылки импортируется как обычный форматированный текст.
                    foreach (var innerRun in hyperlink.Elements<W.Run>())
                        AppendRun(innerRun, chunk, section, resolver, effPara, mainPart, extractedImages, warnings);
                    break;

                case W.InsertedRun ins:
                    foreach (var innerRun in ins.Elements<W.Run>())
                        AppendRun(innerRun, chunk, section, resolver, effPara, mainPart, extractedImages, warnings);
                    break;

                case W.DeletedRun:
                    // Текст, удалённый с отслеживанием правок — не переносим в импорт
                    // (эквивалент «принять все правки» для удалений).
                    break;
            }
        }

        private void AppendRun(
            W.Run run,
            TextChunk chunk,
            SectionModel section,
            DocxFormatResolver resolver,
            EffectiveParagraph effPara,
            MainDocumentPart mainPart,
            Dictionary<string, byte[]> extractedImages,
            List<string> warnings)
        {
            var runProps = resolver.ResolveEffectiveRun(run, effPara).ToRunProperties();

            foreach (var child in run.ChildElements)
            {
                switch (child)
                {
                    case W.Text t:
                        chunk.Runs.Add(new RunModel { Text = t.Text, Properties = runProps });
                        break;

                    case W.TabChar:
                        chunk.Runs.Add(new RunModel { Text = "\t", Properties = runProps });
                        break;

                    case W.CarriageReturn:
                    case W.Break brk when brk.Type?.Value != W.BreakValues.Page:
                        // Разрыв строки внутри абзаца (Shift+Enter) — переносим как перевод строки
                        // в тексте: раскладка абзаца интерпретирует \n как мягкий перенос строки.
                        chunk.Runs.Add(new RunModel { Text = "\n", Properties = runProps });
                        break;

                    case W.Drawing drawing:
                        ImportDrawing(drawing, chunk, section, runProps, mainPart, extractedImages, warnings);
                        break;

                    case W.FootnoteReference:
                    case W.EndnoteReference:
                        warnings.Add("Сноски/концевые сноски не поддерживаются и были пропущены.");
                        break;
                }
            }
        }

        private void ImportDrawing(
            W.Drawing drawing,
            TextChunk chunk,
            SectionModel section,
            RunProperties runProps,
            MainDocumentPart mainPart,
            Dictionary<string, byte[]> extractedImages,
            List<string> warnings)
        {
            Wp.Inline? inline = drawing.Inline;
            Wp.Anchor? anchor = drawing.Anchor;
            bool isFloating = anchor is not null;

            // У плавающего объекта (wp:anchor) графика лежит дочерним элементом:
            // отдельного свойства, как у wp:inline, у него нет.
            Dr.Graphic? graphic = inline?.Graphic ?? anchor?.GetFirstChild<Dr.Graphic>();
            long extentCx = inline?.Extent?.Cx ?? anchor?.Extent?.Cx ?? 0;
            long extentCy = inline?.Extent?.Cy ?? anchor?.Extent?.Cy ?? 0;

            var blip = graphic?.GraphicData?.Descendants<Dr.Blip>().FirstOrDefault();
            string? relId = blip?.Embed?.Value;
            if (string.IsNullOrEmpty(relId))
                return; // не растровая картинка (например, диаграмма/OLE) — пропускаем молча, это не потеря текста

            if (mainPart.GetPartById(relId!) is not ImagePart imagePart)
                return;

            byte[] data;
            using (var stream = imagePart.GetStream(FileMode.Open, FileAccess.Read))
            using (var mem = new MemoryStream())
            {
                stream.CopyTo(mem);
                data = mem.ToArray();
            }

            string ext = ContentTypeToExtension(imagePart.ContentType);
            if (ext.Length == 0)
            {
                warnings.Add("Изображение неподдерживаемого формата (векторное или неизвестное) пропущено.");
                return;
            }

            string fileName = $"img_{Guid.NewGuid():N}{ext}";
            extractedImages[fileName] = data;

            double widthPt = extentCx > 0 ? extentCx / EmuPerPoint : 100;
            double heightPt = extentCy > 0 ? extentCy / EmuPerPoint : 100;

            var image = new ImageBlock
            {
                ImageFileName = fileName,
                WidthPt = widthPt,
                HeightPt = heightPt,
                WrapMode = WrapMode.Inline
            };

            if (isFloating)
                warnings.Add("Обтекание текстом у плавающих картинок не переносится — картинка вставлена как обычная (в тексте).");

            section.InlineObjects.Add(image);
            chunk.Runs.Add(new RunModel
            {
                Text = RunModel.ObjectPlaceholder.ToString(),
                Properties = runProps,
                InlineImageId = image.Id
            });
        }

        private static string ContentTypeToExtension(string contentType) => contentType switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/bmp" => ".bmp",
            "image/tiff" => ".tiff",
            "image/x-icon" => ".ico",
            "image/webp" => ".webp",
            _ => string.Empty
        };

        // ── Таблицы ─────────────────────────────────────────────────────────

        private TableBlock? ImportTable(
            W.Table table,
            SectionModel section,
            DocxFormatResolver resolver,
            DocxNumberingMap numbering,
            Dictionary<int, Guid> listIdMap,
            MainDocumentPart mainPart,
            Dictionary<string, byte[]> extractedImages,
            List<string> warnings,
            int depth)
        {
            if (depth > 0)
            {
                warnings.Add("Вложенные таблицы не поддерживаются и были пропущены.");
                return null;
            }

            var grid = table.GetFirstChild<W.TableGrid>();
            var columnWidthsTwips = grid?.Elements<W.GridColumn>()
                .Select(c => ParseLong(c.Width?.Value) ?? 0L)
                .ToList() ?? new List<long>();

            var rows = table.Elements<W.TableRow>().ToList();
            int columnCount = columnWidthsTwips.Count > 0
                ? columnWidthsTwips.Count
                : rows.SelectMany(r => r.Elements<W.TableCell>())
                    .Sum(c => c.TableCellProperties?.GridSpan?.Val?.Value ?? 1);
            if (columnCount <= 0) columnCount = 1;

            var block = new TableBlock
            {
                RowCount = rows.Count,
                ColumnCount = columnCount
            };

            long totalWidthTwips = columnWidthsTwips.Sum();
            if (totalWidthTwips > 0)
            {
                foreach (var w in columnWidthsTwips)
                {
                    block.Columns.Add(new TableColumnDefinition
                    {
                        WidthType = TableColumnWidthType.Percent,
                        WidthValue = Math.Round(w * 100.0 / totalWidthTwips, 2)
                    });
                }
            }
            else
            {
                for (int i = 0; i < columnCount; i++)
                    block.Columns.Add(new TableColumnDefinition { WidthType = TableColumnWidthType.Auto });
            }

            var tblBorders = table.GetFirstChild<W.TableProperties>()?.GetFirstChild<W.TableBorders>();

            // vMerge отслеживается по столбцам: для каждого столбца храним последнюю
            // "главную" ячейку вертикального объединения (или null, если объединения нет).
            var openVMerge = new TableCell?[columnCount];

            for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                var cells = rows[rowIndex].Elements<W.TableCell>().ToList();
                int col = 0;

                foreach (var wCell in cells)
                {
                    var cellProps = wCell.TableCellProperties;
                    int gridSpan = cellProps?.GridSpan?.Val?.Value ?? 1;
                    if (gridSpan < 1) gridSpan = 1;

                    var vMerge = cellProps?.VerticalMerge;
                    bool isMergeContinuation = vMerge is not null &&
                        (vMerge.Val is null || vMerge.Val.Value == W.MergedCellValues.Continue);

                    if (isMergeContinuation && col < columnCount && openVMerge[col] is { } masterCell)
                    {
                        masterCell.RowSpan++;
                        col += gridSpan;
                        continue;
                    }

                    var paragraphs = new List<ParagraphBlock>();
                    foreach (var p in wCell.Elements<W.Paragraph>())
                    {
                        var effPara = resolver.ResolveEffectiveParagraph(p);
                        string? styleName = resolver.MapStyleName(p, effPara);
                        var para = new ParagraphBlock { Properties = effPara.ToParagraphProperties(styleName) };
                        para.ListProperties = numbering.Resolve(p, listIdMap);

                        var chunk = new TextChunk();
                        para.Chunks.Clear();
                        para.Chunks.Add(chunk);

                        // Картинки внутри ячейки регистрируются в SectionModel.InlineObjects
                        // документа целиком (как и обычные инлайн-картинки в тексте) — ячейка
                        // хранит только ссылку на них через Guid рана, поэтому сюда передаётся
                        // секция документа, а не что-то специфичное для таблицы.
                        foreach (var child in p.ChildElements)
                        {
                            if (child is W.ParagraphProperties) continue;
                            AppendRunOrDrawing(child, chunk, section, resolver, effPara,
                                mainPart, extractedImages, warnings);
                        }

                        if (chunk.Runs.Count == 0)
                            chunk.Runs.Add(new RunModel { Text = string.Empty, Properties = effPara.ToRunProperties() });
                        chunk.InvalidateLength();
                        paragraphs.Add(para);
                    }
                    if (paragraphs.Count == 0) paragraphs.Add(new ParagraphBlock());

                    var newCell = new TableCell
                    {
                        Row = rowIndex,
                        Column = col,
                        RowSpan = 1,
                        ColSpan = gridSpan,
                        Paragraphs = paragraphs,
                        Borders = ResolveCellBorders(cellProps?.TableCellBorders, tblBorders),
                        BackgroundColor = NormalizeShadingColor(cellProps?.Shading)
                    };

                    // Значения перечислений OOXML в SDK — структуры, а не enum:
                    // в шаблонах switch они непригодны, сравниваем оператором равенства.
                    var vAlign = cellProps?.TableCellVerticalAlignment?.Val?.Value;
                    if (vAlign == W.TableVerticalAlignmentValues.Center)
                        newCell.VerticalAlignment = Models.Document.VerticalAlignment.Middle;
                    else if (vAlign == W.TableVerticalAlignmentValues.Bottom)
                        newCell.VerticalAlignment = Models.Document.VerticalAlignment.Bottom;
                    else
                        newCell.VerticalAlignment = Models.Document.VerticalAlignment.Top;

                    block.Cells.Add(newCell);

                    if (vMerge is not null && (vMerge.Val is null || vMerge.Val.Value == W.MergedCellValues.Restart))
                    {
                        if (col < columnCount) openVMerge[col] = newCell;
                    }
                    else if (col < columnCount)
                    {
                        openVMerge[col] = null;
                    }

                    col += gridSpan;
                }
            }

            return block;
        }

        private static CellBorders ResolveCellBorders(W.TableCellBorders? cellBorders, W.TableBorders? tableBorders)
        {
            var result = new CellBorders();
            result.Top = ResolveBorderStyle(cellBorders?.TopBorder ?? tableBorders?.TopBorder, out var topColor, out var topThickness);
            result.Bottom = ResolveBorderStyle(cellBorders?.BottomBorder ?? tableBorders?.BottomBorder, out var bottomColor, out var bottomThickness);
            result.Left = ResolveBorderStyle(cellBorders?.LeftBorder ?? tableBorders?.LeftBorder, out var leftColor, out var leftThickness);
            result.Right = ResolveBorderStyle(cellBorders?.RightBorder ?? tableBorders?.RightBorder, out var rightColor, out var rightThickness);
            result.Color = topColor ?? bottomColor ?? leftColor ?? rightColor;
            double thickness = new[] { topThickness, bottomThickness, leftThickness, rightThickness }
                .Where(v => v > 0).DefaultIfEmpty(0.5).Average();
            result.ThicknessPt = thickness;
            return result;
        }

        private static BorderStyle ResolveBorderStyle(W.BorderType? border, out string? color, out double thicknessPt)
        {
            color = null;
            thicknessPt = 0.5;
            if (border is null || border.Val is null) return BorderStyle.Single;

            color = NormalizeHexColor(border.Color?.Value);
            if (border.Size is not null)
                thicknessPt = border.Size.Value / EighthsPerPoint;

            var borderVal = border.Val.Value;

            if (borderVal == W.BorderValues.Nil || borderVal == W.BorderValues.None)
                return BorderStyle.None;

            if (borderVal == W.BorderValues.Double)
                return BorderStyle.Double;

            if (borderVal == W.BorderValues.Dashed || borderVal == W.BorderValues.DashDotStroked)
                return BorderStyle.Dashed;

            if (borderVal == W.BorderValues.Dotted)
                return BorderStyle.Dotted;

            if (borderVal == W.BorderValues.Thick
                || borderVal == W.BorderValues.ThickThinSmallGap
                || borderVal == W.BorderValues.ThinThickSmallGap)
                return BorderStyle.Thick;

            return BorderStyle.Single;
        }

        private static string? NormalizeShadingColor(W.Shading? shading)
        {
            if (shading?.Fill is null) return null;
            return NormalizeHexColor(shading.Fill.Value);
        }

        // ── Параметры страницы (последний раздел) ──────────────────────────

        /// <summary>
        /// Лист и поля, которые Word подставляет документу без описания раздела.
        /// Файл, сохранённый без w:sectPr, ничего о странице не говорит, и текстовый
        /// редактор обязан взять те же величины, иначе текст ляжет в зону другой
        /// высоты и разбивка на страницы разойдётся с исходником: лишние пять
        /// миллиметров сверху и снизу — это минус строка на каждом листе.
        /// </summary>
        private static void ApplyWordDefaultPageSettings(DocumentModel doc)
        {
            doc.PageSettings.ApplyPaperSize(Core.Models.Print.PaperSize.A4);
            doc.PageSettings.Orientation = Core.Models.Print.PageOrientation.Portrait;

            doc.PageSettings.MarginTopMm = 20;
            doc.PageSettings.MarginBottomMm = 20;
            doc.PageSettings.MarginLeftMm = 30;
            doc.PageSettings.MarginRightMm = 15;
            doc.PageSettings.MarginGutterMm = 0;

            doc.PageSettings.HeaderDistanceMm = 12.5;
            doc.PageSettings.FooterDistanceMm = 12.5;
        }

        private void ApplyFinalSectionPageSettings(
            W.Body body, DocumentModel doc, DocxFormatResolver resolver, List<string> warnings)
        {
            // Умолчания ставятся до разбора раздела, а не вместо него: всё, что файл
            // о странице говорит, ниже их перекрывает. Раздел может описывать лист,
            // но молчать о полях (или наоборот) — тогда недосказанное остаётся
            // вордовским, а не остаётся от прежнего документа во вкладке.
            ApplyWordDefaultPageSettings(doc);

            var sectPr = body.GetFirstChild<W.SectionProperties>();
            if (sectPr is null) return;

            var pageSize = sectPr.GetFirstChild<W.PageSize>();
            if (pageSize is not null)
            {
                double widthMm = (pageSize.Width?.Value ?? 11906) / TwipsPerMm;
                double heightMm = (pageSize.Height?.Value ?? 16838) / TwipsPerMm;
                bool landscape = pageSize.Orient?.Value == W.PageOrientationValues.Landscape;

                doc.PageSettings.PaperSize = Core.Models.Print.PaperSize.Custom;
                doc.PageSettings.WidthMm = Math.Round(landscape ? heightMm : widthMm, 1);
                doc.PageSettings.HeightMm = Math.Round(landscape ? widthMm : heightMm, 1);
                doc.PageSettings.Orientation = landscape
                    ? Core.Models.Print.PageOrientation.Landscape
                    : Core.Models.Print.PageOrientation.Portrait;
            }

            var margin = sectPr.GetFirstChild<W.PageMargin>();
            if (margin is not null)
            {
                if (margin.Top is not null) doc.PageSettings.MarginTopMm = Math.Round(margin.Top.Value / TwipsPerMm, 1);
                if (margin.Bottom is not null) doc.PageSettings.MarginBottomMm = Math.Round(margin.Bottom.Value / TwipsPerMm, 1);
                if (margin.Left is not null) doc.PageSettings.MarginLeftMm = Math.Round(margin.Left.Value / TwipsPerMm, 1);
                if (margin.Right is not null) doc.PageSettings.MarginRightMm = Math.Round(margin.Right.Value / TwipsPerMm, 1);
                if (margin.Gutter is not null) doc.PageSettings.MarginGutterMm = Math.Round(margin.Gutter.Value / TwipsPerMm, 1);
                if (margin.Header is not null) doc.PageSettings.HeaderDistanceMm = Math.Round(margin.Header.Value / TwipsPerMm, 1);
                if (margin.Footer is not null) doc.PageSettings.FooterDistanceMm = Math.Round(margin.Footer.Value / TwipsPerMm, 1);
            }

            var cols = sectPr.GetFirstChild<W.Columns>();
            int? colCount = cols?.ColumnCount?.Value;
            if (colCount is int cc && cc > 1)
            {
                doc.ColumnSettings.ColumnCount = cc;

                // Значение читается через InnerText: атрибут w:space в схеме — мера в twips,
                // и разные версии SDK типизируют его по-разному. Строковое представление
                // одинаково доступно у любого из вариантов.
                string? spaceRaw = cols?.Space?.InnerText;
                if (!string.IsNullOrEmpty(spaceRaw) &&
                    double.TryParse(spaceRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var spaceTwips))
                    doc.ColumnSettings.GapMm = Math.Round(spaceTwips / TwipsPerMm, 1);
            }

            if (sectPr.Elements<W.HeaderReference>().Any() || sectPr.Elements<W.FooterReference>().Any())
                warnings.Add("Колонтитулы (верхний/нижний) не поддерживаются и не были импортированы.");
        }

        private static long? ParseLong(string? s) =>
            long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

        internal static string? NormalizeHexColor(string? val)
        {
            if (string.IsNullOrWhiteSpace(val)) return null;
            if (string.Equals(val, "auto", StringComparison.OrdinalIgnoreCase)) return null;
            val = val.TrimStart('#');
            if (val.Length == 6 && Regex.IsMatch(val, "^[0-9A-Fa-f]{6}$"))
                return "#" + val.ToUpperInvariant();
            return null;
        }

        // ── Импорт plain text (.txt) — без изменений ────────────────────────

        /// <summary>
        /// Импортирует plain text (.txt) файл.
        /// Каждая строка становится отдельным параграфом.
        /// Форматирование не применяется — используется стиль Normal.
        /// </summary>
        public async Task<ImportResult> ImportFromTxtAsync(string filePath)
        {
            try
            {
                string[] lines = await System.IO.File.ReadAllLinesAsync(filePath);
                var doc = DocumentModel.CreateNew(System.IO.Path.GetFileNameWithoutExtension(filePath));
                var section = doc.Sections[0];
                section.Blocks.Clear();

                foreach (string line in lines)
                {
                    var para = new ParagraphBlock();
                    para.Properties.StyleName = "Normal";

                    var run = new Models.Inline.RunModel { Text = line };
                    para.Chunks[0].Runs.Add(run);
                    para.Chunks[0].InvalidateLength();

                    section.Blocks.Add(para);
                }

                // Минимум один параграф если файл пустой.
                if (section.Blocks.Count == 0)
                    section.Blocks.Add(new ParagraphBlock());

                return ImportResult.Ok(doc, new[] { "Plain text imported without formatting." });
            }
            catch (Exception ex)
            {
                return ImportResult.Fail(ex.Message);
            }
        }
    }

    // ── Разрешение эффективного форматирования Word (стили + прямое) ───────

    /// <summary>
    /// Накопленное символьное форматирование одного уровня каскада (docDefaults→
    /// стиль абзаца→символьный стиль→прямое форматирование рана). На каждом уровне
    /// заданные поля перекрывают предыдущие, незаданные (null) — наследуются.
    /// Читает форматирование из любого контейнера дочерних элементов OOXML
    /// (rPr рана, rPr стиля, rPr по умолчанию) через <see cref="OpenXmlCompositeElement.GetFirstChild{T}"/> —
    /// это разные типы в SDK, но с одинаковым по смыслу набором дочерних элементов.
    /// </summary>
    internal sealed class RunFormat
    {
        private const double HalfPointsPerPoint = 2.0;

        public string? FontFamily;
        public double? FontSizePt;
        public bool? Bold;
        public bool? Italic;
        public bool? Underline;
        public bool? Strike;
        public bool? Superscript;
        public bool? Subscript;
        public bool? AllCaps;
        public bool? SmallCaps;
        public string? TextColor;
        public string? HighlightColor;
        public string? Language;

        public void MergeFrom(
            OpenXmlCompositeElement? container,
            string? themeMajorFont = null,
            string? themeMinorFont = null)
        {
            if (container is null) return;

            var runFonts = container.GetFirstChild<W.RunFonts>();
            if (runFonts is not null)
            {
                string? font = runFonts.Ascii?.Value
                    ?? runFonts.HighAnsi?.Value
                    ?? runFonts.ComplexScript?.Value;

                // Современные документы Word ссылаются не на имя шрифта, а на шрифт
                // темы (w:asciiTheme="minorHAnsi"). Без разбора этой ссылки шрифт
                // документа терялся целиком и текст рисовался шрифтом по умолчанию.
                if (string.IsNullOrEmpty(font))
                {
                    string? themeRef = runFonts.AsciiTheme?.InnerText
                        ?? runFonts.HighAnsiTheme?.InnerText;

                    if (!string.IsNullOrEmpty(themeRef))
                        font = themeRef.StartsWith("major", StringComparison.OrdinalIgnoreCase)
                            ? themeMajorFont
                            : themeMinorFont;
                }

                if (!string.IsNullOrEmpty(font)) FontFamily = font;
            }

            var fontSize = container.GetFirstChild<W.FontSize>();
            if (fontSize?.Val?.Value is string szStr &&
                double.TryParse(szStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var szVal))
                FontSizePt = szVal / HalfPointsPerPoint;

            if (container.GetFirstChild<W.Bold>() is { } b) Bold = b.Val is null || b.Val.Value;
            if (container.GetFirstChild<W.Italic>() is { } it) Italic = it.Val is null || it.Val.Value;

            if (container.GetFirstChild<W.Underline>() is { } u)
                Underline = u.Val is not null && u.Val.Value != W.UnderlineValues.None;

            if (container.GetFirstChild<W.Strike>() is { } s) Strike = s.Val is null || s.Val.Value;

            if (container.GetFirstChild<W.VerticalTextAlignment>() is { } va)
            {
                if (va.Val?.Value == W.VerticalPositionValues.Superscript) { Superscript = true; Subscript = false; }
                else if (va.Val?.Value == W.VerticalPositionValues.Subscript) { Subscript = true; Superscript = false; }
                else { Superscript = false; Subscript = false; }
            }

            if (container.GetFirstChild<W.Caps>() is { } c) AllCaps = c.Val is null || c.Val.Value;
            if (container.GetFirstChild<W.SmallCaps>() is { } sc) SmallCaps = sc.Val is null || sc.Val.Value;

            if (container.GetFirstChild<W.Color>()?.Val?.Value is string colorVal)
                TextColor = ImportService.NormalizeHexColor(colorVal);

            var highlight = container.GetFirstChild<W.Highlight>();
            if (highlight?.Val?.Value is { } hv)
                HighlightColor = HighlightToHex(hv);
            else if (container.GetFirstChild<W.Shading>()?.Fill?.Value is string shadeVal)
                HighlightColor = ImportService.NormalizeHexColor(shadeVal);

            if (container.GetFirstChild<W.Languages>()?.Val?.Value is string lang)
                Language = lang;
        }

        private static string? HighlightToHex(W.HighlightColorValues v)
        {
            if (v == W.HighlightColorValues.Yellow) return "#FFFF00";
            if (v == W.HighlightColorValues.Green) return "#00FF00";
            if (v == W.HighlightColorValues.Cyan) return "#00FFFF";
            if (v == W.HighlightColorValues.Magenta) return "#FF00FF";
            if (v == W.HighlightColorValues.Blue) return "#0000FF";
            if (v == W.HighlightColorValues.Red) return "#FF0000";
            if (v == W.HighlightColorValues.DarkBlue) return "#00008B";
            if (v == W.HighlightColorValues.DarkCyan) return "#008B8B";
            if (v == W.HighlightColorValues.DarkGreen) return "#006400";
            if (v == W.HighlightColorValues.DarkMagenta) return "#8B008B";
            if (v == W.HighlightColorValues.DarkRed) return "#8B0000";
            if (v == W.HighlightColorValues.DarkYellow) return "#808000";
            if (v == W.HighlightColorValues.DarkGray) return "#A9A9A9";
            if (v == W.HighlightColorValues.LightGray) return "#D3D3D3";
            if (v == W.HighlightColorValues.Black) return "#000000";
            return null;
        }

        public RunFormat Clone() => (RunFormat)MemberwiseClone();

        public RunProperties ToRunProperties() => new()
        {
            FontFamily = FontFamily,
            FontSize = FontSizePt,
            IsBold = Bold == true,
            IsItalic = Italic == true,
            IsUnderline = Underline == true,
            IsStrikethrough = Strike == true,
            IsSuperscript = Superscript == true,
            IsSubscript = Subscript == true,
            IsAllCaps = AllCaps == true,
            IsSmallCaps = SmallCaps == true,
            TextColor = TextColor,
            HighlightColor = HighlightColor,
            Language = Language
        };
    }

    /// <summary>
    /// Накопленное форматирование абзаца одного уровня каскада, вместе с базовым
    /// символьным форматированием абзаца (<see cref="BaseRun"/>) — им пользуются
    /// раны, у которых нет собственного прямого форматирования и символьного стиля.
    /// </summary>
    internal sealed class ParaFormat
    {
        private const double TwipsPerPoint = 20.0;

        public W.JustificationValues? Justification;
        public double? LeftIndentPt, RightIndentPt, FirstLineIndentPt;
        public double? SpaceBeforePt, SpaceAfterPt;
        public LineSpacingRule? LineRule;
        public double? LineValue;
        public bool? KeepTogether, KeepWithNext, PageBreakBefore;
        public int? OutlineLevel;
        public RunFormat BaseRun = new();

        public void MergeFrom(OpenXmlCompositeElement? container)
        {
            if (container is null) return;

            if (container.GetFirstChild<W.Justification>()?.Val?.Value is { } j) Justification = j;

            if (container.GetFirstChild<W.Indentation>() is { } ind)
            {
                if (ind.Left?.Value is string l && TryTwips(l, out var lv)) LeftIndentPt = lv;
                else if (ind.Start?.Value is string ls && TryTwips(ls, out var lsv)) LeftIndentPt = lsv;

                if (ind.Right?.Value is string r && TryTwips(r, out var rv)) RightIndentPt = rv;
                else if (ind.End?.Value is string re && TryTwips(re, out var rev)) RightIndentPt = rev;

                if (ind.Hanging?.Value is string hg && TryTwips(hg, out var hgv)) FirstLineIndentPt = -hgv;
                else if (ind.FirstLine?.Value is string fl && TryTwips(fl, out var flv)) FirstLineIndentPt = flv;
            }

            if (container.GetFirstChild<W.SpacingBetweenLines>() is { } sp)
            {
                if (sp.Before?.Value is string b && TryTwips(b, out var bv)) SpaceBeforePt = bv;
                if (sp.After?.Value is string a && TryTwips(a, out var av)) SpaceAfterPt = av;

                if (sp.Line?.Value is string ln &&
                    double.TryParse(ln, NumberStyles.Float, CultureInfo.InvariantCulture, out var lnv))
                {
                    var rule = sp.LineRule?.Value;
                    if (rule is null || rule == W.LineSpacingRuleValues.Auto)
                    {
                        // Значение в 240-х долях строки: 240 = одинарный, 360 = полуторный.
                        LineRule = Models.Styles.LineSpacingRule.Auto;
                        LineValue = lnv / 240.0;
                    }
                    else
                    {
                        LineRule = rule == W.LineSpacingRuleValues.Exact
                            ? Models.Styles.LineSpacingRule.Exact
                            : Models.Styles.LineSpacingRule.AtLeast;
                        LineValue = lnv / TwipsPerPoint;
                    }
                }
            }

            if (container.GetFirstChild<W.KeepNext>() is not null) KeepWithNext = true;
            if (container.GetFirstChild<W.KeepLines>() is not null) KeepTogether = true;
            if (container.GetFirstChild<W.PageBreakBefore>() is not null) PageBreakBefore = true;

            if (container.GetFirstChild<W.OutlineLevel>()?.Val?.Value is int ol) OutlineLevel = ol;
        }

        private static bool TryTwips(string s, out double pt)
        {
            pt = 0;
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var twips)) return false;
            pt = twips / TwipsPerPoint;
            return true;
        }

        /// <summary>Глубокая копия: базовое символьное форматирование копируется тоже.</summary>
        public ParaFormat Clone()
        {
            var copy = (ParaFormat)MemberwiseClone();
            copy.BaseRun = BaseRun.Clone();
            return copy;
        }

        public Models.Styles.ParagraphProperties ToParagraphProperties(string? styleName)
        {
            Models.Styles.TextAlignment? alignment = null;

            if (Justification == W.JustificationValues.Center) alignment = Models.Styles.TextAlignment.Center;
            else if (Justification == W.JustificationValues.Right) alignment = Models.Styles.TextAlignment.Right;
            else if (Justification == W.JustificationValues.Both) alignment = Models.Styles.TextAlignment.Justify;
            else if (Justification == W.JustificationValues.Left) alignment = Models.Styles.TextAlignment.Left;

            // Интервалы записываются явными числами, даже когда файл о них молчит.
            // Незаполненное свойство означало бы «взять из стиля Writersword», а его
            // «Обычный» добавляет 8 пунктов после абзаца — Word в таком документе не
            // добавляет ничего, и на листе терялась целая строка. По стандарту
            // отсутствие w:spacing — это нулевые интервалы и одинарная строка.
            return new Models.Styles.ParagraphProperties
            {
                StyleName = styleName,
                Alignment = alignment,
                LeftIndent = LeftIndentPt ?? 0,
                RightIndent = RightIndentPt ?? 0,
                FirstLineIndent = FirstLineIndentPt ?? 0,
                SpaceBefore = SpaceBeforePt ?? 0,
                SpaceAfter = SpaceAfterPt ?? 0,
                LineSpacingRule = LineRule ?? Models.Styles.LineSpacingRule.Auto,
                LineSpacingValue = LineValue ?? 1.0,
                KeepTogether = KeepTogether ?? false,
                KeepWithNext = KeepWithNext ?? false,
                PageBreakBefore = PageBreakBefore ?? false,
                OutlineLevel = OutlineLevel ?? 0
            };
        }

        public RunProperties ToRunProperties() => BaseRun.ToRunProperties();
    }

    /// <summary>Результат разрешения каскада форматирования для одного параграфа Word.</summary>
    internal readonly struct EffectiveParagraph
    {
        public EffectiveParagraph(ParaFormat format) => Format = format;
        public ParaFormat Format { get; }
        public int? OutlineLevel => Format.OutlineLevel;
        public RunFormat BaseRun => Format.BaseRun;
        public Models.Styles.ParagraphProperties ToParagraphProperties(string? styleName) => Format.ToParagraphProperties(styleName);
        public RunProperties ToRunProperties() => Format.ToRunProperties();
    }

    /// <summary>
    /// Разрешает эффективное форматирование параграфов и ранов Word по цепочке
    /// стилей (w:basedOn) и прямому форматированию — без учёта DocDefaults styles.xml
    /// (в подавляющем большинстве документов стиль "Normal" полностью задаёт базовое
    /// форматирование сам по себе, поэтому этим можно осознанно пренебречь).
    /// Также определяет соответствие стиля абзаца именованным стилям Writersword
    /// (Heading1–6, Quote, Code, Normal) — в первую очередь по w:outlineLvl,
    /// как наиболее надёжному признаку заголовка независимо от локализации Word.
    /// </summary>
    internal sealed class DocxFormatResolver
    {
        private readonly Dictionary<string, W.Style> _stylesById = new(StringComparer.OrdinalIgnoreCase);
        private readonly string? _defaultParagraphStyleId;
        private readonly ParaFormat _documentDefaults = new();
        private readonly string? _themeMajorFont;
        private readonly string? _themeMinorFont;

        public DocxFormatResolver(DocumentFormat.OpenXml.Packaging.MainDocumentPart mainPart)
        {
            // Шрифты темы: на них ссылается w:rFonts у большинства документов Word.
            var fontScheme = mainPart.ThemePart?.Theme?.ThemeElements?.FontScheme;
            _themeMajorFont = fontScheme?.MajorFont?.GetFirstChild<Dr.LatinFont>()?.Typeface?.Value;
            _themeMinorFont = fontScheme?.MinorFont?.GetFirstChild<Dr.LatinFont>()?.Typeface?.Value;

            var styles = mainPart.StyleDefinitionsPart?.Styles;
            if (styles is null) return;

            // docDefaults — основание всего каскада: шрифт и кегль документа обычно
            // заданы именно там, а стиль "Normal" их только дополняет.
            var docDefaults = styles.DocDefaults;
            _documentDefaults.MergeFrom(docDefaults?.ParagraphPropertiesDefault?.ParagraphPropertiesBaseStyle);
            _documentDefaults.BaseRun.MergeFrom(
                docDefaults?.RunPropertiesDefault?.RunPropertiesBaseStyle,
                _themeMajorFont, _themeMinorFont);

            foreach (var style in styles.Elements<W.Style>())
            {
                if (style.StyleId?.Value is string id)
                    _stylesById[id] = style;
                if (style.Type?.Value == W.StyleValues.Paragraph && style.Default?.Value == true)
                    _defaultParagraphStyleId = style.StyleId?.Value;
            }
        }

        public EffectiveParagraph ResolveEffectiveParagraph(W.Paragraph p)
        {
            string styleId = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value
                ?? _defaultParagraphStyleId
                ?? "Normal";

            var format = _documentDefaults.Clone();

            // "Normal" — фактическая база документа даже если явно не входит в цепочку BasedOn.
            if (!string.Equals(styleId, "Normal", StringComparison.OrdinalIgnoreCase))
                MergeParagraphStyle(format, "Normal");

            foreach (var id in BuildStyleChain(styleId))
                MergeParagraphStyle(format, id);

            format.MergeFrom(p.ParagraphProperties);
            return new EffectiveParagraph(format);
        }

        public RunFormat ResolveEffectiveRun(W.Run run, EffectiveParagraph effPara)
        {
            var format = effPara.BaseRun.Clone();

            string? rStyleId = run.RunProperties?.GetFirstChild<W.RunStyle>()?.Val?.Value;
            if (rStyleId is not null)
            {
                foreach (var id in BuildStyleChain(rStyleId))
                    if (_stylesById.TryGetValue(id, out var style))
                        format.MergeFrom(
                            style.GetFirstChild<W.StyleRunProperties>(), _themeMajorFont, _themeMinorFont);
            }

            format.MergeFrom(run.RunProperties, _themeMajorFont, _themeMinorFont);
            return format;
        }

        /// <summary>
        /// Определяет соответствующий встроенный стиль Writersword. w:outlineLvl
        /// (0-based, 0…5 → Heading1…Heading6) — основной признак: Word проставляет
        /// его на стиль заголовка независимо от локализации интерфейса.
        /// </summary>
        public string? MapStyleName(W.Paragraph p, EffectiveParagraph eff)
        {
            if (eff.OutlineLevel is int ol && ol is >= 0 and <= 5)
                return $"Heading{ol + 1}";

            string? id = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
            string? name = id is not null && _stylesById.TryGetValue(id, out var st)
                ? st.StyleName?.Val?.Value
                : null;

            string probe = name ?? id ?? string.Empty;

            var headingMatch = Regex.Match(probe, @"(?:heading|заголовок)\s*([1-9])", RegexOptions.IgnoreCase);
            if (headingMatch.Success
                && int.TryParse(headingMatch.Groups[1].Value, out int lvl)
                && lvl is >= 1 and <= 6)
                return $"Heading{lvl}";

            if (Regex.IsMatch(probe, "quote|цитата", RegexOptions.IgnoreCase)) return "Quote";
            if (Regex.IsMatch(probe, @"^(code|source ?code|код)$", RegexOptions.IgnoreCase)) return "Code";

            // Моноширинный шрифт на весь абзац без явного стиля кода — тоже похоже на код.
            if (eff.BaseRun.FontFamily is { } font &&
                (font.Contains("Consolas", StringComparison.OrdinalIgnoreCase) ||
                 font.Contains("Courier", StringComparison.OrdinalIgnoreCase)))
                return "Code";

            return "Normal";
        }

        private void MergeParagraphStyle(ParaFormat format, string styleId)
        {
            if (!_stylesById.TryGetValue(styleId, out var style)) return;
            format.MergeFrom(style.GetFirstChild<W.StyleParagraphProperties>());
            format.BaseRun.MergeFrom(
                style.GetFirstChild<W.StyleRunProperties>(), _themeMajorFont, _themeMinorFont);
        }

        /// <summary>Цепочка стилей от корня (без BasedOn) к запрошенному, по ссылкам w:basedOn.</summary>
        private List<string> BuildStyleChain(string styleId)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var chain = new List<string>();
            string? current = styleId;

            while (current is not null && seen.Add(current) && _stylesById.TryGetValue(current, out var style))
            {
                chain.Add(current);
                current = style.BasedOn?.Val?.Value;
            }

            chain.Reverse();
            return chain;
        }
    }

    /// <summary>
    /// Разрешает нумерацию Word (numbering.xml: abstractNum + num) в
    /// <see cref="ListProperties"/> Writersword. Переопределения на уровне
    /// конкретного w:num (w:lvlOverride) не поддерживаются — редкий случай,
    /// достаточно базового сопоставления abstractNum → уровни.
    /// </summary>
    internal sealed class DocxNumberingMap
    {
        private sealed class LevelDef
        {
            public ListMarkerType MarkerType;
            public string? CustomMarker;
            public string? NumberPrefix;
            public string? NumberSuffix;
            public int StartAt = 1;
        }

        private readonly Dictionary<int, Dictionary<int, LevelDef>> _byNumId = new();

        public DocxNumberingMap(DocumentFormat.OpenXml.Packaging.MainDocumentPart mainPart)
        {
            var numbering = mainPart.NumberingDefinitionsPart?.Numbering;
            if (numbering is null) return;

            var abstractById = new Dictionary<int, W.AbstractNum>();
            foreach (var a in numbering.Elements<W.AbstractNum>())
                if (a.AbstractNumberId?.Value is int aid)
                    abstractById[aid] = a;

            foreach (var inst in numbering.Elements<W.NumberingInstance>())
            {
                int? numId = inst.NumberID?.Value;
                int? abstractId = inst.AbstractNumId?.Val?.Value;
                if (numId is not int nid || abstractId is not int aid || !abstractById.TryGetValue(aid, out var abs))
                    continue;

                var levels = new Dictionary<int, LevelDef>();
                foreach (var level in abs.Elements<W.Level>())
                {
                    int ilvl = level.LevelIndex?.Value ?? 0;
                    var fmt = level.NumberingFormat?.Val?.Value ?? W.NumberFormatValues.Bullet;
                    string lvlText = level.LevelText?.Val?.Value ?? string.Empty;

                    var def = new LevelDef { StartAt = level.StartNumberingValue?.Val?.Value ?? 1 };
                    MapFormat(fmt, lvlText, def);
                    levels[ilvl] = def;
                }
                _byNumId[nid] = levels;
            }
        }

        private static void MapFormat(W.NumberFormatValues fmt, string lvlText, LevelDef def)
        {
            if (fmt == W.NumberFormatValues.Decimal) def.MarkerType = ListMarkerType.Decimal;
            else if (fmt == W.NumberFormatValues.DecimalZero) def.MarkerType = ListMarkerType.DecimalLeadingZero;
            else if (fmt == W.NumberFormatValues.LowerLetter) def.MarkerType = ListMarkerType.LowerAlpha;
            else if (fmt == W.NumberFormatValues.UpperLetter) def.MarkerType = ListMarkerType.UpperAlpha;
            else if (fmt == W.NumberFormatValues.LowerRoman) def.MarkerType = ListMarkerType.LowerRoman;
            else if (fmt == W.NumberFormatValues.UpperRoman) def.MarkerType = ListMarkerType.UpperRoman;
            else if (fmt == W.NumberFormatValues.Bullet) def.MarkerType = MapBulletChar(lvlText, out def.CustomMarker);
            else def.MarkerType = ListMarkerType.Decimal;

            bool isCounted = def.MarkerType is ListMarkerType.Decimal or ListMarkerType.DecimalLeadingZero
                or ListMarkerType.LowerAlpha or ListMarkerType.UpperAlpha
                or ListMarkerType.LowerRoman or ListMarkerType.UpperRoman;
            if (!isCounted) return;

            var m = Regex.Match(lvlText, @"^(?<prefix>[^%]*)%\d+(?<suffix>.*)$");
            if (m.Success)
            {
                def.NumberPrefix = m.Groups["prefix"].Value.Length > 0 ? m.Groups["prefix"].Value : null;
                def.NumberSuffix = m.Groups["suffix"].Value.Length > 0 ? m.Groups["suffix"].Value : ".";
            }
            else
            {
                def.NumberSuffix = ".";
            }
        }

        private static ListMarkerType MapBulletChar(string ch, out string? custom)
        {
            custom = null;
            if (ch.Length == 0) return ListMarkerType.Bullet;

            char c = ch[0];
            switch (c)
            {
                case '\u2022': case '\uF0B7': case '\u25CF': return ListMarkerType.Bullet;
                case '-': case '\u2013': case '\u2014': return ListMarkerType.Dash;
                case '\u25AA': case '\u25A0': return ListMarkerType.Square;
                case '\u25CB': case 'o': case 'O': return ListMarkerType.Circle;
                default:
                    custom = ch;
                    return ListMarkerType.Custom;
            }
        }

        /// <summary>
        /// Возвращает свойства списка для параграфа, если у него есть w:numPr, иначе null.
        /// listIdMap переиспользуется на весь импорт документа — параграфы с одинаковым
        /// numId получают один и тот же Guid ListId (единый список).
        /// </summary>
        public ListProperties? Resolve(W.Paragraph p, Dictionary<int, Guid> listIdMap)
        {
            var numPr = p.ParagraphProperties?.NumberingProperties;
            int? numId = numPr?.NumberingId?.Val?.Value;
            if (numId is not int nid) return null;

            int ilvl = numPr?.NumberingLevelReference?.Val?.Value ?? 0;
            if (!_byNumId.TryGetValue(nid, out var levels)) return null;

            if (!levels.TryGetValue(ilvl, out var def))
                def = levels.Values.FirstOrDefault() ?? new LevelDef();

            if (!listIdMap.TryGetValue(nid, out var listGuid))
            {
                listGuid = Guid.NewGuid();
                listIdMap[nid] = listGuid;
            }

            return new ListProperties
            {
                ListId = listGuid,
                Level = Math.Clamp(ilvl, 0, 8),
                MarkerType = def.MarkerType,
                CustomMarker = def.CustomMarker,
                NumberPrefix = def.NumberPrefix,
                NumberSuffix = def.NumberSuffix,
                StartAt = def.StartAt
            };
        }
    }
}
