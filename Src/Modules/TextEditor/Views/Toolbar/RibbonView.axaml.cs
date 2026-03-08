using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Writersword.Modules.TextEditor.Views.Toolbar
{
    public partial class RibbonView : UserControl
    {
        public RibbonView()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
