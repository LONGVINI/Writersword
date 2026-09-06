using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Writersword.Modules.TextEditor.Views.Toolbar.Tabs
{
    /// <summary>
    /// Контекстная вкладка «Расположение»: как плавающий объект стоит на листе —
    /// обтекание, сторона, отступы, размер, поворот и порядок наложения.
    ///
    /// Отделена от «Формата» не по типу объекта, а по смыслу: одна вкладка про то,
    /// чем объект является, вторая — про то, где он лежит. Обе одинаковы для
    /// картинки и фигуры, поэтому и DataContext у них один и тот же —
    /// RibbonImageTabViewModel.
    /// </summary>
    public partial class RibbonImagePlacementTab : UserControl
    {
        public RibbonImagePlacementTab()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
