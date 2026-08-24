using Avalonia.Input.Platform;
using Avalonia.Threading;
using ReactiveUI;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using Writersword.Core.Models.Print;
using Writersword.Core.Services;
using Writersword.Core.Interfaces.WorkFlows;
using Writersword.Modules.TextEditor.Contracts;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.Models.Page;
using Writersword.Modules.TextEditor.Models.Styles;
using Writersword.Modules.TextEditor.Services;
using Writersword.Modules.TextEditor.ViewModels.Blocks;
using Writersword.Modules.TextEditor.ViewModels.Toolbar;

namespace Writersword.Modules.TextEditor.ViewModels
{
    public sealed class DocumentViewModel : ReactiveObject, ITextEditorCommandTarget
    {
        private static readonly ILogger _log = Log.ForContext<DocumentViewModel>();

        private readonly DocumentModel _document;
        private readonly ChunkManager _chunkManager;
        private readonly AutoReplaceService _autoReplace;
        private readonly SpellCheckService _spellCheck;

        private EditorViewMode _viewMode;
        private double _zoom = 1.0;
        private bool _isFocusMode;
        private bool _isFullscreen;
        private bool _isReadOnly;

        private ParagraphViewModel? _activeParagraph;

        /// <summary>
        /// Активный параграф внутри ячейки таблицы.
        /// Устанавливается в FireTableCellCursorContext, сбрасывается в SetActiveParagraph.
        /// ApplyParaProperty применяет форматирование к нему когда каретка в таблице.
        /// </summary>
        public ParagraphBlock? TableActiveCellParagraph { get; private set; }

        // Текущее выделение внутри активного параграфа.
        // Устанавливается из DocumentCanvas перед каждым применением форматирования.
        private int _selectionStart;
        private int _selectionEnd;

        /// <summary>
        /// Устанавливает диапазон выделения для активного параграфа.
        /// Вызывается из DocumentCanvas перед применением форматирования.
        /// </summary>
        public void SetSelection(int start, int end)
        {
            _selectionStart = start;
            _selectionEnd = end;
        }

        /// <summary>
        /// Абзацы попавшие в текущее выделение (может быть несколько).
        /// </summary>
        public List<ParagraphViewModel> SelectionParagraphs { get; } = new();

        // ── Делегаты таблицы (устанавливаются DocumentCanvas) ─────────────
        public Action<bool>? TableAddRowDelegate { get; set; }
        public Action<bool>? TableAddColDelegate { get; set; }
        public Action? TableDeleteRowDelegate { get; set; }
        public Action? TableDeleteColDelegate { get; set; }
        public Action? TableDeleteDelegate { get; set; }

        /// <summary>
        /// Вызывается после вставки разрыва страницы с блоком-якорём новой страницы.
        /// DocumentCanvas подписывается и откладывает переход каретки до конца rebuild.
        /// </summary>
        public Action<ParagraphBlock>? OnPageBreakInserted { get; set; }

        // ── Делегат сдвига левого края таблицы ───────────────────────────
        /// <summary>
        /// Устанавливается DocumentCanvas при входе каретки в таблицу.
        /// Параметр — новый отступ таблицы в pt от начала текстовой области.
        /// </summary>
        public Action<double>? TableSetLeftEdgeDelegate { get; set; }

        // Устанавливается DocumentCanvas — пробрасывает вызов в UndoStack.
        public Action? UndoDelegate { get; set; }
        public Action? RedoDelegate { get; set; }

        // Делегаты для создания undo-снапшота форматирования.
        // Устанавливаются DocumentCanvas при подключении.
        public Action<string>? BeginEditDelegate { get; set; }
        public Action? CommitEditDelegate { get; set; }

        // Гранулярный коммит свойств рана (жирность/цвет/размер) через лёгкий TextUndoStack.
        // Канвас строит SetRunPropertyCommand на каждый диапазон и пушит одной командой.
        // Возвращает true, если обработал; иначе ApplyCharProperty идёт снапшотным путём.
        public Func<System.Collections.Generic.IReadOnlyList<(System.Guid ParaId, int From, int To)>,
            Action<RunProperties>, string, bool>? CommitRunPropertyGranularDelegate
        { get; set; }

        // Диапазоны всех выделенных абзацев текущей ячейки таблицы (Id, From, To). Позиция
        // выделения живёт в канвасе. Используется форматированием символов в ячейке, чтобы
        // применять к ВСЕМ выделенным абзацам ячейки, а не к одному активному.
        public Func<System.Collections.Generic.IReadOnlyList<(System.Guid ParaId, int From, int To)>>?
            GetCellSelectionRangesDelegate
        { get; set; }

        // Гранулярный коммит изменений текста с сохранением форматирования (смена регистра).
        // Канвас строит ChangeCaseCommand на каждый диапазон и пушит одной командой в TextUndoStack.
        // Возвращает true, если обработал; иначе ChangeCase идёт снапшотным запасным путём.
        public Func<System.Collections.Generic.IReadOnlyList<(System.Guid ParaId, int From, string OldText, string NewText)>,
            string, bool>? CommitTextEditsDelegate
        { get; set; }

        // Диапазон слова под кареткой: параграф и границы [From, To) в его тексте.
        // Позиция каретки живёт в канвасе, DocVm её не знает. Используется сменой
        // регистра без выделения — команда применяется к текущему слову (как в Word).
        public Func<(ParagraphViewModel Pvm, int From, int To)?>? GetCaretWordRangeDelegate
        { get; set; }

        /// <summary>
        /// Абзац и позиция каретки в нём. Заполняет канвас: только он знает точную
        /// каретку, включая абзацы внутри ячеек таблицы, которых нет в Blocks.
        /// Используется вставкой картинки в строку — она встаёт ровно под курсор.
        /// </summary>
        public Func<(ParagraphBlock Para, int CharIndex)?>? GetCaretTargetDelegate
        { get; set; }

        // Гранулярный коммит свойств абзаца (выравнивание/отступы/интервалы) через TextUndoStack.
        // Канвас строит SetParagraphPropertyCommand на каждый абзац и пушит одной командой.
        // Возвращает true, если обработал; иначе ApplyParaProperty идёт снапшотным путём.
        public Func<System.Collections.Generic.IReadOnlyList<(System.Guid ParaId,
            Action<ParagraphProperties> Apply, Action<ParagraphProperties> Revert)>,
            string, bool>? CommitParagraphPropertyGranularDelegate
        { get; set; }
        public Action? CutDelegate { get; set; }
        public Action? CopyDelegate { get; set; }
        public Action? PasteDelegate { get; set; }

        /// <summary>
        /// Активная таблица — та в которой стоит каретка.
        /// Устанавливается из DocumentCanvas при входе каретки в таблицу.
        /// Используется линейкой для применения изменений ширины и отступа
        /// к правильной таблице (а не к первой найденной через FindTable).
        /// </summary>
        public TableBlock? ActiveTable { get; set; }

        // ── Делегаты: оформление / структура ─────────────────────────────
        public Action? TableMergeCellsDelegate { get; set; }
        public Action? TableSplitCellDelegate { get; set; }

        // Деление обычной ячейки пополам: true — вертикальной чертой, false — горизонтальной.
        public Action<bool>? TableDivideCellDelegate { get; set; }
        public Action<Writersword.Modules.TextEditor.Models.Styles.TextAlignment>? TableSetCellHAlignDelegate { get; set; }

        /// <summary>
        /// Пытается применить выравнивание к выделенной блок-картинке.
        /// Возвращает true, если картинка выделена и выравнивание применено —
        /// тогда абзац не трогается.
        /// </summary>
        public Func<Writersword.Modules.TextEditor.Models.Styles.TextAlignment, bool>? TrySetImageAlignmentDelegate { get; set; }

        /// <summary>
        /// Возвращает выравнивание выделенной блок-картинки, либо null если картинка не выделена.
        /// Позволяет риббону показывать выравнивание картинки, а не активного абзаца.
        /// </summary>
        public Func<Writersword.Modules.TextEditor.Models.Styles.TextAlignment?>? GetSelectedImageAlignmentDelegate { get; set; }

        // Делегаты команд контекстной вкладки «Формат» (работают с выделенной картинкой).
        public Action<WrapMode>? SetImageWrapModeDelegate { get; set; }
        public Action<WrapSide>? SetImageWrapSideDelegate { get; set; }
        public Func<WrapSide?>? GetSelectedImageWrapSideDelegate { get; set; }
        public Action<int>? SetImagePinnedPageDelegate { get; set; }
        public Func<int?>? GetSelectedImagePinnedPageDelegate { get; set; }
        public Func<int?>? GetSelectedImageCurrentPageDelegate { get; set; }
        public Action<bool>? SetImageLockAspectDelegate { get; set; }
        public Action? DeleteSelectedImageDelegate { get; set; }
        public Func<(WrapMode Wrap, bool LockAspect, Writersword.Modules.TextEditor.Models.Styles.TextAlignment Align)?>? GetSelectedImageInfoDelegate { get; set; }
        public Action<double>? SetImageRotationDelegate { get; set; }
        public Func<double?>? GetSelectedImageRotationDelegate { get; set; }
        public Action<double>? SetImageWidthDelegate { get; set; }
        public Action<double>? SetImageHeightDelegate { get; set; }
        public Action<double>? SetImageOpacityDelegate { get; set; }
        public Action<string?, double>? SetImageBorderDelegate { get; set; }
        public Func<(double WidthPt, double HeightPt, double Opacity, string? BorderColor, double BorderThicknessPt)?>? GetSelectedImageStyleDelegate { get; set; }
        public Action? ToggleImageFlipHorizontalDelegate { get; set; }
        public Action? ToggleImageFlipVerticalDelegate { get; set; }
        public Action<bool>? SetImageCropModeDelegate { get; set; }
        public Func<bool>? GetImageCropModeDelegate { get; set; }
        public Action<double, double, double, double>? SetImageWrapPaddingDelegate { get; set; }
        public Func<(double TopPt, double BottomPt, double LeftPt, double RightPt)?>? GetSelectedImageWrapPaddingDelegate { get; set; }
        public Action<int>? TableSetCellVAlignDelegate { get; set; }

        /// <summary>
        /// Абзацы всех выделенных ячеек таблицы. Пусто — выделения ячеек нет.
        /// Нужно форматированию абзаца: при выделении диапазона правка обязана
        /// применяться ко всем ячейкам, а не к одной под кареткой.
        /// </summary>
        public Func<System.Collections.Generic.IReadOnlyList<ParagraphBlock>>? GetSelectedCellParagraphsDelegate { get; set; }

        public Action<double, double, double, double>? TableSetCellPaddingDelegate { get; set; }
        public Func<(double TopPt, double BottomPt, double LeftPt, double RightPt)?>? TableGetCellPaddingDelegate { get; set; }

        // Инструмент рисования границ живёт в канвасе: он же обрабатывает нажатия
        // и показывает курсор. Лента только переключает и читает состояние.
        public Action<int>? TableSetLineToolDelegate { get; set; }
        public Func<int>? TableGetLineToolDelegate { get; set; }

        // Совмещённая установка обеих координат — один шаг отмены на нажатие.
        public Action<int, Writersword.Modules.TextEditor.Models.Styles.TextAlignment>? TableSetCellAlignDelegate { get; set; }

        // Чтение текущего выравнивания целевых ячеек. Нужно ленте, чтобы держать
        // активной ту кнопку, которая соответствует ячейке под кареткой.
        public Func<int?>? TableGetCellVAlignDelegate { get; set; }
        public Func<Writersword.Modules.TextEditor.Models.Styles.TextAlignment?>? TableGetCellHAlignDelegate { get; set; }
        public Action<string?>? TableSetCellBackgroundDelegate { get; set; }
        public Action<string, BorderStyle, double, string?>? TableSetCellBorderDelegate { get; set; }
        public Action<double>? TableSetColumnWidthDelegate { get; set; }
        public Action<double>? TableSetRowHeightDelegate { get; set; }

        /// <summary>
        /// Открыть шаг отмены перед правкой модели и закрыть после неё. Механизм
        /// снимков живёт в полотне, а часть операций с таблицами выполняется здесь —
        /// без этой пары они меняли документ мимо истории, и Ctrl+Z откатывал не их,
        /// а то, что было до них. Делегаты назначает полотно; если их нет (полотно
        /// ещё не подключено), правка просто пройдёт без записи, как и раньше.
        /// </summary>
        public Action<string>? BeginUndoStepDelegate { get; set; }
        public Action? CommitUndoStepDelegate { get; set; }

        /// <summary>
        /// То же самое, но снимок берётся с одной таблицы, а не со всего документа.
        /// Годится для правок, не меняющих состав блоков раздела: содержимое ячеек,
        /// ширины, объединение, сортировка, флаги таблицы. Для операций, где таблица
        /// появляется или исчезает целиком, нужен снимок документа — снимка самой
        /// таблицы для её возврата в раздел недостаточно.
        /// </summary>
        public Action<TableBlock, string>? BeginTableUndoStepDelegate { get; set; }
        public Action? CommitTableUndoStepDelegate { get; set; }

        private void BeginUndoStep(string description) => BeginUndoStepDelegate?.Invoke(description);
        private void CommitUndoStep() => CommitUndoStepDelegate?.Invoke();

        private void BeginTableUndoStep(TableBlock table, string description)
            => BeginTableUndoStepDelegate?.Invoke(table, description);
        private void CommitTableUndoStep() => CommitTableUndoStepDelegate?.Invoke();
        public Action? TableAutoFitDelegate { get; set; }
        public Action? TableDistributeColsDelegate { get; set; }
        public Action? TableDistributeRowsDelegate { get; set; }
        public Action<int, bool>? TableSortDelegate { get; set; }

        public DocumentModel Document => _document;

        public ObservableCollection<ParagraphViewModel> Paragraphs { get; } = new();
        public ObservableCollection<string> AvailableStyleNames { get; } = new();

        public event Action<CursorContext>? CursorContextChanged;

        /// <summary>
        /// Поднимается когда изменилось форматирование параграфа.
        /// DocumentCanvas подписывается чтобы сбросить кеш лейаутов.
        /// </summary>
        public event Action? ParagraphFormatChanged;

        /// <summary>
        /// Структурное изменение документа (вставка/удаление блока-картинки и т.п.),
        /// при котором текст абзацев не менялся. Холст пересобирает раскладку БЕЗ очистки
        /// кэша абзацев — это быстро, в отличие от ParagraphFormatChanged.
        /// </summary>
        public event Action? StructureChanged;

        /// <summary>
        /// Сообщает о структурном изменении документа, сделанном в обход вью-модели —
        /// например уборкой картинок вне страниц при закрытии.
        /// </summary>
        public void RaiseStructureChanged() => StructureChanged?.Invoke();

        /// <summary>
        /// Документ восстановлен после Undo/Redo (снапшот заменил состояние). Владелец (риббон,
        /// линейка) пересинхронизирует то, что не отслеживается кэшем раскладки — например поля
        /// страницы на линейке.
        /// </summary>
        public event Action? DocumentRestored;

        /// <summary>
        /// Содержимое документа изменено правкой, которая не меняет текст абзацев:
        /// свойства картинки (обтекание, размер, поворот, обрезка), поля страницы,
        /// форматирование. Владелец выставляет флаг «документ изменён» — иначе такие
        /// правки не попадают в сохранение, потому что флаг поднимался только по
        /// изменению PlainText параграфа.
        /// </summary>
        public event Action? ContentModified;

        /// <summary>Вызывается канвасом после фиксации правки — уведомляет подписчиков.</summary>
        public void RaiseContentModified() => ContentModified?.Invoke();

        /// <summary>Вызывается канвасом после Undo/Redo снапшота — уведомляет подписчиков.</summary>
        public void RaiseDocumentRestored() => DocumentRestored?.Invoke();

        /// <summary>Начинает снапшот-правку страницы (напр. drag полей на линейке) для Undo.</summary>
        public void BeginPageEdit(string description) => BeginEditDelegate?.Invoke(description);

        /// <summary>Коммитит снапшот-правку страницы в стек отмены.</summary>
        public void CommitPageEdit() => CommitEditDelegate?.Invoke();

        // Абзацы, затронутые последним char-форматированием. Канвас забирает этот список
        // в обработчике ParagraphFormatChanged, чтобы инвалидировать кэш раскладки ТОЛЬКО
        // у них, а не сбрасывать весь кэш и пересобирать весь документ (на больших
        // документах это давало секундный фриз на каждый коммит). null => затронутые
        // неизвестны (напр. форматирование ячейки) => канвас делает полный сброс.
        private IReadOnlyList<ParagraphViewModel>? _lastFormatAffected;

        /// <summary>
        /// Возвращает и сбрасывает список абзацев, затронутых последним форматированием.
        /// Сброс гарантирует, что следующее событие без явного списка приведёт к полному
        /// пересчёту, а не к использованию устаревшего списка.
        /// </summary>
        public IReadOnlyList<ParagraphViewModel>? TakeLastFormatAffected()
        {
            var v = _lastFormatAffected;
            _lastFormatAffected = null;
            return v;
        }

        // Во время drag маркеров отступа на линейке ApplyParaProperty вызывается на каждый
        // шаг мыши. Без батча это давало бы по снапшоту всего документа на каждый шаг (фриз).
        // BeginParagraphFormatBatch делает один снапшот на весь drag, ApplyParaProperty
        // внутри батча снапшот не повторяет, EndParagraphFormatBatch коммитит один раз.
        private bool _suppressFormatSnapshot;

        public void BeginParagraphFormatBatch()
        {
            if (_suppressFormatSnapshot) return;
            _suppressFormatSnapshot = true;
            BeginEditDelegate?.Invoke("Format paragraph");
        }

        public void EndParagraphFormatBatch()
        {
            if (!_suppressFormatSnapshot) return;
            _suppressFormatSnapshot = false;
            CommitEditDelegate?.Invoke();
        }

        // Идёт массовая перестройка всех VM-абзацев (загрузка/undo/структурные операции).
        // Канвас в это время пропускает поабзацную инкрементальную раскладку — она
        // бессмысленна (следом идёт общий пересбор) и даёт O(n^2) на больших документах.
        public bool IsBulkRebuilding { get; private set; }

        /// <summary>
        /// Устанавливается DocumentCanvas. Вызывается при изменении preview-шрифта.
        /// null = preview снят. Никаких изменений модели — canvas сам строит временный лейаут.
        /// </summary>
        // Делегаты live-preview шрифта. Канвас сам вычисляет затронутые абзацы и
        // ячейки по своему состоянию выделения, поэтому сюда передаются только команды
        // начала сессии, имя шрифта при наведении и завершение (коммит/отмена).
        public Action? BeginFontPreviewDelegate { get; set; }
        public Action<string>? PreviewFontFamilyDelegate { get; set; }
        public Action<bool>? EndFontPreviewDelegate { get; set; }

        // Возврат клавиатурного фокуса редактору (канвасу) после работы с лентой.
        public Action? FocusEditorDelegate { get; set; }

        public EditorViewMode ViewMode
        {
            get => _viewMode;
            set
            {
                this.RaiseAndSetIfChanged(ref _viewMode, value);
                this.RaisePropertyChanged(nameof(IsSpreadReading));
                this.RaisePropertyChanged(nameof(IsColumnReading));
            }
        }

        /// <summary>
        /// Настройки чтения: подача, бумага, свет, шрифт чтения, приближение книги.
        /// Объект живой — его правит лента чтения, а канвас читает при каждой сборке.
        /// О смене сообщают <see cref="ReadingSettingsChanged"/> (нужна пересборка) и
        /// <see cref="ReadingVisualChanged"/> (достаточно перерисовки).
        /// </summary>
        public Models.Settings.ReadingSettings Reading { get; } = new();

        /// <summary>
        /// Настройки чтения изменились так, что раскладку нужно пересобрать: другой
        /// лист, другой шрифт, другая подача, другой масштаб содержимого.
        /// </summary>
        public event Action? ReadingSettingsChanged;

        /// <summary>
        /// Настройки чтения изменились только на вид: свет, цвет бумаги, приближение
        /// книги, номера страниц. Раскладка остаётся прежней, и полный пересчёт по ней
        /// не нужен — достаточно перерисовать готовое.
        /// </summary>
        public event Action? ReadingVisualChanged;

        /// <summary>Сообщает о правке настроек чтения, требующей пересборки раскладки.</summary>
        public void RaiseReadingSettingsChanged()
        {
            this.RaisePropertyChanged(nameof(IsSpreadReading));
            this.RaisePropertyChanged(nameof(IsColumnReading));
            ReadingSettingsChanged?.Invoke();
        }

        /// <summary>Сообщает о правке, которую достаточно перерисовать.</summary>
        public void RaiseReadingVisualChanged() => ReadingVisualChanged?.Invoke();

        /// <summary>
        /// Подача режима чтения: книжный разворот, одиночный лист или сплошная лента.
        /// Это не отдельный режим, а способ показа того же чтения, поэтому кнопка
        /// в статус-баре остаётся одна.
        /// </summary>
        public Models.Settings.ReadingFlow ReadingFlow
        {
            get => Reading.Flow;
            set
            {
                if (Reading.Flow == value) return;
                Reading.Flow = value;
                this.RaisePropertyChanged();
                this.RaisePropertyChanged(nameof(IsSpreadReading));
                this.RaisePropertyChanged(nameof(IsColumnReading));
            }
        }

        /// <summary>
        /// Чтение страницами: разворот или одиночный лист. Отсюда канвас узнаёт, что
        /// верстать нужно листами и рисовать книгой, а не сплошной колонкой.
        /// </summary>
        public bool IsSpreadReading => _viewMode == EditorViewMode.Reading && Reading.IsPaged;

        /// <summary>Чтение сплошной лентой: страниц нет, текст прокручивается.</summary>
        public bool IsColumnReading => _viewMode == EditorViewMode.Reading && !Reading.IsPaged;

        private int _pagesPerRow = 1;

        /// <summary>
        /// Число страниц в ряду в режиме страниц: 1 — столбик (как раньше),
        /// 2 — страницы рядом. Влияет только на отображение — раскладка
        /// документа (пагинация) остаётся неизменной.
        /// </summary>
        public int PagesPerRow
        {
            get => _pagesPerRow;
            // 0 — авто: столько страниц в ряду, сколько влезает по ширине при текущем
            // масштабе. Отдалили — стало больше, приблизили — меньше, вплоть до одной.
            set => this.RaiseAndSetIfChanged(ref _pagesPerRow, Math.Clamp(value, 0, 12));
        }

        public double Zoom
        {
            get => _zoom;
            set
            {
                double clamped = Math.Max(0.25, Math.Min(5.0, value));
                this.RaiseAndSetIfChanged(ref _zoom, clamped);
                _document.Zoom = clamped;
            }
        }

        public bool IsFocusMode
        {
            get => _isFocusMode;
            set => this.RaiseAndSetIfChanged(ref _isFocusMode, value);
        }

        public bool IsFullscreen
        {
            get => _isFullscreen;
            set => this.RaiseAndSetIfChanged(ref _isFullscreen, value);
        }

        public bool IsReadOnly
        {
            get => _isReadOnly;
            set => this.RaiseAndSetIfChanged(ref _isReadOnly, value);
        }

        public CanvasSettings CanvasSettings => _document.CanvasSettings;
        public TextEditorPageSettings PageSettings => _document.PageSettings;

        public DocumentViewModel(
            DocumentModel document,
            ChunkManager chunkManager,
            AutoReplaceService autoReplace,
            SpellCheckService spellCheck)
        {
            _document = document ?? throw new ArgumentNullException(nameof(document));
            _chunkManager = chunkManager ?? throw new ArgumentNullException(nameof(chunkManager));
            _autoReplace = autoReplace ?? throw new ArgumentNullException(nameof(autoReplace));
            _spellCheck = spellCheck ?? throw new ArgumentNullException(nameof(spellCheck));

            _viewMode = document.ViewMode;
            _zoom = document.Zoom;

            RebuildStyleNames();
            RebuildParagraphViewModels();
        }

        // ── Активный параграф ─────────────────────────────────────────────

        public void SetActiveParagraph(ParagraphViewModel vm)
        {
            // Выходим из режима таблицы при клике на обычный параграф.
            TableActiveCellParagraph = null;
            _activeParagraph = vm;
            FireCursorContextChanged();
        }

        public void FireCursorContextChanged()
        {
            if (_activeParagraph is null) return;
            CursorContextChanged?.Invoke(BuildCursorContext(_activeParagraph));
        }

        /// <summary>
        /// Обновляет контекст линейки/риббона для параграфа внутри ячейки таблицы.
        /// Вызывается из DocumentCanvas при каждом изменении позиции каретки в таблице.
        /// </summary>
        public void FireTableCellCursorContext(ParagraphBlock cellPara, int selStart = 0, int selEnd = 0)
        {
            TableActiveCellParagraph = cellPara;
            var tempVm = new ParagraphViewModel(cellPara)
            {
                // Реальная позиция каретки/выделения в ячейке — иначе BuildCursorContext
                // всегда читал бы первый ран, и риббон показывал бы один шрифт независимо
                // от того, где стоит каретка и что выделено.
                SelectionStart = selStart,
                SelectionEnd = selEnd
            };
            CursorContextChanged?.Invoke(BuildCursorContext(tempVm));
        }

        private CursorContext BuildCursorContext(ParagraphViewModel pvm)
        {
            var ctx = new CursorContext();
            var block = pvm.Model;

            RunProperties? rp = null;

            if (pvm.SelectionEnd > pvm.SelectionStart)
            {
                int offset = 0;
                foreach (var chunk in block.Chunks)
                    foreach (var run in chunk.Runs)
                    {
                        if (offset + run.Text.Length > pvm.SelectionStart)
                        { rp = run.Properties; goto foundRun; }
                        offset += run.Text.Length;
                    }
                foundRun:;
            }
            else
            {
                // Каретка без выделения: свойства символа слева от каретки (как в Word).
                // SelectionStart == SelectionEnd — позиция каретки, переданная канвасом.
                int caretPos = Math.Max(0, pvm.SelectionStart - 1);
                rp = GetRunPropsAtOffset(block, caretPos);
            }

            if (rp is not null)
            {
                ctx.IsBold = rp.IsBold;
                ctx.IsItalic = rp.IsItalic;
                ctx.IsUnderline = rp.IsUnderline;
                ctx.IsStrikethrough = rp.IsStrikethrough;
                ctx.IsSuperscript = rp.IsSuperscript;
                ctx.IsSubscript = rp.IsSubscript;
                ctx.IsAllCaps = rp.IsAllCaps;
                ctx.TextColor = rp.TextColor ?? "#1A1A1A";
                ctx.HighlightColor = rp.HighlightColor;
                ctx.FontFamily = rp.FontFamily ?? ResolveStyleFontFamily(block.Properties.StyleName);
                ctx.FontSize = rp.FontSize ?? ResolveStyleFontSize(block.Properties.StyleName);
            }
            else
            {
                ctx.FontFamily = ResolveStyleFontFamily(block.Properties.StyleName);
                ctx.FontSize = ResolveStyleFontSize(block.Properties.StyleName);
                ctx.TextColor = "#1A1A1A";
            }

            // Если выделена блок-картинка — риббон показывает её выравнивание, а не абзаца.
            var imageAlign = GetSelectedImageAlignmentDelegate?.Invoke();
            ctx.Alignment = imageAlign ?? block.Properties.Alignment ?? TextAlignment.Left;
            ctx.StyleName = block.Properties.StyleName ?? "Normal";
            bool isListCtx = block.ListProperties is not null
                && block.ListProperties.MarkerType != ListMarkerType.None;

            // Для элемента списка без явного левого отступа текст рисуется по отступу уровня —
            // сообщаем именно его, иначе левый маркер линейки «врёт» (стоит у поля, а текст правее).
            if (isListCtx && block.Properties.LeftIndent is null)
                ctx.LeftIndentPt = block.ListProperties!.EffectiveTextIndentPt();
            else
                ctx.LeftIndentPt = block.Properties.LeftIndent ?? 0;

            if (isListCtx)
            {
                // В списке «абзацная стрелка» (верхний маркер) = начало ТЕКСТА первой строки
                // (номер + ширина + зазор). Значение считает раскладка и кладёт в
                // ComputedFirstLineOffsetPt. Строки 2+ — левый маркер (ctx.LeftIndentPt), от них
                // абзацная не зависит.
                ctx.FirstLineIndentPt = block.ListProperties!.ComputedFirstLineOffsetPt;
            }
            else
            {
                ctx.FirstLineIndentPt = block.Properties.FirstLineIndent ?? 0;
            }
            ctx.RightIndentPt = block.Properties.RightIndent ?? 0;
            ctx.HasSpaceBefore = ResolveEffectiveSpaceBefore(block) > 0;
            ctx.HasSpaceAfter = ResolveEffectiveSpaceAfter(block) > 0;
            return ctx;
        }

        private string ResolveStyleFontFamily(string? styleName)
        {
            var style = _document.FindStyle(styleName ?? "Normal");
            return style?.RunProperties?.FontFamily ?? "Times New Roman";
        }

        private double ResolveStyleFontSize(string? styleName)
        {
            var style = _document.FindStyle(styleName ?? "Normal");
            return style?.RunProperties?.FontSize ?? 14.0;
        }

        // Эффективный интервал перед абзацем (pt): собственное значение, иначе из цепочки стилей BasedOn.
        private double ResolveEffectiveSpaceBefore(ParagraphBlock block)
        {
            if (block.Properties.SpaceBefore.HasValue)
                return block.Properties.SpaceBefore.Value;

            string? name = block.Properties.StyleName ?? "Normal";
            for (int guard = 0; name is not null && guard < 16; guard++)
            {
                var style = _document.FindStyle(name);
                if (style is null) break;
                if (style.ParagraphProperties?.SpaceBefore.HasValue == true)
                    return style.ParagraphProperties.SpaceBefore.Value;
                name = style.BasedOn;
            }
            return 0.0;
        }

        // Эффективный интервал после абзаца (pt): собственное значение, иначе из цепочки стилей BasedOn.
        private double ResolveEffectiveSpaceAfter(ParagraphBlock block)
        {
            if (block.Properties.SpaceAfter.HasValue)
                return block.Properties.SpaceAfter.Value;

            string? name = block.Properties.StyleName ?? "Normal";
            for (int guard = 0; name is not null && guard < 16; guard++)
            {
                var style = _document.FindStyle(name);
                if (style is null) break;
                if (style.ParagraphProperties?.SpaceAfter.HasValue == true)
                    return style.ParagraphProperties.SpaceAfter.Value;
                name = style.BasedOn;
            }
            return 8.0;
        }

        // ── Управление параграфами ────────────────────────────────────────

        public ParagraphViewModel AddParagraphAfter(ParagraphViewModel after)
        {
            var section = _document.Sections[0];
            var newBlock = new ParagraphBlock();

            // Новый абзац наследует форматирование текущего: выравнивание, отступы
            // (левый/правый/первой строки), интервалы, межстрочный и стиль. Иначе при
            // Enter настройки абзаца сбрасывались на дефолтные.
            newBlock.Properties = after.Model.Properties.Clone();
            // Разрыв страницы перед абзацем не наследуем — иначе каждый Enter добавлял бы
            // новый разрыв. Это совпадает с поведением Word.
            newBlock.Properties.PageBreakBefore = false;

            // Элемент списка: новый абзац продолжает тот же список (тот же ListId), нумерация
            // считается движком автоматически. Перезапуск нумерации не наследуем.
            if (after.Model.ListProperties is not null)
            {
                var lp = after.Model.ListProperties.Clone();
                lp.ContinueNumbering = true;
                newBlock.ListProperties = lp;
            }

            int modelIndex = section.Blocks.IndexOf(after.Model);
            if (modelIndex < 0) section.Blocks.Add(newBlock);
            else section.Blocks.Insert(modelIndex + 1, newBlock);

            int vmIndex = Paragraphs.IndexOf(after);
            var newVm = CreateParagraphViewModel(newBlock);
            Paragraphs.Insert(vmIndex + 1, newVm);

            return newVm;
        }

        public ParagraphViewModel? DeleteParagraph(ParagraphViewModel target)
        {
            if (Paragraphs.Count <= 1) return null;

            int vmIndex = Paragraphs.IndexOf(target);
            if (vmIndex < 0) return null;

            _document.Sections[0].Blocks.Remove(target.Model);
            Paragraphs.RemoveAt(vmIndex);

            int focusIndex = Math.Max(0, vmIndex - 1);
            var focusVm = Paragraphs[focusIndex];
            focusVm.RequestFocus();
            return focusVm;
        }

        public void MergeParagraphWithPrevious(ParagraphViewModel target, string textToMerge)
        {
            if (IsReadOnly) return;
            int vmIndex = Paragraphs.IndexOf(target);
            if (vmIndex <= 0) return;

            var previous = Paragraphs[vmIndex - 1];

            // Дописываем содержимое следующего абзаца посимвольно, а не плоским текстом:
            // так переезжают и форматирование каждого символа, и картинки в строке —
            // вставка plain-текста превратила бы картинку в пустой символ-заполнитель.
            var merged = previous.Model.ToCharCells();
            int caretPosition = merged.Count;
            merged.AddRange(target.Model.ToCharCells());
            previous.Model.RebuildFromCharCells(merged);
            previous.RefreshPlainTextFromModel();

            _document.Sections[0].Blocks.Remove(target.Model);
            Paragraphs.RemoveAt(vmIndex);

            previous.RequestFocusAtPosition?.Invoke(caretPosition);
        }

        public void SelectAll() { foreach (var p in Paragraphs) p.IsSelected = true; }
        public void ClearSelection() { foreach (var p in Paragraphs) p.IsSelected = false; }

        public string? GetDocumentSelectedText()
        {
            var selected = Paragraphs.Where(p => p.IsSelected).ToList();
            if (selected.Count == 0) return null;
            return string.Join(Environment.NewLine, selected.Select(p => p.PlainText));
        }

        // ── ITextEditorCommandTarget: символы ─────────────────────────────

        public void ToggleBold() => ApplyCharProperty(p => p.IsBold = !p.IsBold);
        public void ToggleItalic() => ApplyCharProperty(p => p.IsItalic = !p.IsItalic);
        public void ToggleUnderline() => ApplyCharProperty(p => p.IsUnderline = !p.IsUnderline);
        public void ToggleStrikethrough() => ApplyCharProperty(p => p.IsStrikethrough = !p.IsStrikethrough);

        public void ToggleSuperscript()
            => ApplyCharProperty(p => { p.IsSuperscript = !p.IsSuperscript; if (p.IsSuperscript) p.IsSubscript = false; });

        public void ToggleSubscript()
            => ApplyCharProperty(p => { p.IsSubscript = !p.IsSubscript; if (p.IsSubscript) p.IsSuperscript = false; });

        public void ToggleAllCaps() => ApplyCharProperty(p => p.IsAllCaps = !p.IsAllCaps);

        /// <summary>
        /// Меняет регистр текста (сами буквы, не форматирование), сохраняя форматирование
        /// ранов. С выделением работает по его границам; без выделения применяется к слову
        /// под кареткой (как в Word), диапазон которого запрашивается у канваса.
        /// </summary>
        public void ChangeCase(TextCaseMode mode)
        {
            if (IsReadOnly) return;
            // Целевые диапазоны: (параграф, from, to) по выделению либо слово под кареткой.
            var targets = new System.Collections.Generic.List<(ParagraphViewModel Pvm, int From, int To)>();
            if (SelectionParagraphs.Count > 0)
            {
                for (int i = 0; i < SelectionParagraphs.Count; i++)
                {
                    var pvm = SelectionParagraphs[i];
                    int len = pvm.Model.GetPlainText().Length;
                    int from = (SelectionParagraphs.Count == 1 || i == 0) ? pvm.SelectionStart : 0;
                    int to = (SelectionParagraphs.Count == 1 || i == SelectionParagraphs.Count - 1) ? pvm.SelectionEnd : len;
                    from = Math.Clamp(from, 0, len);
                    to = Math.Clamp(to, from, len);
                    if (to > from) targets.Add((pvm, from, to));
                }
            }
            else
            {
                var word = GetCaretWordRangeDelegate?.Invoke();
                if (word is null) return;
                targets.Add(word.Value);
            }
            if (targets.Count == 0) return;

            // Собираем правки (paraId, from, старый текст, новый текст) по диапазонам.
            var edits = new System.Collections.Generic.List<(System.Guid ParaId, int From, string OldText, string NewText)>();
            foreach (var (pvm, from, to) in targets)
            {
                string full = pvm.Model.GetPlainText();
                int len = full.Length;
                int f = Math.Clamp(from, 0, len);
                int t = Math.Clamp(to, f, len);
                if (t <= f) continue;

                char[] chars = full.ToCharArray();
                ApplyCaseToRange(chars, f, t, mode);
                string newText = new string(chars, f, t - f);
                string oldText = full.Substring(f, t - f);
                if (oldText != newText)
                    edits.Add((pvm.Model.Id, f, oldText, newText));
            }
            if (edits.Count == 0) return;

            // Операционный путь: гранулярная команда в общий TextUndoStack (как и весь остальной
            // ввод/форматирование). Отмена идёт в общем хронологическом порядке, без снапшота.
            if (CommitTextEditsDelegate is not null && CommitTextEditsDelegate(edits, "Change case"))
            {
                FireCursorContextChanged();
                return;
            }

            // Запасной путь (канвас не подключён) — снапшот.
            BeginEditDelegate?.Invoke("Change case");
            foreach (var (pvm, from, to) in targets)
            {
                TransformParagraphRange(pvm.Model, from, to, mode);
                pvm.RefreshPlainTextFromModel();
            }
            CommitEditDelegate?.Invoke();
            _lastFormatAffected = targets.ConvertAll(t => t.Pvm);
            FireCursorContextChanged();
            ParagraphFormatChanged?.Invoke();
        }

        // Меняет регистр символов абзаца в диапазоне [from, to), записывая их обратно в раны.
        // Длина текста не меняется, поэтому структура ранов и форматирование сохраняются.
        private static void TransformParagraphRange(ParagraphBlock block, int from, int to, TextCaseMode mode)
        {
            string full = block.GetPlainText();
            int len = full.Length;
            from = Math.Clamp(from, 0, len);
            to = Math.Clamp(to, from, len);
            if (to <= from) return;

            char[] chars = full.ToCharArray();
            ApplyCaseToRange(chars, from, to, mode);

            int offset = 0;
            foreach (var chunk in block.Chunks)
                foreach (var run in chunk.Runs)
                {
                    int rl = run.Text.Length;
                    if (rl == 0) continue;
                    int runStart = offset;
                    int s = Math.Max(from, runStart);
                    int e = Math.Min(to, runStart + rl);
                    if (e > s)
                    {
                        var arr = run.Text.ToCharArray();
                        for (int g = s; g < e; g++)
                            arr[g - runStart] = chars[g];
                        run.Text = new string(arr);
                    }
                    offset += rl;
                }
            block.InvalidateAllChunks();
        }

        // Применяет режим регистра к диапазону массива символов с учётом контекста слева
        // (для Title — границы слов, для Sentence — конец предложения).
        private static void ApplyCaseToRange(char[] text, int from, int to, TextCaseMode mode)
        {
            switch (mode)
            {
                case TextCaseMode.Upper:
                    for (int i = from; i < to; i++) text[i] = char.ToUpper(text[i]);
                    break;

                case TextCaseMode.Lower:
                    for (int i = from; i < to; i++) text[i] = char.ToLower(text[i]);
                    break;

                case TextCaseMode.Toggle:
                    for (int i = from; i < to; i++)
                        text[i] = char.IsUpper(text[i]) ? char.ToLower(text[i]) : char.ToUpper(text[i]);
                    break;

                case TextCaseMode.Title:
                    {
                        bool prevSep = from == 0 || !char.IsLetterOrDigit(text[from - 1]);
                        for (int i = from; i < to; i++)
                        {
                            char c = text[i];
                            if (char.IsLetter(c))
                            {
                                text[i] = prevSep ? char.ToUpper(c) : char.ToLower(c);
                                prevSep = false;
                            }
                            else prevSep = !char.IsLetterOrDigit(c);
                        }
                        break;
                    }

                case TextCaseMode.Sentence:
                    {
                        // Определяем начало предложения по контексту слева от диапазона.
                        bool startSentence = true;
                        for (int j = from - 1; j >= 0; j--)
                        {
                            char pc = text[j];
                            if (pc == ' ' || pc == '\t') continue;
                            startSentence = pc == '.' || pc == '!' || pc == '?';
                            break;
                        }
                        for (int i = from; i < to; i++)
                        {
                            char c = text[i];
                            if (char.IsLetter(c))
                            {
                                text[i] = startSentence ? char.ToUpper(c) : char.ToLower(c);
                                startSentence = false;
                            }
                            else if (c == '.' || c == '!' || c == '?')
                                startSentence = true;
                        }
                        break;
                    }
            }
        }
        public void ToggleSmallCaps() => ApplyCharProperty(p => p.IsSmallCaps = !p.IsSmallCaps);
        public void ClearFormatting() => ApplyCharProperty(_ => { }, clearAll: true);

        public void SetTextColor(string color) => ApplyCharProperty(p => p.TextColor = color);
        public void SetHighlightColor(string? color) => ApplyCharProperty(p => p.HighlightColor = color);
        public void SetFontFamily(string font) => ApplyCharProperty(p => p.FontFamily = font);

        // Live-preview шрифта полностью реализован в DocumentCanvas: он знает полную
        // картину выделения (обычные абзацы + ячейки таблицы). Здесь — только проброс.
        public void BeginFontPreview() => BeginFontPreviewDelegate?.Invoke();

        public void PreviewFontFamily(string font) => PreviewFontFamilyDelegate?.Invoke(font);

        public void EndFontPreview(bool commit) => EndFontPreviewDelegate?.Invoke(commit);

        public void FocusEditor() => FocusEditorDelegate?.Invoke();

        /// <summary>
        /// Применяет шрифт к набору абзацев и диапазонов одним undo-снапшотом.
        /// Вызывается DocumentCanvas при коммите live-preview для всего выделения,
        /// включая абзацы и ячейки таблицы. Диапазон end &lt;= start трактуется как весь абзац.
        /// </summary>
        public void ApplyFontToBlocks(
            IReadOnlyList<(ParagraphBlock block, int start, int end)> targets, string font)
        {
            if (IsReadOnly) return;
            if (targets is null || targets.Count == 0) return;

            BeginEditDelegate?.Invoke("Format text");

            foreach (var (block, start, end) in targets)
            {
                if (block is null) continue;
                if (end > start)
                    ApplyCharPropertyToRange(block, start, end, p => p.FontFamily = font, false);
                else
                    ApplyCharPropertyToBlock(block, 0, 0, p => p.FontFamily = font, false);
            }

            CommitEditDelegate?.Invoke();
            FireCursorContextChanged();
            ParagraphFormatChanged?.Invoke();
        }

        public void SetFontSize(double size)
            => ApplyCharProperty(p => p.FontSize = size > 0 ? size : (double?)null);

        public void IncreaseFontSize()
        {
            double current = ResolveCurrentFontSize();
            ApplyCharProperty(p => p.FontSize = current + 2);
        }

        public void DecreaseFontSize()
        {
            double current = ResolveCurrentFontSize();
            ApplyCharProperty(p => p.FontSize = Math.Max(1, current - 2));
        }

        private double ResolveCurrentFontSize()
        {
            // Берём размер по позиции выделения (первый выделенный абзац, его SelectionStart),
            // как это делает BuildCursorContext. Раньше всегда читался первый ран абзаца —
            // из-за этого increase/decrease на выделении не на первом ране «застревали»:
            // читался старый размер первого рана, и каждое нажатие давало один и тот же шаг.
            ParagraphViewModel? pvm = SelectionParagraphs.Count > 0 ? SelectionParagraphs[0] : _activeParagraph;
            if (pvm is null) return 14;
            var block = pvm.Model;
            int pos = pvm.SelectionEnd > pvm.SelectionStart ? pvm.SelectionStart : 0;
            var rp = GetRunPropsAtOffset(block, pos);
            return rp?.FontSize ?? ResolveStyleFontSize(block.Properties.StyleName);
        }

        // Возвращает свойства рана, покрывающего символ в позиции charOffset.
        // Если позиция в конце текста — последний непустой ран.
        private static RunProperties? GetRunPropsAtOffset(ParagraphBlock block, int charOffset)
        {
            int offset = 0;
            foreach (var chunk in block.Chunks)
                foreach (var run in chunk.Runs)
                {
                    if (offset + run.Text.Length > charOffset)
                        return run.Properties;
                    offset += run.Text.Length;
                }
            for (int ci = block.Chunks.Count - 1; ci >= 0; ci--)
                if (block.Chunks[ci].Runs.Count > 0)
                    return block.Chunks[ci].Runs[^1].Properties;
            return null;
        }

        // ── ITextEditorCommandTarget: абзац ───────────────────────────────

        public void SetAlignment(TextAlignment a)
        {
            // Если выделена блок-картинка — выравниваем её в колонке, а не абзац.
            if (TrySetImageAlignmentDelegate?.Invoke(a) == true) return;
            ApplyParaProperty(p => p.Alignment = a);
        }

        // ── Команды выделенной картинки (контекстная вкладка «Формат») ─────
        public void SetImageWrapMode(WrapMode mode) { if (IsReadOnly) return; SetImageWrapModeDelegate?.Invoke(mode); }
        public void SetImageWrapSide(WrapSide side) { if (IsReadOnly) return; SetImageWrapSideDelegate?.Invoke(side); }
        public WrapSide? GetSelectedImageWrapSide() => GetSelectedImageWrapSideDelegate?.Invoke();
        public void SetImagePinnedPage(int page) { if (IsReadOnly) return; SetImagePinnedPageDelegate?.Invoke(page); }
        public int? GetSelectedImagePinnedPage() => GetSelectedImagePinnedPageDelegate?.Invoke();
        public int? GetSelectedImageCurrentPage() => GetSelectedImageCurrentPageDelegate?.Invoke();
        public void SetImageLockAspect(bool locked) { if (IsReadOnly) return; SetImageLockAspectDelegate?.Invoke(locked); }
        public void DeleteSelectedImage() { if (IsReadOnly) return; DeleteSelectedImageDelegate?.Invoke(); }
        public (WrapMode Wrap, bool LockAspect, Writersword.Modules.TextEditor.Models.Styles.TextAlignment Align)? GetSelectedImageInfo()
            => GetSelectedImageInfoDelegate?.Invoke();
        public void SetImageRotation(double degrees) { if (IsReadOnly) return; SetImageRotationDelegate?.Invoke(degrees); }
        public double? GetSelectedImageRotation() => GetSelectedImageRotationDelegate?.Invoke();
        public void SetImageWidth(double widthPt) { if (IsReadOnly) return; SetImageWidthDelegate?.Invoke(widthPt); }
        public void SetImageHeight(double heightPt) { if (IsReadOnly) return; SetImageHeightDelegate?.Invoke(heightPt); }
        public void SetImageOpacity(double opacity) { if (IsReadOnly) return; SetImageOpacityDelegate?.Invoke(opacity); }
        public void SetImageBorder(string? colorHex, double thicknessPt) { if (IsReadOnly) return; SetImageBorderDelegate?.Invoke(colorHex, thicknessPt); }
        public (double WidthPt, double HeightPt, double Opacity, string? BorderColor, double BorderThicknessPt)? GetSelectedImageStyle()
            => GetSelectedImageStyleDelegate?.Invoke();
        public void ToggleImageFlipHorizontal() { if (IsReadOnly) return; ToggleImageFlipHorizontalDelegate?.Invoke(); }
        public void ToggleImageFlipVertical() { if (IsReadOnly) return; ToggleImageFlipVerticalDelegate?.Invoke(); }
        public void SetImageCropMode(bool on) { if (IsReadOnly) return; SetImageCropModeDelegate?.Invoke(on); }
        public bool GetImageCropMode() => GetImageCropModeDelegate?.Invoke() ?? false;
        public void SetImageWrapPadding(double topPt, double bottomPt, double leftPt, double rightPt)
        { if (IsReadOnly) return; SetImageWrapPaddingDelegate?.Invoke(topPt, bottomPt, leftPt, rightPt); }
        public (double TopPt, double BottomPt, double LeftPt, double RightPt)? GetSelectedImageWrapPadding()
            => GetSelectedImageWrapPaddingDelegate?.Invoke();

        public void IncreaseIndent()
            => ApplyParaProperty(p => p.LeftIndent = (p.LeftIndent ?? 0) + 18);

        public void DecreaseIndent()
            => ApplyParaProperty(p => p.LeftIndent = Math.Max(0, (p.LeftIndent ?? 0) - 18));

        public void SetLineSpacing(double v)
            => ApplyParaProperty(p => { p.LineSpacingRule = LineSpacingRule.Auto; p.LineSpacingValue = v; });

        public void SetSpaceBefore(double pt) => ApplyParaProperty(p => p.SpaceBefore = pt);
        public void SetSpaceAfter(double pt) => ApplyParaProperty(p => p.SpaceAfter = pt);
        public void ApplyStyle(string name) => ApplyParaProperty(p => p.StyleName = name);

        public void SetLeftIndentPt(double pt) => ApplyParaProperty(p => p.LeftIndent = pt);
        public void SetFirstLineIndentPt(double pt) => ApplyParaProperty(p => p.FirstLineIndent = pt);
        public void SetRightIndentPt(double pt) => ApplyParaProperty(p => p.RightIndent = pt);

        /// <summary>
        /// Снимок свойств текущего абзаца (активного или абзаца активной ячейки) для пред-заполнения
        /// окна «Абзац». Null — нет активного абзаца.
        /// </summary>
        public ParagraphProperties? GetActiveParagraphProperties()
        {
            if (TableActiveCellParagraph is not null)
                return TableActiveCellParagraph.Properties.Clone();
            return _activeParagraph?.Model.Properties.Clone();
        }

        /// <summary>
        /// Применяет к выделенным абзацам поля окна «Абзац» (выравнивание, уровень, отступы,
        /// интервалы, междустрочный) одной командой отмены. Прочие поля (стиль, флаги страницы)
        /// не трогает.
        /// </summary>
        public void ApplyParagraphSettings(ParagraphProperties s) => ApplyParaProperty(p =>
        {
            p.Alignment = s.Alignment;
            p.OutlineLevel = s.OutlineLevel;
            p.LeftIndent = s.LeftIndent;
            p.RightIndent = s.RightIndent;
            p.FirstLineIndent = s.FirstLineIndent;
            p.SpaceBefore = s.SpaceBefore;
            p.SpaceAfter = s.SpaceAfter;
            p.LineSpacingRule = s.LineSpacingRule;
            p.LineSpacingValue = s.LineSpacingValue;
        });

        /// <summary>Ставит выделенным абзацам структурный уровень (0 — основной текст, 1…9).</summary>
        public void SetOutlineLevel(int level) => ApplyParaProperty(p => p.OutlineLevel = level);

        // ── ITextEditorCommandTarget: списки ──────────────────────────────

        // Применяет мутацию свойств списка к выделенным (или активному) абзацам.
        // Нумерация зависит от соседних абзацев, поэтому _lastFormatAffected НЕ выставляется:
        // канвас делает полный пересбор раскладки (сброс кэша) и пересчитывает маркеры.
        private void ApplyListMutation(Action<ParagraphBlock> mutate)
        {
            if (IsReadOnly) return;

            // Выделен диапазон ячеек — список применяется ко всем их абзацам.
            // Ветка ниже работает с одним абзацем активной ячейки, из-за неё
            // список доставался только той ячейке, где стоит каретка.
            var cellParagraphs = GetSelectedCellParagraphsDelegate?.Invoke();
            if (cellParagraphs is { Count: > 0 })
            {
                if (!_suppressFormatSnapshot) BeginEditDelegate?.Invoke("Format list");
                foreach (var para in cellParagraphs) mutate(para);
                if (!_suppressFormatSnapshot) CommitEditDelegate?.Invoke();
                FireCursorContextChanged();
                ParagraphFormatChanged?.Invoke();
                return;
            }

            if (TableActiveCellParagraph is not null)
            {
                if (!_suppressFormatSnapshot) BeginEditDelegate?.Invoke("Format list");
                mutate(TableActiveCellParagraph);
                if (!_suppressFormatSnapshot) CommitEditDelegate?.Invoke();
                FireCursorContextChanged();
                ParagraphFormatChanged?.Invoke();
                return;
            }

            var targets = SelectionParagraphs.Count > 0
                ? SelectionParagraphs.ToList()
                : (_activeParagraph is not null
                    ? new System.Collections.Generic.List<ParagraphViewModel> { _activeParagraph }
                    : null);
            if (targets is null || targets.Count == 0) return;

            if (!_suppressFormatSnapshot) BeginEditDelegate?.Invoke("Format list");
            foreach (var pvm in targets) mutate(pvm.Model);
            if (!_suppressFormatSnapshot) CommitEditDelegate?.Invoke();

            FireCursorContextChanged();
            ParagraphFormatChanged?.Invoke();
        }

        // Отступы списка при создании. Левый отступ — база строк 2+. Позицию номера (метки)
        // задаём АБСОЛЮТНО (от поля), чтобы номер жил независимо от левого края строк 2+:
        // двигаешь строки 2+ — номер стоит, двигаешь номер — строки 2+ стоят.
        private static void EnsureListLeftIndent(ParagraphBlock b, int level)
        {
            if (b.Properties.LeftIndent is null)
                b.Properties.LeftIndent = (level + 1) * ListProperties.DefaultLevelStepPt;

            if (b.ListProperties is not null && b.ListProperties.MarkerIndentPt is null)
            {
                double textLeft = b.Properties.LeftIndent ?? (level + 1) * ListProperties.DefaultLevelStepPt;
                b.ListProperties.MarkerIndentPt = Math.Max(0.0, textLeft - ListProperties.DefaultHangingPt);
            }
        }

        // Снимает список с абзаца. Авто-отступ уровня убираем, чтобы абзац вернулся в исходный вид.
        private static void RemoveListFormatting(ParagraphBlock b)
        {
            var lp = b.ListProperties;
            if (lp is not null && b.Properties.LeftIndent.HasValue)
            {
                double autoIndent = (lp.Level + 1) * ListProperties.DefaultLevelStepPt;
                if (Math.Abs(b.Properties.LeftIndent.Value - autoIndent) < 0.5)
                    b.Properties.LeftIndent = null;
            }
            b.ListProperties = null;
        }

        public void ToggleBulletList()
        {
            bool on = _activeParagraph?.Model.ListProperties?.MarkerType == ListMarkerType.Bullet;
            if (on) { ApplyListMutation(RemoveListFormatting); return; }
            var id = Guid.NewGuid();
            ApplyListMutation(b =>
            {
                int level = b.ListProperties?.Level ?? 0;
                b.ListProperties = new ListProperties
                { ListId = id, Level = level, MarkerType = ListMarkerType.Bullet };
                EnsureListLeftIndent(b, level);
            });
        }

        public void ToggleNumberedList()
        {
            bool on = _activeParagraph?.Model.ListProperties?.MarkerType == ListMarkerType.Decimal;
            if (on) { ApplyListMutation(RemoveListFormatting); return; }
            var id = Guid.NewGuid();
            ApplyListMutation(b =>
            {
                int level = b.ListProperties?.Level ?? 0;
                b.ListProperties = new ListProperties
                { ListId = id, Level = level, MarkerType = ListMarkerType.Decimal };
                EnsureListLeftIndent(b, level);
            });
        }

        public void ToggleMultilevelList()
        {
            ApplyListMutation(b =>
            {
                if (b.ListProperties is null)
                    b.ListProperties = new ListProperties
                    { ListId = Guid.NewGuid(), Level = 0, MarkerType = ListMarkerType.Decimal };
                else
                    b.ListProperties.Level = (b.ListProperties.Level + 1) % 9;
                EnsureListLeftIndent(b, b.ListProperties.Level);
            });
        }

        public void ApplyListType(ListMarkerType markerType)
        {
            if (markerType == ListMarkerType.None)
            {
                ApplyListMutation(RemoveListFormatting);
                return;
            }
            var id = Guid.NewGuid();
            ApplyListMutation(b =>
            {
                int level = b.ListProperties?.Level ?? 0;
                b.ListProperties = new ListProperties
                { ListId = id, Level = level, MarkerType = markerType };
                EnsureListLeftIndent(b, level);
            });
        }

        public void ApplyCustomBulletList(string marker)
        {
            var id = Guid.NewGuid();
            ApplyListMutation(b =>
            {
                int level = b.ListProperties?.Level ?? 0;
                b.ListProperties = new ListProperties
                {
                    ListId = id,
                    Level = level,
                    MarkerType = ListMarkerType.Custom,
                    CustomMarker = string.IsNullOrEmpty(marker) ? "•" : marker
                };
                EnsureListLeftIndent(b, level);
            });
        }

        public ListProperties? GetActiveListProperties()
        {
            // Абзац ячейки в Paragraphs не лежит, _activeParagraph про него не знает.
            // Без этой ветки список внутри таблицы считался «не списком»: диалог
            // настроек и метка на линейке ничего не получали.
            var p = TableActiveCellParagraph ?? _activeParagraph?.Model;
            if (p?.ListProperties is null) return null;
            var clone = p.ListProperties.Clone();
            // Позиция текста для диалога = фактический левый отступ абзаца.
            clone.TextIndentPt = p.Properties.LeftIndent ?? clone.EffectiveTextIndentPt();
            return clone;
        }

        public void ApplyListSettings(ListProperties settings)
        {
            if (settings is null) { ApplyListMutation(b => b.ListProperties = null); return; }
            var id = settings.ListId != Guid.Empty ? settings.ListId : Guid.NewGuid();
            ApplyListMutation(b =>
            {
                int level = b.ListProperties?.Level ?? settings.Level;
                var lp = settings.Clone();
                lp.ListId = id;
                lp.Level = level;
                b.ListProperties = lp;

                // Позиция текста из диалога → левый отступ абзаца (единый источник правды).
                if (settings.TextIndentPt.HasValue)
                    b.Properties.LeftIndent = Math.Max(0.0, settings.TextIndentPt.Value);
                else
                    EnsureListLeftIndent(b, level);
            });
        }

        // Тянем метку — двигается ТОЛЬКО метка, текст/абзац не трогаем. Метка ходит независимо:
        // влево — до края страницы, вправо — свободно (ограничение по правому краю даёт линейка).
        // От наезда цифры на текст удерживает зазор при отрисовке (по реальной ширине символа).
        public void SetListMarkerIndentPt(double pt)
            => ApplyListMutation(b =>
            {
                if (b.ListProperties is null) return;
                var ps = _document.PageSettings;
                double textWidthPt =
                    (ps.GetPhysicalWidthMm() - ps.MarginLeftMm - ps.MarginGutterMm - ps.MarginRightMm) * 72.0 / 25.4;

                // Слева метка не ограничивается. Любой предел здесь — левое поле страницы,
                // ширина зоны — срабатывал раньше линейки и останавливал жест там, где место
                // ещё было видно. Пусть номер уезжает куда угодно: это выбор пользователя,
                // и он его видит.
                // Правый предел метки — правый край текстовой зоны. Место под текст здесь не
                // резервируется: когда рядом с номером текст перестаёт помещаться, раскладка
                // отдаёт номеру первую строку целиком, а текст уводит на вторую
                // (SKTextRenderer.BuildLayout, MarkerOwnsFirstLine). Прежний резерв «цифра +
                // зазор + минимум текста» останавливал метку задолго до этого перехода.
                double upperPt = textWidthPt - (b.Properties.RightIndent ?? 0.0);

                b.ListProperties.MarkerIndentPt = Math.Min(pt, upperPt);
            });

        public void SetListTextIndentPt(double pt)
            => ApplyListMutation(b =>
            {
                if (b.ListProperties is null) return;
                b.Properties.LeftIndent = Math.Max(0.0, pt);
            });

        // Перетаскивание абзацной стрелки в списке: задаёт зазор между цифрой и текстом.
        // gapPt — расстояние от правого края цифры до начала текста первой строки.
        public void SetListMarkerGapPt(double gapPt)
            => ApplyListMutation(b =>
            {
                if (b.ListProperties is null) return;
                b.ListProperties.MarkerTextMinGapPt = Math.Max(0.0, gapPt);
            });

        // Схема по умолчанию для многоуровневого списка: чередование десятичной, буквенной и
        // римской нумерации по уровням (как часто делают в структурных списках).
        public static System.Collections.Generic.List<ListMarkerType> DefaultMultilevelScheme() => new()
        {
            ListMarkerType.Decimal, ListMarkerType.LowerAlpha, ListMarkerType.LowerRoman,
            ListMarkerType.Decimal, ListMarkerType.LowerAlpha, ListMarkerType.LowerRoman,
            ListMarkerType.Decimal, ListMarkerType.LowerAlpha, ListMarkerType.LowerRoman
        };

        public void ApplyMultilevelList()
            => ApplyMultilevelScheme(DefaultMultilevelScheme());

        public void ApplyMultilevelScheme(System.Collections.Generic.List<ListMarkerType> scheme)
        {
            if (scheme is null || scheme.Count == 0) return;
            var id = Guid.NewGuid();
            ApplyListMutation(b =>
            {
                int level = b.ListProperties?.Level ?? 0;
                b.ListProperties = new ListProperties
                {
                    ListId = id,
                    Level = level,
                    MarkerType = scheme[0],
                    LevelMarkers = new System.Collections.Generic.List<ListMarkerType>(scheme)
                };
                // Отступ по уровню + абсолютная позиция номера (независима от строк 2+).
                b.Properties.LeftIndent = (level + 1) * ListProperties.DefaultLevelStepPt;
                b.ListProperties.MarkerIndentPt = Math.Max(0.0,
                    (level + 1) * ListProperties.DefaultLevelStepPt - ListProperties.DefaultHangingPt);
            });
        }

        /// <summary>Снимок схемы уровней активного многоуровневого списка (null — нет).</summary>
        public System.Collections.Generic.List<ListMarkerType>? GetActiveListLevelMarkers()
        {
            var lm = _activeParagraph?.Model.ListProperties?.LevelMarkers;
            return lm is null ? null : new System.Collections.Generic.List<ListMarkerType>(lm);
        }

        /// <summary>Понизить уровень элемента списка (глубже). Отступ и маркер следуют за уровнем.</summary>
        public void DemoteListItem()
            => ApplyListMutation(b =>
            {
                if (b.ListProperties is null) return;
                int level = Math.Min(8, b.ListProperties.Level + 1);
                b.ListProperties.Level = level;
                b.Properties.LeftIndent = (level + 1) * ListProperties.DefaultLevelStepPt;
                b.ListProperties.MarkerIndentPt = Math.Max(0.0,
                    (level + 1) * ListProperties.DefaultLevelStepPt - ListProperties.DefaultHangingPt);
            });

        /// <summary>Повысить уровень элемента списка (выше). Отступ и маркер следуют за уровнем.</summary>
        public void PromoteListItem()
            => ApplyListMutation(b =>
            {
                if (b.ListProperties is null) return;
                int level = Math.Max(0, b.ListProperties.Level - 1);
                b.ListProperties.Level = level;
                b.Properties.LeftIndent = (level + 1) * ListProperties.DefaultLevelStepPt;
                b.ListProperties.MarkerIndentPt = Math.Max(0.0,
                    (level + 1) * ListProperties.DefaultLevelStepPt - ListProperties.DefaultHangingPt);
            });

        /// <summary>true — активный абзац является элементом списка (для обработки Tab).</summary>
        public bool IsActiveParagraphList()
            => _activeParagraph?.Model.ListProperties is not null;

        // ── ITextEditorCommandTarget: буфер обмена ────────────────────────

        public void Cut()
        {
            if (IsReadOnly) return;
            if (CutDelegate != null) CutDelegate.Invoke();
            else _activeParagraph?.RequestFocus();
        }

        public void Copy()
        {
            if (CopyDelegate != null) { CopyDelegate.Invoke(); return; }
            string? docText = GetDocumentSelectedText();
            if (docText is not null) { CopyToClipboardAsync(docText); return; }
            _activeParagraph?.RequestFocus();
        }

        public void Paste()
        {
            if (IsReadOnly) return;
            if (PasteDelegate != null) PasteDelegate.Invoke();
            else _activeParagraph?.RequestFocus();
        }

        void ITextEditorCommandTarget.SelectAll() => SelectAll();
        public void Undo() => UndoDelegate?.Invoke();
        public void Redo() => RedoDelegate?.Invoke();

        // ── ITextEditorCommandTarget: вставка ─────────────────────────────

        public void InsertTable(int rows, int columns) => InsertBlockAtCaret(BuildEmptyTable(rows, columns));

        public void InsertTableBlock(TableBlock table) => InsertBlock(table);

        /// <summary>
        /// Вставляет TableBlock сразу после заданного якорного параграфа.
        /// В отличие от InsertBlock, не зависит от _activeParagraph — позволяет
        /// точно контролировать позицию при последовательной вставке нескольких блоков.
        /// Возвращает post-anchor ParagraphBlock (пустой параграф после таблицы),
        /// созданный NormalizeTableAnchors внутри RebuildParagraphViewModels.
        /// </summary>
        public ParagraphBlock? InsertTableBlockAfterParagraph(TableBlock table, ParagraphBlock anchor)
        {
            if (_document.Sections.Count == 0) return null;
            var section = _document.Sections[0];

            int idx = section.Blocks.IndexOf(anchor);
            if (idx >= 0)
                section.Blocks.Insert(idx + 1, table);
            else
                section.Blocks.Add(table);

            RebuildParagraphViewModels();

            int tblIdx = section.Blocks.IndexOf(table);
            if (tblIdx >= 0 && tblIdx + 1 < section.Blocks.Count
                && section.Blocks[tblIdx + 1] is ParagraphBlock postAnchor)
                return postAnchor;

            return null;
        }
        public void InsertImage(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath)) return;

            byte[] data;
            try { data = System.IO.File.ReadAllBytes(filePath); }
            catch { return; }

            InsertImageBytes(data, System.IO.Path.GetExtension(filePath));
        }

        /// <summary>
        /// Вставляет картинку из готовых байтов (файл или буфер обмена). Файл кладётся в проект,
        /// в документ добавляется ImageBlock под кареткой. Операция попадает в Undo.
        /// </summary>
        public void InsertImageBytes(byte[] data, string ext)
        {
            if (IsReadOnly) return;
            if (data is null || data.Length == 0) return;

            // Файлы картинок хранятся внутри проекта (ZIP), доступ — через контекст активной вкладки.
            var ctx = CoreServices.GetService<ITabCollection>()?.ActiveTab?.Context;
            if (ctx is null) return;

            if (string.IsNullOrWhiteSpace(ext)) ext = ".png";
            if (!ext.StartsWith(".")) ext = "." + ext;
            string fileName = $"img_{System.Guid.NewGuid():N}{ext}";
            ctx.WriteFile($"TextEditor/Images/{fileName}", data);

            // Байты картинки уходят на диск сразу. Кеш восстановления хранит только
            // JSON документа, поэтому после аварии он сошлётся на этот файл — и тот
            // обязан существовать. Без сброса в RELEASE он остался бы в памяти
            // открытого архива до ближайшего сохранения.
            ctx.FlushStorage();

            // Размер по умолчанию берём из самого изображения (пиксели при 96 dpi -> пункты),
            // ширину разумно ограничиваем, сохраняя пропорции.
            double widthPt = 200, heightPt = 150;
            try
            {
                using var bmp = SkiaSharp.SKBitmap.Decode(data);
                if (bmp is { Width: > 0, Height: > 0 })
                {
                    widthPt = bmp.Width * 72.0 / 96.0;
                    heightPt = bmp.Height * 72.0 / 96.0;
                    const double maxWidthPt = 400.0;
                    if (widthPt > maxWidthPt)
                    {
                        double k = maxWidthPt / widthPt;
                        widthPt = maxWidthPt;
                        heightPt *= k;
                    }
                }
            }
            catch { }

            var image = new ImageBlock
            {
                ImageFileName = fileName,
                WidthPt = widthPt,
                HeightPt = heightPt
            };

            // Снимок до/после вставки — для Ctrl+Z.
            BeginEditDelegate?.Invoke("Вставка изображения");
            var section = _document.Sections[0];

            // Картинка «в тексте» — обычный символ в строке под кареткой. Отдельным блоком
            // она становится только когда включено обтекание.
            if (!InsertImageIntoLine(section, image))
            {
                int idx = _activeParagraph is not null ? section.Blocks.IndexOf(_activeParagraph.Model) : -1;
                if (idx >= 0)
                    section.Blocks.Insert(idx + 1, image);
                else
                    section.Blocks.Add(image);
            }
            CommitEditDelegate?.Invoke();

            StructureChanged?.Invoke();
        }

        /// <summary>
        /// Кладёт картинку в хранилище встроенных объектов раздела и вставляет её
        /// в абзац каретки одним символом. Возвращает false, если режим обтекания
        /// не Inline либо каретки нет — тогда вызывающий кладёт картинку блоком.
        /// </summary>
        private bool InsertImageIntoLine(SectionModel section, ImageBlock image)
        {
            if (image.WrapMode != WrapMode.Inline) return false;

            var target = GetCaretTargetDelegate?.Invoke();
            ParagraphBlock? para = target?.Para;
            int at = target?.CharIndex ?? 0;

            // Канвас каретку не отдал (например, вставка сразу после загрузки документа) —
            // работаем по активному абзацу.
            if (para is null && _activeParagraph is not null)
            {
                para = _activeParagraph.Model;
                at = Math.Max(0, Math.Min(_activeParagraph.SelectionStart, para.TotalLength));
            }

            if (para is null) return false;

            section.InlineObjects.Add(image);
            para.InsertInlineObject(at, image.Id);
            InlineImageInserted?.Invoke(para, at);
            return true;
        }

        /// <summary>
        /// Картинка встроена в строку: абзац и позиция символа. Канвас по этому событию
        /// ставит каретку сразу за картинкой и пересобирает раскладку абзаца.
        /// </summary>
        public event Action<ParagraphBlock, int>? InlineImageInserted;

        /// <summary>
        /// Состав объектов в строках абзаца изменился (картинка ушла из строки или
        /// пришла в неё). Канвас перечитывает текст абзаца и сбрасывает его раскладку.
        /// </summary>
        public event Action<ParagraphBlock>? InlineObjectsChanged;

        /// <summary>
        /// Все абзацы документа, включая абзацы ячеек таблиц и надписей: картинка
        /// в строке может стоять в любом из них.
        /// </summary>
        private IEnumerable<ParagraphBlock> EnumerateAllParagraphs()
        {
            foreach (var section in _document.Sections)
            {
                foreach (var block in section.Blocks)
                {
                    if (block is ParagraphBlock para)
                    {
                        yield return para;
                    }
                    else if (block is TableBlock table)
                    {
                        foreach (var cell in table.Cells)
                            foreach (var cellPara in cell.Paragraphs)
                                yield return cellPara;
                    }
                    else if (block is FloatingTextBlock floatingText)
                    {
                        foreach (var textPara in floatingText.Paragraphs)
                            yield return textPara;
                    }
                }

                foreach (var block in section.FloatingObjects)
                    if (block is FloatingTextBlock floatingText)
                        foreach (var textPara in floatingText.Paragraphs)
                            yield return textPara;
            }
        }

        /// <summary>Раздел, в чьём хранилище объектов строки лежит картинка (или null).</summary>
        private SectionModel? FindSectionOfInlineImage(ImageBlock image)
        {
            foreach (var section in _document.Sections)
                if (section.InlineObjects.Contains(image))
                    return section;
            return null;
        }

        /// <summary>
        /// Абзац, в строке которого стоит картинка, и позиция её символа.
        /// </summary>
        public (ParagraphBlock Para, int CharIndex)? FindInlineImageOwner(ImageBlock image)
        {
            if (image is null) return null;
            foreach (var para in EnumerateAllParagraphs())
            {
                int idx = para.IndexOfInlineObject(image.Id);
                if (idx >= 0) return (para, idx);
            }
            return null;
        }

        /// <summary>
        /// Выводит картинку из строки текста в отдельный плавающий блок с заданным
        /// обтеканием: символ из абзаца убирается, картинка переезжает из хранилища
        /// объектов строки в поток блоков сразу за своим абзацем. Смещения задаются
        /// вызывающим по текущему положению картинки, чтобы она не прыгнула.
        /// </summary>
        public bool ConvertInlineImageToBlock(ImageBlock image, WrapMode mode,
            double offsetXPt, double offsetYPt)
        {
            if (IsReadOnly || image is null) return false;

            var section = FindSectionOfInlineImage(image);
            if (section is null) return false;

            var owner = FindInlineImageOwner(image);
            if (owner is { } found)
                found.Para.SpliceText(found.CharIndex, found.CharIndex + 1, string.Empty);

            section.InlineObjects.Remove(image);
            image.WrapMode = mode;
            image.OffsetXPt = offsetXPt;
            image.OffsetYPt = offsetYPt;

            int at = owner is { } o ? section.Blocks.IndexOf(o.Para) : -1;
            if (at >= 0) section.Blocks.Insert(at + 1, image);
            else section.Blocks.Add(image);

            if (owner is { } changed) InlineObjectsChanged?.Invoke(changed.Para);
            StructureChanged?.Invoke();
            return true;
        }

        /// <summary>
        /// Встраивает картинку-блок в строку текста. Место выбирается по её месту в
        /// потоке: конец предыдущего абзаца, иначе начало следующего — так картинка
        /// остаётся там же, где стояла, но становится обычным символом.
        /// </summary>
        public bool ConvertBlockImageToInline(ImageBlock image)
        {
            if (IsReadOnly || image is null) return false;

            SectionModel? section = null;
            foreach (var s in _document.Sections)
                if (s.Blocks.Contains(image) || s.FloatingObjects.Contains(image))
                { section = s; break; }
            if (section is null) return false;

            int idx = section.Blocks.IndexOf(image);
            section.Blocks.Remove(image);
            section.FloatingObjects.Remove(image);

            ParagraphBlock? owner = null;
            int at = 0;

            if (idx > 0)
            {
                for (int i = Math.Min(idx, section.Blocks.Count) - 1; i >= 0; i--)
                    if (section.Blocks[i] is ParagraphBlock prev)
                    { owner = prev; at = prev.TotalLength; break; }
            }

            if (owner is null)
            {
                for (int i = Math.Max(0, idx); i < section.Blocks.Count; i++)
                    if (section.Blocks[i] is ParagraphBlock next)
                    { owner = next; at = 0; break; }
            }

            bool addedParagraph = false;
            if (owner is null)
            {
                owner = new ParagraphBlock();
                section.Blocks.Add(owner);
                addedParagraph = true;
            }

            image.WrapMode = WrapMode.Inline;
            image.OffsetXPt = 0.0;
            image.OffsetYPt = 0.0;
            section.InlineObjects.Add(image);
            owner.InsertInlineObject(at, image.Id);

            if (addedParagraph) RebuildParagraphViewModels();
            InlineObjectsChanged?.Invoke(owner);
            StructureChanged?.Invoke();
            return true;
        }
        /// <summary>
        /// Вставляет точную копию картинки (все свойства: размер, кроп, поворот, рамка).
        /// Файл переиспользуется — он уже лежит в проекте. Плавающая копия слегка смещается,
        /// чтобы не легла точно на оригинал. Операция попадает в Undo.
        /// </summary>
        public ImageBlock? InsertImageClone(ImageBlock src, ImageBlock? anchorAfter = null)
        {
            if (IsReadOnly || src is null) return null;

            bool floating = src.WrapMode != WrapMode.Inline;
            var image = new ImageBlock
            {
                ImageFileName = src.ImageFileName,
                WidthPt = src.WidthPt,
                HeightPt = src.HeightPt,
                LockAspectRatio = src.LockAspectRatio,
                RotationDeg = src.RotationDeg,
                Opacity = src.Opacity,
                BorderColor = src.BorderColor,
                BorderThicknessPt = src.BorderThicknessPt,
                FlipHorizontal = src.FlipHorizontal,
                FlipVertical = src.FlipVertical,
                CropLeftFrac = src.CropLeftFrac,
                CropTopFrac = src.CropTopFrac,
                CropRightFrac = src.CropRightFrac,
                CropBottomFrac = src.CropBottomFrac,
                WrapMode = src.WrapMode,
                WrapSide = src.WrapSide,
                PinnedPage = src.PinnedPage,
                Alignment = src.Alignment,
                Anchor = src.Anchor,
                WrapPadTopPt = src.WrapPadTopPt,
                WrapPadBottomPt = src.WrapPadBottomPt,
                WrapPadLeftPt = src.WrapPadLeftPt,
                WrapPadRightPt = src.WrapPadRightPt,
                // Плавающую копию смещаем, чтобы её было видно рядом с оригиналом.
                OffsetXPt = floating ? src.OffsetXPt + 12.0 : src.OffsetXPt,
                OffsetYPt = floating ? src.OffsetYPt + 12.0 : src.OffsetYPt,
                ZOrder = src.ZOrder,
                AltText = src.AltText
            };

            BeginEditDelegate?.Invoke("Вставка изображения");
            var section = _document.Sections[0];
            // Копия картинки «в тексте» встаёт символом под кареткой, как и оригинал.
            if (!InsertImageIntoLine(section, image))
            {
                // Копию ставим сразу ПОСЛЕ исходной картинки (anchorAfter) — тогда плавающая
                // копия окажется на той же странице рядом. Иначе — после активного абзаца,
                // иначе — в конец.
                int idx = anchorAfter is not null ? section.Blocks.IndexOf(anchorAfter) : -1;
                if (idx < 0)
                    idx = _activeParagraph is not null ? section.Blocks.IndexOf(_activeParagraph.Model) : -1;
                if (idx >= 0)
                    section.Blocks.Insert(idx + 1, image);
                else
                    section.Blocks.Add(image);
            }
            CommitEditDelegate?.Invoke();

            StructureChanged?.Invoke();
            return image;
        }

        /// <summary>
        /// Вставляет картинку из байтов, перенося свойства из шаблона. Байты пишутся
        /// НОВЫМ файлом в ZIP ТЕКУЩЕГО проекта — поэтому работает и при копировании
        /// между проектами (файл переносится в целевой проект). Возвращает блок.
        /// </summary>
        public ImageBlock? InsertImageWithProps(byte[] data, ImageBlock template,
            double floatOffsetXPt, double floatOffsetYPt)
        {
            if (IsReadOnly || data is null || data.Length == 0 || template is null) return null;

            var ctx = CoreServices.GetService<ITabCollection>()?.ActiveTab?.Context;
            if (ctx is null) return null;

            string fileName = $"img_{System.Guid.NewGuid():N}.png";
            ctx.WriteFile($"TextEditor/Images/{fileName}", data);

            // См. InsertImageBytes: файл должен лежать на диске к моменту, когда на
            // него сошлётся кеш восстановления.
            ctx.FlushStorage();

            bool floating = template.WrapMode != WrapMode.Inline;
            var image = new ImageBlock
            {
                ImageFileName = fileName,
                WidthPt = template.WidthPt,
                HeightPt = template.HeightPt,
                LockAspectRatio = template.LockAspectRatio,
                RotationDeg = template.RotationDeg,
                Opacity = template.Opacity,
                BorderColor = template.BorderColor,
                BorderThicknessPt = template.BorderThicknessPt,
                FlipHorizontal = template.FlipHorizontal,
                FlipVertical = template.FlipVertical,
                CropLeftFrac = template.CropLeftFrac,
                CropTopFrac = template.CropTopFrac,
                CropRightFrac = template.CropRightFrac,
                CropBottomFrac = template.CropBottomFrac,
                WrapMode = template.WrapMode,
                WrapSide = template.WrapSide,
                PinnedPage = template.PinnedPage,
                Alignment = template.Alignment,
                Anchor = template.Anchor,
                WrapPadTopPt = template.WrapPadTopPt,
                WrapPadBottomPt = template.WrapPadBottomPt,
                WrapPadLeftPt = template.WrapPadLeftPt,
                WrapPadRightPt = template.WrapPadRightPt,
                // Плавающую картинку ставим у курсора (переданное смещение от текстовой
                // области страницы каретки). Inline течёт в потоке — смещения не нужны.
                OffsetXPt = floating ? floatOffsetXPt : 0.0,
                OffsetYPt = floating ? floatOffsetYPt : 0.0,
                ZOrder = template.ZOrder,
                AltText = template.AltText
            };

            BeginEditDelegate?.Invoke("Вставка изображения");
            var section = _document.Sections[0];
            if (!InsertImageIntoLine(section, image))
            {
                // Вставляем в поток у каретки (после активного абзаца). Нет каретки —
                // в начало документа, чтобы вставка была видна, а не улетела в конец.
                int idx = _activeParagraph is not null ? section.Blocks.IndexOf(_activeParagraph.Model) : -1;
                if (idx < 0 && section.Blocks.Count > 0) idx = 0;
                if (idx >= 0)
                    section.Blocks.Insert(idx + 1, image);
                else
                    section.Blocks.Add(image);
            }
            CommitEditDelegate?.Invoke();

            StructureChanged?.Invoke();
            return image;
        }

        /// <summary>
        /// Вставляет картинку в поток сразу за указанным блоком. Используется вставкой
        /// из буфера: картинка, через которую прошло выделение, возвращается между теми
        /// же абзацами, что и в исходном тексте. Id у копии новый — вставок может быть
        /// несколько, и они не должны делить один объект.
        /// </summary>
        public ImageBlock? InsertImageAfterBlock(ImageBlock src, BlockModel? after)
        {
            if (IsReadOnly || src is null || _document.Sections.Count == 0) return null;

            var section = _document.Sections[0];
            var copy = CloneImageBlock(src);

            int idx = after is not null ? section.Blocks.IndexOf(after) : -1;
            if (idx >= 0) section.Blocks.Insert(idx + 1, copy);
            else section.Blocks.Add(copy);

            StructureChanged?.Invoke();
            return copy;
        }

        public void RemoveImage(ImageBlock image)
        {
            if (IsReadOnly) return;
            if (image is null) return;

            // Картинка в строке: убираем её символ из абзаца, иначе в тексте осталась бы
            // пустая позиция, по которой каретка ходит, а показывать нечего.
            var inlineSection = FindSectionOfInlineImage(image);
            if (inlineSection is not null)
            {
                BeginEditDelegate?.Invoke("Удаление изображения");
                var owner = FindInlineImageOwner(image);
                if (owner is { } found)
                    found.Para.SpliceText(found.CharIndex, found.CharIndex + 1, string.Empty);
                inlineSection.InlineObjects.Remove(image);
                CommitEditDelegate?.Invoke();

                if (owner is { } changed) InlineObjectsChanged?.Invoke(changed.Para);
                StructureChanged?.Invoke();
                return;
            }

            foreach (var section in _document.Sections)
            {
                if (section.Blocks.Contains(image) || section.FloatingObjects.Contains(image))
                {
                    // Снимок до/после удаления — для Ctrl+Z.
                    BeginEditDelegate?.Invoke("Удаление изображения");
                    section.Blocks.Remove(image);
                    section.FloatingObjects.Remove(image);
                    CommitEditDelegate?.Invoke();
                    StructureChanged?.Invoke();
                    return;
                }
            }
        }

        /// <summary>
        /// Убирает из хранилища объектов строки картинки, на которые больше не ссылается
        /// ни один run: их символы удалены правкой текста (Delete, Backspace, вырезание).
        /// Вызывается перед сохранением — до этого момента объект должен жить, иначе
        /// отмена удаления восстановила бы ссылку в никуда.
        /// Возвращает число выброшенных объектов.
        /// </summary>
        public int PurgeOrphanInlineObjects()
        {
            var referenced = new HashSet<Guid>();
            foreach (var para in EnumerateAllParagraphs())
                foreach (var id in para.EnumerateInlineImageIds())
                    referenced.Add(id);

            int removed = 0;
            foreach (var section in _document.Sections)
            {
                for (int i = section.InlineObjects.Count - 1; i >= 0; i--)
                {
                    if (referenced.Contains(section.InlineObjects[i].Id)) continue;
                    section.InlineObjects.RemoveAt(i);
                    removed++;
                }
            }
            return removed;
        }

        /// <summary>
        /// Выдаёт каждой картинке в строках абзаца собственную копию объекта. Нужно после
        /// вставки из буфера: иначе вставленный абзац ссылался бы на ту же картинку, что и
        /// исходный, и изменение размера в одном месте меняло бы её в обоих.
        /// Ссылки на пропавшие объекты (вставка из другого документа) убираются вместе
        /// с символом — пустого места в тексте не остаётся.
        /// </summary>
        public void MaterializeInlineImages(ParagraphBlock? para)
        {
            if (para is null || _document.Sections.Count == 0) return;

            var cells = para.ToCharCells();
            bool changed = false;

            for (int i = cells.Count - 1; i >= 0; i--)
            {
                if (cells[i].InlineImageId is not Guid sourceId) continue;

                var source = FindInlineImageById(sourceId);
                if (source is null)
                {
                    cells.RemoveAt(i);
                    changed = true;
                    continue;
                }

                var copy = CloneImageBlock(source);
                _document.Sections[0].InlineObjects.Add(copy);
                cells[i] = new ParagraphBlock.CharCell(cells[i].Ch, cells[i].Props, copy.Id);
                changed = true;
            }

            if (!changed) return;

            para.RebuildFromCharCells(cells);
            InlineObjectsChanged?.Invoke(para);
        }

        /// <summary>
        /// Убирает из текста символы-заполнители объекта, за которыми не стоит живой
        /// картинки. Обратная сторона <see cref="PurgeOrphanInlineObjects"/>: тот чистит
        /// картинки, потерявшие своё место в тексте, а здесь чистится место, потерявшее
        /// картинку. Без этого шрифт рисует голый U+FFFC как рамку с надписью OBJ —
        /// в тексте появляется квадратик, за которым ничего нет и который нельзя ни
        /// выделить как картинку, ни настроить.
        ///
        /// Такой символ остаётся после правок, где ссылка на объект терялась, а текст
        /// нет: перенос между документами, откат структурной правки, старые файлы.
        /// Возвращает число удалённых символов.
        /// </summary>
        public int PurgeDanglingObjectChars()
        {
            int removed = 0;

            foreach (var para in EnumerateAllParagraphs())
            {
                var cells = para.ToCharCells();
                bool changed = false;

                for (int i = cells.Count - 1; i >= 0; i--)
                {
                    if (cells[i].Ch != Models.Inline.RunModel.ObjectPlaceholder) continue;

                    // Ссылка есть и картинка на месте — это нормальный объект.
                    if (cells[i].InlineImageId is Guid id && FindInlineImageById(id) is not null)
                        continue;

                    cells.RemoveAt(i);
                    changed = true;
                    removed++;
                }

                if (!changed) continue;

                para.RebuildFromCharCells(cells);
                InlineObjectsChanged?.Invoke(para);
            }

            return removed;
        }

        private ImageBlock? FindInlineImageById(Guid id)
        {
            foreach (var section in _document.Sections)
                foreach (var block in section.InlineObjects)
                    if (block is ImageBlock image && image.Id == id)
                        return image;
            return null;
        }

        /// <summary>Копия картинки со всеми свойствами и новым Id. Файл переиспользуется.</summary>
        private static ImageBlock CloneImageBlock(ImageBlock src) => new()
        {
            ImageFileName = src.ImageFileName,
            WidthPt = src.WidthPt,
            HeightPt = src.HeightPt,
            LockAspectRatio = src.LockAspectRatio,
            RotationDeg = src.RotationDeg,
            Opacity = src.Opacity,
            BorderColor = src.BorderColor,
            BorderThicknessPt = src.BorderThicknessPt,
            FlipHorizontal = src.FlipHorizontal,
            FlipVertical = src.FlipVertical,
            CropLeftFrac = src.CropLeftFrac,
            CropTopFrac = src.CropTopFrac,
            CropRightFrac = src.CropRightFrac,
            CropBottomFrac = src.CropBottomFrac,
            WrapMode = src.WrapMode,
            WrapSide = src.WrapSide,
            PinnedPage = src.PinnedPage,
            Alignment = src.Alignment,
            Anchor = src.Anchor,
            WrapPadTopPt = src.WrapPadTopPt,
            WrapPadBottomPt = src.WrapPadBottomPt,
            WrapPadLeftPt = src.WrapPadLeftPt,
            WrapPadRightPt = src.WrapPadRightPt,
            ZOrder = src.ZOrder,
            AltText = src.AltText
        };

        public void InsertShape(ShapeType st) { }
        public void InsertFloatingTextBox() { }
        public void InsertPageBreak()
        {
            if (IsReadOnly) return;
            InsertBlock(new BreakBlock { BreakType = BreakType.Page });
            NormalizeBreakAnchors();

            // Уведомляем DocumentCanvas о том какой якорь нужно сфокусировать.
            // Canvas применит переход ПОСЛЕ перестройки _layouts, поэтому
            // прямой вызов RequestFocusAtPosition здесь не подходит — _layouts ещё старые.
            if (_document.Sections.Count == 0) return;
            var blocks = _document.Sections[0].Blocks;
            for (int i = 0; i < blocks.Count - 1; i++)
            {
                if (blocks[i] is not BreakBlock { BreakType: BreakType.Page }) continue;
                if (blocks[i + 1] is ParagraphBlock anchorBlock)
                {
                    OnPageBreakInserted?.Invoke(anchorBlock);
                    break;
                }
            }
        }
        public void InsertSectionBreak(BreakType t) => InsertBlock(new BreakBlock { BreakType = t });
        public void InsertFootnote() => AddAnnotation(InlineAnnotationType.Footnote);
        public void InsertEndnote() => AddAnnotation(InlineAnnotationType.Endnote);
        public void InsertBookmark(string name) => AddAnnotation(InlineAnnotationType.Bookmark, bookmarkName: name);
        public void InsertHyperlink(string url, string? text) => AddAnnotation(InlineAnnotationType.Hyperlink, url: url);
        public void InsertTOC() { }
        public void InsertComment(string text) => AddAnnotation(InlineAnnotationType.Comment, content: text);

        // ── Таблица ───────────────────────────────────────────────────────
        // Все структурные операции игнорируются в режиме сравнения (read-only):
        // кнопки риббона остаются кликабельными, но данные документа не меняются.
        public void TableAddRow(bool above) { if (IsReadOnly) return; TableAddRowDelegate?.Invoke(above); }
        public void TableAddColumn(bool left) { if (IsReadOnly) return; TableAddColDelegate?.Invoke(left); }
        public void TableDeleteRow() { if (IsReadOnly) return; TableDeleteRowDelegate?.Invoke(); }
        public void TableDeleteColumn() { if (IsReadOnly) return; TableDeleteColDelegate?.Invoke(); }
        public void TableDelete() { if (IsReadOnly) return; TableDeleteDelegate?.Invoke(); }

        public void TableMergeCells() { if (IsReadOnly) return; TableMergeCellsDelegate?.Invoke(); }
        public void TableSplitCell() { if (IsReadOnly) return; TableSplitCellDelegate?.Invoke(); }
        public void TableDivideCell(bool vertical) { if (IsReadOnly) return; TableDivideCellDelegate?.Invoke(vertical); }
        public void TableSetCellHAlign(Writersword.Modules.TextEditor.Models.Styles.TextAlignment align)
        { if (IsReadOnly) return; TableSetCellHAlignDelegate?.Invoke(align); }
        public void TableSetCellVAlign(int vAlign) { if (IsReadOnly) return; TableSetCellVAlignDelegate?.Invoke(vAlign); }

        public void TableSetCellPadding(double topPt, double bottomPt, double leftPt, double rightPt)
        { if (IsReadOnly) return; TableSetCellPaddingDelegate?.Invoke(topPt, bottomPt, leftPt, rightPt); }

        public (double TopPt, double BottomPt, double LeftPt, double RightPt)? TableGetCellPadding()
            => TableGetCellPaddingDelegate?.Invoke();

        // Переключение инструмента правкой документа не является, поэтому работает
        // и в режиме только для чтения — рисование границ канвас всё равно не даст.
        public void TableSetLineTool(int tool) => TableSetLineToolDelegate?.Invoke(tool);
        public int TableGetLineTool() => TableGetLineToolDelegate?.Invoke() ?? 0;

        public void TableSetCellAlign(int vAlign,
            Writersword.Modules.TextEditor.Models.Styles.TextAlignment hAlign)
        { if (IsReadOnly) return; TableSetCellAlignDelegate?.Invoke(vAlign, hAlign); }

        // Чтение состояния идёт и в режиме сравнения: подсветка кнопок не правка.
        public int? TableGetCellVAlign() => TableGetCellVAlignDelegate?.Invoke();
        public Writersword.Modules.TextEditor.Models.Styles.TextAlignment? TableGetCellHAlign()
            => TableGetCellHAlignDelegate?.Invoke();
        public void TableSetCellBackground(string? color) { if (IsReadOnly) return; TableSetCellBackgroundDelegate?.Invoke(color); }
        public void TableSetCellBorder(string side, BorderStyle style, double thicknessPt, string? color)
        { if (IsReadOnly) return; TableSetCellBorderDelegate?.Invoke(side, style, thicknessPt, color); }
        public void TableSetColumnWidth(double widthMm) { if (IsReadOnly) return; TableSetColumnWidthDelegate?.Invoke(widthMm); }
        public void TableSetRowHeight(double heightPt) { if (IsReadOnly) return; TableSetRowHeightDelegate?.Invoke(heightPt); }
        public void TableAutoFit() { if (IsReadOnly) return; TableAutoFitDelegate?.Invoke(); }
        public void TableDistributeColumns() { if (IsReadOnly) return; TableDistributeColsDelegate?.Invoke(); }
        public void TableDistributeRows() { if (IsReadOnly) return; TableDistributeRowsDelegate?.Invoke(); }
        public void TableSort(int columnIndex, bool ascending) { if (IsReadOnly) return; TableSortDelegate?.Invoke(columnIndex, ascending); }

        public void TableToggleRepeatHeader()
        {
            if (IsReadOnly) return;
            var table = ActiveTable;
            if (table is null) return;
            BeginTableUndoStep(table, "Toggle repeat header");
            table.RepeatHeader = !table.RepeatHeader;
            CommitTableUndoStep();
            FireParagraphFormatChanged();
        }

        public bool TableGetRepeatHeader() => ActiveTable?.RepeatHeader ?? false;

        public void TableToggleSplitMode()
        {
            if (IsReadOnly) return;
            var table = ActiveTable;
            if (table is null) return;
            BeginTableUndoStep(table, "Toggle split mode");
            table.SplitMode = table.SplitMode == Models.Document.TableSplitMode.ByRow
                ? Models.Document.TableSplitMode.ByCell
                : Models.Document.TableSplitMode.ByRow;
            CommitTableUndoStep();
            FireParagraphFormatChanged();
        }
        public bool TableGetSplitModeByCell() =>
            ActiveTable?.SplitMode == Models.Document.TableSplitMode.ByCell;

        public void TableSetBreakLabel(string? text)
        {
            if (IsReadOnly) return;
            var table = ActiveTable; if (table is null) return;
            BeginTableUndoStep(table, "Set break label");
            table.BreakLabel = string.IsNullOrWhiteSpace(text) ? null : text;
            CommitTableUndoStep();
            FireParagraphFormatChanged();
        }
        public void TableSetContinuationLabel(string? text)
        {
            if (IsReadOnly) return;
            var table = ActiveTable; if (table is null) return;
            BeginTableUndoStep(table, "Set continuation label");
            table.ContinuationLabel = string.IsNullOrWhiteSpace(text) ? null : text;
            CommitTableUndoStep();
            FireParagraphFormatChanged();
        }
        public string? TableGetBreakLabel() => ActiveTable?.BreakLabel;
        public string? TableGetContinuationLabel() => ActiveTable?.ContinuationLabel;

        public void RebuildParagraphViewModelsPublic() => RebuildParagraphViewModels();
        public void FireParagraphFormatChanged() => ParagraphFormatChanged?.Invoke();

        // ── Операции с таблицами (модель) ─────────────────────────────────

        // Все операции ниже вызываются из контекстного меню таблицы и меняют модель
        // напрямую. Каждая открывает и закрывает шаг отмены сама: снимок берётся до
        // первой правки и закрывается после последней, включая ветку, где таблица
        // удаляется целиком — иначе Ctrl+Z откатывал бы не эту операцию, а предыдущую.

        public void TableAddRowBelow(TableBlock table, int afterRow)
        {
            if (IsReadOnly) return;
            BeginTableUndoStep(table, "Add row");
            int insertRow = afterRow + 1;
            foreach (var cell in table.Cells)
                if (cell.Row >= insertRow) cell.Row++;
            for (int c = 0; c < table.ColumnCount; c++)
                table.Cells.Add(new TableCell { Row = insertRow, Column = c });
            table.InsertRowMinHeight(insertRow);
            table.RowCount++;
            CommitTableUndoStep();
        }

        public void TableAddRowAbove(TableBlock table, int beforeRow)
        {
            if (IsReadOnly) return;
            BeginTableUndoStep(table, "Add row");
            foreach (var cell in table.Cells)
                if (cell.Row >= beforeRow) cell.Row++;
            for (int c = 0; c < table.ColumnCount; c++)
                table.Cells.Add(new TableCell { Row = beforeRow, Column = c });
            table.InsertRowMinHeight(beforeRow);
            table.RowCount++;
            CommitTableUndoStep();
        }

        public void TableDeleteRow(TableBlock table, int row)
        {
            if (IsReadOnly) return;
            BeginUndoStep("Delete row");
            if (table.RowCount <= 1)
            {
                _document.Sections[0].Blocks.Remove(table);
                CommitUndoStep();
                RebuildParagraphViewModels();
                return;
            }
            table.Cells.RemoveAll(c => c.Row == row);
            foreach (var cell in table.Cells)
                if (cell.Row > row) cell.Row--;
            table.RemoveRowMinHeight(row);
            table.RowCount--;
            CommitUndoStep();
        }

        public void TableAddColumnRight(TableBlock table, int afterCol)
        {
            if (IsReadOnly) return;
            BeginTableUndoStep(table, "Add column");
            int insertCol = afterCol + 1;
            foreach (var cell in table.Cells)
                if (cell.Column >= insertCol) cell.Column++;
            for (int r = 0; r < table.RowCount; r++)
                table.Cells.Add(new TableCell { Row = r, Column = insertCol });
            table.Columns.Insert(insertCol,
                new TableColumnDefinition { WidthType = TableColumnWidthType.Auto });
            table.ColumnCount++;
            CommitTableUndoStep();
        }

        public void TableAddColumnLeft(TableBlock table, int beforeCol)
        {
            if (IsReadOnly) return;
            BeginTableUndoStep(table, "Add column");
            foreach (var cell in table.Cells)
                if (cell.Column >= beforeCol) cell.Column++;
            for (int r = 0; r < table.RowCount; r++)
                table.Cells.Add(new TableCell { Row = r, Column = beforeCol });
            table.Columns.Insert(beforeCol,
                new TableColumnDefinition { WidthType = TableColumnWidthType.Auto });
            table.ColumnCount++;
            CommitTableUndoStep();
        }

        public void TableDeleteColumn(TableBlock table, int col)
        {
            if (IsReadOnly) return;
            BeginUndoStep("Delete column");
            if (table.ColumnCount <= 1)
            {
                _document.Sections[0].Blocks.Remove(table);
                CommitUndoStep();
                RebuildParagraphViewModels();
                return;
            }
            table.Cells.RemoveAll(c => c.Column == col);
            foreach (var cell in table.Cells)
                if (cell.Column > col) cell.Column--;
            if (col < table.Columns.Count)
                table.Columns.RemoveAt(col);
            table.ColumnCount--;
            CommitUndoStep();
        }

        public void TableMergeCells(TableBlock table,
            int startRow, int startCol, int endRow, int endCol)
        {
            if (IsReadOnly) return;
            var mainCell = table.GetCell(startRow, startCol);
            if (mainCell is null) return;
            BeginTableUndoStep(table, "Merge cells");

            for (int r = startRow; r <= endRow; r++)
            {
                for (int c = startCol; c <= endCol; c++)
                {
                    if (r == startRow && c == startCol) continue;
                    var cell = table.GetCell(r, c);
                    if (cell is null) continue;
                    bool isEmpty = cell.Paragraphs.Count == 1
                        && string.IsNullOrEmpty(GetCellPlainText(cell));
                    if (!isEmpty)
                        foreach (var para in cell.Paragraphs)
                            mainCell.Paragraphs.Add(para);
                    table.Cells.Remove(cell);
                }
            }
            mainCell.RowSpan = endRow - startRow + 1;
            mainCell.ColSpan = endCol - startCol + 1;
            CommitTableUndoStep();
        }

        public void TableSplitCell(TableBlock table, int row, int col)
        {
            if (IsReadOnly) return;
            var mainCell = table.GetCell(row, col);
            if (mainCell is null || (mainCell.RowSpan == 1 && mainCell.ColSpan == 1)) return;
            BeginTableUndoStep(table, "Split cell");

            int rowSpan = mainCell.RowSpan;
            int colSpan = mainCell.ColSpan;
            mainCell.RowSpan = 1;
            mainCell.ColSpan = 1;

            for (int r = row; r < row + rowSpan; r++)
                for (int c = col; c < col + colSpan; c++)
                {
                    if (r == row && c == col) continue;
                    table.Cells.Add(new TableCell { Row = r, Column = c });
                }
            CommitTableUndoStep();
        }

        public void TableSetColumnWidth(TableBlock table, int colIndex, double widthMm)
        {
            if (IsReadOnly) return;
            if (colIndex < 0 || colIndex >= table.Columns.Count) return;
            BeginTableUndoStep(table, "Resize column");
            table.Columns[colIndex].WidthType = TableColumnWidthType.Fixed;
            table.Columns[colIndex].WidthValue = Math.Max(5.0, widthMm);
            CommitTableUndoStep();
        }

        public TableBlock? FindTable(Func<TableBlock, bool> predicate)
        {
            foreach (var section in _document.Sections)
                foreach (var block in section.Blocks)
                    if (block is TableBlock t && predicate(t))
                        return t;
            return null;
        }

        // ── ITextEditorCommandTarget: макет ───────────────────────────────

        public void SetPageSize(PaperSize size)
        {
            if (IsReadOnly) return;
            _document.PageSettings.ApplyPaperSize(size);
            this.RaisePropertyChanged(nameof(PageSettings));
        }

        public void SetPageOrientation(PageOrientation o)
        {
            if (IsReadOnly) return;
            _document.PageSettings.Orientation = o;
            this.RaisePropertyChanged(nameof(PageSettings));
        }

        public void SetPageMargins(double top, double bottom, double left, double right)
        {
            if (IsReadOnly) return;
            _document.PageSettings.MarginTopMm = top;
            _document.PageSettings.MarginBottomMm = bottom;
            _document.PageSettings.MarginLeftMm = left;
            _document.PageSettings.MarginRightMm = right;
            this.RaisePropertyChanged(nameof(PageSettings));
        }

        public void SetColumns(int count)
        {
            if (IsReadOnly) return;
            _document.ColumnSettings.ColumnCount = count;
        }

        // ── ITextEditorCommandTarget: вид ─────────────────────────────────

        public void SetZoom(double zoom) => Zoom = zoom;

        public void SetViewMode(EditorViewMode mode)
        {
            ViewMode = mode;
            _document.ViewMode = mode;
        }

        /// <summary>Ставит подачу чтения: разворот, одиночный лист или сплошная лента.</summary>
        public void SetReadingFlow(Models.Settings.ReadingFlow flow) => ReadingFlow = flow;

        public void ToggleFullscreen() => IsFullscreen = !IsFullscreen;
        public void ToggleFocusMode() => IsFocusMode = !IsFocusMode;

        public void SetCanvasTheme(CanvasThemePreset preset)
        {
            _document.CanvasSettings.ApplyPreset(preset);
            this.RaisePropertyChanged(nameof(CanvasSettings));
        }

        public void SetCanvasColors(string pageBackground, string textColor)
        {
            _document.CanvasSettings.Preset = CanvasThemePreset.Custom;
            _document.CanvasSettings.PageBackgroundColor = pageBackground;
            _document.CanvasSettings.DefaultTextColor = textColor;
            this.RaisePropertyChanged(nameof(CanvasSettings));
        }

        public void ZoomIn()
        {
            double[] steps = { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 2.5, 3.0, 4.0, 5.0 };
            foreach (double step in steps)
                if (step > Zoom + 0.01) { Zoom = step; return; }
        }

        public void ZoomOut()
        {
            double[] steps = { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 2.5, 3.0, 4.0, 5.0 };
            for (int i = steps.Length - 1; i >= 0; i--)
                if (steps[i] < Zoom - 0.01) { Zoom = steps[i]; return; }
        }

        public void ZoomReset() => Zoom = 1.0;

        // ── ITextEditorCommandTarget: инструменты ─────────────────────────

        public void OpenFind() { }
        public void OpenFindReplace() { }
        public void RunSpellCheck() { }
        public void ShowWordCount() { }
        public void Print() { }
        public void ExportToPdf() { }
        public void ExportToDocx() { }
        public void ExportToTxt() { }
        public void ExportToMarkdown() { }

        // ── Внутренние методы ─────────────────────────────────────────────

        private void ApplyCharProperty(Action<RunProperties> mutate, bool clearAll = false)
        {
            if (IsReadOnly) return;
            _log.Information("[FONT] ApplyCharProperty: tableCell={TC} clearAll={CA} granularDelegate={GD} active={AP} selStart={SS} selEnd={SE}",
                TableActiveCellParagraph is not null, clearAll,
                CommitRunPropertyGranularDelegate is not null, _activeParagraph is not null,
                _selectionStart, _selectionEnd);
            // Ячейка таблицы: применяем операционно ко ВСЕМ выделенным абзацам ячейки (диапазоны
            // отдаёт канвас), а не к одному активному — иначе формат «через строку» не проходил.
            // Без выделения / при очистке — старый снапшотный путь (умеет ставить свойство пустому
            // рану для «ожидающего» форматирования).
            if (TableActiveCellParagraph is not null && !clearAll
                && CommitRunPropertyGranularDelegate is not null)
            {
                var cellRanges = GetCellSelectionRangesDelegate?.Invoke();
                if (cellRanges is { Count: > 0 }
                    && CommitRunPropertyGranularDelegate(cellRanges, mutate, "Format text"))
                {
                    FireCursorContextChanged();
                    return;
                }
            }

            // Ячейка таблицы, очистка форматирования или нет лёгкого пути — снапшот (как было).
            if (TableActiveCellParagraph is not null || clearAll
                || CommitRunPropertyGranularDelegate is null || _activeParagraph is null)
            {
                ApplyCharPropertySnapshot(mutate, clearAll);
                return;
            }

            // Собираем диапазоны (paraId, from, to) — та же логика, что и в снапшотном пути.
            var ranges = new System.Collections.Generic.List<(System.Guid, int, int)>();
            if (SelectionParagraphs.Count > 1)
            {
                for (int idx = 0; idx < SelectionParagraphs.Count; idx++)
                {
                    var pvm = SelectionParagraphs[idx];
                    bool isFirst = idx == 0;
                    bool isLast = idx == SelectionParagraphs.Count - 1;
                    int len = pvm.Model.GetPlainText().Length;
                    int s, e;
                    if (!isFirst && !isLast) { s = 0; e = len; }
                    else { s = isFirst ? pvm.SelectionStart : 0; e = isLast ? pvm.SelectionEnd : len; }
                    s = Math.Clamp(s, 0, len);
                    e = Math.Clamp(e, 0, len);
                    if (e > s) ranges.Add((pvm.Model.Id, s, e));
                }
            }
            else
            {
                int len = _activeParagraph.Model.GetPlainText().Length;
                int s, e;
                if (_selectionEnd > _selectionStart) { s = _selectionStart; e = _selectionEnd; }
                else { s = 0; e = len; }
                s = Math.Clamp(s, 0, len);
                e = Math.Clamp(e, 0, len);
                if (e > s) ranges.Add((_activeParagraph.Model.Id, s, e));
            }

            // Нечего форматировать гранулярно (пустой абзац, пустое выделение) — снапшот:
            // он умеет проставить свойство пустому рану для «ожидающего» форматирования.
            if (ranges.Count == 0)
            {
                ApplyCharPropertySnapshot(mutate, clearAll);
                return;
            }

            bool handled = CommitRunPropertyGranularDelegate(ranges, mutate, "Format text");
            if (!handled)
            {
                ApplyCharPropertySnapshot(mutate, clearAll);
                return;
            }

            // Модель и раскладку обновил канвас (команда + точечный пересбор). Здесь только
            // обновляем состояние тулбара под кареткой.
            FireCursorContextChanged();
        }

        // Прежний снапшотный путь форматирования (полная сериализация документа для отмены).
        // Используется для ячеек таблицы, очистки форматирования и пустых абзацев.
        private void ApplyCharPropertySnapshot(Action<RunProperties> mutate, bool clearAll = false)
        {
            // Режим ячейки таблицы — применяем только к активной ячейке.
            if (TableActiveCellParagraph is not null)
            {
                _log.Information("[FONT] Snapshot cell apply: para len={L} selStart={SS} selEnd={SE}",
                    TableActiveCellParagraph.GetPlainText().Length, _selectionStart, _selectionEnd);
                BeginEditDelegate?.Invoke("Format text");
                ApplyCharPropertyToBlock(TableActiveCellParagraph, _selectionStart, _selectionEnd, mutate, clearAll);
                CommitEditDelegate?.Invoke();
                FireCursorContextChanged();
                ParagraphFormatChanged?.Invoke();
                return;
            }

            if (_activeParagraph is null) return;

            BeginEditDelegate?.Invoke("Format text");

            if (SelectionParagraphs.Count > 1)
            {
                for (int idx = 0; idx < SelectionParagraphs.Count; idx++)
                {
                    var pvm = SelectionParagraphs[idx];
                    bool isFirst = idx == 0;
                    bool isLast = idx == SelectionParagraphs.Count - 1;

                    if (!isFirst && !isLast)
                    {
                        // Средний параграф — целиком (selEnd <= selStart = "нет выделения" = все раны).
                        ApplyCharPropertyToBlock(pvm.Model, 0, 0, mutate, clearAll);
                    }
                    else
                    {
                        // Первый: от SelectionStart до конца (int.MaxValue clamp-ится внутри).
                        // Последний: от 0 до SelectionEnd.
                        int s = isFirst ? pvm.SelectionStart : 0;
                        int e = isLast ? pvm.SelectionEnd : int.MaxValue;
                        if (e > s)
                            ApplyCharPropertyToRange(pvm.Model, s, e, mutate, clearAll);
                    }
                }
            }
            else
            {
                ApplyCharPropertyToBlock(_activeParagraph.Model, _selectionStart, _selectionEnd, mutate, clearAll);
            }

            CommitEditDelegate?.Invoke();
            FireCursorContextChanged();
            // Затронуты только эти абзацы — канвас инвалидирует кэш раскладки точечно,
            // а не сбрасывает весь документ.
            _lastFormatAffected = SelectionParagraphs.Count > 1
                ? SelectionParagraphs.ToList()
                : new[] { _activeParagraph };
            ParagraphFormatChanged?.Invoke();
        }

        private static void ApplyCharPropertyToBlock(
            ParagraphBlock block, int selStart, int selEnd,
            Action<RunProperties> mutate, bool clearAll)
        {
            if (selEnd > selStart)
            {
                ApplyCharPropertyToRange(block, selStart, selEnd, mutate, clearAll);
                return;
            }
            // Нет выделения — применяем ко всем ранам параграфа.
            foreach (var chunk in block.Chunks)
            {
                foreach (var run in chunk.Runs)
                {
                    if (clearAll) run.Properties = null;
                    else
                    {
                        run.Properties ??= new RunProperties();
                        mutate(run.Properties);
                        if (run.Properties.IsDefault()) run.Properties = null;
                    }
                }
                chunk.InvalidateLength();
            }
        }

        private void ApplyParaProperty(Action<ParagraphProperties> mutate)
        {
            if (IsReadOnly) return;

            // Выделен диапазон ячеек: правка идёт по всем их абзацам. Ветка ниже
            // работает с единственным абзацем активной ячейки, и при выделении
            // нескольких ячеек форматирование доставалось только первой.
            var cellParagraphs = GetSelectedCellParagraphsDelegate?.Invoke();
            if (cellParagraphs is { Count: > 0 })
            {
                if (!_suppressFormatSnapshot)
                    BeginEditDelegate?.Invoke("Format paragraph");

                foreach (var para in cellParagraphs)
                    mutate(para.Properties);

                if (!_suppressFormatSnapshot)
                    CommitEditDelegate?.Invoke();

                // Контекст риббона обновляем по абзацу активной ячейки: кнопки
                // выравнивания должны показать новое состояние сразу.
                if (TableActiveCellParagraph is not null)
                {
                    var activeVm = new ParagraphViewModel(TableActiveCellParagraph);
                    CursorContextChanged?.Invoke(BuildCursorContext(activeVm));
                }

                ParagraphFormatChanged?.Invoke();
                return;
            }

            // Режим таблицы: применяем к параграфу активной ячейки (снапшотный путь как был).
            if (TableActiveCellParagraph is not null)
            {
                if (!_suppressFormatSnapshot)
                    BeginEditDelegate?.Invoke("Format paragraph");
                mutate(TableActiveCellParagraph.Properties);
                if (!_suppressFormatSnapshot)
                    CommitEditDelegate?.Invoke();
                var tempVm = new ParagraphViewModel(TableActiveCellParagraph);
                CursorContextChanged?.Invoke(BuildCursorContext(tempVm));
                ParagraphFormatChanged?.Invoke();
                return;
            }

            // Список затронутых абзацев.
            var targets = SelectionParagraphs.Count > 0
                ? SelectionParagraphs.ToList()
                : (_activeParagraph is not null
                    ? new System.Collections.Generic.List<ParagraphViewModel> { _activeParagraph }
                    : null);
            if (targets is null || targets.Count == 0) return;

            // Операционный путь (одиночное форматирование, не во время drag отступов на линейке):
            // гранулярная команда в общий TextUndoStack. Ctrl+Z мгновенный, без тяжёлого снапшота
            // всего документа — на больших документах это и убирало фриз при отмене.
            if (!_suppressFormatSnapshot && CommitParagraphPropertyGranularDelegate is not null)
            {
                var edits = new System.Collections.Generic.List<(System.Guid,
                    Action<ParagraphProperties>, Action<ParagraphProperties>)>();
                foreach (var pvm in targets)
                {
                    var old = pvm.Model.Properties.Clone();
                    edits.Add((pvm.Model.Id, mutate, p => p.CopyFrom(old)));
                }
                if (CommitParagraphPropertyGranularDelegate(edits, "Format paragraph"))
                {
                    FireCursorContextChanged();
                    return;
                }
            }

            // Снапшотный путь: батч-drag отступов (один снапшот на весь drag) или нет делегата.
            if (!_suppressFormatSnapshot)
                BeginEditDelegate?.Invoke("Format paragraph");
            foreach (var pvm in targets)
                mutate(pvm.Model.Properties);
            if (!_suppressFormatSnapshot)
                CommitEditDelegate?.Invoke();
            FireCursorContextChanged();
            // Затронуты только эти абзацы — канвас инвалидирует кэш раскладки точечно,
            // а не сбрасывает весь документ (на больших документах это убирает фриз).
            _lastFormatAffected = targets;
            ParagraphFormatChanged?.Invoke();
        }

        // Через эти два метода в документ попадают все вставляемые блоки: таблица,
        // картинка, фигура, разрыв, сноска. Шага отмены здесь не было ни у одного —
        // Ctrl+Z после вставки таблицы честно отвечал «нечего отменять».
        // Снимок берётся документа, а не блока: меняется состав раздела, и вернуть
        // блок на место по снимку его самого невозможно.
        // Вложенность безопасна: вставка из буфера уже открывает свой шаг снаружи,
        // счётчик глубины в полотне сложит их в один.
        private void InsertBlock(BlockModel block)
        {
            if (IsReadOnly) return;
            if (_document.Sections.Count == 0) return;
            var section = _document.Sections[0];

            BeginUndoStep("Insert block");
            if (_activeParagraph is not null)
            {
                int idx = section.Blocks.IndexOf(_activeParagraph.Model);
                if (idx >= 0)
                {
                    section.Blocks.Insert(idx + 1, block);
                    CommitUndoStep();
                    RebuildParagraphViewModels();
                    return;
                }
            }

            section.Blocks.Add(block);
            CommitUndoStep();
            RebuildParagraphViewModels();
        }

        /// <summary>
        /// Вставляет блок в позицию каретки. Каретка внутри текста разрезает абзац, и
        /// блок встаёт между половинами — как это делает вставка картинки. В начале или
        /// конце абзаца резать нечего, блок просто встаёт перед ним или после него.
        /// Позицию каретки знает только канвас, поэтому она берётся у него делегатом;
        /// без делегата или для абзаца из ячейки таблицы работает прежний путь
        /// «после активного абзаца».
        /// </summary>
        private void InsertBlockAtCaret(BlockModel block)
        {
            if (IsReadOnly) return;
            if (_document.Sections.Count == 0) return;
            var section = _document.Sections[0];

            var target = GetCaretTargetDelegate?.Invoke();
            ParagraphBlock? para = target?.Para;

            // Абзацы ячеек таблицы в Blocks не лежат — IndexOf вернёт -1.
            int paraIdx = para is null ? -1 : section.Blocks.IndexOf(para);
            if (paraIdx < 0)
            {
                // Шаг откроет InsertBlock — второй раз открывать не нужно.
                InsertBlock(block);
                return;
            }

            int plainLen = para!.GetPlainText().Length;
            int cut = Math.Clamp(target?.CharIndex ?? 0, 0, plainLen);

            BeginUndoStep("Insert block");

            if (cut == 0)
            {
                section.Blocks.Insert(paraIdx, block);
                CommitUndoStep();
                RebuildParagraphViewModels();
                return;
            }

            if (cut >= plainLen)
            {
                section.Blocks.Insert(paraIdx + 1, block);
                CommitUndoStep();
                RebuildParagraphViewModels();
                return;
            }

            // Хвост уезжает в новый абзац. Форматирование абзаца наследуется, как при
            // разбиении по Enter, иначе продолжение текста теряет выравнивание, отступы
            // и место в списке.
            var tailRuns = Commands.DocumentModelHelper.DeleteRange(para, cut, plainLen - cut);

            var tail = new ParagraphBlock { Properties = para.Properties.Clone() };
            if (para.ListProperties is not null)
            {
                var lp = para.ListProperties.Clone();
                lp.ContinueNumbering = true;
                tail.ListProperties = lp;
            }
            if (tailRuns.Length > 0)
                Commands.DocumentModelHelper.RestoreRuns(tail, 0, tailRuns);

            section.Blocks.Insert(paraIdx + 1, tail);
            section.Blocks.Insert(paraIdx + 1, block);
            CommitUndoStep();
            RebuildParagraphViewModels();
        }

        private static void ApplyCharPropertyToRange(
    ParagraphBlock block, int selStart, int selEnd,
    Action<RunProperties> mutate, bool clearAll)
        {
            // Посимвольный разбор идёт через ячейки параграфа: обход по run.Text потерял бы
            // ссылку на встроенную картинку, и форматирование куска текста с картинкой
            // превращало бы её в пустой символ-заполнитель.
            var cells = block.ToCharCells();
            for (int i = 0; i < cells.Count; i++)
                cells[i] = new ParagraphBlock.CharCell(
                    cells[i].Ch, cells[i].Props?.Clone(), cells[i].InlineImageId);

            int len = cells.Count;
            selStart = Math.Max(0, Math.Min(selStart, len));
            selEnd = Math.Max(selStart, Math.Min(selEnd, len));

            for (int i = selStart; i < selEnd; i++)
            {
                var cell = cells[i];
                if (clearAll)
                {
                    cells[i] = new ParagraphBlock.CharCell(cell.Ch, null, cell.InlineImageId);
                }
                else
                {
                    var newProps = cell.Props?.Clone() ?? new RunProperties();
                    mutate(newProps);
                    cells[i] = new ParagraphBlock.CharCell(
                        cell.Ch, newProps.IsDefault() ? null : newProps, cell.InlineImageId);
                }
            }

            block.RebuildFromCharCells(cells);
        }

        private static bool RunPropertiesEqualValue(RunProperties? a, RunProperties? b)
        {
            bool aDefault = a is null || a.IsDefault();
            bool bDefault = b is null || b.IsDefault();
            if (aDefault && bDefault) return true;
            if (aDefault || bDefault) return false;
            return a!.FontFamily == b!.FontFamily
                && a.FontSize == b.FontSize
                && a.IsBold == b.IsBold
                && a.IsItalic == b.IsItalic
                && a.IsUnderline == b.IsUnderline
                && a.IsStrikethrough == b.IsStrikethrough
                && a.IsSuperscript == b.IsSuperscript
                && a.IsSubscript == b.IsSubscript
                && a.IsAllCaps == b.IsAllCaps
                && a.IsSmallCaps == b.IsSmallCaps
                && a.TextColor == b.TextColor
                && a.HighlightColor == b.HighlightColor
                && a.Language == b.Language;
        }

        /// <summary>
        /// Гарантирует наличие пустого ParagraphBlock до и после каждой TableBlock.
        /// Якоря невидимы визуально (нулевая высота в layout) но нужны для:
        /// — позиционирования каретки у края таблицы по клику
        /// — вставки параграфов выше/ниже таблицы через Enter
        /// Якорь после таблицы защищён от удаления в DocumentCanvas.
        /// </summary>
        private void NormalizeTableAnchors()
        {
            if (_document.Sections.Count == 0) return;
            var blocks = _document.Sections[0].Blocks;

            for (int i = blocks.Count - 1; i >= 0; i--)
            {
                if (blocks[i] is not TableBlock) continue;

                // Якорь после таблицы: пустой ParagraphBlock (текст пустой).
                // Проверяем через GetPlainText() — Chunks.Count всегда >= 1 даже у нового блока.
                // Якорь после таблицы нужен всегда, в том числе между двумя таблицами: это
                // единственное место, куда встаёт каретка при клике справа от таблицы, и
                // единственный способ разъединить таблицы потом. Зазора он не создаёт —
                // между двумя таблицами раскладка рисует его сбоку, не занимая строки
                // (Layout.cs, ветка «Якорь после таблицы»).
                bool hasAfter = i + 1 < blocks.Count
                    && blocks[i + 1] is ParagraphBlock afterPb
                    && string.IsNullOrEmpty(afterPb.GetPlainText());
                if (!hasAfter)
                    blocks.Insert(i + 1, new ParagraphBlock());

                // Якорь перед таблицей вставляется, только когда блока-параграфа перед ней
                // нет вовсе: иначе каретке негде встать выше таблицы. Требовать здесь именно
                // ПУСТОЙ абзац нельзя — тогда над каждой таблицей появляется лишняя строка:
                // якорь занимает высоту строки в потоке (Layout.cs, AbsXPt: anchorXPt), и
                // удалить её пользователь не может, нормализация возвращает её обратно.
                //
                bool hasBefore = i > 0 && blocks[i - 1] is ParagraphBlock;
                if (!hasBefore)
                    blocks.Insert(i, new ParagraphBlock());
            }
        }

        private void NormalizeBreakAnchors()
        {
            if (_document.Sections.Count == 0) return;
            var blocks = _document.Sections[0].Blocks;

            // Проходим с конца чтобы Insert не сдвигал ещё не обработанные индексы.
            for (int i = blocks.Count - 1; i >= 0; i--)
            {
                if (blocks[i] is not BreakBlock { BreakType: BreakType.Page }) continue;

                // Якорь нужен только если после разрыва вообще нет параграфа
                // (разрыв в конце документа или за ним стоит не-параграф блок).
                // Если параграф уже есть — он и будет якорём: Backspace в его начале
                // вызовет DeleteBreakWithAnchor через IsBreakAnchor.
                bool hasFollowingParagraph = i + 1 < blocks.Count
                    && blocks[i + 1] is ParagraphBlock;
                if (!hasFollowingParagraph)
                    blocks.Insert(i + 1, new ParagraphBlock());
            }
        }

        /// <summary>
        /// Удаляет разрыв страницы вместе с его параграфом-якорем.
        /// Вызывается из DocumentCanvas при Backspace в начале якоря
        /// или при Delete в конце параграфа непосредственно перед разрывом.
        /// </summary>
        public void DeleteBreakWithAnchor(ParagraphViewModel anchor)
        {
            if (IsReadOnly) return;
            var blocks = _document.Sections[0].Blocks;
            int anchorIdx = blocks.IndexOf(anchor.Model);
            if (anchorIdx <= 0 || blocks[anchorIdx - 1] is not BreakBlock) return;

            // Удаляем сначала якорь (больший индекс), потом разрыв (меньший).
            blocks.RemoveAt(anchorIdx);
            blocks.RemoveAt(anchorIdx - 1);

            int vmIdx = Paragraphs.IndexOf(anchor);
            if (vmIdx >= 0) Paragraphs.RemoveAt(vmIdx);

            // Перемещаем каретку в конец предыдущего параграфа.
            int focusIdx = Math.Max(0, vmIdx - 1);
            if (focusIdx < Paragraphs.Count)
                Paragraphs[focusIdx].RequestFocusAtPosition?.Invoke(
                    Paragraphs[focusIdx].PlainText?.Length ?? 0);
        }

        /// <summary>
        /// Удаляет пустой абзац-разделитель между двумя таблицами, ставя их вплотную.
        /// Возвращает false, если абзац разделителем не является — тогда вызывающий
        /// обрабатывает нажатие как обычно.
        ///
        /// Сам по себе такой абзац создаётся по умолчанию (NormalizeTableAnchors ставит
        /// якорь после каждой таблицы) и служит местом, где можно набирать текст между
        /// таблицами. Но удалить его было нельзя ничем: Backspace и Delete считали его
        /// защищённым якорем с обеих сторон, а нормализация возвращала его обратно.
        /// Поставить две таблицы рядом было невозможно.
        /// </summary>
        public bool TryDeleteTableSeparator(ParagraphViewModel anchor)
        {
            if (IsReadOnly) return false;
            if (_document.Sections.Count == 0) return false;
            if (!string.IsNullOrEmpty(anchor.PlainText)) return false;

            var blocks = _document.Sections[0].Blocks;
            int idx = blocks.IndexOf(anchor.Model);

            // Разделитель — только абзац, у которого таблица и сверху, и снизу.
            // Якорь между таблицей и текстом трогать нельзя: он единственное место,
            // откуда можно писать после таблицы.
            if (idx <= 0 || idx + 1 >= blocks.Count) return false;
            if (blocks[idx - 1] is not TableBlock || blocks[idx + 1] is not TableBlock) return false;

            blocks.RemoveAt(idx);

            int vmIdx = Paragraphs.IndexOf(anchor);
            if (vmIdx >= 0) Paragraphs.RemoveAt(vmIdx);

            return true;
        }

        /// <summary>
        /// Вставляет пустой абзац сразу после таблицы и возвращает его. null — если абзац
        /// там уже есть или таблица не найдена.
        ///
        /// Нужен, чтобы разъединить две поставленные вплотную таблицы: между ними нет ни
        /// одного блока, поставить туда каретку нечем, и вернуть разделитель иначе никак.
        /// </summary>
        /// <summary>
        /// Сразу за этой таблицей идёт другая таблица — то есть места для каретки между ними
        /// нет вовсе.
        /// </summary>
        public bool IsTableFollowedByTable(TableBlock table)
        {
            if (_document.Sections.Count == 0) return false;
            var blocks = _document.Sections[0].Blocks;
            int idx = blocks.IndexOf(table);
            return idx >= 0 && idx + 1 < blocks.Count && blocks[idx + 1] is TableBlock;
        }

        /// <summary>
        /// Пустой абзац-якорь сразу за таблицей — то место сбоку-снизу от неё, куда встаёт
        /// каретка при клике правее таблицы. null — если там не абзац или он не пуст.
        /// </summary>
        public ParagraphBlock? GetEmptyAnchorAfterTable(TableBlock table)
        {
            if (_document.Sections.Count == 0) return null;
            var blocks = _document.Sections[0].Blocks;
            int idx = blocks.IndexOf(table);
            if (idx < 0 || idx + 1 >= blocks.Count) return null;
            return blocks[idx + 1] is ParagraphBlock pb && string.IsNullOrEmpty(pb.GetPlainText())
                ? pb
                : null;
        }

        public ParagraphBlock? InsertParagraphAfterTable(TableBlock table)
        {
            if (IsReadOnly) return null;
            if (_document.Sections.Count == 0) return null;

            var blocks = _document.Sections[0].Blocks;
            int idx = blocks.IndexOf(table);
            if (idx < 0) return null;
            if (idx + 1 < blocks.Count && blocks[idx + 1] is ParagraphBlock) return null;

            var para = new ParagraphBlock();
            blocks.Insert(idx + 1, para);
            return para;
        }

        private void AddAnnotation(
            InlineAnnotationType type,
            string? bookmarkName = null,
            string? content = null,
            string? url = null)
        {
            if (IsReadOnly) return;
            _document.Annotations.Add(new InlineAnnotation
            {
                Type = type,
                BookmarkName = bookmarkName,
                Content = content,
                Url = url
            });
        }

        private static TableBlock BuildEmptyTable(int rows, int columns)
        {
            var table = new TableBlock { RowCount = rows, ColumnCount = columns };
            for (int c = 0; c < columns; c++)
                table.Columns.Add(new TableColumnDefinition { WidthType = TableColumnWidthType.Auto });
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < columns; c++)
                    table.Cells.Add(new TableCell { Row = r, Column = c });
            return table;
        }

        private static string GetCellPlainText(TableCell cell)
        {
            var sb = new StringBuilder();
            foreach (var para in cell.Paragraphs)
                foreach (var chunk in para.Chunks)
                    foreach (var run in chunk.Runs)
                        sb.Append(run.Text);
            return sb.ToString();
        }

        private void RebuildStyleNames()
        {
            AvailableStyleNames.Clear();
            foreach (var style in _document.Styles)
                AvailableStyleNames.Add(
                    style.DisplayName.Length > 0 ? style.DisplayName : style.Name);
        }

        private void RebuildParagraphViewModels()
        {
            NormalizeTableAnchors();
            NormalizeBreakAnchors();
            IsBulkRebuilding = true;
            try
            {
                Paragraphs.Clear();
                if (_document.Sections.Count == 0) return;
                foreach (var block in _document.Sections[0].Blocks)
                    if (block is ParagraphBlock para)
                        Paragraphs.Add(CreateParagraphViewModel(para));
            }
            finally
            {
                IsBulkRebuilding = false;
            }
        }

        public void DeleteSelectedParagraphs()
        {
            if (IsReadOnly) return;
            var toDelete = Paragraphs.Where(p => p.IsSelected).ToList();
            if (toDelete.Count == 0) return;

            int firstIdx = Paragraphs.IndexOf(toDelete[0]);
            int focusIdx = Math.Max(0, firstIdx - 1);

            var blocks = _document.Sections[0].Blocks;
            foreach (var pvm in toDelete)
            {
                // Если удаляемый параграф — якорь разрыва страницы, удаляем и сам BreakBlock.
                int blockIdx = blocks.IndexOf(pvm.Model);
                if (blockIdx > 0 && blocks[blockIdx - 1] is BreakBlock { BreakType: BreakType.Page })
                    blocks.RemoveAt(blockIdx - 1);  // BreakBlock удалён; якорь сместился на -1

                blocks.Remove(pvm.Model);
                Paragraphs.Remove(pvm);
            }

            if (Paragraphs.Count == 0)
            {
                var empty = new ParagraphBlock();
                blocks.Add(empty);
                Paragraphs.Add(CreateParagraphViewModel(empty));
            }

            Paragraphs[Math.Min(focusIdx, Paragraphs.Count - 1)].RequestFocus();
        }

        private ParagraphViewModel CreateParagraphViewModel(ParagraphBlock block)
        {
            var vm = new ParagraphViewModel(block);
            vm.RequestAddAfter = AddParagraphAfter;
            vm.RequestDelete = pvm => DeleteParagraph(pvm);
            vm.RequestMergeWithPrevious = MergeParagraphWithPrevious;
            vm.RequestSelectAll = SelectAll;
            vm.RequestClearSelection = ClearSelection;
            vm.RequestGetDocumentSelectedText = GetDocumentSelectedText;
            vm.OnActivated = SetActiveParagraph;
            vm.RequestDeleteSelected = DeleteSelectedParagraphs;
            vm.OnSelectionChanged = _ => FireCursorContextChanged();
            return vm;
        }

        private static async void CopyToClipboardAsync(string text)
        {
            try
            {
                var lifetime = Avalonia.Application.Current?.ApplicationLifetime
                    as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
                var clipboard = lifetime?.MainWindow?.Clipboard;
                if (clipboard is not null)
                    await clipboard.SetTextAsync(text);
            }
            catch { }
        }

        // ── Расширенные методы модели таблицы ────────────────────────────

        public void TableCellSetBackground(TableCell cell, string? color)
        {
            if (IsReadOnly) return;
            cell.BackgroundColor = color;
            ParagraphFormatChanged?.Invoke();
        }

        public static void TableCellSetBorder(TableCell cell, string side,
            BorderStyle style, double thicknessPt, string? color)
        {
            var b = cell.Borders;
            if (color is not null) b.Color = color;
            b.ThicknessPt = thicknessPt > 0 ? thicknessPt : b.ThicknessPt;
            switch (side)
            {
                case "top": b.Top = style; break;
                case "bottom": b.Bottom = style; break;
                case "left": b.Left = style; break;
                case "right": b.Right = style; break;
                case "all":
                case "outer": b.Top = b.Bottom = b.Left = b.Right = style; break;
                case "inner": b.Top = b.Bottom = b.Left = b.Right = style; break;
            }
        }

        public void TableCellSetHAlign(TableCell cell,
            Writersword.Modules.TextEditor.Models.Styles.TextAlignment align)
        {
            if (IsReadOnly) return;
            foreach (var para in cell.Paragraphs)
                para.Properties.Alignment = align;
            ParagraphFormatChanged?.Invoke();
        }

        public void TableCellSetVAlign(TableCell cell, int vAlign)
        {
            if (IsReadOnly) return;
            cell.VerticalAlignment = (VerticalAlignment)vAlign;
            ParagraphFormatChanged?.Invoke();
        }

        public void TableAutoFitColumns(TableBlock table)
        {
            if (IsReadOnly) return;
            BeginTableUndoStep(table, "Autofit columns");
            for (int i = 0; i < table.Columns.Count; i++)
            {
                table.Columns[i].WidthType = TableColumnWidthType.Auto;
                table.Columns[i].WidthValue = 0;
            }
            CommitTableUndoStep();
            ParagraphFormatChanged?.Invoke();
        }

        public void TableDistributeColumnsEvenly(TableBlock table)
        {
            if (IsReadOnly) return;
            int cols = table.ColumnCount;
            if (cols == 0) return;
            BeginTableUndoStep(table, "Distribute columns");
            double each = 100.0 / cols;
            for (int i = 0; i < table.Columns.Count; i++)
            {
                table.Columns[i].WidthType = TableColumnWidthType.Percent;
                table.Columns[i].WidthValue = each;
            }
            CommitTableUndoStep();
            ParagraphFormatChanged?.Invoke();
        }

        public void TableSortByColumn(TableBlock table, int col, bool ascending)
        {
            if (IsReadOnly) return;
            if (col < 0 || col >= table.ColumnCount) return;

            var rows = new List<(int RowIdx, string SortKey, List<TableCell> Cells)>();
            for (int r = 0; r < table.RowCount; r++)
            {
                var cells = table.Cells.Where(c => c.Row == r).ToList();
                var sortCell = table.GetCell(r, col);
                string key = sortCell is not null ? GetCellPlainText(sortCell) : "";
                rows.Add((r, key, cells));
            }

            var sorted = ascending
                ? rows.OrderBy(x => double.TryParse(x.SortKey, out var d) ? d : double.MaxValue)
                      .ThenBy(x => x.SortKey, StringComparer.CurrentCulture).ToList()
                : rows.OrderByDescending(x => double.TryParse(x.SortKey, out var d) ? d : double.MinValue)
                      .ThenByDescending(x => x.SortKey, StringComparer.CurrentCulture).ToList();

            // Снимок берётся здесь, а не в начале метода: до этой точки шли только
            // чтение и сортировка списка, модель не менялась. Открывать шаг раньше
            // значило бы записать в историю выход по любому из ранних return.
            BeginTableUndoStep(table, "Sort table");
            for (int newRow = 0; newRow < sorted.Count; newRow++)
                foreach (var cell in sorted[newRow].Cells)
                    cell.Row = newRow;
            CommitTableUndoStep();

            ParagraphFormatChanged?.Invoke();
        }

        public void PasteTextAtCursor(string text)
        {
            if (IsReadOnly) return;
            if (_activeParagraph is null) return;

            string[] lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            int caretPos = _activeParagraph.SelectionStart;
            string before = _activeParagraph.PlainText?[..caretPos] ?? "";
            string after = _activeParagraph.PlainText?[caretPos..] ?? "";

            if (lines.Length == 1)
            {
                _activeParagraph.Model.SpliceText(caretPos, caretPos, lines[0]);
                _activeParagraph.RefreshPlainTextFromModel();
                int newPos = caretPos + lines[0].Length;
                _activeParagraph.SelectionStart = newPos;
                _activeParagraph.SelectionEnd = newPos;
                _activeParagraph.RequestFocusAtPosition?.Invoke(newPos);
                return;
            }

            _activeParagraph.Model.SpliceText(caretPos, (before + after).Length, lines[0]);
            _activeParagraph.RefreshPlainTextFromModel();
            ParagraphViewModel prev = _activeParagraph;

            for (int i = 1; i < lines.Length - 1; i++)
            {
                var newVm = AddParagraphAfter(prev);
                newVm.PlainText = lines[i];
                prev = newVm;
            }

            var last = AddParagraphAfter(prev);
            last.Model.SpliceText(0, 0, lines[^1] + after);
            last.RefreshPlainTextFromModel();
            last.RequestFocusAtPosition?.Invoke(lines[^1].Length);
        }
    }
}