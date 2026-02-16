using Newtonsoft.Json;
using ReactiveUI;
using Writersword.Core.Enums;

namespace Writersword.Core.Models.WorkModes
{
    /// <summary>
    /// Слот модуля в WorkMode
    /// Описывает расположение и состояние одного модуля
    /// </summary>
    [JsonObject(MemberSerialization.OptIn)]
    public class ModuleSlot : ReactiveObject
    {
        private bool _isCurrentlyOpen;

        /// <summary>Идентификатор модуля</summary>
        [JsonProperty]
        public string ModuleType { get; set; } = "";

        /// <summary>Уникальный ID экземпляра модуля (стабильный между сессиями)</summary>
        [JsonProperty]
        public string InstanceId { get; set; } = "";

        /// <summary>
        /// Флаг: модуль в данный момент открыт в UI
        /// true = модуль виден в Dock
        /// false = модуль закрыт, но его InstanceId и позиция сохранены
        /// Сохраняется в workspace.json для восстановления состояния между сессиями
        /// </summary>
        [JsonProperty]
        public bool IsCurrentlyOpen
        {
            get => _isCurrentlyOpen;
            set => this.RaiseAndSetIfChanged(ref _isCurrentlyOpen, value);
        }

        /// <summary>
        /// Иерархический путь контейнера в котором находится модуль
        /// Примеры: "Root.Center", "Root.Right.Top", "Float.0"
        /// null если модуль не размещён или закрыт
        /// </summary>
        [JsonProperty]
        public string? Path { get; set; }

        /// <summary>Находится ли модуль в плавающем окне</summary>
        [JsonProperty]
        public bool IsFloating { get; set; }

        /// <summary>Порядок вкладки если в контейнере несколько модулей</summary>
        [JsonProperty]
        public int TabOrder { get; set; }

        /// <summary>Является ли модуль активной вкладкой в контейнере</summary>
        [JsonProperty]
        public bool IsActiveTab { get; set; }

        /// <summary>X координата плавающего окна</summary>
        [JsonProperty]
        public int FloatX { get; set; }

        /// <summary>Y координата плавающего окна</summary>
        [JsonProperty]
        public int FloatY { get; set; }

        /// <summary>Ширина плавающего окна</summary>
        [JsonProperty]
        public int FloatWidth { get; set; } = 800;

        /// <summary>Высота плавающего окна</summary>
        [JsonProperty]
        public int FloatHeight { get; set; } = 600;

        /// <summary>
        /// Предпочитаемая позиция при первом добавлении модуля
        /// Используется только при создании нового модуля
        /// Не сохраняется в JSON
        /// </summary>
        [JsonIgnore]
        public PreferredDockPosition PreferredPosition { get; set; } = PreferredDockPosition.RightAsTab;

        /// <summary>
        /// Категория модуля в этом WorkMode
        /// Определяет можно ли добавить/удалить модуль
        /// Берётся из дефолтной конфигурации WorkMode
        /// Не сохраняется в workspace.json
        /// </summary>
        [JsonIgnore]
        public ModuleCategory Category { get; set; } = ModuleCategory.Optional;

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