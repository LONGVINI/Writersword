using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Writersword.Views.Components.MenuBar.Sections
{
    public partial class MenuBarViewSection : MenuItem
    {
        public MenuBarViewSection()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}