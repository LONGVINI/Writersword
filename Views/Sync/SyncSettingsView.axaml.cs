using Avalonia.Controls;
using Avalonia.Interactivity;
using Writersword.ViewModels.Sync;

namespace Writersword.Views.Sync
{
    public partial class SyncSettingsView : Window
    {
        public SyncSettingsView()
        {
            InitializeComponent();

            // Модель не знает про окно и закрывает его через обратный вызов:
            // иначе ссылка на Window уехала бы в слой моделей, где ей не место.
            DataContextChanged += (_, _) =>
            {
                if (DataContext is SyncSettingsViewModel vm)
                    vm.CloseRequested = Close;
            };
        }

        private void Close_Click(object? sender, RoutedEventArgs e) => Close();
    }
}
