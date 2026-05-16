using Avalonia.Media.Imaging;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Writersword.Core.Services;
using Writersword.Modules.Characters.Interfaces;
using Writersword.Modules.Characters.Models;

namespace Writersword.Modules.Characters.Services
{
    /// <summary>
    /// Управляет аватарками персонажей.
    ///
    /// Проектные аватарки хранятся прямо в ZIP-файле проекта по пути
    ///   Characters/assets/avatars/{filename}
    /// через DocumentContext.WriteFile — мгновенно, минуя очередь сохранения.
    ///
    /// Библиотечные аватарки хранятся в
    ///   %AppData%/Writersword/Characters/Library/
    /// и доступны во всех проектах.
    ///
    /// AvatarRef-строки в Character.AvatarPath:
    ///   "project:filename.jpg"  — проектный аватар
    ///   "lib:filename.jpg"      — библиотечный аватар
    /// </summary>
    public class CharacterAvatarService : ICharacterAvatarService
    {
        private static readonly ILogger _logger = Log.ForContext<CharacterAvatarService>();

        private static readonly string[] AllowedExtensions = { ".jpg", ".jpeg", ".png", ".webp" };

        private const string ZipAvatarsFolder = "Characters/assets/avatars";

        private static readonly string LibraryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Writersword", "Characters", "Library");

        private DocumentContext? _context;
        private string? _builtInPath;

        public void SetContext(DocumentContext? context) => _context = context;
        public void SetBuiltInPath(string? path) => _builtInPath = path;

        // ── Сохранение ────────────────────────────────────────────────────

        public async Task<string?> SaveToProjectAsync(byte[] imageData, string suggestedName)
        {
            if (_context == null)
            {
                _logger.Warning("SaveToProjectAsync: no context");
                return null;
            }

            var ext = Path.GetExtension(suggestedName).ToLowerInvariant();
            if (!AllowedExtensions.Contains(ext))
            {
                _logger.Warning("SaveToProjectAsync: unsupported extension {Ext}", ext);
                return null;
            }

            // Уникальное имя определяем на UI-треде (читает ZIP).
            var uniqueName = GetUniqueProjectName(suggestedName);
            var zipPath = $"{ZipAvatarsFolder}/{uniqueName}";
            var ctx = _context;

            try
            {
                // Запись в ZIP — в фоновом потоке чтобы не заморозить UI.
                await Task.Run(() => ctx.WriteFile(zipPath, imageData));
                _logger.Debug("Avatar saved to project: {Path}", zipPath);
                return $"project:{uniqueName}";
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to save project avatar");
                return null;
            }
        }

        public async Task<string?> SaveToLibraryAsync(byte[] imageData, string suggestedName)
        {
            try
            {
                Directory.CreateDirectory(LibraryPath);

                var ext = Path.GetExtension(suggestedName).ToLowerInvariant();
                if (!AllowedExtensions.Contains(ext))
                {
                    _logger.Warning("SaveToLibrary: unsupported extension {Ext}", ext);
                    return null;
                }

                var uniqueName = GetUniqueLibraryName(suggestedName);
                var filePath = Path.Combine(LibraryPath, uniqueName);

                await File.WriteAllBytesAsync(filePath, imageData);
                _logger.Debug("Avatar saved to library: {Path}", filePath);
                return $"lib:{uniqueName}";
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to save library avatar");
                return null;
            }
        }

        public async Task<string?> CopyProjectAvatarToLibraryAsync(string projectAvatarRef)
        {
            var bytes = LoadAvatarBytes(projectAvatarRef);
            if (bytes == null) return null;

            var fileName = ExtractFileName(projectAvatarRef);
            return await SaveToLibraryAsync(bytes, fileName);
        }

        // ── Загрузка ──────────────────────────────────────────────────────

        public byte[]? LoadAvatarBytes(string? avatarRef)
        {
            if (string.IsNullOrEmpty(avatarRef)) return null;

            try
            {
                if (avatarRef.StartsWith("project:"))
                {
                    var fileName = avatarRef["project:".Length..];
                    var zipPath = $"{ZipAvatarsFolder}/{fileName}";
                    return _context?.ReadFile(zipPath);
                }

                if (avatarRef.StartsWith("lib:"))
                {
                    var fileName = avatarRef["lib:".Length..];
                    var filePath = Path.Combine(LibraryPath, fileName);
                    return File.Exists(filePath) ? File.ReadAllBytes(filePath) : null;
                }

                if (avatarRef.StartsWith("builtin:"))
                {
                    var filePath = avatarRef["builtin:".Length..];
                    return File.Exists(filePath) ? File.ReadAllBytes(filePath) : null;
                }

                if (avatarRef.StartsWith("builtin:"))
                {
                    var fp = avatarRef["builtin:".Length..];
                    return File.Exists(fp) ? File.ReadAllBytes(fp) : null;
                }
                _logger.Warning("Unknown avatar ref format: {Ref}", avatarRef);
                return null;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to load avatar bytes for {Ref}", avatarRef);
                return null;
            }
        }

        public Bitmap? LoadBitmap(string? avatarRef)
        {
            var bytes = LoadAvatarBytes(avatarRef);
            if (bytes == null) return null;

            try
            {
                using var ms = new MemoryStream(bytes);
                return new Bitmap(ms);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to decode bitmap for {Ref}", avatarRef);
                return null;
            }
        }

        // ── Удаление ──────────────────────────────────────────────────────

        public void DeleteAvatar(string? avatarRef)
        {
            if (string.IsNullOrEmpty(avatarRef)) return;

            try
            {
                if (avatarRef.StartsWith("project:"))
                {
                    var fileName = avatarRef["project:".Length..];
                    var zipPath = $"{ZipAvatarsFolder}/{fileName}";
                    _context?.DeleteFile(zipPath);
                    _logger.Debug("Deleted project avatar: {Path}", zipPath);
                }
                else if (avatarRef.StartsWith("lib:"))
                {
                    var fileName = avatarRef["lib:".Length..];
                    var filePath = Path.Combine(LibraryPath, fileName);
                    if (File.Exists(filePath)) File.Delete(filePath);
                    _logger.Debug("Deleted library avatar: {Path}", filePath);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to delete avatar {Ref}", avatarRef);
            }
        }

        // ── Галерея ───────────────────────────────────────────────────────

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
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to list project avatars");
                return Array.Empty<CharacterAvatarItem>();
            }
        }

        public IReadOnlyList<CharacterAvatarItem> GetLibraryAvatars()
        {
            try
            {
                if (!Directory.Exists(LibraryPath))
                    return Array.Empty<CharacterAvatarItem>();

                return Directory
                    .GetFiles(LibraryPath)
                    .Where(f => AllowedExtensions.Contains(
                        Path.GetExtension(f).ToLowerInvariant()))
                    .Select(f => new CharacterAvatarItem
                    {
                        AvatarRef = $"lib:{Path.GetFileName(f)}",
                        FileName = Path.GetFileName(f),
                        Source = CharacterAvatarSource.Library
                    })
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to list library avatars");
                return Array.Empty<CharacterAvatarItem>();
            }
        }

        public IReadOnlyList<CharacterAvatarItem> GetBuiltInAvatars()
        {
            if (string.IsNullOrEmpty(_builtInPath) || !Directory.Exists(_builtInPath))
                return Array.Empty<CharacterAvatarItem>();
            try
            {
                return Directory.GetFiles(_builtInPath)
                    .Where(f => AllowedExtensions.Contains(
                        Path.GetExtension(f).ToLowerInvariant()))
                    .Select(f => new CharacterAvatarItem
                    {
                        AvatarRef = $"builtin:{f}",
                        FileName = Path.GetFileName(f),
                        Source = CharacterAvatarSource.BuiltIn
                    })
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to list built-ins from {Path}", _builtInPath);
                return Array.Empty<CharacterAvatarItem>();
            }
        }

        // ── Вспомогательные ───────────────────────────────────────────────

        private string GetUniqueProjectName(string suggestedName)
        {
            if (_context == null) return suggestedName;

            var nameWithout = Path.GetFileNameWithoutExtension(suggestedName);
            var ext = Path.GetExtension(suggestedName).ToLowerInvariant();
            var candidate = suggestedName;
            int counter = 1;

            while (_context.FileExists($"{ZipAvatarsFolder}/{candidate}"))
            {
                candidate = $"{nameWithout} ({counter}){ext}";
                counter++;
            }
            return candidate;
        }

        private static string GetUniqueLibraryName(string suggestedName)
        {
            Directory.CreateDirectory(LibraryPath);

            var nameWithout = Path.GetFileNameWithoutExtension(suggestedName);
            var ext = Path.GetExtension(suggestedName).ToLowerInvariant();
            var candidate = suggestedName;
            int counter = 1;

            while (File.Exists(Path.Combine(LibraryPath, candidate)))
            {
                candidate = $"{nameWithout} ({counter}){ext}";
                counter++;
            }
            return candidate;
        }

        private static string ExtractFileName(string avatarRef)
        {
            var colonIdx = avatarRef.IndexOf(':');
            return colonIdx >= 0 ? avatarRef[(colonIdx + 1)..] : avatarRef;
        }
    }
}