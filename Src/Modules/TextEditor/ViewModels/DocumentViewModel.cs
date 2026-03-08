using System;
using System.Collections.ObjectModel;
using ReactiveUI;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Styles;

namespace Writersword.Modules.TextEditor.ViewModels.Blocks
{
    /// <summary>
    /// ViewModel одного параграфа.
    /// Хранит ссылку на модель и вычисляет свойства для привязки в View.
    /// </summary>
    public sealed class ParagraphViewModel : ReactiveObject
    {
        private readonly ParagraphBlock _model;
        private string _plainText;
        private bool _isSelected;
        private bool _isFocused;

        public Guid BlockId => _model.Id;
        public ParagraphBlock Model => _model;

        public bool IsSelected
        {
            get => _isSelected;
            set => this.RaiseAndSetIfChanged(ref _isSelected, value);
        }

        public bool IsFocused
        {
            get => _isFocused;
            set => this.RaiseAndSetIfChanged(ref _isFocused, value);
        }

        /// <summary>
        /// Текст параграфа для двусторонней привязки к TextBox.
        /// При изменении обновляет модель.
        /// </summary>
        public string PlainText
        {
            get => _plainText;
            set
            {
                this.RaiseAndSetIfChanged(ref _plainText, value);
                _model.SetPlainText(value);
            }
        }

        /// <summary>
        /// Событие запроса фокуса на этот параграф.
        /// Вызывается после создания нового параграфа через Enter.
        /// EditorParagraphView подписывается и фокусирует свой TextBox.
        /// </summary>
        public event Action? FocusRequested;

        /// <summary>Запросить фокус на этот параграф.</summary>
        public void RequestFocus() => FocusRequested?.Invoke();

        /// <summary>
        /// Делегат фокуса с установкой каретки на конкретную позицию.
        /// Используется после мержа параграфов чтобы каретка встала в точку слияния.
        /// </summary>
        public Action<int>? RequestFocusAtPosition { get; set; }

        /// <summary>Команда добавления нового параграфа после текущего (Enter).</summary>
        public Func<ParagraphViewModel, ParagraphViewModel>? RequestAddAfter { get; set; }

        /// <summary>Команда удаления текущего параграфа (Backspace на пустом).</summary>
        public Action<ParagraphViewModel>? RequestDelete { get; set; }

        /// <summary>
        /// Команда слияния текущего параграфа с предыдущим.
        /// Передаёт текст текущего параграфа для добавления в конец предыдущего.
        /// Вызывается при Backspace в позиции 0.
        /// </summary>
        public Action<ParagraphViewModel, string>? RequestMergeWithPrevious { get; set; }

        public ParagraphViewModel(ParagraphBlock model)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _plainText = model.GetPlainText();
        }
    }
}

namespace Writersword.Modules.TextEditor.ViewModels
{
    using Writersword.Modules.TextEditor.Models.Document;
    using Writersword.Modules.TextEditor.Models.Inline;
    using Writersword.Modules.TextEditor.Models.Page;
    using Writersword.Modules.TextEditor.Models.Styles;
    using Writersword.Modules.TextEditor.Services;
    using Writersword.Modules.TextEditor.ViewModels.Blocks;
    using Writersword.Modules.TextEditor.ViewModels.Toolbar;

    /// <summary>
    /// ViewModel документа. Реализует <see cref="ITextEditorCommandTarget"/>.
    /// Является основным медиатором между моделью, Ribbon и View параграфов.
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

        public DocumentModel Document => _document;

        /// <summary>Список ViewModel параграфов первого раздела (для отображения в ItemsControl).</summary>
        public ObservableCollection<ParagraphViewModel> Paragraphs { get; } = new();

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

        /// <summary>Настройки листа (цвет фона, текста, пресет).</summary>
        public CanvasSettings CanvasSettings => _document.CanvasSettings;

        /// <summary>Настройки страницы первого раздела или документа.</summary>
        public PageSettings PageSettings => _document.PageSettings;

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

            RebuildParagraphViewModels();
        }

        /// <summary>
        /// Добавляет новый параграф после указанного.
        /// Двойной Post гарантирует что новый View успеет смонтироваться
        /// в визуальное дерево до запроса фокуса.
        /// </summary>
        public ParagraphViewModel AddParagraphAfter(ParagraphViewModel after)
        {
            var section = _document.Sections[0];
            var newBlock = new ParagraphBlock();

            int modelIndex = section.Blocks.IndexOf(after.Model);
            if (modelIndex < 0)
                section.Blocks.Add(newBlock);
            else
                section.Blocks.Insert(modelIndex + 1, newBlock);

            int vmIndex = Paragraphs.IndexOf(after);
            var newVm = CreateParagraphViewModel(newBlock);
            Paragraphs.Insert(vmIndex + 1, newVm);

            // Двойной Post: первый ждёт рендера, второй ждёт монтирования в дерево
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => newVm.RequestFocus(),
                    Avalonia.Threading.DispatcherPriority.Input),
                Avalonia.Threading.DispatcherPriority.Render);

            return newVm;
        }

        /// <summary>
        /// Удаляет параграф и переводит фокус на предыдущий.
        /// Не удаляет если параграф единственный.
        /// </summary>
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

        /// <summary>
        /// Переносит текст параграфа в конец предыдущего и удаляет текущий.
        /// Если параграф первый — ничего не делает.
        /// Каретка устанавливается в точку слияния (конец исходного текста предыдущего параграфа).
        /// </summary>
        public void MergeParagraphWithPrevious(ParagraphViewModel target, string textToMerge)
        {
            int vmIndex = Paragraphs.IndexOf(target);
            if (vmIndex <= 0) return;

            var previous = Paragraphs[vmIndex - 1];

            // Позиция каретки — конец текста предыдущего параграфа до слияния
            int caretPosition = previous.PlainText.Length;

            // Переносим текст в предыдущий параграф
            previous.PlainText = previous.PlainText + textToMerge;

            // Удаляем текущий параграф
            _document.Sections[0].Blocks.Remove(target.Model);
            Paragraphs.RemoveAt(vmIndex);

            // Фокус на предыдущий параграф с кареткой в точке слияния
            previous.RequestFocusAtPosition?.Invoke(caretPosition);
        }

        // --- ITextEditorCommandTarget ---

        public void ToggleBold() => ApplyCharProperty(p => p.IsBold = !p.IsBold);
        public void ToggleItalic() => ApplyCharProperty(p => p.IsItalic = !p.IsItalic);
        public void ToggleUnderline() => ApplyCharProperty(p => p.IsUnderline = !p.IsUnderline);
        public void ToggleStrikethrough() => ApplyCharProperty(p => p.IsStrikethrough = !p.IsStrikethrough);
        public void ToggleSuperscript() => ApplyCharProperty(p => { p.IsSuperscript = !p.IsSuperscript; if (p.IsSuperscript) p.IsSubscript = false; });
        public void ToggleSubscript() => ApplyCharProperty(p => { p.IsSubscript = !p.IsSubscript; if (p.IsSubscript) p.IsSuperscript = false; });
        public void ToggleAllCaps() => ApplyCharProperty(p => p.IsAllCaps = !p.IsAllCaps);
        public void ToggleSmallCaps() => ApplyCharProperty(p => p.IsSmallCaps = !p.IsSmallCaps);
        public void ClearFormatting() => ApplyCharProperty(_ => { }, clearAll: true);
        public void SetTextColor(string color) => ApplyCharProperty(p => p.TextColor = color);
        public void SetHighlightColor(string? color) => ApplyCharProperty(p => p.HighlightColor = color);
        public void SetFontFamily(string font) => ApplyCharProperty(p => p.FontFamily = font);
        public void SetFontSize(double size) => ApplyCharProperty(p => p.FontSize = size);
        public void IncreaseFontSize() => ApplyCharProperty(p => p.FontSize = (p.FontSize ?? 14) + 2);
        public void DecreaseFontSize() => ApplyCharProperty(p => p.FontSize = Math.Max(1, (p.FontSize ?? 14) - 2));

        public void SetAlignment(TextAlignment alignment) => ApplyParaProperty(p => p.Alignment = alignment);
        public void IncreaseIndent() => ApplyParaProperty(p => p.LeftIndent = (p.LeftIndent ?? 0) + 18);
        public void DecreaseIndent() => ApplyParaProperty(p => p.LeftIndent = Math.Max(0, (p.LeftIndent ?? 0) - 18));
        public void SetLineSpacing(double multiplier) => ApplyParaProperty(p =>
        {
            p.LineSpacingRule = LineSpacingRule.Auto;
            p.LineSpacingValue = multiplier;
        });
        public void SetSpaceBefore(double pt) => ApplyParaProperty(p => p.SpaceBefore = pt);
        public void SetSpaceAfter(double pt) => ApplyParaProperty(p => p.SpaceAfter = pt);
        public void ApplyStyle(string styleName) => ApplyParaProperty(p => p.StyleName = styleName);

        public void ToggleBulletList() { }
        public void ToggleNumberedList() { }
        public void ToggleMultilevelList() { }

        public void Cut() { }
        public void Copy() { }
        public void Paste() { }
        public void SelectAll() { }
        public void Undo() { }
        public void Redo() { }

        public void InsertTable(int rows, int columns) { InsertBlock(BuildEmptyTable(rows, columns)); }
        public void InsertImage(string filePath) { }
        public void InsertShape(Models.Document.ShapeType st) { }
        public void InsertFloatingTextBox() { }
        public void InsertPageBreak() { InsertBlock(new BreakBlock { BreakType = BreakType.Page }); }
        public void InsertSectionBreak(BreakType t) { InsertBlock(new BreakBlock { BreakType = t }); }
        public void InsertFootnote() { AddAnnotation(InlineAnnotationType.Footnote); }
        public void InsertEndnote() { AddAnnotation(InlineAnnotationType.Endnote); }
        public void InsertBookmark(string name) { AddAnnotation(InlineAnnotationType.Bookmark, name); }
        public void InsertHyperlink(string url, string? text) { AddAnnotation(InlineAnnotationType.Hyperlink, url: url); }
        public void InsertTOC() { }
        public void InsertComment(string text) { AddAnnotation(InlineAnnotationType.Comment, content: text); }

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
        public void SetColumns(int count) { _document.ColumnSettings.ColumnCount = count; }
        public void SetZoom(double zoom) { Zoom = zoom; }
        public void SetViewMode(EditorViewMode mode) { ViewMode = mode; _document.ViewMode = mode; }
        public void ToggleFullscreen() { IsFullscreen = !IsFullscreen; }
        public void ToggleFocusMode() { IsFocusMode = !IsFocusMode; }
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

        public void OpenFind() { }
        public void OpenFindReplace() { }
        public void RunSpellCheck() { }
        public void ShowWordCount() { }
        public void Print() { }
        public void ExportToPdf() { }
        public void ExportToDocx() { }
        public void ExportToTxt() { }
        public void ExportToMarkdown() { }

        // --- Внутренние методы ---

        private void ApplyCharProperty(Action<RunProperties> mutate, bool clearAll = false)
        {
            _ = mutate;
        }

        private void ApplyParaProperty(Action<ParagraphProperties> mutate)
        {
            _ = mutate;
        }

        private void InsertBlock(BlockModel block)
        {
            if (_document.Sections.Count == 0) return;
            _document.Sections[0].Blocks.Add(block);
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

        private void RebuildParagraphViewModels()
        {
            Paragraphs.Clear();
            if (_document.Sections.Count == 0) return;

            foreach (var block in _document.Sections[0].Blocks)
            {
                if (block is ParagraphBlock para)
                    Paragraphs.Add(CreateParagraphViewModel(para));
            }
        }

        private ParagraphViewModel CreateParagraphViewModel(ParagraphBlock block)
        {
            var vm = new ParagraphViewModel(block);
            vm.RequestAddAfter = AddParagraphAfter;
            vm.RequestDelete = pvm => DeleteParagraph(pvm);
            vm.RequestMergeWithPrevious = MergeParagraphWithPrevious;
            return vm;
        }
    }
}