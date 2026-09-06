using Avalonia.Controls;
using Avalonia.Interactivity;
using Writersword.ViewModels.Sync;

namespace Writersword.Views.Sync
{
    public partial class DevicesView : Window
    {
        public DevicesView()
        {
            InitializeComponent();

            // Часы модели останавливаются вместе с окном: иначе они тикали бы до
            // выхода из программы, перебирая книги ради списка, которого никто не
            // видит.
            Closed += (_, _) => (DataContext as DevicesViewModel)?.Dispose();
        }

        private void Close_Click(object? sender, RoutedEventArgs e) => Close();
    }
}
