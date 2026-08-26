using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System;
using System.Linq;
using Writersword.Modules.Notes.Models;
using Writersword.Modules.Notes.ViewModels;

namespace Writersword.Modules.Notes.Views
{
    public partial class NotesView : UserControl
    {
        private const double CompactWidth = 560;

        public NotesView()
        {
            InitializeComponent();
            SizeChanged += OnSizeChanged;
        }

        private NotesViewModel? ViewModel => DataContext as NotesViewModel;

        private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            // Режим определяется шириной самого Dock-содержимого, а не окна:
            // плавающая или пристыкованная вкладка меняет представление независимо.
            if (ViewModel != null)
                ViewModel.IsCompact = e.NewSize.Width < CompactWidth;
        }

        private void OnAddPageClick(object? sender, RoutedEventArgs e)
        {
            if (ViewModel?.IsReadOnly != false)
                return;
            var page = ViewModel.AddPage();
            FocusBlock(page.Blocks[0]);
        }

        private void OnTogglePagesClick(object? sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
                ViewModel.IsPagePanelOpen = !ViewModel.IsPagePanelOpen;
        }

        private void OnBlockTypeClick(object? sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string tag } &&
                Enum.TryParse<NoteBlockType>(tag, out var type))
            {
                ViewModel?.SetSelectedBlockType(type);
            }
        }

        private void OnStrikeClick(object? sender, RoutedEventArgs e) => ViewModel?.ToggleSelectedStrikeThrough();
        private void OnHighlightClick(object? sender, RoutedEventArgs e) => ViewModel?.ToggleSelectedHighlight();

        private void OnBlockPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is Control { DataContext: NoteBlockViewModel block })
                ViewModel?.SelectBlock(block);
        }

        private void OnEditorGotFocus(object? sender, RoutedEventArgs e)
        {
            if (sender is Control { DataContext: NoteBlockViewModel block })
                ViewModel?.SelectBlock(block);
        }

        private void OnEditorKeyDown(object? sender, KeyEventArgs e)
        {
            if (sender is not TextBox { DataContext: NoteBlockViewModel block } editor || ViewModel == null)
                return;

            block.Text = editor.Text ?? string.Empty;
            if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
            {
                e.Handled = true;
                FocusBlock(ViewModel.CommitLine(block));
            }
            else if (e.Key == Key.Back && block.Text.Length == 0)
            {
                var target = ViewModel.RemoveEmptyBlock(block);
                if (target != null)
                {
                    e.Handled = true;
                    FocusBlock(target, moveCaretToEnd: true);
                }
            }
        }

        private void FocusBlock(NoteBlockViewModel block, bool moveCaretToEnd = false)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var editor = this.GetVisualDescendants()
                    .OfType<TextBox>()
                    .FirstOrDefault(control => control.Tag is Guid id && id == block.Id);
                if (editor == null)
                    return;
                editor.Focus();
                editor.CaretIndex = moveCaretToEnd ? editor.Text?.Length ?? 0 : 0;
            }, DispatcherPriority.Loaded);
        }
    }
}
