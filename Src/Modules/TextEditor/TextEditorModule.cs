using Writersword.Core.Services;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using System;
using System.Collections.Generic;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Interfaces.Services.UI;
using Writersword.Core.Models.Settings;
using Writersword.Modules.Common;
using Writersword.Modules.TextEditor.Commands;
using Writersword.Modules.TextEditor.HotKeys;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Page;
using Writersword.Modules.TextEditor.Models.Styles;
using Writersword.Modules.TextEditor.Services;
using Writersword.Modules.TextEditor.ViewModels;
using Writersword.Modules.TextEditor.Views;
using Writersword.Modules.TextEditor.Views.Settings;
using Writersword.Core.Interfaces.Services.Input;
using Writersword.Core.Interfaces.Services.Storage;
using Writersword.Modules.TextEditor.Models.Settings;
using Writersword.Modules.TextEditor.Resources;
using Writersword.Modules.TextEditor.Document;

namespace Writersword.Modules.TextEditor
{
    internal sealed class TextEditorModuleMetadata : IModuleMetadata
    {
        public string ModuleType => "TextEditor";
        public string DisplayName => TextEditorStrings.DisplayName;
        public string Description => TextEditorStrings.Description;
    }

    public sealed class TextEditorModule : BaseModule, IConfigurableModule, IUndoableModule, IHotKeyProvider
    {
        private static readonly ILogger _logger = Log.ForContext<TextEditorModule>();

        private readonly DocumentSerializer _serializer;
        private readonly DeltaHashService _hashService;
        private readonly ChunkManager _chunkManager;
        // Стек для операций форматирования (BeginEdit/CommitEdit) — каждый снапшот
        // хранит полный JSON документа. Ограничен 20 записями до полного перехода
        // на операционную систему команд.
        private readonly UndoRedoStack _undoStack = new(20);

        // Лёгкий стек для операций набора текста.
        // Каждая запись хранит только позицию и текст — не полный JSON документа.
        // 1000 записей ≈ несколько КБ вместо гигабайт.
        private readonly Writersword.Modules.TextEditor.Commands.TextUndoRedoStack _textUndoStack = new(1000);
        private readonly ISettingsService _settingsService;
        private readonly IPrintService _printService;
        private readonly IHotKeyService? _hotKeyService;

        private static readonly TextEditorSettings _hardcodedDefaults = new();

        private TextEditorViewModel? _viewModel;
        private TextEditorSettingsViewModel? _globalSettingsVm;
        private TextEditorSettingsViewModel? _localSettingsVm;
        private TextEditorView? _lastCreatedView;

        private TextEditorSettings _globalSettings = new();
        private TextEditorSettings _localSettings = new();

        private DeltaCachePayload? _lastDeltaPayload;
        /// <summary>
        /// Сырые данные загруженные из файла (нормализованные без caret).
        /// Используется для сравнения в HasUnsavedChanges — пока нет изменений
        /// GetCustomData должен возвращать точно то же что было загружено.
        /// </summary>
        private string? _baselineCustomData;

        // Кеш сессионных данных (para, charIdx, scrollY).
        // Обновляется на UI-потоке через UpdateSessionCache() всякий раз когда
        // меняется позиция каретки или скролл. GetSessionData() читает его безопасно
        // с любого потока — никакого обращения к visual tree не нужно.
        private string? _cachedSessionData;

        public override string moduleType => "TextEditor";
        public override object? ViewModel => _viewModel;
        public override IModuleMetadata Metadata { get; } = new TextEditorModuleMetadata();
        public override bool SupportsDeltaComparison => true;

        // ── IHotKeyDescriptor ─────────────────────────────────────────────

        /// <summary>
        /// Returns static list of hotkey definitions for this module.
        /// Called once at application startup by ModuleFactory.
        /// </summary>
        public IReadOnlyList<HotKey> GetHotKeys()
            => new TextEditorHotKeyDescriptor().GetHotKeys();

        // ── IUndoableModule ───────────────────────────────────────────────

        public bool CanUndo => _undoStack.CanUndo;
        public bool CanRedo => _undoStack.CanRedo;
        public string? UndoDescription => _undoStack.UndoDescription;
        public string? RedoDescription => _undoStack.RedoDescription;

        public void Undo()
        {
            _undoStack.Undo();
            _viewModel?.DocumentViewModel?.FireCursorContextChanged();
        }

        public void Redo()
        {
            _undoStack.Redo();
            _viewModel?.DocumentViewModel?.FireCursorContextChanged();
        }

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
            _settingsService = CoreServices.GetRequiredService<ISettingsService>();
            _printService = CoreServices.GetRequiredService<IPrintService>();
            _hotKeyService = CoreServices.GetService<IHotKeyService>();
            Title = "Text Editor";

            var saved = _settingsService.GetModuleSettings<TextEditorSettings>(moduleType);
            if (saved is not null)
            {
                _globalSettings = saved;
                _localSettings = saved;
                _logger.Debug("Settings loaded: MonitorSizeInches={V}", _globalSettings.MonitorSizeInches);
            }
        }

        // ── BaseModule ────────────────────────────────────────────────────

        public override Control? CreateView()
        {
            _viewModel ??= CreateAndInitViewModel();
            var view = new TextEditorView(_undoStack) { DataContext = _viewModel };
            _lastCreatedView = view;

            // Передаём сервис хоткеев в канвас после создания View.
            if (_hotKeyService is not null)
                BindCanvasHotKeyService(view);

            BindCanvasTextUndoStack(view);

            // Передаём карту шрифтов по скриптам в канвас при создании View.
            ApplyScriptFontMapToCanvas();

            // Если SetSessionData был вызван до CreateView (стандартный сценарий DockFactory),
            // восстанавливаем позицию каретки сейчас — view уже существует.
            if (_cachedSessionData is not null)
                RestoreCaretFromCache();

            return view;
        }

        private void BindCanvasHotKeyService(TextEditorView view)
        {
            var canvas = view.FindControl<DocumentCanvas>("PageCanvas");
            if (canvas is null)
            {
                _logger.Warning("BindCanvasHotKeyService: PageCanvas not found");
                return;
            }

            canvas.SetHotKeyService(_hotKeyService!);
            _logger.Debug("HotKeyService bound to PageCanvas");
        }

        private void BindCanvasTextUndoStack(TextEditorView view)
        {
            var canvas = view.FindControl<DocumentCanvas>("PageCanvas");
            if (canvas is null)
            {
                _logger.Warning("BindCanvasTextUndoStack: PageCanvas not found");
                return;
            }

            canvas.TextUndoStack = _textUndoStack;
            _logger.Debug("TextUndoStack bound to PageCanvas");
        }

        /// <summary>
        /// Передаёт текущую карту "скрипт → шрифт" в DocumentCanvas.
        /// Вызывается при создании View и при изменении настроек.
        /// </summary>
        private void ApplyScriptFontMapToCanvas()
        {
            var canvas = _lastCreatedView?.FindControl<DocumentCanvas>("PageCanvas");
            if (canvas is null) return;
            canvas.ScriptFontMap = _localSettings.ScriptFontMap;
        }



        public override object? GetCustomData()
        {
            // GetCustomData может вызываться с фонового потока (авто-сохранение).
            // Вся работа с моделью документа должна быть на UI-потоке, иначе
            // "Collection was modified" при одновременном Ctrl+Z или вводе текста.
            if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                return Avalonia.Threading.Dispatcher.UIThread.Invoke(GetCustomData);

            if (_viewModel?.DocumentViewModel is null)
            {
                // Логируем Warning чтобы зафиксировать факт возврата null.
                // Если это происходит при сохранении — данные TextEditor не попадут в файл.
                // ViewModel null означает что CreateView() не был вызван для этого модуля.
                _logger.Warning("GetCustomData: _viewModel or DocumentViewModel is null — returning null. " +
                    "Module type: {Type}", moduleType);
                return null;
            }

            try
            {
                DeltaCachePayload payload = _serializer.BuildDeltaPayload(
                    _viewModel.DocumentViewModel.Document, _lastDeltaPayload);

                // Если изменений нет и есть базовая линия — возвращаем её как есть.
                // Это гарантирует что HasUnsavedChanges вернёт false пока пользователь
                // ничего не менял (хеш совпадёт с файлом на диске).
                bool hasChanges = payload.ChangedChunks.Count > 0
                                  || payload.RemovedChunks.Count > 0
                                  || payload.ChangedAnnotations.Count > 0
                                  || payload.RemovedAnnotations.Count > 0;

                if (!hasChanges && _baselineCustomData is not null)
                {
                    _logger.Debug("GetCustomData: no changes, returning baseline (len={L})", _baselineCustomData.Length);
                    return _baselineCustomData;
                }

                _lastDeltaPayload = payload;

                string documentJson = _serializer.Serialize(_viewModel.DocumentViewModel.Document);

                // Удаляем из проекта файлы картинок, на которые в документе больше нет ссылок.
                try { CleanupUnusedImages(_viewModel.DocumentViewModel.Document); }
                catch (Exception cex) { _logger.Warning(cex, "CleanupUnusedImages failed"); }

                string localSettingsJson = System.Text.Json.JsonSerializer.Serialize(_localSettings);

                var envelope = new
                {
                    v = 2,
                    doc = documentJson,
                    local = localSettingsJson,
                };
                var result = System.Text.Json.JsonSerializer.Serialize(envelope);
                _baselineCustomData = null; // сбрасываем — теперь есть реальные изменения
                _logger.Debug("GetCustomData: serialized document, len={L}", result.Length);
                return result;
            }
            catch (Exception ex)
            {
                // Логируем Error с полным стектрейсом — это позволит найти причину при следующем возникновении.
                _logger.Error(ex, "GetCustomData: exception during serialization — returning null. Data will NOT be saved!");
                return null;
            }
        }

        // Сверяет файлы в TextEditor/Images с реально используемыми в документе именами
        // и удаляет лишние. Удаляются только файлы без ссылок — используемые не трогаются.
        private void CleanupUnusedImages(Writersword.Modules.TextEditor.Models.Document.DocumentModel document)
        {
            // Авто-очистка файлов картинок временно отключена: удаление конфликтовало
            // с восстановлением (recovery) и отменой — после удаления и сохранения картинка
            // терялась. Переработать так, чтобы учитывать снимки recovery и историю undo,
            // прежде чем что-либо удалять.
            _ = document;
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

                    int envelopeVersion = root.TryGetProperty("v", out var ver) ? ver.GetInt32() : 1;

                    if ((envelopeVersion == 2 || envelopeVersion == 3)
                        && root.TryGetProperty("doc", out var docProp)
                        && root.TryGetProperty("local", out var localProp))
                    {
                        string docJson = docProp.GetString() ?? string.Empty;
                        string localJson = localProp.GetString() ?? string.Empty;

                        if (!string.IsNullOrWhiteSpace(localJson))
                        {
                            var savedLocal = System.Text.Json.JsonSerializer
                                .Deserialize<TextEditorSettings>(localJson);
                            if (savedLocal is not null)
                            {
                                _localSettings = savedLocal;
                                _logger.Debug("Local settings restored: MonitorSizeInches={V}",
                                    _localSettings.MonitorSizeInches);
                            }
                        }

                        // Восстанавливаем позицию каретки из поля "caret" (версия 3+).
                        if (envelopeVersion >= 3
                            && root.TryGetProperty("caret", out var caretProp)
                            && caretProp.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            _cachedSessionData = caretProp.GetString();
                        }

                        DocumentModel? doc = _serializer.Deserialize(docJson);
                        if (doc is not null)
                        {
                            _viewModel.LoadDocument(doc, _localSettings);

                            // Инициализируем _lastDeltaPayload сразу после загрузки.
                            _lastDeltaPayload = _serializer.BuildDeltaPayload(doc, null);

                            // Строим baseline без caret — именно это GetCustomData будет
                            // возвращать пока нет изменений. Файл тоже перезапишется без caret
                            // при следующем сохранении → хеши совпадут.
                            _baselineCustomData = System.Text.Json.JsonSerializer.Serialize(new
                            {
                                v = 2,
                                doc = docJson,
                                local = localJson.Length > 0 ? localJson
                                        : System.Text.Json.JsonSerializer.Serialize(_localSettings),
                            });


                            // Каретку восстанавливаем ПОСЛЕ загрузки документа.
                            if (_cachedSessionData is not null)
                                RestoreCaretFromCache();

                            _logger.Debug("Document loaded (v{V}), title={Title}", envelopeVersion, doc.Title);
                            return;
                        }
                    }
                    else
                    {
                        DocumentModel? doc = _serializer.Deserialize(raw);
                        if (doc is not null)
                        {
                            _viewModel.LoadDocument(doc, _localSettings);
                            _lastDeltaPayload = _serializer.BuildDeltaPayload(doc, null);
                            _logger.Debug("Document loaded (legacy), title={Title}", doc.Title);
                            return;
                        }
                    }
                    _logger.Warning("Deserialize returned null");
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Deserialization error");
                }
            }

            _viewModel.LoadNewDocument(_localSettings);
        }

        public override object? GetSessionData()
        {
            // Вызывается в двух контекстах:
            //
            // 1. UI-поток (переключение вкладок, ручное сохранение) →
            //    берём свежее состояние из canvas и обновляем кеш.
            //
            // 2. Фоновый поток (autosave-таймер ModuleStateCollectorService) →
            //    canvas трогать нельзя, возвращаем последний кеш.
            //    Данные чуть устаревшие (позиция на момент последнего UI-вызова),
            //    но это приемлемо для autosave — точность до "последней вкладки".

            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                RefreshSessionCacheOnUIThread();
            else
                UpdateCachedZoomOnly();

            return _cachedSessionData;
        }

        /// <summary>
        /// Освежает в сессионном кэше ТОЛЬКО масштаб из DocumentViewModel.Zoom, не трогая канвас
        /// (каретку/скролл). Вызывается из GetSessionData на фоновом потоке (autosave-таймер):
        /// канвас оттуда трогать нельзя, но зум — простое свойство, читать его безопасно. Без
        /// этого фоновое сохранение писало бы устаревший масштаб (залипал старый, напр. 68%).
        /// </summary>
        private void UpdateCachedZoomOnly()
        {
            var dvm = _viewModel?.DocumentViewModel;
            if (dvm is null) return;
            double zoom = dvm.Zoom;

            try
            {
                int para = 0, ch = 0;
                double scroll = 0;
                if (_cachedSessionData is not null)
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(_cachedSessionData);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("para", out var p)) para = p.GetInt32();
                    if (root.TryGetProperty("ch", out var c)) ch = c.GetInt32();
                    if (root.TryGetProperty("scroll", out var s)) scroll = s.GetDouble();
                }

                _cachedSessionData = System.Text.Json.JsonSerializer.Serialize(new
                {
                    para,
                    ch,
                    scroll,
                    zoom
                });
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "UpdateCachedZoomOnly: failed");
            }
        }

        /// <summary>
        /// Восстанавливает позицию каретки из _cachedSessionData.
        /// Вызывается после загрузки документа — откладывается до Loaded.
        /// </summary>
        private void RestoreCaretFromCache()
        {
            if (_cachedSessionData is null) return;

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(_cachedSessionData);
                var root = doc.RootElement;

                int docParaIdx = root.TryGetProperty("para", out var p) ? p.GetInt32() : 0;
                int charIdx = root.TryGetProperty("ch", out var c) ? c.GetInt32() : 0;
                double scrollY = root.TryGetProperty("scroll", out var s) ? s.GetDouble() : 0;
                double zoom = root.TryGetProperty("zoom", out var z) ? z.GetDouble() : 0;

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    // Масштаб — часть состояния вида (SessionData), а не содержимого документа.
                    // Восстанавливаем его ДО каретки/скролла, иначе scroll-offset пересчитается с
                    // неправильным зумом. Актуальность кэша обеспечивает GetSessionData (см. ниже):
                    // на фоновом потоке он освежает зум из DocumentViewModel, поэтому залипания нет.
                    if (zoom > 0.01 && _viewModel?.DocumentViewModel is { } dvm)
                        dvm.Zoom = zoom;

                    var canvas = _lastCreatedView?.FindControl<DocumentCanvas>("PageCanvas");
                    canvas?.RestoreCaretState(docParaIdx, charIdx);

                    var sv = _lastCreatedView?
                        .FindControl<Avalonia.Controls.ScrollViewer>("DocumentScrollViewer");
                    if (sv is not null && scrollY > 0)
                        sv.Offset = new Avalonia.Vector(sv.Offset.X, scrollY);

                }, Avalonia.Threading.DispatcherPriority.Loaded);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "RestoreCaretFromCache: failed");
            }
        }

        /// <summary>
        /// Обновляет кеш сессионных данных. Должен вызываться только с UI-потока.
        /// </summary>
        private void RefreshSessionCacheOnUIThread()
        {
            var canvas = _lastCreatedView?.FindControl<DocumentCanvas>("PageCanvas");
            if (canvas is null) return;

            var (docParaIdx, charIdx, scrollY) = canvas.GetCaretState();
            double zoom = _viewModel?.DocumentViewModel?.Zoom ?? 1.0;

            _cachedSessionData = System.Text.Json.JsonSerializer.Serialize(new
            {
                para = docParaIdx,
                ch = charIdx,
                scroll = scrollY,
                zoom = zoom
            });
        }

        public override void SetSessionData(object? data)
        {
            // SetSessionData вызывается из DockFactory ДО CreateView —
            // _lastCreatedView ещё null, FindControl ничего не найдёт.
            // Просто кешируем данные; RestoreCaretFromCache() вызовется
            // из CreateView после того как view будет построен.
            string? raw = data switch
            {
                string s when !string.IsNullOrWhiteSpace(s) => s,
                byte[] b when b.Length > 0 => System.Text.Encoding.UTF8.GetString(b),
                _ => null
            };
            if (raw is not null)
                _cachedSessionData = raw;
        }

        // ── IHotKeyProvider ───────────────────────────────────────────────

        /// <summary>
        /// Routes a hotkey command to the appropriate target.
        /// Navigation and editing go to DocumentCanvas.
        /// Formatting, tools and export go to TextEditorViewModel.
        /// </summary>
        public void ExecuteHotKey(string id)
        {
            var canvas = _lastCreatedView?.FindControl<DocumentCanvas>("PageCanvas");

            if (canvas is not null)
            {
                switch (id)
                {
                    case "TextEditor.Navigation.Left":
                        canvas.ExecuteNavLeft(false); return;
                    case "TextEditor.Navigation.Right":
                        canvas.ExecuteNavRight(false); return;
                    case "TextEditor.Navigation.Up":
                        canvas.ExecuteNavUp(false); return;
                    case "TextEditor.Navigation.Down":
                        canvas.ExecuteNavDown(false); return;
                    case "TextEditor.Navigation.Home":
                        canvas.ExecuteHome(false, false); return;
                    case "TextEditor.Navigation.End":
                        canvas.ExecuteEnd(false, false); return;
                    case "TextEditor.Navigation.DocumentStart":
                        canvas.ExecuteHome(true, false); return;
                    case "TextEditor.Navigation.DocumentEnd":
                        canvas.ExecuteEnd(true, false); return;
                    case "TextEditor.Navigation.PageUp":
                        canvas.ExecuteNavUp(false); return;
                    case "TextEditor.Navigation.PageDown":
                        canvas.ExecuteNavDown(false); return;
                    case "TextEditor.Navigation.WordLeft":
                        canvas.ExecuteNavLeft(false); return;
                    case "TextEditor.Navigation.WordRight":
                        canvas.ExecuteNavRight(false); return;

                    case "TextEditor.Selection.Left":
                        canvas.ExecuteNavLeft(true); return;
                    case "TextEditor.Selection.Right":
                        canvas.ExecuteNavRight(true); return;
                    case "TextEditor.Selection.Up":
                        canvas.ExecuteNavUp(true); return;
                    case "TextEditor.Selection.Down":
                        canvas.ExecuteNavDown(true); return;
                    case "TextEditor.Selection.Home":
                        canvas.ExecuteHome(false, true); return;
                    case "TextEditor.Selection.End":
                        canvas.ExecuteEnd(false, true); return;
                    case "TextEditor.Selection.DocumentStart":
                        canvas.ExecuteHome(true, true); return;
                    case "TextEditor.Selection.DocumentEnd":
                        canvas.ExecuteEnd(true, true); return;
                    case "TextEditor.Selection.All":
                        canvas.ExecuteSelectAll(); return;
                    case "TextEditor.Selection.WordLeft":
                        canvas.ExecuteNavLeft(true); return;
                    case "TextEditor.Selection.WordRight":
                        canvas.ExecuteNavRight(true); return;

                    case "TextEditor.Editing.DeleteBack":
                        canvas.ExecuteDeleteBackSmart(); return;
                    case "TextEditor.Editing.DeleteForward":
                        canvas.ExecuteDeleteForwardSmart(); return;
                    case "TextEditor.Editing.NewParagraph":
                        canvas.ExecuteNewParagraphSmart(); return;

                    case "TextEditor.Clipboard.Copy":
                        canvas.ExecuteCopy(); return;
                    case "TextEditor.Clipboard.Cut":
                        canvas.ExecuteCut(); return;
                    case "TextEditor.Clipboard.Paste":
                        canvas.ExecutePaste(); return;

                    case "TextEditor.UndoRedo.Undo":
                        canvas.ExecuteUndo(); return;
                    case "TextEditor.UndoRedo.Redo":
                        canvas.ExecuteRedo(); return;
                }
            }

            if (_viewModel is not null)
            {
                switch (id)
                {
                    case "TextEditor.Editing.InsertPageBreak":
                        _viewModel.InsertPageBreak(); return;

                    case "TextEditor.Format.Bold":
                        _viewModel.ToggleBold(); return;
                    case "TextEditor.Format.Italic":
                        _viewModel.ToggleItalic(); return;
                    case "TextEditor.Format.Underline":
                        _viewModel.ToggleUnderline(); return;
                    case "TextEditor.Format.Strikethrough":
                        _viewModel.ToggleStrikethrough(); return;
                    case "TextEditor.Format.Superscript":
                        _viewModel.ToggleSuperscript(); return;
                    case "TextEditor.Format.Subscript":
                        _viewModel.ToggleSubscript(); return;
                    case "TextEditor.Format.AllCaps":
                        _viewModel.ToggleAllCaps(); return;
                    case "TextEditor.Format.SmallCaps":
                        _viewModel.ToggleSmallCaps(); return;
                    case "TextEditor.Format.ClearFormatting":
                        _viewModel.ClearFormatting(); return;
                    case "TextEditor.Format.IncreaseFontSize":
                        _viewModel.IncreaseFontSize(); return;
                    case "TextEditor.Format.DecreaseFontSize":
                        _viewModel.DecreaseFontSize(); return;

                    case "TextEditor.Format.AlignLeft":
                        _viewModel.SetAlignment(TextAlignment.Left); return;
                    case "TextEditor.Format.AlignCenter":
                        _viewModel.SetAlignment(TextAlignment.Center); return;
                    case "TextEditor.Format.AlignRight":
                        _viewModel.SetAlignment(TextAlignment.Right); return;
                    case "TextEditor.Format.AlignJustify":
                        _viewModel.SetAlignment(TextAlignment.Justify); return;
                    case "TextEditor.Format.IncreaseIndent":
                        _viewModel.IncreaseIndent(); return;
                    case "TextEditor.Format.DecreaseIndent":
                        _viewModel.DecreaseIndent(); return;

                    case "TextEditor.View.ZoomIn":
                        _viewModel.ZoomIn(); return;
                    case "TextEditor.View.ZoomOut":
                        _viewModel.ZoomOut(); return;
                    case "TextEditor.View.ZoomReset":
                        _viewModel.ZoomReset(); return;

                    case "TextEditor.Tools.Find":
                        _viewModel.OpenFind(); return;
                    case "TextEditor.Tools.FindReplace":
                        _viewModel.OpenFindReplace(); return;
                    case "TextEditor.Tools.SpellCheck":
                        _viewModel.RunSpellCheck(); return;
                    case "TextEditor.Tools.WordCount":
                        _viewModel.ShowWordCount(); return;

                    case "TextEditor.File.Print":
                        _viewModel.Print(); return;
                    case "TextEditor.File.ExportPdf":
                        _viewModel.ExportToPdf(); return;
                    case "TextEditor.File.ExportDocx":
                        _viewModel.ExportToDocx(); return;
                    case "TextEditor.File.ExportTxt":
                        _viewModel.ExportToTxt(); return;
                }
            }

            _logger.Warning("ExecuteHotKey: unhandled id={Id}", id);
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
            _localSettings = s;
            ApplyScriptFontMapToCanvas();
        }

        public void ApplyLocalSettings(object settings)
        {
            if (settings is not TextEditorSettings s) return;
            _logger.Debug("ApplyLocalSettings: MonitorSizeInches={V}", s.MonitorSizeInches);
            _localSettings = s;
            _viewModel?.ApplySettings(s);
            ApplyScriptFontMapToCanvas();
        }

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

        public void PromoteLocalToGlobal()
        {
            if (_localSettingsVm is null) return;
            var settings = _localSettingsVm.GetSettings();
            _globalSettings = settings;
            _settingsService.SaveModuleSettings(moduleType, settings);
            _settingsService.Save();
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

            // Регистрируем хоткеи если сервис доступен.
            if (_hotKeyService is not null)
            {
                _hotKeyService.RegisterFromDescriptor(this);
                _hotKeyService.BindExecutor(moduleType, this);
            }

            _logger.Debug("TextEditorModule initialized");
        }

        public override void Dispose()
        {
            if (_hotKeyService is not null)
                _hotKeyService.UnbindExecutor(moduleType);

            if (_viewModel is not null)
                _viewModel.PrintRequested -= OnPrintRequested;

            _viewModel?.Dispose();
            _viewModel = null;
            _lastCreatedView = null;
            Writersword.Modules.TextEditor.Rendering.SKTextRenderer.TrimFontCache();
            base.Dispose();
        }

        // ── Печать ────────────────────────────────────────────────────────

        private void OnPrintRequested(DocumentModel document, TextEditorPageSettings pageSettings)
        {
            _logger.Debug("OnPrintRequested: title={Title}", document.Title);

            Dispatcher.UIThread.InvokeAsync(async () =>
            {
                try
                {
                    var printDocument = new TextEditorPrintDocument(document);
                    var mainWindow = (Avalonia.Application.Current?.ApplicationLifetime
                    as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;

                    if (mainWindow is null)
                    {
                        _logger.Warning("OnPrintRequested: MainWindow is not available");
                        return;
                    }

                    await _printService.ShowPrintPreviewAsync(printDocument, mainWindow);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Print preview failed");
                }
            });
        }

        private TextEditorViewModel CreateAndInitViewModel()
        {
            var vm = new TextEditorViewModel();
            vm.PrintRequested += OnPrintRequested;
            vm.LoadNewDocument(_localSettings);
            return vm;
        }
    }
}