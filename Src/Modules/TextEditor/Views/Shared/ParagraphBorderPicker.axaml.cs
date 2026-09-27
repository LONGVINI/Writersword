using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Writersword.Modules.TextEditor.Views.Shared
{
    /// <summary>
    /// Выбор рамки абзаца: образец абзаца с кнопками у сторон и в центре, ниже —
    /// вид линии, толщина, отступ от текста и цвет. Данные и команды берёт из
    /// DataContext вкладки «Главная».
    /// </summary>
    public partial class ParagraphBorderPicker : UserControl
    {
        public ParagraphBorderPicker()
        {
            InitializeComponent();
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
    }
}
