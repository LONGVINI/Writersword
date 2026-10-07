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

    public sealed class TextEditorModule : BaseModule, IConfigurableModule, IUndoableModule, IHotKeyProvider, IStateSnapshotModule, IPreparedDataModule, IProjectAssetHolder
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
        /// Ревизия модуля (BaseModule.Revision) на момент последнего снимка данных
        /// документа. Пока она не сдвинулась, модуль о правках не сообщал, и снимок
        /// проверяется сверкой хешей; сдвинулась — снимок снимается без сверки.
        /// Только UI-поток.
        /// </summary>
        private long _revisionAtLastSnapshot = long.MinValue;

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
            _logger.Debug("[UNDO-KEY] Отмена через модуль в обход полотна: снимков для отмены {Can}", _undoStack.CanUndo);
            _undoStack.Undo();
            _viewModel?.DocumentViewModel?.FireCursorContextChanged();
        }

        public void Redo()
        {
            _logger.Debug("[UNDO-KEY] Повтор через модуль в обход полотна: снимков для повтора {Can}", _undoStack.CanRedo);
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

            // Отслеживание правок: состояние документа однозначно задаётся позициями в
            // обоих стеках отмены (набор текста и форматирование). Каждое движение любого
            // из них — правка; возврат обоих в сохранённые позиции — откат до сохранённого.
            SetHistoryBaseline(CombinedHistoryState());
            _undoStack.StateChanged += OnHistoryStateChanged;
            _textUndoStack.StateChanged += OnHistoryStateChanged;

            SharedReadingSettingsSaved += OnSharedReadingSettingsSaved;
        }

        // ── Отслеживание правок ───────────────────────────────────────────

        /// <summary>
        /// Все правки документа проходят через стеки отмены, поэтому модуль сообщает
        /// о правках сам, и вкладка не собирает документ целиком ради ответа на вопрос
        /// «есть ли несохранённое».
        /// </summary>
        public override bool TracksChanges => true;

        /// <summary>
        /// Номер состояния документа из двух стеков отмены. Номера состояний сквозные и
        /// уникальные, перемешивание делает совпадение разных пар практически невозможным.
        /// </summary>
        private long CombinedHistoryState()
            => unchecked((_textUndoStack.StateId * -7046029254386353131L) ^ _undoStack.StateId);

        private void OnHistoryStateChanged() => NotifyHistoryChanged(CombinedHistoryState());

        private void OnViewModelContentEdited() => NotifyContentChanged();

        private void OnViewModelChangedOutsideHistory() => NotifyDataChanged();

        // ── BaseModule ────────────────────────────────────────────────────

        /// <summary>
        /// Реакция на смену контекста: режим сравнения делает документ read-only.
        /// Защита стоит на уровне вьюмодели (форматирование, абзацы, вставка блоков)
        /// и канваса (ввод, удаление, Enter, буфер обмена, undo/redo, ручки таблиц) —
        /// листать и копировать можно, изменять данные нельзя.
        /// </summary>
        protected override void OnContextChanged(DocumentContext? context)
        {
            // Шрифты, уложенные в прежний проект, к новому отношения не имеют:
            // забыть их надо до первой отрисовки, иначе новая рукопись покажется
            // чужим шрифтом с тем же именем семейства.
            Services.ProjectFonts.Invalidate();

            ApplyReadOnlyFromContext();

            // Контекст мог прийти после создания вида: закладка ищется по книге,
            // а книгу называет именно он.
            BindReadingBookmark();

            // Тот же случай и у документа Word, из которого сделан проект: какой
            // документ вливать, известно только по файлу проекта.
            SchedulePendingImport();
        }

        // Импорт документа, из которого сделан проект, уже запущен. Второй вызов
        // (контекст пришёл после вида, вид пересоздан) его не повторяет.
        private bool _pendingImportScheduled;

        /// <summary>
        /// Проект создан экраном приветствия из документа Word, и документ ещё не
        /// влит. Импорт откладывается на следующий такт: вид к этому моменту уже
        /// построен, а загрузка модуля доведена до конца — иначе вливаемый
        /// документ могла бы перекрыть пустая болванка нового проекта.
        /// </summary>
        private void SchedulePendingImport()
        {
            if (_pendingImportScheduled) return;
            if (_viewModel?.DocumentViewModel is null || _lastCreatedView is null) return;

            var context = Context;
            if (context is null || context.IsInCompareMode) return;
            if (!PendingFileImports.IsPending(context.FilePath)) return;

            _pendingImportScheduled = true;
            Dispatcher.UIThread.Post(() => _ = RunPendingImportAsync(context), DispatcherPriority.Background);
        }

        /// <summary>
        /// Вливает документ в проект и сразу сохраняет проект: файл на диске должен
        /// содержать текст с первой же минуты, а не пустоту до первого Ctrl+S —
        /// закрой человек программу, не сохранившись, он нашёл бы пустой проект
        /// там, где ждал свою рукопись.
        /// </summary>
        private async System.Threading.Tasks.Task RunPendingImportAsync(DocumentContext context)
        {
            try
            {
                // Контекст успел смениться — документ принадлежит другому проекту.
                if (!ReferenceEquals(Context, context)) return;

                var docVm = _viewModel?.DocumentViewModel;
                if (docVm is null) return;

                if (!PendingFileImports.TryTake(context.FilePath, out var sourcePath)) return;

                _logger.Information("Importing the source document into the new project: {Source} -> {Project}",
                    sourcePath, context.FilePath);

                bool imported = await docVm.ImportIntoNewProjectAsync(sourcePath, context);
                if (!imported) return;

                var tab = CoreServices.GetService<ITabCollection>()?.FindByPath(context.FilePath);
                var workflow = CoreServices.GetService<IProjectWorkflow>();
                if (tab is null || workflow is null) return;

                await workflow.SaveDocumentAsync(tab, showNotification: false);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to import the source document into the new project: {Project}",
                    context.FilePath);
            }
        }

        // Канвас и книга, для которых закладка уже передана. Повторная передача той
        // же книге того же канваса вернула бы книгу на запомненное место посреди чтения.
        private DocumentCanvas? _readingBookmarkCanvas;
        private string? _readingBookmarkKey;

        /// <summary>
        /// Имя книги для закладки чтения: идентификатор проекта, а без него — путь к
        /// файлу. Идентификатор переживает переименование и перенос файла. В режиме
        /// сравнения версий закладка не ведётся: там читают не книгу, а её версию.
        /// </summary>
        private string? ReadingBookmarkKey()
        {
            var ctx = Context;
            if (ctx is null || ctx.IsInCompareMode) return null;

            var id = ctx.Project?.Id;
            if (!string.IsNullOrWhiteSpace(id)) return id;

            return string.IsNullOrWhiteSpace(ctx.FilePath) ? null : ctx.FilePath;
        }

        /// <summary>
        /// Связывает канвас с закладкой чтения: отдаёт ему место, запомненное с
        /// прошлого раза, и запоминает каждое новое место, куда книгу перелистнули.
        /// </summary>
        private void BindReadingBookmark()
        {
            var canvas = _lastCreatedView?.FindControl<DocumentCanvas>("PageCanvas");
            if (canvas is null) return;

            canvas.ReadingPositionChanged = (paragraph, line) =>
            {
                var book = ReadingBookmarkKey();
                if (book is null) return;

                ReadingBookmarkStore.Save(book, paragraph, line);
            };

            var key = ReadingBookmarkKey();
            if (key is null) return;
            if (ReferenceEquals(canvas, _readingBookmarkCanvas) && key == _readingBookmarkKey) return;

            _readingBookmarkCanvas = canvas;
            _readingBookmarkKey = key;

            if (ReadingBookmarkStore.Get(key) is { } mark)
                canvas.SetReadingBookmark(mark.Paragraph, mark.Line);
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

            // Полотно сообщает, когда место в документе сменилось: сессионный кэш
            // освежается на UI-потоке, и фоновое сохранение получает свежее место.
            BindCanvasViewState(view);

            // Передаём карту шрифтов по скриптам в канвас при создании View.
            ApplyScriptFontMapToCanvas();

            // Если SetSessionData был вызван до CreateView (стандартный сценарий DockFactory),
            // восстанавливаем позицию каретки сейчас — view уже существует.
            if (_cachedSessionData is not null)
                RestoreCaretFromCache();
            else
                RestoreEditingPlaceFromStore();

            // Место, где книгу читали в прошлый раз, — книга откроется с него.
            BindReadingBookmark();

            // Проект сделан из документа Word — документ вливается, как только
            // модулю есть где его показать.
            SchedulePendingImport();

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

        // Полотно, чьи сообщения о смене места слушает модуль. Вью пересоздаётся при
        // переключениях, и подписка переходит на новое полотно.
        private DocumentCanvas? _viewStateCanvas;

        private void BindCanvasViewState(TextEditorView view)
        {
            var canvas = view.FindControl<DocumentCanvas>("PageCanvas");

            if (_viewStateCanvas is not null)
                _viewStateCanvas.ViewStateChanged -= OnCanvasViewStateChanged;

            _viewStateCanvas = canvas;

            if (canvas is not null)
                canvas.ViewStateChanged += OnCanvasViewStateChanged;
        }

        /// <summary>
        /// Место в документе сменилось. Кэш освежается только по текущему полотну:
        /// прежнее, уходящее с экрана, могло сообщить уже после создания новой вью,
        /// а у новой место ещё ждёт раскладки и отдаётся им самим.
        /// </summary>
        private void OnCanvasViewStateChanged(object? sender, EventArgs e)
        {
            var current = _lastCreatedView?.FindControl<DocumentCanvas>("PageCanvas");
            if (sender is DocumentCanvas canvas && !ReferenceEquals(canvas, current)) return;

            RefreshSessionCacheOnUIThread();

            // Место уходит и в данные программы: кеш проекта с сессией удаляется при
            // сохранении и при закрытии, и без этой записи рукопись после перезапуска
            // открывалась бы в начале (Services.EditingPlaceStore).
            if (current is not null) SaveEditingPlace(current);
        }

        /// <summary>Запоминает место в рукописи в данных программы.</summary>
        private void SaveEditingPlace(DocumentCanvas canvas)
        {
            var key = ReadingBookmarkKey();
            if (key is null) return;

            var dvm = _viewModel?.DocumentViewModel;
            if (dvm is null) return;

            var (para, ch, scrollY) = canvas.GetCaretState();
            EditingPlaceStore.Save(key, new EditingPlaceStore.Place(
                para, ch, scrollY, dvm.Zoom, dvm.ViewMode.ToString()));
        }

        /// <summary>
        /// Место из данных программы, приведённое к текущему виду. Прокрутка снята в
        /// точках экрана: при другом масштабе она пересчитывается, при другом режиме
        /// сбрасывается в ноль — тогда полотно ставит вид по каретке.
        /// </summary>
        private (int Para, int Char, double ScrollY)? StoredEditingPlace()
        {
            var key = ReadingBookmarkKey();
            if (key is null) return null;

            if (EditingPlaceStore.Get(key) is not { } place) return null;

            var dvm = _viewModel?.DocumentViewModel;
            if (dvm is null) return null;

            double scroll = place.ScrollY;
            if (!string.Equals(place.ViewMode, dvm.ViewMode.ToString(), StringComparison.Ordinal))
                scroll = 0;
            else if (place.Zoom > 0.01 && Math.Abs(place.Zoom - dvm.Zoom) > 0.0001)
                scroll = scroll * dvm.Zoom / place.Zoom;

            return (place.Paragraph, place.Char, scroll);
        }

        /// <summary>
        /// Возвращает рукопись на место из данных программы, когда сессии нет: кеш
        /// проекта после сохранения и закрытия удалён, а место осталось.
        /// </summary>
        private void RestoreEditingPlaceFromStore()
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (StoredEditingPlace() is not { } place) return;

                var canvas = _lastCreatedView?.FindControl<DocumentCanvas>("PageCanvas");
                if (canvas is null) return;

                _logger.Debug(
                    "[VIEW] Место из данных программы: абзац {Para}, символ {Char}, прокрутка {Scroll:F0}",
                    place.Para, place.Char, place.ScrollY);

                canvas.RestoreViewState(place.Para, place.Char, place.ScrollY);
            }, Avalonia.Threading.DispatcherPriority.Loaded);
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
        /// Передаёт в DocumentCanvas настройки замены недостающих знаков: общий
        /// признак, шрифт подстановки и карту "скрипт → шрифт".
        /// Вызывается при создании View и при изменении настроек.
        ///
        /// Настройки берутся локальные, а не глобальные, и это не оплошность:
        /// вид рукописи задаёт сам проект и уезжает вместе с ним, а не зависит от
        /// того, на какой машине её открыли.
        ///
        /// Порядок присваиваний важен: каждое из трёх свойств пересобирает
        /// резолвер стилей, поэтому карта ставится последней — к этому моменту
        /// остальные две уже на месте.
        /// </summary>
        private void ApplyScriptFontMapToCanvas()
        {
            var canvas = _lastCreatedView?.FindControl<DocumentCanvas>("PageCanvas");
            if (canvas is null) return;
            canvas.BreakOnHyphen = _localSettings.BreakOnHyphen;
            canvas.SubstituteMissingGlyphs = _localSettings.SubstituteMissingGlyphs;
            canvas.SubstituteFontFamily = _localSettings.SubstituteFontFamily;
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
            // нельзя: он уходит в кеш и в проект как полноценные данные и затирает
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
                // Не ошибка: у нового проекта данных для модуля нет и быть не может,
                // а пустой документ отдавать нельзя — он затрёт сохранённое. На уровне
                // Error эта строка сыпалась при каждом создании проекта и создавала
                // ложную тревогу. Сам возврат null остаётся защитой и работает как прежде.
                _logger.Debug("TakeStateSnapshot: module never received its data and the document was never " +
                    "filled — returning null so the saved version is preserved. Module type: {Type}", moduleType);
                return null;
            }

            try
            {
                var document = _viewModel.DocumentViewModel.Document;
                long revision = Revision;

                if (revision != _revisionAtLastSnapshot)
                {
                    // Модуль сам сообщил о правке с прошлого снимка: документ точно
                    // изменился, и сверять хеши всех чанков ради ответа «есть ли
                    // изменения» незачем — снимок снимается в любом случае, а сам он
                    // полный, не из изменившихся чанков. Сверка всего документа
                    // стоила десятков миллисекунд UI-потока на каждом тике кеша, пока
                    // идёт набор. Чанки нормализуются, как и при сверке: снимок
                    // должен уйти в том же виде, что и прежде.
                    //
                    // Хеши чанков после этого отстают от текста. Ближайшая сверка
                    // (без новых правок) увидит расхождение и снимет снимок ещё раз —
                    // лишняя работа один раз, но не пропуск правки.
                    _serializer.NormalizeAllChunks(document);
                }
                else
                {
                    // Модуль о правках не сообщал. Сверка хешей остаётся страховкой:
                    // правка, прошедшая мимо отслеживания, всё равно попадёт в
                    // сохранение.
                    DeltaCachePayload payload = _serializer.BuildDeltaPayload(document, _lastDeltaPayload);

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
                }

                _revisionAtLastSnapshot = revision;

                // Удаляем из проекта файлы картинок, на которые в документе больше нет ссылок.
                try { CleanupUnusedImages(_viewModel.DocumentViewModel.Document); }
                catch (Exception cex) { _logger.Warning(cex, "CleanupUnusedImages failed"); }

                string localSettingsJson = System.Text.Json.JsonSerializer.Serialize(_localSettings);

                DocumentModel documentClone = DocumentCloner.Clone(document);

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
                        case Models.Document.ImageBlock image:
                            if (!string.IsNullOrEmpty(image.ImageFileName))
                                target.Add(image.ImageFileName);

                            // Исходник перекодированной картинки (TIFF, EMF, WMF из
                            // .docx) живой так же: экспорт пишет в .docx именно его.
                            if (!string.IsNullOrEmpty(image.SourceImageFileName))
                                target.Add(image.SourceImageFileName!);
                            break;

                        // Картинка-заливка фигуры — такой же файл проекта.
                        case Models.Document.ShapeBlock shape
                            when !string.IsNullOrEmpty(shape.FillImageFileName):
                            target.Add(shape.FillImageFileName!);
                            break;

                        // Картинка может лежать внутри ячейки таблицы или надписи —
                        // такие ссылки тоже живые.
                        case Models.Document.TableBlock table:
                            foreach (var cell in table.Cells)
                            {
                                Walk(cell.ParagraphsDeep());

                                // Плавающие картинки и фигуры ячейки.
                                if (cell.Floats is { Count: > 0 } cellFloats)
                                    Walk(cellFloats.Select(f => f.Object));
                            }

                            // Вложенные таблицы с их собственными плавающими объектами.
                            Walk(table.Cells
                                .Where(c => c.NestedTables is { Count: > 0 })
                                .SelectMany(c => c.NestedTables!)
                                .Select(n => (Models.Document.BlockModel)n.Table));
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
                        .GetService<Writersword.Core.Interfaces.Services.IProjectCacheService>();
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
        /// Можно ли считать текущее состояние документа полным.
        ///
        /// Модуль поднимается раньше, чем к нему приезжают данные: сначала
        /// Create и Initialize, и только потом, через фоновый поток,
        /// ApplyPreparedCustomData. Между этими двумя моментами вьюмодель уже
        /// есть, а документ пуст — и уборка, запущенная в этот промежуток,
        /// не нашла бы ни одной живой ссылки и стёрла бы все картинки рукописи.
        ///
        /// То же состояние остаётся навсегда, если данные модуля не разобрались:
        /// битый или незнакомый конверт даёт пустой документ при живых файлах
        /// в архиве.
        ///
        /// Признак тот же, что защищает сохранение в TakeStateSnapshot: либо
        /// данные пришли, либо пользователь сам что-то написал.
        /// </summary>
        private bool DocumentStateIsComplete()
        {
            if (_viewModel?.DocumentViewModel?.Document is null) return false;
            return _documentLoadedFromData || _documentEverHadContent;
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

            if (!DocumentStateIsComplete())
            {
                _logger.Warning("CompactUnusedImages: document state is not complete — " +
                    "cleanup refused so that live images are not taken for orphans");
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

        // ── Файлы проекта ─────────────────────────────────────────────────

        /// <summary>
        /// Файлы, которые держит редактор: картинки документа и картинки видов
        /// чтения — бумага и поле вокруг книги.
        ///
        /// Картинки документа лежат в архиве с самого начала, и здесь они
        /// перечисляются не ради укладки, а ради второго вопроса: на месте ли
        /// они. Ссылка на страницу, файла которой в архиве нет, — это дырка в
        /// рукописи, и увидеть её нужно списком, а не пустым местом на листе.
        /// </summary>
        public IEnumerable<Writersword.Core.Models.Project.ProjectAssetRef> CollectAssetRefs()
        {
            var ctx = CoreServices.GetService<ITabCollection>()?.ActiveTab?.Context;

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectImageNames(_viewModel?.DocumentViewModel?.Document, names);

            foreach (var name in names)
            {
                var path = $"TextEditor/Images/{name}";

                // Картинка недавнего импорта может ещё писаться в проект.
                var bytes = PendingProjectImages.Read(ctx, path);

                yield return new Writersword.Core.Models.Project.ProjectAssetRef
                {
                    Ref = path,
                    ModuleType = moduleType,
                    Kind = Writersword.Core.Models.Project.ProjectAssetKind.Image,
                    Place = bytes is { Length: > 0 }
                        ? Writersword.Core.Models.Project.ProjectAssetPlace.InProject
                        : Writersword.Core.Models.Project.ProjectAssetPlace.Missing,
                    Hold = Writersword.Core.Models.Project.ProjectAssetHold.Used,
                    DisplayName = name,
                    OwnerName = "Картинка в тексте",
                    Bytes = bytes?.LongLength ?? 0
                };
            }

            foreach (var theme in ReadingThemesInPlay())
            {
                foreach (var item in ThemeAssets(theme))
                    yield return item;
            }

            foreach (var item in FontAssets())
                yield return item;
        }

        // ── Шрифты ────────────────────────────────────────────────────────

        /// <summary>
        /// Шрифты, которыми набрана рукопись, и то, что о них известно.
        ///
        /// Шрифт хранится именем семейства, и у того, кому проект передали, это
        /// имя может не значить ничего: Скиа молча подставит похожий, и книга
        /// откроется другой — другие пропорции, другие переносы, другое число
        /// страниц. Поэтому шрифт перечисляется наравне с картинками.
        /// </summary>
        private IEnumerable<Writersword.Core.Models.Project.ProjectAssetRef> FontAssets()
        {
            foreach (var (family, owner) in UsedFontFamilies())
            {
                bool embedded = Services.ProjectFonts.IsEmbedded(family);
                bool installed = Services.ProjectFonts.IsInstalled(family);

                var place = embedded
                    ? Writersword.Core.Models.Project.ProjectAssetPlace.InProject
                    : installed
                        // Шрифт есть в системе автора, но не в проекте: у другого
                        // человека его может не оказаться, и уложить его надо.
                        ? Writersword.Core.Models.Project.ProjectAssetPlace.OnDisk
                        // Шрифта нет ни в проекте, ни в системе: текст уже сейчас
                        // показывается подменой, и уложить нечего.
                        : Writersword.Core.Models.Project.ProjectAssetPlace.Missing;

                yield return new Writersword.Core.Models.Project.ProjectAssetRef
                {
                    Ref = "font:" + family,
                    ModuleType = moduleType,
                    Kind = Writersword.Core.Models.Project.ProjectAssetKind.Font,
                    Place = place,
                    Hold = Writersword.Core.Models.Project.ProjectAssetHold.Used,
                    DisplayName = family,
                    OwnerName = owner,
                    // Размер нужен как раз у неуложенных: из него складывается
                    // обещание «файл вырастет на N МБ». Прежнее условие считало
                    // ровно наоборот — у уже уложенных, — и диалог всегда обещал 0.
                    Bytes = embedded
                        ? Services.ProjectFonts.BytesOf(family)
                        : Services.ProjectFonts.InstalledBytesOf(family)
                };
            }
        }

        /// <summary>
        /// Семейства шрифтов, встречающиеся в рукописи, и где именно. Стили
        /// документа, отдельные куски текста, ячейки таблиц, надписи и шрифт
        /// вида чтения — всё, что доходит до отрисовки.
        /// </summary>
        private IEnumerable<(string Family, string Owner)> UsedFontFamilies()
        {
            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            void Note(string? family, string owner)
            {
                if (string.IsNullOrWhiteSpace(family)) return;
                if (seen.ContainsKey(family!)) return;
                seen[family!] = owner;
            }

            var document = _viewModel?.DocumentViewModel?.Document;
            if (document is not null)
            {
                foreach (var style in document.Styles)
                    Note(style.RunProperties?.FontFamily, $"Стиль «{style.Name}»");

                foreach (var section in document.Sections)
                {
                    WalkFonts(section.Blocks, Note);
                    WalkFonts(section.FloatingObjects, Note);
                    WalkFonts(section.InlineObjects, Note);
                }
            }

            // Гарнитура чтения живёт в настройках чтения, а не в виде: вид держит
            // цвета, бумагу и поле, а начертание — поправка под глаза читателя.
            Note(_viewModel?.DocumentViewModel?.Reading.FontFamily, "Чтение");

            foreach (var pair in seen)
                yield return (pair.Key, pair.Value);
        }

        private static void WalkFonts(
            IEnumerable<Models.Document.BlockModel> blocks,
            Action<string?, string> note)
        {
            foreach (var block in blocks)
            {
                switch (block)
                {
                    case Models.Document.ParagraphBlock paragraph:
                        foreach (var chunk in paragraph.Chunks)
                            foreach (var run in chunk.Runs)
                                note(run.Properties?.FontFamily, "Текст рукописи");
                        break;

                    case Models.Document.TableBlock table:
                        foreach (var cell in table.Cells)
                            WalkFonts(cell.ParagraphsDeep(), note);
                        break;

                    case Models.Document.FloatingTextBlock floatingText:
                        WalkFonts(floatingText.Paragraphs, note);
                        break;
                }
            }
        }

        /// <summary>
        /// Начертания, которые нужно уложить вместе с семейством. Кладутся все
        /// четыре: полужирный и курсив встречаются в рукописи повсеместно, а
        /// разбирать по кускам, какое именно начертание где использовано, значит
        /// однажды не доложить одно из них и получить подмену ровно в том
        /// абзаце, где её меньше всего ждут.
        /// </summary>
        private static readonly SkiaSharp.SKFontStyle[] EmbeddedFontStyles =
        {
            SkiaSharp.SKFontStyle.Normal,
            SkiaSharp.SKFontStyle.Bold,
            SkiaSharp.SKFontStyle.Italic,
            SkiaSharp.SKFontStyle.BoldItalic
        };

        /// <summary>
        /// Виды чтения, картинки которых должны уехать с проектом: приложенные к
        /// документу и тот, которым книга открыта сейчас.
        ///
        /// Виды из настроек программы сюда не попадают намеренно. Они не часть
        /// проекта: их не отдают вместе с рукописью, и укладывать их картинки в
        /// архив значило бы возить с каждым проектом чужое оформление.
        /// </summary>
        private IEnumerable<ReadingTheme> ReadingThemesInPlay()
        {
            var document = _viewModel?.DocumentViewModel?.Document;
            if (document?.ReadingThemes is { } themes)
                foreach (var theme in themes)
                    if (theme != null) yield return theme;

            if (_viewModel?.DocumentViewModel?.Reading.Active is { } active)
                yield return active;
        }

        private IEnumerable<Writersword.Core.Models.Project.ProjectAssetRef> ThemeAssets(
            ReadingTheme theme)
        {
            // Картинок у вида может быть сколько угодно: и бумага, и поле — наборы.
            var refs = new List<(string? Reference, string What)>();
            foreach (var one in theme.ImagePaths) refs.Add((one, "бумага"));
            foreach (var one in theme.BackdropImagePaths) refs.Add((one, "фон"));

            foreach (var (reference, what) in refs)
            {
                if (string.IsNullOrWhiteSpace(reference)) continue;

                var bytes = ReadingAssets.Read(reference);

                var place = bytes is null || bytes.Length == 0
                    ? Writersword.Core.Models.Project.ProjectAssetPlace.Missing
                    : ReadingAssets.IsProjectRef(reference)
                        ? Writersword.Core.Models.Project.ProjectAssetPlace.InProject
                        : ReadingAssets.IsAppRef(reference)
                            ? Writersword.Core.Models.Project.ProjectAssetPlace.InApp
                            : Writersword.Core.Models.Project.ProjectAssetPlace.OnDisk;

                yield return new Writersword.Core.Models.Project.ProjectAssetRef
                {
                    Ref = reference!,
                    ModuleType = moduleType,
                    Kind = Writersword.Core.Models.Project.ProjectAssetKind.Image,
                    Place = place,
                    Hold = Writersword.Core.Models.Project.ProjectAssetHold.Used,
                    DisplayName = System.IO.Path.GetFileName(reference!),
                    OwnerName = $"Вид чтения «{theme.Name}», {what}",
                    Bytes = bytes?.LongLength ?? 0
                };
            }
        }

        /// <summary>
        /// Уложить в архив картинки видов чтения, лежащие снаружи.
        ///
        /// Картинки документа здесь не трогаются: они и так в архиве — другого
        /// места для них никогда и не было.
        /// </summary>
        public System.Threading.Tasks.Task<int> EmbedExternalAssetsAsync()
        {
            int embedded = 0;

            foreach (var theme in ReadingThemesInPlay())
            {
                // Все картинки вида разом — и бумага, и поле, сколько бы их ни было.
                int moved = 0;
                theme.MapImageReferences(reference =>
                {
                    var inProject = ReadingAssets.EnsureInProject(reference);
                    if (!string.Equals(inProject, reference, StringComparison.Ordinal)) moved++;
                    return inProject;
                });

                embedded += moved;
            }

            if (embedded > 0)
            {
                _viewModel?.DocumentViewModel?.RaiseReadingSettingsChanged();
                _logger.Information("Reading images embedded into the project: {Count}", embedded);
            }

            // Шрифты: укладывается только то, что есть в системе. Семейство,
            // которого нет и здесь, взять неоткуда — о нём говорит отчёт, а
            // выдумывать вместо него подмену и класть её в проект нельзя.
            int fonts = 0;
            foreach (var (family, _) in UsedFontFamilies())
            {
                if (Services.ProjectFonts.IsEmbedded(family)) continue;
                if (!Services.ProjectFonts.IsInstalled(family)) continue;

                fonts += Services.ProjectFonts.Embed(family, EmbeddedFontStyles);
            }

            if (fonts > 0)
                _logger.Information("Font files embedded into the project: {Count}", fonts);

            return System.Threading.Tasks.Task.FromResult(embedded + fonts);
        }

        /// <summary>
        /// Уборка: картинки документа, на которые не осталось ссылок, и картинки
        /// видов, которыми ни один вид больше не пользуется.
        ///
        /// Живыми считаются не только ссылки текущего состояния: картинку ещё
        /// можно вернуть из истории отмены, из версии восстановления и из буфера
        /// обмена. Именно на этом обожглась прежняя уборка по таймеру — она
        /// удаляла файл, который возвращался по Ctrl+Z.
        /// </summary>
        public Writersword.Core.Models.Project.ProjectAssetCleanup CompactUnusedAssets()
        {
            var projectPath = CoreServices.GetService<ITabCollection>()?.ActiveTab?.FilePath;

            // Шрифты уборка не трогает намеренно. Живых ссылок на них у истории
            // отмены нет — она помнит картинки, но не гарнитуры, — а значит
            // отличить «шрифт больше не нужен» от «шрифт вернётся следующим
            // Ctrl+Z» здесь нечем. Убирать наугад то, что весит мегабайты и
            // молча меняет вид всей рукописи, нельзя.
            var (removed, freed) = CompactUnusedImages(projectPath);
            var reading = CompactUnusedReadingImages();

            return new Writersword.Core.Models.Project.ProjectAssetCleanup
            {
                Removed = removed + reading.Removed,
                FreedBytes = freed + reading.FreedBytes
            };
        }

        /// <summary>
        /// Складывает в набор имена файлов картинок одного вида чтения.
        /// </summary>
        private static void AddReadingRefs(
            ReadingTheme? theme, HashSet<string> live)
        {
            if (theme is null) return;

            foreach (var reference in theme.AllImages)
            {
                if (ReadingAssets.IsProjectRef(reference))
                    live.Add(System.IO.Path.GetFileName(reference));
            }
        }

        /// <summary>
        /// Виды чтения из версии, лежащей в кеше восстановления. Пока версия не
        /// принята и не отклонена, её картинки нужны.
        ///
        /// Читается тем же путём, что и картинки текста в
        /// CollectLiveImageReferences, и так же роняет уборку при неудаче:
        /// не прочитали — значит не знаем, что живое, и удалять нельзя.
        /// </summary>
        private IReadOnlyList<ReadingTheme> CachedReadingThemes()
        {
            var projectPath = CoreServices.GetService<ITabCollection>()?.ActiveTab?.FilePath;
            if (string.IsNullOrEmpty(projectPath))
                return System.Array.Empty<ReadingTheme>();

            try
            {
                var cacheService = CoreServices
                    .GetService<Writersword.Core.Interfaces.Services.IProjectCacheService>();
                var cached = cacheService?.GetModuleCustomData(projectPath!, moduleType);
                var document = (PrepareCustomData(cached) as PreparedDocumentData)?.Document;

                return document?.ReadingThemes is { } themes
                    ? themes.ToList()
                    : (IReadOnlyList<ReadingTheme>)
                        System.Array.Empty<ReadingTheme>();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to read cached reading themes — cleanup aborted for safety");
                throw;
            }
        }

        /// <summary>
        /// Убирает из архива картинки видов чтения, на которые не ссылается ни
        /// один вид документа и ни рабочая копия.
        ///
        /// Виды из настроек программы здесь не спрашиваются: их картинки лежат в
        /// данных программы, а не в архиве, и на содержимое архива они не
        /// влияют.
        /// </summary>
        private Writersword.Core.Models.Project.ProjectAssetCleanup CompactUnusedReadingImages()
        {
            var ctx = CoreServices.GetService<ITabCollection>()?.ActiveTab?.Context;
            if (ctx is null) return Writersword.Core.Models.Project.ProjectAssetCleanup.Nothing;

            if (!DocumentStateIsComplete())
            {
                _logger.Warning("CompactUnusedReadingImages: document state is not complete — " +
                    "cleanup refused so that live view images are not taken for orphans");
                return Writersword.Core.Models.Project.ProjectAssetCleanup.Nothing;
            }

            var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var theme in ReadingThemesInPlay())
                AddReadingRefs(theme, live);

            // Версия из кеша восстановления держит свои картинки живыми ровно так
            // же, как и картинки текста: пока пользователь не выбрал версию, её
            // бумага должна оставаться на месте. Без этого смена бумаги, уборка и
            // последующий откат давали вид без картинки.
            var cachedThemes = CachedReadingThemes();
            foreach (var theme in cachedThemes)
                AddReadingRefs(theme, live);

            int removed = 0;
            long freed = 0;

            foreach (var path in ctx.GetFiles("TextEditor/Reading").ToList())
            {
                string name = path.Substring(path.LastIndexOf('/') + 1);
                if (string.IsNullOrEmpty(name)) continue;
                if (live.Contains(name)) continue;

                long size = ctx.ReadFile(path)?.LongLength ?? 0;
                ctx.DeleteFile(path);
                removed++;
                freed += size;

                _logger.Debug("Unused reading image removed: {Name} ({Size} bytes)", name, size);
            }

            if (removed > 0) ctx.FlushStorage();

            return new Writersword.Core.Models.Project.ProjectAssetCleanup
            {
                Removed = removed,
                FreedBytes = freed
            };
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

                    // Настройки чтения — личное дело читателя, а не свойство рукописи:
                    // подача, формат листа, вид и ступень размера должны быть теми же,
                    // какими человек оставил их в прошлый раз, в любом проекте. В файле
                    // проекта лежит их копия на момент сохранения, и без этого переноса
                    // она перекрывала выбор человека при каждом открытии — вид и подача
                    // «не сохранялись». Виды переносятся тем же порядком: заведённый
                    // «везде» обязан быть под рукой и в чужой рукописи.
                    CarryOverReadingPreferences(_globalSettings, _localSettings);

                    // «Мои эффекты» — наборы человека, а не рукописи: в любом проекте
                    // под рукой те, что он завёл последними, а не копия из файла.
                    CarryOverTextEffectPresets(_globalSettings, _localSettings);

                    // Вид листа при правке — тоже предпочтение человека, а не
                    // рукописи. Копия в файле проекта снята в момент его последнего
                    // сохранения, а смена вида проект изменённым не делает: выбранный
                    // после этого вид в файл не попадал, и при следующем открытии
                    // проекта лист возвращался к старому — вид «слетал».
                    CarryOverEditorViewPreferences(_globalSettings, _localSettings);
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

                // Загруженный документ и есть исходная точка: первый снимок после
                // загрузки проверяется сверкой хешей, как и прежде.
                _revisionAtLastSnapshot = Revision;

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
                Dispatcher.UIThread.Post(WarnOnMissingFonts, DispatcherPriority.Background);
                return;
            }

            _viewModel.LoadNewDocument(_localSettings);
            ApplyReadOnlyFromContext();
        }

        // Сообщает о картинках, файлы которых не найдены в проекте.
        /// <summary>
        /// Сообщает о гарнитурах, которых нет ни в проекте, ни в системе.
        ///
        /// Молчать здесь нельзя: текст показывается подменой, разбивка строк и
        /// число страниц отличаются от авторских, а понять это по экрану
        /// невозможно — подмена выглядит как обычный текст. Имена гарнитур в
        /// документе при этом остаются нетронутыми: вернётся шрифт — вернётся и
        /// вид, ничего восстанавливать не придётся.
        /// </summary>
        private void WarnOnMissingFonts()
        {
            try
            {
                var missing = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
                foreach (var (family, _) in UsedFontFamilies())
                    if (Services.ProjectFonts.IsMissing(family)) missing.Add(family);

                // Значок на вкладке модуля выставляется всегда — и когда всё в
                // порядке, чтобы снять оставшийся от прошлого документа.
                // Всплывающее сообщение говорит один раз и исчезает, а нехватка
                // никуда не девается: увидеть её должно быть можно и через час.
                MarkFontWarning(missing);

                if (missing.Count == 0) return;

                _logger.Warning("Missing font families: {Families}", string.Join(", ", missing));

                CoreServices.GetService<Writersword.Core.Interfaces.Services.UI.INotificationService>()
                    ?.ShowWarning(missing.Count == 1
                        ? $"Гарнитура {missing.First()} не установлена — текст показан подменой, "
                          + "разбивка страниц может отличаться от авторской"
                        : $"Не установлено гарнитур: {missing.Count} ({string.Join(", ", missing.Take(3))}"
                          + (missing.Count > 3 ? "…" : "")
                          + ") — текст показан подменой, разбивка страниц может отличаться");
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Missing font check failed");
            }
        }

        /// <summary>
        /// Красный значок на вкладке модуля и текст подсказки к нему.
        ///
        /// Модуль не знает ни про Dock, ни про вкладки — его сборка на сборку
        /// приложения не ссылается и не должна. Наружу уходит только намерение
        /// «показать вот это», а куда именно, решает приложение.
        /// </summary>
        private void MarkFontWarning(ICollection<string> missing)
        {
            try
            {
                string? text = missing.Count == 0
                    ? null
                    : "Не установлены гарнитуры: " + string.Join(", ", missing)
                      + ".\nТекст показан подменой — разбивка строк и число страниц "
                      + "могут отличаться от авторских.\nСами шрифты в документе не "
                      + "изменены: вернётся гарнитура — вернётся и вид.";

                CoreServices
                    .GetService<Writersword.Core.Interfaces.Services.UI.IModuleBadgeService>()
                    ?.SetWarning(moduleType, text);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Font warning mark failed");
            }
        }

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


                    // Каретку и прокрутку ставит само полотно, когда документ разложен.
                    // Вью после переключения вкладки или воркмода создаётся заново, и
                    // раскладка готовится ещё секунду-две: прокрутка, выставленная сразу,
                    // упиралась в пустой холст и прижималась к началу документа.
                    var canvas = _lastCreatedView?.FindControl<DocumentCanvas>("PageCanvas");

                    // Место из данных программы свежее сессии: сессия лежит в кеше,
                    // который на диск пишется только при правках, а место — после
                    // каждой остановки прокрутки и каретки.
                    if (StoredEditingPlace() is { } stored)
                        canvas?.RestoreViewState(stored.Para, stored.Char, stored.ScrollY);
                    else
                        canvas?.RestoreViewState(docParaIdx, charIdx, scrollY);

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

            _logger.Debug("[VIEW] Место в документе запомнено: абзац {Para}, символ {Char}, прокрутка {Scroll:F0}",
                docParaIdx, charIdx, scrollY);

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

            if (id is "TextEditor.UndoRedo.Undo" or "TextEditor.UndoRedo.Redo")
                _logger.Debug(
                    "[UNDO-KEY] Горячая клавиша {Id}: холст в фокусе {Focused}, холст найден {Found}",
                    id, DocumentCanvas.FocusedInstance is not null, canvas is not null);

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
                    case "TextEditor.Format.CycleCase":
                        _viewModel.CycleCase(); return;
                    case "TextEditor.Format.SmallCaps":
                        _viewModel.ToggleSmallCaps(); return;
                    case "TextEditor.Format.HiddenText":
                        _viewModel.ToggleHiddenText(); return;
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
                    case "TextEditor.Format.AlignDistribute":
                        _viewModel.SetAlignment(TextAlignment.Distribute); return;
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
                    case "TextEditor.View.FormattingMarks":
                        _viewModel.ToggleFormattingMarks(); return;

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
        // Правки окна настроек ложатся на живой набор, а не на тот, с которым окно
        // открылось: между открытием и «ОК» человек мог завести вид, переставить
        // список или спрятать лишнее — всё это живёт в этом же наборе.
        public object GetSettings()
            => _globalSettingsVm is null
                ? _globalSettings
                : _globalSettingsVm.ApplyTo(_globalSettings.Clone());

        public object GetLocalSettings()
            => _localSettingsVm is null
                ? _localSettings
                : _localSettingsVm.ApplyTo(_localSettings.Clone());

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
            l.BreakOnHyphen.GlobalValue = g.BreakOnHyphen.Value;
            l.BreakOnHyphen.Value = g.BreakOnHyphen.Value;
            l.SubstituteMissingGlyphs.GlobalValue = g.SubstituteMissingGlyphs.Value;
            l.SubstituteMissingGlyphs.Value = g.SubstituteMissingGlyphs.Value;
            l.SubstituteFontFamily.GlobalValue = g.SubstituteFontFamily.Value;
            l.SubstituteFontFamily.Value = g.SubstituteFontFamily.Value;
            _logger.Debug("ApplyGlobalToLocal completed");
        }

        public void PromoteLocalToGlobal()
        {
            if (_localSettingsVm is null) return;
            var settings = _localSettingsVm.ApplyTo(_localSettings.Clone());
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
            _localSettingsVm.BreakOnHyphen.PromoteToGlobal();
            _localSettingsVm.SubstituteMissingGlyphs.PromoteToGlobal();
            _localSettingsVm.SubstituteFontFamily.PromoteToGlobal();
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
                _globalSettingsVm.SubstituteMissingGlyphs.Value = settings.SubstituteMissingGlyphs;
                _globalSettingsVm.SubstituteFontFamily.Value = settings.SubstituteFontFamily;
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
            _globalSettingsVm.SubstituteMissingGlyphs.ResetToHardcoded();
            _globalSettingsVm.SubstituteFontFamily.ResetToHardcoded();
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
                _localSettingsVm.BreakOnHyphen.GlobalValue = _globalSettingsVm.BreakOnHyphen.Value;
                _localSettingsVm.SubstituteMissingGlyphs.GlobalValue = _globalSettingsVm.SubstituteMissingGlyphs.Value;
                _localSettingsVm.SubstituteFontFamily.GlobalValue = _globalSettingsVm.SubstituteFontFamily.Value;
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
            _localSettingsVm.BreakOnHyphen.ResetToGlobal();
            _localSettingsVm.SubstituteMissingGlyphs.ResetToGlobal();
            _localSettingsVm.SubstituteFontFamily.ResetToGlobal();
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
            _localSettingsVm.BreakOnHyphen.ResetToHardcoded();
            _localSettingsVm.SubstituteMissingGlyphs.ResetToHardcoded();
            _localSettingsVm.SubstituteFontFamily.ResetToHardcoded();
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
            SharedReadingSettingsSaved -= OnSharedReadingSettingsSaved;

            if (_hotKeyService is not null)
                _hotKeyService.UnbindExecutor(moduleType);

            if (_viewModel is not null)
            {
                _viewModel.PrintRequested -= OnPrintRequested;
                _viewModel.ContentEdited -= OnViewModelContentEdited;
                _viewModel.ChangedOutsideHistory -= OnViewModelChangedOutsideHistory;
            }

            _undoStack.StateChanged -= OnHistoryStateChanged;
            _textUndoStack.StateChanged -= OnHistoryStateChanged;

            if (_viewStateCanvas is not null)
            {
                _viewStateCanvas.ViewStateChanged -= OnCanvasViewStateChanged;
                _viewStateCanvas = null;
            }

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
            vm.ContentEdited += OnViewModelContentEdited;
            vm.ChangedOutsideHistory += OnViewModelChangedOutsideHistory;
            vm.GlobalSettingsChanged = SaveGlobalSettings;
            vm.LoadNewDocument(_localSettings);
            return vm;
        }

        /// <summary>
        /// Переносит предпочтения чтения из общих настроек в набор, восстановленный из
        /// файла проекта. Всё остальное в этом наборе — свойства самой рукописи и
        /// остаётся как есть.
        /// </summary>
        private static void CarryOverReadingPreferences(TextEditorSettings from, TextEditorSettings to)
        {
            to.ReadingFlow = from.ReadingFlow;
            to.ReadingSheetFormat = from.ReadingSheetFormat;
            to.ReadingThemeId = from.ReadingThemeId;
            to.ReadingShowPageNumbers = from.ReadingShowPageNumbers;
            to.ReadingScaleContent = from.ReadingScaleContent;
            to.ReadingFontStep = from.ReadingFontStep;
            to.ReadingFontFamily = from.ReadingFontFamily;
            to.ReadingThemes = from.ReadingThemes;
            to.HiddenReadingThemeIds = from.HiddenReadingThemeIds;
            to.ReadingThemeOrder = from.ReadingThemeOrder;
        }

        /// <summary>
        /// Переносит «Мои эффекты» из общих настроек в другой набор. Наборы — вещь
        /// человека, как и виды чтения: заведённый в одном проекте нужен во всех.
        /// </summary>
        private static void CarryOverTextEffectPresets(TextEditorSettings from, TextEditorSettings to)
        {
            to.TextEffectPresets = from.TextEffectPresets;
        }

        /// <summary>
        /// Переносит предпочтения вкладки «Вид» из одного набора в другой: вид листа
        /// при правке и его рабочую копию, что убирать в фокусе, свёрнутость ленты,
        /// строку состояния и цвет каретки. Всё это общее для всех документов —
        /// вкладка записывает его в общие настройки при каждой правке.
        ///
        /// Линейка и масштаб сюда не входят: у них есть значение проекта в окне
        /// настроек, и перекрывать его общим значением нельзя.
        ///
        /// Рабочая копия вида клонируется: два редактора не должны держать один и
        /// тот же объект — ползунок в одном менял бы лист другого в обход записи.
        /// </summary>
        private static void CarryOverEditorViewPreferences(TextEditorSettings from, TextEditorSettings to)
        {
            to.EditorThemeEnabled = from.EditorThemeEnabled;
            to.EditorThemeId = from.EditorThemeId;
            to.EditorTheme = from.EditorTheme?.Clone();
            to.FocusHidesRuler = from.FocusHidesRuler;
            to.FocusHidesStatusBar = from.FocusHidesStatusBar;
            to.FocusRibbonOnHover = from.FocusRibbonOnHover;
            to.RibbonCollapsed = from.RibbonCollapsed;
            to.ShowStatusBar = from.ShowStatusBar;
            to.CaretColor = from.CaretColor;
        }

        /// <summary>
        /// Совпадают ли в двух наборах предпочтения вкладки «Вид». Нужно, чтобы не
        /// перерисовывать чужой редактор, когда общие настройки записаны по другому
        /// поводу: смена подачи или вида чтения его листа не касается.
        /// </summary>
        private static bool SameEditorViewPreferences(TextEditorSettings a, TextEditorSettings b)
            => a.EditorThemeEnabled == b.EditorThemeEnabled
               && string.Equals(a.EditorThemeId, b.EditorThemeId, StringComparison.Ordinal)
               && ReadingTheme.SameLook(a.EditorTheme, b.EditorTheme)
               && a.FocusHidesRuler == b.FocusHidesRuler
               && a.FocusHidesStatusBar == b.FocusHidesStatusBar
               && a.FocusRibbonOnHover == b.FocusRibbonOnHover
               && a.RibbonCollapsed == b.RibbonCollapsed
               && a.ShowStatusBar == b.ShowStatusBar
               && string.Equals(a.CaretColor ?? string.Empty, b.CaretColor ?? string.Empty,
                                StringComparison.OrdinalIgnoreCase);

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
                _logger.Warning(ex, "Failed to save the module shared settings");
            }

            // Остальные открытые редакторы узнают о записи сразу же.
            SharedReadingSettingsSaved?.Invoke(this, settings);
        }

        /// <summary>
        /// Общие настройки записаны одним из редакторов. Модуль заводится на каждый
        /// открытый проект, и у каждого своя копия общих настроек — снятая из
        /// хранилища в тот момент, когда он создавался.
        ///
        /// Без этого события виды чтения терялись. Вид, заведённый в одном проекте,
        /// попадал в хранилище, а редактор другого проекта продолжал держать список
        /// без него. Стоило второму записать общие настройки по любому поводу — сменить
        /// подачу, передвинуть ползунок света, открыть и закрыть окно видов, — и в
        /// хранилище уходил его устаревший список: новый вид пропадал, спрятанные
        /// снова появлялись, порядок откатывался. Со стороны это и выглядело как «виды
        /// сами исчезают».
        ///
        /// Теперь каждая запись разносится по всем редакторам: они перенимают виды и
        /// предпочтения чтения из только что записанного набора, и следующая запись
        /// любого из них несёт уже полный список.
        /// </summary>
        private static event Action<TextEditorModule, TextEditorSettings>? SharedReadingSettingsSaved;

        private void OnSharedReadingSettingsSaved(TextEditorModule sender, TextEditorSettings saved)
        {
            if (ReferenceEquals(sender, this)) return;

            try
            {
                // Своя копия: списки видов не должны быть общими объектами двух
                // редакторов — правка в одном меняла бы другой в обход записи.
                var snapshot = saved.Clone();

                // Вид листа при правке разносится тем же путём, что и виды чтения.
                // Без этого редактор другого проекта держал прежний вид в своей копии
                // общих настроек и при первой же своей записи — масштаб, режим,
                // подача чтения — возвращал его в хранилище: выбранный вид «слетал»
                // и после перезапуска, и при переходе в другой проект.
                bool editorViewChanged = !SameEditorViewPreferences(snapshot, _localSettings);

                CarryOverReadingPreferences(snapshot, _globalSettings);
                CarryOverEditorViewPreferences(snapshot, _globalSettings);
                CarryOverTextEffectPresets(snapshot, _globalSettings);
                if (!ReferenceEquals(_localSettings, _globalSettings))
                {
                    CarryOverReadingPreferences(snapshot, _localSettings);
                    CarryOverEditorViewPreferences(snapshot, _localSettings);
                    CarryOverTextEffectPresets(snapshot, _localSettings);
                }

                _viewModel?.RefreshSharedReadingThemes();
                _viewModel?.RefreshSharedTextEffectPresets();
                if (editorViewChanged) _viewModel?.RefreshSharedEditorView();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to take over the shared reading settings");
            }
        }
    }
}