using Serilog;
using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using Writersword.Core.Interfaces.WorkFlows;
using Writersword.Core.Services;

namespace Writersword.Modules.TextEditor.Models.Settings
{
    /// <summary>
    /// Картинки вида чтения: бумага и поле вокруг книги.
    ///
    /// Раньше вид хранил путь к файлу на диске — «E:\Фоны\бумага.jpg». У автора
    /// такая книга выглядела как задумано, а у того, кому проект передали, на
    /// месте бумаги оказывался ровный цвет: файла по этому пути у него нет и
    /// быть не может. Ни сообщения, ни следа — просто другая книга.
    ///
    /// Поэтому картинка не берётся с диска, а лежит в одном из двух хранилищ, и
    /// вид ссылается на неё коротким адресом:
    ///
    ///     project:9f3c…8a.jpg — в архиве проекта, уезжает вместе с ним;
    ///     app:9f3c…8a.jpg     — в данных программы, общая для всех проектов;
    ///     E:\Фоны\бумага.jpg  — прежний вид записи, читается по-старому.
    ///
    /// Хранилища два, потому что областей у вида тоже две. Вид «в документе»
    /// уезжает с рукописью, и его картинка обязана лежать в архиве. Вид «везде»
    /// живёт в настройках программы и переживает удаление проекта — его картинке
    /// место в данных программы. Один и тот же вид может числиться в обеих
    /// областях: тогда у каждой его копии свой адрес картинки и своя копия
    /// файла. Это не расточительство: копия в проекте — единственное, что
    /// доедет до чужой машины, а копия в программе — единственное, что переживёт
    /// удаление проекта.
    ///
    /// Имя файла выводится из его содержимого. Одна и та же картинка, выбранная
    /// дважды или назначенная двум видам, ложится в хранилище один раз.
    /// </summary>
    public static class ReadingAssets
    {
        private static readonly ILogger _logger = Log.ForContext(typeof(ReadingAssets));

        /// <summary>Приставка адреса в архиве проекта.</summary>
        public const string ProjectPrefix = "project:";

        /// <summary>Приставка адреса в данных программы.</summary>
        public const string AppPrefix = "app:";

        /// <summary>Папка картинок вида внутри архива проекта.</summary>
        private const string ZipFolder = "TextEditor/Reading";

        /// <summary>Папка картинок вида в данных программы.</summary>
        private static string AppFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Writersword", "Reading");

        /// <summary>Форматы, которые годятся картинке вида.</summary>
        private static readonly string[] AllowedExtensions =
            { ".jpg", ".jpeg", ".png", ".webp", ".bmp" };

        // ── Разбор адреса ─────────────────────────────────────────────────

        public static bool IsProjectRef(string? reference)
            => !string.IsNullOrEmpty(reference)
               && reference!.StartsWith(ProjectPrefix, StringComparison.Ordinal);

        public static bool IsAppRef(string? reference)
            => !string.IsNullOrEmpty(reference)
               && reference!.StartsWith(AppPrefix, StringComparison.Ordinal);

        /// <summary>
        /// Прежняя запись: путь к файлу на диске. Такие адреса читаются, но с
        /// проектом никуда не едут — их место занимает укладка в хранилище.
        /// </summary>
        public static bool IsDiskPath(string? reference)
            => !string.IsNullOrWhiteSpace(reference)
               && !IsProjectRef(reference)
               && !IsAppRef(reference);

        /// <summary>Картинка уедет вместе с проектом.</summary>
        public static bool TravelsWithProject(string? reference)
            => string.IsNullOrWhiteSpace(reference) || IsProjectRef(reference);

        /// <summary>Имя файла внутри хранилища, без приставки.</summary>
        private static string NameOf(string reference)
            => Path.GetFileName(reference[(reference.IndexOf(':') + 1)..]);

        // ── Чтение ────────────────────────────────────────────────────────

        /// <summary>
        /// Байты картинки по адресу. null — адреса нет, файла нет или прочитать
        /// его не удалось. Отличать эти случаи вызывающей стороне незачем: во
        /// всех трёх показывать нечего, а сказать об этом человеку должна
        /// проверка проекта, а не отрисовщик страницы.
        /// </summary>
        public static byte[]? Read(string? reference)
        {
            if (string.IsNullOrWhiteSpace(reference)) return null;

            try
            {
                if (IsProjectRef(reference))
                    return Context?.ReadFile($"{ZipFolder}/{NameOf(reference!)}");

                if (IsAppRef(reference))
                {
                    var path = Path.Combine(AppFolder, NameOf(reference!));
                    return File.Exists(path) ? File.ReadAllBytes(path) : null;
                }

                return File.Exists(reference) ? File.ReadAllBytes(reference) : null;
            }
            catch (Exception ex)
            {
                // Молча возвращать null нельзя: на экране будет ровный цвет вместо
                // бумаги, и почему — не скажет ничего и никто.
                _logger.Warning(ex, "Failed to read the reading theme image: {Ref}", reference);
                return null;
            }
        }

        /// <summary>Картинка по адресу читается.</summary>
        public static bool Exists(string? reference)
        {
            if (string.IsNullOrWhiteSpace(reference)) return false;

            try
            {
                if (IsProjectRef(reference))
                    return Context?.FileExists($"{ZipFolder}/{NameOf(reference!)}") == true;

                if (IsAppRef(reference))
                    return File.Exists(Path.Combine(AppFolder, NameOf(reference!)));

                return File.Exists(reference);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to verify the reading theme image: {Ref}", reference);
                return false;
            }
        }

        // ── Укладка ───────────────────────────────────────────────────────

        /// <summary>
        /// Уложить картинку в архив проекта и вернуть её адрес. Уже лежащая в
        /// архиве возвращается как есть; та, которую прочитать нечем, тоже —
        /// подменять её пустотой нельзя, иначе адрес потеряется и разбираться,
        /// чего не хватало, будет уже не по чему.
        /// </summary>
        public static string? EnsureInProject(string? reference)
        {
            if (string.IsNullOrWhiteSpace(reference)) return reference;
            if (IsProjectRef(reference)) return reference;

            var context = Context;
            if (context is null) return reference;

            var bytes = Read(reference);
            if (bytes is null || bytes.Length == 0) return reference;

            var name = StoredName(bytes, reference!);
            var path = $"{ZipFolder}/{name}";

            try
            {
                if (!context.FileExists(path))
                {
                    context.WriteFile(path, bytes);
                    context.FlushStorage();
                    _logger.Debug("Reading theme image stored in the project: {Name} ({Size} bytes)",
                        name, bytes.Length);
                }
                return ProjectPrefix + name;
            }
            catch (Exception ex)
            {
                // Адрес остаётся прежним: он хотя бы работает здесь и сейчас.
                // О том, что картинка так и лежит снаружи, скажет проверка
                // проекта перед передачей.
                _logger.Error(ex, "Failed to store the reading theme image in the project: {Ref}", reference);
                return reference;
            }
        }

        /// <summary>
        /// Уложить картинку в данные программы и вернуть её адрес. Нужно видам
        /// со снятой областью «в документе»: такой вид переживает и закрытие
        /// проекта, и его удаление, а картинка, оставленная в архиве, — нет.
        /// </summary>
        public static string? EnsureInAppStore(string? reference)
        {
            if (string.IsNullOrWhiteSpace(reference)) return reference;
            if (IsAppRef(reference)) return reference;

            var bytes = Read(reference);
            if (bytes is null || bytes.Length == 0) return reference;

            try
            {
                Directory.CreateDirectory(AppFolder);

                var name = StoredName(bytes, reference!);
                var path = Path.Combine(AppFolder, name);
                if (!File.Exists(path))
                {
                    File.WriteAllBytes(path, bytes);
                    _logger.Debug("Reading theme image stored in application data: {Name} ({Size} bytes)",
                        name, bytes.Length);
                }

                return AppPrefix + name;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to store the reading theme image in application data: {Ref}",
                    reference);
                return reference;
            }
        }

        /// <summary>
        /// Имя файла в хранилище: отпечаток содержимого и прежнее расширение.
        ///
        /// По содержимому, а не по исходному имени: одна и та же картинка,
        /// выбранная дважды, должна лечь один раз, а два разных файла с именем
        /// «фон.jpg» — не затереть друг друга.
        /// </summary>
        private static string StoredName(byte[] data, string reference)
        {
            var hash = Convert.ToHexString(SHA256.HashData(data))
                .ToLower(CultureInfo.InvariantCulture);

            var ext = Path.GetExtension(
                IsProjectRef(reference) || IsAppRef(reference)
                    ? NameOf(reference)
                    : reference).ToLowerInvariant();

            if (Array.IndexOf(AllowedExtensions, ext) < 0) ext = ".png";

            return hash[..16] + ext;
        }

        /// <summary>
        /// Хранилище файлов открытого проекта. Может отсутствовать: вид правят и
        /// тогда, когда ни один проект не открыт.
        /// </summary>
        private static DocumentContext? Context
            => CoreServices.GetService<ITabCollection>()?.ActiveTab?.Context;
    }
}
