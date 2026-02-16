using Avalonia;
using Dock.Model.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using DynamicData.Binding;
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
    public class WorkspaceController : IWorkspaceController
    {
        private readonly ILogger<WorkspaceController> _logger;
        private readonly DocumentTabViewModel _tab;
        private readonly string _projectPath;
        private readonly DockFactory _dockFactory;
        private readonly IWorkspaceAutoSaveService _autoSave;
        private readonly ContainerPathBuilder _pathBuilder;

        private WorkMode _activeWorkMode;
        private IRootDock _dockLayout = null!;
        private List<WorkMode> _availableWorkModes;
        private readonly List<IDisposable> _subscriptions;
        private readonly IWorkModeService _workModeService;

        private bool _isCleaningUp = false;
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
            _subscriptions = new List<IDisposable>();
            _pathBuilder = new ContainerPathBuilder();

            var configService = App.Services.GetRequiredService<IWorkModeConfigurationService>();
            _workModeService = new WorkModeService(configService);
            _workModeService.InitializeWorkModes(tab.GetProject().Type, loadedWorkModes);

            _activeWorkMode = loadedWorkModes.FirstOrDefault(w => w.IsActive)
                              ?? loadedWorkModes.First();

            _logger.LogDebug("Created for: {TabTitle}", tab.Title);
            _logger.LogDebug("Total WorkModes: {TotalCount}, Active: {ActiveTitle}", _availableWorkModes.Count, _activeWorkMode.Title);
        }

        public IRootDock GetCurrentLayout() => _dockLayout;

        public List<WorkMode> GetAvailableWorkModes() => _availableWorkModes;

        public WorkMode GetActiveWorkMode() => _activeWorkMode;

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
                        {
                            CollectDocumentIds(window.Layout, realDocumentIds);
                        }
                    }
                }

                var filteredModules = allModules
                    .Where(m => realDocumentIds.Contains($"Module_{m.Metadata.ModuleId}"))
                    .ToList();

                _logger.LogDebug("Returned {FilteredCount}/{TotalCount} modules for WorkMode: {WorkModeTitle} (from real UI)",
                    filteredModules.Count, allModules.Count, _activeWorkMode.Title);
                return filteredModules;
            }

            _logger.LogDebug("No ActiveWorkMode, returning all {Count} modules", allModules.Count);
            return allModules;
        }

        public void RefreshModulesFromContext()
        {
            var modules = GetActiveModules();
            foreach (var module in modules)
            {
                module.RefreshFromContext();
            }
            _logger.LogDebug("Refreshed {Count} modules from context", modules.Count());
        }

        public void SwitchWorkMode(WorkMode newMode)
        {
            _logger.LogDebug("Switching WorkMode: {OldTitle} -> {NewTitle}", _activeWorkMode.Title, newMode.Title);

            _autoSave.NotifyChange();

            CloseCurrentModules();

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
        /// Добавить модуль в текущий WorkMode
        /// Создаёт новый слот и размещает модуль по PreferredPosition
        /// </summary>
        public void AddModule(string moduleId)
        {
            _logger.LogDebug("Adding module: {ModuleId}", moduleId);

            if (_activeWorkMode == null || _dockLayout == null)
            {
                _logger.LogWarning("Cannot add module - no active WorkMode or DockLayout");
                return;
            }

            string documentId = $"Module_{moduleId}";
            bool isInDock = FindDocumentInLayout(_dockLayout, documentId);
            bool isInFloat = IsDocumentInFloatWindows(documentId);

            if (isInDock || isInFloat)
            {
                _logger.LogError("Module {ModuleId} already exists in UI", moduleId);
                return;
            }

            ModuleCategory category;

            if (_activeWorkMode.ModuleCategories.TryGetValue(moduleId, out var explicitCategory))
            {
                category = explicitCategory;
            }
            else
            {
                category = ModuleCategory.Optional;
            }

            if (category == ModuleCategory.Forbidden)
            {
                _logger.LogError("Cannot add Forbidden module: {ModuleId}", moduleId);
                return;
            }

            var existingSlot = _activeWorkMode.ModuleSlots.FirstOrDefault(s => s.ModuleType == moduleId);

            if (existingSlot == null)
            {
                var workModeConfigService = App.Services.GetRequiredService<IWorkModeConfigurationService>();

                string? existingInstanceId = FindExistingInstanceIdInWorkModes(moduleId);

                if (existingInstanceId == null)
                {
                    existingInstanceId = Guid.NewGuid().ToString();
                    _logger.LogDebug("Generated new InstanceId: {InstanceId}", existingInstanceId);
                }
                else
                {
                    _logger.LogDebug("Reusing existing InstanceId: {InstanceId}", existingInstanceId);
                }

                bool isCloseable = category != ModuleCategory.Required;

                _logger.LogDebug("Module {ModuleId} Category={Category}, IsCloseable={IsCloseable}",
                    moduleId, category, isCloseable);

                var moduleMetadata = App.Services.GetRequiredService<ModuleFactory>()
                    .GetAllModuleMetadata()
                    .FirstOrDefault(m => m.ModuleId == moduleId);

                if (moduleMetadata == null)
                {
                    _logger.LogError("Module metadata not found: {ModuleId}", moduleId);
                    return;
                }

                var newSlot = new ModuleSlot
                {
                    ModuleType = moduleId,
                    InstanceId = existingInstanceId,
                    Path = null,
                    IsCloseable = isCloseable,
                    IsCurrentlyOpen = true,
                    IsFloating = false,
                    MinWidth = 200,
                    MinHeight = 150,
                    PreferredPosition = PreferredDockPosition.RightAsTab,
                    Category = category
                };

                _logger.LogDebug("Created new slot: ModuleId={ModuleId}, InstanceId={InstanceId}, Category={Category}, IsCloseable={IsCloseable}",
                    moduleId, existingInstanceId, category, isCloseable);

                _activeWorkMode.ModuleSlots.Add(newSlot);
                existingSlot = newSlot;
            }
            else
            {
                if (existingSlot.IsCurrentlyOpen)
                {
                    _logger.LogDebug("Module slot marked as open but not in UI: {ModuleId}, reopening", moduleId);
                }

                existingSlot.IsCurrentlyOpen = true;
                existingSlot.Category = category;

                _logger.LogDebug("Reopening existing slot: ModuleId={ModuleId}, InstanceId={InstanceId}, Category={Category}",
                    moduleId, existingSlot.InstanceId, category);
            }

            var hasVisibleModules = _activeWorkMode.ModuleSlots.Any(s => s.ModuleType != moduleId && s.IsCurrentlyOpen);

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
                _logger.LogDebug("Adding module dynamically with Category={Category}", existingSlot.Category);
                _dockFactory.InsertModuleByPreference(_dockLayout, existingSlot);
            }

            WorkspaceChanged?.Invoke(this, EventArgs.Empty);
            _autoSave.SaveNowAsync().ConfigureAwait(false);

            _logger.LogDebug("Module added successfully");
        }

        /// <summary>
        /// Удалить модуль из текущего WorkMode
        /// </summary>
        public void RemoveModule(string moduleId)
        {
            _logger.LogDebug("Removing module: {ModuleId}", moduleId);

            if (_activeWorkMode == null || _dockLayout == null) return;

            var slot = _activeWorkMode.ModuleSlots.FirstOrDefault(s => s.ModuleType == moduleId);
            if (slot != null)
            {
                RemoveModuleFromLayout(_dockLayout, moduleId);
                _autoSave.NotifyChange();
                WorkspaceChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void ReturnRequiredModuleToDock(string moduleId)
        {
            _logger.LogDebug("Returning required module to dock: {ModuleId}", moduleId);

            var slot = _activeWorkMode.ModuleSlots.FirstOrDefault(s => s.ModuleType == moduleId);
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
        /// Обработчик закрытия модуля пользователем через крестик в Dock
        /// Помечает модуль как закрытый и очищает пустые контейнеры
        /// </summary>
        public void HandleModuleClosedInDock(string moduleId)
        {
            if (_isDeactivating)
            {
                _logger.LogDebug("Ignoring close during tab switch");
                return;
            }

            _logger.LogDebug("Module closed in dock: {ModuleId}", moduleId);

            if (_activeWorkMode == null) return;

            var slot = _activeWorkMode.ModuleSlots.FirstOrDefault(s => s.ModuleType == moduleId);

            if (slot != null && slot.Category == ModuleCategory.Required)
            {
                _logger.LogError("ATTEMPT TO CLOSE REQUIRED MODULE: {ModuleId}, returning to dock", moduleId);

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    ReturnRequiredModuleToDock(moduleId);
                });

                return;
            }

            if (slot != null)
            {
                slot.IsCurrentlyOpen = false;
                slot.Path = null;
                slot.IsFloating = false;

                _logger.LogDebug("Marked module as closed, ViewModel kept in Context: ModuleId={ModuleId}, InstanceId={InstanceId}",
                    moduleId, slot.InstanceId);
            }

            if (_dockLayout != null)
            {
                var dockFactory = App.Services.GetRequiredService<DockFactory>();

                _isCleaningUp = true;
                dockFactory.CleanupEmptyContainers(_dockLayout);
                _isCleaningUp = false;
            }

            WorkspaceChanged?.Invoke(this, EventArgs.Empty);
            _autoSave.SaveNowAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Получить список всех открытых модулей в текущем WorkMode
        /// Сканирует основной dock и все float окна
        /// Используется для синхронизации меню модулей с реальным UI
        /// </summary>
        public HashSet<string> GetOpenModuleIds()
        {
            var result = new HashSet<string>();

            if (_dockLayout == null)
            {
                _logger.LogDebug("GetOpenModuleIds: no layout");
                return result;
            }

            CollectModuleIdsRecursive(_dockLayout, result);

            if (_dockLayout.Windows != null && _dockLayout.Windows.Count > 0)
            {
                _logger.LogDebug("Scanning {Count} float windows", _dockLayout.Windows.Count);

                foreach (var window in _dockLayout.Windows)
                {
                    if (window.Layout != null)
                    {
                        CollectModuleIdsRecursive(window.Layout, result);
                    }
                }
            }

            _logger.LogDebug("Found {Count} open modules: {Modules}", result.Count, string.Join(", ", result));
            return result;
        }

        /// <summary>
        /// Рекурсивно собрать ID всех открытых модулей из layout
        /// </summary>
        private void CollectModuleIdsRecursive(IDockable dockable, HashSet<string> result)
        {
            if (dockable is Document document && document.Id != null)
            {
                var moduleId = document.Id.Replace("Module_", "");
                result.Add(moduleId);
            }

            if (dockable is IDock dock && dock.VisibleDockables != null)
            {
                foreach (var child in dock.VisibleDockables)
                {
                    CollectModuleIdsRecursive(child, result);
                }
            }
        }

        public async Task SaveWorkspaceAsync()
        {
            _logger.LogDebug("Saving workspace");
            await _autoSave.SaveNowAsync();
        }

        public void Dispose()
        {
            _logger.LogDebug("Disposing");

            _autoSave.Stop();

            CloseAllFloatWindows();

            DisposeAllModules();

            UnsubscribeFromDockEvents();

            _subscriptions.ForEach(s => s.Dispose());
            _subscriptions.Clear();

            _logger.LogDebug("Disposed");
        }

        private void CloseCurrentModules()
        {
            _logger.LogDebug("Closing current modules");

            foreach (var slot in _activeWorkMode.ModuleSlots)
            {
                if (!string.IsNullOrEmpty(slot.InstanceId) && slot.IsCurrentlyOpen)
                {
                    _tab.ModuleContext.RemoveModule(slot.InstanceId);
                    slot.IsCurrentlyOpen = false;
                    _logger.LogDebug("Closed module: ModuleId={ModuleId}, InstanceId preserved: {InstanceId}",
                        slot.ModuleType, slot.InstanceId);
                }
            }
        }

        private void DisposeAllModules()
        {
            _logger.LogDebug("Disposing all modules");

            foreach (var workMode in _availableWorkModes)
            {
                foreach (var slot in workMode.ModuleSlots)
                {
                    if (!string.IsNullOrEmpty(slot.InstanceId))
                    {
                        _tab.ModuleContext.RemoveModule(slot.InstanceId);
                        _logger.LogDebug("Disposed module: ModuleId={ModuleId}, InstanceId={InstanceId}",
                            slot.ModuleType, slot.InstanceId);
                    }
                }
            }
        }

        private void ClearAllModulesFromContext()
        {
            _logger.LogDebug("Clearing ALL modules from ProjectModuleContext");

            var allModules = _tab.ModuleContext.GetAllModules().ToList();

            foreach (var module in allModules)
            {
                _tab.ModuleContext.RemoveModule(module.InstanceId);
                _logger.LogDebug("Removed module from Context: {ModuleId}, InstanceId: {InstanceId}",
                    module.Metadata.ModuleId, module.InstanceId);
            }

            _logger.LogDebug("Cleared {Count} modules from Context", allModules.Count);
        }

        private void CloseAllFloatWindows()
        {
            _logger.LogDebug("Closing float windows...");
            _logger.LogDebug("Float windows before close: {Count}", _dockLayout?.Windows?.Count ?? 0);

            if (_dockLayout?.Windows == null || _dockLayout.Windows.Count == 0)
            {
                _logger.LogDebug("No float windows to close");
                return;
            }

            var windowsToClose = _dockLayout.Windows.ToList();

            foreach (var window in windowsToClose)
            {
                if (window.Host is HostWindow hostWindow)
                {
                    _logger.LogDebug("Closing float window: {WindowId}", window.Id);
                    hostWindow.Exit();
                }
            }

            _dockLayout.Windows.Clear();

            _logger.LogDebug("Float windows closed, checking if any remain...");
            _logger.LogDebug("Remaining windows count: {Count}", _dockLayout.Windows.Count);
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

        /// <summary>
        /// Подписаться на события Dock для отслеживания изменений
        /// Обновляет Path при drag-and-drop
        /// </summary>
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

                        foreach (var item in e.NewItems)
                        {
                            if (item is IDockWindow dockWindow && dockWindow.Layout != null)
                            {
                                SubscribeToFloatWindowEvents(dockWindow);
                            }
                        }

                        _autoSave.NotifyChange();
                    }

                    if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems != null)
                    {
                        foreach (var item in e.OldItems)
                        {
                            if (item is Document document && document.Id != null)
                            {
                                var moduleId = document.Id.Replace("Module_", "");

                                bool isStillInLayout = FindDocumentInLayout(_dockLayout, document.Id);
                                bool isBeingAdded = _dockFactory.IsModuleBeingAdded(moduleId);
                                bool isInFloatWindow = IsDocumentInFloatWindows(document.Id);

                                _logger.LogDebug("RootDock.Windows Remove: {ModuleId}, InLayout={InLayout}, BeingAdded={BeingAdded}, InFloat={InFloat}",
                                    moduleId, isStillInLayout, isBeingAdded, isInFloatWindow);

                                if (!isStillInLayout && !isBeingAdded && !isInFloatWindow)
                                {
                                    _logger.LogDebug("Document really closed: {ModuleId}", moduleId);
                                    HandleModuleClosedInDock(moduleId);
                                }
                                else
                                {
                                    if (isBeingAdded)
                                    {
                                        _logger.LogDebug("Document being added: {ModuleId}", moduleId);
                                    }
                                    else if (isInFloatWindow)
                                    {
                                        _logger.LogDebug("Document still in float window: {ModuleId}", moduleId);
                                    }
                                    else
                                    {
                                        _logger.LogDebug("Document moved, not closed: {ModuleId}", moduleId);
                                    }
                                }
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

                if (rootDock.Windows != null)
                {
                    foreach (var window in rootDock.Windows)
                    {
                        SubscribeToFloatWindowEvents(window);
                    }
                }
            }

            if (dockable is INotifyPropertyChanged notifyProperty)
            {
                PropertyChangedEventHandler handler = (sender, e) =>
                {
                    _logger.LogDebug("PropertyChanged: {PropertyName} for {DockId}", e.PropertyName, dockable.Id);

                    if (e.PropertyName == "Proportion" || e.PropertyName == nameof(IDock.ActiveDockable))
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
                    if (_isCleaningUp)
                        return;

                    _autoSave.NotifyChange();

                    if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
                    {
                        foreach (var item in e.NewItems)
                        {
                            if (item is IDockable newDockable)
                            {
                                SubscribeToDockEvents(newDockable);
                            }
                        }
                    }

                    if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems != null)
                    {
                        foreach (var item in e.OldItems)
                        {
                            if (item is Document document && document.Id != null)
                            {
                                var moduleId = document.Id.Replace("Module_", "");

                                _logger.LogDebug("VisibleDockables Remove: {ModuleId} from Dock={DockId}", moduleId, dock.Id);

                                bool isStillInLayout = FindDocumentInLayout(_dockLayout, document.Id);
                                bool isBeingAdded = _dockFactory.IsModuleBeingAdded(moduleId);
                                bool isInFloatWindow = IsDocumentInFloatWindows(document.Id);

                                _logger.LogDebug("Remove checks: {ModuleId}, InLayout={InLayout}, BeingAdded={BeingAdded}, InFloat={InFloat}",
                                    moduleId, isStillInLayout, isBeingAdded, isInFloatWindow);

                                if (!isStillInLayout && !isBeingAdded && !isInFloatWindow)
                                {
                                    _logger.LogDebug("Document really closed: {ModuleId}", moduleId);
                                    HandleModuleClosedInDock(moduleId);
                                }
                                else
                                {
                                    if (isInFloatWindow)
                                    {
                                        _logger.LogDebug("Document moved to float window: {ModuleId}", moduleId);
                                        var slot = _activeWorkMode.ModuleSlots.FirstOrDefault(sl => sl.ModuleType == moduleId);
                                        if (slot != null)
                                        {
                                            slot.IsFloating = true;
                                            _logger.LogDebug("Set IsFloating=true for: {ModuleId}", moduleId);
                                            _autoSave.SaveNowAsync().ConfigureAwait(false);
                                        }
                                    }
                                    else if (isBeingAdded)
                                    {
                                        _logger.LogDebug("Document being added: {ModuleId}", moduleId);
                                    }
                                    else
                                    {
                                        _logger.LogDebug("Document moved, not closed: {ModuleId}", moduleId);
                                    }
                                }
                            }
                        }
                    }
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

        /// <summary>
        /// Подписаться на события float окна для автоматического закрытия пустых окон
        /// </summary>
        private void SubscribeToFloatWindowEvents(IDockWindow dockWindow)
        {
            if (dockWindow.Layout == null)
                return;

            var floatDock = FindDocumentDockInFloatWindow(dockWindow.Layout);
            if (floatDock?.VisibleDockables is INotifyCollectionChanged floatObservable)
            {
                NotifyCollectionChangedEventHandler handler = (s, e) =>
                {
                    if (_isCleaningUp)
                        return;

                    if (e.Action == NotifyCollectionChangedAction.Remove ||
                        e.Action == NotifyCollectionChangedAction.Replace ||
                        e.Action == NotifyCollectionChangedAction.Reset)
                    {
                        var remainingDocuments = floatDock.VisibleDockables?
                            .OfType<Document>()
                            .Count() ?? 0;

                        _logger.LogDebug("Float window {WindowId} documents count: {Count}", dockWindow.Id, remainingDocuments);

                        if (remainingDocuments == 0)
                        {
                            _logger.LogDebug("Float window {WindowId} is empty, closing", dockWindow.Id);

                            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            {
                                if (dockWindow.Host is HostWindow hostWindow)
                                {
                                    hostWindow.Exit();
                                }

                                if (_dockLayout?.Windows != null && _dockLayout.Windows.Contains(dockWindow))
                                {
                                    _dockLayout.Windows.Remove(dockWindow);
                                    _logger.LogDebug("Float window {WindowId} removed from collection", dockWindow.Id);
                                }
                            });
                        }
                    }
                };

                floatObservable.CollectionChanged += handler;

                var subscription = Disposable.Create(() =>
                {
                    floatObservable.CollectionChanged -= handler;
                });

                _subscriptions.Add(subscription);

                _logger.LogDebug("Subscribed to float window events: {WindowId}", dockWindow.Id);
            }
        }

        /// <summary>
        /// Найти DocumentDock в float окне
        /// </summary>
        private DocumentDock? FindDocumentDockInFloatWindow(IDock? layout)
        {
            if (layout == null) return null;

            if (layout is DocumentDock dd)
                return dd;

            if (layout is IRootDock rootDock && rootDock.VisibleDockables != null)
            {
                foreach (var child in rootDock.VisibleDockables)
                {
                    if (child is DocumentDock docDock)
                        return docDock;
                }
            }

            return null;
        }

        /// <summary>
        /// Найти контейнер в котором находится документ
        /// </summary>
        private IDock? FindContainerForDocument(IDockable dockable, string documentId)
        {
            if (dockable is IDock dock && dock.VisibleDockables != null)
            {
                if (dock.VisibleDockables.Any(d => d.Id == documentId))
                {
                    return dock;
                }

                foreach (var child in dock.VisibleDockables)
                {
                    var found = FindContainerForDocument(child, documentId);
                    if (found != null)
                        return found;
                }
            }

            return null;
        }

        private bool IsDocumentInFloatWindows(string documentId)
        {
            if (_dockLayout?.Windows == null) return false;

            foreach (var window in _dockLayout.Windows)
            {
                if (window.Layout != null && FindDocumentInLayout(window.Layout, documentId))
                {
                    _logger.LogDebug("Found document in float window: {DocumentId}", documentId);
                    return true;
                }
            }

            return false;
        }

        private bool FindDocumentInLayout(IDockable dockable, string documentId)
        {
            if (dockable is IDock dock && dock.VisibleDockables != null)
            {
                if (dock.VisibleDockables.Any(d => d.Id == documentId))
                {
                    return true;
                }

                foreach (var child in dock.VisibleDockables)
                {
                    if (FindDocumentInLayout(child, documentId))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private void UnsubscribeFromDockEvents()
        {
            _logger.LogDebug("Unsubscribing from events");
            _subscriptions.ForEach(s => s.Dispose());
            _subscriptions.Clear();
        }

        private string? FindExistingInstanceIdInWorkModes(string moduleId)
        {
            _logger.LogDebug("Searching for existing InstanceId for module: {ModuleId}", moduleId);

            foreach (var workMode in _availableWorkModes)
            {
                var slot = workMode.ModuleSlots?.FirstOrDefault(s => s.ModuleType == moduleId);
                if (slot?.InstanceId != null)
                {
                    _logger.LogDebug("Found InstanceId in WorkMode '{WorkMode}': {InstanceId}",
                        workMode.Title, slot.InstanceId);
                    return slot.InstanceId;
                }
            }

            _logger.LogDebug("No existing InstanceId found, will create new");
            return null;
        }

        public void Activate()
        {
            _logger.LogDebug("Activating workspace");

            UnsubscribeFromDockEvents();

            if (!string.IsNullOrEmpty(_projectPath))
            {
                var projectWorkflow = App.Services.GetRequiredService<IProjectWorkflow>();
                var fileStorage = projectWorkflow.GetFileStorageForProject(_projectPath);

                if (fileStorage != null)
                {
                    var workModeConfigService = App.Services.GetRequiredService<IWorkModeConfigurationService>();
                    var loadedWorkModes = workModeConfigService.LoadConfiguration(_tab.GetProject().Type, fileStorage);

                    if (loadedWorkModes != null && loadedWorkModes.Count > 0)
                    {
                        _availableWorkModes = loadedWorkModes;
                        _activeWorkMode = loadedWorkModes.FirstOrDefault(w => w.IsActive) ?? loadedWorkModes.First();
                        _workModeService.InitializeWorkModes(_tab.GetProject().Type, loadedWorkModes);
                        _logger.LogDebug("Reloaded WorkModes from workspace.json: {Count} WorkModes", loadedWorkModes.Count);
                    }
                    else
                    {
                        _logger.LogWarning("Failed to reload WorkModes, using existing");
                    }
                }
            }

            EnsureAllModulesCreated();

            _dockLayout = _dockFactory.CreateLayout(_activeWorkMode, _tab);

            _logger.LogDebug("Layout created, checking float windows...");
            if (_dockLayout?.Windows != null && _dockLayout.Windows.Count > 0)
            {
                _logger.LogDebug("Float windows created: {Count}", _dockLayout.Windows.Count);
                foreach (var window in _dockLayout.Windows)
                {
                    _logger.LogDebug("Float window: {Id}, HasHost: {HasHost}", window.Id, window.Host != null);
                }
            }
            else
            {
                _logger.LogDebug("No float windows in new layout");
            }

            SubscribeToDockEvents(_dockLayout!);

            if (!string.IsNullOrEmpty(_projectPath))
            {
                _autoSave.Start(_projectPath, _tab.GetProject());
                _logger.LogDebug("AutoSave started for: {ProjectPath}", _projectPath);
            }

            _logger.LogDebug("Workspace activated - Layout created from saved configuration");
        }

        public void Deactivate()
        {
            _logger.LogDebug("Deactivating workspace");

            _isDeactivating = true;

            try
            {
                _logger.LogDebug("Saving workspace.json BEFORE stopping AutoSave");

                try
                {
                    _autoSave.SaveNowAsync().Wait();
                    _logger.LogDebug("Workspace saved successfully");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to save workspace during deactivation");
                }

                _logger.LogDebug("NOW stopping AutoSave");
                _autoSave.Stop();

                _logger.LogDebug("Now closing float windows...");
                if (_dockLayout?.Windows != null)
                {
                    _logger.LogDebug("Float windows before close: {Count}", _dockLayout.Windows.Count);
                }

                CloseAllFloatWindows();

                _logger.LogDebug("Float windows closed successfully");

                ClearAllModulesFromContext();

                UnsubscribeFromDockEvents();

                _dockLayout = null!;

                _logger.LogDebug("Layout cleared");
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

        private void ClearLayoutRecursive(IDockable dockable)
        {
            if (dockable is IDock dock && dock.VisibleDockables != null)
            {
                var documents = dock.VisibleDockables.OfType<Document>().ToList();

                foreach (var doc in documents)
                {
                    dock.VisibleDockables.Remove(doc);
                    _logger.LogDebug("Removed document from layout: {DocumentId}", doc.Id);
                }

                if (dock.VisibleDockables.Count == 0)
                {
                    dock.ActiveDockable = null!;
                }
                else
                {
                    dock.ActiveDockable = dock.VisibleDockables.FirstOrDefault();
                }

                foreach (var child in dock.VisibleDockables.ToList())
                {
                    ClearLayoutRecursive(child);
                }
            }
        }

        /// <summary>
        /// Рекурсивно собрать ID всех документов из Dock структуры
        /// Поддерживает обход как обычных Dock, так и Float окон
        /// </summary>
        private void CollectDocumentIds(IDockable dockable, HashSet<string> documentIds)
        {
            if (dockable is IDock dock && dock.VisibleDockables != null)
            {
                foreach (var item in dock.VisibleDockables)
                {
                    if (item is Document doc && doc.Id != null)
                    {
                        documentIds.Add(doc.Id);
                    }

                    CollectDocumentIds(item, documentIds);
                }
            }
        }

        /// <summary>
        /// Создать ВСЕ модули из workspace.json ПЕРЕД построением UI
        /// Гарантирует что все модули существуют в Context
        /// </summary>
        private void EnsureAllModulesCreated()
        {
            _logger.LogDebug("EnsureAllModulesCreated: Starting...");

            var openSlots = _activeWorkMode.ModuleSlots
                .Where(s => s.IsCurrentlyOpen)
                .ToList();

            _logger.LogDebug("Found {Count} open modules in workspace.json", openSlots.Count);

            foreach (var slot in openSlots)
            {
                // Проверяем что модуль УЖЕ создан
                var exists = _tab.ModuleContext.GetModule(slot.InstanceId);
                if (exists != null)
                {
                    _logger.LogDebug("Module already exists: {ModuleId}, InstanceId: {InstanceId}",
                        slot.ModuleType, slot.InstanceId);
                    continue;
                }

                // Создаём модуль
                _logger.LogDebug("Creating module: {ModuleId}, InstanceId: {InstanceId}",
                    slot.ModuleType, slot.InstanceId);

                var module = _tab.ModuleContext.CreateModule(slot.ModuleType, slot.InstanceId);

                if (module != null)
                {
                    module.Context = _tab.Context;
                    _logger.LogDebug("Created and assigned context: {ModuleId}", slot.ModuleType);
                }
                else
                {
                    _logger.LogError("Failed to create module: {ModuleId}", slot.ModuleType);
                }
            }

            _logger.LogDebug("EnsureAllModulesCreated: Complete");
        }
    }
}