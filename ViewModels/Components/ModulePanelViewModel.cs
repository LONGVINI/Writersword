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
using Writersword.Src.Core.Interfaces.WorkFlows;

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
        /// Показывает ВСЕ модули из ModuleCategories (кроме Forbidden)
        /// Галочки синхронизируются с РЕАЛЬНЫМ UI состоянием
        /// </summary>
        public void LoadModulesForWorkMode(WorkMode workMode)
        {
            _logger.LogDebug("Loading modules for WorkMode: {Title}", workMode.Title);

            _currentWorkMode = workMode;
            AvailableModules.Clear();

            var allModuleMetadata = _moduleFactory.GetAllModuleMetadata();

            var mainViewModel = App.Services.GetRequiredService<MainWindowViewModel>();
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
                ModuleCategory category;

                if (workMode.ModuleCategories.TryGetValue(metadata.ModuleId, out var explicitCategory))
                {
                    category = explicitCategory;
                }
                else
                {
                    category = ModuleCategory.Optional;
                }

                if (category == ModuleCategory.Forbidden)
                {
                    _logger.LogDebug("Skipping forbidden module: {ModuleId}", metadata.ModuleId);
                    continue;
                }

                bool isActuallyOpen = actuallyOpenModules.Contains(metadata.ModuleId);

                var panelItem = new ModuleItemViewModel
                {
                    ModuleId = metadata.ModuleId,
                    DisplayName = metadata.DisplayName,
                    IsActive = isActuallyOpen,
                    IsRequired = category == ModuleCategory.Required,
                    Category = category,
                    Order = GetCategoryOrder(category)
                };

                AvailableModules.Add(panelItem);

                _logger.LogDebug("Added module: {ModuleId}, Category: {Category}, ReallyOpen: {IsOpen}",
                    metadata.ModuleId, category, isActuallyOpen);
            }

            var sorted = AvailableModules
                .OrderBy(m => m.Order)
                .ThenBy(m => m.DisplayName)
                .ToList();

            AvailableModules = sorted;

            _logger.LogDebug("Loaded {Count} modules (sorted by category)", AvailableModules.Count);
        }

        /// <summary>
        /// Обновить состояние галочек без полной перезагрузки списка
        /// Вызывается при наведении на меню для синхронизации с UI
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
                bool wasActive = moduleItem.IsActive;
                bool shouldBeActive = actuallyOpenModules.Contains(moduleItem.ModuleId);

                if (wasActive != shouldBeActive)
                {
                    _logger.LogDebug("Module {ModuleId} state changed: {Old} -> {New}",
                        moduleItem.ModuleId, wasActive, shouldBeActive);
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
        /// Если уже открыт - фокусирует, если нет - создаёт новый
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

            if (_isModuleAlreadyOpen?.Invoke(moduleId) == true)
            {
                _logger.LogDebug("Module already open, focusing");
                _onFocusModule?.Invoke(moduleId);
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
                if (_isModuleAlreadyOpen?.Invoke(module.ModuleId) == true)
                {
                    _logger.LogDebug("Module already open, focusing");
                    _onFocusModule?.Invoke(module.ModuleId);
                }
                else
                {
                    _onModuleAdded?.Invoke(module.ModuleId);
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

        /// <summary>Категория модуля в текущем WorkMode</summary>
        public ModuleCategory Category { get; set; } = ModuleCategory.Optional;

        /// <summary>Порядок сортировки (для UI)</summary>
        public int Order { get; set; }
    }
}