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
using Writersword.Core.Models.WorkModes;
using Writersword.Modules.Common;
using Writersword.Src.Core.Interfaces.Services;
using Writersword.Src.Core.Interfaces.WorkFlows;
using Writersword.ViewModels;
using Writersword.Views;

namespace Writersword.Src.Infrastructure.Dock
{
    /// <summary>
    /// Фабрика для создания Dock элементов
    /// STATELESS - не хранит состояние, только создаёт структуры
    /// Работает с новой структурой ModuleSlot + SplitContainer
    /// Вся логика управления состоянием перенесена в WorkspaceController
    /// </summary>
    public class DockFactory : Factory
    {
        private readonly ILogger<DockFactory> _logger;

        /// <summary>
        /// Словарь для отслеживания модулей в процессе перемещения
        /// Используется для предотвращения ложных срабатываний событий закрытия
        /// </summary>
        private readonly Dictionary<string, bool> _modulesBeingMoved = new();

        public DockFactory()
        {
            _logger = App.Services.GetService<ILogger<DockFactory>>()!;
        }

        /// <summary>
        /// Инициализация Locators (вызывается ОДИН раз)
        /// Настраивает фабрику для создания окон и элементов Dock
        /// </summary>
        public void Initialize()
        {
            // Локатор контекстов (не используется в текущей реализации)
            ContextLocator = new Dictionary<string, Func<object?>>
            {
                ["Root"] = () => null
            };

            // Локатор окон - создаёт HostWindow для Float окон
            HostWindowLocator = new Dictionary<string, Func<IHostWindow?>>
            {
                [nameof(IDockWindow)] = () =>
                {
                    _logger.LogDebug("HostWindowLocator called - creating HostWindow");
                    return new HostWindow();
                }
            };

            // Локатор dockable элементов (для динамического создания)
            DockableLocator = new Dictionary<string, Func<IDockable?>>();

            _logger.LogDebug("Initialized with custom HostWindow");

            // Диагностика фабрики
            DockDiagnostics.InspectFactoryMethods();
        }

        /// <summary>
        /// Создать layout из WorkMode
        /// Главный метод создания UI структуры
        /// </summary>
        public IRootDock CreateLayout(WorkMode workMode, DocumentTabViewModel? ownerTab = null)
        {
            _logger.LogDebug("Creating layout for: {Title}", workMode.Title);

            var mainDock = CreateDockFromNewStructure(workMode, ownerTab);

            // Создаём корневой Dock
            var rootDock = new RootDock
            {
                Id = "Root",
                Title = "Root",
                ActiveDockable = mainDock,
                DefaultDockable = mainDock,
                Factory = this
            };

            if (rootDock.VisibleDockables == null)
                rootDock.VisibleDockables = new List<IDockable>();

            rootDock.VisibleDockables.Add(mainDock);

            // Инициализируем layout
            InitLayout(rootDock);

            // Создаём Float окна для модулей с IsFloating = true
            CreateFloatingWindows(rootDock, workMode);

            _logger.LogDebug("Layout created from new structure");

            return rootDock;
        }

        /// <summary>
        /// Создать Dock из новой структуры (Containers + ModuleSlots)
        /// Если нет контейнеров - создаёт простой DocumentDock
        /// </summary>
        private IDock CreateDockFromNewStructure(WorkMode workMode, DocumentTabViewModel? ownerTab)
        {
            _logger.LogDebug("Creating layout from Containers + ModuleSlots");

            if (workMode.Containers == null || workMode.Containers.Count == 0)
            {
                _logger.LogDebug("No containers, creating simple DocumentDock");
                return CreateSimpleDocumentDockFromSlots(workMode, ownerTab);
            }

            var rootContainer = workMode.Containers.FirstOrDefault(c => c.Id == "Root");
            if (rootContainer == null)
            {
                _logger.LogDebug("No Root container found, using first");
                rootContainer = workMode.Containers[0];
            }

            var dock = CreateDockFromContainer(rootContainer, workMode, ownerTab);

            _logger.LogDebug("Layout created from {Count} containers", workMode.Containers.Count);

            return dock;
        }

        /// <summary>
        /// Создать флоат окна для модулей с IsFloating = true
        /// Вызывается ПОСЛЕ создания основного layout
        /// </summary>
        private void CreateFloatingWindows(IRootDock rootDock, WorkMode workMode)
        {
            var floatingModules = workMode.ModuleSlots.Where(s => s.IsFloating).ToList();

            if (floatingModules.Count == 0)
            {
                _logger.LogDebug("No floating modules");
                return;
            }

            _logger.LogDebug("Creating {Count} floating windows", floatingModules.Count);

            foreach (var floatSlot in floatingModules)
            {
                var document = CreateModuleDocument(floatSlot);
                if (document == null)
                {
                    _logger.LogWarning("Failed to create document for floating module: {ModuleId}", floatSlot.ModuleId);
                    continue;
                }

                // Создаём HostWindow для флоат окна
                var hostWindow = new HostWindow();

                // Настраиваем документ для Float
                if (document is Document doc)
                {
                    doc.Owner = rootDock;
                    doc.CanFloat = true;
                }

                InitDockable(document, rootDock);

                // Создаём DocumentDock для флоат окна
                var floatDock = new DocumentDock
                {
                    Id = $"Float_{floatSlot.ModuleId}",
                    Title = document.Title,
                    CanCreateDocument = false,
                    Factory = this
                };

                if (floatDock.VisibleDockables == null)
                    floatDock.VisibleDockables = new List<IDockable>();

                floatDock.VisibleDockables.Add(document);
                floatDock.ActiveDockable = document;

                // Настраиваем окно
                hostWindow.SetTitle(document.Title);

                if (floatSlot.FloatWidth > 0 && floatSlot.FloatHeight > 0)
                {
                    hostWindow.SetSize(floatSlot.FloatWidth, floatSlot.FloatHeight);
                }

                if (floatSlot.FloatX > 0 || floatSlot.FloatY > 0)
                {
                    hostWindow.SetPosition(floatSlot.FloatX, floatSlot.FloatY);
                }

                // Добавляем флоат окно в RootDock
                AddFloatWindow(rootDock, floatDock, hostWindow);

                _logger.LogDebug("Created floating window: {ModuleId} at ({X}, {Y})", floatSlot.ModuleId, floatSlot.FloatX, floatSlot.FloatY);
            }
        }

        /// <summary>
        /// Добавить флоат окно в RootDock
        /// Создаёт отдельный RootDock для флоат окна и добавляет в Windows коллекцию
        /// </summary>
        private void AddFloatWindow(IRootDock rootDock, IDock floatDock, IHostWindow hostWindow)
        {
            if (rootDock.Windows == null)
                rootDock.Windows = new ObservableCollectionExtended<IDockWindow>();

            // Создаём отдельный RootDock для флоат окна
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

            // Устанавливаем layout и показываем окно
            hostWindow.SetLayout(floatRootDock);
            hostWindow.Present(false);

            _logger.LogDebug("Float window added to RootDock: {Id}", floatDock.Id);
        }

        /// <summary>
        /// Рекурсивно создать Dock из SplitContainer
        /// Если контейнер конечный (нет детей) - создаёт DocumentDock
        /// Если есть дети - создаёт ProportionalDock со split
        /// </summary>
        private IDock CreateDockFromContainer(SplitContainer container, WorkMode workMode, DocumentTabViewModel? ownerTab)
        {
            _logger.LogDebug("Processing container: {Id}, Orientation: {Orientation}", container.Id, container.Orientation);

            if (container.Children == null || container.Children.Count == 0)
            {
                _logger.LogDebug("Container {Id} is leaf - creating DocumentDock", container.Id);
                return CreateDocumentDockForContainer(container, workMode, ownerTab);
            }

            // Если есть дети - создаём ProportionalDock со split
            _logger.LogDebug("Container {Id} has {Count} children", container.Id, container.Children.Count);

            var orientation = container.Orientation switch
            {
                "Horizontal" => Orientation.Horizontal,
                "Vertical" => Orientation.Vertical,
                _ => Orientation.Horizontal
            };

            var proportionalDock = new ProportionalDock
            {
                Id = container.Id,
                Title = container.Id,
                Proportion = container.Proportion > 0 ? container.Proportion : double.NaN,
                Orientation = orientation
            };

            if (proportionalDock.VisibleDockables == null)
                proportionalDock.VisibleDockables = new List<IDockable>();

            // Рекурсивно создаём дочерние dock'и + сплиттеры между ними
            for (int i = 0; i < container.Children.Count; i++)
            {
                var childDock = CreateDockFromContainer(container.Children[i], workMode, ownerTab);
                proportionalDock.VisibleDockables.Add(childDock);

                // Добавляем сплиттер после каждого элемента кроме последнего
                if (i < container.Children.Count - 1)
                {
                    var splitter = new ProportionalDockSplitter
                    {
                        Id = $"Splitter_{container.Id}_{i}",
                        Title = $"Splitter_{i}"
                    };
                    proportionalDock.VisibleDockables.Add(splitter);
                }
            }

            // Активируем первую панель (не сплиттер)
            proportionalDock.ActiveDockable = proportionalDock.VisibleDockables
                .FirstOrDefault(d => d is not ProportionalDockSplitter);

            _logger.LogDebug("Created ProportionalDock: {Id}, children: {Count}", container.Id, container.Children.Count);

            return proportionalDock;
        }

        /// <summary>
        /// Создать DocumentDock для конечного контейнера
        /// Заполняет модулями из ModuleSlots где ContainerId совпадает
        /// </summary>
        private DocumentDock CreateDocumentDockForContainer(SplitContainer container, WorkMode workMode, DocumentTabViewModel? ownerTab)
        {
            var documents = new List<IDockable>();

            var modulesInContainer = workMode.ModuleSlots
                .Where(slot => slot.ContainerId == container.Id && !slot.IsFloating)
                .OrderBy(slot => slot.TabOrder)
                .ToList();

            _logger.LogDebug("Container {Id} has {Count} modules", container.Id, modulesInContainer.Count);

            foreach (var slot in modulesInContainer)
            {
                var doc = CreateModuleDocument(slot, ownerTab);
                if (doc != null)
                {
                    documents.Add(doc);
                }
            }

            // Находим активный модуль
            var activeSlot = modulesInContainer.FirstOrDefault(s => s.IsActiveTab);
            var activeDoc = activeSlot != null
                ? documents.FirstOrDefault(d => d.Id == $"Module_{activeSlot.ModuleId}")
                : documents.FirstOrDefault();

            var documentDock = new DocumentDock
            {
                Id = container.Id,
                Title = container.Id,
                Proportion = container.Proportion > 0 ? container.Proportion : 0.5,
                ActiveDockable = activeDoc,
                CanCreateDocument = false
            };

            if (documentDock.VisibleDockables == null)
                documentDock.VisibleDockables = new List<IDockable>();

            foreach (var doc in documents)
            {
                documentDock.VisibleDockables.Add(doc);
            }

            _logger.LogDebug("Created DocumentDock: {Id}, modules: {Count}, active: {ActiveId}", container.Id, documents.Count, activeDoc?.Id);

            return documentDock;
        }

        /// <summary>
        /// Создать простой DocumentDock если нет структуры контейнеров
        /// Используется как fallback
        /// </summary>
        private DocumentDock CreateSimpleDocumentDockFromSlots(WorkMode workMode, DocumentTabViewModel? ownerTab)
        {
            var documents = new List<IDockable>();

            foreach (var slot in workMode.ModuleSlots.Where(s => !s.IsFloating))
            {
                var doc = CreateModuleDocument(slot, ownerTab);
                if (doc != null)
                {
                    documents.Add(doc);
                }
            }

            var documentDock = new DocumentDock
            {
                Id = "Documents",
                Title = "Documents",
                Proportion = double.NaN,
                ActiveDockable = documents.Count > 0 ? documents[0] : null,
                CanCreateDocument = false
            };

            if (documentDock.VisibleDockables == null)
                documentDock.VisibleDockables = new List<IDockable>();

            foreach (var doc in documents)
            {
                documentDock.VisibleDockables.Add(doc);
            }

            _logger.LogDebug("Created simple DocumentDock with {Count} modules", documents.Count);

            return documentDock;
        }

        /// <summary>
        /// Создать Document для модуля
        /// Создаёт модуль через ProjectModuleContext и оборачивает в Document
        /// ЗАЩИТА: проверяет что модуль ещё не создан
        /// </summary>
        public IDockable? CreateModuleDocument(ModuleSlot slot, DocumentTabViewModel? ownerTab = null)
        {
            _logger.LogDebug("Creating document for: {ModuleId}", slot.ModuleId);

            var tab = ownerTab ?? App.Services.GetRequiredService<ITabCollection>().ActiveTab;

            if (tab == null)
            {
                _logger.LogError("No tab provided and no active tab");
                return null;
            }

            if (!string.IsNullOrEmpty(slot.InstanceId))
            {
                var existingModule = tab.ModuleContext.GetModule(slot.InstanceId);
                if (existingModule != null)
                {
                    _logger.LogError("Module already exists with InstanceId: {InstanceId}", slot.InstanceId);
                    _logger.LogError("Skipping duplicate creation!");
                    return null;
                }
            }

            string? instanceIdToUse = null;
            object? customDataToRestore = null;

            var project = tab.GetProject();

            if (project.ModulesData.TryGetValue(slot.ModuleId, out var data))
            {
                customDataToRestore = data;
                if (customDataToRestore != null)
                {
                    _logger.LogDebug("Found CustomData in module data");
                }
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

            var module = tab.ModuleContext.CreateModule(slot.ModuleId, instanceIdToUse);

            if (module?.ViewModel == null)
            {
                _logger.LogWarning("Module not created: {ModuleId}", slot.ModuleId);
                return null;
            }

            if (string.IsNullOrEmpty(slot.InstanceId))
            {
                slot.InstanceId = module.InstanceId;
                _logger.LogDebug("Saved InstanceId to slot: {InstanceId}", module.InstanceId);
            }

            module.Context = tab.Context;
            _logger.LogDebug("Context assigned to module: {ModuleId}", slot.ModuleId);

            if (customDataToRestore != null)
            {
                module.SetCustomData(customDataToRestore);
                _logger.LogDebug("Restored CustomData for: {ModuleId}", slot.ModuleId);
            }

            var view = module.CreateView();
            if (view == null)
            {
                _logger.LogWarning("No View: {ModuleId}", slot.ModuleId);
                return null;
            }

            string stableId = $"Module_{slot.ModuleId}";

            var document = new Document
            {
                Id = stableId,
                Title = module.Title,
                Content = view,
                CanClose = slot.IsCloseable,
                CanFloat = slot.IsCloseable
            };

            _logger.LogDebug("Document created: {ModuleId}, InstanceId: {InstanceId}, CanClose={CanClose}", slot.ModuleId, module.InstanceId, document.CanClose);

            return document;
        }

        /// <summary>
        /// Вставить новый модуль в существующий layout по PreferredPosition
        /// Используется при динамическом добавлении модулей
        /// </summary>
        public void InsertModuleByPreference(IRootDock rootDock, ModuleSlot slot)
        {
            _logger.LogDebug("Inserting module {ModuleId} with position {Position}", slot.ModuleId, slot.PreferredPosition);

            var document = CreateModuleDocument(slot);
            if (document == null)
            {
                _logger.LogWarning("Failed to create document for {ModuleId}", slot.ModuleId);
                return;
            }

            var basePosition = GetBasePosition(slot.PreferredPosition);
            var isTab = slot.PreferredPosition.ToString().EndsWith("AsTab");

            _logger.LogDebug("Base position: {BasePosition}, IsTab: {IsTab}", basePosition, isTab);

            var targetDock = FindOrCreateDockForPosition(rootDock, basePosition, isTab);

            if (targetDock != null)
            {
                if (targetDock.VisibleDockables == null)
                    targetDock.VisibleDockables = new List<IDockable>();

                targetDock.VisibleDockables.Add(document);
                targetDock.ActiveDockable = document;

                // Регистрируем документ для Float системы
                SetOwnerAndRegisterForFloat(document, targetDock);

                _logger.LogDebug("Module {ModuleId} inserted successfully", slot.ModuleId);
            }
            else
            {
                _logger.LogError("Could not find or create dock for position {BasePosition}", basePosition);
            }
        }

        /// <summary>
        /// Эмулирует drag&drop для регистрации документа в Float системе
        /// Необходимо для корректной работы Float функциональности
        /// </summary>
        private void SetOwnerAndRegisterForFloat(IDockable document, IDock owner)
        {
            _logger.LogDebug("Emulating drag&drop: {Id}", document.Id);

            if (owner.Factory == null)
            {
                owner.Factory = this;
            }

            InitDockable(document, owner);

            if (document is Document doc)
            {
                doc.CanFloat = true;
            }

            string moduleId = document.Id?.Replace("Module_", "") ?? "";

            try
            {
                var sourceDock = document.Owner as IDock;
                if (sourceDock == null || sourceDock.VisibleDockables == null)
                {
                    _modulesBeingMoved[moduleId] = false;
                    return;
                }

                var targetDock = FindAnotherDock(sourceDock);
                if (targetDock == null)
                {
                    _modulesBeingMoved[moduleId] = false;
                    return;
                }

                _modulesBeingMoved[moduleId] = true;

                // Временно перемещаем документ для регистрации
                var originalIndex = sourceDock.VisibleDockables.IndexOf(document);
                sourceDock.VisibleDockables.Remove(document);

                if (targetDock.VisibleDockables == null)
                    targetDock.VisibleDockables = new List<IDockable>();

                targetDock.VisibleDockables.Add(document);
                targetDock.ActiveDockable = document;

                // Возвращаем документ на место
                targetDock.VisibleDockables.Remove(document);
                sourceDock.VisibleDockables.Insert(originalIndex, document);
                sourceDock.ActiveDockable = document;

                _modulesBeingMoved[moduleId] = false;

                _logger.LogDebug("Move complete");
            }
            catch (Exception ex)
            {
                _modulesBeingMoved[moduleId] = false;
                _logger.LogError(ex, "Move failed");
            }
        }

        /// <summary>
        /// Найти другой DocumentDock для эмуляции drag&drop
        /// </summary>
        private DocumentDock? FindAnotherDock(IDock sourceDock)
        {
            var root = sourceDock;
            while (root.Owner != null)
            {
                root = root.Owner as IDock;
                if (root == null) break;
            }

            if (root == null) return null;

            return FindAnotherDockRecursive(root, sourceDock);
        }

        /// <summary>
        /// Рекурсивно найти другой DocumentDock
        /// </summary>
        private DocumentDock? FindAnotherDockRecursive(IDockable dockable, IDock exclude)
        {
            if (dockable is DocumentDock dd && dockable != exclude)
            {
                return dd;
            }

            if (dockable is IDock dock && dock.VisibleDockables != null)
            {
                foreach (var child in dock.VisibleDockables)
                {
                    var found = FindAnotherDockRecursive(child, exclude);
                    if (found != null) return found;
                }
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

        /// <summary>
        /// Найти или создать Dock для позиции
        /// </summary>
        private IDock? FindOrCreateDockForPosition(IRootDock rootDock, PreferredDockPosition position, bool asTab)
        {
            var mainDock = rootDock.VisibleDockables?.FirstOrDefault() as ProportionalDock;
            if (mainDock == null)
            {
                _logger.LogWarning("No main ProportionalDock found");
                return null;
            }

            return position switch
            {
                PreferredDockPosition.Right => FindOrCreateRightDock(mainDock, PreferredDockPosition.Right, asTab),
                PreferredDockPosition.TopRight => FindOrCreateRightDock(mainDock, PreferredDockPosition.TopRight, asTab),
                PreferredDockPosition.BottomRight => FindOrCreateRightDock(mainDock, PreferredDockPosition.BottomRight, asTab),
                PreferredDockPosition.Left => FindOrCreateLeftDock(mainDock, PreferredDockPosition.Left, asTab),
                PreferredDockPosition.TopLeft => FindOrCreateLeftDock(mainDock, PreferredDockPosition.TopLeft, asTab),
                PreferredDockPosition.BottomLeft => FindOrCreateLeftDock(mainDock, PreferredDockPosition.BottomLeft, asTab),
                PreferredDockPosition.Bottom => FindOrCreateBottomDock(mainDock, asTab),
                PreferredDockPosition.Top => FindOrCreateTopDock(mainDock, asTab),
                _ => null
            };
        }

        /// <summary>
        /// Найти или создать правый Dock
        /// </summary>
        private IDock? FindOrCreateRightDock(ProportionalDock mainDock, PreferredDockPosition position, bool asTab)
        {
            _logger.LogDebug("FindOrCreateRightDock: {Position}, asTab={AsTab}", position, asTab);

            ProportionalDock searchDock = mainDock;

            if (mainDock.Orientation == Orientation.Horizontal)
            {
                var rightElement = mainDock.VisibleDockables?.LastOrDefault(d => d is not ProportionalDockSplitter);

                if (rightElement is ProportionalDock nestedLayout && nestedLayout.Orientation == Orientation.Vertical)
                {
                    _logger.LogDebug("Found nested vertical layout: {Id}", nestedLayout.Id);
                    searchDock = nestedLayout;
                }
            }

            List<IDock> panels;
            if (searchDock.Orientation == Orientation.Vertical)
            {
                panels = CollectAllDocumentDocks(searchDock);
            }
            else
            {
                panels = FindPanelsInDirection(searchDock, "Right");
            }

            if (asTab && panels.Count > 0)
            {
                var targetPanel = position switch
                {
                    PreferredDockPosition.BottomRight => panels.Last(),
                    PreferredDockPosition.TopRight => panels.First(),
                    _ => panels.First()
                };

                _logger.LogDebug("Using existing panel: {Id}", targetPanel.Id);
                return targetPanel;
            }

            _logger.LogDebug("Creating new right panel");
            var newPanel = new DocumentDock
            {
                Id = $"Right_{Guid.NewGuid()}",
                Title = "Right",
                Proportion = double.NaN,
                CanCreateDocument = false
            };

            InsertPanelInDirection(searchDock, newPanel, "Right", position);
            return newPanel;
        }

        /// <summary>
        /// Собрать все DocumentDock из ProportionalDock
        /// </summary>
        private static List<IDock> CollectAllDocumentDocks(ProportionalDock dock)
        {
            var result = new List<IDock>();
            if (dock.VisibleDockables == null) return result;

            foreach (var child in dock.VisibleDockables)
            {
                if (child is DocumentDock dd)
                {
                    result.Add(dd);
                }
                else if (child is ProportionalDock pd)
                {
                    result.AddRange(CollectAllDocumentDocks(pd));
                }
            }

            return result;
        }

        /// <summary>
        /// Найти или создать левый Dock
        /// </summary>
        private IDock? FindOrCreateLeftDock(ProportionalDock mainDock, PreferredDockPosition position, bool asTab)
        {
            _logger.LogDebug("FindOrCreateLeftDock: {Position}, asTab={AsTab}", position, asTab);

            var leftPanels = FindPanelsInDirection(mainDock, "Left");

            if (asTab && leftPanels.Count > 0)
            {
                return position switch
                {
                    PreferredDockPosition.BottomLeft => leftPanels.Last(),
                    PreferredDockPosition.TopLeft => leftPanels.First(),
                    _ => leftPanels.First()
                };
            }

            var newPanel = new DocumentDock
            {
                Id = $"Left_{Guid.NewGuid()}",
                Title = "Left",
                Proportion = double.NaN,
                CanCreateDocument = false
            };

            InsertPanelInDirection(mainDock, newPanel, "Left", position);
            return newPanel;
        }

        /// <summary>
        /// Найти или создать нижний Dock
        /// </summary>
        private IDock? FindOrCreateBottomDock(ProportionalDock mainDock, bool asTab)
        {
            _logger.LogDebug("FindOrCreateBottomDock: asTab={AsTab}", asTab);

            var bottomPanels = FindPanelsInDirection(mainDock, "Bottom");

            if (asTab && bottomPanels.Count > 0)
            {
                return bottomPanels.First();
            }

            var newPanel = new DocumentDock
            {
                Id = $"Bottom_{Guid.NewGuid()}",
                Title = "Bottom",
                Proportion = 0.3,
                CanCreateDocument = false
            };

            InsertPanelInDirection(mainDock, newPanel, "Bottom", PreferredDockPosition.Bottom);
            return newPanel;
        }

        /// <summary>
        /// Найти или создать верхний Dock
        /// </summary>
        private IDock? FindOrCreateTopDock(ProportionalDock mainDock, bool asTab)
        {
            _logger.LogDebug("FindOrCreateTopDock: asTab={AsTab}", asTab);

            var topPanels = FindPanelsInDirection(mainDock, "Top");

            if (asTab && topPanels.Count > 0)
            {
                return topPanels.First();
            }

            var newPanel = new DocumentDock
            {
                Id = $"Top_{Guid.NewGuid()}",
                Title = "Top",
                Proportion = 0.3,
                CanCreateDocument = false
            };

            InsertPanelInDirection(mainDock, newPanel, "Top", PreferredDockPosition.Top);
            return newPanel;
        }

        /// <summary>
        /// Найти панели в заданном направлении
        /// </summary>
        private static List<IDock> FindPanelsInDirection(ProportionalDock mainDock, string direction)
        {
            var panels = new List<IDock>();
            if (mainDock.VisibleDockables == null) return panels;

            switch (direction)
            {
                case "Right":
                    if (mainDock.Orientation == Orientation.Horizontal && mainDock.VisibleDockables.Count > 1)
                    {
                        var rightElement = mainDock.VisibleDockables.Last();
                        CollectDocksRecursive(rightElement, panels);
                    }
                    break;

                case "Left":
                    if (mainDock.Orientation == Orientation.Horizontal && mainDock.VisibleDockables.Count > 0)
                    {
                        var leftElement = mainDock.VisibleDockables.First();
                        CollectDocksRecursive(leftElement, panels);
                    }
                    break;

                case "Bottom":
                    if (mainDock.Orientation == Orientation.Vertical && mainDock.VisibleDockables.Count > 1)
                    {
                        var bottomElement = mainDock.VisibleDockables.Last();
                        CollectDocksRecursive(bottomElement, panels);
                    }
                    break;

                case "Top":
                    if (mainDock.Orientation == Orientation.Vertical && mainDock.VisibleDockables.Count > 0)
                    {
                        var topElement = mainDock.VisibleDockables.First();
                        CollectDocksRecursive(topElement, panels);
                    }
                    break;
            }

            return panels;
        }

        /// <summary>
        /// Рекурсивно собрать Dock из элемента
        /// </summary>
        private static void CollectDocksRecursive(IDockable element, List<IDock> result)
        {
            if (element is ProportionalDock propDock && propDock.VisibleDockables != null)
            {
                foreach (var child in propDock.VisibleDockables)
                {
                    if (child is IDock dock && !(child is ProportionalDock))
                    {
                        result.Add(dock);
                    }
                    else
                    {
                        CollectDocksRecursive(child, result);
                    }
                }
            }
            else if (element is IDock dock)
            {
                result.Add(dock);
            }
        }

        /// <summary>
        /// Вставить панель в заданном направлении
        /// </summary>
        private void InsertPanelInDirection(ProportionalDock mainDock, IDock newPanel, string direction, PreferredDockPosition position)
        {
            _logger.LogDebug("InsertPanelInDirection: {Direction}, position={Position}", direction, position);

            if (mainDock.VisibleDockables == null)
                mainDock.VisibleDockables = new List<IDockable>();

            switch (direction)
            {
                case "Right":
                    InsertRightPanel(mainDock, newPanel, position);
                    break;

                case "Left":
                    InsertLeftPanel(mainDock, newPanel, position);
                    break;

                case "Bottom":
                    InsertBottomPanel(mainDock, newPanel);
                    break;

                case "Top":
                    InsertTopPanel(mainDock, newPanel);
                    break;
            }
        }

        /// <summary>
        /// Вставить панель справа
        /// </summary>
        private void InsertRightPanel(ProportionalDock mainDock, IDock newPanel, PreferredDockPosition position)
        {
            if (mainDock.Orientation == Orientation.Horizontal && mainDock.VisibleDockables!.Count > 1)
            {
                var rightElement = mainDock.VisibleDockables.Last();

                if (rightElement is ProportionalDock rightDock && rightDock.Orientation == Orientation.Vertical)
                {
                    if (rightDock.VisibleDockables == null)
                        rightDock.VisibleDockables = new List<IDockable>();

                    if (position == PreferredDockPosition.TopRight)
                    {
                        rightDock.VisibleDockables.Insert(0, newPanel);
                    }
                    else
                    {
                        rightDock.VisibleDockables.Add(newPanel);
                    }

                    _logger.LogDebug("Added to existing right vertical split");
                    return;
                }

                var verticalSplit = new ProportionalDock
                {
                    Id = $"RightVertical_{Guid.NewGuid()}",
                    Orientation = Orientation.Vertical,
                    Proportion = rightElement is IDock dock ? dock.Proportion : 0.3
                };

                if (verticalSplit.VisibleDockables == null)
                    verticalSplit.VisibleDockables = new List<IDockable>();

                verticalSplit.VisibleDockables.Add(rightElement);

                if (position == PreferredDockPosition.TopRight)
                {
                    verticalSplit.VisibleDockables.Insert(0, newPanel);
                }
                else
                {
                    verticalSplit.VisibleDockables.Add(newPanel);
                }

                mainDock.VisibleDockables[mainDock.VisibleDockables.Count - 1] = verticalSplit;

                _logger.LogDebug("Created new right vertical split");
            }
            else
            {
                if (mainDock.Orientation != Orientation.Horizontal)
                {
                    mainDock.Orientation = Orientation.Horizontal;
                }

                var splitter = new ProportionalDockSplitter
                {
                    Id = $"Splitter_{mainDock.VisibleDockables!.Count}",
                    Title = $"Splitter_{mainDock.VisibleDockables.Count}"
                };

                mainDock.VisibleDockables.Add(splitter);

                newPanel.Proportion = 0.3;
                mainDock.VisibleDockables.Add(newPanel);

                _logger.LogDebug("Added first right panel with splitter");
            }
        }

        /// <summary>
        /// Вставить панель слева
        /// </summary>
        private void InsertLeftPanel(ProportionalDock mainDock, IDock newPanel, PreferredDockPosition position)
        {
            if (mainDock.Orientation == Orientation.Horizontal && mainDock.VisibleDockables!.Count > 0)
            {
                var leftElement = mainDock.VisibleDockables.First();

                if (leftElement is ProportionalDock leftDock && leftDock.Orientation == Orientation.Vertical)
                {
                    if (leftDock.VisibleDockables == null)
                        leftDock.VisibleDockables = new List<IDockable>();

                    if (position == PreferredDockPosition.TopLeft)
                    {
                        leftDock.VisibleDockables.Insert(0, newPanel);
                    }
                    else
                    {
                        leftDock.VisibleDockables.Add(newPanel);
                    }

                    _logger.LogDebug("Added to existing left vertical split");
                    return;
                }

                var verticalSplit = new ProportionalDock
                {
                    Id = $"LeftVertical_{Guid.NewGuid()}",
                    Orientation = Orientation.Vertical,
                    Proportion = leftElement is IDock dock ? dock.Proportion : 0.7
                };

                if (verticalSplit.VisibleDockables == null)
                    verticalSplit.VisibleDockables = new List<IDockable>();

                verticalSplit.VisibleDockables.Add(leftElement);

                if (position == PreferredDockPosition.TopLeft)
                {
                    verticalSplit.VisibleDockables.Insert(0, newPanel);
                }
                else
                {
                    verticalSplit.VisibleDockables.Add(newPanel);
                }

                mainDock.VisibleDockables[0] = verticalSplit;

                _logger.LogDebug("Created new left vertical split");
            }
            else
            {
                if (mainDock.Orientation != Orientation.Horizontal)
                {
                    mainDock.Orientation = Orientation.Horizontal;
                }

                newPanel.Proportion = 0.3;
                mainDock.VisibleDockables!.Insert(0, newPanel);

                _logger.LogDebug("Added first left panel");
            }
        }

        /// <summary>
        /// Вставить панель снизу
        /// </summary>
        private void InsertBottomPanel(ProportionalDock mainDock, IDock newPanel)
        {
            if (mainDock.VisibleDockables == null)
                mainDock.VisibleDockables = new List<IDockable>();

            var currentContent = mainDock.VisibleDockables.ToList();
            mainDock.VisibleDockables.Clear();

            var contentDock = new ProportionalDock
            {
                Id = $"Content_{Guid.NewGuid()}",
                Orientation = mainDock.Orientation,
                Proportion = 0.7
            };

            if (contentDock.VisibleDockables == null)
                contentDock.VisibleDockables = new List<IDockable>();

            foreach (var item in currentContent)
            {
                contentDock.VisibleDockables.Add(item);
            }

            mainDock.Orientation = Orientation.Vertical;
            mainDock.VisibleDockables.Add(contentDock);
            mainDock.VisibleDockables.Add(newPanel);

            _logger.LogDebug("Added bottom panel with vertical split");
        }

        /// <summary>
        /// Вставить панель сверху
        /// </summary>
        private static void InsertTopPanel(ProportionalDock mainDock, IDock newPanel)
        {
            if (mainDock.VisibleDockables == null)
                mainDock.VisibleDockables = new List<IDockable>();

            var currentContent = mainDock.VisibleDockables.ToList();
            mainDock.VisibleDockables.Clear();

            var contentDock = new ProportionalDock
            {
                Id = $"Content_{Guid.NewGuid()}",
                Orientation = mainDock.Orientation,
                Proportion = 0.7
            };

            if (contentDock.VisibleDockables == null)
                contentDock.VisibleDockables = new List<IDockable>();

            foreach (var item in currentContent)
            {
                contentDock.VisibleDockables.Add(item);
            }

            mainDock.Orientation = Orientation.Vertical;
            mainDock.VisibleDockables.Add(newPanel);
            mainDock.VisibleDockables.Add(contentDock);
        }

        /// <summary>
        /// Сериализовать текущий layout в новую структуру (Containers + ModuleSlots с обновлёнными данными)
        /// Используется при сохранении workspace.json
        /// </summary>
        public (List<SplitContainer> Containers, List<ModuleSlot> UpdatedSlots) SerializeCurrentLayout(IRootDock rootDock, WorkMode workMode)
        {
            try
            {
                _logger.LogDebug("Serializing current layout to new structure");

                var mainDock = rootDock.VisibleDockables?.FirstOrDefault() as ProportionalDock;
                if (mainDock == null)
                {
                    _logger.LogWarning("No main dock to serialize");
                    return (new List<SplitContainer>(), workMode.ModuleSlots);
                }

                var containers = new List<SplitContainer>();
                var updatedSlots = new List<ModuleSlot>(workMode.ModuleSlots);

                // Сериализуем структуру контейнеров
                var rootContainer = SerializeContainerRecursive(mainDock, "Root");
                containers.Add(rootContainer);

                // Обновляем ModuleSlots с актуальными данными из UI
                UpdateModuleSlotsFromDock(rootDock, updatedSlots);

                // Обновляем флоат окна
                UpdateFloatingModules(rootDock, updatedSlots);

                _logger.LogDebug("Serialized: {ContainerCount} containers, {SlotCount} slots updated", containers.Count, updatedSlots.Count);
                return (containers, updatedSlots);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error serializing layout");
                return (new List<SplitContainer>(), workMode.ModuleSlots);
            }
        }

        /// <summary>
        /// Рекурсивно сериализовать Dock в SplitContainer
        /// </summary>
        private SplitContainer SerializeContainerRecursive(IDock dock, string containerId)
        {
            var container = new SplitContainer
            {
                Id = containerId,
                Proportion = dock.Proportion
            };

            if (dock is ProportionalDock propDock && propDock.VisibleDockables != null && propDock.VisibleDockables.Count > 0)
            {
                container.Orientation = propDock.Orientation == Orientation.Horizontal ? "Horizontal" : "Vertical";
                container.Children = new List<SplitContainer>();

                foreach (var child in propDock.VisibleDockables)
                {
                    if (child is ProportionalDockSplitter)
                        continue;

                    if (child is IDock childDock)
                    {
                        // Проверяем что контейнер не пустой перед добавлением
                        if (IsContainerEmpty(childDock))
                        {
                            _logger.LogDebug("Skipping empty container: {Id}", childDock.Id);
                            continue;
                        }

                        var childContainer = SerializeContainerRecursive(childDock, childDock.Id ?? Guid.NewGuid().ToString());
                        container.Children.Add(childContainer);
                    }
                }

                _logger.LogDebug("Serialized container: {Id}, Orientation: {Orientation}, Children: {Count}", container.Id, container.Orientation, container.Children.Count);
            }
            else
            {
                container.Orientation = null;
                container.Children = null;
                _logger.LogDebug("Serialized leaf container: {Id}", container.Id);
            }

            return container;
        }

        /// <summary>
        /// Проверить что контейнер пустой (не содержит модулей)
        /// </summary>
        private bool IsContainerEmpty(IDock dock)
        {
            // Если это DocumentDock - проверяем есть ли в нём документы
            if (dock is DocumentDock docDock)
            {
                return docDock.VisibleDockables == null || docDock.VisibleDockables.Count == 0;
            }

            // Если это ProportionalDock - проверяем рекурсивно все дочерние элементы
            if (dock is ProportionalDock propDock && propDock.VisibleDockables != null)
            {
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
            }

            return true;
        }

        /// <summary>
        /// Обновить ModuleSlots с актуальными данными из UI
        /// Устанавливает ContainerId, TabOrder, IsActiveTab, InstanceId для каждого модуля
        /// </summary>
        private void UpdateModuleSlotsFromDock(IRootDock rootDock, List<ModuleSlot> slots)
        {
            var moduleInfo = new Dictionary<string, (string ContainerId, int TabOrder, bool IsActiveTab, string InstanceId)>();

            CollectModuleInfoRecursive(rootDock, moduleInfo);

            _logger.LogDebug("Collected info for {Count} modules from UI", moduleInfo.Count);

            foreach (var slot in slots)
            {
                if (moduleInfo.TryGetValue(slot.ModuleId, out var info))
                {
                    slot.ContainerId = info.ContainerId;
                    slot.TabOrder = info.TabOrder;
                    slot.IsActiveTab = info.IsActiveTab;
                    slot.InstanceId = info.InstanceId;

                    _logger.LogDebug("Updated slot: {ModuleId}, Instance: {InstanceId}, Container: {ContainerId}, Tab: {TabOrder}, Active: {IsActiveTab}",
                        slot.ModuleId, info.InstanceId, info.ContainerId, info.TabOrder, info.IsActiveTab);
                }
            }
        }

        /// <summary>
        /// Обновить информацию о флоат окнах в ModuleSlots
        /// Сбрасывает все флаги, затем восстанавливает только видимые окна
        /// </summary>
        private void UpdateFloatingModules(IRootDock rootDock, List<ModuleSlot> slots)
        {
            _logger.LogDebug("rootDock.Windows count: {Count}", rootDock.Windows?.Count ?? 0);

            // Сначала сбрасываем ВСЕ флоат флаги
            foreach (var slot in slots)
            {
                if (slot.IsFloating)
                {
                    slot.IsFloating = false;
                    slot.ContainerId = null;
                    _logger.LogDebug("Reset floating flag: {ModuleId}", slot.ModuleId);
                }
            }

            // Если нет окон - выходим
            if (rootDock.Windows == null || rootDock.Windows.Count == 0)
            {
                _logger.LogDebug("No floating windows to restore");
                return;
            }

            // Собираем данные ТОЛЬКО из видимых окон
            var windowsData = new List<(string ModuleId, double X, double Y, double Width, double Height)>();

            foreach (var window in rootDock.Windows)
            {
                // Проверяем что окно видимо
                if (window.Host is HostWindow hostWindow)
                {
                    bool isVisible = false;

                    try
                    {
                        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                        {
                            if (hostWindow.GetWindow() is FloatingWindow floatWindow)
                            {
                                isVisible = floatWindow.IsVisible;
                            }
                        }
                        else
                        {
                            var task = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                if (hostWindow.GetWindow() is FloatingWindow floatWindow)
                                {
                                    return floatWindow.IsVisible;
                                }
                                return false;
                            });
                            isVisible = task.GetAwaiter().GetResult();
                        }
                    }
                    catch
                    {
                        isVisible = false;
                    }

                    if (!isVisible)
                    {
                        _logger.LogDebug("Skipping invisible window: {Id}", window.Id);
                        continue;
                    }
                }

                var floatDock = FindDocumentDockInLayout(window.Layout);
                if (floatDock != null && floatDock.VisibleDockables != null)
                {
                    foreach (var dockable in floatDock.VisibleDockables)
                    {
                        if (dockable is Document document)
                        {
                            string moduleId = document.Id.Replace("Module_", "");
                            double x = 0, y = 0, width = 800, height = 600;

                            try
                            {
                                if (window.Host is HostWindow hw)
                                {
                                    if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                                    {
                                        hw.GetPosition(out x, out y);
                                        hw.GetSize(out width, out height);
                                    }
                                    else
                                    {
                                        var task = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                                        {
                                            double tx = 0, ty = 0, tw = 800, th = 600;
                                            hw.GetPosition(out tx, out ty);
                                            hw.GetSize(out tw, out th);
                                            return (tx, ty, tw, th);
                                        });
                                        var result = task.GetAwaiter().GetResult();
                                        x = result.Item1;
                                        y = result.Item2;
                                        width = result.Item3;
                                        height = result.Item4;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Error getting window position");
                            }

                            windowsData.Add((moduleId, x, y, width, height));
                            _logger.LogDebug("Captured visible float window: {ModuleId}", moduleId);
                        }
                    }
                }
            }

            // Обновляем только видимые
            foreach (var data in windowsData)
            {
                var slot = slots.FirstOrDefault(s => s.ModuleId == data.ModuleId);
                if (slot != null)
                {
                    slot.IsFloating = true;
                    slot.ContainerId = null;
                    slot.FloatX = (int)data.X;
                    slot.FloatY = (int)data.Y;
                    slot.FloatWidth = (int)data.Width;
                    slot.FloatHeight = (int)data.Height;
                    _logger.LogDebug("Restored floating: {ModuleId}", data.ModuleId);
                }
            }

            _logger.LogDebug("Updated {Count} visible floating windows", windowsData.Count);
        }

        /// <summary>
        /// Найти DocumentDock внутри Layout (может быть вложен в RootDock)
        /// </summary>
        private DocumentDock? FindDocumentDockInLayout(IDock? layout)
        {
            if (layout == null) return null;

            // Если это уже DocumentDock - возвращаем
            if (layout is DocumentDock dd)
                return dd;

            // Если это RootDock - ищем внутри
            if (layout is IRootDock rootDock && rootDock.VisibleDockables != null)
            {
                foreach (var child in rootDock.VisibleDockables)
                {
                    if (child is DocumentDock docDock)
                        return docDock;
                }
            }

            // Если это обычный Dock - ищем рекурсивно
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
        /// Рекурсивно собрать информацию о модулях из Dock структуры
        /// </summary>
        private void CollectModuleInfoRecursive(IDockable dockable, Dictionary<string, (string ContainerId, int TabOrder, bool IsActiveTab, string InstanceId)> moduleInfo)
        {
            if (dockable is DocumentDock docDock && docDock.VisibleDockables != null)
            {
                var containerId = docDock.Id ?? "UNKNOWN";

                for (int i = 0; i < docDock.VisibleDockables.Count; i++)
                {
                    var child = docDock.VisibleDockables[i];

                    if (child is Document document && document.Id != null)
                    {
                        var moduleId = document.Id.Replace("Module_", "");
                        var isActive = docDock.ActiveDockable == document;

                        // Получаем InstanceId из View → ViewModel → Module
                        string? instanceId = null;

                        if (document.Content is Avalonia.Controls.Control control &&
                            control.DataContext is object viewModel)
                        {
                            var tabCollection = App.Services.GetRequiredService<ITabCollection>();
                            if (tabCollection.ActiveTab != null)
                            {
                                var allModules = tabCollection.ActiveTab.ModuleContext.GetAllModules();
                                var module = allModules.FirstOrDefault(m => m.ViewModel == viewModel);
                                instanceId = module?.InstanceId;
                            }
                        }

                        if (instanceId != null)
                        {
                            moduleInfo[moduleId] = (containerId, i, isActive, instanceId);
                            _logger.LogDebug("Found module: {ModuleId} (Instance: {InstanceId}) in {ContainerId}, tab {TabIndex}, active: {IsActive}",
                                moduleId, instanceId, containerId, i, isActive);
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
                    CollectModuleInfoRecursive(child, moduleInfo);
                }
            }
        }
    }
}