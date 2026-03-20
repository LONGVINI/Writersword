using System;
using SkiaSharp;
using Writersword.Core.Interfaces.Print;
using Writersword.Core.Models.Print;
using Writersword.Infrastructure.Rendering;
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
            _styles = new StyleResolver(document.Styles);
            _pageLayout = _renderer.BuildPageLayout(_document, _pageSettings, _styles);
        }

        /// <inheritdoc/>
        public void RenderPage(int pageIndex, SKCanvas canvas, float pageWidthPt, float pageHeightPt)
        {
            if (pageIndex < 0 || pageIndex >= _pageLayout.Pages.Count) return;

            var page = _pageLayout.Pages[pageIndex];
            SKTextRenderer.RenderPage(canvas, page, SKColors.Transparent);
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