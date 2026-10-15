using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.Models.Styles;
using Writersword.Modules.TextEditor.Rendering;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Сравнение рукописи с файлом — как «Сравнить документы» в Word.
    ///
    /// Нужно, когда правку прислали без записи исправлений: рукопись отдали, её
    /// поправили молча и вернули файлом. Все различия размечаются правками, будто
    /// запись была включена, и дальше с ними работают как с обычными: принять,
    /// отклонить, по одной или все сразу.
    ///
    /// Основой разметки служит открытая рукопись, а не присланный файл: её абзацы,
    /// примечания, картинки и всё своё, чего в файле Word нет, остаются на местах.
    /// Удалённое в файле отмечается удалённым прямо в рукописи, набранное в файле —
    /// вставляется вставкой, оформление слов, сменённое в файле, — сменой оформления.
    /// «Принять все» даёт текст файла, «Отклонить все» — рукопись как была.
    ///
    /// В отличие от <see cref="CompareMarkup"/> (две версии одного проекта с общими
    /// Id) здесь Id у сторон разные: файл прошёл через Word. Поэтому блоки
    /// сопоставляются по содержимому — текст абзаца, текст таблицы, сами байты
    /// картинки, — а изменённые абзацы, которые по тексту уже не совпадают,
    /// находятся по похожести слов и сравниваются пословно.
    ///
    /// Оформление сверяется действующее, со стилями: файл Word несёт его явным у
    /// каждого фрагмента, рукопись — часто стилем, и сравнение «как записано»
    /// показало бы сменой оформления каждый заголовок. Сверяются начертания, которые
    /// правят редакторы: жирный, курсив, подчёркивание, зачёркивание, индексы,
    /// прописные и выделение цветом. Шрифт и размер не сверяются — их разница после
    /// Word почти всегда шум перевода, а не правка.
    /// </summary>
    public static class FileCompare
    {
        // Разность длиннее этого числа правок дальше не считается: середина
        // становится заменой целиком (SequenceDiff).
        private const int MaxEditDistance = 4000;

        // Порог похожести абзацев: при меньшей доле общих слов абзацы считаются
        // разными — один удалён, другой вставлен.
        private const double ParagraphPairThreshold = 0.5;

        // Порог похожести таблиц одинаковой формы.
        private const double TablePairThreshold = 0.3;

        // Выравнивание места замены полной таблицей похожести — до этого числа
        // клеток; больше — проходом с окном.
        private const int AlignmentCellLimit = 250_000;

        // Окно прохода для больших мест замены.
        private const int GreedyWindow = 8;

        /// <summary>Итог сравнения.</summary>
        public sealed class Result
        {
            /// <summary>Мест, где текст вставлен.</summary>
            public int InsertedPlaces { get; set; }

            /// <summary>Мест, где текст удалён.</summary>
            public int DeletedPlaces { get; set; }

            /// <summary>Мест, где сменилось оформление.</summary>
            public int FormatPlaces { get; set; }

            /// <summary>
            /// Различий, которые правкой не размечаются и не переносятся: разрывы,
            /// формулы и фигуры потока, плавающие объекты файла.
            /// </summary>
            public int SkippedPlaces { get; set; }

            /// <summary>Примечаний из файла, перенесённых в рукопись.</summary>
            public int CommentsAdded { get; set; }

            /// <summary>Файлы картинок из присланного файла, которые вошли в рукопись.</summary>
            public HashSet<string> AdoptedImageFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

            public int Total => InsertedPlaces + DeletedPlaces + FormatPlaces;
        }

        /// <summary>
        /// Размечает рукопись различиями с файлом. Рукопись меняется на месте,
        /// присланный документ после вызова непригоден: вставленные блоки и абзацы
        /// переносятся из него без копирования.
        ///
        /// Непринятых правок ни в одной из сторон быть не должно — их сначала
        /// принимает <see cref="AcceptAll"/>: сравнивается текст, каким он стал.
        /// </summary>
        /// <param name="current">Открытая рукопись.</param>
        /// <param name="revised">Документ из присланного файла.</param>
        /// <param name="info">Автор и время пометок.</param>
        /// <param name="currentImageKey">Отпечаток содержимого картинки рукописи; null — неизвестен.</param>
        /// <param name="revisedImageKey">Отпечаток содержимого картинки файла; null — неизвестен.</param>
        public static Result Apply(
            DocumentModel current,
            DocumentModel revised,
            RevisionInfo info,
            Func<ImageBlock, string?> currentImageKey,
            Func<ImageBlock, string?> revisedImageKey)
        {
            var result = new Result();

            if (current.Sections.Count == 0)
                current.Sections.Add(new SectionModel());

            var context = new Context(current, revised, info, result, currentImageKey, revisedImageKey);

            var currentSection = current.Sections[0];

            // Файл Word приходит одним разделом, но на случай нескольких — их блоки
            // идут подряд, как они стоят в тексте.
            var revisedBlocks = new List<BlockModel>();
            var revisedFloating = new List<BlockModel>();
            foreach (var section in revised.Sections)
            {
                revisedBlocks.AddRange(section.Blocks);
                revisedFloating.AddRange(section.FloatingObjects);
            }

            currentSection.Blocks = context.MergeBlocks(currentSection.Blocks, revisedBlocks);

            // Закреплённые на листе картинки и фигуры: вставленные — с автором файла,
            // убранные — отмеченными удалёнными.
            context.MergeFloating(currentSection.FloatingObjects, revisedFloating);

            context.FinishLists();
            context.FinishComments();

            if (result.Total > 0)
                current.RevisionView = RevisionView.AllMarkup;

            return result;
        }

        /// <summary>
        /// Принимает все правки документа: вставленное становится обычным текстом,
        /// удалённое уходит, сменённое оформление остаётся новым. Картинки с
        /// правками — так же.
        /// </summary>
        public static void AcceptAll(DocumentModel document)
        {
            RevisionService.Resolve(document, _ => new RevisionService.Scope(0, int.MaxValue, true), accept: true);
            RevisionService.ResolveBlocks(document, RevisionService.BlockRevisions(document), accept: true, whole: true);

            // Объекты в строке с отметками на самом объекте (из старых документов).
            foreach (var section in document.Sections)
            {
                var marked = new List<BlockModel>();
                foreach (var block in section.InlineObjects)
                    if (RevisionMarks.HasMark(block)) marked.Add(block);
                if (marked.Count > 0)
                    RevisionService.ResolveBlocks(document, marked, accept: true, whole: true);
            }
        }

        /// <summary>Есть ли в документе правки — для вопроса перед сравнением.</summary>
        public static bool HasRevisions(DocumentModel document) => RevisionService.CountRevisions(document) > 0;

        // ── Сравнение ────────────────────────────────────────────────────────

        private sealed class Context
        {
            private readonly DocumentModel _current;
            private readonly RevisionInfo _info;
            private readonly Result _result;
            private readonly Func<ImageBlock, string?> _currentImageKey;
            private readonly Func<ImageBlock, string?> _revisedImageKey;
            private readonly Func<string, string?> _currentFileKey;
            private readonly Func<string, string?> _revisedFileKey;

            private readonly StyleResolver _currentStyles;
            private readonly StyleResolver _revisedStyles;

            private readonly Dictionary<Guid, BlockModel> _currentInlineObjects = new();
            private readonly Dictionary<Guid, BlockModel> _revisedInlineObjects = new();

            // Списки файла, сопоставленные спискам рукописи по совпавшим абзацам:
            // вставленный пункт продолжает нумерацию своего списка, а не начинает новую.
            private readonly Dictionary<Guid, Guid> _listMap = new();
            private readonly List<ParagraphBlock> _adoptedParagraphs = new();

            // Начертание знака считается по цепочке стилей; у знаков абзаца свойства
            // общие на ран, и память по паре «свойства, стиль абзаца» снимает повтор.
            private readonly Dictionary<(RunProperties?, string), Look> _currentLooks = new(LookKeyComparer.Instance);
            private readonly Dictionary<(RunProperties?, string), Look> _revisedLooks = new(LookKeyComparer.Instance);

            // Примечания файла: по Id файла — само примечание, и Id, под которым оно
            // живёт в рукописи (своё же примечание, вернувшееся из Word, — прежний Id).
            private readonly Dictionary<Guid, CommentModel> _revisedComments = new();
            private readonly Dictionary<Guid, Guid> _commentMap = new();

            // Абзацы файла и абзацы рукописи, которыми они стали: пара или сам
            // перенесённый абзац. По ним плавающий объект ячейки находит свой абзац.
            private readonly Dictionary<ParagraphBlock, ParagraphBlock> _paragraphMap = new(ReferenceEqualityComparer.Instance);

            // Сменённые списки абзацев: решаются в конце, когда списки файла уже
            // сопоставлены спискам рукописи.
            private readonly List<(ParagraphBlock Current, ListProperties? Revised)> _listChanges = new();

            // Сравнение идёт внутри ячейки таблицы: интервалы абзаца по умолчанию там нулевые.
            private bool _inCell;

            private readonly ConditionalWeakTable<BlockModel, Dictionary<string, int>> _wordCounts = new();
            private readonly Dictionary<BlockModel, string> _keys = new(ReferenceEqualityComparer.Instance);

            public Context(
                DocumentModel current,
                DocumentModel revised,
                RevisionInfo info,
                Result result,
                Func<ImageBlock, string?> currentImageKey,
                Func<ImageBlock, string?> revisedImageKey)
            {
                _current = current;
                _info = info;
                _result = result;
                _currentImageKey = currentImageKey;
                _revisedImageKey = revisedImageKey;

                _currentStyles = new StyleResolver(current.Styles);
                _revisedStyles = new StyleResolver(revised.Styles);

                // Отпечаток файла картинки по имени — для заливки фигуры картинкой.
                _currentFileKey = fileName => currentImageKey(new ImageBlock { ImageFileName = fileName });
                _revisedFileKey = fileName => revisedImageKey(new ImageBlock { ImageFileName = fileName });

                foreach (var block in current.Sections[0].InlineObjects)
                    _currentInlineObjects[block.Id] = block;

                foreach (var section in revised.Sections)
                    foreach (var block in section.InlineObjects)
                        _revisedInlineObjects[block.Id] = block;

                foreach (var comment in revised.Comments)
                    _revisedComments[comment.Id] = comment;
            }

            private RevisionInfo NewInfo() => _info.Clone();

            private Look CurrentLook(RunProperties? props, string paragraphStyle)
            {
                var key = (props, paragraphStyle);
                if (!_currentLooks.TryGetValue(key, out var look))
                {
                    look = Look.Of(props, paragraphStyle, _currentStyles);
                    _currentLooks[key] = look;
                }
                return look;
            }

            private Look RevisedLook(RunProperties? props, string paragraphStyle)
            {
                var key = (props, paragraphStyle);
                if (!_revisedLooks.TryGetValue(key, out var look))
                {
                    look = Look.Of(props, paragraphStyle, _revisedStyles);
                    _revisedLooks[key] = look;
                }
                return look;
            }

            // ── Блоки ────────────────────────────────────────────────────────

            /// <summary>Шаг выравнивания места замены.</summary>
            private readonly struct Step
            {
                public Step(BlockModel? current, BlockModel? revised)
                {
                    Current = current;
                    Revised = revised;
                }

                /// <summary>Блок рукописи; null — блок есть только в файле.</summary>
                public BlockModel? Current { get; }

                /// <summary>Блок файла; null — блок есть только в рукописи.</summary>
                public BlockModel? Revised { get; }
            }

            /// <summary>
            /// Новый список блоков рукописи: совпавшие и похожие блоки — на своих
            /// местах и сверенные, удалённые в файле — размечены удалёнными, новые из
            /// файла — вставлены и размечены вставкой.
            /// </summary>
            public List<BlockModel> MergeBlocks(List<BlockModel> currentBlocks, List<BlockModel> revisedBlocks)
            {
                var dictionary = new Dictionary<string, int>(StringComparer.Ordinal);
                var a = new int[revisedBlocks.Count];
                var b = new int[currentBlocks.Count];

                for (int i = 0; i < a.Length; i++) a[i] = KeyIndex(BlockKey(revisedBlocks[i], revised: true), dictionary);
                for (int i = 0; i < b.Length; i++) b[i] = KeyIndex(BlockKey(currentBlocks[i], revised: false), dictionary);

                var merged = new List<BlockModel>(currentBlocks.Count + 8);
                var deleted = new List<BlockModel>();
                var inserted = new List<BlockModel>();

                foreach (var op in SequenceDiff.Compute(a, b, MaxEditDistance))
                {
                    switch (op.Kind)
                    {
                        case SequenceDiff.OpKind.Equal:
                            FlushHunk(deleted, inserted, merged);
                            CompareMatchedBlocks(currentBlocks[op.IndexB], revisedBlocks[op.IndexA]);
                            merged.Add(currentBlocks[op.IndexB]);
                            break;

                        case SequenceDiff.OpKind.OnlyA:
                            inserted.Add(revisedBlocks[op.IndexA]);
                            break;

                        case SequenceDiff.OpKind.OnlyB:
                            deleted.Add(currentBlocks[op.IndexB]);
                            break;
                    }
                }

                FlushHunk(deleted, inserted, merged);
                return merged;
            }

            /// <summary>
            /// Место замены: блоки рукописи, которых нет в файле, и блоки файла,
            /// которых нет в рукописи. Похожие пары сверяются, остальное размечается.
            /// </summary>
            private void FlushHunk(List<BlockModel> deleted, List<BlockModel> inserted, List<BlockModel> merged)
            {
                if (deleted.Count == 0 && inserted.Count == 0) return;

                foreach (var step in Align(deleted, inserted))
                {
                    if (step.Current is { } currentBlock && step.Revised is { } revisedBlock)
                    {
                        CompareMatchedBlocks(currentBlock, revisedBlock);
                        merged.Add(currentBlock);
                    }
                    else if (step.Current is { } onlyCurrent)
                    {
                        if (!IsLayoutFiller(onlyCurrent, merged))
                            MarkBlockDeleted(onlyCurrent);
                        merged.Add(onlyCurrent);
                    }
                    else if (step.Revised is { } onlyRevised)
                    {
                        if (IsLayoutFiller(onlyRevised, merged)) continue;
                        if (MarkBlockInserted(onlyRevised))
                            merged.Add(onlyRevised);
                    }
                }

                deleted.Clear();
                inserted.Clear();
            }

            /// <summary>
            /// Пустой абзац сразу за таблицей или разрывом. Word держит такой абзац
            /// там, где его нет у нас (после таблицы, на месте разрыва внутри абзаца),
            /// и прохождение рукописи через Word добавляет или убирает его без всякой
            /// правки. Он не размечается: в нём нечего принимать или отклонять.
            /// </summary>
            private static bool IsLayoutFiller(BlockModel block, List<BlockModel> merged)
            {
                if (block is not ParagraphBlock paragraph) return false;
                if (paragraph.TotalLength != 0) return false;
                if (merged.Count == 0) return false;
                return merged[^1] is TableBlock or BreakBlock;
            }

            /// <summary>
            /// Выравнивание места замены: какие блоки рукописи и файла — один и тот же
            /// изменённый блок. Порядок обеих сторон сохраняется; в месте, где подряд
            /// идут удалённое и вставленное, удалённое — раньше, как у Word.
            /// </summary>
            private List<Step> Align(List<BlockModel> deleted, List<BlockModel> inserted)
            {
                int n = deleted.Count;
                int m = inserted.Count;
                var steps = new List<Step>(n + m);

                if (n == 0 || m == 0)
                {
                    foreach (var block in deleted) steps.Add(new Step(block, null));
                    foreach (var block in inserted) steps.Add(new Step(null, block));
                    return steps;
                }

                if ((long)n * m > AlignmentCellLimit)
                    return AlignGreedy(deleted, inserted);

                // Наибольшая сумма похожести пар при сохранённом порядке — как общая
                // подпоследовательность, только у каждой пары свой вес.
                var similarity = new double[n, m];
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < m; j++)
                        similarity[i, j] = PairSimilarity(deleted[i], inserted[j]);

                var score = new double[n + 1, m + 1];
                for (int i = n - 1; i >= 0; i--)
                {
                    for (int j = m - 1; j >= 0; j--)
                    {
                        double best = Math.Max(score[i + 1, j], score[i, j + 1]);
                        if (similarity[i, j] > 0)
                            best = Math.Max(best, score[i + 1, j + 1] + similarity[i, j]);
                        score[i, j] = best;
                    }
                }

                var pendingInserted = new List<BlockModel>();
                int x = 0, y = 0;

                void FlushInserted()
                {
                    foreach (var block in pendingInserted) steps.Add(new Step(null, block));
                    pendingInserted.Clear();
                }

                while (x < n && y < m)
                {
                    if (similarity[x, y] > 0
                        && Math.Abs(score[x, y] - (score[x + 1, y + 1] + similarity[x, y])) < 1e-9)
                    {
                        FlushInserted();
                        steps.Add(new Step(deleted[x], inserted[y]));
                        x++;
                        y++;
                    }
                    else if (Math.Abs(score[x, y] - score[x + 1, y]) < 1e-9)
                    {
                        // Удалённое ставится сразу, вставленное копится до следующей
                        // пары: так удалённое в месте замены идёт раньше вставленного.
                        steps.Add(new Step(deleted[x], null));
                        x++;
                    }
                    else
                    {
                        pendingInserted.Add(inserted[y]);
                        y++;
                    }
                }

                while (x < n) steps.Add(new Step(deleted[x++], null));
                FlushInserted();
                while (y < m) steps.Add(new Step(null, inserted[y++]));

                return steps;
            }

            /// <summary>
            /// Выравнивание большого места замены: проход по обеим сторонам с окном.
            /// Пара — если блоки похожи; иначе ищется ближайший похожий в окне.
            /// </summary>
            private List<Step> AlignGreedy(List<BlockModel> deleted, List<BlockModel> inserted)
            {
                var steps = new List<Step>(deleted.Count + inserted.Count);
                int p = 0, q = 0;

                while (p < deleted.Count && q < inserted.Count)
                {
                    if (PairSimilarity(deleted[p], inserted[q]) > 0)
                    {
                        steps.Add(new Step(deleted[p++], inserted[q++]));
                        continue;
                    }

                    int skipDeleted = -1;
                    for (int k = 1; k <= GreedyWindow && p + k < deleted.Count; k++)
                    {
                        if (PairSimilarity(deleted[p + k], inserted[q]) > 0) { skipDeleted = k; break; }
                    }

                    if (skipDeleted > 0)
                    {
                        for (int k = 0; k < skipDeleted; k++) steps.Add(new Step(deleted[p++], null));
                        continue;
                    }

                    int skipInserted = -1;
                    for (int k = 1; k <= GreedyWindow && q + k < inserted.Count; k++)
                    {
                        if (PairSimilarity(deleted[p], inserted[q + k]) > 0) { skipInserted = k; break; }
                    }

                    if (skipInserted > 0)
                    {
                        for (int k = 0; k < skipInserted; k++) steps.Add(new Step(null, inserted[q++]));
                        continue;
                    }

                    steps.Add(new Step(deleted[p++], null));
                }

                while (p < deleted.Count) steps.Add(new Step(deleted[p++], null));
                while (q < inserted.Count) steps.Add(new Step(null, inserted[q++]));

                return steps;
            }

            /// <summary>
            /// Похожесть блока рукописи и блока файла для пары: доля общих слов. Ноль —
            /// не пара: блоки разного рода, таблицы разной формы, слов общих мало.
            /// </summary>
            private double PairSimilarity(BlockModel current, BlockModel revised)
            {
                switch (current)
                {
                    case ParagraphBlock when revised is ParagraphBlock:
                    {
                        double value = WordSimilarity(current, revised);
                        return value >= ParagraphPairThreshold ? value : 0;
                    }

                    case TableBlock currentTable when revised is TableBlock revisedTable:
                    {
                        // Таблицы разной формы — пара, только если лишние ячейки любой
                        // стороны пусты: Word дополняет короткие строки пустыми ячейками,
                        // а текст ячейки, которой в рукописи нет, перенести некуда. Либо
                        // форма разнится одними строками: строки сводятся по тексту,
                        // лишние — вставлены или удалены правкой строки.
                        if (!UnmatchedCellsEmpty(currentTable, revisedTable)
                            && !RowsAlignable(currentTable, revisedTable))
                            return 0;

                        double value = WordSimilarity(current, revised);
                        return value >= TablePairThreshold ? value : 0;
                    }

                    default:
                        return 0;
                }
            }

            private static bool UnmatchedCellsEmpty(TableBlock currentTable, TableBlock revisedTable)
            {
                var currentPlaces = new HashSet<(int, int)>();
                foreach (var cell in currentTable.Cells) currentPlaces.Add((cell.Row, cell.Column));

                var revisedPlaces = new HashSet<(int, int)>();
                foreach (var cell in revisedTable.Cells) revisedPlaces.Add((cell.Row, cell.Column));

                foreach (var cell in currentTable.Cells)
                    if (!revisedPlaces.Contains((cell.Row, cell.Column)) && !CellIsEmpty(cell)) return false;

                foreach (var cell in revisedTable.Cells)
                    if (!currentPlaces.Contains((cell.Row, cell.Column)) && !CellIsEmpty(cell)) return false;

                return true;
            }

            private static bool CellIsEmpty(TableCell cell)
            {
                foreach (var paragraph in cell.ParagraphsDeep())
                    if (paragraph.TotalLength != 0) return false;
                return true;
            }

            /// <summary>Коэффициент Дайса по словам двух блоков. Два пустых блока похожи вполне.</summary>
            private double WordSimilarity(BlockModel a, BlockModel b)
            {
                var wordsA = WordCounts(a);
                var wordsB = WordCounts(b);

                int totalA = 0, totalB = 0, common = 0;
                foreach (var count in wordsA.Values) totalA += count;
                foreach (var count in wordsB.Values) totalB += count;

                if (totalA == 0 && totalB == 0) return 1;
                if (totalA == 0 || totalB == 0) return 0;

                foreach (var pair in wordsA)
                    if (wordsB.TryGetValue(pair.Key, out int other))
                        common += Math.Min(pair.Value, other);

                return 2.0 * common / (totalA + totalB);
            }

            private Dictionary<string, int> WordCounts(BlockModel block)
            {
                if (_wordCounts.TryGetValue(block, out var cached)) return cached;

                var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                var sb = new StringBuilder();

                void Flush()
                {
                    if (sb.Length == 0) return;
                    string word = sb.ToString();
                    counts[word] = counts.TryGetValue(word, out int c) ? c + 1 : 1;
                    sb.Clear();
                }

                void Scan(string text)
                {
                    foreach (char ch in text)
                    {
                        if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
                        else Flush();
                    }
                    Flush();
                }

                switch (block)
                {
                    case ParagraphBlock paragraph:
                        Scan(paragraph.GetPlainText());
                        break;

                    case TableBlock table:
                        foreach (var cell in table.Cells)
                            foreach (var paragraph in cell.ParagraphsDeep())
                                Scan(paragraph.GetPlainText());
                        break;
                }

                _wordCounts.Add(block, counts);
                return counts;
            }

            /// <summary>
            /// Ключ блока для разности: что в нём лежит, а не его Id. Абзац — его
            /// текст, таблица — форма и текст ячеек, картинка — отпечаток байтов.
            /// </summary>
            private string BlockKey(BlockModel block, bool revised)
            {
                if (_keys.TryGetValue(block, out var key)) return key;

                key = block switch
                {
                    ParagraphBlock paragraph => "P\u0001" + ParagraphKeyText(paragraph, revised),
                    TableBlock table => "T\u0001" + TableKeyText(table, revised),
                    ImageBlock image => "I\u0001" + (ImageKey(image, revised) ?? image.Id.ToString("N")),
                    ShapeBlock shape => "S\u0001" + ShapeKey(shape, revised),
                    BreakBlock breakBlock => "B\u0001" + breakBlock.BreakType + (breakBlock.IsSectionBreak ? "s" : string.Empty),
                    _ => "O\u0001" + block.BlockType
                };

                _keys[block] = key;
                return key;
            }

            private string ParagraphKeyText(ParagraphBlock paragraph, bool revised)
            {
                var sb = new StringBuilder(paragraph.TotalLength + 8);

                foreach (var chunk in paragraph.Chunks)
                {
                    foreach (var run in chunk.Runs)
                    {
                        if (run.InlineImageId is Guid objectId)
                        {
                            sb.Append(ObjectToken(objectId, revised));
                            continue;
                        }

                        sb.Append(run.Text);
                    }
                }

                return sb.ToString();
            }

            private string TableKeyText(TableBlock table, bool revised)
            {
                var cells = new List<TableCell>(table.Cells);
                cells.Sort((x, y) => x.Row != y.Row ? x.Row.CompareTo(y.Row) : x.Column.CompareTo(y.Column));

                var sb = new StringBuilder();
                sb.Append(table.RowCount).Append('x').Append(table.ColumnCount);

                foreach (var cell in cells)
                {
                    sb.Append('\u0002');
                    foreach (var paragraph in cell.ParagraphsDeep())
                        sb.Append(ParagraphKeyText(paragraph, revised)).Append('\u0003');
                }

                return sb.ToString();
            }

            private string? ImageKey(ImageBlock image, bool revised)
                => revised ? _revisedImageKey(image) : _currentImageKey(image);

            /// <summary>
            /// Фигура по тому, что в ней есть: вид, заливка, обводка и надпись. Размер в
            /// ключ не входит: та же фигура другого размера — та же фигура со сменой
            /// вида, а не удалённая и вставленная.
            /// </summary>
            private string ShapeKey(ShapeBlock shape, bool revised)
            {
                string fill = shape.FillImageFileName is { Length: > 0 } fillFile
                    ? (revised ? _revisedFileKey(fillFile) : _currentFileKey(fillFile)) ?? fillFile
                    : shape.FillColor ?? string.Empty;

                return shape.ShapeType + "|" + fill + "|" + (shape.StrokeColor ?? string.Empty) + "|" + (shape.InnerText ?? string.Empty);
            }

            private string? FileKeyOf(string fileName, bool revised)
                => revised ? _revisedFileKey(fileName) : _currentFileKey(fileName);

            /// <summary>Ключ плавающего объекта листа или ячейки: картинка — по байтам, фигура — по виду.</summary>
            private string FloatKey(BlockModel block, bool revised) => block switch
            {
                ImageBlock image => "I" + (ImageKey(image, revised) ?? image.Id.ToString("N")),
                ShapeBlock shape => "S" + ShapeKey(shape, revised),
                FloatingTextBlock textBox => "X" + string.Join("\n", textBox.Paragraphs.ConvertAll(p => p.GetPlainText())),
                _ => "O" + block.BlockType + block.Id.ToString("N")
            };

            /// <summary>
            /// Объект в строке как «слово»: картинка — отпечатком своих байтов, чтобы
            /// одна и та же картинка рукописи и файла совпала, хоть Id у неё разные.
            /// </summary>
            private string ObjectToken(Guid objectId, bool revised)
            {
                var objects = revised ? _revisedInlineObjects : _currentInlineObjects;
                if (!objects.TryGetValue(objectId, out var block))
                    return "\uFFFC" + objectId.ToString("N");

                string? key = block switch
                {
                    ImageBlock image => ImageKey(image, revised) is { } imageKey ? "I" + imageKey : null,
                    FormulaBlock formula => "F" + formula.Omml,
                    FieldBlock field => "D" + field.Instruction.Trim(),
                    // Сноска — «словом» по своему виду: правленая сноска остаётся той же
                    // сноской, а её текст сверяется по словам отдельно (CompareNotes).
                    NoteBlock note => "N" + (int)note.Kind,
                    ShapeBlock shape => "S" + ShapeKey(shape, revised),
                    _ => null
                };

                return "\uFFFC" + (key ?? objectId.ToString("N"));
            }

            private static int KeyIndex(string key, Dictionary<string, int> dictionary)
            {
                if (!dictionary.TryGetValue(key, out int index))
                {
                    index = dictionary.Count;
                    dictionary[key] = index;
                }
                return index;
            }

            // ── Пары и разметка блоков ───────────────────────────────────────

            private void CompareMatchedBlocks(BlockModel currentBlock, BlockModel revisedBlock)
            {
                switch (currentBlock)
                {
                    case ParagraphBlock currentParagraph when revisedBlock is ParagraphBlock revisedParagraph:
                        CompareParagraphs(currentParagraph, revisedParagraph);
                        break;

                    case TableBlock currentTable when revisedBlock is TableBlock revisedTable:
                        CompareTables(currentTable, revisedTable);
                        break;

                    case IAppearanceTracked:
                        CompareObjectAppearance(currentBlock, revisedBlock);
                        break;
                }
            }

            /// <summary>
            /// Картинка или фигура, которая есть и в рукописи, и в файле: если в файле
            /// её сдвинули, изменили размер, повернули, обрезали или перекрасили, вид
            /// файла переходит на объект рукописи сменой вида с прежним видом рукописи —
            /// отклонение вернёт объект как был.
            /// </summary>
            private void CompareObjectAppearance(BlockModel current, BlockModel revised)
            {
                if (ReferenceEquals(current, revised)) return;
                if (current is not IAppearanceTracked tracked || revised is not IAppearanceTracked) return;
                if (tracked.Deleted is not null) return;

                bool look = ObjectAppearance.DiffersVisibly(current, revised);
                bool place = ObjectAppearance.PlaceDiffers(current, revised);
                if (!look && !place) return;

                string previous = ObjectAppearance.RejectView(current);
                ObjectAppearance.CopyVisible(revised, current, withPlace: place);

                if (string.Equals(previous, ObjectAppearance.Capture(current), StringComparison.Ordinal))
                {
                    tracked.Change = null;
                    return;
                }

                tracked.Change = new ObjectChange { Info = NewInfo(), Previous = previous };
                _result.FormatPlaces++;
            }

            /// <summary>Объект в строке, совпавший «словом»: сверяется его вид.</summary>
            private void CompareInlineObjectAppearance(Guid currentId, Guid revisedId)
            {
                if (!_currentInlineObjects.TryGetValue(currentId, out var currentBlock)) return;
                if (!_revisedInlineObjects.TryGetValue(revisedId, out var revisedBlock)) return;
                CompareObjectAppearance(currentBlock, revisedBlock);
            }

            private void MarkBlockDeleted(BlockModel block)
            {
                switch (block)
                {
                    case ParagraphBlock paragraph:
                        MarkParagraphDeleted(paragraph);
                        break;

                    case TableBlock table:
                        foreach (var cell in table.Cells)
                            foreach (var paragraph in cell.ParagraphsDeep())
                                MarkParagraphDeleted(paragraph);

                        // Картинки и фигуры в ячейках уходят вместе с таблицей.
                        foreach (var (_, cellFloat) in RevisionService.CellFloats(table))
                            if (cellFloat.Object is IRevisionMarked cellMarked)
                                MarkWholeDeleted(cellMarked);
                        break;

                    // Картинка, фигура, разрыв — блок целиком, с отметкой удаления.
                    case IRevisionMarked marked:
                        MarkWholeDeleted(marked);
                        break;

                    default:
                        // Блоков другого рода в потоке Word не бывает (надпись приходит
                        // фигурой): различие считается пропущенным.
                        _result.SkippedPlaces++;
                        break;
                }
            }

            private void MarkWholeDeleted(IRevisionMarked marked)
            {
                marked.Inserted = null;
                marked.Deleted = NewInfo();
                _result.DeletedPlaces++;
            }

            private void MarkWholeInserted(BlockModel block)
            {
                if (block is not IRevisionMarked marked) return;

                marked.Deleted = null;
                marked.Inserted = NewInfo();
                AdoptObjectFiles(block);
                _result.InsertedPlaces++;
            }

            /// <summary>Файлы объекта из присланного файла, которые должны попасть в проект.</summary>
            private void AdoptObjectFiles(BlockModel block)
            {
                switch (block)
                {
                    case ImageBlock image:
                        AdoptImageFiles(image);
                        break;

                    case ShapeBlock shape when !string.IsNullOrWhiteSpace(shape.FillImageFileName):
                        _result.AdoptedImageFiles.Add(shape.FillImageFileName!);
                        break;
                }
            }

            /// <summary>
            /// Размечает вставкой блок файла. False — такой блок в рукопись не
            /// переносится: вставка без пометки была бы той самой молчаливой правкой.
            /// </summary>
            private bool MarkBlockInserted(BlockModel block)
            {
                switch (block)
                {
                    case ParagraphBlock paragraph:
                        AdoptParagraph(paragraph);
                        return true;

                    case TableBlock table:
                        foreach (var cell in table.Cells)
                            foreach (var paragraph in cell.ParagraphsDeep())
                                AdoptParagraph(paragraph);

                        // Картинки и фигуры в ячейках входят вместе с таблицей.
                        foreach (var (_, cellFloat) in RevisionService.CellFloats(table))
                            MarkWholeInserted(cellFloat.Object);
                        return true;

                    // Картинка, фигура, разрыв — блок целиком, с отметкой вставки.
                    case IRevisionMarked:
                        MarkWholeInserted(block);
                        return true;

                    default:
                        _result.SkippedPlaces++;
                        return false;
                }
            }

            // ── Плавающие объекты ────────────────────────────────────────────

            /// <summary>
            /// Плавающие объекты листа или ячейки: одинаковые (по байтам картинки, по
            /// виду фигуры) остаются как есть, объект только рукописи отмечается
            /// удалённым, только файла — входит в рукопись вставкой.
            /// </summary>
            public void MergeFloating(List<BlockModel> currentObjects, List<BlockModel> revisedObjects)
            {
                var unmatched = new Dictionary<string, List<BlockModel>>(StringComparer.Ordinal);
                foreach (var block in currentObjects)
                {
                    string key = FloatKey(block, revised: false);
                    if (!unmatched.TryGetValue(key, out var list)) unmatched[key] = list = new List<BlockModel>();
                    list.Add(block);
                }

                var added = new List<BlockModel>();
                foreach (var block in revisedObjects)
                {
                    string key = FloatKey(block, revised: true);
                    if (unmatched.TryGetValue(key, out var list) && list.Count > 0)
                    {
                        // Тот же объект: сдвинут, изменён размер, повёрнут — смена вида.
                        CompareObjectAppearance(list[0], block);
                        list.RemoveAt(0);
                        continue;
                    }

                    if (block is IRevisionMarked)
                    {
                        MarkWholeInserted(block);
                        added.Add(block);
                    }
                    else
                    {
                        _result.SkippedPlaces++;
                    }
                }

                foreach (var list in unmatched.Values)
                {
                    foreach (var block in list)
                    {
                        if (block is IRevisionMarked marked) MarkWholeDeleted(marked);
                        else _result.SkippedPlaces++;
                    }
                }

                currentObjects.AddRange(added);
            }

            /// <summary>
            /// Плавающие объекты сопоставленной ячейки. Новый объект из файла встаёт у
            /// абзаца рукописи, которым стал его абзац в файле.
            /// </summary>
            private void MergeCellFloats(TableCell currentCell, TableCell revisedCell)
            {
                var currentObjects = new List<BlockModel>();
                if (currentCell.Floats is { } currentFloats)
                    foreach (var cellFloat in currentFloats)
                        currentObjects.Add(cellFloat.Object);

                var revisedObjects = new List<BlockModel>();
                var revisedAnchors = new Dictionary<BlockModel, ParagraphBlock?>(ReferenceEqualityComparer.Instance);
                if (revisedCell.Floats is { } revisedFloats)
                {
                    foreach (var cellFloat in revisedFloats)
                    {
                        revisedObjects.Add(cellFloat.Object);
                        int index = revisedCell.FloatParagraphIndex(cellFloat);
                        revisedAnchors[cellFloat.Object] = index >= 0 && index < revisedCell.Paragraphs.Count
                            ? revisedCell.Paragraphs[index]
                            : null;
                    }
                }

                if (currentObjects.Count == 0 && revisedObjects.Count == 0) return;

                int before = currentObjects.Count;
                MergeFloating(currentObjects, revisedObjects);

                for (int i = before; i < currentObjects.Count; i++)
                {
                    var block = currentObjects[i];
                    var anchor = revisedAnchors.TryGetValue(block, out var revisedAnchor) && revisedAnchor is not null
                        && _paragraphMap.TryGetValue(revisedAnchor, out var mapped)
                        ? mapped
                        : null;

                    int anchorIndex = anchor is null ? 0 : Math.Max(0, currentCell.Paragraphs.IndexOf(anchor));
                    (currentCell.Floats ??= new List<CellFloat>()).Add(new CellFloat
                    {
                        AnchorParagraphId = anchor?.Id ?? (currentCell.Paragraphs.Count > 0 ? currentCell.Paragraphs[0].Id : Guid.Empty),
                        AnchorParagraphIndex = anchorIndex,
                        Object = block
                    });
                }
            }

            // ── Таблицы ──────────────────────────────────────────────────────

            /// <summary>
            /// Таблицы одной формы: ячейки сверяются на своих местах (строка, столбец),
            /// абзацы ячейки — теми же правилами, что блоки документа.
            /// </summary>
            private void CompareTables(TableBlock currentTable, TableBlock revisedTable)
            {
                // Столбцы те же и строки не объединены: строки сводятся по тексту —
                // вставленная или удалённая строка видна строкой, а не сдвигом текста
                // по ячейкам всех строк ниже.
                if (RowsAlignable(currentTable, revisedTable))
                {
                    CompareTableRows(currentTable, revisedTable);
                    CompareTableLook(currentTable, revisedTable);
                    return;
                }

                CompareTableCells(currentTable, revisedTable);
                CompareTableLook(currentTable, revisedTable);
            }

            /// <summary>
            /// Таблицы с теми же столбцами и без объединения строк: их строки можно
            /// свести по тексту.
            /// </summary>
            private static bool RowsAlignable(TableBlock currentTable, TableBlock revisedTable)
            {
                if (currentTable.ColumnCount != revisedTable.ColumnCount) return false;
                foreach (var cell in currentTable.Cells) if (cell.RowSpan > 1) return false;
                foreach (var cell in revisedTable.Cells) if (cell.RowSpan > 1) return false;
                return true;
            }

            /// <summary>Текст строки таблицы — ключ строки при сведении.</summary>
            private static string RowKey(TableBlock table, int row)
            {
                var cells = new List<TableCell>();
                foreach (var cell in table.Cells) if (cell.Row == row) cells.Add(cell);
                cells.Sort((a, b) => a.Column.CompareTo(b.Column));

                var sb = new StringBuilder();
                foreach (var cell in cells)
                {
                    sb.Append('\u0002');
                    foreach (var paragraph in cell.ParagraphsDeep())
                        sb.Append(paragraph.GetPlainText()).Append('\u0003');
                }
                return sb.ToString();
            }

            /// <summary>Доля общих слов двух строк таблицы.</summary>
            private static double RowSimilarity(string a, string b)
            {
                var wordsA = new HashSet<string>(a.Split(RowSeparators, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
                var wordsB = new HashSet<string>(b.Split(RowSeparators, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
                if (wordsA.Count == 0 && wordsB.Count == 0) return 1;
                int common = 0;
                foreach (var word in wordsA) if (wordsB.Contains(word)) common++;
                int union = wordsA.Count + wordsB.Count - common;
                return union == 0 ? 0 : (double)common / union;
            }

            private static readonly char[] RowSeparators = { ' ', '\t', '\u0002', '\u0003', '\n', ',', '.', ';', ':' };

            /// <summary>
            /// Строки таблиц с теми же столбцами сводятся по тексту: одинаковые и похожие —
            /// пары, ячейки которых сверяются на своих местах; строка только рукописи
            /// остаётся удалённой правкой строки, строка только файла входит вставленной.
            /// </summary>
            private void CompareTableRows(TableBlock currentTable, TableBlock revisedTable)
            {
                int currentRows = currentTable.RowCount;
                int revisedRows = revisedTable.RowCount;

                var keys = new Dictionary<string, int>(StringComparer.Ordinal);
                var currentKeys = new string[currentRows];
                var revisedKeys = new string[revisedRows];
                var a = new int[revisedRows];
                var b = new int[currentRows];
                for (int r = 0; r < revisedRows; r++) { revisedKeys[r] = RowKey(revisedTable, r); a[r] = KeyIndex(revisedKeys[r], keys); }
                for (int r = 0; r < currentRows; r++) { currentKeys[r] = RowKey(currentTable, r); b[r] = KeyIndex(currentKeys[r], keys); }

                // План: пары (строка рукописи, строка файла); в месте замены похожие
                // строки идут парами по порядку.
                var plan = new List<(int Current, int Revised)>();
                var hunkCurrent = new List<int>();
                var hunkRevised = new List<int>();

                void FlushRows()
                {
                    int i = 0, j = 0;
                    while (i < hunkCurrent.Count || j < hunkRevised.Count)
                    {
                        if (i < hunkCurrent.Count && j < hunkRevised.Count
                            && RowSimilarity(currentKeys[hunkCurrent[i]], revisedKeys[hunkRevised[j]]) >= TablePairThreshold)
                        {
                            plan.Add((hunkCurrent[i], hunkRevised[j]));
                            i++; j++;
                            continue;
                        }

                        // Удалённое — раньше вставленного, как у Word.
                        if (i < hunkCurrent.Count) plan.Add((hunkCurrent[i++], -1));
                        else plan.Add((-1, hunkRevised[j++]));
                    }

                    hunkCurrent.Clear();
                    hunkRevised.Clear();
                }

                foreach (var op in SequenceDiff.Compute(a, b, MaxEditDistance))
                {
                    switch (op.Kind)
                    {
                        case SequenceDiff.OpKind.Equal:
                            FlushRows();
                            plan.Add((op.IndexB, op.IndexA));
                            break;
                        case SequenceDiff.OpKind.OnlyA:
                            hunkRevised.Add(op.IndexA);
                            break;
                        case SequenceDiff.OpKind.OnlyB:
                            hunkCurrent.Add(op.IndexB);
                            break;
                    }
                }
                FlushRows();

                var currentLines = new List<TableCell>[currentRows];
                for (int r = 0; r < currentRows; r++) currentLines[r] = new List<TableCell>();
                foreach (var cell in currentTable.Cells)
                    if (cell.Row >= 0 && cell.Row < currentRows) currentLines[cell.Row].Add(cell);

                var revisedLines = new List<TableCell>[revisedRows];
                for (int r = 0; r < revisedRows; r++) revisedLines[r] = new List<TableCell>();
                foreach (var cell in revisedTable.Cells)
                    if (cell.Row >= 0 && cell.Row < revisedRows) revisedLines[cell.Row].Add(cell);

                var cells = new List<TableCell>(currentTable.Cells.Count + 8);
                var heights = new List<double>();
                var exact = new List<int>();
                int index = 0;

                foreach (var (currentRow, revisedRow) in plan)
                {
                    if (currentRow >= 0)
                    {
                        heights.Add(currentTable.GetRowMinHeightPt(currentRow));
                        if (currentTable.IsRowHeightExact(currentRow)) exact.Add(index);

                        var revisedByColumn = new Dictionary<int, TableCell>();
                        if (revisedRow >= 0)
                            foreach (var cell in revisedLines[revisedRow]) revisedByColumn[cell.Column] = cell;

                        bool deletedRow = revisedRow < 0;
                        foreach (var cell in currentLines[currentRow])
                        {
                            cell.Row = index;
                            cells.Add(cell);

                            if (deletedRow)
                            {
                                cell.Inserted = null;
                                cell.Deleted = NewInfo();
                                foreach (var paragraph in cell.ParagraphsDeep())
                                    MarkParagraphDeleted(paragraph);
                                if (cell.Floats is { } deletedFloats)
                                    foreach (var cellFloat in deletedFloats)
                                        if (cellFloat.Object is IRevisionMarked floatMarked) MarkWholeDeleted(floatMarked);
                                continue;
                            }

                            if (revisedByColumn.TryGetValue(cell.Column, out var revisedCell))
                                CompareCellPair(cell, revisedCell);
                            else if (!CellIsEmpty(cell))
                                foreach (var paragraph in cell.ParagraphsDeep())
                                    MarkParagraphDeleted(paragraph);
                        }

                        if (deletedRow) _result.DeletedPlaces++;
                    }
                    else
                    {
                        heights.Add(revisedTable.GetRowMinHeightPt(revisedRow));
                        if (revisedTable.IsRowHeightExact(revisedRow)) exact.Add(index);

                        foreach (var cell in revisedLines[revisedRow])
                        {
                            cell.Row = index;
                            cell.Deleted = null;
                            cell.Inserted = NewInfo();
                            foreach (var paragraph in cell.ParagraphsDeep())
                                AdoptParagraph(paragraph);
                            if (cell.Floats is { } floats)
                                foreach (var cellFloat in floats)
                                    MarkWholeInserted(cellFloat.Object);
                            cells.Add(cell);
                        }

                        _result.InsertedPlaces++;
                    }

                    index++;
                }

                currentTable.Cells.Clear();
                currentTable.Cells.AddRange(cells);
                currentTable.RowCount = index;
                currentTable.RowMinHeightsPt = heights;
                currentTable.ExactHeightRows = exact.Count > 0 ? exact : null;
            }

            /// <summary>
            /// Оформление таблицы файла против оформления рукописи — по местам ячеек:
            /// если в файле сменили границы, заливку, ширины, высоты, поля, оформление
            /// файла переходит на таблицу рукописи сменой оформления с прежним видом
            /// рукописи.
            /// </summary>
            private void CompareTableLook(TableBlock currentTable, TableBlock revisedTable)
            {
                if (currentTable.RowCount <= 0 || currentTable.ColumnCount <= 0) return;

                string current = TableFrame.Capture(currentTable);
                string file = TableFrame.CaptureByPlace(revisedTable, currentTable);
                if (!TableFrame.DiffersFromFile(current, file)) return;

                string previous = TableFrame.RejectView(currentTable);

                // Свойства самой таблицы и оформление ячеек — из файла; имя стиля
                // остаётся рукописи: оформление стиля уже разложено по ячейкам.
                string? styleName = currentTable.StyleName;
                TableFrame.Restore(currentTable, file);
                currentTable.StyleName = styleName;

                if (string.Equals(previous, TableFrame.Capture(currentTable), StringComparison.Ordinal))
                {
                    currentTable.FormatChange = null;
                    return;
                }

                currentTable.FormatChange = new ObjectChange { Info = NewInfo(), Previous = previous };
                _result.FormatPlaces++;
            }

            /// <summary>Таблицы одной формы: ячейки сверяются на своих местах.</summary>
            private void CompareTableCells(TableBlock currentTable, TableBlock revisedTable)
            {
                var revisedCells = new Dictionary<(int Row, int Column), TableCell>();
                foreach (var cell in revisedTable.Cells)
                    revisedCells[(cell.Row, cell.Column)] = cell;

                foreach (var cell in currentTable.Cells)
                {
                    if (!revisedCells.TryGetValue((cell.Row, cell.Column), out var revisedCell))
                    {
                        // Ячейки на этом месте в файле нет (объединены иначе): её текст
                        // в файле не найден. Пустой ячейке размечать нечего.
                        if (!CellIsEmpty(cell))
                            foreach (var paragraph in cell.ParagraphsDeep())
                                MarkParagraphDeleted(paragraph);
                        continue;
                    }

                    CompareCellPair(cell, revisedCell);
                }
            }

            /// <summary>Ячейка рукописи и та же ячейка файла: абзацы и плавающие объекты.</summary>
            private void CompareCellPair(TableCell cell, TableCell revisedCell)
            {
                {
                    bool wasInCell = _inCell;
                    _inCell = true;
                    List<BlockModel> mergedBlocks;
                    try
                    {
                        mergedBlocks = MergeBlocks(
                            new List<BlockModel>(cell.Paragraphs),
                            new List<BlockModel>(revisedCell.Paragraphs));
                    }
                    finally
                    {
                        _inCell = wasInCell;
                    }

                    var paragraphs = new List<ParagraphBlock>(mergedBlocks.Count);
                    foreach (var block in mergedBlocks)
                        if (block is ParagraphBlock paragraph)
                            paragraphs.Add(paragraph);

                    if (paragraphs.Count == 0)
                        paragraphs.Add(new ParagraphBlock());

                    cell.Paragraphs = paragraphs;

                    MergeCellFloats(cell, revisedCell);

                    // Вставленные и удалённые абзацы сдвинули номера абзацев, перед
                    // которыми стоят вложенные таблицы и плавающие объекты ячейки.
                    cell.AnchorNestedTables();
                    cell.AnchorFloats();
                }
            }

            // ── Абзацы ───────────────────────────────────────────────────────

            /// <summary>Абзац файла входит в рукопись вставкой.</summary>
            private void AdoptParagraph(ParagraphBlock paragraph)
            {
                var cells = paragraph.ToCharCells();
                var mapped = new Dictionary<RunProperties, RunProperties>(ReferenceEqualityComparer.Instance);
                RunProperties? fromNull = null;

                for (int i = 0; i < cells.Count; i++)
                {
                    var source = cells[i].Props;
                    RunProperties marked;

                    if (source is null)
                    {
                        marked = fromNull ??= new RunProperties { Inserted = NewInfo() };
                    }
                    else if (!mapped.TryGetValue(source, out marked!))
                    {
                        marked = source.WithoutRevisions();
                        marked.CommentIds = MapComments(source.CommentIds);
                        marked.Inserted = NewInfo();
                        mapped[source] = marked;
                    }

                    if (cells[i].InlineImageId is Guid objectId)
                        AdoptInlineObject(objectId);

                    cells[i] = new ParagraphBlock.CharCell(cells[i].Ch, marked, cells[i].InlineImageId);
                }

                paragraph.RebuildFromCharCells(cells);

                paragraph.Properties.MarkDeleted = null;
                paragraph.Properties.FormatChange = null;
                paragraph.Properties.MarkInserted = NewInfo();

                // Оглавление файла — просто текст: к оглавлению рукописи оно не
                // относится и обновлять его нечем.
                paragraph.Properties.TocOwnerId = null;
                paragraph.Properties.TocTargetBlockId = null;

                _adoptedParagraphs.Add(paragraph);
                _paragraphMap[paragraph] = paragraph;
                _result.InsertedPlaces++;
            }

            private void MarkParagraphDeleted(ParagraphBlock paragraph)
            {
                var cells = paragraph.ToCharCells();
                var mapped = new Dictionary<RunProperties, RunProperties>(ReferenceEqualityComparer.Instance);
                RunProperties? fromNull = null;

                for (int i = 0; i < cells.Count; i++)
                {
                    var source = cells[i].Props;
                    RunProperties marked;

                    if (source is null)
                    {
                        marked = fromNull ??= new RunProperties { Deleted = NewInfo() };
                    }
                    else if (!mapped.TryGetValue(source, out marked!))
                    {
                        marked = source.WithoutRevisions();
                        marked.Deleted = NewInfo();
                        mapped[source] = marked;
                    }

                    cells[i] = new ParagraphBlock.CharCell(cells[i].Ch, marked, cells[i].InlineImageId);
                }

                paragraph.RebuildFromCharCells(cells);

                paragraph.Properties.MarkInserted = null;
                paragraph.Properties.MarkDeleted = NewInfo();

                _result.DeletedPlaces++;
            }

            /// <summary>
            /// Абзац рукописи и его изменённый вариант из файла: разность по словам.
            /// Совпавшие слова сверяются ещё и по оформлению.
            /// </summary>
            private void CompareParagraphs(ParagraphBlock current, ParagraphBlock revised)
            {
                _paragraphMap[revised] = current;
                CompareList(current, revised);
                CompareParagraphFormat(current, revised);

                var currentCells = current.ToCharCells();
                var revisedCells = revised.ToCharCells();

                string currentStyle = current.Properties.StyleName ?? StyleResolver.DefaultStyleName;
                string revisedStyle = revised.Properties.StyleName ?? StyleResolver.DefaultStyleName;

                var currentTokens = Tokenize(currentCells, revised: false);
                var revisedTokens = Tokenize(revisedCells, revised: true);

                var dictionary = new Dictionary<string, int>(StringComparer.Ordinal);
                var a = new int[revisedTokens.Count];
                var b = new int[currentTokens.Count];
                for (int i = 0; i < a.Length; i++) a[i] = KeyIndex(revisedTokens[i].Text, dictionary);
                for (int i = 0; i < b.Length; i++) b[i] = KeyIndex(currentTokens[i].Text, dictionary);

                var merged = new List<ParagraphBlock.CharCell>(currentCells.Count + 8);
                bool changed = false;

                var equalMemo = new Dictionary<(RunProperties?, RunProperties?), (RunProperties? Props, bool Format)>(PairComparer.Instance);
                var insertMemo = new Dictionary<(RunProperties?, RunProperties?), RunProperties>(PairComparer.Instance);
                var deleteMemo = new Dictionary<RunProperties, RunProperties>(ReferenceEqualityComparer.Instance);
                RunProperties? deleteFromNull = null;

                // Оформление для вставленного текста берётся у соседнего текста
                // рукописи: вставка не должна выглядеть чужим шрифтом файла.
                RunProperties? lastCurrentProps = null;
                bool haveLastCurrent = false;

                SequenceDiff.OpKind previous = SequenceDiff.OpKind.Equal;
                bool previousFormat = false;

                var ops = SequenceDiff.Compute(a, b, MaxEditDistance);

                for (int o = 0; o < ops.Count; o++)
                {
                    var op = ops[o];

                    switch (op.Kind)
                    {
                        case SequenceDiff.OpKind.Equal:
                        {
                            var revisedToken = revisedTokens[op.IndexA];
                            var currentToken = currentTokens[op.IndexB];
                            bool formatHere = false;

                            for (int k = 0; k < currentToken.Length; k++)
                            {
                                var currentCell = currentCells[currentToken.Start + k];
                                var revisedCell = revisedCells[revisedToken.Start + k];

                                lastCurrentProps = currentCell.Props;
                                haveLastCurrent = true;

                                // Начертание объекта в строке (картинки, формулы) на листе
                                // не видно: сверять его — ловить шум перевода. У сноски
                                // сверяется её текст.
                                if (currentCell.InlineImageId is Guid currentObject)
                                {
                                    if (revisedCell.InlineImageId is Guid revisedObject)
                                    {
                                        CompareNotes(currentObject, revisedObject);
                                        CompareInlineObjectAppearance(currentObject, revisedObject);
                                    }

                                    merged.Add(currentCell);
                                    continue;
                                }

                                var key = (currentCell.Props, revisedCell.Props);
                                if (!equalMemo.TryGetValue(key, out var outcome))
                                {
                                    outcome = EqualCellProps(currentCell.Props, currentStyle, revisedCell.Props, revisedStyle);
                                    equalMemo[key] = outcome;
                                }

                                if (ReferenceEquals(outcome.Props, currentCell.Props))
                                {
                                    merged.Add(currentCell);
                                    continue;
                                }

                                merged.Add(new ParagraphBlock.CharCell(currentCell.Ch, outcome.Props, currentCell.InlineImageId));
                                changed = true;
                                if (outcome.Format) formatHere = true;
                            }

                            if (formatHere && !previousFormat) _result.FormatPlaces++;
                            previousFormat = formatHere;
                            previous = SequenceDiff.OpKind.Equal;
                            break;
                        }

                        case SequenceDiff.OpKind.OnlyA:
                        {
                            var token = revisedTokens[op.IndexA];

                            // Нет соседнего текста рукописи слева — берётся первый справа.
                            var template = haveLastCurrent ? lastCurrentProps : NextCurrentProps(ops, o, currentTokens, currentCells);
                            string templateStyle = currentStyle;

                            for (int k = 0; k < token.Length; k++)
                            {
                                var cell = revisedCells[token.Start + k];

                                var key = (template, cell.Props);
                                if (!insertMemo.TryGetValue(key, out var insertedProps))
                                {
                                    insertedProps = WithInserted(template, templateStyle, cell.Props, revisedStyle);
                                    insertMemo[key] = insertedProps;
                                }

                                if (cell.InlineImageId is Guid objectId)
                                    AdoptInlineObject(objectId);

                                merged.Add(new ParagraphBlock.CharCell(cell.Ch, insertedProps, cell.InlineImageId));
                            }

                            if (previous != SequenceDiff.OpKind.OnlyA) _result.InsertedPlaces++;
                            previous = SequenceDiff.OpKind.OnlyA;
                            previousFormat = false;
                            changed = true;
                            break;
                        }

                        case SequenceDiff.OpKind.OnlyB:
                        {
                            var token = currentTokens[op.IndexB];

                            for (int k = 0; k < token.Length; k++)
                            {
                                var cell = currentCells[token.Start + k];
                                lastCurrentProps = cell.Props;
                                haveLastCurrent = true;

                                RunProperties marked;
                                if (cell.Props is null)
                                {
                                    marked = deleteFromNull ??= new RunProperties { Deleted = NewInfo() };
                                }
                                else if (!deleteMemo.TryGetValue(cell.Props, out marked!))
                                {
                                    marked = cell.Props.WithoutRevisions();
                                    marked.Deleted = NewInfo();
                                    deleteMemo[cell.Props] = marked;
                                }

                                merged.Add(new ParagraphBlock.CharCell(cell.Ch, marked, cell.InlineImageId));
                            }

                            if (previous != SequenceDiff.OpKind.OnlyB) _result.DeletedPlaces++;
                            previous = SequenceDiff.OpKind.OnlyB;
                            previousFormat = false;
                            changed = true;
                            break;
                        }
                    }
                }

                if (changed)
                    current.RebuildFromCharCells(merged);
            }

            /// <summary>Свойства первого знака рукописи после места вставки.</summary>
            private static RunProperties? NextCurrentProps(
                List<SequenceDiff.Op> ops,
                int from,
                List<Token> currentTokens,
                List<ParagraphBlock.CharCell> currentCells)
            {
                for (int o = from + 1; o < ops.Count; o++)
                {
                    if (ops[o].Kind == SequenceDiff.OpKind.OnlyA) continue;
                    var token = currentTokens[ops[o].IndexB];
                    return currentCells[token.Start].Props;
                }

                return currentCells.Count > 0 ? currentCells[^1].Props : null;
            }

            /// <summary>
            /// Оформление абзаца: стиль заголовка (абзац стал заголовком, перестал им
            /// быть или сменил уровень), выравнивание, отступы, интервалы до и после,
            /// межстрочный интервал. Сверяются действующие значения — со стилем, как
            /// абзац стоит на листе: Word пишет их явными, рукопись часто берёт из стиля.
            /// Отличие ложится сменой оформления абзаца с прежним оформлением.
            /// </summary>
            private void CompareParagraphFormat(ParagraphBlock current, ParagraphBlock revised)
            {
                var props = current.Properties;
                var before = props.WithoutRevisions();
                bool changed = false;

                string? currentHeading = HeadingStyle(props.StyleName);
                string? revisedHeading = HeadingStyle(revised.Properties.StyleName);
                if (!string.Equals(currentHeading, revisedHeading, StringComparison.OrdinalIgnoreCase))
                {
                    props.StyleName = revisedHeading is not null
                        ? revised.Properties.StyleName
                        : (revised.Properties.StyleName is { } style && _current.FindStyle(style) is not null ? style : null);
                    props.OutlineLevel = revised.Properties.OutlineLevel;
                    changed = true;
                }

                // Действующие значения — после смены стиля: стиль заголовка несёт свои
                // интервалы, и сверять их надо уже с ним.
                var currentLook = ParaLook.Of(current, _currentStyles, _inCell);
                var revisedLook = ParaLook.Of(revised, _revisedStyles, _inCell);
                bool indentsComparable = current.ListProperties is null && revised.ListProperties is null;

                if (currentLook.Alignment != revisedLook.Alignment)
                {
                    props.Alignment = revisedLook.Alignment;
                    changed = true;
                }

                if (indentsComparable)
                {
                    if (!Near(currentLook.LeftIndent, revisedLook.LeftIndent)) { props.LeftIndent = revisedLook.LeftIndent; changed = true; }
                    if (!Near(currentLook.RightIndent, revisedLook.RightIndent)) { props.RightIndent = revisedLook.RightIndent; changed = true; }
                    if (!Near(currentLook.FirstLineIndent, revisedLook.FirstLineIndent)) { props.FirstLineIndent = revisedLook.FirstLineIndent; changed = true; }
                }

                if (!Near(currentLook.SpaceBefore, revisedLook.SpaceBefore)) { props.SpaceBefore = revisedLook.SpaceBefore; changed = true; }
                if (!Near(currentLook.SpaceAfter, revisedLook.SpaceAfter)) { props.SpaceAfter = revisedLook.SpaceAfter; changed = true; }

                if (currentLook.LineRule != revisedLook.LineRule || Math.Abs(currentLook.LineValue - revisedLook.LineValue) > 0.01)
                {
                    props.LineSpacingRule = revisedLook.LineRule;
                    props.LineSpacingValue = revisedLook.LineValue;
                    changed = true;
                }

                if (!changed) return;

                props.FormatChange = new ParagraphFormatChange
                {
                    Info = NewInfo(),
                    Previous = before
                };

                _result.FormatPlaces++;
            }

            private static bool Near(double a, double b) => Math.Abs(a - b) <= 0.5;

            private static string? HeadingStyle(string? styleName)
            {
                if (string.IsNullOrEmpty(styleName)) return null;
                if (styleName.StartsWith("Heading", StringComparison.OrdinalIgnoreCase)) return styleName;
                if (string.Equals(styleName, "Title", StringComparison.OrdinalIgnoreCase)) return styleName;
                return null;
            }

            /// <summary>
            /// Список абзаца. Одного рода списки (оба маркированные или оба нумерованные,
            /// того же уровня) — тот же список: по ним список файла сопоставляется списку
            /// рукописи. Иначе абзац стал пунктом, перестал им быть или сменил вид
            /// списка — это правка, она решается в конце.
            /// </summary>
            private void CompareList(ParagraphBlock current, ParagraphBlock revised)
            {
                var currentList = current.ListProperties;
                var revisedList = revised.ListProperties;

                if (SameListKind(currentList, revisedList))
                {
                    if (currentList is not null && revisedList is not null && !_listMap.ContainsKey(revisedList.ListId))
                        _listMap[revisedList.ListId] = currentList.ListId;
                    return;
                }

                _listChanges.Add((current, revisedList));
            }

            private static bool SameListKind(ListProperties? a, ListProperties? b)
            {
                if (a is null || b is null) return a is null && b is null;
                return a.Level == b.Level && a.IsNumbered == b.IsNumbered && a.MarkerType == b.MarkerType;
            }

            /// <summary>
            /// Вставленные пункты списков переходят в списки рукописи, с которыми их
            /// списки файла совпали по соседним пунктам. Сменённые списки абзацев
            /// ложатся правкой: прежний список запоминается и вернётся отклонением.
            /// </summary>
            public void FinishLists()
            {
                foreach (var paragraph in _adoptedParagraphs)
                {
                    if (paragraph.ListProperties is { } list && _listMap.TryGetValue(list.ListId, out var currentId))
                        list.ListId = currentId;
                }

                foreach (var (current, revisedList) in _listChanges)
                {
                    var previousList = current.ListProperties;

                    ListProperties? newList = null;
                    if (revisedList is not null)
                    {
                        newList = revisedList.Clone();
                        if (_listMap.TryGetValue(revisedList.ListId, out var mappedId))
                            newList.ListId = mappedId;
                    }

                    current.ListProperties = newList;

                    var props = current.Properties;
                    if (props.FormatChange is { } change)
                    {
                        change.ListChanged = true;
                        change.PreviousList = previousList?.Clone();
                    }
                    else
                    {
                        props.FormatChange = new ParagraphFormatChange
                        {
                            Info = NewInfo(),
                            Previous = props.WithoutRevisions(),
                            ListChanged = true,
                            PreviousList = previousList?.Clone()
                        };
                        _result.FormatPlaces++;
                    }
                }
            }

            // ── Примечания ───────────────────────────────────────────────────

            /// <summary>
            /// Отметки примечаний файла в Id рукописи. Примечание, которого в рукописи
            /// нет, переносится в неё; то же, что уже есть (рукопись прошла через Word
            /// и вернулась со своими же примечаниями), — узнаётся по автору и тексту.
            /// </summary>
            private List<Guid>? MapComments(List<Guid>? revisedIds)
            {
                if (revisedIds is not { Count: > 0 }) return null;

                var result = new List<Guid>(revisedIds.Count);
                foreach (var id in revisedIds)
                    if (EnsureComment(id) is Guid mapped && !result.Contains(mapped))
                        result.Add(mapped);

                return result.Count > 0 ? result : null;
            }

            private Guid? EnsureComment(Guid revisedId)
            {
                if (_commentMap.TryGetValue(revisedId, out var known)) return known;
                if (!_revisedComments.TryGetValue(revisedId, out var comment)) return null;

                Guid? parent = comment.ParentId is Guid parentId ? EnsureComment(parentId) : null;

                foreach (var existing in _current.Comments)
                {
                    if (!string.Equals(existing.Author?.Trim(), comment.Author?.Trim(), StringComparison.Ordinal)) continue;
                    if (!string.Equals(existing.PlainText.Trim(), comment.PlainText.Trim(), StringComparison.Ordinal)) continue;
                    if (existing.ParentId != parent) continue;

                    _commentMap[revisedId] = existing.Id;
                    return existing.Id;
                }

                comment.ParentId = parent;
                _current.Comments.Add(comment);
                _commentMap[revisedId] = comment.Id;
                _result.CommentsAdded++;
                return comment.Id;
            }

            /// <summary>Ответы на перенесённые примечания приходят следом за ними.</summary>
            public void FinishComments()
            {
                bool added;
                do
                {
                    added = false;
                    foreach (var comment in _revisedComments.Values)
                    {
                        if (_commentMap.ContainsKey(comment.Id)) continue;
                        if (comment.ParentId is not Guid parentId || !_commentMap.ContainsKey(parentId)) continue;

                        EnsureComment(comment.Id);
                        added = true;
                    }
                }
                while (added);
            }

            /// <summary>
            /// Знак, совпавший текстом: начертание из файла, если оно другое (смена
            /// оформления), и примечания файла, если они легли на этот текст.
            /// </summary>
            private (RunProperties? Props, bool Format) EqualCellProps(
                RunProperties? currentProps, string currentStyle, RunProperties? revisedProps, string revisedStyle)
            {
                var currentLook = CurrentLook(currentProps, currentStyle);
                var revisedLook = RevisedLook(revisedProps, revisedStyle);
                bool format = !currentLook.Equals(revisedLook);

                RunProperties? result = format
                    ? WithFormatChange(currentProps, currentLook, revisedLook, revisedProps, revisedStyle)
                    : currentProps;

                var revisedComments = MapComments(revisedProps?.CommentIds);
                if (revisedComments is not null)
                {
                    var existing = result?.CommentIds;
                    List<Guid>? union = null;
                    foreach (var id in revisedComments)
                    {
                        if (existing is not null && existing.Contains(id)) continue;
                        union ??= existing is null ? new List<Guid>() : new List<Guid>(existing);
                        union.Add(id);
                    }

                    if (union is not null)
                    {
                        if (!format) result = currentProps?.Clone() ?? new RunProperties();
                        result!.CommentIds = union;
                    }
                }

                return (result, format);
            }

            // ── Знаки ────────────────────────────────────────────────────────

            /// <summary>
            /// Совпавший знак с другим начертанием: оформление рукописи, а в нём —
            /// начертание из файла; прежнее запоминается сменой оформления.
            /// </summary>
            /// <summary>Шрифт знака файла по имени, как он записан, — со стилем.</summary>
            private string RevisedFamily(RunProperties? revisedProps, string revisedStyle)
                => revisedProps?.FontFamily ?? _revisedStyles.ResolveFontFamily(revisedProps?.StyleName ?? revisedStyle);

            private RunProperties WithFormatChange(RunProperties? currentProps, Look currentLook, Look revisedLook, RunProperties? revisedProps, string revisedStyle)
            {
                var props = currentProps?.WithoutRevisions() ?? new RunProperties();
                revisedLook.ApplyTo(props, currentLook, revisedProps, RevisedFamily(revisedProps, revisedStyle));

                props.FormatChange = new RunFormatChange
                {
                    Info = NewInfo(),
                    Previous = currentProps is null || currentProps.IsDefaultFormatting()
                        ? null
                        : currentProps.WithoutRevisions()
                };

                return props;
            }

            /// <summary>
            /// Вставленный знак: оформление соседнего текста рукописи, а начертание —
            /// из файла, если оно там другое.
            /// </summary>
            private RunProperties WithInserted(RunProperties? template, string templateStyle, RunProperties? revisedProps, string revisedStyle)
            {
                var props = template?.WithoutRevisions() ?? new RunProperties();
                props.CommentIds = MapComments(revisedProps?.CommentIds);

                var templateLook = CurrentLook(template, templateStyle);
                var revisedLook = RevisedLook(revisedProps, revisedStyle);
                if (!templateLook.Equals(revisedLook))
                    revisedLook.ApplyTo(props, templateLook, revisedProps, RevisedFamily(revisedProps, revisedStyle));

                props.Inserted = NewInfo();
                return props;
            }

            /// <summary>
            /// Сноска рукописи и та же сноска в файле: абзацы текста сноски сверяются
            /// теми же правилами, что абзацы документа, — правка в сноске видна словами.
            /// </summary>
            private void CompareNotes(Guid currentId, Guid revisedId)
            {
                if (!_currentInlineObjects.TryGetValue(currentId, out var currentBlock) || currentBlock is not NoteBlock currentNote) return;
                if (!_revisedInlineObjects.TryGetValue(revisedId, out var revisedBlock) || revisedBlock is not NoteBlock revisedNote) return;
                if (ReferenceEquals(currentNote, revisedNote)) return;

                var mergedBlocks = MergeBlocks(
                    new List<BlockModel>(currentNote.Paragraphs),
                    new List<BlockModel>(revisedNote.Paragraphs));

                var paragraphs = new List<ParagraphBlock>(mergedBlocks.Count);
                foreach (var block in mergedBlocks)
                    if (block is ParagraphBlock paragraph)
                        paragraphs.Add(paragraph);

                if (paragraphs.Count == 0)
                    paragraphs.Add(new ParagraphBlock());

                currentNote.Paragraphs = paragraphs;
            }

            /// <summary>Объект в строке из файла переезжает в рукопись вместе со своим знаком.</summary>
            private void AdoptInlineObject(Guid objectId)
            {
                if (_currentInlineObjects.ContainsKey(objectId)) return;
                if (!_revisedInlineObjects.TryGetValue(objectId, out var block)) return;

                _current.Sections[0].InlineObjects.Add(block);
                _currentInlineObjects[objectId] = block;

                AdoptObjectFiles(block);

                switch (block)
                {
                    case NoteBlock note:
                        // Текст новой сноски — тоже вставка: отклонение знака сноски
                        // без этого оставило бы её текст обычным.
                        foreach (var paragraph in note.Paragraphs)
                            MarkNoteParagraphInserted(paragraph);
                        break;
                }
            }

            private void MarkNoteParagraphInserted(ParagraphBlock paragraph)
            {
                var cells = paragraph.ToCharCells();
                var mapped = new Dictionary<RunProperties, RunProperties>(ReferenceEqualityComparer.Instance);
                RunProperties? fromNull = null;

                for (int i = 0; i < cells.Count; i++)
                {
                    var source = cells[i].Props;
                    RunProperties marked;

                    if (source is null)
                    {
                        marked = fromNull ??= new RunProperties { Inserted = NewInfo() };
                    }
                    else if (!mapped.TryGetValue(source, out marked!))
                    {
                        marked = source.WithoutRevisions();
                        marked.CommentIds = MapComments(source.CommentIds);
                        marked.Inserted = NewInfo();
                        mapped[source] = marked;
                    }

                    cells[i] = new ParagraphBlock.CharCell(cells[i].Ch, marked, cells[i].InlineImageId);
                }

                paragraph.RebuildFromCharCells(cells);
                paragraph.Properties.MarkInserted = NewInfo();
            }

            private void AdoptImageFiles(ImageBlock image)
            {
                if (!string.IsNullOrWhiteSpace(image.ImageFileName))
                    _result.AdoptedImageFiles.Add(image.ImageFileName);
                if (!string.IsNullOrWhiteSpace(image.SourceImageFileName))
                    _result.AdoptedImageFiles.Add(image.SourceImageFileName!);
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
            /// Слова абзаца: буквы и цифры подряд — одно слово, каждый пробел и знак
            /// препинания — отдельно. Объект в строке — «словом» по отпечатку картинки.
            /// </summary>
            private List<Token> Tokenize(List<ParagraphBlock.CharCell> cells, bool revised)
            {
                var tokens = new List<Token>();
                var sb = new StringBuilder();
                int i = 0;

                while (i < cells.Count)
                {
                    var cell = cells[i];

                    if (cell.InlineImageId is Guid objectId)
                    {
                        tokens.Add(new Token(i, 1, ObjectToken(objectId, revised)));
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

            private static bool IsWordJoiner(List<ParagraphBlock.CharCell> cells, int index)
            {
                char ch = cells[index].Ch;
                if (ch is not ('\'' or '’' or '-' or '\u00AD')) return false;
                if (index == 0 || index + 1 >= cells.Count) return false;
                return char.IsLetterOrDigit(cells[index - 1].Ch) && char.IsLetterOrDigit(cells[index + 1].Ch)
                       && cells[index + 1].InlineImageId is null;
            }
        }

        // ── Начертание ───────────────────────────────────────────────────────

        /// <summary>
        /// Действующее начертание знака: что видно на листе с учётом стиля абзаца
        /// и символьного стиля. Его, а не запись свойств, сравнивают стороны.
        /// </summary>
        private readonly record struct Look(
            bool Bold,
            bool Italic,
            UnderlineStyle Underline,
            bool Strike,
            bool DoubleStrike,
            bool Superscript,
            bool Subscript,
            bool AllCaps,
            bool SmallCaps,
            bool Highlight,
            string FontFamily,
            double FontSize,
            string Color)
        {
            public static Look Of(RunProperties? props, string paragraphStyle, StyleResolver styles)
            {
                string style = props?.StyleName ?? paragraphStyle;

                return new Look(
                    props?.IsBold ?? (styles.ResolveBold(style) || (props?.StyleName is not null && styles.ResolveBold(paragraphStyle))),
                    props?.IsItalic ?? (styles.ResolveItalic(style) || (props?.StyleName is not null && styles.ResolveItalic(paragraphStyle))),
                    props?.UnderlineStyle ?? UnderlineStyle.None,
                    props?.IsStrikethrough ?? false,
                    props?.IsDoubleStrikethrough ?? false,
                    props?.IsSuperscript ?? false,
                    props?.IsSubscript ?? false,
                    (props?.IsAllCaps ?? false) || styles.ResolveAllCaps(style),
                    (props?.IsSmallCaps ?? false) || styles.ResolveSmallCaps(style),
                    HasHighlight(props?.HighlightColor),
                    (props?.FontFamily ?? styles.ResolveFontFamily(style)).Trim().ToUpperInvariant(),
                    Math.Round((props?.FontSize ?? styles.ResolveFontSize(style)) * 2.0) / 2.0,
                    NormalizeColor(props?.TextColor ?? styles.FindTextColor(style) ?? styles.FindTextColor(paragraphStyle)));
            }

            /// <summary>Цвет букв как строка сравнения: «авто» и чёрный — одно и то же.</summary>
            private static string NormalizeColor(string? color)
            {
                if (string.IsNullOrWhiteSpace(color)) return "AUTO";
                string value = color.Trim().TrimStart('#').ToUpperInvariant();
                if (value.Length == 8) value = value.Substring(2);
                return value is "AUTO" or "000000" ? "AUTO" : value;
            }

            private static bool HasHighlight(string? color)
                => !string.IsNullOrWhiteSpace(color)
                   && !string.Equals(color, "none", StringComparison.OrdinalIgnoreCase)
                   && !string.Equals(color, "transparent", StringComparison.OrdinalIgnoreCase);

            /// <summary>
            /// Ставит в свойства начертание этой стороны там, где оно отличается от
            /// начертания <paramref name="from"/>. Остальное оформление не трогается.
            /// </summary>
            public void ApplyTo(RunProperties props, Look from, RunProperties? source, string? familyName)
            {
                if (Bold != from.Bold) props.IsBold = Bold;
                if (Italic != from.Italic) props.IsItalic = Italic;
                if (Underline != from.Underline)
                {
                    props.UnderlineStyle = Underline;
                    props.UnderlineColor = source?.UnderlineColor;
                }
                if (Strike != from.Strike) props.IsStrikethrough = Strike;
                if (DoubleStrike != from.DoubleStrike) props.IsDoubleStrikethrough = DoubleStrike;
                if (Superscript != from.Superscript) props.IsSuperscript = Superscript;
                if (Subscript != from.Subscript) props.IsSubscript = Subscript;
                if (AllCaps != from.AllCaps) props.IsAllCaps = AllCaps;
                if (SmallCaps != from.SmallCaps) props.IsSmallCaps = SmallCaps;
                if (Highlight != from.Highlight) props.HighlightColor = Highlight ? source?.HighlightColor : null;
                if (!string.Equals(FontFamily, from.FontFamily, StringComparison.Ordinal)) props.FontFamily = familyName ?? source?.FontFamily ?? props.FontFamily;
                if (Math.Abs(FontSize - from.FontSize) > 0.01) props.FontSize = FontSize;
                if (!string.Equals(Color, from.Color, StringComparison.Ordinal)) props.TextColor = Color == "AUTO" ? null : "#" + Color;
            }
        }

        /// <summary>
        /// Действующее оформление абзаца — как его кладёт на лист отрисовка: своё
        /// значение абзаца, иначе стиля; интервалы в ячейке таблицы по умолчанию нули.
        /// </summary>
        private readonly record struct ParaLook(
            TextAlignment Alignment,
            double LeftIndent,
            double RightIndent,
            double FirstLineIndent,
            double SpaceBefore,
            double SpaceAfter,
            LineSpacingRule LineRule,
            double LineValue)
        {
            public static ParaLook Of(ParagraphBlock paragraph, StyleResolver styles, bool inCell)
            {
                var p = paragraph.Properties;
                string? style = p.StyleName;

                var (rule, value) = p.LineSpacingValue.HasValue
                    ? (p.LineSpacingRule ?? LineSpacingRule.Auto, p.LineSpacingValue.Value)
                    : (styles.ResolveLineSpacingRule(style), (double)styles.ResolveLineSpacing(style));

                return new ParaLook(
                    p.Alignment ?? (TextAlignment)(int)styles.ResolveAlignment(style),
                    p.LeftIndent ?? styles.ResolveLeftIndent(style),
                    p.RightIndent ?? styles.ResolveRightIndent(style),
                    p.FirstLineIndent ?? 0.0,
                    p.SpaceBefore ?? (inCell ? 0.0 : styles.ResolveSpaceBefore(style)),
                    p.SpaceAfter ?? (inCell ? 0.0 : styles.ResolveSpaceAfter(style)),
                    rule,
                    Math.Round(value, 3));
            }
        }

        /// <summary>Свойства и стиль абзаца как ключ памяти начертаний: свойства — по ссылке.</summary>
        private sealed class LookKeyComparer : IEqualityComparer<(RunProperties?, string)>
        {
            public static readonly LookKeyComparer Instance = new();

            public bool Equals((RunProperties?, string) x, (RunProperties?, string) y)
                => ReferenceEquals(x.Item1, y.Item1) && string.Equals(x.Item2, y.Item2, StringComparison.Ordinal);

            public int GetHashCode((RunProperties?, string) obj)
                => HashCode.Combine(
                    obj.Item1 is null ? 0 : RuntimeHelpers.GetHashCode(obj.Item1),
                    StringComparer.Ordinal.GetHashCode(obj.Item2));
        }

        /// <summary>Пара свойств как ключ памяти: сравнение по ссылкам.</summary>
        private sealed class PairComparer : IEqualityComparer<(RunProperties?, RunProperties?)>
        {
            public static readonly PairComparer Instance = new();

            public bool Equals((RunProperties?, RunProperties?) x, (RunProperties?, RunProperties?) y)
                => ReferenceEquals(x.Item1, y.Item1) && ReferenceEquals(x.Item2, y.Item2);

            public int GetHashCode((RunProperties?, RunProperties?) obj)
                => HashCode.Combine(
                    obj.Item1 is null ? 0 : RuntimeHelpers.GetHashCode(obj.Item1),
                    obj.Item2 is null ? 0 : RuntimeHelpers.GetHashCode(obj.Item2));
        }
    }
}
