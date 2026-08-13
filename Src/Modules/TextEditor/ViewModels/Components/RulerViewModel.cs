using ReactiveUI;
using System;
using System.Collections.Generic;
using Writersword.Modules.TextEditor.Models.Settings;

namespace Writersword.Modules.TextEditor.ViewModels.Components
{
    public enum RulerMode
    {
        Paragraph = 0,
        Table = 1
    }

    public sealed class RulerIndentMarker
    {
        public RulerIndentMarkerType Type { get; init; }
        public double Position { get; set; }
    }

    public enum RulerIndentMarkerType
    {
        LeftIndent = 0,
        FirstLineIndent = 1,
        RightIndent = 2,
        ListMarker = 3
    }

    /// <summary>
    /// Фактическая геометрия абзаца под кареткой, снятая с раскладки. Единственный источник
    /// положения стрелок на линейке: раскладка уже применила все свои правила — ограничители,
    /// перенос текста списка на вторую строку, поля и рамку ячейки, — и любая попытка
    /// повторить их расчётом по значениям модели рано или поздно с ней расходится.
    ///
    /// Все величины в миллиметрах. Отступы отсчитываются от зоны абзаца, а не от страницы:
    /// у обычного абзаца зона — текстовая область страницы, у абзаца ячейки — её контентный
    /// бокс (за полями и рамкой). <see cref="RightIndentMm"/> — расстояние от ПРАВОГО края
    /// зоны, как линейка правый маркер и рисует.
    /// </summary>
    public readonly struct RulerParagraphGeometry
    {
        /// <summary>Левый край зоны абзаца от начала текстовой области страницы.</summary>
        public double ZoneLeftMm { get; init; }

        /// <summary>Ширина зоны абзаца.</summary>
        public double ZoneWidthMm { get; init; }

        /// <summary>Левый отступ (строки 2+) от левого края зоны.</summary>
        public double LeftIndentMm { get; init; }

        /// <summary>Начало текста первой строки от левого края зоны.</summary>
        public double FirstLineMm { get; init; }

        /// <summary>Правый отступ от правого края зоны.</summary>
        public double RightIndentMm { get; init; }

        /// <summary>Левый край номера/значка списка от левого края зоны.</summary>
        public double MarkerMm { get; init; }

        /// <summary>Абзац — элемент списка, метку показывать.</summary>
        public bool HasMarker { get; init; }

        /// <summary>
        /// Насколько левее начала зоны разрешено уводить маркеры. Для абзаца ячейки это её
        /// поле: номер списка можно поставить в самый край клетки, а не только от начала
        /// текста. Для обычного абзаца — левое поле страницы.
        /// </summary>
        public double LeftOverhangMm { get; init; }
    }

    public sealed class RulerColumnMarker
    {
        /// <summary>Индекс колонки (0-based). -1 = левый край таблицы.</summary>
        public int ColumnIndex { get; init; }

        /// <summary>
        /// X-позиция правого края колонки в единицах линейки от начала текстовой области.
        /// Для ColumnIndex=-1 это позиция левого края таблицы.
        /// </summary>
        public double RightEdge { get; set; }
    }

    public sealed class RulerViewModel : ReactiveObject
    {
        private RulerUnits _units;
        private RulerMode _mode;
        private double _zoom = 1.0;
        private bool _isVisible = true;
        private bool _isSnapEnabled = true;
        private bool _isReadOnly;
        private int _focusedPageIndex = 0;

        private double _pageWidthMm = 210;
        private double _pageHeightMm = 297;
        private double _marginLeftMm = 30;
        private double _marginRightMm = 15;
        private double _marginTopMm = 25;
        private double _marginBottomMm = 25;
        private double _pageOffsetXPx = 0;

        private double _leftIndentMm = 0;
        private double _firstLineIndentMm = 0;
        private double _rightIndentMm = 0;
        private double _listMarkerMm = 0;
        private bool _showListMarker = false;

        private double _scrollOffsetY = 0;
        private double _viewportHeight = 600;
        private double _contentTopOffsetPx = 0;
        private int _pagesPerRow = 1;

        private RulerIndentMarkerType? _draggingIndentMarker;
        private int _draggingColumnIndex = -1;

        // ── Свойства ──────────────────────────────────────────────────────

        public RulerUnits Units
        {
            get => _units;
            set => this.RaiseAndSetIfChanged(ref _units, value);
        }

        public RulerMode Mode
        {
            get => _mode;
            set => this.RaiseAndSetIfChanged(ref _mode, value);
        }

        public bool IsSnapEnabled
        {
            get => _isSnapEnabled;
            set => this.RaiseAndSetIfChanged(ref _isSnapEnabled, value);
        }

        /// <summary>
        /// Режим сравнения версий: линейка работает только на отображение.
        /// Контролы линеек не начинают drag маркеров отступов, колонок и полей.
        /// </summary>
        public bool IsReadOnly
        {
            get => _isReadOnly;
            set => this.RaiseAndSetIfChanged(ref _isReadOnly, value);
        }

        public double SnapStep => 0.25;

        public double Zoom
        {
            get => _zoom;
            set => this.RaiseAndSetIfChanged(ref _zoom, Math.Max(0.25, Math.Min(5.0, value)));
        }

        public bool IsVisible
        {
            get => _isVisible;
            set => this.RaiseAndSetIfChanged(ref _isVisible, value);
        }

        /// <summary>
        /// Индекс страницы на которой стоит каретка (0-based).
        /// Вертикальная линейка отображает шкалу только для этой страницы.
        /// </summary>
        public int FocusedPageIndex
        {
            get => _focusedPageIndex;
            set => this.RaiseAndSetIfChanged(ref _focusedPageIndex, Math.Max(0, value));
        }

        public double PageWidthMm
        {
            get => _pageWidthMm;
            set => this.RaiseAndSetIfChanged(ref _pageWidthMm, value);
        }

        public double PageHeightMm
        {
            get => _pageHeightMm;
            set => this.RaiseAndSetIfChanged(ref _pageHeightMm, value);
        }

        public double MarginLeftMm
        {
            get => _marginLeftMm;
            set => this.RaiseAndSetIfChanged(ref _marginLeftMm, value);
        }

        public double MarginRightMm
        {
            get => _marginRightMm;
            set => this.RaiseAndSetIfChanged(ref _marginRightMm, value);
        }

        public double MarginTopMm
        {
            get => _marginTopMm;
            set => this.RaiseAndSetIfChanged(ref _marginTopMm, value);
        }

        public double MarginBottomMm
        {
            get => _marginBottomMm;
            set => this.RaiseAndSetIfChanged(ref _marginBottomMm, value);
        }

        public double PageOffsetXPx
        {
            get => _pageOffsetXPx;
            set => this.RaiseAndSetIfChanged(ref _pageOffsetXPx, value);
        }

        public double LeftIndentMm
        {
            get => _leftIndentMm;
            set { this.RaiseAndSetIfChanged(ref _leftIndentMm, value); UpdateIndentMarkers(); }
        }

        public double FirstLineIndentMm
        {
            get => _firstLineIndentMm;
            set { this.RaiseAndSetIfChanged(ref _firstLineIndentMm, value); UpdateIndentMarkers(); }
        }

        public double RightIndentMm
        {
            get => _rightIndentMm;
            set { this.RaiseAndSetIfChanged(ref _rightIndentMm, value); UpdateIndentMarkers(); }
        }

        /// <summary>Позиция маркера списка (выступ) от левого поля, мм. Двигает стрелку «край списка».</summary>
        public double ListMarkerMm
        {
            get => _listMarkerMm;
            set { this.RaiseAndSetIfChanged(ref _listMarkerMm, value); UpdateIndentMarkers(); }
        }

        /// <summary>Показывать ли стрелку «край списка» (только когда активный абзац — элемент списка).</summary>
        public bool ShowListMarker
        {
            get => _showListMarker;
            set => this.RaiseAndSetIfChanged(ref _showListMarker, value);
        }

        public double ScrollOffsetY
        {
            get => _scrollOffsetY;
            set => this.RaiseAndSetIfChanged(ref _scrollOffsetY, value);
        }

        public double ViewportHeight
        {
            get => _viewportHeight;
            set => this.RaiseAndSetIfChanged(ref _viewportHeight, value);
        }

        /// <summary>
        /// Вертикальное смещение канваса внутри вьюпорта, px. Когда высота документа меньше
        /// высоты вьюпорта (мелкий зум, короткий документ), Avalonia центрирует канвас по
        /// вертикали, и верх первой страницы оказывается ниже верха вьюпорта. Вертикальная
        /// линейка прибавляет это смещение — иначе её шкала не совпадает с листом.
        /// Горизонтальный аналог — PageOffsetXPx.
        /// </summary>
        public double ContentTopOffsetPx
        {
            get => _contentTopOffsetPx;
            set => this.RaiseAndSetIfChanged(ref _contentTopOffsetPx, value);
        }

        /// <summary>
        /// Число страниц в ряду (1 или 2). В режиме двух страниц рядом вертикальная позиция
        /// страницы определяется её рядом, а не порядковым номером.
        /// </summary>
        public int PagesPerRow
        {
            get => _pagesPerRow;
            set => this.RaiseAndSetIfChanged(ref _pagesPerRow, Math.Clamp(value, 1, 2));
        }

        public List<RulerIndentMarker> IndentMarkers { get; } = new()
        {
            new RulerIndentMarker { Type = RulerIndentMarkerType.LeftIndent,      Position = 0 },
            new RulerIndentMarker { Type = RulerIndentMarkerType.FirstLineIndent, Position = 0 },
            new RulerIndentMarker { Type = RulerIndentMarkerType.RightIndent,     Position = 0 },
            new RulerIndentMarker { Type = RulerIndentMarkerType.ListMarker,      Position = 0 }
        };

        public List<RulerColumnMarker> ColumnMarkers { get; } = new();

        public RulerIndentMarkerType? DraggingIndentMarker
        {
            get => _draggingIndentMarker;
            set => this.RaiseAndSetIfChanged(ref _draggingIndentMarker, value);
        }

        public int DraggingColumnIndex
        {
            get => _draggingColumnIndex;
            set => this.RaiseAndSetIfChanged(ref _draggingColumnIndex, value);
        }

        // ── События ───────────────────────────────────────────────────────

        public event Action<RulerIndentMarkerType, double>? IndentMarkerChanged;

        /// <summary>Перетаскивание маркера отступа началось (нажата кнопка мыши на маркере).</summary>
        public event Action? IndentDragStarted;

        /// <summary>Перетаскивание маркера отступа завершено (кнопка мыши отпущена).</summary>
        public event Action? IndentDragEnded;
        public Func<double>? GetMinParagraphIndentMm { get; set; }

        /// <summary>
        /// Правый предел для маркера в единицах линейки (в её текущей системе отсчёта:
        /// вне таблицы от начала текстовой зоны, в таблице от левого края ячейки).
        /// null — предела нет. Нужен для списка: номер и начало текста не могут уехать
        /// вправо настолько, чтобы тексту не осталось места, — иначе раскладка упирает
        /// строку в свой предел, а стрелка уходит дальше, и они расходятся.
        /// </summary>
        public Func<RulerIndentMarkerType, double?>? GetIndentUpperLimitUnits { get; set; }

        /// <summary>
        /// Насколько левее начала зоны можно увести маркер. Пока раскладка не прислала свою
        /// величину, берётся левое поле страницы — прежнее поведение для обычного абзаца.
        /// </summary>
        private double LeftBoundUnits()
            => _leftOverhangUnits > 0 ? _leftOverhangUnits : MmToUnits(MarginLeftMm);

        /// <summary>Применяет предел из <see cref="GetIndentUpperLimitUnits"/> к позиции маркера.</summary>
        private double ClampToUpperLimit(RulerIndentMarkerType type, double positionUnits)
        {
            if (GetIndentUpperLimitUnits?.Invoke(type) is double limit)
                return Math.Min(positionUnits, limit);
            return positionUnits;
        }

        /// <summary>Начато перетаскивание поля страницы — владелец делает снапшот для Undo.</summary>
        public event Action? MarginDragStarted;
        public void BeginMarginDrag() => MarginDragStarted?.Invoke();

        public event Action<double, double>? MarginChanged;
        public void NotifyMarginChanged() => MarginChanged?.Invoke(MarginLeftMm, MarginRightMm);

        public event Action<double, double>? MarginCommitted;
        public void CommitMarginChange() => MarginCommitted?.Invoke(MarginLeftMm, MarginRightMm);

        public event Action<IReadOnlyDictionary<int, double>>? AllColumnWidthsChanged;
        public event Action<IReadOnlyDictionary<int, double>>? AllColumnWidthsChanging;

        /// <summary>Live-событие: левый край таблицы сдвинулся. Параметр — новый отступ в мм.</summary>
        public event Action<double>? TableLeftEdgeChanging;

        /// <summary>Commit-событие: пользователь отпустил маркер левого края таблицы. Параметр — отступ в мм.</summary>
        public event Action<double>? TableLeftEdgeChanged;

        /// <summary>Смещение левого края таблицы в единицах линейки от начала текстовой области.</summary>
        public double TableLeftEdgeUnits { get; private set; }

        /// <summary>
        /// Левый край зоны абзаца под кареткой в единицах: для ячейки — её контентный бокс,
        /// за полем и рамкой. Пишется ТОЛЬКО из <see cref="ApplyParagraphGeometry"/>.
        ///
        /// Раньше сюда же писал метод, считавший края по маркерам столбцов. Края столбца и
        /// контентный бокс различаются на поле ячейки — около 4-5 мм, — и во время жеста
        /// значения чередовались кадр за кадром: геометрия себя блокирует, пока тянут маркер,
        /// а тот метод продолжал возвращать старую точку отсчёта. Все стрелки рисуются от
        /// неё, поэтому прыгали разом.
        /// </summary>
        public double ActiveCellLeftUnits { get; private set; }

        /// <summary>Правый край зоны абзаца под кареткой в единицах. Пишется там же.</summary>
        public double ActiveCellRightUnits { get; private set; }

        /// <summary>
        /// Насколько левее начала зоны можно увести маркер, в единицах. Приходит из раскладки
        /// вместе с границами зоны: у ячейки это её поле, у обычного абзаца — поле страницы.
        /// </summary>
        private double _leftOverhangUnits;

        // ── Конвертация ───────────────────────────────────────────────────

        public double MmToUnits(double mm)
            => Units == RulerUnits.Inches ? mm / 25.4 : mm / 10.0;

        public double UnitsToMm(double units)
            => Units == RulerUnits.Inches ? units * 25.4 : units * 10.0;

        public double MajorTickInterval => 1.0;

        public double MinorTickInterval
            => Units == RulerUnits.Inches ? 0.25 : 0.5;

        public double TinyTickInterval
            => Units == RulerUnits.Inches ? 0.125 : 0.1;

        // ── Методы ────────────────────────────────────────────────────────

        public void UpdatePageSettings(
            double widthMm, double heightMm,
            double marginLeftMm, double marginRightMm,
            double marginTopMm, double marginBottomMm)
        {
            PageWidthMm = widthMm;
            PageHeightMm = heightMm;
            MarginLeftMm = marginLeftMm;
            MarginRightMm = marginRightMm;
            MarginTopMm = marginTopMm;
            MarginBottomMm = marginBottomMm;
        }

        public void UpdateFromParagraphContext(
            double leftIndentPt,
            double firstLineIndentPt,
            double rightIndentPt)
        {
            const double PtToMm = 25.4 / 72.0;
            _leftIndentMm = leftIndentPt * PtToMm;
            _firstLineIndentMm = firstLineIndentPt * PtToMm;
            _rightIndentMm = rightIndentPt * PtToMm;

            // Отступы абзаца внутри ячейки хранятся в модели относительно самой ячейки,
            // а маркеры в табличном режиме рисуются и тянутся в тех же координатах —
            // от левого края активной ячейки. Значит контекст курсора приходит уже в нужной
            // системе отсчёта, и оба режима обслуживаются одним и тем же кодом без перевода.
            UpdateIndentMarkers();
            this.RaisePropertyChanged(nameof(LeftIndentMm));
            this.RaisePropertyChanged(nameof(FirstLineIndentMm));
            this.RaisePropertyChanged(nameof(RightIndentMm));
        }

        public void UpdateTableColumns(
            IReadOnlyList<double> columnOffsetsMm,
            IReadOnlyList<double> columnWidthsMm,
            double tableOffsetMm = 0)
        {
            ColumnMarkers.Clear();

            TableLeftEdgeUnits = MmToUnits(tableOffsetMm);
            // Маркер левого края таблицы (ColumnIndex = -1).
            ColumnMarkers.Add(new RulerColumnMarker
            {
                ColumnIndex = -1,
                RightEdge = TableLeftEdgeUnits
            });

            for (int i = 0; i < columnWidthsMm.Count; i++)
            {
                double leftMm = tableOffsetMm + (i < columnOffsetsMm.Count ? columnOffsetsMm[i] : 0);
                ColumnMarkers.Add(new RulerColumnMarker
                {
                    ColumnIndex = i,
                    RightEdge = MmToUnits(leftMm + columnWidthsMm[i])
                });
            }

            Mode = RulerMode.Table;
        }

        /// <summary>
        /// Ставит все стрелки и границы зоны по фактической геометрии абзаца под кареткой.
        /// Единственный, кому разрешено двигать маркеры вне перетаскивания.
        ///
        /// Во время жеста вызов игнорируется: позицию ведёт мышь, а раскладка в этот момент
        /// отдаёт ещё не применённое состояние — иначе стрелка дёргалась бы между двумя
        /// значениями на каждом кадре.
        /// </summary>
        public void ApplyParagraphGeometry(RulerParagraphGeometry geometry)
        {
            if (DraggingIndentMarker is not null) return;

            ActiveCellLeftUnits = MmToUnits(geometry.ZoneLeftMm);
            ActiveCellRightUnits = MmToUnits(geometry.ZoneLeftMm + geometry.ZoneWidthMm);
            _leftOverhangUnits = MmToUnits(geometry.LeftOverhangMm);

            _leftIndentMm = geometry.LeftIndentMm;
            _firstLineIndentMm = geometry.FirstLineMm;
            _rightIndentMm = geometry.RightIndentMm;
            _listMarkerMm = geometry.MarkerMm;

            UpdateIndentMarkers();
            ShowListMarker = geometry.HasMarker;

            this.RaisePropertyChanged(nameof(ActiveCellLeftUnits));
            this.RaisePropertyChanged(nameof(ActiveCellRightUnits));
            this.RaisePropertyChanged(nameof(LeftIndentMm));
            this.RaisePropertyChanged(nameof(FirstLineIndentMm));
            this.RaisePropertyChanged(nameof(RightIndentMm));
        }

        public void SwitchToParagraphMode()
        {
            if (Mode == RulerMode.Paragraph) return;
            ColumnMarkers.Clear();
            Mode = RulerMode.Paragraph;
        }

        public void BeginIndentDrag(RulerIndentMarkerType markerType)
        {
            DraggingIndentMarker = markerType;
            IndentDragStarted?.Invoke();
        }

        public void EndIndentDrag()
        {
            if (DraggingIndentMarker is null) return;
            var marker = GetIndentMarker(DraggingIndentMarker.Value);
            if (marker is not null)
            {
                // Применяем snap при отпускании кнопки мыши.
                double pos = marker.Position;
                if (IsSnapEnabled)
                    pos = Math.Round(pos / SnapStep) * SnapStep;
                // Округление к шагу привязки может перебросить маркер за предел на четверть
                // единицы — применяем предел после снапа, иначе отпускание кнопки возвращало
                // бы стрелку туда, куда её во время жеста не пускали.
                pos = ClampToUpperLimit(DraggingIndentMarker.Value, pos);
                marker.Position = pos;
                this.RaisePropertyChanged(nameof(IndentMarkers));
                IndentMarkerChanged?.Invoke(DraggingIndentMarker.Value, UnitsToMm(pos));
            }
            DraggingIndentMarker = null;
            IndentDragEnded?.Invoke();
        }

        public void BeginColumnDrag(int listIndex)
            => DraggingColumnIndex = listIndex;

        /// <summary>
        /// Drag маркера колонки или левого края таблицы.
        /// rightEdgeUnits — позиция курсора в единицах линейки от начала текстовой области.
        /// </summary>
        public void UpdateColumnDrag(double rightEdgeUnits)
        {
            if (DraggingColumnIndex < 0 || DraggingColumnIndex >= ColumnMarkers.Count) return;

            var draggingMarker = ColumnMarkers[DraggingColumnIndex];

            // ── Левый край таблицы: сдвигаем всю таблицу ─────────────────
            if (draggingMarker.ColumnIndex < 0)
            {
                if (IsSnapEnabled)
                    rightEdgeUnits = Math.Round(rightEdgeUnits / SnapStep) * SnapStep;

                // Левый край таблицы не может уйти левее левого края страницы (-MarginLeft).
                // Правое ограничение отсутствует.
                double newLeft = Math.Max(-MmToUnits(MarginLeftMm), rightEdgeUnits);
                double delta = newLeft - TableLeftEdgeUnits;
                TableLeftEdgeUnits = newLeft;
                foreach (var m in ColumnMarkers)
                    m.RightEdge += delta;

                this.RaisePropertyChanged(nameof(ColumnMarkers));
                TableLeftEdgeChanging?.Invoke(UnitsToMm(newLeft));
                return;
            }

            // ── Маркер ширины колонки ─────────────────────────────────────
            if (IsSnapEnabled)
                rightEdgeUnits = Math.Round(rightEdgeUnits / SnapStep) * SnapStep;

            // Минимальная ширина колонки — 5мм.
            // Нет максимума: таблица может выходить за правый край страницы.
            double minRE = DraggingColumnIndex > 0
                ? ColumnMarkers[DraggingColumnIndex - 1].RightEdge + MmToUnits(5)
                : TableLeftEdgeUnits + MmToUnits(5);

            // Следующий маркер не может оказаться левее текущего + 5мм (порядок колонок).
            double maxRE = DraggingColumnIndex < ColumnMarkers.Count - 1
                ? ColumnMarkers[DraggingColumnIndex + 1].RightEdge - MmToUnits(5)
                : double.MaxValue; // последняя колонка — без правого ограничения

            ColumnMarkers[DraggingColumnIndex].RightEdge =
                Math.Max(minRE, maxRE == double.MaxValue ? rightEdgeUnits : Math.Min(rightEdgeUnits, maxRE));

            this.RaisePropertyChanged(nameof(ColumnMarkers));

            // Строим словарь ширин ВСЕХ колонок и стреляем разом.
            var allWidths = BuildAllColumnWidths();
            AllColumnWidthsChanging?.Invoke(allWidths);
        }

        public void EndColumnDrag()
        {
            if (DraggingColumnIndex < 0 || DraggingColumnIndex >= ColumnMarkers.Count) return;

            var draggingMarker = ColumnMarkers[DraggingColumnIndex];

            if (draggingMarker.ColumnIndex < 0)
            {
                TableLeftEdgeChanged?.Invoke(UnitsToMm(TableLeftEdgeUnits));
            }
            else
            {
                var allWidths = BuildAllColumnWidths();
                AllColumnWidthsChanged?.Invoke(allWidths);
            }

            DraggingColumnIndex = -1;
        }

        /// <summary>
        /// Строит словарь {columnIndex → widthMm} по всем маркерам колонок.
        /// Используется чтобы зафиксировать ВСЕ колонки при drag одной.
        /// </summary>
        private Dictionary<int, double> BuildAllColumnWidths()
        {
            var result = new Dictionary<int, double>();
            for (int i = 0; i < ColumnMarkers.Count; i++)
            {
                var m = ColumnMarkers[i];
                if (m.ColumnIndex < 0) continue; // пропускаем маркер левого края

                double leftEdgeMm = i > 0
                    ? UnitsToMm(ColumnMarkers[i - 1].RightEdge)
                    : UnitsToMm(TableLeftEdgeUnits);
                double rightEdgeMm = UnitsToMm(m.RightEdge);
                result[m.ColumnIndex] = Math.Max(5, rightEdgeMm - leftEdgeMm);
            }
            return result;
        }

        /// <summary>
        /// Двигает ТОЛЬКО маркер абзацной стрелки (первой строки), не трогая остальные маркеры.
        /// Нужно во время drag метки списка, чтобы абзацная стрелка ехала за меткой, а сама
        /// метка не сбрасывалась. Значение — в той же системе отсчёта, в которой линейка держит
        /// все маркеры: вне таблицы от начала текстовой зоны, в таблице от левого края активной
        /// ячейки. Это ровно то, что лежит в отступах абзаца, поэтому вызывающий передаёт
        /// величину из модели как есть.
        /// </summary>
        public void SetFirstLineMarkerMm(double mm)
        {
            _firstLineIndentMm = mm;
            var m = GetIndentMarker(RulerIndentMarkerType.FirstLineIndent);
            if (m is not null) m.Position = MmToUnits(mm);
            this.RaisePropertyChanged(nameof(IndentMarkers));
        }

        /// <summary>
        /// Двигает ТОЛЬКО маркер левого отступа, не трогая остальные. Нужен при перетаскивании
        /// метки списка: она везёт за собой левый отступ абзаца, и без этого нижняя стрелка
        /// стояла на месте весь жест, показывая величину, которой в модели уже нет.
        /// Система отсчёта — как у остальных маркеров линейки.
        /// </summary>
        public void SetLeftMarkerMm(double mm)
        {
            _leftIndentMm = mm;
            var m = GetIndentMarker(RulerIndentMarkerType.LeftIndent);
            if (m is not null) m.Position = MmToUnits(mm);
            this.RaisePropertyChanged(nameof(IndentMarkers));
        }

        private void UpdateIndentMarkers()
        {
            GetIndentMarker(RulerIndentMarkerType.LeftIndent)!.Position = MmToUnits(_leftIndentMm);
            GetIndentMarker(RulerIndentMarkerType.FirstLineIndent)!.Position = MmToUnits(_firstLineIndentMm);
            GetIndentMarker(RulerIndentMarkerType.RightIndent)!.Position = MmToUnits(_rightIndentMm);
            var listMarker = GetIndentMarker(RulerIndentMarkerType.ListMarker);
            if (listMarker is not null) listMarker.Position = MmToUnits(_listMarkerMm);
            this.RaisePropertyChanged(nameof(IndentMarkers));
        }

        public double GetIndentMarkerPosition(RulerIndentMarkerType type)
            => GetIndentMarker(type)?.Position ?? 0;

        private RulerIndentMarker? GetIndentMarker(RulerIndentMarkerType type)
        {
            foreach (var m in IndentMarkers)
                if (m.Type == type) return m;
            return null;
        }

        /// <summary>
        /// Drag маркера отступа в режиме абзаца — позиция относительно начала текстовой области.
        /// </summary>
        public void UpdateIndentDragUnclamped(double positionUnits)
        {
            if (DraggingIndentMarker is null) return;
            var marker = GetIndentMarker(DraggingIndentMarker.Value);
            if (marker is null) return;

            if (IsSnapEnabled)
                positionUnits = Math.Round(positionUnits / SnapStep) * SnapStep;

            double pageWidthUnits = MmToUnits(PageWidthMm);
            positionUnits = Math.Min(positionUnits, pageWidthUnits);
            positionUnits = ClampToUpperLimit(DraggingIndentMarker.Value, positionUnits);

            if (DraggingIndentMarker == RulerIndentMarkerType.RightIndent)
                positionUnits = Math.Max(positionUnits, -MmToUnits(MarginRightMm));
            else
                positionUnits = Math.Max(positionUnits, -LeftBoundUnits());

            // Левая стрелка тянет за собой абзацную, сохраняя расстояние между ними: так
            // сдвигается весь абзац целиком. У элемента списка это неверно — там абзацная
            // стрелка показывает начало текста после номера и от левого отступа не зависит,
            // левая же задаёт только строки 2+. ShowListMarker включён ровно тогда, когда
            // активный абзац — элемент списка.
            if (DraggingIndentMarker == RulerIndentMarkerType.LeftIndent && !ShowListMarker)
            {
                var firstMarker = GetIndentMarker(RulerIndentMarkerType.FirstLineIndent);
                if (firstMarker is not null)
                {
                    double leftCurrent = GetIndentMarker(RulerIndentMarkerType.LeftIndent)!.Position;
                    double offset = firstMarker.Position - leftCurrent;
                    double newFirst = positionUnits + offset;
                    firstMarker.Position = Math.Max(-MmToUnits(MarginLeftMm),
                        Math.Min(pageWidthUnits, newFirst));
                }
            }

            marker.Position = positionUnits;
            this.RaisePropertyChanged(nameof(IndentMarkers));
            IndentMarkerChanged?.Invoke(DraggingIndentMarker.Value, UnitsToMm(marker.Position));
        }

        /// <summary>
        /// Drag маркера отступа в режиме таблицы.
        /// positionUnitsFromCellStart — позиция относительно левого края ячейки.
        /// </summary>
        public void UpdateTableIndentDragUnclamped(double positionUnitsFromCellStart)
        {
            if (DraggingIndentMarker is null) return;
            var marker = GetIndentMarker(DraggingIndentMarker.Value);
            if (marker is null) return;

            if (IsSnapEnabled)
                positionUnitsFromCellStart = Math.Round(positionUnitsFromCellStart / SnapStep) * SnapStep;

            double cellW = ActiveCellRightUnits - ActiveCellLeftUnits;
            positionUnitsFromCellStart = Math.Min(positionUnitsFromCellStart, cellW);

            // Влево метка идёт до левого поля страницы: запас приходит из раскладки и включает
            // смещение самой зоны, поэтому номер уводится и за край клетки. Прежний запас был
            // равен полю ячейки — те самые пара миллиметров, — и жест упирался вплотную к
            // тексту. Остальные маркеры держатся начала зоны: у них левее ничего нет.
            positionUnitsFromCellStart = DraggingIndentMarker == RulerIndentMarkerType.ListMarker
                ? Math.Max(-LeftBoundUnits(), positionUnitsFromCellStart)
                : Math.Max(0, positionUnitsFromCellStart);
            positionUnitsFromCellStart =
                ClampToUpperLimit(DraggingIndentMarker.Value, positionUnitsFromCellStart);

            // Как и вне таблицы: у элемента списка абзацная стрелка живёт от номера,
            // а не от левого отступа, и за нижней стрелкой не едет.
            if (DraggingIndentMarker == RulerIndentMarkerType.LeftIndent && !ShowListMarker)
            {
                var firstMarker = GetIndentMarker(RulerIndentMarkerType.FirstLineIndent);
                if (firstMarker is not null)
                {
                    double leftCurrent = GetIndentMarker(RulerIndentMarkerType.LeftIndent)!.Position;
                    double offset = firstMarker.Position - leftCurrent;
                    firstMarker.Position = Math.Max(0, Math.Min(cellW, positionUnitsFromCellStart + offset));
                }
            }

            marker.Position = positionUnitsFromCellStart;
            this.RaisePropertyChanged(nameof(IndentMarkers));
            IndentMarkerChanged?.Invoke(DraggingIndentMarker.Value, UnitsToMm(marker.Position));
        }
    }
}