using Avalonia.Threading;
using Dock.Model.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Writersword.Core.Enums;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Interfaces.Services;
using Writersword.Core.Models.WorkModes;
using Writersword.Modules.Common;
using Writersword.Src.Core.Interfaces.Services;
using Writersword.Src.Core.Interfaces.WorkFlows;
using Writersword.Src.Core.Interfaces.WorkModes;
using Writersword.Src.Core.Interfaces.Workspace;
using Writersword.Src.Infrastructure.Dock;
using Writersword.Src.Infrastructure.Services.WorkModes;
using Writersword.ViewModels;

namespace Writersword.Src.Infrastructure.Workspace
{
    /// <summary>
    /// Контроллер workspace для вкладки документа
    /// Управляет WorkModes, модулями и Dock layout
    /// Модули создаются исключительно в DockFactory при построении layout
    /// Dock.Avalonia управляет своим состоянием самостоятельно
    /// </summary>
    public class WorkspaceController : IWorkspaceController
    {
        private readonly ILogger<WorkspaceController> _logger;
        private readonly DocumentTabViewModel _tab;
        private readonly string _projectPath;
        private readonly DockFactory _dockFactory;
        private readonly IWorkspaceAutoSaveService _autoSave;
        private readonly IWorkModeService _workModeService;

        private WorkMode _activeWorkMode;
        private IRootDock _dockLayout = null!;
        private List<WorkMode> _availableWorkModes;

        private bool _isDeactivating = false;

        public event EventHandler? WorkspaceChanged;

        public IWorkModeService GetWorkModeService() => _workModeService;

        public WorkspaceController(
            DocumentTabViewModel tab,
            string projectPath,
            List<WorkMode> loadedWorkModes,
            DockFactory dockFactory,
            IWorkspaceAutoSaveService autoSave)
        {
            _logger = App.Services.GetService<ILogger<WorkspaceController>>()!;
            _tab = tab;
            _projectPath = projectPath;
            _dockFactory = dockFactory;
            _autoSave = autoSave;
            _availableWorkModes = loadedWorkModes;

            var configService = App.Services.GetRequiredService<IWorkModeConfigurationService>();
            _workModeService = new WorkModeService(configService);
            _workModeService.InitializeWorkModes(tab.GetProject().Type, loadedWorkModes);

            _activeWorkMode = loadedWorkModes.FirstOrDefault(w => w.IsActive)
                              ?? loadedWorkModes.First();

            _logger.LogDebug("Created for: {TabTitle}", tab.Title);
            _logger.LogDebug("Total WorkModes: {TotalCount}, Active: {ActiveTitle}",
                _availableWorkModes.Count, _activeWorkMode.Title);
        }

        public IRootDock GetCurrentLayout() => _dockLayout;

        public List<WorkMode> GetAvailableWorkModes() => _availableWorkModes;

        public WorkMode GetActiveWorkMode() => _activeWorkMode;

        /// <summary>
        /// Получить список активных модулей текущего WorkMode
        /// Сканирует реальный UI (dock + float окна)
        /// </summary>
        public List<IModule> GetActiveModules()
        {
            var allModules = _tab.ModuleContext.GetAllModules();

            if (_activeWorkMode != null)
            {
                var realDocumentIds = new HashSet<string>();
                CollectDocumentIds(_dockLayout, realDocumentIds);

                if (_dockLayout?.Windows != null)
                {
                    foreach (var window in _dockLayout.Windows)
                    {
                        if (window.Layout != null)
                            CollectDocumentIds(window.Layout, realDocumentIds);
                    }
                }

                var filteredModules = allModules
                    .Where(m => realDocumentIds.Contains($"Module_{m.moduleType}"))
                    .ToList();

                _logger.LogDebug("Returned {FilteredCount}/{TotalCount} modules for WorkMode: {WorkModeTitle}",
                    filteredModules.Count, allModules.Count, _activeWorkMode.Title);
                return filteredModules;
            }

            return allModules;
        }

        /// <summary>
        /// Обновить все модули из контекста
        /// </summary>
        public void RefreshModulesFromContext()
        {
            var modules = GetActiveModules();
            foreach (var module in modules)
                module.RefreshFromContext();

            _logger.LogDebug("Refreshed {Count} modules from context", modules.Count);
        }

        /// <summary>
        /// Переключить WorkMode
        /// 1. Сериализуем текущий layout
        /// 2. Закрываем float окна
        /// 3. Переключаем флаги IsActive
        /// 4. Очищаем модули старого WorkMode из контекста
        /// 5. Сбрасываем внутреннее состояние Factory (ClearCurrentLayout)
        /// 6. Создаём новый layout (DockFactory создаст нужные модули сам)
        /// </summary>
        public void SwitchWorkMode(WorkMode newMode)
        {
            _logger.LogDebug("Switching WorkMode: {OldTitle} -> {NewTitle}", _activeWorkMode.Title, newMode.Title);

            if (_dockLayout != null)
            {
                var (serializedLayout, updatedSlots) = _dockFactory.SerializeCurrentLayout(
                    _dockLayout, _activeWorkMode, _tab.ModuleContext);

                if (serializedLayout != null)
                {
                    _activeWorkMode.SerializedDockLayout = serializedLayout;
                    _activeWorkMode.ModuleSlots = updatedSlots;
                    _logger.LogDebug("Serialized layout for WorkMode: {Title}", _activeWorkMode.Title);
                }
                else
                {
                    _logger.LogWarning("Failed to serialize layout for WorkMode: {Title}", _activeWorkMode.Title);
                }
            }

            CloseAllFloatWindows();

            _activeWorkMode.IsActive = false;
            newMode.IsActive = true;
            _activeWorkMode = newMode;

            _dockFactory.DetachViewsFromLayout(_dockLayout);
            ClearModulesNotInNewWorkMode(newMode);

            _dockLayout = _dockFactory.CreateLayout(newMode, _tab);

            _dockFactory.OnModuleClosed = (moduleType) =>
            {
                Dispatcher.UIThread.Post(() => HandleModuleClosedInDock(moduleType));
            };

            _autoSave.NotifyChange();
            WorkspaceChanged?.Invoke(this, EventArgs.Empty);

            _logger.LogDebug("WorkMode switched successfully");
        }

        /// <summary>
        /// Добавить модуль в текущий WorkMode
        /// </summary>
        public void AddModule(string moduleType)
        {
            _logger.LogDebug("Adding module: {moduleType}", moduleType);

            if (_activeWorkMode == null || _dockLayout == null)
            {
                _logger.LogWarning("Cannot add module - no active WorkMode or DockLayout");
                return;
            }

            string documentId = $"Module_{moduleType}";
            bool isInDock = FindDocumentInLayout(_dockLayout, documentId);
            bool isInFloat = IsDocumentInFloatWindows(documentId);

            if (isInDock || isInFloat)
            {
                _logger.LogError("Module {moduleType} already exists in UI", moduleType);
                return;
            }

            ModuleCategory category = _activeWorkMode.ModuleCategories.TryGetValue(moduleType, out var explicitCategory)
                ? explicitCategory
                : ModuleCategory.Optional;

            if (category == ModuleCategory.Forbidden)
            {
                _logger.LogError("Cannot add Forbidden module: {moduleType}", moduleType);
                return;
            }

            var existingSlot = _activeWorkMode.ModuleSlots.FirstOrDefault(s => s.ModuleType == moduleType);

            if (existingSlot == null)
            {
                var moduleMetadata = App.Services.GetRequiredService<ModuleFactory>()
                    .GetAllModuleMetadata()
                    .FirstOrDefault(m => m.ModuleType == moduleType);

                if (moduleMetadata == null)
                {
                    _logger.LogError("Module metadata not found: {moduleType}", moduleType);
                    return;
                }

                var newSlot = new ModuleSlot
                {
                    ModuleType = moduleType,
                    PreferredPosition = PreferredDockPosition.RightAsTab,
                    Category = category
                };

                _activeWorkMode.ModuleSlots.Add(newSlot);
                existingSlot = newSlot;
            }
            else
            {
                existingSlot.Category = category;
            }

            var openModuleIds = GetOpenModuleIds();
            bool hasVisibleModules = openModuleIds.Any(id => id != moduleType);

            if (!hasVisibleModules)
            {
                _dockLayout = _dockFactory.CreateLayout(_activeWorkMode, _tab);

                _dockFactory.OnModuleClosed = (mt) =>
                {
                    Dispatcher.UIThread.Post(() => HandleModuleClosedInDock(mt));
                };
            }
            else
            {
                _dockFactory.InsertModuleByPreference(_dockLayout, existingSlot);
            }

            _autoSave.NotifyChange();
            WorkspaceChanged?.Invoke(this, EventArgs.Empty);

            _logger.LogDebug("Module added successfully");
        }

        /// <summary>
        /// Удалить модуль из текущего WorkMode
        /// </summary>
        public void RemoveModule(string moduleType)
        {
            _logger.LogDebug("Removing module: {moduleType}", moduleType);

            if (_activeWorkMode == null || _dockLayout == null) return;

            var slot = _activeWorkMode.ModuleSlots.FirstOrDefault(s => s.ModuleType == moduleType);
            if (slot != null)
            {
                RemoveModuleFromLayout(_dockLayout, moduleType);
                _tab.ModuleContext.RemoveModule(moduleType);
                _autoSave.NotifyChange();
                WorkspaceChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Вернуть Required модуль обратно в dock
        /// </summary>
        public void ReturnRequiredModuleToDock(string moduleType)
        {
            _logger.LogDebug("Returning required module to dock: {moduleType}", moduleType);

            var slot = _activeWorkMode.ModuleSlots.FirstOrDefault(s => s.ModuleType == moduleType);
            if (slot == null)
            {
                _logger.LogWarning("Module slot not found: {moduleType}", moduleType);
                return;
            }

            _dockLayout = _dockFactory.CreateLayout(_activeWorkMode, _tab);

            _dockFactory.OnModuleClosed = (mt) =>
            {
                Dispatcher.UIThread.Post(() => HandleModuleClosedInDock(mt));
            };

            _autoSave.NotifyChange();
            WorkspaceChanged?.Invoke(this, EventArgs.Empty);

            _logger.LogDebug("Module returned successfully");
        }

        /// <summary>
        /// Обработчик реального закрытия модуля — вызывается из DockFactory.CloseDockable
        /// </summary>
        public void HandleModuleClosedInDock(string moduleType)
        {
            if (_isDeactivating) return;

            _logger.LogDebug("Module closed in dock: {moduleType}", moduleType);

            if (_activeWorkMode == null) return;

            var slot = _activeWorkMode.ModuleSlots.FirstOrDefault(s => s.ModuleType == moduleType);

            if (slot != null && slot.Category == ModuleCategory.Required)
            {
                _logger.LogError("Attempt to close Required module: {moduleType}", moduleType);
                Dispatcher.UIThread.Post(() => ReturnRequiredModuleToDock(moduleType));
                return;
            }

            _tab.ModuleContext.RemoveModule(moduleType);
            _dockFactory.CleanupEmptyContainersInLayout(_dockLayout);

            _autoSave.NotifyChange();
            WorkspaceChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Получить список всех открытых модулей в текущем WorkMode
        /// </summary>
        public HashSet<string> GetOpenModuleIds()
        {
            var result = new HashSet<string>();

            if (_dockLayout == null)
                return result;

            CollectModuleIdsRecursive(_dockLayout, result);

            if (_dockLayout.Windows != null)
            {
                foreach (var window in _dockLayout.Windows)
                {
                    if (window.Layout != null)
                        CollectModuleIdsRecursive(window.Layout, result);
                }
            }

            _logger.LogDebug("Found {Count} open modules: {Modules}",
                result.Count, string.Join(", ", result));
            return result;
        }

        private void CollectModuleIdsRecursive(IDockable dockable, HashSet<string> result)
        {
            if (dockable is Document document && document.Id != null)
                result.Add(document.Id.Replace("Module_", ""));

            if (dockable is IDock dock && dock.VisibleDockables != null)
            {
                foreach (var child in dock.VisibleDockables)
                    CollectModuleIdsRecursive(child, result);
            }
        }

        /// <summary>
        /// Сохранить workspace асинхронно
        /// </summary>
        public async Task SaveWorkspaceAsync()
        {
            _logger.LogDebug("Saving workspace");
            await _autoSave.SaveNowAsync();
        }

        /// <summary>
        /// Активировать workspace
        /// Конфиг уже загружен и передан в конструктор — повторная загрузка не нужна
        /// Перед созданием layout сбрасывает испорченный serializedDockLayout
        /// (все слоты ведут в центр при наличии нескольких модулей)
        /// </summary>
        public void Activate()
        {
            _logger.LogDebug("Activating workspace");

            ResetDegenerateSerializedLayoutIfNeeded(_activeWorkMode);

            _dockLayout = _dockFactory.CreateLayout(_activeWorkMode, _tab);

            _dockFactory.OnModuleClosed = (moduleType) =>
            {
                Dispatcher.UIThread.Post(() => HandleModuleClosedInDock(moduleType));
            };

            if (!string.IsNullOrEmpty(_projectPath))
                _autoSave.Start(_projectPath, _tab.GetProject());

            _logger.LogDebug("Workspace activated");
        }

        /// <summary>
        /// Деактивировать workspace
        /// Сохраняет, закрывает float окна, очищает модули и layout
        /// </summary>
        public void Deactivate()
        {
            _logger.LogDebug("Deactivating workspace");

            _isDeactivating = true;

            try
            {
                try
                {
                    _autoSave.SaveNowAsync().Wait();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to save workspace during deactivation");
                }

                _autoSave.Stop();

                CloseAllFloatWindows();
                _dockFactory.DetachViewsFromLayout(_dockLayout);
                ClearAllModulesFromContext();

                _dockFactory.OnModuleClosed = null;

                _dockLayout = null!;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during deactivation");
                throw;
            }
            finally
            {
                _isDeactivating = false;
            }

            _logger.LogDebug("Workspace deactivated");
        }

        /// <summary>
        /// Освободить ресурсы
        /// </summary>
        public void Dispose()
        {
            _logger.LogDebug("Disposing");

            _autoSave.Stop();
            CloseAllFloatWindows();
            ClearAllModulesFromContext();

            _logger.LogDebug("Disposed");
        }

        /// <summary>
        /// Сбросить WorkMode до конфигурации по умолчанию
        /// </summary>
        public void ResetWorkModeToDefault(WorkMode workMode, WorkMode defaultConfig)
        {
            workMode.SerializedDockLayout = null;

            workMode.ModuleSlots = defaultConfig.ModuleSlots
                .Select(s => new ModuleSlot
                {
                    ModuleType = s.ModuleType,
                    PreferredPosition = s.PreferredPosition,
                    Category = s.Category
                })
                .ToList();

            workMode.ModuleCategories = new Dictionary<string, ModuleCategory>(defaultConfig.ModuleCategories);

            // Деактивируем — сохраняет, детачит Views, очищает модули
            Deactivate();

            // Активируем заново — точно как при первом открытии вкладки
            Activate();

            _autoSave.NotifyChange();
            WorkspaceChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Сбросить serializedDockLayout если все слоты WorkMode ведут в центр
        /// при наличии более одного слота — такой layout вырожден:
        /// все модули оказываются вкладками в одном DocumentDock
        /// </summary>
        private void ResetDegenerateSerializedLayoutIfNeeded(WorkMode workMode)
        {
            if (string.IsNullOrEmpty(workMode.SerializedDockLayout))
                return;

            if (workMode.ModuleSlots.Count <= 1)
                return;

            bool allAreCenter = workMode.ModuleSlots.All(s =>
                s.PreferredPosition is PreferredDockPosition.RightAsTab
                    or PreferredDockPosition.LeftAsTab
                    or PreferredDockPosition.TopAsTab
                    or PreferredDockPosition.BottomAsTab
                    or PreferredDockPosition.TopRightAsTab
                    or PreferredDockPosition.TopLeftAsTab
                    or PreferredDockPosition.BottomRightAsTab
                    or PreferredDockPosition.BottomLeftAsTab);

            if (allAreCenter)
            {
                _logger.LogWarning(
                    "WorkMode '{Title}' has {Count} slots all mapped to center — serialized layout reset",
                    workMode.Title, workMode.ModuleSlots.Count);

                workMode.SerializedDockLayout = null;
            }
        }

        /// <summary>
        /// Очистить все модули из контекста
        /// </summary>
        private void ClearAllModulesFromContext()
        {
            var allModules = _tab.ModuleContext.GetAllModules().ToList();
            foreach (var module in allModules)
                _tab.ModuleContext.RemoveModule(module.moduleType);

            _logger.LogDebug("Cleared {Count} modules from Context", allModules.Count);
        }

        /// <summary>
        /// Очистить из контекста модули которых НЕТ в новом WorkMode
        /// Позволяет переиспользовать общие модули (например Timer) между WorkMode
        /// </summary>
        private void ClearModulesNotInNewWorkMode(WorkMode newWorkMode)
        {
            var targetModuleTypes = new HashSet<string>(
                newWorkMode.ModuleSlots.Select(s => s.ModuleType)
            );

            var allModules = _tab.ModuleContext.GetAllModules().ToList();
            int cleared = 0;

            foreach (var module in allModules)
            {
                if (!targetModuleTypes.Contains(module.moduleType))
                {
                    _tab.ModuleContext.RemoveModule(module.moduleType);
                    cleared++;
                }
            }

            _logger.LogDebug("Cleared {Count} modules not in new WorkMode: {Title}",
                cleared, newWorkMode.Title);
        }

        private void CloseAllFloatWindows()
        {
            if (_dockLayout?.Windows == null || _dockLayout.Windows.Count == 0)
                return;

            foreach (var window in _dockLayout.Windows.ToList())
            {
                if (window.Host is HostWindow hostWindow)
                    hostWindow.Exit();
            }

            _dockLayout.Windows.Clear();

            _logger.LogDebug("Float windows closed");
        }

        private void RemoveModuleFromLayout(IRootDock rootDock, string moduleType)
        {
            string documentId = $"Module_{moduleType}";
            RemoveDocumentRecursive(rootDock, documentId);
        }

        private bool RemoveDocumentRecursive(IDockable dockable, string documentId)
        {
            if (dockable is IDock dock && dock.VisibleDockables != null)
            {
                var document = dock.VisibleDockables.FirstOrDefault(d => d.Id == documentId);
                if (document != null)
                {
                    dock.VisibleDockables.Remove(document);

                    if (dock.ActiveDockable == document)
                        dock.ActiveDockable = dock.VisibleDockables.FirstOrDefault();

                    return true;
                }

                foreach (var child in dock.VisibleDockables.ToList())
                {
                    if (RemoveDocumentRecursive(child, documentId))
                        return true;
                }
            }

            return false;
        }

        private bool IsDocumentInFloatWindows(string documentId)
        {
            if (_dockLayout?.Windows == null) return false;

            foreach (var window in _dockLayout.Windows)
            {
                if (window.Layout != null && FindDocumentInLayout(window.Layout, documentId))
                    return true;
            }

            return false;
        }

        private bool FindDocumentInLayout(IDockable dockable, string documentId)
        {
            if (dockable is IDock dock)
            {
                if (dock.ActiveDockable?.Id == documentId)
                    return true;

                if (dock.VisibleDockables != null)
                {
                    if (dock.VisibleDockables.Any(d => d.Id == documentId))
                        return true;

                    foreach (var child in dock.VisibleDockables)
                    {
                        if (FindDocumentInLayout(child, documentId))
                            return true;
                    }
                }
            }

            return false;
        }

        private void CollectDocumentIds(IDockable dockable, HashSet<string> documentIds)
        {
            if (dockable is IDock dock && dock.VisibleDockables != null)
            {
                foreach (var item in dock.VisibleDockables)
                {
                    if (item is Document doc && doc.Id != null)
                        documentIds.Add(doc.Id);

                    CollectDocumentIds(item, documentIds);
                }
            }
        }
    }
}