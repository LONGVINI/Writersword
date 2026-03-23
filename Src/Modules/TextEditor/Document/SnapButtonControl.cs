using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using System;
using System.ComponentModel;
using Writersword.Modules.TextEditor.ViewModels.Components;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Кнопка-магнитик в левом верхнем углу линейки.
    /// Переключает IsSnapEnabled в RulerViewModel.
    /// </summary>
    public sealed class SnapButtonControl : Control
    {
        private RulerViewModel? _vm;

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
            _vm = DataContext as RulerViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
            InvalidateVisual();
        }

        private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(RulerViewModel.IsSnapEnabled))
                InvalidateVisual();
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (_vm is null) return;
            _vm.IsSnapEnabled = !_vm.IsSnapEnabled;
            e.Handled = true;
        }

        public override void Render(DrawingContext ctx)
        {
            ctx.Custom(new DrawOp(this, new Rect(0, 0, Bounds.Width, Bounds.Height)));
        }

        private void Draw(SKCanvas canvas)
        {
            bool on = _vm?.IsSnapEnabled ?? false;
            float w = (float)Bounds.Width;
            float h = (float)Bounds.Height;

            // ── Фон ──────────────────────────────────────────────────────
            using var bgPaint = new SKPaint
            {
                Color = on ? new SKColor(0x1A, 0x73, 0xE8) : new SKColor(0xF0, 0xF0, 0xF0)
            };
            canvas.DrawRect(0, 0, w, h, bgPaint);

            // ── Бордер ───────────────────────────────────────────────────
            using var borderPaint = new SKPaint
            {
                Color = new SKColor(0xBB, 0xBB, 0xBB),
                StrokeWidth = 1f,
                IsStroke = true
            };
            canvas.DrawLine(w - 0.5f, 0, w - 0.5f, h, borderPaint); // правая
            canvas.DrawLine(0, h - 0.5f, w, h - 0.5f, borderPaint); // нижняя

            // ── Иконка магнита ────────────────────────────────────────────
            // Рисуем букву Ω (омега) — классический символ магнита.
            // Или настоящий магнит: вертикальная скоба с ножками.
            float pad = w * 0.15f;
            float iconW = w - pad * 2f;
            float iconH = h - pad * 2f;
            float ox = pad;
            float oy = pad + iconH * 0.05f;

            SKColor bodyCol = on ? SKColors.White : new SKColor(0x44, 0x44, 0x44);
            SKColor poleCol = on ? new SKColor(0xFF, 0xE0, 0x00) : new SKColor(0x88, 0x88, 0x88);

            float lw = Math.Max(1.5f, w * 0.11f);

            using var bodyP = new SKPaint
            {
                Color = bodyCol,
                StrokeWidth = lw,
                IsStroke = true,
                IsAntialias = true,
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round
            };

            // Магнит в форме подковы:
            // - дуга вверху (180° снизу вверх)
            // - две вертикальные ножки

            float arcCX = ox + iconW * 0.5f;
            float arcCY = oy + iconH * 0.42f;
            float arcR = iconW * 0.34f;
            float legTop = arcCY;
            float legBot = oy + iconH * 0.88f;

            // Дуга (открыта вниз — 0° → 180° = верхняя половина окружности).
            using var path = new SKPath();
            path.AddArc(
                new SKRect(arcCX - arcR, arcCY - arcR, arcCX + arcR, arcCY + arcR),
                0f, 180f);
            canvas.DrawPath(path, bodyP);

            // Ножки.
            canvas.DrawLine(arcCX - arcR, legTop, arcCX - arcR, legBot, bodyP);
            canvas.DrawLine(arcCX + arcR, legTop, arcCX + arcR, legBot, bodyP);

            // Полюса (горизонтальные торцы ножек).
            using var poleP = new SKPaint
            {
                Color = poleCol,
                StrokeWidth = lw * 1.3f,
                IsStroke = true,
                IsAntialias = true,
                StrokeCap = SKStrokeCap.Round
            };
            float poleHalf = arcR * 0.45f;
            canvas.DrawLine(arcCX - arcR - poleHalf, legBot,
                            arcCX - arcR + poleHalf, legBot, poleP);
            canvas.DrawLine(arcCX + arcR - poleHalf, legBot,
                            arcCX + arcR + poleHalf, legBot, poleP);
        }

        private sealed class DrawOp : ICustomDrawOperation
        {
            private readonly SnapButtonControl _ctrl;
            public Rect Bounds { get; }
            public DrawOp(SnapButtonControl ctrl, Rect bounds) { _ctrl = ctrl; Bounds = bounds; }
            public void Dispose() { }
            public bool Equals(ICustomDrawOperation? other) => false;
            public bool HitTest(Point p) => true;
            public void Render(ImmediateDrawingContext ctx)
            {
                var f = ctx.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature))
                    as ISkiaSharpApiLeaseFeature;
                if (f is null) return;
                using var lease = f.Lease();
                _ctrl.Draw(lease.SkCanvas);
            }
        }
    }
}