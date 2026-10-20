using System;
using System.Collections.Generic;
using System.Globalization;

namespace Writersword.Modules.TextEditor.Models.Document
{
    /// <summary>
    /// Готовые стили и экспресс-макеты диаграммы, как в Word.
    ///
    /// Стиль меняет облик: фон, область построения, сетку, рамку, цвета и размеры
    /// надписей, у одного — заливку рядов. Данные, виды рядов и расположение
    /// элементов он не трогает.
    ///
    /// Макет меняет состав и расположение элементов: название, легенду, названия осей,
    /// подписи значений, сетку и таблицу данных. Облик остаётся прежним.
    ///
    /// Каждый пресет собирает новую модель из копии: модель после создания не меняется.
    /// </summary>
    public static class ChartPresets
    {
        /// <summary>Пресет: имя, пояснение для подсказки и правка копии диаграммы.</summary>
        public sealed record Preset(string Name, string Description, Func<ChartModel, ChartModel> Apply);

        private const string AxisTitlePlaceholder = "Название оси";

        // ── Стили ────────────────────────────────────────────────────────

        public static readonly IReadOnlyList<Preset> Styles = new[]
        {
            new Preset("Стандартный", "Белый фон, тонкая сетка, серые надписи",
                c => Look(c, _ => { })),

            new Preset("С подписями", "Подписи значений вместо сетки и оси значений",
                c => Look(c, d =>
                {
                    d.ShowGridlines = false;
                    d.ShowDataLabels = true;
                    d.HideValueAxis = d.HasAxes;
                })),

            new Preset("Подробная сетка", "Основная и вспомогательная сетка по обеим осям",
                c => Look(c, d =>
                {
                    d.ShowMinorGridlines = true;
                    d.ShowCategoryGridlines = true;
                })),

            new Preset("Без рамки", "Ни рамки, ни сетки: только ряды и подписи",
                c => Look(c, d =>
                {
                    d.ShowBorder = false;
                    d.ShowGridlines = false;
                })),

            new Preset("Серая область", "Область построения на светло-сером фоне",
                c => Look(c, d => d.PlotAreaFill = "#F2F2F2")),

            new Preset("Светлый градиент", "Фон диаграммы — мягкий голубой градиент",
                c => Look(c, d => d.BackgroundFill = "grad|lin|90|blk|0:#E7EEF8;1:#FFFFFF")),

            new Preset("Тёмный", "Тёмно-серый фон и светлые надписи, без рамки",
                c => Look(c, d =>
                {
                    d.BackgroundFill = "#404040";
                    d.ShowBorder = false;
                    TextColor(d, "#F2F2F2");
                })),

            new Preset("Ночной", "Тёмно-синий градиент и светлые надписи",
                c => Look(c, d =>
                {
                    d.BackgroundFill = "grad|lin|90|blk|0:#0B0F1A;1:#1F2A44";
                    d.ShowBorder = false;
                    TextColor(d, "#E6E6E6");
                })),

            new Preset("Акцент", "Крупное жирное название цветом первого ряда",
                c => Look(c, d =>
                {
                    string accent = d.Series.Count > 0 ? ChartModel.SolidColor(d.SeriesColor(0)) : "#4472C4";
                    d.TitleStyle = new ChartTextStyle { SizePt = 16, Bold = true, ColorHex = accent };
                })),

            new Preset("Крупный текст", "Все надписи крупнее — для презентаций и печати",
                c => Look(c, d =>
                {
                    d.TitleStyle = new ChartTextStyle { SizePt = 18, Bold = true };
                    d.LegendStyle = new ChartTextStyle { SizePt = 11 };
                    d.AxisLabelStyle = new ChartTextStyle { SizePt = 11 };
                    d.AxisTitleStyle = new ChartTextStyle { SizePt = 12 };
                    d.DataLabelStyle = new ChartTextStyle { SizePt = 11 };
                })),

            new Preset("Градиент рядов", "Ряды залиты градиентом своего цвета: светлее кверху",
                c => Look(c, d =>
                {
                    for (int s = 0; s < d.Series.Count; s++)
                    {
                        var series = d.Series[s];
                        series.ColorHex = Gradient(ChartModel.SolidColor(d.SeriesColor(s)));
                        if (series.PointColors is { } points)
                            for (int i = 0; i < points.Count; i++)
                                if (points[i] is { } own) points[i] = Gradient(ChartModel.SolidColor(own));
                    }
                }))
        };

        /// <summary>
        /// Облик по стилю: всё, что меняют стили, возвращается к виду Word по умолчанию,
        /// потом стиль ставит своё. Заливка рядов градиентом снимается — у прочих
        /// стилей ряды сплошные.
        /// </summary>
        private static ChartModel Look(ChartModel chart, Action<ChartModel> style)
        {
            var next = chart.Clone();
            next.BackgroundFill = null;
            next.BackgroundImage = null;
            next.PlotAreaFill = null;
            next.TitleStyle = null;
            next.LegendStyle = null;
            next.AxisLabelStyle = null;
            next.AxisTitleStyle = null;
            next.DataLabelStyle = null;
            next.ShowBorder = true;
            next.ShowGridlines = next.HasAxes;
            next.ShowMinorGridlines = false;
            next.ShowCategoryGridlines = false;
            next.ShowCategoryMinorGridlines = false;
            next.HideValueAxis = false;

            for (int s = 0; s < next.Series.Count; s++)
            {
                var series = next.Series[s];
                if (ChartModel.IsGradient(series.ColorHex)) series.ColorHex = ChartModel.SolidColor(series.ColorHex);
                if (series.PointColors is { } points)
                    for (int i = 0; i < points.Count; i++)
                        if (ChartModel.IsGradient(points[i])) points[i] = ChartModel.SolidColor(points[i]);
            }

            style(next);
            return next;
        }

        private static void TextColor(ChartModel chart, string color)
        {
            foreach (ChartTextRole role in Enum.GetValues(typeof(ChartTextRole)))
            {
                var style = chart.StyleOf(role)?.Clone() ?? new ChartTextStyle();
                style.ColorHex = color;
                chart.SetStyle(role, style);
            }
        }

        /// <summary>Градиент снизу вверх: свой цвет внизу, светлее на две трети — вверху.</summary>
        private static string Gradient(string hex)
        {
            var (r, g, b) = ChartModel.ParseHex(hex);
            string light = string.Create(CultureInfo.InvariantCulture,
                $"#{Lighten(r):X2}{Lighten(g):X2}{Lighten(b):X2}");
            string solid = string.Create(CultureInfo.InvariantCulture, $"#{r:X2}{g:X2}{b:X2}");
            return "grad|lin|90|blk|0:" + solid + ";1:" + light;

            static int Lighten(int channel) => (int)Math.Round(channel + (255 - channel) * 0.55);
        }

        // ── Экспресс-макеты ──────────────────────────────────────────────

        public static readonly IReadOnlyList<Preset> Layouts = new[]
        {
            new Preset("Макет 1", "Название и легенда справа", c => Layout(c, title: true, ChartLegendPosition.Right)),
            new Preset("Макет 2", "Название, легенда сверху, подписи значений вместо оси и сетки",
                c => Layout(c, title: true, ChartLegendPosition.Top, labels: true, valueAxis: false, gridlines: false)),
            new Preset("Макет 3", "Название и легенда снизу — как у новой диаграммы", c => Layout(c, title: true, ChartLegendPosition.Bottom)),
            new Preset("Макет 4", "Без названия: легенда снизу и подписи значений",
                c => Layout(c, title: false, ChartLegendPosition.Bottom, labels: true, gridlines: false)),
            new Preset("Макет 5", "Название, таблица данных и название оси значений",
                c => Layout(c, title: true, ChartLegendPosition.None, valueTitle: true, table: true)),
            new Preset("Макет 6", "Название и название оси значений, без легенды",
                c => Layout(c, title: true, ChartLegendPosition.None, valueTitle: true)),
            new Preset("Макет 7", "Без названия: названия обеих осей и легенда справа",
                c => Layout(c, title: false, ChartLegendPosition.Right, categoryTitle: true, valueTitle: true)),
            new Preset("Макет 8", "Название и названия обеих осей, без легенды",
                c => Layout(c, title: true, ChartLegendPosition.None, categoryTitle: true, valueTitle: true)),
            new Preset("Макет 9", "Название, названия обеих осей и легенда справа",
                c => Layout(c, title: true, ChartLegendPosition.Right, categoryTitle: true, valueTitle: true)),
            new Preset("Макет 10", "Название, легенда справа и подписи значений",
                c => Layout(c, title: true, ChartLegendPosition.Right, labels: true)),
            new Preset("Макет 11", "Только легенда справа", c => Layout(c, title: false, ChartLegendPosition.Right))
        };

        /// <summary>
        /// Расположение элементов: название, легенда, названия осей (своё название оси
        /// сохраняется, пустое получает заготовку), подписи значений, ось значений,
        /// сетка и таблица данных.
        /// </summary>
        private static ChartModel Layout(ChartModel chart, bool title, ChartLegendPosition legend,
            bool categoryTitle = false, bool valueTitle = false, bool labels = false,
            bool valueAxis = true, bool gridlines = true, bool table = false)
        {
            var next = chart.Clone();
            next.ShowTitle = title;
            next.Legend = legend;
            next.ShowDataLabels = labels;
            next.DataLabelPosition = ChartLabelPosition.Auto;

            if (next.HasAxes)
            {
                next.CategoryAxisTitle = categoryTitle ? Keep(next.CategoryAxisTitle) : null;
                next.ValueAxisTitle = valueTitle ? Keep(next.ValueAxisTitle) : null;
                next.HideValueAxis = !valueAxis;
                next.HideCategoryAxis = false;
                next.ShowGridlines = gridlines;
                next.ShowDataTable = table && next.CanShowDataTable;
                next.HideDataTableKeys = false;
            }

            // Без легенды у круговой нечем различить доли — подписи берут это на себя.
            if (next.IsCircular && legend == ChartLegendPosition.None) next.ShowDataLabels = true;
            return next;

            static string Keep(string? text) => string.IsNullOrWhiteSpace(text) ? AxisTitlePlaceholder : text!;
        }
    }
}
