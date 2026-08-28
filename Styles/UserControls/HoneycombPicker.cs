using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using System;
using System.Collections.Generic;

namespace Writersword.Styles.UserControls
{
    /// <summary>
    /// Соты: сетка шестиугольников, щелчок по ячейке отдаёт её цвет.
    ///
    /// Вынесено отдельным контролом, чтобы соты можно было показать и в
    /// маленькой всплывашке образца цвета, и в большом окне настройки. Раньше
    /// они жили только внутри окна, и мини-версия означала бы вторую такую же
    /// сетку, которая разошлась бы с первой при первой же правке.
    ///
    /// Радиус ячейки задаётся снаружи: в окне соты крупные, во всплывашке —
    /// мельче, а сетка одна и та же.
    /// </summary>
    public class HoneycombPicker : Canvas
    {
        private const int Columns = 12;
        private const int HueRows = 7;

        private readonly List<Polygon> _cells = new();
        private Polygon? _selected;
        private double _builtRadius = -1;

        public static readonly StyledProperty<double> CellRadiusProperty =
            AvaloniaProperty.Register<HoneycombPicker, double>(nameof(CellRadius), 11.0);

        /// <summary>Радиус ячейки. Смена перестраивает сетку.</summary>
        public double CellRadius
        {
            get => GetValue(CellRadiusProperty);
            set => SetValue(CellRadiusProperty, value);
        }

        public static readonly StyledProperty<string?> SelectedHexProperty =
            AvaloniaProperty.Register<HoneycombPicker, string?>(nameof(SelectedHex));

        /// <summary>Код цвета выбранной ячейки, вида #RRGGBB.</summary>
        public string? SelectedHex
        {
            get => GetValue(SelectedHexProperty);
            set => SetValue(SelectedHexProperty, value);
        }

        /// <summary>Щёлкнули по ячейке. Строка — код цвета вида #RRGGBB.</summary>
        public event Action<string>? ColorPicked;

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == CellRadiusProperty) Build();
            else if (change.Property == SelectedHexProperty) Highlight(SelectedHex);
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            Build();
            Highlight(SelectedHex);
        }

        /// <summary>
        /// Построить сетку. Повторный вызов с тем же радиусом ничего не делает:
        /// контрол пересоздают при каждом показе всплывашки, а сотня
        /// многоугольников заново каждый раз ни к чему.
        /// </summary>
        private void Build()
        {
            var r = CellRadius;
            if (r <= 0) return;
            if (Math.Abs(_builtRadius - r) < 0.01 && _cells.Count > 0) return;

            Children.Clear();
            _cells.Clear();
            _selected = null;

            var w = Math.Sqrt(3) * r;
            var rowH = 1.5 * r;

            // Строки тона: сверху светлые, книзу тёмные.
            for (var row = 0; row < HueRows; row++)
            {
                var lightness = 0.82 - row * (0.62 / (HueRows - 1));
                for (var col = 0; col < Columns; col++)
                {
                    var hue = col * (360.0 / Columns);
                    AddCell(row, col, r, HslToRgb(hue, 0.82, lightness));
                }
            }

            // Последняя строка — серые: от белого к чёрному.
            for (var col = 0; col < Columns; col++)
            {
                var g = 1.0 - col / (double)(Columns - 1);
                var v = (byte)Math.Round(g * 255);
                AddCell(HueRows, col, r, Color.FromRgb(v, v, v));
            }

            Width = Columns * w + w / 2 + 4;
            Height = (HueRows + 1) * rowH + r + 4;
            _builtRadius = r;
        }

        private void AddCell(int row, int col, double r, Color color)
        {
            var w = Math.Sqrt(3) * r;
            var rowH = 1.5 * r;
            var offset = (row % 2 == 1) ? w / 2 : 0;
            var cx = col * w + w / 2 + offset + 2;
            var cy = row * rowH + r + 2;

            var hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            var poly = new Polygon
            {
                Points = HexPoints(cx, cy, r),
                Fill = new SolidColorBrush(color),
                Stroke = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)),
                StrokeThickness = 1,
                Tag = hex,
                Cursor = new Cursor(StandardCursorType.Hand)
            };

            poly.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                SelectedHex = hex;
                ColorPicked?.Invoke(hex);
            };

            Children.Add(poly);
            _cells.Add(poly);
        }

        /// <summary>Обвести ячейку, чей цвет совпадает с заданным.</summary>
        private void Highlight(string? hex)
        {
            Polygon? match = null;
            if (!string.IsNullOrEmpty(hex))
                foreach (var cell in _cells)
                    if (cell.Tag is string tag
                        && string.Equals(tag, hex, StringComparison.OrdinalIgnoreCase))
                    {
                        match = cell;
                        break;
                    }

            if (ReferenceEquals(match, _selected)) return;

            if (_selected is not null)
            {
                _selected.Stroke = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0));
                _selected.StrokeThickness = 1;
                _selected.ZIndex = 0;
            }

            _selected = match;

            if (_selected is not null)
            {
                _selected.Stroke = Brushes.White;
                _selected.StrokeThickness = 2;

                // Обводка рисуется поверх соседей: у прилегающих ячеек общие
                // грани, и без подъёма половина контура пряталась бы под ними.
                _selected.ZIndex = 1;
            }
        }

        private static Points HexPoints(double cx, double cy, double r)
        {
            var points = new Points();
            for (var i = 0; i < 6; i++)
            {
                var angle = Math.PI / 180.0 * (60 * i - 30);
                points.Add(new Point(cx + r * Math.Cos(angle), cy + r * Math.Sin(angle)));
            }
            return points;
        }

        /// <summary>
        /// HSL в цвет. Своя копия, а не общая с окном настройки: контрол обязан
        /// собираться сам по себе, без оглядки на то, кто его показывает.
        /// </summary>
        private static Color HslToRgb(double h, double s, double l)
        {
            h = ((h % 360) + 360) % 360;
            var c = (1 - Math.Abs(2 * l - 1)) * s;
            var x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
            var m = l - c / 2;

            double r1, g1, b1;
            if (h < 60) { r1 = c; g1 = x; b1 = 0; }
            else if (h < 120) { r1 = x; g1 = c; b1 = 0; }
            else if (h < 180) { r1 = 0; g1 = c; b1 = x; }
            else if (h < 240) { r1 = 0; g1 = x; b1 = c; }
            else if (h < 300) { r1 = x; g1 = 0; b1 = c; }
            else { r1 = c; g1 = 0; b1 = x; }

            return Color.FromRgb(
                (byte)Math.Round((r1 + m) * 255),
                (byte)Math.Round((g1 + m) * 255),
                (byte)Math.Round((b1 + m) * 255));
        }
    }
}
