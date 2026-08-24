using Writersword.Core.Services;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Interfaces.WorkFlows;
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

    public sealed class TextEditorModule : BaseModule, IConfigurableModule, IUndoableModule, IHotKeyProvider, IStateSnapshotModule, IPreparedDataModule
    {
        private static readonly ILogger _logger = Log.ForContext<TextEditorModule>();

        private readonly DocumentSerializer _serializer;
        private readonly DeltaHashService _hashService;
        private readonly ChunkManager _chunkManager;
        // Стек для операций форматирования (BeginEdit/CommitEdit) — каждый снапшот
        // хранит полный JSON документа. Ограничен 200 записями до полного перехода
        // на операционную систему команд.
        private readonly UndoRedoStack _undoStack = new(200);

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
        /// Получал ли модуль данные документа из проекта.
        /// Ставится при успешном применении подготовленных данных. Пока флаг не
        /// поднят, модуль показывает пустой документ, созданный при инициализации,
        /// — и такой документ не имеет права попасть в сохранение.
        /// </summary>
        private bool _documentLoadedFromData;

        /// <summary>
        /// Был ли в документе хоть какой-то текст за время жизни модуля.
        /// Отличает «модуль не получил данные и показывает пустую болванку»
        /// от «пользователь сам стёр написанное»: второе сохранять нужно.
        /// </summary>
        private bool _documentEverHadContent;
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

        // Версия снимка данных документа. Инкрементируется на UI-потоке при каждом
        // снимке с изменениями и при загрузке данных в SetCustomData. Фоновая
        // сериализация обновляет _baselineCustomData только если её снимок всё ещё
        // актуален — устаревший результат не затирает более свежие данные.
        private long _snapshotVersion;

        // Синхронизация доступа к _baselineCustomData и _snapshotVersion:
        // базовая линия читается и пишется как с UI-потока, так и с фоновых
        // потоков сериализации снимков.
        private readonly object _baselineSync = new();

        /// <summary>
        /// Снимок состояния документа для двухфазного сбора данных.
        /// Document — глубокий клон живой модели, изолированный от правок пользователя.
        /// Version — версия снимка для защиты базовой линии от устаревших результатов.
        /// </summary>
        private sealed class DocumentStateSnapshot
        {
            public DocumentModel Document { get; }
            public string LocalSettingsJson { get; }
            public long Version { get; }

            public DocumentStateSnapshot(DocumentModel document, string localSettingsJson, long version)
            {
                Document = document;
                LocalSettingsJson = localSettingsJson;
                Version = version;
            }
        }

        /// <summary>
        /// Результат фоновой подготовки данных документа (PrepareCustomData).
        /// Содержит десериализованную модель и прединициализированную дельту —
        /// на UI-потоке остаётся только построение вьюмоделей (LoadDocument).
        /// BaselineJson заполнен только для конвертного формата (v2/v3),
        /// для legacy-формата он null — как и в прежнем SetCustomData.
        /// </summary>
        private sealed class PreparedDocumentData
        {
            public DocumentModel? Document { get; set; }
            public TextEditorSettings? LocalSettings { get; set; }
            public string? CachedSessionData { get; set; }
            public string? BaselineJson { get; set; }
            public DeltaCachePayload? InitialDelta { get; set; }
            public int EnvelopeVersion { get; set; }
        }

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

        /// <summary>
        /// Реакция на смену контекста: режим сравнения делает документ read-only.
        /// Защита стоит на уровне вьюмодели (форматирование, абзацы, вставка блоков)
        /// и канваса (ввод, удаление, Enter, буфер обмена, undo/redo, ручки таблиц) —
        /// листать и копировать можно, изменять данные нельзя.
        /// </summary>
        protected override void OnContextChanged(DocumentContext? context)
        {
            ApplyReadOnlyFromContext();
        }

        private void ApplyReadOnlyFromContext()
        {
            if (_viewModel is null) return;
            bool readOnly = Context?.IsInCompareMode == true;

            var docVm = _viewModel.DocumentViewModel;
            if (docVm is not null && docVm.IsReadOnly != readOnly)
                docVm.IsReadOnly = readOnly;

            // Линейки: запрет drag маркеров отступов, колонок и полей страницы.
            if (_viewModel.Ruler.IsReadOnly != readOnly)
                _viewModel.Ruler.IsReadOnly = readOnly;

            // Риббон: содержимое вкладок не принимает клики и ввод, слегка
            // приглушается, но продолжает отражать состояние под кареткой.
            if (_viewModel.Ribbon.IsEditingEnabled != !readOnly)
                _viewModel.Ribbon.IsEditingEnabled = !readOnly;
        }

        public override Control? CreateView()
        {
            _viewModel ??= CreateAndInitViewModel();
            var view = new TextEditorView(_undoStack) { DataContext = _viewModel };
            _lastCreatedView = view;

            // Контекст мог быть установлен до создания вьюмодели —
            // применяем read-only режима сравнения сейчас.
            ApplyReadOnlyFromContext();

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
            // Но полная JSON-сериализация документа на UI-потоке недопустимо дорога
            // для больших документов, поэтому сбор разделён на две фазы:
            // TakeStateSnapshot — быстрый снимок модели на UI-потоке (клон без JSON),
            // SerializeStateSnapshot — тяжёлая сериализация снимка на потоке вызывающего.
            if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                object? snapshot = Avalonia.Threading.Dispatcher.UIThread.Invoke(TakeStateSnapshot);
                return snapshot is null ? null : SerializeStateSnapshot(snapshot);
            }

            object? uiSnapshot = TakeStateSnapshot();
            return uiSnapshot is null ? null : SerializeStateSnapshot(uiSnapshot);
        }

        /// <summary>
        /// Фаза 1 сбора данных: быстрый снимок состояния документа на UI-потоке.
        /// Строит дельту, и если изменений нет — возвращает готовую базовую линию (строку).
        /// При наличии изменений возвращает DocumentStateSnapshot с глубоким клоном модели —
        /// его сериализация выполняется отдельно через SerializeStateSnapshot на любом потоке.
        /// </summary>
        public object? TakeStateSnapshot()
        {
            if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                return Avalonia.Threading.Dispatcher.UIThread.Invoke(TakeStateSnapshot);

            if (_viewModel?.DocumentViewModel is null)
            {
                // Логируем Warning чтобы зафиксировать факт возврата null.
                // Если это происходит при сохранении — данные TextEditor не попадут в файл.
                // ViewModel null означает что CreateView() не был вызван для этого модуля.
                _logger.Warning("TakeStateSnapshot: _viewModel or DocumentViewModel is null — returning null. " +
                    "Module type: {Type}", moduleType);
                return null;
            }

            // Модуль поднялся, но своих данных так и не получил, и пользователь
            // в нём ничего не написал. Отдавать пустой документ в этом состоянии
            // нельзя: он уходит в кеш и в ZIP как полноценные данные и затирает
            // сохранённый текст. Возврат null включает защиту в ProjectWorkflow —
            // значение модуля берётся из файла и остаётся нетронутым.
            // Появившийся текст запоминается навсегда: иначе намеренная очистка
            // документа не сохранялась бы. Пользователь написал абзац и стёр его —
            // документ снова пуст, но это уже его решение, а не потеря данных,
            // и защита ниже не должна такое отменять.
            if (!_documentEverHadContent && DocumentHasContent(_viewModel.DocumentViewModel.Document))
                _documentEverHadContent = true;

            if (!_documentLoadedFromData && !_documentEverHadContent)
            {
                _logger.Error("TakeStateSnapshot: module never received its data and the document was never " +
                    "filled — returning null so the saved version is preserved. Module type: {Type}", moduleType);
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
                                  || payload.RemovedAnnotations.Count > 0
                                  // Правки картинок и фигур живут вне чанков:
                                  // поворот, размер, обрезка, прозрачность, рамка,
                                  // обтекание и позиция видны только здесь.
                                  || payload.StructureChanged;

                if (!hasChanges)
                {
                    lock (_baselineSync)
                    {
                        if (_baselineCustomData is not null)
                            return _baselineCustomData;
                    }
                    // Базовая линия отсутствует (сериализация предыдущего снимка ещё
                    // не завершилась) — снимаем полный снимок, чтобы вызывающий код
                    // гарантированно получил актуальные данные.
                }

                _lastDeltaPayload = payload;

                // Удаляем из проекта файлы картинок, на которые в документе больше нет ссылок.
                try { CleanupUnusedImages(_viewModel.DocumentViewModel.Document); }
                catch (Exception cex) { _logger.Warning(cex, "CleanupUnusedImages failed"); }

                string localSettingsJson = System.Text.Json.JsonSerializer.Serialize(_localSettings);

                DocumentModel documentClone = DocumentCloner.Clone(_viewModel.DocumentViewModel.Document);

                long version;
                lock (_baselineSync)
                {
                    version = ++_snapshotVersion;
                    // Базовая линия устарела: содержимое изменилось, а новая строка
                    // появится только после сериализации снимка. До этого момента
                    // параллельные вызовы не должны получать старую строку как актуальную.
                    _baselineCustomData = null;
                }

                return new DocumentStateSnapshot(documentClone, localSettingsJson, version);
            }
            catch (Exception ex)
            {
                // Логируем Error с полным стектрейсом — это позволит найти причину при следующем возникновении.
                _logger.Error(ex, "TakeStateSnapshot: exception — returning null. Data will NOT be saved!");
                return null;
            }
        }

        /// <summary>
        /// Фаза 2 сбора данных: тяжёлая JSON-сериализация снимка.
        /// Можно вызывать с любого потока — снимок содержит клон модели,
        /// изолированный от правок пользователя на UI-потоке.
        /// Результат идентичен прежнему результату GetCustomData на момент снятия снимка.
        /// </summary>
        public object? SerializeStateSnapshot(object snapshot)
        {
            // Изменений не было — снимок уже является готовой строкой данных (базовой линией).
            if (snapshot is string baseline)
                return baseline;

            if (snapshot is not DocumentStateSnapshot documentSnapshot)
            {
                _logger.Error("SerializeStateSnapshot: unexpected snapshot type {Type} — returning null",
                    snapshot.GetType().FullName);
                return null;
            }

            try
            {
                string documentJson = _serializer.Serialize(documentSnapshot.Document);

                var envelope = new
                {
                    v = 2,
                    doc = documentJson,
                    local = documentSnapshot.LocalSettingsJson,
                };
                var result = System.Text.Json.JsonSerializer.Serialize(envelope);

                lock (_baselineSync)
                {
                    // Кешируем результат как новую базовую линию: пока дельта не покажет новых
                    // изменений, следующие опросы возвращают эту же строку без повторной
                    // сериализации всего документа. Обновляем только если за время сериализации
                    // не был снят более свежий снимок — устаревшая строка не затирает актуальную.
                    if (documentSnapshot.Version == _snapshotVersion)
                        _baselineCustomData = result;
                }

                return result;
            }
            catch (Exception ex)
            {
                // Логируем Error с полным стектрейсом — это позволит найти причину при следующем возникновении.
                _logger.Error(ex, "SerializeStateSnapshot: exception during serialization — returning null. Data will NOT be saved!");
                return null;
            }
        }

        // Сверяет файлы в TextEditor/Images с реально используемыми в документе именами
        // и удаляет лишние. Удаляются только файлы без ссылок — используемые не трогаются.
        private void CleanupUnusedImages(Writersword.Modules.TextEditor.Models.Document.DocumentModel document)
        {
            // Авто-очистка по таймеру намеренно не делается: она и раньше удаляла файл,
            // который возвращался по Ctrl+Z или из версии восстановления. Уборка живёт
            // отдельной командой CompactUnusedImages и запускается только пользователем.
            _ = document;
        }

        /// <summary>
        /// Имена файлов картинок, на которые ссылается документ.
        /// </summary>
        private static void CollectImageNames(
            Writersword.Modules.TextEditor.Models.Document.DocumentModel? document,
            HashSet<string> target)
        {
            if (document is null) return;

            void Walk(System.Collections.Generic.IEnumerable<Models.Document.BlockModel> blocks)
            {
                foreach (var block in blocks)
                {
                    switch (block)
                    {
                        case Models.Document.ImageBlock image
                            when !string.IsNullOrEmpty(image.ImageFileName):
                            target.Add(image.ImageFileName);
                            break;

                        // Картинка может лежать внутри ячейки таблицы или надписи —
                        // такие ссылки тоже живые.
                        case Models.Document.TableBlock table:
                            foreach (var cell in table.Cells)
                                Walk(cell.Paragraphs);
                            break;

                        case Models.Document.FloatingTextBlock floatingText:
                            Walk(floatingText.Paragraphs);
                            break;
                    }
                }
            }

            foreach (var section in document.Sections)
            {
                Walk(section.Blocks);
                Walk(section.FloatingObjects);
                Walk(section.InlineObjects);
            }
        }

        /// <summary>
        /// Собирает имена файлов картинок, которые ещё могут понадобиться:
        /// текущий документ, вся история отмены и повтора, версия из кеша
        /// восстановления и картинка в буфере обмена. Всё, чего здесь нет,
        /// вернуть уже неоткуда.
        /// </summary>
        private HashSet<string> CollectLiveImageReferences(string? projectPath)
        {
            var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            CollectImageNames(_viewModel?.DocumentViewModel?.Document, live);

            // История отмены и повтора: снимки хранят JSON документа до и после
            // операции, имена картинок вытаскиваются прямо из него.
            foreach (var command in _undoStack.AllCommands)
            {
                if (command is not Commands.DocumentSnapshotCommand snapshot) continue;
                foreach (var name in snapshot.ReferencedImageFiles)
                    if (!string.IsNullOrEmpty(name)) live.Add(name);
            }

            // Версия из кеша восстановления: пока она не принята и не отклонена,
            // её картинки нужны — иначе выбор версии в Compare даст дырки.
            if (!string.IsNullOrEmpty(projectPath))
            {
                try
                {
                    var cacheService = CoreServices
                        .GetService<Writersword.Core.Interfaces.Services.IZipCacheService>();
                    var cached = cacheService?.GetModuleCustomData(projectPath!, moduleType);
                    var cachedDocument = (PrepareCustomData(cached) as PreparedDocumentData)?.Document;
                    CollectImageNames(cachedDocument, live);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to read cache references — cleanup aborted for safety");
                    throw;
                }
            }

            var clipboardName = DocumentCanvas.ClipboardImageFileName;
            if (!string.IsNullOrEmpty(clipboardName)) live.Add(clipboardName!);

            return live;
        }

        /// <summary>
        /// Удаляет из проекта файлы картинок, на которые не осталось ни одной живой
        /// ссылки. Возвращает число удалённых файлов и освобождённый объём в байтах.
        /// Вызывается только по явной команде пользователя.
        /// </summary>
        public (int Removed, long FreedBytes) CompactUnusedImages(string? projectPath)
        {
            var ctx = CoreServices.GetService<ITabCollection>()?.ActiveTab?.Context;
            if (ctx is null)
            {
                _logger.Warning("CompactUnusedImages: no active document context");
                return (0, 0);
            }

            var live = CollectLiveImageReferences(projectPath);

            int removed = 0;
            long freed = 0;

            foreach (var path in ctx.GetFiles("TextEditor/Images").ToList())
            {
                string name = path.Substring(path.LastIndexOf('/') + 1);
                if (string.IsNullOrEmpty(name)) continue;
                if (live.Contains(name)) continue;

                long size = ctx.ReadFile(path)?.LongLength ?? 0;
                ctx.DeleteFile(path);
                removed++;
                freed += size;

                _logger.Debug("Unused image removed: {Name} ({Size} bytes)", name, size);
            }

            if (removed > 0)
            {
                // Архив переписывается — только после этого файл проекта реально
                // уменьшается, а не просто теряет запись в оглавлении.
                ctx.FlushStorage();
                _logger.Information("Compact: {Count} unused images removed, {Freed} bytes freed",
                    removed, freed);
            }

            return (removed, freed);
        }

        /// <summary>
        /// Имена картинок, на которые документ ссылается, но файлов в проекте нет.
        /// Пустой список — всё на месте.
        /// </summary>
        public IReadOnlyList<string> FindMissingImageFiles()
        {
            var ctx = CoreServices.GetService<ITabCollection>()?.ActiveTab?.Context;
            if (ctx is null || _viewModel?.DocumentViewModel?.Document is null)
                return System.Array.Empty<string>();

            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectImageNames(_viewModel.DocumentViewModel.Document, referenced);

            var missing = new List<string>();
            foreach (var name in referenced)
                if (!ctx.FileExists($"TextEditor/Images/{name}"))
                    missing.Add(name);

            return missing;
        }

        public override void SetCustomData(object? data)
        {
            // Синхронный путь: подготовка и применение на текущем потоке.
            // DockFactory при отложенном прикреплении вызывает PrepareCustomData
            // на фоновом потоке и ApplyPreparedCustomData на UI-потоке раздельно —
            // тяжёлая десериализация документа не блокирует интерфейс.
            ApplyPreparedCustomData(PrepareCustomData(data));
        }

        /// <summary>
        /// Фаза 1 восстановления: парсинг конверта, десериализация документа и расчёт
        /// начальной дельты. Можно вызывать с любого потока — модель ещё не привязана
        /// к вьюмоделям, гонок с UI нет. Возвращает null если данные пусты или нечитаемы.
        /// </summary>
        public object? PrepareCustomData(object? data)
        {
            string? raw = data switch
            {
                string s when !string.IsNullOrWhiteSpace(s) => s,
                byte[] b when b.Length > 0 => System.Text.Encoding.UTF8.GetString(b),
                _ => null
            };

            if (raw is null)
                return null;

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

                    var prepared = new PreparedDocumentData { EnvelopeVersion = envelopeVersion };

                    if (!string.IsNullOrWhiteSpace(localJson))
                    {
                        var savedLocal = System.Text.Json.JsonSerializer
                            .Deserialize<TextEditorSettings>(localJson);
                        if (savedLocal is not null)
                            prepared.LocalSettings = savedLocal;
                    }

                    // Позиция каретки из поля "caret" (версия 3+).
                    if (envelopeVersion >= 3
                        && root.TryGetProperty("caret", out var caretProp)
                        && caretProp.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        prepared.CachedSessionData = caretProp.GetString();
                    }

                    DocumentModel? doc = _serializer.Deserialize(docJson);
                    if (doc is not null)
                    {
                        prepared.Document = doc;

                        // Начальная дельта считается здесь же: хеширование всего документа —
                        // ощутимая CPU-работа, и на UI-потоке ей делать нечего.
                        prepared.InitialDelta = _serializer.BuildDeltaPayload(doc, null);

                        // Строим baseline без caret — именно это GetCustomData будет
                        // возвращать пока нет изменений. Файл тоже перезапишется без caret
                        // при следующем сохранении → хеши совпадут.
                        prepared.BaselineJson = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            v = 2,
                            doc = docJson,
                            local = localJson.Length > 0 ? localJson
                                    : System.Text.Json.JsonSerializer.Serialize(_localSettings),
                        });

                        return prepared;
                    }
                }
                else
                {
                    DocumentModel? legacyDoc = _serializer.Deserialize(raw);
                    if (legacyDoc is not null)
                    {
                        return new PreparedDocumentData
                        {
                            Document = legacyDoc,
                            InitialDelta = _serializer.BuildDeltaPayload(legacyDoc, null),
                            EnvelopeVersion = envelopeVersion
                        };
                    }
                }
                _logger.Warning("PrepareCustomData: Deserialize returned null");
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "PrepareCustomData: deserialization error");
            }

            return null;
        }

        /// <summary>
        /// Фаза 2 восстановления: применение подготовленных данных. Только UI-поток —
        /// здесь строятся вьюмодели (LoadDocument) и восстанавливается каретка.
        /// При prepared == null загружается пустой документ (как прежний SetCustomData
        /// при нечитаемых данных).
        /// </summary>
        public void ApplyPreparedCustomData(object? prepared)
        {
            _viewModel ??= CreateAndInitViewModel();

            if (prepared is PreparedDocumentData p && p.Document is not null)
            {
                if (p.LocalSettings is not null)
                {
                    _localSettings = p.LocalSettings;
                    _logger.Debug("Local settings restored: MonitorSizeInches={V}",
                        _localSettings.MonitorSizeInches);
                }

                if (p.CachedSessionData is not null)
                    _cachedSessionData = p.CachedSessionData;

                _viewModel.LoadDocument(p.Document, _localSettings);

                // LoadDocument создаёт новый DocumentViewModel — флаг read-only
                // режима сравнения на нём не выставлен. Применяем его заново,
                // иначе после Switch Version в compare mode документ редактируется.
                ApplyReadOnlyFromContext();

                // Инициализируем _lastDeltaPayload рассчитанной в фазе 1 дельтой.
                _lastDeltaPayload = p.InitialDelta;

                if (p.BaselineJson is not null)
                {
                    lock (_baselineSync)
                    {
                        // Инкремент версии инвалидирует снимки, снятые до загрузки:
                        // их фоновая сериализация не затрёт базовую линию нового документа.
                        _snapshotVersion++;
                        _baselineCustomData = p.BaselineJson;
                    }
                }

                // Каретку восстанавливаем ПОСЛЕ загрузки документа.
                // BaselineJson != null означает конвертный формат (v2/v3) — legacy-путь
                // каретку в SetCustomData не восстанавливал, сохраняем это поведение.
                if (p.BaselineJson is not null && _cachedSessionData is not null)
                    RestoreCaretFromCache();

                _documentLoadedFromData = true;

                _logger.Debug("Document loaded (v{V}), title={Title}", p.EnvelopeVersion, p.Document.Title);

                // Проверка ссылок на файлы картинок — фоном, после загрузки.
                // Отсутствующий файл раньше давал просто пустое место на листе,
                // и понять, что картинка потеряна, было нельзя.
                Dispatcher.UIThread.Post(WarnOnMissingImages, DispatcherPriority.Background);
                return;
            }

            _viewModel.LoadNewDocument(_localSettings);
            ApplyReadOnlyFromContext();
        }

        // Сообщает о картинках, файлы которых не найдены в проекте.
        private void WarnOnMissingImages()
        {
            try
            {
                var missing = FindMissingImageFiles();
                if (missing.Count == 0) return;

                _logger.Warning("Missing image files in project: {Files}", string.Join(", ", missing));

                CoreServices.GetService<Writersword.Core.Interfaces.Services.UI.INotificationService>()
                    ?.ShowWarning(missing.Count == 1
                        ? "Файл одной картинки не найден в проекте — она не отображается"
                        : $"Не найдены файлы картинок: {missing.Count} — они не отображаются");
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Missing image check failed");
            }
        }

        /// <summary>
        /// Есть ли в документе хоть что-то, кроме пустого абзаца: текст в любом
        /// run, плавающий объект или блок, отличный от параграфа (таблица,
        /// изображение, разрыв). Дешёвая проверка по модели, без сериализации.
        /// </summary>
        private static bool DocumentHasContent(DocumentModel? document)
        {
            if (document is null) return false;

            foreach (var section in document.Sections)
            {
                if (section.FloatingObjects.Count > 0)
                    return true;

                foreach (var block in section.Blocks)
                {
                    if (block is not ParagraphBlock paragraph)
                        return true;

                    foreach (var chunk in paragraph.Chunks)
                    {
                        foreach (var run in chunk.Runs)
                        {
                            if (!string.IsNullOrEmpty(run.Text))
                                return true;
                        }
                    }
                }
            }

            return document.Annotations.Count > 0;
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
        /// Освежает в сессионном кэше состояние вида из DocumentViewModel — масштаб, режим
        /// отображения и число страниц в ряду, — не трогая канвас (каретку/скролл).
        /// Вызывается из GetSessionData на фоновом потоке (autosave-таймер): канвас оттуда
        /// трогать нельзя, но это простые свойства вью-модели, читать их безопасно. Без
        /// этого фоновое сохранение писало бы устаревшее состояние (залипал старый масштаб,
        /// напр. 68%, и прежний режим отображения).
        /// </summary>
        private void UpdateCachedZoomOnly()
        {
            var dvm = _viewModel?.DocumentViewModel;
            if (dvm is null) return;
            double zoom = dvm.Zoom;
            string viewMode = dvm.ViewMode.ToString();
            int pagesPerRow = dvm.PagesPerRow;
            var reading = dvm.Reading;

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
                    zoom,
                    viewMode,
                    pagesPerRow,
                    reading
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

                // Режим отображения и число страниц в ряду — такое же состояние вида, как
                // масштаб. В документе режим тоже лежит, но его запись идёт через дельту
                // содержимого и до диска не доходит, пока текст не менялся. Ведущим считаем
                // сессионное значение; отсутствие поля (данные прежних версий) означает
                // «оставить то, что пришло из документа».
                EditorViewMode? viewMode = null;
                if (root.TryGetProperty("viewMode", out var vmProp)
                    && vmProp.ValueKind == System.Text.Json.JsonValueKind.String
                    && Enum.TryParse<EditorViewMode>(vmProp.GetString(), out var parsedViewMode))
                {
                    viewMode = parsedViewMode;
                }

                int? pagesPerRow = null;
                if (root.TryGetProperty("pagesPerRow", out var pprProp)
                    && pprProp.ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    pagesPerRow = pprProp.GetInt32();
                }

                // Настройки чтения: тема бумаги, приближение книги, формат листа.
                // Хранятся в сессии проекта — выбранный вид переживает перезапуск, но
                // в сам документ не попадает и на печать не влияет. Отсутствие поля —
                // данные прежних версий, там останутся значения по умолчанию.
                Models.Settings.ReadingSettings? reading = null;
                if (root.TryGetProperty("reading", out var readingProp)
                    && readingProp.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    try
                    {
                        reading = System.Text.Json.JsonSerializer
                            .Deserialize<Models.Settings.ReadingSettings>(readingProp.GetRawText());
                    }
                    catch (Exception rex)
                    {
                        _logger.Warning(rex, "Настройки чтения из сессии не разобраны");
                    }
                }

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    // Масштаб — часть состояния вида (SessionData), а не содержимого документа.
                    // Восстанавливаем его ДО каретки/скролла, иначе scroll-offset пересчитается с
                    // неправильным зумом. Актуальность кэша обеспечивает GetSessionData (см. ниже):
                    // на фоновом потоке он освежает зум из DocumentViewModel, поэтому залипания нет.
                    if (zoom > 0.01 && _viewModel?.DocumentViewModel is { } dvm)
                        dvm.Zoom = zoom;

                    // Режим и число страниц применяются тоже до каретки и скролла: они меняют
                    // пагинацию, а значит и координату, на которую встанет скролл.
                    if (reading is not null && _viewModel is { } readVm)
                        readVm.ApplyRestoredReadingSettings(reading);

                    if ((viewMode is not null || pagesPerRow is not null) && _viewModel is { } vm)
                    {
                        vm.ApplyRestoredViewState(
                            viewMode ?? vm.DocumentViewModel?.ViewMode ?? EditorViewMode.Page,
                            pagesPerRow ?? vm.DocumentViewModel?.PagesPerRow ?? 1);
                    }


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
            var dvm = _viewModel?.DocumentViewModel;
            double zoom = dvm?.Zoom ?? 1.0;

            _cachedSessionData = System.Text.Json.JsonSerializer.Serialize(new
            {
                para = docParaIdx,
                ch = charIdx,
                scroll = scrollY,
                zoom = zoom,
                viewMode = (dvm?.ViewMode ?? EditorViewMode.Page).ToString(),
                pagesPerRow = dvm?.PagesPerRow ?? 1,
                reading = dvm?.Reading
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
            {
                _cachedSessionData = raw;

                // View уже существует (например, Switch Version в режиме сравнения) —
                // применяем позицию каретки и скролла сразу, CreateView не будет вызван.
                if (_lastCreatedView is not null)
                    RestoreCaretFromCache();
            }
        }

        // ── IHotKeyProvider ───────────────────────────────────────────────

        /// <summary>
        /// Routes a hotkey command to the appropriate target.
        /// Navigation and editing go to DocumentCanvas.
        /// Formatting, tools and export go to TextEditorViewModel.
        /// </summary>
        public void ExecuteHotKey(string id)
        {
            var canvas = DocumentCanvas.FocusedInstance
                ?? _lastCreatedView?.FindControl<DocumentCanvas>("PageCanvas");

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
            vm.GlobalSettingsChanged = SaveGlobalSettings;
            vm.LoadNewDocument(_localSettings);
            return vm;
        }

        /// <summary>
        /// Сохраняет общие настройки модуля. Нужно видам чтения: вид, помеченный
        /// как общий для всех проектов, обязан пережить закрытие программы, а куда
        /// его писать, знает модуль — вью-модель про хранилище настроек не знает.
        /// </summary>
        private void SaveGlobalSettings(TextEditorSettings settings)
        {
            try
            {
                _globalSettings = settings;
                _localSettings = settings;
                _settingsService.SaveModuleSettings(moduleType, settings);
                _settingsService.Save();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Общие настройки модуля сохранить не удалось");
            }
        }
    }
}