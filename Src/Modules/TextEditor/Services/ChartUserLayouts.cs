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
    /// Свой экспресс-макет: состав и расположение элементов, снятые с диаграммы, —
    /// название, легенда, подписи значений, названия осей, оси, сетка и таблица данных.
    /// Облик, данные и цвета в макет не входят: так же, как у готовых макетов.
    /// </summary>
    public sealed class ChartUserLayout
    {
        private const string AxisTitlePlaceholder = "Название оси";

        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = string.Empty;

        public bool ShowTitle { get; set; } = true;
        public ChartLegendPosition Legend { get; set; } = ChartLegendPosition.Bottom;
        public bool ShowDataLabels { get; set; }
        public ChartLabelPosition DataLabelPosition { get; set; }
        public bool CategoryTitle { get; set; }
        public bool ValueTitle { get; set; }
        public bool HideValueAxis { get; set; }
        public bool HideCategoryAxis { get; set; }
        public bool ShowGridlines { get; set; } = true;
        public bool ShowDataTable { get; set; }
        public bool HideDataTableKeys { get; set; }

        /// <summary>Снимает расположение элементов с диаграммы.</summary>
        public static ChartUserLayout From(ChartModel chart, string name) => new()
        {
            Name = name,
            ShowTitle = chart.ShowTitle,
            Legend = chart.Legend,
            ShowDataLabels = chart.ShowDataLabels,
            DataLabelPosition = chart.DataLabelPosition,
            CategoryTitle = !string.IsNullOrWhiteSpace(chart.CategoryAxisTitle),
            ValueTitle = !string.IsNullOrWhiteSpace(chart.ValueAxisTitle),
            HideValueAxis = chart.HideValueAxis,
            HideCategoryAxis = chart.HideCategoryAxis,
            ShowGridlines = chart.ShowGridlines,
            ShowDataTable = chart.ShowDataTable,
            HideDataTableKeys = chart.HideDataTableKeys
        };

        /// <summary>
        /// Копия диаграммы с этим расположением. Своё название оси сохраняется, пустое
        /// получает заготовку; оси, сетка и таблица ставятся только там, где у диаграммы
        /// есть оси.
        /// </summary>
        public ChartModel ApplyTo(ChartModel chart)
        {
            var next = chart.Clone();
            next.ShowTitle = ShowTitle;
            next.Legend = Legend;
            next.ShowDataLabels = ShowDataLabels;
            next.DataLabelPosition = DataLabelPosition;

            if (next.HasAxes)
            {
                next.CategoryAxisTitle = CategoryTitle ? Keep(next.CategoryAxisTitle) : null;
                next.ValueAxisTitle = ValueTitle ? Keep(next.ValueAxisTitle) : null;
                next.HideValueAxis = HideValueAxis;
                next.HideCategoryAxis = HideCategoryAxis;
                next.ShowGridlines = ShowGridlines;
                next.ShowDataTable = ShowDataTable && next.CanShowDataTable;
                next.HideDataTableKeys = HideDataTableKeys;
            }

            // Без легенды у круговой нечем различить доли — подписи берут это на себя.
            if (next.IsCircular && Legend == ChartLegendPosition.None) next.ShowDataLabels = true;
            return next;

            static string Keep(string? text) => string.IsNullOrWhiteSpace(text) ? AxisTitlePlaceholder : text!;
        }

        /// <summary>Пресет для галереи макетов: образец, подсказка и правка, как у готовых.</summary>
        public ChartPresets.Preset ToPreset() => new(Name, Describe(), ApplyTo);

        private string Describe()
        {
            var parts = new List<string>();
            parts.Add(ShowTitle ? "название" : "без названия");
            parts.Add(Legend switch
            {
                ChartLegendPosition.None => "без легенды",
                ChartLegendPosition.Right => "легенда справа",
                ChartLegendPosition.Left => "легенда слева",
                ChartLegendPosition.Top => "легенда сверху",
                _ => "легенда снизу"
            });
            if (ShowDataLabels) parts.Add("подписи значений");
            if (CategoryTitle && ValueTitle) parts.Add("названия обеих осей");
            else if (CategoryTitle) parts.Add("название оси категорий");
            else if (ValueTitle) parts.Add("название оси значений");
            if (ShowDataTable) parts.Add("таблица данных");
            string text = string.Join(", ", parts);
            return "Свой макет: " + text;
        }
    }

    public sealed class ChartUserLayoutData
    {
        public List<ChartUserLayout> Layouts { get; set; } = new();
    }

    /// <summary>
    /// Свои экспресс-макеты диаграмм. Хранятся в настройках программы и видны во всех
    /// проектах, как свои стили и наборы цветов.
    /// </summary>
    public static class ChartUserLayouts
    {
        private const string SettingsKey = "ChartUserLayouts";
        public const int MaxLayouts = 40;
        private static readonly ILogger _log = Log.ForContext(typeof(ChartUserLayouts));

        public static List<ChartUserLayout> Load()
        {
            try
            {
                var data = CoreServices.GetService<ISettingsService>()?.GetModuleSettings<ChartUserLayoutData>(SettingsKey);
                return data?.Layouts?.Where(l => l is not null).ToList() ?? new List<ChartUserLayout>();
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[CHART] Свои макеты не прочитались");
                return new List<ChartUserLayout>();
            }
        }

        public static void Save(IEnumerable<ChartUserLayout> layouts)
        {
            var settings = CoreServices.GetService<ISettingsService>();
            if (settings is null) return;

            try
            {
                settings.SaveModuleSettings(SettingsKey, new ChartUserLayoutData { Layouts = layouts.ToList() });
                settings.Save();
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[CHART] Свои макеты не сохранились");
            }
        }

        /// <summary>Сохраняет расположение элементов диаграммы новым макетом «Мой макет N».</summary>
        public static ChartUserLayout? AddFrom(ChartModel chart)
        {
            var layouts = Load();
            if (layouts.Count >= MaxLayouts) return null;

            int number = 1;
            while (layouts.Any(l => l.Name == "Мой макет " + number)) number++;
            var layout = ChartUserLayout.From(chart, "Мой макет " + number);
            layouts.Add(layout);
            Save(layouts);
            return layout;
        }

        /// <summary>Переписывает макет расположением элементов диаграммы. Имя остаётся.</summary>
        public static void UpdateFrom(string id, ChartModel chart)
        {
            var layouts = Load();
            int index = layouts.FindIndex(l => l.Id == id);
            if (index < 0) return;
            var updated = ChartUserLayout.From(chart, layouts[index].Name);
            updated.Id = id;
            layouts[index] = updated;
            Save(layouts);
        }

        public static void Delete(string id)
        {
            var layouts = Load();
            if (layouts.RemoveAll(l => l.Id == id) > 0) Save(layouts);
        }

        public static void Rename(string id, string name)
        {
            name = name.Trim();
            if (name.Length == 0) return;
            var layouts = Load();
            if (layouts.FirstOrDefault(l => l.Id == id) is not { } layout || layout.Name == name) return;
            layout.Name = name;
            Save(layouts);
        }
    }
}
