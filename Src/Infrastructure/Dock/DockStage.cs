using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Writersword.Infrastructure.Dock
{
    /// <summary>
    /// Сцена док-области: держит по одному DockControl на каждую живую раскладку
    /// (раскладка = вкладка проекта + её воркмод) и переключает между ними
    /// видимостью, как браузер переключает вкладки.
    /// <para>
    /// Раньше был один DockControl, и каждое переключение вкладки или воркмода
    /// шло через DockLayout = null → новая раскладка: Dock разбирал всё дерево
    /// панелей, а вью модулей снимались с визуального дерева и вешались обратно.
    /// Повторное присоединение большой вью к дереву — это полный перестиль,
    /// пересоздание шаблонов и перемер всего содержимого на UI-потоке: секунды
    /// заморозки на TextEditor и Characters, хотя сами вью уже были в памяти.
    /// </para>
    /// <para>
    /// Здесь вью не покидают дерево вовсе. Скрытый DockControl (IsVisible=false)
    /// не участвует ни в раскладке, ни в отрисовке, ни в хит-тесте, а стили,
    /// шаблоны и биндинги его содержимого остаются применёнными. Показ обратно —
    /// один проход раскладки по уже готовому дереву.
    /// </para>
    /// <para>
    /// Память почти не растёт: модули и их вью и раньше жили до закрытия или
    /// выгрузки вкладки. Добавляется только лёгкая обвязка Dock на каждую
    /// раскладку. DockControl мёртвых раскладок (закрытая или выгруженная
    /// вкладка, пересозданная раскладка) удаляются по списку AliveLayouts.
    /// </para>
    /// <para>
    /// Совместимость с прежней семантикой DockLayout: присвоение null и затем
    /// той же самой раскладки, что показана сейчас, — это запрос на полную
    /// пересборку дерева (OnNeedRerender после перемещения панели,
    /// принудительное обновление после закрытия модуля). Такой запрос
    /// выполняется на том же DockControl ровно как раньше: Layout = null → Layout.
    /// Null без последующего присвоения (закрыты все вкладки) скрывает сцену.
    /// </para>
    /// </summary>
    public sealed class DockStage : Panel
    {
        public static readonly StyledProperty<IRootDock?> LayoutProperty =
            AvaloniaProperty.Register<DockStage, IRootDock?>(nameof(Layout));

        public static readonly StyledProperty<IReadOnlyCollection<IRootDock>?> AliveLayoutsProperty =
            AvaloniaProperty.Register<DockStage, IReadOnlyCollection<IRootDock>?>(nameof(AliveLayouts));

        private readonly Dictionary<IRootDock, DockControl> _controls =
            new(ReferenceEqualityComparer.Instance);

        private readonly ILogger<DockStage>? _logger;

        private DockControl? _shownControl;
        private IRootDock? _shownLayout;

        // Было присвоение null и ещё не пришла следующая раскладка.
        private bool _resetPending;

        /// <summary>Раскладка, которая должна быть на экране.</summary>
        public IRootDock? Layout
        {
            get => GetValue(LayoutProperty);
            set => SetValue(LayoutProperty, value);
        }

        /// <summary>
        /// Все раскладки, которые ещё могут быть показаны (текущие и
        /// припаркованные раскладки всех открытых вкладок). DockControl
        /// раскладок вне списка удаляются.
        /// </summary>
        public IReadOnlyCollection<IRootDock>? AliveLayouts
        {
            get => GetValue(AliveLayoutsProperty);
            set => SetValue(AliveLayoutsProperty, value);
        }

        /// <summary>Количество DockControl на сцене (для диагностики).</summary>
        public int CachedCount => _controls.Count;

        public DockStage()
        {
            _logger = App.Services?.GetService<ILogger<DockStage>>();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == LayoutProperty)
                OnLayoutChanged(change.GetNewValue<IRootDock?>());
            else if (change.Property == AliveLayoutsProperty)
                Prune(change.GetNewValue<IReadOnlyCollection<IRootDock>?>());
        }

        private void OnLayoutChanged(IRootDock? layout)
        {
            if (layout == null)
            {
                // Ничего не прячем сразу: почти всегда следом придёт новая раскладка,
                // и промежуточный пустой кадр был бы лишним миганием. Если раскладка
                // так и не пришла (закрыты все вкладки) — сцена скрывается.
                _resetPending = true;
                Dispatcher.UIThread.Post(() =>
                {
                    if (Layout == null && _resetPending)
                    {
                        _resetPending = false;
                        HideAll();
                    }
                }, DispatcherPriority.Background);
                return;
            }

            bool rebuildRequested = _resetPending && ReferenceEquals(layout, _shownLayout);
            _resetPending = false;

            if (rebuildRequested && _controls.TryGetValue(layout, out var existing))
            {
                // Прежняя семантика null → та же раскладка: полная пересборка дерева
                // Dock на том же контроле.
                existing.Layout = null;
                existing.Layout = layout;
                _logger?.LogDebug("DockStage: rebuild requested for shown layout");
                return;
            }

            Show(layout);
        }

        private void Show(IRootDock layout)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            bool created = false;
            if (!_controls.TryGetValue(layout, out var target))
            {
                // Фон DockControl задаёт стиль dock|DockControl в DockStyles.axaml.
                target = new DockControl { Layout = layout };
                _controls[layout] = target;
                Children.Add(target);
                created = true;
            }

            foreach (var control in _controls.Values)
            {
                bool visible = ReferenceEquals(control, target);

                if (!visible && control.IsVisible)
                    MoveFocusOutOf(control);

                control.IsVisible = visible;
                control.IsHitTestVisible = visible;
                SetRegisteredInFactory(control, visible);
            }

            _shownControl = target;
            _shownLayout = layout;

            stopwatch.Stop();
            Writersword.Infrastructure.Diagnostics.SwitchProfiler.Mark(
                created ? "DockStage: создан новый DockControl" : "DockStage: показан готовый DockControl");
            _logger?.LogDebug(
                "DockStage: layout shown ({Mode}) in {ElapsedMs}ms, cached controls={Count}",
                created ? "created" : "reused", stopwatch.ElapsedMilliseconds, _controls.Count);
        }

        private void HideAll()
        {
            foreach (var control in _controls.Values)
            {
                if (control.IsVisible)
                    MoveFocusOutOf(control);

                control.IsVisible = false;
                control.IsHitTestVisible = false;
                SetRegisteredInFactory(control, false);
            }

            _shownControl = null;
            _shownLayout = null;
            _logger?.LogDebug("DockStage: all layouts hidden");
        }

        private void Prune(IReadOnlyCollection<IRootDock>? alive)
        {
            var keep = new HashSet<IRootDock>(
                alive ?? (IReadOnlyCollection<IRootDock>)Array.Empty<IRootDock>(),
                ReferenceEqualityComparer.Instance);

            if (Layout != null)
                keep.Add(Layout);

            int removed = 0;
            foreach (var layout in _controls.Keys.ToList())
            {
                if (keep.Contains(layout))
                    continue;

                var control = _controls[layout];
                _controls.Remove(layout);

                if (ReferenceEquals(control, _shownControl))
                {
                    MoveFocusOutOf(control);
                    _shownControl = null;
                    _shownLayout = null;
                }

                control.Layout = null;
                Children.Remove(control);
                removed++;
            }

            if (removed > 0)
            {
                _logger?.LogDebug(
                    "DockStage: removed {Removed} dead layouts, cached controls={Count}",
                    removed, _controls.Count);
            }
        }

        /// <summary>
        /// Скрытые DockControl убираются из списка фабрики: Dock перебирает этот
        /// список при поиске цели перетаскивания и не должен видеть невидимые
        /// раскладки, лежащие под показанной.
        /// </summary>
        private static void SetRegisteredInFactory(DockControl control, bool registered)
        {
            var dockControls = control.Layout?.Factory?.DockControls;
            if (dockControls == null)
                return;

            bool contains = dockControls.Contains(control);
            if (registered && !contains)
                dockControls.Add(control);
            else if (!registered && contains)
                dockControls.Remove(control);
        }

        /// <summary>
        /// Фокус не должен оставаться внутри скрытой раскладки: иначе клавиатурный
        /// ввод уходил бы в невидимый редактор. Фокус уводится в FocusSink главного
        /// окна (соседний элемент сцены) — тем же путём, что и клик мимо TextBox:
        /// у TextBox срабатывает LostFocus, и незавершённая правка фиксируется.
        /// </summary>
        private void MoveFocusOutOf(DockControl control)
        {
            var focusManager = TopLevel.GetTopLevel(this)?.FocusManager;
            if (focusManager?.GetFocusedElement() is not Visual focused
                || !control.IsVisualAncestorOf(focused))
                return;

            var focusSink = (Parent as Panel)?.Children
                .OfType<Control>()
                .FirstOrDefault(c => c.Name == "FocusSink");

            focusSink?.Focus();
        }
    }
}
