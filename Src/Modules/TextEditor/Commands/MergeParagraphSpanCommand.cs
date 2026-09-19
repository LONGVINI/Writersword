using System;
using Writersword.Core.Interfaces.Modules;
using Writersword.Modules.TextEditor.ViewModels;

namespace Writersword.Modules.TextEditor.Commands
{
    /// <summary>
    /// Шаг отмены слияния двух абзацев — Backspace в начале абзаца и Delete в конце
    /// предыдущего.
    ///
    /// Зеркален <see cref="SplitParagraphSpanCommand"/> и описывает ту же пару состояний:
    /// слитое и разделённое. Отличается тем, какое из них считается отменой, и тем, куда
    /// после перехода встаёт каретка.
    ///
    /// Прежде слияние шло через <see cref="DocumentSnapshotCommand"/>: два прохода
    /// сериализации всей рукописи на каждый Backspace в начале строки, а на откате —
    /// пересборка вью-моделей всех абзацев. Здесь шаг весит два абзаца.
    /// </summary>
    public sealed class MergeParagraphSpanCommand : IUndoableCommand, ITextCommand
    {
        private readonly DocumentViewModel _docVm;
        private readonly DocumentViewModel.SplitParagraphSpan _span;

        public MergeParagraphSpanCommand(
            DocumentViewModel docVm, DocumentViewModel.SplitParagraphSpan span, string description)
        {
            _docVm = docVm;
            _span = span;
            Description = description;
        }

        public string Description { get; }

        /// <summary>
        /// Куда поставить каретку после отката или повтора: опознаватель абзаца и место
        /// в нём. Полотно назначает обработчик при укладке шага в стек.
        /// </summary>
        public Action<Guid, int>? RestoreCaretCallback { get; set; }

        /// <summary>
        /// Повтор: слить абзацы заново. Каретка встаёт на шов — туда, где кончается
        /// первый абзац и начинается вернувшийся текст второго.
        /// </summary>
        public void Execute()
        {
            if (!_docVm.ApplyParagraphUnion(_span)) return;

            RestoreCaretCallback?.Invoke(_span.FirstParaId, _span.At);
        }

        /// <summary>
        /// Отмена: разделить абзацы обратно. Каретка встаёт в начало вернувшегося
        /// второго абзаца — ровно туда, где она стояла перед нажатием Backspace.
        /// </summary>
        public void Undo()
        {
            if (!_docVm.ApplyParagraphDivision(_span)) return;
            if (_span.TailBlock is null) return;

            RestoreCaretCallback?.Invoke(_span.TailBlock.Id, 0);
        }

        // ── ITextCommand ──────────────────────────────────────────────────
        //
        // Тот же шаг под вторым именем: так он ложится и в общий стек отмены, и внутрь
        // составной команды.

        void ITextCommand.Apply(Models.Document.DocumentModel doc) => Execute();

        void ITextCommand.Revert(Models.Document.DocumentModel doc) => Undo();

        bool ITextCommand.TryMerge(ITextCommand next) => false;
    }
}
