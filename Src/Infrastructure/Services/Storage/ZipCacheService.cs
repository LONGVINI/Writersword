using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
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

        public bool HasCache(string projectPath)
        {
            return File.Exists(GetCachePath(projectPath));
        }

        public DateTime? GetCacheDate(string projectPath)
        {
            var cachePath = GetCachePath(projectPath);
            if (!File.Exists(cachePath)) return null;

            try
            {
                return LoadMetadata(cachePath)?.CacheDate;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading cache date");
                return null;
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
            if (!File.Exists(cachePath))
            {
                _logger.LogDebug("Cache not found: {CachePath}", cachePath);
                return null;
            }

            try
            {
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
        }

        /// <summary>
        /// Загрузить CustomData И SessionData из кеша одним чтением архива.
        /// </summary>
        public (Dictionary<string, object?> CustomData, Dictionary<string, object?> SessionData)?
            LoadCacheWithSession(string projectPath, string? expectedProjectId = null)
        {
            var cachePath = GetCachePath(projectPath);
            if (!File.Exists(cachePath))
            {
                _logger.LogDebug("Cache not found: {CachePath}", cachePath);
                return null;
            }

            try
            {
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
        }

        /// <summary>
        /// Загрузить метаданные кеша без загрузки данных модулей
        /// </summary>
        public ModuleCacheMetadata? LoadCacheMetadata(string projectPath)
        {
            var cachePath = GetCachePath(projectPath);
            if (!File.Exists(cachePath)) return null;

            try
            {
                return LoadMetadata(cachePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading cache metadata");
                return null;
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

                    var customDataJson = JsonConvert.SerializeObject(customData, Formatting.Indented);
                    string? sessionDataJson = null;
                    if (sessionDataDict.TryGetValue(moduleType, out var sessionData) && sessionData != null)
                        sessionDataJson = JsonConvert.SerializeObject(sessionData, Formatting.Indented);

                    modulesToSave[moduleType] = (customDataJson, sessionDataJson);
                }

                if (modulesToSave.Count == 0)
                {
                    _logger.LogDebug("Nothing to save");
                    return;
                }

                bool fileExists = File.Exists(cachePath);

                // Если файл принадлежит другому проекту — удаляем и создаём заново.
                if (fileExists)
                {
                    var existingMeta = LoadMetadata(cachePath);
                    if (existingMeta != null && existingMeta.ProjectId != projectId)
                    {
                        _logger.LogWarning(
                            "Cache belongs to different project ({ExistingId}), recreating for {NewId}",
                            existingMeta.ProjectId, projectId);
                        File.Delete(cachePath);
                        fileExists = false;
                    }
                }

                var archiveMode = fileExists ? ZipArchiveMode.Update : ZipArchiveMode.Create;

                using (var archive = ZipFile.Open(cachePath, archiveMode))
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

                    // Перезаписываем метаданные.
                    if (fileExists)
                        archive.GetEntry("cache.json")?.Delete();

                    var newMetaEntry = archive.CreateEntry("cache.json", CompressionLevel.Optimal);
                    using (var stream = newMetaEntry.Open())
                    using (var writer = new StreamWriter(stream))
                        await writer.WriteAsync(JsonConvert.SerializeObject(metadata, Formatting.Indented));
                }

                _logger.LogDebug("Cache updated: {ModulesCount} modules written", modulesToSave.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving cache");
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
            if (File.Exists(cachePath))
            {
                File.Delete(cachePath);
                _logger.LogDebug("Cache deleted: {CachePath}", cachePath);
            }
        }

        public Dictionary<string, object?>? ReadProjectDataWithoutLock(string projectPath)
        {
            try
            {
                using (var stream = new FileStream(projectPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    var entry = archive.GetEntry("project.json");
                    if (entry == null)
                    {
                        _logger.LogWarning("project.json not found in: {ProjectPath}", projectPath);
                        return null;
                    }

                    using (var entryStream = entry.Open())
                    using (var reader = new StreamReader(entryStream))
                    {
                        var json = reader.ReadToEnd();
                        var project = JsonConvert.DeserializeObject<ProjectFile>(json);
                        _logger.LogDebug("Read project data without lock: {ModulesCount} modules",
                            project?.ModulesData.Count ?? 0);
                        return project?.ModulesData;
                    }
                }
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