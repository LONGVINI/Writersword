using Dock.Model.Controls;
using Dock.Model.Core;
using Microsoft.Extensions.DependencyInjection;
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
using Writersword.Src.Core.Interfaces.Workspace;
using Writersword.Src.Infrastructure.Dock;
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
        private readonly DocumentTabViewModel _tab;
        private readonly string _projectPath;
        private readonly DockFactory _dockFactory;
        private readonly IWorkspaceAutoSaveService _autoSave;

        private WorkMode _activeWorkMode;
        private IRootDock _dockLayout;
        private List<WorkMode> _availableWorkModes;
        private readonly List<IDisposable> _subscriptions;

        /// <summary>
        /// Событие изменения workspace
        /// </summary>
        public event EventHandler? WorkspaceChanged;

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
            _tab = tab;
            _projectPath = projectPath;
            _dockFactory = dockFactory;
            _autoSave = autoSave;
            _availableWorkModes = loadedWorkModes;
            _subscriptions = new List<IDisposable>();

            _activeWorkMode = loadedWorkModes.FirstOrDefault(w => w.IsActive)
                              ?? loadedWorkModes.First();

            _dockLayout = _dockFactory.CreateLayout(_activeWorkMode, _tab);

            if (!string.IsNullOrEmpty(_projectPath))
            {
                _autoSave.Start(_projectPath, tab.GetProject());
                SubscribeToDockEvents(_dockLayout);
            }

            Console.WriteLine($"[WorkspaceController] Created for: {tab.Title}");
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

                Console.WriteLine($"[WorkspaceController] Returned {filteredModules.Count}/{allModules.Count} modules for WorkMode: {_activeWorkMode.Title}");
                return filteredModules;
            }

            Console.WriteLine($"[WorkspaceController] No ActiveWorkMode, returning all {allModules.Count} modules");
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
            Console.WriteLine($"[WorkspaceController] Refreshed {modules.Count()} modules from context");
        }

        /// <summary>
        /// Переключить WorkMode
        /// </summary>
        public void SwitchWorkMode(WorkMode newMode)
        {
            Console.WriteLine($"[WorkspaceController] Switching WorkMode: {_activeWorkMode.Title} -> {newMode.Title}");

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

            Console.WriteLine($"[WorkspaceController] WorkMode switched successfully");
        }

        /// <summary>
        /// Добавить модуль динамически
        /// </summary>
        public void AddModule(string moduleId)
        {
            Console.WriteLine($"[WorkspaceController] Adding module: {moduleId}");

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
                Console.WriteLine($"[WorkspaceController] No visible modules - recreating layout");
                var newLayout = _dockFactory.CreateLayout(_activeWorkMode);
                UnsubscribeFromDockEvents();
                _dockLayout = newLayout;
                SubscribeToDockEvents(_dockLayout);
            }
            else
            {
                Console.WriteLine($"[WorkspaceController] Adding module dynamically");
                _dockFactory.InsertModuleByPreference(_dockLayout, existingSlot);
            }

            _autoSave.NotifyChange();
            WorkspaceChanged?.Invoke(this, EventArgs.Empty);

            Console.WriteLine($"[WorkspaceController] Module added successfully");
        }

        /// <summary>
        /// Удалить модуль
        /// </summary>
        public void RemoveModule(string moduleId)
        {
            Console.WriteLine($"[WorkspaceController] Removing module: {moduleId}");

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
            Console.WriteLine($"[WorkspaceController] Returning required module to dock: {moduleId}");

            var slot = _activeWorkMode.ModuleSlots.FirstOrDefault(s => s.ModuleId == moduleId);
            if (slot == null)
            {
                Console.WriteLine($"[WorkspaceController] Module slot not found: {moduleId}");
                return;
            }

            slot.IsFloating = false;
            Console.WriteLine($"[WorkspaceController] Reset IsFloating for: {moduleId}");

            UnsubscribeFromDockEvents();
            _dockLayout = _dockFactory.CreateLayout(_activeWorkMode, _tab);
            SubscribeToDockEvents(_dockLayout);

            _autoSave.NotifyChange();
            WorkspaceChanged?.Invoke(this, EventArgs.Empty);

            Console.WriteLine($"[WorkspaceController] Module returned successfully");
        }

        /// <summary>
        /// Обработчик закрытия модуля в Dock
        /// </summary>
        public void HandleModuleClosedInDock(string moduleId)
        {
            Console.WriteLine($"[WorkspaceController] Module closed in dock: {moduleId}");

            if (_activeWorkMode == null) return;

            var slot = _activeWorkMode.ModuleSlots.FirstOrDefault(s => s.ModuleId == moduleId);
            if (slot != null && slot.IsFloating)
            {
                slot.IsFloating = false;
                slot.ContainerId = null;
                Console.WriteLine($"[WorkspaceController] Reset floating flag for: {moduleId}");
            }

            _autoSave.NotifyChange();
            WorkspaceChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Сохранить workspace
        /// </summary>
        public async Task SaveWorkspaceAsync()
        {
            Console.WriteLine($"[WorkspaceController] Saving workspace");
            await _autoSave.SaveNowAsync();
        }

        /// <summary>
        /// Очистка ресурсов
        /// </summary>
        public void Dispose()
        {
            Console.WriteLine($"[WorkspaceController] Disposing");

            _autoSave.SaveNowAsync().Wait();
            _autoSave.Stop();

            CloseAllFloatWindows();

            DisposeCurrentModules();

            UnsubscribeFromDockEvents();

            _subscriptions.ForEach(s => s.Dispose());
            _subscriptions.Clear();

            Console.WriteLine($"[WorkspaceController] Disposed");
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
            Console.WriteLine($"[WorkspaceController] Searching for: {documentId}");
            RemoveDocumentRecursive(rootDock, documentId);
        }

        private bool RemoveDocumentRecursive(IDockable dockable, string documentId)
        {
            if (dockable is IDock dock && dock.VisibleDockables != null)
            {
                var document = dock.VisibleDockables.FirstOrDefault(d => d.Id == documentId);
                if (document != null)
                {
                    Console.WriteLine($"[WorkspaceController] Found document, removing from {dock.Id}");
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
            Console.WriteLine($"[WorkspaceController] Subscribing to events: {dockable.Id}");

            if (dockable is IRootDock rootDock && rootDock.Windows is INotifyCollectionChanged windowsObservable)
            {
                NotifyCollectionChangedEventHandler windowsHandler = (s, e) =>
                {
                    if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
                    {
                        Console.WriteLine($"[WorkspaceController] Float window created");
                        _autoSave.NotifyChange();
                    }

                    if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems != null)
                    {
                        foreach (var item in e.OldItems)
                        {
                            if (item is IDockWindow dockWindow)
                            {
                                Console.WriteLine($"[WorkspaceController] Float window closed: {dockWindow.Id}");
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
            Console.WriteLine($"[WorkspaceController] Unsubscribing from events");
            _subscriptions.ForEach(s => s.Dispose());
            _subscriptions.Clear();
        }
    }
}