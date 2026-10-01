using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Writersword.Modules.TextEditor.Views.Shared
{
    /// <summary>
    /// Выбор подчёркивания: плитки видов линии и цвет линии. Данные и команды берёт
    /// из DataContext вкладки «Главная».
    /// </summary>
    public partial class UnderlinePicker : UserControl
    {
        public UnderlinePicker()
        {
            InitializeComponent();
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
    }
}
