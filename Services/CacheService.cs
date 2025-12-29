using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Writersword.Core.Models.Project;
using Writersword.Services.Interfaces;

namespace Writersword.Services
{
    /// <summary>
    /// Сервис управления файлами автосохранения (.wsasd)
    /// Отвечает за создание, загрузку и удаление кеш-файлов
    /// </summary>
    public class CacheService : ICacheService
    {
        /// <summary>Расширение файла кеша (Writersword AutoSave Data)</summary>
        private const string CACHE_EXTENSION = ".wsasd";

        /// <summary>
        /// Проверить существует ли кеш для проекта
        /// </summary>
        public bool HasCache(string projectPath)
        {
            var cachePath = GetCachePath(projectPath);
            var exists = File.Exists(cachePath);

            if (exists)
            {
                Console.WriteLine($"[CacheService] Cache found: {cachePath}");
            }

            return exists;
        }

        /// <summary>
        /// Сохранить проект в кеш
        /// </summary>
        public async Task SaveToCacheAsync(ProjectFile project, string projectPath)
        {
            try
            {
                var cachePath = GetCachePath(projectPath);
                var json = JsonConvert.SerializeObject(project, Formatting.Indented);

                var directory = Path.GetDirectoryName(cachePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                await File.WriteAllTextAsync(cachePath, json);
                Console.WriteLine($"[CacheService] Cache saved: {cachePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CacheService] ERROR: Failed to save cache: {ex.Message}");
            }
        }

        /// <summary>
        /// Загрузить проект из кеша
        /// </summary>
        public async Task<ProjectFile?> LoadFromCacheAsync(string projectPath)
        {
            try
            {
                var cachePath = GetCachePath(projectPath);

                if (!File.Exists(cachePath))
                {
                    Console.WriteLine($"[CacheService] Cache not found: {cachePath}");
                    return null;
                }

                var json = await File.ReadAllTextAsync(cachePath);
                var project = JsonConvert.DeserializeObject<ProjectFile>(json);

                if (project != null)
                {
                    Console.WriteLine($"[CacheService] Cache loaded: {cachePath}");
                }

                return project;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CacheService] ERROR: Failed to load cache: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Удалить кеш файл
        /// </summary>
        public void DeleteCache(string projectPath)
        {
            try
            {
                var cachePath = GetCachePath(projectPath);

                if (File.Exists(cachePath))
                {
                    File.Delete(cachePath);
                    Console.WriteLine($"[CacheService] Cache deleted: {cachePath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CacheService] ERROR: Failed to delete cache: {ex.Message}");
            }
        }

        /// <summary>
        /// Получить дату создания кеша
        /// </summary>
        public DateTime? GetCacheDate(string projectPath)
        {
            try
            {
                var cachePath = GetCachePath(projectPath);

                if (File.Exists(cachePath))
                {
                    return File.GetLastWriteTime(cachePath);
                }

                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CacheService] ERROR: Failed to get cache date: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Получить дату сохранения основного файла
        /// </summary>
        public DateTime? GetSaveDate(string projectPath)
        {
            try
            {
                if (File.Exists(projectPath))
                {
                    return File.GetLastWriteTime(projectPath);
                }

                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CacheService] ERROR: Failed to get save date: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Получить путь к кеш файлу
        /// </summary>
        private string GetCachePath(string projectPath)
        {
            return projectPath + CACHE_EXTENSION;
        }
    }
}