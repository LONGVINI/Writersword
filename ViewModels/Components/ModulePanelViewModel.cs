using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using Writersword.Core.Models.WorkModes;
using Writersword.Modules.Common;

namespace Writersword.ViewModels.Components
{
    /// <summary>
    /// ViewModel для панели модулей
    /// Показывает список доступных модулей для текущего WorkMode
    /// Позволяет включать/выключать модули
    /// </summary>
    public class ModulePanelViewModel : ViewModelBase
    {
        private readonly ILogger<ModulePanelViewModel> _logger;
        private readonly ModuleFactory _moduleFactory;
        private List<ModuleItemViewModel> _availableModules = new();
        private WorkMode? _currentWorkMode;

        /// <summary>Список доступных модулей для добавления</summary>
        public List<ModuleItemViewModel> AvailableModules
        {
            get => _availableModules;
            set => this.RaiseAndSetIfChanged(ref _availableModules, value);
        }

        /// <summary>Команда переключения видимости модуля</summary>
        public ReactiveCommand<ModuleItemViewModel, Unit> ToggleModuleCommand { get; }

        /// <summary>Функция добавления модуля (передаётся из MainWindowViewModel)</summary>
        private Action<string>? _onModuleAdded;

        /// <summary>Функция удаления модуля (передаётся из MainWindowViewModel)</summary>
        private Action<string>? _onModuleRemoved;

        public ModulePanelViewModel(ModuleFactory moduleFactory)
        {
            _logger = App.Services.GetService<ILogger<ModulePanelViewModel>>()!;
            _moduleFactory = moduleFactory;

            ToggleModuleCommand = ReactiveCommand.Create<ModuleItemViewModel>(ToggleModule);

            _logger.LogDebug("Initialized");
        }

        /// <summary>
        /// Установить обработчики добавления/удаления модулей
        /// Вызывается из MainWindowViewModel после создания
        /// </summary>
        public void SetModuleHandlers(Action<string> onModuleAdded, Action<string> onModuleRemoved)
        {
            _onModuleAdded = onModuleAdded;
            _onModuleRemoved = onModuleRemoved;
            _logger.LogDebug("Module handlers set");
        }

        /// <summary>
        /// Загрузить список модулей для текущего WorkMode
        /// Вызывается из MainWindowViewModel при смене WorkMode
        /// </summary>
        public void LoadModulesForWorkMode(WorkMode workMode)
        {
            _currentWorkMode = workMode;

            var allModules = _moduleFactory.GetAllModuleMetadata();

            var moduleItems = new List<ModuleItemViewModel>();

            foreach (var metadata in allModules)
            {
                var moduleSlot = workMode.ModuleSlots.FirstOrDefault(ms => ms.ModuleId == metadata.ModuleId);

                var item = new ModuleItemViewModel
                {
                    ModuleId = metadata.ModuleId,
                    DisplayName = metadata.DisplayName,
                    Description = metadata.Description,
                    IsActive = moduleSlot != null,
                    IsRequired = moduleSlot != null && !moduleSlot.IsCloseable
                };

                moduleItems.Add(item);
            }

            AvailableModules = moduleItems
                .OrderByDescending(m => m.IsActive)
                .ThenBy(m => m.DisplayName)
                .ToList();

            _logger.LogDebug("Loaded {Count} modules for WorkMode: {WorkModeTitle}", AvailableModules.Count, workMode.Title);
        }

        /// <summary>
        /// Очистить панель модулей (когда нет активного WorkMode)
        /// </summary>
        public void Clear()
        {
            _currentWorkMode = null;
            AvailableModules = new List<ModuleItemViewModel>();
            _logger.LogDebug("Cleared all modules");
        }

        /// <summary>
        /// Открыть модуль (публичный метод для вызова из меню)
        /// Если уже открыт - ничего не делаем (MainWindow сфокусирует)
        /// </summary>
        public void OpenModule(string moduleId)
        {
            _logger.LogDebug("OpenModule: {ModuleId}", moduleId);

            var moduleItem = AvailableModules.FirstOrDefault(m => m.ModuleId == moduleId);

            if (moduleItem == null)
            {
                _logger.LogWarning("Module not found: {ModuleId}", moduleId);
                return;
            }

            if (moduleItem.IsActive)
            {
                _logger.LogDebug("Module already active");
                return;
            }

            _logger.LogDebug("Opening module");
            _onModuleAdded?.Invoke(moduleId);
            moduleItem.IsActive = true;

            AvailableModules = AvailableModules
                .OrderByDescending(m => m.IsActive)
                .ThenBy(m => m.DisplayName)
                .ToList();
        }

        /// <summary>
        /// Отметить модуль как закрытый (снять галочку IsActive)
        /// Вызывается из MainWindowViewModel.HandleModuleClosedInDock
        /// когда пользователь закрыл модуль крестиком в Dock
        /// </summary>
        public void MarkModuleAsClosed(string moduleId)
        {
            _logger.LogDebug("Marking module as closed: {ModuleId}", moduleId);

            var moduleItem = AvailableModules.FirstOrDefault(m => m.ModuleId == moduleId);
            if (moduleItem != null)
            {
                moduleItem.IsActive = false;

                AvailableModules = AvailableModules
                    .OrderByDescending(m => m.IsActive)
                    .ThenBy(m => m.DisplayName)
                    .ToList();

                _logger.LogDebug("Module marked as closed: {ModuleId}", moduleId);
            }
            else
            {
                _logger.LogWarning("Module not found in list: {ModuleId}", moduleId);
            }
        }

        /// <summary>Переключить видимость модуля (из панели модулей)</summary>
        private void ToggleModule(ModuleItemViewModel module)
        {
            if (module.IsRequired)
            {
                _logger.LogDebug("Cannot toggle required module: {DisplayName}", module.DisplayName);
                return;
            }

            _logger.LogDebug("Toggling module: {DisplayName} (current: {IsActive})", module.DisplayName, module.IsActive);

            if (module.IsActive)
            {
                _onModuleRemoved?.Invoke(module.ModuleId);
                module.IsActive = false;
            }
            else
            {
                _onModuleAdded?.Invoke(module.ModuleId);
                module.IsActive = true;
            }

            AvailableModules = AvailableModules
                .OrderByDescending(m => m.IsActive)
                .ThenBy(m => m.DisplayName)
                .ToList();
        }
    }

    /// <summary>
    /// ViewModel для одного элемента в списке модулей
    /// Представляет один модуль с его состоянием
    /// </summary>
    public class ModuleItemViewModel : ViewModelBase
    {
        private bool _isActive;

        /// <summary>Идентификатор модуля</summary>
        public string ModuleId { get; set; } = "";

        /// <summary>Отображаемое имя</summary>
        public string DisplayName { get; set; } = "";

        /// <summary>Иконка модуля</summary>
        public string Icon { get; set; } = "";

        /// <summary>Описание модуля</summary>
        public string Description { get; set; } = "";

        /// <summary>Активен ли модуль (включён в WorkMode)</summary>
        public bool IsActive
        {
            get => _isActive;
            set => this.RaiseAndSetIfChanged(ref _isActive, value);
        }

        /// <summary>Обязательный ли модуль (нельзя выключить)</summary>
        public bool IsRequired { get; set; }

        /// <summary>Можно ли переключать модуль (не обязательный)</summary>
        public bool CanToggle => !IsRequired;
    }
}