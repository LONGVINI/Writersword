using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
using Writersword.Infrastructure.Rendering;
using Writersword.Modules.Common;
using Writersword.Modules.TextEditor.Commands;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.ViewModels;
using Writersword.Modules.TextEditor.ViewModels.Blocks;

namespace Writersword.Modules.TextEditor.Document
{
    public sealed class DocumentCanvas : Control
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

        // ── Bitmap-кеш для мигания каретки ────────────────────────────────
        private SKBitmap? _lastFullRenderBitmap;
        private int _lastFullRenderWidth;
        private int _lastFullRenderHeight;
        private bool _caretOnlyRedraw = false;

        // ── Буфер обмена ─────────────────────────────────────────────────
        private string? _clipboardCache;

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
            _lastFullRenderBitmap?.Dispose();
            _lastFullRenderBitmap = null;
        }

        protected override void OnGotFocus(GotFocusEventArgs e)
        {
            base.OnGotFocus(e);
            _ = PrefetchClipboardAsync();
        }

        private async Task PrefetchClipboardAsync()
        {
            try
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard is null) return;
#pragma warning disable CS0618
                _clipboardCache = await clipboard.GetTextAsync();
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
                    _scrollOffsetY = sv.Offset.Y;
                    _viewportHeight = sv.Viewport.Height;
                    break;
                }
                parent = parent.Parent;
            }
        }

        private void UnsubscribeFromScrollViewer()
        {
            if (_parentScrollViewer is null) return;
            _parentScrollViewer.ScrollChanged -= OnScrollChanged;
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
                SnapCaretToCorrectSlice();
                _caretLineHint = -1;

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

        // ── Добавление параграфов ячейки в _layouts ───────────────────────

        /// <param name="rowFrom">Первая строка слайса (включительно).</param>
        /// <param name="rowTo">Последняя строка слайса (не включительно). -1 = до конца.</param>
        /// <param name="firstRowOffset">Смещение контента первой строки (ByCell).</param>
        /// <param name="lastRowVisibleH">Видимая высота последней строки (ByCell). -1 = целая.</param>
        private void AddCellParasToLayouts(
            List<ParaLayout> newLayouts,
            TableBlock tableBlock,
            SKTableLayout tableLayout,
            int tableEntryIdx,
            float tableXPt,
            float tableYPt,
            int pageIdx,
            int rowFrom = 0,
            int rowTo = -1,
            float firstRowOffset = 0f,
            float lastRowVisibleH = -1f)
        {
            int effectiveRowTo = rowTo < 0 ? tableLayout.Rows.Count : rowTo;
            float rowOffsetY = rowFrom > 0 && rowFrom < tableLayout.Rows.Count
                ? tableLayout.Rows[rowFrom].Ypt : 0f;

            _logger.Debug(
                "[CELLS] tableYPt={TY:F1} rowFrom={RF} rowTo={RT} effectiveRowTo={ERT} rowOffsetY={ROY:F1} firstRowOffset={FRO:F1} lastRowVisH={LRV:F1}",
                tableYPt, rowFrom, rowTo, effectiveRowTo, rowOffsetY, firstRowOffset, lastRowVisibleH);

            foreach (var rowLayout in tableLayout.Rows)
            {
                if (rowLayout.Row < rowFrom || rowLayout.Row >= effectiveRowTo) continue;

                bool isLastRow = rowLayout.Row == effectiveRowTo - 1;
                bool isByCellSplit = isLastRow && lastRowVisibleH >= 0f;
                bool isContinuationFirstRow = rowLayout.Row == rowFrom && firstRowOffset > 0f;

                foreach (var cellLayout in rowLayout.Cells)
                {
                    if (cellLayout.Row != rowLayout.Row) continue; // пропускаем дубли объединённых ячеек

                    _logger.Debug(
                        "[CELLS] row={R} col={C} isCont={IC} isSplit={IS} cellYpt={CYT:F1} rowOffsetY={ROY:F1}",
                        rowLayout.Row, cellLayout.Column,
                        isContinuationFirstRow, isByCellSplit, cellLayout.Ypt, rowOffsetY);

                    float cellContentX = tableXPt + cellLayout.Xpt
                        + cellLayout.PadLeftPt + cellLayout.Borders.Left.WidthPt;

                    // Базовая Y ячейки относительно начала этого слайса на странице.
                    float cellBaseY = tableYPt + cellLayout.Ypt - rowOffsetY;

                    // Для первой строки ByCell-продолжения сдвигаем текст вверх:
                    // невидимая часть уезжает выше tableYPt и будет отсечена clipY.
                    float cellContentY = cellBaseY - firstRowOffset
                        + cellLayout.PadTopPt + cellLayout.Borders.Top.WidthPt;

                    float clipX = tableXPt + cellLayout.Xpt + cellLayout.Borders.Left.WidthPt;
                    float clipW = cellLayout.WidthPt
                        - cellLayout.Borders.Left.WidthPt - cellLayout.Borders.Right.WidthPt;

                    float clipY;
                    float clipH;

                    if (isContinuationFirstRow)
                    {
                        // Продолжение ByCell: видимая область начинается прямо от tableYPt.
                        // Высота клипа = оставшаяся высота строки на этой странице.
                        // Если строка ещё и разрывается снизу (средняя страница при 3+ разрывах),
                        // lastRowVisibleH уже выражает высоту видимого окна — вычитать firstRowOffset не нужно.
                        float remaining = isByCellSplit
                            ? lastRowVisibleH
                            : rowLayout.HeightPt - firstRowOffset;
                        clipY = tableYPt + cellLayout.Borders.Top.WidthPt;
                        clipH = Math.Max(0f, remaining
                            - cellLayout.Borders.Top.WidthPt - cellLayout.Borders.Bottom.WidthPt);
                    }
                    else if (isByCellSplit)
                    {
                        // Последняя разорванная строка: ограничиваем снизу.
                        clipY = cellBaseY + cellLayout.Borders.Top.WidthPt;
                        clipH = Math.Max(0f, lastRowVisibleH
                            - cellLayout.Borders.Top.WidthPt - cellLayout.Borders.Bottom.WidthPt);
                    }
                    else
                    {
                        // Обычная строка (в т.ч. merged cells): полная высота ячейки.
                        clipY = cellBaseY + cellLayout.Borders.Top.WidthPt;
                        clipH = Math.Max(0f, cellLayout.HeightPt
                            - cellLayout.Borders.Top.WidthPt - cellLayout.Borders.Bottom.WidthPt);
                    }

                    // Получаем оригинальную ячейку модели
                    var modelCell = tableBlock.GetCell(cellLayout.Row, cellLayout.Column);
                    if (modelCell is null) continue;

                    for (int pi = 0; pi < cellLayout.Paragraphs.Count; pi++)
                    {
                        var cellPara = cellLayout.Paragraphs[pi];
                        var paraBlock = (pi < modelCell.Paragraphs.Count)
                            ? modelCell.Paragraphs[pi]
                            : null;
                        if (paraBlock is null) continue;

                        // Стабильный VM — переиспользуется между rebuild'ами
                        if (!_cellVmCache.TryGetValue(paraBlock, out var vm))
                        {
                            vm = new ParagraphViewModel(paraBlock);
                            _cellVmCache[paraBlock] = vm;
                        }

                        var info = new CellInfo(
                                tableBlock, modelCell, paraBlock, pi, tableEntryIdx,
                                cellContentX, cellContentY,
                                clipX, clipY, clipW, clipH);

                        // Вертикальное выравнивание — копируем из SKTableLayout
                        float contentAreaH = cellLayout.HeightPt
                            - cellLayout.PadTopPt - cellLayout.PadBottomPt
                            - cellLayout.Borders.Top.WidthPt - cellLayout.Borders.Bottom.WidthPt;
                        float contentOffsetY = cellLayout.VerticalAlignment switch
                        {
                            1 => Math.Max(0f, (contentAreaH - cellLayout.ContentHeightPt) / 2f),
                            2 => Math.Max(0f, contentAreaH - cellLayout.ContentHeightPt),
                            _ => 0f
                        };

                        float absParaY = cellContentY
                            + contentOffsetY
                            + cellPara.Ypt
                            + cellPara.Layout.SpaceBeforePt;

                        _logger.Debug(
                            "[CELLS]   pi={PI} cellContentY={CCY:F1} clipY={CY:F1} clipH={CH:F1} absParaY={APY:F1}",
                            pi, cellContentY, clipY, clipH, absParaY);

                        // Последний параграф ячейки растягивается до нижнего края клип-прямоугольника.
                        // Без этого у нижней части пустой ячейки Y-расстояние > 0 для всех параграфов
                        // строки таблицы, и HitTest может выбрать параграф из соседней ячейки.
                        bool isLastInCell = (pi == cellLayout.Paragraphs.Count - 1);
                        float cellBottom = clipY + clipH;
                        float paraHeight = isLastInCell
                            ? Math.Max(cellPara.Layout.TotalHeightPt, cellBottom - absParaY)
                            : cellPara.Layout.TotalHeightPt;

                        newLayouts.Add(new ParaLayout(
                            vm,
                            cellPara.Layout,
                            absParaY,
                            paraHeight,
                            pageIdx,
                            0,
                            cellPara.Layout.Lines.Count,
                            AbsXPt: cellContentX,
                            Cell: info));
                    }
                }
            }
        }

        private void RebuildPageMode()
        {
            float pageWidthPt = GetPageWidthPt();
            float pageHeightPt = GetPageHeightPt();
            var (ml, mt, mr, mb) = GetPagePaddingPt();
            float textWidthPt = Math.Max(pageWidthPt - ml - mr, 1f);
            float canvasWPt = (float)(_canvasWidth * PxToPt);
            float pageXPt = Math.Max((canvasWPt - pageWidthPt) / 2f, 0f);
            float textXPt = pageXPt + ml;

            float pageYPt = PageGapPt;
            float pageBottomPt = pageYPt + pageHeightPt - mb;
            float contentYPt = pageYPt + mt;
            int pageIdx = 0;

            var newLayouts = new List<ParaLayout>();
            var newPages = new List<PageRect>();
            var newTables = new List<TableEntry>();

            newPages.Add(new PageRect(pageYPt, pageWidthPt, pageHeightPt, pageXPt, mt, ml, mb));

            float pageOffsetXPx = pageXPt * PtToPx * (float)Zoom
                - (float)(_parentScrollViewer?.Offset.X ?? 0);
            _lastPageOffsetXPx = pageOffsetXPx;
            PageOffsetXChanged?.Invoke(pageOffsetXPx);

            foreach (var block in DocVm!.Document.Sections[0].Blocks)
            {
                if (block is BreakBlock bb && bb.BreakType == BreakType.Page)
                {
                    pageYPt = pageYPt + pageHeightPt + PageGapPt;
                    pageBottomPt = pageYPt + pageHeightPt - mb;
                    contentYPt = pageYPt + mt;
                    pageIdx++;
                    newPages.Add(new PageRect(pageYPt, pageWidthPt, pageHeightPt, pageXPt, mt, ml, mb));
                    continue;
                }

                if (block is TableBlock tableBlock)
                {
                    var tableLayout = _renderer.BuildTableLayout(tableBlock, textWidthPt, _styleResolver!);
                    float tableXPt = textXPt + (float)tableBlock.LeftIndentPt;
                    bool byCell = tableBlock.SplitMode == TableSplitMode.ByCell;
                    // Высота полной текстовой зоны страницы — защита от бесконечного цикла
                    // при ByCell когда строка выше чем вся страница.
                    float fullPageH = pageHeightPt - mt - mb;

                    float sliceFirstRowOffset = 0f;
                    // Offset первой строки текущего слайса — сохраняется при старте слайса,
                    // не обнуляется в else-ветке (в отличие от sliceFirstRowOffset).
                    float sliceStartOffset = 0f;
                    int rowFrom = 0;
                    float sliceStartY = contentYPt;
                    bool isFirstSlice = true;

                    _logger.Debug(
                        "[TBL] START rows={R} totalH={H:F1} contentY={CY:F1} pageBottom={PB:F1} pageIdx={PI} byCell={BC}",
                        tableLayout.Rows.Count, tableLayout.TotalHeightPt,
                        contentYPt, pageBottomPt, pageIdx, byCell);

                    for (int ri = 0; ri < tableLayout.Rows.Count; ri++)
                    {
                        var row = tableLayout.Rows[ri];
                        float effectiveH = row.HeightPt - sliceFirstRowOffset;

                        float available = pageBottomPt - contentYPt;
                        bool atPageTop = contentYPt <= pageYPt + mt + 0.5f;

                        _logger.Debug(
                            "[TBL] row={RI} rowH={RH:F1} offset={OF:F1} effectiveH={EH:F1} " +
                            "available={AV:F1} atPageTop={AT} contentY={CY:F1} pageBottom={PB:F1}",
                            ri, row.HeightPt, sliceFirstRowOffset, effectiveH,
                            available, atPageTop, contentYPt, pageBottomPt);

                        if (effectiveH > available && !atPageTop)
                        {
                            if (byCell && available > 5f && effectiveH <= fullPageH)
                            {
                                // ByCell: видим часть строки ri, остаток на следующей странице
                                float visibleH = available;
                                float nextOffset = sliceFirstRowOffset + visibleH;

                                _logger.Debug(
                                    "[TBL] BYCELL-SPLIT ri={RI} sliceStartY={SSY:F1} rowFrom={RF} rowTo={RT} " +
                                    "visibleH={VH:F1} nextOffset={NO:F1} pageIdx={PI}",
                                    ri, sliceStartY, rowFrom, ri + 1, visibleH, nextOffset, pageIdx);

                                int teIdx = newTables.Count;
                                newTables.Add(new TableEntry(tableBlock, tableLayout,
                                    sliceStartY, tableXPt, pageIdx,
                                    RowFrom: rowFrom, RowTo: ri + 1,
                                    LastRowVisibleHeightPt: visibleH,
                                    FirstRowContentOffsetPt: sliceFirstRowOffset,
                                    IsContinuation: !isFirstSlice));
                                AddCellParasToLayouts(newLayouts, tableBlock, tableLayout,
                                    teIdx, tableXPt, sliceStartY, pageIdx,
                                    rowFrom, ri + 1, sliceFirstRowOffset, visibleH);

                                pageYPt = pageYPt + pageHeightPt + PageGapPt;
                                pageBottomPt = pageYPt + pageHeightPt - mb;
                                pageIdx++;
                                newPages.Add(new PageRect(pageYPt, pageWidthPt, pageHeightPt, pageXPt, mt, ml, mb));
                                contentYPt = pageYPt + mt;
                                sliceStartY = contentYPt;
                                sliceStartOffset = nextOffset;

                                _logger.Debug(
                                    "[TBL] BYCELL-NEWPAGE pageIdx={PI} newContentY={CY:F1} sliceStartY={SSY:F1} nextOffset={NO:F1}",
                                    pageIdx, contentYPt, sliceStartY, nextOffset);

                                rowFrom = ri;
                                sliceFirstRowOffset = nextOffset;
                                isFirstSlice = false;
                                ri--;  // повторяем строку ri на новой странице
                                continue;
                            }
                            else
                            {
                                // ByRow: строка ri целиком на следующую страницу
                                _logger.Debug(
                                    "[TBL] BYROW-SPLIT ri={RI} sliceStartY={SSY:F1} rowFrom={RF} pageIdx={PI}",
                                    ri, sliceStartY, rowFrom, pageIdx);
                                if (ri > rowFrom)
                                {
                                    int teIdx = newTables.Count;
                                    newTables.Add(new TableEntry(tableBlock, tableLayout,
                                        sliceStartY, tableXPt, pageIdx,
                                        RowFrom: rowFrom, RowTo: ri,
                                        LastRowVisibleHeightPt: -1f,
                                        FirstRowContentOffsetPt: sliceStartOffset,
                                        IsContinuation: !isFirstSlice));
                                    AddCellParasToLayouts(newLayouts, tableBlock, tableLayout,
                                        teIdx, tableXPt, sliceStartY, pageIdx,
                                        rowFrom, ri, sliceStartOffset, -1f);
                                }

                                pageYPt = pageYPt + pageHeightPt + PageGapPt;
                                pageBottomPt = pageYPt + pageHeightPt - mb;
                                pageIdx++;
                                newPages.Add(new PageRect(pageYPt, pageWidthPt, pageHeightPt, pageXPt, mt, ml, mb));
                                contentYPt = pageYPt + mt;
                                sliceStartY = contentYPt;
                                sliceStartOffset = 0f;

                                rowFrom = ri;
                                sliceFirstRowOffset = 0f;
                                isFirstSlice = false;
                            }
                        }
                        else
                        {
                            sliceFirstRowOffset = 0f;
                        }

                        contentYPt += effectiveH;
                    }

                    // Финальный слайс
                    _logger.Debug(
                        "[TBL] FINAL sliceStartY={SSY:F1} rowFrom={RF} contentY={CY:F1} pageIdx={PI}",
                        sliceStartY, rowFrom, contentYPt, pageIdx);
                    if (rowFrom < tableLayout.Rows.Count)
                    {
                        int teIdx = newTables.Count;
                        newTables.Add(new TableEntry(tableBlock, tableLayout,
                            sliceStartY, tableXPt, pageIdx,
                            RowFrom: rowFrom, RowTo: -1,
                            LastRowVisibleHeightPt: -1f,
                            FirstRowContentOffsetPt: sliceStartOffset,
                            IsContinuation: !isFirstSlice));
                        AddCellParasToLayouts(newLayouts, tableBlock, tableLayout,
                            teIdx, tableXPt, sliceStartY, pageIdx,
                            rowFrom, -1, sliceStartOffset, -1f);
                    }

                    contentYPt += FallbackLinePt;
                    continue;
                }

                if (block is not ParagraphBlock paraBlock) continue;

                ParagraphViewModel? pvm = null;
                foreach (var p in DocVm.Paragraphs)
                    if (p.Model == paraBlock) { pvm = p; break; }
                if (pvm is null) continue;

                var layout = GetOrBuildLayout(pvm, textWidthPt);
                if (layout.Lines.Count == 0) continue;

                float absXPt = textXPt;
                contentYPt += layout.SpaceBeforePt;
                int lineFrom = 0;
                float lineGroupYPt = contentYPt;

                for (int li = 0; li < layout.Lines.Count; li++)
                {
                    var line = layout.Lines[li];
                    bool isLast = li == layout.Lines.Count - 1;

                    if (contentYPt + line.Height > pageBottomPt
                        && contentYPt > pageYPt + mt)
                    {
                        if (li > lineFrom)
                        {
                            newLayouts.Add(new ParaLayout(
                                pvm, layout, lineGroupYPt,
                                contentYPt - lineGroupYPt,
                                pageIdx, lineFrom, li,
                                AbsXPt: absXPt));
                        }

                        pageYPt = pageYPt + pageHeightPt + PageGapPt;
                        pageBottomPt = pageYPt + pageHeightPt - mb;
                        contentYPt = pageYPt + mt;
                        pageIdx++;
                        newPages.Add(new PageRect(pageYPt, pageWidthPt, pageHeightPt, pageXPt, mt, ml, mb));

                        lineFrom = li;
                        lineGroupYPt = contentYPt;
                    }

                    contentYPt += line.Height;
                    if (isLast) contentYPt += layout.SpaceAfterPt;
                }

                newLayouts.Add(new ParaLayout(
                    pvm, layout, lineGroupYPt,
                    contentYPt - lineGroupYPt,
                    pageIdx, lineFrom, layout.Lines.Count,
                    AbsXPt: absXPt));
            }

            float newCanvasH = pageYPt + pageHeightPt + PageGapPt;

            lock (_renderLock)
            {
                _layouts = newLayouts;
                _pages = newPages;
                _tables = newTables;
                _canvasHeightPt = newCanvasH;
                _canvasHeight = newCanvasH * PtToPx;
            }
        }

        private void RebuildFlowMode(float maxWidthPt, float padHPt, float padWPt)
        {
            float textWidthPt = Math.Max(maxWidthPt - padWPt * 2f, 1f);
            float yPt = padHPt;

            var newLayouts = new List<ParaLayout>();
            var newTables = new List<TableEntry>();

            foreach (var block in DocVm!.Document.Sections[0].Blocks)
            {
                if (block is TableBlock tableBlock)
                {
                    var tableLayout = _renderer.BuildTableLayout(tableBlock, textWidthPt, _styleResolver!);
                    float tableXPt = padWPt + (float)tableBlock.LeftIndentPt;
                    int teIdx = newTables.Count;
                    newTables.Add(new TableEntry(tableBlock, tableLayout, yPt, tableXPt, 0));

                    AddCellParasToLayouts(newLayouts, tableBlock, tableLayout,
                        teIdx, tableXPt, yPt, 0);

                    yPt += tableLayout.TotalHeightPt + FallbackLinePt;
                    continue;
                }

                if (block is not ParagraphBlock paraBlock) continue;

                ParagraphViewModel? pvm = null;
                foreach (var p in DocVm.Paragraphs)
                    if (p.Model == paraBlock) { pvm = p; break; }
                if (pvm is null) continue;

                var layout = GetOrBuildLayout(pvm, textWidthPt);
                float hPt = Math.Max(layout.TotalHeightPt, FallbackLinePt);
                newLayouts.Add(new ParaLayout(
                    pvm, layout,
                    yPt + layout.SpaceBeforePt, hPt,
                    0, 0, layout.Lines.Count,
                    AbsXPt: padWPt));
                yPt += layout.BlockHeightPt;
            }

            float newCanvasH = yPt + padHPt;

            lock (_renderLock)
            {
                _layouts = newLayouts;
                _pages = new List<PageRect>();
                _tables = newTables;
                _canvasHeightPt = newCanvasH;
                _canvasHeight = newCanvasH * PtToPx;
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

        // ── Render ────────────────────────────────────────────────────────
        public override void Render(DrawingContext ctx)
        {
            ctx.Custom(new CanvasSKDrawOperation(
                this, new Rect(0, 0, Bounds.Width, Bounds.Height)));
        }

        internal void RenderWithSKCanvas(SKCanvas canvas)
        {
            List<ParaLayout> layouts;
            List<PageRect> pages;
            List<TableEntry> tables;
            float canvasHeightPt;
            double canvasWidth;

            lock (_renderLock)
            {
                layouts = _layouts;
                pages = _pages;
                tables = _tables;
                canvasHeightPt = _canvasHeightPt;
                canvasWidth = _canvasWidth;
            }

            double zoom = Zoom;
            float scale = (float)(PtToPx * zoom);

            int pixelW = (int)Math.Max(Bounds.Width, 1);
            int pixelH = (int)Math.Max(Bounds.Height, 1);

            if (_caretOnlyRedraw
                && _lastFullRenderBitmap is not null
                && _lastFullRenderWidth == pixelW
                && _lastFullRenderHeight == pixelH)
            {
                _caretOnlyRedraw = false;
                canvas.DrawBitmap(_lastFullRenderBitmap, 0, 0);

                if (_caretVisible)
                {
                    canvas.Save();
                    canvas.Scale(scale, scale);
                    DrawCaretOnCanvas(canvas, layouts, pages, canvasWidth);
                    canvas.Restore();
                }
                return;
            }

            _caretOnlyRedraw = false;

            using var surface = SKSurface.Create(
                new SKImageInfo(pixelW, pixelH, SKColorType.Bgra8888, SKAlphaType.Premul));

            if (surface is not null)
            {
                var offscreen = surface.Canvas;
                offscreen.Save();
                offscreen.Scale(scale, scale);

                var mode = DocVm?.ViewMode ?? EditorViewMode.Draft;
                if (mode == EditorViewMode.Page)
                    RenderPageMode(offscreen, layouts, pages, tables, canvasHeightPt, canvasWidth, false);
                else
                    RenderFlowMode(offscreen, mode, layouts, tables, canvasHeightPt, canvasWidth, false);

                offscreen.Restore();

                using var snapshot = surface.Snapshot();
                _lastFullRenderBitmap?.Dispose();
                _lastFullRenderBitmap = SKBitmap.FromImage(snapshot);
                _lastFullRenderWidth = pixelW;
                _lastFullRenderHeight = pixelH;

                canvas.DrawBitmap(_lastFullRenderBitmap, 0, 0);

                if (_caretVisible)
                {
                    canvas.Save();
                    canvas.Scale(scale, scale);
                    DrawCaretOnCanvas(canvas, layouts, pages, canvasWidth);
                    canvas.Restore();
                }
            }
            else
            {
                canvas.Save();
                canvas.Scale(scale, scale);
                var mode = DocVm?.ViewMode ?? EditorViewMode.Draft;
                if (mode == EditorViewMode.Page)
                    RenderPageMode(canvas, layouts, pages, tables, canvasHeightPt, canvasWidth, _caretVisible);
                else
                    RenderFlowMode(canvas, mode, layouts, tables, canvasHeightPt, canvasWidth, _caretVisible);
                canvas.Restore();
            }
        }

        // Рисует только рамки и фон таблицы (без параграфов — они в _layouts).
        private static void RenderTableStructureOnly(
            SKCanvas canvas, SKTableLayout tableLayout, float tableX, float tableY,
            int rowFrom = 0, int rowTo = -1,
            float lastRowVisibleHeightPt = -1f, float firstRowContentOffsetPt = 0f,
            bool isContinuation = false)
        {
            var m = canvas.TotalMatrix;
            float canvasScale = MathF.Sqrt(m.ScaleX * m.ScaleX + m.SkewY * m.SkewY);
            if (canvasScale < 0.01f) canvasScale = 1f;

            int effectiveRowTo = rowTo < 0 ? tableLayout.Rows.Count : rowTo;
            float rowOffsetY = rowFrom > 0 && rowFrom < tableLayout.Rows.Count
                ? tableLayout.Rows[rowFrom].Ypt : 0f;

            foreach (var row in tableLayout.Rows)
            {
                if (row.Row < rowFrom || row.Row >= effectiveRowTo) continue;

                bool isFirstRow = row.Row == rowFrom;
                bool isLastRow = row.Row == effectiveRowTo - 1;
                float rowShift = isFirstRow ? firstRowContentOffsetPt : 0f;
                // Эффективная высота строки после вычета уже показанной части сверху.
                float effectiveRowH = isFirstRow ? row.HeightPt - rowShift : row.HeightPt;
                // Для последней строки слайса с ByCell-разрывом — ограничиваем снизу.
                // lastRowVisibleHeightPt уже выражен как высота видимого окна на этой странице,
                // без вычета firstRowShift, поэтому просто берём его напрямую.
                float visibleH = (isLastRow && lastRowVisibleHeightPt >= 0f)
                    ? lastRowVisibleHeightPt
                    : effectiveRowH;

                foreach (var cell in row.Cells)
                {
                    float cellX = tableX + cell.Xpt;
                    float cellY = tableY + cell.Ypt - rowOffsetY - rowShift;

                    // Фон — только в пределах видимой части строки
                    if (!string.IsNullOrEmpty(cell.BackgroundColor)
                        && SKColor.TryParse(cell.BackgroundColor, out var bgColor))
                    {
                        using var bgPaint = new SKPaint { Color = bgColor };
                        canvas.DrawRect(cellX, cellY + rowShift, cell.WidthPt, visibleH, bgPaint);
                    }

                    // Видимый верхний край (для первой строки продолжения cellY сдвинут вверх).
                    float visibleCellY = cellY + rowShift;
                    bool suppressBottom = isLastRow && lastRowVisibleHeightPt >= 0f;
                    SKTextRenderer.RenderCellBordersPublic(canvas, cell, cellX, visibleCellY,
                        visibleH, canvasScale, false, suppressBottom);
                }
            }
        }

        // Цвета ручек
        private static readonly SKColor HandleFill = new(0x22, 0x99, 0xFF, 0xCC);
        private static readonly SKColor HandleStroke = new(0xFF, 0xFF, 0xFF, 0xCC);

        /// <summary>
        /// Рисует ↔-ручки на внутренних границах колонок и правом крае таблицы (по центру высоты),
        /// и ↕-ручку на нижнем крае таблицы (по центру ширины).
        /// Ручки рисуются только для активной таблицы (где стоит каретка).
        /// </summary>
        private void RenderTableHandles(SKCanvas canvas, TableEntry te)
        {
            if (!ReferenceEquals(te.Table, _activeTableBlock)) return;

            var layout = te.Layout;
            float tableX = te.XPt;
            float tableY = te.Ypt;
            float tableH = layout.TotalHeightPt;
            float tableW = layout.TotalWidthPt;

            const float HW = 6f;   // half-width ручки в pt
            const float HH = 4f;   // half-height ручки в pt

            using var fill = new SKPaint { Color = HandleFill, IsAntialias = true };
            using var stroke = new SKPaint { Color = HandleStroke, StrokeWidth = 1f, IsStroke = true, IsAntialias = true };

            // ↔ на каждой внутренней и внешней правой границе колонки (по центру Y таблицы)
            float midY = tableY + tableH / 2f;
            float accX = tableX;
            for (int i = 0; i < layout.ColumnWidthsPt.Count; i++)
            {
                accX += layout.ColumnWidthsPt[i];
                float hx = accX;
                float hy = midY;
                DrawHandle(canvas, hx, hy, HW, HH, fill, stroke, horizontal: true);
            }

            // ↕ на нижнем краю по центру ширины
            float midX = tableX + tableW / 2f;
            DrawHandle(canvas, midX, tableY + tableH, HH, HW, fill, stroke, horizontal: false);

            // ↔ на левом крае (для сдвига всей таблицы)
            DrawHandle(canvas, tableX, midY, HW, HH, fill, stroke, horizontal: true);
        }

        private static void DrawHandle(SKCanvas canvas,
            float cx, float cy, float hw, float hh,
            SKPaint fill, SKPaint stroke, bool horizontal)
        {
            var rect = new SKRect(cx - hw, cy - hh, cx + hw, cy + hh);
            canvas.DrawRoundRect(rect, 2f, 2f, fill);
            canvas.DrawRoundRect(rect, 2f, 2f, stroke);

            // Стрелочки внутри
            using var arrow = new SKPaint
            { Color = SKColors.White, StrokeWidth = 1f, IsStroke = true, IsAntialias = true };
            if (horizontal)
            {
                // ←
                canvas.DrawLine(cx - hw + 1.5f, cy, cx - 1f, cy, arrow);
                canvas.DrawLine(cx - hw + 1.5f, cy, cx - hw + 3.5f, cy - 2f, arrow);
                canvas.DrawLine(cx - hw + 1.5f, cy, cx - hw + 3.5f, cy + 2f, arrow);
                // →
                canvas.DrawLine(cx + hw - 1.5f, cy, cx + 1f, cy, arrow);
                canvas.DrawLine(cx + hw - 1.5f, cy, cx + hw - 3.5f, cy - 2f, arrow);
                canvas.DrawLine(cx + hw - 1.5f, cy, cx + hw - 3.5f, cy + 2f, arrow);
            }
            else
            {
                // ↑
                canvas.DrawLine(cx, cy - hh + 1.5f, cx, cy - 1f, arrow);
                canvas.DrawLine(cx, cy - hh + 1.5f, cx - 2f, cy - hh + 3.5f, arrow);
                canvas.DrawLine(cx, cy - hh + 1.5f, cx + 2f, cy - hh + 3.5f, arrow);
                // ↓
                canvas.DrawLine(cx, cy + hh - 1.5f, cx, cy + 1f, arrow);
                canvas.DrawLine(cx, cy + hh - 1.5f, cx - 2f, cy + hh - 3.5f, arrow);
                canvas.DrawLine(cx, cy + hh - 1.5f, cx + 2f, cy + hh - 3.5f, arrow);
            }
        }

        private void RenderPageMode(
            SKCanvas canvas,
            List<ParaLayout> layouts,
            List<PageRect> pages,
            List<TableEntry> tables,
            float canvasHeightPt,
            double canvasWidth,
            bool drawCaret)
        {
            float canvasWPt = (float)(canvasWidth * PxToPt);

            using var bgPaint = new SKPaint { Color = CanvasBgColor };
            canvas.DrawRect(0, 0, canvasWPt, canvasHeightPt, bgPaint);

            var (firstPage, lastPage) = GetVisiblePageRange(pages);

            for (int pi = firstPage; pi <= lastPage && pi < pages.Count; pi++)
            {
                var page = pages[pi];
                using var sh = new SKPaint { Color = PageShadowColor };
                canvas.DrawRect(page.PadLeftPt + 3, page.Ypt + 3, page.WidthPt, page.HeightPt, sh);
                using var pg = new SKPaint { Color = SKColors.White };
                canvas.DrawRect(page.PadLeftPt, page.Ypt, page.WidthPt, page.HeightPt, pg);
            }

            // Рисуем рамки таблиц (без содержимого) — клипуем по правому краю страницы
            foreach (var te in tables)
            {
                if (te.PageIndex < firstPage || te.PageIndex > lastPage) continue;
                // Клип по правому краю страницы: таблица может выходить за край,
                // но видна только в пределах страницы.
                // По вертикали клипуем по полной высоте страницы (включая поля),
                // а не по текстовой зоне — иначе нижняя граница последней ByRow-строки
                // (расположенная точно на textBottom) обрезается исключающим клипом.
                // Корректное отсечение лишних линий обеспечивают suppressBottom/visibleH.
                if (te.PageIndex < pages.Count)
                {
                    var pg = pages[te.PageIndex];
                    float pageRight = pg.PadLeftPt + pg.WidthPt;
                    float pageTop = pg.Ypt;
                    float pageBottom = pg.Ypt + pg.HeightPt;
                    canvas.Save();
                    canvas.ClipRect(new SKRect(0, pageTop, pageRight, pageBottom));
                    RenderTableStructureOnly(canvas, te.Layout, te.XPt, te.Ypt,
                        te.RowFrom, te.RowTo,
                        te.LastRowVisibleHeightPt, te.FirstRowContentOffsetPt,
                        te.IsContinuation);
                    canvas.Restore();
                }
                else
                {
                    RenderTableStructureOnly(canvas, te.Layout, te.XPt, te.Ypt,
                        te.RowFrom, te.RowTo,
                        te.LastRowVisibleHeightPt, te.FirstRowContentOffsetPt,
                        te.IsContinuation);
                }
            }

            for (int i = 0; i < layouts.Count; i++)
            {
                var pl = layouts[i];
                if (pl.PageIndex < firstPage || pl.PageIndex > lastPage) continue;

                // Для ячеек таблицы дополнительно клипуем по правому краю страницы
                // (ячейка может выходить за край, текст должен быть обрезан).
                if (pl.Cell != null && pl.PageIndex < pages.Count)
                {
                    var pg = pages[pl.PageIndex];
                    float pageRight = pg.PadLeftPt + pg.WidthPt;
                    float textTop = pg.Ypt + pg.PadTopPt;
                    float textBottom = pg.Ypt + pg.HeightPt - pg.PadBottomPt;
                    canvas.Save();
                    canvas.ClipRect(new SKRect(0, textTop, pageRight, textBottom));
                    RenderParaLayout(canvas, i, pl, layouts, drawCaret);
                    canvas.Restore();
                }
                else
                {
                    RenderParaLayout(canvas, i, pl, layouts, drawCaret);
                }
            }
        }

        private void RenderFlowMode(
            SKCanvas canvas,
            EditorViewMode mode,
            List<ParaLayout> layouts,
            List<TableEntry> tables,
            float canvasHeightPt,
            double canvasWidth,
            bool drawCaret)
        {
            float canvasWPt = (float)(canvasWidth * PxToPt);

            using var bgPaint = new SKPaint { Color = SKColors.Transparent };
            canvas.DrawRect(0, 0, canvasWPt, canvasHeightPt, bgPaint);

            float zoom2 = (float)Zoom;
            float viewTopPt = (float)(_scrollOffsetY / zoom2 * PxToPt) - FallbackLinePt * 5f;
            float viewBotPt = (float)((_scrollOffsetY + Math.Max(_viewportHeight, 100))
                / zoom2 * PxToPt) + FallbackLinePt * 5f;

            foreach (var te in tables)
            {
                if (te.Ypt + te.Layout.TotalHeightPt < viewTopPt) continue;
                if (te.Ypt > viewBotPt) break;
                RenderTableStructureOnly(canvas, te.Layout, te.XPt, te.Ypt);
            }

            for (int i = 0; i < layouts.Count; i++)
            {
                var pl = layouts[i];
                if (pl.Ypt + pl.HeightPt < viewTopPt) continue;
                if (pl.Ypt > viewBotPt) break;

                RenderParaLayout(canvas, i, pl, layouts, drawCaret);
            }
        }

        /// <summary>
        /// Рисует один параграф (обычный или в ячейке таблицы).
        /// Для ячейки применяет clip-rect.
        /// </summary>
        private void RenderParaLayout(
            SKCanvas canvas, int idx, ParaLayout pl,
            List<ParaLayout> layouts, bool drawCaret)
        {
            float absX = pl.AbsXPt;
            float absY = pl.Ypt;

            bool isCell = pl.Cell != null;

            if (isCell)
            {
                canvas.Save();
                var clip = pl.Cell!;
                canvas.ClipRect(new SKRect(clip.ClipX, clip.ClipY,
                    clip.ClipX + clip.ClipW, clip.ClipY + clip.ClipH));
            }

            DrawSelectionForSlice(canvas, idx, pl, absX, absY, layouts);

            SKTextRenderer.RenderParagraphLines(
                canvas, pl.Layout,
                absX + pl.Layout.LeftIndentPt,
                absY,
                pl.LineFrom, pl.LineTo);

            if (drawCaret && _caretPara == idx)
                DrawCaret(canvas, pl, absX, absY);

            if (isCell)
                canvas.Restore();
        }

        private void DrawCaretOnCanvas(
            SKCanvas canvas,
            List<ParaLayout> layouts,
            List<PageRect> pages,
            double canvasWidth)
        {
            if (!_caretVisible) return;
            if (_caretPara < 0 || _caretPara >= layouts.Count) return;

            var pl = layouts[_caretPara];
            float xPt = pl.AbsXPt;

            // Для ячейки — clip чтобы каретка не торчала за рамку
            bool isCell = pl.Cell != null;
            if (isCell)
            {
                canvas.Save();
                var c = pl.Cell!;
                canvas.ClipRect(new SKRect(c.ClipX, c.ClipY, c.ClipX + c.ClipW, c.ClipY + c.ClipH));
            }

            DrawCaret(canvas, pl, xPt, pl.Ypt);

            if (isCell) canvas.Restore();
        }

        private (int first, int last) GetVisiblePageRange(List<PageRect> pages)
        {
            if (pages.Count == 0) return (0, 0);
            double zoom2 = Zoom;
            float viewTopPt = (float)(_scrollOffsetY / zoom2 * PxToPt);
            float viewBotPt = (float)((_scrollOffsetY + Math.Max(_viewportHeight, 100)) / zoom2 * PxToPt);
            float bufferPt = (pages.Count > 0 ? pages[0].HeightPt : 842f) + PageGapPt;
            viewTopPt -= bufferPt;
            viewBotPt += bufferPt;

            int first = 0, last = pages.Count - 1;
            for (int i = 0; i < pages.Count; i++)
                if (pages[i].Ypt + pages[i].HeightPt >= viewTopPt) { first = i; break; }
            for (int i = first; i < pages.Count; i++)
            {
                last = i;
                if (pages[i].Ypt > viewBotPt) break;
            }
            return (first, last);
        }

        private void DrawSelectionForSlice(
            SKCanvas canvas, int sliceIdx, ParaLayout pl,
            float xPt, float yPt, List<ParaLayout> layouts)
        {
            if (!HasSel()) return;

            var (sp, sc, ep, ec) = NormalizeSelection();
            if (sliceIdx < sp || sliceIdx > ep) return;

            int len = pl.Vm.PlainText?.Length ?? 0;
            int from = sliceIdx == sp ? sc : 0;
            int to = sliceIdx == ep ? ec : len;

            from = Clamp(from, 0, len);
            to = Clamp(to, 0, len);
            if (from >= to && !(from == 0 && len == 0)) return;

            if (from == to && len == 0)
            {
                float lineH = pl.Layout.Lines.Count > 0 ? pl.Layout.Lines[0].Height : FallbackLinePt;
                float yBase = pl.LineFrom < pl.Layout.Lines.Count ? pl.Layout.Lines[pl.LineFrom].Y : 0f;
                using var ep2 = new SKPaint { Color = SelectionColor };
                canvas.DrawRect(xPt, yPt + (0 - yBase), 5f, lineH, ep2);
                return;
            }

            var rects = pl.Layout.HitTestRange(from, to);
            if (rects.Count == 0) return;

            float yBase2 = pl.LineFrom < pl.Layout.Lines.Count
                ? pl.Layout.Lines[pl.LineFrom].Y : 0f;

            using var paint = new SKPaint { Color = SelectionColor };
            foreach (var r in rects)
            {
                if (r.LineIndex < pl.LineFrom || r.LineIndex >= pl.LineTo) continue;
                canvas.DrawRect(
                    xPt + r.Rect.Left,
                    yPt + (r.Rect.Top - yBase2),
                    r.Rect.Width, r.Rect.Height, paint);
            }
        }

        private void DrawCaret(SKCanvas canvas, ParaLayout pl, float xPt, float yPt)
        {
            int pos = Clamp(_caretChar, 0, pl.Vm.PlainText?.Length ?? 0);

            float yBase = pl.LineFrom < pl.Layout.Lines.Count
                ? pl.Layout.Lines[pl.LineFrom].Y : 0f;

            int drawLineIdx;
            SKCaretRect caret;

            if (_caretLineHint >= 0
                && _caretLineHint >= pl.LineFrom
                && _caretLineHint < Math.Min(pl.LineTo, pl.Layout.Lines.Count))
            {
                var hintLine = pl.Layout.Lines[_caretLineHint];
                if (pos > hintLine.LastCharIndex && !hintLine.IsLastLine)
                {
                    var lastSeg = hintLine.Segments.Count > 0 ? hintLine.Segments[^1] : null;
                    float hintLineExtra = (_caretLineHint == 0) ? pl.Layout.FirstLineIndentPt : 0f;
                    caret = new SKCaretRect
                    {
                        X = lastSeg != null
                            ? pl.Layout.LeftIndentPt + hintLineExtra + lastSeg.X + lastSeg.Width
                            : pl.Layout.LeftIndentPt + hintLineExtra,
                        Y = hintLine.Y,
                        Height = hintLine.Height,
                        Baseline = hintLine.Baseline
                    };
                    drawLineIdx = _caretLineHint;
                }
                else
                {
                    caret = pl.Layout.HitTestPosition(pos);
                    drawLineIdx = _caretLineHint;
                }
            }
            else
            {
                caret = pl.Layout.HitTestPosition(pos);
                drawLineIdx = pl.Layout.GetLineIndexForChar(pos);
            }

            // caret.X уже включает FirstLineIndentPt для строки 0 (из HitTestPosition).
            using var paint = new SKPaint { Color = SKColors.Black, StrokeWidth = 1.1f, IsAntialias = false };
            float cx = xPt + caret.X;
            float cy = yPt + (caret.Y - yBase);
            canvas.DrawLine(cx, cy, cx, cy + caret.Height, paint);
        }

        // ── Pointer ───────────────────────────────────────────────────────
        // ── Ручки таблицы — HitTest ───────────────────────────────────────

        private enum TableHandleType { None, ColResize, TableMove, RowResize }

        private struct TableHandleHit
        {
            public TableHandleType Type;
            public int EntryIdx;
            public int ColIndex;   // ColResize: индекс колонки; RowResize: индекс строки
        }

        // Hit-зона вокруг линии в pt (~4px при zoom=1)
        private const float TableLineHitPt = 4f * PxToPt;

        private TableHandleHit HitTestTableHandle(float xPt, float yPt)
        {
            List<TableEntry> tables;
            lock (_renderLock) { tables = _tables; }

            for (int ti = 0; ti < tables.Count; ti++)
            {
                var te = tables[ti];
                float tX = te.XPt;
                float tY = te.Ypt;
                float tH = te.Layout.TotalHeightPt;
                float tW = te.Layout.TotalWidthPt;
                float r = TableLineHitPt;

                // Грубая проверка — вне таблицы совсем
                if (yPt < tY - r || yPt > tY + tH + r) continue;
                if (xPt < tX - r || xPt > tX + tW + r) continue;

                // Вертикальные линии — только если курсор НА линии по X
                // И внутри вертикального диапазона таблицы по Y
                bool onTableY = yPt >= tY - r && yPt <= tY + tH + r;
                if (onTableY)
                {
                    // Левый край таблицы
                    if (Math.Abs(xPt - tX) <= r)
                        return new TableHandleHit { Type = TableHandleType.TableMove, EntryIdx = ti };

                    // Правые края колонок
                    float accX = tX;
                    for (int i = 0; i < te.Layout.ColumnWidthsPt.Count; i++)
                    {
                        accX += te.Layout.ColumnWidthsPt[i];
                        if (Math.Abs(xPt - accX) <= r)
                            return new TableHandleHit
                            {
                                Type = TableHandleType.ColResize,
                                EntryIdx = ti,
                                ColIndex = i
                            };
                    }
                }

                // Горизонтальные линии — только если курсор НА линии по Y
                // И внутри горизонтального диапазона таблицы по X
                bool onTableX = xPt >= tX - r && xPt <= tX + tW + r;
                if (onTableX)
                {
                    float accY = tY;
                    foreach (var row in te.Layout.Rows)
                    {
                        accY += row.HeightPt;
                        if (Math.Abs(yPt - accY) <= r)
                            return new TableHandleHit
                            {
                                Type = TableHandleType.RowResize,
                                EntryIdx = ti,
                                ColIndex = row.Row
                            };
                    }
                }
            }
            return new TableHandleHit { Type = TableHandleType.None };
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            Focus();

            var pt = e.GetPosition(this);
            double zoom = Zoom;
            float xPt = (float)(pt.X / zoom * PxToPt);
            float yPt = (float)(pt.Y / zoom * PxToPt);

            // ── Проверяем ручки таблицы ПЕРВЫМИ ─────────────────────────
            var handleHit = HitTestTableHandle(xPt, yPt);
            if (handleHit.Type != TableHandleType.None)
            {
                _tableDragMode = (TableDragMode)(int)handleHit.Type;
                _tableDragEntryIdx = handleHit.EntryIdx;
                _tableDragColIndex = handleHit.ColIndex;
                _tableDragStartXPt = xPt;

                var te = _tables[handleHit.EntryIdx];
                if (handleHit.Type == TableHandleType.ColResize)
                {
                    // Берём фактическую ширину из layout (может быть Auto → вычисленная)
                    // и конвертируем pt → мм
                    float colWidthPt = handleHit.ColIndex < te.Layout.ColumnWidthsPt.Count
                        ? te.Layout.ColumnWidthsPt[handleHit.ColIndex]
                        : 20f;
                    _tableDragStartVal = (float)(colWidthPt * 25.4 / 72.0); // мм
                }
                else
                {
                    _tableDragStartVal = (float)te.Table.LeftIndentPt; // pt
                }

                // Входим в таблицу если ещё не там
                if (_activeTableBlock == null || !ReferenceEquals(_activeTableBlock, te.Table))
                {
                    _activeTableBlock = te.Table;
                    _activeCellTableEntryIdx = handleHit.EntryIdx;
                    if (DocVm is not null) DocVm.ActiveTable = te.Table;
                }

                Cursor = new Cursor(handleHit.Type == TableHandleType.RowResize
                    ? StandardCursorType.SizeNorthSouth
                    : StandardCursorType.SizeWestEast);
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }

            var (pi, ci) = HitTest(pt);

            // Определяем: это ячейка таблицы?
            bool wasInCell = IsInCell(_caretPara);
            bool nowInCell = pi >= 0 && pi < _layouts.Count && _layouts[pi].Cell != null;

            _caretPara = pi;
            _caretChar = ci;
            _selStartPara = pi; _selStartChar = ci;
            _selEndPara = pi; _selEndChar = ci;
            _isSelecting = true;

            SnapCaretToCorrectSlice();
            UpdatePreferredX();

            // Уведомляем о смене контекста (ячейка / параграф)
            UpdateCellContext(wasInCell, nowInCell);

            // Обновляем активный параграф для риббона
            if (!nowInCell)
            {
                var pvm = GetVmAt(_caretPara);
                if (pvm is not null) DocVm?.SetActiveParagraph(pvm);
            }
            else
            {
                var cell = _layouts[_caretPara].Cell!;
                DocVm?.FireTableCellCursorContext(cell.ParaBlock);
            }

            UpdateSelectionContext();
            ResetCaret(); InvalidateFull();
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);

            double zoom = Zoom;
            var rawPt = e.GetPosition(this);
            float xPt = (float)(rawPt.X / zoom * PxToPt);
            float yPt = (float)(rawPt.Y / zoom * PxToPt);

            // ── Drag ручки таблицы ────────────────────────────────────────
            if (_tableDragMode != TableDragMode.None)
            {
                if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                {
                    FinishTableDrag();
                    return;
                }

                float deltaPt = xPt - _tableDragStartXPt;

                if (_tableDragMode == TableDragMode.TableMove)
                {
                    // Сдвигаем всю таблицу: LeftIndentPt += delta (без ограничений)
                    if (_activeTableBlock is not null)
                    {
                        _activeTableBlock.LeftIndentPt = _tableDragStartVal + deltaPt;
                        if (DocVm is not null) DocVm.ActiveTable = _activeTableBlock;
                        _cellLayoutCache.Clear();
                        RebuildLayouts();
                        NotifyCaretEnteredTableCallback();
                        InvalidateFull();
                    }
                }
                else if (_tableDragMode == TableDragMode.ColResize)
                {
                    // Изменяем ширину колонки: WidthValue (мм) + delta (pt → мм)
                    if (_activeTableBlock is not null
                        && _tableDragColIndex >= 0
                        && _tableDragColIndex < _activeTableBlock.Columns.Count)
                    {
                        double deltaMm = deltaPt * 25.4 / 72.0;
                        // _tableDragStartVal = ширина колонки в мм на момент нажатия
                        double newMm = Math.Max(5.0, _tableDragStartVal + deltaMm);
                        _activeTableBlock.Columns[_tableDragColIndex].WidthType = TableColumnWidthType.Fixed;
                        _activeTableBlock.Columns[_tableDragColIndex].WidthValue = newMm;
                        if (DocVm is not null) DocVm.ActiveTable = _activeTableBlock;
                        _cellLayoutCache.Clear();
                        RebuildLayouts();
                        NotifyCaretEnteredTableCallback();
                        InvalidateFull();
                    }
                }
                else if (_tableDragMode == TableDragMode.RowResize)
                {
                    // Изменяем высоту строки по вертикальному drag (Y delta)
                    float deltaYPt = yPt - _tableDragStartXPt; // используем StartXPt как startYPt
                    if (_activeTableBlock is not null
                        && _tableDragColIndex >= 0
                        && _activeCellTableEntryIdx >= 0
                        && _activeCellTableEntryIdx < _tables.Count)
                    {
                        var te = _tables[_activeCellTableEntryIdx];
                        if (_tableDragColIndex < te.Layout.Rows.Count)
                        {
                            // RowHeight задаём через свойство RowHeight на модели
                            // Сохраняем min 5pt
                            double newHeightPt = Math.Max(5.0, _tableDragStartVal + deltaYPt);
                            // Применяем ко всем ячейкам строки через RowHeightPt в TableBlock
                            // (если нет отдельного поля — пока пропускаем, только rebuild)
                        }
                    }
                }

                e.Handled = true;
                return;
            }

            // ── Курсор при наведении на ручки ─────────────────────────────
            if (!_isSelecting)
            {
                var handleHit = HitTestTableHandle(xPt, yPt);
                Cursor = handleHit.Type switch
                {
                    TableHandleType.RowResize => new Cursor(StandardCursorType.SizeNorthSouth),
                    TableHandleType.ColResize or TableHandleType.TableMove
                        => new Cursor(StandardCursorType.SizeWestEast),
                    _ => new Cursor(StandardCursorType.Ibeam)
                };
            }

            if (!_isSelecting) return;
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

            var (pi, ci) = HitTest(rawPt);

            // Выделение только внутри одной ячейки (или только вне ячеек)
            bool startInCell = IsInCell(_selStartPara);
            bool nowInCell = pi >= 0 && pi < _layouts.Count && _layouts[pi].Cell != null;
            if (startInCell != nowInCell) { e.Handled = true; return; }
            if (startInCell && nowInCell)
            {
                var startCell = _layouts[_selStartPara].Cell;
                var endCell = _layouts[pi].Cell;
                if (startCell?.Cell != endCell?.Cell) { e.Handled = true; return; }
            }

            _selEndPara = pi; _selEndChar = ci;
            _caretPara = pi; _caretChar = ci;

            UpdateSelectionContext();
            InvalidateFull();
            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);

            if (_tableDragMode != TableDragMode.None)
            {
                FinishTableDrag();
                e.Pointer.Capture(null);
                Cursor = new Cursor(StandardCursorType.Ibeam);
                e.Handled = true;
                return;
            }

            _isSelecting = false;
            UpdateSelectionContext();
        }

        private void FinishTableDrag()
        {
            if (_tableDragMode == TableDragMode.ColResize && _activeTableBlock is not null)
            {
                // Фиксируем ВСЕ колонки по текущим ширинам из layout
                if (_activeCellTableEntryIdx >= 0 && _activeCellTableEntryIdx < _tables.Count)
                {
                    var te = _tables[_activeCellTableEntryIdx];
                    for (int i = 0; i < _activeTableBlock.Columns.Count
                                    && i < te.Layout.ColumnWidthsPt.Count; i++)
                    {
                        _activeTableBlock.Columns[i].WidthType = TableColumnWidthType.Fixed;
                        _activeTableBlock.Columns[i].WidthValue = te.Layout.ColumnWidthsPt[i] * 25.4 / 72.0;
                    }
                }
            }

            _tableDragMode = TableDragMode.None;
            _tableDragEntryIdx = -1;
            _tableDragColIndex = -1;
        }

        // ── Keyboard ─────────────────────────────────────────────────────
        protected override void OnTextInput(TextInputEventArgs e)
        {
            base.OnTextInput(e);
            if (string.IsNullOrEmpty(e.Text)) return;
            _caretLineHint = -1;

            InsertText(e.Text);
            e.Handled = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (_hotKeyService is not null)
            {
                var gesture = new KeyGesture(e.Key, e.KeyModifiers);
                if (_hotKeyService.HandleKeyPress(gesture, "TextEditor"))
                {
                    e.Handled = true;
                    return;
                }
            }

            HandleKeyFallback(e);
        }

        private void HandleKeyFallback(KeyEventArgs e)
        {
            bool shft = e.KeyModifiers == KeyModifiers.Shift;
            bool ctrl = e.KeyModifiers == KeyModifiers.Control;

            // Tab: навигация по ячейкам
            if (e.Key == Key.Tab && IsInCell(_caretPara))
            {
                if (shft) NavigateCellPrev(); else NavigateCellNext();
                e.Handled = true;
                return;
            }

            switch (e.Key)
            {
                case Key.Back: ExecuteDeleteBackSmart(); e.Handled = true; break;
                case Key.Delete: ExecuteDeleteForwardSmart(); e.Handled = true; break;
                case Key.Enter: ExecuteNewParagraphSmart(); e.Handled = true; break;

                case Key.Left: ExecuteNavLeft(shft); e.Handled = true; break;
                case Key.Right: ExecuteNavRight(shft); e.Handled = true; break;
                case Key.Up: ExecuteNavUp(shft); e.Handled = true; break;
                case Key.Down: ExecuteNavDown(shft); e.Handled = true; break;

                case Key.Home: ExecuteHome(ctrl, shft); e.Handled = true; break;
                case Key.End: ExecuteEnd(ctrl, shft); e.Handled = true; break;

                case Key.C when ctrl: ExecuteCopy(); e.Handled = true; break;
                case Key.X when ctrl: ExecuteCut(); e.Handled = true; break;
                case Key.V when ctrl: ExecutePaste(); e.Handled = true; break;
                case Key.A when ctrl: ExecuteSelectAll(); e.Handled = true; break;

                case Key.Z when ctrl: ExecuteUndo(); e.Handled = true; break;
                case Key.Y when ctrl: ExecuteRedo(); e.Handled = true; break;

                case Key.Escape when IsInCell(_caretPara):
                    // Escape из ячейки — перемещаем каретку на параграф после таблицы
                    EscapeCell();
                    e.Handled = true;
                    break;
            }
        }

        // ── Cell navigation ───────────────────────────────────────────────

        private bool IsInCell(int layoutIdx)
            => layoutIdx >= 0 && layoutIdx < _layouts.Count && _layouts[layoutIdx].Cell != null;

        private CellInfo? GetCurrentCell()
            => IsInCell(_caretPara) ? _layouts[_caretPara].Cell : null;

        /// <summary>
        /// Обновляет _activeTableBlock и уведомляет линейку о входе/выходе из ячейки.
        /// </summary>
        private void UpdateCellContext(bool wasInCell, bool nowInCell)
        {
            if (!wasInCell && !nowInCell) return;

            if (!nowInCell && wasInCell)
            {
                NotifyLeftCell();
                return;
            }

            if (nowInCell)
            {
                var cell = _layouts[_caretPara].Cell!;
                _activeTableBlock = cell.Table;
                _activeCellRow = cell.Cell.Row;
                _activeCellCol = cell.Cell.Column;
                _activeCellTableEntryIdx = cell.TableEntryIdx;

                // Сообщаем DocumentViewModel об активной таблице — линейка использует
                // это чтобы применять изменения ширины/отступа к правильной таблице.
                if (DocVm is not null) DocVm.ActiveTable = cell.Table;

                // Регистрируем делегаты структурных операций
                if (DocVm is { } vm)
                {
                    vm.TableAddRowDelegate = ExecuteTableAddRow;
                    vm.TableAddColDelegate = ExecuteTableAddColumn;
                    vm.TableDeleteRowDelegate = ExecuteTableDeleteRow;
                    vm.TableDeleteColDelegate = ExecuteTableDeleteColumn;
                    vm.TableDeleteDelegate = ExecuteTableDelete;
                    vm.TableSetLeftEdgeDelegate = leftIndentPt =>
                    {
                        if (_activeTableBlock is null) return;
                        _activeTableBlock.LeftIndentPt = leftIndentPt; // без ограничений
                        _cellLayoutCache.Clear();
                        RebuildLayouts();
                        NotifyCaretEnteredTableCallback();
                        InvalidateFull();
                    };
                }

                NotifyCaretEnteredTableCallback();
            }
        }

        private void NotifyLeftCell()
        {
            if (_activeTableBlock is null) return;
            _activeTableBlock = null;
            _activeCellTableEntryIdx = -1;

            if (DocVm is { } vm)
            {
                vm.ActiveTable = null;
                vm.TableAddRowDelegate = null;
                vm.TableAddColDelegate = null;
                vm.TableDeleteRowDelegate = null;
                vm.TableDeleteColDelegate = null;
                vm.TableDeleteDelegate = null;
                vm.TableSetLeftEdgeDelegate = null;
            }

            DocVm?.SetActiveParagraph(GetVmAt(_caretPara) ?? DocVm.Paragraphs.FirstOrDefault()!);
            CaretLeftTable?.Invoke();
        }

        private void NotifyCaretEnteredTableCallback()
        {
            if (_activeCellTableEntryIdx < 0 || _activeCellTableEntryIdx >= _tables.Count) return;

            var te = _tables[_activeCellTableEntryIdx];
            var offsets = new List<double>();
            var widths = new List<double>();

            foreach (var w in te.Layout.ColumnWidthsPt) widths.Add(PtToMm(w));
            foreach (var o in te.Layout.ColumnOffsetsPt) offsets.Add(PtToMm(o));

            double tableOffsetMm = PtToMm(te.XPt);
            if (_pages.Count > 0 && te.PageIndex < _pages.Count)
            {
                var pg = _pages[te.PageIndex];
                tableOffsetMm = PtToMm(te.XPt - (pg.PadLeftPt + pg.MarginLeftPt));
            }

            CaretEnteredTable?.Invoke(offsets, widths, tableOffsetMm, _activeCellCol);
        }

        /// <summary>Tab — следующая ячейка.</summary>
        private void NavigateCellNext()
        {
            // Ищем следующий layout entry в другой ячейке той же таблицы
            var curCell = GetCurrentCell();
            if (curCell is null) return;

            for (int i = _caretPara + 1; i < _layouts.Count; i++)
            {
                var pl = _layouts[i];
                if (pl.Cell?.Table != curCell.Table)
                {
                    // Вышли за пределы таблицы — переходим на первый не-ячеечный элемент
                    _caretPara = i;
                    _caretChar = 0;
                    bool wasInCell = true;
                    UpdateCellContext(wasInCell, false);
                    SyncSel(); ResetCaret(); InvalidateFull();
                    return;
                }
                // Первый параграф следующей ячейки
                if (pl.Cell?.Cell != curCell.Cell && pl.Cell?.CellParaIndex == 0)
                {
                    _caretPara = i;
                    _caretChar = 0;
                    UpdateCellContext(true, true);
                    if (DocVm is not null) DocVm.FireTableCellCursorContext(pl.Cell!.ParaBlock);
                    SyncSel(); ResetCaret(); InvalidateFull();
                    return;
                }
            }

            // Конец таблицы — ищем первый параграф после таблицы в _layouts
            for (int i = _caretPara + 1; i < _layouts.Count; i++)
            {
                if (_layouts[i].Cell == null)
                {
                    _caretPara = i; _caretChar = 0;
                    UpdateCellContext(true, false);
                    SyncSel(); ResetCaret(); InvalidateFull();
                    return;
                }
            }
        }

        /// <summary>Shift+Tab — предыдущая ячейка.</summary>
        private void NavigateCellPrev()
        {
            var curCell = GetCurrentCell();
            if (curCell is null) return;

            // Ищем первый параграф ячейки (CellParaIndex == 0) идущей перед текущей
            int prevCellStart = -1;

            for (int i = _caretPara - 1; i >= 0; i--)
            {
                var pl = _layouts[i];
                if (pl.Cell?.Table != curCell.Table)
                {
                    // Вышли за пределы таблицы
                    _caretPara = i; _caretChar = GetVmAt(i)?.PlainText?.Length ?? 0;
                    UpdateCellContext(true, false);
                    SyncSel(); ResetCaret(); InvalidateFull();
                    return;
                }
                if (pl.Cell?.Cell != curCell.Cell && pl.Cell?.CellParaIndex == 0)
                {
                    prevCellStart = i;
                    break;
                }
            }

            if (prevCellStart >= 0)
            {
                _caretPara = prevCellStart;
                _caretChar = GetVmAt(prevCellStart)?.PlainText?.Length ?? 0;
                UpdateCellContext(true, true);
                if (DocVm is not null) DocVm.FireTableCellCursorContext(_layouts[prevCellStart].Cell!.ParaBlock);
                SyncSel(); ResetCaret(); InvalidateFull();
            }
        }

        private void EscapeCell()
        {
            // Ищем первый не-ячеечный элемент после таблицы
            for (int i = _caretPara + 1; i < _layouts.Count; i++)
            {
                if (_layouts[i].Cell == null)
                {
                    _caretPara = i; _caretChar = 0;
                    UpdateCellContext(true, false);
                    SyncSel(); ResetCaret(); InvalidateFull();
                    return;
                }
            }
        }

        // ── Вставка / удаление текста (умные — работают в ячейке и вне) ──

        private void InsertText(string text)
        {
            if (IsInCell(_caretPara))
            {
                CellInsertText(text);
                return;
            }

            BeginEdit("Type text");
            DeleteSelection();

            var pvm = GetVmAt(_caretPara);
            if (pvm is null) return;

            string t = pvm.PlainText ?? "";
            int pos = Clamp(_caretChar, 0, t.Length);
            pvm.PlainText = t[..pos] + text + t[pos..];
            _caretChar = pos + text.Length;

            CommitEdit();
            UpdatePreferredX();
            SyncSel(); ResetCaret();
        }

        private void CellInsertText(string text)
        {
            var cell = GetCurrentCell();
            if (cell is null) return;

            string t = cell.ParaBlock.GetPlainText();
            int pos = Clamp(_caretChar, 0, t.Length);
            SetCellParaText(cell.Cell, cell.CellParaIndex, t[..pos] + text + t[pos..]);
            _caretChar = pos + text.Length;

            RebuildAfterCellEdit();
        }

        public void ExecuteDeleteBackSmart()
        {
            _caretLineHint = -1;

            if (IsInCell(_caretPara))
            {
                CellDeleteBack();
                return;
            }
            ExecuteDeleteBack();
        }

        public void ExecuteDeleteForwardSmart()
        {
            _caretLineHint = -1;

            if (IsInCell(_caretPara))
            {
                CellDeleteForward();
                return;
            }
            ExecuteDeleteForward();
        }

        public void ExecuteNewParagraphSmart()
        {
            if (IsInCell(_caretPara))
            {
                CellNewParagraph();
                return;
            }
            ExecuteNewParagraph();
        }

        private void CellDeleteBack()
        {
            var cell = GetCurrentCell();
            if (cell is null) return;

            string t = cell.ParaBlock.GetPlainText();

            if (_caretChar > 0)
            {
                int p = Clamp(_caretChar, 1, t.Length);
                SetCellParaText(cell.Cell, cell.CellParaIndex, t[..(p - 1)] + t[p..]);
                _caretChar = p - 1;
            }
            else if (cell.CellParaIndex > 0)
            {
                // Объединяем с предыдущим параграфом той же ячейки
                var prev = cell.Cell.Paragraphs[cell.CellParaIndex - 1];
                string pt = prev.GetPlainText();
                SetCellParaText(cell.Cell, cell.CellParaIndex - 1, pt + t);
                cell.Cell.Paragraphs.RemoveAt(cell.CellParaIndex);
                _caretChar = pt.Length;
                // Обновляем cell paraIndex → после rebuild snap найдёт нужный слайс
            }
            // else: начало первого параграфа ячейки — блокируем (нельзя выйти)

            RebuildAfterCellEdit();
        }

        private void CellDeleteForward()
        {
            var cell = GetCurrentCell();
            if (cell is null) return;

            string t = cell.ParaBlock.GetPlainText();

            if (_caretChar < t.Length)
            {
                int p = Clamp(_caretChar, 0, t.Length - 1);
                SetCellParaText(cell.Cell, cell.CellParaIndex, t[..p] + t[(p + 1)..]);
            }
            else if (cell.CellParaIndex < cell.Cell.Paragraphs.Count - 1)
            {
                // Объединяем со следующим параграфом ячейки
                var next = cell.Cell.Paragraphs[cell.CellParaIndex + 1];
                string nt = next.GetPlainText();
                SetCellParaText(cell.Cell, cell.CellParaIndex, t + nt);
                cell.Cell.Paragraphs.RemoveAt(cell.CellParaIndex + 1);
            }
            // else: конец последнего параграфа ячейки — блокируем

            RebuildAfterCellEdit();
        }

        private void CellNewParagraph()
        {
            var cell = GetCurrentCell();
            if (cell is null) return;

            string t = cell.ParaBlock.GetPlainText();
            int pos = Clamp(_caretChar, 0, t.Length);

            SetCellParaText(cell.Cell, cell.CellParaIndex, t[..pos]);

            var newPara = new ParagraphBlock();
            if (pos < t.Length)
            {
                var chunk = new TextChunk();
                chunk.Runs.Add(new RunModel { Text = t[pos..] });
                newPara.Chunks.Add(chunk);
            }
            cell.Cell.Paragraphs.Insert(cell.CellParaIndex + 1, newPara);
            _caretChar = 0;
            // После rebuild snap найдёт новый параграф (следующий за текущим в той же ячейке)

            RebuildAfterCellEdit();
        }

        private void RebuildAfterCellEdit()
        {
            // Запоминаем параграф ячейки для снапа после rebuild
            ParagraphBlock? targetBlock = null;
            if (IsInCell(_caretPara))
            {
                var cell = GetCurrentCell()!;
                // После удаления/вставки нужно снапнуться на правильный параграф.
                // Если текущий параграф ячейки всё ещё существует — на него.
                // Если нет (был удалён через merge) — на предыдущий.
                int idx = cell.CellParaIndex;
                if (idx < cell.Cell.Paragraphs.Count)
                    targetBlock = cell.Cell.Paragraphs[idx];
                else if (cell.Cell.Paragraphs.Count > 0)
                    targetBlock = cell.Cell.Paragraphs[cell.Cell.Paragraphs.Count - 1];
            }

            _cellLayoutCache.Clear();
            RebuildLayouts();

            // Snap: найти слайс с targetBlock
            if (targetBlock != null && _cellVmCache.TryGetValue(targetBlock, out var targetVm))
            {
                for (int i = 0; i < _layouts.Count; i++)
                    if (_layouts[i].Vm == targetVm) { _caretPara = i; break; }
            }

            NotifyCaretEnteredTableCallback();

            // Контекст ячейки для линейки
            if (IsInCell(_caretPara) && DocVm is not null)
                DocVm.FireTableCellCursorContext(_layouts[_caretPara].Cell!.ParaBlock);

            SyncSel(); ResetCaret(); InvalidateFull();
        }

        private void SetCellParaText(TableCell cell, int paraIdx, string text)
        {
            if (paraIdx >= cell.Paragraphs.Count) return;
            var para = cell.Paragraphs[paraIdx];
            if (para.Chunks.Count == 0)
            {
                var chunk = new TextChunk();
                chunk.Runs.Add(new RunModel { Text = text });
                para.Chunks.Add(chunk);
            }
            else
            {
                para.Chunks[0].Runs.Clear();
                para.Chunks[0].Runs.Add(new RunModel { Text = text });
                para.Chunks[0].InvalidateLength();
            }

            // Синхронизируем VM из кеша — иначе PlainText?.Length будет устаревшим,
            // что приводит к неправильному Clamp(_caretChar) в DrawCaret и навигации.
            // vm.PlainText setter записывает в модель повторно (безопасно) и обновляет _plainText.
            if (_cellVmCache.TryGetValue(para, out var vm))
                vm.PlainText = text;
        }

        // ── Публичные команды ─────────────────────────────────────────────
        public void ExecuteDeleteBack()
        {
            _caretLineHint = -1;
            var pvm = GetVmAt(_caretPara);
            if (pvm is null) return;
            string text = pvm.PlainText ?? "";
            BeginEdit("Delete");
            if (HasSel()) { DeleteSelection(); CommitEdit(); ResetCaret(); InvalidateFull(); return; }
            if (_caretChar > 0 && text.Length > 0)
            {
                int p = Clamp(_caretChar, 1, text.Length);
                pvm.PlainText = text[..(p - 1)] + text[p..];
                _caretChar = p - 1;
            }
            else if (_caretChar == 0 && _caretPara > 0 && !IsInCell(_caretPara))
                DocVm?.MergeParagraphWithPrevious(pvm, text);
            CommitEdit();
            SnapCaretToCorrectSlice();
            UpdatePreferredX();
            SyncSel(); ResetCaret(); InvalidateFull();
        }

        public void ExecuteDeleteForward()
        {
            _caretLineHint = -1;
            var pvm = GetVmAt(_caretPara);
            if (pvm is null) return;
            string text = pvm.PlainText ?? "";
            BeginEdit("Delete");
            if (HasSel()) { DeleteSelection(); CommitEdit(); ResetCaret(); InvalidateFull(); return; }
            if (_caretChar < text.Length)
            {
                int p = Clamp(_caretChar, 0, text.Length - 1);
                pvm.PlainText = text[..p] + text[(p + 1)..];
            }
            else if (_caretPara < _layouts.Count - 1 && !IsInCell(_caretPara))
            {
                var next = GetVmAt(_caretPara + 1);
                if (next is not null && !IsInCell(_caretPara + 1))
                { pvm.PlainText += next.PlainText; DocVm?.DeleteParagraph(next); }
            }
            CommitEdit();
            SnapCaretToCorrectSlice();
            UpdatePreferredX();
            SyncSel(); ResetCaret(); InvalidateFull();
        }

        public void ExecuteNewParagraph()
        {
            var pvm = GetVmAt(_caretPara);
            if (pvm is null) return;
            BeginEdit("New paragraph");
            DeleteSelection();
            string text = pvm.PlainText ?? "";
            int cp = Clamp(_caretChar, 0, text.Length);
            pvm.PlainText = text[..cp];
            var newVm = DocVm?.AddParagraphAfter(pvm);
            if (newVm is not null)
            {
                newVm.PlainText = text[cp..];
                _rebuildCts.Cancel();
                _rebuildCts = new System.Threading.CancellationTokenSource();
                RebuildLayouts();
                for (int i = 0; i < _layouts.Count; i++)
                    if (_layouts[i].Vm == newVm) { _caretPara = i; _caretChar = 0; break; }
            }
            CommitEdit();
            SnapCaretToCorrectSlice();
            UpdatePreferredX();
            SyncSel(); ResetCaret(); InvalidateFull();
        }

        public void ExecuteNavLeft(bool extend)
        {
            _caretLineHint = -1;
            bool inCell = IsInCell(_caretPara);

            if (HasSel() && !extend)
            { var (sp, sc, _, _) = NormalizeSelection(); _caretPara = sp; _caretChar = sc; }
            else if (_caretChar > 0)
                _caretChar--;
            else if (_caretPara > 0 && !inCell)
            { _caretPara--; _caretChar = GetVmAt(_caretPara)?.PlainText?.Length ?? 0; }
            // В ячейке: не выходим за начало через стрелки

            SnapCaretToCorrectSlice();
            if (!extend) SyncSel(); else ExtendSel();
            UpdatePreferredX();
            ResetCaret(); InvalidateFull();
        }

        public void ExecuteNavRight(bool extend)
        {
            _caretLineHint = -1;
            bool inCell = IsInCell(_caretPara);
            int len = GetVmAt(_caretPara)?.PlainText?.Length ?? 0;

            if (HasSel() && !extend)
            { var (_, _, ep, ec) = NormalizeSelection(); _caretPara = ep; _caretChar = ec; }
            else if (_caretChar < len)
                _caretChar++;
            else if (_caretPara < _layouts.Count - 1 && !inCell)
            { _caretPara++; _caretChar = 0; }
            // В ячейке: не выходим за конец через стрелки

            SnapCaretToCorrectSlice();
            if (!extend) SyncSel(); else ExtendSel();
            UpdatePreferredX();
            ResetCaret(); InvalidateFull();
        }

        public void ExecuteNavUp(bool extend)
        {
            _caretLineHint = -1;
            MoveCaretVertically(-1);
            SnapCaretToCorrectSlice();
            if (!extend) SyncSel(); else ExtendSel();
            ResetCaret(); InvalidateFull();
        }

        public void ExecuteNavDown(bool extend)
        {
            _caretLineHint = -1;
            MoveCaretVertically(+1);
            SnapCaretToCorrectSlice();
            if (!extend) SyncSel(); else ExtendSel();
            ResetCaret(); InvalidateFull();
        }

        public void ExecuteHome(bool document, bool extend)
        {
            if (document) { _caretPara = 0; _caretChar = 0; }
            else
            {
                var layout = GetLayoutAt(_caretPara);
                if (layout is not null)
                {
                    int li = layout.GetLineIndexForChar(_caretChar);
                    _caretChar = li >= 0 && li < layout.Lines.Count
                        ? layout.Lines[li].FirstCharIndex : 0;
                }
                else _caretChar = 0;
            }
            SnapCaretToCorrectSlice();
            if (!extend) SyncSel(); else ExtendSel();
            UpdatePreferredX();
            ResetCaret(); InvalidateFull();
        }

        public void ExecuteEnd(bool document, bool extend)
        {
            if (document)
            {
                _caretPara = _layouts.Count - 1;
                _caretChar = GetVmAt(_caretPara)?.PlainText?.Length ?? 0;
            }
            else
            {
                int len = GetVmAt(_caretPara)?.PlainText?.Length ?? 0;
                var layout = GetLayoutAt(_caretPara);
                if (layout is not null)
                {
                    int li = layout.GetLineIndexForChar(_caretChar);
                    _caretChar = li >= 0 && li < layout.Lines.Count
                        ? layout.Lines[li].LastCharIndex + 1 : len;
                }
                else _caretChar = len;
            }
            SnapCaretToCorrectSlice();
            if (!extend) SyncSel(); else ExtendSel();
            UpdatePreferredX();
            ResetCaret(); InvalidateFull();
        }

        public void ExecuteSelectAll()
        {
            if (_layouts.Count == 0) return;
            _selStartPara = 0; _selStartChar = 0;
            _selEndPara = _layouts.Count - 1;
            _selEndChar = GetVmAt(_layouts.Count - 1)?.PlainText?.Length ?? 0;
            _caretPara = _selEndPara; _caretChar = _selEndChar;
            SnapCaretToCorrectSlice();
            UpdatePreferredX();
            InvalidateFull();
        }

        public void ExecuteCopy() => _ = CopyAsync();
        public void ExecuteCut() => _ = CutAsync();
        public void ExecutePaste() => _ = PasteAsync();

        public void ExecuteUndo()
        {
            UndoStack?.Undo();
            ClampCaret(); SyncSel(); ResetCaret(); InvalidateFull();
        }

        public void ExecuteRedo()
        {
            UndoStack?.Redo();
            ClampCaret(); SyncSel(); ResetCaret(); InvalidateFull();
        }

        // ── Таблица — структурные операции ────────────────────────────────
        private void ExecuteTableAddRow(bool above)
        {
            if (_activeTableBlock is null) return;
            int insertRow = above ? _activeCellRow : _activeCellRow + 1;
            foreach (var cell in _activeTableBlock.Cells)
                if (cell.Row >= insertRow) cell.Row++;
            for (int c = 0; c < _activeTableBlock.ColumnCount; c++)
                _activeTableBlock.Cells.Add(new TableCell { Row = insertRow, Column = c });
            _activeTableBlock.RowCount++;
            if (above) _activeCellRow++;
            _cellLayoutCache.Clear();
            RebuildLayouts();
            InvalidateFull();
        }

        private void ExecuteTableDeleteRow()
        {
            if (_activeTableBlock is null) return;
            int deleteRow = _activeCellRow;
            _activeTableBlock.Cells.RemoveAll(c => c.Row == deleteRow);
            foreach (var cell in _activeTableBlock.Cells)
                if (cell.Row > deleteRow) cell.Row--;
            _activeTableBlock.RowCount--;
            if (_activeTableBlock.RowCount <= 0) { ExecuteTableDelete(); return; }
            _activeCellRow = Clamp(_activeCellRow, 0, _activeTableBlock.RowCount - 1);
            _cellLayoutCache.Clear();
            RebuildLayouts();
            InvalidateFull();
        }

        private void ExecuteTableAddColumn(bool left)
        {
            if (_activeTableBlock is null) return;
            int insertCol = left ? _activeCellCol : _activeCellCol + 1;
            foreach (var cell in _activeTableBlock.Cells)
                if (cell.Column >= insertCol) cell.Column++;
            for (int r = 0; r < _activeTableBlock.RowCount; r++)
                _activeTableBlock.Cells.Add(new TableCell { Row = r, Column = insertCol });
            var colDef = new TableColumnDefinition { WidthType = TableColumnWidthType.Auto };
            if (insertCol < _activeTableBlock.Columns.Count)
                _activeTableBlock.Columns.Insert(insertCol, colDef);
            else
                _activeTableBlock.Columns.Add(colDef);
            _activeTableBlock.ColumnCount++;
            if (left) _activeCellCol++;
            _cellLayoutCache.Clear();
            RebuildLayouts();
            InvalidateFull();
        }

        private void ExecuteTableDeleteColumn()
        {
            if (_activeTableBlock is null) return;
            int deleteCol = _activeCellCol;
            _activeTableBlock.Cells.RemoveAll(c => c.Column == deleteCol);
            foreach (var cell in _activeTableBlock.Cells)
                if (cell.Column > deleteCol) cell.Column--;
            if (deleteCol < _activeTableBlock.Columns.Count)
                _activeTableBlock.Columns.RemoveAt(deleteCol);
            _activeTableBlock.ColumnCount--;
            if (_activeTableBlock.ColumnCount <= 0) { ExecuteTableDelete(); return; }
            _activeCellCol = Clamp(_activeCellCol, 0, _activeTableBlock.ColumnCount - 1);
            _cellLayoutCache.Clear();
            RebuildLayouts();
            InvalidateFull();
        }

        private void ExecuteTableDelete()
        {
            if (_activeTableBlock is null || DocVm is null) return;
            DocVm.Document.Sections[0].Blocks.Remove(_activeTableBlock);
            _cellVmCache.Clear();
            _cellLayoutCache.Clear();
            DocVm.RebuildParagraphViewModelsPublic();
            NotifyLeftCell();
            _caretPara = Clamp(_caretPara, 0, Math.Max(0, _layouts.Count - 1));
            _caretChar = 0;
            RebuildLayouts();
            InvalidateFull();
        }

        // ── Вертикальная навигация ────────────────────────────────────────
        private void MoveCaretVertically(int dir)
        {
            bool inCell = IsInCell(_caretPara);
            var layout = GetLayoutAt(_caretPara);
            if (layout is null)
            {
                if (!inCell)
                {
                    _caretPara = Clamp(_caretPara + dir, 0, _layouts.Count - 1);
                    _caretChar = Clamp(_caretChar, 0, GetVmAt(_caretPara)?.PlainText?.Length ?? 0);
                }
                return;
            }

            int lineIdx = layout.GetLineIndexForChar(_caretChar);
            int targetLine = lineIdx + dir;

            if (targetLine >= 0 && targetLine < layout.Lines.Count)
            {
                _caretChar = layout.GetCharIndexForVerticalMove(
                    _caretChar, dir, _preferredCaretXPt);
                return;
            }

            if (inCell)
            {
                // В ячейке: переходим на параграф выше/ниже в той же ячейке
                var cell = GetCurrentCell()!;
                int newParaIdx = _caretPara + dir;
                if (newParaIdx >= 0 && newParaIdx < _layouts.Count)
                {
                    var next = _layouts[newParaIdx];
                    if (next.Cell?.Cell == cell.Cell)
                    {
                        _caretPara = newParaIdx;
                        var nextLayout = next.Layout;
                        if (nextLayout.Lines.Count > 0)
                        {
                            var fl = dir > 0 ? nextLayout.Lines[0] : nextLayout.Lines[^1];
                            var hit = nextLayout.HitTestPoint(
                                _preferredCaretXPt - nextLayout.LeftIndentPt,
                                fl.Y + fl.Height * 0.5f);
                            _caretChar = hit.CharIndex;
                        }
                        else _caretChar = 0;
                        return;
                    }
                }
                // Упёрлись в край ячейки — ничего не делаем
                return;
            }

            // Обычный параграф
            if (dir < 0 && _caretPara > 0)
            {
                _caretPara--;
                var prev = GetLayoutAt(_caretPara);
                if (prev is not null && prev.Lines.Count > 0)
                {
                    var ll = prev.Lines[^1];
                    var hit = prev.HitTestPoint(
                        _preferredCaretXPt - prev.LeftIndentPt,
                        ll.Y + ll.Height * 0.5f);
                    _caretChar = hit.CharIndex;
                }
                else _caretChar = GetVmAt(_caretPara)?.PlainText?.Length ?? 0;
            }
            else if (dir > 0 && _caretPara < _layouts.Count - 1)
            {
                _caretPara++;
                var next = GetLayoutAt(_caretPara);
                if (next is not null && next.Lines.Count > 0)
                {
                    var fl = next.Lines[0];
                    var hit = next.HitTestPoint(
                        _preferredCaretXPt - next.LeftIndentPt,
                        fl.Y + fl.Height * 0.5f);
                    _caretChar = hit.CharIndex;
                }
                else _caretChar = 0;
            }
        }

        private void ClampCaret()
        {
            _caretPara = Clamp(_caretPara, 0, Math.Max(0, _layouts.Count - 1));
            _caretChar = Clamp(_caretChar, 0, GetVmAt(_caretPara)?.PlainText?.Length ?? 0);
        }

        private void UpdatePreferredX()
        {
            var layout = GetLayoutAt(_caretPara);
            if (layout is null) return;
            var caret = layout.HitTestPosition(_caretChar);
            _preferredCaretXPt = caret.X;
        }

        private void SnapCaretToCorrectSlice()
        {
            if (_layouts.Count == 0) return;
            _caretPara = Clamp(_caretPara, 0, _layouts.Count - 1);

            var targetVm = GetVmAt(_caretPara);
            if (targetVm is null) return;

            var layout = GetLayoutAt(_caretPara);
            if (layout is null) return;

            int lineIdx = layout.GetLineIndexForChar(_caretChar);

            if (_caretLineHint >= 0)
            {
                for (int i = 0; i < _layouts.Count; i++)
                {
                    var pl = _layouts[i];
                    if (pl.Vm == targetVm
                        && _caretLineHint >= pl.LineFrom
                        && _caretLineHint < pl.LineTo)
                    {
                        _caretPara = i;
                        return;
                    }
                }
            }

            var currentPl = _layouts[_caretPara];
            if (currentPl.Vm == targetVm && lineIdx >= currentPl.LineFrom && lineIdx < currentPl.LineTo)
                return;

            for (int i = 0; i < _layouts.Count; i++)
            {
                var pl = _layouts[i];
                if (pl.Vm != targetVm) continue;
                if (lineIdx >= pl.LineFrom && lineIdx < pl.LineTo)
                {
                    _caretPara = i;
                    return;
                }
            }
        }

        // ── Undo ─────────────────────────────────────────────────────────
        private void BeginEdit(string description)
        {
            if (DocVm is null) return;
            _pendingSnapshot = new DocumentSnapshotCommand(DocVm, description);
        }

        private void CommitEdit()
        {
            if (_pendingSnapshot is null || UndoStack is null) return;
            _pendingSnapshot.Commit();
            UndoStack.Push(_pendingSnapshot);
            _pendingSnapshot = null;
        }

        // ── Selection ────────────────────────────────────────────────────
        private bool HasSel() =>
            _selStartPara != _selEndPara || _selStartChar != _selEndChar;

        private (int sp, int sc, int ep, int ec) NormalizeSelection()
        {
            // Используем layout-индексы напрямую — работает и для ячеек и для параграфов
            if (_selStartPara < _selEndPara)
                return (_selStartPara, _selStartChar, _selEndPara, _selEndChar);
            if (_selStartPara > _selEndPara)
                return (_selEndPara, _selEndChar, _selStartPara, _selStartChar);
            if (_selStartChar <= _selEndChar)
                return (_selStartPara, _selStartChar, _selEndPara, _selEndChar);
            return (_selEndPara, _selEndChar, _selStartPara, _selStartChar);
        }

        private void SyncSel()
        {
            _selStartPara = _caretPara; _selStartChar = _caretChar;
            _selEndPara = _caretPara; _selEndChar = _caretChar;
        }

        private void ExtendSel()
        {
            _selEndPara = _caretPara;
            _selEndChar = _caretChar;
        }

        private void DeleteSelection()
        {
            if (!HasSel()) return;
            var (sp, sc, ep, ec) = NormalizeSelection();
            var sVm = GetVmAt(sp);
            var eVm = GetVmAt(ep);
            if (sVm is null || eVm is null) return;

            if (sVm == eVm)
            {
                string t = sVm.PlainText ?? "";
                int s2 = Clamp(sc, 0, t.Length);
                int e2 = Clamp(ec, 0, t.Length);
                sVm.PlainText = t[..s2] + t[e2..];
                _caretChar = s2;
            }
            else if (!IsInCell(sp) && !IsInCell(ep))
            {
                // Обычное межпараграфное удаление
                string st = sVm.PlainText ?? "";
                string et = eVm.PlainText ?? "";
                int s2 = Clamp(sc, 0, st.Length);
                int e2 = Clamp(ec, 0, et.Length);

                int si = DocVm?.Paragraphs.IndexOf(sVm) ?? 0;
                int ei = DocVm?.Paragraphs.IndexOf(eVm) ?? 0;

                var toDelete = new List<ParagraphViewModel>();
                for (int di = ei; di > si; di--)
                    if (di < (DocVm?.Paragraphs.Count ?? 0))
                        toDelete.Add(DocVm!.Paragraphs[di]);

                sVm.PlainText = st[..s2] + et[e2..];
                foreach (var p in toDelete) DocVm?.DeleteParagraph(p);
                _caretChar = s2;
            }

            _caretPara = sp;
            SyncSel();
            SnapCaretToCorrectSlice();
            UpdatePreferredX();
        }

        // ── Clipboard ────────────────────────────────────────────────────
        private async Task CopyAsync()
        {
            if (!HasSel()) return;
            var (sp, sc, ep, ec) = NormalizeSelection();
            var sVm = GetVmAt(sp);
            var eVm = GetVmAt(ep);
            if (sVm is null || eVm is null) return;

            var lines = new List<string>();
            var seenVms = new HashSet<ParagraphViewModel>();

            for (int i = sp; i <= ep && i < _layouts.Count; i++)
            {
                var pvm = GetVmAt(i);
                if (pvm is null || !seenVms.Add(pvm)) continue;

                string t = pvm.PlainText ?? "";
                int from = (i == sp) ? Clamp(sc, 0, t.Length) : 0;
                int to = (i == ep) ? Clamp(ec, 0, t.Length) : t.Length;
                if (from > to) to = from;
                lines.Add(t[from..to]);
            }

            string result = string.Join(Environment.NewLine, lines);
            _clipboardCache = result;

            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
            {
#pragma warning disable CS0618
                await clipboard.SetTextAsync(result);
#pragma warning restore CS0618
            }
        }

        private async Task CutAsync()
        {
            BeginEdit("Cut");
            await CopyAsync();
            DeleteSelection();
            CommitEdit();
            SnapCaretToCorrectSlice();
            UpdatePreferredX();
            SyncSel(); ResetCaret(); InvalidateFull();
        }

        private async Task PasteAsync()
        {
            string? text = _clipboardCache;
            if (string.IsNullOrEmpty(text))
            {
                var cb = TopLevel.GetTopLevel(this)?.Clipboard;
                if (cb is null) return;
#pragma warning disable CS0618
                text = await cb.GetTextAsync();
#pragma warning restore CS0618
            }
            if (string.IsNullOrEmpty(text)) return;

            _ = PrefetchClipboardAsync();

            if (IsInCell(_caretPara))
            {
                // Вставка в ячейку — только первая строка (без разбиения ячейки на параграфы для простоты TODO)
                string firstLine = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n')[0];
                CellInsertText(firstLine);
                return;
            }

            BeginEdit("Paste");
            DeleteSelection();

            string[] lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            var pvm = GetVmAt(_caretPara);
            if (pvm is null) return;

            string cur = pvm.PlainText ?? "";
            int pos = Clamp(_caretChar, 0, cur.Length);
            string before = cur[..pos];
            string after = cur[pos..];

            if (lines.Length == 1)
            {
                pvm.PlainText = before + lines[0] + after;
                _caretChar = pos + lines[0].Length;
            }
            else
            {
                pvm.PlainText = before + lines[0];
                var prev = pvm;
                for (int i = 1; i < lines.Length - 1; i++)
                {
                    var nv = DocVm?.AddParagraphAfter(prev);
                    if (nv is not null) { nv.PlainText = lines[i]; prev = nv; }
                }
                var last = DocVm?.AddParagraphAfter(prev);
                if (last is not null)
                {
                    last.PlainText = lines[^1] + after;
                    _caretPara = DocVm!.Paragraphs.IndexOf(last);
                    _caretChar = lines[^1].Length;
                }
            }

            CommitEdit();
            SnapCaretToCorrectSlice();
            UpdatePreferredX();
            SyncSel();
            ResetCaret();
        }

        // ── HitTest ───────────────────────────────────────────────────────
        /// <summary>
        /// Единый HitTest для всех элементов _layouts (параграфы и ячейки таблиц).
        /// Использует pl.AbsXPt — абсолютный X начала текстовой зоны.
        /// </summary>
        private (int parIdx, int charIdx) HitTest(Point ptLogPx)
        {
            List<ParaLayout> layouts;
            lock (_renderLock) { layouts = _layouts; }

            if (layouts.Count == 0) return (0, 0);

            double zoom = Zoom;
            float xPt = (float)(ptLogPx.X / zoom * PxToPt);
            float yPt = (float)(ptLogPx.Y / zoom * PxToPt);

            // ── Двухпроходной поиск ───────────────────────────────────────
            // Проход 1: находим минимальное Y-расстояние.
            float bestYDist = float.MaxValue;
            for (int i = 0; i < layouts.Count; i++)
            {
                var pl = layouts[i];
                float top = pl.Ypt;
                float bot = pl.Ypt + pl.HeightPt;
                float dist = yPt < top ? top - yPt : yPt > bot ? yPt - bot : 0f;
                if (dist < bestYDist) bestYDist = dist;
            }

            // Проход 2: среди всех кандидатов с минимальным Y-расстоянием
            // выбираем тот, чей X-диапазон [AbsXPt .. AbsXPt + layoutWidth] ближайший к клику.
            // Это решает проблему таблиц: ячейки одной строки имеют dist==0 по Y,
            // и без X-проверки всегда выбирается первая (самая левая) ячейка.
            int bestIdx = 0;
            float bestXDist = float.MaxValue;

            for (int i = 0; i < layouts.Count; i++)
            {
                var pl = layouts[i];
                float top = pl.Ypt;
                float bot = pl.Ypt + pl.HeightPt;
                float yDist = yPt < top ? top - yPt : yPt > bot ? yPt - bot : 0f;

                if (Math.Abs(yDist - bestYDist) > 0.5f) continue; // не с минимальным Y

                // Ширина текстовой зоны: для ячейки — ширина ячейки из CellInfo,
                // для параграфа — полная ширина через layout.
                float layoutW;
                if (pl.Cell != null)
                    layoutW = pl.Cell.ClipW;  // ширина клип-прямоугольника ячейки
                else
                    layoutW = pl.Layout.LeftIndentPt + pl.Layout.RightIndentPt
                              + (pl.Layout.Lines.Count > 0
                                  ? pl.Layout.Lines.Max(l => l.Segments.Count > 0
                                      ? l.Segments[^1].X + l.Segments[^1].Width : 0f)
                                  : 100f);

                float xLeft = pl.AbsXPt;
                float xRight = pl.AbsXPt + layoutW;
                float xDist = xPt < xLeft ? xLeft - xPt
                             : xPt > xRight ? xPt - xRight
                             : 0f;

                if (xDist < bestXDist)
                {
                    bestXDist = xDist;
                    bestIdx = i;
                }
            }

            var best = layouts[bestIdx];
            float padXPt = best.AbsXPt;

            float yBase = best.LineFrom < best.Layout.Lines.Count
                ? best.Layout.Lines[best.LineFrom].Y : 0f;

            float localX = xPt - padXPt - best.Layout.LeftIndentPt;
            float localY = yPt - best.Ypt + yBase;

            if (best.LineFrom < best.Layout.Lines.Count)
            {
                float fy = best.Layout.Lines[best.LineFrom].Y;
                int lto = best.LineTo > 0 && best.LineTo <= best.Layout.Lines.Count
                    ? best.LineTo : best.Layout.Lines.Count;
                float ly = best.Layout.Lines[lto - 1].Y + best.Layout.Lines[lto - 1].Height;
                localY = Clamp(localY, fy + 0.1f, ly - 0.1f);
            }

            float hitX = localX;
            if (best.LineFrom == 0
                && best.Layout.FirstLineIndentPt != 0
                && best.Layout.Lines.Count > 0)
            {
                float line0Bottom = best.Layout.Lines[0].Y + best.Layout.Lines[0].Height;
                if (localY <= line0Bottom)
                    hitX -= best.Layout.FirstLineIndentPt;
            }

            var hit = best.Layout.HitTestPoint(hitX, localY);

            _caretLineHint = -1;
            for (int li = best.LineFrom; li < Math.Min(best.LineTo, best.Layout.Lines.Count); li++)
            {
                var ln = best.Layout.Lines[li];
                if (localY <= ln.Y + ln.Height) { _caretLineHint = li; break; }
            }

            return (bestIdx, hit.CharIndex);
        }

        // ── Scroll to caret ───────────────────────────────────────────────
        private void ScrollToCaret()
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_caretPara < 0 || _caretPara >= _layouts.Count) return;

                double zoom = Zoom;
                var pl = _layouts[_caretPara];
                int pos = Clamp(_caretChar, 0, pl.Vm.PlainText?.Length ?? 0);
                var caret = pl.Layout.HitTestPosition(pos);

                float yBase = pl.LineFrom < pl.Layout.Lines.Count
                    ? pl.Layout.Lines[pl.LineFrom].Y : 0f;

                // Используем AbsXPt который одинаково правильный для ячеек и параграфов
                double xPx = (pl.AbsXPt + caret.X) * PtToPx * zoom;
                double yPx = (pl.Ypt + (caret.Y - yBase)) * PtToPx * zoom;
                double hPx = caret.Height * PtToPx * zoom;

                this.BringIntoView(new Rect(xPx - 10, yPx - 10, 20, hPx + 20));
            }, DispatcherPriority.Render);
        }

        // ── Helpers ───────────────────────────────────────────────────────
        private ParagraphViewModel? GetVmAt(int idx) =>
            idx >= 0 && idx < _layouts.Count ? _layouts[idx].Vm : null;

        private ParagraphViewModel? GetVmAt(int idx, List<ParaLayout> layouts) =>
            idx >= 0 && idx < layouts.Count ? layouts[idx].Vm : null;

        private SKTextLayout? GetLayoutAt(int idx) =>
            idx >= 0 && idx < _layouts.Count ? _layouts[idx].Layout : null;

        private int FindFirstSliceForDocVmParagraph(int paragraphIndex)
        {
            if (paragraphIndex < 0 || DocVm is null) return 0;
            if (paragraphIndex >= DocVm.Paragraphs.Count) return _layouts.Count - 1;
            var target = DocVm.Paragraphs[paragraphIndex];
            for (int i = 0; i < _layouts.Count; i++)
                if (_layouts[i].Vm == target) return i;
            return 0;
        }

        private void InvalidateFull()
        {
            _caretOnlyRedraw = false;
            InvalidateVisual();
        }

        private void ResetCaret()
        {
            _caretVisible = true;
            _caretTimer.Stop();
            _caretTimer.Start();
            ScrollToCaret();

            // Уведомляем вертикальную линейку о странице каретки
            if (_caretPara >= 0 && _caretPara < _layouts.Count)
                CaretPageChanged?.Invoke(_layouts[_caretPara].PageIndex);
        }

        private static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;
        private static float Clamp(float v, float min, float max) => v < min ? min : v > max ? max : v;

        private void UpdateSelectionContext()
        {
            if (DocVm is null) return;
            DocVm.SelectionParagraphs.Clear();
            if (!HasSel()) return;

            var (sp, _, ep, _) = NormalizeSelection();
            var seen = new HashSet<ParagraphViewModel>();
            for (int i = sp; i <= ep && i < _layouts.Count; i++)
            {
                var pvm = GetVmAt(i);
                // Добавляем только VM из DocVm.Paragraphs (не ячеечные)
                if (pvm is not null && seen.Add(pvm) && DocVm.Paragraphs.Contains(pvm))
                    DocVm.SelectionParagraphs.Add(pvm);
            }
        }

        public (int docParaIdx, int charIdx, double scrollY) GetCaretState()
        {
            int docIdx = 0;
            if (_caretPara >= 0 && _caretPara < _layouts.Count
                && DocVm is not null && !IsInCell(_caretPara))
            {
                int idx = DocVm.Paragraphs.IndexOf(_layouts[_caretPara].Vm);
                if (idx >= 0) docIdx = idx;
            }
            return (docIdx, _caretChar, _scrollOffsetY);
        }

        public void RestoreCaretState(int docParaIdx, int charIdx)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_layouts.Count == 0) return;
                _caretPara = FindFirstSliceForDocVmParagraph(docParaIdx);
                _caretChar = Clamp(charIdx, 0, GetVmAt(_caretPara)?.PlainText?.Length ?? 0);
                SnapCaretToCorrectSlice();
                UpdatePreferredX();
                SyncSel();
                ResetCaret();
                var pvm = GetVmAt(_caretPara);
                if (pvm is not null && DocVm?.Paragraphs.Contains(pvm) == true)
                    DocVm?.SetActiveParagraph(pvm);
                UpdateSelectionContext();
                Focus();
                InvalidateFull();
            }, DispatcherPriority.Loaded);
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