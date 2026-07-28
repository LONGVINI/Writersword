using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Writersword.Modules.Characters.Models;
using Writersword.Modules.Characters.Models.Enums;
using Writersword.Src.Modules.Characters.Resources;

namespace Writersword.Modules.Characters.ViewModels.Tabs
{
    /// <summary>
    /// Одна точка шкалы — кругляшок. Шкала рисуется кругляшками, а не голым
    /// слайдером: «Агрессия 3 из 5» читается мгновенно, «37» на линии 0..100 —
    /// нет. Подсказка берётся из описаний точек шкалы в определении параметра.
    /// </summary>
    public class CharacterScaleDotViewModel : ReactiveObject
    {
        public double Value { get; }
        public string Hint { get; }

        private bool _isFilled;
        public bool IsFilled { get => _isFilled; set => this.RaiseAndSetIfChanged(ref _isFilled, value); }

        public CharacterScaleDotViewModel(double value, bool isFilled, string hint)
        {
            Value = value;
            _isFilled = isFilled;
            Hint = hint;
        }
    }

    /// <summary>
    /// Обёртка над параметром персонажа для интерфейса. Модель остаётся
    /// хранилищем, а всё, что нужно только форме — человеческое название типа,
    /// кругляшки шкалы, признаки видимости редакторов — живёт здесь.
    /// </summary>
    public class CharacterParameterItemViewModel : ReactiveObject
    {
        private readonly CharacterParameter _model;

        /// <summary>
        /// Вызывается при любом изменении, требующем автосохранения. Имя не
        /// «Changed»: у ReactiveObject уже есть член с таким именем, и наше
        /// событие его скрывало бы.
        /// </summary>
        public event Action? Edited;

        public CharacterParameter Model => _model;
        public string Id => _model.Id;

        public ObservableCollection<CharacterScaleDotViewModel> Dots { get; } = new();

        public CharacterParameterItemViewModel(CharacterParameter model)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            RebuildDots();
        }

        // ── Общее ────────────────────────────────────────────────────────

        public string Name
        {
            get => _model.Name;
            set { if (_model.Name == value) return; _model.Name = value; this.RaisePropertyChanged(); Edited?.Invoke(); }
        }

        public string Description
        {
            get => _model.Description;
            set { if (_model.Description == value) return; _model.Description = value; this.RaisePropertyChanged(); this.RaisePropertyChanged(nameof(HasDescription)); Edited?.Invoke(); }
        }

        public bool HasDescription => !string.IsNullOrWhiteSpace(_model.Description);

        /// <summary>
        /// Примечание к значению: «да, но в тушёном виде», «187, сутулится
        /// и кажется ниже». Значение сравнивается и считается, примечание
        /// живёт для человека — структура никогда не вмещает жизнь целиком,
        /// каким бы точным ни был тип поля.
        /// </summary>
        public string ValueNote
        {
            get => _model.ValueNote;
            set
            {
                if (_model.ValueNote == value) return;
                _model.ValueNote = value;
                this.RaisePropertyChanged();
                this.RaisePropertyChanged(nameof(HasValueNote));
                Edited?.Invoke();
            }
        }

        public bool HasValueNote => !string.IsNullOrWhiteSpace(_model.ValueNote);

        public string GroupName => _model.GroupName;
        public bool HasGroup => !string.IsNullOrWhiteSpace(_model.GroupName);

        /// <summary>
        /// Название типа человеческим языком. В интерфейсе не должно быть слов
        /// из перечислений кода: пользователю нечем расшифровать «StateList».
        /// </summary>
        public string TypeLabel => _model.Type switch
        {
            CharacterParameterType.Numeric => CharactersStrings.Param_AddNumeric,
            CharacterParameterType.Text => CharactersStrings.Param_AddText,
            CharacterParameterType.StateList => CharactersStrings.Param_AddStateList,
            CharacterParameterType.Boolean => CharactersStrings.Param_AddBoolean,
            _ => string.Empty
        };

        /// <summary>
        /// Параметр не относится к персонажу в принципе. Значение при этом не
        /// стирается — отметка снимается, и прежние данные на месте.
        /// </summary>
        public bool IsNotApplicable
        {
            get => _model.IsNotApplicable;
            set
            {
                if (_model.IsNotApplicable == value) return;
                _model.IsNotApplicable = value;
                this.RaisePropertyChanged();
                RaiseEditorVisibility();
                Edited?.Invoke();
            }
        }

        // ── Видимость редакторов ─────────────────────────────────────────

        public bool IsApplicable => !_model.IsNotApplicable;

        public bool ShowScaleDots => IsApplicable && _model.Type == CharacterParameterType.Numeric && UseDots;
        public bool ShowScaleSlider => IsApplicable && _model.Type == CharacterParameterType.Numeric && !UseDots;
        public bool ShowText => IsApplicable && _model.Type == CharacterParameterType.Text;
        public bool ShowChoice => IsApplicable && _model.Type == CharacterParameterType.StateList;
        public bool ShowYesNo => IsApplicable && _model.Type == CharacterParameterType.Boolean;

        private void RaiseEditorVisibility()
        {
            this.RaisePropertyChanged(nameof(IsApplicable));
            this.RaisePropertyChanged(nameof(ShowScaleDots));
            this.RaisePropertyChanged(nameof(ShowScaleSlider));
            this.RaisePropertyChanged(nameof(ShowText));
            this.RaisePropertyChanged(nameof(ShowChoice));
            this.RaisePropertyChanged(nameof(ShowYesNo));
        }

        // ── Шкала ────────────────────────────────────────────────────────

        /// <summary>
        /// Кругляшками рисуются только короткие шкалы. Ставить сорок кружков
        /// на диапазон 0..100 бессмысленно — там остаётся линия, но с явной
        /// подписью значения, которой раньше не было.
        /// </summary>
        public const int MaxDots = 10;

        public int StepCount
        {
            get
            {
                var step = _model.Step > 0 ? _model.Step : 1;
                var span = _model.MaxValue - _model.MinValue;
                if (span <= 0) return 0;
                return (int)Math.Round(span / step);
            }
        }

        public bool UseDots => StepCount > 0 && StepCount <= MaxDots;

        public double MinValue => _model.MinValue;
        public double MaxValue => _model.MaxValue;
        public double Step => _model.Step > 0 ? _model.Step : 1;

        public double NumericValue
        {
            get => _model.NumericValue;
            set
            {
                if (Math.Abs(_model.NumericValue - value) < double.Epsilon) return;
                _model.NumericValue = value;
                this.RaisePropertyChanged();
                this.RaisePropertyChanged(nameof(ValueCaption));
                UpdateDotFill();
                Edited?.Invoke();
            }
        }

        /// <summary>Подпись значения: «3 / 5». Без неё шкала не читается.</summary>
        public string ValueCaption =>
            Format(_model.NumericValue) + " / " + Format(_model.MaxValue);

        private static string Format(double v) =>
            Math.Abs(v - Math.Round(v)) < 0.001
                ? ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture)
                : v.ToString("0.##", CultureInfo.InvariantCulture);

        /// <summary>
        /// Подсказка у самой шкалы: описания крайних точек, если заданы.
        /// «0 — трус, 5 — лезет в драку» объясняет параметр без документации.
        /// </summary>
        public string ScaleHint
        {
            get
            {
                var min = _model.MinDescription;
                var max = _model.MaxDescription;
                if (string.IsNullOrWhiteSpace(min) && string.IsNullOrWhiteSpace(max)) return string.Empty;
                return Format(_model.MinValue) + " — " + min + "   ·   " + Format(_model.MaxValue) + " — " + max;
            }
        }

        public bool HasScaleHint => !string.IsNullOrWhiteSpace(ScaleHint);

        private void RebuildDots()
        {
            Dots.Clear();
            if (!UseDots) return;

            var step = Step;
            for (int i = 1; i <= StepCount; i++)
            {
                var value = _model.MinValue + i * step;
                _model.ScalePoints.TryGetValue(value, out var hint);
                Dots.Add(new CharacterScaleDotViewModel(
                    value,
                    _model.NumericValue >= value - double.Epsilon,
                    hint ?? string.Empty));
            }
        }

        private void UpdateDotFill()
        {
            foreach (var dot in Dots)
                dot.IsFilled = _model.NumericValue >= dot.Value - double.Epsilon;
        }

        /// <summary>
        /// Клик по кругляшку. Повторный клик по текущему значению обнуляет
        /// шкалу — иначе выставленное по ошибке значение нечем снять.
        /// </summary>
        public void SetFromDot(double value)
        {
            NumericValue = Math.Abs(_model.NumericValue - value) < double.Epsilon
                ? _model.MinValue
                : value;
        }

        // ── Текст ────────────────────────────────────────────────────────

        public string TextValue
        {
            get => _model.TextValue;
            set { if (_model.TextValue == value) return; _model.TextValue = value; this.RaisePropertyChanged(); Edited?.Invoke(); }
        }

        // ── Выбор ────────────────────────────────────────────────────────

        public IReadOnlyList<string> States => _model.States;

        public int CurrentStateIndex
        {
            get => _model.CurrentStateIndex;
            set { if (_model.CurrentStateIndex == value) return; _model.CurrentStateIndex = value; this.RaisePropertyChanged(); Edited?.Invoke(); }
        }

        // ── Да или нет ───────────────────────────────────────────────────

        public bool BoolValue
        {
            get => _model.BoolValue;
            set
            {
                if (_model.BoolValue == value) return;
                _model.BoolValue = value;
                this.RaisePropertyChanged();
                this.RaisePropertyChanged(nameof(BoolCaption));
                Edited?.Invoke();
            }
        }

        public string BoolCaption => _model.BoolValue ? _model.TrueLabel : _model.FalseLabel;
    }
}
