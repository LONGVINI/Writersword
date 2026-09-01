using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Writersword.Core.Services.Storage;
using Writersword.Core.Interfaces.Services;
using Writersword.Core.Models.Cache;
using Writersword.Core.Models.Project;

namespace Writersword.Infrastructure.Services.Storage
{
    /// <summary>
    /// Реализация сервиса для работы с кешем в формате ZIP
    /// Кеш хранится в .writersword.wsasd файле (ZIP архив)
    /// Ключ данных модуля — moduleType (строка), не InstanceId (GUID)
    /// При загрузке проверяется ProjectId для защиты от кросс-проектного загрязнения
    /// </summary>
    public class ZipCacheService : IZipCacheService
    {
        private readonly ILogger<ZipCacheService> _logger;
        private readonly IHashService _hashService;
        // Гарантирует что только одна операция одновременно обращается к .wsasd файлу.
        private readonly System.Threading.SemaphoreSlim _fileLock = new(1, 1);

        public ZipCacheService(IHashService hashService)
        {
            _logger = App.Services.GetService<ILogger<ZipCacheService>>()!;
            _hashService = hashService;
        }

        private string GetCachePath(string projectPath)
        {
            var fullPath = Path.GetFullPath(projectPath);
            return fullPath + ".wsasd";
        }

        /// <summary>
        /// Путь резервной копии кеша. Создаётся при каждой записи: аварийное
        /// выключение может застать перезапись основного файла и оставить обрывок,
        /// и тогда точкой восстановления служит предыдущая копия.
        /// </summary>
        private string GetBackupPath(string projectPath) => GetCachePath(projectPath) + ".bak";

        public bool HasCache(string projectPath)
        {
            if (File.Exists(GetCachePath(projectPath))) return true;

            // Основной файл мог не пережить аварию — резервная копия остаётся
            // полноценной точкой восстановления и поднимает режим сравнения.
            return File.Exists(GetBackupPath(projectPath));
        }

        // Читается ли архив кеша: файл существует и в нём есть метаданные.
        private bool IsReadableCacheArchive(string path)
        {
            if (!File.Exists(path)) return false;
            try
            {
                using var archive = ZipFile.OpenRead(path);
                return archive.GetEntry("cache.json") != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Готовит основной файл кеша к чтению. Повреждённый файл восстанавливается
        /// из резервной копии всегда, отсутствующий — только при
        /// restoreMissingFromBackup: во время работы файл могли убрать намеренно
        /// (переключение воркмода), и подменять его копией нельзя. Проверки при
        /// открытии проекта, наоборот, обязаны увидеть уцелевшую копию.
        /// false — читать нечего, вызывающий работает как при отсутствии кеша.
        /// </summary>
        private bool EnsureCacheReadable(string cachePath, bool restoreMissingFromBackup = false)
        {
            if (IsReadableCacheArchive(cachePath)) return true;
            if (!File.Exists(cachePath) && !restoreMissingFromBackup) return false;

            var backupPath = cachePath + ".bak";
            if (IsReadableCacheArchive(backupPath))
            {
                try
                {
                    File.Copy(backupPath, cachePath, overwrite: true);
                    _logger.LogWarning(
                        "Cache file was damaged or missing — restored from backup: {CachePath}", cachePath);
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to restore cache from backup: {CachePath}", cachePath);
                    return false;
                }
            }

            if (File.Exists(cachePath))
                _logger.LogError(
                    "Cache file is damaged and has no usable backup: {CachePath}", cachePath);

            return false;
        }

        public DateTime? GetCacheDate(string projectPath)
        {
            var cachePath = GetCachePath(projectPath);
            if (!File.Exists(cachePath) && !File.Exists(GetBackupPath(projectPath))) return null;

            if (!_fileLock.Wait(TimeSpan.FromSeconds(3)))
            {
                // Таймаут означает что UI-поток заблокирован ожиданием этого же лока
                // (классический дедлок при откате вкладки или закрытии приложения).
                // Возвращаем null — вызывающий код использует project.ModulesData как fallback.
                _logger.LogWarning("Cache lock timeout — returning null, caller uses in-memory fallback");
                return null;
            }
            try
            {
                if (!EnsureCacheReadable(cachePath, restoreMissingFromBackup: true)) return null;
                return LoadMetadata(cachePath)?.CacheDate;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading cache date");
                return null;
            }
            finally
            {
                _fileLock.Release();
            }
        }

        /// <summary>
        /// Загрузить CustomData из кеша
        /// Возвращает словарь moduleType → CustomData
        /// Проверяет ProjectId чтобы убедиться что кеш принадлежит именно этому проекту
        /// </summary>
        /// <param name="projectPath">Путь к .writersword файлу</param>
        /// <param name="expectedProjectId">ID проекта для верификации. Если null — верификация пропускается</param>
        public Dictionary<string, object?>? LoadCache(string projectPath, string? expectedProjectId = null)
        {
            var cachePath = GetCachePath(projectPath);
            if (!File.Exists(cachePath) && !File.Exists(GetBackupPath(projectPath)))
            {
                _logger.LogDebug("Cache not found: {CachePath}", cachePath);
                return null;
            }

            if (!_fileLock.Wait(TimeSpan.FromSeconds(3)))
            {
                // Таймаут означает что UI-поток заблокирован ожиданием этого же лока
                // (классический дедлок при откате вкладки или закрытии приложения).
                // Возвращаем null — вызывающий код использует project.ModulesData как fallback.
                _logger.LogWarning("Cache lock timeout — returning null, caller uses in-memory fallback");
                return null;
            }
            try
            {
                if (!EnsureCacheReadable(cachePath)) return null;

                var customData = new Dictionary<string, object?>();

                using (var archive = ZipFile.OpenRead(cachePath))
                {
                    var metadata = LoadMetadataFromArchive(archive);
                    if (metadata == null)
                    {
                        _logger.LogWarning("Failed to load metadata from cache: {CachePath}", cachePath);
                        return null;
                    }

                    if (!string.IsNullOrEmpty(expectedProjectId)
                        && metadata.ProjectId != expectedProjectId)
                    {
                        _logger.LogError(
                            "Cache ProjectId mismatch: expected {Expected}, got {Actual}. Cache belongs to another project, ignoring.",
                            expectedProjectId, metadata.ProjectId);
                        return null;
                    }

                    _logger.LogDebug("Cache loaded: ProjectId={ProjectId}, Date={CacheDate}, Modules={ModulesCount}",
                        metadata.ProjectId, metadata.CacheDate, metadata.Modules.Count);

                    foreach (var kvp in metadata.Modules)
                    {
                        var moduleType = kvp.Key;

                        var customDataEntry = archive.GetEntry($"modules/{moduleType}/customdata.json");
                        if (customDataEntry != null)
                        {
                            using (var stream = customDataEntry.Open())
                            using (var reader = new StreamReader(stream))
                            {
                                var json = reader.ReadToEnd();
                                var data = JsonConvert.DeserializeObject<object>(json);
                                customData[moduleType] = data;
                                _logger.LogDebug("Loaded cache data for: {moduleType}", moduleType);
                            }
                        }
                    }
                }

                return customData;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading cache: {CachePath}", cachePath);
                return null;
            }
            finally
            {
                _fileLock.Release();
            }
        }

        /// <summary>
        /// Загрузить CustomData И SessionData из кеша одним чтением архива.
        /// </summary>
        public (Dictionary<string, object?> CustomData, Dictionary<string, object?> SessionData)?
            LoadCacheWithSession(string projectPath, string? expectedProjectId = null)
        {
            var cachePath = GetCachePath(projectPath);
            if (!File.Exists(cachePath) && !File.Exists(GetBackupPath(projectPath)))
            {
                _logger.LogDebug("Cache not found: {CachePath}", cachePath);
                return null;
            }

            if (!_fileLock.Wait(TimeSpan.FromSeconds(3)))
            {
                // Таймаут означает что UI-поток заблокирован ожиданием этого же лока
                // (классический дедлок при откате вкладки или закрытии приложения).
                // Возвращаем null — вызывающий код использует project.ModulesData как fallback.
                _logger.LogWarning("Cache lock timeout — returning null, caller uses in-memory fallback");
                return null;
            }
            try
            {
                if (!EnsureCacheReadable(cachePath)) return null;

                var customData = new Dictionary<string, object?>();
                var sessionData = new Dictionary<string, object?>();

                using (var archive = ZipFile.OpenRead(cachePath))
                {
                    var metadata = LoadMetadataFromArchive(archive);
                    if (metadata == null)
                    {
                        _logger.LogWarning("Failed to load metadata from cache: {CachePath}", cachePath);
                        return null;
                    }

                    if (!string.IsNullOrEmpty(expectedProjectId)
                        && metadata.ProjectId != expectedProjectId)
                    {
                        _logger.LogError(
                            "Cache ProjectId mismatch: expected {Expected}, got {Actual}.",
                            expectedProjectId, metadata.ProjectId);
                        return null;
                    }

                    foreach (var kvp in metadata.Modules)
                    {
                        var moduleType = kvp.Key;

                        // CustomData
                        var customEntry = archive.GetEntry($"modules/{moduleType}/customdata.json");
                        if (customEntry != null)
                        {
                            using var stream = customEntry.Open();
                            using var reader = new StreamReader(stream);
                            var json = reader.ReadToEnd();
                            customData[moduleType] = JsonConvert.DeserializeObject<object>(json);
                        }

                        // SessionData
                        var sessionEntry = archive.GetEntry($"modules/{moduleType}/sessiondata.json");
                        if (sessionEntry != null)
                        {
                            using var stream = sessionEntry.Open();
                            using var reader = new StreamReader(stream);
                            var json = reader.ReadToEnd();
                            sessionData[moduleType] = JsonConvert.DeserializeObject<object>(json);
                            _logger.LogDebug("Loaded session data for: {moduleType}", moduleType);
                        }
                    }
                }

                _logger.LogDebug("LoadCacheWithSession: {CustomCount} custom, {SessionCount} session",
                    customData.Count, sessionData.Count);
                return (customData, sessionData);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading cache with session: {CachePath}", cachePath);
                return null;
            }
            finally
            {
                _fileLock.Release();
            }
        }

        /// <summary>
        /// Загрузить метаданные кеша без загрузки данных модулей
        /// </summary>
        public ModuleCacheMetadata? LoadCacheMetadata(string projectPath)
        {
            var cachePath = GetCachePath(projectPath);
            if (!File.Exists(cachePath) && !File.Exists(GetBackupPath(projectPath))) return null;

            if (!_fileLock.Wait(TimeSpan.FromSeconds(3)))
            {
                // Таймаут означает что UI-поток заблокирован ожиданием этого же лока
                // (классический дедлок при откате вкладки или закрытии приложения).
                // Возвращаем null — вызывающий код использует project.ModulesData как fallback.
                _logger.LogWarning("Cache lock timeout — returning null, caller uses in-memory fallback");
                return null;
            }
            try
            {
                if (!EnsureCacheReadable(cachePath, restoreMissingFromBackup: true)) return null;
                return LoadMetadata(cachePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading cache metadata");
                return null;
            }
            finally
            {
                _fileLock.Release();
            }
        }

        /// <summary>
        /// Сохранить кеш проекта.
        /// Использует ZipArchiveMode.Update — обновляет только записи переданных модулей,
        /// записи остальных модулей остаются нетронутыми.
        /// Файл кеша удаляется только через DeleteCache() (при Ctrl+S или закрытии проекта).
        /// </summary>
        /// <param name="projectPath">Путь к .writersword файлу</param>
        /// <param name="projectId">ID проекта — записывается в метадату для верификации при загрузке</param>
        /// <param name="customDataDict">Данные модулей: moduleType → data</param>
        /// <param name="sessionDataDict">Сессионные данные модулей: moduleType → data</param>
        public async Task SaveCacheAsync(
            string projectPath,
            string projectId,
            Dictionary<string, object?> customDataDict,
            Dictionary<string, object?> sessionDataDict)
        {
            var cachePath = GetCachePath(projectPath);

            await _fileLock.WaitAsync().ConfigureAwait(false);
            try
            {
                _logger.LogDebug("Saving cache (update): {ModulesCount} modules", customDataDict.Count);

                // Подготавливаем данные модулей которые нужно записать.
                var modulesToSave = new Dictionary<string, (string CustomDataJson, string? SessionDataJson)>();

                foreach (var kvp in customDataDict)
                {
                    var moduleType = kvp.Key;
                    var customData = kvp.Value;

                    if (customData == null || (customData is string str && string.IsNullOrWhiteSpace(str)))
                    {
                        _logger.LogDebug("Skipping module without data: {moduleType}", moduleType);
                        continue;
                    }

                    // Сериализуем снапшот — живая коллекция может меняться из UI-треда
                    // пока фоновый поток сериализует, что даёт InvalidOperationException.
                    object? customDataToSerialize = customData is IDictionary<string, object?> cd
                        ? new Dictionary<string, object?>(cd)
                        : customData;
                    var customDataJson = JsonConvert.SerializeObject(customDataToSerialize, Formatting.Indented);
                    string? sessionDataJson = null;
                    if (sessionDataDict.TryGetValue(moduleType, out var sessionData) && sessionData != null)
                    {
                        object? sessionDataToSerialize = sessionData is IDictionary<string, object?> sd
                            ? new Dictionary<string, object?>(sd)
                            : sessionData;
                        sessionDataJson = JsonConvert.SerializeObject(sessionDataToSerialize, Formatting.Indented);
                    }

                    modulesToSave[moduleType] = (customDataJson, sessionDataJson);
                }

                if (modulesToSave.Count == 0)
                {
                    _logger.LogDebug("Nothing to save");
                    return;
                }

                bool fileExists = EnsureCacheReadable(cachePath);

                // Если файл принадлежит другому проекту — пишем архив заново.
                if (fileExists)
                {
                    var existingMeta = LoadMetadata(cachePath);
                    if (existingMeta != null && existingMeta.ProjectId != projectId)
                    {
                        _logger.LogWarning(
                            "Cache belongs to different project ({ExistingId}), recreating for {NewId}",
                            existingMeta.ProjectId, projectId);
                        fileExists = false;
                    }
                }

                // Запись идёт во временный файл и только потом подменяет основной.
                // ZipArchiveMode.Update перезаписывает архив на месте: аварийное
                // выключение посреди этой операции оставляло обрывок вместо точки
                // восстановления, и приложение при следующем старте молча открывало
                // сохранённую версию. Временный файл + File.Replace делают запись
                // неделимой, а прежнее содержимое уезжает в .bak.
                var tempPath = cachePath + ".tmp";
                var backupPath = GetBackupPath(projectPath);

                try { if (File.Exists(tempPath)) File.Delete(tempPath); }
                catch (IOException ex) { _logger.LogDebug(ex, "Stale temp cache file left in place"); }

                // Записи модулей, которых нет в этом проходе, должны уцелеть —
                // поэтому обновляем копию текущего архива, а не пустой файл.
                if (fileExists) File.Copy(cachePath, tempPath, overwrite: true);

                var archiveMode = fileExists ? ZipArchiveMode.Update : ZipArchiveMode.Create;

                using (var archive = ZipFile.Open(tempPath, archiveMode))
                {
                    // Обновляем метаданные: читаем существующие, дополняем новыми.
                    ModuleCacheMetadata metadata;
                    if (fileExists)
                    {
                        metadata = LoadMetadataFromArchive(archive) ?? new ModuleCacheMetadata
                        {
                            ProjectId = projectId,
                            ProjectPath = projectPath,
                            Version = 1,
                            Modules = new Dictionary<string, ModuleHashMetadata>()
                        };
                    }
                    else
                    {
                        metadata = new ModuleCacheMetadata
                        {
                            ProjectId = projectId,
                            ProjectPath = projectPath,
                            Version = 1,
                            Modules = new Dictionary<string, ModuleHashMetadata>()
                        };
                    }

                    metadata.CacheDate = DateTime.Now;

                    foreach (var kvp in modulesToSave)
                    {
                        var moduleType = kvp.Key;
                        var (customDataJson, sessionDataJson) = kvp.Value;

                        var currentHash = _hashService.ComputeHash(customDataJson);
                        metadata.Modules[moduleType] = new ModuleHashMetadata
                        {
                            Hash = currentHash,
                            LastModified = DateTime.Now,
                            Size = Encoding.UTF8.GetByteCount(customDataJson)
                        };

                        // GetEntry/Delete работает только в Update режиме.
                        if (fileExists)
                        {
                            archive.GetEntry($"modules/{moduleType}/customdata.json")?.Delete();
                            archive.GetEntry($"modules/{moduleType}/sessiondata.json")?.Delete();
                        }

                        var newCustomEntry = archive.CreateEntry(
                            $"modules/{moduleType}/customdata.json", CompressionLevel.Optimal);
                        using (var stream = newCustomEntry.Open())
                        using (var writer = new StreamWriter(stream))
                            await writer.WriteAsync(customDataJson);

                        if (sessionDataJson != null)
                        {
                            var newSessionEntry = archive.CreateEntry(
                                $"modules/{moduleType}/sessiondata.json", CompressionLevel.Optimal);
                            using (var stream = newSessionEntry.Open())
                            using (var writer = new StreamWriter(stream))
                                await writer.WriteAsync(sessionDataJson);
                        }

                        _logger.LogDebug("Updated cache entry for: {moduleType}", moduleType);
                    }

                    // Хеш файла проекта — для быстрого сравнения при открытии.
                    // Позволяет пропустить загрузку 400 МБ данных до показа диалога восстановления.
                    // Сам файл проекта в этот момент может быть кратковременно занят другой
                    // операцией сохранения (ZipFileStorageService держит его открытым в
                    // RELEASE-режиме) — несколько попыток с паузой снимают эту гонку без
                    // изменения результата при успехе.
                    const int hashReadAttempts = 3;
                    for (int attempt = 1; attempt <= hashReadAttempts; attempt++)
                    {
                        try
                        {
                            // Глобальный шлюз файла проекта: хеширование не пересекается
                            // с записью через ZipFileStorageService и сохранением проекта.
                            // FileShare.ReadWrite — чтобы не блокировать удерживаемый
                            // в RELEASE-режиме дескриптор хранилища.
                            using var fileGate = ProjectFileLock.Acquire(projectPath);
                            using var sha = SHA256.Create();
                            using var fs = new FileStream(
                                projectPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            var hashBytes = sha.ComputeHash(fs);
                            metadata.ProjectFileHash = Convert.ToHexString(hashBytes);
                            break;
                        }
                        catch (IOException ex) when (attempt < hashReadAttempts)
                        {
                            _logger.LogDebug(ex, "Project file busy, retrying hash computation ({Attempt}/{Total})", attempt, hashReadAttempts);
                            await Task.Delay(100 * attempt).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to compute project file hash");
                            metadata.ProjectFileHash = "";
                            break;
                        }
                    }

                    // Перезаписываем метаданные.
                    if (fileExists)
                        archive.GetEntry("cache.json")?.Delete();

                    var newMetaEntry = archive.CreateEntry("cache.json", CompressionLevel.Optimal);
                    using (var stream = newMetaEntry.Open())
                    using (var writer = new StreamWriter(stream))
                        await writer.WriteAsync(JsonConvert.SerializeObject(metadata, Formatting.Indented));
                }

                // Сброс на физический диск: без него запись остаётся в кеше файловой
                // системы, и аппаратный ресет теряет её целиком.
                using (var flushStream = new FileStream(
                    tempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    flushStream.Flush(flushToDisk: true);
                }

                // Подмена одним шагом: прежний файл уходит в .bak и остаётся
                // рабочей точкой восстановления, если следующая запись не доживёт.
                if (File.Exists(cachePath))
                    File.Replace(tempPath, cachePath, backupPath, ignoreMetadataErrors: true);
                else
                    File.Move(tempPath, cachePath);

                _logger.LogDebug("Cache updated: {ModulesCount} modules written", modulesToSave.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving cache");
            }
            finally
            {
                _fileLock.Release();
            }
        }

        public object? GetModuleCustomData(string projectPath, string moduleType)
        {
            var cache = LoadCache(projectPath);
            if (cache == null) return null;

            cache.TryGetValue(moduleType, out var data);
            return data;
        }

        public void DeleteCache(string projectPath)
        {
            var cachePath = GetCachePath(projectPath);
            var backupPath = GetBackupPath(projectPath);
            var tempPath = cachePath + ".tmp";

            if (!File.Exists(cachePath) && !File.Exists(backupPath) && !File.Exists(tempPath)) return;

            if (!_fileLock.Wait(TimeSpan.FromSeconds(3)))
            {
                _logger.LogWarning("Cache lock timeout in DeleteCache — skipping delete");
                return;
            }
            try
            {
                // Удаляются все следы точки восстановления: оставшаяся резервная
                // копия воскресила бы уже принятую или отклонённую версию.
                foreach (var path in new[] { cachePath, backupPath, tempPath })
                {
                    if (!File.Exists(path)) continue;
                    File.Delete(path);
                    _logger.LogDebug("Cache deleted: {CachePath}", path);
                }
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Error deleting cache: {CachePath}", cachePath);
            }
            finally
            {
                _fileLock.Release();
            }
        }

        public void MoveCacheToBackup(string projectPath)
        {
            var cachePath = GetCachePath(projectPath);
            if (!File.Exists(cachePath)) return;

            if (!_fileLock.Wait(TimeSpan.FromSeconds(3)))
            {
                _logger.LogWarning("Cache lock timeout in MoveCacheToBackup — skipping");
                return;
            }
            try
            {
                var backupPath = GetBackupPath(projectPath);
                File.Move(cachePath, backupPath, overwrite: true);
                _logger.LogDebug("Cache moved to backup: {BackupPath}", backupPath);
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Error moving cache to backup: {CachePath}", cachePath);
            }
            finally
            {
                _fileLock.Release();
            }
        }

        public Dictionary<string, object?>? ReadProjectDataWithoutLock(string projectPath)
        {
            // «WithoutLock» относится к внутреннему замку КЕША. Замок файла
            // проекта здесь больше не нужен: база сама разводит читателей и
            // писателя, и застать её посреди записи невозможно.
            try
            {
                using var storage = new SqliteFileStorageService(projectPath, Serilog.Log.Logger);

                if (storage.ReadFile("project.json") is null)
                {
                    _logger.LogWarning("project.json not found in: {ProjectPath}", projectPath);
                    return null;
                }

                // Данные модулей лежат отдельными записями, а не внутри
                // project.json, как было в архиве. Прочитать один project.json
                // и взять оттуда ModulesData теперь недостаточно — он их не
                // содержит.
                var result = new Dictionary<string, object?>();

                foreach (var entry in storage.EnumerateEntries())
                {
                    var path = entry.Path;

                    if (!path.StartsWith("modules/", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!path.EndsWith("/CustomData.json", StringComparison.OrdinalIgnoreCase)) continue;

                    var parts = path.Split('/');
                    if (parts.Length < 3) continue;

                    var data = storage.ReadFile(path);
                    if (data is null) continue;

                    result[parts[1]] = System.Text.Encoding.UTF8.GetString(data);
                }

                _logger.LogDebug("Read project data without lock: {ModulesCount} modules", result.Count);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading project data without lock");
                return null;
            }
        }

        private ModuleCacheMetadata? LoadMetadata(string cachePath)
        {
            try
            {
                using (var archive = ZipFile.OpenRead(cachePath))
                {
                    return LoadMetadataFromArchive(archive);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading metadata from: {CachePath}", cachePath);
                return null;
            }
        }

        private ModuleCacheMetadata? LoadMetadataFromArchive(ZipArchive archive)
        {
            try
            {
                var metadataEntry = archive.GetEntry("cache.json");
                if (metadataEntry == null)
                {
                    _logger.LogWarning("cache.json not found in archive");
                    return null;
                }

                using (var stream = metadataEntry.Open())
                using (var reader = new StreamReader(stream))
                {
                    var json = reader.ReadToEnd();
                    return JsonConvert.DeserializeObject<ModuleCacheMetadata>(json);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing cache metadata");
                return null;
            }
        }
    }
}