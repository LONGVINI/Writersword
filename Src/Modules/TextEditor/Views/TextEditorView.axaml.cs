using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using ReactiveUI;
using Serilog;
using System;
using Writersword.Modules.Common;
using Writersword.Modules.TextEditor.Document;
using Writersword.Modules.TextEditor.ViewModels;

namespace Writersword.Modules.TextEditor.Views
{
    public partial class TextEditorView : UserControl
    {
        private static readonly ILogger _logger = Log.ForContext<TextEditorView>();
        private readonly UndoRedoStack _undoStack;
        private IDisposable? _monitorSubscription;

        // Всплывающая подсказка номера страницы при перетаскивании ползунка.
        private bool _draggingScrollbar;
        private DocumentCanvas? _tooltipCanvas;
        private ScrollViewer? _tooltipScrollViewer;
        private StackPanel? _pageTooltip;
        private TextBlock? _pageTooltipText;

        public TextEditorView(UndoRedoStack undoStack)
        {
            _undoStack = undoStack;
            InitializeComponent();
            WireCanvas();
            WireScroll();
            WirePageTooltip();
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

                vm.Ruler.ScrollOffsetY = scrollViewer.Offset.Y;
                vm.Ruler.ViewportHeight = scrollViewer.Viewport.Height;

                scrollViewer.ScrollChanged += (_, _) =>
                {
                    vm.Ruler.ScrollOffsetY = scrollViewer.Offset.Y;
                    vm.Ruler.ViewportHeight = scrollViewer.Viewport.Height;
                };
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

            // Выделение/снятие картинки → контекстная вкладка «Формат».
            canvas.ImageSelectionChanged = selected =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    vm.NotifyImageSelectionChanged(selected),
                    Avalonia.Threading.DispatcherPriority.Background);
            };

            // Страница каретки → вертикальная линейка.
            // Вертикальная линейка использует FocusedPageIndex чтобы отображать
            // шкалу только для страницы где стоит каретка, как в Word.
            canvas.CaretPageChanged = pageIndex =>
            {
                vm.Ruler.FocusedPageIndex = pageIndex;
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
    }
}