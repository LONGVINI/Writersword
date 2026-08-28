using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Writersword.Core.Interfaces.Services.Storage;
using Writersword.Core.Interfaces.WorkFlows;
using Writersword.Core.Models.Backup;
using Writersword.Core.Models.Settings;

namespace Writersword.Infrastructure.Services.Storage
{
    /// <summary>
    /// История версий проекта на складе объектов с дедупликацией по содержимому.
    ///
    /// Структура хранилища «Роман.wsbk»:
    ///   objects/a3/a3f19c8e….gz   — содержимое записи, имя = SHA-256, сжато GZip
    ///   snapshots/2026-07-30_15-42.json — манифест точки: путь → хеш
    ///
    /// Дедупликация даёт главный выигрыш на неизменных бинарных записях:
    /// аватарки и картинки документа лежат в одном экземпляре на всю историю,
    /// а расти от точки к точке будут только реально меняющиеся JSON модулей.
    /// </summary>
    public class BackupService : IBackupService
    {
        private readonly ILogger<BackupService> _logger;
        private readonly ISettingsService _settingsService;

        /// <summary>Ключ настроек истории версий в ISettingsService.</summary>
        private const string SettingsKey = "backups";

        private const string ObjectsDir = "objects";
        private const string SnapshotsDir = "snapshots";
        private const string SnapshotExtension = ".json";
        private const string ObjectExtension = ".gz";

        /// <summary>
        /// Одновременный доступ к одному хранилищу: снятие точки идёт из потока
        /// сохранения, чтение списка и восстановление — из UI.
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _storeGates =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Кеш переопределений пути, прочитанных из архивов проектов.
        /// GetStoragePath вызывается в том числе из привязки в окне настроек,
        /// то есть с UI-потока и многократно. Без кеша каждое обращение
        /// открывало бы ZIP проекта под общим файловым шлюзом, и интерфейс
        /// замирал бы на время фонового сохранения.
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _overrideCache =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Проекты, для которых перенос старого хранилища уже проверялся.</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _migrationChecked =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Корни, пригодность которых уже проверена.</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _rootUsable =
            new(StringComparer.OrdinalIgnoreCase);

        public BackupService(ISettingsService settingsService)
        {
            _logger = App.Services.GetService<ILogger<BackupService>>()!;
            _settingsService = settingsService;
        }

        // ── Расположение хранилища ────────────────────────────────────────

        public string GetStoragePath(string projectPath)
        {
            var fullProjectPath = Path.GetFullPath(projectPath);
            var root = ResolveStorageRoot(fullProjectPath);

            // Имя папки хранилища — имя проекта плюс отпечаток полного пути.
            // Без отпечатка два «Роман.writersword» из разных папок делили бы
            // одну историю; с ним имя остаётся читаемым и остаётся уникальным.
            var name = Path.GetFileNameWithoutExtension(fullProjectPath);
            var stamp = ShortHash(fullProjectPath);

            var storePath = Path.Combine(root, $"{name}-{stamp}");

            // Перенос проверяется один раз за сессию: обращений к пути много,
            // а старое хранилище может существовать только до первого переезда.
            if (_migrationChecked.TryAdd(fullProjectPath, true))
                MigrateLegacyStore(fullProjectPath, storePath);

            return storePath;
        }

        /// <summary>
        /// Перенос хранилища из прежнего расположения рядом с проектом.
        ///
        /// Раньше история лежала в «Проект.wsbk» по соседству с файлом. После
        /// переезда в папку профиля такие хранилища перестали бы находиться:
        /// список точек оказался бы пустым, а сами точки остались бы на диске
        /// мусором. Перенос делается один раз и только если на новом месте
        /// ещё ничего нет — иначе можно затереть свежую историю старой.
        /// </summary>
        private void MigrateLegacyStore(string fullProjectPath, string storePath)
        {
            try
            {
                if (Directory.Exists(storePath))
                    return;

                var dir = Path.GetDirectoryName(fullProjectPath);
                if (string.IsNullOrEmpty(dir)) return;

                var legacyPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(fullProjectPath) + ".wsbk");

                if (!Directory.Exists(legacyPath))
                    return;

                if (!Directory.Exists(Path.Combine(legacyPath, SnapshotsDir)))
                    return;

                Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
                Directory.Move(legacyPath, storePath);

                WriteStoreMeta(storePath, fullProjectPath);

                _logger.LogInformation("Legacy backup store moved: {From} -> {To}", legacyPath, storePath);
            }
            catch (Exception ex)
            {
                // Перенос не критичен: если он не удался, история просто
                // останется на старом месте, и её можно перенести руками.
                _logger.LogWarning(ex, "Failed to migrate legacy backup store for {Path}", fullProjectPath);
            }
        }

        /// <summary>
        /// Корень, в котором лежат хранилища. Порядок предпочтений:
        /// путь, прописанный в самом проекте, затем путь из настроек
        /// пользователя, затем папка приложения в профиле.
        ///
        /// Рядом с проектом истории по умолчанию больше нет: раскрытая папка
        /// со служебными файлами в рабочем каталоге мешает и провоцирует
        /// «прибраться», удалив единственную копию.
        /// </summary>
        private string ResolveStorageRoot(string fullProjectPath)
        {
            var projectRoot = ReadProjectStorageOverride(fullProjectPath);

            if (!string.IsNullOrWhiteSpace(projectRoot) && IsUsableRoot(projectRoot))
                return projectRoot!;

            var settings = LoadSettings();

            if (!string.IsNullOrWhiteSpace(settings.StoragePath) && IsUsableRoot(settings.StoragePath))
                return settings.StoragePath;

            return DefaultStorageRoot;
        }

        /// <summary>
        /// Папка хранилищ по умолчанию: %LOCALAPPDATA%\Writersword\Backups.
        /// </summary>
        public static string DefaultStorageRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Writersword",
            "Backups");

        /// <summary>
        /// Проверка пути на пригодность. Недоступная папка не считается ошибкой:
        /// проект могли передать другому человеку вместе с путём вида
        /// D:\Работа\Бэкапы, которого у него нет. В этом случае берётся
        /// следующий вариант по списку, а не выбрасывается исключение.
        /// </summary>
        private bool IsUsableRoot(string root)
        {
            // Результат кешируется: проверка вызывается на каждое обращение к
            // пути, а создание папки — операция с диском.
            return _rootUsable.GetOrAdd(root, key =>
            {
                try
                {
                    if (Directory.Exists(key)) return true;

                    Directory.CreateDirectory(key);
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Backup root is not usable, falling back: {Root}", key);
                    return false;
                }
            });
        }

        /// <summary>
        /// Путь к хранилищу, прописанный внутри самого проекта.
        /// Лежит отдельной записью в архиве и переезжает вместе с файлом.
        /// </summary>
        private string? ReadProjectStorageOverride(string projectPath)
        {
            // Пустая строка в кеше означает «переопределения нет»: словарь
            // не хранит null, а различать «не читали» и «прочитали пусто» нужно.
            if (_overrideCache.TryGetValue(projectPath, out var cached))
                return string.IsNullOrEmpty(cached) ? null : cached;

            var value = ReadProjectStorageOverrideCore(projectPath);
            _overrideCache[projectPath] = value ?? string.Empty;
            return value;
        }

        /// <summary>
        /// Открыть архив проекта на чтение, не мешая уже открытому дескриптору.
        ///
        /// ZipFile.OpenRead запрашивает FileShare.Read. В RELEASE-режиме
        /// ZipFileStorageService держит тот же файл открытым на ReadWrite всю
        /// сессию, и Windows отвечает нарушением совместного доступа: режим
        /// шаринга нового запроса не покрывает право записи живого дескриптора.
        /// Явный FileStream с FileShare.ReadWrite это право разрешает и
        /// открывается поверх. FileShare.Delete добавлен ради сохранения через
        /// временный файл с последующей заменой.
        ///
        /// Читать под открытым на запись дескриптором безопасно, потому что все
        /// вызывающие места удерживают ProjectFileLock на время работы с
        /// архивом: параллельной записи в этот момент нет.
        /// </summary>
        private static ZipArchive OpenProjectForRead(string projectPath)
        {
            var stream = new FileStream(
                projectPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            return new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        }

        /// <summary>
        /// Сбросить на диск записи открытого хранилища проекта.
        ///
        /// Нужен всем, кто читает .writersword как файл: пока проект открыт,
        /// ZipArchive в режиме Update копит изменения в памяти и пишет их только
        /// при закрытии. Если проект не открыт — сбрасывать нечего, и это не
        /// ошибка.
        ///
        /// Метод синхронный и сам берёт ProjectFileLock, поэтому вызывать его
        /// можно только вне уже взятого шлюза.
        /// </summary>
        private void FlushOpenStorage(string projectPath)
        {
            try
            {
                var workflow = App.Services.GetService<IProjectWorkflow>();
                workflow?.GetFileStorageForProject(projectPath)?.Flush();
            }
            catch (Exception ex)
            {
                // Неудачный сброс не повод отменять точку: она просто окажется
                // на состоянии последней записи на диск, а это лучше, чем ничего.
                _logger.LogWarning(ex, "Failed to flush open storage before snapshot: {Path}", projectPath);
            }
        }

        private string? ReadProjectStorageOverrideCore(string projectPath)
        {
            try
            {
                if (!File.Exists(projectPath)) return null;

                using var fileGate = ProjectFileLock.Acquire(projectPath);
                using var archive = OpenProjectForRead(projectPath);

                var entry = archive.GetEntry(ProjectOverrideEntry);
                if (entry == null) return null;

                using var stream = entry.Open();
                using var reader = new StreamReader(stream);

                var json = reader.ReadToEnd();
                var settings = JsonConvert.DeserializeObject<BackupSettings>(json);

                return settings?.StoragePath;
            }
            catch (Exception ex)
            {
                // Нечитаемое переопределение не должно ломать историю целиком.
                _logger.LogWarning(ex, "Failed to read project backup override from {Path}", projectPath);
                return null;
            }
        }

        /// <summary>Запись переопределения внутри архива проекта.</summary>
        private const string ProjectOverrideEntry = "backups/storage.json";

        public string? GetProjectStorageOverride(string projectPath)
            => ReadProjectStorageOverride(Path.GetFullPath(projectPath));

        public async Task<bool> SetProjectStorageOverrideAsync(string projectPath, string? path)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var fullPath = Path.GetFullPath(projectPath);

                    if (!File.Exists(fullPath))
                        return false;

                    var value = string.IsNullOrWhiteSpace(path) ? string.Empty : path.Trim();

                    // Пишем через хранилище открытого проекта, если оно есть:
                    // в Release-сборке файл держится открытым, и вторая попытка
                    // открыть его на запись упрётся в занятость.
                    var workflow = App.Services.GetService<IProjectWorkflow>();
                    var storage = workflow?.GetFileStorageForProject(fullPath);

                    var json = JsonConvert.SerializeObject(
                        new BackupSettings { StoragePath = value }, Formatting.Indented);

                    if (storage != null)
                    {
                        if (string.IsNullOrEmpty(value))
                        {
                            if (storage.FileExists(ProjectOverrideEntry))
                                storage.DeleteFile(ProjectOverrideEntry);
                        }
                        else
                        {
                            storage.WriteFile(ProjectOverrideEntry, System.Text.Encoding.UTF8.GetBytes(json));
                        }
                    }
                    else
                    {
                        WriteOverrideDirectly(fullPath, value, json);
                    }

                    // Кеш переопределения и признак пригодности корня устарели.
                    _overrideCache[fullPath] = value;
                    _migrationChecked.TryRemove(fullPath, out _);

                    _logger.LogInformation(
                        "Project backup override set for {Path}: '{Value}'", fullPath, value);

                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to set project backup override for {Path}", projectPath);
                    return false;
                }
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Запись переопределения прямо в архив — путь для закрытого проекта.
        /// </summary>
        private void WriteOverrideDirectly(string projectPath, string value, string json)
        {
            using var fileGate = ProjectFileLock.Acquire(projectPath);
            using var archive = ZipFile.Open(projectPath, ZipArchiveMode.Update);

            var existing = archive.GetEntry(ProjectOverrideEntry);
            existing?.Delete();

            if (string.IsNullOrEmpty(value))
                return;

            var entry = archive.CreateEntry(ProjectOverrideEntry, CompressionLevel.Optimal);

            using var stream = entry.Open();
            using var writer = new StreamWriter(stream);
            writer.Write(json);
        }

        /// <summary>Короткий отпечаток пути для имени папки хранилища.</summary>
        private static string ShortHash(string value)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
            return Convert.ToHexString(bytes).Substring(0, 8).ToLowerInvariant();
        }

        private BackupSettings LoadSettings()
            => _settingsService.GetModuleSettings<BackupSettings>(SettingsKey) ?? new BackupSettings();

        private static SemaphoreSlim GetGate(string storePath)
            => _storeGates.GetOrAdd(storePath, _ => new SemaphoreSlim(1, 1));

        // ── Создание точки ────────────────────────────────────────────────

        public async Task<bool> CreateSnapshotAsync(string projectPath, BackupTrigger trigger)
        {
            var settings = LoadSettings();

            // Точка перед откатом создаётся даже при выключенной истории.
            // Диалог отката обещает, что вернуться назад можно; без этой точки
            // обещание было бы ложью, а откат — необратимым.
            if (!settings.Enabled && trigger != BackupTrigger.BeforeRestore)
            {
                _logger.LogDebug("Backups disabled, snapshot skipped");
                return false;
            }

            if (!IsTriggerAllowed(settings, trigger))
            {
                _logger.LogDebug("Snapshot skipped: trigger {Trigger} is off in settings", trigger);
                return false;
            }

            if (string.IsNullOrEmpty(projectPath) || !File.Exists(projectPath))
            {
                _logger.LogDebug("Snapshot skipped: project file does not exist yet: {Path}", projectPath);
                return false;
            }

            var storePath = GetStoragePath(projectPath);
            var gate = GetGate(storePath);
            await gate.WaitAsync().ConfigureAwait(false);

            try
            {
                Directory.CreateDirectory(Path.Combine(storePath, ObjectsDir));
                Directory.CreateDirectory(Path.Combine(storePath, SnapshotsDir));

                WriteStoreMeta(storePath, projectPath);

                var snapshot = new BackupSnapshot
                {
                    CreatedAt = DateTimeOffset.Now,
                    Trigger = trigger,
                    ProjectTitle = Path.GetFileNameWithoutExtension(projectPath)
                };

                // Точка снимается с файла на диске, а ZipArchive в режиме Update
                // держит записи в памяти до закрытия. Всё, что писалось через
                // IProjectFileStorage без последующего сброса — свежий аватар,
                // локальный пак — на диск ещё не попало, и без сброса точка
                // окажется неполной. Особенно это важно перед сжатием проекта и
                // перед откатом: там точка — единственный способ вернуть удалённое.
                //
                // Строго до взятия шлюза ниже: Flush внутри берёт тот же
                // ProjectFileLock, а он нереентерабельный — вызов под уже взятым
                // шлюзом даст мёртвую блокировку.
                FlushOpenStorage(projectPath);

                // Файл проекта читается под общим шлюзом: параллельно его может
                // перезаписывать сохранение или хешировать кеш-сервис.
                using (var fileGate = await ProjectFileLock.AcquireAsync(projectPath).ConfigureAwait(false))
                using (var archive = OpenProjectForRead(projectPath))
                {
                    foreach (var entry in archive.Entries)
                    {
                        // Папки внутри ZIP приходят как записи нулевой длины с
                        // пустым Name — восстанавливать их отдельно не нужно.
                        if (string.IsNullOrEmpty(entry.Name))
                            continue;

                        byte[] content;
                        using (var entryStream = entry.Open())
                        using (var buffer = new MemoryStream())
                        {
                            await entryStream.CopyToAsync(buffer).ConfigureAwait(false);
                            content = buffer.ToArray();
                        }

                        var hash = ComputeHash(content);
                        WriteObjectIfMissing(storePath, hash, content);

                        snapshot.Entries.Add(new BackupEntry
                        {
                            Path = entry.FullName,
                            Hash = hash,
                            Length = content.LongLength,
                            LastWriteTime = entry.LastWriteTime
                        });
                    }
                }

                if (snapshot.Entries.Count == 0)
                {
                    _logger.LogWarning("Snapshot skipped: project archive has no entries: {Path}", projectPath);
                    return false;
                }

                var previous = ReadSnapshots(storePath).OrderByDescending(s => s.CreatedAt).FirstOrDefault();

                // Точка не создаётся, если предыдущая описывает ровно тот же
                // набор хешей. Служебные записи при сравнении игнорируются:
                // раскладка окон в workspace.json меняется от переключения
                // вкладок и делала «другой» точку с теми же данными.
                if (previous != null && SameContent(previous, snapshot))
                {
                    _logger.LogDebug("Snapshot skipped: content identical to previous point");
                    return false;
                }

                // Ограничение частоты. Ручная точка, точка при закрытии и точка
                // перед откатом проходят всегда: они привязаны к событию, а не
                // к фоновому ритму, и пропускать их нельзя.
                if (previous != null
                    && settings.MinIntervalMinutes > 0
                    && trigger is BackupTrigger.ManualSave or BackupTrigger.AutoSave
                    && (snapshot.CreatedAt - previous.CreatedAt).TotalMinutes < settings.MinIntervalMinutes)
                {
                    _logger.LogDebug(
                        "Snapshot skipped: last point was {Minutes:0} minutes ago, minimum is {Min}",
                        (snapshot.CreatedAt - previous.CreatedAt).TotalMinutes, settings.MinIntervalMinutes);
                    return false;
                }

                snapshot.Id = BuildSnapshotId(storePath, snapshot.CreatedAt);

                var manifestPath = Path.Combine(storePath, SnapshotsDir, snapshot.Id + SnapshotExtension);
                var json = JsonConvert.SerializeObject(snapshot, Formatting.Indented);
                await File.WriteAllTextAsync(manifestPath, json).ConfigureAwait(false);

                _logger.LogDebug("Snapshot created: {Id} ({Count} entries) for {Path}",
                    snapshot.Id, snapshot.Entries.Count, projectPath);

                Prune(storePath, settings);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create snapshot for {Path}", projectPath);
                return false;
            }
            finally
            {
                gate.Release();
            }
        }

        // ── Чтение списка ─────────────────────────────────────────────────

        public async Task<IReadOnlyList<BackupSnapshotInfo>> GetSnapshotsAsync(string projectPath)
        {
            var storePath = GetStoragePath(projectPath);

            return await Task.Run(() =>
            {
                try
                {
                    return (IReadOnlyList<BackupSnapshotInfo>)ReadSnapshots(storePath)
                        .OrderByDescending(s => s.CreatedAt)
                        .Select(s => new BackupSnapshotInfo
                        {
                            Id = s.Id,
                            CreatedAt = s.CreatedAt,
                            Trigger = s.Trigger,
                            ProjectTitle = s.ProjectTitle,
                            EntryCount = s.Entries.Count,
                            TotalLength = s.Entries.Sum(e => e.Length),
                            ModuleSizes = BuildModuleSizes(s)
                        })
                        .ToList();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to read snapshots from {Store}", storePath);
                    return Array.Empty<BackupSnapshotInfo>();
                }
            }).ConfigureAwait(false);
        }

        public async Task<bool> MoveStoreAsync(string oldProjectPath, string newProjectPath)
        {
            return await Task.Run(() =>
            {
                try
                {
                    if (string.IsNullOrEmpty(oldProjectPath) || string.IsNullOrEmpty(newProjectPath))
                        return false;

                    var oldStore = GetStoragePath(oldProjectPath);
                    var newStore = GetStoragePath(newProjectPath);

                    if (string.Equals(oldStore, newStore, StringComparison.OrdinalIgnoreCase))
                        return true;

                    if (!Directory.Exists(oldStore))
                        return false;

                    // Если на новом месте уже есть история, старую не трогаем:
                    // «Сохранить как» поверх существующего проекта не должно
                    // затирать его собственные точки.
                    if (Directory.Exists(newStore))
                    {
                        _logger.LogWarning(
                            "Store move skipped, target already exists: {Target}", newStore);
                        return false;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(newStore)!);
                    Directory.Move(oldStore, newStore);

                    WriteStoreMeta(newStore, newProjectPath);

                    _logger.LogInformation("Backup store moved: {From} -> {To}", oldStore, newStore);
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to move store from {Old} to {New}",
                        oldProjectPath, newProjectPath);
                    return false;
                }
            }).ConfigureAwait(false);
        }

        // ── Список хранилищ и уборка ──────────────────────────────────────

        /// <summary>Имя файла с описанием хранилища.</summary>
        private const string StoreMetaFile = "store.json";

        /// <summary>
        /// Записать, какому проекту принадлежит хранилище. Без этой пометки
        /// папка с историей неотличима от любой другой, и понять, чью историю
        /// можно удалить, невозможно.
        /// </summary>
        private void WriteStoreMeta(string storePath, string projectPath)
        {
            try
            {
                var metaPath = Path.Combine(storePath, StoreMetaFile);

                var meta = new BackupStoreInfo
                {
                    Path = storePath,
                    ProjectPath = Path.GetFullPath(projectPath),
                    ProjectName = Path.GetFileNameWithoutExtension(projectPath)
                };

                File.WriteAllText(metaPath, JsonConvert.SerializeObject(meta, Formatting.Indented));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write store meta for {Store}", storePath);
            }
        }

        public async Task<IReadOnlyList<BackupStoreInfo>> ListStoresAsync()
        {
            return await Task.Run(() =>
            {
                var result = new List<BackupStoreInfo>();

                foreach (var root in GetKnownRoots())
                {
                    if (!Directory.Exists(root)) continue;

                    foreach (var dir in Directory.EnumerateDirectories(root))
                    {
                        try
                        {
                            // Признак хранилища — папка снимков. Посторонние
                            // каталоги в корне не показываем и не трогаем.
                            if (!Directory.Exists(Path.Combine(dir, SnapshotsDir)))
                                continue;

                            var info = ReadStoreMeta(dir) ?? new BackupStoreInfo
                            {
                                ProjectName = Path.GetFileName(dir)
                            };

                            info.Path = dir;
                            info.ProjectExists = !string.IsNullOrEmpty(info.ProjectPath)
                                                 && File.Exists(info.ProjectPath);

                            var snapshots = ReadSnapshots(dir);
                            info.SnapshotCount = snapshots.Count;
                            info.LastSnapshot = snapshots.Count > 0
                                ? snapshots.Max(s => s.CreatedAt)
                                : null;

                            info.SizeBytes = Directory
                                .EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                                .Sum(f => new FileInfo(f).Length);

                            result.Add(info);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to read store {Dir}", dir);
                        }
                    }
                }

                return (IReadOnlyList<BackupStoreInfo>)result
                    .OrderBy(s => s.ProjectExists ? 0 : 1)
                    .ThenByDescending(s => s.LastSnapshot)
                    .ToList();
            }).ConfigureAwait(false);
        }

        public async Task<bool> DeleteStoreAsync(string storePath)
        {
            return await Task.Run(() =>
            {
                try
                {
                    if (string.IsNullOrEmpty(storePath) || !Directory.Exists(storePath))
                        return false;

                    var full = Path.GetFullPath(storePath);

                    // Удаляем только внутри своих корней: ошибка в пути не должна
                    // приводить к сносу произвольной папки на диске.
                    bool inKnownRoot = GetKnownRoots().Any(root =>
                        !string.IsNullOrEmpty(root)
                        && full.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase));

                    if (!inKnownRoot)
                    {
                        _logger.LogError("Refusing to delete store outside known roots: {Path}", full);
                        return false;
                    }

                    Directory.Delete(full, recursive: true);
                    _logger.LogInformation("Backup store deleted: {Path}", full);
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to delete store {Path}", storePath);
                    return false;
                }
            }).ConfigureAwait(false);
        }

        /// <summary>Корни, в которых имеет смысл искать хранилища.</summary>
        private IEnumerable<string> GetKnownRoots()
        {
            yield return DefaultStorageRoot;

            var settings = LoadSettings();

            if (!string.IsNullOrWhiteSpace(settings.StoragePath))
                yield return settings.StoragePath;
        }

        private BackupStoreInfo? ReadStoreMeta(string storePath)
        {
            try
            {
                var metaPath = Path.Combine(storePath, StoreMetaFile);

                if (!File.Exists(metaPath)) return null;

                return JsonConvert.DeserializeObject<BackupStoreInfo>(File.ReadAllText(metaPath));
            }
            catch
            {
                return null;
            }
        }

        public async Task<Dictionary<string, long>> GetCurrentModuleSizesAsync(string projectPath)
        {
            return await Task.Run(() =>
            {
                var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

                try
                {
                    if (!File.Exists(projectPath)) return result;

                    using var fileGate = ProjectFileLock.Acquire(projectPath);
                    using var archive = OpenProjectForRead(projectPath);

                    foreach (var entry in archive.Entries)
                    {
                        var path = entry.FullName.Replace('\\', '/');

                        if (!path.StartsWith("modules/", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!path.EndsWith("/CustomData.json", StringComparison.OrdinalIgnoreCase)) continue;

                        var parts = path.Split('/');
                        if (parts.Length < 3) continue;

                        result[parts[1]] = entry.Length;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to read current module sizes from {Path}", projectPath);
                }

                return result;
            }).ConfigureAwait(false);
        }

        public async Task<long> GetStorageSizeAsync(string projectPath)
        {
            var storePath = GetStoragePath(projectPath);

            return await Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(storePath)) return 0L;

                    return Directory
                        .EnumerateFiles(storePath, "*", SearchOption.AllDirectories)
                        .Sum(f => new FileInfo(f).Length);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to measure store size: {Store}", storePath);
                    return 0L;
                }
            }).ConfigureAwait(false);
        }

        // ── Восстановление ────────────────────────────────────────────────

        public async Task<bool> RestoreSnapshotAsync(string projectPath, string snapshotId, string targetPath)
        {
            var storePath = GetStoragePath(projectPath);
            var gate = GetGate(storePath);
            await gate.WaitAsync().ConfigureAwait(false);

            try
            {
                var manifestPath = Path.Combine(storePath, SnapshotsDir, snapshotId + SnapshotExtension);

                if (!File.Exists(manifestPath))
                {
                    _logger.LogError("Snapshot manifest not found: {Path}", manifestPath);
                    return false;
                }

                var json = await File.ReadAllTextAsync(manifestPath).ConfigureAwait(false);
                var snapshot = JsonConvert.DeserializeObject<BackupSnapshot>(json);

                if (snapshot == null || snapshot.Entries.Count == 0)
                {
                    _logger.LogError("Snapshot manifest is empty or unreadable: {Path}", manifestPath);
                    return false;
                }

                // Сборка идёт во временный файл и заменяет целевой одним Move —
                // прерывание на середине не оставит полупустой проект.
                string tempPath = targetPath + ".restore.tmp";

                using (var fileGate = await ProjectFileLock.AcquireAsync(targetPath).ConfigureAwait(false))
                {
                    using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite))
                    using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create))
                    {
                        foreach (var entry in snapshot.Entries)
                        {
                            var content = ReadObject(storePath, entry.Hash);

                            if (content == null)
                            {
                                _logger.LogError(
                                    "Object missing for entry {Entry} (hash {Hash}) — restore aborted",
                                    entry.Path, entry.Hash);
                                return false;
                            }

                            var zipEntry = archive.CreateEntry(entry.Path, CompressionLevel.Optimal);
                            zipEntry.LastWriteTime = entry.LastWriteTime;

                            using var target = zipEntry.Open();
                            await target.WriteAsync(content, 0, content.Length).ConfigureAwait(false);
                        }
                    }

                    File.Move(tempPath, targetPath, overwrite: true);
                }

                _logger.LogInformation("Snapshot {Id} restored to {Path}", snapshotId, targetPath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to restore snapshot {Id}", snapshotId);
                return false;
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>Папка, в которой разворачиваются точки для сравнения.</summary>
        private static string CompareTempDir => Path.Combine(Path.GetTempPath(), "Writersword", "compare");

        public void CleanupTempFiles()
        {
            try
            {
                if (!Directory.Exists(CompareTempDir))
                    return;

                int removed = 0;

                // Вызывается при запуске, когда ни одно сравнение не идёт:
                // всё, что здесь лежит, осталось от прошлой сессии. Внутри
                // полные копии проектов — и место, и содержимое книги в общей
                // временной папке.
                foreach (var file in Directory.EnumerateFiles(CompareTempDir))
                {
                    try
                    {
                        File.Delete(file);
                        removed++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Temp compare file is busy, skipped: {File}", file);
                    }
                }

                if (removed > 0)
                    _logger.LogInformation("Compare temp files removed: {Count}", removed);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean compare temp folder");
            }
        }

        public async Task<string?> ExtractSnapshotToTempAsync(string projectPath, string snapshotId)
        {
            try
            {
                var tempDir = CompareTempDir;
                Directory.CreateDirectory(tempDir);

                var name = Path.GetFileNameWithoutExtension(projectPath);
                var ext = Path.GetExtension(projectPath);
                var tempPath = Path.Combine(tempDir, $"{name}-{snapshotId}{ext}");

                bool ok = await RestoreSnapshotAsync(projectPath, snapshotId, tempPath).ConfigureAwait(false);

                if (!ok)
                {
                    _logger.LogError("Failed to extract snapshot {Id} to temp", snapshotId);
                    return null;
                }

                _logger.LogDebug("Snapshot {Id} extracted to {Path}", snapshotId, tempPath);
                return tempPath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to extract snapshot {Id} to temp", snapshotId);
                return null;
            }
        }

        public async Task<bool> DeleteSnapshotAsync(string projectPath, string snapshotId)
        {
            var storePath = GetStoragePath(projectPath);
            var gate = GetGate(storePath);
            await gate.WaitAsync().ConfigureAwait(false);

            try
            {
                var manifestPath = Path.Combine(storePath, SnapshotsDir, snapshotId + SnapshotExtension);

                if (!File.Exists(manifestPath))
                    return false;

                File.Delete(manifestPath);
                CollectGarbage(storePath);

                _logger.LogDebug("Snapshot deleted: {Id}", snapshotId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete snapshot {Id}", snapshotId);
                return false;
            }
            finally
            {
                gate.Release();
            }
        }

        // ── Склад объектов ────────────────────────────────────────────────

        private static string ComputeHash(byte[] content)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(content)).ToLowerInvariant();
        }

        /// <summary>
        /// Путь объекта. Первые два символа хеша уходят в имя подпапки, иначе
        /// в одном каталоге накапливаются десятки тысяч файлов.
        /// </summary>
        private static string GetObjectPath(string storePath, string hash)
            => Path.Combine(storePath, ObjectsDir, hash.Substring(0, 2), hash + ObjectExtension);

        private void WriteObjectIfMissing(string storePath, string hash, byte[] content)
        {
            var objectPath = GetObjectPath(storePath, hash);

            if (File.Exists(objectPath))
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(objectPath)!);

            var tempPath = objectPath + ".tmp";

            using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            using (var gzip = new GZipStream(fileStream, CompressionLevel.Optimal))
            {
                gzip.Write(content, 0, content.Length);
            }

            // Объект появляется в хранилище целиком: манифест не должен
            // ссылаться на файл, запись которого прервалась.
            File.Move(tempPath, objectPath, overwrite: true);
        }

        private byte[]? ReadObject(string storePath, string hash)
        {
            var objectPath = GetObjectPath(storePath, hash);

            if (!File.Exists(objectPath))
                return null;

            using var fileStream = new FileStream(objectPath, FileMode.Open, FileAccess.Read);
            using var gzip = new GZipStream(fileStream, CompressionMode.Decompress);
            using var buffer = new MemoryStream();
            gzip.CopyTo(buffer);
            return buffer.ToArray();
        }

        // ── Манифесты ─────────────────────────────────────────────────────

        private List<BackupSnapshot> ReadSnapshots(string storePath)
            => ReadSnapshots(storePath, out _);

        /// <summary>
        /// Прочитать манифесты хранилища. Через <paramref name="allReadable"/>
        /// сообщается, удалось ли прочитать все: сборщику мусора это критично,
        /// потому что объекты нечитаемого манифеста выглядят ничейными.
        /// </summary>
        private List<BackupSnapshot> ReadSnapshots(string storePath, out bool allReadable)
        {
            var result = new List<BackupSnapshot>();
            allReadable = true;

            var dir = Path.Combine(storePath, SnapshotsDir);

            if (!Directory.Exists(dir))
                return result;

            foreach (var file in Directory.EnumerateFiles(dir, "*" + SnapshotExtension))
            {
                try
                {
                    var snapshot = JsonConvert.DeserializeObject<BackupSnapshot>(File.ReadAllText(file));

                    if (snapshot == null)
                    {
                        allReadable = false;
                        continue;
                    }

                    if (string.IsNullOrEmpty(snapshot.Id))
                        snapshot.Id = Path.GetFileNameWithoutExtension(file);

                    result.Add(snapshot);
                }
                catch (Exception ex)
                {
                    // Битый манифест уносит одну точку, остальные читаются дальше.
                    allReadable = false;
                    _logger.LogError(ex, "Unreadable snapshot manifest: {File}", file);
                }
            }

            return result;
        }

        /// <summary>
        /// Размер данных каждого модуля в точке. Данные модулей лежат по пути
        /// modules/{ModuleType}/CustomData.json — имя модуля берётся из пути.
        /// </summary>
        private static Dictionary<string, long> BuildModuleSizes(BackupSnapshot snapshot)
        {
            var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in snapshot.Entries)
            {
                var path = entry.Path.Replace('\\', '/');

                if (!path.StartsWith("modules/", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!path.EndsWith("/CustomData.json", StringComparison.OrdinalIgnoreCase))
                    continue;

                var parts = path.Split('/');
                if (parts.Length < 3) continue;

                var moduleName = parts[1];
                result[moduleName] = result.TryGetValue(moduleName, out var existing)
                    ? existing + entry.Length
                    : entry.Length;
            }

            return result;
        }

        /// <summary>
        /// Идентификатор по времени создания. При совпадении до секунды
        /// добавляется порядковый суффикс.
        /// </summary>
        private static string BuildSnapshotId(string storePath, DateTimeOffset createdAt)
        {
            var baseId = createdAt.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
            var dir = Path.Combine(storePath, SnapshotsDir);
            var candidate = baseId;
            int suffix = 1;

            while (File.Exists(Path.Combine(dir, candidate + SnapshotExtension)))
            {
                candidate = baseId + "_" + suffix.ToString(CultureInfo.InvariantCulture);
                suffix++;
            }

            return candidate;
        }

        /// <summary>
        /// Служебная запись архива, не относящаяся к содержимому проекта:
        /// раскладка окон и локальные настройки модулей. Меняются от простого
        /// переключения вкладки, поэтому в сравнении точек не участвуют.
        /// </summary>
        private static bool IsServiceEntry(string path)
        {
            var normalized = path.Replace('\\', '/');

            return normalized.Equals("workspace.json", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("local_settings/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Две точки считаются одинаковыми, если совпадает набор пар «путь → хеш»
        /// среди записей с содержимым. Служебные записи игнорируются.
        /// </summary>
        private static bool SameContent(BackupSnapshot a, BackupSnapshot b)
        {
            var mapA = a.Entries
                .Where(e => !IsServiceEntry(e.Path))
                .ToDictionary(e => e.Path, e => e.Hash, StringComparer.Ordinal);

            var contentB = b.Entries.Where(e => !IsServiceEntry(e.Path)).ToList();

            if (mapA.Count != contentB.Count)
                return false;

            foreach (var entry in contentB)
            {
                if (!mapA.TryGetValue(entry.Path, out var hash) || hash != entry.Hash)
                    return false;
            }

            return true;
        }

        /// <summary>Разрешён ли этот повод для точки текущими настройками.</summary>
        private static bool IsTriggerAllowed(BackupSettings settings, BackupTrigger trigger) => trigger switch
        {
            BackupTrigger.ManualSave => settings.OnManualSave,
            BackupTrigger.AutoSave => settings.OnTimer,
            BackupTrigger.AppClose => settings.OnAppClose,
            // Ручная точка и точка перед откатом создаются всегда: первую
            // запросил пользователь, вторая делает откат обратимым.
            _ => true
        };

        // ── Очистка ───────────────────────────────────────────────────────

        /// <summary>
        /// Убирает лишние точки и объекты, на которые больше никто не ссылается.
        ///
        /// Сначала прореживание по времени: за сегодня хранятся все точки, за
        /// последнюю неделю — по одной на день, дальше — по одной на неделю.
        /// Так список остаётся коротким, а глубина истории — месяцы. Затем
        /// жёсткий потолок по числу точек как страховка.
        ///
        /// Точки, созданные пользователем вручную, не удаляются никогда:
        /// он пометил этот момент осознанно.
        /// </summary>
        private void Prune(string storePath, BackupSettings settings)
        {
            try
            {
                var snapshots = ReadSnapshots(storePath)
                    .OrderByDescending(s => s.CreatedAt)
                    .ToList();

                var doomed = new List<BackupSnapshot>();

                if (settings.Thinning)
                    doomed.AddRange(SelectThinnedOut(snapshots));

                doomed.AddRange(SelectExpiredUserPoints(snapshots, settings));

                if (settings.MaxSnapshots > 0)
                {
                    var survivors = snapshots.Except(doomed).ToList();

                    if (survivors.Count > settings.MaxSnapshots)
                    {
                        var overflow = survivors.Skip(settings.MaxSnapshots).ToList();

                        // Ручные точки вытесняются лимитом только в режиме
                        // WithLimit, и всё равно последними: сначала уходят
                        // автоматические, даже если они новее.
                        doomed.AddRange(overflow.Where(s => s.Trigger != BackupTrigger.UserPoint));

                        if (settings.UserPointRetention == UserPointRetention.WithLimit)
                        {
                            int stillOver = survivors.Count - doomed.Count(d => survivors.Contains(d))
                                            - settings.MaxSnapshots;

                            if (stillOver > 0)
                            {
                                doomed.AddRange(survivors
                                    .Where(s => s.Trigger == BackupTrigger.UserPoint)
                                    .OrderBy(s => s.CreatedAt)
                                    .Take(stillOver));
                            }
                        }
                    }
                }

                if (doomed.Count == 0)
                    return;

                foreach (var stale in doomed)
                {
                    var path = Path.Combine(storePath, SnapshotsDir, stale.Id + SnapshotExtension);
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        _logger.LogDebug("Snapshot pruned: {Id} ({Trigger})", stale.Id, stale.Trigger);
                    }
                }

                CollectGarbage(storePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Prune failed for store {Store}", storePath);
            }
        }

        /// <summary>
        /// Ручные точки, отслужившие свой срок по выбранному правилу.
        /// Режимы Never и WithLimit здесь ничего не выбирают: первый не удаляет
        /// вовсе, второй разбирается лимитом.
        /// </summary>
        private static List<BackupSnapshot> SelectExpiredUserPoints(
            List<BackupSnapshot> snapshots, BackupSettings settings)
        {
            var result = new List<BackupSnapshot>();

            var userPoints = snapshots
                .Where(s => s.Trigger == BackupTrigger.UserPoint)
                .OrderByDescending(s => s.CreatedAt)
                .ToList();

            if (userPoints.Count == 0)
                return result;

            switch (settings.UserPointRetention)
            {
                case UserPointRetention.AfterAge:
                    if (settings.UserPointMaxAgeDays > 0)
                    {
                        var edge = DateTimeOffset.Now.AddDays(-settings.UserPointMaxAgeDays);
                        result.AddRange(userPoints.Where(s => s.CreatedAt < edge));
                    }
                    break;

                case UserPointRetention.KeepLast:
                    if (settings.UserPointKeepLast > 0 && userPoints.Count > settings.UserPointKeepLast)
                        result.AddRange(userPoints.Skip(settings.UserPointKeepLast));
                    break;
            }

            return result;
        }

        /// <summary>
        /// Точки, которые убирает прореживание. В каждой корзине времени
        /// остаётся самая свежая точка, остальные уходят.
        /// </summary>
        private static List<BackupSnapshot> SelectThinnedOut(List<BackupSnapshot> snapshots)
        {
            // Корзины считаются по возрасту, а не по календарю. При счёте по
            // датам ночная работа схлопывалась в полночь: точки, сделанные
            // двадцать минут назад, оказывались «за вчера» и прореживались.
            var now = DateTimeOffset.Now;

            var result = new List<BackupSnapshot>();
            var seenBuckets = new HashSet<string>(StringComparer.Ordinal);

            // Идём от свежих к старым: первая точка в корзине — самая новая, она и остаётся.
            foreach (var snapshot in snapshots.OrderByDescending(s => s.CreatedAt))
            {
                if (snapshot.Trigger == BackupTrigger.UserPoint)
                    continue;

                double ageHours = (now - snapshot.CreatedAt).TotalHours;

                // Последние сутки сохраняются целиком: именно к ним откатываются чаще всего.
                if (ageHours <= 24)
                    continue;

                string bucket = ageHours <= 24 * 7
                    ? "d:" + ((int)(ageHours / 24)).ToString(CultureInfo.InvariantCulture)
                    : "w:" + ((int)(ageHours / (24 * 7))).ToString(CultureInfo.InvariantCulture);

                if (!seenBuckets.Add(bucket))
                    result.Add(snapshot);
            }

            return result;
        }

        /// <summary>
        /// Удаляет объекты, не упомянутые ни в одном оставшемся манифесте.
        /// </summary>
        private void CollectGarbage(string storePath)
        {
            try
            {
                var objectsRoot = Path.Combine(storePath, ObjectsDir);
                if (!Directory.Exists(objectsRoot))
                    return;

                var snapshots = ReadSnapshots(storePath, out bool allReadable);

                // Если хоть один манифест не прочитался, уборка отменяется:
                // его объекты выглядели бы ничейными и были бы удалены, а вместе
                // с ними исчез бы шанс починить эту точку. Лишние объекты на
                // диске — плата несопоставимо меньшая.
                if (!allReadable)
                {
                    _logger.LogWarning(
                        "Garbage collection skipped: some manifests are unreadable in {Store}", storePath);
                    return;
                }

                var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var snapshot in snapshots)
                    foreach (var entry in snapshot.Entries)
                        referenced.Add(entry.Hash);

                int removed = 0;

                foreach (var file in Directory.EnumerateFiles(objectsRoot, "*" + ObjectExtension, SearchOption.AllDirectories))
                {
                    var hash = Path.GetFileNameWithoutExtension(file);

                    if (referenced.Contains(hash))
                        continue;

                    File.Delete(file);
                    removed++;
                }

                if (removed > 0)
                    _logger.LogDebug("Garbage collected: {Count} unreferenced objects", removed);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Garbage collection failed for store {Store}", storePath);
            }
        }
    }
}
