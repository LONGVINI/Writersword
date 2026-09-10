using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Writersword.Modules.Notes.Models;
using Writersword.Modules.Notes.ViewModels;

namespace Writersword.Modules.Notes.Views
{
    public partial class NotesView : UserControl
    {
        private const double CompactWidth = 560;
        private const string DividerText = "────────────────────────";
        private readonly Dictionary<NotePageViewModel, PageEditorState> _pageEditors = new();
        private NotesViewModel? _subscribedViewModel;
        private NotePageViewModel? _activePage;
        private PendingChange? _pendingChange;
        private Guid? _pressedChecklist;
        private bool _isUpdatingEditor;

        public NotesView()
        {
            InitializeComponent();
            Editor.TextArea.TextView.LineTransformers.Add(new NotesLineTransformer(this));
            Editor.TextArea.Caret.PositionChanged += OnCaretPositionChanged;

            // Обработка выполняется до стандартных команд TextArea, иначе Enter
            // успевает изменить документ до преобразования блочной разметки.
            Editor.AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
            Editor.AddHandler(PointerPressedEvent, OnEditorPointerPressed, RoutingStrategies.Tunnel);
            Editor.AddHandler(PointerReleasedEvent, OnEditorPointerReleased, RoutingStrategies.Tunnel);
            Editor.PointerCaptureLost += (_, _) => _pressedChecklist = null;
            DataContextChanged += OnDataContextChanged;
            SizeChanged += OnSizeChanged;
            AttachedToVisualTree += (_, _) => SubscribeViewModel();
            DetachedFromVisualTree += (_, _) => UnsubscribeViewModel();
        }

        private NotesViewModel? ViewModel => DataContext as NotesViewModel;

        private void SubscribeViewModel()
        {
            if (_subscribedViewModel == ViewModel)
                return;
            UnsubscribeViewModel();
            _subscribedViewModel = ViewModel;
            if (_subscribedViewModel != null)
                _subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
            LoadSelectedPage();
        }

        private void UnsubscribeViewModel()
        {
            if (_subscribedViewModel != null)
                _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribedViewModel = null;
        }

        private void OnDataContextChanged(object? sender, EventArgs e)
        {
            SaveEditorPosition();
            UnsubscribeViewModel();
            _pageEditors.Clear();
            _activePage = null;
            SubscribeViewModel();
            if (ViewModel == null)
                LoadSelectedPage();
            else if (Bounds.Width > 0)
                ViewModel.IsCompact = Bounds.Width < CompactWidth;
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(NotesViewModel.SelectedPage))
                LoadSelectedPage();
        }

        private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            // Плавающая панель использует собственную ширину, а не ширину окна.
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
                !Enum.TryParse<NoteBlockType>(tag, out var type) || !Enum.IsDefined(type))
                return;

            EditSelectedBlock(blocks =>
            {
                var block = blocks[0];
                // Разделитель добавляется после непустого текста, чтобы команда
                // не скрывала содержимое существующего абзаца.
                if (type == NoteBlockType.Divider && block.Type != type && block.Text.Length > 0)
                {
                    blocks.Add(new NoteBlock { Type = NoteBlockType.Divider });
                    return GetDisplayText(block).Length + Environment.NewLine.Length + DividerText.Length;
                }
                block.Type = type;
                if (type != NoteBlockType.Checklist)
                    block.IsChecked = false;
                return null;
            });
        }

        private void OnStrikeClick(object? sender, RoutedEventArgs e) => EditSelectedBlock(blocks =>
        {
            blocks[0].IsStruckThrough = !blocks[0].IsStruckThrough;
            return null;
        });

        private void OnHighlightClick(object? sender, RoutedEventArgs e) => EditSelectedBlock(blocks =>
        {
            blocks[0].IsHighlighted = !blocks[0].IsHighlighted;
            return null;
        });

        private void EditSelectedBlock(Func<List<NoteBlock>, int?> edit)
        {
            if (ViewModel?.SelectedPage is not { } page || ViewModel.IsReadOnly || ViewModel.SelectedBlock == null)
                return;
            var index = page.Blocks.IndexOf(ViewModel.SelectedBlock);
            if (index >= 0)
                EditBlock(index, edit);
            Editor.Focus();
        }

        private void OnEditorKeyDown(object? sender, KeyEventArgs e)
        {
            if (_activePage == null || ViewModel?.IsReadOnly != false ||
                !Editor.TextArea.Selection.IsEmpty || e.KeyModifiers != KeyModifiers.None)
                return;

            var line = Editor.Document.GetLineByOffset(Editor.CaretOffset);
            var index = line.LineNumber - 1;
            var block = _activePage.Blocks[index];
            if (e.Key == Key.Enter)
            {
                var contentOffset = Math.Clamp(Editor.CaretOffset - line.Offset - GetPrefix(block).Length, 0, block.Text.Length);
                var isAtEnd = Editor.CaretOffset == line.EndOffset;
                EditBlock(index, blocks =>
                {
                    var current = blocks[0];
                    if (current.Text.Length == 0 && current.Type is NoteBlockType.Bullet or NoteBlockType.Checklist)
                    {
                        current.Type = NoteBlockType.Paragraph;
                        current.IsChecked = false;
                        return 0;
                    }

                    if (current.Type == NoteBlockType.Paragraph && isAtEnd &&
                        TryParseShortcut(current.Text, out var type, out var text))
                    {
                        current.Type = type;
                        current.Text = text;
                        contentOffset = text.Length;
                    }
                    var next = new NoteBlock
                    {
                        Type = current.Type is NoteBlockType.Bullet or NoteBlockType.Checklist
                            ? current.Type : NoteBlockType.Paragraph,
                        Text = current.Text[contentOffset..]
                    };
                    current.Text = current.Text[..contentOffset];
                    blocks.Add(next);
                    return GetDisplayText(current).Length + Environment.NewLine.Length + GetPrefix(next).Length;
                });
                e.Handled = true;
            }
            else if (e.Key == Key.Back && Editor.CaretOffset <= line.Offset + GetPrefix(block).Length &&
                     block.Type is NoteBlockType.Bullet or NoteBlockType.Checklist or NoteBlockType.Quote)
            {
                EditBlock(index, blocks =>
                {
                    blocks[0].Type = NoteBlockType.Paragraph;
                    blocks[0].IsChecked = false;
                    return 0;
                });
                e.Handled = true;
            }
        }

        private void OnEditorPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            _pressedChecklist = null;
            if (ViewModel?.IsReadOnly != false || e.KeyModifiers != KeyModifiers.None || e.ClickCount != 1 ||
                !e.GetCurrentPoint(Editor).Properties.IsLeftButtonPressed)
                return;
            var block = HitChecklist(e.GetPosition(Editor.TextArea.TextView));
            if (block == null)
                return;
            _pressedChecklist = block.Id;
            e.Pointer.Capture(Editor);
            Editor.Focus();
            e.Handled = true;
        }

        private void OnEditorPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            var pressedId = _pressedChecklist;
            if (pressedId == null)
                return;
            _pressedChecklist = null;
            e.Pointer.Capture(null);
            e.Handled = true;
            if (ViewModel?.IsReadOnly != false || e.InitialPressMouseButton != MouseButton.Left ||
                e.KeyModifiers != KeyModifiers.None || _activePage == null)
                return;
            var block = HitChecklist(e.GetPosition(Editor.TextArea.TextView));
            if (block?.Id != pressedId)
                return;
            EditBlock(_activePage.Blocks.IndexOf(block), blocks =>
            {
                blocks[0].IsChecked = !blocks[0].IsChecked;
                return null;
            });
        }

        private NoteBlockViewModel? HitChecklist(Point position)
        {
            var textView = Editor.TextArea.TextView;
            if (!new Rect(textView.Bounds.Size).Contains(position))
                return null;
            textView.EnsureVisualLines();
            var documentPosition = position + textView.ScrollOffset;
            var hit = textView.GetPositionFloor(documentPosition);
            if (hit is not { Column: 1 } location || GetBlockForLine(location.Line) is not { IsChecklist: true } block)
                return null;
            // Проверяется прямоугольник первого символа, включая смещение прокрутки.
            // Каретка и перенос продолжения строки не определяют попадание в чекбокс.
            var start = textView.GetVisualPosition(new AvaloniaEdit.TextViewPosition(location.Line, 1), VisualYPosition.LineTop);
            var end = textView.GetVisualPosition(new AvaloniaEdit.TextViewPosition(location.Line, 2), VisualYPosition.LineBottom);
            return new Rect(start, end).Contains(documentPosition) ? block : null;
        }

        private void EditBlock(int index, Func<List<NoteBlock>, int?> edit)
        {
            if (_activePage == null || ViewModel?.IsReadOnly != false || index < 0)
                return;
            var page = _activePage;
            var before = new[] { page.Blocks[index].ToModel() };
            var after = new List<NoteBlock> { page.Blocks[index].ToModel() };
            var requestedCaret = edit(after);
            var line = Editor.Document.GetLineByNumber(index + 1);
            var offset = line.Offset;
            var oldLength = line.Length;
            var text = string.Join(Environment.NewLine, after.Select(GetDisplayText));
            var caret = Editor.CaretOffset;
            var selectionStart = Editor.SelectionStart;
            var selectionEnd = selectionStart + Editor.SelectionLength;
            var oldPrefix = GetPrefix(before[0]).Length;
            var newPrefix = GetPrefix(after[0]).Length;

            int MapPosition(int position)
            {
                if (position < offset)
                    return position;
                if (position > offset + oldLength)
                    return position + text.Length - oldLength;
                return offset + newPrefix + Math.Clamp(position - offset - oldPrefix, 0, after[0].Text.Length);
            }

            _isUpdatingEditor = true;
            try
            {
                using (Editor.Document.RunUpdate())
                {
                    ReplaceBlocks(page, index, 1, after);
                    if (Editor.Document.GetText(offset, oldLength) != text)
                        Editor.Document.Replace(offset, oldLength, text);
                    // Состояние модели находится в той же группе Undo, что и текст.
                    // Для выделения/зачёркивания группа содержит только эту операцию.
                    Editor.Document.UndoStack.Push(new BlockChangeOperation(this, page, index, before, after.ToArray()));
                    if (requestedCaret is { } target)
                    {
                        Editor.Select(offset + target, 0);
                        Editor.CaretOffset = offset + target;
                    }
                    else
                    {
                        var start = MapPosition(selectionStart);
                        var end = MapPosition(selectionEnd);
                        Editor.Select(start, end - start);
                        Editor.CaretOffset = MapPosition(caret);
                    }
                }
            }
            finally
            {
                _isUpdatingEditor = false;
            }
            RefreshPresentation();
        }

        private void OnDocumentChanging(object? sender, DocumentChangeEventArgs e)
        {
            _pendingChange = null;
            if (_isUpdatingEditor || _activePage == null || !Editor.Document.UndoStack.AcceptChanges)
                return;
            var first = Editor.Document.GetLineByOffset(e.Offset);
            var last = Editor.Document.GetLineByOffset(e.Offset + e.RemovalLength);
            var index = first.LineNumber - 1;
            var count = last.LineNumber - first.LineNumber + 1;
            _pendingChange = new PendingChange(index, Editor.Document.LineCount,
                _activePage.Blocks.Skip(index).Take(count).Select(block => block.ToModel()).ToArray(),
                e.Offset > first.Offset,
                e.Offset + e.RemovalLength < last.EndOffset || e.Offset + e.RemovalLength == last.Offset);
        }

        private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
        {
            var change = _pendingChange;
            _pendingChange = null;
            if (_isUpdatingEditor || _activePage == null || change == null || !Editor.Document.UndoStack.AcceptChanges)
                return;

            // Границы берутся из DocumentChangeEventArgs до изменения документа.
            // Строки вне изменённого диапазона сохраняют свои блоки и оформление.
            var count = change.Before.Length + Editor.Document.LineCount - change.LineCount;
            var after = new List<NoteBlock>(count);
            var moveFirstToEnd = !change.HasPrefix && change.HasSuffix && count > 1 && change.Before.Length == 1;
            for (var i = 0; i < count; i++)
            {
                NoteBlock block;
                if ((i == count - 1 && change.HasSuffix && (change.Before.Length > 1 || moveFirstToEnd)) &&
                    (count > 1 || !change.HasPrefix))
                    block = new NoteBlockViewModel(change.Before[^1]).ToModel();
                else if (i == 0 && !moveFirstToEnd)
                    block = new NoteBlockViewModel(change.Before[0]).ToModel();
                else
                    block = new NoteBlock();
                var line = Editor.Document.GetLineByNumber(change.Index + i + 1);
                ReadDisplayText(block, Editor.Document.GetText(line));
                after.Add(block);
            }
            ReplaceBlocks(_activePage, change.Index, change.Before.Length, after);
            Editor.Document.UndoStack.Push(new BlockChangeOperation(this, _activePage, change.Index, change.Before, after.ToArray()));
        }

        private static void ReadDisplayText(NoteBlock block, string text)
        {
            if (block.Type == NoteBlockType.Divider)
            {
                if (text == GetDisplayText(block))
                    return;
                // Изменённая строка разделителя становится текстом. Ни один
                // введённый символ не отбрасывается при сохранении или смене страницы.
                block.Type = NoteBlockType.Paragraph;
                block.IsChecked = false;
            }
            var prefix = GetPrefix(block);
            if (prefix.Length > 0 && text.StartsWith(prefix, StringComparison.Ordinal))
                block.Text = text[prefix.Length..];
            else
            {
                if (prefix.Length > 0)
                {
                    block.Type = NoteBlockType.Paragraph;
                    block.IsChecked = false;
                }
                block.Text = text;
            }
        }

        private static void ReplaceBlocks(NotePageViewModel page, int index, int count, IEnumerable<NoteBlock> blocks)
        {
            var replacements = blocks.Select(block => new NoteBlockViewModel(block)).ToArray();
            for (var i = 0; i < count; i++)
                page.Blocks.RemoveAt(index);
            for (var i = 0; i < replacements.Length; i++)
                page.Blocks.Insert(index + i, replacements[i]);
            page.UpdatedAtUtc = DateTime.UtcNow;
        }

        private void OnDocumentTextChanged(object? sender, EventArgs e)
        {
            if (!_isUpdatingEditor)
                RefreshPresentation();
        }

        private void OnCaretPositionChanged(object? sender, EventArgs e)
        {
            if (!_isUpdatingEditor && _activePage != null && ViewModel?.SelectedPage == _activePage)
                ViewModel.SelectBlock(GetBlockForLine(Editor.TextArea.Caret.Line));
        }

        private void RefreshPresentation()
        {
            OnCaretPositionChanged(this, EventArgs.Empty);
            Editor.TextArea.TextView.Redraw();
        }

        private void SaveEditorPosition()
        {
            if (_activePage == null || !_pageEditors.TryGetValue(_activePage, out var state))
                return;
            state.CaretOffset = Editor.CaretOffset;
            state.SelectionStart = Editor.SelectionStart;
            state.SelectionLength = Editor.SelectionLength;
        }

        private void LoadSelectedPage()
        {
            SaveEditorPosition();
            Editor.Document.Changing -= OnDocumentChanging;
            Editor.Document.Changed -= OnDocumentChanged;
            Editor.Document.TextChanged -= OnDocumentTextChanged;
            _pendingChange = null;
            _pressedChecklist = null;
            _activePage = ViewModel?.SelectedPage;
            foreach (var oldPage in _pageEditors.Keys.Where(page => ViewModel?.Pages.Contains(page) != true).ToArray())
                _pageEditors.Remove(oldPage);

            _isUpdatingEditor = true;
            try
            {
                if (_activePage == null)
                    Editor.Document = new TextDocument();
                else
                {
                    // У каждой страницы свой TextDocument и UndoStack. Переход между
                    // страницами не является редактированием и не смешивает историю.
                    if (!_pageEditors.TryGetValue(_activePage, out var state))
                    {
                        state = new PageEditorState(new TextDocument(string.Join(Environment.NewLine,
                            _activePage.Blocks.Select(block => GetDisplayText(block.ToModel())))));
                        _pageEditors.Add(_activePage, state);
                    }
                    Editor.Document = state.Document;
                    Editor.Select(state.SelectionStart, state.SelectionLength);
                    Editor.CaretOffset = state.CaretOffset;
                }
                Editor.Document.Changing += OnDocumentChanging;
                Editor.Document.Changed += OnDocumentChanged;
                Editor.Document.TextChanged += OnDocumentTextChanged;
            }
            finally
            {
                _isUpdatingEditor = false;
            }
            RefreshPresentation();
        }

        private NoteBlockViewModel? GetBlockForLine(int lineNumber)
        {
            var blocks = _activePage?.Blocks;
            var index = lineNumber - 1;
            return blocks != null && index >= 0 && index < blocks.Count ? blocks[index] : null;
        }

        private IBrush? FindBrush(string resourceKey) =>
            this.TryFindResource(resourceKey, out var value) ? value as IBrush : null;

        private static string GetDisplayText(NoteBlock block) =>
            block.Type == NoteBlockType.Divider && block.Text.Length == 0 ? DividerText : GetPrefix(block) + block.Text;

        private static string GetPrefix(NoteBlockViewModel block) => GetPrefix(block.Type, block.IsChecked);
        private static string GetPrefix(NoteBlock block) => GetPrefix(block.Type, block.IsChecked);
        private static string GetPrefix(NoteBlockType type, bool isChecked) => type switch
        {
            NoteBlockType.Bullet => "• ",
            NoteBlockType.Checklist => isChecked ? "☑ " : "☐ ",
            NoteBlockType.Quote => "│ ",
            _ => string.Empty
        };

        private static bool TryParseShortcut(string text, out NoteBlockType type, out string content)
        {
            var trimmed = text.TrimStart();
            var result = trimmed switch
            {
                "---" => (NoteBlockType.Divider, string.Empty),
                var value when value.StartsWith("- [ ] ", StringComparison.Ordinal) => (NoteBlockType.Checklist, value[6..]),
                var value when value.StartsWith("### ", StringComparison.Ordinal) => (NoteBlockType.Heading3, value[4..]),
                var value when value.StartsWith("## ", StringComparison.Ordinal) => (NoteBlockType.Heading2, value[3..]),
                var value when value.StartsWith("# ", StringComparison.Ordinal) => (NoteBlockType.Heading1, value[2..]),
                var value when value.StartsWith("- ", StringComparison.Ordinal) => (NoteBlockType.Bullet, value[2..]),
                var value when value.StartsWith("> ", StringComparison.Ordinal) => (NoteBlockType.Quote, value[2..]),
                _ => (NoteBlockType.Paragraph, text)
            };
            type = result.Item1;
            content = result.Item2;
            return type != NoteBlockType.Paragraph;
        }

        private sealed record PendingChange(int Index, int LineCount, NoteBlock[] Before, bool HasPrefix, bool HasSuffix);

        private sealed class PageEditorState(TextDocument document)
        {
            public TextDocument Document { get; } = document;
            public int CaretOffset { get; set; }
            public int SelectionStart { get; set; }
            public int SelectionLength { get; set; }
        }

        private sealed class BlockChangeOperation(NotesView owner, NotePageViewModel page, int index,
            NoteBlock[] before, NoteBlock[] after) : IUndoableOperation
        {
            public void Undo() => Restore(after.Length, before);
            public void Redo() => Restore(before.Length, after);

            private void Restore(int count, NoteBlock[] blocks)
            {
                ReplaceBlocks(page, index, count, blocks);
                if (owner._activePage == page)
                    owner.RefreshPresentation();
            }
        }

        private sealed class NotesLineTransformer(NotesView owner) : DocumentColorizingTransformer
        {
            protected override void ColorizeLine(DocumentLine line)
            {
                var block = owner.GetBlockForLine(line.LineNumber);
                if (block == null || line.Length == 0)
                    return;
                ChangeLinePart(line.Offset, line.EndOffset, element =>
                {
                    if (block.IsHeading)
                    {
                        element.TextRunProperties.SetFontRenderingEmSize(block.EditorFontSize);
                        element.TextRunProperties.SetTypeface(new Typeface(owner.Editor.FontFamily, FontStyle.Normal, FontWeight.SemiBold));
                    }
                    if (block.IsHighlighted && owner.FindBrush("AccentSubtleBrush") is { } highlight)
                        element.TextRunProperties.SetBackgroundBrush(highlight);
                    if (block.IsVisuallyStruck)
                        element.TextRunProperties.SetTextDecorations(TextDecorations.Strikethrough);
                    if (block.Type is NoteBlockType.Quote or NoteBlockType.Divider && owner.FindBrush("TextMutedBrush") is { } muted)
                        element.TextRunProperties.SetForegroundBrush(muted);
                });
            }
        }
    }
}