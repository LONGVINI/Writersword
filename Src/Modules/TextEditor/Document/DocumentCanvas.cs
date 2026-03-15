using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Threading.Tasks;
using Writersword.Modules.Common;
using Writersword.Modules.TextEditor.Commands;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.ViewModels;
using Writersword.Modules.TextEditor.ViewModels.Blocks;

namespace Writersword.Modules.TextEditor.Views.Document
{
    public sealed class DocumentCanvas : Control
    {
        // ── Layout ───────────────────────────────────────────────────────
        private record ParaLayout(ParagraphViewModel Vm, TextLayout Layout, double Y, double Height, int PageIndex);
        private record PageRect(double Y, double Width, double Height, double PadLeft, double PadTop);

        private List<ParaLayout> _layouts = new();
        private List<PageRect> _pages = new();
        private double _canvasWidth;
        private double _canvasHeight;

        // ── Constants ────────────────────────────────────────────────────
        private const double PageGap = 20;
        private const double DraftPadH = 12;
        private const double DraftPadW = 0;
        private const double ReadingMax = 680;
        private const double LineHeight = 22;

        // ── Caret ────────────────────────────────────────────────────────
        private int _caretPara = 0;
        private int _caretChar = 0;
        private bool _caretVisible = true;
        private readonly DispatcherTimer _caretTimer;

        // ── Selection ────────────────────────────────────────────────────
        private int _selStartPara = 0;
        private int _selStartChar = 0;
        private int _selEndPara = 0;
        private int _selEndChar = 0;
        private bool _isSelecting;

        // ── Logger ───────────────────────────────────────────────────────
        private static readonly ILogger _logger = Log.ForContext<DocumentCanvas>();

        // ── Undo / settings ──────────────────────────────────────────────
        public UndoRedoStack? UndoStack { get; set; }

        private double _monitorSizeInches = 0;
        private double _cachedDpi = 96.0;

        /// <summary>
        /// Вызывается после пересчёта DPI — передаёт рекомендуемый zoom наружу.
        /// </summary>
        public Action<double>? RecommendedZoomChanged { get; set; }

        /// <summary>
        /// Physical monitor diagonal in inches.
        /// 0 = use standard 96 DPI.
        /// Triggers DPI cache rebuild and layout on change.
        /// </summary>
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

        private DocumentSnapshotCommand? _pendingSnapshot;

        // ── Brushes ───────────────────────────────────────────────────────
        private static readonly IBrush SelectionBrush =
            new SolidColorBrush(Color.Parse("#3390FF"), 0.35);
        private static readonly IBrush PageBrush = Brushes.White;
        private static readonly IBrush CanvasBrush = new SolidColorBrush(Color.Parse("#E8E8E8"));
        private static readonly IPen CaretPen = new Pen(Brushes.Black, 1.5);

        private DocumentViewModel? DocVm => DataContext as DocumentViewModel;
        private double Zoom => DocVm?.Zoom ?? 1.0;

        public DocumentCanvas()
        {
            Focusable = true;
            Cursor = new Cursor(StandardCursorType.Ibeam);

            _caretTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
            _caretTimer.Tick += (_, _) => { _caretVisible = !_caretVisible; InvalidateVisual(); };
            _caretTimer.Start();
        }

        // ── DPI cache ─────────────────────────────────────────────────────

        /// <summary>
        /// Recalculates physical DPI from monitor diagonal and screen resolution.
        /// Called once per layout pass and when MonitorSizeInches changes.
        /// </summary>
        private void RebuildDpiCache()
        {
            if (_monitorSizeInches <= 0)
            {
                _cachedDpi = 96.0;
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => RecommendedZoomChanged?.Invoke(RecommendedZoom));
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

            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => RecommendedZoomChanged?.Invoke(RecommendedZoom));
        }

        private double MmToLogicalPx(double mm) => mm * (96.0 / 25.4);
        public double RecommendedZoom => _cachedDpi > 0 ? _cachedDpi / 96.0 : 1.0;

        private double GetPageWidthPx()
        {
            var ps = DocVm?.Document.PageSettings;
            if (ps is null) return MmToLogicalPx(210);
            return ps.Orientation == Models.Page.PageOrientation.Landscape
                ? MmToLogicalPx(ps.HeightMm)
                : MmToLogicalPx(ps.WidthMm);
        }

        private double GetPageHeightPx()
        {
            var ps = DocVm?.Document.PageSettings;
            if (ps is null) return MmToLogicalPx(297);
            return ps.Orientation == Models.Page.PageOrientation.Landscape
                ? MmToLogicalPx(ps.WidthMm)
                : MmToLogicalPx(ps.HeightMm);
        }

        private (double padLeft, double padTop, double padRight, double padBottom) GetPagePadding()
        {
            var ps = DocVm?.Document.PageSettings;
            if (ps is null) return (MmToLogicalPx(20), MmToLogicalPx(20), MmToLogicalPx(20), MmToLogicalPx(20));
            return (
                MmToLogicalPx(ps.MarginLeftMm),
                MmToLogicalPx(ps.MarginTopMm),
                MmToLogicalPx(ps.MarginRightMm),
                MmToLogicalPx(ps.MarginBottomMm));
        }

        // ── DataContext ───────────────────────────────────────────────────

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            // Screen becomes accessible only after attachment to the visual tree.
            // Re-run DPI calculation so RecommendedZoomChanged fires with a valid screen.
            RebuildDpiCache();
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);

            if (DocVm is not null)
            {
                DocVm.Paragraphs.CollectionChanged += OnParagraphsChanged;
                DocVm.PropertyChanged += OnDocVmPropertyChanged;
                foreach (var pvm in DocVm.Paragraphs)
                    WirePvm(pvm);
            }

            InvalidateMeasure();
        }

        private void OnDocVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DocumentViewModel.ViewMode) ||
                e.PropertyName == nameof(DocumentViewModel.Zoom) ||
                e.PropertyName == nameof(DocumentViewModel.PageSettings))
                InvalidateMeasure();
        }

        private void OnParagraphsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems is not null)
                foreach (ParagraphViewModel pvm in e.NewItems) WirePvm(pvm);
            if (e.OldItems is not null)
                foreach (ParagraphViewModel pvm in e.OldItems)
                    pvm.PropertyChanged -= OnPvmPropertyChanged;
            InvalidateMeasure();
        }

        private void WirePvm(ParagraphViewModel pvm)
        {
            pvm.PropertyChanged += OnPvmPropertyChanged;

            pvm.FocusRequested += () =>
            {
                if (DocVm is null) return;
                int idx = DocVm.Paragraphs.IndexOf(pvm);
                if (idx < 0) return;
                _caretPara = idx;
                _caretChar = pvm.PlainText?.Length ?? 0;
                SyncSel(); ResetCaret(); InvalidateVisual();
            };

            pvm.RequestFocusAtPosition = pos =>
            {
                if (DocVm is null) return;
                int idx = DocVm.Paragraphs.IndexOf(pvm);
                if (idx < 0) return;
                _caretPara = idx;
                _caretChar = Clamp(pos, 0, pvm.PlainText?.Length ?? 0);
                SyncSel(); ResetCaret(); InvalidateVisual();
            };
        }

        private void OnPvmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ParagraphViewModel.PlainText))
                InvalidateMeasure();
        }

        // ── Measure / Layout ─────────────────────────────────────────────

        protected override Size MeasureOverride(Size available)
        {
            double zoom = Zoom;
            double availW = double.IsInfinity(available.Width) ? 800 : Math.Max(available.Width, 1);

            _canvasWidth = Math.Max(availW / zoom, 1);

            RebuildLayouts();

            double visualH = Math.Max(_canvasHeight * zoom, 100);
            double visualW = availW;

            if (DocVm?.ViewMode == EditorViewMode.Page)
                visualW = Math.Max(availW, GetPageWidthPx() * zoom + PageGap * 4);

            return new Size(visualW, visualH);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double zoom = Zoom;
            double logicalW = Math.Max(finalSize.Width / zoom, 1);

            if (Math.Abs(logicalW - _canvasWidth) > 0.5)
            {
                _canvasWidth = logicalW;
                RebuildLayouts();
            }

            return new Size(finalSize.Width, Math.Max(_canvasHeight * zoom, 100));
        }

        private void RebuildLayouts()
        {
            _layouts.Clear();
            _pages.Clear();
            if (DocVm is null) { _canvasHeight = 100; return; }

            switch (DocVm.ViewMode)
            {
                case EditorViewMode.Page:
                    RebuildPageMode();
                    break;
                case EditorViewMode.Draft:
                case EditorViewMode.Web:
                    RebuildFlowMode(_canvasWidth, DraftPadH, DraftPadW);
                    break;
                case EditorViewMode.Reading:
                    double rw = Math.Min(_canvasWidth, ReadingMax);
                    RebuildFlowMode(rw, 24, (_canvasWidth - rw) / 2);
                    break;
            }
        }

        private void RebuildPageMode()
        {
            double pageW = GetPageWidthPx();
            double pageH = GetPageHeightPx();
            var (pl, pt, pr, pb) = GetPagePadding();
            double textW = Math.Max(pageW - pl - pr, 1);
            double pageX = Math.Max((_canvasWidth - pageW) / 2.0, 0);

            double pageY = PageGap;
            double contentY = pageY + pt;
            int pageIdx = 0;

            _pages.Add(new PageRect(pageY, pageW, pageH, pageX, pt));

            foreach (var pvm in DocVm!.Paragraphs)
            {
                var rp = GetFirstRp(pvm);
                var tl = BuildTextLayout(pvm, rp, textW);
                double h = Math.Max(tl.Height, LineHeight);

                if (contentY + h > pageY + pageH - pb && contentY > pageY + pt)
                {
                    pageY = pageY + pageH + PageGap;
                    contentY = pageY + pt;
                    pageIdx++;
                    _pages.Add(new PageRect(pageY, pageW, pageH, pageX, pt));
                }

                _layouts.Add(new ParaLayout(pvm, tl, contentY, h, pageIdx));
                contentY += h + 4;
            }

            _canvasHeight = pageY + pageH + PageGap;
        }

        private void RebuildFlowMode(double maxWidth, double padH, double padW)
        {
            double textW = Math.Max(maxWidth - padW * 2, 1);
            double y = padH;

            foreach (var pvm in DocVm!.Paragraphs)
            {
                var rp = GetFirstRp(pvm);
                var tl = BuildTextLayout(pvm, rp, textW);
                double h = Math.Max(tl.Height, LineHeight);

                _layouts.Add(new ParaLayout(pvm, tl, y, h, 0));
                y += h + 4;
            }

            _canvasHeight = y + padH;
        }

        private static RunProperties? GetFirstRp(ParagraphViewModel pvm)
        {
            if (pvm.Model.Chunks.Count > 0 && pvm.Model.Chunks[0].Runs.Count > 0)
                return pvm.Model.Chunks[0].Runs[0].Properties;
            return null;
        }

        private TextLayout BuildTextLayout(ParagraphViewModel pvm, RunProperties? rp, double maxW)
        {
            string family = rp?.FontFamily ?? "Times New Roman";
            double size = rp?.FontSize ?? 14.0;
            var weight = rp?.IsBold == true ? FontWeight.Bold : FontWeight.Normal;
            var fStyle = rp?.IsItalic == true ? FontStyle.Italic : FontStyle.Normal;
            var tf = new Typeface(family, fStyle, weight);

            IBrush fg = Brushes.Black;
            if (rp?.TextColor is not null && Color.TryParse(rp.TextColor, out var col))
                fg = new SolidColorBrush(col);

            TextAlignment align = TextAlignment.Left;
            if (pvm.Model.Properties.Alignment.HasValue)
                align = pvm.Model.Properties.Alignment.Value switch
                {
                    Models.Styles.TextAlignment.Center => TextAlignment.Center,
                    Models.Styles.TextAlignment.Right => TextAlignment.Right,
                    Models.Styles.TextAlignment.Justify => TextAlignment.Justify,
                    _ => TextAlignment.Left
                };

            string text = string.IsNullOrEmpty(pvm.PlainText) ? "\u200B" : pvm.PlainText;

            return new TextLayout(text, tf, size, fg,
                textAlignment: align,
                textWrapping: TextWrapping.Wrap,
                maxWidth: maxW);
        }

        // ── Render ────────────────────────────────────────────────────────

        public override void Render(DrawingContext ctx)
        {
            double zoom = Zoom;

            using (ctx.PushTransform(Matrix.CreateScale(zoom, zoom)))
            {
                var mode = DocVm?.ViewMode ?? EditorViewMode.Draft;
                if (mode == EditorViewMode.Page)
                    RenderPageMode(ctx);
                else
                    RenderFlowMode(ctx, mode);
            }
        }

        private void RenderPageMode(DrawingContext ctx)
        {
            ctx.FillRectangle(CanvasBrush, new Rect(0, 0, _canvasWidth, _canvasHeight));

            var (ml, _, _, _) = GetPagePadding();

            foreach (var page in _pages)
            {
                ctx.FillRectangle(
                    new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)),
                    new Rect(page.PadLeft + 4, page.Y + 4, page.Width, page.Height));

                ctx.FillRectangle(PageBrush, new Rect(page.PadLeft, page.Y, page.Width, page.Height));
            }

            for (int i = 0; i < _layouts.Count; i++)
            {
                var pl = _layouts[i];
                var page = pl.PageIndex < _pages.Count ? _pages[pl.PageIndex] : _pages[0];
                double originX = page.PadLeft + ml;
                var origin = new Point(originX, pl.Y);

                DrawSelectionForPara(ctx, i, pl, origin);
                pl.Layout.Draw(ctx, origin);

                if (_caretVisible && _caretPara == i)
                    DrawCaret(ctx, pl, origin);
            }
        }

        private void RenderFlowMode(DrawingContext ctx, EditorViewMode mode)
        {
            ctx.FillRectangle(Brushes.Transparent, new Rect(0, 0, _canvasWidth, _canvasHeight));

            double padW = mode == EditorViewMode.Reading
                ? (_canvasWidth - Math.Min(_canvasWidth, ReadingMax)) / 2
                : DraftPadW;

            for (int i = 0; i < _layouts.Count; i++)
            {
                var pl = _layouts[i];
                var origin = new Point(padW, pl.Y);

                DrawSelectionForPara(ctx, i, pl, origin);
                pl.Layout.Draw(ctx, origin);

                if (_caretVisible && _caretPara == i)
                    DrawCaret(ctx, pl, origin);
            }
        }

        private void DrawSelectionForPara(DrawingContext ctx, int i, ParaLayout pl, Point origin)
        {
            if (!HasSel()) return;
            var (sp, sc, ep, ec) = NormalizeSelection();
            if (i < sp || i > ep) return;

            int len = pl.Vm.PlainText?.Length ?? 0;
            int from, to;
            if (sp == ep) { from = sc; to = ec; }
            else if (i == sp) { from = sc; to = len; }
            else if (i == ep) { from = 0; to = ec; }
            else { from = 0; to = len; }

            from = Clamp(from, 0, len);
            to = Clamp(to, 0, len);
            if (from >= to) return;

            foreach (var r in pl.Layout.HitTestTextRange(from, to - from))
                ctx.FillRectangle(SelectionBrush,
                    new Rect(origin.X + r.X, origin.Y + r.Y, r.Width, r.Height));
        }

        private void DrawCaret(DrawingContext ctx, ParaLayout pl, Point origin)
        {
            int pos = Clamp(_caretChar, 0, pl.Vm.PlainText?.Length ?? 0);
            Rect b = pl.Layout.HitTestTextPosition(pos);
            double x = origin.X + b.X;
            double y1 = origin.Y + b.Y;
            double y2 = y1 + (b.Height > 0 ? b.Height : LineHeight);
            ctx.DrawLine(CaretPen, new Point(x, y1), new Point(x, y2));
        }

        // ── Pointer ───────────────────────────────────────────────────────

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            Focus();

            var (pi, ci) = HitTest(e.GetPosition(this));
            _caretPara = pi;
            _caretChar = ci;
            _selStartPara = pi;
            _selStartChar = ci;
            _selEndPara = pi;
            _selEndChar = ci;
            _isSelecting = true;

            var pvm = GetVmAt(pi);
            if (pvm is not null) DocVm?.SetActiveParagraph(pvm);

            ResetCaret(); InvalidateVisual();
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

            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            _isSelecting = false;
        }

        // ── Keyboard ─────────────────────────────────────────────────────

        protected override void OnTextInput(TextInputEventArgs e)
        {
            base.OnTextInput(e);
            if (string.IsNullOrEmpty(e.Text)) return;

            BeginEdit("Type text");
            DeleteSelection();

            var pvm = GetVmAt(_caretPara);
            if (pvm is null) return;

            string t = pvm.PlainText ?? "";
            int pos = Clamp(_caretChar, 0, t.Length);
            pvm.PlainText = t[..pos] + e.Text + t[pos..];
            _caretChar = pos + e.Text.Length;

            CommitEdit(); SyncSel(); ResetCaret(); InvalidateMeasure();
            e.Handled = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            var pvm = GetVmAt(_caretPara);
            if (pvm is null) return;

            string text = pvm.PlainText ?? "";
            int len = text.Length;
            bool ctrl = e.KeyModifiers == KeyModifiers.Control;
            bool shft = e.KeyModifiers == KeyModifiers.Shift;

            switch (e.Key)
            {
                case Key.Back:
                    BeginEdit("Delete");
                    if (HasSel()) { DeleteSelection(); CommitEdit(); e.Handled = true; break; }
                    if (_caretChar > 0)
                    {
                        int p = Clamp(_caretChar, 1, text.Length);
                        pvm.PlainText = text[..(p - 1)] + text[p..];
                        _caretChar = p - 1;
                    }
                    else if (_caretPara > 0)
                    {
                        var prev = GetVmAt(_caretPara - 1)!;
                        int mergeAt = prev.PlainText?.Length ?? 0;
                        DocVm?.MergeParagraphWithPrevious(pvm, text);
                        _caretPara--;
                        _caretChar = mergeAt;
                    }
                    CommitEdit(); SyncSel(); e.Handled = true; break;

                case Key.Delete:
                    BeginEdit("Delete");
                    if (HasSel()) { DeleteSelection(); CommitEdit(); e.Handled = true; break; }
                    if (_caretChar < len)
                    {
                        int p = Clamp(_caretChar, 0, text.Length - 1);
                        pvm.PlainText = text[..p] + text[(p + 1)..];
                    }
                    else if (_caretPara < (DocVm?.Paragraphs.Count ?? 0) - 1)
                    {
                        var next = GetVmAt(_caretPara + 1);
                        if (next is not null)
                        {
                            pvm.PlainText += next.PlainText;
                            DocVm?.DeleteParagraph(next);
                        }
                    }
                    CommitEdit(); SyncSel(); e.Handled = true; break;

                case Key.Enter:
                    BeginEdit("New paragraph");
                    DeleteSelection();
                    text = pvm.PlainText ?? "";
                    int cp = Clamp(_caretChar, 0, text.Length);
                    string bf = text[..cp];
                    string af = text[cp..];
                    pvm.PlainText = bf;
                    var newVm = DocVm?.AddParagraphAfter(pvm);
                    if (newVm is not null)
                    {
                        newVm.PlainText = af;
                        _caretPara = DocVm!.Paragraphs.IndexOf(newVm);
                        _caretChar = 0;
                    }
                    CommitEdit(); SyncSel(); e.Handled = true; break;

                case Key.Left:
                    if (HasSel() && !shft)
                    { var (sp, sc, _, _) = NormalizeSelection(); _caretPara = sp; _caretChar = sc; }
                    else if (_caretChar > 0) _caretChar--;
                    else if (_caretPara > 0) { _caretPara--; _caretChar = GetVmAt(_caretPara)?.PlainText?.Length ?? 0; }
                    if (!shft) SyncSel(); else ExtendSel();
                    e.Handled = true; break;

                case Key.Right:
                    if (HasSel() && !shft)
                    { var (_, _, ep, ec) = NormalizeSelection(); _caretPara = ep; _caretChar = ec; }
                    else if (_caretChar < len) _caretChar++;
                    else if (_caretPara < _layouts.Count - 1) { _caretPara++; _caretChar = 0; }
                    if (!shft) SyncSel(); else ExtendSel();
                    e.Handled = true; break;

                case Key.Up:
                    MoveCaretVertically(-1);
                    if (!shft) SyncSel(); else ExtendSel();
                    e.Handled = true; break;

                case Key.Down:
                    MoveCaretVertically(+1);
                    if (!shft) SyncSel(); else ExtendSel();
                    e.Handled = true; break;

                case Key.Home:
                    if (ctrl) { _caretPara = 0; _caretChar = 0; }
                    else _caretChar = 0;
                    if (!shft) SyncSel(); else ExtendSel();
                    e.Handled = true; break;

                case Key.End:
                    if (ctrl) { _caretPara = _layouts.Count - 1; _caretChar = GetVmAt(_caretPara)?.PlainText?.Length ?? 0; }
                    else _caretChar = len;
                    if (!shft) SyncSel(); else ExtendSel();
                    e.Handled = true; break;

                case Key.C when ctrl: _ = CopyAsync(); e.Handled = true; break;
                case Key.X when ctrl: _ = CutAsync(); e.Handled = true; break;
                case Key.V when ctrl: _ = PasteAsync(); e.Handled = true; break;
                case Key.A when ctrl: SelectAll(); e.Handled = true; break;

                case Key.Z when ctrl:
                    UndoStack?.Undo(); ClampCaret(); SyncSel(); e.Handled = true; break;
                case Key.Y when ctrl:
                    UndoStack?.Redo(); ClampCaret(); SyncSel(); e.Handled = true; break;
            }

            ResetCaret();
            InvalidateMeasure();
        }

        private void MoveCaretVertically(int dir)
        {
            _caretPara = Clamp(_caretPara + dir, 0, _layouts.Count - 1);
            _caretChar = Clamp(_caretChar, 0, GetVmAt(_caretPara)?.PlainText?.Length ?? 0);
        }

        private void ClampCaret()
        {
            int maxPara = Math.Max(0, (DocVm?.Paragraphs.Count ?? 1) - 1);
            _caretPara = Clamp(_caretPara, 0, maxPara);
            _caretChar = Clamp(_caretChar, 0, GetVmAt(_caretPara)?.PlainText?.Length ?? 0);
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

        private void SelectAll()
        {
            if (_layouts.Count == 0) return;
            _selStartPara = 0; _selStartChar = 0;
            _selEndPara = _layouts.Count - 1;
            _selEndChar = GetVmAt(_layouts.Count - 1)?.PlainText?.Length ?? 0;
            _caretPara = _selEndPara;
            _caretChar = _selEndChar;
            InvalidateVisual();
        }

        private void DeleteSelection()
        {
            if (!HasSel()) return;
            var (sp, sc, ep, ec) = NormalizeSelection();

            if (sp == ep)
            {
                var pvm = GetVmAt(sp);
                if (pvm is null) return;
                string t = pvm.PlainText ?? "";
                int s2 = Clamp(sc, 0, t.Length);
                int e2 = Clamp(ec, 0, t.Length);
                pvm.PlainText = t[..s2] + t[e2..];
                _caretPara = sp; _caretChar = s2;
            }
            else
            {
                var startPvm = GetVmAt(sp);
                var endPvm = GetVmAt(ep);
                if (startPvm is null || endPvm is null) return;

                string st = startPvm.PlainText ?? "";
                string et = endPvm.PlainText ?? "";
                int s2 = Clamp(sc, 0, st.Length);
                int e2 = Clamp(ec, 0, et.Length);

                var toDelete = new List<ParagraphViewModel>();
                for (int i = ep; i > sp; i--)
                {
                    var p = GetVmAt(i);
                    if (p is not null) toDelete.Add(p);
                }

                startPvm.PlainText = st[..s2] + et[e2..];
                foreach (var p in toDelete) DocVm?.DeleteParagraph(p);

                _caretPara = sp; _caretChar = s2;
            }

            SyncSel();
            InvalidateMeasure();
        }

        // ── Clipboard ────────────────────────────────────────────────────

        private async Task CopyAsync()
        {
            if (!HasSel()) return;
            var (sp, sc, ep, ec) = NormalizeSelection();
            var lines = new List<string>();

            for (int i = sp; i <= ep; i++)
            {
                string t = GetVmAt(i)?.PlainText ?? "";
                int from = Clamp(i == sp ? sc : 0, 0, t.Length);
                int to = Clamp(i == ep ? ec : t.Length, 0, t.Length);
                if (from > to) to = from;
                lines.Add(t[from..to]);
            }

            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
                await clipboard.SetTextAsync(string.Join(Environment.NewLine, lines));
        }

        private async Task CutAsync()
        {
            BeginEdit("Cut");
            await CopyAsync();
            DeleteSelection();
            CommitEdit();
        }

        private async Task PasteAsync()
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null) return;

            string? text = await clipboard.TryGetTextAsync();
            if (string.IsNullOrEmpty(text)) return;

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

            CommitEdit(); SyncSel(); InvalidateMeasure();
        }

        // ── HitTest ───────────────────────────────────────────────────────

        private (int parIdx, int charIdx) HitTest(Point pt)
        {
            if (_layouts.Count == 0) return (0, 0);

            double zoom = Zoom;
            pt = new Point(pt.X / zoom, pt.Y / zoom);

            var mode = DocVm?.ViewMode ?? EditorViewMode.Draft;
            double padW;

            if (mode == EditorViewMode.Page)
            {
                var (ml, _, _, _) = GetPagePadding();
                double pageX = Math.Max((_canvasWidth - GetPageWidthPx()) / 2.0, 0);
                padW = pageX + ml;
            }
            else if (mode == EditorViewMode.Reading)
                padW = (_canvasWidth - Math.Min(_canvasWidth, ReadingMax)) / 2;
            else
                padW = DraftPadW;

            for (int i = 0; i < _layouts.Count; i++)
            {
                var pl = _layouts[i];
                double bot = pl.Y + pl.Height + 4;

                if (pt.Y <= bot || i == _layouts.Count - 1)
                {
                    double lx = Clamp(pt.X - padW, 0,
                        Math.Max(pl.Layout.WidthIncludingTrailingWhitespace, 1));
                    double ly = Clamp(pt.Y - pl.Y, 0,
                        Math.Max(pl.Height - 1, 0));

                    var hit = pl.Layout.HitTestPoint(new Point(lx, ly));
                    return (i, hit.TextPosition);
                }
            }

            var lastPl = _layouts[^1];
            return (_layouts.Count - 1, lastPl.Vm.PlainText?.Length ?? 0);
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
                Rect b = pl.Layout.HitTestTextPosition(pos);
                double x = b.X;
                double y = pl.Y + b.Y;
                double h = b.Height > 0 ? b.Height : LineHeight;

                this.BringIntoView(new Rect(
                    (x - 10) * zoom,
                    (y - 10) * zoom,
                    20 * zoom,
                    (h + 20) * zoom));
            }, DispatcherPriority.Render);
        }

        // ── Helpers ───────────────────────────────────────────────────────

        private ParagraphViewModel? GetVmAt(int idx) =>
            idx >= 0 && idx < _layouts.Count ? _layouts[idx].Vm : null;

        private void ResetCaret()
        {
            _caretVisible = true;
            _caretTimer.Stop();
            _caretTimer.Start();
            ScrollToCaret();
        }

        private static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;
        private static double Clamp(double v, double min, double max) => v < min ? min : v > max ? max : v;
    }
}