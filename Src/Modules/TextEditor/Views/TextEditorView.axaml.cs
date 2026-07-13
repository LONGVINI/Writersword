using Avalonia.Controls;
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

        public TextEditorView(UndoRedoStack undoStack)
        {
            _undoStack = undoStack;
            InitializeComponent();
            WireCanvas();
            WireScroll();
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