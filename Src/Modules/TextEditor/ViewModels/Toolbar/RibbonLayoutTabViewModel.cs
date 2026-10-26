using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Input;
using ReactiveUI;
using Writersword.Core.Models.Print;
using Writersword.Modules.TextEditor.Contracts;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Page;

namespace Writersword.Modules.TextEditor.ViewModels.Toolbar
{
    public sealed class RibbonLayoutTabViewModel : ReactiveObject
    {
        private readonly ITextEditorCommandTarget _target;

        private bool _isPageGroupExpanded = true;
        private bool _isColumnsGroupExpanded = true;
        private bool _isBreaksGroupExpanded = true;

        private int _currentColumnCount = 1;

        public bool IsPageGroupExpanded
        {
            get => _isPageGroupExpanded;
            set => this.RaiseAndSetIfChanged(ref _isPageGroupExpanded, value);
        }

        public bool IsColumnsGroupExpanded
        {
            get => _isColumnsGroupExpanded;
            set => this.RaiseAndSetIfChanged(ref _isColumnsGroupExpanded, value);
        }

        public bool IsBreaksGroupExpanded
        {
            get => _isBreaksGroupExpanded;
            set => this.RaiseAndSetIfChanged(ref _isBreaksGroupExpanded, value);
        }

        public int CurrentColumnCount
        {
            get => _currentColumnCount;
            set => this.RaiseAndSetIfChanged(ref _currentColumnCount, value);
        }

        // ── К чему применяются параметры страницы ─────────────────────────
        // Как «Применить» в окне параметров страницы Word: ко всему разделу под
        // кареткой или только к выделенному — тогда раздел режется разрывами, и
        // новые размер, ориентацию, поля и колонки получает только выделенный кусок.

        /// <summary>Параметры страницы — только выделенному (абзацу под кареткой).</summary>
        public bool ApplyToSelection
        {
            get => _target.PageSetupToSelection;
            set
            {
                if (_target.PageSetupToSelection == value) return;
                _target.PageSetupToSelection = value;
                this.RaisePropertyChanged(nameof(ApplyToSelection));
                this.RaisePropertyChanged(nameof(ApplyToSection));
            }
        }

        /// <summary>Параметры страницы — всему разделу под кареткой.</summary>
        public bool ApplyToSection
        {
            get => !_target.PageSetupToSelection;
            set
            {
                if (value) ApplyToSelection = false;
                else this.RaisePropertyChanged(nameof(ApplyToSection));
            }
        }

        // ── Поля страницы и переплёт ──────────────────────────────────────
        // Поля меню «Поля»: значения в миллиметрах, как их хранит документ. Правка поля
        // сразу ложится на документ; при открытии меню поля перечитываются.

        private decimal _marginTopMm;
        private decimal _marginBottomMm;
        private decimal _marginLeftMm;
        private decimal _marginRightMm;
        private decimal _gutterMm;

        // Подстановка значений из документа не должна тут же писать их обратно.
        private bool _syncingMargins;

        private bool _mirrorMargins;

        /// <summary>
        /// Зеркальные поля документа: левое поле — внутреннее, правое — внешнее, на
        /// чётных листах они меняются местами вместе с переплётом.
        /// </summary>
        public bool MirrorMargins
        {
            get => _mirrorMargins;
            set
            {
                if (_mirrorMargins == value) return;
                _mirrorMargins = value;
                this.RaisePropertyChanged(nameof(MirrorMargins));
                this.RaisePropertyChanged(nameof(MarginLeftLabel));
                this.RaisePropertyChanged(nameof(MarginRightLabel));

                if (!_syncingMargins)
                    _target.SetMirrorMargins(value);
            }
        }

        /// <summary>Подпись левого поля: при зеркальных полях оно внутреннее.</summary>
        public string MarginLeftLabel => _mirrorMargins ? "Внутреннее, мм" : "Левое, мм";

        /// <summary>Подпись правого поля: при зеркальных полях оно внешнее.</summary>
        public string MarginRightLabel => _mirrorMargins ? "Внешнее, мм" : "Правое, мм";

        /// <summary>Верхнее поле страницы, мм.</summary>
        public decimal MarginTopMm
        {
            get => _marginTopMm;
            set => SetMargin(ref _marginTopMm, value, nameof(MarginTopMm));
        }

        /// <summary>Нижнее поле страницы, мм.</summary>
        public decimal MarginBottomMm
        {
            get => _marginBottomMm;
            set => SetMargin(ref _marginBottomMm, value, nameof(MarginBottomMm));
        }

        /// <summary>Левое поле страницы без переплёта, мм.</summary>
        public decimal MarginLeftMm
        {
            get => _marginLeftMm;
            set => SetMargin(ref _marginLeftMm, value, nameof(MarginLeftMm));
        }

        /// <summary>Правое поле страницы, мм.</summary>
        public decimal MarginRightMm
        {
            get => _marginRightMm;
            set => SetMargin(ref _marginRightMm, value, nameof(MarginRightMm));
        }

        /// <summary>
        /// Переплёт, мм: полоса под сшивку у корешка, прибавляется к полю. При разных
        /// колонтитулах чётных и нечётных страниц сторона переплёта чередуется.
        /// </summary>
        public decimal GutterMm
        {
            get => _gutterMm;
            set
            {
                decimal clamped = Math.Clamp(value, 0m, 100m);
                bool changed = _gutterMm != clamped;
                _gutterMm = clamped;
                this.RaisePropertyChanged(nameof(GutterMm));

                if (changed && !_syncingMargins)
                    _target.SetPageGutter((double)_gutterMm);
            }
        }

        // Уведомление идёт всегда: значение могло быть обрезано по диапазону, и поле
        // ввода иначе осталось бы с недопустимым числом.
        private void SetMargin(ref decimal field, decimal value, string name)
        {
            decimal clamped = Math.Clamp(value, 0m, 200m);
            bool changed = field != clamped;
            field = clamped;
            this.RaisePropertyChanged(name);

            if (changed && !_syncingMargins)
                _target.SetPageMargins(
                    (double)_marginTopMm, (double)_marginBottomMm,
                    (double)_marginLeftMm, (double)_marginRightMm);
        }

        /// <summary>
        /// Перечитывает поля и переплёт раздела под кареткой. Зовётся при открытии меню
        /// «Поля»: поля могли поменять линейкой или импортом, пока меню было закрыто, а
        /// каретка — уйти в другой раздел.
        /// </summary>
        public void RefreshPageMargins()
        {
            this.RaisePropertyChanged(nameof(ApplyToSelection));
            this.RaisePropertyChanged(nameof(ApplyToSection));

            var (top, bottom, left, right, gutter) = _target.GetPageMargins();

            _syncingMargins = true;
            try
            {
                MarginTopMm = Math.Round((decimal)top, 2);
                MarginBottomMm = Math.Round((decimal)bottom, 2);
                MarginLeftMm = Math.Round((decimal)left, 2);
                MarginRightMm = Math.Round((decimal)right, 2);
                GutterMm = Math.Round((decimal)gutter, 2);
                MirrorMargins = _target.GetMirrorMargins();
            }
            finally
            {
                _syncingMargins = false;
            }
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
        public ICommand InsertColumnBreakCommand { get; }
        public ICommand InsertSectionBreakNextPageCommand { get; }
        public ICommand InsertSectionBreakContinuousCommand { get; }
        public ICommand InsertSectionBreakEvenPageCommand { get; }
        public ICommand InsertSectionBreakOddPageCommand { get; }

        /// <summary>Две колонки, левая уже: треть и две трети ширины, как «Слева» у Word.</summary>
        public ICommand SetColumnsLeftCommand { get; }

        /// <summary>Две колонки, правая уже: «Справа» у Word.</summary>
        public ICommand SetColumnsRightCommand { get; }

        /// <summary>Колонки из окна «Другие колонки».</summary>
        public ICommand ApplyColumnsCommand { get; }

        /// <summary>Параметры из окна «Параметры раздела».</summary>
        public ICommand ApplySectionSetupCommand { get; }

        public RibbonLayoutTabViewModel(ITextEditorCommandTarget target)
        {
            _target = target;

            SetSizeA4Command = ReactiveCommand.Create(() => _target.SetPageSize(PaperSize.A4));
            SetSizeA3Command = ReactiveCommand.Create(() => _target.SetPageSize(PaperSize.A3));
            SetSizeA5Command = ReactiveCommand.Create(() => _target.SetPageSize(PaperSize.A5));
            SetSizeLetterCommand = ReactiveCommand.Create(() => _target.SetPageSize(PaperSize.Letter));
            SetOrientationPortraitCommand = ReactiveCommand.Create(() => _target.SetPageOrientation(PageOrientation.Portrait));
            SetOrientationLandscapeCommand = ReactiveCommand.Create(() => _target.SetPageOrientation(PageOrientation.Landscape));
            SetMarginsCommand = ReactiveCommand.Create(RefreshPageMargins);

            Set1ColumnCommand = ReactiveCommand.Create(() => SetColumns(1));
            Set2ColumnsCommand = ReactiveCommand.Create(() => SetColumns(2));
            Set3ColumnsCommand = ReactiveCommand.Create(() => SetColumns(3));

            InsertPageBreakCommand = ReactiveCommand.Create(() => _target.InsertPageBreak());
            InsertSectionBreakCommand = ReactiveCommand.Create(() => _target.InsertSectionBreak(BreakType.SectionNextPage));
            InsertColumnBreakCommand = ReactiveCommand.Create(() => _target.InsertColumnBreak());
            InsertSectionBreakNextPageCommand = ReactiveCommand.Create(() => _target.InsertSectionBreak(BreakType.SectionNextPage));
            InsertSectionBreakContinuousCommand = ReactiveCommand.Create(() => _target.InsertSectionBreak(BreakType.SectionContinuous));
            InsertSectionBreakEvenPageCommand = ReactiveCommand.Create(() => _target.InsertSectionBreak(BreakType.SectionEvenPage));
            InsertSectionBreakOddPageCommand = ReactiveCommand.Create(() => _target.InsertSectionBreak(BreakType.SectionOddPage));

            SetColumnsLeftCommand = ReactiveCommand.Create(() => SetTwoUnequalColumns(narrowFirst: true));
            SetColumnsRightCommand = ReactiveCommand.Create(() => SetTwoUnequalColumns(narrowFirst: false));
            ApplyColumnsCommand = ReactiveCommand.Create(ApplyColumns);
            ApplySectionSetupCommand = ReactiveCommand.Create(ApplySectionSetup);
        }

        // ── Другие колонки ────────────────────────────────────────────────
        // Окно колонок раздела под кареткой: число, одинаковая ли ширина, промежуток,
        // черта и ширина каждой колонки. Значения — в миллиметрах. Ничего не ложится на
        // документ, пока не нажата «Применить».

        private const decimal DefaultColumnGapMm = 12.5m;

        // Самая узкая колонка — полдюйма, как у Word: уже в строку не встаёт и пары
        // слов, а число колонок окна упирается в то, сколько таких влезает в ширину
        // текста. На листе A4 с обычными полями это одиннадцать-двенадцать колонок,
        // на узком — меньше; больше сорока пяти не бывает ни на каком листе.
        private const decimal MinColumnWidthMm = 12.7m;
        private const int ColumnCountLimit = 45;

        // Сколько колонок у раздела в документе. Окно не урезает их при открытии, даже
        // если такие колонки уже, чем позволяет предел: документ мог прийти извне.
        private int _loadedColumnCount = 1;

        private decimal _columnCountValue = 1;
        private bool _columnsEqualWidth = true;
        private decimal _columnGapMm = DefaultColumnGapMm;
        private bool _columnSeparator;
        private decimal _columnsTextWidthMm;
        private string _columnsTotalInfo = string.Empty;

        // Пересчёт ширин не должен будить сам себя через уведомления строк.
        private bool _syncingColumns;

        /// <summary>Колонки окна по порядку: ширина и промежуток после каждой.</summary>
        public ObservableCollection<ColumnRowViewModel> ColumnRows { get; } = new();

        /// <summary>Число колонок окна.</summary>
        public decimal ColumnCountValue
        {
            get => _columnCountValue;
            set
            {
                decimal clamped = Math.Clamp(Math.Round(value), 1m, MaxColumnCount);
                bool changed = _columnCountValue != clamped;
                _columnCountValue = clamped;
                this.RaisePropertyChanged(nameof(ColumnCountValue));
                this.RaisePropertyChanged(nameof(HasSeveralColumns));
                this.RaisePropertyChanged(nameof(CanEditColumnGap));
                this.RaisePropertyChanged(nameof(ShowColumnTable));
                this.RaisePropertyChanged(nameof(MaxColumnGapMm));
                if (changed && !_syncingColumns)
                {
                    FitColumnGap();
                    RebuildColumnRows(keepWidths: false);
                }
            }
        }

        /// <summary>
        /// Больше колонок, чем это число, в ширину текста не влезет: каждая была бы уже
        /// полудюйма. Колонки раздела, открытого в окне, предел не урезает.
        /// </summary>
        public decimal MaxColumnCount
        {
            get
            {
                int fit = _columnsTextWidthMm > 0m
                    ? (int)Math.Floor(_columnsTextWidthMm / MinColumnWidthMm)
                    : ColumnCountLimit;
                return Math.Clamp(Math.Max(fit, _loadedColumnCount), 1, ColumnCountLimit);
            }
        }

        /// <summary>
        /// Самый широкий промежуток у колонок одинаковой ширины, при котором каждая
        /// колонка не уже полудюйма.
        /// </summary>
        public decimal MaxColumnGapMm
        {
            get
            {
                int count = (int)_columnCountValue;
                if (count < 2 || _columnsTextWidthMm <= 0m) return 200m;
                decimal fit = (_columnsTextWidthMm - count * MinColumnWidthMm) / (count - 1);
                fit = Math.Floor(fit * 100m) / 100m;
                return Math.Clamp(fit, 0m, 200m);
            }
        }

        /// <summary>
        /// Таблица ширин по колонкам нужна только колонкам разной ширины: у одинаковых
        /// ширина выводится из промежутка, и править в таблице нечего.
        /// </summary>
        public bool ShowColumnTable => HasSeveralColumns && !_columnsEqualWidth;

        /// <summary>
        /// Сужает промежуток одинаковых колонок, если с ним колонки вышли бы уже
        /// полудюйма, — так делает и Word, когда колонок прибавляют.
        /// </summary>
        private void FitColumnGap()
        {
            if (!_columnsEqualWidth || _columnCountValue < 2) return;
            decimal max = MaxColumnGapMm;
            if (_columnGapMm <= max) return;
            _columnGapMm = max;
            this.RaisePropertyChanged(nameof(ColumnGapMm));
        }

        /// <summary>Колонок больше одной: черта и ширины имеют смысл.</summary>
        public bool HasSeveralColumns => _columnCountValue > 1;

        /// <summary>
        /// Общий промежуток правится только у нескольких колонок одинаковой ширины: у
        /// одной колонки промежутка нет, у разных — у каждой свой, в её строке.
        /// </summary>
        public bool CanEditColumnGap => HasSeveralColumns && _columnsEqualWidth;

        /// <summary>Все колонки одной ширины через один промежуток.</summary>
        public bool ColumnsEqualWidth
        {
            get => _columnsEqualWidth;
            set
            {
                if (_columnsEqualWidth == value) return;
                _columnsEqualWidth = value;
                this.RaisePropertyChanged(nameof(ColumnsEqualWidth));
                this.RaisePropertyChanged(nameof(CanEditColumnGap));
                this.RaisePropertyChanged(nameof(ShowColumnTable));
                if (!_syncingColumns)
                {
                    FitColumnGap();
                    RebuildColumnRows(keepWidths: !value);
                }
            }
        }

        /// <summary>Промежуток между равными колонками, мм.</summary>
        public decimal ColumnGapMm
        {
            get => _columnGapMm;
            set
            {
                decimal clamped = Math.Clamp(value, 0m, 200m);
                if (!_syncingColumns && _columnsEqualWidth && _columnCountValue > 1)
                    clamped = Math.Min(clamped, MaxColumnGapMm);
                bool changed = _columnGapMm != clamped;
                _columnGapMm = clamped;
                this.RaisePropertyChanged(nameof(ColumnGapMm));
                if (changed && !_syncingColumns && _columnsEqualWidth) RebuildColumnRows(keepWidths: false);
            }
        }

        /// <summary>Черта между колонками.</summary>
        public bool ColumnSeparator
        {
            get => _columnSeparator;
            set => this.RaiseAndSetIfChanged(ref _columnSeparator, value);
        }

        // Авто-цвет черты — чёрный, как у Word. Подборщику цвета нужен код, и «авто»
        // показывается им; в раздел чёрный уходит как «авто» (null).
        private const string AutoSeparatorColor = "#000000";
        private string _columnSeparatorColor = AutoSeparatorColor;

        /// <summary>
        /// Цвет черты между колонками — код подборщика: сплошной, с прозрачностью или
        /// градиент (SectionColumns.SeparatorColor).
        /// </summary>
        public string ColumnSeparatorColor
        {
            get => _columnSeparatorColor;
            set => this.RaiseAndSetIfChanged(ref _columnSeparatorColor,
                string.IsNullOrWhiteSpace(value) ? AutoSeparatorColor : value);
        }

        /// <summary>Цвет черты для раздела: чёрный — это «авто», null.</summary>
        private string? SeparatorColorForSection()
        {
            string color = _columnSeparatorColor;
            if (string.Equals(color, AutoSeparatorColor, StringComparison.OrdinalIgnoreCase)
                || string.Equals(color, "#FF000000", StringComparison.OrdinalIgnoreCase))
                return null;
            return color;
        }

        /// <summary>Сколько ширины текста занимают колонки с промежутками — подпись окна.</summary>
        public string ColumnsTotalInfo
        {
            get => _columnsTotalInfo;
            private set => this.RaiseAndSetIfChanged(ref _columnsTotalInfo, value);
        }

        /// <summary>
        /// Перечитывает колонки раздела под кареткой. Зовётся при открытии окна: каретка
        /// могла уйти в другой раздел, а колонки — поменяться.
        /// </summary>
        public void RefreshColumnDialog()
        {
            this.RaisePropertyChanged(nameof(ApplyToSelection));
            this.RaisePropertyChanged(nameof(ApplyToSection));

            var setup = _target.GetSectionSetup();
            var columns = setup.Columns;

            _syncingColumns = true;
            try
            {
                _columnsTextWidthMm = (decimal)Math.Max(setup.Page.GetTextWidthMm(), 1.0);

                bool unequal = columns.Columns is { Count: > 1 };
                _loadedColumnCount = unequal ? columns.Columns!.Count : Math.Max(1, columns.Count);
                this.RaisePropertyChanged(nameof(MaxColumnCount));
                ColumnCountValue = unequal ? columns.Columns!.Count : Math.Max(1, columns.Count);
                ColumnsEqualWidth = !unequal;
                ColumnGapMm = Math.Round((decimal)(unequal ? columns.Columns![0].SpaceAfterMm : columns.GapMm), 2);
                ColumnSeparator = columns.Separator;
                ColumnSeparatorColor = columns.SeparatorColor ?? AutoSeparatorColor;

                ColumnRows.Clear();
                if (unequal)
                {
                    var own = columns.Columns!;
                    for (int i = 0; i < own.Count; i++)
                    {
                        ColumnRows.Add(new ColumnRowViewModel(
                            i + 1, i == own.Count - 1, editable: true,
                            Math.Round((decimal)own[i].WidthMm, 2),
                            Math.Round((decimal)own[i].SpaceAfterMm, 2),
                            OnColumnRowChanged));
                    }
                }
            }
            finally
            {
                _syncingColumns = false;
            }

            if (ColumnRows.Count == 0) RebuildColumnRows(keepWidths: false);
            else UpdateColumnsTotal();
        }

        /// <summary>
        /// Строки колонок заново. Равные колонки делят ширину текста поровну за вычетом
        /// промежутков. У колонок разной ширины при keepWidths прежние строки остаются —
        /// так переключатель «одинаковой ширины» не стирает введённое; новые строки
        /// получают равную долю.
        /// </summary>
        private void RebuildColumnRows(bool keepWidths)
        {
            int count = (int)_columnCountValue;
            decimal gap = count > 1 ? _columnGapMm : 0m;
            decimal width = Math.Max((_columnsTextWidthMm - gap * (count - 1)) / count, 1m);
            width = Math.Round(width, 2);

            var previous = new List<(decimal Width, decimal Space)>();
            if (keepWidths)
                foreach (var row in ColumnRows) previous.Add((row.WidthMm, row.SpaceMm));

            _syncingColumns = true;
            try
            {
                ColumnRows.Clear();
                for (int i = 0; i < count; i++)
                {
                    bool last = i == count - 1;
                    decimal rowWidth = keepWidths && i < previous.Count ? previous[i].Width : width;
                    decimal rowSpace = last ? 0m : keepWidths && i < previous.Count - 1 ? previous[i].Space : gap;

                    ColumnRows.Add(new ColumnRowViewModel(
                        i + 1, last, editable: !_columnsEqualWidth, rowWidth, rowSpace, OnColumnRowChanged));
                }
            }
            finally
            {
                _syncingColumns = false;
            }

            UpdateColumnsTotal();
        }

        private void OnColumnRowChanged()
        {
            if (_syncingColumns) return;
            UpdateColumnsTotal();
        }

        private void UpdateColumnsTotal()
        {
            if (_columnsEqualWidth && ColumnRows.Count > 1)
            {
                ColumnsTotalInfo = $"Каждая колонка — {ColumnRows[0].WidthMm:0.##} мм при ширине текста {_columnsTextWidthMm:0.#} мм";
                return;
            }

            decimal used = 0m;
            foreach (var row in ColumnRows)
                used += row.WidthMm + (row.IsLast ? 0m : row.SpaceMm);

            string text = $"Ширина текста {_columnsTextWidthMm:0.#} мм, колонки с промежутками — {used:0.#} мм";
            if (used > _columnsTextWidthMm + 0.05m)
                text += ": шире текста, колонки будут сжаты в него";
            ColumnsTotalInfo = text;
        }

        private void ApplyColumns()
        {
            int count = (int)_columnCountValue;
            SectionColumns columns;

            if (count > 1 && !_columnsEqualWidth && ColumnRows.Count == count)
            {
                var list = new List<SectionColumn>(count);
                foreach (var row in ColumnRows)
                {
                    list.Add(new SectionColumn
                    {
                        WidthMm = (double)Math.Max(row.WidthMm, 1m),
                        SpaceAfterMm = row.IsLast ? 0.0 : (double)Math.Max(row.SpaceMm, 0m)
                    });
                }

                columns = new SectionColumns
                {
                    Count = count,
                    GapMm = list[0].SpaceAfterMm,
                    Separator = _columnSeparator,
                    SeparatorColor = SeparatorColorForSection(),
                    Columns = list
                };
            }
            else
            {
                columns = new SectionColumns
                {
                    Count = count,
                    GapMm = (double)_columnGapMm,
                    Separator = count > 1 && _columnSeparator,
                    SeparatorColor = SeparatorColorForSection()
                };
            }

            _target.SetColumnLayout(columns);
            CurrentColumnCount = _target.GetColumnCount();
        }

        /// <summary>
        /// Две колонки разной ширины — заготовки «Слева» и «Справа» у Word: узкая
        /// колонка в треть ширины, широкая — в две трети, промежуток прежний.
        /// </summary>
        private void SetTwoUnequalColumns(bool narrowFirst)
        {
            var setup = _target.GetSectionSetup();
            double textWidth = Math.Max(setup.Page.GetTextWidthMm(), 1.0);
            double gap = setup.Columns.Columns is { Count: > 1 } own ? own[0].SpaceAfterMm : setup.Columns.GapMm;
            gap = Math.Clamp(gap, 0.0, textWidth / 2.0);

            double narrow = Math.Round((textWidth - gap) / 3.0, 2);
            double wide = Math.Round(textWidth - gap - narrow, 2);

            _target.SetColumnLayout(new SectionColumns
            {
                Count = 2,
                GapMm = gap,
                Separator = setup.Columns.Separator,
                SeparatorColor = setup.Columns.SeparatorColor,
                Columns = new List<SectionColumn>
                {
                    new() { WidthMm = narrowFirst ? narrow : wide, SpaceAfterMm = gap },
                    new() { WidthMm = narrowFirst ? wide : narrow, SpaceAfterMm = 0.0 }
                }
            });
            CurrentColumnCount = _target.GetColumnCount();
        }

        // ── Параметры раздела ─────────────────────────────────────────────
        // Окно с тем, чего нет на ленте: свой размер листа, выравнивание по высоте,
        // начало раздела, номера строк и рамка страницы. Всё ложится одним шагом отмены
        // по «Применить».

        /// <summary>Цвета рамки на выбор: имя и цвет в виде RRGGBB; null — авто (чёрный).</summary>
        private static readonly (string Name, string? Hex)[] BorderPalette =
        {
            ("Авто", null),
            ("Чёрный", "000000"),
            ("Тёмно-синий", "1F3864"),
            ("Синий", "2F5496"),
            ("Серый", "808080"),
            ("Красный", "C00000"),
            ("Зелёный", "538135")
        };

        /// <summary>Виды линии рамки по порядку списка; первый — рамки нет.</summary>
        private static readonly string[] BorderStyles = { "none", "single", "double", "dotted", "dashed", "thickThinSmallGap", "thinThickSmallGap" };

        private const decimal BorderSpaceFromPagePt = 24m;
        private const decimal BorderSpaceFromTextPt = 4m;

        private SectionSetup? _loadedSetup;
        private bool _bordersEdited;
        private bool _syncingSetup;
        private string? _customBorderColor;

        private decimal _pageWidthMm;
        private decimal _pageHeightMm;
        private int _verticalAlignmentIndex;
        private int _sectionStartIndex;
        private bool _hasSectionStart;
        private bool _lineNumbersOn;
        private decimal _lineNumberStart = 1;
        private decimal _lineNumberCountBy = 1;
        private decimal _lineNumberDistanceMm;
        private int _lineNumberRestartIndex;
        private int _borderStyleIndex;
        private decimal _borderWidthPt = 0.5m;
        private int _borderColorIndex;
        private decimal _borderSpacePt = BorderSpaceFromPagePt;
        private int _borderFromIndex;
        private int _borderDisplayIndex;

        /// <summary>Ширина листа так, как он стоит, мм.</summary>
        public decimal PageWidthMm
        {
            get => _pageWidthMm;
            set => this.RaiseAndSetIfChanged(ref _pageWidthMm, Math.Clamp(value, 10m, 1000m));
        }

        /// <summary>Высота листа так, как он стоит, мм.</summary>
        public decimal PageHeightMm
        {
            get => _pageHeightMm;
            set => this.RaiseAndSetIfChanged(ref _pageHeightMm, Math.Clamp(value, 10m, 1000m));
        }

        /// <summary>Выравнивание по высоте: 0 — сверху, 1 — по центру, 2 — по высоте, 3 — снизу.</summary>
        public int VerticalAlignmentIndex
        {
            get => _verticalAlignmentIndex;
            set => this.RaiseAndSetIfChanged(ref _verticalAlignmentIndex, Math.Clamp(value, 0, 3));
        }

        /// <summary>
        /// Начало раздела: 0 — со следующей страницы, 1 — на текущей, 2 — с чётной,
        /// 3 — с нечётной.
        /// </summary>
        public int SectionStartIndex
        {
            get => _sectionStartIndex;
            set => this.RaiseAndSetIfChanged(ref _sectionStartIndex, Math.Clamp(value, 0, 3));
        }

        /// <summary>Перед разделом есть разрыв: у первого раздела документа начала не выбрать.</summary>
        public bool HasSectionStart
        {
            get => _hasSectionStart;
            private set => this.RaiseAndSetIfChanged(ref _hasSectionStart, value);
        }

        /// <summary>Строки раздела нумеруются.</summary>
        public bool LineNumbersOn
        {
            get => _lineNumbersOn;
            set => this.RaiseAndSetIfChanged(ref _lineNumbersOn, value);
        }

        /// <summary>Номер первой строки.</summary>
        public decimal LineNumberStart
        {
            get => _lineNumberStart;
            set => this.RaiseAndSetIfChanged(ref _lineNumberStart, Math.Clamp(Math.Round(value), 1m, 32767m));
        }

        /// <summary>Показывается каждый n-й номер.</summary>
        public decimal LineNumberCountBy
        {
            get => _lineNumberCountBy;
            set => this.RaiseAndSetIfChanged(ref _lineNumberCountBy, Math.Clamp(Math.Round(value), 1m, 100m));
        }

        /// <summary>Расстояние от номера до текста, мм; 0 — как у Word по умолчанию.</summary>
        public decimal LineNumberDistanceMm
        {
            get => _lineNumberDistanceMm;
            set => this.RaiseAndSetIfChanged(ref _lineNumberDistanceMm, Math.Clamp(value, 0m, 100m));
        }

        /// <summary>Счёт строк заново: 0 — на каждой странице, 1 — в каждом разделе, 2 — непрерывно.</summary>
        public int LineNumberRestartIndex
        {
            get => _lineNumberRestartIndex;
            set => this.RaiseAndSetIfChanged(ref _lineNumberRestartIndex, Math.Clamp(value, 0, 2));
        }

        /// <summary>
        /// Вид линии рамки: 0 — нет, 1 — одинарная, 2 — двойная, 3 — точками,
        /// 4 — штрихами, 5 — толстая и тонкая, 6 — тонкая и толстая.
        /// </summary>
        public int BorderStyleIndex
        {
            get => _borderStyleIndex;
            set
            {
                int clamped = Math.Clamp(value, 0, BorderStyles.Length - 1);
                if (_borderStyleIndex == clamped) return;
                _borderStyleIndex = clamped;
                this.RaisePropertyChanged(nameof(BorderStyleIndex));
                this.RaisePropertyChanged(nameof(HasBorder));
                MarkBordersEdited();
            }
        }

        /// <summary>Рамка есть: её толщина, цвет и отступ имеют смысл.</summary>
        public bool HasBorder => _borderStyleIndex > 0;

        /// <summary>Толщина линии рамки, пт.</summary>
        public decimal BorderWidthPt
        {
            get => _borderWidthPt;
            set
            {
                decimal clamped = Math.Clamp(value, 0.25m, 6m);
                if (_borderWidthPt == clamped) return;
                _borderWidthPt = clamped;
                this.RaisePropertyChanged(nameof(BorderWidthPt));
                MarkBordersEdited();
            }
        }

        /// <summary>Цвета рамки для списка: палитра и, если у рамки свой цвет, он.</summary>
        public ObservableCollection<string> BorderColorNames { get; } = new();

        /// <summary>Цвет рамки — номер в <see cref="BorderColorNames"/>.</summary>
        public int BorderColorIndex
        {
            get => _borderColorIndex;
            set
            {
                if (value < 0 || _borderColorIndex == value) return;
                _borderColorIndex = value;
                this.RaisePropertyChanged(nameof(BorderColorIndex));
                MarkBordersEdited();
            }
        }

        /// <summary>Отступ рамки от края листа или от текста, пт.</summary>
        public decimal BorderSpacePt
        {
            get => _borderSpacePt;
            set
            {
                decimal clamped = Math.Clamp(value, 0m, 31m);
                if (_borderSpacePt == clamped) return;
                _borderSpacePt = clamped;
                this.RaisePropertyChanged(nameof(BorderSpacePt));
                MarkBordersEdited();
            }
        }

        /// <summary>Отступ рамки отсчитывается: 0 — от края листа, 1 — от текста.</summary>
        public int BorderFromIndex
        {
            get => _borderFromIndex;
            set
            {
                int clamped = Math.Clamp(value, 0, 1);
                if (_borderFromIndex == clamped) return;
                _borderFromIndex = clamped;
                this.RaisePropertyChanged(nameof(BorderFromIndex));

                // Отступ по умолчанию другой точки отсчёта: Word ставит рамку в 24 пт от
                // края листа и в 4 пт от текста.
                if (!_syncingSetup)
                {
                    if (clamped == 1 && _borderSpacePt == BorderSpaceFromPagePt) BorderSpacePt = BorderSpaceFromTextPt;
                    else if (clamped == 0 && _borderSpacePt == BorderSpaceFromTextPt) BorderSpacePt = BorderSpaceFromPagePt;
                }

                MarkBordersEdited();
            }
        }

        /// <summary>На каких листах раздела рамка: 0 — на всех, 1 — только на первом, 2 — кроме первого.</summary>
        public int BorderDisplayIndex
        {
            get => _borderDisplayIndex;
            set
            {
                int clamped = Math.Clamp(value, 0, 2);
                if (_borderDisplayIndex == clamped) return;
                _borderDisplayIndex = clamped;
                this.RaisePropertyChanged(nameof(BorderDisplayIndex));
                MarkBordersEdited();
            }
        }

        private bool _borderTop = true;
        private bool _borderBottom = true;
        private bool _borderLeft = true;
        private bool _borderRight = true;
        private bool _borderBehind;

        /// <summary>Рамка сверху.</summary>
        public bool BorderTop
        {
            get => _borderTop;
            set { if (_borderTop == value) return; _borderTop = value; this.RaisePropertyChanged(nameof(BorderTop)); MarkBordersEdited(); }
        }

        /// <summary>Рамка снизу.</summary>
        public bool BorderBottom
        {
            get => _borderBottom;
            set { if (_borderBottom == value) return; _borderBottom = value; this.RaisePropertyChanged(nameof(BorderBottom)); MarkBordersEdited(); }
        }

        /// <summary>Рамка слева.</summary>
        public bool BorderLeft
        {
            get => _borderLeft;
            set { if (_borderLeft == value) return; _borderLeft = value; this.RaisePropertyChanged(nameof(BorderLeft)); MarkBordersEdited(); }
        }

        /// <summary>Рамка справа.</summary>
        public bool BorderRight
        {
            get => _borderRight;
            set { if (_borderRight == value) return; _borderRight = value; this.RaisePropertyChanged(nameof(BorderRight)); MarkBordersEdited(); }
        }

        /// <summary>Рамка под текстом (иначе поверх), как w:zOrder="back" у Word.</summary>
        public bool BorderBehind
        {
            get => _borderBehind;
            set { if (_borderBehind == value) return; _borderBehind = value; this.RaisePropertyChanged(nameof(BorderBehind)); MarkBordersEdited(); }
        }

        private void MarkBordersEdited()
        {
            if (!_syncingSetup) _bordersEdited = true;
        }

        /// <summary>
        /// Перечитывает параметры раздела под кареткой. Зовётся при открытии окна: каретка
        /// могла уйти в другой раздел, а параметры — поменяться импортом или отменой.
        /// </summary>
        public void RefreshSectionSetup()
        {
            this.RaisePropertyChanged(nameof(ApplyToSelection));
            this.RaisePropertyChanged(nameof(ApplyToSection));

            var setup = _target.GetSectionSetup();
            _loadedSetup = setup;
            _bordersEdited = false;

            _syncingSetup = true;
            try
            {
                PageWidthMm = Math.Round((decimal)setup.Page.GetPhysicalWidthMm(), 1);
                PageHeightMm = Math.Round((decimal)setup.Page.GetPhysicalHeightMm(), 1);

                VerticalAlignmentIndex = setup.VerticalAlignment switch
                {
                    SectionVerticalAlignment.Center => 1,
                    SectionVerticalAlignment.Justify => 2,
                    SectionVerticalAlignment.Bottom => 3,
                    _ => 0
                };

                HasSectionStart = setup.StartBreak is not null;
                SectionStartIndex = setup.StartBreak switch
                {
                    BreakType.SectionContinuous => 1,
                    BreakType.SectionEvenPage => 2,
                    BreakType.SectionOddPage => 3,
                    _ => 0
                };

                var numbering = setup.LineNumbering;
                LineNumbersOn = numbering is not null;
                LineNumberStart = numbering?.Start ?? 1;
                LineNumberCountBy = numbering?.CountBy ?? 1;
                LineNumberDistanceMm = Math.Round((decimal)(numbering?.DistanceMm ?? 0.0), 1);
                LineNumberRestartIndex = numbering?.Restart switch
                {
                    LineNumberRestart.NewSection => 1,
                    LineNumberRestart.Continuous => 2,
                    _ => 0
                };

                LoadBorders(setup.PageBorders);
            }
            finally
            {
                _syncingSetup = false;
            }
        }

        private void LoadBorders(SectionPageBorders? borders)
        {
            var side = borders?.Top ?? borders?.Left ?? borders?.Bottom ?? borders?.Right;

            int style = 0;
            if (side is not null)
            {
                style = Array.IndexOf(BorderStyles, side.Style ?? "single");
                if (style <= 0) style = 1;
            }

            _borderStyleIndex = style;
            this.RaisePropertyChanged(nameof(BorderStyleIndex));
            this.RaisePropertyChanged(nameof(HasBorder));

            BorderWidthPt = side is null ? 0.5m : Math.Round((decimal)side.WidthPt, 2);
            BorderFromIndex = borders is { FromPageEdge: false } ? 1 : 0;
            BorderSpacePt = side is null
                ? (_borderFromIndex == 1 ? BorderSpaceFromTextPt : BorderSpaceFromPagePt)
                : Math.Round((decimal)side.SpacePt, 1);
            BorderDisplayIndex = borders?.Display switch
            {
                PageBorderDisplay.FirstPage => 1,
                PageBorderDisplay.NotFirstPage => 2,
                _ => 0
            };

            // Стороны рамки: у новой рамки — все четыре, у прежней — те, что у неё есть.
            BorderTop = borders is null || borders.Top is not null;
            BorderBottom = borders is null || borders.Bottom is not null;
            BorderLeft = borders is null || borders.Left is not null;
            BorderRight = borders is null || borders.Right is not null;
            BorderBehind = borders?.Behind ?? false;

            // Цвет рамки: из палитры, а свой — отдельной строкой списка.
            BorderColorNames.Clear();
            foreach (var (name, _) in BorderPalette) BorderColorNames.Add(name);

            _customBorderColor = null;
            int colorIndex = 0;
            string? hex = string.IsNullOrWhiteSpace(side?.Color) || side!.Color == "auto"
                ? null
                : side.Color.TrimStart('#').ToUpperInvariant();
            if (hex is not null)
            {
                colorIndex = -1;
                for (int i = 0; i < BorderPalette.Length; i++)
                {
                    if (string.Equals(BorderPalette[i].Hex, hex, StringComparison.OrdinalIgnoreCase)) { colorIndex = i; break; }
                }

                if (colorIndex < 0)
                {
                    _customBorderColor = hex;
                    BorderColorNames.Add("Свой (#" + hex + ")");
                    colorIndex = BorderColorNames.Count - 1;
                }
            }

            _borderColorIndex = colorIndex;
            this.RaisePropertyChanged(nameof(BorderColorIndex));
        }

        /// <summary>Рамка по полям окна; null — рамки нет.</summary>
        private SectionPageBorders? BuildBorders()
        {
            if (_borderStyleIndex <= 0) return null;

            string? color = _borderColorIndex >= 0 && _borderColorIndex < BorderPalette.Length
                ? BorderPalette[_borderColorIndex].Hex
                : _customBorderColor;

            PageBorderSide Side() => new()
            {
                Style = BorderStyles[_borderStyleIndex],
                WidthPt = (double)_borderWidthPt,
                SpacePt = (double)_borderSpacePt,
                Color = color
            };

            // Ни одной стороны — рамки нет.
            if (!_borderTop && !_borderBottom && !_borderLeft && !_borderRight) return null;

            return new SectionPageBorders
            {
                FromPageEdge = _borderFromIndex == 0,
                Display = _borderDisplayIndex switch
                {
                    1 => PageBorderDisplay.FirstPage,
                    2 => PageBorderDisplay.NotFirstPage,
                    _ => PageBorderDisplay.AllPages
                },
                Behind = _borderBehind,
                Top = _borderTop ? Side() : null,
                Left = _borderLeft ? Side() : null,
                Bottom = _borderBottom ? Side() : null,
                Right = _borderRight ? Side() : null
            };
        }

        private void ApplySectionSetup()
        {
            var loaded = _loadedSetup ?? _target.GetSectionSetup();

            var page = loaded.Page.Clone();
            bool landscape = page.Orientation == PageOrientation.Landscape;
            double width = (double)_pageWidthMm;
            double height = (double)_pageHeightMm;
            page.WidthMm = landscape ? height : width;
            page.HeightMm = landscape ? width : height;

            var setup = new SectionSetup
            {
                Page = page,
                Columns = loaded.Columns.Clone(),
                VerticalAlignment = _verticalAlignmentIndex switch
                {
                    1 => SectionVerticalAlignment.Center,
                    2 => SectionVerticalAlignment.Justify,
                    3 => SectionVerticalAlignment.Bottom,
                    _ => SectionVerticalAlignment.Top
                },
                LineNumbering = _lineNumbersOn
                    ? new SectionLineNumbering
                    {
                        Start = (int)_lineNumberStart,
                        CountBy = (int)_lineNumberCountBy,
                        DistanceMm = (double)_lineNumberDistanceMm,
                        Restart = _lineNumberRestartIndex switch
                        {
                            1 => LineNumberRestart.NewSection,
                            2 => LineNumberRestart.Continuous,
                            _ => LineNumberRestart.NewPage
                        }
                    }
                    : null,

                // Рамку, которой в окне не трогали, окно не переписывает: у её сторон
                // может быть разный вид, а окно задаёт выбранные стороны одинаковыми.
                PageBorders = _bordersEdited ? BuildBorders() : loaded.PageBorders?.Clone(),

                StartBreak = _hasSectionStart
                    ? _sectionStartIndex switch
                    {
                        1 => BreakType.SectionContinuous,
                        2 => BreakType.SectionEvenPage,
                        3 => BreakType.SectionOddPage,
                        _ => BreakType.SectionNextPage
                    }
                    : null
            };

            _target.SetSectionSetup(setup);
            RefreshSectionSetup();
        }

        private void SetColumns(int count)
        {
            _target.SetColumns(count);
            CurrentColumnCount = _target.GetColumnCount();
        }

        /// <summary>Колонки раздела под кареткой — для переключателей группы «Колонки».</summary>
        public void RefreshColumns() => CurrentColumnCount = _target.GetColumnCount();

        public void UpdateLayout(double availableWidth)
        {
            // Группы «Страница» и «Колонки» шире прежнего на кнопки «Раздел», «Слева»,
            // «Справа» и «Другие колонки» — сворачиваются раньше.
            if (availableWidth >= 820)
            {
                IsPageGroupExpanded = true;
                IsColumnsGroupExpanded = true;
                IsBreaksGroupExpanded = true;
                return;
            }

            IsBreaksGroupExpanded = false;

            if (availableWidth >= 620)
            {
                IsPageGroupExpanded = true;
                IsColumnsGroupExpanded = true;
                return;
            }

            IsColumnsGroupExpanded = false;
            IsPageGroupExpanded = availableWidth >= 300;
        }
    }
}