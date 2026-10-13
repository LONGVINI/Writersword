using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Writersword.Styles.UserControls;

namespace Writersword.Modules.TextEditor.Views.Toolbar.Tabs
{
    public partial class RibbonFormulaTab : UserControl
    {
        private RibbonScrollContainer? _scrollContainer;

        public RibbonFormulaTab()
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
            _scrollContainer = this.FindControl<RibbonScrollContainer>("ScrollContainer");
        }

        // Группы вкладки не сворачиваются: что не влезло по ширине, листается стрелками.
        private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (_scrollContainer is null) return;
            _scrollContainer.ArrowsVisible = true;
            _scrollContainer.NotifySizeChanged();
        }
    }
}
