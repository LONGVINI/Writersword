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
using Writersword.Core.Models.Modules;
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
        private readonly IHashService _hashService;

        public ZipCacheService(IHashService hashService)
        {
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
                Console.WriteLine($"[ZipCacheService] Error reading cache date: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Загрузить весь кеш проекта
        /// Возвращает словарь ModuleType → ModuleState
        /// </summary>
        public Dictionary<string, ModuleState>? LoadCache(string projectPath)
        {
            var cachePath = GetCachePath(projectPath);

            if (!File.Exists(cachePath))
            {
                Console.WriteLine($"[ZipCacheService] Cache not found: {cachePath}");
                return null;
            }

            try
            {
                var states = new Dictionary<string, ModuleState>();

                using (var archive = ZipFile.OpenRead(cachePath))
                {
                    // Загружаем метаданные
                    var metadata = LoadMetadataFromArchive(archive);
                    if (metadata == null)
                    {
                        Console.WriteLine($"[ZipCacheService] Failed to load metadata");
                        return null;
                    }

                    Console.WriteLine($"[ZipCacheService] ===== CACHE LOADED =====");
                    Console.WriteLine($"[ZipCacheService] Project path: {projectPath}");
                    Console.WriteLine($"[ZipCacheService] Project ID: {metadata.ProjectId}");
                    Console.WriteLine($"[ZipCacheService] Cache date: {metadata.CacheDate}");
                    Console.WriteLine($"[ZipCacheService] Modules: {metadata.Modules.Count}");

                    // Загружаем состояния модулей
                    foreach (var moduleId in metadata.Modules.Keys)
                    {
                        var state = new ModuleState();

                        // metadata.json
                        var metadataEntry = archive.GetEntry($"modules/{moduleId}/metadata.json");
                        if (metadataEntry != null)
                        {
                            using (var stream = metadataEntry.Open())
                            using (var reader = new StreamReader(stream))
                            {
                                var json = reader.ReadToEnd();
                                var meta = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
                                if (meta != null && meta.TryGetValue("InstanceId", out var id))
                                {
                                    state.InstanceId = id?.ToString() ?? "";
                                }
                            }
                        }

                        // customdata.json
                        var customDataEntry = archive.GetEntry($"modules/{moduleId}/customdata.json");
                        if (customDataEntry != null)
                        {
                            using (var stream = customDataEntry.Open())
                            using (var reader = new StreamReader(stream))
                            {
                                var json = reader.ReadToEnd();
                                state.CustomData = JsonConvert.DeserializeObject<object>(json);
                            }
                        }

                        // sessiondata.json
                        var sessionDataEntry = archive.GetEntry($"modules/{moduleId}/sessiondata.json");
                        if (sessionDataEntry != null)
                        {
                            using (var stream = sessionDataEntry.Open())
                            using (var reader = new StreamReader(stream))
                            {
                                var json = reader.ReadToEnd();
                                state.SessionData = JsonConvert.DeserializeObject<object>(json);
                            }
                        }

                        states[moduleId] = state;
                        Console.WriteLine($"[ZipCacheService]   - {moduleId}: loaded (Instance: {state.InstanceId})");
                    }

                    Console.WriteLine($"[ZipCacheService] =======================");
                }

                return states;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ZipCacheService] Error loading cache: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Сохранить весь кеш проекта
        /// Использует хеширование для проверки изменений
        /// </summary>
        public async Task SaveCacheAsync(string projectPath, string projectId, Dictionary<string, ModuleState> moduleStates)
        {
            var cachePath = GetCachePath(projectPath);

            try
            {
                // Загружаем старые метаданные (если есть)
                ModuleCacheMetadata? oldMetadata = null;
                if (File.Exists(cachePath))
                {
                    oldMetadata = LoadMetadata(cachePath);
                }

                // Создаём новые метаданные
                var newMetadata = new ModuleCacheMetadata
                {
                    ProjectId = projectId,
                    ProjectPath = projectPath,
                    CacheDate = DateTime.Now,
                    Version = 1,
                    Modules = new Dictionary<string, ModuleHashMetadata>()
                };

                // Список модулей для сохранения
                var modulesToSave = new Dictionary<string, ModuleState>();

                // Проверяем каждый модуль
                foreach (var kvp in moduleStates)
                {
                    var moduleId = kvp.Key;
                    var state = kvp.Value;

                    // ПРОПУСКАЕМ модули без данных
                    if (state.CustomData == null ||
                        (state.CustomData is string str && string.IsNullOrWhiteSpace(str)))
                    {
                        Console.WriteLine($"[ZipCacheService] Skipping module without data: {moduleId}");
                        continue;
                    }

                    // Вычисляем хеш CustomData
                    var currentHash = _hashService.ComputeHash(state.CustomData);

                    // Проверяем изменился ли модуль (для логирования)
                    if (oldMetadata?.Modules.TryGetValue(moduleId, out var oldMeta) == true)
                    {
                        if (oldMeta.Hash == currentHash)
                        {
                            Console.WriteLine($"[ZipCacheService] Module unchanged: {moduleId}");
                        }
                        else
                        {
                            Console.WriteLine($"[ZipCacheService] Module changed: {moduleId}");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"[ZipCacheService] Module new: {moduleId}");
                    }

                    // Сохраняем метаданные
                    var stateJson = JsonConvert.SerializeObject(state);
                    var stateSize = Encoding.UTF8.GetByteCount(stateJson);

                    newMetadata.Modules[moduleId] = new ModuleHashMetadata
                    {
                        Hash = currentHash,
                        LastModified = DateTime.Now,
                        Size = stateSize
                    };

                    // Добавляем в список для сохранения (всегда обновляем весь архив)
                    modulesToSave[moduleId] = state;
                }

                // Создаём новый ZIP архив
                if (File.Exists(cachePath))
                {
                    File.Delete(cachePath);
                }

                using (var archive = ZipFile.Open(cachePath, ZipArchiveMode.Create))
                {
                    // Сохраняем метаданные
                    var metadataEntry = archive.CreateEntry("cache.json", CompressionLevel.Optimal);
                    using (var stream = metadataEntry.Open())
                    using (var writer = new StreamWriter(stream))
                    {
                        var metadataJson = JsonConvert.SerializeObject(newMetadata, Formatting.Indented);
                        await writer.WriteAsync(metadataJson);
                    }

                    // Сохраняем состояния модулей
                    foreach (var kvp in modulesToSave)
                    {
                        var moduleId = kvp.Key;
                        var state = kvp.Value;

                        // metadata.json
                        var metadata = new
                        {
                            InstanceId = state.InstanceId,
                            ModuleId = moduleId,
                            Version = "1.0"
                        };
                        var metadataJson = JsonConvert.SerializeObject(metadata, Formatting.Indented);
                        var moduleMetadataEntry = archive.CreateEntry($"modules/{moduleId}/metadata.json", CompressionLevel.Optimal);
                        using (var stream = moduleMetadataEntry.Open())
                        using (var writer = new StreamWriter(stream))
                        {
                            await writer.WriteAsync(metadataJson);
                        }

                        // customdata.json
                        if (state.CustomData != null)
                        {
                            var customDataJson = JsonConvert.SerializeObject(state.CustomData, Formatting.Indented);
                            var customDataEntry = archive.CreateEntry($"modules/{moduleId}/customdata.json", CompressionLevel.Optimal);
                            using (var stream = customDataEntry.Open())
                            using (var writer = new StreamWriter(stream))
                            {
                                await writer.WriteAsync(customDataJson);
                            }
                        }

                        // sessiondata.json
                        if (state.SessionData != null)
                        {
                            var sessionDataJson = JsonConvert.SerializeObject(state.SessionData, Formatting.Indented);
                            var sessionDataEntry = archive.CreateEntry($"modules/{moduleId}/sessiondata.json", CompressionLevel.Optimal);
                            using (var stream = sessionDataEntry.Open())
                            using (var writer = new StreamWriter(stream))
                            {
                                await writer.WriteAsync(sessionDataJson);
                            }
                        }
                    }
                }

                Console.WriteLine($"[ZipCacheService] ===== CACHE SAVED =====");
                Console.WriteLine($"[ZipCacheService] Project path: {projectPath}");
                Console.WriteLine($"[ZipCacheService] Cache file: {cachePath}");
                Console.WriteLine($"[ZipCacheService] Modules saved: {modulesToSave.Count}");
                Console.WriteLine($"[ZipCacheService] ======================");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ZipCacheService] Error saving cache: {ex.Message}");
            }
        }

        /// <summary>
        /// Получить состояние конкретного модуля из кеша
        /// </summary>
        public ModuleState? GetModuleState(string projectPath, string moduleType)
        {
            var cache = LoadCache(projectPath);

            if (cache == null)
                return null;

            cache.TryGetValue(moduleType, out var state);
            return state;
        }

        /// <summary>
        /// Сохранить состояние конкретного модуля в кеш
        /// </summary>
        public async Task SaveModuleStateAsync(string projectPath, string projectId, string moduleType, ModuleState state)
        {
            // Загружаем существующий кеш или создаём новый
            var cache = LoadCache(projectPath) ?? new Dictionary<string, ModuleState>();

            // Обновляем/добавляем состояние модуля
            cache[moduleType] = state;

            // Сохраняем весь кеш
            await SaveCacheAsync(projectPath, projectId, cache);

            Console.WriteLine($"[ZipCacheService] Module state saved: {moduleType}");
        }

        /// <summary>Удалить кеш проекта</summary>
        public void DeleteCache(string projectPath)
        {
            var cachePath = GetCachePath(projectPath);

            if (File.Exists(cachePath))
            {
                File.Delete(cachePath);
                Console.WriteLine($"[ZipCacheService] Cache deleted: {cachePath}");
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
                Console.WriteLine($"[ZipCacheService] Error loading metadata: {ex.Message}");
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
                    Console.WriteLine($"[ZipCacheService] cache.json not found in archive");
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
                Console.WriteLine($"[ZipCacheService] Error parsing metadata: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Прочитать данные проекта из ZIP БЕЗ блокировки файла
        /// Используется для сравнения данных в CacheUpdateService
        /// Открывает ZIP в режиме Read с FileShare.ReadWrite - позволяет другим процессам читать И записывать
        /// </summary>
        /// <param name="projectPath">Путь к файлу проекта (.writersword)</param>
        /// <returns>ModulesData из проекта или null если ошибка</returns>
        public Dictionary<string, object?>? ReadProjectDataWithoutLock(string projectPath)
        {
            try
            {
                // Открываем ZIP в режиме Read с FileShare.ReadWrite - позволяет другим процессам работать с файлом!
                using (var stream = new FileStream(projectPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    var entry = archive.GetEntry("project.json");
                    if (entry == null)
                    {
                        Console.WriteLine($"[ZipCacheService] project.json not found in: {projectPath}");
                        return null;
                    }

                    using (var entryStream = entry.Open())
                    using (var reader = new StreamReader(entryStream))
                    {
                        var json = reader.ReadToEnd();
                        var project = JsonConvert.DeserializeObject<ProjectFile>(json);

                        Console.WriteLine($"[ZipCacheService] Read project data without lock: {project?.ModulesData.Count ?? 0} modules");
                        return project?.ModulesData;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ZipCacheService] Error reading project data without lock: {ex.Message}");
                return null;
            }
        }
    }
}