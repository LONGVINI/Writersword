using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Writersword.Modules.TextEditor.Views.Toolbar.Tabs
{
    /// <summary>
    /// Контекстная вкладка «Колонтитулы». Появляется на время работы с колонтитулами
    /// и собирает в одном месте номер страницы, параметры и исключения для листов.
    /// </summary>
    public partial class RibbonHeaderFooterTab : UserControl
    {
        public RibbonHeaderFooterTab()
        {
            InitializeComponent();
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
    }
}
