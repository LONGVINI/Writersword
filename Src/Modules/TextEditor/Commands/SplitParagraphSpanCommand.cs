using System;
using Writersword.Core.Interfaces.Modules;
using Writersword.Modules.TextEditor.ViewModels;

namespace Writersword.Modules.TextEditor.Commands
{
    /// <summary>
    /// Шаг отмены деления абзаца клавишей Enter.
    ///
    /// Общий путь такой правки — <see cref="DocumentSnapshotCommand"/> — сериализует всю
    /// рукопись в JSON дважды: при открытии шага и при его закрытии. Цена не зависит от
    /// того, что именно нажали: делишь одну строку в трёхсотстраничной книге — платишь
    /// за триста страниц, и платишь на каждый Enter. Откат вдобавок пересоздаёт
    /// вью-модели всех абзацев и обнуляет кэш раскладок, то есть верстает книгу заново.
    ///
    /// Здесь шаг помнит ровно разделённое: содержимое слитого абзаца и отделённый абзац
    /// живым объектом. Остальная рукопись не трогается, её раскладки остаются в кэше.
    ///
    /// Отделённый абзац хранится живым, а не текстом: из документа он вынут, и держит
    /// его один этот шаг. Вытеснили шаг из истории — абзац уходит вместе с ним, как и
    /// любой другой шаг за пределом стека.
    /// </summary>
    public sealed class SplitParagraphSpanCommand : IUndoableCommand, ITextCommand
    {
        private readonly DocumentViewModel _docVm;
        private readonly DocumentViewModel.SplitParagraphSpan _span;

        public SplitParagraphSpanCommand(
            DocumentViewModel docVm, DocumentViewModel.SplitParagraphSpan span, string description)
        {
            _docVm = docVm;
            _span = span;
            Description = description;
        }

        public string Description { get; }

        /// <summary>
        /// Куда поставить каретку после отката или повтора: опознаватель абзаца и место
        /// в нём. Полотно назначает обработчик при укладке шага в стек — само оно про
        /// абзацы модели ничего не знает, а модель ничего не знает про слайсы раскладки.
        /// </summary>
        public Action<Guid, int>? RestoreCaretCallback { get; set; }

        /// <summary>Повтор: разделить абзац заново. Каретка встаёт в начало отделённого.</summary>
        public void Execute()
        {
            if (!_docVm.ApplyParagraphDivision(_span)) return;
            if (_span.TailBlock is null) return;

            RestoreCaretCallback?.Invoke(_span.TailBlock.Id, 0);
        }

        /// <summary>
        /// Отмена: склеить абзац обратно. Каретка встаёт в место деления — туда, где она
        /// стояла перед нажатием, и туда же, куда смотрит человек, отменяя Enter.
        /// </summary>
        public void Undo()
        {
            if (!_docVm.ApplyParagraphUnion(_span)) return;

            RestoreCaretCallback?.Invoke(_span.FirstParaId, _span.At);
        }

        // ── ITextCommand ──────────────────────────────────────────────────
        //
        // Тот же шаг под вторым именем: так он ложится и в общий стек отмены, и внутрь
        // составной команды. Enter поверх выделения — это удаление и деление одним
        // нажатием, и отменяться они обязаны вместе.

        void ITextCommand.Apply(Models.Document.DocumentModel doc) => Execute();

        void ITextCommand.Revert(Models.Document.DocumentModel doc) => Undo();

        bool ITextCommand.TryMerge(ITextCommand next) => false;
    }
}
