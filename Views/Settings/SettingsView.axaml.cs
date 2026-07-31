using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Writersword.ViewModels.Settings;

namespace Writersword.Views.Settings
{
    /// <summary>
    /// ���� �������� ����������
    /// </summary>
    public partial class SettingsView : Window
    {
        private readonly ILogger<SettingsView> _logger;

        public SettingsView()
        {
            _logger = App.Services.GetService<ILogger<SettingsView>>()!;
            InitializeComponent();

            DataContextChanged += OnDataContextChanged;

            // Снятие фокуса щелчком мимо поля и завершение ввода по Enter
            // работают для всех окон через FocusReleaseBehavior — оно
            // подключено стилем в Styles/Controls.axaml.
        }

        private void OnDataContextChanged(object? sender, System.EventArgs e)
        {
            if (DataContext is SettingsViewModel vm)
            {
                vm.CloseRequested += () =>
                {
                    _logger.LogDebug("Settings closed");
                    Close();
                };
            }
        }

        /// <summary>���������� ����� �� �������</summary>
        private void Tab_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is Border border &&
                border.DataContext is SettingsTabItem tab &&
                DataContext is SettingsViewModel vm)
            {
                vm.SelectTab(tab);
            }
        }

        /// <summary>���������� ����� �� ��������� ������ � ����������� ��� ������������� ������</summary>
        private void SectionHeader_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is Border border &&
                border.DataContext is SettingsTabItem tab &&
                DataContext is SettingsViewModel vm)
            {
                vm.ToggleSection(tab);
            }
        }
    }
}