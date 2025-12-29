using Dock.Model.Avalonia;
using Dock.Model.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Linq;
using Writersword.Core.Enums;
using Writersword.Core.Models.WorkModes;
using Writersword.Modules.Common;

namespace Writersword.Src.Infrastructure.Dock
{
    /// <summary>
    /// Фабрика для создания Dock элементов
    /// ИСПРАВЛЕНО: CanFloat=true, rootDock.Factory установлена
    /// </summary>
    public class DockFactory : Factory
    {
        private readonly ModuleRegistry _moduleRegistry;

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

            // ИСПРАВЛЕНО: Локатор окон возвращает словарь как и было
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

            // Получаем конфигурацию Dock из WorkMode
            workMode.Settings.CustomSettings.TryGetValue("DockLayout", out var value);
            var dockConfig = value as DockLayoutConfig;

            // Если нет сохранённой конфигурации - нужно получить DEFAULT
            // Пока создаём простую структуру
            var mainDock = CreateDockFromConfig(workMode, dockConfig);

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

            Console.WriteLine($"[DockFactory] Layout created with custom configuration");

            return rootDock;
        }

        private IDock CreateDockFromConfig(WorkMode workMode, DockLayoutConfig? config)
        {
            // Если нет конфига - создаём все модули в одном DocumentDock (как раньше)
            if (config == null || config.Panels.Count == 0)
            {
                Console.WriteLine("[DockFactory] No config, creating simple DocumentDock");
                return CreateSimpleDocumentDock(workMode);
            }

            // Создаём ProportionalDock с панелями по конфигу
            Console.WriteLine($"[DockFactory] Creating layout with {config.Panels.Count} panels");

            var proportionalDock = new ProportionalDock
            {
                Id = "MainLayout",
                Title = "MainLayout",
                Proportion = double.NaN,
                Orientation = config.MainOrientation == DockOrientation.Horizontal
                    ? Orientation.Horizontal
                    : Orientation.Vertical
            };

            if (proportionalDock.VisibleDockables == null)
                proportionalDock.VisibleDockables = new List<IDockable>();

            foreach (var panelConfig in config.Panels)
            {
                var panel = CreatePanelFromConfig(workMode, panelConfig);
                if (panel != null)
                {
                    proportionalDock.VisibleDockables.Add(panel);
                }
            }

            if (proportionalDock.VisibleDockables.Count > 0)
            {
                proportionalDock.ActiveDockable = proportionalDock.VisibleDockables[0];
            }

            return proportionalDock;
        }

        private IDock? CreatePanelFromConfig(WorkMode workMode, DockPanelConfig panelConfig)
        {
            // Если есть вложенный layout - создаём рекурсивно
            if (panelConfig.NestedLayout != null)
            {
                Console.WriteLine($"[DockFactory] Creating nested layout for panel: {panelConfig.Id}");
                return CreateDockFromConfig(workMode, panelConfig.NestedLayout);
            }

            // Иначе создаём DocumentDock с модулями из списка
            var documents = new List<IDockable>();

            foreach (var moduleType in panelConfig.Modules)
            {
                var slot = workMode.ModuleSlots.FirstOrDefault(s => s.ModuleType == moduleType);
                if (slot != null && slot.IsVisible)
                {
                    var doc = CreateModuleDocument(slot);
                    if (doc != null)
                    {
                        documents.Add(doc);
                    }
                }
            }

            if (documents.Count == 0)
            {
                Console.WriteLine($"[DockFactory] Panel {panelConfig.Id} has no modules, skipping");
                return null;
            }

            var documentDock = new DocumentDock
            {
                Id = panelConfig.Id,
                Title = panelConfig.Id,
                Proportion = panelConfig.Proportion,
                ActiveDockable = documents[0],
                CanCreateDocument = false
            };

            if (documentDock.VisibleDockables == null)
                documentDock.VisibleDockables = new List<IDockable>();

            foreach (var doc in documents)
            {
                documentDock.VisibleDockables.Add(doc);
            }

            Console.WriteLine($"[DockFactory] Created panel {panelConfig.Id} with {documents.Count} documents");

            return documentDock;
        }

        private DocumentDock CreateSimpleDocumentDock(WorkMode workMode)
        {
            var documents = new List<IDockable>();

            foreach (var slot in workMode.ModuleSlots)
            {
                if (!slot.IsVisible) continue;

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

            return documentDock;
        }

        public IDockable? CreateModuleDocument(ModuleSlot slot)
        {
            Console.WriteLine($"[DockFactory] Creating document for: {slot.ModuleType}");

            var module = _moduleRegistry.CreateModule(slot.ModuleType);
            if (module?.ViewModel == null)
            {
                Console.WriteLine($"[DockFactory] Module not created: {slot.ModuleType}");
                return null;
            }

            var view = module.CreateView();
            if (view == null)
            {
                Console.WriteLine($"[DockFactory] No View: {slot.ModuleType}");
                return null;
            }

            string stableId = $"Module_{slot.ModuleType}";

            var document = new Document
            {
                Id = stableId,
                Title = module.Title,
                Content = view,
                CanClose = slot.IsCloseable,
                CanFloat = true
            };

            bool wasAddedToDock = false;
            IDisposable? subscription = null;

            subscription = document.WhenAnyValue(x => x.Owner)
                .Subscribe(owner =>
                {
                    if (owner != null && !wasAddedToDock)
                    {
                        wasAddedToDock = true;
                        Console.WriteLine($"[DockFactory] Document added: {slot.ModuleType}");
                    }
                    else if (owner == null && wasAddedToDock && slot.IsCloseable)
                    {
                        Console.WriteLine($"[DockFactory] Document closed: {slot.ModuleType}");
                        slot.IsVisible = false;
                        subscription?.Dispose();
                    }
                });

            Console.WriteLine($"[DockFactory] Created document: {document.Title} (ID: {document.Id}, CanClose={document.CanClose})");

            return document;
        }

        /// <summary>
        /// Вставить новый модуль в существующий layout по PreferredPosition
        /// Используется когда пользователь добавляет модуль динамически
        /// </summary>
        public void InsertModuleByPreference(IRootDock rootDock, ModuleSlot slot)
        {
            Console.WriteLine($"[DockFactory] Inserting module {slot.ModuleType} with position {slot.PreferredPosition}");

            var document = CreateModuleDocument(slot);
            if (document == null)
            {
                Console.WriteLine($"[DockFactory] Failed to create document for {slot.ModuleType}");
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

                Console.WriteLine($"[DockFactory] Module {slot.ModuleType} inserted successfully");
            }
            else
            {
                Console.WriteLine($"[DockFactory] ERROR: Could not find or create dock for position {basePosition}");
            }
        }

        /// <summary>Получить базовую позицию без AsTab</summary>
        static private PreferredDockPosition GetBasePosition(PreferredDockPosition position)
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

        /// <summary>Найти или создать правую панель</summary>
        private IDock? FindOrCreateRightDock(ProportionalDock mainDock, PreferredDockPosition position, bool asTab)
        {
            Console.WriteLine($"[DockFactory] FindOrCreateRightDock: {position}, asTab={asTab}");

            var rightPanels = FindPanelsInDirection(mainDock, "Right");

            // AsTab - добавляем в существующую панель
            if (asTab && rightPanels.Count > 0)
            {
                return position switch
                {
                    PreferredDockPosition.BottomRight => rightPanels.Last(),
                    PreferredDockPosition.TopRight => rightPanels.First(),
                    _ => rightPanels.First()
                };
            }

            // Отдельная панель - создаём новую
            var newPanel = new DocumentDock
            {
                Id = $"Right_{Guid.NewGuid()}",
                Title = "Right",
                Proportion = double.NaN,
                CanCreateDocument = false
            };

            InsertPanelInDirection(mainDock, newPanel, "Right", position);
            return newPanel;
        }

        /// <summary>Найти или создать левую панель</summary>
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

        /// <summary>Найти или создать нижнюю панель</summary>
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

        /// <summary>Найти или создать верхнюю панель</summary>
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

        /// <summary>Найти все панели в направлении</summary>
        static private List<IDock> FindPanelsInDirection(ProportionalDock mainDock, string direction)
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

        /// <summary>Рекурсивно собрать все Dock из структуры</summary>
        static private void CollectDocksRecursive(IDockable element, List<IDock> result)
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

        /// <summary>Вставить панель в направлении</summary>
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

        /// <summary>Вставить панель справа</summary>
        static private void InsertRightPanel(ProportionalDock mainDock, IDock newPanel, PreferredDockPosition position)
        {
            // Если mainDock уже Horizontal и есть правая часть
            if (mainDock.Orientation == Orientation.Horizontal && mainDock.VisibleDockables!.Count > 1)
            {
                var rightElement = mainDock.VisibleDockables.Last();

                // Если правая часть это вертикальный split - добавляем туда
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

                // Иначе создаём вертикальный split справа
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
                // Нет правой части - создаём horizontal split
                if (mainDock.Orientation != Orientation.Horizontal)
                {
                    Console.WriteLine($"[DockFactory] Converting mainDock to Horizontal");
                    mainDock.Orientation = Orientation.Horizontal;
                }

                newPanel.Proportion = 0.3;
                mainDock.VisibleDockables!.Add(newPanel);

                Console.WriteLine($"[DockFactory] Added first right panel");
            }
        }

        /// <summary>Вставить панель слева</summary>
        static private void InsertLeftPanel(ProportionalDock mainDock, IDock newPanel, PreferredDockPosition position)
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

        /// <summary>Вставить панель снизу</summary>
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

        /// <summary>Вставить панель сверху</summary>
        static private void InsertTopPanel(ProportionalDock mainDock, IDock newPanel)
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
    }
}