using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using Serilog;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Writersword.Core.Interfaces.Services.Input;
using Writersword.Core.Models.Print;
using Writersword.Core.Models.Rendering;
using System.Text.Json;
using Writersword.Modules.TextEditor.Rendering;
using Writersword.Modules.Common;
using Writersword.Modules.TextEditor.Commands;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.ViewModels;
using Writersword.Modules.TextEditor.ViewModels.Blocks;

namespace Writersword.Modules.TextEditor.Document
{
    public sealed partial class DocumentCanvas : Control
    {
        // ── Конвертация единиц ────────────────────────────────────────────
        private const float PtToPx = 96f / 72f;
        private const float PxToPt = 72f / 96f;

        // ── Константы геометрии ───────────────────────────────────────────
        private const float PageGapPt = 15f;
        private const float DraftPadHPt = 9f;
        private const float DraftPadWPt = 0f;
        private const float ReadingMaxPt = 510f;
        private const float FallbackLinePt = 16.5f;

        // Отступ каретки якоря от границы таблицы — чтобы не перекрывалась рамкой.
        private const float AnchorMarginPt = 4f;

        // Дополнительный отступ сверху для строк параграфа, продолжающегося на новой странице.
        // Добавляется к lineGroupYPt при переносе — чтобы первая строка не прилипала к полю.
        private const float PageContinuationTopPadPt = 4f;

        // ── CellInfo: metadata для параграфа ячейки таблицы ──────────────
        // Таблица — это просто "параграфы в тюрьме": параграфы ячеек
        // добавляются в _layouts рядом с обычными параграфами. Каретка,
        // выделение и навигация работают через единый _layouts без
        // отдельного "режима таблицы".
        private sealed class CellInfo
        {
            public TableBlock Table { get; }
            public TableCell Cell { get; }
            public ParagraphBlock ParaBlock { get; }
            public int CellParaIndex { get; }  // индекс внутри cell.Paragraphs
            public int TableEntryIdx { get; }  // индекс в _tables
            public float ContentXPt { get; }  // абсолютный X начала содержимого
            public float ContentYPt { get; }  // абсолютный Y начала содержимого
            public float ClipX { get; }  // clip rect для рендера
            public float ClipY { get; }
            public float ClipW { get; }
            public float ClipH { get; }

            public CellInfo(TableBlock table, TableCell cell, ParagraphBlock paraBlock,
                int cellParaIndex, int tableEntryIdx,
                float contentXPt, float contentYPt,
                float clipX, float clipY, float clipW, float clipH)
            {
                Table = table; Cell = cell; ParaBlock = paraBlock;
                CellParaIndex = cellParaIndex; TableEntryIdx = tableEntryIdx;
                ContentXPt = contentXPt; ContentYPt = contentYPt;
                ClipX = clipX; ClipY = clipY; ClipW = clipW; ClipH = clipH;
            }
        }

        // ── Layout параграфов ─────────────────────────────────────────────
        private record ParaLayout(
            ParagraphViewModel Vm,
            SKTextLayout? Layout,      // null для параграфов за пределами viewport-буфера
            float Ypt,
            float HeightPt,
            int PageIndex,
            int LineFrom,
            int LineTo,
            float AbsXPt = 0,          // абсолютный X левого края текстовой зоны
            CellInfo? Cell = null,     // null = обычный параграф
            Rendering.ListMarkerInfo? Marker = null);  // маркер списка (null = не элемент списка)

        private record PageRect(
            float Ypt,
            float WidthPt,
            float HeightPt,
            float PadLeftPt,
            float PadTopPt,
            float MarginLeftPt,
            float PadBottomPt = 0f);

        // ── Layout таблиц (только для рендера рамок/фона) ─────────────────
        // Одна запись = один слайс таблицы на одной странице.
        // При разбивке таблицы по строкам создаётся несколько записей с одним Layout.
        private record TableEntry(
            TableBlock Table,
            SKTableLayout Layout,
            float Ypt,
            float XPt,
            int PageIndex,
            int RowFrom = 0,
            int RowTo = -1,
            float LastRowVisibleHeightPt = -1f,
            float FirstRowContentOffsetPt = 0f,
            bool IsContinuation = false);

        // ── Layout изображений ────────────────────────────────────────────
        // Одна запись = одно изображение-блок на своей странице.
        private record ImageEntry(
            ImageBlock Block,
            float Ypt,
            float XPt,
            float WidthPt,
            float HeightPt,
            int PageIndex);

        // ── Атомарный снимок для render-потока ────────────────────────────
        private readonly object _renderLock = new();
        private List<ParaLayout> _layouts = new();
        private List<PageRect> _pages = new();
        private List<TableEntry> _tables = new();
        private List<ImageEntry> _images = new();

        // Выделенная картинка (для рамки и удаления). null — ничего не выделено.
        private ImageBlock? _selectedImage;
        private readonly SKPaint _paintImageSelection = new()
        {
            Style = SKPaintStyle.Stroke,
            Color = new SKColor(0xE0, 0x7B, 0x39),
            StrokeWidth = 1.5f,
            IsAntialias = true
        };

        // Перетаскивание плавающей картинки.
        private bool _imageDragging;
        private bool _imageDragMoved;
        private float _imgDragStartXPt;
        private float _imgDragStartYPt;
        private double _imgDragStartOffX;
        private double _imgDragStartOffY;

        // Изменение размера выделенной картинки за маркер.
        // Индексы: 0 — верх-лево, 1 — верх-право, 2 — низ-право, 3 — низ-лево,
        // 4 — верх-центр, 5 — право-центр, 6 — низ-центр, 7 — лево-центр, 8 — поворот.
        private bool _imageResizing;
        private bool _imageResizeMoved;
        private int _imageResizeCorner = -1;
        private float _imgResizeStartXPt;
        private float _imgResizeStartYPt;
        private double _imgResizeStartW;
        private double _imgResizeStartH;
        private double _imgResizeStartOffX;
        private double _imgResizeStartOffY;
        private double _imgResizeStartRotDeg;

        // Поворот выделенной картинки за круглый маркер над верхней гранью.
        private bool _imageRotating;
        private bool _imageRotateMoved;
        private double _imgRotStartDeg;
        private float _imgRotPointerStartDeg;
        private float _imgRotCenterXPt;
        private float _imgRotCenterYPt;

        // Полупрозрачная заливка угловых маркеров размера.
        private readonly SKPaint _paintImageHandleFill = new()
        {
            Style = SKPaintStyle.Fill,
            Color = new SKColor(0xFF, 0xFF, 0xFF),
            IsAntialias = true
        };

        // Сглаживание края и билинейная фильтрация при отрисовке картинок:
        // без него повёрнутая картинка рисуется с рваным ступенчатым краем.
        private readonly SKPaint _paintImageDraw = new()
        {
            IsAntialias = true,
            FilterQuality = SKFilterQuality.High
        };

        // Режим предпросмотра переполнения страницы: во время драга (поворот/ресайз)
        // страница инлайн-картинки заморожена как на момент нажатия. Если картинка
        // перестаёт влезать — остаётся на месте, выходит за нижний край листа и
        // рисуется серой полупрозрачной. Если была на следующей странице и снова
        // влезает — остаётся на следующей. Реальный перенос в обе стороны
        // выполняется финальным пересбором после отпускания кнопки мыши.
        private bool _imageOverflowPreviewMode;
        private ImageBlock? _imageOverflowPreviewBlock;

        // Была ли выделенная картинка на момент старта драга перенесена
        // на следующую страницу из-за нехватки места.
        private bool _imagePreviewStartTransferred;

        // Инлайн-картинки, перенесённые последним пересбором на следующую страницу
        // из-за нехватки места. Читается при старте драга для заморозки страницы.
        private HashSet<ImageBlock> _inlineTransferredImages = new();

        // Рамка картинки: цвет и толщина выставляются перед отрисовкой каждой картинки.
        private readonly SKPaint _paintImageBorderDraw = new()
        {
            Style = SKPaintStyle.Stroke,
            IsAntialias = true
        };

        // Обесцвеченная полупрозрачная отрисовка картинки в предпросмотре переполнения.
        private readonly SKPaint _paintImageDrawOverflow = new()
        {
            IsAntialias = true,
            FilterQuality = SKFilterQuality.High,
            ColorFilter = SKColorFilter.CreateColorMatrix(new float[]
            {
                0.21f, 0.72f, 0.07f, 0f,    0f,
                0.21f, 0.72f, 0.07f, 0f,    0f,
                0.21f, 0.72f, 0.07f, 0f,    0f,
                0f,    0f,    0f,    0.55f, 0f
            })
        };

        // Половина стороны квадратного маркера и радиус попадания по нему, в пунктах.
        private const float ImageHandleHalfPt = 3.5f;
        private const float ImageHandleHitPt = 6f;

        // Режим обрезки выделенной картинки: маркеры двигают границы кадрирования,
        // а не размер. Сбрасывается при снятии выделения.
        private bool _imageCropMode;

        // Акцентная заливка маркеров в режиме обрезки.
        private readonly SKPaint _paintImageHandleCropFill = new()
        {
            Style = SKPaintStyle.Fill,
            Color = new SKColor(0xE0, 0x7B, 0x39),
            IsAntialias = true
        };

        // Обрезка драгом маркера: стартовые доли кадрирования.
        private bool _imageCropDragging;
        private double _imgCropStartL;
        private double _imgCropStartT;
        private double _imgCropStartR;
        private double _imgCropStartB;

        // Расстояние маркера поворота от верхней грани и его радиус, в пунктах.
        private const float ImageRotateHandleOffsetPt = 20f;
        private const float ImageRotateHandleRadiusPt = 6f;

        // Круговая стрелка внутри маркера поворота.
        private readonly SKPaint _paintRotateArrowStroke = new()
        {
            Style = SKPaintStyle.Stroke,
            Color = new SKColor(0xE0, 0x7B, 0x39),
            StrokeWidth = 1.1f,
            IsAntialias = true
        };
        private readonly SKPaint _paintRotateArrowFill = new()
        {
            Style = SKPaintStyle.Fill,
            Color = new SKColor(0xE0, 0x7B, 0x39),
            IsAntialias = true
        };
        private double _canvasWidth;
        private double _canvasHeight;
        private float _canvasHeightPt;

        // ── Кеш лейаутов обычных параграфов ──────────────────────────────
        private readonly Dictionary<ParagraphViewModel,
            (string Text, float Width, SKTextLayout Layout)> _layoutCache = new();

        // Кеш декодированных изображений по имени файла внутри проекта.
        private readonly Dictionary<string, SKImage?> _imageCache = new();

        // Поля live-preview шрифта вынесены в DocumentCanvas.FontPreview.cs.

        // Хранит лямбды подписанные в WirePvm чтобы точно отписать в UnwirePvm.
        // Анонимные лямбды нельзя отписать через -= без сохранения ссылки.
        private readonly Dictionary<ParagraphViewModel, Action> _pvmFocusHandlers = new();

        // ── Кеш VM-обёрток и лейаутов для параграфов ячеек ───────────────
        // Ключ — ParagraphBlock (живёт в TableCell.Paragraphs).
        // VM-обёртки переиспользуются между rebuild'ами → SnapCaretToCorrectSlice
        // находит нужный слайс через Vm == targetVm (ссылка стабильна).
        private readonly Dictionary<ParagraphBlock, ParagraphViewModel> _cellVmCache = new();
        private readonly Dictionary<ParagraphBlock,
            (string Text, float Width, SKTextLayout Layout)> _cellLayoutCache = new();

        // Превью шрифта для абзацев ячеек: оригинальный абзац -> preview-абзац (построен по
        // выделенному диапазону). BuildTableLayout строит раскладку из него. Пусто вне превью.
        private readonly Dictionary<ParagraphBlock, ParagraphBlock> _cellFontPreview = new();

        // Кеш раскладок таблиц. BuildTableLayout перевёрстывает параграфы всех ячеек таблицы,
        // и без кеша это выполнялось при каждом пересборе раскладки — то есть на каждый
        // введённый символ. Инвалидируется вместе с _cellLayoutCache.
        private readonly Dictionary<TableBlock, (float Width, SKTableLayout Layout)> _tableLayoutCache = new();

        // Общая точка сброса кешей содержимого ячеек: поабзацного и табличного.
        // Все операции, меняющие содержимое или структуру таблиц, вызывают этот метод.
        private void InvalidateCellLayoutCaches()
        {
            _cellLayoutCache.Clear();
            _tableLayoutCache.Clear();
        }

        // Возвращает раскладку таблицы из кеша либо строит и кеширует её.
        // Во время live-preview шрифта кеш не используется: раскладка зависит от
        // временной карты _cellFontPreview и не должна переживать предпросмотр.
        private SKTableLayout GetOrBuildTableLayout(TableBlock table, float textWidthPt)
        {
            bool previewActive = _cellFontPreview.Count > 0;
            if (!previewActive
                && _tableLayoutCache.TryGetValue(table, out var cached)
                && Math.Abs(cached.Width - textWidthPt) < 0.1f)
                return cached.Layout;

            var layout = _renderer.BuildTableLayout(table, textWidthPt, _styleResolver!, _cellFontPreview);
            if (!previewActive)
                _tableLayoutCache[table] = (textWidthPt, layout);
            return layout;
        }

        // ── Дебаунс пересчёта ─────────────────────────────────────────────
        private System.Threading.CancellationTokenSource _rebuildCts = new();

        // ── Виртуализация ─────────────────────────────────────────────────
        private ScrollViewer? _parentScrollViewer;
        private double _scrollOffsetY = 0;
        private double _viewportHeight = 600;

        // ── Каретка ───────────────────────────────────────────────────────
        // Единая для всего документа включая ячейки таблицы.
        private int _caretPara = 0;
        private int _caretChar = 0;
        private int _caretLineHint = -1;
        private bool _caretVisible = true;
        private float _preferredCaretXPt = 0f;

        // Активна ли серия вертикальных перемещений (Up/Down подряд). В начале серии столбец
        // (_preferredCaretXPt) захватывается из ЖИВОЙ геометрии каретки и держится до любого
        // горизонтального перемещения/клика/правки (там вызывается UpdatePreferredX, который
        // сбрасывает флаг). Так столбец не «уезжает» при многократном Down на короткие строки.
        private bool _vNavActive;
        private readonly DispatcherTimer _caretTimer;

        // ── Анимация скролла ──────────────────────────────────────────────
        private DispatcherTimer? _scrollAnimTimer;
        private double _scrollAnimFrom;
        private double _scrollAnimTo;
        private double _scrollAnimElapsedMs;
        private const double ScrollAnimDurationMs = 130.0;
        private const double ScrollAnimTickMs = 8.0;

        // ── Активная таблица (для структурных операций AddRow и т.д.) ────
        private TableBlock? _activeTableBlock;
        private int _activeCellRow = 0;
        private int _activeCellCol = 0;
        private int _activeCellTableEntryIdx = -1;

        // ── Drag ручек таблицы (без использования линейки) ───────────────
        private enum TableDragMode { None, ColResize, TableMove, RowResize }
        private TableDragMode _tableDragMode = TableDragMode.None;
        private int _tableDragColIndex = -1;    // индекс колонки при ColResize
        private int _tableDragEntryIdx = -1;    // индекс TableEntry
        private float _tableDragStartXPt = 0f;    // X мыши при начале drag в pt
        private float _tableDragStartVal = 0f;    // исходная ширина колонки или LeftIndentPt в pt

        // Размер hit-зоны ручки в pt (~5px при 100% zoom)
        private const float TableHandleHitPt = 5f * PxToPt;

        // ── Выделение ─────────────────────────────────────────────────────
        private int _selStartPara = 0;
        private int _selStartChar = 0;
        private int _selEndPara = 0;
        private int _selEndChar = 0;
        private bool _isSelecting;

        // ── Авто-скролл при выделении у края вьюпорта ─────────────────────
        // Пока идёт выделение и указатель у верхней/нижней границы видимой области, таймер
        // прокручивает документ со скоростью, растущей по мере приближения к краю, и продолжает
        // расширять выделение под текущим указателем. _autoScrollViewportPoint хранит позицию
        // указателя ОТНОСИТЕЛЬНО вьюпорта (указатель физически не двигается во время авто-скролла,
        // поэтому его координаты в канвасе пересчитываются из вьюпорт-позиции и текущего offset).
        private Avalonia.Threading.DispatcherTimer? _autoScrollTimer;
        private double _autoScrollVelocity;
        private Point _autoScrollViewportPoint;

        // ── Выделение нескольких ячеек ────────────────────────────────────
        // Единый словарь: TableBlock → (startRow, startCol, endRow, endCol).
        // Обновляется при движении курсора, очищается при новом клике.
        private bool _isCellRangeSelecting = false;

        // Ячейка, в которой было нажатие мыши (якорь cell-range выделения).
        // Хранится отдельно, т.к. для пустых ячеек без layout-записи HitTest
        // возвращает неправильный pi (ближайший по Y параграф другой строки).
        private TableBlock? _pressCellTable;
        private int _pressCellRow = -1;
        private int _pressCellCol = -1;

        private readonly Dictionary<TableBlock, (int sr, int sc, int er, int ec)> _tableSelections = new();

        // Потоковое выделение ячеек: абзац ячейки -> выделенный диапазон [from, to].
        // Частичная стартовая ячейка, целые промежуточные по порядку чтения, частичная конечная.
        // Пусто, когда потокового выделения нет (тогда работает прямоугольное _tableSelections).
        private readonly Dictionary<ParagraphBlock, (int from, int to)> _cellFlowRanges = new();

        // Полностью попавшие в поток ячейки (table, row, col) — заливаются целиком прямоугольником
        // ячейки, как обычное табличное выделение (а не по тексту, иначе пустые/узкие дают полоски).
        private readonly HashSet<(TableBlock table, int row, int col)> _cellFlowFull = new();

        private sealed record FrozenTableSelection(
            TableBlock Table,
            int StartRow, int StartCol,
            int EndRow, int EndCol);

        // ── Bitmap-кеш для мигания каретки и скролла ──────────────────────
        //
        // render-bitmap — CPU-буфер, в который выполняется офскрин-растеризация текста.
        // После рендера с него снимается иммутабельный SKImage (_displayImage): DrawImage
        // такого снимка не копирует пиксели при каждом кадре, а GPU кэширует текстуру
        // по uniqueID изображения. Повторные кадры (мигание каретки, чужие инвалидации,
        // скролл в пределах overscan) стоят один блит закэшированной текстуры.
        private readonly object _bitmapLock = new();
        private SKBitmap? _renderBitmap;   // офскрин-цель, пишем на render-треде
        private SKImage? _displayImage;    // иммутабельный снимок, читает compositor
        private int _bitmapW;
        private int _bitmapH;
        private float _lastFullRenderScrollY;
        // Очередь для освобождения битмапов старого размера.
        private readonly System.Collections.Concurrent.ConcurrentQueue<SKBitmap> _bitmapDisposeQueue = new();

        // Очередь для освобождения списанных снимков (_displayImage).
        // SKImage освобождается ТОЛЬКО на render-потоке (в начале следующего рендера)
        // либо при повторном прикреплении канваса: освобождение с UI-потока по таймеру
        // диспетчера гонялось с уже поставленной в очередь композитора отрисовкой —
        // DrawImage читал освобождённый нативный объект и падал с access violation.
        private readonly System.Collections.Concurrent.ConcurrentQueue<SKImage> _imageDisposeQueue = new();

        private bool _caretOnlyRedraw = false;
        // Контент изменился и требует полного рендера. Пока флаг поднят, быстрый путь
        // (_caretOnlyRedraw — мигание каретки, скролл по кэш-снимку) не имеет права
        // подменить полный рендер: иначе скролл-событие, пришедшее в одном батче с
        // правкой текста (ScrollToCaret при печати у края страницы), перетирало запрос
        // полного рендера, и на экране вечно оставался старый снимок.
        private bool _contentDirty = true;
        private volatile bool _isTransitioning;

        // Текущий сфокусированный канвас. Хоткеи редактора приходят через глобальный
        // _hotKeyService и исполняются в TextEditorModule.ExecuteHotKey, которому нужен
        // именно активный документ. Раньше он брал _lastCreatedView, но при переключении
        // воркмодов/вкладок это уже другой экземпляр (или его PageCanvas отвязан) — и
        // Enter/Copy/навигация уходили в чужой канвас. Ссылка обновляется на фокусе и
        // сбрасывается при откреплении из дерева.
        internal static DocumentCanvas? FocusedInstance;

        // ── Буфер обмена ─────────────────────────────────────────────────
        private string? _clipboardCache;

        // Внутренний буфер: JSON-массив ClipboardBlock (параграфы + таблицы в порядке документа).
        // Заполняется при Copy, используется при Paste для точного воспроизведения структуры.
        private string? _internalClipboardJson;

        private enum ClipboardBlockKind { Paragraph, Table }
        private sealed class ClipboardBlock
        {
            public ClipboardBlockKind Kind { get; set; }
            public string? Text { get; set; }           // plain-text для Paragraph (fallback)
            public ParagraphBlock? Block { get; set; }  // полная модель параграфа (стили + runs)
            public TableBlock? Table { get; set; }      // для Table (уже слайснутая)
        }

        // ── Рендеринг ─────────────────────────────────────────────────────
        private readonly SKTextRenderer _renderer = new();
        private StyleResolver? _styleResolver;

        /// <summary>
        /// Карта "скрипт → шрифт" из настроек редактора.
        /// Пробрасывается в StyleResolver и используется SKTextRenderer для фолбэка символов.
        /// Обновляется из TextEditorModule при изменении настроек.
        /// </summary>
        public IReadOnlyDictionary<string, string>? ScriptFontMap
        {
            get => _scriptFontMap;
            set
            {
                _scriptFontMap = value;
                if (DocVm is not null)
                    _styleResolver = new StyleResolver(DocVm.Document.Styles, _scriptFontMap);
            }
        }
        private IReadOnlyDictionary<string, string>? _scriptFontMap;

        // ── Логирование ───────────────────────────────────────────────────
        private static readonly ILogger _logger = Log.ForContext<DocumentCanvas>();

        // ── HotKey ───────────────────────────────────────────────────────
        private IHotKeyService? _hotKeyService;

        // ── Undo ─────────────────────────────────────────────────────────
        public UndoRedoStack? UndoStack { get; set; }

        /// <summary>
        /// Лёгкий стек операционных команд для набора текста.
        /// Каждая запись хранит несколько байт вместо полного JSON документа.
        /// Устанавливается TextEditorModule при создании View.
        /// </summary>
        public Writersword.Modules.TextEditor.Commands.TextUndoRedoStack? TextUndoStack { get; set; }

        // Единый хронологический порядок отмены между снапшотным (UndoStack) и операционным
        // (TextUndoStack) стеками. Без него ExecuteUndo сначала вычерпывал бы весь операционный
        // стек, и Ctrl+Z откатывал бы не последнее действие, а сначала весь набор текста.
        private enum UndoSource { Text, Snapshot }
        private readonly LinkedList<UndoSource> _undoOrder = new();
        private readonly Stack<UndoSource> _redoOrder = new();

        // Кладёт операционную команду в стек и фиксирует её в общем порядке отмены.
        // Если команда слилась с предыдущей (TryMerge вернул, что добавления нет) — отдельной
        // записи порядка не создаём.
        private void PushTextCommand(Writersword.Modules.TextEditor.Commands.ITextCommand cmd)
        {
            if (TextUndoStack is null) return;
            if (TextUndoStack.Push(cmd))
            {
                _undoOrder.AddLast(UndoSource.Text);
                _redoOrder.Clear();
            }
        }

        // Фиксирует снапшотную команду в общем порядке отмены (вызывается из CommitEdit).
        private void RecordSnapshotInOrder()
        {
            _undoOrder.AddLast(UndoSource.Snapshot);
            _redoOrder.Clear();
        }

        // Сброс порядка отмены — при смене документа, когда стеки очищаются.
        private void ResetUndoOrder()
        {
            _undoOrder.Clear();
            _redoOrder.Clear();
        }

        private double _monitorSizeInches = 0;
        private double _cachedDpi = 96.0;
        private DocumentSnapshotCommand? _pendingSnapshot;

        // ── Цвета ─────────────────────────────────────────────────────────
        private static readonly SKColor SelectionColor = new(0x33, 0x90, 0xFF, 0x60);
        private static readonly SKColor CanvasBgColor = new(0xE8, 0xE8, 0xE8);
        private static readonly SKColor PageShadowColor = new(0x00, 0x00, 0x00, 0x28);

        // Кешированные паинты — создаются один раз, живут всё время жизни канваса.
        // Вместо 13+ аллокаций на каждый рендер-кадр — ноль.
        // Все паинты используются только на compositor-треде, поэтому thread-safe.
        private readonly SKPaint _paintCanvasBg = new() { Color = new SKColor(0xE8, 0xE8, 0xE8) };
        private readonly SKPaint _paintPageShadow = new() { Color = new SKColor(0x00, 0x00, 0x00, 0x28) };
        private readonly SKPaint _paintPageWhite = new() { Color = SKColors.White };
        private readonly SKPaint _paintTransparent = new() { Color = SKColors.Transparent };
        // Обычное выделение — мягкое полупрозрачное голубое.
        private readonly SKPaint _paintSelection = new() { Color = new SKColor(0x33, 0x90, 0xFF, 0x60) };
        // Выделение поверх голубой/циановой заливки: голубое по голубому сливается, поэтому
        // для таких заливок берём мягкий тёплый (янтарный) полупрозрачный — он контрастен синему.
        private readonly SKPaint _paintSelectionAlt = new() { Color = new SKColor(0xFF, 0x8F, 0x00, 0x66) };
        private readonly SKPaint _paintCaret = new() { Color = SKColors.Black, StrokeWidth = 1.1f, IsAntialias = false, IsStroke = true };
        private readonly SKPaint _paintHandleFill = new() { Color = new SKColor(0x22, 0x99, 0xFF, 0xCC), IsAntialias = true };
        private readonly SKPaint _paintHandleStroke = new() { Color = new SKColor(0xFF, 0xFF, 0xFF, 0xCC), StrokeWidth = 1f, IsStroke = true, IsAntialias = true };
        private readonly SKPaint _paintHandleArrow = new() { Color = SKColors.White, StrokeWidth = 1f, IsStroke = true, IsAntialias = true };
        // Паинт для фона ячейки — Color мутируется перед каждым DrawRect (compositor-тред).
        private readonly SKPaint _paintCellBg = new();

        private DocumentViewModel? _docVm;
        private DocumentViewModel? DocVm => _docVm;
        private double Zoom => DocVm?.Zoom ?? 1.0;

        // Блок-якорь на который нужно переместить каретку после ближайшего rebuild.
        // Устанавливается при вставке разрыва страницы, потребляется в ScheduleRebuild.
        private ParagraphBlock? _pendingFocusBlock;

        // ── Callbacks ────────────────────────────────────────────────────
        public Action<double>? RecommendedZoomChanged { get; set; }

        private double _lastPageOffsetXPx = 0;

        // Горизонтальный центр страницы (pageXPt), запечённый в раскладку при последнем пересчёте.
        // Рендер сравнивает его с центром по живому _canvasWidth и доводит страницу сдвигом, не
        // пересобирая раскладку. Во время зум-жеста это центрирует лист без тяжёлой пагинации, а
        // когда пересчёт уже прошёл — сдвиг нулевой (бесшовно).
        private float _layoutPageXPt;
        private Action<double>? _pageOffsetXChanged;
        public Action<double>? PageOffsetXChanged
        {
            get => _pageOffsetXChanged;
            set { _pageOffsetXChanged = value; value?.Invoke(_lastPageOffsetXPx); }
        }

        public Action<IReadOnlyList<double>, IReadOnlyList<double>, double, int>? CaretEnteredTable { get; set; }
        public Action? CaretLeftTable { get; set; }

        /// <summary>Выделена (true) или снята с выделения (false) картинка — для контекстной вкладки.</summary>
        public Action<bool>? ImageSelectionChanged { get; set; }

        /// <summary>
        /// Вызывается когда каретка перемещается на другую страницу.
        /// Вертикальная линейка отображает шкалу только для этой страницы.
        /// </summary>
        public Action<int>? CaretPageChanged { get; set; }

        public Action<int, int, double>? CaretStateChanged { get; set; }

        public double MonitorSizeInches
        {
            get => _monitorSizeInches;
            set
            {
                if (Math.Abs(_monitorSizeInches - value) < 0.01) return;
                _monitorSizeInches = value;
                RebuildDpiCache();
                InvalidateMeasure();
            }
        }

        public DocumentCanvas()
        {
            Focusable = true;
            Cursor = new Cursor(StandardCursorType.Ibeam);

            _caretTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
            _caretTimer.Tick += (_, _) =>
            {
                _caretVisible = !_caretVisible;
                _caretOnlyRedraw = true;
                InvalidateVisual();
            };
            GotFocus += OnGotFocusHandler;
            // Каретка мигает только пока редактор в фокусе: без фокуса таймер остановлен
            // и редактор не генерирует кадры вообще — окно не перерисовывается в покое.
            LostFocus += OnLostFocusHandler;
        }

        // ── HotKey ───────────────────────────────────────────────────────
        public void SetHotKeyService(IHotKeyService service) => _hotKeyService = service;

        // ── DPI ───────────────────────────────────────────────────────────
        private void RebuildDpiCache()
        {
            if (_monitorSizeInches <= 0)
            {
                _cachedDpi = 96.0;
                Dispatcher.UIThread.Post(() => RecommendedZoomChanged?.Invoke(RecommendedZoom));
                return;
            }
            var topLevel = TopLevel.GetTopLevel(this);
            var screen = topLevel?.Screens?.ScreenFromVisual(this);
            if (screen is null) return;
            double physW = screen.Bounds.Width * screen.Scaling;
            double physH = screen.Bounds.Height * screen.Scaling;
            double diagPx = Math.Sqrt(physW * physW + physH * physH);
            _cachedDpi = diagPx / _monitorSizeInches;
            Dispatcher.UIThread.Post(() => RecommendedZoomChanged?.Invoke(RecommendedZoom));
        }

        public double RecommendedZoom => _cachedDpi > 0 ? _cachedDpi / 96.0 : 1.0;

        private static float MmToPt(double mm) => (float)(mm * 72.0 / 25.4);
        private static double PtToMm(float pt) => pt * 25.4 / 72.0;

        private float GetPageWidthPt()
        {
            var ps = DocVm?.Document.PageSettings;
            if (ps is null) return MmToPt(210);
            return ps.Orientation == PageOrientation.Landscape ? MmToPt(ps.HeightMm) : MmToPt(ps.WidthMm);
        }
        private float GetPageHeightPt()
        {
            var ps = DocVm?.Document.PageSettings;
            if (ps is null) return MmToPt(297);
            return ps.Orientation == PageOrientation.Landscape ? MmToPt(ps.WidthMm) : MmToPt(ps.HeightMm);
        }
        private (float left, float top, float right, float bottom) GetPagePaddingPt()
        {
            var ps = DocVm?.Document.PageSettings;
            if (ps is null) return (MmToPt(20), MmToPt(20), MmToPt(20), MmToPt(20));
            return (MmToPt(ps.MarginLeftMm + ps.MarginGutterMm), MmToPt(ps.MarginTopMm),
                    MmToPt(ps.MarginRightMm), MmToPt(ps.MarginBottomMm));
        }

        // ── DataContext / ScrollViewer ────────────────────────────────────
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _isTransitioning = false;

            // Дренируем битмапы и снимки, накопившиеся пока view была detached.
            // RenderWithSKCanvas тоже дренирует, но он вызывается только когда
            // контрол видим. При смене воркмода TextEditor может долго не рендериться.
            // К моменту повторного прикрепления старые отрисовки композитора давно
            // завершены — освобождать здесь безопасно.
            while (_bitmapDisposeQueue.TryDequeue(out var stale))
                stale?.Dispose();
            while (_imageDisposeQueue.TryDequeue(out var staleImage))
                staleImage?.Dispose();

            base.OnAttachedToVisualTree(e);

            // Возвращаем подписки, снятые в OnDetachedFromVisualTree. Раньше они жили
            // только в конструкторе: при переиспользовании кэшированной вьюхи (detach →
            // reattach) конструктор не вызывается, и после переприцепки фокусная логика
            // и мигание каретки оставались мёртвыми. Пара -=/+= защищает от двойной
            // подписки при первом attach после конструктора.
            GotFocus -= OnGotFocusHandler;
            GotFocus += OnGotFocusHandler;
            LostFocus -= OnLostFocusHandler;
            LostFocus += OnLostFocusHandler;

            if (IsFocused)
            {
                _caretVisible = true;
                _caretTimer.Stop();
                _caretTimer.Start();
            }

            // Восстанавливаем подписки на DocumentViewModel и параграфы, снятые при
            // detach: у переиспользуемой вьюхи DataContext не меняется, и без этого
            // цепочка «ввод → PlainText → перерисовка» оставалась мёртвой навсегда.
            WireDocVmSubscriptions();

            RebuildDpiCache();
            SubscribeToScrollViewer();
            _ = PrefetchClipboardAsync();
            InvalidateFull();
            Avalonia.Threading.Dispatcher.UIThread.Post(
                InvalidateMeasure,
                Avalonia.Threading.DispatcherPriority.Loaded);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _isTransitioning = true;
            if (ReferenceEquals(FocusedInstance, this)) FocusedInstance = null;
            base.OnDetachedFromVisualTree(e);

            // Останавливаем таймер — он держит ссылку на this через замыкание и мешает GC.
            _caretTimer.Stop();
            GotFocus -= OnGotFocusHandler;
            LostFocus -= OnLostFocusHandler;

            // Отписываемся от DocumentViewModel и всех ParagraphViewModel.
            if (_docVm is not null)
            {
                _docVm.Paragraphs.CollectionChanged -= OnParagraphsChanged;
                _docVm.PropertyChanged -= OnDocVmPropertyChanged;
                _docVm.ParagraphFormatChanged -= OnParagraphFormatChanged;
                _docVm.StructureChanged -= OnStructureChanged;
                _docVm.BeginFontPreviewDelegate = null;
                _docVm.PreviewFontFamilyDelegate = null;
                _docVm.EndFontPreviewDelegate = null;
                _docVm.FocusEditorDelegate = null;
                _docVm.OnPageBreakInserted = null;
                _docVm.UndoDelegate = null;
                _docVm.RedoDelegate = null;
                _docVm.CutDelegate = null;
                _docVm.CopyDelegate = null;
                _docVm.PasteDelegate = null;
                _docVm.BeginEditDelegate = null;
                _docVm.CommitEditDelegate = null;
                _docVm.CommitRunPropertyGranularDelegate = null;
                _docVm.CommitTextEditsDelegate = null;
                _docVm.CommitParagraphPropertyGranularDelegate = null;
                _docVm.GetCaretWordRangeDelegate = null;

                // Снимаем делегаты с каждого параграфа — иначе замыкания удерживают canvas.
                foreach (var pvm in _docVm.Paragraphs)
                    UnwirePvm(pvm);
            }

            // Отменяем фоновый rebuild.
            _rebuildCts.Cancel();

            // Останавливаем прогрев кеша раскладки: его проходы перепланируют себя
            // через диспетчер и без остановки продолжали бы шейпить отцепленную вьюху.
            // При повторном прикреплении measure перезапустит прогрев с того же места —
            // уже зашейпленные абзацы лежат в кеше и повторно не обрабатываются.
            SetWarmupActive(false);

            // Не диспозим bitmap и снимок напрямую — render-тред (compositor) может
            // держать локальную ссылку на тот же объект и рисовать его прямо сейчас:
            // уже поставленная в очередь композитора отрисовка выполняется ПОСЛЕ
            // detach, и освобождение с UI-потока (даже отложенным постом) гонялось
            // с DrawImage на render-потоке — приложение падало с access violation
            // внутри SkiaSharp. Оба объекта уходят в очереди и освобождаются только
            // там, где гонка исключена: в начале следующего рендера (render-поток,
            // рендеры сериализованы) либо при повторном прикреплении канваса
            // (к этому моменту старые отрисовки давно завершены). Если канвас больше
            // никогда не рендерится — нативную память вернёт финализатор SkiaSharp.
            lock (_bitmapLock)
            {
                if (_renderBitmap is not null)
                {
                    _bitmapDisposeQueue.Enqueue(_renderBitmap);
                    _renderBitmap = null;
                }
                if (_displayImage is not null)
                {
                    _imageDisposeQueue.Enqueue(_displayImage);
                    _displayImage = null;
                }
            }

            // SKPaint не диспозим здесь: DockFactory переиспользует DocumentCanvas
            // (detach → reattach при переключении вкладок). Если диспозить паинты
            // на detach, при повторном reattach рендер упадёт с disposed-объектами.
            // SKPaint — крошечные нативные объекты (~200 байт), GC соберёт при финализации.

            // Списки раскладки (_layouts, _pages, _tables) и кеши вёрстки СОХРАНЯЕМ:
            // DockFactory переиспользует канвас при переключении вкладок и воркмодов
            // (detach → reattach), и с живой раскладкой MeasureOverride после reattach
            // пропускает полный проход пагинации по отпечатку (LayoutsMatchCurrentState) —
            // на больших документах это разница между мгновенно и секундами.
            // Актуальность гарантирует отпечаток: смена документа создаёт новый
            // DocumentViewModel (ловится по ссылке), изменение ширины/режима/стилей
            // ловится по остальным полям отпечатка. При закрытии вкладки канвас
            // умирает целиком и нативную память соберёт GC.

            UnsubscribeFromScrollViewer();
        }

        private void OnGotFocusHandler(object? sender, Avalonia.Input.FocusChangedEventArgs e)
        {
            FocusedInstance = this;
            _ = PrefetchClipboardAsync();

            // Возобновляем мигание каретки.
            _caretVisible = true;
            _caretTimer.Stop();
            _caretTimer.Start();
            _caretOnlyRedraw = true;
            InvalidateVisual();
        }

        private void OnLostFocusHandler(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            // Без фокуса каретка не мигает и не показывается — редактор перестаёт
            // генерировать кадры, композитор окна спит, пока его не разбудит кто-то другой.
            _caretTimer.Stop();
            if (_caretVisible)
            {
                _caretVisible = false;
                _caretOnlyRedraw = true;
                InvalidateVisual();
            }
        }

        private async Task PrefetchClipboardAsync()
        {
            try
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard is null) return;
#pragma warning disable CS0618
                _clipboardCache = await clipboard.TryGetTextAsync();
#pragma warning restore CS0618
            }
            catch { }
        }

        private void SubscribeToScrollViewer()
        {
            StyledElement? parent = Parent;
            while (parent is not null)
            {
                if (parent is ScrollViewer sv)
                {
                    _parentScrollViewer = sv;
                    sv.ScrollChanged += OnScrollChanged;
                    sv.PropertyChanged += OnScrollViewerPropertyChanged;
                    _scrollOffsetY = sv.Offset.Y;
                    _viewportHeight = sv.Viewport.Height;
                    break;
                }
                parent = parent.Parent;
            }
        }

        private void OnViewportSizeChanged()
        {
            if (_parentScrollViewer is null) return;
            _viewportHeight = _parentScrollViewer.Viewport.Height;
            // Принудительно пересчитываем layout — viewport мог измениться
            // из-за закрытия/открытия панели dock, страница должна перецентроваться.
            InvalidateMeasure();
        }

        private void OnScrollViewerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == ScrollViewer.ViewportProperty)
                OnViewportSizeChanged();
        }

        private void UnsubscribeFromScrollViewer()
        {
            if (_parentScrollViewer is null) return;
            _parentScrollViewer.ScrollChanged -= OnScrollChanged;
            _parentScrollViewer.PropertyChanged -= OnScrollViewerPropertyChanged;
            _parentScrollViewer = null;
        }

        /// <summary>Число страниц текущей раскладки.</summary>
        public int PageCount
        {
            get { lock (_renderLock) { return Math.Max(1, _pages.Count); } }
        }

        /// <summary>
        /// Номер страницы (1-based) у верха вьюпорта при заданном вертикальном смещении прокрутки (px).
        /// Используется всплывающей подсказкой при перетаскивании ползунка.
        /// </summary>
        public int GetPageAtOffset(double offsetYPx)
        {
            List<PageRect> pages;
            lock (_renderLock) { pages = _pages; }
            if (pages.Count == 0) return 1;
            double zoom = Zoom;
            float viewTopPt = (float)(offsetYPx / zoom * PxToPt);
            int page = 1;
            for (int i = 0; i < pages.Count; i++)
            {
                if (pages[i].Ypt <= viewTopPt + 1f) page = i + 1;
                else break;
            }
            return page;
        }

        private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            if (sender is not ScrollViewer sv) return;
            _scrollOffsetY = sv.Offset.Y;
            _viewportHeight = sv.Viewport.Height;
            // Контент не менялся — скролл лишь сдвигает окно по уже отрисованному
            // overscan-битмапу. Ветка _caretOnlyRedraw в RenderWithSKCanvas переиспользует
            // битмап, пока вьюпорт внутри его диапазона, и уходит в полный рендер только
            // когда прокрутка выходит за край отрисованной области.
            _caretOnlyRedraw = true;
            InvalidateVisual();
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);

            if (_docVm is not null)
            {
                // Отписываемся от всех параграфов старого DocVm.
                // Без этого каждый ParagraphViewModel держит замыкание на этот канвас
                // через FocusRequested и RequestFocusAtPosition — canvas не освобождается GC.
                foreach (var pvm in _docVm.Paragraphs)
                    UnwirePvm(pvm);

                _docVm.Paragraphs.CollectionChanged -= OnParagraphsChanged;
                _docVm.PropertyChanged -= OnDocVmPropertyChanged;
                _docVm.ParagraphFormatChanged -= OnParagraphFormatChanged;
                _docVm.StructureChanged -= OnStructureChanged;
                _docVm.BeginFontPreviewDelegate = null;
                _docVm.PreviewFontFamilyDelegate = null;
                _docVm.EndFontPreviewDelegate = null;
                _docVm.FocusEditorDelegate = null;
                _docVm.OnPageBreakInserted = null;
                _docVm.UndoDelegate = null;
                _docVm.RedoDelegate = null;
                _docVm.CutDelegate = null;
                _docVm.CopyDelegate = null;
                _docVm.PasteDelegate = null;
                _docVm.BeginEditDelegate = null;
                _docVm.CommitEditDelegate = null;
                _docVm.CommitRunPropertyGranularDelegate = null;
                _docVm.CommitTextEditsDelegate = null;
                _docVm.CommitParagraphPropertyGranularDelegate = null;
                _docVm.GetCaretWordRangeDelegate = null;
            }

            _docVm = DataContext as DocumentViewModel;
            _layoutCache.Clear();
            _pvmFocusHandlers.Clear();
            _cellVmCache.Clear();
            InvalidateCellLayoutCaches();
            ResetUndoOrder();

            if (DocVm is not null)
            {
                _styleResolver = new StyleResolver(DocVm.Document.Styles, _scriptFontMap);
                _lastZoom = DocVm.Zoom;
                WireDocVmSubscriptions();
            }

            InvalidateMeasure();
        }

        /// <summary>
        /// Подписки канваса на DocumentViewModel, его параграфы и делегаты.
        /// Вызывается из OnDataContextChanged и ПОВТОРНО из OnAttachedToVisualTree:
        /// при detach все подписки снимаются, а у кэшированной вьюхи (переиспользование
        /// в доке) OnDataContextChanged не срабатывает — без повторной подписки ввод
        /// менял модель, но перерисовка не запускалась (цепочка PlainText →
        /// ScheduleRebuild → InvalidateFull была мертва). Идемпотентен: перед каждой
        /// подпиской выполняется отписка.
        /// </summary>
        private void WireDocVmSubscriptions()
        {
            if (DocVm is null) return;

            DocVm.Paragraphs.CollectionChanged -= OnParagraphsChanged;
            DocVm.PropertyChanged -= OnDocVmPropertyChanged;
            DocVm.ParagraphFormatChanged -= OnParagraphFormatChanged;
            DocVm.StructureChanged -= OnStructureChanged;

            DocVm.Paragraphs.CollectionChanged += OnParagraphsChanged;
            DocVm.PropertyChanged += OnDocVmPropertyChanged;
            DocVm.ParagraphFormatChanged += OnParagraphFormatChanged;
            DocVm.StructureChanged += OnStructureChanged;
            DocVm.BeginFontPreviewDelegate = BeginFontPreviewSession;
            DocVm.PreviewFontFamilyDelegate = PreviewFontFamilySession;
            DocVm.EndFontPreviewDelegate = EndFontPreviewSession;
            DocVm.FocusEditorDelegate = FocusEditorFromHost;
            DocVm.OnPageBreakInserted = block => _pendingFocusBlock = block;
            DocVm.UndoDelegate = ExecuteUndo;
            DocVm.RedoDelegate = ExecuteRedo;
            DocVm.CutDelegate = ExecuteCut;
            DocVm.CopyDelegate = ExecuteCopy;
            DocVm.PasteDelegate = ExecutePaste;
            DocVm.BeginEditDelegate = BeginEdit;
            DocVm.CommitEditDelegate = CommitEdit;
            DocVm.CommitRunPropertyGranularDelegate = CommitRunPropertyGranular;
            DocVm.GetCellSelectionRangesDelegate = GetCellSelectionRanges;
            DocVm.CommitTextEditsDelegate = CommitTextEditsGranular;
            DocVm.CommitParagraphPropertyGranularDelegate = CommitParagraphPropertyGranular;
            DocVm.GetCaretWordRangeDelegate = GetCaretWordRange;
            DocVm.TrySetImageAlignmentDelegate = TrySetSelectedImageAlignment;
            DocVm.GetSelectedImageAlignmentDelegate = GetSelectedImageAlignment;
            DocVm.SetImageWrapModeDelegate = SetSelectedImageWrapMode;
            DocVm.SetImageLockAspectDelegate = SetSelectedImageLockAspect;
            DocVm.DeleteSelectedImageDelegate = DeleteSelectedImageFromCanvas;
            DocVm.GetSelectedImageInfoDelegate = GetSelectedImageInfo;
            DocVm.SetImageRotationDelegate = SetSelectedImageRotation;
            DocVm.GetSelectedImageRotationDelegate = GetSelectedImageRotation;
            DocVm.SetImageWidthDelegate = SetSelectedImageWidth;
            DocVm.SetImageHeightDelegate = SetSelectedImageHeight;
            DocVm.SetImageOpacityDelegate = SetSelectedImageOpacity;
            DocVm.SetImageBorderDelegate = SetSelectedImageBorder;
            DocVm.GetSelectedImageStyleDelegate = GetSelectedImageStyle;
            DocVm.ToggleImageFlipHorizontalDelegate = ToggleSelectedImageFlipHorizontal;
            DocVm.ToggleImageFlipVerticalDelegate = ToggleSelectedImageFlipVertical;
            DocVm.SetImageCropModeDelegate = SetSelectedImageCropMode;
            DocVm.GetImageCropModeDelegate = GetSelectedImageCropMode;

            foreach (var pvm in DocVm.Paragraphs)
            {
                UnwirePvm(pvm);
                WirePvm(pvm);
            }
        }

        /// <summary>
        /// Применяет горизонтальное выравнивание к выделенной блок-картинке (Inline).
        /// Возвращает true, если картинка выделена и выравнивание изменено или уже совпадает —
        /// в этом случае команда выравнивания не должна трогать абзац.
        /// </summary>
        private bool TrySetSelectedImageAlignment(
            Writersword.Modules.TextEditor.Models.Styles.TextAlignment alignment)
        {
            if (_selectedImage is null)
                return false;

            // Плавающая картинка позиционируется смещением якоря, выравнивание к ней
            // неприменимо. Команду всё равно поглощаем: иначе она проваливалась в
            // ApplyParaProperty и меняла выравнивание абзаца при выделенной картинке.
            if (_selectedImage.WrapMode != WrapMode.Inline)
                return true;

            if (_selectedImage.Alignment != alignment)
            {
                BeginEdit("Выравнивание изображения");
                _selectedImage.Alignment = alignment;
                CommitEdit();
                RebuildLayouts();
                InvalidateFull();
            }

            // Обновляем риббон: кнопки должны отражать выравнивание картинки и не
            // «залипать» при повторных кликах (ToggleButton с OneWay-биндингом).
            DocVm?.FireCursorContextChanged();
            return true;
        }

        /// <summary>Выравнивание выделенной блок-картинки для отображения в риббоне (или null).</summary>
        private Writersword.Modules.TextEditor.Models.Styles.TextAlignment? GetSelectedImageAlignment()
            => _selectedImage is not null && _selectedImage.WrapMode == WrapMode.Inline
                ? _selectedImage.Alignment
                : (Writersword.Modules.TextEditor.Models.Styles.TextAlignment?)null;

        // Меняет режим обтекания выделенной картинки (команда контекстной вкладки).
        private void SetSelectedImageWrapMode(WrapMode mode)
        {
            if (_selectedImage is null || _selectedImage.WrapMode == mode) return;

            BeginEdit("Обтекание изображения");
            // Переход из блока (Inline) в плавающий режим: фиксируем текущее положение
            // как смещение якоря, чтобы картинка не прыгнула в угол страницы.
            if (_selectedImage.WrapMode == WrapMode.Inline && mode != WrapMode.Inline)
            {
                for (int i = 0; i < _images.Count; i++)
                {
                    var entry = _images[i];
                    if (!ReferenceEquals(entry.Block, _selectedImage)) continue;
                    if (entry.PageIndex >= 0 && entry.PageIndex < _pages.Count)
                    {
                        var pg = _pages[entry.PageIndex];
                        _selectedImage.OffsetXPt = entry.XPt - pg.PadLeftPt - pg.MarginLeftPt;
                        _selectedImage.OffsetYPt = entry.Ypt - pg.Ypt - pg.PadTopPt;
                    }
                    break;
                }
            }
            _selectedImage.WrapMode = mode;
            CommitEdit();
            RebuildLayouts();
            InvalidateFull();
        }

        // Включает/выключает блокировку пропорций выделенной картинки.
        private void SetSelectedImageLockAspect(bool locked)
        {
            if (_selectedImage is null || _selectedImage.LockAspectRatio == locked) return;
            BeginEdit("Пропорции изображения");
            _selectedImage.LockAspectRatio = locked;
            CommitEdit();
            InvalidateFull();
        }

        // Удаляет выделенную картинку (команда контекстной вкладки).
        private void DeleteSelectedImageFromCanvas()
        {
            if (_selectedImage is null) return;
            var img = _selectedImage;
            _selectedImage = null;
            DocVm?.RemoveImage(img);
            ImageSelectionChanged?.Invoke(false);
            InvalidateFull();
        }

        // Текущие параметры выделенной картинки для синхронизации вкладки (или null).
        private (WrapMode Wrap, bool LockAspect, Writersword.Modules.TextEditor.Models.Styles.TextAlignment Align)? GetSelectedImageInfo()
            => _selectedImage is null
                ? null
                : (_selectedImage.WrapMode, _selectedImage.LockAspectRatio, _selectedImage.Alignment);

        // Задаёт ширину выделенной картинки в пунктах. При включённых пропорциях
        // высота масштабируется тем же коэффициентом.
        private void SetSelectedImageWidth(double widthPt)
        {
            if (_selectedImage is null) return;
            double w = Math.Max(widthPt, 4.0);
            if (Math.Abs(_selectedImage.WidthPt - w) < 0.01) return;
            BeginEdit("Размер изображения");
            if (_selectedImage.LockAspectRatio && _selectedImage.WidthPt > 0.0)
                _selectedImage.HeightPt = Math.Max(4.0, _selectedImage.HeightPt * (w / _selectedImage.WidthPt));
            _selectedImage.WidthPt = w;
            CommitEdit();
            RebuildLayouts();
            InvalidateMeasure();
            InvalidateFull();
            ImageSelectionChanged?.Invoke(true);
        }

        // Задаёт высоту выделенной картинки в пунктах. При включённых пропорциях
        // ширина масштабируется тем же коэффициентом.
        private void SetSelectedImageHeight(double heightPt)
        {
            if (_selectedImage is null) return;
            double h = Math.Max(heightPt, 4.0);
            if (Math.Abs(_selectedImage.HeightPt - h) < 0.01) return;
            BeginEdit("Размер изображения");
            if (_selectedImage.LockAspectRatio && _selectedImage.HeightPt > 0.0)
                _selectedImage.WidthPt = Math.Max(4.0, _selectedImage.WidthPt * (h / _selectedImage.HeightPt));
            _selectedImage.HeightPt = h;
            CommitEdit();
            RebuildLayouts();
            InvalidateMeasure();
            InvalidateFull();
            ImageSelectionChanged?.Invoke(true);
        }

        // Задаёт непрозрачность выделенной картинки (0..1).
        private void SetSelectedImageOpacity(double opacity)
        {
            if (_selectedImage is null) return;
            double o = Math.Clamp(opacity, 0.0, 1.0);
            if (Math.Abs(_selectedImage.Opacity - o) < 0.001) return;
            BeginEdit("Прозрачность изображения");
            _selectedImage.Opacity = o;
            CommitEdit();
            InvalidateFull();
        }

        // Задаёт рамку выделенной картинки: цвет в hex и толщину в пунктах.
        // null или полностью прозрачный цвет — рамка убирается.
        private void SetSelectedImageBorder(string? colorHex, double thicknessPt)
        {
            if (_selectedImage is null) return;
            string? color = string.IsNullOrEmpty(colorHex) || colorHex == "#00000000" ? null : colorHex;
            double thick = Math.Clamp(thicknessPt, 0.0, 50.0);
            _logger.Debug("[IMG] border request color={C} thick={T}", color ?? "none", thick);
            if (_selectedImage.BorderColor == color
                && Math.Abs(_selectedImage.BorderThicknessPt - thick) < 0.01) return;
            BeginEdit("Рамка изображения");
            _selectedImage.BorderColor = color;
            _selectedImage.BorderThicknessPt = thick;
            CommitEdit();
            InvalidateFull();
        }

        // Переключает зеркальное отражение выделенной картинки по горизонтали.
        private void ToggleSelectedImageFlipHorizontal()
        {
            if (_selectedImage is null) return;
            BeginEdit("Отражение изображения");
            _selectedImage.FlipHorizontal = !_selectedImage.FlipHorizontal;
            CommitEdit();
            InvalidateFull();
        }

        // Переключает зеркальное отражение выделенной картинки по вертикали.
        private void ToggleSelectedImageFlipVertical()
        {
            if (_selectedImage is null) return;
            BeginEdit("Отражение изображения");
            _selectedImage.FlipVertical = !_selectedImage.FlipVertical;
            CommitEdit();
            InvalidateFull();
        }

        // Включает/выключает режим обрезки выделенной картинки.
        private void SetSelectedImageCropMode(bool on)
        {
            bool next = on && _selectedImage is not null;
            if (_imageCropMode == next) return;
            _imageCropMode = next;
            InvalidateFull();
        }

        // Текущее состояние режима обрезки для синхронизации вкладки.
        private bool GetSelectedImageCropMode() => _imageCropMode;

        // Геометрия и оформление выделенной картинки для полей вкладки (или null).
        private (double WidthPt, double HeightPt, double Opacity, string? BorderColor, double BorderThicknessPt)? GetSelectedImageStyle()
            => _selectedImage is null
                ? null
                : (_selectedImage.WidthPt, _selectedImage.HeightPt, _selectedImage.Opacity,
                   _selectedImage.BorderColor, _selectedImage.BorderThicknessPt);

        // Задаёт угол поворота выделенной картинки (команда контекстной вкладки).
        private void SetSelectedImageRotation(double degrees)
        {
            if (_selectedImage is null) return;
            double normalized = ((degrees % 360.0) + 360.0) % 360.0;
            if (Math.Abs(_selectedImage.RotationDeg - normalized) < 0.01) return;
            BeginEdit("Поворот изображения");
            _selectedImage.RotationDeg = normalized;
            CommitEdit();
            RebuildLayouts();
            InvalidateMeasure();
            InvalidateFull();
        }

        // Текущий угол поворота выделенной картинки для вкладки (или null).
        private double? GetSelectedImageRotation()
            => _selectedImage?.RotationDeg;

        // Структурное изменение (вставка/удаление картинки и т.п.): пересобираем раскладку
        // БЕЗ очистки кэша абзацев — текст абзацев не менялся, переформировывать их не нужно,
        // поэтому операция быстрая даже на большом документе.
        private void OnStructureChanged()
        {
            RebuildLayouts();
            _caretLineHint = -1;
            SnapCaretToCorrectSlice();
            InvalidateFull();
        }

        private void OnParagraphFormatChanged()
        {
            // При изменении форматирования (шрифт, размер, цвет и т.п.) текст параграфа
            // не меняется, поэтому _layoutCache не инвалидируется автоматически — чистим явно.
            // Берём список затронутых абзацев: если он есть — инвалидируем кэш ТОЛЬКО у них,
            // а раскладки остальных (на больших документах — тысячи) остаются валидными и
            // переиспользуются при RebuildLayouts. Это убирает полный пересбор всего документа
            // через Skia на каждый коммит форматирования.
            var affected = DocVm?.TakeLastFormatAffected();
            if (affected is { Count: > 0 })
            {
                foreach (var pvm in affected)
                    _layoutCache.Remove(pvm);
            }
            else
            {
                // Затронутые неизвестны (например, форматирование ячейки) — полный сброс.
                _layoutCache.Clear();
                InvalidateCellLayoutCaches();
            }

            RebuildLayouts();
            // Подсказка строки каретки могла устареть: при смене форматирования (шрифт,
            // размер) абзац перетекает по строкам иначе. Сбрасываем, иначе DrawCaret
            // нарисует каретку на старой строке.
            _caretLineHint = -1;
            SnapCaretToCorrectSlice();
            UpdatePreferredX();

            // Если каретка в таблице — обновляем маркеры линейки.
            // Без этого после смены LeftIndentPt или ширины колонки линейка
            // показывает старые позиции и следующий drag считается от них.
            if (_activeTableBlock is not null)
                NotifyCaretEnteredTableCallback();

            InvalidateFull();
        }

        private double _lastZoom = 1.0;

        // На больших документах RebuildLayouts (пагинация всех абзацев) слишком тяжёл, чтобы
        // гонять его на каждый шаг зума. Во время жеста масштабирования рендерим уже посчитанную
        // раскладку, лишь масштабируя её, а полный пересчёт делаем один раз после остановки
        // (debounce). _zooming на это время заставляет Measure/Arrange пропускать RebuildLayouts.
        // Флаг гарантированно сбрасывается таймером и принудительно на любом вводе (см.
        // FinishZoomImmediately), поэтому залипнуть и заблокировать отрисовку/undo не может.
        private bool _zooming;
        private DispatcherTimer? _zoomSettleTimer;

        private void OnDocVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // Изменение масштаба. Ключевое: НЕ чистим кэш раскладки. В режиме страниц ширина
            // текста от зума не зависит (она равна ширине страницы), поэтому кэш абзацев валиден,
            // и RebuildLayouts только пере-позиционирует страницы (центрирование pageXPt) и берёт
            // абзацы из кэша — это дёшево. Чистка кэша заставляла бы перелейаутить все абзацы на
            // каждый шаг зума (фриз на больших документах). Текст рисуется векторно с масштабом,
            // поэтому пере-растеризация не нужна.
            if (e.PropertyName == nameof(DocumentViewModel.Zoom))
            {
                // Скролл при зуме НЕ трогаем. Горизонтально лист центрирует рендер (сдвиг по
                // живому _canvasWidth). Вертикально — контент просто масштабируется от текущей
                // прокрутки. Раньше тут синхронно ставилось вертикальное смещение, но ScrollViewer
                // ещё не знал новую высоту контента (она обновляется в Measure следующим кадром),
                // поэтому на увеличении смещение обрезалось по старой высоте: на один кадр текст
                // прыгал вниз и сверху мелькала пустота. Без этой привязки мерцания нет.
                _lastZoom = Zoom;

                // Тяжёлый RebuildLayouts (пагинация всех абзацев) откладываем: во время жеста
                // Measure/Arrange его пропускают (флаг _zooming), рендерится посчитанное ранее,
                // масштабированное под новый зум. Полный пересчёт — после остановки.
                _zooming = true;
                InvalidateMeasure();
                InvalidateVisual();
                ScheduleZoomSettle();
                return;
            }

            if (e.PropertyName is nameof(DocumentViewModel.ViewMode)
                               or nameof(DocumentViewModel.PageSettings))
            {
                if (DocVm is not null)
                    _styleResolver = new StyleResolver(DocVm.Document.Styles, _scriptFontMap);
                _layoutCache.Clear();
                InvalidateCellLayoutCaches();
                RebuildLayouts();

                _lastZoom = Zoom;
                InvalidateMeasure();
                InvalidateFull();
            }
        }

        private void ScheduleZoomSettle()
        {
            if (_zoomSettleTimer is null)
            {
                _zoomSettleTimer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(140)
                };
                _zoomSettleTimer.Tick += (_, _) => FinishZoomImmediately();
            }
            _zoomSettleTimer.Stop();
            _zoomSettleTimer.Start();
        }

        private void FinishZoomImmediately()
        {
            _zoomSettleTimer?.Stop();
            if (!_zooming) return;
            _zooming = false;
            RebuildLayouts();
            InvalidateMeasure();
            InvalidateFull();
        }

        private void OnParagraphsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems is not null)
                foreach (ParagraphViewModel pvm in e.NewItems) WirePvm(pvm);

            if (e.OldItems is not null)
                foreach (ParagraphViewModel pvm in e.OldItems)
                {
                    UnwirePvm(pvm);
                    _layoutCache.Remove(pvm);
                }

            int dirtyIdx = 0;
            if (e.NewItems is not null && e.NewStartingIndex >= 0)
                dirtyIdx = e.NewStartingIndex;
            else if (e.OldItems is not null && e.OldStartingIndex >= 0)
                dirtyIdx = Math.Max(0, e.OldStartingIndex - 1);

            ScheduleRebuild(dirtyIdx);
        }

        private void WirePvm(ParagraphViewModel pvm)
        {
            pvm.PropertyChanged += OnPvmPropertyChanged;

            // Сохраняем лямбду чтобы точно отписать в UnwirePvm.
            // Анонимную лямбду нельзя отписать через -= без сохранённой ссылки.
            Action handler = () => OnPvmFocusRequested(pvm);
            _pvmFocusHandlers[pvm] = handler;
            pvm.FocusRequested += handler;

            pvm.RequestFocusAtPosition = pos => OnPvmRequestFocusAtPosition(pvm, pos);
        }

        private void OnPvmFocusRequested(ParagraphViewModel pvm)
        {
            if (DocVm is null) return;
            int idx = DocVm.Paragraphs.IndexOf(pvm);
            if (idx < 0) return;
            _caretPara = FindFirstSliceForDocVmParagraph(idx);
            _caretChar = pvm.PlainText?.Length ?? 0;
            NotifyLeftCell();
            SnapCaretToCorrectSlice();
            UpdatePreferredX();
            SyncSel(); ResetCaret(); InvalidateVisual();
        }

        private void OnPvmRequestFocusAtPosition(ParagraphViewModel pvm, int pos)
        {
            if (DocVm is null) return;
            int idx = DocVm.Paragraphs.IndexOf(pvm);
            if (idx < 0) return;
            _caretPara = FindFirstSliceForDocVmParagraph(idx);
            _caretChar = Clamp(pos, 0, pvm.PlainText?.Length ?? 0);
            NotifyLeftCell();
            SnapCaretToCorrectSlice();
            UpdatePreferredX();
            SyncSel(); ResetCaret(); InvalidateVisual();
        }

        private void UnwirePvm(ParagraphViewModel pvm)
        {
            pvm.PropertyChanged -= OnPvmPropertyChanged;

            if (_pvmFocusHandlers.TryGetValue(pvm, out var focusHandler))
            {
                pvm.FocusRequested -= focusHandler;
                _pvmFocusHandlers.Remove(pvm);
            }

            pvm.RequestFocusAtPosition = null;
        }

        private void OnPvmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ParagraphViewModel.PlainText)) return;
            if (sender is ParagraphViewModel pvm && DocVm is not null)
            {
                int idx = DocVm.Paragraphs.IndexOf(pvm);
                if (idx >= 0) { ScheduleRebuild(idx); return; }
            }
            ScheduleRebuild(0);
        }

        // ── Дебаунс пересчёта ─────────────────────────────────────────────
        private void ScheduleRebuild(int dirtyParaIdx)
        {
            if (DocVm?.IsBulkRebuilding != true)
            {
                ParagraphViewModel? dirtyPvm = null;
                if (DocVm is not null && dirtyParaIdx >= 0 && dirtyParaIdx < DocVm.Paragraphs.Count)
                {
                    dirtyPvm = DocVm.Paragraphs[dirtyParaIdx];
                    _layoutCache.Remove(dirtyPvm);

                    int sliceCount = _layouts.Count(l => l.Vm == dirtyPvm && l.Cell is null);
                    if (sliceCount == 1)
                    {
                        // Быстрый путь для редактирования: обновляем только один параграф.
                        QuickUpdateParagraphLayout(dirtyPvm);
                    }
                    else if (sliceCount == 0)
                    {
                        // Новый параграф (Enter): вставляем в _layouts с оценочной высотой
                        // чтобы ScrollToCaret мог найти его позицию немедленно.
                        QuickInsertParagraphLayout(dirtyParaIdx, dirtyPvm);
                    }
                    // sliceCount > 1: пропускаем быстрый путь, полный пересчёт ниже.
                }
            }

            var oldCts = _rebuildCts;
            _rebuildCts = new System.Threading.CancellationTokenSource();
            oldCts.Cancel();
            oldCts.Dispose();
            var cts = _rebuildCts;

            InvalidateFull();

            Dispatcher.UIThread.Post(() =>
            {
                if (cts.IsCancellationRequested) return;

                double oldCanvasH = _canvasHeight;
                RebuildLayouts();
                SnapCaretToCorrectSlice(); 

                if (_pendingFocusBlock is not null && DocVm is not null)
                {
                    var anchorVm = DocVm.Paragraphs.FirstOrDefault(p => p.Model == _pendingFocusBlock);
                    _pendingFocusBlock = null;
                    if (anchorVm is not null)
                    {
                        int pvmIdx = DocVm.Paragraphs.IndexOf(anchorVm);
                        _caretPara = FindFirstSliceForDocVmParagraph(pvmIdx);
                        _caretChar = 0;
                        NotifyLeftCell();
                        SnapCaretToCorrectSlice();
                        UpdatePreferredX();
                        SyncSel();
                        _caretVisible = true;
                        _caretTimer.Stop();
                        _caretTimer.Start();
                        if (_caretPara >= 0 && _caretPara < _layouts.Count)
                            CaretPageChanged?.Invoke(_layouts[_caretPara].PageIndex);
                        ScrollToCenterCaret();
                    }
                }

                if (Math.Abs(_canvasHeight - oldCanvasH) > 0.5)
                    InvalidateMeasure();
                else
                    InvalidateFull();

                // После полного rebuild _layouts актуален — прокручиваем к каретке
                // Нужно при Enter: ResetCaret вызывается до rebuild, каретка вне _layouts
                ScrollToCaret();

            }, DispatcherPriority.Background);
        }

        // ── Measure / Layout ──────────────────────────────────────────────
        protected override Size MeasureOverride(Size available)
        {
            double zoom = Zoom;
            double availW = double.IsInfinity(available.Width) ? 800 : Math.Max(available.Width, 1);
            double viewportW = _parentScrollViewer?.Viewport.Width > 0
                ? _parentScrollViewer.Viewport.Width : availW;
            _canvasWidth = Math.Max(viewportW / zoom, 1);

            if (_styleResolver is null && DocVm is not null)
                _styleResolver = new StyleResolver(DocVm.Document.Styles, _scriptFontMap);

            // Пересчёт раскладки выполняется только если отпечаток не совпал:
            // measure вызывается при каждом переприкреплении вьюхи (переключение
            // вкладок и воркмодов) и после каждого InvalidateMeasure, а полный проход
            // пагинации большого документа занимает секунды даже с тёплым кешем
            // лейаутов. Все содержательные изменения (ввод, форматирование, таблицы,
            // смена документа, ширины, режима, стилей) идут через прямые вызовы
            // RebuildLayouts либо меняют поля отпечатка — пропуск безопасен.
            if (!_zooming && !LayoutsMatchCurrentState())
            {
                // Холодный кеш большого документа: синхронный пересчёт зашейпил бы
                // тысячи абзацев и заблокировал UI-поток на секунды. Вместо этого
                // запускается порционный прогрев кеша (PumpLayoutWarmup): абзацы
                // шейпятся с бюджетом времени на проход диспетчера, UI остаётся
                // отзывчивым, а полный пересчёт выполняется после прогрева по
                // InvalidateMeasure — уже с тёплым кешем, за десятки миллисекунд.
                if (ShouldWarmupBeforeRebuild())
                    StartLayoutWarmup();
                else
                    RebuildLayouts();
            }

            double visualH = Math.Max(_canvasHeight * zoom, 100);
            double visualW = availW;

            if (DocVm?.ViewMode == EditorViewMode.Page)
                visualW = Math.Max(availW,
                    GetPageWidthPt() * PtToPx * zoom + PageGapPt * PtToPx * 4);

            return new Size(visualW, visualH);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double zoom = Zoom;
            double viewportW = _parentScrollViewer?.Viewport.Width > 0
                ? _parentScrollViewer.Viewport.Width : finalSize.Width;
            double logicalW = Math.Max(viewportW / zoom, 1);

            // При изменении ширины канваса обновляем _canvasWidth. В режиме страниц это влияет
            // только на центрирование страниц, в режиме потока — на ширину текста (см. ниже).
            // Во время зум-жеста пересчёт пропускаем — его сделает FinishZoomImmediately.
            if (!_zooming && Math.Abs(logicalW - _canvasWidth) > 0.5)
            {
                _canvasWidth = logicalW;
                // В режиме страниц ширина текста равна ширине страницы и от logicalW (а значит и
                // от зума) не зависит — кэш абзацев валиден, чистить его не нужно, иначе на зуме
                // перелейаутился бы весь документ. RebuildLayouts только пере-центрирует страницы.
                // В режиме потока ширина текста = logicalW, поэтому при её изменении нужен рефлоу.
                if (DocVm?.ViewMode != EditorViewMode.Page)
                {
                    _layoutCache.Clear();
                    InvalidateCellLayoutCaches();
                }
                RebuildLayouts();
            }

            return new Size(finalSize.Width, Math.Max(_canvasHeight * zoom, 100));
        }

        // ── Пересчёт лейаута ──────────────────────────────────────────────

        // Отпечаток состояния, для которого построены _layouts/_pages/_tables.
        // Обновляется в конце RebuildLayouts; MeasureOverride сравнивает его с текущим
        // состоянием и пропускает полный пересчёт при совпадении. Смена документа
        // создаёт новый DocumentViewModel (LoadDocument), смена карты шрифтов — новый
        // StyleResolver (сеттер ScriptFontMap), поэтому оба случая ловятся сравнением
        // ссылок. Ширина и режим отображения сравниваются по значению.
        private object? _layoutsFingerprintDocVm;
        private object? _layoutsFingerprintParagraphs;
        private object? _layoutsFingerprintStyleResolver;
        private int _layoutsFingerprintParagraphCount = -1;
        private double _layoutsFingerprintWidth = double.NaN;
        private EditorViewMode _layoutsFingerprintViewMode = (EditorViewMode)(-1);

        /// <summary>
        /// Возвращает true если текущая раскладка (_layouts/_pages/_tables) построена
        /// ровно для текущего состояния канваса и полный пересчёт в measure не нужен.
        /// </summary>
        private bool LayoutsMatchCurrentState()
        {
            if (DocVm is null) return false;

            return _layouts.Count > 0
                && ReferenceEquals(_layoutsFingerprintDocVm, DocVm)
                && ReferenceEquals(_layoutsFingerprintParagraphs, DocVm.Paragraphs)
                && _layoutsFingerprintParagraphCount == DocVm.Paragraphs.Count
                && ReferenceEquals(_layoutsFingerprintStyleResolver, _styleResolver)
                && !double.IsNaN(_layoutsFingerprintWidth)
                && Math.Abs(_layoutsFingerprintWidth - _canvasWidth) < 0.5
                && _layoutsFingerprintViewMode == DocVm.ViewMode;
        }

        // ── Порционный прогрев кеша раскладки ─────────────────────────────
        // Холодное построение раскладки большого документа (шейпинг тысяч абзацев
        // через Skia) блокировало UI-поток на секунды при первом открытии модуля
        // в воркмоде. Прогрев шейпит абзацы порциями с бюджетом времени на проход
        // диспетчера: между проходами UI обрабатывает ввод и рендер, а полный
        // пересчёт раскладки выполняется один раз после прогрева с тёплым кешем.
        // Работа целиком на UI-потоке — гонок с вводом и моделью нет по построению.
        private bool _layoutWarmupActive;
        private const int WarmupColdThreshold = 200;
        private const int WarmupPassBudgetMs = 30;

        // Глобальный счётчик активных прогревов. Читается главным окном:
        // снапшот-оверлей вкладки (мгновенное переключение как в Chrome)
        // держится на экране, пока хоть один канвас прогревает раскладку —
        // иначе оверлей скрылся бы поверх ещё пустого канваса.
        private static int _activeWarmupCount;
        public static int ActiveWarmupCount => System.Threading.Volatile.Read(ref _activeWarmupCount);

        /// <summary>
        /// Единственная точка изменения флага прогрева — поддерживает глобальный
        /// счётчик сбалансированным при любых путях завершения (финиш, detach,
        /// потеря документа).
        /// </summary>
        private void SetWarmupActive(bool active)
        {
            if (_layoutWarmupActive == active) return;
            _layoutWarmupActive = active;
            if (active)
                System.Threading.Interlocked.Increment(ref _activeWarmupCount);
            else
                System.Threading.Interlocked.Decrement(ref _activeWarmupCount);
        }

        /// <summary>
        /// Актуальна ли кеш-запись раскладки абзаца для текущей ширины текста.
        /// Условие идентично проверке в GetOrBuildLayout: несовпадение текста или
        /// ширины означает, что абзац будет перешейплен заново.
        /// </summary>
        private bool IsLayoutCacheEntryValid(ParagraphViewModel pvm, float widthPt)
        {
            return _layoutCache.TryGetValue(pvm, out var cached)
                && cached.Text == (pvm.PlainText ?? string.Empty)
                && Math.Abs(cached.Width - widthPt) < 0.1f;
        }

        /// <summary>
        /// Возвращает true если раскладку нужно строить через прогрев: документ большой
        /// и значительная часть абзацев ещё не зашейплена (холодный кеш) либо их кеш
        /// устарел (другая ширина текста). Для тёплого кеша и маленьких документов
        /// синхронный пересчёт занимает миллисекунды и прогрев не нужен.
        /// </summary>
        private bool ShouldWarmupBeforeRebuild()
        {
            if (_layoutWarmupActive) return true;
            if (DocVm is null) return false;

            var paragraphs = DocVm.Paragraphs;
            if (paragraphs.Count < WarmupColdThreshold) return false;

            float widthPt = GetCurrentTextWidthPt();
            int uncached = 0;
            foreach (var pvm in paragraphs)
            {
                if (!IsLayoutCacheEntryValid(pvm, widthPt))
                {
                    uncached++;
                    if (uncached >= WarmupColdThreshold)
                        return true;
                }
            }
            return false;
        }

        private void StartLayoutWarmup()
        {
            if (_layoutWarmupActive) return;
            SetWarmupActive(true);
            // Один раз перед прогревом выставляем тексты маркеров списков (и чиним битые позиции),
            // чтобы раскладка учла ширину цифры уже в кэше. Раньше это делалось каждый проход.
            ApplyListMarkerTexts();
            _logger.Debug("Layout warmup started: {Count} paragraphs, cache={CacheCount}",
                DocVm?.Paragraphs.Count ?? 0, _layoutCache.Count);
            Dispatcher.UIThread.Post(PumpLayoutWarmup, DispatcherPriority.Loaded);
        }

        /// <summary>
        /// Один проход прогрева: шейпит незакешированные абзацы пока не исчерпан
        /// бюджет времени, затем перепланирует себя. Когда все абзацы зашейплены —
        /// вызывает InvalidateMeasure: пересчёт раскладки в measure пройдёт быстро,
        /// целиком из кеша. Приоритет Loaded — выше Background, проход не голодает
        /// при непрерывных layout-инвалидациях.
        /// </summary>
        // Вычисляет тексты маркеров списков (нумерацию) и кладёт их в модель, чтобы раскладка
        // могла измерить ширину цифры. Вызывается перед прогревом кэша, а также при полном
        // пересборе тексты выставляются в RebuildPageMode/FlowMode.
        private void ApplyListMarkerTexts()
        {
            if (DocVm is null) return;
            double textWidthPt = GetCurrentTextWidthPt();
            foreach (var section in DocVm.Document.Sections)
            {
                var map = Rendering.ListNumberingEngine.Compute(section.Blocks);
                foreach (var block in section.Blocks)
                    if (block is ParagraphBlock p && p.ListProperties is not null)
                    {
                        p.ListProperties.ComputedMarkerText =
                            map.TryGetValue(p, out var mi) ? mi.Text : null;
                        MigrateCorruptListMarker(p, textWidthPt);
                    }
            }
        }

        // Сбрасывает явно повреждённую позицию номера (левый край цифры у/за правым краем
        // текстовой зоны — след старых багов), чтобы номер вернулся к нормальному выступу слева.
        private static void MigrateCorruptListMarker(ParagraphBlock p, double textWidthPt)
        {
            if (p.ListProperties?.MarkerIndentPt is double mi && mi > textWidthPt - 20.0)
                p.ListProperties.MarkerIndentPt = null;
        }

        private void PumpLayoutWarmup()
        {
            if (!_layoutWarmupActive) return;

            if (DocVm is null)
            {
                SetWarmupActive(false);
                return;
            }

            if (_styleResolver is null)
                _styleResolver = new StyleResolver(DocVm.Document.Styles, _scriptFontMap);

            float widthPt = GetCurrentTextWidthPt();
            var passStopwatch = System.Diagnostics.Stopwatch.StartNew();
            bool allShaped = true;

            var paragraphs = DocVm.Paragraphs;
            for (int i = 0; i < paragraphs.Count; i++)
            {
                var pvm = paragraphs[i];

                // Проверка идентична GetOrBuildLayout: запись с устаревшим текстом
                // или другой шириной будет перешейплена — такой абзац не пропускаем,
                // иначе вся перевёрстка свалилась бы в финальный синхронный проход.
                if (IsLayoutCacheEntryValid(pvm, widthPt)) continue;

                GetOrBuildLayout(pvm, widthPt);

                if (passStopwatch.ElapsedMilliseconds >= WarmupPassBudgetMs)
                {
                    allShaped = false;
                    break;
                }
            }

            if (!allShaped)
            {
                Dispatcher.UIThread.Post(PumpLayoutWarmup, DispatcherPriority.Loaded);
                return;
            }

            SetWarmupActive(false);
            _logger.Debug("Layout warmup finished: cache={CacheCount} — scheduling rebuild", _layoutCache.Count);

            // Пересчёт через measure: раскладка соберётся из тёплого кеша.
            InvalidateMeasure();
            InvalidateFull();
        }

        private void RebuildLayouts()
        {
            if (DocVm is null)
            {
                float emptyH = FallbackLinePt * 5f;
                lock (_renderLock)
                {
                    _layouts = new List<ParaLayout>();
                    _pages = new List<PageRect>();
                    _tables = new List<TableEntry>();
                    _canvasHeightPt = emptyH;
                    _canvasHeight = emptyH * PtToPx;
                }

                // Раскладка пуста — отпечаток недействителен.
                _layoutsFingerprintDocVm = null;
                _layoutsFingerprintParagraphs = null;
                _layoutsFingerprintStyleResolver = null;
                _layoutsFingerprintParagraphCount = -1;
                _layoutsFingerprintWidth = double.NaN;
                _layoutsFingerprintViewMode = (EditorViewMode)(-1);
                return;
            }

            if (_styleResolver is null)
                _styleResolver = new StyleResolver(DocVm.Document.Styles, _scriptFontMap);

            // Ворота холодного пересчёта для ПРЯМЫХ вызовов (смена зума, структуры,
            // подписок во время загрузки документа): при холодном кеше полный проход
            // зашейпил бы тысячи абзацев синхронно на UI-потоке (~секунда), в обход
            // прогрева. Вместо этого запускается/продолжается порционный прогрев —
            // по завершении он сам запланирует пересчёт через InvalidateMeasure.
            if (ShouldWarmupBeforeRebuild())
            {
                StartLayoutWarmup();
                return;
            }

            // Диагностика провисаний UI-потока: полный пересчёт раскладки выполняется
            // синхронно (MeasureOverride/ScheduleRebuild), и на больших документах при
            // холодном кеше лейаутов это главный кандидат на заморозку интерфейса.
            // Замер пишется в лог только когда пересчёт превысил порог.
            var rebuildStopwatch = System.Diagnostics.Stopwatch.StartNew();

            switch (DocVm.ViewMode)
            {
                case EditorViewMode.Page:
                    RebuildPageMode();
                    break;
                case EditorViewMode.Draft:
                case EditorViewMode.Web:
                    RebuildFlowMode((float)(_canvasWidth * PxToPt), DraftPadHPt, DraftPadWPt);
                    break;
                case EditorViewMode.Reading:
                    {
                        float cw = (float)(_canvasWidth * PxToPt);
                        RebuildFlowMode(Math.Min(cw, ReadingMaxPt), 18f,
                            (cw - Math.Min(cw, ReadingMaxPt)) / 2f);
                        break;
                    }
            }

            rebuildStopwatch.Stop();
            if (rebuildStopwatch.ElapsedMilliseconds > 50)
            {
                _logger.Warning(
                    "RebuildLayouts took {ElapsedMs}ms on UI thread: mode={Mode}, paragraphs={ParaCount}, layoutCache={CacheCount}",
                    rebuildStopwatch.ElapsedMilliseconds,
                    DocVm.ViewMode,
                    DocVm.Paragraphs.Count,
                    _layoutCache.Count);
            }

            // Фиксируем отпечаток состояния, для которого построена раскладка —
            // последующие measure-проходы с тем же состоянием пропустят пересчёт.
            _layoutsFingerprintDocVm = DocVm;
            _layoutsFingerprintParagraphs = DocVm.Paragraphs;
            _layoutsFingerprintStyleResolver = _styleResolver;
            _layoutsFingerprintParagraphCount = DocVm.Paragraphs.Count;
            _layoutsFingerprintWidth = _canvasWidth;
            _layoutsFingerprintViewMode = DocVm.ViewMode;
        }


        /// <summary>
        /// Возвращает layout для рендера параграфа.
        /// Во время live-preview оверлейный _layouts уже содержит preview-layout в pl.Layout.
        /// </summary>
        private SKTextLayout GetRenderLayout(ParaLayout pl, float widthPt)
        {
            // Во время live-preview оверлейный _layouts уже содержит preview-layout
            // прямо в pl.Layout (см. DocumentCanvas.FontPreview.cs), отдельная ветка не нужна.
            return pl.Layout ?? GetOrBuildLayout(pl.Vm, widthPt);
        }

        /// <summary>
        /// Быстрая оценка высоты параграфа без построения SKTextLayout.
        /// Используется для параграфов вне viewport-буфера — точность ~±30%,
        /// достаточная для позиционирования скроллбара и прокрутки.
        /// </summary>
        private float EstimateHeight(ParagraphViewModel pvm, float widthPt)
        {
            int charCount = pvm.PlainText?.Length ?? 0;
            if (charCount == 0) return FallbackLinePt;
            const float AvgCharWidthPt = 5.5f;
            float charsPerLine = Math.Max(widthPt / AvgCharWidthPt, 1f);
            float lines = MathF.Ceiling(charCount / charsPerLine) + 0.5f;
            return MathF.Max(lines * FallbackLinePt, FallbackLinePt);
        }

        private SKTextLayout GetOrBuildLayout(ParagraphViewModel pvm, float widthPt)
        {
            string text = pvm.PlainText ?? string.Empty;
            if (_layoutCache.TryGetValue(pvm, out var cached)
                && cached.Text == text
                && Math.Abs(cached.Width - widthPt) < 0.1f)
                return cached.Layout;
            var layout = _renderer.BuildLayout(pvm.Model, widthPt, _styleResolver!);
            _layoutCache[pvm] = (text, widthPt, layout);
            return layout;
        }

        // Отступ текста от габарита обтекаемого объекта, в пунктах.
        private const float WrapZoneMarginPt = 6f;

        /// <summary>
        /// Зоны обтекания для параграфа с верхом paraTopPt: габариты плавающих
        /// картинок в режимах Square/Tight (AABB с учётом поворота, с полями),
        /// переведённые в координаты текстовой области параграфа.
        /// null — обтекаемых объектов рядом нет.
        /// </summary>
        private List<SKWrapZone>? ComputeWrapZones(
            List<ImageEntry> images, float paraTopPt, float textXPt, float textWidthPt)
        {
            List<SKWrapZone>? zones = null;

            foreach (var ie in images)
            {
                var wm = ie.Block.WrapMode;
                if (wm != WrapMode.Square && wm != WrapMode.Tight) continue;

                double rad = ie.Block.RotationDeg * Math.PI / 180.0;
                float absCos = (float)Math.Abs(Math.Cos(rad));
                float absSin = (float)Math.Abs(Math.Sin(rad));
                float boxW = ie.WidthPt * absCos + ie.HeightPt * absSin;
                float boxH = ie.WidthPt * absSin + ie.HeightPt * absCos;
                float cx = ie.XPt + ie.WidthPt / 2f;
                float cy = ie.Ypt + ie.HeightPt / 2f;

                float top = cy - boxH / 2f - WrapZoneMarginPt;
                float bottom = cy + boxH / 2f + WrapZoneMarginPt;
                float left = cx - boxW / 2f - WrapZoneMarginPt - textXPt;
                float right = cx + boxW / 2f + WrapZoneMarginPt - textXPt;

                // Зона целиком выше параграфа или слишком далеко ниже — не влияет.
                if (bottom <= paraTopPt) continue;
                if (top >= paraTopPt + 3000f) continue;
                // Зона вне текстовой колонки по горизонтали — не влияет.
                if (right <= 0f || left >= textWidthPt) continue;

                left = Math.Max(left, 0f);
                right = Math.Min(right, textWidthPt);

                zones ??= new List<SKWrapZone>();
                zones.Add(new SKWrapZone(top - paraTopPt, bottom - paraTopPt, left, right));
            }

            return zones;
        }

        /// <summary>
        /// Раскладка параграфа с зонами обтекания. Кеш не используется: зоны зависят
        /// от позиций плавающих объектов, а ключ кеша (текст, ширина) их не учитывает.
        /// </summary>
        private SKTextLayout BuildWrappedLayout(
            ParagraphViewModel pvm, float widthPt, IReadOnlyList<SKWrapZone> zones)
            => _renderer.BuildLayout(pvm.Model, widthPt, _styleResolver!, isCell: false, wrapZones: zones);

        // ── ICustomDrawOperation ──────────────────────────────────────────
        private sealed class CanvasSKDrawOperation : ICustomDrawOperation
        {
            private readonly DocumentCanvas _canvas;
            public Rect Bounds { get; }

            public CanvasSKDrawOperation(DocumentCanvas canvas, Rect bounds)
            {
                _canvas = canvas;
                Bounds = bounds;
            }

            public void Dispose() { }
            public bool Equals(ICustomDrawOperation? other) => false;
            public bool HitTest(Point p) => true;

            public void Render(ImmediateDrawingContext context)
            {
                var feature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature))
                    as ISkiaSharpApiLeaseFeature;
                if (feature is null) return;
                using var lease = feature.Lease();
                _canvas.RenderWithSKCanvas(lease.SkCanvas);
            }
        }
    }
}