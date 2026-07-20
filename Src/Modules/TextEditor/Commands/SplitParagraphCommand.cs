using System;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Commands
{
    /// <summary>
    /// Команда разбиения параграфа на два (клавиша Enter).
    /// Хранит Id нового параграфа чтобы Revert мог найти его для удаления и слияния обратно.
    /// </summary>
    public sealed class SplitParagraphCommand : ITextCommand
    {
        private readonly Guid _paraId;
        private readonly int _splitPos;

        /// <summary>
        /// Id нового параграфа созданного при разбиении.
        /// Задаётся в конструкторе и используется в Revert для поиска параграфа.
        /// </summary>
        public Guid NewParagraphId { get; }

        public string Description => "Split paragraph";

        public SplitParagraphCommand(Guid paraId, int splitPos)
        {
            _paraId = paraId;
            _splitPos = splitPos;
            NewParagraphId = Guid.NewGuid();
        }

        public void Apply(DocumentModel doc)
        {
            var pos = DocumentModelHelper.FindBlockPosition(doc, _paraId);
            if (pos is null) return;

            var (section, blockIndex) = pos.Value;
            var original = (ParagraphBlock)section.Blocks[blockIndex];

            int plainLen = original.GetPlainText().Length;
            int cutPos = Math.Min(_splitPos, plainLen);

            // Собираем хвост параграфа который уйдёт в новый.
            var tailRuns = DocumentModelHelper.GetRunsInRange(original, cutPos, plainLen - cutPos);

            // Удаляем хвост из исходного параграфа.
            if (cutPos < plainLen)
                DocumentModelHelper.DeleteRange(original, cutPos, plainLen - cutPos);

            // Создаём новый параграф с хвостом и заранее известным Id.
            var newPara = new ParagraphBlock { Id = NewParagraphId };

            // Новый абзац наследует форматирование исходного, чтобы Enter продолжал
            // тот же стиль/выравнивание/отступы, а в списке — создавал следующий элемент.
            newPara.Properties = original.Properties.Clone();

            if (original.ListProperties is not null)
            {
                var lp = original.ListProperties.Clone();
                // Следующий элемент всегда продолжает нумерацию (перезапуск наследовать нельзя,
                // иначе каждый новый элемент начинал бы список заново).
                lp.ContinueNumbering = true;
                newPara.ListProperties = lp;
            }

            if (tailRuns.Length > 0)
                DocumentModelHelper.RestoreRuns(newPara, 0, tailRuns);

            section.Blocks.Insert(blockIndex + 1, newPara);
        }

        public void Revert(DocumentModel doc)
        {
            var originalPos = DocumentModelHelper.FindBlockPosition(doc, _paraId);
            if (originalPos is null) return;

            var (section, blockIndex) = originalPos.Value;

            if (blockIndex + 1 >= section.Blocks.Count) return;
            if (section.Blocks[blockIndex + 1] is not ParagraphBlock nextPara) return;
            if (nextPara.Id != NewParagraphId) return;

            var original = (ParagraphBlock)section.Blocks[blockIndex];

            // Возвращаем хвост обратно в исходный параграф.
            int originalLen = original.GetPlainText().Length;
            var nextRuns = DocumentModelHelper.GetRunsInRange(nextPara, 0, nextPara.GetPlainText().Length);
            if (nextRuns.Length > 0)
                DocumentModelHelper.RestoreRuns(original, originalLen, nextRuns);

            section.Blocks.RemoveAt(blockIndex + 1);
        }

        public bool TryMerge(ITextCommand next) => false;
    }
}
