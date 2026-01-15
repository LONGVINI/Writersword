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
using Writersword.ViewModels;

namespace Writersword.Src.Infrastructure.Dock
{
    /// <summary>
    /// Фабрика для создания Dock элементов
    /// ИСПРАВЛЕНО: CanFloat=true, rootDock.Factory установлена
    /// </summary>
    public class DockFactory : Factory
    {
        private readonly ModuleRegistry _moduleRegistry;
        private readonly Dictionary<string, bool> _modulesBeingMoved = new();
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

            // КРИТИЧНО: Добавляем панели И сплиттеры между ними!
            for (int i = 0; i < config.Panels.Count; i++)
            {
                var panel = CreatePanelFromConfig(workMode, config.Panels[i]);
                if (panel != null)
                {
                    proportionalDock.VisibleDockables.Add(panel);

                    // Если это НЕ последняя панель - добавляем сплиттер ПОСЛЕ неё
                    if (i < config.Panels.Count - 1)
                    {
                        var splitter = new ProportionalDockSplitter
                        {
                            Id = $"Splitter_{i}",
                            Title = $"Splitter_{i}"
                        };
                        proportionalDock.VisibleDockables.Add(splitter);
                        Console.WriteLine($"[DockFactory] Added splitter between panels {i} and {i + 1}");
                    }
                }
            }

            if (proportionalDock.VisibleDockables.Count > 0)
            {
                // Активируем первую ПАНЕЛЬ (не сплиттер!)
                proportionalDock.ActiveDockable = proportionalDock.VisibleDockables
                    .FirstOrDefault(d => d is not ProportionalDockSplitter);
            }

            // === ДИАГНОСТИКА: ЧТО МЫ СОЗДАЛИ ===
            Console.WriteLine($"[DockFactory] === DIAGNOSTIC for {proportionalDock.Id} ===");
            Console.WriteLine($"  Orientation: {proportionalDock.Orientation}");
            Console.WriteLine($"  Proportion: {proportionalDock.Proportion}");
            Console.WriteLine($"  Children count: {proportionalDock.VisibleDockables?.Count ?? 0}");

            if (proportionalDock.VisibleDockables != null)
            {
                for (int i = 0; i < proportionalDock.VisibleDockables.Count; i++)
                {
                    var child = proportionalDock.VisibleDockables[i];
                    Console.WriteLine($"  Child[{i}]: Type={child.GetType().Name}, Id={child.Id}");

                    if (child is IDock dock)
                    {
                        Console.WriteLine($"    Proportion: {dock.Proportion}");

                        if (child is ProportionalDock propDock)
                        {
                            Console.WriteLine($"    Orientation: {propDock.Orientation}");
                            Console.WriteLine($"    Nested children: {propDock.VisibleDockables?.Count ?? 0}");

                            // Диагностика вложенных детей
                            if (propDock.VisibleDockables != null)
                            {
                                for (int j = 0; j < propDock.VisibleDockables.Count; j++)
                                {
                                    var nestedChild = propDock.VisibleDockables[j];
                                    Console.WriteLine($"      Nested[{j}]: Type={nestedChild.GetType().Name}, Id={nestedChild.Id}");
                                    if (nestedChild is IDock nestedDock)
                                    {
                                        Console.WriteLine($"        Proportion: {nestedDock.Proportion}");
                                    }
                                }
                            }
                        }
                    }
                }
            }
            Console.WriteLine($"[DockFactory] === END DIAGNOSTIC ===");

            return proportionalDock;
        }

        private IDock? CreatePanelFromConfig(WorkMode workMode, DockPanelConfig panelConfig)
        {
            // Если есть вложенный layout - создаём рекурсивно
            if (panelConfig.NestedLayout != null)
            {
                Console.WriteLine($"[DockFactory] Creating nested layout for panel: {panelConfig.Id}");
                var nestedDock = CreateDockFromConfig(workMode, panelConfig.NestedLayout);

                // КРИТИЧНО: Устанавливаем пропорцию из конфига!
                if (nestedDock is IDock dock)
                {
                    dock.Proportion = panelConfig.Proportion > 0 ? panelConfig.Proportion : 0.3;
                    Console.WriteLine($"[DockFactory] Set proportion {dock.Proportion} for nested layout {panelConfig.Id}");
                }

                // КРИТИЧНО: Устанавливаем ID и Title!
                if (nestedDock is ProportionalDock pd)
                {
                    pd.Id = panelConfig.Id;
                    pd.Title = panelConfig.Id;
                    Console.WriteLine($"[DockFactory] Set ID for nested layout: {pd.Id}");
                }

                return nestedDock;
            }

            // Иначе создаём DocumentDock с модулями из списка
            var documents = new List<IDockable>();

            foreach (var moduleType in panelConfig.Modules)
            {
                var slot = workMode.ModuleSlots.FirstOrDefault(s => s.ModuleId == moduleType);
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
                Proportion = panelConfig.Proportion > 0 ? panelConfig.Proportion : 0.5,
                ActiveDockable = documents[0],
                CanCreateDocument = false
            };

            if (documentDock.VisibleDockables == null)
                documentDock.VisibleDockables = new List<IDockable>();

            foreach (var doc in documents)
            {
                documentDock.VisibleDockables.Add(doc);
            }

            Console.WriteLine($"[DockFactory] Created panel {panelConfig.Id} with {documents.Count} documents, Proportion={documentDock.Proportion}");

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

        /// <summary>
        /// Создать Document для модуля с подпиской на закрытие
        /// ИСПРАВЛЕНО: Игнорирует Remove события во время drag&drop эмуляции
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

            Console.WriteLine($"[DockFactory] Document created: {slot.ModuleId}, CanClose={document.CanClose}, slot.IsCloseable={slot.IsCloseable}");
            bool wasAddedToDock = false;
            bool hasSubscribedToCollection = false;  // НОВЫЙ ФЛАГ!
            IDisposable? subscription = null;

            subscription = document.WhenAnyValue(x => x.Owner)
                .Subscribe(owner =>
                {
                    Console.WriteLine($"[DockFactory] Owner changed for {slot.ModuleId}: owner={(owner != null ? "NOT NULL" : "NULL")}, wasAdded={wasAddedToDock}");

                    if (owner != null && !wasAddedToDock)
                    {
                        wasAddedToDock = true;
                        Console.WriteLine($"[DockFactory] Document added: {slot.ModuleId}");

                        if (owner is IDock dock && !hasSubscribedToCollection)  // ПРОВЕРЯЕМ ФЛАГ!
                        {
                            hasSubscribedToCollection = true;  // УСТАНАВЛИВАЕМ РАЗ И НАВСЕГДА!

                            var visibleType = dock.VisibleDockables?.GetType().Name ?? "NULL";
                            Console.WriteLine($"[DockFactory] VisibleDockables type: {visibleType}");
                            Console.WriteLine($"[DockFactory] Is INotifyCollectionChanged: {dock.VisibleDockables is System.Collections.Specialized.INotifyCollectionChanged}");

                            if (dock.VisibleDockables is System.Collections.Specialized.INotifyCollectionChanged observable)
                            {
                                Console.WriteLine($"[DockFactory] Subscribing to CollectionChanged for: {slot.ModuleId}");

                                observable.CollectionChanged += (s, e) =>
                                {
                                    Console.WriteLine($"[DockFactory] CollectionChanged event! Action: {e.Action}");

                                    if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove &&
                                        e.OldItems?.Contains(document) == true)
                                    {
                                        if (_modulesBeingMoved.TryGetValue(slot.ModuleId, out var movingFlag) && movingFlag)
                                        {
                                            Console.WriteLine($"[DockFactory] Ignoring Remove - internal move in progress");
                                            return;
                                        }

                                        Console.WriteLine($"[DockFactory] Document REMOVED from VisibleDockables: {slot.ModuleId}");

                                        slot.IsVisible = false;

                                        var mainVM = App.Services.GetRequiredService<MainWindowViewModel>();
                                        mainVM.HandleModuleClosedInDock(slot.ModuleId);

                                        subscription?.Dispose();
                                    }
                                };
                            }
                            else
                            {
                                Console.WriteLine($"[DockFactory] VisibleDockables is NOT INotifyCollectionChanged!");
                            }
                        }
                    }
                });

            Console.WriteLine($"[DockFactory] Created document: {document.Title} (ID: {document.Id}, CanClose={document.CanClose})");

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

                // КРИТИЧНО: СНАЧАЛА добавляем в VisibleDockables!
                // Это важно для срабатывания подписки WhenAnyValue(Owner)
                targetDock.VisibleDockables.Add(document);
                targetDock.ActiveDockable = document;

                // ПОТОМ регистрируем для Float (Owner уже будет установлен подпиской!)
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
        /// Перемещает документ между Dock'ами и обратно
        /// ИСПРАВЛЕНО: Устанавливает флаг чтобы игнорировать Remove события
        /// </summary>
        private void SetOwnerAndRegisterForFloat(IDockable document, IDock owner)
        {
            Console.WriteLine($"[DockFactory] Emulating drag&drop: {document.Id}");

            if (document is Document diagnosticDoc)
            {
                Console.WriteLine($"[BEFORE] CanFloat={diagnosticDoc.CanFloat}, Owner={diagnosticDoc.Owner?.Id ?? "NULL"}");
            }

            if (owner.Factory == null)
            {
                owner.Factory = this;
            }

            InitDockable(document, owner);

            if (document is Document doc)
            {
                doc.CanFloat = true;
                Console.WriteLine($"[SetOwnerAndRegisterForFloat] Set CanFloat=true");
            }

            string moduleId = document.Id?.Replace("Module_", "") ?? "";

            try
            {
                Console.WriteLine($"[DockFactory] Moving document to trigger registration...");

                var sourceDock = document.Owner as IDock;
                if (sourceDock == null || sourceDock.VisibleDockables == null)
                {
                    Console.WriteLine($"[DockFactory] No source dock, skipping move");
                    _modulesBeingMoved[moduleId] = false;

                    if (document is Document d1)
                    {
                        Console.WriteLine($"[AFTER - NO MOVE] CanFloat={d1.CanFloat}, Owner={d1.Owner?.Id ?? "NULL"}");
                    }
                    return;
                }

                var targetDock = FindAnotherDock(sourceDock);
                if (targetDock == null)
                {
                    Console.WriteLine($"[DockFactory] No target dock found, skipping move");
                    _modulesBeingMoved[moduleId] = false;

                    if (document is Document d2)
                    {
                        Console.WriteLine($"[AFTER - NO TARGET] CanFloat={d2.CanFloat}, Owner={d2.Owner?.Id ?? "NULL"}");
                    }
                    return;
                }

                Console.WriteLine($"[DockFactory] Moving from {sourceDock.Id} to {targetDock.Id}");

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

                Console.WriteLine($"[DockFactory] Move complete - document should be registered!");
            }
            catch (Exception ex)
            {
                _modulesBeingMoved[moduleId] = false;
                Console.WriteLine($"[DockFactory] Move failed: {ex.Message}");
            }

            if (document is Document finalDoc)
            {
                Console.WriteLine($"[AFTER - FINAL] CanFloat={finalDoc.CanFloat}, Owner={finalDoc.Owner?.Id ?? "NULL"}");
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
        private IDock? FindOrCreateRightDock(ProportionalDock mainDock, PreferredDockPosition position, bool asTab)
        {
            Console.WriteLine($"[FindOrCreateRightDock] {position}, asTab={asTab}");
            Console.WriteLine($"[FindOrCreateRightDock] mainDock.Id={mainDock.Id}, Orientation={mainDock.Orientation}");

            ProportionalDock searchDock = mainDock;

            // Если это horizontal root dock, ищем вложенный vertical layout справа
            if (mainDock.Orientation == Orientation.Horizontal)
            {
                Console.WriteLine($"[FindOrCreateRightDock] mainDock is Horizontal, checking children...");
                Console.WriteLine($"[FindOrCreateRightDock] Children count: {mainDock.VisibleDockables?.Count ?? 0}");

                var rightElement = mainDock.VisibleDockables?.LastOrDefault(d => d is not ProportionalDockSplitter);

                Console.WriteLine($"[FindOrCreateRightDock] rightElement type: {rightElement?.GetType().Name ?? "NULL"}");
                Console.WriteLine($"[FindOrCreateRightDock] rightElement id: {rightElement?.Id ?? "NULL"}");

                if (rightElement is ProportionalDock nestedLayout)
                {
                    Console.WriteLine($"[FindOrCreateRightDock] Found ProportionalDock, orientation: {nestedLayout.Orientation}");

                    if (nestedLayout.Orientation == Orientation.Vertical)
                    {
                        Console.WriteLine($"[FindOrCreateRightDock] Found nested vertical layout: {nestedLayout.Id}");
                        searchDock = nestedLayout;
                    }
                }
            }

            // КРИТИЧНО: Если searchDock VERTICAL - ищем Top/Bottom панели, НЕ Right!
            List<IDock> panels;
            if (searchDock.Orientation == Orientation.Vertical)
            {
                Console.WriteLine($"[FindOrCreateRightDock] searchDock is VERTICAL, looking for Top/Bottom panels");
                panels = CollectAllDocumentDocks(searchDock);  // ← Собираем ВСЕ DocumentDock'и
            }
            else
            {
                panels = FindPanelsInDirection(searchDock, "Right");
            }

            Console.WriteLine($"[FindOrCreateRightDock] Found {panels.Count} panels in {searchDock.Id}");

            foreach (var panel in panels)
            {
                Console.WriteLine($"  - Panel: {panel.Id}");
            }

            // AsTab - добавляем в существующую панель
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

            // Отдельная панель - создаём новую
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

        /// <summary>
        /// Собрать ВСЕ DocumentDock'и из структуры (рекурсивно)
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
                    Console.WriteLine($"[InsertRightPanel] Converting mainDock to Horizontal");
                    mainDock.Orientation = Orientation.Horizontal;
                }

                // КРИТИЧНО: Добавляем СПЛИТТЕР перед новой панелью!
                var splitter = new ProportionalDockSplitter
                {
                    Id = $"Splitter_{mainDock.VisibleDockables!.Count}",
                    Title = $"Splitter_{mainDock.VisibleDockables.Count}"
                };

                mainDock.VisibleDockables.Add(splitter);
                Console.WriteLine($"[InsertRightPanel] Added splitter");

                newPanel.Proportion = 0.3;
                mainDock.VisibleDockables.Add(newPanel);

                Console.WriteLine($"[InsertRightPanel] Added first right panel with splitter");
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