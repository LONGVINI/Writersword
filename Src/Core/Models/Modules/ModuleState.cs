namespace Writersword.Core.Models.Modules
{
    /// <summary>
    /// Состояние модуля для сохранения/восстановления
    /// Хранится в JSON файле проекта (.writersword)
    /// </summary>
    public class ModuleState
    {
        /// <summary>Позиция скролла</summary>
        public double ScrollPosition { get; set; }

        /// <summary>Размер модуля (ширина, высота)</summary>
        public ModuleSize? Size { get; set; }

        /// <summary>Произвольные данные модуля (JSON строка)</summary>
        public string? CustomData { get; set; }
    }
}