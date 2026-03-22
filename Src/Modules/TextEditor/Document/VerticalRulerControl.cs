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
    /// • Шкала рисуется только для ОДНОЙ страницы — той, чей центр ближе всего
    ///   к центру видимой области (viewport). Никаких перекрытий меток между
    ///   соседними страницами.
    /// • Ноль шкалы = верхняя граница ТЕКСТОВОЙ области (после верхнего поля).
    ///   Зона полей: отрицательные значения сверху, значения > textHeight снизу.
    /// • Поля закрашены серым, текстовая зона — светлым фоном.
    ///
    /// ОПТИМИЗАЦИЯ:
    /// • Один проход делений на страницу → никакого роста сложности при
    ///   большом количестве страниц в документе.
    /// </summary>
    public sealed class VerticalRulerControl : Control
    {
        // ── Константы ────────────────────────────────────────────────────
        private const double RulerWidthPx = 24.0;
        private const double MajorTickWidthPx = 10.0;
        private const double MinorTickWidthPx = 6.0;
        private const double TinyTickWidthPx = 3.0;

        // ── Цвета ─────────────────────────────────────────────────────────
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

        // ── Состояние ─────────────────────────────────────────────────────
        private RulerViewModel? _vm;

        // ── Конструктор ───────────────────────────────────────────────────
        public VerticalRulerControl()
        {
            Width = RulerWidthPx;
        }

        // ── DataContext ───────────────────────────────────────────────────
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

        // ── Render ────────────────────────────────────────────────────────
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
            double viewportH = _vm.ViewportHeight > 0 ? _vm.ViewportHeight : h;

            const double PageGapPt = 15.0;
            const double PtToPx = 96.0 / 72.0;

            double pageHeightPx = MmToPx(_vm.PageHeightMm, zoom);
            double marginTopPx = MmToPx(_vm.MarginTopMm, zoom);
            double marginBotPx = MmToPx(_vm.MarginBottomMm, zoom);
            double pageGapPx = PageGapPt * PtToPx * zoom;
            double pageWithGapH = pageHeightPx + pageGapPx;

            // ── Определяем "текущую" страницу ────────────────────────────
            // Страница, чей центр ближе всего к центру viewport.
            // Это ровно та страница, которую видит пользователь (как в Word).
            double viewCenterDoc = scrollY + viewportH * 0.5;
            double fromFirstPage = Math.Max(0, viewCenterDoc - pageGapPx);
            int currentPageIdx = (int)(fromFirstPage / pageWithGapH);

            // Экранная Y верхнего края текущей страницы.
            double pTopY = pageGapPx + currentPageIdx * pageWithGapH - scrollY;
            double tTopY = pTopY + marginTopPx;
            double tBotY = pTopY + pageHeightPx - marginBotPx;
            double pBotY = pTopY + pageHeightPx;

            // ── Фон ──────────────────────────────────────────────────────
            using var bgPaint = new SKPaint { Color = ColBg };
            canvas.DrawRect(0, 0, w, h, bgPaint);

            // ── Серые зоны ────────────────────────────────────────────────
            using var marginPaint = new SKPaint { Color = ColMarginZone };

            // Всё выше текстовой зоны — серое (поле + пространство над листом).
            if (tTopY > 0)
                canvas.DrawRect(0, 0, w, (float)Math.Min(tTopY, h), marginPaint);

            // Всё ниже текстовой зоны — серое (поле + пространство под листом).
            if (tBotY < h)
                canvas.DrawRect(0, (float)Math.Max(0, tBotY),
                    w, h - (float)Math.Max(0, tBotY), marginPaint);

            // Зазор между страницами (если он попадает в видимую область).
            if (pBotY > 0 && pBotY < h)
            {
                float gapH = (float)Math.Min(pageGapPx, h - pBotY);
                if (gapH > 0)
                    canvas.DrawRect(0, (float)pBotY, w, gapH, marginPaint);
            }

            // ── Шкала ─────────────────────────────────────────────────────
            DrawScale(canvas, tTopY, tBotY, w, h, zoom);

            // ── Правая граница ────────────────────────────────────────────
            using var borderPaint = new SKPaint
            {
                Color = ColBorder,
                StrokeWidth = 1f,
                IsStroke = true
            };
            canvas.DrawLine(w - 0.5f, 0, w - 0.5f, h, borderPaint);
        }

        /// <summary>
        /// Рисует деления для ОДНОЙ страницы.
        /// <paramref name="tTopY"/> — экранный Y начала текста (ноль шкалы).
        /// <paramref name="tBotY"/> — экранный Y конца  текста.
        /// </summary>
        private void DrawScale(
            SKCanvas canvas,
            double tTopY,
            double tBotY,
            float w,
            float h,
            double zoom)
        {
            if (_vm is null) return;

            double unitSizePx = UnitSizePx(zoom);
            double majorInterval = _vm.MajorTickInterval;
            double minorInterval = _vm.MinorTickInterval;
            double tinyInterval = _vm.TinyTickInterval;

            // Диапазон шкалы в единицах — от верхнего поля до нижнего.
            double marginTopU = _vm.MmToUnits(_vm.MarginTopMm);
            double marginBotU = _vm.MmToUnits(_vm.MarginBottomMm);
            double textHU = _vm.MmToUnits(
                _vm.PageHeightMm - _vm.MarginTopMm - _vm.MarginBottomMm);

            double startUnit = -marginTopU;               // начало: верхний край листа
            double endUnit = textHU + marginBotU;       // конец:  нижний край листа
            double step = tinyInterval;
            int stepCount = (int)Math.Ceiling((endUnit - startUnit) / step) + 2;

            // Краски создаём один раз.
            using var majorP = StrokePaint(ColTickMajor);
            using var minorP = StrokePaint(ColTickMinor);
            using var tinyP = StrokePaint(ColTickTiny);
            using var majorPM = StrokePaint(ColTickMajorM);
            using var minorPM = StrokePaint(ColTickMinorM);
            using var tinyPM = StrokePaint(ColTickTinyM);
            using var labelP = new SKPaint { Color = ColLabel, IsAntialias = true };
            using var labelPM = new SKPaint { Color = ColLabelMargin, IsAntialias = true };

            using var tf = SKTypeface.FromFamilyName("Arial", SKFontStyle.Normal)
                             ?? SKTypeface.Default;
            using var font = new SKFont(tf, 8f);

            for (int i = 0; i <= stepCount; i++)
            {
                double unitValue = startUnit + i * step;
                double yPx = tTopY + unitValue * unitSizePx;

                // Пропускаем деления вне видимой области.
                if (yPx < -2 || yPx > h + 2) continue;

                bool inMargin = unitValue < 0 || unitValue > textHU;
                bool isMajor = IsMultiple(unitValue + marginTopU, majorInterval);
                bool isMinor = !isMajor && IsMultiple(unitValue + marginTopU, minorInterval);

                float tickW = isMajor ? (float)MajorTickWidthPx
                            : isMinor ? (float)MinorTickWidthPx
                            : (float)TinyTickWidthPx;

                SKPaint paint = inMargin
                    ? (isMajor ? majorPM : isMinor ? minorPM : tinyPM)
                    : (isMajor ? majorP : isMinor ? minorP : tinyP);

                canvas.DrawLine(w - tickW, (float)yPx, w, (float)yPx, paint);

                // Подпись только на основных делениях (кроме нуля).
                if (!isMajor || Math.Abs(unitValue) <= majorInterval * 0.1) continue;

                // Отображаем расстояние от начала текстовой зоны (или от поля).
                double displayValue;
                if (inMargin)
                    displayValue = unitValue < 0 ? -unitValue : unitValue - textHU;
                else
                    displayValue = unitValue;

                string label = _vm.Units == RulerUnits.Inches
                    ? displayValue.ToString("0.##")
                    : ((int)Math.Round(displayValue * 10)).ToString();

                // Рисуем подпись повёрнутую на -90° — вертикальный текст.
                using var save = new SKAutoCanvasRestore(canvas, true);
                canvas.Translate(w - tickW - 2f, (float)yPx);
                canvas.RotateDegrees(-90);
                float textW = font.MeasureText(label);
                canvas.DrawText(label, -textW / 2f, 0,
                    font, inMargin ? labelPM : labelP);
            }
        }

        // ── Вспомогательные ──────────────────────────────────────────────

        private double UnitSizePx(double zoom)
        {
            if (_vm is null) return 96.0 * zoom;
            double unitMm = _vm.Units == RulerUnits.Inches ? 25.4 : 10.0;
            return unitMm * (96.0 / 25.4) * zoom;
        }

        private static double MmToPx(double mm, double zoom)
            => mm * (96.0 / 25.4) * zoom;

        private static bool IsMultiple(double value, double step)
        {
            if (step <= 0) return false;
            double r = Math.Abs(value % step);
            return r < step * 0.01 || r > step * 0.99;
        }

        private static SKPaint StrokePaint(SKColor color) => new SKPaint
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