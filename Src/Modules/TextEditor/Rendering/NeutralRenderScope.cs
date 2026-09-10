using System;
using SkiaSharp;

namespace Writersword.Modules.TextEditor.Rendering
{
    /// <summary>
    /// Отключает экранные подмены рендера на время печати и экспорта.
    ///
    /// Подмены — цвет текста, цвет бумаги, маркеры и линии таблиц, шрифт и кегль
    /// чтения — живут статическими полями <see cref="SKTextRenderer"/>: рендер один
    /// на все канвасы, и ставятся они перед каждым проходом. На экране это ровно то,
    /// что нужно. Но тем же рендером печатается бумага, и лист, перекрашенный видом
    /// рабочей области, уходил бы в принтер и в PDF вместе с цветом.
    ///
    /// Область снимает подмены на время прохода и возвращает их обратно, что бы в
    /// проходе ни случилось. Возврат обязателен: не вернуть их значит оставить
    /// открытый на экране документ с чёрным текстом на тёмной бумаге.
    /// </summary>
    public readonly struct NeutralRenderScope : IDisposable
    {
        private readonly SKColor? _textColor;
        private readonly SKColor? _paperColor;
        private readonly SKColor? _markerColor;
        private readonly SKColor? _borderColor;
        private readonly string? _fontFamily;
        private readonly float _fontScale;
        private readonly float _contentScale;

        private NeutralRenderScope(
            SKColor? textColor,
            SKColor? paperColor,
            SKColor? markerColor,
            SKColor? borderColor,
            string? fontFamily,
            float fontScale,
            float contentScale)
        {
            _textColor = textColor;
            _paperColor = paperColor;
            _markerColor = markerColor;
            _borderColor = borderColor;
            _fontFamily = fontFamily;
            _fontScale = fontScale;
            _contentScale = contentScale;
        }

        /// <summary>Снимает подмены и запоминает их, чтобы вернуть в конце прохода.</summary>
        public static NeutralRenderScope Begin()
        {
            var scope = new NeutralRenderScope(
                SKTextRenderer.DefaultTextColorOverride,
                SKTextRenderer.ReadingPaperColorOverride,
                SKTextRenderer.ReadingMarkerColorOverride,
                SKTextRenderer.ReadingBorderColorOverride,
                SKTextRenderer.ReadingFontFamilyOverride,
                SKTextRenderer.ReadingFontScale,
                SKTextRenderer.ReadingContentScale);

            SKTextRenderer.DefaultTextColorOverride = null;
            SKTextRenderer.ReadingPaperColorOverride = null;
            SKTextRenderer.ReadingMarkerColorOverride = null;
            SKTextRenderer.ReadingBorderColorOverride = null;
            SKTextRenderer.ReadingFontFamilyOverride = null;
            SKTextRenderer.ReadingFontScale = 1f;
            SKTextRenderer.ReadingContentScale = 1f;

            return scope;
        }

        public void Dispose()
        {
            SKTextRenderer.DefaultTextColorOverride = _textColor;
            SKTextRenderer.ReadingPaperColorOverride = _paperColor;
            SKTextRenderer.ReadingMarkerColorOverride = _markerColor;
            SKTextRenderer.ReadingBorderColorOverride = _borderColor;
            SKTextRenderer.ReadingFontFamilyOverride = _fontFamily;
            SKTextRenderer.ReadingFontScale = _fontScale;
            SKTextRenderer.ReadingContentScale = _contentScale;
        }
    }
}
