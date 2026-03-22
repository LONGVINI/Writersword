using System.Windows.Input;
using ReactiveUI;
using Writersword.Modules.TextEditor.Contracts;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.ViewModels.Toolbar
{
    public sealed class RibbonInsertTabViewModel : ReactiveObject
    {
        private readonly ITextEditorCommandTarget _target;

        private bool _isTableGroupExpanded = true;
        private bool _isMediaGroupExpanded = true;
        private bool _isPageGroupExpanded = true;
        private bool _isLinksGroupExpanded = true;

        private const double WidthTable = 100;
        private const double WidthMedia = 200;
        private const double WidthPage = 150;
        private const double WidthLinks = 200;
        private const double WidthSmall = 66;

        public bool IsTableGroupExpanded
        {
            get => _isTableGroupExpanded;
            set => this.RaiseAndSetIfChanged(ref _isTableGroupExpanded, value);
        }
        public bool IsMediaGroupExpanded
        {
            get => _isMediaGroupExpanded;
            set => this.RaiseAndSetIfChanged(ref _isMediaGroupExpanded, value);
        }
        public bool IsPageGroupExpanded
        {
            get => _isPageGroupExpanded;
            set => this.RaiseAndSetIfChanged(ref _isPageGroupExpanded, value);
        }
        public bool IsLinksGroupExpanded
        {
            get => _isLinksGroupExpanded;
            set => this.RaiseAndSetIfChanged(ref _isLinksGroupExpanded, value);
        }

        // Команды Insert остаются как заглушки — всё ещё дорабатывается.
        public ICommand InsertTableCommand { get; }
        public ICommand InsertImageCommand { get; }
        public ICommand InsertFloatingTextBoxCommand { get; }
        public ICommand InsertShapeRectangleCommand { get; }
        public ICommand InsertShapeEllipseCommand { get; }
        public ICommand InsertShapeLineCommand { get; }
        public ICommand InsertShapeArrowCommand { get; }
        public ICommand InsertShapeCalloutCommand { get; }
        public ICommand InsertPageBreakCommand { get; }
        public ICommand InsertSectionBreakNextPageCommand { get; }
        public ICommand InsertSectionBreakContinuousCommand { get; }
        public ICommand InsertHyperlinkCommand { get; }
        public ICommand InsertBookmarkCommand { get; }
        public ICommand InsertCommentCommand { get; }

        public RibbonInsertTabViewModel(ITextEditorCommandTarget target)
        {
            _target = target;

            // InsertTableCommand открывает пикер через code-behind RibbonInsertTab.
            // Реальная вставка идёт через InsertTableWithSize(rows, cols).
            InsertTableCommand = ReactiveCommand.Create(() => { });
            InsertImageCommand = ReactiveCommand.Create(() => { });
            InsertFloatingTextBoxCommand = ReactiveCommand.Create(() => { });
            InsertShapeRectangleCommand = ReactiveCommand.Create(() => { });
            InsertShapeEllipseCommand = ReactiveCommand.Create(() => { });
            InsertShapeLineCommand = ReactiveCommand.Create(() => { });
            InsertShapeArrowCommand = ReactiveCommand.Create(() => { });
            InsertShapeCalloutCommand = ReactiveCommand.Create(() => { });
            InsertPageBreakCommand = ReactiveCommand.Create(() => _target.InsertPageBreak());
            InsertSectionBreakNextPageCommand = ReactiveCommand.Create(
                () => _target.InsertSectionBreak(BreakType.SectionNextPage));
            InsertSectionBreakContinuousCommand = ReactiveCommand.Create(
                () => _target.InsertSectionBreak(BreakType.SectionContinuous));
            InsertHyperlinkCommand = ReactiveCommand.Create(() => { });
            InsertBookmarkCommand = ReactiveCommand.Create(() => { });
            InsertCommentCommand = ReactiveCommand.Create(() => { });
        }

        /// <summary>
        /// Вставляет таблицу заданного размера.
        /// Вызывается из code-behind RibbonInsertTab после выбора в TableGridPickerControl.
        /// </summary>
        public void InsertTableWithSize(int rows, int cols)
        {
            _target.InsertTable(rows, cols);
        }

        /// <summary>
        /// Порядок сворачивания: Ссылки → Страница → Медиа → Таблица.
        /// </summary>
        public void UpdateLayout(double availableWidth)
        {
            if (availableWidth >= 900)
            {
                IsTableGroupExpanded = true;
                IsMediaGroupExpanded = true;
                IsPageGroupExpanded = true;
                IsLinksGroupExpanded = true;
                return;
            }

            IsLinksGroupExpanded = false;

            if (availableWidth >= 720)
            {
                IsTableGroupExpanded = true;
                IsMediaGroupExpanded = true;
                IsPageGroupExpanded = true;
                return;
            }

            IsPageGroupExpanded = false;

            if (availableWidth >= 580)
            {
                IsTableGroupExpanded = true;
                IsMediaGroupExpanded = true;
                return;
            }

            IsMediaGroupExpanded = false;
            IsTableGroupExpanded = true;
        }
    }
}