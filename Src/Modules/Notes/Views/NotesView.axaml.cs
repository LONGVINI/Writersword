using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using System;
using System.ComponentModel;
using System.Linq;
using Writersword.Modules.Notes.Models;
using Writersword.Modules.Notes.ViewModels;

namespace Writersword.Modules.Notes.Views
{
    public partial class NotesView : UserControl
    {
        private const double CompactWidth = 560;
        private readonly NotesLineTransformer _lineTransformer;
        private NotesViewModel? _subscribedViewModel;
        private bool _isUpdatingEditor;

        public NotesView()
        {
            InitializeComponent();

            _lineTransformer = new NotesLineTransformer(this);
            Editor.TextArea.TextView.LineTransformers.Add(_lineTransformer);
            Editor.Document.TextChanged += OnDocumentTextChanged;
            Editor.TextArea.Caret.PositionChanged += OnCaretPositionChanged;

            DataContextChanged += OnDataContextChanged;
            SizeChanged += OnSizeChanged;
        }

        private NotesViewModel? ViewModel => DataContext as NotesViewModel;

        private void OnDataContextChanged(object? sender, EventArgs e)
        {
            if (_subscribedViewModel != null)
                _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;

            _subscribedViewModel = ViewModel;
            if (_subscribedViewModel != null)
                _subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;

            LoadSelectedPage();
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(NotesViewModel.SelectedPage))
                LoadSelectedPage();
        }

        private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            // Ширина измеряется у самого содержимого Dock: плавающая вкладка
            // переключается в компактный режим независимо от главного окна.
            if (ViewModel != null)
                ViewModel.IsCompact = e.NewSize.Width < CompactWidth;
        }

        private void OnAddPageClick(object? sender, RoutedEventArgs e)
        {
            if (ViewModel?.IsReadOnly != false)
                return;

            ViewModel.AddPage();
            Editor.Focus();
        }

        private void OnTogglePagesClick(object? sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
                ViewModel.IsPagePanelOpen = !ViewModel.IsPagePanelOpen;
        }

        private void OnBlockTypeClick(object? sender, RoutedEventArgs e)
        {
            if (sender is not Control { Tag: string tag } ||
                !Enum.TryParse<NoteBlockType>(tag, out var type) ||
                ViewModel == null)
            {
                return;
            }

            ViewModel.SetSelectedBlockType(type);
            RefreshCurrentLine();
            Editor.Focus();
        }

        private void OnStrikeClick(object? sender, RoutedEventArgs e)
        {
            ViewModel?.ToggleSelectedStrikeThrough();
            Editor.TextArea.TextView.Redraw();
            Editor.Focus();
        }

        private void OnHighlightClick(object? sender, RoutedEventArgs e)
        {
            ViewModel?.ToggleSelectedHighlight();
            Editor.TextArea.TextView.Redraw();
            Editor.Focus();
        }

        private void OnEditorKeyDown(object? sender, KeyEventArgs e)
        {
            if (ViewModel?.SelectedPage == null || ViewModel.IsReadOnly)
                return;

            var document = Editor.Document;
            var line = document.GetLineByOffset(Editor.CaretOffset);
            var lineIndex = line.LineNumber - 1;
            if (lineIndex < 0 || lineIndex >= ViewModel.SelectedPage.Blocks.Count)
                return;

            var block = ViewModel.SelectedPage.Blocks[lineIndex];
            if (e.Key == Key.Enter && Editor.TextArea.Selection.IsEmpty)
            {
                InsertNewLine(line, block, lineIndex);
                e.Handled = true;
            }
            else if (e.Key == Key.Back && Editor.TextArea.Selection.IsEmpty)
            {
                var contentStart = line.Offset + GetPrefix(block).Length;
                if (Editor.CaretOffset <= contentStart &&
                    block.Type is NoteBlockType.Bullet or NoteBlockType.Checklist or NoteBlockType.Quote)
                {
                    block.Type = NoteBlockType.Paragraph;
                    ReplaceLine(line, block.Text, 0);
                    ViewModel.SelectBlock(block);
                    e.Handled = true;
                }
            }
        }

        private void OnEditorPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (ViewModel?.SelectedBlock is not { Type: NoteBlockType.Checklist } block ||
                Editor.TextArea.Caret.Column > 3 || ViewModel.IsReadOnly)
            {
                return;
            }

            // Символ чек-листа находится внутри единого текстового поля. Щелчок
            // по нему меняет состояние, не создавая отдельный строковый контрол.
            block.IsChecked = !block.IsChecked;
            RefreshCurrentLine();
        }

        private void InsertNewLine(DocumentLine line, NoteBlockViewModel block, int lineIndex)
        {
            var document = Editor.Document;
            var rawLine = document.GetText(line);
            var caretOffset = Editor.CaretOffset;

            if (block.Type == NoteBlockType.Paragraph && caretOffset == line.EndOffset &&
                TryParseShortcut(rawLine, out var shortcutType, out var shortcutText))
            {
                block.Type = shortcutType;
                block.Text = shortcutText;
                var displayText = GetDisplayText(block);

                _isUpdatingEditor = true;
                document.Replace(line.Offset, line.Length, displayText);
                _isUpdatingEditor = false;
                caretOffset = line.Offset + displayText.Length;
            }

            var nextType = block.Type is NoteBlockType.Bullet or NoteBlockType.Checklist
                ? block.Type
                : NoteBlockType.Paragraph;
            var nextBlock = new NoteBlockViewModel(new NoteBlock { Type = nextType });
            ViewModel!.SelectedPage!.Blocks.Insert(lineIndex + 1, nextBlock);

            var prefix = GetPrefix(nextBlock);
            var insertion = Environment.NewLine + prefix;
            _isUpdatingEditor = true;
            document.Insert(caretOffset, insertion);
            Editor.CaretOffset = caretOffset + insertion.Length;
            _isUpdatingEditor = false;

            SynchronizeBlocksFromDocument();
            ViewModel.SelectBlock(nextBlock);
            Editor.TextArea.TextView.Redraw();
        }

        private void OnDocumentTextChanged(object? sender, EventArgs e)
        {
            if (!_isUpdatingEditor)
                SynchronizeBlocksFromDocument();
        }

        private void OnCaretPositionChanged(object? sender, EventArgs e)
        {
            if (ViewModel?.SelectedPage == null)
                return;

            var index = Editor.TextArea.Caret.Line - 1;
            if (index >= 0 && index < ViewModel.SelectedPage.Blocks.Count)
                ViewModel.SelectBlock(ViewModel.SelectedPage.Blocks[index]);
        }

        private void LoadSelectedPage()
        {
            if (ViewModel?.SelectedPage == null)
                return;

            var blocks = ViewModel.SelectedPage.Blocks;
            if (blocks.Count == 0)
                blocks.Add(NotePageViewModel.CreateParagraph());

            _isUpdatingEditor = true;
            Editor.Document.Text = string.Join(Environment.NewLine, blocks.Select(GetDisplayText));
            Editor.CaretOffset = 0;
            _isUpdatingEditor = false;

            ViewModel.SelectBlock(blocks[0]);
            Editor.TextArea.TextView.Redraw();
        }

        private void SynchronizeBlocksFromDocument()
        {
            if (ViewModel?.SelectedPage == null)
                return;

            var page = ViewModel.SelectedPage;
            var lines = Editor.Document.Lines;

            // Вставка нескольких строк через буфер также должна создать
            // соответствующие элементы модели, иначе часть текста не сохранится.
            while (page.Blocks.Count < lines.Count)
            {
                var insertionIndex = Math.Clamp(Editor.TextArea.Caret.Line - 1, 0, page.Blocks.Count);
                page.Blocks.Insert(insertionIndex, NotePageViewModel.CreateParagraph());
            }

            while (page.Blocks.Count > lines.Count && page.Blocks.Count > 1)
            {
                var removalIndex = Math.Clamp(Editor.TextArea.Caret.Line, 0, page.Blocks.Count - 1);
                page.Blocks.RemoveAt(removalIndex);
            }

            for (var index = 0; index < lines.Count && index < page.Blocks.Count; index++)
            {
                var block = page.Blocks[index];
                var displayText = Editor.Document.GetText(lines[index]);
                block.Text = StripPrefix(displayText, block);
            }

            page.UpdatedAtUtc = DateTime.UtcNow;
            OnCaretPositionChanged(this, EventArgs.Empty);
            Editor.TextArea.TextView.Redraw();
        }

        private void RefreshCurrentLine()
        {
            if (ViewModel?.SelectedPage == null || ViewModel.SelectedBlock == null)
                return;

            var index = ViewModel.SelectedPage.Blocks.IndexOf(ViewModel.SelectedBlock);
            if (index < 0 || index >= Editor.Document.LineCount)
                return;

            var line = Editor.Document.GetLineByNumber(index + 1);
            ReplaceLine(line, GetDisplayText(ViewModel.SelectedBlock), GetPrefix(ViewModel.SelectedBlock).Length);
            Editor.TextArea.TextView.Redraw();
        }

        private void ReplaceLine(DocumentLine line, string text, int caretColumn)
        {
            _isUpdatingEditor = true;
            Editor.Document.Replace(line.Offset, line.Length, text);
            Editor.CaretOffset = line.Offset + Math.Clamp(caretColumn, 0, text.Length);
            _isUpdatingEditor = false;
        }

        private NoteBlockViewModel? GetBlockForLine(int lineNumber)
        {
            var blocks = ViewModel?.SelectedPage?.Blocks;
            var index = lineNumber - 1;
            return blocks != null && index >= 0 && index < blocks.Count ? blocks[index] : null;
        }

        private IBrush? FindBrush(string resourceKey)
        {
            return this.TryFindResource(resourceKey, out var value) ? value as IBrush : null;
        }

        private static string GetDisplayText(NoteBlockViewModel block)
        {
            if (block.Type == NoteBlockType.Divider)
                return "────────────────────────";
            return GetPrefix(block) + block.Text;
        }

        private static string GetPrefix(NoteBlockViewModel block) => block.Type switch
        {
            NoteBlockType.Bullet => "• ",
            NoteBlockType.Checklist => block.IsChecked ? "☑ " : "☐ ",
            NoteBlockType.Quote => "│ ",
            _ => string.Empty
        };

        private static string StripPrefix(string displayText, NoteBlockViewModel block)
        {
            var prefix = GetPrefix(block);
            return prefix.Length > 0 && displayText.StartsWith(prefix, StringComparison.Ordinal)
                ? displayText[prefix.Length..]
                : block.Type == NoteBlockType.Divider ? string.Empty : displayText;
        }

        private static bool TryParseShortcut(string text, out NoteBlockType type, out string content)
        {
            var trimmed = text.TrimStart();
            if (trimmed == "---")
            {
                type = NoteBlockType.Divider;
                content = string.Empty;
                return true;
            }
            if (trimmed.StartsWith("- [ ]", StringComparison.Ordinal))
            {
                type = NoteBlockType.Checklist;
                content = trimmed[5..].TrimStart();
                return true;
            }
            if (trimmed.StartsWith("###", StringComparison.Ordinal))
            {
                type = NoteBlockType.Heading3;
                content = trimmed[3..].TrimStart();
                return true;
            }
            if (trimmed.StartsWith("##", StringComparison.Ordinal))
            {
                type = NoteBlockType.Heading2;
                content = trimmed[2..].TrimStart();
                return true;
            }
            if (trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                type = NoteBlockType.Heading1;
                content = trimmed[1..].TrimStart();
                return true;
            }
            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                type = NoteBlockType.Bullet;
                content = trimmed[2..];
                return true;
            }
            if (trimmed.StartsWith(">", StringComparison.Ordinal))
            {
                type = NoteBlockType.Quote;
                content = trimmed[1..].TrimStart();
                return true;
            }

            type = NoteBlockType.Paragraph;
            content = text;
            return false;
        }

        private sealed class NotesLineTransformer : DocumentColorizingTransformer
        {
            private readonly NotesView _owner;

            public NotesLineTransformer(NotesView owner)
            {
                _owner = owner;
            }

            protected override void ColorizeLine(DocumentLine line)
            {
                var block = _owner.GetBlockForLine(line.LineNumber);
                if (block == null || line.Length == 0)
                    return;

                ChangeLinePart(line.Offset, line.EndOffset, element =>
                {
                    if (block.IsHeading)
                    {
                        element.TextRunProperties.SetFontRenderingEmSize(block.EditorFontSize);
                        element.TextRunProperties.SetTypeface(new Typeface(
                            _owner.Editor.FontFamily,
                            FontStyle.Normal,
                            FontWeight.SemiBold));
                    }

                    if (block.IsHighlighted && _owner.FindBrush("AccentSubtleBrush") is { } highlight)
                        element.TextRunProperties.SetBackgroundBrush(highlight);

                    if ((block.IsStruckThrough || block.IsChecked))
                        element.TextRunProperties.SetTextDecorations(TextDecorations.Strikethrough);

                    if (block.Type is NoteBlockType.Quote or NoteBlockType.Divider &&
                        _owner.FindBrush("TextMutedBrush") is { } muted)
                    {
                        element.TextRunProperties.SetForegroundBrush(muted);
                    }
                });
            }
        }
    }
}
