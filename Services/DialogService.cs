using Avalonia.Controls;
using Avalonia.Platform.Storage;
using System.Collections.Generic;
using System.Threading.Tasks;
using Writersword.Services.Interfaces;
using Writersword.Views;

namespace Writersword.Services
{
    public class DialogService : IDialogService
    {
        private Window? _mainWindow;

        public void SetMainWindow(Window mainWindow)
        {
            _mainWindow = mainWindow;
        }

        public async Task<string?> OpenFileAsync()
        {
            if (_mainWindow == null) return null;

            var files = await _mainWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open Project",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Writersword Project")
                    {
                        Patterns = new[] { "*.writersword" }
                    }
                }
            });

            return files.Count > 0 ? files[0].Path.LocalPath : null;
        }

        public async Task<string?> SaveFileAsync()
        {
            if (_mainWindow == null) return null;

            var file = await _mainWindow.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Project",
                DefaultExtension = "writersword",
                SuggestedFileName = "Untitled",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Writersword Project")
                    {
                        Patterns = new[] { "*.writersword" }
                    }
                }
            });

            return file?.Path.LocalPath;
        }

        /// <summary>ѕоказать сообщение пользователю</summary>
        public async Task ShowMessageAsync(string title, string message)
        {
            await ShowMessageAsync(title, message, MessageBoxType.Info, MessageBoxButtons.OK);
        }

        /// <summary>ѕоказать сообщение с выбором типа и кнопок</summary>
        public async Task<MessageBoxResult> ShowMessageAsync(
            string title,
            string message,
            MessageBoxType type,
            MessageBoxButtons buttons)
        {
            if (_mainWindow == null)
            {
                System.Console.WriteLine($"[DialogService] ShowMessage: {title} - {message}");
                return MessageBoxResult.None;
            }

            var messageBox = new MessageBoxView(title, message, type, buttons);
            await messageBox.ShowDialog(_mainWindow);
            return messageBox.Result;
        }
    }
}