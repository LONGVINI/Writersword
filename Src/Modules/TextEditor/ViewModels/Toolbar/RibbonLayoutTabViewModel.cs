using System;
using System.Windows.Input;
using ReactiveUI;
using Writersword.Core.Models.Print;
using Writersword.Modules.TextEditor.Contracts;
using Writersword.Modules.TextEditor.Models.Document;

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
            if (availableWidth >= 700)
            {
                IsPageGroupExpanded = true;
                IsColumnsGroupExpanded = true;
                IsBreaksGroupExpanded = true;
                return;
            }

            IsBreaksGroupExpanded = false;

            if (availableWidth >= 530)
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