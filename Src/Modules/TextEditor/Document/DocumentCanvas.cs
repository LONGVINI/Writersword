using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using Serilog;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Writersword.Core.Interfaces.Services.Input;
using Writersword.Core.Models.Print;
using Writersword.Core.Models.Rendering;
using System.Text.Json;
using Writersword.Infrastructure.Rendering;
using Writersword.Modules.Common;
using Writersword.Modules.TextEditor.Commands;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.ViewModels;
using Writersword.Modules.TextEditor.ViewModels.Blocks;

namespace Writersword.Modules.TextEditor.Document
{
    public sealed partial class DocumentCanvas : Control
    {
        // ── Конвертация единиц ────────────────────────────────────────────
        private const float PtToPx = 96f / 72f;
        private const float PxToPt = 72f / 96f;

        // ── Константы геометрии ───────────────────────────────────────────
        private const float PageGapPt = 15f;
        private const float DraftPadHPt = 9f;
        private const float DraftPadWPt = 0f;
        private const float ReadingMaxPt = 510f;
        private const float FallbackLinePt = 16.5f;

        // Отступ каретки якоря от границы таблицы — чтобы не перекрывалась рамкой.
        private const float AnchorMarginPt = 4f;

        // Дополнительный отступ сверху для строк параграфа, продолжающегося на новой странице.
        // Добавляется к lineGroupYPt при переносе — чтобы первая строка не прилипала к полю.
        private const float PageContinuationTopPadPt = 4f;

        // ── CellInfo: metadata для параграфа ячейки таблицы ──────────────
        // Таблица — это просто "параграфы в тюрьме": параграфы ячеек
        // добавляются в _layouts рядом с обычными параграфами. Каретка,
        // выделение и навигация работают через единый _layouts без
        // отдельного "режима таблицы".
        private sealed class CellInfo
        {
            public TableBlock Table { get; }
            public TableCell Cell { get; }
            public ParagraphBlock ParaBlock { get; }
            public int CellParaIndex { get; }  // индекс внутри cell.Paragraphs
            public int TableEntryIdx { get; }  // индекс в _tables
            public float ContentXPt { get; }  // абсолютный X начала содержимого
            public float ContentYPt { get; }  // абсолютный Y начала содержимого
            public float ClipX { get; }  // clip rect для рендера
            public float ClipY { get; }
            public float ClipW { get; }
            public float ClipH { get; }

            public CellInfo(TableBlock table, TableCell cell, ParagraphBlock paraBlock,
                int cellParaIndex, int tableEntryIdx,
                float contentXPt, float contentYPt,
                float clipX, float clipY, float clipW, float clipH)
            {
                Table = table; Cell = cell; ParaBlock = paraBlock;
                CellParaIndex = cellParaIndex; TableEntryIdx = tableEntryIdx;
                ContentXPt = contentXPt; ContentYPt = contentYPt;
                ClipX = clipX; ClipY = clipY; ClipW = clipW; ClipH = clipH;
            }
        }

        // ── Layout параграфов ─────────────────────────────────────────────
        private record ParaLayout(
            ParagraphViewModel Vm,
            SKTextLayout Layout,
            float Ypt,
            float HeightPt,
            int PageIndex,
            int LineFrom,
            int LineTo,
            float AbsXPt = 0,          // абсолютный X левого края текстовой зоны
            CellInfo? Cell = null);    // null = обычный параграф

        private record PageRect(
            float Ypt,
            float WidthPt,
            float HeightPt,
            float PadLeftPt,
            float PadTopPt,
            float MarginLeftPt,
            float PadBottomPt = 0f);

        // ── Layout таблиц (только для рендера рамок/фона) ─────────────────
        // Одна запись = один слайс таблицы на одной странице.
        // При разбивке таблицы по строкам создаётся несколько записей с одним Layout.
        private record TableEntry(
            TableBlock Table,
            SKTableLayout Layout,
            float Ypt,
            float XPt,
            int PageIndex,
            int RowFrom = 0,
            int RowTo = -1,
            float LastRowVisibleHeightPt = -1f,
            float FirstRowContentOffsetPt = 0f,
            bool IsContinuation = false);

        // ── Атомарный снимок для render-потока ────────────────────────────
        private readonly object _renderLock = new();
        private List<ParaLayout> _layouts = new();
        private List<PageRect> _pages = new();
        private List<TableEntry> _tables = new();
        private double _canvasWidth;
        private double _canvasHeight;
        private float _canvasHeightPt;

        // ── Кеш лейаутов обычных параграфов ──────────────────────────────
        private readonly Dictionary<ParagraphViewModel,
            (string Text, float Width, SKTextLayout Layout)> _layoutCache = new();

        // ── Кеш VM-обёрток и лейаутов для параграфов ячеек ───────────────
        // Ключ — ParagraphBlock (живёт в TableCell.Paragraphs).
        // VM-обёртки переиспользуются между rebuild'ами → SnapCaretToCorrectSlice
        // находит нужный слайс через Vm == targetVm (ссылка стабильна).
        private readonly Dictionary<ParagraphBlock, ParagraphViewModel> _cellVmCache = new();
        private readonly Dictionary<ParagraphBlock,
            (string Text, float Width, SKTextLayout Layout)> _cellLayoutCache = new();

        // ── Дебаунс пересчёта ─────────────────────────────────────────────
        private System.Threading.CancellationTokenSource _rebuildCts = new();

        // ── Виртуализация ─────────────────────────────────────────────────
        private ScrollViewer? _parentScrollViewer;
        private double _scrollOffsetY = 0;
        private double _viewportHeight = 600;

        // ── Каретка ───────────────────────────────────────────────────────
        // Единая для всего документа включая ячейки таблицы.
        private int _caretPara = 0;
        private int _caretChar = 0;
        private int _caretLineHint = -1;
        private bool _caretVisible = true;
        private float _preferredCaretXPt = 0f;
        private readonly DispatcherTimer _caretTimer;

        // ── Анимация скролла ──────────────────────────────────────────────
        private DispatcherTimer? _scrollAnimTimer;
        private double _scrollAnimFrom;
        private double _scrollAnimTo;
        private double _scrollAnimElapsedMs;
        private const double ScrollAnimDurationMs = 130.0;
        private const double ScrollAnimTickMs = 8.0;

        // ── Активная таблица (для структурных операций AddRow и т.д.) ────
        private TableBlock? _activeTableBlock;
        private int _activeCellRow = 0;
        private int _activeCellCol = 0;
        private int _activeCellTableEntryIdx = -1;

        // ── Drag ручек таблицы (без использования линейки) ───────────────
        private enum TableDragMode { None, ColResize, TableMove, RowResize }
        private TableDragMode _tableDragMode = TableDragMode.None;
        private int _tableDragColIndex = -1;    // индекс колонки при ColResize
        private int _tableDragEntryIdx = -1;    // индекс TableEntry
        private float _tableDragStartXPt = 0f;    // X мыши при начале drag в pt
        private float _tableDragStartVal = 0f;    // исходная ширина колонки или LeftIndentPt в pt

        // Размер hit-зоны ручки в pt (~5px при 100% zoom)
        private const float TableHandleHitPt = 5f * PxToPt;

        // ── Выделение ─────────────────────────────────────────────────────
        private int _selStartPara = 0;
        private int _selStartChar = 0;
        private int _selEndPara = 0;
        private int _selEndChar = 0;
        private bool _isSelecting;

        // ── Выделение нескольких ячеек ────────────────────────────────────
        // Единый словарь: TableBlock → (startRow, startCol, endRow, endCol).
        // Обновляется при движении курсора, очищается при новом клике.
        private bool _isCellRangeSelecting = false;
        private TableBlock? _cellSelTable;

        // Ячейка, в которой было нажатие мыши (якорь cell-range выделения).
        // Хранится отдельно, т.к. для пустых ячеек без layout-записи HitTest
        // возвращает неправильный pi (ближайший по Y параграф другой строки).
        private TableBlock? _pressCellTable;
        private int _pressCellRow = -1;
        private int _pressCellCol = -1;
        private int _cellSelStartRow = -1;
        private int _cellSelStartCol = -1;
        private int _cellSelEndRow = -1;
        private int _cellSelEndCol = -1;

        private readonly Dictionary<TableBlock, (int sr, int sc, int er, int ec)> _tableSelections = new();

        private sealed record FrozenTableSelection(
            TableBlock Table,
            int StartRow, int StartCol,
            int EndRow, int EndCol);

        // ── Bitmap-кеш для мигания каретки ────────────────────────────────
        private readonly object _bitmapLock = new();
        private SKBitmap? _lastFullRenderBitmap;
        private int _lastFullRenderWidth;
        private int _lastFullRenderHeight;
        // Бitmaps ожидающие освобождения — не диспозим сразу чтобы избежать
        // race condition когда рендер-тред ещё использует bitmap который UI-тред заменил.
        private readonly System.Collections.Concurrent.ConcurrentQueue<SKBitmap> _bitmapDisposeQueue = new();
        private bool _caretOnlyRedraw = false;

        // ── Буфер обмена ─────────────────────────────────────────────────
        private string? _clipboardCache;

        // Внутренний буфер: JSON-массив ClipboardBlock (параграфы + таблицы в порядке документа).
        // Заполняется при Copy, используется при Paste для точного воспроизведения структуры.
        private string? _internalClipboardJson;

        private enum ClipboardBlockKind { Paragraph, Table }
        private sealed class ClipboardBlock
        {
            public ClipboardBlockKind Kind { get; set; }
            public string? Text { get; set; }           // plain-text для Paragraph (fallback)
            public ParagraphBlock? Block { get; set; }  // полная модель параграфа (стили + runs)
            public TableBlock? Table { get; set; }      // для Table (уже слайснутая)
        }

        // ── Рендеринг ─────────────────────────────────────────────────────
        private readonly SKTextRenderer _renderer = new();
        private StyleResolver? _styleResolver;

        // ── Логирование ───────────────────────────────────────────────────
        private static readonly ILogger _logger = Log.ForContext<DocumentCanvas>();

        // ── HotKey ───────────────────────────────────────────────────────
        private IHotKeyService? _hotKeyService;

        // ── Undo ─────────────────────────────────────────────────────────
        public UndoRedoStack? UndoStack { get; set; }

        private double _monitorSizeInches = 0;
        private double _cachedDpi = 96.0;
        private DocumentSnapshotCommand? _pendingSnapshot;

        // ── Цвета ─────────────────────────────────────────────────────────
        private static readonly SKColor SelectionColor = new(0x33, 0x90, 0xFF, 0x60);
        private static readonly SKColor CanvasBgColor = new(0xE8, 0xE8, 0xE8);
        private static readonly SKColor PageShadowColor = new(0x00, 0x00, 0x00, 0x28);

        private DocumentViewModel? _docVm;
        private DocumentViewModel? DocVm => _docVm;
        private double Zoom => DocVm?.Zoom ?? 1.0;

        // Блок-якорь на который нужно переместить каретку после ближайшего rebuild.
        // Устанавливается при вставке разрыва страницы, потребляется в ScheduleRebuild.
        private ParagraphBlock? _pendingFocusBlock;

        // ── Callbacks ────────────────────────────────────────────────────
        public Action<double>? RecommendedZoomChanged { get; set; }

        private double _lastPageOffsetXPx = 0;
        private Action<double>? _pageOffsetXChanged;
        public Action<double>? PageOffsetXChanged
        {
            get => _pageOffsetXChanged;
            set { _pageOffsetXChanged = value; value?.Invoke(_lastPageOffsetXPx); }
        }

        public Action<IReadOnlyList<double>, IReadOnlyList<double>, double, int>? CaretEnteredTable { get; set; }
        public Action? CaretLeftTable { get; set; }

        /// <summary>
        /// Вызывается когда каретка перемещается на другую страницу.
        /// Вертикальная линейка отображает шкалу только для этой страницы.
        /// </summary>
        public Action<int>? CaretPageChanged { get; set; }

        public Action<int, int, double>? CaretStateChanged { get; set; }

        public double MonitorSizeInches
        {
            get => _monitorSizeInches;
            set
            {
                if (Math.Abs(_monitorSizeInches - value) < 0.01) return;
                _monitorSizeInches = value;
                RebuildDpiCache();
                InvalidateMeasure();
            }
        }

        public DocumentCanvas()
        {
            Focusable = true;
            Cursor = new Cursor(StandardCursorType.Ibeam);

            _caretTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
            _caretTimer.Tick += (_, _) =>
            {
                _caretVisible = !_caretVisible;
                _caretOnlyRedraw = true;
                InvalidateVisual();
            };
            _caretTimer.Start();
            GotFocus += OnGotFocusHandler;
        }

        // ── HotKey ───────────────────────────────────────────────────────
        public void SetHotKeyService(IHotKeyService service) => _hotKeyService = service;

        // ── DPI ───────────────────────────────────────────────────────────
        private void RebuildDpiCache()
        {
            if (_monitorSizeInches <= 0)
            {
                _cachedDpi = 96.0;
                Dispatcher.UIThread.Post(() => RecommendedZoomChanged?.Invoke(RecommendedZoom));
                return;
            }
            var topLevel = TopLevel.GetTopLevel(this);
            var screen = topLevel?.Screens?.ScreenFromVisual(this);
            if (screen is null) return;
            double physW = screen.Bounds.Width * screen.Scaling;
            double physH = screen.Bounds.Height * screen.Scaling;
            double diagPx = Math.Sqrt(physW * physW + physH * physH);
            _cachedDpi = diagPx / _monitorSizeInches;
            Dispatcher.UIThread.Post(() => RecommendedZoomChanged?.Invoke(RecommendedZoom));
        }

        public double RecommendedZoom => _cachedDpi > 0 ? _cachedDpi / 96.0 : 1.0;

        private static float MmToPt(double mm) => (float)(mm * 72.0 / 25.4);
        private static double PtToMm(float pt) => pt * 25.4 / 72.0;

        private float GetPageWidthPt()
        {
            var ps = DocVm?.Document.PageSettings;
            if (ps is null) return MmToPt(210);
            return ps.Orientation == PageOrientation.Landscape ? MmToPt(ps.HeightMm) : MmToPt(ps.WidthMm);
        }
        private float GetPageHeightPt()
        {
            var ps = DocVm?.Document.PageSettings;
            if (ps is null) return MmToPt(297);
            return ps.Orientation == PageOrientation.Landscape ? MmToPt(ps.WidthMm) : MmToPt(ps.HeightMm);
        }
        private (float left, float top, float right, float bottom) GetPagePaddingPt()
        {
            var ps = DocVm?.Document.PageSettings;
            if (ps is null) return (MmToPt(20), MmToPt(20), MmToPt(20), MmToPt(20));
            return (MmToPt(ps.MarginLeftMm + ps.MarginGutterMm), MmToPt(ps.MarginTopMm),
                    MmToPt(ps.MarginRightMm), MmToPt(ps.MarginBottomMm));
        }

        // ── DataContext / ScrollViewer ────────────────────────────────────
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            RebuildDpiCache();
            SubscribeToScrollViewer();
            _ = PrefetchClipboardAsync();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            UnsubscribeFromScrollViewer();
            lock (_bitmapLock)
            {
                if (_lastFullRenderBitmap is not null)
                {
                    _bitmapDisposeQueue.Enqueue(_lastFullRenderBitmap);
                    _lastFullRenderBitmap = null;
                }
            }
        }

        private void OnGotFocusHandler(object? sender, Avalonia.Input.FocusChangedEventArgs e)
        {
            _ = PrefetchClipboardAsync();
        }

        private async Task PrefetchClipboardAsync()
        {
            try
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard is null) return;
#pragma warning disable CS0618
                _clipboardCache = await clipboard.TryGetTextAsync();
#pragma warning restore CS0618
            }
            catch { }
        }

        private void SubscribeToScrollViewer()
        {
            StyledElement? parent = Parent;
            while (parent is not null)
            {
                if (parent is ScrollViewer sv)
                {
                    _parentScrollViewer = sv;
                    sv.ScrollChanged += OnScrollChanged;
                    sv.PropertyChanged += OnScrollViewerPropertyChanged;
                    _scrollOffsetY = sv.Offset.Y;
                    _viewportHeight = sv.Viewport.Height;
                    break;
                }
                parent = parent.Parent;
            }
        }

        private void OnViewportSizeChanged()
        {
            if (_parentScrollViewer is null) return;
            _viewportHeight = _parentScrollViewer.Viewport.Height;
            // Принудительно пересчитываем layout — viewport мог измениться
            // из-за закрытия/открытия панели dock, страница должна перецентроваться.
            InvalidateMeasure();
        }

        private void OnScrollViewerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == ScrollViewer.ViewportProperty)
                OnViewportSizeChanged();
        }

        private void UnsubscribeFromScrollViewer()
        {
            if (_parentScrollViewer is null) return;
            _parentScrollViewer.ScrollChanged -= OnScrollChanged;
            _parentScrollViewer.PropertyChanged -= OnScrollViewerPropertyChanged;
            _parentScrollViewer = null;
        }

        private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            if (sender is not ScrollViewer sv) return;
            _scrollOffsetY = sv.Offset.Y;
            _viewportHeight = sv.Viewport.Height;
            InvalidateFull();
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);

            if (_docVm is not null)
            {
                _docVm.Paragraphs.CollectionChanged -= OnParagraphsChanged;
                _docVm.PropertyChanged -= OnDocVmPropertyChanged;
                _docVm.ParagraphFormatChanged -= OnParagraphFormatChanged;
                _docVm.OnPageBreakInserted = null;
            }

            _docVm = DataContext as DocumentViewModel;
            _layoutCache.Clear();
            _cellVmCache.Clear();
            _cellLayoutCache.Clear();

            if (DocVm is not null)
            {
                _styleResolver = new StyleResolver(DocVm.Document.Styles);
                _lastZoom = DocVm.Zoom;
                DocVm.Paragraphs.CollectionChanged += OnParagraphsChanged;
                DocVm.PropertyChanged += OnDocVmPropertyChanged;
                DocVm.ParagraphFormatChanged += OnParagraphFormatChanged;
                DocVm.OnPageBreakInserted = block => _pendingFocusBlock = block;
                DocVm.UndoDelegate = ExecuteUndo;
                DocVm.RedoDelegate = ExecuteRedo;
                DocVm.CutDelegate = ExecuteCut;
                DocVm.CopyDelegate = ExecuteCopy;
                DocVm.PasteDelegate = ExecutePaste;
                foreach (var pvm in DocVm.Paragraphs)
                    WirePvm(pvm);
            }

            InvalidateMeasure();
        }

        private void OnParagraphFormatChanged()
        {
            _layoutCache.Clear();
            _cellLayoutCache.Clear();
            RebuildLayouts();
            SnapCaretToCorrectSlice();
            UpdatePreferredX();

            // Если каретка в таблице — обновляем маркеры линейки.
            // Без этого после смены LeftIndentPt или ширины колонки линейка
            // показывает старые позиции и следующий drag считается от них.
            if (_activeTableBlock is not null)
                NotifyCaretEnteredTableCallback();

            InvalidateFull();
        }

        private double _lastZoom = 1.0;

        private void OnDocVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(DocumentViewModel.ViewMode)
                               or nameof(DocumentViewModel.Zoom)
                               or nameof(DocumentViewModel.PageSettings))
            {
                if (DocVm is not null)
                    _styleResolver = new StyleResolver(DocVm.Document.Styles);
                _layoutCache.Clear();
                _cellLayoutCache.Clear();
                RebuildLayouts();

                if (e.PropertyName == nameof(DocumentViewModel.Zoom)
                    && _parentScrollViewer is { } sv)
                {
                    double newZoom = Zoom;
                    if (Math.Abs(newZoom - _lastZoom) > 0.001)
                    {
                        double docOffsetPt = _lastZoom > 0
                            ? sv.Offset.Y / (_lastZoom * PtToPx) : 0;
                        _lastZoom = newZoom;
                        InvalidateMeasure();
                        Dispatcher.UIThread.Post(() =>
                        {
                            double newOffsetPx = docOffsetPt * newZoom * PtToPx;
                            sv.Offset = new Avalonia.Vector(sv.Offset.X, newOffsetPx);
                        }, Avalonia.Threading.DispatcherPriority.Loaded);
                        InvalidateFull();
                        return;
                    }
                }

                _lastZoom = Zoom;
                InvalidateMeasure();
                InvalidateFull();
            }
        }

        private void OnParagraphsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems is not null)
                foreach (ParagraphViewModel pvm in e.NewItems) WirePvm(pvm);

            if (e.OldItems is not null)
                foreach (ParagraphViewModel pvm in e.OldItems)
                {
                    pvm.PropertyChanged -= OnPvmPropertyChanged;
                    _layoutCache.Remove(pvm);
                }

            int dirtyIdx = 0;
            if (e.NewItems is not null && e.NewStartingIndex >= 0)
                dirtyIdx = e.NewStartingIndex;
            else if (e.OldItems is not null && e.OldStartingIndex >= 0)
                dirtyIdx = Math.Max(0, e.OldStartingIndex - 1);

            ScheduleRebuild(dirtyIdx);
        }

        private void WirePvm(ParagraphViewModel pvm)
        {
            pvm.PropertyChanged += OnPvmPropertyChanged;

            pvm.FocusRequested += () =>
            {
                if (DocVm is null) return;
                int idx = DocVm.Paragraphs.IndexOf(pvm);
                if (idx < 0) return;
                _caretPara = FindFirstSliceForDocVmParagraph(idx);
                _caretChar = pvm.PlainText?.Length ?? 0;
                NotifyLeftCell(); // выходим из ячейки
                SnapCaretToCorrectSlice();
                UpdatePreferredX();
                SyncSel(); ResetCaret(); InvalidateVisual();
            };

            pvm.RequestFocusAtPosition = pos =>
            {
                if (DocVm is null) return;
                int idx = DocVm.Paragraphs.IndexOf(pvm);
                if (idx < 0) return;
                _caretPara = FindFirstSliceForDocVmParagraph(idx);
                _caretChar = Clamp(pos, 0, pvm.PlainText?.Length ?? 0);
                NotifyLeftCell();
                SnapCaretToCorrectSlice();
                UpdatePreferredX();
                SyncSel(); ResetCaret(); InvalidateVisual();
            };
        }

        private void OnPvmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ParagraphViewModel.PlainText)) return;
            if (sender is ParagraphViewModel pvm && DocVm is not null)
            {
                int idx = DocVm.Paragraphs.IndexOf(pvm);
                if (idx >= 0) { ScheduleRebuild(idx); return; }
            }
            ScheduleRebuild(0);
        }

        // ── Дебаунс пересчёта ─────────────────────────────────────────────
        private void ScheduleRebuild(int dirtyParaIdx)
        {
            if (DocVm is not null && dirtyParaIdx < DocVm.Paragraphs.Count)
                _layoutCache.Remove(DocVm.Paragraphs[dirtyParaIdx]);

            _rebuildCts.Cancel();
            _rebuildCts = new System.Threading.CancellationTokenSource();
            var cts = _rebuildCts;

            InvalidateFull();

            Dispatcher.UIThread.Post(() =>
            {
                if (cts.IsCancellationRequested) return;

                double oldCanvasH = _canvasHeight;
                RebuildLayouts();
                SnapCaretToCorrectSlice(); // обновляет _caretLineHint по актуальному положению каретки

                // Если после предыдущего rebuild был запрошен переход к якорю разрыва —
                // применяем его сейчас, когда _layouts актуальны.
                if (_pendingFocusBlock is not null && DocVm is not null)
                {
                    var anchorVm = DocVm.Paragraphs.FirstOrDefault(p => p.Model == _pendingFocusBlock);
                    _pendingFocusBlock = null;
                    if (anchorVm is not null)
                    {
                        int pvmIdx = DocVm.Paragraphs.IndexOf(anchorVm);
                        _caretPara = FindFirstSliceForDocVmParagraph(pvmIdx);
                        _caretChar = 0;
                        NotifyLeftCell();
                        SnapCaretToCorrectSlice();
                        UpdatePreferredX();
                        SyncSel();
                        _caretVisible = true;
                        _caretTimer.Stop();
                        _caretTimer.Start();
                        if (_caretPara >= 0 && _caretPara < _layouts.Count)
                            CaretPageChanged?.Invoke(_layouts[_caretPara].PageIndex);
                        ScrollToCenterCaret();
                    }
                }

                if (Math.Abs(_canvasHeight - oldCanvasH) > 0.5)
                    InvalidateMeasure();
                else
                    InvalidateFull();

            }, DispatcherPriority.Background);
        }

        // ── Measure / Layout ──────────────────────────────────────────────
        protected override Size MeasureOverride(Size available)
        {
            double zoom = Zoom;
            double availW = double.IsInfinity(available.Width) ? 800 : Math.Max(available.Width, 1);
            double viewportW = _parentScrollViewer?.Viewport.Width > 0
                ? _parentScrollViewer.Viewport.Width : availW;
            _canvasWidth = Math.Max(viewportW / zoom, 1);

            if (_styleResolver is null && DocVm is not null)
                _styleResolver = new StyleResolver(DocVm.Document.Styles);

            _layoutCache.Clear();
            _cellLayoutCache.Clear();
            RebuildLayouts();

            double visualH = Math.Max(_canvasHeight * zoom, 100);
            double visualW = availW;

            if (DocVm?.ViewMode == EditorViewMode.Page)
                visualW = Math.Max(availW,
                    GetPageWidthPt() * PtToPx * zoom + PageGapPt * PtToPx * 4);

            return new Size(visualW, visualH);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double zoom = Zoom;
            double viewportW = _parentScrollViewer?.Viewport.Width > 0
                ? _parentScrollViewer.Viewport.Width : finalSize.Width;
            double logicalW = Math.Max(viewportW / zoom, 1);

            if (Math.Abs(logicalW - _canvasWidth) > 0.5)
            {
                _canvasWidth = logicalW;
                _layoutCache.Clear();
                _cellLayoutCache.Clear();
                RebuildLayouts();
            }

            return new Size(finalSize.Width, Math.Max(_canvasHeight * zoom, 100));
        }

        // ── Пересчёт лейаута ──────────────────────────────────────────────
        private void RebuildLayouts()
        {
            if (DocVm is null)
            {
                float emptyH = FallbackLinePt * 5f;
                lock (_renderLock)
                {
                    _layouts = new List<ParaLayout>();
                    _pages = new List<PageRect>();
                    _tables = new List<TableEntry>();
                    _canvasHeightPt = emptyH;
                    _canvasHeight = emptyH * PtToPx;
                }
                return;
            }

            if (_styleResolver is null)
                _styleResolver = new StyleResolver(DocVm.Document.Styles);

            switch (DocVm.ViewMode)
            {
                case EditorViewMode.Page:
                    RebuildPageMode();
                    break;
                case EditorViewMode.Draft:
                case EditorViewMode.Web:
                    RebuildFlowMode((float)(_canvasWidth * PxToPt), DraftPadHPt, DraftPadWPt);
                    break;
                case EditorViewMode.Reading:
                    {
                        float cw = (float)(_canvasWidth * PxToPt);
                        RebuildFlowMode(Math.Min(cw, ReadingMaxPt), 18f,
                            (cw - Math.Min(cw, ReadingMaxPt)) / 2f);
                        break;
                    }
            }
        }


        private SKTextLayout GetOrBuildLayout(ParagraphViewModel pvm, float widthPt)
        {
            string text = pvm.PlainText ?? string.Empty;
            if (_layoutCache.TryGetValue(pvm, out var cached)
                && cached.Text == text
                && Math.Abs(cached.Width - widthPt) < 0.1f)
                return cached.Layout;

            var layout = _renderer.BuildLayout(pvm.Model, widthPt, _styleResolver!);
            _layoutCache[pvm] = (text, widthPt, layout);
            return layout;
        }

        // ── ICustomDrawOperation ──────────────────────────────────────────
        private sealed class CanvasSKDrawOperation : ICustomDrawOperation
        {
            private readonly DocumentCanvas _canvas;
            public Rect Bounds { get; }

            public CanvasSKDrawOperation(DocumentCanvas canvas, Rect bounds)
            {
                _canvas = canvas;
                Bounds = bounds;
            }

            public void Dispose() { }
            public bool Equals(ICustomDrawOperation? other) => false;
            public bool HitTest(Point p) => true;

            public void Render(ImmediateDrawingContext context)
            {
                var feature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature))
                    as ISkiaSharpApiLeaseFeature;
                if (feature is null) return;
                using var lease = feature.Lease();
                _canvas.RenderWithSKCanvas(lease.SkCanvas);
            }
        }
    }
}