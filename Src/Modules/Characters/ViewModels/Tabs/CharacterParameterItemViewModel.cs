using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Avalonia.Media;
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

        // ── Подпись ──────────────────────────────────────────────────────
        //
        // Подпись оформляется в анкете: жирная, своего цвета, со значком
        // слева. Подсказка поля — знаком «?» рядом с подписью.

        public FontWeight LabelFontWeight => _model.LabelBold ? FontWeight.Bold : FontWeight.Normal;

        /// <summary>Цвет подписи; null — цвет текста темы (берётся из стиля).</summary>
        public IBrush? LabelBrush => ParseBrush(_model.LabelColor);

        public bool HasLabelBrush => LabelBrush != null;

        public string LabelIcon => _model.LabelIcon;

        public bool HasLabelIcon => !string.IsNullOrWhiteSpace(_model.LabelIcon);

        private double _labelWidth = DefaultLabelWidth;

        /// <summary>Ширина подписи: в узких колонках строки раскладки она короче.</summary>
        public double LabelWidth
        {
            get => _labelWidth;
            set => this.RaiseAndSetIfChanged(ref _labelWidth, value);
        }

        public const double DefaultLabelWidth = 130;
        public const double NarrowLabelWidth = 96;

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
                this.RaisePropertyChanged(nameof(ShowNote));
                Edited?.Invoke();
            }
        }

        public bool HasValueNote => !string.IsNullOrWhiteSpace(_model.ValueNote);

        private bool _isNoteOpen;

        /// <summary>
        /// Строка примечания раскрыта значком на строке поля. Пустое
        /// примечание в покое не показывается: у большинства значений его
        /// нет, и пустая строка под каждым полем удваивала бы список.
        /// </summary>
        public bool IsNoteOpen
        {
            get => _isNoteOpen;
            set
            {
                if (_isNoteOpen == value) return;
                this.RaiseAndSetIfChanged(ref _isNoteOpen, value);
                this.RaisePropertyChanged(nameof(ShowNote));
            }
        }

        public bool ShowNote => IsApplicable && (HasValueNote || _isNoteOpen);

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

            // Новые типы подписаны прямо здесь, а не строкой из ресурсов:
            // ресурсы правятся сразу в трёх файлах (resx, ru.resx и
            // сгенерированный Designer), и разъехавшийся между ними ключ
            // ломает сборку молча. Появится перевод — переедут и эти.
            CharacterParameterType.Number => "Число",
            CharacterParameterType.LongText => "Описание",
            CharacterParameterType.MultiChoice => "Несколько вариантов",
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

        /// <summary>Свободное число: поле в одну строку, без шкалы и краёв.</summary>
        public bool ShowNumber => IsApplicable && _model.Type == CharacterParameterType.Number;

        /// <summary>Описание: то же текстовое значение, но поле высокое.</summary>
        public bool ShowLongText => IsApplicable && _model.Type == CharacterParameterType.LongText;

        /// <summary>Несколько вариантов: список галок вместо выпадающего списка.</summary>
        public bool ShowMultiChoice => IsApplicable && _model.Type == CharacterParameterType.MultiChoice;

        private void RaiseEditorVisibility()
        {
            this.RaisePropertyChanged(nameof(IsApplicable));
            this.RaisePropertyChanged(nameof(ShowScaleDots));
            this.RaisePropertyChanged(nameof(ShowScaleSlider));
            this.RaisePropertyChanged(nameof(ShowText));
            this.RaisePropertyChanged(nameof(ShowChoice));
            this.RaisePropertyChanged(nameof(ShowYesNo));
            this.RaisePropertyChanged(nameof(ShowNumber));
            this.RaisePropertyChanged(nameof(ShowLongText));
            this.RaisePropertyChanged(nameof(ShowMultiChoice));
            this.RaisePropertyChanged(nameof(ValueSummary));
            this.RaisePropertyChanged(nameof(ShowNote));
            RaiseDisplayVisibility();
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
                RaiseScaleValue();
                Edited?.Invoke();
            }
        }

        /// <summary>
        /// Значение для ползунка поля: в модель уходит число,
        /// округлённое до шага шкалы и прижатое к её краям.
        /// </summary>
        public double SliderValue
        {
            get => _model.NumericValue;
            set
            {
                var snapped = _model.MinValue + Math.Round((value - _model.MinValue) / Step) * Step;
                snapped = Math.Clamp(snapped, _model.MinValue, Math.Max(_model.MinValue, _model.MaxValue));

                if (Math.Abs(_model.NumericValue - snapped) < double.Epsilon) return;

                _model.NumericValue = snapped;
                RaiseScaleValue();
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
            set { if (_model.TextValue == value) return; _model.TextValue = value; this.RaisePropertyChanged(); this.RaisePropertyChanged(nameof(ValueSummary)); Edited?.Invoke(); }
        }

        // ── Выбор ────────────────────────────────────────────────────────

        public IReadOnlyList<string> States => _model.States;

        public int CurrentStateIndex
        {
            get => _model.CurrentStateIndex;
            set
            {
                if (_model.CurrentStateIndex == value) return;
                _model.CurrentStateIndex = value;
                this.RaisePropertyChanged();
                this.RaisePropertyChanged(nameof(CurrentStateName));
                this.RaisePropertyChanged(nameof(ValueSummary));

                // Выбор мог прийти из выпадающего списка — чипы идут следом.
                if (_choiceChips != null && !_syncingChoice)
                    foreach (var chip in _choiceChips)
                        chip.SetSelectedSilently(chip.Name == CurrentStateName);

                Edited?.Invoke();
            }
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
                this.RaisePropertyChanged(nameof(ValueSummary));
                Edited?.Invoke();
            }
        }

        public string BoolCaption => _model.BoolValue ? _model.TrueLabel : _model.FalseLabel;

        // ── Свободное число ──────────────────────────────────────────────
        //
        // Через строку, а не через double: пустое поле должно оставаться
        // пустым. Привязка к числу подставила бы в него ноль, и «рост не
        // указан» стало бы «рост нулевой» — разные утверждения, как и с
        // отметкой «неприменимо» выше.

        public string NumberText
        {
            get => _model.IsNotApplicable || !_model.HasNumber
                ? string.Empty
                : FormatNumber(_model.NumericValue);
            set
            {
                var text = (value ?? string.Empty).Trim();

                if (text.Length == 0)
                {
                    if (!_model.HasNumber) return;
                    _model.HasNumber = false;
                    _model.NumericValue = 0;
                    this.RaisePropertyChanged();
                    this.RaisePropertyChanged(nameof(ValueSummary));
                    Edited?.Invoke();
                    return;
                }

                // Запятая и точка равноправны: раскладка одна, а привычка
                // у каждого своя.
                text = text.Replace(',', '.');
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                {
                    // Не число — поле возвращается к прежнему значению, а не
                    // молча обнуляется.
                    this.RaisePropertyChanged();
                    return;
                }

                if (_model.HasNumber && Math.Abs(_model.NumericValue - parsed) < double.Epsilon) return;

                _model.HasNumber = true;
                _model.NumericValue = parsed;
                this.RaisePropertyChanged();
                this.RaisePropertyChanged(nameof(ValueSummary));
                Edited?.Invoke();
            }
        }


        // ── Пределы, шаг, знаки ──────────────────────────────────────────

        private int Decimals => Math.Clamp(_model.Decimals, 0, 6);

        private string FormatNumber(double value)
        {
            var format = Decimals == 0 ? "0" : "0." + new string('#', Decimals);
            return value.ToString(format, CultureInfo.CurrentCulture);
        }

        /// <summary>Привести число к пределам, шагу и числу знаков из анкеты.</summary>
        private double NormalizeNumber(double value)
        {
            value = Clamp(value);
            if (_model.UseStep) value = Clamp(Snap(value));
            return Math.Round(value, Decimals);
        }

        private double Clamp(double value)
        {
            if (_model.NumberMin is { } min && value < min) value = min;
            if (_model.NumberMax is { } max && value > max) value = max;
            return value;
        }

        private double LinearStep => _model.Step > 0 ? _model.Step : 1;

        /// <summary>Множитель шага «умножением»: меньше единицы шагать некуда.</summary>
        private double Factor => _model.Step > 1 ? _model.Step : 2;

        private List<double> StepValues() =>
            CharacterFieldDefinition.SplitStates(_model.StepValuesRaw)
                .Select(s => double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? (double?)v : null)
                .Where(v => v.HasValue)
                .Select(v => v!.Value)
                .Distinct()
                .OrderBy(v => v)
                .ToList();

        /// <summary>Ближайшее допустимое значение по правилу шага.</summary>
        private double Snap(double value)
        {
            switch (_model.StepRule)
            {
                case CharacterStepRule.Multiply:
                {
                    var origin = _model.NumberMin is { } m && m > 0 ? m : 1;
                    if (value <= origin) return origin;
                    var power = Math.Round(Math.Log(value / origin) / Math.Log(Factor));
                    return origin * Math.Pow(Factor, Math.Max(0, power));
                }

                case CharacterStepRule.List:
                {
                    var values = StepValues();
                    return values.Count == 0 ? value : values.OrderBy(v => Math.Abs(v - value)).First();
                }

                default:
                {
                    var origin = _model.NumberMin ?? 0;
                    return origin + Math.Round((value - origin) / LinearStep) * LinearStep;
                }
            }
        }

        /// <summary>
        /// Привести вписанное к пределам, шагу и числу знаков анкеты. Зовётся,
        /// когда поле ввода отпустили, а не на каждую букву: иначе «15» при
        /// шаге 10 превращалось бы в ноль ещё на первой цифре.
        /// </summary>
        public void CommitNumber(bool upper)
        {
            if (_model.IsNotApplicable) return;

            if (upper)
            {
                if (_model.NumericValueTo is not { } to) return;
                var normalized = NormalizeNumber(to);
                if (Math.Abs(normalized - to) > double.Epsilon)
                {
                    _model.NumericValueTo = normalized;
                    this.RaisePropertyChanged(nameof(ValueSummary));
                    Edited?.Invoke();
                }
                this.RaisePropertyChanged(nameof(NumberToText));
                return;
            }

            if (!_model.HasNumber) return;
            var value = NormalizeNumber(_model.NumericValue);
            if (Math.Abs(value - _model.NumericValue) > double.Epsilon)
            {
                _model.NumericValue = value;
                this.RaisePropertyChanged(nameof(ValueSummary));
                Edited?.Invoke();
            }
            this.RaisePropertyChanged(nameof(NumberText));
        }

        /// <summary>
        /// Шаг стрелками вверх и вниз: по правилу шага, а без шага — на
        /// единицу младшего знака. upper — верхняя граница диапазона.
        /// </summary>
        public void StepNumber(int direction, bool upper)
        {
            if (_model.IsNotApplicable || direction == 0) return;

            double current = upper
                ? _model.NumericValueTo ?? (_model.HasNumber ? _model.NumericValue : _model.NumberMin ?? 0)
                : _model.HasNumber ? _model.NumericValue : _model.NumberMin ?? 0;

            double next;
            if (!_model.UseStep)
            {
                next = current + direction * Math.Pow(10, -Decimals);
            }
            else
            {
                switch (_model.StepRule)
                {
                    case CharacterStepRule.Multiply:
                        next = direction > 0 ? Snap(current) * Factor : Snap(current) / Factor;
                        break;

                    case CharacterStepRule.List:
                    {
                        var values = StepValues();
                        if (values.Count == 0) return;
                        next = direction > 0
                            ? values.FirstOrDefault(v => v > current + 1e-9, values[^1])
                            : values.LastOrDefault(v => v < current - 1e-9, values[0]);
                        break;
                    }

                    default:
                        next = Snap(current) + direction * LinearStep;
                        break;
                }
            }

            next = NormalizeNumber(next);
            var text = next.ToString(CultureInfo.InvariantCulture);
            if (upper) NumberToText = text;
            else NumberText = text;
        }

        // ── Ширина поля ввода ────────────────────────────────────────────
        //
        // Поле ввода не тянется во всю строку: у имени и у места рождения
        // разная длина ответа. Ширину задаёт анкета — в конструкторе край
        // поля перетаскивают мышью.

        public const double DefaultTextWidth = 280;
        public const double DefaultLongTextWidth = 460;
        public const double DefaultNumberWidth = 90;
        public const double MinInputWidth = 60;
        public const double MaxInputWidth = 900;

        private double DefaultInputWidth => _model.Type switch
        {
            CharacterParameterType.LongText => DefaultLongTextWidth,
            CharacterParameterType.Number => DefaultNumberWidth,
            _ => DefaultTextWidth
        };

        /// <summary>Ширина поля ввода текста, описания или числа.</summary>
        public double InputWidth
        {
            get => _model.InputWidth is { } w && w > 0 ? w : DefaultInputWidth;
            set
            {
                var clamped = Math.Clamp(value, MinInputWidth, MaxInputWidth);
                if (_model.InputWidth.HasValue && Math.Abs(_model.InputWidth.Value - clamped) < 0.5) return;
                _model.InputWidth = clamped;
                this.RaisePropertyChanged();
            }
        }

        /// <summary>
        /// Поле показано в конструкторе анкет: у полей ввода есть ручка
        /// ширины. В карточке персонажа её нет — там ширина уже задана.
        /// </summary>
        public bool IsDesignerPreview { get; set; }

        /// <summary>Ширину поля дотянули и отпустили — конструктор запоминает её в анкете.</summary>
        public event Action<double>? InputWidthCommitted;

        public void CommitInputWidth() => InputWidthCommitted?.Invoke(InputWidth);

        // ── Вид поля ─────────────────────────────────────────────────────
        //
        // Вид задаётся в анкете, а здесь он сводится к тому, что показать:
        // «по умолчанию» превращается в конкретный вид по типу и длине шкалы.

        /// <summary>Шариков больше этого не рисуется: такая шкала становится полосой.</summary>
        public const int MaxBalls = 12;

        /// <summary>Делений у полосы не больше этого: длинная шкала сжимается в десятки.</summary>
        public const int MaxBarSegments = 20;

        public CharacterFieldDisplay EffectiveDisplay
        {
            get
            {
                var display = _model.Display;

                switch (_model.Type)
                {
                    case CharacterParameterType.Numeric:
                        if (display == CharacterFieldDisplay.Balls)
                            return StepCount <= MaxBalls ? CharacterFieldDisplay.Balls : CharacterFieldDisplay.Bar;
                        if (display == CharacterFieldDisplay.Bipolar)
                            return StepCount + 1 <= MaxBalls ? CharacterFieldDisplay.Bipolar : CharacterFieldDisplay.Slider;
                        if (display == CharacterFieldDisplay.Bar || display == CharacterFieldDisplay.Slider)
                            return display;
                        if (display == CharacterFieldDisplay.Stars || display == CharacterFieldDisplay.Glyph)
                            return StepCount <= MaxBalls ? display : CharacterFieldDisplay.Bar;
                        return UseDots ? CharacterFieldDisplay.Balls : CharacterFieldDisplay.Slider;

                    case CharacterParameterType.StateList:
                        if (display == CharacterFieldDisplay.Chips || display == CharacterFieldDisplay.Dropdown)
                            return display;
                        return _model.States.Count <= 6 ? CharacterFieldDisplay.Chips : CharacterFieldDisplay.Dropdown;

                    case CharacterParameterType.MultiChoice:
                        return CharacterFieldDisplay.Chips;

                    default:
                        return CharacterFieldDisplay.Auto;
                }
            }
        }

        private bool IsNumeric => _model.Type == CharacterParameterType.Numeric;

        public bool ShowBalls => IsApplicable && IsNumeric && EffectiveDisplay == CharacterFieldDisplay.Balls;
        public bool ShowBipolar => IsApplicable && IsNumeric && EffectiveDisplay == CharacterFieldDisplay.Bipolar;
        public bool ShowBar => IsApplicable && IsNumeric && EffectiveDisplay == CharacterFieldDisplay.Bar;
        public bool ShowSlider => IsApplicable && IsNumeric && EffectiveDisplay == CharacterFieldDisplay.Slider;

        /// <summary>Оценка значками: звёзды или свой значок анкеты.</summary>
        public bool ShowGlyphs => IsApplicable && IsNumeric &&
                                  (EffectiveDisplay == CharacterFieldDisplay.Stars ||
                                   EffectiveDisplay == CharacterFieldDisplay.Glyph);

        /// <summary>Значок оценки: у звёзд — звезда, у своего — заданный в анкете, по умолчанию сердечко.</summary>
        public string GlyphKey => EffectiveDisplay == CharacterFieldDisplay.Stars
            ? "star"
            : string.IsNullOrWhiteSpace(_model.RatingGlyph) ? "heart" : _model.RatingGlyph;

        public bool ShowChoiceChips => IsApplicable && _model.Type == CharacterParameterType.StateList &&
                                       EffectiveDisplay == CharacterFieldDisplay.Chips;
        public bool ShowChoiceList => IsApplicable && _model.Type == CharacterParameterType.StateList &&
                                      EffectiveDisplay == CharacterFieldDisplay.Dropdown;
        public bool ShowMultiChips => IsApplicable && _model.Type == CharacterParameterType.MultiChoice;

        public bool ShowColor => IsApplicable && _model.Type == CharacterParameterType.Color;

        /// <summary>
        /// Поле короткое и встаёт в две колонки: одна строка значения. Текст,
        /// описание, длинные списки и полюса с подписями занимают всю ширину.
        /// </summary>
        public bool IsWide =>
            _model.Type == CharacterParameterType.LongText ||
            _model.Type == CharacterParameterType.Text ||
            _model.Type == CharacterParameterType.MultiChoice ||
            (_model.Type == CharacterParameterType.StateList && EffectiveDisplay == CharacterFieldDisplay.Chips) ||
            (IsNumeric && EffectiveDisplay == CharacterFieldDisplay.Bipolar) ||
            (_model.Type == CharacterParameterType.Number && AllowedModeCount > 1) ||
            (_model.Type == CharacterParameterType.Color && _model.Palette.Count > 6);

        private void RaiseDisplayVisibility()
        {
            this.RaisePropertyChanged(nameof(ShowBalls));
            this.RaisePropertyChanged(nameof(ShowBipolar));
            this.RaisePropertyChanged(nameof(ShowBar));
            this.RaisePropertyChanged(nameof(ShowSlider));
            this.RaisePropertyChanged(nameof(ShowGlyphs));
            this.RaisePropertyChanged(nameof(GlyphKey));
            this.RaisePropertyChanged(nameof(ShowChoiceChips));
            this.RaisePropertyChanged(nameof(ShowChoiceList));
            this.RaisePropertyChanged(nameof(ShowMultiChips));
            this.RaisePropertyChanged(nameof(ShowColor));
            RaiseNumberModeVisibility();
        }

        /// <summary>Свой цвет поля; пусто — акцентный цвет темы.</summary>
        public IBrush? AccentBrush => ParseBrush(_model.AccentColor);

        private static IBrush? ParseBrush(string? hex) =>
            !string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex, out var color)
                ? new SolidColorBrush(color)
                : null;

        // ── Шарики ───────────────────────────────────────────────────────

        public int BallCount => Math.Max(1, StepCount);

        /// <summary>Сколько шариков заполнено: ноль — край шкалы.</summary>
        public int BallIndex
        {
            get => Math.Clamp((int)Math.Round((_model.NumericValue - _model.MinValue) / Step), 0, StepCount);
            set => NumericValue = _model.MinValue + Math.Clamp(value, 0, StepCount) * Step;
        }

        private IReadOnlyList<string>? _ballCaptions;

        /// <summary>
        /// Подписи шариков: слово деления из анкеты, а без него — «3 / 5».
        /// Нулевая пустая: ничего не выбрано, и подписывать нечего.
        /// </summary>
        public IReadOnlyList<string> BallCaptions => _ballCaptions ??= BuildBallCaptions();

        private IReadOnlyList<string> BuildBallCaptions()
        {
            var count = BallCount;
            var result = new List<string>(count + 1) { string.Empty };
            for (int i = 1; i <= count; i++)
            {
                var value = _model.MinValue + i * Step;
                result.Add(PointLabel(value) ?? i.ToString(CultureInfo.InvariantCulture) + " / " +
                           count.ToString(CultureInfo.InvariantCulture));
            }
            return result;
        }

        private string? PointLabel(double value)
        {
            foreach (var pair in _model.ScalePoints)
                if (Math.Abs(pair.Key - value) < 0.0001 && !string.IsNullOrWhiteSpace(pair.Value))
                    return pair.Value;
            return null;
        }

        // ── Полюса ───────────────────────────────────────────────────────
        //
        // Положений на одно больше, чем шагов: и крайний левый, и крайний
        // правый — законные ответы. «Не отмечено» хранится признаком HasNumber,
        // как у свободного числа: край шкалы и «не решил» — разные вещи.

        public int BipolarCount => Math.Max(2, StepCount + 1);

        public int BipolarIndex
        {
            get => _model.HasNumber
                ? Math.Clamp((int)Math.Round((_model.NumericValue - _model.MinValue) / Step), 0, StepCount) + 1
                : 0;
            set
            {
                if (value <= 0)
                {
                    if (!_model.HasNumber) return;
                    _model.HasNumber = false;
                    _model.NumericValue = _model.MinValue;
                }
                else
                {
                    var target = _model.MinValue + (Math.Clamp(value, 1, BipolarCount) - 1) * Step;
                    if (_model.HasNumber && Math.Abs(_model.NumericValue - target) < double.Epsilon) return;
                    _model.HasNumber = true;
                    _model.NumericValue = target;
                }

                RaiseScaleValue();
                Edited?.Invoke();
            }
        }

        private IReadOnlyList<string>? _bipolarCaptions;

        public IReadOnlyList<string> BipolarCaptions => _bipolarCaptions ??= BuildBipolarCaptions();

        private IReadOnlyList<string> BuildBipolarCaptions()
        {
            var count = BipolarCount;
            var result = new List<string>(count + 1) { string.Empty };
            for (int i = 1; i <= count; i++)
                result.Add(PointLabel(_model.MinValue + (i - 1) * Step) ?? string.Empty);
            return result;
        }

        public string LeftPole => _model.MinDescription;
        public string RightPole => _model.MaxDescription;

        // ── Полоса ───────────────────────────────────────────────────────

        public int BarCount => Math.Max(1, Math.Min(StepCount, MaxBarSegments));

        private double BarSpan => Math.Max(Step, _model.MaxValue - _model.MinValue);

        public int BarIndex
        {
            get => Math.Clamp((int)Math.Round((_model.NumericValue - _model.MinValue) / BarSpan * BarCount), 0, BarCount);
            set
            {
                var raw = _model.MinValue + Math.Clamp(value, 0, BarCount) * BarSpan / BarCount;
                // Значение встаёт на шаг шкалы: у полосы деление может
                // приходиться между шагами, а хранится всегда число шкалы.
                var snapped = _model.MinValue + Math.Round((raw - _model.MinValue) / Step) * Step;
                NumericValue = Math.Clamp(snapped, _model.MinValue, _model.MaxValue);
            }
        }

        private IReadOnlyList<string>? _barCaptions;

        public IReadOnlyList<string> BarCaptions => _barCaptions ??= BuildBarCaptions();

        private IReadOnlyList<string> BuildBarCaptions()
        {
            var count = BarCount;
            var result = new List<string>(count + 1);
            for (int i = 0; i <= count; i++)
            {
                var raw = _model.MinValue + i * BarSpan / count;
                var value = _model.MinValue + Math.Round((raw - _model.MinValue) / Step) * Step;
                result.Add(PointLabel(value) ?? Format(value) + " / " + Format(_model.MaxValue));
            }
            return result;
        }

        private void RaiseScaleValue()
        {
            this.RaisePropertyChanged(nameof(NumericValue));
            this.RaisePropertyChanged(nameof(SliderValue));
            this.RaisePropertyChanged(nameof(ValueCaption));
            this.RaisePropertyChanged(nameof(BallIndex));
            this.RaisePropertyChanged(nameof(BipolarIndex));
            this.RaisePropertyChanged(nameof(BarIndex));
            this.RaisePropertyChanged(nameof(ValueSummary));
            UpdateDotFill();
        }

        // ── Выбор чипами ─────────────────────────────────────────────────

        private ObservableCollection<CharacterChoiceOptionViewModel>? _choiceChips;
        private bool _syncingChoice;

        /// <summary>
        /// Варианты одиночного выбора чипами. Отмечен всегда ровно один:
        /// одиночный выбор хранится номером, и «ничего» у него не бывает —
        /// щелчок по отмеченному ничего не снимает.
        /// </summary>
        public ObservableCollection<CharacterChoiceOptionViewModel> ChoiceChips
        {
            get
            {
                if (_choiceChips != null) return _choiceChips;

                _choiceChips = new ObservableCollection<CharacterChoiceOptionViewModel>();
                for (int i = 0; i < _model.States.Count; i++)
                {
                    _choiceChips.Add(new CharacterChoiceOptionViewModel(
                        _model.States[i],
                        i == _model.CurrentStateIndex,
                        SelectChoiceChip));
                }

                return _choiceChips;
            }
        }

        private void SelectChoiceChip(string state, bool selected)
        {
            if (_syncingChoice || _choiceChips == null) return;

            _syncingChoice = true;
            try
            {
                if (selected)
                {
                    var index = _model.States.IndexOf(state);
                    if (index >= 0) CurrentStateIndex = index;
                }

                foreach (var chip in _choiceChips)
                    chip.SetSelectedSilently(chip.Name == CurrentStateName);
            }
            finally
            {
                _syncingChoice = false;
            }

            this.RaisePropertyChanged(nameof(ValueSummary));
        }

        public string CurrentStateName =>
            _model.CurrentStateIndex >= 0 && _model.CurrentStateIndex < _model.States.Count
                ? _model.States[_model.CurrentStateIndex]
                : string.Empty;

        // ── Цвет ─────────────────────────────────────────────────────────

        private ObservableCollection<CharacterColorOptionViewModel>? _paletteOptions;

        public ObservableCollection<CharacterColorOptionViewModel> PaletteOptions
        {
            get
            {
                if (_paletteOptions != null) return _paletteOptions;

                _paletteOptions = new ObservableCollection<CharacterColorOptionViewModel>();
                foreach (var hex in _model.Palette)
                {
                    var brush = ParseBrush(hex);
                    if (brush == null) continue;
                    _paletteOptions.Add(new CharacterColorOptionViewModel(
                        hex, brush, SameColor(hex, _model.ColorValue), SelectPaletteColor));
                }

                return _paletteOptions;
            }
        }

        public bool HasPalette => _model.Palette.Count > 0;
        public bool AllowCustomColor => _model.AllowCustomColor || _model.Palette.Count == 0;

        /// <summary>Выбранный цвет строкой #RRGGBB; пусто — не выбран.</summary>
        public string ColorValue
        {
            get => _model.ColorValue;
            set
            {
                var hex = (value ?? string.Empty).Trim();
                if (string.Equals(_model.ColorValue, hex, StringComparison.OrdinalIgnoreCase)) return;
                _model.ColorValue = hex;

                if (_paletteOptions != null)
                    foreach (var option in _paletteOptions)
                        option.SetSelectedSilently(SameColor(option.Hex, hex));

                this.RaisePropertyChanged();
                this.RaisePropertyChanged(nameof(ColorBrush));
                this.RaisePropertyChanged(nameof(HasColor));
                this.RaisePropertyChanged(nameof(ValueSummary));
                Edited?.Invoke();
            }
        }

        public bool HasColor => ParseBrush(_model.ColorValue) != null;
        public IBrush? ColorBrush => ParseBrush(_model.ColorValue);

        private void SelectPaletteColor(string hex, bool selected)
        {
            // Щелчок по выбранному цвету снимает выбор: так цвет можно
            // вернуть в «не указан», не заводя отдельной кнопки.
            ColorValue = selected ? hex : string.Empty;
        }

        private static bool SameColor(string? a, string? b) =>
            !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
            string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

        // ── Свободное число: способы ─────────────────────────────────────

        private static readonly (CharacterNumberMode Mode, CharacterNumberModes Flag, string Label, string Hint)[] ModeTable =
        {
            (CharacterNumberMode.Exact, CharacterNumberModes.Exact, "Точно", "Число известно точно"),
            (CharacterNumberMode.Approx, CharacterNumberModes.Approx, "Примерно", "«Около тридцати»: в карточке будет «около», а сравнивается само число"),
            (CharacterNumberMode.Range, CharacterNumberModes.Range, "От — до", "Диапазон: «от 25 до 35». Для сравнения берётся середина"),
            (CharacterNumberMode.Stage, CharacterNumberModes.Stage, "Этап", "Этап из списка анкеты: «подросток», «взрослый». Для сравнения берётся середина этапа"),
            (CharacterNumberMode.BirthYear, CharacterNumberModes.BirthYear, "Год рождения", "Вместо возраста — год, когда персонаж родился")
        };

        private CharacterNumberModes AllowedModes =>
            _model.NumberModes == CharacterNumberModes.None ? CharacterNumberModes.Exact : _model.NumberModes;

        private int AllowedModeCount => ModeTable.Count(m => AllowedModes.HasFlag(m.Flag));

        /// <summary>
        /// Способ, которым задано число. Если анкета больше не разрешает
        /// сохранённый способ, показывается первый разрешённый — само
        /// значение при этом не теряется.
        /// </summary>
        public CharacterNumberMode NumberMode
        {
            get
            {
                var stored = ModeTable.FirstOrDefault(m => m.Mode == _model.NumberMode);
                if (AllowedModes.HasFlag(stored.Flag)) return _model.NumberMode;
                return ModeTable.First(m => AllowedModes.HasFlag(m.Flag)).Mode;
            }
            set
            {
                if (_model.NumberMode == value) return;
                _model.NumberMode = value;

                if (_numberModeOptions != null)
                    foreach (var option in _numberModeOptions)
                        option.SetSelectedSilently(option.Mode == value);

                RaiseNumberModeVisibility();
                this.RaisePropertyChanged(nameof(ValueSummary));
                Edited?.Invoke();
            }
        }

        private ObservableCollection<CharacterNumberModeOptionViewModel>? _numberModeOptions;

        public ObservableCollection<CharacterNumberModeOptionViewModel> NumberModeOptions
        {
            get
            {
                if (_numberModeOptions != null) return _numberModeOptions;

                _numberModeOptions = new ObservableCollection<CharacterNumberModeOptionViewModel>();
                var current = NumberMode;
                foreach (var entry in ModeTable)
                {
                    if (!AllowedModes.HasFlag(entry.Flag)) continue;
                    _numberModeOptions.Add(new CharacterNumberModeOptionViewModel(
                        entry.Mode, entry.Label, entry.Hint, entry.Mode == current,
                        mode => NumberMode = mode));
                }

                return _numberModeOptions;
            }
        }

        public bool ShowNumberModes => ShowNumber && AllowedModeCount > 1;

        public bool ShowNumberSingle => ShowNumber &&
            (NumberMode == CharacterNumberMode.Exact || NumberMode == CharacterNumberMode.BirthYear);
        public bool ShowNumberApprox => ShowNumber && NumberMode == CharacterNumberMode.Approx;
        public bool ShowNumberRange => ShowNumber && NumberMode == CharacterNumberMode.Range;
        public bool ShowNumberStage => ShowNumber && NumberMode == CharacterNumberMode.Stage;

        /// <summary>Поле для числа нужно всем способам, кроме этапа.</summary>
        public bool ShowNumberMain => ShowNumber && NumberMode != CharacterNumberMode.Stage;

        public bool ShowUnit => ShowNumberMain && HasUnit;

        /// <summary>Единица рядом с полем; у года рождения единицы нет.</summary>
        public string UnitCaption => NumberMode == CharacterNumberMode.BirthYear ? "год" : _model.Unit;
        public bool HasUnit => !string.IsNullOrWhiteSpace(UnitCaption);

        private void RaiseNumberModeVisibility()
        {
            this.RaisePropertyChanged(nameof(NumberMode));
            this.RaisePropertyChanged(nameof(ShowNumberModes));
            this.RaisePropertyChanged(nameof(ShowNumberSingle));
            this.RaisePropertyChanged(nameof(ShowNumberApprox));
            this.RaisePropertyChanged(nameof(ShowNumberRange));
            this.RaisePropertyChanged(nameof(ShowNumberStage));
            this.RaisePropertyChanged(nameof(ShowNumberMain));
            this.RaisePropertyChanged(nameof(UnitCaption));
            this.RaisePropertyChanged(nameof(HasUnit));
            this.RaisePropertyChanged(nameof(ShowUnit));
        }

        /// <summary>
        /// Верхняя граница диапазона строкой — по той же причине, что и
        /// NumberText: пустое поле должно оставаться пустым.
        /// </summary>
        public string NumberToText
        {
            get => _model.NumericValueTo.HasValue
                ? FormatNumber(_model.NumericValueTo.Value)
                : string.Empty;
            set
            {
                var text = (value ?? string.Empty).Trim();

                if (text.Length == 0)
                {
                    if (!_model.NumericValueTo.HasValue) return;
                    _model.NumericValueTo = null;
                }
                else
                {
                    if (!double.TryParse(text.Replace(',', '.'), NumberStyles.Float,
                            CultureInfo.InvariantCulture, out var parsed))
                    {
                        this.RaisePropertyChanged();
                        return;
                    }


                    if (_model.NumericValueTo.HasValue &&
                        Math.Abs(_model.NumericValueTo.Value - parsed) < double.Epsilon) return;

                    _model.NumericValueTo = parsed;
                }

                this.RaisePropertyChanged();
                this.RaisePropertyChanged(nameof(ValueSummary));
                Edited?.Invoke();
            }
        }

        private ObservableCollection<CharacterChoiceOptionViewModel>? _stageOptions;
        private bool _syncingStage;

        /// <summary>Этапы анкеты чипами; отмечен тот, что выбран у персонажа.</summary>
        public ObservableCollection<CharacterChoiceOptionViewModel> StageOptions
        {
            get
            {
                if (_stageOptions != null) return _stageOptions;

                _stageOptions = new ObservableCollection<CharacterChoiceOptionViewModel>();
                foreach (var stage in _model.Stages)
                {
                    if (string.IsNullOrWhiteSpace(stage.Name)) continue;
                    _stageOptions.Add(new CharacterChoiceOptionViewModel(
                        stage.Name, stage.Name == _model.StageName, SelectStage)
                    {
                        Hint = StageRange(stage)
                    });
                }

                return _stageOptions;
            }
        }

        public bool HasStages => _model.Stages.Any(s => !string.IsNullOrWhiteSpace(s.Name));

        private static string StageRange(CharacterNumberStage stage) =>
            stage.To.HasValue
                ? Format(stage.From) + "–" + Format(stage.To.Value)
                : Format(stage.From) + "+";

        private void SelectStage(string name, bool selected)
        {
            if (_syncingStage || _stageOptions == null) return;

            _syncingStage = true;
            try
            {
                var target = selected ? name : string.Empty;
                if (_model.StageName != target)
                {
                    _model.StageName = target;
                    Edited?.Invoke();
                }

                foreach (var option in _stageOptions)
                    option.SetSelectedSilently(option.Name == _model.StageName);
            }
            finally
            {
                _syncingStage = false;
            }

            this.RaisePropertyChanged(nameof(ValueSummary));
        }

        /// <summary>
        /// Число, которым значение участвует в сравнении: примерное — как
        /// есть, диапазон — серединой, этап — серединой этапа. Год рождения
        /// в возраст не переводится: для этого нужен год сюжета.
        /// </summary>
        public double? ComparableNumber
        {
            get
            {
                if (_model.IsNotApplicable) return null;

                switch (_model.Type)
                {
                    case CharacterParameterType.Numeric:
                        return _model.NumericValue;

                    case CharacterParameterType.Number:
                        switch (NumberMode)
                        {
                            case CharacterNumberMode.Range:
                                if (!_model.HasNumber) return _model.NumericValueTo;
                                return _model.NumericValueTo.HasValue
                                    ? (_model.NumericValue + _model.NumericValueTo.Value) / 2.0
                                    : _model.NumericValue;
                            case CharacterNumberMode.Stage:
                                return _model.Stages.FirstOrDefault(s => s.Name == _model.StageName)?.Midpoint;
                            case CharacterNumberMode.BirthYear:
                                return null;
                            default:
                                return _model.HasNumber ? _model.NumericValue : null;
                        }

                    default:
                        return null;
                }
            }
        }

        // ── Значение одной строкой ───────────────────────────────────────

        /// <summary>
        /// Значение словами — для подсказок, сравнения и мест, где поле
        /// показывается без правки: «около 30 лет», «Холерик», «4 / 5».
        /// </summary>
        public string ValueSummary
        {
            get
            {
                if (_model.IsNotApplicable) return CharactersStrings.Param_NotApplicable;

                switch (_model.Type)
                {
                    case CharacterParameterType.Numeric:
                        if (EffectiveDisplay == CharacterFieldDisplay.Bipolar)
                        {
                            var index = BipolarIndex;
                            if (index <= 0) return string.Empty;
                            var label = BipolarCaptions[index];
                            return string.IsNullOrWhiteSpace(label)
                                ? index.ToString(CultureInfo.InvariantCulture) + " / " +
                                  BipolarCount.ToString(CultureInfo.InvariantCulture)
                                : label;
                        }
                        return PointLabel(_model.NumericValue) ?? ValueCaption;

                    case CharacterParameterType.Number:
                        return NumberSummary();

                    case CharacterParameterType.StateList:
                        return CurrentStateName;

                    case CharacterParameterType.MultiChoice:
                        return SelectedStatesCaption;

                    case CharacterParameterType.Boolean:
                        return BoolCaption;

                    case CharacterParameterType.Color:
                        return _model.ColorValue;

                    default:
                        return _model.TextValue;
                }
            }
        }

        private string NumberSummary()
        {
            string WithUnit(string number) =>
                string.IsNullOrWhiteSpace(_model.Unit) ? number : number + " " + _model.Unit;

            switch (NumberMode)
            {
                case CharacterNumberMode.Approx:
                    return _model.HasNumber ? "около " + WithUnit(Format(_model.NumericValue)) : string.Empty;

                case CharacterNumberMode.Range:
                    if (!_model.HasNumber && !_model.NumericValueTo.HasValue) return string.Empty;
                    var from = _model.HasNumber ? Format(_model.NumericValue) : "…";
                    var to = _model.NumericValueTo.HasValue ? Format(_model.NumericValueTo.Value) : "…";
                    return WithUnit(from + "–" + to);

                case CharacterNumberMode.Stage:
                    return _model.StageName;

                case CharacterNumberMode.BirthYear:
                    return _model.HasNumber ? "род. " + Format(_model.NumericValue) : string.Empty;

                default:
                    return _model.HasNumber ? WithUnit(Format(_model.NumericValue)) : string.Empty;
            }
        }

        // ── Несколько вариантов ──────────────────────────────────────────

        private ObservableCollection<CharacterChoiceOptionViewModel>? _options;

        /// <summary>
        /// Варианты с галками. Собираются лениво: у поля другого типа их нет
        /// вовсе, а список параметров бывает длинным.
        /// </summary>
        public ObservableCollection<CharacterChoiceOptionViewModel> Options
        {
            get
            {
                if (_options != null) return _options;

                _options = new ObservableCollection<CharacterChoiceOptionViewModel>();
                foreach (var state in _model.States)
                {
                    var option = new CharacterChoiceOptionViewModel(
                        state,
                        _model.SelectedStates.Contains(state),
                        ToggleOption);
                    _options.Add(option);
                }

                return _options;
            }
        }

        private void ToggleOption(string state, bool selected)
        {
            if (selected)
            {
                if (_model.SelectedStates.Contains(state)) return;
                _model.SelectedStates.Add(state);
            }
            else
            {
                if (!_model.SelectedStates.Remove(state)) return;
            }

            this.RaisePropertyChanged(nameof(SelectedStatesCaption));
            this.RaisePropertyChanged(nameof(ValueSummary));
            Edited?.Invoke();
        }

        /// <summary>Отмеченное одной строкой — для свёрнутого вида и сравнения.</summary>
        public string SelectedStatesCaption => string.Join(", ", _model.SelectedStates);
    }

    /// <summary>
    /// Один вариант в поле с несколькими ответами. Своя вью-модель, а не
    /// голая строка: галке нужно состояние, а списку — знать, что её
    /// переключили.
    /// </summary>
    public class CharacterChoiceOptionViewModel : ReactiveObject
    {
        private readonly Action<string, bool> _toggle;

        public CharacterChoiceOptionViewModel(string name, bool isSelected, Action<string, bool> toggle)
        {
            Name = name;
            _isSelected = isSelected;
            _toggle = toggle;
        }

        public string Name { get; }

        /// <summary>Пояснение к варианту: у этапа — его границы, «13–17».</summary>
        public string Hint { get; init; } = string.Empty;

        public bool HasHint => !string.IsNullOrWhiteSpace(Hint);

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                this.RaiseAndSetIfChanged(ref _isSelected, value);
                _toggle(Name, value);
            }
        }

        /// <summary>
        /// Поставить отметку без оповещения владельца — когда её меняет он
        /// сам: соседний вариант одиночного выбора, выбор из списка.
        /// </summary>
        public void SetSelectedSilently(bool value) =>
            this.RaiseAndSetIfChanged(ref _isSelected, value, nameof(IsSelected));
    }

    /// <summary>Цвет палитры поля — кружком в карточке.</summary>
    public class CharacterColorOptionViewModel : ReactiveObject
    {
        private readonly Action<string, bool> _toggle;

        public CharacterColorOptionViewModel(string hex, IBrush brush, bool isSelected, Action<string, bool> toggle)
        {
            Hex = hex;
            Brush = brush;
            _isSelected = isSelected;
            _toggle = toggle;
        }

        public string Hex { get; }
        public IBrush Brush { get; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                this.RaiseAndSetIfChanged(ref _isSelected, value);
                _toggle(Hex, value);
            }
        }

        public void SetSelectedSilently(bool value) =>
            this.RaiseAndSetIfChanged(ref _isSelected, value, nameof(IsSelected));
    }

    /// <summary>
    /// Способ задать число — кнопкой переключателя. Отмечен всегда один:
    /// щелчок по отмеченному ничего не снимает.
    /// </summary>
    public class CharacterNumberModeOptionViewModel : ReactiveObject
    {
        private readonly Action<CharacterNumberMode> _select;

        public CharacterNumberModeOptionViewModel(
            CharacterNumberMode mode, string label, string hint, bool isSelected,
            Action<CharacterNumberMode> select)
        {
            Mode = mode;
            Label = label;
            Hint = hint;
            _isSelected = isSelected;
            _select = select;
        }

        public CharacterNumberMode Mode { get; }
        public string Label { get; }
        public string Hint { get; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (!value)
                {
                    // Снять отметку щелчком нельзя: способ задан всегда.
                    // Оповещение возвращает кнопке прежнее состояние.
                    this.RaisePropertyChanged();
                    return;
                }

                if (_isSelected) return;
                this.RaiseAndSetIfChanged(ref _isSelected, value);
                _select(Mode);
            }
        }

        public void SetSelectedSilently(bool value) =>
            this.RaiseAndSetIfChanged(ref _isSelected, value, nameof(IsSelected));
    }
}
