using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using Writersword.Modules.TextEditor.ViewModels.Components;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Горизонтальная линейка редактора.
    /// В режиме таблицы отображает маркеры колонок, левый край таблицы (drag сдвигает всю таблицу)
    /// и маркеры отступов абзаца в пределах активной ячейки.
    /// </summary>
    public sealed class HorizontalRulerControl : Control
    {
        private const double RulerHeightPx = 24.0;
        private const double MarkerSizePx = 8.0;
        private const double MarkerHitRadiusPx = 7.0;
        private const double MajorTickHeightPx = 10.0;
        private const double MinorTickHeightPx = 6.0;
        private const double TinyTickHeightPx = 3.0;

        private static readonly SKColor ColorBackground = new(0xF0, 0xF0, 0xF0);
        private static readonly SKColor ColorOutsidePage = new(0xD0, 0xD0, 0xD0);
        private static readonly SKColor ColorTickMajor = new(0x60, 0x60, 0x60);
        private static readonly SKColor ColorTickMinor = new(0x99, 0x99, 0x99);
        private static readonly SKColor ColorTickTiny = new(0xBB, 0xBB, 0xBB);
        private static readonly SKColor ColorLabel = new(0x44, 0x44, 0x44);
        private static readonly SKColor ColorLabelNegative = new(0x99, 0x44, 0x44);
        private static readonly SKColor ColorBorder = new(0xCC, 0xCC, 0xCC);
        private static readonly SKColor ColorMarkerIndent = new(0x33, 0x66, 0xCC);
        private static readonly SKColor ColorMarkerList = new(0x8A, 0x3F, 0xD0); // фиолетовый — маркер края списка
        private static readonly SKColor ColorMarkerColumn = new(0x22, 0x99, 0x55);
        private static readonly SKColor ColorMarkerLeftEdge = new(0x22, 0x99, 0x55); // такой же зелёный — перетаскивает всю таблицу
        private static readonly SKColor ColorMarkerDragging = new(0xFF, 0x66, 0x00);
        private static readonly SKColor ColorGuideLine = new(0xFF, 0x66, 0x00, 0xAA);
        private static readonly SKColor ColorMarginZone = new(0xD8, 0xD8, 0xD8);
        private static readonly SKColor ColorMarginHandle = new(0x88, 0x88, 0x88);

        private RulerViewModel? _vm;
        private bool _isDragging;
        private bool _isDraggingMargin;
        private bool _draggingLeftMargin;
        private bool _isDraggingIndentInTable; // true = drag indent маркера в режиме таблицы

        public HorizontalRulerControl()
        {
            Height = RulerHeightPx;
            Cursor = new Cursor(StandardCursorType.Arrow);
        }

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

            using var bgPaint = new SKPaint { Color = ColorBackground };
            canvas.DrawRect(0, 0, w, h, bgPaint);

            double pageOffsetXPx = _vm.PageOffsetXPx;
            double pageWidthPx = MmToPx(_vm.PageWidthMm, zoom);
            double marginLeftPx = MmToPx(_vm.MarginLeftMm, zoom);
            double marginRightPx = MmToPx(_vm.MarginRightMm, zoom);
            double textAreaStartPx = pageOffsetXPx + marginLeftPx;
            double textAreaEndPx = pageOffsetXPx + pageWidthPx - marginRightPx;

            // Область вне страницы.
            using var outerPaint = new SKPaint { Color = ColorOutsidePage };
            if (pageOffsetXPx > 0)
                canvas.DrawRect(0, 0, (float)pageOffsetXPx, h, outerPaint);
            double pageRightPx = pageOffsetXPx + pageWidthPx;
            if (pageRightPx < w)
                canvas.DrawRect((float)pageRightPx, 0, w - (float)pageRightPx, h, outerPaint);

            // Серые зоны полей.
            using var marginPaint = new SKPaint { Color = ColorMarginZone };
            canvas.DrawRect((float)pageOffsetXPx, 0,
                (float)(textAreaStartPx - pageOffsetXPx), h, marginPaint);
            canvas.DrawRect((float)textAreaEndPx, 0,
                (float)(pageRightPx - textAreaEndPx), h, marginPaint);

            // Граница поля и текста.
            using var handlePaint = new SKPaint
            { Color = ColorMarginHandle, StrokeWidth = 1f, IsStroke = true };
            canvas.DrawLine((float)textAreaStartPx, 0, (float)textAreaStartPx, h, handlePaint);
            canvas.DrawLine((float)textAreaEndPx, 0, (float)textAreaEndPx, h, handlePaint);

            DrawScale(canvas, pageOffsetXPx, pageWidthPx,
                textAreaStartPx, textAreaEndPx, h, zoom);

            if (_vm.Mode == RulerMode.Paragraph)
            {
                DrawIndentMarkers(canvas, textAreaStartPx, textAreaEndPx, h, zoom);
            }
            else
            {
                double unitSizePx = UnitSizePx(zoom);
                double cellStartPx = textAreaStartPx + _vm.ActiveCellLeftUnits * unitSizePx;
                double cellEndPx = textAreaStartPx + _vm.ActiveCellRightUnits * unitSizePx;
                DrawColumnMarkers(canvas, textAreaStartPx, cellStartPx, cellEndPx, h, zoom);
            }

            using var borderPaint = new SKPaint
            { Color = ColorBorder, StrokeWidth = 1f, IsStroke = true };
            canvas.DrawLine(0, h - 0.5f, w, h - 0.5f, borderPaint);
        }

        // ── Шкала ─────────────────────────────────────────────────────────

        private void DrawScale(
            SKCanvas canvas,
            double pageOffsetXPx, double pageWidthPx,
            double textAreaStartPx, double textAreaEndPx,
            float h, double zoom)
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
            using var labelPaint = new SKPaint { Color = ColorLabel, IsAntialias = true };
            using var labelNegPaint = new SKPaint { Color = ColorLabelNegative, IsAntialias = true };

            using var typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Normal)
                ?? SKTypeface.Default;
            using var font = new SKFont(typeface, 8f);

            double pageStartXPx = pageOffsetXPx;
            double pageEndXPx = pageOffsetXPx + pageWidthPx;

            int stepsLeft = (int)Math.Ceiling((textAreaStartPx - pageStartXPx) / (unitSizePx * tinyInterval)) + 2;
            int stepsRight = (int)Math.Ceiling((pageEndXPx - textAreaStartPx) / (unitSizePx * tinyInterval)) + 2;

            int tinyPerMajor = (int)Math.Round(majorInterval / tinyInterval);
            int tinyPerMinor = (int)Math.Round(minorInterval / tinyInterval);

            for (int i = -stepsLeft; i <= stepsRight; i++)
            {
                double unitValue = i * tinyInterval;
                double xPx = textAreaStartPx + unitValue * unitSizePx;

                if (xPx < 0 || xPx > Bounds.Width) continue;

                bool isMajor = (i % tinyPerMajor) == 0;
                bool isMinor = !isMajor && (i % tinyPerMinor) == 0;

                float tickH = isMajor ? (float)MajorTickHeightPx
                            : isMinor ? (float)MinorTickHeightPx
                            : (float)TinyTickHeightPx;

                var paint = isMajor ? majorPaint : isMinor ? minorPaint : tinyPaint;
                canvas.DrawLine((float)xPx, h - tickH, (float)xPx, h, paint);

                if (isMajor)
                {
                    if (Math.Abs(unitValue) < majorInterval * 0.1) continue;

                    bool isNeg = unitValue < 0;
                    string label = _vm.Units == Models.Settings.RulerUnits.Inches
                        ? Math.Abs(unitValue).ToString("0.##")
                        : ((int)Math.Round(Math.Abs(unitValue) * 10)).ToString();
                    if (isNeg) label = "-" + label;

                    float textW = font.MeasureText(label);
                    canvas.DrawText(label,
                        (float)xPx - textW / 2f, h - (float)MajorTickHeightPx - 2f,
                        font, isNeg ? labelNegPaint : labelPaint);
                }
            }
        }

        // ── Маркеры отступов ──────────────────────────────────────────────

        private void DrawIndentMarkers(
            SKCanvas canvas,
            double textAreaStartPx, double textAreaEndPx,
            float h, double zoom)
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

            // Дополнительная стрелка «край списка» (hanging): показывается только для абзацев-списков.
            // Рисуется верхним треугольником фиолетового цвета на позиции маркера от левого поля.
            if (_vm.ShowListMarker)
            {
                var listMarker = GetIndentMarker(RulerIndentMarkerType.ListMarker);
                if (listMarker is not null)
                {
                    bool isDragging = _vm.DraggingIndentMarker == RulerIndentMarkerType.ListMarker;
                    var color = isDragging ? ColorMarkerDragging : ColorMarkerList;
                    using var fillPaint = new SKPaint { Color = color, IsAntialias = true };
                    using var strokePaint = new SKPaint
                    {
                        Color = SKColors.White,
                        StrokeWidth = 1f,
                        IsStroke = true,
                        IsAntialias = true
                    };
                    double xPx = textAreaStartPx + listMarker.Position * unitSizePx;
                    DrawTriangleUp(canvas, (float)xPx, 0, ms, fillPaint, strokePaint);

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
        }

        private RulerIndentMarker? GetIndentMarker(RulerIndentMarkerType type)
        {
            if (_vm is null) return null;
            foreach (var m in _vm.IndentMarkers)
                if (m.Type == type) return m;
            return null;
        }

        // ── Маркеры колонок (режим таблицы) ──────────────────────────────

        private void DrawColumnMarkers(
            SKCanvas canvas,
            double textAreaStartPx,
            double cellStartPx, double cellEndPx,
            float h, double zoom)
        {
            if (_vm is null) return;

            double unitSizePx = UnitSizePx(zoom);

            // Снимаем снимок списка перед итерацией — ColumnMarkers может изменяться
            // из UI-потока (drag, UpdateTableColumns) пока render-поток рисует.
            var markers = _vm.ColumnMarkers.ToList();
            int draggingIdx = _vm.DraggingColumnIndex;

            foreach (var marker in markers)
            {
                double xPx = textAreaStartPx + marker.RightEdge * unitSizePx;
                bool isLeftEdge = marker.ColumnIndex < 0;

                bool isDragging = draggingIdx >= 0
                    && draggingIdx < markers.Count
                    && markers[draggingIdx].ColumnIndex == marker.ColumnIndex;

                var color = isDragging ? ColorMarkerDragging
                          : isLeftEdge ? ColorMarkerLeftEdge
                          : ColorMarkerColumn;
                float strokeW = isDragging ? 2f : 1f;

                bool skipLine = isLeftEdge && marker.RightEdge < _vm.MmToUnits(0.5);

                // Линия на всю высоту (визуально)
                if (!skipLine)
                {
                    using var linePaint = new SKPaint
                    {
                        Color = color,
                        StrokeWidth = strokeW,
                        IsStroke = true,
                        IsAntialias = false
                    };
                    canvas.DrawLine((float)xPx, 0, (float)xPx, h, linePaint);
                }

                // Треугольник-стрелочка вверху — только она имеет коллайдер
                float triSize = (float)(MarkerSizePx * 0.7);
                using var fillPaint = new SKPaint { Color = color, IsAntialias = true };
                using var strokePaint2 = new SKPaint
                { Color = SKColors.White, StrokeWidth = 1f, IsStroke = true, IsAntialias = true };
                DrawTriangleDown(canvas, (float)xPx, 0, triSize, fillPaint, strokePaint2);

                if (isDragging)
                {
                    using var guidePaint = new SKPaint
                    {
                        Color = ColorGuideLine,
                        StrokeWidth = 1f,
                        IsStroke = true,
                        PathEffect = SKPathEffect.CreateDash(new[] { 4f, 3f }, 0)
                    };
                    canvas.DrawLine((float)xPx, 0, (float)xPx, h, guidePaint);
                }
            }

            // Маркеры отступа абзаца внутри активной ячейки.
            DrawIndentMarkers(canvas, cellStartPx, cellEndPx, h, zoom);
        }

        // ── Pointer events ────────────────────────────────────────────────

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (_vm is null) return;

            // Режим сравнения: линейка только отображает — никакие drag
            // (отступы, колонки, поля страницы) не начинаются.
            if (_vm.IsReadOnly) return;

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
                    _isDraggingIndentInTable = false;
                    _vm.BeginIndentDrag(hitMarker.Value);
                    e.Pointer.Capture(this);
                    Cursor = new Cursor(StandardCursorType.SizeWestEast);
                    e.Handled = true;
                    return;
                }
            }
            else // Table mode
            {
                double cellStartPx = textAreaStartPx + _vm.ActiveCellLeftUnits * unitSizePx;
                double cellEndPx = textAreaStartPx + _vm.ActiveCellRightUnits * unitSizePx;

                // Сначала проверяем маркеры отступа параграфа ячейки.
                var hitMarker = HitTestIndentMarkerPriority(
                    pos.X, pos.Y, cellStartPx, cellEndPx, unitSizePx);

                if (hitMarker.HasValue)
                {
                    _isDragging = true;
                    _isDraggingIndentInTable = true;
                    _vm.BeginIndentDrag(hitMarker.Value);
                    e.Pointer.Capture(this);
                    Cursor = new Cursor(StandardCursorType.SizeWestEast);
                    e.Handled = true;
                    return;
                }

                // Маркеры колонок — коллайдер только на треугольнике (верхние MarkerSizePx px).
                int hitCol = HitTestColumnMarker(pos.X, pos.Y, textAreaStartPx, unitSizePx);
                if (hitCol >= 0)
                {
                    _isDragging = true;
                    _isDraggingIndentInTable = false;
                    _vm.BeginColumnDrag(hitCol);
                    e.Pointer.Capture(this);
                    Cursor = new Cursor(StandardCursorType.SizeWestEast);
                    e.Handled = true;
                    return;
                }
            }

            // Drag границы поля.
            const double MarginHitPx = 5.0;
            if (Math.Abs(pos.X - textAreaStartPx) <= MarginHitPx)
            {
                _isDraggingMargin = true; _draggingLeftMargin = true;
                _vm.BeginMarginDrag();
                e.Pointer.Capture(this);
                Cursor = new Cursor(StandardCursorType.SizeWestEast);
                e.Handled = true;
            }
            else if (Math.Abs(pos.X - textAreaEndPx) <= MarginHitPx)
            {
                _isDraggingMargin = true; _draggingLeftMargin = false;
                _vm.BeginMarginDrag();
                e.Pointer.Capture(this);
                Cursor = new Cursor(StandardCursorType.SizeWestEast);
                e.Handled = true;
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

            if (_isDraggingMargin)
            {
                double pageOffsetXPx2 = _vm.PageOffsetXPx;
                double pageWidthPx2 = MmToPx(_vm.PageWidthMm, zoom);
                double clampedX = Math.Max(pageOffsetXPx2,
                    Math.Min(pos.X, pageOffsetXPx2 + pageWidthPx2));

                if (_draggingLeftMargin)
                {
                    double newMarginMm = PxToMm(clampedX - pageOffsetXPx2, zoom);
                    if (_vm.IsSnapEnabled)
                    { double s = _vm.UnitsToMm(_vm.SnapStep); newMarginMm = Math.Round(newMarginMm / s) * s; }
                    newMarginMm = Math.Max(0, Math.Min(newMarginMm, _vm.PageWidthMm - _vm.MarginRightMm - 5));
                    _vm.MarginLeftMm = newMarginMm;
                }
                else
                {
                    double newMarginMm = PxToMm(pageOffsetXPx2 + pageWidthPx2 - clampedX, zoom);
                    if (_vm.IsSnapEnabled)
                    { double s = _vm.UnitsToMm(_vm.SnapStep); newMarginMm = Math.Round(newMarginMm / s) * s; }
                    newMarginMm = Math.Max(0, Math.Min(newMarginMm, _vm.PageWidthMm - _vm.MarginLeftMm - 5));
                    _vm.MarginRightMm = newMarginMm;
                }

                _vm.NotifyMarginChanged();
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            if (_isDragging)
            {
                if (_isDraggingIndentInTable && _vm.DraggingIndentMarker.HasValue)
                {
                    // Drag отступа внутри ячейки — позиция относительно левого края ячейки.
                    double cellStartPx = textAreaStartPx + _vm.ActiveCellLeftUnits * unitSizePx;
                    double cellEndPx = textAreaStartPx + _vm.ActiveCellRightUnits * unitSizePx;
                    double clampedX = Math.Max(cellStartPx, Math.Min(pos.X, cellEndPx));

                    double posUnits;
                    if (_vm.DraggingIndentMarker == RulerIndentMarkerType.RightIndent)
                        posUnits = (cellEndPx - clampedX) / unitSizePx;
                    else
                        posUnits = (clampedX - cellStartPx) / unitSizePx;

                    _vm.UpdateTableIndentDragUnclamped(posUnits);
                }
                else if (!_isDraggingIndentInTable && _vm.Mode == RulerMode.Paragraph
                    && _vm.DraggingIndentMarker.HasValue)
                {
                    double pageStartPx = _vm.PageOffsetXPx;
                    double pageEndPx = _vm.PageOffsetXPx + MmToPx(_vm.PageWidthMm, zoom);

                    double posUnits;
                    if (_vm.DraggingIndentMarker == RulerIndentMarkerType.RightIndent)
                    {
                        double pageEndPx2 = _vm.PageOffsetXPx + MmToPx(_vm.PageWidthMm, zoom);
                        double clampedX = Math.Max(_vm.PageOffsetXPx, Math.Min(pos.X, pageEndPx2));
                        posUnits = (textAreaEndPx - clampedX) / unitSizePx;
                    }
                    else
                    {
                        double clampedX = Math.Max(pageStartPx, Math.Min(pos.X, pageEndPx));
                        posUnits = (clampedX - textAreaStartPx) / unitSizePx;
                        double minUnits = -(textAreaStartPx - pageStartPx) / unitSizePx;
                        posUnits = Math.Max(posUnits, minUnits);
                    }

                    _vm.UpdateIndentDragUnclamped(posUnits);
                }
                else if (!_isDraggingIndentInTable && _vm.Mode == RulerMode.Table
                    && _vm.DraggingColumnIndex >= 0)
                {
                    double posUnits = (pos.X - textAreaStartPx) / unitSizePx;
                    _vm.UpdateColumnDrag(posUnits);
                }

                InvalidateVisual();
                e.Handled = true;
                return;
            }

            // Курсор при наведении.
            UpdateHoverCursor(pos.X, pos.Y, textAreaStartPx, textAreaEndPx, unitSizePx);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);

            if (_isDraggingMargin)
            {
                _isDraggingMargin = false;
                e.Pointer.Capture(null);
                Cursor = new Cursor(StandardCursorType.Arrow);
                _vm?.CommitMarginChange();
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            if (!_isDragging || _vm is null) return;

            _isDragging = false;
            bool wasIndentInTable = _isDraggingIndentInTable;
            _isDraggingIndentInTable = false;
            e.Pointer.Capture(null);
            Cursor = new Cursor(StandardCursorType.Arrow);

            if (wasIndentInTable || (_vm.Mode == RulerMode.Paragraph && _vm.DraggingIndentMarker.HasValue))
                _vm.EndIndentDrag();
            else if (_vm.Mode == RulerMode.Table && !wasIndentInTable)
                _vm.EndColumnDrag();

            InvalidateVisual();
            e.Handled = true;
        }

        // ── HitTest ───────────────────────────────────────────────────────

        private RulerIndentMarkerType? HitTestIndentMarkerPriority(
            double xPx, double yPx,
            double textAreaStartPx, double textAreaEndPx,
            double unitSizePx)
        {
            if (_vm is null) return null;

            double r = MarkerHitRadiusPx;
            double h = RulerHeightPx;

            double xLeft = textAreaStartPx + GetMarkerPosition(RulerIndentMarkerType.LeftIndent) * unitSizePx;
            double xFirst = textAreaStartPx + GetMarkerPosition(RulerIndentMarkerType.FirstLineIndent) * unitSizePx;
            double xRight = textAreaEndPx - GetMarkerPosition(RulerIndentMarkerType.RightIndent) * unitSizePx;
            double xList = textAreaStartPx + GetMarkerPosition(RulerIndentMarkerType.ListMarker) * unitSizePx;

            bool hitLeft = Math.Abs(xPx - xLeft) <= r;
            bool hitFirst = Math.Abs(xPx - xFirst) <= r;
            bool hitRight = Math.Abs(xPx - xRight) <= r;
            bool hitList = _vm.ShowListMarker && Math.Abs(xPx - xList) <= r;

            // Каждый треугольник кликабелен ТОЛЬКО в своей Y-зоне:
            //   FirstLineIndent / ListMarker → верхние MarkerSizePx пикселей (DrawTriangleUp вверху)
            //   LeftIndent      → нижние MarkerSizePx пикселей  (DrawTriangleDown внизу)
            //   RightIndent     → нижние MarkerSizePx пикселей
            // Между треугольниками (средняя зона) — ни один из них не перехватывает клик.
            bool inTopZone = yPx <= MarkerSizePx;
            bool inBottomZone = yPx >= h - MarkerSizePx;

            // Маркер списка имеет приоритет над отступом первой строки в верхней зоне.
            if (inTopZone && hitList) return RulerIndentMarkerType.ListMarker;
            if (inTopZone && hitFirst) return RulerIndentMarkerType.FirstLineIndent;
            if (inBottomZone)
            {
                if (hitLeft) return RulerIndentMarkerType.LeftIndent;
                if (hitRight) return RulerIndentMarkerType.RightIndent;
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

        private int HitTestColumnMarker(double xPx, double yPx, double textAreaStartPx, double unitSizePx)
        {
            if (_vm is null) return -1;
            double r = MarkerHitRadiusPx;
            int bestIdx = -1;
            double bestD = double.MaxValue;
            for (int i = 0; i < _vm.ColumnMarkers.Count; i++)
            {
                double markerX = textAreaStartPx + _vm.ColumnMarkers[i].RightEdge * unitSizePx;
                double d = Math.Abs(xPx - markerX);
                if (d <= r && d < bestD) { bestD = d; bestIdx = i; }
            }
            return bestIdx;
        }

        private void UpdateHoverCursor(
            double xPx, double yPx,
            double textAreaStartPx, double textAreaEndPx,
            double unitSizePx)
        {
            if (_vm is null) return;

            // Режим сравнения: перетаскивание запрещено — курсор ресайза не показываем.
            if (_vm.IsReadOnly)
            {
                Cursor = new Cursor(StandardCursorType.Arrow);
                return;
            }


            if (_vm.Mode == RulerMode.Paragraph)
            {
                var hit = HitTestIndentMarkerPriority(
                    xPx, yPx, textAreaStartPx, textAreaEndPx, unitSizePx);
                if (hit.HasValue) { Cursor = new Cursor(StandardCursorType.SizeWestEast); return; }
            }
            else
            {
                double cellStartPx = textAreaStartPx + _vm.ActiveCellLeftUnits * unitSizePx;
                double cellEndPx = textAreaStartPx + _vm.ActiveCellRightUnits * unitSizePx;

                // Треугольники отступа имеют приоритет в своих зонах
                var hit = HitTestIndentMarkerPriority(xPx, yPx, cellStartPx, cellEndPx, unitSizePx);
                if (hit.HasValue) { Cursor = new Cursor(StandardCursorType.SizeWestEast); return; }

                // Маркеры колонок — HitTestColumnMarker сам ограничивает по Y
                int hitCol = HitTestColumnMarker(xPx, yPx, textAreaStartPx, unitSizePx);
                if (hitCol >= 0) { Cursor = new Cursor(StandardCursorType.SizeWestEast); return; }
            }

            const double MarginHoverPx = 5.0;
            if (Math.Abs(xPx - textAreaStartPx) <= MarginHoverPx
                || Math.Abs(xPx - textAreaEndPx) <= MarginHoverPx)
            { Cursor = new Cursor(StandardCursorType.SizeWestEast); return; }

            Cursor = new Cursor(StandardCursorType.Arrow);
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

        // ── Вспомогательные ───────────────────────────────────────────────

        private double UnitSizePx(double zoom)
        {
            if (_vm is null) return 96.0 * zoom;
            double unitMm = _vm.Units == Models.Settings.RulerUnits.Inches ? 25.4 : 10.0;
            return unitMm * (96.0 / 25.4) * zoom;
        }

        private static double MmToPx(double mm, double zoom)
            => mm * (96.0 / 25.4) * zoom;

        private static double PxToMm(double px, double zoom)
            => px / (96.0 / 25.4) / zoom;

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