using System;
using System.Collections.Generic;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.Models.Styles;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Правки рецензирования на уровне модели: запись правок при наборе и удалении,
    /// принятие и отклонение, поиск следующей и предыдущей правки.
    ///
    /// Здесь только модель документа: что происходит со знаками и абзацами. Шаг
    /// отмены, каретку и раскладку ведёт полотно.
    ///
    /// Абзацы живут в «списках»: поток раздела (вперемешку с таблицами, картинками
    /// и разрывами) и абзацы ячейки таблицы. Слить два абзаца можно только внутри
    /// одного списка и только если между ними нет таблицы или разрыва — так же
    /// не сливает их и Word.
    /// </summary>
    public static class RevisionService
    {
        // ── Списки абзацев ────────────────────────────────────────────────

        /// <summary>Список, в котором лежат абзацы: поток раздела или ячейка таблицы.</summary>
        public sealed class ParagraphList
        {
            private readonly List<BlockModel>? _flow;
            private readonly List<ParagraphBlock>? _cell;

            private ParagraphList(List<BlockModel>? flow, List<ParagraphBlock>? cell, TableCell? owner)
            {
                _flow = flow;
                _cell = cell;
                Cell = owner;
            }

            public static ParagraphList Flow(List<BlockModel> blocks) => new(blocks, null, null);

            public static ParagraphList OfCell(TableCell cell) => new(null, cell.Paragraphs, cell);

            /// <summary>Ячейка, которой принадлежит список. Null — поток раздела.</summary>
            public TableCell? Cell { get; }

            public bool IsFlow => _flow is not null;

            public int Count => _flow?.Count ?? _cell!.Count;

            /// <summary>Абзац на месте i; null — там не абзац (таблица, картинка, разрыв).</summary>
            public ParagraphBlock? ParagraphAt(int i)
                => _flow is not null ? _flow[i] as ParagraphBlock : _cell![i];

            /// <summary>Блок, через который абзацы не сливаются: таблица или разрыв.</summary>
            public bool IsBarrier(int i)
                => _flow is not null && _flow[i] is TableBlock or BreakBlock;

            public BlockModel BlockAt(int i) => _flow is not null ? _flow[i] : _cell![i];

            public int IndexOf(ParagraphBlock paragraph)
            {
                if (_flow is not null)
                {
                    for (int i = 0; i < _flow.Count; i++)
                        if (ReferenceEquals(_flow[i], paragraph)) return i;
                    return -1;
                }

                for (int i = 0; i < _cell!.Count; i++)
                    if (ReferenceEquals(_cell[i], paragraph)) return i;
                return -1;
            }

            public void RemoveAt(int i)
            {
                if (_flow is not null) _flow.RemoveAt(i);
                else _cell!.RemoveAt(i);
            }

            /// <summary>
            /// Следующий абзац того же списка, с которым этот можно слить. -1 — его нет
            /// или между ними таблица либо разрыв.
            /// </summary>
            public int NextMergeable(int index)
            {
                for (int j = index + 1; j < Count; j++)
                {
                    if (IsBarrier(j)) return -1;
                    if (ParagraphAt(j) is not null) return j;
                }

                return -1;
            }

            /// <summary>Предыдущий абзац, с которым этот можно слить. -1 — нет.</summary>
            public int PreviousMergeable(int index)
            {
                for (int j = index - 1; j >= 0; j--)
                {
                    if (IsBarrier(j)) return -1;
                    if (ParagraphAt(j) is not null) return j;
                }

                return -1;
            }
        }

        /// <summary>
        /// Все списки абзацев документа: поток каждого раздела и ячейки таблиц, в том
        /// числе вложенных.
        /// </summary>
        public static IEnumerable<ParagraphList> AllLists(DocumentModel doc)
        {
            foreach (var section in doc.Sections)
            {
                yield return ParagraphList.Flow(section.Blocks);

                foreach (var block in section.Blocks)
                    if (block is TableBlock table)
                        foreach (var list in TableLists(table))
                            yield return list;
            }
        }

        private static IEnumerable<ParagraphList> TableLists(TableBlock table)
        {
            foreach (var cell in table.Cells)
            {
                yield return ParagraphList.OfCell(cell);

                if (cell.NestedTables is { Count: > 0 } nested)
                    foreach (var inner in nested)
                        foreach (var list in TableLists(inner.Table))
                            yield return list;
            }
        }

        /// <summary>Список, в котором лежит абзац, и его место в нём.</summary>
        public static (ParagraphList List, int Index)? Locate(DocumentModel doc, ParagraphBlock paragraph)
        {
            foreach (var list in AllLists(doc))
            {
                int index = list.IndexOf(paragraph);
                if (index >= 0) return (list, index);
            }

            return null;
        }

        /// <summary>
        /// Все абзацы документа в порядке чтения: поток, а таблица — ячейка за
        /// ячейкой, по строкам, с вложенными таблицами на своих местах.
        /// </summary>
        public static List<ParagraphBlock> ReadingOrder(DocumentModel doc)
        {
            var result = new List<ParagraphBlock>();

            foreach (var section in doc.Sections)
            {
                foreach (var block in section.Blocks)
                {
                    if (block is ParagraphBlock paragraph) result.Add(paragraph);
                    else if (block is TableBlock table) AppendTable(table, result);
                }
            }

            return result;
        }

        private static void AppendTable(TableBlock table, List<ParagraphBlock> result)
        {
            var cells = new List<TableCell>(table.Cells);
            cells.Sort((a, b) => a.Row != b.Row ? a.Row.CompareTo(b.Row) : a.Column.CompareTo(b.Column));

            foreach (var cell in cells)
                foreach (var paragraph in cell.ParagraphsDeep())
                    result.Add(paragraph);
        }

        // ── Сведения о правках ────────────────────────────────────────────

        /// <summary>Авторы правок в порядке первого появления в документе.</summary>
        public static List<string> Authors(DocumentModel doc)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<string>();

            void Add(RevisionInfo? info)
            {
                if (info is null) return;
                if (seen.Add(info.Author)) result.Add(info.Author);
            }

            foreach (var paragraph in ReadingOrder(doc))
            {
                foreach (var chunk in paragraph.Chunks)
                    foreach (var run in chunk.Runs)
                    {
                        Add(run.Properties?.Inserted);
                        Add(run.Properties?.Deleted);
                        Add(run.Properties?.FormatChange?.Info);
                    }

                Add(paragraph.Properties.MarkInserted);
                Add(paragraph.Properties.MarkDeleted);
                Add(paragraph.Properties.FormatChange?.Info);
            }

            return result;
        }

        /// <summary>
        /// Сколько правок в документе: подряд идущие знаки одной правки считаются
        /// одной, знак абзаца и смена оформления абзаца — каждая своей.
        /// </summary>
        public static int CountRevisions(DocumentModel doc)
        {
            int count = 0;

            foreach (var paragraph in ReadingOrder(doc))
            {
                if (paragraph.Properties.FormatChange is not null) count++;

                var cells = paragraph.ToCharCells();
                for (int i = 0; i < cells.Count; i++)
                {
                    var props = cells[i].Props;
                    if (props?.HasRevision != true) continue;
                    if (i > 0 && SameRevisionGroup(cells[i - 1].Props, props)) continue;
                    count++;
                }

                if (paragraph.Properties.MarkInserted is not null || paragraph.Properties.MarkDeleted is not null)
                    count++;
            }

            return count;
        }

        /// <summary>Знаки принадлежат одной правке: те же отметки вставки, удаления и смены оформления.</summary>
        public static bool SameRevisionGroup(RunProperties? a, RunProperties? b)
        {
            if (a?.HasRevision != true || b?.HasRevision != true) return false;
            return RunProperties.SameRevisions(a, b);
        }

        // ── Запись правок ─────────────────────────────────────────────────

        /// <summary>
        /// Отмечает знаки [from, to) вставленными: прежние отметки снимаются, ставится
        /// вставка. Оформление знаков не меняется.
        /// </summary>
        public static void MarkInserted(ParagraphBlock paragraph, int from, int to, RevisionInfo info)
        {
            var cells = paragraph.ToCharCells();
            from = Math.Clamp(from, 0, cells.Count);
            to = Math.Clamp(to, from, cells.Count);
            if (from == to) return;

            var mapped = new Dictionary<RunProperties, RunProperties>(ReferenceEqualityComparer.Instance);
            RunProperties? fromNull = null;

            for (int i = from; i < to; i++)
            {
                var source = cells[i].Props;
                RunProperties marked;

                if (source is null)
                {
                    marked = fromNull ??= new RunProperties { Inserted = info.Clone() };
                }
                else if (!mapped.TryGetValue(source, out marked!))
                {
                    marked = source.WithoutRevisions();
                    marked.Inserted = info.Clone();
                    mapped[source] = marked;
                }

                cells[i] = new ParagraphBlock.CharCell(cells[i].Ch, marked, cells[i].InlineImageId);
            }

            paragraph.RebuildFromCharCells(cells);
        }

        /// <summary>
        /// Удаляет знаки [from, to) под рецензированием. Уже удалённое остаётся как
        /// есть; вставленное тем же автором убирается совсем — как в Word, своя
        /// вставка при удалении правкой не становится; остальное отмечается удалённым.
        /// Возвращает число знаков, убранных совсем.
        /// </summary>
        public static int MarkDeleted(ParagraphBlock paragraph, int from, int to, RevisionInfo info)
        {
            var cells = paragraph.ToCharCells();
            from = Math.Clamp(from, 0, cells.Count);
            to = Math.Clamp(to, from, cells.Count);
            if (from == to) return 0;

            var result = new List<ParagraphBlock.CharCell>(cells.Count);
            var mapped = new Dictionary<RunProperties, RunProperties>(ReferenceEqualityComparer.Instance);
            RunProperties? fromNull = null;
            int removed = 0;

            for (int i = 0; i < cells.Count; i++)
            {
                if (i < from || i >= to)
                {
                    result.Add(cells[i]);
                    continue;
                }

                var source = cells[i].Props;

                if (source?.Deleted is not null)
                {
                    result.Add(cells[i]);
                    continue;
                }

                if (IsOwnInsertion(source?.Inserted, info))
                {
                    removed++;
                    continue;
                }

                RunProperties marked;
                if (source is null)
                {
                    marked = fromNull ??= new RunProperties { Deleted = info.Clone() };
                }
                else if (!mapped.TryGetValue(source, out marked!))
                {
                    marked = source.Clone();
                    marked.Deleted = info.Clone();
                    mapped[source] = marked;
                }

                result.Add(new ParagraphBlock.CharCell(cells[i].Ch, marked, cells[i].InlineImageId));
            }

            paragraph.RebuildFromCharCells(result);
            return removed;
        }

        /// <summary>Вставка сделана этим же автором (не перемещение) — её удаление убирает её совсем.</summary>
        public static bool IsOwnInsertion(RevisionInfo? inserted, RevisionInfo author)
            => inserted is not null && !inserted.IsMove
               && string.Equals(inserted.Author, author.Author, StringComparison.Ordinal);

        /// <summary>
        /// Сливает абзац на месте index со следующим в том же списке: знаки второго
        /// дописываются в конец первого, знак абзаца остаётся второго — с его
        /// отметками правок, оформление абзаца остаётся первого. Возвращает длину
        /// первого до слияния (место, с которого в нём начался второй) или -1, если
        /// слить нельзя.
        /// </summary>
        public static int MergeWithNext(ParagraphList list, int index)
        {
            var first = list.ParagraphAt(index);
            int nextIndex = list.NextMergeable(index);
            if (first is null || nextIndex < 0) return -1;

            var second = list.ParagraphAt(nextIndex)!;

            var cells = first.ToCharCells();
            int joinAt = cells.Count;
            cells.AddRange(second.ToCharCells());
            first.RebuildFromCharCells(cells);

            first.Properties.MarkInserted = second.Properties.MarkInserted?.Clone();
            first.Properties.MarkDeleted = second.Properties.MarkDeleted?.Clone();

            list.RemoveAt(nextIndex);
            return joinAt;
        }

        // ── Принятие и отклонение ─────────────────────────────────────────

        /// <summary>
        /// Что из абзаца входит в правку: знаки [From, To) и, если Mark, — знак абзаца и
        /// смена оформления абзаца.
        /// </summary>
        public readonly record struct Scope(int From, int To, bool Mark);

        /// <summary>
        /// Принимает или отклоняет правки в заданных местах документа. Возвращает
        /// абзацы, которые изменились, и признак, что менялся состав абзацев (слияния).
        ///
        /// Списки обходятся с конца: слияние абзаца со следующим не сдвигает места
        /// абзацев, которые ещё предстоит обойти.
        /// </summary>
        public static (HashSet<ParagraphBlock> Touched, bool StructureChanged) Resolve(
            DocumentModel doc, Func<ParagraphBlock, Scope?> scopeOf, bool accept)
        {
            var touched = new HashSet<ParagraphBlock>(ReferenceEqualityComparer.Instance);
            bool structureChanged = false;

            // Списки собираются заранее: слияние меняет состав ячеек и потока, а
            // перечисление по живым спискам рассыпалось бы.
            var lists = new List<ParagraphList>(AllLists(doc));

            foreach (var list in lists)
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (i >= list.Count) continue;

                    var paragraph = list.ParagraphAt(i);
                    if (paragraph is null) continue;
                    if (scopeOf(paragraph) is not { } scope) continue;

                    if (ResolveRange(paragraph, scope.From, scope.To, accept))
                        touched.Add(paragraph);

                    if (!scope.Mark) continue;

                    if (ResolveParagraphFormat(paragraph, accept))
                        touched.Add(paragraph);

                    var props = paragraph.Properties;
                    bool markGoes = accept ? props.MarkDeleted is not null : props.MarkInserted is not null;
                    bool markStays = accept ? props.MarkInserted is not null : props.MarkDeleted is not null;

                    if (markGoes)
                    {
                        // Знак абзаца уходит — абзац сливается со следующим. Нельзя
                        // слить (последний в списке, за ним таблица) — знак остаётся
                        // обычным.
                        if (MergeWithNext(list, i) >= 0)
                        {
                            structureChanged = true;
                        }
                        else
                        {
                            props.MarkInserted = null;
                            props.MarkDeleted = null;
                        }

                        touched.Add(paragraph);
                    }
                    else if (markStays)
                    {
                        props.MarkInserted = null;
                        props.MarkDeleted = null;
                        touched.Add(paragraph);
                    }
                }
            }

            return (touched, structureChanged);
        }

        /// <summary>
        /// Принимает или отклоняет правки знаков [from, to) абзаца:
        ///   принять — вставленное становится обычным текстом, удалённое уходит,
        ///   прежнее оформление забывается;
        ///   отклонить — вставленное уходит, удалённое возвращается, оформление
        ///   становится прежним.
        /// </summary>
        public static bool ResolveRange(ParagraphBlock paragraph, int from, int to, bool accept)
        {
            var cells = paragraph.ToCharCells();
            from = Math.Clamp(from, 0, cells.Count);
            to = Math.Clamp(to, from, cells.Count);

            bool changed = false;
            var result = new List<ParagraphBlock.CharCell>(cells.Count);
            var mapped = new Dictionary<RunProperties, RunProperties?>(ReferenceEqualityComparer.Instance);

            for (int i = 0; i < cells.Count; i++)
            {
                var props = cells[i].Props;
                if (i < from || i >= to || props?.HasRevision != true)
                {
                    result.Add(cells[i]);
                    continue;
                }

                changed = true;

                // Знак уходит: принятое удаление или отклонённая вставка. Знак, у
                // которого есть обе отметки (удалённая вставка), уходит в обоих случаях:
                // принять — удаление, отклонить — вставку.
                bool goes = accept ? props.Deleted is not null : props.Inserted is not null;
                if (goes) continue;

                if (!mapped.TryGetValue(props, out var resolved))
                {
                    resolved = ResolvedProperties(props, accept);
                    mapped[props] = resolved;
                }

                result.Add(new ParagraphBlock.CharCell(cells[i].Ch, resolved, cells[i].InlineImageId));
            }

            if (changed) paragraph.RebuildFromCharCells(result);
            return changed;
        }

        /// <summary>Свойства знака, который после решения по правке остаётся.</summary>
        private static RunProperties? ResolvedProperties(RunProperties props, bool accept)
        {
            RunProperties resolved;

            if (!accept && props.FormatChange is { } change)
            {
                // Отклонённая смена оформления: прежнее оформление. Остальные отметки
                // этим решением не затрагиваются — их снимает своё правило ниже.
                resolved = change.Previous?.Clone() ?? new RunProperties();
            }
            else
            {
                resolved = props.WithoutRevisions();
            }

            // Остающийся знак — принятая вставка или отклонённое удаление: в обоих
            // случаях он становится обычным текстом, без отметок.
            resolved.Inserted = null;
            resolved.Deleted = null;
            resolved.FormatChange = null;

            return resolved.IsDefault() ? null : resolved;
        }

        /// <summary>Принимает или отклоняет смену оформления абзаца.</summary>
        public static bool ResolveParagraphFormat(ParagraphBlock paragraph, bool accept)
        {
            var props = paragraph.Properties;
            if (props.FormatChange is not { } change) return false;

            if (!accept && change.Previous is { } previous)
            {
                var markInserted = props.MarkInserted;
                var markDeleted = props.MarkDeleted;

                props.CopyFrom(previous);

                props.MarkInserted = markInserted;
                props.MarkDeleted = markDeleted;
            }

            props.FormatChange = null;
            return true;
        }

        // ── Поиск правок ──────────────────────────────────────────────────

        /// <summary>Место правки в документе: абзац и знаки [From, To).</summary>
        public readonly record struct Hit(ParagraphBlock Paragraph, int From, int To, bool IsMark);

        /// <summary>
        /// Все правки абзаца по порядку: смена оформления абзаца (в его начале),
        /// группы знаков одной правки, знак абзаца (в конце).
        /// </summary>
        public static List<Hit> HitsIn(ParagraphBlock paragraph)
        {
            var hits = new List<Hit>();
            var cells = paragraph.ToCharCells();

            if (paragraph.Properties.FormatChange is not null)
                hits.Add(new Hit(paragraph, 0, cells.Count, true));

            int i = 0;
            while (i < cells.Count)
            {
                var props = cells[i].Props;
                if (props?.HasRevision != true)
                {
                    i++;
                    continue;
                }

                int start = i;
                i++;
                while (i < cells.Count && SameRevisionGroup(cells[i].Props, props)) i++;

                hits.Add(new Hit(paragraph, start, i, false));
            }

            if (paragraph.Properties.MarkInserted is not null || paragraph.Properties.MarkDeleted is not null)
                hits.Add(new Hit(paragraph, cells.Count, cells.Count, true));

            return hits;
        }

        /// <summary>
        /// Правка под позицией: группа знаков, в которой позиция стоит или которая
        /// кончается прямо перед ней; знак абзаца, если позиция в конце абзаца.
        /// </summary>
        public static Hit? HitAt(ParagraphBlock paragraph, int position)
        {
            Hit? markHit = null;

            foreach (var hit in HitsIn(paragraph))
            {
                if (hit.IsMark)
                {
                    if (hit.From == hit.To && position >= hit.From) return hit;
                    markHit ??= hit;
                    continue;
                }

                if (position >= hit.From && position <= hit.To) return hit;
            }

            return markHit;
        }
    }
}
