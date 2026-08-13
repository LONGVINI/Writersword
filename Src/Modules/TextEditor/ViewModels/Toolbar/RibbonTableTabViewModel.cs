using System;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using ReactiveUI;
using Writersword.Modules.TextEditor.Contracts;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Views.Dialogs;

namespace Writersword.Modules.TextEditor.ViewModels.Toolbar
{
    /// <summary>
    /// ViewModel контекстной вкладки «Таблица» в Ribbon.
    /// Появляется только когда каретка находится внутри таблицы.
    /// </summary>
    public sealed class RibbonTableTabViewModel : ReactiveObject
    {
        private readonly ITextEditorCommandTarget _target;

        private bool _isByCell;

        /// <summary>
        /// True — режим разбивки ByCell, False — ByRow.
        /// Биндится к тексту/иконке кнопки-тоггла в XAML.
        /// </summary>
        public bool IsByCell
        {
            get => _isByCell;
            private set => this.RaiseAndSetIfChanged(ref _isByCell, value);
        }

        private bool _isRepeatHeader;

        /// <summary>
        /// True — первая строка таблицы повторяется на каждой странице. Кнопка-тоггл
        /// в ленте показывает это состояние: раньше по её виду нельзя было понять,
        /// включён повтор или нет.
        /// </summary>
        public bool IsRepeatHeader
        {
            get => _isRepeatHeader;
            private set => this.RaiseAndSetIfChanged(ref _isRepeatHeader, value);
        }

        // ── Перо границ ───────────────────────────────────────────────────
        // Цвет, толщина и стиль линии задаются отдельно от того, к каким сторонам
        // их применяют: сначала настраивается перо, потом жмётся нужная сторона.
        // Так же устроена работа с границами в Word.

        private string _borderColorPick = "#000000";
        private decimal _borderThicknessPt = 0.5m;
        private BorderStyle _borderLineStyle = BorderStyle.Single;

        /// <summary>Цвет линии в HEX. Двусторонняя привязка к ColorPickerButton.</summary>
        public string BorderColorPick
        {
            get => _borderColorPick;
            set => this.RaiseAndSetIfChanged(ref _borderColorPick, value);
        }

        /// <summary>
        /// Толщина линии в пунктах. Тип decimal — этого требует NumericUpDown,
        /// к которому свойство привязано; в команду уходит уже double.
        /// </summary>
        public decimal BorderThicknessPt
        {
            get => _borderThicknessPt;
            set
            {
                this.RaiseAndSetIfChanged(ref _borderThicknessPt, Math.Clamp(value, 0.25m, 6m));
                this.RaisePropertyChanged(nameof(BorderSampleThickness));
            }
        }

        /// <summary>
        /// Толщина линии в образце, пиксели. Образец обязан показывать реальную
        /// настройку: при 0,5pt он должен быть волосяным, как в таблице, а не жирным
        /// бруском. Ниже одного пикселя не опускаемся — иначе линия исчезнет.
        /// </summary>
        public double BorderSampleThickness =>
            Math.Clamp((double)_borderThicknessPt * 1.6, 1.0, 8.0);

        /// <summary>Стиль линии. Меняется командами SetBorderStyle*.</summary>
        public BorderStyle BorderLineStyle
        {
            get => _borderLineStyle;
            private set
            {
                this.RaiseAndSetIfChanged(ref _borderLineStyle, value);
                this.RaisePropertyChanged(nameof(BorderStyleName));
                this.RaisePropertyChanged(nameof(IsBorderSingle));
                this.RaisePropertyChanged(nameof(IsBorderDouble));
                this.RaisePropertyChanged(nameof(IsBorderDashed));
                this.RaisePropertyChanged(nameof(IsBorderDotted));
                this.RaisePropertyChanged(nameof(IsBorderThick));
            }
        }

        /// <summary>Название текущего стиля линии — используется в пунктах меню.</summary>
        public string BorderStyleName => _borderLineStyle switch
        {
            BorderStyle.None => "Нет",
            BorderStyle.Double => "Двойная",
            BorderStyle.Dashed => "Пунктир",
            BorderStyle.Dotted => "Точки",
            BorderStyle.Thick => "Жирная",
            _ => "Сплошная"
        };

        // Признаки текущего стиля. Кнопка в ленте показывает не обрезанное название,
        // а образец самой линии — по нему стиль читается без чтения текста.
        public bool IsBorderSingle => _borderLineStyle == BorderStyle.Single;
        public bool IsBorderDouble => _borderLineStyle == BorderStyle.Double;
        public bool IsBorderDashed => _borderLineStyle == BorderStyle.Dashed;
        public bool IsBorderDotted => _borderLineStyle == BorderStyle.Dotted;
        public bool IsBorderThick => _borderLineStyle == BorderStyle.Thick;

        // ── Свой узор линии ───────────────────────────────────────────────

        private string _customDashPattern = "6,3";

        /// <summary>
        /// Узор пользовательской линии: чередование длин штриха и пробела в единицах
        /// толщины, как в StrokeDashArray. Пока живёт только в пере и в образце:
        /// модель хранит стиль перечислением BorderStyle, произвольный узор туда
        /// не положить — это отдельная правка модели, сериализатора и отрисовщика.
        /// </summary>
        public string CustomDashPattern
        {
            get => _customDashPattern;
            private set
            {
                this.RaiseAndSetIfChanged(ref _customDashPattern, value);
                this.RaisePropertyChanged(nameof(CustomDashArray));
            }
        }

        /// <summary>Узор в виде, пригодном для StrokeDashArray.</summary>
        public Avalonia.Collections.AvaloniaList<double> CustomDashArray
        {
            get
            {
                var list = new Avalonia.Collections.AvaloniaList<double>();
                foreach (var part in _customDashPattern.Split(',', ';', ' '))
                {
                    if (double.TryParse(part.Trim(),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out double v) && v > 0)
                    {
                        list.Add(v);
                    }
                }
                if (list.Count == 0) { list.Add(6); list.Add(3); }
                return list;
            }
        }

        // ── Поля ячейки ───────────────────────────────────────────────────
        // Внутренние отступы от рамки до текста. Именно они дают тот воздух
        // сверху и снизу, который не объясняется метриками шрифта.

        private decimal _padTopPt = 4m;
        private decimal _padBottomPt = 4m;
        private decimal _padLeftPt = 6m;
        private decimal _padRightPt = 6m;

        // Подстановка значений из ячейки не должна тут же писать их обратно.
        private bool _syncingPadding;

        public decimal PadTopPt
        {
            get => _padTopPt;
            set { if (SetPad(ref _padTopPt, value)) this.RaisePropertyChanged(nameof(PadTopPt)); }
        }

        public decimal PadBottomPt
        {
            get => _padBottomPt;
            set { if (SetPad(ref _padBottomPt, value)) this.RaisePropertyChanged(nameof(PadBottomPt)); }
        }

        public decimal PadLeftPt
        {
            get => _padLeftPt;
            set { if (SetPad(ref _padLeftPt, value)) this.RaisePropertyChanged(nameof(PadLeftPt)); }
        }

        public decimal PadRightPt
        {
            get => _padRightPt;
            set { if (SetPad(ref _padRightPt, value)) this.RaisePropertyChanged(nameof(PadRightPt)); }
        }

        // Возвращает true всегда: уведомление нужно и когда значение обрезано по
        // диапазону, иначе поле останется с недопустимым числом.
        private bool SetPad(ref decimal field, decimal value)
        {
            decimal clamped = Math.Clamp(value, 0m, 100m);
            if (field != clamped)
            {
                field = clamped;
                if (!_syncingPadding)
                    _target.TableSetCellPadding(
                        (double)_padTopPt, (double)_padBottomPt,
                        (double)_padLeftPt, (double)_padRightPt);
            }
            return true;
        }

        private void RefreshPaddingState()
        {
            var pad = _target.TableGetCellPadding();
            if (pad is null) return;

            _syncingPadding = true;
            try
            {
                PadTopPt = (decimal)pad.Value.TopPt;
                PadBottomPt = (decimal)pad.Value.BottomPt;
                PadLeftPt = (decimal)pad.Value.LeftPt;
                PadRightPt = (decimal)pad.Value.RightPt;
            }
            finally
            {
                _syncingPadding = false;
            }
        }

        // ── Инструменты границ ────────────────────────────────────────────
        // Карандаш и ластик — взаимоисключающие режимы. Повторное нажатие на
        // активную кнопку выключает режим, поэтому это не радиогруппа.

        private int _lineTool;

        public bool IsPencilTool => _lineTool == 1;
        public bool IsEraserTool => _lineTool == 2;

        public ICommand TogglePencilToolCommand { get; }
        public ICommand ToggleEraserToolCommand { get; }

        private void SetLineTool(int tool)
        {
            _lineTool = _lineTool == tool ? 0 : tool;
            _target.TableSetLineTool(_lineTool);
            this.RaisePropertyChanged(nameof(IsPencilTool));
            this.RaisePropertyChanged(nameof(IsEraserTool));
        }

        // ── Активное выравнивание ─────────────────────────────────────────
        // Одна из девяти кнопок подсвечена всегда, пока каретка в таблице.
        // Исключение — выделение нескольких ячеек с разным выравниванием: тогда
        // значение неизвестно, и активной кнопки нет.

        private int? _cellVAlign;
        private Models.Styles.TextAlignment? _cellHAlign;

        private bool IsAlign(int vAlign, Models.Styles.TextAlignment hAlign)
            => _cellVAlign == vAlign && _cellHAlign == hAlign;

        public bool IsAlignTopLeft      => IsAlign(0, Models.Styles.TextAlignment.Left);
        public bool IsAlignTopCenter    => IsAlign(0, Models.Styles.TextAlignment.Center);
        public bool IsAlignTopRight     => IsAlign(0, Models.Styles.TextAlignment.Right);
        public bool IsAlignMiddleLeft   => IsAlign(1, Models.Styles.TextAlignment.Left);
        public bool IsAlignMiddleCenter => IsAlign(1, Models.Styles.TextAlignment.Center);
        public bool IsAlignMiddleRight  => IsAlign(1, Models.Styles.TextAlignment.Right);
        public bool IsAlignBottomLeft   => IsAlign(2, Models.Styles.TextAlignment.Left);
        public bool IsAlignBottomCenter => IsAlign(2, Models.Styles.TextAlignment.Center);
        public bool IsAlignBottomRight  => IsAlign(2, Models.Styles.TextAlignment.Right);

        // Четвёртый столбец сетки. Без него выравнивание по ширине оставалось
        // невидимым: задать его в ячейке было нечем, а унаследованное от стиля
        // не подсвечивалось ни одной кнопкой.
        public bool IsAlignTopJustify    => IsAlign(0, Models.Styles.TextAlignment.Justify);
        public bool IsAlignMiddleJustify => IsAlign(1, Models.Styles.TextAlignment.Justify);
        public bool IsAlignBottomJustify => IsAlign(2, Models.Styles.TextAlignment.Justify);

        /// <summary>
        /// Перечитывает выравнивание целевых ячеек и обновляет подсветку.
        /// Выравнивание «по ширине» к сетке из трёх столбцов не сводится —
        /// в этом случае активной кнопки тоже нет.
        /// </summary>
        private void RefreshAlignState()
        {
            _cellVAlign = _target.TableGetCellVAlign();
            _cellHAlign = _target.TableGetCellHAlign();

            this.RaisePropertyChanged(nameof(IsAlignTopLeft));
            this.RaisePropertyChanged(nameof(IsAlignTopCenter));
            this.RaisePropertyChanged(nameof(IsAlignTopRight));
            this.RaisePropertyChanged(nameof(IsAlignMiddleLeft));
            this.RaisePropertyChanged(nameof(IsAlignMiddleCenter));
            this.RaisePropertyChanged(nameof(IsAlignMiddleRight));
            this.RaisePropertyChanged(nameof(IsAlignBottomLeft));
            this.RaisePropertyChanged(nameof(IsAlignBottomCenter));
            this.RaisePropertyChanged(nameof(IsAlignBottomRight));
            this.RaisePropertyChanged(nameof(IsAlignTopJustify));
            this.RaisePropertyChanged(nameof(IsAlignMiddleJustify));
            this.RaisePropertyChanged(nameof(IsAlignBottomJustify));
        }

        // ── Заливка ячейки ────────────────────────────────────────────────

        private string _cellColorPick = "#BDD7EE";

        /// <summary>
        /// Произвольный цвет заливки ячейки в HEX. Двусторонняя привязка к
        /// ColorPickerButton: выбор цвета сразу применяется к выделенным ячейкам.
        /// Шесть кнопок-образцов рядом остаются как быстрый доступ к частым цветам.
        /// </summary>
        public string CellColorPick
        {
            get => _cellColorPick;
            set
            {
                if (string.Equals(_cellColorPick, value, StringComparison.OrdinalIgnoreCase)) return;
                this.RaiseAndSetIfChanged(ref _cellColorPick, value);
                if (!string.IsNullOrWhiteSpace(value)) _target.TableSetCellBackground(value);
            }
        }

        // ── Размеры ячейки ────────────────────────────────────────────────

        private decimal _columnWidthMm = 40m;
        private decimal _rowHeightPt = 20m;

        /// <summary>
        /// Ширина столбца под кареткой, мм. Применяется сразу при изменении — так же,
        /// как размеры картинки на вкладке «Формат». Отдельной кнопки применения нет.
        /// </summary>
        public decimal ColumnWidthMm
        {
            get => _columnWidthMm;
            set
            {
                decimal clamped = Math.Clamp(value, 5m, 400m);
                if (_columnWidthMm != clamped)
                {
                    _columnWidthMm = clamped;
                    _target.TableSetColumnWidth((double)clamped);
                }
                // Уведомляем всегда: если введено значение вне диапазона, контрол
                // должен вернуться к обрезанному.
                this.RaisePropertyChanged(nameof(ColumnWidthMm));
            }
        }

        /// <summary>Высота строки под кареткой, пункты. Применяется сразу при изменении.</summary>
        public decimal RowHeightPt
        {
            get => _rowHeightPt;
            set
            {
                decimal clamped = Math.Clamp(value, 6m, 600m);
                if (_rowHeightPt != clamped)
                {
                    _rowHeightPt = clamped;
                    _target.TableSetRowHeight((double)clamped);
                }
                this.RaisePropertyChanged(nameof(RowHeightPt));
            }
        }

        // ── Строки ────────────────────────────────────────────────────────
        public ICommand AddRowAboveCommand { get; }
        public ICommand AddRowBelowCommand { get; }
        public ICommand DeleteRowCommand { get; }
        public ICommand DistributeRowsCommand { get; }

        // ── Столбцы ──────────────────────────────────────────────────────
        public ICommand AddColumnLeftCommand { get; }
        public ICommand AddColumnRightCommand { get; }
        public ICommand DeleteColumnCommand { get; }
        public ICommand DistributeColumnsCommand { get; }

        // ── Таблица целиком ───────────────────────────────────────────────
        public ICommand DeleteTableCommand { get; }
        public ICommand AutoFitCommand { get; }

        // ── Объединение / разбиение ───────────────────────────────────────
        public ICommand MergeCellsCommand { get; }
        public ICommand SplitCellCommand { get; }

        // ── Выравнивание текста ───────────────────────────────────────────
        public ICommand AlignTopLeftCommand { get; }
        public ICommand AlignTopCenterCommand { get; }
        public ICommand AlignTopRightCommand { get; }
        public ICommand AlignMiddleLeftCommand { get; }
        public ICommand AlignMiddleCenterCommand { get; }
        public ICommand AlignMiddleRightCommand { get; }
        public ICommand AlignBottomLeftCommand { get; }
        public ICommand AlignBottomCenterCommand { get; }
        public ICommand AlignBottomRightCommand { get; }
        public ICommand AlignTopJustifyCommand { get; }
        public ICommand AlignMiddleJustifyCommand { get; }
        public ICommand AlignBottomJustifyCommand { get; }

        // Раздельное выравнивание. Девять комбинированных кнопок требовали задавать
        // обе оси разом и не читались на размере иконки ленты; эти шесть меняют
        // только свою ось, вторая остаётся какой была. Комбинированные оставлены —
        // они ничего не ломают и могут пригодиться в контекстном меню.
        public ICommand AlignTopCommand { get; }
        public ICommand AlignMiddleCommand { get; }
        public ICommand AlignBottomCommand { get; }
        public ICommand AlignLeftCommand { get; }
        public ICommand AlignCenterCommand { get; }
        public ICommand AlignRightCommand { get; }

        // ── Заливка ячейки ────────────────────────────────────────────────
        public ICommand SetCellBackgroundNoneCommand { get; }
        public ICommand SetCellBackgroundBlueCommand { get; }
        public ICommand SetCellBackgroundGreenCommand { get; }
        public ICommand SetCellBackgroundYellowCommand { get; }
        public ICommand SetCellBackgroundRedCommand { get; }
        public ICommand SetCellBackgroundGrayCommand { get; }

        // ── Границы ───────────────────────────────────────────────────────
        public ICommand BorderAllCommand { get; }
        public ICommand BorderNoneCommand { get; }
        public ICommand BorderOuterCommand { get; }
        public ICommand BorderInnerCommand { get; }
        public ICommand BorderTopCommand { get; }
        public ICommand BorderBottomCommand { get; }
        public ICommand BorderLeftCommand { get; }
        public ICommand BorderRightCommand { get; }

        // ── Стиль линии границ ────────────────────────────────────────────
        public ICommand SetBorderStyleSingleCommand { get; }
        public ICommand SetBorderStyleDoubleCommand { get; }
        public ICommand SetBorderStyleDashedCommand { get; }
        public ICommand SetBorderStyleDottedCommand { get; }
        public ICommand SetBorderStyleThickCommand { get; }

        /// <summary>Открывает ввод собственного узора линии.</summary>
        public ICommand EditCustomLinePatternCommand { get; }


        // ── Сортировка ────────────────────────────────────────────────────
        public ICommand SortAscCommand { get; }
        public ICommand SortDescCommand { get; }

        // ── Заголовок ─────────────────────────────────────────────────────
        public ICommand RepeatHeaderCommand { get; }

        // ── Режим разбивки ────────────────────────────────────────────────
        public ICommand ToggleSplitModeCommand { get; }

        // ── Метки продолжения ─────────────────────────────────────────────
        public ICommand SetBreakLabelCommand { get; }
        public ICommand SetContinuationLabelCommand { get; }

        public RibbonTableTabViewModel(ITextEditorCommandTarget target)
        {
            _target = target;

            // Строки
            AddRowAboveCommand = ReactiveCommand.Create(() => _target.TableAddRow(above: true));
            AddRowBelowCommand = ReactiveCommand.Create(() => _target.TableAddRow(above: false));
            DeleteRowCommand = ReactiveCommand.Create(() => _target.TableDeleteRow());
            DistributeRowsCommand = ReactiveCommand.Create(() => _target.TableDistributeRows());

            // Столбцы
            AddColumnLeftCommand = ReactiveCommand.Create(() => _target.TableAddColumn(left: true));
            AddColumnRightCommand = ReactiveCommand.Create(() => _target.TableAddColumn(left: false));
            DeleteColumnCommand = ReactiveCommand.Create(() => _target.TableDeleteColumn());
            DistributeColumnsCommand = ReactiveCommand.Create(() => _target.TableDistributeColumns());

            // Таблица целиком
            DeleteTableCommand = ReactiveCommand.Create(() => _target.TableDelete());
            AutoFitCommand = ReactiveCommand.Create(() => _target.TableAutoFit());

            // Объединение / разбиение
            MergeCellsCommand = ReactiveCommand.Create(() => _target.TableMergeCells());
            SplitCellCommand = ReactiveCommand.Create(() => _target.TableSplitCell());

            // Выравнивание — vAlign 0=Top 1=Middle 2=Bottom.
            // После применения состояние перечитывается: подсветка обязана
            // перейти на нажатую кнопку сразу, а не на следующем входе в ячейку.
            AlignTopLeftCommand = ReactiveCommand.Create(() => ApplyAlign(0, Models.Styles.TextAlignment.Left));
            AlignTopCenterCommand = ReactiveCommand.Create(() => ApplyAlign(0, Models.Styles.TextAlignment.Center));
            AlignTopRightCommand = ReactiveCommand.Create(() => ApplyAlign(0, Models.Styles.TextAlignment.Right));
            AlignMiddleLeftCommand = ReactiveCommand.Create(() => ApplyAlign(1, Models.Styles.TextAlignment.Left));
            AlignMiddleCenterCommand = ReactiveCommand.Create(() => ApplyAlign(1, Models.Styles.TextAlignment.Center));
            AlignMiddleRightCommand = ReactiveCommand.Create(() => ApplyAlign(1, Models.Styles.TextAlignment.Right));
            AlignBottomLeftCommand = ReactiveCommand.Create(() => ApplyAlign(2, Models.Styles.TextAlignment.Left));
            AlignBottomCenterCommand = ReactiveCommand.Create(() => ApplyAlign(2, Models.Styles.TextAlignment.Center));
            AlignBottomRightCommand = ReactiveCommand.Create(() => ApplyAlign(2, Models.Styles.TextAlignment.Right));
            AlignTopJustifyCommand = ReactiveCommand.Create(() => ApplyAlign(0, Models.Styles.TextAlignment.Justify));
            AlignMiddleJustifyCommand = ReactiveCommand.Create(() => ApplyAlign(1, Models.Styles.TextAlignment.Justify));
            AlignBottomJustifyCommand = ReactiveCommand.Create(() => ApplyAlign(2, Models.Styles.TextAlignment.Justify));

            // Раздельное выравнивание по осям
            AlignTopCommand = ReactiveCommand.Create(() => { _target.TableSetCellVAlign(0); RefreshAlignState(); });
            AlignMiddleCommand = ReactiveCommand.Create(() => { _target.TableSetCellVAlign(1); RefreshAlignState(); });
            AlignBottomCommand = ReactiveCommand.Create(() => { _target.TableSetCellVAlign(2); RefreshAlignState(); });
            AlignLeftCommand = ReactiveCommand.Create(() => { _target.TableSetCellHAlign(Models.Styles.TextAlignment.Left); RefreshAlignState(); });
            AlignCenterCommand = ReactiveCommand.Create(() => { _target.TableSetCellHAlign(Models.Styles.TextAlignment.Center); RefreshAlignState(); });
            AlignRightCommand = ReactiveCommand.Create(() => { _target.TableSetCellHAlign(Models.Styles.TextAlignment.Right); RefreshAlignState(); });

            // Заливка
            SetCellBackgroundNoneCommand = ReactiveCommand.Create(() => _target.TableSetCellBackground(null));
            SetCellBackgroundBlueCommand = ReactiveCommand.Create(() => _target.TableSetCellBackground("#BDD7EE"));
            SetCellBackgroundGreenCommand = ReactiveCommand.Create(() => _target.TableSetCellBackground("#C6EFCE"));
            SetCellBackgroundYellowCommand = ReactiveCommand.Create(() => _target.TableSetCellBackground("#FFEB9C"));
            SetCellBackgroundRedCommand = ReactiveCommand.Create(() => _target.TableSetCellBackground("#FFC7CE"));
            SetCellBackgroundGrayCommand = ReactiveCommand.Create(() => _target.TableSetCellBackground("#D9D9D9"));

            // Границы — сторона берётся из кнопки, а стиль, толщина и цвет из пера,
            // настроенного рядом. Раньше все линии рисовались чёрными 0.5pt сплошными.
            BorderAllCommand = ReactiveCommand.Create(() => { ApplyBorder("outer"); ApplyBorder("inner"); });
            BorderNoneCommand = ReactiveCommand.Create(() => _target.TableSetCellBorder("all", BorderStyle.None, 0, null));
            BorderOuterCommand = ReactiveCommand.Create(() => ApplyBorder("outer"));
            BorderInnerCommand = ReactiveCommand.Create(() => ApplyBorder("inner"));
            BorderTopCommand = ReactiveCommand.Create(() => ApplyBorder("top"));
            BorderBottomCommand = ReactiveCommand.Create(() => ApplyBorder("bottom"));
            BorderLeftCommand = ReactiveCommand.Create(() => ApplyBorder("left"));
            BorderRightCommand = ReactiveCommand.Create(() => ApplyBorder("right"));

            // Стиль линии
            SetBorderStyleSingleCommand = ReactiveCommand.Create(() => BorderLineStyle = BorderStyle.Single);
            SetBorderStyleDoubleCommand = ReactiveCommand.Create(() => BorderLineStyle = BorderStyle.Double);
            SetBorderStyleDashedCommand = ReactiveCommand.Create(() => BorderLineStyle = BorderStyle.Dashed);
            SetBorderStyleDottedCommand = ReactiveCommand.Create(() => BorderLineStyle = BorderStyle.Dotted);
            SetBorderStyleThickCommand = ReactiveCommand.Create(() => BorderLineStyle = BorderStyle.Thick);
            EditCustomLinePatternCommand = ReactiveCommand.CreateFromTask(EditCustomLinePatternAsync);

            // Инструменты границ
            TogglePencilToolCommand = ReactiveCommand.Create(() => SetLineTool(1));
            ToggleEraserToolCommand = ReactiveCommand.Create(() => SetLineTool(2));

            // Сортировка
            SortAscCommand = ReactiveCommand.Create(() => _target.TableSort(-1, ascending: true));
            SortDescCommand = ReactiveCommand.Create(() => _target.TableSort(-1, ascending: false));

            // Заголовок — тоггл с обновлением состояния для подсветки кнопки
            RepeatHeaderCommand = ReactiveCommand.Create(() =>
            {
                _target.TableToggleRepeatHeader();
                IsRepeatHeader = _target.TableGetRepeatHeader();
            });

            // Режим разбивки — тоггл с обновлением реактивного свойства IsByCell
            ToggleSplitModeCommand = ReactiveCommand.Create(() =>
            {
                _target.TableToggleSplitMode();
                IsByCell = _target.TableGetSplitModeByCell();
            });

            // Метки разрыва и продолжения с диалогом ввода
            SetBreakLabelCommand = ReactiveCommand.CreateFromTask(SetBreakLabelAsync);
            SetContinuationLabelCommand = ReactiveCommand.CreateFromTask(SetContinuationLabelAsync);
        }

        /// <summary>
        /// Синхронизирует состояние вкладки с таблицей под кареткой: режим разбивки,
        /// повтор заголовка и выравнивание ячейки. Вызывается из
        /// TextEditorViewModel.NotifyCaretEnteredTable, а тот дёргается канвасом при
        /// каждой смене ячейки — поэтому подсветка следует за кареткой.
        /// </summary>
        public void SyncFromTarget()
        {
            IsByCell = _target.TableGetSplitModeByCell();
            IsRepeatHeader = _target.TableGetRepeatHeader();

            // Канвас сбрасывает инструмент при выходе из таблицы — кнопки должны
            // погаснуть вместе с ним, иначе останутся подсвеченными вхолостую.
            _lineTool = _target.TableGetLineTool();
            this.RaisePropertyChanged(nameof(IsPencilTool));
            this.RaisePropertyChanged(nameof(IsEraserTool));

            RefreshAlignState();
            RefreshPaddingState();
        }

        /// <summary>
        /// Задаёт обе координаты выравнивания разом и перечитывает состояние.
        /// </summary>
        private void ApplyAlign(int vAlign, Models.Styles.TextAlignment hAlign)
        {
            // Одним вызовом, а не парой сеттеров: два вызова кладут в стек отмены
            // два снимка, и одно нажатие кнопки отменялось бы двумя Ctrl+Z.
            _target.TableSetCellAlign(vAlign, hAlign);
            RefreshAlignState();
        }

        // ── Приватные методы ──────────────────────────────────────────────

        /// <summary>
        /// Применяет текущее перо (стиль, толщина, цвет) к указанной стороне.
        /// Пустой цвет означает «цвет по умолчанию» — так же, как раньше.
        /// </summary>
        /// <summary>
        /// Ввод собственного узора линии: чередование штрих-пробел через запятую.
        /// Узор сохраняется в пере и виден в образце. Донести его до ячейки пока
        /// нечем — стиль границы в модели это перечисление из шести значений.
        /// </summary>
        private async Task EditCustomLinePatternAsync()
        {
            string? result = await ShowInputDialogAsync(
                "Свой узор линии",
                "Чередование длин штриха и пробела через запятую.\n" +
                "Например: 6,3 — штрих шесть, пробел три. 1,2 — точки.",
                CustomDashPattern);

            if (!string.IsNullOrWhiteSpace(result))
            {
                CustomDashPattern = result.Trim();
                BorderLineStyle = BorderStyle.Dashed;
            }
        }

        private void ApplyBorder(string side)
        {
            string? color = string.IsNullOrWhiteSpace(BorderColorPick) ? null : BorderColorPick;
            _target.TableSetCellBorder(side, BorderLineStyle, (double)BorderThicknessPt, color);
        }

        /// <summary>
        /// Первый клик — ставит дефолтный текст.
        /// Последующие клики — открывают диалог редактирования.
        /// Пустая строка в диалоге — убирает метку.
        /// </summary>
        private async Task SetBreakLabelAsync()
        {
            string? current = _target.TableGetBreakLabel();
            if (current is null)
            {
                _target.TableSetBreakLabel("Продолжение на следующей странице");
            }
            else
            {
                string? result = await ShowInputDialogAsync(
                    "Надпись разрыва",
                    "Введите текст под таблицей перед разрывом страницы.\nОставьте пустым — убрать надпись.",
                    current);

                if (result is not null)
                    _target.TableSetBreakLabel(string.IsNullOrWhiteSpace(result) ? null : result);
            }
        }

        /// <summary>
        /// Первый клик — ставит дефолтный текст.
        /// Последующие клики — открывают диалог редактирования.
        /// Пустая строка в диалоге — убирает метку.
        /// </summary>
        private async Task SetContinuationLabelAsync()
        {
            string? current = _target.TableGetContinuationLabel();
            if (current is null)
            {
                _target.TableSetContinuationLabel("Таблица (продолжение)");
            }
            else
            {
                string? result = await ShowInputDialogAsync(
                    "Надпись продолжения",
                    "Введите текст над продолжением таблицы на следующей странице.\nОставьте пустым — убрать надпись.",
                    current);

                if (result is not null)
                    _target.TableSetContinuationLabel(string.IsNullOrWhiteSpace(result) ? null : result);
            }
        }

        /// <summary>
        /// Открывает InputDialog (собственный диалог модуля) через главное окно Avalonia.
        /// Не зависит от основного приложения — использует только публичное Avalonia API.
        /// </summary>
        private static async Task<string?> ShowInputDialogAsync(string title, string prompt, string current)
        {
            var lifetime = Application.Current?.ApplicationLifetime
                as IClassicDesktopStyleApplicationLifetime;

            var owner = lifetime?.MainWindow;
            if (owner is null) return null;

            var dialog = new InputDialog(title, prompt, current);
            return await dialog.ShowDialog<string?>(owner);
        }
    }
}