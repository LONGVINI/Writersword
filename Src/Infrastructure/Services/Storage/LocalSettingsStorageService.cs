using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Text;
using Writersword.Core.Interfaces.Services;
using Writersword.Src.Core.Interfaces.Services.Storage;

namespace Writersword.Src.Infrastructure.Services.Storage
{
    /// <summary>
    /// Реализация сервиса локальных настроек модулей.
    /// Сериализует объект в JSON и пишет в {moduleType}/settings.json внутри project.zip.
    /// </summary>
    public class LocalSettingsStorageService : ILocalSettingsStorageService
    {
        private readonly ILogger<LocalSettingsStorageService> _logger;

        public LocalSettingsStorageService()
        {
            _logger = App.Services.GetService<ILogger<LocalSettingsStorageService>>()!;
        }

        /// <summary>
        /// Сохранить локальные настройки модуля в ZIP.
        /// </summary>
        public void Save(IProjectFileStorage storage, string moduleType, object settings)
        {
            try
            {
                var path = $"Modules/{moduleType}/settings.json";
                var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                var bytes = Encoding.UTF8.GetBytes(json);
                storage.WriteFile(path, bytes);
                _logger.LogDebug("Local settings saved: {ModuleType}", moduleType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save local settings for {ModuleType}", moduleType);
            }
        }

        /// <summary>
        /// Загрузить локальные настройки модуля из ZIP.
        /// </summary>
        public object? Load(IProjectFileStorage storage, string moduleType, Type settingsType)
        {
            try
            {
                var path = $"Modules/{moduleType}/settings.json";
                var bytes = storage.ReadFile(path);

                if (bytes == null)
                {
                    _logger.LogDebug("No local settings found: {ModuleType}", moduleType);
                    return null;
                }

                var json = Encoding.UTF8.GetString(bytes);
                var settings = JsonConvert.DeserializeObject(json, settingsType);
                _logger.LogDebug("Local settings loaded: {ModuleType}", moduleType);
                return settings;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load local settings for {ModuleType}", moduleType);
                return null;
            }
        }
    }
}