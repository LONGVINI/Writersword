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
            // 1. Если есть локальная конфигурация в workspace.json → используем её
            if (fileStorage != null)
            {
                var localWorkModes = LoadLocalConfiguration(fileStorage);
                if (localWorkModes != null && localWorkModes.Count > 0)
                {
                    _logger.LogDebug("Using LOCAL configuration ({Count} modes)", localWorkModes.Count);
                    return localWorkModes;
                }
            }

            // 2. Если нет локальной → пытаемся загрузить глобальную конфигурацию из Settings.json
            var globalWorkModes = LoadGlobalConfiguration(projectType);
            if (globalWorkModes != null && globalWorkModes.Count > 0)
            {
                _logger.LogDebug("Using GLOBAL configuration ({Count} modes)", globalWorkModes.Count);
                return globalWorkModes;
            }

            // 3. Если нет глобальной → используем дефолтную (hardcoded) конфигурацию
            _logger.LogDebug("Using DEFAULT configuration");
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
                _logger.LogWarning("No WorkModes registered for project type: {ProjectType}", projectType);

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

                _logger.LogDebug("Loaded default config for: {Title}", workMode.Title);
            }

            _logger.LogDebug("Created DEFAULT configuration with {Count} modes", workModes.Count);
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
                        ModuleType = "TextEditor",
                        IsFloating = false,
                        TabOrder = 0,
                        IsActiveTab = true,
                        IsCloseable = false,
                        MinWidth = 400,
                        MinHeight = 300
                    }
                },
            };

            workModes.Add(editorMode);

            _logger.LogDebug("Created FALLBACK configuration");
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
                    _logger.LogDebug("No local config in ZIP");
                    return null;
                }

                _logger.LogDebug("Loaded local config: {Count} modes", config.WorkModes.Count);

                // Восстанавливаем метаданные модулей из дефолтной конфигурации
                RestoreModuleMetadata(config.WorkModes);

                // ВАЖНО: Восстанавливаем дефолтную конфигурацию для пустых WorkModes
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
        /// Если WorkMode не имеет модулей - восстанавливаем их из дефолтной конфигурации
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
        /// Заполняет IsCloseable, MinWidth, MinHeight, PreferredPosition, Category
        /// Вызывается после десериализации из workspace.json
        /// </summary>
        private void RestoreModuleMetadata(List<WorkMode> workModes)
        {
            var workModeRegistry = App.Services.GetRequiredService<WorkModeRegistry>();

            foreach (var workMode in workModes)
            {
                // Получаем зарегистрированный WorkMode по ID
                var registeredWorkMode = workModeRegistry.GetWorkMode(workMode.WorkModeId);

                if (registeredWorkMode == null)
                {
                    _logger.LogWarning("WorkMode not registered: {WorkModeId}", workMode.WorkModeId);
                    continue;
                }

                // Получаем дефолтную конфигурацию
                var defaultConfig = registeredWorkMode.GetDefaultConfig();

                // Восстанавливаем ModuleCategories (не сохраняются в JSON)
                workMode.ModuleCategories = new Dictionary<string, ModuleCategory>(defaultConfig.ModuleCategories);

                _logger.LogDebug("Restored {Count} module categories for WorkMode: {Title}",
                    workMode.ModuleCategories.Count, workMode.Title);

                // Восстанавливаем метаданные для каждого модуля
                foreach (var slot in workMode.ModuleSlots)
                {
                    // Определяем категорию
                    ModuleCategory category;

                    if (workMode.ModuleCategories.TryGetValue(slot.ModuleType, out var explicitCategory))
                    {
                        category = explicitCategory;
                    }
                    else
                    {
                        // Если не указан явно - по умолчанию Optional
                        category = ModuleCategory.Optional;
                    }

                    slot.Category = category;

                    // Определяем IsCloseable по категории
                    slot.IsCloseable = category != ModuleCategory.Required;

                    // Ищем дефолтный слот для восстановления размеров и позиции
                    var defaultSlot = defaultConfig.ModuleSlots
                        .FirstOrDefault(s => s.ModuleType == slot.ModuleType);

                    if (defaultSlot != null)
                    {
                        // Восстанавливаем метаданные (помеченные [JsonIgnore])
                        slot.MinWidth = defaultSlot.MinWidth;
                        slot.MinHeight = defaultSlot.MinHeight;
                        slot.PreferredPosition = defaultSlot.PreferredPosition;

                        _logger.LogDebug("Restored metadata for {ModuleId}: Category={Category}, IsCloseable={IsCloseable}",
                            slot.ModuleType, category, slot.IsCloseable);
                    }
                    else
                    {
                        // Если нет в дефолтной конфигурации - используем дефолты из ModuleSlot
                        _logger.LogDebug("No default config for module {ModuleId}, using ModuleSlot defaults. Category={Category}",
                            slot.ModuleType, category);
                    }
                }
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
                _logger.LogWarning("WorkMode not found: {WorkModeId}", workModeId);
                return true; // Если WorkMode не найден - разрешаем удаление
            }

            // Ищем модуль в слотах
            var moduleSlot = workMode.ModuleSlots.FirstOrDefault(ms => ms.ModuleType == moduleId);
            if (moduleSlot == null)
            {
                _logger.LogDebug("Module not found in WorkMode: {ModuleId}", moduleId);
                return true; // Модуль не найден в дефолтной конфигурации - можно удалить
            }

            // Если модуль IsCloseable=false - его нельзя удалить
            bool canRemove = moduleSlot.IsCloseable;
            _logger.LogDebug("CanRemoveModule({WorkModeId}, {ModuleId}): {CanRemove}", workModeId, moduleId, canRemove);
            return canRemove;
        }
    }
}