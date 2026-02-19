using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        private readonly ILogger<WorkModeService> _logger;
        private readonly IWorkModeConfigurationService _configService;
        private List<WorkMode> _workModes = new();
        private string _currentProjectType = "";

        public WorkModeService(IWorkModeConfigurationService configService)
        {
            _logger = App.Services.GetService<ILogger<WorkModeService>>()!;
            _configService = configService;
        }

        /// <summary>Инициализировать WorkModes для проекта</summary>
        public List<WorkMode> InitializeWorkModes(string projectType, List<WorkMode>? savedWorkModes = null)
        {
            _currentProjectType = projectType;

            if (savedWorkModes != null && savedWorkModes.Count > 0)
            {
                _workModes = savedWorkModes;

                var activeWorkMode = _workModes.FirstOrDefault(wm => wm.IsActive);
                if (activeWorkMode == null)
                {
                    _workModes[0].IsActive = true;
                    _logger.LogDebug("No active WorkMode, activated first: {Title}", _workModes[0].Title);
                }
                else
                {
                    _logger.LogDebug("Restored active WorkMode: {Title}", activeWorkMode.Title);
                }

                _logger.LogDebug("Loaded {Count} WorkModes from saved data", _workModes.Count);
            }
            else
            {
                var allWorkModes = _configService.LoadConfiguration(projectType, fileStorage: null);

                if (allWorkModes.Count == 0)
                {
                    _logger.LogError("No WorkModes available");
                    return new List<WorkMode>();
                }

                _workModes = allWorkModes;

                var activeWorkMode = _workModes.FirstOrDefault(wm => wm.IsActive);
                if (activeWorkMode == null)
                {
                    _workModes[0].IsActive = true;
                    _logger.LogDebug("Activated first WorkMode: {Title}", _workModes[0].Title);
                }

                _logger.LogDebug("Loaded {Count} WorkModes from config", _workModes.Count);
            }

            _logger.LogDebug("Initialized with {Count} WorkModes", _workModes.Count);
            return _workModes;
        }

        /// <summary>Добавить новый режим работы</summary>
        public WorkMode AddWorkMode(string workModeId, string title, string icon)
        {
            var defaultConfig = _configService.LoadDefaultConfiguration(_currentProjectType);
            var defaultWorkMode = defaultConfig.FirstOrDefault(wm => wm.WorkModeId == workModeId);

            if (defaultWorkMode != null)
            {
                defaultWorkMode.IsActive = false;
                _workModes.Add(defaultWorkMode);
                _logger.LogDebug("Added WorkMode from default: {Title}", defaultWorkMode.Title);
                return defaultWorkMode;
            }

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
            _logger.LogDebug("Added minimal WorkMode: {Title}", title);

            return workMode;
        }

        /// <summary>Удалить режим работы</summary>
        public bool RemoveWorkMode(WorkMode workMode)
        {
            if (!workMode.IsCloseable)
            {
                _logger.LogDebug("Cannot remove WorkMode: {Title} (not closeable)", workMode.Title);
                return false;
            }

            var removed = _workModes.Remove(workMode);
            if (removed)
            {
                _logger.LogDebug("Removed WorkMode: {Title}", workMode.Title);

                if (workMode.IsActive && _workModes.Count > 0)
                {
                    SetActiveWorkMode(_workModes[0]);
                }
            }

            return removed;
        }

        /// <summary>Добавить модуль в режим</summary>
        public ModuleSlot AddModuleToWorkMode(WorkMode workMode, string moduleType)
        {
            var slot = new ModuleSlot
            {
                ModuleType = moduleType,
                PreferredPosition = PreferredDockPosition.RightAsTab
            };

            workMode.ModuleSlots.Add(slot);
            _logger.LogDebug("Added module {moduleType} to {WorkModeTitle}", moduleType, workMode.Title);

            return slot;
        }

        /// <summary>Удалить модуль из режима</summary>
        public bool RemoveModuleFromWorkMode(WorkMode workMode, ModuleSlot moduleSlot)
        {
            if (!_configService.CanRemoveModule(_currentProjectType, workMode.WorkModeId, moduleSlot.ModuleType))
            {
                _logger.LogDebug("Cannot remove module {moduleType} (required)", moduleSlot.ModuleType);
                return false;
            }

            var removed = workMode.ModuleSlots.Remove(moduleSlot);
            if (removed)
            {
                _logger.LogDebug("Removed module {moduleType} from {WorkModeTitle}", moduleSlot.ModuleType, workMode.Title);
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

            _logger.LogDebug("Active WorkMode: {Title}", workMode.Title);
        }
    }
}