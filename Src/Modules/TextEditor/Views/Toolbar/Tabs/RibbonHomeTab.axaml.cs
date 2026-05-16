using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
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
        private bool _fontUserIsTyping;

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
                _fontAutoComplete.ItemFilter = FontItemFilter;
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
                _fontInnerTextBox.PointerReleased += OnFontInnerPointerReleased;
                _fontInnerTextBox.GotFocus += OnFontInnerGotFocus;
                _fontInnerTextBox.LostFocus += OnFontInnerLostFocus;
                _fontInnerTextBox.KeyDown += OnFontInnerKeyDown;
            }
        }

        // ── Фильтр ────────────────────────────────────────────────────────

        private bool FontItemFilter(string? search, object? item)
        {
            // Пока пользователь не начал печатать — показываем весь список.
            if (!_fontUserIsTyping) return true;
            if (item is not string s) return false;
            return s.Contains(search ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        // ── Внутренний TextBox события ────────────────────────────────────

        private void OnFontInnerGotFocus(object? sender, FocusChangedEventArgs e)
        {
            _fontJustGotFocus = true;
        }

        private void OnFontInnerLostFocus(object? sender, FocusChangedEventArgs e)
        {
            _fontJustGotFocus = false;
            _fontUserIsTyping = false;
        }

        private void OnFontInnerPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            // SelectAll только при первом получении фокуса.
            if (_fontJustGotFocus)
            {
                _fontJustGotFocus = false;
                _fontUserIsTyping = false;
                _fontInnerTextBox?.SelectAll();
            }

            // Открываем дропдаун при каждом клике — и при первом, и при повторных.
            if (_fontAutoComplete?.IsDropDownOpen != true)
                _fontAutoComplete!.IsDropDownOpen = true;
        }

        /// <summary>
        /// Пользователь нажал клавишу — включаем фильтрацию.
        /// KeyDown надёжнее TextChanged: не срабатывает на программные изменения
        /// текста внутри AutoCompleteBox (выбор элемента, сброс и т.д.).
        /// Навигационные клавиши (стрелки, Enter, Escape) фильтр не включают.
        /// </summary>
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
                    _fontUserIsTyping = true;
                    break;
            }
        }

        // ── Дропдаун открылся/закрылся ────────────────────────────────────

        private void OnFontDropDownOpened(object? sender, EventArgs e)
        {
            // Сбрасываем флаг — при открытии всегда показываем полный список.
            _fontUserIsTyping = false;

            if (DataContext is not RibbonHomeTabViewModel vm) return;
            string? current = vm.CurrentFontFamily;
            if (current is null || _fontInnerList is null) return;

            Dispatcher.UIThread.Post(() =>
            {
                if (_fontInnerList is null || current is null) return;
                _fontInnerList.SelectedItem = current;
                _fontInnerList.ScrollIntoView(current);
            }, DispatcherPriority.Loaded);
        }

        private void OnFontDropDownClosed(object? sender, EventArgs e)
        {
            _fontUserIsTyping = false;
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