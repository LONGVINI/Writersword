using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using ReactiveUI;
using System;
using System.Windows.Input;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Writersword.Core.Interfaces.Services.Storage;
using Writersword.Core.Interfaces.WorkFlows;
using Writersword.Core.Models.Project;
using Writersword.Core.Services;

namespace Writersword.Styles.UserControls
{
    public partial class ColorPickerButton : UserControl
    {
        public static readonly StyledProperty<string> HexColorProperty =
            AvaloniaProperty.Register<ColorPickerButton, string>(
                nameof(HexColor),
                defaultValue: "#607D8B",
                defaultBindingMode: BindingMode.TwoWay);

        public string HexColor
        {
            get => GetValue(HexColorProperty);
            set => SetValue(HexColorProperty, value);
        }

        /// <summary>
        /// Открыт ли список цветов. Нужно снаружи: когда пикер стоит внутри общей
        /// рамки рядом с кнопкой-действием, подсветку держит эта рамка, а она про
        /// состояние попапа сама не знает. Псевдокласс :flyout-open живёт на
        /// внутренней кнопке и наружу не виден.
        /// </summary>
        public static readonly StyledProperty<bool> IsMenuOpenProperty =
            AvaloniaProperty.Register<ColorPickerButton, bool>(nameof(IsMenuOpen));

        public bool IsMenuOpen
        {
            get => GetValue(IsMenuOpenProperty);
            set => SetValue(IsMenuOpenProperty, value);
        }


        public static readonly StyledProperty<bool> ShowCardPreviewProperty =
            AvaloniaProperty.Register<ColorPickerButton, bool>(nameof(ShowCardPreview), false);

        // true для пикеров в карточках персонажей — в модале показывается превью карточки.
        public bool ShowCardPreview
        {
            get => GetValue(ShowCardPreviewProperty);
            set => SetValue(ShowCardPreviewProperty, value);
        }

        // Данные для превью реальной карточки в редакторе: картинка, имя, запасной значок.
        public static readonly StyledProperty<Bitmap?> PreviewImageProperty =
            AvaloniaProperty.Register<ColorPickerButton, Bitmap?>(nameof(PreviewImage));
        public Bitmap? PreviewImage
        {
            get => GetValue(PreviewImageProperty);
            set => SetValue(PreviewImageProperty, value);
        }

        public static readonly StyledProperty<string?> PreviewNameProperty =
            AvaloniaProperty.Register<ColorPickerButton, string?>(nameof(PreviewName));
        public string? PreviewName
        {
            get => GetValue(PreviewNameProperty);
            set => SetValue(PreviewNameProperty, value);
        }

        public static readonly StyledProperty<string?> PreviewFallbackProperty =
            AvaloniaProperty.Register<ColorPickerButton, string?>(nameof(PreviewFallback));
        public string? PreviewFallback
        {
            get => GetValue(PreviewFallbackProperty);
            set => SetValue(PreviewFallbackProperty, value);
        }

        // Доп. функция кольца вокруг аватара (двусторонняя связь с моделью карточки).
        public static readonly StyledProperty<bool> RingEnabledProperty =
            AvaloniaProperty.Register<ColorPickerButton, bool>(
                nameof(RingEnabled), defaultBindingMode: BindingMode.TwoWay);
        public bool RingEnabled
        {
            get => GetValue(RingEnabledProperty);
            set => SetValue(RingEnabledProperty, value);
        }

        // true для пикеров на карточках групп — в редакторе цвета появляется
        // настройка закладки-ленточки, а превью рисует её.
        public static readonly StyledProperty<bool> PreviewIsGroupProperty =
            AvaloniaProperty.Register<ColorPickerButton, bool>(nameof(PreviewIsGroup));
        public bool PreviewIsGroup
        {
            get => GetValue(PreviewIsGroupProperty);
            set => SetValue(PreviewIsGroupProperty, value);
        }

        // Закладка-ленточка карточки группы (двусторонняя связь с моделью карточки).
        public static readonly StyledProperty<bool> BookmarkEnabledProperty =
            AvaloniaProperty.Register<ColorPickerButton, bool>(
                nameof(BookmarkEnabled), defaultValue: true,
                defaultBindingMode: BindingMode.TwoWay);
        public bool BookmarkEnabled
        {
            get => GetValue(BookmarkEnabledProperty);
            set => SetValue(BookmarkEnabledProperty, value);
        }

        public static readonly StyledProperty<ICommand?> ApplyRingToAllCommandProperty =
            AvaloniaProperty.Register<ColorPickerButton, ICommand?>(nameof(ApplyRingToAllCommand));
        public ICommand? ApplyRingToAllCommand
        {
            get => GetValue(ApplyRingToAllCommandProperty);
            set => SetValue(ApplyRingToAllCommandProperty, value);
        }

        public IReadOnlyList<string> PresetColors { get; } = new[]
        {
            // «Без цвета» — полностью прозрачное значение (перечёркнутый образец).
            "#00000000",
            "#F44336", "#E91E63", "#9C27B0", "#673AB7", "#3F51B5", "#2196F3",
            "#03A9F4", "#00BCD4", "#009688", "#4CAF50", "#8BC34A", "#FFEB3B",
            "#FFC107", "#FF9800", "#FF5722", "#795548", "#607D8B", "#9E9E9E",
            "#455A64", "#E07B39", "#37474F", "#212121", "#FFFFFF", "#BDBDBD"
        };

        // Палитра проекта: закреплённые («+») и недавние. Источник истины — ProjectFile,
        // эти коллекции — лишь представление для биндинга образцов в флауте.
        private const int MaxRecentColors = 12;
        public ObservableCollection<string> PinnedColors { get; } = new();
        public ObservableCollection<string> RecentColors { get; } = new();

        public static readonly DirectProperty<ColorPickerButton, bool> HasPinnedColorsProperty =
            AvaloniaProperty.RegisterDirect<ColorPickerButton, bool>(nameof(HasPinnedColors), o => o.HasPinnedColors);
        private bool _hasPinnedColors;
        public bool HasPinnedColors
        {
            get => _hasPinnedColors;
            private set => SetAndRaise(HasPinnedColorsProperty, ref _hasPinnedColors, value);
        }

        public static readonly DirectProperty<ColorPickerButton, bool> HasRecentColorsProperty =
            AvaloniaProperty.RegisterDirect<ColorPickerButton, bool>(nameof(HasRecentColors), o => o.HasRecentColors);
        private bool _hasRecentColors;
        public bool HasRecentColors
        {
            get => _hasRecentColors;
            private set => SetAndRaise(HasRecentColorsProperty, ref _hasRecentColors, value);
        }

        // Видимые палитры (проектные + глобальные) для секции «Палитры» в попапе.
        public ObservableCollection<PaletteListItem> PopupPalettes { get; } = new();

        public static readonly DirectProperty<ColorPickerButton, bool> HasPalettesProperty =
            AvaloniaProperty.RegisterDirect<ColorPickerButton, bool>(nameof(HasPalettes), o => o.HasPalettes);
        private bool _hasPalettes;
        public bool HasPalettes
        {
            get => _hasPalettes;
            private set => SetAndRaise(HasPalettesProperty, ref _hasPalettes, value);
        }

        // Раскрытость секций попапа (двусторонняя — для биндинга тел и шевронов).
        public static readonly DirectProperty<ColorPickerButton, bool> BasicExpandedProperty =
            AvaloniaProperty.RegisterDirect<ColorPickerButton, bool>(
                nameof(BasicExpanded), o => o.BasicExpanded, (o, v) => o.BasicExpanded = v);
        private bool _basicExpanded = true;
        public bool BasicExpanded
        {
            get => _basicExpanded;
            set => SetAndRaise(BasicExpandedProperty, ref _basicExpanded, value);
        }

        public static readonly DirectProperty<ColorPickerButton, bool> MineExpandedProperty =
            AvaloniaProperty.RegisterDirect<ColorPickerButton, bool>(
                nameof(MineExpanded), o => o.MineExpanded, (o, v) => o.MineExpanded = v);
        private bool _mineExpanded = true;
        public bool MineExpanded
        {
            get => _mineExpanded;
            set => SetAndRaise(MineExpandedProperty, ref _mineExpanded, value);
        }

        public static readonly DirectProperty<ColorPickerButton, bool> RecentExpandedProperty =
            AvaloniaProperty.RegisterDirect<ColorPickerButton, bool>(
                nameof(RecentExpanded), o => o.RecentExpanded, (o, v) => o.RecentExpanded = v);
        private bool _recentExpanded = true;
        public bool RecentExpanded
        {
            get => _recentExpanded;
            set => SetAndRaise(RecentExpandedProperty, ref _recentExpanded, value);
        }

        public static readonly DirectProperty<ColorPickerButton, bool> PalettesExpandedProperty =
            AvaloniaProperty.RegisterDirect<ColorPickerButton, bool>(
                nameof(PalettesExpanded), o => o.PalettesExpanded, (o, v) => o.PalettesExpanded = v);
        private bool _palettesExpanded = true;
        public bool PalettesExpanded
        {
            get => _palettesExpanded;
            set => SetAndRaise(PalettesExpandedProperty, ref _palettesExpanded, value);
        }

        private GlobalPaletteData _global = new();

        public ReactiveCommand<string, Unit> SelectPresetCommand { get; }
        public ReactiveCommand<string, Unit> RemovePinnedCommand { get; }
        public ReactiveCommand<string, Unit> RemoveRecentCommand { get; }
        public ReactiveCommand<Unit, Unit> PinCurrentCommand { get; }
        public ReactiveCommand<Unit, Unit> OpenAdvancedCommand { get; }

        private ColorSpectrum? _spectrum;
        private Button? _triggerButton;
        private FlyoutBase? _flyout;
        private bool _syncingColorView;

        public ColorPickerButton()
        {
            // DataContext НЕ трогаем — иначе внешние binding типа
            // HexColor="{Binding Color}" будут искать Color на самом ColorPickerButton,
            // а не на CharacterFolderViewModel из DataTemplate.
            InitializeComponent();

            PinnedColors.CollectionChanged += (_, __) => HasPinnedColors = PinnedColors.Count > 0;
            RecentColors.CollectionChanged += (_, __) => HasRecentColors = RecentColors.Count > 0;

            SelectPresetCommand = ReactiveCommand.Create<string>(hex =>
            {
                if (string.IsNullOrWhiteSpace(hex)) return;
                HexColor = hex;
                _flyout?.Hide();
            });

            RemovePinnedCommand = ReactiveCommand.Create<string>(hex =>
            {
                CurrentProject?.ProjectPinnedColors.RemoveAll(c => HexEquals(c, hex));
                for (int i = PinnedColors.Count - 1; i >= 0; i--)
                    if (HexEquals(PinnedColors[i], hex)) PinnedColors.RemoveAt(i);
            });

            RemoveRecentCommand = ReactiveCommand.Create<string>(hex =>
            {
                CurrentProject?.ProjectRecentColors.RemoveAll(c => HexEquals(c, hex));
                for (int i = RecentColors.Count - 1; i >= 0; i--)
                    if (HexEquals(RecentColors[i], hex)) RecentColors.RemoveAt(i);
            });

            PinCurrentCommand = ReactiveCommand.Create(() => AddPinned(HexColor));

            OpenAdvancedCommand = ReactiveCommand.CreateFromTask(OpenAdvancedAsync);

            // Правая кнопка по образцу цвета выбирает подборщик. Меню строится
            // на каждый вызов: режим общий на всё приложение и мог смениться с
            // другой кнопки, а отметка обязана стоять напротив нынешнего.
            AddHandler(ContextRequestedEvent, OnColorContextRequested, RoutingStrategies.Tunnel);
        }

        /// <summary>
        /// Правая кнопка по образцу цвета показывает подборщик — квадрат,
        /// колесо или значения — прямо во всплывашке, с живым цветом. Левая
        /// при этом остаётся заготовками: главный способ брать цвет никуда не
        /// девается.
        ///
        /// Какой именно подборщик, задаёт галочка в окне настройки цвета.
        /// </summary>
        private void OnColorContextRequested(object? sender, ContextRequestedEventArgs e)
        {
            e.Handled = true;

            _openAsMini = true;
            _flyout?.ShowAt(this);
        }

        // Всплывашку открыли правой кнопкой: показать подборщик, а не заготовки.
        // Признак снимается при закрытии — следующее открытие левой кнопкой
        // обязано снова показать заготовки.
        private bool _openAsMini;

        private async void OnConfigureClick(object? sender, RoutedEventArgs e)
        {
            await OpenAdvancedAsync();
        }

        private Control? FindModalHost()
        {
            Visual? v = this;
            while (v is not null)
            {
                if (v is Control c && ModalHost.GetIsHost(c)) return c;
                v = v.GetVisualParent();
            }
            return null;
        }

        private ColorEditorOverlay? FindEditorOverlay()
        {
            var host = FindModalHost();
            if (host is null) return null;
            return host.GetVisualDescendants().OfType<ColorEditorOverlay>().FirstOrDefault();
        }

        private async Task OpenAdvancedAsync()
        {
            try
            {
                _flyout?.Hide();
                var overlay = FindEditorOverlay();
                if (overlay is null) return;
                // Открываем сразу на вкладке выбранного подборщика. У режима
                // «Заготовки» своей вкладки нет — туда попадают кнопкой
                // «Настроить», и редактор встаёт на квадрат, как и раньше.
                var result = await overlay.ShowAsync(
                    HexColor, ShowCardPreview, PreviewImage, PreviewName, PreviewFallback,
                    RingEnabled, CurrentProject?.AvatarRingsAll ?? false,
                    PreviewIsGroup, BookmarkEnabled,
                    ColorPickerModeStore.TabOf(ColorPickerModeStore.Current));
                if (result is null) return;

                // Code несёт полный выбор: код градиента либо обычный hex одноцвета.
                var picked = !string.IsNullOrWhiteSpace(result.Code) ? result.Code! : result.Hex;
                if (!string.IsNullOrWhiteSpace(picked))
                    HexColor = picked;

                if (result.ApplyAll is bool applyVal)
                {
                    var cmd = ApplyRingToAllCommand;
                    if (cmd is not null && cmd.CanExecute(applyVal)) cmd.Execute(applyVal);
                    RingEnabled = applyVal;

                    var proj = CurrentProject;
                    if (proj is not null) proj.AvatarRingsAll = applyVal;
                    SaveActiveDocument();
                }
                else
                {
                    RingEnabled = result.Ring;
                }

                // Настройка закладки возвращается только для карточек групп.
                if (PreviewIsGroup) BookmarkEnabled = result.Bookmark;
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "ColorPicker: OpenAdvanced failed");
            }
        }

        private static ProjectFile? CurrentProject =>
            CoreServices.GetService<ITabCollection>()?.ActiveTab?.Context?.Project;

        // Проектные данные (палитра, флаг колец) не помечают проект «грязным» —
        // сохраняем документ явно после массового переключения колец.
        private static void SaveActiveDocument()
        {
            try
            {
                var tab = CoreServices.GetService<ITabCollection>()?.ActiveTab;
                var workflow = CoreServices.GetService<IProjectWorkflow>();
                if (tab is not null && workflow is not null)
                    _ = workflow.SaveDocumentAsync(tab);
            }
            catch { }
        }

        private static string Normalize(string? hex) => (hex ?? string.Empty).Trim().ToUpperInvariant();

        private static bool HexEquals(string a, string b) =>
            string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _triggerButton = this.FindControl<Button>("TriggerButton");

            _flyout = _triggerButton?.Flyout;
            if (_flyout is not null)
            {
                // Событие Opening объявлено на PopupFlyoutBase (не на FlyoutBase);
                // стандартный Flyout из кнопки — его наследник.
                if (_flyout is PopupFlyoutBase pf)
                {
                    pf.Opening -= OnFlyoutOpening;
                    pf.Opening += OnFlyoutOpening;
                }
                _flyout.Opened -= OnFlyoutOpened;
                _flyout.Opened += OnFlyoutOpened;
                _flyout.Closed -= OnFlyoutClosed;
                _flyout.Closed += OnFlyoutClosed;
            }

        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            if (_flyout is not null)
            {
                if (_flyout is PopupFlyoutBase pf) pf.Opening -= OnFlyoutOpening;
                _flyout.Opened -= OnFlyoutOpened;
                _flyout.Closed -= OnFlyoutClosed;
            }
            _flyout = null;
            _spectrumSubscription?.Dispose();
            _spectrumSubscription = null;
            _spectrum = null;
            _triggerButton = null;
        }

        // Данные и состояние секций загружаются ДО показа попапа (Opening), чтобы
        // содержимое сразу строилось в правильном свёрнутом/раскрытом виде. При
        // загрузке в Opened попап успевал отрисоваться с раскрытыми по умолчанию
        // секциями и на глазах проигрывал анимацию их сворачивания.
        private void OnFlyoutOpening(object? sender, EventArgs e)
        {
            // Признак поднимается уже здесь, а не в Opened: между нажатием и
            // показом попапа проходит кадр, и внешняя подсветка успевала мигнуть.
            IsMenuOpen = true;
            // Переходы сворачивания снимаются на время показа: привязки ScaleY
            // применяют сохранённое состояние при построении/присоединении
            // содержимого, и объявленный заранее переход проигрывал это как
            // анимацию закрытия. Обратно они навешиваются в Opened после отрисовки.
            if (_flyout is Flyout f && f.Content is Visual root)
                SetCollapseTransitions(root, enable: false);
            ReloadFromProject();
            LoadPalettesAndCollapse();
            ConfigureMiniPicker();
            SyncColorViewFromHex(HexColor);
        }

        /// <summary>
        /// Настроить содержимое всплывашки под выбранный подборщик: заготовки
        /// или мини-версия квадрата, колеса, сот, шума и значений.
        ///
        /// Контролы ищутся здесь, а не при присоединении к дереву: содержимое
        /// всплывашки до её первого показа в дереве не существует.
        /// </summary>
        private void ConfigureMiniPicker()
        {
            if (_flyout is not Flyout flyout) return;
            if (flyout.Content is not Control root) return;
            _miniRoot = root;

            var host = FindInFlyout<StackPanel>(root, "MiniPickerHost");
            var presets = FindInFlyout<ScrollViewer>(root, "PresetsScroll");
            var spectrumHost = FindInFlyout<StackPanel>(root, "SpectrumHost");
            var spectrum = FindInFlyout<ColorSpectrum>(root, "MiniSpectrum");
            var honeyHost = FindInFlyout<Border>(root, "HoneycombHost");
            var honey = FindInFlyout<HoneycombPicker>(root, "MiniHoneycomb");
            var noiseHost = FindInFlyout<StackPanel>(root, "NoiseHost");
            var noise = FindInFlyout<NoisePicker>(root, "MiniNoise");
            var rgbRows = FindInFlyout<ScrollViewer>(root, "RgbRowsHost");
            var valuesHost = FindInFlyout<StackPanel>(root, "ValuesHost");

            var mini = _openAsMini;
            var mode = ColorPickerModeStore.Current;
            var isHoney = mode == ColorPickerMode.Honeycomb;
            var isNoise = mode == ColorPickerMode.Noise;
            var isValues = mode == ColorPickerMode.Values;
            var isWheel = mode == ColorPickerMode.Wheel;

            if (host is not null) host.IsVisible = mini;
            if (presets is not null) presets.IsVisible = !mini;
            if (honeyHost is not null) honeyHost.IsVisible = mini && isHoney;
            if (noiseHost is not null) noiseHost.IsVisible = mini && isNoise;
            var showSpectrum = mini && !isHoney && !isNoise && !isValues;
            if (spectrumHost is not null) spectrumHost.IsVisible = showSpectrum;
            if (rgbRows is not null) rgbRows.IsVisible = showSpectrum;

            // У колеса V стоит сбоку и во всю его высоту, у квадрата — строкой
            // под ним. Ползунка два, а не один переставляемый: шаблон трека у
            // Slider.grad задан поперёк и вдоль сразу, и одним контролом обе
            // раскладки не покрыть.
            SetMiniVisible("SpectrumVColumn", showSpectrum && isWheel);
            SetMiniVisible("SpectrumHColumn", showSpectrum && !isWheel);
            SetMiniVisible("SpectrumDimRing", showSpectrum && isWheel);

            EnableSliderJump(root);
            if (valuesHost is not null) valuesHost.IsVisible = mini && isValues;

            if (honey is not null && !ReferenceEquals(_honeycomb, honey))
            {
                _honeycomb = honey;
                honey.ColorPicked += OnMiniColorPicked;
            }

            if (noise is not null && !ReferenceEquals(_noise, noise))
            {
                _noise = noise;
                noise.ColorPicked += OnMiniColorPicked;
            }

            if (!mini) return;

            if (isHoney && _honeycomb is not null)
                _honeycomb.SelectedHex = NormalizeHex(HexColor);

            if (isValues)
                ApplyValMode(_valMode);

            // Спектр остаётся в дереве и при сотах, шуме и значениях: он —
            // единственная точка, через которую цвет уходит наружу, и строки
            // RGB/HSL/HSV пишут именно в него. Наружу его прячет SpectrumHost.
            if (spectrum is not null)
            {
                // Колесо крутит оттенок с насыщенностью, яркость уходит на
                // боковую полосу. Квадрат — насыщенность × яркость при
                // выбранном оттенке, оттенок уходит на радужную полосу: ровно
                // тот же разбор, что у вкладки «Квадрат» в окне «Настроить цвет».
                spectrum.Shape = isWheel ? ColorSpectrumShape.Ring : ColorSpectrumShape.Box;
                spectrum.Components = isWheel
                    ? ColorSpectrumComponents.HueSaturation
                    : ColorSpectrumComponents.SaturationValue;

                // Подписка одна на всё время жизни кнопки: всплывашка открывается
                // много раз, и каждый показ добавлял бы ещё одного слушателя.
                if (!ReferenceEquals(_spectrum, spectrum))
                {
                    _spectrumSubscription?.Dispose();
                    _spectrum = spectrum;

                    // GetObservable отдаёт текущее значение сразу при подписке, а
                    // у только что построенного спектра оно своё, не наше. Без
                    // этой заслонки первое же открытие всплывашки переписывало бы
                    // цвет элемента цветом спектра по умолчанию.
                    _syncingColorView = true;
                    try
                    {
                        _spectrumSubscription = spectrum
                            .GetObservable(ColorSpectrum.HsvColorProperty)
                            .Subscribe(OnSpectrumHsvChanged);
                    }
                    finally { _syncingColorView = false; }
                }
            }

            RefreshMiniStateFromHex();
        }

        private IDisposable? _spectrumSubscription;
        private HoneycombPicker? _honeycomb;
        private NoisePicker? _noise;

        // ── Мини-подборщик: RGB/HSL/HSV-строки и общая альфа ──────────────
        // Тот же смысл, что был у сломанного встроенного «третьего ползунка»
        // ColorSpectrum (IsColorSpectrumSliderVisible) — у Avalonia.Controls.
        // ColorPicker он рисуется без градиента, сплошной акцентной плашкой,
        // неотличимой от кнопки. Вместо него — свои строки со своим,
        // проверенным треком (тот же Slider.grad, что и в окне «Настроить
        // цвет»). Формулы HSV/HSL — оттуда же, своя копия по тому же
        // принципу, что и у NoisePicker: контрол обязан собираться сам по
        // себе, без оглядки на то, кто его показывает.
        //
        // Альфа здесь одна на все виды подборщика и живёт отдельно от
        // спектра — то же решение, что и в окне «Настроить цвет»: раньше свой
        // ползунок альфы был у каждой вкладки, значение у них всё равно было
        // общее.
        private Control? _miniRoot;
        private byte _miniAlpha = 255;
        private string _valMode = "rgb";
        private bool _syncingMini;

        /// <summary>Соты и шум отдают готовый код цвета (без альфы) — она добавляется здесь.</summary>
        private void OnMiniColorPicked(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return;
            var picked = ParseHexColor(hex);
            HexColor = FormatHex(Color.FromArgb(_miniAlpha, picked.R, picked.G, picked.B));
            SetMiniSlider("MiniSlA", _miniAlpha,
                Grad(Color.FromArgb(0, picked.R, picked.G, picked.B), Color.FromArgb(255, picked.R, picked.G, picked.B)));
        }

        private void OnMiniRgbChanged(object? sender, RangeBaseValueChangedEventArgs e)
        {
            if (_syncingMini || _spectrum is null) return;
            ApplyRgbFromSliders("MiniSlR", "MiniSlG", "MiniSlB");
        }

        private void OnValRgbChanged(object? sender, RangeBaseValueChangedEventArgs e)
        {
            if (_syncingMini || _spectrum is null) return;
            ApplyRgbFromSliders("ValSlR", "ValSlG", "ValSlB");
        }

        private void ApplyRgbFromSliders(string rName, string gName, string bName)
        {
            var r = (byte)Math.Clamp(Math.Round(ReadMiniSlider(rName)), 0, 255);
            var g = (byte)Math.Clamp(Math.Round(ReadMiniSlider(gName)), 0, 255);
            var b = (byte)Math.Clamp(Math.Round(ReadMiniSlider(bName)), 0, 255);
            PushToSpectrum(Color.FromRgb(r, g, b));
        }

        private void OnValHslChanged(object? sender, RangeBaseValueChangedEventArgs e)
        {
            if (_syncingMini || _spectrum is null) return;
            var h = ReadMiniSlider("ValSlHslH");
            var s = ReadMiniSlider("ValSlHslS") / 100.0;
            var l = ReadMiniSlider("ValSlHslL") / 100.0;
            PushToSpectrum(HslToRgb(h, s, l));
        }

        private void OnValHsvChanged(object? sender, RangeBaseValueChangedEventArgs e)
        {
            if (_syncingMini || _spectrum is null) return;
            var h = ReadMiniSlider("ValSlHsvH");
            var s = ReadMiniSlider("ValSlHsvS") / 100.0;
            var v = ReadMiniSlider("ValSlHsvV") / 100.0;
            _spectrum.HsvColor = new HsvColor(1, ((h % 360) + 360) % 360,
                Math.Clamp(s, 0, 1), Math.Clamp(v, 0, 1));
        }

        /// <summary>
        /// Ползунок третьей составляющей квадрата и колеса. Меняется только V,
        /// тон и насыщенность берутся у спектра: пересчёт через RGB терял бы
        /// их на чёрном и на серых, и точка на спектре уезжала бы в угол.
        /// </summary>
        private void OnMiniValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
        {
            if (_syncingMini || _spectrum is null) return;
            var hsv = _spectrum.HsvColor;
            _spectrum.HsvColor = new HsvColor(1, hsv.H, hsv.S,
                Math.Clamp(e.NewValue / 100.0, 0, 1));
        }

        /// <summary>
        /// Боковые полосы спектра под текущий цвет: яркость у колеса, оттенок у
        /// квадрата. Заодно затемнение колеса: спектр Avalonia в режиме
        /// HueSaturation всегда рисуется при V = 1 и от яркости не зависит — её
        /// показывает чёрный слой поверх, как WheelDim в окне «Настроить цвет».
        /// </summary>
        private void ApplySpectrumBars(HsvColor hsv)
        {
            // Заслонка снимается в прежнее положение, а не в false: метод зовут и
            // изнутри уже закрытого участка, и раньше досрочное открытие пускало
            // бы обратную волну — ползунок писал бы в спектр, который его и
            // выставил.
            var wasSyncing = _syncingMini;
            _syncingMini = true;
            try
            {
                SetMiniSlider("MiniSlVv", hsv.V * 100,
                    GradV(HsvToRgb(hsv.H, hsv.S, 0), HsvToRgb(hsv.H, hsv.S, 1)));

                // Радуга полосы оттенка задана в разметке и от цвета не зависит —
                // здесь только положение бегунка.
                SetMiniSliderValue("MiniSlH", hsv.H);

                SetMiniOpacity("SpectrumDimRing", Math.Clamp(1 - hsv.V, 0, 1));
            }
            finally { _syncingMini = wasSyncing; }
        }

        private void SetMiniSliderValue(string name, double value)
        {
            if (_miniRoot is null) return;
            var slider = FindInFlyout<Slider>(_miniRoot, name);
            if (slider is not null) slider.Value = value;
        }

        private void SetMiniOpacity(string name, double opacity)
        {
            if (_miniRoot is null) return;
            var c = FindInFlyout<Control>(_miniRoot, name);
            if (c is not null) c.Opacity = opacity;
        }

        /// <summary>
        /// Полоса оттенка у квадрата. Меняется только H, насыщенность и яркость
        /// берутся у спектра: пересчёт через RGB терял бы их на чёрном и на
        /// серых, и точка на квадрате уезжала бы в угол.
        /// </summary>
        private void OnMiniHueChanged(object? sender, RangeBaseValueChangedEventArgs e)
        {
            if (_syncingMini || _spectrum is null) return;
            var hsv = _spectrum.HsvColor;
            _spectrum.HsvColor = new HsvColor(1, Math.Clamp(e.NewValue, 0, 360), hsv.S, hsv.V);
        }

        /// <summary>Отдать спектру цвет в RGB — единственная точка, из которой он уходит наружу.</summary>
        private void PushToSpectrum(Color rgb)
        {
            if (_spectrum is null) return;
            _spectrum.HsvColor = Color.FromRgb(rgb.R, rgb.G, rgb.B).ToHsv();
        }

        private void OnMiniAlphaChanged(object? sender, RangeBaseValueChangedEventArgs e)
        {
            if (_syncingMini) return;
            _miniAlpha = (byte)Math.Clamp(Math.Round(e.NewValue), 0, 255);
            var rgb = ParseHexColor(HexColor);
            HexColor = FormatHex(Color.FromArgb(_miniAlpha, rgb.R, rgb.G, rgb.B));
        }

        private void OnValModeClick(object? sender, RoutedEventArgs e)
        {
            if (sender is not Control c || c.Tag is not string mode) return;
            ApplyValMode(mode);
        }

        private void ApplyValMode(string mode)
        {
            _valMode = mode;
            SetMiniVisible("ValRgbRows", mode == "rgb");
            SetMiniVisible("ValHslRows", mode == "hsl");
            SetMiniVisible("ValHsvRows", mode == "hsv");
            SetMiniActive("ValModeRgbBtn", mode == "rgb");
            SetMiniActive("ValModeHslBtn", mode == "hsl");
            SetMiniActive("ValModeHsvBtn", mode == "hsv");
        }

        /// <summary>Пересобрать значения и градиенты-треки RGB/HSL/HSV-строк под цвет.</summary>
        private void RefreshMiniColorRows(Color rgb)
        {
            _syncingMini = true;
            try
            {
                SetMiniSlider("MiniSlR", rgb.R, Grad(Color.FromRgb(0, rgb.G, rgb.B), Color.FromRgb(255, rgb.G, rgb.B)));
                SetMiniSlider("MiniSlG", rgb.G, Grad(Color.FromRgb(rgb.R, 0, rgb.B), Color.FromRgb(rgb.R, 255, rgb.B)));
                SetMiniSlider("MiniSlB", rgb.B, Grad(Color.FromRgb(rgb.R, rgb.G, 0), Color.FromRgb(rgb.R, rgb.G, 255)));

                SetMiniSlider("ValSlR", rgb.R, Grad(Color.FromRgb(0, rgb.G, rgb.B), Color.FromRgb(255, rgb.G, rgb.B)));
                SetMiniSlider("ValSlG", rgb.G, Grad(Color.FromRgb(rgb.R, 0, rgb.B), Color.FromRgb(rgb.R, 255, rgb.B)));
                SetMiniSlider("ValSlB", rgb.B, Grad(Color.FromRgb(rgb.R, rgb.G, 0), Color.FromRgb(rgb.R, rgb.G, 255)));

                var (h, s, l) = RgbToHsl(rgb);
                SetMiniSlider("ValSlHslH", h, HRainbow());
                SetMiniSlider("ValSlHslS", s * 100, Grad(HslToRgb(h, 0, l), HslToRgb(h, 1, l)));
                SetMiniSlider("ValSlHslL", l * 100, Grad(HslToRgb(h, s, 0), HslToRgb(h, s, 0.5), HslToRgb(h, s, 1)));

                var (hh, ss, vv) = RgbToHsv(rgb);
                SetMiniSlider("ValSlHsvH", hh, HRainbow());
                SetMiniSlider("ValSlHsvS", ss * 100, Grad(HsvToRgb(hh, 0, vv), HsvToRgb(hh, 1, vv)));
                SetMiniSlider("ValSlHsvV", vv * 100, Grad(HsvToRgb(hh, ss, 0), HsvToRgb(hh, ss, 1)));
            }
            finally { _syncingMini = false; }
        }

        /// <summary>Прочитать текущий HexColor и разослать его во все мини-ползунки. Зовётся один раз при открытии.</summary>
        private void RefreshMiniStateFromHex()
        {
            var c = ParseHexColor(HexColor);
            _miniAlpha = c.A;
            var rgb = Color.FromRgb(c.R, c.G, c.B);
            RefreshMiniColorRows(rgb);
            SetMiniSlider("MiniSlA", _miniAlpha,
                Grad(Color.FromArgb(0, rgb.R, rgb.G, rgb.B), Color.FromArgb(255, rgb.R, rgb.G, rgb.B)));
        }

        private void SetMiniSlider(string name, double value, IBrush background)
        {
            if (_miniRoot is null) return;
            var slider = FindInFlyout<Slider>(_miniRoot, name);
            if (slider is null) return;
            slider.Value = value;
            slider.Background = background;
        }

        private double ReadMiniSlider(string name)
        {
            if (_miniRoot is null) return 0;
            return FindInFlyout<Slider>(_miniRoot, name)?.Value ?? 0;
        }

        private void SetMiniVisible(string name, bool visible)
        {
            if (_miniRoot is null) return;
            var c = FindInFlyout<Control>(_miniRoot, name);
            if (c is not null) c.IsVisible = visible;
        }

        private void SetMiniActive(string name, bool active)
        {
            if (_miniRoot is null) return;
            var b = FindInFlyout<Button>(_miniRoot, name);
            b?.Classes.Set("active", active);
        }

        // Щелчок по полосе ставит бегунок на это место сразу и тут же начинает
        // перетаскивание: мышь захватывается на ползунок, и значение продолжает
        // идти за курсором, пока кнопка зажата, — отпускать и заново цеплять
        // бегунок не нужно. Обработчик свой и висит туннелем на самом ползунке:
        // у шаблонов Slider.grad и Slider.gradv кнопки трека свои, и штатный
        // перенос за них не цепляется.
        private readonly HashSet<Slider> _jumpWired = new();
        private Slider? _jumpDrag;

        private void EnableSliderJump(Visual root)
        {
            foreach (var slider in root.GetVisualDescendants().OfType<Slider>())
            {
                if (!_jumpWired.Add(slider)) continue;
                slider.AddHandler(PointerPressedEvent, OnSliderJumpPressed,
                    RoutingStrategies.Tunnel);
                slider.AddHandler(PointerMovedEvent, OnSliderJumpMoved,
                    RoutingStrategies.Tunnel);
                slider.AddHandler(PointerReleasedEvent, OnSliderJumpReleased,
                    RoutingStrategies.Tunnel);
                slider.AddHandler(PointerCaptureLostEvent, OnSliderJumpCaptureLost,
                    RoutingStrategies.Tunnel);
            }
        }

        private void OnSliderJumpPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Slider slider) return;

            var point = e.GetCurrentPoint(slider);
            if (!point.Properties.IsLeftButtonPressed) return;

            // Нажатие по самому бегунку оставляем ему: он и так тащится штатно,
            // а перенос дёрнул бы значение к точке захвата.
            if (e.Source is Visual source &&
                source.FindAncestorOfType<Thumb>(includeSelf: true) is not null) return;

            MoveSliderToPoint(slider, point.Position);

            // Захват мыши на сам ползунок — чтобы движение сразу продолжало вести
            // значение, без отпускания и повторного захвата за бегунок. Событие
            // помечается разобранным: иначе кнопка трека под курсором перехватит
            // мышь себе и начнёт подводить значение шагами.
            _jumpDrag = slider;
            e.Pointer.Capture(slider);
            e.Handled = true;
        }

        private void OnSliderJumpMoved(object? sender, PointerEventArgs e)
        {
            if (_jumpDrag is null || !ReferenceEquals(_jumpDrag, sender)) return;

            var point = e.GetCurrentPoint(_jumpDrag);
            if (!point.Properties.IsLeftButtonPressed)
            {
                EndSliderJumpDrag(e.Pointer);
                return;
            }

            MoveSliderToPoint(_jumpDrag, point.Position);
            e.Handled = true;
        }

        private void OnSliderJumpReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_jumpDrag is null || !ReferenceEquals(_jumpDrag, sender)) return;
            EndSliderJumpDrag(e.Pointer);
            e.Handled = true;
        }

        private void OnSliderJumpCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        {
            if (ReferenceEquals(_jumpDrag, sender)) _jumpDrag = null;
        }

        private void EndSliderJumpDrag(IPointer pointer)
        {
            pointer.Capture(null);
            _jumpDrag = null;
        }

        /// <summary>Поставить значение ползунка по точке в его собственных координатах.</summary>
        private static void MoveSliderToPoint(Slider slider, Point position)
        {
            var horizontal = slider.Orientation == Avalonia.Layout.Orientation.Horizontal;
            var length = horizontal ? slider.Bounds.Width : slider.Bounds.Height;

            var thumb = slider.GetVisualDescendants().OfType<Thumb>().FirstOrDefault();
            var thumbLength = thumb is null
                ? 14.0
                : (horizontal ? thumb.Bounds.Width : thumb.Bounds.Height);
            if (thumbLength <= 0) thumbLength = 14.0;

            var usable = length - thumbLength;
            if (usable <= 0) return;

            var pos = horizontal ? position.X : position.Y;
            var t = Math.Clamp((pos - thumbLength / 2) / usable, 0, 1);

            // Тот же разбор направления, что и у самого Slider: вертикальный по
            // умолчанию идёт снизу вверх, а IsDirectionReversed переворачивает
            // ход — на нём стоит полоса оттенка, у которой ноль сверху.
            var flip = horizontal ? slider.IsDirectionReversed : !slider.IsDirectionReversed;
            if (flip) t = 1 - t;

            slider.SetCurrentValue(RangeBase.ValueProperty,
                slider.Minimum + t * (slider.Maximum - slider.Minimum));
        }

        private static string FormatHex(Color c) =>
            c.A == 255
                ? $"#{c.R:X2}{c.G:X2}{c.B:X2}"
                : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

        private static Color ParseHexColor(string? hex)
        {
            try { return Color.Parse(Writersword.Core.Models.Project.GradientSpec.Parse(hex).SolidHex); }
            catch { return Colors.Black; }
        }

        private static LinearGradientBrush Grad(params Color[] stops)
        {
            var b = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative)
            };
            if (stops.Length == 1)
                b.GradientStops.Add(new GradientStop(stops[0], 0));
            else
                for (int i = 0; i < stops.Length; i++)
                    b.GradientStops.Add(new GradientStop(stops[i], (double)i / (stops.Length - 1)));
            return b;
        }

        /// <summary>
        /// То же, что Grad, но снизу вверх — для вертикального ползунка V у
        /// колеса: у него ноль внизу, и горизонтальная заливка легла бы поперёк
        /// хода.
        /// </summary>
        private static LinearGradientBrush GradV(params Color[] stops)
        {
            var b = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative)
            };
            if (stops.Length == 1)
                b.GradientStops.Add(new GradientStop(stops[0], 0));
            else
                for (int i = 0; i < stops.Length; i++)
                    b.GradientStops.Add(new GradientStop(stops[i], (double)i / (stops.Length - 1)));
            return b;
        }

        private static LinearGradientBrush HRainbow()
        {
            var b = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative)
            };
            double[] hs = { 0, 60, 120, 180, 240, 300, 360 };
            for (int i = 0; i < hs.Length; i++)
                b.GradientStops.Add(new GradientStop(HsvToRgb(hs[i], 1, 1), (double)i / (hs.Length - 1)));
            return b;
        }

        private static (double h, double s, double v) RgbToHsv(Color c)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double d = max - min;

            double h = 0;
            if (d > 1e-6)
            {
                if (max == r) h = 60 * (((g - b) / d) % 6);
                else if (max == g) h = 60 * (((b - r) / d) + 2);
                else h = 60 * (((r - g) / d) + 4);
            }
            if (h < 0) h += 360;

            double s = max <= 1e-6 ? 0 : d / max;
            double v = max;
            return (h, s, v);
        }

        private static (double h, double s, double l) RgbToHsl(Color c)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double d = max - min;
            double l = (max + min) / 2;

            double h = 0, s = 0;
            if (d > 1e-6)
            {
                s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
                if (max == r) h = 60 * (((g - b) / d) % 6);
                else if (max == g) h = 60 * (((b - r) / d) + 2);
                else h = 60 * (((r - g) / d) + 4);
                if (h < 0) h += 360;
            }
            return (h, s, l);
        }

        private static Color HsvToRgb(double h, double s, double v)
        {
            h = ((h % 360) + 360) % 360;
            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60.0 % 2) - 1));
            double m = v - c;
            double r = 0, g = 0, b = 0;
            if (h < 60) { r = c; g = x; }
            else if (h < 120) { r = x; g = c; }
            else if (h < 180) { g = c; b = x; }
            else if (h < 240) { g = x; b = c; }
            else if (h < 300) { r = x; b = c; }
            else { r = c; b = x; }
            return Color.FromRgb(
                (byte)Math.Round((r + m) * 255),
                (byte)Math.Round((g + m) * 255),
                (byte)Math.Round((b + m) * 255));
        }

        private static Color HslToRgb(double h, double s, double l)
        {
            h = ((h % 360) + 360) % 360;
            double c = (1 - Math.Abs(2 * l - 1)) * s;
            double x = c * (1 - Math.Abs((h / 60.0 % 2) - 1));
            double m = l - c / 2;
            double r = 0, g = 0, b = 0;
            if (h < 60) { r = c; g = x; }
            else if (h < 120) { r = x; g = c; }
            else if (h < 180) { g = c; b = x; }
            else if (h < 240) { g = x; b = c; }
            else if (h < 300) { r = x; b = c; }
            else { r = c; b = x; }
            return Color.FromRgb(
                (byte)Math.Round((r + m) * 255),
                (byte)Math.Round((g + m) * 255),
                (byte)Math.Round((b + m) * 255));
        }

        private void OnMiniNoisePreset(object? sender, SelectionChangedEventArgs e)
        {
            if (_noise is null) return;
            if (sender is not ComboBox box) return;
            if (box.SelectedItem is not ComboBoxItem item) return;
            if (item.Tag is not string preset) return;

            _noise.Preset = preset;
        }

        private void OnMiniNoiseRegen(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            _noise?.Regenerate();
        }

        /// <summary>Вернуть поле шума к общему виду после наезда на точку.</summary>
        private void OnMiniNoiseReset(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            _noise?.ResetView();
        }

        /// <summary>
        /// Код цвета в виде #RRGGBB — тот, которым помечены ячейки сот. Из
        /// ссылки может прийти и восьмизначный код с прозрачностью, и код
        /// градиента; в обоих случаях подсвечивать в сотах нечего.
        /// </summary>
        private static string? NormalizeHex(string? code)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;

            var text = code.Trim();
            if (!text.StartsWith("#", StringComparison.Ordinal)) return null;

            if (text.Length == 7) return text.ToUpperInvariant();
            if (text.Length == 9) return ("#" + text.Substring(3)).ToUpperInvariant();
            return null;
        }

        /// <summary>
        /// Найти именованный контрол внутри содержимого всплывашки. Сначала по
        /// области имён, потом обходом дерева: до первого показа область имён
        /// уже есть, а визуального дерева ещё нет, и наоборот — после показа
        /// обход надёжнее, если содержимое пересобрали.
        /// </summary>
        private static T? FindInFlyout<T>(Control root, string name) where T : Control
        {
             var byName = root.FindControl<T>(name);
            if (byName is not null) return byName;

            return root.GetVisualDescendants()
                .OfType<T>()
                .FirstOrDefault(x => x.Name == name);
        }

        private void OnFlyoutOpened(object? sender, EventArgs e)
        {
            IsMenuOpen = true;
            // Страховка: если флайаут не PopupFlyoutBase и Opening не сработало,
            // грузим данные и состояние секций хотя бы здесь (прежнее поведение).
            if (_flyout is not PopupFlyoutBase)
            {
                ReloadFromProject();
                LoadPalettesAndCollapse();
            }
            // При первом открытии содержимое попапа (включая спектр) создаётся
            // только к этому моменту — повторяем настройку и синхронизацию по
            // факту показа.
            ConfigureMiniPicker();
            SyncColorViewFromHex(HexColor);

            // Переходы включаем после первой отрисовки открытого попапа: стартовые
            // значения уже применены без анимации, анимируются только последующие
            // действия пользователя (клики по заголовкам секций и палитр).
            if (_flyout is Flyout fl && fl.Content is Visual root)
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => SetCollapseTransitions(root, enable: true),
                    Avalonia.Threading.DispatcherPriority.Background);
        }

        // Включает или снимает переход ScaleY у всех сворачиваемых блоков попапа
        // (секции и карточки палитр — LayoutTransformControl со ScaleTransform).
        // Карточки палитр пересоздаются при каждом открытии, поэтому проход
        // делается по всему дереву содержимого, а не по фиксированному списку.
        private static void SetCollapseTransitions(Visual root, bool enable)
        {
            foreach (var ltc in root.GetVisualDescendants().OfType<LayoutTransformControl>())
            {
                if (ltc.LayoutTransform is not ScaleTransform st) continue;
                if (!enable)
                {
                    st.Transitions = null;
                    continue;
                }
                st.Transitions ??= new Avalonia.Animation.Transitions
                {
                    new Avalonia.Animation.DoubleTransition
                    {
                        Property = ScaleTransform.ScaleYProperty,
                        Duration = TimeSpan.FromMilliseconds(220),
                        Easing = new Avalonia.Animation.Easings.CubicEaseOut()
                    }
                };
            }
        }

        private void OnFlyoutClosed(object? sender, EventArgs e)
        {
            IsMenuOpen = false;

            // Признак снимается здесь, а не при открытии: следующее нажатие
            // левой кнопкой обязано снова показать заготовки.
            _openAsMini = false;

            // Применённый при закрытии цвет уходит в «недавние» проекта.
            AddRecent(HexColor);
        }

        private void ReloadFromProject()
        {
            PinnedColors.Clear();
            RecentColors.Clear();
            var proj = CurrentProject;
            if (proj is null) return;
            foreach (var c in proj.ProjectPinnedColors) PinnedColors.Add(c);
            foreach (var c in proj.ProjectRecentColors) RecentColors.Add(c);
            HasPinnedColors = PinnedColors.Count > 0;
            HasRecentColors = RecentColors.Count > 0;
        }

        // Загружает видимые палитры (проектные + глобальные) и состояние
        // сворачивания секций попапа из глобальных настроек.
        private void LoadPalettesAndCollapse()
        {
            var s = CoreServices.GetService<ISettingsService>();
            _global = s?.GetModuleSettings<GlobalPaletteData>("ColorPalettes") ?? new GlobalPaletteData();
            _global.Palettes ??= new List<ColorPalette>();
            _global.CollapsedSections ??= new Dictionary<string, bool>();

            PopupPalettes.Clear();
            var proj = CurrentProject;

            // Единый порядок с окном управления: проектные и глобальные вместе,
            // сортировка по позиции (стабильно), а не «сначала все проектные, потом
            // все глобальные». Иначе быстрый список расходится с окном управления.
            // Позиция глобальной палитры — проектная (ссылки Id -> позиция), для
            // палитры без ссылки отправной точкой служит её глобальный Order.
            var raw = new List<(ColorPalette p, bool g, double ord)>();
            if (proj is not null)
                foreach (var p in proj.ProjectPalettes) raw.Add((p, false, p.Order));
            var order = proj?.GlobalPaletteOrder;
            foreach (var p in _global.Palettes)
                raw.Add((p, true, order is not null && order.TryGetValue(p.Id, out var o) ? o : p.Order));

            foreach (var (p, g, _) in raw.OrderBy(x => x.ord))
                if (p.Visible && p.Colors.Count > 0)
                    PopupPalettes.Add(new PaletteListItem
                    { Palette = p, IsGlobal = g, Expanded = !IsCollapsed("pp.pal." + p.Id) });
            HasPalettes = PopupPalettes.Count > 0;

            BasicExpanded = !IsCollapsed("pp.basic");
            MineExpanded = !IsCollapsed("pp.mine");
            RecentExpanded = !IsCollapsed("pp.recent");
            PalettesExpanded = !IsCollapsed("pp.palettes");
        }

        private bool IsCollapsed(string key) =>
            _global.CollapsedSections.TryGetValue(key, out var v) && v;

        private void OnSectionHeader(object? sender, RoutedEventArgs e)
        {
            if (sender is not Control c || c.Tag is not string key) return;

            bool collapsed;
            switch (key)
            {
                case "pp.basic": BasicExpanded = !BasicExpanded; collapsed = !BasicExpanded; break;
                case "pp.mine": MineExpanded = !MineExpanded; collapsed = !MineExpanded; break;
                case "pp.recent": RecentExpanded = !RecentExpanded; collapsed = !RecentExpanded; break;
                case "pp.palettes": PalettesExpanded = !PalettesExpanded; collapsed = !PalettesExpanded; break;
                default: return;
            }

            _global.CollapsedSections[key] = collapsed;
            SaveGlobalCollapse();
        }

        // Клик по заголовку отдельной палитры в попапе — сворачивает/разворачивает её.
        private void OnPalettePopupHeader(object? sender, RoutedEventArgs e)
        {
            if (sender is not Control c || c.DataContext is not PaletteListItem item) return;
            item.Expanded = !item.Expanded;
            _global.CollapsedSections["pp.pal." + item.Palette.Id] = !item.Expanded;
            SaveGlobalCollapse();
        }

        private void SaveGlobalCollapse()
        {
            var s = CoreServices.GetService<ISettingsService>();
            if (s is null) return;
            s.SaveModuleSettings("ColorPalettes", _global);
            s.Save();
        }

        private void AddPinned(string hex)
        {
            var norm = Normalize(hex);
            if (string.IsNullOrEmpty(norm)) return;
            var proj = CurrentProject;
            if (proj is null) return;
            if (proj.ProjectPinnedColors.Any(c => HexEquals(c, norm))) return;
            proj.ProjectPinnedColors.Add(norm);
            PinnedColors.Add(norm);
        }

        private void AddRecent(string hex)
        {
            var norm = Normalize(hex);
            if (string.IsNullOrEmpty(norm)) return;
            // Пресеты и уже закреплённые не засоряют «недавние».
            if (PresetColors.Any(c => HexEquals(c, norm))) return;
            var proj = CurrentProject;
            if (proj is null) return;
            if (proj.ProjectPinnedColors.Any(c => HexEquals(c, norm))) return;

            proj.ProjectRecentColors.RemoveAll(c => HexEquals(c, norm));
            proj.ProjectRecentColors.Insert(0, norm);
            while (proj.ProjectRecentColors.Count > MaxRecentColors)
                proj.ProjectRecentColors.RemoveAt(proj.ProjectRecentColors.Count - 1);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == HexColorProperty && !_syncingColorView)
                SyncColorViewFromHex(HexColor);
        }

        private void SyncColorViewFromHex(string hex)
        {
            if (_spectrum is null) return;
            try
            {
                _syncingColorView = true;
                var c = Color.Parse(
                    Writersword.Core.Models.Project.GradientSpec.Parse(hex).SolidHex);
                var hsv = Color.FromRgb(c.R, c.G, c.B).ToHsv();
                _spectrum.HsvColor = hsv;
                ApplySpectrumBars(hsv);
            }
            catch { }
            finally
            {
                _syncingColorView = false;
            }
        }

        private void OnSpectrumHsvChanged(HsvColor hsv)
        {
            if (_syncingColorView) return;

            var color = hsv.ToRgb();

            _syncingColorView = true;
            HexColor = FormatHex(Color.FromArgb(_miniAlpha, color.R, color.G, color.B));
            _syncingColorView = false;

            RefreshMiniColorRows(Color.FromRgb(color.R, color.G, color.B));
            SetMiniSlider("MiniSlA", _miniAlpha,
                Grad(Color.FromArgb(0, color.R, color.G, color.B), Color.FromArgb(255, color.R, color.G, color.B)));

            // Ползунок V ставится по значению самого спектра, а не по пересчёту
            // из RGB: на чёрном пересчёт отдаёт тон и насыщенность нулями, и
            // трек ползунка становился серым вместо цветного.
            _syncingMini = true;
            try
            {
                ApplySpectrumBars(hsv);
            }
            finally { _syncingMini = false; }
        }
    }

    /// <summary>Конвертеры для попапа выбора цвета.</summary>
    public static class PaletteConverters
    {
        // Инверсия bool: для биндинга «свёрнутого» шеврона секции.
        public static readonly IValueConverter Not =
            new FuncValueConverter<bool, bool>(b => !b);

        // bool -> ScaleY: раскрыто = 1, свёрнуто = 0 (для анимации схлопывания).
        public static readonly IValueConverter Scale =
            new FuncValueConverter<bool, double>(b => b ? 1.0 : 0.0);

        // Код цвета -> является ли он «без цвета» (полностью прозрачным):
        // для отрисовки перечёркнутого образца.
        public static readonly IValueConverter IsNoColor =
            new FuncValueConverter<string?, bool>(s =>
                string.Equals(s?.Trim(), "#00000000", StringComparison.OrdinalIgnoreCase));
    }
}
