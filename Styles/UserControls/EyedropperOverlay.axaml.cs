using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Writersword.Styles.UserControls
{
    /// <summary>
    /// Полноэкранный оверлей пипетки: показывает снимок экрана, перехватывает все
    /// события мыши (наведение/клик не доходят до чужих окон), рисует курсор-пипетку
    /// и лупу с выбираемым пикселем. Реализация Windows (GDI).
    /// </summary>
    public partial class EyedropperOverlay : Window
    {
        private TaskCompletionSource<Color?>? _tcs;

        private WriteableBitmap? _bitmap;
        private byte[]? _buffer;
        private int _vx, _vy, _vw, _vh;

        public EyedropperOverlay()
        {
            InitializeComponent();
        }

        public static Task<Color?> PickAsync(TopLevel? owner)
        {
            var w = new EyedropperOverlay();
            return w.RunAsync(owner);
        }

        private Task<Color?> RunAsync(TopLevel? owner)
        {
            _tcs = new TaskCompletionSource<Color?>();

            if (!CaptureScreen())
            {
                _tcs.TrySetResult(null);
                return _tcs.Task;
            }

            double scale = owner?.RenderScaling ?? 1.0;
            if (scale <= 0) scale = 1.0;

            Position = new PixelPoint(_vx, _vy);
            Width = _vw / scale;
            Height = _vh / scale;

            var screenImage = this.FindControl<Image>("ScreenImage");
            if (screenImage is not null)
            {
                screenImage.Source = _bitmap;
                screenImage.Width = Width;
                screenImage.Height = Height;
            }

            // Декоративные элементы не должны перехватывать события мыши.
            foreach (var name in new[] { "Loupe", "HexLabel", "Pipette" })
            {
                var c = this.FindControl<Control>(name);
                if (c is not null) c.IsHitTestVisible = false;
            }

            var loupeImage = this.FindControl<Image>("LoupeImage");
            if (loupeImage is not null)
                RenderOptions.SetBitmapInterpolationMode(loupeImage, BitmapInterpolationMode.None);

            var canvas = this.FindControl<Canvas>("RootCanvas");
            if (canvas is not null)
            {
                canvas.PointerMoved += OnPointerMoved;
                canvas.PointerPressed += OnPointerPressed;
            }
            KeyDown += OnKeyDown;

            Show();
            Activate();
            Focus();

            return _tcs.Task;
        }

        private Color ColorAt(int px, int py)
        {
            if (_buffer is null) return Colors.Black;
            px = Math.Clamp(px, 0, _vw - 1);
            py = Math.Clamp(py, 0, _vh - 1);
            int i = (py * _vw + px) * 4;
            byte b = _buffer[i];
            byte g = _buffer[i + 1];
            byte r = _buffer[i + 2];
            return Color.FromRgb(r, g, b);
        }

        private void OnPointerMoved(object? sender, PointerEventArgs e)
        {
            if (_bitmap is null) return;

            GetCursorPos(out var cp);
            int px = cp.X - _vx;
            int py = cp.Y - _vy;
            var color = ColorAt(px, py);

            // Лупа: фрагмент 15x15 вокруг курсора.
            const int cropSize = 15;
            int sx = Math.Clamp(px - cropSize / 2, 0, Math.Max(0, _vw - cropSize));
            int sy = Math.Clamp(py - cropSize / 2, 0, Math.Max(0, _vh - cropSize));
            var loupeImage = this.FindControl<Image>("LoupeImage");
            if (loupeImage is not null)
            {
                try { loupeImage.Source = new CroppedBitmap(_bitmap, new PixelRect(sx, sy, cropSize, cropSize)); }
                catch { }
            }

            var hexText = this.FindControl<TextBlock>("HexText");
            if (hexText is not null) hexText.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            var hexSwatch = this.FindControl<Border>("HexSwatch");
            if (hexSwatch is not null) hexSwatch.Background = new SolidColorBrush(color);

            var pos = e.GetPosition((Visual?)sender);
            PositionDecor(pos);
        }

        private void PositionDecor(Point pos)
        {
            const double loupeSize = 128;
            const double half = loupeSize / 2;

            var loupe = this.FindControl<Border>("Loupe");
            var label = this.FindControl<Border>("HexLabel");

            // Лупа центрируется ровно на курсоре — её центр и есть точка выбора.
            if (loupe is not null)
            {
                Canvas.SetLeft(loupe, pos.X - half);
                Canvas.SetTop(loupe, pos.Y - half);
                loupe.IsVisible = true;
            }

            // Подпись HEX — под лупой (или над ней у нижнего края), по центру лупы.
            if (label is not null)
            {
                // Фактические размеры подписи (на первом кадре ещё не измерены — берём запас).
                double lw = label.Bounds.Width > 0 ? label.Bounds.Width : 70;
                double lh = label.Bounds.Height > 0 ? label.Bounds.Height : 24;

                // По вертикали: под лупой; у нижнего края — над лупой.
                double ly = pos.Y + half + 6;
                if (ly + lh > Height) ly = pos.Y - half - 6 - lh;

                // По горизонтали: ровно по центру лупы.
                double lx = pos.X - lw / 2;

                // Не даём подписи выходить за края экрана (важно у боковых краёв).
                lx = Math.Clamp(lx, 4, Math.Max(4, Width - lw - 4));
                ly = Math.Clamp(ly, 4, Math.Max(4, Height - lh - 4));

                Canvas.SetLeft(label, lx);
                Canvas.SetTop(label, ly);
                label.IsVisible = true;
            }
        }

        private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            var props = e.GetCurrentPoint((Visual?)sender).Properties;
            if (props.IsRightButtonPressed)
            {
                Finish(null);
                return;
            }
            GetCursorPos(out var cp);
            Finish(ColorAt(cp.X - _vx, cp.Y - _vy));
        }

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) Finish(null);
        }

        private void Finish(Color? result)
        {
            var tcs = _tcs;
            _tcs = null;
            Close();
            tcs?.TrySetResult(result);
        }

        // ── Снимок экрана (Windows GDI) ───────────────────────────────────

        private bool CaptureScreen()
        {
            try
            {
                _vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
                _vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
                _vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
                _vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
                if (_vw <= 0 || _vh <= 0) return false;

                IntPtr hScreen = GetDC(IntPtr.Zero);
                IntPtr hMem = CreateCompatibleDC(hScreen);
                IntPtr hBmp = CreateCompatibleBitmap(hScreen, _vw, _vh);
                IntPtr old = SelectObject(hMem, hBmp);
                BitBlt(hMem, 0, 0, _vw, _vh, hScreen, _vx, _vy, SRCCOPY);

                var bmi = new BITMAPINFOHEADER
                {
                    biSize = 40,
                    biWidth = _vw,
                    biHeight = -_vh, // top-down
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0
                };
                _buffer = new byte[_vw * _vh * 4];
                GetDIBits(hMem, hBmp, 0, (uint)_vh, _buffer, ref bmi, 0);

                SelectObject(hMem, old);
                DeleteObject(hBmp);
                DeleteDC(hMem);
                ReleaseDC(IntPtr.Zero, hScreen);

                _bitmap = new WriteableBitmap(
                    new PixelSize(_vw, _vh), new Vector(96, 96),
                    PixelFormat.Bgra8888, AlphaFormat.Opaque);
                using (var fb = _bitmap.Lock())
                {
                    int srcStride = _vw * 4;
                    int dstStride = fb.RowBytes;
                    if (srcStride == dstStride)
                    {
                        Marshal.Copy(_buffer, 0, fb.Address, _buffer.Length);
                    }
                    else
                    {
                        for (int row = 0; row < _vh; row++)
                            Marshal.Copy(_buffer, row * srcStride,
                                IntPtr.Add(fb.Address, row * dstStride), srcStride);
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;
        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;
        private const int SRCCOPY = 0x00CC0020;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr hdc, int x, int y, int w, int h, IntPtr hdcSrc, int x1, int y1, int rop);
        [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);
    }
}
