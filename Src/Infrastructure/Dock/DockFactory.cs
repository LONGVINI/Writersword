using Avalonia;
using Dock.Model.Avalonia;
using Dock.Model.Avalonia.Controls;
using Dock.Model.Avalonia.Core;
using Dock.Model.Controls;
using Dock.Model.Core;
using DynamicData.Binding;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Linq;
using Writersword.Core.Enums;
using Writersword.Core.Models.WorkModes;
using Writersword.Modules.Common;
using Writersword.Src.Core.Interfaces.Services;
using Writersword.ViewModels;
using DynamicData;

namespace Writersword.Src.Infrastructure.Dock
{
    /// <summary>
    /// Фабрика для создания Dock элементов
    /// ОБНОВЛЕНО: Работает с новой структурой ModuleSlot + SplitContainer
    /// </summary>
    public class DockFactory : Factory
    {
        private readonly ModuleRegistry _moduleRegistry;
        private readonly Dictionary<string, bool> _modulesBeingMoved = new();

        /// <summary>Словарь подписок по пути к проекту (для безопасной отписки)</summary>
        private readonly Dictionary<string, List<IDisposable>> _subscriptions = new();

        /// <summary>Сервис автосохранения для уведомления об изменениях</summary>
        private IWorkspaceAutoSaveService? _autoSaveService;

        /// <summary>Путь к текущему проекту (для логирования)</summary>
        private string? _currentProjectPath;


        public DockFactory(ModuleRegistry moduleRegistry)
        {
            _moduleRegistry = moduleRegistry;
        }

        /// <summary>
        /// Инициализация Locators (вызывается ОДИН раз)
        /// </summary>
        public void Initialize()
        {
            // Локатор контекстов (не используется)
            ContextLocator = new Dictionary<string, Func<object?>>
            {
                ["Root"] = () => null
            };

            // Локатор окон
            HostWindowLocator = new Dictionary<string, Func<IHostWindow?>>
            {
                [nameof(IDockWindow)] = () =>
                {
                    Console.WriteLine("[DockFactory] HostWindowLocator called - creating HostWindow");
                    return new HostWindow();
                }
            };

            // Локатор dockable элементов (для динамического создания)
            DockableLocator = new Dictionary<string, Func<IDockable?>>();

            Console.WriteLine("[DockFactory] Initialized with custom HostWindow");

            // ДИАГНОСТИКА
            DockDiagnostics.InspectFactoryMethods();
        }


        /// <summary>Создать layout из WorkMode</summary>
        public IRootDock CreateLayout(WorkMode workMode)
        {
            Console.WriteLine($"[DockFactory] Creating layout for: {workMode.Title}");

            // Создаём структуру из новых данных (Containers + ModuleSlots)
            var mainDock = CreateDockFromNewStructure(workMode);

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

            InitLayout(rootDock);

            // Создаём флоат окна ПОСЛЕ инициализации layout
            CreateFloatingWindows(rootDock, workMode);

            Console.WriteLine($"[DockFactory] Layout created from new structure");

            return rootDock;
        }

        /// <summary>
        /// Создать Dock из новой структуры (Containers + ModuleSlots)
        /// </summary>
        private IDock CreateDockFromNewStructure(WorkMode workMode)
        {
            Console.WriteLine($"[DockFactory] Creating layout from Containers + ModuleSlots");

            // Если нет контейнеров - создаём простой DocumentDock
            if (workMode.Containers == null || workMode.Containers.Count == 0)
            {
                Console.WriteLine("[DockFactory] No containers, creating simple DocumentDock");
                return CreateSimpleDocumentDockFromSlots(workMode);
            }

            // Ищем корневой контейнер
            var rootContainer = workMode.Containers.FirstOrDefault(c => c.Id == "Root");
            if (rootContainer == null)
            {
                Console.WriteLine("[DockFactory] No Root container found, using first");
                rootContainer = workMode.Containers[0];
            }

            // Рекурсивно создаём структуру из контейнеров
            var dock = CreateDockFromContainer(rootContainer, workMode);

            Console.WriteLine($"[DockFactory] Layout created from {workMode.Containers.Count} containers");

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
                Console.WriteLine("[DockFactory] No floating modules");
                return;
            }

            Console.WriteLine($"[DockFactory] Creating {floatingModules.Count} floating windows");

            foreach (var floatSlot in floatingModules)
            {
                var document = CreateModuleDocument(floatSlot);
                if (document == null)
                {
                    Console.WriteLine($"[DockFactory] Failed to create document for floating module: {floatSlot.ModuleId}");
                    continue;
                }

                // Создаём HostWindow для флоат окна
                var hostWindow = new HostWindow();

                // КРИТИЧНО: Устанавливаем фабрику для document
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

                // Устанавливаем title через интерфейс
                hostWindow.SetTitle(document.Title);

                // Устанавливаем размер и позицию через интерфейс
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

                Console.WriteLine($"[DockFactory] Created floating window: {floatSlot.ModuleId} at ({floatSlot.FloatX}, {floatSlot.FloatY})");
            }
        }

        /// <summary>
        /// Добавить флоат окно в RootDock
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

            Console.WriteLine($"[DockFactory] Float window added to RootDock: {floatDock.Id}");
        }

        /// <summary>
        /// Рекурсивно создать Dock из SplitContainer
        /// </summary>
        private IDock CreateDockFromContainer(SplitContainer container, WorkMode workMode)
        {
            Console.WriteLine($"[DockFactory] Processing container: {container.Id}, Orientation: {container.Orientation}");

            // Если контейнер НЕ имеет детей - это конечная панель с модулями
            if (container.Children == null || container.Children.Count == 0)
            {
                Console.WriteLine($"[DockFactory] Container {container.Id} is leaf - creating DocumentDock");
                return CreateDocumentDockForContainer(container, workMode);
            }

            // Если есть дети - создаём ProportionalDock со split
            Console.WriteLine($"[DockFactory] Container {container.Id} has {container.Children.Count} children");

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
                var childDock = CreateDockFromContainer(container.Children[i], workMode);
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

            Console.WriteLine($"[DockFactory] Created ProportionalDock: {container.Id}, children: {container.Children.Count}");

            return proportionalDock;
        }

        /// <summary>
        /// Создать DocumentDock для конечного контейнера
        /// Заполняет модулями из ModuleSlots где ContainerId совпадает
        /// </summary>
        private DocumentDock CreateDocumentDockForContainer(SplitContainer container, WorkMode workMode)
        {
            var documents = new List<IDockable>();

            // Находим модули для этого контейнера
            var modulesInContainer = workMode.ModuleSlots
                .Where(slot => slot.ContainerId == container.Id && !slot.IsFloating)
                .OrderBy(slot => slot.TabOrder)
                .ToList();

            Console.WriteLine($"[DockFactory] Container {container.Id} has {modulesInContainer.Count} modules");

            foreach (var slot in modulesInContainer)
            {
                var doc = CreateModuleDocument(slot);
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

            Console.WriteLine($"[DockFactory] Created DocumentDock: {container.Id}, modules: {documents.Count}, active: {activeDoc?.Id}");

            return documentDock;
        }

        /// <summary>
        /// Создать простой DocumentDock если нет структуры контейнеров
        /// Используется как fallback
        /// </summary>
        private DocumentDock CreateSimpleDocumentDockFromSlots(WorkMode workMode)
        {
            var documents = new List<IDockable>();

            // Берём только не-floating модули
            foreach (var slot in workMode.ModuleSlots.Where(s => !s.IsFloating))
            {
                var doc = CreateModuleDocument(slot);
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

            Console.WriteLine($"[DockFactory] Created simple DocumentDock with {documents.Count} modules");

            return documentDock;
        }

        /// <summary>
        /// Создать Document для модуля с подпиской на закрытие
        /// </summary>
        public IDockable? CreateModuleDocument(ModuleSlot slot)
        {
            Console.WriteLine($"[DockFactory] Creating document for: {slot.ModuleId}");

            var module = _moduleRegistry.CreateModule(slot.ModuleId);
            if (module?.ViewModel == null)
            {
                Console.WriteLine($"[DockFactory] Module not created: {slot.ModuleId}");
                return null;
            }

            var tabCollection = App.Services.GetRequiredService<Writersword.Src.Core.Interfaces.WorkFlows.ITabCollection>();
            if (tabCollection.ActiveTab != null)
            {
                module.Context = tabCollection.ActiveTab.Context;
                Console.WriteLine($"[DockFactory] Context assigned to module: {slot.ModuleId}");

                var project = tabCollection.ActiveTab.GetProject();
                if (project.ModulesData.TryGetValue(slot.ModuleId.ToString(), out var data))
                {
                    var state = new Writersword.Core.Models.Modules.ModuleState
                    {
                        CustomData = data
                    };
                    module.RestoreState(state);
                    Console.WriteLine($"[DockFactory] Restored state for: {slot.ModuleId}");
                }
            }

            var view = module.CreateView();
            if (view == null)
            {
                Console.WriteLine($"[DockFactory] No View: {slot.ModuleId}");
                return null;
            }

            string stableId = $"Module_{slot.ModuleId}";

            var document = new Document
            {
                Id = stableId,
                Title = module.Title,
                Content = view,
                CanClose = slot.IsCloseable,
                CanFloat = true
            };

            Console.WriteLine($"[DockFactory] Document created: {slot.ModuleId}, CanClose={document.CanClose}");

            bool wasAddedToDock = false;
            bool hasSubscribedToCollection = false;
            IDisposable? subscription = null;

            subscription = document.WhenAnyValue(x => x.Owner)
                .Subscribe(owner =>
                {
                    Console.WriteLine($"[DockFactory] Owner changed for {slot.ModuleId}: owner={(owner != null ? "NOT NULL" : "NULL")}, wasAdded={wasAddedToDock}");

                    if (owner != null && !wasAddedToDock)
                    {
                        wasAddedToDock = true;
                        Console.WriteLine($"[DockFactory] Document added: {slot.ModuleId}");

                        if (owner is IDock dock && !hasSubscribedToCollection)
                        {
                            hasSubscribedToCollection = true;

                            if (dock.VisibleDockables is System.Collections.Specialized.INotifyCollectionChanged observable)
                            {
                                Console.WriteLine($"[DockFactory] Subscribing to CollectionChanged for: {slot.ModuleId}");

                                observable.CollectionChanged += (s, e) =>
                                {
                                    if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove &&
                                        e.OldItems?.Contains(document) == true)
                                    {
                                        if (_modulesBeingMoved.TryGetValue(slot.ModuleId, out var movingFlag) && movingFlag)
                                        {
                                            Console.WriteLine($"[DockFactory] Ignoring Remove - internal move in progress");
                                            return;
                                        }

                                        Console.WriteLine($"[DockFactory] Document REMOVED from VisibleDockables: {slot.ModuleId}");

                                        var mainVM = App.Services.GetRequiredService<MainWindowViewModel>();
                                        mainVM.HandleModuleClosedInDock(slot.ModuleId);

                                        subscription?.Dispose();
                                    }
                                };
                            }
                        }
                    }
                });

            return document;
        }

        /// <summary>
        /// Вставить новый модуль в существующий layout по PreferredPosition
        /// </summary>
        public void InsertModuleByPreference(IRootDock rootDock, ModuleSlot slot)
        {
            Console.WriteLine($"[DockFactory] Inserting module {slot.ModuleId} with position {slot.PreferredPosition}");

            var document = CreateModuleDocument(slot);
            if (document == null)
            {
                Console.WriteLine($"[DockFactory] Failed to create document for {slot.ModuleId}");
                return;
            }

            var basePosition = GetBasePosition(slot.PreferredPosition);
            var isTab = slot.PreferredPosition.ToString().EndsWith("AsTab");

            Console.WriteLine($"[DockFactory] Base position: {basePosition}, IsTab: {isTab}");

            var targetDock = FindOrCreateDockForPosition(rootDock, basePosition, isTab);

            if (targetDock != null)
            {
                if (targetDock.VisibleDockables == null)
                    targetDock.VisibleDockables = new List<IDockable>();

                targetDock.VisibleDockables.Add(document);
                targetDock.ActiveDockable = document;

                SetOwnerAndRegisterForFloat(document, targetDock);

                Console.WriteLine($"[DockFactory] Module {slot.ModuleId} inserted successfully");
            }
            else
            {
                Console.WriteLine($"[DockFactory] ERROR: Could not find or create dock for position {basePosition}");
            }
        }

        /// <summary>
        /// Эмулирует drag&drop для регистрации документа в Float системе
        /// </summary>
        private void SetOwnerAndRegisterForFloat(IDockable document, IDock owner)
        {
            Console.WriteLine($"[DockFactory] Emulating drag&drop: {document.Id}");

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

                var originalIndex = sourceDock.VisibleDockables.IndexOf(document);
                sourceDock.VisibleDockables.Remove(document);

                if (targetDock.VisibleDockables == null)
                    targetDock.VisibleDockables = new List<IDockable>();

                targetDock.VisibleDockables.Add(document);
                targetDock.ActiveDockable = document;

                targetDock.VisibleDockables.Remove(document);
                sourceDock.VisibleDockables.Insert(originalIndex, document);
                sourceDock.ActiveDockable = document;

                _modulesBeingMoved[moduleId] = false;

                Console.WriteLine($"[DockFactory] Move complete");
            }
            catch (Exception ex)
            {
                _modulesBeingMoved[moduleId] = false;
                Console.WriteLine($"[DockFactory] Move failed: {ex.Message}");
            }
        }

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

        /// <summary>Получить базовую позицию без AsTab</summary>
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

        /// <summary>Найти или создать Dock для позиции</summary>
        private IDock? FindOrCreateDockForPosition(IRootDock rootDock, PreferredDockPosition position, bool asTab)
        {
            var mainDock = rootDock.VisibleDockables?.FirstOrDefault() as ProportionalDock;
            if (mainDock == null)
            {
                Console.WriteLine("[DockFactory] No main ProportionalDock found");
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

        private IDock? FindOrCreateRightDock(ProportionalDock mainDock, PreferredDockPosition position, bool asTab)
        {
            Console.WriteLine($"[FindOrCreateRightDock] {position}, asTab={asTab}");

            ProportionalDock searchDock = mainDock;

            if (mainDock.Orientation == Orientation.Horizontal)
            {
                var rightElement = mainDock.VisibleDockables?.LastOrDefault(d => d is not ProportionalDockSplitter);

                if (rightElement is ProportionalDock nestedLayout && nestedLayout.Orientation == Orientation.Vertical)
                {
                    Console.WriteLine($"[FindOrCreateRightDock] Found nested vertical layout: {nestedLayout.Id}");
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

                Console.WriteLine($"[FindOrCreateRightDock] Using existing panel: {targetPanel.Id}");
                return targetPanel;
            }

            Console.WriteLine($"[FindOrCreateRightDock] Creating new right panel");
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

        private IDock? FindOrCreateLeftDock(ProportionalDock mainDock, PreferredDockPosition position, bool asTab)
        {
            Console.WriteLine($"[DockFactory] FindOrCreateLeftDock: {position}, asTab={asTab}");

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

        private IDock? FindOrCreateBottomDock(ProportionalDock mainDock, bool asTab)
        {
            Console.WriteLine($"[DockFactory] FindOrCreateBottomDock: asTab={asTab}");

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

        private IDock? FindOrCreateTopDock(ProportionalDock mainDock, bool asTab)
        {
            Console.WriteLine($"[DockFactory] FindOrCreateTopDock: asTab={asTab}");

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

        private void InsertPanelInDirection(ProportionalDock mainDock, IDock newPanel, string direction, PreferredDockPosition position)
        {
            Console.WriteLine($"[DockFactory] InsertPanelInDirection: {direction}, position={position}");

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

        private static void InsertRightPanel(ProportionalDock mainDock, IDock newPanel, PreferredDockPosition position)
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

                    Console.WriteLine($"[DockFactory] Added to existing right vertical split");
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

                Console.WriteLine($"[DockFactory] Created new right vertical split");
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

                Console.WriteLine($"[InsertRightPanel] Added first right panel with splitter");
            }
        }

        private static void InsertLeftPanel(ProportionalDock mainDock, IDock newPanel, PreferredDockPosition position)
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

                    Console.WriteLine($"[DockFactory] Added to existing left vertical split");
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

                Console.WriteLine($"[DockFactory] Created new left vertical split");
            }
            else
            {
                if (mainDock.Orientation != Orientation.Horizontal)
                {
                    mainDock.Orientation = Orientation.Horizontal;
                }

                newPanel.Proportion = 0.3;
                mainDock.VisibleDockables!.Insert(0, newPanel);

                Console.WriteLine($"[DockFactory] Added first left panel");
            }
        }

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

            Console.WriteLine($"[DockFactory] Added bottom panel with vertical split");
        }

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

            Console.WriteLine($"[DockFactory] Added top panel with vertical split");
        }

        /// <summary>
        /// Сериализовать текущий layout в новую структуру (Containers + ModuleSlots с обновлёнными данными)
        /// </summary>
        public (List<SplitContainer> Containers, List<ModuleSlot> UpdatedSlots) SerializeCurrentLayout(IRootDock rootDock, WorkMode workMode)
        {
            try
            {
                Console.WriteLine("[DockFactory] Serializing current layout to new structure");

                var mainDock = rootDock.VisibleDockables?.FirstOrDefault() as ProportionalDock;
                if (mainDock == null)
                {
                    Console.WriteLine("[DockFactory] No main dock to serialize");
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

                Console.WriteLine($"[DockFactory] Serialized: {containers.Count} containers, {updatedSlots.Count} slots updated");
                return (containers, updatedSlots);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DockFactory] Error serializing layout: {ex.Message}");
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

            // Если это ProportionalDock со split - рекурсивно обрабатываем детей
            if (dock is ProportionalDock propDock && propDock.VisibleDockables != null && propDock.VisibleDockables.Count > 0)
            {
                container.Orientation = propDock.Orientation == Orientation.Horizontal ? "Horizontal" : "Vertical";
                container.Children = new List<SplitContainer>();

                foreach (var child in propDock.VisibleDockables)
                {
                    // Пропускаем сплиттеры
                    if (child is ProportionalDockSplitter)
                        continue;

                    if (child is IDock childDock)
                    {
                        var childContainer = SerializeContainerRecursive(childDock, childDock.Id ?? Guid.NewGuid().ToString());
                        container.Children.Add(childContainer);
                    }
                }

                Console.WriteLine($"[DockFactory] Serialized container: {container.Id}, Orientation: {container.Orientation}, Children: {container.Children.Count}");
            }
            else
            {
                // Конечный узел (DocumentDock с модулями)
                container.Orientation = null;
                container.Children = null;
                Console.WriteLine($"[DockFactory] Serialized leaf container: {container.Id}");
            }

            return container;
        }

        /// <summary>
        /// Обновить ModuleSlots с актуальными данными из UI
        /// Устанавливает ContainerId, TabOrder, IsActiveTab для каждого модуля
        /// </summary>
        private void UpdateModuleSlotsFromDock(IRootDock rootDock, List<ModuleSlot> slots)
        {
            // Сначала собираем информацию о всех модулях в UI
            var moduleInfo = new Dictionary<string, (string ContainerId, int TabOrder, bool IsActiveTab)>();

            CollectModuleInfoRecursive(rootDock, moduleInfo);

            Console.WriteLine($"[DockFactory] Collected info for {moduleInfo.Count} modules from UI");

            // Обновляем слоты
            foreach (var slot in slots)
            {
                if (moduleInfo.TryGetValue(slot.ModuleId, out var info))
                {
                    slot.ContainerId = info.ContainerId;
                    slot.TabOrder = info.TabOrder;
                    slot.IsActiveTab = info.IsActiveTab;

                    Console.WriteLine($"[DockFactory] Updated slot: {slot.ModuleId}, Container: {info.ContainerId}, Tab: {info.TabOrder}, Active: {info.IsActiveTab}");
                }
            }
        }

        /// <summary>
        /// Обновить информацию о флоат окнах в ModuleSlots
        /// </summary>
        private void UpdateFloatingModules(IRootDock rootDock, List<ModuleSlot> slots)
        {
            Console.WriteLine($"[DockFactory] === UpdateFloatingModules DIAGNOSTIC ===");
            Console.WriteLine($"[DockFactory] rootDock.Windows count: {rootDock.Windows?.Count ?? 0}");

            if (rootDock.Windows == null || rootDock.Windows.Count == 0)
            {
                Console.WriteLine($"[DockFactory] === END DIAGNOSTIC ===");
                Console.WriteLine($"[DockFactory] No floating windows");

                // ВАЖНО: Сбрасываем IsFloating для модулей, которые больше не флоат
                foreach (var slot in slots.Where(s => s.IsFloating))
                {
                    // Если модуль был флоат, но окон нет - значит он закрыт
                    // Проверяем, есть ли он в основном dock
                    bool foundInDock = false;

                    if (rootDock.VisibleDockables != null)
                    {
                        foundInDock = FindModuleInDock(rootDock, slot.ModuleId);
                    }

                    if (!foundInDock)
                    {
                        // Модуль не найден нигде - он закрыт, убираем флоат
                        slot.IsFloating = false;
                        slot.ContainerId = null;
                        Console.WriteLine($"[DockFactory] Reset floating flag for closed module: {slot.ModuleId}");
                    }
                }

                return;
            }

            foreach (var window in rootDock.Windows)
            {
                Console.WriteLine($"[DockFactory]   Window ID: {window.Id}");
                Console.WriteLine($"[DockFactory]   Window Title: {(window as IDockable)?.Title ?? "N/A"}");
                Console.WriteLine($"[DockFactory]   Host type: {window.Host?.GetType().Name ?? "null"}");
                Console.WriteLine($"[DockFactory]   Layout type: {window.Layout?.GetType().Name ?? "null"}");

                var floatDock = FindDocumentDockInLayout(window.Layout);

                if (floatDock != null && floatDock.VisibleDockables != null)
                {
                    Console.WriteLine($"[DockFactory]   Found DocumentDock inside, VisibleDockables: {floatDock.VisibleDockables.Count}");

                    foreach (var dockable in floatDock.VisibleDockables)
                    {
                        Console.WriteLine($"[DockFactory]     - Dockable: {dockable.Id}, Type: {dockable.GetType().Name}");

                        if (dockable is Document document)
                        {
                            string moduleId = document.Id.Replace("Module_", "");

                            var slot = slots.FirstOrDefault(s => s.ModuleId == moduleId);
                            if (slot != null)
                            {
                                slot.IsFloating = true;
                                slot.ContainerId = null;

                                double x = 0, y = 0, width = 800, height = 600;
                                window.Host?.GetPosition(out x, out y);
                                window.Host?.GetSize(out width, out height);

                                slot.FloatX = (int)x;
                                slot.FloatY = (int)y;
                                slot.FloatWidth = (int)width;
                                slot.FloatHeight = (int)height;

                                Console.WriteLine($"[DockFactory] Updated floating module: {moduleId} at ({x}, {y})");
                            }
                        }
                    }
                }
            }

            Console.WriteLine($"[DockFactory] === END DIAGNOSTIC ===");
            Console.WriteLine($"[DockFactory] Updating {rootDock.Windows.Count} floating windows");
        }

        private bool FindModuleInDock(IDock? dock, string moduleId)
        {
            if (dock == null) return false;

            if (dock.VisibleDockables != null)
            {
                foreach (var dockable in dock.VisibleDockables)
                {
                    if (dockable is Document doc && doc.Id == "Module_" + moduleId)
                    {
                        return true;
                    }

                    if (dockable is IDock childDock)
                    {
                        if (FindModuleInDock(childDock, moduleId))
                            return true;
                    }
                }
            }

            return false;
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
        private void CollectModuleInfoRecursive(IDockable dockable, Dictionary<string, (string ContainerId, int TabOrder, bool IsActiveTab)> moduleInfo)
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

                        moduleInfo[moduleId] = (containerId, i, isActive);

                        Console.WriteLine($"[DockFactory] Found module: {moduleId} in {containerId}, tab {i}, active: {isActive}");
                    }
                }
            }

            // Рекурсивно обходим дочерние элементы
            if (dockable is IDock dock && dock.VisibleDockables != null)
            {
                foreach (var child in dock.VisibleDockables)
                {
                    CollectModuleInfoRecursive(child, moduleInfo);
                }
            }
        }

        /// <summary>
        /// Установить сервис автосохранения для проекта
        /// </summary>
        public void SetAutoSaveService(IWorkspaceAutoSaveService autoSaveService, string projectPath)
        {
            _autoSaveService = autoSaveService;
            _currentProjectPath = projectPath;

            if (!_subscriptions.ContainsKey(projectPath))
            {
                _subscriptions[projectPath] = new List<IDisposable>();
            }

            Console.WriteLine($"[DockFactory] AutoSaveService set for: {projectPath}");
        }

        /// <summary>
        /// Подписаться на события изменения Dock структуры
        /// </summary>
        public void SubscribeToDockEvents(IDockable dockable, string projectPath)
        {
            if (!_subscriptions.ContainsKey(projectPath))
            {
                _subscriptions[projectPath] = new List<IDisposable>();
            }

            var subscriptions = _subscriptions[projectPath];

            Console.WriteLine($"[DockFactory] SUBSCRIBE for: {dockable.Id}");

            // Подписка на создание флоат окон
            if (dockable is IRootDock rootDock && rootDock.Windows is System.Collections.Specialized.INotifyCollectionChanged windowsObservable)
            {
                Console.WriteLine($"[DockFactory] Subscribing to Windows.CollectionChanged");

                System.Collections.Specialized.NotifyCollectionChangedEventHandler windowsHandler = (s, e) =>
                {
                    if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add && e.NewItems != null)
                    {
                        foreach (var item in e.NewItems)
                        {
                            if (item is IDockWindow dockWindow)
                            {
                                Console.WriteLine($"[DockFactory] NEW FLOAT WINDOW CREATED: {dockWindow.Id}");

                                // Уведомляем автосохранение
                                if (_autoSaveService != null && _currentProjectPath == projectPath)
                                {
                                    _autoSaveService.NotifyChange();
                                }
                            }
                        }
                    }
                };

                windowsObservable.CollectionChanged += windowsHandler;

                var windowsSubscription = System.Reactive.Disposables.Disposable.Create(() =>
                {
                    windowsObservable.CollectionChanged -= windowsHandler;
                });

                subscriptions.Add(windowsSubscription);
            }

            if (dockable is System.ComponentModel.INotifyPropertyChanged notifyProperty)
            {
                System.ComponentModel.PropertyChangedEventHandler handler = (sender, e) =>
                {
                    if (e.PropertyName == nameof(IDock.Proportion) ||
                        e.PropertyName == nameof(IDock.ActiveDockable))
                    {
                        OnDockPropertyChanged(projectPath, dockable.Id);
                    }
                };

                notifyProperty.PropertyChanged += handler;

                var subscription = System.Reactive.Disposables.Disposable.Create(() =>
                {
                    notifyProperty.PropertyChanged -= handler;
                });

                subscriptions.Add(subscription);
            }

            if (dockable is IDock dock && dock.VisibleDockables is System.Collections.Specialized.INotifyCollectionChanged observable)
            {
                System.Collections.Specialized.NotifyCollectionChangedEventHandler handler = (s, e) =>
                    OnDockCollectionChanged(projectPath, dockable.Id, e);

                observable.CollectionChanged += handler;

                var subscription = System.Reactive.Disposables.Disposable.Create(() =>
                {
                    observable.CollectionChanged -= handler;
                });

                subscriptions.Add(subscription);
            }

            if (dockable is Document document)
            {
                System.ComponentModel.PropertyChangedEventHandler handler = (sender, e) =>
                {
                    if (e.PropertyName == nameof(Document.Owner))
                    {
                        OnDocumentOwnerChanged(projectPath, document.Id);
                    }
                };

                if (document is System.ComponentModel.INotifyPropertyChanged docNotify)
                {
                    docNotify.PropertyChanged += handler;

                    var subscription = System.Reactive.Disposables.Disposable.Create(() =>
                    {
                        docNotify.PropertyChanged -= handler;
                    });

                    subscriptions.Add(subscription);
                }
            }

            if (dockable is IDock dockWithChildren && dockWithChildren.VisibleDockables != null)
            {
                foreach (var child in dockWithChildren.VisibleDockables)
                {
                    SubscribeToDockEvents(child, projectPath);
                }
            }
        }
      
        private void OnDockPropertyChanged(string projectPath, string? dockId)
        {
            if (_autoSaveService == null || _currentProjectPath != projectPath)
            {
                return;
            }

            Console.WriteLine($"[DockFactory] Property changed: {dockId}");
            _autoSaveService.NotifyChange();
        }

        private void OnDockCollectionChanged(string projectPath, string? dockId, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (_autoSaveService == null || _currentProjectPath != projectPath)
            {
                return;
            }

            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove && e.OldItems != null)
            {
                foreach (var item in e.OldItems)
                {
                    if (item is Document doc)
                    {
                        var moduleId = doc.Id?.Replace("Module_", "") ?? "";
                        if (_modulesBeingMoved.TryGetValue(moduleId, out var isMoving) && isMoving)
                        {
                            return;
                        }
                    }
                }
            }

            Console.WriteLine($"[DockFactory] Collection changed in {dockId}: {e.Action}");
            _autoSaveService.NotifyChange();
        }

        private void OnDocumentOwnerChanged(string projectPath, string? documentId)
        {
            if (_autoSaveService == null || _currentProjectPath != projectPath)
            {
                return;
            }

            Console.WriteLine($"[DockFactory] Owner changed: {documentId}");
            _autoSaveService.NotifyChange();
        }

        /// <summary>
        /// Отписаться от всех событий для проекта
        /// </summary>
        public void UnsubscribeFromDockEvents(string projectPath)
        {
            if (!_subscriptions.ContainsKey(projectPath))
            {
                return;
            }

            var subscriptions = _subscriptions[projectPath];

            Console.WriteLine($"[DockFactory] Unsubscribing from {subscriptions.Count} events for: {projectPath}");

            foreach (var subscription in subscriptions)
            {
                subscription.Dispose();
            }

            _subscriptions.Remove(projectPath);

            if (_currentProjectPath == projectPath)
            {
                _autoSaveService = null;
                _currentProjectPath = null;
            }

            Console.WriteLine($"[DockFactory] Unsubscribed successfully");
        }
    }
}