using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Writersword.Core.Interfaces.Services.Storage;
using Writersword.Core.Services;
using Writersword.Modules.Characters.Interfaces;
using Writersword.Modules.Characters.Models;

namespace Writersword.Modules.Characters.Services
{
    public class CharacterAvatarService : ICharacterAvatarService
    {
        private static readonly ILogger _logger = Log.ForContext<CharacterAvatarService>();
        private static readonly string[] AllowedExtensions = { ".jpg", ".jpeg", ".png", ".webp" };

        // Значки меток: к форматам аватаров добавлены мелкие растры, которые
        // в качестве фотографии бессмысленны, и вектор. Список отдельный —
        // иначе SVG попал бы в выбор аватарок, где его нечем показать.
        private static readonly string[] AllowedIconExtensions =
            { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".ico", ".svg" };
        private const string ZipAvatarsFolder = "Characters/assets/avatars";

        // Локальные паки живут в архиве проекта рядом с проектными аватарками,
        // но в своей папке: выборка проектных аватарок ищет по префиксу
        // "Characters/assets/avatars/", и папка паков под него не подпадает.
        private const string ZipPacksFolder = "Characters/assets/avatarpacks";
        private const string PackMetaFileName = "pack.json";

        // Ключ глобальных настроек со списком «Недавних».
        private const string RecentsSettingsKey = "CharacterAvatarRecents";

        // Предел «Недавних». Список висит в настройках и читается при каждом
        // открытии пикера — держать в нём всю историю проекта незачем.
        private const int RecentsMaxEntries = 60;

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

        // Кэш байтов аватарок по ссылке. Убирает повторные открытия большого
        // проектного zip при отрисовке (миниатюры пикера, карточки) — основная
        // причина залипаний UI на крупных проектах. Ссылки уникальны и неизменяемы.
        //
        // Ключом идёт адрес файла без кадра: кадр живёт в ссылке персонажа и на
        // содержимое файла не влияет, иначе одна картинка с тремя обрезками
        // заняла бы в кэше три места.
        private readonly Dictionary<string, byte[]> _byteCache = new();
        private readonly Queue<string> _byteCacheOrder = new();
        private readonly object _byteCacheLock = new object();
        private const int ByteCacheMaxEntries = 150;

        // Отпечатки содержимого по адресу файла. Нужны поиску дубликата при
        // добавлении картинки: без кэша каждая новая картинка перечитывала бы
        // и хешировала всё хранилище целиком.
        private readonly Dictionary<string, string> _hashCache = new();
        private readonly object _hashCacheLock = new object();

        private void CacheBytes(string avatarRef, byte[] data)
        {
            lock (_byteCacheLock)
            {
                if (_byteCache.ContainsKey(avatarRef)) return;
                _byteCache[avatarRef] = data;
                _byteCacheOrder.Enqueue(avatarRef);
                while (_byteCacheOrder.Count > ByteCacheMaxEntries)
                {
                    var oldest = _byteCacheOrder.Dequeue();
                    _byteCache.Remove(oldest);
                }
            }
        }

        private byte[]? TryGetCachedBytes(string avatarRef)
        {
            lock (_byteCacheLock)
                return _byteCache.TryGetValue(avatarRef, out var data) ? data : null;
        }

        private void EvictCachedBytes(string avatarRef)
        {
            lock (_byteCacheLock)
                _byteCache.Remove(avatarRef);

            lock (_hashCacheLock)
                _hashCache.Remove(avatarRef);
        }

        public void SetContext(DocumentContext? context)
        {
            _context = context;

            // Смена проекта меняет содержимое ссылок project: и lpack: —
            // кэш прошлого проекта на них уже не отвечает.
            lock (_byteCacheLock)
            {
                _byteCache.Clear();
                _byteCacheOrder.Clear();
            }
            lock (_hashCacheLock)
            {
                _hashCache.Clear();
            }
        }

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

        public Task<string?> SaveToProjectAsync(byte[] imageData, string suggestedName)
            => SaveToProjectAsync(imageData, suggestedName, AllowedExtensions);

        public Task<string?> SaveIconToProjectAsync(byte[] imageData, string suggestedName)
            => SaveToProjectAsync(imageData, suggestedName, AllowedIconExtensions);

        private async Task<string?> SaveToProjectAsync(byte[] imageData, string suggestedName, string[] allowedExtensions)
        {
            if (_context == null) { _logger.Warning("SaveToProjectAsync: no context"); return null; }
            var ext = Path.GetExtension(suggestedName).ToLowerInvariant();
            if (!allowedExtensions.Contains(ext)) return null;
            var uniqueName = GetUniqueProjectName(suggestedName);
            try
            {
                var avatarRef = $"project:{uniqueName}";
                CacheBytes(avatarRef, imageData);
                RememberHash(avatarRef, imageData);
                await Task.Run(() =>
                    _context.WriteFile($"{ZipAvatarsFolder}/{uniqueName}", imageData));
                _logger.Debug("Project avatar saved: {Name}", uniqueName);
                return avatarRef;
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
                var avatarRef = $"lib:{uniqueName}";
                CacheBytes(avatarRef, imageData);
                RememberHash(avatarRef, imageData);
                return avatarRef;
            }
            catch (Exception ex) { _logger.Error(ex, "SaveToLibraryAsync failed"); return null; }
        }

        public async Task<string?> SaveToPackAsync(byte[] imageData, string suggestedName, string packId)
        {
            if (string.IsNullOrEmpty(packId)) return null;

            var ext = Path.GetExtension(suggestedName).ToLowerInvariant();
            if (!AllowedExtensions.Contains(ext)) return null;

            // Библиотека — тот же пак с точки зрения пикера, но своя папка.
            if (packId == LibraryPackId)
                return await SaveToLibraryAsync(imageData, suggestedName);

            try
            {
                // Локальный пак: файл уходит в архив проекта.
                if (IsLocalPackId(packId))
                {
                    if (_context == null) return null;
                    var localName = GetUniqueLocalPackName(suggestedName, packId);
                    var localRef = $"lpack:{packId}:{localName}";
                    CacheBytes(localRef, imageData);
                    RememberHash(localRef, imageData);
                    await Task.Run(() =>
                        _context.WriteFile($"{ZipPacksFolder}/{packId}/{localName}", imageData));
                    return localRef;
                }

                // Глобальный пользовательский пак: папка в %AppData%.
                var dir = Path.Combine(UserPacksPath, packId);
                if (!Directory.Exists(dir)) return null;

                var uniqueName = GetUniqueName(suggestedName, dir);
                await File.WriteAllBytesAsync(Path.Combine(dir, uniqueName), imageData);
                var avatarRef = $"pack:{packId}:{uniqueName}";
                CacheBytes(avatarRef, imageData);
                RememberHash(avatarRef, imageData);
                return avatarRef;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "SaveToPackAsync failed: {Id}", packId);
                return null;
            }
        }

        public async Task<string?> CopyProjectAvatarToLibraryAsync(string projectRef)
        {
            var bytes = LoadAvatarBytes(projectRef);
            if (bytes == null) return null;
            return await SaveToLibraryAsync(bytes, ExtractFileName(projectRef));
        }

        // ── Поиск дубликата ───────────────────────────────────────────────

        public string? FindStoredByContent(byte[] imageData)
            => FindStoredByContent(imageData, null);

        /// <summary>
        /// Найти сохранённую картинку с тем же содержимым, ограничив поиск
        /// нужными адресами. Отбор нужен укладке в проект: там годится только
        /// то, что уедет вместе с ним, а совпадение в глобальном паке ответом
        /// на вопрос «лежит ли она уже в проекте» не является.
        /// </summary>
        private string? FindStoredByContent(byte[] imageData, Func<string, bool>? where)
        {
            if (imageData == null || imageData.Length == 0) return null;

            var wanted = ComputeHash(imageData);

            // Порядок обхода задаёт и порядок предпочтения: сначала то, что
            // лежит в самом проекте — такая картинка уедет вместе с ним и не
            // рассыплется на чужой машине.
            foreach (var candidate in EnumerateStoredRefs())
            {
                if (where != null && !where(candidate)) continue;

                var hash = GetHashOf(candidate);
                if (hash != null && string.Equals(hash, wanted, StringComparison.Ordinal))
                    return candidate;
            }

            return null;
        }

        /// <summary>
        /// Адреса всех картинок, которые служба может отдать и удалить.
        /// Встроенные паки не перечисляются: совпадение с ресурсом сборки
        /// пришлось бы всё равно копировать в проект при первой же правке.
        /// </summary>
        private IEnumerable<string> EnumerateStoredRefs()
        {
            foreach (var item in GetProjectAvatars())
                yield return item.AvatarRef;

            foreach (var pack in GetLocalPacks())
                foreach (var item in pack.Items)
                    yield return item.AvatarRef;

            foreach (var item in GetLibraryItems())
                yield return item.AvatarRef;

            if (Directory.Exists(UserPacksPath))
            {
                foreach (var dir in Directory.GetDirectories(UserPacksPath).OrderBy(d => d))
                {
                    var pack = LoadUserPack(dir);
                    if (pack == null) continue;
                    foreach (var item in pack.Items)
                        yield return item.AvatarRef;
                }
            }
        }

        private string? GetHashOf(string avatarRef)
        {
            var baseRef = CharacterAvatarRef.BaseOf(avatarRef);
            if (string.IsNullOrEmpty(baseRef)) return null;

            lock (_hashCacheLock)
                if (_hashCache.TryGetValue(baseRef, out var cached)) return cached;

            var bytes = LoadAvatarBytes(baseRef);
            if (bytes == null) return null;

            var hash = ComputeHash(bytes);
            lock (_hashCacheLock)
                _hashCache[baseRef] = hash;
            return hash;
        }

        private void RememberHash(string avatarRef, byte[] data)
        {
            var baseRef = CharacterAvatarRef.BaseOf(avatarRef);
            if (string.IsNullOrEmpty(baseRef)) return;
            lock (_hashCacheLock)
                _hashCache[baseRef] = ComputeHash(data);
        }

        private static string ComputeHash(byte[] data)
        {
            var hash = SHA256.HashData(data);
            return Convert.ToHexString(hash);
        }

        // ── Где лежит картинка ────────────────────────────────────────────
        //
        // Проект должен быть самодостаточен: всё, что видно на его страницах и
        // карточках, обязано лежать в его архиве. Иначе передача проекта другому
        // человеку превращается в лотерею — у него нет ни библиотеки, ни паков
        // отправителя, и половина аватарок оказывается пустыми местами без
        // единого слова о том, чего не хватает.
        //
        // Глобальное хранилище от этого не становится лишним. Оно библиотека, из
        // которой берут, а не место, на которое ссылаются.

        /// <summary>
        /// Картинка уедет вместе с проектом: она либо в его архиве, либо в
        /// ресурсах сборки, которые есть у каждого, кто запустил программу.
        /// </summary>
        public static bool TravelsWithProject(string? avatarRef)
        {
            var baseRef = CharacterAvatarRef.BaseOf(avatarRef);
            if (string.IsNullOrEmpty(baseRef)) return true;

            return baseRef!.StartsWith("project:", StringComparison.Ordinal)
                || baseRef.StartsWith("lpack:", StringComparison.Ordinal)
                || baseRef.StartsWith("builtin:", StringComparison.Ordinal);
        }

        /// <summary>Ссылка ведёт в архив проекта.</summary>
        private static bool IsInProjectArchive(string? avatarRef)
        {
            var baseRef = CharacterAvatarRef.BaseOf(avatarRef);
            return !string.IsNullOrEmpty(baseRef)
                && (baseRef!.StartsWith("project:", StringComparison.Ordinal)
                    || baseRef.StartsWith("lpack:", StringComparison.Ordinal));
        }

        /// <summary>
        /// Где лежит картинка по этой ссылке. Отвечает на единственный важный
        /// вопрос: уедет ли она вместе с проектом.
        ///
        /// Ссылка, по которой прочитать нечего, объявляется потерянной, даже
        /// если по виду она проектная: показать по ней всё равно нечего, и
        /// человеку важнее знать это, чем то, куда она вела.
        /// </summary>
        public Core.Models.Project.ProjectAssetPlace PlaceOf(string? avatarRef)
        {
            var baseRef = CharacterAvatarRef.BaseOf(avatarRef);
            if (string.IsNullOrEmpty(baseRef))
                return Core.Models.Project.ProjectAssetPlace.Missing;

            if (LoadAvatarBytes(baseRef) == null)
                return Core.Models.Project.ProjectAssetPlace.Missing;

            if (baseRef!.StartsWith("project:", StringComparison.Ordinal)
                || baseRef.StartsWith("lpack:", StringComparison.Ordinal))
                return Core.Models.Project.ProjectAssetPlace.InProject;

            if (baseRef.StartsWith("builtin:", StringComparison.Ordinal))
                return Core.Models.Project.ProjectAssetPlace.BuiltIn;

            // lib: и pack: — библиотека и общие паки в данных программы.
            return Core.Models.Project.ProjectAssetPlace.InApp;
        }

        /// <summary>Размер картинки в байтах. 0 — прочитать нечем.</summary>
        public long SizeOf(string? avatarRef) => LoadAvatarBytes(avatarRef)?.LongLength ?? 0;

        public Task<string?> EnsureInProjectAsync(string? avatarRef)
            => EnsureInProjectAsync(avatarRef, false);

        /// <summary>
        /// Уложить картинку в проект и вернуть новую ссылку на неё.
        ///
        /// Что уже уезжает с проектом, возвращается как есть. Что лежит в
        /// библиотеке или глобальном паке — копируется в архив; исходник при
        /// этом остаётся на месте, потому что он общий для всех проектов и
        /// забирать его у остальных незачем.
        ///
        /// Кадры принадлежат персонажу, а не файлу, и переезжают вместе со
        /// ссылкой: обрезка, сделанная до укладки, не должна пропадать.
        ///
        /// Ссылку, которую прочитать нечем, метод возвращает нетронутой. Молча
        /// подменять её на пустоту нельзя: файл может быть временно недоступен,
        /// а испорченная запись уже не расскажет, чего не хватало.
        /// </summary>
        public async Task<string?> EnsureInProjectAsync(string? avatarRef, bool iconFormats)
        {
            var baseRef = CharacterAvatarRef.BaseOf(avatarRef);
            if (string.IsNullOrEmpty(baseRef)) return avatarRef;
            if (TravelsWithProject(baseRef)) return avatarRef;
            if (_context == null) return avatarRef;

            var bytes = LoadAvatarBytes(baseRef);
            if (bytes == null || bytes.Length == 0)
            {
                _logger.Warning("EnsureInProject: nothing to read at {Ref}", baseRef);
                return avatarRef;
            }

            // Та же картинка могла лечь в проект раньше — с другого персонажа
            // или из другого пака. Второй копии в архиве взяться неоткуда.
            var stored = FindStoredByContent(bytes, IsInProjectArchive)
                ?? await SaveToProjectAsync(bytes, ExtractFileName(baseRef),
                    iconFormats ? AllowedIconExtensions : AllowedExtensions);

            if (string.IsNullOrEmpty(stored)) return avatarRef;

            return CharacterAvatarRef.Combine(
                stored,
                CharacterAvatarRef.CropOf(avatarRef),
                CharacterAvatarRef.StripCropOf(avatarRef));
        }

        /// <summary>
        /// Ссылки на картинки сменились: пак уложен в проект или перенесён из
        /// одной области в другую. Ключ — прежняя ссылка, значение — новая.
        ///
        /// Событие, а не возвращаемое значение, потому что переписывать ссылки
        /// службе картинок нечем и незачем: она знает файлы, но не знает, кто на
        /// них ссылается. Слушает модуль персонажей — у него есть и персонажи, и
        /// реестр меток.
        /// </summary>
        public event Action<IReadOnlyDictionary<string, string>>? AvatarRefsRemapped;

        private void RaiseRemap(IReadOnlyDictionary<string, string> map)
        {
            if (map.Count == 0) return;
            try { AvatarRefsRemapped?.Invoke(map); }
            catch (Exception ex) { _logger.Error(ex, "AvatarRefsRemapped handler failed"); }
        }

        // ── Загрузка ──────────────────────────────────────────────────────

        public byte[]? LoadAvatarBytes(string? avatarRef)
        {
            // Байты принадлежат файлу, кадр — персонажу: до хранилища кадр не
            // доходит, иначе ссылки одной картинки с разной обрезкой искали бы
            // разные, несуществующие файлы.
            var baseRef = CharacterAvatarRef.BaseOf(avatarRef);
            if (string.IsNullOrEmpty(baseRef)) return null;

            var cached = TryGetCachedBytes(baseRef);
            if (cached != null) return cached;

            var bytes = LoadAvatarBytesFromSource(baseRef);
            if (bytes != null) CacheBytes(baseRef, bytes);
            return bytes;
        }

        private byte[]? LoadAvatarBytesFromSource(string avatarRef)
        {
            try
            {
                if (avatarRef.StartsWith("project:"))
                {
                    // Допускаем как корректную ссылку project:имя.png, так и старую
                    // битую project:Characters/assets/avatars/имя.png — берём имя файла.
                    var name = Path.GetFileName(avatarRef["project:".Length..]);
                    return _context?.ReadFile($"{ZipAvatarsFolder}/{name}");
                }
                if (avatarRef.StartsWith("lpack:"))
                {
                    var parts = avatarRef["lpack:".Length..].Split(':', 2);
                    if (parts.Length == 2)
                        return _context?.ReadFile($"{ZipPacksFolder}/{parts[0]}/{Path.GetFileName(parts[1])}");
                    return null;
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

        // Аватарка показывается максимум ~150px, но полноразмерное фото (с телефона/
        // камеры) уходит в GPU как огромная текстура и вешает рендер-поток на части
        // драйверов. Уменьшаем до безопасного размера перед отдачей в UI.
        private const int AvatarMaxSide = 512;

        public Bitmap? LoadBitmap(string? avatarRef) => LoadBitmap(avatarRef, AvatarMaxSide);

        public Bitmap? LoadBitmap(string? avatarRef, int maxSide)
            => LoadBitmap(avatarRef, maxSide, false);

        public Bitmap? LoadBitmap(string? avatarRef, int maxSide, bool forStrip)
        {
            // Кадров у ссылки два: свой кружку и свой полоске. Какой брать,
            // решает не ссылка, а то, чем её сейчас показывают.
            var crop = CharacterAvatarRef.CropFor(avatarRef, forStrip);
            var bytes = LoadAvatarBytes(avatarRef);
            if (bytes == null) return null;
            try
            {
                using var ms = new MemoryStream(bytes);
                Bitmap bitmap = new Bitmap(ms);

                // Кадр режется до уменьшения: обрезанный кусок сам может быть
                // меньше предела, и уменьшать после обрезки было бы нечего, а
                // уменьшив сначала, мы вырезали бы кадр из уже потерянных точек.
                if (crop != null && !crop.IsFull)
                {
                    var cropped = CropBitmap(bitmap, crop);
                    if (cropped != null)
                    {
                        bitmap.Dispose();
                        bitmap = cropped;
                    }
                }

                var w = bitmap.PixelSize.Width;
                var h = bitmap.PixelSize.Height;
                if (w <= maxSide && h <= maxSide)
                    return bitmap;

                var scale = (double)maxSide / Math.Max(w, h);
                var target = new PixelSize(
                    Math.Max(1, (int)Math.Round(w * scale)),
                    Math.Max(1, (int)Math.Round(h * scale)));
                var scaled = bitmap.CreateScaledBitmap(target, BitmapInterpolationMode.HighQuality);
                bitmap.Dispose();
                return scaled;
            }
            catch (Exception ex) { _logger.Error(ex, "LoadBitmap failed for {Ref}", avatarRef); return null; }
        }

        /// <summary>
        /// Вырезать кадр в новый битмап.
        ///
        /// Через копирование точек, а не через отрисовку в RenderTargetBitmap:
        /// копирование не заводит поверхность рисования и не зависит от
        /// графического устройства, поэтому идёт одинаково на любом бэкенде и
        /// в любом потоке — миниатюры пикера строятся пачкой и не в UI-потоке.
        /// </summary>
        private static Bitmap? CropBitmap(Bitmap source, CharacterAvatarCrop crop)
        {
            try
            {
                var rect = crop.ToPixelRect(source.PixelSize.Width, source.PixelSize.Height);
                if (rect.Width <= 0 || rect.Height <= 0) return null;

                // Точки копируются в свой буфер, а из него собирается обычный
                // Bitmap, а не WriteableBitmap.
                //
                // Разница не в удобстве, а в том, что бывает дальше: обрезанную
                // картинку ещё уменьшают под размер карточки, а уменьшение в
                // Skia (ResizeBitmap) на источнике-WriteableBitmap падает с
                // «Invalid source bitmap type». Ошибка ловилась выше, LoadBitmap
                // возвращал null, и карточка с подрезанной аватаркой крупнее
                // предела оставалась пустой.
                var stride = rect.Width * 4;
                var bufferSize = stride * rect.Height;
                var buffer = Marshal.AllocHGlobal(bufferSize);
                try
                {
                    source.CopyPixels(rect, buffer, bufferSize, stride);

                    // Конструктор копирует точки себе, поэтому буфер
                    // освобождается сразу же в finally.
                    return new Bitmap(
                        PixelFormat.Bgra8888,
                        AlphaFormat.Premul,
                        buffer,
                        new PixelSize(rect.Width, rect.Height),
                        source.Dpi,
                        stride);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "CropBitmap failed");
                return null;
            }
        }

        public PixelSize? GetImageSize(string? avatarRef)
        {
            // Размер нужен исходный, до кадра: окно обрезки считает кадр в
            // долях этого исходника и рисует рамку поверх него целиком.
            var bytes = LoadAvatarBytes(avatarRef);
            if (bytes == null) return null;
            try
            {
                using var ms = new MemoryStream(bytes);
                using var bitmap = new Bitmap(ms);
                return bitmap.PixelSize;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "GetImageSize failed for {Ref}", avatarRef);
                return null;
            }
        }

        // ── Удаление ──────────────────────────────────────────────────────

        public void DeleteAvatar(string? avatarRef)
        {
            var baseRef = CharacterAvatarRef.BaseOf(avatarRef);
            if (string.IsNullOrEmpty(baseRef)) return;
            EvictCachedBytes(baseRef);
            try
            {
                if (baseRef.StartsWith("project:"))
                    _context?.DeleteFile($"{ZipAvatarsFolder}/{Path.GetFileName(baseRef["project:".Length..])}");
                else if (baseRef.StartsWith("lpack:"))
                {
                    var parts = baseRef["lpack:".Length..].Split(':', 2);
                    if (parts.Length == 2)
                        _context?.DeleteFile($"{ZipPacksFolder}/{parts[0]}/{Path.GetFileName(parts[1])}");
                }
                else if (baseRef.StartsWith("lib:"))
                {
                    var path = Path.Combine(LibraryPath, baseRef["lib:".Length..]);
                    if (File.Exists(path)) File.Delete(path);
                }
                else if (baseRef.StartsWith("pack:"))
                {
                    var parts = baseRef["pack:".Length..].Split(':', 2);
                    if (parts.Length == 2)
                    {
                        var path = Path.Combine(UserPacksPath, parts[0], parts[1]);
                        if (File.Exists(path)) File.Delete(path);
                    }
                }
            }
            catch (Exception ex) { _logger.Error(ex, "DeleteAvatar failed for {Ref}", baseRef); }
        }

        // ── Паки ──────────────────────────────────────────────────────────

        /// <summary>Идентификатор несгруппированной библиотеки пользователя.</summary>
        public const string LibraryPackId = "__library__";

        /// <summary>Приставка идентификатора локального пака.</summary>
        private const string LocalPackPrefix = "local-";

        private static bool IsLocalPackId(string packId) =>
            packId.StartsWith(LocalPackPrefix, StringComparison.Ordinal);

        public IReadOnlyList<CharacterAvatarItem> GetProjectAvatars()
        {
            if (_context == null) return Array.Empty<CharacterAvatarItem>();
            try
            {
                return _context.GetFiles(ZipAvatarsFolder)
                    .Where(f => AllowedExtensions.Contains(
                        Path.GetExtension(f).ToLowerInvariant()))
                    .Select(f =>
                    {
                        // GetFiles возвращает полный путь внутри zip; ссылка project:
                        // должна содержать только имя файла, иначе LoadAvatarBytes
                        // повторно приклеит папку и файл не найдётся.
                        var fileName = Path.GetFileName(f);
                        return new CharacterAvatarItem
                        {
                            AvatarRef = $"project:{fileName}",
                            FileName = fileName,
                            Source = CharacterAvatarSource.Project,
                            Scope = CharacterAvatarPackScope.Local
                        };
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
            var libraryItems = GetLibraryItems();
            if (libraryItems.Count > 0)
                result.Add(new CharacterAvatarPackInfo
                {
                    Id = LibraryPackId,
                    Source = CharacterAvatarPackSource.UserGlobal,
                    Items = libraryItems,
                    IconRef = libraryItems.FirstOrDefault()?.AvatarRef
                });

            // Локальные паки текущего проекта.
            result.AddRange(GetLocalPacks());

            return result;
        }

        private List<CharacterAvatarItem> GetLibraryItems()
        {
            if (!Directory.Exists(LibraryPath)) return new List<CharacterAvatarItem>();
            try
            {
                return Directory.GetFiles(LibraryPath)
                    .Where(f => AllowedExtensions.Contains(
                        Path.GetExtension(f).ToLowerInvariant()))
                    .Select(f => new CharacterAvatarItem
                    {
                        AvatarRef = $"lib:{Path.GetFileName(f)}",
                        FileName = Path.GetFileName(f),
                        Source = CharacterAvatarSource.Library,
                        PackId = LibraryPackId,
                        Scope = CharacterAvatarPackScope.Global
                    })
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "GetLibraryItems failed");
                return new List<CharacterAvatarItem>();
            }
        }

        // ── Локальные паки в архиве проекта ───────────────────────────────

        private List<CharacterAvatarPackInfo> GetLocalPacks()
        {
            var result = new List<CharacterAvatarPackInfo>();
            if (_context == null) return result;

            try
            {
                // GetFiles отдаёт всё поддерево одним списком — раскладываем
                // по пакам сами, по первому сегменту после папки паков.
                var prefix = ZipPacksFolder + "/";
                var byPack = new Dictionary<string, List<string>>(StringComparer.Ordinal);

                foreach (var full in _context.GetFiles(ZipPacksFolder))
                {
                    var normalized = full.Replace('\\', '/');
                    if (!normalized.StartsWith(prefix, StringComparison.Ordinal)) continue;

                    var rest = normalized[prefix.Length..];
                    var slash = rest.IndexOf('/');
                    if (slash <= 0) continue;

                    var packId = rest[..slash];
                    var fileName = rest[(slash + 1)..];
                    if (fileName.Contains('/')) continue;

                    if (!byPack.TryGetValue(packId, out var list))
                    {
                        list = new List<string>();
                        byPack[packId] = list;
                    }
                    list.Add(fileName);
                }

                foreach (var (packId, files) in byPack.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    var pack = ReadLocalPackMeta(packId) ?? new CharacterAvatarPackInfo { Id = packId };
                    pack.Id = packId;
                    pack.Source = CharacterAvatarPackSource.UserLocal;
                    pack.FolderPath = $"{ZipPacksFolder}/{packId}";
                    pack.Items = files
                        .Where(f => AllowedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                        .Select(f => new CharacterAvatarItem
                        {
                            AvatarRef = $"lpack:{packId}:{f}",
                            FileName = f,
                            Source = CharacterAvatarSource.UserPack,
                            PackId = packId,
                            Scope = CharacterAvatarPackScope.Local
                        })
                        .ToList();
                    pack.IconRef = pack.Items
                        .FirstOrDefault(i => i.FileName == pack.IconFileName)?.AvatarRef
                        ?? pack.Items.FirstOrDefault()?.AvatarRef;

                    result.Add(pack);
                }
            }
            catch (Exception ex) { _logger.Error(ex, "GetLocalPacks failed"); }

            return result;
        }

        private CharacterAvatarPackInfo? ReadLocalPackMeta(string packId)
        {
            try
            {
                var bytes = _context?.ReadFile($"{ZipPacksFolder}/{packId}/{PackMetaFileName}");
                if (bytes == null || bytes.Length == 0) return null;
                return JsonSerializer.Deserialize<CharacterAvatarPackInfo>(
                    Encoding.UTF8.GetString(bytes));
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "ReadLocalPackMeta failed: {Id}", packId);
                return null;
            }
        }

        private void WriteLocalPackMeta(CharacterAvatarPackInfo pack)
        {
            if (_context == null || string.IsNullOrEmpty(pack.Id)) return;
            try
            {
                var json = JsonSerializer.Serialize(
                    pack, new JsonSerializerOptions { WriteIndented = true });
                _context.WriteFile(
                    $"{ZipPacksFolder}/{pack.Id}/{PackMetaFileName}",
                    Encoding.UTF8.GetBytes(json));
                _context.FlushStorage();
            }
            catch (Exception ex) { _logger.Error(ex, "WriteLocalPackMeta failed: {Id}", pack.Id); }
        }

        private string GetUniqueLocalPackName(string name, string packId)
        {
            if (_context == null) return name;
            var wo = Path.GetFileNameWithoutExtension(name);
            var ext = Path.GetExtension(name).ToLowerInvariant();
            var c = name; int n = 1;
            while (_context.FileExists($"{ZipPacksFolder}/{packId}/{c}"))
                c = $"{wo} ({n++}){ext}";
            return c;
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
                        PackId = packId,
                        Scope = CharacterAvatarPackScope.Global
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

        public CharacterAvatarPackInfo? CreatePack(string name, CharacterAvatarPackScope scope)
        {
            if (scope == CharacterAvatarPackScope.Global)
                return CreateUserPack(name);

            if (_context == null) return null;

            // Приставка отличает локальный пак от глобального по одному
            // идентификатору: ссылки, меню и перенос между областями работают
            // с ним, не заглядывая в хранилище.
            var id = LocalPackPrefix + Guid.NewGuid().ToString("N")[..8];
            var pack = new CharacterAvatarPackInfo
            {
                Id = id,
                Name = name,
                Source = CharacterAvatarPackSource.UserLocal,
                FolderPath = $"{ZipPacksFolder}/{id}"
            };
            WriteLocalPackMeta(pack);
            return pack;
        }

        public void DeleteUserPack(string packId)
        {
            var dir = Path.Combine(UserPacksPath, packId);
            if (Directory.Exists(dir))
                try { Directory.Delete(dir, recursive: true); }
                catch (Exception ex) { _logger.Error(ex, "DeleteUserPack failed: {Id}", packId); }
        }

        public void UpdatePackMeta(
            string packId, CharacterAvatarPackScope scope, string? name, string? iconFileName)
        {
            // Библиотека — не папка, а склад несгруппированных картинок:
            // ни имени, ни обложки у неё нет.
            if (string.IsNullOrEmpty(packId) || packId == LibraryPackId) return;

            try
            {
                if (scope == CharacterAvatarPackScope.Local)
                {
                    var local = ReadLocalPackMeta(packId) ?? new CharacterAvatarPackInfo();
                    local.Id = packId;
                    local.Source = CharacterAvatarPackSource.UserLocal;
                    local.FolderPath = $"{ZipPacksFolder}/{packId}";
                    if (name != null) local.Name = name;
                    local.IconFileName = iconFileName;
                    WriteLocalPackMeta(local);
                    return;
                }

                var dir = Path.Combine(UserPacksPath, packId);
                if (!Directory.Exists(dir)) return;

                var jsonPath = Path.Combine(dir, PackMetaFileName);
                var pack = File.Exists(jsonPath)
                    ? JsonSerializer.Deserialize<CharacterAvatarPackInfo>(File.ReadAllText(jsonPath))
                    : null;

                pack ??= new CharacterAvatarPackInfo();
                pack.Id = packId;
                pack.Source = CharacterAvatarPackSource.UserGlobal;
                pack.FolderPath = dir;
                if (name != null) pack.Name = name;
                pack.IconFileName = iconFileName;
                SavePackJson(pack);
            }
            catch (Exception ex) { _logger.Error(ex, "UpdatePackMeta failed: {Id}", packId); }
        }

        public void DeletePack(string packId, CharacterAvatarPackScope scope)
        {
            if (string.IsNullOrEmpty(packId) || packId == LibraryPackId) return;

            if (scope == CharacterAvatarPackScope.Global)
            {
                DeleteUserPack(packId);
                return;
            }

            if (_context == null) return;
            try
            {
                var prefix = $"{ZipPacksFolder}/{packId}/";
                var files = _context.GetFiles($"{ZipPacksFolder}/{packId}").ToList();
                foreach (var full in files)
                {
                    EvictCachedBytes($"lpack:{packId}:{Path.GetFileName(full)}");
                    _context.DeleteFile(full);
                }

                // Метаданные попадают в тот же список, но на случай пустого
                // пака удаляем их отдельно — тогда список выше пуст.
                if (_context.FileExists(prefix + PackMetaFileName))
                    _context.DeleteFile(prefix + PackMetaFileName);

                _context.FlushStorage();
            }
            catch (Exception ex) { _logger.Error(ex, "DeletePack failed: {Id}", packId); }
        }

        /// <summary>
        /// Опознаватель копии пака в другой области. Выводится из исходного, а
        /// не берётся случайным: уложить один и тот же пак в проект дважды —
        /// обычное дело (добавили картинок и уложили снова), и каждый раз это
        /// должен быть тот же пак, а не ещё один его близнец рядом.
        /// </summary>
        private static string CopyPackId(string sourceId, CharacterAvatarPackScope targetScope)
        {
            var clean = sourceId.StartsWith(LocalPackPrefix, StringComparison.Ordinal)
                ? sourceId[LocalPackPrefix.Length..]
                : sourceId;

            var sb = new StringBuilder(clean.Length);
            foreach (var ch in clean)
                if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_') sb.Append(ch);

            var id = sb.Length == 0 ? Guid.NewGuid().ToString("N")[..8] : sb.ToString();
            if (id.Length > 32) id = id[..32];

            return targetScope == CharacterAvatarPackScope.Local ? LocalPackPrefix + id : id;
        }

        /// <summary>Пак с таким опознавателем уже есть в этой области.</summary>
        private CharacterAvatarPackInfo? FindPack(string packId, CharacterAvatarPackScope scope)
            => GetAllPacks().FirstOrDefault(p =>
                string.Equals(p.Id, packId, StringComparison.Ordinal) && p.Scope == scope);

        /// <summary>
        /// Завести пак с заданным опознавателем. Уже существующий возвращается
        /// как есть: повторная укладка дополняет пак, а не заводит второй.
        /// </summary>
        private CharacterAvatarPackInfo? EnsurePack(
            string packId, string name, CharacterAvatarPackScope scope)
        {
            var existing = FindPack(packId, scope);
            if (existing != null) return existing;

            if (scope == CharacterAvatarPackScope.Local)
            {
                if (_context == null) return null;

                var local = new CharacterAvatarPackInfo
                {
                    Id = packId,
                    Name = name,
                    Source = CharacterAvatarPackSource.UserLocal,
                    FolderPath = $"{ZipPacksFolder}/{packId}"
                };
                WriteLocalPackMeta(local);
                return local;
            }

            var dir = Path.Combine(UserPacksPath, packId);
            Directory.CreateDirectory(dir);
            var pack = new CharacterAvatarPackInfo
            {
                Id = packId,
                Name = name,
                Source = CharacterAvatarPackSource.UserGlobal,
                FolderPath = dir
            };
            SavePackJson(pack);
            return pack;
        }

        /// <summary>
        /// Положить копию пака в другую область хранения. Исходник остаётся на
        /// месте — в этом вся разница с переносом.
        ///
        /// Это и есть ответ на «хочу отдать проект вместе со своим паком»: пак
        /// ложится копией в архив, у отправителя он никуда не девается и дальше
        /// доступен во всех его проектах, а получатель открывает проект и видит
        /// пак целиком, ничего не устанавливая.
        ///
        /// Ссылки персонажей переписываются на копию только при укладке в
        /// проект. В обратную сторону — когда проектный пак делают общим —
        /// проект продолжает смотреть в свой архив: он должен оставаться
        /// самодостаточным независимо от того, что лежит в настройках программы.
        /// </summary>
        public async Task<CharacterAvatarPackInfo?> CopyPackToScopeAsync(
            string packId, CharacterAvatarPackScope targetScope)
        {
            if (string.IsNullOrEmpty(packId) || packId == LibraryPackId) return null;

            var source = GetAllPacks().FirstOrDefault(p => p.Id == packId);
            if (source == null) return null;
            if (source.Scope == targetScope) return source;

            // Содержимое читается до создания приёмника: если исходный пак
            // прочитать не удалось, пустой пак на новом месте не появится.
            var payload = new List<(CharacterAvatarItem Item, byte[] Data)>();
            foreach (var item in source.Items)
            {
                var bytes = LoadAvatarBytes(item.AvatarRef);
                if (bytes != null) payload.Add((item, bytes));
            }

            // Пустой пак копируется: у него есть имя и обложка, и завести его на
            // новом месте — осмысленное действие. А вот пак, у которого файлы
            // числятся, но ни один не читается, копировать нечем: на новом месте
            // получилась бы пустая оболочка вместо набора картинок.
            if (payload.Count == 0 && source.Items.Count > 0)
            {
                _logger.Warning("CopyPackToScope: nothing readable in {Id}", packId);
                return null;
            }

            var targetId = CopyPackId(packId, targetScope);
            var target = EnsurePack(targetId, source.Name ?? packId, targetScope);
            if (target == null) return null;

            // Что уже лежит в приёмнике, второй раз не кладётся: повторная
            // укладка того же пака должна дополнять его, а не удваивать.
            var existingByHash = new Dictionary<string, string>(StringComparer.Ordinal);
            var alreadyThere = FindPack(targetId, targetScope);
            if (alreadyThere?.Items != null)
            {
                foreach (var item in alreadyThere.Items)
                {
                    var hash = GetHashOf(item.AvatarRef);
                    if (hash != null) existingByHash[hash] = item.AvatarRef;
                }
            }

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (item, data) in payload)
            {
                if (existingByHash.TryGetValue(ComputeHash(data), out var already))
                {
                    map[item.AvatarRef] = already;
                    continue;
                }

                var copied = await SaveToPackAsync(data, item.FileName, targetId);
                if (copied != null) map[item.AvatarRef] = copied;
            }

            // Имя и обложка — часть пака, и без них копия выглядит чужой.
            UpdatePackMeta(targetId, targetScope, source.Name, source.IconFileName);

            if (targetScope == CharacterAvatarPackScope.Local) RaiseRemap(map);

            _context?.FlushStorage();
            return FindPack(targetId, targetScope);
        }

        public async Task<CharacterAvatarPackInfo?> MovePackToScopeAsync(
            string packId, CharacterAvatarPackScope targetScope)
        {
            if (string.IsNullOrEmpty(packId) || packId == LibraryPackId) return null;

            var source = GetAllPacks().FirstOrDefault(p => p.Id == packId);
            if (source == null || !source.IsEditable) return null;
            if (source.Scope == targetScope) return source;

            // Перенос — это копия и удаление исходника, и порядок здесь не
            // косметический: пока копия не легла, удалять нечего.
            //
            // Раньше перенос молча стирал аватарки. Пак на новом месте получал
            // новый опознаватель, старый удалялся, а у персонажей оставались
            // ссылки на пак, которого больше нет: аватарки гасли все разом, и
            // вернуть их было неоткуда — исходник уже стёрт. Чинилась при этом
            // только строка «Недавних», то есть ровно то, что дешевле всего
            // потерять.
            var oldRefs = source.Items.Select(i => i.AvatarRef).ToList();

            var copied = await CopyPackToScopeAsync(packId, targetScope);
            if (copied == null) return null;

            var map = BuildRemapByContent(oldRefs, copied);
            DeletePack(packId, source.Scope);

            // Переписывать ссылки нужно и при переносе из проекта наружу: там
            // исходника тоже не остаётся, и указывать персонажам некуда.
            RaiseRemap(map);

            // Старые записи «Недавних» указывают в пустоту — убираем их, чтобы
            // список не собирал мёртвые строки.
            foreach (var oldRef in oldRefs)
                RemoveRecentAvatar(oldRef);

            return GetAllPacks().FirstOrDefault(p => p.Id == copied.Id);
        }

        /// <summary>
        /// Сопоставить прежние ссылки пака с новыми по содержимому файлов.
        /// По именам сопоставлять нельзя: приёмник имеет право переименовать
        /// файл, если такое имя у него уже занято.
        /// </summary>
        private Dictionary<string, string> BuildRemapByContent(
            IReadOnlyList<string> oldRefs, CharacterAvatarPackInfo target)
        {
            var byHash = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in target.Items)
            {
                var hash = GetHashOf(item.AvatarRef);
                if (hash != null && !byHash.ContainsKey(hash)) byHash[hash] = item.AvatarRef;
            }

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var oldRef in oldRefs)
            {
                var hash = GetHashOf(oldRef);
                if (hash != null && byHash.TryGetValue(hash, out var newRef))
                    map[oldRef] = newRef;
            }
            return map;
        }

        public async Task MoveAvatarToPackAsync(string avatarRef, string targetPackId)
        {
            var bytes = LoadAvatarBytes(avatarRef);
            if (bytes == null) return;
            var fileName = ExtractFileName(avatarRef);

            var oldRef = CharacterAvatarRef.BaseOf(avatarRef);

            // Локальный пак и любой пак, кроме библиотеки, обслуживает общий
            // путь сохранения: он сам выбирает архив проекта или %AppData%.
            if (targetPackId != LibraryPackId && IsLocalPackId(targetPackId))
            {
                var moved = await SaveToPackAsync(bytes, fileName, targetPackId);
                if (moved == null) return;

                DeleteAvatar(avatarRef);
                RemapOne(oldRef, moved);
                return;
            }

            var targetDir = targetPackId == LibraryPackId
                ? LibraryPath
                : Path.Combine(UserPacksPath, targetPackId);
            Directory.CreateDirectory(targetDir);

            var uniqueName = GetUniqueName(fileName, targetDir);
            await File.WriteAllBytesAsync(Path.Combine(targetDir, uniqueName), bytes);

            var newRef = targetPackId == LibraryPackId
                ? $"lib:{uniqueName}"
                : $"pack:{targetPackId}:{uniqueName}";
            RememberHash(newRef, bytes);

            DeleteAvatar(avatarRef);
            RemapOne(oldRef, newRef);
        }

        /// <summary>
        /// Сообщить о смене одной ссылки. Картинка переехала — всё, что на неё
        /// смотрело, должно смотреть на новое место, иначе на карточках
        /// остаются пустые кружки от файла, который никуда не пропадал.
        /// </summary>
        private void RemapOne(string? oldRef, string? newRef)
        {
            if (string.IsNullOrEmpty(oldRef) || string.IsNullOrEmpty(newRef)) return;
            if (string.Equals(oldRef, newRef, StringComparison.Ordinal)) return;

            RemoveRecentAvatar(oldRef);
            RaiseRemap(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [oldRef!] = newRef!
            });
        }

        public async Task<CharacterAvatarPackInfo?> ImportPackFromZipAsync(string zipPath)
        {
            try
            {
                var tempId = Guid.NewGuid().ToString("N")[..8];
                var dir = Path.Combine(UserPacksPath, tempId);
                Directory.CreateDirectory(dir);
                await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, dir, overwriteFiles: true));

                var jsonPath = Path.Combine(dir, PackMetaFileName);
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
                // Локальный пак лежит в архиве проекта: собираем zip из его
                // содержимого, а не копируем папку с диска — папки нет.
                if (IsLocalPackId(packId))
                {
                    var pack = GetLocalPacks().FirstOrDefault(p => p.Id == packId);
                    if (pack == null) return;

                    var entries = new List<(string Name, byte[] Data)>();
                    foreach (var item in pack.Items)
                    {
                        var bytes = LoadAvatarBytes(item.AvatarRef);
                        if (bytes != null) entries.Add((item.FileName, bytes));
                    }

                    var metaJson = JsonSerializer.Serialize(
                        pack, new JsonSerializerOptions { WriteIndented = true });
                    entries.Add((PackMetaFileName, Encoding.UTF8.GetBytes(metaJson)));

                    await Task.Run(() =>
                    {
                        using var zipStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
                        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);
                        foreach (var (name, data) in entries)
                        {
                            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                            using var target = entry.Open();
                            target.Write(data, 0, data.Length);
                        }
                    });
                    return;
                }

                var dir = Path.Combine(UserPacksPath, packId);
                if (!Directory.Exists(dir)) return;
                await Task.Run(() =>
                    ZipFile.CreateFromDirectory(dir, outputPath, CompressionLevel.Optimal, false));
            }
            catch (Exception ex) { _logger.Error(ex, "ExportPackToZipAsync failed: {Id}", packId); }
        }

        // ── Недавние ──────────────────────────────────────────────────────

        public IReadOnlyList<CharacterAvatarItem> GetRecentAvatars()
        {
            var data = LoadRecents();
            var result = new List<CharacterAvatarItem>();

            foreach (var entry in data.Entries)
            {
                if (string.IsNullOrEmpty(entry.AvatarRef)) continue;

                // Записи чужих проектов прочитать нечем — они просто не
                // показываются. Чистить хранилище при этом не нужно: тот же
                // проект откроют снова, и его записи вернутся в список.
                if (LoadAvatarBytes(entry.AvatarRef) == null) continue;

                var baseRef = CharacterAvatarRef.BaseOf(entry.AvatarRef) ?? entry.AvatarRef;
                result.Add(new CharacterAvatarItem
                {
                    AvatarRef = entry.AvatarRef,
                    FileName = ExtractFileName(baseRef),
                    Source = SourceOfRef(baseRef),
                    Scope = ScopeOfRef(baseRef)
                });
            }

            return result;
        }

        public void AddRecentAvatar(string? avatarRef)
        {
            if (string.IsNullOrEmpty(avatarRef)) return;
            var baseRef = CharacterAvatarRef.BaseOf(avatarRef);
            if (string.IsNullOrEmpty(baseRef)) return;

            var data = LoadRecents();

            // Совпадение по адресу файла, а не по всей ссылке: одна картинка
            // стоит в списке одной записью, сколько бы разных кадров из неё ни
            // сделали. Запись при этом обновляется на последний кадр.
            data.Entries.RemoveAll(e =>
                CharacterAvatarRef.SameFile(e.AvatarRef, baseRef));

            data.Entries.Insert(0, new CharacterAvatarRecentEntry
            {
                AvatarRef = avatarRef,
                UsedAt = DateTime.UtcNow
            });

            if (data.Entries.Count > RecentsMaxEntries)
                data.Entries.RemoveRange(RecentsMaxEntries, data.Entries.Count - RecentsMaxEntries);

            SaveRecents(data);
        }

        public void RemoveRecentAvatar(string? avatarRef)
        {
            if (string.IsNullOrEmpty(avatarRef)) return;
            var baseRef = CharacterAvatarRef.BaseOf(avatarRef);
            if (string.IsNullOrEmpty(baseRef)) return;

            var data = LoadRecents();
            var removed = data.Entries.RemoveAll(e =>
                CharacterAvatarRef.SameFile(e.AvatarRef, baseRef));
            if (removed > 0) SaveRecents(data);
        }

        public void ClearRecentAvatars()
        {
            var data = LoadRecents();
            if (data.Entries.Count == 0) return;
            data.Entries.Clear();
            SaveRecents(data);
        }

        private static CharacterAvatarRecentsData LoadRecents()
        {
            try
            {
                var settings = CoreServices.GetService<ISettingsService>();
                var data = settings?.GetModuleSettings<CharacterAvatarRecentsData>(RecentsSettingsKey);
                if (data == null) return new CharacterAvatarRecentsData();
                data.Entries ??= new List<CharacterAvatarRecentEntry>();
                return data;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "LoadRecents failed");
                return new CharacterAvatarRecentsData();
            }
        }

        private static void SaveRecents(CharacterAvatarRecentsData data)
        {
            try
            {
                var settings = CoreServices.GetService<ISettingsService>();
                if (settings == null) return;
                settings.SaveModuleSettings(RecentsSettingsKey, data);
                settings.Save();
            }
            catch (Exception ex) { _logger.Error(ex, "SaveRecents failed"); }
        }

        private static CharacterAvatarSource SourceOfRef(string baseRef)
        {
            if (baseRef.StartsWith("project:", StringComparison.Ordinal)) return CharacterAvatarSource.Project;
            if (baseRef.StartsWith("lib:", StringComparison.Ordinal)) return CharacterAvatarSource.Library;
            if (baseRef.StartsWith("builtin:", StringComparison.Ordinal)) return CharacterAvatarSource.BuiltIn;
            return CharacterAvatarSource.UserPack;
        }

        private static CharacterAvatarPackScope ScopeOfRef(string baseRef) =>
            baseRef.StartsWith("project:", StringComparison.Ordinal)
            || baseRef.StartsWith("lpack:", StringComparison.Ordinal)
                ? CharacterAvatarPackScope.Local
                : CharacterAvatarPackScope.Global;

        // ── Вспомогательные ───────────────────────────────────────────────

        private CharacterAvatarPackInfo? LoadUserPack(string dir)
        {
            try
            {
                var jsonPath = Path.Combine(dir, PackMetaFileName);
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
                        Source = CharacterAvatarSource.UserPack,
                        PackId = pack.Id,
                        Scope = CharacterAvatarPackScope.Global
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
                Path.Combine(pack.FolderPath, PackMetaFileName),
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
            var baseRef = CharacterAvatarRef.BaseOf(avatarRef) ?? avatarRef;

            // builtin: разделяет пак и файл косой чертой, остальные схемы —
            // двоеточием. Берём последний сегмент по обоим разделителям.
            var byColon = baseRef.Split(':')[^1];
            return byColon.Split('/')[^1];
        }
    }
}
