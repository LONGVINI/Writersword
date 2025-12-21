using Dock.Model.Avalonia;
using Dock.Model.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using ReactiveUI;
using System;
using System.Collections.Generic;
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

            Console.WriteLine($"[DockFactory] Created {documents.Count} documents");

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

            var proportionalDock = new ProportionalDock
            {
                Id = "MainLayout",
                Title = "MainLayout",
                Proportion = double.NaN,
                Orientation = Orientation.Horizontal,
                ActiveDockable = documentDock
            };

            if (proportionalDock.VisibleDockables == null)
                proportionalDock.VisibleDockables = new List<IDockable>();

            proportionalDock.VisibleDockables.Add(documentDock);

            var rootDock = new RootDock
            {
                Id = "Root",
                Title = "Root",
                ActiveDockable = proportionalDock,
                DefaultDockable = proportionalDock,
                Factory = this
            };

            if (rootDock.VisibleDockables == null)
                rootDock.VisibleDockables = new List<IDockable>();

            rootDock.VisibleDockables.Add(proportionalDock);

            InitLayout(rootDock);

            Console.WriteLine($"[DockFactory] Layout created and initialized with Factory reference");

            DockDiagnostics.InspectRootDock(rootDock);
            if (documents.Count > 0)
            {
                DockDiagnostics.InspectDocument(documents[0]);
            }

            return rootDock;
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
    }
}