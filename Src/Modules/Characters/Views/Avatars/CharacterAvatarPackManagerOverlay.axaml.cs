using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Writersword.Modules.Characters.ViewModels.Avatars;
using Writersword.Src.Modules.Characters.Resources;

namespace Writersword.Modules.Characters.Views.Avatars
{
    /// <summary>
    /// Папки с аватарками. Живёт на уровне модуля, показывается поверх выбора
    /// аватарки и несёт свой скрим.
    ///
    /// Размеры панели ограничиваются под размер модуля тем же способом, что у
    /// редактора цвета: наблюдатель Bounds ставит панели MaxWidth и MaxHeight,
    /// а середина окна прокручивается.
    ///
    /// Окно принимает брошенные картинки — они ложатся в выбранную папку, — а
    /// кнопка приёма архива принимает брошенный ZIP.
    /// </summary>
    public partial class CharacterAvatarPackManagerOverlay : UserControl
    {
        private static readonly ILogger _logger = Log.ForContext<CharacterAvatarPackManagerOverlay>();

        private static readonly FilePickerFileType ZipFileType =
            new("ZIP") { Patterns = new[] { "*.zip" } };

        private static readonly FilePickerFileType ImageFileType =
            new(CharactersStrings.FilePicker_ImagesFilter)
            {
                Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.webp" },
                MimeTypes = new[] { "image/jpeg", "image/png", "image/webp" }
            };

        public CharacterAvatarPackManagerOverlay()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;

            // Панель не должна вылезать за модуль: при сжатом окне она иначе
            // обрезается по краям вместе с кнопками.
            this.GetObservable(BoundsProperty).Subscribe(b =>
            {
                if (b.Width <= 0) return;
                ApplyPanelMetrics(b.Width, b.Height);
            });
        }

        private void ApplyPanelMetrics(double width, double height)
        {
            var panel = this.FindControl<Border>("ManagerPanel");
            if (panel is null) return;

            panel.MaxHeight = Math.Max(260, height - 48);
            panel.MaxWidth = Math.Min(720, Math.Max(160, width - 48));
            panel.Width = Math.Min(720, Math.Max(160, width - 48));
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
                        Title = "Выберите ZIP-архив с папкой аватарок",
                        AllowMultiple = false,
                        FileTypeFilter = new[] { ZipFileType }
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
                        Title = "Сохранить папку аватарок как ZIP",
                        SuggestedFileName = $"{packName}.zip",
                        FileTypeChoices = new[] { ZipFileType }
                    });
                return file?.Path.LocalPath;
            };

            vm.RequestImagePicker = async () =>
            {
                var result = new List<(byte[] data, string name)>();

                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null) return result;

                var files = await topLevel.StorageProvider.OpenFilePickerAsync(
                    new FilePickerOpenOptions
                    {
                        Title = CharactersStrings.FilePicker_SelectImageTitle,
                        AllowMultiple = true,
                        FileTypeFilter = new[] { ImageFileType }
                    });

                foreach (var file in files)
                {
                    try
                    {
                        await using var stream = await file.OpenReadAsync();
                        using var buffer = new MemoryStream();
                        await stream.CopyToAsync(buffer);
                        result.Add((buffer.ToArray(), file.Name));
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "Reading picked image failed: {Name}", file.Name);
                    }
                }

                return result;
            };
        }

        // ── Приём картинок всем окном ─────────────────────────────────────

        private void OnManagerDragOver(object? sender, DragEventArgs e)
        {
            var accepts = e.DataTransfer.Contains(DataFormat.File);
            e.DragEffects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
            SetDropTarget(accepts);
            e.Handled = true;
        }

        private void OnManagerDragLeave(object? sender, DragEventArgs e)
        {
            SetDropTarget(false);
            e.Handled = true;
        }

        private async void OnManagerDrop(object? sender, DragEventArgs e)
        {
            e.Handled = true;
            SetDropTarget(false);

            if (DataContext is not CharacterAvatarPackManagerViewModel vm) return;

            var files = e.DataTransfer.TryGetFiles();
            if (files == null) return;

            // В папку кладут запас, поэтому берётся вся брошенная пачка, а не
            // одна картинка: здесь у выбора нет единственного результата.
            foreach (var file in files)
            {
                if (file is not IStorageFile storageFile) continue;
                if (!CharacterAvatarPickerOverlay.IsDroppableImage(storageFile.Name)) continue;

                try
                {
                    await using var stream = await storageFile.OpenReadAsync();
                    using var buffer = new MemoryStream();
                    await stream.CopyToAsync(buffer);

                    await vm.HandleDroppedImageAsync(buffer.ToArray(), storageFile.Name);
                }
                catch (Exception ex)
                {
                    // Бросить могут что угодно — папку, ярлык, недоступный файл.
                    _logger.Error(ex, "Pack manager drop failed: {Name}", storageFile.Name);
                }
            }
        }

        // ── Приём архива кнопкой ──────────────────────────────────────────
        //
        // Кнопка приёма ZIP объявлена приёмником сама. Событие Drop всплывает,
        // и обработчик кнопки успевает пометить его обработанным раньше, чем
        // до него доберётся обработчик всего окна — иначе архив ушёл бы в
        // общий разбор, где ждут только картинки.

        private void OnZipDragOver(object? sender, DragEventArgs e)
        {
            var accepts = e.DataTransfer.Contains(DataFormat.File);
            e.DragEffects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
            SetZipArmed(accepts);
            SetDropTarget(false);
            e.Handled = true;
        }

        private void OnZipDragLeave(object? sender, DragEventArgs e)
        {
            SetZipArmed(false);
            e.Handled = true;
        }

        private async void OnZipDrop(object? sender, DragEventArgs e)
        {
            e.Handled = true;
            SetZipArmed(false);
            SetDropTarget(false);

            if (DataContext is not CharacterAvatarPackManagerViewModel vm) return;

            var files = e.DataTransfer.TryGetFiles();
            if (files == null) return;

            foreach (var file in files)
            {
                if (file is not IStorageFile storageFile) continue;

                var path = storageFile.Path.LocalPath;
                if (string.IsNullOrEmpty(path)) continue;
                if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;

                try { await vm.HandleDroppedZipAsync(path); }
                catch (Exception ex) { _logger.Error(ex, "Zip drop failed: {Path}", path); }

                return;
            }

            vm.StatusMessage = "На эту кнопку бросают ZIP-архив с папкой аватарок.";
        }

        private void SetZipArmed(bool value)
        {
            var button = this.FindControl<Button>("ImportZipButton");
            if (button == null) return;

            if (value)
            {
                if (!button.Classes.Contains("dropArmed")) button.Classes.Add("dropArmed");
            }
            else
            {
                button.Classes.Remove("dropArmed");
            }
        }

        private void SetDropTarget(bool value)
        {
            if (DataContext is CharacterAvatarPackManagerViewModel vm)
                vm.IsDropTarget = value;
        }

        // Скрим блокирует модуль, но окно не закрывает — как в редакторе цвета.
        private void OnScrimPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;
    }
}
