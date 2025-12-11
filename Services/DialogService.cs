using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Writersword.Services.Interfaces;

namespace Writersword.Services
{
    /// <summary>
    /// Реализация сервиса диалоговых окон
    /// </summary>
    public class DialogService : IDialogService
    {
        private Window? _mainWindow;

        /// <summary>
        /// Установить главное окно (нужно для показа диалогов поверх него)
        /// </summary>
        public void SetMainWindow(Window window)
        {
            _mainWindow = window;
        }

        /// <summary>Показать диалог "Открыть файл"</summary>
        public async Task<string?> OpenFileAsync()
        {
            if (_mainWindow == null) return null;

            // StorageProvider - новый API Avalonia для работы с файлами
            var files = await _mainWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open Writersword Project",
                AllowMultiple = false, // Только один файл
                FileTypeFilter = new List<FilePickerFileType>
                {
                    new("Writersword Project") { Patterns = new[] { "*.writersword" } },
                    new("All Files") { Patterns = new[] { "*.*" } }
                }
            });

            // Возвращаем путь к выбранному файлу (или null если отменили)
            return files.FirstOrDefault()?.Path.LocalPath;
        }

        /// <summary>Показать диалог "Сохранить файл"</summary>
        public async Task<string?> SaveFileAsync()
        {
            if (_mainWindow == null) return null;

            try
            {
                // Получаем путь по умолчанию
                var settingsService = App.Services.GetRequiredService<ISettingsService>();
                var defaultFolder = settingsService.DefaultProjectsFolder;

                IStorageFolder? suggestedFolder = null;

                // Пытаемся получить папку, если путь корректен
                if (!string.IsNullOrEmpty(defaultFolder) && System.IO.Directory.Exists(defaultFolder))
                {
                    suggestedFolder = await _mainWindow.StorageProvider.TryGetFolderFromPathAsync(defaultFolder);
                }

                var file = await _mainWindow.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Save Writersword Project",
                    DefaultExtension = "writersword",
                    SuggestedFileName = "Untitled",
                    SuggestedStartLocation = suggestedFolder,
                    FileTypeChoices = new List<FilePickerFileType>
                    {
                        new("Writersword Project") { Patterns = new[] { "*.writersword" } }
                    }
                });

                return file?.Path.LocalPath;
            }
            catch (System.Exception ex)
            {
                System.Console.WriteLine($"SaveFileAsync error: {ex.Message}");

                // Если ошибка - показываем диалог без suggested folder
                var file = await _mainWindow.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Save Writersword Project",
                    DefaultExtension = "writersword",
                    SuggestedFileName = "Untitled",
                    FileTypeChoices = new List<FilePickerFileType>
                    {
                        new("Writersword Project") { Patterns = new[] { "*.writersword" } }
                    }
                });

                return file?.Path.LocalPath;
            }
        }

        /// <summary>Показать простое окно с сообщением</summary>
        public async Task ShowMessageAsync(string title, string message)
        {
            if (_mainWindow == null) return;

            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                // Создаём простое модальное окно
                var dialog = new Window
                {
                    Title = title,
                    Width = 400,
                    Height = 200,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner, // По центру родителя
                    CanResize = false,
                    Content = new StackPanel
                    {
                        Margin = new Avalonia.Thickness(20),
                        Children =
                        {
                            new TextBlock
                            {
                                Text = message,
                                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                                Margin = new Avalonia.Thickness(0, 0, 0, 20)
                            },
                            new Button
                            {
                                Content = "OK",
                                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                                Padding = new Avalonia.Thickness(30, 10)
                            }
                        }
                    }
                };

                // Привязываем закрытие окна к кнопке OK
                if (dialog.Content is StackPanel panel && panel.Children[1] is Button okButton)
                {
                    okButton.Click += (s, e) => dialog.Close();
                }

                await dialog.ShowDialog(_mainWindow);
            }, DispatcherPriority.Normal);
        }
    }
}