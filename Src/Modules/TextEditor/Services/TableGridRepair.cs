using System.Collections.Generic;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Приведение сетки таблицы к целому виду: каждая клетка RowCount×ColumnCount
    /// принадлежит ровно одной ячейке — не двум и не ни одной.
    ///
    /// Отрисовка рисует ячейки, а не сетку: линию между двумя клетками рисует та
    /// ячейка, которой клетка принадлежит. Ничья клетка не рисует ничего — на
    /// листе получается провал без собственных границ, обведённый только теми
    /// линиями, которые нарисовали соседи со своей стороны. Именно так выглядит
    /// таблица после вставки и удаления строк и столбцов рядом с объединёнными
    /// ячейками, и после деления ячейки со вставкой строки или столбца: те
    /// операции заполняли новую строку не целиком, а удаляли объединённую
    /// ячейку вместе со всеми накрытыми ею клетками.
    ///
    /// Ремонт вызывается после каждой структурной операции — до фиксации шага
    /// отмены, чтобы в снимок попала уже целая сетка, — и один раз при загрузке
    /// документа: таблицы, испорченные прежними версиями редактора, чинятся при
    /// открытии, без правки со стороны пользователя.
    /// </summary>
    public static class TableGridRepair
    {
        /// <summary>
        /// Чинит все таблицы документа. Возвращает true, если что-то было
        /// изменено — вызывающий может пометить документ требующим пересохранения.
        /// </summary>
        public static bool Repair(DocumentModel? document)
        {
            if (document is null) return false;

            bool changed = false;
            foreach (var section in document.Sections)
                foreach (var block in section.Blocks)
                    if (block is TableBlock table && Repair(table))
                        changed = true;

            return changed;
        }

        /// <summary>
        /// Чинит сетку одной таблицы. Возвращает true, если сетка была нецелой.
        /// </summary>
        public static bool Repair(TableBlock? table)
        {
            if (table is null) return false;

            int rows = table.RowCount;
            int cols = table.ColumnCount;
            if (rows <= 0 || cols <= 0) return false;

            bool changed = false;

            // Ячейка, НАЧАЛО которой за сеткой, — настоящая ячейка со своим текстом:
            // это счётчики строк и столбцов отстали от списка. Сетка растёт под неё,
            // а не наоборот: обрезка по счётчикам молча удаляла бы содержимое вместо
            // ремонта. Объединение, вылезающее за край, режется — это след неудачной
            // правки структуры, а не содержимое.
            foreach (var cell in table.Cells)
            {
                if (cell.Row < 0 || cell.Column < 0) continue;
                if (cell.Row >= rows) rows = cell.Row + 1;
                if (cell.Column >= cols) cols = cell.Column + 1;
            }

            if (rows != table.RowCount) { table.RowCount = rows; changed = true; }
            if (cols != table.ColumnCount) { table.ColumnCount = cols; changed = true; }

            // Ширины столбцов адресуются индексом столбца, поэтому список
            // дополняется вместе со счётчиком.
            while (table.Columns.Count < cols)
            {
                table.Columns.Add(new TableColumnDefinition
                {
                    WidthType = TableColumnWidthType.Auto
                });
                changed = true;
            }

            // Отрицательные координаты не адресуют ничего — такую запись не
            // нарисовать и не выделить.
            for (int i = table.Cells.Count - 1; i >= 0; i--)
            {
                var cell = table.Cells[i];

                if (cell.Row < 0 || cell.Column < 0)
                {
                    table.Cells.RemoveAt(i);
                    changed = true;
                    continue;
                }

                if (cell.RowSpan < 1) { cell.RowSpan = 1; changed = true; }
                if (cell.ColSpan < 1) { cell.ColSpan = 1; changed = true; }

                if (cell.Row + cell.RowSpan > rows)
                {
                    cell.RowSpan = rows - cell.Row;
                    changed = true;
                }
                if (cell.Column + cell.ColSpan > cols)
                {
                    cell.ColSpan = cols - cell.Column;
                    changed = true;
                }
            }

            // Раскладка по клеткам идёт в порядке списка: право на клетку получает
            // первая ячейка, которая её попросила. Тот же порядок у
            // TableBlock.GetCell, поэтому видимая таблица не переставляется —
            // меняются только те ячейки, которые до сих пор были невидимы.
            var owner = new TableCell?[rows, cols];
            var orphans = new List<TableCell>();

            foreach (var cell in table.Cells)
            {
                // Ширина, которая реально досталась: свободные столбцы подряд,
                // начиная с собственного, в первой строке ячейки.
                int freeCols = 0;
                while (freeCols < cell.ColSpan
                       && owner[cell.Row, cell.Column + freeCols] is null)
                    freeCols++;

                // Ни одной свободной клетки — ячейка целиком под чужой. Такую
                // не нарисовать и не выделить: она остаётся в списке мёртвым
                // грузом и мешает следующему ремонту. Удаляем.
                if (freeCols == 0)
                {
                    orphans.Add(cell);
                    continue;
                }

                // Высота: строки подряд, свободные на всей доставшейся ширине.
                // Первая строка свободна по построению freeCols.
                int freeRows = 1;
                while (freeRows < cell.RowSpan)
                {
                    int r = cell.Row + freeRows;
                    bool rowFree = true;
                    for (int c = cell.Column; c < cell.Column + freeCols; c++)
                    {
                        if (owner[r, c] is null) continue;
                        rowFree = false;
                        break;
                    }
                    if (!rowFree) break;
                    freeRows++;
                }

                if (cell.ColSpan != freeCols) { cell.ColSpan = freeCols; changed = true; }
                if (cell.RowSpan != freeRows) { cell.RowSpan = freeRows; changed = true; }

                for (int r = cell.Row; r < cell.Row + freeRows; r++)
                    for (int c = cell.Column; c < cell.Column + freeCols; c++)
                        owner[r, c] = cell;
            }

            foreach (var orphan in orphans)
            {
                table.Cells.Remove(orphan);
                changed = true;
            }

            // Оставшиеся ничьи клетки получают собственную ячейку. Оформление
            // берётся с соседа сверху, а если его нет — слева: вставленная строка
            // должна выглядеть как строка над ней, вставленный столбец — как
            // столбец слева. Пустая ячейка с оформлением по умолчанию рисовала бы
            // чужие рамки посреди таблицы с настроенными.
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    if (owner[r, c] is not null) continue;

                    var sample = r > 0 ? owner[r - 1, c] : null;
                    if (sample is null && c > 0) sample = owner[r, c - 1];
                    if (sample is null && table.Cells.Count > 0) sample = table.Cells[0];

                    var filler = sample is null
                        ? new TableCell { Row = r, Column = c }
                        : new TableCell
                        {
                            Row = r,
                            Column = c,
                            BackgroundColor = sample.BackgroundColor,
                            Borders = sample.Borders.Clone(),
                            VerticalAlignment = sample.VerticalAlignment,
                            PaddingTopPt = sample.PaddingTopPt,
                            PaddingBottomPt = sample.PaddingBottomPt,
                            PaddingLeftPt = sample.PaddingLeftPt,
                            PaddingRightPt = sample.PaddingRightPt
                        };

                    table.Cells.Add(filler);
                    owner[r, c] = filler;
                    changed = true;
                }
            }

            return changed;
        }
    }
}
