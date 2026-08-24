using ReactiveUI;
using ReactiveUI.Avalonia;
using Serilog;
using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Text;
using Writersword.Core.Models.Print;
using Writersword.Modules.TextEditor.Contracts;
using Writersword.Modules.TextEditor.Models;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Page;
using Writersword.Modules.TextEditor.Models.Settings;
using Writersword.Modules.TextEditor.Models.Styles;
using Writersword.Modules.TextEditor.Services;
using Writersword.Modules.TextEditor.ViewModels.Blocks;
using Writersword.Modules.TextEditor.ViewModels.Components;
using Writersword.Modules.TextEditor.ViewModels.Reading;
using Writersword.Modules.TextEditor.ViewModels.StatusBar;
using Writersword.Modules.TextEditor.ViewModels.Toolbar;

namespace Writersword.Modules.TextEditor.ViewModels
{
    public sealed class TextEditorViewModel : ReactiveObject, ITextEditorCommandTarget, IReadingHost, IDisposable
    {
        private static readonly ILogger _logger = Log.ForContext<TextEditorViewModel>();

        private readonly DocumentSerializer _serializer;
        private readonly ChunkManager _chunkManager;
        private readonly DeltaHashService _hashService;
        private readonly AutoReplaceService _autoReplace;
        private readonly SpellCheckService _spellCheck;
        private readonly ExportService _exportService;

        private IDisposable? _autoSaveSubscription;
        private IDisposable? _paragraphsSubscription;
        private bool _disposed;
        private bool _isModified;
        private DocumentViewModel? _documentViewModel;
        private double _monitorSizeInches;

        // ── Public properties ─────────────────────────────────────────────

        public DocumentViewModel? DocumentViewModel
        {
            get => _documentViewModel;
            private set => this.RaiseAndSetIfChanged(ref _documentViewModel, value);
        }

        public RibbonViewModel Ribbon { get; }
        public StatusBarViewModel StatusBar { get; }

        // ── Чтение ────────────────────────────────────────────────────────

        private bool _isReadingMode;

        /// <summary>
        /// Открыт режим чтения — любой подачей. Пока он открыт, лента редактора,
        /// линейки и строка состояния скрыты: чтение отличается от прочих режимов не
        /// картинкой, а тем, что на экране остаётся только текст и лента чтения.
        /// </summary>
        public bool IsReadingMode
        {
            get => _isReadingMode;
            private set => this.RaiseAndSetIfChanged(ref _isReadingMode, value);
        }

        private bool _isPagedReading;

        /// <summary>Чтение страницами: разворот или одиночный лист.</summary>
        public bool IsPagedReading
        {
            get => _isPagedReading;
            private set => this.RaiseAndSetIfChanged(ref _isPagedReading, value);
        }

        /// <summary>Лента чтения. Живёт всё время, показывается только в чтении.</summary>
        public ReadingRibbonViewModel ReadingRibbon { get; }

        /// <summary>Настройки чтения. Правятся лентой, применяются канвасом.</summary>
        public Models.Settings.ReadingSettings? Reading => DocumentViewModel?.Reading;

        /// <summary>Обновляет подпись страниц. Зовёт канвас после листания.</summary>
        public void UpdateSpreadPageLabel(int firstPage, int totalPages)
        {
            if (totalPages <= 0)
            {
                ReadingRibbon.PageLabel = string.Empty;
                ReadingRibbon.SetPageState(1, 1);
                return;
            }

            // Поле ввода и ползунок берут состояние отсюда же: место в книге одно, и
            // хранить его в двух местах значит однажды их рассогласовать.
            ReadingRibbon.SetPageState(firstPage, totalPages);

            int second = Math.Min(firstPage + 1, totalPages);
            bool pair = ReadingRibbon.Flow == Models.Settings.ReadingFlow.Spread && second > firstPage;

            ReadingRibbon.PageLabel = pair
                ? $"{firstPage}–{second} из {totalPages}"
                : $"{firstPage} из {totalPages}";
        }

        /// <summary>Выход из чтения обратно к страницам.</summary>
        public void ExitReading()
        {
            if (DocumentViewModel is null) return;

            // Полноэкранное чтение отпускается вместе с самим чтением: окно, оставшееся
            // без интерфейса после выхода из книги, выглядит поломкой, а не режимом.
            if (Reading is { Fullscreen: true } fs)
            {
                fs.Fullscreen = false;
                FullscreenRequested?.Invoke(false);
                ReadingRibbon.RefreshAll();
            }

            // Подача не сбрасывается: выбранный вид чтения — предпочтение человека,
            // и в следующий раз книга должна открыться так же, как он её оставил.
            StatusBar.ViewMode = EditorViewMode.Page;
            RefreshSpreadState();
        }

        /// <summary>
        /// Переносит предпочтения чтения из общих настроек в живые настройки документа.
        /// Зовётся при загрузке: подача, вид и прочее — личное дело читателя, и ждать
        /// их он будет в любом документе, а не только в том, где однажды выбрал.
        /// </summary>
        private void ApplyReadingPreferences(Models.Settings.ReadingSettings target)
        {
            target.Flow = Settings.ReadingFlow;
            target.Format = Settings.ReadingSheetFormat;
            target.ThemeId = string.IsNullOrWhiteSpace(Settings.ReadingThemeId)
                ? Models.Settings.ReadingTheme.CreamId
                : Settings.ReadingThemeId;
            target.Active = FindReadingTheme(target.ThemeId).Clone();
            target.ShowPageNumbers = Settings.ReadingShowPageNumbers;
            target.ScaleContent = Settings.ReadingScaleContent;
            target.FontStep = Math.Clamp(Settings.ReadingFontStep,
                Models.Settings.ReadingSettings.MinFontStep,
                Models.Settings.ReadingSettings.MaxFontStep);
        }

        /// <summary>
        /// Запоминает предпочтения чтения. Зовётся лентой после каждой правки, которая
        /// должна пережить не только перезапуск, но и переход в другой документ.
        ///
        /// Приближение книги сюда не попадает намеренно: его тянут ползунком, и запись
        /// настроек на каждое его положение — это десятки записей в файл на одно
        /// движение руки. Оно остаётся в сессии проекта.
        /// </summary>
        public void PersistReadingPreferences()
        {
            if (Reading is not { } r) return;

            Settings.ReadingFlow = r.Flow;
            Settings.ReadingSheetFormat = r.Format;
            Settings.ReadingThemeId = r.ThemeId;
            Settings.ReadingShowPageNumbers = r.ShowPageNumbers;
            Settings.ReadingScaleContent = r.ScaleContent;
            Settings.ReadingFontStep = r.FontStep;

            GlobalSettingsChanged?.Invoke(Settings);
        }

        /// <summary>
        /// Применяет настройки чтения, восстановленные из сессии. Поля копируются в
        /// живой объект, а не подменяют его: на него уже подписан канвас.
        /// </summary>
        public void ApplyRestoredReadingSettings(Models.Settings.ReadingSettings restored)
        {
            if (DocumentViewModel is null) return;

            var target = DocumentViewModel.Reading;
            target.Flow = restored.Flow;
            target.Format = restored.Format;
            target.ThemeId = string.IsNullOrWhiteSpace(restored.ThemeId)
                ? Models.Settings.ReadingTheme.CreamId
                : restored.ThemeId;

            // Рабочая копия вида берётся из сессии, а если её там нет (данные прежних
            // версий) — из вида по опознавателю.
            target.Active = restored.Active?.Clone()
                ?? FindReadingTheme(target.ThemeId).Clone();

            target.Active.Brightness = Math.Clamp(target.Active.Brightness, 0.35, 1.0);
            target.Active.Contrast = Math.Clamp(target.Active.Contrast, 0.6, 1.6);
            target.Active.Warmth = Math.Clamp(target.Active.Warmth, 0.0, 1.0);

            target.FontStep = Math.Clamp(restored.FontStep,
                Models.Settings.ReadingSettings.MinFontStep,
                Models.Settings.ReadingSettings.MaxFontStep);
            target.Zoom = Math.Clamp(restored.Zoom,
                Models.Settings.ReadingSettings.MinZoom,
                Models.Settings.ReadingSettings.MaxZoom);
            target.ScaleContent = restored.ScaleContent;
            target.ShowPageNumbers = restored.ShowPageNumbers;
            target.RibbonExpanded = restored.RibbonExpanded;

            // Полноэкранный режим намеренно не восстанавливается: окно, само собой
            // раскрывшееся на весь экран при запуске, пугает больше, чем помогает.
            target.Fullscreen = false;

            RefreshSpreadState();
            ReadingRibbon.RefreshAll();
        }

        // ── Виды чтения ───────────────────────────────────────────────────

        /// <summary>
        /// Все виды, доступные сейчас: встроенные, приложенные к документу и общие
        /// для всех проектов. Один и тот же вид может лежать сразу в двух местах —
        /// тогда в списке он один, но помечен обеими областями.
        /// </summary>
        public System.Collections.Generic.IReadOnlyList<Models.Settings.ReadingTheme> ReadingThemes()
        {
            var result = new System.Collections.Generic.List<Models.Settings.ReadingTheme>();
            var byId = new System.Collections.Generic.Dictionary<string, Models.Settings.ReadingTheme>(
                StringComparer.Ordinal);

            foreach (var builtIn in Models.Settings.ReadingTheme.BuiltIn)
            {
                var copy = builtIn.Clone();
                result.Add(copy);
                byId[copy.Id] = copy;
            }

            var docThemes = DocumentViewModel?.Document.ReadingThemes;
            if (docThemes is not null)
            {
                foreach (var theme in docThemes)
                {
                    if (byId.TryGetValue(theme.Id, out var existing))
                    {
                        existing.InDocument = true;
                        continue;
                    }

                    var copy = theme.Clone();
                    copy.InDocument = true;
                    result.Add(copy);
                    byId[copy.Id] = copy;
                }
            }

            foreach (var theme in Settings.ReadingThemes)
            {
                if (byId.TryGetValue(theme.Id, out var existing))
                {
                    existing.IsGlobal = true;
                    continue;
                }

                var copy = theme.Clone();
                copy.IsGlobal = true;
                result.Add(copy);
                byId[copy.Id] = copy;
            }

            return result;
        }

        /// <summary>Вид по опознавателю. Не нашёлся — встроенный кремовый.</summary>
        public Models.Settings.ReadingTheme FindReadingTheme(string? id)
        {
            foreach (var theme in ReadingThemes())
                if (string.Equals(theme.Id, id, StringComparison.Ordinal)) return theme;

            return Models.Settings.ReadingTheme.FindBuiltIn(id);
        }

        /// <summary>
        /// Раскладывает виды по хранилищам согласно их областям. Встроенные никуда не
        /// пишутся — они есть всегда. Вид, у которого снята область, из этого
        /// хранилища уходит.
        /// </summary>
        public void SaveReadingThemes(
            System.Collections.Generic.IReadOnlyList<Models.Settings.ReadingTheme> themes)
        {
            var doc = DocumentViewModel?.Document;

            var forDocument = new System.Collections.Generic.List<Models.Settings.ReadingTheme>();
            var forGlobal = new System.Collections.Generic.List<Models.Settings.ReadingTheme>();

            foreach (var theme in themes)
            {
                if (theme.IsBuiltIn) continue;
                if (theme.InDocument) forDocument.Add(theme.Clone());
                if (theme.IsGlobal) forGlobal.Add(theme.Clone());
            }

            if (doc is not null)
                doc.ReadingThemes = forDocument.Count > 0 ? forDocument : null;

            Settings.ReadingThemes = forGlobal;

            // Список правился, а выбранный вид мог быть переименован или удалён —
            // рабочая копия обновляется по опознавателю.
            if (Reading is { } reading)
            {
                var current = FindReadingTheme(reading.ThemeId);
                reading.ThemeId = current.Id;
            }

            ReadingRibbon.RefreshAll();
            DocumentViewModel?.RaiseReadingSettingsChanged();

            // Документ изменился — правка видов такая же правка, как любая другая.
            if (forDocument.Count > 0 || doc?.ReadingThemes is not null) IsModified = true;

            GlobalSettingsChanged?.Invoke(Settings);
        }

        /// <summary>Открывает окно видов чтения.</summary>
        public void OpenReadingThemes() => ReadingThemesRequested?.Invoke();

        /// <summary>Просьба показать окно видов чтения. Исполняет вью.</summary>
        public Action? ReadingThemesRequested { get; set; }

        /// <summary>
        /// Общие настройки модуля изменились и их пора сохранить. Модуль знает, куда
        /// их писать; вью-модель — нет.
        /// </summary>
        public Action<TextEditorSettings>? GlobalSettingsChanged { get; set; }

        // ── Договор с лентой чтения (IReadingHost) ────────────────────────

        /// <summary>
        /// Правка требует пересборки раскладки: другой лист, другой шрифт, другая
        /// подача, другой масштаб содержимого.
        /// </summary>
        public void ApplyReadingLayout()
        {
            RefreshSpreadState();
            DocumentViewModel?.RaiseReadingSettingsChanged();
        }

        /// <summary>Правка видна сразу: свет, цвета, приближение книги, номера страниц.</summary>
        public void ApplyReadingVisual()
        {
            DocumentViewModel?.RaiseReadingVisualChanged();
        }

        /// <summary>Листание: -1 назад, +1 вперёд.</summary>
        public void TurnReadingPage(int direction) => ReadingTurnRequested?.Invoke(direction);

        /// <summary>К началу книги.</summary>
        public void GoReadingFirst() => ReadingGoToRequested?.Invoke(0);

        /// <summary>К концу книги. Отрицательный индекс канвас понимает как последнюю.</summary>
        public void GoReadingLast() => ReadingGoToRequested?.Invoke(-1);

        /// <summary>Открыть книгу на странице с указанным номером, считая с нуля.</summary>
        public void GoReadingPage(int pageIndex, bool animate)
            => ReadingGoToPageRequested?.Invoke(pageIndex, animate);

        /// <summary>Разворачивает модуль на весь экран и обратно.</summary>
        public void ApplyReadingFullscreen(bool on) => FullscreenRequested?.Invoke(on);

        /// <summary>
        /// Просьба к окну развернуться во весь экран или вернуться. Модуль не знает,
        /// как устроена оболочка приложения, поэтому решение принимает вью.
        /// </summary>
        public Action<bool>? FullscreenRequested { get; set; }

        /// <summary>Просьба перелистнуть книгу. Исполняет канвас через вью.</summary>
        public Action<int>? ReadingTurnRequested { get; set; }

        /// <summary>
        /// Просьба открыть книгу на странице: 0 — первая, отрицательное — последняя,
        /// иначе номер с нуля. Исполняет канвас через вью.
        /// </summary>
        public Action<int>? ReadingGoToRequested { get; set; }

        /// <summary>
        /// Просьба открыть книгу на странице с указанным номером (с нуля) — с
        /// анимацией перехода или без неё. Исполняет канвас через вью.
        /// </summary>
        public Action<int, bool>? ReadingGoToPageRequested { get; set; }

        private void RefreshSpreadState()
        {
            var mode = DocumentViewModel?.ViewMode ?? EditorViewMode.Page;
            bool reading = mode == EditorViewMode.Reading;
            bool paged = DocumentViewModel?.IsSpreadReading ?? false;

            IsReadingMode = reading;
            IsPagedReading = paged;

            // Горизонтальная линейка нужна везде, где есть текстовая колонка: отступы
            // абзаца и табуляции работают в любом режиме. В чтении её нет вместе со
            // всем остальным интерфейсом.
            //
            // Вертикальная показывает поля страницы, а в потоковых режимах страниц не
            // существует — там она мерила бы то, чего нет.
            bool showRuler = Settings.ShowRuler;
            Ruler.IsVisible = !reading && showRuler;
            IsVerticalRulerVisible = mode == EditorViewMode.Page && showRuler;
        }

        private bool _isVerticalRulerVisible = true;

        /// <summary>Видна ли вертикальная линейка — только в режиме страниц.</summary>
        public bool IsVerticalRulerVisible
        {
            get => _isVerticalRulerVisible;
            private set => this.RaiseAndSetIfChanged(ref _isVerticalRulerVisible, value);
        }

        /// <summary>
        /// ViewModel линейки — горизонтальной и вертикальной.
        /// </summary>
        public RulerViewModel Ruler { get; }

        public TextEditorSettings Settings { get; private set; } = new();

        public double MonitorSizeInches
        {
            get => _monitorSizeInches;
            private set => this.RaiseAndSetIfChanged(ref _monitorSizeInches, value);
        }

        public bool IsModified
        {
            get => _isModified;
            set => this.RaiseAndSetIfChanged(ref _isModified, value);
        }

        // ── События ───────────────────────────────────────────────────────

        public event Action<DocumentModel, TextEditorPageSettings>? PrintRequested;

        // ── Constructor ───────────────────────────────────────────────────

        public TextEditorViewModel()
        {
            _hashService = new DeltaHashService();
            _chunkManager = new ChunkManager(_hashService);
            _serializer = new DocumentSerializer(_hashService, _chunkManager);
            _autoReplace = new AutoReplaceService();
            _spellCheck = new SpellCheckService();
            _exportService = new ExportService();

            Ribbon = new RibbonViewModel(this);
            StatusBar = new StatusBarViewModel();
            Ruler = new RulerViewModel();
            ReadingRibbon = new ReadingRibbonViewModel(this);

            // Подписки на события линейки.
            Ruler.IndentMarkerChanged += OnRulerIndentMarkerChanged;
            Ruler.IndentDragStarted += OnIndentDragStarted;
            Ruler.IndentDragEnded += OnIndentDragEnded;
            Ruler.AllColumnWidthsChanged += OnRulerAllColumnWidthsChanged;
            Ruler.AllColumnWidthsChanging += OnRulerAllColumnWidthsChanging;
            Ruler.MarginDragStarted += OnRulerMarginDragStarted;
            Ruler.MarginChanged += OnRulerMarginChanged;
            Ruler.MarginCommitted += OnRulerMarginCommitted;

            // Левый край таблицы через линейку.
            Ruler.TableLeftEdgeChanging += OnRulerTableLeftEdgeChanging;
            Ruler.TableLeftEdgeChanged += OnRulerTableLeftEdgeChanged;

            Ruler.GetMinParagraphIndentMm = () =>
            {
                var doc = DocumentViewModel?.Document;
                if (doc is null) return double.MaxValue;
                double minPt = double.MaxValue;
                foreach (var section in doc.Sections)
                    foreach (var block in section.Blocks)
                        if (block is Writersword.Modules.TextEditor.Models.Document.ParagraphBlock p)
                        {
                            double li = p.Properties.LeftIndent ?? 0;
                            double fi = p.Properties.FirstLineIndent ?? 0;
                            double minIndent = Math.Min(li, li + fi);
                            if (minIndent < minPt) minPt = minIndent;
                        }
                return minPt == double.MaxValue ? double.MaxValue : minPt * 25.4 / 72.0;
            };

            Ruler.GetIndentUpperLimitUnits = GetRulerIndentUpperLimitUnits;
        }

        /// <summary>
        /// Правый предел для стрелок списка в единицах линейки. У двух стрелок он разный,
        /// потому что уводить текст на вторую строку вправе только метка.
        ///
        /// Метка (фиолетовая) двигает номер, а текст первой строки идёт за ним. Её предел —
        /// правый край зоны: чем правее метка, тем меньше места остаётся первой строке, и с
        /// какого-то момента раскладка отдаёт первую строку номеру целиком, а текст начинает
        /// со второй. Ничего под текст ей не резервируется — этот переход штатный.
        ///
        /// Абзацная стрелка задаёт лишь зазор между номером и текстом и такого перехода
        /// не делает — ей предел ставится там, где текст ещё помещается в строку. Иначе
        /// раскладка упирала строку в свой предел, стрелка уезжала дальше, и они расходились.
        /// </summary>
        private double? GetRulerIndentUpperLimitUnits(RulerIndentMarkerType type)
        {
            if (type != RulerIndentMarkerType.ListMarker
                && type != RulerIndentMarkerType.FirstLineIndent)
                return null;

            var lp = DocumentViewModel?.GetActiveListProperties();
            if (lp is null || lp.MarkerType == Models.Document.ListMarkerType.None)
                return null;

            const double PtToMm = 25.4 / 72.0;
            // Тот же минимум ширины первой строки, что и в раскладке (SKTextRenderer.BuildLayout).
            const double MinTextPt = 36.0;

            // Границы зоны абзаца линейка получает из раскладки (ApplyParagraphGeometry): для
            // ячейки это её контентный бокс, за полями и рамкой, поэтому вычитать поля здесь
            // уже не нужно — они в этих границах учтены.
            double availableMm = Ruler.Mode == RulerMode.Table
                ? Ruler.UnitsToMm(Ruler.ActiveCellRightUnits - Ruler.ActiveCellLeftUnits)
                : Ruler.PageWidthMm - Ruler.MarginLeftMm - Ruler.MarginRightMm;

            double rightIndentMm = Ruler.UnitsToMm(
                Ruler.GetIndentMarkerPosition(RulerIndentMarkerType.RightIndent));

            if (type == RulerIndentMarkerType.FirstLineIndent)
            {
                // Дальше этой точки текст первой строки не пускает раскладка.
                return Ruler.MmToUnits(availableMm - rightIndentMm - MinTextPt * PtToMm);
            }

            // Метка доходит до правого края зоны. Ни место под текст, ни ширину самого
            // номера не резервируем: чем правее метка, тем меньше места остаётся первой
            // строке, и с какого-то момента текст просто начинается со второй.
            return Ruler.MmToUnits(availableMm - rightIndentMm);
        }

        // ── Document loading ──────────────────────────────────────────────

        public void LoadDocument(DocumentModel document, TextEditorSettings settings)
        {
            Settings = settings ?? new TextEditorSettings();
            MonitorSizeInches = Settings.MonitorSizeInches;

            _logger.Debug("LoadDocument: MonitorSizeInches={V}", MonitorSizeInches);

            if (_documentViewModel is not null)
            {
                _documentViewModel.CursorContextChanged -= OnCursorContextChanged;
                _documentViewModel.DocumentRestored -= OnDocumentRestored;
            }

            // Старое представление картинки «в тексте» (отдельный блок в потоке) переводим
            // в новое (символ в строке) до создания вью-моделей: дальше по коду документ
            // должен быть уже в одном, актуальном виде.
            Services.InlineImageMigration.Migrate(document);

            var docVm = new DocumentViewModel(document, _chunkManager, _autoReplace, _spellCheck);
            docVm.CursorContextChanged += OnCursorContextChanged;
            docVm.DocumentRestored += OnDocumentRestored;

            // Символы-заполнители объектов, потерявшие свою картинку, убираются до
            // первой отрисовки: иначе в тексте виден квадратик с надписью OBJ, за
            // которым ничего нет.
            int danglingObjects = docVm.PurgeDanglingObjectChars();
            if (danglingObjects > 0)
                _logger.Information("Убрано пустых меток объектов в тексте: {N}", danglingObjects);

            // Зум может меняться не только ползунком, но и из канваса (Ctrl + колесо). Подписываемся
            // на DocVm.Zoom и подтягиваем ползунок и линейку. Петли нет: если значение уже совпадает,
            // StatusBar.Zoom не трогаем, поэтому ZoomChanged → SetZoom повторно не срабатывает.
            docVm.PropertyChanged += (_, ev) =>
            {
                if (ev.PropertyName != nameof(docVm.Zoom)) return;
                double z = docVm.Zoom;
                if (Math.Abs(StatusBar.Zoom - z) > 0.0001)
                    StatusBar.Zoom = z;
                Ruler.Zoom = z;
            };
            // Устанавливаем начальный активный параграф чтобы команды тулбара
            // (Bold, Italic и др.) работали без предварительного клика в канвас.
            if (docVm.Paragraphs.Count > 0)
                docVm.SetActiveParagraph(docVm.Paragraphs[0]);
            DocumentViewModel = docVm;

            _paragraphsSubscription?.Dispose();
            _paragraphsSubscription = SubscribeToParagraphChanges(docVm);

            // Подача чтения и всё, что к ней прилагается, берётся из общих настроек:
            // это предпочтение человека, а не свойство рукописи. Сессия проекта потом
            // может уточнить его своим сохранённым состоянием.
            ApplyReadingPreferences(docVm.Reading);

            StatusBar.IsSpellCheckActive = Settings.SpellCheckEnabled;
            StatusBar.Zoom = document.Zoom > 0 ? document.Zoom : Settings.DefaultZoom;
            DocumentViewModel?.SetZoom(StatusBar.Zoom);

            // Режим просмотра восстанавливается из документа так же, как зум. Без этого
            // статус-бар остаётся со своим дефолтом Page при документе в веб-режиме, и
            // кнопка «Страницы» перестаёт работать: её сеттер выходит по совпадению
            // значения, пока пользователь не переключится на другой режим и обратно.
            StatusBar.SyncViewMode(document.ViewMode);

            SyncRulerToDocument(document);
            Ruler.Zoom = StatusBar.Zoom;
            Ruler.Units = Settings.RulerUnits;
            Ruler.IsVisible = Settings.ShowRuler;
            Ruler.PagesPerRow = DocumentViewModel?.PagesPerRow ?? 1;

            StartAutoSave(Settings.AutoSaveIntervalSeconds);
            RefreshStatusBar();

            StatusBar.ViewModeChanged = mode =>
            {
                DocumentViewModel?.SetViewMode(mode);
                StatusBar.ViewMode = mode;
                RefreshSpreadState();
            };

            StatusBar.PagesPerRowChanged = pagesPerRow =>
            {
                if (DocumentViewModel is not null)
                    DocumentViewModel.PagesPerRow = pagesPerRow;
                Ruler.PagesPerRow = pagesPerRow;
            };

            StatusBar.ZoomChanged = zoom =>
            {
                DocumentViewModel?.SetZoom(zoom);
                Ruler.Zoom = zoom;
            };

            // Видимость линеек и панели чтения зависит от режима, который только что
            // восстановлен из документа.
            RefreshSpreadState();

            _logger.Debug("Document loaded: title={Title}", document.Title);
        }

        public void LoadNewDocument(TextEditorSettings settings)
        {
            LoadDocument(DocumentModel.CreateNew(), settings);
        }

        /// <summary>
        /// Применяет состояние вида, восстановленное из сессионных данных: режим
        /// отображения и число страниц в ряду. Документ, статус-бар и линейка
        /// обновляются одним проходом, иначе индикатор и канвас разъезжаются:
        /// сеттер режима в статус-баре выходит по совпадению значения, и кнопка
        /// нужного режима перестаёт реагировать на нажатие.
        /// </summary>
        public void ApplyRestoredViewState(EditorViewMode viewMode, int pagesPerRow)
        {
            if (DocumentViewModel is null) return;

            DocumentViewModel.SetViewMode(viewMode);
            StatusBar.SyncViewMode(viewMode);

            DocumentViewModel.PagesPerRow = pagesPerRow;
            StatusBar.SyncPagesPerRow(pagesPerRow);
            Ruler.PagesPerRow = pagesPerRow;

            RefreshSpreadState();
        }

        /// <summary>
        /// Убирает картинки, потерявшие свою страницу: целиком ушедшие мимо листа и
        /// закреплённые, вылезшие на соседнюю страницу. Вызывать ТОЛЬКО при штатном
        /// закрытии приложения — кеш восстановления обязан пережить аварийное
        /// завершение вместе со всеми картинками.
        /// Возвращает число удалённых.
        /// </summary>
        public int PurgeLostImagesBeforeExit()
        {
            if (DocumentViewModel is null) return 0;

            int removed = Services.OffPageImageCleanup.Purge(DocumentViewModel.Document);
            if (removed > 0)
            {
                _logger.Information("Удалено картинок вне страниц при закрытии: {N}", removed);
                DocumentViewModel.RaiseStructureChanged();
            }
            return removed;
        }

        public string? GetSerializedDocument()
        {
            if (DocumentViewModel is null) return null;

            // Картинки, чей символ удалён из текста, до этого момента жили в документе —
            // отмена удаления возвращала бы ссылку в никуда. В сохраняемый документ они
            // уже не нужны.
            DocumentViewModel.PurgeOrphanInlineObjects();

            return _serializer.Serialize(DocumentViewModel.Document);
        }

        public void ApplySettings(TextEditorSettings settings)
        {
            _logger.Debug("ApplySettings: MonitorSizeInches={V}", settings.MonitorSizeInches);
            Settings = settings;
            MonitorSizeInches = settings.MonitorSizeInches;
            Ruler.Units = settings.RulerUnits;

            // Настройка задаёт разрешение показывать линейки, а окончательное решение
            // принимает режим: в чтении их нет, вертикальной нет и в потоковых режимах.
            RefreshSpreadState();
        }

        // ── Cursor context ────────────────────────────────────────────────

        private void OnCursorContextChanged(CursorContext ctx)
        {
            Ribbon.Home.UpdateFromCursorContext(ctx);
            StatusBar.Language = ctx.Language ?? Settings.DefaultLanguage;

            // Стрелки линейки отсюда больше не ставятся. Их единственный источник —
            // NotifyRulerGeometry: фактическая геометрия абзаца, снятая с построенной
            // раскладки. Здесь величины приходят из модели, и каждую поправку, которую
            // раскладка сделала по-своему — ограничитель первой строки, перенос текста
            // списка на вторую строку, поля и рамку ячейки, — пришлось бы повторять
            // расчётом. Ровно на этом стрелки и расходились с текстом.
        }

        // ── Линейка ───────────────────────────────────────────────────────

        // После Undo/Redo снапшота документ мог получить другие поля страницы — линейка их не
        // отслеживает, поэтому пересинхронизируем её с восстановленными настройками.
        private void OnDocumentRestored()
        {
            if (DocumentViewModel is null) return;
            SyncRulerToDocument(DocumentViewModel.Document);
        }

        private void SyncRulerToDocument(DocumentModel document)
        {
            var ps = document.PageSettings;
            Ruler.UpdatePageSettings(
                widthMm: ps.GetPhysicalWidthMm(),
                heightMm: ps.GetPhysicalHeightMm(),
                marginLeftMm: ps.MarginLeftMm + ps.MarginGutterMm,
                marginRightMm: ps.MarginRightMm,
                marginTopMm: ps.MarginTopMm,
                marginBottomMm: ps.MarginBottomMm);
        }

        public void NotifyPageSettingsChanged()
        {
            if (DocumentViewModel is null) return;
            SyncRulerToDocument(DocumentViewModel.Document);
        }

        public void NotifyPageOffsetChanged(double pageOffsetXPx)
        {
            Ruler.PageOffsetXPx = pageOffsetXPx;
        }

        /// <summary>
        /// Фактическая геометрия абзаца под кареткой из раскладки — по ней линейка и ставит
        /// стрелки. Единственный путь, которым они двигаются вне перетаскивания.
        /// </summary>
        public void NotifyRulerGeometry(RulerParagraphGeometry geometry)
            => Ruler.ApplyParagraphGeometry(geometry);

        /// <summary>
        /// Переключает линейку в режим таблицы при входе каретки в таблицу.
        /// </summary>
        public void NotifyCaretEnteredTable(
            System.Collections.Generic.IReadOnlyList<double> columnOffsetsMm,
            System.Collections.Generic.IReadOnlyList<double> columnWidthsMm,
            double tableOffsetMm = 0,
            int activeColumnIndex = 0)
        {
            // Границы активной ячейки здесь не выставляются: их даёт NotifyRulerGeometry
            // по контентному боксу из раскладки. Прежний вызов писал края СТОЛБЦА, то есть
            // другую точку отсчёта, и стрелки прыгали между двумя вариантами.
            Ruler.UpdateTableColumns(columnOffsetsMm, columnWidthsMm, tableOffsetMm);
            Ribbon.IsTableTabVisible = true;
            // Синхронизируем кнопку-тоггл режима разбивки с текущей таблицей
            Ribbon.Table.SyncFromTarget();
        }

        /// <summary>
        /// Переключает линейку обратно в режим абзаца при выходе каретки из таблицы.
        /// </summary>
        public void NotifyCaretLeftTable()
        {
            Ruler.SwitchToParagraphMode();
            Ribbon.IsTableTabVisible = false;
        }

        // Индекс контекстной вкладки «Формат» (картинка) в TabControl риббона:
        // Home=0, Insert=1, Layout=2, References=3, Table=4, Image=5.
        private const int ImageTabIndex = 5;

        // Вкладка, активная до автопереключения на «Формат» — восстанавливается
        // при снятии выделения картинки.
        private int _tabIndexBeforeImage = -1;

        // Была ли видна «Работа с таблицами» до выделения картинки. Клик по картинке
        // каретку из таблицы не выводит, поэтому табличная вкладка сама не гасла и
        // висела рядом с «Форматом» — обе с акцентным заголовком, будто активны обе.
        private bool _tableTabBeforeImage;

        /// <summary>
        /// Показывает/скрывает контекстную вкладку «Формат» (работа с картинкой)
        /// при выделении/снятии выделения изображения на канвасе.
        /// При выделении вкладка активируется автоматически, при снятии —
        /// возвращается вкладка, которая была активна до этого.
        /// </summary>
        public void NotifyImageSelectionChanged(bool selected)
        {
            if (selected)
            {
                if (!Ribbon.IsImageTabVisible)
                {
                    _tabIndexBeforeImage = Ribbon.SelectedTabIndex;
                    _tableTabBeforeImage = Ribbon.IsTableTabVisible;
                    Ribbon.IsTableTabVisible = false;
                    Ribbon.IsImageTabVisible = true;
                    Ribbon.SelectedTabIndex = ImageTabIndex;
                }
                Ribbon.Image.SyncFromTarget();
            }
            else
            {
                bool wasOnImageTab = Ribbon.SelectedTabIndex == ImageTabIndex;
                Ribbon.IsImageTabVisible = false;
                Ribbon.IsTableTabVisible = _tableTabBeforeImage;
                if (wasOnImageTab)
                {
                    Ribbon.SelectedTabIndex =
                        _tabIndexBeforeImage >= 0 && _tabIndexBeforeImage != ImageTabIndex
                            ? _tabIndexBeforeImage
                            : 0;
                }
                _tabIndexBeforeImage = -1;
                _tableTabBeforeImage = false;
            }
        }

        private void OnIndentDragStarted() => DocumentViewModel?.BeginParagraphFormatBatch();
        private void OnIndentDragEnded() => DocumentViewModel?.EndParagraphFormatBatch();

        private void OnRulerIndentMarkerChanged(RulerIndentMarkerType markerType, double valueMm)
        {
            // Смещение зоны к значению НЕ прибавляется. Все отступы абзаца внутри ячейки —
            // левый, первой строки, правый и позиция метки списка — хранятся в модели
            // относительно самой ячейки, и маркеры линейки живут в координатах её зоны
            // (границы зоны приходят из раскладки, см. ApplyParagraphGeometry). То есть обе
            // стороны уже в одной системе отсчёта, и прибавка ломала запись. Ошибка не была
            // видна в первом столбце: там смещение равно нулю. В любом следующем в LeftIndent
            // уходило значение шире самой ячейки, текстовая зона схлопывалась, и текст
            // переставал переноситься.

            double valuePt = valueMm * 72.0 / 25.4;

            switch (markerType)
            {
                case RulerIndentMarkerType.LeftIndent:
                    {
                        // Читаем текущие позиции маркеров напрямую — они актуальны во время drag,
                        // тогда как LeftIndentMm/FirstLineIndentMm обновляются только вне drag.
                        double absFirstMm = Ruler.UnitsToMm(
                            Ruler.GetIndentMarkerPosition(RulerIndentMarkerType.FirstLineIndent));
                        double newLeftMm = valueMm;
                        double pageLeftMm = -Ruler.MarginLeftMm;
                        double newAbsFirstMm = Math.Max(absFirstMm, pageLeftMm);
                        double newFirstRelMm = newAbsFirstMm - newLeftMm;
                        DocumentViewModel?.SetLeftIndentPt(newLeftMm * 72.0 / 25.4);

                        // В списке нижняя стрелка задаёт отступ строк 2+ и на первую строку
                        // не влияет: её текст стоит от номера («метка + ширина + зазор»), эту
                        // величину считает раскладка. Поэтому абзацную стрелку не трогаем
                        // вовсе — ни новым значением, ни возвратом на старое. Подстановка
                        // TextIndentPt, стоявшая здесь, равнялась только что записанному левому
                        // отступу, и стрелка ездила ровно над нижней, ничего не двигая.
                        var lpL = DocumentViewModel?.GetActiveListProperties();
                        if (lpL is null || lpL.MarkerType == Models.Document.ListMarkerType.None)
                        {
                            DocumentViewModel?.SetFirstLineIndentPt(newFirstRelMm * 72.0 / 25.4);
                        }
                        break;
                    }
                case RulerIndentMarkerType.FirstLineIndent:
                    {
                        // В списке абзацная стрелка = начало текста (метка + ширина + зазор).
                        // Перетаскивание меняет ЗАЗОР между цифрой и текстом, а не отступ абзаца.
                        var lpFirst = DocumentViewModel?.GetActiveListProperties();
                        if (lpFirst is not null && lpFirst.MarkerType != Models.Document.ListMarkerType.None)
                        {
                            double markerAbsPt = lpFirst.MarkerIndentPt ?? 0.0;
                            double newGapPt = valuePt - markerAbsPt - lpFirst.ComputedMarkerWidthPt;
                            DocumentViewModel?.SetListMarkerGapPt(newGapPt);
                        }
                        else
                        {
                            double leftMm = Ruler.UnitsToMm(
                                Ruler.GetIndentMarkerPosition(RulerIndentMarkerType.LeftIndent));
                            DocumentViewModel?.SetFirstLineIndentPt((valuePt - leftMm * 72.0 / 25.4));
                        }
                        break;
                    }
                case RulerIndentMarkerType.RightIndent:
                    DocumentViewModel?.SetRightIndentPt(valuePt);
                    break;
                case RulerIndentMarkerType.ListMarker:
                    {
                        // Метка тащит пункт целиком: номер и текст едут вместе, зазор
                        // между ними сохраняется. Раньше писалась только позиция номера,
                        // текст стоял на месте, и перетаскивание меняло зазор — пункт
                        // не смещался, а растягивался.
                        var lpBefore = DocumentViewModel?.GetActiveListProperties();
                        // TextIndentPt допускает null — «позиция текста не задана явно».
                        // Подставляем ноль: дальше он служит только базой для сдвига.
                        double oldTextPt = lpBefore?.TextIndentPt ?? 0.0;
                        double oldMarkerPt = lpBefore?.MarkerIndentPt
                            ?? Math.Max(0.0, oldTextPt
                                             - Models.Document.ListProperties.DefaultHangingPt);

                        DocumentViewModel?.SetListMarkerIndentPt(valuePt);

                        // Сдвиг берём по ФАКТИЧЕСКИ применённой позиции: метка могла
                        // упереться в ограничение, и текст обязан остановиться с ней,
                        // иначе он уезжает дальше и зазор всё равно расползается.
                        var lpApplied = DocumentViewModel?.GetActiveListProperties();
                        if (lpBefore is not null && lpApplied is not null)
                        {
                            double appliedDeltaPt =
                                (lpApplied.MarkerIndentPt ?? oldMarkerPt) - oldMarkerPt;
                            if (Math.Abs(appliedDeltaPt) > 0.001)
                            {
                                // Текст едет за номером, но только пока ему остаётся место.
                                // Дальше номер идёт один: тексту в первой строке места нет,
                                // и раскладка уводит его на вторую. Без этого предела левый
                                // отступ доезжал вместе с меткой до правого края, ширина
                                // строки схлопывалась почти в ноль, и текст пропадал —
                                // в узкой ячейке это происходило сразу.
                                double newTextPt = oldTextPt + appliedDeltaPt;
                                if (GetRulerIndentUpperLimitUnits(
                                        RulerIndentMarkerType.FirstLineIndent) is double textLimitUnits)
                                {
                                    double textLimitPt = Ruler.UnitsToMm(textLimitUnits) * 72.0 / 25.4;
                                    newTextPt = Math.Min(newTextPt, textLimitPt);
                                }
                                DocumentViewModel?.SetListTextIndentPt(newTextPt);
                            }
                        }
                        // Абзацная стрелка встаёт по ФАКТИЧЕСКОЙ позиции текста, а не
                        // по пересчёту «метка + ширина цифры + зазор». Пересчёт был
                        // вторым источником правды: модель уже знает, где начинается
                        // текст, и два писателя в одно свойство на каждом движении мыши
                        // и давали дёрганье. Заодно стрелка автоматически упирается
                        // вместе с меткой — текст дальше неё не уехал.
                        var lpMk = DocumentViewModel?.GetActiveListProperties();
                        if (lpMk is not null)
                        {
                            const double PtToMm = 25.4 / 72.0;

                            // Абзацная стрелка = ФАКТИЧЕСКОЕ начало текста первой строки:
                            // отступ абзаца плюс сдвиг, который только что посчитала раскладка
                            // (запись выше синхронно пересобрала её). Формула «номер + ширина
                            // + зазор» о правилах раскладки не знала: когда текст упирался в
                            // свой предел или уходил на вторую строку, стрелка показывала
                            // точку, где текста нет, и уезжала следом за меткой.
                            Ruler.SetFirstLineMarkerMm(
                                ((lpMk.TextIndentPt ?? 0.0) + lpMk.ComputedFirstLineOffsetPt) * PtToMm);

                            // Нижняя стрелка едет за меткой: левый отступ ей метка и меняет.
                            // Без этого она весь жест стояла на прежнем месте и прыгала уже
                            // после отпускания, когда приходил контекст курсора.
                            Ruler.SetLeftMarkerMm((lpMk.TextIndentPt ?? 0.0) * PtToMm);
                        }
                        break;
                    }
            }

        }

        // Начало перетаскивания поля — делаем снапшот документа, чтобы Ctrl+Z вернул поля.
        private void OnRulerMarginDragStarted()
            => DocumentViewModel?.BeginPageEdit("Изменение полей страницы");

        private void OnRulerMarginChanged(double marginLeftMm, double marginRightMm)
        {
            if (DocumentViewModel is null) return;

            // Фиксируем Auto-колонки всех таблиц до изменения поля.
            // Без этого ComputeColumnWidths пересчитывает их под новую ширину текстовой зоны
            // и таблица визуально растягивается/сжимается.
            var ps = DocumentViewModel.Document.PageSettings;
            double oldTextWidthMm = ps.GetPhysicalWidthMm()
                - ps.MarginLeftMm - ps.MarginGutterMm - ps.MarginRightMm;
            double oldTextWidthPt = oldTextWidthMm * 72.0 / 25.4;
            FreezeAutoColumns(oldTextWidthPt);

            DocumentViewModel.SetPageMargins(
                Ruler.MarginTopMm, Ruler.MarginBottomMm,
                marginLeftMm, marginRightMm);

            double minIndentPt = -marginLeftMm * 72.0 / 25.4;
            bool changed = false;
            var doc = DocumentViewModel.Document;
            foreach (var section in doc.Sections)
                foreach (var block in section.Blocks)
                {
                    if (block is Writersword.Modules.TextEditor.Models.Document.ParagraphBlock p)
                    {
                        double li = p.Properties.LeftIndent ?? 0;
                        double fi = p.Properties.FirstLineIndent ?? 0;
                        if (li < minIndentPt)
                        {
                            p.Properties.LeftIndent = minIndentPt;
                            if (fi < 0 && li + fi < minIndentPt)
                                p.Properties.FirstLineIndent = 0;
                            changed = true;
                        }
                        else if (li + fi < minIndentPt)
                        {
                            p.Properties.FirstLineIndent = minIndentPt - li;
                            changed = true;
                        }
                    }
                    else if (block is TableBlock t)
                    {
                        // Таблица не может уйти левее левого края страницы.
                        if (t.LeftIndentPt < minIndentPt)
                        {
                            t.LeftIndentPt = minIndentPt;
                            changed = true;
                        }
                    }
                }

            if (changed)
                DocumentViewModel.FireParagraphFormatChanged();

            SyncRulerToDocument(DocumentViewModel.Document);
        }

        // Конвертирует все Auto-колонки всех таблиц в Fixed с текущими вычисленными значениями.
        // Вызывается перед изменением полей страницы чтобы таблицы не меняли размер.
        private void FreezeAutoColumns(double textWidthPt)
        {
            if (DocumentViewModel is null) return;
            foreach (var section in DocumentViewModel.Document.Sections)
                foreach (var block in section.Blocks)
                {
                    if (block is not TableBlock table) continue;
                    int colCount = table.Columns.Count;
                    if (colCount == 0) continue;

                    float usedPt = 0f;
                    int autoCount = 0;
                    var fixedPt = new float[colCount];

                    for (int i = 0; i < colCount; i++)
                    {
                        var col = table.Columns[i];
                        if (col.WidthType == TableColumnWidthType.Fixed)
                        {
                            fixedPt[i] = (float)(col.WidthValue * 72.0 / 25.4);
                            usedPt += fixedPt[i];
                        }
                        else if (col.WidthType == TableColumnWidthType.Percent)
                        {
                            fixedPt[i] = (float)(textWidthPt * col.WidthValue / 100.0);
                            usedPt += fixedPt[i];
                        }
                        else
                        {
                            autoCount++;
                        }
                    }

                    if (autoCount == 0) continue;

                    float autoWidth = (float)Math.Max(10.0, (textWidthPt - usedPt) / autoCount);
                    for (int i = 0; i < colCount; i++)
                    {
                        if (table.Columns[i].WidthType != TableColumnWidthType.Auto)
                            continue;
                        table.Columns[i].WidthType = TableColumnWidthType.Fixed;
                        table.Columns[i].WidthValue = autoWidth * 25.4 / 72.0;
                    }
                }
        }

        private void OnRulerMarginCommitted(double marginLeftMm, double marginRightMm)
        {
            if (DocumentViewModel is null) return;
            DocumentViewModel.SetPageMargins(
                Ruler.MarginTopMm, Ruler.MarginBottomMm,
                marginLeftMm, marginRightMm);
            SyncRulerToDocument(DocumentViewModel.Document);
            // Закрываем снапшот, начатый в OnRulerMarginDragStarted — теперь Ctrl+Z вернёт поля.
            DocumentViewModel.CommitPageEdit();
        }


        private void OnRulerAllColumnWidthsChanging(IReadOnlyDictionary<int, double> widths)
        {
            ApplyAllColumnWidths(widths);
        }

        private void OnRulerAllColumnWidthsChanged(IReadOnlyDictionary<int, double> widths)
        {
            ApplyAllColumnWidths(widths);
            _logger.Debug("All column widths changed: {Count} columns", widths.Count);
        }

        /// <summary>
        /// Применяет ширины ВСЕХ колонок активной таблицы одновременно.
        /// Это гарантирует что Auto-колонки не пересчитываются и занимают
        /// именно то место которое задано маркерами линейки.
        /// </summary>
        private void ApplyAllColumnWidths(IReadOnlyDictionary<int, double> widths)
        {
            if (DocumentViewModel is null) return;
            var table = DocumentViewModel.ActiveTable;
            if (table is null) return;

            foreach (var kv in widths)
                DocumentViewModel.TableSetColumnWidth(table, kv.Key, kv.Value);

            DocumentViewModel.FireParagraphFormatChanged();
        }

        /// <summary>
        /// Live-обновление отступа таблицы при drag левого края.
        /// Применяется к ActiveTable напрямую — делегат может быть null если каретка
        /// вышла из таблицы пока пользователь продолжает drag.
        /// </summary>
        private void OnRulerTableLeftEdgeChanging(double leftEdgeMm)
        {
            var table = DocumentViewModel?.ActiveTable;
            if (table is null) return;
            table.LeftIndentPt = leftEdgeMm * 72.0 / 25.4;
            DocumentViewModel?.FireParagraphFormatChanged();
        }

        /// <summary>
        /// Commit отступа таблицы при отпускании drag левого края.
        /// </summary>
        private void OnRulerTableLeftEdgeChanged(double leftEdgeMm)
        {
            var table = DocumentViewModel?.ActiveTable;
            if (table is null) return;
            table.LeftIndentPt = leftEdgeMm * 72.0 / 25.4;
            DocumentViewModel?.FireParagraphFormatChanged();
            _logger.Debug("Table left edge changed: {W}mm", leftEdgeMm);
        }

        // ── ITextEditorCommandTarget ──────────────────────────────────────

        public void ToggleBold() => DocumentViewModel?.ToggleBold();
        public void ToggleItalic() => DocumentViewModel?.ToggleItalic();
        public void ToggleUnderline() => DocumentViewModel?.ToggleUnderline();
        public void ToggleStrikethrough() => DocumentViewModel?.ToggleStrikethrough();
        public void ToggleSuperscript() => DocumentViewModel?.ToggleSuperscript();
        public void ToggleSubscript() => DocumentViewModel?.ToggleSubscript();
        public void ToggleAllCaps() => DocumentViewModel?.ToggleAllCaps();
        public void ChangeCase(Contracts.TextCaseMode mode) => DocumentViewModel?.ChangeCase(mode);
        public void ToggleSmallCaps() => DocumentViewModel?.ToggleSmallCaps();
        public void ClearFormatting() => DocumentViewModel?.ClearFormatting();

        public void SetTextColor(string c) => DocumentViewModel?.SetTextColor(c);
        public void SetHighlightColor(string? c) => DocumentViewModel?.SetHighlightColor(c);
        public void SetFontFamily(string f) => DocumentViewModel?.SetFontFamily(f);
        public void BeginFontPreview() => DocumentViewModel?.BeginFontPreview();
        public void PreviewFontFamily(string f) => DocumentViewModel?.PreviewFontFamily(f);
        public void EndFontPreview(bool commit) => DocumentViewModel?.EndFontPreview(commit);
        public void FocusEditor() => DocumentViewModel?.FocusEditor();
        public void SetFontSize(double s) => DocumentViewModel?.SetFontSize(s);
        public void IncreaseFontSize() => DocumentViewModel?.IncreaseFontSize();
        public void DecreaseFontSize() => DocumentViewModel?.DecreaseFontSize();

        public void SetAlignment(TextAlignment a) => DocumentViewModel?.SetAlignment(a);
        public void IncreaseIndent() => DocumentViewModel?.IncreaseIndent();
        public void DecreaseIndent() => DocumentViewModel?.DecreaseIndent();
        public void SetLineSpacing(double v) => DocumentViewModel?.SetLineSpacing(v);
        public void SetSpaceBefore(double pt) => DocumentViewModel?.SetSpaceBefore(pt);
        public void SetSpaceAfter(double pt) => DocumentViewModel?.SetSpaceAfter(pt);
        public void ApplyStyle(string name) => DocumentViewModel?.ApplyStyle(name);

        public ParagraphProperties? GetActiveParagraphProperties()
            => DocumentViewModel?.GetActiveParagraphProperties();
        public void ApplyParagraphSettings(ParagraphProperties settings)
            => DocumentViewModel?.ApplyParagraphSettings(settings);
        public void SetOutlineLevel(int level) => DocumentViewModel?.SetOutlineLevel(level);

        public void ToggleBulletList() => DocumentViewModel?.ToggleBulletList();
        public void ToggleNumberedList() => DocumentViewModel?.ToggleNumberedList();
        public void ToggleMultilevelList() => DocumentViewModel?.ToggleMultilevelList();
        public void ApplyListType(Writersword.Modules.TextEditor.Models.Document.ListMarkerType markerType)
            => DocumentViewModel?.ApplyListType(markerType);
        public void ApplyCustomBulletList(string marker) => DocumentViewModel?.ApplyCustomBulletList(marker);
        public Writersword.Modules.TextEditor.Models.Document.ListProperties? GetActiveListProperties()
            => DocumentViewModel?.GetActiveListProperties();
        public void ApplyListSettings(Writersword.Modules.TextEditor.Models.Document.ListProperties settings)
            => DocumentViewModel?.ApplyListSettings(settings);
        public void SetListMarkerIndentPt(double pt) => DocumentViewModel?.SetListMarkerIndentPt(pt);
        public void SetListTextIndentPt(double pt) => DocumentViewModel?.SetListTextIndentPt(pt);
        public void ApplyMultilevelList() => DocumentViewModel?.ApplyMultilevelList();
        public void ApplyMultilevelScheme(System.Collections.Generic.List<Writersword.Modules.TextEditor.Models.Document.ListMarkerType> scheme)
            => DocumentViewModel?.ApplyMultilevelScheme(scheme);

        public void Cut() => DocumentViewModel?.Cut();
        public void Copy() => DocumentViewModel?.Copy();
        public void Paste() => DocumentViewModel?.Paste();
        public void SelectAll() => DocumentViewModel?.SelectAll();
        public void Undo() => DocumentViewModel?.Undo();
        public void Redo() => DocumentViewModel?.Redo();

        public void InsertTable(int rows, int cols) => DocumentViewModel?.InsertTable(rows, cols);
        public void InsertImage(string path) => DocumentViewModel?.InsertImage(path);
        public void InsertShape(ShapeType st) => DocumentViewModel?.InsertShape(st);
        public void InsertFloatingTextBox() => DocumentViewModel?.InsertFloatingTextBox();
        public void InsertPageBreak() => DocumentViewModel?.InsertPageBreak();
        public void InsertSectionBreak(BreakType t) => DocumentViewModel?.InsertSectionBreak(t);
        public void InsertFootnote() => DocumentViewModel?.InsertFootnote();
        public void InsertEndnote() => DocumentViewModel?.InsertEndnote();
        public void InsertBookmark(string name) => DocumentViewModel?.InsertBookmark(name);
        public void InsertHyperlink(string url, string? text) => DocumentViewModel?.InsertHyperlink(url, text);
        public void InsertTOC() => DocumentViewModel?.InsertTOC();
        public void InsertComment(string text) => DocumentViewModel?.InsertComment(text);

        // ── Изображение ───────────────────────────────────────────────────
        public void SetImageWrapMode(WrapMode mode) => DocumentViewModel?.SetImageWrapMode(mode);
        public void SetImageWrapSide(WrapSide side) => DocumentViewModel?.SetImageWrapSide(side);
        public WrapSide? GetSelectedImageWrapSide() => DocumentViewModel?.GetSelectedImageWrapSide();
        public void SetImagePinnedPage(int page) => DocumentViewModel?.SetImagePinnedPage(page);
        public int? GetSelectedImagePinnedPage() => DocumentViewModel?.GetSelectedImagePinnedPage();
        public int? GetSelectedImageCurrentPage() => DocumentViewModel?.GetSelectedImageCurrentPage();
        public void SetImageLockAspect(bool locked) => DocumentViewModel?.SetImageLockAspect(locked);
        public void DeleteSelectedImage() => DocumentViewModel?.DeleteSelectedImage();
        public (WrapMode Wrap, bool LockAspect, Writersword.Modules.TextEditor.Models.Styles.TextAlignment Align)? GetSelectedImageInfo()
            => DocumentViewModel?.GetSelectedImageInfo();
        public void SetImageRotation(double degrees) => DocumentViewModel?.SetImageRotation(degrees);
        public double? GetSelectedImageRotation() => DocumentViewModel?.GetSelectedImageRotation();
        public void SetImageWidth(double widthPt) => DocumentViewModel?.SetImageWidth(widthPt);
        public void SetImageHeight(double heightPt) => DocumentViewModel?.SetImageHeight(heightPt);
        public void SetImageOpacity(double opacity) => DocumentViewModel?.SetImageOpacity(opacity);
        public void SetImageBorder(string? colorHex, double thicknessPt) => DocumentViewModel?.SetImageBorder(colorHex, thicknessPt);
        public (double WidthPt, double HeightPt, double Opacity, string? BorderColor, double BorderThicknessPt)? GetSelectedImageStyle()
            => DocumentViewModel?.GetSelectedImageStyle();
        public void ToggleImageFlipHorizontal() => DocumentViewModel?.ToggleImageFlipHorizontal();
        public void ToggleImageFlipVertical() => DocumentViewModel?.ToggleImageFlipVertical();
        public void SetImageCropMode(bool on) => DocumentViewModel?.SetImageCropMode(on);
        public bool GetImageCropMode() => DocumentViewModel?.GetImageCropMode() ?? false;
        public void SetImageWrapPadding(double topPt, double bottomPt, double leftPt, double rightPt)
            => DocumentViewModel?.SetImageWrapPadding(topPt, bottomPt, leftPt, rightPt);
        public (double TopPt, double BottomPt, double LeftPt, double RightPt)? GetSelectedImageWrapPadding()
            => DocumentViewModel?.GetSelectedImageWrapPadding();

        // ── Таблица ───────────────────────────────────────────────────────

        public void TableAddRow(bool above) => DocumentViewModel?.TableAddRow(above);
        public void TableAddColumn(bool left) => DocumentViewModel?.TableAddColumn(left);
        public void TableDeleteRow() => DocumentViewModel?.TableDeleteRow();
        public void TableDeleteColumn() => DocumentViewModel?.TableDeleteColumn();
        public void TableDelete() => DocumentViewModel?.TableDelete();

        public void TableMergeCells() => DocumentViewModel?.TableMergeCells();
        public void TableSplitCell() => DocumentViewModel?.TableSplitCell();
        public void TableDivideCell(bool vertical) => DocumentViewModel?.TableDivideCell(vertical);
        public void TableSetCellHAlign(Writersword.Modules.TextEditor.Models.Styles.TextAlignment align)
            => DocumentViewModel?.TableSetCellHAlign(align);
        public void TableSetCellVAlign(int vAlign) => DocumentViewModel?.TableSetCellVAlign(vAlign);
        public void TableSetCellPadding(double topPt, double bottomPt, double leftPt, double rightPt)
            => DocumentViewModel?.TableSetCellPadding(topPt, bottomPt, leftPt, rightPt);
        public (double TopPt, double BottomPt, double LeftPt, double RightPt)? TableGetCellPadding()
            => DocumentViewModel?.TableGetCellPadding();
        public void TableSetLineTool(int tool) => DocumentViewModel?.TableSetLineTool(tool);
        public int TableGetLineTool() => DocumentViewModel?.TableGetLineTool() ?? 0;
        public void TableSetCellAlign(int vAlign,
            Writersword.Modules.TextEditor.Models.Styles.TextAlignment hAlign)
            => DocumentViewModel?.TableSetCellAlign(vAlign, hAlign);
        public int? TableGetCellVAlign() => DocumentViewModel?.TableGetCellVAlign();
        public Writersword.Modules.TextEditor.Models.Styles.TextAlignment? TableGetCellHAlign()
            => DocumentViewModel?.TableGetCellHAlign();
        public void TableSetCellBackground(string? color) => DocumentViewModel?.TableSetCellBackground(color);
        public void TableSetCellBorder(string side, BorderStyle style, double thicknessPt, string? color)
            => DocumentViewModel?.TableSetCellBorder(side, style, thicknessPt, color);
        public void TableSetColumnWidth(double widthMm) => DocumentViewModel?.TableSetColumnWidth(widthMm);
        public void TableSetRowHeight(double heightPt) => DocumentViewModel?.TableSetRowHeight(heightPt);
        public void TableAutoFit() => DocumentViewModel?.TableAutoFit();
        public void TableDistributeColumns() => DocumentViewModel?.TableDistributeColumns();
        public void TableDistributeRows() => DocumentViewModel?.TableDistributeRows();
        public void TableSort(int columnIndex, bool ascending) => DocumentViewModel?.TableSort(columnIndex, ascending);

        public void TableToggleRepeatHeader()
        {
            var table = DocumentViewModel?.ActiveTable;
            if (table is null) return;
            table.RepeatHeader = !table.RepeatHeader;
            DocumentViewModel?.FireParagraphFormatChanged();
        }

        public bool TableGetRepeatHeader()
            => DocumentViewModel?.ActiveTable?.RepeatHeader ?? false;

        public void TableToggleSplitMode() => DocumentViewModel?.TableToggleSplitMode();
        public bool TableGetSplitModeByCell() => DocumentViewModel?.TableGetSplitModeByCell() ?? false;
        public void TableSetBreakLabel(string? text) => DocumentViewModel?.TableSetBreakLabel(text);
        public void TableSetContinuationLabel(string? text) => DocumentViewModel?.TableSetContinuationLabel(text);
        public string? TableGetBreakLabel() => DocumentViewModel?.TableGetBreakLabel();
        public string? TableGetContinuationLabel() => DocumentViewModel?.TableGetContinuationLabel();

        // ── Макет страницы ────────────────────────────────────────────────

        public void SetPageSize(PaperSize s) => DocumentViewModel?.SetPageSize(s);
        public void SetPageOrientation(PageOrientation o) => DocumentViewModel?.SetPageOrientation(o);

        public void SetPageMargins(double t, double b, double l, double r)
        {
            DocumentViewModel?.SetPageMargins(t, b, l, r);
            if (DocumentViewModel is not null)
                SyncRulerToDocument(DocumentViewModel.Document);
        }

        public void SetColumns(int c) => DocumentViewModel?.SetColumns(c);

        // ── Вид ───────────────────────────────────────────────────────────

        public void SetZoom(double zoom)
        {
            DocumentViewModel?.SetZoom(zoom);
            Ruler.Zoom = zoom;
        }

        public void SetViewMode(EditorViewMode m) => DocumentViewModel?.SetViewMode(m);
        public void ToggleFullscreen() => DocumentViewModel?.ToggleFullscreen();
        public void ToggleFocusMode() => DocumentViewModel?.ToggleFocusMode();
        public void SetCanvasTheme(CanvasThemePreset p) => DocumentViewModel?.SetCanvasTheme(p);
        public void SetCanvasColors(string bg, string tc) => DocumentViewModel?.SetCanvasColors(bg, tc);

        public void ZoomIn()
        {
            if (DocumentViewModel is null) return;
            double[] steps = { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 2.5, 3.0, 4.0, 5.0 };
            double current = StatusBar.Zoom;
            foreach (double step in steps)
                if (step > current + 0.01)
                {
                    DocumentViewModel.SetZoom(step);
                    StatusBar.Zoom = step;
                    Ruler.Zoom = step;
                    return;
                }
        }

        public void ZoomOut()
        {
            if (DocumentViewModel is null) return;
            double[] steps = { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 2.5, 3.0, 4.0, 5.0 };
            double current = StatusBar.Zoom;
            for (int i = steps.Length - 1; i >= 0; i--)
                if (steps[i] < current - 0.01)
                {
                    DocumentViewModel.SetZoom(steps[i]);
                    StatusBar.Zoom = steps[i];
                    Ruler.Zoom = steps[i];
                    return;
                }
        }

        public void ZoomReset()
        {
            if (DocumentViewModel is null) return;
            DocumentViewModel.SetZoom(1.0);
            StatusBar.Zoom = 1.0;
            Ruler.Zoom = 1.0;
        }

        // ── Инструменты ──────────────────────────────────────────────────

        public void OpenFind() => DocumentViewModel?.OpenFind();
        public void OpenFindReplace() => DocumentViewModel?.OpenFindReplace();
        public void RunSpellCheck() => DocumentViewModel?.RunSpellCheck();
        public void ShowWordCount() => DocumentViewModel?.ShowWordCount();

        public void Print()
        {
            if (DocumentViewModel is null) return;
            _logger.Debug("Print requested: title={Title}", DocumentViewModel.Document.Title);
            PrintRequested?.Invoke(
                DocumentViewModel.Document,
                DocumentViewModel.Document.PageSettings);
        }

        public void ExportToPdf() => DocumentViewModel?.ExportToPdf();
        public void ExportToDocx() => DocumentViewModel?.ExportToDocx();
        public void ExportToTxt() => DocumentViewModel?.ExportToTxt();
        public void ExportToMarkdown() => DocumentViewModel?.ExportToMarkdown();

        // ── Auto save ─────────────────────────────────────────────────────

        private void StartAutoSave(int intervalSeconds)
        {
            _autoSaveSubscription?.Dispose();
            _autoSaveSubscription = null;
            if (intervalSeconds <= 0) return;

            _autoSaveSubscription = Observable
                .Interval(TimeSpan.FromSeconds(intervalSeconds))
                .ObserveOn(AvaloniaScheduler.Instance)
                .Subscribe(_ => OnAutoSaveTick());
        }

        private void OnAutoSaveTick()
        {
            if (!IsModified || DocumentViewModel is null) return;
            _logger.Debug("Auto save tick");
            RefreshStatusBar();
        }

        private void RefreshStatusBar()
        {
            if (DocumentViewModel is null) return;

            var sb = new StringBuilder();
            int paraCount = 0;

            foreach (var section in DocumentViewModel.Document.Sections)
                foreach (var block in section.Blocks)
                    if (block is ParagraphBlock para)
                    {
                        sb.Append(para.GetPlainText()).Append(' ');
                        paraCount++;
                    }

            StatusBar.UpdateFromText(sb.ToString(), paraCount, pageCount: 1);
        }

        // ── IDisposable ───────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Ruler.IndentMarkerChanged -= OnRulerIndentMarkerChanged;
            Ruler.MarginDragStarted -= OnRulerMarginDragStarted;
            Ruler.AllColumnWidthsChanged -= OnRulerAllColumnWidthsChanged;
            Ruler.AllColumnWidthsChanging -= OnRulerAllColumnWidthsChanging;
            Ruler.MarginChanged -= OnRulerMarginChanged;
            Ruler.MarginCommitted -= OnRulerMarginCommitted;
            Ruler.TableLeftEdgeChanging -= OnRulerTableLeftEdgeChanging;
            Ruler.TableLeftEdgeChanged -= OnRulerTableLeftEdgeChanged;

            if (_documentViewModel is not null)
            {
                _documentViewModel.CursorContextChanged -= OnCursorContextChanged;
                _documentViewModel.DocumentRestored -= OnDocumentRestored;
            }

            _autoSaveSubscription?.Dispose();
            _paragraphsSubscription?.Dispose();
            _spellCheck.Dispose();

            // Явно очищаем параграфы чтобы не ждать GC.
            // Если вью всё ещё жив (Avalonia держит ссылку) — данные
            // освобождаются сразу, а не когда-то после сборки мусора.
            if (_documentViewModel is not null)
            {
                // Очищаем модельные данные документа (ParagraphBlock с TextChunk, Run).
                // Paragraphs.Clear() убирает только VM-обёртки, а сами блоки
                // остаются в Document.Sections[0].Blocks — именно они дают 1.4M объектов.
                var blocks = _documentViewModel.Document?.Sections?.Count > 0
                    ? _documentViewModel.Document.Sections[0].Blocks
                    : null;
                blocks?.Clear();
                _documentViewModel.Paragraphs.Clear();
                _documentViewModel = null;
            }
        }

        // ── Paragraph subscriptions ───────────────────────────────────────

        private IDisposable SubscribeToParagraphChanges(DocumentViewModel docVm)
        {
            // Вместо WhenAnyValue+Throttle на каждый параграф (5000 Rx-цепочек для большого документа)
            // используем один PropertyChanged обработчик + один DispatcherTimer для дебаунса.
            // Это сокращает количество Rx-объектов планировщика с ~25000 до 1.
            var debounce = new Avalonia.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };
            debounce.Tick += (_, _) =>
            {
                debounce.Stop();
                IsModified = true;
                RefreshStatusBar();
            };

            void OnParagraphChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (e.PropertyName != nameof(ParagraphViewModel.PlainText)) return;
                debounce.Stop();
                debounce.Start();
            }

            foreach (var pvm in docVm.Paragraphs)
                pvm.PropertyChanged += OnParagraphChanged;

            void OnCollectionChanged(object? sender,
                System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
            {
                if (e.NewItems is not null)
                    foreach (ParagraphViewModel pvm in e.NewItems)
                        pvm.PropertyChanged += OnParagraphChanged;
                if (e.OldItems is not null)
                    foreach (ParagraphViewModel pvm in e.OldItems)
                        pvm.PropertyChanged -= OnParagraphChanged;
            }

            docVm.Paragraphs.CollectionChanged += OnCollectionChanged;

            // Правки, не меняющие текст абзацев (обтекание и прочие свойства картинки,
            // поля страницы, форматирование), приходят отдельным событием: по PlainText
            // они не отслеживаются и без этого не попадали в сохранение.
            void OnContentModified()
            {
                // Только флаг. RefreshStatusBar здесь не вызывается: он склеивает текст
                // всего документа в StringBuilder, а свойства картинки на счётчики слов
                // не влияют — на большом документе это была бы полная пересборка текста
                // на каждый коммит правки картинки.
                IsModified = true;
            }

            docVm.ContentModified += OnContentModified;

            return System.Reactive.Disposables.Disposable.Create(() =>
            {
                debounce.Stop();
                docVm.ContentModified -= OnContentModified;
                docVm.Paragraphs.CollectionChanged -= OnCollectionChanged;
                foreach (var pvm in docVm.Paragraphs)
                    pvm.PropertyChanged -= OnParagraphChanged;
            });
        }
    }
}