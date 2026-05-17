using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System;
using Writersword.Modules.TextEditor.ViewModels.Toolbar;
using Writersword.Styles.UserControls;

namespace Writersword.Modules.TextEditor.Views.Toolbar.Tabs
{
    public partial class RibbonHomeTab : UserControl
    {
        private RibbonScrollContainer? _scrollContainer;
        private ListBox? _fontSizeList;
        private AutoCompleteBox? _fontAutoComplete;
        private TextBox? _fontInnerTextBox;
        private ListBox? _fontInnerList;
        private bool _fontJustGotFocus;
        private bool _fontScrolling;
        private string? _fontBeforeOpen;

        public RibbonHomeTab()
        {
            InitializeComponent();
            SizeChanged += OnSizeChanged;
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            AttachControls();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            DetachControls();
        }

        private void AttachControls()
        {
            _scrollContainer = this.FindControl<RibbonScrollContainer>("ScrollContainer");

            _fontSizeList = this.FindControl<ListBox>("FontSizeListBox");
            if (_fontSizeList is not null)
            {
                _fontSizeList.SelectionChanged -= OnFontSizeListSelectionChanged;
                _fontSizeList.SelectionChanged += OnFontSizeListSelectionChanged;
            }

            _fontAutoComplete = this.FindControl<AutoCompleteBox>("FontAutoComplete");
            if (_fontAutoComplete is not null)
            {
                _fontAutoComplete.TemplateApplied -= OnFontAutoCompleteTemplateApplied;
                _fontAutoComplete.TemplateApplied += OnFontAutoCompleteTemplateApplied;
                _fontAutoComplete.DropDownOpened -= OnFontDropDownOpened;
                _fontAutoComplete.DropDownOpened += OnFontDropDownOpened;
                _fontAutoComplete.DropDownClosed -= OnFontDropDownClosed;
                _fontAutoComplete.DropDownClosed += OnFontDropDownClosed;
            }
        }

        private void DetachControls()
        {
            if (_fontSizeList is not null)
                _fontSizeList.SelectionChanged -= OnFontSizeListSelectionChanged;

            if (_fontAutoComplete is not null)
            {
                _fontAutoComplete.TemplateApplied -= OnFontAutoCompleteTemplateApplied;
                _fontAutoComplete.DropDownOpened -= OnFontDropDownOpened;
                _fontAutoComplete.DropDownClosed -= OnFontDropDownClosed;
            }

            DetachInnerControls();
        }

        private void DetachInnerControls()
        {
            if (_fontInnerTextBox is not null)
            {
                _fontInnerTextBox.PointerReleased -= OnFontInnerPointerReleased;
                _fontInnerTextBox.GotFocus -= OnFontInnerGotFocus;
                _fontInnerTextBox.LostFocus -= OnFontInnerLostFocus;
                _fontInnerTextBox.KeyDown -= OnFontInnerKeyDown;
            }
        }

        private void OnFontAutoCompleteTemplateApplied(object? sender, TemplateAppliedEventArgs e)
        {
            DetachInnerControls();
            _fontInnerTextBox = e.NameScope.Find<TextBox>("PART_TextBox");
            _fontInnerList = e.NameScope.Find<ListBox>("PART_SelectingItemsControl");

            if (_fontInnerTextBox is not null)
            {
                _fontInnerTextBox.MinHeight = 0;
                _fontInnerTextBox.Height = 22;
                _fontInnerTextBox.VerticalContentAlignment = VerticalAlignment.Center;
                _fontInnerTextBox.Padding = new Thickness(6, 0);

                _fontInnerTextBox.PointerReleased += OnFontInnerPointerReleased;
                _fontInnerTextBox.GotFocus += OnFontInnerGotFocus;
                _fontInnerTextBox.LostFocus += OnFontInnerLostFocus;
                _fontInnerTextBox.KeyDown += OnFontInnerKeyDown;
            }
        }

        // ── TextBox события ───────────────────────────────────────────────

        private void OnFontInnerGotFocus(object? sender, FocusChangedEventArgs e)
        {
            _fontJustGotFocus = true;
        }

        private void OnFontInnerLostFocus(object? sender, FocusChangedEventArgs e)
        {
            _fontJustGotFocus = false;
        }

        private void OnFontInnerPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            // Первый клик — выделяем весь текст.
            // Повторные клики — курсор встаёт в место клика, не трогаем дропдаун.
            if (!_fontJustGotFocus) return;
            _fontJustGotFocus = false;
            Dispatcher.UIThread.Post(
                () => _fontInnerTextBox?.SelectAll(),
                DispatcherPriority.Background);
        }

        private void OnFontInnerKeyDown(object? sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Up:
                case Key.Down:
                case Key.Enter:
                case Key.Escape:
                case Key.Tab:
                case Key.Left:
                case Key.Right:
                case Key.Home:
                case Key.End:
                    return;
                default:
                    // При вводе AutoCompleteBox сам открывает дропдаун через MinimumPrefixLength=0
                    break;
            }
        }

        // ── Дропдаун ─────────────────────────────────────────────────────

        private void OnFontDropDownOpened(object? sender, EventArgs e)
        {
            if (DataContext is not RibbonHomeTabViewModel vm) return;
            _fontBeforeOpen = vm.CurrentFontFamily;

            if (_fontScrolling) return;
            _fontScrolling = true;

            string? current = _fontBeforeOpen;
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (_fontInnerList is null || current is null) return;
                    _fontInnerList.SelectedItem = current;
                    _fontInnerList.ScrollIntoView(current);
                }
                finally { _fontScrolling = false; }
            }, DispatcherPriority.Background);
        }

        private void OnFontDropDownClosed(object? sender, EventArgs e)
        {
            if (DataContext is not RibbonHomeTabViewModel vm) return;
            string restore = vm.CurrentFontFamily ?? _fontBeforeOpen ?? string.Empty;
            if (_fontInnerTextBox is not null && _fontInnerTextBox.Text != restore)
                _fontInnerTextBox.Text = restore;
        }

        // ── Ribbon resize ─────────────────────────────────────────────────

        private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (DataContext is RibbonHomeTabViewModel vm)
            {
                vm.UpdateLayout(e.NewSize.Width);
                if (_scrollContainer is not null)
                    _scrollContainer.ArrowsVisible = !vm.IsClipboardGroupExpanded;
            }
            _scrollContainer?.NotifySizeChanged();
        }

        // ── FontSize list ─────────────────────────────────────────────────

        private void OnFontSizeListSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (sender is not ListBox lb) return;
            if (lb.SelectedItem is not string sizeStr) return;
            if (DataContext is RibbonHomeTabViewModel vm)
                vm.SelectFontSizeCommand.Execute(sizeStr);
            lb.SelectedItem = null;
        }
    }
}