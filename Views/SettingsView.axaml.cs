using Avalonia.Controls;
using Avalonia.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Writersword.ViewModels;

namespace Writersword.Views
{
    /// <summary>
    /// Окно настроек приложения
    /// </summary>
    public partial class SettingsView : Window
    {
        private readonly ILogger<SettingsView> _logger;

        public SettingsView()
        {
            _logger = App.Services.GetService<ILogger<SettingsView>>()!;
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
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

        /// <summary>Обработчик клика по вкладке</summary>
        private void Tab_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is Border border &&
                border.DataContext is SettingsTabItem tab &&
                DataContext is SettingsViewModel vm)
            {
                vm.SelectTab(tab);
            }
        }
    }
}