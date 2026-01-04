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
        private readonly ModuleRegistry _moduleRegistry;
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

        public ModulePanelViewModel(ModuleRegistry moduleRegistry)
        {
            _moduleRegistry = moduleRegistry;

            // Команда переключения модуля
            ToggleModuleCommand = ReactiveCommand.Create<ModuleItemViewModel>(ToggleModule);

            Console.WriteLine("[ModulePanelViewModel] Initialized");
        }

        /// <summary>
        /// Установить обработчики добавления/удаления модулей
        /// Вызывается из MainWindowViewModel после создания
        /// </summary>
        public void SetModuleHandlers(Action<string> onModuleAdded, Action<string> onModuleRemoved)
        {
            _onModuleAdded = onModuleAdded;
            _onModuleRemoved = onModuleRemoved;
            Console.WriteLine("[ModulePanelViewModel] Module handlers set");
        }

        /// <summary>
        /// Загрузить список модулей для текущего WorkMode
        /// Вызывается из MainWindowViewModel при смене WorkMode
        /// </summary>
        public void LoadModulesForWorkMode(WorkMode workMode)
        {
            _currentWorkMode = workMode;

            // Получаем все доступные модули из реестра
            var allModules = _moduleRegistry.GetAllModuleMetadata();

            // Создаём список ModuleItemViewModel
            var moduleItems = new List<ModuleItemViewModel>();

            foreach (var metadata in allModules)
            {
                // Проверяем есть ли модуль в текущем WorkMode
                var moduleSlot = workMode.ModuleSlots.FirstOrDefault(ms => ms.ModuleId == metadata.ModuleId);

                var item = new ModuleItemViewModel
                {
                    ModuleId = metadata.ModuleId,
                    DisplayName = metadata.DisplayName,
                    Icon = metadata.Icon,
                    Description = metadata.Description,
                    IsActive = moduleSlot != null && moduleSlot.IsVisible,
                    IsRequired = moduleSlot != null && !moduleSlot.IsCloseable
                };

                moduleItems.Add(item);
            }

            // Сортируем: сначала активные, потом по алфавиту
            AvailableModules = moduleItems
                .OrderByDescending(m => m.IsActive)
                .ThenBy(m => m.DisplayName)
                .ToList();

            Console.WriteLine($"[ModulePanelViewModel] Loaded {AvailableModules.Count} modules for WorkMode: {workMode.Title}");
        }

        /// <summary>Переключить видимость модуля</summary>
        private void ToggleModule(ModuleItemViewModel module)
        {
            // Обязательные модули нельзя выключить
            if (module.IsRequired)
            {
                Console.WriteLine($"[ModulePanelViewModel] Cannot toggle required module: {module.DisplayName}");
                return;
            }

            Console.WriteLine($"[ModulePanelViewModel] Toggling module: {module.DisplayName} (current: {module.IsActive})");

            if (module.IsActive)
            {
                // Выключаем модуль
                _onModuleRemoved?.Invoke(module.ModuleId);
                module.IsActive = false;
            }
            else
            {
                // Включаем модуль
                _onModuleAdded?.Invoke(module.ModuleId);
                module.IsActive = true;
            }

            // Пересортируем список (активные наверх)
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