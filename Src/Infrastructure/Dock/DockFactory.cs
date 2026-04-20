using Dock.Model.Avalonia;
using Dock.Model.Avalonia.Controls;
using Dock.Model.Avalonia.Core;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Serializer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using Writersword.Core.Enums;
using Writersword.Core.Interfaces.Services;
using Writersword.Core.Models.WorkModes;
using Writersword.Core.Services;
using Writersword.Core.Interfaces.WorkFlows;
using Writersword.ViewModels;

namespace Writersword.Infrastructure.Dock
{
    /// <summary>
    /// Фабрика для создания Dock элементов
    /// Использует Dock.Serializer для сохранения/загрузки структуры
    /// Document.Context хранит moduleType (строка) — уникальный ключ модуля в рамках проекта
    /// При отсутствии сериализованного layout строит дерево вручную из PreferredPosition
    /// Каждый модуль живёт в своём DocumentDock для сохранения chrome (заголовок с кнопками)
    /// </summary>
    public class DockFactory : Factory
    {
        private readonly ILogger<DockFactory> _logger;
        private readonly HashSet<string> _modulesBeingAdded = new();
        private IRootDock? _currentRootDock;
        private bool _isMoving = false;
        private IDockSerializer? _dockSerializer;

        /// <summary>
        /// Callback вызывается когда пользователь закрывает модуль через крестик в Dock
        /// Единственное надёжное место для перехвата реального закрытия (не drag)
        /// </summary>
        [Newtonsoft.Json.JsonIgnore]
        public Action<string>? OnModuleClosed { get; set; }

        /// <summary>
        /// Callback вызывается когда пользователь переключается на другой модуль в Dock
        /// </summary>
        [Newtonsoft.Json.JsonIgnore]
        public Action<string>? OnModuleFocused { get; set; }

        /// <summary>
        /// Callback вызывается после перемещения модуля когда нужно обновить DockLayout в UI.
        /// В Dock 12 изменение Content существующих Document-ов не обновляет DockControl —
        /// требуется полный пересоздание через null+reassign DockLayout.
        /// </summary>
        [Newtonsoft.Json.JsonIgnore]
        public Action? OnNeedRerender { get; set; }

        public DockFactory()
        {
            _logger = App.Services.GetService<ILogger<DockFactory>>()!;
        }

        /// <summary>
        /// Инициализация Locators
        /// </summary>
        public void Initialize()
        {
            ContextLocator = new Dictionary<string, Func<object?>>
            {
                ["Root"] = () => null
            };

            HostWindowLocator = new Dictionary<string, Func<IHostWindow?>>
            {
                [nameof(IDockWindow)] = () =>
                {
                    _logger.LogDebug("HostWindowLocator called - creating HostWindow");
                    return new HostWindow();
                }
            };

            DockableLocator = new Dictionary<string, Func<IDockable?>>();

            _logger.LogDebug("Initialized with custom HostWindow");
        }

        /// <summary>
        /// Перехват реального закрытия модуля пользователем через крестик
        /// При drag этот метод НЕ вызывается — только при реальном Close
        /// </summary>
        public override void CloseDockable(IDockable dockable)
        {
            if (dockable is Document doc && doc.Id?.StartsWith("Module_") == true)
            {
                var moduleType = doc.Id.Replace("Module_", "");
                _logger.LogDebug("CloseDockable: {moduleType}", moduleType);

                if (_isMoving)
                {
                    _logger.LogDebug("CloseDockable skipped (dock is reorganizing): {moduleType}", moduleType);
                    base.CloseDockable(dockable);
                    return;
                }

                _logger.LogDebug("CloseDockable called: {moduleType}, _isMoving={IsMoving}, CanClose={CanClose}",
    moduleType, _isMoving, doc.CanClose);

                doc.Content = null;
                base.CloseDockable(dockable);
                OnModuleClosed?.Invoke(moduleType);

                // После закрытия Dock 12 перестраивает визуальное дерево —
                // оставшиеся модули теряют ContentPresenter. Пересоздаём View-шки.
                if (_currentRootDock != null)
                {
                    var root = _currentRootDock;
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        var tab = App.Services.GetRequiredService<ITabCollection>().ActiveTab;
                        if (tab != null)
                            RecreateDocumentViews(root, tab);
                    }, Avalonia.Threading.DispatcherPriority.Loaded);
                }

            }
            else
            {
                base.CloseDockable(dockable);
            }
        }

        public override void MoveDockable(IDock sourceOwner, IDock targetOwner, IDockable sourceDockable, IDockable? targetDockable)
        {
            _isMoving = true;
            try
            {
                base.MoveDockable(sourceOwner, targetOwner, sourceDockable, targetDockable);
            }
            finally
            {
                _isMoving = false;
            }

            if (_currentRootDock != null)
            {
                var rootToNormalize = _currentRootDock;
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    () =>
                    {
                        NormalizeProportionsRecursive(rootToNormalize);
                        // В Dock 12 View нельзя переиспользовать после перемещения между
                        // ContentPresenter-ами — VisualParent остаётся на старом CP.
                        // Единственное решение — пересоздать View через module.CreateView().
                        // ViewModel остаётся той же → данные модуля не теряются.
                        var tab = App.Services.GetRequiredService<ITabCollection>().ActiveTab;
                        if (tab != null)
                            RecreateDocumentViews(rootToNormalize, tab);
                    },
                    Avalonia.Threading.DispatcherPriority.Loaded);
            }
        }

        /// <summary>
        /// Пересоздаёт View для каждого Document через module.CreateView().
        /// Используется после MoveDockable — в Dock 12 существующий View не рендерится
        /// после перемещения между DocumentDock-ами.
        /// </summary>
        private void RecreateDocumentViews(IDockable dockable, DocumentTabViewModel tab)
        {
            if (dockable is Document doc && doc.Id?.StartsWith("Module_") == true)
            {
                var moduleType = doc.Id.Replace("Module_", "");
                var module = tab.ModuleContext.GetModule(moduleType);
                if (module != null)
                {
                    var newView = module.CreateView();
                    if (newView != null)
                    {
                        doc.Content = null;
                        doc.Content = newView;
                        _logger.LogDebug("View recreated for: {moduleType}", moduleType);
                    }
                }
                return;
            }

            if (dockable is IDock dock && dock.VisibleDockables != null)
                foreach (var child in dock.VisibleDockables.ToList())
                    RecreateDocumentViews(child, tab);
        }

        /// <summary>
        /// Получить или создать сериализатор Dock
        /// </summary>
        private IDockSerializer GetSerializer()
        {
            if (_dockSerializer != null)
                return _dockSerializer;

            _logger.LogDebug("Creating Dock.Serializer");
            _dockSerializer = new DockSerializer(App.Services);
            _logger.LogDebug("Dock.Serializer created successfully");
            return _dockSerializer;
        }

        // =====================================================================
        // СОЗДАНИЕ LAYOUT
        // =====================================================================

        /// <summary>
        /// Создать layout из WorkMode
        /// При наличии SerializedDockLayout восстанавливает из него
        /// Если после восстановления Document-ов нет — fallback на PreferredPositions
        /// </summary>
        public IRootDock CreateLayout(WorkMode workMode, DocumentTabViewModel? ownerTab = null)
        {
            _logger.LogDebug("Creating layout for: {Title}", workMode.Title);

            if (!string.IsNullOrEmpty(workMode.SerializedDockLayout))
            {
                _logger.LogDebug("Attempting to restore layout from SerializedDockLayout");

                try
                {
                    var serializer = GetSerializer();
                    IRootDock? restored = null;

                    using (var stream = new System.IO.MemoryStream(
                        System.Text.Encoding.UTF8.GetBytes(workMode.SerializedDockLayout)))
                    {
                        restored = serializer.Load<RootDock>(stream);
                    }

                    if (restored != null)
                    {
                        _logger.LogDebug("Successfully restored RootDock from serialized layout");

                        FixRootDockActiveState(restored);

                        int restoredCount = RestoreModulesInLayout(restored, workMode, ownerTab);

                        if (restoredCount == 0 && workMode.ModuleSlots.Count > 0)
                        {
                            _logger.LogWarning("No modules restored from serialized layout, falling back to PreferredPositions");
                        }
                        else
                        {
                            NormalizeProportionsRecursive(restored);
                            restored.Factory = this;
                            InitLayout(restored);
                            ValidateAndRemoveDuplicates(restored);

                            _logger.LogDebug("Layout restored with {Count} modules", restoredCount);
                            _currentRootDock = restored;
                            return restored;
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Dock.Serializer returned null, falling back to PreferredPositions");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to restore layout, falling back to PreferredPositions");
                }
            }

            _logger.LogDebug("Creating layout from PreferredPositions");
            return CreateLayoutFromPreferredPositions(workMode, ownerTab);
        }

        /// <summary>
        /// Нормализовать пропорции в ProportionalDock после десериализации
        /// Dock.Serializer сохраняет абсолютные пропорции, которые при изменении
        /// размера окна могут не суммироваться в 1.0 — оставшееся пространство
        /// рендерится как чёрный прямоугольник
        /// </summary>
        private void NormalizeProportionsRecursive(IDockable dockable)
        {
            if (dockable is ProportionalDock proportionalDock
                && proportionalDock.VisibleDockables != null
                && proportionalDock.VisibleDockables.Count > 0)
            {
                var nonSplitters = proportionalDock.VisibleDockables
                    .Where(d => d is not ProportionalDockSplitter)
                    .OfType<IDock>()
                    .ToList();

                if (nonSplitters.Count > 0)
                {
                    bool hasInvalidProportion = nonSplitters.Any(d =>
                        double.IsNaN(d.Proportion) || d.Proportion <= 0.0);

                    if (hasInvalidProportion)
                    {
                        double equal = 1.0 / nonSplitters.Count;

                        _logger.LogDebug(
                            "Redistributing equal proportions in {DockId}: {Count} items, each={Prop:F3} (had invalid proportions)",
                            proportionalDock.Id, nonSplitters.Count, equal);

                        foreach (var item in nonSplitters)
                            item.Proportion = equal;
                    }
                    else
                    {
                        double total = nonSplitters.Sum(d => d.Proportion);

                        if (total > 0 && Math.Abs(total - 1.0) > 0.01)
                        {
                            _logger.LogDebug(
                                "Normalizing proportions in {DockId}: total={Total:F3}, items={Count}",
                                proportionalDock.Id, total, nonSplitters.Count);

                            foreach (var item in nonSplitters)
                                item.Proportion = item.Proportion / total;
                        }
                    }
                }

                foreach (var child in proportionalDock.VisibleDockables)
                    NormalizeProportionsRecursive(child);
            }
            else if (dockable is IDock dock && dock.VisibleDockables != null)
            {
                foreach (var child in dock.VisibleDockables)
                    NormalizeProportionsRecursive(child);
            }
        }

        /// <summary>
        /// Исправить Active/Default/FocusedDockable у RootDock после десериализации
        /// Dock.Serializer сохраняет ссылки на вложенные элементы (DocumentDock, Document),
        /// а не на прямого дочернего RootDock (ProportionalDock).
        /// DockControl рендерит только то что в ActiveDockable — если это вложенный элемент,
        /// то весь остальной layout остаётся невидимым.
        /// Все три ссылки должны указывать строго на прямого дочернего RootDock.
        /// </summary>
        private void FixRootDockActiveState(IRootDock rootDock)
        {
            if (rootDock.VisibleDockables == null || rootDock.VisibleDockables.Count == 0)
                return;

            var topLevelChild = rootDock.VisibleDockables
                .FirstOrDefault(d => !IsContainerEmptyOrInvisible(d))
                ?? rootDock.VisibleDockables.First();

            bool activeIsDirectChild = rootDock.ActiveDockable != null
                && rootDock.VisibleDockables.Contains(rootDock.ActiveDockable);
            bool defaultIsDirectChild = rootDock.DefaultDockable != null
                && rootDock.VisibleDockables.Contains(rootDock.DefaultDockable);
            bool focusedIsDirectChild = rootDock.FocusedDockable != null
                && rootDock.VisibleDockables.Contains(rootDock.FocusedDockable);

            if (!activeIsDirectChild)
            {
                _logger.LogDebug("ActiveDockable is not a direct child of RootDock, resetting");
                rootDock.ActiveDockable = topLevelChild;
            }

            if (!defaultIsDirectChild)
            {
                _logger.LogDebug("DefaultDockable is not a direct child of RootDock, resetting");
                rootDock.DefaultDockable = topLevelChild;
            }

            if (!focusedIsDirectChild)
            {
                _logger.LogDebug("FocusedDockable is not a direct child of RootDock, resetting");
                rootDock.FocusedDockable = topLevelChild;
            }
        }

        private static bool IsContainerEmptyOrInvisible(IDockable? dockable)
        {
            if (dockable == null) return true;
            if (dockable is IDock dock)
            {
                if (dock.Proportion == 0.0) return true;
                if ((dock.VisibleDockables == null || dock.VisibleDockables.Count == 0)
                    && (dock is DocumentDock || dock is ProportionalDock))
                    return true;
            }
            return false;
        }

        // =====================================================================
        // ВОССТАНОВЛЕНИЕ ИЗ СЕРИАЛИЗОВАННОГО LAYOUT
        // =====================================================================

        private int RestoreModulesInLayout(IRootDock rootDock, WorkMode workMode, DocumentTabViewModel? ownerTab)
        {
            var tab = ownerTab ?? App.Services.GetRequiredService<ITabCollection>().ActiveTab;
            if (tab == null)
            {
                _logger.LogError("No tab for restoring modules");
                return 0;
            }

            int count = RestoreModulesRecursive(rootDock, workMode, tab);

            if (rootDock.Windows != null)
            {
                foreach (var window in rootDock.Windows)
                {
                    if (window.Layout != null)
                        count += RestoreModulesRecursive(window.Layout, workMode, tab);
                }
            }

            _logger.LogDebug("Modules restored in layout: {Count}", count);
            return count;
        }

        /// <summary>
        /// Рекурсивно восстановить модули из Document
        /// Document.Context содержит moduleType (строка)
        /// Данные ищутся сначала в кеше, потом в ModulesData проекта — по ключу moduleType
        /// </summary>
        private int RestoreModulesRecursive(IDockable dockable, WorkMode workMode, DocumentTabViewModel tab)
        {
            int count = 0;

            if (dockable is Document document && document.Id?.StartsWith("Module_") == true)
            {
                var moduleType = document.Id.Replace("Module_", "");

                var slot = workMode.ModuleSlots.FirstOrDefault(s => s.ModuleType == moduleType);

                if (slot == null)
                {
                    _logger.LogWarning("No slot for document {DocId}, clearing content", document.Id);
                    document.Content = null;
                    return 0;
                }

                _logger.LogDebug("Restoring module: {moduleType}", moduleType);

                var project = tab.GetProject();
                var cacheService = App.Services.GetRequiredService<IZipCacheService>();
                var cacheResult = cacheService.LoadCacheWithSession(tab.FilePath, project.Id);

                object? customDataToRestore = null;
                object? sessionDataToRestore = null;

                if (cacheResult.HasValue)
                {
                    cacheResult.Value.CustomData.TryGetValue(moduleType, out customDataToRestore);
                    cacheResult.Value.SessionData.TryGetValue(moduleType, out sessionDataToRestore);
                    if (customDataToRestore != null)
                        _logger.LogDebug("Using cache data for: {moduleType}", moduleType);
                }

                if (customDataToRestore == null
                    && project.ModulesData.TryGetValue(moduleType, out var fileData))
                {
                    customDataToRestore = fileData;
                    _logger.LogDebug("Using project file data for: {moduleType}", moduleType);
                }

                if (customDataToRestore == null)
                    _logger.LogWarning("No data found for module: {moduleType} — will load empty", moduleType);

                var module = tab.ModuleContext.CreateModule(moduleType);

                if (module?.ViewModel == null)
                {
                    _logger.LogWarning("Failed to create module: {moduleType}", moduleType);
                    document.Content = null;
                    return 0;
                }

                module.Context = tab.Context;

                if (customDataToRestore != null)
                    module.SetCustomData(customDataToRestore);

                if (sessionDataToRestore != null)
                {
                    module.SetSessionData(sessionDataToRestore);
                    _logger.LogDebug("Restored session data for: {moduleType}", moduleType);
                }

                var moduleView = module.CreateView();
                if (moduleView != null)
                {
                    document.Content = moduleView;
                    document.Context = moduleType;
                    document.Title = module.Title;
                    document.CanClose = slot.IsCloseable;
                    document.CanFloat = slot.IsCloseable;

                    _logger.LogDebug("Module restored: {moduleType}", moduleType);
                    count++;
                }
                else
                {
                    _logger.LogWarning("CreateView returned null for: {moduleType}", moduleType);
                    document.Content = null;
                }
            }

            if (dockable is IDock dock && dock.VisibleDockables != null)
            {
                foreach (var child in dock.VisibleDockables)
                    count += RestoreModulesRecursive(child, workMode, tab);
            }

            return count;
        }

        // =====================================================================
        // ПОСТРОЕНИЕ LAYOUT ИЗ PREFERRED POSITIONS
        // Каждый модуль живёт в своём DocumentDock — только так Dock.Avalonia
        // рендерит chrome (заголовок с кнопками close/float/drag)
        // =====================================================================

        /// <summary>
        /// Создать Layout из PreferredPosition модулей
        /// </summary>
        private IRootDock CreateLayoutFromPreferredPositions(WorkMode workMode, DocumentTabViewModel? ownerTab)
        {
            _logger.LogDebug("Building layout manually from PreferredPositions");

            var slotsToPlace = workMode.ModuleSlots
                .OrderBy(s => s.Category)
                .ToList();

            _logger.LogDebug("Slots to place: {Count}", slotsToPlace.Count);

            var documents = new List<(ModuleSlot Slot, Document Doc)>();
            foreach (var slot in slotsToPlace)
            {
                if (CreateModuleDocument(slot, ownerTab) is Document doc)
                    documents.Add((slot, doc));
                else
                    _logger.LogWarning("Failed to create document for: {ModuleType}", slot.ModuleType);
            }

            if (documents.Count == 0)
            {
                _logger.LogWarning("No documents created, returning empty layout");
                var empty = new RootDock
                {
                    Id = "Root",
                    Title = "Root",
                    Context = workMode.Id,
                    VisibleDockables = new List<IDockable>()
                };
                InitLayout(empty);
                return empty;
            }

            var centerDocs = documents.Where(d => IsCenter(d.Slot.PreferredPosition)).ToList();
            var leftDocs = documents.Where(d => IsLeft(d.Slot.PreferredPosition)).ToList();
            var rightDocs = documents.Where(d => IsRight(d.Slot.PreferredPosition)).ToList();
            var topDocs = documents.Where(d => IsTop(d.Slot.PreferredPosition)).ToList();
            var bottomDocs = documents.Where(d => IsBottom(d.Slot.PreferredPosition)).ToList();

            _logger.LogDebug("Groups: center={C} left={L} right={R} top={T} bottom={B}",
                centerDocs.Count, leftDocs.Count, rightDocs.Count, topDocs.Count, bottomDocs.Count);

            foreach (var d in documents)
                _logger.LogDebug("  Slot: {ModuleType} pos={Pos}({PosInt}) -> center={C} left={L} right={R}",
                    d.Slot.ModuleType, d.Slot.PreferredPosition, (int)d.Slot.PreferredPosition,
                    IsCenter(d.Slot.PreferredPosition), IsLeft(d.Slot.PreferredPosition), IsRight(d.Slot.PreferredPosition));

            if (centerDocs.Count == 0 && documents.Count > 0)
            {
                var first = documents.First();
                centerDocs.Add(first);
                leftDocs.Remove(first);
                rightDocs.Remove(first);
                topDocs.Remove(first);
                bottomDocs.Remove(first);
            }

            var centerDocDock = BuildDocumentDock("Root.Center", "Center", centerDocs, double.NaN);

            var horizontalChildren = new List<IDockable>();

            foreach (var group in leftDocs)
            {
                horizontalChildren.Add(BuildDocumentDock(
                    $"Root.Left_{group.Slot.ModuleType}", group.Slot.ModuleType,
                    new[] { group }, double.NaN));
                horizontalChildren.Add(NewSplitter());
            }

            horizontalChildren.Add(centerDocDock);

            if (rightDocs.Count == 1)
            {
                var (slot, _) = rightDocs[0];
                horizontalChildren.Add(NewSplitter());
                horizontalChildren.Add(BuildDocumentDock(
                    $"Root.Right_{slot.ModuleType}", slot.ModuleType, rightDocs, double.NaN));
            }
            else if (rightDocs.Count > 1)
            {
                horizontalChildren.Add(NewSplitter());
                horizontalChildren.Add(BuildVerticalStack(rightDocs));
            }

            List<IDockable> topLevelChildren;
            Orientation topLevelOrientation;

            if (topDocs.Count > 0 || bottomDocs.Count > 0)
            {
                var horizontal = new ProportionalDock
                {
                    Id = Guid.NewGuid().ToString(),
                    Title = "Horizontal",
                    Orientation = Orientation.Horizontal,
                    Proportion = double.NaN,
                    VisibleDockables = horizontalChildren
                };

                topLevelChildren = new List<IDockable>();
                topLevelOrientation = Orientation.Vertical;

                if (topDocs.Count > 0)
                {
                    topLevelChildren.Add(BuildDocumentDock("Root.Top", "Top", topDocs, double.NaN));
                    topLevelChildren.Add(NewSplitter());
                }

                topLevelChildren.Add(horizontal);

                if (bottomDocs.Count > 0)
                {
                    topLevelChildren.Add(NewSplitter());
                    topLevelChildren.Add(BuildDocumentDock("Root.Bottom", "Bottom", bottomDocs, double.NaN));
                }
            }
            else
            {
                topLevelChildren = horizontalChildren;
                topLevelOrientation = Orientation.Horizontal;
            }

            var mainProportional = new ProportionalDock
            {
                Id = Guid.NewGuid().ToString(),
                Title = "Main",
                Orientation = topLevelOrientation,
                Proportion = double.NaN,
                VisibleDockables = topLevelChildren
            };

            DistributeProportions(mainProportional);

            var rootDock = new RootDock
            {
                Id = "Root",
                Title = "Root",
                Context = workMode.Id,
                IsFocusableRoot = true,
                VisibleDockables = new List<IDockable> { mainProportional },
                ActiveDockable = mainProportional,
                DefaultDockable = mainProportional,
                FocusedDockable = mainProportional
            };

            InitLayout(rootDock);
            ValidateAndRemoveDuplicates(rootDock);

            _logger.LogDebug("Layout built manually with {Count} documents", documents.Count);
            _currentRootDock = rootDock;
            return rootDock;
        }

        private static DocumentDock BuildDocumentDock(
            string id,
            string title,
            IEnumerable<(ModuleSlot Slot, Document Doc)> items,
            double proportion)
        {
            var dock = new DocumentDock
            {
                Id = id,
                Title = title,
                Proportion = proportion,
                CanCreateDocument = false,
                VisibleDockables = new List<IDockable>()
            };

            Document? firstDoc = null;
            foreach (var (_, doc) in items)
            {
                doc.Proportion = double.NaN;
                dock.VisibleDockables.Add(doc);
                firstDoc ??= doc;
            }

            if (firstDoc != null)
                dock.ActiveDockable = firstDoc;

            return dock;
        }

        private static ProportionalDock BuildVerticalStack(List<(ModuleSlot Slot, Document Doc)> items)
        {
            var stack = new ProportionalDock
            {
                Id = Guid.NewGuid().ToString(),
                Title = "RightColumn",
                Orientation = Orientation.Vertical,
                Proportion = double.NaN,
                VisibleDockables = new List<IDockable>()
            };

            bool first = true;
            double proportion = 1.0 / items.Count;

            foreach (var (slot, doc) in items)
            {
                if (!first)
                    stack.VisibleDockables.Add(NewSplitter());

                doc.Proportion = double.NaN;
                var wrapper = new DocumentDock
                {
                    Id = $"Root.Right_{slot.ModuleType}",
                    Title = slot.ModuleType,
                    Proportion = proportion,
                    CanCreateDocument = false,
                    VisibleDockables = new List<IDockable> { doc },
                    ActiveDockable = doc
                };

                stack.VisibleDockables.Add(wrapper);
                first = false;
            }

            return stack;
        }

        private static void DistributeProportions(ProportionalDock dock)
        {
            if (dock.VisibleDockables == null) return;

            var nonSplitters = dock.VisibleDockables
                .Where(d => d is not ProportionalDockSplitter)
                .OfType<IDock>()
                .ToList();

            if (nonSplitters.Count == 0) return;

            double proportion = 1.0 / nonSplitters.Count;

            foreach (var d in nonSplitters)
                d.Proportion = proportion;
        }

        private static ProportionalDockSplitter NewSplitter() =>
            new() { Id = Guid.NewGuid().ToString() };

        // =====================================================================
        // КЛАССИФИКАЦИЯ ПОЗИЦИЙ
        // =====================================================================

        private static bool IsCenter(PreferredDockPosition pos) =>
            pos is PreferredDockPosition.RightAsTab
                or PreferredDockPosition.LeftAsTab
                or PreferredDockPosition.TopAsTab
                or PreferredDockPosition.BottomAsTab
                or PreferredDockPosition.TopRightAsTab
                or PreferredDockPosition.TopLeftAsTab
                or PreferredDockPosition.BottomRightAsTab
                or PreferredDockPosition.BottomLeftAsTab;

        private static bool IsLeft(PreferredDockPosition pos) =>
            pos is PreferredDockPosition.Left
                or PreferredDockPosition.TopLeft
                or PreferredDockPosition.BottomLeft;

        private static bool IsRight(PreferredDockPosition pos) =>
            pos is PreferredDockPosition.Right
                or PreferredDockPosition.TopRight
                or PreferredDockPosition.BottomRight;

        private static bool IsTop(PreferredDockPosition pos) =>
            pos is PreferredDockPosition.Top;

        private static bool IsBottom(PreferredDockPosition pos) =>
            pos is PreferredDockPosition.Bottom;

        // =====================================================================
        // СОЗДАНИЕ DOCUMENT ДЛЯ МОДУЛЯ
        // =====================================================================

        /// <summary>
        /// Создать Document для модуля
        /// Document.Context = moduleType (строка) — используется при восстановлении из сериализации
        /// Данные ищутся в кеше и в ModulesData по ключу moduleType
        /// </summary>
        public IDockable? CreateModuleDocument(ModuleSlot slot, DocumentTabViewModel? ownerTab = null)
        {
            _logger.LogDebug("Creating document for: {ModuleType}, IsCloseable={IsCloseable}",
                slot.ModuleType, slot.IsCloseable);

            var tab = ownerTab ?? App.Services.GetRequiredService<ITabCollection>().ActiveTab;
            if (tab == null)
            {
                _logger.LogError("No tab provided and no active tab");
                return null;
            }

            var project = tab.GetProject();
            var cacheService = App.Services.GetRequiredService<IZipCacheService>();
            var cacheResult = cacheService.LoadCacheWithSession(tab.FilePath, project.Id);

            object? customDataToRestore = null;
            object? sessionDataToRestore = null;

            if (cacheResult.HasValue)
            {
                cacheResult.Value.CustomData.TryGetValue(slot.ModuleType, out customDataToRestore);
                cacheResult.Value.SessionData.TryGetValue(slot.ModuleType, out sessionDataToRestore);
                if (customDataToRestore != null)
                    _logger.LogDebug("Using cache data for: {ModuleType}", slot.ModuleType);
            }

            if (customDataToRestore == null
                && project.ModulesData.TryGetValue(slot.ModuleType, out var fileData))
            {
                customDataToRestore = fileData;
                _logger.LogDebug("Using project file data for: {ModuleType}", slot.ModuleType);
            }

            if (customDataToRestore == null)
                _logger.LogWarning("No data found for module: {ModuleType} — will load empty", slot.ModuleType);

            var module = tab.ModuleContext.CreateModule(slot.ModuleType);

            if (module?.ViewModel == null)
            {
                _logger.LogWarning("Module not created: {ModuleType}", slot.ModuleType);
                return null;
            }

            module.Context = tab.Context;

            if (customDataToRestore != null)
            {
                module.SetCustomData(customDataToRestore);
                _logger.LogDebug("Restored data for: {ModuleType}", slot.ModuleType);
            }

            if (sessionDataToRestore != null)
            {
                module.SetSessionData(sessionDataToRestore);
                _logger.LogDebug("Restored session data for: {ModuleType}", slot.ModuleType);
            }

            var moduleView = module.CreateView();
            if (moduleView == null)
            {
                _logger.LogWarning("No View: {ModuleType}", slot.ModuleType);
                return null;
            }

            var doc = new Document
            {
                Id = $"Module_{slot.ModuleType}",
                Title = module.Title,
                Content = moduleView,
                Context = slot.ModuleType,
                CanClose = slot.IsCloseable,
                CanFloat = slot.IsCloseable,
                Factory = this
            };

            _logger.LogDebug("Document created: {ModuleType}, CanClose={CanClose}",
                slot.ModuleType, doc.CanClose);

            return doc;
        }

        // =====================================================================
        // ВСТАВКА МОДУЛЯ В СУЩЕСТВУЮЩИЙ LAYOUT
        // =====================================================================

        /// <summary>
        /// Вставить новый модуль в существующий layout
        /// </summary>
        public void InsertModuleByPreference(IRootDock rootDock, ModuleSlot slot)
        {
            _logger.LogDebug("Inserting module {ModuleType} at {Position}", slot.ModuleType, slot.PreferredPosition);

            _modulesBeingAdded.Add(slot.ModuleType);

            try
            {
                if (CreateModuleDocument(slot) is not Document doc)
                {
                    _logger.LogWarning("Failed to create document for {ModuleType}", slot.ModuleType);
                    return;
                }

                doc.Proportion = double.NaN;

                var position = slot.PreferredPosition;

                if (IsCenter(position))
                {
                    var allDocDocks = new List<DocumentDock>();
                    CollectDocumentDocks(rootDock, allDocDocks);
                    var targetDock = allDocDocks.FirstOrDefault();

                    if (targetDock == null)
                    {
                        _logger.LogWarning("No DocumentDock found for tab insert: {ModuleType}", slot.ModuleType);
                        return;
                    }

                    targetDock.VisibleDockables ??= new List<IDockable>();
                    targetDock.VisibleDockables.Add(doc);
                    targetDock.ActiveDockable = doc;

                    doc.Factory = this;
                    doc.Owner = targetDock;

                    _logger.LogDebug("Module {ModuleType} inserted as tab", slot.ModuleType);
                }
                else
                {
                    var newDocDock = new DocumentDock
                    {
                        Id = $"Root.Side_{slot.ModuleType}",
                        Title = slot.ModuleType,
                        Proportion = 0.25,
                        CanCreateDocument = false,
                        Factory = this,
                        VisibleDockables = new List<IDockable> { doc },
                        ActiveDockable = doc
                    };

                    doc.Factory = this;
                    doc.Owner = newDocDock;

                    var topProportional = FindTopLevelProportionalDock(rootDock);
                    if (topProportional == null)
                    {
                        _logger.LogWarning("No top-level ProportionalDock for {ModuleType}", slot.ModuleType);
                        return;
                    }

                    newDocDock.Owner = topProportional;
                    topProportional.VisibleDockables ??= new List<IDockable>();

                    if (IsLeft(position))
                    {
                        topProportional.VisibleDockables.Insert(0, NewSplitter());
                        topProportional.VisibleDockables.Insert(0, newDocDock);
                    }
                    else
                    {
                        topProportional.VisibleDockables.Add(NewSplitter());
                        topProportional.VisibleDockables.Add(newDocDock);
                    }

                    DistributeProportions(topProportional);

                    _logger.LogDebug("Module {ModuleType} inserted as new DocumentDock at {Position}",
                        slot.ModuleType, position);
                }

                ValidateAndRemoveDuplicates(rootDock);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error inserting module {ModuleType}", slot.ModuleType);
            }
            finally
            {
                _modulesBeingAdded.Remove(slot.ModuleType);
            }
        }

        public bool IsModuleBeingAdded(string moduleType) =>
            _modulesBeingAdded.Contains(moduleType);

        // =====================================================================
        // СЕРИАЛИЗАЦИЯ
        // =====================================================================

        /// <summary>
        /// Сериализовать текущий layout через Dock.Serializer
        /// </summary>
        public (string? SerializedLayout, List<ModuleSlot> UpdatedSlots) SerializeCurrentLayout(
            IRootDock rootDock,
            WorkMode workMode,
            ProjectModuleContext moduleContext)
        {
            try
            {
                if (rootDock.Context as string != workMode.Id)
                {
                    _logger.LogError("RootDock belongs to WorkMode {RootId}, but serializing {CurrentId}",
                        rootDock.Context, workMode.Id);
                    return (null, workMode.ModuleSlots);
                }

                _logger.LogDebug("Serializing current layout via Dock.Serializer");

                var serializer = GetSerializer();
                string layoutJson;

                using (var stream = new System.IO.MemoryStream())
                {
                    serializer.Save(stream, rootDock);
                    layoutJson = System.Text.Encoding.UTF8.GetString(stream.ToArray());
                }

                if (string.IsNullOrEmpty(layoutJson))
                {
                    _logger.LogWarning("Dock.Serializer returned empty JSON");
                    return (null, workMode.ModuleSlots);
                }

                _logger.LogDebug("Serialized layout, JSON length: {Length}", layoutJson.Length);
                return (layoutJson, workMode.ModuleSlots);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error serializing layout");
                return (null, workMode.ModuleSlots);
            }
        }

        // =====================================================================
        // ОТКРЕПЛЕНИЕ VIEW ОТ СТАРОГО LAYOUT
        // =====================================================================

        /// <summary>
        /// Открепить все View-шки из Document-ов старого layout перед его заменой
        /// Когда модуль переиспользуется между WorkMode (например Timer),
        /// его View всё ещё числится дочерним у ContentPresenter старого Document.
        /// Если не очистить Content — Avalonia падает при попытке добавить View в новый Document:
        /// "already has a visual parent"
        /// </summary>
        public void DetachViewsFromLayout(IRootDock? oldLayout)
        {
            if (oldLayout == null)
                return;

            if (_currentRootDock == oldLayout)
                _currentRootDock = null;

            DetachViewsRecursive(oldLayout);

            if (oldLayout.Windows != null)
            {
                foreach (var window in oldLayout.Windows)
                {
                    if (window.Layout != null)
                        DetachViewsRecursive(window.Layout);
                }
            }

            _logger.LogDebug("Views detached from old layout");
        }

        private void DetachViewsRecursive(IDockable dockable)
        {
            if (dockable is Document document)
            {
                if (document.Content != null)
                {
                    _logger.LogDebug("Detaching view from Document: {Id}", document.Id);
                    document.Content = null;
                }
                return;
            }

            if (dockable is IDock dock && dock.VisibleDockables != null)
            {
                foreach (var child in dock.VisibleDockables)
                    DetachViewsRecursive(child);
            }
        }

        // =====================================================================
        // ОЧИСТКА ПУСТЫХ КОНТЕЙНЕРОВ
        // =====================================================================

        /// <summary>
        /// Удалить пустые DocumentDock и ProportionalDock из layout после закрытия модулей
        /// Dock.Avalonia не убирает контейнеры автоматически — они остаются с нулевым содержимым
        /// и занимают место, не давая оставшимся модулям растянуться
        /// </summary>
        public void CleanupEmptyContainersInLayout(IRootDock rootDock)
        {
            var topProportional = FindTopLevelProportionalDock(rootDock);
            if (topProportional == null)
                return;

            bool changed = true;
            while (changed)
                changed = CleanupProportionalDockRecursive(topProportional);

            _logger.LogDebug("Empty containers cleaned up");
        }

        /// <summary>
        /// Рекурсивно очищает ProportionalDock от пустых детей
        /// Возвращает true если были изменения (нужен повторный проход)
        /// </summary>
        private bool CleanupProportionalDockRecursive(ProportionalDock dock)
        {
            if (dock.VisibleDockables == null)
                return false;

            bool changed = false;

            foreach (var child in dock.VisibleDockables.ToList())
            {
                if (child is ProportionalDock childProportional)
                    changed |= CleanupProportionalDockRecursive(childProportional);
            }

            var toRemove = dock.VisibleDockables
                .Where(d => IsEmptyContainer(d))
                .ToList();

            foreach (var empty in toRemove)
            {
                dock.VisibleDockables.Remove(empty);
                _logger.LogDebug("Removed empty container: {Type} ({Id})",
                    empty.GetType().Name, empty.Id);
                changed = true;
            }

            if (changed)
            {
                CleanupSplitters(dock);
                DistributeProportions(dock);
            }

            return changed;
        }

        private static bool IsEmptyContainer(IDockable dockable)
        {
            if (dockable is DocumentDock docDock)
                return docDock.VisibleDockables == null || docDock.VisibleDockables.Count == 0;

            if (dockable is ProportionalDock propDock)
            {
                if (propDock.VisibleDockables == null || propDock.VisibleDockables.Count == 0)
                    return true;

                return !propDock.VisibleDockables.Any(d => d is not ProportionalDockSplitter);
            }

            return false;
        }

        private static void CleanupSplitters(ProportionalDock dock)
        {
            if (dock.VisibleDockables == null)
                return;

            while (dock.VisibleDockables.Count > 0
                   && dock.VisibleDockables[0] is ProportionalDockSplitter)
                dock.VisibleDockables.RemoveAt(0);

            while (dock.VisibleDockables.Count > 0
                   && dock.VisibleDockables[^1] is ProportionalDockSplitter)
                dock.VisibleDockables.RemoveAt(dock.VisibleDockables.Count - 1);

            for (int i = dock.VisibleDockables.Count - 1; i > 0; i--)
            {
                if (dock.VisibleDockables[i] is ProportionalDockSplitter
                    && dock.VisibleDockables[i - 1] is ProportionalDockSplitter)
                    dock.VisibleDockables.RemoveAt(i);
            }
        }

        // =====================================================================
        // ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ
        // =====================================================================

        /// <summary>
        /// Валидация и удаление дубликатов модулей
        /// </summary>
        public void ValidateAndRemoveDuplicates(IRootDock rootDock)
        {
            var seenModules = new Dictionary<string, string>();
            var duplicatesToRemove = new List<(IDock Container, IDockable Duplicate)>();

            ScanForDuplicatesRecursive(rootDock, seenModules, duplicatesToRemove);

            if (rootDock.Windows != null)
            {
                foreach (var window in rootDock.Windows)
                {
                    if (window.Layout != null)
                        ScanForDuplicatesRecursive(window.Layout, seenModules, duplicatesToRemove);
                }
            }

            if (duplicatesToRemove.Count > 0)
            {
                _logger.LogError("Found {Count} duplicates, removing", duplicatesToRemove.Count);
                foreach (var (container, duplicate) in duplicatesToRemove)
                {
                    container.VisibleDockables?.Remove(duplicate);
                    _logger.LogDebug("Removed duplicate: {Id}", duplicate.Id);
                }
            }
        }

        private void ScanForDuplicatesRecursive(
            IDockable dockable,
            Dictionary<string, string> seenModules,
            List<(IDock, IDockable)> duplicatesToRemove)
        {
            if (dockable is Document document && document.Id != null)
            {
                var moduleType = document.Id.Replace("Module_", "");

                if (seenModules.ContainsKey(moduleType))
                {
                    _logger.LogError("DUPLICATE: {moduleType} in {Current}",
                        moduleType, document.Owner?.Id ?? "unknown");
                    if (document.Owner is IDock owner)
                        duplicatesToRemove.Add((owner, document));
                }
                else
                {
                    seenModules[moduleType] = document.Owner?.Id ?? "unknown";
                }
            }

            if (dockable is IDock dock && dock.VisibleDockables != null)
            {
                foreach (var child in dock.VisibleDockables.ToList())
                    ScanForDuplicatesRecursive(child, seenModules, duplicatesToRemove);
            }
        }

        private static void CollectDocumentDocks(IDockable dockable, List<DocumentDock> result)
        {
            if (dockable is DocumentDock docDock)
            {
                result.Add(docDock);
                return;
            }

            if (dockable is IDock dock && dock.VisibleDockables != null)
            {
                foreach (var child in dock.VisibleDockables)
                    CollectDocumentDocks(child, result);
            }
        }

        private static ProportionalDock? FindTopLevelProportionalDock(IRootDock rootDock) =>
            rootDock.VisibleDockables?.OfType<ProportionalDock>().FirstOrDefault();

        public override IDockWindow CreateDockWindow()
        {
            _logger.LogDebug("Creating DockWindow");
            var window = new DockWindow { Id = Guid.NewGuid().ToString(), Factory = this };
            _logger.LogDebug("DockWindow created: {Id}", window.Id);
            return window;
        }

        /// <summary>
        /// Перехват смены активного документа в Dock
        /// Вызывается когда пользователь кликает на другую вкладку модуля
        /// </summary>
        public override void OnFocusedDockableChanged(IDockable? dockable)
        {
            base.OnFocusedDockableChanged(dockable);

            if (dockable is Document doc && doc.Id?.StartsWith("Module_") == true)
            {
                var moduleType = doc.Id.Replace("Module_", "");
                _logger.LogDebug("Module focused: {moduleType}", moduleType);
                OnModuleFocused?.Invoke(moduleType);
            }
        }
    }
}