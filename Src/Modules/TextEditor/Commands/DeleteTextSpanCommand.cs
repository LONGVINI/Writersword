using System;
using Writersword.Core.Interfaces.Modules;
using Writersword.Modules.TextEditor.ViewModels;

namespace Writersword.Modules.TextEditor.Commands
{
    /// <summary>
    /// Шаг отмены удалённого выделения, идущего через несколько абзацев.
    ///
    /// Общий путь такой правки — <see cref="DocumentSnapshotCommand"/> — сериализует всю
    /// рукопись до и после, а при откате пересоздаёт вью-модели всех её абзацев. Цена
    /// не зависит от того, сколько удалили: сносишь десять строк из трёхсотстраничной
    /// книги — платишь за триста страниц. Кэш раскладок при этом обнуляется целиком, и
    /// книга верстается заново.
    ///
    /// Здесь шаг помнит ровно снятое: прежнее содержимое первого абзаца и сами снятые
    /// абзацы живыми объектами. Откат — это вставка их обратно; остальная рукопись не
    /// трогается, её раскладки остаются в кэше.
    ///
    /// Блоки хранятся живыми, а не текстом: из документа они вынуты, и держит их один
    /// этот шаг. Вытеснили его из истории — вернуть кусок уже нечем, как и любой другой
    /// шаг за пределом стека.
    /// </summary>
    public sealed class DeleteTextSpanCommand : IUndoableCommand, ITextCommand
    {
        private readonly DocumentViewModel _docVm;
        private DocumentViewModel.RemovedTextSpan _span;

        public DeleteTextSpanCommand(
            DocumentViewModel docVm, DocumentViewModel.RemovedTextSpan span, string description)
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

        /// <summary>Повтор: снять тот же кусок заново.</summary>
        public void Execute()
        {
            var again = _docVm.RemoveTextSpan(_span);
            if (again is not null) _span = again;

            RestoreCaretCallback?.Invoke(_span.FirstParaId, _span.From);
        }

        /// <summary>Отмена: вернуть кусок на место.</summary>
        public void Undo()
        {
            _docVm.RestoreTextSpan(_span);

            // Каретка встаёт туда, где выделение начиналось, — на границу правки, которую
            // отменили. Не в конец вернувшегося куска: человек отменяет удаление, чтобы
            // увидеть возвращённое, а начало куска и есть то место, куда он смотрел.
            RestoreCaretCallback?.Invoke(_span.FirstParaId, _span.From);
        }

        // ── ITextCommand ──────────────────────────────────────────────────
        //
        // Тот же шаг, второй раз названный. Нужно это затем, чтобы шаг мог лечь и в общий
        // стек отмены, и внутрь составной команды: правка поверх выделения — это удаление
        // и вставка одним нажатием, и человек отменяет её тоже одним.
        //
        // Документ параметром не берётся: шаг работает через вью-модель, которая владеет
        // и документом, и списком абзацев. Держать два пути к одной рукописи значило бы
        // однажды пройти по ним вразнобой.

        void ITextCommand.Apply(Models.Document.DocumentModel doc) => Execute();

        void ITextCommand.Revert(Models.Document.DocumentModel doc) => Undo();

        bool ITextCommand.TryMerge(ITextCommand next) => false;
    }
}
