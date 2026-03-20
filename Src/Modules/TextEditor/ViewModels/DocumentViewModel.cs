using Avalonia.Threading;
using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using Writersword.Core.Models.Print;
using Writersword.Modules.TextEditor.Contracts;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.Models.Page;
using Writersword.Modules.TextEditor.Models.Styles;
using Writersword.Modules.TextEditor.Services;
using Writersword.Modules.TextEditor.ViewModels.Blocks;
using Writersword.Modules.TextEditor.ViewModels.Toolbar;

namespace Writersword.Modules.TextEditor.ViewModels
{
    /// <summary>
    /// ViewModel документа. Реализует ITextEditorCommandTarget.
    /// Является медиатором между моделью, Ribbon и View параграфов.
    /// </summary>
    public sealed class DocumentViewModel : ReactiveObject, ITextEditorCommandTarget
    {
        private readonly DocumentModel _document;
        private readonly ChunkManager _chunkManager;
        private readonly AutoReplaceService _autoReplace;
        private readonly SpellCheckService _spellCheck;

        private EditorViewMode _viewMode;
        private double _zoom = 1.0;
        private bool _isFocusMode;
        private bool _isFullscreen;
        private bool _isReadOnly;

        private ParagraphViewModel? _activeParagraph;

        public DocumentModel Document => _document;

        /// <summary>Список ViewModel параграфов первого раздела.</summary>
        public ObservableCollection<ParagraphViewModel> Paragraphs { get; } = new();

        /// <summary>Имена стилей документа для ComboBox в Ribbon.</summary>
        public ObservableCollection<string> AvailableStyleNames { get; } = new();

        /// <summary>Событие изменения контекста курсора.</summary>
        public event Action<CursorContext>? CursorContextChanged;

        public EditorViewMode ViewMode
        {
            get => _viewMode;
            set => this.RaiseAndSetIfChanged(ref _viewMode, value);
        }

        public double Zoom
        {
            get => _zoom;
            set
            {
                double clamped = Math.Max(0.25, Math.Min(5.0, value));
                this.RaiseAndSetIfChanged(ref _zoom, clamped);
                _document.Zoom = clamped;
            }
        }

        public bool IsFocusMode
        {
            get => _isFocusMode;
            set => this.RaiseAndSetIfChanged(ref _isFocusMode, value);
        }

        public bool IsFullscreen
        {
            get => _isFullscreen;
            set => this.RaiseAndSetIfChanged(ref _isFullscreen, value);
        }

        public bool IsReadOnly
        {
            get => _isReadOnly;
            set => this.RaiseAndSetIfChanged(ref _isReadOnly, value);
        }

        public CanvasSettings CanvasSettings => _document.CanvasSettings;
        public TextEditorPageSettings PageSettings => _document.PageSettings;

        public DocumentViewModel(
            DocumentModel document,
            ChunkManager chunkManager,
            AutoReplaceService autoReplace,
            SpellCheckService spellCheck)
        {
            _document = document ?? throw new ArgumentNullException(nameof(document));
            _chunkManager = chunkManager ?? throw new ArgumentNullException(nameof(chunkManager));
            _autoReplace = autoReplace ?? throw new ArgumentNullException(nameof(autoReplace));
            _spellCheck = spellCheck ?? throw new ArgumentNullException(nameof(spellCheck));

            _viewMode = document.ViewMode;
            _zoom = document.Zoom;

            RebuildStyleNames();
            RebuildParagraphViewModels();
        }

        // ── Активный параграф ─────────────────────────────────────────────

        public void SetActiveParagraph(ParagraphViewModel vm)
        {
            _activeParagraph = vm;
            FireCursorContextChanged();
        }

        public void FireCursorContextChanged()
        {
            if (_activeParagraph is null) return;
            CursorContextChanged?.Invoke(BuildCursorContext(_activeParagraph));
        }

        private CursorContext BuildCursorContext(ParagraphViewModel pvm)
        {
            var ctx = new CursorContext();
            var block = pvm.Model;

            RunProperties? rp = null;

            if (pvm.SelectionEnd > pvm.SelectionStart)
            {
                int offset = 0;
                foreach (var chunk in block.Chunks)
                    foreach (var run in chunk.Runs)
                    {
                        if (offset + run.Text.Length > pvm.SelectionStart) { rp = run.Properties; goto foundRun; }
                        offset += run.Text.Length;
                    }
                foundRun:;
            }
            else
            {
                if (block.Chunks.Count > 0 && block.Chunks[0].Runs.Count > 0)
                    rp = block.Chunks[0].Runs[0].Properties;
            }

            if (rp is not null)
            {
                ctx.IsBold = rp.IsBold;
                ctx.IsItalic = rp.IsItalic;
                ctx.IsUnderline = rp.IsUnderline;
                ctx.IsStrikethrough = rp.IsStrikethrough;
                ctx.IsSuperscript = rp.IsSuperscript;
                ctx.IsSubscript = rp.IsSubscript;
                ctx.IsAllCaps = rp.IsAllCaps;
                ctx.TextColor = rp.TextColor ?? "#1A1A1A";
                ctx.HighlightColor = rp.HighlightColor;
                ctx.FontFamily = rp.FontFamily ?? ResolveStyleFontFamily(block.Properties.StyleName);
                ctx.FontSize = rp.FontSize ?? ResolveStyleFontSize(block.Properties.StyleName);
            }
            else
            {
                ctx.FontFamily = ResolveStyleFontFamily(block.Properties.StyleName);
                ctx.FontSize = ResolveStyleFontSize(block.Properties.StyleName);
                ctx.TextColor = "#1A1A1A";
            }

            ctx.Alignment = block.Properties.Alignment ?? TextAlignment.Left;
            ctx.StyleName = block.Properties.StyleName ?? "Normal";
            return ctx;
        }

        private string ResolveStyleFontFamily(string? styleName)
        {
            var style = _document.FindStyle(styleName ?? "Normal");
            return style?.RunProperties?.FontFamily ?? "Times New Roman";
        }

        private double ResolveStyleFontSize(string? styleName)
        {
            var style = _document.FindStyle(styleName ?? "Normal");
            return style?.RunProperties?.FontSize ?? 14.0;
        }

        // ── Управление параграфами ────────────────────────────────────────

        public ParagraphViewModel AddParagraphAfter(ParagraphViewModel after)
        {
            var section = _document.Sections[0];
            var newBlock = new ParagraphBlock();
            newBlock.Properties.StyleName = after.Model.Properties.StyleName ?? "Normal";

            int modelIndex = section.Blocks.IndexOf(after.Model);
            if (modelIndex < 0) section.Blocks.Add(newBlock);
            else section.Blocks.Insert(modelIndex + 1, newBlock);

            int vmIndex = Paragraphs.IndexOf(after);
            var newVm = CreateParagraphViewModel(newBlock);
            Paragraphs.Insert(vmIndex + 1, newVm);

            return newVm;
        }

        public ParagraphViewModel? DeleteParagraph(ParagraphViewModel target)
        {
            if (Paragraphs.Count <= 1) return null;

            int vmIndex = Paragraphs.IndexOf(target);
            if (vmIndex < 0) return null;

            _document.Sections[0].Blocks.Remove(target.Model);
            Paragraphs.RemoveAt(vmIndex);

            int focusIndex = Math.Max(0, vmIndex - 1);
            var focusVm = Paragraphs[focusIndex];
            focusVm.RequestFocus();
            return focusVm;
        }

        public void MergeParagraphWithPrevious(ParagraphViewModel target, string textToMerge)
        {
            int vmIndex = Paragraphs.IndexOf(target);
            if (vmIndex <= 0) return;

            var previous = Paragraphs[vmIndex - 1];
            int caretPosition = previous.PlainText.Length;
            previous.PlainText = previous.PlainText + textToMerge;

            _document.Sections[0].Blocks.Remove(target.Model);
            Paragraphs.RemoveAt(vmIndex);

            previous.RequestFocusAtPosition?.Invoke(caretPosition);
        }

        public void SelectAll() { foreach (var p in Paragraphs) p.IsSelected = true; }
        public void ClearSelection() { foreach (var p in Paragraphs) p.IsSelected = false; }

        public string? GetDocumentSelectedText()
        {
            var selected = Paragraphs.Where(p => p.IsSelected).ToList();
            if (selected.Count == 0) return null;
            return string.Join(Environment.NewLine, selected.Select(p => p.PlainText));
        }

        // ── ITextEditorCommandTarget: символы ─────────────────────────────

        public void ToggleBold() => ApplyCharProperty(p => p.IsBold = !p.IsBold);
        public void ToggleItalic() => ApplyCharProperty(p => p.IsItalic = !p.IsItalic);
        public void ToggleUnderline() => ApplyCharProperty(p => p.IsUnderline = !p.IsUnderline);
        public void ToggleStrikethrough() => ApplyCharProperty(p => p.IsStrikethrough = !p.IsStrikethrough);

        public void ToggleSuperscript()
        {
            ApplyCharProperty(p => { p.IsSuperscript = !p.IsSuperscript; if (p.IsSuperscript) p.IsSubscript = false; });
        }

        public void ToggleSubscript()
        {
            ApplyCharProperty(p => { p.IsSubscript = !p.IsSubscript; if (p.IsSubscript) p.IsSuperscript = false; });
        }

        public void ToggleAllCaps() => ApplyCharProperty(p => p.IsAllCaps = !p.IsAllCaps);
        public void ToggleSmallCaps() => ApplyCharProperty(p => p.IsSmallCaps = !p.IsSmallCaps);
        public void ClearFormatting() => ApplyCharProperty(_ => { }, clearAll: true);

        public void SetTextColor(string color) => ApplyCharProperty(p => p.TextColor = color);
        public void SetHighlightColor(string? color) => ApplyCharProperty(p => p.HighlightColor = color);
        public void SetFontFamily(string font) => ApplyCharProperty(p => p.FontFamily = font);

        public void SetFontSize(double size)
            => ApplyCharProperty(p => p.FontSize = size > 0 ? size : (double?)null);

        public void IncreaseFontSize()
        {
            double current = ResolveCurrentFontSize();
            ApplyCharProperty(p => p.FontSize = current + 2);
        }

        public void DecreaseFontSize()
        {
            double current = ResolveCurrentFontSize();
            ApplyCharProperty(p => p.FontSize = Math.Max(1, current - 2));
        }

        private double ResolveCurrentFontSize()
        {
            if (_activeParagraph is null) return 14;
            var block = _activeParagraph.Model;
            if (block.Chunks.Count > 0 && block.Chunks[0].Runs.Count > 0)
                return block.Chunks[0].Runs[0].Properties?.FontSize
                    ?? ResolveStyleFontSize(block.Properties.StyleName);
            return ResolveStyleFontSize(block.Properties.StyleName);
        }

        // ── ITextEditorCommandTarget: абзац ───────────────────────────────

        public void SetAlignment(TextAlignment a) => ApplyParaProperty(p => p.Alignment = a);
        public void IncreaseIndent() => ApplyParaProperty(p => p.LeftIndent = (p.LeftIndent ?? 0) + 18);
        public void DecreaseIndent() => ApplyParaProperty(p => p.LeftIndent = Math.Max(0, (p.LeftIndent ?? 0) - 18));
        public void SetLineSpacing(double v) => ApplyParaProperty(p => { p.LineSpacingRule = LineSpacingRule.Auto; p.LineSpacingValue = v; });
        public void SetSpaceBefore(double pt) => ApplyParaProperty(p => p.SpaceBefore = pt);
        public void SetSpaceAfter(double pt) => ApplyParaProperty(p => p.SpaceAfter = pt);
        public void ApplyStyle(string name) => ApplyParaProperty(p => p.StyleName = name);

        // ── ITextEditorCommandTarget: списки ──────────────────────────────

        public void ToggleBulletList()
        {
            if (_activeParagraph is null) return;
            var block = _activeParagraph.Model;
            block.ListProperties = block.ListProperties?.MarkerType == ListMarkerType.Bullet
                ? null
                : new ListProperties { ListId = Guid.NewGuid(), Level = 0, MarkerType = ListMarkerType.Bullet };
            FireCursorContextChanged();
        }

        public void ToggleNumberedList()
        {
            if (_activeParagraph is null) return;
            var block = _activeParagraph.Model;
            block.ListProperties = block.ListProperties?.MarkerType == ListMarkerType.Decimal
                ? null
                : new ListProperties { ListId = Guid.NewGuid(), Level = 0, MarkerType = ListMarkerType.Decimal };
            FireCursorContextChanged();
        }

        public void ToggleMultilevelList()
        {
            if (_activeParagraph is null) return;
            var block = _activeParagraph.Model;
            if (block.ListProperties is null)
                block.ListProperties = new ListProperties { ListId = Guid.NewGuid(), Level = 0, MarkerType = ListMarkerType.Decimal };
            else
                block.ListProperties.Level = (block.ListProperties.Level + 1) % 9;
            FireCursorContextChanged();
        }

        // ── ITextEditorCommandTarget: буфер обмена ────────────────────────

        public void Cut() { _activeParagraph?.RequestFocus(); }
        public void Copy()
        {
            string? docText = GetDocumentSelectedText();
            if (docText is not null) { CopyToClipboardAsync(docText); return; }
            _activeParagraph?.RequestFocus();
        }
        public void Paste() { _activeParagraph?.RequestFocus(); }

        void ITextEditorCommandTarget.SelectAll() => SelectAll();
        public void Undo() { }
        public void Redo() { }

        // ── ITextEditorCommandTarget: вставка ─────────────────────────────

        public void InsertTable(int rows, int columns) => InsertBlock(BuildEmptyTable(rows, columns));
        public void InsertImage(string filePath) { }
        public void InsertShape(ShapeType st) { }
        public void InsertFloatingTextBox() { }
        public void InsertPageBreak() => InsertBlock(new BreakBlock { BreakType = BreakType.Page });
        public void InsertSectionBreak(BreakType t) => InsertBlock(new BreakBlock { BreakType = t });
        public void InsertFootnote() => AddAnnotation(InlineAnnotationType.Footnote);
        public void InsertEndnote() => AddAnnotation(InlineAnnotationType.Endnote);
        public void InsertBookmark(string name) => AddAnnotation(InlineAnnotationType.Bookmark, bookmarkName: name);
        public void InsertHyperlink(string url, string? text) => AddAnnotation(InlineAnnotationType.Hyperlink, url: url);
        public void InsertTOC() { }
        public void InsertComment(string text) => AddAnnotation(InlineAnnotationType.Comment, content: text);

        // ── ITextEditorCommandTarget: макет ───────────────────────────────

        public void SetPageSize(PaperSize size)
        {
            _document.PageSettings.ApplyPaperSize(size);
            this.RaisePropertyChanged(nameof(PageSettings));
        }

        public void SetPageOrientation(PageOrientation o)
        {
            _document.PageSettings.Orientation = o;
            this.RaisePropertyChanged(nameof(PageSettings));
        }

        public void SetPageMargins(double top, double bottom, double left, double right)
        {
            _document.PageSettings.MarginTopMm = top;
            _document.PageSettings.MarginBottomMm = bottom;
            _document.PageSettings.MarginLeftMm = left;
            _document.PageSettings.MarginRightMm = right;
            this.RaisePropertyChanged(nameof(PageSettings));
        }

        public void SetColumns(int count) => _document.ColumnSettings.ColumnCount = count;

        // ── ITextEditorCommandTarget: вид ─────────────────────────────────

        public void SetZoom(double zoom) => Zoom = zoom;
        public void SetViewMode(EditorViewMode mode) { ViewMode = mode; _document.ViewMode = mode; }
        public void ToggleFullscreen() => IsFullscreen = !IsFullscreen;
        public void ToggleFocusMode() => IsFocusMode = !IsFocusMode;

        public void SetCanvasTheme(CanvasThemePreset preset)
        {
            _document.CanvasSettings.ApplyPreset(preset);
            this.RaisePropertyChanged(nameof(CanvasSettings));
        }

        public void SetCanvasColors(string pageBackground, string textColor)
        {
            _document.CanvasSettings.Preset = CanvasThemePreset.Custom;
            _document.CanvasSettings.PageBackgroundColor = pageBackground;
            _document.CanvasSettings.DefaultTextColor = textColor;
            this.RaisePropertyChanged(nameof(CanvasSettings));
        }

        public void ZoomIn()
        {
            double[] steps = { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 2.5, 3.0, 4.0, 5.0 };
            foreach (double step in steps)
                if (step > Zoom + 0.01) { Zoom = step; return; }
        }

        public void ZoomOut()
        {
            double[] steps = { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 2.5, 3.0, 4.0, 5.0 };
            for (int i = steps.Length - 1; i >= 0; i--)
                if (steps[i] < Zoom - 0.01) { Zoom = steps[i]; return; }
        }

        public void ZoomReset() => Zoom = 1.0;

        // ── ITextEditorCommandTarget: поиск / инструменты / экспорт ───────

        public void OpenFind() { }
        public void OpenFindReplace() { }
        public void RunSpellCheck() { }
        public void ShowWordCount() { }
        public void Print() { }
        public void ExportToPdf() { }
        public void ExportToDocx() { }
        public void ExportToTxt() { }
        public void ExportToMarkdown() { }

        // ── Внутренние методы ─────────────────────────────────────────────

        private void ApplyCharProperty(Action<RunProperties> mutate, bool clearAll = false)
        {
            if (_activeParagraph is null) return;

            var block = _activeParagraph.Model;
            int selStart = _activeParagraph.SelectionStart;
            int selEnd = _activeParagraph.SelectionEnd;
            bool hasSelection = selEnd > selStart;
            int globalOffset = 0;

            foreach (var chunk in block.Chunks)
            {
                foreach (var run in chunk.Runs)
                {
                    int runStart = globalOffset;
                    int runEnd = globalOffset + run.Text.Length;
                    bool inRange = !hasSelection || (runEnd > selStart && runStart < selEnd);

                    if (inRange)
                    {
                        if (clearAll) run.Properties = null;
                        else
                        {
                            run.Properties ??= new RunProperties();
                            mutate(run.Properties);
                            if (run.Properties.IsDefault()) run.Properties = null;
                        }
                    }

                    globalOffset += run.Text.Length;
                }
                chunk.InvalidateLength();
            }

            FireCursorContextChanged();
        }

        private void ApplyParaProperty(Action<ParagraphProperties> mutate)
        {
            if (_activeParagraph is null) return;
            mutate(_activeParagraph.Model.Properties);
            FireCursorContextChanged();
        }

        private void InsertBlock(BlockModel block)
        {
            if (_document.Sections.Count == 0) return;
            var section = _document.Sections[0];

            if (_activeParagraph is not null)
            {
                int idx = section.Blocks.IndexOf(_activeParagraph.Model);
                if (idx >= 0) { section.Blocks.Insert(idx + 1, block); RebuildParagraphViewModels(); return; }
            }

            section.Blocks.Add(block);
            RebuildParagraphViewModels();
        }

        private void AddAnnotation(
            InlineAnnotationType type,
            string? bookmarkName = null,
            string? content = null,
            string? url = null)
        {
            _document.Annotations.Add(new InlineAnnotation
            {
                Type = type,
                BookmarkName = bookmarkName,
                Content = content,
                Url = url
            });
        }

        private static TableBlock BuildEmptyTable(int rows, int columns)
        {
            var table = new TableBlock { RowCount = rows, ColumnCount = columns };
            for (int c = 0; c < columns; c++)
                table.Columns.Add(new TableColumnDefinition { WidthType = TableColumnWidthType.Auto });
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < columns; c++)
                    table.Cells.Add(new TableCell { Row = r, Column = c });
            return table;
        }

        private void RebuildStyleNames()
        {
            AvailableStyleNames.Clear();
            foreach (var style in _document.Styles)
                AvailableStyleNames.Add(style.DisplayName.Length > 0 ? style.DisplayName : style.Name);
        }

        private void RebuildParagraphViewModels()
        {
            Paragraphs.Clear();
            if (_document.Sections.Count == 0) return;
            foreach (var block in _document.Sections[0].Blocks)
                if (block is ParagraphBlock para)
                    Paragraphs.Add(CreateParagraphViewModel(para));
        }

        public void DeleteSelectedParagraphs()
        {
            var toDelete = Paragraphs.Where(p => p.IsSelected).ToList();
            if (toDelete.Count == 0) return;

            int firstIdx = Paragraphs.IndexOf(toDelete[0]);
            int focusIdx = Math.Max(0, firstIdx - 1);

            foreach (var pvm in toDelete)
            {
                _document.Sections[0].Blocks.Remove(pvm.Model);
                Paragraphs.Remove(pvm);
            }

            if (Paragraphs.Count == 0)
            {
                var empty = new ParagraphBlock();
                _document.Sections[0].Blocks.Add(empty);
                Paragraphs.Add(CreateParagraphViewModel(empty));
            }

            Paragraphs[Math.Min(focusIdx, Paragraphs.Count - 1)].RequestFocus();
        }

        private ParagraphViewModel CreateParagraphViewModel(ParagraphBlock block)
        {
            var vm = new ParagraphViewModel(block);
            vm.RequestAddAfter = AddParagraphAfter;
            vm.RequestDelete = pvm => DeleteParagraph(pvm);
            vm.RequestMergeWithPrevious = MergeParagraphWithPrevious;
            vm.RequestSelectAll = SelectAll;
            vm.RequestClearSelection = ClearSelection;
            vm.RequestGetDocumentSelectedText = GetDocumentSelectedText;
            vm.OnActivated = SetActiveParagraph;
            vm.RequestDeleteSelected = DeleteSelectedParagraphs;
            vm.OnSelectionChanged = _ => FireCursorContextChanged();
            return vm;
        }

        private static async void CopyToClipboardAsync(string text)
        {
            try
            {
                var lifetime = Avalonia.Application.Current?.ApplicationLifetime
                    as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
                var clipboard = lifetime?.MainWindow?.Clipboard;
                if (clipboard is not null)
                    await clipboard.SetTextAsync(text);
            }
            catch { }
        }

        public void PasteTextAtCursor(string text)
        {
            if (_activeParagraph is null) return;

            string[] lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            int caretPos = _activeParagraph.SelectionStart;
            string before = _activeParagraph.PlainText[..caretPos];
            string after = _activeParagraph.PlainText[caretPos..];

            if (lines.Length == 1)
            {
                _activeParagraph.PlainText = before + lines[0] + after;
                _activeParagraph.SelectionStart = caretPos + lines[0].Length;
                _activeParagraph.SelectionEnd = _activeParagraph.SelectionStart;
                _activeParagraph.RequestFocusAtPosition?.Invoke(_activeParagraph.SelectionStart);
                return;
            }

            _activeParagraph.PlainText = before + lines[0];
            ParagraphViewModel prev = _activeParagraph;

            for (int i = 1; i < lines.Length - 1; i++)
            {
                var newVm = AddParagraphAfter(prev);
                newVm.PlainText = lines[i];
                prev = newVm;
            }

            var last = AddParagraphAfter(prev);
            last.PlainText = lines[^1] + after;
            last.RequestFocusAtPosition?.Invoke(lines[^1].Length);
        }
    }
}