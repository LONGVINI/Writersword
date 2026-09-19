using Writersword.Core.Interfaces.Modules;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.ViewModels;

namespace Writersword.Modules.TextEditor.Commands
{
    /// <summary>
    /// Шаг отмены удаления картинки — с листа, из-под текста или из самой строки.
    ///
    /// Прежде это шло через <see cref="DocumentSnapshotCommand"/>: удаление меняет состав
    /// блоков, и казалось, что вернуть по значениям нечего — блока в документе уже нет.
    /// Но блок никуда не девается, если его подержать: шаг хранит саму картинку живым
    /// объектом и место, из которого она вынута. Для картинки в строке хранится ещё и
    /// прежнее содержимое абзаца-хозяина — её символ стоял среди букв.
    ///
    /// Цена шага — одна картинка и, в худшем случае, один абзац. Снимок стоил всей
    /// рукописи, дважды.
    /// </summary>
    public sealed class RemoveImageCommand : IUndoableCommand, ITextCommand
    {
        private readonly DocumentViewModel _docVm;
        private DocumentViewModel.RemovedImage _removed;

        public RemoveImageCommand(
            DocumentViewModel docVm, DocumentViewModel.RemovedImage removed, string description)
        {
            _docVm = docVm;
            _removed = removed;
            Description = description;
        }

        public string Description { get; }

        /// <summary>Повтор: снять картинку заново.</summary>
        public void Execute()
        {
            if (_removed.Image is null) return;

            var again = _docVm.TakeImageOut(_removed.Image);
            if (again is not null) _removed = again;
        }

        /// <summary>Отмена: вернуть картинку туда, откуда сняли.</summary>
        public void Undo() => _docVm.PutImageBack(_removed);

        void ITextCommand.Apply(DocumentModel doc) => Execute();

        void ITextCommand.Revert(DocumentModel doc) => Undo();

        bool ITextCommand.TryMerge(ITextCommand next) => false;
    }
}
