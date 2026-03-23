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

        // ── Layout параграфов ─────────────────────────────────────────────
        private record ParaLayout(
            ParagraphViewModel Vm,
            SKTextLayout Layout,
            float Ypt,
            float HeightPt,
            int PageIndex,
            int LineFrom,
            int LineTo);

        private record PageRect(
            float Ypt,
            float WidthPt,
            float HeightPt,
            float PadLeftPt,
            float PadTopPt,
            float MarginLeftPt);

        // ── Layout таблиц ─────────────────────────────────────────────────
        private record TableEntry(
            TableBlock Table,
            SKTableLayout Layout,
            float Ypt,
            float XPt,
            int PageIndex);

        // ── Режим каретки ─────────────────────────────────────────────────
        private enum CaretMode { Normal, Table }

        // ── Атомарный снимок для render-потока ────────────────────────────
        private readonly object _renderLock = new();
        private List<ParaLayout> _layouts = new();
        private List<PageRect> _pages = new();
        private List<TableEntry> _tables = new();
        private double _canvasWidth;
        private double _canvasHeight;
        private float _canvasHeightPt;

        // ── Кеш лейаутов ─────────────────────────────────────────────────
        private readonly Dictionary<ParagraphViewModel,
            (string Text, float Width, SKTextLayout Layout)> _layoutCache = new();

        // ── Дебаунс пересчёта ─────────────────────────────────────────────
        private System.Threading.CancellationTokenSource _rebuildCts = new();

        // ── Виртуализация ─────────────────────────────────────────────────
        private ScrollViewer? _parentScrollViewer;
        private double _scrollOffsetY = 0;
        private double _viewportHeight = 600;

        // ── Каретка — обычный режим ───────────────────────────────────────
        private CaretMode _caretMode = CaretMode.Normal;
        private int _caretPara = 0;
        private int _caretChar = 0;
        private int _caretLineHint = -1; // строка кликнутая мышью; -1 = не задано
        private bool _caretVisible = true;
        private float _preferredCaretXPt = 0f;
        private readonly DispatcherTimer _caretTimer;

        // ── Каретка — таблица ─────────────────────────────────────────────
        private int _tableCaretRow = 0;
        private int _tableCaretCol = 0;
        private int _tableCaretPara = 0;
        private int _tableCaretChar = 0;
        private int _tableEntryIdx = -1;
        private TableBlock? _activeTableBlock;

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

        // ── Callbacks для TextEditorView ──────────────────────────────────

        public Action<double>? RecommendedZoomChanged { get; set; }

        private double _lastPageOffsetXPx = 0;
        private Action<double>? _pageOffsetXChanged;

        public Action<double>? PageOffsetXChanged
        {
            get => _pageOffsetXChanged;
            set
            {
                _pageOffsetXChanged = value;
                value?.Invoke(_lastPageOffsetXPx);
            }
        }

        public Action<IReadOnlyList<double>, IReadOnlyList<double>>? CaretEnteredTable { get; set; }
        public Action? CaretLeftTable { get; set; }

        /// <summary>
        /// Вызывается с UI-потока всякий раз когда позиция каретки или скролл изменились.
        /// TextEditorModule подписывается чтобы обновлять кеш сессионных данных
        /// без обращения к visual tree из фонового потока.
        /// Параметры: (docParaIdx, charIdx, scrollOffsetY).
        /// </summary>
        public Action<int, int, double>? CaretStateChanged { get; set; }

        public double MonitorSizeInches
        {
            get => _monitorSizeInches;
            set
            {
                if (Math.Abs(_monitorSizeInches - value) < 0.01) return;
                _logger.Debug("MonitorSizeInches: {Old} -> {New}", _monitorSizeInches, value);
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

        public void SetHotKeyService(IHotKeyService service)
        {
            _hotKeyService = service;
        }

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

            _logger.Debug("DPI recalculated: physW={W} physH={H} diagPx={D} dpi={DPI}",
                physW, physH, diagPx, _cachedDpi);

            Dispatcher.UIThread.Post(() => RecommendedZoomChanged?.Invoke(RecommendedZoom));
        }

        public double RecommendedZoom => _cachedDpi > 0 ? _cachedDpi / 96.0 : 1.0;

        private static float MmToPt(double mm) => (float)(mm * 72.0 / 25.4);
        private static double PtToMm(float pt) => pt * 25.4 / 72.0;

        private float GetPageWidthPt()
        {
            var ps = DocVm?.Document.PageSettings;
            if (ps is null) return MmToPt(210);
            return ps.Orientation == PageOrientation.Landscape
                ? MmToPt(ps.HeightMm) : MmToPt(ps.WidthMm);
        }

        private float GetPageHeightPt()
        {
            var ps = DocVm?.Document.PageSettings;
            if (ps is null) return MmToPt(297);
            return ps.Orientation == PageOrientation.Landscape
                ? MmToPt(ps.WidthMm) : MmToPt(ps.HeightMm);
        }

        private (float left, float top, float right, float bottom) GetPagePaddingPt()
        {
            var ps = DocVm?.Document.PageSettings;
            if (ps is null) return (MmToPt(20), MmToPt(20), MmToPt(20), MmToPt(20));
            return (
                MmToPt(ps.MarginLeftMm + ps.MarginGutterMm),
                MmToPt(ps.MarginTopMm),
                MmToPt(ps.MarginRightMm),
                MmToPt(ps.MarginBottomMm));
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
                    _logger.Debug("ScrollViewer subscribed: viewportH={H}", _viewportHeight);
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

            _logger.Debug("DataContextChanged: docVm={HasVm}", _docVm is not null);

            if (DocVm is not null)
            {
                _styleResolver = new StyleResolver(DocVm.Document.Styles);
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
            RebuildLayouts();
            // После перестройки лейаутов текст мог перенестись на другие строки.
            // Без этих вызовов каретка остаётся на старом слайсе → визуально
            // оказывается в неверной позиции.
            SnapCaretToCorrectSlice();
            UpdatePreferredX();
            InvalidateFull();
        }

        private void OnDocVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(DocumentViewModel.ViewMode)
                               or nameof(DocumentViewModel.Zoom)
                               or nameof(DocumentViewModel.PageSettings))
            {
                _logger.Debug("DocVm property changed: {Prop}", e.PropertyName);
                if (DocVm is not null)
                    _styleResolver = new StyleResolver(DocVm.Document.Styles);
                _layoutCache.Clear();
                InvalidateMeasure();
            }
        }

        private void OnParagraphsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            _logger.Debug("OnParagraphsChanged: action={A} newStart={NS} oldStart={OS}",
                e.Action, e.NewStartingIndex, e.OldStartingIndex);

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
                _caretPara = FindFirstSliceForParagraphIndex(idx);
                _caretChar = pvm.PlainText?.Length ?? 0;
                SwitchToNormalMode();
                SnapCaretToCorrectSlice();
                UpdatePreferredX();
                SyncSel(); ResetCaret(); InvalidateVisual();
            };

            pvm.RequestFocusAtPosition = pos =>
            {
                if (DocVm is null) return;
                int idx = DocVm.Paragraphs.IndexOf(pvm);
                if (idx < 0) return;
                _caretPara = FindFirstSliceForParagraphIndex(idx);
                _caretChar = Clamp(pos, 0, pvm.PlainText?.Length ?? 0);
                SwitchToNormalMode();
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
            _logger.Debug("ScheduleRebuild: paraIdx={Idx}", dirtyParaIdx);

            if (DocVm is not null && dirtyParaIdx < DocVm.Paragraphs.Count)
                _layoutCache.Remove(DocVm.Paragraphs[dirtyParaIdx]);

            _rebuildCts.Cancel();
            _rebuildCts = new System.Threading.CancellationTokenSource();
            var cts = _rebuildCts;

            InvalidateFull();

            Dispatcher.UIThread.Post(() =>
            {
                if (cts.IsCancellationRequested)
                {
                    _logger.Debug("ScheduleRebuild: cancelled");
                    return;
                }

                _logger.Debug("ScheduleRebuild: executing full rebuild");

                double oldCanvasH = _canvasHeight;
                RebuildLayouts();

                // Snap после rebuild с актуальными лейаутами.
                // Hint уже сброшен в OnTextInput — snap использует стандартный lineIdx.
                SnapCaretToCorrectSlice();
                _caretLineHint = -1; // сбрасываем — hint одноразовый, только для первого snap
                UpdatePreferredX();
                SyncSel();

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

            _logger.Debug("MeasureOverride: availW={A} viewportW={V} canvasWidth={C} zoom={Z}",
                availW, viewportW, _canvasWidth, zoom);

            if (_styleResolver is null && DocVm is not null)
                _styleResolver = new StyleResolver(DocVm.Document.Styles);

            _layoutCache.Clear();
            RebuildLayouts();

            double visualH = Math.Max(_canvasHeight * zoom, 100);
            double visualW = availW;

            if (DocVm?.ViewMode == EditorViewMode.Page)
                visualW = Math.Max(availW,
                    GetPageWidthPt() * PtToPx * zoom + PageGapPt * PtToPx * 4);

            _logger.Debug("MeasureOverride result: visualW={W} visualH={H}", visualW, visualH);
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
                _logger.Debug("ArrangeOverride: width changed {Old} -> {New}", _canvasWidth, logicalW);
                _canvasWidth = logicalW;
                _layoutCache.Clear();
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

            _logger.Debug("RebuildLayouts: mode={M} paragraphs={P} canvasWidth={W}",
                DocVm.ViewMode, DocVm.Paragraphs.Count, _canvasWidth);

            switch (DocVm.ViewMode)
            {
                case EditorViewMode.Page:
                    RebuildPageMode();
                    break;
                case EditorViewMode.Draft:
                case EditorViewMode.Web:
                    RebuildFlowMode(
                        (float)(_canvasWidth * PxToPt), DraftPadHPt, DraftPadWPt);
                    break;
                case EditorViewMode.Reading:
                    {
                        float cw = (float)(_canvasWidth * PxToPt);
                        RebuildFlowMode(Math.Min(cw, ReadingMaxPt), 18f,
                            (cw - Math.Min(cw, ReadingMaxPt)) / 2f);
                        break;
                    }
            }

            _logger.Debug("RebuildLayouts done: layouts={L} pages={P} tables={T} canvasH={H}",
                _layouts.Count, _pages.Count, _tables.Count, _canvasHeightPt);
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

            newPages.Add(new PageRect(pageYPt, pageWidthPt, pageHeightPt, pageXPt, mt, ml));

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
                    newPages.Add(new PageRect(pageYPt, pageWidthPt, pageHeightPt, pageXPt, mt, ml));
                    continue;
                }

                if (block is TableBlock tableBlock)
                {
                    var tableLayout = _renderer.BuildTableLayout(tableBlock, textWidthPt, _styleResolver!);
                    float tableH = tableLayout.TotalHeightPt;

                    if (contentYPt + tableH > pageBottomPt && contentYPt > pageYPt + mt)
                    {
                        pageYPt = pageYPt + pageHeightPt + PageGapPt;
                        pageBottomPt = pageYPt + pageHeightPt - mb;
                        contentYPt = pageYPt + mt;
                        pageIdx++;
                        newPages.Add(new PageRect(pageYPt, pageWidthPt, pageHeightPt, pageXPt, mt, ml));
                    }

                    newTables.Add(new TableEntry(tableBlock, tableLayout, contentYPt, textXPt, pageIdx));
                    contentYPt += tableH + FallbackLinePt;
                    continue;
                }

                if (block is not ParagraphBlock paraBlock) continue;

                ParagraphViewModel? pvm = null;
                foreach (var p in DocVm.Paragraphs)
                    if (p.Model == paraBlock) { pvm = p; break; }
                if (pvm is null) continue;

                var layout = GetOrBuildLayout(pvm, textWidthPt);
                if (layout.Lines.Count == 0) continue;

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
                                pageIdx, lineFrom, li));
                        }

                        pageYPt = pageYPt + pageHeightPt + PageGapPt;
                        pageBottomPt = pageYPt + pageHeightPt - mb;
                        contentYPt = pageYPt + mt;
                        pageIdx++;
                        newPages.Add(new PageRect(
                            pageYPt, pageWidthPt, pageHeightPt, pageXPt, mt, ml));

                        lineFrom = li;
                        lineGroupYPt = contentYPt;
                    }

                    contentYPt += line.Height;
                    if (isLast) contentYPt += layout.SpaceAfterPt;
                }

                newLayouts.Add(new ParaLayout(
                    pvm, layout, lineGroupYPt,
                    contentYPt - lineGroupYPt,
                    pageIdx, lineFrom, layout.Lines.Count));
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
                    newTables.Add(new TableEntry(tableBlock, tableLayout, yPt, padWPt, 0));
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
                    pvm, layout, yPt + layout.SpaceBeforePt, hPt, 0, 0, layout.Lines.Count));
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

            // BuildLayout внутри уже вычитает LeftIndent + RightIndent из widthPt,
            // поэтому передаём полную ширину текстовой зоны страницы без изменений.
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
                    DrawCaretOnCanvas(canvas, layouts, pages, tables, canvasWidth);
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
                    RenderPageMode(offscreen, layouts, pages, tables, canvasHeightPt, canvasWidth,
                        drawCaret: false);
                else
                    RenderFlowMode(offscreen, mode, layouts, tables, canvasHeightPt, canvasWidth,
                        drawCaret: false);

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
                    DrawCaretOnCanvas(canvas, layouts, pages, tables, canvasWidth);
                    canvas.Restore();
                }
            }
            else
            {
                canvas.Save();
                canvas.Scale(scale, scale);

                var mode = DocVm?.ViewMode ?? EditorViewMode.Draft;
                if (mode == EditorViewMode.Page)
                    RenderPageMode(canvas, layouts, pages, tables, canvasHeightPt, canvasWidth,
                        drawCaret: _caretVisible);
                else
                    RenderFlowMode(canvas, mode, layouts, tables, canvasHeightPt, canvasWidth,
                        drawCaret: _caretVisible);

                canvas.Restore();
            }
        }

        private void RenderPageMode(
            SKCanvas canvas,
            List<ParaLayout> layouts,
            List<PageRect> pages,
            List<TableEntry> tables,
            float canvasHeightPt,
            double canvasWidth,
            bool drawCaret = true)
        {
            float canvasWPt = (float)(canvasWidth * PxToPt);

            using var bgPaint = new SKPaint { Color = CanvasBgColor };
            canvas.DrawRect(0, 0, canvasWPt, canvasHeightPt, bgPaint);

            var (firstPage, lastPage) = GetVisiblePageRange(pages);

            for (int pi = firstPage; pi <= lastPage && pi < pages.Count; pi++)
            {
                var page = pages[pi];
                using var sh = new SKPaint { Color = PageShadowColor };
                canvas.DrawRect(page.PadLeftPt + 3, page.Ypt + 3,
                                page.WidthPt, page.HeightPt, sh);
                using var pg = new SKPaint { Color = SKColors.White };
                canvas.DrawRect(page.PadLeftPt, page.Ypt,
                                page.WidthPt, page.HeightPt, pg);
            }

            foreach (var te in tables)
            {
                if (te.PageIndex < firstPage || te.PageIndex > lastPage) continue;
                SKTextRenderer.RenderTable(canvas, te.Layout, te.XPt, te.Ypt);
            }

            for (int i = 0; i < layouts.Count; i++)
            {
                var pl = layouts[i];
                if (pl.PageIndex < firstPage || pl.PageIndex > lastPage) continue;
                if (pl.PageIndex >= pages.Count) continue;

                var page = pages[pl.PageIndex];
                float paraXPt = page.PadLeftPt + page.MarginLeftPt;
                float paraXPtWithIndent = paraXPt + pl.Layout.LeftIndentPt;
                float paraYPt = pl.Ypt;

                DrawSelectionForSlice(canvas, i, pl, paraXPt, paraYPt, layouts);
                SKTextRenderer.RenderParagraphLines(
                    canvas, pl.Layout, paraXPtWithIndent, paraYPt, pl.LineFrom, pl.LineTo);

                if (drawCaret && _caretMode == CaretMode.Normal && _caretPara == i)
                    DrawCaret(canvas, pl, paraXPt, paraYPt);
            }

            if (drawCaret && _caretMode == CaretMode.Table)
                DrawTableCaret(canvas, tables);

        }

        private void RenderFlowMode(
            SKCanvas canvas,
            EditorViewMode mode,
            List<ParaLayout> layouts,
            List<TableEntry> tables,
            float canvasHeightPt,
            double canvasWidth,
            bool drawCaret = true)
        {
            float canvasWPt = (float)(canvasWidth * PxToPt);

            using var bgPaint = new SKPaint { Color = SKColors.Transparent };
            canvas.DrawRect(0, 0, canvasWPt, canvasHeightPt, bgPaint);

            float padWPt = mode == EditorViewMode.Reading
                ? (canvasWPt - Math.Min(canvasWPt, ReadingMaxPt)) / 2f : DraftPadWPt;

            float zoom2 = (float)Zoom;
            float viewTopPt = (float)(_scrollOffsetY / zoom2 * PxToPt) - FallbackLinePt * 5f;
            float viewBotPt = (float)((_scrollOffsetY + Math.Max(_viewportHeight, 100))
                                        / zoom2 * PxToPt) + FallbackLinePt * 5f;

            foreach (var te in tables)
            {
                if (te.Ypt + te.Layout.TotalHeightPt < viewTopPt) continue;
                if (te.Ypt > viewBotPt) break;
                SKTextRenderer.RenderTable(canvas, te.Layout, te.XPt, te.Ypt);
            }

            for (int i = 0; i < layouts.Count; i++)
            {
                var pl = layouts[i];
                if (pl.Ypt + pl.HeightPt < viewTopPt) continue;
                if (pl.Ypt > viewBotPt) break;

                float paraXPt = padWPt;
                float paraXPtWithIndent = paraXPt + pl.Layout.LeftIndentPt;
                float paraYPt = pl.Ypt;

                DrawSelectionForSlice(canvas, i, pl, paraXPt, paraYPt, layouts);
                SKTextRenderer.RenderParagraphLines(
                    canvas, pl.Layout, paraXPtWithIndent, paraYPt, pl.LineFrom, pl.LineTo);

                if (drawCaret && _caretMode == CaretMode.Normal && _caretPara == i)
                    DrawCaret(canvas, pl, paraXPt, paraYPt);
            }

            if (drawCaret && _caretMode == CaretMode.Table)
                DrawTableCaret(canvas, tables);

        }

        private void DrawCaretOnCanvas(
            SKCanvas canvas,
            List<ParaLayout> layouts,
            List<PageRect> pages,
            List<TableEntry> tables,
            double canvasWidth)
        {
            if (!_caretVisible) return;

            if (_caretMode == CaretMode.Normal)
            {
                if (_caretPara < 0 || _caretPara >= layouts.Count) return;
                var pl = layouts[_caretPara];
                float paraX = GetParaX(pl, pages, canvasWidth);
                DrawCaret(canvas, pl, paraX, pl.Ypt);
            }
            else
            {
                DrawTableCaret(canvas, tables);
            }
        }

        private float GetParaX(ParaLayout pl, List<PageRect> pages, double canvasWidth)
        {
            var mode = DocVm?.ViewMode ?? EditorViewMode.Draft;
            if (mode == EditorViewMode.Page && pl.PageIndex < pages.Count)
            {
                var page = pages[pl.PageIndex];
                return page.PadLeftPt + page.MarginLeftPt;
            }
            float canvasWPt = (float)(canvasWidth * PxToPt);
            if (mode == EditorViewMode.Reading)
                return (canvasWPt - Math.Min(canvasWPt, ReadingMaxPt)) / 2f;
            return DraftPadWPt;
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
            var startVm = GetVmAt(sp, layouts);
            var endVm = GetVmAt(ep, layouts);
            if (startVm is null || endVm is null) return;

            int startDocIdx = DocVm?.Paragraphs.IndexOf(startVm) ?? -1;
            int endDocIdx = DocVm?.Paragraphs.IndexOf(endVm) ?? -1;
            int thisDocIdx = DocVm?.Paragraphs.IndexOf(pl.Vm) ?? -1;

            if (thisDocIdx < 0 || thisDocIdx < startDocIdx || thisDocIdx > endDocIdx) return;

            int len = pl.Vm.PlainText?.Length ?? 0;
            int from = startDocIdx == endDocIdx ? sc
                     : thisDocIdx == startDocIdx ? sc : 0;
            int to = startDocIdx == endDocIdx ? ec
                     : thisDocIdx == endDocIdx ? ec : len;

            from = Clamp(from, 0, len);
            to = Clamp(to, 0, len);
            if (from >= to)
            {
                if (from == to && len == 0)
                {
                    float yBase = pl.LineFrom < pl.Layout.Lines.Count
                        ? pl.Layout.Lines[pl.LineFrom].Y : 0f;
                    float lineH = pl.Layout.Lines.Count > 0
                        ? pl.Layout.Lines[0].Height : FallbackLinePt;
                    using var ep2 = new SKPaint { Color = SelectionColor };
                    canvas.DrawRect(xPt, yPt - yBase + yBase, 5f, lineH, ep2);
                }
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
                    r.Rect.Width, r.Rect.Height,
                    paint);
            }
        }

        private void DrawCaret(SKCanvas canvas, ParaLayout pl, float xPt, float yPt)
        {
            int pos = Clamp(_caretChar, 0, pl.Vm.PlainText?.Length ?? 0);

            float yBase = pl.LineFrom < pl.Layout.Lines.Count
                ? pl.Layout.Lines[pl.LineFrom].Y : 0f;

            // Если есть hint — рисуем каретку на строке hint даже если HitTestPosition
            // вернула бы следующую строку (boundary-позиция LastCharIndex+1).
            int drawLineIdx;
            SKCaretRect caret;

            if (_caretLineHint >= 0
                && _caretLineHint >= pl.LineFrom
                && _caretLineHint < Math.Min(pl.LineTo, pl.Layout.Lines.Count))
            {
                var hintLine = pl.Layout.Lines[_caretLineHint];
                // Если pos = LastCharIndex+1 этой строки — рисуем в её конце.
                if (pos > hintLine.LastCharIndex && !hintLine.IsLastLine)
                {
                    var lastSeg = hintLine.Segments.Count > 0 ? hintLine.Segments[^1] : null;
                    caret = new SKCaretRect
                    {
                        X = lastSeg != null
                            ? pl.Layout.LeftIndentPt + lastSeg.X + lastSeg.Width
                            : pl.Layout.LeftIndentPt,
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

            float firstLineX = (drawLineIdx == 0 && pl.LineFrom == 0)
                ? pl.Layout.FirstLineIndentPt : 0f;

            using var paint = new SKPaint
            {
                Color = SKColors.Black,
                StrokeWidth = 1.1f,
                IsAntialias = false
            };

            float cx = xPt + caret.X + firstLineX;
            float cy = yPt + (caret.Y - yBase);
            canvas.DrawLine(cx, cy, cx, cy + caret.Height, paint);
        }

        private void DrawTableCaret(SKCanvas canvas, List<TableEntry> tables)
        {
            if (_tableEntryIdx < 0 || _tableEntryIdx >= tables.Count) return;

            var te = tables[_tableEntryIdx];
            var cell = te.Layout.FindCell(_tableCaretRow, _tableCaretCol);
            if (cell is null || _tableCaretPara >= cell.Paragraphs.Count) return;

            var paraLayout = cell.Paragraphs[_tableCaretPara];
            int pos = Clamp(_tableCaretChar, 0, paraLayout.Layout.TextLength);

            var caret = paraLayout.Layout.HitTestPosition(pos);

            float cellContentX = te.XPt + cell.Xpt + cell.PadLeftPt + cell.Borders.Left.WidthPt;
            float cellContentY = te.Ypt + cell.Ypt + cell.PadTopPt + cell.Borders.Top.WidthPt;

            float cx = cellContentX + paraLayout.Layout.LeftIndentPt + caret.X;
            float cy = cellContentY + paraLayout.Ypt + paraLayout.Layout.SpaceBeforePt + caret.Y;

            using var paint = new SKPaint
            {
                Color = SKColors.Black,
                StrokeWidth = 1.1f,
                IsAntialias = false
            };
            canvas.DrawLine(cx, cy, cx, cy + caret.Height, paint);
        }

        // ── Pointer ───────────────────────────────────────────────────────

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            Focus();

            var pt = e.GetPosition(this);

            if (HitTestTable(pt, out int tableIdx, out int row, out int col,
                out int paraIdx, out int charIdx))
            {
                EnterTableMode(tableIdx, row, col, paraIdx, charIdx);
                e.Handled = true;
                return;
            }

            var (pi, ci) = HitTest(pt);
            _caretPara = pi; _caretChar = ci;
            _selStartPara = pi; _selStartChar = ci;
            _selEndPara = pi; _selEndChar = ci;
            _isSelecting = true;

            SwitchToNormalMode();
            SnapCaretToCorrectSlice();
            UpdatePreferredX();

            var pvm = GetVmAt(_caretPara);
            if (pvm is not null) DocVm?.SetActiveParagraph(pvm);
            UpdateSelectionContext();


            ResetCaret(); InvalidateFull();
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!_isSelecting) return;
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

            var (pi, ci) = HitTest(e.GetPosition(this));
            _selEndPara = pi; _selEndChar = ci;
            _caretPara = pi; _caretChar = ci;

            UpdateSelectionContext();
            InvalidateFull();
            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            _isSelecting = false;
            UpdateSelectionContext();
        }

        // ── Keyboard ─────────────────────────────────────────────────────

        protected override void OnTextInput(TextInputEventArgs e)
        {
            base.OnTextInput(e);
            if (string.IsNullOrEmpty(e.Text)) return;
            _caretLineHint = -1; // сбрасываем — после ввода snap работает по стандартному lineIdx

            if (_caretMode == CaretMode.Table)
            {
                TableInsertText(e.Text);
                e.Handled = true;
                return;
            }

            BeginEdit("Type text");
            DeleteSelection();

            var pvm = GetVmAt(_caretPara);
            if (pvm is null) return;

            string t = pvm.PlainText ?? "";
            int pos = Clamp(_caretChar, 0, t.Length);
            pvm.PlainText = t[..pos] + e.Text + t[pos..];
            _caretChar = pos + e.Text.Length;

            CommitEdit();
            // SnapCaretToCorrectSlice вызовется внутри ScheduleRebuild после RebuildLayouts —
            // только там лейауты актуальны после переноса строк.
            UpdatePreferredX();
            SyncSel(); ResetCaret();
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

            if (_caretMode == CaretMode.Table)
            {
                switch (e.Key)
                {
                    case Key.Tab when !shft: TableNavigateNext(); e.Handled = true; return;
                    case Key.Tab when shft: TableNavigatePrev(); e.Handled = true; return;
                    case Key.Enter: TableNewParagraph(); e.Handled = true; return;
                    case Key.Back: TableDeleteBack(); e.Handled = true; return;
                    case Key.Delete: TableDeleteForward(); e.Handled = true; return;
                    case Key.Left: TableNavLeft(shft); e.Handled = true; return;
                    case Key.Right: TableNavRight(shft); e.Handled = true; return;
                    case Key.Escape:
                        SwitchToNormalMode(); InvalidateFull(); e.Handled = true; return;
                }
            }

            switch (e.Key)
            {
                case Key.Back: ExecuteDeleteBack(); e.Handled = true; break;
                case Key.Delete: ExecuteDeleteForward(); e.Handled = true; break;
                case Key.Enter: ExecuteNewParagraph(); e.Handled = true; break;

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
            }
        }

        // ── Таблица — режим каретки ───────────────────────────────────────

        /// <summary>
        /// Входим в режим таблицы: устанавливаем каретку в ячейку и регистрируем
        /// делегаты структурных операций в DocumentViewModel.
        /// </summary>
        private void EnterTableMode(
            int tableIdx, int row, int col, int paraIdx, int charIdx)
        {
            bool wasInTable = _caretMode == CaretMode.Table;
            bool sameTable = wasInTable && _tableEntryIdx == tableIdx;

            _caretMode = CaretMode.Table;
            _tableEntryIdx = tableIdx;
            _tableCaretRow = row;
            _tableCaretCol = col;
            _tableCaretPara = paraIdx;
            _tableCaretChar = charIdx;

            if (tableIdx >= 0 && tableIdx < _tables.Count)
                _activeTableBlock = _tables[tableIdx].Table;

            // Регистрируем делегаты — контекстный Ribbon теперь может вызывать
            // структурные операции через ITextEditorCommandTarget.
            if (DocVm is { } vm)
            {
                vm.TableAddRowDelegate = ExecuteTableAddRow;
                vm.TableAddColDelegate = ExecuteTableAddColumn;
                vm.TableDeleteRowDelegate = ExecuteTableDeleteRow;
                vm.TableDeleteColDelegate = ExecuteTableDeleteColumn;
                vm.TableDeleteDelegate = ExecuteTableDelete;
            }

            if (!sameTable)
                NotifyCaretEnteredTableCallback();

            ResetCaret(); InvalidateFull();
        }

        /// <summary>
        /// Выходим из режима таблицы, обнуляем делегаты в DocumentViewModel.
        /// </summary>
        private void SwitchToNormalMode()
        {
            if (_caretMode == CaretMode.Table)
            {
                _caretMode = CaretMode.Normal;
                _activeTableBlock = null;
                _tableEntryIdx = -1;

                // Очищаем делегаты — контекстный Ribbon больше не должен
                // трогать таблицу, которой нет в активном контексте.
                if (DocVm is { } vm)
                {
                    vm.TableAddRowDelegate = null;
                    vm.TableAddColDelegate = null;
                    vm.TableDeleteRowDelegate = null;
                    vm.TableDeleteColDelegate = null;
                    vm.TableDeleteDelegate = null;
                }

                CaretLeftTable?.Invoke();
            }
        }

        private void NotifyCaretEnteredTableCallback()
        {
            if (_tableEntryIdx < 0 || _tableEntryIdx >= _tables.Count) return;

            var te = _tables[_tableEntryIdx];
            var offsets = new List<double>();
            var widths = new List<double>();

            foreach (var w in te.Layout.ColumnWidthsPt)
                widths.Add(PtToMm(w));
            foreach (var o in te.Layout.ColumnOffsetsPt)
                offsets.Add(PtToMm(o));

            CaretEnteredTable?.Invoke(offsets, widths);
        }

        // ── Таблица — структурные операции ───────────────────────────────
        // Вызываются через делегаты DocumentViewModel (установлены в EnterTableMode).

        /// <summary>Добавить строку выше (above=true) или ниже (above=false) текущей.</summary>
        private void ExecuteTableAddRow(bool above)
        {
            if (_activeTableBlock is null) return;

            int insertRow = above ? _tableCaretRow : _tableCaretRow + 1;

            foreach (var cell in _activeTableBlock.Cells)
                if (cell.Row >= insertRow) cell.Row++;

            for (int c = 0; c < _activeTableBlock.ColumnCount; c++)
                _activeTableBlock.Cells.Add(new TableCell { Row = insertRow, Column = c });

            _activeTableBlock.RowCount++;

            // Поправляем позицию каретки если вставили выше.
            if (above)
                _tableCaretRow++;

            RebuildLayouts();
            InvalidateFull();
        }

        /// <summary>Удалить строку, в которой находится каретка.</summary>
        private void ExecuteTableDeleteRow()
        {
            if (_activeTableBlock is null) return;

            int deleteRow = _tableCaretRow;

            _activeTableBlock.Cells.RemoveAll(c => c.Row == deleteRow);
            foreach (var cell in _activeTableBlock.Cells)
                if (cell.Row > deleteRow) cell.Row--;

            _activeTableBlock.RowCount--;

            if (_activeTableBlock.RowCount <= 0)
            {
                ExecuteTableDelete();
                return;
            }

            _tableCaretRow = Clamp(_tableCaretRow, 0, _activeTableBlock.RowCount - 1);

            RebuildLayouts();
            InvalidateFull();
        }

        /// <summary>Добавить столбец слева (left=true) или справа (left=false) от текущего.</summary>
        private void ExecuteTableAddColumn(bool left)
        {
            if (_activeTableBlock is null) return;

            int insertCol = left ? _tableCaretCol : _tableCaretCol + 1;

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

            if (left)
                _tableCaretCol++;

            RebuildLayouts();
            InvalidateFull();
        }

        /// <summary>Удалить столбец, в котором находится каретка.</summary>
        private void ExecuteTableDeleteColumn()
        {
            if (_activeTableBlock is null) return;

            int deleteCol = _tableCaretCol;

            _activeTableBlock.Cells.RemoveAll(c => c.Column == deleteCol);
            foreach (var cell in _activeTableBlock.Cells)
                if (cell.Column > deleteCol) cell.Column--;

            if (deleteCol < _activeTableBlock.Columns.Count)
                _activeTableBlock.Columns.RemoveAt(deleteCol);

            _activeTableBlock.ColumnCount--;

            if (_activeTableBlock.ColumnCount <= 0)
            {
                ExecuteTableDelete();
                return;
            }

            _tableCaretCol = Clamp(_tableCaretCol, 0, _activeTableBlock.ColumnCount - 1);

            RebuildLayouts();
            InvalidateFull();
        }

        /// <summary>Удалить всю таблицу целиком.</summary>
        private void ExecuteTableDelete()
        {
            if (_activeTableBlock is null || DocVm is null) return;

            DocVm.Document.Sections[0].Blocks.Remove(_activeTableBlock);

            SwitchToNormalMode();

            // Пересинхронизируем коллекцию ParagraphViewModel после удаления блока.
            DocVm.RebuildParagraphViewModelsPublic();

            _caretPara = Clamp(_caretPara, 0, Math.Max(0, _layouts.Count - 1));
            _caretChar = 0;

            RebuildLayouts();
            InvalidateFull();
        }

        // ── Таблица — редактирование ──────────────────────────────────────

        private void TableInsertText(string text)
        {
            var cell = GetActiveTableCell();
            if (cell is null || _tableCaretPara >= cell.Paragraphs.Count) return;

            var para = cell.Paragraphs[_tableCaretPara];
            string t = para.GetPlainText();
            int pos = Clamp(_tableCaretChar, 0, t.Length);

            SetCellParaText(cell, _tableCaretPara, t[..pos] + text + t[pos..]);
            _tableCaretChar = pos + text.Length;

            InvalidateFull();
        }

        private void TableDeleteBack()
        {
            var cell = GetActiveTableCell();
            if (cell is null || _tableCaretPara >= cell.Paragraphs.Count) return;

            var para = cell.Paragraphs[_tableCaretPara];
            string t = para.GetPlainText();

            if (_tableCaretChar > 0)
            {
                int p = Clamp(_tableCaretChar, 1, t.Length);
                SetCellParaText(cell, _tableCaretPara, t[..(p - 1)] + t[p..]);
                _tableCaretChar = p - 1;
            }
            else if (_tableCaretPara > 0)
            {
                var prev = cell.Paragraphs[_tableCaretPara - 1];
                string pt = prev.GetPlainText();
                SetCellParaText(cell, _tableCaretPara - 1, pt + t);
                cell.Paragraphs.RemoveAt(_tableCaretPara);
                _tableCaretPara--;
                _tableCaretChar = pt.Length;
            }

            InvalidateFull();
        }

        private void TableDeleteForward()
        {
            var cell = GetActiveTableCell();
            if (cell is null || _tableCaretPara >= cell.Paragraphs.Count) return;

            var para = cell.Paragraphs[_tableCaretPara];
            string t = para.GetPlainText();

            if (_tableCaretChar < t.Length)
            {
                int p = Clamp(_tableCaretChar, 0, t.Length - 1);
                SetCellParaText(cell, _tableCaretPara, t[..p] + t[(p + 1)..]);
            }
            else if (_tableCaretPara < cell.Paragraphs.Count - 1)
            {
                var next = cell.Paragraphs[_tableCaretPara + 1];
                string nt = next.GetPlainText();
                SetCellParaText(cell, _tableCaretPara, t + nt);
                cell.Paragraphs.RemoveAt(_tableCaretPara + 1);
            }

            InvalidateFull();
        }

        private void TableNewParagraph()
        {
            var cell = GetActiveTableCell();
            if (cell is null || _tableCaretPara >= cell.Paragraphs.Count) return;

            var para = cell.Paragraphs[_tableCaretPara];
            string t = para.GetPlainText();
            int pos = Clamp(_tableCaretChar, 0, t.Length);

            SetCellParaText(cell, _tableCaretPara, t[..pos]);

            var newPara = new ParagraphBlock();
            if (pos < t.Length)
            {
                var run = new RunModel { Text = t[pos..] };
                var chunk = new TextChunk();
                chunk.Runs.Add(run);
                newPara.Chunks.Add(chunk);
            }

            cell.Paragraphs.Insert(_tableCaretPara + 1, newPara);
            _tableCaretPara++;
            _tableCaretChar = 0;

            InvalidateFull();
        }

        private void TableNavLeft(bool extend)
        {
            if (_tableCaretChar > 0)
            { _tableCaretChar--; ResetCaret(); InvalidateFull(); }
        }

        private void TableNavRight(bool extend)
        {
            var cell = GetActiveTableCell();
            if (cell is null || _tableCaretPara >= cell.Paragraphs.Count) return;
            int len = cell.Paragraphs[_tableCaretPara].GetPlainText().Length;
            if (_tableCaretChar < len)
            { _tableCaretChar++; ResetCaret(); InvalidateFull(); }
        }

        private void TableNavigateNext()
        {
            if (_tableEntryIdx < 0 || _tableEntryIdx >= _tables.Count) return;

            var layout = _tables[_tableEntryIdx].Layout;
            int col = _tableCaretCol + 1;
            int row = _tableCaretRow;

            if (col >= layout.ColumnCount) { col = 0; row++; }

            if (row >= layout.RowCount)
            {
                SwitchToNormalMode();
                _caretPara = Math.Min(_caretPara + 1, _layouts.Count - 1);
                _caretChar = 0;
                ResetCaret(); InvalidateFull();
                return;
            }

            var cell = layout.FindCell(row, col);
            if (cell is null) return;

            _tableCaretRow = row;
            _tableCaretCol = col;
            _tableCaretPara = 0;
            _tableCaretChar = 0;
            ResetCaret(); InvalidateFull();
        }

        private void TableNavigatePrev()
        {
            if (_tableEntryIdx < 0 || _tableEntryIdx >= _tables.Count) return;

            var layout = _tables[_tableEntryIdx].Layout;
            int col = _tableCaretCol - 1;
            int row = _tableCaretRow;

            if (col < 0) { col = layout.ColumnCount - 1; row--; }

            if (row < 0)
            {
                SwitchToNormalMode();
                _caretPara = Math.Max(_caretPara - 1, 0);
                _caretChar = GetVmAt(_caretPara)?.PlainText?.Length ?? 0;
                ResetCaret(); InvalidateFull();
                return;
            }

            var cell = layout.FindCell(row, col);
            if (cell is null) return;

            _tableCaretRow = row;
            _tableCaretCol = col;
            _tableCaretPara = 0;
            _tableCaretChar = 0;
            ResetCaret(); InvalidateFull();
        }

        private TableCell? GetActiveTableCell()
        {
            if (_tableEntryIdx < 0 || _tableEntryIdx >= _tables.Count) return null;
            return _tables[_tableEntryIdx].Table.GetCell(_tableCaretRow, _tableCaretCol);
        }

        private static void SetCellParaText(TableCell cell, int paraIdx, string text)
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
            else if (_caretChar == 0 && _caretPara > 0)
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
            else if (_caretPara < (DocVm?.Paragraphs.Count ?? 0) - 1)
            {
                var next = GetVmAt(_caretPara + 1);
                if (next is not null) { pvm.PlainText += next.PlainText; DocVm?.DeleteParagraph(next); }
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

                int newSliceIdx = -1;
                for (int i = 0; i < _layouts.Count; i++)
                    if (_layouts[i].Vm == newVm) { newSliceIdx = i; break; }

                if (newSliceIdx >= 0) { _caretPara = newSliceIdx; _caretChar = 0; }
            }
            CommitEdit();
            SnapCaretToCorrectSlice();
            UpdatePreferredX();
            SyncSel(); ResetCaret(); InvalidateFull();
        }

        public void ExecuteNavLeft(bool extend)
        {
            _caretLineHint = -1;
            if (HasSel() && !extend)
            { var (sp, sc, _, _) = NormalizeSelection(); _caretPara = sp; _caretChar = sc; }
            else if (_caretChar > 0) _caretChar--;
            else if (_caretPara > 0)
            { _caretPara--; _caretChar = GetVmAt(_caretPara)?.PlainText?.Length ?? 0; }
            SnapCaretToCorrectSlice();
            if (!extend) SyncSel(); else ExtendSel();
            UpdatePreferredX();
            ResetCaret(); InvalidateFull();
        }

        public void ExecuteNavRight(bool extend)
        {
            _caretLineHint = -1;
            int len = GetVmAt(_caretPara)?.PlainText?.Length ?? 0;
            if (HasSel() && !extend)
            { var (_, _, ep, ec) = NormalizeSelection(); _caretPara = ep; _caretChar = ec; }
            else if (_caretChar < len) _caretChar++;
            else if (_caretPara < _layouts.Count - 1) { _caretPara++; _caretChar = 0; }
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

        // ── Вертикальная навигация ────────────────────────────────────────

        private void MoveCaretVertically(int dir)
        {
            var layout = GetLayoutAt(_caretPara);
            if (layout is null)
            {
                _caretPara = Clamp(_caretPara + dir, 0, _layouts.Count - 1);
                _caretChar = Clamp(_caretChar, 0, GetVmAt(_caretPara)?.PlainText?.Length ?? 0);
                return;
            }

            int lineIdx = layout.GetLineIndexForChar(_caretChar);
            int targetLine = lineIdx + dir;

            if (targetLine >= 0 && targetLine < layout.Lines.Count)
            {
                // GetCharIndexForVerticalMove ожидает layout-space (LeftIndentPt + glyphX)
                // и сам вычитает LeftIndentPt внутри. Передаём _preferredCaretXPt как есть.
                _caretChar = layout.GetCharIndexForVerticalMove(
                    _caretChar, dir, _preferredCaretXPt);
            }
            else if (dir < 0 && _caretPara > 0)
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
            // Сохраняем caret.X (= LeftIndentPt + seg.X + glyphOffset) без firstLineExtra.
            // GetCharIndexForVerticalMove вычитает внутри LeftIndentPt, получает seg.X — это
            // glyph-space и он одинаков для всех строк (seg.X начинается с 0).
            // Добавлять FirstLineIndentPt здесь нельзя — при переходе с line0 на line1
            // это создало бы неверное смещение на line1 которая не имеет firstLineExtra.
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

            // Если есть hint от клика мышью — предпочитаем удержать каретку на этой строке.
            // Это решает boundary-case: char=422=FirstChar_line4, но пользователь кликнул
            // на строку 3 (hint=3). Остаёмся в слайсе содержащем строку hint.
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
                        _logger.Debug(
                            "SnapCaret(hint): char={C} hint={H} → slice {I} [{LF}..{LT})",
                            _caretChar, _caretLineHint, i, pl.LineFrom, pl.LineTo);
                        return;
                    }
                }
            }

            // Стандартный поиск по lineIdx.
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
                    _logger.Debug(
                        "SnapCaret: char={C} lineIdx={L} → slice {I} [{LF}..{LT})",
                        _caretChar, lineIdx, i, pl.LineFrom, pl.LineTo);
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
            var sVm = GetVmAt(_selStartPara);
            var eVm = GetVmAt(_selEndPara);
            if (sVm is null || eVm is null)
                return (_selStartPara, _selStartChar, _selEndPara, _selEndChar);

            int si = DocVm?.Paragraphs.IndexOf(sVm) ?? 0;
            int ei = DocVm?.Paragraphs.IndexOf(eVm) ?? 0;

            if (si < ei) return (_selStartPara, _selStartChar, _selEndPara, _selEndChar);
            if (si > ei) return (_selEndPara, _selEndChar, _selStartPara, _selStartChar);
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
            else
            {
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

            int si = DocVm?.Paragraphs.IndexOf(sVm) ?? 0;
            int ei = DocVm?.Paragraphs.IndexOf(eVm) ?? 0;

            var lines = new List<string>();
            var seenVms = new HashSet<ParagraphViewModel>();

            for (int i = sp; i <= ep && i < _layouts.Count; i++)
            {
                var pvm = GetVmAt(i);
                if (pvm is null || !seenVms.Add(pvm)) continue;

                int di = DocVm?.Paragraphs.IndexOf(pvm) ?? -1;
                if (di < si || di > ei) continue;

                string t = pvm.PlainText ?? "";
                int from = di == si ? Clamp(sc, 0, t.Length) : 0;
                int to = di == ei ? Clamp(ec, 0, t.Length) : t.Length;
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
        /// Преобразует координату клика (логические пиксели Avalonia)
        /// в (индекс_параграфа, позиция_символа).
        ///
        /// ИСПРАВЛЕНИЕ: используем _pages[best.PageIndex].PadLeftPt + MarginLeftPt
        /// вместо пересчёта из _canvasWidth (который обновляется ArrangeOverride
        /// независимо от перестройки лейаутов). Гарантирует согласованность
        /// между рендером и hit-тестом.
        /// </summary>
        private (int parIdx, int charIdx) HitTest(Point ptLogPx)
        {
            List<ParaLayout> layouts;
            List<PageRect> pages;
            lock (_renderLock) { layouts = _layouts; pages = _pages; }

            if (layouts.Count == 0) return (0, 0);

            double zoom = Zoom;
            float xPt = (float)(ptLogPx.X / zoom * PxToPt);
            float yPt = (float)(ptLogPx.Y / zoom * PxToPt);

            var mode = DocVm?.ViewMode ?? EditorViewMode.Draft;

            int bestIdx = 0;
            float bestDist = float.MaxValue;

            for (int i = 0; i < layouts.Count; i++)
            {
                var pl = layouts[i];
                float top = pl.Ypt;
                float bot = pl.Ypt + pl.HeightPt;
                float dist = yPt < top ? top - yPt
                           : yPt > bot ? yPt - bot
                           : 0f;
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestIdx = i;
                    if (dist == 0f) break;
                }
            }

            var best = layouts[bestIdx];

            float padXPt;
            if (mode == EditorViewMode.Page)
            {
                if (best.PageIndex >= 0 && best.PageIndex < pages.Count)
                {
                    var pg = pages[best.PageIndex];
                    padXPt = pg.PadLeftPt + pg.MarginLeftPt;
                }
                else
                {
                    var (ml, _, _, _) = GetPagePaddingPt();
                    float cw = (float)(_canvasWidth * PxToPt);
                    padXPt = Math.Max((cw - GetPageWidthPt()) / 2f, 0f) + ml;
                }
            }
            else if (mode == EditorViewMode.Reading)
            {
                float cw = (float)(_canvasWidth * PxToPt);
                padXPt = (cw - Math.Min(cw, ReadingMaxPt)) / 2f;
            }
            else
            {
                padXPt = DraftPadWPt;
            }

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

            // Для первой строки рендер сдвигает текст на FirstLineIndentPt вправо.
            // HitTestPoint работает в glyph-space (seg.X = 0), поэтому вычитаем смещение
            // только когда клик попал именно на layout line 0.
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
            int resultChar = hit.CharIndex;

            // Запоминаем строку по Y клика — используется в SnapCaretToCorrectSlice
            // и DrawCaret чтобы удержать каретку на нужной строке при boundary-позиции.
            _caretLineHint = -1;
            for (int li = best.LineFrom; li < Math.Min(best.LineTo, best.Layout.Lines.Count); li++)
            {
                var ln = best.Layout.Lines[li];
                if (localY <= ln.Y + ln.Height)
                {
                    _caretLineHint = li;
                    break;
                }
            }

            _logger.Debug(
                "HitTest: sliceIdx={S} raw={R} result={C} lineHint={H} localX={X:F1} localY={Y:F1}",
                bestIdx, hit.CharIndex, resultChar, _caretLineHint, hitX, localY);

            return (bestIdx, resultChar);
        }
        /// </summary>
        private bool HitTestTable(
            Point ptLogPx,
            out int tableIdx, out int row, out int col,
            out int paraIdx, out int charIdx)
        {
            tableIdx = charIdx = paraIdx = row = col = 0;

            List<TableEntry> tables;
            lock (_renderLock) { tables = _tables; }

            double zoom = Zoom;
            float xPt = (float)(ptLogPx.X / zoom * PxToPt);
            float yPt = (float)(ptLogPx.Y / zoom * PxToPt);

            for (int ti = 0; ti < tables.Count; ti++)
            {
                var te = tables[ti];
                float relX = xPt - te.XPt;
                float relY = yPt - te.Ypt;

                if (relX < 0 || relY < 0
                    || relX > te.Layout.TotalWidthPt
                    || relY > te.Layout.TotalHeightPt)
                    continue;

                var result = te.Layout.HitTestParagraph(relX, relY);
                if (result is null) continue;

                var (cell, para) = result.Value;

                float cellContentX = cell.Xpt + cell.PadLeftPt + cell.Borders.Left.WidthPt;
                float cellContentY = cell.Ypt + cell.PadTopPt + cell.Borders.Top.WidthPt;
                float localX = relX - cellContentX - para.Layout.LeftIndentPt;
                float localY = relY - cellContentY - para.Ypt - para.Layout.SpaceBeforePt;

                localY = Math.Max(0f, localY);

                var hit = para.Layout.HitTestPoint(localX, localY);

                tableIdx = ti;
                row = cell.Row;
                col = cell.Column;
                paraIdx = para.ParagraphIndex;
                charIdx = hit.CharIndex;
                return true;
            }

            return false;
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

                // caret.X = LeftIndentPt + glyphX — отступ уже включён, не добавляем снова.
                double xPx = caret.X * PtToPx * zoom;
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

        private int FindFirstSliceForParagraphIndex(int paragraphIndex)
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
        }

        private static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;
        private static float Clamp(float v, float min, float max) => v < min ? min : v > max ? max : v;

        /// <summary>
        /// Синхронизирует DocVm.SelectionParagraphs с текущим выделением.
        /// Вызывается после каждого изменения выделения (клик, перетаскивание).
        /// </summary>
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
                if (pvm is not null && seen.Add(pvm))
                    DocVm.SelectionParagraphs.Add(pvm);
            }
        }

        /// <summary>
        /// Возвращает текущее состояние каретки для сохранения в SessionData.
        /// Возвращает индекс параграфа в документе (не в _layouts),
        /// позицию символа и текущее смещение скролла.
        /// </summary>
        public (int docParaIdx, int charIdx, double scrollY) GetCaretState()
        {
            int docIdx = 0;
            if (_caretPara >= 0 && _caretPara < _layouts.Count && DocVm is not null)
            {
                int idx = DocVm.Paragraphs.IndexOf(_layouts[_caretPara].Vm);
                if (idx >= 0) docIdx = idx;
            }
            return (docIdx, _caretChar, _scrollOffsetY);
        }

        /// <summary>
        /// Восстанавливает позицию каретки из SessionData.
        /// Откладывает выполнение до момента когда лейауты уже построены.
        /// </summary>
        public void RestoreCaretState(int docParaIdx, int charIdx)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_layouts.Count == 0) return;
                _caretPara = FindFirstSliceForParagraphIndex(docParaIdx);
                _caretChar = Clamp(charIdx, 0, GetVmAt(_caretPara)?.PlainText?.Length ?? 0);
                SnapCaretToCorrectSlice();
                UpdatePreferredX();
                SyncSel();
                ResetCaret();
                InvalidateFull();
            }, DispatcherPriority.Loaded);
        }



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