using System.Text.Json.Serialization;
using Writersword.Core.Enums;

namespace Writersword.Core.Models.WorkModes
{
    /// <summary>
    /// Слот модуля в WorkMode
    /// Описывает расположение и состояние одного модуля
    /// </summary>
    public class ModuleSlot
    {
        /// <summary>Идентификатор модуля</summary>
        public string ModuleId { get; set; } = "";

        /// <summary>
        /// ID контейнера в котором находится модуль
        /// null если модуль в флоат окне
        /// </summary>
        public string? ContainerId { get; set; }

        /// <summary>Находится ли модуль в плавающем окне</summary>
        public bool IsFloating { get; set; }

        /// <summary>Порядок вкладки если в контейнере несколько модулей</summary>
        public int TabOrder { get; set; }

        /// <summary>Является ли модуль активной вкладкой в контейнере</summary>
        public bool IsActiveTab { get; set; }

        /// <summary>X координата плавающего окна</summary>
        public int FloatX { get; set; }

        /// <summary>Y координата плавающего окна</summary>
        public int FloatY { get; set; }

        /// <summary>Ширина плавающего окна</summary>
        public int FloatWidth { get; set; } = 800;

        /// <summary>Высота плавающего окна</summary>
        public int FloatHeight { get; set; } = 600;

        /// <summary>
        /// Предпочитаемая позиция при первом добавлении модуля
        /// Используется только при создании нового модуля
        /// Не сохраняется в JSON
        /// </summary>
        [JsonIgnore]
        public PreferredDockPosition PreferredPosition { get; set; } = PreferredDockPosition.RightAsTab;

        /// <summary>
        /// Можно ли закрыть модуль
        /// Берётся из метаданных модуля
        /// Не сохраняется в JSON
        /// </summary>
        [JsonIgnore]
        public bool IsCloseable { get; set; } = true;

        /// <summary>
        /// Минимальная ширина модуля (px)
        /// Берётся из метаданных модуля
        /// Не сохраняется в JSON
        /// </summary>
        [JsonIgnore]
        public double MinWidth { get; set; } = 200;

        /// <summary>
        /// Минимальная высота модуля (px)
        /// Берётся из метаданных модуля
        /// Не сохраняется в JSON
        /// </summary>
        [JsonIgnore]
        public double MinHeight { get; set; } = 150;
    }
}