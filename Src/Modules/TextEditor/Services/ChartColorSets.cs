using System;
using System.Collections.Generic;
using System.Linq;
using Serilog;
using Writersword.Core.Interfaces.Services.Storage;
using Writersword.Core.Services;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>Свой набор цветов диаграммы: имя и цвета рядов (у круговой — долей) по порядку.</summary>
    public sealed class ChartColorSet
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string Name { get; set; } = string.Empty;

        /// <summary>Коды цветов подборщика: «#RRGGBB», «#AARRGGBB» или градиент «grad|…».</summary>
        public List<string> Colors { get; set; } = new();

        public ChartColorSet Clone() => new() { Id = Id, Name = Name, Colors = new List<string>(Colors) };
    }

    /// <summary>Свои наборы цветов в настройках приложения.</summary>
    public sealed class ChartColorSetData
    {
        public List<ChartColorSet> Sets { get; set; } = new();
    }

    /// <summary>
    /// Хранилище своих наборов цветов диаграмм. Наборы общие для всех проектов: их
    /// держат глобальные настройки, как и палитры подборщика цвета.
    /// </summary>
    public static class ChartColorSets
    {
        private const string SettingsKey = "ChartColorSets";

        /// <summary>Сколько цветов держит один набор: больше рядов на диаграмме не бывает.</summary>
        public const int MaxColors = 48;

        private static readonly ILogger _log = Log.ForContext(typeof(ChartColorSets));

        public static List<ChartColorSet> Load()
        {
            try
            {
                var data = CoreServices.GetService<ISettingsService>()?.GetModuleSettings<ChartColorSetData>(SettingsKey);
                return data?.Sets?
                    .Where(s => s is not null && s.Colors is { Count: > 0 })
                    .Select(s => s.Clone())
                    .ToList() ?? new List<ChartColorSet>();
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[CHART] Свои наборы цветов не прочитались");
                return new List<ChartColorSet>();
            }
        }

        public static void Save(IEnumerable<ChartColorSet> sets)
        {
            var settings = CoreServices.GetService<ISettingsService>();
            if (settings is null) return;

            try
            {
                settings.SaveModuleSettings(SettingsKey, new ChartColorSetData { Sets = sets.Select(s => s.Clone()).ToList() });
                settings.Save();
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[CHART] Свои наборы цветов не сохранились");
            }
        }
    }
}
