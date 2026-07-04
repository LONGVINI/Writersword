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
    /// Сервис для работы с проектами в формате ZIP.
    /// Не управляет открытыми файлами и папками в ZIP архиве.
    /// Управляет только project.json и modules/*.json.
    /// workspace.json управляется через IWorkspaceConfigService.
    /// Ключ данных модуля — moduleType (строка), не InstanceId.
    /// </summary>
    public class ZipProjectService
    {
        private readonly ILogger<ZipProjectService> _logger;

        // Сохранения сериализуем: иначе параллельные вызовы пишут в один .tmp и
        // падают с IOException (file used by another process).
        private static readonly System.Threading.SemaphoreSlim _saveGate = new(1, 1);

        public ZipProjectService()
        {
            _logger = App.Services.GetService<ILogger<ZipProjectService>>()!;
        }

        /// <summary>
        /// Сохранить проект в ZIP файл.
        ///
        /// Алгоритм атомарной записи:
        ///   1. Пишем во временный файл (.tmp).
        ///   2. В новый архив сначала копируем все файлы из СТАРОГО архива,
        ///      которыми этот метод НЕ управляет (workspace.json, local_settings/*, и т.д.).
        ///   3. Затем записываем/перезаписываем управляемые файлы:
        ///      project.json, modules/*/Metadata.json, modules/*/CustomData.json.
        ///   4. Атомарно заменяем целевой файл временным.
        ///
        /// Это гарантирует что workspace.json и local_settings/*.json
        /// не теряются при каждом Ctrl+S.
        /// </summary>
        public async Task<bool> SaveToZipAsync(ProjectFile project, string filePath)
        {
            await _saveGate.WaitAsync();
            try
            {
                _logger.LogDebug("Saving to ZIP: {FilePath}", filePath);

                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                string tempPath = filePath + ".tmp";

                _logger.LogDebug("Writing to temp file: {TempPath}", tempPath);

                using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite))
                using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create))
                {
                    // ── Шаг 1: Копируем все «посторонние» файлы из старого ZIP ──────────────
                    // К ним относятся: workspace.json, local_settings/*.json и любые другие
                    // файлы, которые пишутся через ZipFileStorageService (WorkspaceConfigService,
                    // LocalSettingsStorageService и т.д.).
                    // Без этого шага все настройки layout и локальные настройки модулей
                    // уничтожались при каждом сохранении проекта.
                    if (File.Exists(filePath))
                    {
                        try
                        {
                            using var oldStream = new FileStream(
                                filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                            using var oldArchive = new ZipArchive(oldStream, ZipArchiveMode.Read);

                            foreach (var entry in oldArchive.Entries)
                            {
                                // Пропускаем файлы, которыми управляет этот метод —
                                // они будут записаны заново ниже.
                                bool isManagedByUs =
                                    entry.FullName.Equals("project.json",
                                        StringComparison.OrdinalIgnoreCase) ||
                                    entry.FullName.StartsWith("modules/",
                                        StringComparison.OrdinalIgnoreCase);

                                if (isManagedByUs)
                                    continue;

                                // Копируем «посторонний» файл как есть.
                                var newEntry = archive.CreateEntry(
                                    entry.FullName, CompressionLevel.Optimal);

                                await using var src = entry.Open();
                                await using var dst = newEntry.Open();
                                await src.CopyToAsync(dst);

                                _logger.LogDebug("Preserved extra file: {Entry}", entry.FullName);
                            }
                        }
                        catch (Exception ex)
                        {
                            // Если не удалось прочитать старый архив — продолжаем без копирования.
                            // Это лучше чем потерять весь проект.
                            _logger.LogWarning(ex,
                                "Could not read old ZIP for extra-file preservation: {FilePath}",
                                filePath);
                        }
                    }

                    // ── Шаг 2: Записываем project.json ───────────────────────────────────────
                    var projectMeta = new
                    {
                        project.Title,
                        project.Type,
                        project.FormatVersion,
                        project.Id,
                        project.CreatedAt,
                        project.LastModified,
                        project.ProjectPinnedColors,
                        project.ProjectRecentColors,
                        project.AvatarRingsAll,
                        project.ProjectPalettes,
                        project.GlobalPaletteOrder
                    };

                    var projectJson = JsonHelper.Serialize(projectMeta);
                    var projectEntry = archive.CreateEntry("project.json", CompressionLevel.Optimal);
                    using (var writer = new StreamWriter(projectEntry.Open()))
                    {
                        await writer.WriteAsync(projectJson);
                    }

                    _logger.LogDebug("Saved project.json");

                    // ── Шаг 3: Записываем данные модулей ─────────────────────────────────────
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

                // ── Шаг 4: Атомарно заменяем целевой файл ────────────────────────────────
                // Запись завершена успешно — только теперь заменяем оригинал.
                // При любом сбое выше оригинальный файл остаётся нетронутым.
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
            finally
            {
                _saveGate.Release();
            }
        }

        /// <summary>
        /// Загрузить проект из ZIP файла.
        /// Загружает метаданные из project.json и данные модулей из modules/*.json.
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

                    _logger.LogDebug("Project loaded: {Title}, {Count} modules",
                        project.Title, project.ModulesData.Count);
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