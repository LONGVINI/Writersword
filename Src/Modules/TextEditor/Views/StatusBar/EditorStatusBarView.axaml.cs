using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Writersword.Modules.TextEditor.Views.StatusBar
{
    public partial class EditorStatusBarView : UserControl
    {
        public EditorStatusBarView() { InitializeComponent(); }
        private void InitializeComponent() { AvaloniaXamlLoader.Load(this); }
    }
}
