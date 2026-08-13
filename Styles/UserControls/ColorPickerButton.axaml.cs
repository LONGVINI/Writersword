using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Data.Converters;
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

        private ColorView? _colorView;
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
        }

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
                var result = await overlay.ShowAsync(
                    HexColor, ShowCardPreview, PreviewImage, PreviewName, PreviewFallback,
                    RingEnabled, CurrentProject?.AvatarRingsAll ?? false,
                    PreviewIsGroup, BookmarkEnabled);
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
            _colorView = this.FindControl<ColorView>("ColorViewControl");

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

            if (_colorView is not null)
            {
                SyncColorViewFromHex(HexColor);
                _colorView.GetObservable(ColorView.ColorProperty)
                    .Subscribe(OnColorViewColorChanged);
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
            _colorView = null;
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
            SyncColorViewFromHex(HexColor);
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
            // При первом открытии содержимое попапа (включая ColorView) создаётся
            // только к этому моменту — повторяем синхронизацию по факту показа.
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
            if (_colorView is null) return;
            try
            {
                _syncingColorView = true;
                _colorView.Color = Color.Parse(
                    Writersword.Core.Models.Project.GradientSpec.Parse(hex).SolidHex);
            }
            catch { }
            finally
            {
                _syncingColorView = false;
            }
        }

        private void OnColorViewColorChanged(Color color)
        {
            if (_syncingColorView) return;
            _syncingColorView = true;
            HexColor = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            _syncingColorView = false;
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
