using System;
using System.Collections.Generic;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Правки таблиц: строки и столбцы, вставленные или удалённые правкой, и смена
    /// оформления таблицы.
    ///
    /// Строка или столбец, вставленные при записи исправлений, отмечаются на своих
    /// ячейках (<see cref="TableCell.Inserted"/>): отклонение убирает их целиком, с
    /// текстом. Удалённые остаются в таблице с отметкой удаления
    /// (<see cref="TableCell.Deleted"/>) и текстом, зачёркнутым как удалённый, —
    /// принятие убирает их, отклонение возвращает. Смена оформления лежит на самой
    /// таблице (<see cref="TableBlock.FormatChange"/>) прежним видом из
    /// <see cref="TableFrame"/>.
    /// </summary>
    public static class TableRevisions
    {
        /// <summary>Строка или столбец таблицы.</summary>
        public enum Axis
        {
            Row,
            Column
        }

        /// <summary>Строка или столбец, вставленные или удалённые правкой целиком.</summary>
        public sealed class Group
        {
            public TableBlock Table { get; init; } = null!;
            public Axis Axis { get; init; }
            public int Index { get; init; }
            public bool Deleted { get; init; }
            public RevisionInfo Info { get; init; } = new();
            public List<TableCell> Cells { get; init; } = new();
        }

        /// <summary>У таблицы есть свои правки: строки, столбцы или оформление.</summary>
        public static bool HasMarks(TableBlock table)
        {
            if (table.FormatChange is not null) return true;
            foreach (var cell in table.Cells)
                if (cell.Inserted is not null || cell.Deleted is not null)
                    return true;
            return false;
        }

        /// <summary>
        /// Строки и столбцы с правкой. Строка — вставлена (удалена), если все
        /// ячейки, которые в ней начинаются, отмечены вставкой (удалением); столбец —
        /// так же по ячейкам, которые в нём начинаются, кроме ячеек уже найденных
        /// строк.
        /// </summary>
        public static List<Group> Groups(TableBlock table)
        {
            var groups = new List<Group>();
            var inRows = new HashSet<TableCell>(ReferenceEqualityComparer.Instance);

            for (int row = 0; row < table.RowCount; row++)
            {
                var cells = new List<TableCell>();
                foreach (var cell in table.Cells)
                    if (cell.Row == row) cells.Add(cell);
                if (cells.Count == 0) continue;

                if (cells.TrueForAll(c => c.Deleted is not null))
                {
                    groups.Add(new Group { Table = table, Axis = Axis.Row, Index = row, Deleted = true, Info = cells[0].Deleted!, Cells = cells });
                    foreach (var cell in cells) inRows.Add(cell);
                }
                else if (cells.TrueForAll(c => c.Inserted is not null))
                {
                    groups.Add(new Group { Table = table, Axis = Axis.Row, Index = row, Deleted = false, Info = cells[0].Inserted!, Cells = cells });
                    foreach (var cell in cells) inRows.Add(cell);
                }
            }

            for (int column = 0; column < table.ColumnCount; column++)
            {
                var cells = new List<TableCell>();
                var own = new List<TableCell>();
                foreach (var cell in table.Cells)
                {
                    if (cell.Column != column) continue;
                    cells.Add(cell);
                    if (!inRows.Contains(cell)) own.Add(cell);
                }
                if (own.Count == 0) continue;

                if (own.TrueForAll(c => c.Deleted is not null))
                    groups.Add(new Group { Table = table, Axis = Axis.Column, Index = column, Deleted = true, Info = own[0].Deleted!, Cells = cells });
                else if (own.TrueForAll(c => c.Inserted is not null))
                    groups.Add(new Group { Table = table, Axis = Axis.Column, Index = column, Deleted = false, Info = own[0].Inserted!, Cells = cells });
            }

            return groups;
        }

        // ── Строки и столбцы ─────────────────────────────────────────────

        /// <summary>
        /// Убирает строку таблицы. Объединённая ячейка, которая её накрывает или в
        /// ней начинается и идёт ниже, остаётся — на строку короче.
        /// </summary>
        public static void RemoveRow(TableBlock table, int row)
        {
            for (int i = table.Cells.Count - 1; i >= 0; i--)
            {
                var cell = table.Cells[i];

                if (cell.Row == row)
                {
                    if (cell.RowSpan > 1) cell.RowSpan--;
                    else table.Cells.RemoveAt(i);
                }
                else if (cell.Row < row && cell.Row + cell.RowSpan > row)
                {
                    cell.RowSpan--;
                }
            }

            foreach (var cell in table.Cells)
                if (cell.Row > row) cell.Row--;

            table.RemoveRowMinHeight(row);
            table.RowCount = Math.Max(0, table.RowCount - 1);
        }

        /// <summary>
        /// Убирает столбец таблицы. Объединённая ячейка, которая его накрывает или в
        /// нём начинается и идёт правее, остаётся — на столбец уже.
        /// </summary>
        public static void RemoveColumn(TableBlock table, int column)
        {
            for (int i = table.Cells.Count - 1; i >= 0; i--)
            {
                var cell = table.Cells[i];

                if (cell.Column == column)
                {
                    if (cell.ColSpan > 1) cell.ColSpan--;
                    else table.Cells.RemoveAt(i);
                }
                else if (cell.Column < column && cell.Column + cell.ColSpan > column)
                {
                    cell.ColSpan--;
                }
            }

            foreach (var cell in table.Cells)
                if (cell.Column > column) cell.Column--;

            if (column < table.Columns.Count) table.Columns.RemoveAt(column);
            table.ColumnCount = Math.Max(0, table.ColumnCount - 1);
        }

        // ── Принятие и отклонение ────────────────────────────────────────

        /// <summary>
        /// Таблица как после отклонения её строк, столбцов и оформления — для вида
        /// «все правки отклонены» (RevisionSignature) на копии таблицы. Текст ячеек
        /// не трогается: его вид при отклонении пишет сам отпечаток. Сетка не
        /// ремонтируется — новые ячейки дали бы новый отпечаток.
        /// </summary>
        public static void RejectStructure(TableBlock table)
        {
            var removeRows = new List<int>();
            var removeColumns = new List<int>();
            foreach (var group in Groups(table))
            {
                if (group.Deleted) continue;
                if (group.Axis == Axis.Row) removeRows.Add(group.Index);
                else removeColumns.Add(group.Index);
            }

            removeRows.Sort();
            for (int i = removeRows.Count - 1; i >= 0; i--) RemoveRow(table, removeRows[i]);
            removeColumns.Sort();
            for (int i = removeColumns.Count - 1; i >= 0; i--) RemoveColumn(table, removeColumns[i]);

            foreach (var cell in table.Cells)
            {
                cell.Inserted = null;
                cell.Deleted = null;
            }

            if (table.FormatChange is { } change)
            {
                TableFrame.Restore(table, change.Previous);
                table.FormatChange = null;
            }
        }

        /// <summary>
        /// Принимает или отклоняет правки таблицы. <paramref name="only"/> — только
        /// строки и столбцы, в которые входит хоть одна из этих ячеек (null — все);
        /// <paramref name="format"/> — и смену оформления. Принять: удалённые строки и
        /// столбцы уходят, вставленные остаются без отметки, их текст — обычный.
        /// Отклонить: вставленные уходят, удалённые возвращаются с прежним текстом.
        /// Возвращает true, если таблица менялась.
        /// </summary>
        public static bool Resolve(
            DocumentModel document, TableBlock table, bool accept,
            ICollection<TableCell>? only = null, bool format = true)
        {
            bool changed = false;

            var groups = Groups(table);
            var chosen = new List<Group>();
            foreach (var group in groups)
            {
                if (only is not null)
                {
                    bool hit = false;
                    foreach (var cell in group.Cells)
                        if (only.Contains(cell)) { hit = true; break; }
                    if (!hit) continue;
                }
                chosen.Add(group);
            }

            // Текст ячеек, которые остаются, решается тем же решением: вставленная
            // строка, принятая, — с обычным текстом; удалённая, отклонённая, — с
            // возвращённым.
            var keptParagraphs = new HashSet<ParagraphBlock>(ReferenceEqualityComparer.Instance);
            foreach (var group in chosen)
            {
                bool goes = accept ? group.Deleted : !group.Deleted;
                if (goes) continue;
                foreach (var cell in group.Cells)
                    foreach (var paragraph in cell.ParagraphsDeep())
                        keptParagraphs.Add(paragraph);
            }

            if (keptParagraphs.Count > 0)
            {
                RevisionService.Resolve(
                    document,
                    paragraph => keptParagraphs.Contains(paragraph)
                        ? new RevisionService.Scope(0, int.MaxValue, true)
                        : null,
                    accept);
                changed = true;
            }

            // Отметки снимаются со всех ячеек выбранных строк и столбцов, затем
            // уходящие убираются — с конца, чтобы номера не сдвигались.
            var removeRows = new List<int>();
            var removeColumns = new List<int>();
            foreach (var group in chosen)
            {
                bool goes = accept ? group.Deleted : !group.Deleted;
                if (goes)
                {
                    if (group.Axis == Axis.Row) removeRows.Add(group.Index);
                    else removeColumns.Add(group.Index);
                    continue;
                }

                foreach (var cell in group.Cells)
                {
                    if (accept) cell.Inserted = null;
                    else cell.Deleted = null;
                }
                changed = true;
            }

            removeRows.Sort();
            for (int i = removeRows.Count - 1; i >= 0; i--)
            {
                RemoveRow(table, removeRows[i]);
                changed = true;
            }

            removeColumns.Sort();
            for (int i = removeColumns.Count - 1; i >= 0; i--)
            {
                RemoveColumn(table, removeColumns[i]);
                changed = true;
            }

            // Отметки, оставшиеся без строки и столбца (ячейки, разрезанные
            // объединением), при решении по всей таблице снимаются: правку им не
            // показать отдельно.
            if (only is null)
            {
                foreach (var cell in table.Cells)
                {
                    if (cell.Inserted is null && cell.Deleted is null) continue;
                    cell.Inserted = null;
                    cell.Deleted = null;
                    changed = true;
                }
            }

            if (format && table.FormatChange is { } change)
            {
                if (!accept) TableFrame.Restore(table, change.Previous);
                table.FormatChange = null;
                changed = true;
            }

            if (removeRows.Count > 0 || removeColumns.Count > 0)
                TableGridRepair.Repair(table);

            return changed;
        }
    }

    /// <summary>
    /// Пункт рецензии таблицы: строка, столбец или оформление. В документе не
    /// живёт — служит ключом пункта панели и тем, что решает пункт: решение по нему
    /// трогает только свою строку, свой столбец или оформление
    /// (RevisionService.ResolveBlocks).
    /// </summary>
    public sealed class TableRevisionPart : BlockModel
    {
        public override BlockType BlockType => BlockType.Table;

        public TableBlock Table { get; init; } = null!;

        /// <summary>Ячейки строки или столбца; пусто — пункт оформления.</summary>
        public IReadOnlyList<TableCell> Cells { get; init; } = Array.Empty<TableCell>();

        /// <summary>Пункт — смена оформления таблицы.</summary>
        public bool Format { get; init; }

        /// <summary>Строка или столбец; у пункта оформления не важно.</summary>
        public TableRevisions.Axis Axis { get; init; }

        public override bool Equals(object? obj)
            => obj is TableRevisionPart other
               && ReferenceEquals(other.Table, Table)
               && other.Format == Format
               && other.Axis == Axis
               && (Cells.Count == 0 ? other.Cells.Count == 0
                   : other.Cells.Count > 0 && other.Cells[0].Id == Cells[0].Id);

        public override int GetHashCode()
            => HashCode.Combine(Table.Id, Format, Axis, Cells.Count > 0 ? Cells[0].Id : Guid.Empty);
    }
}
