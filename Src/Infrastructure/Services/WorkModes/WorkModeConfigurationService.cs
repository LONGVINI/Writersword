using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using Writersword.Core.Enums;
using Writersword.Core.Models.WorkModes;
using Writersword.Src.Core.Interfaces.WorkModes;
using Writersword.Src.WorkModes.Common;

namespace Writersword.Src.Infrastructure.Services.WorkModes
{
    /// <summary>
    /// Сервис управления конфигурациями WorkModes
    /// Определяет приоритет: Проект → Глобальная → Дефолтная
    /// </summary>
    public class WorkModeConfigurationService : IWorkModeConfigurationService
    {
        /// <summary>
        /// Загрузить конфигурацию для проекта
        /// Приоритет: Проект → Глобальная → Дефолтная
        /// </summary>
        public List<WorkMode> LoadConfiguration(string projectType, List<WorkMode>? projectWorkModes)
        {
            // 1. Если в проекте есть сохранённые WorkModes → используем их
            if (projectWorkModes != null && projectWorkModes.Count > 0)
            {
                Console.WriteLine($"[WorkModeConfigService] Using PROJECT configuration ({projectWorkModes.Count} modes)");
                return CloneWorkModes(projectWorkModes);
            }

            // 2. Если нет → пытаемся загрузить глобальную конфигурацию
            var globalWorkModes = LoadGlobalConfiguration(projectType);
            if (globalWorkModes != null && globalWorkModes.Count > 0)
            {
                Console.WriteLine($"[WorkModeConfigService] Using GLOBAL configuration ({globalWorkModes.Count} modes)");
                return CloneWorkModes(globalWorkModes);
            }

            // 3. Если нет → используем дефолтную конфигурацию
            Console.WriteLine($"[WorkModeConfigService] Using DEFAULT configuration");
            return LoadDefaultConfiguration(projectType);
        }

        /// <summary>
        /// Загрузить дефолтную конфигурацию из реестра WorkMode
        /// Использует GetDefaultConfig() каждого WorkMode
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
                // Получаем DEFAULT конфигурацию из WorkMode
                var defaultConfig = registeredWM.GetDefaultConfig();

                // Создаём экземпляр WorkMode
                var workMode = new WorkMode
                {
                    WorkModeId = registeredWM.Id,
                    Title = registeredWM.DisplayName,
                    Icon = registeredWM.Icon,
                    Order = defaultConfig.Order,
                    IsActive = defaultConfig.Order == 0, // Первый активен
                    IsCloseable = registeredWM.IsCloseable
                };

                // КОПИРУЕМ ModuleSlots из конфига
                foreach (var slotConfig in defaultConfig.ModuleSlots)
                {
                    workMode.ModuleSlots.Add(new ModuleSlot
                    {
                        ModuleId = slotConfig.ModuleId,
                        IsVisible = slotConfig.IsVisible,
                        IsCloseable = slotConfig.Category != ModuleCategory.Required,
                        MinWidth = slotConfig.MinWidth,
                        MinHeight = slotConfig.MinHeight,
                        PreferredPosition = slotConfig.PreferredPosition ?? PreferredDockPosition.RightAsTab
                    });
                }

                // КРИТИЧЕСКИ ВАЖНО: Сохраняем DockLayout в CustomSettings!
                if (defaultConfig.DockLayout != null)
                {
                    workMode.Settings.CustomSettings["DockLayout"] = defaultConfig.DockLayout;
                    Console.WriteLine($"[WorkModeConfigService] DockLayout saved for: {workMode.Title}");
                }

                workModes.Add(workMode);
            }

            Console.WriteLine($"[WorkModeConfigService] Created DEFAULT configuration with {workModes.Count} modes");
            return workModes;
        }

        /// <summary>
        /// Fallback конфигурация если реестр пуст
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
                IsCloseable = false
            };

            editorMode.ModuleSlots.Add(new ModuleSlot
            {
                ModuleId = "TextEditor",
                IsVisible = true,
                IsCloseable = false,
                MinWidth = 400,
                MinHeight = 300,
                PreferredPosition = PreferredDockPosition.Left
            });

            workModes.Add(editorMode);

            Console.WriteLine($"[WorkModeConfigService] Created FALLBACK configuration");
            return workModes;
        }

        /// <summary>Загрузить глобальную конфигурацию (из settings.json)</summary>
        private List<WorkMode>? LoadGlobalConfiguration(string projectType)
        {
            // TODO: Реализовать загрузку из settings.json
            // Пока возвращаем null (нет глобальной конфигурации)
            return null;
        }

        /// <summary>Сохранить конфигурацию глобально</summary>
        public void SaveGlobalConfiguration(string projectType, List<WorkMode> workModes)
        {
            // TODO: Реализовать сохранение в settings.json
            Console.WriteLine($"[WorkModeConfigService] SaveGlobalConfiguration: {projectType} ({workModes.Count} modes)");
        }

        /// <summary>Удалить глобальную конфигурацию</summary>
        public void DeleteGlobalConfiguration(string projectType)
        {
            // TODO: Реализовать удаление из settings.json
            Console.WriteLine($"[WorkModeConfigService] DeleteGlobalConfiguration: {projectType}");
        }

        /// <summary>Проверить можно ли удалить модуль из режима</summary>
        public bool CanRemoveModule(string workModeId, string moduleId)
        {
            // Получаем дефолтную конфигурацию для проверки
            var defaultConfig = LoadDefaultConfiguration("novel");
            var workMode = defaultConfig.FirstOrDefault(wm => wm.WorkModeId == workModeId);

            if (workMode == null) return true;

            // Ищем модуль в обязательных
            var moduleSlot = workMode.ModuleSlots.FirstOrDefault(ms => ms.ModuleId == moduleId);
            if (moduleSlot == null) return true;

            // Если модуль не closeable - его нельзя удалить
            return moduleSlot.IsCloseable;
        }

        /// <summary>Получить обязательные модули для режима</summary>
        public List<string> GetRequiredModules(string workModeId)
        {
            var defaultConfig = LoadDefaultConfiguration("novel");
            var workMode = defaultConfig.FirstOrDefault(wm => wm.WorkModeId == workModeId);

            if (workMode == null) return new List<string>();

            // Возвращаем модули которые нельзя закрыть
            return workMode.ModuleSlots
                .Where(ms => !ms.IsCloseable)
                .Select(ms => ms.ModuleId)
                .ToList();
        }

        /// <summary>Клонировать WorkModes (глубокое копирование)</summary>
        public List<WorkMode> CloneWorkModes(List<WorkMode> source)
        {
            var cloned = new List<WorkMode>();

            foreach (var wm in source)
            {
                var newWm = new WorkMode
                {
                    WorkModeId = wm.WorkModeId,
                    Title = wm.Title,
                    Icon = wm.Icon,
                    Order = wm.Order,
                    IsActive = wm.IsActive,
                    IsCloseable = wm.IsCloseable,
                    LastAccessedAt = wm.LastAccessedAt
                };

                // Клонируем слоты модулей
                foreach (var slot in wm.ModuleSlots)
                {
                    newWm.ModuleSlots.Add(new ModuleSlot
                    {
                        ModuleId = slot.ModuleId,
                        IsVisible = slot.IsVisible,
                        IsCloseable = slot.IsCloseable,
                        MinWidth = slot.MinWidth,
                        MinHeight = slot.MinHeight,
                        PreferredPosition = slot.PreferredPosition
                    });
                }

                cloned.Add(newWm);
            }

            return cloned;
        }
    }
}