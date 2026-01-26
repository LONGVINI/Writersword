using System.Text.Json.Serialization;
using Writersword.Core.Enums;

namespace Writersword.Core.Models.WorkModes
{
    /// <summary>
    /// Слот модуля в WorkMode
    /// Описывает один модуль и его параметры отображения
    /// </summary>
    public class ModuleSlot
    {
        /// <summary>Идентификатор модуля (строка)</summary>
        public string ModuleId { get; set; } = "";

        /// <summary>Видим ли модуль (показан/скрыт)</summary>
        public bool IsVisible { get; set; } = true;

        /// <summary>Предпочитаемая позиция при первом добавлении</summary>
        public PreferredDockPosition PreferredPosition { get; set; } = PreferredDockPosition.RightAsTab;

        /// <summary>ID панели в которой находится модуль</summary>
        public string? PanelId { get; set; }

        /// <summary>Пропорция размера панели (0.0-1.0)</summary>
        public double PanelProportion { get; set; }

        /// <summary>Порядок вкладок в панели</summary>
        public int TabOrder { get; set; }

        /// <summary>Является ли модуль активной вкладкой в панели</summary>
        public bool IsFocused { get; set; }

        /// <summary>Находится ли модуль в плавающем окне</summary>
        public bool IsFloating { get; set; }

        /// <summary>X координата плавающего окна</summary>
        public double FloatX { get; set; }

        /// <summary>Y координата плавающего окна</summary>
        public double FloatY { get; set; }

        /// <summary>Ширина плавающего окна</summary>
        public double FloatWidth { get; set; }

        /// <summary>Высота плавающего окна</summary>
        public double FloatHeight { get; set; }

        /// <summary>Можно ли закрыть модуль - берётся из дефолтных настроек</summary>
        [JsonIgnore]
        public bool IsCloseable { get; set; } = true;

        /// <summary>Минимальная ширина модуля (px) - берётся из дефолтных настроек</summary>
        [JsonIgnore]
        public double MinWidth { get; set; } = 200;

        /// <summary>Минимальная высота модуля (px) - берётся из дефолтных настроек</summary>
        [JsonIgnore]
        public double MinHeight { get; set; } = 150;
    }
}