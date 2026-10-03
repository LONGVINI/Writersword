using System;
using SkiaSharp;
using Writersword.Core.Models.Rendering;

namespace Writersword.Modules.TextEditor.Rendering
{
    /// <summary>
    /// Рисует границы ячейки таблицы так, как их рисует Word.
    ///
    /// Граница между двумя ячейками одна (SKTableLayout.HorizontalEdges и VerticalEdges):
    /// из двух линий, заданных ячейками по обе стороны, оставлена сильнейшая. Рисует её
    /// одна ячейка: горизонтальную — нижняя, вертикальную — правая. Нижнюю и правую
    /// стороны ячейка рисует сама только там, где соседа с той стороны нет, и на
    /// обрыве таблицы в конце страницы.
    ///
    /// Горизонтальная линия лежит в полосе у верха строки вплотную к верху полосы,
    /// вертикальная — серединой на линии сетки. Черты составной линии идут в одном
    /// порядке на всех сторонах: сверху вниз и слева направо. Поэтому у Word и есть
    /// пары «тонкая и толстая» и «толстая и тонкая»: рамку собирают из обеих.
    ///
    /// На экране линии строятся из целых пикселей по правилу Word: толщина черты —
    /// целая часть её размера в пикселях, но не меньше пикселя; просветы и длины
    /// штрихов отсчитываются в таких же толщинах. Линия в полтора пункта при обычном
    /// масштабе — два пикселя, а не три. На печати размеры точные.
    ///
    /// Стыки линий в узлах сетки:
    ///   • более слабая линия упирается в более сильную и не пересекает её;
    ///   • черта составной линии сходится с чертой поперечной составной линии, лежащей
    ///     с той же стороны: ближняя к ячейке — с ближней, дальняя — с дальней. Рамка из
    ///     двойных линий складывается из вложенных прямоугольников, сетка из двойных
    ///     линий — из рамок вокруг каждой ячейки;
    ///   • простая линия упирается в составную той же силы;
    ///   • равные простые линии закрывают угол обе.
    ///
    /// Штрихи, волны и наклонные штрихи привязаны к листу, а не к началу линии: у линий
    /// соседних ячеек на одной границе рисунок общий и не сбивается на стыке.
    /// </summary>
    internal static class TableBorderPainter
    {
        /// <summary>Поперечная линия в узле сетки: её края поперёк себя и сила.</summary>
        private readonly struct Box
        {
            public Box(float low, float high, bool stranded, float weight)
            {
                Low = low;
                High = high;
                Stranded = stranded;
                Weight = weight;
                Exists = true;
            }

            public bool Exists { get; }
            public bool Stranded { get; }
            public float Low { get; }
            public float High { get; }
            public float Weight { get; }
            public float Size => High - Low;
        }

        private static bool IsLine(SKTableBorderLineLayout? line)
            => line is not null && line.Style != SKBorderLineShape.None && line.WidthPt > 0f;

        private static bool SameLine(SKTableBorderLineLayout? a, SKTableBorderLineLayout? b)
        {
            if (a is null || b is null) return a is null && b is null;
            return a.Style == b.Style
                && Math.Abs(a.WidthPt - b.WidthPt) < 0.001f
                && string.Equals(a.Color, b.Color, StringComparison.OrdinalIgnoreCase)
                && Math.Abs(a.Weight - b.Weight) < 0.001f;
        }

        /// <summary>Ближайший целый пиксель, в pt.</summary>
        private static float Snap(float value, float scale)
            => (float)Math.Floor(value * scale + 0.5f) / scale;

        /// <summary>Толщина черты в пикселях по правилу Word: целая часть, но не меньше одного.</summary>
        private static float UnitPx(float widthPt, float scale)
            => Math.Max(1f, (float)Math.Floor(widthPt * scale + 0.001f));

        /// <summary>
        /// Черты составной линии от её верхнего (левого) края: пары «начало, толщина», pt.
        /// На экране каждая черта и каждый просвет — целое число пикселей.
        /// </summary>
        private static float[]? MeasureStrands(SKTableBorderLineLayout line, float scale, bool snap, out float total)
        {
            total = 0f;

            float[]? nominal = SKBorderLineShape.Strands(line.Style, line.WidthPt);
            if (nominal is null) return null;

            var result = new float[nominal.Length];
            float cursor = 0f;
            float nominalEnd = 0f;

            for (int i = 0; i + 1 < nominal.Length; i += 2)
            {
                float width = nominal[i + 1];
                float near = nominal[i] - width / 2f;
                float gap = near - nominalEnd;

                if (snap)
                {
                    float gapPx = i == 0 ? 0f : UnitPx(gap, scale);
                    float widthPx = UnitPx(width, scale);
                    result[i] = cursor + gapPx / scale;
                    result[i + 1] = widthPx / scale;
                }
                else
                {
                    result[i] = near;
                    result[i + 1] = width;
                }

                cursor = result[i] + result[i + 1];
                nominalEnd = near + width;
            }

            total = cursor;
            return result;
        }

        /// <summary>Ширина линии без черт поперёк себя: столько она занимает на экране или в печати.</summary>
        private static float SimpleTotal(SKTableBorderLineLayout line, float scale, bool snap)
        {
            float width = line.WidthPt;

            switch (line.Style)
            {
                case SKBorderLineShape.ThreeDEmboss:
                case SKBorderLineShape.ThreeDEngrave:
                    if (snap)
                    {
                        float unit = UnitPx(width, scale);
                        float edge = Math.Max(1f, (float)Math.Floor(unit / 2f));
                        return (unit + edge * 2f) / scale;
                    }
                    return width * 2f;

                case SKBorderLineShape.Wave:
                {
                    float span = SKBorderLineShape.WaveSpanPt(width);
                    return snap ? Math.Max(3f, (float)Math.Floor(span * scale + 0.5f)) / scale : span;
                }

                case SKBorderLineShape.DoubleWave:
                    return snap ? Math.Max(6f, (float)Math.Floor(line.SpanPt * scale + 0.5f)) / scale : line.SpanPt;

                case SKBorderLineShape.DashDotStroked:
                    return snap
                        ? Math.Max(2f, (float)Math.Floor(line.SpanPt * scale + 0.001f)) / scale
                        : line.SpanPt;

                default:
                    return snap ? UnitPx(width, scale) / scale : width;
            }
        }

        private static float TotalOf(SKTableBorderLineLayout line, float scale, bool snap, out bool stranded)
        {
            float[]? strands = MeasureStrands(line, scale, snap, out float total);
            stranded = strands is not null;
            return stranded ? total : SimpleTotal(line, scale, snap);
        }

        /// <summary>Вертикальная линия серединой на оси axis.</summary>
        private static Box VerticalBox(SKTableBorderLineLayout? line, float axis, float scale, bool snap)
        {
            if (!IsLine(line)) return default;

            float total = TotalOf(line!, scale, snap, out bool stranded);
            float low = snap ? Snap(axis - total / 2f, scale) : axis - total / 2f;
            return new Box(low, low + total, stranded, line!.Weight);
        }

        /// <summary>Горизонтальная линия верхним краем на lowY.</summary>
        private static Box HorizontalBox(SKTableBorderLineLayout? line, float lowY, float scale, bool snap)
        {
            if (!IsLine(line)) return default;

            float total = TotalOf(line!, scale, snap, out bool stranded);
            float low = snap ? Snap(lowY, scale) : lowY;
            return new Box(low, low + total, stranded, line!.Weight);
        }

        private static Box StrongerBox(Box a, Box b)
        {
            if (!a.Exists) return b;
            if (!b.Exists) return a;
            return b.Weight > a.Weight ? b : a;
        }

        // ── Концы черт составной линии ───────────────────────────────────────
        //
        // near и far — начало и конец черты от верхнего (левого) края своей линии, total —
        // ширина всей линии. Черта из первой половины линии относится к ячейке по ту
        // сторону (выше или левее), из второй — к ячейке по эту (ниже или правее), и
        // сходится с поперечной линией той же ячейки. first — поперечная линия первой
        // стороны (выше узла или левее), second — второй; ahead — продолжение самой
        // линии за узлом.

        private static bool BelongsToFirst(float near, float far, float total, Box second)
        {
            float centre = (near + far) / (2f * total);
            if (centre < 0.49f) return true;
            if (centre > 0.51f) return false;
            return !second.Stranded;
        }

        /// <summary>Начало черты: узел у левого конца горизонтальной линии или у верхнего конца вертикальной.</summary>
        private static float StrandStart(
            float weight, float near, float far, float total, float grid, Box first, Box second, Box ahead)
        {
            // Более сильная простая линия поперёк: составная упирается в неё всеми чертами.
            float blocked = float.NegativeInfinity;
            if (first.Exists && !first.Stranded && first.Weight > weight + WeightTolerance) blocked = Math.Max(blocked, first.High);
            if (second.Exists && !second.Stranded && second.Weight > weight + WeightTolerance) blocked = Math.Max(blocked, second.High);
            if (!float.IsNegativeInfinity(blocked)) return blocked;

            bool toFirst = BelongsToFirst(near, far, total, second);
            Box cross = toFirst ? first : second;

            if (cross.Stranded)
            {
                return toFirst
                    ? cross.Low + (total - far) / total * cross.Size
                    : cross.Low + near / total * cross.Size;
            }

            if (ahead.Stranded) return grid;

            Box other = toFirst ? second : first;
            if (other.Stranded)
            {
                return toFirst
                    ? other.Low + near / total * other.Size
                    : other.Low + (total - far) / total * other.Size;
            }

            Box strongest = StrongerBox(first, second);
            return strongest.Exists ? strongest.Low : grid;
        }

        /// <summary>Конец черты: узел у правого конца горизонтальной линии или у нижнего конца вертикальной.</summary>
        private static float StrandEnd(
            float weight, float near, float far, float total, float grid, Box first, Box second, Box ahead)
        {
            float blocked = float.PositiveInfinity;
            if (first.Exists && !first.Stranded && first.Weight > weight + WeightTolerance) blocked = Math.Min(blocked, first.Low);
            if (second.Exists && !second.Stranded && second.Weight > weight + WeightTolerance) blocked = Math.Min(blocked, second.Low);
            if (!float.IsPositiveInfinity(blocked)) return blocked;

            bool toFirst = BelongsToFirst(near, far, total, second);
            Box cross = toFirst ? first : second;

            if (cross.Stranded)
            {
                return toFirst
                    ? cross.Low + far / total * cross.Size
                    : cross.Low + (total - near) / total * cross.Size;
            }

            if (ahead.Stranded) return grid;

            Box other = toFirst ? second : first;
            if (other.Stranded)
            {
                return toFirst
                    ? other.Low + (total - near) / total * other.Size
                    : other.Low + far / total * other.Size;
            }

            Box strongest = StrongerBox(first, second);
            return strongest.Exists ? strongest.High : grid;
        }

        // ── Концы простой линии ──────────────────────────────────────────────

        private const float WeightTolerance = 0.001f;

        /// <summary>
        /// Поперечная линия не пускает простую: она сильнее либо составная и не слабее.
        /// </summary>
        private static bool Blocks(Box cross, float weight)
            => cross.Exists
               && (cross.Weight > weight + WeightTolerance
                   || (cross.Stranded && cross.Weight >= weight - WeightTolerance));

        private static float SimpleStart(float weight, float grid, Box first, Box second, Box ahead)
        {
            float blocked = float.NegativeInfinity;
            if (Blocks(first, weight)) blocked = Math.Max(blocked, first.High);
            if (Blocks(second, weight)) blocked = Math.Max(blocked, second.High);
            if (!float.IsNegativeInfinity(blocked)) return blocked;

            if (ahead.Exists) return grid;

            // Линия начинается в этом узле и никому не уступает: закрывает угол целиком.
            float cover = float.PositiveInfinity;
            if (first.Exists) cover = Math.Min(cover, first.Low);
            if (second.Exists) cover = Math.Min(cover, second.Low);
            return float.IsPositiveInfinity(cover) ? grid : cover;
        }

        private static float SimpleEnd(float weight, float grid, Box first, Box second, Box ahead)
        {
            float blocked = float.PositiveInfinity;
            if (Blocks(first, weight)) blocked = Math.Min(blocked, first.Low);
            if (Blocks(second, weight)) blocked = Math.Min(blocked, second.Low);
            if (!float.IsPositiveInfinity(blocked)) return blocked;

            if (ahead.Exists) return grid;

            float cover = float.NegativeInfinity;
            if (first.Exists) cover = Math.Max(cover, first.High);
            if (second.Exists) cover = Math.Max(cover, second.High);
            return float.IsNegativeInfinity(cover) ? grid : cover;
        }

        /// <summary>
        /// Границы одной ячейки. cellX и cellY — левый верхний угол видимой части ячейки,
        /// visibleH — её видимая высота. sliceEnd — ячейка последняя в куске таблицы на
        /// странице: если таблица идёт дальше, кусок замыкается линией под ячейкой.
        /// </summary>
        public static void DrawCell(
            SKCanvas canvas, SKTableLayout? table, SKTableCellLayout cell,
            float cellX, float cellY, float visibleH, float canvasScale,
            bool suppressTop, bool suppressBottom, bool sliceEnd, bool snapToPixels,
            Func<string?, SKColor> resolveColor)
        {
            float scale = canvasScale > 0f ? canvasScale : 1f;
            bool snap = snapToPixels && canvasScale > 0f;

            var horizontalEdges = table?.HorizontalEdges;
            var verticalEdges = table?.VerticalEdges;
            var slotFilled = table?.SlotFilled;

            int rowCount = table?.RowCount ?? 0;
            int colCount = table?.ColumnCount ?? 0;

            bool hasGrid = table is not null
                && horizontalEdges is not null && verticalEdges is not null && slotFilled is not null
                && rowCount > 0 && colCount > 0
                && horizontalEdges.GetLength(0) == rowCount + 1 && horizontalEdges.GetLength(1) == colCount
                && verticalEdges.GetLength(0) == rowCount && verticalEdges.GetLength(1) == colCount + 1
                && slotFilled.GetLength(0) == rowCount && slotFilled.GetLength(1) == colCount;

            int row = cell.Row;
            int col = cell.Column;

            // Без сетки границ ячейка рисуется сама по себе, своими четырьмя сторонами.
            if (!hasGrid)
            {
                rowCount = row + Math.Max(cell.RowSpan, 1);
                colCount = col + Math.Max(cell.ColSpan, 1);
            }

            int lastRow = Math.Max(row, Math.Min(row + Math.Max(cell.RowSpan, 1), rowCount) - 1);
            int lastCol = Math.Max(col, Math.Min(col + Math.Max(cell.ColSpan, 1), colCount) - 1);

            float cellBottom = cellY + visibleH;
            bool tableBottom = lastRow + 1 >= rowCount;
            bool closesSlice = sliceEnd && !tableBottom && !suppressBottom;

            // Верх линий под ячейкой: между строками и на обрыве куска — под нижним краем
            // ячейки; у низа таблицы — в полосе под последней строкой (уточняется ниже).
            float bottomLineY = tableBottom ? cellBottom - cell.BottomInsetPt : cellBottom;

            SKTableBorderLineLayout? HorizontalAt(int r, int c)
            {
                if (r < 0 || r > rowCount || c < 0 || c >= colCount) return null;

                SKTableBorderLineLayout? line;
                if (hasGrid) line = horizontalEdges![r, c];
                else if (c < col || c > lastCol) line = null;
                else if (r == row) line = cell.Borders.Top;
                else if (r == lastRow + 1) line = cell.Borders.Bottom;
                else line = null;

                return IsLine(line) ? line : null;
            }

            SKTableBorderLineLayout? VerticalAt(int r, int c)
            {
                if (r < 0 || r >= rowCount || c < 0 || c > colCount) return null;

                SKTableBorderLineLayout? line;
                if (hasGrid) line = verticalEdges![r, c];
                else if (r < row || r > lastRow) line = null;
                else if (c == col) line = cell.Borders.Left;
                else if (c == lastCol + 1) line = cell.Borders.Right;
                else line = null;

                return IsLine(line) ? line : null;
            }

            bool Filled(int r, int c)
                => hasGrid && r >= 0 && r < rowCount && c >= 0 && c < colCount && slotFilled![r, c];

            float BoundaryX(int c)
            {
                if (c <= col) return cellX;
                if (c > lastCol) return cellX + cell.WidthPt;

                if (hasGrid && c < table!.ColumnOffsetsPt.Count)
                    return cellX + table.ColumnOffsetsPt[c] - cell.Xpt;

                return cellX + cell.WidthPt * (c - col) / (lastCol - col + 1);
            }

            float BoundaryY(int r)
            {
                if (r <= row) return cellY;
                if (r > lastRow) return bottomLineY;

                float offset = hasGrid && r < table!.Rows.Count && row < table.Rows.Count
                    ? table.Rows[r].Ypt - table.Rows[row].Ypt
                    : visibleH * (r - row) / (lastRow - row + 1);

                return cellY + Math.Min(offset, visibleH);
            }

            // Линии под последней строкой таблицы стоят верхом на одной высоте: так, чтобы
            // самая широкая из них кончалась у низа таблицы. На экране линия уже своего
            // места в вёрстке (толщина округляется вниз до пикселя), и остаток полосы
            // лежит между содержимым и линией, а не под ней — как у Word.
            if (tableBottom)
            {
                float widest = 0f;
                for (int c = 0; c < colCount; c++)
                {
                    var line = HorizontalAt(rowCount, c);
                    if (line is not null) widest = Math.Max(widest, TotalOf(line, scale, snap, out _));
                }

                if (widest > 0f)
                {
                    float tableEnd = snap ? Snap(cellBottom, scale) : cellBottom;
                    bottomLineY = tableEnd - widest;
                }
            }

            // Горизонтальная линия по границе строк boundaryRow над колонками c1..c2.
            // hasBelow — под линией на этой странице есть строка.
            void DrawHorizontalRun(SKTableBorderLineLayout line, float lowY, int boundaryRow, int c1, int c2, bool hasBelow)
            {
                float gridLeft = BoundaryX(c1);
                float gridRight = BoundaryX(c2 + 1);
                if (gridRight - gridLeft <= 0f) return;

                Box upLeft = VerticalBox(VerticalAt(boundaryRow - 1, c1), gridLeft, scale, snap);
                Box downLeft = hasBelow ? VerticalBox(VerticalAt(boundaryRow, c1), gridLeft, scale, snap) : default;
                Box aheadLeft = HorizontalBox(HorizontalAt(boundaryRow, c1 - 1), lowY, scale, snap);

                Box upRight = VerticalBox(VerticalAt(boundaryRow - 1, c2 + 1), gridRight, scale, snap);
                Box downRight = hasBelow ? VerticalBox(VerticalAt(boundaryRow, c2 + 1), gridRight, scale, snap) : default;
                Box aheadRight = HorizontalBox(HorizontalAt(boundaryRow, c2 + 1), lowY, scale, snap);

                float low = snap ? Snap(lowY, scale) : lowY;
                SKColor color = resolveColor(line.Color);

                float[]? strands = MeasureStrands(line, scale, snap, out float total);
                if (strands is not null)
                {
                    using var paint = new SKPaint { Color = color, IsStroke = false, IsAntialias = !snap };

                    for (int i = 0; i + 1 < strands.Length; i += 2)
                    {
                        float near = strands[i];
                        float far = near + strands[i + 1];

                        float from = StrandStart(line.Weight, near, far, total, gridLeft, upLeft, downLeft, aheadLeft);
                        float to = StrandEnd(line.Weight, near, far, total, gridRight, upRight, downRight, aheadRight);
                        if (snap)
                        {
                            from = Snap(from, scale);
                            to = Snap(to, scale);
                        }
                        if (to - from <= 0f) continue;

                        canvas.DrawRect(new SKRect(from, low + near, to, low + far), paint);
                    }
                    return;
                }

                float start = SimpleStart(line.Weight, gridLeft, upLeft, downLeft, aheadLeft);
                float end = SimpleEnd(line.Weight, gridRight, upRight, downRight, aheadRight);
                if (snap)
                {
                    start = Snap(start, scale);
                    end = Snap(end, scale);
                }
                if (end - start <= 0f) return;

                DrawSimple(canvas, line, color, false, start, end, low, SimpleTotal(line, scale, snap), scale, snap);
            }

            // Вертикальная линия по границе колонок boundaryCol вдоль строк r1..r2.
            void DrawVerticalRun(SKTableBorderLineLayout line, int boundaryCol, int r1, int r2)
            {
                float axis = BoundaryX(boundaryCol);
                float topY = BoundaryY(r1);
                bool reachesCellBottom = r2 >= lastRow;
                float bottomY = reachesCellBottom ? bottomLineY : BoundaryY(r2 + 1);

                bool plainTop = suppressTop && r1 <= row;
                bool plainBottom = reachesCellBottom && suppressBottom;

                // Ниже конца ячейки на этой странице ничего нет у низа таблицы и на обрыве куска.
                bool hasBelow = !(reachesCellBottom && (tableBottom || closesSlice));

                Box leftTop = HorizontalBox(HorizontalAt(r1, boundaryCol - 1), topY, scale, snap);
                Box rightTop = HorizontalBox(HorizontalAt(r1, boundaryCol), topY, scale, snap);
                Box aheadTop = VerticalBox(VerticalAt(r1 - 1, boundaryCol), axis, scale, snap);

                Box leftBottom = HorizontalBox(HorizontalAt(r2 + 1, boundaryCol - 1), bottomY, scale, snap);
                Box rightBottom = HorizontalBox(HorizontalAt(r2 + 1, boundaryCol), bottomY, scale, snap);
                Box aheadBottom = hasBelow ? VerticalBox(VerticalAt(r2 + 1, boundaryCol), axis, scale, snap) : default;

                float gridTop = snap ? Snap(topY, scale) : topY;
                float gridBottom = snap ? Snap(bottomY, scale) : bottomY;
                float plainEnd = snap ? Snap(cellBottom, scale) : cellBottom;

                SKColor color = resolveColor(line.Color);

                float[]? strands = MeasureStrands(line, scale, snap, out float total);
                float simpleTotal = strands is null ? SimpleTotal(line, scale, snap) : total;
                float low = snap ? Snap(axis - simpleTotal / 2f, scale) : axis - simpleTotal / 2f;

                if (strands is not null)
                {
                    using var paint = new SKPaint { Color = color, IsStroke = false, IsAntialias = !snap };

                    for (int i = 0; i + 1 < strands.Length; i += 2)
                    {
                        float near = strands[i];
                        float far = near + strands[i + 1];

                        float from = plainTop
                            ? gridTop
                            : StrandStart(line.Weight, near, far, total, gridTop, leftTop, rightTop, aheadTop);
                        float to = plainBottom
                            ? plainEnd
                            : StrandEnd(line.Weight, near, far, total, gridBottom, leftBottom, rightBottom, aheadBottom);
                        if (snap)
                        {
                            from = Snap(from, scale);
                            to = Snap(to, scale);
                        }
                        if (to - from <= 0f) continue;

                        canvas.DrawRect(new SKRect(low + near, from, low + far, to), paint);
                    }
                    return;
                }

                float start = plainTop
                    ? gridTop
                    : SimpleStart(line.Weight, gridTop, leftTop, rightTop, aheadTop);
                float end = plainBottom
                    ? plainEnd
                    : SimpleEnd(line.Weight, gridBottom, leftBottom, rightBottom, aheadBottom);
                if (snap)
                {
                    start = Snap(start, scale);
                    end = Snap(end, scale);
                }
                if (end - start <= 0f) return;

                DrawSimple(canvas, line, color, true, start, end, low, simpleTotal, scale, snap);
            }

            // Верхняя сторона: общая граница с ячейками над этой.
            if (!suppressTop)
            {
                int c = col;
                while (c <= lastCol)
                {
                    var line = HorizontalAt(row, c);
                    int runEnd = c;
                    while (runEnd + 1 <= lastCol && SameLine(HorizontalAt(row, runEnd + 1), line)) runEnd++;

                    if (line is not null) DrawHorizontalRun(line, cellY, row, c, runEnd, true);
                    c = runEnd + 1;
                }
            }

            // Нижняя сторона: только там, где её не нарисует ячейка снизу.
            if (!suppressBottom)
            {
                int c = col;
                while (c <= lastCol)
                {
                    bool own = tableBottom || closesSlice || !Filled(lastRow + 1, c);
                    var line = own ? HorizontalAt(lastRow + 1, c) : null;

                    int runEnd = c;
                    while (runEnd + 1 <= lastCol
                        && (tableBottom || closesSlice || !Filled(lastRow + 1, runEnd + 1)) == own
                        && SameLine(own ? HorizontalAt(lastRow + 1, runEnd + 1) : null, line))
                        runEnd++;

                    if (line is not null) DrawHorizontalRun(line, bottomLineY, lastRow + 1, c, runEnd, false);
                    c = runEnd + 1;
                }
            }

            // Левая сторона: общая граница с ячейками слева.
            {
                int r = row;
                while (r <= lastRow)
                {
                    var line = VerticalAt(r, col);
                    int runEnd = r;
                    while (runEnd + 1 <= lastRow && SameLine(VerticalAt(runEnd + 1, col), line)) runEnd++;

                    if (line is not null) DrawVerticalRun(line, col, r, runEnd);
                    r = runEnd + 1;
                }
            }

            // Правая сторона: только там, где её не нарисует ячейка справа.
            {
                int r = row;
                while (r <= lastRow)
                {
                    bool own = lastCol + 1 >= colCount || !Filled(r, lastCol + 1);
                    var line = own ? VerticalAt(r, lastCol + 1) : null;

                    int runEnd = r;
                    while (runEnd + 1 <= lastRow
                        && (lastCol + 1 >= colCount || !Filled(runEnd + 1, lastCol + 1)) == own
                        && SameLine(own ? VerticalAt(runEnd + 1, lastCol + 1) : null, line))
                        runEnd++;

                    if (line is not null) DrawVerticalRun(line, lastCol + 1, r, runEnd);
                    r = runEnd + 1;
                }
            }
        }

        /// <summary>
        /// Линия без отдельных прямых черт: сплошная, штриховая, точечная, объёмная,
        /// волнистая, из наклонных штрихов. Линия занимает поперёк себя полосу от low до
        /// low + total и идёт вдоль себя от from до to.
        /// </summary>
        private static void DrawSimple(
            SKCanvas canvas, SKTableBorderLineLayout line, SKColor color, bool vertical,
            float from, float to, float low, float total, float scale, bool snap)
        {
            if (total <= 0f || to - from <= 0f) return;

            SKRect Band(float bandLow, float bandHigh) => vertical
                ? new SKRect(bandLow, from, bandHigh, to)
                : new SKRect(from, bandLow, to, bandHigh);

            switch (line.Style)
            {
                case SKBorderLineShape.ThreeDEmboss:
                case SKBorderLineShape.ThreeDEngrave:
                {
                    // Три полосы: тёмный край, основной цвет, светлый край. У вдавленной
                    // линии тёмный край сверху и слева, у выпуклой — снизу и справа.
                    // Порядок один на всех сторонах ячейки, как у Word.
                    float edge;
                    if (snap)
                    {
                        float unit = UnitPx(line.WidthPt, scale);
                        edge = Math.Max(1f, (float)Math.Floor(unit / 2f)) / scale;
                    }
                    else
                    {
                        edge = line.WidthPt / 2f;
                    }

                    bool engrave = line.Style == SKBorderLineShape.ThreeDEngrave;
                    SKColor dark = Shade(color, false);
                    SKColor light = Shade(color, true);

                    using var paint = new SKPaint { IsStroke = false, IsAntialias = !snap };

                    paint.Color = engrave ? dark : light;
                    canvas.DrawRect(Band(low, low + edge), paint);

                    paint.Color = color;
                    canvas.DrawRect(Band(low + edge, low + total - edge), paint);

                    paint.Color = engrave ? light : dark;
                    canvas.DrawRect(Band(low + total - edge, low + total), paint);
                    return;
                }

                case SKBorderLineShape.Wave:
                {
                    float stroke = snap ? 1f / scale : SKBorderLineShape.WaveStrokePt(line.WidthPt);
                    DrawWave(canvas, color, vertical, from, to, low, total,
                        WavePeriod(line.WidthPt, scale, snap), stroke);
                    return;
                }

                case SKBorderLineShape.DoubleWave:
                {
                    // Две тонкие волны вплотную у верхнего (левого) края полосы; остаток
                    // полосы — просвет до содержимого ячейки.
                    float waveLine = SKBorderLineShape.DoubleWaveStrokePt(line.WidthPt);
                    float waveSpan = SKBorderLineShape.WaveSpanPt(waveLine);
                    if (snap) waveSpan = Math.Max(3f, (float)Math.Floor(waveSpan * scale + 0.5f)) / scale;
                    waveSpan = Math.Min(waveSpan, total / 2f);

                    float waveStroke = snap ? 1f / scale : SKBorderLineShape.WaveStrokePt(waveLine);
                    float wavePeriod = WavePeriod(waveLine, scale, snap);

                    DrawWave(canvas, color, vertical, from, to, low, waveSpan, wavePeriod, waveStroke);
                    DrawWave(canvas, color, vertical, from, to, low + waveSpan, waveSpan, wavePeriod, waveStroke);
                    return;
                }

                case SKBorderLineShape.DashDotStroked:
                    DrawStroked(canvas, line, color, vertical, from, to, low, total, scale, snap);
                    return;
            }

            float[]? dashUnits = SKBorderLineShape.DashUnits(line.Style);
            if (dashUnits is null)
            {
                using var fill = new SKPaint { Color = color, IsStroke = false, IsAntialias = !snap };
                canvas.DrawRect(Band(low, low + total), fill);
                return;
            }

            // Штрихи и просветы — в толщинах линии: на экране толщина целая в пикселях,
            // и все штрихи одной линии выходят одной длины.
            var intervals = new float[dashUnits.Length];
            for (int i = 0; i < dashUnits.Length; i++)
                intervals[i] = dashUnits[i] * total;

            // Рисунок привязан к листу: штрих начинается там, где координата кратна
            // длине всего чередования.
            float period = 0f;
            for (int i = 0; i < intervals.Length; i++) period += intervals[i];
            float phase = period > 0f ? ((from % period) + period) % period : 0f;

            using var effect = SKPathEffect.CreateDash(intervals, phase);
            using var stroked = new SKPaint
            {
                Color = color,
                IsStroke = true,
                IsAntialias = !snap,
                StrokeWidth = total,
                PathEffect = effect
            };

            float axis = low + total / 2f;
            if (vertical) canvas.DrawLine(axis, from, axis, to, stroked);
            else canvas.DrawLine(from, axis, to, axis, stroked);
        }

        /// <summary>Длина волны вдоль линии; на экране — чётное число пикселей.</summary>
        private static float WavePeriod(float lineWidthPt, float scale, bool snap)
        {
            float period = SKBorderLineShape.WavePeriodPt(lineWidthPt);
            if (!snap) return period;

            float halfPx = Math.Max(2f, (float)Math.Floor(period * scale / 2f + 0.5f));
            return halfPx * 2f / scale;
        }

        /// <summary>
        /// Волна: зигзаг в полосе от low до low + span. Волна не подгоняется под длину
        /// стороны: идёт от начала линии своим шагом и обрезается у конца, как у Word.
        /// </summary>
        private static void DrawWave(
            SKCanvas canvas, SKColor color, bool vertical,
            float from, float to, float low, float span, float period, float stroke)
        {
            if (span <= 0f || period <= 0f || to - from <= 0f) return;

            float crest = low + stroke / 2f;
            float trough = low + span - stroke / 2f;
            if (trough < crest) trough = crest;

            float half = period / 2f;

            // Волна привязана к листу: гребни стоят там, где координата кратна длине волны.
            int index = (int)Math.Floor(from / half);

            using var path = new SKPath();
            bool started = false;
            for (float along = index * half; along < to + half; along += half, index++)
            {
                float across = (index & 1) == 0 ? crest : trough;
                if (!started)
                {
                    path.MoveTo(vertical ? across : along, vertical ? along : across);
                    started = true;
                }
                else
                {
                    path.LineTo(vertical ? across : along, vertical ? along : across);
                }
            }

            using var paint = new SKPaint
            {
                Color = color,
                IsStroke = true,
                IsAntialias = true,
                StrokeWidth = stroke,
                StrokeJoin = SKStrokeJoin.Miter,
                StrokeCap = SKStrokeCap.Butt
            };

            canvas.Save();
            canvas.ClipRect(vertical
                ? new SKRect(low, from, low + span, to)
                : new SKRect(from, low, to, low + span));
            canvas.DrawPath(path, paint);
            canvas.Restore();
        }

        /// <summary>
        /// Полоса из наклонных штрихов (w:val="dashDotStroked"): длинный штрих и короткий
        /// по очереди, скошенные под сорок пять градусов. У Word на экране длинный штрих —
        /// две с половиной толщины линии, короткий штрих и просветы — по половине.
        /// </summary>
        private static void DrawStroked(
            SKCanvas canvas, SKTableBorderLineLayout line, SKColor color, bool vertical,
            float from, float to, float low, float total, float scale, bool snap)
        {
            float longStroke;
            float shortStroke;

            if (snap)
            {
                float unit = UnitPx(line.WidthPt, scale);
                longStroke = Math.Max(2f, (float)Math.Floor(unit * 2.5f + 0.5f)) / scale;
                shortStroke = Math.Max(1f, (float)Math.Floor(unit / 2f)) / scale;
            }
            else
            {
                longStroke = line.WidthPt * SKBorderLineShape.StrokedLongShare;
                shortStroke = line.WidthPt * SKBorderLineShape.StrokedShortShare;
            }

            float gap = shortStroke;
            float period = longStroke + gap + shortStroke + gap;
            if (period <= 0f) return;

            float high = low + total;

            using var path = new SKPath();

            // Штрих — параллелограмм: у верхнего (левого) края полосы он сдвинут вдоль
            // линии на ширину полосы.
            void AddStroke(float start, float length)
            {
                float end = start + length;
                if (vertical)
                {
                    path.MoveTo(low, start + total);
                    path.LineTo(low, end + total);
                    path.LineTo(high, end);
                    path.LineTo(high, start);
                }
                else
                {
                    path.MoveTo(start + total, low);
                    path.LineTo(end + total, low);
                    path.LineTo(end, high);
                    path.LineTo(start, high);
                }
                path.Close();
            }

            // Рисунок привязан к листу, как у штриховых линий.
            float first = (float)Math.Floor((from - total) / period) * period - period;

            for (float at = first; at < to; at += period)
            {
                AddStroke(at, longStroke);
                AddStroke(at + longStroke + gap, shortStroke);
            }

            using var paint = new SKPaint { Color = color, IsStroke = false, IsAntialias = !snap };

            canvas.Save();
            canvas.ClipRect(vertical
                ? new SKRect(low, from, high, to)
                : new SKRect(from, low, to, high));
            canvas.DrawPath(path, paint);
            canvas.Restore();
        }

        /// <summary>
        /// Светлый или тёмный край объёмной линии. У Word тёмный край — тот же тон с
        /// яркостью чуть больше половины, светлый — тон, осветлённый на четверть пути
        /// до белого.
        /// </summary>
        private static SKColor Shade(SKColor color, bool lighter)
        {
            color.ToHsl(out float hue, out float saturation, out float lightness);

            float shaded = lighter
                ? lightness + (100f - lightness) * 0.265f
                : lightness * 0.55f;

            return SKColor.FromHsl(hue, saturation, Math.Clamp(shaded, 0f, 100f), color.Alpha);
        }
    }
}
