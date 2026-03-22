using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using System;
using System.Collections.Generic;
using Writersword.Modules.TextEditor.ViewModels.Components;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Горизонтальная линейка редактора.
    /// Шкала идёт по всей ширине страницы включая отрицательные значения.
    /// Ноль = начало текстовой области (после левого поля).
    ///
    /// ИСПРАВЛЕНО:
    /// • Зоны полей (левое и правое) внутри листа теперь тоже закрашены серым —
    ///   как в Word. Белым остаётся только текстовая зона.
    /// • Стандартные отступы страницы визуально выделены, что упрощает понимание
    ///   маркеров отступа параграфа.
    /// </summary>
    public sealed class HorizontalRulerControl : Control
    {
        // ── Константы геометрии ───────────────────────────────────────────
        private const double RulerHeightPx = 24.0;
        private const double MarkerSizePx = 8.0;
        private const double MarkerHitRadiusPx = 7.0;
        private const double MajorTickHeightPx = 10.0;
        private const double MinorTickHeightPx = 6.0;
        private const double TinyTickHeightPx = 3.0;

        // ── Цвета ─────────────────────────────────────────────────────────
        private static readonly SKColor ColorBackground = new(0xF0, 0xF0, 0xF0);
        private static readonly SKColor ColorOutsidePage = new(0xD0, 0xD0, 0xD0);
        private static readonly SKColor ColorMarginZone = new(0xD8, 0xD8, 0xD8); // поля внутри листа
        private static readonly SKColor ColorTickMajor = new(0x60, 0x60, 0x60);
        private static readonly SKColor ColorTickMinor = new(0x99, 0x99, 0x99);
        private static readonly SKColor ColorTickTiny = new(0xBB, 0xBB, 0xBB);
        private static readonly SKColor ColorLabel = new(0x44, 0x44, 0x44);
        private static readonly SKColor ColorLabelNegative = new(0x99, 0x44, 0x44);
        private static readonly SKColor ColorBorder = new(0xCC, 0xCC, 0xCC);
        private static readonly SKColor ColorMarkerIndent = new(0x33, 0x66, 0xCC);
        private static readonly SKColor ColorMarkerColumn = new(0x22, 0x99, 0x55);
        private static readonly SKColor ColorMarkerDragging = new(0xFF, 0x66, 0x00);
        private static readonly SKColor ColorGuideLine = new(0xFF, 0x66, 0x00, 0xAA);

        // ── Состояние ─────────────────────────────────────────────────────
        private RulerViewModel? _vm;
        private bool _isDragging;

        public HorizontalRulerControl()
        {
            Height = RulerHeightPx;
            Cursor = new Cursor(StandardCursorType.Arrow);
        }

        // ── Привязка к ViewModel ──────────────────────────────────────────

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (_vm is not null)
                _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = DataContext as RulerViewModel;
            if (_vm is not null)
                _vm.PropertyChanged += OnVmPropertyChanged;
            InvalidateVisual();
        }

        private void OnVmPropertyChanged(object? sender,
            System.ComponentModel.PropertyChangedEventArgs e)
            => InvalidateVisual();

        // ── Render ────────────────────────────────────────────────────────

        public override void Render(DrawingContext ctx)
        {
            ctx.Custom(new RulerSKDrawOperation(
                this, new Rect(0, 0, Bounds.Width, Bounds.Height)));
        }

        internal void RenderWithSKCanvas(SKCanvas canvas)
        {
            if (_vm is null) return;

            float w = (float)Bounds.Width;
            float h = (float)RulerHeightPx;
            double zoom = _vm.Zoom;

            // ── Фон ──────────────────────────────────────────────────────

            using var bgPaint = new SKPaint { Color = ColorBackground };
            canvas.DrawRect(0, 0, w, h, bgPaint);

            double pageOffsetXPx = _vm.PageOffsetXPx;
            double pageWidthPx = MmToPx(_vm.PageWidthMm, zoom);
            double marginLeftPx = MmToPx(_vm.MarginLeftMm, zoom);
            double marginRightPx = MmToPx(_vm.MarginRightMm, zoom);
            double textAreaStartPx = pageOffsetXPx + marginLeftPx;
            double textAreaEndPx = pageOffsetXPx + pageWidthPx - marginRightPx;
            double pageRightPx = pageOffsetXPx + pageWidthPx;

            // ── Серые зоны ────────────────────────────────────────────────

            using var outerPaint = new SKPaint { Color = ColorOutsidePage };
            using var marginPaint = new SKPaint { Color = ColorMarginZone };

            // 1. Слева от листа.
            if (pageOffsetXPx > 0)
                canvas.DrawRect(0, 0, (float)pageOffsetXPx, h, outerPaint);

            // 2. Справа от листа.
            if (pageRightPx < w)
                canvas.DrawRect((float)pageRightPx, 0,
                    w - (float)pageRightPx, h, outerPaint);

            // 3. Левое поле (между краем листа и началом текста).
            if (textAreaStartPx > pageOffsetXPx)
                canvas.DrawRect(
                    (float)pageOffsetXPx, 0,
                    (float)(textAreaStartPx - pageOffsetXPx), h,
                    marginPaint);

            // 4. Правое поле (между концом текста и правым краем листа).
            if (textAreaEndPx < pageRightPx)
                canvas.DrawRect(
                    (float)textAreaEndPx, 0,
                    (float)(pageRightPx - textAreaEndPx), h,
                    marginPaint);

            // ── Деления и маркеры ────────────────────────────────────────

            DrawScale(canvas, pageOffsetXPx, pageWidthPx,
                textAreaStartPx, textAreaEndPx, h, zoom);

            if (_vm.Mode == RulerMode.Paragraph)
                DrawIndentMarkers(canvas, textAreaStartPx, textAreaEndPx, h, zoom);
            else
                DrawColumnMarkers(canvas, textAreaStartPx, h, zoom);

            // ── Нижняя граница ───────────────────────────────────────────

            using var borderPaint = new SKPaint
            { Color = ColorBorder, StrokeWidth = 1f, IsStroke = true };
            canvas.DrawLine(0, h - 0.5f, w, h - 0.5f, borderPaint);
        }

        // ── Шкала ────────────────────────────────────────────────────────

        private void DrawScale(
            SKCanvas canvas,
            double pageOffsetXPx,
            double pageWidthPx,
            double textAreaStartPx,
            double textAreaEndPx,
            float h,
            double zoom)
        {
            if (_vm is null) return;

            double unitSizePx = UnitSizePx(zoom);
            double majorInterval = _vm.MajorTickInterval;
            double minorInterval = _vm.MinorTickInterval;
            double tinyInterval = _vm.TinyTickInterval;

            using var majorPaint = new SKPaint
            { Color = ColorTickMajor, StrokeWidth = 1f, IsStroke = true, IsAntialias = false };
            using var minorPaint = new SKPaint
            { Color = ColorTickMinor, StrokeWidth = 1f, IsStroke = true, IsAntialias = false };
            using var tinyPaint = new SKPaint
            { Color = ColorTickTiny, StrokeWidth = 1f, IsStroke = true, IsAntialias = false };
            using var labelPaint = new SKPaint
            { Color = ColorLabel, IsAntialias = true };
            using var labelNegPaint = new SKPaint
            { Color = ColorLabelNegative, IsAntialias = true };

            using var typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Normal)
                ?? SKTypeface.Default;
            using var font = new SKFont(typeface, 8f);

            double marginLeftUnits = _vm.MmToUnits(_vm.MarginLeftMm);
            double pageWidthUnits = _vm.MmToUnits(_vm.PageWidthMm);

            double startUnit = -marginLeftUnits;               // левый край листа
            double endUnit = pageWidthUnits - marginLeftUnits; // правый край листа

            double step = tinyInterval;
            int stepCount = (int)Math.Ceiling((endUnit - startUnit) / step) + 2;

            for (int i = 0; i <= stepCount; i++)
            {
                double unitValue = startUnit + i * step;
                double xPx = textAreaStartPx + unitValue * unitSizePx;
                double unitFromPageLeft = unitValue + marginLeftUnits;

                if (xPx < 0 || xPx > Bounds.Width) continue;

                bool isMajor = IsMultiple(unitFromPageLeft, majorInterval);
                bool isMinor = !isMajor && IsMultiple(unitFromPageLeft, minorInterval);

                float tickH = isMajor ? (float)MajorTickHeightPx
                            : isMinor ? (float)MinorTickHeightPx
                            : (float)TinyTickHeightPx;

                var paint = isMajor ? majorPaint : isMinor ? minorPaint : tinyPaint;
                canvas.DrawLine((float)xPx, h - tickH, (float)xPx, h, paint);

                if (isMajor && Math.Abs(unitValue) > majorInterval * 0.1)
                {
                    bool isNeg = unitValue < 0;
                    string label = _vm.Units == Models.Settings.RulerUnits.Inches
                        ? Math.Abs(unitValue).ToString("0.##")
                        : ((int)Math.Round(Math.Abs(unitValue) * 10)).ToString();
                    if (isNeg) label = "-" + label;

                    float textW = font.MeasureText(label);
                    var lp = isNeg ? labelNegPaint : labelPaint;
                    canvas.DrawText(label,
                        (float)xPx - textW / 2f, h - tickH - 2f,
                        font, lp);
                }
            }
        }

        // ── Маркеры отступов ──────────────────────────────────────────────

        private void DrawIndentMarkers(
            SKCanvas canvas,
            double textAreaStartPx,
            double textAreaEndPx,
            float h,
            double zoom)
        {
            if (_vm is null) return;

            double unitSizePx = UnitSizePx(zoom);
            float ms = (float)MarkerSizePx;
            float markerY = h - ms;

            var drawOrder = new[]
            {
                RulerIndentMarkerType.LeftIndent,
                RulerIndentMarkerType.RightIndent,
                RulerIndentMarkerType.FirstLineIndent
            };

            foreach (var type in drawOrder)
            {
                var marker = GetIndentMarker(type);
                if (marker is null) continue;

                bool isDragging = _vm.DraggingIndentMarker == type;
                var color = isDragging ? ColorMarkerDragging : ColorMarkerIndent;

                using var fillPaint = new SKPaint { Color = color, IsAntialias = true };
                using var strokePaint = new SKPaint
                {
                    Color = SKColors.White,
                    StrokeWidth = 1f,
                    IsStroke = true,
                    IsAntialias = true
                };

                double xPx;
                if (type == RulerIndentMarkerType.RightIndent)
                {
                    xPx = textAreaEndPx - marker.Position * unitSizePx;
                    DrawTriangleDown(canvas, (float)xPx, markerY, ms, fillPaint, strokePaint);
                }
                else if (type == RulerIndentMarkerType.FirstLineIndent)
                {
                    xPx = textAreaStartPx + marker.Position * unitSizePx;
                    DrawTriangleUp(canvas, (float)xPx, 0, ms, fillPaint, strokePaint);
                }
                else // LeftIndent
                {
                    xPx = textAreaStartPx + marker.Position * unitSizePx;
                    DrawTriangleDown(canvas, (float)xPx, markerY, ms, fillPaint, strokePaint);
                }

                if (isDragging)
                {
                    using var guidePaint = new SKPaint
                    {
                        Color = ColorGuideLine,
                        StrokeWidth = 1f,
                        IsStroke = true,
                        PathEffect = SKPathEffect.CreateDash(new[] { 4f, 4f }, 0)
                    };
                    canvas.DrawLine((float)xPx, 0, (float)xPx, h, guidePaint);
                }
            }
        }

        private RulerIndentMarker? GetIndentMarker(RulerIndentMarkerType type)
        {
            if (_vm is null) return null;
            foreach (var m in _vm.IndentMarkers)
                if (m.Type == type) return m;
            return null;
        }

        private void DrawColumnMarkers(
            SKCanvas canvas,
            double textAreaStartPx,
            float h,
            double zoom)
        {
            if (_vm is null) return;

            double unitSizePx = UnitSizePx(zoom);
            float ms = (float)(MarkerSizePx * 0.75);

            foreach (var marker in _vm.ColumnMarkers)
            {
                double xPx = textAreaStartPx + marker.RightEdge * unitSizePx;
                bool isDragging = _vm.DraggingColumnIndex == marker.ColumnIndex;
                var color = isDragging ? ColorMarkerDragging : ColorMarkerColumn;

                using var linePaint = new SKPaint
                {
                    Color = color,
                    StrokeWidth = isDragging ? 2f : 1.5f,
                    IsStroke = true,
                    IsAntialias = false
                };
                canvas.DrawLine((float)xPx, 0, (float)xPx, h, linePaint);

                using var fillPaint = new SKPaint { Color = color, IsAntialias = true };
                using var strokePaint = new SKPaint
                {
                    Color = SKColors.White,
                    StrokeWidth = 1f,
                    IsStroke = true,
                    IsAntialias = true
                };
                DrawTriangleDown(canvas, (float)xPx, 0, ms, fillPaint, strokePaint);
            }
        }

        // ── Геометрические примитивы ──────────────────────────────────────

        private static void DrawTriangleDown(SKCanvas canvas,
            float cx, float y, float size,
            SKPaint fillPaint, SKPaint strokePaint)
        {
            using var path = new SKPath();
            path.MoveTo(cx - size / 2f, y);
            path.LineTo(cx + size / 2f, y);
            path.LineTo(cx, y + size);
            path.Close();
            canvas.DrawPath(path, fillPaint);
            canvas.DrawPath(path, strokePaint);
        }

        private static void DrawTriangleUp(SKCanvas canvas,
            float cx, float y, float size,
            SKPaint fillPaint, SKPaint strokePaint)
        {
            using var path = new SKPath();
            path.MoveTo(cx - size / 2f, y + size);
            path.LineTo(cx + size / 2f, y + size);
            path.LineTo(cx, y);
            path.Close();
            canvas.DrawPath(path, fillPaint);
            canvas.DrawPath(path, strokePaint);
        }

        // ── Pointer events ────────────────────────────────────────────────

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (_vm is null) return;

            var pos = e.GetPosition(this);
            double zoom = _vm.Zoom;
            double unitSizePx = UnitSizePx(zoom);
            double textAreaStartPx = _vm.PageOffsetXPx + MmToPx(_vm.MarginLeftMm, zoom);
            double textAreaEndPx = _vm.PageOffsetXPx
                + MmToPx(_vm.PageWidthMm, zoom)
                - MmToPx(_vm.MarginRightMm, zoom);

            if (_vm.Mode == RulerMode.Paragraph)
            {
                var hitMarker = HitTestIndentMarkerPriority(
                    pos.X, pos.Y, textAreaStartPx, textAreaEndPx, unitSizePx);

                if (hitMarker.HasValue)
                {
                    _isDragging = true;
                    _vm.BeginIndentDrag(hitMarker.Value);
                    e.Pointer.Capture(this);
                    Cursor = new Cursor(StandardCursorType.SizeWestEast);
                    e.Handled = true;
                    return;
                }
            }
            else
            {
                int hitCol = HitTestColumnMarker(pos.X, textAreaStartPx, unitSizePx);
                if (hitCol >= 0)
                {
                    _isDragging = true;
                    _vm.BeginColumnDrag(hitCol);
                    e.Pointer.Capture(this);
                    Cursor = new Cursor(StandardCursorType.SizeWestEast);
                    e.Handled = true;
                    return;
                }
            }
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (_vm is null) return;

            var pos = e.GetPosition(this);
            double zoom = _vm.Zoom;
            double unitSizePx = UnitSizePx(zoom);
            double textAreaStartPx = _vm.PageOffsetXPx + MmToPx(_vm.MarginLeftMm, zoom);
            double textAreaEndPx = _vm.PageOffsetXPx
                + MmToPx(_vm.PageWidthMm, zoom)
                - MmToPx(_vm.MarginRightMm, zoom);

            if (_isDragging)
            {
                if (_vm.Mode == RulerMode.Paragraph && _vm.DraggingIndentMarker.HasValue)
                {
                    double posUnits = _vm.DraggingIndentMarker == RulerIndentMarkerType.RightIndent
                        ? (textAreaEndPx - pos.X) / unitSizePx
                        : (pos.X - textAreaStartPx) / unitSizePx;

                    _vm.UpdateIndentDragUnclamped(posUnits);
                }
                else if (_vm.Mode == RulerMode.Table && _vm.DraggingColumnIndex >= 0)
                {
                    double posUnits = (pos.X - textAreaStartPx) / unitSizePx;
                    _vm.UpdateColumnDrag(posUnits);
                }

                InvalidateVisual();
                e.Handled = true;
                return;
            }

            if (_vm.Mode == RulerMode.Paragraph)
            {
                var hit = HitTestIndentMarkerPriority(
                    pos.X, pos.Y, textAreaStartPx, textAreaEndPx, unitSizePx);
                Cursor = hit.HasValue
                    ? new Cursor(StandardCursorType.SizeWestEast)
                    : new Cursor(StandardCursorType.Arrow);
            }
            else
            {
                int hitCol = HitTestColumnMarker(pos.X, textAreaStartPx, unitSizePx);
                Cursor = hitCol >= 0
                    ? new Cursor(StandardCursorType.SizeWestEast)
                    : new Cursor(StandardCursorType.Arrow);
            }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (!_isDragging || _vm is null) return;

            _isDragging = false;
            e.Pointer.Capture(null);
            Cursor = new Cursor(StandardCursorType.Arrow);

            if (_vm.Mode == RulerMode.Paragraph)
                _vm.EndIndentDrag();
            else
                _vm.EndColumnDrag();

            InvalidateVisual();
            e.Handled = true;
        }

        // ── HitTest ───────────────────────────────────────────────────────

        private RulerIndentMarkerType? HitTestIndentMarkerPriority(
            double xPx, double yPx,
            double textAreaStartPx,
            double textAreaEndPx,
            double unitSizePx)
        {
            if (_vm is null) return null;

            double r = MarkerHitRadiusPx;
            double h = RulerHeightPx;
            bool isUpperHalf = yPx < h / 2.0;

            double xLeft = textAreaStartPx + GetMarkerPosition(RulerIndentMarkerType.LeftIndent) * unitSizePx;
            double xFirst = textAreaStartPx + GetMarkerPosition(RulerIndentMarkerType.FirstLineIndent) * unitSizePx;
            double xRight = textAreaEndPx - GetMarkerPosition(RulerIndentMarkerType.RightIndent) * unitSizePx;

            bool hitLeft = Math.Abs(xPx - xLeft) <= r;
            bool hitFirst = Math.Abs(xPx - xFirst) <= r;
            bool hitRight = Math.Abs(xPx - xRight) <= r;

            if (isUpperHalf)
            {
                if (hitFirst) return RulerIndentMarkerType.FirstLineIndent;
                if (hitLeft) return RulerIndentMarkerType.LeftIndent;
                if (hitRight) return RulerIndentMarkerType.RightIndent;
            }
            else
            {
                if (hitLeft) return RulerIndentMarkerType.LeftIndent;
                if (hitRight) return RulerIndentMarkerType.RightIndent;
                if (hitFirst) return RulerIndentMarkerType.FirstLineIndent;
            }

            return null;
        }

        private double GetMarkerPosition(RulerIndentMarkerType type)
        {
            if (_vm is null) return 0;
            foreach (var m in _vm.IndentMarkers)
                if (m.Type == type) return m.Position;
            return 0;
        }

        private int HitTestColumnMarker(
            double xPx,
            double textAreaStartPx,
            double unitSizePx)
        {
            if (_vm is null) return -1;
            double r = MarkerHitRadiusPx;
            foreach (var marker in _vm.ColumnMarkers)
            {
                double markerX = textAreaStartPx + marker.RightEdge * unitSizePx;
                if (Math.Abs(xPx - markerX) <= r)
                    return marker.ColumnIndex;
            }
            return -1;
        }

        // ── Вспомогательные ──────────────────────────────────────────────

        private double UnitSizePx(double zoom)
        {
            if (_vm is null) return 96.0 * zoom;
            double unitMm = _vm.Units == Models.Settings.RulerUnits.Inches ? 25.4 : 10.0;
            return unitMm * (96.0 / 25.4) * zoom;
        }

        private static double MmToPx(double mm, double zoom)
            => mm * (96.0 / 25.4) * zoom;

        private static bool IsMultiple(double value, double step)
        {
            if (step <= 0) return false;
            double remainder = Math.Abs(value % step);
            return remainder < step * 0.01 || remainder > step * 0.99;
        }

        // ── ICustomDrawOperation ──────────────────────────────────────────

        private sealed class RulerSKDrawOperation : ICustomDrawOperation
        {
            private readonly HorizontalRulerControl _ruler;
            public Rect Bounds { get; }

            public RulerSKDrawOperation(HorizontalRulerControl ruler, Rect bounds)
            {
                _ruler = ruler;
                Bounds = bounds;
            }

            public void Dispose() { }
            public bool Equals(ICustomDrawOperation? other) => false;
            public bool HitTest(Point p) => true;

            public void Render(ImmediateDrawingContext context)
            {
                var feature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature))
                    as ISkiaSharpApiLeaseFeature;
                if (feature is null) return;
                using var lease = feature.Lease();
                _ruler.RenderWithSKCanvas(lease.SkCanvas);
            }
        }
    }
}