using System.Collections.Generic;
using System.Linq;
using Writersword.Core.Enums;
using Writersword.Core.Models.Settings;
using Writersword.Core.Models.WorkModes;
using Writersword.Services.Interfaces;
using Writersword.Src.Core.Interfaces.WorkModes;
using Writersword.Src.Core.Models.WorkModes;

namespace Writersword.Core.Services.WorkModes
{
    /// <summary>
    /// Сервис управления конфигурациями WorkModes
    /// Определяет приоритет: Проект → Глобальная → Дефолтная
    /// </summary>
    public class WorkModeConfigurationService : IWorkModeConfigurationService
    {
        private readonly ISettingsService _settingsService;

        public WorkModeConfigurationService(ISettingsService settingsService)
        {
            _settingsService = settingsService;
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
            var preset = WorkModePresetFactory.GetPreset(projectType);
            return CreateWorkModesFromPreset(preset);
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
        public bool CanRemoveModule(WorkModeType workModeType, ModuleType moduleType)
        {
            return WorkModeRules.CanRemoveModule(workModeType, moduleType);
        }

        /// <summary>Получить обязательные модули</summary>
        public List<ModuleType> GetRequiredModules(WorkModeType workModeType)
        {
            return WorkModeRules.GetRequiredModules(workModeType);
        }

        /// <summary>Клонировать WorkModes (глубокое копирование)</summary>
        public List<WorkMode> CloneWorkModes(List<WorkMode> source)
        {
            return source.Select(wm => new WorkMode
            {
                Id = System.Guid.NewGuid().ToString(), // Новый ID для копии
                Type = wm.Type,
                Title = wm.Title,
                Icon = wm.Icon,
                Order = wm.Order,
                IsCloseable = wm.IsCloseable,
                IsActive = wm.IsActive,
                ModuleSlots = wm.ModuleSlots.Select(ms => new ModuleSlot
                {
                    Id = System.Guid.NewGuid().ToString(), // Новый ID
                    ModuleType = ms.ModuleType,
                    Position = new WorkModeGridPosition
                    {
                        Row = ms.Position.Row,
                        Column = ms.Position.Column
                    },
                    Size = new WorkModeGridSize(ms.Size.RowSpan, ms.Size.ColumnSpan),
                    MinWidth = ms.MinWidth,
                    MinHeight = ms.MinHeight,
                    IsResizable = ms.IsResizable,
                    IsVisible = ms.IsVisible,
                    IsCloseable = ms.IsCloseable,
                    ModuleState = new Dictionary<string, object>(ms.ModuleState)
                }).ToList(),
                Settings = new WorkModeSettings
                {
                    LayoutType = wm.Settings.LayoutType,
                    GridColumns = wm.Settings.GridColumns,
                    GridRows = wm.Settings.GridRows,
                    CustomSettings = new Dictionary<string, object>(wm.Settings.CustomSettings)
                }
            }).ToList();
        }

        /// <summary>Создать WorkModes из пресета (шаблона)</summary>
        private List<WorkMode> CreateWorkModesFromPreset(WorkModePreset preset)
        {
            return preset.WorkModes.Select(template => new WorkMode
            {
                Type = template.Type,
                Title = template.Title,
                Icon = template.Icon,
                Order = template.Order,
                IsCloseable = template.IsCloseable,
                IsActive = false,
                ModuleSlots = template.ModuleSlots.Select(slotTemplate => new ModuleSlot
                {
                    ModuleType = slotTemplate.ModuleType,
                    Position = new WorkModeGridPosition
                    {
                        Row = slotTemplate.Position.Row,
                        Column = slotTemplate.Position.Column
                    },
                    Size = new WorkModeGridSize(slotTemplate.Size.RowSpan, slotTemplate.Size.ColumnSpan),
                    MinWidth = slotTemplate.MinWidth,
                    MinHeight = slotTemplate.MinHeight,
                    IsVisible = slotTemplate.IsVisible,
                    IsResizable = true,
                    IsCloseable = slotTemplate.IsCloseable
                }).ToList()
            }).ToList();
        }
    }
}