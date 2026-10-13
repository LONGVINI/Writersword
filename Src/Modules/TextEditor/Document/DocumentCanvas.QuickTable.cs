using System;
using System.Collections.Generic;
using Writersword.Core.Models.Rendering;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Набор в ячейке таблицы без раскладки всего документа.
    ///
    /// Правка текста ячейки раньше сбрасывала раскладки всех таблиц документа и
    /// раскладывала листы целиком на каждую букву — на большой книге с таблицами это
    /// сотни миллисекунд на потоке интерфейса, и набор в ячейке провисал. Буква меняет
    /// одну таблицу: переложить её, и если её высота та же, пересобрать только записи
    /// абзацев её ячеек на месте. Листы целиком раскладываются после паузы в наборе
    /// (TypingRebuildDelay), как при наборе в обычном абзаце.
    ///
    /// Быстрый путь берётся только для таблицы, которая целиком стоит на одном листе
    /// одним куском, не плавающая и без вложенных таблиц, и только когда ни высота строк,
    /// ни число абзацев ячеек не изменились. Иначе — прежний полный путь.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        /// <summary>
        /// Перекладывает одну таблицу на месте. False — быстрый путь не подходит, нужен
        /// полный проход; кеш раскладки этой таблицы тогда уже обновлён.
        /// </summary>
        private bool TryQuickTableUpdate(TableBlock table)
        {
            if (DocVm is null || _styleResolver is null) return false;
            if (_cellFontPreview.Count > 0) return false;
            if (table.FloatPosition is not null) return false;
            if (!_tableLayoutCache.TryGetValue(table, out var cached)) return false;

            List<ParaLayout> layouts;
            List<TableEntry> tables;
            lock (_renderLock)
            {
                layouts = _layouts;
                tables = _tables;
            }

            // Таблица одним куском: одна запись, с первой строки до последней.
            int entryIndex = -1;
            for (int i = 0; i < tables.Count; i++)
            {
                if (ReferenceEquals(tables[i].Table, table))
                {
                    if (entryIndex >= 0) return false;
                    entryIndex = i;
                }
                else if (table.FindNestedOwner(tables[i].Table) is not null)
                {
                    // Вложенная таблица живёт отдельной записью — её место зависит от
                    // хозяйки, быстрый путь её не переставляет.
                    return false;
                }
            }
            if (entryIndex < 0) return false;

            var entry = tables[entryIndex];
            if (entry.RowFrom != 0 || entry.RowTo != -1 || entry.IsContinuation
                || entry.FirstRowContentOffsetPt != 0f || entry.RepeatedHeaderHeightPt != 0f)
                return false;

            // Записи абзацев её ячеек идут подряд.
            int first = -1;
            int last = -1;
            for (int i = 0; i < layouts.Count; i++)
            {
                if (layouts[i].Cell is not { } cell || cell.TableEntryIdx != entryIndex) continue;
                if (first < 0) first = i;
                else if (last != i - 1) return false;
                last = i;
            }
            if (first < 0) return false;

            var oldLayout = entry.Layout;
            _tableLayoutCache.Remove(table);
            var newLayout = GetOrBuildTableLayout(table, cached.Width);

            // Высота строк та же — ниже таблицы ничего не сдвигается.
            if (newLayout.Rows.Count != oldLayout.Rows.Count) return false;
            if (Math.Abs(newLayout.TotalHeightPt - oldLayout.TotalHeightPt) > 0.01f) return false;
            for (int r = 0; r < newLayout.Rows.Count; r++)
                if (Math.Abs(newLayout.Rows[r].HeightPt - oldLayout.Rows[r].HeightPt) > 0.01f) return false;

            var newTables = new List<TableEntry>(tables);
            newTables[entryIndex] = entry with { Layout = newLayout };

            var fresh = new List<ParaLayout>(last - first + 1);
            AddCellParasToLayouts(fresh, newTables, table, newLayout,
                entryIndex, entry.XPt, entry.Ypt, entry.PageIndex);

            // Число записей то же — номера слайсов (каретка, выделение) не сдвигаются.
            if (fresh.Count != last - first + 1 || newTables.Count != tables.Count) return false;

            var newLayouts = new List<ParaLayout>(layouts);
            for (int k = 0; k < fresh.Count; k++)
                newLayouts[first + k] = fresh[k];

            lock (_renderLock)
            {
                _layouts = newLayouts;
                _tables = newTables;
            }

            PerfCount("ui.quick.table");
            NotePartialPage(entry.PageIndex);
            InvalidateFull();
            return true;
        }

        /// <summary>
        /// Таблицы, в ячейках которых стоят эти абзацы. False — какой-то абзац не в
        /// ячейке показанной раскладки.
        /// </summary>
        private bool TablesOfCellParagraphs(IEnumerable<ParagraphBlock> paragraphs, HashSet<TableBlock> tables)
        {
            List<ParaLayout> layouts;
            lock (_renderLock) layouts = _layouts;

            foreach (var paragraph in paragraphs)
            {
                TableBlock? found = null;
                foreach (var pl in layouts)
                {
                    if (pl.Cell is { } cell && ReferenceEquals(cell.ParaBlock, paragraph))
                    {
                        found = cell.Table;
                        break;
                    }
                }

                if (found is null) return false;
                tables.Add(found);
            }

            return true;
        }

        /// <summary>
        /// Полная раскладка листов после паузы в наборе — для правки, которую быстрый
        /// путь уже показал (абзац или таблица переложены на месте).
        /// </summary>
        private void ScheduleDeferredFullRebuild()
        {
            MarkContentEdit();

            var oldCts = _rebuildCts;
            _rebuildCts = new System.Threading.CancellationTokenSource();
            oldCts.Cancel();
            oldCts.Dispose();
            var cts = _rebuildCts;

            _typingRebuild = () =>
            {
                if (cts.IsCancellationRequested) return;

                double oldCanvasH = _canvasHeight;
                RebuildLayouts();
                SnapCaretToCorrectSlice();

                if (Math.Abs(_canvasHeight - oldCanvasH) > 0.5)
                    InvalidateMeasure();
                else
                    InvalidateFull();
            };

            _typingRebuildTimer ??= CreateTypingRebuildTimer();
            _typingRebuildTimer.Stop();
            _typingRebuildTimer.Start();
        }
    }
}
