using Avalonia.Controls;
using Avalonia.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Text;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Models;
using Writersword.Modules.Common;
using Writersword.Modules.TextEditor.ViewModels;
using Writersword.Modules.TextEditor.Views;
using Writersword.Src.Core.Interfaces.Services.Storage;
using Writersword.Src.Core.Services;
using Writersword.Src.Modules.TextEditor.Models;
using Writersword.Src.Modules.TextEditor.Resources;

namespace Writersword.Modules.TextEditor
{
    public class TextEditorModule : BaseModule, IConfigurableModule, IUndoableModule
    {
        private readonly ILogger<TextEditorModule> _logger;
        private TextEditorViewModel? _viewModel;
        private IDisposable? _textSubscription;

        private const string LocalSettingsPath = "TextEditor/settings.json";

        public TextEditorModule() : base()
        {
            _logger = App.Services.GetService<ILogger<TextEditorModule>>()!;
        }

        public override string moduleType => "TextEditor";
        public override string Title { get; set; } = "Text Editor";
        public override object? ViewModel => _viewModel;
        public override IModuleMetadata Metadata => new TextEditorMetadata();

        public string SettingsTitle => TextEditorStrings.DisplayName;
        public Type SettingsType => typeof(TextEditorSettings);

        // -----------------------------------------------------------------------
        // IUndoableModule — делегируем в нативный TextBox через колбэки из View
        // -----------------------------------------------------------------------

        public bool CanUndo => true;
        public bool CanRedo => true;
        public string? UndoDescription => null;
        public string? RedoDescription => null;

        /// <summary>Подключается из TextEditorView — вызывает TextBox.Undo()</summary>
        public Action? UndoAction { get; set; }

        /// <summary>Подключается из TextEditorView — вызывает TextBox.Redo()</summary>
        public Action? RedoAction { get; set; }

        public void Undo() => UndoAction?.Invoke();
        public void Redo() => RedoAction?.Invoke();

        /// <summary>Не используется — стек ведёт сам TextBox</summary>
        public void PushCommand(IUndoableCommand command) { }

        // -----------------------------------------------------------------------

        public override void Initialize()
        {
            _logger.LogDebug("Initialize START (moduleType: {moduleType})", moduleType);
            _viewModel = new TextEditorViewModel();

            var settingsService = App.Services.GetRequiredService<ISettingsService>();
            var globalSettings = settingsService.GetModuleSettings<TextEditorSettings>(moduleType)
                                 ?? new TextEditorSettings();

            _viewModel.ApplySettings(globalSettings);
            CreateSubscription();

            _logger.LogDebug("Initialized (moduleType: {moduleType})", moduleType);
        }

        private void LoadAndApplyLocalSettings()
        {
            if (Context?.FileStorage == null) return;

            var data = Context.FileStorage.ReadFile(LocalSettingsPath);
            if (data == null) return;

            try
            {
                var json = Encoding.UTF8.GetString(data);
                var local = JsonConvert.DeserializeObject<TextEditorSettings>(json);
                if (local != null)
                {
                    _viewModel?.ApplySettings(local);
                    _logger.LogDebug("Local settings applied: FontSize={FontSize}, FontFamily={FontFamily}",
                        local.FontSize, local.FontFamily);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading local settings");
            }
        }

        private void SaveLocalSettings(TextEditorSettings settings)
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
            if (context != null && _viewModel != null)
            {
                _viewModel.IsReadOnly = context.IsInCompareMode;
                _logger.LogDebug("Context changed - IsReadOnly: {IsReadOnly}", _viewModel.IsReadOnly);
                LoadAndApplyLocalSettings();
            }
        }

        public object GetSettings()
        {
            var settingsService = App.Services.GetRequiredService<ISettingsService>();
            return settingsService.GetModuleSettings<TextEditorSettings>(moduleType)
                   ?? new TextEditorSettings();
        }

        public void ApplySettings(object settings)
        {
            if (settings is not TextEditorSettings typed) return;

            var settingsService = App.Services.GetRequiredService<ISettingsService>();
            settingsService.SaveModuleSettings(moduleType, typed);

            _viewModel?.ApplySettings(typed);
            _logger.LogDebug("Global settings applied: FontSize={FontSize}, FontFamily={FontFamily}",
                typed.FontSize, typed.FontFamily);
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
                return JsonConvert.DeserializeObject<TextEditorSettings>(json) ?? GetSettings();
            }
            catch
            {
                return GetSettings();
            }
        }

        public void ApplyLocalSettings(object settings)
        {
            if (settings is not TextEditorSettings typed) return;

            SaveLocalSettings(typed);
            _viewModel?.ApplySettings(typed);
            _logger.LogDebug("Local settings applied and saved: FontSize={FontSize}, FontFamily={FontFamily}",
                typed.FontSize, typed.FontFamily);
        }

        public Control CreateSettingsView()
        {
            var settingsService = App.Services.GetRequiredService<ISettingsService>();
            var settings = settingsService.GetModuleSettings<TextEditorSettings>(moduleType)
                           ?? new TextEditorSettings();

            var vm = new TextEditorSettingsViewModel
            {
                FontSize = settings.FontSize,
                FontFamily = settings.FontFamily
            };

            vm.WhenAnyValue(x => x.FontSize, x => x.FontFamily)
                .Skip(1)
                .Subscribe(tuple =>
                {
                    ApplySettings(new TextEditorSettings
                    {
                        FontSize = tuple.Item1,
                        FontFamily = tuple.Item2
                    });
                });

            return new TextEditorSettingsView { DataContext = vm };
        }

        public Control CreateLocalSettingsView()
        {
            var local = GetLocalSettings() as TextEditorSettings ?? new TextEditorSettings();

            var vm = new TextEditorSettingsViewModel
            {
                FontSize = local.FontSize,
                FontFamily = local.FontFamily
            };

            vm.WhenAnyValue(x => x.FontSize, x => x.FontFamily)
                .Skip(1)
                .Subscribe(tuple =>
                {
                    ApplyLocalSettings(new TextEditorSettings
                    {
                        FontSize = tuple.Item1,
                        FontFamily = tuple.Item2
                    });
                });

            return new TextEditorSettingsView { DataContext = vm };
        }

        private void CreateSubscription()
        {
            _textSubscription?.Dispose();
            _textSubscription = _viewModel!.WhenAnyValue(x => x.PlainText)
                .Throttle(TimeSpan.FromMilliseconds(500))
                .Subscribe(text =>
                {
                    _logger.LogDebug("Text updated: {Length} chars", text?.Length ?? 0);
                });
        }

        public override object? GetCustomData()
        {
            var text = _viewModel?.PlainText ?? "";
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        public override object? GetSessionData()
        {
            return new
            {
                lastEditTime = DateTime.Now,
                scrollPosition = 0
            };
        }

        public override void SetCustomData(object? data)
        {
            if (_viewModel == null)
            {
                _logger.LogWarning("SetCustomData called but ViewModel is null");
                return;
            }

            _textSubscription?.Dispose();

            string text = data switch
            {
                string str => str,
                JValue jValue => jValue.Value?.ToString() ?? "",
                not null => data.ToString() ?? "",
                _ => ""
            };

            _viewModel.LoadDocument(text);
            _logger.LogDebug("Loaded {Length} chars", text.Length);

            CreateSubscription();
        }

        public override void SetSessionData(object? data)
        {
            _logger.LogDebug("SessionData set");
        }

        public override void Dispose()
        {
            _textSubscription?.Dispose();
            UndoAction = null;
            RedoAction = null;
            _logger.LogDebug("Disposed (moduleType: {moduleType})", moduleType);
        }

        public override Control? CreateView()
        {
            var view = new TextEditorView();
            view.DataContext = ViewModel;
            view.WireModule(this);
            return view;
        }

        public IReadOnlyList<KeyGesture> BlockedNativeGestures => new[]
        {
            new KeyGesture(Key.Z, KeyModifiers.Control),
            new KeyGesture(Key.Y, KeyModifiers.Control),
            new KeyGesture(Key.Z, KeyModifiers.Control | KeyModifiers.Shift)
        };
    }

    internal class TextEditorMetadata : IModuleMetadata
    {
        public string ModuleType => "TextEditor";
        public string DisplayName => TextEditorStrings.DisplayName;
        public string Description => TextEditorStrings.Description;
    }
}