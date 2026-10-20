using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Models.Page
{
    /// <summary>
    /// Параметры раздела под кареткой для окон вкладки «Макет»: лист, колонки,
    /// выравнивание по высоте, рамка, номера строк и то, как раздел начинается. Копия:
    /// правка её полей документ не меняет — для этого есть команды вкладки.
    /// </summary>
    public sealed class SectionSetup
    {
        /// <summary>Лист и поля раздела.</summary>
        public TextEditorPageSettings Page { get; set; } = new();

        /// <summary>Колонки раздела.</summary>
        public SectionColumns Columns { get; set; } = new();

        public SectionVerticalAlignment VerticalAlignment { get; set; }

        /// <summary>Рамка страницы; null — рамки нет.</summary>
        public SectionPageBorders? PageBorders { get; set; }

        /// <summary>Номера строк; null — строки не нумеруются.</summary>
        public SectionLineNumbering? LineNumbering { get; set; }

        /// <summary>
        /// Как раздел начинается: вид разрыва раздела перед ним. Null — раздел первый в
        /// документе, разрыва перед ним нет.
        /// </summary>
        public BreakType? StartBreak { get; set; }
    }
}
