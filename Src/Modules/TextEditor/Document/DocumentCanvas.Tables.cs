using System;
using System.Collections.Generic;
using Avalonia.Input;
using Writersword.Core.Models.Rendering;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Document
{
    public sealed partial class DocumentCanvas
    {
        // Строит потоковое выделение ячеек по порядку чтения (Row*ColumnCount+Col) от точки
        // нажатия (_selStartPara/_selStartChar) до курсора (cursorSlice/cursorChar). Заполняет
        // Добавляет в _cellFlowFull все ячейки таблицы с reading-index в [loIdx, hiIdx] целиком.
        // Словари должны быть очищены вызывающим.
        private bool BuildCellFlowWhole(TableBlock table, int loIdx, int hiIdx)
        {
            int colCount = Math.Max(1, table.ColumnCount);
            foreach (var cell in table.Cells)
            {
                int idx = cell.Row * colCount + cell.Column;
                if (idx < loIdx || idx > hiIdx) continue;
                _cellFlowFull.Add((table, cell.Row, cell.Column));
            }
            return _cellFlowFull.Count > 0;
        }

        // Поток при входе текстового выделения в таблицу извне: от верхнего-левого края (fromTop)
        // или нижнего-правого до ячейки курсора включительно. Только ЦЕЛЬНЫЕ ячейки.
        private bool BuildCellFlowFromEdge(TableBlock table, int cursorSlice, int cursorChar, bool fromTop)
        {
            _cellFlowRanges.Clear();
            _cellFlowFull.Clear();
            if (cursorSlice < 0 || cursorSlice >= _layouts.Count) return false;
            var cursorCell = _layouts[cursorSlice].Cell;
            if (cursorCell is null || cursorCell.Table != table) return false;

            int colCount = Math.Max(1, table.ColumnCount);
            int cIdx = cursorCell.Cell.Row * colCount + cursorCell.Cell.Column;
            int maxIdx = (table.RowCount - 1) * colCount + (table.ColumnCount - 1);

            int loIdx = fromTop ? 0 : cIdx;
            int hiIdx = fromTop ? cIdx : maxIdx;
            return BuildCellFlowWhole(table, loIdx, hiIdx);
        }

        // ── Таблица — структурные операции ────────────────────────────────

        /// <summary>
        /// Ставит каретку в активную ячейку после структурной правки таблицы и
        /// возвращает клавиатурный фокус канвасу.
        /// Голого RebuildLayouts здесь мало: строки и столбцы сдвинулись, раскладка
        /// пересобрана заново, и прежний индекс слайса (_caretPara) указывает уже не
        /// на тот абзац, а то и за пределы списка — каретка пропадает. Фокус же
        /// уходит на кнопку ленты, с которой пришла команда, а горячие клавиши
        /// редактора разбирает только OnKeyDown канваса: без возврата фокуса ни
        /// Ctrl+Z, ни любое другое сочетание до редактора не доходит.
        /// </summary>
        private void RestoreCaretAfterTableStructure()
        {
            var cell = _activeTableBlock?.GetCell(_activeCellRow, _activeCellCol);
            ParagraphBlock? target = cell is not null && cell.Paragraphs.Count > 0
                ? cell.Paragraphs[0]
                : null;

            _caretChar = target is null
                ? 0
                : Clamp(_caretChar, 0, target.GetPlainText().Length);

            RebuildAfterCellEdit(target);
            InvalidateFull();

            if (!IsFocused) Focus();
        }

        private void ExecuteTableAddRow(bool above)
        {
            if (_activeTableBlock is null) return;
            BeginTableEdit(_activeTableBlock, "Add row");
            int insertRow = above ? _activeCellRow : _activeCellRow + 1;
            foreach (var cell in _activeTableBlock.Cells)
                if (cell.Row >= insertRow) cell.Row++;
            for (int c = 0; c < _activeTableBlock.ColumnCount; c++)
                _activeTableBlock.Cells.Add(new TableCell { Row = insertRow, Column = c });
            // Заданные высоты сдвигаются вместе со строками, иначе они достались бы
            // соседям: список адресуется индексом строки.
            _activeTableBlock.InsertRowMinHeight(insertRow);
            _activeTableBlock.RowCount++;
            if (above) _activeCellRow++;
            CommitTableEdit();
            RestoreCaretAfterTableStructure();
        }

        // Цвет фона ячеек. Применяется к выделенному диапазону ячеек (_tableSelections), а если
        // диапазона нет — к активной ячейке под кареткой. color == null/пусто снимает заливку.
        private void ExecuteTableSetCellBackground(string? color)
        {
            string? value = string.IsNullOrWhiteSpace(color) ? null : color;

            var targets = new List<TableCell>();
            if (_tableSelections.Count > 0)
            {
                foreach (var kv in _tableSelections)
                {
                    int minRow = Math.Min(kv.Value.sr, kv.Value.er);
                    int maxRow = Math.Max(kv.Value.sr, kv.Value.er);
                    int minCol = Math.Min(kv.Value.sc, kv.Value.ec);
                    int maxCol = Math.Max(kv.Value.sc, kv.Value.ec);
                    foreach (var cell in kv.Key.Cells)
                    {
                        if (cell.Row < minRow || cell.Row > maxRow) continue;
                        if (cell.Column < minCol || cell.Column > maxCol) continue;
                        targets.Add(cell);
                    }
                }
            }
            else if (_activeTableBlock is not null)
            {
                foreach (var cell in _activeTableBlock.Cells)
                {
                    if (cell.Row != _activeCellRow || cell.Column != _activeCellCol) continue;
                    targets.Add(cell);
                    break;
                }
            }

            if (targets.Count == 0) return;

            // Заливка может задеть ячейки нескольких таблиц сразу — выделение это
            // допускает. Пока таблица одна, снимок берётся только с неё; если их
            // больше, дешевле и надёжнее один снимок документа, чем несколько
            // независимых шагов, которые пришлось бы откатывать по одному.
            var touchedTables = new HashSet<TableBlock>();
            if (_tableSelections.Count > 0)
                foreach (var kv in _tableSelections) touchedTables.Add(kv.Key);
            else if (_activeTableBlock is not null)
                touchedTables.Add(_activeTableBlock);

            bool singleTable = touchedTables.Count == 1;
            if (singleTable) BeginTableEdit(_activeTableBlock ?? System.Linq.Enumerable.First(touchedTables), "Set cell background");
            else BeginEdit("Set cell background");

            foreach (var cell in targets) cell.BackgroundColor = value;

            if (singleTable) CommitTableEdit();
            else CommitEdit();

            InvalidateCellLayoutCaches();
            RebuildLayouts();
            InvalidateFull();
        }

        // Ячейки, к которым применяется оформление: выделенный диапазон, а если
        // диапазона нет — одна ячейка под кареткой. Логика общая для выравнивания
        // по вертикали и по горизонтали.
        private List<TableCell> CollectTargetCells(out HashSet<TableBlock> tables)
        {
            var targets = new List<TableCell>();
            tables = new HashSet<TableBlock>();

            if (_tableSelections.Count > 0)
            {
                foreach (var kv in _tableSelections)
                {
                    int minRow = Math.Min(kv.Value.sr, kv.Value.er);
                    int maxRow = Math.Max(kv.Value.sr, kv.Value.er);
                    int minCol = Math.Min(kv.Value.sc, kv.Value.ec);
                    int maxCol = Math.Max(kv.Value.sc, kv.Value.ec);
                    foreach (var cell in kv.Key.Cells)
                    {
                        if (cell.Row < minRow || cell.Row > maxRow) continue;
                        if (cell.Column < minCol || cell.Column > maxCol) continue;
                        targets.Add(cell);
                        tables.Add(kv.Key);
                    }
                }
                return targets;
            }

            if (_activeTableBlock is not null)
            {
                foreach (var cell in _activeTableBlock.Cells)
                {
                    if (cell.Row != _activeCellRow || cell.Column != _activeCellCol) continue;
                    targets.Add(cell);
                    tables.Add(_activeTableBlock);
                    break;
                }
            }

            return targets;
        }

        // Абзацы всех выделенных ячеек. Пустой список означает, что выделения ячеек
        // нет и форматирование должно идти обычным путём — по абзацу под кареткой.
        // Условие именно по _tableSelections: CollectTargetCells при отсутствии
        // выделения подставляет ячейку под кареткой, и по нему отличить одно от
        // другого нельзя.
        private IReadOnlyList<ParagraphBlock> QuerySelectedCellParagraphs()
        {
            var result = new List<ParagraphBlock>();
            if (_tableSelections.Count > 0)
            {
                foreach (var cell in CollectTargetCells(out _))
                    result.AddRange(cell.Paragraphs);

                return result;
            }

            // Обычное текстовое выделение, захватившее несколько абзацев ячеек.
            // В _tableSelections оно не попадает — там лежит только выделение ячеек
            // целиком, — а SelectionParagraphs собирает лишь абзацы верхнего уровня
            // (они проверяются на принадлежность DocVm.Paragraphs, куда абзацы ячеек
            // не входят). Из-за этого форматирование и отступы доставались одному
            // абзацу с кареткой: выделив несколько пунктов списка в ячейке,
            // пользователь двигал стрелкой линейки только текущий.
            if (!HasSel()) return result;

            var (sp, _, ep, _) = NormalizeSelection();
            var seen = new HashSet<ParagraphBlock>();
            for (int i = sp; i <= ep && i < _layouts.Count; i++)
            {
                var block = _layouts[i].Cell?.ParaBlock;
                if (block is null) continue;
                if (seen.Add(block)) result.Add(block);
            }

            // Один абзац отдаём пустым списком: вызывающий уйдёт по прежней ветке
            // с TableActiveCellParagraph, и поведение одиночного выделения не меняется.
            if (result.Count < 2) result.Clear();
            return result;
        }

        // Высота строки под кареткой, из поля ленты. Как и перетаскивание, задаёт
        // минимальную высоту: содержимое выше — строка растёт дальше.
        private void ExecuteTableSetRowHeight(double heightPt)
        {
            if (_activeTableBlock is null) return;
            BeginTableEdit(_activeTableBlock, "Set row height");
            _activeTableBlock.SetRowMinHeightPt(_activeCellRow, Math.Max(14.0, heightPt));
            CommitTableEdit();
            InvalidateCellLayoutCaches();
            RebuildLayouts();
            InvalidateFull();
        }

        // Ширина столбца под кареткой, из поля ленты. Ширина задаётся фиксированной:
        // явное число из поля — это именно требование, а не подсказка раскладке.
        private void ExecuteTableSetColumnWidth(double widthMm)
        {
            if (_activeTableBlock is null) return;
            if (_activeCellCol < 0 || _activeCellCol >= _activeTableBlock.Columns.Count) return;
            BeginTableEdit(_activeTableBlock, "Set column width");
            _activeTableBlock.Columns[_activeCellCol].WidthType = TableColumnWidthType.Fixed;
            _activeTableBlock.Columns[_activeCellCol].WidthValue = Math.Max(5.0, widthMm);
            CommitTableEdit();
            InvalidateCellLayoutCaches();
            RebuildLayouts();
            InvalidateFull();
        }

        // Вертикальное выравнивание содержимого ячейки: 0 — верх, 1 — середина,
        // 2 — низ. Значение приходит числом от ленты, здесь приводится к типу модели.
        private void ExecuteTableSetCellVAlign(int vAlign)
        {
            var targets = CollectTargetCells(out var tables);
            if (targets.Count == 0) return;

            // Тип указан полностью: у самого полотна есть свойство VerticalAlignment
            // от контрола Avalonia, и короткое имя разрешается в него, а не в
            // перечисление модели.
            var value = vAlign switch
            {
                1 => Models.Document.VerticalAlignment.Middle,
                2 => Models.Document.VerticalAlignment.Bottom,
                _ => Models.Document.VerticalAlignment.Top
            };

            bool singleTable = tables.Count == 1;
            if (singleTable) BeginTableEdit(System.Linq.Enumerable.First(tables), "Cell vertical align");
            else BeginEdit("Cell vertical align");

            foreach (var cell in targets) cell.VerticalAlignment = value;

            if (singleTable) CommitTableEdit();
            else CommitEdit();

            InvalidateCellLayoutCaches();
            RebuildLayouts();
            InvalidateFull();
        }

        // Горизонтальное выравнивание содержимого ячейки. Своего свойства у ячейки
        // нет: текст в ней — обычные абзацы, поэтому выравнивание проставляется
        // каждому абзацу ячейки, как это делает лента для основного потока.
        private void ExecuteTableSetCellHAlign(Models.Styles.TextAlignment align)
        {
            var targets = CollectTargetCells(out var tables);
            if (targets.Count == 0) return;

            bool singleTable = tables.Count == 1;
            if (singleTable) BeginTableEdit(System.Linq.Enumerable.First(tables), "Cell horizontal align");
            else BeginEdit("Cell horizontal align");

            foreach (var cell in targets)
                foreach (var para in cell.Paragraphs)
                    para.Properties.Alignment = align;

            if (singleTable) CommitTableEdit();
            else CommitEdit();

            InvalidateCellLayoutCaches();
            RebuildLayouts();
            InvalidateFull();
        }

        // Внутренние поля ячейки. Меняют высоту строки и ширину текстовой зоны,
        // поэтому после правки нужен и сброс кеша раскладки ячеек, и пересбор.
        private void ExecuteTableSetCellPadding(
            double topPt, double bottomPt, double leftPt, double rightPt)
        {
            var targets = CollectTargetCells(out var tables);
            if (targets.Count == 0) return;

            bool singleTable = tables.Count == 1;
            if (singleTable) BeginTableEdit(System.Linq.Enumerable.First(tables), "Cell padding");
            else BeginEdit("Cell padding");

            foreach (var cell in targets)
            {
                cell.PaddingTopPt = Math.Max(0, topPt);
                cell.PaddingBottomPt = Math.Max(0, bottomPt);
                cell.PaddingLeftPt = Math.Max(0, leftPt);
                cell.PaddingRightPt = Math.Max(0, rightPt);
            }

            if (singleTable) CommitTableEdit();
            else CommitEdit();

            InvalidateCellLayoutCaches();
            RebuildLayouts();
            InvalidateFull();
        }

        // Поля целевых ячеек. Разные значения внутри выделения дают null: показывать
        // в полях ленты одно из них означало бы соврать про остальные.
        private (double TopPt, double BottomPt, double LeftPt, double RightPt)? QueryTableCellPadding()
        {
            var targets = CollectTargetCells(out _);
            if (targets.Count == 0) return null;

            var first = targets[0];
            for (int i = 1; i < targets.Count; i++)
            {
                var c = targets[i];
                if (c.PaddingTopPt != first.PaddingTopPt) return null;
                if (c.PaddingBottomPt != first.PaddingBottomPt) return null;
                if (c.PaddingLeftPt != first.PaddingLeftPt) return null;
                if (c.PaddingRightPt != first.PaddingRightPt) return null;
            }

            return (first.PaddingTopPt, first.PaddingBottomPt,
                    first.PaddingLeftPt, first.PaddingRightPt);
        }

        // Активный инструмент границ: 0 — нет, 1 — карандаш, 2 — ластик.
        // Режим, а не разовое действие: держится между нажатиями, пока его не
        // выключат кнопкой или Escape.
        private int _lineTool;

        private void ExecuteTableSetLineTool(int tool)
        {
            int normalized = tool is 1 or 2 ? tool : 0;
            if (_lineTool == normalized) return;
            _lineTool = normalized;

            // Курсор ставится сразу, не дожидаясь движения мыши: иначе после
            // нажатия кнопки в ленте вид указателя менялся бы только над текстом.
            Cursor = _lineTool == 0
                ? new Cursor(StandardCursorType.Ibeam)
                : new Cursor(StandardCursorType.Cross);

            InvalidateFull();
        }

        private int QueryTableLineTool() => _lineTool;

        // Объединение выделенных ячеек. Объединённая ячейка хранится как обычная,
        // но с RowSpan/ColSpan больше единицы, а накрытых ею записей в Cells нет
        // вовсе — GetCell разрешает любую точку прямоугольника в её владельца.
        private void ExecuteTableMergeCells()
        {
            if (_activeTableBlock is not { } table) return;
            if (!_tableSelections.TryGetValue(table, out var sel)) return;

            int r1 = Math.Min(sel.sr, sel.er), r2 = Math.Max(sel.sr, sel.er);
            int c1 = Math.Min(sel.sc, sel.ec), c2 = Math.Max(sel.sc, sel.ec);
            if (r1 == r2 && c1 == c2) return;

            // Прямоугольник расширяется до целых ячеек: в выделение мог попасть
            // кусок уже объединённой ячейки, а разрезанной оставить её нельзя.
            // Повторяем, пока границы не перестанут расти — расширение по одной
            // ячейке может втянуть в прямоугольник следующую.
            bool grown;
            do
            {
                grown = false;
                for (int r = r1; r <= r2; r++)
                {
                    for (int c = c1; c <= c2; c++)
                    {
                        var probe = table.GetCell(r, c);
                        if (probe is null) continue;
                        if (probe.Row < r1) { r1 = probe.Row; grown = true; }
                        if (probe.Column < c1) { c1 = probe.Column; grown = true; }
                        int pr2 = probe.Row + probe.RowSpan - 1;
                        int pc2 = probe.Column + probe.ColSpan - 1;
                        if (pr2 > r2) { r2 = pr2; grown = true; }
                        if (pc2 > c2) { c2 = pc2; grown = true; }
                    }
                }
            } while (grown);

            var target = table.GetCell(r1, c1);
            if (target is null) return;

            BeginTableEdit(table, "Merge cells");

            var absorbed = new List<Models.Document.TableCell>();
            foreach (var cell in table.Cells)
            {
                if (ReferenceEquals(cell, target)) continue;
                if (cell.Row < r1 || cell.Row > r2) continue;
                if (cell.Column < c1 || cell.Column > c2) continue;
                absorbed.Add(cell);
            }

            // Текст поглощённых ячеек переезжает в целевую. Пустые абзацы
            // отбрасываются: иначе объединение пустых ячеек оставляло бы в итоговой
            // столько пустых строк, сколько было ячеек.
            foreach (var cell in absorbed)
                foreach (var para in cell.Paragraphs)
                    if (!string.IsNullOrEmpty(para.GetPlainText()))
                        target.Paragraphs.Add(para);

            foreach (var cell in absorbed)
                table.Cells.Remove(cell);

            target.RowSpan = r2 - r1 + 1;
            target.ColSpan = c2 - c1 + 1;

            CommitTableEdit();

            _tableSelections.Remove(table);
            _activeCellRow = r1;
            _activeCellCol = c1;

            RestoreCaretAfterTableStructure();
            NotifyCaretEnteredTableCallback();
        }

        // Разбиение объединённой ячейки обратно на одиночные. Содержимое остаётся
        // в исходной ячейке — восстанавливать его по бывшим ячейкам не из чего,
        // при объединении текст был в неё перенесён. Так же ведёт себя Word.
        private void ExecuteTableSplitCell()
        {
            if (_activeTableBlock is not { } table) return;

            var cell = table.GetCell(_activeCellRow, _activeCellCol);
            if (cell is null) return;
            if (cell.RowSpan <= 1 && cell.ColSpan <= 1) return;

            BeginTableEdit(table, "Split cell");

            int r1 = cell.Row, c1 = cell.Column;
            int r2 = r1 + cell.RowSpan - 1, c2 = c1 + cell.ColSpan - 1;

            for (int r = r1; r <= r2; r++)
            {
                for (int c = c1; c <= c2; c++)
                {
                    if (r == r1 && c == c1) continue;
                    table.Cells.Add(CreateCellLike(cell, r, c));
                }
            }

            cell.RowSpan = 1;
            cell.ColSpan = 1;

            CommitTableEdit();

            RestoreCaretAfterTableStructure();
            NotifyCaretEnteredTableCallback();
        }

        // Деление обычной ячейки пополам: vertical = true — вертикальной чертой
        // (получаются два столбца), false — горизонтальной (две строки).
        //
        // От ExecuteTableSplitCell отличается предметом: тот лишь снимает ранее
        // сделанное объединение и обратно ячейку не делит.
        //
        // Сценария два. Если ячейка уже растянута на несколько столбцов (строк),
        // новая граница проходит внутри её собственного диапазона и сетка таблицы
        // не меняется вовсе. Если ячейка занимает ровно одну клетку, в таблицу
        // добавляется столбец (строка) — делить иначе нечего. Прочие ячейки при
        // этом сохраняют прежний вид: накрывающие место вставки расширяются на
        // единицу, стоящие дальше сдвигаются.
        private void ExecuteTableDivideCell(bool vertical)
        {
            if (_activeTableBlock is not { } table) return;

            var cell = table.GetCell(_activeCellRow, _activeCellCol);
            if (cell is null) return;

            BeginTableEdit(table, vertical ? "Divide cell vertically" : "Divide cell horizontally");

            if (vertical)
            {
                if (cell.ColSpan >= 2)
                {
                    int leftSpan = cell.ColSpan / 2;
                    var right = CreateCellLike(cell, cell.Row, cell.Column + leftSpan);
                    right.RowSpan = cell.RowSpan;
                    right.ColSpan = cell.ColSpan - leftSpan;
                    table.Cells.Add(right);
                    cell.ColSpan = leftSpan;
                }
                else
                {
                    int insertCol = cell.Column + 1;

                    foreach (var other in table.Cells)
                    {
                        if (ReferenceEquals(other, cell)) continue;

                        if (other.Column >= insertCol) other.Column++;
                        else if (other.Column + other.ColSpan > insertCol) other.ColSpan++;
                    }

                    // Ширина исходного столбца делится пополам, иначе таблица уехала бы
                    // вправо на целый столбец. Автоширину делить нечего — пересчитается.
                    var inserted = new Models.Document.TableColumnDefinition
                    {
                        WidthType = Models.Document.TableColumnWidthType.Auto
                    };
                    if (cell.Column < table.Columns.Count)
                    {
                        var source = table.Columns[cell.Column];
                        if (source.WidthType == Models.Document.TableColumnWidthType.Fixed)
                        {
                            double half = source.WidthValue / 2.0;
                            source.WidthValue = half;
                            inserted.WidthType = Models.Document.TableColumnWidthType.Fixed;
                            inserted.WidthValue = half;
                        }
                    }
                    table.Columns.Insert(insertCol, inserted);
                    table.ColumnCount++;

                    var added = CreateCellLike(cell, cell.Row, insertCol);
                    added.RowSpan = cell.RowSpan;
                    added.ColSpan = 1;
                    table.Cells.Add(added);
                }
            }
            else
            {
                if (cell.RowSpan >= 2)
                {
                    int topSpan = cell.RowSpan / 2;
                    var bottom = CreateCellLike(cell, cell.Row + topSpan, cell.Column);
                    bottom.RowSpan = cell.RowSpan - topSpan;
                    bottom.ColSpan = cell.ColSpan;
                    table.Cells.Add(bottom);
                    cell.RowSpan = topSpan;
                }
                else
                {
                    int insertRow = cell.Row + 1;

                    foreach (var other in table.Cells)
                    {
                        if (ReferenceEquals(other, cell)) continue;

                        if (other.Row >= insertRow) other.Row++;
                        else if (other.Row + other.RowSpan > insertRow) other.RowSpan++;
                    }

                    table.InsertRowMinHeight(insertRow);
                    table.RowCount++;

                    var added = CreateCellLike(cell, insertRow, cell.Column);
                    added.RowSpan = 1;
                    added.ColSpan = cell.ColSpan;
                    table.Cells.Add(added);
                }
            }

            CommitTableEdit();

            RestoreCaretAfterTableStructure();
            NotifyCaretEnteredTableCallback();
        }

        // Новая пустая ячейка с оформлением образца: рамки, заливка, отступы и
        // выравнивание. Без этого разбитая ячейка теряла бы вид соседей.
        private static Models.Document.TableCell CreateCellLike(
            Models.Document.TableCell source, int row, int column)
            => new()
            {
                Row = row,
                Column = column,
                BackgroundColor = source.BackgroundColor,
                Borders = source.Borders.Clone(),
                VerticalAlignment = source.VerticalAlignment,
                PaddingTopPt = source.PaddingTopPt,
                PaddingBottomPt = source.PaddingBottomPt,
                PaddingLeftPt = source.PaddingLeftPt,
                PaddingRightPt = source.PaddingRightPt
            };

        // Обе координаты выравнивания за одну операцию: кнопка сетки задаёт
        // вертикаль и горизонталь разом, и в стек отмены должен уйти один снимок.
        // Раздельные вызовы клали два, и одно нажатие отменялось двумя Ctrl+Z.
        private void ExecuteTableSetCellAlign(int vAlign, Models.Styles.TextAlignment hAlign)
        {
            var targets = CollectTargetCells(out var tables);
            if (targets.Count == 0) return;

            var vValue = vAlign switch
            {
                1 => Models.Document.VerticalAlignment.Middle,
                2 => Models.Document.VerticalAlignment.Bottom,
                _ => Models.Document.VerticalAlignment.Top
            };

            bool singleTable = tables.Count == 1;
            if (singleTable) BeginTableEdit(System.Linq.Enumerable.First(tables), "Cell align");
            else BeginEdit("Cell align");

            foreach (var cell in targets)
            {
                cell.VerticalAlignment = vValue;
                foreach (var para in cell.Paragraphs)
                    para.Properties.Alignment = hAlign;
            }

            if (singleTable) CommitTableEdit();
            else CommitEdit();

            InvalidateCellLayoutCaches();
            RebuildLayouts();
            InvalidateFull();
        }

        // Текущее выравнивание целевых ячеек. Целевые собираются тем же
        // CollectTargetCells, что и у сеттеров, — иначе подсветка кнопки могла бы
        // расходиться с тем, на что эта кнопка подействует.
        // Разные значения внутри выделения дают null: активной кнопки нет.
        private int? QueryTableCellVAlign()
        {
            var targets = CollectTargetCells(out _);
            if (targets.Count == 0) return null;

            var first = targets[0].VerticalAlignment;
            for (int i = 1; i < targets.Count; i++)
                if (targets[i].VerticalAlignment != first) return null;

            return first switch
            {
                Models.Document.VerticalAlignment.Middle => 1,
                Models.Document.VerticalAlignment.Bottom => 2,
                _ => 0
            };
        }

        // Горизонтальное выравнивание ячейки хранится в её абзацах, поэтому
        // проверяются все абзацы всех целевых ячеек. Ячейка без абзацев
        // пропускается: своего значения у неё нет.
        private Models.Styles.TextAlignment? QueryTableCellHAlign()
        {
            var targets = CollectTargetCells(out _);
            if (targets.Count == 0) return null;

            Models.Styles.TextAlignment? common = null;
            bool any = false;

            foreach (var cell in targets)
            {
                foreach (var para in cell.Paragraphs)
                {
                    var align = ResolveParagraphAlignment(para);
                    if (!any) { common = align; any = true; }
                    else if (common != align) return null;
                }
            }

            return any ? common : null;
        }

        /// <summary>
        /// Действующее выравнивание абзаца. Свойство абзаца допускает null — это
        /// означает «унаследовать», а не «слева», поэтому у только что созданной
        /// ячейки читать его напрямую нельзя: получалось бы «значение не определено»
        /// и ни одна кнопка не подсвечивалась. Наследование разрешается той же
        /// цепочкой стилей, по которой считает отрисовка, с тем же итоговым Left.
        /// </summary>
        private Models.Styles.TextAlignment ResolveParagraphAlignment(
            Models.Document.ParagraphBlock para)
        {
            if (para.Properties.Alignment is { } explicitAlign) return explicitAlign;

            var resolved = _styleResolver?.ResolveAlignment(para.Properties.StyleName);
            return resolved is null
                ? Models.Styles.TextAlignment.Left
                : (Models.Styles.TextAlignment)(int)resolved.Value;
        }

        private void ExecuteTableDeleteRow()
        {
            if (_activeTableBlock is null) return;
            BeginEdit("Delete row");
            int deleteRow = _activeCellRow;
            _activeTableBlock.Cells.RemoveAll(c => c.Row == deleteRow);
            foreach (var cell in _activeTableBlock.Cells)
                if (cell.Row > deleteRow) cell.Row--;
            _activeTableBlock.RemoveRowMinHeight(deleteRow);
            _activeTableBlock.RowCount--;
            CommitEdit();
            if (_activeTableBlock.RowCount <= 0) { ExecuteTableDelete(); return; }
            _activeCellRow = Clamp(_activeCellRow, 0, _activeTableBlock.RowCount - 1);
            RestoreCaretAfterTableStructure();
        }

        private void ExecuteTableAddColumn(bool left)
        {
            if (_activeTableBlock is null) return;
            BeginTableEdit(_activeTableBlock, "Add column");
            int insertCol = left ? _activeCellCol : _activeCellCol + 1;
            foreach (var cell in _activeTableBlock.Cells)
                if (cell.Column >= insertCol) cell.Column++;
            for (int r = 0; r < _activeTableBlock.RowCount; r++)
                _activeTableBlock.Cells.Add(new TableCell { Row = r, Column = insertCol });
            var colDef = new TableColumnDefinition { WidthType = TableColumnWidthType.Auto };
            if (insertCol < _activeTableBlock.Columns.Count)
                _activeTableBlock.Columns.Insert(insertCol, colDef);
            else
                _activeTableBlock.Columns.Add(colDef);
            _activeTableBlock.ColumnCount++;
            if (left) _activeCellCol++;
            CommitTableEdit();
            RestoreCaretAfterTableStructure();
        }

        private void ExecuteTableDeleteColumn()
        {
            if (_activeTableBlock is null) return;
            BeginEdit("Delete column");
            int deleteCol = _activeCellCol;
            _activeTableBlock.Cells.RemoveAll(c => c.Column == deleteCol);
            foreach (var cell in _activeTableBlock.Cells)
                if (cell.Column > deleteCol) cell.Column--;
            if (deleteCol < _activeTableBlock.Columns.Count)
                _activeTableBlock.Columns.RemoveAt(deleteCol);
            _activeTableBlock.ColumnCount--;
            CommitEdit();
            if (_activeTableBlock.ColumnCount <= 0) { ExecuteTableDelete(); return; }
            _activeCellCol = Clamp(_activeCellCol, 0, _activeTableBlock.ColumnCount - 1);
            RestoreCaretAfterTableStructure();
        }

        private void ExecuteTableDelete()
        {
            if (_activeTableBlock is null || DocVm is null) return;
            BeginEdit("Delete table");
            DocVm.Document.Sections[0].Blocks.Remove(_activeTableBlock);
            CommitEdit();
            _cellVmCache.Clear();
            InvalidateCellLayoutCaches();
            DocVm.RebuildParagraphViewModelsPublic();
            NotifyLeftCell();
            _caretPara = Clamp(_caretPara, 0, Math.Max(0, _layouts.Count - 1));
            _caretChar = 0;
            RebuildLayouts();
            InvalidateFull();

            if (!IsFocused) Focus();
        }

        // ── Вертикальная навигация ────────────────────────────────────────
        // Абсолютная геометрия каретки (в pt), согласованная с отрисовкой DrawCaret:
        // absXPt — абсолютный X каретки (с учётом отступа абзаца и выравнивания),
        // lineTopPt — абсолютный Y верха строки каретки, lineHeightPt — высота строки.
        private bool TryGetCaretGeometry(out float absXPt, out float lineTopPt, out float lineHeightPt)
        {
            absXPt = 0f; lineTopPt = 0f; lineHeightPt = 0f;
            if (_caretPara < 0 || _caretPara >= _layouts.Count) return false;
            var pl = _layouts[_caretPara];
            var layout = GetRenderLayout(pl, (float)(_canvasWidth * PxToPt));
            if (layout is null || layout.Lines.Count == 0) return false;

            int pos = Clamp(_caretChar, 0, pl.Vm.PlainText?.Length ?? 0);
            var caret = layout.HitTestPosition(pos);
            int drawLineIdx = layout.GetLineIndexForChar(pos);
            float yBase = pl.LineFrom < layout.Lines.Count ? layout.Lines[pl.LineFrom].Y : 0f;
            float firstLineBaked = (drawLineIdx == 0) ? layout.FirstLineIndentPt : 0f;
            float caretAlignOffset = LineAlignShift(layout, drawLineIdx) - firstLineBaked
                + JustifyShiftBeforeChar(layout, drawLineIdx, pos);

            absXPt = pl.AbsXPt + caret.X + caretAlignOffset;
            lineTopPt = pl.Ypt + (caret.Y - yBase);
            lineHeightPt = caret.Height > 0.01f ? caret.Height : FallbackLinePt;
            return true;
        }

        // Перемещение каретки вверх/вниз — чисто геометрическое, тем же HitTest, что и клик.
        // Берём абсолютную точку каретки, держим целевой X (_preferredCaretXPt, абсолютный) и
        // шагаем по Y за границу текущей строки в направлении dir, пока HitTest не попадёт на
        // другую позицию. Это автоматически работает с разными абзацными отступами, разной
        // высотой строк, межабзацными интервалами и таблицами (вход в нужную колонку, переход
        // между строками таблицы, выход из неё) — без спец-логики «в ячейке/не в ячейке».
        private void MoveCaretVertically(int dir)
        {
            if (_layouts.Count == 0) return;

            if (!TryGetCaretGeometry(out float curXPt, out float lineTopPt, out float lineHeightPt))
            {
                // Нет геометрии (пустой/неинициализированный layout) — индексный фолбэк.
                _caretPara = Clamp(_caretPara + dir, 0, _layouts.Count - 1);
                _caretChar = Clamp(_caretChar, 0, GetVmAt(_caretPara)?.PlainText?.Length ?? 0);
                return;
            }

            // Начало серии вертикальных перемещений — фиксируем столбец из ТЕКУЩЕЙ позиции каретки
            // (живая геометрия, без устаревшего значения). Дальше при Up/Down подряд он держится.
            if (!_vNavActive)
            {
                _preferredCaretXPt = curXPt;
                _vNavActive = true;
            }

            float curCenterPt = lineTopPt + lineHeightPt * 0.5f;
            float lineBottomPt = lineTopPt + lineHeightPt;
            float width = (float)(_canvasWidth * PxToPt);

            float bestCenterPt = float.NaN;

            // Шаг 1: соседняя визуальная строка ВНУТРИ текущего абзаца (многострочный абзац).
            {
                var pl = _layouts[_caretPara];
                var lay = GetRenderLayout(pl, width);
                if (lay is not null && lay.Lines.Count > 0)
                {
                    int lf = Clamp(pl.LineFrom, 0, lay.Lines.Count - 1);
                    float yBase = lay.Lines[lf].Y;
                    int lineTo = Math.Min(pl.LineTo, lay.Lines.Count);
                    float bestDelta = float.MaxValue;

                    for (int li = pl.LineFrom; li < lineTo; li++)
                    {
                        var line = lay.Lines[li];
                        float c = pl.Ypt + (line.Y - yBase) + line.Height * 0.5f;
                        float d = c - curCenterPt;
                        if (dir > 0 && d > 0.5f && d < bestDelta) { bestDelta = d; bestCenterPt = c; }
                        else if (dir < 0 && d < -0.5f && -d < bestDelta) { bestDelta = -d; bestCenterPt = c; }
                    }
                }
            }

            // Шаг 2: соседней строки в текущем абзаце нет — ищем ближайшую по вертикали ДРУГУЮ
            // полосу среди ВСЕХ layout (следующий абзац / ячейка соседней строки таблицы / абзац
            // за таблицей). Сканируем по Ypt/HeightPt без построения раскладки (дёшево), берём
            // ближайший в направлении dir. Окном соседних индексов это не находилось: в таблице
            // ячейка строки ниже идёт в порядке чтения далеко. Конкретную колонку под столбцом
            // определит сам HitTest по _preferredCaretXPt.
            if (float.IsNaN(bestCenterPt))
            {
                int targetIdx = -1;
                float bestEdge = dir > 0 ? float.MaxValue : float.MinValue;

                for (int i = 0; i < _layouts.Count; i++)
                {
                    if (i == _caretPara) continue;
                    var p = _layouts[i];
                    if (dir > 0)
                    {
                        if (p.Ypt >= lineBottomPt - 0.5f && p.Ypt < bestEdge)
                        { bestEdge = p.Ypt; targetIdx = i; }
                    }
                    else
                    {
                        float pBot = p.Ypt + p.HeightPt;
                        if (pBot <= lineTopPt + 0.5f && pBot > bestEdge)
                        { bestEdge = pBot; targetIdx = i; }
                    }
                }

                if (targetIdx >= 0)
                {
                    var tp = _layouts[targetIdx];
                    var tlay = GetRenderLayout(tp, width);
                    if (tlay is not null && tlay.Lines.Count > 0)
                    {
                        int lf = Clamp(tp.LineFrom, 0, tlay.Lines.Count - 1);
                        float yBase = tlay.Lines[lf].Y;
                        int lineTo = Math.Min(tp.LineTo, tlay.Lines.Count);
                        int li = dir > 0 ? lf : Math.Max(lf, lineTo - 1);
                        var line = tlay.Lines[li];
                        bestCenterPt = tp.Ypt + (line.Y - yBase) + line.Height * 0.5f;
                    }
                    else
                    {
                        bestCenterPt = tp.Ypt + tp.HeightPt * 0.5f;
                    }
                }
            }

            if (float.IsNaN(bestCenterPt)) return; // край документа — некуда

            // Бьём тем же HitTest, что и клик, по сохранённому абсолютному X и центру найденной
            // строки — каретка встаёт в тот же экранный столбец на соседней строке/в ячейке.
            float pxPerPt = (float)(PtToPx * Zoom);
            var (pi, ci) = HitTest(new Avalonia.Point(_preferredCaretXPt * pxPerPt, bestCenterPt * pxPerPt));
            if (pi >= 0 && pi < _layouts.Count)
            {
                _caretPara = pi;
                _caretChar = ci;
            }
        }

        private void ClampCaret()
        {
            _caretPara = Clamp(_caretPara, 0, Math.Max(0, _layouts.Count - 1));
            _caretChar = Clamp(_caretChar, 0, GetVmAt(_caretPara)?.PlainText?.Length ?? 0);
        }

        private void UpdatePreferredX()
        {
            // Храним АБСОЛЮТНЫЙ X каретки (с учётом отступа абзаца и выравнивания), а не локальный.
            // Иначе при разных абзацных отступах один и тот же локальный X = разный экранный, и
            // вертикальная навигация съезжала. Геометрия — та же, что у отрисовки каретки.
            if (TryGetCaretGeometry(out float absXPt, out _, out _))
                _preferredCaretXPt = absXPt;

            // Это горизонтальное перемещение/клик/правка — следующая серия Up/Down заново возьмёт
            // столбец из текущей позиции каретки.
            _vNavActive = false;
        }

        private void SnapCaretToCorrectSlice()
        {
            if (_layouts.Count == 0) return;
            _caretPara = Clamp(_caretPara, 0, _layouts.Count - 1);

            var targetVm = GetVmAt(_caretPara);
            if (targetVm is null) return;

            var layout = GetLayoutAt(_caretPara);
            if (layout is null) return;

            int lineIdx = layout.GetLineIndexForChar(_caretChar);

            // Для ByCell-split ячейки в _layouts присутствуют два слайса с одинаковым VM,
            // LineFrom=0 и LineTo=lineCount — стандартное совпадение по диапазону строк
            // всегда выбирает первый (страница 1). Используем clip-Y как тай-брейкер:
            // правильным является тот слайс, в чьём clip-прямоугольнике находится каретка.
            bool InClip(int idx)
            {
                var pl = _layouts[idx];
                if (pl.Cell == null) return true; // обычный параграф — всегда подходит
                int pos = Clamp(_caretChar, 0, pl.Vm.PlainText?.Length ?? 0);
                var snapLayout = pl.Layout
                    ?? GetOrBuildLayout(pl.Vm, (float)(_canvasWidth * PxToPt));
                var caretRect = snapLayout.HitTestPosition(pos);
                // RenderParagraphLines рендерит строки со смещением (line.Y - lines[lineFrom].Y).
                // Нужно применять тот же yBase иначе caretAbsY указывает на неправильную
                // страницу и SnapCaretToCorrectSlice выбирает слайс первой страницы вместо второй.
                float yBase = pl.LineFrom < snapLayout.Lines.Count
                    ? snapLayout.Lines[pl.LineFrom].Y : 0f;
                float caretAbsY = pl.Ypt + (caretRect.Y - yBase);
                return caretAbsY >= pl.Cell.ClipY - 0.5f
                    && caretAbsY < pl.Cell.ClipY + pl.Cell.ClipH + 0.5f;
            }

            void CommitSlice(int idx)
            {
                _caretPara = idx;
                var snapped = _layouts[idx];
                int pos = Clamp(_caretChar, 0, snapped.Vm.PlainText?.Length ?? 0);
                var snappedLayout = snapped.Layout
                    ?? GetOrBuildLayout(snapped.Vm, (float)(_canvasWidth * PxToPt));
                int actualLine = snappedLayout.GetLineIndexForChar(pos);

                // GetLineIndexForChar для pos = line[N].LastCharIndex + 1 возвращает N+1,
                // потому что позиция формально принадлежит следующей строке.
                // Но визуально каретка должна стоять в КОНЦЕ строки N, а не в начале N+1.
                // Проверяем: если предыдущая строка заканчивается ровно на pos-1 и не является
                // последней — возвращаемся на неё. DrawCaret с таким hint рисует в конце строки N.
                if (actualLine > 0 && actualLine < snappedLayout.Lines.Count)
                {
                    var prevLine = snappedLayout.Lines[actualLine - 1];
                    // Применяем коррекцию только если _caretLineHint не указывает на actualLine.
                    // Левый клик: HitTest ставит hint=N и pos=FirstCharIndex_N = LastCharIndex_(N-1)+1.
                    // Без проверки hint коррекция переводила бы на конец строки N-1 вместо начала N.
                    if (!prevLine.IsLastLine && pos == prevLine.LastCharIndex + 1
                        && _caretLineHint != actualLine)
                        actualLine--;
                }

                _caretLineHint = (actualLine >= snapped.LineFrom && actualLine < snapped.LineTo)
                    ? actualLine
                    : (snapped.LineFrom < snapped.LineTo ? snapped.LineFrom : -1);
            }

            if (_caretLineHint >= 0)
            {
                int firstHintMatch = -1;
                for (int i = 0; i < _layouts.Count; i++)
                {
                    var pl = _layouts[i];
                    if (pl.Vm != targetVm) continue;
                    if (_caretLineHint < pl.LineFrom || _caretLineHint >= pl.LineTo) continue;

                    if (firstHintMatch < 0) firstHintMatch = i;
                    if (InClip(i)) { CommitSlice(i); return; }
                }
                if (firstHintMatch >= 0) { CommitSlice(firstHintMatch); return; }
            }

            // Проверяем текущий слайс — если он уже правильный и каретка в его clip, выходим.
            var currentPl = _layouts[_caretPara];
            if (currentPl.Vm == targetVm
                && lineIdx >= currentPl.LineFrom && lineIdx < currentPl.LineTo
                && InClip(_caretPara))
            {
                _caretLineHint = lineIdx;
                return;
            }

            // Полный перебор: ищем слайс с совпадением по диапазону строк и clip-Y.
            int firstLineMatch = -1;
            for (int i = 0; i < _layouts.Count; i++)
            {
                var pl = _layouts[i];
                if (pl.Vm != targetVm) continue;
                if (lineIdx < pl.LineFrom || lineIdx >= pl.LineTo) continue;

                if (firstLineMatch < 0) firstLineMatch = i;
                if (InClip(i)) { _caretPara = i; _caretLineHint = lineIdx; return; }
            }
            if (firstLineMatch >= 0) { _caretPara = firstLineMatch; _caretLineHint = lineIdx; }
        }

    }
}