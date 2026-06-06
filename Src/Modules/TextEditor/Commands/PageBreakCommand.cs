using System;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Commands
{
    /// <summary>
    /// Команда вставки или удаления разрыва страницы.
    /// Разрыв вставляется как BreakBlock сразу после указанного параграфа.
    /// </summary>
    public sealed class PageBreakCommand : ITextCommand
    {
        /// <summary>
        /// Id параграфа после которого вставляется (или перед следующим блоком удаляется) разрыв.
        /// </summary>
        private readonly Guid _afterParagraphId;

        /// <summary>True — вставить разрыв, False — удалить.</summary>
        private readonly bool _insert;

        public string Description => _insert ? "Insert page break" : "Delete page break";

        /// <param name="afterParagraphId">Id параграфа после которого стоит разрыв.</param>
        /// <param name="insert">True для вставки, False для удаления.</param>
        public PageBreakCommand(Guid afterParagraphId, bool insert)
        {
            _afterParagraphId = afterParagraphId;
            _insert = insert;
        }

        public void Apply(DocumentModel doc)
        {
            if (_insert) Insert(doc);
            else Delete(doc);
        }

        public void Revert(DocumentModel doc)
        {
            if (_insert) Delete(doc);
            else Insert(doc);
        }

        private void Insert(DocumentModel doc)
        {
            var pos = DocumentModelHelper.FindBlockPosition(doc, _afterParagraphId);
            if (pos is null) return;
            var (section, index) = pos.Value;
            section.Blocks.Insert(index + 1, new BreakBlock { BreakType = BreakType.Page });
        }

        private void Delete(DocumentModel doc)
        {
            var pos = DocumentModelHelper.FindBlockPosition(doc, _afterParagraphId);
            if (pos is null) return;
            var (section, index) = pos.Value;
            if (index + 1 < section.Blocks.Count && section.Blocks[index + 1] is BreakBlock)
                section.Blocks.RemoveAt(index + 1);
        }

        public bool TryMerge(ITextCommand next) => false;
    }
}
