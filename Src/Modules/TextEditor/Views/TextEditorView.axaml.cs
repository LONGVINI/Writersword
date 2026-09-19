using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Platform.Storage;
using ReactiveUI;
using Serilog;
using System;
using Writersword.Modules.Common;
using Writersword.Modules.TextEditor.Document;
using Writersword.Modules.TextEditor.Models.Settings;
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
            WireFocusMode();
        }

        public TextEditorView() : this(new UndoRedoStack()) { }

        // ── Режим фокуса ──────────────────────────────────────────────────

        private IDisposable? _focusSubscription;

        // Полоса у верхней кромки рабочей области, при заходе в которую лента
        // возвращается. Двадцати точек хватает, чтобы попасть намеренно, и мало,
        // чтобы задеть случайно, целясь в первую строку текста.
        private const double FocusHoverBandPx = 20;

        // Ниже этой границы лента снова уезжает. Отступ от её нижнего края нужен,
        // иначе лента, выехав под курсором, сама уводит его за свою границу — и
        // тут же прячется.
        private const double FocusHideBelowPx = 40;

        /// <summary>
        /// Держит верх в согласии с фокусом: показывает язычок, возвращает ленту при
        /// заходе к верхней кромке и убирает её, когда указатель уходит к тексту.
        /// </summary>
        private void WireFocusMode()
        {
            DataContextChanged += (_, _) => SubscribeFocusState();
            SubscribeFocusState();

            // Наведение слушается на всём модуле: лента в фокусе убрана, и ловить
            // указатель на ней самой нечем — её ещё нет.
            AddHandler(PointerMovedEvent, OnFocusPointerMoved, RoutingStrategies.Tunnel);
        }

        private void SubscribeFocusState()
        {
            _focusSubscription?.Dispose();
            _focusSubscription = null;

            if (DataContext is not TextEditorViewModel vm)
            {
                ApplyFocusUiState();
                return;
            }

            _focusSubscription = vm
                .WhenAnyValue(x => x.IsFocusMode, x => x.IsReadingMode, x => x.IsRibbonCollapsed,
                              x => x.IsFocusRibbonPeeking)
                .Subscribe(_ => ApplyFocusUiState());

            ApplyFocusUiState();
        }

        /// <summary>
        /// Держит язычок ленты редактора в согласии с ней самой: показывает его
        /// везде, кроме чтения, и разворачивает стрелку туда, куда лента уйдёт по
        /// нажатию. Стрелка вниз — лента убрана и вернётся, вверх — лента на месте
        /// и уедет.
        /// </summary>
        private void ApplyFocusUiState()
        {
            var tab = this.FindControl<Button>("EditorRibbonTab");
            var arrow = this.FindControl<Avalonia.Controls.Shapes.Path>("EditorRibbonTabArrow");
            if (tab is null) return;

            if (DataContext is not TextEditorViewModel vm)
            {
                tab.IsVisible = false;
                return;
            }

            // В чтении у ленты правки язычка нет: там своя лента и свой язычок.
            tab.IsVisible = !vm.IsReadingMode;

            if (arrow is not null)
            {
                arrow.RenderTransform = TransformOperations.Parse(
                    vm.IsRibbonVisible ? "rotate(180deg)" : "rotate(0deg)");
            }
        }

        /// <summary>
        /// Возвращает ленту, когда указатель подходит к верхней кромке, и убирает,
        /// когда он уходит вниз. Работает только в фокусе и только если человек не
        /// отказался от наведения на вкладке «Вид»: с тачпада случайный заезд к
        /// верху экрана мешает больше, чем помогает.
        /// </summary>
        private void OnFocusPointerMoved(object? sender, PointerEventArgs e)
        {
            if (DataContext is not TextEditorViewModel vm) return;
            if (!vm.IsFocusMode || vm.IsReadingMode) return;

            if (vm.EditorView is not { FocusRibbonOnHover: true })
            {
                // Наведение выключено: лента живёт только язычком, и уводить её
                // движением мыши нельзя — иначе поднятая язычком лента исчезала бы
                // от первого же движения к тексту.
                return;
            }

            double y = e.GetPosition(this).Y;

            if (!vm.IsFocusRibbonPeeking)
            {
                if (y <= FocusHoverBandPx) vm.IsFocusRibbonPeeking = true;
                return;
            }

            var ribbon = this.FindControl<ContentControl>("EditorRibbonHost");
            double ribbonBottom = ribbon?.Bounds.Height ?? 0;
            if (ribbonBottom < 1) ribbonBottom = FocusHoverBandPx;

            if (y > ribbonBottom + FocusHideBelowPx) vm.IsFocusRibbonPeeking = false;
        }

        // ── Фон позади страниц ────────────────────────────────────────────

        // Разобранная картинка фона и адрес, по которому она прочитана. Держится
        // до смены адреса: читать файл и раскодировать его на каждую правку света
        // незачем, а правок света бывает по десятку в секунду.
        private Bitmap? _backdropBitmap;
        private string? _backdropBitmapRef;

        /// <summary>
        /// Перекладывает слой фона под прокруткой: цвет поля, картинка, её укладка
        /// и плотность. Фона нет — слои прячутся, и поле снова заливает канвас.
        /// </summary>
        private void ApplyBackdropLayer()
        {
            var fill = this.FindControl<Border>("BackdropFillLayer");
            var image = this.FindControl<Border>("BackdropImageLayer");
            if (fill is null || image is null) return;

            var backdrop = (DataContext as TextEditorViewModel)?.WindowBackdrop();
            if (backdrop is not { } b)
            {
                // Картинки нет — остаётся серая подложка. Прятать её нельзя: поле
                // заливает канвас, а он занимает высоту документа, и под коротким
                // документом сквозь прокрутку просвечивала бы тёмная оболочка.
                fill.Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8));
                fill.IsVisible = true;

                image.IsVisible = false;
                image.Background = null;
                ReleaseBackdropBitmap();
                return;
            }

            fill.Background = Color.TryParse(b.FieldHex, out var color)
                ? new SolidColorBrush(color)
                : new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8));
            fill.IsVisible = true;

            var bitmap = ResolveBackdropBitmap(b.ImageRef);
            if (bitmap is null)
            {
                // Цвет поля остаётся: слой уже закрыл собой канвас, и снимать его
                // здесь значит показать серую подложку вместо фона.
                image.IsVisible = false;
                image.Background = null;
                return;
            }

            image.Background = new ImageBrush(bitmap)
            {
                Stretch = b.Fit switch
                {
                    ReadingBackdropFit.Contain => Stretch.Uniform,
                    ReadingBackdropFit.Stretch => Stretch.Fill,
                    ReadingBackdropFit.Tile => Stretch.None,
                    _ => Stretch.UniformToFill
                },
                // Замощение повторяет картинку в её собственном размере — тем же
                // правилом, что и картинка бумаги.
                TileMode = b.Fit == ReadingBackdropFit.Tile ? TileMode.Tile : TileMode.None,
                DestinationRect = b.Fit == ReadingBackdropFit.Tile
                    ? new RelativeRect(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height,
                                       RelativeUnit.Absolute)
                    : new RelativeRect(0, 0, 1, 1, RelativeUnit.Relative)
            };

            image.Opacity = b.Opacity;
            image.IsVisible = true;
        }

        /// <summary>Картинка по адресу вида. Читается один раз и держится до смены адреса.</summary>
        private Bitmap? ResolveBackdropBitmap(string? reference)
        {
            if (string.IsNullOrWhiteSpace(reference)) return null;

            if (_backdropBitmap is not null
                && string.Equals(_backdropBitmapRef, reference, StringComparison.Ordinal))
                return _backdropBitmap;

            ReleaseBackdropBitmap();

            try
            {
                // Адрес разбирает библиотека фонов: она знает и свои папки, и
                // прежние адреса видов чтения, и пути на диске.
                var data = Models.Settings.BackdropLibrary.Read(reference);
                if (data is null || data.Length == 0)
                {
                    _logger.Warning("Background image is set but unreadable: {Ref}", reference);
                    return null;
                }

                using var stream = new System.IO.MemoryStream(data);
                _backdropBitmap = new Bitmap(stream);
                _backdropBitmapRef = reference;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to read the background image: {Ref}", reference);
                ReleaseBackdropBitmap();
            }

            return _backdropBitmap;
        }

        private void ReleaseBackdropBitmap()
        {
            _backdropBitmap?.Dispose();
            _backdropBitmap = null;
            _backdropBitmapRef = null;
        }

        // ── Картинка позади страниц ───────────────────────────────────────

        /// <summary>
        /// Выбирает картинку, которая ляжет на поле вокруг листа.
        ///
        /// Файл не запоминается путём на диске: путь переживает ни переезд папки,
        /// ни чистку загрузок, а фон после этого молча пропадает. Копия ложится в
        /// хранилище — и именно в данные программы, а не в архив проекта.
        ///
        /// Место хранения следует за настройкой. Вид рабочей области лежит в общих
        /// настройках модуля и одинаков во всех проектах; картинка, уложенная в
        /// архив одного проекта, в остальных не нашлась бы, и фон появлялся бы
        /// через раз — там, где его когда-то выбрали. Картинки видов чтения тем
        /// временем продолжают ездить с рукописью: вид, помеченный «в документе»,
        /// обязан доехать до получателя целиком.
        /// </summary>
        /// <summary>
        /// Язычок ленты редактора.
        ///
        /// В фокусе он поднимает ленту на время, не выходя из режима: человек берёт
        /// инструмент и возвращается к тексту. В обычной правке — сворачивает и
        /// разворачивает её насовсем, как язычок ленты чтения и как двойной щелчок
        /// по вкладке в Word.
        /// </summary>
        private void OnEditorRibbonTabClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not TextEditorViewModel vm) return;

            if (vm.IsFocusMode)
            {
                vm.IsFocusRibbonPeeking = !vm.IsFocusRibbonPeeking;
                return;
            }

            // Лента, вызванная наведением, при сворачивании должна уйти вместе со
            // всеми: иначе признак остаётся поднятым и лента возвращается сама.
            vm.IsFocusRibbonPeeking = false;
            vm.IsRibbonCollapsed = !vm.IsRibbonCollapsed;
        }

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

            // Окно «Табуляция» — тоже оверлей модуля: открывается из меню линейки.
            vm.TabSettingsRequested = ShowTabSettingsOverlay;

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

            // Лента: место в тексте — доля всей длины, и переход идёт по ней.
            vm.ReadingGoToPercentRequested = (percent, takeFocus) =>
                SpreadCanvas?.GoReadingPercent(percent, takeFocus);

            // Виды чтения — обычный оверлей модуля.
            vm.ReadingThemesRequested = ShowThemeOverlay;
            vm.ReadingThemeCreateRequested = ShowThemeOverlayWithNewTheme;

            // Картинка позади страниц выбирается файловым окном, а его знает
            // только вью: модель модуля про окна не осведомлена.

            // Слой фона под прокруткой перекладывает тоже вью — кисти и чтение
            // картинки в окно живут здесь.
            vm.BackdropLayerChanged = ApplyBackdropLayer;
            ApplyBackdropLayer();

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

            // Выход из фокуса по Esc. Первое нажатие при поднятой ленте убирает её
            // обратно, второе выходит из режима: человек, вызвавший ленту язычком,
            // ждёт от Esc отмены последнего действия, а не всего режима.
            canvas.FocusEscapePressed = () =>
            {
                if (vm.IsFocusRibbonPeeking)
                {
                    vm.IsFocusRibbonPeeking = false;
                    return;
                }

                vm.IsFocusMode = false;
            };

            // Смена разворота → подпись в ленте чтения.
            canvas.SpreadPageChanged = () =>
            {
                // Состояние снимается сейчас, а не в отложенном вызове: книга
                // объявляет о переходе в его начале, и к моменту доставки объявление
                // уже снято — лента получила бы страницу, с которой книга ушла.
                int page = canvas.SpreadPageNumber;
                int count = canvas.SpreadPageCount;
                double ms = canvas.SpreadTransitionMs;

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    vm.UpdateSpreadPageLabel(page, count, ms),
                    Avalonia.Threading.DispatcherPriority.Normal);
            };

            // Прокрутка ленты → доля прочитанного в поле и на ползунке.
            canvas.ReadingPercentChanged = percent =>
                vm.ReadingRibbon.SetPercentState(percent);

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
        private void ShowThemeOverlay() => ShowThemeOverlay(false);

        /// <summary>
        /// Показывает окно видов, сразу заведя новый вид копией выбранного. Просьба
        /// приходит кнопкой «Создать вид» — из ленты чтения и из вкладки «Вид»:
        /// заводится вид в обоих случаях одинаково.
        /// </summary>
        private void ShowThemeOverlayWithNewTheme() => ShowThemeOverlay(true);

        private async void ShowThemeOverlay(bool startWithNewTheme)
        {
            if (DataContext is not TextEditorViewModel vm) return;
            if (vm.Reading is not { } reading) return;

            var overlay = this.FindControl<ReadingThemeOverlay>("ThemeOverlay");
            if (overlay is null) return;

            // Окно ничего не копит: каждая правка приходит сюда по ходу и сохраняется
            // сразу. Закрытие — хоть кнопкой, хоть крестиком — ничего не отменяет.
            overlay.ApplyRequested = state =>
            {
                vm.SaveReadingThemes(state.Themes);

                // Выбранный в окне вид сразу становится рабочим: человек его только что
                // настраивал и ждёт увидеть книгу такой.
                reading.ApplyTheme(state.Selected);
                vm.ReadingRibbon.RefreshAll();
                vm.ApplyReadingLayout();
            };

            try
            {
                var result = await overlay.ShowAsync(vm.ReadingThemes(), reading.ThemeId, startWithNewTheme);
                if (result is null) return;

                vm.SaveReadingThemes(result.Themes);
                reading.ApplyTheme(result.Selected);
                vm.ReadingRibbon.RefreshAll();
                vm.ApplyReadingLayout();
            }
            finally
            {
                overlay.ApplyRequested = null;
            }
        }

        /// <summary>
        /// Показывает окно полной статистики документа. Величины берутся из строки
        /// состояния: слова и знаки она пересчитывает по тексту перед открытием, а
        /// страницы и строки держит от последней пересборки раскладки.
        /// </summary>
        /// <summary>
        /// Показывает окно «Табуляция» на позициях абзаца под кареткой. Единицы берутся
        /// у линейки: окно открывается из неё, и мерить в двух местах по-разному нельзя.
        /// </summary>
        private void ShowTabSettingsOverlay()
        {
            if (DataContext is not TextEditorViewModel vm) return;
            if (vm.DocumentViewModel is null) return;

            var overlay = this.FindControl<TabSettingsOverlay>("TabsOverlay");
            if (overlay is null) return;

            overlay.Applied = (stops, stepPt) => vm.ApplyTabSettings(stops, stepPt);
            overlay.Show(
                vm.DocumentViewModel.GetActiveTabStops(),
                vm.DocumentViewModel.Document.DefaultTabStopPt,
                vm.Ruler.Units);
        }

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
