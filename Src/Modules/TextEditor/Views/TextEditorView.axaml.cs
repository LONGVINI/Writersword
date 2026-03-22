using Avalonia.Controls;
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
            // Подписываемся на DataContextChanged — к этому моменту
            // visual tree уже построен и ScrollViewer точно найдётся.
            DataContextChanged += (_, _) =>
            {
                if (DataContext is not TextEditorViewModel vm) return;

                var scrollViewer = this.FindControl<ScrollViewer>("DocumentScrollViewer");
                if (scrollViewer is null) return;

                // Устанавливаем начальные значения.
                vm.Ruler.ScrollOffsetY = scrollViewer.Offset.Y;
                vm.Ruler.ViewportHeight = scrollViewer.Viewport.Height;

                // Подписываемся на скролл.
                scrollViewer.ScrollChanged += (_, _) =>
                {
                    vm.Ruler.ScrollOffsetY = scrollViewer.Offset.Y;
                    vm.Ruler.ViewportHeight = scrollViewer.Viewport.Height;

                    // ВРЕМЕННО
                    System.Diagnostics.Debug.WriteLine(
                        $"Scroll: Y={scrollViewer.Offset.Y:F1} " +
                        $"ViewportH={scrollViewer.Viewport.Height:F1}");
                };
            };
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
            canvas.CaretEnteredTable = (offsets, widths) =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    vm.NotifyCaretEnteredTable(offsets, widths),
                    Avalonia.Threading.DispatcherPriority.Background);
            };

            canvas.CaretLeftTable = () =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    vm.NotifyCaretLeftTable(),
                    Avalonia.Threading.DispatcherPriority.Background);
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