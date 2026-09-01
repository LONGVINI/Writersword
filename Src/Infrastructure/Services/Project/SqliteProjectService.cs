using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Writersword.Core.Models.Project;
using Writersword.Core.Services.Storage;
using Writersword.Shared.Helpers;

namespace Writersword.Infrastructure.Services.Project
{
    /// <summary>
    /// Чтение и запись файла проекта.
    ///
    /// Пришло на смену ZipProjectService. Разница не только в контейнере: из
    /// сохранения ушли два шага, которые существовали исключительно из-за
    /// устройства ZIP.
    ///
    /// Первый — перенос «посторонних» файлов. Архив пересоздавался с нуля, и
    /// раскладка окон с локальными настройками модулей исчезала при каждом
    /// сохранении, если её не скопировать вручную. В базе ничего не
    /// пересоздаётся: не тронутые записи просто остаются на месте.
    ///
    /// Второй — запись во временный файл с последующей заменой. Атомарность
    /// теперь обеспечивает транзакция, и промежуточного состояния, в котором
    /// проект наполовину записан, не существует.
    /// </summary>
    public class SqliteProjectService
    {
        private const string ProjectEntry = "project.json";
        private const string ModulesPrefix = "modules/";

        private readonly ILogger<SqliteProjectService> _logger;
        private readonly Serilog.ILogger _serilog;

        // Сохранения не идут внахлёст: автосохранение по таймеру и ручное
        // сохранение легко приходятся на один момент.
        private readonly SemaphoreSlim _saveGate = new(1, 1);

        public SqliteProjectService()
        {
            _logger = App.Services.GetService<ILogger<SqliteProjectService>>()!;
            _serilog = Serilog.Log.Logger;
        }

        /// <summary>
        /// Записать проект в файл.
        ///
        /// Записываются только метаданные и данные модулей. Всё остальное, что
        /// лежит в проекте — раскладка, локальные настройки, картинки, шрифты, —
        /// не читается и не переписывается вовсе.
        /// </summary>
        public async Task<bool> SaveAsync(ProjectFile project, string filePath)
        {
            await _saveGate.WaitAsync();

            try
            {
                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                // Работа с базой уходит в пул потоков: вызов приходит из
                // UI-контекста, а запись мегабайтов на диск его блокировать
                // не должна.
                return await Task.Run(() =>
                {
                    using var storage = new SqliteFileStorageService(filePath, _serilog);

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

                    storage.WriteFile(ProjectEntry,
                        Encoding.UTF8.GetBytes(JsonHelper.Serialize(projectMeta)));

                    foreach (var moduleEntry in project.ModulesData)
                    {
                        var moduleType = moduleEntry.Key;
                        var customData = moduleEntry.Value;

                        storage.WriteFile($"{ModulesPrefix}{moduleType}/Metadata.json",
                            Encoding.UTF8.GetBytes(JsonHelper.Serialize(new { moduleType })));

                        if (customData is null || (customData is string empty && string.IsNullOrWhiteSpace(empty)))
                        {
                            // Модуль отдал пустоту — прежние данные убираются,
                            // иначе в проекте осталась бы версия недельной
                            // давности, выглядящая как настоящая.
                            storage.DeleteFile($"{ModulesPrefix}{moduleType}/CustomData.json");
                            continue;
                        }

                        var customDataJson = customData is string s ? s : JsonHelper.Serialize(customData);

                        storage.WriteFile($"{ModulesPrefix}{moduleType}/CustomData.json",
                            Encoding.UTF8.GetBytes(customDataJson));
                    }

                    // Журнал сливается в основной файл: дальше проект может
                    // уехать в хранилище или попасть в точку восстановления,
                    // и рядом лежащий WAL там был бы лишним.
                    storage.Flush();

                    var fileSize = new FileInfo(filePath).Length / 1024;
                    _logger.LogDebug("Project saved: {FilePath} ({FileSize} KB)", filePath, fileSize);

                    return true;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Save error");
                return false;
            }
            finally
            {
                _saveGate.Release();
            }
        }

        /// <summary>Прочитать проект из файла.</summary>
        public async Task<ProjectFile?> LoadAsync(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    _logger.LogWarning("File not found: {FilePath}", filePath);
                    return null;
                }

                return await Task.Run(() =>
                {
                    using var storage = new SqliteFileStorageService(filePath, _serilog);

                    var projectBytes = storage.ReadFile(ProjectEntry);
                    if (projectBytes is null)
                    {
                        _logger.LogWarning("project.json not found in {FilePath}", filePath);
                        return null;
                    }

                    var project = JsonConvert.DeserializeObject<ProjectFile>(
                        Encoding.UTF8.GetString(projectBytes));

                    if (project is null)
                    {
                        _logger.LogWarning("Failed to deserialize project.json");
                        return null;
                    }

                    foreach (var moduleType in EnumerateModules(storage))
                    {
                        var data = storage.ReadFile($"{ModulesPrefix}{moduleType}/CustomData.json");
                        if (data is null)
                            continue;

                        project.ModulesData[moduleType] = Encoding.UTF8.GetString(data);
                        _logger.LogDebug("Loaded module: {ModuleType}", moduleType);
                    }

                    _logger.LogDebug("Project loaded: {Title}, {Count} modules",
                        project.Title, project.ModulesData.Count);

                    return project;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Load error");
                return null;
            }
        }

        /// <summary>
        /// Типы модулей, у которых есть данные.
        ///
        /// Список берётся из путей, а не из отдельного перечня: перечень мог бы
        /// разойтись с содержимым, а пути — это и есть содержимое.
        /// </summary>
        private static IEnumerable<string> EnumerateModules(SqliteFileStorageService storage)
            => storage.GetFiles(ModulesPrefix)
                .Where(path => path.EndsWith("/CustomData.json", StringComparison.OrdinalIgnoreCase))
                .Select(path => path.Split('/'))
                .Where(parts => parts.Length >= 3)
                .Select(parts => parts[1])
                .Distinct()
                .ToList();

        /// <summary>
        /// Создать пустой файл проекта.
        ///
        /// Нужен отдельно от сохранения: программа создаёт файл до того, как в
        /// нём появятся модули, и открывать его потом должно быть можно.
        /// </summary>
        public async Task<bool> CreateEmptyAsync(ProjectFile project, string filePath)
            => await SaveAsync(project, filePath);
    }
}
