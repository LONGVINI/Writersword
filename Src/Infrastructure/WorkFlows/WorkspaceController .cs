using Dock.Model.Controls;
using Dock.Model.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Threading.Tasks;
using Writersword.Core.Enums;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Models.WorkModes;
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
    /// Контроллер рабочего пространства одной вкладки (проекта)
    /// Полностью изолирован - не знает о других вкладках
    /// Управляет всем UI состоянием проекта
    /// </summary>
    public class WorkspaceController : IWorkspaceController
    {
        private readonly ILogger<WorkspaceController> _logger;
        private readonly DocumentTabViewModel _tab;
        private readonly string _projectPath;
        private readonly DockFactory _dockFactory;
        private readonly IWorkspaceAutoSaveService _autoSave;

        private WorkMode _activeWorkMode;
        private IRootDock _dockLayout;
        private List<WorkMode> _availableWorkModes;
        private readonly List<IDisposable> _subscriptions;

        private readonly IWorkModeService _workModeService;

        /// <summary>
        /// Событие изменения workspace
        /// </summary>
        public event EventHandler? WorkspaceChanged;

        /// <summary>
        /// Получить WorkModeService этого проекта
        /// </summary>
        public IWorkModeService GetWorkModeService() => _workModeService;


        /// <summary>
        /// Конструктор контроллера workspace
        /// </summary>
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
            _subscriptions = new List<IDisposable>();

            var configService = App.Services.GetRequiredService<IWorkModeConfigurationService>();
            _workModeService = new WorkModeService(configService);
            _workModeService.InitializeWorkModes(tab.GetProject().Type, loadedWorkModes);

            _activeWorkMode = loadedWorkModes.FirstOrDefault(w => w.IsActive)
                              ?? loadedWorkModes.First();

            _dockLayout = _dockFactory.CreateLayout(_activeWorkMode, _tab);

            if (!string.IsNullOrEmpty(_projectPath))
            {
                _autoSave.Start(_projectPath, tab.GetProject());
                SubscribeToDockEvents(_dockLayout);
            }

            _logger.LogDebug("Created for: {TabTitle}", tab.Title);
            _logger.LogDebug("Total WorkModes: {TotalCount}, Active: {ActiveTitle}", _availableWorkModes.Count, _activeWorkMode.Title);
        }

        /// <summary>
        /// Получить текущий DockLayout
        /// </summary>
        public IRootDock GetCurrentLayout() => _dockLayout;

        /// <summary>
        /// Получить все доступные WorkModes
        /// </summary>
        public List<WorkMode> GetAvailableWorkModes() => _availableWorkModes;

        /// <summary>
        /// Получить активный WorkMode
        /// </summary>
        public WorkMode GetActiveWorkMode() => _activeWorkMode;

        /// <summary>
        /// Получить активные модули текущего WorkMode
        /// </summary>
        public List<IModule> GetActiveModules()
        {
            var allModules = _tab.ModuleContext.GetAllModules();

            if (_activeWorkMode != null)
            {
                var validInstanceIds = _activeWorkMode.ModuleSlots
                    .Where(s => !string.IsNullOrEmpty(s.InstanceId))
                    .Select(s => s.InstanceId)
                    .ToHashSet();

                var filteredModules = allModules
                    .Where(m => validInstanceIds.Contains(m.InstanceId))
                    .ToList();

                _logger.LogDebug("Returned {FilteredCount}/{TotalCount} modules for WorkMode: {WorkModeTitle}", filteredModules.Count, allModules.Count, _activeWorkMode.Title);
                return filteredModules;
            }

            _logger.LogDebug("No ActiveWorkMode, returning all {Count} modules", allModules.Count);
            return allModules;
        }

        /// <summary>
        /// Обновить все активные модули из Context
        /// Используется когда Context.IsInCompareMode меняется
        /// </summary>
        public void RefreshModulesFromContext()
        {
            var modules = GetActiveModules();
            foreach (var module in modules)
            {
                module.RefreshFromContext();
            }
            _logger.LogDebug("Refreshed {Count} modules from context", modules.Count());
        }

        /// <summary>
        /// Переключить WorkMode
        /// </summary>
        public void SwitchWorkMode(WorkMode newMode)
        {
            _logger.LogDebug("Switching WorkMode: {OldTitle} -> {NewTitle}", _activeWorkMode.Title, newMode.Title);

            _autoSave.NotifyChange();

            DisposeCurrentModules();

            CloseAllFloatWindows();

            _activeWorkMode.IsActive = false;
            newMode.IsActive = true;
            _activeWorkMode = newMode;

            UnsubscribeFromDockEvents();

            _dockLayout = _dockFactory.CreateLayout(newMode, _tab);

            SubscribeToDockEvents(_dockLayout);

            WorkspaceChanged?.Invoke(this, EventArgs.Empty);

            _logger.LogDebug("WorkMode switched successfully");
        }

        /// <summary>
        /// Добавить модуль динамически
        /// </summary>
        public void AddModule(string moduleId)
        {
            _logger.LogDebug("Adding module: {ModuleId}", moduleId);

            if (_activeWorkMode == null || _dockLayout == null) return;

            var existingSlot = _activeWorkMode.ModuleSlots.FirstOrDefault(s => s.ModuleId == moduleId);

            if (existingSlot == null)
            {
                var workModeConfigService = App.Services.GetRequiredService<IWorkModeConfigurationService>();
                var newSlot = new ModuleSlot
                {
                    ModuleId = moduleId,
                    IsCloseable = workModeConfigService.CanRemoveModule(
                        _tab.GetProject().Type,
                        _activeWorkMode.WorkModeId,
                        moduleId
                    ),
                    MinWidth = 200,
                    MinHeight = 150,
                    PreferredPosition = PreferredDockPosition.RightAsTab
                };

                _activeWorkMode.ModuleSlots.Add(newSlot);
                existingSlot = newSlot;
            }

            var hasVisibleModules = _activeWorkMode.ModuleSlots.Any(s => s.ModuleId != moduleId);

            if (!hasVisibleModules)
            {
                _logger.LogDebug("No visible modules - recreating layout");
                var newLayout = _dockFactory.CreateLayout(_activeWorkMode);
                UnsubscribeFromDockEvents();
                _dockLayout = newLayout;
                SubscribeToDockEvents(_dockLayout);
            }
            else
            {
                _logger.LogDebug("Adding module dynamically");
                _dockFactory.InsertModuleByPreference(_dockLayout, existingSlot);
            }

            _autoSave.NotifyChange();
            WorkspaceChanged?.Invoke(this, EventArgs.Empty);

            _logger.LogDebug("Module added successfully");
        }

        /// <summary>
        /// Удалить модуль
        /// </summary>
        public void RemoveModule(string moduleId)
        {
            _logger.LogDebug("Removing module: {ModuleId}", moduleId);

            if (_activeWorkMode == null || _dockLayout == null) return;

            var slot = _activeWorkMode.ModuleSlots.FirstOrDefault(s => s.ModuleId == moduleId);
            if (slot != null)
            {
                RemoveModuleFromLayout(_dockLayout, moduleId);
                _autoSave.NotifyChange();
                WorkspaceChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Вернуть обязательный модуль из Float в Dock
        /// </summary>
        public void ReturnRequiredModuleToDock(string moduleId)
        {
            _logger.LogDebug("Returning required module to dock: {ModuleId}", moduleId);

            var slot = _activeWorkMode.ModuleSlots.FirstOrDefault(s => s.ModuleId == moduleId);
            if (slot == null)
            {
                _logger.LogWarning("Module slot not found: {ModuleId}", moduleId);
                return;
            }

            slot.IsFloating = false;
            _logger.LogDebug("Reset IsFloating for: {ModuleId}", moduleId);

            UnsubscribeFromDockEvents();
            _dockLayout = _dockFactory.CreateLayout(_activeWorkMode, _tab);
            SubscribeToDockEvents(_dockLayout);

            _autoSave.NotifyChange();
            WorkspaceChanged?.Invoke(this, EventArgs.Empty);

            _logger.LogDebug("Module returned successfully");
        }

        /// <summary>
        /// Обработчик закрытия модуля в Dock
        /// </summary>
        public void HandleModuleClosedInDock(string moduleId)
        {
            _logger.LogDebug("Module closed in dock: {ModuleId}", moduleId);

            if (_activeWorkMode == null) return;

            var slot = _activeWorkMode.ModuleSlots.FirstOrDefault(s => s.ModuleId == moduleId);
            if (slot != null && slot.IsFloating)
            {
                slot.IsFloating = false;
                slot.ContainerId = null;
                _logger.LogDebug("Reset floating flag for: {ModuleId}", moduleId);
            }

            _autoSave.NotifyChange();
            WorkspaceChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Сохранить workspace
        /// </summary>
        public async Task SaveWorkspaceAsync()
        {
            _logger.LogDebug("Saving workspace");
            await _autoSave.SaveNowAsync();
        }

        /// <summary>
        /// Очистка ресурсов
        /// </summary>
        public void Dispose()
        {
            _logger.LogDebug("Disposing");

            _autoSave.Stop();

            CloseAllFloatWindows();

            DisposeCurrentModules();

            UnsubscribeFromDockEvents();

            _subscriptions.ForEach(s => s.Dispose());
            _subscriptions.Clear();

            _logger.LogDebug("Disposed");
        }

        private void DisposeCurrentModules()
        {
            foreach (var slot in _activeWorkMode.ModuleSlots)
            {
                if (!string.IsNullOrEmpty(slot.InstanceId))
                {
                    _tab.ModuleContext.RemoveModule(slot.InstanceId);
                    slot.InstanceId = "";
                }
            }
        }

        private void CloseAllFloatWindows()
        {
            if (_dockLayout?.Windows != null)
            {
                foreach (var window in _dockLayout.Windows.ToList())
                {
                    if (window.Host is HostWindow hostWindow)
                    {
                        hostWindow.Exit();
                    }
                }
                _dockLayout.Windows.Clear();
            }
        }

        private void RemoveModuleFromLayout(IRootDock rootDock, string moduleId)
        {
            string documentId = $"Module_{moduleId}";
            _logger.LogDebug("Searching for: {DocumentId}", documentId);
            RemoveDocumentRecursive(rootDock, documentId);
        }

        private bool RemoveDocumentRecursive(IDockable dockable, string documentId)
        {
            if (dockable is IDock dock && dock.VisibleDockables != null)
            {
                var document = dock.VisibleDockables.FirstOrDefault(d => d.Id == documentId);
                if (document != null)
                {
                    _logger.LogDebug("Found document, removing from {DockId}", dock.Id);
                    dock.VisibleDockables.Remove(document);

                    if (dock.ActiveDockable == document)
                    {
                        dock.ActiveDockable = dock.VisibleDockables.FirstOrDefault();
                    }

                    return true;
                }

                foreach (var child in dock.VisibleDockables.ToList())
                {
                    if (RemoveDocumentRecursive(child, documentId))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private void SubscribeToDockEvents(IDockable dockable)
        {
            _logger.LogDebug("Subscribing to events: {DockableId}", dockable.Id);

            if (dockable is IRootDock rootDock && rootDock.Windows is INotifyCollectionChanged windowsObservable)
            {
                NotifyCollectionChangedEventHandler windowsHandler = (s, e) =>
                {
                    if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
                    {
                        _logger.LogDebug("Float window created");
                        _autoSave.NotifyChange();
                    }

                    if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems != null)
                    {
                        foreach (var item in e.OldItems)
                        {
                            if (item is IDockWindow dockWindow)
                            {
                                _logger.LogDebug("Float window closed: {WindowId}", dockWindow.Id);
                                var moduleId = dockWindow.Id?.Replace("Float_", "") ?? "";
                                HandleModuleClosedInDock(moduleId);
                            }
                        }
                    }
                };

                windowsObservable.CollectionChanged += windowsHandler;

                var subscription = Disposable.Create(() =>
                {
                    windowsObservable.CollectionChanged -= windowsHandler;
                });

                _subscriptions.Add(subscription);
            }

            if (dockable is INotifyPropertyChanged notifyProperty)
            {
                PropertyChangedEventHandler handler = (sender, e) =>
                {
                    if (e.PropertyName == nameof(IDock.Proportion) ||
                        e.PropertyName == nameof(IDock.ActiveDockable))
                    {
                        _autoSave.NotifyChange();
                    }
                };

                notifyProperty.PropertyChanged += handler;

                var subscription = Disposable.Create(() =>
                {
                    notifyProperty.PropertyChanged -= handler;
                });

                _subscriptions.Add(subscription);
            }

            if (dockable is IDock dock && dock.VisibleDockables is INotifyCollectionChanged observable)
            {
                NotifyCollectionChangedEventHandler handler = (s, e) =>
                {
                    _autoSave.NotifyChange();
                };

                observable.CollectionChanged += handler;

                var subscription = Disposable.Create(() =>
                {
                    observable.CollectionChanged -= handler;
                });

                _subscriptions.Add(subscription);
            }

            if (dockable is IDock dockWithChildren && dockWithChildren.VisibleDockables != null)
            {
                foreach (var child in dockWithChildren.VisibleDockables)
                {
                    SubscribeToDockEvents(child);
                }
            }
        }

        private void UnsubscribeFromDockEvents()
        {
            _logger.LogDebug("Unsubscribing from events");
            _subscriptions.ForEach(s => s.Dispose());
            _subscriptions.Clear();
        }
    }
}