using ReactiveUI;
using ReactiveUI.Avalonia;
using Serilog;
using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Text;
using Writersword.Core.Models.Print;
using Writersword.Modules.TextEditor.Contracts;
using Writersword.Modules.TextEditor.Models;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Page;
using Writersword.Modules.TextEditor.Models.Settings;
using Writersword.Modules.TextEditor.Models.Styles;
using Writersword.Modules.TextEditor.Services;
using Writersword.Modules.TextEditor.ViewModels.Blocks;
using Writersword.Modules.TextEditor.ViewModels.Components;
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

        public DocumentViewModel? DocumentViewModel
        {
            get => _documentViewModel;
            private set => this.RaiseAndSetIfChanged(ref _documentViewModel, value);
        }

        public RibbonViewModel Ribbon { get; }
        public StatusBarViewModel StatusBar { get; }

        /// <summary>
        /// ViewModel линейки — горизонтальной и вертикальной.
        /// </summary>
        public RulerViewModel Ruler { get; }

        public TextEditorSettings Settings { get; private set; } = new();

        public double MonitorSizeInches
        {
            get => _monitorSizeInches;
            private set => this.RaiseAndSetIfChanged(ref _monitorSizeInches, value);
        }

        public bool IsModified
        {
            get => _isModified;
            set => this.RaiseAndSetIfChanged(ref _isModified, value);
        }

        // ── События ───────────────────────────────────────────────────────

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
            Ruler = new RulerViewModel();

            // Подписки на события линейки.
            Ruler.IndentMarkerChanged += OnRulerIndentMarkerChanged;
            Ruler.IndentDragStarted += () => DocumentViewModel?.BeginParagraphFormatBatch();
            Ruler.IndentDragEnded += () => DocumentViewModel?.EndParagraphFormatBatch();
            Ruler.AllColumnWidthsChanged += OnRulerAllColumnWidthsChanged;
            Ruler.AllColumnWidthsChanging += OnRulerAllColumnWidthsChanging;
            Ruler.MarginChanged += OnRulerMarginChanged;
            Ruler.MarginCommitted += OnRulerMarginCommitted;

            // Левый край таблицы через линейку.
            Ruler.TableLeftEdgeChanging += OnRulerTableLeftEdgeChanging;
            Ruler.TableLeftEdgeChanged += OnRulerTableLeftEdgeChanged;

            Ruler.GetMinParagraphIndentMm = () =>
            {
                var doc = DocumentViewModel?.Document;
                if (doc is null) return double.MaxValue;
                double minPt = double.MaxValue;
                foreach (var section in doc.Sections)
                    foreach (var block in section.Blocks)
                        if (block is Writersword.Modules.TextEditor.Models.Document.ParagraphBlock p)
                        {
                            double li = p.Properties.LeftIndent ?? 0;
                            double fi = p.Properties.FirstLineIndent ?? 0;
                            double minIndent = Math.Min(li, li + fi);
                            if (minIndent < minPt) minPt = minIndent;
                        }
                return minPt == double.MaxValue ? double.MaxValue : minPt * 25.4 / 72.0;
            };
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

            // Зум может меняться не только ползунком, но и из канваса (Ctrl + колесо). Подписываемся
            // на DocVm.Zoom и подтягиваем ползунок и линейку. Петли нет: если значение уже совпадает,
            // StatusBar.Zoom не трогаем, поэтому ZoomChanged → SetZoom повторно не срабатывает.
            docVm.PropertyChanged += (_, ev) =>
            {
                if (ev.PropertyName != nameof(docVm.Zoom)) return;
                double z = docVm.Zoom;
                if (Math.Abs(StatusBar.Zoom - z) > 0.0001)
                    StatusBar.Zoom = z;
                Ruler.Zoom = z;
            };
            // Устанавливаем начальный активный параграф чтобы команды тулбара
            // (Bold, Italic и др.) работали без предварительного клика в канвас.
            if (docVm.Paragraphs.Count > 0)
                docVm.SetActiveParagraph(docVm.Paragraphs[0]);
            DocumentViewModel = docVm;

            _paragraphsSubscription?.Dispose();
            _paragraphsSubscription = SubscribeToParagraphChanges(docVm);

            StatusBar.IsSpellCheckActive = Settings.SpellCheckEnabled;
            StatusBar.Zoom = document.Zoom > 0 ? document.Zoom : Settings.DefaultZoom;
            DocumentViewModel?.SetZoom(StatusBar.Zoom);

            SyncRulerToDocument(document);
            Ruler.Zoom = StatusBar.Zoom;
            Ruler.Units = Settings.RulerUnits;
            Ruler.IsVisible = Settings.ShowRuler;

            StartAutoSave(Settings.AutoSaveIntervalSeconds);
            RefreshStatusBar();

            StatusBar.ViewModeChanged = mode =>
            {
                DocumentViewModel?.SetViewMode(mode);
                StatusBar.ViewMode = mode;
            };

            StatusBar.ZoomChanged = zoom =>
            {
                DocumentViewModel?.SetZoom(zoom);
                Ruler.Zoom = zoom;
            };

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

        public void ApplySettings(TextEditorSettings settings)
        {
            _logger.Debug("ApplySettings: MonitorSizeInches={V}", settings.MonitorSizeInches);
            Settings = settings;
            MonitorSizeInches = settings.MonitorSizeInches;
            Ruler.Units = settings.RulerUnits;
            Ruler.IsVisible = settings.ShowRuler;
        }

        // ── Cursor context ────────────────────────────────────────────────

        private void OnCursorContextChanged(CursorContext ctx)
        {
            Ribbon.Home.UpdateFromCursorContext(ctx);
            StatusBar.Language = ctx.Language ?? Settings.DefaultLanguage;

            // Не обновляем маркеры во время drag.
            if (Ruler.DraggingIndentMarker is null)
            {
                Ruler.UpdateFromParagraphContext(
                    ctx.LeftIndentPt,
                    ctx.LeftIndentPt + ctx.FirstLineIndentPt,
                    ctx.RightIndentPt);
            }
        }

        // ── Линейка ───────────────────────────────────────────────────────

        private void SyncRulerToDocument(DocumentModel document)
        {
            var ps = document.PageSettings;
            Ruler.UpdatePageSettings(
                widthMm: ps.GetPhysicalWidthMm(),
                heightMm: ps.GetPhysicalHeightMm(),
                marginLeftMm: ps.MarginLeftMm + ps.MarginGutterMm,
                marginRightMm: ps.MarginRightMm,
                marginTopMm: ps.MarginTopMm,
                marginBottomMm: ps.MarginBottomMm);
        }

        public void NotifyPageSettingsChanged()
        {
            if (DocumentViewModel is null) return;
            SyncRulerToDocument(DocumentViewModel.Document);
        }

        public void NotifyPageOffsetChanged(double pageOffsetXPx)
        {
            Ruler.PageOffsetXPx = pageOffsetXPx;
        }

        /// <summary>
        /// Переключает линейку в режим таблицы при входе каретки в таблицу.
        /// </summary>
        public void NotifyCaretEnteredTable(
            System.Collections.Generic.IReadOnlyList<double> columnOffsetsMm,
            System.Collections.Generic.IReadOnlyList<double> columnWidthsMm,
            double tableOffsetMm = 0,
            int activeColumnIndex = 0)
        {
            Ruler.UpdateTableColumns(columnOffsetsMm, columnWidthsMm, tableOffsetMm);
            Ruler.UpdateActiveCellBounds(activeColumnIndex);
            Ribbon.IsTableTabVisible = true;
            // Синхронизируем кнопку-тоггл режима разбивки с текущей таблицей
            Ribbon.Table.SyncFromTarget();
        }

        /// <summary>
        /// Переключает линейку обратно в режим абзаца при выходе каретки из таблицы.
        /// </summary>
        public void NotifyCaretLeftTable()
        {
            Ruler.SwitchToParagraphMode();
            Ribbon.IsTableTabVisible = false;
        }

        /// <summary>
        /// Показывает/скрывает контекстную вкладку «Формат» (работа с картинкой)
        /// при выделении/снятии выделения изображения на канвасе.
        /// </summary>
        public void NotifyImageSelectionChanged(bool selected)
        {
            Ribbon.IsImageTabVisible = selected;
            if (selected) Ribbon.Image.SyncFromTarget();
        }

        private void OnRulerIndentMarkerChanged(RulerIndentMarkerType markerType, double valueMm)
        {
            // В режиме таблицы маркер позиционируется относительно левого края ячейки.
            // SetLeftIndentPt/SetFirstLineIndentPt ожидают значение относительно начала текстовой зоны.
            // Добавляем смещение ячейки чтобы перевести в абсолютные координаты.
            if (Ruler.Mode == RulerMode.Table)
            {
                double cellOffsetMm = Ruler.UnitsToMm(Ruler.ActiveCellLeftUnits);
                if (markerType != RulerIndentMarkerType.RightIndent)
                    valueMm += cellOffsetMm;
            }

            double valuePt = valueMm * 72.0 / 25.4;

            switch (markerType)
            {
                case RulerIndentMarkerType.LeftIndent:
                    {
                        // Читаем текущие позиции маркеров напрямую — они актуальны во время drag,
                        // тогда как LeftIndentMm/FirstLineIndentMm обновляются только вне drag.
                        double absFirstMm = Ruler.UnitsToMm(
                            Ruler.GetIndentMarkerPosition(RulerIndentMarkerType.FirstLineIndent));
                        double newLeftMm = valueMm;
                        double pageLeftMm = -Ruler.MarginLeftMm;
                        double newAbsFirstMm = Math.Max(absFirstMm, pageLeftMm);
                        double newFirstRelMm = newAbsFirstMm - newLeftMm;
                        DocumentViewModel?.SetLeftIndentPt(newLeftMm * 72.0 / 25.4);
                        DocumentViewModel?.SetFirstLineIndentPt(newFirstRelMm * 72.0 / 25.4);
                        break;
                    }
                case RulerIndentMarkerType.FirstLineIndent:
                    {
                        double leftMm = Ruler.UnitsToMm(
                            Ruler.GetIndentMarkerPosition(RulerIndentMarkerType.LeftIndent));
                        DocumentViewModel?.SetFirstLineIndentPt((valuePt - leftMm * 72.0 / 25.4));
                        break;
                    }
                case RulerIndentMarkerType.RightIndent:
                    DocumentViewModel?.SetRightIndentPt(valuePt);
                    break;
            }
        }

        private void OnRulerMarginChanged(double marginLeftMm, double marginRightMm)
        {
            if (DocumentViewModel is null) return;

            // Фиксируем Auto-колонки всех таблиц до изменения поля.
            // Без этого ComputeColumnWidths пересчитывает их под новую ширину текстовой зоны
            // и таблица визуально растягивается/сжимается.
            var ps = DocumentViewModel.Document.PageSettings;
            double oldTextWidthMm = ps.GetPhysicalWidthMm()
                - ps.MarginLeftMm - ps.MarginGutterMm - ps.MarginRightMm;
            double oldTextWidthPt = oldTextWidthMm * 72.0 / 25.4;
            FreezeAutoColumns(oldTextWidthPt);

            DocumentViewModel.SetPageMargins(
                Ruler.MarginTopMm, Ruler.MarginBottomMm,
                marginLeftMm, marginRightMm);

            double minIndentPt = -marginLeftMm * 72.0 / 25.4;
            bool changed = false;
            var doc = DocumentViewModel.Document;
            foreach (var section in doc.Sections)
                foreach (var block in section.Blocks)
                {
                    if (block is Writersword.Modules.TextEditor.Models.Document.ParagraphBlock p)
                    {
                        double li = p.Properties.LeftIndent ?? 0;
                        double fi = p.Properties.FirstLineIndent ?? 0;
                        if (li < minIndentPt)
                        {
                            p.Properties.LeftIndent = minIndentPt;
                            if (fi < 0 && li + fi < minIndentPt)
                                p.Properties.FirstLineIndent = 0;
                            changed = true;
                        }
                        else if (li + fi < minIndentPt)
                        {
                            p.Properties.FirstLineIndent = minIndentPt - li;
                            changed = true;
                        }
                    }
                    else if (block is TableBlock t)
                    {
                        // Таблица не может уйти левее левого края страницы.
                        if (t.LeftIndentPt < minIndentPt)
                        {
                            t.LeftIndentPt = minIndentPt;
                            changed = true;
                        }
                    }
                }

            if (changed)
                DocumentViewModel.FireParagraphFormatChanged();

            SyncRulerToDocument(DocumentViewModel.Document);
        }

        // Конвертирует все Auto-колонки всех таблиц в Fixed с текущими вычисленными значениями.
        // Вызывается перед изменением полей страницы чтобы таблицы не меняли размер.
        private void FreezeAutoColumns(double textWidthPt)
        {
            if (DocumentViewModel is null) return;
            foreach (var section in DocumentViewModel.Document.Sections)
                foreach (var block in section.Blocks)
                {
                    if (block is not TableBlock table) continue;
                    int colCount = table.Columns.Count;
                    if (colCount == 0) continue;

                    float usedPt = 0f;
                    int autoCount = 0;
                    var fixedPt = new float[colCount];

                    for (int i = 0; i < colCount; i++)
                    {
                        var col = table.Columns[i];
                        if (col.WidthType == TableColumnWidthType.Fixed)
                        {
                            fixedPt[i] = (float)(col.WidthValue * 72.0 / 25.4);
                            usedPt += fixedPt[i];
                        }
                        else if (col.WidthType == TableColumnWidthType.Percent)
                        {
                            fixedPt[i] = (float)(textWidthPt * col.WidthValue / 100.0);
                            usedPt += fixedPt[i];
                        }
                        else
                        {
                            autoCount++;
                        }
                    }

                    if (autoCount == 0) continue;

                    float autoWidth = (float)Math.Max(10.0, (textWidthPt - usedPt) / autoCount);
                    for (int i = 0; i < colCount; i++)
                    {
                        if (table.Columns[i].WidthType != TableColumnWidthType.Auto)
                            continue;
                        table.Columns[i].WidthType = TableColumnWidthType.Fixed;
                        table.Columns[i].WidthValue = autoWidth * 25.4 / 72.0;
                    }
                }
        }

        private void OnRulerMarginCommitted(double marginLeftMm, double marginRightMm)
        {
            if (DocumentViewModel is null) return;
            DocumentViewModel.SetPageMargins(
                Ruler.MarginTopMm, Ruler.MarginBottomMm,
                marginLeftMm, marginRightMm);
            SyncRulerToDocument(DocumentViewModel.Document);
        }


        private void OnRulerAllColumnWidthsChanging(IReadOnlyDictionary<int, double> widths)
        {
            ApplyAllColumnWidths(widths);
        }

        private void OnRulerAllColumnWidthsChanged(IReadOnlyDictionary<int, double> widths)
        {
            ApplyAllColumnWidths(widths);
            _logger.Debug("All column widths changed: {Count} columns", widths.Count);
        }

        /// <summary>
        /// Применяет ширины ВСЕХ колонок активной таблицы одновременно.
        /// Это гарантирует что Auto-колонки не пересчитываются и занимают
        /// именно то место которое задано маркерами линейки.
        /// </summary>
        private void ApplyAllColumnWidths(IReadOnlyDictionary<int, double> widths)
        {
            if (DocumentViewModel is null) return;
            var table = DocumentViewModel.ActiveTable;
            if (table is null) return;

            foreach (var kv in widths)
                DocumentViewModel.TableSetColumnWidth(table, kv.Key, kv.Value);

            DocumentViewModel.FireParagraphFormatChanged();
        }

        /// <summary>
        /// Live-обновление отступа таблицы при drag левого края.
        /// Применяется к ActiveTable напрямую — делегат может быть null если каретка
        /// вышла из таблицы пока пользователь продолжает drag.
        /// </summary>
        private void OnRulerTableLeftEdgeChanging(double leftEdgeMm)
        {
            var table = DocumentViewModel?.ActiveTable;
            if (table is null) return;
            table.LeftIndentPt = leftEdgeMm * 72.0 / 25.4;
            DocumentViewModel?.FireParagraphFormatChanged();
        }

        /// <summary>
        /// Commit отступа таблицы при отпускании drag левого края.
        /// </summary>
        private void OnRulerTableLeftEdgeChanged(double leftEdgeMm)
        {
            var table = DocumentViewModel?.ActiveTable;
            if (table is null) return;
            table.LeftIndentPt = leftEdgeMm * 72.0 / 25.4;
            DocumentViewModel?.FireParagraphFormatChanged();
            _logger.Debug("Table left edge changed: {W}mm", leftEdgeMm);
        }

        // ── ITextEditorCommandTarget ──────────────────────────────────────

        public void ToggleBold() => DocumentViewModel?.ToggleBold();
        public void ToggleItalic() => DocumentViewModel?.ToggleItalic();
        public void ToggleUnderline() => DocumentViewModel?.ToggleUnderline();
        public void ToggleStrikethrough() => DocumentViewModel?.ToggleStrikethrough();
        public void ToggleSuperscript() => DocumentViewModel?.ToggleSuperscript();
        public void ToggleSubscript() => DocumentViewModel?.ToggleSubscript();
        public void ToggleAllCaps() => DocumentViewModel?.ToggleAllCaps();
        public void ChangeCase(Contracts.TextCaseMode mode) => DocumentViewModel?.ChangeCase(mode);
        public void ToggleSmallCaps() => DocumentViewModel?.ToggleSmallCaps();
        public void ClearFormatting() => DocumentViewModel?.ClearFormatting();

        public void SetTextColor(string c) => DocumentViewModel?.SetTextColor(c);
        public void SetHighlightColor(string? c) => DocumentViewModel?.SetHighlightColor(c);
        public void SetFontFamily(string f) => DocumentViewModel?.SetFontFamily(f);
        public void BeginFontPreview() => DocumentViewModel?.BeginFontPreview();
        public void PreviewFontFamily(string f) => DocumentViewModel?.PreviewFontFamily(f);
        public void EndFontPreview(bool commit) => DocumentViewModel?.EndFontPreview(commit);
        public void FocusEditor() => DocumentViewModel?.FocusEditor();
        public void SetFontSize(double s) => DocumentViewModel?.SetFontSize(s);
        public void IncreaseFontSize() => DocumentViewModel?.IncreaseFontSize();
        public void DecreaseFontSize() => DocumentViewModel?.DecreaseFontSize();

        public void SetAlignment(TextAlignment a) => DocumentViewModel?.SetAlignment(a);
        public void IncreaseIndent() => DocumentViewModel?.IncreaseIndent();
        public void DecreaseIndent() => DocumentViewModel?.DecreaseIndent();
        public void SetLineSpacing(double v) => DocumentViewModel?.SetLineSpacing(v);
        public void SetSpaceBefore(double pt) => DocumentViewModel?.SetSpaceBefore(pt);
        public void SetSpaceAfter(double pt) => DocumentViewModel?.SetSpaceAfter(pt);
        public void ApplyStyle(string name) => DocumentViewModel?.ApplyStyle(name);

        public ParagraphProperties? GetActiveParagraphProperties()
            => DocumentViewModel?.GetActiveParagraphProperties();
        public void ApplyParagraphSettings(ParagraphProperties settings)
            => DocumentViewModel?.ApplyParagraphSettings(settings);
        public void SetOutlineLevel(int level) => DocumentViewModel?.SetOutlineLevel(level);

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
        public void InsertShape(ShapeType st) => DocumentViewModel?.InsertShape(st);
        public void InsertFloatingTextBox() => DocumentViewModel?.InsertFloatingTextBox();
        public void InsertPageBreak() => DocumentViewModel?.InsertPageBreak();
        public void InsertSectionBreak(BreakType t) => DocumentViewModel?.InsertSectionBreak(t);
        public void InsertFootnote() => DocumentViewModel?.InsertFootnote();
        public void InsertEndnote() => DocumentViewModel?.InsertEndnote();
        public void InsertBookmark(string name) => DocumentViewModel?.InsertBookmark(name);
        public void InsertHyperlink(string url, string? text) => DocumentViewModel?.InsertHyperlink(url, text);
        public void InsertTOC() => DocumentViewModel?.InsertTOC();
        public void InsertComment(string text) => DocumentViewModel?.InsertComment(text);

        // ── Изображение ───────────────────────────────────────────────────
        public void SetImageWrapMode(WrapMode mode) => DocumentViewModel?.SetImageWrapMode(mode);
        public void SetImageLockAspect(bool locked) => DocumentViewModel?.SetImageLockAspect(locked);
        public void DeleteSelectedImage() => DocumentViewModel?.DeleteSelectedImage();
        public (WrapMode Wrap, bool LockAspect, Writersword.Modules.TextEditor.Models.Styles.TextAlignment Align)? GetSelectedImageInfo()
            => DocumentViewModel?.GetSelectedImageInfo();

        // ── Таблица ───────────────────────────────────────────────────────

        public void TableAddRow(bool above) => DocumentViewModel?.TableAddRow(above);
        public void TableAddColumn(bool left) => DocumentViewModel?.TableAddColumn(left);
        public void TableDeleteRow() => DocumentViewModel?.TableDeleteRow();
        public void TableDeleteColumn() => DocumentViewModel?.TableDeleteColumn();
        public void TableDelete() => DocumentViewModel?.TableDelete();

        public void TableMergeCells() => DocumentViewModel?.TableMergeCells();
        public void TableSplitCell() => DocumentViewModel?.TableSplitCell();
        public void TableSetCellHAlign(Writersword.Modules.TextEditor.Models.Styles.TextAlignment align)
            => DocumentViewModel?.TableSetCellHAlign(align);
        public void TableSetCellVAlign(int vAlign) => DocumentViewModel?.TableSetCellVAlign(vAlign);
        public void TableSetCellBackground(string? color) => DocumentViewModel?.TableSetCellBackground(color);
        public void TableSetCellBorder(string side, BorderStyle style, double thicknessPt, string? color)
            => DocumentViewModel?.TableSetCellBorder(side, style, thicknessPt, color);
        public void TableSetColumnWidth(double widthMm) => DocumentViewModel?.TableSetColumnWidth(widthMm);
        public void TableSetRowHeight(double heightPt) => DocumentViewModel?.TableSetRowHeight(heightPt);
        public void TableAutoFit() => DocumentViewModel?.TableAutoFit();
        public void TableDistributeColumns() => DocumentViewModel?.TableDistributeColumns();
        public void TableDistributeRows() => DocumentViewModel?.TableDistributeRows();
        public void TableSort(int columnIndex, bool ascending) => DocumentViewModel?.TableSort(columnIndex, ascending);

        public void TableToggleRepeatHeader()
        {
            var table = DocumentViewModel?.ActiveTable;
            if (table is null) return;
            table.RepeatHeader = !table.RepeatHeader;
            DocumentViewModel?.FireParagraphFormatChanged();
        }

        public bool TableGetRepeatHeader()
            => DocumentViewModel?.ActiveTable?.RepeatHeader ?? false;

        public void TableToggleSplitMode() => DocumentViewModel?.TableToggleSplitMode();
        public bool TableGetSplitModeByCell() => DocumentViewModel?.TableGetSplitModeByCell() ?? false;
        public void TableSetBreakLabel(string? text) => DocumentViewModel?.TableSetBreakLabel(text);
        public void TableSetContinuationLabel(string? text) => DocumentViewModel?.TableSetContinuationLabel(text);
        public string? TableGetBreakLabel() => DocumentViewModel?.TableGetBreakLabel();
        public string? TableGetContinuationLabel() => DocumentViewModel?.TableGetContinuationLabel();

        // ── Макет страницы ────────────────────────────────────────────────

        public void SetPageSize(PaperSize s) => DocumentViewModel?.SetPageSize(s);
        public void SetPageOrientation(PageOrientation o) => DocumentViewModel?.SetPageOrientation(o);

        public void SetPageMargins(double t, double b, double l, double r)
        {
            DocumentViewModel?.SetPageMargins(t, b, l, r);
            if (DocumentViewModel is not null)
                SyncRulerToDocument(DocumentViewModel.Document);
        }

        public void SetColumns(int c) => DocumentViewModel?.SetColumns(c);

        // ── Вид ───────────────────────────────────────────────────────────

        public void SetZoom(double zoom)
        {
            DocumentViewModel?.SetZoom(zoom);
            Ruler.Zoom = zoom;
        }

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
                    Ruler.Zoom = step;
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
                    Ruler.Zoom = steps[i];
                    return;
                }
        }

        public void ZoomReset()
        {
            if (DocumentViewModel is null) return;
            DocumentViewModel.SetZoom(1.0);
            StatusBar.Zoom = 1.0;
            Ruler.Zoom = 1.0;
        }

        // ── Инструменты ──────────────────────────────────────────────────

        public void OpenFind() => DocumentViewModel?.OpenFind();
        public void OpenFindReplace() => DocumentViewModel?.OpenFindReplace();
        public void RunSpellCheck() => DocumentViewModel?.RunSpellCheck();
        public void ShowWordCount() => DocumentViewModel?.ShowWordCount();

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
                .ObserveOn(AvaloniaScheduler.Instance)
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

            Ruler.IndentMarkerChanged -= OnRulerIndentMarkerChanged;
            Ruler.AllColumnWidthsChanged -= OnRulerAllColumnWidthsChanged;
            Ruler.AllColumnWidthsChanging -= OnRulerAllColumnWidthsChanging;
            Ruler.MarginChanged -= OnRulerMarginChanged;
            Ruler.MarginCommitted -= OnRulerMarginCommitted;
            Ruler.TableLeftEdgeChanging -= OnRulerTableLeftEdgeChanging;
            Ruler.TableLeftEdgeChanged -= OnRulerTableLeftEdgeChanged;

            if (_documentViewModel is not null)
                _documentViewModel.CursorContextChanged -= OnCursorContextChanged;

            _autoSaveSubscription?.Dispose();
            _paragraphsSubscription?.Dispose();
            _spellCheck.Dispose();

            // Явно очищаем параграфы чтобы не ждать GC.
            // Если вью всё ещё жив (Avalonia держит ссылку) — данные
            // освобождаются сразу, а не когда-то после сборки мусора.
            if (_documentViewModel is not null)
            {
                // Очищаем модельные данные документа (ParagraphBlock с TextChunk, Run).
                // Paragraphs.Clear() убирает только VM-обёртки, а сами блоки
                // остаются в Document.Sections[0].Blocks — именно они дают 1.4M объектов.
                var blocks = _documentViewModel.Document?.Sections?.Count > 0
                    ? _documentViewModel.Document.Sections[0].Blocks
                    : null;
                blocks?.Clear();
                _documentViewModel.Paragraphs.Clear();
                _documentViewModel = null;
            }
        }

        // ── Paragraph subscriptions ───────────────────────────────────────

        private IDisposable SubscribeToParagraphChanges(DocumentViewModel docVm)
        {
            // Вместо WhenAnyValue+Throttle на каждый параграф (5000 Rx-цепочек для большого документа)
            // используем один PropertyChanged обработчик + один DispatcherTimer для дебаунса.
            // Это сокращает количество Rx-объектов планировщика с ~25000 до 1.
            var debounce = new Avalonia.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };
            debounce.Tick += (_, _) =>
            {
                debounce.Stop();
                IsModified = true;
                RefreshStatusBar();
            };

            void OnParagraphChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (e.PropertyName != nameof(ParagraphViewModel.PlainText)) return;
                debounce.Stop();
                debounce.Start();
            }

            foreach (var pvm in docVm.Paragraphs)
                pvm.PropertyChanged += OnParagraphChanged;

            void OnCollectionChanged(object? sender,
                System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
            {
                if (e.NewItems is not null)
                    foreach (ParagraphViewModel pvm in e.NewItems)
                        pvm.PropertyChanged += OnParagraphChanged;
                if (e.OldItems is not null)
                    foreach (ParagraphViewModel pvm in e.OldItems)
                        pvm.PropertyChanged -= OnParagraphChanged;
            }

            docVm.Paragraphs.CollectionChanged += OnCollectionChanged;

            return System.Reactive.Disposables.Disposable.Create(() =>
            {
                debounce.Stop();
                docVm.Paragraphs.CollectionChanged -= OnCollectionChanged;
                foreach (var pvm in docVm.Paragraphs)
                    pvm.PropertyChanged -= OnParagraphChanged;
            });
        }
    }
}