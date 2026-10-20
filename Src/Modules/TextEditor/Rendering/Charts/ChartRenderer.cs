using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;
using Writersword.Core.Models.Project;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Rendering.Charts
{
    /// <summary>
    /// Рисует диаграмму в прямоугольник холста: название, легенду, оси с делениями,
    /// сетку, столбцы, линии, области, точки и доли круга. Размеры — в пунктах, как у
    /// Word: надписи 9 пт, название 14 пт, линия ряда 2,25 пт, сетка 0,75 пт.
    ///
    /// Рисует вектором, без состояния: один и тот же вызов служит экрану (через кэш
    /// растров <see cref="ChartImageCache"/>), печати и выгрузке в PDF.
    /// </summary>
    public static partial class ChartRenderer
    {
        private const string DefaultFont = "Calibri";
        private const float TitleSizePt = 14f;
        private const float LabelSizePt = 9f;
        private const float AxisTitleSizePt = 10f;
        private const float PaddingPt = 7f;
        private const float GapPt = 5f;
        private const float SeriesLineWidthPt = 2.25f;
        private const float MarkerSizePt = 5f;
        private const float GridLineWidthPt = 0.75f;
        private const float LegendSymbolPt = 6f;
        private const float LegendLineSymbolPt = 16f;
        private const float LegendEntryGapPt = 9f;
        private const float MaxCategoryLabelShare = 0.4f;

        private static readonly SKColor TextColor = new(0x59, 0x59, 0x59);
        private static readonly SKColor GridColor = new(0xD9, 0xD9, 0xD9);
        private static readonly SKColor BorderColor = new(0xD9, 0xD9, 0xD9);
        private static readonly SKColor AxisLineColor = new(0xBF, 0xBF, 0xBF);

        /// <summary>
        /// Рисует диаграмму в прямоугольник rect (в пунктах холста). images — картинки
        /// хранилища проекта по имени файла (для фона); null — источник печати.
        /// </summary>
        public static void Draw(SKCanvas canvas, ChartModel chart, SKRect rect, Func<string, SKImage?>? images = null)
        {
            if (canvas is null || chart is null || rect.Width <= 1f || rect.Height <= 1f) return;

            var previous = _images;
            _images = images;
            try
            {
                DrawCore(canvas, chart, rect);
            }
            finally
            {
                _images = previous;
            }
        }

        /// <summary>Есть ли уже картинка фона диаграммы: пока её нет, растр не запоминается.</summary>
        public static bool BackgroundReady(ChartModel chart, Func<string, SKImage?>? images)
        {
            if (string.IsNullOrEmpty(chart.BackgroundImage)) return true;
            var previous = _images;
            _images = images;
            try
            {
                return ResolveImage(chart.BackgroundImage!) is not null;
            }
            finally
            {
                _images = previous;
            }
        }

        private static void DrawCore(SKCanvas canvas, ChartModel chart, SKRect rect)
        {
            // Скрытые фильтром ряды и категории не рисуются.
            chart = chart.VisibleView();


            canvas.Save();
            canvas.ClipRect(rect);

            DrawBackground(canvas, chart, rect);

            using var fonts = new ChartFonts(chart);

            var area = new SKRect(rect.Left + PaddingPt, rect.Top + PaddingPt, rect.Right - PaddingPt, rect.Bottom - PaddingPt);

            area = DrawTitle(canvas, chart, area, fonts);

            var legendEntries = BuildLegend(chart);
            if (chart.Legend != ChartLegendPosition.None && legendEntries.Count > 0)
                area = DrawLegend(canvas, chart, area, legendEntries, fonts);

            if (area.Width > 4f && area.Height > 4f)
            {
                if (chart.Kind is ChartKind.Pie or ChartKind.Doughnut)
                    DrawPie(canvas, chart, area, fonts);
                else if (chart.Kind == ChartKind.Scatter)
                    DrawScatter(canvas, chart, area, fonts);
                else if (chart.Kind == ChartKind.Bubble)
                    DrawBubble(canvas, chart, area, fonts);
                else if (chart.Kind == ChartKind.Radar)
                    DrawRadar(canvas, chart, area, fonts);
                else if (chart.Kind == ChartKind.Stock)
                    DrawStock(canvas, chart, area, fonts);
                else if (chart.Kind == ChartKind.Bar)
                    DrawBarChart(canvas, chart, area, fonts);
                else
                    DrawCategoryChart(canvas, chart, area, fonts);
            }

            canvas.Restore();

            if (chart.ShowBorder)
            {
                using var border = new SKPaint
                {
                    Color = BorderColor,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = GridLineWidthPt,
                    IsAntialias = true
                };
                float half = GridLineWidthPt / 2f;
                canvas.DrawRect(new SKRect(rect.Left + half, rect.Top + half, rect.Right - half, rect.Bottom - half), border);
            }
        }

        // ── Шрифты и текст ───────────────────────────────────────────────

        /// <summary>
        /// Шрифты и кисти одной отрисовки — по роду надписей: подписи осей (Label и
        /// Paint), название, легенда, названия осей и подписи значений. У каждого рода
        /// своё оформление из модели (<see cref="ChartTextStyle"/>), пустое — Word по
        /// умолчанию.
        /// </summary>
        private sealed class ChartFonts : IDisposable
        {
            public ChartFonts(ChartModel chart)
            {
                string family = string.IsNullOrWhiteSpace(chart.FontFamily) ? DefaultFont : chart.FontFamily!;
                string probe = TextProbe(chart);
                (Label, Paint) = Make(chart.AxisLabelStyle, family, ChartTextRole.AxisLabels, probe);
                (Title, TitlePaint) = Make(chart.TitleStyle, family, ChartTextRole.Title, probe);
                (AxisTitle, AxisTitlePaint) = Make(chart.AxisTitleStyle, family, ChartTextRole.AxisTitles, probe);
                (Legend, LegendPaint) = Make(chart.LegendStyle, family, ChartTextRole.Legend, probe);
                (Data, DataPaint) = Make(chart.DataLabelStyle, family, ChartTextRole.DataLabels, probe);
                DataColored = !string.IsNullOrWhiteSpace(chart.DataLabelStyle?.ColorHex);
            }

            /// <summary>
            /// Знаки, которые диаграмма пишет: название, имена рядов, категории, названия
            /// осей и цифры. По ним проверяется, умеет ли шрифт их нарисовать.
            /// </summary>
            private static string TextProbe(ChartModel chart)
            {
                var text = new System.Text.StringBuilder("0123456789");
                text.Append(chart.DisplayTitle);
                text.Append(chart.CategoryAxisTitle).Append(chart.ValueAxisTitle).Append(chart.SecondaryAxisTitle);
                foreach (var series in chart.Series) text.Append(series.Name);
                foreach (var category in chart.Categories) text.Append(category);

                var chars = new HashSet<char>();
                var probe = new System.Text.StringBuilder();
                foreach (char c in text.ToString())
                    if (!char.IsWhiteSpace(c) && !char.IsControl(c) && !char.IsSurrogate(c) && chars.Add(c)) probe.Append(c);
                return probe.ToString();
            }

            /// <summary>
            /// Гарнитура для надписей: заданная, а если в ней нет знаков диаграммы (шрифт
            /// без кириллицы, шрифт-символы), — шрифт диаграммы, затем Calibri. Иначе
            /// вместо подписей были бы квадраты и значки.
            /// </summary>
            private static SKTypeface TypefaceFor(string face, string family, bool bold, bool italic, string probe)
            {
                var typeface = SKTextRenderer.ResolveTypeface(face, bold, italic);
                if (probe.Length == 0 || typeface.ContainsGlyphs(probe)) return typeface;

                foreach (var fallback in new[] { family, DefaultFont })
                {
                    if (string.Equals(fallback, face, StringComparison.OrdinalIgnoreCase)) continue;
                    var candidate = SKTextRenderer.ResolveTypeface(fallback, bold, italic);
                    if (candidate.ContainsGlyphs(probe)) return candidate;
                }
                return typeface;
            }

            private static (SKFont Font, SKPaint Paint) Make(ChartTextStyle? style, string family, ChartTextRole role, string probe)
            {
                string face = string.IsNullOrWhiteSpace(style?.FontFamily) ? family : style!.FontFamily!;
                bool bold = style?.Bold ?? ChartModel.DefaultBoldOf(role);
                bool italic = style?.Italic ?? false;
                float size = (float)Math.Clamp(style?.SizePt ?? ChartModel.DefaultSizeOf(role), 1.0, 400.0);

                var font = new SKFont(TypefaceFor(face, family, bold, italic, probe), size)
                {
                    Subpixel = true,
                    Edging = SKFontEdging.Antialias
                };
                string? color = style?.ColorHex is { Length: > 0 } own ? own : null;
                bool gradient = color is not null && ChartModel.IsGradient(color);
                var paint = new SKPaint
                {
                    Color = color is null ? TextColor : ParseColor(gradient ? ChartModel.SolidColor(color) : color),
                    IsAntialias = true,
                    Style = SKPaintStyle.Fill
                };

                // Градиент у надписи натягивается на каждую строку отдельно при отрисовке
                // (DrawTextCentered): у кисти шрифта своих границ нет.
                if (gradient) TextGradients.AddOrUpdate(paint, color!);
                return (font, paint);
            }

            /// <summary>Подписи делений и категорий осей.</summary>
            public SKFont Label { get; }
            public SKPaint Paint { get; }
            public SKFont Title { get; }
            public SKPaint TitlePaint { get; }
            public SKFont AxisTitle { get; }
            public SKPaint AxisTitlePaint { get; }
            public SKFont Legend { get; }
            public SKPaint LegendPaint { get; }
            public SKFont Data { get; }
            public SKPaint DataPaint { get; }

            /// <summary>У подписей значений свой цвет: на долях круговой они не белые.</summary>
            public bool DataColored { get; }

            public void Dispose()
            {
                Label.Dispose();
                Paint.Dispose();
                Title.Dispose();
                TitlePaint.Dispose();
                AxisTitle.Dispose();
                AxisTitlePaint.Dispose();
                Legend.Dispose();
                LegendPaint.Dispose();
                Data.Dispose();
                DataPaint.Dispose();
            }
        }

        /// <summary>
        /// Фон всей диаграммы: заливка (цвет или градиент, белая по умолчанию), поверх
        /// неё — картинка фона, растянутая на диаграмму.
        /// </summary>
        private static void DrawBackground(SKCanvas canvas, ChartModel chart, SKRect rect)
        {
            using (var background = FillPaint(chart.BackgroundFill ?? "#FFFFFF", rect))
                canvas.DrawRect(rect, background);

            if (!string.IsNullOrEmpty(chart.BackgroundImage) && ResolveImage(chart.BackgroundImage!) is { } picture)
            {
                using var paint = new SKPaint { IsAntialias = true };
                canvas.DrawImage(picture, new SKRect(0, 0, picture.Width, picture.Height), rect,
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
            }
        }

        /// <summary>Заливка области построения, если она задана.</summary>
        private static void FillPlotArea(SKCanvas canvas, ChartModel chart, SKRect plot)
        {
            if (string.IsNullOrEmpty(chart.PlotAreaFill)) return;
            using var fill = FillPaint(chart.PlotAreaFill!, plot);
            canvas.DrawRect(plot, fill);
        }

        /// <summary>
        /// Источник картинок текущей отрисовки (<see cref="Draw"/>): у полотна — его кеш
        /// картинок; без него — источник печати.
        /// </summary>
        [ThreadStatic] private static Func<string, SKImage?>? _images;

        private static SKImage? ResolveImage(string fileName)
        {
            try
            {
                return _images?.Invoke(fileName) ?? SKTextRenderer.PrintImageResolver?.Invoke(fileName);
            }
            catch
            {
                return null;
            }
        }

        private static float LineHeight(SKFont font)
        {
            var metrics = font.Metrics;
            return metrics.Descent - metrics.Ascent;
        }

        /// <summary>Градиенты надписей: кисть шрифта — код градиента из оформления надписи.</summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SKPaint, string> TextGradients = new();

        /// <summary>Пишет строку так, чтобы её середина по высоте пришлась на centerY.</summary>
        private static void DrawTextCentered(SKCanvas canvas, string text, float x, float centerY, SKTextAlign align, SKFont font, SKPaint paint)
        {
            if (string.IsNullOrEmpty(text)) return;
            var metrics = font.Metrics;
            float baseline = centerY - (metrics.Ascent + metrics.Descent) / 2f;

            // Надпись с градиентом: заливка натягивается на прямоугольник самой строки —
            // как в Word, где градиент текста идёт по его габариту.
            if (TextGradients.TryGetValue(paint, out var gradient))
            {
                float textWidth = font.MeasureText(text);
                float textLeft = align switch
                {
                    SKTextAlign.Center => x - textWidth / 2f,
                    SKTextAlign.Right => x - textWidth,
                    _ => x
                };
                var bounds = new SKRect(textLeft, baseline + metrics.Ascent, textLeft + Math.Max(textWidth, 1f), baseline + metrics.Descent);
                using var fill = FillPaint(gradient, bounds);
                using var textPath = font.GetTextPath(text, new SKPoint(textLeft, baseline));
                canvas.DrawPath(textPath, fill);
                return;
            }

            // Повёрнутый текст (названия осей) — контуром букв. Кеш глифов на
            // видеокарте раскладывает повёрнутую строку по экранным точкам, и буквы
            // наезжали друг на друга; контур от этого не зависит.
            var matrix = canvas.TotalMatrix;
            if (Math.Abs(matrix.SkewX) > 1e-4f || Math.Abs(matrix.SkewY) > 1e-4f)
            {
                float width = font.MeasureText(text);
                float left = align switch
                {
                    SKTextAlign.Center => x - width / 2f,
                    SKTextAlign.Right => x - width,
                    _ => x
                };
                using var path = font.GetTextPath(text, new SKPoint(left, baseline));
                canvas.DrawPath(path, paint);
                return;
            }

            canvas.DrawText(text, x, baseline, align, font, paint);
        }

        /// <summary>Обрезает строку многоточием так, чтобы она уместилась в ширину.</summary>
        private static string Fit(string text, SKFont font, float maxWidth)
        {
            if (string.IsNullOrEmpty(text) || maxWidth <= 0f) return string.Empty;
            if (font.MeasureText(text) <= maxWidth) return text;

            const string ellipsis = "…";
            for (int length = text.Length - 1; length > 0; length--)
            {
                string candidate = text.Substring(0, length).TrimEnd() + ellipsis;
                if (font.MeasureText(candidate) <= maxWidth) return candidate;
            }
            return ellipsis;
        }

        /// <summary>
        /// Разбивает строку на строки по словам под ширину. Слово длиннее ширины
        /// остаётся целым — его обрежет многоточием тот, кто рисует.
        /// </summary>
        private static List<string> Wrap(string text, SKFont font, float maxWidth, int maxLines)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(text)) return lines;

            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string current = string.Empty;
            foreach (var word in words)
            {
                string candidate = current.Length == 0 ? word : current + " " + word;
                if (current.Length > 0 && font.MeasureText(candidate) > maxWidth)
                {
                    lines.Add(current);
                    current = word;
                }
                else
                {
                    current = candidate;
                }
            }
            if (current.Length > 0) lines.Add(current);

            if (lines.Count > maxLines)
            {
                string tail = string.Join(" ", lines.GetRange(maxLines - 1, lines.Count - maxLines + 1));
                lines.RemoveRange(maxLines - 1, lines.Count - maxLines + 1);
                lines.Add(tail);
            }

            for (int i = 0; i < lines.Count; i++)
                lines[i] = Fit(lines[i], font, maxWidth);

            return lines;
        }

        /// <summary>
        /// Цвет ряда или доли: «#RRGGBB», «#AARRGGBB» или градиент (код GradientSpec
        /// «grad|…»). У градиента — его первая точка: для подписей и контуров.
        /// </summary>
        private static SKColor ParseColor(string hex)
        {
            var (a, r, g, b) = ChartModel.ParseArgb(hex);
            return new SKColor((byte)r, (byte)g, (byte)b, (byte)a);
        }

        /// <summary>
        /// Кисть заливки цветом ряда: сплошным или градиентом, натянутым на bounds —
        /// линейным под своим углом, радиальным или коническим из центра, как в
        /// стандартном подборщике цвета.
        /// </summary>
        private static void ApplyFill(SKPaint paint, string? color, SKRect bounds)
        {
            paint.Shader = null;
            if (color is null) return;
            paint.Color = ParseColor(color);
            if (!ChartModel.IsGradient(color)) return;

            var spec = GradientSpec.Parse(color);
            if (spec.IsSolid || bounds.Width <= 0f || bounds.Height <= 0f) return;

            paint.Color = SKColors.Black;
            paint.Shader = Rendering.GradientShaderFactory.BuildShader(spec, bounds);
        }

        private static SKPaint FillPaint(string color, SKRect bounds)
        {
            var paint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
            ApplyFill(paint, color, bounds);
            return paint;
        }

        // ── Название ─────────────────────────────────────────────────────

        private static SKRect DrawTitle(SKCanvas canvas, ChartModel chart, SKRect area, ChartFonts fonts)
        {
            string? title = chart.DisplayTitle;
            if (string.IsNullOrWhiteSpace(title)) return area;

            var lines = Wrap(title!, fonts.Title, area.Width, 2);
            float lineHeight = LineHeight(fonts.Title);
            float y = area.Top;
            foreach (var line in lines)
            {
                DrawTextCentered(canvas, line, area.MidX, y + lineHeight / 2f, SKTextAlign.Center, fonts.Title, fonts.TitlePaint);
                y += lineHeight;
            }

            return new SKRect(area.Left, Math.Min(area.Bottom, y + GapPt), area.Right, area.Bottom);
        }

        // ── Легенда ──────────────────────────────────────────────────────

        private readonly record struct LegendEntry(string Label, SKColor Color, bool LineSymbol, bool Marker, string? Fill = null);

        private static List<LegendEntry> BuildLegend(ChartModel chart)
        {
            var entries = new List<LegendEntry>();

            if (chart.Kind is ChartKind.Pie or ChartKind.Doughnut)
            {
                if (chart.Series.Count == 0) return entries;
                var first = chart.Series[0];
                int count = Math.Max(chart.Categories.Count, first.Values.Count);
                for (int i = 0; i < count; i++)
                    entries.Add(new LegendEntry(chart.CategoryLabel(i), ParseColor(chart.PointColor(first, i)), false, false, chart.PointColor(first, i)));
                return entries;
            }

            // У биржевой ряды сами линий не рисуют: в легенде — их имена с точкой
            // цвета теней и свечей.
            if (chart.Kind == ChartKind.Stock)
            {
                for (int i = 0; i < chart.Series.Count; i++)
                {
                    string stockName = string.IsNullOrWhiteSpace(chart.Series[i].Name) ? "Ряд " + (i + 1) : chart.Series[i].Name;
                    entries.Add(new LegendEntry(stockName, StockInk, false, true));
                }
                return entries;
            }

            for (int i = 0; i < chart.Series.Count; i++)
            {
                var series = chart.Series[i];
                var kind = chart.KindOf(series);
                bool line = kind is ChartKind.Line
                    || (kind == ChartKind.Scatter && series.ShowLine)
                    || (kind == ChartKind.Radar && !chart.RadarFilled);
                bool marker = (kind is ChartKind.Line or ChartKind.Scatter or ChartKind.Radar && series.ShowMarkers)
                    || kind == ChartKind.Bubble;
                if (kind == ChartKind.Radar && chart.RadarFilled) marker = false;
                string name = string.IsNullOrWhiteSpace(series.Name) ? "Ряд " + (i + 1) : series.Name;
                entries.Add(new LegendEntry(name, ParseColor(chart.SeriesColor(i)), line, marker, chart.SeriesColor(i)));
            }

            return entries;
        }

        private static float LegendEntryWidth(LegendEntry entry, SKFont font)
            => (entry.LineSymbol ? LegendLineSymbolPt : LegendSymbolPt) + 3f + font.MeasureText(entry.Label);

        private static SKRect DrawLegend(SKCanvas canvas, ChartModel chart, SKRect area, List<LegendEntry> entries, ChartFonts fonts)
        {
            var font = fonts.Legend;
            float lineHeight = LineHeight(font) + 2f;

            bool horizontal = chart.Legend is ChartLegendPosition.Bottom or ChartLegendPosition.Top;

            if (horizontal)
            {
                // Строки легенды: записи идут подряд и переносятся, когда не помещаются.
                var rows = new List<List<(LegendEntry Entry, float Width)>>();
                var row = new List<(LegendEntry, float)>();
                float rowWidth = 0f;
                foreach (var entry in entries)
                {
                    float width = Math.Min(LegendEntryWidth(entry, font), area.Width);
                    float needed = row.Count == 0 ? width : rowWidth + LegendEntryGapPt + width;
                    if (row.Count > 0 && needed > area.Width)
                    {
                        rows.Add(row);
                        row = new List<(LegendEntry, float)>();
                        rowWidth = width;
                        row.Add((entry, width));
                    }
                    else
                    {
                        rowWidth = needed;
                        row.Add((entry, width));
                    }
                }
                if (row.Count > 0) rows.Add(row);

                int maxRows = Math.Max(1, (int)(area.Height * 0.3f / lineHeight));
                if (rows.Count > maxRows) rows.RemoveRange(maxRows, rows.Count - maxRows);

                float height = rows.Count * lineHeight;
                float top = chart.Legend == ChartLegendPosition.Top ? area.Top : area.Bottom - height;

                for (int r = 0; r < rows.Count; r++)
                {
                    float total = 0f;
                    foreach (var (_, width) in rows[r]) total += width;
                    total += LegendEntryGapPt * (rows[r].Count - 1);

                    float x = area.MidX - total / 2f;
                    float centerY = top + r * lineHeight + lineHeight / 2f;
                    foreach (var (entry, width) in rows[r])
                    {
                        DrawLegendEntry(canvas, entry, x, centerY, width, fonts);
                        x += width + LegendEntryGapPt;
                    }
                }

                return chart.Legend == ChartLegendPosition.Top
                    ? new SKRect(area.Left, area.Top + height + GapPt, area.Right, area.Bottom)
                    : new SKRect(area.Left, area.Top, area.Right, area.Bottom - height - GapPt);
            }

            // Легенда сбоку: записи столбиком, по середине высоты.
            float maxWidth = area.Width * 0.35f;
            float columnWidth = 0f;
            foreach (var entry in entries)
                columnWidth = Math.Max(columnWidth, Math.Min(LegendEntryWidth(entry, font), maxWidth));

            int fit = Math.Max(1, (int)(area.Height / lineHeight));
            int shown = Math.Min(entries.Count, fit);
            float columnHeight = shown * lineHeight;
            float left = chart.Legend == ChartLegendPosition.Left ? area.Left : area.Right - columnWidth;
            float startY = area.MidY - columnHeight / 2f;

            for (int i = 0; i < shown; i++)
                DrawLegendEntry(canvas, entries[i], left, startY + i * lineHeight + lineHeight / 2f, columnWidth, fonts);

            return chart.Legend == ChartLegendPosition.Left
                ? new SKRect(area.Left + columnWidth + GapPt * 2f, area.Top, area.Right, area.Bottom)
                : new SKRect(area.Left, area.Top, area.Right - columnWidth - GapPt * 2f, area.Bottom);
        }

        private static void DrawLegendEntry(SKCanvas canvas, LegendEntry entry, float x, float centerY, float width, ChartFonts fonts)
        {
            using var paint = new SKPaint { Color = entry.Color, IsAntialias = true };
            float symbolWidth;

            // Значок градиентного ряда — тем же градиентом в его клетке.
            float boxWidth = entry.LineSymbol ? LegendLineSymbolPt : LegendSymbolPt;
            ApplyFill(paint, entry.Fill ?? string.Empty, new SKRect(x, centerY - LegendSymbolPt / 2f, x + boxWidth, centerY + LegendSymbolPt / 2f));
            if (entry.Fill is null) paint.Color = entry.Color;

            if (entry.LineSymbol)
            {
                symbolWidth = LegendLineSymbolPt;
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = SeriesLineWidthPt;
                paint.StrokeCap = SKStrokeCap.Round;
                canvas.DrawLine(x + 1f, centerY, x + symbolWidth - 1f, centerY, paint);
                if (entry.Marker)
                {
                    paint.Style = SKPaintStyle.Fill;
                    canvas.DrawCircle(x + symbolWidth / 2f, centerY, MarkerSizePt / 2f, paint);
                }
            }
            else if (entry.Marker)
            {
                symbolWidth = LegendSymbolPt;
                paint.Style = SKPaintStyle.Fill;
                canvas.DrawCircle(x + symbolWidth / 2f, centerY, MarkerSizePt / 2f, paint);
            }
            else
            {
                symbolWidth = LegendSymbolPt;
                paint.Style = SKPaintStyle.Fill;
                canvas.DrawRect(new SKRect(x, centerY - symbolWidth / 2f, x + symbolWidth, centerY + symbolWidth / 2f), paint);
            }

            float textX = x + symbolWidth + 3f;
            string label = Fit(entry.Label, fonts.Legend, x + width - textX);
            DrawTextCentered(canvas, label, textX, centerY, SKTextAlign.Left, fonts.Legend, fonts.LegendPaint);
        }

        // ── Подписи чисел ────────────────────────────────────────────────

        private static string FormatTick(ChartModel chart, double value, ChartAxisScale scale, bool percent)
            => FormatAxisTick(chart.NumberFormat, value, scale, percent);

        private static string FormatAxisTick(string? format, double value, ChartAxisScale scale, bool percent)
        {
            if (percent) return ChartScale.FormatValue(value, "0%");
            if (!string.IsNullOrEmpty(format)) return ChartScale.FormatValue(value, format);
            return ChartScale.FormatValue(value, null, ChartScale.DecimalsForStep(scale.Step));
        }

        private static string FormatDataLabel(ChartModel chart, double value)
            => ChartScale.FormatValue(value, chart.NumberFormat);

        // ── Названия осей ────────────────────────────────────────────────

        /// <summary>
        /// Откладывает место под названия осей: название оси категорий — под областью
        /// построения, название оси значений — повёрнутым слева. У линейчатой оси
        /// меняются местами вместе с самими осями.
        /// </summary>
        private static SKRect ReserveAxisTitles(
            SKCanvas canvas, SKRect area, string? bottomTitle, string? leftTitle, ChartFonts fonts)
        {
            float lineHeight = LineHeight(fonts.AxisTitle);

            if (!string.IsNullOrWhiteSpace(bottomTitle))
            {
                string text = Fit(bottomTitle!, fonts.AxisTitle, area.Width);
                DrawTextCentered(canvas, text, area.MidX, area.Bottom - lineHeight / 2f, SKTextAlign.Center, fonts.AxisTitle, fonts.AxisTitlePaint);
                area = new SKRect(area.Left, area.Top, area.Right, area.Bottom - lineHeight - GapPt);
            }

            if (!string.IsNullOrWhiteSpace(leftTitle))
            {
                string text = Fit(leftTitle!, fonts.AxisTitle, area.Height);
                canvas.Save();
                canvas.Translate(area.Left + lineHeight / 2f, area.MidY);
                canvas.RotateDegrees(-90f);
                DrawTextCentered(canvas, text, 0f, 0f, SKTextAlign.Center, fonts.AxisTitle, fonts.AxisTitlePaint);
                canvas.Restore();
                area = new SKRect(area.Left + lineHeight + GapPt, area.Top, area.Right, area.Bottom);
            }

            return area;
        }

        /// <summary>Название вспомогательной оси — повёрнутым справа, как у Excel.</summary>
        private static SKRect ReserveRightAxisTitle(SKCanvas canvas, SKRect area, string? title, ChartFonts fonts)
        {
            if (string.IsNullOrWhiteSpace(title)) return area;

            float lineHeight = LineHeight(fonts.AxisTitle);
            string text = Fit(title!, fonts.AxisTitle, area.Height);
            canvas.Save();
            canvas.Translate(area.Right - lineHeight / 2f, area.MidY);
            canvas.RotateDegrees(-90f);
            DrawTextCentered(canvas, text, 0f, 0f, SKTextAlign.Center, fonts.AxisTitle, fonts.AxisTitlePaint);
            canvas.Restore();
            return new SKRect(area.Left, area.Top, area.Right - lineHeight - GapPt, area.Bottom);
        }

        // ── Данные осевых диаграмм ───────────────────────────────────────

        /// <summary>Ряды диаграммы, разложенные по виду: столбцы, области и линии.</summary>
        private sealed class CategoryData
        {
            public List<(ChartSeries Series, int Index)> Bars { get; } = new();
            public List<(ChartSeries Series, int Index)> Areas { get; } = new();
            public List<(ChartSeries Series, int Index)> Lines { get; } = new();

            public List<(double From, double To)?[]> BarStack { get; set; } = new();
            public List<(double From, double To)?[]> AreaStack { get; set; } = new();
            public List<(double From, double To)?[]> LineStack { get; set; } = new();

            public double Min { get; set; } = double.PositiveInfinity;
            public double Max { get; set; } = double.NegativeInfinity;

            public void Include(double value)
            {
                if (double.IsNaN(value) || double.IsInfinity(value)) return;
                if (value < Min) Min = value;
                if (value > Max) Max = value;
            }
        }

        /// <param name="secondary">
        /// null — все ряды; false — ряды основной оси; true — ряды вспомогательной.
        /// </param>
        private static CategoryData CollectCategoryData(ChartModel chart, int categories, bool horizontalBars, bool? secondary = null)
        {
            var data = new CategoryData();

            for (int i = 0; i < chart.Series.Count; i++)
            {
                var series = chart.Series[i];
                if (secondary is bool wanted && chart.IsOnSecondaryAxis(series) != wanted) continue;
                var kind = horizontalBars ? ChartKind.Bar : chart.KindOf(series);
                switch (kind)
                {
                    case ChartKind.Column:
                    case ChartKind.Bar:
                        data.Bars.Add((series, i));
                        break;
                    case ChartKind.Area:
                        data.Areas.Add((series, i));
                        break;
                    default:
                        data.Lines.Add((series, i));
                        break;
                }
            }

            data.BarStack = ChartScale.Stack(data.Bars.ConvertAll(b => b.Series), categories, chart.Grouping);
            data.AreaStack = ChartScale.Stack(data.Areas.ConvertAll(b => b.Series), categories, chart.Grouping);
            data.LineStack = ChartScale.Stack(data.Lines.ConvertAll(b => b.Series), categories, chart.Grouping);

            foreach (var stack in new[] { data.BarStack, data.AreaStack })
                foreach (var points in stack)
                    foreach (var point in points)
                        if (point is { } p)
                        {
                            data.Include(p.From);
                            data.Include(p.To);
                        }

            foreach (var points in data.LineStack)
                foreach (var point in points)
                    if (point is { } p) data.Include(p.To);

            if (double.IsInfinity(data.Min) || double.IsInfinity(data.Max))
            {
                data.Min = 0;
                data.Max = 1;
            }

            return data;
        }

        private static ChartAxisScale ValueScale(ChartModel chart, CategoryData data, float lengthPt, bool forceZero)
        {
            double? fixedMin = chart.ValueAxisMin;
            double? fixedMax = chart.ValueAxisMax;

            if (chart.Grouping == ChartGrouping.PercentStacked)
            {
                fixedMin ??= data.Min < 0 ? -1.0 : 0.0;
                fixedMax ??= data.Max > 0 ? 1.0 : 0.0;
            }

            // У нормированной подписи всегда проценты, свой формат там не участвует.
            string? format = chart.Grouping == ChartGrouping.PercentStacked ? "0%" : chart.NumberFormat;
            return ChartScale.Compute(data.Min, data.Max, lengthPt, LabelSizePt, forceZero, fixedMin, fixedMax, ChartScale.MinStepFor(format));
        }

        private static float MaxTickWidth(ChartModel chart, ChartAxisScale scale, SKFont font, bool percent)
            => MaxAxisTickWidth(chart.NumberFormat, scale, font, percent);

        private static float MaxAxisTickWidth(string? format, ChartAxisScale scale, SKFont font, bool percent)
        {
            float width = 0f;
            foreach (var tick in scale.Ticks())
                width = Math.Max(width, font.MeasureText(FormatAxisTick(format, tick, scale, percent)));
            return width;
        }

        /// <summary>Шкала вспомогательной оси: свои пределы, та же группировка.</summary>
        private static ChartAxisScale SecondaryValueScale(ChartModel chart, CategoryData data, float lengthPt, bool forceZero)
        {
            double? fixedMin = chart.SecondaryAxisMin;
            double? fixedMax = chart.SecondaryAxisMax;

            if (chart.Grouping == ChartGrouping.PercentStacked)
            {
                fixedMin ??= data.Min < 0 ? -1.0 : 0.0;
                fixedMax ??= data.Max > 0 ? 1.0 : 0.0;
            }

            string? format = chart.Grouping == ChartGrouping.PercentStacked ? "0%" : chart.SecondaryNumberFormat;
            return ChartScale.Compute(data.Min, data.Max, lengthPt, LabelSizePt, forceZero, fixedMin, fixedMax, ChartScale.MinStepFor(format));
        }

        // ── Гистограмма, график и области ────────────────────────────────

        private static void DrawCategoryChart(SKCanvas canvas, ChartModel chart, SKRect area, ChartFonts fonts)
        {
            int categories = chart.CategoryCount;
            if (categories == 0) categories = 1;

            // Комбинированная диаграмма со вспомогательной осью: ряды на ней — своей
            // шкалой, подписи которой справа от области построения.
            bool dual = chart.HasSecondaryAxis;

            area = ReserveAxisTitles(canvas, area, chart.CategoryAxisTitle, chart.ValueAxisTitle, fonts);
            if (dual) area = ReserveRightAxisTitle(canvas, area, chart.SecondaryAxisTitle, fonts);

            var data = CollectCategoryData(chart, categories, horizontalBars: false, secondary: dual ? false : null);
            var data2 = dual ? CollectCategoryData(chart, categories, horizontalBars: false, secondary: true) : null;

            bool percent = chart.Grouping == ChartGrouping.PercentStacked;
            bool forceZero = data.Bars.Count > 0 || data.Areas.Count > 0;
            bool forceZero2 = data2 is not null && (data2.Bars.Count > 0 || data2.Areas.Count > 0);
            var font = fonts.Label;
            float lineHeight = LineHeight(font);

            ChartAxisScale scale2 = default;
            float valueLabelWidth2 = 0f;

            void ComputeScales(float plotHeight, out ChartAxisScale primary, out float primaryWidth)
            {
                primary = ValueScale(chart, data, plotHeight, forceZero);
                primaryWidth = chart.HideValueAxis ? 0f : MaxTickWidth(chart, primary, font, percent) + 4f;
                if (data2 is not null)
                {
                    scale2 = SecondaryValueScale(chart, data2, plotHeight, forceZero2);
                    valueLabelWidth2 = MaxAxisTickWidth(chart.SecondaryNumberFormat, scale2, font, percent) + 4f;
                }
            }

            // Таблица данных встаёт на место подписей категорий: строка категорий и по
            // строке на ряд; имена рядов — слева, под подписями оси значений.
            bool table = chart.DataTableVisible;
            float rowHeight = lineHeight + 4f;
            float tableKeyWidth = table && !chart.HideDataTableKeys ? LegendSymbolPt + 4f : 0f;
            float tableLabelWidth = 0f;
            if (table)
            {
                foreach (var series in chart.Series)
                    tableLabelWidth = Math.Max(tableLabelWidth, font.MeasureText(series.Name));
                tableLabelWidth = Math.Min(tableLabelWidth + tableKeyWidth + 9f, area.Width * 0.35f);
            }

            // Подписи категорий: в строку, если помещаются в слот, иначе наклонно.
            float categoryLabelHeight = table ? rowHeight * (1 + chart.Series.Count) : chart.HideCategoryAxis ? 0f : lineHeight + 3f;
            ComputeScales(area.Height - categoryLabelHeight, out var scale, out float valueLabelWidth);
            if (table) valueLabelWidth = Math.Max(valueLabelWidth, tableLabelWidth);

            float plotWidth = area.Width - valueLabelWidth - valueLabelWidth2;
            float slot = plotWidth / categories;

            float widestCategory = 0f;
            if (!chart.HideCategoryAxis)
                for (int c = 0; c < categories; c++)
                    widestCategory = Math.Max(widestCategory, font.MeasureText(chart.CategoryLabel(c)));

            bool rotate = !table && !chart.HideCategoryAxis && widestCategory > slot - 2f;
            if (rotate)
            {
                float maxLabel = Math.Min(widestCategory, area.Height * MaxCategoryLabelShare);
                categoryLabelHeight = maxLabel * 0.7071f + lineHeight * 0.7071f + 3f;
                ComputeScales(area.Height - categoryLabelHeight, out scale, out valueLabelWidth);
                plotWidth = area.Width - valueLabelWidth - valueLabelWidth2;
                slot = plotWidth / categories;
            }

            // Области от края до края: крайние подписи категорий стоят на краях области
            // построения, и под их половины нужны поля, иначе они срезаются. С таблицей
            // данных точки стоят посередине её столбцов.
            bool areaAtEdges = !table && data.Bars.Count == 0 && data.Lines.Count == 0
                && (data2 is null || (data2.Bars.Count == 0 && data2.Lines.Count == 0));
            float leftInset = 0f, rightInset = 0f;
            if (areaAtEdges && !chart.HideCategoryAxis && !rotate && categories > 1)
            {
                leftInset = Math.Max(0f, font.MeasureText(chart.CategoryLabel(0)) / 2f - valueLabelWidth);
                rightInset = Math.Max(0f, font.MeasureText(chart.CategoryLabel(categories - 1)) / 2f - valueLabelWidth2);
            }

            var plot = new SKRect(
                area.Left + valueLabelWidth + leftInset,
                area.Top + lineHeight / 2f,
                area.Right - valueLabelWidth2 - rightInset,
                area.Bottom - categoryLabelHeight);
            if (plot.Width <= 2f || plot.Height <= 2f) return;
            slot = plot.Width / categories;

            float ValueYOf(ChartAxisScale axis, double value) => plot.Bottom - (float)(axis.Fraction(value) * plot.Height);
            float ValueY(double value) => ValueYOf(scale, value);

            // Сетка и подписи оси значений. Сетка — по основной оси, как у Excel.
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

                // Сетка по оси категорий: основная — по границам категорий (у областей от
                // края до края — по самим точкам), вспомогательная — посередине между ними.
                if (chart.ShowCategoryMinorGridlines)
                    for (int c = 0; c < (areaAtEdges ? categories - 1 : categories); c++)
                    {
                        float x = areaAtEdges ? plot.Left + plot.Width * (c + 0.5f) / Math.Max(1, categories - 1) : plot.Left + slot * (c + 0.5f);
                        canvas.DrawLine(x, plot.Top, x, plot.Bottom, minorGrid);
                    }
                if (chart.ShowCategoryGridlines)
                    for (int c = 0; c <= (areaAtEdges ? categories - 1 : categories); c++)
                    {
                        float x = areaAtEdges ? plot.Left + plot.Width * c / Math.Max(1, categories - 1) : plot.Left + slot * c;
                        canvas.DrawLine(x, plot.Top, x, plot.Bottom, grid);
                    }

                foreach (var tick in scale.Ticks())
                {
                    float y = ValueY(tick);
                    if (chart.ShowGridlines) canvas.DrawLine(plot.Left, y, plot.Right, y, grid);
                    if (!chart.HideValueAxis)
                        DrawTextCentered(canvas, FormatTick(chart, tick, scale, percent), plot.Left - 4f, y, SKTextAlign.Right, font, fonts.Paint);
                }
            }

            if (data2 is not null)
            {
                foreach (var tick in scale2.Ticks())
                {
                    float y = ValueYOf(scale2, tick);
                    DrawTextCentered(canvas, FormatAxisTick(chart.SecondaryNumberFormat, tick, scale2, percent), plot.Right + 4f, y, SKTextAlign.Left, font, fonts.Paint);
                }
            }

            float PointX(int category, bool edges)
            {
                if (edges && categories > 1) return plot.Left + plot.Width * category / (categories - 1);
                return plot.Left + slot * (category + 0.5f);
            }

            // Области — под столбцами и линиями, как у Excel. Ряды рисуются по порядку:
            // следующий ложится поверх предыдущего.
            void DrawAreas(CategoryData set, ChartAxisScale axis)
            {
                for (int a = 0; a < set.Areas.Count; a++)
                {
                    var (_, index) = set.Areas[a];
                    var points = set.AreaStack[a];
                    using var path = new SKPath();
                    bool started = false;
                    int firstCategory = -1, lastCategory = -1;
                    for (int c = 0; c < categories; c++)
                    {
                        if (points[c] is not { } p) continue;
                        float x = PointX(c, areaAtEdges);
                        float y = ValueYOf(axis, p.To);
                        if (!started) { path.MoveTo(x, y); started = true; firstCategory = c; }
                        else path.LineTo(x, y);
                        lastCategory = c;
                    }
                    if (!started) continue;
                    for (int c = lastCategory; c >= firstCategory; c--)
                    {
                        if (points[c] is not { } p) continue;
                        path.LineTo(PointX(c, areaAtEdges), ValueYOf(axis, chart.Grouping == ChartGrouping.Standard ? Math.Max(axis.Min, Math.Min(0, axis.Max)) : p.From));
                    }
                    path.Close();

                    using var fill = FillPaint(chart.SeriesColor(index), path.Bounds);
                    canvas.DrawPath(path, fill);
                }
            }

            (double First, double Pitch, double Width) BarLayout(CategoryData set)
            {
                bool stacked = chart.Grouping != ChartGrouping.Standard;
                int barsInCluster = stacked ? 1 : set.Bars.Count;
                double overlap = stacked ? 1.0 : chart.OverlapPercent / 100.0;
                return ChartScale.BarSlots(slot, barsInCluster, chart.GapWidthPercent / 100.0, overlap);
            }

            void DrawBars(CategoryData set, ChartAxisScale axis)
            {
                if (set.Bars.Count == 0) return;
                bool stacked = chart.Grouping != ChartGrouping.Standard;
                var (first, pitch, width) = BarLayout(set);

                for (int b = 0; b < set.Bars.Count; b++)
                {
                    var (series, index) = set.Bars[b];
                    var points = set.BarStack[b];
                    for (int c = 0; c < categories; c++)
                    {
                        if (points[c] is not { } p) continue;
                        float x = plot.Left + slot * c + (float)(first + (stacked ? 0 : pitch * b));
                        float y1 = ValueYOf(axis, Clamp(p.From, axis));
                        float y2 = ValueYOf(axis, Clamp(p.To, axis));
                        var bar = new SKRect(x, Math.Min(y1, y2), x + (float)width, Math.Max(y1, y2));
                        using var fill = FillPaint(BarColor(chart, series, index, c), bar);
                        canvas.DrawRect(bar, fill);
                    }
                }
            }

            void DrawLines(CategoryData set, ChartAxisScale axis)
            {
                for (int l = 0; l < set.Lines.Count; l++)
                {
                    var (series, index) = set.Lines[l];
                    var points = set.LineStack[l];
                    var placed = new List<SKPoint?>(categories);
                    for (int c = 0; c < categories; c++)
                        placed.Add(points[c] is { } p ? new SKPoint(PointX(c, false), ValueYOf(axis, Clamp(p.To, axis))) : null);

                    DrawSeriesLine(canvas, placed, chart.SeriesColor(index), plot, series.ShowLine, series.Smooth, series.ShowMarkers);
                }
            }

            void DrawValueLabels(CategoryData set, ChartAxisScale axis)
            {
                bool stacked = chart.Grouping != ChartGrouping.Standard;
                float dataLine = LineHeight(fonts.Data);
                if (set.Bars.Count > 0)
                {
                    var (first, pitch, width) = BarLayout(set);
                    var position = chart.LabelPositionFor(ChartKind.Column);
                    if (position == ChartLabelPosition.Auto) position = stacked ? ChartLabelPosition.Center : ChartLabelPosition.OutsideEnd;
                    for (int b = 0; b < set.Bars.Count; b++)
                    {
                        var (series, _) = set.Bars[b];
                        var points = set.BarStack[b];
                        for (int c = 0; c < categories; c++)
                        {
                            if (points[c] is not { } p || c >= series.Values.Count || series.Values[c] is not double raw) continue;
                            float x = plot.Left + slot * c + (float)(first + (stacked ? 0 : pitch * b) + width / 2.0);
                            string label = FormatDataLabel(chart, raw);
                            float end = ValueYOf(axis, Clamp(p.To, axis));
                            float start = ValueYOf(axis, Clamp(p.From, axis));
                            bool up = end <= start;
                            float y = position switch
                            {
                                ChartLabelPosition.Center => (start + end) / 2f,
                                ChartLabelPosition.InsideEnd => up ? end + dataLine / 2f + 2f : end - dataLine / 2f - 2f,
                                ChartLabelPosition.InsideBase => up ? start - dataLine / 2f - 2f : start + dataLine / 2f + 2f,
                                _ => up ? end - dataLine / 2f - 1f : end + dataLine / 2f + 1f
                            };
                            DrawTextCentered(canvas, label, x, y, SKTextAlign.Center, fonts.Data, fonts.DataPaint);
                        }
                    }
                }

                var linePosition = chart.LabelPositionFor(ChartKind.Line);
                for (int l = 0; l < set.Lines.Count; l++)
                {
                    var (series, _) = set.Lines[l];
                    var points = set.LineStack[l];
                    for (int c = 0; c < categories; c++)
                    {
                        if (points[c] is not { } p || c >= series.Values.Count || series.Values[c] is not double raw) continue;
                        DrawPointLabel(canvas, FormatDataLabel(chart, raw), PointX(c, false), ValueYOf(axis, Clamp(p.To, axis)),
                            linePosition, ChartLabelPosition.Above, dataLine, MarkerSizePt, fonts);
                    }
                }

                for (int a = 0; a < set.Areas.Count; a++)
                {
                    var (series, _) = set.Areas[a];
                    var points = set.AreaStack[a];
                    for (int c = 0; c < categories; c++)
                    {
                        if (points[c] is not { } p || c >= series.Values.Count || series.Values[c] is not double raw) continue;
                        float y = (ValueYOf(axis, Clamp(p.From, axis)) + ValueYOf(axis, Clamp(p.To, axis))) / 2f;
                        DrawTextCentered(canvas, FormatDataLabel(chart, raw), PointX(c, areaAtEdges), y, SKTextAlign.Center, fonts.Data, fonts.DataPaint);
                    }
                }
            }

            DrawAreas(data, scale);
            DrawBars(data, scale);

            // Ось категорий — на нуле, если ноль в шкале, иначе внизу.
            float axisY = scale.Min <= 0 && scale.Max >= 0 ? ValueY(0) : plot.Bottom;
            using (var axis = new SKPaint { Color = AxisLineColor, Style = SKPaintStyle.Stroke, StrokeWidth = GridLineWidthPt, IsAntialias = true })
                canvas.DrawLine(plot.Left, axisY, plot.Right, axisY, axis);

            // Линии проекции, минимума-максимума и полосы повышения — под линиями рядов.
            void DrawLineExtras(CategoryData set, ChartAxisScale axis)
            {
                float baseY = axis.Min <= 0 && axis.Max >= 0 ? ValueYOf(axis, 0) : plot.Bottom;

                if (chart.ShowDropLines)
                {
                    using var drop = new SKPaint { Color = DropLineInk, Style = SKPaintStyle.Stroke, StrokeWidth = 0.75f, IsAntialias = true };
                    for (int l = 0; l < set.Lines.Count; l++)
                        for (int c = 0; c < categories; c++)
                            if (set.LineStack[l][c] is { } p)
                                canvas.DrawLine(PointX(c, false), ValueYOf(axis, Clamp(p.To, axis)), PointX(c, false), baseY, drop);
                    for (int a = 0; a < set.Areas.Count; a++)
                        for (int c = 0; c < categories; c++)
                            if (set.AreaStack[a][c] is { } p)
                                canvas.DrawLine(PointX(c, areaAtEdges), ValueYOf(axis, Clamp(p.To, axis)), PointX(c, areaAtEdges), baseY, drop);
                }

                if (set.Lines.Count < 2) return;

                if (chart.ShowHighLowLines)
                {
                    using var ink = new SKPaint { Color = AnalyticsInk, Style = SKPaintStyle.Stroke, StrokeWidth = 0.75f, IsAntialias = true };
                    for (int c = 0; c < categories; c++)
                    {
                        double low = double.PositiveInfinity, high = double.NegativeInfinity;
                        for (int l = 0; l < set.Lines.Count; l++)
                            if (set.LineStack[l][c] is { } p) { low = Math.Min(low, p.To); high = Math.Max(high, p.To); }
                        if (high > low)
                            canvas.DrawLine(PointX(c, false), ValueYOf(axis, Clamp(high, axis)), PointX(c, false), ValueYOf(axis, Clamp(low, axis)), ink);
                    }
                }

                if (chart.ShowUpDownBars)
                {
                    // Белая полоса — последний ряд выше первого (рост), тёмная — ниже.
                    using var border = new SKPaint { Color = AnalyticsInk, Style = SKPaintStyle.Stroke, StrokeWidth = 0.75f, IsAntialias = true };
                    using var up = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill, IsAntialias = true };
                    using var down = new SKPaint { Color = AnalyticsInk, Style = SKPaintStyle.Fill, IsAntialias = true };
                    float half = slot * 0.2f;
                    for (int c = 0; c < categories; c++)
                    {
                        if (set.LineStack[0][c] is not { } firstPoint || set.LineStack[set.Lines.Count - 1][c] is not { } lastPoint) continue;
                        float y1 = ValueYOf(axis, Clamp(firstPoint.To, axis));
                        float y2 = ValueYOf(axis, Clamp(lastPoint.To, axis));
                        float x = PointX(c, false);
                        var rect = new SKRect(x - half, Math.Min(y1, y2), x + half, Math.Max(Math.Max(y1, y2), Math.Min(y1, y2) + 0.75f));
                        canvas.DrawRect(rect, lastPoint.To >= firstPoint.To ? up : down);
                        canvas.DrawRect(rect, border);
                    }
                }
            }

            // Линии тренда и планки погрешностей рядов набора: X — номер категории с единицы, как у Excel.
            void DrawAnalytics(CategoryData set, ChartAxisScale axis)
            {
                Func<double, double, SKPoint> Map(float offset, bool edges) => (x, y) => new SKPoint(
                    edges && categories > 1
                        ? plot.Left + plot.Width * (float)((x - 1) / (categories - 1))
                        : plot.Left + slot * (float)(x - 1) + offset,
                    ValueYOf(axis, y));

                bool stacked = chart.Grouping != ChartGrouping.Standard;

                void Series(ChartSeries series, int index, (double From, double To)?[] stack, float barOffset, bool edges)
                {
                    var trend = new List<(double X, double Y)>();
                    var errors = new List<(double X, double Y, double Raw)>();
                    for (int c = 0; c < categories; c++)
                    {
                        if (c >= series.Values.Count || series.Values[c] is not double raw || stack[c] is not { } p) continue;
                        trend.Add((c + 1, raw));
                        errors.Add((c + 1, stacked ? p.To : raw, raw));
                    }
                    DrawTrendline(canvas, chart, series, index, trend, Map(slot / 2f, edges), plot, fonts);
                    DrawErrorBars(canvas, series, errors, Map(barOffset, edges), plot);
                }

                if (set.Bars.Count > 0)
                {
                    var (first, pitch, width) = BarLayout(set);
                    for (int b = 0; b < set.Bars.Count; b++)
                    {
                        var (series, index) = set.Bars[b];
                        Series(series, index, set.BarStack[b], (float)(first + (stacked ? 0 : pitch * b) + width / 2.0), false);
                    }
                }
                for (int l = 0; l < set.Lines.Count; l++)
                {
                    var (series, index) = set.Lines[l];
                    Series(series, index, set.LineStack[l], slot / 2f, false);
                }
                for (int a = 0; a < set.Areas.Count; a++)
                {
                    var (series, index) = set.Areas[a];
                    Series(series, index, set.AreaStack[a], slot / 2f, areaAtEdges);
                }
            }

            DrawLineExtras(data, scale);
            DrawLines(data, scale);

            // Ряды вспомогательной оси — поверх рядов основной, как у Excel.
            if (data2 is not null)
            {
                DrawAreas(data2, scale2);
                DrawBars(data2, scale2);
                DrawLineExtras(data2, scale2);
                DrawLines(data2, scale2);
            }

            DrawAnalytics(data, scale);
            if (data2 is not null) DrawAnalytics(data2, scale2);

            if (chart.ShowDataLabels)
            {
                DrawValueLabels(data, scale);
                if (data2 is not null) DrawValueLabels(data2, scale2);
            }

            // Подписи категорий или таблица данных на их месте.
            if (table)
                DrawDataTable(canvas, chart, categories, plot, area.Left, slot, rowHeight, tableKeyWidth, fonts);
            else if (!chart.HideCategoryAxis)
                DrawCategoryLabels(canvas, chart, categories, c => PointX(c, areaAtEdges), slot, plot.Bottom, rotate, area.Height * MaxCategoryLabelShare, fonts);
        }

        private static string BarColor(ChartModel chart, ChartSeries series, int seriesIndex, int pointIndex)
        {
            if (series.PointColors is { } colors && pointIndex < colors.Count && !string.IsNullOrEmpty(colors[pointIndex]))
                return colors[pointIndex]!;
            return chart.SeriesColor(seriesIndex);
        }

        private static double Clamp(double value, ChartAxisScale scale) => Math.Clamp(value, scale.Min, scale.Max);

        // ── Аналитика: линии тренда, планки погрешностей, линии графика ──

        /// <summary>Цвет планок погрешностей и линий минимума-максимума, как у Word.</summary>
        private static readonly SKColor AnalyticsInk = new(0x59, 0x59, 0x59);

        /// <summary>Цвет линий проекции — светлее, как у Word.</summary>
        private static readonly SKColor DropLineInk = new(0xA6, 0xA6, 0xA6);

        /// <summary>
        /// Линия тренда ряда пунктиром из точек цветом ряда: по точкам (X, Y) в единицах
        /// данных, map переводит их на лист. Прогноз вперёд и назад продлевает линию;
        /// уравнение и R² — у её конца.
        /// </summary>
        private static void DrawTrendline(SKCanvas canvas, ChartModel chart, ChartSeries series, int index,
            IReadOnlyList<(double X, double Y)> points, Func<double, double, SKPoint> map, SKRect plot, ChartFonts fonts)
        {
            if (series.Trendline is not { } trendline || points.Count < 2) return;

            using var stroke = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.75f,
                StrokeCap = SKStrokeCap.Round,
                Color = ParseColor(ChartModel.SolidColor(chart.SeriesColor(index))),
                PathEffect = SKPathEffect.CreateDash(new[] { 0.1f, 3.5f }, 0),
                IsAntialias = true
            };

            ChartTrend.TrendFit? fit = null;
            using var path = new SKPath();
            SKPoint? end = null;

            if (trendline.Type == ChartTrendlineType.MovingAverage)
            {
                var average = ChartTrend.MovingAverage(points, trendline.Period);
                for (int i = 0; i < average.Count; i++)
                {
                    var p = map(average[i].X, average[i].Y);
                    if (i == 0) path.MoveTo(p); else path.LineTo(p);
                    end = p;
                }
            }
            else
            {
                fit = ChartTrend.Fit(trendline, points);
                if (fit is null) return;

                double from = points.Min(p => p.X) - Math.Max(0, trendline.Backward);
                double to = points.Max(p => p.X) + Math.Max(0, trendline.Forward);
                const int samples = 120;
                bool started = false;
                for (int i = 0; i <= samples; i++)
                {
                    double x = from + (to - from) * i / samples;
                    double y = fit.Evaluate(x);
                    if (double.IsNaN(y) || double.IsInfinity(y)) { started = false; continue; }
                    var p = map(x, y);
                    if (!started) { path.MoveTo(p); started = true; } else path.LineTo(p);
                    end = p;
                }
            }

            canvas.Save();
            canvas.ClipRect(plot);
            canvas.DrawPath(path, stroke);
            canvas.Restore();

            if (fit is null || end is not { } anchor || (!trendline.ShowEquation && !trendline.ShowRSquared)) return;

            var lines = new List<string>();
            if (trendline.ShowEquation) lines.Add(fit.Equation);
            if (trendline.ShowRSquared) lines.Add(ChartTrend.RSquaredText(fit.RSquared));
            float lineHeight = LineHeight(fonts.Data);
            float widest = lines.Max(l => fonts.Data.MeasureText(l));
            float x0 = Math.Clamp(anchor.X, plot.Left + widest + 2f, plot.Right - 2f);
            float y0 = Math.Clamp(anchor.Y - lineHeight * lines.Count - 4f, plot.Top, plot.Bottom - lineHeight * lines.Count);
            for (int i = 0; i < lines.Count; i++)
                DrawTextCentered(canvas, lines[i], x0, y0 + lineHeight * (i + 0.5f), SKTextAlign.Right, fonts.Data, fonts.DataPaint);
        }

        /// <summary>
        /// Планки погрешностей ряда: у каждой точки (X, Y на листе, Raw — само значение)
        /// линия в обе стороны на величину погрешности и засечки на концах.
        /// </summary>
        private static void DrawErrorBars(SKCanvas canvas, ChartSeries series,
            IReadOnlyList<(double X, double Y, double Raw)> points, Func<double, double, SKPoint> map, SKRect plot)
        {
            if (series.ErrorBars is not { } bars || points.Count == 0) return;

            var raws = points.Select(p => p.Raw).ToList();
            using var ink = new SKPaint { Color = AnalyticsInk, Style = SKPaintStyle.Stroke, StrokeWidth = 0.75f, IsAntialias = true };

            canvas.Save();
            canvas.ClipRect(SKRect.Inflate(plot, 4f, 4f));
            foreach (var (x, y, raw) in points)
            {
                var (center, amount) = ChartTrend.ErrorBar(bars, raws, raw);
                if (amount <= 0 || double.IsNaN(amount)) continue;
                double middle = y + (center - raw);
                var a = map(x, middle - amount);
                var b = map(x, middle + amount);
                canvas.DrawLine(a, b, ink);
                if (bars.NoEndCap) continue;

                const float cap = 3f;
                bool vertical = Math.Abs(a.X - b.X) < Math.Abs(a.Y - b.Y);
                foreach (var p in new[] { a, b })
                {
                    if (vertical) canvas.DrawLine(p.X - cap, p.Y, p.X + cap, p.Y, ink);
                    else canvas.DrawLine(p.X, p.Y - cap, p.X, p.Y + cap, ink);
                }
            }
            canvas.Restore();
        }

        /// <summary>Цвет вспомогательных линий сетки — светлее основных, как у Word.</summary>
        private static readonly SKColor MinorGridColor = new(0xF2, 0xF2, 0xF2);

        /// <summary>
        /// На тёмном фоне сетка рисуется полупрозрачной светлой, а не светло-серой —
        /// иначе она режет глаз ярче самих рядов.
        /// </summary>
        private static bool DarkBackground(ChartModel chart) => chart.HasDarkBackground;

        private static SKPaint GridPaint(ChartModel chart) => new()
        {
            Color = DarkBackground(chart) ? new SKColor(0xFF, 0xFF, 0xFF, 0x40) : GridColor,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = GridLineWidthPt,
            IsAntialias = true
        };

        private static SKPaint MinorGridPaint(ChartModel chart) => new()
        {
            Color = DarkBackground(chart) ? new SKColor(0xFF, 0xFF, 0xFF, 0x1C) : MinorGridColor,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = GridLineWidthPt,
            IsAntialias = true
        };

        /// <summary>Деления вспомогательной сетки: пять на шаг основной, без самих основных.</summary>
        private static IEnumerable<double> MinorTicks(ChartAxisScale scale)
        {
            if (scale.Step <= 0 || scale.Max <= scale.Min) yield break;
            double minor = scale.Step / 5.0;
            int count = (int)Math.Round((scale.Max - scale.Min) / minor);
            if (count > 1000) yield break;
            for (int i = 1; i < count; i++)
            {
                if (i % 5 == 0) continue;
                yield return scale.Min + i * minor;
            }
        }

        /// <summary>
        /// Подпись значения у точки (график, точечная, пузырьковая) по месту: сверху,
        /// снизу, слева, справа или в центре; Auto — место по умолчанию fallback.
        /// </summary>
        private static void DrawPointLabel(SKCanvas canvas, string text, float x, float y, ChartLabelPosition position,
            ChartLabelPosition fallback, float lineHeight, float markerSize, ChartFonts fonts)
        {
            if (position == ChartLabelPosition.Auto) position = fallback;
            float half = markerSize / 2f;
            switch (position)
            {
                case ChartLabelPosition.Below:
                    DrawTextCentered(canvas, text, x, y + lineHeight / 2f + half, SKTextAlign.Center, fonts.Data, fonts.DataPaint);
                    break;
                case ChartLabelPosition.Left:
                    DrawTextCentered(canvas, text, x - half - 3f, y, SKTextAlign.Right, fonts.Data, fonts.DataPaint);
                    break;
                case ChartLabelPosition.Right:
                    DrawTextCentered(canvas, text, x + half + 3f, y, SKTextAlign.Left, fonts.Data, fonts.DataPaint);
                    break;
                case ChartLabelPosition.Center:
                    DrawTextCentered(canvas, text, x, y, SKTextAlign.Center, fonts.Data, fonts.DataPaint);
                    break;
                default:
                    DrawTextCentered(canvas, text, x, y - lineHeight / 2f - half, SKTextAlign.Center, fonts.Data, fonts.DataPaint);
                    break;
            }
        }

        /// <summary>
        /// Таблица данных под областью построения, как у Word: строка категорий, под ней
        /// по строке на ряд — ключ легенды, имя ряда и значения в столбцах категорий.
        /// </summary>
        private static void DrawDataTable(SKCanvas canvas, ChartModel chart, int categories, SKRect plot, float left,
            float slot, float rowHeight, float keyWidth, ChartFonts fonts)
        {
            var font = fonts.Label;
            int rows = 1 + chart.Series.Count;
            float top = plot.Bottom;
            float bottom = top + rowHeight * rows;

            using var lines = GridPaint(chart);

            // Строка категорий начинается у области построения, строки рядов — от левого
            // края таблицы, где стоят их имена.
            canvas.DrawLine(plot.Left, top, plot.Right, top, lines);
            for (int r = 1; r <= rows; r++)
            {
                float y = top + rowHeight * r;
                canvas.DrawLine(left, y, plot.Right, y, lines);
            }
            for (int c = 0; c <= categories; c++)
            {
                float x = plot.Left + slot * c;
                canvas.DrawLine(x, top, x, bottom, lines);
            }
            canvas.DrawLine(left, top + rowHeight, left, bottom, lines);

            for (int c = 0; c < categories; c++)
            {
                string label = Fit(chart.CategoryLabel(c), font, slot - 4f);
                DrawTextCentered(canvas, label, plot.Left + slot * (c + 0.5f), top + rowHeight / 2f, SKTextAlign.Center, font, fonts.Paint);
            }

            float nameWidth = plot.Left - left - keyWidth - 6f;
            for (int s = 0; s < chart.Series.Count; s++)
            {
                var series = chart.Series[s];
                float centerY = top + rowHeight * (s + 1.5f);
                float x = left + 3f;
                if (keyWidth > 0f)
                {
                    var key = new SKRect(x, centerY - LegendSymbolPt / 2f, x + LegendSymbolPt, centerY + LegendSymbolPt / 2f);
                    using var fill = FillPaint(chart.SeriesColor(s), key);
                    canvas.DrawRect(key, fill);
                    x += keyWidth;
                }
                DrawTextCentered(canvas, Fit(series.Name, font, Math.Max(4f, nameWidth)), x, centerY, SKTextAlign.Left, font, fonts.Paint);

                for (int c = 0; c < categories; c++)
                {
                    if (c >= series.Values.Count || series.Values[c] is not double value) continue;
                    string text = Fit(FormatDataLabel(chart, value), font, slot - 4f);
                    DrawTextCentered(canvas, text, plot.Left + slot * (c + 0.5f), centerY, SKTextAlign.Center, font, fonts.Paint);
                }
            }
        }

        private static void DrawCategoryLabels(
            SKCanvas canvas, ChartModel chart, int categories, Func<int, float> centerX, float slot,
            float top, bool rotate, float maxLength, ChartFonts fonts)
        {
            var font = fonts.Label;
            float lineHeight = LineHeight(font);

            if (!rotate)
            {
                for (int c = 0; c < categories; c++)
                {
                    string label = Fit(chart.CategoryLabel(c), font, slot);
                    DrawTextCentered(canvas, label, centerX(c), top + 3f + lineHeight / 2f, SKTextAlign.Center, font, fonts.Paint);
                }
                return;
            }

            // Наклонные подписи: конец подписи у своей категории, текст уходит влево вниз.
            // Если слоты уже строки, подписывается каждая n-я категория.
            int every = Math.Max(1, (int)Math.Ceiling(lineHeight * 1.2f / Math.Max(slot, 0.1f) * 0.7071f));
            for (int c = 0; c < categories; c += every)
            {
                string label = Fit(chart.CategoryLabel(c), font, maxLength);
                canvas.Save();
                canvas.Translate(centerX(c), top + 3f);
                canvas.RotateDegrees(-45f);
                DrawTextCentered(canvas, label, 0f, 0f, SKTextAlign.Right, font, fonts.Paint);
                canvas.Restore();
            }
        }

        /// <summary>Линия ряда по точкам; пустые точки рвут линию, как у Excel.</summary>
        /// <summary>
        /// Линия ряда с маркерами. Градиент ряда натягивается на bounds — область
        /// построения: цвет линии меняется вдоль неё так же, как в образце подборщика.
        /// </summary>
        private static void DrawSeriesLine(SKCanvas canvas, List<SKPoint?> points, string color, SKRect bounds, bool showLine, bool smooth, bool showMarkers)
        {
            if (showLine)
            {
                using var stroke = new SKPaint
                {
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = SeriesLineWidthPt,
                    StrokeCap = SKStrokeCap.Round,
                    StrokeJoin = SKStrokeJoin.Round,
                    IsAntialias = true
                };
                ApplyFill(stroke, color, bounds);

                var run = new List<SKPoint>();
                void Flush()
                {
                    if (run.Count >= 2)
                    {
                        using var path = smooth ? SmoothPath(run) : PolylinePath(run);
                        canvas.DrawPath(path, stroke);
                    }
                    run.Clear();
                }

                foreach (var point in points)
                {
                    if (point is { } p) run.Add(p);
                    else Flush();
                }
                Flush();
            }

            if (showMarkers || !showLine)
            {
                using var fill = FillPaint(color, bounds);
                foreach (var point in points)
                    if (point is { } p) canvas.DrawCircle(p.X, p.Y, MarkerSizePt / 2f, fill);
            }
        }

        private static SKPath PolylinePath(List<SKPoint> points)
        {
            var path = new SKPath();
            path.MoveTo(points[0]);
            for (int i = 1; i < points.Count; i++) path.LineTo(points[i]);
            return path;
        }

        /// <summary>Сглаженная линия: кривые Безье по Катмуллу — Рому через все точки.</summary>
        private static SKPath SmoothPath(List<SKPoint> points)
        {
            var path = new SKPath();
            path.MoveTo(points[0]);
            for (int i = 0; i < points.Count - 1; i++)
            {
                var p0 = i > 0 ? points[i - 1] : points[i];
                var p1 = points[i];
                var p2 = points[i + 1];
                var p3 = i + 2 < points.Count ? points[i + 2] : p2;

                var c1 = new SKPoint(p1.X + (p2.X - p0.X) / 6f, p1.Y + (p2.Y - p0.Y) / 6f);
                var c2 = new SKPoint(p2.X - (p3.X - p1.X) / 6f, p2.Y - (p3.Y - p1.Y) / 6f);
                path.CubicTo(c1, c2, p2);
            }
            return path;
        }

        // ── Линейчатая ───────────────────────────────────────────────────

        private static void DrawBarChart(SKCanvas canvas, ChartModel chart, SKRect area, ChartFonts fonts)
        {
            int categories = chart.CategoryCount;
            if (categories == 0) categories = 1;

            // У линейчатой категории идут по вертикали, значения — по горизонтали.
            area = ReserveAxisTitles(canvas, area, chart.ValueAxisTitle, chart.CategoryAxisTitle, fonts);

            var data = CollectCategoryData(chart, categories, horizontalBars: true);
            bool percent = chart.Grouping == ChartGrouping.PercentStacked;
            var font = fonts.Label;
            float lineHeight = LineHeight(font);

            float categoryLabelWidth = 0f;
            if (!chart.HideCategoryAxis)
            {
                for (int c = 0; c < categories; c++)
                    categoryLabelWidth = Math.Max(categoryLabelWidth, font.MeasureText(chart.CategoryLabel(c)));
                categoryLabelWidth = Math.Min(categoryLabelWidth, area.Width * MaxCategoryLabelShare) + 4f;
            }

            float valueLabelHeight = chart.HideValueAxis ? 0f : lineHeight + 3f;
            float plotWidth = area.Width - categoryLabelWidth;
            var scale = ValueScale(chart, data, plotWidth, forceZero: true);

            // Крайние подписи делений выходят за область наполовину своей ширины.
            float lastLabelHalf = chart.HideValueAxis ? 0f : font.MeasureText(FormatTick(chart, scale.Max, scale, percent)) / 2f;

            var plot = new SKRect(area.Left + categoryLabelWidth, area.Top, area.Right - lastLabelHalf, area.Bottom - valueLabelHeight);
            if (plot.Width <= 2f || plot.Height <= 2f) return;

            float ValueX(double value) => plot.Left + (float)(scale.Fraction(value) * plot.Width);
            float slot = plot.Height / categories;

            // Первая категория — внизу, как у Excel.
            float SlotTop(int category) => plot.Bottom - slot * (category + 1);

            FillPlotArea(canvas, chart, plot);

            using (var grid = GridPaint(chart))
            using (var minorGrid = MinorGridPaint(chart))
            {
                if (chart.ShowMinorGridlines)
                    foreach (var tick in MinorTicks(scale))
                    {
                        float x = ValueX(tick);
                        canvas.DrawLine(x, plot.Top, x, plot.Bottom, minorGrid);
                    }
                if (chart.ShowCategoryMinorGridlines)
                    for (int c = 0; c < categories; c++)
                    {
                        float y = SlotTop(c) + slot / 2f;
                        canvas.DrawLine(plot.Left, y, plot.Right, y, minorGrid);
                    }
                if (chart.ShowCategoryGridlines)
                    for (int c = 0; c <= categories; c++)
                    {
                        float y = plot.Bottom - slot * c;
                        canvas.DrawLine(plot.Left, y, plot.Right, y, grid);
                    }

                foreach (var tick in scale.Ticks())
                {
                    float x = ValueX(tick);
                    if (chart.ShowGridlines) canvas.DrawLine(x, plot.Top, x, plot.Bottom, grid);
                    if (!chart.HideValueAxis)
                        DrawTextCentered(canvas, FormatTick(chart, tick, scale, percent), x, plot.Bottom + 3f + lineHeight / 2f, SKTextAlign.Center, font, fonts.Paint);
                }
            }

            bool stacked = chart.Grouping != ChartGrouping.Standard;
            int barsInCluster = stacked ? 1 : data.Bars.Count;
            double overlap = stacked ? 1.0 : chart.OverlapPercent / 100.0;
            var (first, pitch, width) = ChartScale.BarSlots(slot, barsInCluster, chart.GapWidthPercent / 100.0, overlap);
            var labelPosition = chart.LabelPositionFor(ChartKind.Bar);
            if (labelPosition == ChartLabelPosition.Auto) labelPosition = stacked ? ChartLabelPosition.Center : ChartLabelPosition.OutsideEnd;

            for (int b = 0; b < data.Bars.Count; b++)
            {
                var (series, index) = data.Bars[b];
                var points = data.BarStack[b];
                for (int c = 0; c < categories; c++)
                {
                    if (points[c] is not { } p) continue;
                    // Первый ряд — внизу группы.
                    float bottom = SlotTop(c) + slot - (float)(first + (stacked ? 0 : pitch * b));
                    float top = bottom - (float)width;
                    float x1 = ValueX(Clamp(p.From, scale));
                    float x2 = ValueX(Clamp(p.To, scale));
                    var barRect = new SKRect(Math.Min(x1, x2), top, Math.Max(x1, x2), bottom);
                    using var fill = FillPaint(BarColor(chart, series, index, c), barRect);
                    canvas.DrawRect(barRect, fill);

                    if (chart.ShowDataLabels && c < series.Values.Count && series.Values[c] is double raw)
                    {
                        string label = FormatDataLabel(chart, raw);
                        float centerY = (top + bottom) / 2f;
                        bool right = x2 >= x1;
                        switch (labelPosition)
                        {
                            case ChartLabelPosition.Center:
                                DrawTextCentered(canvas, label, (x1 + x2) / 2f, centerY, SKTextAlign.Center, fonts.Data, fonts.DataPaint);
                                break;
                            case ChartLabelPosition.InsideEnd:
                                DrawTextCentered(canvas, label, right ? x2 - 3f : x2 + 3f, centerY, right ? SKTextAlign.Right : SKTextAlign.Left, fonts.Data, fonts.DataPaint);
                                break;
                            case ChartLabelPosition.InsideBase:
                                DrawTextCentered(canvas, label, right ? x1 + 3f : x1 - 3f, centerY, right ? SKTextAlign.Left : SKTextAlign.Right, fonts.Data, fonts.DataPaint);
                                break;
                            default:
                                DrawTextCentered(canvas, label, right ? x2 + 3f : x2 - 3f, centerY, right ? SKTextAlign.Left : SKTextAlign.Right, fonts.Data, fonts.DataPaint);
                                break;
                        }
                    }
                }
            }

            float axisX = scale.Min <= 0 && scale.Max >= 0 ? ValueX(0) : plot.Left;
            using (var axis = new SKPaint { Color = AxisLineColor, Style = SKPaintStyle.Stroke, StrokeWidth = GridLineWidthPt, IsAntialias = true })
                canvas.DrawLine(axisX, plot.Top, axisX, plot.Bottom, axis);

            // Линии тренда и планки погрешностей: X — номер категории снизу вверх,
            // значения идут по горизонтали.
            Func<double, double, SKPoint> BarMap(float fromBottom) => (x, y) => new SKPoint(ValueX(y), plot.Bottom - slot * (float)(x - 1) - fromBottom);
            for (int b = 0; b < data.Bars.Count; b++)
            {
                var (series, index) = data.Bars[b];
                var stack = data.BarStack[b];
                var trend = new List<(double X, double Y)>();
                var errors = new List<(double X, double Y, double Raw)>();
                for (int c = 0; c < categories; c++)
                {
                    if (c >= series.Values.Count || series.Values[c] is not double raw || stack[c] is not { } p) continue;
                    trend.Add((c + 1, raw));
                    errors.Add((c + 1, stacked ? p.To : raw, raw));
                }
                DrawTrendline(canvas, chart, series, index, trend, BarMap(slot / 2f), plot, fonts);
                DrawErrorBars(canvas, series, errors, BarMap((float)(first + (stacked ? 0 : pitch * b) + width / 2.0)), plot);
            }

            if (!chart.HideCategoryAxis)
            {
                for (int c = 0; c < categories; c++)
                {
                    string label = Fit(chart.CategoryLabel(c), font, categoryLabelWidth - 4f);
                    DrawTextCentered(canvas, label, plot.Left - 4f, SlotTop(c) + slot / 2f, SKTextAlign.Right, font, fonts.Paint);
                }
            }
        }

        // ── Точечная ─────────────────────────────────────────────────────

        private static void DrawScatter(SKCanvas canvas, ChartModel chart, SKRect area, ChartFonts fonts)
        {
            area = ReserveAxisTitles(canvas, area, chart.CategoryAxisTitle, chart.ValueAxisTitle, fonts);

            double xMin = double.PositiveInfinity, xMax = double.NegativeInfinity;
            double yMin = double.PositiveInfinity, yMax = double.NegativeInfinity;

            foreach (var series in chart.Series)
            {
                for (int i = 0; i < series.Values.Count; i++)
                {
                    if (series.Values[i] is not double y || double.IsNaN(y) || double.IsInfinity(y)) continue;
                    double x = XOf(series, i);
                    if (double.IsNaN(x) || double.IsInfinity(x)) continue;
                    xMin = Math.Min(xMin, x); xMax = Math.Max(xMax, x);
                    yMin = Math.Min(yMin, y); yMax = Math.Max(yMax, y);
                }
            }

            if (double.IsInfinity(xMin)) { xMin = 0; xMax = 1; }
            if (double.IsInfinity(yMin)) { yMin = 0; yMax = 1; }

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
            var labelPosition = chart.LabelPositionFor(ChartKind.Scatter);
            float dataLine = LineHeight(fonts.Data);

            for (int s = 0; s < chart.Series.Count; s++)
            {
                var series = chart.Series[s];
                var placed = new List<SKPoint?>(series.Values.Count);
                for (int i = 0; i < series.Values.Count; i++)
                {
                    if (series.Values[i] is double y && !double.IsNaN(y) && !double.IsInfinity(y))
                    {
                        double x = XOf(series, i);
                        placed.Add(new SKPoint(X(Math.Clamp(x, xScale.Min, xScale.Max)), Y(Math.Clamp(y, yScale.Min, yScale.Max))));
                    }
                    else
                    {
                        placed.Add(null);
                    }
                }

                DrawSeriesLine(canvas, placed, chart.SeriesColor(s), plot, series.ShowLine, series.Smooth, series.ShowMarkers);

                var trend = new List<(double X, double Y)>();
                for (int i = 0; i < series.Values.Count; i++)
                    if (series.Values[i] is double yv && !double.IsNaN(yv) && !double.IsInfinity(yv)) trend.Add((XOf(series, i), yv));
                SKPoint MapXY(double xv, double yv) => new(X(xv), Y(yv));
                DrawTrendline(canvas, chart, series, s, trend, MapXY, plot, fonts);
                DrawErrorBars(canvas, series, trend.Select(p => (p.X, p.Y, p.Y)).ToList(), MapXY, plot);

                if (chart.ShowDataLabels)
                {
                    for (int i = 0; i < placed.Count; i++)
                        if (placed[i] is { } p && series.Values[i] is double raw)
                            DrawPointLabel(canvas, FormatDataLabel(chart, raw), p.X, p.Y, labelPosition, ChartLabelPosition.Above, dataLine, MarkerSizePt, fonts);
                }
            }
        }

        /// <summary>X точки точечной диаграммы: свой или номер точки, как у Excel без X.</summary>
        private static double XOf(ChartSeries series, int index)
        {
            if (series.XValues is { } xs && index < xs.Count && xs[index] is double x) return x;
            return index + 1;
        }

        // ── Круговая и кольцевая ─────────────────────────────────────────

        private static void DrawPie(SKCanvas canvas, ChartModel chart, SKRect area, ChartFonts fonts)
        {
            if (chart.Series.Count == 0) return;

            bool doughnut = chart.Kind == ChartKind.Doughnut;
            var labelPosition = doughnut ? ChartLabelPosition.Auto : chart.LabelPositionFor(ChartKind.Pie);
            bool outsideLabels = chart.ShowDataLabels && labelPosition == ChartLabelPosition.OutsideEnd;
            float dataLine = LineHeight(fonts.Data);

            // Подписи снаружи — круг меньше на их место.
            float labelRoom = 0f;
            if (outsideLabels)
            {
                float widest = 0f;
                foreach (var value in chart.Series[0].Values)
                    if (value is double v) widest = Math.Max(widest, fonts.Data.MeasureText(FormatDataLabel(chart, v)));
                labelRoom = Math.Max(dataLine, widest) + 4f;
            }

            float radius = Math.Min(area.Width, area.Height) / 2f - 2f - labelRoom;
            if (radius <= 2f) return;
            var center = new SKPoint(area.MidX, area.MidY);

            var rings = doughnut ? chart.Series : new List<ChartSeries> { chart.Series[0] };
            float hole = doughnut ? radius * Math.Clamp(chart.HoleSizePercent, 10, 90) / 100f : 0f;
            float ringWidth = (radius - hole) / Math.Max(1, rings.Count);

            var font = fonts.Label;

            using var separator = new SKPaint
            {
                Color = SKColors.White,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 0.75f,
                IsAntialias = true,
                StrokeJoin = SKStrokeJoin.Round
            };

            for (int r = 0; r < rings.Count; r++)
            {
                var series = rings[r];
                double total = 0;
                foreach (var value in series.Values)
                    if (value is double v && v > 0 && !double.IsInfinity(v)) total += v;
                if (total <= 0) continue;

                float outer = radius - ringWidth * r;
                float inner = doughnut ? outer - ringWidth : 0f;
                var outerRect = new SKRect(center.X - outer, center.Y - outer, center.X + outer, center.Y + outer);
                var innerRect = new SKRect(center.X - inner, center.Y - inner, center.X + inner, center.Y + inner);

                float angle = -90f + chart.FirstSliceAngle;
                for (int i = 0; i < series.Values.Count; i++)
                {
                    if (series.Values[i] is not double value || value <= 0 || double.IsInfinity(value)) continue;
                    float sweep = (float)(value / total * 360.0);

                    using var path = new SKPath();
                    if (inner > 0f)
                    {
                        path.ArcTo(outerRect, angle, sweep, true);
                        path.ArcTo(innerRect, angle + sweep, -sweep, false);
                        path.Close();
                    }
                    else if (sweep >= 359.99f)
                    {
                        path.AddOval(outerRect);
                    }
                    else
                    {
                        path.MoveTo(center);
                        path.ArcTo(outerRect, angle, sweep, false);
                        path.Close();
                    }

                    // Градиент доли — по кругу всей диаграммы: радиальный расходится из
                    // центра, конический идёт по кругу, как в образце подборщика.
                    using var fill = FillPaint(chart.PointColor(series, i), outerRect);
                    canvas.DrawPath(path, fill);
                    canvas.DrawPath(path, separator);

                    if (chart.ShowDataLabels)
                    {
                        float mid = (angle + sweep / 2f) * MathF.PI / 180f;
                        string text = FormatDataLabel(chart, value);
                        float textWidth = fonts.Data.MeasureText(text);
                        float labelRadius = labelPosition switch
                        {
                            ChartLabelPosition.Center => outer * 0.5f,
                            ChartLabelPosition.InsideEnd => Math.Max(outer * 0.3f, outer - Math.Max(dataLine, textWidth / 2f) - 3f),
                            ChartLabelPosition.OutsideEnd => outer + Math.Max(dataLine / 2f, textWidth / 2f) + 3f,
                            _ => inner > 0f ? (inner + outer) / 2f : outer * 0.65f
                        };
                        float lx = center.X + MathF.Cos(mid) * labelRadius;
                        float ly = center.Y + MathF.Sin(mid) * labelRadius;
                        using var labelPaint = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Fill };
                        bool outside = labelPosition == ChartLabelPosition.OutsideEnd;
                        DrawTextCentered(canvas, text, lx, ly, SKTextAlign.Center, fonts.Data, fonts.DataColored || outside ? fonts.DataPaint : labelPaint);
                    }

                    angle += sweep;
                }
            }
        }
    }
}
