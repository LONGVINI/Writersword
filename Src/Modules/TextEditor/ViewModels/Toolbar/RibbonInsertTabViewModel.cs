using System.Windows.Input;
using System;
using ReactiveUI;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Writersword.Modules.TextEditor.Contracts;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Resources;

namespace Writersword.Modules.TextEditor.ViewModels.Toolbar
{
    public sealed class RibbonInsertTabViewModel : ReactiveObject
    {
        private readonly ITextEditorCommandTarget _target;

        /// <summary>
        /// Вкладка колонтитулов: группа «Колонтитулы» на «Вставке» берёт её команды.
        /// Ставит RibbonViewModel сразу после создания вкладок.
        /// </summary>
        public RibbonHeaderFooterTabViewModel? HeaderFooter { get; set; }

        private bool _isFileGroupExpanded = true;
        private bool _isTableGroupExpanded = true;
        private bool _isMediaGroupExpanded = true;
        private bool _isPageGroupExpanded = true;
        private bool _isLinksGroupExpanded = true;
        private bool _isHeaderFooterGroupExpanded = true;

        private const double WidthTable = 100;
        private const double WidthMedia = 200;
        private const double WidthPage = 150;
        private const double WidthLinks = 200;
        private const double WidthSmall = 66;

        public bool IsFileGroupExpanded
        {
            get => _isFileGroupExpanded;
            set => this.RaiseAndSetIfChanged(ref _isFileGroupExpanded, value);
        }
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
        public bool IsHeaderFooterGroupExpanded
        {
            get => _isHeaderFooterGroupExpanded;
            set => this.RaiseAndSetIfChanged(ref _isHeaderFooterGroupExpanded, value);
        }

        /// <summary>Открывает файл документа и передаёт его редактору на импорт.</summary>
        public ICommand ImportDocumentCommand { get; }

        /// <summary>Экспорт документа в соответствующий формат.</summary>
        public ICommand ExportDocxCommand { get; }
        public ICommand ExportPdfCommand { get; }
        public ICommand ExportTxtCommand { get; }
        public ICommand ExportMarkdownCommand { get; }

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
        public ICommand InsertSectionBreakEvenPageCommand { get; }
        public ICommand InsertSectionBreakOddPageCommand { get; }
        public ICommand InsertColumnBreakCommand { get; }
        public ICommand InsertHyperlinkCommand { get; }
        public ICommand InsertBookmarkCommand { get; }
        public ICommand InsertCommentCommand { get; }
        public ICommand InsertFormulaCommand { get; }

        /// <summary>
        /// Новая диаграмма. Параметр — вид по имени (Column, Bar, Line, Area, Pie,
        /// Doughnut, Scatter); без параметра — гистограмма, как первый вид у Word.
        /// </summary>
        public ICommand InsertChartCommand { get; }

        /// <summary>Диаграмма по данным таблицы под кареткой: выделенные ячейки или вся таблица.</summary>
        public ICommand InsertChartFromTableCommand { get; }

        /// <summary>Готовая формула из меню кнопки «Формула»; параметр — запись LaTeX.</summary>
        public ICommand InsertReadyFormulaCommand { get; }
        public ICommand InsertDateTimeCommand { get; }

        public RibbonInsertTabViewModel(ITextEditorCommandTarget target)
        {
            _target = target;

            ImportDocumentCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                var window = (Avalonia.Application.Current?.ApplicationLifetime
                    as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                if (window?.StorageProvider is null) return;

                var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = TextEditorStrings.Import_Dialog_Title,
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType(TextEditorStrings.FileType_Import)
                        {
                            Patterns = new[] { "*.docx", "*.txt" }
                        },
                        new FilePickerFileType(TextEditorStrings.FileType_Docx)
                        {
                            Patterns = new[] { "*.docx" }
                        },
                        new FilePickerFileType(TextEditorStrings.FileType_Txt)
                        {
                            Patterns = new[] { "*.txt" }
                        }
                    }
                });

                if (files.Count == 0) return;
                var path = files[0].TryGetLocalPath();
                if (!string.IsNullOrEmpty(path))
                    _target.ImportFromFile(path);
            });

            // Диалог сохранения открывает сам редактор: он знает заголовок документа
            // и умеет достать картинки из проекта для встраивания в файл.
            ExportDocxCommand = ReactiveCommand.Create(() => _target.ExportToDocx());
            ExportPdfCommand = ReactiveCommand.Create(() => _target.ExportToPdf());
            ExportTxtCommand = ReactiveCommand.Create(() => _target.ExportToTxt());
            ExportMarkdownCommand = ReactiveCommand.Create(() => _target.ExportToMarkdown());

            // InsertTableCommand открывает пикер через code-behind RibbonInsertTab.
            // Реальная вставка идёт через InsertTableWithSize(rows, cols).
            InsertTableCommand = ReactiveCommand.Create(() => { });
            InsertImageCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                var window = (Avalonia.Application.Current?.ApplicationLifetime
                    as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                if (window?.StorageProvider is null) return;

                var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Вставить изображение",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("Изображения")
                        {
                            Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.webp" }
                        }
                    }
                });

                if (files.Count == 0) return;
                var path = files[0].TryGetLocalPath();
                if (!string.IsNullOrEmpty(path))
                    _target.InsertImage(path);
            });
            InsertFloatingTextBoxCommand = ReactiveCommand.Create(() => { });
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
            InsertFormulaCommand = ReactiveCommand.Create(() => _target.InsertFormula());
            InsertChartFromTableCommand = ReactiveCommand.Create(() => _target.InsertChartFromTable());
            InsertChartCommand = ReactiveCommand.Create<string?>(kind =>
            {
                if (kind == "Combo")
                    _target.InsertComboChart();
                else
                    _target.InsertChart(Enum.TryParse<ChartKind>(kind, out var parsed) ? parsed : ChartKind.Column);
            });
            InsertReadyFormulaCommand = ReactiveCommand.Create<string>(latex => _target.InsertReadyFormula(latex));
            InsertDateTimeCommand = ReactiveCommand.Create(() => _target.InsertDateTime());
            InsertPageBreakCommand = ReactiveCommand.Create(() => _target.InsertPageBreak());
            InsertSectionBreakNextPageCommand = ReactiveCommand.Create(
                () => _target.InsertSectionBreak(BreakType.SectionNextPage));
            InsertSectionBreakContinuousCommand = ReactiveCommand.Create(
                () => _target.InsertSectionBreak(BreakType.SectionContinuous));
            InsertSectionBreakEvenPageCommand = ReactiveCommand.Create(
                () => _target.InsertSectionBreak(BreakType.SectionEvenPage));
            InsertSectionBreakOddPageCommand = ReactiveCommand.Create(
                () => _target.InsertSectionBreak(BreakType.SectionOddPage));
            InsertColumnBreakCommand = ReactiveCommand.Create(() => _target.InsertColumnBreak());
            InsertHyperlinkCommand = ReactiveCommand.Create(() => { });
            InsertBookmarkCommand = ReactiveCommand.Create(() => { });
            InsertCommentCommand = ReactiveCommand.Create(() => _target.InsertComment(string.Empty));
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
        /// Порядок сворачивания: Ссылки → Колонтитулы → Страница → Медиа → Файл → Таблица.
        /// Пороги подняты на ширину группы «Файл», добавленной в начало вкладки, и на
        /// ширину группы «Колонтитулы» (в свёрнутом виде — одна кнопка).
        /// </summary>
        public void UpdateLayout(double availableWidth)
        {
            // Пороги групп, идущих после «Медиа», включают ширину кнопки «Формула» в ней.
            if (availableWidth >= 1292)
            {
                IsFileGroupExpanded = true;
                IsTableGroupExpanded = true;
                IsMediaGroupExpanded = true;
                IsPageGroupExpanded = true;
                IsHeaderFooterGroupExpanded = true;
                IsLinksGroupExpanded = true;
                return;
            }

            IsLinksGroupExpanded = false;

            if (availableWidth >= 1162)
            {
                IsFileGroupExpanded = true;
                IsTableGroupExpanded = true;
                IsMediaGroupExpanded = true;
                IsPageGroupExpanded = true;
                IsHeaderFooterGroupExpanded = true;
                return;
            }

            IsHeaderFooterGroupExpanded = false;

            if (availableWidth >= 992)
            {
                IsFileGroupExpanded = true;
                IsTableGroupExpanded = true;
                IsMediaGroupExpanded = true;
                IsPageGroupExpanded = true;
                return;
            }

            IsPageGroupExpanded = false;

            if (availableWidth >= 852)
            {
                IsFileGroupExpanded = true;
                IsTableGroupExpanded = true;
                IsMediaGroupExpanded = true;
                return;
            }

            IsMediaGroupExpanded = false;

            if (availableWidth >= 650)
            {
                IsFileGroupExpanded = true;
                IsTableGroupExpanded = true;
                return;
            }

            IsFileGroupExpanded = false;
            IsTableGroupExpanded = true;
        }
    }
}