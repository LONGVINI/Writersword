using System;
using System.Windows.Input;
using ReactiveUI;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Page;

namespace Writersword.Modules.TextEditor.ViewModels.Toolbar
{
    /// <summary>
    /// ViewModel вкладки "Вставка".
    /// Содержит команды вставки таблиц, изображений, фигур, разрывов, ссылок.
    /// </summary>
    public sealed class RibbonInsertTabViewModel : ReactiveObject
    {
        private readonly ITextEditorCommandTarget _target;

        public ICommand InsertTableCommand                    { get; }
        public ICommand InsertImageCommand                    { get; }
        public ICommand InsertFloatingTextBoxCommand          { get; }
        public ICommand InsertShapeRectangleCommand           { get; }
        public ICommand InsertShapeEllipseCommand             { get; }
        public ICommand InsertShapeLineCommand                { get; }
        public ICommand InsertShapeArrowCommand               { get; }
        public ICommand InsertShapeCalloutCommand             { get; }
        public ICommand InsertPageBreakCommand                { get; }
        public ICommand InsertSectionBreakNextPageCommand     { get; }
        public ICommand InsertSectionBreakContinuousCommand   { get; }
        public ICommand InsertHyperlinkCommand                { get; }
        public ICommand InsertBookmarkCommand                 { get; }
        public ICommand InsertCommentCommand                  { get; }

        public RibbonInsertTabViewModel(ITextEditorCommandTarget target)
        {
            _target = target ?? throw new ArgumentNullException(nameof(target));

            // Таблица: вставляем 3x3 по умолчанию.
            InsertTableCommand = ReactiveCommand.Create(
                () => _target.InsertTable(3, 3));

            InsertImageCommand = ReactiveCommand.Create(
                () => _target.InsertImage(string.Empty));

            InsertFloatingTextBoxCommand = ReactiveCommand.Create(
                () => _target.InsertFloatingTextBox());

            InsertShapeRectangleCommand = ReactiveCommand.Create(
                () => _target.InsertShape(ShapeType.Rectangle));

            InsertShapeEllipseCommand = ReactiveCommand.Create(
                () => _target.InsertShape(ShapeType.Ellipse));

            InsertShapeLineCommand = ReactiveCommand.Create(
                () => _target.InsertShape(ShapeType.Line));

            InsertShapeArrowCommand = ReactiveCommand.Create(
                () => _target.InsertShape(ShapeType.Arrow));

            InsertShapeCalloutCommand = ReactiveCommand.Create(
                () => _target.InsertShape(ShapeType.Callout));

            InsertPageBreakCommand = ReactiveCommand.Create(
                () => _target.InsertPageBreak());

            InsertSectionBreakNextPageCommand = ReactiveCommand.Create(
                () => _target.InsertSectionBreak(BreakType.SectionNextPage));

            InsertSectionBreakContinuousCommand = ReactiveCommand.Create(
                () => _target.InsertSectionBreak(BreakType.SectionContinuous));

            InsertHyperlinkCommand = ReactiveCommand.Create(
                () => _target.InsertHyperlink(string.Empty, null));

            InsertBookmarkCommand = ReactiveCommand.Create(
                () => _target.InsertBookmark(string.Empty));

            InsertCommentCommand = ReactiveCommand.Create(
                () => _target.InsertComment(string.Empty));
        }
    }

    /// <summary>
    /// ViewModel вкладки "Макет".
    /// Содержит команды настройки страницы, колонок и разрывов.
    /// </summary>
    public sealed class RibbonLayoutTabViewModel : ReactiveObject
    {
        private readonly ITextEditorCommandTarget _target;

        public ICommand SetSizeA4Command                { get; }
        public ICommand SetSizeA3Command                { get; }
        public ICommand SetSizeA5Command                { get; }
        public ICommand SetSizeLetterCommand            { get; }
        public ICommand SetOrientationPortraitCommand   { get; }
        public ICommand SetOrientationLandscapeCommand  { get; }
        public ICommand SetMarginsCommand               { get; }
        public ICommand Set1ColumnCommand               { get; }
        public ICommand Set2ColumnsCommand              { get; }
        public ICommand Set3ColumnsCommand              { get; }
        public ICommand InsertPageBreakCommand          { get; }
        public ICommand InsertSectionBreakCommand       { get; }

        public RibbonLayoutTabViewModel(ITextEditorCommandTarget target)
        {
            _target = target ?? throw new ArgumentNullException(nameof(target));

            SetSizeA4Command = ReactiveCommand.Create(
                () => _target.SetPageSize(PaperSize.A4));

            SetSizeA3Command = ReactiveCommand.Create(
                () => _target.SetPageSize(PaperSize.A3));

            SetSizeA5Command = ReactiveCommand.Create(
                () => _target.SetPageSize(PaperSize.A5));

            SetSizeLetterCommand = ReactiveCommand.Create(
                () => _target.SetPageSize(PaperSize.Letter));

            SetOrientationPortraitCommand = ReactiveCommand.Create(
                () => _target.SetPageOrientation(PageOrientation.Portrait));

            SetOrientationLandscapeCommand = ReactiveCommand.Create(
                () => _target.SetPageOrientation(PageOrientation.Landscape));

            // Поля: стандартные 20 мм со всех сторон.
            SetMarginsCommand = ReactiveCommand.Create(
                () => _target.SetPageMargins(20, 20, 25, 15));

            Set1ColumnCommand = ReactiveCommand.Create(
                () => _target.SetColumns(1));

            Set2ColumnsCommand = ReactiveCommand.Create(
                () => _target.SetColumns(2));

            Set3ColumnsCommand = ReactiveCommand.Create(
                () => _target.SetColumns(3));

            InsertPageBreakCommand = ReactiveCommand.Create(
                () => _target.InsertPageBreak());

            InsertSectionBreakCommand = ReactiveCommand.Create(
                () => _target.InsertSectionBreak(BreakType.SectionNextPage));
        }
    }

    /// <summary>
    /// ViewModel вкладки "Ссылки".
    /// Содержит команды оглавления, сносок, проверки орфографии и экспорта.
    /// </summary>
    public sealed class RibbonReferencesTabViewModel : ReactiveObject
    {
        private readonly ITextEditorCommandTarget _target;

        public ICommand InsertTOCCommand       { get; }
        public ICommand UpdateTOCCommand       { get; }
        public ICommand InsertFootnoteCommand  { get; }
        public ICommand InsertEndnoteCommand   { get; }
        public ICommand RunSpellCheckCommand   { get; }
        public ICommand ShowWordCountCommand   { get; }
        public ICommand PrintCommand           { get; }
        public ICommand ExportPdfCommand       { get; }
        public ICommand ExportDocxCommand      { get; }
        public ICommand ExportTxtCommand       { get; }
        public ICommand ExportMarkdownCommand  { get; }

        public RibbonReferencesTabViewModel(ITextEditorCommandTarget target)
        {
            _target = target ?? throw new ArgumentNullException(nameof(target));

            InsertTOCCommand       = ReactiveCommand.Create(() => _target.InsertTOC());
            UpdateTOCCommand       = ReactiveCommand.Create(() => _target.InsertTOC());
            InsertFootnoteCommand  = ReactiveCommand.Create(() => _target.InsertFootnote());
            InsertEndnoteCommand   = ReactiveCommand.Create(() => _target.InsertEndnote());
            RunSpellCheckCommand   = ReactiveCommand.Create(() => _target.RunSpellCheck());
            ShowWordCountCommand   = ReactiveCommand.Create(() => _target.ShowWordCount());
            PrintCommand           = ReactiveCommand.Create(() => _target.Print());
            ExportPdfCommand       = ReactiveCommand.Create(() => _target.ExportToPdf());
            ExportDocxCommand      = ReactiveCommand.Create(() => _target.ExportToDocx());
            ExportTxtCommand       = ReactiveCommand.Create(() => _target.ExportToTxt());
            ExportMarkdownCommand  = ReactiveCommand.Create(() => _target.ExportToMarkdown());
        }
    }
}
