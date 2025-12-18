namespace Writersword.Core.Models.WorkModes
{
    /// <summary>
    /// Размер модуля в сетке WorkMode (сколько ячеек занимает)
    /// Например: RowSpan=2, ColumnSpan=3 означает модуль занимает 2 строки и 3 столбца
    /// </summary>
    public class WorkModeGridSize
    {
        /// <summary>Сколько строк занимает модуль</summary>
        public int RowSpan { get; set; }

        /// <summary>Сколько столбцов занимает модуль</summary>
        public int ColumnSpan { get; set; }

        /// <summary>Конструктор с параметрами по умолчанию</summary>
        public WorkModeGridSize(int rowSpan = 1, int columnSpan = 1)
        {
            RowSpan = rowSpan;
            ColumnSpan = columnSpan;
        }
    }
}