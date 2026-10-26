using Avalonia.Controls;
using Avalonia.Interactivity;
using Writersword.Modules.Characters.ViewModels.Anketas;

namespace Writersword.Modules.Characters.Views.Fields
{
    /// <summary>
    /// Особый вид всей анкеты в панели свойств редактора анкет: выравнивание
    /// полей в строках, расстояние между строками, высота описаний, шрифт и
    /// размеры. Свёрнут по умолчанию — обычно им не пользуются.
    /// </summary>
    public partial class AnketaLookPanel : UserControl
    {
        public AnketaLookPanel() => InitializeComponent();

        private void OnResetClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            (DataContext as AnketaSheetViewModel)?.ResetAnketaLook();
        }
    }
}
