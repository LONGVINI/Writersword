using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
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

                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                bool fileExists = File.Exists(filePath);
                ZipArchiveMode mode = fileExists ? ZipArchiveMode.Update : ZipArchiveMode.Create;
                FileMode fileMode = fileExists ? FileMode.Open : FileMode.Create;

                Console.WriteLine($"[ZipProjectService] Mode: {mode}, FileExists: {fileExists}");

                using (var fileStream = new FileStream(filePath, fileMode, FileAccess.ReadWrite))
                using (var archive = new ZipArchive(fileStream, mode))
                {
                    if (mode == ZipArchiveMode.Update)
                    {
                        var projectEntry = archive.GetEntry("project.json");
                        if (projectEntry != null)
                        {
                            projectEntry.Delete();
                            Console.WriteLine($"[ZipProjectService] Deleted old project.json");
                        }

                        var oldModules = archive.Entries
                            .Where(e => e.FullName.StartsWith("modules/"))
                            .ToList();

                        foreach (var entry in oldModules)
                        {
                            entry.Delete();
                        }

                        if (oldModules.Count > 0)
                        {
                            Console.WriteLine($"[ZipProjectService] Deleted {oldModules.Count} old module entries");
                        }
                    }

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
                    var newProjectEntry = archive.CreateEntry("project.json", CompressionLevel.Optimal);
                    using (var writer = new StreamWriter(newProjectEntry.Open()))
                    {
                        await writer.WriteAsync(projectJson);
                    }

                    Console.WriteLine($"[ZipProjectService] Saved project.json");

                    foreach (var moduleEntry in project.ModulesData)
                    {
                        var moduleId = moduleEntry.Key;
                        var customData = moduleEntry.Value;

                        if (customData == null || (customData is string str && string.IsNullOrWhiteSpace(str)))
                        {
                            Console.WriteLine($"[ZipProjectService] Skipping empty module: {moduleId}");
                            continue;
                        }

                        var customDataJson = JsonHelper.Serialize(customData);
                        var customDataEntry = archive.CreateEntry($"modules/{moduleId}/customdata.json", CompressionLevel.Optimal);
                        using (var writer = new StreamWriter(customDataEntry.Open()))
                        {
                            await writer.WriteAsync(customDataJson);
                        }

                        Console.WriteLine($"[ZipProjectService] Saved module: {moduleId}");
                    }
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

                    var moduleIds = archive.Entries
                        .Where(e => e.FullName.StartsWith("modules/") && e.FullName.EndsWith("/customdata.json"))
                        .Select(e => e.FullName.Split('/')[1])
                        .Distinct()
                        .ToList();

                    foreach (var moduleId in moduleIds)
                    {
                        var customDataEntry = archive.GetEntry($"modules/{moduleId}/customdata.json");
                        if (customDataEntry != null)
                        {
                            using (var reader = new StreamReader(customDataEntry.Open()))
                            {
                                var customDataJson = await reader.ReadToEndAsync();
                                var customData = JsonConvert.DeserializeObject<object>(customDataJson);

                                project.ModulesData[moduleId] = customData;
                                Console.WriteLine($"[ZipProjectService] Loaded module: {moduleId}");
                            }
                        }
                    }

                    Console.WriteLine($"[ZipProjectService] Project loaded successfully");
                    Console.WriteLine($"[ZipProjectService] Modules: {project.ModulesData.Count}");
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