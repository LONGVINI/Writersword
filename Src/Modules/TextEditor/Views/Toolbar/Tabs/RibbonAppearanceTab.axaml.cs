using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Writersword.Modules.TextEditor.ViewModels.Toolbar;
using Writersword.Styles.UserControls;

namespace Writersword.Modules.TextEditor.Views.Toolbar.Tabs
{
    public partial class RibbonAppearanceTab : UserControl
    {
        private RibbonScrollContainer? _scrollContainer;

        public RibbonAppearanceTab()
        {
            InitializeComponent();
            SizeChanged += OnSizeChanged;
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        /// <summary>Нажали «Настроить виды» — список видов больше не нужен.</summary>
        private void OnConfigureThemesClick(object? sender, RoutedEventArgs e)
        {
            // Список закрывается следующим тактом, а не сейчас.
            //
            // Закрытый сразу, он уносит с собой и нажатую кнопку: она уходит из
            // дерева до того, как кнопка дойдёт до своей команды, и «Настроить виды»
            // переставало открывать окно вовсе.
            var source = sender;
            Dispatcher.UIThread.Post(() => CloseHostFlyout(source), DispatcherPriority.Background);
        }

        /// <summary>
        /// Закрывает всплывающий список, из которого нажали кнопку.
        ///
        /// Список видов живёт во флайауте и сам по себе не закрывается: кнопка
        /// «Настроить виды» открывает поверх него окно, а список остаётся висеть у
        /// верхнего края и уходит только от первого нажатия мимо. Человек уже видит
        /// открытое окно, и висящий над ним список читается как подвисшая панель.
        /// </summary>
        private static void CloseHostFlyout(object? sender)
        {
            if (sender is not Control control) return;
            if (TopLevel.GetTopLevel(control) is not PopupRoot root) return;
            if (root.Parent is not Popup popup) return;

            // Через сам флайаут, а не через окно: тогда кнопка-владелец узнаёт, что
            // список закрыт, и не остаётся в нажатом виде.
            if (popup.PlacementTarget is Button { Flyout: { } flyout })
            {
                flyout.Hide();
                return;
            }

            popup.Close();
        }


        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _scrollContainer = this.FindControl<RibbonScrollContainer>("ScrollContainer");
        }

        private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (DataContext is RibbonAppearanceTabViewModel vm)
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
