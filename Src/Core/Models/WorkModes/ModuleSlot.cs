using ReactiveUI;
using System;
using System.Collections.Generic;
using Writersword.Core.Enums;

namespace Writersword.Core.Models.WorkModes
{
    /// <summary>
    /// Слот для размещения модуля внутри WorkMode
    /// Определяет какой модуль находится в режиме, где он расположен и какого размера
    /// </summary>
    public class ModuleSlot : ReactiveObject
    { 
        /// <summary>Уникальный ID слота</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>Тип модуля в этом слоте</summary>
        public ModuleType ModuleType { get; set; }

        /// <summary>Позиция в сетке (строка, столбец)</summary>
        public WorkModeGridPosition Position { get; set; } = new();

        /// <summary>Размер в сетке (сколько строк и столбцов занимает)</summary>
        public WorkModeGridSize Size { get; set; } = new(1, 1);

        /// <summary>Минимальная ширина модуля (px)</summary>
        public double MinWidth { get; set; } = 200;

        /// <summary>Минимальная высота модуля (px)</summary>
        public double MinHeight { get; set; } = 150;

        /// <summary>Можно ли изменять размер модуля</summary>
        public bool IsResizable { get; set; } = true;

        /// <summary>Можно ли закрыть модуль в этом WorkMode</summary>
        public bool IsCloseable { get; set; } = true;

        /// <summary>Состояние модуля (для восстановления при загрузке)</summary>
        public Dictionary<string, object> ModuleState { get; set; } = new();

        /// <summary>Видим ли модуль (можно скрыть, но не удалять)</summary>
        private bool _isVisible = true;
        /// <summary>
        /// Видим ли модуль (можно скрыть, но не удалять)
        /// ИСПРАВЛЕНО: Использует ReactiveUI для уведомления UI об изменениях
        /// </summary>
        public bool IsVisible
        {
            get => _isVisible;
            set => this.RaiseAndSetIfChanged(ref _isVisible, value);
        }
    }
}