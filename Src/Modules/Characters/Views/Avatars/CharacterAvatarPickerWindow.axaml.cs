using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Serilog;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Writersword.Modules.Characters.Interfaces;
using Writersword.Modules.Characters.ViewModels.Avatars;

namespace Writersword.Modules.Characters.Views.Avatars
{
    public partial class CharacterAvatarPickerWindow : Window
    {
        private static readonly ILogger _logger = Log.ForContext<CharacterAvatarPickerWindow>();

        private static readonly FilePickerFileType CharacterImageFileType = new("Изображения")
        {
            Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.webp" },
            MimeTypes = new[] { "image/jpeg", "image/png", "image/webp" }
        };

        private string? _selectedAvatarRef;

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
            var window = new CharacterAvatarPickerWindow { DataContext = vm };

            vm.AvatarSelected += ref_ => window._selectedAvatarRef = ref_;
            vm.CloseRequested += () => window.Close();

            vm.RequestFilePicker = async () =>
            {
                var files = await window.StorageProvider.OpenFilePickerAsync(
                    new FilePickerOpenOptions
                    {
                        Title = "Выберите изображение",
                        AllowMultiple = false,
                        FileTypeFilter = new[] { CharacterImageFileType }
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
    }
}