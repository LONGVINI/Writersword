using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using System;
using System.ComponentModel;
using Writersword.Modules.TextEditor.Models.Settings;
using Writersword.Modules.TextEditor.ViewModels.Components;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Вертикальная линейка редактора.
    ///
    /// ПОВЕДЕНИЕ (аналогично Word):
    /// • Шкала рисуется только для ОДНОЙ страницы — той, на которой стоит каретка
    ///   (FocusedPageIndex из RulerViewModel). Никаких перекрытий меток.
    /// • Ноль шкалы = верхняя граница ТЕКСТОВОЙ области (после верхнего поля).
    /// • Поля закрашены серым, текстовая зона — светлым фоном.
    /// </summary>
    public sealed class VerticalRulerControl : Control
    {
        private const double RulerWidthPx = 24.0;
        private const double MajorTickWidthPx = 10.0;
        private const double MinorTickWidthPx = 6.0;
        private const double TinyTickWidthPx = 3.0;

        private static readonly SKColor ColBg = new(0xF0, 0xF0, 0xF0);
        private static readonly SKColor ColMarginZone = new(0xD8, 0xD8, 0xD8);
        private static readonly SKColor ColTickMajor = new(0x60, 0x60, 0x60);
        private static readonly SKColor ColTickMinor = new(0x99, 0x99, 0x99);
        private static readonly SKColor ColTickTiny = new(0xBB, 0xBB, 0xBB);
        private static readonly SKColor ColTickMajorM = new(0x99, 0x99, 0x99);
        private static readonly SKColor ColTickMinorM = new(0xBB, 0xBB, 0xBB);
        private static readonly SKColor ColTickTinyM = new(0xD0, 0xD0, 0xD0);
        private static readonly SKColor ColLabel = new(0x44, 0x44, 0x44);
        private static readonly SKColor ColLabelMargin = new(0x88, 0x88, 0x88);
        private static readonly SKColor ColBorder = new(0xCC, 0xCC, 0xCC);
        private static readonly SKColor ColMarginHandle = new(0x88, 0x88, 0x88);

        private RulerViewModel? _vm;
        private bool _isDraggingMargin;
        private bool _draggingTopMargin;
        // Сохраняем геометрию страницы в момент нажатия — не пересчитываем во время drag,
        // чтобы изменение FocusedPageIndex (смена каретки) не смещало маркер.
        private double _dragPageTopY;
        private double _dragPageBotY;

        public VerticalRulerControl()
        {
            Width = RulerWidthPx;
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (_vm is not null)
                _vm.PropertyChanged -= OnVmChanged;
            _vm = DataContext as RulerViewModel;
            if (_vm is not null)
                _vm.PropertyChanged += OnVmChanged;
            InvalidateVisual();
        }

        private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
            => InvalidateVisual();

        public override void Render(DrawingContext ctx)
        {
            ctx.Custom(new RulerDrawOp(this,
                new Rect(0, 0, Bounds.Width, Bounds.Height)));
        }

        internal void RenderWithSKCanvas(SKCanvas canvas)
        {
            if (_vm is null) return;

            float w = (float)RulerWidthPx;
            float h = (float)Bounds.Height;
            double zoom = _vm.Zoom;
            double scrollY = _vm.ScrollOffsetY;

            const double PageGapPt = 15.0;
            const double PtToPx = 96.0 / 72.0;

            double pageHeightPx = MmToPx(_vm.PageHeightMm, zoom);
            double marginTopPx = MmToPx(_vm.MarginTopMm, zoom);
            double marginBotPx = MmToPx(_vm.MarginBottomMm, zoom);
            double pageGapPx = PageGapPt * PtToPx * zoom;
            double pageWithGapH = pageHeightPx + pageGapPx;

            // ── Страница по индексу каретки ───────────────────────────────
            // Используем FocusedPageIndex — ту страницу, где стоит каретка,
            // а не страницу в центре viewport. Это точное поведение как в Word.
            int currentPageIdx = Math.Max(0, _vm.FocusedPageIndex);

            double pTopY = pageGapPx + currentPageIdx * pageWithGapH - scrollY;
            double tTopY = pTopY + marginTopPx;
            double tBotY = pTopY + pageHeightPx - marginBotPx;
            double pBotY = pTopY + pageHeightPx;

            // ── Фон ──────────────────────────────────────────────────────
            using var bgPaint = new SKPaint { Color = ColBg };
            canvas.DrawRect(0, 0, w, h, bgPaint);

            // ── Серые зоны (поля) ─────────────────────────────────────────
            using var marginPaint = new SKPaint { Color = ColMarginZone };

            if (tTopY > 0)
                canvas.DrawRect(0, 0, w, (float)Math.Min(tTopY, h), marginPaint);

            if (tBotY < h)
                canvas.DrawRect(0, (float)Math.Max(0, tBotY),
                    w, h - (float)Math.Max(0, tBotY), marginPaint);

            // Зазор между страницами если попадает в видимую область.
            if (pBotY > 0 && pBotY < h)
            {
                float gapH = (float)Math.Min(pageGapPx, h - pBotY);
                if (gapH > 0)
                    canvas.DrawRect(0, (float)pBotY, w, gapH, marginPaint);
            }

            // ── Линии границ полей ────────────────────────────────────────
            using var handlePaint = new SKPaint
            { Color = ColMarginHandle, StrokeWidth = 1f, IsStroke = true };
            if (tTopY > 0 && tTopY < h)
                canvas.DrawLine(0, (float)tTopY, w, (float)tTopY, handlePaint);
            if (tBotY > 0 && tBotY < h)
                canvas.DrawLine(0, (float)tBotY, w, (float)tBotY, handlePaint);

            // ── Шкала ─────────────────────────────────────────────────────
            DrawScale(canvas, tTopY, tBotY, w, h, zoom);

            // ── Правая граница ────────────────────────────────────────────
            using var borderPaint = new SKPaint
            { Color = ColBorder, StrokeWidth = 1f, IsStroke = true };
            canvas.DrawLine(w - 0.5f, 0, w - 0.5f, h, borderPaint);
        }

        private void DrawScale(
            SKCanvas canvas,
            double tTopY, double tBotY,
            float w, float h,
            double zoom)
        {
            if (_vm is null) return;

            double unitSizePx = UnitSizePx(zoom);
            double majorInterval = _vm.MajorTickInterval;
            double minorInterval = _vm.MinorTickInterval;
            double tinyInterval = _vm.TinyTickInterval;

            int tinyPerMajor = (int)Math.Round(majorInterval / tinyInterval);
            int tinyPerMinor = (int)Math.Round(minorInterval / tinyInterval);

            double textHU = _vm.MmToUnits(_vm.PageHeightMm - _vm.MarginTopMm - _vm.MarginBottomMm);
            double pageTopY = tTopY - MmToPx(_vm.MarginTopMm, zoom);
            double pageBotY = tBotY + MmToPx(_vm.MarginBottomMm, zoom);
            int stepsUp = (int)Math.Ceiling((tTopY - pageTopY) / (unitSizePx * tinyInterval)) + 2;
            int stepsDown = (int)Math.Ceiling((pageBotY - tTopY) / (unitSizePx * tinyInterval)) + 2;

            using var majorP = StrokePaint(ColTickMajor);
            using var minorP = StrokePaint(ColTickMinor);
            using var tinyP = StrokePaint(ColTickTiny);
            using var majorPM = StrokePaint(ColTickMajorM);
            using var minorPM = StrokePaint(ColTickMinorM);
            using var tinyPM = StrokePaint(ColTickTinyM);
            using var labelP = new SKPaint { Color = ColLabel, IsAntialias = true };
            using var labelPM = new SKPaint { Color = ColLabelMargin, IsAntialias = true };

            using var tf = SKTypeface.FromFamilyName("Arial", SKFontStyle.Normal) ?? SKTypeface.Default;
            using var font = new SKFont(tf, 8f);

            for (int i = -stepsUp; i <= stepsDown; i++)
            {
                double unitValue = i * tinyInterval;
                double yPx = tTopY + unitValue * unitSizePx;

                if (yPx < -2 || yPx > h + 2) continue;

                bool inMargin = unitValue < 0 || unitValue > textHU;
                bool isMajor = (i % tinyPerMajor) == 0;
                bool isMinor = !isMajor && (i % tinyPerMinor) == 0;

                float tickW = isMajor ? (float)MajorTickWidthPx
                            : isMinor ? (float)MinorTickWidthPx
                            : (float)TinyTickWidthPx;

                SKPaint paint = inMargin
                    ? (isMajor ? majorPM : isMinor ? minorPM : tinyPM)
                    : (isMajor ? majorP : isMinor ? minorP : tinyP);

                canvas.DrawLine(w - tickW, (float)yPx, w, (float)yPx, paint);

                if (!isMajor) continue;
                if (Math.Abs(unitValue) <= majorInterval * 0.1) continue;

                double displayValue = inMargin
                    ? (unitValue < 0 ? -unitValue : unitValue - textHU)
                    : unitValue;

                string label = _vm.Units == RulerUnits.Inches
                    ? displayValue.ToString("0.##")
                    : ((int)Math.Round(displayValue * 10)).ToString();

                using var save = new SKAutoCanvasRestore(canvas, true);
                canvas.Translate(w - tickW - 2f, (float)yPx);
                canvas.RotateDegrees(-90);
                float textW = font.MeasureText(label);
                canvas.DrawText(label, -textW / 2f, 0, font, inMargin ? labelPM : labelP);
            }
        }

        // ── Pointer events ────────────────────────────────────────────────

        private const double MarginHitPx = 5.0;

        private (double pTopY, double pBotY, double tTopY, double tBotY) ComputePageGeometry()
        {
            if (_vm is null) return (0, 0, 0, 0);

            const double PageGapPt = 15.0; const double PtToPx = 96.0 / 72.0;
            double zoom = _vm.Zoom;
            double scrollY = _vm.ScrollOffsetY;
            double pageHeightPx = MmToPx(_vm.PageHeightMm, zoom);
            double pageGapPx = PageGapPt * PtToPx * zoom;
            int pageIdx = Math.Max(0, _vm.FocusedPageIndex);
            double pTopY = pageGapPx + pageIdx * (pageHeightPx + pageGapPx) - scrollY;
            double pBotY = pTopY + pageHeightPx;
            double tTopY = pTopY + MmToPx(_vm.MarginTopMm, zoom);
            double tBotY = pTopY + pageHeightPx - MmToPx(_vm.MarginBottomMm, zoom);
            return (pTopY, pBotY, tTopY, tBotY);
        }

        protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (_vm is null) return;

            var pos = e.GetPosition(this);
            var (pTopY, pBotY, tTopY, tBotY) = ComputePageGeometry();

            if (Math.Abs(pos.Y - tTopY) <= MarginHitPx)
            {
                _isDraggingMargin = true; _draggingTopMargin = true;
                _dragPageTopY = pTopY; _dragPageBotY = pBotY;
                e.Pointer.Capture(this);
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeNorthSouth);
                e.Handled = true;
            }
            else if (Math.Abs(pos.Y - tBotY) <= MarginHitPx)
            {
                _isDraggingMargin = true; _draggingTopMargin = false;
                _dragPageTopY = pTopY; _dragPageBotY = pBotY;
                e.Pointer.Capture(this);
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeNorthSouth);
                e.Handled = true;
            }
        }

        protected override void OnPointerMoved(Avalonia.Input.PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (_vm is null) return;

            var pos = e.GetPosition(this);
            double zoom = _vm.Zoom;
            var (pTopY, pBotY, tTopY, tBotY) = ComputePageGeometry();

            if (_isDraggingMargin)
            {
                double clampedY = Math.Max(_dragPageTopY, Math.Min(pos.Y, _dragPageBotY));
                if (_draggingTopMargin)
                {
                    double newMm = PxToMm(clampedY - _dragPageTopY, zoom);
                    if (_vm.IsSnapEnabled) { double s = _vm.UnitsToMm(_vm.SnapStep); newMm = Math.Round(newMm / s) * s; }
                    newMm = Math.Max(0, Math.Min(newMm, _vm.PageHeightMm - _vm.MarginBottomMm - 5));
                    _vm.MarginTopMm = newMm;
                }
                else
                {
                    double newMm = PxToMm(_dragPageBotY - clampedY, zoom);
                    if (_vm.IsSnapEnabled) { double s = _vm.UnitsToMm(_vm.SnapStep); newMm = Math.Round(newMm / s) * s; }
                    newMm = Math.Max(0, Math.Min(newMm, _vm.PageHeightMm - _vm.MarginTopMm - 5));
                    _vm.MarginBottomMm = newMm;
                }
                _vm.NotifyMarginChanged();
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            if (Math.Abs(pos.Y - tTopY) <= MarginHitPx || Math.Abs(pos.Y - tBotY) <= MarginHitPx)
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeNorthSouth);
            else
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Arrow);
        }

        protected override void OnPointerReleased(Avalonia.Input.PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (!_isDraggingMargin) return;
            _isDraggingMargin = false;
            e.Pointer.Capture(null);
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Arrow);
            _vm?.CommitMarginChange();
            InvalidateVisual();
            e.Handled = true;
        }

        // ── Helpers ───────────────────────────────────────────────────────

        private static double PxToMm(double px, double zoom)
            => px / (96.0 / 25.4) / zoom;

        private double UnitSizePx(double zoom)
        {
            if (_vm is null) return 96.0 * zoom;
            double unitMm = _vm.Units == RulerUnits.Inches ? 25.4 : 10.0;
            return unitMm * (96.0 / 25.4) * zoom;
        }

        private static double MmToPx(double mm, double zoom)
            => mm * (96.0 / 25.4) * zoom;

        private static SKPaint StrokePaint(SKColor color) => new()
        {
            Color = color,
            StrokeWidth = 1f,
            IsStroke = true,
            IsAntialias = false
        };

        // ── ICustomDrawOperation ──────────────────────────────────────────

        private sealed class RulerDrawOp : ICustomDrawOperation
        {
            private readonly VerticalRulerControl _ruler;
            public Rect Bounds { get; }

            public RulerDrawOp(VerticalRulerControl ruler, Rect bounds)
            {
                _ruler = ruler;
                Bounds = bounds;
            }

            public void Dispose() { }
            public bool Equals(ICustomDrawOperation? other) => false;
            public bool HitTest(Point p) => true;

            public void Render(ImmediateDrawingContext context)
            {
                var f = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature))
                    as ISkiaSharpApiLeaseFeature;
                if (f is null) return;
                using var lease = f.Lease();
                _ruler.RenderWithSKCanvas(lease.SkCanvas);
            }
        }
    }
}