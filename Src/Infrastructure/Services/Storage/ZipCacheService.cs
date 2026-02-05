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

namespace Writersword.Src.Infrastructure.Services.Storage
{
    /// <summary>
    /// Реализация сервиса для работы с кешем в формате ZIP
    /// Кеш хранится в .writersword.wsasd файле (ZIP архив)
    /// Использует хеширование для оптимизации сохранения
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

        /// <summary>Получить путь к файлу кеша</summary>
        private string GetCachePath(string projectPath)
        {
            var fullPath = Path.GetFullPath(projectPath);
            var cachePath = fullPath + ".wsasd";
            return cachePath;
        }

        /// <summary>Проверить существует ли кеш для проекта</summary>
        public bool HasCache(string projectPath)
        {
            var cachePath = GetCachePath(projectPath);
            return File.Exists(cachePath);
        }

        /// <summary>Получить дату создания кеша</summary>
        public DateTime? GetCacheDate(string projectPath)
        {
            var cachePath = GetCachePath(projectPath);

            if (!File.Exists(cachePath))
                return null;

            try
            {
                var metadata = LoadMetadata(cachePath);
                return metadata?.CacheDate;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading cache date");
                return null;
            }
        }

        /// <summary>
        /// Загрузить CustomData из кеша
        /// Возвращает словарь ModuleId → CustomData
        /// </summary>
        public Dictionary<string, object?>? LoadCache(string projectPath)
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
                        _logger.LogWarning("Failed to load metadata");
                        return null;
                    }

                    _logger.LogDebug("===== CACHE LOADED =====");
                    _logger.LogDebug("Project path: {ProjectPath}", projectPath);
                    _logger.LogDebug("Project ID: {ProjectId}", metadata.ProjectId);
                    _logger.LogDebug("Cache date: {CacheDate}", metadata.CacheDate);
                    _logger.LogDebug("Modules: {ModulesCount}", metadata.Modules.Count);

                    foreach (var moduleId in metadata.Modules.Keys)
                    {
                        var customDataEntry = archive.GetEntry($"modules/{moduleId}/customdata.json");
                        if (customDataEntry != null)
                        {
                            using (var stream = customDataEntry.Open())
                            using (var reader = new StreamReader(stream))
                            {
                                var json = reader.ReadToEnd();
                                var data = JsonConvert.DeserializeObject<object>(json);
                                customData[moduleId] = data;
                                _logger.LogDebug("- {ModuleId}: loaded", moduleId);
                            }
                        }
                    }

                    _logger.LogDebug("=======================");
                }

                return customData;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading cache");
                return null;
            }
        }

        /// <summary>
        /// Сохранить кеш проекта
        /// Принимает ДВА словаря: CustomData и SessionData
        /// Использует хеширование для проверки изменений
        /// </summary>
        public async Task SaveCacheAsync(string projectPath, string projectId, Dictionary<string, object?> customDataDict, Dictionary<string, object?> sessionDataDict)
        {
            var cachePath = GetCachePath(projectPath);

            try
            {
                _logger.LogDebug("===== SAVING CACHE =====");
                _logger.LogDebug("CustomData modules: {CustomDataCount}", customDataDict.Count);
                _logger.LogDebug("SessionData modules: {SessionDataCount}", sessionDataDict.Count);

                ModuleCacheMetadata? oldMetadata = null;
                if (File.Exists(cachePath))
                {
                    oldMetadata = LoadMetadata(cachePath);
                }

                var newMetadata = new ModuleCacheMetadata
                {
                    ProjectId = projectId,
                    ProjectPath = projectPath,
                    CacheDate = DateTime.Now,
                    Version = 1,
                    Modules = new Dictionary<string, ModuleHashMetadata>()
                };

                var modulesToSave = new Dictionary<string, (object? CustomData, object? SessionData)>();

                foreach (var kvp in customDataDict)
                {
                    var moduleId = kvp.Key;
                    var customData = kvp.Value;

                    if (customData == null || (customData is string str && string.IsNullOrWhiteSpace(str)))
                    {
                        _logger.LogDebug("Skipping module without data: {ModuleId}", moduleId);
                        continue;
                    }

                    var currentHash = _hashService.ComputeHash(customData);

                    if (oldMetadata?.Modules.TryGetValue(moduleId, out var oldMeta) == true)
                    {
                        if (oldMeta.Hash == currentHash)
                        {
                            _logger.LogDebug("Module unchanged: {ModuleId}", moduleId);
                        }
                        else
                        {
                            _logger.LogDebug("Module changed: {ModuleId}", moduleId);
                        }
                    }
                    else
                    {
                        _logger.LogDebug("Module new: {ModuleId}", moduleId);
                    }

                    var customDataJson = JsonConvert.SerializeObject(customData);
                    var stateSize = Encoding.UTF8.GetByteCount(customDataJson);

                    newMetadata.Modules[moduleId] = new ModuleHashMetadata
                    {
                        Hash = currentHash,
                        LastModified = DateTime.Now,
                        Size = stateSize
                    };

                    sessionDataDict.TryGetValue(moduleId, out var sessionData);
                    modulesToSave[moduleId] = (customData, sessionData);
                }

                if (File.Exists(cachePath))
                {
                    File.Delete(cachePath);
                }

                using (var archive = ZipFile.Open(cachePath, ZipArchiveMode.Create))
                {
                    var metadataEntry = archive.CreateEntry("cache.json", CompressionLevel.Optimal);
                    using (var stream = metadataEntry.Open())
                    using (var writer = new StreamWriter(stream))
                    {
                        var metadataJson = JsonConvert.SerializeObject(newMetadata, Formatting.Indented);
                        await writer.WriteAsync(metadataJson);
                    }

                    foreach (var kvp in modulesToSave)
                    {
                        var moduleId = kvp.Key;
                        var (customData, sessionData) = kvp.Value;

                        if (customData != null)
                        {
                            var customDataJson = JsonConvert.SerializeObject(customData, Formatting.Indented);
                            var customDataEntry = archive.CreateEntry($"modules/{moduleId}/customdata.json", CompressionLevel.Optimal);
                            using (var stream = customDataEntry.Open())
                            using (var writer = new StreamWriter(stream))
                            {
                                await writer.WriteAsync(customDataJson);
                            }
                        }

                        if (sessionData != null)
                        {
                            var sessionDataJson = JsonConvert.SerializeObject(sessionData, Formatting.Indented);
                            var sessionDataEntry = archive.CreateEntry($"modules/{moduleId}/sessiondata.json", CompressionLevel.Optimal);
                            using (var stream = sessionDataEntry.Open())
                            using (var writer = new StreamWriter(stream))
                            {
                                await writer.WriteAsync(sessionDataJson);
                            }
                        }
                    }
                }

                _logger.LogDebug("===== CACHE SAVED =====");
                _logger.LogDebug("Project path: {ProjectPath}", projectPath);
                _logger.LogDebug("Cache file: {CachePath}", cachePath);
                _logger.LogDebug("Modules saved: {ModulesCount}", modulesToSave.Count);
                _logger.LogDebug("======================");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving cache");
            }
        }

        /// <summary>
        /// Получить CustomData конкретного модуля из кеша
        /// </summary>
        public object? GetModuleCustomData(string projectPath, string moduleId)
        {
            var cache = LoadCache(projectPath);

            if (cache == null)
                return null;

            cache.TryGetValue(moduleId, out var data);
            return data;
        }

        /// <summary>Удалить кеш проекта</summary>
        public void DeleteCache(string projectPath)
        {
            var cachePath = GetCachePath(projectPath);

            if (File.Exists(cachePath))
            {
                File.Delete(cachePath);
                _logger.LogDebug("Cache deleted: {CachePath}", cachePath);
            }
        }

        /// <summary>Загрузить метаданные из файла кеша</summary>
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
                _logger.LogError(ex, "Error loading metadata");
                return null;
            }
        }

        /// <summary>Загрузить метаданные из открытого архива</summary>
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
                    var metadata = JsonConvert.DeserializeObject<ModuleCacheMetadata>(json);
                    return metadata;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing metadata");
                return null;
            }
        }

        /// <summary>
        /// Прочитать данные проекта из ZIP БЕЗ блокировки файла
        /// Используется для сравнения данных в CacheUpdateService
        /// Открывает ZIP в режиме Read с FileShare.ReadWrite - позволяет другим процессам читать И записывать
        /// </summary>
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

                        _logger.LogDebug("Read project data without lock: {ModulesCount} modules", project?.ModulesData.Count ?? 0);
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
    }
}