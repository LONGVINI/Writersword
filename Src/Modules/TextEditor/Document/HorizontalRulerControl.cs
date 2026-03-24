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
        private static readonly SKColor ColorMarginZone = new(0xD8, 0xD8, 0xD8);
        private static readonly SKColor ColorMarginHandle = new(0x88, 0x88, 0x88);
        private static readonly SKColor ColorZeroLabel = new(0x33, 0x66, 0xCC);

        // ── Кнопка привязки (магнитик) ───────────────────────────────────
        // Кнопка занимает весь левый угол линейки — квадрат RulerHeightPx × RulerHeightPx
        private const double SnapBtnSize = 24.0;  // = RulerHeightPx, квадрат на всю высоту
        private const double SnapBtnRight = 0.0;   // без отступа — прямо в угол
        private static readonly SKColor ColorSnapOn = new(0x33, 0x66, 0xCC);
        private static readonly SKColor ColorSnapOff = new(0xBB, 0xBB, 0xBB);

        // ── Состояние ─────────────────────────────────────────────────────
        private RulerViewModel? _vm;
        private bool _isDragging;
        private bool _isDraggingMargin;
        private bool _draggingLeftMargin; // true = левое поле, false = правое

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

            using var bgPaint = new SKPaint { Color = ColorBackground };
            canvas.DrawRect(0, 0, w, h, bgPaint);

            double pageOffsetXPx = _vm.PageOffsetXPx;
            double pageWidthPx = MmToPx(_vm.PageWidthMm, zoom);
            double marginLeftPx = MmToPx(_vm.MarginLeftMm, zoom);
            double marginRightPx = MmToPx(_vm.MarginRightMm, zoom);
            double textAreaStartPx = pageOffsetXPx + marginLeftPx;
            double textAreaEndPx = pageOffsetXPx + pageWidthPx - marginRightPx;

            // Область вне страницы — чуть темнее.
            using var outerPaint = new SKPaint { Color = ColorOutsidePage };
            if (pageOffsetXPx > 0)
                canvas.DrawRect(0, 0, (float)pageOffsetXPx, h, outerPaint);
            double pageRightPx = pageOffsetXPx + pageWidthPx;
            if (pageRightPx < w)
                canvas.DrawRect((float)pageRightPx, 0,
                    w - (float)pageRightPx, h, outerPaint);

            // Серые зоны полей внутри страницы (как в Word).
            using var marginPaint = new SKPaint { Color = ColorMarginZone };
            // Левое поле: от левого края листа до начала текста.
            canvas.DrawRect((float)pageOffsetXPx, 0,
                (float)(textAreaStartPx - pageOffsetXPx), h, marginPaint);
            // Правое поле: от конца текста до правого края листа.
            canvas.DrawRect((float)textAreaEndPx, 0,
                (float)(pageRightPx - textAreaEndPx), h, marginPaint);

            // Граница между полем и текстом (тонкая тёмная линия).
            using var handlePaint = new SKPaint
            { Color = ColorMarginHandle, StrokeWidth = 1f, IsStroke = true };
            canvas.DrawLine((float)textAreaStartPx, 0, (float)textAreaStartPx, h, handlePaint);
            canvas.DrawLine((float)textAreaEndPx, 0, (float)textAreaEndPx, h, handlePaint);

            DrawScale(canvas, pageOffsetXPx, pageWidthPx,
                textAreaStartPx, textAreaEndPx, h, zoom);

            if (_vm.Mode == RulerMode.Paragraph)
                DrawIndentMarkers(canvas, textAreaStartPx, textAreaEndPx, h, zoom);
            else
                DrawColumnMarkers(canvas, textAreaStartPx, h, zoom);

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

            // Шкала строится ОТ textAreaStartPx (= ноль линейки).
            // Тики идут с шагом unitSizePx влево и вправо от этой точки.
            // При сдвиге левого поля textAreaStartPx меняется → вся шкала
            // физически сдвигается вместе с ней (как в Word).
            //
            // Диапазон: от левого края страницы до правого края страницы.
            double pageStartXPx = pageOffsetXPx;
            double pageEndXPx = pageOffsetXPx + pageWidthPx;

            // Сколько шагов влево от textAreaStartPx до левого края страницы?
            int stepsLeft = (int)Math.Ceiling((textAreaStartPx - pageStartXPx) / (unitSizePx * tinyInterval)) + 2;
            int stepsRight = (int)Math.Ceiling((pageEndXPx - textAreaStartPx) / (unitSizePx * tinyInterval)) + 2;

            for (int i = -stepsLeft; i <= stepsRight; i++)
            {
                double unitValue = i * tinyInterval;
                double xPx = textAreaStartPx + unitValue * unitSizePx;

                if (xPx < 0 || xPx > Bounds.Width) continue;

                // Классифицируем по индексу i — чисто целочисленно, без float-ошибок.
                int tinyPerMajor = (int)Math.Round(majorInterval / tinyInterval);
                int tinyPerMinor = (int)Math.Round(minorInterval / tinyInterval);
                bool isMajor = (i % tinyPerMajor) == 0;
                bool isMinor = !isMajor && (i % tinyPerMinor) == 0;

                float tickH = isMajor
                    ? (float)MajorTickHeightPx
                    : isMinor
                        ? (float)MinorTickHeightPx
                        : (float)TinyTickHeightPx;

                var paint = isMajor ? majorPaint : isMinor ? minorPaint : tinyPaint;
                canvas.DrawLine((float)xPx, h - tickH, (float)xPx, h, paint);

                if (isMajor)
                {
                    if (Math.Abs(unitValue) < majorInterval * 0.1) continue; // не рисуем "0"

                    string label;
                    SKPaint lPaint;
                    {
                        bool isNeg = unitValue < 0;
                        label = _vm.Units == Models.Settings.RulerUnits.Inches
                            ? Math.Abs(unitValue).ToString("0.##")
                            : ((int)Math.Round(Math.Abs(unitValue) * 10)).ToString();
                        if (isNeg) label = "-" + label;
                        lPaint = isNeg ? labelNegPaint : labelPaint;
                    }

                    float textW = font.MeasureText(label);
                    canvas.DrawText(label,
                        (float)xPx - textW / 2f, h - (float)MajorTickHeightPx - 2f,
                        font, lPaint);
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

            // Порядок рисования: сначала нижние (LeftIndent, RightIndent),
            // потом верхний (FirstLineIndent) — чтобы верхний был поверх.
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

        // ── Кнопка привязки ──────────────────────────────────────────────

        /// <summary>
        /// Рисует кнопку-магнитик в правом верхнем углу линейки.
        /// Нажатие переключает IsSnapEnabled в RulerViewModel.
        /// </summary>
        private void DrawSnapButton(SKCanvas canvas, float rulerW, float rulerH)
        {
            if (_vm is null) return;

            // Кнопка — квадрат в самом левом углу линейки (0,0).
            float bx = 0f;
            float by = 0f;
            float bs = rulerH;  // полная высота линейки

            // Фон кнопки — светло-серый или синий.
            bool snapOn = _vm.IsSnapEnabled;
            using var bgPaint = new SKPaint
            {
                Color = snapOn ? ColorSnapOn : new SKColor(0xE0, 0xE0, 0xE0),
                IsAntialias = false
            };
            canvas.DrawRect(bx, by, bs, bs, bgPaint);

            // Правая граница кнопки.
            using var sepPaint = new SKPaint { Color = ColorBorder, StrokeWidth = 1f, IsStroke = true };
            canvas.DrawLine(bs, 0, bs, rulerH, sepPaint);

            // Иконка магнита: подкова «U» повёрнутая вниз + ножки.
            float cx = bx + bs / 2f;
            float cy = by + bs * 0.42f;
            float r = bs * 0.30f;

            using var iconPaint = new SKPaint
            {
                Color = snapOn ? SKColors.White : new SKColor(0x66, 0x66, 0x66),
                StrokeWidth = bs * 0.12f,
                IsStroke = true,
                IsAntialias = true,
                StrokeCap = SKStrokeCap.Round
            };

            // Дуга подковы — открыта вниз (от 0° до 180° по часовой = верхняя полуокружность).
            using var arcPath = new SKPath();
            arcPath.AddArc(new SKRect(cx - r, cy - r, cx + r, cy + r), 0, 180);
            canvas.DrawPath(arcPath, iconPaint);

            // Ножки идут вниз от краёв дуги.
            float legLen = r * 0.75f;
            canvas.DrawLine(cx - r, cy, cx - r, cy + legLen, iconPaint);
            canvas.DrawLine(cx + r, cy, cx + r, cy + legLen, iconPaint);

            // Кончики — горизонтальные полоски (полюса магнита).
            using var tipPaint = new SKPaint
            {
                Color = snapOn ? new SKColor(0xFF, 0xCC, 0x00) : new SKColor(0x99, 0x99, 0x99),
                StrokeWidth = bs * 0.14f,
                IsStroke = true,
                IsAntialias = true,
                StrokeCap = SKStrokeCap.Round
            };
            float tipW = r * 0.55f;
            canvas.DrawLine(cx - r - tipW * 0.5f, cy + legLen,
                            cx - r + tipW * 0.5f, cy + legLen, tipPaint);
            canvas.DrawLine(cx + r - tipW * 0.5f, cy + legLen,
                            cx + r + tipW * 0.5f, cy + legLen, tipPaint);
        }

        /// <summary>
        /// Возвращает true если точка попадает в область кнопки-магнитика.
        /// </summary>
        private bool HitSnapButton(double x, double y, float rulerW, float rulerH)
        {
            // Кнопка = квадрат rulerH × rulerH в левом углу.
            return x >= 0 && x <= rulerH && y >= 0 && y <= rulerH;
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
                // Hit-тест с приоритетом по Y:
                // верхняя половина → сначала FirstLineIndent,
                // нижняя половина → сначала LeftIndent/RightIndent.
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

            // Drag границы поля: клик в ±4px от textAreaStart или textAreaEnd.
            const double MarginHitPx = 5.0;
            if (Math.Abs(pos.X - textAreaStartPx) <= MarginHitPx)
            {
                _isDraggingMargin = true;
                _draggingLeftMargin = true;
                e.Pointer.Capture(this);
                Cursor = new Cursor(StandardCursorType.SizeWestEast);
                e.Handled = true;
            }
            else if (Math.Abs(pos.X - textAreaEndPx) <= MarginHitPx)
            {
                _isDraggingMargin = true;
                _draggingLeftMargin = false;
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
                    {
                        double snapMm = _vm.UnitsToMm(_vm.SnapStep);
                        newMarginMm = Math.Round(newMarginMm / snapMm) * snapMm;
                    }
                    newMarginMm = Math.Max(0, Math.Min(newMarginMm,
                        _vm.PageWidthMm - _vm.MarginRightMm - 5));
                    _vm.MarginLeftMm = newMarginMm;
                }
                else
                {
                    double newMarginMm = PxToMm(pageOffsetXPx2 + pageWidthPx2 - clampedX, zoom);
                    if (_vm.IsSnapEnabled)
                    {
                        double snapMm = _vm.UnitsToMm(_vm.SnapStep);
                        newMarginMm = Math.Round(newMarginMm / snapMm) * snapMm;
                    }
                    newMarginMm = Math.Max(0, Math.Min(newMarginMm,
                        _vm.PageWidthMm - _vm.MarginLeftMm - 5));
                    _vm.MarginRightMm = newMarginMm;
                }

                // Шкала перерисуется автоматически: MarginLeftMm/RightMm — реактивные,
                // RulerControl подписан на PropertyChanged → InvalidateVisual.
                // Ноль шкалы всегда = textAreaStartPx = pageOffset + MarginLeft,
                // поэтому при сдвиге поля все метки автоматически сдвигаются.
                _vm.NotifyMarginChanged();
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            if (_isDragging)
            {
                if (_vm.Mode == RulerMode.Paragraph && _vm.DraggingIndentMarker.HasValue)
                {
                    double pageStartPx = _vm.PageOffsetXPx;
                    double pageEndPx = _vm.PageOffsetXPx + MmToPx(_vm.PageWidthMm, zoom);

                    double posUnits;
                    if (_vm.DraggingIndentMarker == RulerIndentMarkerType.RightIndent)
                    {
                        // RightIndent считается от правого края текстовой зоны.
                        // Отрицательное значение = маркер в зоне правого поля (текст расширен).
                        // Минимум: правый край листа = -MarginRightMm в единицах.
                        double pageEndPx2 = _vm.PageOffsetXPx + MmToPx(_vm.PageWidthMm, zoom);
                        double clampedX = Math.Max(_vm.PageOffsetXPx, Math.Min(pos.X, pageEndPx2));
                        posUnits = (textAreaEndPx - clampedX) / unitSizePx;
                    }
                    else
                    {
                        // LeftIndent и FirstLineIndent — от левого края текста.
                        // Ограничиваем X физическим листком — маркер не может уйти за левый край.
                        double clampedX = Math.Max(pageStartPx, Math.Min(pos.X, pageEndPx));
                        posUnits = (clampedX - textAreaStartPx) / unitSizePx;
                        // posUnits может быть отрицательным (маркер в зоне поля) —
                        // минимум = -MarginLeftUnits (левый край листа в координатах шкалы).
                        double minUnits = -(textAreaStartPx - pageStartPx) / unitSizePx;
                        posUnits = Math.Max(posUnits, minUnits);
                    }

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

            // Курсор при наведении.
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

            // Курсор при наведении на границу поля.
            const double MarginHoverPx = 5.0;
            if (Math.Abs(pos.X - textAreaStartPx) <= MarginHoverPx
                || Math.Abs(pos.X - textAreaEndPx) <= MarginHoverPx)
                Cursor = new Cursor(StandardCursorType.SizeWestEast);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (_isDraggingMargin)
            {
                _isDraggingMargin = false;
                e.Pointer.Capture(null);
                Cursor = new Cursor(StandardCursorType.Arrow);
                // CommitMarginChange инициирует полный пересчёт лейаутов.
                _vm?.CommitMarginChange();
                InvalidateVisual();
                e.Handled = true;
                return;
            }

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

        /// <summary>
        /// Hit-тест с приоритетом по Y-позиции:
        /// верхняя половина линейки → FirstLineIndent имеет приоритет.
        /// нижняя половина → LeftIndent/RightIndent имеют приоритет.
        /// Это решает баг когда оба маркера на одной X-позиции.
        /// </summary>
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

            // Вычисляем X-позиции всех маркеров.
            double xLeft = textAreaStartPx + GetMarkerPosition(RulerIndentMarkerType.LeftIndent) * unitSizePx;
            double xFirst = textAreaStartPx + GetMarkerPosition(RulerIndentMarkerType.FirstLineIndent) * unitSizePx;
            double xRight = textAreaEndPx - GetMarkerPosition(RulerIndentMarkerType.RightIndent) * unitSizePx;

            bool hitLeft = Math.Abs(xPx - xLeft) <= r;
            bool hitFirst = Math.Abs(xPx - xFirst) <= r;
            bool hitRight = Math.Abs(xPx - xRight) <= r;

            if (isUpperHalf)
            {
                // Верхняя половина: FirstLineIndent первым.
                if (hitFirst) return RulerIndentMarkerType.FirstLineIndent;
                if (hitLeft) return RulerIndentMarkerType.LeftIndent;
                if (hitRight) return RulerIndentMarkerType.RightIndent;
            }
            else
            {
                // Нижняя половина: нижние маркеры первыми.
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

        private static double PxToMm(double px, double zoom)
            => px / (96.0 / 25.4) / zoom;

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