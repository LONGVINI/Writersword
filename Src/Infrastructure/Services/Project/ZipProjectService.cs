using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Writersword.Core.Models.Project;
using Writersword.Src.Shared.Helpers;

namespace Writersword.Src.Infrastructure.Services.Project
{
    /// <summary>
    /// Сервис для работы с проектами в формате ZIP
    /// НЕ использует временные папки - работает с ZIP напрямую
    /// Отвечает ТОЛЬКО за project.json и modules/*.json
    /// workspace.json управляется через IWorkspaceConfigService
    /// </summary>
    public class ZipProjectService
    {
        private readonly ILogger<ZipProjectService> _logger;

        public ZipProjectService()
        {
            _logger = App.Services.GetService<ILogger<ZipProjectService>>()!;
        }

        /// <summary>
        /// Сохранить проект в ZIP архив
        /// Создаёт новый ZIP или обновляет существующий
        /// </summary>
        public async Task<bool> SaveToZipAsync(ProjectFile project, string filePath)
        {
            try
            {
                _logger.LogDebug("Saving to ZIP: {FilePath}", filePath);

                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                bool fileExists = File.Exists(filePath);
                ZipArchiveMode mode = fileExists ? ZipArchiveMode.Update : ZipArchiveMode.Create;
                FileMode fileMode = fileExists ? FileMode.Open : FileMode.Create;

                _logger.LogDebug("Mode: {Mode}, FileExists: {FileExists}", mode, fileExists);

                using (var fileStream = new FileStream(filePath, fileMode, FileAccess.ReadWrite))
                using (var archive = new ZipArchive(fileStream, mode))
                {
                    if (mode == ZipArchiveMode.Update)
                    {
                        var projectEntry = archive.GetEntry("project.json");
                        if (projectEntry != null)
                        {
                            projectEntry.Delete();
                            _logger.LogDebug("Deleted old project.json");
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
                            _logger.LogDebug("Deleted {Count} old module entries", oldModules.Count);
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

                    _logger.LogDebug("Saved project.json");

                    foreach (var moduleEntry in project.ModulesData)
                    {
                        var moduleId = moduleEntry.Key;
                        var customData = moduleEntry.Value;

                        // Ищем InstanceId для этого модуля из WorkModes
                        string? instanceId = null;
                        if (project.WorkModes != null)
                        {
                            foreach (var workMode in project.WorkModes)
                            {
                                var slot = workMode.ModuleSlots.FirstOrDefault(s => s.ModuleId == moduleId && !string.IsNullOrEmpty(s.InstanceId));
                                if (slot != null)
                                {
                                    instanceId = slot.InstanceId;
                                    break;
                                }
                            }
                        }

                        // Создаем Metadata.json с InstanceId
                        var metadataEntry = archive.CreateEntry($"modules/{moduleId}/Metadata.json", CompressionLevel.Optimal);
                        using (var writer = new StreamWriter(metadataEntry.Open()))
                        {
                            var metadata = new
                            {
                                ModuleId = moduleId,
                                InstanceId = instanceId
                            };
                            var metadataJson = JsonHelper.Serialize(metadata);
                            await writer.WriteAsync(metadataJson);
                        }

                        _logger.LogDebug("Saved Metadata for: {ModuleId}, InstanceId: {InstanceId}", moduleId, instanceId ?? "null");

                        // CustomData сохраняем как раньше
                        if (customData != null && !(customData is string str && string.IsNullOrWhiteSpace(str)))
                        {
                            var customDataJson = JsonHelper.Serialize(customData);
                            var customDataEntry = archive.CreateEntry($"modules/{moduleId}/CustomData.json", CompressionLevel.Optimal);
                            using (var writer = new StreamWriter(customDataEntry.Open()))
                            {
                                await writer.WriteAsync(customDataJson);
                            }

                            _logger.LogDebug("Saved CustomData for: {ModuleId}", moduleId);
                        }
                    }
                }

                var fileSize = new FileInfo(filePath).Length / 1024;
                _logger.LogDebug("ZIP saved successfully: {FilePath} ({FileSize} KB)", filePath, fileSize);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Save error");
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
                _logger.LogDebug("Loading from ZIP: {FilePath}", filePath);

                if (!File.Exists(filePath))
                {
                    _logger.LogWarning("File not found: {FilePath}", filePath);
                    return null;
                }

                using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Read))
                {
                    var projectEntry = archive.GetEntry("project.json");
                    if (projectEntry == null)
                    {
                        _logger.LogWarning("project.json not found in ZIP");
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
                        _logger.LogWarning("Failed to deserialize project.json");
                        return null;
                    }

                    _logger.LogDebug("Loaded project.json: {Title}", project.Title);

                    var moduleIds = archive.Entries
                        .Where(e => e.FullName.StartsWith("modules/") && e.FullName.EndsWith("/CustomData.json"))  // ? Заглавная!
                        .Select(e => e.FullName.Split('/')[1])
                        .Distinct()
                        .ToList();

                    foreach (var moduleId in moduleIds)
                    {
                        var customDataEntry = archive.GetEntry($"modules/{moduleId}/CustomData.json");
                        if (customDataEntry != null)
                        {
                            using (var reader = new StreamReader(customDataEntry.Open()))
                            {
                                var customDataJson = await reader.ReadToEndAsync();
                                var customData = JsonConvert.DeserializeObject<object>(customDataJson);

                                project.ModulesData[moduleId] = customData;
                                _logger.LogDebug("Loaded module: {ModuleId}", moduleId);
                            }
                        }
                    }

                    _logger.LogDebug("Project loaded successfully");
                    _logger.LogDebug("Modules: {Count}", project.ModulesData.Count);
                    return project;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Load error");
                return null;
            }
        }
    }
}