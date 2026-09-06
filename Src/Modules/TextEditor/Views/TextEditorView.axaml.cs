using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using ReactiveUI;
using Serilog;
using System;
using Writersword.Modules.Common;
using Writersword.Modules.TextEditor.Document;
using Writersword.Modules.TextEditor.ViewModels;
using Writersword.Modules.TextEditor.ViewModels.StatusBar;
using Writersword.Modules.TextEditor.Views.Dialogs;
using Writersword.Modules.TextEditor.Views.Reading;

namespace Writersword.Modules.TextEditor.Views
{
    public partial class TextEditorView : UserControl
    {
        private static readonly ILogger _logger = Log.ForContext<TextEditorView>();
        private readonly UndoRedoStack _undoStack;
        private IDisposable? _monitorSubscription;
        private IDisposable? _readingSubscription;

        // Всплывающая подсказка номера страницы при перетаскивании ползунка.
        private bool _draggingScrollbar;
        private DocumentCanvas? _tooltipCanvas;
        private ScrollViewer? _tooltipScrollViewer;
        private StackPanel? _pageTooltip;
        private TextBlock? _pageTooltipText;

        // Единственный экземпляр ленты чтения. Он же переезжает из верхней полосы
        // в боковую колонку и обратно: два экземпляра делили бы одну вью-модель и
        // спорили за её признак вертикальности.
        private ReadingRibbonView? _readingRibbon;

        // Ширина рабочей области, ниже которой горизонтальная лента отбирает у книги
        // больше, чем даёт, и уходит вбок вертикальной колонкой.
        private const double ReadingNarrowWidthPx = 940;

        // Запас на обратный переход. Без него ширина, легшая ровно на порог, качала бы
        // ленту между верхом и боком: уход вбок возвращает рабочей области свою ширину,
        // а та снова оказывается достаточной — и лента идёт обратно.
        private const double ReadingWideEnoughPx = ReadingNarrowWidthPx + 80;

        // Ширина боковой колонки. Та же величина стоит в разметке.
        private const double ReadingSideRibbonPx = 182;

        // Высота горизонтальной ленты. Та же величина стоит в разметке.
        private const double ReadingRibbonHeightPx = 104;

        public TextEditorView(UndoRedoStack undoStack)
        {
            _undoStack = undoStack;
            InitializeComponent();
            WireCanvas();
            WireScroll();
            WireContentTopOffset();
            WirePageTooltip();
            WireReadingRibbon();
        }

        public TextEditorView() : this(new UndoRedoStack()) { }

        private void WireCanvas()
        {
            var canvas = this.FindControl<DocumentCanvas>("PageCanvas");
            if (canvas is null)
            {
                _logger.Warning("PageCanvas not found");
                return;
            }

            canvas.UndoStack = _undoStack;
            DataContextChanged += (_, _) => SyncCanvas(canvas);
            SyncCanvas(canvas);
        }

        private void WireScroll()
        {
            DataContextChanged += (_, _) =>
            {
                if (DataContext is not TextEditorViewModel vm) return;

                var scrollViewer = this.FindControl<ScrollViewer>("DocumentScrollViewer");
                if (scrollViewer is null) return;

                var pageCanvas = this.FindControl<DocumentCanvas>("PageCanvas");

                vm.Ruler.ScrollOffsetY = scrollViewer.Offset.Y;
                vm.Ruler.ViewportHeight = scrollViewer.Viewport.Height;
                if (pageCanvas is not null)
                    vm.Ruler.FocusedPageIndex = pageCanvas.GetPageAtOffset(scrollViewer.Offset.Y) - 1;

                scrollViewer.ScrollChanged += (_, _) =>
                {
                    vm.Ruler.ScrollOffsetY = scrollViewer.Offset.Y;
                    vm.Ruler.ViewportHeight = scrollViewer.Viewport.Height;
                    // Вертикальная линейка следует за страницей вверху вьюпорта (как в Word),
                    // а не за страницей каретки: при скролле далеко от каретки шкала иначе
                    // привязывалась к невидимой странице и уезжала.
                    if (pageCanvas is not null)
                        vm.Ruler.FocusedPageIndex = pageCanvas.GetPageAtOffset(scrollViewer.Offset.Y) - 1;
                };
            };
        }

        /// <summary>
        /// Держит у линейки актуальный вертикальный сдвиг канваса внутри вьюпорта.
        /// ArrangeOverride канваса возвращает реальную высоту документа, и когда она меньше
        /// высоты вьюпорта, Avalonia (Layoutable.ArrangeCore при VerticalAlignment=Stretch)
        /// центрирует канвас по вертикали. Лист тогда стоит ниже верха вьюпорта, а линейка
        /// об этом не знала и рисовала шкалу от верха — на мелком зуме расхождение достигало
        /// десятков пикселей. Считаем сдвиг по реальной геометрии, а не по предположению
        /// о выравнивании: TranslatePoint даёт положение верха канваса в координатах
        /// ScrollViewer, прибавленный Offset.Y снимает вклад прокрутки.
        /// Подписка одна на всё время жизни вью — DataContext читается на каждом вызове.
        /// </summary>
        private void WireContentTopOffset()
        {
            var scrollViewer = this.FindControl<ScrollViewer>("DocumentScrollViewer");
            var pageCanvas = this.FindControl<DocumentCanvas>("PageCanvas");
            if (scrollViewer is null || pageCanvas is null) return;

            pageCanvas.LayoutUpdated += (_, _) =>
            {
                if (DataContext is not TextEditorViewModel vm) return;

                var origin = pageCanvas.TranslatePoint(new Point(0, 0), scrollViewer);
                if (origin is null) return;

                vm.Ruler.ContentTopOffsetPx = origin.Value.Y + scrollViewer.Offset.Y;
            };
        }

        private void WirePageTooltip()
        {
            _tooltipScrollViewer = this.FindControl<ScrollViewer>("DocumentScrollViewer");
            _tooltipCanvas = this.FindControl<DocumentCanvas>("PageCanvas");
            _pageTooltip = this.FindControl<StackPanel>("PageDragTooltip");
            _pageTooltipText = this.FindControl<TextBlock>("PageDragTooltipText");

            if (_tooltipScrollViewer is null) return;

            // Ждём применения шаблона, чтобы добраться до вертикального ползунка.
            _tooltipScrollViewer.TemplateApplied += (_, args) =>
            {
                var vbar = args.NameScope.Find<ScrollBar>("PART_VerticalScrollBar");
                if (vbar is null) return;

                // Tunnel — срабатывает даже когда указатель захвачен ползунком.
                vbar.AddHandler(PointerPressedEvent, OnScrollbarPressed, RoutingStrategies.Tunnel);
                vbar.AddHandler(PointerReleasedEvent, OnScrollbarReleased, RoutingStrategies.Tunnel);
                vbar.AddHandler(PointerCaptureLostEvent, OnScrollbarCaptureLost, RoutingStrategies.Tunnel);
            };

            // Обновление подсказки во время прокрутки, пока ползунок зажат.
            _tooltipScrollViewer.ScrollChanged += (_, _) =>
            {
                if (_draggingScrollbar) UpdatePageTooltip();
            };
        }

        private void OnScrollbarPressed(object? sender, PointerPressedEventArgs e)
        {
            _draggingScrollbar = true;
            if (_pageTooltip is not null) _pageTooltip.IsVisible = true;
            UpdatePageTooltip();
        }

        private void OnScrollbarReleased(object? sender, PointerReleasedEventArgs e) => HidePageTooltip();

        private void OnScrollbarCaptureLost(object? sender, PointerCaptureLostEventArgs e) => HidePageTooltip();

        private void HidePageTooltip()
        {
            _draggingScrollbar = false;
            if (_pageTooltip is not null) _pageTooltip.IsVisible = false;
        }

        private void UpdatePageTooltip()
        {
            if (_tooltipCanvas is null || _tooltipScrollViewer is null
                || _pageTooltip is null || _pageTooltipText is null) return;

            int page = _tooltipCanvas.GetPageAtOffset(_tooltipScrollViewer.Offset.Y);
            int total = _tooltipCanvas.PageCount;
            if (page > total) page = total;
            _pageTooltipText.Text = $"Страница {page} / {total}";

            // Позиция подсказки — по центру ползунка. Считаем геометрию ползунка
            // из extent/viewport/offset, а не по доле прокрутки.
            double extent = _tooltipScrollViewer.Extent.Height;
            double viewport = _tooltipScrollViewer.Viewport.Height;
            double offset = _tooltipScrollViewer.Offset.Y;
            if (extent <= 0.0 || viewport <= 0.0) return;

            double thumbHeight = viewport / extent * viewport;
            double thumbCenter = offset / extent * viewport + thumbHeight / 2.0;

            double top = thumbCenter - _pageTooltip.Bounds.Height / 2.0;
            double maxTop = viewport - _pageTooltip.Bounds.Height;
            if (top < 0.0) top = 0.0;
            else if (top > maxTop) top = maxTop < 0.0 ? 0.0 : maxTop;
            _pageTooltip.Margin = new Thickness(0, top, 16, 0);
        }

        protected override void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);
            var canvas = this.FindControl<DocumentCanvas>("PageCanvas");
            if (canvas is null) return;
            // После реаттача в Dock (RecreateDocumentViews) ScrollViewer
            // ещё не знает свой реальный размер в момент OnAttachedToVisualTree.
            // Принудительный перемер здесь гарантирует что canvas получит
            // правильный _viewportHeight и запустит рендер.
            canvas.InvalidateMeasure();
        }

        // ── Лента чтения ──────────────────────────────────────────────────

        /// <summary>
        /// Держит ленту чтения в согласии с режимом: показывает и прячет её, уводит
        /// вбок в узком окне, разворачивает и сворачивает язычком.
        /// </summary>
        private void WireReadingRibbon()
        {
            _readingRibbon = this.FindControl<ContentControl>("ReadingRibbonHost")?.Content as ReadingRibbonView;

            DataContextChanged += (_, _) => SubscribeReadingState();
            SubscribeReadingState();

            // Ширина рабочей области решает, горизонтальная лента или вертикальная.
            // Мерить нужно именно её, а не всё окно: слева от книги стоит панель
            // проекта, и окно может быть широким, когда книге места уже нет.
            var work = this.FindControl<ScrollViewer>("DocumentScrollViewer");
            if (work is not null)
                work.SizeChanged += (_, _) => UpdateReadingLayoutMode();

            SizeChanged += (_, _) => UpdateReadingLayoutMode();
        }

        private void SubscribeReadingState()
        {
            _readingSubscription?.Dispose();
            _readingSubscription = null;

            if (DataContext is not TextEditorViewModel vm) return;

            // Три признака решают вид ленты: идёт ли чтение, развёрнута ли лента и
            // хватает ли ширины на горизонтальную полосу.
            var byMode = vm.WhenAnyValue(x => x.IsReadingMode)
                .Subscribe(_ => ApplyReadingUiState());

            var byRibbon = vm.ReadingRibbon
                .WhenAnyValue(x => x.RibbonExpanded, x => x.IsVertical)
                .Subscribe(_ => ApplyReadingUiState());

            _readingSubscription = new System.Reactive.Disposables.CompositeDisposable(byMode, byRibbon);

            ApplyReadingUiState();
            UpdateReadingLayoutMode();
        }

        /// <summary>
        /// Решает, идёт лента поверху или стоит вбок. Порог один и по ширине рабочей
        /// области: считать по числу групп бессмысленно — они разной ширины.
        /// </summary>
        private void UpdateReadingLayoutMode()
        {
            if (DataContext is not TextEditorViewModel vm) return;
            if (!vm.IsReadingMode) return;

            var work = this.FindControl<ScrollViewer>("DocumentScrollViewer");
            double width = work?.Bounds.Width ?? Bounds.Width;
            if (width < 1) return;

            // Пока лента стоит вбок, она сама отнимает у рабочей области свою ширину.
            // Возврат к горизонтальной считается по восстановленной ширине, иначе
            // лента застревала бы сбоку навсегда. Пороги ухода и возврата разные —
            // на одинаковых лента качалась бы на границе туда-обратно.
            if (vm.ReadingRibbon.IsVertical)
            {
                if (width + ReadingSideRibbonPx > ReadingWideEnoughPx)
                    vm.ReadingRibbon.IsVertical = false;
                return;
            }

            if (width < ReadingNarrowWidthPx)
                vm.ReadingRibbon.IsVertical = true;
        }

        /// <summary>Расставляет ленту чтения и её язычок по текущему состоянию.</summary>
        private void ApplyReadingUiState()
        {
            var topHost = this.FindControl<ContentControl>("ReadingRibbonHost");
            var sideHost = this.FindControl<ContentControl>("ReadingRibbonSideHost");
            var tab = this.FindControl<Button>("ReadingRibbonTab");
            var arrow = this.FindControl<Avalonia.Controls.Shapes.Path>("ReadingRibbonTabArrow");
            if (topHost is null || sideHost is null || tab is null) return;

            _readingRibbon ??= (topHost.Content as ReadingRibbonView) ?? (sideHost.Content as ReadingRibbonView);

            bool reading = DataContext is TextEditorViewModel v && v.IsReadingMode;
            bool vertical = DataContext is TextEditorViewModel v2 && v2.ReadingRibbon.IsVertical;
            bool expanded = DataContext is TextEditorViewModel v3 && v3.ReadingRibbon.RibbonExpanded;

            // Лента переезжает целиком, а не копируется: вью-модель у неё одна.
            if (_readingRibbon is not null)
            {
                if (vertical && !ReferenceEquals(sideHost.Content, _readingRibbon))
                {
                    topHost.Content = null;
                    sideHost.Content = _readingRibbon;
                }
                else if (!vertical && !ReferenceEquals(topHost.Content, _readingRibbon))
                {
                    sideHost.Content = null;
                    topHost.Content = _readingRibbon;
                }

                // Лента держит свой полный размер и прижата к тому краю, из-под
                // которого выезжает; обрезает её хозяин. Иначе она не выезжает, а
                // сплющивается: содержимое ужимается вместе с хозяином, группы с их
                // выравниванием по центру ползают внутри, и вместо чистого движения
                // выходит толчея.
                if (vertical)
                {
                    _readingRibbon.Width = ReadingSideRibbonPx;
                    _readingRibbon.Height = double.NaN;
                    _readingRibbon.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
                    _readingRibbon.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
                }
                else
                {
                    _readingRibbon.Height = ReadingRibbonHeightPx;
                    _readingRibbon.Width = double.NaN;
                    _readingRibbon.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
                    _readingRibbon.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
                }
            }

            // Лента не пропадает рывком, а уезжает: высота (у горизонтальной) и
            // ширина (у вертикальной) идут к нулю переходом, объявленным в разметке.
            // Видимость снимается только вместе с самим чтением — иначе уезжать было
            // бы нечему.
            topHost.IsVisible = reading && !vertical;
            sideHost.IsVisible = reading && vertical;
            tab.IsVisible = reading;

            topHost.Height = expanded ? ReadingRibbonHeightPx : 0;
            topHost.Opacity = expanded ? 1 : 0;

            sideHost.Width = expanded ? ReadingSideRibbonPx : 0;
            sideHost.Opacity = expanded ? 1 : 0;

            // Язычок принимает форму той стороны, с которой лежит лента: полукруг
            // снизу у горизонтальной, полукруг слева у вертикальной. Иначе он
            // выглядит приклеенным не к тому краю.
            if (vertical)
            {
                tab.Width = 20;
                tab.Height = 46;
                tab.Margin = new Thickness(0, 18, 0, 0);
                tab.BorderThickness = new Thickness(1, 1, 0, 1);
                tab.CornerRadius = new CornerRadius(12, 0, 0, 12);
            }
            else
            {
                tab.Width = 46;
                tab.Height = 20;
                tab.Margin = new Thickness(0, 0, 18, 0);
                tab.BorderThickness = new Thickness(1, 0, 1, 1);
                tab.CornerRadius = new CornerRadius(0, 0, 12, 12);
            }

            // Стрелка показывает, куда уйдёт лента. Горизонтальная убирается вверх,
            // вертикальная — вправо, поэтому и разворот у стрелки разный.
            if (arrow is not null)
            {
                string transform = vertical
                    ? (expanded ? "rotate(270deg)" : "rotate(90deg)")
                    : (expanded ? "rotate(180deg)" : "rotate(0deg)");
                arrow.RenderTransform = TransformOperations.Parse(transform);
            }
        }

        private void OnReadingRibbonTabClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not TextEditorViewModel vm) return;
            vm.ReadingRibbon.RibbonExpanded = !vm.ReadingRibbon.RibbonExpanded;
        }

        // ── Полноэкранное чтение ──────────────────────────────────────────

        // Прежнее состояние окна. Выйдя из чтения, человек должен получить окно
        // таким, каким оставил.
        private WindowState? _stateBeforeFullscreen;

        // Пока идёт полноэкранное чтение, содержимое модуля живёт не на своём месте
        // в разметке, а в слое поверх всего окна. Здесь — что и куда переехало.
        private OverlayLayer? _fullscreenLayer;
        private Panel? _fullscreenHost;
        private Control? _fullscreenContent;

        /// <summary>
        /// Разворачивает модуль на весь экран и обратно. Мало развернуть окно: над
        /// модулем остаются заголовок, вкладки и панели оболочки, а чтение затевается
        /// ровно затем, чтобы на экране не было ничего, кроме книги. Поэтому
        /// содержимое модуля переезжает в слой поверх всего окна — так же поступает
        /// браузер, уводя страницу поверх своей обвязки.
        /// </summary>
        private void ApplyFullscreen(bool on)
        {
            if (on) EnterFullscreen();
            else LeaveFullscreen();
        }

        private void EnterFullscreen()
        {
            if (_fullscreenContent is not null) return;
            if (Content is not Control root) return;

            var layer = OverlayLayer.GetOverlayLayer(this);
            if (layer is null) return;

            if (TopLevel.GetTopLevel(this) is Window window)
            {
                _stateBeforeFullscreen ??= window.WindowState;
                window.WindowState = WindowState.FullScreen;
            }

            _fullscreenContent = root;
            Content = null;

            // Подложка непрозрачна: слой сам по себе прозрачен, и без неё сквозь
            // книгу просвечивала бы оболочка приложения.
            var host = new Panel
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
                // Вью-модель дальше по дереву наследуется от хоста: вырванное из
                // разметки содержимое своего DataContext больше ниоткуда не получит.
                DataContext = DataContext,
                Width = layer.Bounds.Width,
                Height = layer.Bounds.Height
            };
            host.Children.Add(root);

            layer.Children.Add(host);
            layer.LayoutUpdated += OnFullscreenLayerLayoutUpdated;

            _fullscreenLayer = layer;
            _fullscreenHost = host;
        }

        private void LeaveFullscreen()
        {
            if (_fullscreenContent is null) return;

            if (_fullscreenLayer is not null)
                _fullscreenLayer.LayoutUpdated -= OnFullscreenLayerLayoutUpdated;

            _fullscreenHost?.Children.Clear();
            if (_fullscreenLayer is not null && _fullscreenHost is not null)
                _fullscreenLayer.Children.Remove(_fullscreenHost);

            Content = _fullscreenContent;

            _fullscreenContent = null;
            _fullscreenHost = null;
            _fullscreenLayer = null;

            if (TopLevel.GetTopLevel(this) is Window window)
            {
                window.WindowState = _stateBeforeFullscreen ?? WindowState.Normal;
                _stateBeforeFullscreen = null;
            }
        }

        private void OnFullscreenLayerLayoutUpdated(object? sender, EventArgs e)
        {
            if (_fullscreenLayer is null || _fullscreenHost is null) return;
            _fullscreenHost.Width = _fullscreenLayer.Bounds.Width;
            _fullscreenHost.Height = _fullscreenLayer.Bounds.Height;
        }

        // ── Связка с канвасом ─────────────────────────────────────────────

        private DocumentCanvas? SpreadCanvas => this.FindControl<DocumentCanvas>("PageCanvas");

        private void SyncCanvas(DocumentCanvas canvas)
        {
            if (DataContext is not TextEditorViewModel vm)
            {
                _logger.Debug("SyncCanvas: DataContext is not TextEditorViewModel");
                return;
            }

            canvas.RecommendedZoomChanged = recommendedZoom =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    vm.StatusBar.RecommendedZoom = recommendedZoom;
                    _logger.Debug("RecommendedZoom updated: {V}", recommendedZoom);
                }, Avalonia.Threading.DispatcherPriority.Background);
            };

            // X-смещение страницы → линейка.
            canvas.PageOffsetXChanged = pageOffsetXPx =>
            {
                vm.NotifyPageOffsetChanged(pageOffsetXPx);
            };

            // Число страниц и строк → строка состояния и окно статистики. Считает их
            // раскладка: по тексту документа ни то, ни другое не выводится.
            canvas.PaginationChanged = (pageCount, lineCount) =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    vm.StatusBar.UpdatePagination(pageCount, lineCount),
                    Avalonia.Threading.DispatcherPriority.Background);
            };

            // Окно полной статистики — обычный оверлей модуля.
            vm.WordCountRequested = ShowStatisticsOverlay;

            // Полноэкранное чтение: окном и слоями распоряжается вью, модуль о них
            // ничего не знает.
            vm.FullscreenRequested = ApplyFullscreen;

            // Листание и переходы по книге исполняет канвас.
            vm.ReadingTurnRequested = dir => SpreadCanvas?.SpreadTurn(dir);
            vm.ReadingGoToRequested = page =>
            {
                var c = SpreadCanvas;
                if (c is null) return;
                c.SpreadGoToPage(page < 0 ? Math.Max(0, c.SpreadPageCount - 1) : page);
            };
            vm.ReadingGoToPageRequested = (page, animate) =>
            {
                var c = SpreadCanvas;
                if (c is null) return;

                c.SpreadGoToPage(Math.Max(0, page), animate);

                // Просьба пришла из поля ввода в ленте, а оно единственное во всей
                // ленте берёт фокус. Клавиатуру нужно вернуть книге: иначе стрелки и
                // пробел уходят в поле, и листание молчит до первого щелчка мимо.
                //
                // Возвращается она и тогда, когда переход никуда не ведёт: ввод могли
                // и не разобрать, но поле человек уже отпустил.
                c.Focus();
            };

            // Виды чтения — обычный оверлей модуля.
            vm.ReadingThemesRequested = ShowThemeOverlay;

            // Выход из чтения и из полного экрана по клавише: в книге все нажатия
            // разбирает канвас, и наружу они не уходят.
            canvas.ReadingEscapePressed = () =>
            {
                if (vm.Reading is { Fullscreen: true })
                {
                    vm.ReadingRibbon.Fullscreen = false;
                    return;
                }
                vm.ExitReading();
            };

            canvas.ReadingFullscreenTogglePressed = () =>
                vm.ReadingRibbon.Fullscreen = !vm.ReadingRibbon.Fullscreen;

            // Смена разворота → подпись в ленте чтения.
            canvas.SpreadPageChanged = () =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    vm.UpdateSpreadPageLabel(canvas.SpreadPageNumber, canvas.SpreadPageCount),
                    Avalonia.Threading.DispatcherPriority.Background);
            };

            // Уведомление о входе/выходе каретки из таблицы.
            canvas.CaretEnteredTable = (offsets, widths, tableOffsetMm, activeCol) =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    vm.NotifyCaretEnteredTable(offsets, widths, tableOffsetMm, activeCol),
                    Avalonia.Threading.DispatcherPriority.Background);
            };

            canvas.CaretLeftTable = () =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    vm.NotifyCaretLeftTable(),
                    Avalonia.Threading.DispatcherPriority.Background);
            };

            // Фактическая геометрия абзаца под кареткой → положение стрелок линейки.
            //
            // Через диспетчер, но приоритетом Render: он отрабатывает в том же кадре, и
            // отставания стрелок на одно действие не возникает. Фоновый приоритет, стоявший
            // здесь раньше, доставлял геометрию уже после отрисовки — линейка показывала
            // величины предыдущего абзаца.
            //
            // Прямой синхронный вызов тоже не годится: событие приходит изнутри
            // RebuildLayouts (OnParagraphFormatChanged, NotifyCaretEnteredTableCallback), и
            // запись свойств вью-модели посреди пересборки поднимает PropertyChanged, а с ним
            // перерисовку линейки — вплоть до повторного входа в раскладку.
            canvas.RulerGeometryChanged = geometry =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    vm.NotifyRulerGeometry(geometry),
                    Avalonia.Threading.DispatcherPriority.Render);
            };

            // Выделение/снятие картинки → контекстная вкладка «Формат».
            canvas.ImageSelectionChanged = selected =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    vm.NotifyImageSelectionChanged(selected),
                    Avalonia.Threading.DispatcherPriority.Background);
            };

            // Выделение/снятие фигуры → контекстная вкладка «Формат фигуры».
            canvas.ShapeSelectionChanged = selected =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    vm.NotifyShapeSelectionChanged(selected),
                    Avalonia.Threading.DispatcherPriority.Background);
            };

            // Страница каретки → вертикальная линейка.
            // Вертикальная линейка использует FocusedPageIndex чтобы отображать
            // шкалу только для страницы где стоит каретка, как в Word.
            canvas.CaretPageChanged = pageIndex =>
            {
                vm.Ruler.FocusedPageIndex = pageIndex;

                // Та же страница — номер в строке состояния. Индекс раскладки нулевой,
                // человеку страницы нумеруются с единицы.
                vm.StatusBar.CurrentPage = pageIndex + 1;
            };

            _logger.Debug("SyncCanvas: MonitorSizeInches={V}", vm.MonitorSizeInches);
            canvas.MonitorSizeInches = vm.MonitorSizeInches;

            _monitorSubscription?.Dispose();
            _monitorSubscription = vm.WhenAnyValue(x => x.MonitorSizeInches)
                .Subscribe(v =>
                {
                    _logger.Debug("MonitorSizeInches subscription fired: {V}", v);
                    canvas.MonitorSizeInches = v;
                });
        }

        /// <summary>
        /// Показывает окно видов чтения и применяет результат. Окно правит копию:
        /// отказ должен возвращать всё ровно таким, каким было до открытия.
        /// </summary>
        private async void ShowThemeOverlay()
        {
            if (DataContext is not TextEditorViewModel vm) return;
            if (vm.Reading is not { } reading) return;

            var overlay = this.FindControl<ReadingThemeOverlay>("ThemeOverlay");
            if (overlay is null) return;

            var result = await overlay.ShowAsync(vm.ReadingThemes(), reading.ThemeId);
            if (result is null) return;

            vm.SaveReadingThemes(result.Themes);

            // Выбранный в окне вид сразу становится рабочим: человек его только что
            // настраивал и ждёт увидеть книгу такой.
            reading.ApplyTheme(result.Selected);
            vm.ReadingRibbon.RefreshAll();
            vm.ApplyReadingLayout();
        }

        /// <summary>
        /// Показывает окно полной статистики документа. Величины берутся из строки
        /// состояния: слова и знаки она пересчитывает по тексту перед открытием, а
        /// страницы и строки держит от последней пересборки раскладки.
        /// </summary>
        private void ShowStatisticsOverlay()
        {
            if (DataContext is not TextEditorViewModel vm) return;

            var overlay = this.FindControl<DocumentStatisticsOverlay>("StatisticsOverlay");
            if (overlay is null) return;

            var status = vm.StatusBar;
            var stats = new DocumentStatistics(
                Pages: status.PageCount,
                Words: status.WordCount,
                CharsNoSpaces: status.CharCountNoSpaces,
                CharsWithSpaces: status.CharCount,
                Paragraphs: status.ParagraphCount,
                Lines: status.LineCount);

            // Черновик и веб-разметка листов не строят: чисел страниц и строк там нет,
            // и показывать вместо них остатки прежней постраничной раскладки нельзя.
            bool draftLayout = status.ViewMode
                is Models.Document.EditorViewMode.Draft
                or Models.Document.EditorViewMode.Web;

            overlay.Show(stats, draftLayout);
        }

    }
}
