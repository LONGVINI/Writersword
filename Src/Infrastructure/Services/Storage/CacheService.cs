//using System;
//using System.Collections.Generic;
//using System.IO;
//using System.Threading.Tasks;
//using Newtonsoft.Json;
//using Writersword.Core.Interfaces.Services;
//using Writersword.Core.Models.Cache;
//using Writersword.Core.Models.Modules;

//namespace Writersword.Services
//{
//    /// <summary>
//    /// Сервис для работы с кешем проекта (.wsasd файлы)
//    /// Кеш хранит состояния модулей для быстрого восстановления сессии
//    /// </summary>
//    public class CacheService : IZipCacheService
//    {
//        /// <summary>Получить путь к файлу кеша</summary>
//        /// <summary>
//        /// Получить путь к файлу кеша
//        /// Всегда использует абсолютный путь для защиты от коллизий
//        /// </summary>
//        private string GetCachePath(string projectPath)
//        {
//            var fullPath = Path.GetFullPath(projectPath);
//            var cachePath = fullPath + ".wsasd";
//            Console.WriteLine($"[CacheService] GetCachePath: {projectPath} → {cachePath}");
//            return cachePath;
//        }

//        /// <summary>Проверить существует ли кеш для проекта</summary>
//        public bool HasCache(string projectPath)
//        {
//            var cachePath = GetCachePath(projectPath);
//            return File.Exists(cachePath);
//        }

//        /// <summary>Получить дату создания кеша</summary>
//        public DateTime? GetCacheDate(string projectPath)
//        {
//            var cachePath = GetCachePath(projectPath);

//            if (!File.Exists(cachePath))
//                return null;

//            try
//            {
//                var json = File.ReadAllText(cachePath);
//                var cache = JsonConvert.DeserializeObject<ModuleCache>(json);
//                return cache?.CacheDate;
//            }
//            catch (Exception ex)
//            {
//                Console.WriteLine($"[CacheService] Error reading cache date: {ex.Message}");
//                return null;
//            }
//        }

//        /// <summary>
//        /// Загрузить весь кеш проекта
//        /// Возвращает словарь ModuleType → ModuleState
//        /// </summary>
//        public Dictionary<string, ModuleState>? LoadCache(string projectPath)
//        {
//            var cachePath = GetCachePath(projectPath);

//            if (!File.Exists(cachePath))
//            {
//                Console.WriteLine($"[CacheService] Cache not found: {cachePath}");
//                return null;
//            }

//            try
//            {
//                var json = File.ReadAllText(cachePath);
//                var cache = JsonConvert.DeserializeObject<ModuleCache>(json);

//                Console.WriteLine($"[CacheService] ===== CACHE LOADED =====");
//                Console.WriteLine($"[CacheService] Project path: {projectPath}");
//                Console.WriteLine($"[CacheService] Cache file: {cachePath}");
//                Console.WriteLine($"[CacheService] Cache date: {cache?.CacheDate}");
//                Console.WriteLine($"[CacheService] Modules: {cache?.Modules.Count ?? 0}");

//                if (cache?.Modules != null)
//                {
//                    foreach (var kvp in cache.Modules)
//                    {
//                        var customDataPreview = kvp.Value.CustomData?.ToString()?.Substring(0, Math.Min(50, kvp.Value.CustomData?.ToString()?.Length ?? 0));
//                        Console.WriteLine($"[CacheService]   - {kvp.Key}: {customDataPreview}...");
//                    }
//                }

//                Console.WriteLine($"[CacheService] =======================");

//                return cache?.Modules;
//            }
//            catch (Exception ex)
//            {
//                Console.WriteLine($"[CacheService] Error loading cache: {ex.Message}");
//                return null;
//            }
//        }

//        /// <summary>
//        /// Сохранить весь кеш проекта
//        /// Принимает словарь ModuleType → ModuleState
//        /// </summary>
//        public async Task SaveCacheAsync(string projectPath, Dictionary<string, ModuleState> moduleStates)
//        {
//            var cachePath = GetCachePath(projectPath);

//            try
//            {
//                var cache = new ModuleCache
//                {
//                    Modules = moduleStates,
//                    CacheDate = DateTime.Now
//                };

//                var json = JsonConvert.SerializeObject(cache, Formatting.Indented);
//                await File.WriteAllTextAsync(cachePath, json);

//                Console.WriteLine($"[CacheService] ===== CACHE SAVED =====");
//                Console.WriteLine($"[CacheService] Project path: {projectPath}");
//                Console.WriteLine($"[CacheService] Cache file: {cachePath}");
//                Console.WriteLine($"[CacheService] Modules saved: {moduleStates.Count}");

//                foreach (var kvp in moduleStates)
//                {
//                    var customDataPreview = kvp.Value.CustomData?.ToString()?.Substring(0, Math.Min(50, kvp.Value.CustomData?.ToString()?.Length ?? 0));
//                    Console.WriteLine($"[CacheService]   - {kvp.Key}: {customDataPreview}...");
//                }

//                Console.WriteLine($"[CacheService] ======================");
//            }
//            catch (Exception ex)
//            {
//                Console.WriteLine($"[CacheService] Error saving cache: {ex.Message}");
//            }
//        }

//        /// <summary>
//        /// Получить состояние конкретного модуля из кеша
//        /// Возвращает null если кеша нет или модуль не найден
//        /// </summary>
//        public ModuleState? GetModuleState(string projectPath, string moduleType)
//        {
//            var cache = LoadCache(projectPath);

//            if (cache == null)
//                return null;

//            cache.TryGetValue(moduleType, out var state);
//            return state;
//        }

//        /// <summary>
//        /// Сохранить состояние конкретного модуля в кеш
//        /// Обновляет существующий кеш или создаёт новый
//        /// </summary>
//        public async Task SaveModuleStateAsync(string projectPath, string moduleType, ModuleState state)
//        {
//            // Загружаем существующий кеш или создаём новый
//            var cache = LoadCache(projectPath) ?? new Dictionary<string, ModuleState>();

//            // Обновляем/добавляем состояние модуля
//            cache[moduleType] = state;

//            // Сохраняем весь кеш
//            await SaveCacheAsync(projectPath, cache);

//            Console.WriteLine($"[CacheService] Module state saved: {moduleType}");
//        }

//        /// <summary>Удалить кеш проекта</summary>
//        public void DeleteCache(string projectPath)
//        {
//            var cachePath = GetCachePath(projectPath);

//            if (File.Exists(cachePath))
//            {
//                File.Delete(cachePath);
//                Console.WriteLine($"[CacheService] Cache deleted: {cachePath}");
//            }
//        }
//    }
//}