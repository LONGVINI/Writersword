using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Views.Dialogs
{
    /// <summary>
    /// Состояние окна диаграммы без окна: настройки, лист данных и цвета долей, и
    /// перевод между ними и моделью диаграммы. Отдельно от окна — чтобы проверяться
    /// без интерфейса.
    ///
    /// Лист: строка 0 — имена рядов, столбец 0 — подписи категорий (у точечной и
    /// пузырьковой — значения X), дальше по столбцу на ряд; у пузырьковой у ряда два
    /// столбца — Y и размер. Настройки (<see cref="Draft"/>) — модель без данных: вид,
    /// подписи, оси и свойства рядов по порядку (цвет, свой вид, ось, маркеры).
    /// </summary>
    public sealed class ChartEditorState
    {
        public ChartEditorState(ChartModel draft, ChartSheet sheet, List<string?> pointColors)
        {
            Draft = draft;
            Sheet = sheet;
            PointColors = pointColors;
        }

        public ChartModel Draft { get; set; }

        public ChartSheet Sheet { get; set; }

        /// <summary>Цвета долей круговой и кольцевой по категориям; null — цвет палитры.</summary>
        public List<string?> PointColors { get; set; }

        public ChartKind Kind => Draft.Kind;

        public bool Bubble => Draft.Kind == ChartKind.Bubble;

        public int SeriesCount => Bubble ? Math.Max(0, (Sheet.ColumnCount - 1) / 2) : Math.Max(0, Sheet.ColumnCount - 1);

        public int CategoryCount => Math.Max(0, Sheet.RowCount - 1);

        public int ValueColumn(int series) => Bubble ? 1 + series * 2 : 1 + series;

        public int SeriesOfColumn(int column) => column <= 0 ? -1 : Bubble ? (column - 1) / 2 : column - 1;

        public bool IsSizeColumn(int column) => Bubble && column > 0 && (column - 1) % 2 == 1;

        public static bool IsXY(ChartKind kind) => kind is ChartKind.Scatter or ChartKind.Bubble;

        public static bool IsCircular(ChartKind kind) => kind is ChartKind.Pie or ChartKind.Doughnut;

        public static bool CanCombine(ChartKind kind) => kind is ChartKind.Column or ChartKind.Line or ChartKind.Area;

        public ChartEditorState Clone()
            => new(Draft.Clone(), Sheet.Clone(), new List<string?>(PointColors));

        /// <summary>Совпадает ли с другим состоянием во всём, что уходит в диаграмму.</summary>
        public bool SameAs(ChartEditorState other)
            => other is not null
               && Draft.ComputeFingerprint() == other.Draft.ComputeFingerprint()
               && Sheet.ContentEquals(other.Sheet)
               && PointColors.SequenceEqual(other.PointColors);

        /// <summary>Число в тексте ячейки так, чтобы оно читалось обратно тем же числом.</summary>
        public static string FormatNumber(double? value)
            => value is double v ? v.ToString(CultureInfo.CurrentCulture) : string.Empty;

        // ── Модель → лист ────────────────────────────────────────────────

        public static ChartEditorState FromChart(ChartModel chart)
        {
            var draft = chart.Clone();

            bool xy = IsXY(chart.Kind);
            bool bubble = chart.Kind == ChartKind.Bubble;
            int rows = xy ? 0 : chart.CategoryCount;
            foreach (var series in chart.Series) rows = Math.Max(rows, series.Values.Count);
            rows = Math.Max(1, rows);

            int seriesCount = Math.Max(1, chart.Series.Count);
            var sheet = new ChartSheet(rows + 1, 1 + seriesCount * (bubble ? 2 : 1));

            var first = chart.Series.Count > 0 ? chart.Series[0] : null;
            for (int r = 0; r < rows; r++)
            {
                if (xy)
                {
                    double? x = first?.XValues is { } xs && r < xs.Count ? xs[r] : r + 1;
                    sheet.Set(r + 1, 0, FormatNumber(x));
                }
                else
                {
                    // Категория без подписи остаётся пустой ячейкой: номер, который диаграмма
                    // показывает на её месте, — не её текст.
                    sheet.Set(r + 1, 0, r < chart.Categories.Count ? chart.Categories[r] ?? string.Empty : string.Empty);
                }
            }

            for (int s = 0; s < chart.Series.Count; s++)
            {
                var series = chart.Series[s];
                int column = bubble ? 1 + s * 2 : 1 + s;
                sheet.Set(0, column, series.Name);
                for (int r = 0; r < rows; r++)
                {
                    sheet.Set(r + 1, column, r < series.Values.Count ? FormatNumber(series.Values[r]) : string.Empty);
                    if (bubble)
                        sheet.Set(r + 1, column + 1, series.BubbleSizes is { } sizes && r < sizes.Count ? FormatNumber(sizes[r]) : "1");
                }
            }

            if (chart.Series.Count == 0)
            {
                sheet.Set(0, 1, "Ряд 1");
                draft.Series.Add(new ChartSeries { Name = "Ряд 1" });
            }

            var points = new List<string?>(rows);
            for (int r = 0; r < rows; r++)
                points.Add(first?.PointColors is { } pc && r < pc.Count ? pc[r] : null);

            return new ChartEditorState(draft, sheet, points);
        }

        // ── Лист → модель ────────────────────────────────────────────────

        public ChartModel Build()
        {
            var chart = Draft.Clone();
            bool xy = IsXY(chart.Kind);
            bool bubble = chart.Kind == ChartKind.Bubble;
            bool circular = IsCircular(chart.Kind);
            int rows = CategoryCount;

            chart.Categories = new List<string>();
            if (!xy)
            {
                for (int r = 0; r < rows; r++) chart.Categories.Add(Sheet.Get(r + 1, 0));

                // Хвост пустых подписей не нужен: подпись без текста у категории и так пустая.
                while (chart.Categories.Count > 0 && chart.Categories[^1].Length == 0) chart.Categories.RemoveAt(chart.Categories.Count - 1);
            }

            var xs = new List<double?>();
            if (xy)
                for (int r = 0; r < rows; r++) xs.Add(ChartDataGrid.TryNumber(Sheet.Get(r + 1, 0), out double x) ? x : null);

            List<string?>? pointColors = null;
            if (circular && PointColors.Take(rows).Any(c => c is not null))
                pointColors = new List<string?>(PointColors.Take(rows));

            chart.Series = new List<ChartSeries>();
            for (int s = 0; s < SeriesCount; s++)
            {
                var meta = s < Draft.Series.Count ? Draft.Series[s].Clone() : new ChartSeries();
                int column = ValueColumn(s);

                meta.Name = Sheet.Get(0, column).Trim();
                meta.Values = new List<double?>(rows);
                for (int r = 0; r < rows; r++)
                    meta.Values.Add(ChartDataGrid.TryNumber(Sheet.Get(r + 1, column), out double v) ? v : null);

                meta.XValues = xy ? new List<double?>(xs) : null;

                if (bubble)
                {
                    meta.BubbleSizes = new List<double?>(rows);
                    for (int r = 0; r < rows; r++)
                        meta.BubbleSizes.Add(ChartDataGrid.TryNumber(Sheet.Get(r + 1, column + 1), out double size) ? size : 1.0);
                }
                else
                {
                    meta.BubbleSizes = null;
                }

                if (circular) meta.PointColors = pointColors is null ? null : new List<string?>(pointColors);

                if (!CanCombine(chart.Kind))
                {
                    meta.SecondaryAxis = false;
                    if (meta.Kind is ChartKind k && k != chart.Kind && !(chart.Kind == ChartKind.Bar && k == ChartKind.Line)) meta.Kind = null;
                }
                if (meta.Kind == chart.Kind) meta.Kind = null;

                chart.Series.Add(meta);
            }

            if (!chart.HasSecondaryAxis)
            {
                chart.SecondaryAxisTitle = null;
                chart.SecondaryAxisMin = null;
                chart.SecondaryAxisMax = null;
                chart.SecondaryNumberFormat = null;
            }

            return chart;
        }

        // ── Порядок в листе ──────────────────────────────────────────────

        /// <summary>
        /// Приводит состояние в порядок после правки листа: у пузырьковой у каждого ряда
        /// есть столбец размеров, у каждого ряда — свойства и имя, у каждой строки —
        /// место цвета доли.
        /// </summary>
        public void Normalize()
        {
            if (Bubble && (Sheet.ColumnCount - 1) % 2 == 1)
            {
                int sizeColumn = Sheet.ColumnCount;
                Sheet.EnsureSize(Sheet.RowCount, sizeColumn + 1);
                for (int r = 1; r < Sheet.RowCount; r++)
                    if (Sheet.Get(r, sizeColumn - 1).Length > 0) Sheet.Set(r, sizeColumn, "1");
            }

            Sheet.EnsureSize(Math.Max(2, Sheet.RowCount), Math.Max(Bubble ? 3 : 2, Sheet.ColumnCount));

            while (Draft.Series.Count < SeriesCount)
            {
                var template = Draft.Series.Count > 0 ? Draft.Series[^1] : null;
                Draft.Series.Add(NewSeriesLike(template));
            }
            while (Draft.Series.Count > SeriesCount && Draft.Series.Count > 1) Draft.Series.RemoveAt(Draft.Series.Count - 1);

            for (int s = 0; s < SeriesCount; s++)
            {
                int column = ValueColumn(s);
                if (Sheet.Get(0, column).Trim().Length == 0) Sheet.Set(0, column, UniqueSeriesName(s));
            }

            while (PointColors.Count < CategoryCount) PointColors.Add(null);
            while (PointColors.Count > CategoryCount) PointColors.RemoveAt(PointColors.Count - 1);
        }

        /// <summary>Свойства нового ряда: как у соседнего, без своего цвета, вида и оси.</summary>
        public static ChartSeries NewSeriesLike(ChartSeries? template) => new()
        {
            ShowMarkers = template?.ShowMarkers ?? true,
            Smooth = template?.Smooth ?? false,
            ShowLine = template?.ShowLine ?? true
        };

        public string UniqueSeriesName(int index)
        {
            var names = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
            for (int s = 0; s < SeriesCount; s++) names.Add(Sheet.Get(0, ValueColumn(s)).Trim());
            int n = index + 1;
            while (names.Contains("Ряд " + n)) n++;
            return "Ряд " + n;
        }

        public string UniqueCategoryName()
        {
            var names = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
            for (int r = 1; r < Sheet.RowCount; r++) names.Add(Sheet.Get(r, 0).Trim());
            int n = 1;
            while (names.Contains("Категория " + n)) n++;
            return "Категория " + n;
        }

        // ── Строки и ряды ────────────────────────────────────────────────

        /// <summary>Вставляет count категорий перед строкой листа at (1 — первая категория).</summary>
        public void InsertRows(int at, int count)
        {
            at = Math.Clamp(at, 1, Sheet.RowCount);
            Sheet.InsertRows(at, count);
            for (int i = 0; i < count; i++)
            {
                if (!IsXY(Kind)) Sheet.Set(at + i, 0, UniqueCategoryName());
                if (Bubble)
                    for (int s = 0; s < SeriesCount; s++) Sheet.Set(at + i, ValueColumn(s) + 1, "1");
                PointColors.Insert(Math.Clamp(at - 1 + i, 0, PointColors.Count), null);
            }
            Normalize();
        }

        /// <summary>Удаляет строки листа (категории); одна категория остаётся всегда.</summary>
        public void RemoveRows(IReadOnlyList<int> rows)
        {
            var sorted = rows.Where(r => r >= 1 && r < Sheet.RowCount).Distinct().OrderBy(r => r).ToList();
            if (sorted.Count >= CategoryCount) sorted.RemoveAt(sorted.Count - 1);
            for (int i = sorted.Count - 1; i >= 0; i--)
            {
                Sheet.RemoveRows(sorted[i], 1);
                if (sorted[i] - 1 < PointColors.Count) PointColors.RemoveAt(sorted[i] - 1);
            }
            Normalize();
        }

        /// <summary>Вставляет новый ряд на место at (0 — первый ряд). Возвращает его первый столбец.</summary>
        public int InsertSeries(int at)
        {
            at = Math.Clamp(at, 0, SeriesCount);
            int width = Bubble ? 2 : 1;
            int column = ValueColumn(at);
            Sheet.InsertColumns(column, width);

            var template = Draft.Series.Count > 0 ? Draft.Series[Math.Clamp(at - 1, 0, Draft.Series.Count - 1)] : null;
            Draft.Series.Insert(Math.Min(at, Draft.Series.Count), NewSeriesLike(template));

            Sheet.Set(0, column, UniqueSeriesName(at));
            if (Bubble)
                for (int r = 1; r < Sheet.RowCount; r++) Sheet.Set(r, column + 1, "1");
            Normalize();
            return column;
        }

        /// <summary>Удаляет ряды; один ряд остаётся всегда.</summary>
        public void RemoveSeries(IReadOnlyList<int> series)
        {
            var sorted = series.Where(s => s >= 0 && s < SeriesCount).Distinct().OrderBy(s => s).ToList();
            if (sorted.Count >= SeriesCount) sorted.RemoveAt(sorted.Count - 1);
            for (int i = sorted.Count - 1; i >= 0; i--)
            {
                int s = sorted[i];
                Sheet.RemoveColumns(ValueColumn(s), Bubble ? 2 : 1);
                if (s < Draft.Series.Count) Draft.Series.RemoveAt(s);
            }
            Normalize();
        }

        // ── Вид ──────────────────────────────────────────────────────────

        /// <summary>Комбинированная: ряды разного вида или часть рядов на оси справа.</summary>
        public bool IsCombo()
        {
            if (!CanCombine(Kind)) return false;
            foreach (var series in Draft.Series)
                if (series.SecondaryAxis || (series.Kind is ChartKind k && k != Kind)) return true;
            return false;
        }

        /// <summary>
        /// Новый вид всей диаграммы: все ряды становятся этого вида, как «Изменить тип
        /// диаграммы» у Word; вспомогательная ось снимается.
        /// </summary>
        public static ChartEditorState WithKind(ChartEditorState state, ChartKind kind)
        {
            var current = state.Build();
            var next = current.WithKind(kind);
            foreach (var series in next.Series)
            {
                series.Kind = null;
                series.SecondaryAxis = false;
            }
            return FromChart(next);
        }

        /// <summary>
        /// Комбинированная, как первая в галерее Word: последний ряд — линией по
        /// вспомогательной оси, остальные — столбцами.
        /// </summary>
        public static ChartEditorState AsCombo(ChartEditorState state)
        {
            var chart = state.Build();
            if (!CanCombine(chart.Kind)) chart = chart.WithKind(ChartKind.Column);
            if (chart.Series.Count < 2)
            {
                var added = new ChartSeries { Name = "Ряд " + (chart.Series.Count + 1) };
                for (int i = 0; i < Math.Max(1, chart.CategoryCount); i++) added.Values.Add(null);
                chart.Series.Add(added);
            }

            var last = chart.Series[^1];
            last.Kind = chart.Kind == ChartKind.Line ? ChartKind.Column : ChartKind.Line;
            last.SecondaryAxis = true;
            last.ShowMarkers = false;
            return FromChart(chart);
        }

        /// <summary>Строки и столбцы местами: ряды становятся категориями.</summary>
        public static ChartEditorState Transposed(ChartEditorState state)
        {
            var chart = state.Build().Transposed();
            foreach (var series in chart.Series) series.SecondaryAxis = false;
            return FromChart(chart);
        }
    }
}
