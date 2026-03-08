using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Serilog;
using Writersword.Core.Interfaces.Modules;
using Writersword.Modules.Common;
using Writersword.Modules.TextEditor.Models;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Services;
using Writersword.Modules.TextEditor.ViewModels;
using Writersword.Modules.TextEditor.Views;
using Writersword.Modules.TextEditor.Views.Settings;

namespace Writersword.Modules.TextEditor
{
    /// <summary>
    /// Метаданные модуля TextEditor.
    /// </summary>
    internal sealed class TextEditorModuleMetadata : IModuleMetadata
    {
        public string ModuleType => "TextEditor";
        public string DisplayName => "Text Editor";
        public string Description => "Main text editing workspace";
    }

    /// <summary>
    /// Модуль текстового редактора.
    /// Документ сериализуется в JSON и возвращается через GetCustomData().
    /// Инфраструктура сохраняет его в ZIP проекта через SetCustomData() при загрузке.
    /// </summary>
    public sealed class TextEditorModule : BaseModule, IConfigurableModule, IUndoableModule
    {
        private static readonly ILogger _log = Log.ForContext<TextEditorModule>();

        private readonly DocumentSerializer _serializer;
        private readonly DeltaHashService _hashService;
        private readonly ChunkManager _chunkManager;

        private TextEditorViewModel? _viewModel;
        private TextEditorSettings _settings = new();
        private DeltaCachePayload? _lastDeltaPayload;

        // --- BaseModule ---

        /// <summary>Строковый идентификатор типа модуля.</summary>
        public override string moduleType => "TextEditor";

        /// <summary>ViewModel модуля (используется Dock.Avalonia для заголовка вкладки).</summary>
        public override object? ViewModel => _viewModel;

        /// <summary>Метаданные модуля.</summary>
        public override IModuleMetadata Metadata { get; } = new TextEditorModuleMetadata();

        /// <summary>Дельта-кеш включён.</summary>
        public override bool SupportsDeltaComparison => true;

        public TextEditorModule()
        {
            _hashService = new DeltaHashService();
            _chunkManager = new ChunkManager(_hashService);
            _serializer = new DocumentSerializer(_hashService, _chunkManager);

            Title = "Text Editor";
        }

        /// <summary>
        /// Создаёт View модуля.
        /// ViewModel создаётся здесь если ещё не существует.
        /// </summary>
        public override Control? CreateView()
        {
            _viewModel ??= CreateAndInitViewModel();
            return new TextEditorView { DataContext = _viewModel };
        }

        /// <summary>
        /// Сериализует документ в JSON и возвращает как object.
        /// Вызывается инфраструктурой при сохранении проекта.
        /// При SupportsDeltaComparison = true возвращает дельта-payload
        /// вместо полного документа если есть изменения.
        /// </summary>
        public override object? GetCustomData()
        {
            if (_viewModel?.DocumentViewModel is null) return null;

            DeltaCachePayload payload = _serializer.BuildDeltaPayload(
                _viewModel.DocumentViewModel.Document,
                _lastDeltaPayload);

            _lastDeltaPayload = payload;

            // Возвращаем полный документ — инфраструктура сама решает что сохранять.
            return _serializer.Serialize(_viewModel.DocumentViewModel.Document);
        }

        /// <summary>
        /// Получает сохранённые данные из инфраструктуры и загружает документ.
        /// Инфраструктура может передать string, byte[] или данные в неизвестном формате.
        /// При любой ошибке десериализации создаём новый документ.
        /// </summary>
        public override void SetCustomData(object? data)
        {
            _viewModel ??= CreateAndInitViewModel();

            string? json = data switch
            {
                string s when !string.IsNullOrWhiteSpace(s) => s,
                byte[] bytes when bytes.Length > 0 => System.Text.Encoding.UTF8.GetString(bytes),
                _ => null
            };

            if (json is not null)
            {
                try
                {
                    DocumentModel? doc = _serializer.Deserialize(json);
                    if (doc is not null)
                    {
                        _viewModel.LoadDocument(doc, _settings);
                        _log.Debug("TextEditorModule: document loaded, title={Title}", doc.Title);
                        return;
                    }
                    _log.Warning("TextEditorModule: deserialize returned null, creating new document");
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "TextEditorModule: failed to deserialize, data type={Type}, length={Len}, creating new document",
                        data?.GetType().Name ?? "null",
                        json.Length);
                }
            }
            else
            {
                _log.Debug("TextEditorModule: no data to restore (type={Type}), creating new document",
                    data?.GetType().Name ?? "null");
            }

            _viewModel.LoadNewDocument(_settings);
        }

        // --- IConfigurableModule ---

        /// <summary>Название раздела в диалоге настроек.</summary>
        public string SettingsTitle => "Text Editor";

        /// <summary>Тип объекта настроек для десериализации инфраструктурой.</summary>
        public Type SettingsType => typeof(TextEditorSettings);

        /// <summary>Возвращает текущие глобальные настройки модуля.</summary>
        public object GetSettings() => _settings;

        /// <summary>Применяет глобальные настройки. Вызывается после сохранения в диалоге.</summary>
        public void ApplySettings(object settings)
        {
            if (settings is TextEditorSettings s)
            {
                _settings = s;
                _log.Debug("TextEditorModule: global settings applied");
            }
        }

        /// <summary>Создаёт View глобальных настроек для вкладки в диалоге Settings.</summary>
        public Control CreateSettingsView()
        {
            return new TextEditorSettingsView
            {
                DataContext = new TextEditorSettingsViewModel(_settings)
            };
        }

        /// <summary>
        /// Возвращает локальные настройки проекта.
        /// Сейчас локальные настройки хранятся внутри DocumentModel.CanvasSettings
        /// и возвращаются как часть GetCustomData(). Здесь возвращаем null.
        /// </summary>
        public object GetLocalSettings() => _settings;

        /// <summary>Применяет локальные настройки проекта.</summary>
        public void ApplyLocalSettings(object settings)
        {
            if (settings is TextEditorSettings s)
            {
                _settings = s;
                _log.Debug("TextEditorModule: local settings applied");
            }
        }

        /// <summary>
        /// Создаёт View локальных настроек проекта.
        /// Использует тот же View что и глобальные настройки.
        /// </summary>
        public Control CreateLocalSettingsView()
        {
            return new TextEditorSettingsView
            {
                DataContext = new TextEditorSettingsViewModel(_settings)
            };
        }

        // --- IUndoableModule ---

        public bool CanUndo => false;
        public bool CanRedo => false;

        public string? UndoDescription => null;
        public string? RedoDescription => null;

        public void Undo() => _viewModel?.Undo();
        public void Redo() => _viewModel?.Redo();

        /// <summary>
        /// Добавляет команду в стек и выполняет её.
        /// UndoRedoStack подключается отдельным этапом.
        /// </summary>
        public void PushCommand(IUndoableCommand command)
        {
            command.Execute();
        }

        /// <summary>
        /// Жесты которые перехватывает модуль.
        /// Блокируем стандартные текстовые Ctrl+Z/Y чтобы не дублировались
        /// с собственным UndoRedoStack.
        /// </summary>
        public IReadOnlyList<KeyGesture> BlockedNativeGestures { get; } = new[]
        {
            new KeyGesture(Key.Z, KeyModifiers.Control),
            new KeyGesture(Key.Y, KeyModifiers.Control)
        };

        // --- Жизненный цикл ---

        public override void Initialize()
        {
            base.Initialize();
            _viewModel ??= CreateAndInitViewModel();
            _log.Debug("TextEditorModule: initialized");
        }

        public override void Dispose()
        {
            _viewModel?.Dispose();
            base.Dispose();
        }

        // --- Вспомогательные методы ---

        private TextEditorViewModel CreateAndInitViewModel()
        {
            var vm = new TextEditorViewModel();
            vm.LoadNewDocument(_settings);
            return vm;
        }
    }
}