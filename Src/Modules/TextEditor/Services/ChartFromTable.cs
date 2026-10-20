using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Данные диаграммы из таблицы документа — так, как их понимает Excel, когда
    /// строит диаграмму по выделенным ячейкам.
    ///
    /// Первая строка с текстом — имена рядов, первый столбец с текстом — подписи
    /// категорий. Столбцы, где нет ни одного числа (единицы, примечания), в диаграмму
    /// не идут. Рядами становятся столбцы, если строк данных не меньше, чем столбцов,
    /// иначе — строки. Число пишется через точку или запятую, с пробелами разрядов и со
    /// знаком процента.
    /// </summary>
    public static class ChartFromTable
    {
        /// <summary>
        /// Модель гистограммы по прямоугольнику ячеек таблицы. null — в ячейках нет ни
        /// одного числа.
        /// </summary>
        public static ChartModel? Build(TableBlock table, int firstRow, int firstColumn, int lastRow, int lastColumn)
        {
            if (table is null) return null;

            int r0 = Math.Max(0, Math.Min(firstRow, lastRow));
            int r1 = Math.Min(table.RowCount - 1, Math.Max(firstRow, lastRow));
            int c0 = Math.Max(0, Math.Min(firstColumn, lastColumn));
            int c1 = Math.Min(table.ColumnCount - 1, Math.Max(firstColumn, lastColumn));
            if (r1 < r0 || c1 < c0) return null;

            var grid = new List<List<string>>();
            for (int r = r0; r <= r1; r++)
            {
                var row = new List<string>();
                for (int c = c0; c <= c1; c++)
                {
                    // Объединённая ячейка отдаёт текст только своему верхнему левому месту.
                    var cell = table.GetCell(r, c);
                    bool origin = cell is not null && cell.Row == r && cell.Column == c;
                    row.Add(origin ? CellText(cell!) : string.Empty);
                }
                grid.Add(row);
            }

            return Build(grid);
        }

        /// <summary>Модель по сетке текстов ячеек (строки — первый индекс).</summary>
        public static ChartModel? Build(IReadOnlyList<IReadOnlyList<string>> cells)
        {
            var grid = new List<List<string>>();
            foreach (var row in cells) grid.Add(new List<string>(row));
            return Build(grid);
        }

        private static ChartModel? Build(List<List<string>> grid)
        {
            // Пустые строки и столбцы по краям и внутри выделения ничего не значат.
            grid.RemoveAll(row => row.TrueForAll(string.IsNullOrWhiteSpace));
            if (grid.Count == 0) return null;

            int columns = 0;
            foreach (var row in grid) columns = Math.Max(columns, row.Count);
            foreach (var row in grid) while (row.Count < columns) row.Add(string.Empty);

            for (int c = columns - 1; c >= 0; c--)
            {
                bool empty = true;
                foreach (var row in grid)
                    if (!string.IsNullOrWhiteSpace(row[c])) { empty = false; break; }
                if (!empty) continue;
                foreach (var row in grid) row.RemoveAt(c);
                columns--;
            }
            if (columns == 0) return null;

            // Подписи категорий: первый столбец, если в нём есть текст, а не одни числа.
            bool labelColumn = false;
            for (int r = 0; r < grid.Count; r++)
            {
                if (r == 0) continue;
                if (!string.IsNullOrWhiteSpace(grid[r][0]) && ParseNumber(grid[r][0]) is null) { labelColumn = true; break; }
            }

            // Столбец номеров по порядку («№»: 1, 2, 3…) — тоже подписи, а не ряд данных:
            // числа в нём ничего не измеряют. Если за ним идёт столбец текста
            // («Наименование»), подписи берутся из него, а номера не идут никуда.
            int labelIndex = 0;
            if (!labelColumn && columns > 1)
            {
                bool provisionalHeader = false;
                for (int c = 1; c < columns; c++)
                    if (!string.IsNullOrWhiteSpace(grid[0][c]) && ParseNumber(grid[0][c]) is null) { provisionalHeader = true; break; }

                // Узнаётся по заголовку столбца: «№», «#», «No», «п/п» или пусто.
                string head = provisionalHeader ? grid[0][0].Trim().ToLowerInvariant().TrimEnd('.') : "?";
                bool numberHeader = head.Length == 0 || head.Contains('№') || head is "#" or "no" or "n" or "nr" or "п/п" or "пп";

                int start = provisionalHeader ? 1 : 0;
                bool ordinal = numberHeader && grid.Count - start >= 2;
                for (int r = start; r < grid.Count && ordinal; r++)
                    ordinal = ParseNumber(grid[r][0]) is double n && n == r - start + 1;

                if (ordinal)
                {
                    labelColumn = true;

                    bool textNext = columns > 2;
                    for (int r = start; r < grid.Count && textNext; r++)
                        if (!string.IsNullOrWhiteSpace(grid[r][1]) && ParseNumber(grid[r][1]) is not null) textNext = false;
                    if (textNext) labelIndex = 1;
                }
            }

            // Имена рядов: первая строка, если в ней есть текст в столбцах данных или
            // она вся не числа.
            bool headerRow = false;
            for (int c = labelColumn ? labelIndex + 1 : 0; c < columns; c++)
            {
                if (!string.IsNullOrWhiteSpace(grid[0][c]) && ParseNumber(grid[0][c]) is null) { headerRow = true; break; }
            }
            if (grid.Count == 1) headerRow = false;

            int firstDataRow = headerRow ? 1 : 0;
            int firstDataColumn = labelColumn ? labelIndex + 1 : 0;

            // Столбцы данных — те, где есть хоть одно число.
            var dataColumns = new List<int>();
            for (int c = firstDataColumn; c < columns; c++)
            {
                for (int r = firstDataRow; r < grid.Count; r++)
                {
                    if (ParseNumber(grid[r][c]) is not null) { dataColumns.Add(c); break; }
                }
            }
            if (dataColumns.Count == 0) return null;

            int dataRows = grid.Count - firstDataRow;
            if (dataRows <= 0) return null;

            var chart = new ChartModel { Kind = ChartKind.Column };

            bool seriesInColumns = dataRows >= dataColumns.Count;

            // Без подписи в таблице имя по роли: ряд — «Ряд N», категория — «Категория N».
            string ColumnName(int c, int ordinal)
                => headerRow && !string.IsNullOrWhiteSpace(grid[0][c])
                    ? grid[0][c]
                    : (seriesInColumns ? "Ряд " : "Категория ") + (ordinal + 1);

            string RowName(int r, int ordinal)
                => labelColumn && !string.IsNullOrWhiteSpace(grid[r][labelIndex])
                    ? grid[r][labelIndex]
                    : (seriesInColumns ? "Категория " : "Ряд ") + (ordinal + 1);

            if (seriesInColumns)
            {
                for (int r = firstDataRow; r < grid.Count; r++)
                    chart.Categories.Add(RowName(r, r - firstDataRow));

                for (int i = 0; i < dataColumns.Count; i++)
                {
                    int c = dataColumns[i];
                    var series = new ChartSeries { Name = ColumnName(c, i) };
                    for (int r = firstDataRow; r < grid.Count; r++) series.Values.Add(ParseNumber(grid[r][c]));
                    chart.Series.Add(series);
                }
            }
            else
            {
                for (int i = 0; i < dataColumns.Count; i++)
                    chart.Categories.Add(ColumnName(dataColumns[i], i));

                for (int r = firstDataRow; r < grid.Count; r++)
                {
                    var series = new ChartSeries { Name = RowName(r, r - firstDataRow) };
                    foreach (int c in dataColumns) series.Values.Add(ParseNumber(grid[r][c]));
                    chart.Series.Add(series);
                }
            }

            // Одному ряду — его имя названием, как у Word; многим — легенда.
            chart.Legend = chart.Series.Count > 1 ? ChartLegendPosition.Bottom : ChartLegendPosition.None;
            return chart;
        }

        /// <summary>Текст ячейки одной строкой: абзацы через пробел.</summary>
        private static string CellText(TableCell cell)
        {
            var text = new StringBuilder();
            foreach (var paragraph in cell.ParagraphsDeep())
            {
                string part = paragraph.GetPlainText().Replace(Models.Inline.RunModel.ObjectPlaceholder.ToString(), string.Empty).Trim();
                if (part.Length == 0) continue;
                if (text.Length > 0) text.Append(' ');
                text.Append(part);
            }
            return text.ToString().Trim();
        }

        /// <summary>Число ячейки: через точку или запятую, с пробелами разрядов и знаком процента.</summary>
        public static double? ParseNumber(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            string cleaned = text.Trim()
                .Replace(" ", string.Empty)
                .Replace(" ", string.Empty)
                .Replace(" ", string.Empty)
                .Replace('−', '-')
                .Replace(',', '.');

            bool percent = cleaned.EndsWith('%');
            if (percent) cleaned = cleaned.TrimEnd('%');

            if (!double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) return null;
            if (double.IsNaN(value) || double.IsInfinity(value)) return null;
            return percent ? value / 100.0 : value;
        }
    }
}
