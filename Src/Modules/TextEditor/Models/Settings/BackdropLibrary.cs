using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog;
using Writersword.Core.Interfaces.WorkFlows;
// DocumentContext лежит в Core.Services, хотя файл его — в папке Models/Documents.
using Writersword.Core.Services;

namespace Writersword.Modules.TextEditor.Models.Settings
{
    /// <summary>Где живёт папка фонов.</summary>
    public enum BackdropScope
    {
        /// <summary>В архиве проекта: уезжает вместе с рукописью, и её увидят другие.</summary>
        Local = 0,
        /// <summary>В данных программы: одна на все проекты, переживает их удаление.</summary>
        Global = 1
    }

    /// <summary>Картинка фона в папке.</summary>
    public sealed class BackdropItem
    {
        public BackdropItem(string reference, string fileName, string packId, BackdropScope scope)
        {
            Reference = reference;
            FileName = fileName;
            PackId = packId;
            Scope = scope;
        }

        /// <summary>Адрес картинки — им она и запоминается в виде.</summary>
        public string Reference { get; }

        /// <summary>Имя файла: оно же подпись на плитке.</summary>
        public string FileName { get; }

        public string PackId { get; }

        public BackdropScope Scope { get; }
    }

    /// <summary>Папка фонов: имя, область хранения и картинки.</summary>
    public sealed class BackdropPack
    {
        public BackdropPack(string id, string name, BackdropScope scope, bool isRecent = false)
        {
            Id = id;
            Name = name;
            Scope = scope;
            IsRecent = isRecent;
        }

        public string Id { get; }

        public string Name { get; internal set; }

        public BackdropScope Scope { get; }

        /// <summary>
        /// Папка «Недавние». Заводится сама, лежит в проекте и принимает всё, что
        /// выбрано файловым окном мимо папок. Ни переименовать, ни удалить её
        /// нельзя: это не выбор человека, а место, куда всегда что-то кладётся.
        /// </summary>
        public bool IsRecent { get; }

        public List<BackdropItem> Items { get; } = new();

        public bool CanRename => !IsRecent;
        public bool CanDelete => !IsRecent;
    }

    /// <summary>
    /// Хранилище картинок фона: папки, их содержимое и чтение картинок.
    ///
    /// Ссылок на папки где-то на диске здесь нет намеренно. Человек переложит
    /// картинку или почистит загрузки — и фон пропадёт, ничего об этом не сказав.
    /// Поэтому картинка всегда копируется внутрь: глобальная папка ложится в данные
    /// программы и одинакова во всех проектах, локальная — в архив проекта и
    /// уезжает вместе с рукописью, чтобы её увидел тот, кому проект передали.
    ///
    /// Адрес картинки несёт в себе и область, и папку:
    ///   bgapp:&lt;папка&gt;/&lt;файл&gt;   — данные программы,
    ///   bgproj:&lt;папка&gt;/&lt;файл&gt;  — архив проекта.
    /// </summary>
    public static class BackdropLibrary
    {
        private static readonly ILogger _logger = Log.ForContext(typeof(BackdropLibrary));

        public const string AppPrefix = "bgapp:";
        public const string ProjectPrefix = "bgproj:";

        /// <summary>Папка «Недавние» — одна на проект, с постоянным опознавателем.</summary>
        public const string RecentPackId = "recent";

        public const string RecentPackName = "Недавние";

        /// <summary>Папка фонов внутри архива проекта.</summary>
        private const string ZipRoot = "TextEditor/Backdrops";

        /// <summary>Папка фонов в данных программы.</summary>
        private static string AppRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Writersword", "Backdrops");

        private static readonly string[] AllowedExtensions =
            { ".jpg", ".jpeg", ".png", ".webp", ".bmp" };

        /// <summary>Архив открытого проекта. Нет проекта — нет и локальных папок.</summary>
        private static DocumentContext? Context
            => CoreServices.GetService<ITabCollection>()?.ActiveTab?.Context;

        // ── Адреса ────────────────────────────────────────────────────────

        public static bool IsAppRef(string? reference)
            => !string.IsNullOrEmpty(reference)
               && reference!.StartsWith(AppPrefix, StringComparison.Ordinal);

        public static bool IsProjectRef(string? reference)
            => !string.IsNullOrEmpty(reference)
               && reference!.StartsWith(ProjectPrefix, StringComparison.Ordinal);

        public static bool IsLibraryRef(string? reference)
            => IsAppRef(reference) || IsProjectRef(reference);

        private static string TailOf(string reference)
            => reference[(reference.IndexOf(':') + 1)..];

        private static string MakeRef(BackdropScope scope, string packId, string fileName)
            => (scope == BackdropScope.Global ? AppPrefix : ProjectPrefix) + packId + "/" + fileName;

        // ── Чтение ────────────────────────────────────────────────────────

        /// <summary>
        /// Байты картинки по адресу. null — адреса нет, файла нет или прочитать его
        /// не удалось. Отличать эти случаи вызывающей стороне незачем: показывать
        /// во всех трёх нечего.
        /// </summary>
        public static byte[]? Read(string? reference)
        {
            if (string.IsNullOrWhiteSpace(reference)) return null;

            try
            {
                if (IsAppRef(reference))
                {
                    var path = Path.Combine(AppRoot, TailOf(reference!).Replace('/', Path.DirectorySeparatorChar));
                    return File.Exists(path) ? File.ReadAllBytes(path) : null;
                }

                if (IsProjectRef(reference))
                    return Context?.ReadFile($"{ZipRoot}/{TailOf(reference!)}");

                // Прежние адреса видов чтения и пути на диске: фон мог быть выбран
                // до появления папок, и терять его из-за этого нельзя.
                return ReadingAssets.Read(reference);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to read the backdrop image: {Ref}", reference);
                return null;
            }
        }

        // ── Папки ─────────────────────────────────────────────────────────

        /// <summary>
        /// Все папки: сперва «Недавние», затем локальные проекта, затем глобальные.
        /// Порядок постоянный — по нему человек и ищет глазами.
        /// </summary>
        public static IReadOnlyList<BackdropPack> Packs()
        {
            var result = new List<BackdropPack>();

            var local = LoadLocalPacks();
            var recent = local.FirstOrDefault(p => p.IsRecent)
                         ?? new BackdropPack(RecentPackId, RecentPackName, BackdropScope.Local, isRecent: true);

            FillItems(recent);
            result.Add(recent);

            foreach (var pack in local.Where(p => !p.IsRecent).OrderBy(p => p.Name, StringComparer.CurrentCulture))
            {
                FillItems(pack);
                result.Add(pack);
            }

            foreach (var pack in LoadGlobalPacks().OrderBy(p => p.Name, StringComparer.CurrentCulture))
            {
                FillItems(pack);
                result.Add(pack);
            }

            return result;
        }

        /// <summary>Заводит папку и возвращает её. Пустое имя — папка «Без имени».</summary>
        public static BackdropPack? CreatePack(string name, BackdropScope scope)
        {
            string id = Guid.NewGuid().ToString("N")[..12];
            string clean = string.IsNullOrWhiteSpace(name) ? "Без имени" : name.Trim();

            try
            {
                if (scope == BackdropScope.Global)
                {
                    Directory.CreateDirectory(Path.Combine(AppRoot, id));
                    var packs = LoadGlobalPacks();
                    var pack = new BackdropPack(id, clean, BackdropScope.Global);
                    packs.Add(pack);
                    SaveGlobalNames(packs);
                    return pack;
                }

                if (Context is null)
                {
                    // Локальная папка живёт в архиве проекта, и без него её негде
                    // держать. Молча заводить глобальную вместо неё нельзя: человек
                    // просил папку, которая уедет с рукописью.
                    _logger.Warning("Cannot create a local backdrop folder: no project is open");
                    return null;
                }

                var localPacks = LoadLocalPacks();
                var local = new BackdropPack(id, clean, BackdropScope.Local);
                localPacks.Add(local);
                SaveLocalNames(localPacks);
                return local;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to create a backdrop folder: {Name}", clean);
                return null;
            }
        }

        public static void RenamePack(BackdropPack pack, string name)
        {
            if (pack is null || !pack.CanRename) return;
            if (string.IsNullOrWhiteSpace(name)) return;

            pack.Name = name.Trim();

            if (pack.Scope == BackdropScope.Global)
            {
                var packs = LoadGlobalPacks();
                var found = packs.FirstOrDefault(p => p.Id == pack.Id);
                if (found is not null) found.Name = pack.Name;
                SaveGlobalNames(packs);
            }
            else
            {
                var packs = LoadLocalPacks();
                var found = packs.FirstOrDefault(p => p.Id == pack.Id);
                if (found is not null) found.Name = pack.Name;
                else packs.Add(pack);
                SaveLocalNames(packs);
            }
        }

        /// <summary>Удаляет папку вместе с картинками. «Недавние» не удаляются.</summary>
        public static void DeletePack(BackdropPack pack)
        {
            if (pack is null || !pack.CanDelete) return;

            try
            {
                if (pack.Scope == BackdropScope.Global)
                {
                    var dir = Path.Combine(AppRoot, pack.Id);
                    if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);

                    var packs = LoadGlobalPacks();
                    packs.RemoveAll(p => p.Id == pack.Id);
                    SaveGlobalNames(packs);
                    return;
                }

                var context = Context;
                if (context is not null)
                {
                    foreach (var file in ProjectFilesOf(pack.Id))
                        context.DeleteFile(file);
                    context.FlushStorage();
                }

                var localPacks = LoadLocalPacks();
                localPacks.RemoveAll(p => p.Id == pack.Id);
                SaveLocalNames(localPacks);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to delete the backdrop folder: {Id}", pack.Id);
            }
        }

        // ── Картинки ──────────────────────────────────────────────────────

        /// <summary>
        /// Кладёт файл с диска в папку и возвращает картинку. Уже лежащая там
        /// (тот же файл по содержимому) не удваивается — возвращается прежняя.
        /// </summary>
        public static BackdropItem? Import(string diskPath, BackdropPack pack)
        {
            if (pack is null || string.IsNullOrWhiteSpace(diskPath)) return null;

            try
            {
                if (!File.Exists(diskPath)) return null;

                var data = File.ReadAllBytes(diskPath);
                if (data.Length == 0) return null;

                string name = StoredName(data, diskPath);

                if (pack.Scope == BackdropScope.Global)
                {
                    var dir = Path.Combine(AppRoot, pack.Id);
                    Directory.CreateDirectory(dir);

                    var path = Path.Combine(dir, name);
                    if (!File.Exists(path)) File.WriteAllBytes(path, data);
                }
                else
                {
                    var context = Context;
                    if (context is null)
                    {
                        _logger.Warning("Cannot store a local backdrop: no project is open");
                        return null;
                    }

                    var inZip = $"{ZipRoot}/{pack.Id}/{name}";
                    if (!context.FileExists(inZip))
                    {
                        context.WriteFile(inZip, data);
                        context.FlushStorage();
                    }

                    // Папка «Недавние» заводится сама и в списке имён не значится —
                    // о ней говорит только наличие картинок. Прочие папки
                    // записываются, иначе пустая папка исчезнет при перезапуске.
                    if (!pack.IsRecent)
                    {
                        var packs = LoadLocalPacks();
                        if (packs.All(p => p.Id != pack.Id))
                        {
                            packs.Add(pack);
                            SaveLocalNames(packs);
                        }
                    }
                }

                var item = new BackdropItem(
                    MakeRef(pack.Scope, pack.Id, name), name, pack.Id, pack.Scope);

                if (pack.Items.All(i => !string.Equals(i.Reference, item.Reference, StringComparison.Ordinal)))
                    pack.Items.Add(item);

                return item;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to store the backdrop image: {Path}", diskPath);
                return null;
            }
        }

        /// <summary>Убирает картинку из хранилища.</summary>
        public static void Delete(BackdropItem item)
        {
            if (item is null) return;

            try
            {
                if (item.Scope == BackdropScope.Global)
                {
                    var path = Path.Combine(AppRoot, item.PackId, item.FileName);
                    if (File.Exists(path)) File.Delete(path);
                    return;
                }

                var context = Context;
                if (context is null) return;

                context.DeleteFile($"{ZipRoot}/{item.PackId}/{item.FileName}");
                context.FlushStorage();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to delete the backdrop image: {Ref}", item.Reference);
            }
        }

        /// <summary>Папка, в которой лежит картинка по адресу. Не нашлась — null.</summary>
        public static BackdropPack? PackOf(string? reference, IReadOnlyList<BackdropPack> packs)
        {
            if (!IsLibraryRef(reference)) return null;

            string tail = TailOf(reference!);
            int slash = tail.IndexOf('/');
            if (slash <= 0) return null;

            string packId = tail[..slash];
            var scope = IsAppRef(reference) ? BackdropScope.Global : BackdropScope.Local;

            return packs.FirstOrDefault(p => p.Id == packId && p.Scope == scope);
        }

        // ── Внутреннее ────────────────────────────────────────────────────

        /// <summary>Наполняет папку её картинками.</summary>
        private static void FillItems(BackdropPack pack)
        {
            pack.Items.Clear();

            try
            {
                if (pack.Scope == BackdropScope.Global)
                {
                    var dir = Path.Combine(AppRoot, pack.Id);
                    if (!Directory.Exists(dir)) return;

                    foreach (var path in Directory.EnumerateFiles(dir)
                                 .Where(IsImage)
                                 .OrderBy(Path.GetFileName, StringComparer.CurrentCulture))
                    {
                        string name = Path.GetFileName(path);
                        pack.Items.Add(new BackdropItem(
                            MakeRef(BackdropScope.Global, pack.Id, name), name, pack.Id, BackdropScope.Global));
                    }

                    return;
                }

                foreach (var file in ProjectFilesOf(pack.Id).Where(IsImage)
                             .OrderBy(f => f, StringComparer.CurrentCulture))
                {
                    string name = file[(file.LastIndexOf('/') + 1)..];
                    pack.Items.Add(new BackdropItem(
                        MakeRef(BackdropScope.Local, pack.Id, name), name, pack.Id, BackdropScope.Local));
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to list the backdrop folder: {Id}", pack.Id);
            }
        }

        private static bool IsImage(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return Array.IndexOf(AllowedExtensions, ext) >= 0;
        }

        /// <summary>Файлы папки внутри архива проекта, полными путями в архиве.</summary>
        private static IEnumerable<string> ProjectFilesOf(string packId)
        {
            var context = Context;
            if (context is null) return Array.Empty<string>();

            try
            {
                return context.GetFiles($"{ZipRoot}/{packId}").ToList();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to list the project backdrop folder: {Id}", packId);
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// Имя файла в хранилище: отпечаток содержимого и прежнее расширение.
        /// По содержимому, а не по исходному имени: одна и та же картинка, выбранная
        /// дважды, должна лечь один раз, а два разных файла с именем «фон.jpg» — не
        /// затереть друг друга.
        /// </summary>
        private static string StoredName(byte[] data, string diskPath)
        {
            string hash = Convert.ToHexString(SHA256.HashData(data))
                .ToLower(CultureInfo.InvariantCulture)[..16];

            string ext = Path.GetExtension(diskPath).ToLowerInvariant();
            if (Array.IndexOf(AllowedExtensions, ext) < 0) ext = ".png";

            // Исходное имя остаётся в начале: на плитке подписан файл, и «закат.jpg»
            // человек узнаёт, а «a3f9c1…» — нет.
            string stem = Path.GetFileNameWithoutExtension(diskPath);
            stem = new string(stem.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray());
            if (stem.Length > 40) stem = stem[..40];
            if (string.IsNullOrWhiteSpace(stem)) stem = "фон";

            return $"{stem}-{hash[..6]}{ext}";
        }

        // ── Имена папок ───────────────────────────────────────────────────

        private sealed class PackName
        {
            public string Id { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
        }

        private static string GlobalNamesPath => Path.Combine(AppRoot, "folders.json");

        private static List<BackdropPack> LoadGlobalPacks()
        {
            var result = new List<BackdropPack>();

            try
            {
                if (!File.Exists(GlobalNamesPath)) return result;

                var names = JsonSerializer.Deserialize<List<PackName>>(
                    File.ReadAllText(GlobalNamesPath, Encoding.UTF8)) ?? new List<PackName>();

                foreach (var n in names)
                    if (!string.IsNullOrWhiteSpace(n.Id))
                        result.Add(new BackdropPack(n.Id, n.Name, BackdropScope.Global));
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to read the list of global backdrop folders");
            }

            return result;
        }

        private static void SaveGlobalNames(List<BackdropPack> packs)
        {
            try
            {
                Directory.CreateDirectory(AppRoot);
                var names = packs.Select(p => new PackName { Id = p.Id, Name = p.Name }).ToList();
                File.WriteAllText(GlobalNamesPath,
                    JsonSerializer.Serialize(names, new JsonSerializerOptions { WriteIndented = true }),
                    Encoding.UTF8);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to save the list of global backdrop folders");
            }
        }

        private static List<BackdropPack> LoadLocalPacks()
        {
            var result = new List<BackdropPack>
            {
                new(RecentPackId, RecentPackName, BackdropScope.Local, isRecent: true)
            };

            var context = Context;
            if (context is null) return result;

            try
            {
                var data = context.ReadFile($"{ZipRoot}/folders.json");
                if (data is null || data.Length == 0) return result;

                var names = JsonSerializer.Deserialize<List<PackName>>(
                    Encoding.UTF8.GetString(data)) ?? new List<PackName>();

                foreach (var n in names)
                    if (!string.IsNullOrWhiteSpace(n.Id) && n.Id != RecentPackId)
                        result.Add(new BackdropPack(n.Id, n.Name, BackdropScope.Local));
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to read the list of project backdrop folders");
            }

            return result;
        }

        private static void SaveLocalNames(List<BackdropPack> packs)
        {
            var context = Context;
            if (context is null) return;

            try
            {
                var names = packs
                    .Where(p => !p.IsRecent)
                    .Select(p => new PackName { Id = p.Id, Name = p.Name })
                    .ToList();

                context.WriteFile($"{ZipRoot}/folders.json",
                    Encoding.UTF8.GetBytes(
                        JsonSerializer.Serialize(names, new JsonSerializerOptions { WriteIndented = true })));
                context.FlushStorage();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to save the list of project backdrop folders");
            }
        }
    }
}
