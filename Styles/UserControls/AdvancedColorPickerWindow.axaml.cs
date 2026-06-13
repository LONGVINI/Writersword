using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Writersword.Styles.UserControls
{
    public partial class AdvancedColorPickerWindow : Window
    {
        private readonly IScreenColorPicker _eyedropper = ScreenColorPicker.Create();
        private string? _result;
        private bool _syncing;

        public IReadOnlyList<string> Presets { get; } = new[]
        {
            "#F44336", "#E91E63", "#9C27B0", "#673AB7", "#3F51B5", "#2196F3",
            "#03A9F4", "#00BCD4", "#009688", "#4CAF50", "#8BC34A", "#FFEB3B",
            "#FFC107", "#FF9800", "#FF5722", "#795548", "#607D8B", "#9E9E9E",
            "#455A64", "#E07B39", "#37474F", "#212121", "#FFFFFF", "#BDBDBD"
        };

        public AdvancedColorPickerWindow()
        {
            InitializeComponent();
        }

        public static async Task<string?> ShowAsync(Window owner, Control? host, string initialHex, bool showPreview)
        {
            var w = new AdvancedColorPickerWindow();

            // Окно покрывает область модуля-хоста: затемнение и центр — по модулю.
            // Если хост не передан/не измерен — fallback на экран владельца.
            if (host is not null && host.Bounds.Width > 0 && host.Bounds.Height > 0)
            {
                w.Position = host.PointToScreen(new Point(0, 0));
                w.Width = host.Bounds.Width;
                w.Height = host.Bounds.Height;
            }
            else
            {
                var screen = owner.Screens.ScreenFromWindow(owner) ?? owner.Screens.Primary;
                if (screen is not null)
                {
                    w.Position = screen.Bounds.Position;
                    w.Width = screen.Bounds.Width / screen.Scaling;
                    w.Height = screen.Bounds.Height / screen.Scaling;
                }
            }

            var preview = w.FindControl<Control>("PreviewPanel");
            if (preview is not null) preview.IsVisible = showPreview;

            var eye = w.FindControl<Button>("EyedropperButton");
            if (eye is not null) eye.IsEnabled = w._eyedropper.IsSupported;

            Color c;
            try { c = Color.Parse(initialHex); }
            catch { c = Color.FromRgb(0x60, 0x7D, 0x8B); }

            w.SetColor(c, updateSliders: true, updateHex: true);

            await w.ShowDialog(owner);
            return w._result;
        }

        private void SetColor(Color c, bool updateSliders, bool updateHex)
        {
            _syncing = true;
            try
            {
                var swatch = this.FindControl<Border>("PreviewSwatch");
                if (swatch is not null) swatch.Background = new SolidColorBrush(c);

                if (updateSliders)
                {
                    var sr = this.FindControl<Slider>("SliderR"); if (sr is not null) sr.Value = c.R;
                    var sg = this.FindControl<Slider>("SliderG"); if (sg is not null) sg.Value = c.G;
                    var sb = this.FindControl<Slider>("SliderB"); if (sb is not null) sb.Value = c.B;
                }

                var lr = this.FindControl<TextBlock>("LabelR"); if (lr is not null) lr.Text = c.R.ToString();
                var lg = this.FindControl<TextBlock>("LabelG"); if (lg is not null) lg.Text = c.G.ToString();
                var lb = this.FindControl<TextBlock>("LabelB"); if (lb is not null) lb.Text = c.B.ToString();

                if (updateHex)
                {
                    var hb = this.FindControl<TextBox>("HexBox");
                    if (hb is not null) hb.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
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

        private Color CurrentColor()
        {
            var sr = this.FindControl<Slider>("SliderR");
            var sg = this.FindControl<Slider>("SliderG");
            var sb = this.FindControl<Slider>("SliderB");
            byte r = (byte)(sr?.Value ?? 0);
            byte g = (byte)(sg?.Value ?? 0);
            byte b = (byte)(sb?.Value ?? 0);
            return Color.FromRgb(r, g, b);
        }

        private void OnRgbChanged(object? sender, RangeBaseValueChangedEventArgs e)
        {
            if (_syncing) return;
            SetColor(CurrentColor(), updateSliders: false, updateHex: true);
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
            try { SetColor(Color.Parse(t), updateSliders: true, updateHex: false); }
            catch { /* некорректный ввод — игнорируем */ }
        }

        private void OnPresetClick(object? sender, RoutedEventArgs e)
        {
            if ((sender as Control)?.DataContext is string hex)
            {
                try { SetColor(Color.Parse(hex), updateSliders: true, updateHex: true); }
                catch { }
            }
        }

        private async void OnEyedropperClick(object? sender, RoutedEventArgs e)
        {
            if (!_eyedropper.IsSupported) return;

            var prev = WindowState;
            WindowState = WindowState.Minimized;

            Color? picked = null;
            try { picked = await _eyedropper.PickAsync(); }
            catch { picked = null; }

            WindowState = prev;
            Activate();

            if (picked is not null)
                SetColor(picked.Value, updateSliders: true, updateHex: true);
        }

        private void OnOkClick(object? sender, RoutedEventArgs e)
        {
            var c = CurrentColor();
            _result = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            Close();
        }

        private void OnCancelClick(object? sender, RoutedEventArgs e)
        {
            _result = null;
            Close();
        }

        private void OnCloseClick(object? sender, RoutedEventArgs e)
        {
            _result = null;
            Close();
        }

        private void OnScrimClick(object? sender, PointerPressedEventArgs e)
        {
            _result = null;
            Close();
        }
    }
}
