using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Writersword.Core.Models.Project
{
    /// <summary>
    /// Именованная палитра цветов. Может быть локальной (в проекте) или
    /// глобальной (в settings.json приложения).
    /// </summary>
    public class ColorPalette
    {
        [JsonProperty("Id")]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [JsonProperty("Name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("Colors")]
        public List<string> Colors { get; set; } = new();

        // Видимость в быстром попапе: true — палитра показывается среди образцов,
        // false — хранится, но в попап не попадает. Управляется глазиком в редакторе.
        [JsonProperty("Visible")]
        public bool Visible { get; set; } = true;
    }

    /// <summary>
    /// Глобальная библиотека палитр приложения. Хранится в settings.json через
    /// механизм module-settings (ключ "ColorPalettes").
    /// </summary>
    public class GlobalPaletteData
    {
        /// <summary>Редактируемые «стандартные» цвета (глобальные).</summary>
        [JsonProperty("StandardColors")]
        public List<string> StandardColors { get; set; } = new();

        /// <summary>Глобальные именованные палитры.</summary>
        [JsonProperty("Palettes")]
        public List<ColorPalette> Palettes { get; set; } = new();

        // Состояние сворачивания секций (ключ секции -> свёрнута ли). Глобальное,
        // чтобы быстрый попап и редактор показывали одинаковую раскладку везде.
        [JsonProperty("CollapsedSections")]
        public Dictionary<string, bool> CollapsedSections { get; set; } = new();
    }
}
