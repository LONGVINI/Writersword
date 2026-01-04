using System;
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

        /// <summary>Можно ли закрыть модуль</summary>
        public bool IsCloseable { get; set; } = true;

        /// <summary>Минимальная ширина модуля (px)</summary>
        public double MinWidth { get; set; } = 200;

        /// <summary>Минимальная высота модуля (px)</summary>
        public double MinHeight { get; set; } = 150;

        /// <summary>Предпочитаемая позиция при первом добавлении</summary>
        public PreferredDockPosition PreferredPosition { get; set; } = PreferredDockPosition.RightAsTab;

        /// <summary>Дата последнего доступа к модулю</summary>
        public DateTime LastAccessedAt { get; set; } = DateTime.Now;
    }
}