using System;
using Writersword.Core.Interfaces.Modules;
using Writersword.Modules.TextEditor.ViewModels;

namespace Writersword.Modules.TextEditor.Commands
{
    /// <summary>
    /// Шаг отмены вставки блока в поток: разрыва страницы, таблицы, картинки, фигуры.
    ///
    /// Прежде вставка шла через <see cref="DocumentSnapshotCommand"/>: два прохода
    /// сериализации всей рукописи на каждое нажатие, а на откате — разбор документа
    /// из текста. Здесь шаг хранит только то, что вставка поменяла: появившиеся блоки
    /// с их соседями слева и содержимое разрезанного абзаца до и после.
    /// </summary>
    public sealed class BlockSpliceCommand : IUndoableCommand, ITextCommand
    {
        private readonly DocumentViewModel _docVm;
        private readonly DocumentViewModel.BlockSplice _splice;

        public BlockSpliceCommand(
            DocumentViewModel docVm, DocumentViewModel.BlockSplice splice, string description)
        {
            _docVm = docVm;
            _splice = splice;
            Description = description;
        }

        public string Description { get; }

        /// <summary>
        /// Куда поставить каретку после отката или повтора: опознаватель абзаца и место
        /// в нём. Полотно назначает обработчик при укладке шага в стек.
        /// </summary>
        public Action<Guid, int>? RestoreCaretCallback { get; set; }

        /// <summary>Повтор: вставить блоки заново. Каретка встаёт за вставленным блоком.</summary>
        public void Execute()
        {
            if (!_docVm.ApplyBlockSplice(_splice, forward: true)) return;

            RestoreCaretCallback?.Invoke(_splice.CaretAfterParaId, _splice.CaretAfterChar);
        }

        /// <summary>Отмена: убрать вставленное. Каретка возвращается туда, где стояла.</summary>
        public void Undo()
        {
            if (!_docVm.ApplyBlockSplice(_splice, forward: false)) return;

            RestoreCaretCallback?.Invoke(_splice.CaretBeforeParaId, _splice.CaretBeforeChar);
        }

        // ── ITextCommand ──────────────────────────────────────────────────
        //
        // Тот же шаг под вторым именем: так он ложится в общий стек отмены полотна.

        void ITextCommand.Apply(Models.Document.DocumentModel doc) => Execute();

        void ITextCommand.Revert(Models.Document.DocumentModel doc) => Undo();

        bool ITextCommand.TryMerge(ITextCommand next) => false;
    }
}
