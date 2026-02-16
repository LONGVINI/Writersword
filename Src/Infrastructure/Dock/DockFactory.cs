using Avalonia;
using Avalonia.Controls;
using Dock.Model.Avalonia;
using Dock.Model.Avalonia.Controls;
using Dock.Model.Avalonia.Core;
using Dock.Model.Controls;
using Dock.Model.Core;
using DynamicData;
using DynamicData.Binding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Linq;
using Writersword.Core.Enums;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Interfaces.Services;
using Writersword.Core.Models.WorkModes;
using Writersword.Core.Services;
using Writersword.Modules.Common;
using Writersword.Src.Core.Interfaces.Services;
using Writersword.Src.Core.Interfaces.WorkFlows;
using Writersword.Src.Modules;
using Writersword.ViewModels;
using Writersword.Views;

namespace Writersword.Src.Infrastructure.Dock
{
    /// <summary>
    /// Фабрика для создания Dock элементов
    /// Использует иерархическую систему Path вместо случайных GUID
    /// Работает с LayoutTree для точного восстановления структуры
    /// Вся логика управления состоянием перенесена в WorkspaceController
    /// </summary>
    public class DockFactory : Factory
    {
        private readonly ILogger<DockFactory> _logger;
        private readonly HashSet<string> _modulesBeingAdded = new();
        private DockPanelInserter? _panelInserter;
        private readonly ContainerPathBuilder _pathBuilder;

        public DockFactory()
        {
            _logger = App.Services.GetService<ILogger<DockFactory>>()!;
            _pathBuilder = new ContainerPathBuilder();
        }

        /// <summary>
        /// Инициализация Locators
        /// Настраивает фабрику для создания окон и элементов Dock
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

            var panelInserterLogger = App.Services.GetService<ILogger<DockPanelInserter>>()!;
            _panelInserter = new DockPanelInserter(this, panelInserterLogger);

            _logger.LogDebug("Initialized with custom HostWindow");

            DockDiagnostics.InspectFactoryMethods();
        }

        /// <summary>
        /// Создать layout из WorkMode
        /// Главный метод создания UI структуры
        /// </summary>
        public IRootDock CreateLayout(WorkMode workMode, DocumentTabViewModel? ownerTab = null)
        {
            _logger.LogDebug("Creating layout for: {Title}", workMode.Title);

            IDock mainDock;

            if (workMode.LayoutTree != null)
            {
                _logger.LogDebug("Creating layout from LayoutTree");
                mainDock = CreateDockFromLayoutTree(workMode, ownerTab);
            }
            else
            {
                _logger.LogDebug("No LayoutTree, creating from PreferredPosition");
                mainDock = CreateLayoutFromPreferredPositions(workMode, ownerTab);
            }

            var rootDock = new RootDock
            {
                Id = "Root",
                Title = "Root",
                Context = workMode.Id,
                ActiveDockable = mainDock,
                DefaultDockable = mainDock,
                Factory = this
            };

            if (rootDock.VisibleDockables == null)
                rootDock.VisibleDockables = new List<IDockable>();

            rootDock.VisibleDockables.Add(mainDock);

            InitLayout(rootDock);

            CreateFloatingWindows(rootDock, workMode, ownerTab);

            _logger.LogDebug("Layout created, marked with WorkMode.Id: {WorkModeId}", workMode.Id);

            ValidateAndRemoveDuplicates(rootDock);

            return rootDock;
        }

        /// <summary>
        /// Создать Dock из LayoutTree
        /// Точно восстанавливает иерархическую структуру
        /// </summary>
        private IDock CreateDockFromLayoutTree(WorkMode workMode, DocumentTabViewModel? ownerTab)
        {
            _logger.LogDebug("Creating dock from LayoutTree");

            if (workMode.LayoutTree == null)
            {
                _logger.LogWarning("LayoutTree is null");
                return CreateLayoutFromPreferredPositions(workMode, ownerTab);
            }

            var rootNode = workMode.LayoutTree;
            var dock = CreateDockFromNode(rootNode, workMode, ownerTab);

            _logger.LogDebug("Layout created from LayoutTree");
            return dock;
        }

        /// <summary>
        /// Рекурсивно создать Dock из LayoutNode
        /// </summary>
        private IDock CreateDockFromNode(LayoutNode node, WorkMode workMode, DocumentTabViewModel? ownerTab)
        {
            _logger.LogDebug("Creating dock from node: Path={Path}, Type={Type}", node.Path, node.Type);

            if (node.Type == "DocumentDock")
            {
                return CreateDocumentDockFromNode(node, workMode, ownerTab);
            }

            if (node.Type == "ProportionalDock")
            {
                return CreateProportionalDockFromNode(node, workMode, ownerTab);
            }

            _logger.LogWarning("Unknown node type: {Type}", node.Type);
            return CreateDocumentDockFromNode(node, workMode, ownerTab);
        }

        /// <summary>
        /// Создать DocumentDock из LayoutNode
        /// </summary>
        private DocumentDock CreateDocumentDockFromNode(LayoutNode node, WorkMode workMode, DocumentTabViewModel? ownerTab)
        {
            var modulesInContainer = workMode.ModuleSlots
                .Where(slot => slot.Path == node.Path && !slot.IsFloating && slot.IsCurrentlyOpen)
                .OrderBy(slot => slot.TabOrder)
                .ToList();

            _logger.LogDebug("Container {Path} has {Count} modules", node.Path, modulesInContainer.Count);

            var documents = new List<IDockable>();

            foreach (var slot in modulesInContainer)
            {
                var doc = CreateModuleDocument(slot, ownerTab);
                if (doc != null)
                {
                    documents.Add(doc);
                }
            }

            var activeSlot = modulesInContainer.FirstOrDefault(s => s.IsActiveTab);
            var activeDoc = activeSlot != null
                ? documents.FirstOrDefault(d => d.Id == $"Module_{activeSlot.ModuleType}")
                : documents.FirstOrDefault();

            var documentDock = new DocumentDock
            {
                Id = node.Path,
                Title = _pathBuilder.GetSegmentName(node.Path),
                Proportion = double.IsNaN(node.Proportion) ? double.NaN : node.Proportion,
                ActiveDockable = activeDoc,
                CanCreateDocument = false,
                Factory = this
            };

            if (documentDock.VisibleDockables == null)
                documentDock.VisibleDockables = new List<IDockable>();

            foreach (var doc in documents)
            {
                documentDock.VisibleDockables.Add(doc);
            }

            _logger.LogDebug("Created DocumentDock: {Path}, modules: {Count}", node.Path, documents.Count);

            return documentDock;
        }

        /// <summary>
        /// Создать ProportionalDock из LayoutNode
        /// </summary>
        private ProportionalDock CreateProportionalDockFromNode(LayoutNode node, WorkMode workMode, DocumentTabViewModel? ownerTab)
        {
            var orientation = node.Orientation switch
            {
                "Horizontal" => Orientation.Horizontal,
                "Vertical" => Orientation.Vertical,
                _ => Orientation.Horizontal
            };

            var proportionalDock = new ProportionalDock
            {
                Id = node.Path,
                Title = _pathBuilder.GetSegmentName(node.Path),
                Proportion = double.IsNaN(node.Proportion) ? double.NaN : node.Proportion,
                Orientation = orientation,
                Factory = this
            };

            if (proportionalDock.VisibleDockables == null)
                proportionalDock.VisibleDockables = new List<IDockable>();

            if (node.Children != null && node.Children.Count > 0)
            {
                for (int i = 0; i < node.Children.Count; i++)
                {
                    var childDock = CreateDockFromNode(node.Children[i], workMode, ownerTab);
                    proportionalDock.VisibleDockables.Add(childDock);

                    if (i < node.Children.Count - 1)
                    {
                        var splitter = new ProportionalDockSplitter
                        {
                            Id = _pathBuilder.BuildPath(node.Path, $"Splitter{i}"),
                            Title = $"Splitter{i}"
                        };
                        proportionalDock.VisibleDockables.Add(splitter);
                    }
                }
            }

            proportionalDock.ActiveDockable = proportionalDock.VisibleDockables
                .FirstOrDefault(d => d is not ProportionalDockSplitter);

            _logger.LogDebug("Created ProportionalDock: {Path}, children: {Count}", node.Path, node.Children?.Count ?? 0);

            return proportionalDock;
        }

        /// <summary>
        /// Создать Layout динамически из PreferredPosition модулей
        /// Используется когда LayoutTree не задан
        /// </summary>
        private IDock CreateLayoutFromPreferredPositions(WorkMode workMode, DocumentTabViewModel? ownerTab)
        {
            _logger.LogDebug("Creating dynamic layout from PreferredPosition");

            var rootDock = new ProportionalDock
            {
                Id = "Root",
                Title = "Main",
                Proportion = double.NaN,
                Orientation = Orientation.Horizontal,
                Factory = this
            };

            if (rootDock.VisibleDockables == null)
                rootDock.VisibleDockables = new List<IDockable>();

            var openSlots = workMode.ModuleSlots
                .Where(s => s.IsCurrentlyOpen && !s.IsFloating)
                .OrderBy(s => s.Category)
                .ThenBy(s => s.TabOrder)
                .ToList();

            foreach (var slot in openSlots)
            {
                _logger.LogDebug("Slot order: {ModuleType}, Category={Category}, TabOrder={TabOrder}, Position={Position}",
                    slot.ModuleType, slot.Category, slot.TabOrder, slot.PreferredPosition);
            }

            _logger.LogDebug("Found {Count} open modules to place", openSlots.Count);

            foreach (var slot in openSlots)
            {
                _logger.LogDebug("Will place: {ModuleType}, Position={Position}, Category={Category}",
                    slot.ModuleType, slot.PreferredPosition, slot.Category);
            }

            if (openSlots.Count == 0)
            {
                _logger.LogWarning("No modules to place, creating empty center dock");

                var emptyDock = new DocumentDock
                {
                    Id = "Root.Center",
                    Title = "Center",
                    Proportion = double.NaN,
                    CanCreateDocument = false,
                    Factory = this
                };

                if (emptyDock.VisibleDockables == null)
                    emptyDock.VisibleDockables = new List<IDockable>();

                rootDock.VisibleDockables.Add(emptyDock);
                rootDock.ActiveDockable = emptyDock;

                return rootDock;
            }

            foreach (var slot in openSlots)
            {
                _logger.LogDebug("Placing module: {ModuleId} at {Position}", slot.ModuleType, slot.PreferredPosition);

                var document = CreateModuleDocument(slot, ownerTab);
                if (document == null)
                {
                    _logger.LogWarning("Failed to create document for: {ModuleId}", slot.ModuleType);
                    continue;
                }

                if (rootDock.VisibleDockables.Count == 0)
                {
                    var centerDock = new DocumentDock
                    {
                        Id = "Root.Center",
                        Title = "Center",
                        Proportion = 0.7,
                        CanCreateDocument = false,
                        Factory = this
                    };

                    if (centerDock.VisibleDockables == null)
                        centerDock.VisibleDockables = new List<IDockable>();

                    centerDock.VisibleDockables.Add(document);
                    centerDock.ActiveDockable = document;

                    rootDock.VisibleDockables.Add(centerDock);

                    slot.Path = "Root.Center";

                    _logger.LogDebug("Created center dock with first module: {ModuleId}", slot.ModuleType);
                }
                else
                {
                    InsertModuleByPreferredPositionInternal(rootDock, slot, document);
                }
            }

            rootDock.ActiveDockable = rootDock.VisibleDockables.FirstOrDefault();

            _logger.LogDebug("Dynamic layout created with {Count} panels", rootDock.VisibleDockables.Count);

            return rootDock;
        }

        /// <summary>
        /// Вставить модуль по PreferredPosition при создании layout
        /// </summary>
        private void InsertModuleByPreferredPositionInternal(IDock rootDock, ModuleSlot slot, IDockable document)
        {
            var basePosition = GetBasePosition(slot.PreferredPosition);
            var asTab = slot.PreferredPosition.ToString().EndsWith("AsTab");

            _logger.LogDebug("Inserting {ModuleId} at {BasePosition}, asTab={AsTab}", slot.ModuleType, basePosition, asTab);

            ProportionalDock? mainDock = null;

            if (rootDock is ProportionalDock propDock)
            {
                mainDock = propDock;
            }
            else if (rootDock is IDock dock && dock.VisibleDockables != null)
            {
                mainDock = dock.VisibleDockables.FirstOrDefault() as ProportionalDock;
            }

            if (mainDock == null)
            {
                _logger.LogError("Could not find ProportionalDock in layout");
                return;
            }

            _logger.LogDebug("Current mainDock - Orientation={Orientation}, Children={Count}",
    mainDock.Orientation, mainDock.VisibleDockables?.Count ?? 0);

            var tempRoot = new RootDock
            {
                Id = "TempRoot",
                Factory = this
            };

            if (tempRoot.VisibleDockables == null)
                tempRoot.VisibleDockables = new List<IDockable>();

            tempRoot.VisibleDockables.Add(mainDock);

            var targetDock = _panelInserter?.FindOrCreateDockForPosition(tempRoot, basePosition, asTab);

            if (targetDock == null)
            {
                _logger.LogError("Could not find/create dock for position: {Position}", basePosition);
                return;
            }

            if (targetDock.VisibleDockables == null)
                targetDock.VisibleDockables = new List<IDockable>();

            if (document is Document doc)
            {
                doc.Owner = targetDock;
                doc.CanFloat = slot.IsCloseable;
                doc.CanClose = slot.IsCloseable;
            }

            InitDockable(document, targetDock);

            if (targetDock.VisibleDockables.Count > 0 && !asTab)
            {
                var splitter = new ProportionalDockSplitter
                {
                    Id = _pathBuilder.BuildPath(targetDock.Id ?? "Root", $"Splitter{targetDock.VisibleDockables.Count}"),
                    Title = "Splitter"
                };
                targetDock.VisibleDockables.Add(splitter);
                _logger.LogDebug("Added splitter before module {ModuleId}", slot.ModuleType);
            }

            targetDock.VisibleDockables.Add(document);
            targetDock.ActiveDockable = document;

            slot.Path = targetDock.Id;

            _logger.LogDebug("Module {ModuleId} inserted into {Path}", slot.ModuleType, targetDock.Id);
        }

        public bool IsModuleBeingAdded(string moduleId)
        {
            return _modulesBeingAdded.Contains(moduleId);
        }

        /// <summary>
        /// Создать флоат окна для модулей с IsFloating = true
        /// </summary>
        public void CreateFloatingWindows(IRootDock rootDock, WorkMode workMode, DocumentTabViewModel? ownerTab = null)
        {
            var floatingModules = workMode.ModuleSlots.Where(s => s.IsFloating && s.IsCurrentlyOpen).ToList();

            if (floatingModules.Count == 0)
            {
                _logger.LogDebug("No floating modules");
                return;
            }

            _logger.LogDebug("Creating {Count} floating windows", floatingModules.Count);

            var seenInstanceIds = new HashSet<string>();

            for (int i = 0; i < floatingModules.Count; i++)
            {
                var floatSlot = floatingModules[i];

                if (!string.IsNullOrEmpty(floatSlot.InstanceId))
                {
                    if (seenInstanceIds.Contains(floatSlot.InstanceId))
                    {
                        _logger.LogError("DUPLICATE float window detected: ModuleId={ModuleId}, InstanceId={InstanceId}, skipping",
                            floatSlot.ModuleType, floatSlot.InstanceId);
                        continue;
                    }
                    seenInstanceIds.Add(floatSlot.InstanceId);
                }

                if (IsFloatWindowAlreadyExists(rootDock, floatSlot.ModuleType, floatSlot.InstanceId))
                {
                    _logger.LogError("Float window already exists: ModuleId={ModuleId}, InstanceId={InstanceId}, skipping duplicate",
                        floatSlot.ModuleType, floatSlot.InstanceId);
                    continue;
                }

                var document = CreateModuleDocument(floatSlot, ownerTab);
                if (document == null)
                {
                    _logger.LogWarning("Failed to create document for floating module: {ModuleId}", floatSlot.ModuleType);
                    continue;
                }

                var hostWindow = new HostWindow();

                if (document is Document doc)
                {
                    doc.Owner = rootDock;
                    doc.CanFloat = true;
                }

                InitDockable(document, rootDock);

                var floatPath = $"Float.{i}";
                var floatDock = new DocumentDock
                {
                    Id = floatPath,
                    Title = document.Title,
                    CanCreateDocument = false,
                    Factory = this
                };

                if (floatDock.VisibleDockables == null)
                    floatDock.VisibleDockables = new List<IDockable>();

                floatDock.VisibleDockables.Add(document);
                floatDock.ActiveDockable = document;

                floatSlot.Path = floatPath;

                hostWindow.SetTitle(document.Title);

                if (floatSlot.FloatWidth > 0 && floatSlot.FloatHeight > 0)
                {
                    hostWindow.SetSize(floatSlot.FloatWidth, floatSlot.FloatHeight);
                }
                else
                {
                    hostWindow.SetSize(800, 600);
                }

                if (floatSlot.FloatX > 0 || floatSlot.FloatY > 0)
                {
                    hostWindow.SetPosition(floatSlot.FloatX, floatSlot.FloatY);
                }
                else
                {
                    hostWindow.SetPosition(100 + (i * 50), 100 + (i * 50));
                }

                AddFloatWindow(rootDock, floatDock, hostWindow);

                _logger.LogDebug("Created floating window {Index}: {ModuleId} at ({X}, {Y}), size ({W}, {H})",
                    i, floatSlot.ModuleType, floatSlot.FloatX, floatSlot.FloatY, floatSlot.FloatWidth, floatSlot.FloatHeight);
            }

            _logger.LogDebug("Finished creating floating windows. Total in rootDock.Windows: {Count}", rootDock.Windows?.Count ?? 0);
        }

        /// <summary>
        /// Проверить существует ли уже float окно с таким ModuleId или InstanceId
        /// Проверяет только в текущем rootDock
        /// </summary>
        private bool IsFloatWindowAlreadyExists(IRootDock rootDock, string moduleId, string instanceId)
        {
            if (rootDock.Windows == null || rootDock.Windows.Count == 0)
            {
                return false;
            }

            var tabCollection = App.Services.GetRequiredService<ITabCollection>();
            var activeTab = tabCollection.ActiveTab;

            if (activeTab?.ModuleContext == null)
            {
                _logger.LogWarning("No active tab context for float window validation");
                return false;
            }

            _logger.LogDebug("Checking {Count} windows in current rootDock for duplicates", rootDock.Windows.Count);

            foreach (var window in rootDock.Windows)
            {
                var floatDock = FindDocumentDockInLayout(window.Layout);
                if (floatDock?.VisibleDockables == null)
                    continue;

                foreach (var dockable in floatDock.VisibleDockables)
                {
                    if (dockable is Document document)
                    {
                        var existingModuleId = document.Id?.Replace("Module_", "");

                        if (existingModuleId == moduleId)
                        {
                            _logger.LogError("CRITICAL: Float window with ModuleId={ModuleId} already exists in window {WindowId}!",
                                moduleId, window.Id);
                            return true;
                        }

                        if (!string.IsNullOrEmpty(instanceId))
                        {
                            var existingInstanceId = GetInstanceIdFromDocument(document, activeTab.ModuleContext);

                            if (existingInstanceId == instanceId)
                            {
                                _logger.LogError("Float window with same InstanceId={InstanceId} already exists in window {WindowId}!",
                                    instanceId, window.Id);
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Добавить флоат окно в RootDock
        /// </summary>
        private void AddFloatWindow(IRootDock rootDock, IDock floatDock, IHostWindow hostWindow)
        {
            if (rootDock.Windows == null)
                rootDock.Windows = new ObservableCollectionExtended<IDockWindow>();

            var floatRootDock = new RootDock
            {
                Id = $"FloatRoot_{floatDock.Id}",
                Title = floatDock.Title,
                ActiveDockable = floatDock,
                DefaultDockable = floatDock,
                Factory = this
            };

            if (floatRootDock.VisibleDockables == null)
                floatRootDock.VisibleDockables = new List<IDockable>();

            floatRootDock.VisibleDockables.Add(floatDock);

            var dockWindow = new DockWindow
            {
                Id = floatDock.Id,
                Title = floatDock.Title,
                Layout = floatRootDock,
                Host = hostWindow,
                Factory = this
            };

            rootDock.Windows.Add(dockWindow);

            hostWindow.SetLayout(floatRootDock);
            hostWindow.Present(false);

            _logger.LogDebug("Float window added to RootDock: {Id}", floatDock.Id);
        }

        /// <summary>
        /// Создать Document для модуля
        /// </summary>
        public IDockable? CreateModuleDocument(ModuleSlot slot, DocumentTabViewModel? ownerTab = null)
        {
            _logger.LogDebug("Creating document for: {ModuleId}, IsCloseable={IsCloseable}", slot.ModuleType, slot.IsCloseable);

            var tab = ownerTab ?? App.Services.GetRequiredService<ITabCollection>().ActiveTab;
            if (tab == null)
            {
                _logger.LogError("No tab provided and no active tab");
                return null;
            }

            string? instanceIdToUse = null;
            object? customDataToRestore = null;

            var project = tab.GetProject();

            var cacheService = App.Services.GetRequiredService<IZipCacheService>();
            var cache = cacheService.LoadCache(tab.FilePath);

            if (cache != null && cache.TryGetValue(slot.InstanceId, out var cacheData))
            {
                customDataToRestore = cacheData;
                _logger.LogDebug("Loaded data from CACHE for: {ModuleId}", slot.ModuleType);
            }
            else if (project.ModulesData.TryGetValue(slot.InstanceId, out var fileData))
            {
                customDataToRestore = fileData;
                _logger.LogDebug("Loaded data from PROJECT FILE for: {ModuleId}", slot.ModuleType);
            }

            if (string.IsNullOrEmpty(instanceIdToUse) && !string.IsNullOrEmpty(slot.InstanceId))
            {
                instanceIdToUse = slot.InstanceId;
                _logger.LogDebug("Using InstanceId from slot: {InstanceId}", instanceIdToUse);
            }

            if (!string.IsNullOrEmpty(instanceIdToUse))
            {
                _logger.LogDebug("Creating module WITH InstanceId: {InstanceId}", instanceIdToUse);
            }
            else
            {
                _logger.LogDebug("Creating module WITHOUT InstanceId (will generate new)");
            }

            var module = tab.ModuleContext.CreateModule(slot.ModuleType, instanceIdToUse);

            if (module?.ViewModel == null)
            {
                _logger.LogWarning("Module not created: {ModuleId}", slot.ModuleType);
                return null;
            }

            if (string.IsNullOrEmpty(slot.InstanceId))
            {
                slot.InstanceId = module.InstanceId;
                _logger.LogDebug("Saved InstanceId to slot: {InstanceId}", module.InstanceId);
            }

            module.Context = tab.Context;
            _logger.LogDebug("Context assigned to module: {ModuleId}", slot.ModuleType);

            if (customDataToRestore != null)
            {
                module.SetCustomData(customDataToRestore);
                _logger.LogDebug("Restored CustomData for: {ModuleId}", slot.ModuleType);
            }

            var moduleView = module.CreateView();
            if (moduleView == null)
            {
                _logger.LogWarning("No View: {ModuleId}", slot.ModuleType);
                return null;
            }

            string docId = $"Module_{slot.ModuleType}";

            var doc = new Document
            {
                Id = docId,
                Title = module.Title,
                Content = moduleView,
                CanClose = slot.Category != ModuleCategory.Required && slot.IsCloseable,
                CanFloat = slot.Category != ModuleCategory.Required && slot.IsCloseable,
                Factory = this
            };

            _logger.LogDebug("Document created: {ModuleId}, InstanceId: {InstanceId}, Category: {Category}, CanClose={CanClose}, CanFloat={CanFloat}",
                slot.ModuleType, module.InstanceId, slot.Category, doc.CanClose, doc.CanFloat);

            return doc;
        }

        /// <summary>
        /// Вставить новый модуль в существующий layout по PreferredPosition
        /// </summary>
        public void InsertModuleByPreference(IRootDock rootDock, ModuleSlot slot)
        {
            _logger.LogDebug("Inserting module {ModuleId} with position {Position}, IsCloseable={IsCloseable}",
                slot.ModuleType, slot.PreferredPosition, slot.IsCloseable);

            RebuildContainerPathsRecursive(rootDock, "Root");

            var moduleId = slot.ModuleType;

            _modulesBeingAdded.Add(moduleId);

            try
            {
                var document = CreateModuleDocument(slot);
                if (document == null)
                {
                    _logger.LogWarning("Failed to create document for {ModuleId}", slot.ModuleType);
                    return;
                }

                var basePosition = GetBasePosition(slot.PreferredPosition);
                var isTab = slot.PreferredPosition.ToString().EndsWith("AsTab");

                _logger.LogDebug("Base position: {BasePosition}, IsTab: {IsTab}", basePosition, isTab);

                var targetDock = _panelInserter?.FindOrCreateDockForPosition(rootDock, basePosition, isTab);

                if (targetDock != null)
                {
                    if (targetDock.VisibleDockables == null)
                    {
                        _logger.LogDebug("Initializing VisibleDockables as ObservableCollection for {DockId}", targetDock.Id);
                        targetDock.VisibleDockables = new ObservableCollectionExtended<IDockable>();
                    }
                    else if (targetDock.VisibleDockables is not ObservableCollectionExtended<IDockable>)
                    {
                        _logger.LogDebug("Converting VisibleDockables to ObservableCollection for {DockId}", targetDock.Id);
                        var existingItems = targetDock.VisibleDockables.ToList();
                        targetDock.VisibleDockables = new ObservableCollectionExtended<IDockable>(existingItems);
                    }

                    if (document is Document doc)
                    {
                        doc.Owner = targetDock;
                        doc.CanFloat = slot.IsCloseable;
                        doc.CanClose = slot.IsCloseable;

                        _logger.LogDebug("After Init: Owner={Owner}, CanFloat={CanFloat}, CanClose={CanClose}",
                            doc.Owner?.Id ?? "NULL", doc.CanFloat, doc.CanClose);
                    }

                    InitDockable(document, targetDock);

                    var currentCount = targetDock.VisibleDockables.Count;

                    AddDockable(targetDock, document);
                    SetActiveDockable(document);
                    SetFocusedDockable(targetDock, document);
                    targetDock.ActiveDockable = document;

                    slot.IsCurrentlyOpen = true;
                    slot.Path = targetDock.Id;

                    _logger.LogDebug("Module {ModuleId} inserted successfully ({NewCount} total, was {OldCount})",
                        slot.ModuleType, targetDock.VisibleDockables.Count, currentCount);

                    ValidateAndRemoveDuplicates(rootDock);
                }
                else
                {
                    _logger.LogError("Could not find or create dock for position {BasePosition}", basePosition);
                    slot.IsCurrentlyOpen = false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error inserting module {ModuleId}", moduleId);
                slot.IsCurrentlyOpen = false;
            }
            finally
            {
                _modulesBeingAdded.Remove(moduleId);
            }
        }

        /// <summary>
        /// Восстановить корректные иерархические Path для всех контейнеров
        /// </summary>
        private void RebuildContainerPathsRecursive(IDockable dockable, string path)
        {
            if (dockable is IDock dock)
            {
                if (string.IsNullOrEmpty(dock.Id) || !dock.Id.StartsWith("Root"))
                {
                    dock.Id = path;
                    _logger.LogDebug("Rebuilt container path: {Path}", path);
                }

                if (dock.VisibleDockables != null)
                {
                    int childIndex = 0;
                    foreach (var child in dock.VisibleDockables)
                    {
                        if (child is ProportionalDockSplitter)
                            continue;

                        if (child is IDock childDock)
                        {
                            string childPath;

                            if (!string.IsNullOrEmpty(childDock.Id) && childDock.Id.Contains("."))
                            {
                                var segment = _pathBuilder.GetSegmentName(childDock.Id);
                                childPath = _pathBuilder.BuildPath(path, segment);
                            }
                            else
                            {
                                childPath = _pathBuilder.BuildPath(path, $"Panel{childIndex}");
                            }

                            RebuildContainerPathsRecursive(childDock, childPath);
                            childIndex++;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Очистить пустые контейнеры из layout
        /// Скрывает контейнеры вместо удаления для сохранения структуры
        /// </summary>
        public void CleanupEmptyContainers(IRootDock rootDock)
        {
            if (rootDock.VisibleDockables == null) return;

            var mainDock = rootDock.VisibleDockables.FirstOrDefault() as ProportionalDock;
            if (mainDock == null) return;

            HideEmptyContainersRecursive(mainDock);
        }

        /// <summary>
        /// Рекурсивно скрыть пустые контейнеры
        /// НЕ удаляет контейнеры для сохранения структуры
        /// </summary>
        private void HideEmptyContainersRecursive(ProportionalDock dock)
        {
            if (dock.VisibleDockables == null) return;

            foreach (var child in dock.VisibleDockables.ToList())
            {
                if (child is ProportionalDockSplitter)
                    continue;

                if (child is ProportionalDock childPropDock)
                {
                    HideEmptyContainersRecursive(childPropDock);
                }
                else if (child is DocumentDock docDock)
                {
                    if (IsContainerEmpty(docDock))
                    {
                        docDock.Proportion = 0.0;
                        _logger.LogDebug("Hidden empty DocumentDock (Proportion=0): {Id}", docDock.Id);
                    }
                }
            }
        }

        /// <summary>
        /// Проверить что контейнер пустой
        /// </summary>
        private bool IsContainerEmpty(IDock dock)
        {
            if (dock is DocumentDock docDock)
            {
                if (docDock.VisibleDockables == null || docDock.VisibleDockables.Count == 0)
                    return true;

                var nonSplitterCount = docDock.VisibleDockables
                    .Where(d => d is not ProportionalDockSplitter)
                    .Count();

                return nonSplitterCount == 0;
            }

            if (dock is ProportionalDock propDock)
            {
                if (propDock.VisibleDockables == null || propDock.VisibleDockables.Count == 0)
                    return true;

                foreach (var child in propDock.VisibleDockables)
                {
                    if (child is ProportionalDockSplitter)
                        continue;

                    if (child is IDock childDock)
                    {
                        if (!IsContainerEmpty(childDock))
                            return false;
                    }
                }

                return true;
            }

            return true;
        }

        /// <summary>
        /// Сериализовать текущий layout в LayoutTree
        /// Обновляет Path в ModuleSlots
        /// </summary>
        public (LayoutNode? LayoutTree, List<ModuleSlot> UpdatedSlots) SerializeCurrentLayout(
            IRootDock rootDock,
            WorkMode workMode,
            ProjectModuleContext moduleContext)
        {
            try
            {
                if (rootDock.Context as string != workMode.Id)
                {
                    _logger.LogError("RootDock belongs to WorkMode {RootWorkModeId}, but trying to serialize WorkMode {CurrentWorkModeId}",
                        rootDock.Context, workMode.Id);
                    return (null, workMode.ModuleSlots);
                }

                _logger.LogDebug("Serializing current layout to LayoutTree");

                var mainDock = rootDock.VisibleDockables?.FirstOrDefault() as ProportionalDock;
                if (mainDock == null)
                {
                    _logger.LogWarning("No main dock to serialize");
                    return (null, workMode.ModuleSlots);
                }

                var layoutTree = SerializeNodeRecursive(mainDock);
                var updatedSlots = new List<ModuleSlot>(workMode.ModuleSlots);

                UpdateModuleSlotsFromDock(rootDock, updatedSlots, workMode.Id, moduleContext);
                UpdateFloatingModules(rootDock, updatedSlots, workMode.Id, moduleContext);

                _logger.LogDebug("Serialized LayoutTree, {SlotCount} slots updated", updatedSlots.Count);
                return (layoutTree, updatedSlots);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error serializing layout");
                return (null, workMode.ModuleSlots);
            }
        }

        /// <summary>
        /// Валидация и удаление дубликатов модулей во всём layout
        /// Вызывается после создания layout и изменений структуры
        /// </summary>
        public void ValidateAndRemoveDuplicates(IRootDock rootDock)
        {
            var seenModules = new Dictionary<string, string>();
            var duplicatesToRemove = new List<(IDock Container, IDockable Duplicate)>();

            _logger.LogDebug("Starting duplicate validation");

            ScanForDuplicatesRecursive(rootDock, seenModules, duplicatesToRemove);

            if (rootDock.Windows != null && rootDock.Windows.Count > 0)
            {
                foreach (var window in rootDock.Windows)
                {
                    if (window.Layout != null)
                    {
                        ScanForDuplicatesRecursive(window.Layout, seenModules, duplicatesToRemove);
                    }
                }
            }

            if (duplicatesToRemove.Count > 0)
            {
                _logger.LogError("Found {Count} duplicate modules, removing", duplicatesToRemove.Count);

                foreach (var (container, duplicate) in duplicatesToRemove)
                {
                    if (container.VisibleDockables != null)
                    {
                        container.VisibleDockables.Remove(duplicate);
                        _logger.LogDebug("Removed duplicate: {Id}", duplicate.Id);
                    }
                }
            }
            else
            {
                _logger.LogDebug("No duplicates found");
            }
        }

        /// <summary>
        /// Рекурсивно сканировать layout на дубликаты модулей
        /// </summary>
        private void ScanForDuplicatesRecursive(
            IDockable dockable,
            Dictionary<string, string> seenModules,
            List<(IDock, IDockable)> duplicatesToRemove)
        {
            if (dockable is Document document && document.Id != null)
            {
                var moduleId = document.Id.Replace("Module_", "");

                if (seenModules.ContainsKey(moduleId))
                {
                    _logger.LogError("DUPLICATE MODULE: {ModuleId} - first seen in {FirstPath}, duplicate in {CurrentPath}",
                        moduleId, seenModules[moduleId], document.Owner?.Id ?? "unknown");

                    if (document.Owner is IDock owner)
                    {
                        duplicatesToRemove.Add((owner, document));
                    }
                }
                else
                {
                    seenModules[moduleId] = document.Owner?.Id ?? "unknown";
                }
            }

            if (dockable is IDock dock && dock.VisibleDockables != null)
            {
                foreach (var child in dock.VisibleDockables.ToList())
                {
                    ScanForDuplicatesRecursive(child, seenModules, duplicatesToRemove);
                }
            }
        }

        /// <summary>
        /// Рекурсивно сериализовать Dock в LayoutNode
        /// </summary>
        private LayoutNode SerializeNodeRecursive(IDock dock)
        {
            _logger.LogDebug("Serializing node: {Id}, Type: {Type}", dock.Id, dock.GetType().Name);

            var node = new LayoutNode
            {
                Path = dock.Id ?? "Root",
                Proportion = dock.Proportion
            };

            if (dock is ProportionalDock propDock && propDock.VisibleDockables != null && propDock.VisibleDockables.Count > 0)
            {
                node.Type = "ProportionalDock";
                node.Orientation = propDock.Orientation == Orientation.Horizontal ? "Horizontal" : "Vertical";
                node.Children = new List<LayoutNode>();

                var seenPaths = new HashSet<string>();

                foreach (var child in propDock.VisibleDockables)
                {
                    if (child is ProportionalDockSplitter)
                        continue;

                    if (child is IDock childDock)
                    {
                        var childPath = childDock.Id ?? "";
                        if (seenPaths.Contains(childPath))
                        {
                            _logger.LogWarning("Duplicate Path detected in serialization: {Path}, skipping", childPath);
                            continue;
                        }

                        seenPaths.Add(childPath);
                        var childNode = SerializeNodeRecursive(childDock);
                        node.Children.Add(childNode);
                    }
                }

                _logger.LogDebug("Serialized ProportionalDock: {Path}, children: {Count}", node.Path, node.Children.Count);
            }
            else
            {
                node.Type = "DocumentDock";
                node.Orientation = null;
                node.Children = null;
                _logger.LogDebug("Serialized DocumentDock: {Path}", node.Path);
            }

            return node;
        }

        /// <summary>
        /// Обновить ModuleSlots с актуальными Path из UI
        /// </summary>
        private void UpdateModuleSlotsFromDock(
            IRootDock rootDock,
            List<ModuleSlot> slots,
            string workModeId,
            ProjectModuleContext moduleContext)
        {
            var moduleInfo = new Dictionary<string, (string Path, int TabOrder, bool IsActiveTab, string InstanceId)>();

            CollectModuleInfoRecursive(rootDock, moduleInfo, moduleContext);

            _logger.LogDebug("Collected info for {Count} modules from main Dock", moduleInfo.Count);

            if (rootDock.Windows != null && rootDock.Windows.Count > 0)
            {
                _logger.LogDebug("Collecting modules from {Count} float windows", rootDock.Windows.Count);

                foreach (var window in rootDock.Windows)
                {
                    if (window.Host == null)
                    {
                        _logger.LogDebug("Skipping float window without Host: {WindowId}", window.Id);
                        continue;
                    }

                    var floatDock = FindDocumentDockInLayout(window.Layout);
                    if (floatDock != null)
                    {
                        CollectModuleInfoFromFloatWindow(floatDock, moduleInfo, moduleContext);
                    }
                }

                _logger.LogDebug("Total modules after float windows: {Count}", moduleInfo.Count);
            }

            var tabCollection = App.Services.GetRequiredService<ITabCollection>();
            var activeTab = tabCollection.ActiveTab;

            if (activeTab == null)
            {
                _logger.LogError("No active tab for validation");
                return;
            }

            if (activeTab.Workspace == null)
            {
                _logger.LogError("No Workspace in active tab");
                return;
            }

            foreach (var slot in slots)
            {
                if (moduleInfo.TryGetValue(slot.ModuleType, out var info))
                {
                    var moduleInContext = moduleContext.GetModule(info.InstanceId);
                    if (moduleInContext == null)
                    {
                        _logger.LogError("InstanceId {InstanceId} for module {ModuleId} NOT FOUND in ProjectModuleContext",
                            info.InstanceId, slot.ModuleType);
                        continue;
                    }

                    if (moduleInContext.ModuleId != slot.ModuleType)
                    {
                        _logger.LogError("InstanceId {InstanceId} belongs to module {ActualModule}, but slot expects {ExpectedModule}",
                            info.InstanceId, moduleInContext.ModuleId, slot.ModuleType);
                        continue;
                    }

                    if (!string.IsNullOrEmpty(slot.InstanceId) && slot.InstanceId != info.InstanceId)
                    {
                        _logger.LogError("Slot already has InstanceId {SlotId}, but UI shows {UiId}",
                            slot.InstanceId, info.InstanceId);
                        continue;
                    }

                    if (!string.IsNullOrEmpty(info.Path))
                    {
                        var mainDock = rootDock.VisibleDockables?.FirstOrDefault() as IDock;
                        var searchRoot = mainDock ?? rootDock;

                        var containerExists = _pathBuilder.FindContainerByPath(searchRoot, info.Path);
                        if (containerExists == null)
                        {
                            _logger.LogWarning("Path {Path} not found in layout, module might be orphaned: {ModuleId}",
                                info.Path, slot.ModuleType);
                        }
                    }

                    slot.Path = info.Path;
                    slot.TabOrder = info.TabOrder;
                    slot.IsActiveTab = info.IsActiveTab;
                    slot.InstanceId = info.InstanceId;
                    slot.IsCurrentlyOpen = true;

                    _logger.LogDebug("Updated slot: {ModuleId}, Instance: {InstanceId}, Path: {Path}, Tab: {TabOrder}, Active: {IsActiveTab}",
                        slot.ModuleType, info.InstanceId, info.Path, info.TabOrder, info.IsActiveTab);
                }
                else
                {
                    slot.IsCurrentlyOpen = false;
                    slot.Path = null;

                    _logger.LogDebug("Module not in UI, marked as closed: {ModuleId}, InstanceId preserved: {InstanceId}",
                        slot.ModuleType, slot.InstanceId);
                }
            }

            var pathCounts = new Dictionary<string, int>();
            foreach (var slot in slots.Where(s => s.IsCurrentlyOpen && !string.IsNullOrEmpty(s.Path)))
            {
                var key = $"{slot.ModuleType}_{slot.Path}";
                if (pathCounts.ContainsKey(key))
                {
                    pathCounts[key]++;
                }
                else
                {
                    pathCounts[key] = 1;
                }
            }

            var duplicates = pathCounts.Where(kvp => kvp.Value > 1).ToList();
            if (duplicates.Count > 0)
            {
                _logger.LogError("CRITICAL: Found {Count} duplicate module-path combinations!", duplicates.Count);

                foreach (var dup in duplicates)
                {
                    _logger.LogError("Duplicate: {Key} appears {Count} times", dup.Key, dup.Value);
                }

                var seenKeys = new HashSet<string>();
                foreach (var slot in slots.Where(s => s.IsCurrentlyOpen).ToList())
                {
                    var key = $"{slot.ModuleType}_{slot.Path}";

                    if (seenKeys.Contains(key))
                    {
                        _logger.LogWarning("Removing duplicate: {ModuleId} in {Path}, InstanceId: {InstanceId}",
                            slot.ModuleType, slot.Path, slot.InstanceId);
                        slot.IsCurrentlyOpen = false;
                        slot.Path = null;
                    }
                    else
                    {
                        seenKeys.Add(key);
                    }
                }

                var remainingCount = slots.Count(s => s.IsCurrentlyOpen);
                _logger.LogDebug("Deduplication complete: {RemainingCount} unique modules remaining", remainingCount);
            }
        }

        /// <summary>
        /// Рекурсивно собрать информацию о модулях из Dock структуры
        /// </summary>
        private void CollectModuleInfoRecursive(
            IDockable dockable,
            Dictionary<string, (string Path, int TabOrder, bool IsActiveTab, string InstanceId)> moduleInfo,
            ProjectModuleContext moduleContext)
        {
            if (dockable is DocumentDock docDock && docDock.VisibleDockables != null)
            {
                var containerPath = docDock.Id ?? "Root";

                for (int i = 0; i < docDock.VisibleDockables.Count; i++)
                {
                    var child = docDock.VisibleDockables[i];

                    if (child is Document document && document.Id != null)
                    {
                        var moduleId = document.Id.Replace("Module_", "");
                        var isActive = docDock.ActiveDockable == document;

                        string? instanceId = null;

                        try
                        {
                            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                            {
                                instanceId = GetInstanceIdFromDocument(document, moduleContext);
                            }
                            else
                            {
                                _logger.LogWarning("CollectModuleInfoRecursive called from non-UI thread, switching to UI thread");
                                instanceId = Avalonia.Threading.Dispatcher.UIThread.Invoke(() => GetInstanceIdFromDocument(document, moduleContext));
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error getting InstanceId for module: {ModuleId}", moduleId);
                        }

                        if (instanceId != null)
                        {
                            moduleInfo[moduleId] = (containerPath, i, isActive, instanceId);
                            _logger.LogDebug("Found module: {ModuleId} (Instance: {InstanceId}) in {Path}, tab {TabIndex}, active: {IsActive}",
                                moduleId, instanceId, containerPath, i, isActive);
                        }
                        else
                        {
                            _logger.LogWarning("Could not get InstanceId for module: {ModuleId}", moduleId);
                        }
                    }
                }
            }

            if (dockable is IDock dock && dock.VisibleDockables != null)
            {
                foreach (var child in dock.VisibleDockables)
                {
                    CollectModuleInfoRecursive(child, moduleInfo, moduleContext);
                }
            }
        }

        /// <summary>
        /// Собрать информацию о модулях из Float окна
        /// </summary>
        private void CollectModuleInfoFromFloatWindow(
            DocumentDock floatDock,
            Dictionary<string, (string Path, int TabOrder, bool IsActiveTab, string InstanceId)> moduleInfo,
            ProjectModuleContext moduleContext)
        {
            if (floatDock.VisibleDockables == null) return;

            var containerPath = floatDock.Id ?? "Float.0";

            for (int i = 0; i < floatDock.VisibleDockables.Count; i++)
            {
                var child = floatDock.VisibleDockables[i];

                if (child is Document document && document.Id != null)
                {
                    var moduleId = document.Id.Replace("Module_", "");
                    var isActive = floatDock.ActiveDockable == document;

                    string? instanceId = null;

                    try
                    {
                        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                        {
                            instanceId = GetInstanceIdFromDocument(document, moduleContext);
                        }
                        else
                        {
                            _logger.LogWarning("CollectModuleInfoFromFloatWindow called from non-UI thread, switching to UI thread");
                            instanceId = Avalonia.Threading.Dispatcher.UIThread.Invoke(() => GetInstanceIdFromDocument(document, moduleContext));
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error getting InstanceId for float module: {ModuleId}", moduleId);
                    }

                    if (instanceId != null)
                    {
                        moduleInfo[moduleId] = (containerPath, i, isActive, instanceId);
                        _logger.LogDebug("Found FLOAT module: {ModuleId} (Instance: {InstanceId}) in {Path}, tab {TabIndex}, active: {IsActive}",
                            moduleId, instanceId, containerPath, i, isActive);
                    }
                    else
                    {
                        _logger.LogWarning("Could not get InstanceId for float module: {ModuleId}", moduleId);
                    }
                }
            }
        }

        /// <summary>
        /// Обновить информацию о флоат окнах в ModuleSlots
        /// </summary>
        private void UpdateFloatingModules(
            IRootDock rootDock,
            List<ModuleSlot> slots,
            string workModeId,
            ProjectModuleContext moduleContext)
        {
            _logger.LogDebug("rootDock.Windows count: {Count}", rootDock.Windows?.Count ?? 0);

            foreach (var slot in slots)
            {
                if (slot.IsFloating)
                {
                    slot.IsFloating = false;
                    slot.Path = null;
                    _logger.LogDebug("Reset floating flag: {ModuleId}", slot.ModuleType);
                }
            }

            if (rootDock.Windows == null || rootDock.Windows.Count == 0)
            {
                _logger.LogDebug("No floating windows to restore");
                return;
            }

            var windowsData = new List<(string ModuleId, string InstanceId, string Path, double X, double Y, double Width, double Height)>();

            for (int windowIndex = 0; windowIndex < rootDock.Windows.Count; windowIndex++)
            {
                var window = rootDock.Windows[windowIndex];

                if (window.Host is not HostWindow hostWindow)
                    continue;

                var floatDock = FindDocumentDockInLayout(window.Layout);
                if (floatDock != null && floatDock.VisibleDockables != null)
                {
                    var floatPath = $"Float.{windowIndex}";

                    foreach (var dockable in floatDock.VisibleDockables)
                    {
                        if (dockable is Document document)
                        {
                            string moduleId = document.Id.Replace("Module_", "");

                            string? instanceId = null;
                            if (document.Content is Control control && control.DataContext is object viewModel)
                            {
                                var allModules = moduleContext.GetAllModules();
                                var module = allModules.FirstOrDefault(m => m.ViewModel == viewModel);
                                instanceId = module?.InstanceId;
                            }

                            if (instanceId == null)
                            {
                                _logger.LogWarning("Could not get InstanceId for floating module: {ModuleId}", moduleId);
                                continue;
                            }

                            double x = 0, y = 0, width = 800, height = 600;

                            try
                            {
                                if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                                {
                                    hostWindow.GetPosition(out x, out y);
                                    hostWindow.GetSize(out width, out height);
                                }
                                else
                                {
                                    var task = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                                    {
                                        double tx = 0, ty = 0, tw = 800, th = 600;
                                        hostWindow.GetPosition(out tx, out ty);
                                        hostWindow.GetSize(out tw, out th);
                                        return (tx, ty, tw, th);
                                    });
                                    var result = task.GetAwaiter().GetResult();
                                    x = result.Item1;
                                    y = result.Item2;
                                    width = result.Item3;
                                    height = result.Item4;
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Error getting window position");
                            }

                            windowsData.Add((moduleId, instanceId, floatPath, x, y, width, height));
                            _logger.LogDebug("Captured float window: {ModuleId} at ({X}, {Y})", moduleId, x, y);
                        }
                    }
                }
            }

            foreach (var data in windowsData)
            {
                var slot = slots.FirstOrDefault(s => s.InstanceId == data.InstanceId);
                if (slot != null)
                {
                    slot.IsFloating = true;
                    slot.IsCurrentlyOpen = true;
                    slot.Path = data.Path;
                    slot.FloatX = (int)data.X;
                    slot.FloatY = (int)data.Y;
                    slot.FloatWidth = (int)data.Width;
                    slot.FloatHeight = (int)data.Height;
                    _logger.LogDebug("Restored floating: {ModuleId}, Instance: {InstanceId}, Path: {Path}",
                        data.ModuleId, data.InstanceId, data.Path);
                }
            }

            var seenFloatKeys = new HashSet<string>();
            foreach (var slot in slots.Where(s => s.IsFloating).ToList())
            {
                var key = $"{slot.ModuleType}_{slot.InstanceId}";

                if (seenFloatKeys.Contains(key))
                {
                    _logger.LogError("DUPLICATE float slot detected: ModuleId={ModuleId}, InstanceId={InstanceId}, closing duplicate",
                        slot.ModuleType, slot.InstanceId);
                    slot.IsFloating = false;
                    slot.IsCurrentlyOpen = false;
                }
                else
                {
                    seenFloatKeys.Add(key);
                }
            }

            _logger.LogDebug("Updated {Count} floating windows", windowsData.Count);
        }

        /// <summary>
        /// Найти DocumentDock внутри Layout
        /// </summary>
        private DocumentDock? FindDocumentDockInLayout(IDock? layout)
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

            if (layout.VisibleDockables != null)
            {
                foreach (var child in layout.VisibleDockables)
                {
                    if (child is DocumentDock docDock)
                        return docDock;

                    if (child is IDock childDock)
                    {
                        var found = FindDocumentDockInLayout(childDock);
                        if (found != null) return found;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Извлечь InstanceId из Document
        /// </summary>
        private string? GetInstanceIdFromDocument(Document document, ProjectModuleContext moduleContext)
        {
            if (document.Content is Avalonia.Controls.Control control &&
                control.DataContext is object viewModel)
            {
                var allModules = moduleContext.GetAllModules();
                var module = allModules.FirstOrDefault(m => m.ViewModel == viewModel);
                return module?.InstanceId;
            }

            return null;
        }

        /// <summary>
        /// Получить базовую позицию без AsTab суффикса
        /// </summary>
        private static PreferredDockPosition GetBasePosition(PreferredDockPosition position)
        {
            return position switch
            {
                PreferredDockPosition.RightAsTab => PreferredDockPosition.Right,
                PreferredDockPosition.LeftAsTab => PreferredDockPosition.Left,
                PreferredDockPosition.TopAsTab => PreferredDockPosition.Top,
                PreferredDockPosition.BottomAsTab => PreferredDockPosition.Bottom,
                PreferredDockPosition.TopRightAsTab => PreferredDockPosition.TopRight,
                PreferredDockPosition.TopLeftAsTab => PreferredDockPosition.TopLeft,
                PreferredDockPosition.BottomRightAsTab => PreferredDockPosition.BottomRight,
                PreferredDockPosition.BottomLeftAsTab => PreferredDockPosition.BottomLeft,
                _ => position
            };
        }

        public override IDockWindow CreateDockWindow()
        {
            _logger.LogDebug("Creating DockWindow");

            var window = new DockWindow
            {
                Id = Guid.NewGuid().ToString(),
                Factory = this
            };

            _logger.LogDebug("DockWindow created: {Id}", window.Id);
            return window;
        }
    }
}