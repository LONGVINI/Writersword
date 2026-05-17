using Avalonia.Controls;
using Avalonia.Platform.Storage;
using System;
using System.Threading.Tasks;
using Writersword.Modules.Characters.ViewModels.Avatars;

namespace Writersword.Modules.Characters.Views.Avatars
{
    public partial class CharacterAvatarPackManagerOverlay : UserControl
    {
        public CharacterAvatarPackManagerOverlay()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object? sender, EventArgs e)
        {
            if (DataContext is not CharacterAvatarPackManagerViewModel vm) return;

            vm.RequestZipImportPicker = async () =>
            {
                var window = TopLevel.GetTopLevel(this) as Window;
                if (window == null) return null;
                var files = await window.StorageProvider.OpenFilePickerAsync(
                    new FilePickerOpenOptions
                    {
                        Title = "Выберите ZIP-файл пака",
                        AllowMultiple = false,
                        FileTypeFilter = new[] { new FilePickerFileType("ZIP") { Patterns = new[] { "*.zip" } } }
                    });
                return files.Count > 0 ? files[0].Path.LocalPath : null;
            };

            vm.RequestZipExportPicker = async (packName) =>
            {
                var window = TopLevel.GetTopLevel(this) as Window;
                if (window == null) return null;
                var file = await window.StorageProvider.SaveFilePickerAsync(
                    new FilePickerSaveOptions
                    {
                        Title = "Сохранить пак как ZIP",
                        SuggestedFileName = $"{packName}.zip",
                        FileTypeChoices = new[] { new FilePickerFileType("ZIP") { Patterns = new[] { "*.zip" } } }
                    });
                return file?.Path.LocalPath;
            };
        }
    }
}