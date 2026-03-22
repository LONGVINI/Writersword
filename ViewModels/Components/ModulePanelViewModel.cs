using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using Writersword.Core.Enums;
using Writersword.Core.Models.WorkModes;
using Writersword.Modules.Common;
using Writersword.Core.Interfaces.WorkFlows;

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

        /// <summary>Функция проверки открыт ли модуль (передаётся из MainWindowViewModel)</summary>
        private Func<string, bool>? _isModuleAlreadyOpen;

        /// <summary>Функция фокусировки модуля (передаётся из MainWindowViewModel)</summary>
        private Action<string>? _onFocusModule;

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
        /// Установить обработчики проверки и фокусировки модулей
        /// Вызывается из MainWindowViewModel после создания
        /// </summary>
        public void SetModuleCheckHandlers(Func<string, bool> isModuleAlreadyOpen, Action<string> onFocusModule)
        {
            _isModuleAlreadyOpen = isModuleAlreadyOpen;
            _onFocusModule = onFocusModule;
            _logger.LogDebug("Module check handlers set");
        }

        /// <summary>
        /// Загрузить модули для WorkMode
        /// Галочки синхронизируются с реальным UI состоянием
        /// </summary>
        public void LoadModulesForWorkMode(WorkMode workMode)
        {
            _logger.LogDebug("Loading modules for WorkMode: {Title}", workMode.Title);

            _currentWorkMode = workMode;
            AvailableModules.Clear();

            var allModuleMetadata = _moduleFactory.GetAllModuleMetadata();

            var tabCollection = App.Services.GetRequiredService<ITabCollection>();
            var activeTab = tabCollection.ActiveTab;

            HashSet<string> actuallyOpenModules = new HashSet<string>();

            if (activeTab?.Workspace != null)
            {
                actuallyOpenModules = activeTab.Workspace.GetOpenModuleIds();
                _logger.LogDebug("Actually open modules from UI: {Count} - {Modules}",
                    actuallyOpenModules.Count, string.Join(", ", actuallyOpenModules));
            }
            else
            {
                _logger.LogWarning("No active workspace, cannot get real UI state");
            }

            foreach (var metadata in allModuleMetadata)
            {
                ModuleCategory category = workMode.ModuleCategories.TryGetValue(metadata.ModuleType, out var explicitCategory)
                    ? explicitCategory
                    : ModuleCategory.Optional;

                if (category == ModuleCategory.Forbidden)
                {
                    _logger.LogDebug("Skipping forbidden module: {moduleType}", metadata.ModuleType);
                    continue;
                }

                bool isActuallyOpen = actuallyOpenModules.Contains(metadata.ModuleType);

                var panelItem = new ModuleItemViewModel
                {
                    moduleType = metadata.ModuleType,
                    DisplayName = metadata.DisplayName,
                    IsActive = isActuallyOpen,
                    IsRequired = category == ModuleCategory.Required,
                    Category = category,
                    Order = GetCategoryOrder(category)
                };

                AvailableModules.Add(panelItem);

                _logger.LogDebug("Added module: {moduleType}, Category: {Category}, ReallyOpen: {IsOpen}",
                    metadata.ModuleType, category, isActuallyOpen);
            }

            AvailableModules = AvailableModules
                .OrderBy(m => m.Order)
                .ThenBy(m => m.DisplayName)
                .ToList();

            _logger.LogDebug("Loaded {Count} modules", AvailableModules.Count);
        }

        /// <summary>
        /// Обновить состояние галочек без полной перезагрузки списка
        /// Вызывается при обновлении меню для синхронизации с UI
        /// </summary>
        public void RefreshModuleStates()
        {
            if (_currentWorkMode == null)
            {
                _logger.LogDebug("No current WorkMode, skipping refresh");
                return;
            }

            var tabCollection = App.Services.GetRequiredService<ITabCollection>();
            var activeTab = tabCollection.ActiveTab;

            if (activeTab?.Workspace == null)
            {
                _logger.LogWarning("No active workspace, cannot refresh states");
                return;
            }

            var actuallyOpenModules = activeTab.Workspace.GetOpenModuleIds();

            _logger.LogDebug("Refreshing module states, actually open: {Count}", actuallyOpenModules.Count);

            foreach (var moduleItem in AvailableModules)
            {
                bool shouldBeActive = actuallyOpenModules.Contains(moduleItem.moduleType);

                if (moduleItem.IsActive != shouldBeActive)
                {
                    _logger.LogDebug("Module {moduleType} state changed: {Old} -> {New}",
                        moduleItem.moduleType, moduleItem.IsActive, shouldBeActive);
                    moduleItem.IsActive = shouldBeActive;
                }
            }

            _logger.LogDebug("Module states refreshed");
        }

        /// <summary>
        /// Получить порядок сортировки для категории
        /// </summary>
        private int GetCategoryOrder(ModuleCategory category)
        {
            return category switch
            {
                ModuleCategory.Required => 0,
                ModuleCategory.Optional => 1,
                ModuleCategory.Unwanted => 2,
                ModuleCategory.Forbidden => 3,
                _ => 4
            };
        }

        /// <summary>
        /// Очистить панель модулей
        /// </summary>
        public void Clear()
        {
            _currentWorkMode = null;
            AvailableModules = new List<ModuleItemViewModel>();
            _logger.LogDebug("Cleared all modules");
        }

        /// <summary>
        /// Открыть модуль (вызов из меню)
        /// Если уже открыт - фокусирует, если нет - создаёт новый
        /// </summary>
        public void OpenModule(string moduleType)
        {
            _logger.LogDebug("OpenModule: {moduleType}", moduleType);

            var moduleItem = AvailableModules.FirstOrDefault(m => m.moduleType == moduleType);

            if (moduleItem == null)
            {
                _logger.LogWarning("Module not found: {moduleType}", moduleType);
                return;
            }

            if (_isModuleAlreadyOpen?.Invoke(moduleType) == true)
            {
                _logger.LogDebug("Module already open, focusing");
                _onFocusModule?.Invoke(moduleType);
                return;
            }

            _logger.LogDebug("Opening module");
            _onModuleAdded?.Invoke(moduleType);
            moduleItem.IsActive = true;

            AvailableModules = AvailableModules
                .OrderByDescending(m => m.IsActive)
                .ThenBy(m => m.DisplayName)
                .ToList();
        }

        /// <summary>
        /// Переключить видимость модуля (из панели модулей)
        /// </summary>
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
                _onModuleRemoved?.Invoke(module.moduleType);
                module.IsActive = false;
            }
            else
            {
                if (_isModuleAlreadyOpen?.Invoke(module.moduleType) == true)
                {
                    _logger.LogDebug("Module already open, focusing");
                    _onFocusModule?.Invoke(module.moduleType);
                }
                else
                {
                    _onModuleAdded?.Invoke(module.moduleType);
                    module.IsActive = true;
                }
            }

            AvailableModules = AvailableModules
                .OrderByDescending(m => m.IsActive)
                .ThenBy(m => m.DisplayName)
                .ToList();
        }
    }

    /// <summary>
    /// ViewModel для одного элемента в списке модулей
    /// </summary>
    public class ModuleItemViewModel : ViewModelBase
    {
        private bool _isActive;

        /// <summary>Идентификатор модуля</summary>
        public string moduleType { get; set; } = "";

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

        /// <summary>Можно ли переключать модуль</summary>
        public bool CanToggle => !IsRequired;

        /// <summary>Категория модуля в текущем WorkMode</summary>
        public ModuleCategory Category { get; set; } = ModuleCategory.Optional;

        /// <summary>Порядок сортировки</summary>
        public int Order { get; set; }
    }
}