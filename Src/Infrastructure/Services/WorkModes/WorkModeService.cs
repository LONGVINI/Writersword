using System;
using System.Collections.Generic;
using System.Linq;
using Writersword.Core.Enums;
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
        private List<WorkMode> _workModes = new();
        private string _currentProjectType = "";

        public WorkModeService(IWorkModeConfigurationService configService)
        {
            _configService = configService;
        }

        /// <summary>Инициализировать WorkModes для проекта</summary>
        public List<WorkMode> InitializeWorkModes(string projectType, List<WorkMode>? savedWorkModes = null)
        {
            _currentProjectType = projectType;

            // Если переданы сохранённые WorkModes - загружаем ВСЕ
            if (savedWorkModes != null && savedWorkModes.Count > 0)
            {
                _workModes = savedWorkModes;

                var activeWorkMode = _workModes.FirstOrDefault(wm => wm.IsActive);
                if (activeWorkMode == null)
                {
                    _workModes[0].IsActive = true;
                    Console.WriteLine($"[WorkModeService] No active WorkMode, activated first: {_workModes[0].Title}");
                }
                else
                {
                    Console.WriteLine($"[WorkModeService] Restored active WorkMode: {activeWorkMode.Title}");
                }

                Console.WriteLine($"[WorkModeService] Loaded {_workModes.Count} WorkModes from saved data");
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

                _workModes = allWorkModes;

                // Проверяем что есть активный
                var activeWorkMode = _workModes.FirstOrDefault(wm => wm.IsActive);
                if (activeWorkMode == null)
                {
                    _workModes[0].IsActive = true;
                    Console.WriteLine($"[WorkModeService] Activated first WorkMode: {_workModes[0].Title}");
                }

                Console.WriteLine($"[WorkModeService] Loaded {_workModes.Count} WorkModes from config");
            }

            Console.WriteLine($"[WorkModeService] Initialized with {_workModes.Count} WorkModes");
            return _workModes;
        }

        /// <summary>Добавить новый режим работы</summary>
        public WorkMode AddWorkMode(string workModeId, string title, string icon)
        {
            // Получаем дефолтную конфигурацию для этого типа WorkMode
            var defaultConfig = _configService.LoadDefaultConfiguration(_currentProjectType);
            var defaultWorkMode = defaultConfig.FirstOrDefault(wm => wm.WorkModeId == workModeId);

            if (defaultWorkMode != null)
            {
                // Используем готовый WorkMode из дефолтной конфигурации
                defaultWorkMode.IsActive = false;
                _workModes.Add(defaultWorkMode);
                Console.WriteLine($"[WorkModeService] Added WorkMode from default: {defaultWorkMode.Title}");
                return defaultWorkMode;
            }

            // Если не нашли в дефолтах - создаём минимальный
            var workMode = new WorkMode
            {
                WorkModeId = workModeId,
                Title = title,
                Icon = icon,
                Order = _workModes.Count,
                IsCloseable = workModeId != "editor",
                IsActive = false
            };

            _workModes.Add(workMode);
            Console.WriteLine($"[WorkModeService] Added minimal WorkMode: {title}");

            return workMode;
        }

        /// <summary>Удалить режим работы</summary>
        public bool RemoveWorkMode(WorkMode workMode)
        {
            if (!workMode.IsCloseable)
            {
                Console.WriteLine($"[WorkModeService] Cannot remove WorkMode: {workMode.Title} (not closeable)");
                return false;
            }

            var removed = _workModes.Remove(workMode);
            if (removed)
            {
                Console.WriteLine($"[WorkModeService] Removed WorkMode: {workMode.Title}");

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
                PreferredPosition = PreferredDockPosition.RightAsTab
            };

            workMode.ModuleSlots.Add(slot);
            Console.WriteLine($"[WorkModeService] Added module {moduleId} to {workMode.Title}");

            return slot;
        }

        /// <summary>Удалить модуль из режима</summary>
        public bool RemoveModuleFromWorkMode(WorkMode workMode, ModuleSlot moduleSlot)
        {
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
            foreach (var wm in _workModes)
            {
                wm.IsActive = false;
            }

            workMode.IsActive = true;

            Console.WriteLine($"[WorkModeService] Active WorkMode: {workMode.Title}");
        }
    }
}