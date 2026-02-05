using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Text;
using Writersword.Core.Interfaces.Services;
using Writersword.Core.Models.Settings;
using Writersword.Src.Core.Interfaces.Services;

namespace Writersword.Src.Infrastructure.Services.WorkModes
{
    /// <summary>
    /// Сервис управления локальной конфигурацией workspace
    /// Читает/пишет workspace.json внутри ZIP проекта
    /// </summary>
    public class WorkspaceConfigService : IWorkspaceConfigService
    {
        private readonly ILogger<WorkspaceConfigService> _logger;
        private const string ConfigFileName = "workspace.json";

        public WorkspaceConfigService()
        {
            _logger = App.Services.GetService<ILogger<WorkspaceConfigService>>()!;
        }

        /// <summary>
        /// Загрузить локальную конфигурацию из workspace.json в ZIP
        /// Возвращает null если файл не найден или ошибка чтения
        /// </summary>
        public WorkspaceLocalConfig? LoadFromZip(IProjectFileStorage fileStorage)
        {
            try
            {
                _logger.LogDebug("Loading workspace.json from ZIP");

                if (!fileStorage.FileExists(ConfigFileName))
                {
                    _logger.LogDebug("workspace.json not found in ZIP");
                    return null;
                }

                var data = fileStorage.ReadFile(ConfigFileName);
                if (data == null || data.Length == 0)
                {
                    _logger.LogWarning("workspace.json is empty");
                    return null;
                }

                var json = Encoding.UTF8.GetString(data);
                var config = JsonConvert.DeserializeObject<WorkspaceLocalConfig>(json);

                if (config != null)
                {
                    _logger.LogDebug("WorkModes count: {Count}", config.WorkModes.Count);
                }

                return config;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load config");
                return null;
            }
        }

        /// <summary>
        /// Сохранить локальную конфигурацию в workspace.json в ZIP
        /// </summary>
        public bool SaveToZip(IProjectFileStorage fileStorage, WorkspaceLocalConfig config)
        {
            try
            {
                _logger.LogDebug("Saving workspace.json to ZIP");

                var json = JsonConvert.SerializeObject(config, Formatting.Indented);
                var data = Encoding.UTF8.GetBytes(json);

                fileStorage.WriteFile(ConfigFileName, data);

                _logger.LogDebug("WorkModes count: {Count}", config.WorkModes.Count);
                _logger.LogDebug("Size: {Size} bytes", data.Length);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save config");
                return false;
            }
        }

        /// <summary>
        /// Удалить workspace.json из ZIP
        /// </summary>
        public bool DeleteFromZip(IProjectFileStorage fileStorage)
        {
            try
            {
                _logger.LogDebug("Deleting workspace.json from ZIP");

                if (!fileStorage.FileExists(ConfigFileName))
                {
                    _logger.LogDebug("File not found, nothing to delete");
                    return true;
                }

                fileStorage.DeleteFile(ConfigFileName);

                _logger.LogDebug("Deleted workspace.json");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete config");
                return false;
            }
        }
    }
}