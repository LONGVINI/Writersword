using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Reactive;

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

        public IReadOnlyList<string> PresetColors { get; } = new[]
        {
            "#F44336", "#E91E63", "#9C27B0", "#673AB7", "#3F51B5", "#2196F3",
            "#03A9F4", "#00BCD4", "#009688", "#4CAF50", "#8BC34A", "#FFEB3B",
            "#FFC107", "#FF9800", "#FF5722", "#795548", "#607D8B", "#9E9E9E",
            "#455A64", "#E07B39", "#37474F", "#212121", "#FFFFFF", "#BDBDBD"
        };

        public ReactiveCommand<string, Unit> SelectPresetCommand { get; }

        private ColorView? _colorView;
        private Button? _triggerButton;
        private bool _syncingColorView;

        public ColorPickerButton()
        {
            // DataContext НЕ трогаем — иначе внешние binding типа
            // HexColor="{Binding Color}" будут искать Color на самом ColorPickerButton,
            // а не на CharacterFolderViewModel из DataTemplate.
            InitializeComponent();

            SelectPresetCommand = ReactiveCommand.Create<string>(hex =>
            {
                HexColor = hex;
                (_triggerButton?.Flyout as Flyout)?.Hide();
            });
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _triggerButton = this.FindControl<Button>("TriggerButton");
            _colorView = this.FindControl<ColorView>("ColorViewControl");

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
            _colorView = null;
            _triggerButton = null;
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