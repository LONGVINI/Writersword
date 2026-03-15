using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Writersword.Modules.TextEditor.ViewModels.StatusBar;

namespace Writersword.Modules.TextEditor.Views.StatusBar
{
    public partial class EditorStatusBarView : UserControl
    {
        public EditorStatusBarView()
        {
            InitializeComponent();

            this.AddHandler(PointerPressedEvent, OnTunnelPointerPressed, RoutingStrategies.Tunnel);

            var tb = this.FindControl<TextBox>("ZoomTextBox");
            if (tb is null) return;
            tb.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                ApplyZoomFromTextBox(tb);
                e.Handled = true;
            };
            tb.LostFocus += (_, _) =>
            {
                ApplyZoomFromTextBox(tb);
            };
            tb.GotFocus += (_, _) =>
            {
                if (DataContext is StatusBarViewModel vm)
                    tb.Text = vm.ZoomPercent.ToString();
            };
        }

        private void OnTunnelPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.Source is not Control ctrl) return;
            var btn = ctrl as Button ?? ctrl.FindAncestorOfType<Button>(includeSelf: true);
            if (btn is not null)
                ToolTip.SetIsOpen(btn, false);
        }

        private void ApplyZoomFromTextBox(TextBox tb)
        {
            if (DataContext is not StatusBarViewModel vm) return;
            string raw = (tb.Text ?? "").Replace("%", "").Trim();
            if (string.IsNullOrEmpty(raw))
            {
                tb.Text = $"{vm.ZoomPercent}%";
                return;
            }
            if (int.TryParse(raw, out int percent))
            {
                percent = percent < 25 ? 25 : percent > 500 ? 500 : percent;
                vm.Zoom = percent / 100.0;
            }
            tb.Text = $"{vm.ZoomPercent}%";
        }
    }
}