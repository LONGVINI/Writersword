using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Writersword.Modules.Characters.ViewModels;

namespace Writersword.Modules.Characters.Views.Tabs
{
    public partial class CharactersListView : UserControl
    {
        public CharactersListView()
        {
            InitializeComponent();
            AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        }

        public void FocusSearch()
            => this.FindControl<TextBox>("SearchTextBox")?.Focus();

        private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (DataContext is not CharactersViewModel vm) return;

            var src = e.Source as Avalonia.Visual;
            while (src is not null)
            {
                if (src is Control c && c.DataContext is CharacterFolderViewModel folderVm)
                {
                    vm.ActiveFolderId = folderVm.FolderId;
                    return;
                }
                src = src.GetVisualParent();
            }
        }
    }
}