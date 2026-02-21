using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Writersword.Core.Enums;
using Writersword.Resources.Localization;
using Writersword.Src.Core.Interfaces.Services.UI;
using Writersword.Views;

namespace Writersword.Src.Infrastructure.Services.UI
{
    public class DialogService : IDialogService
    {
        private readonly ILogger<DialogService> _logger;
        private Window? _mainWindow;

        public DialogService()
        {
            _logger = App.Services.GetService<ILogger<DialogService>>()!;
        }

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

        public async Task<string?> SaveFileAsync(string? defaultFileName = null)
        {
            if (_mainWindow == null) return null;

            var file = await _mainWindow.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Project",
                DefaultExtension = "writersword",
                SuggestedFileName = defaultFileName ?? "Untitled",
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

        /// <summary>Показать сообщение пользователю</summary>
        public async Task ShowMessageAsync(string title, string message)
        {
            await ShowMessageAsync(title, message, MessageBoxType.Info, MessageBoxButtons.OK);
        }

        /// <summary>Показать сообщение с выбором типа и кнопок</summary>
        public async Task<MessageBoxResult> ShowMessageAsync(
            string title,
            string message,
            MessageBoxType type,
            MessageBoxButtons buttons)
        {
            if (_mainWindow == null)
            {
                _logger.LogWarning("ShowMessage: {Title} - {Message}", title, message);
                return MessageBoxResult.None;
            }

            var messageBox = new MessageBoxView(title, message, type, buttons);
            await messageBox.ShowDialog(_mainWindow);
            return messageBox.Result;
        }

        /// <summary>
        /// Показать диалог восстановления проекта из автосохранения
        /// </summary>
        public async Task<RecoveryDialogResult> ShowRecoveryDialogAsync(DateTime cacheDate, DateTime saveDate)
        {
            if (_mainWindow == null)
            {
                _logger.LogWarning("ShowRecoveryDialog: Cache={CacheDate}, Save={SaveDate}", cacheDate, saveDate);
                return RecoveryDialogResult.Cancel;
            }

            // Создаём диалог с датами
            var messageBox = new MessageBoxView(
                Strings.MessageBox_Recovery_Title,
                Strings.MessageBox_Recovery_Message,
                cacheDate,
                saveDate
            );

            await messageBox.ShowDialog(_mainWindow);

            // Преобразуем MessageBoxResult в RecoveryDialogResult
            return messageBox.Result switch
            {
                MessageBoxResult.Restore => RecoveryDialogResult.Restore,
                MessageBoxResult.OpenSaved => RecoveryDialogResult.OpenSaved,
                MessageBoxResult.Compare => RecoveryDialogResult.Compare,
                MessageBoxResult.Cancel => RecoveryDialogResult.Cancel,
                _ => RecoveryDialogResult.Cancel
            };
        }
    }
}