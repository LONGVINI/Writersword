using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Writersword.Infrastructure.Behaviours;
using Writersword.Infrastructure.Controls;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Services;
using Writersword.Styles.UserControls;
using VerticalAlignment = Avalonia.Layout.VerticalAlignment;

namespace Writersword.Modules.TextEditor.Views.Dialogs
{
    /// <summary>Что открыть в окне диаграммы сразу после показа.</summary>
    public enum ChartEditorStartAction
    {
        None,

        /// <summary>Окошко своих наборов цветов.</summary>
        ColorSets,

        /// <summary>Новый свой набор из цветов диаграммы и его настройка.</summary>
        NewColorSet
    }

    /// <summary>
    /// Окно диаграммы — оверлей модуля, как «Эффекты текста»: затемняет редактор и
    /// стоит над ним, под редактором цвета, чтобы кнопки цвета открывали полный
    /// подборщик поверх окна.
    ///
    /// Слева — виды диаграмм значками, в середине — образец (та же отрисовка, что на
    /// листе) и таблица данных, устроенная как лист Excel, справа — настройки
    /// карточками: подписи, оформление, ряды, оси.
    ///
    /// У окна своя история: Ctrl+Z и Ctrl+Y (и кнопки в заголовке) отменяют и
    /// возвращают правки таблицы и настроек по одной. В документ диаграмма уходит
    /// целиком по «Готово», одним шагом отмены документа. Окно без правок ничего в
    /// документе не меняет.
    /// </summary>
    public sealed class ChartEditorOverlay : UserControl
    {
        // ── Поиск окна ───────────────────────────────────────────────────

        private static readonly List<WeakReference<ChartEditorOverlay>> Hosts = new();

        /// <summary>
        /// Что открыть в окне сразу после показа — для кнопок ленты, ведущих в окно:
        /// «Новый набор» и «Настроить наборы» в меню цветов. Сбрасывается при показе.
        /// </summary>
        public static ChartEditorStartAction PendingStart { get; set; }

        /// <summary>
        /// Окно диаграммы для показа: то, что стоит в активном окне приложения в
        /// видимом редакторе. Окон может быть несколько — по одному на редактор, и
        /// в плавающих окнах тоже.
        /// </summary>
        public static ChartEditorOverlay? FindHost()
        {
            var alive = new List<ChartEditorOverlay>();
            for (int i = Hosts.Count - 1; i >= 0; i--)
            {
                if (Hosts[i].TryGetTarget(out var host)) alive.Add(host);
                else Hosts.RemoveAt(i);
            }

            ChartEditorOverlay? best = null;
            int bestScore = -1;
            foreach (var host in alive)
            {
                if (TopLevel.GetTopLevel(host) is not { } top) continue;
                int score = 0;
                if (top is Window { IsActive: true }) score += 2;
                if (host.Parent is Visual parent && parent.IsEffectivelyVisible) score += 1;
                if (score > bestScore)
                {
                    best = host;
                    bestScore = score;
                }
            }
            return best;
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            Hosts.Add(new WeakReference<ChartEditorOverlay>(this));
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            Hosts.RemoveAll(w => !w.TryGetTarget(out var h) || ReferenceEquals(h, this));
            if (_pending is not null) Finish(null);
            base.OnDetachedFromVisualTree(e);
        }

        // ── Справочники ──────────────────────────────────────────────────

        /// <summary>Пункт галереи видов: вид, комбинированная ли, подпись.</summary>
        private sealed record KindEntry(ChartKind Kind, bool Combo, string Label, string Tip);

        private static readonly KindEntry[] KindEntries =
        {
            new(ChartKind.Column, false, "Гистограмма", "Вертикальные столбцы: сравнить величины по категориям"),
            new(ChartKind.Bar, false, "Линейчатая", "Горизонтальные полосы: удобно при длинных подписях"),
            new(ChartKind.Line, false, "График", "Линии: изменение во времени"),
            new(ChartKind.Area, false, "С областями", "Закрашенные области: изменение и вклад в сумму"),
            new(ChartKind.Column, true, "Комбинированная", "Столбцы и линия по своей оси справа — разные величины на одной диаграмме"),
            new(ChartKind.Pie, false, "Круговая", "Доли целого: показывает первый ряд"),
            new(ChartKind.Doughnut, false, "Кольцевая", "Доли целого кольцами: по кольцу на ряд"),
            new(ChartKind.Scatter, false, "Точечная", "Точки по двум числовым осям X и Y"),
            new(ChartKind.Bubble, false, "Пузырьковая", "Точки X и Y с размером пузырька"),
            new(ChartKind.Radar, false, "Лепестковая", "Ряды по лучам из центра"),
            new(ChartKind.Stock, false, "Биржевая", "Максимум, минимум и закрытие; с открытием — свечи")
        };

        private static readonly string[] GroupingLabels = { "Обычная", "С накоплением", "Нормированная (100 %)" };
        private static readonly string[] LegendLabels = { "Нет", "Снизу", "Справа", "Сверху", "Слева" };

        /// <summary>Вид ряда в комбинированной диаграмме.</summary>
        private static readonly (ChartKind Kind, string Label)[] SeriesKinds =
        {
            (ChartKind.Column, "Столбцы"),
            (ChartKind.Line, "Линия"),
            (ChartKind.Area, "Область")
        };

        /// <summary>Форматы чисел оси и подписей: подпись и код Excel (null — общий).</summary>
        private static readonly (string Label, string? Code)[] NumberFormats =
        {
            ("Общий", null),
            ("0", "0"),
            ("0,0", "0.0"),
            ("0,00", "0.00"),
            ("1 234", "#,##0"),
            ("1 234,00", "#,##0.00"),
            ("0 %", "0%"),
            ("0,0 %", "0.0%")
        };

        private const string UndoIcon = "M12.5 8c-2.65 0-5.05.99-6.9 2.6L2 7v9h9l-3.62-3.62c1.39-1.16 3.16-1.88 5.12-1.88 3.54 0 6.55 2.31 7.6 5.5l2.37-.78C21.08 11.03 17.15 8 12.5 8z";
        private const string RedoIcon = "M18.4 10.6C16.55 8.99 14.15 8 11.5 8c-4.65 0-8.58 3.03-9.96 7.22L3.9 16c1.05-3.19 4.05-5.5 7.6-5.5 1.95 0 3.73.72 5.12 1.88L13 16h9V7l-3.6 3.6z";
        private const string CloseIcon = "M19 6.41L17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z";
        private const string TrashIcon = "M6 19c0 1.1.9 2 2 2h8c1.1 0 2-.9 2-2V7H6v12zM19 4h-3.5l-1-1h-5l-1 1H5v2h14V4z";
        private const string PlusIcon = "M19 13h-6v6h-2v-6H5v-2h6V5h2v6h6v2z";
        private const string MinusIcon = "M19 13H5v-2h14v2z";
        private const string SwapIcon = "M7 3L3 7h3v7h2V7h3L7 3zm10 18l4-4h-3v-7h-2v7h-3l4 4z";
        private const string ResetIcon = "M17.65 6.35A7.958 7.958 0 0 0 12 4C7.58 4 4.01 7.58 4.01 12S7.58 20 12 20c3.73 0 6.84-2.55 7.73-6h-2.08A5.99 5.99 0 0 1 12 18c-3.31 0-6-2.69-6-6s2.69-6 6-6c1.66 0 3.14.69 4.22 1.78L13 11h7V4l-2.35 2.35z";
        private const string PictureIcon = "M21 19V5c0-1.1-.9-2-2-2H5c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2zM8.5 13.5l2.5 3.01L14.5 12l4.5 6H5l3.5-4.5z";

        /// <summary>Роды надписей в карточке «Надписи».</summary>
        private static readonly (ChartTextRole Role, string Label)[] TextRoles =
        {
            (ChartTextRole.Title, "Название"),
            (ChartTextRole.Legend, "Легенда"),
            (ChartTextRole.AxisLabels, "Подписи осей"),
            (ChartTextRole.AxisTitles, "Названия осей"),
            (ChartTextRole.DataLabels, "Подписи значений")
        };

        private const string DefaultChartFont = "Calibri";

        private const string GearIcon = "M19.14 12.94c.04-.3.06-.61.06-.94 0-.32-.02-.64-.07-.94l2.03-1.58a.49.49 0 0 0 .12-.61l-1.92-3.32a.488.488 0 0 0-.59-.22l-2.39.96c-.5-.38-1.03-.7-1.62-.94l-.36-2.54a.484.484 0 0 0-.48-.41h-3.84c-.24 0-.43.17-.47.41l-.36 2.54c-.59.24-1.13.57-1.62.94l-2.39-.96c-.22-.08-.47 0-.59.22L2.74 8.87c-.12.21-.08.47.12.61l2.03 1.58c-.05.3-.09.63-.09.94s.02.64.07.94l-2.03 1.58a.49.49 0 0 0-.12.61l1.92 3.32c.12.22.37.29.59.22l2.39-.96c.5.38 1.03.7 1.62.94l.36 2.54c.05.24.24.41.48.41h3.84c.24 0 .44-.17.47-.41l.36-2.54c.59-.24 1.13-.56 1.62-.94l2.39.96c.22.08.47 0 .59-.22l1.92-3.32c.12-.22.07-.47-.12-.61l-2.01-1.58zM12 15.6c-1.98 0-3.6-1.62-3.6-3.6s1.62-3.6 3.6-3.6 3.6 1.62 3.6 3.6-1.62 3.6-3.6 3.6z";
        private const string SecondaryAxisIcon = "M19 3h2v18h-2V3zM16 6h2v2h-2V6zm0 5h2v2h-2v-2zm0 5h2v2h-2v-2zM4 13h3v7H4v-7zm5-4h3v11H9V9z";
        private const string DragIcon = "M11 18c0 1.1-.9 2-2 2s-2-.9-2-2 .9-2 2-2 2 .9 2 2zm-2-8c-1.1 0-2 .9-2 2s.9 2 2 2 2-.9 2-2-.9-2-2-2zm0-6c-1.1 0-2 .9-2 2s.9 2 2 2 2-.9 2-2-.9-2-2-2zm6 4c1.1 0 2-.9 2-2s-.9-2-2-2-2 .9-2 2 .9 2 2 2zm0 2c-1.1 0-2 .9-2 2s.9 2 2 2 2-.9 2-2-.9-2-2-2zm0 6c-1.1 0-2 .9-2 2s.9 2 2 2 2-.9 2-2-.9-2-2-2z";
        private const string ArrowUpIcon = "M7.41 15.41L12 10.83l4.59 4.58L18 14l-6-6-6 6z";
        private const string ArrowDownIcon = "M7.41 8.59L12 13.17l4.59-4.58L18 10l-6 6-6-6 1.41-1.41z";

        private const string PaletteIcon = "M12 3a9 9 0 0 0 0 18c.83 0 1.5-.67 1.5-1.5 0-.39-.15-.74-.39-1.01-.23-.26-.38-.61-.38-.99 0-.83.67-1.5 1.5-1.5H16c2.76 0 5-2.24 5-5 0-4.42-4.03-8-9-8zm-5.5 9a1.5 1.5 0 1 1 0-3 1.5 1.5 0 0 1 0 3zm3-4a1.5 1.5 0 1 1 0-3 1.5 1.5 0 0 1 0 3zm5 0a1.5 1.5 0 1 1 0-3 1.5 1.5 0 0 1 0 3zm3 4a1.5 1.5 0 1 1 0-3 1.5 1.5 0 0 1 0 3z";

        // ── Состояние ────────────────────────────────────────────────────


        private TaskCompletionSource<ChartEditResult?>? _pending;
        private ChartModel _original = new();
        private bool _canDelete;
        private double _aspect = 432.0 / 252.0;

        /// <summary>Настройки, лист данных и цвета долей (<see cref="ChartEditorState"/>).</summary>
        private ChartEditorState _state = ChartEditorState.FromChart(new ChartModel());

        private ChartModel _draft => _state.Draft;
        private ChartSheet _sheet => _state.Sheet;
        private List<string?> _pointColors => _state.PointColors;

        private readonly List<ChartEditorState> _undo = new();
        private readonly List<ChartEditorState> _redo = new();
        private string? _coalesceKey;
        private DateTime _coalesceTime;

        private ChartEditorState _initial = ChartEditorState.FromChart(new ChartModel());

        private bool _loading;

        // ── Элементы ─────────────────────────────────────────────────────

        private readonly Border _card;
        private readonly TextBlock _titleText;
        private readonly Button _undoButton;
        private readonly Button _redoButton;
        private readonly Button _deleteButton;
        private readonly Button _okButton;

        private readonly List<(KindEntry Entry, Border Item, TextBlock Label)> _kindItems = new();

        private readonly Border _previewFrame;
        private readonly ChartPreviewControl _preview;
        private readonly DispatcherTimer _previewTimer;

        private readonly ChartDataGrid _grid;
        private readonly ScrollViewer _gridScroll;
        private readonly HintMark _dataHint;
        private readonly HintMark _seriesHint;
        private readonly Button _removeRowButton;
        private readonly Button _removeSeriesButton;
        private readonly Button _swapButton;

        private readonly TextBox _title;
        private readonly ComboBox _legend;
        private readonly CheckBox _dataLabels;

        private readonly Control _groupingRow;
        private readonly ComboBox _grouping;
        private readonly CheckBox _gridlines;
        private readonly CheckBox _minorGridlines;
        private readonly CheckBox _categoryGridlines;
        private readonly CheckBox _categoryMinorGridlines;
        private readonly ComboBox _labelPosition;
        private readonly List<ChartLabelPosition> _labelPositions = new();
        private readonly Control _labelPositionRow;
        private readonly CheckBox _dataTable;
        private readonly CheckBox _dataTableKeys;
        private readonly CheckBox _showCategoryAxis;
        private readonly CheckBox _showValueAxis;

        // Карточка «Анализ»: линии тренда и планки погрешностей по рядам, линии графика.
        private readonly Border _analysisCard;
        private readonly ComboBox _analysisSeries;
        private readonly List<int> _analysisIndexes = new();
        private readonly ComboBox _trendType;
        private readonly Control _trendOrderRow;
        private readonly TextBox _trendOrder;
        private readonly Control _trendPeriodRow;
        private readonly TextBox _trendPeriod;
        private readonly Control _trendForecastRow;
        private readonly TextBox _trendForward;
        private readonly TextBox _trendBackward;
        private readonly CheckBox _trendEquation;
        private readonly CheckBox _trendRSquared;
        private readonly ComboBox _errorType;
        private readonly Control _errorValueRow;
        private readonly TextBox _errorValue;
        private readonly TextBlock _errorValueLabel;
        private readonly CheckBox _dropLines;
        private readonly CheckBox _highLowLines;
        private readonly CheckBox _upDownBars;

        private static readonly string[] TrendTypeLabels =
            { "Нет", "Линейная", "Экспоненциальная", "Логарифмическая", "Полиномиальная", "Степенная", "Скользящее среднее" };

        private static readonly string[] ErrorTypeLabels =
            { "Нет", "Стандартная погрешность", "Процент", "Стандартное отклонение", "Постоянная величина" };
        private readonly CheckBox _border;
        private readonly CheckBox _markers;
        private readonly CheckBox _smooth;
        private readonly CheckBox _lines;
        private readonly CheckBox _radarFilled;
        private readonly Control _holeRow;
        private readonly Slider _hole;
        private readonly TextBlock _holeValue;
        private readonly Control _angleRow;
        private readonly Slider _angle;
        private readonly TextBlock _angleValue;
        private readonly Control _gapRow;
        private readonly Slider _gap;
        private readonly TextBlock _gapValue;

        private readonly TextBlock _seriesCardTitle;
        private readonly StackPanel _seriesList;
        private readonly List<(string Key, IReadOnlyList<string> Colors, Border Frame)> _schemeChips = new();
        private readonly WrapPanel _schemeStrip;
        private readonly Control _schemeEditor;
        private readonly TextBox _schemeName;
        private readonly StackPanel _schemeColors;
        private readonly Button _schemeAddColor;

        /// <summary>Окошко «Свои наборы цветов» поверх окна диаграммы.</summary>
        private readonly Grid _setsLayer;
        private readonly Border _setsPanel;
        private readonly WrapPanel _setsList;
        private readonly TextBlock _setsEmpty;

        /// <summary>
        /// Набор, по которому щёлкнули последним: обводится он один, даже если у
        /// другого набора те же цвета.
        /// </summary>
        private string? _activeSchemeKey;

        /// <summary>Откладывает запись своих наборов, пока цвет тянут в подборщике.</summary>
        private readonly DispatcherTimer _setsSaveTimer;

        /// <summary>Свои наборы цветов (общие для всех проектов) и набор, открытый для правки.</summary>
        private List<ChartColorSet> _userSets = new();
        private ChartColorSet? _editingSet;

        /// <summary>Строка карточки «Надписи»: поля одного рода надписей.</summary>
        private sealed class TextRow
        {
            public ChartTextRole Role;
            public Control Root = null!;
            public TextBlock FontText = null!;
            public TextBox Size = null!;
            public ToggleButton Bold = null!;
            public ToggleButton Italic = null!;
            public ColorPickerButton Color = null!;
            public Button Reset = null!;
        }

        private readonly List<TextRow> _textRows = new();

        private readonly ColorPickerButton _backgroundColor;
        private readonly Button _backgroundReset;
        private readonly Button _pictureButton;
        private readonly Button _pictureRemove;
        private readonly ColorPickerButton _plotColor;
        private readonly Button _plotReset;

        /// <summary>Кладёт файл картинки в хранилище проекта и даёт его имя; null — некуда.</summary>
        private Func<string, string?>? _storeImage;

        /// <summary>Байты картинки хранилища проекта по имени файла.</summary>
        private Func<string, byte[]?>? _readImage;

        /// <summary>
        /// Картинки фона для образца по именам файлов. Образец рисуется в потоке
        /// отрисовки, поэтому картинки готовятся заранее и только читаются.
        /// </summary>
        private readonly ConcurrentDictionary<string, SKImage?> _pictures = new(StringComparer.OrdinalIgnoreCase);

        private readonly Border _axesCard;
        private readonly TextBlock _categoryAxisLabel;
        private readonly TextBox _categoryAxisTitle;
        private readonly TextBlock _valueAxisLabel;
        private readonly TextBox _valueAxisTitle;
        private readonly TextBox _valueMin;
        private readonly TextBox _valueMax;
        private readonly ComboBox _valueFormat;
        private readonly List<string?> _valueFormatCodes = new();
        private readonly TextBox _xMin;
        private readonly TextBox _xMax;
        private readonly ComboBox _xFormat;
        private readonly List<string?> _xFormatCodes = new();
        private readonly StackPanel _xScalePanel;
        private readonly StackPanel _categoryAxisBlock;
        private readonly Border _axesDivider;
        private readonly StackPanel _secondaryPanel;
        private readonly TextBox _secondaryTitle;
        private readonly TextBox _secondaryMin;
        private readonly TextBox _secondaryMax;
        private readonly ComboBox _secondaryFormat;
        private readonly List<string?> _secondaryFormatCodes = new();

        public ChartEditorOverlay()
        {
            IsVisible = false;

            // Горячие клавиши редактора ищут модуль по Tag предков элемента в фокусе.
            // Окно — не редактор: своя метка отводит от него Enter, стрелки, Ctrl+Z,
            // Ctrl+C и прочие клавиши правки документа, и они работают в окне.
            Tag = "ChartEditor";

            AddDialogStyles();

            var scrim = new Border { Background = new SolidColorBrush(Color.FromArgb(0xB3, 0, 0, 0)) };

            // Щелчок мимо окна окно не закрывает — в нём могут быть набранные данные, — а
            // возвращает фокус в таблицу: клавиши окна (Esc, Ctrl+Z) работают дальше.
            scrim.PointerPressed += (_, e) =>
            {
                _grid?.Focus();
                e.Handled = true;
            };

            // ── Заголовок ──
            _titleText = new TextBlock { Text = "Диаграмма", FontSize = 16, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            Bind(_titleText, TextBlock.ForegroundProperty, "TextPrimaryBrush");

            var subtitle = Hint("Как работать с окном",
                "Слева — вид диаграммы, справа — подписи, оформление, ряды и оси. Таблица — как лист Excel: "
                + "щёлкните по ячейке и печатайте, двойной щелчок или F2 правят текст, Enter — вниз, Tab — вправо, "
                + "Ctrl+V вставит таблицу из Excel или Word. Ctrl+Z и Ctrl+Y отменяют и возвращают правки в окне; "
                + "в документ диаграмма попадёт по «Готово» одним шагом отмены.");
            subtitle.Margin = new Thickness(10, 2, 0, 0);
            subtitle.HorizontalAlignment = HorizontalAlignment.Left;

            _undoButton = IconButton(UndoIcon, "Отменить правку в окне (Ctrl+Z)");
            _undoButton.Click += (_, _) => Undo();
            _redoButton = IconButton(RedoIcon, "Вернуть правку в окне (Ctrl+Y)");
            _redoButton.Click += (_, _) => Redo();
            var closeButton = IconButton(CloseIcon, "Закрыть без изменений (Esc)");
            closeButton.Click += (_, _) => Finish(null);

            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto"), Margin = new Thickness(0, 0, 0, 14) };
            Place(header, _titleText, 0, 0);
            Place(header, subtitle, 0, 1);
            Place(header, _undoButton, 0, 2);
            Place(header, _redoButton, 0, 3);
            Place(header, closeButton, 0, 4);
            closeButton.Margin = new Thickness(8, 0, 0, 0);

            // ── Галерея видов ──
            var kindStack = new StackPanel { Spacing = 2 };
            kindStack.Children.Add(SectionLabel("Вид"));
            foreach (var entry in KindEntries)
            {
                var icon = new ChartKindIcon { Kind = entry.Kind, Combo = entry.Combo, Width = 30, Height = 22, VerticalAlignment = VerticalAlignment.Center };
                var label = new TextBlock { Text = entry.Label, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
                Bind(label, TextBlock.ForegroundProperty, "TextPrimaryBrush");

                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                Place(row, icon, 0, 0);
                Place(row, label, 0, 1);

                var item = new Border
                {
                    Child = row,
                    Padding = new Thickness(8, 7),
                    CornerRadius = new CornerRadius(7),
                    Background = Brushes.Transparent,
                    Cursor = new Cursor(StandardCursorType.Hand)
                };
                TooltipBehavior.SetTip(item, entry.Tip);
                var captured = entry;
                item.PointerPressed += (_, e) =>
                {
                    if (!e.GetCurrentPoint(item).Properties.IsLeftButtonPressed) return;
                    SelectKindEntry(captured);
                    e.Handled = true;
                };
                item.PointerEntered += (_, _) => PaintKindItems();
                item.PointerExited += (_, _) => PaintKindItems();
                _kindItems.Add((entry, item, label));
                kindStack.Children.Add(item);
            }

            var kindPanel = new Border
            {
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 8),
                Child = new ScrollViewer
                {
                    Content = kindStack,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    AllowAutoHide = false
                }
            };
            Bind(kindPanel, Border.BackgroundProperty, "BgSurfaceBrush");
            Bind(kindPanel, Border.BorderBrushProperty, "BorderDefaultBrush");

            // ── Образец ──
            _preview = new ChartPreviewControl { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
            _preview.Images = PictureOf;
            _previewFrame = new Border
            {
                Background = Brushes.White,
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6),
                ClipToBounds = true,
                Height = 280,
                Child = _preview
            };
            Bind(_previewFrame, Border.BorderBrushProperty, "BorderDefaultBrush");

            // ── Данные ──
            _grid = new ChartDataGrid
            {
                RoleOf = RoleOf,
                PlaceholderOf = PlaceholderOf,
                ColumnWidthOf = ColumnWidthOf,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            _grid.BeforeChange += () => PushUndo();
            _grid.Changed += OnGridChanged;
            _grid.SelectionChanged += UpdateDataButtons;
            _grid.MenuRequested += ShowGridMenu;

            _gridScroll = new ScrollViewer
            {
                Content = _grid,
                AllowAutoHide = false,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            var gridFrame = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                ClipToBounds = true,
                Child = _gridScroll
            };
            Bind(gridFrame, Border.BorderBrushProperty, "BorderDefaultBrush");
            Bind(gridFrame, Border.BackgroundProperty, "BgAppBrush");

            // Кнопки таблицы — квадратные кнопки ленты: зелёный плюс добавляет, красная
            // корзина удаляет. Подписи «Строки» и «Ряды» перед парами говорят, к чему они.
            var addRowButton = DataButton(PlusIcon, "Добавить строку", "Новая категория под выделенной строкой", DataButtonKind.Add);
            addRowButton.Click += (_, _) => InsertRows(below: true);
            _removeRowButton = DataButton(TrashIcon, "Удалить строку", "Удалить выделенные строки", DataButtonKind.Remove);
            _removeRowButton.Click += (_, _) => RemoveSelectedRows();
            var addSeriesButton = DataButton(PlusIcon, "Добавить ряд", "Новый ряд справа от выделенного", DataButtonKind.Add);
            addSeriesButton.Click += (_, _) => InsertSeries(right: true);
            _removeSeriesButton = DataButton(TrashIcon, "Удалить ряд", "Удалить выделенные ряды", DataButtonKind.Remove);
            _removeSeriesButton.Click += (_, _) => RemoveSelectedSeries();
            _swapButton = DataButton(SwapIcon, "Строки ⇄ столбцы", "Поменять местами ряды и категории", DataButtonKind.Neutral);
            _swapButton.Click += (_, _) => Transpose();

            // У заголовков разделов отступ снизу; в строке кнопок он поднимал подпись
            // над соседними — здесь она стоит по центру строки, как «Строки» и «Ряды».
            var dataLabel = SectionLabel("Данные");
            dataLabel.Margin = new Thickness(2, 0, 0, 0);
            dataLabel.FontSize = 12;
            dataLabel.VerticalAlignment = VerticalAlignment.Center;
            _dataHint = Hint("Таблица данных", string.Empty);
            _dataHint.Margin = new Thickness(6, 0, 14, 0);
            _dataHint.VerticalAlignment = VerticalAlignment.Center;

            var dataTools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            dataTools.Children.Add(dataLabel);
            dataTools.Children.Add(_dataHint);
            dataTools.Children.Add(GroupLabel("Строки"));
            dataTools.Children.Add(addRowButton);
            dataTools.Children.Add(_removeRowButton);
            dataTools.Children.Add(DataSeparator());
            dataTools.Children.Add(GroupLabel("Ряды"));
            dataTools.Children.Add(addSeriesButton);
            dataTools.Children.Add(_removeSeriesButton);
            dataTools.Children.Add(DataSeparator());
            dataTools.Children.Add(_swapButton);

            var center = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), RowSpacing = 8 };
            Place(center, _previewFrame, 0, 0);
            Place(center, dataTools, 1, 0);
            Place(center, gridFrame, 2, 0);
            gridFrame.MinHeight = 120;
            center.SizeChanged += (_, e) => FitPreview(e.NewSize);

            // ── Настройки: подписи ──
            // Название без отдельной галочки: есть текст — название есть, поле пустое — нет.
            _title = Input();
            _title.PlaceholderText = "без названия";
            TooltipBehavior.SetTip(_title, "Название диаграммы");
            TooltipBehavior.SetDescription(_title, "Сотрите текст, чтобы убрать название с диаграммы.");
            _title.TextChanged += (_, _) =>
            {
                string text = _title.Text?.Trim() ?? string.Empty;
                Edit("title", d =>
                {
                    d.ShowTitle = text.Length > 0;
                    d.Title = text;
                }, coalesce: true);
            };

            _legend = Combo(LegendLabels);
            _legend.SelectionChanged += (_, _) =>
            {
                if (_legend.SelectedIndex >= 0) Edit("legend", d => d.Legend = (ChartLegendPosition)_legend.SelectedIndex);
            };

            _dataLabels = Check("Подписи значений");
            _dataLabels.IsCheckedChanged += (_, _) =>
            {
                Edit("labels", d => d.ShowDataLabels = _dataLabels.IsChecked == true);
                _labelPositionRow!.IsVisible = _dataLabels.IsChecked == true && _labelPositions.Count > 1;
            };

            _labelPosition = new ComboBox { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
            _labelPosition.SelectionChanged += (_, _) =>
            {
                if (_loading || _labelPosition.SelectedIndex < 0 || _labelPosition.SelectedIndex >= _labelPositions.Count) return;
                var position = _labelPositions[_labelPosition.SelectedIndex];
                Edit("labelPos", d => d.DataLabelPosition = position);
            };
            _labelPositionRow = FieldRow("Место", _labelPosition);

            _dataTable = Check("Таблица данных");
            _dataTable.IsCheckedChanged += (_, _) =>
            {
                Edit("table", d => d.ShowDataTable = _dataTable.IsChecked == true);
                _dataTableKeys!.IsVisible = _dataTable.IsChecked == true;
            };
            _dataTableKeys = Check("Ключи легенды");
            _dataTableKeys.Margin = new Thickness(22, 0, 0, 0);
            _dataTableKeys.IsCheckedChanged += (_, _) => Edit("tableKeys", d => d.HideDataTableKeys = _dataTableKeys.IsChecked != true);

            var labelsCard = Card("Название и легенда",
                FieldRow("Название", _title),
                FieldRow("Легенда", _legend),
                _dataLabels,
                _labelPositionRow,
                _dataTable,
                _dataTableKeys);

            // ── Настройки: оформление ──
            _grouping = Combo(GroupingLabels);
            _grouping.SelectionChanged += (_, _) =>
            {
                if (_grouping.SelectedIndex >= 0) Edit("grouping", d => d.Grouping = (ChartGrouping)_grouping.SelectedIndex);
            };
            _groupingRow = FieldRow("Ряды", _grouping);

            _gridlines = Check("Линии сетки");
            _gridlines.IsCheckedChanged += (_, _) => Edit("grid", d => d.ShowGridlines = _gridlines.IsChecked == true);
            _minorGridlines = Check("Вспомогательная сетка");
            _minorGridlines.IsCheckedChanged += (_, _) => Edit("gridMinor", d => d.ShowMinorGridlines = _minorGridlines.IsChecked == true);
            _categoryGridlines = Check("Сетка по категориям");
            _categoryGridlines.IsCheckedChanged += (_, _) => Edit("gridCat", d => d.ShowCategoryGridlines = _categoryGridlines.IsChecked == true);
            _categoryMinorGridlines = Check("Вспомогательная сетка по категориям");
            _categoryMinorGridlines.IsCheckedChanged += (_, _) => Edit("gridCatMinor", d => d.ShowCategoryMinorGridlines = _categoryMinorGridlines.IsChecked == true);
            _border = Check("Рамка");
            _border.IsCheckedChanged += (_, _) => Edit("border", d => d.ShowBorder = _border.IsChecked == true);
            _markers = Check("Маркеры точек");
            _markers.IsCheckedChanged += (_, _) => Edit("markers", d => SetLineFlags(d, markers: _markers.IsChecked == true));
            _smooth = Check("Сглаженные линии");
            _smooth.IsCheckedChanged += (_, _) => Edit("smooth", d => SetLineFlags(d, smooth: _smooth.IsChecked == true));
            _lines = Check("Линии между точками");
            _lines.IsCheckedChanged += (_, _) => Edit("lines", d => SetLineFlags(d, lines: _lines.IsChecked == true));
            _radarFilled = Check("С заливкой");
            _radarFilled.IsCheckedChanged += (_, _) => Edit("radarFill", d => d.RadarFilled = _radarFilled.IsChecked == true);

            (_hole, _holeValue, _holeRow) = SliderRow("Отверстие", 10, 90, 1, "%");
            _hole.PropertyChanged += (_, e) =>
            {
                if (e.Property != RangeBase.ValueProperty) return;
                _holeValue.Text = ((int)Math.Round(_hole.Value)).ToString(CultureInfo.CurrentCulture) + " %";
                Edit("hole", d => d.HoleSizePercent = (int)Math.Round(_hole.Value), coalesce: true);
            };
            (_angle, _angleValue, _angleRow) = SliderRow("Поворот", 0, 359, 1, "°");
            _angle.PropertyChanged += (_, e) =>
            {
                if (e.Property != RangeBase.ValueProperty) return;
                _angleValue.Text = ((int)Math.Round(_angle.Value)).ToString(CultureInfo.CurrentCulture) + "°";
                Edit("angle", d => d.FirstSliceAngle = (int)Math.Round(_angle.Value), coalesce: true);
            };
            (_gap, _gapValue, _gapRow) = SliderRow("Зазор", 0, 500, 5, "%");
            _gap.PropertyChanged += (_, e) =>
            {
                if (e.Property != RangeBase.ValueProperty) return;
                _gapValue.Text = ((int)Math.Round(_gap.Value)).ToString(CultureInfo.CurrentCulture) + " %";
                Edit("gap", d => d.GapWidthPercent = (int)Math.Round(_gap.Value), coalesce: true);
            };

            var lookCard = Card("Оформление",
                _groupingRow,
                _gridlines, _minorGridlines, _categoryGridlines, _categoryMinorGridlines,
                _border, _markers, _smooth, _lines, _radarFilled,
                _holeRow, _angleRow, _gapRow);

            // ── Настройки: надписи ──
            // Все надписи сразу, строкой на каждую: шрифт, размер, жирный, курсив, цвет.
            var textRowsPanel = new StackPanel { Spacing = 8 };
            foreach (var (role, label) in TextRoles)
            {
                var row = BuildTextRow(role, label);
                _textRows.Add(row);
                textRowsPanel.Children.Add(row.Root);
            }

            var textCard = CardWithTitle(
                TitleRow(CardTitle("Надписи"), Hint("Надписи",
                    "Шрифт, размер, жирный, курсив и цвет — у каждой надписи свои. Размер вводится числом в пунктах; "
                    + "пустое поле — размер по умолчанию (он виден серым). Кнопка со стрелкой возвращает "
                    + "надписям оформление по умолчанию.")),
                textRowsPanel);

            // ── Настройки: фон ──
            _backgroundColor = FillPicker("#FFFFFF", "Заливка всей диаграммы: цвет, прозрачность или градиент. Правая кнопка — подбор своего цвета", value =>
            {
                string? fill = value is null ? "#00000000" : SameColor(value, "#FFFFFF") ? null : value;
                Edit("background", d => d.BackgroundFill = fill, coalesce: true);
                _backgroundReset!.IsVisible = fill is not null;
            });
            _backgroundReset = ResetButton(false);
            TooltipBehavior.SetTip(_backgroundReset, "Вернуть белую заливку");
            _backgroundReset.Click += (_, _) =>
            {
                Edit("backgroundReset", d => d.BackgroundFill = null);
                RefreshBackground();
            };

            _pictureButton = ToolButton(PictureIcon, "Картинка…", "Поставить картинку фоном диаграммы: растягивается на всю диаграмму поверх заливки");
            _pictureButton.Click += async (_, _) => await PickBackgroundPicture();
            _pictureRemove = ToolButton(TrashIcon, "Убрать", "Убрать картинку фона");
            _pictureRemove.Click += (_, _) =>
            {
                Edit("pictureRemove", d => d.BackgroundImage = null);
                RefreshBackground();
            };

            _plotColor = FillPicker("#00000000", "Заливка области построения — под рядами и сеткой. Правая кнопка — подбор своего цвета", value =>
            {
                Edit("plotFill", d => d.PlotAreaFill = value, coalesce: true);
                _plotReset!.IsVisible = value is not null;
            });
            _plotReset = ResetButton(false);
            TooltipBehavior.SetTip(_plotReset, "Убрать заливку области построения");
            _plotReset.Click += (_, _) =>
            {
                Edit("plotReset", d => d.PlotAreaFill = null);
                RefreshBackground();
            };

            var backgroundRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            backgroundRow.Children.Add(_backgroundColor);
            backgroundRow.Children.Add(_backgroundReset);
            var pictureRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            pictureRow.Children.Add(_pictureButton);
            pictureRow.Children.Add(_pictureRemove);
            var plotRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            plotRow.Children.Add(_plotColor);
            plotRow.Children.Add(_plotReset);

            var backgroundCard = CardWithTitle(
                TitleRow(CardTitle("Фон"), Hint("Фон",
                    "Заливка всей диаграммы и области построения (где ряды и сетка): цвет, прозрачность или "
                    + "градиент — линейный, радиальный, конический. Картинка ложится на всю диаграмму поверх заливки.")),
                FieldRow("Диаграмма", backgroundRow),
                FieldRow("Картинка", pictureRow),
                FieldRow("Область", plotRow));

            // ── Настройки: ряды ──
            _seriesCardTitle = CardTitle("Ряды");
            _seriesHint = Hint("Ряды", string.Empty);
            _seriesList = new StackPanel { Spacing = 6 };
            _schemeStrip = new WrapPanel { Orientation = Orientation.Horizontal };

            _schemeName = new TextBox { FontSize = 12, MinHeight = 26, Padding = new Thickness(6, 3), PlaceholderText = "Имя набора" };
            _schemeName.LostFocus += (_, _) => RenameEditingSet();
            _schemeName.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                RenameEditingSet();
                _grid.Focus();
                e.Handled = true;
            };
            _schemeColors = new StackPanel { Spacing = 3 };
            _schemeAddColor = ToolButton(PlusIcon, "Цвет", "Добавить в набор ещё один цвет");
            _schemeAddColor.Click += (_, _) => AddSetColor();
            var updateSet = ToolButton(ResetIcon, "Взять цвета рядов", "Заменить цвета набора нынешними цветами рядов диаграммы (у круговой — долей)");
            updateSet.Click += (_, _) => UpdateEditingSet();
            var deleteSet = ToolButton(TrashIcon, "Удалить", "Удалить свой набор");
            deleteSet.Click += (_, _) => DeleteEditingSet();
            var schemeButtons = new WrapPanel { Orientation = Orientation.Horizontal };
            schemeButtons.Children.Add(_schemeAddColor);
            schemeButtons.Children.Add(updateSet);
            schemeButtons.Children.Add(deleteSet);

            _schemeEditor = Stack(6,
                FieldLabel("Имя"),
                _schemeName,
                TitleRow(FieldLabel("Цвета по порядку"), Hint("Цвета набора",
                    "Первый цвет получает первый ряд, второй — второй и так далее; рядов больше, чем цветов, — цвета идут по кругу. "
                    + "Щелчок по кружку открывает выбор цвета, правая кнопка — свой цвет, прозрачность и градиент. "
                    + "Порядок меняется перетаскиванием строки за ручку слева, крестик убирает цвет.")),
                _schemeColors,
                schemeButtons);
            _schemeEditor.IsVisible = false;

            _setsSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _setsSaveTimer.Tick += (_, _) =>
            {
                _setsSaveTimer.Stop();
                ChartColorSets.Save(_userSets);
            };

            var seriesCard = CardWithTitle(TitleRow(_seriesCardTitle, _seriesHint),
                _schemeStrip,
                Divider(),
                _seriesList);
            _userSets = ChartColorSets.Load();
            RebuildSchemeStrip();

            // ── Настройки: оси ──
            _categoryAxisLabel = FieldLabel("Ось категорий");
            _categoryAxisTitle = Input();
            _categoryAxisTitle.PlaceholderText = "название оси";
            _categoryAxisTitle.TextChanged += (_, _) => Edit("catTitle", d => d.CategoryAxisTitle = NullIfEmpty(_categoryAxisTitle.Text), coalesce: true);
            _valueAxisLabel = FieldLabel("Ось значений");
            _valueAxisTitle = Input();
            _valueAxisTitle.PlaceholderText = "название оси";
            _valueAxisTitle.TextChanged += (_, _) => Edit("valTitle", d => d.ValueAxisTitle = NullIfEmpty(_valueAxisTitle.Text), coalesce: true);

            _valueMin = LimitInput();
            _valueMin.TextChanged += (_, _) => Edit("valMin", d => d.ValueAxisMin = ParseLimit(_valueMin), coalesce: true);
            _valueMax = LimitInput();
            _valueMax.TextChanged += (_, _) => Edit("valMax", d => d.ValueAxisMax = ParseLimit(_valueMax), coalesce: true);
            _valueFormat = FormatPicker(_valueFormatCodes);
            _valueFormat.SelectionChanged += (_, _) =>
            {
                if (_valueFormat.SelectedIndex >= 0) Edit("valFormat", d => d.NumberFormat = SelectedFormat(_valueFormat, _valueFormatCodes));
            };

            // Ось X точечной и пузырьковой — ось значений со своей шкалой: свои пределы
            // и формат чисел, отдельно от оси Y (дни по X, миллионы по Y).
            _xMin = LimitInput();
            _xMin.TextChanged += (_, _) => Edit("xMin", d => d.XAxisMin = ParseLimit(_xMin), coalesce: true);
            _xMax = LimitInput();
            _xMax.TextChanged += (_, _) => Edit("xMax", d => d.XAxisMax = ParseLimit(_xMax), coalesce: true);
            _xFormat = FormatPicker(_xFormatCodes);
            _xFormat.SelectionChanged += (_, _) =>
            {
                if (_xFormat.SelectedIndex >= 0) Edit("xFormat", d => d.XNumberFormat = SelectedFormat(_xFormat, _xFormatCodes));
            };
            _xScalePanel = Stack(6, LimitsRow(_xMin, _xMax), FieldRow("Числа", _xFormat));

            _secondaryTitle = Input();
            _secondaryTitle.PlaceholderText = "без названия";
            _secondaryTitle.TextChanged += (_, _) => Edit("secTitle", d => d.SecondaryAxisTitle = NullIfEmpty(_secondaryTitle.Text), coalesce: true);
            _secondaryMin = LimitInput();
            _secondaryMin.TextChanged += (_, _) => Edit("secMin", d => d.SecondaryAxisMin = ParseLimit(_secondaryMin), coalesce: true);
            _secondaryMax = LimitInput();
            _secondaryMax.TextChanged += (_, _) => Edit("secMax", d => d.SecondaryAxisMax = ParseLimit(_secondaryMax), coalesce: true);
            _secondaryFormat = FormatPicker(_secondaryFormatCodes);
            _secondaryFormat.SelectionChanged += (_, _) =>
            {
                if (_secondaryFormat.SelectedIndex >= 0) Edit("secFormat", d => d.SecondaryNumberFormat = SelectedFormat(_secondaryFormat, _secondaryFormatCodes));
            };

            _secondaryPanel = Stack(6,
                Divider(),
                TitleRow(FieldLabel("Вспомогательная ось (справа)"),
                    Hint("Вспомогательная ось", "Своя шкала справа для рядов, отмеченных «Ось справа» в карточке «Ряды». "
                        + "Нужна, когда величины разного масштаба: например, продажи в миллионах и рост в процентах.")),
                _secondaryTitle,
                LimitsRow(_secondaryMin, _secondaryMax),
                FieldRow("Числа", _secondaryFormat));

            _showCategoryAxis = Check("Подписи");
            TooltipBehavior.SetTip(_showCategoryAxis, "Показывать подписи у этой оси");
            _showCategoryAxis.IsCheckedChanged += (_, _) => Edit("catAxis", d => d.HideCategoryAxis = _showCategoryAxis.IsChecked != true);
            _showValueAxis = Check("Подписи");
            TooltipBehavior.SetTip(_showValueAxis, "Показывать подписи у этой оси");
            _showValueAxis.IsCheckedChanged += (_, _) => Edit("valAxis", d => d.HideValueAxis = _showValueAxis.IsChecked != true);

            // У каждой оси свой блок, блоки разделены чертой: заголовок с галочкой подписей
            // справа, название, и у числовой оси — её пределы и формат чисел. У оси
            // категорий пределов нет: на ней не числа, а подписи категорий.
            _categoryAxisBlock = Stack(6, AxisHeader(_categoryAxisLabel, _showCategoryAxis), _categoryAxisTitle, _xScalePanel);
            _axesDivider = Divider();
            var valueAxisBlock = Stack(6,
                AxisHeader(_valueAxisLabel, _showValueAxis), _valueAxisTitle,
                LimitsRow(_valueMin, _valueMax),
                FieldRow("Числа", _valueFormat));
            _axesCard = Card("Оси",
                _categoryAxisBlock,
                _axesDivider,
                valueAxisBlock,
                _secondaryPanel);

            // ── Настройки: анализ ──
            _analysisSeries = Combo(Array.Empty<string>());
            _analysisSeries.SelectionChanged += (_, _) => { if (!_loading) RefreshAnalysis(rebuildList: false); };

            _trendType = Combo(TrendTypeLabels);
            _trendType.SelectionChanged += (_, _) =>
            {
                if (_loading || _trendType.SelectedIndex < 0) return;
                int index = _trendType.SelectedIndex;
                EditTrend(t => index == 0 ? null : (t ?? new ChartTrendline()).Also(x => x.Type = (ChartTrendlineType)(index - 1)));
                RefreshAnalysis(rebuildList: false);
            };

            _trendOrder = SmallNumber("2");
            _trendOrder.TextChanged += (_, _) =>
            {
                if (_loading || !int.TryParse(_trendOrder.Text, out int order)) return;
                EditTrend(t => t?.Also(x => x.Order = Math.Clamp(order, 2, 6)), coalesce: true);
            };
            _trendOrderRow = FieldRow("Степень", _trendOrder);

            _trendPeriod = SmallNumber("2");
            _trendPeriod.TextChanged += (_, _) =>
            {
                if (_loading || !int.TryParse(_trendPeriod.Text, out int period)) return;
                EditTrend(t => t?.Also(x => x.Period = Math.Max(2, period)), coalesce: true);
            };
            _trendPeriodRow = FieldRow("Период", _trendPeriod);

            _trendForward = SmallNumber("0");
            _trendForward.TextChanged += (_, _) =>
            {
                if (_loading) return;
                double forward = ChartDataGrid.TryNumber(_trendForward.Text, out double f) ? Math.Max(0, f) : 0;
                EditTrend(t => t?.Also(x => x.Forward = forward), coalesce: true);
            };
            _trendBackward = SmallNumber("0");
            _trendBackward.TextChanged += (_, _) =>
            {
                if (_loading) return;
                double backward = ChartDataGrid.TryNumber(_trendBackward.Text, out double b) ? Math.Max(0, b) : 0;
                EditTrend(t => t?.Also(x => x.Backward = backward), coalesce: true);
            };
            // Прогноз одной строкой: подписи «вперёд» и «назад» стоят внутри полей.
            _trendForward.InnerLeftContent = InnerHint("вперёд");
            _trendBackward.InnerLeftContent = InnerHint("назад");
            const string forecastTip = "На сколько продлить линию тренда за последнюю точку (вперёд) и до первой (назад): "
                + "в шагах категорий, у точечной — в единицах X.";
            TooltipBehavior.SetTip(_trendForward, "Прогноз вперёд");
            TooltipBehavior.SetDescription(_trendForward, forecastTip);
            TooltipBehavior.SetTip(_trendBackward, "Прогноз назад");
            TooltipBehavior.SetDescription(_trendBackward, forecastTip);
            var forecast = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 6 };
            Place(forecast, _trendForward, 0, 0);
            Place(forecast, _trendBackward, 0, 1);
            _trendForecastRow = FieldRow("Прогноз", forecast);

            _trendEquation = Check("Уравнение");
            _trendEquation.IsCheckedChanged += (_, _) =>
            {
                if (_loading) return;
                bool show = _trendEquation.IsChecked == true;
                EditTrend(t => t?.Also(x => x.ShowEquation = show));
            };
            _trendRSquared = Check("Значение R²");
            _trendRSquared.IsCheckedChanged += (_, _) =>
            {
                if (_loading) return;
                bool show = _trendRSquared.IsChecked == true;
                EditTrend(t => t?.Also(x => x.ShowRSquared = show));
            };

            _errorType = Combo(ErrorTypeLabels);
            _errorType.SelectionChanged += (_, _) =>
            {
                if (_loading || _errorType.SelectedIndex < 0) return;
                int index = _errorType.SelectedIndex;
                EditErrors(e =>
                {
                    if (index == 0) return null;
                    var type = (ChartErrorBarType)(index - 1);
                    if (e is not null && e.Type == type) return e;
                    return new ChartErrorBars { Type = type, Value = ChartErrorBars.DefaultValue(type), NoEndCap = e?.NoEndCap ?? false };
                });
                RefreshAnalysis(rebuildList: false);
            };
            _errorValue = SmallNumber("5");
            _errorValue.TextChanged += (_, _) =>
            {
                if (_loading || !ChartDataGrid.TryNumber(_errorValue.Text, out double value)) return;
                EditErrors(e => e?.Also(x => x.Value = Math.Max(0, value)), coalesce: true);
            };
            _errorValueLabel = FieldLabel("Величина");
            var errorValueGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("78,*") };
            Place(errorValueGrid, _errorValueLabel, 0, 0);
            Place(errorValueGrid, _errorValue, 0, 1);
            _errorValueRow = errorValueGrid;

            _dropLines = Check("Линии проекции");
            _dropLines.IsCheckedChanged += (_, _) => Edit("dropLines", d => d.ShowDropLines = _dropLines.IsChecked == true);
            _highLowLines = Check("Линии максимума и минимума");
            _highLowLines.IsCheckedChanged += (_, _) => Edit("hiLow", d => d.ShowHighLowLines = _highLowLines.IsChecked == true);
            _upDownBars = Check("Полосы повышения и понижения");
            _upDownBars.IsCheckedChanged += (_, _) => Edit("upDown", d => d.ShowUpDownBars = _upDownBars.IsChecked == true);

            _analysisCard = CardWithTitle(
                TitleRow(CardTitle("Анализ"), Hint("Анализ",
                    "Линии тренда и пределы погрешностей — для выбранного ряда или для всех сразу. Линия тренда подбирается "
                    + "по значениям ряда; прогноз продлевает её вперёд и назад. Линии проекции, максимума и минимума и полосы "
                    + "повышения и понижения бывают у графиков.")),
                FieldRow("Ряд", _analysisSeries),
                FieldRow("Тренд", _trendType),
                _trendOrderRow,
                _trendPeriodRow,
                _trendForecastRow,
                TwoChecks(_trendEquation, _trendRSquared),
                Divider(),
                FieldRow("Погрешности", _errorType),
                _errorValueRow,
                Divider(),
                _dropLines,
                _highLowLines,
                _upDownBars);

            var settings = new StackPanel { Spacing = 10 };
            settings.Children.Add(labelsCard);
            settings.Children.Add(seriesCard);
            settings.Children.Add(lookCard);
            settings.Children.Add(textCard);
            settings.Children.Add(backgroundCard);
            settings.Children.Add(_axesCard);
            settings.Children.Add(_analysisCard);
            var settingsScroll = new ScrollViewer
            {
                Content = settings,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                AllowAutoHide = false,
                Padding = new Thickness(0, 0, 14, 0)
            };

            var body = new Grid { ColumnDefinitions = new ColumnDefinitions("200,*,330"), ColumnSpacing = 16 };
            Place(body, kindPanel, 0, 0);
            Place(body, center, 0, 1);
            Place(body, settingsScroll, 0, 2);

            // ── Кнопки ──
            _deleteButton = new Button { Classes = { "chartDanger" }, Padding = new Thickness(12, 6) };
            _deleteButton.Content = IconText(TrashIcon, "Удалить диаграмму");
            TooltipBehavior.SetTip(_deleteButton, "Убрать диаграмму из документа. Вернуть — Ctrl+Z в тексте");
            _deleteButton.Click += (_, _) => Finish(new ChartEditResult(null, true));

            var cancelButton = new Button { Content = "Отмена", Padding = new Thickness(16, 6), MinWidth = 96, HorizontalContentAlignment = HorizontalAlignment.Center };
            cancelButton.Click += (_, _) => Finish(null);
            _okButton = new Button { Classes = { "primary" }, Content = "Готово", Padding = new Thickness(18, 6), MinWidth = 110, FontWeight = FontWeight.SemiBold, HorizontalContentAlignment = HorizontalAlignment.Center };
            _okButton.Click += (_, _) => Accept();

            var rightButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
            rightButtons.Children.Add(cancelButton);
            rightButtons.Children.Add(_okButton);

            var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 14, 0, 0) };
            Place(footer, _deleteButton, 0, 0);
            Place(footer, rightButtons, 0, 2);

            var dock = new DockPanel();
            DockPanel.SetDock(header, Avalonia.Controls.Dock.Top);
            DockPanel.SetDock(footer, Avalonia.Controls.Dock.Bottom);
            dock.Children.Add(header);
            dock.Children.Add(footer);
            dock.Children.Add(body);

            _card = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(24),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(20, 18),
                Child = dock,
                Effect = new DropShadowEffect { BlurRadius = 32, OffsetX = 0, OffsetY = 12, Opacity = 0.55, Color = Colors.Black }
            };
            Bind(_card, Border.BackgroundProperty, "BgBaseBrush");
            Bind(_card, Border.BorderBrushProperty, "BorderDefaultBrush");

            // ── Окошко своих наборов цветов ──
            _setsList = new WrapPanel { Orientation = Orientation.Horizontal };
            _setsEmpty = FieldLabel("Своих наборов пока нет. «Новый набор» заведёт его из нынешних цветов рядов.");
            _setsEmpty.TextWrapping = TextWrapping.Wrap;
            var newSet = ToolButton(PlusIcon, "Новый набор", "Завести свой набор из нынешних цветов рядов диаграммы");
            newSet.Click += (_, _) => AddUserSet();

            var setsTitle = new TextBlock { Text = "Свои наборы цветов", FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            Bind(setsTitle, TextBlock.ForegroundProperty, "TextPrimaryBrush");
            var setsClose = IconButton(CloseIcon, "Закрыть (Esc)");
            setsClose.Click += (_, _) => CloseSetsManager();
            var setsHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 10) };
            Place(setsHeader, TitleRow(setsTitle, Hint("Свои наборы цветов",
                "Свои наборы видны во всех проектах. Выберите набор сверху и настройте его: имя, цвета, их порядок и число. "
                + "Изменения сохраняются сами. «Применить к диаграмме» перекрашивает ряды этим набором.")), 0, 0);
            Place(setsHeader, setsClose, 0, 1);

            var applySet = new Button { Classes = { "primary" }, Content = "Применить к диаграмме", Padding = new Thickness(14, 6), HorizontalContentAlignment = HorizontalAlignment.Center };
            applySet.Click += (_, _) =>
            {
                if (_editingSet is not { } set) return;
                _activeSchemeKey = "u" + set.Id;
                ApplyScheme(set.Colors);
                CloseSetsManager();
            };
            var setsDone = new Button { Content = "Готово", Padding = new Thickness(14, 6), MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
            setsDone.Click += (_, _) => CloseSetsManager();
            var setsFooter = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            setsFooter.Children.Add(applySet);
            setsFooter.Children.Add(setsDone);

            var setsBody = Stack(8, _setsList, newSet, _setsEmpty, Divider(), _schemeEditor);
            var setsScroll = new ScrollViewer
            {
                Content = setsBody,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                AllowAutoHide = false,
                Padding = new Thickness(0, 0, 14, 0)
            };

            var setsDock = new DockPanel();
            DockPanel.SetDock(setsHeader, Avalonia.Controls.Dock.Top);
            DockPanel.SetDock(setsFooter, Avalonia.Controls.Dock.Bottom);
            setsDock.Children.Add(setsHeader);
            setsDock.Children.Add(setsFooter);
            setsDock.Children.Add(setsScroll);

            _setsPanel = new Border
            {
                Width = 420,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(18, 16),
                Child = setsDock,
                Focusable = true,
                Effect = new DropShadowEffect { BlurRadius = 28, OffsetX = 0, OffsetY = 10, Opacity = 0.55, Color = Colors.Black }
            };
            // Щелчок по пустому месту окошка держит фокус в нём — иначе Ctrl+Z уйдёт мимо.
            _setsPanel.AddHandler(PointerPressedEvent, (_, _) => KeepSetsFocus(), RoutingStrategies.Bubble, handledEventsToo: true);
            Bind(_setsPanel, Border.BackgroundProperty, "BgBaseBrush");
            Bind(_setsPanel, Border.BorderBrushProperty, "BorderDefaultBrush");

            var setsScrim = new Border { Background = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)) };
            setsScrim.PointerPressed += (_, e) =>
            {
                CloseSetsManager();
                e.Handled = true;
            };
            _setsLayer = new Grid { IsVisible = false };
            _setsLayer.Children.Add(setsScrim);
            _setsLayer.Children.Add(_setsPanel);

            var root = new Grid();
            root.Children.Add(scrim);
            root.Children.Add(_card);
            root.Children.Add(_setsLayer);
            root.SizeChanged += (_, e) =>
            {
                FitCard(e.NewSize);
                _setsPanel.MaxHeight = Math.Max(240, e.NewSize.Height - 80);
            };
            Content = root;

            _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
            _previewTimer.Tick += (_, _) =>
            {
                _previewTimer.Stop();
                UpdatePreview();
            };

            AddHandler(KeyDownEvent, OnOverlayKeyDown, RoutingStrategies.Bubble);
        }

        // ── Показ ────────────────────────────────────────────────────────

        /// <summary>
        /// Показывает окно для диаграммы. Итог — новая модель по «Готово», удаление
        /// по «Удалить диаграмму» или null, если окно закрыли без изменений.
        /// </summary>
        public Task<ChartEditResult?> ShowAsync(ChartModel chart, double widthPt, double heightPt, bool canDelete,
            Func<string, string?>? storeImage = null, Func<string, byte[]?>? readImage = null)
        {
            if (_pending is not null) Finish(null);

            _pending = new TaskCompletionSource<ChartEditResult?>();
            _original = chart.Clone();
            _canDelete = canDelete;
            _storeImage = storeImage;
            _readImage = readImage;
            _pictures.Clear();
            if (!string.IsNullOrEmpty(_original.BackgroundImage)) LoadPicture(_original.BackgroundImage!);

            // Свои наборы могли поменяться в окне другого редактора.
            _userSets = ChartColorSets.Load();
            _setsLayer.IsVisible = false;
            ShowSchemeEditor(null);
            _aspect = widthPt > 0 && heightPt > 0 ? widthPt / heightPt : 432.0 / 252.0;
            _preview.ChartWidthPt = 432.0;
            _preview.ChartHeightPt = 432.0 / _aspect;

            _titleText.Text = canDelete ? "Диаграмма" : "Новая диаграмма";
            _okButton.Content = canDelete ? "Готово" : "Вставить";
            _deleteButton.IsVisible = canDelete;

            _undo.Clear();
            _redo.Clear();
            _coalesceKey = null;

            _state = ChartEditorState.FromChart(_original);
            _initial = _state.Clone();

            IsVisible = true;
            FitCard(Bounds.Size);
            RefreshAll();
            _grid.SelectCell(1, 1);
            Dispatcher.UIThread.Post(() => _grid.Focus(), DispatcherPriority.Input);

            var start = PendingStart;
            PendingStart = ChartEditorStartAction.None;
            if (start != ChartEditorStartAction.None)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_pending is null) return;
                    if (start == ChartEditorStartAction.NewColorSet) AddUserSet();
                    else OpenSetsManager(_userSets.FirstOrDefault(u => "u" + u.Id == _activeSchemeKey) ?? _userSets.FirstOrDefault());
                }, DispatcherPriority.Background);
            }

            return _pending.Task;
        }

        private void Accept()
        {
            _grid.CommitPendingEdit();
            bool changed = !_state.SameAs(_initial);

            // Без правок отдаётся исходная модель: окно, открытое и закрытое по
            // «Готово», не должно менять диаграмму Word ни на одну цифру.
            var chart = changed || !_canDelete ? BuildChart() : _original.Clone();
            Finish(new ChartEditResult(chart, false));
        }

        private void Finish(ChartEditResult? result)
        {
            _previewTimer.Stop();
            IsVisible = false;
            _preview.Chart = null;
            var pending = _pending;
            _pending = null;
            pending?.TrySetResult(result);
        }

        private void FitCard(Size available)
        {
            if (available.Width <= 0 || available.Height <= 0) return;
            _card.Width = Math.Max(640, Math.Min(1200, available.Width - 48));
            _card.Height = Math.Max(460, Math.Min(820, available.Height - 48));
        }

        private void FitPreview(Size centerSize)
        {
            if (centerSize.Width <= 0 || centerSize.Height <= 0) return;
            double natural = (centerSize.Width - 14) / _aspect + 14;
            double limit = Math.Max(150, centerSize.Height * 0.48);
            _previewFrame.Height = Math.Clamp(natural, 150, limit);
        }

        private void OnOverlayKeyDown(object? sender, KeyEventArgs e)
        {
            if (!IsVisible || e.Handled) return;

            bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

            // В окошке своих наборов — своя история: Ctrl+Z отменяет правку набора.
            if (ctrl && e.Key == Key.Z && !shift)
            {
                if (_setsLayer.IsVisible) UndoSets(); else Undo();
                e.Handled = true;
            }
            else if (ctrl && (e.Key == Key.Y || (e.Key == Key.Z && shift)))
            {
                if (_setsLayer.IsVisible) RedoSets(); else Redo();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                // Открыто окошко своих наборов — Esc закрывает только его.
                if (_setsLayer.IsVisible) CloseSetsManager();
                else Finish(null);
                e.Handled = true;
            }
        }

        // ── Загрузка модели в окно ───────────────────────────────────────

        private static bool IsXY(ChartKind kind) => ChartEditorState.IsXY(kind);
        private static bool IsCircular(ChartKind kind) => ChartEditorState.IsCircular(kind);
        private static bool CanCombine(ChartKind kind) => ChartEditorState.CanCombine(kind);

        private ChartKind Kind => _state.Kind;
        private bool Bubble => _state.Bubble;
        private int SeriesCount => _state.SeriesCount;
        private int CategoryCount => _state.CategoryCount;
        private int ValueColumn(int series) => _state.ValueColumn(series);
        private int SeriesOfColumn(int column) => _state.SeriesOfColumn(column);
        private bool IsSizeColumn(int column) => _state.IsSizeColumn(column);

        private static string FormatNumber(double? value) => ChartEditorState.FormatNumber(value);

        private ChartModel BuildChart() => _state.Build();

        // ── История окна ─────────────────────────────────────────────────

        private ChartEditorState Capture() => _state.Clone();

        private void PushUndo()
        {
            _undo.Add(Capture());
            if (_undo.Count > 200) _undo.RemoveAt(0);
            _redo.Clear();
            _coalesceKey = null;
            UpdateUndoButtons();
        }

        private void Undo()
        {
            _grid.CommitPendingEdit();
            if (_undo.Count == 0) return;
            _redo.Add(Capture());
            var state = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            Restore(state);
        }

        private void Redo()
        {
            _grid.CommitPendingEdit();
            if (_redo.Count == 0) return;
            _undo.Add(Capture());
            var state = _redo[^1];
            _redo.RemoveAt(_redo.Count - 1);
            Restore(state);
        }

        private void Restore(ChartEditorState state)
        {
            var (top, left, bottom, right) = _grid.Selection;
            _state = state.Clone();
            _coalesceKey = null;
            RefreshAll();
            _grid.SelectRange(top, left, bottom, right);
        }

        private void UpdateUndoButtons()
        {
            _undoButton.IsEnabled = _undo.Count > 0;
            _redoButton.IsEnabled = _redo.Count > 0;
        }

        /// <summary>
        /// Правка настройки из панели: снимок для отмены, изменение и образец. Правка,
        /// которая ничего не поменяла (то же значение), шага отмены не заводит. Правки
        /// потоком (набор текста, ползунок, подбор цвета) одного поля без паузы — один шаг.
        /// </summary>
        private void Edit(string key, Action<ChartModel> change, bool coalesce = false)
        {
            if (_loading || _pending is null) return;

            var before = Capture();
            string beforePrint = before.Draft.ComputeFingerprint();
            change(_draft);
            bool changed = _draft.ComputeFingerprint() != beforePrint
                || !_pointColors.SequenceEqual(before.PointColors);
            if (!changed) return;

            var now = DateTime.UtcNow;
            bool merge = coalesce && _coalesceKey == key && (now - _coalesceTime).TotalMilliseconds < 900;
            if (!merge)
            {
                _undo.Add(before);
                if (_undo.Count > 200) _undo.RemoveAt(0);
                _redo.Clear();
                UpdateUndoButtons();
            }
            _coalesceKey = coalesce ? key : null;
            _coalesceTime = now;

            UpdateKindControls();
            UpdateSchemeChips();
            SchedulePreview();
        }

        // ── Обновление окна ──────────────────────────────────────────────

        private void RefreshAll()
        {
            _loading = true;
            try
            {
                _grid.GhostColumns = 1;
                _grid.Sheet = _sheet;

                _title.Text = _draft.DisplayTitle ?? string.Empty;
                _legend.SelectedIndex = (int)_draft.Legend;
                _dataLabels.IsChecked = _draft.ShowDataLabels;
                _dataTable.IsChecked = _draft.ShowDataTable;
                _dataTableKeys.IsChecked = !_draft.HideDataTableKeys;
                _minorGridlines.IsChecked = _draft.ShowMinorGridlines;
                _categoryGridlines.IsChecked = _draft.ShowCategoryGridlines;
                _categoryMinorGridlines.IsChecked = _draft.ShowCategoryMinorGridlines;
                _showCategoryAxis.IsChecked = !_draft.HideCategoryAxis;
                _showValueAxis.IsChecked = !_draft.HideValueAxis;
                _grouping.SelectedIndex = (int)_draft.Grouping;
                _gridlines.IsChecked = _draft.ShowGridlines;
                _border.IsChecked = _draft.ShowBorder;

                var line = FirstLineSeries();
                _markers.IsChecked = line?.ShowMarkers ?? true;
                _smooth.IsChecked = line?.Smooth ?? false;
                _lines.IsChecked = line?.ShowLine ?? true;
                _radarFilled.IsChecked = _draft.RadarFilled;

                _hole.Value = Math.Clamp(_draft.HoleSizePercent, 10, 90);
                _holeValue.Text = _draft.HoleSizePercent.ToString(CultureInfo.CurrentCulture) + " %";
                _angle.Value = Math.Clamp(_draft.FirstSliceAngle, 0, 359);
                _angleValue.Text = _draft.FirstSliceAngle.ToString(CultureInfo.CurrentCulture) + "°";
                _gap.Value = Math.Clamp(_draft.GapWidthPercent, 0, 500);
                _gapValue.Text = _draft.GapWidthPercent.ToString(CultureInfo.CurrentCulture) + " %";

                _categoryAxisTitle.Text = _draft.CategoryAxisTitle ?? string.Empty;
                _valueAxisTitle.Text = _draft.ValueAxisTitle ?? string.Empty;
                _valueMin.Text = FormatNumber(_draft.ValueAxisMin);
                _valueMax.Text = FormatNumber(_draft.ValueAxisMax);
                SelectFormat(_valueFormat, _valueFormatCodes, _draft.NumberFormat);
                _xMin.Text = FormatNumber(_draft.XAxisMin);
                _xMax.Text = FormatNumber(_draft.XAxisMax);
                SelectFormat(_xFormat, _xFormatCodes, _draft.XNumberFormat);
                _secondaryTitle.Text = _draft.SecondaryAxisTitle ?? string.Empty;
                _secondaryMin.Text = FormatNumber(_draft.SecondaryAxisMin);
                _secondaryMax.Text = FormatNumber(_draft.SecondaryAxisMax);
                SelectFormat(_secondaryFormat, _secondaryFormatCodes, _draft.SecondaryNumberFormat);
            }
            finally
            {
                _loading = false;
            }

            UpdateKindControls();
            RebuildSchemeStrip();
            RebuildSeriesPanel();
            RefreshTextStyle();
            RefreshAnalysis(rebuildList: true);
            RefreshBackground();
            UpdateDataButtons();
            UpdateUndoButtons();
            UpdatePreview();
        }

        private ChartSeries? FirstLineSeries()
        {
            foreach (var series in _draft.Series)
                if (_draft.KindOf(series) is ChartKind.Line or ChartKind.Scatter or ChartKind.Radar) return series;
            return _draft.Series.Count > 0 ? _draft.Series[0] : null;
        }

        private void SetLineFlags(ChartModel draft, bool? markers = null, bool? smooth = null, bool? lines = null)
        {
            foreach (var series in draft.Series)
            {
                var kind = draft.KindOf(series);
                bool lineLike = kind is ChartKind.Line or ChartKind.Scatter or ChartKind.Radar;
                if (!lineLike) continue;
                if (markers is bool m) series.ShowMarkers = m;
                if (smooth is bool s) series.Smooth = s;
                if (lines is bool l && kind == ChartKind.Scatter) series.ShowLine = l;
            }
        }

        private void UpdateKindControls()
        {
            PaintKindItems();
            UpdateTextRowsVisibility();

            var kind = Kind;
            bool circular = IsCircular(kind);
            bool anyLine = kind is ChartKind.Line or ChartKind.Scatter or ChartKind.Radar
                || (CanCombine(kind) && _draft.Series.Exists(s => s.Kind == ChartKind.Line));

            // Флажки линий показывают первую линию: у комбинированной она могла только
            // что появиться.
            bool loading = _loading;
            _loading = true;
            try
            {
                var line = FirstLineSeries();
                _markers.IsChecked = line?.ShowMarkers ?? true;
                _smooth.IsChecked = line?.Smooth ?? false;
                _lines.IsChecked = line?.ShowLine ?? true;
            }
            finally
            {
                _loading = loading;
            }

            _groupingRow.IsVisible = _draft.HasGrouping;
            _gridlines.IsVisible = !circular;
            bool bar = kind == ChartKind.Bar;
            _gridlines.Content = kind == ChartKind.Radar ? "Линии сетки" : bar ? "Вертикальная сетка" : "Горизонтальная сетка";
            _minorGridlines.Content = bar ? "Вспомогательная вертикальная" : "Вспомогательная горизонтальная";
            _categoryGridlines.Content = bar ? "Горизонтальная сетка" : "Вертикальная сетка";
            _categoryMinorGridlines.Content = bar ? "Вспомогательная горизонтальная" : "Вспомогательная вертикальная";
            _minorGridlines.IsVisible = !circular && kind != ChartKind.Radar;
            _categoryGridlines.IsVisible = !circular && kind != ChartKind.Radar;
            _categoryMinorGridlines.IsVisible = !circular && kind != ChartKind.Radar;
            _dataTable.IsVisible = _draft.CanShowDataTable;
            _dataTableKeys.IsVisible = _draft.CanShowDataTable && _draft.ShowDataTable;
            _showCategoryAxis.IsVisible = kind != ChartKind.Radar;
            UpdateLabelPositions();
            _markers.IsVisible = anyLine;
            _smooth.IsVisible = anyLine && kind != ChartKind.Radar;
            _lines.IsVisible = kind == ChartKind.Scatter;
            _radarFilled.IsVisible = kind == ChartKind.Radar;
            _holeRow.IsVisible = kind == ChartKind.Doughnut;
            _angleRow.IsVisible = circular;
            _gapRow.IsVisible = kind is ChartKind.Column or ChartKind.Bar
                || (kind is ChartKind.Line or ChartKind.Area && _draft.Series.Exists(s => s.Kind == ChartKind.Column));

            _axesCard.IsVisible = !circular;
            _categoryAxisLabel.Text = IsXY(kind) ? "Ось X" : kind == ChartKind.Bar ? "Ось категорий (слева)" : "Ось категорий";
            _valueAxisLabel.Text = IsXY(kind) ? "Ось Y" : "Ось значений";
            _categoryAxisLabel.IsVisible = kind != ChartKind.Radar;
            _categoryAxisTitle.IsVisible = kind != ChartKind.Radar;
            _xScalePanel.IsVisible = IsXY(kind);
            _categoryAxisBlock.IsVisible = kind != ChartKind.Radar;
            _axesDivider.IsVisible = kind != ChartKind.Radar;
            _secondaryPanel.IsVisible = BuildDraftHasSecondary();

            _swapButton.IsEnabled = !IsXY(kind);

            // Подсказка таблицы разметкой подсказок (TooltipView): *выделение* и строки.
            // Сверху — как таблица читается для этого вида диаграммы, ниже — как в ней работать.
            string layout = kind switch
            {
                ChartKind.Pie => "*Строки* — доли, *столбец B* — их величины.\n"
                                 + "Круговая показывает только первый ряд.\n"
                                 + "Цвет доли — в карточке «Доли».",
                ChartKind.Doughnut => "*Строки* — доли, *каждый столбец* — своё кольцо.\n"
                                      + "Цвет доли — в карточке «Доли».",
                ChartKind.Scatter => "*Столбец A* — значения X.\n"
                                     + "*Остальные столбцы* — ряды, в них значения Y.\n"
                                     + "*Строка 1* — имена рядов.",
                ChartKind.Bubble => "*Столбец A* — значения X.\n"
                                    + "У каждого ряда *два столбца*: Y и размер пузырька.",
                ChartKind.Radar => "*Строки* — лучи из центра, нужно не меньше трёх.\n"
                                   + "*Столбцы* — ряды.",
                ChartKind.Stock => "*Ряды по порядку*: открытие (если есть), максимум, минимум, закрытие.\n"
                                   + "*Строки* — дни.",
                _ => "*Столбец A* — подписи категорий.\n"
                     + "*Строка 1* — имена рядов.\n"
                     + "*Столбцы B, C…* — ряды, *строки* — их значения."
            };

            TooltipBehavior.SetDescription(_dataHint, layout
                + "\n\nПустая ячейка — нет точки, красная — не число."
                + "\n\nПечатайте прямо в ячейку:"
                + "\n*Enter* — вниз, *Tab* — вправо"
                + "\n*F2* — править текст, *Delete* — очистить"
                + "\n*Ctrl+V* — вставить таблицу из Excel или Word"
                + "\n\nВвод в пустую строку или столбец в конце добавляет категорию или ряд."
                + "\n*Правая кнопка* — вставка и удаление строк и рядов.");
        }

        // ── Анализ ───────────────────────────────────────────────────────

        private TextBox SmallNumber(string placeholder)
        {
            var box = Input();
            box.PlaceholderText = placeholder;
            box.HorizontalContentAlignment = HorizontalAlignment.Right;
            return box;
        }

        /// <summary>Ряды, к которым относятся правки карточки «Анализ»: выбранный или все, где анализ бывает.</summary>
        private List<int> AnalysisTargets()
        {
            int selected = _analysisSeries.SelectedIndex;
            if (selected > 0 && selected - 1 < _analysisIndexes.Count) return new List<int> { _analysisIndexes[selected - 1] };
            return new List<int>(_analysisIndexes);
        }

        private void EditTrend(Func<ChartTrendline?, ChartTrendline?> change, bool coalesce = false)
        {
            var targets = AnalysisTargets();
            Edit("trend" + _analysisSeries.SelectedIndex, d =>
            {
                foreach (int i in targets)
                    if (i < d.Series.Count) d.Series[i].Trendline = change(d.Series[i].Trendline?.Clone());
            }, coalesce);
        }

        private void EditErrors(Func<ChartErrorBars?, ChartErrorBars?> change, bool coalesce = false)
        {
            var targets = AnalysisTargets();
            Edit("errors" + _analysisSeries.SelectedIndex, d =>
            {
                foreach (int i in targets)
                    if (i < d.Series.Count) d.Series[i].ErrorBars = change(d.Series[i].ErrorBars?.Clone());
            }, coalesce);
        }

        /// <summary>
        /// Показывает анализ: список рядов (при rebuildList — заново) и тренд с
        /// погрешностями первого из выбранных рядов.
        /// </summary>
        private void RefreshAnalysis(bool rebuildList)
        {
            if (_analysisCard is null) return;

            bool loading = _loading;
            _loading = true;
            try
            {
                if (rebuildList)
                {
                    int keep = _analysisSeries.SelectedIndex;
                    _analysisIndexes.Clear();
                    var names = new List<string> { "Все ряды" };
                    for (int s = 0; s < _draft.Series.Count; s++)
                    {
                        if (!ChartModel.KindHasTrendline(_draft.KindOf(_draft.Series[s]))) continue;
                        _analysisIndexes.Add(s);
                        string name = s < SeriesCount ? _sheet.Get(0, ValueColumn(s)).Trim() : _draft.Series[s].Name;
                        names.Add(name.Length > 0 ? name : "Ряд " + (s + 1));
                    }
                    _analysisSeries.ItemsSource = names;
                    _analysisSeries.SelectedIndex = keep > 0 && keep < names.Count ? keep : 0;
                }

                var targets = AnalysisTargets();
                var sample = targets.Count > 0 && targets[0] < _draft.Series.Count ? _draft.Series[targets[0]] : null;
                var trend = sample?.Trendline;
                var errors = sample?.ErrorBars;

                _trendType.SelectedIndex = trend is null ? 0 : (int)trend.Type + 1;
                _trendOrder.Text = trend?.Order.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
                _trendPeriod.Text = trend?.Period.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
                _trendForward.Text = trend is { Forward: > 0 } ? FormatNumber(trend.Forward) : string.Empty;
                _trendBackward.Text = trend is { Backward: > 0 } ? FormatNumber(trend.Backward) : string.Empty;
                _trendEquation.IsChecked = trend?.ShowEquation ?? false;
                _trendRSquared.IsChecked = trend?.ShowRSquared ?? false;

                _errorType.SelectedIndex = errors is null ? 0 : (int)errors.Type + 1;
                _errorValue.Text = errors is null ? string.Empty : FormatNumber(errors.Value);

                _dropLines.IsChecked = _draft.ShowDropLines;
                _highLowLines.IsChecked = _draft.ShowHighLowLines;
                _upDownBars.IsChecked = _draft.ShowUpDownBars;
            }
            finally
            {
                _loading = loading;
            }

            var trendNow = _trendType.SelectedIndex - 1;
            bool hasTrend = trendNow >= 0;
            _trendOrderRow.IsVisible = trendNow == (int)ChartTrendlineType.Polynomial;
            _trendPeriodRow.IsVisible = trendNow == (int)ChartTrendlineType.MovingAverage;
            _trendForecastRow.IsVisible = hasTrend && trendNow != (int)ChartTrendlineType.MovingAverage;
            _trendEquation.IsVisible = hasTrend && trendNow != (int)ChartTrendlineType.MovingAverage;
            _trendRSquared.IsVisible = _trendEquation.IsVisible;

            int errorNow = _errorType.SelectedIndex - 1;
            _errorValueRow.IsVisible = errorNow > (int)ChartErrorBarType.StandardError;
            _errorValueLabel.Text = errorNow switch
            {
                (int)ChartErrorBarType.Percentage => "Процент",
                (int)ChartErrorBarType.StandardDeviation => "Отклонений",
                _ => "Величина"
            };

            bool lineSeries = _draft.HasLineSeries;
            _dropLines.IsVisible = lineSeries || Kind == ChartKind.Area;
            _highLowLines.IsVisible = lineSeries && _draft.Series.Count >= 2;
            _upDownBars.IsVisible = lineSeries && _draft.Series.Count >= 2;
            _analysisCard.IsVisible = _analysisIndexes.Count > 0 || _dropLines.IsVisible;
        }

        /// <summary>
        /// Список мест подписей значений для нынешнего вида: «Как у Word» и места,
        /// которые Word допускает. У вида без выбора строки нет.
        /// </summary>
        private void UpdateLabelPositions()
        {
            var grouping = _draft.HasGrouping ? _draft.Grouping : ChartGrouping.Standard;
            var allowed = ChartModel.LabelPositionsFor(Kind, grouping);

            bool loading = _loading;
            _loading = true;
            try
            {
                _labelPositions.Clear();
                _labelPositions.Add(ChartLabelPosition.Auto);
                _labelPositions.AddRange(allowed);
                _labelPosition.ItemsSource = _labelPositions.Select(ChartModel.LabelPositionName).ToList();
                var current = _draft.LabelPositionFor(Kind);
                _labelPosition.SelectedIndex = Math.Max(0, _labelPositions.IndexOf(current));
            }
            finally
            {
                _loading = loading;
            }

            _labelPositionRow.IsVisible = _draft.ShowDataLabels && _labelPositions.Count > 1;
        }

        private bool BuildDraftHasSecondary()
        {
            if (!CanCombine(Kind)) return false;
            bool primary = false, secondary = false;
            for (int s = 0; s < Math.Min(SeriesCount, _draft.Series.Count); s++)
            {
                if (_draft.Series[s].SecondaryAxis) secondary = true;
                else primary = true;
            }
            return primary && secondary;
        }

        private bool IsComboDraft()
        {
            if (!CanCombine(Kind)) return false;
            foreach (var series in _draft.Series)
                if (series.SecondaryAxis || (series.Kind is ChartKind k && k != Kind)) return true;
            return false;
        }

        private void PaintKindItems()
        {
            var accent = BrushOf("AccentDefaultBrush", Color.FromRgb(0x00, 0x7A, 0xCC));
            var accentColor = accent is ISolidColorBrush solid ? solid.Color : Color.FromRgb(0x00, 0x7A, 0xCC);
            var selectedBack = new SolidColorBrush(Color.FromArgb(0x40, accentColor.R, accentColor.G, accentColor.B));
            var hoverBack = BrushOf("BgHoverBrush", Color.FromRgb(0x3A, 0x3A, 0x3A));
            bool combo = IsComboDraft();

            foreach (var (entry, item, label) in _kindItems)
            {
                bool selected = entry.Combo ? combo : !combo && entry.Kind == Kind;
                item.Background = selected ? selectedBack : item.IsPointerOver ? hoverBack : Brushes.Transparent;
                item.BorderThickness = new Thickness(selected ? 1 : 0);
                item.BorderBrush = selected ? accent : null;
                label.FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal;
            }
        }

        private void SchedulePreview()
        {
            _previewTimer.Stop();
            _previewTimer.Start();
        }

        private void UpdatePreview()
        {
            try
            {
                _preview.Chart = BuildChart();
            }
            catch
            {
                _preview.Chart = null;
            }
        }

        // ── Вид ──────────────────────────────────────────────────────────

        private void SelectKindEntry(KindEntry entry)
        {
            _grid.CommitPendingEdit();

            if (entry.Combo)
            {
                if (_state.IsCombo()) return;
                PushUndo();
                _state = ChartEditorState.AsCombo(_state);
                RefreshAll();
                return;
            }

            if (entry.Kind == Kind && !_state.IsCombo()) return;

            PushUndo();
            _state = ChartEditorState.WithKind(_state, entry.Kind);
            RefreshAll();
        }

        // ── Таблица ──────────────────────────────────────────────────────

        private ChartCellRole RoleOf(int row, int column)
        {
            if (row == 0)
            {
                if (column == 0) return ChartCellRole.Corner;
                return IsSizeColumn(column) ? ChartCellRole.SizeHeader : ChartCellRole.SeriesName;
            }

            if (column == 0) return IsXY(Kind) ? ChartCellRole.XValue : ChartCellRole.Category;
            return IsSizeColumn(column) ? ChartCellRole.Size : ChartCellRole.Value;
        }

        private string? PlaceholderOf(int row, int column)
        {
            bool ghostRow = row >= _sheet.RowCount;
            bool ghostColumn = column >= _sheet.ColumnCount;

            if (row == 0 && ghostColumn) return "+ новый ряд";
            if (row == 0 && IsSizeColumn(column)) return "размер";
            if (ghostRow && column == 0) return IsXY(Kind) ? "+ точка" : "+ категория";
            return null;
        }

        private double ColumnWidthOf(int column)
        {
            if (column == 0) return ChartDataGrid.FirstColumnWidth;
            if (column < _sheet.ColumnCount && IsSizeColumn(column)) return ChartDataGrid.SizeColumnWidth;
            return ChartDataGrid.ValueColumnWidth;
        }

        /// <summary>
        /// После правки из таблицы: лист мог вырасти вводом в пустую строку или столбец.
        /// Новому ряду — имя и свойства, у пузырьковой — ещё столбец размеров; списку
        /// цветов долей — столько же мест, сколько строк.
        /// </summary>
        private void OnGridChanged()
        {
            NormalizeSheet();
            _grid.Sheet = _sheet;
            RebuildSeriesPanel();
            UpdateKindControls();
            UpdateDataButtons();
            SchedulePreview();
        }

        private void NormalizeSheet() => _state.Normalize();

        private string UniqueSeriesName(int index) => _state.UniqueSeriesName(index);

        private void UpdateDataButtons()
        {
            var (top, left, bottom, right) = _grid.Selection;
            _removeRowButton.IsEnabled = SelectedDataRows(top, bottom).Count > 0 && CategoryCount > 1;
            _removeSeriesButton.IsEnabled = SelectedSeries(left, right).Count > 0 && SeriesCount > 1;
        }

        private List<int> SelectedDataRows(int top, int bottom)
        {
            var rows = new List<int>();
            for (int r = Math.Max(1, top); r <= Math.Min(bottom, _sheet.RowCount - 1); r++) rows.Add(r);
            return rows;
        }

        private List<int> SelectedSeries(int left, int right)
        {
            var series = new List<int>();
            for (int c = Math.Max(1, left); c <= Math.Min(right, _sheet.ColumnCount - 1); c++)
            {
                int s = SeriesOfColumn(c);
                if (s >= 0 && s < SeriesCount && !series.Contains(s)) series.Add(s);
            }
            return series;
        }

        private void InsertRows(bool below)
        {
            _grid.CommitPendingEdit();
            var (top, _, bottom, _) = _grid.Selection;
            int at = below ? Math.Min(_sheet.RowCount, Math.Max(1, bottom + 1)) : Math.Max(1, top);
            int count = Math.Max(1, SelectedDataRows(top, bottom).Count);
            if (CategoryCount + count > 500) return;

            PushUndo();
            _state.InsertRows(at, count);
            AfterStructureChange();
            // Сразу правка подписи новой категории — каретка в ней, можно печатать.
            _grid.EditCell(at, 0, selectAll: true);
        }

        private void RemoveSelectedRows()
        {
            _grid.CommitPendingEdit();
            var (top, _, bottom, _) = _grid.Selection;
            var rows = SelectedDataRows(top, bottom);
            if (rows.Count == 0 || CategoryCount <= 1) return;

            PushUndo();
            _state.RemoveRows(rows);
            AfterStructureChange();
            _grid.SelectCell(Math.Min(rows[0], _sheet.RowCount - 1), 0);
        }

        private void InsertSeries(bool right)
        {
            _grid.CommitPendingEdit();
            var (_, left, _, rightColumn) = _grid.Selection;
            var selected = SelectedSeries(left, rightColumn);
            int at = selected.Count == 0
                ? SeriesCount
                : right ? selected.Max() + 1 : selected.Min();
            if (SeriesCount >= 60) return;

            PushUndo();
            int column = _state.InsertSeries(at);
            AfterStructureChange();
            // Сразу правка имени нового ряда — каретка в нём, можно печатать.
            _grid.EditCell(0, column, selectAll: true);
        }

        private void RemoveSelectedSeries()
        {
            _grid.CommitPendingEdit();
            var (_, left, _, right) = _grid.Selection;
            var series = SelectedSeries(left, right);
            if (series.Count == 0 || SeriesCount <= 1) return;

            PushUndo();
            int first = series.Min();
            _state.RemoveSeries(series);
            AfterStructureChange();
            _grid.SelectCell(0, Math.Min(ValueColumn(Math.Min(first, SeriesCount - 1)), _sheet.ColumnCount - 1));
        }

        private void Transpose()
        {
            _grid.CommitPendingEdit();
            if (IsXY(Kind)) return;
            PushUndo();
            _state = ChartEditorState.Transposed(_state);
            RefreshAll();
        }

        private void ClearCells()
        {
            _grid.ClearSelection();
        }

        private void AfterStructureChange()
        {
            NormalizeSheet();
            _grid.Sheet = _sheet;
            RebuildSeriesPanel();
            UpdateKindControls();
            UpdateDataButtons();
            SchedulePreview();
        }

        private void ShowGridMenu(ChartGridHit hit)
        {
            var (top, left, bottom, right) = _grid.Selection;
            bool rowsSelected = SelectedDataRows(top, bottom).Count > 0;
            bool seriesSelected = SelectedSeries(left, right).Count > 0;

            var items = new List<Control>
            {
                MenuItem("Вставить строку выше", () => InsertRows(below: false), rowsSelected),
                MenuItem("Вставить строку ниже", () => InsertRows(below: true), true),
                MenuItem("Удалить строки", RemoveSelectedRows, rowsSelected && CategoryCount > 1),
                new Separator(),
                MenuItem("Вставить ряд слева", () => InsertSeries(right: false), seriesSelected),
                MenuItem("Вставить ряд справа", () => InsertSeries(right: true), true),
                MenuItem("Удалить ряды", RemoveSelectedSeries, seriesSelected && SeriesCount > 1),
                new Separator(),
                MenuItem("Вырезать", () => _ = _grid.CopySelectionAsync(cut: true), true, "Ctrl+X"),
                MenuItem("Копировать", () => _ = _grid.CopySelectionAsync(cut: false), true, "Ctrl+C"),
                MenuItem("Вставить", () => _ = _grid.PasteFromClipboardAsync(), true, "Ctrl+V"),
                MenuItem("Очистить", ClearCells, true, "Delete")
            };

            // MenuFlyout, а не отдельно открытый ContextMenu: тот в окне программы не показывается.
            var menu = new MenuFlyout { ItemsSource = items, Placement = PlacementMode.Pointer };
            menu.ShowAt(_grid, showAtPointer: true);
        }

        private static MenuItem MenuItem(string header, Action action, bool enabled, string? gesture = null)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            if (gesture is not null) item.InputGesture = KeyGesture.Parse(gesture);
            item.Click += (_, _) => action();
            return item;
        }

        // ── Ряды и доли ──────────────────────────────────────────────────

        private void RebuildSeriesPanel()
        {
            _seriesList.Children.Clear();
            var kind = Kind;

            if (IsCircular(kind))
            {
                _seriesCardTitle.Text = "Доли";
                TooltipBehavior.SetDescription(_seriesHint, "Доли — строки таблицы. Набор цветов сверху перекрашивает все доли разом; «+» под наборами заводит свой набор, шестерёнка настраивает свои наборы. "
                    + "Цвет доли — кружком слева, правая кнопка по нему подбирает свой цвет.");
                for (int r = 0; r < CategoryCount; r++)
                {
                    int row = r;
                    string label = _sheet.Get(r + 1, 0);
                    string? own = r < _pointColors.Count ? _pointColors[r] : null;
                    var reset = ResetButton(own is not null);
                    var picker = ColorPicker(own ?? ChartModel.PaletteColor(r), hex =>
                    {
                        Edit("point" + row, _ =>
                        {
                            while (_pointColors.Count <= row) _pointColors.Add(null);
                            _pointColors[row] = hex;
                        }, coalesce: true);
                        reset.IsVisible = hex is not null;
                    });
                    reset.Click += (_, _) =>
                    {
                        Edit("pointReset" + row, _ => { if (row < _pointColors.Count) _pointColors[row] = null; });
                        SetPickerSilently(picker, ChartModel.PaletteColor(row));
                        reset.IsVisible = false;
                    };
                    var labelBox = NameBox(label, "Доля " + (r + 1), "pointName" + row, r + 1, 0, "Название доли — то же, что в столбце A таблицы");
                    _seriesList.Children.Add(SeriesRow(picker, labelBox, null, null, reset));
                }
                UpdateSchemeChips();
                RefreshAnalysis(rebuildList: true);
                return;
            }

            _seriesCardTitle.Text = "Ряды";
            bool combo = CanCombine(kind) && SeriesCount > 1;
            for (int s = 0; s < SeriesCount; s++)
            {
                int index = s;
                var meta = s < _draft.Series.Count ? _draft.Series[s] : new ChartSeries();
                string name = _sheet.Get(0, ValueColumn(s));

                var reset = ResetButton(meta.ColorHex is not null);
                var picker = ColorPicker(meta.ColorHex ?? ChartModel.PaletteColor(s), hex =>
                {
                    Edit("color" + index, d => { if (index < d.Series.Count) { d.Series[index].ColorHex = hex; d.Series[index].PointColors = null; } }, coalesce: true);
                    reset.IsVisible = hex is not null;
                });
                reset.Click += (_, _) =>
                {
                    Edit("colorReset" + index, d => { if (index < d.Series.Count) d.Series[index].ColorHex = null; });
                    SetPickerSilently(picker, ChartModel.PaletteColor(index));
                    reset.IsVisible = false;
                };

                ComboBox? kindPicker = null;
                ToggleButton? secondary = null;
                if (combo)
                {
                    kindPicker = Combo(SeriesKinds.Select(k => k.Label).ToArray());
                    kindPicker.MinWidth = 96;
                    kindPicker.FontSize = 11.5;
                    var effective = meta.Kind ?? kind;
                    kindPicker.SelectedIndex = Math.Max(0, Array.FindIndex(SeriesKinds, k => k.Kind == effective));
                    kindPicker.SelectionChanged += (_, _) =>
                    {
                        if (_loading || kindPicker.SelectedIndex < 0) return;
                        var chosen = SeriesKinds[kindPicker.SelectedIndex].Kind;
                        Edit("seriesKind" + index, d =>
                        {
                            if (index >= d.Series.Count) return;
                            var series = d.Series[index];
                            series.Kind = chosen == d.Kind ? null : chosen;
                            if (chosen == ChartKind.Line && meta.Kind != ChartKind.Line && d.Kind != ChartKind.Line) series.ShowMarkers = false;
                        });
                    };

                    var axisToggle = new ToggleButton
                    {
                        Classes = { AccentToggleClass },
                        IsChecked = meta.SecondaryAxis,
                        Width = 26,
                        Height = 26,
                        MinHeight = 0,
                        Padding = new Thickness(0),
                        Margin = new Thickness(4, 0, 4, 0),
                        HorizontalContentAlignment = HorizontalAlignment.Center,
                        VerticalContentAlignment = VerticalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Content = new PathIcon { Data = StreamGeometry.Parse(SecondaryAxisIcon), Width = 14, Height = 14 }
                    };
                    TooltipBehavior.SetTip(axisToggle, "Ось справа");
                    TooltipBehavior.SetDescription(axisToggle, "Ряд по вспомогательной оси — со своей шкалой справа. Нужна, когда ряды сильно разные по величине.");
                    axisToggle.IsCheckedChanged += (_, _) =>
                    {
                        if (_loading) return;
                        Edit("secondary" + index, d => { if (index < d.Series.Count) d.Series[index].SecondaryAxis = axisToggle.IsChecked == true; });
                    };
                    secondary = axisToggle;
                }

                var nameBox = NameBox(name, "Ряд " + (s + 1), "seriesName" + index, 0, ValueColumn(s), "Имя ряда — то же, что в строке 1 таблицы; оно стоит в легенде");
                _seriesList.Children.Add(SeriesRow(picker, nameBox, kindPicker, secondary, reset));
            }

            TooltipBehavior.SetDescription(_seriesHint, combo
                ? "Набор цветов сверху перекрашивает все ряды разом; «+» под наборами заводит свой набор, шестерёнка настраивает свои наборы. Имя ряда пишется прямо здесь или в строке 1 таблицы. Цвет — кружком слева, "
                  + "правая кнопка по нему подбирает свой цвет. Свой вид ряда (столбцы, линия, область) и кнопка оси справа делают "
                  + "диаграмму комбинированной."
                : "Набор цветов сверху перекрашивает все ряды разом; «+» под наборами заводит свой набор, шестерёнка настраивает свои наборы. Имя ряда пишется прямо здесь или в строке 1 таблицы. Цвет — кружком слева, "
                  + "правая кнопка по нему подбирает свой цвет.");
            UpdateSchemeChips();
            RefreshAnalysis(rebuildList: true);
        }

        private void SetPickerSilently(ColorPickerButton picker, string hex)
        {
            bool loading = _loading;
            _loading = true;
            try { picker.HexColor = NormalizeHex(hex); }
            finally { _loading = loading; }
        }

        /// <summary>
        /// Строка ряда (у круговой — доли) одной линией: цвет, сброс цвета, имя, кнопка
        /// оси справа и вид ряда — последние два только у комбинированной.
        /// </summary>
        private Control SeriesRow(Control picker, Control name, ComboBox? kindPicker, ToggleButton? secondary, Control reset)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,26,*,Auto,Auto") };
            Place(row, picker, 0, 0);
            Place(row, reset, 0, 1);
            Place(row, name, 0, 2);
            if (secondary is not null) Place(row, secondary, 0, 3);
            if (kindPicker is not null) Place(row, kindPicker, 0, 4);
            return row;
        }

        /// <summary>
        /// Поле имени ряда или доли. Имя живёт в таблице (строка 1 у рядов, столбец A у
        /// долей), поле пишет прямо туда: правка видна в таблице и в легенде сразу, а
        /// набор подряд — один шаг отмены. Пустое поле показывает имя по умолчанию серым.
        /// </summary>
        private TextBox NameBox(string text, string placeholder, string key, int sheetRow, int sheetColumn, string tip)
        {
            var box = Input();
            box.Text = text;
            box.PlaceholderText = placeholder;
            box.MinHeight = 26;
            box.Height = 26;
            box.Padding = new Thickness(6, 3);
            box.Margin = new Thickness(4, 0, 0, 0);
            box.VerticalAlignment = VerticalAlignment.Center;
            box.VerticalContentAlignment = VerticalAlignment.Center;
            TooltipBehavior.SetTip(box, tip);

            box.TextChanged += (_, _) =>
            {
                if (_loading || _pending is null) return;
                string value = box.Text ?? string.Empty;
                if (_sheet.Get(sheetRow, sheetColumn) == value) return;

                var now = DateTime.UtcNow;
                bool merge = _coalesceKey == key && (now - _coalesceTime).TotalMilliseconds < 900;
                if (!merge)
                {
                    _undo.Add(Capture());
                    if (_undo.Count > 200) _undo.RemoveAt(0);
                    _redo.Clear();
                    UpdateUndoButtons();
                }
                _coalesceKey = key;
                _coalesceTime = now;

                _sheet.Set(sheetRow, sheetColumn, value);
                _grid.InvalidateVisual();
                SchedulePreview();
            };
            return box;
        }

        private ColorPickerButton ColorPicker(string hex, Action<string?> apply)
        {
            var picker = new ColorPickerButton
            {
                Width = 22,
                Height = 22,
                VerticalAlignment = VerticalAlignment.Center,
                HexColor = NormalizeHex(hex)
            };
            TooltipBehavior.SetTip(picker, "Цвет. Правая кнопка — подбор своего цвета");
            picker.PropertyChanged += (_, e) =>
            {
                if (e.Property != ColorPickerButton.HexColorProperty || _loading) return;
                string? value = e.NewValue as string;
                if (string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), "#00000000", StringComparison.OrdinalIgnoreCase))
                    apply(null);
                else
                    apply(NormalizeHex(value));
            };
            return picker;
        }

        private Button ResetButton(bool visible)
        {
            var button = new Button
            {
                Classes = { "chartIcon" },
                Width = 22,
                Height = 22,
                MinHeight = 0,
                Padding = new Thickness(0),
                Margin = new Thickness(4, 0, 0, 0),
                IsVisible = visible,
                Content = new PathIcon { Data = StreamGeometry.Parse(ResetIcon), Width = 11, Height = 11 }
            };
            TooltipBehavior.SetTip(button, "Вернуть цвет по умолчанию");
            return button;
        }

        /// <summary>
        /// Код цвета подборщика как есть: «#RRGGBB», «#AARRGGBB» с прозрачностью или
        /// градиент «grad|…» (линейный, радиальный, конический) — диаграмма рисует и
        /// сохраняет в .docx их все.
        /// </summary>
        private static string NormalizeHex(string hex)
        {
            string value = hex.Trim();
            if (ChartModel.IsGradient(value)) return value;
            value = value.TrimStart('#');
            if (value.Length == 8 && value.StartsWith("FF", StringComparison.OrdinalIgnoreCase)) value = value[2..];
            return "#" + value.ToUpperInvariant();
        }

        // ── Наборы цветов ────────────────────────────────────────────────

        /// <summary>
        /// Наборы цветов полосой образцов прямо в карточке рядов: готовые, затем свои и
        /// «+», который сохраняет нынешние цвета рядов своим набором. Щелчок
        /// перекрашивает все ряды (у круговой и кольцевой — доли). Набор, которым
        /// раскрашена диаграмма сейчас, обведён.
        /// </summary>
        private void RebuildSchemeStrip()
        {
            _schemeStrip.Children.Clear();
            _schemeChips.Clear();

            for (int i = 0; i < ChartModel.ColorSchemes.Count; i++)
            {
                var (name, palette) = ChartModel.ColorSchemes[i];
                string key = "b" + i.ToString(CultureInfo.InvariantCulture);
                _schemeStrip.Children.Add(SchemeChip(key, name, palette, "Перекрасить все ряды этим набором; у круговой и кольцевой — все доли", () =>
                {
                    _activeSchemeKey = key;
                    ShowSchemeEditor(null);
                    ApplyScheme(palette);
                }));
            }

            foreach (var set in _userSets)
            {
                var own = set;
                string key = "u" + own.Id;
                _schemeStrip.Children.Add(SchemeChip(key, string.IsNullOrWhiteSpace(own.Name) ? "Свой набор" : own.Name, own.Colors,
                    "Свой набор: перекрасить все ряды им. Настроить — шестерёнкой в конце полосы", () =>
                    {
                        _activeSchemeKey = key;
                        ApplyScheme(own.Colors);
                    }));
            }

            var plus = StripButton(PlusIcon);
            TooltipBehavior.SetTip(plus, "Свой набор");
            TooltipBehavior.SetDescription(plus, "Завести свой набор из нынешних цветов рядов (у круговой — долей) и открыть его настройку. Свои наборы видны во всех проектах.");
            plus.Click += (_, _) => AddUserSet();
            _schemeStrip.Children.Add(plus);

            if (_userSets.Count > 0)
            {
                var gear = StripButton(GearIcon);
                TooltipBehavior.SetTip(gear, "Настроить свои наборы");
                TooltipBehavior.SetDescription(gear, "Отдельное окно своих наборов: имя, цвета по одному, их порядок и число.");
                gear.Click += (_, _) => OpenSetsManager(_userSets.FirstOrDefault(u => "u" + u.Id == _activeSchemeKey) ?? _userSets[0]);
                _schemeStrip.Children.Add(gear);
            }

            UpdateSchemeChips();
        }

        /// <summary>Образец набора: полоса его цветов; цветов больше десяти — полоса сжимается.</summary>
        private Button SchemeChip(string key, string name, IReadOnlyList<string> colors, string description, Action apply)
        {
            var swatches = new StackPanel { Orientation = Orientation.Horizontal };
            int shown = Math.Min(colors.Count, 12);
            double width = shown <= 6 ? 8 : Math.Max(4, 56.0 / shown);
            for (int i = 0; i < shown; i++)
            {
                swatches.Children.Add(new Border { Width = width, Height = 14, Background = SwatchBrush(colors[i]) });
            }

            var clip = new Border { CornerRadius = new CornerRadius(3), ClipToBounds = true, Child = swatches };
            var frame = new Border
            {
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(2),
                Child = clip
            };
            Bind(frame, Border.BorderBrushProperty, "BorderSubtleBrush");

            var chip = new Button
            {
                Classes = { "chartTool" },
                Content = frame,
                Padding = new Thickness(2),
                MinHeight = 0,
                Margin = new Thickness(0, 0, 4, 4),
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            TooltipBehavior.SetTip(chip, name);
            TooltipBehavior.SetDescription(chip, description);
            chip.Click += (_, _) => apply();

            _schemeChips.Add((key, colors, frame));
            return chip;
        }

        /// <summary>Нынешние цвета рядов, а у круговой и кольцевой — долей.</summary>
        private List<string> CurrentColors()
        {
            var used = new List<string>();
            if (IsCircular(Kind))
            {
                for (int r = 0; r < CategoryCount; r++)
                    used.Add((r < _pointColors.Count ? _pointColors[r] : null) ?? ChartModel.PaletteColor(r));
            }
            else
            {
                for (int s = 0; s < _draft.Series.Count; s++)
                    used.Add(_draft.Series[s].ColorHex ?? ChartModel.PaletteColor(s));
            }
            return used.Select(NormalizeHex).Take(ChartColorSets.MaxColors).ToList();
        }

        private void AddUserSet()
        {
            var colors = CurrentColors();
            if (colors.Count == 0) return;

            int number = 1;
            while (_userSets.Any(s => s.Name == "Мой набор " + number)) number++;
            var set = new ChartColorSet { Name = "Мой набор " + number, Colors = colors };
            PushSetsHistory("new");
            _userSets.Add(set);
            ChartColorSets.Save(_userSets);

            _activeSchemeKey = "u" + set.Id;
            RebuildSchemeStrip();
            OpenSetsManager(set);
            Dispatcher.UIThread.Post(() =>
            {
                _schemeName.Focus();
                _schemeName.SelectAll();
            }, DispatcherPriority.Input);
        }

        /// <summary>Открывает окошко своих наборов с набором set.</summary>
        private void OpenSetsManager(ChartColorSet? set)
        {
            if (_pending is null) return;
            _grid.CommitPendingEdit();
            _setsLayer.IsVisible = true;
            ShowSchemeEditor(set);
            Dispatcher.UIThread.Post(KeepSetsFocus, DispatcherPriority.Input);
        }

        /// <summary>
        /// Фокус внутри окошка наборов: после перестройки строк кнопка с фокусом исчезает,
        /// и без этого Ctrl+Z и Ctrl+Y больше не доходили бы до окошка.
        /// </summary>
        private void KeepSetsFocus()
        {
            if (!_setsLayer.IsVisible) return;
            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;
            bool inside = focused is not null && (ReferenceEquals(focused, _setsPanel) || _setsPanel.IsVisualAncestorOf(focused));
            if (!inside) _setsPanel.Focus();
        }

        private void CloseSetsManager()
        {
            if (!_setsLayer.IsVisible) return;
            RenameEditingSet();
            if (_setsSaveTimer.IsEnabled)
            {
                _setsSaveTimer.Stop();
                ChartColorSets.Save(_userSets);
            }
            _setsLayer.IsVisible = false;
            ShowSchemeEditor(null);
            _grid.Focus();
        }

        /// <summary>Открывает свой набор для правки в окошке наборов; null — правки нет.</summary>
        private void ShowSchemeEditor(ChartColorSet? set)
        {
            _editingSet = set;
            _schemeEditor.IsVisible = set is not null;
            if (set is not null) _schemeName.Text = set.Name;
            RebuildSetColors();
            RebuildSetsList();
        }

        /// <summary>Свои наборы образцами вверху окошка; открытый обведён.</summary>
        private void RebuildSetsList()
        {
            _setsList.Children.Clear();
            _setsEmpty.IsVisible = _userSets.Count == 0;

            foreach (var set in _userSets)
            {
                var own = set;
                var swatches = new StackPanel { Orientation = Orientation.Horizontal };
                int shown = Math.Min(own.Colors.Count, 12);
                double width = shown <= 6 ? 8 : Math.Max(4, 56.0 / shown);
                for (int i = 0; i < shown; i++)
                {
                    swatches.Children.Add(new Border { Width = width, Height = 14, Background = SwatchBrush(own.Colors[i]) });
                }
                var name = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(own.Name) ? "Свой набор" : own.Name,
                    FontSize = 12,
                    MaxWidth = 140,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 2, 0)
                };
                Bind(name, TextBlock.ForegroundProperty, "TextPrimaryBrush");
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                content.Children.Add(new Border { CornerRadius = new CornerRadius(3), ClipToBounds = true, Child = swatches, VerticalAlignment = VerticalAlignment.Center });
                content.Children.Add(name);

                var frame = new Border
                {
                    BorderThickness = new Thickness(1.5),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(4, 3),
                    Child = content
                };
                Bind(frame, Border.BorderBrushProperty, ReferenceEquals(own, _editingSet) ? "AccentDefaultBrush" : "BorderSubtleBrush");

                var chip = new Button
                {
                    Classes = { "chartTool" },
                    Content = frame,
                    Padding = new Thickness(2),
                    MinHeight = 0,
                    Margin = new Thickness(0, 0, 4, 4),
                    Cursor = new Cursor(StandardCursorType.Hand)
                };
                TooltipBehavior.SetTip(chip, "Настроить этот набор");
                chip.Click += (_, _) =>
                {
                    RenameEditingSet();
                    ShowSchemeEditor(own);
                };
                _setsList.Children.Add(chip);
            }
        }

        /// <summary>
        /// Цвета открытого набора строками: ручка для перетаскивания, номер (какой по
        /// счёту ряд получит цвет), стандартная кнопка цвета и крестик. Порядок меняется
        /// перетаскиванием строки за ручку: остальные строки раздвигаются, показывая,
        /// куда она встанет.
        /// </summary>
        private void RebuildSetColors()
        {
            _schemeColors.Children.Clear();
            if (_editingSet is not { } set) return;

            var rows = new List<Control>();
            for (int i = 0; i < set.Colors.Count; i++)
            {
                int index = i;

                var grip = new Border
                {
                    Width = 18,
                    Height = 24,
                    Background = Brushes.Transparent,
                    Cursor = new Cursor(StandardCursorType.SizeNorthSouth),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new PathIcon { Data = StreamGeometry.Parse(DragIcon), Width = 12, Height = 12 }
                };
                Bind(grip.Child, PathIcon.ForegroundProperty, "TextMutedBrush");
                TooltipBehavior.SetTip(grip, "Перетащите, чтобы поменять порядок");

                var number = FieldLabel((i + 1).ToString(CultureInfo.CurrentCulture));
                number.Width = 20;
                number.TextAlignment = TextAlignment.Right;
                number.Margin = new Thickness(0, 0, 8, 0);

                var picker = FillPicker(set.Colors[i], "Цвет " + (i + 1) + ". Правая кнопка — свой цвет, прозрачность, градиент", value =>
                {
                    if (_editingSet != set || index >= set.Colors.Count || value is null) return;
                    PushSetsHistory("color" + set.Id + index, coalesce: true);
                    set.Colors[index] = value;
                    SetChanged(set, rebuildRows: false);
                });

                var remove = SmallIconButton(CloseIcon, "Убрать цвет из набора");
                remove.IsEnabled = set.Colors.Count > 1;
                remove.Click += (_, _) =>
                {
                    if (set.Colors.Count <= 1 || index >= set.Colors.Count) return;
                    PushSetsHistory("remove");
                    set.Colors.RemoveAt(index);
                    SetChanged(set, rebuildRows: true);
                };

                var row = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto"),
                    Background = Brushes.Transparent
                };
                Place(row, grip, 0, 0);
                Place(row, number, 0, 1);
                Place(row, picker, 0, 2);
                Place(row, remove, 0, 4);
                rows.Add(row);
                _schemeColors.Children.Add(row);

                WireColorDrag(grip, row, index, rows, set);
            }

            _schemeAddColor.IsEnabled = set.Colors.Count < ChartColorSets.MaxColors;
        }

        /// <summary>
        /// Перетаскивание строки цвета за ручку. Пока тянут, строка едет за указателем,
        /// а соседние сдвигаются на её место плавно; по отпускании цвет встаёт туда,
        /// куда показывали.
        /// </summary>
        private void WireColorDrag(Border grip, Control row, int index, List<Control> rows, ChartColorSet set)
        {
            double startY = 0, step = 0;
            int target = index;
            bool dragging = false;

            void Reset()
            {
                foreach (var other in rows)
                {
                    other.RenderTransform = null;
                    other.ZIndex = 0;
                    other.Opacity = 1;
                }
            }

            grip.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
                dragging = true;
                startY = e.GetPosition(_schemeColors).Y;
                step = row.Bounds.Height + _schemeColors.Spacing;
                target = index;
                row.ZIndex = 10;
                row.Opacity = 0.9;
                foreach (var other in rows)
                {
                    if (ReferenceEquals(other, row)) continue;
                    other.Transitions = new Transitions
                    {
                        new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(120) }
                    };
                }
                e.Pointer.Capture(grip);
                e.Handled = true;
            };

            grip.PointerMoved += (_, e) =>
            {
                if (!dragging || step <= 0) return;
                double dy = e.GetPosition(_schemeColors).Y - startY;
                double minDy = -index * step, maxDy = (rows.Count - 1 - index) * step;
                dy = Math.Clamp(dy, minDy, maxDy);
                row.RenderTransform = TransformOperations.Parse("translateY(" + dy.ToString("0.##", CultureInfo.InvariantCulture) + "px)");

                target = Math.Clamp(index + (int)Math.Round(dy / step), 0, rows.Count - 1);
                for (int k = 0; k < rows.Count; k++)
                {
                    if (k == index) continue;
                    double shift = k > index && k <= target ? -step : k < index && k >= target ? step : 0;
                    rows[k].RenderTransform = TransformOperations.Parse("translateY(" + shift.ToString("0.##", CultureInfo.InvariantCulture) + "px)");
                }
                e.Handled = true;
            };

            grip.PointerReleased += (_, e) =>
            {
                if (!dragging) return;
                dragging = false;
                e.Pointer.Capture(null);
                Reset();
                if (target != index && index < set.Colors.Count)
                {
                    PushSetsHistory("move");
                    string color = set.Colors[index];
                    set.Colors.RemoveAt(index);
                    set.Colors.Insert(Math.Clamp(target, 0, set.Colors.Count), color);
                    SetChanged(set, rebuildRows: true);
                }
                e.Handled = true;
            };

            grip.PointerCaptureLost += (_, _) =>
            {
                if (!dragging) return;
                dragging = false;
                Reset();
            };
        }

        private void AddSetColor()
        {
            if (_editingSet is not { } set || set.Colors.Count >= ChartColorSets.MaxColors) return;
            PushSetsHistory("add");
            set.Colors.Add(NormalizeHex(ChartModel.PaletteColor(set.Colors.Count)));
            SetChanged(set, rebuildRows: true);
        }

        /// <summary>
        /// Набор поменялся: записать (с задержкой, пока цвет тянут), обновить полосу и
        /// сразу перекрасить ряды, если диаграмма раскрашена этим набором.
        /// </summary>
        private void SetChanged(ChartColorSet set, bool rebuildRows)
        {
            _setsSaveTimer.Stop();
            _setsSaveTimer.Start();

            if (_activeSchemeKey == "u" + set.Id)
                ApplyScheme(set.Colors, coalesce: true);
            RebuildSchemeStrip();
            RebuildSetsList();
            if (rebuildRows)
            {
                RebuildSetColors();
                KeepSetsFocus();
            }
        }

        // ── История своих наборов ────────────────────────────────────────
        // У окошка наборов своя отмена: Ctrl+Z и Ctrl+Y в нём отменяют и возвращают
        // правки наборов — цвет, порядок, число цветов, имя, новый и удалённый набор.

        /// <summary>Шаг истории наборов: все наборы и какой из них был открыт.</summary>
        private sealed record SetsSnapshot(List<ChartColorSet> Sets, string? EditingId);

        private readonly List<SetsSnapshot> _setsUndo = new();
        private readonly List<SetsSnapshot> _setsRedo = new();
        private string? _setsCoalesceKey;
        private DateTime _setsCoalesceTime;

        private SetsSnapshot SnapshotSets() => new(_userSets.Select(s => s.Clone()).ToList(), _editingSet?.Id);

        /// <summary>Запоминает наборы до правки. Правки одного цвета подряд без паузы — один шаг.</summary>
        private void PushSetsHistory(string key, bool coalesce = false)
        {
            var now = DateTime.UtcNow;
            bool merge = coalesce && _setsCoalesceKey == key && (now - _setsCoalesceTime).TotalMilliseconds < 900;
            _setsCoalesceKey = coalesce ? key : null;
            _setsCoalesceTime = now;
            if (merge) return;

            _setsUndo.Add(SnapshotSets());
            if (_setsUndo.Count > 200) _setsUndo.RemoveAt(0);
            _setsRedo.Clear();
        }

        private static string SetKey(ChartColorSet set) => set.Name + "\u0001" + string.Join("|", set.Colors);

        private void UndoSets() => StepSets(_setsUndo, _setsRedo);

        private void RedoSets() => StepSets(_setsRedo, _setsUndo);

        /// <summary>
        /// Шаг отмены или возврата. Открывается тот набор, который этот шаг поменял:
        /// вернули удалённый или созданный набор — он и открыт, поменяли цвета — тоже.
        /// Если наборы не различаются, открыт тот, что был открыт в том состоянии.
        /// </summary>
        private void StepSets(List<SetsSnapshot> from, List<SetsSnapshot> to)
        {
            if (from.Count == 0) return;
            var before = _userSets;
            to.Add(SnapshotSets());
            var target = from[^1];
            from.RemoveAt(from.Count - 1);
            _userSets = target.Sets;
            _setsCoalesceKey = null;

            ChartColorSets.Save(_userSets);
            var changed = _userSets.FirstOrDefault(set =>
                before.FirstOrDefault(old => old.Id == set.Id) is not { } old || SetKey(old) != SetKey(set));
            var editing = changed
                          ?? _userSets.FirstOrDefault(s => s.Id == target.EditingId)
                          ?? _userSets.FirstOrDefault();
            if (editing is not null && _activeSchemeKey == "u" + editing.Id)
                ApplyScheme(editing.Colors, coalesce: true);
            RebuildSchemeStrip();
            ShowSchemeEditor(editing);
            KeepSetsFocus();
        }

        /// <summary>
        /// Кнопка в конце полосы наборов («+», шестерёнка): значок в рамке ростом с образец
        /// набора, чтобы кнопки стояли с образцами в один ряд и выглядели одинаково.
        /// </summary>
        private Button StripButton(string icon)
        {
            var frame = new Border
            {
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(2),
                Width = 28,
                Height = 22,
                Child = new PathIcon { Data = StreamGeometry.Parse(icon), Width = 11, Height = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            Bind(frame, Border.BorderBrushProperty, "BorderSubtleBrush");

            return new Button
            {
                Classes = { "chartTool" },
                Padding = new Thickness(2),
                MinHeight = 0,
                Margin = new Thickness(0, 0, 4, 4),
                Cursor = new Cursor(StandardCursorType.Hand),
                Content = frame
            };
        }

        private static Button SmallIconButton(string icon, string tip)
        {
            var button = new Button
            {
                Classes = { "chartIcon" },
                Width = 24,
                Height = 24,
                MinHeight = 0,
                Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Content = new PathIcon { Data = StreamGeometry.Parse(icon), Width = 12, Height = 12 }
            };
            TooltipBehavior.SetTip(button, tip);
            return button;
        }

        private void RenameEditingSet()
        {
            if (_editingSet is not { } set) return;
            string name = _schemeName.Text?.Trim() ?? string.Empty;
            if (name.Length == 0 || name == set.Name) return;
            PushSetsHistory("rename");
            set.Name = name;
            ChartColorSets.Save(_userSets);
            RebuildSchemeStrip();
            RebuildSetsList();
        }

        private void UpdateEditingSet()
        {
            if (_editingSet is not { } set) return;
            var colors = CurrentColors();
            if (colors.Count == 0) return;
            PushSetsHistory("update");
            set.Colors = colors;
            ChartColorSets.Save(_userSets);
            _activeSchemeKey = "u" + set.Id;
            RebuildSchemeStrip();
            RebuildSetsList();
            RebuildSetColors();
        }

        private void DeleteEditingSet()
        {
            if (_editingSet is not { } set) return;
            int index = _userSets.IndexOf(set);
            PushSetsHistory("delete");
            _userSets.Remove(set);
            ChartColorSets.Save(_userSets);
            RebuildSchemeStrip();
            ShowSchemeEditor(_userSets.Count == 0 ? null : _userSets[Math.Clamp(index, 0, _userSets.Count - 1)]);
        }

        private void ApplyScheme(IReadOnlyList<string> palette, bool coalesce = false)
        {
            if (_pending is null || palette.Count == 0) return;
            _grid.CommitPendingEdit();

            bool circular = IsCircular(Kind);
            int count = CategoryCount;
            Edit("scheme", d =>
            {
                if (circular)
                {
                    while (_pointColors.Count < count) _pointColors.Add(null);
                    for (int i = 0; i < _pointColors.Count; i++) _pointColors[i] = palette[i % palette.Count];
                }
                else
                {
                    for (int s = 0; s < d.Series.Count; s++)
                    {
                        d.Series[s].ColorHex = palette[s % palette.Count];
                        d.Series[s].PointColors = null;
                    }
                }
            }, coalesce);
            RebuildSeriesPanel();
        }

        /// <summary>Обводит набор, которым раскрашена диаграмма сейчас.</summary>
        private void UpdateSchemeChips()
        {
            var used = CurrentColors();

            bool Matches(IReadOnlyList<string> colors)
            {
                if (used.Count == 0 || colors.Count == 0) return false;
                for (int i = 0; i < used.Count; i++)
                    if (!SameColor(used[i], colors[i % colors.Count])) return false;
                return true;
            }

            // Обводится один набор: тот, по которому щёлкнули, если цвета ещё его;
            // иначе первый с такими цветами.
            string? active = null;
            foreach (var (key, colors, _) in _schemeChips)
                if (key == _activeSchemeKey && Matches(colors)) { active = key; break; }
            if (active is null)
                foreach (var (key, colors, _) in _schemeChips)
                    if (Matches(colors)) { active = key; break; }

            foreach (var (key, _, frame) in _schemeChips)
                Bind(frame, Border.BorderBrushProperty, key == active ? "AccentDefaultBrush" : "BorderSubtleBrush");
        }

        /// <summary>
        /// Кисть образца цвета: сплошной с прозрачностью или градиент любого вида —
        /// линейный, радиальный, конический, — той же фабрикой, что у подборщика цвета.
        /// </summary>
        public static IBrush SwatchBrush(string? code)
        {
            if (string.IsNullOrWhiteSpace(code)) return Brushes.Transparent;
            try
            {
                if (ChartModel.IsGradient(code)) return Writersword.Infrastructure.Converters.GradientBrushFactory.FromCode(code);
            }
            catch
            {
                // Код градиента не разобрался — образец его первой точкой.
            }
            var (a, r, g, b) = ChartModel.ParseArgb(code);
            return new SolidColorBrush(Color.FromArgb((byte)a, (byte)r, (byte)g, (byte)b));
        }

        private static bool SameColor(string a, string b)
            => string.Equals(NormalizeHex(a), NormalizeHex(b), StringComparison.OrdinalIgnoreCase);

        // ── Надписи ──────────────────────────────────────────────────────

        /// <summary>
        /// Строка одной надписи: подпись и сброс сверху, под ними шрифт, размер,
        /// жирный, курсив и цвет.
        /// </summary>
        private TextRow BuildTextRow(ChartTextRole role, string label)
        {
            var row = new TextRow { Role = role };

            row.FontText = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Bind(row.FontText, TextBlock.ForegroundProperty, "TextPrimaryBrush");
            // Поле шрифта обведено, как поле размера рядом: видно, что это поле, по
            // которому щёлкают, а не подпись.
            var fontField = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 3),
                MinHeight = 26,
                Child = row.FontText
            };
            Bind(fontField, Border.BorderBrushProperty, "BorderDefaultBrush");
            var fontButton = new Button
            {
                Classes = { "chartTool" },
                Content = fontField,
                FontSize = 12,
                Padding = new Thickness(0),
                MinHeight = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center
            };
            TooltipBehavior.SetTip(fontButton, "Шрифт");
            fontButton.Click += (_, _) => PickFont(fontButton, "textFont" + role,
                d => d.StyleOf(role)?.FontFamily,
                (d, family) => ChangeStyle(d, role, s => s.FontFamily =
                    string.IsNullOrWhiteSpace(family) || string.Equals(family, BaseFontOf(d), StringComparison.OrdinalIgnoreCase) ? null : family));

            row.Size = new TextBox
            {
                FontSize = 12,
                Width = 52,
                MinHeight = 26,
                Padding = new Thickness(6, 3),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                PlaceholderText = FormatSize(ChartModel.DefaultSizeOf(role))
            };
            TooltipBehavior.SetTip(row.Size, "Размер, пт");
            TooltipBehavior.SetDescription(row.Size, "Любое число, например 10,5. Пустое поле — размер по умолчанию: "
                + FormatSize(ChartModel.DefaultSizeOf(role)) + ". Стрелки вверх и вниз меняют размер на пункт.");
            row.Size.TextChanged += (_, _) =>
            {
                if (_loading) return;
                string text = row.Size.Text?.Trim() ?? string.Empty;
                double? size;
                if (text.Length == 0) size = null;
                else if (TryParseSize(text, out double value)) size = value;
                else return;
                Edit("textSize" + role, d => ChangeStyle(d, role, s => s.SizePt = size), coalesce: true);
                UpdateTextReset(row);
            };
            row.Size.KeyDown += (_, e) =>
            {
                if (e.Key is not (Key.Up or Key.Down)) return;
                double current = _draft.StyleOf(role)?.SizePt ?? ChartModel.DefaultSizeOf(role);
                double next = Math.Clamp(Math.Floor(current) + (e.Key == Key.Up ? 1 : (current % 1 == 0 ? -1 : 0)), 1, 400);
                row.Size.Text = FormatSize(next);
                row.Size.CaretIndex = row.Size.Text.Length;
                e.Handled = true;
            };

            row.Bold = StyleToggle("Ж", FontWeight.Bold, FontStyle.Normal, "Жирный");
            row.Bold.IsCheckedChanged += (_, _) =>
            {
                if (_loading) return;
                bool bold = row.Bold.IsChecked == true;
                Edit("textBold" + role, d => ChangeStyle(d, role, s => s.Bold = bold == ChartModel.DefaultBoldOf(role) ? null : bold));
                UpdateTextReset(row);
            };
            row.Italic = StyleToggle("К", FontWeight.Normal, FontStyle.Italic, "Курсив");
            row.Italic.IsCheckedChanged += (_, _) =>
            {
                if (_loading) return;
                bool italic = row.Italic.IsChecked == true;
                Edit("textItalic" + role, d => ChangeStyle(d, role, s => s.Italic = italic ? true : null));
                UpdateTextReset(row);
            };

            row.Color = FillPicker(ChartModel.DefaultTextColor, "Цвет надписи. Правая кнопка — подбор своего цвета", value =>
            {
                string? color = value is null || SameColor(value, ChartModel.DefaultTextColor) ? null : value;
                Edit("textColor" + role, d => ChangeStyle(d, role, s => s.ColorHex = color), coalesce: true);
                UpdateTextReset(row);
            });

            row.Reset = ResetButton(false);
            TooltipBehavior.SetTip(row.Reset, "Вернуть оформление по умолчанию");
            row.Reset.Click += (_, _) =>
            {
                Edit("textReset" + role, d => d.SetStyle(role, null));
                RefreshTextStyle();
            };

            // Кнопка сброса всегда стоит в разметке и только прячется прозрачностью:
            // появляясь, она не меняет высоту строки. Отрицательные поля — чтобы она,
            // будучи выше подписи, не раздвигала строки и в спрятанном виде.
            row.Reset.IsVisible = true;
            row.Reset.Opacity = 0;
            row.Reset.IsHitTestVisible = false;
            row.Reset.Focusable = false;
            row.Reset.Margin = new Thickness(4, -4, 0, -4);
            var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Place(head, FieldLabel(label), 0, 0);
            Place(head, row.Reset, 0, 1);

            var fields = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto"), ColumnSpacing = 4 };
            Place(fields, fontButton, 0, 0);
            Place(fields, row.Size, 0, 1);
            Place(fields, row.Bold, 0, 2);
            Place(fields, row.Italic, 0, 3);
            row.Color.Margin = new Thickness(2, 0, 0, 0);
            Place(fields, row.Color, 0, 4);

            row.Root = Stack(3, head, fields);
            return row;
        }

        private static ToggleButton StyleToggle(string text, FontWeight weight, FontStyle style, string tip)
        {
            var toggle = new ToggleButton
            {
                Classes = { AccentToggleClass },
                Content = new TextBlock { Text = text, FontWeight = weight, FontStyle = style, FontSize = 12 },
                Width = 26,
                Height = 26,
                MinHeight = 0,
                Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            TooltipBehavior.SetTip(toggle, tip);
            return toggle;
        }

        /// <summary>Шрифт, которым пишутся надписи без своего шрифта: шрифт диаграммы или Calibri.</summary>
        private static string BaseFontOf(ChartModel chart)
            => string.IsNullOrWhiteSpace(chart.FontFamily) ? DefaultChartFont : chart.FontFamily!;

        private static string FormatSize(double size) => size.ToString("0.##", CultureInfo.CurrentCulture);

        private static bool TryParseSize(string text, out double size)
        {
            string normalized = text.Replace(',', '.').Replace(" ", string.Empty);
            bool ok = double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out size);
            if (!ok || double.IsNaN(size) || size < 1 || size > 400) return false;
            size = Math.Round(size * 2, MidpointRounding.AwayFromZero) / 2;
            return true;
        }

        /// <summary>Меняет оформление надписей рода role в модели; пустое снимается.</summary>
        private static void ChangeStyle(ChartModel draft, ChartTextRole role, Action<ChartTextStyle> change)
        {
            var style = draft.StyleOf(role)?.Clone() ?? new ChartTextStyle();
            change(style);
            draft.SetStyle(role, style);
        }

        /// <summary>Показывает оформление всех надписей черновика.</summary>
        private void RefreshTextStyle()
        {
            bool loading = _loading;
            _loading = true;
            try
            {
                foreach (var row in _textRows)
                {
                    var style = _draft.StyleOf(row.Role);
                    string family = string.IsNullOrWhiteSpace(style?.FontFamily) ? BaseFontOf(_draft) : style!.FontFamily!;
                    row.FontText.Text = family;
                    row.FontText.FontFamily = new FontFamily(family);

                    // Поле, в котором сейчас печатают, не перебивается: «10,» ещё не число.
                    if (!row.Size.IsFocused)
                        row.Size.Text = style?.SizePt is double size ? FormatSize(size) : string.Empty;

                    row.Bold.IsChecked = style?.Bold ?? ChartModel.DefaultBoldOf(row.Role);
                    row.Italic.IsChecked = style?.Italic ?? false;
                    row.Color.HexColor = NormalizeHex(style?.ColorHex ?? ChartModel.DefaultTextColor);
                }
            }
            finally
            {
                _loading = loading;
            }

            foreach (var row in _textRows) UpdateTextReset(row);
            UpdateTextRowsVisibility();
        }

        private void UpdateTextReset(TextRow row)
        {
            bool shown = _draft.StyleOf(row.Role) is not null;
            row.Reset.Opacity = shown ? 1 : 0;
            row.Reset.IsHitTestVisible = shown;
            row.Reset.Focusable = shown;
        }

        /// <summary>У круговой и кольцевой осей нет — и строк их надписей тоже.</summary>
        private void UpdateTextRowsVisibility()
        {
            bool axes = !IsCircular(Kind);
            foreach (var row in _textRows)
                row.Root.IsVisible = axes || row.Role is not (ChartTextRole.AxisLabels or ChartTextRole.AxisTitles);
        }

        /// <summary>
        /// Выбор шрифта стандартным списком гарнитур редактора. Пока список открыт,
        /// образец показывает шрифт под мышью; в историю окна уходит только выбор.
        /// </summary>
        private async void PickFont(Control anchor, string key, Func<ChartModel, string?> read, Action<ChartModel, string?> write)
        {
            if (_pending is null) return;

            var view = this.FindAncestorOfType<TextEditorView>();
            var picker = view?.FindControl<FontPickerOverlay>("FontOverlay")
                         ?? view?.GetVisualDescendants().OfType<FontPickerOverlay>().FirstOrDefault();
            if (picker is null) return;

            _grid.CommitPendingEdit();
            string? original = read(_draft);
            string current = string.IsNullOrWhiteSpace(original) ? BaseFontOf(_draft) : original!;
            var session = _pending;

            string? chosen = await picker.ShowAsync(ProjectFonts.PickerFamilies(current), current, anchor, family =>
            {
                if (!ReferenceEquals(_pending, session)) return;
                write(_draft, family);
                SchedulePreview();
            });

            if (!ReferenceEquals(_pending, session)) return;

            write(_draft, original);
            if (chosen is not null)
            {
                Edit(key, d => write(d, chosen));
                FontUsage.NoteUsed(chosen);
            }
            else
            {
                SchedulePreview();
            }
            RefreshTextStyle();
        }

        // ── Фон ──────────────────────────────────────────────────────────

        /// <summary>
        /// Кнопка цвета заливки: цвет, прозрачность или градиент. apply получает код
        /// цвета; null — выбрано «без цвета».
        /// </summary>
        private ColorPickerButton FillPicker(string hex, string tip, Action<string?> apply)
        {
            var picker = new ColorPickerButton
            {
                Width = 22,
                Height = 22,
                VerticalAlignment = VerticalAlignment.Center,
                HexColor = NormalizeHex(hex)
            };
            TooltipBehavior.SetTip(picker, tip);
            picker.PropertyChanged += (_, e) =>
            {
                if (e.Property != ColorPickerButton.HexColorProperty || _loading || _pending is null) return;
                string? value = e.NewValue as string;
                if (string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), "#00000000", StringComparison.OrdinalIgnoreCase))
                    apply(null);
                else
                    apply(NormalizeHex(value));
            };
            return picker;
        }

        /// <summary>Показывает заливки и картинку фона черновика.</summary>
        private void RefreshBackground()
        {
            if (_backgroundColor is null) return;

            bool loading = _loading;
            _loading = true;
            try
            {
                _backgroundColor.HexColor = NormalizeHex(_draft.BackgroundFill ?? "#FFFFFF");
                _plotColor.HexColor = _draft.PlotAreaFill is { } plot ? NormalizeHex(plot) : "#00000000";
            }
            finally
            {
                _loading = loading;
            }

            _backgroundReset.IsVisible = _draft.BackgroundFill is not null;
            _plotReset.IsVisible = _draft.PlotAreaFill is not null;
            _pictureButton.IsEnabled = _storeImage is not null;
            _pictureRemove.IsVisible = !string.IsNullOrEmpty(_draft.BackgroundImage);
            TooltipBehavior.SetTip(_pictureButton, string.IsNullOrEmpty(_draft.BackgroundImage)
                ? "Поставить картинку фоном диаграммы: растягивается на всю диаграмму поверх заливки"
                : "Заменить картинку фона");
        }

        private async Task PickBackgroundPicture()
        {
            if (_pending is null || _storeImage is null) return;

            var storage = TopLevel.GetTopLevel(this)?.StorageProvider
                          ?? (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.StorageProvider;
            if (storage is null) return;

            var session = _pending;
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Картинка фона диаграммы",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Изображения") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp" } }
                }
            });
            if (!ReferenceEquals(_pending, session) || files.Count == 0) return;

            string? path = files[0].TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            string? stored = _storeImage(path);
            if (string.IsNullOrEmpty(stored)) return;

            LoadPicture(stored, path);
            Edit("picture", d => d.BackgroundImage = stored);
            RefreshBackground();
            _grid.Focus();
        }

        /// <summary>Готовит картинку фона для образца: из файла на диске или из хранилища проекта.</summary>
        private void LoadPicture(string fileName, string? localPath = null)
        {
            if (_pictures.ContainsKey(fileName)) return;

            byte[]? data = null;
            try
            {
                data = localPath is not null ? System.IO.File.ReadAllBytes(localPath) : _readImage?.Invoke(fileName);
            }
            catch
            {
                data = null;
            }

            SKImage? image = null;
            if (data is { Length: > 0 })
            {
                try { image = SKImage.FromEncodedData(data); }
                catch { image = null; }
            }
            _pictures[fileName] = image;
        }

        private SKImage? PictureOf(string fileName)
            => _pictures.TryGetValue(fileName, out var image) ? image : null;

        // ── Пределы и форматы осей ───────────────────────────────────────

        private static string? NullIfEmpty(string? text)
        {
            string value = text?.Trim() ?? string.Empty;
            return value.Length > 0 ? value : null;
        }

        private static double? ParseLimit(TextBox box)
            => ChartDataGrid.TryNumber(box.Text, out double value) ? value : null;

        private TextBox LimitInput()
        {
            var box = Input();
            box.PlaceholderText = "авто";
            box.HorizontalContentAlignment = HorizontalAlignment.Right;
            box.TextChanged += (_, _) =>
            {
                bool bad = !string.IsNullOrWhiteSpace(box.Text) && !ChartDataGrid.TryNumber(box.Text, out _);
                box.Classes.Set("invalid", bad);
            };
            return box;
        }

        private Control LimitsRow(TextBox min, TextBox max)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,*"), ColumnSpacing = 6 };
            Place(grid, FieldLabel("Мин."), 0, 0);
            Place(grid, min, 0, 1);
            Place(grid, FieldLabel("Макс."), 0, 2);
            Place(grid, max, 0, 3);
            return grid;
        }

        private ComboBox FormatPicker(List<string?> codes)
        {
            codes.Clear();
            var labels = new List<string>();
            foreach (var (label, code) in NumberFormats)
            {
                labels.Add(label);
                codes.Add(code);
            }
            var picker = Combo(labels.ToArray());
            picker.Tag = labels;
            return picker;
        }

        /// <summary>
        /// Ставит формат в выбор. Формат из документа, которого нет в списке, встаёт
        /// в список отдельным пунктом — чтобы окно его не теряло.
        /// </summary>
        private static void SelectFormat(ComboBox picker, List<string?> codes, string? current)
        {
            if (string.IsNullOrEmpty(current) || current == "General")
            {
                picker.SelectedIndex = 0;
                return;
            }

            int index = codes.IndexOf(current);
            if (index < 0)
            {
                codes.Add(current);
                var labels = picker.Tag as List<string> ?? new List<string>();
                labels.Add(current);
                picker.ItemsSource = null;
                picker.ItemsSource = labels.ToArray();
                picker.Tag = labels;
                index = codes.Count - 1;
            }
            picker.SelectedIndex = index;
        }

        private static string? SelectedFormat(ComboBox picker, List<string?> codes)
            => picker.SelectedIndex >= 0 && picker.SelectedIndex < codes.Count ? codes[picker.SelectedIndex] : null;

        // ── Элементы окна ────────────────────────────────────────────────

        private void AddDialogStyles()
        {
            // Кнопка удаления: красная, с корзиной. Цвет держит шаблон кнопки, поэтому
            // стили — на его ContentPresenter, как у остальных кнопок приложения.
            var dangerBack = new SolidColorBrush(Color.FromArgb(0x26, 0xE5, 0x39, 0x35));
            var dangerHover = new SolidColorBrush(Color.FromArgb(0x4D, 0xE5, 0x39, 0x35));
            var dangerPressed = new SolidColorBrush(Color.FromArgb(0x73, 0xE5, 0x39, 0x35));
            var dangerBorder = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
            var dangerText = new SolidColorBrush(Color.FromRgb(0xF0, 0x5A, 0x55));

            Styles.Add(new Style(x => x.OfType<Button>().Class("chartDanger"))
            {
                Setters =
                {
                    new Setter(Button.ForegroundProperty, dangerText),
                    new Setter(Button.BorderBrushProperty, dangerBorder)
                }
            });
            Styles.Add(new Style(x => x.OfType<Button>().Class("chartDanger").Template().OfType<ContentPresenter>())
            {
                Setters =
                {
                    new Setter(ContentPresenter.BackgroundProperty, dangerBack),
                    new Setter(ContentPresenter.BorderBrushProperty, dangerBorder)
                }
            });
            Styles.Add(new Style(x => x.OfType<Button>().Class("chartDanger").Class(":pointerover").Template().OfType<ContentPresenter>())
            {
                Setters =
                {
                    new Setter(ContentPresenter.BackgroundProperty, dangerHover),
                    new Setter(ContentPresenter.BorderBrushProperty, dangerBorder)
                }
            });
            Styles.Add(new Style(x => x.OfType<Button>().Class("chartDanger").Class(":pressed").Template().OfType<ContentPresenter>())
            {
                Setters =
                {
                    new Setter(ContentPresenter.BackgroundProperty, dangerPressed),
                    new Setter(ContentPresenter.BorderBrushProperty, dangerBorder)
                }
            });

            AddAccentToggleStyles(Styles);
            AddRibbonButtonStyles(Styles);

            // Кнопки панели таблицы и значков: прозрачные, подсветка при наведении.
            foreach (var cls in new[] { "chartTool", "chartIcon" })
            {
                Styles.Add(new Style(x => x.OfType<Button>().Class(cls).Template().OfType<ContentPresenter>())
                {
                    Setters =
                    {
                        new Setter(ContentPresenter.BackgroundProperty, Brushes.Transparent),
                        new Setter(ContentPresenter.BorderBrushProperty, Brushes.Transparent)
                    }
                });
                Styles.Add(new Style(x => x.OfType<Button>().Class(cls).Class(":pointerover").Template().OfType<ContentPresenter>())
                {
                    Setters =
                    {
                        new Setter(ContentPresenter.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x33, 0x80, 0x80, 0x80))),
                        new Setter(ContentPresenter.BorderBrushProperty, Brushes.Transparent)
                    }
                });
                Styles.Add(new Style(x => x.OfType<Button>().Class(cls).Class(":disabled"))
                {
                    Setters = { new Setter(Visual.OpacityProperty, 0.4) }
                });
            }
        }

        /// <summary>Класс переключателя в виде квадратных переключателей ленты (SegSquare).</summary>
        public const string AccentToggleClass = "chartSeg";

        /// <summary>
        /// Стили переключателей <see cref="AccentToggleClass"/> — те же, что у квадратных
        /// переключателей ленты «Диаграмма» (ToggleButton.SegSquare): фон поверхности и
        /// тонкая рамка; при наведении рамка цветом акцента; включённый — подложка
        /// AccentSubtleBrush, рамка и текст цветом акцента. Стили ленты объявлены в её
        /// разметке и сюда не доходят, поэтому повторены здесь один в один.
        /// </summary>
        /// <summary>Класс кнопки в виде кнопки ленты (Button.RibbonBtn).</summary>
        public const string RibbonButtonClass = "chartRibbonBtn";

        /// <summary>
        /// Стили кнопок <see cref="RibbonButtonClass"/> — те же, что у кнопок ленты
        /// (Button.RibbonBtn): фон поверхности и тонкая рамка, при наведении рамка цветом
        /// акцента, при нажатии подложка AccentPressedBrush.
        /// </summary>
        public static void AddRibbonButtonStyles(Avalonia.Styling.Styles styles)
        {
            Selector Ribbon(Selector? x) => x.OfType<Button>().Class(RibbonButtonClass);

            var root = new Style(x => Ribbon(x));
            root.Setters.Add(new Setter(TemplatedControl.ForegroundProperty, new DynamicResourceExtension("TextPrimaryBrush")));
            root.Setters.Add(new Setter(InputElement.CursorProperty, new Cursor(StandardCursorType.Hand)));
            root.Setters.Add(new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            root.Setters.Add(new Setter(ContentControl.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            styles.Add(root);

            var presenter = new Style(x => Ribbon(x).Template().OfType<ContentPresenter>());
            presenter.Setters.Add(new Setter(ContentPresenter.BackgroundProperty, new DynamicResourceExtension("BgSurfaceBrush")));
            presenter.Setters.Add(new Setter(ContentPresenter.BorderBrushProperty, new DynamicResourceExtension("BorderSubtleBrush")));
            presenter.Setters.Add(new Setter(ContentPresenter.BorderThicknessProperty, new Thickness(1)));
            presenter.Setters.Add(new Setter(ContentPresenter.CornerRadiusProperty, new CornerRadius(3)));
            styles.Add(presenter);

            var hover = new Style(x => Ribbon(x).Class(":pointerover").Template().OfType<ContentPresenter>());
            hover.Setters.Add(new Setter(ContentPresenter.BackgroundProperty, new DynamicResourceExtension("BgHoverBrush")));
            hover.Setters.Add(new Setter(ContentPresenter.BorderBrushProperty, new DynamicResourceExtension("AccentDefaultBrush")));
            styles.Add(hover);

            var pressed = new Style(x => Ribbon(x).Class(":pressed").Template().OfType<ContentPresenter>());
            pressed.Setters.Add(new Setter(ContentPresenter.BackgroundProperty, new DynamicResourceExtension("AccentPressedBrush")));
            pressed.Setters.Add(new Setter(ContentPresenter.BorderBrushProperty, new DynamicResourceExtension("AccentDefaultBrush")));
            styles.Add(pressed);

            var disabled = new Style(x => Ribbon(x).Class(":disabled"));
            disabled.Setters.Add(new Setter(Visual.OpacityProperty, 0.45));
            styles.Add(disabled);
        }

        public static void AddAccentToggleStyles(Avalonia.Styling.Styles styles)
        {
            Selector Toggle(Selector? x) => x.OfType<ToggleButton>().Class(AccentToggleClass);

            var root = new Style(x => Toggle(x));
            root.Setters.Add(new Setter(TemplatedControl.ForegroundProperty, new DynamicResourceExtension("TextPrimaryBrush")));
            root.Setters.Add(new Setter(InputElement.CursorProperty, new Cursor(StandardCursorType.Hand)));
            styles.Add(root);

            var presenter = new Style(x => Toggle(x).Template().OfType<ContentPresenter>());
            presenter.Setters.Add(new Setter(ContentPresenter.BackgroundProperty, new DynamicResourceExtension("BgSurfaceBrush")));
            presenter.Setters.Add(new Setter(ContentPresenter.BorderBrushProperty, new DynamicResourceExtension("BorderSubtleBrush")));
            presenter.Setters.Add(new Setter(ContentPresenter.BorderThicknessProperty, new Thickness(1)));
            presenter.Setters.Add(new Setter(ContentPresenter.CornerRadiusProperty, new CornerRadius(3)));
            styles.Add(presenter);

            var hover = new Style(x => Toggle(x).Class(":pointerover").Template().OfType<ContentPresenter>());
            hover.Setters.Add(new Setter(ContentPresenter.BackgroundProperty, new DynamicResourceExtension("BgHoverBrush")));
            hover.Setters.Add(new Setter(ContentPresenter.BorderBrushProperty, new DynamicResourceExtension("AccentDefaultBrush")));
            styles.Add(hover);

            var pressed = new Style(x => Toggle(x).Class(":pressed").Template().OfType<ContentPresenter>());
            pressed.Setters.Add(new Setter(ContentPresenter.BackgroundProperty, new DynamicResourceExtension("BgHoverBrush")));
            pressed.Setters.Add(new Setter(ContentPresenter.BorderBrushProperty, new DynamicResourceExtension("AccentDefaultBrush")));
            styles.Add(pressed);

            var on = new Style(x => Toggle(x).Class(":checked").Template().OfType<ContentPresenter>());
            on.Setters.Add(new Setter(ContentPresenter.BackgroundProperty, new DynamicResourceExtension("AccentSubtleBrush")));
            on.Setters.Add(new Setter(ContentPresenter.BorderBrushProperty, new DynamicResourceExtension("AccentDefaultBrush")));
            on.Setters.Add(new Setter(ContentPresenter.ForegroundProperty, new DynamicResourceExtension("AccentDefaultBrush")));
            styles.Add(on);

            var onHover = new Style(x => Toggle(x).Class(":checked").Class(":pointerover").Template().OfType<ContentPresenter>());
            onHover.Setters.Add(new Setter(ContentPresenter.BackgroundProperty, new DynamicResourceExtension("BgHoverBrush")));
            onHover.Setters.Add(new Setter(ContentPresenter.BorderBrushProperty, new DynamicResourceExtension("AccentDefaultBrush")));
            styles.Add(onHover);

            var onIcon = new Style(x => Toggle(x).Class(":checked").Descendant().OfType<PathIcon>());
            onIcon.Setters.Add(new Setter(PathIcon.ForegroundProperty, new DynamicResourceExtension("AccentDefaultBrush")));
            styles.Add(onIcon);

            var disabled = new Style(x => Toggle(x).Class(":disabled"));
            disabled.Setters.Add(new Setter(Visual.OpacityProperty, 0.45));
            styles.Add(disabled);
        }

        private IBrush BrushOf(string key, Color fallback)
        {
            if (this.TryFindResource(key, ActualThemeVariant, out var value))
            {
                if (value is IBrush brush) return brush;
                if (value is Color color) return new SolidColorBrush(color);
            }
            return new SolidColorBrush(fallback);
        }

        private void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
            => target.Bind(property, this.GetResourceObservable(key));

        private static void Place(Grid grid, Control control, int row, int column)
        {
            Grid.SetRow(control, row);
            Grid.SetColumn(control, column);
            grid.Children.Add(control);
        }

        private static StackPanel Stack(double spacing, params Control[] children)
        {
            var stack = new StackPanel { Spacing = spacing };
            foreach (var child in children) stack.Children.Add(child);
            return stack;
        }

        private TextBlock SectionLabel(string text)
        {
            var label = new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(6, 0, 0, 6),
                VerticalAlignment = VerticalAlignment.Center
            };
            Bind(label, TextBlock.ForegroundProperty, "TextMutedBrush");
            return label;
        }

        private TextBlock CardTitle(string text)
        {
            var title = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeight.SemiBold };
            Bind(title, TextBlock.ForegroundProperty, "TextPrimaryBrush");
            return title;
        }

        private Border Card(string title, params Control[] content)
            => CardWithTitle(CardTitle(title), content);

        /// <summary>Значок пояснения (!) — стандартный HintMark со своей подсказкой.</summary>
        private static HintMark Hint(string tip, string description)
        {
            var mark = new HintMark { VerticalAlignment = VerticalAlignment.Center };
            TooltipBehavior.SetTip(mark, tip);
            if (description.Length > 0) TooltipBehavior.SetDescription(mark, description);
            return mark;
        }

        /// <summary>Заголовок со значком пояснения справа от него.</summary>
        private static StackPanel TitleRow(Control title, HintMark hint)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            title.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(title);
            row.Children.Add(hint);
            return row;
        }

        private Border CardWithTitle(Control title, params Control[] content)
        {
            var stack = new StackPanel { Spacing = 8 };
            stack.Children.Add(title);
            foreach (var control in content) stack.Children.Add(control);

            var card = new Border
            {
                Classes = { "chartCard" },
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10),
                Child = stack
            };
            Bind(card, Border.BackgroundProperty, "BgSurfaceBrush");
            Bind(card, Border.BorderBrushProperty, "BorderDefaultBrush");
            return card;
        }

        private TextBlock FieldLabel(string text)
        {
            var label = new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            Bind(label, TextBlock.ForegroundProperty, "TextMutedBrush");
            return label;
        }

        /// <summary>
        /// Подпись и поле в строку. Колонка подписи не уже 78: короткие подписи строятся в
        /// один столбец, а длинная («Погрешности») раздвигает свою строку, а не обрезается.
        /// </summary>
        private Control FieldRow(string label, Control field)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            var text = FieldLabel(label);
            text.MinWidth = 78;
            text.Margin = new Thickness(0, 0, 8, 0);
            Place(grid, text, 0, 0);
            field.HorizontalAlignment = HorizontalAlignment.Stretch;
            Place(grid, field, 0, 1);
            return grid;
        }

        private (Slider Slider, TextBlock Value, Control Row) SliderRow(string label, double min, double max, double step, string unit)
        {
            var slider = new Slider
            {
                Minimum = min,
                Maximum = max,
                TickFrequency = step,
                IsSnapToTickEnabled = true,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, -6)
            };
            SliderJumpBehavior.SetEnabled(slider, true);
            var value = new TextBlock { FontSize = 12, Width = 46, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Bind(value, TextBlock.ForegroundProperty, "TextPrimaryBrush");

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("78,*,Auto") };
            Place(grid, FieldLabel(label), 0, 0);
            Place(grid, slider, 0, 1);
            Place(grid, value, 0, 2);
            return (slider, value, grid);
        }

        /// <summary>Заголовок оси с галочкой подписей у правого края.</summary>
        private static Control AxisHeader(TextBlock label, CheckBox labels)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Place(grid, label, 0, 0);
            labels.HorizontalAlignment = HorizontalAlignment.Right;
            Place(grid, labels, 0, 1);
            return grid;
        }

        /// <summary>Две галочки в одну строку, поровну.</summary>
        private static Control TwoChecks(CheckBox left, CheckBox right)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8 };
            Place(grid, left, 0, 0);
            Place(grid, right, 0, 1);
            return grid;
        }

        /// <summary>Приглушённая подпись внутри поля ввода, слева от числа.</summary>
        private TextBlock InnerHint(string text)
        {
            var label = new TextBlock { Text = text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            Bind(label, TextBlock.ForegroundProperty, "TextMutedBrush");
            return label;
        }

        /// <summary>
        /// Галочка карточки. Высота меньше стандартной (32): галочек в карточках много, и
        /// со стандартной они расползались по высоте.
        /// </summary>
        private CheckBox Check(string text)
        {
            var check = new CheckBox { Content = text, FontSize = 12, MinHeight = 26, VerticalContentAlignment = VerticalAlignment.Center };
            Bind(check, CheckBox.ForegroundProperty, "TextPrimaryBrush");
            return check;
        }

        private static TextBox Input()
            => new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch };

        private static ComboBox Combo(string[] items)
            => new() { ItemsSource = items, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch };

        private Border Divider()
        {
            var line = new Border { Height = 1, Margin = new Thickness(0, 4) };
            Bind(line, Border.BackgroundProperty, "BorderSubtleBrush");
            return line;
        }

        private Border DataSeparator()
        {
            var line = Separator();
            line.Height = 18;
            line.VerticalAlignment = VerticalAlignment.Center;
            return line;
        }

        private Border Separator()
        {
            var line = new Border { Width = 1, Margin = new Thickness(4, 4) };
            Bind(line, Border.BackgroundProperty, "BorderSubtleBrush");
            return line;
        }

        private static StackPanel IconText(string icon, string text)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            panel.Children.Add(new PathIcon { Data = StreamGeometry.Parse(icon), Width = 13, Height = 13, VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            return panel;
        }

        private static Button ToolButton(string icon, string text, string tip)
        {
            var button = new Button
            {
                Classes = { "chartTool" },
                Content = IconText(icon, text),
                Padding = new Thickness(8, 3),
                MinHeight = 26,
                FontSize = 12
            };
            TooltipBehavior.SetTip(button, tip);
            return button;
        }

        private enum DataButtonKind
        {
            Add,
            Remove,
            Neutral
        }

        private static readonly IBrush AddIconBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xB8, 0x5C));
        private static readonly IBrush RemoveIconBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x5A, 0x55));

        /// <summary>
        /// Квадратная кнопка над таблицей данных, как кнопка ленты: значок своего цвета,
        /// что она делает — в подсказке.
        /// </summary>
        private static Button DataButton(string icon, string tip, string description, DataButtonKind kind)
        {
            var glyph = new PathIcon { Data = StreamGeometry.Parse(icon), Width = 14, Height = 14 };
            if (kind == DataButtonKind.Add) glyph.Foreground = AddIconBrush;
            else if (kind == DataButtonKind.Remove) glyph.Foreground = RemoveIconBrush;

            var button = new Button
            {
                Classes = { RibbonButtonClass },
                Content = glyph,
                Width = 28,
                Height = 26,
                MinHeight = 0,
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center
            };
            TooltipBehavior.SetTip(button, tip);
            TooltipBehavior.SetDescription(button, description);
            return button;
        }

        /// <summary>Подпись перед парой кнопок таблицы: «Строки», «Ряды».</summary>
        private TextBlock GroupLabel(string text)
        {
            var label = new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 2, 0) };
            Bind(label, TextBlock.ForegroundProperty, "TextSecondaryBrush");
            return label;
        }

        private static Button IconButton(string icon, string tip)
        {
            var button = new Button
            {
                Classes = { "chartIcon" },
                Width = 30,
                Height = 30,
                MinHeight = 0,
                Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Content = new PathIcon { Data = StreamGeometry.Parse(icon), Width = 14, Height = 14 }
            };
            TooltipBehavior.SetTip(button, tip);
            return button;
        }
    }

    /// <summary>Правка объекта по месту с возвратом его же — для коротких правок настроек ряда.</summary>
    internal static class ChartEditorExtensions
    {
        public static T Also<T>(this T value, Action<T> change)
        {
            change(value);
            return value;
        }
    }
}
