using System;
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
            // Габарит картинки в строке: без него объект встал бы в строку нулевой ширины
            // и переносы строк в печати разошлись бы с редактором.
            _renderer.InlineImageSize = GetInlineImageSize;
            _styles = new StyleResolver(document.Styles);
            _pageLayout = _renderer.BuildPageLayout(_document, _pageSettings, _styles);
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
            try
            {
                SKTextRenderer.RenderPage(canvas, page, SKColors.Transparent);
            }
            finally
            {
                SKTextRenderer.PrintImageResolver = null;
            }
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
                    if (block is not ImageBlock image || image.Id != id) continue;

                    double rad = image.RotationDeg * Math.PI / 180.0;
                    float absCos = (float)Math.Abs(Math.Cos(rad));
                    float absSin = (float)Math.Abs(Math.Sin(rad));
                    float w = (float)image.WidthPt;
                    float h = (float)image.HeightPt;

                    return (w * absCos + h * absSin, w * absSin + h * absCos);
                }
            }
            return null;
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