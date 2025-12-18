using System;
using System.Collections.Generic;
using System.Linq;
using Writersword.Core.Enums;
using Writersword.Core.Models.WorkModes;
using Writersword.Core.Services.Interfaces;

namespace Writersword.Core.Services.WorkModes
{
    /// <summary>
    /// Главный сервис для работы с WorkModes
    /// Управляет режимами работы в текущем проекте
    /// </summary>
    public class WorkModeService : IWorkModeService
    {
        private readonly IWorkModeConfigurationService _configService;
        private List<WorkMode> _workModes = new();

        public WorkModeService(IWorkModeConfigurationService configService)
        {
            _configService = configService;
        }

        /// <summary>Инициализировать WorkModes для проекта</summary>
        public List<WorkMode> InitializeWorkModes(ProjectType projectType, List<WorkMode>? existingWorkModes)
        {
            _workModes = _configService.LoadConfiguration(projectType, existingWorkModes);

            // Активируем первый режим по умолчанию
            if (_workModes.Count > 0 && !_workModes.Any(wm => wm.IsActive))
            {
                _workModes[0].IsActive = true;
            }

            Console.WriteLine($"[WorkModeService] Initialized {_workModes.Count} WorkModes");
            return _workModes;
        }

        /// <summary>Добавить новый режим работы</summary>
        public WorkMode AddWorkMode(WorkModeType type, string title, string icon)
        {
            var workMode = new WorkMode
            {
                Type = type,
                Title = title,
                Icon = icon,
                Order = _workModes.Count,
                IsCloseable = type != WorkModeType.Editor, // Editor нельзя закрыть
                IsActive = false
            };

            // Добавляем обязательные модули для этого режима
            var requiredModules = _configService.GetRequiredModules(type);
            foreach (var moduleType in requiredModules)
            {
                workMode.ModuleSlots.Add(new ModuleSlot
                {
                    ModuleType = moduleType,
                    Position = new WorkModeGridPosition { Row = 0, Column = 0 },
                    Size = new WorkModeGridSize(2, 4),
                    IsVisible = true
                });
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
        public ModuleSlot AddModuleToWorkMode(WorkMode workMode, ModuleType moduleType)
        {
            var slot = new ModuleSlot
            {
                ModuleType = moduleType,
                Position = new WorkModeGridPosition { Row = 0, Column = 0 },
                Size = new WorkModeGridSize(1, 1),
                IsVisible = true
            };

            workMode.ModuleSlots.Add(slot);
            Console.WriteLine($"[WorkModeService] Added module {moduleType} to {workMode.Title}");

            return slot;
        }

        /// <summary>Удалить модуль из режима</summary>
        public bool RemoveModuleFromWorkMode(WorkMode workMode, ModuleSlot moduleSlot)
        {
            // Проверяем можно ли удалить этот модуль
            if (!_configService.CanRemoveModule(workMode.Type, moduleSlot.ModuleType))
            {
                Console.WriteLine($"[WorkModeService] Cannot remove module {moduleSlot.ModuleType} (required)");
                return false;
            }

            var removed = workMode.ModuleSlots.Remove(moduleSlot);
            if (removed)
            {
                Console.WriteLine($"[WorkModeService] Removed module {moduleSlot.ModuleType} from {workMode.Title}");
            }

            return removed;
        }

        /// <summary>Переместить модуль в другую позицию</summary>
        public void MoveModule(ModuleSlot moduleSlot, WorkModeGridPosition newPosition)
        {
            moduleSlot.Position = newPosition;
            Console.WriteLine($"[WorkModeService] Moved module to Row={newPosition.Row}, Col={newPosition.Column}");
        }

        /// <summary>Изменить размер модуля</summary>
        public void ResizeModule(ModuleSlot moduleSlot, WorkModeGridSize newSize)
        {
            if (!moduleSlot.IsResizable)
            {
                Console.WriteLine($"[WorkModeService] Cannot resize module (not resizable)");
                return;
            }

            moduleSlot.Size = newSize;
            Console.WriteLine($"[WorkModeService] Resized module to {newSize.RowSpan}x{newSize.ColumnSpan}");
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
            workMode.LastAccessedAt = DateTime.Now;

            Console.WriteLine($"[WorkModeService] Active WorkMode: {workMode.Title}");
        }
    }
}