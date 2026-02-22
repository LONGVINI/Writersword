using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using ReactiveUI;
using System;
using System.Reactive.Linq;
using System.Text;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Models;
using Writersword.Modules.Common;
using Writersword.Modules.Timer.ViewModels;
using Writersword.Modules.Timer.Views;
using Writersword.Src.Core.Interfaces.Services.Storage;
using Writersword.Src.Core.Services;
using Writersword.Src.Modules.Timer.Models;
using Writersword.Src.Modules.Timer.Resources;

namespace Writersword.Modules.Timer
{
    public class TimerModule : BaseModule, IConfigurableModule
    {
        private readonly ILogger<TimerModule> _logger;
        private TimerViewModel? _viewModel;

        private const string LocalSettingsPath = "Timer/settings.json";

        public TimerModule() : base()
        {
            _logger = App.Services.GetService<ILogger<TimerModule>>()!;
        }

        public override string moduleType => "Timer";
        public override string Title { get; set; } = "Timer";
        public override object? ViewModel => _viewModel;
        public override IModuleMetadata Metadata => new TimerMetadata();

        public string SettingsTitle => TimerStrings.DisplayName;
        public Type SettingsType => typeof(TimerSettings);

        public override void Initialize()
        {
            _viewModel = new TimerViewModel();

            var settingsService = App.Services.GetRequiredService<ISettingsService>();
            var globalSettings = settingsService.GetModuleSettings<TimerSettings>(moduleType)
                                 ?? new TimerSettings();

            _viewModel.ApplySettings(globalSettings);

            _logger.LogDebug("Initialized (moduleType: {moduleType})", moduleType);
        }

        /// <summary>
        /// Загрузить локальные настройки из ZIP проекта и применить поверх глобальных
        /// Вызывается после установки Context
        /// </summary>
        private void LoadAndApplyLocalSettings()
        {
            if (Context?.FileStorage == null) return;

            var data = Context.FileStorage.ReadFile(LocalSettingsPath);
            if (data == null) return;

            try
            {
                var json = Encoding.UTF8.GetString(data);
                var local = JsonConvert.DeserializeObject<TimerSettings>(json);
                if (local != null)
                {
                    _viewModel?.ApplySettings(local);
                    _logger.LogDebug("Local settings applied: DefaultMinutes={Min}, IsCountdown={Countdown}",
                        local.DefaultMinutes, local.IsCountdown);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading local settings");
            }
        }

        /// <summary>
        /// Сохранить локальные настройки в ZIP проекта
        /// </summary>
        private void SaveLocalSettings(TimerSettings settings)
        {
            if (Context?.FileStorage == null)
            {
                _logger.LogWarning("Cannot save local settings — FileStorage is null");
                return;
            }

            try
            {
                var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                var data = Encoding.UTF8.GetBytes(json);
                Context.FileStorage.WriteFile(LocalSettingsPath, data);
                _logger.LogDebug("Local settings saved to ZIP");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving local settings");
            }
        }

        protected override void OnContextChanged(DocumentContext? context)
        {
            if (context != null)
            {
                _logger.LogDebug("Context changed - timer continues running");
                LoadAndApplyLocalSettings();
            }
        }

        public object GetSettings()
        {
            var settingsService = App.Services.GetRequiredService<ISettingsService>();
            return settingsService.GetModuleSettings<TimerSettings>(moduleType)
                   ?? new TimerSettings();
        }

        public void ApplySettings(object settings)
        {
            if (settings is not TimerSettings typed) return;

            var settingsService = App.Services.GetRequiredService<ISettingsService>();
            settingsService.SaveModuleSettings(moduleType, typed);

            _viewModel?.ApplySettings(typed);
            _logger.LogDebug("Global settings applied: DefaultMinutes={Min}, IsCountdown={Countdown}",
                typed.DefaultMinutes, typed.IsCountdown);
        }

        public object GetLocalSettings()
        {
            if (Context?.FileStorage == null)
                return GetSettings();

            var data = Context.FileStorage.ReadFile(LocalSettingsPath);
            if (data == null)
                return GetSettings();

            try
            {
                var json = Encoding.UTF8.GetString(data);
                return JsonConvert.DeserializeObject<TimerSettings>(json) ?? GetSettings();
            }
            catch
            {
                return GetSettings();
            }
        }

        public void ApplyLocalSettings(object settings)
        {
            if (settings is not TimerSettings typed) return;

            SaveLocalSettings(typed);
            _viewModel?.ApplySettings(typed);
            _logger.LogDebug("Local settings applied and saved: DefaultMinutes={Min}, IsCountdown={Countdown}",
                typed.DefaultMinutes, typed.IsCountdown);
        }

        public Control CreateSettingsView()
        {
            var settingsService = App.Services.GetRequiredService<ISettingsService>();
            var settings = settingsService.GetModuleSettings<TimerSettings>(moduleType)
                           ?? new TimerSettings();

            var vm = new TimerSettingsViewModel
            {
                DefaultMinutes = settings.DefaultMinutes,
                DefaultSeconds = settings.DefaultSeconds,
                IsCountdown = settings.IsCountdown
            };

            vm.WhenAnyValue(x => x.DefaultMinutes, x => x.DefaultSeconds, x => x.IsCountdown)
                .Skip(1)
                .Subscribe(tuple =>
                {
                    ApplySettings(new TimerSettings
                    {
                        DefaultMinutes = tuple.Item1,
                        DefaultSeconds = tuple.Item2,
                        IsCountdown = tuple.Item3
                    });
                });

            return new TimerSettingsView { DataContext = vm };
        }

        public Control CreateLocalSettingsView()
        {
            var local = GetLocalSettings() as TimerSettings ?? new TimerSettings();

            var vm = new TimerSettingsViewModel
            {
                DefaultMinutes = local.DefaultMinutes,
                DefaultSeconds = local.DefaultSeconds,
                IsCountdown = local.IsCountdown
            };

            vm.WhenAnyValue(x => x.DefaultMinutes, x => x.DefaultSeconds, x => x.IsCountdown)
                .Skip(1)
                .Subscribe(tuple =>
                {
                    ApplyLocalSettings(new TimerSettings
                    {
                        DefaultMinutes = tuple.Item1,
                        DefaultSeconds = tuple.Item2,
                        IsCountdown = tuple.Item3
                    });
                });

            return new TimerSettingsView { DataContext = vm };
        }

        public override object? GetCustomData() => null;
        public override object? GetSessionData() => null;

        public override Control? CreateView()
        {
            return new TimerView { DataContext = ViewModel };
        }
    }

    internal class TimerMetadata : IModuleMetadata
    {
        public string ModuleType => "Timer";
        public string DisplayName => TimerStrings.DisplayName;
        public string Description => TimerStrings.Description;
    }
}