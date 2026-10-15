using System;
using System.IO;
using SkiaSharp;
using Writersword.Core.Interfaces.Print;
using Writersword.Core.Models.Print;
using Writersword.Modules.TextEditor.Rendering;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Page;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Реализует IPrintableDocument для модуля TextEditor.
    /// Использует SKTextRenderer — тот же движок что и DocumentCanvas.
    /// Гарантирует точное совпадение переносов строк между редактором и PDF.
    /// Вся вёрстка выполняется один раз в конструкторе.
    /// </summary>
    public sealed class TextEditorPrintDocument : IPrintableDocument
    {
        private readonly DocumentModel _document;
        private readonly PrintPageSettings _pageSettings;
        private readonly SKTextRenderer _renderer;
        private readonly StyleResolver _styles;
        private Core.Models.Rendering.SKPageLayout _pageLayout;

        // ── IPrintableDocument ────────────────────────────────────────────

        public string Title => _document.Title;
        public int PageCount => _pageLayout.PageCount;
        public PrintPageSettings PageSettings => _pageSettings;

        public TextEditorPrintDocument(DocumentModel document)
        {
            ArgumentNullException.ThrowIfNull(document);

            // «Исходный документ» печатается с картинками и фигурами в прежнем виде:
            // смена вида снимается с копии документа, сам документ не трогается.
            _document = document.RevisionView == Models.Inline.RevisionView.Original
                ? ObjectAppearance.WithPreviousAppearance(document) ?? document
                : document;
            _pageSettings = ConvertPageSettings(document.PageSettings);
            _renderer = new SKTextRenderer();
            // Печать идёт в выбранном виде показа исправлений, как у Word.
            _styles = new StyleResolver(
                document.Styles,
                revisionView: document.RevisionView,
                revisionAuthors: RevisionService.Authors(document));
            ReserveHeaderFooterSpace();
            // Габарит картинки в строке: без него объект встал бы в строку нулевой ширины
            // и переносы строк в печати разошлись бы с редактором.
            _renderer.InlineImageSize = GetInlineImageSize;
            _renderer.InlineObjectDescent = GetInlineObjectDescent;

            // Сноски: знак — номером по порядку, текст — внизу своего листа, как в
            // редакторе. Номера считаются в том же виде исправлений, что и печать.
            _noteNumbering = NoteNumbering.Compute(document, document.RevisionView);
            _renderer.InlineNoteMark = _noteNumbering.MarkOf;

            // Живые поля: дата — на момент печати, номер страницы — по листу печати.
            foreach (var section in document.Sections)
                foreach (var block in section.InlineObjects)
                    if (block is FieldBlock field)
                        _liveFields[field.Id] = field;
            _renderer.InlineFieldText = PrintFieldText;

            // Склейка абзацев, чей знак абзаца спрятан видом исправлений, — как на экране.
            RevisionDisplay.PrepareJoins(_document, document.RevisionView);

            _pageLayout = _renderer.BuildPageLayout(_document, _pageSettings, _styles, _noteNumbering, _noteRules);
            _decorations = BuildDecorations();

            // Номер страницы в поле узнаётся только после раскладки, а другой номер
            // может сменить ширину строки и сдвинуть листы: раскладка собирается
            // заново, пока номера в полях не совпадут с листами, — как у редактора.
            for (int pass = 0; pass < PrintFieldRelayoutLimit && UpdatePrintPageValues(); pass++)
            {
                _noteRules.Clear();
                _pageLayout = _renderer.BuildPageLayout(_document, _pageSettings, _styles, _noteNumbering, _noteRules);
                _decorations = BuildDecorations();
            }
        }

        /// <summary>Сколько раз печать пересобирает раскладку ради номеров страниц в полях.</summary>
        private const int PrintFieldRelayoutLimit = 3;

        // Номера страниц и число листов для полей PAGE и NUMPAGES по раскладке печати.
        private readonly System.Collections.Generic.Dictionary<Guid, string> _printPageValues = new();

        // Оформление листов печати: номера и видимость колонтитулов. Считается по
        // раскладке печати, а не по полотну: печать может идти без открытого редактора.
        private PageDecoration[] _decorations;

        // Живые поля документа по Id и текст, которым каждое сверстано.
        private readonly System.Collections.Generic.Dictionary<Guid, FieldBlock> _liveFields = new();
        private readonly System.Collections.Generic.Dictionary<Guid, string> _fieldTexts = new();

        // Номера сносок документа и черты над сносками листов печати.
        private readonly NoteNumbering _noteNumbering = NoteNumbering.Empty;
        private readonly System.Collections.Generic.List<SKTextRenderer.PageNoteRule> _noteRules = new();

        /// <summary>
        /// Сведения о листах печати: где начинается каждый абзац и где главы. Индекс
        /// абзаца в раскладке печати — номер блока потока без разрывов страницы: так их
        /// считает SKTextRenderer.BuildPageLayout.
        /// </summary>
        private PageDecoration[] BuildDecorations()
        {
            var settings = _document.HeaderFooter;
            if (settings is null || settings.IsEmpty) return Array.Empty<PageDecoration>();

            var blocks = new System.Collections.Generic.List<BlockModel>();
            foreach (var section in _document.Sections)
                foreach (var block in section.Blocks)
                {
                    if (block is BreakBlock bb && bb.StartsNewPage) continue;
                    blocks.Add(block);
                }

            var starts = new System.Collections.Generic.Dictionary<Guid, int>();
            var chapters = new System.Collections.Generic.HashSet<int>();

            for (int pi = 0; pi < _pageLayout.Pages.Count; pi++)
            {
                foreach (var para in _pageLayout.Pages[pi].Paragraphs)
                {
                    if (para.LineFrom != 0) continue;
                    if (para.ParagraphIndex < 0 || para.ParagraphIndex >= blocks.Count) continue;
                    if (blocks[para.ParagraphIndex] is not ParagraphBlock paragraph) continue;

                    starts.TryAdd(paragraph.Id, pi);
                    if (HeadingCollapseService.LevelOf(paragraph, _document) == 1)
                        chapters.Add(pi);
                }
            }

            return PageNumbering.Compute(settings, _pageLayout.PageCount, starts, chapters);
        }

        /// <summary>
        /// Картинки, прочитанные для этой печати: и сами изображения документа, и
        /// заливки фигур. Кеш живёт столько же, сколько документ печати, — одна и
        /// та же картинка на десяти страницах читается из хранилища один раз.
        /// </summary>
        private readonly System.Collections.Generic.Dictionary<string, SKImage?> _printImages = new();

        /// <summary>
        /// Читает картинку из хранилища проекта. Экранный кеш канваса здесь не
        /// годится: печать может идти без открытого канваса, и грузить она обязана
        /// сама. Неудачное чтение запоминается как null — повторных попыток на
        /// каждой странице не будет.
        /// </summary>
        private SKImage? ResolvePrintImage(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return null;
            if (_printImages.TryGetValue(fileName, out var cached)) return cached;

            SKImage? image = null;
            try
            {
                var ctx = Writersword.Core.Services.CoreServices
                    .GetService<Writersword.Core.Interfaces.WorkFlows.ITabCollection>()?.ActiveTab?.Context;
                var bytes = ctx?.ReadFile($"TextEditor/Images/{fileName}");
                if (bytes is { Length: > 0 })
                {
                    // Растр декодируется сразу в пиксели: образ поверх освобождённого
                    // растра роняет процесс в нативном коде при первой же отрисовке.
                    using var bmp = SKBitmap.Decode(bytes);
                    if (bmp is not null) image = SKImage.FromBitmap(bmp);
                }
            }
            catch { image = null; }

            _printImages[fileName] = image;
            return image;
        }

        /// <inheritdoc/>
        public void RenderPage(int pageIndex, SKCanvas canvas, float pageWidthPt, float pageHeightPt)
        {
            if (pageIndex < 0 || pageIndex >= _pageLayout.Pages.Count) return;

            var page = _pageLayout.Pages[pageIndex];

            // Источник картинок ставится перед каждой страницей: рендер статический
            // и общий, а замыкание здесь — на хранилище именно этого документа.
            SKTextRenderer.PrintImageResolver = ResolvePrintImage;

            // Экранные подмены снимаются на время печати по той же причине, по
            // которой они ставятся: рендер один на все проходы. Вид рабочей области —
            // кремовая бумага, ночной лист, приглушённый свет — относится к глазам
            // перед экраном, а не к бумаге в лотке, и уходить в принтер вместе с
            // текстом ему нельзя. Возврат — в Dispose области, что бы ни случилось.
            using var neutral = NeutralRenderScope.Begin();

            // Сторона переплёта: на листе с чётным номером он справа, и содержимое
            // листа стоит левее на его ширину — как на полотне.
            float gutterShiftPt = GutterShiftPt(pageIndex);

            // Плавающие объекты от края листа сдвиг не проходят — рендер возвращает
            // их на место по этому значению.
            SKTextRenderer.PrintGutterShiftPt = gutterShiftPt;

            try
            {
                if (gutterShiftPt != 0f)
                {
                    canvas.Save();
                    canvas.Translate(gutterShiftPt, 0f);
                }

                // Знаки сносок рисует печать сама: обработчик объектов строки ставит
                // редактор, а печать может идти и без него. Прочие объекты строки
                // остаются прежнему обработчику.
                var previousInlineDrawer = SKTextRenderer.DrawInlineObject;
                if (_noteNumbering.HasNotes || _liveFields.Count > 0)
                {
                    SKTextRenderer.DrawInlineObject = (inlineCanvas, seg, segX, baseY) =>
                    {
                        if (seg.InlineImageId is Guid id && _noteNumbering.MarkOf(id) is { } mark)
                            SKTextRenderer.DrawNoteMark(inlineCanvas, seg, mark, segX, baseY);
                        else if (seg.InlineImageId is Guid fieldId && _fieldTexts.TryGetValue(fieldId, out var fieldText))
                            SKTextRenderer.DrawNoteMark(inlineCanvas, seg, fieldText, segX, baseY);
                        else
                            previousInlineDrawer?.Invoke(inlineCanvas, seg, segX, baseY);
                    };
                }

                try
                {
                    SKTextRenderer.RenderPage(canvas, page, SKColors.Transparent);
                    DrawNoteRules(canvas, pageIndex);
                }
                finally
                {
                    SKTextRenderer.DrawInlineObject = previousInlineDrawer;
                }

                if (gutterShiftPt != 0f) canvas.Restore();

                RenderHeaderFooter(canvas, pageIndex, page, gutterShiftPt);
            }
            finally
            {
                SKTextRenderer.PrintImageResolver = null;
                SKTextRenderer.PrintGutterShiftPt = 0f;
            }
        }

        /// <summary>
        /// Текст живого поля в печати: дата — сейчас, номер страницы и число листов —
        /// по раскладке печати; до первой раскладки — сохранённые в документе.
        /// </summary>
        private string? PrintFieldText(Guid id)
        {
            if (!_liveFields.TryGetValue(id, out var field)) return null;

            string text = FieldCodes.KindOf(field.Instruction) switch
            {
                LiveFieldKind.Date or LiveFieldKind.Time
                    => FieldCodes.FormatDateTime(field.Instruction, DateTime.Now, null),
                LiveFieldKind.Page or LiveFieldKind.NumPages when _printPageValues.TryGetValue(id, out var value)
                    => value,
                LiveFieldKind.None => field.Result,
                _ => string.IsNullOrEmpty(field.Result) ? "1" : field.Result
            };

            _fieldTexts[id] = text;
            return text;
        }

        /// <summary>
        /// Сверяет номера страниц в полях с листами печати, на которых поля встали.
        /// true — хоть одно поле сверстано не тем номером и раскладку надо собрать заново.
        /// </summary>
        private bool UpdatePrintPageValues()
        {
            if (_liveFields.Count == 0) return false;

            var pages = _pageLayout.Pages;
            var pageOf = new System.Collections.Generic.Dictionary<Guid, int>();

            void ScanLines(Core.Models.Rendering.SKTextLayout? layout, int lineFrom, int lineTo, int pageIndex)
            {
                if (layout is null) return;

                lineFrom = Math.Max(0, lineFrom);
                lineTo = lineTo < 0 ? layout.Lines.Count : Math.Min(lineTo, layout.Lines.Count);

                for (int li = lineFrom; li < lineTo; li++)
                    foreach (var segment in layout.Lines[li].Segments)
                        if (segment.InlineImageId is Guid id && _liveFields.ContainsKey(id))
                            pageOf.TryAdd(id, pageIndex);
            }

            for (int pi = 0; pi < pages.Count; pi++)
            {
                var page = pages[pi];

                foreach (var paragraph in page.Paragraphs)
                    ScanLines(paragraph.Layout, paragraph.LineFrom, paragraph.LineTo, pi);

                foreach (var pageTable in page.Tables)
                {
                    var layout = pageTable.Layout;
                    if (layout is null) continue;

                    int rowFrom = Math.Max(0, pageTable.RowFrom);
                    int rowTo = pageTable.RowTo < 0 ? layout.Rows.Count : Math.Min(pageTable.RowTo, layout.Rows.Count);

                    for (int ri = rowFrom; ri < rowTo; ri++)
                        foreach (var cell in layout.Rows[ri].Cells)
                            foreach (var cellParagraph in cell.Paragraphs)
                                ScanLines(cellParagraph.Layout, 0, -1, pi);
                }
            }

            int PageNumber(int pageIndex)
                => pageIndex >= 0 && pageIndex < _decorations.Length && _decorations[pageIndex].Number > 0
                    ? _decorations[pageIndex].Number
                    : pageIndex + 1;

            bool changed = false;

            foreach (var (id, field) in _liveFields)
            {
                string? value = FieldCodes.KindOf(field.Instruction) switch
                {
                    LiveFieldKind.NumPages => FieldCodes.FormatNumber(field.Instruction, _pageLayout.PageCount),
                    LiveFieldKind.Page when pageOf.TryGetValue(id, out int pageIndex)
                        => FieldCodes.FormatNumber(field.Instruction, PageNumber(pageIndex)),
                    _ => null
                };

                if (value is null) continue;

                _printPageValues[id] = value;
                // Поле, которого нет в раскладке, пересборки не требует.
                if (_fieldTexts.TryGetValue(id, out var laidOut) && laidOut != value)
                    changed = true;
            }

            return changed;
        }

        /// <summary>Черты над сносками листа печати.</summary>
        private void DrawNoteRules(SKCanvas canvas, int pageIndex)
        {
            if (_noteRules.Count == 0) return;

            using var paint = new SKPaint
            {
                Color = SKColors.Black,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = NoteFlow.RuleThicknessPt,
                IsAntialias = true
            };

            foreach (var rule in _noteRules)
            {
                if (rule.PageIndex != pageIndex) continue;
                canvas.DrawLine(rule.XPt, rule.YPt, rule.XPt + rule.LengthPt, rule.YPt, paint);
            }
        }

        /// <summary>
        /// Сдвиг содержимого листа из-за стороны переплёта, в пунктах. Раскладка печати
        /// кладёт переплёт слева на каждом листе; когда у чётных и нечётных страниц
        /// разные колонтитулы, Word считает документ двусторонним, и на листе с чётным
        /// печатаемым номером переплёт справа: текст стоит левее на его ширину
        /// (отрицательное значение). Ноль — переплёта нет или он на этом листе слева.
        /// </summary>
        private float GutterShiftPt(int pageIndex)
        {
            if (_document.HeaderFooter?.DifferentOddEven != true) return 0f;

            // Переплёт у каждого раздела свой: лист раздела со своим листом сдвигается
            // на него, а не на переплёт документа.
            double gutterMm = pageIndex >= 0 && pageIndex < _pageLayout.Pages.Count
                ? SKTextRenderer.PrintSheetGutterMm(_pageLayout.Pages[pageIndex]) ?? _pageSettings.MarginGutterMm
                : _pageSettings.MarginGutterMm;
            if (gutterMm <= 0) return 0f;

            int number = pageIndex >= 0 && pageIndex < _decorations.Length
                ? _decorations[pageIndex].Number
                : pageIndex + 1;
            if (number % 2 != 0) return 0f;

            return -(float)(gutterMm * 72.0 / 25.4);
        }

        /// <summary>
        /// Пишет листы печати в PDF. Это та же вёрстка страниц и тот же движок, что у
        /// печати: PDF выходит полным — со всеми эффектами букв, таблицами, картинками,
        /// фигурами и колонтитулами — из любого режима редактора, а не только из
        /// режима «Страницы». Скрытый текст в файл не идёт, как и на печать.
        /// </summary>
        /// <param name="outputPath">Путь к создаваемому файлу .pdf.</param>
        /// <param name="rasterDpi">Разрешение, в котором PDF растрирует то, что не выражается векторно.</param>
        public void WritePdf(string outputPath, float rasterDpi)
        {
            using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);

            // От значений по умолчанию, а не с нуля: у пустых сведений качество сжатия
            // картинок равно нулю, и перекодированный растр выходил кашей.
            var metadata = SKDocumentPdfMetadata.Default;
            metadata.Title = _document.Title ?? string.Empty;
            metadata.Creator = "Writersword";
            metadata.Producer = "Writersword";
            metadata.Creation = DateTime.Now;
            metadata.Modified = DateTime.Now;
            metadata.RasterDpi = rasterDpi;

            using var pdf = SKDocument.CreatePdf(stream, metadata)
                ?? throw new InvalidOperationException("Не удалось создать PDF-документ (SkiaSharp вернул null).");

            for (int pi = 0; pi < _pageLayout.Pages.Count; pi++)
            {
                var page = _pageLayout.Pages[pi];
                var canvas = pdf.BeginPage(page.PageWidthPt, page.PageHeightPt);
                RenderPage(pi, canvas, page.PageWidthPt, page.PageHeightPt);
                pdf.EndPage();
            }

            pdf.Close();
        }

        /// <summary>Колонтитулы листа печати — тем же художником, что на полотне.</summary>
        /// <param name="gutterShiftPt">
        /// Сдвиг содержимого листа из-за стороны переплёта: колонтитулы стоят над той же
        /// полосой набора, что и текст, и уходят вместе с ней.
        /// </param>
        private void RenderHeaderFooter(
            SKCanvas canvas, int pageIndex, Core.Models.Rendering.SKPageContent page, float gutterShiftPt)
        {
            var settings = _document.HeaderFooter;
            if (settings is null || pageIndex >= _decorations.Length) return;

            float marginLeft = Math.Max(page.MarginLeftPt + gutterShiftPt, 0f);
            float marginRight = Math.Max(page.PageWidthPt - marginLeft - page.TextWidthPt, 0f);
            float marginBottom = Math.Max(page.PageHeightPt - page.MarginTopPt - page.TextHeightPt, 0f);

            var box = new HeaderFooterPageBox(
                0f, 0f, page.PageWidthPt, page.PageHeightPt,
                marginLeft, marginRight, page.MarginTopPt, marginBottom,
                (float)(_document.PageSettings.HeaderDistanceMm * 72.0 / 25.4),
                (float)(_document.PageSettings.FooterDistanceMm * 72.0 / 25.4));

            var color = new SKColor(0x1A, 0x1A, 0x1A);
            if (!string.IsNullOrWhiteSpace(settings.TextColor) && SKColor.TryParse(settings.TextColor, out var own))
                color = own;

            HeaderFooterPainter.DrawPage(canvas, settings, _decorations[pageIndex], box, color,
                SKTextRenderer.ResolveTypeface, _styles.ResolveFontFamily(StyleResolver.DefaultStyleName));
        }

        /// <summary>
        /// Габарит встроенной в строку картинки в пунктах. Повёрнутая картинка занимает
        /// свой AABB — так же, как в редакторе.
        /// </summary>
        private (float WidthPt, float HeightPt)? GetInlineImageSize(Guid id)
        {
            foreach (var section in _document.Sections)
            {
                foreach (var block in section.InlineObjects)
                {
                    if (block.Id != id) continue;

                    // Формула: ширина и полная высота вместе с частью под базовой линией.
                    if (block is FormulaBlock formula)
                    {
                        var metrics = Rendering.Formulas.FormulaRenderer.Measure(formula);
                        return (metrics.WidthPt, metrics.HeightPt);
                    }

                    // Объект строки — картинка или фигура; габарит у обоих один и
                    // тот же, что и в редакторе (FloatingObjectBox).
                    if (block is not IFloatingObject floating) continue;

                    return FloatingObjectBox.Of(floating, (float)floating.WidthPt, (float)floating.HeightPt);
                }
            }
            return null;
        }

        /// <summary>Спуск формулы под базовую линию; у картинки и фигуры его нет.</summary>
        private float GetInlineObjectDescent(Guid id)
        {
            foreach (var section in _document.Sections)
            {
                foreach (var block in section.InlineObjects)
                {
                    if (block is FormulaBlock formula && formula.Id == id)
                        return Rendering.Formulas.FormulaRenderer.Measure(formula).DescentPt;
                }
            }
            return 0f;
        }

        /// <summary>
        /// Колонтитул выше поля листа отодвигает текст, как на полотне: поле печати
        /// увеличивается до места, которое колонтитул занимает.
        /// </summary>
        private void ReserveHeaderFooterSpace()
        {
            const double pointsPerMm = 72.0 / 25.4;

            var (top, bottom) = HeaderFooterPainter.BodyReservePt(_document.HeaderFooter,
                (float)(_pageSettings.HeaderDistanceMm * pointsPerMm),
                (float)(_pageSettings.FooterDistanceMm * pointsPerMm),
                SKTextRenderer.ResolveTypeface, _styles.ResolveFontFamily(StyleResolver.DefaultStyleName));

            if (top > 0f) _pageSettings.MarginTopMm = Math.Max(_pageSettings.MarginTopMm, top / pointsPerMm);
            if (bottom > 0f) _pageSettings.MarginBottomMm = Math.Max(_pageSettings.MarginBottomMm, bottom / pointsPerMm);
        }

        // ── Конвертация PageSettings ──────────────────────────────────────

        /// <summary>
        /// Конвертирует TextEditorPageSettings в PrintPageSettings из Core.
        /// Типы PaperSize и PageOrientation общие — выполняется прямое копирование.
        /// </summary>
        private static PrintPageSettings ConvertPageSettings(TextEditorPageSettings src) => new()
        {
            PaperSize = src.PaperSize,
            WidthMm = src.WidthMm,
            HeightMm = src.HeightMm,
            Orientation = src.Orientation,
            MarginTopMm = src.MarginTopMm,
            MarginBottomMm = src.MarginBottomMm,
            MarginLeftMm = src.MarginLeftMm,
            MarginRightMm = src.MarginRightMm,
            MarginGutterMm = src.MarginGutterMm,
            HeaderDistanceMm = src.HeaderDistanceMm,
            FooterDistanceMm = src.FooterDistanceMm
        };
    }
}