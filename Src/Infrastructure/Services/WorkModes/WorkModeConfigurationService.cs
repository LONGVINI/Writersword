using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using Writersword.Core.Enums;
using Writersword.Core.Interfaces.Services;
using Writersword.Core.Models.WorkModes;
using Writersword.Src.Core.Interfaces.Services;
using Writersword.Src.Core.Interfaces.Services.Storage;
using Writersword.Src.WorkModes.Common;

namespace Writersword.Src.Infrastructure.Services.WorkModes
{
    /// <summary>
    /// Сервис управления конфигурациями WorkModes
    /// Определяет приоритет: LOCAL (workspace.json в ZIP) → GLOBAL (Settings.json) → DEFAULT (hardcoded)
    /// </summary>
    public class WorkModeConfigurationService : IWorkModeConfigurationService
    {
        private readonly ILogger<WorkModeConfigurationService> _logger;

        public WorkModeConfigurationService()
        {
            _logger = App.Services.GetService<ILogger<WorkModeConfigurationService>>()!;
        }

        /// <summary>
        /// Загрузить конфигурацию для проекта
        /// Приоритет: LOCAL (workspace.json в ZIP) → GLOBAL (Settings.json) → DEFAULT (hardcoded)
        /// </summary>
        public List<WorkMode> LoadConfiguration(string projectType, IProjectFileStorage? fileStorage = null)
        {
            if (fileStorage != null)
            {
                var localWorkModes = LoadLocalConfiguration(fileStorage);
                if (localWorkModes != null && localWorkModes.Count > 0)
                {
                    _logger.LogDebug("Using LOCAL configuration ({Count} modes)", localWorkModes.Count);
                    return localWorkModes;
                }
            }

            var globalWorkModes = LoadGlobalConfiguration(projectType);
            if (globalWorkModes != null && globalWorkModes.Count > 0)
            {
                _logger.LogDebug("Using GLOBAL configuration ({Count} modes)", globalWorkModes.Count);
                return globalWorkModes;
            }

            _logger.LogDebug("Using DEFAULT configuration");
            return LoadDefaultConfiguration(projectType);
        }

        /// <summary>
        /// Загрузить дефолтную конфигурацию из реестра WorkMode (hardcoded)
        /// </summary>
        public List<WorkMode> LoadDefaultConfiguration(string projectType)
        {
            var workModes = new List<WorkMode>();

            var workModeRegistry = App.Services.GetRequiredService<WorkModeRegistry>();
            var registeredWorkModes = workModeRegistry.GetWorkModesForProjectType(projectType);

            if (registeredWorkModes == null || registeredWorkModes.Count == 0)
            {
                _logger.LogWarning("No WorkModes registered for project type: {ProjectType}", projectType);
                return new List<WorkMode>();
            }

            foreach (var registeredWM in registeredWorkModes)
            {
                var workMode = registeredWM.GetDefaultConfig();
                workMode.IsActive = registeredWM.Order == 0;
                workModes.Add(workMode);
                _logger.LogDebug("Loaded default config for: {Title}", workMode.Title);
            }

            _logger.LogDebug("Created DEFAULT configuration with {Count} modes", workModes.Count);
            return workModes;
        }

        /// <summary>
        /// Загрузить глобальную конфигурацию из Settings.json
        /// </summary>
        private List<WorkMode>? LoadGlobalConfiguration(string projectType)
        {
            try
            {
                var settingsService = App.Services.GetRequiredService<ISettingsService>();
                var workspaceConfig = settingsService.GetWorkspaceConfig(projectType);

                if (workspaceConfig == null)
                {
                    _logger.LogDebug("No global config for: {ProjectType}", projectType);
                    return null;
                }

                _logger.LogDebug("Loading global config: {Count} modes", workspaceConfig.WorkModes.Count);
                return workspaceConfig.WorkModes;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading global config");
                return null;
            }
        }

        /// <summary>
        /// Загрузить локальную конфигурацию из workspace.json в ZIP
        /// </summary>
        private List<WorkMode>? LoadLocalConfiguration(IProjectFileStorage fileStorage)
        {
            try
            {
                var workspaceConfigService = App.Services.GetRequiredService<IWorkspaceConfigService>();
                var config = workspaceConfigService.LoadFromZip(fileStorage);

                if (config == null)
                {
                    _logger.LogDebug("No local config in ZIP");
                    return null;
                }

                _logger.LogDebug("Loaded local config: {Count} modes", config.WorkModes.Count);

                RestoreModuleMetadata(config.WorkModes);
                RestoreEmptyWorkModes(config.WorkModes);

                return config.WorkModes;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading local config");
                return null;
            }
        }

        /// <summary>
        /// Восстановить дефолтную конфигурацию для пустых WorkModes
        /// </summary>
        private void RestoreEmptyWorkModes(List<WorkMode> workModes)
        {
            var workModeRegistry = App.Services.GetRequiredService<WorkModeRegistry>();

            foreach (var workMode in workModes)
            {
                bool isEmptyOrCorrupted = workMode.ModuleSlots == null
                    || workMode.ModuleSlots.Count == 0
                    || workMode.ModuleSlots.All(s => string.IsNullOrEmpty(s.ModuleType));

                if (isEmptyOrCorrupted)
                {
                    _logger.LogDebug("WorkMode '{Title}' is empty or corrupted, restoring defaults", workMode.Title);

                    var registeredWorkMode = workModeRegistry.GetWorkMode(workMode.WorkModeId);

                    if (registeredWorkMode != null)
                    {
                        var defaultConfig = registeredWorkMode.GetDefaultConfig();
                        workMode.ModuleSlots = new List<ModuleSlot>(defaultConfig.ModuleSlots);
                        _logger.LogDebug("Restored {SlotsCount} slots for '{Title}'",
                            workMode.ModuleSlots.Count, workMode.Title);
                    }
                    else
                    {
                        _logger.LogWarning("WorkMode not registered: {WorkModeId}", workMode.WorkModeId);
                    }
                }
            }
        }

        /// <summary>
        /// Восстановить метаданные модулей из дефолтной конфигурации
        /// Заполняет Category, PreferredPosition
        /// Принудительно включает Required модули если они отсутствуют
        /// </summary>
        private void RestoreModuleMetadata(List<WorkMode> workModes)
        {
            var workModeRegistry = App.Services.GetRequiredService<WorkModeRegistry>();

            foreach (var workMode in workModes)
            {
                var registeredWorkMode = workModeRegistry.GetWorkMode(workMode.WorkModeId);

                if (registeredWorkMode == null)
                {
                    _logger.LogWarning("WorkMode not registered: {WorkModeId}", workMode.WorkModeId);
                    continue;
                }

                var defaultConfig = registeredWorkMode.GetDefaultConfig();

                workMode.ModuleCategories = new Dictionary<string, ModuleCategory>(defaultConfig.ModuleCategories);

                _logger.LogDebug("Restored {Count} module categories for WorkMode: {Title}",
                    workMode.ModuleCategories.Count, workMode.Title);

                foreach (var slot in workMode.ModuleSlots)
                {
                    var category = workMode.ModuleCategories.TryGetValue(slot.ModuleType, out var explicitCategory)
                        ? explicitCategory
                        : ModuleCategory.Optional;

                    slot.Category = category;

                    var defaultSlot = defaultConfig.ModuleSlots
                        .FirstOrDefault(s => s.ModuleType == slot.ModuleType);

                    if (defaultSlot != null)
                    {
                        slot.PreferredPosition = defaultSlot.PreferredPosition;
                        _logger.LogDebug("Restored metadata for {ModuleType}: Category={Category}",
                            slot.ModuleType, category);
                    }
                    else
                    {
                        _logger.LogDebug("No default config for {ModuleType}, using slot defaults. Category={Category}",
                            slot.ModuleType, category);
                    }
                }

                foreach (var defaultSlot in defaultConfig.ModuleSlots)
                {
                    if (defaultSlot.Category != ModuleCategory.Required)
                        continue;

                    bool existsInSlots = workMode.ModuleSlots.Any(s => s.ModuleType == defaultSlot.ModuleType);

                    if (!existsInSlots)
                    {
                        _logger.LogDebug("Required module {ModuleType} missing, adding to slots",
                            defaultSlot.ModuleType);

                        workMode.ModuleSlots.Add(new ModuleSlot
                        {
                            ModuleType = defaultSlot.ModuleType,
                            PreferredPosition = defaultSlot.PreferredPosition,
                            Category = ModuleCategory.Required
                        });
                    }
                }
            }
        }

        /// <summary>
        /// Проверить можно ли удалить модуль из WorkMode
        /// </summary>
        public bool CanRemoveModule(string projectType, string workModeId, string moduleType)
        {
            var defaultConfig = LoadDefaultConfiguration(projectType);
            var workMode = defaultConfig.FirstOrDefault(wm => wm.WorkModeId == workModeId);

            if (workMode == null)
            {
                _logger.LogWarning("WorkMode not found: {WorkModeId}", workModeId);
                return true;
            }

            var moduleSlot = workMode.ModuleSlots.FirstOrDefault(ms => ms.ModuleType == moduleType);
            if (moduleSlot == null)
            {
                _logger.LogDebug("Module not found in WorkMode: {ModuleType}", moduleType);
                return true;
            }

            bool canRemove = moduleSlot.IsCloseable;
            _logger.LogDebug("CanRemoveModule({WorkModeId}, {ModuleType}): {CanRemove}",
                workModeId, moduleType, canRemove);
            return canRemove;
        }
    }
}