using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Linq;
using Writersword.Core.Enums;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Interfaces.Services;
using Writersword.Core.Models;
using Writersword.Core.Models.Settings;
using Writersword.Modules.Common;
using Writersword.Modules.Timer.ViewModels;
using Writersword.Modules.Timer.Views;
using Writersword.Core.Interfaces.Services.Storage;
using Writersword.Core.Services;
using Writersword.Modules.Timer.Models;
using Writersword.Modules.Timer.Resources;

namespace Writersword.Modules.Timer
{
    public class TimerModule : BaseModule, IConfigurableModule, IHotKeyProvider
    {
        private readonly ILogger<TimerModule> _logger;
        private TimerViewModel? _viewModel;
        private TimerSettingsViewModel? _settingsVm;

        /// <summary>Хардкод дефолты — создаются один раз, никогда не меняются.</summary>
        private static readonly TimerSettings _hardcodedDefaults = new();

        public TimerModule() : base()
        {
            _logger = CoreServices.GetService<ILogger<TimerModule>>()!;
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

            var settingsService = CoreServices.GetRequiredService<ISettingsService>();
            var globalSettings = settingsService.GetModuleSettings<TimerSettings>(moduleType)
                                 ?? new TimerSettings();

            _viewModel.ApplySettings(globalSettings);

            base.Initialize();

            _logger.LogDebug("Initialized (moduleType: {moduleType})", moduleType);
        }

        /// <summary>
        /// Получить список горячих клавиш модуля.
        /// Реализация IHotKeyProvider — делегирует в TimerMetadata.
        /// </summary>
        public IReadOnlyList<HotKey> GetHotKeys() => new TimerMetadata().GetHotKeys();

        /// <summary>Выполнить действие по ID горячей клавиши.</summary>
        public void ExecuteHotKey(string id)
        {
            if (_viewModel == null) return;

            switch (id)
            {
                case "timer.start":
                    _viewModel.StartCommand.Execute(Unit.Default).Subscribe();
                    _logger.LogDebug("HotKey executed: timer.start");
                    break;

                case "timer.stop":
                    _viewModel.StopCommand.Execute(Unit.Default).Subscribe();
                    _logger.LogDebug("HotKey executed: timer.stop");
                    break;

                case "timer.reset":
                    _viewModel.ResetCommand.Execute(Unit.Default).Subscribe();
                    _logger.LogDebug("HotKey executed: timer.reset");
                    break;

                default:
                    _logger.LogWarning("Unknown hotkey id: {Id}", id);
                    break;
            }
        }

        protected override void OnContextChanged(DocumentContext? context)
        {
            if (context != null)
                _logger.LogDebug("Context changed - timer continues running");
        }

        // ── IConfigurableModule ───────────────────────────────────────────

        /// <summary>
        /// Возвращает хардкод дефолты модуля.
        /// </summary>
        public object GetDefaultSettings() => _hardcodedDefaults;

        /// <summary>
        /// Получить текущие глобальные настройки из ISettingsService.
        /// </summary>
        public object GetSettings()
        {
            var settingsService = CoreServices.GetRequiredService<ISettingsService>();
            return settingsService.GetModuleSettings<TimerSettings>(moduleType)
                   ?? new TimerSettings();
        }

        /// <summary>
        /// Применить глобальные настройки: сохранить в сервис и обновить VM.
        /// </summary>
        public void ApplySettings(object settings)
        {
            if (settings is not TimerSettings typed) return;

            var settingsService = CoreServices.GetRequiredService<ISettingsService>();
            settingsService.SaveModuleSettings(moduleType, typed);

            _viewModel?.ApplySettings(typed);
            _logger.LogDebug("Global settings applied: DefaultMinutes={Min}, IsCountdown={Countdown}",
                typed.DefaultMinutes, typed.IsCountdown);
        }

        /// <summary>
        /// Получить текущие локальные настройки из _settingsVm.
        /// Если VM не создана — возвращает глобальные.
        /// Вызывается сервисом при сохранении в ZIP.
        /// </summary>
        public object GetLocalSettings()
        {
            if (_settingsVm is null)
                return GetSettings();

            return new TimerSettings
            {
                DefaultMinutes = (int)(_settingsVm.DefaultMinutes ?? 0),
                DefaultSeconds = (int)(_settingsVm.DefaultSeconds ?? 0),
                IsCountdown = _settingsVm.IsCountdown
            };
        }

        /// <summary>
        /// Применить локальные настройки к VM и живому таймеру.
        /// Файловый I/O — только через ILocalSettingsStorageService, не здесь.
        /// </summary>
        public void ApplyLocalSettings(object settings)
        {
            if (settings is not TimerSettings typed) return;

            if (_settingsVm is not null)
            {
                _settingsVm.DefaultMinutes = typed.DefaultMinutes;
                _settingsVm.DefaultSeconds = typed.DefaultSeconds;
                _settingsVm.IsCountdown = typed.IsCountdown;
            }

            _viewModel?.ApplySettings(typed);
            _logger.LogDebug("Local settings applied: DefaultMinutes={Min}, IsCountdown={Countdown}",
                typed.DefaultMinutes, typed.IsCountdown);
        }

        /// <summary>
        /// Сбросить UI глобальных настроек к хардкод дефолтам.
        /// Вызывается toolbar кнопкой в глобальной вкладке.
        /// </summary>
        public void ResetSettingsToDefaults()
        {
            if (_settingsVm is null) return;

            _settingsVm.DefaultMinutes = _hardcodedDefaults.DefaultMinutes;
            _settingsVm.DefaultSeconds = _hardcodedDefaults.DefaultSeconds;
            _settingsVm.IsCountdown = _hardcodedDefaults.IsCountdown;

            _logger.LogDebug("Global settings reset to hardcoded defaults");
        }

        /// <summary>
        /// Сбросить UI локальных настроек к глобальным значениям.
        /// Вызывается toolbar кнопкой в локальной вкладке.
        /// </summary>
        public void ResetLocalSettingsToGlobal()
        {
            if (_settingsVm is null) return;

            var globalSettings = (GetSettings() as TimerSettings) ?? new TimerSettings();

            _settingsVm.DefaultMinutes = globalSettings.DefaultMinutes;
            _settingsVm.DefaultSeconds = globalSettings.DefaultSeconds;
            _settingsVm.IsCountdown = globalSettings.IsCountdown;

            _logger.LogDebug("Local settings reset to global values");
        }

        /// <summary>
        /// Сбросить UI локальных настроек к хардкод дефолтам.
        /// Вызывается toolbar кнопкой в локальной вкладке.
        /// </summary>
        public void ResetLocalSettingsToDefaults()
        {
            if (_settingsVm is null) return;

            _settingsVm.DefaultMinutes = _hardcodedDefaults.DefaultMinutes;
            _settingsVm.DefaultSeconds = _hardcodedDefaults.DefaultSeconds;
            _settingsVm.IsCountdown = _hardcodedDefaults.IsCountdown;

            _logger.LogDebug("Local settings reset to hardcoded defaults");
        }

        /// <summary>
        /// Создать View для глобальных настроек.
        /// Подписка на изменения — автоматически сохраняет глобальные настройки.
        /// </summary>
        public Control CreateSettingsView()
        {
            var settingsService = CoreServices.GetRequiredService<ISettingsService>();
            var settings = settingsService.GetModuleSettings<TimerSettings>(moduleType)
                           ?? new TimerSettings();

            _settingsVm = new TimerSettingsViewModel
            {
                DefaultMinutes = settings.DefaultMinutes,
                DefaultSeconds = settings.DefaultSeconds,
                IsCountdown = settings.IsCountdown
            };

            _settingsVm.WhenAnyValue(x => x.DefaultMinutes, x => x.DefaultSeconds, x => x.IsCountdown)
                .Skip(1)
                .Subscribe(tuple =>
                {
                    ApplySettings(new TimerSettings
                    {
                        DefaultMinutes = (int)(tuple.Item1 ?? 0),
                        DefaultSeconds = (int)(tuple.Item2 ?? 0),
                        IsCountdown = tuple.Item3
                    });
                });

            return new TimerSettingsView { DataContext = _settingsVm };
        }

        /// <summary>
        /// Создать View для локальных настроек проекта.
        /// Начальные значения берутся из ZIP через ILocalSettingsStorageService,
        /// или из глобальных если локальных нет.
        /// </summary>
        public Control CreateLocalSettingsView()
        {
            TimerSettings local;

            if (Context?.FileStorage != null)
            {
                var service = CoreServices.GetRequiredService<ILocalSettingsStorageService>();
                local = service.Load(Context.FileStorage, moduleType, typeof(TimerSettings))
                        as TimerSettings
                        ?? (GetSettings() as TimerSettings)
                        ?? new TimerSettings();
            }
            else
            {
                local = (GetSettings() as TimerSettings) ?? new TimerSettings();
            }

            _settingsVm = new TimerSettingsViewModel
            {
                DefaultMinutes = local.DefaultMinutes,
                DefaultSeconds = local.DefaultSeconds,
                IsCountdown = local.IsCountdown
            };

            return new TimerSettingsView { DataContext = _settingsVm };
        }

        /// <summary>
        /// Применить текущие глобальные UI-значения к локальной VM.
        /// Timer использует одну VM — просто обновляем значения из глобальных.
        /// </summary>
        public void ApplyGlobalToLocal()
        {
            if (_settingsVm is null) return;

            var globalSettings = (GetSettings() as TimerSettings) ?? new TimerSettings();

            _settingsVm.DefaultMinutes = globalSettings.DefaultMinutes;
            _settingsVm.DefaultSeconds = globalSettings.DefaultSeconds;
            _settingsVm.IsCountdown = globalSettings.IsCountdown;

            _logger.LogDebug("ApplyGlobalToLocal completed");
        }

        /// <summary>
        /// Сохранить текущие локальные UI-значения как глобальные.
        /// </summary>
        public void PromoteLocalToGlobal()
        {
            if (_settingsVm is null) return;

            var settings = new TimerSettings
            {
                DefaultMinutes = (int)(_settingsVm.DefaultMinutes ?? 0),
                DefaultSeconds = (int)(_settingsVm.DefaultSeconds ?? 0),
                IsCountdown = _settingsVm.IsCountdown
            };

            var settingsService = CoreServices.GetRequiredService<ISettingsService>();
            settingsService.SaveModuleSettings(moduleType, settings);

            _logger.LogDebug("PromoteLocalToGlobal completed");
        }

        public override object? GetCustomData() => null;
        public override object? GetSessionData() => null;

        public override Control? CreateView()
        {
            return new TimerView { DataContext = ViewModel };
        }
    }

    /// <summary>
    /// Метаданные модуля таймера.
    /// Реализует IHotKeyDescriptor — предоставляет статический список горячих клавиш
    /// без необходимости создавать живой экземпляр TimerModule.
    /// </summary>
    internal class TimerMetadata : IModuleMetadata, IHotKeyDescriptor
    {
        public string ModuleType => "Timer";
        public string DisplayName => TimerStrings.DisplayName;
        public string Description => TimerStrings.Description;

        /// <summary>
        /// Статический список горячих клавиш таймера.
        /// DefaultGesture null — пользователь назначает сам.
        /// </summary>
        public IReadOnlyList<HotKey> GetHotKeys() => new[]
        {
            new HotKey
            {
                Id = "timer.start",
                DisplayNameKey = TimerStrings.HotKey_Timer_Start,
                Category = HotKeyCategory.Tools,
                Scope = HotKeyScope.Background,
                ModuleType = ModuleType,
                DefaultGesture = null
            },
            new HotKey
            {
                Id = "timer.stop",
                DisplayNameKey = TimerStrings.HotKey_Timer_Stop,
                Category = HotKeyCategory.Tools,
                Scope = HotKeyScope.Background,
                ModuleType = ModuleType,
                DefaultGesture = null
            },
            new HotKey
            {
                Id = "timer.reset",
                DisplayNameKey = TimerStrings.HotKey_Timer_Reset,
                Category = HotKeyCategory.Tools,
                Scope = HotKeyScope.Background,
                ModuleType = ModuleType,
                DefaultGesture = null
            }
        };
    }
}