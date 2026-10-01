using Avalonia.Media;
using Newtonsoft.Json;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Writersword.Modules.Characters.Interfaces;
using Writersword.Modules.Characters.Models;
using Writersword.Modules.Characters.Models.Enums;
using Writersword.Modules.Characters.ViewModels.Tabs;

namespace Writersword.Modules.Characters.ViewModels.Anketas
{
    /// <summary>
    /// Поле анкеты в конструкторе. Правится копия определения; наружу оно
    /// отдаётся через ToField при сохранении анкеты.
    ///
    /// Здесь настраивается всё, что потом одинаково у всех персонажей: имя,
    /// подсказка, группа, края и шаг шкалы, подписи делений, полюса, варианты,
    /// единица, способы задать число, этапы, палитра, вид и цвет поля. Каждая
    /// правка тут же пересобирает предпросмотр — так видно, что получится в
    /// карточке, без перехода к персонажу.
    /// </summary>
    public class AnketaFieldEditorViewModel : ReactiveObject
    {
        private readonly CharacterAnketaField _field;
        private readonly ICharacterAnketaService _anketaService;
        private readonly Action _changed;
        private bool _suppress;

        public AnketaFieldEditorViewModel(
            CharacterAnketaField field,
            ICharacterAnketaService anketaService,
            Action changed)
        {
            _field = Clone(field);
            _anketaService = anketaService;
            _changed = changed;

            _scaleLabels = string.Join(", ", _field.ScalePoints
                .OrderBy(p => p.Key)
                .Select(p => p.Value));
            _stagesRaw = FormatStages(_field.Stages);
            _paletteRaw = string.Join(", ", _field.Palette);

            BuildDisplayOptions();
            BuildColorOptions();
            BuildScaleCountOptions();
            BuildLabelColorOptions();
            BuildStepRuleOptions();
        }

        /// <summary>
        /// Конструктор анкеты, в которой стоит поле. Окошко настроек поля
        /// живёт во всплывающем слое, вне дерева вью, и до общих вещей анкеты
        /// — списка групп, буфера вида — добирается через него.
        /// </summary>
        public AnketaSheetViewModel? Owner { get; internal set; }

        /// <summary>Ключ поля в раскладке анкеты.</summary>
        public string Key => _field.Key;

        private static CharacterAnketaField Clone(CharacterAnketaField field) =>
            JsonConvert.DeserializeObject<CharacterAnketaField>(JsonConvert.SerializeObject(field))!;

        /// <summary>Идентификатор поля; пусто, пока анкету не сохранили.</summary>
        public string FieldId => _field.FieldId;

        /// <summary>
        /// Задать идентификатор при сохранении. Задаётся один раз: переименование
        /// вопроса не должно рвать связь со значениями в карточках.
        /// </summary>
        internal void AssignFieldId(string id)
        {
            if (string.IsNullOrWhiteSpace(_field.FieldId)) _field.FieldId = id;
        }

        /// <summary>Поле как есть сейчас — для копирования и дублирования.</summary>
        public CharacterAnketaField Snapshot() => Clone(ToField(_field.Order));

        private void NotifyEdited()
        {
            if (_suppress) return;

            // Предпросмотр строится только у поля, чьё окошко открывали:
            // карточек и полей много, и держать превью у каждого незачем.
            if (_preview != null) RebuildPreview();
            RaiseCardLook();
            _changed();
        }

        // ── Строка поля в карточке ───────────────────────────────────────

        private bool _isDragSource;

        /// <summary>Поле сейчас тащат — строка приглушена.</summary>
        public bool IsDragSource { get => _isDragSource; set => this.RaiseAndSetIfChanged(ref _isDragSource, value); }

        /// <summary>
        /// Цвет типа по умолчанию: у каждого типа свой, чтобы карточка
        /// читалась с одного взгляда. Свой цвет поля перекрывает его.
        /// </summary>
        public static string TypeColorOf(CharacterParameterType type) => type switch
        {
            CharacterParameterType.Numeric => "#E08A3C",
            CharacterParameterType.Number => "#5B7FA8",
            CharacterParameterType.StateList => "#8A6FC0",
            CharacterParameterType.MultiChoice => "#4FA39A",
            CharacterParameterType.Boolean => "#7FB069",
            CharacterParameterType.Color => "#C06FA0",
            CharacterParameterType.LongText => "#D4A33B",
            _ => "#9A8F85"
        };

        private Color TypeColor
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(_field.AccentColor) && Color.TryParse(_field.AccentColor, out var own))
                    return own;
                return Color.Parse(TypeColorOf(_field.Type));
            }
        }

        /// <summary>Значок типа — этим цветом.</summary>
        public IBrush TypeBrush => new SolidColorBrush(TypeColor);

        /// <summary>Подложка значка типа — тот же цвет, приглушённый.</summary>
        public IBrush TypeSoftBrush
        {
            get
            {
                var c = TypeColor;
                return new SolidColorBrush(Color.FromArgb(0x33, c.R, c.G, c.B));
            }
        }

        /// <summary>
        /// Коротко, что внутри поля: «из 5», «0–120», «лет», «4 вар.». Видно в
        /// карточке без открытия настроек.
        /// </summary>
        public string ShortSummary => _field.Type switch
        {
            CharacterParameterType.Numeric =>
                Math.Abs(_field.DefaultMinValue) < 0.0001 && Math.Abs(StepValue - 1) < 0.0001
                    ? "из " + F(_field.DefaultMaxValue)
                    : F(_field.DefaultMinValue) + "–" + F(_field.DefaultMaxValue),
            CharacterParameterType.Number => string.IsNullOrWhiteSpace(_field.Unit) ? "число" : _field.Unit,
            CharacterParameterType.StateList or CharacterParameterType.MultiChoice =>
                CharacterFieldDefinition.SplitStates(_field.StatesRaw).Count + " вар.",
            CharacterParameterType.Boolean => "да / нет",
            CharacterParameterType.Color => _field.Palette.Count + " цв.",
            CharacterParameterType.LongText => "абзацы",
            _ => "строка"
        };

        private void RaiseCardLook()
        {
            this.RaisePropertyChanged(nameof(ShortSummary));
            this.RaisePropertyChanged(nameof(TypeBrush));
            this.RaisePropertyChanged(nameof(TypeSoftBrush));
        }

        // ── Общее ────────────────────────────────────────────────────────

        public string Name
        {
            get => _field.Name;
            set
            {
                if (_field.Name == value) return;
                _field.Name = value ?? string.Empty;
                this.RaisePropertyChanged();
                this.RaisePropertyChanged(nameof(DisplayName));
                NotifyEdited();
            }
        }

        /// <summary>Имя в списке полей: безымянное поле всё равно видно.</summary>
        public string DisplayName => string.IsNullOrWhiteSpace(_field.Name) ? "Без названия" : _field.Name;

        public string Description
        {
            get => _field.Description;
            set
            {
                if (_field.Description == value) return;
                _field.Description = value ?? string.Empty;
                this.RaisePropertyChanged();
                NotifyEdited();
            }
        }

        public string GroupName
        {
            get => _field.GroupName;
            set
            {
                var v = (value ?? string.Empty).Trim();
                if (_field.GroupName == v) return;
                _field.GroupName = v;
                this.RaisePropertyChanged();
                NotifyEdited();
            }
        }

        public bool IsComparable
        {
            get => _field.IsComparable;
            set
            {
                if (_field.IsComparable == value) return;
                _field.IsComparable = value;
                this.RaisePropertyChanged();
                NotifyEdited();
            }
        }

        public CharacterParameterType Type => _field.Type;

        public string TypeLabel => TypeLabelOf(_field.Type);

        public static string TypeLabelOf(CharacterParameterType type) => type switch
        {
            CharacterParameterType.Numeric => "Шкала",
            CharacterParameterType.Text => "Текст",
            CharacterParameterType.StateList => "Выбор",
            CharacterParameterType.Boolean => "Да / нет",
            CharacterParameterType.Number => "Число",
            CharacterParameterType.LongText => "Описание",
            CharacterParameterType.MultiChoice => "Несколько",
            CharacterParameterType.Color => "Цвет",
            _ => string.Empty
        };

        public bool IsScale => _field.Type == CharacterParameterType.Numeric;
        public bool IsChoice => _field.Type == CharacterParameterType.StateList;
        public bool IsMulti => _field.Type == CharacterParameterType.MultiChoice;
        public bool HasStates => IsChoice || IsMulti;
        public bool IsBool => _field.Type == CharacterParameterType.Boolean;
        public bool IsNumber => _field.Type == CharacterParameterType.Number;
        public bool IsColor => _field.Type == CharacterParameterType.Color;

        /// <summary>У поля есть выбор вида: шкала и одиночный выбор.</summary>
        public bool HasDisplayChoice => IsScale || IsChoice;

        /// <summary>Свой цвет поля имеет смысл там, где что-то закрашивается.</summary>
        public bool HasAccent => IsScale || IsChoice || IsMulti;

        // ── Строка списка полей ──────────────────────────────────────────

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set => this.RaiseAndSetIfChanged(ref _isSelected, value);
        }

        private bool _showGroupHeader;

        /// <summary>Над полем в списке стоит заголовок его группы.</summary>
        public bool ShowGroupHeader
        {
            get => _showGroupHeader;
            set => this.RaiseAndSetIfChanged(ref _showGroupHeader, value);
        }

        public string GroupHeader => string.IsNullOrWhiteSpace(_field.GroupName) ? "Без группы" : _field.GroupName;

        internal void RaiseGroupHeader() => this.RaisePropertyChanged(nameof(GroupHeader));

        // ── Вид ──────────────────────────────────────────────────────────

        public ObservableCollection<AnketaOptionViewModel<CharacterFieldDisplay>> DisplayOptions { get; } = new();

        private void BuildDisplayOptions()
        {
            DisplayOptions.Clear();

            void Add(CharacterFieldDisplay value, string label, string hint) =>
                DisplayOptions.Add(new AnketaOptionViewModel<CharacterFieldDisplay>(
                    value, label, hint, _field.Display == value, SetDisplay)
                {
                    IconPath = DisplayIconPath(value)
                });

            if (IsScale)
            {
                Add(CharacterFieldDisplay.Auto, "Авто", "Короткая шкала — шарики, длинная — ползунок");
                Add(CharacterFieldDisplay.Balls, "Шарики", "Заполняются до выбранного. Для эмоций, черт, навыков: «вспыльчивость 4 из 5»");
                Add(CharacterFieldDisplay.Bipolar, "Полюса", "Одно положение между двумя крайностями: «интроверт — экстраверт». Подписи крайностей задаются ниже");
                Add(CharacterFieldDisplay.Bar, "Деления", "Полоса из делений — для шкал подлиннее, где шариков было бы много");
                Add(CharacterFieldDisplay.Slider, "Ползунок", "Для длинных шкал: возраст, проценты, 0–100");
                Add(CharacterFieldDisplay.Stars, "Звёзды", "Оценка звёздами: «харизма три из пяти»");
                Add(CharacterFieldDisplay.Glyph, "Свой значок", "Сердечки, молнии, черепа — или свой рисунок из файла. Значок выбирается ниже");
            }
            else if (IsChoice)
            {
                Add(CharacterFieldDisplay.Auto, "Авто", "До шести вариантов — чипы, больше — список");
                Add(CharacterFieldDisplay.Chips, "Чипы", "Все варианты на виду, выбранный подсвечен");
                Add(CharacterFieldDisplay.Dropdown, "Список", "Выпадающий список — для длинного перечня вариантов");
            }
        }

        public CharacterFieldDisplay Display => _field.Display;

        /// <summary>Значок вида — кнопки выбора вида рисуются значками, а не словами.</summary>
        public static string DisplayIconPath(CharacterFieldDisplay display) => display switch
        {
            CharacterFieldDisplay.Balls => "M5 12m-3 0a3 3 0 1 0 6 0a3 3 0 1 0 -6 0M12 12m-3 0a3 3 0 1 0 6 0a3 3 0 1 0 -6 0M19 12m-3 0a3 3 0 1 0 6 0a3 3 0 1 0 -6 0",
            CharacterFieldDisplay.Bar => "M2 9h4v6H2zM8 9h4v6H8zM14 9h4v6h-4zM20 9h2v6h-2z",
            CharacterFieldDisplay.Slider => "M2 11h20v2H2zM9 12m-4 0a4 4 0 1 0 8 0a4 4 0 1 0 -8 0",
            CharacterFieldDisplay.Bipolar => "M2 11h20v2H2zM2 7h2v10H2zM20 7h2v10h-2zM14 12m-3 0a3 3 0 1 0 6 0a3 3 0 1 0 -6 0",
            CharacterFieldDisplay.Chips => "M6 7h5a5 5 0 0 1 0 10H6A5 5 0 0 1 6 7zM17 7h1a5 5 0 0 1 0 10h-1z",
            CharacterFieldDisplay.Dropdown => "M3 5h18v2H3zM3 11h18v2H3zM3 17h18v2H3z",
            CharacterFieldDisplay.Stars => "M12 17.27L18.18 21l-1.64-7.03L22 9.24l-7.19-.61L12 2 9.19 8.63 2 9.24l5.46 4.73L5.82 21z",
            CharacterFieldDisplay.Glyph => "M12 21.35l-1.45-1.32C5.4 15.36 2 12.28 2 8.5 2 5.42 4.42 3 7.5 3c1.74 0 3.41.81 4.5 2.09C13.09 3.81 14.76 3 16.5 3 19.58 3 22 5.42 22 8.5c0 3.78-3.4 6.86-8.55 11.54L12 21.35z",
            _ => "M19 9l1.25-2.75L23 5l-2.75-1.25L19 1l-1.25 2.75L15 5l2.75 1.25L19 9zm-7.5.5L9 4 6.5 9.5 1 12l5.5 2.5L9 20l2.5-5.5L17 12l-5.5-2.5zM19 15l-1.25 2.75L15 19l2.75 1.25L19 23l1.25-2.75L23 19l-2.75-1.25L19 15z"
        };

        /// <summary>Значок типа поля — в окошке настроек и на кнопках добавления.</summary>
        public static string TypeIconPath(CharacterParameterType type) => type switch
        {
            CharacterParameterType.Numeric => "M5 12m-3 0a3 3 0 1 0 6 0a3 3 0 1 0 -6 0M12 12m-3 0a3 3 0 1 0 6 0a3 3 0 1 0 -6 0M19 12m-3 0a3 3 0 1 0 6 0a3 3 0 1 0 -6 0",
            CharacterParameterType.Number => "M10 3L8 21h2l2-18zM16 3l-2 18h2l2-18zM4 8h17v2H4zM3 14h17v2H3z",
            CharacterParameterType.StateList => "M12 7c-2.76 0-5 2.24-5 5s2.24 5 5 5 5-2.24 5-5-2.24-5-5-5zm0-5C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm0 18c-4.42 0-8-3.58-8-8s3.58-8 8-8 8 3.58 8 8-3.58 8-8 8z",
            CharacterParameterType.MultiChoice => "M9,16.17L4.83,12l-1.42,1.41L9,19 21,7l-1.41,-1.41z",
            CharacterParameterType.Boolean => "M17 7H7c-2.76 0-5 2.24-5 5s2.24 5 5 5h10c2.76 0 5-2.24 5-5s-2.24-5-5-5zm0 8c-1.66 0-3-1.34-3-3s1.34-3 3-3 3 1.34 3 3-1.34 3-3 3z",
            CharacterParameterType.Color => "M12 3c-4.97 0-9 4.03-9 9s4.03 9 9 9c.83 0 1.5-.67 1.5-1.5 0-.39-.15-.74-.39-1.01-.23-.26-.38-.61-.38-.99 0-.83.67-1.5 1.5-1.5H16c2.76 0 5-2.24 5-5 0-4.42-4.03-8-9-8zm-5.5 9c-.83 0-1.5-.67-1.5-1.5S5.67 9 6.5 9 8 9.67 8 10.5 7.33 12 6.5 12zm3-4C8.67 8 8 7.33 8 6.5S8.67 5 9.5 5s1.5.67 1.5 1.5S10.33 8 9.5 8zm5 0c-.83 0-1.5-.67-1.5-1.5S13.67 5 14.5 5s1.5.67 1.5 1.5S15.33 8 14.5 8zm3 4c-.83 0-1.5-.67-1.5-1.5S16.67 9 17.5 9s1.5.67 1.5 1.5-.67 1.5-1.5 1.5z",
            CharacterParameterType.LongText => "M14 17H4v2h10v-2zm6-8H4v2h16V9zM4 15h16v-2H4v2zM4 5v2h16V5H4z",
            _ => "M5 4v3h5.5v12h3V7H19V4z"
        };

        public string TypeIcon => TypeIconPath(_field.Type);

        private bool _isMoreOpen;

        /// <summary>В окошке настроек раскрыто «Ещё» — редкие настройки.</summary>
        public bool IsMoreOpen
        {
            get => _isMoreOpen;
            set => this.RaiseAndSetIfChanged(ref _isMoreOpen, value);
        }

        /// <summary>
        /// Шкала «от нуля до N с шагом 1» одним нажатием: так задают шкалу чаще
        /// всего, и три поля ввода ради этого не нужны.
        /// </summary>
        public void SetScaleCount(int count)
        {
            if (!IsScale || count <= 0) return;

            _field.DefaultMinValue = 0;
            _field.DefaultMaxValue = count;
            _field.Step = 1;
            this.RaisePropertyChanged(nameof(MinValue));
            this.RaisePropertyChanged(nameof(MaxValue));
            this.RaisePropertyChanged(nameof(StepValue));
            this.RaisePropertyChanged(nameof(ScaleSummary));
            RefreshScaleCount();
            NotifyEdited();
        }

        private static readonly int[] ScaleCounts = { 3, 5, 7, 10 };

        /// <summary>Длина шкалы одной кнопкой: 3, 5, 7, 10 делений.</summary>
        public ObservableCollection<AnketaOptionViewModel<int>> ScaleCountOptions { get; } = new();

        private void BuildScaleCountOptions()
        {
            ScaleCountOptions.Clear();
            if (!IsScale) return;

            foreach (var count in ScaleCounts)
                ScaleCountOptions.Add(new AnketaOptionViewModel<int>(
                    count,
                    count.ToString(CultureInfo.InvariantCulture),
                    "Шкала из " + count.ToString(CultureInfo.InvariantCulture) + " делений",
                    IsScaleOf(count),
                    SetScaleCount));
        }

        private bool IsScaleOf(int count) =>
            Math.Abs(_field.DefaultMinValue) < 0.0001 &&
            Math.Abs(StepValue - 1) < 0.0001 &&
            Math.Abs(_field.DefaultMaxValue - count) < 0.0001;

        /// <summary>Подсветить кнопку длины, если шкала совпала с одной из них.</summary>
        private void RefreshScaleCount()
        {
            foreach (var option in ScaleCountOptions)
                option.SetSelectedSilently(IsScaleOf(option.Value));
        }

        private void SetDisplay(CharacterFieldDisplay value)
        {
            if (_field.Display == value) return;
            _field.Display = value;
            foreach (var option in DisplayOptions)
                option.SetSelectedSilently(option.Value == value);
            this.RaisePropertyChanged(nameof(Display));
            this.RaisePropertyChanged(nameof(IsBipolar));
            this.RaisePropertyChanged(nameof(IsGlyphDisplay));
            this.RaisePropertyChanged(nameof(ScaleLabelsHint));
            NotifyEdited();
        }

        public bool IsBipolar => IsScale && _field.Display == CharacterFieldDisplay.Bipolar;

        /// <summary>Оценка своим значком — показывается выбор значка.</summary>
        public bool IsGlyphDisplay => IsScale && _field.Display == CharacterFieldDisplay.Glyph;

        // ── Подпись ──────────────────────────────────────────────────────

        public bool LabelBold
        {
            get => _field.LabelBold;
            set
            {
                if (_field.LabelBold == value) return;
                _field.LabelBold = value;
                this.RaisePropertyChanged();
                NotifyEdited();
            }
        }

        public string LabelColor => _field.LabelColor;

        private static readonly string[] LabelColorPresets =
        {
            "#D4C4A8", "#E08A3C", "#C75B5B", "#D4A33B", "#7FB069", "#4FA39A", "#5B7FA8", "#8A6FC0", "#C06FA0"
        };

        /// <summary>Цвета подписи. Повторный щелчок по выбранному возвращает цвет темы.</summary>
        public ObservableCollection<AnketaColorOptionViewModel> LabelColorOptions { get; } = new();

        private void BuildLabelColorOptions()
        {
            LabelColorOptions.Clear();
            foreach (var hex in LabelColorPresets)
            {
                if (!Color.TryParse(hex, out var color)) continue;
                LabelColorOptions.Add(new AnketaColorOptionViewModel(
                    hex, new SolidColorBrush(color),
                    string.Equals(hex, _field.LabelColor, StringComparison.OrdinalIgnoreCase),
                    SetLabelColor));
            }
        }

        public void SetLabelColor(string hex)
        {
            if (string.Equals(_field.LabelColor, hex, StringComparison.OrdinalIgnoreCase)) hex = string.Empty;
            _field.LabelColor = hex ?? string.Empty;
            foreach (var option in LabelColorOptions)
                option.SetSelectedSilently(string.Equals(option.Hex, hex, StringComparison.OrdinalIgnoreCase));
            this.RaisePropertyChanged(nameof(LabelColor));
            NotifyEdited();
        }

        /// <summary>Значок слева от подписи; пусто — без значка.</summary>
        public string LabelIcon
        {
            get => _field.LabelIcon;
            set
            {
                var v = value ?? string.Empty;
                if (_field.LabelIcon == v) return;
                _field.LabelIcon = v;
                this.RaisePropertyChanged();
                this.RaisePropertyChanged(nameof(HasLabelIcon));
                NotifyEdited();
            }
        }

        public bool HasLabelIcon => !string.IsNullOrWhiteSpace(_field.LabelIcon);

        /// <summary>Значок оценки для вида «свой значок»; пусто — сердечко.</summary>
        public string RatingGlyph
        {
            get => string.IsNullOrWhiteSpace(_field.RatingGlyph) ? "heart" : _field.RatingGlyph;
            set
            {
                var v = value ?? string.Empty;
                if (_field.RatingGlyph == v) return;
                _field.RatingGlyph = v;
                this.RaisePropertyChanged();
                NotifyEdited();
            }
        }

        // ── Свободное число: знаки, пределы, шаг ─────────────────────────

        /// <summary>Дробное число — с знаками после запятой.</summary>
        public bool IsDecimal
        {
            get => _field.Decimals > 0;
            set
            {
                if (IsDecimal == value) return;
                _field.Decimals = value ? 2 : 0;
                this.RaisePropertyChanged();
                this.RaisePropertyChanged(nameof(IsInteger));
                this.RaisePropertyChanged(nameof(DecimalsValue));
                NotifyEdited();
            }
        }

        public bool IsInteger
        {
            get => !IsDecimal;
            set => IsDecimal = !value;
        }

        /// <summary>Варианты числа знаков после запятой — для выпадающего списка.</summary>
        public IReadOnlyList<int> DecimalsOptions { get; } = new[] { 1, 2, 3, 4, 5, 6 };

        /// <summary>Сколько знаков после запятой у дробного числа: от одного до шести.</summary>
        public int DecimalsValue
        {
            get => Math.Max(1, _field.Decimals);
            set
            {
                var v = Math.Clamp(value, 1, 6);
                if (_field.Decimals == v) return;
                _field.Decimals = v;
                this.RaisePropertyChanged();
                this.RaisePropertyChanged(nameof(IsDecimal));
                this.RaisePropertyChanged(nameof(IsInteger));
                NotifyEdited();
            }
        }

        private static string FormatOptional(double? value) =>
            value.HasValue ? value.Value.ToString("0.######", CultureInfo.InvariantCulture) : string.Empty;

        private static bool TryParseOptional(string? text, out double? value)
        {
            value = null;
            var raw = (text ?? string.Empty).Trim().Replace(',', '.');
            if (raw.Length == 0) return true;
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return false;
            value = parsed;
            return true;
        }

        /// <summary>Наименьшее допустимое число строкой; пусто — без границы.</summary>
        public string NumberMinText
        {
            get => FormatOptional(_field.NumberMin);
            set
            {
                if (!TryParseOptional(value, out var parsed)) { this.RaisePropertyChanged(); return; }
                if (_field.NumberMin == parsed) return;
                _field.NumberMin = parsed;
                this.RaisePropertyChanged();
                NotifyEdited();
            }
        }

        public string NumberMaxText
        {
            get => FormatOptional(_field.NumberMax);
            set
            {
                if (!TryParseOptional(value, out var parsed)) { this.RaisePropertyChanged(); return; }
                if (_field.NumberMax == parsed) return;
                _field.NumberMax = parsed;
                this.RaisePropertyChanged();
                NotifyEdited();
            }
        }

        /// <summary>Число подчиняется шагу.</summary>
        public bool UseStep
        {
            get => _field.UseStep;
            set
            {
                if (_field.UseStep == value) return;
                _field.UseStep = value;
                this.RaisePropertyChanged();
                NotifyEdited();
            }
        }

        public ObservableCollection<AnketaOptionViewModel<CharacterStepRule>> StepRuleOptions { get; } = new();

        private void BuildStepRuleOptions()
        {
            StepRuleOptions.Clear();
            if (!IsNumber) return;

            void Add(CharacterStepRule rule, string label, string hint) =>
                StepRuleOptions.Add(new AnketaOptionViewModel<CharacterStepRule>(rule, label, hint, _field.StepRule == rule, SetStepRule));

            Add(CharacterStepRule.Linear, "+ шаг", "Ровный шаг от нижней границы: 0, 5, 10, 15");
            Add(CharacterStepRule.Multiply, "× шаг", "Каждое следующее значение во столько раз больше: 1, 2, 4, 8");
            Add(CharacterStepRule.List, "Свой список", "Допустимы только перечисленные значения");
        }

        private void SetStepRule(CharacterStepRule rule)
        {
            if (_field.StepRule == rule) return;
            _field.StepRule = rule;
            foreach (var option in StepRuleOptions) option.SetSelectedSilently(option.Value == rule);
            this.RaisePropertyChanged(nameof(IsStepList));
            NotifyEdited();
        }

        public bool IsStepList => _field.StepRule == CharacterStepRule.List;

        /// <summary>Допустимые значения через запятую — для правила «свой список».</summary>
        public string StepValuesRaw
        {
            get => _field.StepValuesRaw;
            set
            {
                var v = value ?? string.Empty;
                if (_field.StepValuesRaw == v) return;
                _field.StepValuesRaw = v;
                this.RaisePropertyChanged();
                NotifyEdited();
            }
        }

        // ── Цвет поля ────────────────────────────────────────────────────

        private static readonly string[] AccentPresets =
        {
            "#E08A3C", "#C75B5B", "#D4A33B", "#7FB069", "#4FA39A", "#5B7FA8", "#8A6FC0", "#C06FA0", "#9A8F85"
        };

        public ObservableCollection<AnketaColorOptionViewModel> ColorOptions { get; } = new();

        private void BuildColorOptions()
        {
            ColorOptions.Clear();
            foreach (var hex in AccentPresets)
            {
                if (!Color.TryParse(hex, out var color)) continue;
                ColorOptions.Add(new AnketaColorOptionViewModel(
                    hex, new SolidColorBrush(color),
                    string.Equals(hex, _field.AccentColor, StringComparison.OrdinalIgnoreCase),
                    SetAccent));
            }
        }

        public string AccentColor
        {
            get => _field.AccentColor;
            set => SetAccent((value ?? string.Empty).Trim());
        }

        public bool HasAccentColor => !string.IsNullOrWhiteSpace(_field.AccentColor);

        private void SetAccent(string hex)
        {
            // Повторный выбор того же цвета возвращает цвет по умолчанию.
            if (string.Equals(_field.AccentColor, hex, StringComparison.OrdinalIgnoreCase))
                hex = string.Empty;

            ApplyAccent(hex);
        }

        private void ApplyAccent(string hex)
        {
            _field.AccentColor = hex ?? string.Empty;
            foreach (var option in ColorOptions)
                option.SetSelectedSilently(string.Equals(option.Hex, hex, StringComparison.OrdinalIgnoreCase));

            this.RaisePropertyChanged(nameof(AccentColor));
            this.RaisePropertyChanged(nameof(HasAccentColor));
            NotifyEdited();
        }

        public void ResetAccent()
        {
            if (!HasAccentColor) return;
            SetAccent(_field.AccentColor);
        }

        // ── Шкала ────────────────────────────────────────────────────────

        public double MinValue
        {
            get => _field.DefaultMinValue;
            set
            {
                if (double.IsNaN(value) || Math.Abs(_field.DefaultMinValue - value) < double.Epsilon) return;
                _field.DefaultMinValue = value;
                if (_field.DefaultMaxValue <= value) _field.DefaultMaxValue = value + Math.Max(StepValue, 1);
                this.RaisePropertyChanged();
                this.RaisePropertyChanged(nameof(MaxValue));
                this.RaisePropertyChanged(nameof(ScaleSummary));
                RefreshScaleCount();
                NotifyEdited();
            }
        }

        public double MaxValue
        {
            get => _field.DefaultMaxValue;
            set
            {
                if (double.IsNaN(value) || Math.Abs(_field.DefaultMaxValue - value) < double.Epsilon) return;
                _field.DefaultMaxValue = Math.Max(value, _field.DefaultMinValue + Math.Max(StepValue, 0.01));
                this.RaisePropertyChanged();
                this.RaisePropertyChanged(nameof(ScaleSummary));
                RefreshScaleCount();
                NotifyEdited();
            }
        }

        public double StepValue
        {
            get => _field.Step > 0 ? _field.Step : 1;
            set
            {
                if (double.IsNaN(value) || value <= 0 || Math.Abs(_field.Step - value) < double.Epsilon) return;
                _field.Step = value;
                this.RaisePropertyChanged();
                this.RaisePropertyChanged(nameof(ScaleSummary));
                RefreshScaleCount();
                NotifyEdited();
            }
        }

        /// <summary>Сколько делений выходит у шкалы — чтобы было видно, сколько будет шариков.</summary>
        public string ScaleSummary
        {
            get
            {
                var span = _field.DefaultMaxValue - _field.DefaultMinValue;
                if (span <= 0 || StepValue <= 0) return string.Empty;
                var steps = (int)Math.Round(span / StepValue);
                return "Делений: " + steps.ToString(CultureInfo.InvariantCulture);
            }
        }

        private string _scaleLabels;

        /// <summary>
        /// Подписи делений одной строкой через запятую: «спокоен, сдержан,
        /// заводится, взрывается, неуправляем». Раскладываются по делениям
        /// при сохранении (ApplyScaleLabels).
        /// </summary>
        public string ScaleLabels
        {
            get => _scaleLabels;
            set
            {
                if (_scaleLabels == value) return;
                _scaleLabels = value ?? string.Empty;
                this.RaisePropertyChanged();
                NotifyEdited();
            }
        }

        public string ScaleLabelsHint => IsBipolar
            ? "По одному слову на положение, слева направо, через запятую"
            : "По одному слову на шарик или деление, от меньшего к большему, через запятую";

        public string LeftPole
        {
            get => _field.MinDescription;
            set
            {
                if (_field.MinDescription == value) return;
                _field.MinDescription = value ?? string.Empty;
                this.RaisePropertyChanged();
                NotifyEdited();
            }
        }

        public string RightPole
        {
            get => _field.MaxDescription;
            set
            {
                if (_field.MaxDescription == value) return;
                _field.MaxDescription = value ?? string.Empty;
                this.RaisePropertyChanged();
                NotifyEdited();
            }
        }

        private void ApplyScaleLabels()
        {
            var result = new Dictionary<double, string>();
            var labels = CharacterFieldDefinition.SplitStates(_scaleLabels);

            // У полюсов подписан каждый край и всё между ними, начиная с
            // левого; у шариков и полосы — деления начиная с первого, потому
            // что ноль там значит «ничего не выбрано».
            var offset = IsBipolar ? 0 : 1;
            for (int i = 0; i < labels.Count; i++)
            {
                var value = _field.DefaultMinValue + (i + offset) * StepValue;
                if (value > _field.DefaultMaxValue + 0.0001) break;
                result[Math.Round(value, 6)] = labels[i];
            }

            _field.ScalePoints = result;
        }

        // ── Выбор ────────────────────────────────────────────────────────

        public string StatesRaw
        {
            get => _field.StatesRaw;
            set
            {
                if (_field.StatesRaw == value) return;
                _field.StatesRaw = value ?? string.Empty;
                this.RaisePropertyChanged();
                NotifyEdited();
            }
        }

        // ── Да / нет ─────────────────────────────────────────────────────

        public string TrueLabel
        {
            get => _field.TrueLabel;
            set
            {
                if (_field.TrueLabel == value) return;
                _field.TrueLabel = value ?? string.Empty;
                this.RaisePropertyChanged();
                NotifyEdited();
            }
        }

        public string FalseLabel
        {
            get => _field.FalseLabel;
            set
            {
                if (_field.FalseLabel == value) return;
                _field.FalseLabel = value ?? string.Empty;
                this.RaisePropertyChanged();
                NotifyEdited();
            }
        }

        // ── Число ────────────────────────────────────────────────────────

        public string Unit
        {
            get => _field.Unit;
            set
            {
                if (_field.Unit == value) return;
                _field.Unit = value ?? string.Empty;
                this.RaisePropertyChanged();
                NotifyEdited();
            }
        }

        public bool ModeExact { get => HasMode(CharacterNumberModes.Exact); set => SetMode(CharacterNumberModes.Exact, value); }
        public bool ModeApprox { get => HasMode(CharacterNumberModes.Approx); set => SetMode(CharacterNumberModes.Approx, value); }
        public bool ModeRange { get => HasMode(CharacterNumberModes.Range); set => SetMode(CharacterNumberModes.Range, value); }
        public bool ModeStage { get => HasMode(CharacterNumberModes.Stage); set => SetMode(CharacterNumberModes.Stage, value); }
        public bool ModeBirthYear { get => HasMode(CharacterNumberModes.BirthYear); set => SetMode(CharacterNumberModes.BirthYear, value); }

        private bool HasMode(CharacterNumberModes mode) =>
            (_field.NumberModes == CharacterNumberModes.None ? CharacterNumberModes.Exact : _field.NumberModes).HasFlag(mode);

        private void SetMode(CharacterNumberModes mode, bool on)
        {
            var current = _field.NumberModes == CharacterNumberModes.None ? CharacterNumberModes.Exact : _field.NumberModes;
            var next = on ? current | mode : current & ~mode;

            // Хотя бы один способ остаётся всегда: без него число нечем задать.
            if (next == CharacterNumberModes.None) next = CharacterNumberModes.Exact;
            if (next == current)
            {
                RaiseModes();
                return;
            }

            _field.NumberModes = next;
            RaiseModes();
            NotifyEdited();
        }

        private void RaiseModes()
        {
            this.RaisePropertyChanged(nameof(ModeExact));
            this.RaisePropertyChanged(nameof(ModeApprox));
            this.RaisePropertyChanged(nameof(ModeRange));
            this.RaisePropertyChanged(nameof(ModeStage));
            this.RaisePropertyChanged(nameof(ModeBirthYear));
        }

        private string _stagesRaw;

        /// <summary>
        /// Этапы одной строкой: «Ребёнок 0-12, Подросток 13-17, Взрослый 18-59,
        /// Пожилой 60+». Разбираются при каждой правке (ParseStages).
        /// </summary>
        public string StagesRaw
        {
            get => _stagesRaw;
            set
            {
                if (_stagesRaw == value) return;
                _stagesRaw = value ?? string.Empty;
                _field.Stages = ParseStages(_stagesRaw);
                this.RaisePropertyChanged();
                NotifyEdited();
            }
        }

        private static string FormatStages(IEnumerable<CharacterNumberStage> stages) =>
            string.Join(", ", stages.Select(s =>
                s.Name + " " + F(s.From) + (s.To.HasValue ? "-" + F(s.To.Value) : "+")));

        private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        /// <summary>
        /// Разбор этапов: «Имя От-До» или «Имя От+». Этап без чисел остаётся
        /// с нулём: имя важнее границ, а границы допишут.
        /// </summary>
        public static List<CharacterNumberStage> ParseStages(string raw)
        {
            var result = new List<CharacterNumberStage>();
            foreach (var part in CharacterFieldDefinition.SplitStates(raw))
            {
                var tokens = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var range = tokens.Length > 1 ? tokens[^1] : string.Empty;
                var name = tokens.Length > 1 ? string.Join(' ', tokens[..^1]) : part;

                var stage = new CharacterNumberStage { Name = name };

                if (range.EndsWith("+") &&
                    double.TryParse(range.TrimEnd('+').Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var open))
                {
                    stage.From = open;
                }
                else if (range.Contains('-') || range.Contains('–'))
                {
                    var bounds = range.Split('-', '–');
                    if (bounds.Length == 2 &&
                        double.TryParse(bounds[0].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var from) &&
                        double.TryParse(bounds[1].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var to))
                    {
                        stage.From = Math.Min(from, to);
                        stage.To = Math.Max(from, to);
                    }
                    else
                    {
                        stage.Name = part;
                    }
                }
                else
                {
                    stage.Name = part;
                }

                result.Add(stage);
            }

            return result;
        }

        // ── Цвет-значение ────────────────────────────────────────────────

        private string _paletteRaw;

        /// <summary>Палитра поля цвета: «#5B7FA8, #6B8F4E, #7A5236».</summary>
        public string PaletteRaw
        {
            get => _paletteRaw;
            set
            {
                if (_paletteRaw == value) return;
                _paletteRaw = value ?? string.Empty;
                _field.Palette = CharacterFieldDefinition.SplitStates(_paletteRaw)
                    .Where(h => Color.TryParse(h, out _))
                    .ToList();
                this.RaisePropertyChanged();
                NotifyEdited();
            }
        }

        public bool AllowCustomColor
        {
            get => _field.AllowCustomColor;
            set
            {
                if (_field.AllowCustomColor == value) return;
                _field.AllowCustomColor = value;
                this.RaisePropertyChanged();
                NotifyEdited();
            }
        }

        // ── Копирование вида ─────────────────────────────────────────────

        /// <summary>
        /// Перенести вид с другого поля: вид отображения, если он подходит к
        /// типу этого поля, цвет поля и — между шкалами — края и шаг. Имя,
        /// подсказка, подписи и варианты остаются свои: это содержание поля, а
        /// не его вид.
        /// </summary>
        public void ApplyDisplayFrom(AnketaDisplaySnapshot snapshot)
        {
            _suppress = true;
            try
            {
                if (DisplayOptions.Any(o => o.Value == snapshot.Display))
                    SetDisplay(snapshot.Display);

                ApplyAccent(snapshot.AccentColor);

                if (IsScale && snapshot.Type == CharacterParameterType.Numeric)
                {
                    _field.DefaultMinValue = snapshot.MinValue;
                    _field.DefaultMaxValue = snapshot.MaxValue;
                    _field.Step = snapshot.Step;
                    this.RaisePropertyChanged(nameof(MinValue));
                    this.RaisePropertyChanged(nameof(MaxValue));
                    this.RaisePropertyChanged(nameof(StepValue));
                    this.RaisePropertyChanged(nameof(ScaleSummary));
                    RefreshScaleCount();
                }

                if (IsNumber && snapshot.Type == CharacterParameterType.Number)
                {
                    _field.NumberModes = snapshot.NumberModes;
                    _field.Unit = snapshot.Unit;
                    _field.Decimals = snapshot.Decimals;
                    _field.UseStep = snapshot.UseStep;
                    _field.StepRule = snapshot.StepRule;
                    _field.Step = snapshot.Step;
                    RaiseModes();
                    foreach (var option in StepRuleOptions) option.SetSelectedSilently(option.Value == snapshot.StepRule);
                    this.RaisePropertyChanged(nameof(Unit));
                    this.RaisePropertyChanged(nameof(IsDecimal));
                    this.RaisePropertyChanged(nameof(IsInteger));
                    this.RaisePropertyChanged(nameof(DecimalsValue));
                    this.RaisePropertyChanged(nameof(UseStep));
                    this.RaisePropertyChanged(nameof(StepValue));
                    this.RaisePropertyChanged(nameof(IsStepList));
                }

                if (IsScale && snapshot.Type == CharacterParameterType.Numeric)
                {
                    _field.RatingGlyph = snapshot.RatingGlyph;
                    this.RaisePropertyChanged(nameof(RatingGlyph));
                }

                // Оформление подписи переносится между полями любых типов.
                _field.LabelBold = snapshot.LabelBold;
                _field.LabelColor = snapshot.LabelColor;
                _field.LabelIcon = snapshot.LabelIcon;
                foreach (var option in LabelColorOptions)
                    option.SetSelectedSilently(string.Equals(option.Hex, snapshot.LabelColor, StringComparison.OrdinalIgnoreCase));
                this.RaisePropertyChanged(nameof(LabelBold));
                this.RaisePropertyChanged(nameof(LabelColor));
                this.RaisePropertyChanged(nameof(LabelIcon));
                this.RaisePropertyChanged(nameof(HasLabelIcon));
            }
            finally
            {
                _suppress = false;
            }

            NotifyEdited();
        }

        public AnketaDisplaySnapshot CaptureDisplay() => new(
            _field.Type, _field.Display, _field.AccentColor,
            _field.DefaultMinValue, _field.DefaultMaxValue, StepValue,
            _field.NumberModes, _field.Unit,
            _field.LabelBold, _field.LabelColor, _field.LabelIcon, _field.RatingGlyph,
            _field.Decimals, _field.UseStep, _field.StepRule);

        // ── Предпросмотр ─────────────────────────────────────────────────

        private CharacterParameterItemViewModel? _preview;

        /// <summary>
        /// Поле так, как оно встанет в карточку персонажа. Строится при первом
        /// обращении — когда открывают окошко настроек поля.
        /// </summary>
        public CharacterParameterItemViewModel? Preview
        {
            get
            {
                if (_preview == null) _preview = BuildPreview();
                return _preview;
            }
            private set => this.RaiseAndSetIfChanged(ref _preview, value);
        }

        private void RebuildPreview() => Preview = BuildPreview();

        private CharacterParameterItemViewModel? BuildPreview()
        {
            var field = ToField(_field.Order);
            if (string.IsNullOrWhiteSpace(field.Name)) field.Name = DisplayName;

            var anketa = new CharacterAnketa { Fields = new List<CharacterAnketaField> { field } };
            var parameter = _anketaService.BuildParameters(anketa).FirstOrDefault();
            if (parameter == null) return null;

            // В конструкторе у полей ввода есть ручка ширины: ширину тянут
            // прямо на поле, а отпущенная ширина записывается в анкету.
            var preview = new CharacterParameterItemViewModel(parameter) { IsDesignerPreview = true };
            preview.InputWidthCommitted += width =>
            {
                _field.InputWidth = width;
                NotifyEdited();
            };
            return preview;
        }

        // ── Сохранение ───────────────────────────────────────────────────

        /// <summary>Определение поля для анкеты.</summary>
        public CharacterAnketaField ToField(int order)
        {
            _field.Order = order;
            if (IsScale) ApplyScaleLabels();
            return Clone(_field);
        }
    }

    /// <summary>Вид поля, скопированный для переноса на другие поля.</summary>
    public readonly record struct AnketaDisplaySnapshot(
        CharacterParameterType Type,
        CharacterFieldDisplay Display,
        string AccentColor,
        double MinValue,
        double MaxValue,
        double Step,
        CharacterNumberModes NumberModes,
        string Unit,
        bool LabelBold,
        string LabelColor,
        string LabelIcon,
        string RatingGlyph,
        int Decimals,
        bool UseStep,
        CharacterStepRule StepRule);

    /// <summary>Вариант выбора в конструкторе — кнопкой переключателя.</summary>
    public class AnketaOptionViewModel<T> : ReactiveObject
    {
        private readonly Action<T> _select;

        public AnketaOptionViewModel(T value, string label, string hint, bool isSelected, Action<T> select)
        {
            Value = value;
            Label = label;
            Hint = hint;
            _isSelected = isSelected;
            _select = select;
        }

        public T Value { get; }
        public string Label { get; }
        public string Hint { get; }

        /// <summary>Значок варианта (Path Data); пусто — вариант подписан словом.</summary>
        public string IconPath { get; init; } = string.Empty;

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (!value)
                {
                    // Выбран всегда ровно один: щелчок по выбранному ничего не снимает.
                    this.RaisePropertyChanged();
                    return;
                }

                if (_isSelected) return;
                this.RaiseAndSetIfChanged(ref _isSelected, value);
                _select(Value);
            }
        }

        public void SetSelectedSilently(bool value) =>
            this.RaiseAndSetIfChanged(ref _isSelected, value, nameof(IsSelected));
    }

    /// <summary>Цвет поля из набора — кружком.</summary>
    public class AnketaColorOptionViewModel : ReactiveObject
    {
        private readonly Action<string> _select;

        public AnketaColorOptionViewModel(string hex, IBrush brush, bool isSelected, Action<string> select)
        {
            Hex = hex;
            Brush = brush;
            _isSelected = isSelected;
            _select = select;
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
                _select(Hex);
            }
        }

        public void SetSelectedSilently(bool value) =>
            this.RaiseAndSetIfChanged(ref _isSelected, value, nameof(IsSelected));
    }
}
