using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
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
                if (vm.AvatarService == null) return null;

                // Выбор аватара — оверлей по центру модуля (как редактор цвета),
                // а не отдельное системное окно. Кнопок Upload/Delete под аватаром
                // больше нет: удаление доступно кнопкой внутри пикера, действие
                // передаётся только когда аватар есть.
                var host = this.FindAncestorOfType<CharactersModuleView>();
                var overlay = host?.FindControl<CharacterAvatarPickerOverlay>("AvatarPickerOverlayControl");
                if (overlay != null)
                {
                    Action? deleteAction = string.IsNullOrEmpty(vm.AvatarPath)
                        ? null
                        : () => vm.DeleteAvatarCommand.Execute().Subscribe();
                    return await overlay.ShowAsync(vm.AvatarService, vm.CharacterId, deleteAction);
                }

                // Запасной путь, если вью показана вне модуля: прежнее окно.
                var window = TopLevel.GetTopLevel(this) as Window;
                if (window == null) return null;
                return await CharacterAvatarPickerWindow.ShowAsync(
                    window, vm.AvatarService, vm.CharacterId);
            };
        }
    }
}