using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
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

        /// <summary>Подпись области: где вид сохранён.</summary>
        public string ScopeText
        {
            get
            {
                if (Theme.IsBuiltIn) return "есть всегда";
                if (Theme.InDocument && Theme.IsGlobal) return "в документе и везде";
                if (Theme.InDocument) return "в документе";
                if (Theme.IsGlobal) return "везде";
                return "нигде не сохранён";
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
            Raise(nameof(ScopeText));
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
        private const string FontAsInDocument = "Как в документе";

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
        private ColorPickerButton _backColorBtn = null!;
        private CheckBox _backUseImageCheck = null!;
        private ComboBox _backFitCombo = null!;
        private TextBox _backImagePathBox = null!;
        private Slider _backOpacitySlider = null!;
        private TextBlock _backOpacityValue = null!;

        /// <summary>Подписи того, как ложится картинка поля.</summary>
        private static readonly string[] BackdropFitLabels =
            { "Закрыть целиком", "Уместить целиком", "Растянуть", "Замостить" };
        private ComboBox _fontCombo = null!;
        private Slider _brightnessSlider = null!;
        private Slider _contrastSlider = null!;
        private Slider _warmthSlider = null!;
        private TextBlock _brightnessValue = null!;
        private TextBlock _contrastValue = null!;
        private TextBlock _warmthValue = null!;
        private TextBox _imagePathBox = null!;
        private TextBlock _imageInfoText = null!;
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
            _backColorBtn = this.FindControl<ColorPickerButton>("BackColorBtn")!;
            _backUseImageCheck = this.FindControl<CheckBox>("BackUseImageCheck")!;
            _backFitCombo = this.FindControl<ComboBox>("BackFitCombo")!;
            _backImagePathBox = this.FindControl<TextBox>("BackImagePathBox")!;
            _backOpacitySlider = this.FindControl<Slider>("BackOpacitySlider")!;
            _backOpacityValue = this.FindControl<TextBlock>("BackOpacityValue")!;

            _backFitCombo.ItemsSource = BackdropFitLabels;
            _fontCombo = this.FindControl<ComboBox>("FontCombo")!;
            _brightnessSlider = this.FindControl<Slider>("BrightnessSlider")!;
            _contrastSlider = this.FindControl<Slider>("ContrastSlider")!;
            _warmthSlider = this.FindControl<Slider>("WarmthSlider")!;
            _brightnessValue = this.FindControl<TextBlock>("BrightnessValue")!;
            _contrastValue = this.FindControl<TextBlock>("ContrastValue")!;
            _warmthValue = this.FindControl<TextBlock>("WarmthValue")!;
            _imagePathBox = this.FindControl<TextBox>("ImagePathBox")!;
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
            _fontCombo.ItemsSource = LoadFontList();

            this.FindControl<Button>("OkBtn")!.Click += OnOk;
            this.FindControl<Button>("CancelBtn")!.Click += OnCancel;
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
            _backColorBtn.PropertyChanged += OnColorButtonChanged;
            _backUseImageCheck.IsCheckedChanged += (_, _) => OnFieldsChanged();
            _backFitCombo.SelectionChanged += (_, _) => OnFieldsChanged();
            _backOpacitySlider.PropertyChanged += OnSliderChanged;
            _backImagePathBox.TextChanged += (_, _) => OnFieldsChanged();
            this.FindControl<Button>("BackBrowseBtn")!.Click += OnBrowseBackdrop;
            this.FindControl<Button>("BackClearImageBtn")!.Click += OnClearBackdropImage;
            this.FindControl<Button>("BackResetBtn")!.Click += OnResetBackdrop;
            _fontCombo.SelectionChanged += (_, _) => OnFieldsChanged();
            _brightnessSlider.PropertyChanged += OnSliderChanged;
            _contrastSlider.PropertyChanged += OnSliderChanged;
            _warmthSlider.PropertyChanged += OnSliderChanged;
            _opacitySlider.PropertyChanged += OnSliderChanged;
            _tileCheck.IsCheckedChanged += (_, _) => OnFieldsChanged();
            _imagePathBox.TextChanged += (_, _) => OnFieldsChanged();
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        /// <summary>
        /// Список гарнитур для выпадающего списка вида.
        ///
        /// Гарнитура вида передаётся отдельным доводом и попадает в список даже
        /// тогда, когда её нет ни в проекте, ни в системе. Иначе ComboBox не
        /// находил своего SelectedItem и схлопывал выбор в null, а следующая же
        /// правка любого другого поля вида — имени, цвета, ползунка — считывала
        /// этот null и стирала шрифт из проекта.
        /// </summary>
        private static IReadOnlyList<string> LoadFontList(string? keep = null)
        {
            var list = new List<string> { FontAsInDocument };
            list.AddRange(Modules.TextEditor.Services.ProjectFonts.PickerFamilies(keep));
            return list;
        }

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

                // Фон, выводимый из бумаги, показывается тем цветом, который он и
                // получит: пустой образец не объясняет, что там сейчас.
                _backFitCombo.SelectedIndex = (int)(theme?.BackdropImageFit ?? ReadingBackdropFit.Cover);

                _backColorBtn.HexColor = string.IsNullOrWhiteSpace(theme?.BackdropColor)
                    ? DerivedBackdropHex(theme?.SheetColor)
                    : theme!.BackdropColor!;

                _backUseImageCheck.IsChecked = theme?.UseBackdropImage == true;
                _backOpacitySlider.Value = Math.Clamp(theme?.BackdropImageOpacity ?? 1.0, 0.0, 1.0) * 100.0;
                _backImagePathBox.Text = theme?.BackdropImagePath ?? string.Empty;

                ApplyBackdropEnablement(editable, theme?.UseBackdropImage == true);

                // Список пересобирается под гарнитуру этого вида: если её нет на
                // машине, она всё равно должна быть в Items, иначе выбор схлопнется.
                _fontCombo.ItemsSource = LoadFontList(theme?.FontFamily);
                _fontCombo.SelectedItem = string.IsNullOrWhiteSpace(theme?.FontFamily)
                    ? FontAsInDocument
                    : theme!.FontFamily;
                _fontCombo.IsEnabled = editable;

                _brightnessSlider.Value = Math.Clamp(theme?.Brightness ?? 1.0, 0.35, 1.0) * 100.0;
                _contrastSlider.Value = Math.Clamp(theme?.Contrast ?? 1.0, 0.6, 1.6) * 100.0;
                _warmthSlider.Value = Math.Clamp(theme?.Warmth ?? 0.0, 0.0, 1.0) * 100.0;
                _brightnessSlider.IsEnabled = editable;
                _contrastSlider.IsEnabled = editable;
                _warmthSlider.IsEnabled = editable;

                _imagePathBox.Text = theme?.ImagePath ?? string.Empty;
                _opacitySlider.Value = Math.Clamp(theme?.ImageOpacity ?? 1.0, 0.0, 1.0) * 100.0;
                _tileCheck.IsChecked = theme?.ImageTile == true;
                _imagePathBox.IsEnabled = editable;
                _opacitySlider.IsEnabled = editable;
                _tileCheck.IsEnabled = editable;

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

            // Заливка, совпадающая с выводимой из бумаги, своей не считается: иначе
            // «От бумаги» выключалось бы само собой, стоило образцу показать этот цвет.
            string backHex = _backColorBtn.HexColor;
            theme.BackdropColor =
                string.Equals(backHex, DerivedBackdropHex(theme.SheetColor), StringComparison.OrdinalIgnoreCase)
                    ? null
                    : backHex;

            theme.UseBackdropImage = _backUseImageCheck.IsChecked == true;
            theme.BackdropImageFit = (ReadingBackdropFit)Math.Clamp(_backFitCombo.SelectedIndex, 0, 3);
            theme.BackdropImageOpacity = Math.Clamp(_backOpacitySlider.Value / 100.0, 0.0, 1.0);
            theme.BackdropImagePath = string.IsNullOrWhiteSpace(_backImagePathBox.Text)
                ? null
                : _backImagePathBox.Text;

            ApplyBackdropEnablement(!theme.IsBuiltIn, theme.UseBackdropImage);

            // Пустой SelectedItem означает, что комбобокс не нашёл своего значения,
            // а не что человек выбрал «как в документе». Сбрасывать шрифт вида по
            // такому признаку нельзя — это потеря данных проекта.
            string? font = _fontCombo.SelectedItem as string;
            if (!string.IsNullOrWhiteSpace(font))
                theme.FontFamily = font == FontAsInDocument ? null : font;

            theme.Brightness = Math.Clamp(_brightnessSlider.Value / 100.0, 0.35, 1.0);
            theme.Contrast = Math.Clamp(_contrastSlider.Value / 100.0, 0.6, 1.6);
            theme.Warmth = Math.Clamp(_warmthSlider.Value / 100.0, 0.0, 1.0);

            theme.ImagePath = string.IsNullOrWhiteSpace(_imagePathBox.Text) ? null : _imagePathBox.Text;
            theme.ImageOpacity = Math.Clamp(_opacitySlider.Value / 100.0, 0.0, 1.0);
            theme.ImageTile = _tileCheck.IsChecked == true;

            _current.RefreshAll();
            UpdateValueLabels();
            UpdateScopeHint();
            UpdateImageInfo();
            UpdatePreview();
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

            var family = string.IsNullOrWhiteSpace(theme?.FontFamily)
                ? FontFamily.Default
                : new FontFamily(theme!.FontFamily!);
            _previewHead.FontFamily = family;
            _previewBody.FontFamily = family;

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

            var backdropBrush = theme?.UseBackdropImage == true
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
            string? reference = theme?.ImagePath;
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
            string? reference = theme?.BackdropImagePath;
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
            _imageInfoText.Text = DescribeImage(_current?.Theme.ImagePath);
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
            e.Handled = true;
        }

        private void OnAdd(object? sender, RoutedEventArgs e) => AddCopy(NewThemeName);

        private void OnDuplicate(object? sender, RoutedEventArgs e)
        {
            string baseName = _current?.Theme.Name ?? "Вид";
            AddCopy(baseName + " (копия)");
        }

        /// <summary>
        /// Заводит новый вид копией выбранного. От знакомой точки настраивать проще,
        /// чем от белого листа, а встроенный вид иначе и не изменить.
        /// </summary>
        private void AddCopy(string name)
        {
            var source = _current?.Theme ?? ReadingTheme.FindBuiltIn(ReadingTheme.CreamId);

            var copy = source.CopyAs(UniqueName(name));

            // Новый вид по умолчанию лежит в обеих областях. Область «в документе»
            // одна не спасает: вид попадает на диск только вместе с сохранением
            // рукописи, и до него настроенный вид живёт лишь в памяти — закрыл
            // программу, не сохранив, и его нет. Область «везде» пишется в настройки
            // сразу по «Готово», поэтому вид остаётся под рукой в любом случае, а
            // вместе с рукописью уезжает по-прежнему.
            copy.InDocument = true;
            copy.IsGlobal = true;

            var row = new ReadingThemeRow(copy);
            _rows.Add(row);
            Select(row);
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
        }

        // ── Картинка ──────────────────────────────────────────────────────

        private async void OnBrowse(object? sender, RoutedEventArgs e)
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.StorageProvider is not { } storage) return;

            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Картинка бумаги",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Изображения")
                    {
                        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp" }
                    }
                }
            });

            if (files.Count == 0) return;

            string? path = files[0].TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path)) return;

            _imagePathBox.Text = StoreImage(path!);
            OnFieldsChanged();
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

        private void OnClearImage(object? sender, RoutedEventArgs e)
        {
            _imagePathBox.Text = string.Empty;
            OnFieldsChanged();
        }

        private async void OnBrowseBackdrop(object? sender, RoutedEventArgs e)
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.StorageProvider is not { } storage) return;

            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Картинка фона",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Изображения")
                    {
                        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp" }
                    }
                }
            });

            if (files.Count == 0) return;

            string? path = files[0].TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path)) return;

            _backImagePathBox.Text = StoreImage(path!);

            // Выбранный файл сам по себе ничего не покажет, пока картинка не включена:
            // человек выбрал её именно затем, чтобы увидеть.
            _backUseImageCheck.IsChecked = true;

            OnFieldsChanged();
        }

        private void OnClearBackdropImage(object? sender, RoutedEventArgs e)
        {
            _backImagePathBox.Text = string.Empty;
            OnFieldsChanged();
        }

        /// <summary>Возвращает поле к правилу «выводить из бумаги».</summary>
        private void OnResetBackdrop(object? sender, RoutedEventArgs e)
        {
            var theme = _current?.Theme;
            if (theme is null || theme.IsBuiltIn) return;

            _backColorBtn.HexColor = DerivedBackdropHex(theme.SheetColor);
            _backUseImageCheck.IsChecked = false;
            OnFieldsChanged();
        }

        /// <summary>
        /// Гасит то, что без картинки ничего не значит: как ей лечь и насколько она
        /// плотная.
        /// </summary>
        private void ApplyBackdropEnablement(bool editable, bool useImage)
        {
            _backColorBtn.IsEnabled = editable;
            _backUseImageCheck.IsEnabled = editable;
            _backImagePathBox.IsEnabled = editable;
            _backFitCombo.IsEnabled = editable && useImage;
            _backOpacitySlider.IsEnabled = editable && useImage;
        }

        // ── Завершение ────────────────────────────────────────────────────

        private void OnOk(object? sender, RoutedEventArgs e)
        {
            OnFieldsChanged();

            var themes = _rows.Select(r => r.Theme).ToList();
            var selected = _current?.Theme ?? ReadingTheme.FindBuiltIn(ReadingTheme.CreamId);
            Complete(new ReadingThemeResult(themes, selected));
        }

        private void OnCancel(object? sender, RoutedEventArgs e) => CompleteCancel();

        private void OnScrimPressed(object? sender, PointerPressedEventArgs e) => CompleteCancel();

        private void CompleteCancel() => Complete(null);

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
