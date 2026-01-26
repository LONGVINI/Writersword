using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Writersword.Core.Models.Project;
using Writersword.Src.Shared.Helpers;

namespace Writersword.Src.Infrastructure.Services.Storage
{
    /// <summary>
    /// Сервис для работы с проектами в формате ZIP
    /// НЕ использует временные папки - работает с ZIP напрямую
    /// Отвечает ТОЛЬКО за project.json и modules/*.json
    /// workspace.json управляется через IWorkspaceConfigService
    /// </summary>
    public class ZipProjectService
    {
        /// <summary>
        /// Сохранить проект в ZIP архив
        /// Создаёт новый ZIP или обновляет существующий
        /// </summary>
        public async Task<bool> SaveToZipAsync(ProjectFile project, string filePath)
        {
            try
            {
                Console.WriteLine($"[ZipProjectService] Saving to ZIP: {filePath}");

                // Создаём директорию если не существует
                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Если файл существует - удаляем (создадим новый)
                // TODO: В будущем можно оптимизировать - открывать в режиме Update
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }

                // Создаём новый ZIP
                using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create))
                {
                    // 1. Сохраняем project.json (метаданные проекта)
                    var projectMeta = new
                    {
                        project.Title,
                        project.Type,
                        project.FormatVersion,
                        project.Id,
                        project.CreatedAt,
                        project.LastModified
                    };

                    var projectJson = JsonHelper.Serialize(projectMeta);
                    var projectEntry = archive.CreateEntry("project.json", CompressionLevel.Optimal);
                    using (var writer = new StreamWriter(projectEntry.Open()))
                    {
                        await writer.WriteAsync(projectJson);
                    }

                    Console.WriteLine($"[ZipProjectService] Saved project.json");

                    // 2. Сохраняем данные модулей в modules/*.json
                    foreach (var moduleEntry in project.ModulesData)
                    {
                        var moduleId = moduleEntry.Key;
                        var moduleData = moduleEntry.Value;

                        if (moduleData != null)
                        {
                            var moduleFile = new
                            {
                                ModuleId = moduleId,
                                Version = "1.0",
                                Data = moduleData
                            };

                            var moduleJson = JsonHelper.Serialize(moduleFile);
                            var zipEntry = archive.CreateEntry($"modules/{moduleId}.json", CompressionLevel.Optimal);

                            using (var writer = new StreamWriter(zipEntry.Open()))
                            {
                                await writer.WriteAsync(moduleJson);
                            }

                            Console.WriteLine($"[ZipProjectService] Saved module: {moduleId}");
                        }
                    }

                    // ВАЖНО: 
                    // - workspace.json сохраняется через IWorkspaceConfigService (отдельно)
                    // - Все файлы которые модули записали через Context.WriteFile() уже находятся в ZIP благодаря ZipFileStorage
                }

                var fileSize = new FileInfo(filePath).Length / 1024;
                Console.WriteLine($"[ZipProjectService] ZIP saved successfully: {filePath} ({fileSize} KB)");

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ZipProjectService] Save error: {ex.Message}");
                Console.WriteLine($"[ZipProjectService] Stack trace: {ex.StackTrace}");
                return false;
            }
        }

        /// <summary>
        /// Загрузить проект из ZIP архива
        /// Читает метаданные и данные модулей напрямую из ZIP
        /// </summary>
        public async Task<ProjectFile?> LoadFromZipAsync(string filePath)
        {
            try
            {
                Console.WriteLine($"[ZipProjectService] Loading from ZIP: {filePath}");

                if (!File.Exists(filePath))
                {
                    Console.WriteLine($"[ZipProjectService] File not found: {filePath}");
                    return null;
                }

                using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Read))
                {
                    // 1. Загружаем project.json
                    var projectEntry = archive.GetEntry("project.json");
                    if (projectEntry == null)
                    {
                        Console.WriteLine($"[ZipProjectService] project.json not found in ZIP");
                        return null;
                    }

                    string projectJson;
                    using (var reader = new StreamReader(projectEntry.Open()))
                    {
                        projectJson = await reader.ReadToEndAsync();
                    }

                    var project = JsonConvert.DeserializeObject<ProjectFile>(projectJson);
                    if (project == null)
                    {
                        Console.WriteLine($"[ZipProjectService] Failed to deserialize project.json");
                        return null;
                    }

                    Console.WriteLine($"[ZipProjectService] Loaded project.json: {project.Title}");

                    // 2. Загружаем данные модулей из modules/*.json
                    foreach (var entry in archive.Entries)
                    {
                        if (entry.FullName.StartsWith("modules/") && entry.FullName.EndsWith(".json"))
                        {
                            string moduleJson;
                            using (var reader = new StreamReader(entry.Open()))
                            {
                                moduleJson = await reader.ReadToEndAsync();
                            }

                            var moduleObject = JsonConvert.DeserializeObject<Dictionary<string, object>>(moduleJson);

                            if (moduleObject != null && moduleObject.TryGetValue("ModuleId", out var moduleIdObj))
                            {
                                var moduleId = moduleIdObj?.ToString();

                                if (!string.IsNullOrEmpty(moduleId) && moduleObject.TryGetValue("Data", out var data))
                                {
                                    project.ModulesData[moduleId] = data;
                                    Console.WriteLine($"[ZipProjectService] Loaded module: {moduleId}");
                                }
                            }
                        }
                    }

                    Console.WriteLine($"[ZipProjectService] Project loaded successfully");
                    Console.WriteLine($"[ZipProjectService] Modules: {project.ModulesData.Count}");

                    // ВАЖНО: 
                    // - workspace.json загружается через IWorkspaceConfigService (отдельно)
                    // - Файлы которые модули сохранили через Context.WriteFile() останутся в ZIP и будут доступны через ZipFileStorage

                    return project;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ZipProjectService] Load error: {ex.Message}");
                Console.WriteLine($"[ZipProjectService] Stack trace: {ex.StackTrace}");
                return null;
            }
        }
    }
}