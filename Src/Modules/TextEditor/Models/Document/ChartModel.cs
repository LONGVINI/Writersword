using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Writersword.Core.Models.Project;

namespace Writersword.Modules.TextEditor.Models.Document
{
    /// <summary>Вид диаграммы — те же основные виды, что у Word.</summary>
    public enum ChartKind
    {
        /// <summary>Гистограмма: вертикальные столбцы.</summary>
        Column = 0,

        /// <summary>Линейчатая: горизонтальные полосы.</summary>
        Bar = 1,

        /// <summary>График: линии по точкам.</summary>
        Line = 2,

        /// <summary>Круговая.</summary>
        Pie = 3,

        /// <summary>Кольцевая.</summary>
        Doughnut = 4,

        /// <summary>С областями.</summary>
        Area = 5,

        /// <summary>Точечная: пары X и Y.</summary>
        Scatter = 6,

        /// <summary>Лепестковая: категории — лучи из центра, ряды — замкнутые линии.</summary>
        Radar = 7,

        /// <summary>Пузырьковая: X, Y и размер пузырька.</summary>
        Bubble = 8,

        /// <summary>
        /// Биржевая: ряды по порядку — открытие (необязательно), максимум, минимум,
        /// закрытие. Три ряда — «максимум — минимум — закрытие», четыре — с открытием.
        /// </summary>
        Stock = 9
    }

    /// <summary>Как ряды стоят друг относительно друга.</summary>
    public enum ChartGrouping
    {
        /// <summary>Рядом (у столбцов) или каждый сам по себе (у линий и областей).</summary>
        Standard = 0,

        /// <summary>С накоплением: ряды ставятся друг на друга.</summary>
        Stacked = 1,

        /// <summary>Нормированная: с накоплением, и сумма по категории — сто процентов.</summary>
        PercentStacked = 2
    }

    /// <summary>Место легенды.</summary>
    public enum ChartLegendPosition
    {
        None = 0,
        Bottom = 1,
        Right = 2,
        Top = 3,
        Left = 4
    }

    /// <summary>
    /// Где стоят подписи значений, как «Метки данных» у Word. Auto — место Word по
    /// умолчанию для вида: над столбцом, в середине накопленного, над точкой.
    /// </summary>
    public enum ChartLabelPosition
    {
        Auto = 0,
        Center = 1,
        InsideEnd = 2,
        InsideBase = 3,
        OutsideEnd = 4,
        Above = 5,
        Below = 6,
        Left = 7,
        Right = 8,
        BestFit = 9
    }

    /// <summary>Вид линии тренда, как у Word.</summary>
    public enum ChartTrendlineType
    {
        Linear = 0,
        Exponential = 1,
        Logarithmic = 2,
        Polynomial = 3,
        Power = 4,
        MovingAverage = 5
    }

    /// <summary>
    /// Линия тренда ряда: вид, степень многочлена, период скользящего среднего, прогноз
    /// вперёд и назад (в шагах категорий или единицах X) и подписи уравнения и R².
    /// </summary>
    public sealed class ChartTrendline
    {
        public ChartTrendlineType Type { get; set; }

        /// <summary>Степень многочлена (2–6).</summary>
        public int Order { get; set; } = 2;

        /// <summary>Период скользящего среднего (2 и больше).</summary>
        public int Period { get; set; } = 2;

        /// <summary>Прогноз вперёд: на сколько продлить линию за последнюю точку.</summary>
        public double Forward { get; set; }

        /// <summary>Прогноз назад: на сколько продлить линию до первой точки.</summary>
        public double Backward { get; set; }

        /// <summary>Показывать уравнение на диаграмме.</summary>
        public bool ShowEquation { get; set; }

        /// <summary>Показывать величину достоверности аппроксимации R².</summary>
        public bool ShowRSquared { get; set; }

        public ChartTrendline Clone() => new()
        {
            Type = Type,
            Order = Order,
            Period = Period,
            Forward = Forward,
            Backward = Backward,
            ShowEquation = ShowEquation,
            ShowRSquared = ShowRSquared
        };
    }

    /// <summary>Вид величины планок погрешностей, как у Word.</summary>
    public enum ChartErrorBarType
    {
        StandardError = 0,
        Percentage = 1,
        StandardDeviation = 2,
        FixedValue = 3
    }

    /// <summary>
    /// Планки погрешностей ряда: величина (стандартная погрешность, процент, число
    /// стандартных отклонений или постоянное значение), в обе стороны, с засечками.
    /// </summary>
    public sealed class ChartErrorBars
    {
        public ChartErrorBarType Type { get; set; }

        /// <summary>Процент (для Percentage), число отклонений (StandardDeviation) или величина (FixedValue).</summary>
        public double Value { get; set; } = 5;

        /// <summary>Без засечек на концах.</summary>
        public bool NoEndCap { get; set; }

        public ChartErrorBars Clone() => new() { Type = Type, Value = Value, NoEndCap = NoEndCap };

        /// <summary>Величина по умолчанию для вида: 5 % у процента, одно отклонение, единица у постоянной.</summary>
        public static double DefaultValue(ChartErrorBarType type) => type switch
        {
            ChartErrorBarType.Percentage => 5,
            ChartErrorBarType.StandardDeviation => 1,
            ChartErrorBarType.FixedValue => 1,
            _ => 0
        };
    }

    /// <summary>Один ряд данных диаграммы.</summary>
    public sealed class ChartSeries
    {
        /// <summary>Имя ряда — подпись в легенде.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Значения по категориям. Null — пустая ячейка: точки нет.</summary>
        public List<double?> Values { get; set; } = new();

        /// <summary>Значения X точечной диаграммы. У прочих видов не нужны.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<double?>? XValues { get; set; }

        /// <summary>Цвет ряда (#RRGGBB). Null — цвет палитры по номеру ряда.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ColorHex { get; set; }

        /// <summary>
        /// Цвета отдельных точек (#RRGGBB или null) — у круговой и кольцевой каждая доля
        /// своим цветом. Null — цвета по палитре.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string?>? PointColors { get; set; }

        /// <summary>
        /// Свой вид ряда в смешанной диаграмме: линия поверх столбцов, как в
        /// комбинированной диаграмме Word. Null — вид всей диаграммы.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ChartKind? Kind { get; set; }

        /// <summary>Сглаженная линия.</summary>
        public bool Smooth { get; set; }

        /// <summary>Маркеры точек у линии.</summary>
        public bool ShowMarkers { get; set; } = true;

        /// <summary>Линия между точками у графика и точечной; без неё — одни маркеры.</summary>
        public bool ShowLine { get; set; } = true;

        /// <summary>Размеры пузырьков пузырьковой диаграммы. У прочих видов не нужны.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<double?>? BubbleSizes { get; set; }

        /// <summary>
        /// Ряд на вспомогательной оси значений — справа, со своей шкалой, как у
        /// комбинированной диаграммы Word. Только у гистограммы, графика и областей.
        /// </summary>
        public bool SecondaryAxis { get; set; }

        /// <summary>Ряд скрыт фильтром диаграммы: данные остаются, на диаграмме его нет.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Hidden { get; set; }

        /// <summary>Линия тренда ряда. Null — нет.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ChartTrendline? Trendline { get; set; }

        /// <summary>Планки погрешностей ряда. Null — нет.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ChartErrorBars? ErrorBars { get; set; }

        public ChartSeries Clone() => new()
        {
            Name = Name,
            Values = new List<double?>(Values),
            XValues = XValues is null ? null : new List<double?>(XValues),
            ColorHex = ColorHex,
            PointColors = PointColors is null ? null : new List<string?>(PointColors),
            Kind = Kind,
            Smooth = Smooth,
            ShowMarkers = ShowMarkers,
            ShowLine = ShowLine,
            BubbleSizes = BubbleSizes is null ? null : new List<double?>(BubbleSizes),
            SecondaryAxis = SecondaryAxis,
            Hidden = Hidden,
            Trendline = Trendline?.Clone(),
            ErrorBars = ErrorBars?.Clone()
        };
    }

    /// <summary>
    /// Оформление надписей одного рода (название, легенда, подписи осей и т.п.):
    /// шрифт, размер, начертание и цвет. Пустое поле — как у диаграммы по умолчанию:
    /// шрифт диаграммы, размер и цвет Word для этих надписей.
    /// </summary>
    public sealed class ChartTextStyle
    {
        /// <summary>Шрифт. Null — шрифт всей диаграммы.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? FontFamily { get; set; }

        /// <summary>Размер, пт. Null — размер Word по умолчанию для этих надписей.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? SizePt { get; set; }

        /// <summary>Жирный. Null — как по умолчанию (названия осей жирные, прочее нет).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Bold { get; set; }

        /// <summary>Курсив. Null — нет.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Italic { get; set; }

        /// <summary>Цвет текста. Null — серый Word.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ColorHex { get; set; }

        /// <summary>Ничего не задано: оформление по умолчанию.</summary>
        [JsonIgnore]
        public bool IsEmpty => FontFamily is null && SizePt is null && Bold is null && Italic is null && ColorHex is null;

        public ChartTextStyle Clone() => new()
        {
            FontFamily = FontFamily,
            SizePt = SizePt,
            Bold = Bold,
            Italic = Italic,
            ColorHex = ColorHex
        };

        /// <summary>
        /// Копия или null, если стиль пуст: пустой стиль в модели не хранится. Пустые
        /// строки шрифта и цвета — то же, что не заданы.
        /// </summary>
        public static ChartTextStyle? Normalize(ChartTextStyle? style)
        {
            if (style is null) return null;
            var copy = style.Clone();
            if (string.IsNullOrWhiteSpace(copy.FontFamily)) copy.FontFamily = null;
            if (string.IsNullOrWhiteSpace(copy.ColorHex)) copy.ColorHex = null;
            return copy.IsEmpty ? null : copy;
        }
    }

    /// <summary>Род надписей диаграммы, у каждого своё оформление.</summary>
    public enum ChartTextRole
    {
        Title,
        Legend,
        AxisLabels,
        AxisTitles,
        DataLabels
    }

    /// <summary>
    /// Части диаграммы Word в том виде, в каком она пришла из .docx: XML самой
    /// диаграммы и связанные с ней части — книга Excel с данными, стиль и цвета.
    ///
    /// Пока диаграмму не правили, в .docx уходят именно они: Word получает обратно свою
    /// диаграмму со всем оформлением, которого модель не знает (тени, градиенты,
    /// подписи по местам), и по-прежнему открывает её данные в Excel. Правка в
    /// редакторе меняет отпечаток модели, и тогда диаграмма пишется заново из модели.
    /// </summary>
    public sealed class WordChartSource
    {
        /// <summary>XML части диаграммы (c:chartSpace).</summary>
        public string ChartXml { get; set; } = string.Empty;

        /// <summary>Отпечаток модели в момент чтения: совпал — диаграмму не правили.</summary>
        public string Fingerprint { get; set; } = string.Empty;

        /// <summary>Связанные части диаграммы.</summary>
        public List<WordChartPart> Parts { get; set; } = new();

        public WordChartSource Clone()
        {
            var clone = new WordChartSource { ChartXml = ChartXml, Fingerprint = Fingerprint };
            foreach (var part in Parts) clone.Parts.Add(part.Clone());
            return clone;
        }
    }

    /// <summary>Часть, связанная с диаграммой Word.</summary>
    public sealed class WordChartPart
    {
        /// <summary>Id связи, по которому XML диаграммы ссылается на часть.</summary>
        public string RelationshipId { get; set; } = string.Empty;

        /// <summary>Тип связи (URI).</summary>
        public string RelationshipType { get; set; } = string.Empty;

        /// <summary>Тип содержимого части.</summary>
        public string ContentType { get; set; } = string.Empty;

        /// <summary>Байты части.</summary>
        public byte[] Data { get; set; } = Array.Empty<byte>();

        public WordChartPart Clone() => new()
        {
            RelationshipId = RelationshipId,
            RelationshipType = RelationshipType,
            ContentType = ContentType,
            Data = Data
        };
    }

    /// <summary>
    /// Диаграмма: вид, данные и оформление. Живёт в картинке (<see cref="ImageBlock.Chart"/>):
    /// место в тексте, обтекание, размер, поворот и выделение у диаграммы те же, что у
    /// картинки, а вместо файла картинки лист рисует диаграмму по этим данным.
    ///
    /// Модель после создания не меняется: окно правки собирает новую, и картинка
    /// получает её целиком. На этом держится кэш нарисованных диаграмм — он узнаёт
    /// диаграмму по самому объекту модели.
    /// </summary>
    public sealed class ChartModel
    {
        /// <summary>Название по умолчанию — как у новой диаграммы Word.</summary>
        public const string DefaultTitle = "Название диаграммы";

        /// <summary>Палитра рядов по умолчанию: цвета темы Office 2013+, акценты 1–6.</summary>
        public static readonly IReadOnlyList<string> Palette = new[]
        {
            "#4472C4", "#ED7D31", "#A5A5A5", "#FFC000", "#5B9BD5", "#70AD47"
        };

        public ChartKind Kind { get; set; } = ChartKind.Column;

        public ChartGrouping Grouping { get; set; } = ChartGrouping.Standard;

        /// <summary>Текст названия. Пустой при показанном названии — название по умолчанию.</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>Показывать название.</summary>
        public bool ShowTitle { get; set; } = true;

        public ChartLegendPosition Legend { get; set; } = ChartLegendPosition.Bottom;

        /// <summary>Подписи значений у точек и столбцов.</summary>
        public bool ShowDataLabels { get; set; }

        /// <summary>Основные линии сетки по оси значений.</summary>
        public bool ShowGridlines { get; set; } = true;

        /// <summary>Тонкая рамка вокруг всей диаграммы, как у новой диаграммы Word.</summary>
        public bool ShowBorder { get; set; } = true;

        /// <summary>Подписи категорий: «Категория 1», «Кв. 1». У точечной не нужны.</summary>
        public List<string> Categories { get; set; } = new();

        public List<ChartSeries> Series { get; set; } = new();

        /// <summary>Название оси категорий. Null — нет.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CategoryAxisTitle { get; set; }

        /// <summary>Название оси значений. Null — нет.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ValueAxisTitle { get; set; }

        /// <summary>Заданный минимум оси значений. Null — подбирается сам.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? ValueAxisMin { get; set; }

        /// <summary>Заданный максимум оси значений. Null — подбирается сам.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? ValueAxisMax { get; set; }

        /// <summary>Формат чисел Excel у значений («0%», «0.0»). Null — общий.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? NumberFormat { get; set; }

        /// <summary>
        /// Заданный минимум оси X у точечной и пузырьковой: у них ось X — тоже ось
        /// значений, со своей шкалой. Null — подбирается сам.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? XAxisMin { get; set; }

        /// <summary>Заданный максимум оси X у точечной и пузырьковой. Null — подбирается сам.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? XAxisMax { get; set; }

        /// <summary>Формат чисел оси X у точечной и пузырьковой. Null — общий.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? XNumberFormat { get; set; }

        /// <summary>Оформление названия диаграммы. Null — по умолчанию.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ChartTextStyle? TitleStyle { get; set; }

        /// <summary>Оформление легенды. Null — по умолчанию.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ChartTextStyle? LegendStyle { get; set; }

        /// <summary>Оформление подписей делений и категорий осей. Null — по умолчанию.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ChartTextStyle? AxisLabelStyle { get; set; }

        /// <summary>Оформление названий осей. Null — по умолчанию.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ChartTextStyle? AxisTitleStyle { get; set; }

        /// <summary>Оформление подписей значений. Null — по умолчанию.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ChartTextStyle? DataLabelStyle { get; set; }

        /// <summary>
        /// Заливка всей диаграммы: цвет, «#AARRGGBB» или градиент «grad|…»; «#00000000» —
        /// без заливки (прозрачная). Null — белая, как у Word.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? BackgroundFill { get; set; }

        /// <summary>
        /// Картинка фона всей диаграммы — файл в хранилище картинок проекта
        /// (TextEditor/Images), растянутый на диаграмму. Null — нет. Рисуется поверх
        /// заливки.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? BackgroundImage { get; set; }

        /// <summary>Заливка области построения (под рядами и сеткой). Null — нет.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? PlotAreaFill { get; set; }

        /// <summary>Оформление надписей рода role.</summary>
        public ChartTextStyle? StyleOf(ChartTextRole role) => role switch
        {
            ChartTextRole.Title => TitleStyle,
            ChartTextRole.Legend => LegendStyle,
            ChartTextRole.AxisLabels => AxisLabelStyle,
            ChartTextRole.AxisTitles => AxisTitleStyle,
            _ => DataLabelStyle
        };

        /// <summary>Ставит оформление надписей рода role; пустое — снимает.</summary>
        public void SetStyle(ChartTextRole role, ChartTextStyle? style)
        {
            style = ChartTextStyle.Normalize(style);
            switch (role)
            {
                case ChartTextRole.Title: TitleStyle = style; break;
                case ChartTextRole.Legend: LegendStyle = style; break;
                case ChartTextRole.AxisLabels: AxisLabelStyle = style; break;
                case ChartTextRole.AxisTitles: AxisTitleStyle = style; break;
                default: DataLabelStyle = style; break;
            }
        }

        /// <summary>Размер надписей рода role по умолчанию, пт — как у Word.</summary>
        public static double DefaultSizeOf(ChartTextRole role) => role switch
        {
            ChartTextRole.Title => 14.0,
            ChartTextRole.AxisTitles => 10.0,
            _ => 9.0
        };

        /// <summary>Жирные ли надписи рода role по умолчанию.</summary>
        public static bool DefaultBoldOf(ChartTextRole role) => role == ChartTextRole.AxisTitles;

        /// <summary>Цвет надписей по умолчанию — серый Word.</summary>
        public const string DefaultTextColor = "#595959";

        /// <summary>Название вспомогательной оси значений. Null — нет.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SecondaryAxisTitle { get; set; }

        /// <summary>Заданный минимум вспомогательной оси. Null — подбирается сам.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? SecondaryAxisMin { get; set; }

        /// <summary>Заданный максимум вспомогательной оси. Null — подбирается сам.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? SecondaryAxisMax { get; set; }

        /// <summary>Формат чисел вспомогательной оси. Null — общий.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SecondaryNumberFormat { get; set; }

        /// <summary>Зазор между группами столбцов в процентах ширины столбца.</summary>
        public int GapWidthPercent { get; set; } = 219;

        /// <summary>Перекрытие столбцов одной группы в процентах (-100…100).</summary>
        public int OverlapPercent { get; set; } = -27;

        /// <summary>Отверстие кольцевой диаграммы в процентах радиуса.</summary>
        public int HoleSizePercent { get; set; } = 75;

        /// <summary>Поворот первой доли круговой диаграммы, градусы по часовой.</summary>
        public int FirstSliceAngle { get; set; }

        /// <summary>Шрифт надписей. Null — шрифт по умолчанию (Calibri).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? FontFamily { get; set; }

        /// <summary>Подписи оси категорий скрыты (ось удалена в Word).</summary>
        public bool HideCategoryAxis { get; set; }

        /// <summary>Подписи оси значений скрыты (ось удалена в Word).</summary>
        public bool HideValueAxis { get; set; }

        /// <summary>Лепестковая с заливкой: ряды — закрашенные многоугольники.</summary>
        public bool RadarFilled { get; set; }

        /// <summary>Место подписей значений. Auto — место Word по умолчанию.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ChartLabelPosition DataLabelPosition { get; set; }

        /// <summary>Вспомогательные линии сетки по оси значений: пять делений на шаг.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool ShowMinorGridlines { get; set; }

        /// <summary>Основные линии сетки по оси категорий (у точечной — по оси X).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool ShowCategoryGridlines { get; set; }

        /// <summary>Вспомогательные линии сетки по оси категорий (у точечной — по оси X).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool ShowCategoryMinorGridlines { get; set; }

        /// <summary>Таблица данных под областью построения, как у Word.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool ShowDataTable { get; set; }

        /// <summary>Таблица данных без ключей легенды (цветных квадратиков у имён рядов).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool HideDataTableKeys { get; set; }

        /// <summary>Полосы повышения и понижения графика: между первым и последним рядом.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool ShowUpDownBars { get; set; }

        /// <summary>Линии проекции графика и областей: от точек вниз до оси категорий.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool ShowDropLines { get; set; }

        /// <summary>Линии минимума и максимума графика: по категории от меньшего ряда до большего.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool ShowHighLowLines { get; set; }

        /// <summary>
        /// Категории (у круговой — доли), скрытые фильтром диаграммы, по номерам. Данные
        /// остаются в таблице. Null — скрытых нет.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<int>? HiddenCategories { get; set; }

        /// <summary>Есть ли что-то скрытое фильтром: ряды или категории.</summary>
        [JsonIgnore]
        public bool HasFilter => Series.Exists(s => s.Hidden) || HiddenCategories is { Count: > 0 };

        /// <summary>
        /// То, что видно на диаграмме: копия без скрытых фильтром рядов и категорий.
        /// Цвета рядов и долей закрепляются за ними явно — как у Word, скрытый ряд не
        /// сдвигает цвета остальных. Без фильтра — сама модель.
        /// </summary>
        public ChartModel VisibleView()
        {
            if (!HasFilter) return this;

            var view = Clone();
            view.Source = null;
            var hiddenCategories = new HashSet<int>(HiddenCategories ?? new List<int>());

            // Цвета — по прежним номерам рядов и долей.
            for (int s = 0; s < view.Series.Count; s++)
            {
                var series = view.Series[s];
                series.ColorHex = SeriesColor(s);
                if (IsCircular || series.PointColors is not null)
                {
                    int count = Math.Max(CategoryCount, series.Values.Count);
                    var colors = new List<string?>(count);
                    for (int i = 0; i < count; i++) colors.Add(PointColor(Series[s], i));
                    series.PointColors = colors;
                }
            }

            view.Series.RemoveAll(s => s.Hidden);
            // Все ряды не прячутся: пустая диаграмма Word не пишет.
            if (view.Series.Count == 0 && Series.Count > 0)
            {
                var first = Series[0].Clone();
                first.Hidden = false;
                first.ColorHex = SeriesColor(0);
                view.Series.Add(first);
            }

            if (hiddenCategories.Count > 0)
            {
                int total = CategoryCount;
                if (hiddenCategories.Count >= total) hiddenCategories.Clear();

                List<T> Keep<T>(List<T> source)
                {
                    var kept = new List<T>(source.Count);
                    for (int i = 0; i < source.Count; i++)
                        if (!hiddenCategories.Contains(i)) kept.Add(source[i]);
                    return kept;
                }

                view.Categories = Keep(view.Categories);
                foreach (var series in view.Series)
                {
                    series.Values = Keep(series.Values);
                    if (series.XValues is not null) series.XValues = Keep(series.XValues);
                    if (series.BubbleSizes is not null) series.BubbleSizes = Keep(series.BubbleSizes);
                    if (series.PointColors is not null) series.PointColors = Keep(series.PointColors);
                }
            }

            view.HiddenCategories = null;
            return view;
        }

        /// <summary>Исходная диаграмма Word. Null — диаграмма создана в редакторе.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public WordChartSource? Source { get; set; }

        /// <summary>Есть ли у диаграммы оси: у круговой и кольцевой их нет.</summary>
        [JsonIgnore]
        public bool HasAxes => Kind is not (ChartKind.Pie or ChartKind.Doughnut);

        /// <summary>Линии тренда и планки погрешностей: у гистограммы, линейчатой, графика, областей, точечной и пузырьковой.</summary>
        [JsonIgnore]
        public bool CanHaveAnalytics => Kind is ChartKind.Column or ChartKind.Bar or ChartKind.Line or ChartKind.Area
            or ChartKind.Scatter or ChartKind.Bubble;

        /// <summary>Линия тренда у ряда вида kind: у областей и пузырьков Word её тоже строит, у круговых — нет.</summary>
        public static bool KindHasTrendline(ChartKind kind) => kind is ChartKind.Column or ChartKind.Bar or ChartKind.Line
            or ChartKind.Area or ChartKind.Scatter or ChartKind.Bubble;

        /// <summary>Есть ли ряды графика: полосы повышения, линии проекции и минимума-максимума бывают только у них.</summary>
        [JsonIgnore]
        public bool HasLineSeries => Kind == ChartKind.Line || (CanCombine && Series.Exists(s => s.Kind == ChartKind.Line));

        /// <summary>Таблица данных бывает у гистограммы, графика и областей (и их сочетаний).</summary>
        [JsonIgnore]
        public bool CanShowDataTable => Kind is ChartKind.Column or ChartKind.Line or ChartKind.Area;

        /// <summary>Фон диаграммы тёмный (по яркости его цвета, у градиента — первой точки).</summary>
        [JsonIgnore]
        public bool HasDarkBackground
        {
            get
            {
                if (string.IsNullOrEmpty(BackgroundFill)) return false;
                var (a, r, g, b) = ParseArgb(BackgroundFill);
                if (a < 128) return false;
                return (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0 < 0.45;
            }
        }

        /// <summary>Таблица данных видна: включена и вид её допускает.</summary>
        [JsonIgnore]
        public bool DataTableVisible => ShowDataTable && CanShowDataTable;

        /// <summary>
        /// Места подписей значений, которые Word допускает у вида: у столбцов —
        /// в центре, у вершины внутри, у основания и над вершиной (у накопленных — без
        /// последнего), у точек — в центре, слева, справа, сверху, снизу, у круговой —
        /// в центре, у края внутри и снаружи и по ширине. У областей, кольцевой,
        /// лепестковой и биржевой выбора нет.
        /// </summary>
        public static IReadOnlyList<ChartLabelPosition> LabelPositionsFor(ChartKind kind, ChartGrouping grouping)
        {
            switch (kind)
            {
                case ChartKind.Column:
                case ChartKind.Bar:
                    return grouping == ChartGrouping.Standard
                        ? new[] { ChartLabelPosition.Center, ChartLabelPosition.InsideEnd, ChartLabelPosition.InsideBase, ChartLabelPosition.OutsideEnd }
                        : new[] { ChartLabelPosition.Center, ChartLabelPosition.InsideEnd, ChartLabelPosition.InsideBase };
                case ChartKind.Line:
                case ChartKind.Scatter:
                case ChartKind.Bubble:
                    return new[] { ChartLabelPosition.Center, ChartLabelPosition.Left, ChartLabelPosition.Right, ChartLabelPosition.Above, ChartLabelPosition.Below };
                case ChartKind.Pie:
                    return new[] { ChartLabelPosition.Center, ChartLabelPosition.InsideEnd, ChartLabelPosition.OutsideEnd, ChartLabelPosition.BestFit };
                default:
                    return Array.Empty<ChartLabelPosition>();
            }
        }

        /// <summary>
        /// Место подписей у группы рядов вида kind: заданное, если вид его допускает,
        /// иначе Auto. Ряды линии в комбинированной диаграмме берут своё.
        /// </summary>
        public ChartLabelPosition LabelPositionFor(ChartKind kind)
        {
            var grouping = HasGrouping ? Grouping : ChartGrouping.Standard;
            foreach (var position in LabelPositionsFor(kind, grouping))
                if (position == DataLabelPosition) return position;
            return ChartLabelPosition.Auto;
        }

        /// <summary>Подпись места подписей значений для списка «Место».</summary>
        public static string LabelPositionName(ChartLabelPosition position) => position switch
        {
            ChartLabelPosition.Center => "В центре",
            ChartLabelPosition.InsideEnd => "У вершины, внутри",
            ChartLabelPosition.InsideBase => "У основания, внутри",
            ChartLabelPosition.OutsideEnd => "У вершины, снаружи",
            ChartLabelPosition.Above => "Сверху",
            ChartLabelPosition.Below => "Снизу",
            ChartLabelPosition.Left => "Слева",
            ChartLabelPosition.Right => "Справа",
            ChartLabelPosition.BestFit => "Наилучшее размещение",
            _ => "Авто"
        };

        /// <summary>Круговая или кольцевая: доли вместо осей.</summary>
        [JsonIgnore]
        public bool IsCircular => Kind is ChartKind.Pie or ChartKind.Doughnut;

        /// <summary>Точки по двум числовым осям — точечная и пузырьковая: категорий нет, есть X.</summary>
        [JsonIgnore]
        public bool IsXY => Kind is ChartKind.Scatter or ChartKind.Bubble;

        /// <summary>У вида есть группировка рядов: рядом, с накоплением, нормированная.</summary>
        [JsonIgnore]
        public bool HasGrouping => Kind is ChartKind.Column or ChartKind.Bar or ChartKind.Line or ChartKind.Area;

        /// <summary>
        /// Вид, у которого ряды могут быть разного вида и стоять на вспомогательной оси:
        /// гистограмма, график и области — комбинированная диаграмма Word.
        /// </summary>
        [JsonIgnore]
        public bool CanCombine => Kind is ChartKind.Column or ChartKind.Line or ChartKind.Area;

        /// <summary>
        /// Есть ли вспомогательная ось: часть рядов на ней, часть — на основной. Если на
        /// ней все ряды, она основная.
        /// </summary>
        [JsonIgnore]
        public bool HasSecondaryAxis
        {
            get
            {
                if (!CanCombine) return false;
                bool primary = false, secondary = false;
                foreach (var series in Series)
                {
                    if (series.SecondaryAxis) secondary = true;
                    else primary = true;
                }
                return primary && secondary;
            }
        }

        /// <summary>Ряд стоит на вспомогательной оси: она есть, и ряд на ней.</summary>
        public bool IsOnSecondaryAxis(ChartSeries series) => series.SecondaryAxis && HasSecondaryAxis;

        /// <summary>Название, которое видно на диаграмме, или null, если его нет.</summary>
        [JsonIgnore]
        public string? DisplayTitle
        {
            get
            {
                if (!ShowTitle) return null;
                if (!string.IsNullOrWhiteSpace(Title)) return Title;
                return Series.Count == 1 && !string.IsNullOrWhiteSpace(Series[0].Name)
                    ? Series[0].Name
                    : DefaultTitle;
            }
        }

        /// <summary>Число категорий: подписей или самых длинных значений.</summary>
        [JsonIgnore]
        public int CategoryCount
        {
            get
            {
                int count = Categories.Count;
                foreach (var series in Series)
                    count = Math.Max(count, series.Values.Count);
                return count;
            }
        }

        /// <summary>Вид ряда: свой у ряда смешанной диаграммы, иначе вид диаграммы.</summary>
        public ChartKind KindOf(ChartSeries series) => series.Kind ?? Kind;

        /// <summary>Подпись категории; у категории без подписи — её номер, как у Excel.</summary>
        public string CategoryLabel(int index)
        {
            if (index >= 0 && index < Categories.Count) return Categories[index] ?? string.Empty;
            return (index + 1).ToString(CultureInfo.CurrentCulture);
        }

        /// <summary>Цвет ряда: свой или палитры по номеру.</summary>
        public string SeriesColor(int index)
        {
            if (index >= 0 && index < Series.Count && !string.IsNullOrEmpty(Series[index].ColorHex))
                return Series[index].ColorHex!;
            return PaletteColor(index);
        }

        /// <summary>
        /// Цвет точки: свой у точки, иначе палитры по номеру точки — так доли круговой
        /// диаграммы получают разные цвета.
        /// </summary>
        public string PointColor(ChartSeries series, int pointIndex)
        {
            if (series.PointColors is { } colors && pointIndex >= 0 && pointIndex < colors.Count
                && !string.IsNullOrEmpty(colors[pointIndex]))
                return colors[pointIndex]!;
            return PaletteColor(pointIndex);
        }

        /// <summary>
        /// Цвет палитры по номеру. Первые шесть — акценты темы; дальше Word берёт те же
        /// акценты темнее, затем светлее.
        /// </summary>
        public static string PaletteColor(int index)
        {
            if (index < 0) index = 0;
            string baseColor = Palette[index % Palette.Count];
            int cycle = index / Palette.Count;

            return (cycle % 3) switch
            {
                0 => baseColor,
                1 => Shade(baseColor, 0.6),
                _ => Tint(baseColor, 0.6)
            };
        }

        private static string Shade(string hex, double factor)
        {
            var (r, g, b) = ParseHex(hex);
            return ToHex(r * factor, g * factor, b * factor);
        }

        private static string Tint(string hex, double factor)
        {
            var (r, g, b) = ParseHex(hex);
            return ToHex(255 - (255 - r) * factor, 255 - (255 - g) * factor, 255 - (255 - b) * factor);
        }

        /// <summary>
        /// Цвет ряда или доли — градиент: код <see cref="GradientSpec"/> «grad|…» из
        /// стандартного подборщика цвета (линейный, радиальный, конический).
        /// </summary>
        public static bool IsGradient(string? color)
            => color is not null && color.TrimStart().StartsWith("grad|", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Сплошной цвет цвета ряда: у градиента — его первая точка. Прозрачность
        /// («#AARRGGBB») сохраняется.
        /// </summary>
        public static string SolidColor(string? color)
        {
            if (string.IsNullOrWhiteSpace(color)) return "#000000";
            return IsGradient(color) ? GradientSpec.Parse(color).SolidHex : color.Trim();
        }

        /// <summary>Разбирает цвет #RRGGBB, #AARRGGBB или градиент (по первой точке); ошибочный — чёрный.</summary>
        public static (int R, int G, int B) ParseHex(string? hex)
        {
            var (_, r, g, b) = ParseArgb(hex);
            return (r, g, b);
        }

        /// <summary>Разбирает цвет с прозрачностью: #RRGGBB (непрозрачный) или #AARRGGBB.</summary>
        public static (int A, int R, int G, int B) ParseArgb(string? hex)
        {
            if (string.IsNullOrEmpty(hex)) return (255, 0, 0, 0);
            string value = SolidColor(hex).TrimStart('#');
            if (value.Length == 6
                && int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
                return (255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
            if (value.Length == 8
                && uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint argb))
                return ((int)(argb >> 24), (int)((argb >> 16) & 0xFF), (int)((argb >> 8) & 0xFF), (int)(argb & 0xFF));
            return (255, 0, 0, 0);
        }

        private static string ToHex(double r, double g, double b)
        {
            static int Clamp(double v) => (int)Math.Round(Math.Clamp(v, 0, 255));
            return string.Create(CultureInfo.InvariantCulture, $"#{Clamp(r):X2}{Clamp(g):X2}{Clamp(b):X2}");
        }

        /// <summary>
        /// Отпечаток всего, что видно и правится в редакторе. Исходная диаграмма Word в
        /// него не входит: по отпечатку и узнаётся, правили ли диаграмму после чтения.
        /// </summary>
        public string ComputeFingerprint() => JsonSerializer.Serialize(Copy(withSource: false));

        /// <summary>Не менялась ли диаграмма Word с момента чтения.</summary>
        [JsonIgnore]
        public bool IsUnchangedWordChart
            => Source is { ChartXml.Length: > 0 } source && source.Fingerprint == ComputeFingerprint();

        public ChartModel Clone() => Copy(withSource: true);

        private ChartModel Copy(bool withSource)
        {
            var clone = new ChartModel
            {
                Kind = Kind,
                Grouping = Grouping,
                Title = Title,
                ShowTitle = ShowTitle,
                Legend = Legend,
                ShowDataLabels = ShowDataLabels,
                ShowGridlines = ShowGridlines,
                ShowBorder = ShowBorder,
                Categories = new List<string>(Categories),
                CategoryAxisTitle = CategoryAxisTitle,
                ValueAxisTitle = ValueAxisTitle,
                ValueAxisMin = ValueAxisMin,
                ValueAxisMax = ValueAxisMax,
                NumberFormat = NumberFormat,
                XAxisMin = XAxisMin,
                XAxisMax = XAxisMax,
                XNumberFormat = XNumberFormat,
                SecondaryAxisTitle = SecondaryAxisTitle,
                SecondaryAxisMin = SecondaryAxisMin,
                SecondaryAxisMax = SecondaryAxisMax,
                SecondaryNumberFormat = SecondaryNumberFormat,
                TitleStyle = TitleStyle?.Clone(),
                LegendStyle = LegendStyle?.Clone(),
                AxisLabelStyle = AxisLabelStyle?.Clone(),
                AxisTitleStyle = AxisTitleStyle?.Clone(),
                DataLabelStyle = DataLabelStyle?.Clone(),
                BackgroundFill = BackgroundFill,
                BackgroundImage = BackgroundImage,
                PlotAreaFill = PlotAreaFill,
                GapWidthPercent = GapWidthPercent,
                OverlapPercent = OverlapPercent,
                HoleSizePercent = HoleSizePercent,
                FirstSliceAngle = FirstSliceAngle,
                FontFamily = FontFamily,
                HideCategoryAxis = HideCategoryAxis,
                HideValueAxis = HideValueAxis,
                RadarFilled = RadarFilled,
                DataLabelPosition = DataLabelPosition,
                ShowMinorGridlines = ShowMinorGridlines,
                ShowCategoryGridlines = ShowCategoryGridlines,
                ShowCategoryMinorGridlines = ShowCategoryMinorGridlines,
                ShowDataTable = ShowDataTable,
                HideDataTableKeys = HideDataTableKeys,
                ShowUpDownBars = ShowUpDownBars,
                ShowDropLines = ShowDropLines,
                ShowHighLowLines = ShowHighLowLines,
                HiddenCategories = HiddenCategories is null ? null : new List<int>(HiddenCategories),
                Source = withSource ? Source?.Clone() : null
            };

            foreach (var series in Series) clone.Series.Add(series.Clone());
            return clone;
        }

        /// <summary>
        /// Наборы цветов, как «Изменить цвета» у Word: имя набора — цвета рядов (у круговой
        /// и кольцевой — цвета долей) по порядку.
        /// </summary>
        public static readonly IReadOnlyList<(string Name, string[] Colors)> ColorSchemes = new (string, string[])[]
        {
            // Первый набор — цвета, которыми диаграмма красится сама (PaletteColor).
            ("Разноцветная", new[] { "#4472C4", "#ED7D31", "#A5A5A5", "#FFC000", "#5B9BD5", "#70AD47", "#294476", "#8E4B1D" }),
            ("Яркая", new[] { "#C00000", "#FFC000", "#00B050", "#0070C0", "#7030A0", "#FF6600", "#00B0F0", "#FF66CC" }),
            ("Синие оттенки", new[] { "#4472C4", "#8FAADC", "#2F5597", "#B4C7E7", "#203864", "#DAE3F3", "#5B9BD5", "#1F3864" }),
            ("Оранжевые оттенки", new[] { "#ED7D31", "#F4B183", "#C55A11", "#F8CBAD", "#843C0C", "#FBE5D6", "#F79646", "#5F2A08" }),
            ("Зелёные оттенки", new[] { "#70AD47", "#A9D18E", "#548235", "#C5E0B4", "#385723", "#E2F0D9", "#92D050", "#243B16" }),
            ("Серые оттенки", new[] { "#7F7F7F", "#BFBFBF", "#404040", "#D9D9D9", "#262626", "#A6A6A6", "#595959", "#EDEDED" }),
            ("Тёплые", new[] { "#C00000", "#ED7D31", "#FFC000", "#F4B183", "#843C0C", "#FFD966", "#E06666", "#BF9000" }),
            ("Холодные", new[] { "#002060", "#0070C0", "#00B0F0", "#00B050", "#7030A0", "#5B9BD5", "#2E75B6", "#00A3A3" }),
            ("Пастель", new[] { "#A7C7E7", "#F7C6A3", "#C9E4C5", "#F3E1A6", "#D7BDE2", "#F5B7B1", "#AED6F1", "#D5DBDB" }),
            ("Море", new[] { "#03045E", "#0077B6", "#00B4D8", "#48CAE4", "#90E0EF", "#023E8A", "#0096C7", "#ADE8F4" }),
            ("Закат", new[] { "#355070", "#6D597A", "#B56576", "#E56B6F", "#EAAC8B", "#F4D35E", "#9A4C6C", "#F28482" }),
            ("Лес", new[] { "#2D6A4F", "#40916C", "#52B788", "#74C69D", "#95D5B2", "#1B4332", "#B7E4C7", "#081C15" }),
            ("Ягоды", new[] { "#6A0572", "#AB83A1", "#C9184A", "#FF4D6D", "#FF8FA3", "#590D22", "#A4133C", "#FFCCD5" }),
            ("Земля", new[] { "#7F5539", "#9C6644", "#B08968", "#DDB892", "#E6CCB2", "#582F0E", "#A68A64", "#656D4A" }),
            ("Неон", new[] { "#F72585", "#7209B7", "#3A0CA3", "#4361EE", "#4CC9F0", "#06D6A0", "#FFD60A", "#FB5607" }),
            ("Сдержанная", new[] { "#4E79A7", "#F28E2B", "#E15759", "#76B7B2", "#59A14F", "#EDC948", "#B07AA1", "#9C755F" })
        };

        /// <summary>
        /// Копия с другим видом. Данные переводятся, как это делает Word: у точечной и
        /// пузырьковой подписи категорий становятся значениями X (что не число — номером
        /// точки), обратно — X становятся подписями; пузырькам без размеров даётся
        /// единичный размер. Зазоры, отверстие кольца, маркеры и линии — те, что Word
        /// ставит новому виду.
        /// </summary>
        public ChartModel WithKind(ChartKind kind)
        {
            var chart = Clone();
            var previous = chart.Kind;
            if (previous == kind) return chart;

            bool wasXY = chart.IsXY;
            bool wasCircular = chart.IsCircular;

            var defaults = CreateDefault(kind);
            chart.Kind = kind;
            chart.GapWidthPercent = defaults.GapWidthPercent;
            chart.OverlapPercent = defaults.OverlapPercent;
            chart.HoleSizePercent = defaults.HoleSizePercent;
            if (!chart.HasGrouping) chart.Grouping = ChartGrouping.Standard;
            if (chart.IsCircular) chart.ShowGridlines = false;
            else if (wasCircular) chart.ShowGridlines = true;

            if (chart.IsXY && !wasXY)
            {
                int count = chart.CategoryCount;
                var xs = new List<double?>(count);
                for (int i = 0; i < count; i++)
                {
                    string label = i < chart.Categories.Count ? chart.Categories[i] ?? string.Empty : string.Empty;
                    xs.Add(double.TryParse(label.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                        ? x
                        : i + 1);
                }
                foreach (var series in chart.Series) series.XValues = new List<double?>(xs);
                chart.Categories = new List<string>();
            }
            else if (!chart.IsXY && wasXY)
            {
                var first = chart.Series.Count > 0 ? chart.Series[0] : null;
                int count = 0;
                foreach (var series in chart.Series) count = Math.Max(count, series.Values.Count);
                chart.Categories = new List<string>(count);
                for (int i = 0; i < count; i++)
                {
                    double x = first?.XValues is { } xs && i < xs.Count && xs[i] is double v ? v : i + 1;
                    chart.Categories.Add(x.ToString("G15", CultureInfo.CurrentCulture));
                }
                foreach (var series in chart.Series) series.XValues = null;
            }

            for (int s = 0; s < chart.Series.Count; s++)
            {
                var series = chart.Series[s];
                series.Kind = null;
                if (!chart.CanCombine) series.SecondaryAxis = false;

                if (kind == ChartKind.Bubble)
                {
                    series.BubbleSizes ??= new List<double?>();
                    while (series.BubbleSizes.Count < series.Values.Count) series.BubbleSizes.Add(1.0);
                }
                else
                {
                    series.BubbleSizes = null;
                }

                switch (kind)
                {
                    case ChartKind.Line:
                    case ChartKind.Radar:
                        series.ShowLine = true;
                        series.ShowMarkers = false;
                        break;
                    case ChartKind.Scatter:
                        series.ShowLine = false;
                        series.ShowMarkers = true;
                        break;
                    case ChartKind.Stock:
                        series.ShowLine = false;
                        series.ShowMarkers = s == chart.Series.Count - 1;
                        break;
                    default:
                        series.ShowLine = true;
                        series.ShowMarkers = true;
                        break;
                }
            }

            return chart;
        }

        /// <summary>
        /// Копия, у которой строки и столбцы данных поменялись местами: ряды становятся
        /// категориями, категории — рядами, как «Строка/столбец» у Word. У точечной и
        /// пузырьковой категорий нет — копия без изменений.
        /// </summary>
        public ChartModel Transposed()
        {
            var chart = Clone();
            if (chart.IsXY || chart.Series.Count == 0) return chart;

            int count = chart.CategoryCount;
            var oldSeries = chart.Series;

            chart.Categories = new List<string>(oldSeries.Count);
            for (int s = 0; s < oldSeries.Count; s++)
                chart.Categories.Add(string.IsNullOrWhiteSpace(oldSeries[s].Name) ? "Ряд " + (s + 1) : oldSeries[s].Name);

            chart.Series = new List<ChartSeries>(count);
            for (int c = 0; c < count; c++)
            {
                var series = new ChartSeries
                {
                    Name = CategoryLabel(c),
                    ShowMarkers = oldSeries[0].ShowMarkers,
                    ShowLine = oldSeries[0].ShowLine,
                    Smooth = oldSeries[0].Smooth
                };
                foreach (var old in oldSeries)
                    series.Values.Add(c < old.Values.Count ? old.Values[c] : null);
                chart.Series.Add(series);
            }

            return chart;
        }

        /// <summary>
        /// Копия с набором цветов: ряды по порядку получают цвета набора; у круговой и
        /// кольцевой цвета получают доли.
        /// </summary>
        public ChartModel WithColors(IReadOnlyList<string> colors)
        {
            var chart = Clone();
            if (colors is null || colors.Count == 0) return chart;

            if (chart.IsCircular)
            {
                int count = chart.CategoryCount;
                foreach (var series in chart.Series)
                {
                    series.PointColors = new List<string?>(count);
                    for (int i = 0; i < count; i++) series.PointColors.Add(colors[i % colors.Count]);
                }
                return chart;
            }

            for (int s = 0; s < chart.Series.Count; s++)
            {
                chart.Series[s].ColorHex = colors[s % colors.Count];
                chart.Series[s].PointColors = null;
            }
            return chart;
        }

        /// <summary>
        /// Новая диаграмма с данными-образцом, как у Word при вставке: четыре категории и
        /// три ряда; у круговой и кольцевой — один ряд по кварталам, у точечной — три точки.
        /// </summary>
        public static ChartModel CreateDefault(ChartKind kind)
        {
            return CreateDefaultCore(kind);
        }

        /// <summary>
        /// Новая комбинированная диаграмма, как первая в галерее Word: два ряда
        /// гистограммой, третий — графиком на вспомогательной оси.
        /// </summary>
        public static ChartModel CreateCombo()
        {
            var chart = CreateDefaultCore(ChartKind.Column);
            var last = chart.Series[^1];
            last.Kind = ChartKind.Line;
            last.SecondaryAxis = true;
            last.ShowMarkers = false;
            last.Values = new List<double?> { 20, 32, 41, 57 };
            return chart;
        }

        private static ChartModel CreateDefaultCore(ChartKind kind)
        {
            var chart = new ChartModel { Kind = kind };

            switch (kind)
            {
                case ChartKind.Pie:
                case ChartKind.Doughnut:
                    chart.Categories.AddRange(new[] { "Кв. 1", "Кв. 2", "Кв. 3", "Кв. 4" });
                    chart.Series.Add(new ChartSeries
                    {
                        Name = "Продажи",
                        Values = new List<double?> { 8.2, 3.2, 1.4, 1.2 }
                    });
                    chart.Legend = ChartLegendPosition.Bottom;
                    chart.ShowGridlines = false;
                    break;

                case ChartKind.Bubble:
                    chart.Series.Add(new ChartSeries
                    {
                        Name = "Значения Y",
                        XValues = new List<double?> { 0.7, 1.8, 2.6 },
                        Values = new List<double?> { 2.7, 3.2, 0.8 },
                        BubbleSizes = new List<double?> { 10, 4, 8 }
                    });
                    chart.Legend = ChartLegendPosition.None;
                    break;

                case ChartKind.Stock:
                    chart.Categories.AddRange(new[] { "01.01.2026", "02.01.2026", "03.01.2026", "04.01.2026", "05.01.2026" });
                    chart.Series.Add(new ChartSeries { Name = "Максимум", Values = new List<double?> { 55, 57, 57, 58, 58 }, ShowLine = false, ShowMarkers = false });
                    chart.Series.Add(new ChartSeries { Name = "Минимум", Values = new List<double?> { 11, 12, 13, 11, 25 }, ShowLine = false, ShowMarkers = false });
                    chart.Series.Add(new ChartSeries { Name = "Закрытие", Values = new List<double?> { 25, 38, 50, 35, 43 }, ShowLine = false, ShowMarkers = true });
                    break;

                case ChartKind.Radar:
                    chart.Categories.AddRange(new[] { "Категория 1", "Категория 2", "Категория 3", "Категория 4", "Категория 5" });
                    chart.Series.Add(new ChartSeries { Name = "Ряд 1", Values = new List<double?> { 32, 32, 28, 12, 15 }, ShowMarkers = false });
                    chart.Series.Add(new ChartSeries { Name = "Ряд 2", Values = new List<double?> { 12, 12, 12, 21, 28 }, ShowMarkers = false });
                    break;

                case ChartKind.Scatter:
                    chart.Series.Add(new ChartSeries
                    {
                        Name = "Значения Y",
                        XValues = new List<double?> { 0.7, 1.8, 2.6 },
                        Values = new List<double?> { 2.7, 3.2, 0.8 },
                        ShowMarkers = true,
                        ShowLine = false
                    });
                    chart.Legend = ChartLegendPosition.None;
                    break;

                default:
                    chart.Categories.AddRange(new[] { "Категория 1", "Категория 2", "Категория 3", "Категория 4" });
                    chart.Series.Add(new ChartSeries { Name = "Ряд 1", Values = new List<double?> { 4.3, 2.5, 3.5, 4.5 } });
                    chart.Series.Add(new ChartSeries { Name = "Ряд 2", Values = new List<double?> { 2.4, 4.4, 1.8, 2.8 } });
                    chart.Series.Add(new ChartSeries { Name = "Ряд 3", Values = new List<double?> { 2, 2, 3, 5 } });
                    break;
            }

            // Первый вид графика у Word — линии без маркеров.
            if (kind == ChartKind.Line)
                foreach (var series in chart.Series) series.ShowMarkers = false;

            // Кольцо с отверстием в половину радиуса: при трёх четвертях, как у Word,
            // кольцо выходит тонкой лентой, и подписи на нём не помещаются.
            if (kind == ChartKind.Doughnut) chart.HoleSizePercent = 50;

            if (kind == ChartKind.Bar) chart.GapWidthPercent = 182;
            if (kind is ChartKind.Line or ChartKind.Area) chart.GapWidthPercent = 150;

            return chart;
        }
    }
}
