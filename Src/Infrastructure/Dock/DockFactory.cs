// Обходные пути под Dock 12.0. Снято: пакеты подняты до 12.1.0.4,
// CacheDocumentTabContent стоит в App.axaml, пересоздание вью после закрытия
// модуля убрано вместе с флагом RecreateViewsAfterClose, ручная уборка пустых
// контейнеров убрана вместе с CleanupEmptyContainers — с 12.1 это делает сам
// Dock через CollapseDock, а наша уборка ещё и падала.
//
// Осталось, и снимать это порознь:
//   1. RecreateDocumentViews() после перетаскивания — вызов в MoveDockable.
//      Держится не багом библиотеки, а нашим OnNeedRerender: он обнуляет и
//      заново присваивает DockLayout, после чего вью висят на презентерах
//      старого дерева и их приходится переприцеплять. Снимать надо с
//      OnNeedRerender, а не с самого RecreateDocumentViews: тот же метод
//      живёт на пути реактивации вкладки (RecreateAllDocumentViews).
//   2. GetOrCreateView() в BaseModule — восстановление DataContext у
//      кэшированной вью. Прежняя формулировка списывала это на Dock, и она
//      неверна: DataContext гасит наш собственный код — CloseDockable,
//      DetachViewsRecursive(clearDataContext: true) и SetContentDeferred.
//      Пока эти строки на месте, восстановление нужно при любой версии Dock.
//   3. RequestProgressiveRefreshAsync() в CharactersModuleView.OnLoaded — тоже
//      не про Dock: он гасит фриз ItemsRepeater при повторном attach. Парный
//      ему PrepareForReattach чистит Folders, так что снимать эти два можно
//      только вместе, иначе список персонажей окажется пустым.
//
// Релизы: https://github.com/wieslawsoltes/Dock/releases

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
using System.Threading.Tasks;
using Writersword.Core.Enums;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Interfaces.Services;
using Writersword.Core.Models.WorkModes;
using Writersword.Core.Services;
using Writersword.Core.Interfaces.WorkFlows;
using Writersword.ViewModels;
using Document = Dock.Model.Avalonia.Controls.Document;

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
        private bool _isRerendering = false;
        private IDockSerializer? _dockSerializer;

        /// <summary>
        /// Типы модулей, которые уже поднимались за эту сессию.
        ///
        /// Нужен для одного вопроса: собирают модуль первый раз или заново. Первое
        /// — обычное открытие, второе — то самое «моргание», когда живой экземпляр
        /// куда-то делся и его строят повторно. По логу эти два случая иначе не
        /// различить, а лечатся они по-разному.
        /// </summary>
        private readonly HashSet<string> _materializedOnce = new(StringComparer.Ordinal);

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
        ///
        /// Параметр объявлен допускающим null вслед за базовым методом Dock.
        /// Разбор по образцу ниже пустое значение отсеивает сам: null не Document,
        /// и вызов уходит в базовую реализацию, которая его и ждёт.
        /// </summary>
        public override void CloseDockable(IDockable? dockable)
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

                // Вью модуля живёт внутри стабильного хоста (ModuleContentHost) —
                // DataContext отвязывается у самой вью, а не только у обёртки.
                if (doc.Content is ModuleContentHost closingHost)
                {
                    if (closingHost.Content is Avalonia.Controls.Control closingInner)
                        closingInner.DataContext = null;
                    closingHost.Content = null;
                }
                else if (doc.Content is Avalonia.Controls.Control closingCtrl)
                {
                    closingCtrl.DataContext = null;
                }
                doc.Content = null;
                base.CloseDockable(dockable);
                OnModuleClosed?.Invoke(moduleType);
            }
            else
            {
                base.CloseDockable(dockable);
            }
        }

        /// <summary>
        /// Схлопнуть опустевший док — но не сейчас, а следующим тактом диспетчера.
        ///
        /// Штатный путь Dock делает удаление и схлопывание одним заходом: убирает
        /// докабл, видит, что док опустел, и тут же удаляет сам док из родителя.
        /// Второе удаление происходит прямо внутри уведомления об изменении
        /// коллекции от первого. Avalonia в этот момент разбирает контейнер,
        /// разбор гасит DataContext, тот протекает вниз по дереву и доходит до
        /// вкладочной полосы дока. Полоса — это SelectingItemsControl: на смену
        /// DataContext она переустанавливает выделение и читает ItemsSourceView по
        /// индексу, а у того счётчик ещё прежний, тогда как список уже опустел.
        /// Индекс за границей, исключение на UI-потоке, приложение падает.
        ///
        /// Схлопывание перехвачено здесь, а не на путях закрытия и перетаскивания
        /// по отдельности, потому что поломка одна на всех: сначала она вылезла на
        /// закрытии вкладки, после починки закрытия — на перетаскивании, и оба
        /// раза стек упирался в CollapseDock внутри RemoveDockable. Место, где
        /// сходятся все пути, ровно одно, и чинить надо его.
        ///
        /// Схлопывание не выбрасывается, а откладывается: пустой док в раскладке
        /// оставлять нельзя, иначе на месте закрытого модуля остаётся пустая
        /// панель. Один такт с пустым доком на экране незаметен.
        ///
        /// Перед схлопыванием состояние проверяется заново: за прошедший такт док
        /// могли и наполнить — при перетаскивании докабл как раз переезжает в
        /// соседний док, и порядок событий тут не гарантирован.
        /// </summary>
        public override void CollapseDock(IDock dock)
        {
            if (dock is null) return;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (dock.VisibleDockables is { Count: 0 })
                        CollapseNow(dock);
                }
                catch (Exception ex)
                {
                    // Схлопывание — уборка раскладки, а не полезное действие само
                    // по себе. Докабл уже удалён или переехал, и падать здесь
                    // означало бы уронить программу на косметике.
                    _logger.LogError(ex, "Deferred collapse failed for dock {DockId}", dock.Id);
                }
            }, Avalonia.Threading.DispatcherPriority.Background);
        }

        /// <summary>
        /// Настоящее схлопывание. Отдельным методом, чтобы вызов базовой
        /// реализации не оказался внутри лямбды.
        /// </summary>
        private void CollapseNow(IDock dock) => base.CollapseDock(dock);

        // =====================================================================
        // РАЗБОР: откуда берётся плавающая подпись модуля
        // =====================================================================
        //
        // На экране остаётся синяя плашка с названием модуля посреди страницы,
        // хотя сам модуль стоит на своём месте. Такую плашку Dock рисует, когда
        // докабл закреплён сбоку (Pin) или вынесен в отдельное окно (Float).
        // Наш код ни того ни другого не запрашивает — значит это делает сам Dock.
        //
        // Подписываемся на коллекции, а не переопределяем методы фабрики:
        // сигнатуры Pin/Float в 12.1 расходятся с интерфейсом по nullable, и
        // переопределение здесь стоило бы отдельной возни с компилятором. А
        // коллекция скажет ровно то же самое: кто и когда там появился.
        //
        // Блок временный: как только виновник назван, он убирается.

        private readonly HashSet<object> _watchedCollections = new();

        private void WatchLayoutForDiagnostics(IRootDock root)
        {
            Watch("закреплено слева", root.LeftPinnedDockables);
            Watch("закреплено справа", root.RightPinnedDockables);
            Watch("закреплено сверху", root.TopPinnedDockables);
            Watch("закреплено снизу", root.BottomPinnedDockables);
            Watch("плавающие окна", root.Windows);

            void Watch(string what, System.Collections.IEnumerable? collection)
            {
                if (collection is not System.Collections.Specialized.INotifyCollectionChanged notify)
                    return;

                // Раскладка пересоздаётся при каждом переключении вкладки, а
                // подписка живёт на объекте коллекции: без этой проверки на одну
                // и ту же коллекцию накопились бы десятки обработчиков.
                if (!_watchedCollections.Add(collection)) return;

                notify.CollectionChanged += (_, args) =>
                {
                    if (args.NewItems is null || args.NewItems.Count == 0) return;

                    foreach (var item in args.NewItems)
                    {
                        string title = (item as IDockable)?.Title ?? item?.GetType().Name ?? "?";
                        string id = (item as IDockable)?.Id ?? string.Empty;

                        _logger.LogWarning("LAYOUT: added {Title} (id={Id}) to {What}, called from {Caller}",
                            what, title, id, ShortStack());
                    }

                    DumpLayout("после изменения «" + what + "»");
                };
            }
        }

        /// <summary>
        /// Кто позвал: только кадры Writersword, чужие рамки не нужны.
        /// Пустой результат означает, что действие пришло изнутри Dock.
        /// </summary>
        private static string ShortStack()
        {
            try
            {
                var frames = new System.Diagnostics.StackTrace(1, false).GetFrames();
                if (frames is null) return "стек недоступен";

                var parts = new List<string>();
                foreach (var frame in frames)
                {
                    var method = frame.GetMethod();
                    var type = method?.DeclaringType?.FullName;
                    if (type is null) continue;

                    if (!type.StartsWith("Writersword", StringComparison.Ordinal))
                        continue;

                    parts.Add(type.Substring(type.LastIndexOf('.') + 1) + "." + method!.Name);
                    if (parts.Count >= 8) break;
                }

                return parts.Count == 0 ? "вне нашего кода — позвал сам Dock" : string.Join(" ← ", parts);
            }
            catch
            {
                return "стек недоступен";
            }
        }

        /// <summary>
        /// Снимок раскладки: что где лежит, что закреплено, что вынесено в окна.
        /// По нему видно, чем именно стал модуль с «отвалившейся» подписью.
        /// </summary>
        public void DumpLayout(string when)
        {
            try
            {
                var root = _currentRootDock;
                if (root is null)
                {
                    _logger.LogWarning("Layout [{When}]: no root", when);
                    return;
                }

                var sb = new System.Text.StringBuilder();
                sb.AppendLine();
                sb.Append("Раскладка [").Append(when).Append(']');

                DumpNode(sb, root, 1);

                Pinned("слева", root.LeftPinnedDockables);
                Pinned("справа", root.RightPinnedDockables);
                Pinned("сверху", root.TopPinnedDockables);
                Pinned("снизу", root.BottomPinnedDockables);

                if (root.Windows is { Count: > 0 })
                {
                    sb.AppendLine().Append("  ОКОН: ").Append(root.Windows.Count);
                    foreach (var window in root.Windows)
                    {
                        sb.AppendLine().Append("    окно id=").Append(window?.Id);
                        if (window?.Layout is { } layout) DumpNode(sb, layout, 3);
                    }
                }

                _logger.LogWarning("{Dump}", sb.ToString());

                void Pinned(string name, IList<IDockable>? list)
                {
                    if (list is null || list.Count == 0) return;

                    sb.AppendLine().Append("  ЗАКРЕПЛЕНО ").Append(name).Append(':');
                    foreach (var item in list)
                        sb.Append(' ').Append(item?.Title).Append(" (id=").Append(item?.Id).Append(')');
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to capture layout [{When}]", when);
            }
        }

        private static void DumpNode(System.Text.StringBuilder sb, IDockable node, int depth)
        {
            sb.AppendLine().Append(new string(' ', depth * 2))
              .Append(node.GetType().Name)
              .Append(" \"").Append(node.Title).Append('"')
              .Append(" id=").Append(node.Id);

            if (node is not IDock dock) return;

            sb.Append(" видимых=").Append(dock.VisibleDockables?.Count ?? 0);

            if (dock.VisibleDockables is null) return;
            foreach (var child in dock.VisibleDockables)
                if (child is not null) DumpNode(sb, child, depth + 1);
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

            if (_currentRootDock != null && !_isRerendering)
            {
                var rootToNormalize = _currentRootDock;
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    () =>
                    {
                        if (_isRerendering) return;
                        NormalizeProportionsRecursive(rootToNormalize);
                        // DockControl в Dock 12 не реагирует на изменение doc.Content напрямую —
                        // нужен null+reassign DockLayout чтобы ContentPresenter-ы пересоздались.
                        // _isRerendering предотвращает рекурсивный вход через внутренний MoveDockable.
                        _isRerendering = true;
                        OnNeedRerender?.Invoke();
                        Avalonia.Threading.Dispatcher.UIThread.Post(
                            () => _isRerendering = false,
                            Avalonia.Threading.DispatcherPriority.Render);

                        // После пересборды дерева переприцепляем кэшированные вьюхи:
                        // они остаются висеть на презентерах СТАРОГО дерева, и новые
                        // ContentPresenter-ы не могут их принять — панель показывается
                        // пустой. RecreateDocumentViews отцепляет вью от мёртвого
                        // родителя (GetOrCreateView) и переустанавливает Content.
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            var tab = App.Services.GetRequiredService<ITabCollection>()
                                .ActiveTab as DocumentTabViewModel;
                            if (tab != null)
                                RecreateDocumentViews(rootToNormalize, tab);
                        }, Avalonia.Threading.DispatcherPriority.Loaded);
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
                    // Переиспользуем кэшированную вью модуля вместо пересоздания,
                    // прикрепление отложенное: плейсхолдер встаёт мгновенно, тяжёлая
                    // вью цепляется следующим проходом диспетчера. GetOrCreateView
                    // отцепляет её от устаревшего ContentPresenter и восстанавливает
                    // DataContext, если тот был обнулён путями закрытия/детача.
                    SetContentDeferred(doc, () =>
                    {
                        var newView = module.GetOrCreateView();
                        _logger.LogDebug("View reattached (deferred) for: {moduleType}", moduleType);
                        return newView;
                    });
                }
                return;
            }

            if (dockable is IDock dock && dock.VisibleDockables != null)
                foreach (var child in dock.VisibleDockables.ToList())
                    RecreateDocumentViews(child, tab);

            // Флоат-окна живут в отдельных корнях (Windows у RootDock) — обходим и их,
            // иначе вынесенная панель после пересборки дерева остаётся пустой.
            if (dockable is IRootDock rootWithWindows && rootWithWindows.Windows != null)
                foreach (var wnd in rootWithWindows.Windows.ToList())
                    if (wnd.Layout != null)
                        RecreateDocumentViews(wnd.Layout, tab);
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
                            SetCurrentRoot(restored, "раскладка восстановлена из файла");
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
            var tab = ownerTab ?? App.Services.GetRequiredService<ITabCollection>().ActiveTab as DocumentTabViewModel;
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

                // Прикрепление отложенное: документ немедленно получает плейсхолдер,
                // а загрузка/переиспользование модуля и прикрепление тяжёлой вьюхи
                // происходят следующим проходом диспетчера (LoadModuleAndGetView).
                document.Context = moduleType;
                document.CanClose = slot.IsCloseable;
                document.CanFloat = slot.IsCloseable;

                SetContentDeferredAsync(document, async () =>
                {
                    var view = await LoadModuleAndGetViewAsync(tab, moduleType);
                    var m = tab.ModuleContext.GetModule(moduleType);
                    if (m != null) document.Title = m.Title;
                    _logger.LogDebug("Module attached (deferred): {moduleType}", moduleType);
                    return view;
                });
                count++;
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
            SetCurrentRoot(rootDock, "раскладка построена заново");
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

            var tab = ownerTab ?? App.Services.GetRequiredService<ITabCollection>().ActiveTab as DocumentTabViewModel;
            if (tab == null)
            {
                _logger.LogError("No tab provided and no active tab");
                return null;
            }

            // Документ создаётся сразу с плейсхолдером — построение layout мгновенно.
            // Загрузка модуля (или переиспользование живого) и прикрепление вьюхи
            // выполняются отложенно, по одному модулю на проход диспетчера.
            var doc = new Document
            {
                Id = $"Module_{slot.ModuleType}",
                Title = slot.ModuleType,
                Context = slot.ModuleType,
                CanClose = slot.IsCloseable,
                CanFloat = slot.IsCloseable,
                Factory = this
            };

            SetContentDeferredAsync(doc, async () =>
            {
                var view = await LoadModuleAndGetViewAsync(tab, slot.ModuleType);
                var m = tab.ModuleContext.GetModule(slot.ModuleType);
                if (m != null) doc.Title = m.Title;
                _logger.LogDebug("Document content attached (deferred): {ModuleType}", slot.ModuleType);
                return view;
            });

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
        /// <para>
        /// clearDataContext управляет обнулением DataContext у вьюх:
        /// true — модули уничтожаются (Deactivate, ReloadFromGlobalConfig): биндинги нужно
        /// разорвать, иначе CollectionChangedEventManager держит сильную ссылку на коллекции
        /// вьюмодели и она не собирается GC.
        /// false — модули остаются живыми (Suspend, SwitchWorkMode): вью переиспользуется
        /// при возврате, а синхронный разрыв и последующее восстановление ВСЕХ биндингов
        /// больших вью (сотни персонажей, тысячи параграфов) занимали секунды UI-потока.
        /// Утечки нет — вьюмодель и её коллекции живы намеренно, пока жив модуль.
        /// </para>
        /// </summary>
        public void DetachViewsFromLayout(IRootDock? oldLayout, bool clearDataContext = true)
        {
            if (oldLayout == null)
                return;

            if (_currentRootDock == oldLayout)
                _currentRootDock = null;

            DetachViewsRecursive(oldLayout, clearDataContext);

            if (oldLayout.Windows != null)
            {
                foreach (var window in oldLayout.Windows)
                {
                    if (window.Layout != null)
                        DetachViewsRecursive(window.Layout, clearDataContext);
                }
            }

            _logger.LogDebug("Views detached from old layout (clearDataContext={Clear})", clearDataContext);
        }

        private void DetachViewsRecursive(IDockable dockable, bool clearDataContext)
        {
            if (dockable is Document document)
            {
                if (document.Content != null)
                {
                    _logger.LogDebug("Detaching view from Document: {Id}", document.Id);
                    // Обнуляем DataContext до очистки Content.
                    // Avalonia.CollectionChangedEventManager держит СИЛЬНУЮ ссылку
                    // на ObservableCollection пока ItemsControl/view жив.
                    // DataContext = null принудительно отвязывает все биндинги —
                    // WeakEventManager.Entry удаляется, коллекция персонажей освобождается.
                    // Выполняется только при уничтожении модулей (см. описание метода).
                    if (clearDataContext)
                    {
                        // Вью модуля живёт внутри стабильного хоста — DataContext
                        // отвязывается у самой вью, а не только у обёртки.
                        if (document.Content is ModuleContentHost hostCtrl)
                        {
                            if (hostCtrl.Content is Avalonia.Controls.Control innerView)
                                innerView.DataContext = null;
                            hostCtrl.Content = null;
                        }
                        else if (document.Content is Avalonia.Controls.Control ctrl)
                        {
                            ctrl.DataContext = null;
                        }
                    }
                    else if (document.Content is ModuleContentHost aliveHost)
                    {
                        // Модули живы: вью освобождается из хоста, чтобы при возврате
                        // GetOrCreateView мог прицепить её к новому хосту без конфликта
                        // визуальных родителей. DataContext вью не трогаем.
                        //
                        // Временная замерялка: снятие вью с визуального дерева
                        // (aliveHost.Content = null) — основной источник провисания
                        // Suspend на UI-потоке. Разбиваем стоимость по фазам и считаем
                        // реализованные элементы, чтобы понять, во что упирается время:
                        // в число карточек/аватаров или в шторм перемеров репитеров.
                        var probeSw = System.Diagnostics.Stopwatch.StartNew();

                        int visuals = 0, images = 0;
                        var repeaters = new System.Collections.Generic.List<
                            Writersword.Modules.Characters.Controls.PerfItemsRepeater>();
                        if (aliveHost.Content is Avalonia.Visual innerVisual)
                        {
                            foreach (var d in Avalonia.VisualTree.VisualExtensions
                                         .GetVisualDescendants(innerVisual))
                            {
                                visuals++;
                                if (d is Writersword.Modules.Characters.Controls.PerfItemsRepeater rep)
                                    repeaters.Add(rep);
                                else if (d is Avalonia.Controls.Image)
                                    images++;
                            }
                        }
                        long enumMs = probeSw.ElapsedMilliseconds;

                        int measuresBefore = 0;
                        foreach (var r in repeaters) measuresBefore += r.MeasureCount;

                        aliveHost.Content = null;
                        long hostNullMs = probeSw.ElapsedMilliseconds - enumMs;

                        document.Content = null;
                        long docNullMs = probeSw.ElapsedMilliseconds - enumMs - hostNullMs;

                        int measuresAfter = 0;
                        foreach (var r in repeaters) measuresAfter += r.MeasureCount;
                        probeSw.Stop();

                        if (probeSw.ElapsedMilliseconds > 30)
                        {
                            _logger.LogWarning(
                                "Detach probe [{Id}]: total={Total}ms " +
                                "(enumTree={Enum}ms, hostContentNull={Host}ms, docContentNull={Doc}ms) | " +
                                "visuals={Visuals}, repeaters={Rep}, images={Img}, " +
                                "repeaterMeasuresDuringDetach={Storm}",
                                document.Id, probeSw.ElapsedMilliseconds, enumMs, hostNullMs, docNullMs,
                                visuals, repeaters.Count, images, measuresAfter - measuresBefore);
                        }

                        return;
                    }
                    document.Content = null;
                }
                return;
            }

            if (dockable is IDock dock && dock.VisibleDockables != null)
            {
                foreach (var child in dock.VisibleDockables)
                    DetachViewsRecursive(child, clearDataContext);
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
        /// Публичная обёртка — восстанавливает View всех модулей после DockLayout
        /// null+set или первичной активации. Чинит VisualParent и показывает loading.
        /// </summary>
        public void RecreateAllDocumentViews(IDockable root, DocumentTabViewModel tab)
            => RecreateDocumentViews(root, tab);

        /// <summary>
        /// Нормализовать пропорции после null+reassign DockLayout.
        /// null+reassign сбрасывает пропорции в NaN, вызывая повторный MoveDockable.
        /// Вызывается из OnNeedRerender до следующего layout pass.
        /// </summary>
        public void NormalizeAfterRerender(IRootDock rootDock)
            => NormalizeProportionsRecursive(rootDock);

        /// <summary>
        /// Помечает существующий layout текущим корнем без пересоздания — мягкая
        /// реактивация вкладки после Suspend. DetachViewsFromLayout обнуляет
        /// _currentRootDock, и без восстановления перестают работать пересборки
        /// после перемещения панелей (MoveDockable/CloseDockable).
        /// </summary>
        public void AttachToLayout(IRootDock rootDock)
            => SetCurrentRoot(rootDock, "раскладка прикреплена заново");

        /// <summary>
        /// Единственное место, где выставляется текущий корень.
        ///
        /// Раньше присвоение стояло в трёх местах порознь — при восстановлении
        /// из файла, при построении заново и при мягкой реактивации, — и
        /// диагностика, повешенная только на последнее, молчала при первых двух:
        /// при холодном открытии проекта AttachToLayout не зовётся вовсе.
        /// </summary>
        private void SetCurrentRoot(IRootDock rootDock, string reason)
        {
            _currentRootDock = rootDock;
            WatchLayoutForDiagnostics(rootDock);
            DumpLayout(reason);
        }

        /// <summary>
        /// Отложенное прикрепление вьюхи модуля: в Content немедленно ставится лёгкий
        /// плейсхолдер (переключение уходит в кадр мгновенно), а тяжёлая загрузка и
        /// прикрепление вьюхи выполняются следующим проходом диспетчера с фоновым
        /// приоритетом — каждый модуль в своём кадре, ввод пользователя не блокируется.
        /// </summary>
        // Актуальный маркер отложенного задания для каждого документа: если для документа
        // запланировано новое прикрепление, устаревшие задания в очереди отменяются —
        // без этого два наслоившихся задания (закрытие модуля + пересборка) дёргали
        // одну вьюху туда-сюда, и второе могло отцепить только что прицепленную.
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Document, object> _deferredTokens = new();

        /// <summary>
        /// Возвращает стабильный хост содержимого документа, создавая его при
        /// необходимости, и содержимое хоста до перепланирования (для отвязки
        /// DataContext у заменённых вью). Презентеры Dock 12 не отслеживают смену
        /// Document.Content, поэтому вся подмена плейсхолдер → вьюха выполняется
        /// внутри хоста — его identity для презентера не меняется.
        /// </summary>
        private static ModuleContentHost GetOrCreateContentHost(Document doc, out Avalonia.Controls.Control? previousContent)
        {
            if (doc.Content is ModuleContentHost existingHost)
            {
                previousContent = existingHost.Content as Avalonia.Controls.Control;
                return existingHost;
            }

            previousContent = doc.Content as Avalonia.Controls.Control;
            return new ModuleContentHost();
        }

        private void SetContentDeferred(Document doc, Func<Avalonia.Controls.Control?> provideView)
        {
            var token = new object();
            _deferredTokens.Remove(doc);
            _deferredTokens.Add(doc, token);

            var host = GetOrCreateContentHost(doc, out var previous);

            // Загрузка по видимости: колбэк выполнится только когда плейсхолдер
            // реально появился на экране. Модули на невидимых вкладках дока не
            // гидрируются вообще — сколько бы модулей ни было в воркмоде, работу
            // получают только видимые панели; остальные — в момент первого показа.
            var placeholder = new ModuleLoadingPlaceholder();
            placeholder.LoadRequested = () =>
            {
                _logger.LogDebug("Deferred attach started (sync): {Id}", doc.Id);

                // Задание устарело: для этого документа запланировано более новое.
                if (!_deferredTokens.TryGetValue(doc, out var current)
                    || !ReferenceEquals(current, token))
                {
                    _logger.LogDebug("Deferred attach superseded (sync): {Id}", doc.Id);
                    return;
                }

                // Документ уже не принадлежит текущему layout (быстрое переключение
                // воркмодов/вкладок заменило дерево) — прикреплять нельзя, иначе
                // GetOrCreateView украдёт вью у живого презентера актуальной панели.
                if (!IsDocumentInCurrentLayout(doc))
                {
                    _logger.LogDebug("Deferred attach skipped, document not in current layout: {Id}", doc.Id);
                    return;
                }

                try
                {
                    var view = provideView();
                    if (view == null)
                    {
                        host.Content = null;
                        return;
                    }

                    // Старую вьюху отвязываем только если это действительно другой контрол:
                    // у переиспользуемой (кэш модуля) DataContext трогать нельзя.
                    if (previous is not null && !ReferenceEquals(previous, view))
                        previous.DataContext = null;

                    PaneAutoHideBehavior.Attach(view);
                    host.Content = null;
                    host.Content = view;
                    _logger.LogDebug("Deferred attach completed (sync): {Id}", doc.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Deferred module view attach failed: {Id}", doc.Id);
                }
            };
            host.Content = placeholder;
            if (!ReferenceEquals(doc.Content, host))
                doc.Content = host;

            StartDeferredAttachWatchdog(doc, token);
        }

        /// <summary>
        /// Проверяет что Document принадлежит текущему корневому layout (включая
        /// флоат-окна). Отложенные прикрепления УСТАРЕВШИХ Document-ов — из layout-ов,
        /// уже заменённых быстрым переключением воркмодов или вкладок — должны
        /// пропускаться: их GetOrCreateView отцепляет кэшированную вью модуля от
        /// живого презентера актуального layout, и панель оставалась пустой
        /// с вечным плейсхолдером.
        /// </summary>
        /// <summary>
        /// Вкладка модуля в текущей раскладке — чтобы модуль мог что-то на ней
        /// показать: значок предупреждения, изменённый заголовок.
        ///
        /// Ищется обходом, без словаря, и это осознанно. Фабрика одна на всё
        /// приложение, а раскладка своя у каждой вкладки-проекта: словарь по
        /// одному moduleType путал бы документы разных проектов. Обход идёт по
        /// текущему корню, то есть по раскладке активной вкладки — ровно того
        /// модуля, который спрашивает.
        ///
        /// Возвращает null, если модуль в раскладке не найден: он мог быть
        /// закрыт или ещё не прикреплён. Это не ошибка, показывать просто негде.
        /// </summary>
        public Document? FindModuleDocument(string moduleType)
        {
            if (string.IsNullOrEmpty(moduleType)) return null;

            var root = _currentRootDock;
            if (root == null) return null;

            var found = FindModuleDocumentRecursive(root, moduleType);
            if (found != null) return found;

            if (root.Windows != null)
            {
                foreach (var wnd in root.Windows)
                {
                    if (wnd.Layout == null) continue;

                    found = FindModuleDocumentRecursive(wnd.Layout, moduleType);
                    if (found != null) return found;
                }
            }

            return null;
        }

        private static Document? FindModuleDocumentRecursive(IDockable current, string moduleType)
        {
            // Опознаётся по Context: там лежит ключ модуля. Id тоже подошёл бы,
            // но он собирается из строки, а Context кладётся напрямую.
            if (current is Document doc
                && doc.Context is string context
                && string.Equals(context, moduleType, StringComparison.Ordinal))
                return doc;

            if (current is IDock dock && dock.VisibleDockables != null)
            {
                foreach (var child in dock.VisibleDockables)
                {
                    var found = FindModuleDocumentRecursive(child, moduleType);
                    if (found != null) return found;
                }
            }

            return null;
        }

        private bool IsDocumentInCurrentLayout(Document doc)
        {
            var root = _currentRootDock;
            if (root == null) return false;

            if (ContainsDockableRecursive(root, doc)) return true;

            if (root.Windows != null)
            {
                foreach (var wnd in root.Windows)
                {
                    if (wnd.Layout != null && ContainsDockableRecursive(wnd.Layout, doc))
                        return true;
                }
            }

            return false;
        }

        private static bool ContainsDockableRecursive(IDockable current, Document target)
        {
            if (ReferenceEquals(current, target)) return true;

            if (current is IDock dock && dock.VisibleDockables != null)
            {
                foreach (var child in dock.VisibleDockables)
                {
                    if (ContainsDockableRecursive(child, target))
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Вотчдог отложенного прикрепления: если через 10 секунд документ всё ещё
        /// показывает плейсхолдер и задание не было заменено более новым — пишет
        /// ошибку в лог и, если документ принадлежит текущему layout, перезапускает
        /// прикрепление. Самовосстановление ситуации "Модуль загружается" навсегда:
        /// каждая попытка логируется, по логу видно на чём именно застряло.
        /// </summary>
        private void StartDeferredAttachWatchdog(Document doc, object token)
        {
            Avalonia.Threading.DispatcherTimer.RunOnce(() =>
            {
                // Содержимое панели живёт внутри стабильного хоста (ModuleContentHost).
                var hostContent = (doc.Content as ModuleContentHost)?.Content;

                // Лог на каждом срабатывании: по нему видно что вотчдог вообще
                // выполнился и в каком состоянии находился документ.
                _logger.LogDebug(
                    "Deferred attach watchdog: {Id}, content={ContentType}, tokenAlive={TokenAlive}",
                    doc.Id,
                    hostContent?.GetType().Name ?? doc.Content?.GetType().Name ?? "null",
                    _deferredTokens.TryGetValue(doc, out var t) && ReferenceEquals(t, token));

                if (!_deferredTokens.TryGetValue(doc, out var current)
                    || !ReferenceEquals(current, token)
                    || hostContent is not ModuleLoadingPlaceholder placeholder)
                    return;

                // Загрузка не стартовала — панель ни разу не показывалась (невидимая
                // вкладка дока). Это намеренная ленивость, а не зависание: загрузка
                // начнётся при первом показе. Перепроверяем позже.
                if (!placeholder.LoadStarted)
                {
                    StartDeferredAttachWatchdog(doc, token);
                    return;
                }

                if (!IsDocumentInCurrentLayout(doc))
                {
                    // Документ устарел (layout сменился) — плейсхолдер невидим, ретрай не нужен.
                    _logger.LogWarning(
                        "Deferred attach stuck on stale document (not in current layout): {Id}", doc.Id);
                    return;
                }

                var moduleType = doc.Id?.Replace("Module_", "");
                var tab = App.Services.GetRequiredService<ITabCollection>().ActiveTab as DocumentTabViewModel;

                if (string.IsNullOrEmpty(moduleType) || tab == null)
                {
                    _logger.LogError(
                        "Deferred attach did not complete within 10s and cannot be retried: {Id}", doc.Id);
                    return;
                }

                _logger.LogError(
                    "Deferred attach did not complete within 10s: {Id} — retrying", doc.Id);

                SetContentDeferredAsync(doc, async () =>
                {
                    var view = await LoadModuleAndGetViewAsync(tab, moduleType);
                    var m = tab.ModuleContext.GetModule(moduleType);
                    if (m != null) doc.Title = m.Title;
                    return view;
                });
            }, TimeSpan.FromSeconds(10));
        }

        /// <summary>
        /// Асинхронный вариант отложенного прикрепления: тяжёлая загрузка данных модуля
        /// (чтение ZIP-кеша, десериализация документа) выполняется на фоновом потоке
        /// внутри provideViewAsync, а UI-поток занят только созданием вьюмоделей и
        /// прикреплением вьюхи. Плейсхолдер ставится немедленно — переключение уходит
        /// в кадр без задержки.
        /// </summary>
        private void SetContentDeferredAsync(Document doc, Func<Task<Avalonia.Controls.Control?>> provideViewAsync)
        {
            var token = new object();
            _deferredTokens.Remove(doc);
            _deferredTokens.Add(doc, token);

            var host = GetOrCreateContentHost(doc, out var previous);

            // Загрузка по видимости: колбэк выполнится только когда плейсхолдер
            // реально появился на экране. Модули на невидимых вкладках дока не
            // гидрируются вообще — сколько бы модулей ни было в воркмоде, работу
            // получают только видимые панели; остальные — в момент первого показа.
            var placeholder = new ModuleLoadingPlaceholder();
            placeholder.LoadRequested = async () =>
            {
                _logger.LogDebug("Deferred attach started (async): {Id}", doc.Id);

                // Задание устарело: для этого документа запланировано более новое.
                if (!_deferredTokens.TryGetValue(doc, out var current)
                    || !ReferenceEquals(current, token))
                {
                    _logger.LogDebug("Deferred attach superseded (async): {Id}", doc.Id);
                    return;
                }

                // Документ уже не принадлежит текущему layout (быстрое переключение
                // воркмодов/вкладок заменило дерево) — прикреплять нельзя, иначе
                // GetOrCreateView украдёт вью у живого презентера актуальной панели.
                if (!IsDocumentInCurrentLayout(doc))
                {
                    _logger.LogDebug("Deferred attach skipped, document not in current layout: {Id}", doc.Id);
                    return;
                }

                try
                {
                    var view = await provideViewAsync();

                    // Пока шла фоновая загрузка, могло появиться более новое задание —
                    // прикреплять устаревшую вьюху нельзя, она отцепит актуальную.
                    if (!_deferredTokens.TryGetValue(doc, out var afterLoad)
                        || !ReferenceEquals(afterLoad, token))
                        return;

                    // Повторная проверка после фоновой загрузки: за это время layout
                    // мог смениться ещё раз.
                    if (!IsDocumentInCurrentLayout(doc))
                    {
                        _logger.LogDebug("Deferred attach skipped after load, document not in current layout: {Id}", doc.Id);
                        return;
                    }

                    if (view == null)
                    {
                        host.Content = null;
                        return;
                    }

                    // Старую вьюху отвязываем только если это действительно другой контрол:
                    // у переиспользуемой (кэш модуля) DataContext трогать нельзя.
                    if (previous is not null && !ReferenceEquals(previous, view))
                        previous.DataContext = null;

                    PaneAutoHideBehavior.Attach(view);
                    host.Content = null;
                    host.Content = view;
                    _logger.LogDebug("Deferred attach completed (async): {Id}", doc.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Deferred module view attach failed: {Id}", doc.Id);
                }
            };
            host.Content = placeholder;
            if (!ReferenceEquals(doc.Content, host))
                doc.Content = host;

            StartDeferredAttachWatchdog(doc, token);
        }

        // Незавершённые загрузки модулей. Доступ только с UI-потока (все отложенные
        // прикрепления идут через диспетчер), поэтому блокировки не нужны.
        // Пока загрузка в await-окне (чтение кеша, десериализация на фоне), второй
        // отложенный вызов для того же модуля должен получить ТУ ЖЕ задачу — без
        // дедупликации два наслоившихся задания (CreateLayout + пересборка вьюх)
        // проходили проверку GetModule до регистрации модуля и создавали его дважды:
        // один экземпляр получал данные, второй (пустой) прикреплялся в UI.
        private readonly Dictionary<(DocumentTabViewModel Tab, string ModuleType), Task<Avalonia.Controls.Control?>> _moduleLoadTasks = new();

        /// <summary>
        /// Загружает модуль (или переиспользует живой) и возвращает его вьюху.
        /// Живой модуль отдаётся без чтения кеша и без SetCustomData — его состояние
        /// свежее любого кеша. Новый модуль создаётся с данными из кеша либо из
        /// project.ModulesData. Вызывается из отложенного прикрепления.
        /// Конкурентные вызовы для одного модуля дедуплицируются: все ожидают одну
        /// и ту же задачу загрузки, модуль создаётся ровно один раз.
        /// </summary>
        private async Task<Avalonia.Controls.Control?> LoadModuleAndGetViewAsync(DocumentTabViewModel tab, string moduleType)
        {
            var key = (tab, moduleType);
            if (_moduleLoadTasks.TryGetValue(key, out var inFlight))
                return await inFlight;

            var loadTask = LoadModuleAndGetViewCoreAsync(tab, moduleType);
            if (loadTask.IsCompleted)
                return await loadTask;

            _moduleLoadTasks[key] = loadTask;
            try
            {
                return await loadTask;
            }
            finally
            {
                if (_moduleLoadTasks.TryGetValue(key, out var current) && ReferenceEquals(current, loadTask))
                    _moduleLoadTasks.Remove(key);
            }
        }

        /// <summary>
        /// Тело загрузки модуля. Чтение ZIP-кеша (дисковая операция) и десериализация
        /// данных модулей с поддержкой IPreparedDataModule выполняются на фоновом
        /// потоке — раньше весь путь (включая десериализацию целого документа) шёл
        /// на UI-потоке и замораживал интерфейс при первом открытии модуля в воркмоде.
        /// </summary>
        private async Task<Avalonia.Controls.Control?> LoadModuleAndGetViewCoreAsync(DocumentTabViewModel tab, string moduleType)
        {
            var existing = tab.ModuleContext.GetModule(moduleType);
            if (existing?.ViewModel != null)
            {
                _logger.LogDebug("Materialize [{ModuleType}]: instance already alive — reusing view", moduleType);
                return existing.GetOrCreateView();
            }

            // Повторная материализация того же модуля за сессию — это и есть
            // «моргание»: экземпляр был, его не стало, и он собирается заново.
            // Первый раз за сессию — норма, второй и далее — повод смотреть, кто
            // его снёс.
            bool firstTime = _materializedOnce.Add(moduleType);
            if (!firstTime)
                _logger.LogWarning(
                    "Materialize [{ModuleType}]: REBUILDING — instance existed earlier this session and is gone",
                    moduleType);

            var project = tab.GetProject();

            object? customDataToRestore = null;
            object? sessionDataToRestore = null;

            // Откуда пришли данные — единственное, что объясняет пустой модуль,
            // поэтому источник и его содержимое пишутся подробно.
            string source = "none";
            string cacheState;

            // У несохранённого проекта нет пути к файлу — кеш не читается,
            // данные берутся из project.ModulesData. Без этой проверки чтение
            // кеша падало в фоновой задаче и модуль оставался с плейсхолдером.
            var filePath = tab.FilePath;
            if (!string.IsNullOrEmpty(filePath))
            {
                var cacheService = App.Services.GetRequiredService<IZipCacheService>();
                var projectId = project.Id;
                var cacheResult = await Task.Run(() => cacheService.LoadCacheWithSession(filePath, projectId));

                if (cacheResult.HasValue)
                {
                    cacheResult.Value.CustomData.TryGetValue(moduleType, out customDataToRestore);
                    cacheResult.Value.SessionData.TryGetValue(moduleType, out sessionDataToRestore);

                    cacheState = $"read, keys=[{string.Join(", ", cacheResult.Value.CustomData.Keys)}]";

                    if (customDataToRestore != null)
                    {
                        source = "cache";
                        _logger.LogDebug("Using cache data for: {ModuleType}", moduleType);
                    }
                }
                else
                {
                    cacheState = "no cache file";
                }
            }
            else
            {
                cacheState = "project not saved yet — cache skipped";
            }

            if (customDataToRestore == null
                && project.ModulesData.TryGetValue(moduleType, out var fileData))
            {
                customDataToRestore = fileData;
                source = "project file";
                _logger.LogDebug("Using project file data for: {ModuleType}", moduleType);
            }

            _logger.LogDebug(
                "Materialize [{ModuleType}]: source={Source}, cache={CacheState}, "
                + "projectModulesData=[{ProjectKeys}], projectId={ProjectId}, file={File}",
                moduleType, source, cacheState,
                string.Join(", ", project.ModulesData.Keys),
                project.Id,
                string.IsNullOrEmpty(filePath) ? "<none>" : System.IO.Path.GetFileName(filePath));

            if (customDataToRestore == null)
                _logger.LogWarning(
                    "No data found for module: {ModuleType} — will load empty "
                    + "(cache: {CacheState}; project file keys: [{ProjectKeys}])",
                    moduleType, cacheState, string.Join(", ", project.ModulesData.Keys));

            var module = tab.ModuleContext.CreateModule(moduleType);
            if (module?.ViewModel == null)
            {
                _logger.LogWarning("Module not created: {ModuleType}", moduleType);
                return null;
            }

            _logger.LogDebug("Module instance ready: {ModuleType}", moduleType);

            module.Context = tab.Context;

            if (customDataToRestore != null)
            {
                if (module is IPreparedDataModule preparedDataModule)
                {
                    // Фаза 1 — парсинг и десериализация на фоновом потоке,
                    // фаза 2 — применение (создание вьюмоделей) на UI-потоке.
                    // Применение выполняется ОТДЕЛЬНЫМ проходом диспетчера: между
                    // созданием модуля и применением данных обрабатывается
                    // накопившийся ввод — наведения и клики не замирают на всё
                    // время загрузки модуля одним куском.
                    var dataForPrepare = customDataToRestore;
                    var prepared = await Task.Run(() => preparedDataModule.PrepareCustomData(dataForPrepare));
                    _logger.LogDebug("Module data prepared (background): {ModuleType}", moduleType);
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                        () => preparedDataModule.ApplyPreparedCustomData(prepared),
                        Avalonia.Threading.DispatcherPriority.Loaded);
                    _logger.LogDebug("Module data applied: {ModuleType}", moduleType);
                }
                else
                {
                    var dataToApply = customDataToRestore;
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                        () => module.SetCustomData(dataToApply),
                        Avalonia.Threading.DispatcherPriority.Loaded);
                    _logger.LogDebug("Module data applied (legacy path): {ModuleType}", moduleType);
                }
            }

            if (sessionDataToRestore != null)
                module.SetSessionData(sessionDataToRestore);

            _logger.LogDebug("Creating module view: {ModuleType}", moduleType);

            // Создание вьюхи — тоже отдельным проходом: инфляция AXAML больших
            // модулей заметно дорогая, ввод между проходами остаётся живым.
            var createdView = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                () => module.GetOrCreateView(),
                Avalonia.Threading.DispatcherPriority.Loaded);

            _logger.LogDebug("Module view ready: {ModuleType}", moduleType);
            return createdView;
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