using System;
using System.Linq;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Commands
{
    /// <summary>
    /// Команда слияния параграфа с предыдущим (Backspace в начале параграфа).
    /// Хранит полный снапшот поглощаемого параграфа чтобы Revert мог его восстановить.
    /// </summary>
    public sealed class MergeParagraphCommand : ITextCommand
    {
        /// <summary>Id параграфа который будет поглощён (текущий, после которого стоит курсор).</summary>
        private readonly Guid _paraId;

        /// <summary>Id предыдущего параграфа — куда переносится текст.</summary>
        private Guid _prevParaId;

        /// <summary>Снапшот поглощаемого параграфа для точного восстановления.</summary>
        private ParagraphSnapshot? _snapshot;

        /// <summary>Позиция в предыдущем параграфе после которой добавился текст.</summary>
        private int _mergePos;

        public string Description => "Merge paragraph";

        public MergeParagraphCommand(Guid paraId)
        {
            _paraId = paraId;
        }

        public void Apply(DocumentModel doc)
        {
            var pos = DocumentModelHelper.FindBlockPosition(doc, _paraId);
            if (pos is null) return;

            var (section, blockIndex) = pos.Value;
            if (blockIndex == 0) return;
            if (section.Blocks[blockIndex - 1] is not ParagraphBlock prevPara) return;

            var currentPara = (ParagraphBlock)section.Blocks[blockIndex];

            // Сохраняем снапшот и идентификатор предыдущего параграфа.
            _snapshot = ParagraphSnapshot.From(currentPara);
            _prevParaId = prevPara.Id;
            _mergePos = prevPara.GetPlainText().Length;

            // Переносим весь текст текущего параграфа в конец предыдущего.
            var runs = DocumentModelHelper.GetRunsInRange(currentPara, 0, currentPara.GetPlainText().Length);
            if (runs.Length > 0)
                DocumentModelHelper.RestoreRuns(prevPara, _mergePos, runs);

            section.Blocks.RemoveAt(blockIndex);
        }

        public void Revert(DocumentModel doc)
        {
            if (_snapshot is null) return;

            var prevPos = DocumentModelHelper.FindBlockPosition(doc, _prevParaId);
            if (prevPos is null) return;

            var (section, prevBlockIndex) = prevPos.Value;
            var prevPara = (ParagraphBlock)section.Blocks[prevBlockIndex];

            // Удаляем перенесённый текст из предыдущего параграфа.
            int currentLen = prevPara.GetPlainText().Length;
            int addedLen = currentLen - _mergePos;
            if (addedLen > 0)
                DocumentModelHelper.DeleteRange(prevPara, _mergePos, addedLen);

            // Воссоздаём удалённый параграф с оригинальным содержимым.
            var restored = new ParagraphBlock { Id = _paraId };
            if (_snapshot.Runs.Count > 0)
                DocumentModelHelper.RestoreRuns(restored, 0, _snapshot.Runs.ToArray());

            section.Blocks.Insert(prevBlockIndex + 1, restored);
        }

        public bool TryMerge(ITextCommand next) => false;
    }
}
