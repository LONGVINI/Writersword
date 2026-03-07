using Avalonia.Controls;

namespace Writersword.Modules.TextEditor.Views
{
    public partial class TextEditorView : UserControl
    {
        public TextEditorView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Подключает TextBox.Undo() и TextBox.Redo() напрямую к модулю.
        /// Вызывается из TextEditorModule.CreateView().
        /// </summary>
        public void WireModule(TextEditorModule module)
        {
            Loaded += (_, _) =>
            {
                var textBox = this.FindControl<TextBox>("EditorTextBox");
                if (textBox == null) return;

                module.UndoAction = () => textBox.Undo();
                module.RedoAction = () => textBox.Redo();
            };
        }
    }
}