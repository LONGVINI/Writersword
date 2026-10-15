using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Оформление таблицы — для правки оформления таблиц.
    ///
    /// Оформление — всё, что меняется у таблицы без её текста: свойства самой
    /// таблицы (стиль, ширина, отступ, выравнивание, обтекание, повтор заголовка,
    /// разрыв), ширины столбцов, высоты строк и вид ячеек (заливка, границы,
    /// выравнивание по высоте, направление текста, поля). Столбец, строка и ячейка
    /// узнаются по Id ячейки, а не по номеру: строка, вставленная или удалённая
    /// правкой, не сдвигает запись соседей. Строка — по ячейке, с которой она
    /// начинается слева, столбец — по ячейке, с которой он начинается сверху.
    ///
    /// Запись пишется строкой JSON: она ложится в <see cref="TableBlock.FormatChange"/>
    /// прежним видом и возвращается на таблицу при отклонении.
    /// </summary>
    public static class TableFrame
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

        /// <summary>Свойства самой таблицы.</summary>
        public sealed class TableLook
        {
            public string? StyleName { get; set; }
            public double WidthPercent { get; set; }
            public double LeftIndentPt { get; set; }
            public TableBlockAlignment Alignment { get; set; }
            public bool BidiVisual { get; set; }
            public TableFloatPosition? FloatPosition { get; set; }
            public bool RepeatHeader { get; set; }
            public TableSplitMode SplitMode { get; set; }
        }

        /// <summary>Высота строки.</summary>
        public sealed class RowLook
        {
            public double HeightPt { get; set; }
            public bool Exact { get; set; }
        }

        /// <summary>Вид ячейки.</summary>
        public sealed class CellLook
        {
            public string? BackgroundColor { get; set; }
            public string? ShadingPattern { get; set; }
            public string? ShadingPatternColor { get; set; }
            public CellBorders Borders { get; set; } = new();
            public VerticalAlignment VerticalAlignment { get; set; }
            public CellTextDirection TextDirection { get; set; }
            public double PaddingTopPt { get; set; }
            public double PaddingBottomPt { get; set; }
            public double PaddingLeftPt { get; set; }
            public double PaddingRightPt { get; set; }
        }

        /// <summary>Оформление таблицы целиком.</summary>
        public sealed class Frame
        {
            public TableLook Table { get; set; } = new();
            // Ключи по порядку: запись одного оформления — одна и та же строка, в каком
            // бы порядке ни лежали ячейки (RevisionSignature сравнивает строки).
            public SortedDictionary<string, TableColumnDefinition> Columns { get; set; } = new(StringComparer.Ordinal);
            public SortedDictionary<string, RowLook> Rows { get; set; } = new(StringComparer.Ordinal);
            public SortedDictionary<string, CellLook> Cells { get; set; } = new(StringComparer.Ordinal);
        }

        // ── Строки и столбцы по ячейкам ──────────────────────────────────

        /// <summary>Ячейка, с которой строка начинается слева; null — у строки нет своей ячейки.</summary>
        public static TableCell? RowKeyCell(TableBlock table, int row)
        {
            TableCell? best = null;
            foreach (var cell in table.Cells)
                if (cell.Row == row && (best is null || cell.Column < best.Column))
                    best = cell;
            return best;
        }

        /// <summary>Ячейка, с которой столбец начинается сверху; null — у столбца нет своей ячейки.</summary>
        public static TableCell? ColumnKeyCell(TableBlock table, int column)
        {
            TableCell? best = null;
            foreach (var cell in table.Cells)
                if (cell.Column == column && (best is null || cell.Row < best.Row))
                    best = cell;
            return best;
        }

        private static string Key(Guid id) => id.ToString("N");

        // ── Запись и возврат ─────────────────────────────────────────────

        /// <summary>Запись оформления таблицы.</summary>
        public static string Capture(TableBlock table) => JsonSerializer.Serialize(Build(table), JsonOptions);

        /// <summary>Оформление таблицы как объект — для сравнения и описания.</summary>
        public static Frame Build(TableBlock table)
        {
            var frame = new Frame
            {
                Table = new TableLook
                {
                    StyleName = table.StyleName,
                    WidthPercent = table.WidthPercent,
                    LeftIndentPt = table.LeftIndentPt,
                    Alignment = table.Alignment,
                    BidiVisual = table.BidiVisual,
                    FloatPosition = table.FloatPosition?.Clone(),
                    RepeatHeader = table.RepeatHeader,
                    SplitMode = table.SplitMode
                }
            };

            for (int column = 0; column < table.ColumnCount && column < table.Columns.Count; column++)
            {
                if (ColumnKeyCell(table, column) is not { } keyCell) continue;
                var definition = table.Columns[column];
                frame.Columns[Key(keyCell.Id)] = new TableColumnDefinition
                {
                    WidthType = definition.WidthType,
                    WidthValue = definition.WidthValue
                };
            }

            for (int row = 0; row < table.RowCount; row++)
            {
                if (RowKeyCell(table, row) is not { } keyCell) continue;
                frame.Rows[Key(keyCell.Id)] = new RowLook
                {
                    HeightPt = table.GetRowMinHeightPt(row),
                    Exact = table.IsRowHeightExact(row)
                };
            }

            foreach (var cell in table.Cells)
            {
                frame.Cells[Key(cell.Id)] = new CellLook
                {
                    BackgroundColor = cell.BackgroundColor,
                    ShadingPattern = cell.ShadingPattern,
                    ShadingPatternColor = cell.ShadingPatternColor,
                    Borders = cell.Borders.Clone(),
                    VerticalAlignment = cell.VerticalAlignment,
                    TextDirection = cell.TextDirection,
                    PaddingTopPt = cell.PaddingTopPt,
                    PaddingBottomPt = cell.PaddingBottomPt,
                    PaddingLeftPt = cell.PaddingLeftPt,
                    PaddingRightPt = cell.PaddingRightPt
                };
            }

            return frame;
        }

        /// <summary>Запись, прочитанная обратно; null — запись не читается.</summary>
        public static Frame? Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                return JsonSerializer.Deserialize<Frame>(json, JsonOptions);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Возвращает таблице оформление из записи. Строки, столбцы и ячейки,
        /// которых в записи нет (вставлены позже), остаются как есть.
        /// </summary>
        public static void Restore(TableBlock table, string json)
        {
            if (Parse(json) is not { } frame) return;

            var look = frame.Table;
            table.StyleName = look.StyleName;
            table.WidthPercent = look.WidthPercent;
            table.LeftIndentPt = look.LeftIndentPt;
            table.Alignment = look.Alignment;
            table.BidiVisual = look.BidiVisual;
            table.FloatPosition = look.FloatPosition?.Clone();
            table.RepeatHeader = look.RepeatHeader;
            table.SplitMode = look.SplitMode;

            for (int column = 0; column < table.ColumnCount && column < table.Columns.Count; column++)
            {
                if (ColumnKeyCell(table, column) is not { } keyCell) continue;
                if (!frame.Columns.TryGetValue(Key(keyCell.Id), out var definition)) continue;
                table.Columns[column] = new TableColumnDefinition
                {
                    WidthType = definition.WidthType,
                    WidthValue = definition.WidthValue
                };
            }

            for (int row = 0; row < table.RowCount; row++)
            {
                if (RowKeyCell(table, row) is not { } keyCell) continue;
                if (!frame.Rows.TryGetValue(Key(keyCell.Id), out var rowLook)) continue;
                table.SetRowMinHeightPt(row, rowLook.HeightPt);
                table.SetRowHeightExact(row, rowLook.Exact);
            }

            foreach (var cell in table.Cells)
            {
                if (!frame.Cells.TryGetValue(Key(cell.Id), out var cellLook)) continue;
                cell.BackgroundColor = cellLook.BackgroundColor;
                cell.ShadingPattern = cellLook.ShadingPattern;
                cell.ShadingPatternColor = cellLook.ShadingPatternColor;
                cell.Borders = cellLook.Borders?.Clone() ?? new CellBorders();
                cell.VerticalAlignment = cellLook.VerticalAlignment;
                cell.TextDirection = cellLook.TextDirection;
                cell.PaddingTopPt = cellLook.PaddingTopPt;
                cell.PaddingBottomPt = cellLook.PaddingBottomPt;
                cell.PaddingLeftPt = cellLook.PaddingLeftPt;
                cell.PaddingRightPt = cellLook.PaddingRightPt;
            }
        }

        /// <summary>Оформление, каким таблица была бы при отклонении правок.</summary>
        public static string RejectView(TableBlock table) => table.FormatChange?.Previous ?? Capture(table);

        // ── Сравнение ────────────────────────────────────────────────────

        /// <summary>
        /// Что различается в оформлении двух записей — по общим строкам, столбцам и
        /// ячейкам: вставленное или удалённое — правка состава, не оформления.
        /// Пусто — оформление одно и то же.
        /// </summary>
        public static HashSet<string> Differences(Frame a, Frame b) => Differences(a, b, forFile: false);

        /// <summary>
        /// То же с допуском сверки с файлом <paramref name="forFile"/>: размеры — с
        /// погрешностью перевода единиц Word (твипы, доли), имя стиля не сверяется —
        /// его оформление уже разложено по ячейкам.
        /// </summary>
        public static HashSet<string> Differences(Frame a, Frame b, bool forFile)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            double lengthTolerance = forFile ? 0.5 : 0.01;
            double sizeTolerance = forFile ? 0.3 : 0.01;

            var ta = a.Table;
            var tb = b.Table;
            if (!forFile && !string.Equals(ta.StyleName ?? string.Empty, tb.StyleName ?? string.Empty, StringComparison.Ordinal))
                result.Add("style");
            if (Math.Abs(ta.WidthPercent - tb.WidthPercent) > sizeTolerance
                || Math.Abs(ta.LeftIndentPt - tb.LeftIndentPt) > lengthTolerance
                || ta.Alignment != tb.Alignment
                || ta.BidiVisual != tb.BidiVisual)
                result.Add("placement");
            if (!SameJson(ta.FloatPosition, tb.FloatPosition)) result.Add("wrap");
            if (ta.RepeatHeader != tb.RepeatHeader) result.Add("header");
            if (ta.SplitMode != tb.SplitMode) result.Add("split");

            foreach (var (key, column) in a.Columns)
            {
                if (!b.Columns.TryGetValue(key, out var other)) continue;
                if (column.WidthType != other.WidthType || Math.Abs(column.WidthValue - other.WidthValue) > sizeTolerance)
                {
                    result.Add("columns");
                    break;
                }
            }

            foreach (var (key, row) in a.Rows)
            {
                if (!b.Rows.TryGetValue(key, out var other)) continue;
                if (Math.Abs(row.HeightPt - other.HeightPt) > lengthTolerance || row.Exact != other.Exact)
                {
                    result.Add("rows");
                    break;
                }
            }

            foreach (var (key, cell) in a.Cells)
            {
                if (!b.Cells.TryGetValue(key, out var other)) continue;

                if (!string.Equals(cell.BackgroundColor, other.BackgroundColor, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(cell.ShadingPattern, other.ShadingPattern, StringComparison.Ordinal)
                    || !string.Equals(cell.ShadingPatternColor, other.ShadingPatternColor, StringComparison.OrdinalIgnoreCase))
                    result.Add("shading");
                if (!SameJson(cell.Borders, other.Borders)) result.Add("borders");
                if (cell.VerticalAlignment != other.VerticalAlignment) result.Add("valign");
                if (cell.TextDirection != other.TextDirection) result.Add("direction");
                if (Math.Abs(cell.PaddingTopPt - other.PaddingTopPt) > lengthTolerance
                    || Math.Abs(cell.PaddingBottomPt - other.PaddingBottomPt) > lengthTolerance
                    || Math.Abs(cell.PaddingLeftPt - other.PaddingLeftPt) > lengthTolerance
                    || Math.Abs(cell.PaddingRightPt - other.PaddingRightPt) > lengthTolerance)
                    result.Add("padding");
            }

            return result;
        }

        /// <summary>
        /// Оформление таблицы <paramref name="source"/> с ключами ячеек таблицы
        /// <paramref name="target"/>: ячейки узнаются по месту (строка, столбец). Нужно
        /// сверке с файлом — у таблицы файла свои Id ячеек.
        /// </summary>
        public static string CaptureByPlace(TableBlock source, TableBlock target)
        {
            var ids = new Dictionary<(int Row, int Column), Guid>();
            foreach (var cell in target.Cells) ids[(cell.Row, cell.Column)] = cell.Id;

            var copy = (TableBlock)DocumentCloner.CloneBlock(source);
            foreach (var cell in copy.Cells)
                cell.Id = ids.TryGetValue((cell.Row, cell.Column), out var id) ? id : Guid.NewGuid();

            return Capture(copy);
        }

        /// <summary>Оформление двух записей различается с допуском сверки с файлом.</summary>
        public static bool DiffersFromFile(string current, string file)
        {
            var frameA = Parse(current);
            var frameB = Parse(file);
            if (frameA is null || frameB is null) return false;
            return Differences(frameA, frameB, forFile: true).Count > 0;
        }

        /// <summary>Оформление по общим частям различается.</summary>
        public static bool Differs(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.Ordinal)) return false;
            var frameA = Parse(a);
            var frameB = Parse(b);
            if (frameA is null || frameB is null) return !string.Equals(a, b, StringComparison.Ordinal);
            return Differences(frameA, frameB).Count > 0;
        }

        private static bool SameJson(object? a, object? b)
            => string.Equals(
                a is null ? "null" : JsonSerializer.Serialize(a, a.GetType(), JsonOptions),
                b is null ? "null" : JsonSerializer.Serialize(b, b.GetType(), JsonOptions),
                StringComparison.Ordinal);

        /// <summary>Что сменилось в оформлении таблицы — словами для панели рецензии.</summary>
        public static string Describe(string previous, TableBlock table)
        {
            var parts = new List<string>();
            var before = Parse(previous);
            if (before is not null)
            {
                var differences = Differences(before, Build(table));
                if (differences.Contains("style")) parts.Add("стиль таблицы");
                if (differences.Contains("placement")) parts.Add("ширина и место таблицы");
                if (differences.Contains("wrap")) parts.Add("обтекание");
                if (differences.Contains("columns")) parts.Add("ширина столбцов");
                if (differences.Contains("rows")) parts.Add("высота строк");
                if (differences.Contains("borders")) parts.Add("границы");
                if (differences.Contains("shading")) parts.Add("заливка");
                if (differences.Contains("valign")) parts.Add("выравнивание в ячейках");
                if (differences.Contains("direction")) parts.Add("направление текста");
                if (differences.Contains("padding")) parts.Add("поля ячеек");
                if (differences.Contains("header")) parts.Add("повтор заголовка");
                if (differences.Contains("split")) parts.Add("разрыв таблицы");
            }

            if (parts.Count == 0) parts.Add("оформление");

            var sb = new StringBuilder();
            for (int i = 0; i < parts.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(parts[i]);
            }
            return sb.ToString();
        }
    }
}
