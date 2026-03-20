using ReactiveUI;
using Serilog;
using System;
using System.Reactive.Linq;
using System.Text;
using Writersword.Core.Models.Print;
using Writersword.Modules.TextEditor.Contracts;
using Writersword.Modules.TextEditor.Models;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Page;
using Writersword.Modules.TextEditor.Services;
using Writersword.Modules.TextEditor.ViewModels.Blocks;
using Writersword.Modules.TextEditor.ViewModels.StatusBar;
using Writersword.Modules.TextEditor.ViewModels.Toolbar;

namespace Writersword.Modules.TextEditor.ViewModels
{
    public sealed class TextEditorViewModel : ReactiveObject, ITextEditorCommandTarget, IDisposable
    {
        private static readonly ILogger _logger = Log.ForContext<TextEditorViewModel>();

        private readonly DocumentSerializer _serializer;
        private readonly ChunkManager _chunkManager;
        private readonly DeltaHashService _hashService;
        private readonly AutoReplaceService _autoReplace;
        private readonly SpellCheckService _spellCheck;
        private readonly ExportService _exportService;

        private IDisposable? _autoSaveSubscription;
        private IDisposable? _paragraphsSubscription;
        private bool _disposed;
        private bool _isModified;
        private DocumentViewModel? _documentViewModel;
        private double _monitorSizeInches;

        // ── Public properties ─────────────────────────────────────────────

        /// <summary>Document ViewModel. Null until loaded.</summary>
        public DocumentViewModel? DocumentViewModel
        {
            get => _documentViewModel;
            private set => this.RaiseAndSetIfChanged(ref _documentViewModel, value);
        }

        public RibbonViewModel Ribbon { get; }
        public StatusBarViewModel StatusBar { get; }

        /// <summary>Module settings.</summary>
        public TextEditorSettings Settings { get; private set; } = new();

        /// <summary>Physical monitor diagonal — reactive property for View subscription.</summary>
        public double MonitorSizeInches
        {
            get => _monitorSizeInches;
            private set => this.RaiseAndSetIfChanged(ref _monitorSizeInches, value);
        }

        /// <summary>Document has been modified since last save.</summary>
        public bool IsModified
        {
            get => _isModified;
            set => this.RaiseAndSetIfChanged(ref _isModified, value);
        }

        // ── События ───────────────────────────────────────────────────────

        /// <summary>
        /// Поднимается когда пользователь нажимает Print.
        /// TextEditorModule подписывается на это событие,
        /// создаёт TextEditorPrintDocument и вызывает IPrintService.
        /// ViewModel не знает ни о каком сервисе печати напрямую.
        /// </summary>
        public event Action<DocumentModel, TextEditorPageSettings>? PrintRequested;

        // ── Constructor ───────────────────────────────────────────────────

        public TextEditorViewModel()
        {
            _hashService = new DeltaHashService();
            _chunkManager = new ChunkManager(_hashService);
            _serializer = new DocumentSerializer(_hashService, _chunkManager);
            _autoReplace = new AutoReplaceService();
            _spellCheck = new SpellCheckService();
            _exportService = new ExportService();

            Ribbon = new RibbonViewModel(this);
            StatusBar = new StatusBarViewModel();
        }

        // ── Document loading ──────────────────────────────────────────────

        public void LoadDocument(DocumentModel document, TextEditorSettings settings)
        {
            Settings = settings ?? new TextEditorSettings();
            MonitorSizeInches = Settings.MonitorSizeInches;

            _logger.Debug("LoadDocument: MonitorSizeInches={V}", MonitorSizeInches);

            if (_documentViewModel is not null)
                _documentViewModel.CursorContextChanged -= OnCursorContextChanged;

            var docVm = new DocumentViewModel(document, _chunkManager, _autoReplace, _spellCheck);
            docVm.CursorContextChanged += OnCursorContextChanged;
            DocumentViewModel = docVm;

            _paragraphsSubscription?.Dispose();
            _paragraphsSubscription = SubscribeToParagraphChanges(docVm);

            StatusBar.IsSpellCheckActive = Settings.SpellCheckEnabled;
            StatusBar.Zoom = document.Zoom > 0 ? document.Zoom : Settings.DefaultZoom;
            DocumentViewModel?.SetZoom(StatusBar.Zoom);

            StartAutoSave(Settings.AutoSaveIntervalSeconds);
            RefreshStatusBar();

            StatusBar.ViewModeChanged = mode =>
            {
                DocumentViewModel?.SetViewMode(mode);
                StatusBar.ViewMode = mode;
            };

            StatusBar.ZoomChanged = zoom => DocumentViewModel?.SetZoom(zoom);

            _logger.Debug("Document loaded: title={Title}", document.Title);
        }

        public void LoadNewDocument(TextEditorSettings settings)
        {
            LoadDocument(DocumentModel.CreateNew(), settings);
        }

        public string? GetSerializedDocument()
        {
            if (DocumentViewModel is null) return null;
            return _serializer.Serialize(DocumentViewModel.Document);
        }

        /// <summary>
        /// Applies new settings and notifies View of MonitorSizeInches change.
        /// </summary>
        public void ApplySettings(TextEditorSettings settings)
        {
            _logger.Debug("ApplySettings: MonitorSizeInches={V}", settings.MonitorSizeInches);
            Settings = settings;
            MonitorSizeInches = settings.MonitorSizeInches;
        }

        // ── Cursor context ────────────────────────────────────────────────

        private void OnCursorContextChanged(CursorContext ctx)
        {
            Ribbon.Home.UpdateFromCursorContext(ctx);
            StatusBar.Language = ctx.Language ?? Settings.DefaultLanguage;
        }

        // ── ITextEditorCommandTarget ──────────────────────────────────────

        public void ToggleBold() => DocumentViewModel?.ToggleBold();
        public void ToggleItalic() => DocumentViewModel?.ToggleItalic();
        public void ToggleUnderline() => DocumentViewModel?.ToggleUnderline();
        public void ToggleStrikethrough() => DocumentViewModel?.ToggleStrikethrough();
        public void ToggleSuperscript() => DocumentViewModel?.ToggleSuperscript();
        public void ToggleSubscript() => DocumentViewModel?.ToggleSubscript();
        public void ToggleAllCaps() => DocumentViewModel?.ToggleAllCaps();
        public void ToggleSmallCaps() => DocumentViewModel?.ToggleSmallCaps();
        public void ClearFormatting() => DocumentViewModel?.ClearFormatting();

        public void SetTextColor(string c) => DocumentViewModel?.SetTextColor(c);
        public void SetHighlightColor(string? c) => DocumentViewModel?.SetHighlightColor(c);
        public void SetFontFamily(string f) => DocumentViewModel?.SetFontFamily(f);
        public void SetFontSize(double s) => DocumentViewModel?.SetFontSize(s);
        public void IncreaseFontSize() => DocumentViewModel?.IncreaseFontSize();
        public void DecreaseFontSize() => DocumentViewModel?.DecreaseFontSize();

        public void SetAlignment(Models.Styles.TextAlignment a) => DocumentViewModel?.SetAlignment(a);
        public void IncreaseIndent() => DocumentViewModel?.IncreaseIndent();
        public void DecreaseIndent() => DocumentViewModel?.DecreaseIndent();
        public void SetLineSpacing(double v) => DocumentViewModel?.SetLineSpacing(v);
        public void SetSpaceBefore(double pt) => DocumentViewModel?.SetSpaceBefore(pt);
        public void SetSpaceAfter(double pt) => DocumentViewModel?.SetSpaceAfter(pt);
        public void ApplyStyle(string name) => DocumentViewModel?.ApplyStyle(name);

        public void ToggleBulletList() => DocumentViewModel?.ToggleBulletList();
        public void ToggleNumberedList() => DocumentViewModel?.ToggleNumberedList();
        public void ToggleMultilevelList() => DocumentViewModel?.ToggleMultilevelList();

        public void Cut() => DocumentViewModel?.Cut();
        public void Copy() => DocumentViewModel?.Copy();
        public void Paste() => DocumentViewModel?.Paste();
        public void SelectAll() => DocumentViewModel?.SelectAll();
        public void Undo() => DocumentViewModel?.Undo();
        public void Redo() => DocumentViewModel?.Redo();

        public void InsertTable(int rows, int cols) => DocumentViewModel?.InsertTable(rows, cols);
        public void InsertImage(string path) => DocumentViewModel?.InsertImage(path);
        public void InsertShape(Models.Document.ShapeType st) => DocumentViewModel?.InsertShape(st);
        public void InsertFloatingTextBox() => DocumentViewModel?.InsertFloatingTextBox();
        public void InsertPageBreak() => DocumentViewModel?.InsertPageBreak();
        public void InsertSectionBreak(Models.Document.BreakType t) => DocumentViewModel?.InsertSectionBreak(t);
        public void InsertFootnote() => DocumentViewModel?.InsertFootnote();
        public void InsertEndnote() => DocumentViewModel?.InsertEndnote();
        public void InsertBookmark(string name) => DocumentViewModel?.InsertBookmark(name);
        public void InsertHyperlink(string url, string? text) => DocumentViewModel?.InsertHyperlink(url, text);
        public void InsertTOC() => DocumentViewModel?.InsertTOC();
        public void InsertComment(string text) => DocumentViewModel?.InsertComment(text);

        public void SetPageSize(PaperSize s) => DocumentViewModel?.SetPageSize(s);
        public void SetPageOrientation(PageOrientation o) => DocumentViewModel?.SetPageOrientation(o);
        public void SetPageMargins(double t, double b, double l, double r)
            => DocumentViewModel?.SetPageMargins(t, b, l, r);
        public void SetColumns(int c) => DocumentViewModel?.SetColumns(c);

        public void SetZoom(double zoom) => DocumentViewModel?.SetZoom(zoom);
        public void SetViewMode(EditorViewMode m) => DocumentViewModel?.SetViewMode(m);
        public void ToggleFullscreen() => DocumentViewModel?.ToggleFullscreen();
        public void ToggleFocusMode() => DocumentViewModel?.ToggleFocusMode();
        public void SetCanvasTheme(CanvasThemePreset p) => DocumentViewModel?.SetCanvasTheme(p);
        public void SetCanvasColors(string bg, string tc) => DocumentViewModel?.SetCanvasColors(bg, tc);

        public void ZoomIn()
        {
            if (DocumentViewModel is null) return;
            double[] steps = { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 2.5, 3.0, 4.0, 5.0 };
            double current = StatusBar.Zoom;
            foreach (double step in steps)
                if (step > current + 0.01)
                {
                    DocumentViewModel.SetZoom(step);
                    StatusBar.Zoom = step;
                    return;
                }
        }

        public void ZoomOut()
        {
            if (DocumentViewModel is null) return;
            double[] steps = { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 2.5, 3.0, 4.0, 5.0 };
            double current = StatusBar.Zoom;
            for (int i = steps.Length - 1; i >= 0; i--)
                if (steps[i] < current - 0.01)
                {
                    DocumentViewModel.SetZoom(steps[i]);
                    StatusBar.Zoom = steps[i];
                    return;
                }
        }

        public void ZoomReset()
        {
            if (DocumentViewModel is null) return;
            DocumentViewModel.SetZoom(1.0);
            StatusBar.Zoom = 1.0;
        }

        public void OpenFind() => DocumentViewModel?.OpenFind();
        public void OpenFindReplace() => DocumentViewModel?.OpenFindReplace();
        public void RunSpellCheck() => DocumentViewModel?.RunSpellCheck();
        public void ShowWordCount() => DocumentViewModel?.ShowWordCount();

        /// <summary>
        /// Поднимает событие PrintRequested с текущим документом и настройками страницы.
        /// TextEditorModule подписан на это событие и выполняет всю логику печати.
        /// </summary>
        public void Print()
        {
            if (DocumentViewModel is null) return;
            _logger.Debug("Print requested: title={Title}", DocumentViewModel.Document.Title);
            PrintRequested?.Invoke(
                DocumentViewModel.Document,
                DocumentViewModel.Document.PageSettings);
        }

        public void ExportToPdf() => DocumentViewModel?.ExportToPdf();
        public void ExportToDocx() => DocumentViewModel?.ExportToDocx();
        public void ExportToTxt() => DocumentViewModel?.ExportToTxt();
        public void ExportToMarkdown() => DocumentViewModel?.ExportToMarkdown();

        // ── Auto save ─────────────────────────────────────────────────────

        private void StartAutoSave(int intervalSeconds)
        {
            _autoSaveSubscription?.Dispose();
            _autoSaveSubscription = null;
            if (intervalSeconds <= 0) return;

            _autoSaveSubscription = Observable
                .Interval(TimeSpan.FromSeconds(intervalSeconds))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ => OnAutoSaveTick());
        }

        private void OnAutoSaveTick()
        {
            if (!IsModified || DocumentViewModel is null) return;
            _logger.Debug("Auto save tick");
            RefreshStatusBar();
        }

        private void RefreshStatusBar()
        {
            if (DocumentViewModel is null) return;

            var sb = new StringBuilder();
            int paraCount = 0;

            foreach (var section in DocumentViewModel.Document.Sections)
                foreach (var block in section.Blocks)
                    if (block is ParagraphBlock para)
                    {
                        sb.Append(para.GetPlainText()).Append(' ');
                        paraCount++;
                    }

            StatusBar.UpdateFromText(sb.ToString(), paraCount, pageCount: 1);
        }

        // ── IDisposable ───────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_documentViewModel is not null)
                _documentViewModel.CursorContextChanged -= OnCursorContextChanged;

            _autoSaveSubscription?.Dispose();
            _paragraphsSubscription?.Dispose();
            _spellCheck.Dispose();
        }

        // ── Paragraph subscriptions ───────────────────────────────────────

        private IDisposable SubscribeToParagraphChanges(DocumentViewModel docVm)
        {
            var subs = new System.Collections.Generic.Dictionary<Guid, IDisposable>();

            void Subscribe(ParagraphViewModel pvm)
            {
                if (subs.ContainsKey(pvm.BlockId)) return;
                subs[pvm.BlockId] = pvm
                    .WhenAnyValue(p => p.PlainText)
                    .Skip(1)
                    .Throttle(TimeSpan.FromMilliseconds(300))
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ => { IsModified = true; RefreshStatusBar(); });
            }

            void Unsubscribe(ParagraphViewModel pvm)
            {
                if (subs.TryGetValue(pvm.BlockId, out var sub))
                {
                    sub.Dispose();
                    subs.Remove(pvm.BlockId);
                }
            }

            foreach (var pvm in docVm.Paragraphs)
                Subscribe(pvm);

            void OnCollectionChanged(object? sender,
                System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
            {
                if (e.NewItems is not null)
                    foreach (ParagraphViewModel pvm in e.NewItems) Subscribe(pvm);
                if (e.OldItems is not null)
                    foreach (ParagraphViewModel pvm in e.OldItems) Unsubscribe(pvm);
            }

            docVm.Paragraphs.CollectionChanged += OnCollectionChanged;

            return System.Reactive.Disposables.Disposable.Create(() =>
            {
                docVm.Paragraphs.CollectionChanged -= OnCollectionChanged;
                foreach (var sub in subs.Values) sub.Dispose();
                subs.Clear();
            });
        }
    }
}