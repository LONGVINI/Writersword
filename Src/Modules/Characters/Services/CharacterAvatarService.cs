using Avalonia.Media.Imaging;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Writersword.Core.Services;
using Writersword.Modules.Characters.Interfaces;
using Writersword.Modules.Characters.Models;

namespace Writersword.Modules.Characters.Services
{
    public class CharacterAvatarService : ICharacterAvatarService
    {
        private static readonly ILogger _logger = Log.ForContext<CharacterAvatarService>();
        private static readonly string[] AllowedExtensions = { ".jpg", ".jpeg", ".png", ".webp" };
        private const string ZipAvatarsFolder = "Characters/assets/avatars";

        // Несгруппированная библиотека пользователя.
        private static readonly string LibraryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Writersword", "Characters", "Library");

        // Пользовательские паки.
        private static readonly string UserPacksPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Writersword", "AvatarPacks");

        private DocumentContext? _context;

        // Все зарегистрированные директории с встроенными паками.
        // Каждый модуль регистрирует свою через RegisterPackDirectory().
        private readonly List<string> _registeredDirectories = new();

        public void SetContext(DocumentContext? context) => _context = context;

        /// <summary>
        /// Любой модуль регистрирует свою папку с паками.
        /// Каждая подпапка = один пак аватарок.
        /// </summary>
        public void RegisterPackDirectory(string directory)
        {
            if (!string.IsNullOrWhiteSpace(directory)
                && !_registeredDirectories.Contains(directory))
            {
                _registeredDirectories.Add(directory);
                _logger.Debug("Avatar pack directory registered: {Dir}", directory);
            }
        }

        // ── Сохранение ────────────────────────────────────────────────────

        public async Task<string?> SaveToProjectAsync(byte[] imageData, string suggestedName)
        {
            if (_context == null) { _logger.Warning("SaveToProjectAsync: no context"); return null; }
            var ext = Path.GetExtension(suggestedName).ToLowerInvariant();
            if (!AllowedExtensions.Contains(ext)) return null;
            var uniqueName = GetUniqueProjectName(suggestedName);
            try
            {
                await Task.Run(() =>
                    _context.WriteFile($"{ZipAvatarsFolder}/{uniqueName}", imageData));
                _logger.Debug("Project avatar saved: {Name}", uniqueName);
                return $"project:{uniqueName}";
            }
            catch (Exception ex) { _logger.Error(ex, "SaveToProjectAsync failed"); return null; }
        }

        public async Task<string?> SaveToLibraryAsync(byte[] imageData, string suggestedName)
        {
            try
            {
                Directory.CreateDirectory(LibraryPath);
                var ext = Path.GetExtension(suggestedName).ToLowerInvariant();
                if (!AllowedExtensions.Contains(ext)) return null;
                var uniqueName = GetUniqueName(suggestedName, LibraryPath);
                await File.WriteAllBytesAsync(Path.Combine(LibraryPath, uniqueName), imageData);
                return $"lib:{uniqueName}";
            }
            catch (Exception ex) { _logger.Error(ex, "SaveToLibraryAsync failed"); return null; }
        }

        public async Task<string?> CopyProjectAvatarToLibraryAsync(string projectRef)
        {
            var bytes = LoadAvatarBytes(projectRef);
            if (bytes == null) return null;
            return await SaveToLibraryAsync(bytes, ExtractFileName(projectRef));
        }

        // ── Загрузка ──────────────────────────────────────────────────────

        public byte[]? LoadAvatarBytes(string? avatarRef)
        {
            if (string.IsNullOrEmpty(avatarRef)) return null;
            try
            {
                if (avatarRef.StartsWith("project:"))
                {
                    var name = avatarRef["project:".Length..];
                    return _context?.ReadFile($"{ZipAvatarsFolder}/{name}");
                }
                if (avatarRef.StartsWith("lib:"))
                {
                    var path = Path.Combine(LibraryPath, avatarRef["lib:".Length..]);
                    return File.Exists(path) ? File.ReadAllBytes(path) : null;
                }
                if (avatarRef.StartsWith("pack:"))
                {
                    var parts = avatarRef["pack:".Length..].Split(':', 2);
                    if (parts.Length == 2)
                    {
                        var path = Path.Combine(UserPacksPath, parts[0], parts[1]);
                        return File.Exists(path) ? File.ReadAllBytes(path) : null;
                    }
                    return null;
                }
                if (avatarRef.StartsWith("builtin:"))
                {
                    // builtin:packId/filename — ищем во всех зарегистрированных директориях
                    var relative = avatarRef["builtin:".Length..];
                    var parts = relative.Split('/', 2);
                    if (parts.Length == 2)
                    {
                        foreach (var dir in _registeredDirectories)
                        {
                            var path = Path.Combine(dir, parts[0], parts[1]);
                            if (File.Exists(path)) return File.ReadAllBytes(path);
                        }
                    }
                    return null;
                }
                return null;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "LoadAvatarBytes failed for {Ref}", avatarRef);
                return null;
            }
        }

        public Bitmap? LoadBitmap(string? avatarRef)
        {
            var bytes = LoadAvatarBytes(avatarRef);
            if (bytes == null) return null;
            try { using var ms = new MemoryStream(bytes); return new Bitmap(ms); }
            catch (Exception ex) { _logger.Error(ex, "LoadBitmap failed for {Ref}", avatarRef); return null; }
        }

        // ── Удаление ──────────────────────────────────────────────────────

        public void DeleteAvatar(string? avatarRef)
        {
            if (string.IsNullOrEmpty(avatarRef)) return;
            try
            {
                if (avatarRef.StartsWith("project:"))
                    _context?.DeleteFile($"{ZipAvatarsFolder}/{avatarRef["project:".Length..]}");
                else if (avatarRef.StartsWith("lib:"))
                {
                    var path = Path.Combine(LibraryPath, avatarRef["lib:".Length..]);
                    if (File.Exists(path)) File.Delete(path);
                }
                else if (avatarRef.StartsWith("pack:"))
                {
                    var parts = avatarRef["pack:".Length..].Split(':', 2);
                    if (parts.Length == 2)
                    {
                        var path = Path.Combine(UserPacksPath, parts[0], parts[1]);
                        if (File.Exists(path)) File.Delete(path);
                    }
                }
            }
            catch (Exception ex) { _logger.Error(ex, "DeleteAvatar failed for {Ref}", avatarRef); }
        }

        // ── Паки ──────────────────────────────────────────────────────────

        public IReadOnlyList<CharacterAvatarItem> GetProjectAvatars()
        {
            if (_context == null) return Array.Empty<CharacterAvatarItem>();
            try
            {
                return _context.GetFiles(ZipAvatarsFolder)
                    .Where(f => AllowedExtensions.Contains(
                        Path.GetExtension(f).ToLowerInvariant()))
                    .Select(f => new CharacterAvatarItem
                    {
                        AvatarRef = $"project:{f}",
                        FileName = f,
                        Source = CharacterAvatarSource.Project
                    })
                    .ToList();
            }
            catch (Exception ex) { _logger.Error(ex, "GetProjectAvatars failed"); return Array.Empty<CharacterAvatarItem>(); }
        }

        public IReadOnlyList<CharacterAvatarPackInfo> GetAllPacks()
        {
            var result = new List<CharacterAvatarPackInfo>();

            // Встроенные паки из всех зарегистрированных директорий.
            foreach (var dir in _registeredDirectories)
                result.AddRange(LoadPacksFromDirectory(dir, CharacterAvatarPackSource.BuiltIn));

            // Пользовательские паки из %AppData%/AvatarPacks/.
            if (Directory.Exists(UserPacksPath))
                foreach (var dir in Directory.GetDirectories(UserPacksPath).OrderBy(d => d))
                {
                    var pack = LoadUserPack(dir);
                    if (pack != null) result.Add(pack);
                }

            // Несгруппированная библиотека пользователя.
            if (Directory.Exists(LibraryPath))
            {
                var items = Directory.GetFiles(LibraryPath)
                    .Where(f => AllowedExtensions.Contains(
                        Path.GetExtension(f).ToLowerInvariant()))
                    .Select(f => new CharacterAvatarItem
                    {
                        AvatarRef = $"lib:{Path.GetFileName(f)}",
                        FileName = Path.GetFileName(f),
                        Source = CharacterAvatarSource.Library
                    })
                    .ToList();
                if (items.Any())
                    result.Add(new CharacterAvatarPackInfo
                    {
                        Id = "__library__",
                        Source = CharacterAvatarPackSource.UserGlobal,
                        Items = items,
                        IconRef = items.FirstOrDefault()?.AvatarRef
                    });
            }

            return result;
        }

        // ── Встроенные паки из директории ─────────────────────────────────

        private List<CharacterAvatarPackInfo> LoadPacksFromDirectory(
            string baseDir, CharacterAvatarPackSource source)
        {
            var result = new List<CharacterAvatarPackInfo>();
            if (!Directory.Exists(baseDir)) return result;

            foreach (var dir in Directory.GetDirectories(baseDir).OrderBy(d => d))
            {
                var packId = Path.GetFileName(dir);
                var items = Directory.GetFiles(dir)
                    .Where(f => AllowedExtensions.Contains(
                        Path.GetExtension(f).ToLowerInvariant()))
                    .Select(f => new CharacterAvatarItem
                    {
                        AvatarRef = $"builtin:{packId}/{Path.GetFileName(f)}",
                        FileName = Path.GetFileName(f),
                        Source = CharacterAvatarSource.BuiltIn,
                        PackId = packId
                    })
                    .ToList();

                if (!items.Any()) continue;

                // Встроенный пак не имеет pack.json —
                // имя берётся из CharactersStrings.AvatarPack_{Id}.
                result.Add(new CharacterAvatarPackInfo
                {
                    Id = packId,
                    Source = source,
                    Items = items,
                    IconRef = items.FirstOrDefault()?.AvatarRef
                });
            }
            return result;
        }

        // ── Пользовательские паки ─────────────────────────────────────────

        public CharacterAvatarPackInfo CreateUserPack(string name)
        {
            var id = Guid.NewGuid().ToString("N")[..8];
            var dir = Path.Combine(UserPacksPath, id);
            Directory.CreateDirectory(dir);
            var pack = new CharacterAvatarPackInfo
            {
                Id = id,
                Name = name,
                Source = CharacterAvatarPackSource.UserGlobal,
                FolderPath = dir
            };
            SavePackJson(pack);
            return pack;
        }

        public void DeleteUserPack(string packId)
        {
            var dir = Path.Combine(UserPacksPath, packId);
            if (Directory.Exists(dir))
                try { Directory.Delete(dir, recursive: true); }
                catch (Exception ex) { _logger.Error(ex, "DeleteUserPack failed: {Id}", packId); }
        }

        public async Task MoveAvatarToPackAsync(string avatarRef, string targetPackId)
        {
            var bytes = LoadAvatarBytes(avatarRef);
            if (bytes == null) return;
            var fileName = ExtractFileName(avatarRef);
            var targetDir = targetPackId == "__library__"
                ? LibraryPath
                : Path.Combine(UserPacksPath, targetPackId);
            Directory.CreateDirectory(targetDir);
            await File.WriteAllBytesAsync(Path.Combine(targetDir, fileName), bytes);
            DeleteAvatar(avatarRef);
        }

        public async Task<CharacterAvatarPackInfo?> ImportPackFromZipAsync(string zipPath)
        {
            try
            {
                var tempId = Guid.NewGuid().ToString("N")[..8];
                var dir = Path.Combine(UserPacksPath, tempId);
                Directory.CreateDirectory(dir);
                await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, dir, overwriteFiles: true));

                var jsonPath = Path.Combine(dir, "pack.json");
                if (File.Exists(jsonPath))
                {
                    var meta = JsonSerializer.Deserialize<CharacterAvatarPackInfo>(
                        await File.ReadAllTextAsync(jsonPath));
                    if (!string.IsNullOrEmpty(meta?.Id) && meta.Id != tempId)
                    {
                        var namedDir = Path.Combine(UserPacksPath, meta.Id);
                        if (!Directory.Exists(namedDir))
                        { Directory.Move(dir, namedDir); dir = namedDir; }
                    }
                }
                return LoadUserPack(dir);
            }
            catch (Exception ex) { _logger.Error(ex, "ImportPackFromZipAsync failed"); return null; }
        }

        public async Task ExportPackToZipAsync(string packId, string outputPath)
        {
            try
            {
                var dir = Path.Combine(UserPacksPath, packId);
                if (!Directory.Exists(dir)) return;
                await Task.Run(() =>
                    ZipFile.CreateFromDirectory(dir, outputPath, CompressionLevel.Optimal, false));
            }
            catch (Exception ex) { _logger.Error(ex, "ExportPackToZipAsync failed: {Id}", packId); }
        }

        // ── Вспомогательные ───────────────────────────────────────────────

        private CharacterAvatarPackInfo? LoadUserPack(string dir)
        {
            try
            {
                var jsonPath = Path.Combine(dir, "pack.json");
                if (!File.Exists(jsonPath)) return null;

                var pack = JsonSerializer.Deserialize<CharacterAvatarPackInfo>(
                    File.ReadAllText(jsonPath));
                if (pack == null) return null;

                if (string.IsNullOrEmpty(pack.Id)) pack.Id = Path.GetFileName(dir);
                pack.Source = CharacterAvatarPackSource.UserGlobal;
                pack.FolderPath = dir;
                pack.Items = Directory.GetFiles(dir)
                    .Where(f => AllowedExtensions.Contains(
                        Path.GetExtension(f).ToLowerInvariant()))
                    .Select(f => new CharacterAvatarItem
                    {
                        AvatarRef = $"pack:{pack.Id}:{Path.GetFileName(f)}",
                        FileName = Path.GetFileName(f),
                        Source = CharacterAvatarSource.Library,
                        PackId = pack.Id
                    })
                    .ToList();
                pack.IconRef = pack.Items
                    .FirstOrDefault(i => i.FileName == pack.IconFileName)?.AvatarRef
                    ?? pack.Items.FirstOrDefault()?.AvatarRef;
                return pack;
            }
            catch (Exception ex) { _logger.Error(ex, "LoadUserPack failed: {Dir}", dir); return null; }
        }

        private void SavePackJson(CharacterAvatarPackInfo pack)
        {
            if (pack.FolderPath == null) return;
            File.WriteAllText(
                Path.Combine(pack.FolderPath, "pack.json"),
                JsonSerializer.Serialize(pack, new JsonSerializerOptions { WriteIndented = true }));
        }

        private string GetUniqueProjectName(string name)
        {
            if (_context == null) return name;
            var wo = Path.GetFileNameWithoutExtension(name);
            var ext = Path.GetExtension(name).ToLowerInvariant();
            var c = name; int n = 1;
            while (_context.FileExists($"{ZipAvatarsFolder}/{c}"))
                c = $"{wo} ({n++}){ext}";
            return c;
        }

        private static string GetUniqueName(string name, string folder)
        {
            Directory.CreateDirectory(folder);
            var wo = Path.GetFileNameWithoutExtension(name);
            var ext = Path.GetExtension(name).ToLowerInvariant();
            var c = name; int n = 1;
            while (File.Exists(Path.Combine(folder, c)))
                c = $"{wo} ({n++}){ext}";
            return c;
        }

        private static string ExtractFileName(string avatarRef)
        {
            var parts = avatarRef.Split(':');
            return parts[^1];
        }
    }
}