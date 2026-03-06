using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Writersword.Views.Components.MenuBar.Sections
{
    public partial class MenuBarWorkspace : MenuItem
    {
        public MenuBarWorkspace()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}