using System;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Commands
{
    /// <summary>
    /// Заменяет символы абзаца в диапазоне [from, from+len) на новый текст той же длины,
    /// сохраняя структуру ранов и их форматирование. Используется для смены регистра.
    /// Хранит исходный текст диапазона для отмены — Apply пишет новый текст, Revert старый.
    /// </summary>
    public sealed class ChangeCaseCommand : ITextCommand
    {
        private readonly Guid _paraId;
        private readonly int _from;
        private readonly string _oldText;
        private readonly string _newText;

        public ChangeCaseCommand(Guid paraId, int from, string oldText, string newText)
        {
            _paraId = paraId;
            _from = from;
            _oldText = oldText;
            _newText = newText;
        }

        public string Description => "Change case";

        public void Apply(DocumentModel doc) => WriteRange(doc, _newText);
        public void Revert(DocumentModel doc) => WriteRange(doc, _oldText);
        public bool TryMerge(ITextCommand next) => false;

        // Пишет символы text в раны абзаца начиная с позиции _from, посимвольно.
        // Длина не меняется, поэтому структура ранов и форматирование сохраняются.
        private void WriteRange(DocumentModel doc, string text)
        {
            var block = DocumentModelHelper.FindParagraph(doc, _paraId);
            if (block is null) return;

            int to = _from + text.Length;
            int offset = 0;
            foreach (var chunk in block.Chunks)
                foreach (var run in chunk.Runs)
                {
                    int rl = run.Text.Length;
                    if (rl == 0) continue;
                    int runStart = offset;
                    int s = Math.Max(_from, runStart);
                    int e = Math.Min(to, runStart + rl);
                    if (e > s)
                    {
                        var arr = run.Text.ToCharArray();
                        for (int g = s; g < e; g++)
                            arr[g - runStart] = text[g - _from];
                        run.Text = new string(arr);
                    }
                    offset += rl;
                }
            block.InvalidateAllChunks();
        }
    }
}