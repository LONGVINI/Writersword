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
        private const string ConfigFileName = "workspace.json";

        /// <summary>
        /// Загрузить локальную конфигурацию из workspace.json в ZIP
        /// Возвращает null если файл не найден или ошибка чтения
        /// </summary>
        public WorkspaceLocalConfig? LoadFromZip(IProjectFileStorage fileStorage)
        {
            try
            {
                Console.WriteLine("[WorkspaceConfigService] Loading workspace.json from ZIP");

                if (!fileStorage.FileExists(ConfigFileName))
                {
                    Console.WriteLine("[WorkspaceConfigService] workspace.json not found in ZIP");
                    return null;
                }

                var data = fileStorage.ReadFile(ConfigFileName);
                if (data == null || data.Length == 0)
                {
                    Console.WriteLine("[WorkspaceConfigService] workspace.json is empty");
                    return null;
                }

                var json = Encoding.UTF8.GetString(data);
                var config = JsonConvert.DeserializeObject<WorkspaceLocalConfig>(json);

                if (config != null)
                {
                    Console.WriteLine($"[WorkspaceConfigService]   WorkModes count: {config.WorkModes.Count}");
                }

                return config;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WorkspaceConfigService] ERROR loading config: {ex.Message}");
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
                Console.WriteLine("[WorkspaceConfigService] Saving workspace.json to ZIP");

                var json = JsonConvert.SerializeObject(config, Formatting.Indented);
                var data = Encoding.UTF8.GetBytes(json);

                fileStorage.WriteFile(ConfigFileName, data);

                Console.WriteLine($"[WorkspaceConfigService]   WorkModes count: {config.WorkModes.Count}");
                Console.WriteLine($"[WorkspaceConfigService]   Size: {data.Length} bytes");

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WorkspaceConfigService] ERROR saving config: {ex.Message}");
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
                Console.WriteLine("[WorkspaceConfigService] Deleting workspace.json from ZIP");

                if (!fileStorage.FileExists(ConfigFileName))
                {
                    Console.WriteLine("[WorkspaceConfigService] File not found, nothing to delete");
                    return true;
                }

                fileStorage.DeleteFile(ConfigFileName);

                Console.WriteLine("[WorkspaceConfigService] Deleted workspace.json");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WorkspaceConfigService] ERROR deleting config: {ex.Message}");
                return false;
            }
        }
    }
}