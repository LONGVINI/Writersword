using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Serilog;
using Writersword.Modules.Characters.Interfaces;
using Writersword.Modules.Characters.Models;
using Writersword.Modules.Characters.Services;

namespace Writersword.Modules.Characters.Views.Icons
{
    /// <summary>
    /// Приём картинки в значок — общий путь для всех мест, где значок
    /// ставят: окна значков, заголовка анкеты, картинки шаблона, редактора
    /// метки. Файл ложится в проект, растр после этого проходит через ту же
    /// обрезку, что аватарки, — из большой картинки или листа со спрайтами
    /// выбирается нужный кусок; вектор кадра не знает и идёт целиком.
    /// </summary>
    public static class IconImport
    {
        private static readonly ILogger _logger = Log.ForContext(typeof(IconImport));

        /// <summary>Расширения, которые годятся в значок: растр и вектор.</summary>
        public static readonly string[] Extensions =
        {
            ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".ico", ".svg"
        };

        public static readonly FilePickerFileType FileType = new("Картинки значка")
        {
            Patterns = Extensions.Select(e => "*" + e).ToArray()
        };

        /// <summary>Вьюмодель модуля, в котором лежит control: там библиотека значков и хранилище картинок.</summary>
        public static ViewModels.CharactersViewModel? ModuleOf(Control control) =>
            control.FindAncestorOfType<CharactersModuleView>()?.DataContext as ViewModels.CharactersViewModel;

        public static bool IsIconFile(string? name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var ext = Path.GetExtension(name).ToLowerInvariant();
            return Extensions.Contains(ext);
        }

        /// <summary>В перетаскиваемом есть файлы — показывать, что окно их примет.</summary>
        public static bool HasFiles(DragEventArgs e) => e.DataTransfer.Contains(DataFormat.File);

        /// <summary>Брошенные файлы, годные в значок, в том порядке, в каком их отдала система.</summary>
        public static List<IStorageFile> DroppedIconFiles(DragEventArgs e)
        {
            var result = new List<IStorageFile>();
            var files = e.DataTransfer.TryGetFiles();
            if (files == null) return result;

            foreach (var item in files)
                if (item is IStorageFile file && IsIconFile(file.Name))
                    result.Add(file);

            return result;
        }

        /// <summary>Выбрать файлы значков на диске. Пустой список — передумали.</summary>
        public static async Task<List<IStorageFile>> PickFilesAsync(Control host, string title, bool allowMultiple)
        {
            var storage = TopLevel.GetTopLevel(host)?.StorageProvider;
            if (storage == null) return new List<IStorageFile>();

            try
            {
                var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = title,
                    AllowMultiple = allowMultiple,
                    FileTypeFilter = new[] { FileType }
                });

                return files?.Where(f => IsIconFile(f.Name)).ToList() ?? new List<IStorageFile>();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Icon file pick failed");
                return new List<IStorageFile>();
            }
        }

        /// <summary>
        /// Положить файл в проект и, если это растр и просили, предложить
        /// кадр. Возвращает ссылку с кадром или null, если файл не прочитался.
        /// Одинаковая картинка второй раз в проект не кладётся: берётся уже
        /// лежащая, а различаются такие значки только кадром.
        /// </summary>
        public static async Task<string?> ImportAsync(Control host, ICharacterAvatarService avatars,
            IStorageFile file, bool crop)
        {
            try
            {
                await using var stream = await file.OpenReadAsync();
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer);
                return await ImportAsync(host, avatars, buffer.ToArray(), file.Name, crop);
            }
            catch (Exception ex)
            {
                // Бросить могут что угодно — папку, ярлык, недоступный файл.
                _logger.Error(ex, "Icon import failed: {Name}", file.Name);
                return null;
            }
        }

        public static async Task<string?> ImportAsync(Control host, ICharacterAvatarService avatars,
            byte[] data, string fileName, bool crop)
        {
            if (data == null || data.Length == 0 || !IsIconFile(fileName)) return null;

            var imageRef = avatars.FindStoredByContent(data)
                           ?? await avatars.SaveIconToProjectAsync(data, fileName);
            if (string.IsNullOrWhiteSpace(imageRef)) return null;

            return crop ? await CropAsync(host, avatars, imageRef, "Кадр значка") : imageRef;
        }

        /// <summary>
        /// Положить файлы в библиотеку значков. Один файл — с выбором кадра,
        /// несколько — целиком: спрашивать кадр у каждого из десятка брошенных
        /// значило бы превратить приём пачки в работу. Возвращает добавленные
        /// записи в порядке файлов.
        /// </summary>
        public static async Task<List<CharacterIconEntry>> AddToLibraryAsync(Control host,
            ICharacterAvatarService avatars, CharacterIconLibrary library, IReadOnlyList<IStorageFile> files)
        {
            var added = new List<CharacterIconEntry>();
            var crop = files.Count == 1;

            foreach (var file in files)
            {
                var imageRef = await ImportAsync(host, avatars, file, crop);
                if (imageRef == null) continue;

                var entry = library.Add(imageRef, Path.GetFileNameWithoutExtension(file.Name));
                if (entry != null) added.Add(entry);
            }

            return added;
        }

        /// <summary>
        /// Спросить кадр для картинки и вернуть ссылку с ним. Отказ от обрезки
        /// возвращает ссылку как есть: картинка показывается целиком, и это
        /// законный выбор, а не отмена всей затеи.
        /// </summary>
        public static async Task<string?> CropAsync(Control host, ICharacterAvatarService avatars,
            string imageRef, string title)
        {
            if (string.IsNullOrWhiteSpace(imageRef)) return imageRef;

            // Вектор рисуется целиком и кадра не знает.
            if (LabelIconImages.IsVector(imageRef)) return imageRef;

            var overlay = host.FindAncestorOfType<CharactersModuleView>()
                ?.FindControl<Avatars.CharacterAvatarCropOverlay>("AvatarCropOverlayControl");
            if (overlay == null) return imageRef;

            var bytes = avatars.LoadAvatarBytes(CharacterAvatarRef.BaseOf(imageRef));
            if (bytes == null) return imageRef;

            Avalonia.Media.Imaging.Bitmap? bitmap = null;
            try
            {
                using var stream = new MemoryStream(bytes);
                bitmap = new Avalonia.Media.Imaging.Bitmap(stream);

                var crops = await overlay.ShowAsync(
                    bitmap,
                    CharacterAvatarRef.CropOf(imageRef),
                    title,
                    null,
                    null,
                    false,
                    CharacterAvatarRef.RotationOf(imageRef));

                if (crops == null) return imageRef;

                return CharacterAvatarRef.Combine(
                    CharacterAvatarRef.BaseOf(imageRef),
                    crops.Circle,
                    CharacterAvatarRef.StripCropOf(imageRef),
                    crops.Rotation);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Icon crop failed for {Ref}", imageRef);
                return imageRef;
            }
            finally
            {
                bitmap?.Dispose();
            }
        }
    }
}
