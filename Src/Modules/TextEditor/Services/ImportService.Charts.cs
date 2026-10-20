using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Writersword.Modules.TextEditor.Models.Document;
using Dr = DocumentFormat.OpenXml.Drawing;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Диаграммы Word (c:chart в рисунке). Диаграмма читается в модель: вид, ряды,
    /// категории, цвета, название, легенда, оси. Сама часть диаграммы и связанные с ней
    /// части — книга Excel с данными, стиль и цвета — сохраняются как есть: пока
    /// диаграмму не правили, в .docx уходят именно они.
    /// </summary>
    public sealed partial class ImportService
    {
        /// <summary>URI графики рисунка-диаграммы (a:graphicData uri).</summary>
        internal const string ChartGraphicUri = "http://schemas.openxmlformats.org/drawingml/2006/chart";

        private static readonly XNamespace ChartNs = "http://schemas.openxmlformats.org/drawingml/2006/chart";
        private static readonly XNamespace DrawingNs = "http://schemas.openxmlformats.org/drawingml/2006/main";

        /// <summary>Связи части диаграммы, которые переносятся в .docx как есть.</summary>
        internal static readonly IReadOnlyCollection<string> ChartPassthroughRelationships = new[]
        {
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships/package",
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships/oleObject",
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships/themeOverride",
            "http://schemas.microsoft.com/office/2011/relationships/chartStyle",
            "http://schemas.microsoft.com/office/2011/relationships/chartColorStyle"
        };

        /// <summary>Цвета темы документа для диаграмм: accent1…6, dk1, lt1, dk2, lt2.</summary>
        private Dictionary<string, string>? _chartThemeColors;

        /// <summary>
        /// Читает диаграмму рисунка. Null — части диаграммы нет или она не разобралась:
        /// рисунок тогда пропускается, как и раньше.
        /// </summary>
        private ChartModel? ReadChart(Dr.Graphic graphic, MainDocumentPart mainPart, Dictionary<string, byte[]> extractedImages, List<string> warnings)
        {
            string? relId = null;
            foreach (var element in graphic.GraphicData!.Descendants())
            {
                if (element.LocalName != "chart") continue;
                foreach (var attribute in element.GetAttributes())
                {
                    if (attribute.LocalName == "id" && attribute.NamespaceUri == RelationshipsNamespace)
                    {
                        relId = attribute.Value;
                        break;
                    }
                }
                break;
            }
            if (string.IsNullOrEmpty(relId)) return null;

            ChartPart? chartPart = null;
            foreach (var pair in mainPart.Parts)
            {
                if (pair.RelationshipId != relId) continue;
                chartPart = pair.OpenXmlPart as ChartPart;
                break;
            }
            if (chartPart is null) return null;

            string xml;
            try
            {
                using var stream = chartPart.GetStream(FileMode.Open, FileAccess.Read);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                xml = reader.ReadToEnd();
            }
            catch
            {
                warnings.Add("Диаграмма не прочиталась и пропущена.");
                return null;
            }

            ChartModel chart;
            XDocument document;
            try
            {
                document = XDocument.Parse(xml);
                chart = ParseChart(document, ChartThemeColors(mainPart));
            }
            catch
            {
                warnings.Add("Диаграмма не разобралась и пропущена.");
                return null;
            }

            // Картинка фона всей диаграммы — файлом проекта, как картинка документа.
            var backgroundBlip = document.Root?.Element(ChartNs + "spPr")?
                .Element(DrawingNs + "blipFill")?.Element(DrawingNs + "blip");
            string? backgroundRelId = (string?)backgroundBlip?.Attribute(XName.Get("embed", RelationshipsNamespace));
            if (!string.IsNullOrEmpty(backgroundRelId))
            {
                string? fileName = ExtractImagePart(chartPart, backgroundRelId!, extractedImages, warnings, out _);
                if (fileName is not null)
                {
                    chart.BackgroundImage = fileName;
                    chart.BackgroundFill = null;
                }
            }

            chart.Source = ReadChartSource(chartPart, xml);
            if (chart.Source is not null)
                chart.Source.Fingerprint = chart.ComputeFingerprint();

            return chart;
        }

        /// <summary>
        /// Части диаграммы для переноса как есть. Null — у диаграммы есть то, чего
        /// перенос не умеет (внешняя книга, надписи поверх диаграммы со своими частями):
        /// такая диаграмма в .docx пишется заново из модели.
        /// </summary>
        private static WordChartSource? ReadChartSource(ChartPart chartPart, string xml)
        {
            if (chartPart.ExternalRelationships.Any() || chartPart.HyperlinkRelationships.Any())
                return null;

            var source = new WordChartSource { ChartXml = xml };

            foreach (var pair in chartPart.Parts)
            {
                var part = pair.OpenXmlPart;
                if (!ChartPassthroughRelationships.Contains(part.RelationshipType)) return null;
                if (part.Parts.Any() || part.ExternalRelationships.Any()) return null;

                byte[] data;
                using (var stream = part.GetStream(FileMode.Open, FileAccess.Read))
                using (var memory = new MemoryStream())
                {
                    stream.CopyTo(memory);
                    data = memory.ToArray();
                }

                source.Parts.Add(new WordChartPart
                {
                    RelationshipId = pair.RelationshipId,
                    RelationshipType = part.RelationshipType,
                    ContentType = part.ContentType,
                    Data = data
                });
            }

            return source;
        }

        /// <summary>Цвета темы документа; без темы — цвета Office.</summary>
        private Dictionary<string, string> ChartThemeColors(MainDocumentPart mainPart)
        {
            if (_chartThemeColors is not null) return _chartThemeColors;

            var colors = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["dk1"] = "000000", ["lt1"] = "FFFFFF", ["dk2"] = "44546A", ["lt2"] = "E7E6E6",
                ["accent1"] = "4472C4", ["accent2"] = "ED7D31", ["accent3"] = "A5A5A5",
                ["accent4"] = "FFC000", ["accent5"] = "5B9BD5", ["accent6"] = "70AD47",
                ["hlink"] = "0563C1", ["folHlink"] = "954F72"
            };

            try
            {
                var themePart = mainPart.ThemePart;
                if (themePart is not null)
                {
                    using var stream = themePart.GetStream(FileMode.Open, FileAccess.Read);
                    var theme = XDocument.Load(stream);
                    var scheme = theme.Descendants(DrawingNs + "clrScheme").FirstOrDefault();
                    if (scheme is not null)
                    {
                        foreach (var entry in scheme.Elements())
                        {
                            var value = entry.Elements().FirstOrDefault();
                            string? hex = value?.Name.LocalName switch
                            {
                                "srgbClr" => (string?)value.Attribute("val"),
                                "sysClr" => (string?)value.Attribute("lastClr"),
                                _ => null
                            };
                            if (hex is { Length: 6 }) colors[entry.Name.LocalName] = hex.ToUpperInvariant();
                        }
                    }
                }
            }
            catch
            {
                // Тема не прочиталась — остаются цвета Office.
            }

            _chartThemeColors = colors;
            return colors;
        }

        // ── Разбор XML диаграммы ─────────────────────────────────────────

        /// <summary>
        /// Модель диаграммы по XML части (c:chartSpace). Открыт для проверок: разбор не
        /// зависит от пакета документа.
        /// </summary>
        internal static ChartModel ParseChart(XDocument document, IReadOnlyDictionary<string, string> theme)
        {
            var chart = new ChartModel();
            var space = document.Root ?? throw new FormatException("Пустая диаграмма.");
            var chartElement = space.Element(ChartNs + "chart") ?? throw new FormatException("Нет c:chart.");
            var plotArea = chartElement.Element(ChartNs + "plotArea") ?? throw new FormatException("Нет c:plotArea.");

            bool mainKindSet = false;
            bool anyLabels = false;

            // Вспомогательная ось значений — та, что пересекает ось категорий в конце.
            string? secondaryAxisId = SecondaryValueAxisId(plotArea);

            foreach (var group in plotArea.Elements())
            {
                ChartKind? groupKind = GroupKind(group);
                if (groupKind is null) continue;

                ChartKind kind = groupKind.Value;
                bool onSecondary = secondaryAxisId is not null
                    && group.Elements(ChartNs + "axId").Any(a => (string?)a.Attribute("val") == secondaryAxisId);
                string grouping = Val(group.Element(ChartNs + "grouping")) ?? "standard";

                if (!mainKindSet)
                {
                    mainKindSet = true;
                    chart.Kind = kind;
                    chart.Grouping = grouping switch
                    {
                        "stacked" => ChartGrouping.Stacked,
                        "percentStacked" => ChartGrouping.PercentStacked,
                        _ => ChartGrouping.Standard
                    };

                    if (IntVal(group.Element(ChartNs + "gapWidth")) is int gap) chart.GapWidthPercent = gap;
                    else if (kind is ChartKind.Column or ChartKind.Bar) chart.GapWidthPercent = 150;

                    if (IntVal(group.Element(ChartNs + "overlap")) is int overlap) chart.OverlapPercent = overlap;
                    else chart.OverlapPercent = chart.Grouping == ChartGrouping.Standard ? 0 : 100;

                    if (IntVal(group.Element(ChartNs + "holeSize")) is int hole) chart.HoleSizePercent = hole;
                    else if (kind == ChartKind.Doughnut) chart.HoleSizePercent = 50;

                    if (IntVal(group.Element(ChartNs + "firstSliceAng")) is int angle) chart.FirstSliceAngle = angle;

                    if (kind == ChartKind.Radar)
                        chart.RadarFilled = Val(group.Element(ChartNs + "radarStyle")) == "filled";
                }

                bool groupMarkers = Val(group.Element(ChartNs + "marker")) is not ("0" or "false");
                string scatterStyle = Val(group.Element(ChartNs + "scatterStyle")) ?? "lineMarker";

                if (ShowsValues(group.Element(ChartNs + "dLbls"))) anyLabels = true;

                // Линии графика и областей. У биржевой минимум-максимум и полосы — её
                // собственное устройство, а не добавленные элементы.
                if (group.Name.LocalName is "lineChart" or "areaChart")
                {
                    if (group.Element(ChartNs + "dropLines") is not null) chart.ShowDropLines = true;
                    if (group.Name.LocalName == "lineChart")
                    {
                        if (group.Element(ChartNs + "hiLowLines") is not null) chart.ShowHighLowLines = true;
                        if (group.Element(ChartNs + "upDownBars") is not null) chart.ShowUpDownBars = true;
                    }
                }

                foreach (var ser in group.Elements(ChartNs + "ser"))
                {
                    var series = new ChartSeries
                    {
                        Name = SeriesName(ser.Element(ChartNs + "tx")) ?? string.Empty
                    };

                    if (kind != chart.Kind) series.Kind = kind;
                    series.SecondaryAxis = onSecondary;

                    var spPr = ser.Element(ChartNs + "spPr");
                    // Цвет ряда: у линии — цвет линии или маркера, у столбцов и областей —
                    // заливка. У долей круговой своего цвета ряда нет: обводка долей там
                    // белая и цветом ряда не является.
                    series.ColorHex = kind switch
                    {
                        ChartKind.Radar when chart.RadarFilled
                            => FillColor(spPr, theme) ?? LineColor(spPr, theme),
                        ChartKind.Line or ChartKind.Scatter or ChartKind.Radar or ChartKind.Stock
                            => LineColor(spPr, theme) ?? MarkerColor(ser, theme) ?? FillColor(spPr, theme),
                        ChartKind.Pie or ChartKind.Doughnut => FillColor(spPr, theme),
                        _ => FillColor(spPr, theme)
                    };

                    if (kind == ChartKind.Bubble)
                    {
                        series.XValues = ReadNumbers(ser.Element(ChartNs + "xVal"));
                        series.Values = ReadNumbers(ser.Element(ChartNs + "yVal")) ?? new List<double?>();
                        series.BubbleSizes = ReadNumbers(ser.Element(ChartNs + "bubbleSize"));
                    }
                    else if (kind == ChartKind.Scatter)
                    {
                        series.XValues = ReadNumbers(ser.Element(ChartNs + "xVal"));
                        series.Values = ReadNumbers(ser.Element(ChartNs + "yVal")) ?? new List<double?>();

                        // Стиль группы — по умолчанию, у ряда своё: линия скрыта заливкой
                        // «нет», маркер — символом «none».
                        series.ShowLine = scatterStyle != "marker" && !LineIsHidden(spPr);
                        series.Smooth = series.ShowLine
                            && (scatterStyle.StartsWith("smooth", StringComparison.Ordinal)
                                || Val(ser.Element(ChartNs + "smooth")) is "1" or "true");
                        series.ShowMarkers = !MarkerHidden(ser)
                            && (scatterStyle is not ("line" or "smooth") || ser.Element(ChartNs + "marker") is not null);
                        if (!series.ShowLine) series.ShowMarkers = true;
                    }
                    else
                    {
                        series.Values = ReadNumbers(ser.Element(ChartNs + "val")) ?? new List<double?>();

                        if (chart.Categories.Count == 0)
                        {
                            var categories = ReadCategories(ser.Element(ChartNs + "cat"));
                            if (categories is not null) chart.Categories.AddRange(categories);
                        }

                        if (kind == ChartKind.Line)
                        {
                            series.Smooth = Val(ser.Element(ChartNs + "smooth")) is "1" or "true";
                            series.ShowMarkers = groupMarkers && !MarkerHidden(ser);
                            series.ShowLine = !LineIsHidden(spPr);
                            if (!series.ShowLine) series.ShowMarkers = true;
                        }
                        else if (kind == ChartKind.Radar)
                        {
                            series.ShowMarkers = !MarkerHidden(ser)
                                && Val(group.Element(ChartNs + "radarStyle")) == "marker";
                            series.ShowLine = true;
                        }
                        else if (kind == ChartKind.Stock)
                        {
                            series.ShowLine = false;
                            series.ShowMarkers = !MarkerHidden(ser);
                        }
                    }

                    // Доли круговой диаграммы и столбцы с «разными цветами» — свои цвета точек.
                    foreach (var point in ser.Elements(ChartNs + "dPt"))
                    {
                        int? index = IntVal(point.Element(ChartNs + "idx"));
                        if (index is not int i || i < 0 || i > 10000) continue;

                        string? color = FillColor(point.Element(ChartNs + "spPr"), theme);
                        if (color is null) continue;

                        series.PointColors ??= new List<string?>();
                        while (series.PointColors.Count <= i) series.PointColors.Add(null);
                        series.PointColors[i] = color;
                    }

                    if (ShowsValues(ser.Element(ChartNs + "dLbls"))) anyLabels = true;

                    series.Trendline = ReadTrendline(ser.Element(ChartNs + "trendline"));
                    series.ErrorBars = ReadErrorBars(ser.Elements(ChartNs + "errBars")
                        .FirstOrDefault(e => Val(e.Element(ChartNs + "errDir")) is null or "y"));

                    if (chart.NumberFormat is null)
                    {
                        string? format = (string?)ser.Element(ChartNs + kind switch
                        {
                            ChartKind.Scatter or ChartKind.Bubble => "yVal",
                            _ => "val"
                        })?.Descendants(ChartNs + "formatCode").FirstOrDefault();
                        if (!string.IsNullOrEmpty(format) && format != "General") chart.NumberFormat = format;
                    }

                    chart.Series.Add(series);
                }
            }

            if (!mainKindSet) throw new FormatException("Вид диаграммы не поддерживается.");

            chart.ShowDataLabels = anyLabels;

            // Название: своё, по умолчанию или никакого.
            var title = chartElement.Element(ChartNs + "title");
            bool autoTitleDeleted = Val(chartElement.Element(ChartNs + "autoTitleDeleted")) is "1" or "true";
            if (title is not null)
            {
                chart.ShowTitle = true;
                chart.Title = RichText(title.Element(ChartNs + "tx")) ?? string.Empty;
            }
            else
            {
                // Без элемента названия Word сам ставит имя единственного ряда, пока
                // автоматическое название не удалено.
                chart.ShowTitle = !autoTitleDeleted && chart.Series.Count == 1;
                chart.Title = string.Empty;
            }

            var legend = chartElement.Element(ChartNs + "legend");
            chart.Legend = legend is null
                ? ChartLegendPosition.None
                : (Val(legend.Element(ChartNs + "legendPos")) ?? "r") switch
                {
                    "b" => ChartLegendPosition.Bottom,
                    "t" => ChartLegendPosition.Top,
                    "l" => ChartLegendPosition.Left,
                    _ => ChartLegendPosition.Right
                };

            ReadAxes(chart, plotArea);

            // Рамка всей диаграммы.
            var spaceShape = space.Element(ChartNs + "spPr");
            var outline = spaceShape?.Element(DrawingNs + "ln");
            chart.ShowBorder = outline is null
                ? spaceShape is null
                : outline.Element(DrawingNs + "noFill") is null;

            // Шрифт надписей, если он задан явно, а не шрифтом темы.
            string? typeface = space.Element(ChartNs + "txPr")?
                .Descendants(DrawingNs + "latin").FirstOrDefault()?.Attribute("typeface")?.Value;
            if (!string.IsNullOrEmpty(typeface) && !typeface.StartsWith("+", StringComparison.Ordinal))
                chart.FontFamily = typeface;

            ReadTextStyles(chart, chartElement, plotArea, theme);

            // Место подписей значений: у группы или у первого ряда, где оно задано.
            string? labelPosition = plotArea.Descendants(ChartNs + "dLbls")
                .Select(l => Val(l.Element(ChartNs + "dLblPos")))
                .FirstOrDefault(v => v is not null);
            chart.DataLabelPosition = labelPosition switch
            {
                "ctr" => ChartLabelPosition.Center,
                "inEnd" => ChartLabelPosition.InsideEnd,
                "inBase" => ChartLabelPosition.InsideBase,
                "outEnd" => ChartLabelPosition.OutsideEnd,
                "t" => ChartLabelPosition.Above,
                "b" => ChartLabelPosition.Below,
                "l" => ChartLabelPosition.Left,
                "r" => ChartLabelPosition.Right,
                "bestFit" => ChartLabelPosition.BestFit,
                _ => ChartLabelPosition.Auto
            };

            // Таблица данных под областью построения.
            if (plotArea.Element(ChartNs + "dTable") is { } dataTable)
            {
                chart.ShowDataTable = true;
                chart.HideDataTableKeys = Val(dataTable.Element(ChartNs + "showKeys")) is "0" or "false";
            }

            // Фон всей диаграммы и области построения. Белый фон диаграммы и пустая
            // область — как у Word по умолчанию, в модели не хранятся.
            string? background = spaceShape?.Element(DrawingNs + "noFill") is not null
                ? "#00000000"
                : FillColor(spaceShape, theme);
            chart.BackgroundFill = background is null || IsWhite(background) ? null : background;
            chart.PlotAreaFill = FillColor(plotArea.Element(ChartNs + "spPr"), theme);

            return chart;
        }

        /// <summary>Линия тренда ряда (c:trendline). Null — её нет.</summary>
        private static ChartTrendline? ReadTrendline(XElement? element)
        {
            if (element is null) return null;
            var trendline = new ChartTrendline
            {
                Type = Val(element.Element(ChartNs + "trendlineType")) switch
                {
                    "exp" => ChartTrendlineType.Exponential,
                    "log" => ChartTrendlineType.Logarithmic,
                    "poly" => ChartTrendlineType.Polynomial,
                    "power" => ChartTrendlineType.Power,
                    "movingAvg" => ChartTrendlineType.MovingAverage,
                    _ => ChartTrendlineType.Linear
                },
                Order = Math.Clamp(IntVal(element.Element(ChartNs + "order")) ?? 2, 2, 6),
                Period = Math.Max(2, IntVal(element.Element(ChartNs + "period")) ?? 2),
                Forward = Math.Max(0, DoubleVal(element.Element(ChartNs + "forward")) ?? 0),
                Backward = Math.Max(0, DoubleVal(element.Element(ChartNs + "backward")) ?? 0),
                ShowEquation = Val(element.Element(ChartNs + "dispEq")) is "1" or "true",
                ShowRSquared = Val(element.Element(ChartNs + "dispRSqr")) is "1" or "true"
            };
            return trendline;
        }

        /// <summary>Планки погрешностей ряда (c:errBars). Null — их нет или их величина задана ячейками.</summary>
        private static ChartErrorBars? ReadErrorBars(XElement? element)
        {
            if (element is null) return null;
            var type = Val(element.Element(ChartNs + "errValType")) switch
            {
                "percentage" => ChartErrorBarType.Percentage,
                "stdDev" => ChartErrorBarType.StandardDeviation,
                "fixedVal" => ChartErrorBarType.FixedValue,
                "stdErr" => ChartErrorBarType.StandardError,
                _ => (ChartErrorBarType?)null
            };
            if (type is null) return null;
            return new ChartErrorBars
            {
                Type = type.Value,
                Value = DoubleVal(element.Element(ChartNs + "val")) ?? ChartErrorBars.DefaultValue(type.Value),
                NoEndCap = Val(element.Element(ChartNs + "noEndCap")) is "1" or "true"
            };
        }

        private static bool IsWhite(string color)
            => !ChartModel.IsGradient(color) && ChartModel.ParseArgb(color) is (255, 255, 255, 255);

        /// <summary>
        /// Оформление надписей по родам: название, легенда, подписи и названия осей,
        /// подписи значений. Совпадающее с оформлением Word по умолчанию не хранится.
        /// </summary>
        private static void ReadTextStyles(ChartModel chart, XElement chartElement, XElement plotArea, IReadOnlyDictionary<string, string> theme)
        {
            var title = chartElement.Element(ChartNs + "title");
            chart.TitleStyle = TextStyleOf(title, ChartTextRole.Title, theme);
            chart.LegendStyle = TextStyleOf(chartElement.Element(ChartNs + "legend"), ChartTextRole.Legend, theme);

            var axes = plotArea.Elements().Where(e => e.Name.LocalName is "catAx" or "valAx" or "dateAx" or "serAx").ToList();
            chart.AxisLabelStyle = axes
                .Select(a => TextStyleOf(a, ChartTextRole.AxisLabels, theme))
                .FirstOrDefault(s => s is not null);
            chart.AxisTitleStyle = axes
                .Select(a => TextStyleOf(a.Element(ChartNs + "title"), ChartTextRole.AxisTitles, theme))
                .FirstOrDefault(s => s is not null);

            var labels = plotArea.Descendants(ChartNs + "dLbls").Where(l => l.Element(ChartNs + "txPr") is not null).ToList();
            chart.DataLabelStyle = labels
                .Select(l => TextStyleOf(l, ChartTextRole.DataLabels, theme))
                .FirstOrDefault(s => s is not null);
        }

        /// <summary>
        /// Оформление надписей элемента: c:txPr, у названий — ещё c:tx/c:rich (свойства
        /// первого абзаца и первого куска текста поверх них).
        /// </summary>
        private static ChartTextStyle? TextStyleOf(XElement? owner, ChartTextRole role, IReadOnlyDictionary<string, string> theme)
        {
            if (owner is null) return null;

            var properties = new List<XElement>();
            if (owner.Element(ChartNs + "txPr")?.Descendants(DrawingNs + "defRPr").FirstOrDefault() is { } txDefaults)
                properties.Add(txDefaults);
            if (owner.Element(ChartNs + "tx")?.Element(ChartNs + "rich") is { } rich)
            {
                var paragraph = rich.Element(DrawingNs + "p");
                if (paragraph?.Element(DrawingNs + "pPr")?.Element(DrawingNs + "defRPr") is { } richDefaults)
                    properties.Add(richDefaults);
                if (paragraph?.Element(DrawingNs + "r")?.Element(DrawingNs + "rPr") is { } runProperties)
                    properties.Add(runProperties);
            }
            if (properties.Count == 0) return null;

            var style = new ChartTextStyle();
            foreach (var rPr in properties)
            {
                if ((int?)rPr.Attribute("sz") is int size && size > 0) style.SizePt = size / 100.0;
                if ((string?)rPr.Attribute("b") is { } bold) style.Bold = bold is "1" or "true";
                if ((string?)rPr.Attribute("i") is { } italic) style.Italic = italic is "1" or "true";
                if (rPr.Element(DrawingNs + "solidFill") is { } fill && ColorOf(fill, theme) is { } color) style.ColorHex = color;
                else if (rPr.Element(DrawingNs + "gradFill") is { } gradient && GradientOf(gradient, theme) is { } gradientColor) style.ColorHex = gradientColor;
                string? face = (string?)rPr.Element(DrawingNs + "latin")?.Attribute("typeface");
                if (!string.IsNullOrEmpty(face) && !face.StartsWith("+", StringComparison.Ordinal)) style.FontFamily = face;
            }

            // Что совпадает с оформлением по умолчанию — не задано.
            if (style.SizePt is double pt && Math.Abs(pt - ChartModel.DefaultSizeOf(role)) < 0.01) style.SizePt = null;
            if (style.Bold == ChartModel.DefaultBoldOf(role)) style.Bold = null;
            if (style.Italic == false) style.Italic = null;
            if (style.ColorHex is { } hex && hex.TrimStart('#').ToUpperInvariant() is "595959" or "404040") style.ColorHex = null;

            return ChartTextStyle.Normalize(style);
        }

        private static ChartKind? GroupKind(XElement group) => group.Name.LocalName switch
        {
            "barChart" or "bar3DChart" => Val(group.Element(ChartNs + "barDir")) == "bar" ? ChartKind.Bar : ChartKind.Column,
            "lineChart" or "line3DChart" => ChartKind.Line,
            "radarChart" => ChartKind.Radar,
            "stockChart" => ChartKind.Stock,
            "pieChart" or "pie3DChart" or "ofPieChart" => ChartKind.Pie,
            "doughnutChart" => ChartKind.Doughnut,
            "areaChart" or "area3DChart" or "surfaceChart" or "surface3DChart" => ChartKind.Area,
            "scatterChart" => ChartKind.Scatter,
            "bubbleChart" => ChartKind.Bubble,
            _ => null
        };

        /// <summary>
        /// Номер вспомогательной оси значений осевой диаграммы: вторая ось значений,
        /// пересекающая свою ось категорий в конце (справа). Null — её нет.
        /// </summary>
        private static string? SecondaryValueAxisId(XElement plotArea)
        {
            if (plotArea.Element(ChartNs + "catAx") is null && plotArea.Element(ChartNs + "dateAx") is null) return null;

            var valueAxes = plotArea.Elements(ChartNs + "valAx").ToList();
            if (valueAxes.Count < 2) return null;

            var secondary = valueAxes.FirstOrDefault(a => Val(a.Element(ChartNs + "crosses")) == "max")
                ?? valueAxes.Skip(1).FirstOrDefault(a => Val(a.Element(ChartNs + "axPos")) == "r");
            return secondary is null ? null : Val(secondary.Element(ChartNs + "axId"));
        }

        /// <summary>Оси: подписи, названия, сетка, заданные пределы.</summary>
        private static void ReadAxes(ChartModel chart, XElement plotArea)
        {
            XElement? categoryAxis;
            XElement? valueAxis;

            string? secondaryId = chart.IsXY ? null : SecondaryValueAxisId(plotArea);
            var allValueAxes = plotArea.Elements(ChartNs + "valAx").ToList();
            var secondaryAxis = secondaryId is null
                ? null
                : allValueAxes.FirstOrDefault(a => Val(a.Element(ChartNs + "axId")) == secondaryId);
            var valueAxes = allValueAxes.Where(a => !ReferenceEquals(a, secondaryAxis)).ToList();

            if (secondaryAxis is not null)
            {
                chart.SecondaryAxisTitle = AxisTitle(secondaryAxis);
                var secondaryScaling = secondaryAxis.Element(ChartNs + "scaling");
                chart.SecondaryAxisMin = DoubleVal(secondaryScaling?.Element(ChartNs + "min"));
                chart.SecondaryAxisMax = DoubleVal(secondaryScaling?.Element(ChartNs + "max"));
                var secondaryFormat = secondaryAxis.Element(ChartNs + "numFmt");
                string? code = (string?)secondaryFormat?.Attribute("formatCode");
                bool secondaryLinked = (string?)secondaryFormat?.Attribute("sourceLinked") is "1" or "true";
                if (!secondaryLinked && !string.IsNullOrEmpty(code) && code != "General"
                    && chart.Grouping != ChartGrouping.PercentStacked)
                    chart.SecondaryNumberFormat = code;
            }

            if (chart.IsXY)
            {
                // У точечной и пузырьковой обе оси — оси значений: X внизу, Y слева.
                categoryAxis = valueAxes.FirstOrDefault(a => Val(a.Element(ChartNs + "axPos")) is "b" or "t");
                valueAxis = valueAxes.FirstOrDefault(a => !ReferenceEquals(a, categoryAxis));
            }
            else
            {
                // Ось категорий основной оси значений: своя ось категорий есть и у
                // вспомогательной, обычно скрытая.
                var categoryAxes = plotArea.Elements(ChartNs + "catAx").Concat(plotArea.Elements(ChartNs + "dateAx")).ToList();
                categoryAxis = categoryAxes.FirstOrDefault(a => secondaryId is null || Val(a.Element(ChartNs + "crossAx")) != secondaryId)
                    ?? categoryAxes.FirstOrDefault();
                valueAxis = valueAxes.FirstOrDefault();
            }

            if (categoryAxis is not null)
            {
                chart.HideCategoryAxis = Val(categoryAxis.Element(ChartNs + "delete")) is "1" or "true";
                chart.CategoryAxisTitle = AxisTitle(categoryAxis);

                // Ось X точечной и пузырьковой — ось значений: свои пределы и формат чисел.
                if (chart.IsXY)
                {
                    var xScaling = categoryAxis.Element(ChartNs + "scaling");
                    chart.XAxisMin = DoubleVal(xScaling?.Element(ChartNs + "min"));
                    chart.XAxisMax = DoubleVal(xScaling?.Element(ChartNs + "max"));
                    var xFormat = categoryAxis.Element(ChartNs + "numFmt");
                    string? xCode = (string?)xFormat?.Attribute("formatCode");
                    bool xLinked = (string?)xFormat?.Attribute("sourceLinked") is "1" or "true";
                    if (!xLinked && !string.IsNullOrEmpty(xCode) && xCode != "General") chart.XNumberFormat = xCode;
                }

                // У лепестковой линии сетки оси категорий — её лучи, они идут с сеткой значений.
                if (chart.Kind != ChartKind.Radar)
                {
                    chart.ShowCategoryGridlines = categoryAxis.Element(ChartNs + "majorGridlines") is not null;
                    chart.ShowCategoryMinorGridlines = categoryAxis.Element(ChartNs + "minorGridlines") is not null;
                }
            }

            if (valueAxis is not null)
            {
                chart.HideValueAxis = Val(valueAxis.Element(ChartNs + "delete")) is "1" or "true";
                chart.ValueAxisTitle = AxisTitle(valueAxis);
                chart.ShowGridlines = valueAxis.Element(ChartNs + "majorGridlines") is not null;
                chart.ShowMinorGridlines = valueAxis.Element(ChartNs + "minorGridlines") is not null;

                var scaling = valueAxis.Element(ChartNs + "scaling");
                chart.ValueAxisMin = DoubleVal(scaling?.Element(ChartNs + "min"));
                chart.ValueAxisMax = DoubleVal(scaling?.Element(ChartNs + "max"));

                var numberFormat = valueAxis.Element(ChartNs + "numFmt");
                string? axisFormat = (string?)numberFormat?.Attribute("formatCode");
                bool linked = (string?)numberFormat?.Attribute("sourceLinked") is "1" or "true";
                // У нормированной диаграммы проценты на оси — свойство самой оси, а не
                // данных: подписи значений показывают исходные числа, не проценты.
                if (!linked && !string.IsNullOrEmpty(axisFormat) && axisFormat != "General"
                    && chart.Grouping != ChartGrouping.PercentStacked)
                    chart.NumberFormat = axisFormat;
            }
            else
            {
                chart.ShowGridlines = false;
            }
        }

        private static string? AxisTitle(XElement axis)
        {
            var title = axis.Element(ChartNs + "title");
            if (title is null) return null;
            return RichText(title.Element(ChartNs + "tx")) ?? "Название оси";
        }

        /// <summary>Текст из c:tx: форматированный (c:rich) или ссылка на ячейку (c:strRef).</summary>
        private static string? RichText(XElement? tx)
        {
            if (tx is null) return null;

            var rich = tx.Element(ChartNs + "rich");
            if (rich is not null)
            {
                var lines = new List<string>();
                foreach (var paragraph in rich.Elements(DrawingNs + "p"))
                {
                    var line = new StringBuilder();
                    foreach (var text in paragraph.Descendants(DrawingNs + "t"))
                        line.Append(text.Value);
                    lines.Add(line.ToString());
                }
                string joined = string.Join("\n", lines).Trim('\n');
                return joined.Length == 0 ? null : joined;
            }

            return StringValues(tx.Element(ChartNs + "strRef"))?.FirstOrDefault()
                ?? (string?)tx.Element(ChartNs + "v");
        }

        private static string? SeriesName(XElement? tx)
        {
            if (tx is null) return null;
            return StringValues(tx.Element(ChartNs + "strRef"))?.FirstOrDefault()
                ?? (string?)tx.Element(ChartNs + "v")
                ?? RichText(tx);
        }

        /// <summary>Подписи категорий из c:cat: строки, числа или многоуровневые подписи.</summary>
        private static List<string>? ReadCategories(XElement? cat)
        {
            if (cat is null) return null;

            var strings = StringValues(cat.Element(ChartNs + "strRef"))
                ?? StringValues(cat.Element(ChartNs + "strLit"));
            if (strings is not null) return strings;

            var multiLevel = cat.Element(ChartNs + "multiLvlStrRef")?.Element(ChartNs + "multiLvlStrCache")
                ?? cat.Element(ChartNs + "multiLvlStrLit");
            if (multiLevel is not null)
            {
                var level = multiLevel.Elements(ChartNs + "lvl").FirstOrDefault();
                int count = IntVal(multiLevel.Element(ChartNs + "ptCount")) ?? 0;
                if (level is not null) return Points(level, count);
            }

            var numberCache = cat.Element(ChartNs + "numRef")?.Element(ChartNs + "numCache")
                ?? cat.Element(ChartNs + "numLit");
            if (numberCache is not null)
            {
                string? format = (string?)numberCache.Element(ChartNs + "formatCode");
                int count = IntVal(numberCache.Element(ChartNs + "ptCount")) ?? 0;
                var raw = Points(numberCache, count);
                var labels = new List<string>(raw.Count);
                foreach (var value in raw)
                    labels.Add(FormatCategoryNumber(value, format));
                return labels;
            }

            return null;
        }

        /// <summary>Число категории: дата у формата даты, иначе само число.</summary>
        private static string FormatCategoryNumber(string value, string? format)
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                return value;

            if (!string.IsNullOrEmpty(format) && IsDateFormat(format))
            {
                try
                {
                    return DateTime.FromOADate(number).ToString("d", CultureInfo.CurrentCulture);
                }
                catch (ArgumentException)
                {
                    return value;
                }
            }

            return number.ToString(CultureInfo.CurrentCulture);
        }

        private static bool IsDateFormat(string format)
        {
            string lower = format.ToLowerInvariant();
            return (lower.Contains('d') || lower.Contains('y') || lower.Contains("mmm"))
                && !lower.Contains('0') && !lower.Contains('#');
        }

        /// <summary>Строки из c:strRef (кэш значений) или c:strLit.</summary>
        private static List<string>? StringValues(XElement? reference)
        {
            if (reference is null) return null;

            var cache = reference.Name.LocalName == "strRef"
                ? reference.Element(ChartNs + "strCache")
                : reference;
            if (cache is null) return null;

            int count = IntVal(cache.Element(ChartNs + "ptCount")) ?? 0;
            return Points(cache, count);
        }

        /// <summary>Значения c:pt по их номерам; пропущенные номера — пустые строки.</summary>
        private static List<string> Points(XElement cache, int count)
        {
            var result = new List<string>();
            foreach (var point in cache.Elements(ChartNs + "pt"))
            {
                int index = (int?)point.Attribute("idx") ?? result.Count;
                if (index < 0 || index > 100000) continue;
                while (result.Count <= index) result.Add(string.Empty);
                result[index] = (string?)point.Element(ChartNs + "v") ?? string.Empty;
            }
            while (result.Count < count && count <= 100000) result.Add(string.Empty);
            return result;
        }

        /// <summary>Числа ряда из c:val, c:xVal или c:yVal; пустая ячейка — null.</summary>
        private static List<double?>? ReadNumbers(XElement? container)
        {
            if (container is null) return null;

            var cache = container.Element(ChartNs + "numRef")?.Element(ChartNs + "numCache")
                ?? container.Element(ChartNs + "numLit");

            if (cache is null)
            {
                // Подписи вместо чисел (X точечной диаграммы текстом): Word ставит точки
                // по порядку — 1, 2, 3.
                var strings = StringValues(container.Element(ChartNs + "strRef"))
                    ?? StringValues(container.Element(ChartNs + "strLit"));
                if (strings is null) return null;

                var ordinal = new List<double?>(strings.Count);
                for (int i = 0; i < strings.Count; i++) ordinal.Add(i + 1);
                return ordinal;
            }

            int count = IntVal(cache.Element(ChartNs + "ptCount")) ?? 0;
            var values = new List<double?>();
            foreach (var point in cache.Elements(ChartNs + "pt"))
            {
                int index = (int?)point.Attribute("idx") ?? values.Count;
                if (index < 0 || index > 100000) continue;
                while (values.Count <= index) values.Add(null);

                values[index] = double.TryParse(
                    (string?)point.Element(ChartNs + "v"), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                    ? v
                    : null;
            }
            while (values.Count < count && count <= 100000) values.Add(null);
            return values;
        }

        private static bool ShowsValues(XElement? labels)
        {
            if (labels is null) return false;
            if (Val(labels.Element(ChartNs + "delete")) is "1" or "true") return false;
            return Val(labels.Element(ChartNs + "showVal")) is "1" or "true"
                || Val(labels.Element(ChartNs + "showPercent")) is "1" or "true";
        }

        private static bool MarkerHidden(XElement ser)
            => Val(ser.Element(ChartNs + "marker")?.Element(ChartNs + "symbol")) == "none";

        private static bool LineIsHidden(XElement? spPr)
            => spPr?.Element(DrawingNs + "ln")?.Element(DrawingNs + "noFill") is not null;

        private static string? MarkerColor(XElement ser, IReadOnlyDictionary<string, string> theme)
            => FillColor(ser.Element(ChartNs + "marker")?.Element(ChartNs + "spPr"), theme);

        /// <summary>Цвет заливки из c:spPr: сплошная или первая точка градиента.</summary>
        private static string? FillColor(XElement? spPr, IReadOnlyDictionary<string, string> theme)
        {
            if (spPr is null) return null;

            var solid = spPr.Element(DrawingNs + "solidFill");
            if (solid is not null) return ColorOf(solid, theme);

            var gradient = spPr.Element(DrawingNs + "gradFill");
            if (gradient is not null) return GradientOf(gradient, theme);

            return null;
        }

        /// <summary>
        /// Градиент DrawingML (a:gradFill) кодом GradientSpec «grad|…» — тем, что
        /// понимает стандартный подборщик цвета: линейный (a:lin, угол Word идёт по
        /// часовой, у подборщика — против), из центра (a:path circle и rect) —
        /// радиальный, по форме (a:path shape) — конический. Один цвет — обычный цвет.
        /// </summary>
        private static string? GradientOf(XElement gradient, IReadOnlyDictionary<string, string> theme)
        {
            var spec = new Writersword.Core.Models.Project.GradientSpec();
            foreach (var gs in gradient.Element(DrawingNs + "gsLst")?.Elements(DrawingNs + "gs") ?? Enumerable.Empty<XElement>())
            {
                string? hex = ColorOf(gs, theme);
                if (hex is null) continue;
                double position = Math.Clamp(((int?)gs.Attribute("pos") ?? 0) / 100000.0, 0.0, 1.0);
                spec.Stops.Add(new Writersword.Core.Models.Project.GradientColorStop(position, hex));
            }
            if (spec.Stops.Count == 0) return null;

            var path = gradient.Element(DrawingNs + "path");
            if (path is not null)
            {
                spec.Kind = (string?)path.Attribute("path") == "shape"
                    ? Writersword.Core.Models.Project.GradientKind.Conic
                    : Writersword.Core.Models.Project.GradientKind.Radial;
            }
            else
            {
                double word = ((int?)gradient.Element(DrawingNs + "lin")?.Attribute("ang") ?? 0) / 60000.0;
                spec.Kind = Writersword.Core.Models.Project.GradientKind.Linear;
                spec.AngleDeg = ((360.0 - word) % 360.0 + 360.0) % 360.0;
            }

            return spec.ToCode();
        }

        /// <summary>Цвет линии из c:spPr/a:ln.</summary>
        private static string? LineColor(XElement? spPr, IReadOnlyDictionary<string, string> theme)
        {
            var line = spPr?.Element(DrawingNs + "ln");
            if (line is null) return null;

            var solid = line.Element(DrawingNs + "solidFill");
            if (solid is not null) return ColorOf(solid, theme);

            var gradient = line.Element(DrawingNs + "gradFill");
            return gradient is null ? null : GradientOf(gradient, theme);
        }

        /// <summary>
        /// Цвет DrawingML: srgbClr, schemeClr (цвет темы), sysClr или prstClr — с
        /// оттенками lumMod, lumOff, shade и tint.
        /// </summary>
        private static string? ColorOf(XElement parent, IReadOnlyDictionary<string, string> theme)
        {
            var element = parent.Elements().FirstOrDefault(e =>
                e.Name.LocalName is "srgbClr" or "schemeClr" or "sysClr" or "prstClr" or "scrgbClr");
            if (element is null) return null;

            string? hex = element.Name.LocalName switch
            {
                "srgbClr" => (string?)element.Attribute("val"),
                "sysClr" => (string?)element.Attribute("lastClr") ?? ((string?)element.Attribute("val") == "window" ? "FFFFFF" : "000000"),
                "schemeClr" => SchemeColor((string?)element.Attribute("val"), theme),
                "prstClr" => PresetColor((string?)element.Attribute("val")),
                "scrgbClr" => ScRgb(element),
                _ => null
            };
            if (hex is not { Length: 6 }) return null;

            var (r, g, b) = ChartModel.ParseHex(hex);
            double red = r / 255.0, green = g / 255.0, blue = b / 255.0;

            foreach (var modifier in element.Elements())
            {
                double amount = ((int?)modifier.Attribute("val") ?? 100000) / 100000.0;
                switch (modifier.Name.LocalName)
                {
                    case "shade":
                        red *= amount; green *= amount; blue *= amount;
                        break;
                    case "tint":
                        red += (1 - red) * (1 - amount);
                        green += (1 - green) * (1 - amount);
                        blue += (1 - blue) * (1 - amount);
                        break;
                }
            }

            double? lumMod = (int?)element.Element(DrawingNs + "lumMod")?.Attribute("val") / 100000.0;
            double? lumOff = (int?)element.Element(DrawingNs + "lumOff")?.Attribute("val") / 100000.0;
            if (lumMod is not null || lumOff is not null)
                (red, green, blue) = AdjustLightness(red, green, blue, lumMod ?? 1.0, lumOff ?? 0.0);

            static int Byte(double v) => (int)Math.Round(Math.Clamp(v, 0.0, 1.0) * 255.0);

            // Прозрачность цвета (a:alpha) — в виде #AARRGGBB, как у подборщика цвета.
            int? alpha = (int?)element.Element(DrawingNs + "alpha")?.Attribute("val");
            if (alpha is int a && a < 100000)
                return string.Create(CultureInfo.InvariantCulture, $"#{Byte(a / 100000.0):X2}{Byte(red):X2}{Byte(green):X2}{Byte(blue):X2}");

            return string.Create(CultureInfo.InvariantCulture, $"#{Byte(red):X2}{Byte(green):X2}{Byte(blue):X2}");
        }

        private static string? SchemeColor(string? name, IReadOnlyDictionary<string, string> theme)
        {
            string key = name switch
            {
                "tx1" => "dk1",
                "bg1" => "lt1",
                "tx2" => "dk2",
                "bg2" => "lt2",
                "phClr" or null => "accent1",
                _ => name
            };
            return theme.TryGetValue(key, out var hex) ? hex : null;
        }

        private static string? PresetColor(string? name) => name switch
        {
            "black" => "000000",
            "white" => "FFFFFF",
            "red" => "FF0000",
            "green" => "008000",
            "blue" => "0000FF",
            "yellow" => "FFFF00",
            "gray" or "grey" => "808080",
            "orange" => "FFA500",
            _ => null
        };

        private static string? ScRgb(XElement element)
        {
            static int Channel(XElement e, string name)
                => (int)Math.Round(Math.Clamp(((int?)e.Attribute(name) ?? 0) / 100000.0, 0.0, 1.0) * 255.0);
            return string.Create(CultureInfo.InvariantCulture,
                $"{Channel(element, "r"):X2}{Channel(element, "g"):X2}{Channel(element, "b"):X2}");
        }

        /// <summary>Яркость в HSL: умножение на lumMod и сдвиг на lumOff.</summary>
        private static (double R, double G, double B) AdjustLightness(
            double r, double g, double b, double lumMod, double lumOff)
        {
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double l = (max + min) / 2.0;
            double h = 0, s = 0;

            if (max - min > 1e-9)
            {
                double d = max - min;
                s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
                if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
                else if (max == g) h = (b - r) / d + 2;
                else h = (r - g) / d + 4;
                h /= 6.0;
            }

            l = Math.Clamp(l * lumMod + lumOff, 0.0, 1.0);
            if (s <= 1e-9) return (l, l, l);

            double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            double p = 2 * l - q;

            static double Hue(double p, double q, double t)
            {
                if (t < 0) t += 1;
                if (t > 1) t -= 1;
                if (t < 1.0 / 6) return p + (q - p) * 6 * t;
                if (t < 0.5) return q;
                if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
                return p;
            }

            return (Hue(p, q, h + 1.0 / 3), Hue(p, q, h), Hue(p, q, h - 1.0 / 3));
        }

        private static string? Val(XElement? element) => (string?)element?.Attribute("val");

        private static int? IntVal(XElement? element)
            => int.TryParse(Val(element), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : null;

        private static double? DoubleVal(XElement? element)
            => double.TryParse(Val(element), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
    }
}
