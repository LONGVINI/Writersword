using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Writersword.Modules.TextEditor.Views.Toolbar.Tabs
{
    public partial class RibbonImageTab : UserControl
    {
        public RibbonImageTab()
        {
            InitializeComponent();

            // Клик или Tab в числовое поле вкладки сразу выделяет всё значение,
            // чтобы новое число можно было вводить без ручной очистки поля.
            //AddHandler(GotFocusEvent, OnAnyGotFocus, RoutingStrategies.Bubble);
        }

        //private void OnAnyGotFocus(object? sender, GotFocusEventArgs e)
        //{
        //    if (e.Source is not TextBox tb) return;
        //    if (tb.FindAncestorOfType<NumericUpDown>() is null) return;

        //    // Отложенно: NumericUpDown после фокуса сам ставит каретку,
        //    // немедленный SelectAll был бы перетёрт этим действием.
        //    Dispatcher.UIThread.Post(() =>
        //    {
        //        if (tb.IsFocused) tb.SelectAll();
        //    });
        //}

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
