using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SkiaSharp;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.Rendering;

namespace Writersword.Modules.TextEditor.Views.Shared
{
    /// <summary>
    /// Миниатюра набора «Мои эффекты» для плитки меню «A»: буквы на белом листе,
    /// нарисованные движком документа (SKTextRenderer) со всем, что ставит набор.
    /// Что видно на плитке, то и ляжет в текст.
    ///
    /// Картинка рисуется в пикселях экрана и запоминается: плитки перерисовываются
    /// при каждом открытии меню, а набор и размер при этом обычно те же.
    /// </summary>
    public sealed class TextEffectPresetSample : Control
    {
        /// <summary>Набор, который показывает миниатюра.</summary>
        public static readonly StyledProperty<TextEffectPreset?> PresetProperty =
            AvaloniaProperty.Register<TextEffectPresetSample, TextEffectPreset?>(nameof(Preset));

        /// <summary>Текст миниатюры.</summary>
        public static readonly StyledProperty<string> TextProperty =
            AvaloniaProperty.Register<TextEffectPresetSample, string>(nameof(Text), "Аа");

        // Гарнитура и цвет букв миниатюры: набор цвет и шрифт не задаёт — буквы как
        // у обычного текста на листе.
        private const string SampleFontFamily = "Times New Roman";
        private static readonly SKColor SampleTextColor = new(0x1A, 0x1A, 0x1A);

        private Bitmap? _bitmap;
        private TextEffectPreset? _bitmapPreset;
        private string? _bitmapText;
        private int _bitmapWidthPx;
        private int _bitmapHeightPx;

        static TextEffectPresetSample()
        {
            AffectsRender<TextEffectPresetSample>(PresetProperty, TextProperty);
        }

        public TextEffectPreset? Preset
        {
            get => GetValue(PresetProperty);
            set => SetValue(PresetProperty, value);
        }

        public string Text
        {
            get => GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);

            var preset = Preset;
            double width = Bounds.Width;
            double height = Bounds.Height;
            if (preset is null || width <= 0 || height <= 0) return;

            double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
            int widthPx = Math.Max(1, (int)Math.Round(width * scaling));
            int heightPx = Math.Max(1, (int)Math.Round(height * scaling));

            if (_bitmap is null
                || !ReferenceEquals(_bitmapPreset, preset)
                || !string.Equals(_bitmapText, Text, StringComparison.Ordinal)
                || _bitmapWidthPx != widthPx
                || _bitmapHeightPx != heightPx)
            {
                _bitmap?.Dispose();
                _bitmap = Draw(preset, Text, width, height, scaling, widthPx, heightPx);
                _bitmapPreset = preset;
                _bitmapText = Text;
                _bitmapWidthPx = widthPx;
                _bitmapHeightPx = heightPx;
            }

            context.DrawImage(_bitmap, new Rect(0, 0, width, height));
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _bitmap?.Dispose();
            _bitmap = null;
            _bitmapPreset = null;
            base.OnDetachedFromVisualTree(e);
        }

        /// <summary>
        /// Миниатюра в картинку. Холст — в пунктах, как у документа: размеры эффектов
        /// (расстояние тени, размытие, толщина контура) на плитке те же, что в тексте
        /// того же кегля.
        /// </summary>
        private static Bitmap Draw(TextEffectPreset preset, string text,
            double widthDip, double heightDip, double scaling, int widthPx, int heightPx)
        {
            using var bitmap = new SKBitmap(widthPx, heightPx, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.White);

                float pointScale = (float)(scaling * 96.0 / 72.0);
                canvas.Scale(pointScale);

                float widthPt = (float)(widthDip * 72.0 / 96.0);
                float heightPt = (float)(heightDip * 72.0 / 96.0);

                // Кегль — около половины высоты: над буквами остаётся место для
                // свечения и знака ударения, под ними — для отражения и тени.
                float fontSizePt = heightPt * 0.5f;
                float textWidth = SKTextRenderer.MeasureEffectsPreview(text, SampleFontFamily, fontSizePt);
                float x = Math.Max(2f, (widthPt - textWidth) / 2f);
                float baseline = heightPt * 0.62f;

                SKTextRenderer.DrawPresetPreview(
                    canvas, text, SampleFontFamily, fontSizePt, SampleTextColor, preset, x, baseline);

                canvas.Flush();
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = data.AsStream();
            return new Bitmap(stream);
        }
    }
}
