using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using Writersword.Core.Interfaces.Services;
using Writersword.Core.Models.WorkModes;
using Writersword.Src.Core.Interfaces.Services;
using Writersword.Src.Core.Interfaces.Services.Storage;
using Writersword.Src.Core.Interfaces.WorkModes;
using Writersword.Src.WorkModes.Common;

namespace Writersword.Src.Infrastructure.Services.WorkModes
{
    /// <summary>
    /// Сервис управления конфигурациями WorkModes
    /// Определяет приоритет: LOCAL (workspace.json в ZIP) → GLOBAL (Settings.json) → DEFAULT (hardcoded)
    /// </summary>
    public class WorkModeConfigurationService : IWorkModeConfigurationService
    {
        /// <summary>
        /// Загрузить конфигурацию для проекта
        /// Приоритет: LOCAL (workspace.json в ZIP) → GLOBAL (Settings.json) → DEFAULT (hardcoded)
        /// </summary>
        public List<WorkMode> LoadConfiguration(string projectType, IProjectFileStorage? fileStorage = null)
        {
            // 1. Если есть локальная конфигурация в workspace.json → используем её
            if (fileStorage != null)
            {
                var localWorkModes = LoadLocalConfiguration(fileStorage);
                if (localWorkModes != null && localWorkModes.Count > 0)
                {
                    Console.WriteLine($"[WorkModeConfigService] Using LOCAL configuration ({localWorkModes.Count} modes)");
                    return localWorkModes;
                }
            }

            // 2. Если нет локальной → пытаемся загрузить глобальную конфигурацию из Settings.json
            var globalWorkModes = LoadGlobalConfiguration(projectType);
            if (globalWorkModes != null && globalWorkModes.Count > 0)
            {
                Console.WriteLine($"[WorkModeConfigService] Using GLOBAL configuration ({globalWorkModes.Count} modes)");
                return globalWorkModes;
            }

            // 3. Если нет глобальной → используем дефолтную (hardcoded) конфигурацию
            Console.WriteLine($"[WorkModeConfigService] Using DEFAULT configuration");
            return LoadDefaultConfiguration(projectType);
        }

        /// <summary>
        /// Загрузить дефолтную конфигурацию из реестра WorkMode (hardcoded)
        /// Использует GetDefaultConfig() каждого зарегистрированного WorkMode
        /// </summary>
        public List<WorkMode> LoadDefaultConfiguration(string projectType)
        {
            var workModes = new List<WorkMode>();

            // Получаем реестр WorkMode
            var workModeRegistry = App.Services.GetRequiredService<WorkModeRegistry>();

            // Получаем все зарегистрированные WorkMode для этого типа проекта
            var registeredWorkModes = workModeRegistry.GetWorkModesForProjectType(projectType);

            if (registeredWorkModes == null || registeredWorkModes.Count == 0)
            {
                Console.WriteLine($"[WorkModeConfigService] No WorkModes registered for project type: {projectType}");

                // FALLBACK: Хардкод для Editor (если реестр пуст)
                return CreateFallbackEditorMode();
            }

            // Создаём WorkMode из каждого зарегистрированного
            foreach (var registeredWM in registeredWorkModes)
            {
                // Получаем DEFAULT конфигурацию - теперь это уже готовый WorkMode!
                var workMode = registeredWM.GetDefaultConfig();

                // Устанавливаем активность - первый активен
                workMode.IsActive = registeredWM.Order == 0;

                workModes.Add(workMode);

                Console.WriteLine($"[WorkModeConfigService] Loaded default config for: {workMode.Title}");
            }

            Console.WriteLine($"[WorkModeConfigService] Created DEFAULT configuration with {workModes.Count} modes");
            return workModes;
        }

        /// <summary>
        /// Fallback конфигурация если реестр пуст
        /// Создаёт минимальный Editor режим с TextEditor модулем
        /// </summary>
        private List<WorkMode> CreateFallbackEditorMode()
        {
            var workModes = new List<WorkMode>();

            var editorMode = new WorkMode
            {
                WorkModeId = "editor",
                Title = "Editor",
                Icon = "✍️",
                Order = 0,
                IsActive = true,
                IsCloseable = false,
                ModuleSlots = new List<ModuleSlot>
                {
                    new ModuleSlot
                    {
                        ModuleId = "TextEditor",
                        ContainerId = "Main",
                        IsFloating = false,
                        TabOrder = 0,
                        IsActiveTab = true,
                        IsCloseable = false,
                        MinWidth = 400,
                        MinHeight = 300
                    }
                },
                Containers = new List<SplitContainer>
                {
                    new SplitContainer
                    {
                        Id = "Main",
                        Proportion = 1.0,
                        Orientation = null,
                        Children = null
                    }
                }
            };

            workModes.Add(editorMode);

            Console.WriteLine($"[WorkModeConfigService] Created FALLBACK configuration");
            return workModes;
        }

        /// <summary>
        /// Загрузить глобальную конфигурацию из Settings.json → workspaceConfigs[projectType]
        /// Возвращает null если конфигурация не найдена
        /// </summary>
        private List<WorkMode>? LoadGlobalConfiguration(string projectType)
        {
            try
            {
                var settingsService = App.Services.GetRequiredService<ISettingsService>();
                var workspaceConfig = settingsService.GetWorkspaceConfig(projectType);

                if (workspaceConfig == null)
                {
                    Console.WriteLine($"[WorkModeConfigService] No global config for: {projectType}");
                    return null;
                }

                Console.WriteLine($"[WorkModeConfigService] Loading global config: {workspaceConfig.WorkModes.Count} modes");
                return workspaceConfig.WorkModes;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WorkModeConfigService] Error loading global config: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Загрузить локальную конфигурацию из workspace.json в ZIP проекта
        /// Возвращает null если файл не найден или ошибка чтения
        /// </summary>
        private List<WorkMode>? LoadLocalConfiguration(IProjectFileStorage fileStorage)
        {
            try
            {
                var workspaceConfigService = App.Services.GetRequiredService<IWorkspaceConfigService>();
                var config = workspaceConfigService.LoadFromZip(fileStorage);

                if (config == null)
                {
                    Console.WriteLine($"[WorkModeConfigService] No local config in ZIP");
                    return null;
                }

                Console.WriteLine($"[WorkModeConfigService] Loaded local config: {config.WorkModes.Count} modes");
                return config.WorkModes;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WorkModeConfigService] Error loading local config: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Проверить можно ли удалить модуль из WorkMode
        /// Проверяет дефолтную конфигурацию - если модуль Required (IsCloseable=false), то нельзя удалить
        /// </summary>
        public bool CanRemoveModule(string projectType, string workModeId, string moduleId)
        {
            // Получаем дефолтную конфигурацию для проверки
            var defaultConfig = LoadDefaultConfiguration(projectType);
            var workMode = defaultConfig.FirstOrDefault(wm => wm.WorkModeId == workModeId);

            if (workMode == null)
            {
                Console.WriteLine($"[WorkModeConfigService] WorkMode not found: {workModeId}");
                return true; // Если WorkMode не найден - разрешаем удаление
            }

            // Ищем модуль в слотах
            var moduleSlot = workMode.ModuleSlots.FirstOrDefault(ms => ms.ModuleId == moduleId);
            if (moduleSlot == null)
            {
                Console.WriteLine($"[WorkModeConfigService] Module not found in WorkMode: {moduleId}");
                return true; // Модуль не найден в дефолтной конфигурации - можно удалить
            }

            // Если модуль IsCloseable=false - его нельзя удалить
            bool canRemove = moduleSlot.IsCloseable;
            Console.WriteLine($"[WorkModeConfigService] CanRemoveModule({workModeId}, {moduleId}): {canRemove}");
            return canRemove;
        }
    }
}