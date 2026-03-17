using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Writersword.Modules.TextEditor.ViewModels.Toolbar;
using Writersword.Styles.UserControls;

namespace Writersword.Modules.TextEditor.Views.Toolbar.Tabs
{
    public partial class RibbonHomeTab : UserControl
    {
        private RibbonScrollContainer? _scrollContainer;
        private ListBox? _fontSizeList;

        public RibbonHomeTab()
        {
            InitializeComponent();
            SizeChanged += OnSizeChanged;
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            AttachControls();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            if (_fontSizeList is not null)
                _fontSizeList.SelectionChanged -= OnFontSizeListSelectionChanged;
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
        }

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

        /// <summary>
        /// Применяет размер шрифта выбранный из выпадающего списка.
        /// После применения сбрасывает выделение чтобы список можно было выбрать повторно.
        /// </summary>
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