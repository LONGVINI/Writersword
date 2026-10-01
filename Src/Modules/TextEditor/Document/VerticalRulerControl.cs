using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using Writersword.Modules.TextEditor.Models.Settings;
using Writersword.Modules.TextEditor.ViewModels.Components;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Вертикальная линейка редактора.
    ///
    /// ПОВЕДЕНИЕ:
    /// • Шкала рисуется у КАЖДОГО листа, видимого в окне (VisiblePages из
    ///   RulerViewModel), — у каждого по его собственному краю и в его пределах:
    ///   деления соседних листов не встречаются и не налезают друг на друга.
    ///   Одна шкала у листа каретки уезжала вверх, как только человек прокручивал
    ///   на следующую страницу, и напротив неё оставалась серая полоса.
    /// • Ноль шкалы = верхняя граница ТЕКСТОВОЙ области (после верхнего поля).
    /// • Поля и зазоры между листами закрашены серым, текстовая зона — светлым фоном.
    /// • Поля тянутся за границу у любой шкалы: поля у всех листов общие.
    /// </summary>
    public sealed class VerticalRulerControl : Control
    {
        private const double RulerWidthPx = 24.0;
        private const double MajorTickWidthPx = 10.0;
        private const double MinorTickWidthPx = 6.0;
        private const double TinyTickWidthPx = 3.0;

        // Палитра берётся у листа и пересобирается перед каждой отрисовкой — та
        // же, что у горизонтальной линейки: две линейки одного листа обязаны
        // выглядеть одинаково, а собранные по отдельности они разойдутся.
        private RulerPalette _palette = RulerPalette.Default;

        private SKColor ColBg => _palette.Sheet;
        private SKColor ColMarginZone => _palette.MarginZone;
        private SKColor ColTickMajor => _palette.TickMajor;
        private SKColor ColTickMinor => _palette.TickMinor;
        private SKColor ColTickTiny => _palette.TickTiny;
        private SKColor ColTickMajorM => _palette.TickMajorMuted;
        private SKColor ColTickMinorM => _palette.TickMinorMuted;
        private SKColor ColTickTinyM => _palette.TickTinyMuted;
        private SKColor ColLabel => _palette.Label;
        private SKColor ColLabelMargin => _palette.LabelMuted;
        private SKColor ColBorder => _palette.Border;
        private SKColor ColOutsidePage => _palette.OutsidePage;
        private SKColor ColPageEdge => _palette.PageEdge;
        private SKColor ColMarginHandle => _palette.MarginHandle;

        private RulerViewModel? _vm;
        private bool _isDraggingMargin;
        private bool _draggingTopMargin;
        // Сохраняем геометрию страницы в момент нажатия — не пересчитываем во время drag,
        // чтобы изменение FocusedPageIndex (смена каретки) не смещало маркер.
        private double _dragPageTopY;
        private double _dragPageBotY;

        // Масштаб листа, за поле которого тянут, — тоже на момент нажатия: шкал
        // теперь несколько, и пересчитывать миллиметры нужно по тому листу, у
        // которого поле взяли.
        private double _dragZoom = 1.0;

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

            // Цвета берутся у листа перед каждым кадром: вид меняется на ходу.
            _palette = RulerPalette.Resolve(_vm);

            float w = (float)RulerWidthPx;
            float h = (float)Bounds.Height;

            // ── Видимые листы ─────────────────────────────────────────────
            // Шкала у каждого листа в окне, у каждого по его собственному краю
            // (RulerViewModel.VisiblePages). Геометрия считается там же, где и для
            // попаданий указателя — одна формула на отрисовку и на drag полей.
            var sheets = ComputePageGeometries();

            // ── Фон ──────────────────────────────────────────────────────
            // Вне листов — зазоры между ними, место выше первого и ниже последнего —
            // линейка окрашена как поле вокруг страницы, так же как горизонтальная
            // линейка за краем листа. Зазор при этом читается как промежуток между
            // двумя линейками, а не как продолжение полей: когда и поля, и зазор были
            // одного серого, шкалы сливались в одну полосу, и зазор в полсантиметра
            // выглядел частью линейки, вылезшей за лист.
            using var outsidePaint = new SKPaint { Color = ColOutsidePage };
            canvas.DrawRect(0, 0, w, h, outsidePaint);

            // Поля листа — серым, от края листа до края текста.
            using var marginPaint = new SKPaint { Color = ColMarginZone };
            foreach (var sheet in sheets)
            {
                float top = (float)Math.Max(0, sheet.PageTopY);
                float bottom = (float)Math.Min(h, sheet.PageBotY);
                if (bottom > top)
                    canvas.DrawRect(0, top, w, bottom - top, marginPaint);
            }

            using var bgPaint = new SKPaint { Color = ColBg };
            foreach (var sheet in sheets)
            {
                float top = (float)Math.Max(0, sheet.TextTopY);
                float bottom = (float)Math.Min(h, sheet.TextBotY);
                if (bottom > top)
                    canvas.DrawRect(0, top, w, bottom - top, bgPaint);
            }

            // ── Линии границ полей ────────────────────────────────────────
            using var handlePaint = new SKPaint
            { Color = ColMarginHandle, StrokeWidth = 1f, IsStroke = true };
            foreach (var sheet in sheets)
            {
                if (sheet.TextTopY > 0 && sheet.TextTopY < h)
                    canvas.DrawLine(0, (float)sheet.TextTopY, w, (float)sheet.TextTopY, handlePaint);
                if (sheet.TextBotY > 0 && sheet.TextBotY < h)
                    canvas.DrawLine(0, (float)sheet.TextBotY, w, (float)sheet.TextBotY, handlePaint);
            }

            // ── Шкалы ─────────────────────────────────────────────────────
            // Шкала листа не выходит за его край: деления соседних листов иначе
            // сходились бы в зазоре между ними и налезали друг на друга.
            foreach (var sheet in sheets)
            {
                if (sheet.PageBotY < 0 || sheet.PageTopY > h) continue;

                canvas.Save();
                canvas.ClipRect(new SKRect(0, (float)sheet.PageTopY, w, (float)sheet.PageBotY));
                DrawScale(canvas, sheet.TextTopY, sheet.TextBotY, w, h, sheet.Zoom);
                canvas.Restore();
            }

            // ── Края листов ───────────────────────────────────────────────
            // Черта по верхнему и нижнему краю каждого листа: здесь одна шкала
            // кончается, а за зазором начинается следующая. Черта ложится внутрь
            // листа — на его крайний пиксель, а не в зазор.
            using var edgePaint = new SKPaint
            { Color = ColPageEdge, StrokeWidth = 1f, IsStroke = true, IsAntialias = false };
            foreach (var sheet in sheets)
            {
                float edgeTop = (float)Math.Floor(sheet.PageTopY) + 0.5f;
                float edgeBottom = (float)Math.Ceiling(sheet.PageBotY) - 0.5f;

                if (edgeTop > 0 && edgeTop < h)
                    canvas.DrawLine(0, edgeTop, w, edgeTop, edgePaint);
                if (edgeBottom > 0 && edgeBottom < h)
                    canvas.DrawLine(0, edgeBottom, w, edgeBottom, edgePaint);
            }

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

        /// <summary>
        /// Лист на линейке, в точках линейки: край листа, край текстовой зоны и
        /// масштаб, в котором лист стоит на экране.
        /// </summary>
        private readonly record struct PageGeometry(
            double PageTopY, double PageBotY, double TextTopY, double TextBotY, double Zoom);

        /// <summary>
        /// Все листы, видимые в окне, сверху вниз. Листы приходят от раскладки
        /// (RulerViewModel.VisiblePages), поэтому шкала стоит ровно напротив листа при
        /// любом числе листов в ряду.
        ///
        /// Масштаб каждого листа выводится из его высоты на экране, а не берётся у
        /// линейки: во время плавной смены масштаба лист уже промежуточного размера,
        /// а число в настройках — конечное, и шкала по нему разъезжалась бы с листом.
        ///
        /// Листов от раскладки ещё нет — одна шкала по FocusedPageIndex, как раньше.
        /// </summary>
        private List<PageGeometry> ComputePageGeometries()
        {
            var result = new List<PageGeometry>();
            if (_vm is null) return result;

            var bands = _vm.VisiblePages;
            if (bands.Count == 0)
            {
                var (pTop, pBot, tTop, tBot) = ComputePageGeometry();
                result.Add(new PageGeometry(pTop, pBot, tTop, tBot, _vm.Zoom));
                return result;
            }

            double pageHeightAtOnePx = MmToPx(_vm.PageHeightMm, 1.0);

            foreach (var band in bands)
            {
                double zoom = pageHeightAtOnePx > 0.0001 && band.HeightPx > 0
                    ? band.HeightPx / pageHeightAtOnePx
                    : _vm.Zoom;

                // Полоса листа — в координатах холста; на линейку её переносит сдвиг
                // холста в окне за вычетом прокрутки — та же формула, что у одной шкалы.
                double pTop = _vm.ContentTopOffsetPx + band.TopPx - _vm.ScrollOffsetY;
                double pBot = pTop + band.HeightPx;
                double tTop = pTop + MmToPx(_vm.MarginTopMm, zoom);
                double tBot = pBot - MmToPx(_vm.MarginBottomMm, zoom);

                result.Add(new PageGeometry(pTop, pBot, tTop, tBot, zoom));
            }

            return result;
        }

        /// <summary>
        /// Граница поля под указателем: лист и какая граница — верхняя или нижняя.
        /// Из нескольких подходящих берётся ближайшая к указателю. null — указатель
        /// не на границе ни одного листа.
        /// </summary>
        private (PageGeometry Sheet, bool Top)? FindMarginUnder(double y)
        {
            (PageGeometry Sheet, bool Top)? best = null;
            double bestDistance = double.MaxValue;

            foreach (var sheet in ComputePageGeometries())
            {
                double toTop = Math.Abs(y - sheet.TextTopY);
                if (toTop <= MarginHitPx && toTop < bestDistance)
                {
                    best = (sheet, true);
                    bestDistance = toTop;
                }

                double toBottom = Math.Abs(y - sheet.TextBotY);
                if (toBottom <= MarginHitPx && toBottom < bestDistance)
                {
                    best = (sheet, false);
                    bestDistance = toBottom;
                }
            }

            return best;
        }

        private (double pTopY, double pBotY, double tTopY, double tBotY) ComputePageGeometry()
        {
            if (_vm is null) return (0, 0, 0, 0);

            const double PageGapPt = 15.0; const double PtToPx = 96.0 / 72.0;
            double zoom = _vm.Zoom;
            double scrollY = _vm.ScrollOffsetY;
            double pageHeightPx = MmToPx(_vm.PageHeightMm, zoom);
            double pageGapPx = PageGapPt * PtToPx * zoom;
            int pageIdx = Math.Max(0, _vm.FocusedPageIndex);

            // Ряд страницы: в режиме двух страниц рядом вертикальная позиция задаётся рядом,
            // а не порядковым номером страницы (так же считает PageVisualDelta канваса).
            int cols = Math.Max(1, _vm.PagesPerRow);
            int rowIdx = pageIdx / cols;

            // ContentTopOffsetPx — сдвиг канваса внутри вьюпорта. Когда документ ниже вьюпорта,
            // Avalonia центрирует канвас по вертикали, и лист стоит ниже верха вьюпорта.
            // Без этого слагаемого шкала уезжает вверх относительно листа на мелком зуме.
            double pTopY = _vm.ContentTopOffsetPx
                + pageGapPx + rowIdx * (pageHeightPx + pageGapPx) - scrollY;
            double pBotY = pTopY + pageHeightPx;
            double tTopY = pTopY + MmToPx(_vm.MarginTopMm, zoom);
            double tBotY = pTopY + pageHeightPx - MmToPx(_vm.MarginBottomMm, zoom);
            return (pTopY, pBotY, tTopY, tBotY);
        }

        protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (_vm is null) return;

            // Режим сравнения: линейка только отображает — drag полей не начинается.
            if (_vm.IsReadOnly) return;

            var pos = e.GetPosition(this);

            // Поле берётся у того листа, на границе которого указатель: поля у всех
            // листов общие, и тянуть их можно за любую шкалу.
            if (FindMarginUnder(pos.Y) is not { } hit) return;

            _isDraggingMargin = true;
            _draggingTopMargin = hit.Top;
            _dragPageTopY = hit.Sheet.PageTopY;
            _dragPageBotY = hit.Sheet.PageBotY;
            _dragZoom = hit.Sheet.Zoom;
            _vm.BeginMarginDrag();
            e.Pointer.Capture(this);
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeNorthSouth);
            e.Handled = true;
        }

        protected override void OnPointerMoved(Avalonia.Input.PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (_vm is null) return;

            var pos = e.GetPosition(this);
            double zoom = _dragZoom;

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

            if (!_vm.IsReadOnly && FindMarginUnder(pos.Y) is not null)
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeNorthSouth);
            else
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Arrow);
        }

        // Захват мыши потерян посреди жеста поля (окно ушло из фокуса, всплыло другое
        // окно) — отпускания уже не будет. Жест завершается тем, что успели натянуть:
        // иначе на листе остались бы направляющие, а открытый шаг отмены — незакрытым.
        // Обычное отпускание снимает флаг до Capture(null), поэтому сюда не попадает.
        protected override void OnPointerCaptureLost(Avalonia.Input.PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            if (!_isDraggingMargin) return;

            _isDraggingMargin = false;
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Arrow);
            _vm?.CommitMarginChange();
            InvalidateVisual();
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