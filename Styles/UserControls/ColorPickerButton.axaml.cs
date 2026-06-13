using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
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

        public static readonly StyledProperty<bool> ShowCardPreviewProperty =
            AvaloniaProperty.Register<ColorPickerButton, bool>(nameof(ShowCardPreview), false);

        // true для пикеров в карточках персонажей — в модале показывается превью карточки.
        public bool ShowCardPreview
        {
            get => GetValue(ShowCardPreviewProperty);
            set => SetValue(ShowCardPreviewProperty, value);
        }

        public IReadOnlyList<string> PresetColors { get; } = new[]
        {
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

        public ReactiveCommand<string, Unit> SelectPresetCommand { get; }
        public ReactiveCommand<string, Unit> RemovePinnedCommand { get; }
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
                var result = await overlay.ShowAsync(HexColor, ShowCardPreview);
                if (!string.IsNullOrWhiteSpace(result))
                    HexColor = result;
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "ColorPicker: OpenAdvanced failed");
            }
        }

        private static ProjectFile? CurrentProject =>
            CoreServices.GetService<ITabCollection>()?.ActiveTab?.Context?.Project;

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
                _flyout.Opened -= OnFlyoutOpened;
                _flyout.Closed -= OnFlyoutClosed;
            }
            _flyout = null;
            _colorView = null;
            _triggerButton = null;
        }

        private void OnFlyoutOpened(object? sender, EventArgs e)
        {
            ReloadFromProject();
            SyncColorViewFromHex(HexColor);
        }

        private void OnFlyoutClosed(object? sender, EventArgs e)
        {
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
                _colorView.Color = Color.Parse(hex);
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
}
