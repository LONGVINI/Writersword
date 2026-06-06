using System;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;

namespace Writersword.Modules.TextEditor.Commands
{
    /// <summary>
    /// Команда изменения форматирования символов в диапазоне.
    /// Один класс покрывает все run-свойства: Bold, Italic, Underline,
    /// Strikethrough, FontFamily, FontSize, TextColor, HighlightColor,
    /// Superscript, Subscript, AllCaps, SmallCaps, Language, ClearFormatting.
    /// Хранит снапшоты оригинальных runs для точного восстановления при Undo.
    /// </summary>
    public sealed class SetRunPropertyCommand : ITextCommand
    {
        private readonly Guid _paraId;
        private readonly int _from;
        private readonly int _to;
        private readonly Action<RunProperties> _mutate;

        /// <summary>
        /// Снапшоты runs в диапазоне ДО изменения.
        /// Заполняются при первом Apply и используются в Revert.
        /// </summary>
        private RunSnapshot[]? _originalRuns;

        public string Description { get; }

        /// <param name="paraId">Id параграфа.</param>
        /// <param name="from">Начало диапазона (включительно).</param>
        /// <param name="to">Конец диапазона (не включительно).</param>
        /// <param name="mutate">Мутация применяемая к RunProperties каждого run в диапазоне.</param>
        /// <param name="description">Описание для UI (например, "Bold", "Set color").</param>
        public SetRunPropertyCommand(Guid paraId, int from, int to,
            Action<RunProperties> mutate, string description = "Format text")
        {
            _paraId = paraId;
            _from = from;
            _to = to;
            _mutate = mutate;
            Description = description;
        }

        public void Apply(DocumentModel doc)
        {
            var para = DocumentModelHelper.FindParagraph(doc, _paraId);
            if (para is null) return;

            // Сохраняем оригинальное состояние при первом Apply.
            if (_originalRuns is null)
                _originalRuns = DocumentModelHelper.GetRunsInRange(para, _from, _to - _from);

            DocumentModelHelper.ApplyRunProperty(para, _from, _to, _mutate);
        }

        public void Revert(DocumentModel doc)
        {
            if (_originalRuns is null) return;
            var para = DocumentModelHelper.FindParagraph(doc, _paraId);
            if (para is null) return;

            // Удаляем изменённый диапазон и восстанавливаем оригинальные runs.
            DocumentModelHelper.DeleteRange(para, _from, _to - _from);
            DocumentModelHelper.RestoreRuns(para, _from, _originalRuns);
        }

        public bool TryMerge(ITextCommand next) => false;
    }
}
