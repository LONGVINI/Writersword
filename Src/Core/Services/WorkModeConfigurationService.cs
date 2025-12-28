using System.Collections.Generic;
using System.Linq;
using Writersword.Core.Enums;
using Writersword.Core.Models.Settings;
using Writersword.Core.Models.WorkModes;
using Writersword.Services.Interfaces;
using Writersword.Src.Core.Interfaces.WorkModes;
using Writersword.Src.WorkModes.Common;

namespace Writersword.Core.Services.WorkModes
{
    /// <summary>
    /// Сервис управления конфигурациями WorkModes
    /// Определяет приоритет: Проект → Глобальная → Дефолтная
    /// </summary>
    public class WorkModeConfigurationService : IWorkModeConfigurationService
    {
        private readonly ISettingsService _settingsService;
        private readonly WorkModeRegistry _workModeRegistry;

        public WorkModeConfigurationService(
            ISettingsService settingsService,
            WorkModeRegistry workModeRegistry)
        {
            _settingsService = settingsService;
            _workModeRegistry = workModeRegistry;
        }

        /// <summary>
        /// Загрузить конфигурацию для проекта
        /// Приоритет: Проект → Глобальная → Дефолтная
        /// </summary>
        public List<WorkMode> LoadConfiguration(ProjectType projectType, List<WorkMode>? projectWorkModes)
        {
            // 1. Если в проекте уже есть настройки - используем их
            if (projectWorkModes != null && projectWorkModes.Count > 0)
            {
                System.Console.WriteLine($"[WorkModeConfig] Loading from PROJECT for {projectType}");
                return CloneWorkModes(projectWorkModes);
            }

            // 2. Если есть глобальная конфигурация пользователя - используем её
            var globalConfig = _settingsService.GetWorkspaceConfig(projectType);
            if (globalConfig != null && globalConfig.WorkModes.Count > 0)
            {
                System.Console.WriteLine($"[WorkModeConfig] Loading from GLOBAL config for {projectType}");
                return CloneWorkModes(globalConfig.WorkModes);
            }

            // 3. Используем дефолтную конфигурацию
            System.Console.WriteLine($"[WorkModeConfig] Loading DEFAULT config for {projectType}");
            return LoadDefaultConfiguration(projectType);
        }

        /// <summary>Загрузить дефолтную конфигурацию</summary>
        public List<WorkMode> LoadDefaultConfiguration(ProjectType projectType)
        {
            var workModes = new List<WorkMode>();

            // Получаем все зарегистрированные WorkModes
            var allWorkModes = _workModeRegistry.GetAll();

            foreach (var workModeInstance in allWorkModes)
            {
                // Получаем DEFAULT конфигурацию из каждого WorkMode
                var defaultConfig = workModeInstance.GetDefaultConfig();

                // Создаём экземпляр WorkMode
                var workMode = new WorkMode
                {
                    WorkModeId = workModeInstance.Id,
                    Title = workModeInstance.DisplayName,
                    Icon = workModeInstance.Icon,
                    Order = defaultConfig.Order,
                    IsCloseable = workModeInstance.IsCloseable,
                    IsActive = false,
                    ModuleSlots = defaultConfig.ModuleSlots.Select(slotConfig => new ModuleSlot
                    {
                        ModuleType = slotConfig.ModuleType,
                        MinWidth = slotConfig.MinWidth,
                        MinHeight = slotConfig.MinHeight,
                        IsVisible = slotConfig.IsVisible,
                        IsResizable = true,
                        IsCloseable = slotConfig.Category != ModuleCategory.Required,
                        PreferredPosition = slotConfig.PreferredPosition
                    }).ToList(),
                    Settings = new WorkModeSettings
                    {
                        CustomSettings = new Dictionary<string, object>
                        {
                            ["DockLayout"] = defaultConfig.DockLayout // ← СОХРАНЯЕМ DockLayout!
                        }
                    }
                };

                workModes.Add(workMode);
            }

            return workModes.OrderBy(wm => wm.Order).ToList();
        }

        /// <summary>Сохранить конфигурацию глобально</summary>
        public void SaveGlobalConfiguration(ProjectType projectType, List<WorkMode> workModes)
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

        /// <summary>Удалить глобальную конфигурацию</summary>
        public void DeleteGlobalConfiguration(ProjectType projectType)
        {
            _settingsService.DeleteWorkspaceConfig(projectType);
            System.Console.WriteLine($"[WorkModeConfig] Deleted GLOBAL config for {projectType}");
        }

        /// <summary>Проверить можно ли удалить модуль</summary>
        public bool CanRemoveModule(string workModeId, ModuleType moduleType)
        {
            var workMode = _workModeRegistry.GetWorkMode(workModeId);
            if (workMode == null) return true;

            var defaultConfig = workMode.GetDefaultConfig();
            var moduleConfig = defaultConfig.ModuleSlots.FirstOrDefault(m => m.ModuleType == moduleType);

            return moduleConfig == null || moduleConfig.Category != ModuleCategory.Required;
        }

        /// <summary>Получить обязательные модули</summary>
        public List<ModuleType> GetRequiredModules(string workModeId)
        {
            var workMode = _workModeRegistry.GetWorkMode(workModeId);
            if (workMode == null) return new List<ModuleType>();

            var defaultConfig = workMode.GetDefaultConfig();
            return defaultConfig.ModuleSlots
                .Where(m => m.Category == ModuleCategory.Required)
                .Select(m => m.ModuleType)
                .ToList();
        }

        /// <summary>Клонировать WorkModes (глубокое копирование)</summary>
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