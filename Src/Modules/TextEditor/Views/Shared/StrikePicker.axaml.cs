using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Writersword.Modules.TextEditor.Views.Shared
{
    /// <summary>
    /// Выбор зачёркивания: плитки «Нет», одинарное и двойное. Данные и команды берёт
    /// из DataContext вкладки «Главная».
    /// </summary>
    public partial class StrikePicker : UserControl
    {
        public StrikePicker()
        {
            InitializeComponent();
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
    }
}
