using Avalonia.Threading;
using Dock.Model.Avalonia.Controls;
using Document = Dock.Model.Avalonia.Controls.Document;
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
using Writersword.Core.Interfaces.Services.Storage;
using Writersword.Core.Interfaces.WorkFlows;
using Writersword.Core.Interfaces.WorkModes;
using Writersword.Core.Interfaces.Workspace;
using Writersword.Core.Models.WorkModes;
using Writersword.Infrastructure.Dock;
using Writersword.Infrastructure.Services.WorkModes;
using Writersword.Modules.Common;
using Writersword.ViewModels;

namespace Writersword.Infrastructure.Workspace
{
    /// <summary>
    /// Контроллер workspace для вкладки документа.
    /// Управляет WorkModes, модулями и Dock layout.
    /// Модули создаются исключительно в DockFactory при построении layout.
    /// Dock.Avalonia управляет своим состоянием самостоятельно.
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
        private bool _needsFullLayoutRefresh = false;

        public event EventHandler? WorkspaceChanged;

        private readonly IProjectFileStorage? _fileStorage;

        public IWorkModeService GetWorkModeService() => _workModeService;

        public WorkspaceController(
             DocumentTabViewModel tab,
             string projectPath,
             List<WorkMode> loadedWorkModes,
             DockFactory dockFactory,
             IWorkspaceAutoSaveService autoSave,
             IProjectFileStorage? fileStorage = null)
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
            _fileStorage = fileStorage;

            _logger.LogDebug("Created for: {TabTitle}", tab.Title);
            _logger.LogDebug("Total WorkModes: {TotalCount}, Active: {ActiveTitle}",
                _availableWorkModes.Count, _activeWorkMode.Title);
        }

        public IRootDock GetCurrentLayout() => _dockLayout;

        public List<WorkMode> GetAvailableWorkModes() => _availableWorkModes;

        public WorkMode GetActiveWorkMode() => _activeWorkMode;

        /// <summary>
        /// Получить список активных модулей текущего WorkMode.
        /// Сканирует реальный UI (dock + float окна).
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
        /// Обновить все модули из контекста.
        /// </summary>
        public void RefreshModulesFromContext()
        {
            var modules = GetActiveModules();
            foreach (var module in modules)
                module.RefreshFromContext();

            _logger.LogDebug("Refreshed {Count} modules from context", modules.Count);
        }

        /// <summary>
        /// Переключить WorkMode.
        /// Данные модулей сохраняются в project.ModulesData (память) и кеш удаляется ДО CreateLayout.
        /// CreateLayout читает из project.ModulesData как fallback — нет зависимости от файла кеша.
        /// Async-запись кеша происходит ПОСЛЕ CreateLayout — нет race condition с LoadCacheWithSession.
        /// UI-поток не блокируется — фокус TextBox не сбрасывается накопленными событиями.
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

            // Переменные для async-сохранения кеша объявлены здесь чтобы быть
            // доступными после CreateLayout — Task.Run должен быть строго после него.
            string? pendingCachePath = null;
            string? pendingCacheProjectId = null;

            // Сбор данных модулей нужен только если новый WorkMode реально создаст
            // модуль с нуля: живые модули переиспользуются как есть (DockFactory),
            // а их состояние свежее любого кеша. Без этой проверки каждый переклик
            // воркмода собирал данные всех модулей.
            bool willCreateNewModules = newMode.ModuleSlots
                .Any(s => _tab.ModuleContext.GetModule(s.ModuleType) == null);

            var allModulesNow = _tab.ModuleContext.GetAllModules();
            if (allModulesNow.Count > 0 && willCreateNewModules)
            {
                var cacheService = App.Services.GetRequiredService<IZipCacheService>();

                // Удаляем устаревший кеш чтобы отложенные загрузки новых модулей
                // читали project.ModulesData, а не файл с данными на 10 секунд старше.
                // Сам сбор данных модулей выполняется ЦЕЛИКОМ в фоновой задаче после
                // CreateLayout: сериализация сотен персонажей или целого документа
                // на UI-потоке блокировала каждое переключение воркмода на сотни мс.
                // Тяжёлые модули (IStateSnapshotModule) внутри GetCustomData сами
                // прыгают на UI-поток только за быстрым снимком — как при периодическом
                // автосохранении CacheUpdateService, это тот же проверенный путь.
                // Файл убирается в резервную копию, а не удаляется: до записи нового
                // кеша проходит заметное время, и авария в этом окне не должна
                // оставлять проект вообще без точки восстановления.
                cacheService.MoveCacheToBackup(_projectPath);

                pendingCachePath = _projectPath;
                pendingCacheProjectId = _tab.GetProject().Id;
            }

            CloseAllFloatWindows();

            _activeWorkMode.IsActive = false;
            newMode.IsActive = true;
            _activeWorkMode = newMode;

            // clearDataContext: false — модули паркуются живыми и их вью переиспользуются
            // при возврате в воркмод; разрыв биндингов больших вью занимал секунды UI-потока.
            _dockFactory.DetachViewsFromLayout(_dockLayout, clearDataContext: false);

            // Модули, которых нет в новом WorkMode, НЕ уничтожаются — паркуются живыми
            // в контексте вкладки. Возврат в прежний WorkMode переиспользует их мгновенно,
            // без десериализации данных и повторной вёрстки. Память: модули живут до
            // закрытия вкладки — осознанная цена за мгновенное переключение.

            // CreateLayout читает кеш — к этому моменту он уже удалён,
            // поэтому LoadCacheWithSession вернёт null и создаваемые с нуля модули
            // загрузятся из project.ModulesData. Их записи в ModulesData собранными
            // данными не затрагиваются (собираются только живые модули, а они
            // переиспользуются без чтения данных), поэтому отложенное обновление
            // словаря в фоновой задаче ниже на CreateLayout не влияет.
            var layoutStopwatch = System.Diagnostics.Stopwatch.StartNew();
            _dockLayout = _dockFactory.CreateLayout(newMode, _tab);
            layoutStopwatch.Stop();
            if (layoutStopwatch.ElapsedMilliseconds > 50)
            {
                _logger.LogWarning(
                    "WorkMode switch CreateLayout took {ElapsedMs}ms on UI thread for: {Title}",
                    layoutStopwatch.ElapsedMilliseconds, newMode.Title);
            }

            // Async-сбор данных модулей и сохранение СТРОГО ПОСЛЕ CreateLayout — здесь нет race condition.
            // До CreateLayout Task.Run и LoadCacheWithSession конкурировали за .wsasd → IOException.
            // GetAwaiter().GetResult() здесь замораживал UI на 100-500мс и ломал фокус TextBox.
            if (pendingCachePath != null && pendingCacheProjectId != null)
            {
                var stateCollector = App.Services.GetRequiredService<IModuleStateCollectorService>();
                var cacheServiceForSave = App.Services.GetRequiredService<IZipCacheService>();
                var modulesToCollect = allModulesNow;
                var path = pendingCachePath;
                var pid = pendingCacheProjectId;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Сбор данных модулей на фоновом потоке: сериализация сотен
                        // персонажей и целых документов не касается UI-потока. Модули
                        // с IStateSnapshotModule внутри GetCustomData прыгают на UI
                        // только за быстрым снимком модели.
                        var (cd, sd) = stateCollector.CollectAllData(modulesToCollect);

                        if (cd.Count == 0)
                        {
                            _logger.LogWarning("No module data collected after WorkMode switch — cache not updated");
                            return;
                        }

                        // Обновляем project.ModulesData строго на UI-потоке: словарь
                        // читается кодом UI (fallback в DockFactory при создании модулей),
                        // запись с фонового потока создала бы гонку.
                        var project = _tab.GetProject();
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            foreach (var kvp in cd)
                                project.ModulesData[kvp.Key] = kvp.Value;
                        });

                        await cacheServiceForSave.SaveCacheAsync(path, pid, cd, sd);
                        _logger.LogDebug("Cache saved async after WorkMode switch: {Count} modules", cd.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Async cache save failed after WorkMode switch");
                    }
                });
            }

            _dockFactory.OnModuleClosed = (moduleType) =>
            {
                Dispatcher.UIThread.Post(() => HandleModuleClosedInDock(moduleType));
            };

            _autoSave.NotifyChange();
            WorkspaceChanged?.Invoke(this, EventArgs.Empty);

            _logger.LogDebug("WorkMode switched successfully");
        }

        /// <summary>
        /// Добавить модуль в текущий WorkMode.
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

                _logger.LogDebug("Created new slot for {moduleType}: Category={Category}, IsCloseable={IsCloseable}",
                    moduleType, newSlot.Category, newSlot.IsCloseable);

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
        /// Удалить модуль из текущего WorkMode.
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
        /// Вернуть Required модуль обратно в dock.
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
        /// Обработчик реального закрытия модуля — вызывается из DockFactory.CloseDockable.
        /// </summary>
        public void HandleModuleClosedInDock(string moduleType)
        {
            if (_isDeactivating) return;

            _logger.LogDebug("Module closed in dock: {moduleType}", moduleType);

            if (_activeWorkMode == null) return;

            var slot = _activeWorkMode.ModuleSlots.FirstOrDefault(s => s.ModuleType == moduleType);

            _logger.LogDebug("HandleModuleClosed: {moduleType}, slot={SlotFound}, category={Category}, isCloseable={IsCloseable}",
                moduleType,
                slot != null ? "found" : "NULL",
                slot?.Category.ToString() ?? "N/A",
                slot?.IsCloseable.ToString() ?? "N/A");

            if (slot != null && slot.Category == ModuleCategory.Required)
            {
                _logger.LogError("Attempt to close Required module: {moduleType}", moduleType);
                Dispatcher.UIThread.Post(() => ReturnRequiredModuleToDock(moduleType));
                return;
            }

            // Снимаем данные с модуля и сохраняем в project.ModulesData ДО удаления.
            // Иначе при переключении в другой WorkMode с тем же модулем данные будут потеряны.
            var module = _tab.ModuleContext.GetModule(moduleType);
            if (module != null)
            {
                try
                {
                    var customData = module.GetCustomData();
                    if (customData != null)
                    {
                        var project = _tab.GetProject();
                        project.ModulesData[moduleType] = customData;
                        _logger.LogDebug("Module data saved before close: {moduleType}", moduleType);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to save module data before close: {moduleType}", moduleType);
                }
            }

            _tab.ModuleContext.RemoveModule(moduleType);

            // Пустые DocumentDock и ProportionalDock с 12.1 убирает сам Dock
            // через CollapseDock. Наша ручная уборка это дублировала и падала:
            // удаление из VisibleDockables у прицепленной раскладки синхронно
            // разбирает контейнер, разбор гасит DataContext, и вкладочная полоса
            // переустанавливает выделение по уже опустевшему списку.
            _needsFullLayoutRefresh = true;

            _autoSave.NotifyChange();
            WorkspaceChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Получить список всех открытых модулей в текущем WorkMode.
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
        /// Сохранить workspace асинхронно.
        /// </summary>
        public async Task SaveWorkspaceAsync()
        {
            _logger.LogDebug("Saving workspace");
            await _autoSave.SaveNowAsync();
        }

        /// <summary>
        /// Активировать workspace.
        /// Конфиг уже загружен и передан в конструктор — повторная загрузка не нужна.
        /// Перед созданием layout сбрасывает испорченный serializedDockLayout
        /// (все слоты ведут в центр при наличии нескольких модулей).
        /// </summary>
        public void Activate()
        {
            // Мягкая реактивация после Suspend: layout и модули живы, пересоздавать
            // нечего — только вернуть коллбэки и автосейв. Возврат на вкладку мгновенный.
            if (_dockLayout != null)
            {
                _logger.LogDebug("Reactivating workspace (soft, layout alive)");

                _dockFactory.AttachToLayout(_dockLayout);
                _dockFactory.OnModuleClosed = (moduleType) =>
                {
                    Dispatcher.UIThread.Post(() => HandleModuleClosedInDock(moduleType));
                };

                if (!string.IsNullOrEmpty(_projectPath))
                    _autoSave.Start(_projectPath, _tab.GetProject());

                return;
            }

            _logger.LogDebug("Activating workspace");

            ResetDegenerateSerializedLayoutIfNeeded(_activeWorkMode);

            _dockLayout = _dockFactory.CreateLayout(_activeWorkMode, _tab);

            _dockFactory.OnModuleClosed = (moduleType) =>
            {
                Dispatcher.UIThread.Post(() => HandleModuleClosedInDock(moduleType));
            };

            if (!string.IsNullOrEmpty(_projectPath))
                _autoSave.Start(_projectPath, _tab.GetProject());

            // Модули создаются отложенно (плейсхолдеры + фоновые прикрепления в очереди
            // диспетчера). Локальные настройки применяем тем же приоритетом ПОСЛЕ них:
            // очередь FIFO в рамках приоритета гарантирует, что модули уже созданы.
            if (_fileStorage != null)
            {
                var storage = _fileStorage;
                Dispatcher.UIThread.Post(
                    () => ApplyLocalSettingsToModules(storage),
                    DispatcherPriority.Background);
            }

            _logger.LogDebug("Workspace activated");
        }

        /// <summary>
        /// Мягкая приостановка workspace при уходе с вкладки: модули, вьюхи и layout
        /// остаются живыми, возврат на вкладку не требует пересоздания и повторной
        /// загрузки данных (для больших документов это секунды заморозки UI).
        /// Float-окна живут в отдельных корнях и без пересоздания layout не
        /// восстанавливаются — при их наличии выполняется полная деактивация.
        /// </summary>
        public void Suspend()
        {
            if (_dockLayout == null) return;

            if (_dockLayout.Windows != null && _dockLayout.Windows.Count > 0)
            {
                _logger.LogDebug("Suspend: float windows present — falling back to full Deactivate");
                Deactivate();
                return;
            }

            _logger.LogDebug("Suspending workspace (modules kept alive)");

            // Диагностика провисаний: Suspend выполняется синхронно на UI-потоке
            // при каждом уходе с вкладки. Замеряем каждый этап отдельно.
            var suspendStopwatch = System.Diagnostics.Stopwatch.StartNew();

            var (serializedLayout, updatedSlots) = _dockFactory.SerializeCurrentLayout(
                _dockLayout, _activeWorkMode, _tab.ModuleContext);
            if (serializedLayout != null)
            {
                _activeWorkMode.SerializedDockLayout = serializedLayout;
                _activeWorkMode.ModuleSlots = updatedSlots;
            }

            long serializeMs = suspendStopwatch.ElapsedMilliseconds;

            _autoSave.Stop();

            // Сбрасываем состояние модулей в кеш (асинхронность внутри сервиса),
            // чтобы при падении приложения на другой вкладке данные не потерялись.
            var cacheService = App.Services.GetRequiredService<ICacheUpdateService>();
            cacheService.SaveToCache();

            long cacheMs = suspendStopwatch.ElapsedMilliseconds - serializeMs;

            // Отцепляем вьюхи от презентеров текущего дерева: при возврате DockControl
            // построит новые презентеры, и RecreateAllDocumentViews переприцепит вьюхи.
            // clearDataContext: false — модули живы, вью переиспользуются при возврате;
            // разрыв и восстановление биндингов больших вью занимали секунды UI-потока.
            _dockFactory.DetachViewsFromLayout(_dockLayout, clearDataContext: false);

            suspendStopwatch.Stop();
            long detachMs = suspendStopwatch.ElapsedMilliseconds - serializeMs - cacheMs;
            if (suspendStopwatch.ElapsedMilliseconds > 50)
            {
                _logger.LogWarning(
                    "Workspace Suspend took {ElapsedMs}ms on UI thread for: {Title} " +
                    "(serializeLayout={SerializeMs}ms, scheduleCache={CacheMs}ms, detachViews={DetachMs}ms)",
                    suspendStopwatch.ElapsedMilliseconds, _tab.Title,
                    serializeMs, cacheMs, detachMs);
            }

            _logger.LogDebug("Workspace suspended");
        }

        private void ApplyLocalSettingsToModules(IProjectFileStorage storage)
        {
            var service = App.Services.GetRequiredService<ILocalSettingsStorageService>();
            var modules = GetActiveModules();

            foreach (var module in modules)
            {
                if (module is not IConfigurableModule configurable) continue;

                try
                {
                    var settings = service.Load(storage, module.moduleType, configurable.SettingsType);
                    if (settings is not null)
                    {
                        configurable.ApplyLocalSettings(settings);
                        _logger.LogDebug("Local settings applied on activate: {ModuleType}", module.moduleType);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to apply local settings for {ModuleType}", module.moduleType);
                }
            }
        }

        /// <summary>
        /// Деактивировать workspace.
        /// Сбрасывает кеш модулей, сохраняет layout, закрывает float окна, очищает модули.
        /// </summary>
        public void Deactivate()
        {
            _logger.LogDebug("Deactivating workspace");
            _isDeactivating = true;

            try
            {
                if (_dockLayout != null)
                {
                    var (serializedLayout, updatedSlots) = _dockFactory.SerializeCurrentLayout(
                        _dockLayout, _activeWorkMode, _tab.ModuleContext);

                    if (serializedLayout != null)
                    {
                        _activeWorkMode.SerializedDockLayout = serializedLayout;
                        _activeWorkMode.ModuleSlots = updatedSlots;
                        _logger.LogDebug("Serialized layout saved to memory for WorkMode: {Title}", _activeWorkMode.Title);
                    }
                }

                _autoSave.Stop();
                CloseAllFloatWindows();

                var cacheService = App.Services.GetRequiredService<ICacheUpdateService>();
                cacheService.SaveToCache();
                _logger.LogDebug("Cache flushed before deactivation");

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
        /// Освободить ресурсы.
        /// </summary>
        public void Dispose()
        {
            _logger.LogDebug("Disposing");

            _autoSave.Stop();

            var cacheService = App.Services.GetRequiredService<ICacheUpdateService>();
            cacheService.SaveToCache();
            _logger.LogDebug("Cache flushed before dispose");

            CloseAllFloatWindows();
            ClearAllModulesFromContext();

            _logger.LogDebug("Disposed");
        }

        /// <summary>
        /// Сбросить WorkMode до конфигурации по умолчанию.
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

            Deactivate();
            Activate();

            _autoSave.NotifyChange();
            WorkspaceChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Сбросить serializedDockLayout если все слоты WorkMode ведут в центр
        /// при наличии более одного слота — такой layout вырожден.
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
        /// Очистить все модули из контекста.
        /// </summary>
        private void ClearAllModulesFromContext()
        {
            var allModules = _tab.ModuleContext.GetAllModules().ToList();
            foreach (var module in allModules)
                _tab.ModuleContext.RemoveModule(module.moduleType);

            _logger.LogDebug("Cleared {Count} modules from Context", allModules.Count);
        }

        /// <summary>
        /// Очистить из контекста модули которых НЕТ в новом WorkMode.
        /// Позволяет переиспользовать общие модули (например Timer) между WorkMode.
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

        public void ReloadFromGlobalConfig(List<WorkMode> workModes)
        {
            _logger.LogDebug("Reloading workspace from global config: {Count} modes", workModes.Count);

            _isDeactivating = true;

            try
            {
                _autoSave.Stop();
                CloseAllFloatWindows();
                _dockFactory.DetachViewsFromLayout(_dockLayout);
                ClearAllModulesFromContext();
                _dockFactory.OnModuleClosed = null;
            }
            finally
            {
                _isDeactivating = false;
            }

            _availableWorkModes = workModes;
            _workModeService.InitializeWorkModes(_tab.GetProject().Type, workModes);

            _activeWorkMode = workModes.FirstOrDefault(w => w.IsActive)
                             ?? workModes.First();

            ResetDegenerateSerializedLayoutIfNeeded(_activeWorkMode);

            _dockLayout = _dockFactory.CreateLayout(_activeWorkMode, _tab);

            _dockFactory.OnModuleClosed = (moduleType) =>
            {
                Dispatcher.UIThread.Post(() => HandleModuleClosedInDock(moduleType));
            };

            if (!string.IsNullOrEmpty(_tab.FilePath))
                _autoSave.Start(_tab.FilePath, _tab.GetProject());

            _autoSave.NotifyChange();
            WorkspaceChanged?.Invoke(this, EventArgs.Empty);

            _logger.LogDebug("Workspace reloaded from global config");
        }

        public bool ConsumeNeedsFullLayoutRefresh()
        {
            if (!_needsFullLayoutRefresh) return false;
            _needsFullLayoutRefresh = false;
            return true;
        }
    }
}