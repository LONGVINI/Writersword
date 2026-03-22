using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using System;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Виджет выбора размера таблицы (до 8×8).
    /// Skia-контрол — рисует сетку, подсвечивает выбранную область при наведении,
    /// вызывает <see cref="TableSelected"/> при клике.
    ///
    /// Использование:
    ///   var picker = new TableGridPickerControl();
    ///   picker.TableSelected += (rows, cols) => documentVm.InsertTable(rows, cols);
    /// </summary>
    public sealed class TableGridPickerControl : Control
    {
        // ── Параметры сетки ───────────────────────────────────────────────
        private const int MaxCols = 8;
        private const int MaxRows = 8;

        private const float CellSize = 18f;   // пикселей на ячейку
        private const float CellGap = 2f;   // зазор между ячейками
        private const float Padding = 6f;   // внешний отступ
        private const float LabelH = 20f;   // высота подписи сверху

        // ── Цвета ─────────────────────────────────────────────────────────
        private static readonly SKColor ColBg = new(0xF8, 0xF8, 0xF8);
        private static readonly SKColor ColCell = new(0xFF, 0xFF, 0xFF);
        private static readonly SKColor ColCellBorder = new(0xAA, 0xAA, 0xAA);
        private static readonly SKColor ColHover = new(0xFF, 0xCC, 0x88);
        private static readonly SKColor ColHoverBorder = new(0xCC, 0x77, 0x00);
        private static readonly SKColor ColLabel = new(0x33, 0x33, 0x33);

        // ── Состояние ─────────────────────────────────────────────────────
        private int _hoverRow = 0;   // 0 = ничего не выбрано
        private int _hoverCol = 0;

        // ── Событие ───────────────────────────────────────────────────────

        /// <summary>
        /// Вызывается когда пользователь кликает на ячейку.
        /// Параметры: (строки, столбцы).
        /// </summary>
        public event Action<int, int>? TableSelected;

        // ── Конструктор ───────────────────────────────────────────────────

        public TableGridPickerControl()
        {
            // Фиксированный размер контрола.
            Width = Padding * 2 + MaxCols * CellSize + (MaxCols - 1) * CellGap;
            Height = LabelH + Padding * 2 + MaxRows * CellSize + (MaxRows - 1) * CellGap;
            Cursor = new Cursor(StandardCursorType.Hand);
        }

        // ── Render ────────────────────────────────────────────────────────

        public override void Render(DrawingContext ctx)
        {
            ctx.Custom(new GridDrawOp(this, new Rect(0, 0, Bounds.Width, Bounds.Height)));
        }

        internal void RenderWithSKCanvas(SKCanvas canvas)
        {
            float w = (float)Bounds.Width;
            float h = (float)Bounds.Height;

            // Фон.
            using var bgPaint = new SKPaint { Color = ColBg };
            canvas.DrawRect(0, 0, w, h, bgPaint);

            // Подпись.
            string label = (_hoverRow > 0 && _hoverCol > 0)
                ? $"{_hoverCol} × {_hoverRow}"
                : "Вставить таблицу";

            using var tf = SKTypeface.FromFamilyName("Arial", SKFontStyle.Normal) ?? SKTypeface.Default;
            using var font = new SKFont(tf, 10.5f);
            using var lPaint = new SKPaint { Color = ColLabel, IsAntialias = true };
            float tw = font.MeasureText(label);
            canvas.DrawText(label, (w - tw) / 2f, LabelH - 5f, font, lPaint);

            // Ячейки.
            using var fillNorm = new SKPaint { Color = ColCell, IsAntialias = false };
            using var fillHover = new SKPaint { Color = ColHover, IsAntialias = false };
            using var bordNorm = new SKPaint { Color = ColCellBorder, StrokeWidth = 0.8f, IsStroke = true };
            using var bordHover = new SKPaint { Color = ColHoverBorder, StrokeWidth = 1.2f, IsStroke = true };

            for (int r = 0; r < MaxRows; r++)
            {
                for (int c = 0; c < MaxCols; c++)
                {
                    float x = Padding + c * (CellSize + CellGap);
                    float y = LabelH + Padding + r * (CellSize + CellGap);

                    bool highlighted = r < _hoverRow && c < _hoverCol;

                    canvas.DrawRect(x, y, CellSize, CellSize,
                        highlighted ? fillHover : fillNorm);
                    canvas.DrawRect(x, y, CellSize, CellSize,
                        highlighted ? bordHover : bordNorm);
                }
            }
        }

        // ── Pointer events ────────────────────────────────────────────────

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var pos = e.GetPosition(this);
            UpdateHover((float)pos.X, (float)pos.Y);
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            if (_hoverRow != 0 || _hoverCol != 0)
            {
                _hoverRow = 0;
                _hoverCol = 0;
                InvalidateVisual();
            }
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (_hoverRow > 0 && _hoverCol > 0)
                TableSelected?.Invoke(_hoverRow, _hoverCol);
            e.Handled = true;
        }

        private void UpdateHover(float mx, float my)
        {
            int newRow = 0, newCol = 0;

            for (int r = 0; r < MaxRows; r++)
            {
                for (int c = 0; c < MaxCols; c++)
                {
                    float x = Padding + c * (CellSize + CellGap);
                    float y = LabelH + Padding + r * (CellSize + CellGap);

                    if (mx >= x && mx <= x + CellSize
                     && my >= y && my <= y + CellSize)
                    {
                        newRow = r + 1;
                        newCol = c + 1;
                    }
                }
            }

            if (newRow != _hoverRow || newCol != _hoverCol)
            {
                _hoverRow = newRow;
                _hoverCol = newCol;
                InvalidateVisual();
            }
        }

        // ── ICustomDrawOperation ──────────────────────────────────────────

        private sealed class GridDrawOp : ICustomDrawOperation
        {
            private readonly TableGridPickerControl _ctrl;
            public Rect Bounds { get; }

            public GridDrawOp(TableGridPickerControl ctrl, Rect bounds)
            {
                _ctrl = ctrl;
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
                _ctrl.RenderWithSKCanvas(lease.SkCanvas);
            }
        }
    }
}