using System;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Styles;

namespace Writersword.Modules.TextEditor.Commands
{
    /// <summary>
    /// Команда изменения свойства параграфа.
    /// Один класс покрывает все ParagraphProperties:
    /// Alignment, LeftIndent, RightIndent, FirstLineIndent,
    /// SpaceBefore, SpaceAfter, LineSpacing, StyleName.
    /// Хранит делегаты apply/revert вместо конкретного значения —
    /// это позволяет использовать один класс для любого свойства.
    /// </summary>
    public sealed class SetParagraphPropertyCommand : ITextCommand
    {
        private readonly Guid _paraId;
        private readonly Action<ParagraphProperties> _apply;
        private readonly Action<ParagraphProperties> _revert;

        public string Description { get; }

        /// <param name="paraId">Id параграфа.</param>
        /// <param name="apply">Применить новое значение к ParagraphProperties.</param>
        /// <param name="revert">Восстановить старое значение в ParagraphProperties.</param>
        /// <param name="description">Описание для UI (например, "Align center", "Set indent").</param>
        public SetParagraphPropertyCommand(Guid paraId,
            Action<ParagraphProperties> apply,
            Action<ParagraphProperties> revert,
            string description = "Format paragraph")
        {
            _paraId = paraId;
            _apply = apply;
            _revert = revert;
            Description = description;
        }

        public void Apply(DocumentModel doc)
        {
            var para = DocumentModelHelper.FindParagraph(doc, _paraId);
            if (para is null) return;
            _apply(para.Properties);
        }

        public void Revert(DocumentModel doc)
        {
            var para = DocumentModelHelper.FindParagraph(doc, _paraId);
            if (para is null) return;
            _revert(para.Properties);
        }

        public bool TryMerge(ITextCommand next) => false;
    }
}
