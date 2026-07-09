using System;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Commands
{
    /// <summary>
    /// Команда удаления диапазона текста из параграфа.
    /// Покрывает: Delete, Backspace, выделение + ввод (замена выделенного).
    /// Сохраняет точные снапшоты удалённых runs для восстановления
    /// оригинального форматирования при Undo.
    /// </summary>
    public sealed class DeleteTextCommand : ITextCommand
    {
        private readonly Guid _paraId;
        private readonly int _charPos;
        private readonly int _length;

        // Позиция каретки после Revert (Undo). Для Backspace каретка возвращается за
        // восстановленный символ (_charPos + _length), для Delete — остаётся на месте (_charPos).
        // После Apply (Redo) каретка всегда встаёт в начало удалённого диапазона (_charPos).
        private readonly int _caretAfterRevert;

        /// <summary>
        /// Снапшоты удалённых runs.
        /// Заполняются при первом Apply и используются в Revert.
        /// </summary>
        private RunSnapshot[]? _deletedRuns;

        public string Description { get; }

        /// <summary>
        /// Вызывается после Apply (Redo) и Revert (Undo) для восстановления позиции каретки.
        /// Параметры: Id параграфа и символьная позиция каретки.
        /// Устанавливается DocumentCanvas после Push, чтобы не тянуть зависимость на UI в команду.
        /// </summary>
        public Action<Guid, int>? RestoreCaretCallback { get; set; }

        public DeleteTextCommand(Guid paraId, int charPos, int length,
            int caretAfterRevert, string description = "Delete text")
        {
            _paraId = paraId;
            _charPos = charPos;
            _length = length;
            _caretAfterRevert = caretAfterRevert;
            Description = description;
        }

        public void Apply(DocumentModel doc)
        {
            var para = DocumentModelHelper.FindParagraph(doc, _paraId);
            if (para is null) return;

            // При первом Apply сохраняем снапшот удаляемого диапазона.
            if (_deletedRuns is null)
                _deletedRuns = DocumentModelHelper.GetRunsInRange(para, _charPos, _length);

            DocumentModelHelper.DeleteRange(para, _charPos, _length);
            RestoreCaretCallback?.Invoke(_paraId, _charPos);
        }

        public void Revert(DocumentModel doc)
        {
            if (_deletedRuns is null) return;
            var para = DocumentModelHelper.FindParagraph(doc, _paraId);
            if (para is null) return;
            DocumentModelHelper.RestoreRuns(para, _charPos, _deletedRuns);
            RestoreCaretCallback?.Invoke(_paraId, _caretAfterRevert);
        }

        public bool TryMerge(ITextCommand next) => false;
    }
}
