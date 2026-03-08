using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Writersword.Modules.TextEditor.Views
{
    public partial class TextEditorView : UserControl
    {
        public TextEditorView() { InitializeComponent(); }
        private void InitializeComponent() { AvaloniaXamlLoader.Load(this); }
    }
}
