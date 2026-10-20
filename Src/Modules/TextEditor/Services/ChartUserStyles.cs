using System;
using System.Collections.Generic;
using System.Linq;
using Serilog;
using Writersword.Core.Interfaces.Services.Storage;
using Writersword.Core.Services;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Свой стиль диаграммы: облик, снятый с диаграммы, — фон, область построения,
    /// шрифт, оформление каждой надписи, рамка и сетка. Данные, ряды, их цвета и
    /// расположение элементов в стиль не входят: так же, как у готовых стилей.
    /// </summary>
    public sealed class ChartUserStyle
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = string.Empty;

        public string? FontFamily { get; set; }
        public string? BackgroundFill { get; set; }
        public string? PlotAreaFill { get; set; }
        public ChartTextStyle? TitleStyle { get; set; }
        public ChartTextStyle? LegendStyle { get; set; }
        public ChartTextStyle? AxisLabelStyle { get; set; }
        public ChartTextStyle? AxisTitleStyle { get; set; }
        public ChartTextStyle? DataLabelStyle { get; set; }
        public bool ShowBorder { get; set; } = true;
        public bool ShowGridlines { get; set; } = true;
        public bool ShowMinorGridlines { get; set; }
        public bool ShowCategoryGridlines { get; set; }
        public bool ShowCategoryMinorGridlines { get; set; }

        /// <summary>Снимает облик с диаграммы.</summary>
        public static ChartUserStyle From(ChartModel chart, string name) => new()
        {
            Name = name,
            FontFamily = chart.FontFamily,
            BackgroundFill = chart.BackgroundFill,
            PlotAreaFill = chart.PlotAreaFill,
            TitleStyle = chart.TitleStyle?.Clone(),
            LegendStyle = chart.LegendStyle?.Clone(),
            AxisLabelStyle = chart.AxisLabelStyle?.Clone(),
            AxisTitleStyle = chart.AxisTitleStyle?.Clone(),
            DataLabelStyle = chart.DataLabelStyle?.Clone(),
            ShowBorder = chart.ShowBorder,
            ShowGridlines = chart.ShowGridlines,
            ShowMinorGridlines = chart.ShowMinorGridlines,
            ShowCategoryGridlines = chart.ShowCategoryGridlines,
            ShowCategoryMinorGridlines = chart.ShowCategoryMinorGridlines
        };

        /// <summary>
        /// Копия диаграммы с этим обликом. Сетка ставится только там, где у диаграммы
        /// есть оси; картинка фона снимается — в стиле её нет, а поверх нового фона
        /// она закрыла бы его целиком.
        /// </summary>
        public ChartModel ApplyTo(ChartModel chart)
        {
            var next = chart.Clone();
            next.FontFamily = FontFamily;
            next.BackgroundFill = BackgroundFill;
            next.BackgroundImage = null;
            next.PlotAreaFill = PlotAreaFill;
            next.TitleStyle = TitleStyle?.Clone();
            next.LegendStyle = LegendStyle?.Clone();
            next.AxisLabelStyle = AxisLabelStyle?.Clone();
            next.AxisTitleStyle = AxisTitleStyle?.Clone();
            next.DataLabelStyle = DataLabelStyle?.Clone();
            next.ShowBorder = ShowBorder;
            next.ShowGridlines = ShowGridlines && next.HasAxes;
            next.ShowMinorGridlines = ShowMinorGridlines && next.HasAxes;
            next.ShowCategoryGridlines = ShowCategoryGridlines && next.HasAxes;
            next.ShowCategoryMinorGridlines = ShowCategoryMinorGridlines && next.HasAxes;
            return next;
        }

        /// <summary>Пресет для галереи стилей: образец, подсказка и правка, как у готовых.</summary>
        public ChartPresets.Preset ToPreset()
            => new(Name, "Свой стиль: фон, шрифты, рамка и сетка, сохранённые с диаграммы", ApplyTo);
    }

    public sealed class ChartUserStyleData
    {
        public List<ChartUserStyle> Styles { get; set; } = new();
    }

    /// <summary>
    /// Свои стили диаграмм. Хранятся в настройках программы и видны во всех проектах,
    /// как свои наборы цветов.
    /// </summary>
    public static class ChartUserStyles
    {
        private const string SettingsKey = "ChartUserStyles";
        public const int MaxStyles = 40;
        private static readonly ILogger _log = Log.ForContext(typeof(ChartUserStyles));

        public static List<ChartUserStyle> Load()
        {
            try
            {
                var data = CoreServices.GetService<ISettingsService>()?.GetModuleSettings<ChartUserStyleData>(SettingsKey);
                return data?.Styles?.Where(s => s is not null).ToList() ?? new List<ChartUserStyle>();
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[CHART] Свои стили не прочитались");
                return new List<ChartUserStyle>();
            }
        }

        public static void Save(IEnumerable<ChartUserStyle> styles)
        {
            var settings = CoreServices.GetService<ISettingsService>();
            if (settings is null) return;

            try
            {
                settings.SaveModuleSettings(SettingsKey, new ChartUserStyleData { Styles = styles.ToList() });
                settings.Save();
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[CHART] Свои стили не сохранились");
            }
        }

        /// <summary>Сохраняет облик диаграммы новым своим стилем с именем «Мой стиль N».</summary>
        public static ChartUserStyle? AddFrom(ChartModel chart)
        {
            var styles = Load();
            if (styles.Count >= MaxStyles) return null;

            int number = 1;
            while (styles.Any(s => s.Name == "Мой стиль " + number)) number++;
            var style = ChartUserStyle.From(chart, "Мой стиль " + number);
            styles.Add(style);
            Save(styles);
            return style;
        }

        /// <summary>
        /// Переписывает облик своего стиля обликом диаграммы — так стиль правится: его
        /// применяют, настраивают диаграмму в её окне и обновляют стиль по ней. Имя остаётся.
        /// </summary>
        public static void UpdateFrom(string id, ChartModel chart)
        {
            var styles = Load();
            int index = styles.FindIndex(s => s.Id == id);
            if (index < 0) return;
            var updated = ChartUserStyle.From(chart, styles[index].Name);
            updated.Id = id;
            styles[index] = updated;
            Save(styles);
        }

        public static void Delete(string id)
        {
            var styles = Load();
            if (styles.RemoveAll(s => s.Id == id) > 0) Save(styles);
        }

        public static void Rename(string id, string name)
        {
            name = name.Trim();
            if (name.Length == 0) return;
            var styles = Load();
            if (styles.FirstOrDefault(s => s.Id == id) is not { } style || style.Name == name) return;
            style.Name = name;
            Save(styles);
        }
    }
}
