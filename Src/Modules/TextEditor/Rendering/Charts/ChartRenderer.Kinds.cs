using System;
using System.Collections.Generic;
using SkiaSharp;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Rendering.Charts
{
    /// <summary>
    /// Лепестковая, пузырьковая и биржевая диаграммы. Общие куски — шрифты, легенда,
    /// подписи чисел, линии рядов — те же, что у основных видов.
    /// </summary>
    public static partial class ChartRenderer
    {
        /// <summary>Наибольший пузырёк — четверть меньшей стороны области построения, как у Excel.</summary>
        private const float BubbleMaxShare = 0.25f;

        /// <summary>Ширина тела свечи биржевой — доля слота категории.</summary>
        private const float StockBarShare = 0.4f;

        private static readonly SKColor StockInk = new(0x40, 0x40, 0x40);

        // ── Лепестковая ──────────────────────────────────────────────────

        private static void DrawRadar(SKCanvas canvas, ChartModel chart, SKRect area, ChartFonts fonts)
        {
            int categories = chart.CategoryCount;
            if (categories < 3) categories = Math.Max(categories, 3);

            var font = fonts.Label;
            float lineHeight = LineHeight(font);

            float widestLabel = 0f;
            for (int c = 0; c < categories; c++)
                widestLabel = Math.Max(widestLabel, font.MeasureText(chart.CategoryLabel(c)));
            widestLabel = Math.Min(widestLabel, area.Width * 0.25f);

            float radius = Math.Min(area.Width / 2f - widestLabel - 6f, area.Height / 2f - lineHeight - 4f);
            if (radius <= 4f) return;
            var center = new SKPoint(area.MidX, area.MidY);

            double min = double.PositiveInfinity, max = double.NegativeInfinity;
            foreach (var series in chart.Series)
                foreach (var value in series.Values)
                    if (value is double v && !double.IsNaN(v) && !double.IsInfinity(v))
                    {
                        min = Math.Min(min, v);
                        max = Math.Max(max, v);
                    }
            if (double.IsInfinity(min)) { min = 0; max = 1; }

            var scale = ChartScale.Compute(min, max, radius, LabelSizePt, forceZero: true, chart.ValueAxisMin, chart.ValueAxisMax, ChartScale.MinStepFor(chart.NumberFormat));

            SKPoint Vertex(int category, double value)
            {
                float angle = (-90f + 360f * category / categories) * MathF.PI / 180f;
                float r = (float)(Math.Clamp(scale.Fraction(value), 0.0, 1.0) * radius);
                return new SKPoint(center.X + MathF.Cos(angle) * r, center.Y + MathF.Sin(angle) * r);
            }

            // Сетка: многоугольники по делениям и лучи к категориям.
            using (var grid = new SKPaint { Color = GridColor, Style = SKPaintStyle.Stroke, StrokeWidth = GridLineWidthPt, IsAntialias = true })
            {
                if (chart.ShowGridlines)
                {
                    foreach (var tick in scale.Ticks())
                    {
                        if (tick <= scale.Min) continue;
                        using var polygon = new SKPath();
                        for (int c = 0; c < categories; c++)
                        {
                            var p = Vertex(c, tick);
                            if (c == 0) polygon.MoveTo(p); else polygon.LineTo(p);
                        }
                        polygon.Close();
                        canvas.DrawPath(polygon, grid);
                    }
                }

                for (int c = 0; c < categories; c++)
                    canvas.DrawLine(center, Vertex(c, scale.Max), grid);
            }

            // Подписи делений — вдоль первого луча, вверх от центра.
            if (!chart.HideValueAxis)
            {
                foreach (var tick in scale.Ticks())
                {
                    var p = Vertex(0, tick);
                    DrawTextCentered(canvas, FormatTick(chart, tick, scale, false), p.X - 4f, p.Y, SKTextAlign.Right, font, fonts.Paint);
                }
            }

            // Подписи категорий — снаружи вершин; сторона выравнивания по направлению луча.
            if (!chart.HideCategoryAxis)
            {
                for (int c = 0; c < categories; c++)
                {
                    float angle = (-90f + 360f * c / categories) * MathF.PI / 180f;
                    float cos = MathF.Cos(angle), sin = MathF.Sin(angle);
                    float x = center.X + cos * (radius + 5f);
                    float y = center.Y + sin * (radius + lineHeight / 2f + 3f);
                    var align = Math.Abs(cos) < 0.2f ? SKTextAlign.Center : cos > 0 ? SKTextAlign.Left : SKTextAlign.Right;
                    DrawTextCentered(canvas, Fit(chart.CategoryLabel(c), font, widestLabel + 2f), x, y, align, font, fonts.Paint);
                }
            }

            // Ряды: по порядку, следующий поверх предыдущего.
            for (int s = 0; s < chart.Series.Count; s++)
            {
                var series = chart.Series[s];
                string color = chart.SeriesColor(s);
                var radarBounds = new SKRect(center.X - radius, center.Y - radius, center.X + radius, center.Y + radius);

                var points = new List<SKPoint?>(categories + 1);
                for (int c = 0; c < categories; c++)
                    points.Add(c < series.Values.Count && series.Values[c] is double v && !double.IsNaN(v) && !double.IsInfinity(v)
                        ? Vertex(c, v)
                        : null);

                if (chart.RadarFilled)
                {
                    using var path = new SKPath();
                    bool started = false;
                    foreach (var point in points)
                    {
                        var p = point ?? center;
                        if (!started) { path.MoveTo(p); started = true; }
                        else path.LineTo(p);
                    }
                    path.Close();
                    using var fill = FillPaint(color, radarBounds);
                    canvas.DrawPath(path, fill);
                }
                else
                {
                    // Контур замкнут: за последней категорией снова первая.
                    points.Add(points.Count > 0 ? points[0] : null);
                    DrawSeriesLine(canvas, points, color, radarBounds, series.ShowLine, false, series.ShowMarkers);
                    points.RemoveAt(points.Count - 1);
                }

                if (chart.ShowDataLabels)
                {
                    for (int c = 0; c < points.Count; c++)
                        if (points[c] is { } p && c < series.Values.Count && series.Values[c] is double raw)
                            DrawTextCentered(canvas, FormatDataLabel(chart, raw), p.X, p.Y - lineHeight / 2f - 2f, SKTextAlign.Center, fonts.Data, fonts.DataPaint);
                }
            }
        }

        // ── Пузырьковая ──────────────────────────────────────────────────

        private static void DrawBubble(SKCanvas canvas, ChartModel chart, SKRect area, ChartFonts fonts)
        {
            area = ReserveAxisTitles(canvas, area, chart.CategoryAxisTitle, chart.ValueAxisTitle, fonts);

            double xMin = double.PositiveInfinity, xMax = double.NegativeInfinity;
            double yMin = double.PositiveInfinity, yMax = double.NegativeInfinity;
            double sizeMax = 0;

            foreach (var series in chart.Series)
            {
                for (int i = 0; i < series.Values.Count; i++)
                {
                    if (series.Values[i] is not double y || double.IsNaN(y) || double.IsInfinity(y)) continue;
                    double x = XOf(series, i);
                    if (double.IsNaN(x) || double.IsInfinity(x)) continue;
                    xMin = Math.Min(xMin, x); xMax = Math.Max(xMax, x);
                    yMin = Math.Min(yMin, y); yMax = Math.Max(yMax, y);
                    sizeMax = Math.Max(sizeMax, Math.Abs(SizeOf(series, i)));
                }
            }

            if (double.IsInfinity(xMin)) { xMin = 0; xMax = 1; }
            if (double.IsInfinity(yMin)) { yMin = 0; yMax = 1; }
            if (sizeMax <= 0) sizeMax = 1;

            var font = fonts.Label;
            float lineHeight = LineHeight(font);
            float xLabelHeight = chart.HideCategoryAxis ? 0f : lineHeight + 3f;

            var yScale = ChartScale.Compute(yMin, yMax, area.Height - xLabelHeight, LabelSizePt, false, chart.ValueAxisMin, chart.ValueAxisMax, ChartScale.MinStepFor(chart.NumberFormat));
            float yLabelWidth = chart.HideValueAxis ? 0f : MaxTickWidth(chart, yScale, font, false) + 4f;
            var xScale = ChartScale.Compute(xMin, xMax, area.Width - yLabelWidth, font.MeasureText("0000") / 2f, false, chart.XAxisMin, chart.XAxisMax, ChartScale.MinStepFor(chart.XNumberFormat));
            float lastXLabelHalf = chart.HideCategoryAxis ? 0f : font.MeasureText(FormatAxisTick(chart.XNumberFormat, xScale.Max, xScale, false)) / 2f;

            var plot = new SKRect(area.Left + yLabelWidth, area.Top + lineHeight / 2f, area.Right - lastXLabelHalf, area.Bottom - xLabelHeight);
            if (plot.Width <= 2f || plot.Height <= 2f) return;

            float X(double value) => plot.Left + (float)(xScale.Fraction(value) * plot.Width);
            float Y(double value) => plot.Bottom - (float)(yScale.Fraction(value) * plot.Height);

            DrawXYGrid(canvas, chart, plot, xScale, yScale, X, Y, fonts);

            float maxRadius = Math.Min(plot.Width, plot.Height) * BubbleMaxShare / 2f;

            // Пузырьки обрезаются областью построения, как у Excel.
            canvas.Save();
            canvas.ClipRect(plot);

            using var outline = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 0.75f, IsAntialias = true };
            for (int s = 0; s < chart.Series.Count; s++)
            {
                var series = chart.Series[s];

                // Крупные пузырьки — первыми, чтобы мелкие не прятались под ними.
                var order = new List<int>();
                for (int i = 0; i < series.Values.Count; i++) order.Add(i);
                order.Sort((a, b) => Math.Abs(SizeOf(series, b)).CompareTo(Math.Abs(SizeOf(series, a))));

                foreach (int i in order)
                {
                    if (series.Values[i] is not double y || double.IsNaN(y) || double.IsInfinity(y)) continue;
                    double size = SizeOf(series, i);
                    if (size <= 0) continue;

                    float r = (float)(Math.Sqrt(size / sizeMax) * maxRadius);
                    var centerPoint = new SKPoint(X(XOf(series, i)), Y(y));

                    string hex = series.PointColors is { } own && i < own.Count && !string.IsNullOrEmpty(own[i])
                        ? own[i]!
                        : chart.SeriesColor(s);
                    using var fill = FillPaint(hex, new SKRect(centerPoint.X - r, centerPoint.Y - r, centerPoint.X + r, centerPoint.Y + r));
                    canvas.DrawCircle(centerPoint, r, fill);
                    canvas.DrawCircle(centerPoint, r, outline);
                }
            }

            canvas.Restore();

            for (int s = 0; s < chart.Series.Count; s++)
            {
                var series = chart.Series[s];
                var trend = new List<(double X, double Y)>();
                for (int i = 0; i < series.Values.Count; i++)
                    if (series.Values[i] is double yv && !double.IsNaN(yv) && !double.IsInfinity(yv)) trend.Add((XOf(series, i), yv));
                SKPoint MapXY(double xv, double yv) => new(X(xv), Y(yv));
                DrawTrendline(canvas, chart, series, s, trend, MapXY, plot, fonts);
                DrawErrorBars(canvas, series, trend.ConvertAll(p => (p.X, p.Y, p.Y)), MapXY, plot);
            }

            if (chart.ShowDataLabels)
            {
                var labelPosition = chart.LabelPositionFor(ChartKind.Bubble);
                float dataLine = LineHeight(fonts.Data);
                for (int s = 0; s < chart.Series.Count; s++)
                {
                    var series = chart.Series[s];
                    for (int i = 0; i < series.Values.Count; i++)
                    {
                        if (series.Values[i] is not double y || double.IsNaN(y) || double.IsInfinity(y)) continue;
                        float r = labelPosition == ChartLabelPosition.Auto || labelPosition == ChartLabelPosition.Center
                            ? 0f
                            : (float)(Math.Sqrt(Math.Max(0, SizeOf(series, i)) / sizeMax) * maxRadius) * 2f;
                        DrawPointLabel(canvas, FormatDataLabel(chart, y), X(XOf(series, i)), Y(y), labelPosition, ChartLabelPosition.Center, dataLine, r, fonts);
                    }
                }
            }
        }

        /// <summary>Размер пузырька; без размеров — единица, все пузырьки одинаковые.</summary>
        private static double SizeOf(ChartSeries series, int index)
        {
            if (series.BubbleSizes is { } sizes && index < sizes.Count && sizes[index] is double size
                && !double.IsNaN(size) && !double.IsInfinity(size))
                return size;
            return 1.0;
        }

        /// <summary>Сетка, оси и подписи делений диаграммы с двумя числовыми осями.</summary>
        private static void DrawXYGrid(
            SKCanvas canvas, ChartModel chart, SKRect plot, ChartAxisScale xScale, ChartAxisScale yScale,
            Func<double, float> x, Func<double, float> y, ChartFonts fonts)
        {
            var font = fonts.Label;
            float lineHeight = LineHeight(font);

            FillPlotArea(canvas, chart, plot);

            // Сетка по Y — линии сетки оси значений, по X — оси X (у Word это ось
            // категорий): основные и вспомогательные по отдельности.
            using (var grid = GridPaint(chart))
            using (var minorGrid = MinorGridPaint(chart))
            {
                if (chart.ShowMinorGridlines)
                    foreach (var tick in MinorTicks(yScale))
                    {
                        float py = y(tick);
                        canvas.DrawLine(plot.Left, py, plot.Right, py, minorGrid);
                    }
                if (chart.ShowCategoryMinorGridlines)
                    foreach (var tick in MinorTicks(xScale))
                    {
                        float px = x(tick);
                        canvas.DrawLine(px, plot.Top, px, plot.Bottom, minorGrid);
                    }

                foreach (var tick in yScale.Ticks())
                {
                    float py = y(tick);
                    if (chart.ShowGridlines) canvas.DrawLine(plot.Left, py, plot.Right, py, grid);
                    if (!chart.HideValueAxis)
                        DrawTextCentered(canvas, FormatTick(chart, tick, yScale, false), plot.Left - 4f, py, SKTextAlign.Right, font, fonts.Paint);
                }

                foreach (var tick in xScale.Ticks())
                {
                    float px = x(tick);
                    if (chart.ShowCategoryGridlines) canvas.DrawLine(px, plot.Top, px, plot.Bottom, grid);
                    if (!chart.HideCategoryAxis)
                        DrawTextCentered(canvas, FormatAxisTick(chart.XNumberFormat, tick, xScale, false), px, plot.Bottom + 3f + lineHeight / 2f, SKTextAlign.Center, font, fonts.Paint);
                }
            }

            using var axis = new SKPaint { Color = AxisLineColor, Style = SKPaintStyle.Stroke, StrokeWidth = GridLineWidthPt, IsAntialias = true };
            float axisY = yScale.Min <= 0 && yScale.Max >= 0 ? y(0) : plot.Bottom;
            canvas.DrawLine(plot.Left, axisY, plot.Right, axisY, axis);
            float axisX = xScale.Min <= 0 && xScale.Max >= 0 ? x(0) : plot.Left;
            canvas.DrawLine(axisX, plot.Top, axisX, plot.Bottom, axis);
        }

        // ── Биржевая ─────────────────────────────────────────────────────

        /// <summary>
        /// Роли рядов биржевой: четыре и больше — открытие, максимум, минимум, закрытие;
        /// три — максимум, минимум, закрытие; два — максимум и минимум; один — закрытие.
        /// </summary>
        private static (int Open, int High, int Low, int Close) StockRoles(int seriesCount) => seriesCount switch
        {
            >= 4 => (0, 1, 2, 3),
            3 => (-1, 0, 1, 2),
            2 => (-1, 0, 1, -1),
            1 => (-1, -1, -1, 0),
            _ => (-1, -1, -1, -1)
        };

        private static double? StockValue(ChartModel chart, int series, int category)
        {
            if (series < 0 || series >= chart.Series.Count) return null;
            var values = chart.Series[series].Values;
            if (category >= values.Count || values[category] is not double v || double.IsNaN(v) || double.IsInfinity(v)) return null;
            return v;
        }

        private static void DrawStock(SKCanvas canvas, ChartModel chart, SKRect area, ChartFonts fonts)
        {
            int categories = Math.Max(1, chart.CategoryCount);
            area = ReserveAxisTitles(canvas, area, chart.CategoryAxisTitle, chart.ValueAxisTitle, fonts);

            var font = fonts.Label;
            float lineHeight = LineHeight(font);

            double min = double.PositiveInfinity, max = double.NegativeInfinity;
            foreach (var series in chart.Series)
                foreach (var value in series.Values)
                    if (value is double v && !double.IsNaN(v) && !double.IsInfinity(v))
                    {
                        min = Math.Min(min, v);
                        max = Math.Max(max, v);
                    }
            if (double.IsInfinity(min)) { min = 0; max = 1; }

            float categoryLabelHeight = chart.HideCategoryAxis ? 0f : lineHeight + 3f;
            var scale = ChartScale.Compute(min, max, area.Height - categoryLabelHeight, LabelSizePt, false, chart.ValueAxisMin, chart.ValueAxisMax, ChartScale.MinStepFor(chart.NumberFormat));
            float valueLabelWidth = chart.HideValueAxis ? 0f : MaxTickWidth(chart, scale, font, false) + 4f;

            float slot = (area.Width - valueLabelWidth) / categories;
            float widestCategory = 0f;
            if (!chart.HideCategoryAxis)
                for (int c = 0; c < categories; c++)
                    widestCategory = Math.Max(widestCategory, font.MeasureText(chart.CategoryLabel(c)));

            bool rotate = !chart.HideCategoryAxis && widestCategory > slot - 2f;
            if (rotate)
            {
                float maxLabel = Math.Min(widestCategory, area.Height * MaxCategoryLabelShare);
                categoryLabelHeight = maxLabel * 0.7071f + lineHeight * 0.7071f + 3f;
                scale = ChartScale.Compute(min, max, area.Height - categoryLabelHeight, LabelSizePt, false, chart.ValueAxisMin, chart.ValueAxisMax, ChartScale.MinStepFor(chart.NumberFormat));
                valueLabelWidth = chart.HideValueAxis ? 0f : MaxTickWidth(chart, scale, font, false) + 4f;
            }

            var plot = new SKRect(area.Left + valueLabelWidth, area.Top + lineHeight / 2f, area.Right, area.Bottom - categoryLabelHeight);
            if (plot.Width <= 2f || plot.Height <= 2f) return;
            slot = plot.Width / categories;

            float ValueY(double value) => plot.Bottom - (float)(scale.Fraction(Math.Clamp(value, scale.Min, scale.Max)) * plot.Height);
            float CenterX(int category) => plot.Left + slot * (category + 0.5f);

            FillPlotArea(canvas, chart, plot);

            using (var grid = GridPaint(chart))
            using (var minorGrid = MinorGridPaint(chart))
            {
                if (chart.ShowMinorGridlines)
                    foreach (var tick in MinorTicks(scale))
                    {
                        float y = ValueY(tick);
                        canvas.DrawLine(plot.Left, y, plot.Right, y, minorGrid);
                    }
                if (chart.ShowCategoryMinorGridlines)
                    for (int c = 0; c < categories; c++)
                        canvas.DrawLine(CenterX(c), plot.Top, CenterX(c), plot.Bottom, minorGrid);
                if (chart.ShowCategoryGridlines)
                    for (int c = 0; c <= categories; c++)
                        canvas.DrawLine(plot.Left + slot * c, plot.Top, plot.Left + slot * c, plot.Bottom, grid);

                foreach (var tick in scale.Ticks())
                {
                    float y = ValueY(tick);
                    if (chart.ShowGridlines) canvas.DrawLine(plot.Left, y, plot.Right, y, grid);
                    if (!chart.HideValueAxis)
                        DrawTextCentered(canvas, FormatTick(chart, tick, scale, false), plot.Left - 4f, y, SKTextAlign.Right, font, fonts.Paint);
                }
            }

            using (var axis = new SKPaint { Color = AxisLineColor, Style = SKPaintStyle.Stroke, StrokeWidth = GridLineWidthPt, IsAntialias = true })
                canvas.DrawLine(plot.Left, plot.Bottom, plot.Right, plot.Bottom, axis);

            var (open, high, low, close) = StockRoles(chart.Series.Count);
            float barWidth = slot * StockBarShare;

            using var ink = new SKPaint { Color = StockInk, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = true };
            using var upFill = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill, IsAntialias = true };
            using var downFill = new SKPaint { Color = StockInk, Style = SKPaintStyle.Fill, IsAntialias = true };

            for (int c = 0; c < categories; c++)
            {
                float x = CenterX(c);

                // Тень свечи: линия от минимума до максимума дня.
                var values = new List<double>();
                foreach (int role in new[] { open, high, low, close })
                    if (StockValue(chart, role, c) is double v) values.Add(v);
                if (values.Count >= 2)
                {
                    double top = double.NegativeInfinity, bottom = double.PositiveInfinity;
                    foreach (var v in values) { top = Math.Max(top, v); bottom = Math.Min(bottom, v); }
                    canvas.DrawLine(x, ValueY(top), x, ValueY(bottom), ink);
                }

                // Тело свечи: белое — день вырос, тёмное — упал.
                if (StockValue(chart, open, c) is double o && StockValue(chart, close, c) is double cl)
                {
                    float y1 = ValueY(o), y2 = ValueY(cl);
                    var body = new SKRect(x - barWidth / 2f, Math.Min(y1, y2), x + barWidth / 2f, Math.Max(Math.Max(y1, y2), Math.Min(y1, y2) + 0.75f));
                    canvas.DrawRect(body, cl >= o ? upFill : downFill);
                    canvas.DrawRect(body, ink);
                }
                else if (StockValue(chart, close, c) is double closeOnly)
                {
                    // Без открытия закрытие — засечка вправо от тени.
                    float y = ValueY(closeOnly);
                    using var tick = new SKPaint { Color = StockInk, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
                    canvas.DrawLine(x, y, x + Math.Max(3f, barWidth / 2f), y, tick);
                }

                if (chart.ShowDataLabels && StockValue(chart, close, c) is double labelValue)
                    DrawTextCentered(canvas, FormatDataLabel(chart, labelValue), x + barWidth / 2f + 3f, ValueY(labelValue), SKTextAlign.Left, fonts.Data, fonts.DataPaint);
            }

            if (!chart.HideCategoryAxis)
                DrawCategoryLabels(canvas, chart, categories, CenterX, slot, plot.Bottom, rotate, area.Height * MaxCategoryLabelShare, fonts);
        }
    }
}
