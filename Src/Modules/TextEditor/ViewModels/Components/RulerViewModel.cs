using ReactiveUI;
using System;
using System.Collections.Generic;
using Writersword.Modules.TextEditor.Models.Settings;

namespace Writersword.Modules.TextEditor.ViewModels.Components
{
    /// <summary>
    /// Режим отображения линейки.
    /// Переключается автоматически в зависимости от позиции каретки.
    /// </summary>
    public enum RulerMode
    {
        /// <summary>Обычный режим — маркеры отступов абзаца.</summary>
        Paragraph = 0,

        /// <summary>Режим таблицы — маркеры колонок таблицы.</summary>
        Table = 1
    }

    /// <summary>
    /// Маркер отступа абзаца на горизонтальной линейке.
    /// </summary>
    public sealed class RulerIndentMarker
    {
        /// <summary>Тип маркера.</summary>
        public RulerIndentMarkerType Type { get; init; }

        /// <summary>
        /// Позиция маркера в единицах линейки (мм или дюймы)
        /// относительно начала текстовой области страницы.
        /// </summary>
        public double Position { get; set; }
    }

    /// <summary>
    /// Тип маркера отступа на линейке.
    /// </summary>
    public enum RulerIndentMarkerType
    {
        /// <summary>Левый отступ абзаца — нижний треугольник слева.</summary>
        LeftIndent = 0,

        /// <summary>Отступ первой строки — верхний треугольник слева.</summary>
        FirstLineIndent = 1,

        /// <summary>Правый отступ абзаца — нижний треугольник справа.</summary>
        RightIndent = 2
    }

    /// <summary>
    /// Маркер колонки таблицы на горизонтальной линейке.
    /// Показывается когда каретка находится внутри таблицы.
    /// </summary>
    public sealed class RulerColumnMarker
    {
        /// <summary>Индекс колонки (0-based).</summary>
        public int ColumnIndex { get; init; }

        /// <summary>
        /// X-позиция правого края колонки в единицах линейки
        /// относительно начала текстовой области страницы.
        /// Перетаскивание этого маркера изменяет ширину колонки.
        /// </summary>
        public double RightEdge { get; set; }
    }

    /// <summary>
    /// ViewModel горизонтальной и вертикальной линеек редактора.
    /// Хранит всё состояние необходимое для отрисовки линеек:
    /// зум, поля страницы, маркеры отступов, маркеры колонок таблицы.
    /// Обновляется из TextEditorViewModel при смене зума, режима, активного абзаца.
    /// </summary>
    public sealed class RulerViewModel : ReactiveObject
    {
        private RulerUnits _units;
        private RulerMode _mode;
        private double _zoom = 1.0;
        private bool _isVisible = true;

        // ── Геометрия страницы ────────────────────────────────────────────

        private double _pageWidthMm = 210;
        private double _pageHeightMm = 297;
        private double _marginLeftMm = 30;
        private double _marginRightMm = 15;
        private double _marginTopMm = 25;
        private double _marginBottomMm = 25;
        private double _pageOffsetXPx = 0;

        // ── Отступы активного абзаца ──────────────────────────────────────

        private double _leftIndentMm = 0;
        private double _firstLineIndentMm = 0;
        private double _rightIndentMm = 0;

        // ── Скролл (для вертикальной линейки) ─────────────────────────────

        private double _scrollOffsetY = 0;
        private double _viewportHeight = 600;

        // ── Drag состояние ────────────────────────────────────────────────

        private RulerIndentMarkerType? _draggingIndentMarker;
        private int _draggingColumnIndex = -1;

        // ── Публичные свойства ────────────────────────────────────────────

        /// <summary>Единицы измерения линейки.</summary>
        public RulerUnits Units
        {
            get => _units;
            set => this.RaiseAndSetIfChanged(ref _units, value);
        }

        /// <summary>Текущий режим линейки — абзац или таблица.</summary>
        public RulerMode Mode
        {
            get => _mode;
            set => this.RaiseAndSetIfChanged(ref _mode, value);
        }

        /// <summary>Текущий зум редактора. Используется для пересчёта координат.</summary>
        public double Zoom
        {
            get => _zoom;
            set => this.RaiseAndSetIfChanged(ref _zoom, Math.Max(0.25, Math.Min(5.0, value)));
        }

        /// <summary>Видимость линейки. Управляется из настроек.</summary>
        public bool IsVisible
        {
            get => _isVisible;
            set => this.RaiseAndSetIfChanged(ref _isVisible, value);
        }

        // ── Геометрия страницы ────────────────────────────────────────────

        /// <summary>Ширина страницы в мм.</summary>
        public double PageWidthMm
        {
            get => _pageWidthMm;
            set => this.RaiseAndSetIfChanged(ref _pageWidthMm, value);
        }

        /// <summary>Высота страницы в мм.</summary>
        public double PageHeightMm
        {
            get => _pageHeightMm;
            set => this.RaiseAndSetIfChanged(ref _pageHeightMm, value);
        }

        /// <summary>Левое поле страницы в мм.</summary>
        public double MarginLeftMm
        {
            get => _marginLeftMm;
            set => this.RaiseAndSetIfChanged(ref _marginLeftMm, value);
        }

        /// <summary>Правое поле страницы в мм.</summary>
        public double MarginRightMm
        {
            get => _marginRightMm;
            set => this.RaiseAndSetIfChanged(ref _marginRightMm, value);
        }

        /// <summary>Верхнее поле страницы в мм.</summary>
        public double MarginTopMm
        {
            get => _marginTopMm;
            set => this.RaiseAndSetIfChanged(ref _marginTopMm, value);
        }

        /// <summary>Нижнее поле страницы в мм.</summary>
        public double MarginBottomMm
        {
            get => _marginBottomMm;
            set => this.RaiseAndSetIfChanged(ref _marginBottomMm, value);
        }

        /// <summary>
        /// X-смещение левого края страницы в пикселях относительно ScrollViewer.
        /// Используется горизонтальной линейкой для выравнивания шкалы под страницей.
        /// Обновляется при изменении зума и ширины канваса.
        /// </summary>
        public double PageOffsetXPx
        {
            get => _pageOffsetXPx;
            set => this.RaiseAndSetIfChanged(ref _pageOffsetXPx, value);
        }

        // ── Отступы активного абзаца ──────────────────────────────────────

        /// <summary>Левый отступ активного абзаца в мм.</summary>
        public double LeftIndentMm
        {
            get => _leftIndentMm;
            set
            {
                this.RaiseAndSetIfChanged(ref _leftIndentMm, value);
                UpdateIndentMarkers();
            }
        }

        /// <summary>Отступ первой строки активного абзаца в мм.</summary>
        public double FirstLineIndentMm
        {
            get => _firstLineIndentMm;
            set
            {
                this.RaiseAndSetIfChanged(ref _firstLineIndentMm, value);
                UpdateIndentMarkers();
            }
        }

        /// <summary>Правый отступ активного абзаца в мм.</summary>
        public double RightIndentMm
        {
            get => _rightIndentMm;
            set
            {
                this.RaiseAndSetIfChanged(ref _rightIndentMm, value);
                UpdateIndentMarkers();
            }
        }

        // ── Скролл ────────────────────────────────────────────────────────

        /// <summary>Текущее смещение скролла по Y в пикселях.</summary>
        public double ScrollOffsetY
        {
            get => _scrollOffsetY;
            set => this.RaiseAndSetIfChanged(ref _scrollOffsetY, value);
        }

        /// <summary>Высота видимой области в пикселях.</summary>
        public double ViewportHeight
        {
            get => _viewportHeight;
            set => this.RaiseAndSetIfChanged(ref _viewportHeight, value);
        }

        // ── Маркеры ───────────────────────────────────────────────────────

        /// <summary>
        /// Маркеры отступов абзаца.
        /// Активны только когда Mode == Paragraph.
        /// </summary>
        public List<RulerIndentMarker> IndentMarkers { get; } = new()
        {
            new RulerIndentMarker { Type = RulerIndentMarkerType.LeftIndent,      Position = 0 },
            new RulerIndentMarker { Type = RulerIndentMarkerType.FirstLineIndent, Position = 0 },
            new RulerIndentMarker { Type = RulerIndentMarkerType.RightIndent,     Position = 0 }
        };

        /// <summary>
        /// Маркеры колонок таблицы.
        /// Активны только когда Mode == Table.
        /// Заполняются при входе каретки в таблицу.
        /// </summary>
        public List<RulerColumnMarker> ColumnMarkers { get; } = new();

        // ── Drag ──────────────────────────────────────────────────────────

        /// <summary>Маркер отступа который сейчас перетаскивается. Null — нет drag.</summary>
        public RulerIndentMarkerType? DraggingIndentMarker
        {
            get => _draggingIndentMarker;
            set => this.RaiseAndSetIfChanged(ref _draggingIndentMarker, value);
        }

        /// <summary>Индекс колонки маркер которой перетаскивается. -1 — нет drag.</summary>
        public int DraggingColumnIndex
        {
            get => _draggingColumnIndex;
            set => this.RaiseAndSetIfChanged(ref _draggingColumnIndex, value);
        }

        // ── События ───────────────────────────────────────────────────────

        /// <summary>
        /// Вызывается когда пользователь перетащил маркер отступа.
        /// Параметры: тип маркера, новое значение в мм.
        /// DocumentViewModel применяет значение к активному абзацу.
        /// </summary>
        public event Action<RulerIndentMarkerType, double>? IndentMarkerChanged;

        /// <summary>
        /// Вызывается когда пользователь перетащил маркер колонки таблицы.
        /// Параметры: индекс колонки, новая ширина в мм.
        /// DocumentViewModel применяет значение к таблице.
        /// </summary>
        public event Action<int, double>? ColumnWidthChanged;

        // ── Вспомогательные методы ────────────────────────────────────────

        /// <summary>
        /// Конвертирует миллиметры в единицы линейки.
        /// </summary>
        public double MmToUnits(double mm)
            => Units == RulerUnits.Inches ? mm / 25.4 : mm / 10.0;

        /// <summary>
        /// Конвертирует единицы линейки в миллиметры.
        /// </summary>
        public double UnitsToMm(double units)
            => Units == RulerUnits.Inches ? units * 25.4 : units * 10.0;

        /// <summary>
        /// Шаг основных делений линейки в единицах.
        /// 1 см или 1 дюйм.
        /// </summary>
        public double MajorTickInterval => 1.0;

        /// <summary>
        /// Шаг малых делений линейки в единицах.
        /// 0.5 см или 0.25 дюйма.
        /// </summary>
        public double MinorTickInterval
            => Units == RulerUnits.Inches ? 0.25 : 0.5;

        /// <summary>
        /// Шаг мельчайших делений линейки в единицах.
        /// 0.1 см или 0.125 дюйма.
        /// </summary>
        public double TinyTickInterval
            => Units == RulerUnits.Inches ? 0.125 : 0.1;

        /// <summary>
        /// Обновляет геометрию страницы из настроек PageSettings.
        /// Вызывается из TextEditorViewModel при загрузке документа
        /// или изменении полей страницы.
        /// </summary>
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

        /// <summary>
        /// Обновляет маркеры отступов из свойств активного абзаца.
        /// Вызывается из TextEditorViewModel при смене активного абзаца.
        /// Значения в pt конвертируются в мм: 1pt = 25.4/72 мм.
        /// </summary>
        public void UpdateFromParagraphContext(
            double leftIndentPt,
            double firstLineIndentPt,
            double rightIndentPt)
        {
            // pt → мм: 1 pt = 25.4 / 72 мм
            const double PtToMm = 25.4 / 72.0;
            _leftIndentMm = leftIndentPt * PtToMm;
            _firstLineIndentMm = firstLineIndentPt * PtToMm;
            _rightIndentMm = rightIndentPt * PtToMm;
            UpdateIndentMarkers();
            this.RaisePropertyChanged(nameof(LeftIndentMm));
            this.RaisePropertyChanged(nameof(FirstLineIndentMm));
            this.RaisePropertyChanged(nameof(RightIndentMm));
        }

        /// <summary>
        /// Обновляет маркеры колонок таблицы.
        /// Вызывается из DocumentCanvas когда каретка входит в таблицу.
        /// columnOffsetsMm — X-позиции левых краёв колонок в мм относительно текстовой области.
        /// columnWidthsMm — ширины колонок в мм.
        /// </summary>
        public void UpdateTableColumns(
            IReadOnlyList<double> columnOffsetsMm,
            IReadOnlyList<double> columnWidthsMm)
        {
            ColumnMarkers.Clear();

            for (int i = 0; i < columnWidthsMm.Count; i++)
            {
                double rightEdge = (i < columnOffsetsMm.Count ? columnOffsetsMm[i] : 0)
                                   + columnWidthsMm[i];
                ColumnMarkers.Add(new RulerColumnMarker
                {
                    ColumnIndex = i,
                    RightEdge = MmToUnits(rightEdge)
                });
            }

            Mode = RulerMode.Table;
        }

        /// <summary>
        /// Переключает линейку в режим абзаца.
        /// Вызывается из DocumentCanvas когда каретка выходит из таблицы.
        /// </summary>
        public void SwitchToParagraphMode()
        {
            if (Mode == RulerMode.Paragraph) return;
            ColumnMarkers.Clear();
            Mode = RulerMode.Paragraph;
        }

        /// <summary>
        /// Начинает drag маркера отступа.
        /// Вызывается из HorizontalRulerControl при нажатии на маркер.
        /// </summary>
        public void BeginIndentDrag(RulerIndentMarkerType markerType)
        {
            DraggingIndentMarker = markerType;
        }

        /// <summary>
        /// Обновляет позицию маркера отступа во время drag.
        /// positionUnits — позиция в единицах линейки относительно начала текстовой области.
        /// </summary>
        public void UpdateIndentDrag(double positionUnits)
        {
            if (DraggingIndentMarker is null) return;

            var marker = GetIndentMarker(DraggingIndentMarker.Value);
            if (marker is null) return;

            // Ограничиваем позицию в пределах текстовой области.
            double textWidthUnits = MmToUnits(PageWidthMm - MarginLeftMm - MarginRightMm);
            marker.Position = Math.Max(0, Math.Min(positionUnits, textWidthUnits));

            this.RaisePropertyChanged(nameof(IndentMarkers));
        }

        /// <summary>
        /// Завершает drag маркера отступа и применяет значение.
        /// Вызывается из HorizontalRulerControl при отпускании кнопки мыши.
        /// </summary>
        public void EndIndentDrag()
        {
            if (DraggingIndentMarker is null) return;

            var marker = GetIndentMarker(DraggingIndentMarker.Value);
            if (marker is not null)
                IndentMarkerChanged?.Invoke(DraggingIndentMarker.Value, UnitsToMm(marker.Position));

            DraggingIndentMarker = null;
        }

        /// <summary>
        /// Начинает drag маркера колонки таблицы.
        /// Вызывается из HorizontalRulerControl при нажатии на маркер колонки.
        /// </summary>
        public void BeginColumnDrag(int columnIndex)
        {
            DraggingColumnIndex = columnIndex;
        }

        /// <summary>
        /// Обновляет позицию маркера колонки во время drag.
        /// rightEdgeUnits — новый правый край колонки в единицах линейки.
        /// </summary>
        public void UpdateColumnDrag(double rightEdgeUnits)
        {
            if (DraggingColumnIndex < 0 || DraggingColumnIndex >= ColumnMarkers.Count) return;

            double textWidthUnits = MmToUnits(PageWidthMm - MarginLeftMm - MarginRightMm);

            // Минимальная ширина колонки — 5 мм.
            double minRightEdge = DraggingColumnIndex > 0
                ? ColumnMarkers[DraggingColumnIndex - 1].RightEdge + MmToUnits(5)
                : MmToUnits(5);

            // Максимальный правый край — либо левый край следующего маркера минус 5мм, либо конец.
            double maxRightEdge = DraggingColumnIndex < ColumnMarkers.Count - 1
                ? ColumnMarkers[DraggingColumnIndex + 1].RightEdge - MmToUnits(5)
                : textWidthUnits;

            ColumnMarkers[DraggingColumnIndex].RightEdge =
                Math.Max(minRightEdge, Math.Min(rightEdgeUnits, maxRightEdge));

            this.RaisePropertyChanged(nameof(ColumnMarkers));
        }

        /// <summary>
        /// Завершает drag маркера колонки и применяет новую ширину.
        /// Вызывается из HorizontalRulerControl при отпускании кнопки мыши.
        /// </summary>
        public void EndColumnDrag()
        {
            if (DraggingColumnIndex < 0 || DraggingColumnIndex >= ColumnMarkers.Count) return;

            double leftEdgeMm = DraggingColumnIndex > 0
                ? UnitsToMm(ColumnMarkers[DraggingColumnIndex - 1].RightEdge)
                : 0;
            double rightEdgeMm = UnitsToMm(ColumnMarkers[DraggingColumnIndex].RightEdge);
            double newWidthMm = rightEdgeMm - leftEdgeMm;

            ColumnWidthChanged?.Invoke(DraggingColumnIndex, Math.Max(5, newWidthMm));

            DraggingColumnIndex = -1;
        }

        // ── Внутренние методы ─────────────────────────────────────────────

        /// <summary>
        /// Синхронизирует позиции маркеров с текущими значениями отступов.
        /// </summary>
        private void UpdateIndentMarkers()
        {
            GetIndentMarker(RulerIndentMarkerType.LeftIndent)!.Position
                = MmToUnits(_leftIndentMm);
            GetIndentMarker(RulerIndentMarkerType.FirstLineIndent)!.Position
                = MmToUnits(_firstLineIndentMm);
            GetIndentMarker(RulerIndentMarkerType.RightIndent)!.Position
                = MmToUnits(_rightIndentMm);
            this.RaisePropertyChanged(nameof(IndentMarkers));
        }

        /// <summary>
        /// Возвращает маркер отступа по типу.
        /// </summary>
        private RulerIndentMarker? GetIndentMarker(RulerIndentMarkerType type)
        {
            foreach (var m in IndentMarkers)
                if (m.Type == type) return m;
            return null;
        }

        /// <summary>
        /// Обновляет позицию маркера отступа без ограничения минимального значения.
        /// Стреляет IndentMarkerChanged немедленно для живого предпросмотра:
        /// текст сдвигается прямо во время drag без ожидания EndIndentDrag.
        /// </summary>
        public void UpdateIndentDragUnclamped(double positionUnits)
        {
            if (DraggingIndentMarker is null) return;
            var marker = GetIndentMarker(DraggingIndentMarker.Value);
            if (marker is null) return;

            // Только верхний предел — не выходим за правый край страницы.
            double pageWidthUnits = MmToUnits(PageWidthMm);
            marker.Position = Math.Min(positionUnits, pageWidthUnits);

            this.RaisePropertyChanged(nameof(IndentMarkers));

            // Живой предпросмотр — применяем изменение немедленно.
            // EndIndentDrag повторно вызовет это же событие — это нормально,
            // DocumentViewModel идемпотентен для одинакового значения.
            IndentMarkerChanged?.Invoke(DraggingIndentMarker.Value, UnitsToMm(marker.Position));
        }
    }
}