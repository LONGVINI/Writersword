using System;
using System.Collections.Generic;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;

namespace Writersword.Modules.TextEditor.Commands
{
    /// <summary>
    /// Вставка блока скопированных ячеек в таблицу «сетка в сетку»: содержимое и фон каждой
    /// скопированной ячейки перезаписывают целевую ячейку начиная с якорной (row0, col0).
    /// По столбцам выход за границы обрезается, по строкам таблица дорастает снизу.
    /// Обратима без снапшота документа: хранит прежнее содержимое и фон перезаписанных ячеек
    /// и число добавленных строк, восстанавливает их при Revert.
    /// </summary>
    public sealed class PasteCellsCommand : ITextCommand
    {
        private readonly TableBlock _table;
        private readonly int _row0;
        private readonly int _col0;

        // Скопированные ячейки: относительные координаты (0-based), параграфы и фон.
        private readonly List<(int r, int c, List<ParagraphBlock> paras, string? bg)> _source;

        // Целевые записи (абсолютные координаты в таблице) — строятся один раз.
        private List<(int row, int col, List<ParagraphBlock> paras, string? bg)>? _targets;

        // Прежнее содержимое и фон перезаписанных ячеек — для отката. Захватывается при первом Apply.
        private List<(int row, int col, List<ParagraphBlock> oldParas, string? oldBg)>? _saved;

        // Сколько строк дорастили снизу, чтобы вместить вставку.
        private int _addedRows;

        // Вложенные таблицы скопированных ячеек: по списку абзацев ячейки-источника —
        // таблицы и номера абзацев, перед которыми они стоят. Null — вложенных нет.
        private readonly IReadOnlyDictionary<List<ParagraphBlock>, List<(TableBlock Table, int Position)>>? _sourceNested;

        // Вложенные таблицы перезаписанных ячеек до вставки — для отката. Таблицы
        // хранятся ссылками, место — номером абзаца: абзацы откат возвращает копиями.
        private List<(int row, int col, List<(NestedTable Nested, int Position)>? nested)>? _savedNested;

        public string Description => "Paste cells";

        /// <summary>
        /// Вызывается после Apply (Redo) и Revert (Undo). Канвас пересобирает раскладку
        /// таблицы и ставит каретку. Устанавливается DocumentCanvas, чтобы не тянуть UI в команду.
        /// </summary>
        public Action? AfterChange { get; set; }

        public PasteCellsCommand(TableBlock table, int row0, int col0,
            List<(int r, int c, List<ParagraphBlock> paras, string? bg)> source,
            IReadOnlyDictionary<List<ParagraphBlock>, List<(TableBlock Table, int Position)>>? sourceNested = null)
        {
            _table = table;
            _row0 = row0;
            _col0 = col0;
            _source = source;
            _sourceNested = sourceNested;
        }

        public void Apply(DocumentModel doc)
        {
            BuildTargets();
            GrowRows();

            // Захватываем прежнее состояние только на первом Apply — для корректного отката.
            if (_saved is null)
            {
                _saved = new List<(int, int, List<ParagraphBlock>, string?)>();
                _savedNested = new List<(int, int, List<(NestedTable, int)>?)>();
                foreach (var (row, col, _, _) in _targets!)
                {
                    var cell = _table.GetCell(row, col);
                    if (cell is null) continue;
                    _saved.Add((row, col, CloneParas(cell.Paragraphs), cell.BackgroundColor));
                    _savedNested.Add((row, col, CaptureNested(cell)));
                }
            }

            foreach (var (row, col, paras, bg) in _targets!)
            {
                var cell = _table.GetCell(row, col);
                if (cell is null) continue;
                cell.Paragraphs = CloneParas(paras);
                cell.BackgroundColor = bg;

                // Содержимое ячейки заменяется целиком: её прежние вложенные таблицы
                // уходят, а таблицы скопированной ячейки встают копиями перед теми же
                // по счёту абзацами.
                cell.NestedTables = null;
                if (_sourceNested is not null && _sourceNested.TryGetValue(paras, out var nestedSource))
                {
                    foreach (var (nestedTable, position) in nestedSource)
                    {
                        if (Services.DocumentCloner.CloneBlock(nestedTable) is not TableBlock nestedCopy)
                            continue;

                        nestedCopy.RenewIds();
                        cell.InsertNestedTable(nestedCopy, position, first: false);
                    }
                }
            }

            AfterChange?.Invoke();
        }

        public void Revert(DocumentModel doc)
        {
            if (_saved is not null)
            {
                foreach (var (row, col, oldParas, oldBg) in _saved)
                {
                    var cell = _table.GetCell(row, col);
                    if (cell is null) continue;
                    cell.Paragraphs = CloneParas(oldParas);
                    cell.BackgroundColor = oldBg;
                }
            }

            // После абзацев: таблицы привязываются к абзацам, только что вставшим в ячейку.
            if (_savedNested is not null)
            {
                foreach (var (row, col, nested) in _savedNested)
                {
                    var cell = _table.GetCell(row, col);
                    if (cell is null) continue;
                    RestoreNested(cell, nested);
                }
            }

            // Убираем строки, добавленные при вставке.
            if (_addedRows > 0)
            {
                int keepRows = _table.RowCount - _addedRows;
                _table.Cells.RemoveAll(c => c.Row >= keepRows);
                _table.RowCount = keepRows;
            }

            AfterChange?.Invoke();
        }

        public bool TryMerge(ITextCommand next) => false;

        private void BuildTargets()
        {
            if (_targets is not null) return;

            _targets = new List<(int, int, List<ParagraphBlock>, string?)>();
            int maxRow = -1;
            foreach (var (r, c, paras, bg) in _source)
            {
                int col = _col0 + c;
                if (col < 0 || col >= _table.ColumnCount) continue; // столбцы обрезаем
                int row = _row0 + r;
                if (row < 0) continue;
                _targets.Add((row, col, paras, bg));
                if (row > maxRow) maxRow = row;
            }

            _addedRows = maxRow >= _table.RowCount ? (maxRow + 1 - _table.RowCount) : 0;
        }

        private void GrowRows()
        {
            for (int i = 0; i < _addedRows; i++)
            {
                int newRow = _table.RowCount;
                for (int c = 0; c < _table.ColumnCount; c++)
                    _table.Cells.Add(new TableCell { Row = newRow, Column = c });
                _table.RowCount++;
            }
        }

        private static List<(NestedTable Nested, int Position)>? CaptureNested(TableCell cell)
        {
            if (cell.NestedTables is not { Count: > 0 } nestedTables) return null;

            var anchors = new List<(NestedTable Nested, int Position)>(nestedTables.Count);
            foreach (var nested in nestedTables)
                anchors.Add((nested, cell.NestedTablePosition(nested)));
            return anchors;
        }

        private static void RestoreNested(TableCell cell, List<(NestedTable Nested, int Position)>? anchors)
        {
            if (anchors is not { Count: > 0 })
            {
                cell.NestedTables = null;
                return;
            }

            var nestedTables = new List<NestedTable>(anchors.Count);
            foreach (var (nested, savedPosition) in anchors)
            {
                int position = Math.Clamp(savedPosition, 0, cell.Paragraphs.Count);
                nested.BeforeParagraphIndex = position;
                nested.BeforeParagraphId = position < cell.Paragraphs.Count
                    ? cell.Paragraphs[position].Id
                    : Guid.Empty;
                nestedTables.Add(nested);
            }

            cell.NestedTables = nestedTables;
        }

        private static List<ParagraphBlock> CloneParas(List<ParagraphBlock> src)
        {
            var list = new List<ParagraphBlock>(src.Count);
            foreach (var p in src)
                list.Add(ClonePara(p));
            if (list.Count == 0)
                list.Add(new ParagraphBlock());
            return list;
        }

        private static ParagraphBlock ClonePara(ParagraphBlock src)
        {
            var dst = new ParagraphBlock
            {
                Properties = src.Properties.Clone(),
                ListProperties = src.ListProperties?.Clone()
            };
            dst.Chunks.Clear();
            foreach (var chunk in src.Chunks)
            {
                var c = new TextChunk();
                foreach (var run in chunk.Runs)
                    c.Runs.Add(run.Clone());
                dst.Chunks.Add(c);
            }
            if (dst.Chunks.Count == 0)
                dst.Chunks.Add(new TextChunk());
            return dst;
        }
    }
}
