using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Writersword.Modules.TextEditor.Models.Document
{
    /// <summary>
    /// Способ задания ширины столбца таблицы.
    /// </summary>
    public enum TableColumnWidthType
    {
        /// <summary>Ширина вычисляется автоматически.</summary>
        Auto = 0,
        /// <summary>Фиксированная ширина в мм.</summary>
        Fixed = 1,
        /// <summary>Процент от ширины таблицы.</summary>
        Percent = 2
    }

    /// <summary>
    /// Стиль границы ячейки.
    /// </summary>
    public enum BorderStyle
    {
        None = 0,
        Single = 1,
        Double = 2,
        Dashed = 3,
        Dotted = 4,
        Thick = 5
    }

    /// <summary>
    /// Вертикальное выравнивание в ячейке таблицы.
    /// </summary>
    public enum VerticalAlignment
    {
        Top = 0,
        Middle = 1,
        Bottom = 2
    }

    /// <summary>
    /// Описание одного столбца таблицы.
    /// </summary>
    public sealed class TableColumnDefinition
    {
        public TableColumnWidthType WidthType { get; set; } = TableColumnWidthType.Auto;
        public double WidthValue { get; set; }
    }

    /// <summary>
    /// Границы ячейки таблицы.
    /// </summary>
    public sealed class CellBorders
    {
        public BorderStyle Top { get; set; } = BorderStyle.Single;
        public BorderStyle Bottom { get; set; } = BorderStyle.Single;
        public BorderStyle Left { get; set; } = BorderStyle.Single;
        public BorderStyle Right { get; set; } = BorderStyle.Single;
        public double ThicknessPt { get; set; } = 0.5;

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Color { get; set; }

        public CellBorders Clone() => (CellBorders)MemberwiseClone();
    }

    /// <summary>
    /// Одна ячейка таблицы.
    /// Содержит список параграфов (как и обычный поток документа).
    /// </summary>
    public sealed class TableCell
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>Содержимое ячейки — список параграфов.</summary>
        public List<ParagraphBlock> Paragraphs { get; set; } = new() { new ParagraphBlock() };

        /// <summary>
        /// Индекс строки (0-based). Для объединённых ячеек — строка начала.
        /// </summary>
        public int Row { get; set; }

        /// <summary>
        /// Индекс столбца (0-based). Для объединённых ячеек — столбец начала.
        /// </summary>
        public int Column { get; set; }

        /// <summary>Количество объединённых строк (1 = нет объединения).</summary>
        public int RowSpan { get; set; } = 1;

        /// <summary>Количество объединённых столбцов (1 = нет объединения).</summary>
        public int ColSpan { get; set; } = 1;

        /// <summary>Цвет фона ячейки. Null — прозрачный.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? BackgroundColor { get; set; }

        public CellBorders Borders { get; set; } = new();

        public VerticalAlignment VerticalAlignment { get; set; } = VerticalAlignment.Top;

        /// <summary>Внутренние отступы ячейки в пунктах.</summary>
        public double PaddingTopPt { get; set; } = 4;
        public double PaddingBottomPt { get; set; } = 4;
        public double PaddingLeftPt { get; set; } = 6;
        public double PaddingRightPt { get; set; } = 6;
    }

    /// <summary>
    /// Таблица в документе.
    /// Реализована как кастомный контрол (не DataGrid).
    /// </summary>
    public sealed class TableBlock : BlockModel
    {
        public override BlockType BlockType => BlockType.Table;

        /// <summary>Количество строк.</summary>
        public int RowCount { get; set; }

        /// <summary>Количество столбцов.</summary>
        public int ColumnCount { get; set; }

        /// <summary>Определения столбцов (ширины).</summary>
        public List<TableColumnDefinition> Columns { get; set; } = new();

        /// <summary>
        /// Все ячейки таблицы в порядке строк.
        /// При объединении ячеек в списке присутствует только "главная" ячейка.
        /// </summary>
        public List<TableCell> Cells { get; set; } = new();

        /// <summary>Имя готового стиля таблицы. Null — кастомное оформление.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? StyleName { get; set; }

        /// <summary>Ширина таблицы в процентах от текстовой области (100 = во всю ширину).</summary>
        public double WidthPercent { get; set; } = 100;

        /// <summary>
        /// Отступ таблицы слева от начала текстовой области в пунктах.
        /// Сдвигает всю таблицу горизонтально. Управляется через левый маркер линейки в режиме таблицы.
        /// </summary>
        public double LeftIndentPt { get; set; } = 0;

        /// <summary>
        /// Повторять первую строку как заголовок на каждой странице.
        /// </summary>
        public bool RepeatHeader { get; set; } = false;

        /// <summary>Режим разбивки таблицы по страницам.</summary>
        public TableSplitMode SplitMode { get; set; } = TableSplitMode.ByRow;

        /// <summary>Текст после таблицы перед разрывом страницы. Null = не рисовать.</summary>
        public string? BreakLabel { get; set; }

        /// <summary>Текст перед продолжением таблицы на следующей странице. Null = не рисовать.</summary>
        public string? ContinuationLabel { get; set; }

        /// <summary>
        /// Возвращает ячейку по индексу строки и столбца.
        /// Учитывает объединённые ячейки (возвращает главную ячейку).
        /// </summary>
        public TableCell? GetCell(int row, int column)
        {
            foreach (var cell in Cells)
            {
                if (row >= cell.Row && row < cell.Row + cell.RowSpan &&
                    column >= cell.Column && column < cell.Column + cell.ColSpan)
                    return cell;
            }
            return null;
        }
    }

    /// <summary>Режим разбивки таблицы по страницам.</summary>
    public enum TableSplitMode
    {
        ByRow = 0,
        ByCell = 1
    }
}