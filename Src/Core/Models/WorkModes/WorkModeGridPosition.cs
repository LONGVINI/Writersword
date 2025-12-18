namespace Writersword.Core.Models.WorkModes
{
    /// <summary>
    /// Позиция модуля в сетке WorkMode
    /// Используется для размещения модулей в рабочем пространстве
    /// </summary>
    public class WorkModeGridPosition
    {
        /// <summary>Номер строки (начинается с 0)</summary>
        public int Row { get; set; } = 0;

        /// <summary>Номер столбца (начинается с 0)</summary>
        public int Column { get; set; } = 0;
    }
}