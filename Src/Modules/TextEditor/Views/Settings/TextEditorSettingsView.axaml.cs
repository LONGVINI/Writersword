using Avalonia.Controls;
using Avalonia.Input;
using System;
using System.Globalization;
using Writersword.Modules.TextEditor.ViewModels;

namespace Writersword.Modules.TextEditor.Views.Settings
{
    /// <summary>
    /// Code-behind для TextEditorSettingsView.
    /// Содержит ручную валидацию поля MonitorTextBox —
    /// TextBox используется вместо NumericUpDown из-за специфики формата (0.# дюймов).
    /// </summary>
    public partial class TextEditorSettingsView : UserControl
    {
        public TextEditorSettingsView()
        {
            InitializeComponent();
            DataContextChanged += (_, _) => WireMonitorTextBox();
            WireMonitorTextBox();
        }

        /// <summary>
        /// Привязывает MonitorTextBox к MonitorSizeInches.Value в ViewModel.
        /// Использует LostFocus и Enter для применения значения.
        /// </summary>
        private void WireMonitorTextBox()
        {
            var tb = this.FindControl<TextBox>("MonitorTextBox");
            if (tb is null) return;

            if (DataContext is TextEditorSettingsViewModel vm)
            {
                tb.Text = FormatInches(vm.MonitorSizeInches.Value);

                // Подписываемся на изменения значения извне (кнопки сброса в SettingRow)
                vm.MonitorSizeInches.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(vm.MonitorSizeInches.Value))
                        tb.Text = FormatInches(vm.MonitorSizeInches.Value);
                };
            }

            tb.LostFocus += (_, _) => ApplyMonitorValue(tb);
            tb.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                    ApplyMonitorValue(tb);
            };
            tb.GotFocus += (_, _) =>
            {
                if (DataContext is TextEditorSettingsViewModel vm2)
                    tb.Text = FormatInches(vm2.MonitorSizeInches.Value);
            };
        }

        /// <summary>
        /// Применяет введённое значение к MonitorSizeInches.Value.
        /// Принимает точку и запятую как разделитель дробной части.
        /// Значения вне диапазона 0–100 сбрасываются до 0.
        /// </summary>
        private void ApplyMonitorValue(TextBox tb)
        {
            if (DataContext is not TextEditorSettingsViewModel vm) return;

            string raw = (tb.Text ?? "").Trim().Replace(',', '.');
            if (!double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out double value)
                || value < 0 || value > 100)
                value = 0;

            value = Math.Round(value, 1);
            vm.MonitorSizeInches.Value = value;
            tb.Text = FormatInches(value);
        }

        /// <summary>Форматирует значение дюймов для отображения в TextBox.</summary>
        private static string FormatInches(double value) =>
            value.ToString("0.#", CultureInfo.InvariantCulture);
    }
}