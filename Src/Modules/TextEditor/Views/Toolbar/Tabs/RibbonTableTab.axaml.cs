using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Writersword.Modules.TextEditor.Views.Toolbar.Tabs
{
    public partial class RibbonTableTab : UserControl
    {
        public RibbonTableTab()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        /// <summary>
        /// Закрывает список стилей линии после выбора. Список собран из кнопок, а не
        /// из MenuItem: у пунктов меню в шаблоне есть колонки под значок и стрелку
        /// подменю, из-за них по краям оставались пустоты. Кнопки сами выпадашку не
        /// закрывают, поэтому закрываем её здесь.
        /// </summary>
        private void OnLineStyleChosen(object? sender, RoutedEventArgs e)
        {
            this.FindControl<Button>("BorderStyleButton")?.Flyout?.Hide();
        }
    }
}
