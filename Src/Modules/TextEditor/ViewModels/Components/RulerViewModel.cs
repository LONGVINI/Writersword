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

        /// <summary>Левый край активной ячейки в единицах.</summary>
        public double ActiveCellLeftUnits { get; private set; }

        /// <summary>Правый край активной ячейки в единицах.</summary>
        public double ActiveCellRightUnits { get; private set; }

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

            // В режиме таблицы маркеры уже стоят в cell-relative координатах
            // (они переведены в UpdateActiveCellBounds). Не перезаписываем их абсолютными
            // значениями — иначе drag внутри ячейки будет использовать неверную начальную позицию.
            if (Mode == RulerMode.Table)
            {
                this.RaisePropertyChanged(nameof(LeftIndentMm));
                this.RaisePropertyChanged(nameof(FirstLineIndentMm));
                this.RaisePropertyChanged(nameof(RightIndentMm));
                return;
            }

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

        public void UpdateActiveCellBounds(int columnIndex)
        {
            if (columnIndex < 0 || ColumnMarkers.Count == 0) return;

            int markerIdx = -1;
            for (int i = 0; i < ColumnMarkers.Count; i++)
                if (ColumnMarkers[i].ColumnIndex == columnIndex) { markerIdx = i; break; }

            if (markerIdx < 0) return;

            // Маркер с ColumnIndex=-1 хранит позицию левого края таблицы в RightEdge.
            // Левый край ячейки col[n] = правый край маркера col[n-1].
            ActiveCellLeftUnits = markerIdx > 0
                ? ColumnMarkers[markerIdx - 1].RightEdge
                : TableLeftEdgeUnits;
            ActiveCellRightUnits = ColumnMarkers[markerIdx].RightEdge;

            // Перевод маркеров отступа в cell-relative координаты.
            // UpdateFromParagraphContext записывает абсолютные значения (от начала текстовой зоны),
            // но в таблице все drag-операции работают в координатах от начала ячейки.
            // Вычитаем смещение ячейки чтобы маркеры стояли корректно внутри ячейки.
            var leftMarker = GetIndentMarker(RulerIndentMarkerType.LeftIndent);
            var firstMarker = GetIndentMarker(RulerIndentMarkerType.FirstLineIndent);
            var rightMarker = GetIndentMarker(RulerIndentMarkerType.RightIndent);
            double cellW = ActiveCellRightUnits - ActiveCellLeftUnits;
            if (leftMarker is not null)
                leftMarker.Position = Math.Max(0, Math.Min(cellW, leftMarker.Position - ActiveCellLeftUnits));
            if (firstMarker is not null)
                firstMarker.Position = Math.Max(0, Math.Min(cellW, firstMarker.Position - ActiveCellLeftUnits));
            if (rightMarker is not null)
                rightMarker.Position = Math.Max(0, Math.Min(cellW, rightMarker.Position - ActiveCellLeftUnits));

            this.RaisePropertyChanged(nameof(ActiveCellLeftUnits));
            this.RaisePropertyChanged(nameof(ActiveCellRightUnits));
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
        /// Двигает ТОЛЬКО маркер абзацной стрелки (первой строки) на абсолютную позицию (мм от
        /// начала текстовой зоны), не трогая остальные маркеры. Нужно во время drag метки списка,
        /// чтобы абзацная стрелка ехала за меткой, а сама метка не сбрасывалась.
        /// </summary>
        public void SetFirstLineMarkerAbsolute(double mm)
        {
            _firstLineIndentMm = mm;
            var m = GetIndentMarker(RulerIndentMarkerType.FirstLineIndent);
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

            if (DraggingIndentMarker == RulerIndentMarkerType.RightIndent)
                positionUnits = Math.Max(positionUnits, -MmToUnits(MarginRightMm));
            else
                positionUnits = Math.Max(positionUnits, -MmToUnits(MarginLeftMm));

            if (DraggingIndentMarker == RulerIndentMarkerType.LeftIndent)
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
            positionUnitsFromCellStart = Math.Max(0, Math.Min(positionUnitsFromCellStart, cellW));

            if (DraggingIndentMarker == RulerIndentMarkerType.LeftIndent)
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