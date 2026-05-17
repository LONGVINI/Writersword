using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Serilog;
using System;
using System.IO;
using System.Threading.Tasks;
using Writersword.Modules.Characters.Interfaces;
using Writersword.Modules.Characters.ViewModels.Avatars;
using Writersword.Modules.Characters.Views.Avatars;

namespace Writersword.Modules.Characters.Views.Avatars
{
    public partial class CharacterAvatarPickerWindow : Window
    {
        private static readonly ILogger _logger = Log.ForContext<CharacterAvatarPickerWindow>();

        private static readonly FilePickerFileType ImageFileType = new("Изображения")
        {
            Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.webp" },
            MimeTypes = new[] { "image/jpeg", "image/png", "image/webp" }
        };

        private string? _selectedAvatarRef;
        private ICharacterAvatarService? _avatarService;

        public CharacterAvatarPickerWindow()
        {
            InitializeComponent();
        }

        public static async Task<string?> ShowAsync(
            Window parent,
            ICharacterAvatarService avatarService,
            string characterId)
        {
            var vm = new CharacterAvatarPickerViewModel(avatarService, characterId);
            var window = new CharacterAvatarPickerWindow
            {
                DataContext = vm,
                _avatarService = avatarService
            };

            vm.AvatarSelected += ref_ => window._selectedAvatarRef = ref_;
            vm.CloseRequested += () => window.Close();
            vm.OpenManagerRequested += () => window.OpenManager();

            vm.RequestFilePicker = async () =>
            {
                var files = await window.StorageProvider.OpenFilePickerAsync(
                    new FilePickerOpenOptions
                    {
                        Title = "Выберите изображение",
                        AllowMultiple = false,
                        FileTypeFilter = new[] { ImageFileType }
                    });
                if (files.Count == 0) return null;
                var file = files[0];
                await using var stream = await file.OpenReadAsync();
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                return (ms.ToArray(), file.Name);
            };

            await window.ShowDialog(parent);
            return window._selectedAvatarRef;
        }

        private void OpenManager()
        {
            var slot = this.FindControl<ContentControl>("OverlaySlot");
            if (slot == null || slot.IsVisible) return;

            var managerVm = new CharacterAvatarPackManagerViewModel(_avatarService!);
            managerVm.CloseRequested += CloseManager;

            var overlay = new CharacterAvatarPackManagerOverlay { DataContext = managerVm };

            // Подключаем диалоги файлов к менеджеру
            var managerCodeBehind = overlay;
            // DataContextChanged в code-behind CharacterAvatarPackManagerOverlay сам настроит пикеры.

            slot.Content = overlay;
            slot.IsVisible = true;
        }

        private void CloseManager()
        {
            var slot = this.FindControl<ContentControl>("OverlaySlot");
            if (slot == null) return;
            slot.IsVisible = false;
            slot.Content = null;

            // Обновить пикер после закрытия менеджера
            if (DataContext is CharacterAvatarPickerViewModel vm)
                vm.Refresh();
        }
    }
}