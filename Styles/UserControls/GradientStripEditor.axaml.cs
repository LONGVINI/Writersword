using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Writersword.Core.Models.Project;
using CoreGradientStop = Writersword.Core.Models.Project.GradientColorStop;

namespace Writersword.Styles.UserControls
{
    // Полоса-редактор градиента: снизу полоса-превью, сверху стрелки-маркеры,
    // каждая остриём указывает на свою точку цвета. Нажатие по полосе добавляет
    // стоп и сразу начинает его перетаскивать. Нажатие по маркеру выбирает стоп
    // (его цвет правится основным выбором цвета) и тащит его; утаскивание вниз
    // удаляет. Тип/угол/режим/сброс — над полосой.
    public partial class GradientStripEditor : UserControl
    {
        private sealed class Stop
        {
            public double Pos;
            public Color Col;
            public Control? Chip;
        }

        // Геометрия маркера: прямоугольник 16x13 с остриём в точке (8,20).
        private const string PinGeometry = "M0,0 H16 V13 H10 L8,20 L6,13 H0 Z";
        private const double PinTipX = 8;

        private readonly List<Stop> _stops = new();
        private GradientKind _kind = GradientKind.Linear;
        private double _angle = 0;
        private GradientTextFill _fill = GradientTextFill.Block;
        private Stop? _active;

        private bool _suppress;
        private bool _ready;
        private Stop? _dragStop;
        private Control? _dragChip;
        private bool _dragMoved;
        private double _dragStartRatio;
        private Stop? _lastPressStop;
        private long _lastPressTick;

        private Border _bar = null!;
        private Canvas _layer = null!;
        private ComboBox _kindBox = null!;
        private Slider _angleSlider = null!;
        private ToggleButton _fillToggle = null!;
        private Grid _angleRow = null!;
        private TextBox _angleBox = null!;
        private AngleDial _angleDial = null!;

        // Активный стоп выбран: вернуть его hex, чтобы основной выбор цвета встал на него.
        public event Action<string>? ActiveStopSelected;

        // Что-либо изменилось (стопы/тип/угол/режим) — можно перестроить превью/код.
        public event Action? SpecChanged;

        public GradientStripEditor()
        {
            InitializeComponent();
            _bar = this.FindControl<Border>("Bar")!;
            _layer = this.FindControl<Canvas>("StopsLayer")!;
            _kindBox = this.FindControl<ComboBox>("KindBox")!;
            _angleSlider = this.FindControl<Slider>("AngleSlider")!;
            _fillToggle = this.FindControl<ToggleButton>("FillToggle")!;
            _angleRow = this.FindControl<Grid>("AngleRow")!;
            _angleBox = this.FindControl<TextBox>("AngleBox")!;
            _angleDial = this.FindControl<AngleDial>("AngleDial")!;
            _angleDial.UserAngleChanged += OnDialAngleChanged;
            _ready = true;
        }

        // Показать переключатель «Построчно» — включает модуль текста, которому нужна
        // построчная заливка. В остальных местах (карточки, задники) он не нужен.
        public bool ShowTextFillOption
        {
            get => _fillToggle.IsVisible;
            set => _fillToggle.IsVisible = value;
        }

        // ── Внешний интерфейс ────────────────────────────────────────────

        public void Load(GradientSpec spec)
        {
            _suppress = true;
            try
            {
                _kind = spec.Kind;
                _angle = spec.AngleDeg;
                _fill = spec.TextFill;

                _stops.Clear();
                foreach (var s in spec.SortedStops())
                    _stops.Add(new Stop { Pos = s.Position, Col = ParseColor(s.Hex) });
                if (_stops.Count == 0)
                    _stops.Add(new Stop { Pos = 0, Col = Colors.Black });

                _active = _stops[0];

                _kindBox.SelectedIndex = (int)_kind;
                _angleSlider.Value = CompassFromInternal(_angle);
                _fillToggle.IsChecked = _fill == GradientTextFill.PerLine;
                UpdateAngleUi();
            }
            finally { _suppress = false; }

            Rebuild();
        }

        public GradientSpec BuildSpec() => new GradientSpec
        {
            Kind = _kind,
            AngleDeg = _angle,
            TextFill = _fill,
            Stops = _stops
                .OrderBy(s => s.Pos)
                .Select(s => new CoreGradientStop(s.Pos, ToHex(s.Col)))
                .ToList()
        };

        // Обновить цвет активного стопа из основного выбора цвета.
        public void SetActiveColor(Color c)
        {
            if (_active == null) return;
            _active.Col = c;
            // Только перекрашиваем активный маркер и полосу. Полная пересборка стрелок
            // здесь недопустима: во время перетаскивания она уничтожает захваченный
            // указателем маркер, и стоп перестаёт двигаться.
            if (_active.Chip is Path ap)
                ap.Fill = new SolidColorBrush(c);
            RebuildBar();
            RaiseChanged();
        }

        // ── Тип / угол / режим / сброс ───────────────────────────────────

        private void OnKindChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (!_ready || _suppress) return;
            _kind = (GradientKind)Math.Clamp(_kindBox.SelectedIndex, 0, 2);
            UpdateAngleUi();
            RaiseChanged();
        }

        private void OnAngleChanged(object? sender, RangeBaseValueChangedEventArgs e)
        {
            if (!_ready || _suppress) return;
            // Слайдер — в компасной системе (0 сверху, по часовой), как поле и циферблат.
            // Во внутреннюю математику (0 вправо, против часовой) конвертируем для отрисовки.
            _angle = InternalFromCompass(e.NewValue);
            UpdateAngleUi();
            RaiseChanged();
        }

        private void OnFillChanged(object? sender, RoutedEventArgs e)
        {
            if (!_ready || _suppress) return;
            _fill = _fillToggle.IsChecked == true ? GradientTextFill.PerLine : GradientTextFill.Block;
            RaiseChanged();
        }

        // Сброс: оставить один стоп текущего активного цвета (градиент станет одноцветным).
        private void OnResetClick(object? sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            var col = _active?.Col ?? (_stops.Count > 0 ? _stops[0].Col : Colors.Black);
            _stops.Clear();
            var single = new Stop { Pos = 0, Col = col };
            _stops.Add(single);
            _active = single;
            Rebuild();
            ActiveStopSelected?.Invoke(ToHex(single.Col));
            RaiseChanged();
        }

        // Угол прячем для радиального типа, обновляем поле и циферблат.
        private void UpdateAngleUi()
        {
            _angleRow.IsVisible = _kind != GradientKind.Radial;
            double compass = CompassFromInternal(_angle);
            _angleBox.Text = ((int)Math.Round(compass)).ToString(CultureInfo.InvariantCulture);
            _angleDial.Angle = compass;
        }

        // ── Циферблат угла ───────────────────────────────────────────────

        // Циферблат — в компас-системе (0 сверху, по часовой), движок — в
        // математической (0 вправо, против часовой). Конвертируем между ними.
        private void OnDialAngleChanged()
        {
            // Циферблат и слайдер в одной (компасной) системе — значение переносим как есть.
            _angleSlider.Value = _angleDial.Angle;
        }

        private static double CompassFromInternal(double internalDeg)
        {
            double c = (90 - internalDeg) % 360;
            if (c < 0) c += 360;
            return c;
        }

        private static double InternalFromCompass(double compassDeg)
        {
            double a = (90 - compassDeg) % 360;
            if (a < 0) a += 360;
            return a;
        }

        // Автовыделение всего текста при фокусе — можно сразу вводить новое значение.
        private void OnAngleBoxGotFocus(object? sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb) tb.SelectAll();
        }

        private void OnAngleBoxKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CommitAngleBox();
                e.Handled = true;
            }
        }

        private void OnAngleBoxCommit(object? sender, RoutedEventArgs e) => CommitAngleBox();

        // Разобрать введённый угол, ограничить 0..360 и применить через слайдер.
        private void CommitAngleBox()
        {
            var digits = new string((_angleBox.Text ?? string.Empty)
                .Where(ch => char.IsDigit(ch) || ch == '-' || ch == '.').ToArray());

            if (double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                v = Math.Clamp(v, 0, 360);
                _angleSlider.Value = v;
            }
            UpdateAngleUi();
        }

        // ── Добавление / перетаскивание / удаление стопов ─────────────────

        private void OnLayerSizeChanged(object? sender, SizeChangedEventArgs e) => RebuildArrows();

        // Нажали по полосе — создаём стоп под курсором и сразу тащим его.
        // Перемещение существующих стопов — только за их маркеры (стрелки).
        private void OnBarPressed(object? sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(_bar).Properties.IsLeftButtonPressed) return;
            double w = _bar.Bounds.Width;
            if (w <= 0) return;
            double ratio = Math.Clamp(e.GetPosition(_bar).X / w, 0, 1);

            var stop = new Stop { Pos = ratio, Col = SampleAt(ratio) };
            _stops.Add(stop);
            _active = stop;
            Rebuild();
            BeginDrag(stop, e.Pointer, _bar);
            RaiseChanged();
            e.Handled = true;
        }

        private void OnBarMoved(object? sender, PointerEventArgs e)
        {
            if (_dragStop == null) return;
            UpdateDrag(e.GetPosition(_layer));
        }

        private void OnBarReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_dragStop == null) return;
            e.Pointer.Capture(null);
            EndDrag();
            e.Handled = true;
        }

        // Нажали по маркеру: выбираем стоп и тащим именно его. ПКМ — удалить.
        // Двойной клик определяем вручную (по ссылке на стоп и времени), чтобы он не
        // конфликтовал с перетаскиванием и не зависел от пересоздания маркеров.
        private void OnArrowPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Control chip || chip.Tag is not Stop stop) return;

            if (e.GetCurrentPoint(chip).Properties.IsRightButtonPressed)
            {
                RemoveStop(stop);
                e.Handled = true;
                return;
            }

            long now = Environment.TickCount64;
            if (ReferenceEquals(stop, _lastPressStop) && now - _lastPressTick < 400)
            {
                _lastPressStop = null;
                DuplicateStop(stop);
                e.Handled = true;
                return;
            }
            _lastPressStop = stop;
            _lastPressTick = now;

            _active = stop;
            BeginDrag(stop, e.Pointer, chip);
            e.Handled = true;
        }

        // Создать копию стопа рядом (тем же цветом): вправо, а у правого края — влево.
        // Отступ берём с гарантированным зазором в пикселях, иначе маркеры встают
        // впритык и цепляется соседний вместо нужного.
        private void DuplicateStop(Stop stop)
        {
            double w = _layer.Bounds.Width;
            double offset = 0.06;
            if (w > 0) offset = Math.Max(offset, 26.0 / w);

            double pos = stop.Pos + offset;
            if (pos > 1) pos = stop.Pos - offset;
            pos = Math.Clamp(pos, 0, 1);

            var copy = new Stop { Pos = pos, Col = stop.Col };
            _stops.Add(copy);
            _active = copy;
            Rebuild();
            ActiveStopSelected?.Invoke(ToHex(copy.Col));
            RaiseChanged();
        }

        // Удалить стоп, если он не последний: обновить активный и перестроить.
        private void RemoveStop(Stop stop)
        {
            if (_stops.Count <= 1) return;
            _stops.Remove(stop);
            if (_active == stop)
                _active = _stops.OrderBy(s => s.Pos).First();
            if (_dragStop == stop)
            {
                _dragStop = null;
                _dragChip = null;
                _dragMoved = false;
            }
            Rebuild();
            if (_active != null) ActiveStopSelected?.Invoke(ToHex(_active.Col));
            RaiseChanged();
        }

        private void OnArrowMoved(object? sender, PointerEventArgs e)
        {
            if (_dragStop == null) return;
            UpdateDrag(e.GetPosition(_layer));
        }

        private void OnArrowReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_dragStop == null) return;
            e.Pointer.Capture(null);
            EndDrag();
            e.Handled = true;
        }

        // Захват указателя на переданный элемент: пока он жив (его не пересоздаём),
        // события движения приходят стабильно и стоп тащится за курсором.
        private void BeginDrag(Stop stop, IPointer pointer, IInputElement captureTarget)
        {
            _dragStop = stop;
            _dragChip = stop.Chip;
            _dragMoved = false;
            _dragStartRatio = stop.Pos;
            pointer.Capture(captureTarget);
            HighlightActive();
            ActiveStopSelected?.Invoke(ToHex(stop.Col));
        }

        // p — позиция относительно слоя стрелок.
        private void UpdateDrag(Point p)
        {
            if (_dragStop == null) return;
            double w = _layer.Bounds.Width;
            if (w <= 0) return;
            double nr = Math.Clamp(p.X / w, 0, 1);
            if (Math.Abs(nr - _dragStartRatio) > 0.002) _dragMoved = true;
            _dragStop.Pos = nr;
            if (_dragChip != null)
                Canvas.SetLeft(_dragChip, _dragStop.Pos * w - PinTipX);
            RebuildBar();
        }

        private void EndDrag()
        {
            if (_dragStop == null) return;

            bool moved = _dragMoved;
            _dragStop = null;
            _dragChip = null;
            _dragMoved = false;

            // Чистый клик без сдвига не пересоздаёт маркеры: иначе элемент исчезает
            // между нажатиями и двойной клик для удаления не срабатывает.
            if (moved) Rebuild();
            else HighlightActive();

            if (_active != null) ActiveStopSelected?.Invoke(ToHex(_active.Col));
            RaiseChanged();
        }

        // ── Перестроение ─────────────────────────────────────────────────

        private void Rebuild()
        {
            RebuildBar();
            RebuildArrows();
        }

        private void RebuildBar()
        {
            var sorted = _stops.OrderBy(s => s.Pos).ToList();
            if (sorted.Count == 1)
            {
                _bar.Background = new SolidColorBrush(sorted[0].Col);
                return;
            }

            var brush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative)
            };
            foreach (var s in sorted)
                brush.GradientStops.Add(new Avalonia.Media.GradientStop(s.Col, s.Pos));
            _bar.Background = brush;
        }

        private void RebuildArrows()
        {
            _layer.Children.Clear();
            double w = _layer.Bounds.Width;
            if (w <= 0) return;

            var accent = this.TryFindResource("AccentDefaultBrush", out var ar) ? ar as IBrush : Brushes.Orange;
            var subtle = this.TryFindResource("BorderSubtleBrush", out var sr) ? sr as IBrush : Brushes.Gray;

            foreach (var s in _stops)
            {
                bool isActive = ReferenceEquals(s, _active);
                var pin = new Path
                {
                    Data = Geometry.Parse(PinGeometry),
                    Fill = new SolidColorBrush(s.Col),
                    Stroke = isActive ? accent : subtle,
                    StrokeThickness = isActive ? 2 : 1,
                    Tag = s
                };
                pin.PointerPressed += OnArrowPressed;
                pin.PointerMoved += OnArrowMoved;
                pin.PointerReleased += OnArrowReleased;

                s.Chip = pin;
                Canvas.SetLeft(pin, s.Pos * w - PinTipX);
                Canvas.SetTop(pin, 0);
                _layer.Children.Add(pin);
            }
        }

        // Перекрасить обводку существующих маркеров под активный стоп, не уничтожая
        // их (нужно во время захвата указателя при перетаскивании).
        private void HighlightActive()
        {
            var accent = this.TryFindResource("AccentDefaultBrush", out var ar) ? ar as IBrush : Brushes.Orange;
            var subtle = this.TryFindResource("BorderSubtleBrush", out var sr) ? sr as IBrush : Brushes.Gray;

            foreach (var child in _layer.Children)
            {
                if (child is Path p && p.Tag is Stop s)
                {
                    bool isActive = ReferenceEquals(s, _active);
                    p.Stroke = isActive ? accent : subtle;
                    p.StrokeThickness = isActive ? 2 : 1;
                }
            }
        }

        private void RaiseChanged()
        {
            if (_suppress) return;
            SpecChanged?.Invoke();
        }

        // ── Помощники ────────────────────────────────────────────────────

        // Цвет градиента в позиции ratio: линейная интерполяция между соседними стопами.
        private Color SampleAt(double ratio)
        {
            var sorted = _stops.OrderBy(s => s.Pos).ToList();
            if (sorted.Count == 0) return Colors.Black;
            if (ratio <= sorted[0].Pos) return sorted[0].Col;
            if (ratio >= sorted[^1].Pos) return sorted[^1].Col;

            for (int i = 0; i < sorted.Count - 1; i++)
            {
                var a = sorted[i];
                var b = sorted[i + 1];
                if (ratio >= a.Pos && ratio <= b.Pos)
                {
                    double span = b.Pos - a.Pos;
                    double t = span <= 1e-6 ? 0 : (ratio - a.Pos) / span;
                    return Lerp(a.Col, b.Col, t);
                }
            }
            return sorted[^1].Col;
        }

        private static Color Lerp(Color a, Color b, double t)
        {
            byte L(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
            return Color.FromArgb(L(a.A, b.A), L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
        }

        private static Color ParseColor(string hex)
            => Color.TryParse(hex, out var c) ? c : Colors.Black;

        private static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }
}
