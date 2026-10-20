using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Writersword.Modules.TextEditor.ViewModels.Toolbar;
using Writersword.Styles.UserControls;

namespace Writersword.Modules.TextEditor.Views.Toolbar.Tabs
{
    public partial class RibbonLayoutTab : UserControl
    {
        private RibbonScrollContainer? _scrollContainer;

        public RibbonLayoutTab()
        {
            InitializeComponent();
            SizeChanged += OnSizeChanged;

            // Колонки показываются по разделу под кареткой, а каретка ходит, пока лента
            // не на виду: состояние перечитывается, когда к ленте подводят мышь.
            PointerEntered += (_, _) => (DataContext as RibbonLayoutTabViewModel)?.RefreshColumns();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _scrollContainer = this.FindControl<RibbonScrollContainer>("ScrollContainer");
            (DataContext as RibbonLayoutTabViewModel)?.RefreshColumns();
        }

        /// <summary>
        /// Меню «Поля» открывается: поля и переплёт перечитываются из документа — их
        /// могли поменять линейкой или импортом, пока меню было закрыто.
        /// </summary>
        private void OnMarginsFlyoutOpening(object? sender, EventArgs e)
        {
            if (DataContext is RibbonLayoutTabViewModel vm)
                vm.RefreshPageMargins();
        }

        /// <summary>
        /// Окно «Параметры раздела» открывается: параметры перечитываются из раздела под
        /// кареткой — каретка могла уйти в другой раздел.
        /// </summary>
        private void OnSectionSetupFlyoutOpening(object? sender, EventArgs e)
        {
            if (DataContext is RibbonLayoutTabViewModel vm)
                vm.RefreshSectionSetup();
        }

        /// <summary>Окно «Другие колонки» открывается: колонки раздела под кареткой.</summary>
        private void OnColumnsFlyoutOpening(object? sender, EventArgs e)
        {
            if (DataContext is RibbonLayoutTabViewModel vm)
                vm.RefreshColumnDialog();
        }

        private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (DataContext is RibbonLayoutTabViewModel vm)
            {
                vm.UpdateLayout(e.NewSize.Width);
                if (_scrollContainer is not null)
                {
                    _scrollContainer.ArrowsVisible = true;
                    _scrollContainer.NotifySizeChanged();
                }
            }
        }
    }
}