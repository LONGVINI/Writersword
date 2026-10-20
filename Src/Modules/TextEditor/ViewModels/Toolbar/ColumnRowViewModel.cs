using System;
using ReactiveUI;

namespace Writersword.Modules.TextEditor.ViewModels.Toolbar
{
    /// <summary>
    /// Колонка в окне «Другие колонки»: её номер, ширина и промежуток после неё, мм.
    /// У последней колонки промежутка нет.
    /// </summary>
    public sealed class ColumnRowViewModel : ReactiveObject
    {
        private readonly Action _changed;
        private decimal _widthMm;
        private decimal _spaceMm;

        public ColumnRowViewModel(int number, bool isLast, bool editable, decimal widthMm, decimal spaceMm, Action changed)
        {
            Number = number;
            IsLast = isLast;
            IsEditable = editable;
            _widthMm = widthMm;
            _spaceMm = spaceMm;
            _changed = changed;
        }

        /// <summary>Номер колонки слева направо, с единицы.</summary>
        public int Number { get; }

        /// <summary>Подпись строки.</summary>
        public string Title => "Колонка " + Number;

        /// <summary>Колонка последняя: промежутка после неё нет.</summary>
        public bool IsLast { get; }

        /// <summary>Промежуток после колонки есть.</summary>
        public bool HasSpace => !IsLast;

        /// <summary>Ширину правят руками: колонки разной ширины.</summary>
        public bool IsEditable { get; }

        /// <summary>Промежуток после колонки правят руками.</summary>
        public bool IsSpaceEditable => IsEditable && !IsLast;

        /// <summary>Ширина колонки, мм.</summary>
        public decimal WidthMm
        {
            get => _widthMm;
            set
            {
                decimal clamped = Math.Clamp(value, 1m, 1000m);
                if (_widthMm == clamped) return;
                _widthMm = clamped;
                this.RaisePropertyChanged(nameof(WidthMm));
                _changed();
            }
        }

        /// <summary>Промежуток после колонки, мм.</summary>
        public decimal SpaceMm
        {
            get => _spaceMm;
            set
            {
                decimal clamped = Math.Clamp(value, 0m, 200m);
                if (_spaceMm == clamped) return;
                _spaceMm = clamped;
                this.RaisePropertyChanged(nameof(SpaceMm));
                _changed();
            }
        }
    }
}
