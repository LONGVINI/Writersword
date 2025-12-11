using Avalonia.Controls;
using Avalonia.Input;
using Writersword.ViewModels;
using System.ComponentModel;

namespace Writersword.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            // Обработчик попытки закрытия окна
            Closing += OnClosing;
        }

        /// <summary>Обработчик клика по вкладке</summary>
        private void Tab_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is Border border && border.DataContext is DocumentTabViewModel tab)
            {
                if (DataContext is MainWindowViewModel vm)
                {
                    vm.ActivateTab(tab);
                }
            }
        }

        /// <summary>Обработчик попытки закрытия главного окна</summary>
        private async void OnClosing(object? sender, CancelEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                // Если нет открытых вкладок - отменяем закрытие и показываем Welcome
                if (vm.OpenTabs.Count == 0)
                {
                    e.Cancel = true;
                    await Writersword.App.ShowWelcomeScreen(this);
                }
            }
        }
    }
}