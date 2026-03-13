using Avalonia.Controls;
using Avalonia.Input;
using Writersword.Modules.TextEditor.ViewModels.StatusBar;

namespace Writersword.Modules.TextEditor.Views.StatusBar
{
    public partial class EditorStatusBarView : UserControl
    {
        public EditorStatusBarView()
        {
            InitializeComponent();

            var tb = this.FindControl<TextBox>("ZoomTextBox");
            if (tb is null) return;

            // Применяем только по Enter
            tb.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                ApplyZoomFromTextBox(tb);
                e.Handled = true;
            };

            // Или когда фокус уходит
            tb.LostFocus += (_, _) =>
            {
                ApplyZoomFromTextBox(tb);
            };

            // При получении фокуса — показываем чистое число
            tb.GotFocus += (_, _) =>
            {
                if (DataContext is StatusBarViewModel vm)
                    tb.Text = vm.ZoomPercent.ToString();
            };
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
                // Клампим в диапазон
                percent = percent < 25 ? 25 : percent > 500 ? 500 : percent;
                vm.Zoom = percent / 100.0;
            }

            tb.Text = $"{vm.ZoomPercent}%";
        }
    }
}