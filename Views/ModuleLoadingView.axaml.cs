using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Writersword.Views
{
    public partial class ModuleLoadingView : UserControl
    {
        public ModuleLoadingView()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}