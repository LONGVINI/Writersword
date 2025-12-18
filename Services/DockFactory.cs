using Avalonia.Controls;
using Dock.Model.Avalonia;
using Dock.Model.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using System;
using System.Collections.Generic;
using Writersword.Core.Enums;
using Writersword.Core.Models.WorkModes;
using Writersword.Modules.Common;

namespace Writersword.Services
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

        /// <summary>
        /// Создать layout из WorkMode
        /// </summary>
        public IRootDock CreateLayout(WorkMode workMode)
        {
            Console.WriteLine($"[DockFactory] Creating layout for: {workMode.Title}");

            // Создаём документы
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

            // DocumentDock
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

            // ProportionalDock
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

            // RootDock
            var rootDock = new RootDock
            {
                Id = "Root",
                Title = "Root",
                ActiveDockable = proportionalDock,
                DefaultDockable = proportionalDock,

                // КРИТИЧНО: Привязываем Factory к RootDock
                Factory = this
            };

            if (rootDock.VisibleDockables == null)
                rootDock.VisibleDockables = new List<IDockable>();

            rootDock.VisibleDockables.Add(proportionalDock);

            // КРИТИЧНО: Инициализируем layout через фабрику
            InitLayout(rootDock);

            Console.WriteLine($"[DockFactory] Layout created and initialized with Factory reference");

            // ДИАГНОСТИКА
            DockDiagnostics.InspectRootDock(rootDock);
            if (documents.Count > 0)
            {
                DockDiagnostics.InspectDocument(documents[0]);
            }

            return rootDock;
        }

        private IDockable? CreateModuleDocument(ModuleSlot slot)
        {
            Console.WriteLine($"[DockFactory] Creating document for: {slot.ModuleType}");

            var module = _moduleRegistry.CreateModule(slot.ModuleType);
            if (module?.ViewModel == null)
            {
                Console.WriteLine($"[DockFactory] Module not created: {slot.ModuleType}");
                return null;
            }

            var view = CreateModuleView(slot.ModuleType, module.ViewModel);
            if (view == null)
            {
                Console.WriteLine($"[DockFactory] No View: {slot.ModuleType}");
                return null;
            }

            // ИЗМЕНЕНИЕ: Используем стабильный ID на основе типа модуля
            // Вместо нового Guid каждый раз
            string stableId = $"Module_{slot.ModuleType}";

            var document = new Document
            {
                Id = stableId,  // <-- Стабильный ID!
                Title = module.Title,
                Content = view,
                CanClose = slot.IsCloseable,
                CanFloat = true
            };

            Console.WriteLine($"[DockFactory] Created document: {document.Title} (ID: {document.Id})");

            return document;
        }

        private Control? CreateModuleView(ModuleType moduleType, object viewModel)
        {
            return moduleType switch
            {
                ModuleType.TextEditor => new Modules.TextEditor.Views.TextEditorView { DataContext = viewModel },
                ModuleType.Synonyms => new Modules.Synonyms.Views.SynonymsView { DataContext = viewModel },
                ModuleType.Notes => new Modules.Notes.Views.NotesView { DataContext = viewModel },
                ModuleType.Timer => new Modules.Timer.Views.TimerView { DataContext = viewModel },
                _ => null
            };
        }
    }
}