using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using System;
using System.Runtime.InteropServices;

namespace Writersword.Styles.UserControls
{
    /// <summary>
    /// Поле случайных цветов: щелчок по точке наезжает на неё камерой и отдаёт
    /// её цвет.
    ///
    /// Вынесено отдельным контролом, чтобы шум можно было показать и в
    /// маленькой всплывашке образца цвета, и в большом окне настройки. Наезд
    /// здесь тот же самый, что и у поля в окне «Настроить цвет»
    /// (Styles/UserControls/ColorEditorOverlay.axaml.cs): камера едет к точке
    /// клика около двух секунд, масштаб доходит почти до одного зерна, и к
    /// концу наезда поверх картинки проявляется сплошная заливка выбранным
    /// цветом. Сам цвет уходит наружу только в конце — иначе по мелкому зерну
    /// легко промахнуться и не увидеть, что вообще было взято.
    ///
    /// Раньше здесь была упрощённая замена наезда — RenderTransform с
    /// переходом на 260 мс и откатом обратно; выглядело это как «немного
    /// пододвинул и всё», и вернуться к общему виду было нечем.
    ///
    /// Контрол — Border: внутри картинка поля и слой сплошной заливки, а
    /// обрезка по скруглённым углам и приём щелчков лежат на самом Border.
    /// Поле квадратное по построению: задавайте одинаковые Width и Height.
    /// </summary>
    public class NoisePicker : Border
    {
        private readonly Random _rng = new();

        private readonly Panel _panel = new();
        private readonly Image _image = new();
        private readonly Border _solid = new();
        private readonly ScaleTransform _scaleT = new(1, 1);
        private readonly TranslateTransform _translateT = new();

        private WriteableBitmap? _bitmap;
        private double[]? _fieldR, _fieldG, _fieldB;
        private int _builtResolution = -1;
        private string? _builtPreset;

        private DispatcherTimer? _timer;
        private double _animT, _animStep;
        private double _scale = 1, _tx, _ty;
        private double _scaleStart = 1, _txStart, _tyStart;
        private double _scaleTarget = 1, _txTarget, _tyTarget;
        private Color _pending;
        private bool _hasPending;

        // Глубина наезда: во столько раз крупнее одного зерна становится кадр.
        // Та же цифра, что и в окне «Настроить цвет».
        private const double ZoomFactor = 2.2;
        private const double ZoomInMs = 2000;
        private const double ZoomOutMs = 300;
        private const double FrameMs = 16;

        public static readonly StyledProperty<string> PresetProperty =
            AvaloniaProperty.Register<NoisePicker, string>(nameof(Preset), "rainbow");

        /// <summary>Набор цветов: rainbow, skin, pastel, gray, neon.</summary>
        public string Preset
        {
            get => GetValue(PresetProperty);
            set => SetValue(PresetProperty, value);
        }

        public static readonly StyledProperty<int> ResolutionProperty =
            AvaloniaProperty.Register<NoisePicker, int>(nameof(Resolution), 64);

        /// <summary>Сторона поля в точках. Крупнее — мельче зерно.</summary>
        public int Resolution
        {
            get => GetValue(ResolutionProperty);
            set => SetValue(ResolutionProperty, value);
        }

        /// <summary>Щёлкнули по точке и наезд на неё завершился. Строка — код цвета вида #RRGGBB.</summary>
        public event Action<string>? ColorPicked;

        public NoisePicker()
        {
            ClipToBounds = true;
            Cursor = new Cursor(StandardCursorType.Hand);

            // Щелчки принимает сам Border, а не картинка: у картинки висит
            // RenderTransform наезда, и её координаты уже сдвинуты. Без заливки
            // Border не попадает под курсор вовсе — картинка и слой заливки из
            // проверки исключены, и щелчок проваливался бы насквозь.
            Background = Brushes.Transparent;

            _image.Stretch = Stretch.Fill;
            _image.HorizontalAlignment = HorizontalAlignment.Stretch;
            _image.VerticalAlignment = VerticalAlignment.Stretch;
            _image.IsHitTestVisible = false;
            _image.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative);
            _image.RenderTransform = new TransformGroup
            {
                Children = { _scaleT, _translateT }
            };
            RenderOptions.SetBitmapInterpolationMode(_image, BitmapInterpolationMode.HighQuality);

            _solid.IsHitTestVisible = false;
            _solid.Opacity = 0;

            _panel.Children.Add(_image);
            _panel.Children.Add(_solid);
            Child = _panel;
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == PresetProperty || change.Property == ResolutionProperty)
                Build();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            Build();
        }

        /// <summary>Пересобрать поле заново — другие случайные цвета, камера в исходном виде.</summary>
        public void Regenerate()
        {
            _builtResolution = -1;
            Build();
        }

        /// <summary>Плавно вернуть камеру к общему виду поля.</summary>
        public void ResetView()
        {
            _hasPending = false;
            _solid.Opacity = 0;
            _scaleStart = _scale; _txStart = _tx; _tyStart = _ty;
            _scaleTarget = 1; _txTarget = 0; _tyTarget = 0;
            StartAnim(ZoomOutMs);
        }

        /// <summary>Снять наезд без анимации — поле собрано заново, ехать некуда.</summary>
        private void ResetViewNow()
        {
            _timer?.Stop();
            _hasPending = false;
            _solid.Opacity = 0;
            _scale = _scaleTarget = _scaleStart = 1;
            _tx = _txTarget = _txStart = 0;
            _ty = _tyTarget = _tyStart = 0;
            ApplyTransform();
        }

        private void Build()
        {
            var n = Math.Max(8, Resolution);
            if (_builtResolution == n && _builtPreset == Preset && _bitmap is not null) return;

            _fieldR = new double[n * n];
            _fieldG = new double[n * n];
            _fieldB = new double[n * n];

            var bitmap = new WriteableBitmap(
                new PixelSize(n, n), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Unpremul);

            using (var frame = bitmap.Lock())
            {
                var stride = frame.RowBytes;
                var row = new byte[stride];

                for (var y = 0; y < n; y++)
                {
                    for (var x = 0; x < n; x++)
                    {
                        var color = NextColor();
                        var idx = y * n + x;
                        _fieldR[idx] = color.R;
                        _fieldG[idx] = color.G;
                        _fieldB[idx] = color.B;

                        var o = x * 4;
                        row[o] = color.B;
                        row[o + 1] = color.G;
                        row[o + 2] = color.R;
                        row[o + 3] = 255;
                    }
                    Marshal.Copy(row, 0, IntPtr.Add(frame.Address, y * stride), stride);
                }
            }

            // Прежний битмап отпускается только после того, как показан новый:
            // освобождённый под источником даёт пустое место на кадр.
            var previous = _bitmap;
            _bitmap = bitmap;
            _image.Source = bitmap;
            previous?.Dispose();

            _builtResolution = n;
            _builtPreset = Preset;

            ResetViewNow();
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);

            // Щелчок посреди наезда игнорируется: цвет уже выбран и вот-вот
            // уйдёт наружу, второй наезд поверх первого сбил бы анимацию.
            if (_hasPending) return;
            if (_fieldR is null) return;

            var view = _panel.Bounds.Width;
            if (view <= 0) return;

            // Точка берётся относительно панели, а не картинки: у картинки
            // висит RenderTransform, и её собственные координаты уже сдвинуты
            // и растянуты наездом.
            var p = e.GetPosition(_panel);
            var localX = (p.X - _tx) / _scale;
            var localY = (p.Y - _ty) / _scale;

            _pending = SampleAt(localX, localY, view);
            _hasPending = true;

            _solid.Background = new SolidColorBrush(_pending);
            _solid.Opacity = 0;

            var target = Math.Max(8, Resolution) * ZoomFactor;
            var imageSize = view * target;
            _scaleStart = _scale; _txStart = _tx; _tyStart = _ty;
            _scaleTarget = target;
            _txTarget = Math.Clamp(view / 2 - localX * target, view - imageSize, 0);
            _tyTarget = Math.Clamp(view / 2 - localY * target, view - imageSize, 0);
            StartAnim(ZoomInMs);

            e.Handled = true;
        }

        /// <summary>Билинейная выборка цвета поля в точке (в координатах картинки до наезда).</summary>
        private Color SampleAt(double localX, double localY, double view)
        {
            var n = Math.Max(8, _builtResolution);
            if (_fieldR is null || _fieldG is null || _fieldB is null || view <= 0)
                return Colors.Black;

            var bx = Math.Clamp(localX / view * n - 0.5, 0, n - 1.0001);
            var by = Math.Clamp(localY / view * n - 0.5, 0, n - 1.0001);
            var x0 = (int)Math.Floor(bx);
            var y0 = (int)Math.Floor(by);
            var x1 = Math.Min(x0 + 1, n - 1);
            var y1 = Math.Min(y0 + 1, n - 1);
            var fx = bx - x0;
            var fy = by - y0;

            double Sample(double[] channel)
            {
                var top = channel[y0 * n + x0] * (1 - fx) + channel[y0 * n + x1] * fx;
                var bottom = channel[y1 * n + x0] * (1 - fx) + channel[y1 * n + x1] * fx;
                return top * (1 - fy) + bottom * fy;
            }

            return Color.FromRgb(
                (byte)Math.Clamp(Sample(_fieldR), 0, 255),
                (byte)Math.Clamp(Sample(_fieldG), 0, 255),
                (byte)Math.Clamp(Sample(_fieldB), 0, 255));
        }

        private void StartAnim(double durationMs)
        {
            _animT = 0;
            _animStep = durationMs <= 0 ? 1 : FrameMs / durationMs;

            if (_timer is null)
            {
                _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FrameMs) };
                _timer.Tick += OnTick;
            }
            _timer.Start();
        }

        private void OnTick(object? sender, EventArgs e)
        {
            _animT += _animStep;

            // ease-in-out cubic — мягкий старт и мягкая остановка.
            var x = _animT >= 1 ? 1 : _animT;
            var t = x < 0.5 ? 4 * x * x * x : 1 - Math.Pow(-2 * x + 2, 3) / 2;

            _scale = _scaleStart + (_scaleTarget - _scaleStart) * t;
            _tx = _txStart + (_txTarget - _txStart) * t;
            _ty = _tyStart + (_tyTarget - _tyStart) * t;
            ApplyTransform();

            // К концу наезда проявляем сплошную заливку — поле становится однотонным.
            _solid.Opacity = _hasPending ? Math.Clamp((t - 0.6) / 0.4, 0, 1) : 0;

            if (_animT < 1) return;

            _timer?.Stop();
            if (!_hasPending) return;

            _hasPending = false;
            ColorPicked?.Invoke($"#{_pending.R:X2}{_pending.G:X2}{_pending.B:X2}");
        }

        private void ApplyTransform()
        {
            _scaleT.ScaleX = _scale;
            _scaleT.ScaleY = _scale;
            _translateT.X = _tx;
            _translateT.Y = _ty;
        }

        private Color NextColor()
        {
            double R() => _rng.NextDouble();

            switch (Preset)
            {
                case "skin":
                    return HsvToRgb(18 + R() * 26, 0.25 + R() * 0.40, 0.55 + R() * 0.40);
                case "pastel":
                    return HsvToRgb(R() * 360, 0.18 + R() * 0.27, 0.85 + R() * 0.15);
                case "gray":
                {
                    var v = (byte)_rng.Next(0, 256);
                    return Color.FromRgb(v, v, v);
                }
                case "neon":
                    return HsvToRgb(R() * 360, 0.90 + R() * 0.10, 0.95 + R() * 0.05);
                default:
                    return HsvToRgb(R() * 360, 0.6 + R() * 0.4, 0.7 + R() * 0.3);
            }
        }

        /// <summary>
        /// HSV в цвет. Своя копия, а не общая с окном настройки: контрол обязан
        /// собираться сам по себе, без оглядки на то, кто его показывает.
        /// </summary>
        private static Color HsvToRgb(double h, double s, double v)
        {
            h = ((h % 360) + 360) % 360;
            var c = v * s;
            var x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
            var m = v - c;

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
