using Writersword.Core.Interfaces.Modules;
using Writersword.Modules.TextEditor.ViewModels;

namespace Writersword.Modules.TextEditor.Commands
{
    /// <summary>
    /// Команда изменения текста для Undo/Redo стека.
    /// </summary>
    public class TextChangeCommand : IUndoableCommand
    {
        private readonly TextEditorViewModel _vm;
        private readonly string _before;
        private readonly string _after;

        public string Description => $"Text change ({_after.Length} chars)";

        public TextChangeCommand(TextEditorViewModel vm, string before, string after)
        {
            _vm = vm;
            _before = before;
            _after = after;
        }

        public void Execute() => _vm.ApplyTextSilently(_after);
        public void Undo() => _vm.ApplyTextSilently(_before);
    }
}