using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Serilog;
using System;
using System.IO;
using System.Threading.Tasks;
using Writersword.Modules.Characters.ViewModels.Tabs;
using Writersword.Modules.Characters.Views.Avatars;

namespace Writersword.Modules.Characters.Views.Card.Tabs
{
    public partial class CharacterBasicsTabView : UserControl
    {
        private static readonly ILogger _logger = Log.ForContext<CharacterBasicsTabView>();

        public CharacterBasicsTabView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object? sender, EventArgs e)
        {
            if (DataContext is not CharacterBasicsTabViewModel vm) return;

            vm.RequestPickerOpen = async () =>
            {
                var window = TopLevel.GetTopLevel(this) as Window;
                if (window == null || vm.AvatarService == null) return null;
                return await CharacterAvatarPickerWindow.ShowAsync(
                    window, vm.AvatarService, vm.CharacterId);
            };
        }
    }
}