using System;
using System.Windows.Input;
using ReactiveUI;
using Writersword.Modules.TextEditor.Contracts;

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
        /// Перечитывает поля и переплёт из документа. Зовётся при открытии меню «Поля»:
        /// поля могли поменять линейкой или импортом, пока меню было закрыто.
        /// </summary>
        public void RefreshPageMargins()
        {
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

            SetSizeA4Command = ReactiveCommand.Create(() => { });
            SetSizeA3Command = ReactiveCommand.Create(() => { });
            SetSizeA5Command = ReactiveCommand.Create(() => { });
            SetSizeLetterCommand = ReactiveCommand.Create(() => { });
            SetOrientationPortraitCommand = ReactiveCommand.Create(() => { });
            SetOrientationLandscapeCommand = ReactiveCommand.Create(() => { });
            SetMarginsCommand = ReactiveCommand.Create(() => { });

            Set1ColumnCommand = ReactiveCommand.Create(() => CurrentColumnCount = 1);
            Set2ColumnsCommand = ReactiveCommand.Create(() => CurrentColumnCount = 2);
            Set3ColumnsCommand = ReactiveCommand.Create(() => CurrentColumnCount = 3);

            InsertPageBreakCommand = ReactiveCommand.Create(() => { });
            InsertSectionBreakCommand = ReactiveCommand.Create(() => { });
        }

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