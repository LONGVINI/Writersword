using Writersword.Core.Interfaces.Modules;
using Writersword.Modules.TextEditor.Models.Page;
using Writersword.Modules.TextEditor.ViewModels;

namespace Writersword.Modules.TextEditor.Commands
{
    /// <summary>
    /// Шаг отмены правки колонтитулов: помнит настройки колонтитулов до и после, а не
    /// снимок всей рукописи.
    ///
    /// Колонтитулы и правила страниц живут у документа отдельно от текста, и ни один
    /// абзац их правка не трогает. Снимок документа ради них сериализовал бы всю книгу
    /// и сбросил бы кэш раскладки — здесь откат сводится к подмене одного объекта.
    /// </summary>
    public sealed class HeaderFooterChangeCommand : IUndoableCommand
    {
        private readonly DocumentViewModel _docVm;
        private readonly HeaderFooterSettings? _before;
        private readonly HeaderFooterSettings? _after;

        public HeaderFooterChangeCommand(
            DocumentViewModel docVm,
            HeaderFooterSettings? before,
            HeaderFooterSettings? after,
            string description)
        {
            _docVm = docVm;
            _before = before?.Clone();
            _after = after?.Clone();
            Description = description;
        }

        public string Description { get; }

        /// <summary>Повтор: вернуть настройки после правки.</summary>
        public void Execute() => _docVm.RestoreHeaderFooter(_after?.Clone());

        /// <summary>Отмена: вернуть настройки до правки.</summary>
        public void Undo() => _docVm.RestoreHeaderFooter(_before?.Clone());
    }
}
