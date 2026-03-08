using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Writersword.Modules.TextEditor.Views.Toolbar.Tabs
{
    public partial class RibbonLayoutTab : UserControl
    {
        public RibbonLayoutTab() { InitializeComponent(); }
        private void InitializeComponent() { AvaloniaXamlLoader.Load(this); }
    }
}
