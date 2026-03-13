using Avalonia;
using Avalonia.Controls;
using Serilog;
using System;
using System.ComponentModel;
using System.Reactive.Disposables;
using Writersword.Core.Enums;
using Writersword.Core.Interfaces.Settings;

namespace Writersword.Views.Settings
{
    /// <summary>
    /// Кнопки сброса настройки — ↺G и ↺.
    /// Ставится рядом с контролом в Grid, без обёртки контента.
    /// Разработчик модуля использует напрямую вместо SettingRow.
    /// </summary>
    public partial class SettingButtons : UserControl
    {
        private static readonly ILogger _logger = Log.ForContext<SettingButtons>();

        private IDisposable? _settingSubscription;
        private Button? _btnResetToGlobal;
        private Button? _btnResetToHardcoded;

        // ── Avalonia Properties ───────────────────────────────────────────

        public static readonly StyledProperty<ISettingValue?> SettingProperty =
            AvaloniaProperty.Register<SettingButtons, ISettingValue?>(nameof(Setting));

        public static readonly StyledProperty<SettingsFieldContext> ContextProperty =
            AvaloniaProperty.Register<SettingButtons, SettingsFieldContext>(
                nameof(Context), SettingsFieldContext.Global);

        // ── CLR Properties ────────────────────────────────────────────────

        public ISettingValue? Setting
        {
            get => GetValue(SettingProperty);
            set => SetValue(SettingProperty, value);
        }

        public SettingsFieldContext Context
        {
            get => GetValue(ContextProperty);
            set => SetValue(ContextProperty, value);
        }

        // ── Constructor ───────────────────────────────────────────────────

        public SettingButtons()
        {
            InitializeComponent();

            _btnResetToGlobal = this.FindControl<Button>("BtnResetToGlobal");
            _btnResetToHardcoded = this.FindControl<Button>("BtnResetToHardcoded");

            if (_btnResetToGlobal is not null)
                _btnResetToGlobal.Click += (_, _) =>
                {
                    _logger.Debug("ResetToGlobal: {Type}", Setting?.GetType().Name);
                    Setting?.ResetToGlobal();
                };

            if (_btnResetToHardcoded is not null)
                _btnResetToHardcoded.Click += (_, _) =>
                {
                    _logger.Debug("ResetToHardcoded: {Type}", Setting?.GetType().Name);
                    Setting?.ResetToHardcoded();
                };

            this.GetObservable(SettingProperty).Subscribe(OnSettingChanged);
            this.GetObservable(ContextProperty).Subscribe(_ => UpdateVisibility());
        }

        // ── Private ───────────────────────────────────────────────────────

        private void OnSettingChanged(ISettingValue? setting)
        {
            _settingSubscription?.Dispose();
            _settingSubscription = null;

            if (setting is INotifyPropertyChanged npc)
            {
                PropertyChangedEventHandler handler = (_, _) => UpdateVisibility();
                npc.PropertyChanged += handler;
                _settingSubscription = Disposable.Create(() => npc.PropertyChanged -= handler);
            }

            UpdateVisibility();
        }

        private void UpdateVisibility()
        {
            bool showHardcoded = Setting?.IsOverriddenFromHardcoded == true;
            bool showGlobal = Context == SettingsFieldContext.Local
                                 && Setting?.IsOverriddenFromGlobal == true;

            _logger.Debug("Visibility: SH={SH}, SG={SG}", showHardcoded, showGlobal);

            if (_btnResetToHardcoded is not null)
                _btnResetToHardcoded.IsVisible = showHardcoded;

            if (_btnResetToGlobal is not null)
                _btnResetToGlobal.IsVisible = showGlobal;
        }
    }
}