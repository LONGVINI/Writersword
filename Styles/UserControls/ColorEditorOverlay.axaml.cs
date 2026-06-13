using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Writersword.Core.Interfaces.WorkFlows;
using Writersword.Core.Models.Project;
using Writersword.Core.Services;

namespace Writersword.Styles.UserControls
{
    /// <summary>
    /// Внутри-приложенческий оверлей редактора цвета. Живёт в составе модуля,
    /// затемняет и блокирует только его область, не создаёт окно ОС.
    /// Два режима выбора: HSV-спектр (квадрат насыщенности/яркости + полоса оттенка)
    /// и палитра «соты». Плюс пипетка с экрана и пользовательская палитра проекта
    /// с перетаскиванием образцов.
    /// </summary>
    public partial class ColorEditorOverlay : UserControl
    {
        private const double SvSize = 220;
        private const double HueLen = 220;

        private readonly IScreenColorPicker _eyedropper = ScreenColorPicker.Create();
        private bool _syncing;
        private TaskCompletionSource<string?>? _tcs;

        // Текущий цвет редактора и его HSV-представление (оттенок сохраняется
        // при уходе насыщенности в ноль, чтобы полоса не прыгала на красный).
        private Color _current;
        private double _h, _s, _v;

        private bool _svDrag, _hueDrag;
        private bool _honeycombBuilt;

        // Пользовательская палитра проекта (закреплённые цвета). Источник истины —
        // ProjectFile.ProjectPinnedColors; эта коллекция — представление для биндинга.
        public ObservableCollection<string> Palette { get; } = new();

        private bool _palettePressed, _paletteDragging;
        private int _paletteDragIndex = -1;
        private Point _palettePressPos;
        private string? _paletteDragHex;

        public ColorEditorOverlay()
        {
            InitializeComponent();
            IsVisible = false;
        }

        /// <summary>
        /// Показывает редактор поверх модуля. Возвращает выбранный HEX или null при отмене.
        /// </summary>
        public Task<string?> ShowAsync(string hex, bool showPreview)
        {
            _tcs?.TrySetResult(null);
            _tcs = new TaskCompletionSource<string?>();

            var preview = this.FindControl<Control>("PreviewPanel");
            if (preview is not null) preview.IsVisible = showPreview;

            var eye = this.FindControl<Button>("EyedropperButton");
            if (eye is not null) eye.IsEnabled = _eyedropper.IsSupported;

            BuildHoneycomb();
            LoadPalette();
            SetTab(spectrum: true);

            Color c;
            try { c = Color.Parse(hex); }
            catch { c = Color.FromRgb(0x60, 0x7D, 0x8B); }

            SetColor(c);

            IsVisible = true;
            return _tcs.Task;
        }

        private void Complete(string? result)
        {
            IsVisible = false;
            var tcs = _tcs;
            _tcs = null;
            tcs?.TrySetResult(result);
        }

        // ── Применение/отрисовка цвета ────────────────────────────────────

        // Цвет из внешнего источника (RGB, HEX, соты, палитра, пипетка): пересчёт HSV.
        private void SetColor(Color c)
        {
            var (h, s, v) = RgbToHsv(c);
            if (s > 1e-4) _h = h;
            _s = s;
            _v = v;
            Render(c);
        }

        // Цвет из внутреннего источника (SV-квадрат, полоса оттенка): HSV не трогаем.
        private void ApplyHsv() => Render(HsvToRgb(_h, _s, _v));

        private void Render(Color c)
        {
            _syncing = true;
            try
            {
                _current = c;

                var sw = this.FindControl<Border>("PreviewSwatch");
                if (sw is not null) sw.Background = new SolidColorBrush(c);

                var sr = this.FindControl<Slider>("SliderR"); if (sr is not null) sr.Value = c.R;
                var sg = this.FindControl<Slider>("SliderG"); if (sg is not null) sg.Value = c.G;
                var sb = this.FindControl<Slider>("SliderB"); if (sb is not null) sb.Value = c.B;

                var lr = this.FindControl<TextBlock>("LabelR"); if (lr is not null) lr.Text = c.R.ToString();
                var lg = this.FindControl<TextBlock>("LabelG"); if (lg is not null) lg.Text = c.G.ToString();
                var lb = this.FindControl<TextBlock>("LabelB"); if (lb is not null) lb.Text = c.B.ToString();

                var hb = this.FindControl<TextBox>("HexBox");
                if (hb is not null) hb.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";

                var hueLayer = this.FindControl<Border>("SvHueLayer");
                if (hueLayer is not null) hueLayer.Background = new SolidColorBrush(HsvToRgb(_h, 1, 1));

                var svThumb = this.FindControl<Border>("SvThumb");
                if (svThumb is not null)
                {
                    Canvas.SetLeft(svThumb, _s * SvSize - 8);
                    Canvas.SetTop(svThumb, (1 - _v) * SvSize - 8);
                }

                var hueThumb = this.FindControl<Border>("HueThumb");
                if (hueThumb is not null)
                {
                    Canvas.SetLeft(hueThumb, -2);
                    Canvas.SetTop(hueThumb, _h / 360.0 * HueLen - 3);
                }

                UpdateCardPreview(c);
            }
            finally
            {
                _syncing = false;
            }
        }

        private void UpdateCardPreview(Color c)
        {
            var brush = new SolidColorBrush(c);
            var border = this.FindControl<Border>("PreviewCardBorder");
            if (border is not null) border.BorderBrush = brush;
            var avatar = this.FindControl<Border>("PreviewAvatar");
            if (avatar is not null) avatar.Background = brush;
        }

        private void SelectFromHex(string hex)
        {
            try { SetColor(Color.Parse(hex)); }
            catch { }
        }

        // ── Режимы (Спектр / Соты) ────────────────────────────────────────

        private void OnTabSpectrum(object? sender, RoutedEventArgs e) => SetTab(spectrum: true);
        private void OnTabHoneycomb(object? sender, RoutedEventArgs e) => SetTab(spectrum: false);

        private void SetTab(bool spectrum)
        {
            var sp = this.FindControl<Control>("SpectrumPanel");
            var hc = this.FindControl<Control>("HoneycombPanel");
            if (sp is not null) sp.IsVisible = spectrum;
            if (hc is not null) hc.IsVisible = !spectrum;

            ToggleClass(this.FindControl<Button>("TabSpectrumBtn"), "active", spectrum);
            ToggleClass(this.FindControl<Button>("TabHoneycombBtn"), "active", !spectrum);
        }

        private static void ToggleClass(Button? b, string cls, bool on)
        {
            if (b is null) return;
            if (on) { if (!b.Classes.Contains(cls)) b.Classes.Add(cls); }
            else b.Classes.Remove(cls);
        }

        // ── SV-квадрат ────────────────────────────────────────────────────

        private void OnSvPressed(object? sender, PointerPressedEventArgs e)
        {
            _svDrag = true;
            e.Pointer.Capture(sender as IInputElement);
            UpdateSv(e.GetPosition(sender as Visual));
            e.Handled = true;
        }

        private void OnSvMoved(object? sender, PointerEventArgs e)
        {
            if (_svDrag) UpdateSv(e.GetPosition(sender as Visual));
        }

        private void OnSvReleased(object? sender, PointerReleasedEventArgs e)
        {
            _svDrag = false;
            e.Pointer.Capture(null);
        }

        private void UpdateSv(Point p)
        {
            _s = Math.Clamp(p.X / SvSize, 0, 1);
            _v = Math.Clamp(1 - p.Y / SvSize, 0, 1);
            ApplyHsv();
        }

        // ── Полоса оттенка ────────────────────────────────────────────────

        private void OnHuePressed(object? sender, PointerPressedEventArgs e)
        {
            _hueDrag = true;
            e.Pointer.Capture(sender as IInputElement);
            UpdateHue(e.GetPosition(sender as Visual));
            e.Handled = true;
        }

        private void OnHueMoved(object? sender, PointerEventArgs e)
        {
            if (_hueDrag) UpdateHue(e.GetPosition(sender as Visual));
        }

        private void OnHueReleased(object? sender, PointerReleasedEventArgs e)
        {
            _hueDrag = false;
            e.Pointer.Capture(null);
        }

        private void UpdateHue(Point p)
        {
            _h = Math.Clamp(p.Y / HueLen, 0, 1) * 360;
            ApplyHsv();
        }

        // ── Соты ──────────────────────────────────────────────────────────

        private void BuildHoneycomb()
        {
            if (_honeycombBuilt) return;
            var canvas = this.FindControl<Canvas>("HoneycombCanvas");
            if (canvas is null) return;
            canvas.Children.Clear();

            const double r = 11;
            const int cols = 12;
            const int hueRows = 7;
            double w = Math.Sqrt(3) * r;
            double rowH = 1.5 * r;

            for (int row = 0; row < hueRows; row++)
            {
                double l = 0.82 - row * (0.62 / (hueRows - 1));
                for (int col = 0; col < cols; col++)
                {
                    double hue = col * (360.0 / cols);
                    AddHex(canvas, row, col, r, HslToRgb(hue, 0.82, l));
                }
            }
            for (int col = 0; col < cols; col++)
            {
                double g = 1.0 - col / (double)(cols - 1);
                byte v = (byte)Math.Round(g * 255);
                AddHex(canvas, hueRows, col, r, Color.FromRgb(v, v, v));
            }

            int totalRows = hueRows + 1;
            canvas.Width = cols * w + w / 2 + 4;
            canvas.Height = totalRows * rowH + r + 4;
            _honeycombBuilt = true;
        }

        private void AddHex(Canvas canvas, int row, int col, double r, Color color)
        {
            double w = Math.Sqrt(3) * r;
            double rowH = 1.5 * r;
            double offset = (row % 2 == 1) ? w / 2 : 0;
            double cx = col * w + w / 2 + offset + 2;
            double cy = row * rowH + r + 2;

            var poly = new Polygon
            {
                Points = HexPoints(cx, cy, r),
                Fill = new SolidColorBrush(color),
                Stroke = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)),
                StrokeThickness = 1,
                Tag = $"#{color.R:X2}{color.G:X2}{color.B:X2}",
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            poly.PointerPressed += OnHoneycombCellPressed;
            canvas.Children.Add(poly);
        }

        private static IList<Point> HexPoints(double cx, double cy, double r)
        {
            double hw = Math.Sqrt(3) / 2 * r;
            return new List<Point>
            {
                new Point(cx, cy - r),
                new Point(cx + hw, cy - r / 2),
                new Point(cx + hw, cy + r / 2),
                new Point(cx, cy + r),
                new Point(cx - hw, cy + r / 2),
                new Point(cx - hw, cy - r / 2),
            };
        }

        private void OnHoneycombCellPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is Polygon p && p.Tag is string hex) SelectFromHex(hex);
            e.Handled = true;
        }

        // ── Пользовательская палитра (drag-reorder + добавление/удаление) ──

        private void LoadPalette()
        {
            Palette.Clear();
            var proj = CurrentProject;
            if (proj is null) return;
            foreach (var c in proj.ProjectPinnedColors) Palette.Add(Normalize(c));
        }

        private void PersistPalette()
        {
            var proj = CurrentProject;
            if (proj is null) return;
            proj.ProjectPinnedColors.Clear();
            foreach (var c in Palette) proj.ProjectPinnedColors.Add(Normalize(c));
        }

        private int IndexOfPalette(string hex)
        {
            var n = Normalize(hex);
            for (int i = 0; i < Palette.Count; i++)
                if (Normalize(Palette[i]) == n) return i;
            return -1;
        }

        private void RemovePaletteColor(string hex)
        {
            var i = IndexOfPalette(hex);
            if (i >= 0) { Palette.RemoveAt(i); PersistPalette(); }
        }

        private void OnAddCurrentClick(object? sender, RoutedEventArgs e)
        {
            var hex = $"#{_current.R:X2}{_current.G:X2}{_current.B:X2}";
            if (IndexOfPalette(hex) < 0)
            {
                Palette.Add(Normalize(hex));
                PersistPalette();
            }
        }

        private void OnPalettePressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Border b || b.DataContext is not string hex) return;

            var props = e.GetCurrentPoint(b).Properties;
            if (props.IsRightButtonPressed)
            {
                RemovePaletteColor(hex);
                e.Handled = true;
                return;
            }

            _palettePressed = true;
            _paletteDragging = false;
            _paletteDragHex = hex;
            _paletteDragIndex = IndexOfPalette(hex);
            _palettePressPos = e.GetPosition(this);
            e.Pointer.Capture(b);
            e.Handled = true;
        }

        private void OnPaletteMoved(object? sender, PointerEventArgs e)
        {
            if (!_palettePressed) return;

            var cur = e.GetPosition(this);
            if (!_paletteDragging)
            {
                double dx = cur.X - _palettePressPos.X;
                double dy = cur.Y - _palettePressPos.Y;
                if (dx * dx + dy * dy < 25) return;
                _paletteDragging = true;
            }

            var items = this.FindControl<ItemsControl>("PaletteItems");
            if (items is null) return;

            int target = TargetIndexAt(items, e.GetPosition(items));
            if (target >= 0 && _paletteDragIndex >= 0 && target != _paletteDragIndex)
            {
                Palette.Move(_paletteDragIndex, target);
                _paletteDragIndex = target;
            }
        }

        private void OnPaletteReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (!_palettePressed) return;
            _palettePressed = false;
            e.Pointer.Capture(null);

            if (!_paletteDragging)
            {
                if (_paletteDragHex is string h) SelectFromHex(h);
            }
            else
            {
                PersistPalette();
            }

            _paletteDragging = false;
            _paletteDragIndex = -1;
            _paletteDragHex = null;
        }

        private static int TargetIndexAt(ItemsControl items, Point p)
        {
            foreach (var cont in items.GetRealizedContainers())
                if (cont.Bounds.Contains(p)) return items.IndexFromContainer(cont);
            return -1;
        }

        // ── RGB / HEX ─────────────────────────────────────────────────────

        private void OnRgbChanged(object? sender, RangeBaseValueChangedEventArgs e)
        {
            if (_syncing) return;
            var sr = this.FindControl<Slider>("SliderR");
            var sg = this.FindControl<Slider>("SliderG");
            var sb = this.FindControl<Slider>("SliderB");
            var c = Color.FromRgb(
                (byte)(sr?.Value ?? 0),
                (byte)(sg?.Value ?? 0),
                (byte)(sb?.Value ?? 0));
            SetColor(c);
        }

        private void OnHexCommit(object? sender, RoutedEventArgs e) => CommitHex();

        private void OnHexKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) CommitHex();
        }

        private void CommitHex()
        {
            if (_syncing) return;
            var hb = this.FindControl<TextBox>("HexBox");
            if (hb?.Text is not string t || string.IsNullOrWhiteSpace(t)) return;
            try { SetColor(Color.Parse(t)); }
            catch { }
        }

        // ── Пипетка ───────────────────────────────────────────────────────

        private async void OnEyedropperClick(object? sender, RoutedEventArgs e)
        {
            if (!_eyedropper.IsSupported) return;

            // Прячем оверлей, чтобы он не попал в снимок экрана, и даём кадр на перерисовку.
            var owner = TopLevel.GetTopLevel(this);
            IsVisible = false;
            await Task.Delay(60);
            Color? picked = null;
            try { picked = await _eyedropper.PickAsync(owner); }
            catch { picked = null; }
            IsVisible = true;

            if (picked is not null) SetColor(picked.Value);
        }

        // ── Кнопки ────────────────────────────────────────────────────────

        private void OnOkClick(object? sender, RoutedEventArgs e)
            => Complete($"#{_current.R:X2}{_current.G:X2}{_current.B:X2}");

        private void OnCancelClick(object? sender, RoutedEventArgs e) => Complete(null);

        private void OnCloseClick(object? sender, RoutedEventArgs e) => Complete(null);

        // ── Преобразования цвета и доступ к проекту ───────────────────────

        private static ProjectFile? CurrentProject =>
            CoreServices.GetService<ITabCollection>()?.ActiveTab?.Context?.Project;

        private static string Normalize(string? hex) => (hex ?? string.Empty).Trim().ToUpperInvariant();

        private static (double h, double s, double v) RgbToHsv(Color c)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double d = max - min;

            double h = 0;
            if (d > 1e-6)
            {
                if (max == r) h = 60 * (((g - b) / d) % 6);
                else if (max == g) h = 60 * (((b - r) / d) + 2);
                else h = 60 * (((r - g) / d) + 4);
            }
            if (h < 0) h += 360;

            double s = max <= 1e-6 ? 0 : d / max;
            double v = max;
            return (h, s, v);
        }

        private static Color HsvToRgb(double h, double s, double v)
        {
            h = ((h % 360) + 360) % 360;
            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60.0 % 2) - 1));
            double m = v - c;
            double r = 0, g = 0, b = 0;
            if (h < 60) { r = c; g = x; }
            else if (h < 120) { r = x; g = c; }
            else if (h < 180) { g = c; b = x; }
            else if (h < 240) { g = x; b = c; }
            else if (h < 300) { r = x; b = c; }
            else { r = c; b = x; }
            return Color.FromRgb(
                (byte)Math.Round((r + m) * 255),
                (byte)Math.Round((g + m) * 255),
                (byte)Math.Round((b + m) * 255));
        }

        private static Color HslToRgb(double h, double s, double l)
        {
            h = ((h % 360) + 360) % 360;
            double c = (1 - Math.Abs(2 * l - 1)) * s;
            double x = c * (1 - Math.Abs((h / 60.0 % 2) - 1));
            double m = l - c / 2;
            double r = 0, g = 0, b = 0;
            if (h < 60) { r = c; g = x; }
            else if (h < 120) { r = x; g = c; }
            else if (h < 180) { g = c; b = x; }
            else if (h < 240) { g = x; b = c; }
            else if (h < 300) { r = x; b = c; }
            else { r = c; b = x; }
            return Color.FromRgb(
                (byte)Math.Round((r + m) * 255),
                (byte)Math.Round((g + m) * 255),
                (byte)Math.Round((b + m) * 255));
        }
    }
}
