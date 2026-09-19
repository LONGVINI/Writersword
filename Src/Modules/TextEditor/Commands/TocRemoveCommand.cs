using System.Collections.Generic;
using Writersword.Core.Interfaces.Modules;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Toc;
using Writersword.Modules.TextEditor.ViewModels;

namespace Writersword.Modules.TextEditor.Commands
{
    /// <summary>
    /// Шаг отмены снятого оглавления: помнит сами строки, а не снимок всей рукописи.
    ///
    /// Общий путь отмены — <see cref="DocumentSnapshotCommand"/> — сериализует документ
    /// целиком до и после операции, а при откате пересоздаёт вью-модели всех абзацев.
    /// Для оглавления это несоразмерно: снимается триста строк, а платит за них вся
    /// книга. На трёхсотстраничной рукописи снимки стоили сотню миллисекунд на самом
    /// удалении, а откат — двух секунд: кэш раскладок обнулялся целиком, и три тысячи
    /// абзацев верстались заново ради трёхсот вернувшихся.
    ///
    /// Здесь же откат — это вставка запомненных блоков обратно. Остальные абзацы не
    /// трогаются вовсе, их раскладки остаются в кэше, и книга пересобирается по готовому.
    ///
    /// Блоки хранятся живыми объектами, а не текстом: из документа они вынуты, и держит
    /// их один этот шаг отмены. Пока он в стеке, они целы; вытеснили его из стека —
    /// вернуть оглавление уже нечем, как и любой другой шаг за пределом истории.
    /// </summary>
    public sealed class TocRemoveCommand : IUndoableCommand
    {
        private readonly DocumentViewModel _docVm;
        private readonly List<ParagraphBlock> _blocks;
        private readonly TocSettings _settings;
        private readonly int _blockIndex;

        // Пустой абзац, заведённый на месте снесённого оглавления, когда в рукописи не
        // осталось ничего другого. Отмена обязана его убрать: иначе каждый круг
        // «удалить — вернуть» оставлял бы в начале книги лишнюю пустую строку.
        //
        // Не readonly: повтор заводит новый абзац, и помнить надо именно его.
        private ParagraphBlock? _filler;

        public TocRemoveCommand(
            DocumentViewModel docVm,
            int blockIndex,
            List<ParagraphBlock> blocks,
            TocSettings settings,
            ParagraphBlock? filler,
            string description)
        {
            _docVm = docVm;
            _blockIndex = blockIndex;
            _blocks = blocks;
            _settings = settings;
            _filler = filler;
            Description = description;
        }

        public string Description { get; }

        /// <summary>Повтор: снять оглавление заново.</summary>
        public void Execute()
        {
            _filler = _docVm.DropTocBlocksForRedo(_settings);
        }

        /// <summary>Отмена: вернуть строки и запись настроек на прежнее место.</summary>
        public void Undo()
        {
            _docVm.RestoreTocBlocks(_blockIndex, _blocks, _settings, _filler);
            _filler = null;
        }
    }
}
