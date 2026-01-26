using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Writersword.Core.Enums;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Interfaces.Services;
using Writersword.Core.Models.WorkModes;
using Writersword.Src.Core.Interfaces.Services;
using Writersword.Src.Core.Interfaces.WorkModes;

namespace Writersword.Src.Infrastructure.Services.WorkModes
{
    /// <summary>
    /// Главный сервис для работы с WorkModes
    /// Управляет режимами работы в текущем проекте
    /// </summary>
    public class WorkModeService : IWorkModeService
    {
        private readonly IWorkModeConfigurationService _configService;
        private readonly IModuleLifecycleService _lifecycleService;
        private List<WorkMode> _workModes = new();
        private string _currentProjectType = "";

        public WorkModeService(
            IWorkModeConfigurationService configService,
            IModuleLifecycleService lifecycleService)
        {
            _configService = configService;
            _lifecycleService = lifecycleService;
        }

        /// <summary>Инициализировать WorkModes для проекта</summary>
        public List<WorkMode> InitializeWorkModes(string projectType, List<WorkMode>? savedWorkModes = null)
        {
            _currentProjectType = projectType;

            // Если переданы сохранённые WorkModes - загружаем ТОЛЬКО активный
            if (savedWorkModes != null && savedWorkModes.Count > 0)
            {
                var activeWorkMode = savedWorkModes.FirstOrDefault(wm => wm.IsActive);

                if (activeWorkMode == null)
                {
                    Console.WriteLine("[WorkModeService] ERROR: No active WorkMode in saved data!");
                    return new List<WorkMode>();
                }

                _workModes = new List<WorkMode> { activeWorkMode };
                Console.WriteLine($"[WorkModeService] Loaded ONLY active WorkMode: {activeWorkMode.Title}");
            }
            else
            {
                // Нет сохранённых - вызываем LoadConfiguration
                // Он сделает: LOCAL → GLOBAL → DEFAULT
                var allWorkModes = _configService.LoadConfiguration(projectType, fileStorage: null);

                if (allWorkModes.Count == 0)
                {
                    Console.WriteLine("[WorkModeService] ERROR: No WorkModes available!");
                    return new List<WorkMode>();
                }

                // Берём ТОЛЬКО активный
                var activeWorkMode = allWorkModes.FirstOrDefault(wm => wm.IsActive);
                if (activeWorkMode == null)
                {
                    // Активируем первый
                    allWorkModes[0].IsActive = true;
                    activeWorkMode = allWorkModes[0];
                }

                _workModes = new List<WorkMode> { activeWorkMode };
                Console.WriteLine($"[WorkModeService] Loaded active WorkMode from config: {activeWorkMode.Title}");
            }

            Console.WriteLine($"[WorkModeService] Initialized with 1 WorkMode");
            return _workModes;
        }

        /// <summary>Добавить новый режим работы</summary>
        public WorkMode AddWorkMode(string workModeId, string title, string icon)
        {
            var workMode = new WorkMode
            {
                WorkModeId = workModeId,
                Title = title,
                Icon = icon,
                Order = _workModes.Count,
                IsCloseable = workModeId != "editor", // Editor нельзя закрыть
                IsActive = false
            };

            // Получаем дефолтную конфигурацию для этого типа WorkMode
            var defaultConfig = _configService.LoadDefaultConfiguration(_currentProjectType);
            var defaultWorkMode = defaultConfig.FirstOrDefault(wm => wm.WorkModeId == workModeId);

            if (defaultWorkMode != null)
            {
                // Копируем слоты модулей из дефолтной конфигурации
                foreach (var slot in defaultWorkMode.ModuleSlots)
                {
                    workMode.ModuleSlots.Add(new ModuleSlot
                    {
                        ModuleId = slot.ModuleId,
                        IsVisible = slot.IsVisible,
                        IsCloseable = slot.IsCloseable,
                        MinWidth = slot.MinWidth,
                        MinHeight = slot.MinHeight,
                        PreferredPosition = slot.PreferredPosition
                    });
                }
            }

            _workModes.Add(workMode);
            Console.WriteLine($"[WorkModeService] Added WorkMode: {title}");

            return workMode;
        }

        /// <summary>Удалить режим работы</summary>
        public bool RemoveWorkMode(WorkMode workMode)
        {
            // Нельзя удалить если режим нельзя закрыть
            if (!workMode.IsCloseable)
            {
                Console.WriteLine($"[WorkModeService] Cannot remove WorkMode: {workMode.Title} (not closeable)");
                return false;
            }

            var removed = _workModes.Remove(workMode);
            if (removed)
            {
                Console.WriteLine($"[WorkModeService] Removed WorkMode: {workMode.Title}");

                // Если это был активный режим - активируем первый
                if (workMode.IsActive && _workModes.Count > 0)
                {
                    SetActiveWorkMode(_workModes[0]);
                }
            }

            return removed;
        }

        /// <summary>Добавить модуль в режим</summary>
        public ModuleSlot AddModuleToWorkMode(WorkMode workMode, string moduleId)
        {
            var slot = new ModuleSlot
            {
                ModuleId = moduleId,
                IsVisible = true,
                PreferredPosition = PreferredDockPosition.RightAsTab
            };

            workMode.ModuleSlots.Add(slot);
            Console.WriteLine($"[WorkModeService] Added module {moduleId} to {workMode.Title}");

            return slot;
        }

        /// <summary>Удалить модуль из режима</summary>
        public bool RemoveModuleFromWorkMode(WorkMode workMode, ModuleSlot moduleSlot)
        {
            // Проверяем можно ли удалить этот модуль
            if (!_configService.CanRemoveModule(_currentProjectType, workMode.WorkModeId, moduleSlot.ModuleId))
            {
                Console.WriteLine($"[WorkModeService] Cannot remove module {moduleSlot.ModuleId} (required)");
                return false;
            }

            var removed = workMode.ModuleSlots.Remove(moduleSlot);
            if (removed)
            {
                Console.WriteLine($"[WorkModeService] Removed module {moduleSlot.ModuleId} from {workMode.Title}");
            }

            return removed;
        }

        /// <summary>Показать/скрыть модуль</summary>
        public void ToggleModuleVisibility(ModuleSlot moduleSlot)
        {
            moduleSlot.IsVisible = !moduleSlot.IsVisible;
            Console.WriteLine($"[WorkModeService] Module visibility: {moduleSlot.IsVisible}");
        }

        /// <summary>Получить все WorkModes</summary>
        public List<WorkMode> GetAllWorkModes()
        {
            return _workModes;
        }

        /// <summary>Получить активный WorkMode</summary>
        public WorkMode? GetActiveWorkMode()
        {
            return _workModes.FirstOrDefault(wm => wm.IsActive);
        }

        /// <summary>Установить активный WorkMode</summary>
        public void SetActiveWorkMode(WorkMode workMode)
        {
            // Деактивируем все
            foreach (var wm in _workModes)
            {
                wm.IsActive = false;
            }

            // Активируем выбранный
            workMode.IsActive = true;

            Console.WriteLine($"[WorkModeService] Active WorkMode: {workMode.Title}");
        }

        /// <summary>
        /// Переключиться на другой WorkMode
        /// Закрывает модули старого WorkMode и открывает новые
        /// </summary>
        /// <param name="newWorkMode">Новый режим работы</param>
        /// <param name="activeModules">Активные модули</param>
        /// <param name="projectPath">Путь к проекту</param>
        /// <param name="projectId">GUID проекта</param>
        public async Task SwitchWorkModeAsync(WorkMode newWorkMode, IEnumerable<IModule> activeModules, string projectPath, string projectId)
        {
            var oldWorkMode = GetActiveWorkMode();

            if (oldWorkMode == newWorkMode)
            {
                Console.WriteLine("[WorkModeService] Already in this WorkMode");
                return;
            }

            Console.WriteLine($"[WorkModeService] Switching: {oldWorkMode?.Title} → {newWorkMode.Title}");

            // 1. Закрываем все модули старого WorkMode (с сохранением в кеш)
            foreach (var module in activeModules)
            {
                await _lifecycleService.CloseModuleAsync(module, projectPath, projectId);
            }

            // 2. Активируем новый WorkMode
            SetActiveWorkMode(newWorkMode);

            Console.WriteLine($"[WorkModeService] Switched to: {newWorkMode.Title}");
        }
    }
}