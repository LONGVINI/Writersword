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
using Writersword.Shared.Helpers;

namespace Writersword.Infrastructure.Services.Project
{
    /// <summary>
    /// ������ ��� ������ � ��������� � ������� ZIP
    /// �� ���������� ��������� ����� � �������� � ZIP ��������
    /// �������� ������ �� project.json � modules/*.json
    /// workspace.json ����������� ����� IWorkspaceConfigService
    /// ���� ������ ������ � moduleType (������), �� InstanceId
    /// </summary>
    public class ZipProjectService
    {
        private readonly ILogger<ZipProjectService> _logger;

        public ZipProjectService()
        {
            _logger = App.Services.GetService<ILogger<ZipProjectService>>()!;
        }

        /// <summary>
        /// ��������� ������ � ZIP �����
        /// ������ ����� ZIP ��� ��������� ������������
        /// </summary>
        public async Task<bool> SaveToZipAsync(ProjectFile project, string filePath)
        {
            try
            {
                _logger.LogDebug("Saving to ZIP: {FilePath}", filePath);

                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                // Атомарная запись через временный файл: сначала пишем во временный файл,
                // затем атомарно заменяем целевой файл. Это гарантирует что при любом сбое
                // в процессе записи оригинальный файл остаётся нетронутым.
                string tempPath = filePath + ".tmp";

                _logger.LogDebug("Writing to temp file: {TempPath}", tempPath);

                using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite))
                using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create))
                {
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
                        var moduleType = moduleEntry.Key;
                        var customData = moduleEntry.Value;

                        var metadataEntry = archive.CreateEntry(
                            $"modules/{moduleType}/Metadata.json", CompressionLevel.Optimal);
                        using (var writer = new StreamWriter(metadataEntry.Open()))
                        {
                            var metadata = new { moduleType };
                            await writer.WriteAsync(JsonHelper.Serialize(metadata));
                        }

                        _logger.LogDebug("Saved Metadata for: {moduleType}", moduleType);

                        if (customData != null && !(customData is string str && string.IsNullOrWhiteSpace(str)))
                        {
                            string customDataJson = customData is string s
                                ? s
                                : JsonHelper.Serialize(customData);

                            var customDataEntry = archive.CreateEntry(
                                $"modules/{moduleType}/CustomData.json", CompressionLevel.Optimal);
                            using (var writer = new StreamWriter(customDataEntry.Open()))
                            {
                                await writer.WriteAsync(customDataJson);
                            }

                            _logger.LogDebug("Saved CustomData for: {moduleType}", moduleType);
                        }
                    }
                }

                // Запись завершена успешно — заменяем целевой файл атомарно.
                File.Move(tempPath, filePath, overwrite: true);

                var fileSize = new FileInfo(filePath).Length / 1024;
                _logger.LogDebug("ZIP saved: {FilePath} ({FileSize} KB)", filePath, fileSize);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Save error");

                // Удаляем временный файл если он остался после сбоя.
                try
                {
                    string tempPath = filePath + ".tmp";
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch { }

                return false;
            }
        }

        /// <summary>
        /// ��������� ������ �� ZIP ������
        /// ������ ���������� � ������ ������� �������� �� ZIP
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
                        .Where(e => e.FullName.StartsWith("modules/", StringComparison.OrdinalIgnoreCase)
                                 && e.FullName.EndsWith("/CustomData.json", StringComparison.OrdinalIgnoreCase))
                        .Select(e => e.FullName.Split('/')[1])
                        .Distinct()
                        .ToList();

                    foreach (var moduleType in moduleIds)
                    {
                        var customDataEntry = archive.Entries.FirstOrDefault(e =>
                            e.FullName.Equals($"modules/{moduleType}/CustomData.json",
                                StringComparison.OrdinalIgnoreCase));

                        if (customDataEntry != null)
                        {
                            using (var reader = new StreamReader(customDataEntry.Open()))
                            {
                                var customDataJson = await reader.ReadToEndAsync();
                                project.ModulesData[moduleType] = customDataJson;
                                _logger.LogDebug("Loaded module: {moduleType}", moduleType);
                            }
                        }
                    }

                    _logger.LogDebug("Project loaded: {Title}, {Count} modules", project.Title, project.ModulesData.Count);
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