using System;
using System.Windows.Input;
using ReactiveUI;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Page;

namespace Writersword.Modules.TextEditor.ViewModels.Toolbar
{
    /// <summary>
    /// ViewModel вкладки "Вставка" Ribbon.
    /// </summary>
    public sealed class RibbonInsertTabViewModel : ReactiveObject
    {
        private readonly ITextEditorCommandTarget _target;

        public ICommand InsertTableCommand { get; }
        public ICommand InsertImageCommand { get; }
        public ICommand InsertShapeRectangleCommand { get; }
        public ICommand InsertShapeEllipseCommand { get; }
        public ICommand InsertShapeLineCommand { get; }
        public ICommand InsertShapeArrowCommand { get; }
        public ICommand InsertShapeCalloutCommand { get; }
        public ICommand InsertFloatingTextBoxCommand { get; }
        public ICommand InsertPageBreakCommand { get; }
        public ICommand InsertSectionBreakNextPageCommand { get; }
        public ICommand InsertSectionBreakContinuousCommand { get; }
        public ICommand InsertFootnoteCommand { get; }
        public ICommand InsertEndnoteCommand { get; }
        public ICommand InsertBookmarkCommand { get; }
        public ICommand InsertHyperlinkCommand { get; }
        public ICommand InsertTOCCommand { get; }
        public ICommand InsertCommentCommand { get; }

        public RibbonInsertTabViewModel(ITextEditorCommandTarget target)
        {
            _target = target ?? throw new ArgumentNullException(nameof(target));

            InsertTableCommand              = ReactiveCommand.Create(() => _target.InsertTable(3, 3));
            InsertImageCommand              = ReactiveCommand.Create(() => _target.InsertImage(string.Empty));
            InsertShapeRectangleCommand     = ReactiveCommand.Create(() => _target.InsertShape(ShapeType.Rectangle));
            InsertShapeEllipseCommand       = ReactiveCommand.Create(() => _target.InsertShape(ShapeType.Ellipse));
            InsertShapeLineCommand          = ReactiveCommand.Create(() => _target.InsertShape(ShapeType.Line));
            InsertShapeArrowCommand         = ReactiveCommand.Create(() => _target.InsertShape(ShapeType.Arrow));
            InsertShapeCalloutCommand       = ReactiveCommand.Create(() => _target.InsertShape(ShapeType.Callout));
            InsertFloatingTextBoxCommand    = ReactiveCommand.Create(() => _target.InsertFloatingTextBox());
            InsertPageBreakCommand          = ReactiveCommand.Create(() => _target.InsertPageBreak());
            InsertSectionBreakNextPageCommand =
                ReactiveCommand.Create(() => _target.InsertSectionBreak(BreakType.SectionNextPage));
            InsertSectionBreakContinuousCommand =
                ReactiveCommand.Create(() => _target.InsertSectionBreak(BreakType.SectionContinuous));
            InsertFootnoteCommand   = ReactiveCommand.Create(() => _target.InsertFootnote());
            InsertEndnoteCommand    = ReactiveCommand.Create(() => _target.InsertEndnote());
            InsertBookmarkCommand   = ReactiveCommand.Create(() => _target.InsertBookmark(string.Empty));
            InsertHyperlinkCommand  = ReactiveCommand.Create(() => _target.InsertHyperlink(string.Empty, null));
            InsertTOCCommand        = ReactiveCommand.Create(() => _target.InsertTOC());
            InsertCommentCommand    = ReactiveCommand.Create(() => _target.InsertComment(string.Empty));
        }
    }

    /// <summary>
    /// ViewModel вкладки "Макет" Ribbon.
    /// </summary>
    public sealed class RibbonLayoutTabViewModel : ReactiveObject
    {
        private readonly ITextEditorCommandTarget _target;

        private PaperSize _currentPaperSize = PaperSize.A4;
        private PageOrientation _currentOrientation = PageOrientation.Portrait;
        private int _currentColumnCount = 1;

        public PaperSize CurrentPaperSize
        {
            get => _currentPaperSize;
            set => this.RaiseAndSetIfChanged(ref _currentPaperSize, value);
        }

        public PageOrientation CurrentOrientation
        {
            get => _currentOrientation;
            set => this.RaiseAndSetIfChanged(ref _currentOrientation, value);
        }

        public int CurrentColumnCount
        {
            get => _currentColumnCount;
            set => this.RaiseAndSetIfChanged(ref _currentColumnCount, value);
        }

        public ICommand SetSizeA4Command { get; }
        public ICommand SetSizeA3Command { get; }
        public ICommand SetSizeA5Command { get; }
        public ICommand SetSizeLetterCommand { get; }

        public ICommand SetOrientationPortraitCommand { get; }
        public ICommand SetOrientationLandscapeCommand { get; }

        public ICommand SetMarginsCommand { get; }
        public ICommand Set1ColumnCommand { get; }
        public ICommand Set2ColumnsCommand { get; }
        public ICommand Set3ColumnsCommand { get; }

        public ICommand InsertPageBreakCommand { get; }
        public ICommand InsertSectionBreakCommand { get; }

        public RibbonLayoutTabViewModel(ITextEditorCommandTarget target)
        {
            _target = target ?? throw new ArgumentNullException(nameof(target));

            SetSizeA4Command     = ReactiveCommand.Create(() => _target.SetPageSize(PaperSize.A4));
            SetSizeA3Command     = ReactiveCommand.Create(() => _target.SetPageSize(PaperSize.A3));
            SetSizeA5Command     = ReactiveCommand.Create(() => _target.SetPageSize(PaperSize.A5));
            SetSizeLetterCommand = ReactiveCommand.Create(() => _target.SetPageSize(PaperSize.Letter));

            SetOrientationPortraitCommand  =
                ReactiveCommand.Create(() => _target.SetPageOrientation(PageOrientation.Portrait));
            SetOrientationLandscapeCommand =
                ReactiveCommand.Create(() => _target.SetPageOrientation(PageOrientation.Landscape));

            SetMarginsCommand = ReactiveCommand.Create(() =>
                _target.SetPageMargins(25, 25, 30, 15));

            Set1ColumnCommand = ReactiveCommand.Create(() => _target.SetColumns(1));
            Set2ColumnsCommand = ReactiveCommand.Create(() => _target.SetColumns(2));
            Set3ColumnsCommand = ReactiveCommand.Create(() => _target.SetColumns(3));

            InsertPageBreakCommand    = ReactiveCommand.Create(() => _target.InsertPageBreak());
            InsertSectionBreakCommand =
                ReactiveCommand.Create(() => _target.InsertSectionBreak(BreakType.SectionNextPage));
        }
    }

    /// <summary>
    /// ViewModel вкладки "Ссылки" Ribbon.
    /// </summary>
    public sealed class RibbonReferencesTabViewModel : ReactiveObject
    {
        private readonly ITextEditorCommandTarget _target;

        public ICommand InsertTOCCommand { get; }
        public ICommand UpdateTOCCommand { get; }
        public ICommand InsertFootnoteCommand { get; }
        public ICommand InsertEndnoteCommand { get; }
        public ICommand InsertBookmarkCommand { get; }
        public ICommand InsertHyperlinkCommand { get; }
        public ICommand InsertCommentCommand { get; }
        public ICommand RunSpellCheckCommand { get; }
        public ICommand ShowWordCountCommand { get; }
        public ICommand PrintCommand { get; }
        public ICommand ExportPdfCommand { get; }
        public ICommand ExportDocxCommand { get; }
        public ICommand ExportTxtCommand { get; }
        public ICommand ExportMarkdownCommand { get; }

        public RibbonReferencesTabViewModel(ITextEditorCommandTarget target)
        {
            _target = target ?? throw new ArgumentNullException(nameof(target));

            InsertTOCCommand       = ReactiveCommand.Create(() => _target.InsertTOC());
            UpdateTOCCommand       = ReactiveCommand.Create(() => { /* обновление TOC — TODO */ });
            InsertFootnoteCommand  = ReactiveCommand.Create(() => _target.InsertFootnote());
            InsertEndnoteCommand   = ReactiveCommand.Create(() => _target.InsertEndnote());
            InsertBookmarkCommand  = ReactiveCommand.Create(() => _target.InsertBookmark(string.Empty));
            InsertHyperlinkCommand = ReactiveCommand.Create(() => _target.InsertHyperlink(string.Empty, null));
            InsertCommentCommand   = ReactiveCommand.Create(() => _target.InsertComment(string.Empty));

            RunSpellCheckCommand   = ReactiveCommand.Create(() => _target.RunSpellCheck());
            ShowWordCountCommand   = ReactiveCommand.Create(() => _target.ShowWordCount());

            PrintCommand          = ReactiveCommand.Create(() => _target.Print());
            ExportPdfCommand      = ReactiveCommand.Create(() => _target.ExportToPdf());
            ExportDocxCommand     = ReactiveCommand.Create(() => _target.ExportToDocx());
            ExportTxtCommand      = ReactiveCommand.Create(() => _target.ExportToTxt());
            ExportMarkdownCommand = ReactiveCommand.Create(() => _target.ExportToMarkdown());
        }
    }
}
