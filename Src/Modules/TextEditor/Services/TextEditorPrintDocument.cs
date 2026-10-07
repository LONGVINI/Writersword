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
        private readonly Core.Models.Rendering.SKPageLayout _pageLayout;

        // ── IPrintableDocument ────────────────────────────────────────────

        public string Title => _document.Title;
        public int PageCount => _pageLayout.PageCount;
        public PrintPageSettings PageSettings => _pageSettings;

        public TextEditorPrintDocument(DocumentModel document)
        {
            _document = document ?? throw new ArgumentNullException(nameof(document));
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
            _pageLayout = _renderer.BuildPageLayout(_document, _pageSettings, _styles);
            _decorations = BuildDecorations();
        }

        // Оформление листов печати: номера и видимость колонтитулов. Считается по
        // раскладке печати, а не по полотну: печать может идти без открытого редактора.
        private readonly PageDecoration[] _decorations;

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
                    if (block is BreakBlock bb && bb.BreakType == BreakType.Page) continue;
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

                SKTextRenderer.RenderPage(canvas, page, SKColors.Transparent);

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
        /// Сдвиг содержимого листа из-за стороны переплёта, в пунктах. Раскладка печати
        /// кладёт переплёт слева на каждом листе; когда у чётных и нечётных страниц
        /// разные колонтитулы, Word считает документ двусторонним, и на листе с чётным
        /// печатаемым номером переплёт справа: текст стоит левее на его ширину
        /// (отрицательное значение). Ноль — переплёта нет или он на этом листе слева.
        /// </summary>
        private float GutterShiftPt(int pageIndex)
        {
            if (_pageSettings.MarginGutterMm <= 0) return 0f;
            if (_document.HeaderFooter?.DifferentOddEven != true) return 0f;

            int number = pageIndex >= 0 && pageIndex < _decorations.Length
                ? _decorations[pageIndex].Number
                : pageIndex + 1;
            if (number % 2 != 0) return 0f;

            return -(float)(_pageSettings.MarginGutterMm * 72.0 / 25.4);
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
                    // Объект строки — картинка или фигура; габарит у обоих один и
                    // тот же, что и в редакторе (FloatingObjectBox).
                    if (block is not IFloatingObject floating || block.Id != id) continue;

                    return FloatingObjectBox.Of(floating, (float)floating.WidthPt, (float)floating.HeightPt);
                }
            }
            return null;
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