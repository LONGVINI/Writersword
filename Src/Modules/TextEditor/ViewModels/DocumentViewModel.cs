using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ReactiveUI;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Writersword.Core.Models.Print;
using Writersword.Core.Services;
using Writersword.Core.Interfaces.Services.Storage;
using Writersword.Core.Interfaces.Services.UI;
using Writersword.Core.Interfaces.WorkFlows;
using Writersword.Core.Models.Backup;
using Writersword.Modules.TextEditor.Resources;
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
        /// <summary>
        /// Пересобрать раскладку целиком. Нужен там, где изменилось общее для всего
        /// документа: шаг табуляции по умолчанию попадает в резолвер стилей при его
        /// сборке, и точечный пересчёт затронутых абзацев про него ничего не знает.
        /// </summary>
        public Action? RelayoutAllDelegate { get; set; }

        public void RelayoutAll() => RelayoutAllDelegate?.Invoke();

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
        // Правки выделенной фигуры уходят в канвас: выделение живёт там же, где
        // раскладка, и вью-модель о нём ничего не знает.
        public Func<(ShapeType Type, WrapMode Wrap, WrapSide WrapSide, ShapeDashStyle Dash, ShapeArrowHead StartArrow, ShapeArrowHead EndArrow, string? FillColor, string? StrokeColor, double StrokeThicknessPt, double CornerRadiusPt, double Opacity, double WidthPt, double HeightPt, double RotationDeg, bool LockAspect, int PinnedPage, bool HasFillImage, bool FillImageStretch)?>? GetSelectedShapeInfoDelegate { get; set; }
        public Action<ShapeType>? SetShapeTypeDelegate { get; set; }
        public Action<string?>? SetShapeFillDelegate { get; set; }
        public Action<string?>? SetShapeStrokeDelegate { get; set; }
        public Action<double>? SetShapeStrokeThicknessDelegate { get; set; }
        public Action<ShapeDashStyle>? SetShapeDashDelegate { get; set; }
        public Action<double>? SetShapeCornerRadiusDelegate { get; set; }
        public Action<ShapeArrowHead, ShapeArrowHead>? SetShapeArrowsDelegate { get; set; }
        public Action<double>? SetShapeOpacityDelegate { get; set; }
        public Action<double>? SetShapeWidthDelegate { get; set; }
        public Action<double>? SetShapeHeightDelegate { get; set; }
        public Action<double>? SetShapeRotationDelegate { get; set; }
        public Action<bool>? SetShapeLockAspectDelegate { get; set; }
        public Action<WrapMode>? SetShapeWrapModeDelegate { get; set; }
        public Action<WrapSide>? SetShapeWrapSideDelegate { get; set; }
        public Action<double, double, double, double>? SetShapeWrapPaddingDelegate { get; set; }
        public Action<bool>? SetShapePinnedDelegate { get; set; }
        public Action<bool>? SetShapeZOrderDelegate { get; set; }
        public Action<string?>? SetShapeFillImageDelegate { get; set; }
        public Action<bool>? SetShapeFillImageStretchDelegate { get; set; }
        public Action? DeleteSelectedShapeDelegate { get; set; }

        public Action<ShapeDashStyle>? SetImageBorderDashDelegate { get; set; }
        public Func<ShapeDashStyle?>? GetSelectedImageBorderDashDelegate { get; set; }
        public Action<ShapeType>? SetImageShapeTypeDelegate { get; set; }
        public Func<ShapeType?>? GetSelectedImageShapeTypeDelegate { get; set; }
        public Action<double>? SetImageCornerRadiusDelegate { get; set; }
        public Func<double?>? GetSelectedImageCornerRadiusDelegate { get; set; }

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
        public Action<ImageBorderAlign>? SetImageBorderAlignDelegate { get; set; }
        public Func<ImageBorderAlign?>? GetSelectedImageBorderAlignDelegate { get; set; }
        public Func<(double WidthPt, double HeightPt, double Opacity, string? BorderColor, double BorderThicknessPt)?>? GetSelectedImageStyleDelegate { get; set; }
        public Action? ToggleImageFlipHorizontalDelegate { get; set; }
        public Action? ToggleImageFlipVerticalDelegate { get; set; }
        public Action<bool>? SetImageCropModeDelegate { get; set; }
        public Func<bool>? GetImageCropModeDelegate { get; set; }
        public Action<double, double, double, double>? SetImageWrapPaddingDelegate { get; set; }
        public Func<(double TopPt, double BottomPt, double LeftPt, double RightPt)?>? GetSelectedImageWrapPaddingDelegate { get; set; }

        /// <summary>
        /// Что выделено на листе: фигура, картинка, есть ли внутри картинка и
        /// является ли объект линией. Лента «Формат» одна на оба объекта и по
        /// этому признаку показывает группы, которые есть только у одного из них.
        /// </summary>
        public Func<(bool HasShape, bool HasImage, bool HasFillImage, bool IsLine)?>? GetSelectedFloatingKindDelegate { get; set; }

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

        /// <summary>
        /// Кладёт в историю готовый шаг отмены. Нужен операциям, у которых свой, дешёвый
        /// способ откатиться, — снятию оглавления прежде всего: ему довольно помнить
        /// снятые абзацы, а снимок всей рукописи для них несоразмерен.
        ///
        /// Пара «начать — завершить» здесь не годится: она заводит снимок документа сама,
        /// а нам нужно положить в тот же стек свою команду, чтобы Ctrl+Z шёл по общему
        /// порядку и не путал, чья очередь откатываться.
        /// </summary>
        public Action<Writersword.Core.Interfaces.Modules.IUndoableCommand>?
            PushUndoCommandDelegate { get; set; }

        /// <summary>
        /// Ставит каретку на абзац немедленно и без прокрутки, в отличие от
        /// <see cref="GoToParagraph"/>, который откладывает переход до конца кадра и
        /// довозит вид до места сам.
        ///
        /// Нужен отмене и повтору: полотно прокручивает вид к каретке сразу, как только
        /// команда вернула управление, и к тому мигу каретка обязана стоять на новом
        /// месте. Отложенный переход к этому моменту ещё не случился — и прокрутка шла по
        /// номеру слайса от прежнего состава документа, то есть в никуда.
        /// </summary>
        public Action<int>? SetCaretToParagraphDelegate { get; set; }

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

        // ── Оглавление и навигатор ────────────────────────────────────────

        /// <summary>
        /// Карта «абзац — номер страницы» текущей раскладки. Ставит канвас: по тексту
        /// документа номер страницы не выводится, его знает только раскладка.
        /// </summary>
        public Func<System.Collections.Generic.Dictionary<Guid, int>>? GetBlockPageNumbersDelegate { get; set; }

        /// <summary>
        /// Переход к абзацу по его месту в потоке документа. Ставит канвас.
        /// Второй довод — класть ли прежнее место в историю прыжков.
        /// </summary>
        public Action<int, bool>? GoToParagraphDelegate { get; set; }

        /// <summary>
        /// Карта «абзац — номер страницы». Пустая, пока раскладка не построена: показывать
        /// в оглавлении выдуманные номера хуже, чем не показывать никаких.
        /// </summary>
        public System.Collections.Generic.Dictionary<Guid, int> GetBlockPageNumbers()
            => GetBlockPageNumbersDelegate?.Invoke()
               ?? new System.Collections.Generic.Dictionary<Guid, int>();

        /// <summary>
        /// Уводит рукопись к абзацу по его месту в потоке документа. Прежнее место
        /// кладётся в историю прыжков — оттуда его достаёт Alt+Влево.
        /// </summary>
        public void GoToParagraph(int paragraphIndex) => GoToParagraphDelegate?.Invoke(paragraphIndex, true);

        /// <summary>
        /// То же, но без записи в историю прыжков. Для возвратов каретки, которые
        /// прыжком не являются: пересборка оглавления, отмена, повтор. Они ставят
        /// каретку туда же, где человек и был, и в историю им не место.
        /// </summary>
        public void GoToParagraphKeepHistory(int paragraphIndex)
            => GoToParagraphDelegate?.Invoke(paragraphIndex, false);

        /// <summary>
        /// Показать или убрать навигатор. Ставит модуль: панель принадлежит ему, а не
        /// рукописи, и документ о её существовании ничего не знает — команда риббона
        /// просто проходит его насквозь.
        /// </summary>
        public Action? ToggleNavigatorDelegate { get; set; }

        /// <inheritdoc/>
        public void ToggleNavigator() => ToggleNavigatorDelegate?.Invoke();

        /// <summary>
        /// Вид рабочей области изменился: режим показа, масштаб, число листов в ряду или
        /// цвет листа. Модуль ловит это и кладёт значение в общие настройки программы —
        /// вид одинаков во всех рукописях и в файл документа не пишется.
        /// </summary>
        public event Action? ViewPreferenceChanged;

        /// <summary>Сообщает наружу, что вид рабочей области изменился.</summary>
        private void RaiseViewPreferenceChanged() => ViewPreferenceChanged?.Invoke();

        /// <summary>
        /// Каретка перешла в другой абзац потока. Число — место абзаца в Paragraphs.
        /// Навигатор подсвечивает по нему главу, в которой человек сейчас работает.
        /// </summary>
        public event Action<int>? ActiveParagraphChanged;

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
        /// Набор стилей документа пополнился или изменился.
        ///
        /// Канвас строит указатель имён стилей один раз и о стиле, дописанном позже, сам
        /// не узнаёт: строка сослалась бы на имя, которого в указателе нет, и вышла бы
        /// обычным текстом. Именно так вело себя оглавление в рукописи, начатой до
        /// появления стилей Toc1…Toc9.
        /// </summary>
        public event Action? StylesChanged;

        /// <summary>
        /// Сообщает, что список стилей документа изменился: обновляет список имён для
        /// ленты и просит канвас пересобрать резолвер стилей.
        /// </summary>
        public void RaiseStylesChanged()
        {
            RebuildStyleNames();
            StylesChanged?.Invoke();
        }

        /// <summary>
        /// Номера страниц в оглавлениях устарели: раскладка ещё не пересчитана под новое
        /// содержимое потока. Канвас ловит это, дожидается пересчёта и зовёт
        /// <see cref="ApplyTocPageNumbers"/>.
        /// </summary>
        /// <remarks>
        /// Признак — обновлять ли оглавления, которым самообновление выключено. Его
        /// поднимают явные действия человека: вставка, правка настроек, кнопка
        /// «Обновить». Сдвиг страниц сам по себе такого права не даёт.
        /// </remarks>
        public event Action<bool>? TocPageNumbersStale;

        /// <summary>Просит хозяина раскладки пересчитать номера страниц в оглавлениях.</summary>
        private void RequestTocPageNumbers(bool force = false) => TocPageNumbersStale?.Invoke(force);

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

        /// <summary>
        /// Сообщает, что параметры страницы поменялись целиком: размер бумаги,
        /// ориентация, поля, колонки. По этому уведомлению канвас пересобирает
        /// геометрию листа и резолвер стилей, а линейка — свои поля. Нужен тем,
        /// кто меняет настройки в обход вью-модели: импорт и снимки Undo.
        /// </summary>
        public void RaisePageSettingsChanged() => this.RaisePropertyChanged(nameof(PageSettings));

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
        //
        // Счётчик, а не признак: операции вкладываются друг в друга — пересборка
        // оглавления сносит старые строки и тут же вставляет новые, — и внутренний
        // «выход» снял бы признак посреди внешней операции.
        private int _bulkRebuildDepth;

        public bool IsBulkRebuilding => _bulkRebuildDepth > 0;

        private void BeginBulkRebuild() => _bulkRebuildDepth++;

        private void EndBulkRebuild()
        {
            if (_bulkRebuildDepth > 0) _bulkRebuildDepth--;
        }

        /// <summary>
        /// Устанавливается DocumentCanvas. Вызывается при изменении preview-шрифта.
        /// null = preview снят. Никаких изменений модели — canvas сам строит временный лейаут.
        /// </summary>
        // Делегаты live-preview шрифта. Канвас сам вычисляет затронутые абзацы и
        // ячейки по своему состоянию выделения, поэтому сюда передаются только команды
        // начала сессии, имя шрифта при наведении и завершение (коммит/отмена).
        public Action? BeginFontPreviewDelegate { get; set; }
        public Action<string>? PreviewFontFamilyDelegate { get; set; }
        public Action<bool, string?>? EndFontPreviewDelegate { get; set; }

        // Возврат клавиатурного фокуса редактору (канвасу) после работы с лентой.
        public Action? FocusEditorDelegate { get; set; }

        public EditorViewMode ViewMode
        {
            get => _viewMode;
            set
            {
                bool changed = _viewMode != value;

                this.RaiseAndSetIfChanged(ref _viewMode, value);
                this.RaisePropertyChanged(nameof(IsSpreadReading));
                this.RaisePropertyChanged(nameof(IsColumnReading));

                if (changed) RaiseViewPreferenceChanged();
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
        /// Вид рабочей области при правке: цвет листа и текста, свет, обработка
        /// картинок, что убирается в режиме фокуса.
        ///
        /// Объект живой, как и настройки чтения: его правит лента, а канвас читает
        /// при каждой отрисовке. О смене сообщает <see cref="ReadingVisualChanged"/> —
        /// раскладка от цвета листа не зависит, и пересобирать её незачем.
        /// </summary>
        public Models.Settings.EditorViewSettings EditorView { get; } = new();

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
            set
            {
                int clamped = Math.Clamp(value, 0, 12);
                bool changed = _pagesPerRow != clamped;

                this.RaiseAndSetIfChanged(ref _pagesPerRow, clamped);

                if (changed) RaiseViewPreferenceChanged();
            }
        }

        public double Zoom
        {
            get => _zoom;
            set
            {
                double clamped = Math.Max(0.25, Math.Min(5.0, value));
                bool changed = Math.Abs(_zoom - clamped) > 0.0001;

                this.RaiseAndSetIfChanged(ref _zoom, clamped);

                // Масштаб держится в модели документа, но в файл оттуда не уезжает:
                // поле помечено как несохраняемое. Живёт он в общих настройках — их и
                // обновляет сообщение наружу.
                _document.Zoom = clamped;

                if (changed) RaiseViewPreferenceChanged();
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

            // Рукопись могла прийти из файла, записанного версией, которая теряла
            // настройки оглавлений. Строки на месте, настроек нет — восстанавливаем по
            // самим строкам, пока никто не успел их спросить.
            RestoreLostTocSettings();
        }

        // ── Потерянные настройки оглавления ───────────────────────────────

        /// <summary>
        /// Восстанавливает настройки оглавлений по самим строкам рукописи.
        ///
        /// Снимок документа какое-то время терял список настроек, а строки сохранял: после
        /// открытия такого файла оглавление было на листе, но для программы не существовало.
        /// Лента не показывала свою вкладку, «Обновить» молчал, а отметки табуляции
        /// оставались там, где их застала последняя правка, — номера страниц расходились
        /// по строкам вкривь и вкось.
        ///
        /// Восстановленное не угадывается: уровни, заголовок, заполнитель и его плотность
        /// читаются из самих строк. Домысливать приходится только сдвиг по уровням, и тот
        /// считается по фактическим отступам.
        /// </summary>
        private void RestoreLostTocSettings()
        {
            if (_document.Sections.Count == 0) return;

            foreach (var block in _document.Sections[0].Blocks)
            {
                if (block is not ParagraphBlock para) continue;
                if (para.Properties.TocOwnerId is not System.Guid owner) continue;

                EnsureTocSettings(owner);
            }
        }

        /// <summary>
        /// Настройки оглавления по опознавателю. Потерянные восстанавливаются по строкам,
        /// поэтому null не возвращается никогда: раз строка носит опознаватель, оглавление
        /// в рукописи есть.
        /// </summary>
        public Models.Toc.TocSettings TocSettingsFor(System.Guid ownerId)
            => EnsureTocSettings(ownerId);

        private Models.Toc.TocSettings EnsureTocSettings(System.Guid ownerId)
        {
            _document.TableOfContents ??= new System.Collections.Generic.List<Models.Toc.TocSettings>();

            foreach (var toc in _document.TableOfContents)
                if (toc.Id == ownerId) return toc;

            var restored = new Models.Toc.TocSettings { Id = ownerId };
            _document.TableOfContents.Add(restored);

            if (_document.Sections.Count == 0) return restored;

            int minLevel = int.MaxValue;
            int maxLevel = 0;
            bool sawTitle = false;
            bool sawPages = false;
            bool leaderTaken = false;

            double minLevelIndent = 0;
            double deeperIndent = 0;
            int deeperLevel = 0;

            foreach (var block in _document.Sections[0].Blocks)
            {
                if (block is not ParagraphBlock para) continue;

                var props = para.Properties;
                if (props.TocOwnerId != ownerId) continue;

                if (props.TocEntryLevel <= 0)
                {
                    sawTitle = true;
                    string title = para.GetPlainText();
                    if (!string.IsNullOrWhiteSpace(title)) restored.Title = title;
                    continue;
                }

                int level = props.TocEntryLevel;
                if (level < minLevel)
                {
                    minLevel = level;
                    minLevelIndent = props.LeftIndent ?? 0;
                }
                if (level > maxLevel) maxLevel = level;

                if (level > minLevel && deeperLevel == 0)
                {
                    deeperLevel = level;
                    deeperIndent = props.LeftIndent ?? 0;
                }

                if (props.TabStops is not { Count: > 0 }) continue;

                sawPages = true;

                if (leaderTaken) continue;
                leaderTaken = true;

                var stop = props.TabStops[0];
                restored.Leader = (Models.Toc.TocLeader)(int)stop.Leader;
                if (stop.LeaderDensity > 0) restored.LeaderDensity = stop.LeaderDensity;
            }

            if (maxLevel <= 0) return restored;

            restored.MinLevel = minLevel;
            restored.MaxLevel = maxLevel;
            restored.ShowTitle = sawTitle;
            restored.ShowPageNumbers = sawPages;

            // Шаг сдвига — по двум настоящим строкам разных уровней. Одинаковые отступы
            // означают, что сдвиг выключен: человек мог снять его галочкой или свести
            // строки стрелками линейки, и восстанавливать его против воли незачем.
            if (deeperLevel > minLevel)
            {
                double step = (deeperIndent - minLevelIndent) / (deeperLevel - minLevel);
                if (step > 0.5)
                {
                    restored.IndentByLevel = true;
                    restored.LevelIndentPt = step;
                }
                else
                {
                    restored.IndentByLevel = false;
                }
            }

            // Отметки табуляции могли застыть на прежних отступах: пока настроек не было,
            // переставить их было некому. Сейчас настройки есть — ставим числа на место,
            // не трогая ни текста строк, ни их отступов.
            double textWidthPt = TocService.TextWidthPt(_document);

            foreach (var block in _document.Sections[0].Blocks)
            {
                if (block is not ParagraphBlock para) continue;
                if (para.Properties.TocOwnerId != ownerId) continue;

                TocService.RefreshEntryTabStop(para.Properties, restored, textWidthPt);
            }

            return restored;
        }

        // ── Активный параграф ─────────────────────────────────────────────

        public void SetActiveParagraph(ParagraphViewModel vm)
        {
            // Выходим из режима таблицы при клике на обычный параграф.
            TableActiveCellParagraph = null;
            _activeParagraph = vm;
            FireCursorContextChanged();
            ActiveParagraphChanged?.Invoke(Paragraphs.IndexOf(vm));
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

            // Разные гарнитуры внутри выделения. null означает «не одна» —
            // поле шрифта в ленте показывает пустоту, как в Word. Отличать это от
            // «шрифт не задан» нужно отдельным признаком: null у самого FontFamily
            // означает совсем другое — наследование от стиля абзаца.
            bool fontsDiffer = false;
            string? firstFont = null;

            if (pvm.SelectionEnd > pvm.SelectionStart)
            {
                int offset = 0;
                foreach (var chunk in block.Chunks)
                {
                    foreach (var run in chunk.Runs)
                    {
                        int runStart = offset;
                        int runEnd = offset + run.Text.Length;
                        offset = runEnd;

                        // Ран целиком вне выделения — пропускаем.
                        if (runEnd <= pvm.SelectionStart) continue;
                        if (runStart >= pvm.SelectionEnd) break;

                        // Первый пересёкшийся ран задаёт остальные свойства ленты:
                        // жирность, цвет и прочее берутся по нему, как и раньше.
                        rp ??= run.Properties;

                        string? runFont = run.Properties?.FontFamily
                            ?? ResolveStyleFontFamily(block.Properties.StyleName);

                        if (firstFont is null && !fontsDiffer)
                            firstFont = runFont;
                        else if (!string.Equals(firstFont, runFont, StringComparison.Ordinal))
                            fontsDiffer = true;
                    }

                    if (offset >= pvm.SelectionEnd) break;
                }
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
                ctx.FontFamily = fontsDiffer
                    ? null
                    : rp.FontFamily ?? ResolveStyleFontFamily(block.Properties.StyleName);
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

            int modelIndex = section.Blocks.IndexOf(after.Model);

            // Принадлежность к оглавлению наследуется, пока абзац остаётся внутри блока.
            //
            // Enter посреди оглавления делит строку, и обе половины обязаны остаться его
            // частью. Иначе между ними вставал чужой абзац, блок разваливался надвое — с
            // двумя закладками на листе и двумя диапазонами пересборки, — хотя человек
            // ничего не делил и остался в том же оглавлении.
            //
            // Enter в конце последней строки, наоборот, выводит в рукопись: это
            // единственный способ выйти из оглавления вниз, и терять его нельзя.
            bool staysInToc = after.Model.Properties.TocOwnerId is System.Guid tocOwner
                              && FollowedBySameToc(section, modelIndex, tocOwner);

            if (!staysInToc)
            {
                newBlock.Properties.TocOwnerId = null;
                newBlock.Properties.TocEntryLevel = 0;
            }

            // Ссылка на главу не наследуется никогда: она принадлежит конкретной строке, и
            // копия увела бы по новой строке в ту же главу. Ручная пометка «взять в
            // оглавление» не наследуется по той же причине — её ставят конкретному абзацу.
            newBlock.Properties.TocTargetBlockId = null;
            newBlock.Properties.IncludeInToc = false;

            // Элемент списка: новый абзац продолжает тот же список (тот же ListId), нумерация
            // считается движком автоматически. Перезапуск нумерации не наследуем.
            if (after.Model.ListProperties is not null)
            {
                var lp = after.Model.ListProperties.Clone();
                lp.ContinueNumbering = true;
                newBlock.ListProperties = lp;
            }

            if (modelIndex < 0) section.Blocks.Add(newBlock);
            else section.Blocks.Insert(modelIndex + 1, newBlock);

            int vmIndex = Paragraphs.IndexOf(after);
            var newVm = CreateParagraphViewModel(newBlock);
            Paragraphs.Insert(vmIndex + 1, newVm);

            return newVm;
        }

        /// <summary>
        /// За блоком идёт строка того же оглавления.
        ///
        /// По этому и решается, остаётся ли новый абзац внутри блока оглавления: пока
        /// ниже есть своя строка, каретка стоит в середине списка, и разрывать его
        /// нечем. Как только своих строк ниже нет, Enter выводит в рукопись.
        ///
        /// Смотрится первый же абзац ниже, а не весь остаток документа: строки
        /// оглавления идут подряд, и этого достаточно — тем же правилом пересборка
        /// ищет диапазон блока (TocService.FindRange).
        /// </summary>
        private static bool FollowedBySameToc(
            SectionModel section, int blockIndex, System.Guid ownerId)
        {
            if (blockIndex < 0) return false;

            for (int i = blockIndex + 1; i < section.Blocks.Count; i++)
            {
                if (section.Blocks[i] is not ParagraphBlock next) continue;
                return next.Properties.TocOwnerId == ownerId;
            }

            return false;
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

        /// <summary>
        /// Снимает подряд идущие абзацы одним заходом.
        ///
        /// Тот же результат, что от <see cref="DeleteParagraph"/>, вызванного по разу на
        /// каждый абзац, но без трёх вещей, которые поштучное снятие делает зря и на
        /// большом выделении делает мучительно долго.
        ///
        /// Первое: поиск абзаца перебором — и в списке вью-моделей, и в списке блоков.
        /// На каждый снимаемый абзац два прохода по всей книге; на выделении в триста
        /// строк это миллионы сравнений, на выделении во весь документ — квадрат.
        /// Здесь место известно заранее, а блоки отсеиваются одним проходом.
        ///
        /// Второе: запрос фокуса. Поштучное снятие просит фокус на соседа после КАЖДОГО
        /// абзаца, а запрос фокуса — это и поиск слайса перебором, и прокрутка к каретке.
        /// Триста снятых строк означали триста прокруток, и человека по дороге таскало
        /// по книге. Каретку ставит вызывающий, один раз и туда, куда следует.
        ///
        /// Третье: поабзацная пересборка раскладки. Снятие идёт под признаком массовой
        /// перестройки — следом всё равно пересобирают всё.
        /// </summary>
        /// <returns>Сколько абзацев снято.</returns>
        public int DeleteParagraphRange(int firstVmIndex, int count)
        {
            if (count <= 0) return 0;
            if (firstVmIndex < 0 || firstVmIndex >= Paragraphs.Count) return 0;
            if (_document.Sections.Count == 0) return 0;

            if (firstVmIndex + count > Paragraphs.Count)
                count = Paragraphs.Count - firstVmIndex;

            // Хотя бы один абзац в рукописи остаётся: документ без абзацев невозможен.
            if (Paragraphs.Count - count < 1) count = Paragraphs.Count - 1;
            if (count <= 0) return 0;

            var doomed = new System.Collections.Generic.HashSet<ParagraphBlock>();
            for (int i = firstVmIndex; i < firstVmIndex + count; i++)
                doomed.Add(Paragraphs[i].Model);

            _document.Sections[0].Blocks.RemoveAll(
                b => b is ParagraphBlock para && doomed.Contains(para));

            BeginBulkRebuild();
            try
            {
                for (int i = 0; i < count; i++)
                    Paragraphs.RemoveAt(firstVmIndex);
            }
            finally
            {
                EndBulkRebuild();
            }

            return count;
        }

        // ── Снятие куска текста через несколько абзацев ────────────────────

        /// <summary>
        /// Всё, что нужно, чтобы вернуть снятый кусок текста на место.
        ///
        /// Хранит ровно затронутое: прежнее содержимое первого абзаца и сами снятые
        /// абзацы живыми объектами. Ни копии рукописи, ни её текста в JSON — шаг отмены
        /// весит столько, сколько весит удалённое, и ни байтом больше.
        /// </summary>
        public sealed class RemovedTextSpan
        {
            /// <summary>Абзац, в котором выделение началось. Он остаётся в рукописи.</summary>
            public System.Guid FirstParaId { get; set; }

            /// <summary>Его содержимое до правки — посимвольно, со всем форматированием.</summary>
            public System.Collections.Generic.List<ParagraphBlock.CharCell> FirstCellsBefore { get; set; }
                = new();

            /// <summary>Снятые абзацы по порядку. Последний из них — тот, где выделение кончилось.</summary>
            public System.Collections.Generic.List<ParagraphBlock> Blocks { get; set; } = new();

            /// <summary>Откуда в первом абзаце начиналось выделение.</summary>
            public int From { get; set; }

            /// <summary>Где в последнем абзаце оно кончалось.</summary>
            public int To { get; set; }
        }

        /// <summary>
        /// Снимает кусок текста, идущий через несколько абзацев: хвост первого, абзацы
        /// между ними целиком и голову последнего. Голова последнего абзаца при этом
        /// прирастает к первому — ровно так же, как это делает обычное удаление.
        /// </summary>
        /// <returns>
        /// Описание снятого — его держит шаг отмены. null означает «этот случай мне не по
        /// зубам»: между абзацами выделения стоит таблица, картинка или разрыв. Вызывающий
        /// по null уходит на общий путь со снимком документа.
        /// </returns>
        public RemovedTextSpan? RemoveTextSpan(ParagraphBlock first, int from, ParagraphBlock last, int to)
        {
            if (IsReadOnly || first is null || last is null) return null;
            if (ReferenceEquals(first, last)) return null;
            if (_document.Sections.Count == 0) return null;

            var blocks = _document.Sections[0].Blocks;

            int firstIdx = blocks.IndexOf(first);
            int lastIdx = blocks.IndexOf(last);
            if (firstIdx < 0 || lastIdx <= firstIdx) return null;

            int firstVmIndex = CountParagraphsBefore(firstIdx);
            int lastVmIndex = CountParagraphsBefore(lastIdx);
            int count = lastVmIndex - firstVmIndex;
            if (count <= 0) return null;

            // Между первым и последним абзацем не должно быть блоков другого рода.
            // Возврат вставляет снятые абзацы подряд, и таблица, стоявшая между ними,
            // после отмены оказалась бы не там, где была. Такой случай честнее отдать
            // общему пути, чем вернуть криво.
            if (lastIdx - firstIdx != count) return null;

            var span = new RemovedTextSpan
            {
                FirstParaId = first.Id,
                From = from,
                To = to,
                FirstCellsBefore = first.ToCharCells()
            };

            for (int i = firstVmIndex + 1; i <= lastVmIndex && i < Paragraphs.Count; i++)
                span.Blocks.Add(Paragraphs[i].Model);

            if (span.Blocks.Count == 0) return null;

            ApplyTextSpanMerge(first, from, last, to);

            DeleteParagraphRange(firstVmIndex + 1, count);

            RefreshParagraphAt(firstVmIndex);
            RaiseStructureChanged();

            return span;
        }

        /// <summary>Повторяет снятие того же куска — после отмены. Зовётся шагом отмены.</summary>
        public RemovedTextSpan? RemoveTextSpan(RemovedTextSpan span)
        {
            if (span is null || span.Blocks.Count == 0) return null;

            var first = FindParagraphBlock(span.FirstParaId);
            var last = span.Blocks[span.Blocks.Count - 1];
            if (first is null) return null;

            return RemoveTextSpan(first, span.From, last, span.To);
        }

        /// <summary>
        /// Возвращает снятый кусок на место. Зовётся шагом отмены и только им.
        ///
        /// Абзацы вставляются те же самые объекты, что были вынуты, а первому абзацу
        /// возвращается его прежнее содержимое. Остальная рукопись не трогается вовсе:
        /// её вью-модели живы, её раскладки лежат в кэше и переживают откат.
        /// </summary>
        public void RestoreTextSpan(RemovedTextSpan span)
        {
            if (span is null || span.Blocks.Count == 0) return;
            if (_document.Sections.Count == 0) return;

            var first = FindParagraphBlock(span.FirstParaId);
            if (first is null) return;

            var blocks = _document.Sections[0].Blocks;
            int firstIdx = blocks.IndexOf(first);
            if (firstIdx < 0) return;

            InsertParagraphBlocks(firstIdx + 1, span.Blocks);

            first.RebuildFromCharCells(span.FirstCellsBefore);
            RefreshParagraphAt(CountParagraphsBefore(firstIdx));

            RaiseStructureChanged();
        }

        /// <summary>
        /// Пара соседних абзацев, которую можно привести к двум состояниям: слитому в
        /// один абзац и разделённому надвое.
        ///
        /// Одним описанием пользуются и деление (Enter), и слияние (Backspace в начале
        /// абзаца, Delete в конце): состояния у них одни и те же, отличается только то,
        /// какое из них считается отменой. Хранится содержимое слитого абзаца и сам
        /// отделённый абзац живым объектом — копии рукописи здесь нет, и шаг весит
        /// столько, сколько весит один абзац.
        /// </summary>
        public sealed class SplitParagraphSpan
        {
            /// <summary>Первый абзац пары. Он остаётся в рукописи в обоих состояниях.</summary>
            public System.Guid FirstParaId { get; set; }

            /// <summary>
            /// Содержимое абзаца в СЛИТОМ состоянии — посимвольно, со всем
            /// форматированием и картинками в строке.
            /// </summary>
            public System.Collections.Generic.List<ParagraphBlock.CharCell> WholeCells { get; set; }
                = new();

            /// <summary>
            /// Второй абзац пары. Пока пара слита, он вынут из рукописи и живёт в этом
            /// шаге отмены.
            /// </summary>
            public ParagraphBlock? TailBlock { get; set; }

            /// <summary>Место раздела: сколько знаков слитого абзаца принадлежит первому.</summary>
            public int At { get; set; }
        }

        /// <summary>
        /// Приводит пару к слитому состоянию: снимает второй абзац, а первому отдаёт
        /// содержимое обоих.
        ///
        /// Остальная рукопись не трогается вовсе — её вью-модели живы, её раскладки
        /// лежат в кэше и переход переживают.
        /// </summary>
        public bool ApplyParagraphUnion(SplitParagraphSpan span)
        {
            if (IsReadOnly) return false;
            if (span?.TailBlock is null) return false;
            if (_document.Sections.Count == 0) return false;

            var first = FindParagraphBlock(span.FirstParaId);
            if (first is null) return false;

            var blocks = _document.Sections[0].Blocks;

            int tailIdx = blocks.IndexOf(span.TailBlock);
            if (tailIdx >= 0) RemoveParagraphBlocks(tailIdx, 1);

            first.RebuildFromCharCells(span.WholeCells);

            int firstIdx = blocks.IndexOf(first);
            if (firstIdx >= 0) RefreshParagraphAt(CountParagraphsBefore(firstIdx));

            RaiseStructureChanged();

            return true;
        }

        /// <summary>
        /// Приводит пару к разделённому состоянию: обрезает первому абзацу хвост и
        /// ставит следом второй.
        ///
        /// Хвост заново не собирается: второй абзац всё это время лежал в шаге отмены
        /// целым, со своими ранами и свойствами.
        /// </summary>
        public bool ApplyParagraphDivision(SplitParagraphSpan span)
        {
            if (IsReadOnly) return false;
            if (span?.TailBlock is null) return false;
            if (_document.Sections.Count == 0) return false;

            var first = FindParagraphBlock(span.FirstParaId);
            if (first is null) return false;

            var blocks = _document.Sections[0].Blocks;
            int firstIdx = blocks.IndexOf(first);
            if (firstIdx < 0) return false;

            var cells = first.ToCharCells();
            int cut = span.At < 0 ? 0 : (span.At > cells.Count ? cells.Count : span.At);
            if (cells.Count > cut) cells.RemoveRange(cut, cells.Count - cut);
            first.RebuildFromCharCells(cells);

            InsertParagraphBlocks(
                firstIdx + 1,
                new System.Collections.Generic.List<ParagraphBlock> { span.TailBlock });

            RefreshParagraphAt(CountParagraphsBefore(firstIdx));
            RaiseStructureChanged();

            return true;
        }

        /// <summary>
        /// Вставка, добавившая в поток несколько абзацев подряд.
        ///
        /// Так выглядит вставка многострочного текста из буфера: первому абзацу
        /// достаётся часть вставленного, остальное ложится новыми абзацами следом.
        /// Шаг хранит содержимое первого абзаца до и после правки и сами добавленные
        /// абзацы живыми объектами — ровно столько, сколько весит вставленное.
        /// </summary>
        public sealed class InsertedParagraphsSpan
        {
            /// <summary>Абзац, в который вставляли. Он остаётся в рукописи.</summary>
            public System.Guid FirstParaId { get; set; }

            /// <summary>Его содержимое до вставки — посимвольно.</summary>
            public System.Collections.Generic.List<ParagraphBlock.CharCell> FirstCellsBefore { get; set; }
                = new();

            /// <summary>Его содержимое после вставки.</summary>
            public System.Collections.Generic.List<ParagraphBlock.CharCell> FirstCellsAfter { get; set; }
                = new();

            /// <summary>Добавленные абзацы по порядку. Пока шаг откачен, их держит он.</summary>
            public System.Collections.Generic.List<ParagraphBlock> AddedBlocks { get; set; } = new();
        }

        /// <summary>Отменяет вставку: снимает добавленные абзацы и возвращает первому прежнее.</summary>
        public bool RevertInsertedParagraphs(InsertedParagraphsSpan span)
        {
            if (span is null) return false;
            if (_document.Sections.Count == 0) return false;

            var first = FindParagraphBlock(span.FirstParaId);
            if (first is null) return false;

            var blocks = _document.Sections[0].Blocks;

            // Каждый абзац ищется по своему месту: правки между вставкой и откатом
            // могли сдвинуть их в потоке, и снимать по запомненным номерам значило бы
            // снять чужое.
            foreach (var added in span.AddedBlocks)
            {
                int at = blocks.IndexOf(added);
                if (at >= 0) RemoveParagraphBlocks(at, 1);
            }

            first.RebuildFromCharCells(span.FirstCellsBefore);
            RefreshParagraphByBlock(first);

            RaiseStructureChanged();
            return true;
        }

        /// <summary>Повторяет вставку: те же абзацы встают обратно за первым.</summary>
        public bool ApplyInsertedParagraphs(InsertedParagraphsSpan span)
        {
            if (IsReadOnly || span is null) return false;
            if (_document.Sections.Count == 0) return false;

            var first = FindParagraphBlock(span.FirstParaId);
            if (first is null) return false;

            var blocks = _document.Sections[0].Blocks;
            int firstIdx = blocks.IndexOf(first);
            if (firstIdx < 0) return false;

            first.RebuildFromCharCells(span.FirstCellsAfter);
            RefreshParagraphByBlock(first);

            if (span.AddedBlocks.Count > 0)
                InsertParagraphBlocks(firstIdx + 1, span.AddedBlocks);

            RaiseStructureChanged();
            return true;
        }

        /// <summary>
        /// Сливает два соседних абзаца в один и отдаёт описание пары для шага отмены.
        ///
        /// Абзацы обязаны стоять в потоке подряд. В списке абзацев соседями выглядят и
        /// те, между которыми лежит таблица или картинка: блоки другого рода в него не
        /// попадают. Возврат вставил бы снятый абзац не на своё место, поэтому такой
        /// случай честнее отдать общему пути, чем вернуть криво.
        /// </summary>
        /// <returns>null — абзацы не подряд или не найдены; вызывающий уходит на снимок.</returns>
        public SplitParagraphSpan? MergeParagraphs(ParagraphBlock first, ParagraphBlock tail)
        {
            if (IsReadOnly || first is null || tail is null) return null;
            if (ReferenceEquals(first, tail)) return null;
            if (_document.Sections.Count == 0) return null;

            var blocks = _document.Sections[0].Blocks;

            int firstIdx = blocks.IndexOf(first);
            if (firstIdx < 0 || firstIdx + 1 >= blocks.Count) return null;
            if (!ReferenceEquals(blocks[firstIdx + 1], tail)) return null;

            // Содержимое переносится посимвольно, а не плоским текстом: так переезжают
            // и форматирование каждого знака, и картинки в строке.
            var whole = first.ToCharCells();
            int at = whole.Count;
            whole.AddRange(tail.ToCharCells());

            var span = new SplitParagraphSpan
            {
                FirstParaId = first.Id,
                WholeCells = new System.Collections.Generic.List<ParagraphBlock.CharCell>(whole),
                TailBlock = tail,
                At = at
            };

            first.RebuildFromCharCells(whole);

            RemoveParagraphBlocks(firstIdx + 1, 1);

            RefreshParagraphAt(CountParagraphsBefore(firstIdx));
            RaiseStructureChanged();

            return span;
        }

        /// <summary>
        /// Склейка первого абзаца с хвостом последнего. Посимвольно, а не плоским
        /// текстом: тот потерял бы и форматирование каждого знака, и картинки в строке.
        /// </summary>
        private static void ApplyTextSpanMerge(
            ParagraphBlock first, int from, ParagraphBlock last, int to)
        {
            var merged = first.ToCharCells();
            int cut = from < 0 ? 0 : (from > merged.Count ? merged.Count : from);
            if (merged.Count > cut) merged.RemoveRange(cut, merged.Count - cut);

            var tail = last.ToCharCells();
            int drop = to < 0 ? 0 : (to > tail.Count ? tail.Count : to);
            if (drop > 0) tail.RemoveRange(0, drop);

            merged.AddRange(tail);
            first.RebuildFromCharCells(merged);
        }

        /// <summary>Абзац рукописи по его опознавателю. null — такого в потоке нет.</summary>
        private ParagraphBlock? FindParagraphBlock(System.Guid paraId)
        {
            if (_document.Sections.Count == 0) return null;

            foreach (var block in _document.Sections[0].Blocks)
                if (block is ParagraphBlock para && para.Id == paraId) return para;

            return null;
        }

        /// <summary>Перечитывает текст вью-модели по её месту в списке абзацев.</summary>
        private void RefreshParagraphAt(int vmIndex)
        {
            if (vmIndex < 0 || vmIndex >= Paragraphs.Count) return;
            Paragraphs[vmIndex].RefreshPlainTextFromModel();
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

        public void EndFontPreview(bool commit, string? fontFamily = null)
            => EndFontPreviewDelegate?.Invoke(commit, fontFamily);

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
        // ── Выделенная фигура ─────────────────────────────────────────────
        public (ShapeType Type, WrapMode Wrap, WrapSide WrapSide, ShapeDashStyle Dash, ShapeArrowHead StartArrow, ShapeArrowHead EndArrow, string? FillColor, string? StrokeColor, double StrokeThicknessPt, double CornerRadiusPt, double Opacity, double WidthPt, double HeightPt, double RotationDeg, bool LockAspect, int PinnedPage, bool HasFillImage, bool FillImageStretch)? GetSelectedShapeInfo()
            => GetSelectedShapeInfoDelegate?.Invoke();

        public void SetShapeType(ShapeType type) { if (IsReadOnly) return; SetShapeTypeDelegate?.Invoke(type); }
        public void SetShapeFill(string? hexColor) { if (IsReadOnly) return; SetShapeFillDelegate?.Invoke(hexColor); }
        public void SetShapeStroke(string? hexColor) { if (IsReadOnly) return; SetShapeStrokeDelegate?.Invoke(hexColor); }
        public void SetShapeStrokeThickness(double thicknessPt) { if (IsReadOnly) return; SetShapeStrokeThicknessDelegate?.Invoke(thicknessPt); }
        public void SetShapeDash(ShapeDashStyle dash) { if (IsReadOnly) return; SetShapeDashDelegate?.Invoke(dash); }
        public void SetShapeCornerRadius(double radiusPt) { if (IsReadOnly) return; SetShapeCornerRadiusDelegate?.Invoke(radiusPt); }
        public void SetShapeArrows(ShapeArrowHead start, ShapeArrowHead end) { if (IsReadOnly) return; SetShapeArrowsDelegate?.Invoke(start, end); }
        public void SetShapeOpacity(double opacity) { if (IsReadOnly) return; SetShapeOpacityDelegate?.Invoke(opacity); }
        public void SetShapeWidth(double widthPt) { if (IsReadOnly) return; SetShapeWidthDelegate?.Invoke(widthPt); }
        public void SetShapeHeight(double heightPt) { if (IsReadOnly) return; SetShapeHeightDelegate?.Invoke(heightPt); }
        public void SetShapeRotation(double degrees) { if (IsReadOnly) return; SetShapeRotationDelegate?.Invoke(degrees); }
        public void SetShapeLockAspect(bool locked) { if (IsReadOnly) return; SetShapeLockAspectDelegate?.Invoke(locked); }
        public void SetShapeWrapMode(WrapMode mode) { if (IsReadOnly) return; SetShapeWrapModeDelegate?.Invoke(mode); }
        public void SetShapeWrapSide(WrapSide side) { if (IsReadOnly) return; SetShapeWrapSideDelegate?.Invoke(side); }
        public void SetShapeWrapPadding(double topPt, double bottomPt, double leftPt, double rightPt)
        { if (IsReadOnly) return; SetShapeWrapPaddingDelegate?.Invoke(topPt, bottomPt, leftPt, rightPt); }
        public void SetShapePinned(bool pinned) { if (IsReadOnly) return; SetShapePinnedDelegate?.Invoke(pinned); }
        public void SetShapeZOrder(bool toFront) { if (IsReadOnly) return; SetShapeZOrderDelegate?.Invoke(toFront); }
        public void SetShapeFillImage(string? filePath) { if (IsReadOnly) return; SetShapeFillImageDelegate?.Invoke(filePath); }
        public void SetShapeFillImageStretch(bool stretch) { if (IsReadOnly) return; SetShapeFillImageStretchDelegate?.Invoke(stretch); }
        public void DeleteSelectedShape() { if (IsReadOnly) return; DeleteSelectedShapeDelegate?.Invoke(); }

        public void SetImageBorderDash(ShapeDashStyle dash)
        { if (IsReadOnly) return; SetImageBorderDashDelegate?.Invoke(dash); }
        public ShapeDashStyle? GetSelectedImageBorderDash()
            => GetSelectedImageBorderDashDelegate?.Invoke();
        public void SetImageShapeType(ShapeType type)
        { if (IsReadOnly) return; SetImageShapeTypeDelegate?.Invoke(type); }
        public ShapeType? GetSelectedImageShapeType()
            => GetSelectedImageShapeTypeDelegate?.Invoke();
        public void SetImageCornerRadius(double radiusPt)
        { if (IsReadOnly) return; SetImageCornerRadiusDelegate?.Invoke(radiusPt); }
        public double? GetSelectedImageCornerRadius()
            => GetSelectedImageCornerRadiusDelegate?.Invoke();

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
        public void SetImageBorderAlign(ImageBorderAlign align) { if (IsReadOnly) return; SetImageBorderAlignDelegate?.Invoke(align); }
        public ImageBorderAlign? GetSelectedImageBorderAlign() => GetSelectedImageBorderAlignDelegate?.Invoke();
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
        public (bool HasShape, bool HasImage, bool HasFillImage, bool IsLine)? GetSelectedFloatingKind()
            => GetSelectedFloatingKindDelegate?.Invoke();

        public void IncreaseIndent()
            => ApplyParaProperty(p => p.LeftIndent = (p.LeftIndent ?? 0) + 18);

        public void DecreaseIndent()
            => ApplyParaProperty(p => p.LeftIndent = Math.Max(0, (p.LeftIndent ?? 0) - 18));

        public void SetLineSpacing(double v)
            => ApplyParaProperty(p => { p.LineSpacingRule = LineSpacingRule.Auto; p.LineSpacingValue = v; });

        public void SetSpaceBefore(double pt) => ApplyParaProperty(p => p.SpaceBefore = pt);
        public void SetSpaceAfter(double pt) => ApplyParaProperty(p => p.SpaceAfter = pt);
        public void ApplyStyle(string name) => ApplyParaProperty(p => p.StyleName = name);

        public void SetLeftIndentPt(double pt) => ApplyParaProperty(p =>
        {
            p.LeftIndent = pt;
            RefreshTocEntryTabStop(p);
        });

        public void SetFirstLineIndentPt(double pt) => ApplyParaProperty(p => p.FirstLineIndent = pt);

        public void SetRightIndentPt(double pt) => ApplyParaProperty(p =>
        {
            p.RightIndent = pt;
            RefreshTocEntryTabStop(p);
        });

        /// <summary>
        /// Двигает отметку табуляции строки оглавления следом за её отступами.
        ///
        /// Номер страницы прижат к отметке, а отметка стоит у правого края текста абзаца.
        /// Пока она не ехала за отступами, оглавление нельзя было ни сузить, ни расширить
        /// стрелками линейки: текст сдвигался, а числа оставались на прежнем месте — и
        /// уходили за поле или повисали посреди строки.
        ///
        /// Обычного абзаца это не касается: у него нет TocOwnerId, и позиции табуляции
        /// остаются там, куда их поставил человек.
        /// </summary>
        private void RefreshTocEntryTabStop(ParagraphProperties props)
        {
            if (props.TocOwnerId is not System.Guid owner) return;
            if (props.TocEntryLevel <= 0) return;

            TocService.RefreshEntryTabStop(
                props, EnsureTocSettings(owner), TocService.TextWidthPt(_document));
        }

        /// <summary>
        /// Записывает абзацу набор позиций табуляции целиком. Частичной правки здесь нет
        /// намеренно: набор всегда приходит готовым — с линейки или из окна настройки, —
        /// и сравнивать его с прежним не за чем.
        ///
        /// Каждому абзацу достаётся своя копия списка: выделение может охватывать несколько
        /// абзацев, а общий список означал бы, что правка одного из них молча меняет все
        /// остальные и их шаги отмены.
        /// </summary>
        public void SetTabStops(System.Collections.Generic.IReadOnlyList<TabStop>? stops)
            => ApplyParaProperty(p =>
            {
                if (stops is null || stops.Count == 0)
                {
                    p.TabStops = null;
                    return;
                }

                var copy = new System.Collections.Generic.List<TabStop>(stops.Count);
                foreach (var stop in stops) copy.Add(stop.Clone());
                p.TabStops = copy;
            });

        /// <summary>
        /// Позиции табуляции абзаца под кареткой — копия для окна настройки.
        /// Пусто, когда своих позиций у абзаца нет.
        /// </summary>
        public System.Collections.Generic.List<TabStop> GetActiveTabStops()
        {
            var props = TableActiveCellParagraph?.Properties ?? _activeParagraph?.Model.Properties;
            var result = new System.Collections.Generic.List<TabStop>();
            if (props?.TabStops is null) return result;

            foreach (var stop in props.TabStops) result.Add(stop.Clone());
            result.Sort(static (a, b) => a.PositionPt.CompareTo(b.PositionPt));
            return result;
        }

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

            // Файлы картинок хранятся внутри проекта, доступ — через контекст активной вкладки.
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
        /// НОВЫМ файлом в ТЕКУЩИЙ проект — поэтому работает и при копировании
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

        /// <summary>Откуда картинка была снята — туда же её и возвращать.</summary>
        public enum ImagePlace
        {
            /// <summary>Блок в потоке документа.</summary>
            Block = 0,
            /// <summary>Плавающий объект, лежащий поверх листа.</summary>
            Floating = 1,
            /// <summary>Картинка в строке: живёт символом внутри абзаца.</summary>
            Inline = 2
        }

        /// <summary>
        /// Всё, что нужно, чтобы вернуть снятую картинку на место.
        ///
        /// Сама картинка хранится живым объектом: из документа она вынута, и держит её
        /// один шаг отмены. Для картинки в строке хранится ещё и прежнее содержимое
        /// абзаца-хозяина: её символ стоял среди букв, и вернуть его иначе как вместе с
        /// ними нельзя — форматирование соседей разъехалось бы.
        /// </summary>
        public sealed class RemovedImage
        {
            public ImageBlock? Image { get; set; }

            public SectionModel? Section { get; set; }

            public ImagePlace Place { get; set; }

            /// <summary>Место в списке, из которого картинка вынута.</summary>
            public int Index { get; set; }

            /// <summary>Абзац-хозяин — только для картинки в строке.</summary>
            public System.Guid OwnerParaId { get; set; }

            /// <summary>Его содержимое до снятия — посимвольно.</summary>
            public System.Collections.Generic.List<ParagraphBlock.CharCell> OwnerCellsBefore { get; set; }
                = new();
        }

        /// <summary>
        /// Снимает картинку с листа и отдаёт описание для шага отмены.
        ///
        /// Отличие от <see cref="RemoveImage"/> в одном: тот открывает снимок всей
        /// рукописи, а здесь шаг весит саму картинку и, для картинки в строке, один
        /// абзац.
        /// </summary>
        /// <returns>null — такой картинки в рукописи нет.</returns>
        public RemovedImage? TakeImageOut(ImageBlock image)
        {
            if (IsReadOnly || image is null) return null;

            var inlineSection = FindSectionOfInlineImage(image);
            if (inlineSection is not null)
            {
                if (FindInlineImageOwner(image) is not { } found) return null;

                var takenInline = new RemovedImage
                {
                    Image = image,
                    Section = inlineSection,
                    Place = ImagePlace.Inline,
                    Index = inlineSection.InlineObjects.IndexOf(image),
                    OwnerParaId = found.Para.Id,
                    OwnerCellsBefore = found.Para.ToCharCells()
                };

                found.Para.SpliceText(found.CharIndex, found.CharIndex + 1, string.Empty);
                inlineSection.InlineObjects.Remove(image);

                RefreshParagraphByBlock(found.Para);
                InlineObjectsChanged?.Invoke(found.Para);
                StructureChanged?.Invoke();

                return takenInline;
            }

            foreach (var section in _document.Sections)
            {
                int blockIdx = section.Blocks.IndexOf(image);
                if (blockIdx >= 0)
                {
                    section.Blocks.RemoveAt(blockIdx);
                    StructureChanged?.Invoke();

                    return new RemovedImage
                    {
                        Image = image,
                        Section = section,
                        Place = ImagePlace.Block,
                        Index = blockIdx
                    };
                }

                int floatIdx = section.FloatingObjects.IndexOf(image);
                if (floatIdx >= 0)
                {
                    section.FloatingObjects.RemoveAt(floatIdx);
                    StructureChanged?.Invoke();

                    return new RemovedImage
                    {
                        Image = image,
                        Section = section,
                        Place = ImagePlace.Floating,
                        Index = floatIdx
                    };
                }
            }

            return null;
        }

        /// <summary>Возвращает снятую картинку туда, откуда её взяли.</summary>
        public bool PutImageBack(RemovedImage removed)
        {
            if (removed?.Image is null || removed.Section is null) return false;

            if (removed.Place == ImagePlace.Inline)
            {
                var owner = FindParagraphBlock(removed.OwnerParaId);
                if (owner is null) return false;

                removed.Section.InlineObjects.Insert(
                    ClampIndex(removed.Index, removed.Section.InlineObjects.Count),
                    removed.Image);

                // Абзац возвращается целиком: символ картинки стоял среди букв, и
                // вставить его отдельно значило бы угадывать, каким раном он был.
                owner.RebuildFromCharCells(removed.OwnerCellsBefore);

                RefreshParagraphByBlock(owner);
                InlineObjectsChanged?.Invoke(owner);
                StructureChanged?.Invoke();

                return true;
            }

            if (removed.Place == ImagePlace.Floating)
            {
                removed.Section.FloatingObjects.Insert(
                    ClampIndex(removed.Index, removed.Section.FloatingObjects.Count),
                    removed.Image);
            }
            else
            {
                removed.Section.Blocks.Insert(
                    ClampIndex(removed.Index, removed.Section.Blocks.Count),
                    removed.Image);
            }

            StructureChanged?.Invoke();
            return true;
        }

        private static int ClampIndex(int index, int count)
            => index < 0 ? 0 : (index > count ? count : index);

        /// <summary>
        /// Абзац лежит в потоке документа, а не в ячейке таблицы или надписи.
        ///
        /// Спрашивают об этом шаги отмены: они ищут абзацы по потоку, и абзац ячейки для
        /// них всё равно что чужой — вернуть в него содержимое они не смогут.
        /// </summary>
        public bool IsFlowParagraph(ParagraphBlock para)
        {
            if (para is null || _document.Sections.Count == 0) return false;

            foreach (var block in _document.Sections[0].Blocks)
                if (ReferenceEquals(block, para)) return true;

            return false;
        }

        /// <summary>Перечитывает текст вью-модели по её блоку.</summary>
        private void RefreshParagraphByBlock(ParagraphBlock para)
        {
            if (para is null) return;

            foreach (var vm in Paragraphs)
            {
                if (!ReferenceEquals(vm.Model, para)) continue;
                vm.RefreshPlainTextFromModel();
                return;
            }
        }

        /// <summary>
        /// Возвращает абзацам их содержимое — посимвольно, со всем форматированием.
        ///
        /// Нужен шагам отмены, которые правят текст сразу нескольких абзацев и никакой
        /// структуры не меняют: перенос картинки из строки в строку, например. Состав
        /// блоков при этом прежний, и трогать его незачем.
        /// </summary>
        public bool ApplyParagraphCells(
            System.Guid paraId,
            System.Collections.Generic.IReadOnlyList<ParagraphBlock.CharCell> cells)
        {
            var para = FindParagraphBlock(paraId);
            if (para is null) return false;

            para.RebuildFromCharCells(cells);
            RefreshParagraphByBlock(para);

            return true;
        }

        /// <summary>
        /// Ставит готовый абзац в поток документа вместе с его вью-моделью.
        /// Нужен шагам отмены, которые абзац добавляют: вставке перед таблицей и повтору.
        /// </summary>
        public bool InsertFlowParagraph(ParagraphBlock para, int blockIndex)
        {
            if (IsReadOnly || para is null) return false;
            if (_document.Sections.Count == 0) return false;

            var blocks = _document.Sections[0].Blocks;
            int at = ClampIndex(blockIndex, blocks.Count);

            InsertParagraphBlocks(
                at, new System.Collections.Generic.List<ParagraphBlock> { para });

            RaiseStructureChanged();
            return true;
        }

        /// <summary>Снимает абзац из потока вместе с его вью-моделью.</summary>
        public bool RemoveFlowParagraph(ParagraphBlock para)
        {
            if (IsReadOnly || para is null) return false;
            if (_document.Sections.Count == 0) return false;

            var blocks = _document.Sections[0].Blocks;
            int at = blocks.IndexOf(para);
            if (at < 0) return false;

            RemoveParagraphBlocks(at, 1);
            RaiseStructureChanged();

            return true;
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

        // Оформление и положение только что вставленной фигуры.
        private const string ShapeDefaultFill = "#DCE6F1";
        private const string ShapeDefaultStroke = "#2F5597";
        private const double ShapeInsertOffsetPt = 36.0;
        private const double ShapeCascadeStepPt = 14.0;

        /// <summary>
        /// Вставляет фигуру на страницу каретки. Фигура плавающая: место блока в
        /// потоке задаёт только её страницу, а положение на листе — собственные
        /// смещения от начала текстовой области.
        /// </summary>
        public void InsertShape(ShapeType st)
        {
            if (IsReadOnly) return;
            if (_document.Sections.Count == 0) return;

            var section = _document.Sections[0];
            bool isStrokeOnly = st is ShapeType.Line or ShapeType.Arrow;

            var shape = new ShapeBlock
            {
                ShapeType = st,
                WidthPt = isStrokeOnly ? 180.0 : 160.0,
                HeightPt = isStrokeOnly ? 24.0 : 100.0,
                FillColor = isStrokeOnly ? null : ShapeDefaultFill,
                StrokeColor = ShapeDefaultStroke,
                StrokeThicknessPt = isStrokeOnly ? 1.5 : 1.0
            };

            // Каждая следующая фигура ставится со сдвигом: вставленные подряд не
            // ложатся ровно друг на друга, и видно каждую.
            int existing = 0;
            foreach (var block in section.Blocks)
                if (block is ShapeBlock) existing++;

            double cascade = ShapeCascadeStepPt * (existing % 8);
            shape.OffsetXPt = ShapeInsertOffsetPt + cascade;
            shape.OffsetYPt = ShapeInsertOffsetPt + cascade;

            BeginEditDelegate?.Invoke("Вставка фигуры");
            int idx = _activeParagraph is not null
                ? section.Blocks.IndexOf(_activeParagraph.Model)
                : -1;
            if (idx >= 0) section.Blocks.Insert(idx + 1, shape);
            else section.Blocks.Add(shape);
            CommitEditDelegate?.Invoke();

            StructureChanged?.Invoke();
            ShapeInserted?.Invoke(shape);
        }

        /// <summary>
        /// Кладёт в документ готовую фигуру — вставку из буфера или дубликат.
        /// Оформление и размеры уже заданы вызывающим, здесь только место в потоке,
        /// снимок для отмены и уведомление канваса.
        /// </summary>
        public void InsertShapeBlock(ShapeBlock shape)
        {
            if (IsReadOnly) return;
            if (shape is null) return;
            if (_document.Sections.Count == 0) return;

            var section = _document.Sections[0];

            BeginEditDelegate?.Invoke("Вставка фигуры");
            int idx = _activeParagraph is not null
                ? section.Blocks.IndexOf(_activeParagraph.Model)
                : -1;
            if (idx >= 0) section.Blocks.Insert(idx + 1, shape);
            else section.Blocks.Add(shape);
            CommitEditDelegate?.Invoke();

            StructureChanged?.Invoke();
            ShapeInserted?.Invoke(shape);
        }

        /// <summary>
        /// Кладёт файл картинки в хранилище проекта и возвращает его имя внутри проекта.
        /// Тем же путём и в ту же папку, что и обычная вставка изображения: заливка
        /// фигуры картинкой хранится так же, как сама картинка документа.
        /// null — файла нет или хранилище недоступно.
        /// </summary>
        public string? StoreImageFile(string filePath)
        {
            if (IsReadOnly) return null;
            if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath)) return null;

            byte[] data;
            try { data = System.IO.File.ReadAllBytes(filePath); }
            catch { return null; }
            if (data.Length == 0) return null;

            var ctx = CoreServices.GetService<ITabCollection>()?.ActiveTab?.Context;
            if (ctx is null) return null;

            string ext = System.IO.Path.GetExtension(filePath);
            if (string.IsNullOrWhiteSpace(ext)) ext = ".png";
            if (!ext.StartsWith(".")) ext = "." + ext;

            string fileName = $"img_{System.Guid.NewGuid():N}{ext}";
            ctx.WriteFile($"TextEditor/Images/{fileName}", data);

            // Байты уходят на диск сразу: кеш восстановления хранит только JSON
            // документа и после аварии сошлётся на этот файл — тот обязан существовать.
            ctx.FlushStorage();
            return fileName;
        }

        /// <summary>
        /// Фигура вставлена в документ. Канвас по этому событию делает её выделенной:
        /// сразу видно, что появилось на странице, и её можно двигать без поиска.
        /// </summary>
        public event Action<ShapeBlock>? ShapeInserted;

        /// <summary>
        /// Переставляет фигуру, стоящую в потоке, сразу за указанным абзацем.
        /// Для такой фигуры место блока в потоке и есть её положение на листе,
        /// поэтому «перетащить» её значит переставить блок.
        ///
        /// Снимок для отмены берёт вызывающий: меняется состав и порядок блоков.
        /// </summary>
        public void MoveShapeAfterParagraph(ShapeBlock shape, ParagraphBlock target)
        {
            if (IsReadOnly) return;
            if (shape is null || target is null) return;

            foreach (var section in _document.Sections)
            {
                int from = section.Blocks.IndexOf(shape);
                if (from < 0) continue;

                int targetIdx = section.Blocks.IndexOf(target);
                if (targetIdx < 0) return;

                // Индекс цели считается ПОСЛЕ изъятия фигуры: убрав блок, стоявший
                // выше, мы сдвигаем всё, что ниже, на единицу.
                section.Blocks.RemoveAt(from);
                targetIdx = section.Blocks.IndexOf(target);
                if (targetIdx < 0) { section.Blocks.Insert(from, shape); return; }

                int to = targetIdx + 1;
                if (to == from) { section.Blocks.Insert(from, shape); return; }

                section.Blocks.Insert(to, shape);
                StructureChanged?.Invoke();
                return;
            }
        }

        /// <summary>
        /// Убирает фигуру из документа. Снимок для отмены берёт вызывающий: удаление
        /// меняет состав блоков, и одной командой свойств его не откатить.
        /// </summary>
        public void RemoveShape(ShapeBlock shape)
        {
            if (IsReadOnly) return;
            if (shape is null) return;

            foreach (var section in _document.Sections)
            {
                if (!section.Blocks.Contains(shape)
                    && !section.FloatingObjects.Contains(shape)) continue;

                section.Blocks.Remove(shape);
                section.FloatingObjects.Remove(shape);
                StructureChanged?.Invoke();
                return;
            }
        }
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
        /// <summary>
        /// Название по умолчанию над списком. Ставит модуль — строка приходит из ресурсов,
        /// а документ о языке интерфейса не знает.
        /// </summary>
        public Func<string>? TocDefaultTitleProvider { get; set; }

        /// <summary>
        /// Вставляет оглавление на место каретки и сразу наполняет его.
        ///
        /// Оглавление кладётся перед абзацем, в котором стоит каретка, а не после: его
        /// ставят в начало книги, и «перед» — это то место, куда человек метил, поставив
        /// курсор в первую строку.
        /// </summary>
        public void InsertTOC()
        {
            if (IsReadOnly) return;
            if (_document.Sections.Count == 0) return;

            // Рукопись могла быть начата до появления стилей оглавления — дописываем их,
            // иначе строки сослались бы на несуществующий стиль и вышли обычным текстом.
            //
            // Уведомление шлётся только когда стили действительно дописаны: оно чистит
            // кэш раскладки целиком и перевёрстывает всю книгу.
            if (TocService.EnsureBuiltInStyles(_document))
            {
                // Дописанные стили надо донести до раскладки: указатель имён у неё строится
                // один раз, и о стиле, добавленном после, она сама не узнает.
                RaiseStylesChanged();
            }

            var settings = new Models.Toc.TocSettings();
            var blocks = _document.Sections[0].Blocks;

            // Куда вставлять: перед активным абзацем, а нет его — в самое начало.
            int at = 0;
            if (_activeParagraph is not null)
            {
                int idx = blocks.IndexOf(_activeParagraph.Model);
                if (idx >= 0) at = idx;
            }

            var headings = TocService.Collect(_document, settings, GetBlockPageNumbers());

            var paragraphs = TocService.BuildParagraphs(
                settings, headings,
                TocService.TextWidthPt(_document),
                TocDefaultTitleProvider?.Invoke() ?? "Оглавление");

            if (paragraphs.Count == 0) return;

            BeginUndoStep("Оглавление");

            _document.TableOfContents ??= new System.Collections.Generic.List<Models.Toc.TocSettings>();
            _document.TableOfContents.Add(settings);

            InsertParagraphBlocks(at, paragraphs);

            CommitUndoStep();
            RaiseStructureChanged();

            // Каретка уходит в оглавление. Это не вежливость: контекстная вкладка ленты
            // живёт от того, где стоит каретка, и без перехода человек получал бы
            // вставленный список и ни одного инструмента к нему, пока сам не догадается
            // щёлкнуть внутрь.
            MoveCaretToParagraphBlock(paragraphs[0]);

            // Номера страниц посчитаны по раскладке, которой оглавление ещё не сдвинуло.
            // Правда о страницах будет известна только после пересчёта — за ним следует
            // второй проход.
            RequestTocPageNumbers(force: true);
        }

        /// <summary>
        /// Проставляет строкам всех оглавлений свежие номера страниц, не пересобирая
        /// списки.
        ///
        /// Отличие от <see cref="RebuildToc"/> в том, что уцелеет. Пересборка сносит
        /// строки и создаёт их заново из заголовков рукописи: правки, сделанные в самих
        /// строках, уходят вместе со строками. Проход по номерам меняет в строке только
        /// то, что стоит после последней табуляции, — само число, — и не трогает ни
        /// названия, ни их форматирование.
        ///
        /// Идёт по всем оглавлениям рукописи, включая те, которым самообновление
        /// выключено: выключенное самообновление означает «номера обновляются только по
        /// кнопке», и это она и есть.
        ///
        /// Работу делает не этот метод, а полотно: номера страниц знает только раскладка,
        /// и спрашивать их можно лишь после того, как она пересобрана.
        /// </summary>
        public void RefreshTocPageNumbers()
        {
            if (IsReadOnly) return;
            if (_document.TableOfContents is not { Count: > 0 }) return;

            RequestTocPageNumbers(force: true);
        }

        /// <summary>
        /// Как выглядят строки оглавления одного уровня: кегль, жирность, курсив.
        ///
        /// Значения резолвятся по цепочке BasedOn: стили Toc2…Toc9 наследуют друг друга,
        /// и своего кегля у большинства из них нет. Показывать в ленте пустоту там, где
        /// на листе стоит унаследованное значение, значит врать человеку о том, что он
        /// правит.
        /// </summary>
        /// <returns>null — уровень вне 1…9 или стиля в рукописи нет.</returns>
        public (double FontSizePt, bool IsBold, bool IsItalic)? GetTocLevelStyle(int level)
        {
            if (level < 1 || level > 9) return null;

            string name = TocService.TocStyleName(level);

            double size = 0;
            bool bold = false;
            bool italic = false;

            bool sizeFound = false;
            bool boldFound = false;
            bool italicFound = false;
            bool anyStyle = false;

            var visited = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? current = name;

            while (current is not null && visited.Add(current))
            {
                var style = _document.FindStyle(current);
                if (style is null) break;

                anyStyle = true;

                var run = style.RunProperties;
                if (run is not null)
                {
                    if (!sizeFound && run.FontSize.HasValue)
                    {
                        size = run.FontSize.Value;
                        sizeFound = true;
                    }

                    if (!boldFound)
                    {
                        bold = run.IsBold;
                        boldFound = true;
                    }

                    if (!italicFound)
                    {
                        italic = run.IsItalic;
                        italicFound = true;
                    }
                }

                current = style.BasedOn;
            }

            if (!anyStyle) return null;

            if (!sizeFound) size = Rendering.StyleResolver.FallbackFontSizePt;

            return (size, bold, italic);
        }

        /// <summary>
        /// Правит стиль строк оглавления одного уровня. Переданы только те свойства,
        /// которые меняются: остальные остаются такими, какими их видит цепочка BasedOn.
        ///
        /// Правка идёт по стилю, а не по абзацам, и это принципиально: строк оглавления
        /// в книге сотни, они пересобираются, и форматирование, положенное на абзац,
        /// исчезло бы при первом же обновлении. Стиль переживает пересборку.
        /// </summary>
        public void SetTocLevelStyle(int level, double? fontSizePt, bool? isBold, bool? isItalic)
        {
            if (IsReadOnly) return;
            if (level < 1 || level > 9) return;
            if (fontSizePt is null && isBold is null && isItalic is null) return;

            TocService.EnsureBuiltInStyles(_document);

            string name = TocService.TocStyleName(level);
            var style = _document.FindStyle(name);
            if (style is null) return;

            BeginUndoStep("Стиль оглавления");

            style.RunProperties ??= new Models.Inline.RunProperties();

            if (fontSizePt is double size)
            {
                double clamped = size < 1 ? 1 : (size > 400 ? 400 : size);
                style.RunProperties.FontSize = clamped;
            }

            if (isBold is bool bold) style.RunProperties.IsBold = bold;
            if (isItalic is bool italic) style.RunProperties.IsItalic = italic;

            CommitUndoStep();

            RaiseStylesChanged();
            RaiseContentModified();
        }

        /// <summary>
        /// Возвращает стилям оглавления встроенный вид.
        ///
        /// Сбрасываются только они: человек мог править и обычные стили книги, а
        /// «сбросить» на кнопке во вкладке оглавления обещает ровно оглавление.
        /// </summary>
        public void ResetTocStyles()
        {
            if (IsReadOnly) return;

            var builtIn = new System.Collections.Generic.Dictionary<string, Models.Styles.DocumentStyle>(
                StringComparer.Ordinal);

            foreach (var style in Models.Styles.DocumentStyle.CreateBuiltInStyles())
                builtIn[style.Name] = style;

            _document.Styles ??= new System.Collections.Generic.List<Models.Styles.DocumentStyle>();

            BeginUndoStep("Сброс стилей оглавления");

            for (int level = 0; level <= 9; level++)
            {
                string name = level == 0 ? "TocTitle" : TocService.TocStyleName(level);
                if (!builtIn.TryGetValue(name, out var fresh)) continue;

                int at = _document.Styles.FindIndex(
                    st => string.Equals(st.Name, name, StringComparison.Ordinal));

                if (at >= 0) _document.Styles[at] = fresh;
                else _document.Styles.Add(fresh);
            }

            CommitUndoStep();

            RaiseStylesChanged();
            RaiseContentModified();
        }

        /// <summary>
        /// Оглавление, внутри которого стоит каретка. null — каретка не в оглавлении.
        /// По нему риббон решает, показывать ли свою вкладку и что в ней отражать.
        /// </summary>
        public Models.Toc.TocSettings? ActiveToc()
        {
            var ownerId = _activeParagraph?.Model.Properties.TocOwnerId;
            if (ownerId is null) return null;

            // Настроек может не оказаться у рукописи, открытой из файла, записанного
            // версией, которая их теряла. Строка при этом честно носит опознаватель —
            // значит оглавление есть, и отвечать «нет» было бы неправдой.
            return EnsureTocSettings(ownerId.Value);
        }

        /// <summary>
        /// Уводит рукопись к главе, из которой взята строка оглавления под кареткой.
        ///
        /// Это ответ на возражение против перехода по щелчку: строка оглавления — текст,
        /// её правят, и отбирать у щелчка постановку каретки нельзя. Переход остаётся
        /// отдельным действием — кнопкой в ленте.
        /// </summary>
        public void GoToTocTarget()
        {
            var targetId = _activeParagraph?.Model.Properties.TocTargetBlockId;
            if (targetId is null) return;

            var blocks = _document.Sections[0].Blocks;
            int paragraphIndex = 0;

            for (int i = 0; i < blocks.Count; i++)
            {
                if (blocks[i] is not ParagraphBlock para) continue;

                if (para.Id == targetId.Value)
                {
                    GoToParagraph(paragraphIndex);
                    return;
                }

                paragraphIndex++;
            }
        }

        /// <summary>
        /// Пересобирает одно оглавление — после того, как человек поправил его настройки.
        /// </summary>
        public void RebuildToc(Models.Toc.TocSettings settings)
        {
            if (IsReadOnly || settings is null) return;

            var (start, count) = TocService.FindRange(_document, settings.Id);
            if (start < 0) return;

            // Рукопись, начатая до появления стилей оглавления, их не содержит, а правка
            // настроек — первый момент, когда это выясняется на уже вставленном списке.
            //
            // Уведомление — только если стили правда дописаны. Оно чистит кэш раскладки
            // целиком, и на книге в три тысячи абзацев каждое нажатие в ленте
            // перевёрстывало всю рукопись: около секунды на смену вида полосок, хотя
            // меняются несколько строк оглавления.
            if (TocService.EnsureBuiltInStyles(_document)) RaiseStylesChanged();

            var headings = TocService.Collect(_document, settings, GetBlockPageNumbers());

            var paragraphs = TocService.BuildParagraphs(
                settings, headings,
                TocService.TextWidthPt(_document),
                TocDefaultTitleProvider?.Invoke() ?? "Оглавление");

            BeginUndoStep("Настройки оглавления");

            // Каретка стоит на одной из сносимых строк, и после замены она указывала бы
            // на вью-модель, которой в документе больше нет: место в списке теряется,
            // вкладка ленты пустеет, а каретка на листе повисает. Место запоминается
            // строкой от начала оглавления и восстанавливается по новому списку.
            int caretOffset = TocCaretOffset(start, count);

            RemoveParagraphBlocks(start, count);
            InsertParagraphBlocks(start, paragraphs);

            CommitUndoStep();
            RaiseStructureChanged();

            RestoreTocCaret(start, paragraphs.Count, caretOffset);
            RequestTocPageNumbers(force: true);
        }

        /// <summary>
        /// Убирает оглавление из рукописи вместе с его настройками.
        ///
        /// Пустой абзац на месте снесённого оглавления не оставляется: человек просил
        /// убрать оглавление, а не заменить его пустой строкой. Если оно было единственным
        /// содержимым раздела, пустой абзац заводится — документ без абзацев невозможен.
        /// </summary>
        public void RemoveToc(Models.Toc.TocSettings settings)
        {
            if (IsReadOnly || settings is null) return;

            var (start, count) = TocService.FindRange(_document, settings.Id);
            if (start < 0) return;

            var watch = System.Diagnostics.Stopwatch.StartNew();

            bool caretWasInside = TocCaretOffset(start, count) >= 0;

            // Строки запоминаются ДО снятия: шаг отмены держит именно их, и это всё, что
            // ему нужно. Снимок целой рукописи сюда больше не ходит — он стоил на этом
            // документе сотню миллисекунд на удалении и две секунды на откате.
            var removed = CollectTocBlocks(start, count);

            var filler = DropTocBlocks(start, count, settings);
            long removeMs = watch.ElapsedMilliseconds;

            PushUndoCommandDelegate?.Invoke(new Commands.TocRemoveCommand(
                this, start, removed, settings, filler, "Удаление оглавления"));

            long undoMs = watch.ElapsedMilliseconds - removeMs;

            RaiseStructureChanged();
            long layoutMs = watch.ElapsedMilliseconds - removeMs - undoMs;

            LastTocTiming =
                $"строк {count}, снятие {removeMs} мс, шаг отмены {undoMs} мс, " +
                $"пересбор {layoutMs} мс, всего {watch.ElapsedMilliseconds} мс";

            // Каретка стояла на снесённой строке: без переноса она указывает на
            // вью-модель, которой в документе больше нет. Место её то же — туда встал
            // текст, шедший за оглавлением.
            if (caretWasInside) MoveCaretAfterTocRemoval(start, immediate: false);
        }

        /// <summary>Абзацы оглавления одним списком — их запоминает шаг отмены.</summary>
        private System.Collections.Generic.List<ParagraphBlock> CollectTocBlocks(
            int blockIndex, int count)
        {
            var blocks = _document.Sections[0].Blocks;
            var result = new System.Collections.Generic.List<ParagraphBlock>(count);

            for (int i = blockIndex; i < blockIndex + count && i < blocks.Count; i++)
                if (blocks[i] is ParagraphBlock para) result.Add(para);

            return result;
        }

        /// <summary>
        /// Снимает строки оглавления и его запись в настройках.
        ///
        /// Пустой абзац на месте снесённого оглавления не оставляется: человек просил
        /// убрать оглавление, а не заменить его пустой строкой. Если оно было единственным
        /// содержимым раздела, пустой абзац заводится — документ без абзацев невозможен,
        /// и тогда он возвращается вызывающему: отмена обязана его убрать.
        /// </summary>
        private ParagraphBlock? DropTocBlocks(
            int blockIndex, int count, Models.Toc.TocSettings settings)
        {
            RemoveParagraphBlocks(blockIndex, count);
            _document.TableOfContents?.Remove(settings);

            if (Paragraphs.Count > 0) return null;

            var empty = new ParagraphBlock();
            _document.Sections[0].Blocks.Add(empty);
            Paragraphs.Add(CreateParagraphViewModel(empty));
            return empty;
        }

        /// <summary>
        /// Ставит каретку туда, где было снятое оглавление.
        /// </summary>
        /// <param name="immediate">
        /// true — поставить каретку сейчас же, не откладывая и не прокручивая.
        ///
        /// Так надо отмене и повтору. После них полотно само доводит вид до каретки, и
        /// делает это СРАЗУ, как только команда вернула управление. Отложенный переход к
        /// этому моменту ещё не случился, каретка держит номер слайса от прежнего состава
        /// документа — а в него только что вернулись или из него ушли триста абзацев. По
        /// старому номеру полотно и прокручивало: человек нажимал Ctrl+Z и оказывался
        /// неизвестно где.
        ///
        /// Живому удалению, наоборот, нужен обычный отложенный переход: там никто следом
        /// не прокручивает, и довезти вид до места обязан он сам.
        /// </param>
        private void MoveCaretAfterTocRemoval(int blockIndex, bool immediate)
        {
            int index = CountParagraphsBefore(blockIndex);
            if (index >= Paragraphs.Count) index = Paragraphs.Count - 1;
            if (index < 0) return;

            MoveCaretToParagraph(index, immediate);
        }

        /// <summary>
        /// Уводит каретку на абзац по его месту в списке абзацев.
        /// </summary>
        /// <param name="immediate">
        /// true — поставить её ещё и сейчас же, до возврата из метода.
        ///
        /// Отложенный переход нужен всегда: он приходит последним, после всех пересборок
        /// раскладки, и только он может оставить за собой последнее слово о том, где
        /// каретка. Немедленная постановка — вдобавок к нему, для отмены и повтора:
        /// сразу после их возврата полотно прокручивает вид к каретке, и держать в этот
        /// миг номер слайса от прежнего состава документа нельзя.
        /// </param>
        private void MoveCaretToParagraph(int paragraphIndex, bool immediate)
        {
            if (paragraphIndex < 0 || paragraphIndex >= Paragraphs.Count) return;

            if (immediate) SetCaretToParagraphDelegate?.Invoke(paragraphIndex);

            GoToParagraphKeepHistory(paragraphIndex);
            SetActiveParagraph(Paragraphs[paragraphIndex]);
        }

        /// <summary>
        /// Повтор снятия оглавления. Зовётся шагом отмены и только им.
        ///
        /// Место оглавления ищется заново, а не берётся из шага: к повтору документ стоит
        /// ровно в том состоянии, в каком его оставила отмена, и искать по опознавателю
        /// надёжнее, чем помнить номер блока.
        /// </summary>
        /// <returns>Пустой абзац, заведённый на месте оглавления, либо null.</returns>
        public ParagraphBlock? DropTocBlocksForRedo(Models.Toc.TocSettings settings)
        {
            if (settings is null || _document.Sections.Count == 0) return null;

            var (start, count) = TocService.FindRange(_document, settings.Id);
            if (start < 0) return null;

            var filler = DropTocBlocks(start, count, settings);

            RaiseStructureChanged();
            MoveCaretAfterTocRemoval(start, immediate: true);

            return filler;
        }

        /// <summary>
        /// Возвращает снятое оглавление на место. Зовётся шагом отмены и только им.
        ///
        /// Абзацы вставляются те же самые, что были вынуты, — со всеми правками, которые
        /// человек успел в них внести. Вью-модели им заводятся новые, и только им:
        /// остальные абзацы рукописи не пересоздаются, их раскладки остаются в кэше.
        /// </summary>
        public void RestoreTocBlocks(
            int blockIndex,
            System.Collections.Generic.List<ParagraphBlock> blocks,
            Models.Toc.TocSettings settings,
            ParagraphBlock? filler)
        {
            if (blocks is null || blocks.Count == 0) return;
            if (_document.Sections.Count == 0) return;

            var docBlocks = _document.Sections[0].Blocks;

            // Пустой абзац, которым заняли опустевшую рукопись, уходит: его место
            // занимает вернувшееся оглавление.
            if (filler is not null)
            {
                int fillerAt = docBlocks.IndexOf(filler);
                if (fillerAt >= 0) RemoveParagraphBlocks(fillerAt, 1);
            }

            _document.TableOfContents ??=
                new System.Collections.Generic.List<Models.Toc.TocSettings>();

            bool known = false;
            foreach (var toc in _document.TableOfContents)
                if (toc.Id == settings.Id) { known = true; break; }

            if (!known) _document.TableOfContents.Add(settings);

            int at = blockIndex < 0 ? 0
                : (blockIndex > docBlocks.Count ? docBlocks.Count : blockIndex);

            InsertParagraphBlocks(at, blocks);

            RaiseStructureChanged();

            // Каретка встаёт на первую вернувшуюся строку — там, где была правка, которую
            // отменили. Немедленно: полотно прокрутит вид к каретке сразу после возврата
            // из этой команды, и ждать оно не станет.
            MoveCaretToParagraph(CountParagraphsBefore(at), immediate: true);
        }

        /// <summary>
        /// Пересобирает все оглавления рукописи: заголовки могли смениться, страницы уехать.
        ///
        /// Старые строки сносятся и заменяются новыми целиком. Править их по месту было бы
        /// дешевле, но неверно: между двумя пересборками главу могли переименовать, увести
        /// на другой уровень или удалить — сравнивать тут нечего.
        /// </summary>
        public void UpdateTOC()
        {
            if (IsReadOnly) return;
            if (_document.TableOfContents is not { Count: > 0 }) return;

            // Стили оглавления могли не дойти до рукописи: файл начат до их появления,
            // а список стилей при открытии не пополняется. Без них строки ссылаются на
            // несуществующее имя и выходят обычным текстом.
            //
            // Уведомление — только если стили правда дописаны: оно чистит кэш раскладки
            // целиком и перевёрстывает всю книгу.
            if (TocService.EnsureBuiltInStyles(_document)) RaiseStylesChanged();

            bool changed = false;
            BeginUndoStep("Обновление оглавления");

            int caretStart = -1;
            int caretOffset = -1;
            int caretNewCount = 0;

            foreach (var settings in _document.TableOfContents)
            {
                var (start, count) = TocService.FindRange(_document, settings.Id);
                if (start < 0) continue;

                var headings = TocService.Collect(_document, settings, GetBlockPageNumbers());

                var paragraphs = TocService.BuildParagraphs(
                    settings, headings,
                    TocService.TextWidthPt(_document),
                    TocDefaultTitleProvider?.Invoke() ?? "Оглавление");

                int offset = TocCaretOffset(start, count);
                if (offset >= 0)
                {
                    caretStart = start;
                    caretOffset = offset;
                    caretNewCount = paragraphs.Count;
                }

                RemoveParagraphBlocks(start, count);
                InsertParagraphBlocks(start, paragraphs);
                changed = true;
            }

            CommitUndoStep();

            if (!changed) return;

            RaiseStructureChanged();

            if (caretStart >= 0)
                RestoreTocCaret(caretStart, caretNewCount, caretOffset);

            RequestTocPageNumbers(force: true);
        }

        /// <summary>
        /// Какой по счёту строкой оглавления стоит каретка. -1 — каретка вне этого
        /// оглавления, и трогать её при пересборке незачем.
        /// </summary>
        private int TocCaretOffset(int blockStart, int blockCount)
        {
            if (_activeParagraph is null || blockCount <= 0) return -1;

            var blocks = _document.Sections[0].Blocks;
            int paragraphOffset = 0;

            for (int i = blockStart; i < blockStart + blockCount && i < blocks.Count; i++)
            {
                if (blocks[i] is not ParagraphBlock para) continue;

                if (ReferenceEquals(para, _activeParagraph.Model)) return paragraphOffset;

                paragraphOffset++;
            }

            return -1;
        }

        /// <summary>
        /// Возвращает каретку в оглавление после его замены. Строки могло стать меньше —
        /// тогда каретка встаёт на последнюю: уводить её из оглавления нельзя, иначе
        /// вкладка ленты закрывается посреди работы человека с ней.
        /// </summary>
        private void RestoreTocCaret(int blockStart, int newParagraphCount, int caretOffset)
        {
            if (caretOffset < 0 || newParagraphCount <= 0) return;

            int offset = caretOffset >= newParagraphCount ? newParagraphCount - 1 : caretOffset;
            int index = CountParagraphsBefore(blockStart) + offset;

            if (index < 0 || index >= Paragraphs.Count) return;

            GoToParagraphKeepHistory(index);
            SetActiveParagraph(Paragraphs[index]);
        }

        /// <summary>
        /// Ставит каретку в этот абзац потока — по его месту в списке абзацев.
        /// </summary>
        private void MoveCaretToParagraphBlock(ParagraphBlock block)
        {
            if (block is null) return;

            for (int i = 0; i < Paragraphs.Count; i++)
            {
                if (!ReferenceEquals(Paragraphs[i].Model, block)) continue;

                GoToParagraphKeepHistory(i);
                SetActiveParagraph(Paragraphs[i]);
                return;
            }
        }

        /// <summary>
        /// Второй проход оглавления: проставляет строкам номера страниц по готовой
        /// раскладке, не пересобирая список.
        ///
        /// Первый проход неизбежно врёт. Оглавление вставляют в начало книги, оно само
        /// сдвигает всё, что за ним, и номера, посчитанные до его появления на листе,
        /// устаревают в тот же миг. Пересобирать список ради цифр нельзя: человек мог
        /// поправить в строке слово, и пересборка стёрла бы правку. Поэтому правится
        /// только хвост строки за табуляцией.
        ///
        /// Шаг отмены здесь не открывается намеренно: это не правка человека, а
        /// доводка того, что он уже сделал, и отдельного Ctrl+Z ей не полагается.
        /// </summary>
        /// <param name="force">
        /// true — обновить все оглавления рукописи, даже те, которым выключено
        /// самообновление. Так работает кнопка «Обновить».
        /// </param>
        /// <returns>true — хотя бы одна строка изменилась.</returns>
        // Строки оглавления, которым проход только что сменил номер страницы. Канвас
        // забирает список и чистит раскладки ровно у них.
        //
        // Без него оставался один выход — сбросить кэш раскладок целиком, а это значит
        // прогнать через Skia каждый абзац книги ради полутора сотен изменившихся строк.
        // Проход повторяется до трёх раз, и сброс обходился в три полных пересчёта.
        private readonly System.Collections.Generic.List<ParagraphViewModel> _tocTouched = new();

        /// <summary>
        /// Забирает список строк, которым последний проход сменил номер. Список отдаётся
        /// один раз: канвас уже почистил их раскладки, и второй раз чистить нечего.
        /// </summary>
        /// <summary>
        /// Из чего сложилось время последней операции над оглавлением. Читает канвас и
        /// кладёт в журнал: спорить о том, где уходит время, без замера бессмысленно, а
        /// секундомер отсюда виден всем трём фазам — снимку отмены, снятию строк и
        /// пересбору раскладки.
        /// </summary>
        public string? LastTocTiming { get; private set; }

        public System.Collections.Generic.List<ParagraphViewModel> TakeTocTouched()
        {
            var result = new System.Collections.Generic.List<ParagraphViewModel>(_tocTouched);
            _tocTouched.Clear();
            return result;
        }

        public bool ApplyTocPageNumbers(bool force = false)
        {
            if (IsReadOnly) return false;
            if (_document.TableOfContents is not { Count: > 0 }) return false;
            if (_document.Sections.Count == 0) return false;

            _tocTouched.Clear();

            var pageMap = GetBlockPageNumbers();
            if (pageMap.Count == 0) return false;

            double textWidthPt = TocService.TextWidthPt(_document);
            var blocks = _document.Sections[0].Blocks;
            bool changed = false;

            // Смена номера в строке — это смена её текста, а на неё канвас отвечает
            // поабзацным пересчётом: ищет строку в списке абзацев, перебирает все слайсы
            // книги и верстает её заново. На каждую из полутора сотен строк. Следом всё
            // равно идёт общий пересбор, поэтому поабзацный путь здесь только мешает.
            BeginBulkRebuild();
            try
            {
                foreach (var settings in _document.TableOfContents)
                {
                    if (!force && !settings.AutoUpdate) continue;

                    var (start, count) = TocService.FindRange(_document, settings.Id);
                    if (start < 0) continue;

                    int vmIndex = CountParagraphsBefore(start);

                    for (int i = start; i < start + count && i < blocks.Count; i++)
                    {
                        if (blocks[i] is not ParagraphBlock entry) continue;

                        int currentVmIndex = vmIndex;
                        vmIndex++;

                        var targetId = entry.Properties.TocTargetBlockId;
                        if (targetId is null) continue;

                        pageMap.TryGetValue(targetId.Value, out int page);

                        if (!TocService.ApplyPageNumber(entry, settings, page, textWidthPt))
                            continue;

                        changed = true;

                        if (currentVmIndex < 0 || currentVmIndex >= Paragraphs.Count) continue;

                        var pvm = Paragraphs[currentVmIndex];
                        pvm.RefreshPlainTextFromModel();
                        _tocTouched.Add(pvm);
                    }
                }
            }
            finally
            {
                EndBulkRebuild();
            }

            if (changed) RaiseContentModified();

            return changed;
        }

        /// <summary>
        /// Вставляет готовые абзацы в поток документа вместе с их вью-моделями.
        /// Полная пересборка списка абзацев здесь не годится: она рвёт каретку и выделение.
        /// </summary>
        private void InsertParagraphBlocks(
            int blockIndex, System.Collections.Generic.List<ParagraphBlock> paragraphs)
        {
            var blocks = _document.Sections[0].Blocks;
            int vmIndex = CountParagraphsBefore(blockIndex);

            // Вставка идёт тем же скопом, что и снятие: канвас на каждую строку заводил
            // бы её раскладку по отдельности, а следом всё равно пересобирает всё.
            BeginBulkRebuild();
            try
            {
                for (int i = 0; i < paragraphs.Count; i++)
                {
                    blocks.Insert(blockIndex + i, paragraphs[i]);
                    Paragraphs.Insert(vmIndex + i, CreateParagraphViewModel(paragraphs[i]));
                }
            }
            finally
            {
                EndBulkRebuild();
            }
        }

        /// <summary>Убирает из потока абзацы диапазона вместе с их вью-моделями.</summary>
        private void RemoveParagraphBlocks(int blockIndex, int count)
        {
            if (count <= 0) return;

            var blocks = _document.Sections[0].Blocks;
            int vmIndex = CountParagraphsBefore(blockIndex);

            // Снятие каждой строки — уведомление канвасу, а тот на каждое пересчитывает
            // раскладку соседнего абзаца и перебирает все слайсы документа, отыскивая её.
            // На оглавлении в полторы сотни строк это сотни проходов по всей книге, и
            // удаление одним щелчком вставало намертво. Признак массовой перестройки
            // отменяет поабзацный путь: следом всё равно идёт общий пересбор.
            BeginBulkRebuild();
            try
            {
                for (int i = 0; i < count && blockIndex < blocks.Count; i++)
                {
                    if (blocks[blockIndex] is ParagraphBlock && vmIndex < Paragraphs.Count)
                        Paragraphs.RemoveAt(vmIndex);

                    blocks.RemoveAt(blockIndex);
                }
            }
            finally
            {
                EndBulkRebuild();
            }
        }

        /// <summary>
        /// Сколько абзацев лежит в потоке до этого блока. Paragraphs содержит только абзацы,
        /// а Blocks — ещё таблицы, картинки и разрывы, поэтому места в них не совпадают.
        /// </summary>
        private int CountParagraphsBefore(int blockIndex)
        {
            var blocks = _document.Sections[0].Blocks;
            int count = 0;

            for (int i = 0; i < blockIndex && i < blocks.Count; i++)
                if (blocks[i] is ParagraphBlock) count++;

            return count;
        }
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

        public void SetViewMode(EditorViewMode mode) => ViewMode = mode;

        /// <summary>Ставит подачу чтения: разворот, одиночный лист или сплошная лента.</summary>
        public void SetReadingFlow(Models.Settings.ReadingFlow flow) => ReadingFlow = flow;

        public void ToggleFullscreen() => IsFullscreen = !IsFullscreen;
        public void ToggleFocusMode() => IsFocusMode = !IsFocusMode;

        public void SetCanvasTheme(CanvasThemePreset preset)
        {
            _document.CanvasSettings.ApplyPreset(preset);
            this.RaisePropertyChanged(nameof(CanvasSettings));
            RaiseViewPreferenceChanged();
        }

        public void SetCanvasColors(string pageBackground, string textColor)
        {
            _document.CanvasSettings.Preset = CanvasThemePreset.Custom;
            _document.CanvasSettings.PageBackgroundColor = pageBackground;
            _document.CanvasSettings.DefaultTextColor = textColor;
            this.RaisePropertyChanged(nameof(CanvasSettings));
            RaiseViewPreferenceChanged();
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

        // ── Импорт и экспорт документа ────────────────────────────────────

        /// <summary>Формат файла для экспорта.</summary>
        private enum ExportFileFormat
        {
            Docx,
            Pdf,
            Txt,
            Markdown
        }

        public void ExportToPdf() => _ = ExportDocumentAsync(ExportFileFormat.Pdf);
        public void ExportToDocx() => _ = ExportDocumentAsync(ExportFileFormat.Docx);
        public void ExportToTxt() => _ = ExportDocumentAsync(ExportFileFormat.Txt);
        public void ExportToMarkdown() => _ = ExportDocumentAsync(ExportFileFormat.Markdown);

        /// <summary>
        /// Импортирует документ и заменяет им содержимое текущего.
        /// Работа асинхронная (диалог подтверждения и разбор файла), поэтому метод
        /// интерфейса только запускает её: команда риббона возврата не ждёт.
        /// </summary>
        public void ImportFromFile(string filePath) => _ = ImportDocumentAsync(filePath);

        private async Task ImportDocumentAsync(string filePath)
        {
            var notifications = CoreServices.GetService<INotificationService>();

            if (IsReadOnly)
            {
                notifications?.ShowWarning(TextEditorStrings.Import_ReadOnly);
                return;
            }

            if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath))
            {
                notifications?.ShowError(TextEditorStrings.Import_FileNotFound);
                return;
            }

            string extension = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
            if (extension != ".docx" && extension != ".txt")
            {
                notifications?.ShowWarning(
                    string.Format(TextEditorStrings.Import_Unsupported, extension));
                return;
            }

            // Импорт заменяет весь документ целиком — спрашиваем разрешение.
            var dialogs = CoreServices.GetService<IDialogService>();
            if (dialogs is not null)
            {
                var answer = await dialogs.ShowMessageAsync(
                    TextEditorStrings.Import_Confirm_Title,
                    TextEditorStrings.Import_Confirm_Message,
                    Writersword.Core.Enums.MessageBoxType.Question,
                    Writersword.Core.Enums.MessageBoxButtons.YesNo);

                if (answer != Writersword.Core.Enums.MessageBoxResult.Yes) return;
            }

            // Импорт затирает содержимое документа целиком, поэтому перед ним
            // снимается точка восстановления проекта. Если снять её не удалось,
            // пользователь решает сам, продолжать ли без страховки.
            if (!await CreatePreImportBackupAsync())
            {
                if (dialogs is null) 
                {
                    notifications?.ShowWarning(TextEditorStrings.Import_Backup_Failed);
                }
                else
                {
                    var backupAnswer = await dialogs.ShowMessageAsync(
                        TextEditorStrings.Import_Backup_Failed_Title,
                        TextEditorStrings.Import_Backup_Failed_Message,
                        Writersword.Core.Enums.MessageBoxType.Warning,
                        Writersword.Core.Enums.MessageBoxButtons.YesNo);

                    if (backupAnswer != Writersword.Core.Enums.MessageBoxResult.Yes) return;
                }
            }
            else
            {
                notifications?.ShowInfo(TextEditorStrings.Import_Backup_Created);
            }

            ImportResult result;
            try
            {
                var importer = new ImportService();
                result = extension == ".docx"
                    ? await importer.ImportFromDocxAsync(filePath)
                    : await importer.ImportFromTxtAsync(filePath);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[IMPORT] Не удалось импортировать {Path}", filePath);
                notifications?.ShowError(string.Format(TextEditorStrings.Import_Failed, ex.Message));
                return;
            }

            if (!result.Success || result.Document is null)
            {
                _log.Warning("[IMPORT] Импорт не удался: {Error}", result.ErrorMessage);
                notifications?.ShowError(
                    string.Format(TextEditorStrings.Import_Failed, result.ErrorMessage ?? string.Empty));
                return;
            }

            // Картинки кладём в проект до показа документа: иначе ссылки на них повиснут.
            if (result.ExtractedImages.Count > 0)
            {
                var ctx = CoreServices.GetService<ITabCollection>()?.ActiveTab?.Context;
                if (ctx is null)
                {
                    notifications?.ShowWarning(TextEditorStrings.Import_ImagesNotSaved);
                }
                else
                {
                    try
                    {
                        foreach (var image in result.ExtractedImages)
                            ctx.WriteFile($"TextEditor/Images/{image.Key}", image.Value);

                        // Байты картинок должны лежать на диске сразу: кеш восстановления
                        // хранит только JSON документа и сошлётся на эти файлы.
                        ctx.FlushStorage();
                    }
                    catch (Exception ex)
                    {
                        _log.Error(ex, "[IMPORT] Не удалось сохранить картинки импортированного документа");
                        notifications?.ShowWarning(TextEditorStrings.Import_ImagesNotSaved);
                    }
                }
            }

            ApplyImportedDocument(result.Document);

            notifications?.ShowSuccess(TextEditorStrings.Import_Success);

            foreach (var warning in result.Warnings)
                notifications?.ShowWarning(warning);
        }

        /// <summary>
        /// Снимает точку восстановления проекта перед импортом.
        /// Точка берётся с файла проекта на диске, поэтому текущее состояние
        /// документа сначала сохраняется. Тип точки — пользовательский:
        /// прореживание истории такие точки не удаляет, и вернуться к тексту
        /// до импорта можно и через несколько дней.
        /// </summary>
        private async Task<bool> CreatePreImportBackupAsync()
        {
            try
            {
                var tab = CoreServices.GetService<ITabCollection>()?.ActiveTab;
                string? projectPath = tab?.Context?.FilePath;

                if (tab is null || string.IsNullOrWhiteSpace(projectPath))
                    return false;

                var workflow = CoreServices.GetService<IProjectWorkflow>();
                var backups = CoreServices.GetService<IBackupService>();

                if (workflow is null || backups is null) return false;

                if (!await workflow.SaveDocumentAsync(tab, showNotification: false))
                    return false;

                return await backups.CreateSnapshotAsync(projectPath!, BackupTrigger.UserPoint);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[IMPORT] Не удалось снять точку восстановления перед импортом");
                return false;
            }
        }

        /// <summary>
        /// Переносит содержимое импортированного документа в текущий.
        /// Сам объект документа не подменяется: на него ссылаются вкладка, канвас
        /// и сериализатор проекта — заменяется только содержимое.
        /// </summary>
        private void ApplyImportedDocument(DocumentModel imported)
        {
            BeginEditDelegate?.Invoke("Импорт документа");

            _document.Title = imported.Title;

            _document.Styles.Clear();
            foreach (var style in imported.Styles)
                _document.Styles.Add(style);

            // Настройки страницы копируются по значениям, а не подменой объекта:
            // ссылку на них держат канвас, линейка и печать, и подмена оставила бы
            // их со старым листом.
            CopyPageSettings(imported.PageSettings, _document.PageSettings);

            _document.ColumnSettings.ColumnCount = imported.ColumnSettings.ColumnCount;
            _document.ColumnSettings.GapMm = imported.ColumnSettings.GapMm;
            _document.ColumnSettings.ShowSeparator = imported.ColumnSettings.ShowSeparator;

            _document.Sections.Clear();
            foreach (var section in imported.Sections)
                _document.Sections.Add(section);

            if (_document.Sections.Count == 0)
            {
                var section = new SectionModel();
                section.Blocks.Add(new ParagraphBlock());
                _document.Sections.Add(section);
            }

            // Аннотации ссылаются на прежние абзацы и чанки — после замены содержимого
            // они указывают в пустоту.
            _document.Annotations.Clear();

            CommitEditDelegate?.Invoke();

            _activeParagraph = null;
            TableActiveCellParagraph = null;

            RebuildStyleNames();
            RebuildParagraphViewModels();

            // Канвас пересобирает геометрию листа и резолвер стилей по уведомлению
            // о PageSettings: без него он продолжает раскладывать документ на прежнем
            // листе и со старым набором стилей, и число страниц не меняется.
            RaisePageSettingsChanged();
            this.RaisePropertyChanged(nameof(CanvasSettings));

            // Кэш раскладки абзацев ключуется по их вью-моделям: после импорта они
            // все новые, поэтому чистим кэш целиком.
            FireParagraphFormatChanged();

            StructureChanged?.Invoke();
            DocumentRestored?.Invoke();
            ContentModified?.Invoke();
        }

        /// <summary>Переносит физические параметры страницы из импортированного документа.</summary>
        private static void CopyPageSettings(TextEditorPageSettings source, TextEditorPageSettings target)
        {
            target.PaperSize = source.PaperSize;
            target.WidthMm = source.WidthMm;
            target.HeightMm = source.HeightMm;
            target.Orientation = source.Orientation;
            target.MarginTopMm = source.MarginTopMm;
            target.MarginBottomMm = source.MarginBottomMm;
            target.MarginLeftMm = source.MarginLeftMm;
            target.MarginRightMm = source.MarginRightMm;
            target.MarginGutterMm = source.MarginGutterMm;
            target.HeaderDistanceMm = source.HeaderDistanceMm;
            target.FooterDistanceMm = source.FooterDistanceMm;
        }

        private async Task ExportDocumentAsync(ExportFileFormat format)
        {
            var notifications = CoreServices.GetService<INotificationService>();

            var window = (Avalonia.Application.Current?.ApplicationLifetime
                as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

            if (window?.StorageProvider is null)
            {
                notifications?.ShowError(TextEditorStrings.Export_Failed_NoDialog);
                return;
            }

            (string extension, string typeName) = format switch
            {
                ExportFileFormat.Docx => (".docx", TextEditorStrings.FileType_Docx),
                ExportFileFormat.Pdf => (".pdf", TextEditorStrings.FileType_Pdf),
                ExportFileFormat.Txt => (".txt", TextEditorStrings.FileType_Txt),
                _ => (".md", TextEditorStrings.FileType_Markdown)
            };

            var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = TextEditorStrings.Export_Dialog_Title,
                SuggestedFileName = BuildExportFileName(extension),
                DefaultExtension = extension.TrimStart('.'),
                FileTypeChoices = new[]
                {
                    new FilePickerFileType(typeName) { Patterns = new[] { "*" + extension } }
                }
            });

            if (file is null) return;

            string? path = file.TryGetLocalPath();
            if (string.IsNullOrEmpty(path))
            {
                notifications?.ShowError(TextEditorStrings.Export_Failed_NoDialog);
                return;
            }

            // Картинки читаются из архива проекта здесь, в потоке UI: сам экспорт
            // работает в фоне, а хранилище проекта для параллельного чтения не рассчитано.
            var images = CollectDocumentImages();
            byte[]? ResolveImage(string fileName) =>
                images.TryGetValue(fileName, out var data) ? data : null;

            ExportResult result;
            try
            {
                var exporter = new ExportService();
                result = format switch
                {
                    ExportFileFormat.Docx => await exporter.ExportToDocxAsync(_document, path, ResolveImage),
                    ExportFileFormat.Pdf => await exporter.ExportToPdfAsync(_document, path, ResolveImage),
                    ExportFileFormat.Txt => await exporter.ExportToTxtAsync(_document, path),
                    _ => await exporter.ExportToMarkdownAsync(_document, path)
                };
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[EXPORT] Не удалось экспортировать в {Path}", path);
                notifications?.ShowError(string.Format(TextEditorStrings.Export_Failed, ex.Message));
                return;
            }

            if (!result.Success)
            {
                _log.Warning("[EXPORT] Экспорт не удался: {Error}", result.ErrorMessage);
                notifications?.ShowError(
                    string.Format(TextEditorStrings.Export_Failed, result.ErrorMessage ?? string.Empty));
                return;
            }

            notifications?.ShowSuccess(
                string.Format(TextEditorStrings.Export_Success, System.IO.Path.GetFileName(path)));

            foreach (var warning in result.Warnings)
                notifications?.ShowWarning(warning);
        }

        /// <summary>Имя файла по умолчанию для диалога экспорта — из заголовка документа.</summary>
        private string BuildExportFileName(string extension)
        {
            string title = string.IsNullOrWhiteSpace(_document.Title) ? "Document" : _document.Title;

            foreach (char invalid in System.IO.Path.GetInvalidFileNameChars())
                title = title.Replace(invalid, '_');

            title = title.Trim();
            if (title.Length == 0) title = "Document";
            if (title.Length > 100) title = title.Substring(0, 100);

            return title + extension;
        }

        /// <summary>
        /// Байты всех картинок документа по именам файлов. Экспорт получает их
        /// готовым словарём: у сервиса экспорта нет доступа к архиву проекта.
        /// </summary>
        private Dictionary<string, byte[]> CollectDocumentImages()
        {
            var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

            var ctx = CoreServices.GetService<ITabCollection>()?.ActiveTab?.Context;
            if (ctx is null) return result;

            foreach (var section in _document.Sections)
            {
                foreach (var block in section.InlineObjects
                    .Concat(section.FloatingObjects)
                    .Concat(section.Blocks))
                {
                    if (block is not ImageBlock image) continue;
                    if (string.IsNullOrWhiteSpace(image.ImageFileName)) continue;
                    if (result.ContainsKey(image.ImageFileName)) continue;

                    try
                    {
                        var data = ctx.ReadFile($"TextEditor/Images/{image.ImageFileName}");
                        if (data is { Length: > 0 })
                            result[image.ImageFileName] = data;
                    }
                    catch (Exception ex)
                    {
                        _log.Warning(ex, "[EXPORT] Не удалось прочитать картинку {File}", image.ImageFileName);
                    }
                }
            }

            return result;
        }

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
            BeginBulkRebuild();
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
                EndBulkRebuild();
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