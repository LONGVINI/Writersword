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
        private ToggleButton _scopeDocBtn = null!;
        private ToggleButton _scopeGlobalBtn = null!;
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
            _scopeDocBtn = this.FindControl<ToggleButton>("ScopeDocBtn")!;
            _scopeGlobalBtn = this.FindControl<ToggleButton>("ScopeGlobalBtn")!;
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
            _scopeDocBtn.IsCheckedChanged += (_, _) => OnFieldsChanged();
            _scopeGlobalBtn.IsCheckedChanged += (_, _) => OnFieldsChanged();
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

        private static IReadOnlyList<string> LoadFontList()
        {
            var list = new List<string> { FontAsInDocument };
            try
            {
                list.AddRange(SKFontManager.Default.FontFamilies
                    .Where(f => !string.IsNullOrWhiteSpace(f))
                    .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase));
            }
            catch
            {
                list.AddRange(new[] { "Arial", "Times New Roman", "Calibri", "Georgia", "Verdana" });
            }
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
            return _tcs.Task;
        }

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

                _scopeDocBtn.IsChecked = theme?.InDocument == true;
                _scopeGlobalBtn.IsChecked = theme?.IsGlobal == true;
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
            theme.InDocument = _scopeDocBtn.IsChecked == true;
            theme.IsGlobal = _scopeGlobalBtn.IsChecked == true;
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

            string? font = _fontCombo.SelectedItem as string;
            theme.FontFamily = string.IsNullOrWhiteSpace(font) || font == FontAsInDocument ? null : font;

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

            _scopeHint.Text = (theme.InDocument, theme.IsGlobal) switch
            {
                (true, true) => "Уедет с рукописью и останется под рукой в других проектах.",
                (true, false) => "Уедет вместе с рукописью. В других проектах его не будет.",
                (false, true) => "Доступен во всех проектах, но с этой рукописью не уедет.",
                _ => "Вид нигде не сохранён — включите хотя бы одну область, иначе он пропадёт."
            };
        }

        /// <summary>
        /// Показывает размеры выбранной картинки: человеку, кладущему свою бумагу,
        /// это первое, что нужно знать.
        /// </summary>
        private void UpdateImageInfo()
        {
            string? path = _current?.Theme.ImagePath;
            if (string.IsNullOrWhiteSpace(path))
            {
                _imageInfoText.Text = "Размеры картинки появятся здесь после выбора файла.";
                return;
            }

            if (!File.Exists(path))
            {
                _imageInfoText.Text = "Файл не найден по этому пути.";
                return;
            }

            try
            {
                using var stream = File.OpenRead(path);
                var bitmap = new Bitmap(stream);
                var size = bitmap.PixelSize;
                long bytes = new FileInfo(path).Length;

                string hint = size.Width < 600 || size.Height < 600
                    ? " Для растягивания на весь лист этого мало — лучше замостить."
                    : string.Empty;

                _imageInfoText.Text = $"{size.Width} × {size.Height} точек, {bytes / 1024} КБ.{hint}";
            }
            catch (Exception ex)
            {
                _imageInfoText.Text = "Картинку прочитать не удалось: " + ex.Message;
            }
        }

        // ── Список ────────────────────────────────────────────────────────

        private void OnThemeRowPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Border { Tag: ReadingThemeRow row }) return;
            Select(row);
            e.Handled = true;
        }

        private void OnAdd(object? sender, RoutedEventArgs e) => AddCopy("Новый вид");

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

            // Новый вид по умолчанию едет с документом: чаще всего оформление заводят
            // под конкретную рукопись, а не под всю программу.
            copy.InDocument = true;
            copy.IsGlobal = false;

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

            _imagePathBox.Text = path;
            OnFieldsChanged();
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

            _backImagePathBox.Text = path;

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
