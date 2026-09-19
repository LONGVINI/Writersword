using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Writersword.Infrastructure.Behaviours;
using Writersword.Modules.TextEditor.Models.Settings;
using Writersword.Styles.UserControls;

namespace Writersword.Modules.TextEditor.Views.Reading
{
    /// <summary>
    /// Строка списка видов. Отдельная обёртка нужна затем, что списку нужны кисти и
    /// подписи, которых у самой модели вида нет и быть не должно.
    /// </summary>
    public sealed class ReadingThemeRow : INotifyPropertyChanged
    {
        public ReadingThemeRow(ReadingTheme theme)
        {
            Theme = theme;
        }

        public ReadingTheme Theme { get; }

        public string Name => string.IsNullOrWhiteSpace(Theme.Name) ? "Без имени" : Theme.Name;
        public bool IsBuiltIn => Theme.IsBuiltIn;

        public IBrush SheetBrush => Brush(Theme.SheetColor, Colors.White);
        public IBrush InkBrush => Brush(Theme.InkColor, Colors.Black);

        /// <summary>Вид убран из списков выбора. Правится он по-прежнему отсюда.</summary>
        public bool IsHidden
        {
            get => Theme.IsHidden;
            set
            {
                if (Theme.IsHidden == value) return;
                Theme.IsHidden = value;
                Raise();
            }
        }

        /// <summary>Вид приложен к рукописи: в списке это папка.</summary>
        public bool IsLocalScope => !Theme.IsBuiltIn && Theme.InDocument;

        /// <summary>Вид лежит в настройках программы: в списке это глобус.</summary>
        public bool IsGlobalScope => !Theme.IsBuiltIn && Theme.IsGlobal;

        /// <summary>
        /// Где вид сохранён — словами, для подсказки к значкам.
        ///
        /// Подписью в строке это больше не стоит: у каждого вида она своя, все шесть
        /// встроенных повторяли одно и то же «есть всегда», и список из имён с
        /// одинаковой второй строкой читается тяжелее, чем просто список имён.
        /// Значок справа говорит то же самое и не отнимает строки.
        /// </summary>
        public string ScopeHint
        {
            get
            {
                if (Theme.IsBuiltIn) return "Встроенный вид: есть всегда и во всех проектах";
                if (Theme.InDocument && Theme.IsGlobal) return "Хранится в рукописи и в программе";
                if (Theme.InDocument) return "Хранится в рукописи и уедет вместе с ней";
                if (Theme.IsGlobal) return "Хранится в программе: доступен во всех проектах";
                return "Нигде не сохранён";
            }
        }

        private bool _isSelected;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                Raise();
            }
        }

        /// <summary>Сообщает списку, что у вида поменялось всё видимое.</summary>
        public void RefreshAll()
        {
            Raise(nameof(Name));
            Raise(nameof(SheetBrush));
            Raise(nameof(InkBrush));
            Raise(nameof(IsLocalScope));
            Raise(nameof(IsGlobalScope));
            Raise(nameof(IsHidden));
            Raise(nameof(ScopeHint));
        }

        private static IBrush Brush(string? hex, Color fallback)
            => new SolidColorBrush(!string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex, out var c) ? c : fallback);

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Что окно отдаёт назад: полный список видов и выбранный.</summary>
    public sealed class ReadingThemeResult
    {
        public ReadingThemeResult(IReadOnlyList<ReadingTheme> themes, ReadingTheme selected)
        {
            Themes = themes;
            Selected = selected;
        }

        public IReadOnlyList<ReadingTheme> Themes { get; }
        public ReadingTheme Selected { get; }
    }

    /// <summary>
    /// Окно видов чтения. Здесь заводят собственное оформление книги: имя, цвета,
    /// шрифт, свет, картинка бумаги — и решают, где виду жить: в документе, чтобы
    /// он уехал вместе с рукописью, или в настройках программы, чтобы был под рукой
    /// во всех проектах. Можно и то и другое сразу.
    ///
    /// Правится копия списка: отказ должен возвращать всё ровно таким, каким было
    /// до открытия.
    /// </summary>
    public partial class ReadingThemeOverlay : UserControl
    {

        private TaskCompletionSource<ReadingThemeResult?>? _tcs;

        private readonly ObservableCollection<ReadingThemeRow> _rows = new();
        private ReadingThemeRow? _current;

        private Border _scrim = null!;
        private ItemsControl _themeList = null!;
        private TextBox _nameBox = null!;
        private Button _scopeDocBtn = null!;
        private Button _scopeGlobalBtn = null!;
        private Border _scopeThumb = null!;
        private TextBlock _scopeHint = null!;
        private ColorPickerButton _sheetColorBtn = null!;
        private ColorPickerButton _inkColorBtn = null!;
        private ColorPickerButton _caretColorBtn = null!;
        private Button _caretResetBtn = null!;
        private ColorPickerButton _rulerSheetColorBtn = null!;
        private Button _rulerSheetResetBtn = null!;
        private ColorPickerButton _rulerInkColorBtn = null!;
        private Button _rulerInkResetBtn = null!;
        private ColorPickerButton _rulerFieldColorBtn = null!;
        private Button _rulerFieldResetBtn = null!;
        private ColorPickerButton _tabMarkColorBtn = null!;
        private Button _tabMarkResetBtn = null!;
        private Button _previewToggleBtn = null!;
        private PathIcon _previewChevron = null!;
        private Border _previewFrame = null!;

        /// <summary>Высота примера в развёрнутом виде — та же, что стоит в разметке.</summary>
        private const double PreviewHeightPx = 118.0;

        // Пример свёрнут. Состояние держится на время сеанса окна и никуда не
        // сохраняется: свернули, чтобы добраться до нижних полей, — в следующий раз
        // окно снова открывается с бумагой на виду.
        private bool _previewCollapsed;

        // «Цвет выводится сам» для каждого поля раздела правки. Хранить это отдельно
        // от значения обязательно: выведенный цвет совпадает с выбранным вручную по
        // виду, и отличить одно от другого по образцу нельзя. Своё значение живёт в
        // теме как непустое поле, выведенное — как пустое.
        private bool _caretAuto = true;
        private bool _rulerSheetAuto = true;
        private bool _rulerInkAuto = true;
        private bool _rulerFieldAuto = true;
        private bool _tabMarkAuto = true;

        /// <summary>Прежний бирюзовый: им табуляция рисуется, пока вид не задал свой.</summary>
        private const string DefaultTabMarkHex = "#0E7A7A";
        private ColorPickerButton _backColorBtn = null!;
        private ComboBox _backFitCombo = null!;
        private WrapPanel _backGallery = null!;
        private TextBlock _backGalleryHint = null!;
        private Slider _backOpacitySlider = null!;
        private TextBlock _backOpacityValue = null!;

        /// <summary>Подписи того, как ложится картинка поля.</summary>
        private static readonly string[] BackdropFitLabels =
            { "Закрыть целиком", "Уместить целиком", "Растянуть", "Замостить" };
        private Slider _brightnessSlider = null!;
        private Slider _contrastSlider = null!;
        private Slider _warmthSlider = null!;
        private TextBlock _brightnessValue = null!;
        private TextBlock _contrastValue = null!;
        private TextBlock _warmthValue = null!;
        private WrapPanel _paperGallery = null!;
        private TextBlock _paperGalleryHint = null!;
        private ComboBox _paperFlowCombo = null!;
        private Grid _paperFlowRow = null!;
        private TextBlock _imageInfoText = null!;

        /// <summary>Подписи того, как вид разбирает набор картинок бумаги.</summary>
        private static readonly string[] PaperFlowLabels =
            { "Одну", "По очереди", "Вразнобой" };

        // Рабочие наборы картинок выбранного вида. Правятся плитками, уезжают в вид
        // в CollectInto — как и всё остальное в этом окне.
        private readonly List<string> _paperImages = new();
        private readonly List<string> _backImages = new();
        private Slider _opacitySlider = null!;
        private TextBlock _opacityValue = null!;
        private CheckBox _tileCheck = null!;
        private Button _duplicateBtn = null!;
        private Button _deleteBtn = null!;

        // Пример вида: поле вокруг книги, бумага, картинка поверх неё, буквы и свет.
        private Border _previewBackdrop = null!;
        private Border _previewBackdropImage = null!;
        private Border _previewPaper = null!;
        private Border _previewPaperImage = null!;
        private Border _previewWarm = null!;
        private Border _previewDim = null!;
        private TextBlock _previewHead = null!;
        private TextBlock _previewBody = null!;

        // Пока идёт загрузка полей, обработчики ничего не применяют: иначе первая же
        // подстановка перетирала бы соседние значения вида.
        private bool _loading;

        public ReadingThemeOverlay()
        {
            InitializeComponent();
            IsVisible = false;

            _scrim = this.FindControl<Border>("Scrim")!;
            _themeList = this.FindControl<ItemsControl>("ThemeList")!;
            _nameBox = this.FindControl<TextBox>("NameBox")!;
            _scopeDocBtn = this.FindControl<Button>("ScopeDocBtn")!;
            _scopeGlobalBtn = this.FindControl<Button>("ScopeGlobalBtn")!;
            _scopeThumb = this.FindControl<Border>("ScopeThumb")!;
            _scopeHint = this.FindControl<TextBlock>("ScopeHint")!;
            _sheetColorBtn = this.FindControl<ColorPickerButton>("SheetColorBtn")!;
            _inkColorBtn = this.FindControl<ColorPickerButton>("InkColorBtn")!;
            _caretColorBtn = this.FindControl<ColorPickerButton>("CaretColorBtn")!;
            _caretResetBtn = this.FindControl<Button>("CaretResetBtn")!;
            _rulerSheetColorBtn = this.FindControl<ColorPickerButton>("RulerSheetColorBtn")!;
            _rulerSheetResetBtn = this.FindControl<Button>("RulerSheetResetBtn")!;
            _rulerInkColorBtn = this.FindControl<ColorPickerButton>("RulerInkColorBtn")!;
            _rulerInkResetBtn = this.FindControl<Button>("RulerInkResetBtn")!;
            _rulerFieldColorBtn = this.FindControl<ColorPickerButton>("RulerFieldColorBtn")!;
            _rulerFieldResetBtn = this.FindControl<Button>("RulerFieldResetBtn")!;
            _tabMarkColorBtn = this.FindControl<ColorPickerButton>("TabMarkColorBtn")!;
            _tabMarkResetBtn = this.FindControl<Button>("TabMarkResetBtn")!;
            _previewToggleBtn = this.FindControl<Button>("PreviewToggleBtn")!;
            _previewChevron = this.FindControl<PathIcon>("PreviewChevron")!;
            _previewFrame = this.FindControl<Border>("PreviewFrame")!;
            _backColorBtn = this.FindControl<ColorPickerButton>("BackColorBtn")!;
            _backFitCombo = this.FindControl<ComboBox>("BackFitCombo")!;
            _backGallery = this.FindControl<WrapPanel>("BackGallery")!;
            _backGalleryHint = this.FindControl<TextBlock>("BackGalleryHint")!;
            _backOpacitySlider = this.FindControl<Slider>("BackOpacitySlider")!;
            _backOpacityValue = this.FindControl<TextBlock>("BackOpacityValue")!;

            _backFitCombo.ItemsSource = BackdropFitLabels;
            _brightnessSlider = this.FindControl<Slider>("BrightnessSlider")!;
            _contrastSlider = this.FindControl<Slider>("ContrastSlider")!;
            _warmthSlider = this.FindControl<Slider>("WarmthSlider")!;
            _brightnessValue = this.FindControl<TextBlock>("BrightnessValue")!;
            _contrastValue = this.FindControl<TextBlock>("ContrastValue")!;
            _warmthValue = this.FindControl<TextBlock>("WarmthValue")!;
            _paperGallery = this.FindControl<WrapPanel>("PaperGallery")!;
            _paperGalleryHint = this.FindControl<TextBlock>("PaperGalleryHint")!;
            _paperFlowCombo = this.FindControl<ComboBox>("PaperFlowCombo")!;
            _paperFlowRow = this.FindControl<Grid>("PaperFlowRow")!;
            _paperFlowCombo.ItemsSource = PaperFlowLabels;
            _imageInfoText = this.FindControl<TextBlock>("ImageInfoText")!;
            _opacitySlider = this.FindControl<Slider>("OpacitySlider")!;
            _opacityValue = this.FindControl<TextBlock>("OpacityValue")!;
            _tileCheck = this.FindControl<CheckBox>("TileCheck")!;
            _duplicateBtn = this.FindControl<Button>("DuplicateBtn")!;
            _deleteBtn = this.FindControl<Button>("DeleteBtn")!;
            _previewBackdrop = this.FindControl<Border>("PreviewBackdrop")!;
            _previewBackdropImage = this.FindControl<Border>("PreviewBackdropImage")!;
            _previewPaper = this.FindControl<Border>("PreviewPaper")!;
            _previewPaperImage = this.FindControl<Border>("PreviewPaperImage")!;
            _previewWarm = this.FindControl<Border>("PreviewWarm")!;
            _previewDim = this.FindControl<Border>("PreviewDim")!;
            _previewHead = this.FindControl<TextBlock>("PreviewHead")!;
            _previewBody = this.FindControl<TextBlock>("PreviewBody")!;

            _themeList.ItemsSource = _rows;

            // Перетаскивание слушается на всём списке, а не на строке: указатель
            // уезжает с той строки, с которой начали, и события на ней обрываются.
            _themeList.AddHandler(PointerMovedEvent, OnThemeListPointerMoved, RoutingStrategies.Tunnel);
            _themeList.AddHandler(PointerReleasedEvent, OnThemeListPointerReleased, RoutingStrategies.Tunnel);
            _themeList.AddHandler(PointerCaptureLostEvent, OnThemeListCaptureLost, RoutingStrategies.Tunnel);

            this.FindControl<Button>("OkBtn")!.Click += OnOk;
            this.FindControl<Button>("CloseBtn")!.Click += OnCancel;
            this.FindControl<Button>("AddBtn")!.Click += OnAdd;
            this.FindControl<Button>("BrowseBtn")!.Click += OnBrowse;
            this.FindControl<Button>("ClearImageBtn")!.Click += OnClearImage;
            _duplicateBtn.Click += OnDuplicate;
            _deleteBtn.Click += OnDelete;

            _scrim.PointerPressed += OnScrimPressed;

            _nameBox.TextChanged += (_, _) => OnFieldsChanged();
            _scopeDocBtn.Click += (_, _) => SetScope(global: false);
            _scopeGlobalBtn.Click += (_, _) => SetScope(global: true);
            _sheetColorBtn.PropertyChanged += OnColorButtonChanged;
            _inkColorBtn.PropertyChanged += OnColorButtonChanged;
            _caretColorBtn.PropertyChanged += OnEditorColorChanged;
            _rulerSheetColorBtn.PropertyChanged += OnEditorColorChanged;
            _rulerInkColorBtn.PropertyChanged += OnEditorColorChanged;
            _rulerFieldColorBtn.PropertyChanged += OnEditorColorChanged;
            _tabMarkColorBtn.PropertyChanged += OnEditorColorChanged;
            _previewToggleBtn.Click += OnTogglePreview;
            _caretResetBtn.Click += OnResetEditorColor;
            _rulerSheetResetBtn.Click += OnResetEditorColor;
            _rulerInkResetBtn.Click += OnResetEditorColor;
            _rulerFieldResetBtn.Click += OnResetEditorColor;
            _tabMarkResetBtn.Click += OnResetEditorColor;
            _backColorBtn.PropertyChanged += OnColorButtonChanged;
            _backFitCombo.SelectionChanged += (_, _) => OnFieldsChanged();
            _backOpacitySlider.PropertyChanged += OnSliderChanged;
            this.FindControl<Button>("BackBrowseBtn")!.Click += OnBrowseBackdrop;
            this.FindControl<Button>("BackClearImageBtn")!.Click += OnClearBackdropImage;
            this.FindControl<Button>("BackResetBtn")!.Click += OnResetBackdrop;
            _brightnessSlider.PropertyChanged += OnSliderChanged;
            _contrastSlider.PropertyChanged += OnSliderChanged;
            _warmthSlider.PropertyChanged += OnSliderChanged;
            _opacitySlider.PropertyChanged += OnSliderChanged;
            _tileCheck.IsCheckedChanged += (_, _) => OnFieldsChanged();
            _paperFlowCombo.SelectionChanged += (_, _) => OnFieldsChanged();
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            TopLevel.GetTopLevel(this)?.AddHandler(KeyDownEvent, OnOverlayKeyDown, RoutingStrategies.Tunnel);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            TopLevel.GetTopLevel(this)?.RemoveHandler(KeyDownEvent, OnOverlayKeyDown);
            base.OnDetachedFromVisualTree(e);
        }

        private void OnOverlayKeyDown(object? sender, KeyEventArgs e)
        {
            if (!IsVisible) return;
            if (e.Key == Key.Escape) { CompleteCancel(); e.Handled = true; }
        }

        /// <summary>
        /// Показывает окно. themes — все доступные виды, selectedId — выбранный.
        /// Возвращает null, если человек отказался.
        /// </summary>
        public Task<ReadingThemeResult?> ShowAsync(
            IReadOnlyList<ReadingTheme> themes, string? selectedId)
            => ShowAsync(themes, selectedId, false);

        /// <summary>
        /// Показывает окно видов. С <paramref name="startWithNewTheme"/> окно сразу
        /// заводит новый вид копией выбранного и ставит курсор в имя: просьба
        /// «создать вид» приходит из ленты, а заводится вид здесь — держать вторую
        /// копию этого правила в вью-модели значит однажды их разойтись.
        /// </summary>
        public Task<ReadingThemeResult?> ShowAsync(
            IReadOnlyList<ReadingTheme> themes, string? selectedId, bool startWithNewTheme)
        {
            _tcs?.TrySetResult(null);
            _tcs = new TaskCompletionSource<ReadingThemeResult?>();

            _rows.Clear();
            foreach (var theme in themes)
                _rows.Add(new ReadingThemeRow(theme.Clone()));

            var start = _rows.FirstOrDefault(
                r => string.Equals(r.Theme.Id, selectedId, StringComparison.Ordinal)) ?? _rows.FirstOrDefault();

            Select(start);

            IsVisible = true;
            Focus();

            if (startWithNewTheme) AddCopy(NewThemeName);

            return _tcs.Task;
        }

        /// <summary>Имя, под которым заводится вид кнопкой «Создать».</summary>
        private const string NewThemeName = "Новый вид";

        private void Select(ReadingThemeRow? row)
        {
            foreach (var r in _rows) r.IsSelected = ReferenceEquals(r, row);
            _current = row;
            LoadFields();

            // Выбор вида — тоже правка: книга обязана показать его сразу, а не после
            // закрытия окна.
            ApplyNow();
        }

        private void LoadFields()
        {
            var theme = _current?.Theme;

            _loading = true;
            try
            {
                bool editable = theme is not null && !theme.IsBuiltIn;

                _nameBox.Text = theme?.Name ?? string.Empty;
                _nameBox.IsEnabled = editable;

                // Вид, доставшийся из прежних версий сразу в обеих областях, показывается
                // общим: он и так виден во всех проектах, и это та из двух областей, потерю
                // которой человек заметил бы сразу.
                ShowScope(theme?.IsGlobal == true);
                _scopeDocBtn.IsEnabled = editable;
                _scopeGlobalBtn.IsEnabled = editable;

                _sheetColorBtn.HexColor = theme?.SheetColor ?? "#FFFFFF";
                _inkColorBtn.HexColor = theme?.InkColor ?? "#1A1A1A";
                _sheetColorBtn.IsEnabled = editable;
                _inkColorBtn.IsEnabled = editable;

                // Раздел правки: поле без своего цвета показывается тем, который
                // выведется. Пустой образец не объяснил бы, что там сейчас.
                _caretAuto = string.IsNullOrWhiteSpace(theme?.CaretColor);
                _rulerSheetAuto = string.IsNullOrWhiteSpace(theme?.RulerSheetColor);
                _rulerInkAuto = string.IsNullOrWhiteSpace(theme?.RulerInkColor);
                _rulerFieldAuto = string.IsNullOrWhiteSpace(theme?.RulerFieldColor);
                _tabMarkAuto = string.IsNullOrWhiteSpace(theme?.TabMarkColor);

                _caretColorBtn.HexColor = _caretAuto ? AutoCaretHex(theme) : theme!.CaretColor!;
                _rulerSheetColorBtn.HexColor = _rulerSheetAuto
                    ? AutoRulerSheetHex(theme) : theme!.RulerSheetColor!;
                _rulerInkColorBtn.HexColor = _rulerInkAuto
                    ? AutoRulerInkHex(theme) : theme!.RulerInkColor!;
                _rulerFieldColorBtn.HexColor = _rulerFieldAuto
                    ? AutoRulerFieldHex(theme) : theme!.RulerFieldColor!;
                _tabMarkColorBtn.HexColor = _tabMarkAuto
                    ? DefaultTabMarkHex : theme!.TabMarkColor!;

                _caretColorBtn.IsEnabled = editable;
                _caretResetBtn.IsEnabled = editable;
                _rulerSheetColorBtn.IsEnabled = editable;
                _rulerSheetResetBtn.IsEnabled = editable;
                _rulerInkColorBtn.IsEnabled = editable;
                _rulerInkResetBtn.IsEnabled = editable;
                _rulerFieldColorBtn.IsEnabled = editable;
                _rulerFieldResetBtn.IsEnabled = editable;
                _tabMarkColorBtn.IsEnabled = editable;
                _tabMarkResetBtn.IsEnabled = editable;

                // Фон, выводимый из бумаги, показывается тем цветом, который он и
                // получит: пустой образец не объясняет, что там сейчас.
                _backFitCombo.SelectedIndex = (int)(theme?.BackdropImageFit ?? ReadingBackdropFit.Cover);

                _backColorBtn.HexColor = string.IsNullOrWhiteSpace(theme?.BackdropColor)
                    ? DerivedBackdropHex(theme?.SheetColor)
                    : theme!.BackdropColor!;

                _backOpacitySlider.Value = Math.Clamp(theme?.BackdropImageOpacity ?? 1.0, 0.0, 1.0) * 100.0;

                _backImages.Clear();
                if (theme is not null) _backImages.AddRange(theme.BackdropImagePaths);

                ApplyBackdropEnablement(editable, _backImages.Count > 0);

                _brightnessSlider.Value = Math.Clamp(theme?.Brightness ?? 1.0, 0.35, 1.0) * 100.0;
                _contrastSlider.Value = Math.Clamp(theme?.Contrast ?? 1.0, 0.6, 1.6) * 100.0;
                _warmthSlider.Value = Math.Clamp(theme?.Warmth ?? 0.0, 0.0, 1.0) * 100.0;
                _brightnessSlider.IsEnabled = editable;
                _contrastSlider.IsEnabled = editable;
                _warmthSlider.IsEnabled = editable;

                _paperImages.Clear();
                if (theme is not null) _paperImages.AddRange(theme.ImagePaths);

                _paperFlowCombo.SelectedIndex = (int)(theme?.ImageFlow ?? ReadingImageFlow.Sequence);
                _paperFlowCombo.IsEnabled = editable;

                _opacitySlider.Value = Math.Clamp(theme?.ImageOpacity ?? 1.0, 0.0, 1.0) * 100.0;
                _tileCheck.IsChecked = theme?.ImageTile == true;
                _opacitySlider.IsEnabled = editable;
                _tileCheck.IsEnabled = editable;

                RebuildGalleries(editable);

                _deleteBtn.IsEnabled = editable;
                _duplicateBtn.IsEnabled = theme is not null;
            }
            finally
            {
                _loading = false;
            }

            UpdateValueLabels();
            UpdateScopeHint();
            UpdateImageInfo();
            UpdatePreview();
        }

        private void OnColorButtonChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property != ColorPickerButton.HexColorProperty) return;
            OnFieldsChanged();
        }

        private void OnSliderChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property != RangeBase.ValueProperty) return;
            OnFieldsChanged();
        }

        /// <summary>Собирает выбранный вид из полей.</summary>
        private void OnFieldsChanged()
        {
            if (_loading) return;
            if (_current?.Theme is not { } theme) return;
            if (theme.IsBuiltIn)
            {
                // Встроенный вид не правится: поля заблокированы, но событие могло
                // прийти от программной подстановки — просто выходим.
                UpdateValueLabels();
                return;
            }

            theme.Name = string.IsNullOrWhiteSpace(_nameBox.Text) ? "Без имени" : _nameBox.Text!;
            theme.SheetColor = _sheetColorBtn.HexColor;
            theme.InkColor = _inkColorBtn.HexColor;

            // Раздел правки. Пустое поле означает «вывести»: цвет тогда идёт за
            // бумагой и чернилами, а записанное значение застыло бы на месте и
            // перестало меняться вместе с видом.
            theme.CaretColor = _caretAuto ? null : _caretColorBtn.HexColor;
            theme.RulerSheetColor = _rulerSheetAuto ? null : _rulerSheetColorBtn.HexColor;
            theme.RulerInkColor = _rulerInkAuto ? null : _rulerInkColorBtn.HexColor;
            theme.RulerFieldColor = _rulerFieldAuto ? null : _rulerFieldColorBtn.HexColor;
            theme.TabMarkColor = _tabMarkAuto ? null : _tabMarkColorBtn.HexColor;

            SyncAutoEditorSwatches(theme);

            // Заливка, совпадающая с выводимой из бумаги, своей не считается: иначе
            // «От бумаги» выключалось бы само собой, стоило образцу показать этот цвет.
            string backHex = _backColorBtn.HexColor;
            theme.BackdropColor =
                string.Equals(backHex, DerivedBackdropHex(theme.SheetColor), StringComparison.OrdinalIgnoreCase)
                    ? null
                    : backHex;

            theme.BackdropImageFit = (ReadingBackdropFit)Math.Clamp(_backFitCombo.SelectedIndex, 0, 3);
            theme.BackdropImageOpacity = Math.Clamp(_backOpacitySlider.Value / 100.0, 0.0, 1.0);

            theme.BackdropImagePaths.Clear();
            theme.BackdropImagePaths.AddRange(_backImages);

            ApplyBackdropEnablement(!theme.IsBuiltIn, _backImages.Count > 0);

            theme.Brightness = Math.Clamp(_brightnessSlider.Value / 100.0, 0.35, 1.0);
            theme.Contrast = Math.Clamp(_contrastSlider.Value / 100.0, 0.6, 1.6);
            theme.Warmth = Math.Clamp(_warmthSlider.Value / 100.0, 0.0, 1.0);

            theme.ImagePaths.Clear();
            theme.ImagePaths.AddRange(_paperImages);

            theme.ImageFlow = (ReadingImageFlow)Math.Clamp(_paperFlowCombo.SelectedIndex, 0, 2);
            theme.ImageOpacity = Math.Clamp(_opacitySlider.Value / 100.0, 0.0, 1.0);
            theme.ImageTile = _tileCheck.IsChecked == true;

            _current.RefreshAll();
            UpdateValueLabels();
            UpdateScopeHint();
            UpdateImageInfo();
            UpdatePreview();

            // Правка уходит наружу сама, когда рука отпустит ползунок.
            ScheduleApply();
        }

        // ── Пример вида ───────────────────────────────────────────────────

        /// <summary>
        /// Держит пример в согласии с полями. Слои те же и в том же порядке, каким
        /// их кладёт на страницу канвас чтения: цвет листа, картинка бумаги, буквы,
        /// свет поверх всего.
        /// </summary>
        private void UpdatePreview()
        {
            var theme = _current?.Theme;

            var paper = ParsePreviewColor(theme?.SheetColor, Color.FromRgb(0xFF, 0xFF, 0xFF));
            var ink = ParsePreviewColor(theme?.InkColor, Color.FromRgb(0x1A, 0x1A, 0x1A));

            // Контрастность разводит буквы и бумагу — расчёт тот же, что в канвасе:
            // сто процентов оставляют цвет вида как есть, меньше сводят его к листу.
            double contrast = Math.Clamp(theme?.Contrast ?? 1.0, 0.6, 1.6);
            ink = Color.FromRgb(
                SpreadChannel(paper.R, ink.R, contrast),
                SpreadChannel(paper.G, ink.G, contrast),
                SpreadChannel(paper.B, ink.B, contrast));

            _previewPaper.Background = new SolidColorBrush(paper);
            _previewHead.Foreground = new SolidColorBrush(ink);
            _previewBody.Foreground = new SolidColorBrush(ink);

            // Пример набирается тем же шрифтом, что и окно: вид на гарнитуру не
            // влияет, и обещать здесь чужое начертание значит обещать несбыточное.
            _previewHead.FontFamily = FontFamily.Default;
            _previewBody.FontFamily = FontFamily.Default;

            var paperBrush = PreviewPaperBrush(theme);
            _previewPaperImage.Background = paperBrush;
            _previewPaperImage.Opacity = Math.Clamp(theme?.ImageOpacity ?? 1.0, 0.0, 1.0);
            _previewPaperImage.IsVisible = paperBrush is not null;

            // Поле вокруг книги: сперва заливка, затем картинка поверх неё — тот же
            // порядок, каким их кладёт канвас. Заливки нет — цвет выводится из бумаги
            // ровно так же, как это делает чтение.
            var backdropSolid = Rendering.SKTextRenderer.GradientSolidColor(
                string.IsNullOrWhiteSpace(theme?.BackdropColor)
                    ? DerivedBackdropHex(theme?.SheetColor)
                    : theme!.BackdropColor,
                new SKColor(0xE8, 0xE8, 0xE8));

            _previewBackdrop.Background = new SolidColorBrush(
                Color.FromRgb(backdropSolid.Red, backdropSolid.Green, backdropSolid.Blue));

            var backdropBrush = theme is { HasBackdropImage: true }
                ? PreviewBackdropBrush(theme)
                : null;
            _previewBackdropImage.Background = backdropBrush;
            _previewBackdropImage.Opacity = Math.Clamp(theme?.BackdropImageOpacity ?? 1.0, 0.0, 1.0);
            _previewBackdropImage.IsVisible = backdropBrush is not null;

            // Тёплота в канвасе умножает цвета. Плёнка янтаря с такой прозрачностью
            // даёт на белой бумаге ровно тот же цвет, что и умножение, а на цветной
            // расходится с ним настолько мало, что примеру этого достаточно.
            double warmth = Math.Clamp(theme?.Warmth ?? 0.0, 0.0, 1.0);
            _previewWarm.Background = new SolidColorBrush(
                Color.FromArgb((byte)Math.Clamp(warmth * 127.0, 0.0, 255.0), 255, 175, 15));

            double brightness = Math.Clamp(theme?.Brightness ?? 1.0, 0.35, 1.0);
            _previewDim.Background = new SolidColorBrush(
                Color.FromArgb((byte)Math.Clamp((1.0 - brightness) * 255.0, 0.0, 200.0), 0, 0, 0));
        }

        /// <summary>Разводит канал текста и бумаги — тот же расчёт, что в канвасе.</summary>
        private static byte SpreadChannel(byte paper, byte ink, double factor)
            => (byte)Math.Clamp(paper + (ink - paper) * factor, 0.0, 255.0);

        private static Color ParsePreviewColor(string? hex, Color fallback)
            => !string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex, out var c) ? c : fallback;

        // Картинка бумаги примера держится по адресу и способу укладки: пример
        // пересобирается на каждое движение ползунка, а читать и раскодировать файл
        // на каждый шаг нельзя.
        private string? _previewPaperKey;
        private IBrush? _previewPaperCache;

        /// <summary>Кисть картинки бумаги для примера. Картинки нет — null.</summary>
        private IBrush? PreviewPaperBrush(ReadingTheme? theme)
        {
            // Пример показывает первую картинку набора: он про то, как выглядит вид,
            // а не про то, каким по счёту листом его открыли.
            string? reference = theme?.PaperImageFor(0);
            if (theme is null || string.IsNullOrWhiteSpace(reference))
            {
                _previewPaperKey = null;
                _previewPaperCache = null;
                return null;
            }

            string key = reference + (theme.ImageTile ? "|tile" : "|fill");
            if (string.Equals(_previewPaperKey, key, StringComparison.Ordinal))
                return _previewPaperCache;

            _previewPaperKey = key;
            _previewPaperCache = null;

            try
            {
                var data = Models.Settings.ReadingAssets.Read(reference);
                if (data is null || data.Length == 0) return null;

                using var stream = new MemoryStream(data);
                var bitmap = new Bitmap(stream);

                _previewPaperCache = theme.ImageTile
                    ? new ImageBrush(bitmap)
                    {
                        TileMode = TileMode.Tile,
                        Stretch = Stretch.None,
                        DestinationRect = new RelativeRect(
                            0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height, RelativeUnit.Absolute)
                    }
                    : new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
            }
            catch (Exception ex)
            {
                // Битый файл не должен ронять окно: пример просто останется без бумаги.
                System.Diagnostics.Debug.WriteLine("Preview paper image failed: " + ex.Message);
                _previewPaperCache = null;
            }

            return _previewPaperCache;
        }

        // Картинка поля примера держится по адресу и способу укладки — по той же
        // причине, что и картинка бумаги: пример пересобирается на каждое движение
        // ползунка, а читать и раскодировать файл на каждый шаг нельзя.
        private string? _previewBackdropKey;
        private IBrush? _previewBackdropCache;

        /// <summary>Кисть картинки поля для примера. Картинки нет — null.</summary>
        private IBrush? PreviewBackdropBrush(ReadingTheme? theme)
        {
            string? reference = theme?.BackdropImageFor();
            if (theme is null || string.IsNullOrWhiteSpace(reference))
            {
                _previewBackdropKey = null;
                _previewBackdropCache = null;
                return null;
            }

            string key = reference + "|" + theme.BackdropImageFit;
            if (string.Equals(_previewBackdropKey, key, StringComparison.Ordinal))
                return _previewBackdropCache;

            _previewBackdropKey = key;
            _previewBackdropCache = null;

            try
            {
                var data = Models.Settings.ReadingAssets.Read(reference);
                if (data is null || data.Length == 0) return null;

                using var stream = new MemoryStream(data);
                var bitmap = new Bitmap(stream);

                _previewBackdropCache = theme.BackdropImageFit switch
                {
                    ReadingBackdropFit.Tile => new ImageBrush(bitmap)
                    {
                        TileMode = TileMode.Tile,
                        Stretch = Stretch.None,
                        DestinationRect = new RelativeRect(
                            0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height, RelativeUnit.Absolute)
                    },
                    ReadingBackdropFit.Stretch => new ImageBrush(bitmap) { Stretch = Stretch.Fill },
                    ReadingBackdropFit.Contain => new ImageBrush(bitmap) { Stretch = Stretch.Uniform },
                    _ => new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill }
                };
            }
            catch (Exception ex)
            {
                // Битый файл не должен ронять окно: пример просто останется без поля.
                System.Diagnostics.Debug.WriteLine("Preview backdrop image failed: " + ex.Message);
                _previewBackdropCache = null;
            }

            return _previewBackdropCache;
        }

        private void UpdateValueLabels()
        {
            _brightnessValue.Text = $"{Math.Round(_brightnessSlider.Value)}%";
            _contrastValue.Text = $"{Math.Round(_contrastSlider.Value)}%";
            _warmthValue.Text = $"{Math.Round(_warmthSlider.Value)}%";
            _opacityValue.Text = $"{Math.Round(_opacitySlider.Value)}%";
            _backOpacityValue.Text = $"{Math.Round(_backOpacitySlider.Value)}%";
        }

        private void UpdateScopeHint()
        {
            var theme = _current?.Theme;
            if (theme is null) { _scopeHint.Text = string.Empty; return; }

            if (theme.IsBuiltIn)
            {
                _scopeHint.Text = "Встроенный вид есть всегда и во всех проектах. "
                                + "Чтобы изменить его — сделайте копию кнопкой «Дублировать».";
                return;
            }

            _scopeHint.Text = theme.IsGlobal
                ? "Доступен во всех проектах, но с этой рукописью не уедет."
                : "Уедет вместе с рукописью. В других проектах его не будет.";
        }

        /// <summary>
        /// Переносит вид в другую область хранения. Областей две, и они исключают друг
        /// друга: вид, лежащий сразу в документе и в общих настройках, — это две его копии,
        /// которые разойдутся при первой же правке одной из них, а человек будет думать,
        /// что правит одну вещь. Кому нужно и то, и другое, делает дубль — тогда копий
        /// честно две, и обе на виду.
        /// </summary>
        private void SetScope(bool global)
        {
            if (_loading) return;
            if (_current?.Theme is not { } theme || theme.IsBuiltIn) return;
            if (theme.IsGlobal == global && theme.InDocument == !global) return;

            theme.IsGlobal = global;
            theme.InDocument = !global;

            ShowScope(global);

            _current.RefreshAll();
            UpdateScopeHint();
            ApplyNow();
        }

        /// <summary>Ставит переключатель в положение области: плашка едет, стороны меняют цвет.</summary>
        private void ShowScope(bool global)
        {
            _scopeThumb.Classes.Set("right", global);
            _scopeDocBtn.Classes.Set("active", !global);
            _scopeGlobalBtn.Classes.Set("active", global);
        }

        /// <summary>
        /// Показывает размеры выбранной картинки: человеку, кладущему свою бумагу,
        /// это первое, что нужно знать.
        /// </summary>
        private void UpdateImageInfo()
        {
            var theme = _current?.Theme;

            // Набор из нескольких описывается числом и первой картинкой: строка про
            // размеры каждой из десяти заняла бы пол-окна.
            if (theme is not null && theme.ImagePaths.Count > 1)
            {
                _imageInfoText.Text =
                    $"В наборе {theme.ImagePaths.Count} картинок. Первая: {DescribeImage(theme.ImagePaths[0])}";
                return;
            }

            _imageInfoText.Text = DescribeImage(theme?.PaperImageFor(0));
        }

        /// <summary>
        /// Строка о картинке: размеры, вес и где она лежит.
        ///
        /// Место хранения здесь не мелочь. Картинка, оставшаяся путём к файлу на
        /// диске, работает только на этой машине: перенесли папку — и бумага
        /// пропала, отдали проект — и у того, кому отдали, её не было никогда.
        /// Сказать об этом надо там, где картинку выбирают, а не выяснять потом
        /// по пустому листу.
        /// </summary>
        private static string DescribeImage(string? reference)
        {
            if (string.IsNullOrWhiteSpace(reference))
                return "Размеры картинки появятся здесь после выбора файла.";

            var data = Models.Settings.ReadingAssets.Read(reference);
            if (data is null || data.Length == 0)
                return Models.Settings.ReadingAssets.IsDiskPath(reference)
                    ? "Файл не найден по этому пути."
                    : "Картинка не найдена в хранилище вида.";

            try
            {
                using var stream = new MemoryStream(data);
                var bitmap = new Bitmap(stream);
                var size = bitmap.PixelSize;

                string place = Models.Settings.ReadingAssets.IsProjectRef(reference)
                    ? " Лежит в проекте — уедет вместе с ним."
                    : Models.Settings.ReadingAssets.IsAppRef(reference)
                        ? " Лежит в программе — доступна во всех проектах."
                        : " Файл на диске: с проектом не уедет и потеряется при переносе папки.";

                string hint = size.Width < 600 || size.Height < 600
                    ? " Для растягивания на весь лист этого мало — лучше замостить."
                    : string.Empty;

                return $"{size.Width} × {size.Height} точек, {data.Length / 1024} КБ.{hint}{place}";
            }
            catch (Exception ex)
            {
                return "Картинку прочитать не удалось: " + ex.Message;
            }
        }

        // ── Список ────────────────────────────────────────────────────────

        private void OnThemeRowPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Border { Tag: ReadingThemeRow row }) return;
            Select(row);

            // Переставлять можно любой вид, встроенный в том числе: порядок в списке
            // это и есть порядок под руками человека, и запрещать ему двигать шесть
            // видов из десяти значит оставить список наполовину чужим. Сам порядок
            // хранится в настройках программы, поэтому переживает и перезапуск.
            _dragRow = row;
            _dragStartY = _themeList.ItemsPanelRoot is { } startPanel
                ? e.GetPosition(startPanel).Y
                : e.GetPosition(_themeList).Y;
            _dragging = false;

            e.Handled = true;
        }

        /// <summary>Прячет вид из списков выбора и возвращает обратно.</summary>
        private void OnToggleHidden(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: ReadingThemeRow row }) return;

            row.IsHidden = !row.IsHidden;
            ApplyNow();
            e.Handled = true;
        }

        // ── Перетаскивание строк ──────────────────────────────────────────
        // Список правится руками, и порядок в нём — тоже правка: свои виды человек
        // раскладывает так, как ими пользуется.
        //
        // Пока строку ведут, сам список не трогается вовсе: коллекция остаётся
        // прежней, двигаются только картинки строк — взятая едет за указателем,
        // соседи расступаются на её высоту. Перестановка случается один раз, на
        // отпускании.
        //
        // Так сделано не ради красоты. Пока порядок правился прямо по ходу, каждая
        // перестановка меняла раскладку под курсором: строка, только что уехавшая
        // вниз, оказывалась под ним снова и просилась обратно — список трясло между
        // двумя положениями, и порогами это не лечилось, потому что причина в самой
        // перестановке, а не в том, где у неё граница.

        private ReadingThemeRow? _dragRow;
        private int _dragFrom = -1;
        private int _dragTo = -1;
        private double _dragStartY;
        private double _dragRowTop;
        private double _dragRowHeight;
        private double _dragStep;
        private bool _dragging;

        /// <summary>Порог, после которого нажатие считается перетаскиванием.</summary>
        private const double DragThresholdPx = 6.0;

        /// <summary>За сколько сосед уходит с дороги.</summary>
        private static readonly TimeSpan RowSlide = TimeSpan.FromMilliseconds(150);

        private void OnThemeListPointerMoved(object? sender, PointerEventArgs e)
        {
            if (_dragRow is null) return;

            if (!e.GetCurrentPoint(_themeList).Properties.IsLeftButtonPressed)
            {
                EndRowDrag(commit: false);
                return;
            }

            var panel = _themeList.ItemsPanelRoot;
            if (panel is null) return;

            double y = e.GetPosition(panel).Y;

            if (!_dragging)
            {
                if (Math.Abs(y - _dragStartY) < DragThresholdPx) return;
                if (!BeginRowDrag())
                {
                    EndRowDrag(commit: false);
                    return;
                }
            }

            double shift = y - _dragStartY;

            if (_themeList.ContainerFromIndex(_dragFrom) is Control dragged)
                dragged.RenderTransform = Translate(shift);

            LayOutNeighbours(shift);
        }

        private void OnThemeListPointerReleased(object? sender, PointerReleasedEventArgs e)
            => EndRowDrag(commit: true);

        private void OnThemeListCaptureLost(object? sender, PointerCaptureLostEventArgs e)
            => EndRowDrag(commit: false);

        /// <summary>Готовит перетаскивание: запоминает место строки и поднимает её над списком.</summary>
        private bool BeginRowDrag()
        {
            if (_dragRow is null) return false;

            _dragFrom = _rows.IndexOf(_dragRow);
            if (_dragFrom < 0) return false;
            if (_themeList.ContainerFromIndex(_dragFrom) is not Control container) return false;

            _dragRowTop = container.Bounds.Y;
            _dragRowHeight = container.Bounds.Height;
            _dragStep = RowStep();
            _dragTo = _dragFrom;
            _dragging = true;

            // Взятая строка идёт поверх соседей и без перехода: она обязана быть ровно
            // под указателем, а не догонять его.
            container.Transitions = null;
            container.ZIndex = 20;
            container.Opacity = 0.92;

            for (int i = 0; i < _rows.Count; i++)
            {
                if (i == _dragFrom) continue;
                if (_themeList.ContainerFromIndex(i) is not Control neighbour) continue;

                neighbour.RenderTransform = null;
                neighbour.Transitions = new Transitions
                {
                    new TransformOperationsTransition
                    {
                        Property = Visual.RenderTransformProperty,
                        Duration = RowSlide,
                        Easing = new CubicEaseOut()
                    }
                };
            }

            return true;
        }

        /// <summary>
        /// Расставляет соседей вокруг взятой строки и запоминает, куда она метит.
        /// Место считается по её середине: она прошла середину соседа — сосед уходит
        /// с дороги.
        /// </summary>
        private void LayOutNeighbours(double shift)
        {
            double center = _dragRowTop + shift + _dragRowHeight / 2;

            int to = 0;
            for (int i = 0; i < _rows.Count; i++)
            {
                if (i == _dragFrom) continue;
                if (_themeList.ContainerFromIndex(i) is not Control neighbour) continue;
                if (center > neighbour.Bounds.Y + neighbour.Bounds.Height / 2) to++;
            }

            _dragTo = to;

            for (int i = 0; i < _rows.Count; i++)
            {
                if (i == _dragFrom) continue;
                if (_themeList.ContainerFromIndex(i) is not Control neighbour) continue;

                double step = 0;
                if (to > _dragFrom && i > _dragFrom && i <= to) step = -_dragStep;
                else if (to < _dragFrom && i >= to && i < _dragFrom) step = _dragStep;

                neighbour.RenderTransform = Translate(step);
            }
        }

        /// <summary>
        /// Заканчивает перетаскивание: снимает смещения и, если строку отпустили на
        /// новом месте, переставляет её в списке.
        /// </summary>
        private void EndRowDrag(bool commit)
        {
            if (_dragging)
            {
                // Сначала всё возвращается на места и без переходов: список сейчас
                // переставит строки сам, и поехавший вдогонку сдвиг смазал бы итог.
                for (int i = 0; i < _rows.Count; i++)
                {
                    if (_themeList.ContainerFromIndex(i) is not Control container) continue;

                    container.Transitions = null;
                    container.RenderTransform = null;
                    container.ZIndex = 0;
                    container.Opacity = 1.0;
                }

                if (commit && _dragFrom >= 0 && _dragTo >= 0 && _dragTo != _dragFrom)
                {
                    _rows.Move(_dragFrom, _dragTo);
                    ApplyNow();
                }
            }

            _dragRow = null;
            _dragFrom = -1;
            _dragTo = -1;
            _dragging = false;
        }

        /// <summary>Расстояние между соседними строками: высота строки плюс промежуток.</summary>
        private double RowStep()
        {
            double? previous = null;

            for (int i = 0; i < _rows.Count; i++)
            {
                if (_themeList.ContainerFromIndex(i) is not Control container) continue;

                if (previous is { } top) return Math.Max(1.0, container.Bounds.Y - top);
                previous = container.Bounds.Y;
            }

            return Math.Max(1.0, _dragRowHeight);
        }

        /// <summary>Сдвиг по вертикали — тем же способом, каким его понимает разметка.</summary>
        private static ITransform Translate(double dy)
            => TransformOperations.Parse(
                "translateY(" + dy.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "px)");

        private void OnAdd(object? sender, RoutedEventArgs e) => AddCopy(NewThemeName);

        private void OnDuplicate(object? sender, RoutedEventArgs e)
        {
            var source = _current?.Theme ?? ReadingTheme.FindBuiltIn(ReadingTheme.WhiteId);
            string baseName = _current?.Theme.Name ?? "Вид";
            AddTheme(source, baseName + " (копия)");
        }

        /// <summary>
        /// Заводит новый вид копией выбранного. От знакомой точки настраивать проще,
        /// чем от белого листа, а встроенный вид иначе и не изменить.
        /// </summary>
        /// <summary>
        /// Заводит вид с чистого листа: белая бумага, чёрные буквы, никаких
        /// картинок. Копией выбранного он больше не создаётся — «создать» и
        /// «дублировать» стали разными действиями, а раньше кнопка «Создать»
        /// молча приносила чужие настройки, и человек правил не то, что думал.
        /// </summary>
        private void AddCopy(string name)
            => AddTheme(ReadingTheme.FindBuiltIn(ReadingTheme.WhiteId), name);

        /// <summary>Кладёт в список новый вид, сделанный по образцу.</summary>
        private void AddTheme(ReadingTheme source, string name)
        {
            var copy = source.CopyAs(UniqueName(name));

            // Новый вид лежит в программе и доступен во всех проектах. В рукопись он
            // не кладётся сам: вид уезжает с ней и остаётся у получателя навсегда, а
            // такое решение принимают осознанно — переключателем «Где хранить».
            copy.InDocument = false;
            copy.IsGlobal = true;
            copy.IsHidden = false;

            var row = new ReadingThemeRow(copy);
            _rows.Add(row);
            Select(row);
            ApplyNow();
            _nameBox.Focus();
            _nameBox.SelectAll();
        }

        private string UniqueName(string wanted)
        {
            bool Taken(string n) => _rows.Any(
                r => string.Equals(r.Theme.Name, n, StringComparison.CurrentCultureIgnoreCase));

            if (!Taken(wanted)) return wanted;

            for (int i = 2; i < 1000; i++)
            {
                string candidate = $"{wanted} {i}";
                if (!Taken(candidate)) return candidate;
            }
            return wanted;
        }

        private void OnDelete(object? sender, RoutedEventArgs e)
        {
            if (_current is not { } row) return;
            if (row.Theme.IsBuiltIn) return;

            int index = _rows.IndexOf(row);
            _rows.Remove(row);

            var next = _rows.ElementAtOrDefault(Math.Min(index, _rows.Count - 1)) ?? _rows.FirstOrDefault();
            Select(next);
            ApplyNow();
        }

        // ── Картинка ──────────────────────────────────────────────────────

        private async void OnBrowse(object? sender, RoutedEventArgs e)
            => await AddImagesAsync(_paperImages, "Картинки бумаги");

        private void OnClearImage(object? sender, RoutedEventArgs e)
        {
            if (_paperImages.Count == 0) return;
            _paperImages.Clear();
            OnGalleryChanged();
        }

        private async void OnBrowseBackdrop(object? sender, RoutedEventArgs e)
            => await AddImagesAsync(_backImages, "Картинки поля");

        private void OnClearBackdropImage(object? sender, RoutedEventArgs e)
        {
            if (_backImages.Count == 0) return;
            _backImages.Clear();
            OnGalleryChanged();
        }

        /// <summary>
        /// Добавляет картинки в набор — обычным выбором файлов, сразу нескольких.
        ///
        /// Никаких папок и библиотек здесь нет намеренно. Набор принадлежит ЭТОМУ
        /// виду, и раскладывать его по хранилищам незачем: нужен другой набор —
        /// заводится копия вида, это одна кнопка. А где картинкам жить — в документе
        /// или во всех проектах — решает область самого вида, тут же, в шапке окна.
        /// </summary>
        private async Task AddImagesAsync(List<string> set, string title)
        {
            if (_current is null || _current.Theme.IsBuiltIn) return;

            var top = TopLevel.GetTopLevel(this);
            if (top?.StorageProvider is not { } storage) return;

            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = true,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Изображения")
                    {
                        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp" }
                    }
                }
            });

            if (files.Count == 0) return;

            bool added = false;
            foreach (var file in files)
            {
                string? path = file.TryGetLocalPath();
                if (string.IsNullOrWhiteSpace(path)) continue;

                added |= AddImage(set, StoreImage(path!));
            }

            if (added) OnGalleryChanged();
        }

        private static bool AddImage(List<string> set, string? reference)
        {
            if (string.IsNullOrWhiteSpace(reference)) return false;
            if (set.Contains(reference!)) return false;

            set.Add(reference!);
            return true;
        }

        /// <summary>
        /// Уложить выбранный файл в хранилище вида и вернуть его адрес.
        ///
        /// Путь к файлу на диске здесь не годится: вид переживает и переезд
        /// папки, и передачу проекта другому человеку, а путь — ни того, ни
        /// другого. Копия ложится в архив проекта, когда он открыт, и в данные
        /// программы, когда нет; окончательно по областям вида картинки
        /// разложит сохранение (TextEditorViewModel.SaveReadingThemes).
        ///
        /// Не уложилось — остаётся прежний путь: он хотя бы работает здесь и
        /// сейчас, а о том, что картинка лежит снаружи, скажет проверка проекта.
        /// </summary>
        private static string StoreImage(string path)
        {
            var stored = Models.Settings.ReadingAssets.EnsureInProject(path);
            if (Models.Settings.ReadingAssets.IsProjectRef(stored)) return stored!;

            return Models.Settings.ReadingAssets.EnsureInAppStore(path) ?? path;
        }

        // ── Плитки наборов ────────────────────────────────────────────────

        /// <summary>Набор изменился: перерисовать плитки и пересобрать вид.</summary>
        private void OnGalleryChanged()
        {
            RebuildGalleries(_current is { Theme.IsBuiltIn: false });
            OnFieldsChanged();
        }

        private void RebuildGalleries(bool editable)
        {
            BuildGallery(_paperGallery, _paperGalleryHint, _paperImages, editable,
                "Картинок нет — лист заливается цветом.");

            BuildGallery(_backGallery, _backGalleryHint, _backImages, editable,
                "Картинки нет — поле заливается цветом.");

            // Как разбирать набор, спрашивается только когда набор есть: одной
            // картинке очередь ни к чему.
            _paperFlowRow.IsVisible = _paperImages.Count > 1;
        }

        /// <summary>
        /// Плитки набора: сама картинка, а не её путь. Путь ничего не говорит о том,
        /// как лист будет выглядеть, а плитка говорит всё.
        /// </summary>
        private void BuildGallery(
            WrapPanel host, TextBlock hint, List<string> set, bool editable, string emptyText)
        {
            host.Children.Clear();
            host.IsVisible = set.Count > 0;

            hint.IsVisible = set.Count == 0;
            hint.Text = emptyText;

            for (int i = 0; i < set.Count; i++)
                host.Children.Add(BuildTile(set, i, editable));
        }

        private Control BuildTile(List<string> set, int index, bool editable)
        {
            string reference = set[index];
            bool first = index == 0;

            var tile = new Border
            {
                Width = 74,
                Height = 52,
                CornerRadius = new CornerRadius(4),
                ClipToBounds = true,
                BorderThickness = new Thickness(first ? 2 : 1),
                BorderBrush = new SolidColorBrush(first
                    ? Color.FromRgb(0x6A, 0xA9, 0xFF)
                    : Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)),
                Background = ThumbBrush(reference)
                    ?? new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF))
            };

            ToolTip.SetTip(tile, System.IO.Path.GetFileName(reference));

            if (editable)
            {
                // Щелчок ставит картинку первой. Для поля это и есть выбор: на экране
                // показывается первая. Для бумаги — начало очереди.
                tile.PointerPressed += (_, _) =>
                {
                    if (index <= 0 || index >= set.Count) return;

                    set.RemoveAt(index);
                    set.Insert(0, reference);
                    OnGalleryChanged();
                };
            }

            var shell = new Panel { Margin = new Thickness(0, 0, 6, 6) };
            shell.Children.Add(tile);

            if (editable)
            {
                var remove = new Button
                {
                    Content = "✕",
                    FontSize = 10,
                    Width = 18,
                    Height = 18,
                    Padding = new Thickness(0),
                    Margin = new Thickness(0, 2, 2, 0),
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                    HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x20, 0x20, 0x20)),
                    Foreground = Brushes.White,
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(9)
                };

                ToolTip.SetTip(remove, "Убрать картинку из набора");

                remove.Click += (_, args) =>
                {
                    args.Handled = true;
                    if (index < 0 || index >= set.Count) return;

                    set.RemoveAt(index);
                    OnGalleryChanged();
                };

                shell.Children.Add(remove);
            }

            return shell;
        }

        // Разложенные в пиксели миниатюры — по адресу картинки. Окно пересобирает
        // плитки на каждое изменение набора, и читать файлы заново на каждый щелчок
        // незачем.
        private readonly Dictionary<string, IBrush?> _thumbCache = new(StringComparer.Ordinal);

        /// <summary>Кисть миниатюры. Файла нет или он битый — null, плитка останется пустой.</summary>
        private IBrush? ThumbBrush(string reference)
        {
            if (_thumbCache.TryGetValue(reference, out var cached)) return cached;

            IBrush? brush = null;
            try
            {
                var data = Models.Settings.ReadingAssets.Read(reference);
                if (data is not null && data.Length > 0)
                {
                    using var stream = new MemoryStream(data);
                    brush = new ImageBrush(new Bitmap(stream)) { Stretch = Stretch.UniformToFill };
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Thumbnail failed: " + ex.Message);
            }

            _thumbCache[reference] = brush;
            return brush;
        }

        // ── Пример ────────────────────────────────────────────────────────

        /// <summary>Сворачивает и разворачивает пример.</summary>
        private void OnTogglePreview(object? sender, RoutedEventArgs e)
        {
            _previewCollapsed = !_previewCollapsed;
            ApplyPreviewCollapsed();
        }

        /// <summary>
        /// Приводит пример к состоянию. Высота и прозрачность меняются через переходы,
        /// заданные разметкой, поэтому здесь просто ставятся конечные значения.
        /// </summary>
        private void ApplyPreviewCollapsed()
        {
            _previewFrame.Height = _previewCollapsed ? 0.0 : PreviewHeightPx;
            _previewFrame.Opacity = _previewCollapsed ? 0.0 : 1.0;

            // Стрелка ложится набок, когда бумага убрана: то же, что у любого
            // свёрнутого раздела, и понятно без подписи.
            _previewChevron.RenderTransform = _previewCollapsed
                ? TransformOperations.Parse("rotate(-90deg)")
                : TransformOperations.Parse("rotate(0deg)");

            TooltipBehavior.SetTip(_previewToggleBtn,
                _previewCollapsed ? "Развернуть пример" : "Свернуть пример");
        }

        // ── Раздел правки ─────────────────────────────────────────────────

        /// <summary>Цвет каретки, когда своего нет: цвет текста.</summary>
        private static string AutoCaretHex(ReadingTheme? theme) => theme?.InkColor ?? "#1A1A1A";

        /// <summary>Полоса линейки, когда своего цвета нет: бумага.</summary>
        private static string AutoRulerSheetHex(ReadingTheme? theme) => theme?.SheetColor ?? "#FFFFFF";

        /// <summary>Деления и цифры, когда своего цвета нет: чернила.</summary>
        private static string AutoRulerInkHex(ReadingTheme? theme) => theme?.InkColor ?? "#1A1A1A";

        /// <summary>Зона за краем листа, когда своего цвета нет: поле вокруг книги.</summary>
        private static string AutoRulerFieldHex(ReadingTheme? theme) => ReadingTheme.FieldColorHex(theme);

        /// <summary>
        /// Цвет из раздела правки выбрали руками — значит он свой, а не выведенный.
        /// Программная подстановка сюда не доходит: она идёт под флагом загрузки.
        /// </summary>
        private void OnEditorColorChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property != ColorPickerButton.HexColorProperty) return;
            if (_loading) return;

            if (ReferenceEquals(sender, _caretColorBtn)) _caretAuto = false;
            else if (ReferenceEquals(sender, _rulerSheetColorBtn)) _rulerSheetAuto = false;
            else if (ReferenceEquals(sender, _rulerInkColorBtn)) _rulerInkAuto = false;
            else if (ReferenceEquals(sender, _rulerFieldColorBtn)) _rulerFieldAuto = false;
            else if (ReferenceEquals(sender, _tabMarkColorBtn)) _tabMarkAuto = false;

            OnFieldsChanged();
        }

        /// <summary>Возвращает полю вывод цвета из вида.</summary>
        private void OnResetEditorColor(object? sender, RoutedEventArgs e)
        {
            var theme = _current?.Theme;
            if (theme is null || theme.IsBuiltIn) return;

            if (ReferenceEquals(sender, _caretResetBtn)) _caretAuto = true;
            else if (ReferenceEquals(sender, _rulerSheetResetBtn)) _rulerSheetAuto = true;
            else if (ReferenceEquals(sender, _rulerInkResetBtn)) _rulerInkAuto = true;
            else if (ReferenceEquals(sender, _rulerFieldResetBtn)) _rulerFieldAuto = true;
            else if (ReferenceEquals(sender, _tabMarkResetBtn)) _tabMarkAuto = true;
            else return;

            SyncAutoEditorSwatches(theme);
            OnFieldsChanged();
        }

        /// <summary>
        /// Подтягивает образцы тех полей, что выводятся сами: сменилась бумага или
        /// чернила — вместе с ними обязан смениться и показанный цвет, иначе образец
        /// обещает одно, а линейка рисует другое.
        /// </summary>
        private void SyncAutoEditorSwatches(ReadingTheme? theme)
        {
            bool saved = _loading;
            _loading = true;
            try
            {
                if (_caretAuto) _caretColorBtn.HexColor = AutoCaretHex(theme);
                if (_rulerSheetAuto) _rulerSheetColorBtn.HexColor = AutoRulerSheetHex(theme);
                if (_rulerInkAuto) _rulerInkColorBtn.HexColor = AutoRulerInkHex(theme);
                if (_rulerFieldAuto) _rulerFieldColorBtn.HexColor = AutoRulerFieldHex(theme);
                if (_tabMarkAuto) _tabMarkColorBtn.HexColor = DefaultTabMarkHex;
            }
            finally
            {
                _loading = saved;
            }
        }

        /// <summary>Возвращает поле к правилу «выводить из бумаги».</summary>
        private void OnResetBackdrop(object? sender, RoutedEventArgs e)
        {
            var theme = _current?.Theme;
            if (theme is null || theme.IsBuiltIn) return;

            _backColorBtn.HexColor = DerivedBackdropHex(theme.SheetColor);
            _backImages.Clear();
            OnGalleryChanged();
        }

        /// <summary>
        /// Гасит то, что без картинки ничего не значит: как ей лечь и насколько она
        /// плотная.
        /// </summary>
        private void ApplyBackdropEnablement(bool editable, bool hasImage)
        {
            _backColorBtn.IsEnabled = editable;
            _backFitCombo.IsEnabled = editable && hasImage;
            _backOpacitySlider.IsEnabled = editable && hasImage;
        }

        // ── Применение ────────────────────────────────────────────────────
        //
        // Окно ничего не копит и ничего не откатывает: заведённый вид, перестановка,
        // спрятанный глазком, любая правка полей уходят наружу сразу же. Кнопка
        // внизу и крестик только закрывают окно — отменять им нечего, и это честнее
        // прежнего порядка, при котором закрытие крестиком стирало всю работу.

        /// <summary>Просьба применить и сохранить то, что сейчас в окне.</summary>
        public Action<ReadingThemeResult>? ApplyRequested { get; set; }

        private DispatcherTimer? _applyTimer;

        /// <summary>
        /// Задержка перед применением правки полей. Ползунок света шлёт значения
        /// десятками в секунду, и писать настройки на каждое из них незачем:
        /// сохраняется то, на чём человек остановился.
        /// </summary>
        private static readonly TimeSpan ApplyDelay = TimeSpan.FromMilliseconds(320);

        /// <summary>Применить не сейчас, а когда правки утихнут.</summary>
        private void ScheduleApply()
        {
            if (_loading) return;

            if (_applyTimer is null)
            {
                _applyTimer = new DispatcherTimer { Interval = ApplyDelay };
                _applyTimer.Tick += (_, _) => ApplyNow();
            }

            _applyTimer.Stop();
            _applyTimer.Start();
        }

        /// <summary>Применить немедленно: список изменился, ждать нечего.</summary>
        private void ApplyNow()
        {
            _applyTimer?.Stop();

            if (ApplyRequested is not { } apply) return;

            var themes = _rows.Select(r => r.Theme).ToList();
            var selected = _current?.Theme ?? ReadingTheme.FindBuiltIn(ReadingTheme.WhiteId);

            apply(new ReadingThemeResult(themes, selected));
        }

        // ── Завершение ────────────────────────────────────────────────────

        private void OnOk(object? sender, RoutedEventArgs e)
        {
            OnFieldsChanged();
            CloseWindow();
        }

        private void OnCancel(object? sender, RoutedEventArgs e) => CloseWindow();

        private void OnScrimPressed(object? sender, PointerPressedEventArgs e) => CloseWindow();

        private void CompleteCancel() => CloseWindow();

        /// <summary>
        /// Закрывает окно, доводя до конца отложенную правку. Ничего не откатывает:
        /// всё, что человек сделал, уже применено и сохранено.
        /// </summary>
        private void CloseWindow()
        {
            ApplyNow();

            var themes = _rows.Select(r => r.Theme).ToList();
            var selected = _current?.Theme ?? ReadingTheme.FindBuiltIn(ReadingTheme.WhiteId);

            Complete(new ReadingThemeResult(themes, selected));
        }

        /// <summary>
        /// Цвет, который поле вокруг книги получит само, если своего ему не задали:
        /// под светлой бумагой темнее её, под почти чёрной чуть светлее. Правило то
        /// же, что и в самом канвасе, — иначе образец в окне обещал бы одно, а книга
        /// показывала другое.
        /// </summary>
        private static string DerivedBackdropHex(string? sheetHex)
        {
            if (string.IsNullOrWhiteSpace(sheetHex) || !SKColor.TryParse(sheetHex, out var c))
                return "#E8E8E8";

            double luma = (0.2126 * c.Red + 0.7152 * c.Green + 0.0722 * c.Blue) / 255.0;
            bool lighten = luma < 0.14;
            double target = lighten ? 255.0 : 0.0;
            double amount = lighten ? 0.10 : 0.16;

            byte Shift(byte v) => (byte)Math.Clamp(v + (target - v) * amount, 0.0, 255.0);

            return $"#{Shift(c.Red):X2}{Shift(c.Green):X2}{Shift(c.Blue):X2}";
        }

        private void Complete(ReadingThemeResult? result)
        {
            IsVisible = false;
            var tcs = _tcs;
            _tcs = null;
            tcs?.TrySetResult(result);
        }
    }
}
