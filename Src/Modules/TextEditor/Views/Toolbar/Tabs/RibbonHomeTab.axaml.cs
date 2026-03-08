using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Writersword.Modules.TextEditor.Views.Toolbar.Tabs
{
    public partial class RibbonHomeTab : UserControl
    {
        public RibbonHomeTab()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
