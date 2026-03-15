using Avalonia.Controls;
using ReactiveUI;
using Serilog;
using System;
using Writersword.Modules.Common;
using Writersword.Modules.TextEditor.ViewModels;
using Writersword.Modules.TextEditor.Views.Document;

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

        private void SyncCanvas(DocumentCanvas canvas)
        {
            if (DataContext is not TextEditorViewModel vm)
            {
                _logger.Debug("SyncCanvas: DataContext is not TextEditorViewModel");
                return;
            }

            // RecommendedZoomChanged must be assigned before MonitorSizeInches,
            // because the setter calls RebuildDpiCache() which fires the callback.
            canvas.RecommendedZoomChanged = recommendedZoom =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    vm.StatusBar.RecommendedZoom = recommendedZoom;
                    _logger.Debug("RecommendedZoom updated: {V}", recommendedZoom);
                }, Avalonia.Threading.DispatcherPriority.Background);
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