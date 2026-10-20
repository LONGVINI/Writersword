using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using System;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Rendering.Charts;

namespace Writersword.Modules.TextEditor.Views.Dialogs
{
    /// <summary>
    /// Образец диаграммы в окне: рисуется прямо на холсте окна той же отрисовкой, что
    /// и на листе, — векторно, без промежуточной картинки, чёткий при любом масштабе
    /// экрана. Диаграмма вписывается в размер элемента с сохранением пропорций.
    /// </summary>
    internal sealed class ChartPreviewControl : Control
    {
        private ChartModel? _chart;

        /// <summary>Ширина диаграммы в пунктах: по ней берутся размеры шрифтов и линий.</summary>
        public double ChartWidthPt { get; set; } = 432.0;

        /// <summary>Высота диаграммы в пунктах.</summary>
        public double ChartHeightPt { get; set; } = 252.0;

        /// <summary>
        /// Картинки хранилища проекта по имени файла — для фона диаграммы. Вызывается в
        /// потоке отрисовки: источник только читает готовые картинки.
        /// </summary>
        public Func<string, SKImage?>? Images { get; set; }

        /// <summary>Показываемая диаграмма. Модель не меняется после передачи: правка — новая модель.</summary>
        public ChartModel? Chart
        {
            get => _chart;
            set
            {
                _chart = value;
                InvalidateVisual();
            }
        }

        public override void Render(DrawingContext context)
        {
            var bounds = new Rect(Bounds.Size);
            context.FillRectangle(Brushes.White, bounds);

            if (_chart is null || bounds.Width < 1 || bounds.Height < 1 || ChartWidthPt <= 0 || ChartHeightPt <= 0) return;
            context.Custom(new ChartDrawOperation(bounds, _chart, (float)ChartWidthPt, (float)ChartHeightPt, Images));
        }

        private sealed class ChartDrawOperation : ICustomDrawOperation
        {
            private readonly ChartModel _chart;
            private readonly float _widthPt;
            private readonly float _heightPt;
            private readonly Func<string, SKImage?>? _images;

            public ChartDrawOperation(Rect bounds, ChartModel chart, float widthPt, float heightPt, Func<string, SKImage?>? images)
            {
                Bounds = bounds;
                _chart = chart;
                _widthPt = widthPt;
                _heightPt = heightPt;
                _images = images;
            }

            public Rect Bounds { get; }

            public void Dispose() { }

            public bool Equals(ICustomDrawOperation? other) => false;

            public bool HitTest(Point p) => Bounds.Contains(p);

            public void Render(ImmediateDrawingContext context)
            {
                if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature feature) return;

                using var lease = feature.Lease();
                var canvas = lease.SkCanvas;

                float scale = (float)Math.Min(Bounds.Width / _widthPt, Bounds.Height / _heightPt);
                float offsetX = (float)(Bounds.X + (Bounds.Width - _widthPt * scale) / 2.0);
                float offsetY = (float)(Bounds.Y + (Bounds.Height - _heightPt * scale) / 2.0);

                int saved = canvas.Save();
                try
                {
                    canvas.ClipRect(SKRect.Create((float)Bounds.X, (float)Bounds.Y, (float)Bounds.Width, (float)Bounds.Height));
                    canvas.Translate(offsetX, offsetY);
                    canvas.Scale(scale);
                    ChartRenderer.Draw(canvas, _chart, new SKRect(0, 0, _widthPt, _heightPt), _images);
                }
                catch
                {
                    // Образец не нарисовался — окно остаётся рабочим, лист рисует сам.
                }
                finally
                {
                    canvas.RestoreToCount(saved);
                }
            }
        }
    }
}
