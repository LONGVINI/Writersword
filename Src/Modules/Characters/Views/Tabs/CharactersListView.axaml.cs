using Avalonia.Controls;

namespace Writersword.Modules.Characters.Views.Tabs
{
    public partial class CharactersListView : UserControl
    {
        public CharactersListView() => InitializeComponent();

        public void FocusSearch()
            => this.FindControl<TextBox>("SearchTextBox")?.Focus();
    }
}
