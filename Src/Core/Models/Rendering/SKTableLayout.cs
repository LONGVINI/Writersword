using System.Collections.Generic;

namespace Writersword.Core.Models.Rendering
{
    /// <summary>
    /// Параметры одной линии границы ячейки для рендеринга.
    /// </summary>
    public sealed class SKTableBorderLineLayout
    {
        /// <summary>Толщина линии в pt. 0 — не рисовать.</summary>
        public float WidthPt { get; init; } = 0.75f;

        /// <summary>Цвет линии в формате #RRGGBB.</summary>
        public string Color { get; init; } = "#000000";

        /// <summary>Стиль линии (0=Solid, 1=Dashed, 2=Double, 3=None).</summary>
        public int Style { get; init; }
    }

    /// <summary>
    /// Настройки всех четырёх границ ячейки для рендеринга.
    /// </summary>
    public sealed class SKTableCellBorderLayout
    {
        /// <summary>Верхняя граница.</summary>
        public SKTableBorderLineLayout Top { get; init; } = new();

        /// <summary>Нижняя граница.</summary>
        public SKTableBorderLineLayout Bottom { get; init; } = new();

        /// <summary>Левая граница.</summary>
        public SKTableBorderLineLayout Left { get; init; } = new();

        /// <summary>Правая граница.</summary>
        public SKTableBorderLineLayout Right { get; init; } = new();
    }

    /// <summary>
    /// Лейаут одного параграфа внутри ячейки таблицы.
    /// </summary>
    public sealed class SKTableParaLayout
    {
        /// <summary>Результат вёрстки параграфа.</summary>
        public SKTextLayout Layout { get; init; } = null!;

        /// <summary>
        /// Y-позиция верхнего края параграфа в pt
        /// относительно начала текстовой области ячейки (после PadTopPt).
        /// Включает SpaceBeforePt параграфа.
        /// </summary>
        public float Ypt { get; init; }

        /// <summary>Индекс параграфа в Paragraphs ячейки (0-based).</summary>
        public int ParagraphIndex { get; init; }
    }

    /// <summary>
    /// Результат вёрстки одной ячейки таблицы.
    /// Координаты в pt относительно верхнего левого угла таблицы.
    /// </summary>
    public sealed class SKTableCellLayout
    {
        /// <summary>Индекс строки (0-based).</summary>
        public int Row { get; init; }

        /// <summary>Индекс колонки (0-based).</summary>
        public int Column { get; init; }

        /// <summary>Количество объединённых строк.</summary>
        public int RowSpan { get; init; } = 1;

        /// <summary>Количество объединённых колонок.</summary>
        public int ColSpan { get; init; } = 1;

        /// <summary>X-позиция левого края ячейки в pt относительно таблицы.</summary>
        public float Xpt { get; init; }

        /// <summary>Y-позиция верхнего края ячейки в pt относительно таблицы.</summary>
        public float Ypt { get; init; }

        /// <summary>Ширина ячейки в pt.</summary>
        public float WidthPt { get; init; }

        /// <summary>Высота ячейки в pt — определяется содержимым с учётом отступов.</summary>
        public float HeightPt { get; set; }

        /// <summary>Внутренний отступ сверху в pt.</summary>
        public float PadTopPt { get; init; }

        /// <summary>Внутренний отступ снизу в pt.</summary>
        public float PadBottomPt { get; init; }

        /// <summary>Внутренний отступ слева в pt.</summary>
        public float PadLeftPt { get; init; }

        /// <summary>Внутренний отступ справа в pt.</summary>
        public float PadRightPt { get; init; }

        /// <summary>Цвет фона ячейки. Null — нет заливки.</summary>
        public string? BackgroundColor { get; init; }

        /// <summary>Вертикальное выравнивание содержимого (0=Top, 1=Middle, 2=Bottom).</summary>
        public int VerticalAlignment { get; init; }

        /// <summary>Настройки границ ячейки для рендеринга.</summary>
        public SKTableCellBorderLayout Borders { get; init; } = new();

        /// <summary>
        /// Суммарная высота содержимого ячейки в pt (без отступов).
        /// Вычисляется при вёрстке.
        /// </summary>
        public float ContentHeightPt { get; set; }

        /// <summary>Лейауты параграфов содержимого ячейки в порядке следования.</summary>
        public List<SKTableParaLayout> Paragraphs { get; } = new();
    }

    /// <summary>
    /// Результат вёрстки одной строки таблицы.
    /// </summary>
    public sealed class SKTableRowLayout
    {
        /// <summary>Индекс строки (0-based).</summary>
        public int Row { get; init; }

        /// <summary>Y-позиция верхнего края строки в pt относительно таблицы.</summary>
        public float Ypt { get; init; }

        /// <summary>Высота строки в pt — определяется самой высокой ячейкой.</summary>
        public float HeightPt { get; set; }

        /// <summary>Ячейки строки в порядке следования слева направо.</summary>
        public List<SKTableCellLayout> Cells { get; } = new();
    }

    /// <summary>
    /// Результат вёрстки всей таблицы.
    /// Координаты в pt относительно верхнего левого угла таблицы.
    /// Используется DocumentCanvas для рендеринга и линейкой для маркеров колонок.
    /// </summary>
    public sealed class SKTableLayout
    {
        /// <summary>Строки таблицы в порядке следования сверху вниз.</summary>
        public List<SKTableRowLayout> Rows { get; } = new();

        /// <summary>
        /// Ширины колонок в pt в порядке слева направо.
        /// Используется линейкой для отображения маркеров колонок.
        /// </summary>
        public List<float> ColumnWidthsPt { get; } = new();

        /// <summary>
        /// X-позиции левых краёв колонок в pt относительно таблицы.
        /// Длина всегда равна ColumnWidthsPt.Count.
        /// </summary>
        public List<float> ColumnOffsetsPt { get; } = new();

        /// <summary>Суммарная ширина таблицы в pt.</summary>
        public float TotalWidthPt { get; set; }

        /// <summary>Суммарная высота таблицы в pt.</summary>
        public float TotalHeightPt { get; set; }

        /// <summary>Количество строк.</summary>
        public int RowCount { get; init; }

        /// <summary>Количество колонок.</summary>
        public int ColumnCount { get; init; }

        /// <summary>
        /// Находит лейаут ячейки по индексам строки и колонки.
        /// Учитывает объединённые ячейки.
        /// Возвращает null если позиция вне таблицы.
        /// </summary>
        public SKTableCellLayout? FindCell(int row, int col)
        {
            foreach (var tableRow in Rows)
                foreach (var cell in tableRow.Cells)
                    if (row >= cell.Row && row < cell.Row + cell.RowSpan
                        && col >= cell.Column && col < cell.Column + cell.ColSpan)
                        return cell;
            return null;
        }

        /// <summary>
        /// Находит лейаут ячейки по точке клика в pt относительно таблицы.
        /// Возвращает null если точка вне таблицы.
        /// </summary>
        public SKTableCellLayout? HitTestCell(float xPt, float yPt)
        {
            foreach (var row in Rows)
                foreach (var cell in row.Cells)
                    if (xPt >= cell.Xpt && xPt <= cell.Xpt + cell.WidthPt
                        && yPt >= cell.Ypt && yPt <= cell.Ypt + cell.HeightPt)
                        return cell;
            return null;
        }

        /// <summary>
        /// Находит параграф внутри ячейки по точке клика в pt относительно таблицы.
        /// Возвращает null если точка вне любой ячейки.
        /// Используется DocumentCanvas для установки каретки по клику мыши.
        /// </summary>
        public (SKTableCellLayout Cell, SKTableParaLayout Para)? HitTestParagraph(
            float xPt, float yPt)
        {
            var cell = HitTestCell(xPt, yPt);
            if (cell is null) return null;

            float localY = yPt - cell.Ypt - cell.PadTopPt;

            SKTableParaLayout? bestPara = null;
            float bestDist = float.MaxValue;

            foreach (var para in cell.Paragraphs)
            {
                float paraBottom = para.Ypt + para.Layout.BlockHeightPt;
                float dist = localY < para.Ypt
                    ? para.Ypt - localY
                    : localY > paraBottom
                        ? localY - paraBottom
                        : 0f;

                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestPara = para;
                    if (dist == 0f) break;
                }
            }

            if (bestPara is null) return null;
            return (cell, bestPara);
        }

        /// <summary>
        /// Возвращает индекс колонки по X-позиции в pt относительно таблицы.
        /// Используется линейкой при drag маркера колонки.
        /// Возвращает -1 если позиция вне таблицы.
        /// </summary>
        public int HitTestColumn(float xPt)
        {
            for (int i = 0; i < ColumnOffsetsPt.Count; i++)
            {
                float left = ColumnOffsetsPt[i];
                float right = left + ColumnWidthsPt[i];
                if (xPt >= left && xPt <= right)
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// Возвращает общую высоту таблицы с учётом всех строк.
        /// Используется DocumentCanvas для разбивки таблицы по страницам.
        /// </summary>
        public float GetTotalHeightPt()
        {
            float h = 0f;
            foreach (var row in Rows)
                h += row.HeightPt;
            return h;
        }
    }
}