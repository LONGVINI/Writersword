using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media;
using ReactiveUI;
using Writersword.Modules.Characters.Controls;
using Writersword.Modules.Characters.Models;
using Writersword.Modules.Characters.Services;

namespace Writersword.Modules.Characters.ViewModels.Icons
{
    /// <summary>
    /// Окно значков: библиотека своих значков проекта и встроенный набор —
    /// один на всю программу. Здесь выбирают значок анкете, шаблону, метке,
    /// здесь же пополняют библиотеку своими картинками и убирают ненужные.
    ///
    /// Два режима. Выбор — открыто из анкеты, шаблона или метки: щелчок
    /// отмечает значок, «Готово» ставит его. Библиотека — открыто само по
    /// себе: выбирать нечего, окно только показывает и пополняет набор.
    ///
    /// Значки в наборе и превью нарисованы ровно так, как встанут на место:
    /// тем же цветом, с той же подложкой и в том же виде — значком анкеты
    /// или плиткой шаблона (Look).
    /// </summary>
    public class IconPickerViewModel : ReactiveObject
    {
        private readonly CharacterIconLibrary _library;

        public IconPickerViewModel(CharacterIconLibrary library, CharacterIconRequest? request)
        {
            _library = library;

            IsPickMode = request != null;
            Title = request?.Title ?? "Библиотека значков";
            AllowColor = request?.AllowColor ?? false;
            _icon = string.IsNullOrWhiteSpace(request?.Icon) ? CharacterAnketaIcons.Default : request!.Icon;
            _color = string.IsNullOrWhiteSpace(request?.Color) ? CharacterAnketaIcons.DefaultColor : request!.Color;
            _ownIcon = _color;
            Look = request?.Look ?? CharacterIconLook.Badge;
            PreviewTitle = request?.PreviewTitle ?? string.Empty;
            PreviewDescription = request?.PreviewDescription ?? string.Empty;

            _background = request?.Background ?? string.Empty;
            if (CharacterIcons.IsCustomBackground(_background))
                _ownBackground = _background;

            _blockFrame = request?.BlockFrame ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(_blockFrame)) _ownBlockFrame = _blockFrame;

            _border = request?.Border ?? string.Empty;
            if (CharacterIconFrame.Parse(_border) is { } frame)
            {
                _frameWidth = frame.Width;
                if (!frame.IsAuto) _ownFrame = frame.Color;
            }

            UnfoldFilledBackground();

            foreach (var entry in CharacterAnketaIcons.All)
                BuiltIn.Add(new IconOptionViewModel(entry.Key, entry.Label, null));

            BuildColorTargets();
            RebuildSpecials();

            RebuildCustom();
            SyncSelection();
            SyncColors();
            SyncLooks();

            _library.Changed += OnLibraryChanged;
        }

        /// <summary>Отписаться от библиотеки — окно закрыто.</summary>
        public void Detach() => _library.Changed -= OnLibraryChanged;

        private void OnLibraryChanged()
        {
            RebuildCustom();
            SyncSelection();
        }

        public bool IsPickMode { get; }
        public bool IsLibraryMode => !IsPickMode;

        public string Title { get; }

        /// <summary>Цвет можно менять здесь же. У метки цветов два, и правятся они в её редакторе.</summary>
        public bool AllowColor { get; }

        public CharacterIconLibrary Library => _library;

        /// <summary>Где значок встанет: значок анкеты или плитка шаблона.</summary>
        public CharacterIconLook Look { get; }

        public bool IsTileLook => Look == CharacterIconLook.Tile;
        public bool IsBadgeLook => Look == CharacterIconLook.Badge;

        /// <summary>Название рядом со значком в превью — анкеты или шаблона.</summary>
        public string PreviewTitle { get; }

        public string PreviewDescription { get; }

        public bool HasPreviewDescription => !string.IsNullOrWhiteSpace(PreviewDescription);

        /// <summary>Превью с подписью — как на месте: значок и название рядом.</summary>
        public bool HasPreviewTitle => IsPickMode && !string.IsNullOrWhiteSpace(PreviewTitle);

        /// <summary>Превью без подписи — окно открыто не из анкеты и не из шаблона.</summary>
        public bool HasPlainPreview => IsPickMode && string.IsNullOrWhiteSpace(PreviewTitle);

        // ── Выбранное ────────────────────────────────────────────────────

        private string _icon;
        public string Icon
        {
            get => _icon;
            set
            {
                if (_icon == value || string.IsNullOrWhiteSpace(value)) return;
                this.RaiseAndSetIfChanged(ref _icon, value);
                this.RaisePropertyChanged(nameof(IconName));
                SyncSelection();
            }
        }

        private string _color;

        /// <summary>Цвет значка — код цвета программы: одноцвет или градиент.</summary>
        public string Color
        {
            get => _color;
            set
            {
                var v = string.IsNullOrWhiteSpace(value) ? CharacterAnketaIcons.DefaultColor : value;
                if (_color == v) return;
                this.RaiseAndSetIfChanged(ref _color, v);
                this.RaisePropertyChanged(nameof(ColorBrush));
                SyncColors();
                SyncLooks();
            }
        }

        /// <summary>Кисть внешней рамки блока шаблона в превью: своя или цвета шаблона.</summary>
        public IBrush ColorBrush => AnketaIconBadge.BrushOf(IsBlockFrameAuto ? _color : _blockFrame);

        private string ColorOwner => IsTileLook ? "шаблона" : "значка";

        // ── Что красим ───────────────────────────────────────────────────
        //
        // Один набор быстрых цветов и один «Свой цвет» на всё. Сверху
        // выбирается, что красим: значок, подложку, рамку значка, рамку
        // шаблона. Кружок и «Свой цвет» действуют на выбранное. Особые
        // варианты — бледная, без подложки, без рамки, цвета
        // значка — стоят в начале того же ряда, отдельно от цветов.

        // Нейтральные — на них цветной значок читается лучше всего; дальше
        // цвета значков программы. Один набор для всего, что красится.
        private static readonly string[] NeutralPalette =
        {
            "#FFFFFF", "#E8E6E3", "#9E9E9E", "#3A3D42", "#26282C", "#000000"
        };

        public ObservableCollection<IconColorTargetViewModel> Targets { get; } = new();
        public ObservableCollection<IconColorSwatchViewModel> Palette { get; } = new();
        public ObservableCollection<IconSpecialColorViewModel> Specials { get; } = new();

        private void BuildColorTargets()
        {
            Targets.Add(new IconColorTargetViewModel(IconColorTarget.Icon, "Значок",
                "Цвет самого значка" + (IsTileLook ? " — он же цвет шаблона" : string.Empty)));
            Targets.Add(new IconColorTargetViewModel(IconColorTarget.Background, "Подложка",
                "Фон под значком: бледный цвета " + ColorOwner + ", любой свой цвет или без подложки"));
            Targets.Add(new IconColorTargetViewModel(IconColorTarget.Frame, "Рамка",
                "Обводка вокруг значка: без рамки, цвета " + ColorOwner + " или своя. Толщина — ползунком"));
            if (ShowBlockFrame)
                Targets.Add(new IconColorTargetViewModel(IconColorTarget.BlockFrame, "Рамка блока",
                    "Обводка всего блока шаблона и его ленточка: цвета шаблона или своя"));

            foreach (var hex in NeutralPalette.Concat(CharacterAnketaIcons.Colors).Distinct(StringComparer.OrdinalIgnoreCase))
                Palette.Add(new IconColorSwatchViewModel(hex));
        }

        private IconColorTarget _activeTarget = IconColorTarget.Icon;

        /// <summary>Что сейчас красится кружками и «Своим цветом».</summary>
        public IconColorTarget ActiveTarget
        {
            get => _activeTarget;
            set
            {
                if (_activeTarget == value) return;
                this.RaiseAndSetIfChanged(ref _activeTarget, value);
                RebuildSpecials();
                SyncColors();
            }
        }

        public bool HasSpecials => Specials.Count > 0;

        /// <summary>Особые варианты для того, что красим: они не цвет, а способ.</summary>
        private void RebuildSpecials()
        {
            Specials.Clear();
            switch (_activeTarget)
            {
                case IconColorTarget.Background:
                    Specials.Add(new IconSpecialColorViewModel(IconSpecialKind.Pale, "Бледная",
                        "Как обычно: бледная подложка цвета " + ColorOwner + ", значок цветной"));
                    Specials.Add(new IconSpecialColorViewModel(IconSpecialKind.Off, "Без подложки",
                        "Только сам значок, на фоне того места, где он стоит"));
                    break;
                case IconColorTarget.Frame:
                    Specials.Add(new IconSpecialColorViewModel(IconSpecialKind.Off, "Без рамки",
                        "Значок без обводки — как обычно"));
                    Specials.Add(new IconSpecialColorViewModel(IconSpecialKind.Same, "Цвета " + ColorOwner,
                        "Рамка цвета " + ColorOwner + " — меняется вместе с ним"));
                    break;
                case IconColorTarget.BlockFrame:
                    Specials.Add(new IconSpecialColorViewModel(IconSpecialKind.Same, "Цвета шаблона",
                        "Рамка блока и ленточка цвета шаблона — меняются вместе с ним"));
                    break;
            }

            this.RaisePropertyChanged(nameof(HasSpecials));
        }

        /// <summary>Особый вариант выбран: бледная, без, цвета значка.</summary>
        public void ChooseSpecial(IconSpecialColorViewModel special)
        {
            switch (_activeTarget, special.Kind)
            {
                case (IconColorTarget.Background, IconSpecialKind.Pale): SetBackground(string.Empty); break;
                case (IconColorTarget.Background, IconSpecialKind.Off): SetBackground(CharacterIcons.BackgroundNone); break;
                case (IconColorTarget.Frame, IconSpecialKind.Off): SetBorder(string.Empty); break;
                case (IconColorTarget.Frame, IconSpecialKind.Same):
                    SetBorder(CharacterIconFrame.Format(_frameWidth, CharacterIconFrame.Auto)); break;
                case (IconColorTarget.BlockFrame, IconSpecialKind.Same): SetBlockFrame(string.Empty); break;
            }
        }

        // Свой цвет у каждого, что красим, — отдельно от быстрых кружков:
        // кружок палитры его не затирает, и к своему цвету всегда можно
        // вернуться одним щелчком.
        private string _ownIcon;
        private string _ownBackground = DefaultOwnBackground;
        private string _ownFrame = DefaultOwnFrame;
        private string _ownBlockFrame = DefaultOwnFrame;

        private const string DefaultOwnBackground = "#26282C";
        private const string DefaultOwnFrame = "#FFFFFF";

        /// <summary>
        /// Свой цвет того, что красим, — для редактора цвета. Цвет из
        /// редактора сразу ставится и запоминается как свой, даже если он тот
        /// же, что был: выбрали в редакторе — значит, хотят именно его. Ничего
        /// не делается, только если этот же свой цвет уже стоит.
        /// </summary>
        public string ActiveColor
        {
            get => _activeTarget switch
            {
                IconColorTarget.Background => _ownBackground,
                IconColorTarget.Frame => _ownFrame,
                IconColorTarget.BlockFrame => _ownBlockFrame,
                _ => _ownIcon
            };
            set
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                if (string.Equals(value, ActiveColor, StringComparison.OrdinalIgnoreCase) && IsOwnColorSelected) return;

                switch (_activeTarget)
                {
                    case IconColorTarget.Background: _ownBackground = value; break;
                    case IconColorTarget.Frame: _ownFrame = value; break;
                    case IconColorTarget.BlockFrame: _ownBlockFrame = value; break;
                    default: _ownIcon = value; break;
                }

                ApplyColor(value);
            }
        }

        /// <summary>
        /// Свой цвет сейчас стоит у того, что красим. Включается сам, когда
        /// свой цвет выбран, и гаснет, когда выбран быстрый кружок или особый
        /// вариант.
        /// </summary>
        public bool IsOwnColorSelected
        {
            get
            {
                var current = CurrentCustomColor();
                return current != null && string.Equals(current, ActiveColor, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>«Использовать свой цвет» — поставить его снова, например после быстрого кружка или «Без».</summary>
        public void ChooseOwnColor() => ApplyColor(ActiveColor);

        /// <summary>Быстрый цвет кружком — тому, что красим. Свой цвет при этом не меняется.</summary>
        public void ChooseColor(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return;
            ApplyColor(hex);
        }

        /// <summary>Поставить цвет тому, что красим: подложке и рамкам — свой цвет вместо особых вариантов.</summary>
        private void ApplyColor(string value)
        {
            switch (_activeTarget)
            {
                case IconColorTarget.Background:
                    SetBackground(value);
                    break;
                case IconColorTarget.Frame:
                    SetBorder(CharacterIconFrame.Format(_frameWidth, value));
                    break;
                case IconColorTarget.BlockFrame:
                    SetBlockFrame(value);
                    break;
                default:
                    Color = value;
                    break;
            }

            SyncColors();
        }

        /// <summary>Свой цвет того, что красим, если сейчас выбран именно свой; иначе null.</summary>
        private string? CurrentCustomColor() => _activeTarget switch
        {
            IconColorTarget.Background => IsBackgroundCustom ? _background : null,
            IconColorTarget.Frame => CharacterIconFrame.Parse(_border) is { IsAuto: false } frame ? frame.Color : null,
            IconColorTarget.BlockFrame => IsBlockFrameCustom ? _blockFrame : null,
            _ => _color
        };

        private bool IsSpecialSelected(IconSpecialKind kind) => (_activeTarget, kind) switch
        {
            (IconColorTarget.Background, IconSpecialKind.Pale) => IsBackgroundAuto,
            (IconColorTarget.Background, IconSpecialKind.Off) => IsBackgroundNone,
            (IconColorTarget.Frame, IconSpecialKind.Off) => !HasFrame,
            (IconColorTarget.Frame, IconSpecialKind.Same) => IsFrameAuto,
            (IconColorTarget.BlockFrame, IconSpecialKind.Same) => IsBlockFrameAuto,
            _ => false
        };

        // ── Подложка ─────────────────────────────────────────────────────

        /// <summary>
        /// Залитая подложка прежних версий — цвета значка, а значок на ней
        /// белый — раскладывается на обычные цвета: подложка своего цвета,
        /// значок белый, а рамки, что шли за цветом значка, получают прежний
        /// цвет. Выглядит так же, но теперь меняется и подложка, и значок:
        /// у залитой цвет значка был намертво белым.
        /// </summary>
        private void UnfoldFilledBackground()
        {
            if (!CharacterIcons.IsFillBackground(_background)) return;

            var color = _color;
            _background = color;
            _ownBackground = color;

            if (CharacterIconFrame.Parse(_border) is { IsAuto: true } frame)
            {
                _border = CharacterIconFrame.Format(frame.Width, color);
                _ownFrame = color;
            }

            if (ShowBlockFrame && string.IsNullOrWhiteSpace(_blockFrame))
            {
                _blockFrame = color;
                _ownBlockFrame = color;
            }

            _color = "#FFFFFF";
            _ownIcon = _color;
        }

        private string _background;

        /// <summary>Подложка: пусто — как обычно, «fill» — залитая, «none» — без подложки, иначе код цвета.</summary>
        public string Background => _background;

        public bool IsBackgroundAuto => CharacterIcons.IsAutoBackground(_background);
        public bool IsBackgroundNone => CharacterIcons.IsNoBackground(_background);
        public bool IsBackgroundCustom => CharacterIcons.IsCustomBackground(_background);

        private void SetBackground(string value)
        {
            var v = value ?? string.Empty;
            if (_background == v) return;

            _background = v;
            this.RaisePropertyChanged(nameof(Background));
            SyncColors();
            SyncLooks();
        }

        // ── Рамка значка ─────────────────────────────────────────────────

        private string _border;
        private int _frameWidth = CharacterIconFrame.DefaultWidth;

        /// <summary>Рамка: пусто — нет, иначе строка CharacterIconFrame.</summary>
        public string Border => _border;

        public bool HasFrame => CharacterIconFrame.Parse(_border) != null;
        public bool IsFrameAuto => CharacterIconFrame.Parse(_border) is { IsAuto: true };

        public int FrameWidthMin => CharacterIconFrame.MinWidth;
        public int FrameWidthMax => CharacterIconFrame.MaxWidth;

        /// <summary>
        /// Толщина рамки — ползунком. Рамки не было — появляется цвета
        /// значка: тянут ползунок, чтобы её увидеть.
        /// </summary>
        public double FrameWidth
        {
            get => _frameWidth;
            set
            {
                var v = (int)Math.Round(Math.Clamp(value, CharacterIconFrame.MinWidth, CharacterIconFrame.MaxWidth));
                if (_frameWidth == v) return;
                _frameWidth = v;
                this.RaisePropertyChanged(nameof(FrameWidth));

                var color = CharacterIconFrame.Parse(_border)?.Color ?? CharacterIconFrame.Auto;
                SetBorder(CharacterIconFrame.Format(_frameWidth, color));
            }
        }

        /// <summary>Ползунок толщины виден, когда красим рамку значка.</summary>
        public bool ShowFrameWidth => _activeTarget == IconColorTarget.Frame;

        private void SetBorder(string value)
        {
            var v = value ?? string.Empty;
            if (_border == v) return;

            _border = v;
            this.RaisePropertyChanged(nameof(Border));
            SyncColors();
            SyncLooks();
        }

        // ── Внешняя рамка блока шаблона ──────────────────────────────────
        //
        // Рамка вокруг всего блока и ленточка шаблона проекта. Обычно —
        // цвета шаблона, но может быть и своей. Есть только у шаблона.

        private string _blockFrame = string.Empty;

        /// <summary>Рамка блока правится: окно открыто из шаблона и цвет здесь правится.</summary>
        public bool ShowBlockFrame => IsTileLook && AllowColor;

        /// <summary>Цвет внешней рамки: пусто — цвета шаблона, иначе свой код цвета.</summary>
        public string BlockFrame => _blockFrame;

        public bool IsBlockFrameAuto => string.IsNullOrWhiteSpace(_blockFrame);
        public bool IsBlockFrameCustom => !IsBlockFrameAuto;

        private void SetBlockFrame(string value)
        {
            var v = value ?? string.Empty;
            if (_blockFrame == v) return;

            _blockFrame = v;
            this.RaisePropertyChanged(nameof(BlockFrame));
            this.RaisePropertyChanged(nameof(ColorBrush));
            SyncColors();
        }

        /// <summary>Имя выбранного значка — под превью.</summary>
        public string IconName
        {
            get
            {
                var imageRef = CharacterIcons.ImageRefOf(_icon);
                if (imageRef != null)
                    return _library.FindByRef(imageRef)?.Name ?? "Своя картинка";

                var label = CharacterAnketaIcons.LabelOf(_icon);
                return string.IsNullOrEmpty(label) ? _icon : label;
            }
        }

        public CharacterIconChoice Result => new(_icon, _color, _background, _border, _blockFrame);

        // ── Наборы ───────────────────────────────────────────────────────

        public ObservableCollection<IconOptionViewModel> BuiltIn { get; } = new();
        public ObservableCollection<IconOptionViewModel> Custom { get; } = new();

        public bool HasCustom => Custom.Count > 0;

        private string _query = string.Empty;

        /// <summary>Поиск по названиям: «магия», «щит», имя своего файла.</summary>
        public string Query
        {
            get => _query;
            set
            {
                if (_query == value) return;
                this.RaiseAndSetIfChanged(ref _query, value ?? string.Empty);
                this.RaisePropertyChanged(nameof(HasQuery));
                ApplyQuery();
            }
        }

        /// <summary>В поиске что-то набрано — показать крестик очистки.</summary>
        public bool HasQuery => !string.IsNullOrEmpty(_query);

        private bool _hasBuiltInMatches = true;
        public bool HasBuiltInMatches
        {
            get => _hasBuiltInMatches;
            private set => this.RaiseAndSetIfChanged(ref _hasBuiltInMatches, value);
        }

        private void RebuildCustom()
        {
            Custom.Clear();
            foreach (var entry in _library.Entries)
                Custom.Add(new IconOptionViewModel(entry.Icon, entry.Name, entry.Id));

            SyncLooks();
            this.RaisePropertyChanged(nameof(HasCustom));
            this.RaisePropertyChanged(nameof(IconName));
            ApplyQuery();
        }

        private void ApplyQuery()
        {
            foreach (var option in BuiltIn) option.IsMatch = CharacterIconSearch.Matches(_query, option.Label, option.Key);
            foreach (var option in Custom) option.IsMatch = CharacterIconSearch.Matches(_query, option.Label, string.Empty);
            HasBuiltInMatches = BuiltIn.Any(o => o.IsMatch);
        }

        private void SyncSelection()
        {
            if (!IsPickMode) return;

            foreach (var option in BuiltIn) option.IsSelected = option.Key == _icon;
            foreach (var option in Custom) option.IsSelected = option.Key == _icon;
        }

        /// <summary>
        /// Отметки цветов: что выбрано у того, что красим, и чем сейчас
        /// окрашено каждое из того, что можно красить.
        /// </summary>
        private void SyncColors()
        {
            var current = CurrentCustomColor();
            foreach (var swatch in Palette)
                swatch.IsSelected = current != null &&
                                    string.Equals(swatch.Hex, current, StringComparison.OrdinalIgnoreCase);

            var iconBrush = AnketaIconBadge.BrushOf(_color);
            foreach (var special in Specials)
            {
                special.Brush = iconBrush;
                special.IsSelected = IsSpecialSelected(special.Kind);
            }

            foreach (var target in Targets)
            {
                target.IsSelected = target.Target == _activeTarget;
                switch (target.Target)
                {
                    case IconColorTarget.Icon:
                        target.Show(iconBrush, pale: false, off: false);
                        break;
                    case IconColorTarget.Background:
                        target.Show(IsBackgroundCustom ? AnketaIconBadge.BrushOf(_background) : iconBrush,
                            pale: IsBackgroundAuto, off: IsBackgroundNone);
                        break;
                    case IconColorTarget.Frame:
                        var frame = CharacterIconFrame.Parse(_border);
                        target.Show(frame is { IsAuto: false } own ? AnketaIconBadge.BrushOf(own.Color) : iconBrush,
                            pale: false, off: frame == null);
                        break;
                    case IconColorTarget.BlockFrame:
                        target.Show(ColorBrush, pale: false, off: false);
                        break;
                }
            }

            this.RaisePropertyChanged(nameof(ActiveColor));
            this.RaisePropertyChanged(nameof(IsOwnColorSelected));
            this.RaisePropertyChanged(nameof(ShowFrameWidth));
        }

        /// <summary>Значки набора рисуются нынешними цветом, подложкой, рамкой и видом.</summary>
        private void SyncLooks()
        {
            foreach (var option in BuiltIn) option.Apply(_color, _background, _border, Look);
            foreach (var option in Custom) option.Apply(_color, _background, _border, Look);
        }

        // ── Действия ─────────────────────────────────────────────────────

        public void Choose(IconOptionViewModel option)
        {
            if (!IsPickMode || option == null) return;
            Icon = option.Key;
        }

        /// <summary>
        /// Убрать свой значок из библиотеки. У тех, кому он уже стоит, он
        /// остаётся: ссылка записана у них самих.
        /// </summary>
        public void Remove(IconOptionViewModel option)
        {
            if (option?.EntryId == null) return;
            _library.Remove(option.EntryId);
        }

        public void Rename(IconOptionViewModel option, string name)
        {
            if (option?.EntryId == null) return;
            _library.Rename(option.EntryId, name);
        }
    }

    /// <summary>Значок в окне значков — встроенный или свой из библиотеки.</summary>
    public class IconOptionViewModel : ReactiveObject
    {
        public IconOptionViewModel(string key, string label, string? entryId)
        {
            Key = key;
            Label = label;
            EntryId = entryId;
        }

        /// <summary>Значок строкой: ключ встроенного или «img:ссылка».</summary>
        public string Key { get; }

        public string Label { get; }

        /// <summary>Запись библиотеки; null — встроенный значок.</summary>
        public string? EntryId { get; }

        public bool IsCustom => EntryId != null;

        private string? _editName;

        /// <summary>Имя своего значка в окошке правки. Записывается, когда окошко закрывают.</summary>
        public string EditName
        {
            get => _editName ?? Label;
            set => this.RaiseAndSetIfChanged(ref _editName, value);
        }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set => this.RaiseAndSetIfChanged(ref _isSelected, value);
        }

        private bool _isMatch = true;
        public bool IsMatch
        {
            get => _isMatch;
            set => this.RaiseAndSetIfChanged(ref _isMatch, value);
        }

        private string _color = CharacterAnketaIcons.DefaultColor;

        /// <summary>Цвет, которым значок нарисован в наборе, — нынешний цвет значка.</summary>
        public string Color
        {
            get => _color;
            set => this.RaiseAndSetIfChanged(ref _color, value);
        }

        private string _background = string.Empty;

        /// <summary>Подложка, с которой значок нарисован в наборе, — нынешняя подложка.</summary>
        public string Background
        {
            get => _background;
            set => this.RaiseAndSetIfChanged(ref _background, value);
        }

        private string _border = string.Empty;

        /// <summary>Рамка, с которой значок нарисован в наборе, — нынешняя рамка.</summary>
        public string Border
        {
            get => _border;
            set => this.RaiseAndSetIfChanged(ref _border, value);
        }

        private CharacterIconLook _look = CharacterIconLook.Badge;

        /// <summary>Вид значка в наборе — тот, в каком он встанет на место.</summary>
        public CharacterIconLook Look
        {
            get => _look;
            set => this.RaiseAndSetIfChanged(ref _look, value);
        }

        internal void Apply(string color, string background, string border, CharacterIconLook look)
        {
            Color = color;
            Background = background;
            Border = border;
            Look = look;
        }
    }

    /// <summary>Готовый цвет значка — кружком в ряду быстрых цветов.</summary>
    public class IconColorSwatchViewModel : ReactiveObject
    {
        public IconColorSwatchViewModel(string hex)
        {
            Hex = hex;
        }

        public string Hex { get; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set => this.RaiseAndSetIfChanged(ref _isSelected, value);
        }
    }

    /// <summary>Что красится в окне значков.</summary>
    public enum IconColorTarget
    {
        Icon,
        Background,
        Frame,
        BlockFrame
    }

    /// <summary>Особый вариант вместо цвета: способ, а не оттенок.</summary>
    public enum IconSpecialKind
    {
        /// <summary>Бледная подложка цвета значка — как обычно.</summary>
        Pale,

        /// <summary>Без подложки или без рамки.</summary>
        Off,

        /// <summary>Цвета значка (шаблона) — меняется вместе с ним.</summary>
        Same
    }

    /// <summary>Кнопка «что красим» с кружком того, чем это окрашено сейчас.</summary>
    public class IconColorTargetViewModel : ReactiveObject
    {
        public IconColorTargetViewModel(IconColorTarget target, string label, string description)
        {
            Target = target;
            Label = label;
            Description = description;
        }

        public IconColorTarget Target { get; }
        public string Label { get; }
        public string Description { get; }

        private IBrush _brush = Brushes.Transparent;
        public IBrush Brush { get => _brush; private set => this.RaiseAndSetIfChanged(ref _brush, value); }

        private bool _isPale;
        /// <summary>Окрашено бледно — как обычная подложка.</summary>
        public bool IsPale { get => _isPale; private set => this.RaiseAndSetIfChanged(ref _isPale, value); }

        private bool _isOff;
        /// <summary>Выключено: подложки или рамки нет.</summary>
        public bool IsOff { get => _isOff; private set => this.RaiseAndSetIfChanged(ref _isOff, value); }

        public bool IsOn => !_isOff;

        /// <summary>Кружок во всю силу: включено и не бледное.</summary>
        public bool IsSolid => !_isOff && !_isPale;

        private bool _isSelected;
        public bool IsSelected { get => _isSelected; set => this.RaiseAndSetIfChanged(ref _isSelected, value); }

        internal void Show(IBrush brush, bool pale, bool off)
        {
            Brush = brush;
            IsPale = pale;
            IsOff = off;
            this.RaisePropertyChanged(nameof(IsOn));
            this.RaisePropertyChanged(nameof(IsSolid));
        }
    }

    /// <summary>Особый вариант в ряду цветов: бледная, без, цвета значка.</summary>
    public class IconSpecialColorViewModel : ReactiveObject
    {
        public IconSpecialColorViewModel(IconSpecialKind kind, string label, string description)
        {
            Kind = kind;
            Label = label;
            Description = description;
        }

        public IconSpecialKind Kind { get; }
        public string Label { get; }
        public string Description { get; }

        public bool IsPale => Kind == IconSpecialKind.Pale;
        public bool IsOff => Kind == IconSpecialKind.Off;
        public bool IsSame => Kind == IconSpecialKind.Same;

        private IBrush _brush = Brushes.Transparent;
        /// <summary>Цвет значка — им нарисованы бледный кружок и «цвета значка».</summary>
        public IBrush Brush { get => _brush; set => this.RaiseAndSetIfChanged(ref _brush, value); }

        private bool _isSelected;
        public bool IsSelected { get => _isSelected; set => this.RaiseAndSetIfChanged(ref _isSelected, value); }
    }
}
