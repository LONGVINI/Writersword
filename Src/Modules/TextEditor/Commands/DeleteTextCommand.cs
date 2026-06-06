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

        /// <summary>
        /// Снапшоты удалённых runs.
        /// Заполняются при первом Apply и используются в Revert.
        /// </summary>
        private RunSnapshot[]? _deletedRuns;

        public string Description { get; }

        public DeleteTextCommand(Guid paraId, int charPos, int length,
            string description = "Delete text")
        {
            _paraId = paraId;
            _charPos = charPos;
            _length = length;
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
        }

        public void Revert(DocumentModel doc)
        {
            if (_deletedRuns is null) return;
            var para = DocumentModelHelper.FindParagraph(doc, _paraId);
            if (para is null) return;
            DocumentModelHelper.RestoreRuns(para, _charPos, _deletedRuns);
        }

        public bool TryMerge(ITextCommand next) => false;
    }
}
