using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Writersword.Core.Models.Project;
using Writersword.Modules.TextEditor.Models.Document;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Диаграмма для .docx: XML части диаграммы (c:chartSpace) по модели и книга Excel с
    /// её данными. Книга нужна Word, чтобы по «Изменить данные» открыть таблицу
    /// диаграммы в Excel; на самом листе Word рисует диаграмму по кэшу значений в XML.
    /// </summary>
    internal static class DocxChartWriter
    {
        public const string ChartContentType = "application/vnd.openxmlformats-officedocument.drawingml.chart+xml";
        public const string WorkbookContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

        private const string SheetName = "Sheet1";
        private const string TextColor = "595959";
        private const string LineColor = "D9D9D9";

        private const string CategoryAxisId = "507000001";
        private const string ValueAxisId = "507000002";
        private const string SecondaryCategoryAxisId = "507000003";
        private const string SecondaryValueAxisId = "507000004";

        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        // ── XML диаграммы ────────────────────────────────────────────────

        /// <summary>
        /// XML части диаграммы. workbookRelationshipId — связь с книгой данных; null —
        /// книги нет, и c:externalData не пишется.
        /// </summary>
        public static string BuildChartXml(ChartModel chart, string? workbookRelationshipId, string? backgroundImageRelationshipId = null)
        {
            var xml = new StringBuilder(4096);
            xml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            xml.Append("<c:chartSpace xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\"");
            xml.Append(" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"");
            xml.Append(" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
            xml.Append("<c:date1904 val=\"0\"/><c:lang val=\"ru-RU\"/><c:roundedCorners val=\"0\"/>");

            xml.Append("<c:chart>");

            string? title = chart.DisplayTitle;
            if (title is not null)
            {
                xml.Append("<c:title>");
                AppendRichText(xml, title, chart.TitleStyle, ChartTextRole.Title, rotation: null);
                xml.Append("<c:overlay val=\"0\"/></c:title>");
                xml.Append("<c:autoTitleDeleted val=\"0\"/>");
            }
            else
            {
                xml.Append("<c:autoTitleDeleted val=\"1\"/>");
            }

            xml.Append("<c:plotArea><c:layout/>");
            AppendGroups(xml, chart);
            if (chart.HasAxes) AppendAxes(xml, chart);
            if (chart.DataTableVisible) AppendDataTable(xml, chart);
            xml.Append("<c:spPr>");
            if (string.IsNullOrEmpty(chart.PlotAreaFill)) xml.Append("<a:noFill/>");
            else AppendSolid(xml, chart.PlotAreaFill!);
            xml.Append("<a:ln><a:noFill/></a:ln></c:spPr>");
            xml.Append("</c:plotArea>");

            if (chart.Legend != ChartLegendPosition.None)
            {
                string position = chart.Legend switch
                {
                    ChartLegendPosition.Top => "t",
                    ChartLegendPosition.Left => "l",
                    ChartLegendPosition.Right => "r",
                    _ => "b"
                };
                xml.Append("<c:legend><c:legendPos val=\"").Append(position).Append("\"/><c:overlay val=\"0\"/>");
                AppendTextProperties(xml, chart.LegendStyle, ChartTextRole.Legend, TextColor);
                xml.Append("</c:legend>");
            }

            xml.Append("<c:plotVisOnly val=\"1\"/><c:dispBlanksAs val=\"gap\"/>");
            xml.Append("</c:chart>");

            // Вся диаграмма: заливка (белая, как у новой диаграммы Word) или картинка, и
            // тонкая серая рамка.
            xml.Append("<c:spPr>");
            if (backgroundImageRelationshipId is not null)
                xml.Append("<a:blipFill rotWithShape=\"1\"><a:blip r:embed=\"").Append(Escape(backgroundImageRelationshipId))
                    .Append("\"/><a:stretch><a:fillRect/></a:stretch></a:blipFill>");
            else if (chart.BackgroundFill is { } background && !ChartModel.IsGradient(background) && ChartModel.ParseArgb(background).A == 0)
                xml.Append("<a:noFill/>");
            else
                AppendSolid(xml, chart.BackgroundFill ?? "FFFFFF");
            if (chart.ShowBorder)
                xml.Append("<a:ln w=\"9525\" cap=\"flat\" cmpd=\"sng\" algn=\"ctr\"><a:solidFill><a:srgbClr val=\"")
                    .Append(LineColor).Append("\"/></a:solidFill><a:round/></a:ln>");
            else
                xml.Append("<a:ln><a:noFill/></a:ln>");
            xml.Append("</c:spPr>");

            xml.Append("<c:txPr><a:bodyPr/><a:lstStyle/><a:p><a:pPr><a:defRPr sz=\"900\">");
            AppendSolid(xml, TextColor);
            if (!string.IsNullOrWhiteSpace(chart.FontFamily))
            {
                string family = Escape(chart.FontFamily!);
                xml.Append("<a:latin typeface=\"").Append(family).Append("\"/>");
                xml.Append("<a:cs typeface=\"").Append(family).Append("\"/>");
            }
            xml.Append("</a:defRPr></a:pPr><a:endParaRPr lang=\"ru-RU\"/></a:p></c:txPr>");

            if (workbookRelationshipId is not null)
                xml.Append("<c:externalData r:id=\"").Append(Escape(workbookRelationshipId))
                    .Append("\"><c:autoUpdate val=\"0\"/></c:externalData>");

            xml.Append("</c:chartSpace>");
            return xml.ToString();
        }

        /// <summary>
        /// Группы рядов. У смешанной диаграммы ряды своего вида уходят своей группой на
        /// те же оси: области, затем столбцы, затем линии — линии поверх столбцов.
        /// </summary>
        private static void AppendGroups(StringBuilder xml, ChartModel chart)
        {
            if (!chart.HasAxes)
            {
                AppendGroup(xml, chart, chart.Kind, AllSeries(chart));
                return;
            }

            if (chart.Kind is ChartKind.Scatter or ChartKind.Bubble or ChartKind.Radar)
            {
                AppendGroup(xml, chart, chart.Kind, AllSeries(chart));
                return;
            }

            // Биржевая у Word — ровно три или четыре ряда. С меньшим числом рядов она
            // уходит графиком, лишние ряды не пишутся.
            if (chart.Kind == ChartKind.Stock)
            {
                var all = AllSeries(chart);
                if (all.Count >= 3)
                    AppendGroup(xml, chart, ChartKind.Stock, all.Take(4).ToList());
                else
                    AppendGroup(xml, chart, ChartKind.Line, all);
                return;
            }

            // Со вспомогательной осью ряды на ней уходят своими группами на вторую пару
            // осей — после групп основной, как пишет Word.
            var order = new[] { ChartKind.Area, ChartKind.Column, ChartKind.Bar, ChartKind.Line };
            bool dual = chart.HasSecondaryAxis;
            foreach (bool secondary in dual ? new[] { false, true } : new[] { false })
            {
                foreach (var kind in order)
                {
                    var members = new List<(ChartSeries Series, int Index)>();
                    for (int i = 0; i < chart.Series.Count; i++)
                    {
                        if (dual && chart.IsOnSecondaryAxis(chart.Series[i]) != secondary) continue;
                        var own = NormalizedKind(chart, chart.Series[i]);
                        if (own == kind) members.Add((chart.Series[i], i));
                    }
                    if (members.Count > 0) AppendGroup(xml, chart, kind, members, secondary);
                }
            }
        }

        /// <summary>
        /// Вид ряда для записи: у осевой диаграммы ряд может быть только осевым. Свой
        /// круговой или точечный вид ряда в такой диаграмме Word не понял бы.
        /// </summary>
        private static ChartKind NormalizedKind(ChartModel chart, ChartSeries series)
        {
            var kind = chart.KindOf(series);
            if (kind is ChartKind.Pie or ChartKind.Doughnut or ChartKind.Scatter
                or ChartKind.Bubble or ChartKind.Radar or ChartKind.Stock) return chart.Kind;
            if (kind == ChartKind.Bar && chart.Kind != ChartKind.Bar) return ChartKind.Column;
            if (kind == ChartKind.Column && chart.Kind == ChartKind.Bar) return ChartKind.Bar;
            return kind;
        }

        private static List<(ChartSeries Series, int Index)> AllSeries(ChartModel chart)
        {
            var list = new List<(ChartSeries, int)>(chart.Series.Count);
            for (int i = 0; i < chart.Series.Count; i++) list.Add((chart.Series[i], i));
            return list;
        }

        private static string GroupingValue(ChartModel chart, ChartKind kind)
        {
            return chart.Grouping switch
            {
                ChartGrouping.Stacked => "stacked",
                ChartGrouping.PercentStacked => "percentStacked",
                _ => kind is ChartKind.Column or ChartKind.Bar ? "clustered" : "standard"
            };
        }

        private static void AppendGroup(
            StringBuilder xml, ChartModel chart, ChartKind kind, List<(ChartSeries Series, int Index)> members,
            bool secondary = false)
        {
            switch (kind)
            {
                case ChartKind.Column:
                case ChartKind.Bar:
                    xml.Append("<c:barChart><c:barDir val=\"").Append(kind == ChartKind.Bar ? "bar" : "col").Append("\"/>");
                    xml.Append("<c:grouping val=\"").Append(GroupingValue(chart, kind)).Append("\"/>");
                    xml.Append("<c:varyColors val=\"0\"/>");
                    foreach (var (series, index) in members) AppendSeries(xml, chart, kind, series, index);
                    AppendGroupLabels(xml, chart, kind);
                    xml.Append("<c:gapWidth val=\"").Append(Math.Clamp(chart.GapWidthPercent, 0, 500).ToString(Invariant)).Append("\"/>");
                    int overlap = chart.Grouping == ChartGrouping.Standard
                        ? Math.Clamp(chart.OverlapPercent, -100, 100)
                        : 100;
                    xml.Append("<c:overlap val=\"").Append(overlap.ToString(Invariant)).Append("\"/>");
                    AppendAxisIds(xml, secondary);
                    xml.Append("</c:barChart>");
                    break;

                case ChartKind.Line:
                    xml.Append("<c:lineChart>");
                    xml.Append("<c:grouping val=\"").Append(GroupingValue(chart, kind)).Append("\"/>");
                    xml.Append("<c:varyColors val=\"0\"/>");
                    foreach (var (series, index) in members) AppendSeries(xml, chart, kind, series, index);
                    AppendGroupLabels(xml, chart, kind);
                    if (chart.ShowDropLines) AppendChartLines(xml, "dropLines", "A6A6A6");
                    if (chart.ShowHighLowLines && members.Count >= 2) AppendChartLines(xml, "hiLowLines", "595959");
                    if (chart.ShowUpDownBars && members.Count >= 2) AppendUpDownBars(xml);
                    xml.Append("<c:marker val=\"1\"/>");
                    AppendAxisIds(xml, secondary);
                    xml.Append("</c:lineChart>");
                    break;

                case ChartKind.Area:
                    xml.Append("<c:areaChart>");
                    xml.Append("<c:grouping val=\"").Append(GroupingValue(chart, kind)).Append("\"/>");
                    xml.Append("<c:varyColors val=\"0\"/>");
                    foreach (var (series, index) in members) AppendSeries(xml, chart, kind, series, index);
                    AppendGroupLabels(xml, chart, kind);
                    if (chart.ShowDropLines) AppendChartLines(xml, "dropLines", "A6A6A6");
                    AppendAxisIds(xml, secondary);
                    xml.Append("</c:areaChart>");
                    break;

                case ChartKind.Pie:
                    xml.Append("<c:pieChart><c:varyColors val=\"1\"/>");
                    foreach (var (series, index) in members) AppendSeries(xml, chart, kind, series, index);
                    AppendGroupLabels(xml, chart, kind);
                    xml.Append("<c:firstSliceAng val=\"").Append(Math.Clamp(chart.FirstSliceAngle, 0, 360).ToString(Invariant)).Append("\"/>");
                    xml.Append("</c:pieChart>");
                    break;

                case ChartKind.Doughnut:
                    xml.Append("<c:doughnutChart><c:varyColors val=\"1\"/>");
                    foreach (var (series, index) in members) AppendSeries(xml, chart, kind, series, index);
                    AppendGroupLabels(xml, chart, kind);
                    xml.Append("<c:firstSliceAng val=\"").Append(Math.Clamp(chart.FirstSliceAngle, 0, 360).ToString(Invariant)).Append("\"/>");
                    xml.Append("<c:holeSize val=\"").Append(Math.Clamp(chart.HoleSizePercent, 10, 90).ToString(Invariant)).Append("\"/>");
                    xml.Append("</c:doughnutChart>");
                    break;

                case ChartKind.Radar:
                    xml.Append("<c:radarChart><c:radarStyle val=\"").Append(chart.RadarFilled ? "filled" : "marker").Append("\"/>");
                    xml.Append("<c:varyColors val=\"0\"/>");
                    foreach (var (series, index) in members) AppendSeries(xml, chart, kind, series, index);
                    AppendGroupLabels(xml, chart, kind);
                    AppendAxisIds(xml, secondary);
                    xml.Append("</c:radarChart>");
                    break;

                case ChartKind.Bubble:
                    xml.Append("<c:bubbleChart><c:varyColors val=\"0\"/>");
                    foreach (var (series, index) in members) AppendSeries(xml, chart, kind, series, index);
                    AppendGroupLabels(xml, chart, kind);
                    xml.Append("<c:bubbleScale val=\"100\"/><c:showNegBubbles val=\"0\"/>");
                    AppendAxisIds(xml, secondary);
                    xml.Append("</c:bubbleChart>");
                    break;

                case ChartKind.Stock:
                    xml.Append("<c:stockChart>");
                    foreach (var (series, index) in members) AppendSeries(xml, chart, kind, series, index);
                    AppendGroupLabels(xml, chart, kind);
                    xml.Append("<c:hiLowLines><c:spPr><a:ln w=\"9525\" cap=\"flat\" cmpd=\"sng\" algn=\"ctr\">");
                    AppendSolid(xml, "404040");
                    xml.Append("<a:round/></a:ln></c:spPr></c:hiLowLines>");
                    if (members.Count >= 4)
                    {
                        xml.Append("<c:upDownBars><c:gapWidth val=\"150\"/><c:upBars><c:spPr>");
                        AppendSolid(xml, "FFFFFF");
                        xml.Append("<a:ln w=\"9525\">");
                        AppendSolid(xml, "404040");
                        xml.Append("</a:ln></c:spPr></c:upBars><c:downBars><c:spPr>");
                        AppendSolid(xml, "404040");
                        xml.Append("<a:ln w=\"9525\">");
                        AppendSolid(xml, "404040");
                        xml.Append("</a:ln></c:spPr></c:downBars></c:upDownBars>");
                    }
                    AppendAxisIds(xml, secondary);
                    xml.Append("</c:stockChart>");
                    break;

                case ChartKind.Scatter:
                    xml.Append("<c:scatterChart><c:scatterStyle val=\"lineMarker\"/><c:varyColors val=\"0\"/>");
                    foreach (var (series, index) in members) AppendSeries(xml, chart, kind, series, index);
                    AppendGroupLabels(xml, chart, kind);
                    AppendAxisIds(xml, secondary);
                    xml.Append("</c:scatterChart>");
                    break;
            }
        }

        /// <summary>Линия тренда ряда: пунктир из точек его цветом, как у новой линии Word.</summary>
        private static void AppendTrendline(StringBuilder xml, ChartModel chart, ChartSeries series, int index)
        {
            if (series.Trendline is not { } trendline) return;

            xml.Append("<c:trendline><c:spPr><a:ln w=\"22225\" cap=\"rnd\">");
            AppendSolid(xml, ChartModel.SolidColor(chart.SeriesColor(index)));
            xml.Append("<a:prstDash val=\"sysDot\"/></a:ln></c:spPr>");
            string type = trendline.Type switch
            {
                ChartTrendlineType.Exponential => "exp",
                ChartTrendlineType.Logarithmic => "log",
                ChartTrendlineType.Polynomial => "poly",
                ChartTrendlineType.Power => "power",
                ChartTrendlineType.MovingAverage => "movingAvg",
                _ => "linear"
            };
            xml.Append("<c:trendlineType val=\"").Append(type).Append("\"/>");
            if (trendline.Type == ChartTrendlineType.Polynomial)
                xml.Append("<c:order val=\"").Append(Math.Clamp(trendline.Order, 2, 6).ToString(Invariant)).Append("\"/>");
            if (trendline.Type == ChartTrendlineType.MovingAverage)
            {
                xml.Append("<c:period val=\"").Append(Math.Max(2, trendline.Period).ToString(Invariant)).Append("\"/>");
            }
            else
            {
                if (trendline.Forward > 0) xml.Append("<c:forward val=\"").Append(trendline.Forward.ToString("R", Invariant)).Append("\"/>");
                if (trendline.Backward > 0) xml.Append("<c:backward val=\"").Append(trendline.Backward.ToString("R", Invariant)).Append("\"/>");
            }
            bool formulas = trendline.Type != ChartTrendlineType.MovingAverage;
            xml.Append("<c:dispRSqr val=\"").Append(formulas && trendline.ShowRSquared ? "1" : "0").Append("\"/>");
            xml.Append("<c:dispEq val=\"").Append(formulas && trendline.ShowEquation ? "1" : "0").Append("\"/>");
            xml.Append("</c:trendline>");
        }

        /// <summary>Планки погрешностей ряда в обе стороны; у точечной и пузырьковой — по Y.</summary>
        private static void AppendErrorBars(StringBuilder xml, ChartKind kind, ChartSeries series)
        {
            if (series.ErrorBars is not { } bars) return;

            xml.Append("<c:errBars>");
            if (kind is ChartKind.Scatter or ChartKind.Bubble) xml.Append("<c:errDir val=\"y\"/>");
            xml.Append("<c:errBarType val=\"both\"/>");
            string type = bars.Type switch
            {
                ChartErrorBarType.Percentage => "percentage",
                ChartErrorBarType.StandardDeviation => "stdDev",
                ChartErrorBarType.FixedValue => "fixedVal",
                _ => "stdErr"
            };
            xml.Append("<c:errValType val=\"").Append(type).Append("\"/>");
            xml.Append("<c:noEndCap val=\"").Append(bars.NoEndCap ? "1" : "0").Append("\"/>");
            if (bars.Type != ChartErrorBarType.StandardError)
                xml.Append("<c:val val=\"").Append(Math.Max(0, bars.Value).ToString("R", Invariant)).Append("\"/>");
            xml.Append("<c:spPr><a:noFill/><a:ln w=\"9525\" cap=\"flat\" cmpd=\"sng\" algn=\"ctr\">");
            AppendSolid(xml, "595959");
            xml.Append("<a:round/></a:ln></c:spPr></c:errBars>");
        }

        /// <summary>Линия графика: проекции, минимум-максимум; цвет и толщина как у Word.</summary>
        private static void AppendChartLines(StringBuilder xml, string element, string color)
        {
            xml.Append("<c:").Append(element).Append("><c:spPr><a:ln w=\"9525\" cap=\"flat\" cmpd=\"sng\" algn=\"ctr\">");
            AppendSolid(xml, color);
            xml.Append("<a:round/></a:ln></c:spPr></c:").Append(element).Append('>');
        }

        /// <summary>Полосы повышения (белые) и понижения (тёмные) графика.</summary>
        private static void AppendUpDownBars(StringBuilder xml)
        {
            xml.Append("<c:upDownBars><c:gapWidth val=\"150\"/><c:upBars><c:spPr>");
            AppendSolid(xml, "FFFFFF");
            xml.Append("<a:ln w=\"9525\" cap=\"flat\" cmpd=\"sng\" algn=\"ctr\">");
            AppendSolid(xml, "595959");
            xml.Append("<a:round/></a:ln></c:spPr></c:upBars><c:downBars><c:spPr>");
            AppendSolid(xml, "595959");
            xml.Append("<a:ln w=\"9525\" cap=\"flat\" cmpd=\"sng\" algn=\"ctr\">");
            AppendSolid(xml, "595959");
            xml.Append("<a:round/></a:ln></c:spPr></c:downBars></c:upDownBars>");
        }

        /// <summary>Значение c:dLblPos места подписей; null — место Word по умолчанию.</summary>
        private static string? LabelPositionValue(ChartLabelPosition position) => position switch
        {
            ChartLabelPosition.Center => "ctr",
            ChartLabelPosition.InsideEnd => "inEnd",
            ChartLabelPosition.InsideBase => "inBase",
            ChartLabelPosition.OutsideEnd => "outEnd",
            ChartLabelPosition.Above => "t",
            ChartLabelPosition.Below => "b",
            ChartLabelPosition.Left => "l",
            ChartLabelPosition.Right => "r",
            ChartLabelPosition.BestFit => "bestFit",
            _ => null
        };

        /// <summary>Линии сетки оси: основные или вспомогательные (светлее), как у Word.</summary>
        private static void AppendGridlines(StringBuilder xml, ChartModel chart, bool minor)
        {
            xml.Append(minor ? "<c:minorGridlines>" : "<c:majorGridlines>");
            xml.Append("<c:spPr><a:ln w=\"9525\" cap=\"flat\" cmpd=\"sng\" algn=\"ctr\">");
            // На тёмном фоне — полупрозрачный белый, как сетка тёмных стилей Word.
            if (chart.HasDarkBackground) AppendSolid(xml, minor ? "#1CFFFFFF" : "#40FFFFFF");
            else AppendSolid(xml, minor ? "F2F2F2" : LineColor);
            xml.Append("<a:round/></a:ln></c:spPr>");
            xml.Append(minor ? "</c:minorGridlines>" : "</c:majorGridlines>");
        }

        /// <summary>Таблица данных под областью построения: рамки и ключи легенды.</summary>
        private static void AppendDataTable(StringBuilder xml, ChartModel chart)
        {
            xml.Append("<c:dTable><c:showHorzBorder val=\"1\"/><c:showVertBorder val=\"1\"/><c:showOutline val=\"1\"/>");
            xml.Append("<c:showKeys val=\"").Append(chart.HideDataTableKeys ? "0" : "1").Append("\"/>");
            xml.Append("<c:spPr><a:noFill/><a:ln w=\"9525\" cap=\"flat\" cmpd=\"sng\" algn=\"ctr\">");
            AppendSolid(xml, LineColor);
            xml.Append("<a:round/></a:ln></c:spPr>");
            AppendTextProperties(xml, chart.AxisLabelStyle, ChartTextRole.AxisLabels, TextColor);
            xml.Append("</c:dTable>");
        }

        private static void AppendAxisIds(StringBuilder xml, bool secondary)
        {
            xml.Append("<c:axId val=\"").Append(secondary ? SecondaryCategoryAxisId : CategoryAxisId).Append("\"/>");
            xml.Append("<c:axId val=\"").Append(secondary ? SecondaryValueAxisId : ValueAxisId).Append("\"/>");
        }

        private static void AppendGroupLabels(StringBuilder xml, ChartModel chart, ChartKind kind)
        {
            bool show = chart.ShowDataLabels;
            xml.Append("<c:dLbls>");
            if (show)
            {
                xml.Append("<c:spPr><a:noFill/><a:ln><a:noFill/></a:ln></c:spPr>");
                AppendTextProperties(xml, chart.DataLabelStyle, ChartTextRole.DataLabels, "404040");

                // Место подписей — только то, что Word допускает у этого вида: чужое
                // значение он считает ошибкой файла.
                if (LabelPositionValue(chart.LabelPositionFor(kind)) is { } position)
                    xml.Append("<c:dLblPos val=\"").Append(position).Append("\"/>");
            }
            xml.Append("<c:showLegendKey val=\"0\"/><c:showVal val=\"").Append(show ? "1" : "0").Append("\"/>");
            xml.Append("<c:showCatName val=\"0\"/><c:showSerName val=\"0\"/><c:showPercent val=\"0\"/><c:showBubbleSize val=\"0\"/>");
            xml.Append("</c:dLbls>");
        }

        private static void AppendSeries(StringBuilder xml, ChartModel chart, ChartKind kind, ChartSeries series, int index)
        {
            string color = chart.SeriesColor(index);
            string idx = index.ToString(Invariant);
            int pointCount = Math.Max(chart.CategoryCount, series.Values.Count);

            xml.Append("<c:ser><c:idx val=\"").Append(idx).Append("\"/><c:order val=\"").Append(idx).Append("\"/>");

            // Имя ряда — ячейка заголовка его столбца.
            string nameCell = kind switch
            {
                ChartKind.Scatter => $"{SheetName}!${ColumnName(index * 2 + 1)}$1",
                ChartKind.Bubble => $"{SheetName}!${ColumnName(index * 3 + 1)}$1",
                _ => $"{SheetName}!${ColumnName(index + 1)}$1"
            };
            xml.Append("<c:tx><c:strRef><c:f>").Append(nameCell).Append("</c:f><c:strCache><c:ptCount val=\"1\"/>");
            xml.Append("<c:pt idx=\"0\"><c:v>").Append(Escape(series.Name)).Append("</c:v></c:pt></c:strCache></c:strRef></c:tx>");

            switch (kind)
            {
                case ChartKind.Line:
                case ChartKind.Scatter:
                    xml.Append("<c:spPr>");
                    if (series.ShowLine)
                    {
                        xml.Append("<a:ln w=\"28575\" cap=\"rnd\">");
                        AppendSolid(xml, color);
                        xml.Append("<a:round/></a:ln>");
                    }
                    else
                    {
                        xml.Append("<a:ln w=\"25400\" cap=\"rnd\"><a:noFill/><a:round/></a:ln>");
                    }
                    xml.Append("</c:spPr>");

                    if (series.ShowMarkers)
                    {
                        xml.Append("<c:marker><c:symbol val=\"circle\"/><c:size val=\"5\"/><c:spPr>");
                        AppendSolid(xml, color);
                        xml.Append("<a:ln w=\"9525\">");
                        AppendSolid(xml, color);
                        xml.Append("</a:ln></c:spPr></c:marker>");
                    }
                    else
                    {
                        xml.Append("<c:marker><c:symbol val=\"none\"/></c:marker>");
                    }
                    break;

                case ChartKind.Radar:
                    xml.Append("<c:spPr>");
                    if (chart.RadarFilled)
                    {
                        AppendSolid(xml, color);
                        xml.Append("<a:ln><a:noFill/></a:ln>");
                    }
                    else
                    {
                        xml.Append("<a:ln w=\"28575\" cap=\"rnd\">");
                        AppendSolid(xml, color);
                        xml.Append("<a:round/></a:ln>");
                    }
                    xml.Append("</c:spPr>");
                    if (series.ShowMarkers && !chart.RadarFilled)
                    {
                        xml.Append("<c:marker><c:symbol val=\"circle\"/><c:size val=\"5\"/><c:spPr>");
                        AppendSolid(xml, color);
                        xml.Append("</c:spPr></c:marker>");
                    }
                    else
                    {
                        xml.Append("<c:marker><c:symbol val=\"none\"/></c:marker>");
                    }
                    break;

                case ChartKind.Stock:
                    // Сами ряды биржевой не рисуются линиями: видны тени и тела свечей.
                    xml.Append("<c:spPr><a:ln w=\"28575\" cap=\"rnd\"><a:noFill/><a:round/></a:ln></c:spPr>");
                    if (series.ShowMarkers)
                    {
                        xml.Append("<c:marker><c:symbol val=\"dot\"/><c:size val=\"3\"/><c:spPr>");
                        AppendSolid(xml, "404040");
                        xml.Append("</c:spPr></c:marker>");
                    }
                    else
                    {
                        xml.Append("<c:marker><c:symbol val=\"none\"/></c:marker>");
                    }
                    break;

                case ChartKind.Bubble:
                    xml.Append("<c:spPr>");
                    AppendSolid(xml, color);
                    xml.Append("<a:ln w=\"9525\">");
                    AppendSolid(xml, "FFFFFF");
                    xml.Append("</a:ln></c:spPr><c:invertIfNegative val=\"0\"/>");
                    break;

                case ChartKind.Pie:
                case ChartKind.Doughnut:
                    xml.Append("<c:spPr><a:ln w=\"19050\">");
                    AppendSolid(xml, "FFFFFF");
                    xml.Append("</a:ln></c:spPr>");
                    if (kind == ChartKind.Pie) xml.Append("<c:explosion val=\"0\"/>");
                    break;

                default:
                    xml.Append("<c:spPr>");
                    AppendSolid(xml, color);
                    xml.Append("<a:ln><a:noFill/></a:ln></c:spPr>");
                    if (kind is ChartKind.Column or ChartKind.Bar) xml.Append("<c:invertIfNegative val=\"0\"/>");
                    break;
            }

            // Свои цвета точек: доли круговой — всегда, у прочих — где цвет задан.
            for (int point = 0; point < pointCount; point++)
            {
                bool pie = kind is ChartKind.Pie or ChartKind.Doughnut;
                bool own = series.PointColors is { } colors && point < colors.Count && !string.IsNullOrEmpty(colors[point]);
                if (!pie && !own) continue;
                if (kind is ChartKind.Line or ChartKind.Scatter or ChartKind.Radar or ChartKind.Stock) continue;

                string pointColor = chart.PointColor(series, point);
                xml.Append("<c:dPt><c:idx val=\"").Append(point.ToString(Invariant)).Append("\"/>");
                if (kind is ChartKind.Column or ChartKind.Bar) xml.Append("<c:invertIfNegative val=\"0\"/>");
                xml.Append("<c:bubble3D val=\"0\"/><c:spPr>");
                AppendSolid(xml, pointColor);
                if (pie)
                {
                    xml.Append("<a:ln w=\"19050\">");
                    AppendSolid(xml, "FFFFFF");
                    xml.Append("</a:ln>");
                }
                xml.Append("</c:spPr></c:dPt>");
            }

            if (ChartModel.KindHasTrendline(kind))
            {
                AppendTrendline(xml, chart, series, index);
                AppendErrorBars(xml, kind, series);
            }

            if (kind == ChartKind.Scatter)
            {
                string xColumn = ColumnName(index * 2);
                string yColumn = ColumnName(index * 2 + 1);
                var xValues = series.XValues ?? Ordinal(series.Values.Count);

                xml.Append("<c:xVal>");
                AppendNumbers(xml, $"{SheetName}!${xColumn}$2:${xColumn}${Math.Max(2, xValues.Count + 1)}", xValues, null);
                xml.Append("</c:xVal><c:yVal>");
                AppendNumbers(xml, $"{SheetName}!${yColumn}$2:${yColumn}${Math.Max(2, series.Values.Count + 1)}", series.Values, chart.NumberFormat);
                xml.Append("</c:yVal>");
                xml.Append("<c:smooth val=\"").Append(series.Smooth && series.ShowLine ? "1" : "0").Append("\"/>");
            }
            else if (kind == ChartKind.Bubble)
            {
                string xColumn = ColumnName(index * 3);
                string yColumn = ColumnName(index * 3 + 1);
                string sizeColumn = ColumnName(index * 3 + 2);
                var xValues = series.XValues ?? Ordinal(series.Values.Count);
                var sizes = BubbleSizesOf(series);

                xml.Append("<c:xVal>");
                AppendNumbers(xml, $"{SheetName}!${xColumn}$2:${xColumn}${Math.Max(2, xValues.Count + 1)}", xValues, null);
                xml.Append("</c:xVal><c:yVal>");
                AppendNumbers(xml, $"{SheetName}!${yColumn}$2:${yColumn}${Math.Max(2, series.Values.Count + 1)}", series.Values, chart.NumberFormat);
                xml.Append("</c:yVal><c:bubbleSize>");
                AppendNumbers(xml, $"{SheetName}!${sizeColumn}$2:${sizeColumn}${Math.Max(2, sizes.Count + 1)}", sizes, null);
                xml.Append("</c:bubbleSize><c:bubble3D val=\"0\"/>");
            }
            else
            {
                int count = Math.Max(1, pointCount);
                xml.Append("<c:cat><c:strRef><c:f>").Append(SheetName).Append("!$A$2:$A$")
                    .Append((count + 1).ToString(Invariant)).Append("</c:f><c:strCache><c:ptCount val=\"")
                    .Append(count.ToString(Invariant)).Append("\"/>");
                for (int i = 0; i < count; i++)
                    xml.Append("<c:pt idx=\"").Append(i.ToString(Invariant)).Append("\"><c:v>")
                        .Append(Escape(chart.CategoryLabel(i))).Append("</c:v></c:pt>");
                xml.Append("</c:strCache></c:strRef></c:cat>");

                string column = ColumnName(index + 1);
                var values = new List<double?>(series.Values);
                while (values.Count < count) values.Add(null);

                xml.Append("<c:val>");
                AppendNumbers(xml, $"{SheetName}!${column}$2:${column}${count + 1}", values, chart.NumberFormat);
                xml.Append("</c:val>");

                if (kind is ChartKind.Line or ChartKind.Stock)
                    xml.Append("<c:smooth val=\"").Append(kind == ChartKind.Line && series.Smooth ? "1" : "0").Append("\"/>");
            }

            xml.Append("</c:ser>");
        }

        /// <summary>Размеры пузырьков ряда; недостающие — единица, как у пузырька без размера.</summary>
        private static List<double?> BubbleSizesOf(ChartSeries series)
        {
            var sizes = new List<double?>(series.Values.Count);
            for (int i = 0; i < series.Values.Count; i++)
                sizes.Add(series.BubbleSizes is { } own && i < own.Count && own[i] is double size ? size : 1.0);
            return sizes;
        }

        private static List<double?> Ordinal(int count)
        {
            var list = new List<double?>(count);
            for (int i = 0; i < count; i++) list.Add(i + 1);
            return list;
        }

        private static void AppendNumbers(StringBuilder xml, string reference, IReadOnlyList<double?> values, string? format)
        {
            xml.Append("<c:numRef><c:f>").Append(reference).Append("</c:f><c:numCache><c:formatCode>")
                .Append(Escape(string.IsNullOrEmpty(format) ? "General" : format!)).Append("</c:formatCode>");
            xml.Append("<c:ptCount val=\"").Append(values.Count.ToString(Invariant)).Append("\"/>");
            for (int i = 0; i < values.Count; i++)
            {
                if (values[i] is not double v || double.IsNaN(v) || double.IsInfinity(v)) continue;
                xml.Append("<c:pt idx=\"").Append(i.ToString(Invariant)).Append("\"><c:v>")
                    .Append(v.ToString("R", Invariant)).Append("</c:v></c:pt>");
            }
            xml.Append("</c:numCache></c:numRef>");
        }

        private static void AppendAxes(StringBuilder xml, ChartModel chart)
        {
            bool horizontalBars = chart.Kind == ChartKind.Bar;
            bool scatter = chart.IsXY;
            bool radar = chart.Kind == ChartKind.Radar;

            string categoryPosition = horizontalBars ? "l" : "b";
            string valuePosition = horizontalBars ? "b" : "l";

            // Ось категорий (у точечной — ось X, тоже ось значений).
            xml.Append(scatter ? "<c:valAx>" : "<c:catAx>");
            xml.Append("<c:axId val=\"").Append(CategoryAxisId).Append("\"/>");
            xml.Append("<c:scaling><c:orientation val=\"minMax\"/>");
            if (scatter && chart.XAxisMax is double xMax) xml.Append("<c:max val=\"").Append(xMax.ToString("R", Invariant)).Append("\"/>");
            if (scatter && chart.XAxisMin is double xMin) xml.Append("<c:min val=\"").Append(xMin.ToString("R", Invariant)).Append("\"/>");
            xml.Append("</c:scaling>");
            xml.Append("<c:delete val=\"").Append(chart.HideCategoryAxis ? "1" : "0").Append("\"/>");
            xml.Append("<c:axPos val=\"").Append(categoryPosition).Append("\"/>");

            // Лучи лепестковой — линии сетки её оси категорий.
            if (radar && chart.ShowGridlines)
            {
                xml.Append("<c:majorGridlines><c:spPr><a:ln w=\"9525\" cap=\"flat\" cmpd=\"sng\" algn=\"ctr\">");
                AppendSolid(xml, LineColor);
                xml.Append("<a:round/></a:ln></c:spPr></c:majorGridlines>");
            }
            else if (!radar)
            {
                if (chart.ShowCategoryGridlines) AppendGridlines(xml, chart, minor: false);
                if (chart.ShowCategoryMinorGridlines) AppendGridlines(xml, chart, minor: true);
            }

            if (!string.IsNullOrWhiteSpace(chart.CategoryAxisTitle))
                AppendAxisTitle(xml, chart, chart.CategoryAxisTitle!, vertical: horizontalBars);
            if (scatter && !string.IsNullOrEmpty(chart.XNumberFormat))
                xml.Append("<c:numFmt formatCode=\"").Append(Escape(chart.XNumberFormat!)).Append("\" sourceLinked=\"0\"/>");
            else
                xml.Append("<c:numFmt formatCode=\"General\" sourceLinked=\"1\"/>");
            xml.Append("<c:majorTickMark val=\"none\"/><c:minorTickMark val=\"none\"/><c:tickLblPos val=\"nextTo\"/>");
            xml.Append("<c:spPr><a:noFill/><a:ln w=\"9525\" cap=\"flat\" cmpd=\"sng\" algn=\"ctr\">");
            AppendSolid(xml, LineColor);
            xml.Append("<a:round/></a:ln></c:spPr>");
            AppendTextProperties(xml, chart.AxisLabelStyle, ChartTextRole.AxisLabels, TextColor);
            xml.Append("<c:crossAx val=\"").Append(ValueAxisId).Append("\"/><c:crosses val=\"autoZero\"/>");
            if (scatter)
            {
                xml.Append("<c:crossBetween val=\"midCat\"/>");
                xml.Append("</c:valAx>");
            }
            else
            {
                xml.Append("<c:auto val=\"1\"/><c:lblAlgn val=\"ctr\"/><c:lblOffset val=\"100\"/><c:noMultiLvlLbl val=\"0\"/>");
                xml.Append("</c:catAx>");
            }

            // Ось значений.
            xml.Append("<c:valAx><c:axId val=\"").Append(ValueAxisId).Append("\"/>");
            xml.Append("<c:scaling><c:orientation val=\"minMax\"/>");
            if (chart.ValueAxisMax is double max) xml.Append("<c:max val=\"").Append(max.ToString("R", Invariant)).Append("\"/>");
            if (chart.ValueAxisMin is double min) xml.Append("<c:min val=\"").Append(min.ToString("R", Invariant)).Append("\"/>");
            xml.Append("</c:scaling>");
            xml.Append("<c:delete val=\"").Append(chart.HideValueAxis ? "1" : "0").Append("\"/>");
            xml.Append("<c:axPos val=\"").Append(valuePosition).Append("\"/>");
            if (chart.ShowGridlines) AppendGridlines(xml, chart, minor: false);
            if (chart.ShowMinorGridlines) AppendGridlines(xml, chart, minor: true);
            if (!string.IsNullOrWhiteSpace(chart.ValueAxisTitle))
                AppendAxisTitle(xml, chart, chart.ValueAxisTitle!, vertical: !horizontalBars);
            string format = chart.Grouping == ChartGrouping.PercentStacked
                ? "0%"
                : string.IsNullOrEmpty(chart.NumberFormat) ? "General" : chart.NumberFormat!;
            xml.Append("<c:numFmt formatCode=\"").Append(Escape(format)).Append("\" sourceLinked=\"")
                .Append(chart.Grouping == ChartGrouping.PercentStacked || !string.IsNullOrEmpty(chart.NumberFormat) ? "0" : "1")
                .Append("\"/>");
            xml.Append("<c:majorTickMark val=\"none\"/><c:minorTickMark val=\"none\"/><c:tickLblPos val=\"nextTo\"/>");
            xml.Append("<c:spPr><a:noFill/><a:ln><a:noFill/></a:ln></c:spPr>");
            AppendTextProperties(xml, chart.AxisLabelStyle, ChartTextRole.AxisLabels, TextColor);
            xml.Append("<c:crossAx val=\"").Append(CategoryAxisId).Append("\"/><c:crosses val=\"autoZero\"/>");
            xml.Append("<c:crossBetween val=\"").Append(scatter || chart.Kind == ChartKind.Area ? "midCat" : "between").Append("\"/>");
            xml.Append("</c:valAx>");

            if (chart.HasSecondaryAxis) AppendSecondaryAxes(xml, chart);
        }

        /// <summary>
        /// Вспомогательные оси, как у комбинированной диаграммы Word: ось значений справа,
        /// пересекает ось категорий в её конце; своя ось категорий скрыта.
        /// </summary>
        private static void AppendSecondaryAxes(StringBuilder xml, ChartModel chart)
        {
            xml.Append("<c:valAx><c:axId val=\"").Append(SecondaryValueAxisId).Append("\"/>");
            xml.Append("<c:scaling><c:orientation val=\"minMax\"/>");
            if (chart.SecondaryAxisMax is double max) xml.Append("<c:max val=\"").Append(max.ToString("R", Invariant)).Append("\"/>");
            if (chart.SecondaryAxisMin is double min) xml.Append("<c:min val=\"").Append(min.ToString("R", Invariant)).Append("\"/>");
            xml.Append("</c:scaling>");
            xml.Append("<c:delete val=\"0\"/><c:axPos val=\"r\"/>");
            if (!string.IsNullOrWhiteSpace(chart.SecondaryAxisTitle))
                AppendAxisTitle(xml, chart, chart.SecondaryAxisTitle!, vertical: true);
            string format = chart.Grouping == ChartGrouping.PercentStacked
                ? "0%"
                : string.IsNullOrEmpty(chart.SecondaryNumberFormat) ? "General" : chart.SecondaryNumberFormat!;
            xml.Append("<c:numFmt formatCode=\"").Append(Escape(format)).Append("\" sourceLinked=\"")
                .Append(chart.Grouping == ChartGrouping.PercentStacked || !string.IsNullOrEmpty(chart.SecondaryNumberFormat) ? "0" : "1")
                .Append("\"/>");
            xml.Append("<c:majorTickMark val=\"out\"/><c:minorTickMark val=\"none\"/><c:tickLblPos val=\"nextTo\"/>");
            xml.Append("<c:spPr><a:noFill/><a:ln><a:noFill/></a:ln></c:spPr>");
            AppendTextProperties(xml, chart.AxisLabelStyle, ChartTextRole.AxisLabels, TextColor);
            xml.Append("<c:crossAx val=\"").Append(SecondaryCategoryAxisId).Append("\"/><c:crosses val=\"max\"/>");
            xml.Append("<c:crossBetween val=\"").Append(chart.Kind == ChartKind.Area ? "midCat" : "between").Append("\"/>");
            xml.Append("</c:valAx>");

            xml.Append("<c:catAx><c:axId val=\"").Append(SecondaryCategoryAxisId).Append("\"/>");
            xml.Append("<c:scaling><c:orientation val=\"minMax\"/></c:scaling>");
            xml.Append("<c:delete val=\"1\"/><c:axPos val=\"b\"/>");
            xml.Append("<c:numFmt formatCode=\"General\" sourceLinked=\"1\"/>");
            xml.Append("<c:majorTickMark val=\"out\"/><c:minorTickMark val=\"none\"/><c:tickLblPos val=\"nextTo\"/>");
            xml.Append("<c:crossAx val=\"").Append(SecondaryValueAxisId).Append("\"/><c:crosses val=\"autoZero\"/>");
            xml.Append("<c:auto val=\"1\"/><c:lblAlgn val=\"ctr\"/><c:lblOffset val=\"100\"/><c:noMultiLvlLbl val=\"0\"/>");
            xml.Append("</c:catAx>");
        }

        private static void AppendAxisTitle(StringBuilder xml, ChartModel chart, string text, bool vertical)
        {
            xml.Append("<c:title>");
            AppendRichText(xml, text, chart.AxisTitleStyle, ChartTextRole.AxisTitles, rotation: vertical ? -5400000 : null);
            xml.Append("<c:overlay val=\"0\"/></c:title>");
        }

        private static void AppendRichText(StringBuilder xml, string text, ChartTextStyle? style, ChartTextRole role, int? rotation)
        {
            xml.Append("<c:tx><c:rich><a:bodyPr");
            if (rotation is int rot) xml.Append(" rot=\"").Append(rot.ToString(Invariant)).Append("\" vert=\"horz\"");
            xml.Append("/><a:lstStyle/>");

            foreach (string line in text.Replace("\r\n", "\n").Split('\n'))
            {
                xml.Append("<a:p><a:pPr>");
                AppendRunDefaults(xml, style, role, TextColor);
                xml.Append("</a:pPr>");
                if (line.Length > 0)
                    xml.Append("<a:r><a:rPr lang=\"ru-RU\"/><a:t>").Append(Escape(line)).Append("</a:t></a:r>");
                xml.Append("</a:p>");
            }

            xml.Append("</c:rich></c:tx>");
        }

        /// <summary>Свойства текста надписей рода role: размер, начертание, цвет и шрифт.</summary>
        private static void AppendTextProperties(StringBuilder xml, ChartTextStyle? style, ChartTextRole role, string defaultColor)
        {
            xml.Append("<c:txPr><a:bodyPr/><a:lstStyle/><a:p><a:pPr>");
            AppendRunDefaults(xml, style, role, defaultColor);
            xml.Append("</a:pPr><a:endParaRPr lang=\"ru-RU\"/></a:p></c:txPr>");
        }

        /// <summary>a:defRPr по оформлению надписей: что не задано — как у Word по умолчанию.</summary>
        private static void AppendRunDefaults(StringBuilder xml, ChartTextStyle? style, ChartTextRole role, string defaultColor)
        {
            double size = style?.SizePt ?? ChartModel.DefaultSizeOf(role);
            int hundredths = (int)Math.Round(Math.Clamp(size, 1.0, 400.0) * 100.0);
            bool bold = style?.Bold ?? ChartModel.DefaultBoldOf(role);

            xml.Append("<a:defRPr sz=\"").Append(hundredths.ToString(Invariant)).Append("\" b=\"").Append(bold ? "1" : "0").Append('"');
            if (style?.Italic is true) xml.Append(" i=\"1\"");
            xml.Append('>');
            AppendSolid(xml, string.IsNullOrWhiteSpace(style?.ColorHex) ? defaultColor : style!.ColorHex!);
            if (!string.IsNullOrWhiteSpace(style?.FontFamily))
            {
                string family = Escape(style!.FontFamily!);
                xml.Append("<a:latin typeface=\"").Append(family).Append("\"/>");
                xml.Append("<a:cs typeface=\"").Append(family).Append("\"/>");
            }
            xml.Append("</a:defRPr>");
        }

        /// <summary>
        /// Заливка цветом ряда: сплошная (с прозрачностью, если она есть) или градиент
        /// DrawingML — линейный под своим углом, радиальный из центра; конического в
        /// Word нет, ближе всего к нему заливка «по форме».
        /// </summary>
        private static void AppendSolid(StringBuilder xml, string color)
        {
            if (ChartModel.IsGradient(color))
            {
                var spec = GradientSpec.Parse(color);
                if (!spec.IsSolid)
                {
                    AppendGradient(xml, spec);
                    return;
                }
                color = spec.SolidHex;
            }

            xml.Append("<a:solidFill>");
            AppendColor(xml, color);
            xml.Append("</a:solidFill>");
        }

        private static void AppendColor(StringBuilder xml, string color)
        {
            var (a, r, g, b) = ChartModel.ParseArgb(color);
            xml.Append("<a:srgbClr val=\"").Append(string.Create(Invariant, $"{r:X2}{g:X2}{b:X2}")).Append('"');
            if (a < 255)
                xml.Append("><a:alpha val=\"").Append((a * 100000 / 255).ToString(Invariant)).Append("\"/></a:srgbClr>");
            else
                xml.Append("/>");
        }

        private static void AppendGradient(StringBuilder xml, GradientSpec spec)
        {
            xml.Append("<a:gradFill rotWithShape=\"1\"><a:gsLst>");
            foreach (var stop in spec.SortedStops())
            {
                xml.Append("<a:gs pos=\"").Append(((int)Math.Round(stop.Position * 100000)).ToString(Invariant)).Append("\">");
                AppendColor(xml, stop.Hex);
                xml.Append("</a:gs>");
            }
            xml.Append("</a:gsLst>");

            switch (spec.Kind)
            {
                case GradientKind.Radial:
                    xml.Append("<a:path path=\"circle\"><a:fillToRect l=\"50000\" t=\"50000\" r=\"50000\" b=\"50000\"/></a:path>");
                    break;
                case GradientKind.Conic:
                    xml.Append("<a:path path=\"shape\"><a:fillToRect l=\"50000\" t=\"50000\" r=\"50000\" b=\"50000\"/></a:path>");
                    break;
                default:
                    // У подборщика угол идёт против часовой (90 — снизу вверх), у Word — по часовой.
                    double word = ((360.0 - spec.AngleDeg) % 360.0 + 360.0) % 360.0;
                    xml.Append("<a:lin ang=\"").Append(((int)Math.Round(word * 60000)).ToString(Invariant)).Append("\" scaled=\"0\"/>");
                    break;
            }

            xml.Append("<a:tileRect/></a:gradFill>");
        }

        private static string Hex(string color)
        {
            string value = color.TrimStart('#').ToUpperInvariant();
            return value.Length == 6 ? value : "000000";
        }

        private static string Escape(string text) => SecurityElement.Escape(StripInvalidXml(text)) ?? string.Empty;

        /// <summary>Убирает знаки, которых XML не допускает: управляющие, кроме табуляции.</summary>
        private static string StripInvalidXml(string text)
        {
            StringBuilder? clean = null;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                bool valid = c == '\t' || c == '\n' || c == '\r' || (c >= 0x20 && c != 0xFFFE && c != 0xFFFF);
                if (valid)
                {
                    clean?.Append(c);
                    continue;
                }

                clean ??= new StringBuilder(text, 0, i, text.Length);
            }
            return clean?.ToString() ?? text;
        }

        /// <summary>Имя столбца Excel по номеру с нуля: 0 — A, 25 — Z, 26 — AA.</summary>
        internal static string ColumnName(int index)
        {
            var name = new StringBuilder();
            int n = index + 1;
            while (n > 0)
            {
                int rem = (n - 1) % 26;
                name.Insert(0, (char)('A' + rem));
                n = (n - 1) / 26;
            }
            return name.ToString();
        }

        // ── Книга Excel ──────────────────────────────────────────────────

        /// <summary>
        /// Книга с данными диаграммы: в первом столбце категории, дальше по столбцу на
        /// ряд, имена рядов — в первой строке. У точечной — по паре столбцов X и Y на ряд.
        /// </summary>
        public static byte[] BuildWorkbook(ChartModel chart)
        {
            using var memory = new MemoryStream();
            using (var document = SpreadsheetDocument.Create(memory, SpreadsheetDocumentType.Workbook))
            {
                var workbookPart = document.AddWorkbookPart();
                workbookPart.Workbook = new S.Workbook();

                var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
                var sheetData = new S.SheetData();
                worksheetPart.Worksheet = new S.Worksheet(sheetData);

                var sheets = workbookPart.Workbook.AppendChild(new S.Sheets());
                sheets.AppendChild(new S.Sheet
                {
                    Id = workbookPart.GetIdOfPart(worksheetPart),
                    SheetId = 1U,
                    Name = SheetName
                });

                var cells = new SortedDictionary<int, SortedDictionary<int, object?>>();
                void Put(int row, int column, object? value)
                {
                    if (!cells.TryGetValue(row, out var line))
                    {
                        line = new SortedDictionary<int, object?>();
                        cells[row] = line;
                    }
                    line[column] = value;
                }

                if (chart.Kind == ChartKind.Bubble)
                {
                    for (int s = 0; s < chart.Series.Count; s++)
                    {
                        var series = chart.Series[s];
                        var xValues = series.XValues ?? Ordinal(series.Values.Count);
                        var sizes = BubbleSizesOf(series);
                        Put(0, s * 3, "X");
                        Put(0, s * 3 + 1, series.Name);
                        Put(0, s * 3 + 2, "Размер");
                        for (int i = 0; i < xValues.Count; i++) Put(i + 1, s * 3, xValues[i]);
                        for (int i = 0; i < series.Values.Count; i++) Put(i + 1, s * 3 + 1, series.Values[i]);
                        for (int i = 0; i < sizes.Count; i++) Put(i + 1, s * 3 + 2, sizes[i]);
                    }
                }
                else if (chart.Kind == ChartKind.Scatter)
                {
                    for (int s = 0; s < chart.Series.Count; s++)
                    {
                        var series = chart.Series[s];
                        var xValues = series.XValues ?? Ordinal(series.Values.Count);
                        Put(0, s * 2, "X");
                        Put(0, s * 2 + 1, series.Name);
                        for (int i = 0; i < xValues.Count; i++) Put(i + 1, s * 2, xValues[i]);
                        for (int i = 0; i < series.Values.Count; i++) Put(i + 1, s * 2 + 1, series.Values[i]);
                    }
                }
                else
                {
                    int count = Math.Max(1, chart.CategoryCount);
                    for (int i = 0; i < count; i++) Put(i + 1, 0, chart.CategoryLabel(i));
                    for (int s = 0; s < chart.Series.Count; s++)
                    {
                        var series = chart.Series[s];
                        Put(0, s + 1, series.Name);
                        for (int i = 0; i < series.Values.Count; i++) Put(i + 1, s + 1, series.Values[i]);
                    }
                }

                foreach (var (rowIndex, line) in cells)
                {
                    uint rowNumber = (uint)(rowIndex + 1);
                    var row = new S.Row { RowIndex = rowNumber };
                    foreach (var (columnIndex, value) in line)
                    {
                        string reference = ColumnName(columnIndex) + rowNumber.ToString(Invariant);
                        switch (value)
                        {
                            case double number when !double.IsNaN(number) && !double.IsInfinity(number):
                                row.AppendChild(new S.Cell
                                {
                                    CellReference = reference,
                                    CellValue = new S.CellValue(number.ToString("R", Invariant))
                                });
                                break;

                            case string text:
                                row.AppendChild(new S.Cell
                                {
                                    CellReference = reference,
                                    DataType = S.CellValues.InlineString,
                                    InlineString = new S.InlineString(new S.Text(StripInvalidXml(text)))
                                });
                                break;
                        }
                    }
                    sheetData.AppendChild(row);
                }

                workbookPart.Workbook.Save();
            }

            return memory.ToArray();
        }

        // ── Части пакета ─────────────────────────────────────────────────

        /// <summary>
        /// Пишет диаграмму в новую часть: исходную диаграмму Word как есть, если её не
        /// правили, иначе — заново по модели вместе с книгой данных.
        /// </summary>
        public static void WriteChartPart(ChartPart chartPart, ChartModel chart, Func<string, byte[]?>? resolveImage = null)
        {
            if (chart.IsUnchangedWordChart && TryWriteSource(chartPart, chart.Source!)) return;

            // В .docx уходит то, что видно: скрытые фильтром ряды и категории не пишутся.
            chart = chart.VisibleView();

            var workbookPart = chartPart.AddEmbeddedPackagePart(EmbeddedPackagePartType.Xlsx);
            using (var stream = new MemoryStream(BuildWorkbook(chart)))
                workbookPart.FeedData(stream);

            // Картинка фона — своей частью у диаграммы.
            string? imageRelationshipId = null;
            if (!string.IsNullOrEmpty(chart.BackgroundImage) && resolveImage is not null)
            {
                byte[]? picture;
                try { picture = resolveImage(chart.BackgroundImage!); }
                catch { picture = null; }

                if (picture is { Length: > 0 } && PictureType(picture) is { } pictureType)
                {
                    var imagePart = chartPart.AddImagePart(pictureType);
                    using (var pictureStream = new MemoryStream(picture))
                        imagePart.FeedData(pictureStream);
                    imageRelationshipId = chartPart.GetIdOfPart(imagePart);
                }
            }

            string xml = BuildChartXml(chart, chartPart.GetIdOfPart(workbookPart), imageRelationshipId);
            using var output = new MemoryStream(new UTF8Encoding(false).GetBytes(xml));
            chartPart.FeedData(output);
        }

        /// <summary>Вид картинки по её первым байтам; null — не картинка, которую Word примет.</summary>
        private static PartTypeInfo? PictureType(byte[] data)
        {
            if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47) return ImagePartType.Png;
            if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return ImagePartType.Jpeg;
            if (data.Length >= 4 && data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46) return ImagePartType.Gif;
            if (data.Length >= 2 && data[0] == 0x42 && data[1] == 0x4D) return ImagePartType.Bmp;
            return null;
        }

        /// <summary>
        /// Переносит исходную диаграмму Word и её связанные части под теми же Id связей.
        /// False — часть неизвестного вида: тогда ничего не пишется, и диаграмма уходит
        /// заново по модели.
        /// </summary>
        private static bool TryWriteSource(ChartPart chartPart, WordChartSource source)
        {
            foreach (var part in source.Parts)
                if (!ImportService.ChartPassthroughRelationships.Contains(part.RelationshipType))
                    return false;

            foreach (var part in source.Parts)
            {
                OpenXmlPart added = part.RelationshipType switch
                {
                    // Книга Excel — с расширением .xlsx, как её кладёт Word: по нему Word
                    // и открывает данные диаграммы в Excel.
                    "http://schemas.openxmlformats.org/officeDocument/2006/relationships/package"
                        when part.ContentType == WorkbookContentType
                        => AddWorkbookPart(chartPart, part.RelationshipId),
                    "http://schemas.openxmlformats.org/officeDocument/2006/relationships/package"
                        => chartPart.AddNewPart<EmbeddedPackagePart>(part.ContentType, part.RelationshipId),
                    "http://schemas.openxmlformats.org/officeDocument/2006/relationships/oleObject"
                        => chartPart.AddNewPart<EmbeddedObjectPart>(part.ContentType, part.RelationshipId),
                    "http://schemas.openxmlformats.org/officeDocument/2006/relationships/themeOverride"
                        => chartPart.AddNewPart<ThemeOverridePart>(part.ContentType, part.RelationshipId),
                    "http://schemas.microsoft.com/office/2011/relationships/chartStyle"
                        => chartPart.AddNewPart<ChartStylePart>(part.ContentType, part.RelationshipId),
                    _ => chartPart.AddNewPart<ChartColorStylePart>(part.ContentType, part.RelationshipId)
                };

                using var data = new MemoryStream(part.Data);
                added.FeedData(data);
            }

            using var output = new MemoryStream(new UTF8Encoding(false).GetBytes(source.ChartXml));
            chartPart.FeedData(output);
            return true;
        }

        private static EmbeddedPackagePart AddWorkbookPart(ChartPart chartPart, string relationshipId)
        {
            var part = chartPart.AddEmbeddedPackagePart(EmbeddedPackagePartType.Xlsx);
            if (chartPart.GetIdOfPart(part) != relationshipId)
                chartPart.ChangeIdOfPart(part, relationshipId);
            return part;
        }
    }
}
