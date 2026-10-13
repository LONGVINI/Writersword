using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReactiveUI;
using Writersword.Modules.TextEditor.ViewModels;
using Writersword.Modules.TextEditor.Views.Toc;

namespace Writersword.Modules.TextEditor.Views
{
    /// <summary>
    /// Боковые панели рабочей области: навигатор слева и рецензия справа.
    ///
    /// Панель либо стоит рядом с листами, либо выдвигается поверх них. Рядом — когда
    /// после неё рукописи остаётся не меньше <see cref="MinCanvasPx"/>: тогда крайняя
    /// колонка рабочей области берёт её ширину, и листы сдвигаются, а не уходят под
    /// панель. Не хватает — панель ложится поверх листов с тенью, и тогда открыта
    /// только одна: две выдвинутые панели в узком окне закрыли бы рукопись целиком.
    /// Выдвинутая панель уходит по щелчку мимо неё и по Esc. Если ширины хватает на
    /// одну пристёгнутую, пристёгивается рецензия: с ней работают, глядя в текст, а в
    /// навигатор заглядывают на мгновение.
    ///
    /// Ширину тянут за внутренний край. Пока тянут, пристёгнутая панель растёт поверх
    /// листов, а колонка под ней меняется только по отпускании: перекладывать книгу
    /// на каждый пиксель движения незачем.
    /// </summary>
    public partial class TextEditorView
    {
        // Сколько рукописи оставить рядом с пристёгнутыми панелями.
        private const double MinCanvasPx = 560;

        // Пределы ширины панели.
        private const double MinDrawerPx = 280;
        private const double MaxDrawerPx = 560;

        // Сколько рабочей области выдвинутая панель оставляет открытой: полоска
        // листа, по которой видно, что панель лежит поверх него, и куда щёлкнуть,
        // чтобы её убрать.
        private const double OverlayGapPx = 48;

        // Время выезда; совпадает с переходами в разметке.
        private static readonly TimeSpan DrawerSlide = TimeSpan.FromMilliseconds(230);

        private static readonly BoxShadows OverlayShadow = BoxShadows.Parse("0 0 28 0 #73000000");

        private double _leftDrawerWidth = 280;
        private double _rightDrawerWidth = 380;

        private bool _leftDocked;
        private bool _rightDocked;
        private bool _leftShown;
        private bool _rightShown;

        // Прежнее состояние открытия: видно, какая панель открылась только что.
        private bool _wasNavigatorOpen;
        private bool _wasReviewOpen;

        private IDisposable? _drawerSubscription;

        // Перетаскивание края.
        private Border? _gripDrawer;
        private double _gripStartX;
        private double _gripStartWidth;

        private Grid? _workspaceHost;
        private Border? _leftDrawer;
        private Border? _rightDrawer;
        private Border? _leftSpacer;
        private Border? _rightSpacer;
        private Button? _navigatorHandle;
        private Button? _reviewHandle;

        private void WireDrawers()
        {
            _workspaceHost = this.FindControl<Grid>("WorkspaceHost");
            _leftDrawer = this.FindControl<Border>("LeftDrawer");
            _rightDrawer = this.FindControl<Border>("RightDrawer");
            _leftSpacer = this.FindControl<Border>("LeftDockSpacer");
            _rightSpacer = this.FindControl<Border>("RightDockSpacer");
            _navigatorHandle = this.FindControl<Button>("NavigatorHandle");
            _reviewHandle = this.FindControl<Button>("ReviewHandle");

            if (_workspaceHost is null || _leftDrawer is null || _rightDrawer is null) return;

            _leftDrawer.RenderTransform = SlideOut(-1, _leftDrawerWidth);
            _rightDrawer.RenderTransform = SlideOut(1, _rightDrawerWidth);

            // Навигатор задаёт себе ширину сам; в выдвижной панели её задаёт панель.
            if (this.FindControl<NavigatorPanel>("NavigatorPanelView") is { } navigator)
            {
                navigator.Width = double.NaN;
                navigator.CloseRequested += (_, _) => SetNavigatorOpen(false);
            }

            if (_navigatorHandle is not null)
                _navigatorHandle.Click += (_, _) => SetNavigatorOpen(true);
            if (_reviewHandle is not null)
                _reviewHandle.Click += (_, _) => SetReviewOpen(true);

            WireGrip(this.FindControl<Border>("LeftDrawerGrip"), _leftDrawer);
            WireGrip(this.FindControl<Border>("RightDrawerGrip"), _rightDrawer);

            WireScrollBarInset();

            _workspaceHost.PropertyChanged += (_, e) =>
            {
                if (e.Property == BoundsProperty) ApplyDrawers();
            };

            // Щелчок мимо выдвинутой панели её убирает. Слушается на подъёме: сам
            // щелчок при этом доходит до листа, и человек не тратит лишнего нажатия.
            _workspaceHost.AddHandler(PointerPressedEvent, OnWorkspacePointerPressed,
                RoutingStrategies.Tunnel, handledEventsToo: true);
            _workspaceHost.AddHandler(KeyDownEvent, OnWorkspaceKeyDown, RoutingStrategies.Tunnel);

            DataContextChanged += (_, _) => SubscribeDrawers();
            SubscribeDrawers();
        }

        // ── Язычок рецензии и полоса прокрутки ───────────────────────────
        // Язычок стоит у правого края области листов, а там же — вертикальная полоса
        // прокрутки листов. Язычок прижимается к полосе вплотную: его правый край —
        // ровно левый край полосы. Положение полосы берётся из раскладки, а не из
        // её ширины: ширина ничего не говорит о том, где полоса стоит относительно
        // слоя язычка, и прежний отступ «ширина полосы плюс зазор» оставлял то щель,
        // то наезд — смотря как разложены соседние панели. Полосы нет — язычок стоит
        // вплотную к правому краю области прокрутки.

        private ScrollBar? _documentVerticalBar;
        private ScrollViewer? _documentViewer;

        private void WireScrollBarInset()
        {
            if (this.FindControl<ScrollViewer>("DocumentScrollViewer") is not { } viewer) return;

            _documentViewer = viewer;

            // Положение полосы относительно слоя язычка меняется и без её собственных
            // изменений: открылась или закрылась панель, сменился размер окна.
            viewer.PropertyChanged += (_, e) =>
            {
                if (e.Property == BoundsProperty) UpdateReviewHandleInset();
            };
            if (_reviewHandle?.Parent is Control handleHost)
            {
                handleHost.PropertyChanged += (_, e) =>
                {
                    if (e.Property == BoundsProperty) UpdateReviewHandleInset();
                };
            }

            void Attach()
            {
                var bar = FindVerticalBar(viewer);
                if (bar is null || ReferenceEquals(bar, _documentVerticalBar)) return;

                if (_documentVerticalBar is not null)
                    _documentVerticalBar.PropertyChanged -= OnDocumentBarChanged;

                _documentVerticalBar = bar;
                bar.PropertyChanged += OnDocumentBarChanged;
                UpdateReviewHandleInset();
            }

            viewer.TemplateApplied += (_, _) => Dispatcher.UIThread.Post(Attach, DispatcherPriority.Loaded);
            viewer.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(Attach, DispatcherPriority.Loaded);
            Dispatcher.UIThread.Post(Attach, DispatcherPriority.Loaded);
        }

        private static ScrollBar? FindVerticalBar(ScrollViewer viewer)
        {
            foreach (var visual in viewer.GetVisualDescendants())
            {
                if (visual is ScrollBar { Orientation: Avalonia.Layout.Orientation.Vertical } bar
                    && ReferenceEquals(bar.FindAncestorOfType<ScrollViewer>(), viewer))
                    return bar;
            }

            return null;
        }

        private void OnDocumentBarChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == BoundsProperty || e.Property == IsVisibleProperty)
                UpdateReviewHandleInset();
        }

        private void UpdateReviewHandleInset()
        {
            if (_reviewHandle?.Parent is not Control host) return;
            if (host.Bounds.Width <= 0) return;

            // Левый край полосы — если она на виду; иначе правый край области прокрутки.
            Point? edge = null;
            if (_documentVerticalBar is { IsVisible: true } bar && bar.Bounds.Width > 0)
                edge = bar.TranslatePoint(new Point(0, 0), host);
            else if (_documentViewer is { } viewer && viewer.Bounds.Width > 0)
                edge = viewer.TranslatePoint(new Point(viewer.Bounds.Width, 0), host);

            if (edge is not { } point) return;

            double right = Math.Max(0, Math.Round(host.Bounds.Width - point.X));
            var current = _reviewHandle.Margin;
            var margin = new Thickness(current.Left, current.Top, right, current.Bottom);

            if (_reviewHandle.Margin != margin)
                _reviewHandle.Margin = margin;
        }

        private void SubscribeDrawers()
        {
            _drawerSubscription?.Dispose();
            _drawerSubscription = null;

            if (DataContext is not TextEditorViewModel vm)
            {
                ApplyDrawers();
                return;
            }

            _wasNavigatorOpen = vm.IsNavigatorVisible;
            _wasReviewOpen = vm.IsReviewPanelVisible;

            _drawerSubscription = vm
                .WhenAnyValue(x => x.IsNavigatorVisible, x => x.IsReviewPanelVisible,
                              x => x.IsReadingMode, x => x.IsFocusMode)
                .Subscribe(_ => OnDrawerStateChanged());

            ApplyDrawers();
        }

        private void SetNavigatorOpen(bool open)
        {
            if (DataContext is TextEditorViewModel vm) vm.IsNavigatorVisible = open;
        }

        private void SetReviewOpen(bool open)
        {
            if (DataContext is TextEditorViewModel vm) vm.IsReviewPanelVisible = open;
        }

        /// <summary>
        /// Открытие сменилось. Если только что открытая панель ляжет поверх листов, а
        /// вторая уже лежит поверх, вторая уходит: выдвинутой бывает одна.
        /// </summary>
        private void OnDrawerStateChanged()
        {
            if (DataContext is not TextEditorViewModel vm) return;

            bool navOpened = vm.IsNavigatorVisible && !_wasNavigatorOpen;
            bool reviewOpened = vm.IsReviewPanelVisible && !_wasReviewOpen;
            _wasNavigatorOpen = vm.IsNavigatorVisible;
            _wasReviewOpen = vm.IsReviewPanelVisible;

            if (navOpened || reviewOpened)
            {
                var (leftDock, rightDock) = DockPlan(vm.IsNavigatorVisible, vm.IsReviewPanelVisible, HostWidth);

                if (navOpened && !leftDock && vm.IsReviewPanelVisible && !rightDock)
                {
                    _wasReviewOpen = false;
                    vm.IsReviewPanelVisible = false;
                }
                else if (reviewOpened && !rightDock && vm.IsNavigatorVisible && !leftDock)
                {
                    _wasNavigatorOpen = false;
                    vm.IsNavigatorVisible = false;
                }
            }

            ApplyDrawers();
        }

        private double HostWidth => _workspaceHost?.Bounds.Width ?? 0;

        /// <summary>Какие из открытых панелей встают рядом с листами.</summary>
        private (bool Left, bool Right) DockPlan(bool leftOpen, bool rightOpen, double hostWidth)
        {
            if (hostWidth < 1) return (false, false);

            bool right = rightOpen && hostWidth - _rightDrawerWidth >= MinCanvasPx;
            bool left = leftOpen && hostWidth - _leftDrawerWidth - (right ? _rightDrawerWidth : 0) >= MinCanvasPx;
            return (left, right);
        }

        /// <summary>Расставляет панели, колонки под ними и язычки по текущему состоянию.</summary>
        private void ApplyDrawers()
        {
            if (_leftDrawer is null || _rightDrawer is null) return;

            var vm = DataContext as TextEditorViewModel;
            bool reading = vm?.IsReadingMode ?? false;
            bool leftOpen = vm is not null && vm.IsNavigatorVisible && !reading;
            bool rightOpen = vm is not null && vm.IsReviewPanelVisible && !reading;

            double hostWidth = HostWidth;
            var (leftDock, rightDock) = DockPlan(leftOpen, rightOpen, hostWidth);

            // Выдвинутая панель не шире рабочей области за вычетом полоски листа.
            double overlayMax = Math.Max(MinDrawerPx, hostWidth - OverlayGapPx);
            double leftWidth = leftDock ? _leftDrawerWidth : Math.Min(_leftDrawerWidth, overlayMax);
            double rightWidth = rightDock ? _rightDrawerWidth : Math.Min(_rightDrawerWidth, overlayMax);

            _leftDocked = leftOpen && leftDock;
            _rightDocked = rightOpen && rightDock;

            if (_gripDrawer is null)
            {
                _leftDrawer.Width = leftWidth;
                _rightDrawer.Width = rightWidth;
                if (_leftSpacer is not null) _leftSpacer.Width = _leftDocked ? leftWidth : 0;
                if (_rightSpacer is not null) _rightSpacer.Width = _rightDocked ? rightWidth : 0;
            }

            _leftDrawer.BoxShadow = leftOpen && !_leftDocked ? OverlayShadow : default;
            _rightDrawer.BoxShadow = rightOpen && !_rightDocked ? OverlayShadow : default;

            ShowDrawer(_leftDrawer, leftOpen, -1, ref _leftShown);
            ShowDrawer(_rightDrawer, rightOpen, 1, ref _rightShown);

            bool handles = vm is not null && !reading && !vm.IsFocusMode;
            if (_navigatorHandle is not null) _navigatorHandle.IsVisible = handles && !leftOpen;
            if (_reviewHandle is not null)
            {
                _reviewHandle.IsVisible = handles && !rightOpen;
                if (_reviewHandle.IsVisible)
                    Dispatcher.UIThread.Post(UpdateReviewHandleInset, DispatcherPriority.Loaded);
            }
        }

        /// <summary>
        /// Выдвигает панель или убирает её за край. Убранная скрывается совсем после
        /// выезда — чтобы Tab не заводил фокус в то, чего не видно.
        /// </summary>
        private static void ShowDrawer(Border drawer, bool open, int side, ref bool shown)
        {
            if (open == shown)
            {
                if (!open) drawer.RenderTransform = SlideOut(side, drawer.Width);
                return;
            }

            shown = open;

            if (open)
            {
                drawer.IsVisible = true;
                drawer.IsHitTestVisible = true;

                // Выезд начинается со следующего кадра: в этот панель только что стала
                // видимой, и переход от положения за краем иначе не запустится.
                Dispatcher.UIThread.Post(() =>
                {
                    if (!drawer.IsHitTestVisible) return;
                    drawer.RenderTransform = TransformOperations.Parse("translateX(0px)");
                    drawer.Opacity = 1;
                }, DispatcherPriority.Render);
                return;
            }

            drawer.IsHitTestVisible = false;
            drawer.RenderTransform = SlideOut(side, drawer.Width);
            drawer.Opacity = 0;

            DispatcherTimer.RunOnce(() =>
            {
                if (!drawer.IsHitTestVisible) drawer.IsVisible = false;
            }, DrawerSlide);
        }

        private static ITransform SlideOut(int side, double width)
        {
            double w = double.IsNaN(width) || width <= 0 ? 400 : width;
            return TransformOperations.Parse(
                "translateX(" + (side * (w + 32)).ToString(System.Globalization.CultureInfo.InvariantCulture) + "px)");
        }

        // ── Щелчок мимо и Esc ─────────────────────────────────────────────

        private void OnWorkspacePointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.Source is not Visual source) return;

            if (_leftShown && !_leftDocked && !IsInside(source, _leftDrawer) && !IsInside(source, _navigatorHandle))
                SetNavigatorOpen(false);

            if (_rightShown && !_rightDocked && !IsInside(source, _rightDrawer) && !IsInside(source, _reviewHandle))
                SetReviewOpen(false);
        }

        private void OnWorkspaceKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Source is not Visual source) return;

            // Отмена и повтор из панели. Принятую по ошибке правку возвращают сразу,
            // не уводя фокус на лист: полотно ловит Ctrl+Z только у себя. В полях ввода
            // панели эти клавиши остаются за полем.
            if ((e.KeyModifiers & KeyModifiers.Control) != 0
                && (e.Key == Key.Z || e.Key == Key.Y)
                && source is not TextBox
                && (IsInside(source, _leftDrawer) || IsInside(source, _rightDrawer))
                && DataContext is TextEditorViewModel { DocumentViewModel: { } document })
            {
                bool redo = e.Key == Key.Y || (e.KeyModifiers & KeyModifiers.Shift) != 0;
                if (redo) document.Redo();
                else document.Undo();
                e.Handled = true;
                return;
            }

            if (e.Key != Key.Escape) return;

            if (IsInside(source, _leftDrawer))
            {
                if (!_leftDocked) SetNavigatorOpen(false);
                FocusCanvas();
                e.Handled = true;
            }
            else if (IsInside(source, _rightDrawer))
            {
                // Esc в поле ответа сначала снимает фокус с поля — это решает сама панель.
                if (source is TextBox) return;

                if (!_rightDocked) SetReviewOpen(false);
                FocusCanvas();
                e.Handled = true;
            }
        }

        private void FocusCanvas() => this.FindControl<Control>("PageCanvas")?.Focus();

        private static bool IsInside(Visual source, Visual? container)
        {
            if (container is null) return false;
            for (var v = source; v is not null; v = v.GetVisualParent())
                if (ReferenceEquals(v, container)) return true;
            return false;
        }

        // ── Ширина ────────────────────────────────────────────────────────

        private void WireGrip(Border? grip, Border drawer)
        {
            if (grip is null) return;

            grip.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;

                _gripDrawer = drawer;
                _gripStartX = e.GetPosition(this).X;
                _gripStartWidth = drawer.Bounds.Width;
                e.Pointer.Capture(grip);
                e.Handled = true;
            };

            grip.PointerMoved += (_, e) =>
            {
                if (!ReferenceEquals(_gripDrawer, drawer)) return;

                double dx = e.GetPosition(this).X - _gripStartX;
                bool left = ReferenceEquals(drawer, _leftDrawer);
                double width = _gripStartWidth + (left ? dx : -dx);

                double max = Math.Min(MaxDrawerPx, Math.Max(MinDrawerPx, HostWidth - OverlayGapPx));
                drawer.Width = Math.Clamp(width, MinDrawerPx, max);
                e.Handled = true;
            };

            grip.PointerReleased += (_, e) =>
            {
                if (!ReferenceEquals(_gripDrawer, drawer)) return;
                EndGrip(drawer);
                e.Pointer.Capture(null);
                e.Handled = true;
            };

            grip.PointerCaptureLost += (_, _) =>
            {
                if (ReferenceEquals(_gripDrawer, drawer)) EndGrip(drawer);
            };
        }

        private void EndGrip(Border drawer)
        {
            _gripDrawer = null;

            if (ReferenceEquals(drawer, _leftDrawer)) _leftDrawerWidth = drawer.Width;
            else _rightDrawerWidth = drawer.Width;

            ApplyDrawers();
        }
    }
}
