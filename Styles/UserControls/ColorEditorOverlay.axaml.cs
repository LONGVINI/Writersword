using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Writersword.Core.Interfaces.WorkFlows;
using Writersword.Core.Models.Project;
using Writersword.Core.Services;

namespace Writersword.Styles.UserControls
{
    /// <summary>
    /// Внутри-приложенческий оверлей редактора цвета. Живёт в составе модуля,
    /// затемняет и блокирует только его область, не создаёт окно ОС. Тянется
    /// по высоте под размер модуля (середина прокручивается).
    /// Режимы выбора: HSV-квадрат + полоса оттенка, соты, цветовое колесо и
    /// вкладка ручного ввода значений (HEX / RGB / HSL / HSV). Плюс пипетка с
    /// экрана и пользовательская палитра проекта с перетаскиванием образцов.
    /// </summary>
    public partial class ColorEditorOverlay : UserControl
    {
        private const double SvSize = 200;
        private const double HueLen = 200;
        private const double WheelSize = 200;
        private const double WheelRadius = 100;

        private readonly IScreenColorPicker _eyedropper = ScreenColorPicker.Create();
        private bool _syncing;
        private bool _ring;
        private bool _ringsAllState;
        private TaskCompletionSource<ColorEditResult?>? _tcs;

        // Текущий цвет редактора и его HSV-представление (оттенок сохраняется
        // при уходе насыщенности в ноль, чтобы полоса не прыгала на красный).
        private Color _current;
        private double _h, _s, _v;

        private bool _showPreview;
        private bool _previewCollapsed;

        private bool _svDrag, _hueDrag, _wheelDrag;
        private bool _honeycombBuilt, _wheelBuilt;

        // Ячейки сот и текущая подсвеченная (контур вокруг выбранного цвета).
        private readonly List<Polygon> _honeyCells = new();
        private Polygon? _honeySelected;

        // Пользовательская палитра проекта (закреплённые цвета). Источник истины —
        // ProjectFile.ProjectPinnedColors; эта коллекция — представление для биндинга.
        public ObservableCollection<string> Palette { get; } = new();

        private bool _palettePressed, _paletteDragging;
        private int _paletteDragIndex = -1;
        private Point _palettePressPos;
        private string? _paletteDragHex;
        private bool _paletteDirty;

        public ColorEditorOverlay()
        {
            InitializeComponent();
            IsVisible = false;

            // Клик по дорожке градиентного ползунка переносит значение в точку клика.
            foreach (var name in new[]
            {
                "SliderR", "SliderG", "SliderB",
                "SlR", "SlG", "SlB",
                "SlHslH", "SlHslS", "SlHslL",
                "SlHsvH", "SlHsvS", "SlHsvV"
            })
            {
                this.FindControl<Slider>(name)?
                    .AddHandler(InputElement.PointerPressedEvent, OnGradSliderPressed, RoutingStrategies.Tunnel);
            }

            // Связь менеджера палитр с редактором: клик по образцу ставит цвет,
            // «+» берёт текущий цвет редактора.
            var pm = this.FindControl<PaletteManagerView>("PalettesPanel");
            if (pm is not null)
            {
                pm.ColorPicked = SelectFromHex;
                pm.CurrentColorProvider = () => $"#{_current.R:X2}{_current.G:X2}{_current.B:X2}";
            }

            // Высота панели не должна превышать высоту модуля — иначе при сжатии
            // окна редактор обрезается. Середина (ScrollViewer) прокручивается.
            this.GetObservable(BoundsProperty).Subscribe(b =>
            {
                var panel = this.FindControl<Border>("EditorPanel");
                if (panel is null) return;
                panel.MaxHeight = Math.Max(220, b.Height - 48);
                panel.MaxWidth = Math.Min(640, Math.Max(120, b.Width - 48));

                // При узкой панели кнопка пипетки сворачивается до одной иконки.
                var lbl = this.FindControl<TextBlock>("EyedropperLabel");
                if (lbl is not null) lbl.IsVisible = panel.MaxWidth > 340;

            });
        }

        /// <summary>
        /// Показывает редактор поверх модуля. Возвращает выбранный HEX или null при отмене.
        /// </summary>
        public Task<ColorEditResult?> ShowAsync(string hex, bool showPreview,
            Bitmap? image, string? name, string? fallback, bool ringEnabled, bool ringsAllState)
        {
            _tcs?.TrySetResult(null);
            _tcs = new TaskCompletionSource<ColorEditResult?>();

            _showPreview = showPreview;
            _previewCollapsed = false;
            var preview = this.FindControl<Control>("PreviewPanel");
            if (preview is not null) preview.IsVisible = showPreview;
            var previewToggle = this.FindControl<Button>("PreviewToggle");
            if (previewToggle is not null) previewToggle.IsVisible = showPreview;

            var ringSection = this.FindControl<Control>("RingSection");
            if (ringSection is not null) ringSection.IsVisible = showPreview;
            var ringConfirm = this.FindControl<Control>("RingConfirmPanel");
            if (ringConfirm is not null) ringConfirm.IsVisible = false;

            var eye = this.FindControl<Button>("EyedropperButton");
            if (eye is not null) eye.IsEnabled = _eyedropper.IsSupported;

            // Превью реальной карточки: картинка/значок и имя.
            var img = this.FindControl<Image>("PreviewAvatarImage");
            if (img is not null) img.Source = image;
            var fb = this.FindControl<TextBlock>("PreviewFallbackText");
            if (fb is not null)
            {
                fb.Text = string.IsNullOrEmpty(fallback) ? "?" : fallback;
                fb.IsVisible = image is null;
            }
            var nm = this.FindControl<TextBlock>("PreviewNameText");
            if (nm is not null) nm.Text = string.IsNullOrWhiteSpace(name) ? string.Empty : name;

            _ring = ringEnabled;
            _ringsAllState = ringsAllState;
            _paletteDirty = false;
            var ringCheck = this.FindControl<CheckBox>("RingCheck");
            if (ringCheck is not null) ringCheck.IsChecked = ringEnabled;
            // Подпись кнопки переключателя: если у всех включено — «убрать у всех», иначе «включить у всех».
            var ringAllBtn = this.FindControl<Button>("RingAllButton");
            if (ringAllBtn is not null)
                ringAllBtn.Content = ringsAllState
                    ? SharedStrings.ColorEditor_RingNone
                    : SharedStrings.ColorEditor_RingAll;

            BuildHoneycomb();
            BuildWheel();
            LoadPalette();
            SetTab(0);
            this.FindControl<PaletteManagerView>("PalettesPanel")?.Refresh();

            Color c;
            try { c = Color.Parse(hex); }
            catch { c = Color.FromRgb(0x60, 0x7D, 0x8B); }

            SetColor(c);

            IsVisible = true;
            return _tcs.Task;
        }

        // applyAll: null — кольцо только для этого; true — кольца всем; false — убрать у всех.
        private void CompleteEditor(bool? applyAll)
        {
            if (_paletteDirty) SaveActiveDocument();
            var result = new ColorEditResult
            {
                Hex = $"#{_current.R:X2}{_current.G:X2}{_current.B:X2}",
                Ring = _ring,
                ApplyAll = applyAll
            };
            IsVisible = false;
            var tcs = _tcs;
            _tcs = null;
            tcs?.TrySetResult(result);
        }

        private void CompleteCancel()
        {
            // Палитра применяется сразу (через «+»), поэтому сохраняем её и при отмене.
            if (_paletteDirty) SaveActiveDocument();
            IsVisible = false;
            var tcs = _tcs;
            _tcs = null;
            tcs?.TrySetResult(null);
        }

        // Палитра живёт в ProjectFile, но её изменение не помечает проект «грязным»,
        // поэтому при правке палитры сохраняем документ явно.
        private static void SaveActiveDocument()
        {
            try
            {
                var tab = CoreServices.GetService<ITabCollection>()?.ActiveTab;
                var workflow = CoreServices.GetService<IProjectWorkflow>();
                if (tab is not null && workflow is not null)
                    _ = workflow.SaveDocumentAsync(tab);
            }
            catch { }
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

        // Цвет из внутреннего источника (квадрат, оттенок, колесо): HSV не трогаем.
        private void ApplyHsv() => Render(HsvToRgb(_h, _s, _v));

        private void Render(Color c)
        {
            _syncing = true;
            try
            {
                _current = c;

                var sw = this.FindControl<Border>("PreviewSwatch");
                if (sw is not null) sw.Background = new SolidColorBrush(c);

                var hexStr = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                var hb = this.FindControl<TextBox>("HexBox");
                if (hb is not null) hb.Text = hexStr;
                HighlightHoneycomb(hexStr);

                // Ползунки RGB (вкладка «Спектр»)
                var sr = this.FindControl<Slider>("SliderR"); if (sr is not null) sr.Value = c.R;
                var sg = this.FindControl<Slider>("SliderG"); if (sg is not null) sg.Value = c.G;
                var sb = this.FindControl<Slider>("SliderB"); if (sb is not null) sb.Value = c.B;

                // Градиентные ползунки (значения + цвет треков).
                UpdateGradients(c);
                var lr = this.FindControl<TextBlock>("LabelR"); if (lr is not null) lr.Text = c.R.ToString();
                var lg = this.FindControl<TextBlock>("LabelG"); if (lg is not null) lg.Text = c.G.ToString();
                var lb = this.FindControl<TextBlock>("LabelB"); if (lb is not null) lb.Text = c.B.ToString();

                // Текстовые поля (вкладка «Значения»)
                SetText("TxtR", c.R.ToString(CultureInfo.InvariantCulture));
                SetText("TxtG", c.G.ToString(CultureInfo.InvariantCulture));
                SetText("TxtB", c.B.ToString(CultureInfo.InvariantCulture));
                var (hl_h, hl_s, hl_l) = RgbToHsl(c);
                SetText("TxtHslH", ((int)Math.Round(hl_h)).ToString(CultureInfo.InvariantCulture));
                SetText("TxtHslS", ((int)Math.Round(hl_s * 100)).ToString(CultureInfo.InvariantCulture));
                SetText("TxtHslL", ((int)Math.Round(hl_l * 100)).ToString(CultureInfo.InvariantCulture));
                SetText("TxtHsvH", ((int)Math.Round(_h)).ToString(CultureInfo.InvariantCulture));
                SetText("TxtHsvS", ((int)Math.Round(_s * 100)).ToString(CultureInfo.InvariantCulture));
                SetText("TxtHsvV", ((int)Math.Round(_v * 100)).ToString(CultureInfo.InvariantCulture));

                // Квадрат и полоса оттенка
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

                // Колесо
                var wheelDim = this.FindControl<Ellipse>("WheelDim");
                if (wheelDim is not null) wheelDim.Opacity = 1 - _v;
                var wheelVal = this.FindControl<Slider>("WheelValue");
                if (wheelVal is not null) wheelVal.Value = _v * 100;
                var wheelThumb = this.FindControl<Border>("WheelThumb");
                if (wheelThumb is not null)
                {
                    double ang = _h * Math.PI / 180.0;
                    double tx = WheelRadius + Math.Sin(ang) * (_s * WheelRadius);
                    double ty = WheelRadius - Math.Cos(ang) * (_s * WheelRadius);
                    Canvas.SetLeft(wheelThumb, tx - 8);
                    Canvas.SetTop(wheelThumb, ty - 8);
                }

                UpdateCardPreview(c);
            }
            finally
            {
                _syncing = false;
            }
        }

        private void SetText(string name, string value)
        {
            var t = this.FindControl<TextBox>(name);
            if (t is not null) t.Text = value;
        }

        private void UpdateCardPreview(Color c)
        {
            var brush = new SolidColorBrush(c);
            var border = this.FindControl<Border>("PreviewCardBorder");
            if (border is not null) border.BorderBrush = brush;
            var avatar = this.FindControl<Ellipse>("PreviewAvatar");
            if (avatar is not null) avatar.Fill = brush;
            var ring = this.FindControl<Border>("PreviewRing");
            if (ring is not null)
            {
                ring.BorderBrush = brush;
                ring.IsVisible = _ring;
            }
        }

        private void OnRingCheckChanged(object? sender, RoutedEventArgs e)
        {
            var ringCheck = this.FindControl<CheckBox>("RingCheck");
            _ring = ringCheck?.IsChecked == true;
            var ring = this.FindControl<Border>("PreviewRing");
            if (ring is not null) ring.IsVisible = _ring;
        }

        private void OnTogglePreview(object? sender, RoutedEventArgs e)
        {
            _previewCollapsed = !_previewCollapsed;
            var prev = this.FindControl<Control>("PreviewPanel");
            if (prev is not null) prev.IsVisible = _showPreview && !_previewCollapsed;
        }

        private void OnRingAllClick(object? sender, RoutedEventArgs e)
        {
            var p = this.FindControl<Control>("RingConfirmPanel");
            if (p is not null) p.IsVisible = true;
        }

        private void OnRingConfirmCancel(object? sender, RoutedEventArgs e)
        {
            var p = this.FindControl<Control>("RingConfirmPanel");
            if (p is not null) p.IsVisible = false;
        }

        // Подтверждение: переключает состояние «у всех» (вкл↔выкл) и закрывает редактор.
        private void OnConfirmRingApply(object? sender, RoutedEventArgs e) => CompleteEditor(!_ringsAllState);

        // Резервный обработчик скрытой кнопки (на случай возврата двухкнопочного режима).
        private void OnConfirmRingRemove(object? sender, RoutedEventArgs e) => CompleteEditor(false);

        private void SelectFromHex(string hex)
        {
            try { SetColor(Color.Parse(hex)); }
            catch { }
        }

        // ── Вкладки ───────────────────────────────────────────────────────

        private void OnTabSpectrum(object? sender, RoutedEventArgs e) => SetTab(0);
        private void OnTabHoneycomb(object? sender, RoutedEventArgs e) => SetTab(1);
        private void OnTabWheel(object? sender, RoutedEventArgs e) => SetTab(2);
        private void OnTabValues(object? sender, RoutedEventArgs e) => SetTab(3);
        private void OnTabPalettes(object? sender, RoutedEventArgs e) => SetTab(4);

        private void SetTab(int index)
        {
            ShowPanel("SpectrumPanel", index == 0);
            ShowPanel("HoneycombPanel", index == 1);
            ShowPanel("WheelPanel", index == 2);
            ShowPanel("ValuesPanel", index == 3);

            ToggleClass(this.FindControl<Button>("TabSpectrumBtn"), "active", index == 0);
            ToggleClass(this.FindControl<Button>("TabHoneycombBtn"), "active", index == 1);
            ToggleClass(this.FindControl<Button>("TabWheelBtn"), "active", index == 2);
            ToggleClass(this.FindControl<Button>("TabValuesBtn"), "active", index == 3);
        }

        private void ShowPanel(string name, bool visible)
        {
            var c = this.FindControl<Control>(name);
            if (c is not null) c.IsVisible = visible;
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

        // ── Цветовое колесо ───────────────────────────────────────────────

        private void BuildWheel()
        {
            if (_wheelBuilt) return;
            var img = this.FindControl<Image>("WheelImage");
            if (img is null) return;

            int size = (int)WheelSize;
            double r = WheelRadius;
            var wb = new WriteableBitmap(
                new PixelSize(size, size), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Unpremul);

            using (var fb = wb.Lock())
            {
                int stride = fb.RowBytes;
                var row = new byte[stride];
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        double dx = x + 0.5 - r;
                        double dy = y + 0.5 - r;
                        double dist = Math.Sqrt(dx * dx + dy * dy);
                        int o = x * 4;
                        if (dist <= r)
                        {
                            double ang = Math.Atan2(dx, -dy) * 180.0 / Math.PI;
                            if (ang < 0) ang += 360;
                            var col = HsvToRgb(ang, dist / r, 1);
                            byte a = 255;
                            if (dist > r - 1) a = (byte)Math.Clamp((r - dist) * 255.0, 0, 255);
                            row[o] = col.B; row[o + 1] = col.G; row[o + 2] = col.R; row[o + 3] = a;
                        }
                        else
                        {
                            row[o] = 0; row[o + 1] = 0; row[o + 2] = 0; row[o + 3] = 0;
                        }
                    }
                    Marshal.Copy(row, 0, IntPtr.Add(fb.Address, y * stride), stride);
                }
            }

            img.Source = wb;
            _wheelBuilt = true;
        }

        private void OnWheelPressed(object? sender, PointerPressedEventArgs e)
        {
            _wheelDrag = true;
            e.Pointer.Capture(sender as IInputElement);
            UpdateWheel(e.GetPosition(sender as Visual));
            e.Handled = true;
        }

        private void OnWheelMoved(object? sender, PointerEventArgs e)
        {
            if (_wheelDrag) UpdateWheel(e.GetPosition(sender as Visual));
        }

        private void OnWheelReleased(object? sender, PointerReleasedEventArgs e)
        {
            _wheelDrag = false;
            e.Pointer.Capture(null);
        }

        private void UpdateWheel(Point p)
        {
            double dx = p.X - WheelRadius;
            double dy = p.Y - WheelRadius;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            double ang = Math.Atan2(dx, -dy) * 180.0 / Math.PI;
            if (ang < 0) ang += 360;
            _h = ang;
            _s = Math.Clamp(dist / WheelRadius, 0, 1);
            ApplyHsv();
        }

        private void OnWheelValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
        {
            if (_syncing) return;
            _v = Math.Clamp(e.NewValue / 100.0, 0, 1);
            ApplyHsv();
        }

        // ── Соты ──────────────────────────────────────────────────────────

        private void BuildHoneycomb()
        {
            if (_honeycombBuilt) return;
            var canvas = this.FindControl<Canvas>("HoneycombCanvas");
            if (canvas is null) return;
            canvas.Children.Clear();
            _honeyCells.Clear();
            _honeySelected = null;

            const double r = 11;
            const int cols = 12;
            const int hueRows = 7;
            double w = Math.Sqrt(3) * r;
            double rowH = 1.5 * r;

            for (int rowi = 0; rowi < hueRows; rowi++)
            {
                double l = 0.82 - rowi * (0.62 / (hueRows - 1));
                for (int col = 0; col < cols; col++)
                {
                    double hue = col * (360.0 / cols);
                    AddHex(canvas, rowi, col, r, HslToRgb(hue, 0.82, l));
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
            _honeyCells.Add(poly);
        }

        // Подсвечивает ячейку сот, чей цвет совпадает с текущим (контур), остальные сбрасывает.
        private void HighlightHoneycomb(string hex)
        {
            Polygon? match = null;
            foreach (var p in _honeyCells)
                if (p.Tag is string t && string.Equals(t, hex, StringComparison.OrdinalIgnoreCase))
                {
                    match = p;
                    break;
                }

            if (ReferenceEquals(match, _honeySelected)) return;

            if (_honeySelected is not null)
            {
                _honeySelected.Stroke = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0));
                _honeySelected.StrokeThickness = 1;
                _honeySelected.ZIndex = 0;
            }
            if (match is not null)
            {
                match.Stroke = Brushes.White;
                match.StrokeThickness = 3;
                match.ZIndex = 5;
            }
            _honeySelected = match;
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
            if (i >= 0) { Palette.RemoveAt(i); PersistPalette(); _paletteDirty = true; }
        }

        private void OnAddCurrentClick(object? sender, RoutedEventArgs e)
        {
            var hex = $"#{_current.R:X2}{_current.G:X2}{_current.B:X2}";
            if (IndexOfPalette(hex) < 0)
            {
                Palette.Add(Normalize(hex));
                PersistPalette();
                _paletteDirty = true;
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
                _paletteDirty = true;
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

        // ── RGB / HEX / HSL / HSV (ручной ввод цифрами) ───────────────────

        private void OnRgbSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
        {
            if (_syncing) return;
            var sr = this.FindControl<Slider>("SliderR");
            var sg = this.FindControl<Slider>("SliderG");
            var sb = this.FindControl<Slider>("SliderB");
            SetColor(Color.FromRgb(
                (byte)(sr?.Value ?? 0),
                (byte)(sg?.Value ?? 0),
                (byte)(sb?.Value ?? 0)));
        }

        // ── Градиентные ползунки вкладки «Значения» ───────────────────────

        private void OnValRgbSlider(object? sender, RangeBaseValueChangedEventArgs e)
        {
            if (_syncing) return;
            SetColor(Color.FromRgb(
                (byte)SliderVal("SlR"), (byte)SliderVal("SlG"), (byte)SliderVal("SlB")));
        }

        private void OnValHslSlider(object? sender, RangeBaseValueChangedEventArgs e)
        {
            if (_syncing) return;
            SetColor(HslToRgb(
                SliderVal("SlHslH"), SliderVal("SlHslS") / 100.0, SliderVal("SlHslL") / 100.0));
        }

        private void OnValHsvSlider(object? sender, RangeBaseValueChangedEventArgs e)
        {
            if (_syncing) return;
            _h = SliderVal("SlHsvH");
            _s = Math.Clamp(SliderVal("SlHsvS") / 100.0, 0, 1);
            _v = Math.Clamp(SliderVal("SlHsvV") / 100.0, 0, 1);
            ApplyHsv();
        }

        private double SliderVal(string name) => this.FindControl<Slider>(name)?.Value ?? 0;

        // Клик по дорожке ставит значение в точку клика; нажатие по самому ползунку — обычное перетаскивание.
        private void OnGradSliderPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Slider s) return;
            if (e.Source is Control c && c.FindAncestorOfType<Thumb>(true) is not null) return;

            double w = s.Bounds.Width;
            if (w <= 0) return;
            const double thumb = 16;
            double x = e.GetPosition(s).X;
            double frac = Math.Clamp((x - thumb / 2) / Math.Max(1, w - thumb), 0, 1);
            s.Value = s.Minimum + frac * (s.Maximum - s.Minimum);
            e.Handled = true;
        }

        private void SetSliderVal(string name, double v)
        {
            var s = this.FindControl<Slider>(name);
            if (s is not null) s.Value = v;
        }

        private void SetSliderBg(string name, IBrush b)
        {
            var s = this.FindControl<Slider>(name);
            if (s is not null) s.Background = b;
        }

        // Обновляет значения и градиенты-треки всех градиентных ползунков под текущий цвет.
        private void UpdateGradients(Color c)
        {
            var (lh, ls, ll) = RgbToHsl(c);

            SetSliderVal("SlR", c.R); SetSliderVal("SlG", c.G); SetSliderVal("SlB", c.B);
            SetSliderVal("SlHslH", lh); SetSliderVal("SlHslS", ls * 100); SetSliderVal("SlHslL", ll * 100);
            SetSliderVal("SlHsvH", _h); SetSliderVal("SlHsvS", _s * 100); SetSliderVal("SlHsvV", _v * 100);

            var rGrad = Grad(Color.FromRgb(0, c.G, c.B), Color.FromRgb(255, c.G, c.B));
            var gGrad = Grad(Color.FromRgb(c.R, 0, c.B), Color.FromRgb(c.R, 255, c.B));
            var bGrad = Grad(Color.FromRgb(c.R, c.G, 0), Color.FromRgb(c.R, c.G, 255));

            SetSliderBg("SliderR", rGrad); SetSliderBg("SliderG", gGrad); SetSliderBg("SliderB", bGrad);
            SetSliderBg("SlR", Clone(rGrad)); SetSliderBg("SlG", Clone(gGrad)); SetSliderBg("SlB", Clone(bGrad));

            SetSliderBg("SlHslH", HRainbow());
            SetSliderBg("SlHslS", Grad(HslToRgb(lh, 0, ll), HslToRgb(lh, 1, ll)));
            SetSliderBg("SlHslL", Grad(HslToRgb(lh, ls, 0), HslToRgb(lh, ls, 0.5), HslToRgb(lh, ls, 1)));

            SetSliderBg("SlHsvH", HRainbow());
            SetSliderBg("SlHsvS", Grad(HsvToRgb(_h, 0, _v), HsvToRgb(_h, 1, _v)));
            SetSliderBg("SlHsvV", Grad(HsvToRgb(_h, _s, 0), HsvToRgb(_h, _s, 1)));
        }

        private static LinearGradientBrush Clone(LinearGradientBrush src)
        {
            var b = new LinearGradientBrush { StartPoint = src.StartPoint, EndPoint = src.EndPoint };
            foreach (var s in src.GradientStops) b.GradientStops.Add(new GradientStop(s.Color, s.Offset));
            return b;
        }

        private static LinearGradientBrush Grad(params Color[] stops)
        {
            var b = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative)
            };
            if (stops.Length == 1)
                b.GradientStops.Add(new GradientStop(stops[0], 0));
            else
                for (int i = 0; i < stops.Length; i++)
                    b.GradientStops.Add(new GradientStop(stops[i], (double)i / (stops.Length - 1)));
            return b;
        }

        private static LinearGradientBrush HRainbow()
        {
            var b = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative)
            };
            double[] hs = { 0, 60, 120, 180, 240, 300, 360 };
            for (int i = 0; i < hs.Length; i++)
                b.GradientStops.Add(new GradientStop(HsvToRgb(hs[i], 1, 1), (double)i / (hs.Length - 1)));
            return b;
        }

        private void OnRgbKey(object? sender, KeyEventArgs e) { if (e.Key == Key.Enter) CommitRgb(); }
        private void OnRgbCommit(object? sender, RoutedEventArgs e) => CommitRgb();

        private void CommitRgb()
        {
            if (_syncing) return;
            int r = ReadInt("TxtR", 0, 255);
            int g = ReadInt("TxtG", 0, 255);
            int b = ReadInt("TxtB", 0, 255);
            SetColor(Color.FromRgb((byte)r, (byte)g, (byte)b));
        }

        private void OnHslKey(object? sender, KeyEventArgs e) { if (e.Key == Key.Enter) CommitHsl(); }
        private void OnHslCommit(object? sender, RoutedEventArgs e) => CommitHsl();

        private void CommitHsl()
        {
            if (_syncing) return;
            double h = ReadInt("TxtHslH", 0, 360);
            double s = ReadInt("TxtHslS", 0, 100) / 100.0;
            double l = ReadInt("TxtHslL", 0, 100) / 100.0;
            SetColor(HslToRgb(h, s, l));
        }

        private void OnHsvKey(object? sender, KeyEventArgs e) { if (e.Key == Key.Enter) CommitHsv(); }
        private void OnHsvCommit(object? sender, RoutedEventArgs e) => CommitHsv();

        private void CommitHsv()
        {
            if (_syncing) return;
            _h = ReadInt("TxtHsvH", 0, 360);
            _s = ReadInt("TxtHsvS", 0, 100) / 100.0;
            _v = ReadInt("TxtHsvV", 0, 100) / 100.0;
            ApplyHsv();
        }

        private int ReadInt(string name, int min, int max)
        {
            var t = this.FindControl<TextBox>(name);
            var s = (t?.Text ?? string.Empty).Trim();
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                return Math.Clamp(v, min, max);
            return min;
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

        private void OnOkClick(object? sender, RoutedEventArgs e) => CompleteEditor(null);

        private void OnCancelClick(object? sender, RoutedEventArgs e) => CompleteCancel();

        private void OnCloseClick(object? sender, RoutedEventArgs e) => CompleteCancel();

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

        private static (double h, double s, double l) RgbToHsl(Color c)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double d = max - min;
            double l = (max + min) / 2;

            double h = 0, s = 0;
            if (d > 1e-6)
            {
                s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
                if (max == r) h = 60 * (((g - b) / d) % 6);
                else if (max == g) h = 60 * (((b - r) / d) + 2);
                else h = 60 * (((r - g) / d) + 4);
                if (h < 0) h += 360;
            }
            return (h, s, l);
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

    /// <summary>
    /// Результат редактора цвета: выбранный HEX, состояние кольца вокруг аватара
    /// и флаг «применить кольцо ко всем персонажам».
    /// </summary>
    public sealed class ColorEditResult
    {
        public string Hex { get; init; } = string.Empty;
        public bool Ring { get; init; }
        public bool? ApplyAll { get; init; }
    }
}
