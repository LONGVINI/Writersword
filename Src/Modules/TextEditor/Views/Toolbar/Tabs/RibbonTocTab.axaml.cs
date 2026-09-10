using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Writersword.Modules.TextEditor.ViewModels.Toolbar;

namespace Writersword.Modules.TextEditor.Views.Toolbar.Tabs
{
    /// <summary>
    /// Контекстная вкладка «Оглавление». Появляется, когда каретка стоит внутри
    /// оглавления, и собирает в одном месте всё, что к нему относится.
    /// </summary>
    public partial class RibbonTocTab : UserControl
    {
        public RibbonTocTab()
        {
            InitializeComponent();

            // Группы прячутся по очереди, когда ленте не хватает ширины — так же,
            // как на прочих вкладках.
            SizeChanged += (_, e) =>
            {
                if (DataContext is RibbonTocTabViewModel vm)
                    vm.UpdateLayout(e.NewSize.Width);
            };
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
    }
}
