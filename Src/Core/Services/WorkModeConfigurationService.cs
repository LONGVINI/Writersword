using System.Collections.Generic;
using System.Linq;
using Writersword.Core.Enums;
using Writersword.Core.Models.Settings;
using Writersword.Core.Models.WorkModes;
using Writersword.Services.Interfaces;
using Writersword.Src.Core.Interfaces.WorkModes;
using Writersword.Src.WorkModes.Common;
using Writersword.Modules.Common;

namespace Writersword.Core.Services.WorkModes
{
    public class WorkModeConfigurationService : IWorkModeConfigurationService
    {
        private readonly ISettingsService _settingsService;
        private readonly WorkModeRegistry _workModeRegistry;
        private readonly ModuleRegistry _moduleRegistry;

        public WorkModeConfigurationService(
            ISettingsService settingsService,
            WorkModeRegistry workModeRegistry,
            ModuleRegistry moduleRegistry)
        {
            _settingsService = settingsService;
            _workModeRegistry = workModeRegistry;
            _moduleRegistry = moduleRegistry;
        }

        public List<WorkMode> LoadConfiguration(string projectType, List<WorkMode>? projectWorkModes)
        {
            if (projectWorkModes != null && projectWorkModes.Count > 0)
            {
                System.Console.WriteLine($"[WorkModeConfig] Loading from PROJECT for {projectType}");
                return CloneWorkModes(projectWorkModes);
            }

            var globalConfig = _settingsService.GetWorkspaceConfig(projectType);
            if (globalConfig != null && globalConfig.WorkModes.Count > 0)
            {
                System.Console.WriteLine($"[WorkModeConfig] Loading from GLOBAL config for {projectType}");
                return CloneWorkModes(globalConfig.WorkModes);
            }

            System.Console.WriteLine($"[WorkModeConfig] Loading DEFAULT config for {projectType}");
            return LoadDefaultConfiguration(projectType);
        }

        public List<WorkMode> LoadDefaultConfiguration(string projectType)
        {
            var workModes = new List<WorkMode>();
            var allWorkModes = _workModeRegistry.GetAll();

            foreach (var workModeInstance in allWorkModes)
            {
                var defaultConfig = workModeInstance.GetDefaultConfig();

                var workMode = new WorkMode
                {
                    WorkModeId = workModeInstance.Id,
                    Title = workModeInstance.DisplayName,
                    Icon = workModeInstance.Icon,
                    Order = defaultConfig.Order,
                    IsCloseable = workModeInstance.IsCloseable,
                    IsActive = false,
                    ModuleSlots = defaultConfig.ModuleSlots.Select(slotConfig =>
                    {
                        var moduleMetadata = _moduleRegistry.GetAllModuleMetadata()
                            .FirstOrDefault(m => m.ModuleType == slotConfig.ModuleType);

                        return new ModuleSlot
                        {
                            ModuleType = slotConfig.ModuleType,
                            MinWidth = slotConfig.MinWidth,
                            MinHeight = slotConfig.MinHeight,
                            IsVisible = slotConfig.IsVisible,
                            IsResizable = true,
                            IsCloseable = slotConfig.Category != ModuleCategory.Required,
                            PreferredPosition = slotConfig.PreferredPosition ?? moduleMetadata?.DefaultPosition ?? PreferredDockPosition.RightAsTab
                        };
                    }).ToList(),
                    Settings = new WorkModeSettings
                    {
                        CustomSettings = new Dictionary<string, object>
                        {
                            ["DockLayout"] = defaultConfig.DockLayout
                        }
                    }
                };

                workModes.Add(workMode);
            }

            return workModes.OrderBy(wm => wm.Order).ToList();
        }

        public void SaveGlobalConfiguration(string projectType, List<WorkMode> workModes)
        {
            var config = new WorkspaceConfig
            {
                ProjectType = projectType,
                Name = $"{projectType} Custom Configuration",
                WorkModes = CloneWorkModes(workModes)
            };

            _settingsService.SaveWorkspaceConfig(projectType, config);
            System.Console.WriteLine($"[WorkModeConfig] Saved GLOBAL config for {projectType}");
        }

        public void DeleteGlobalConfiguration(string projectType)
        {
            _settingsService.DeleteWorkspaceConfig(projectType);
            System.Console.WriteLine($"[WorkModeConfig] Deleted GLOBAL config for {projectType}");
        }

        public bool CanRemoveModule(string workModeId, ModuleType moduleType)
        {
            var workMode = _workModeRegistry.GetWorkMode(workModeId);
            if (workMode == null) return true;

            var defaultConfig = workMode.GetDefaultConfig();
            var moduleConfig = defaultConfig.ModuleSlots.FirstOrDefault(m => m.ModuleType == moduleType);

            return moduleConfig == null || moduleConfig.Category != ModuleCategory.Required;
        }

        public List<ModuleType> GetRequiredModules(string workModeId)
        {
            var workMode = _workModeRegistry.GetWorkMode(workModeId);
            if (workMode == null) return [];

            var defaultConfig = workMode.GetDefaultConfig();
            return defaultConfig.ModuleSlots
                .Where(m => m.Category == ModuleCategory.Required)
                .Select(m => m.ModuleType)
                .ToList();
        }

        public List<WorkMode> CloneWorkModes(List<WorkMode> source)
        {
            return source.Select(wm => new WorkMode
            {
                Id = System.Guid.NewGuid().ToString(),
                WorkModeId = wm.WorkModeId,
                Title = wm.Title,
                Icon = wm.Icon,
                Order = wm.Order,
                IsCloseable = wm.IsCloseable,
                IsActive = wm.IsActive,
                ModuleSlots = wm.ModuleSlots.Select(ms => new ModuleSlot
                {
                    Id = System.Guid.NewGuid().ToString(),
                    ModuleType = ms.ModuleType,
                    MinWidth = ms.MinWidth,
                    MinHeight = ms.MinHeight,
                    IsResizable = ms.IsResizable,
                    IsVisible = ms.IsVisible,
                    IsCloseable = ms.IsCloseable,
                    PreferredPosition = ms.PreferredPosition,
                    ModuleState = new Dictionary<string, object>(ms.ModuleState)
                }).ToList(),
                Settings = new WorkModeSettings
                {
                    CustomSettings = new Dictionary<string, object>(wm.Settings.CustomSettings)
                }
            }).ToList();
        }
    }
}