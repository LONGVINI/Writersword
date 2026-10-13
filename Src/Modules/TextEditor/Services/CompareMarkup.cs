using System;
using System.Collections.Generic;
using System.Text;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Разметка различий двух версий документа в режиме сравнения.
    ///
    /// Показанная версия размечается так же, как рецензирование: текст, которого во
    /// второй версии нет, — вставлен, текст, который был только во второй, — удалён
    /// и вставлен на своё место зачёркнутым, сменённое оформление — правка
    /// оформления с прежним оформлением из второй версии. Все пометки — от одного
    /// автора (<see cref="Author"/>), и на листе, в карточках и в панели рецензии
    /// они выглядят как обычные правки.
    ///
    /// Разметка меняет модель показанной версии, поэтому делается только над её
    /// копией для показа: данные модуля для сохранения остаются данными показанной
    /// версии без пометок (TextEditorModule).
    ///
    /// Сопоставление идёт по Id блоков: обе версии — один и тот же документ в разные
    /// моменты, и Id абзацев, таблиц и ячеек у них общие. Порядок блоков сверяется
    /// разностью последовательностей, текст сопоставленных абзацев — по словам, как
    /// сравнивает Word: слово меняется целиком, а не по буквам.
    /// </summary>
    public static class CompareMarkup
    {
        /// <summary>Автор пометок сравнения в карточках правок.</summary>
        public const string Author = "Сравнение версий";

        // Разность длиннее этого числа правок считать дальше незачем: такие версии —
        // уже разные документы, и разметка «удалено всё, вставлено всё» честнее.
        private const int MaxEditDistance = 4000;

        /// <summary>Сколько мест размечено.</summary>
        public sealed class Result
        {
            public int InsertedPlaces { get; set; }
            public int DeletedPlaces { get; set; }
            public int FormatPlaces { get; set; }

            public int Total => InsertedPlaces + DeletedPlaces + FormatPlaces;
        }

        /// <summary>
        /// Размечает показанную версию различиями со второй. Вторая версия после
        /// вызова непригодна: удалённые блоки и абзацы переносятся из неё в
        /// показанную, без копирования.
        /// </summary>
        public static Result Apply(DocumentModel shown, DocumentModel other)
        {
            var result = new Result();

            if (shown.Sections.Count == 0 || other.Sections.Count == 0)
                return result;

            var context = new Context(shown, other, result);

            var shownSection = shown.Sections[0];
            var otherSection = other.Sections[0];
            shownSection.Blocks = context.MergeBlocks(shownSection.Blocks, otherSection.Blocks);

            // Пометки видны только при показе всех исправлений: вид «без исправлений»
            // спрятал бы как раз то, ради чего открыто сравнение.
            if (result.Total > 0)
                shown.RevisionView = RevisionView.AllMarkup;

            return result;
        }

        private sealed class Context
        {
            private readonly DocumentModel _shown;
            private readonly Result _result;
            private readonly Dictionary<Guid, BlockModel> _otherInlineObjects = new();
            private readonly HashSet<Guid> _shownInlineObjects = new();

            public Context(DocumentModel shown, DocumentModel other, Result result)
            {
                _shown = shown;
                _result = result;

                foreach (var block in other.Sections[0].InlineObjects)
                    _otherInlineObjects[block.Id] = block;
                foreach (var block in shown.Sections[0].InlineObjects)
                    _shownInlineObjects.Add(block.Id);
            }

            private static RevisionInfo NewInfo() => new() { Author = Author };

            // ── Блоки ────────────────────────────────────────────────────────

            /// <summary>
            /// Новый список блоков показанной версии: её блоки на своих местах, блоки,
            /// которые были только во второй версии, — вставлены перед местом, где
            /// стояли, и размечены удалёнными.
            /// </summary>
            public List<BlockModel> MergeBlocks(List<BlockModel> shownBlocks, List<BlockModel> otherBlocks)
            {
                var ids = new Dictionary<Guid, int>();
                int[] a = BlockKeys(shownBlocks, ids);
                int[] b = BlockKeys(otherBlocks, ids);

                var merged = new List<BlockModel>(shownBlocks.Count + 8);

                foreach (var op in SequenceDiff.Compute(a, b, MaxEditDistance))
                {
                    switch (op.Kind)
                    {
                        case SequenceDiff.OpKind.Equal:
                        {
                            var shownBlock = shownBlocks[op.IndexA];
                            CompareMatchedBlocks(shownBlock, otherBlocks[op.IndexB]);
                            merged.Add(shownBlock);
                            break;
                        }
                        case SequenceDiff.OpKind.OnlyA:
                        {
                            var shownBlock = shownBlocks[op.IndexA];
                            MarkBlockInserted(shownBlock);
                            merged.Add(shownBlock);
                            break;
                        }
                        case SequenceDiff.OpKind.OnlyB:
                        {
                            var otherBlock = otherBlocks[op.IndexB];
                            if (MarkBlockDeleted(otherBlock))
                                merged.Add(otherBlock);
                            break;
                        }
                    }
                }

                return merged;
            }

            /// <summary>
            /// Ключи блоков для разности: Id, а для блоков с одинаковым Id в одной
            /// версии (копия, вставленная с тем же Id) — ещё и номер повтора.
            /// </summary>
            private static int[] BlockKeys(List<BlockModel> blocks, Dictionary<Guid, int> ids)
            {
                var keys = new int[blocks.Count];
                for (int i = 0; i < blocks.Count; i++)
                {
                    var id = blocks[i].Id;
                    if (!ids.TryGetValue(id, out int key))
                    {
                        key = ids.Count;
                        ids[id] = key;
                    }
                    keys[i] = key;
                }
                return keys;
            }

            private void CompareMatchedBlocks(BlockModel shownBlock, BlockModel otherBlock)
            {
                switch (shownBlock)
                {
                    case ParagraphBlock shownParagraph when otherBlock is ParagraphBlock otherParagraph:
                        CompareParagraphs(shownParagraph, otherParagraph);
                        break;

                    case TableBlock shownTable when otherBlock is TableBlock otherTable:
                        CompareTables(shownTable, otherTable);
                        break;
                }
            }

            private void MarkBlockInserted(BlockModel block)
            {
                switch (block)
                {
                    case ParagraphBlock paragraph:
                        MarkParagraphInserted(paragraph);
                        break;

                    case TableBlock table:
                        foreach (var cell in table.Cells)
                            foreach (var paragraph in cell.ParagraphsDeep())
                                MarkParagraphInserted(paragraph);
                        break;

                    case ImageBlock image when image.Inserted is null && image.Deleted is null:
                        image.Inserted = NewInfo();
                        _result.InsertedPlaces++;
                        break;
                }
            }

            /// <summary>
            /// Размечает удалённым блок второй версии. False — такой блок в показанную
            /// версию не переносится (картинки и фигуры второй версии могут ссылаться
            /// на файлы, которых в проекте уже нет).
            /// </summary>
            private bool MarkBlockDeleted(BlockModel block)
            {
                switch (block)
                {
                    case ParagraphBlock paragraph:
                        MarkParagraphDeleted(paragraph);
                        return true;

                    case TableBlock table:
                        foreach (var cell in table.Cells)
                            foreach (var paragraph in cell.ParagraphsDeep())
                                MarkParagraphDeleted(paragraph);
                        return true;

                    default:
                        return false;
                }
            }

            // ── Таблицы ──────────────────────────────────────────────────────

            private void CompareTables(TableBlock shownTable, TableBlock otherTable)
            {
                var otherCells = new Dictionary<Guid, TableCell>();
                foreach (var cell in otherTable.Cells)
                    otherCells[cell.Id] = cell;

                foreach (var cell in shownTable.Cells)
                {
                    if (!otherCells.TryGetValue(cell.Id, out var otherCell))
                    {
                        foreach (var paragraph in cell.ParagraphsDeep())
                            MarkParagraphInserted(paragraph);
                        continue;
                    }

                    cell.Paragraphs = MergeParagraphs(cell.Paragraphs, otherCell.Paragraphs);

                    // Вставленные абзацы удалённого текста сдвинули номера абзацев,
                    // перед которыми стоят вложенные таблицы и плавающие объекты ячейки.
                    cell.AnchorNestedTables();
                    cell.AnchorFloats();

                    if (cell.NestedTables is not { Count: > 0 } nestedTables) continue;

                    var otherNested = new Dictionary<Guid, TableBlock>();
                    if (otherCell.NestedTables is { } otherNestedTables)
                        foreach (var nested in otherNestedTables)
                            otherNested[nested.Table.Id] = nested.Table;

                    foreach (var nested in nestedTables)
                    {
                        if (otherNested.TryGetValue(nested.Table.Id, out var otherTableNested))
                            CompareTables(nested.Table, otherTableNested);
                        else
                            MarkBlockInserted(nested.Table);
                    }
                }
            }

            /// <summary>Абзацы ячейки: те же правила, что у блоков документа.</summary>
            private List<ParagraphBlock> MergeParagraphs(List<ParagraphBlock> shownParagraphs, List<ParagraphBlock> otherParagraphs)
            {
                var ids = new Dictionary<Guid, int>();
                var a = new int[shownParagraphs.Count];
                var b = new int[otherParagraphs.Count];

                for (int i = 0; i < a.Length; i++) a[i] = KeyOf(shownParagraphs[i].Id, ids);
                for (int i = 0; i < b.Length; i++) b[i] = KeyOf(otherParagraphs[i].Id, ids);

                var merged = new List<ParagraphBlock>(shownParagraphs.Count + 2);

                foreach (var op in SequenceDiff.Compute(a, b, MaxEditDistance))
                {
                    switch (op.Kind)
                    {
                        case SequenceDiff.OpKind.Equal:
                            CompareParagraphs(shownParagraphs[op.IndexA], otherParagraphs[op.IndexB]);
                            merged.Add(shownParagraphs[op.IndexA]);
                            break;

                        case SequenceDiff.OpKind.OnlyA:
                            MarkParagraphInserted(shownParagraphs[op.IndexA]);
                            merged.Add(shownParagraphs[op.IndexA]);
                            break;

                        case SequenceDiff.OpKind.OnlyB:
                            MarkParagraphDeleted(otherParagraphs[op.IndexB]);
                            merged.Add(otherParagraphs[op.IndexB]);
                            break;
                    }
                }

                return merged;
            }

            private static int KeyOf(Guid id, Dictionary<Guid, int> ids)
            {
                if (!ids.TryGetValue(id, out int key))
                {
                    key = ids.Count;
                    ids[id] = key;
                }
                return key;
            }

            // ── Абзацы ───────────────────────────────────────────────────────

            private void MarkParagraphInserted(ParagraphBlock paragraph)
            {
                var cells = paragraph.ToCharCells();
                bool changed = false;

                for (int i = 0; i < cells.Count; i++)
                {
                    var cell = cells[i];
                    if (cell.Props is { Inserted: not null } or { Deleted: not null }) continue;
                    cells[i] = WithInserted(cell);
                    changed = true;
                }

                if (changed) paragraph.RebuildFromCharCells(cells);

                if (paragraph.Properties.MarkInserted is null && paragraph.Properties.MarkDeleted is null)
                    paragraph.Properties.MarkInserted = NewInfo();

                _result.InsertedPlaces++;
            }

            private void MarkParagraphDeleted(ParagraphBlock paragraph)
            {
                var cells = paragraph.ToCharCells();

                for (int i = 0; i < cells.Count; i++)
                    cells[i] = WithDeleted(cells[i]);

                paragraph.RebuildFromCharCells(cells);

                paragraph.Properties.MarkInserted = null;
                paragraph.Properties.MarkDeleted = NewInfo();

                _result.DeletedPlaces++;
            }

            /// <summary>
            /// Текст сопоставленных абзацев: разность по словам. Совпавшие слова
            /// сверяются ещё и по оформлению.
            /// </summary>
            private void CompareParagraphs(ParagraphBlock shownParagraph, ParagraphBlock otherParagraph)
            {
                // Почти все абзацы версий одинаковы: их проверка не должна стоить
                // разбора по знакам.
                if (SameContent(shownParagraph, otherParagraph)) return;

                var shownCells = shownParagraph.ToCharCells();
                var otherCells = otherParagraph.ToCharCells();

                var shownTokens = Tokenize(shownCells);
                var otherTokens = Tokenize(otherCells);

                var dictionary = new Dictionary<string, int>(StringComparer.Ordinal);
                var a = new int[shownTokens.Count];
                var b = new int[otherTokens.Count];
                for (int i = 0; i < a.Length; i++) a[i] = TokenKey(shownTokens[i].Text, dictionary);
                for (int i = 0; i < b.Length; i++) b[i] = TokenKey(otherTokens[i].Text, dictionary);

                var merged = new List<ParagraphBlock.CharCell>(shownCells.Count + 8);
                bool changed = false;

                // Пометка считается местом один раз на сплошной кусок: «вставлено
                // слово с пробелом» — одна правка, а не две.
                SequenceDiff.OpKind previous = SequenceDiff.OpKind.Equal;
                bool previousFormat = false;

                foreach (var op in SequenceDiff.Compute(a, b, MaxEditDistance))
                {
                    switch (op.Kind)
                    {
                        case SequenceDiff.OpKind.Equal:
                        {
                            var shownToken = shownTokens[op.IndexA];
                            var otherToken = otherTokens[op.IndexB];
                            bool formatHere = false;

                            for (int k = 0; k < shownToken.Length; k++)
                            {
                                var shownCell = shownCells[shownToken.Start + k];
                                var otherCell = otherCells[otherToken.Start + k];

                                if (shownCell.Props is { HasRevision: true }
                                    || RunProperties.SameFormatting(shownCell.Props, otherCell.Props))
                                {
                                    merged.Add(shownCell);
                                    continue;
                                }

                                merged.Add(WithFormatChange(shownCell, otherCell.Props));
                                formatHere = true;
                                changed = true;
                            }

                            if (formatHere && !previousFormat) _result.FormatPlaces++;
                            previousFormat = formatHere;
                            previous = SequenceDiff.OpKind.Equal;
                            break;
                        }

                        case SequenceDiff.OpKind.OnlyA:
                        {
                            var token = shownTokens[op.IndexA];
                            for (int k = 0; k < token.Length; k++)
                            {
                                var cell = shownCells[token.Start + k];
                                merged.Add(cell.Props is { Inserted: not null } or { Deleted: not null }
                                    ? cell
                                    : WithInserted(cell));
                            }

                            if (previous != SequenceDiff.OpKind.OnlyA) _result.InsertedPlaces++;
                            previous = SequenceDiff.OpKind.OnlyA;
                            previousFormat = false;
                            changed = true;
                            break;
                        }

                        case SequenceDiff.OpKind.OnlyB:
                        {
                            var token = otherTokens[op.IndexB];
                            for (int k = 0; k < token.Length; k++)
                                merged.Add(WithDeleted(otherCells[token.Start + k]));

                            if (previous != SequenceDiff.OpKind.OnlyB) _result.DeletedPlaces++;
                            previous = SequenceDiff.OpKind.OnlyB;
                            previousFormat = false;
                            changed = true;
                            break;
                        }
                    }
                }

                if (changed)
                    shownParagraph.RebuildFromCharCells(merged);
            }

            /// <summary>
            /// Абзацы совпадают и текстом, и оформлением каждого знака. Проход по
            /// ранам обоих абзацев сразу, без разворачивания в знаки.
            /// </summary>
            private static bool SameContent(ParagraphBlock a, ParagraphBlock b)
            {
                if (!string.Equals(a.GetPlainText(), b.GetPlainText(), StringComparison.Ordinal))
                    return false;

                using var runsA = EnumerateRuns(a).GetEnumerator();
                using var runsB = EnumerateRuns(b).GetEnumerator();

                bool hasA = runsA.MoveNext();
                bool hasB = runsB.MoveNext();
                int leftA = hasA ? RunLength(runsA.Current) : 0;
                int leftB = hasB ? RunLength(runsB.Current) : 0;

                while (hasA && hasB)
                {
                    if (leftA == 0) { hasA = runsA.MoveNext(); leftA = hasA ? RunLength(runsA.Current) : 0; continue; }
                    if (leftB == 0) { hasB = runsB.MoveNext(); leftB = hasB ? RunLength(runsB.Current) : 0; continue; }

                    var runA = runsA.Current;
                    var runB = runsB.Current;

                    if (runA.InlineImageId != runB.InlineImageId) return false;
                    if (!RunProperties.SameFormatting(runA.Properties, runB.Properties)) return false;

                    int step = Math.Min(leftA, leftB);
                    leftA -= step;
                    leftB -= step;
                }

                return true;
            }

            private static IEnumerable<RunModel> EnumerateRuns(ParagraphBlock paragraph)
            {
                foreach (var chunk in paragraph.Chunks)
                    foreach (var run in chunk.Runs)
                        yield return run;
            }

            private static int RunLength(RunModel run) => run.InlineImageId.HasValue ? 1 : run.Text.Length;

            private static int TokenKey(string text, Dictionary<string, int> dictionary)
            {
                if (!dictionary.TryGetValue(text, out int key))
                {
                    key = dictionary.Count;
                    dictionary[text] = key;
                }
                return key;
            }

            // ── Знаки ────────────────────────────────────────────────────────

            private ParagraphBlock.CharCell WithInserted(ParagraphBlock.CharCell cell)
            {
                var props = cell.Props?.Clone() ?? new RunProperties();
                props.Inserted = NewInfo();
                return new ParagraphBlock.CharCell(cell.Ch, props, cell.InlineImageId);
            }

            private ParagraphBlock.CharCell WithDeleted(ParagraphBlock.CharCell cell)
            {
                var props = cell.Props?.WithoutRevisions() ?? new RunProperties();
                props.Deleted = NewInfo();

                // Примечания второй версии в показанной не существуют.
                props.CommentIds = null;

                // Картинка в строке живёт в списке объектов раздела: удалённая
                // картинка второй версии переезжает туда вместе со своим знаком,
                // иначе знак остался бы ссылкой в никуда и был бы вычищен при показе.
                if (cell.InlineImageId is Guid objectId
                    && !_shownInlineObjects.Contains(objectId)
                    && _otherInlineObjects.TryGetValue(objectId, out var objectBlock))
                {
                    _shown.Sections[0].InlineObjects.Add(objectBlock);
                    _shownInlineObjects.Add(objectId);
                }

                return new ParagraphBlock.CharCell(cell.Ch, props, cell.InlineImageId);
            }

            private static ParagraphBlock.CharCell WithFormatChange(ParagraphBlock.CharCell cell, RunProperties? previous)
            {
                var props = cell.Props?.Clone() ?? new RunProperties();
                props.FormatChange = new RunFormatChange
                {
                    Info = NewInfo(),
                    Previous = previous?.WithoutRevisions()
                };
                return new ParagraphBlock.CharCell(cell.Ch, props, cell.InlineImageId);
            }

            // ── Слова ────────────────────────────────────────────────────────

            private readonly struct Token
            {
                public Token(int start, int length, string text)
                {
                    Start = start;
                    Length = length;
                    Text = text;
                }

                public int Start { get; }
                public int Length { get; }
                public string Text { get; }
            }

            /// <summary>
            /// Слова абзаца: буквы и цифры подряд — одно слово, каждый пробел, знак
            /// препинания и объект в строке — отдельно. Объект в строке сравнивается
            /// по своему Id: две разные картинки — разные «слова».
            /// </summary>
            private static List<Token> Tokenize(List<ParagraphBlock.CharCell> cells)
            {
                var tokens = new List<Token>();
                var sb = new StringBuilder();
                int i = 0;

                while (i < cells.Count)
                {
                    var cell = cells[i];

                    if (cell.InlineImageId is Guid objectId)
                    {
                        tokens.Add(new Token(i, 1, "￼" + objectId.ToString("N")));
                        i++;
                        continue;
                    }

                    if (char.IsLetterOrDigit(cell.Ch))
                    {
                        int start = i;
                        sb.Clear();
                        while (i < cells.Count && cells[i].InlineImageId is null
                               && (char.IsLetterOrDigit(cells[i].Ch) || IsWordJoiner(cells, i)))
                        {
                            sb.Append(cells[i].Ch);
                            i++;
                        }
                        tokens.Add(new Token(start, i - start, sb.ToString()));
                        continue;
                    }

                    tokens.Add(new Token(i, 1, cell.Ch.ToString()));
                    i++;
                }

                return tokens;
            }

            /// <summary>
            /// Знак внутри слова, который слово не разрывает: апостроф, дефис, мягкий
            /// перенос между буквами («кто-то», «don't»).
            /// </summary>
            private static bool IsWordJoiner(List<ParagraphBlock.CharCell> cells, int index)
            {
                char ch = cells[index].Ch;
                if (ch is not ('\'' or '’' or '-' or '­')) return false;
                if (index == 0 || index + 1 >= cells.Count) return false;
                return char.IsLetterOrDigit(cells[index - 1].Ch) && char.IsLetterOrDigit(cells[index + 1].Ch)
                       && cells[index + 1].InlineImageId is null;
            }
        }
    }

    /// <summary>
    /// Разность двух последовательностей (алгоритм Майерса): кратчайший набор
    /// вставок и удалений, превращающий B в A. Общие начало и конец отрезаются до
    /// поиска — у версий одного документа почти всё совпадает, и сама разность
    /// считается только по месту правок.
    /// </summary>
    internal static class SequenceDiff
    {
        public enum OpKind
        {
            /// <summary>Элемент есть в обеих последовательностях.</summary>
            Equal,

            /// <summary>Элемент есть только в A.</summary>
            OnlyA,

            /// <summary>Элемент есть только в B.</summary>
            OnlyB
        }

        public readonly struct Op
        {
            public Op(OpKind kind, int indexA, int indexB)
            {
                Kind = kind;
                IndexA = indexA;
                IndexB = indexB;
            }

            public OpKind Kind { get; }
            public int IndexA { get; }
            public int IndexB { get; }
        }

        /// <summary>
        /// Шаги разности по порядку. В месте замены удалённое (только B) идёт раньше
        /// вставленного (только A), как у Word. Если правок больше maxDistance,
        /// середина целиком считается заменой.
        /// </summary>
        public static List<Op> Compute(int[] a, int[] b, int maxDistance)
        {
            var ops = new List<Op>(Math.Max(a.Length, b.Length) + 4);

            int prefix = 0;
            while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix]) prefix++;

            int suffix = 0;
            while (suffix < a.Length - prefix && suffix < b.Length - prefix
                   && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix])
                suffix++;

            for (int i = 0; i < prefix; i++)
                ops.Add(new Op(OpKind.Equal, i, i));

            int aStart = prefix;
            int bStart = prefix;
            int aEnd = a.Length - suffix;
            int bEnd = b.Length - suffix;

            Middle(a, aStart, aEnd, b, bStart, bEnd, maxDistance, ops);

            for (int i = 0; i < suffix; i++)
                ops.Add(new Op(OpKind.Equal, aEnd + i, bEnd + i));

            return ops;
        }

        private static void Middle(int[] a, int aStart, int aEnd, int[] b, int bStart, int bEnd, int maxDistance, List<Op> ops)
        {
            int n = aEnd - aStart;
            int m = bEnd - bStart;

            if (n == 0 && m == 0) return;

            if (n == 0 || m == 0)
            {
                Replace(aStart, aEnd, bStart, bEnd, ops);
                return;
            }

            int max = Math.Min(n + m, maxDistance);
            int offset = max + 1;
            var v = new int[2 * max + 3];
            var trace = new List<int[]>();
            int found = -1;

            for (int d = 0; d <= max; d++)
            {
                for (int k = -d; k <= d; k += 2)
                {
                    int x;
                    if (k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]))
                        x = v[offset + k + 1];
                    else
                        x = v[offset + k - 1] + 1;

                    int y = x - k;
                    while (x < n && y < m && a[aStart + x] == b[bStart + y])
                    {
                        x++;
                        y++;
                    }

                    v[offset + k] = x;

                    if (x >= n && y >= m)
                    {
                        found = d;
                        break;
                    }
                }

                // Состояние после шага d: только диапазон [-d, d].
                var snapshot = new int[2 * d + 1];
                Array.Copy(v, offset - d, snapshot, 0, 2 * d + 1);
                trace.Add(snapshot);

                if (found >= 0) break;
            }

            if (found < 0)
            {
                Replace(aStart, aEnd, bStart, bEnd, ops);
                return;
            }

            // Обратный проход: шаги собираются с конца.
            var reversed = new List<Op>(n + m);
            int cx = n;
            int cy = m;

            for (int d = found; d > 0; d--)
            {
                var prev = trace[d - 1];
                int k = cx - cy;

                bool down = k == -d || (k != d && Get(prev, d - 1, k - 1) < Get(prev, d - 1, k + 1));
                int prevK = down ? k + 1 : k - 1;
                int prevX = Get(prev, d - 1, prevK);
                int prevY = prevX - prevK;

                // Совпадения после шага.
                int startX = down ? prevX : prevX + 1;
                int startY = startX - k;
                while (cx > startX && cy > startY)
                {
                    cx--;
                    cy--;
                    reversed.Add(new Op(OpKind.Equal, aStart + cx, bStart + cy));
                }

                if (down)
                {
                    // Шаг вниз: элемент только в B.
                    cy--;
                    reversed.Add(new Op(OpKind.OnlyB, aStart + cx, bStart + cy));
                }
                else
                {
                    // Шаг вправо: элемент только в A.
                    cx--;
                    reversed.Add(new Op(OpKind.OnlyA, aStart + cx, bStart + cy));
                }
            }

            while (cx > 0 && cy > 0)
            {
                cx--;
                cy--;
                reversed.Add(new Op(OpKind.Equal, aStart + cx, bStart + cy));
            }

            reversed.Reverse();
            AppendDeletedFirst(reversed, ops);
        }

        private static int Get(int[] snapshot, int d, int k) => snapshot[k + d];

        /// <summary>
        /// Переносит шаги, ставя в каждой сплошной замене удалённое перед вставленным.
        /// </summary>
        private static void AppendDeletedFirst(List<Op> steps, List<Op> ops)
        {
            int i = 0;
            while (i < steps.Count)
            {
                if (steps[i].Kind == OpKind.Equal)
                {
                    ops.Add(steps[i]);
                    i++;
                    continue;
                }

                int j = i;
                while (j < steps.Count && steps[j].Kind != OpKind.Equal) j++;

                for (int t = i; t < j; t++)
                    if (steps[t].Kind == OpKind.OnlyB) ops.Add(steps[t]);
                for (int t = i; t < j; t++)
                    if (steps[t].Kind == OpKind.OnlyA) ops.Add(steps[t]);

                i = j;
            }
        }

        private static void Replace(int aStart, int aEnd, int bStart, int bEnd, List<Op> ops)
        {
            for (int j = bStart; j < bEnd; j++)
                ops.Add(new Op(OpKind.OnlyB, aStart, j));
            for (int i = aStart; i < aEnd; i++)
                ops.Add(new Op(OpKind.OnlyA, i, bEnd));
        }
    }
}
