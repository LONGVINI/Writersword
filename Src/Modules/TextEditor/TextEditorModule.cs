using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Writersword.Core.Interfaces.Modules;
using Writersword.Modules.Common;
using Writersword.Modules.TextEditor.Commands;
using Writersword.Modules.TextEditor.Models;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Services;
using Writersword.Modules.TextEditor.ViewModels;
using Writersword.Modules.TextEditor.Views;
using Writersword.Modules.TextEditor.Views.Settings;
using Writersword.Src.Core.Interfaces.Services.Storage;
using Writersword.Src.Modules.TextEditor.Resources;

namespace Writersword.Modules.TextEditor
{
    internal sealed class TextEditorModuleMetadata : IModuleMetadata
    {
        public string ModuleType => "TextEditor";
        public string DisplayName => TextEditorStrings.DisplayName;
        public string Description => TextEditorStrings.Description;
    }

    public sealed class TextEditorModule : BaseModule, IConfigurableModule, IUndoableModule
    {
        private static readonly ILogger _logger = Log.ForContext<TextEditorModule>();

        private readonly DocumentSerializer _serializer;
        private readonly DeltaHashService _hashService;
        private readonly ChunkManager _chunkManager;
        private readonly UndoRedoStack _undoStack = new();
        private readonly ISettingsService _settingsService;

        private static readonly TextEditorSettings _hardcodedDefaults = new();

        private TextEditorViewModel? _viewModel;
        private TextEditorSettingsViewModel? _globalSettingsVm;
        private TextEditorSettingsViewModel? _localSettingsVm;

        private TextEditorSettings _globalSettings = new();
        private TextEditorSettings _localSettings = new();

        private DeltaCachePayload? _lastDeltaPayload;

        public override string moduleType => "TextEditor";
        public override object? ViewModel => _viewModel;
        public override IModuleMetadata Metadata { get; } = new TextEditorModuleMetadata();
        public override bool SupportsDeltaComparison => true;

        // ── IUndoableModule ───────────────────────────────────────────────

        public bool CanUndo => _undoStack.CanUndo;
        public bool CanRedo => _undoStack.CanRedo;
        public string? UndoDescription => _undoStack.UndoDescription;
        public string? RedoDescription => _undoStack.RedoDescription;

        public void Undo() { _undoStack.Undo(); _viewModel?.DocumentViewModel?.FireCursorContextChanged(); }
        public void Redo() { _undoStack.Redo(); _viewModel?.DocumentViewModel?.FireCursorContextChanged(); }
        public void PushCommand(IUndoableCommand command) => _undoStack.Push(command);

        public IReadOnlyList<KeyGesture> BlockedNativeGestures { get; } = new[]
        {
            new KeyGesture(Key.Z, KeyModifiers.Control),
            new KeyGesture(Key.Y, KeyModifiers.Control)
        };

        // ── Constructor ───────────────────────────────────────────────────

        public TextEditorModule()
        {
            _hashService = new DeltaHashService();
            _chunkManager = new ChunkManager(_hashService);
            _serializer = new DocumentSerializer(_hashService, _chunkManager);
            _settingsService = App.Services.GetRequiredService<ISettingsService>();
            Title = "Text Editor";

            var saved = _settingsService.GetModuleSettings<TextEditorSettings>(moduleType);
            if (saved is not null)
            {
                _globalSettings = saved;
                _localSettings = saved;
                _logger.Debug("Настройки загружены: MonitorSizeInches={V}", _globalSettings.MonitorSizeInches);
            }
        }

        // ── BaseModule ────────────────────────────────────────────────────

        public override Control? CreateView()
        {
            _viewModel ??= CreateAndInitViewModel();
            return new TextEditorView(_undoStack) { DataContext = _viewModel };
        }

        public override object? GetCustomData()
        {
            if (_viewModel?.DocumentViewModel is null) return null;
            DeltaCachePayload payload = _serializer.BuildDeltaPayload(
                _viewModel.DocumentViewModel.Document, _lastDeltaPayload);
            _lastDeltaPayload = payload;

            string documentJson = _serializer.Serialize(_viewModel.DocumentViewModel.Document);
            string localSettingsJson = System.Text.Json.JsonSerializer.Serialize(_localSettings);

            var envelope = new { v = 2, doc = documentJson, local = localSettingsJson };
            return System.Text.Json.JsonSerializer.Serialize(envelope);
        }

        public override void SetCustomData(object? data)
        {
            _viewModel ??= CreateAndInitViewModel();

            string? raw = data switch
            {
                string s when !string.IsNullOrWhiteSpace(s) => s,
                byte[] b when b.Length > 0 => System.Text.Encoding.UTF8.GetString(b),
                _ => null
            };

            if (raw is not null)
            {
                try
                {
                    using var envelope = System.Text.Json.JsonDocument.Parse(raw);
                    var root = envelope.RootElement;

                    if (root.TryGetProperty("v", out var ver) && ver.GetInt32() == 2
                        && root.TryGetProperty("doc", out var docProp)
                        && root.TryGetProperty("local", out var localProp))
                    {
                        string docJson = docProp.GetString() ?? string.Empty;
                        string localJson = localProp.GetString() ?? string.Empty;

                        if (!string.IsNullOrWhiteSpace(localJson))
                        {
                            var savedLocal = System.Text.Json.JsonSerializer.Deserialize<TextEditorSettings>(localJson);
                            if (savedLocal is not null)
                            {
                                _localSettings = savedLocal;
                                _logger.Debug("Локальные настройки восстановлены: MonitorSizeInches={V}", _localSettings.MonitorSizeInches);
                            }
                        }

                        DocumentModel? doc = _serializer.Deserialize(docJson);
                        if (doc is not null)
                        {
                            _viewModel.LoadDocument(doc, _localSettings);
                            _logger.Debug("Документ загружен (v2), title={Title}", doc.Title);
                            return;
                        }
                    }
                    else
                    {
                        DocumentModel? doc = _serializer.Deserialize(raw);
                        if (doc is not null)
                        {
                            _viewModel.LoadDocument(doc, _localSettings);
                            _logger.Debug("Документ загружен (legacy), title={Title}", doc.Title);
                            return;
                        }
                    }
                    _logger.Warning("Deserialize вернул null");
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Ошибка десериализации");
                }
            }

            _viewModel.LoadNewDocument(_localSettings);
        }

        // ── IConfigurableModule ───────────────────────────────────────────

        public string SettingsTitle => TextEditorStrings.DisplayName;
        public Type SettingsType => typeof(TextEditorSettings);

        public object GetDefaultSettings() => _hardcodedDefaults;

        public object GetSettings() => _globalSettingsVm?.GetSettings() ?? _globalSettings;

        public object GetLocalSettings() => _localSettingsVm?.GetSettings() ?? _localSettings;

        public void ApplySettings(object settings)
        {
            if (settings is not TextEditorSettings s) return;
            _logger.Debug("ApplySettings (global): MonitorSizeInches={V}", s.MonitorSizeInches);
            _globalSettings = s;
            _viewModel?.ApplySettings(s);
            _settingsService.SaveModuleSettings(moduleType, s);
            _settingsService.Save();
        }

        public void ApplyLocalSettings(object settings)
        {
            if (settings is not TextEditorSettings s) return;
            _logger.Debug("ApplyLocalSettings: MonitorSizeInches={V}", s.MonitorSizeInches);
            _localSettings = s;
            _viewModel?.ApplySettings(s);
        }

        /// <summary>
        /// Применить текущие глобальные UI-значения к локальной VM.
        /// Обновляет GlobalValue (ориентир для кнопки сброса) и Value (текущее значение).
        /// </summary>
        public void ApplyGlobalToLocal()
        {
            if (_globalSettingsVm is null || _localSettingsVm is null) return;

            var g = _globalSettingsVm;
            var l = _localSettingsVm;

            l.FontFamily.GlobalValue = g.FontFamily.Value;
            l.FontFamily.Value = g.FontFamily.Value;
            l.FontSize.GlobalValue = g.FontSize.Value;
            l.FontSize.Value = g.FontSize.Value;
            l.SpellCheckEnabled.GlobalValue = g.SpellCheckEnabled.Value;
            l.SpellCheckEnabled.Value = g.SpellCheckEnabled.Value;
            l.DefaultLanguage.GlobalValue = g.DefaultLanguage.Value;
            l.DefaultLanguage.Value = g.DefaultLanguage.Value;
            l.ShowSpellErrors.GlobalValue = g.ShowSpellErrors.Value;
            l.ShowSpellErrors.Value = g.ShowSpellErrors.Value;
            l.AutoReplaceEnabled.GlobalValue = g.AutoReplaceEnabled.Value;
            l.AutoReplaceEnabled.Value = g.AutoReplaceEnabled.Value;
            l.ShowRuler.GlobalValue = g.ShowRuler.Value;
            l.ShowRuler.Value = g.ShowRuler.Value;
            l.ShowFormattingMarks.GlobalValue = g.ShowFormattingMarks.Value;
            l.ShowFormattingMarks.Value = g.ShowFormattingMarks.Value;
            l.DefaultViewMode.GlobalValue = g.DefaultViewMode.Value;
            l.DefaultViewMode.Value = g.DefaultViewMode.Value;
            l.DefaultZoom.GlobalValue = g.DefaultZoom.Value;
            l.DefaultZoom.Value = g.DefaultZoom.Value;
            l.AutoSaveIntervalSeconds.GlobalValue = g.AutoSaveIntervalSeconds.Value;
            l.AutoSaveIntervalSeconds.Value = g.AutoSaveIntervalSeconds.Value;
            l.MonitorSizeInches.GlobalValue = g.MonitorSizeInches.Value;
            l.MonitorSizeInches.Value = g.MonitorSizeInches.Value;

            _logger.Debug("ApplyGlobalToLocal completed");
        }

        /// <summary>
        /// Сохранить текущие локальные UI-значения как глобальные.
        /// Обновляет GlobalValue в локальной VM чтобы кнопки сброса правильно реагировали.
        /// </summary>
        public void PromoteLocalToGlobal()
        {
            if (_localSettingsVm is null) return;

            var settings = _localSettingsVm.GetSettings();
            _globalSettings = settings;
            _settingsService.SaveModuleSettings(moduleType, settings);
            _settingsService.Save();

            // Обновляем GlobalValue в локальной VM — теперь кнопки сброса до глобального скроются
            _localSettingsVm.FontFamily.PromoteToGlobal();
            _localSettingsVm.FontSize.PromoteToGlobal();
            _localSettingsVm.SpellCheckEnabled.PromoteToGlobal();
            _localSettingsVm.DefaultLanguage.PromoteToGlobal();
            _localSettingsVm.ShowSpellErrors.PromoteToGlobal();
            _localSettingsVm.AutoReplaceEnabled.PromoteToGlobal();
            _localSettingsVm.ShowRuler.PromoteToGlobal();
            _localSettingsVm.ShowFormattingMarks.PromoteToGlobal();
            _localSettingsVm.DefaultViewMode.PromoteToGlobal();
            _localSettingsVm.DefaultZoom.PromoteToGlobal();
            _localSettingsVm.AutoSaveIntervalSeconds.PromoteToGlobal();
            _localSettingsVm.MonitorSizeInches.PromoteToGlobal();

            // Также обновляем глобальную VM если открыта
            if (_globalSettingsVm is not null)
            {
                _globalSettingsVm.FontFamily.Value = settings.FontFamily;
                _globalSettingsVm.FontSize.Value = settings.FontSize;
                _globalSettingsVm.SpellCheckEnabled.Value = settings.SpellCheckEnabled;
                _globalSettingsVm.DefaultLanguage.Value = settings.DefaultLanguage;
                _globalSettingsVm.ShowSpellErrors.Value = settings.ShowSpellErrors;
                _globalSettingsVm.AutoReplaceEnabled.Value = settings.AutoReplaceEnabled;
                _globalSettingsVm.ShowRuler.Value = settings.ShowRuler;
                _globalSettingsVm.ShowFormattingMarks.Value = settings.ShowFormattingMarks;
                _globalSettingsVm.DefaultViewMode.Value = settings.DefaultViewMode;
                _globalSettingsVm.DefaultZoom.Value = settings.DefaultZoom;
                _globalSettingsVm.AutoSaveIntervalSeconds.Value = settings.AutoSaveIntervalSeconds;
                _globalSettingsVm.MonitorSizeInches.Value = settings.MonitorSizeInches;
            }

            _logger.Debug("PromoteLocalToGlobal completed");
        }

        public void ResetSettingsToDefaults()
        {
            if (_globalSettingsVm is null) return;
            _globalSettingsVm.FontFamily.ResetToHardcoded();
            _globalSettingsVm.FontSize.ResetToHardcoded();
            _globalSettingsVm.SpellCheckEnabled.ResetToHardcoded();
            _globalSettingsVm.DefaultLanguage.ResetToHardcoded();
            _globalSettingsVm.ShowSpellErrors.ResetToHardcoded();
            _globalSettingsVm.AutoReplaceEnabled.ResetToHardcoded();
            _globalSettingsVm.ShowRuler.ResetToHardcoded();
            _globalSettingsVm.ShowFormattingMarks.ResetToHardcoded();
            _globalSettingsVm.DefaultViewMode.ResetToHardcoded();
            _globalSettingsVm.DefaultZoom.ResetToHardcoded();
            _globalSettingsVm.AutoSaveIntervalSeconds.ResetToHardcoded();
            _globalSettingsVm.MonitorSizeInches.ResetToHardcoded();
            _logger.Debug("Global settings reset to hardcoded defaults");
        }

        public void ResetLocalSettingsToGlobal()
        {
            if (_localSettingsVm is null) return;

            // Если глобальная VM открыта — берём актуальные значения из неё
            if (_globalSettingsVm is not null)
            {
                _localSettingsVm.FontFamily.GlobalValue = _globalSettingsVm.FontFamily.Value;
                _localSettingsVm.FontSize.GlobalValue = _globalSettingsVm.FontSize.Value;
                _localSettingsVm.SpellCheckEnabled.GlobalValue = _globalSettingsVm.SpellCheckEnabled.Value;
                _localSettingsVm.DefaultLanguage.GlobalValue = _globalSettingsVm.DefaultLanguage.Value;
                _localSettingsVm.ShowSpellErrors.GlobalValue = _globalSettingsVm.ShowSpellErrors.Value;
                _localSettingsVm.AutoReplaceEnabled.GlobalValue = _globalSettingsVm.AutoReplaceEnabled.Value;
                _localSettingsVm.ShowRuler.GlobalValue = _globalSettingsVm.ShowRuler.Value;
                _localSettingsVm.ShowFormattingMarks.GlobalValue = _globalSettingsVm.ShowFormattingMarks.Value;
                _localSettingsVm.DefaultViewMode.GlobalValue = _globalSettingsVm.DefaultViewMode.Value;
                _localSettingsVm.DefaultZoom.GlobalValue = _globalSettingsVm.DefaultZoom.Value;
                _localSettingsVm.AutoSaveIntervalSeconds.GlobalValue = _globalSettingsVm.AutoSaveIntervalSeconds.Value;
                _localSettingsVm.MonitorSizeInches.GlobalValue = _globalSettingsVm.MonitorSizeInches.Value;
            }

            _localSettingsVm.FontFamily.ResetToGlobal();
            _localSettingsVm.FontSize.ResetToGlobal();
            _localSettingsVm.SpellCheckEnabled.ResetToGlobal();
            _localSettingsVm.DefaultLanguage.ResetToGlobal();
            _localSettingsVm.ShowSpellErrors.ResetToGlobal();
            _localSettingsVm.AutoReplaceEnabled.ResetToGlobal();
            _localSettingsVm.ShowRuler.ResetToGlobal();
            _localSettingsVm.ShowFormattingMarks.ResetToGlobal();
            _localSettingsVm.DefaultViewMode.ResetToGlobal();
            _localSettingsVm.DefaultZoom.ResetToGlobal();
            _localSettingsVm.AutoSaveIntervalSeconds.ResetToGlobal();
            _localSettingsVm.MonitorSizeInches.ResetToGlobal();
            _logger.Debug("Local settings reset to global values");
        }

        public void ResetLocalSettingsToDefaults()
        {
            if (_localSettingsVm is null) return;
            _localSettingsVm.FontFamily.ResetToHardcoded();
            _localSettingsVm.FontSize.ResetToHardcoded();
            _localSettingsVm.SpellCheckEnabled.ResetToHardcoded();
            _localSettingsVm.DefaultLanguage.ResetToHardcoded();
            _localSettingsVm.ShowSpellErrors.ResetToHardcoded();
            _localSettingsVm.AutoReplaceEnabled.ResetToHardcoded();
            _localSettingsVm.ShowRuler.ResetToHardcoded();
            _localSettingsVm.ShowFormattingMarks.ResetToHardcoded();
            _localSettingsVm.DefaultViewMode.ResetToHardcoded();
            _localSettingsVm.DefaultZoom.ResetToHardcoded();
            _localSettingsVm.AutoSaveIntervalSeconds.ResetToHardcoded();
            _localSettingsVm.MonitorSizeInches.ResetToHardcoded();
            _logger.Debug("Local settings reset to hardcoded defaults");
        }

        public Control CreateSettingsView()
        {
            _globalSettingsVm = new TextEditorSettingsViewModel(_hardcodedDefaults, _globalSettings);
            return new TextEditorSettingsView { DataContext = _globalSettingsVm };
        }

        public Control CreateLocalSettingsView()
        {
            var globalSettings = _settingsService.GetModuleSettings<TextEditorSettings>(moduleType)
                                 ?? _hardcodedDefaults;
            _localSettingsVm = new TextEditorSettingsViewModel(
                _hardcodedDefaults, globalSettings, _localSettings);
            return new TextEditorSettingsView { DataContext = _localSettingsVm };
        }

        // ── Жизненный цикл ────────────────────────────────────────────────

        public override void Initialize()
        {
            base.Initialize();
            _viewModel ??= CreateAndInitViewModel();
            _logger.Debug("Инициализирован");
        }

        public override void Dispose()
        {
            _viewModel?.Dispose();
            base.Dispose();
        }

        private TextEditorViewModel CreateAndInitViewModel()
        {
            var vm = new TextEditorViewModel();
            vm.LoadNewDocument(_localSettings);
            return vm;
        }
    }
}