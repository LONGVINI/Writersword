using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.Models.Styles;

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
    ///
    /// Второе применение — запись действия правкой (<see cref="Options.TrackEdit"/>).
    /// Действия редактора, которые сами правкой не записываются (вставка таблицы,
    /// разрыва, обновление оглавления и прочие), при включённой записи исправлений
    /// размечаются по разнице «до» и «после»: документ до действия — вторая версия,
    /// после — показанная. Здесь уже стоящие правки сохраняются: своя вставка,
    /// убранная действием, уходит совсем, как при удалении с записью, чужая —
    /// остаётся удалённой вставкой; удалённые картинки, фигуры и разрывы
    /// возвращаются на место отмеченными; сменённое оформление абзаца запоминает
    /// прежнее.
    /// </summary>
    public static class CompareMarkup
    {
        /// <summary>Как размечать различия.</summary>
        public sealed class Options
        {
            /// <summary>Автор и время пометок. Null — <see cref="CompareMarkup.Author"/>.</summary>
            public RevisionInfo? Info { get; init; }

            /// <summary>Показать пометки: включить вид «все исправления», если различия есть.</summary>
            public bool ShowMarkup { get; init; } = true;

            /// <summary>
            /// Разметка действия при записи исправлений: вторая версия — тот же документ
            /// до действия, его правки сохраняются по правилам записи.
            /// </summary>
            public bool TrackEdit { get; init; }
        }

        private static readonly JsonSerializerOptions FormattingKeyOptions = new() { WriteIndented = false };
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
            => Apply(shown, other, new Options());

        /// <summary>То же с выбором автора, показа и режима записи действия.</summary>
        public static Result Apply(DocumentModel shown, DocumentModel other, Options options)
        {
            var result = new Result();

            if (shown.Sections.Count == 0 || other.Sections.Count == 0)
                return result;

            var context = new Context(shown, other, result, options);

            var shownSection = shown.Sections[0];
            var otherSection = other.Sections[0];

            // Параметры разделов второй версии — до сведения блоков: оно переносит
            // удалённые блоки второй версии в показанную.
            var otherSectionLooks = new Dictionary<Guid, string>();
            foreach (var block in otherSection.Blocks)
                if (block is BreakBlock { IsSectionBreak: true } otherEnd)
                    otherSectionLooks[otherEnd.Id] = SectionFrame.RejectView(otherEnd, other);
            string otherFinalLook = SectionFrame.RejectViewFinal(other);

            // Список блоков меняется на месте: на него могут держать ссылку полотно и
            // вью-модель документа.
            var merged = context.MergeBlocks(shownSection.Blocks, otherSection.Blocks);
            shownSection.Blocks.Clear();
            shownSection.Blocks.AddRange(merged);

            if (options.TrackEdit)
                context.MergeFloatingObjects(shownSection.FloatingObjects, otherSection.FloatingObjects);

            // Текст сносок — те же правила, что у абзацев ячейки.
            context.MergeNotes(shownSection.InlineObjects);

            // Вид картинок и фигур в строке: размер, поворот, обрезка.
            context.CompareInlineAppearance(shownSection.InlineObjects);

            // Параметры разделов: лист, поля, колонки, рамка, номера строк.
            context.CompareSections(shownSection.Blocks, otherSectionLooks, otherFinalLook);

            // Пометки видны только при показе всех исправлений: вид «без исправлений»
            // спрятал бы как раз то, ради чего открыто сравнение.
            if (options.ShowMarkup && result.Total > 0)
                shown.RevisionView = RevisionView.AllMarkup;

            return result;
        }

        private sealed class Context
        {
            private readonly DocumentModel _shown;
            private readonly Result _result;
            private readonly Options _options;
            private readonly RevisionInfo _info;
            private readonly Dictionary<Guid, BlockModel> _otherInlineObjects = new();
            private readonly HashSet<Guid> _shownInlineObjects = new();

            public Context(DocumentModel shown, DocumentModel other, Result result, Options options)
            {
                _shown = shown;
                _result = result;
                _options = options;
                _info = options.Info?.Clone() ?? new RevisionInfo { Author = Author };

                foreach (var block in other.Sections[0].InlineObjects)
                    _otherInlineObjects[block.Id] = block;
                foreach (var block in shown.Sections[0].InlineObjects)
                    _shownInlineObjects.Add(block.Id);

                _ownShownInlineObjects = new HashSet<Guid>(_shownInlineObjects);
            }

            // Объекты в строке показанной версии до разметки: удалённые знаки второй
            // версии приносят свои объекты, и те своими не считаются.
            private readonly HashSet<Guid> _ownShownInlineObjects;

            /// <summary>
            /// Текст сносок. Сноска есть в обеих версиях — абзацы сверяются как абзацы
            /// ячейки; сноска только показанной версии — её текст вставлен целиком.
            /// Сноски второй версии, вернувшиеся с удалённым знаком, остаются как были.
            /// </summary>
            public void MergeNotes(List<BlockModel> shownObjects)
            {
                foreach (var block in shownObjects.ToArray())
                {
                    if (block is not NoteBlock note) continue;
                    if (!_ownShownInlineObjects.Contains(note.Id)) continue;

                    if (_otherInlineObjects.TryGetValue(note.Id, out var otherBlock) && otherBlock is NoteBlock otherNote)
                    {
                        if (ReferenceEquals(note, otherNote)) continue;

                        var merged = MergeParagraphs(note.Paragraphs, otherNote.Paragraphs);
                        note.Paragraphs.Clear();
                        note.Paragraphs.AddRange(merged);
                        continue;
                    }

                    foreach (var paragraph in note.Paragraphs)
                        MarkParagraphInserted(paragraph);
                }
            }

            private RevisionInfo NewInfo() => _info.Clone();

            /// <summary>
            /// Параметры разделов, которые есть в обеих версиях: сменённые — сменой
            /// параметров с прежними параметрами второй версии; вернувшиеся к прежним —
            /// без неё. Раздел своей вставки смены не получает: его параметры — часть
            /// вставки разрыва.
            /// </summary>
            public void CompareSections(List<BlockModel> shownBlocks, Dictionary<Guid, string> otherLooks, string otherFinalLook)
            {
                foreach (var block in shownBlocks)
                {
                    if (block is not BreakBlock { IsSectionBreak: true } sectionEnd) continue;
                    if (sectionEnd.Deleted is not null) continue;
                    if (_options.TrackEdit && IsOwn(sectionEnd.Inserted)) continue;
                    if (!otherLooks.TryGetValue(sectionEnd.Id, out var previous)) continue;

                    string now = SectionFrame.Capture(sectionEnd, _shown);

                    if (!SectionFrame.Differs(now, previous))
                    {
                        if (sectionEnd.Section is { } same) same.FormatChange = null;
                        continue;
                    }

                    var layout = sectionEnd.Section ??= new Models.Page.SectionLayout();
                    if (layout.FormatChange is not null) continue;

                    layout.FormatChange = new ObjectChange { Info = NewInfo(), Previous = previous };
                    _result.FormatPlaces++;
                }

                string finalNow = SectionFrame.CaptureFinal(_shown);
                if (!SectionFrame.Differs(finalNow, otherFinalLook))
                {
                    _shown.PageSetupChange = null;
                    return;
                }

                if (_shown.PageSetupChange is not null) return;

                _shown.PageSetupChange = new ObjectChange { Info = NewInfo(), Previous = otherFinalLook };
                _result.FormatPlaces++;
            }

            /// <summary>Вставка того же автора — её удаление убирает её совсем.</summary>
            private bool IsOwn(RevisionInfo? inserted) => RevisionService.IsOwnInsertion(inserted, _info);

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

                    case IAppearanceTracked:
                        CompareAppearance(shownBlock, otherBlock);
                        break;
                }
            }

            /// <summary>
            /// Вид картинки или фигуры, которая есть в обеих версиях: сдвинута, изменён
            /// размер, повёрнута, обрезана — смена вида с прежним видом второй версии.
            /// При записи действия своя вставка смены вида не получает, а объект,
            /// вернувшийся к прежнему виду, теряет её.
            /// </summary>
            private void CompareAppearance(BlockModel shownBlock, BlockModel otherBlock)
            {
                if (shownBlock is not IAppearanceTracked shown || otherBlock is not IAppearanceTracked) return;
                if (ReferenceEquals(shownBlock, otherBlock)) return;
                if (shown.Deleted is not null) return;
                if (_options.TrackEdit && IsOwn(shown.Inserted)) return;

                string previous = ObjectAppearance.RejectView(otherBlock);
                string now = ObjectAppearance.Capture(shownBlock);

                if (string.Equals(previous, now, StringComparison.Ordinal))
                {
                    shown.Change = null;
                    return;
                }

                if (shown.Change is not null) return;

                shown.Change = new ObjectChange { Info = NewInfo(), Previous = previous };
                _result.FormatPlaces++;
            }

            /// <summary>Вид объектов в строке, которые есть в обеих версиях.</summary>
            public void CompareInlineAppearance(List<BlockModel> shownObjects)
            {
                foreach (var block in shownObjects)
                {
                    if (block is not IAppearanceTracked) continue;
                    if (!_ownShownInlineObjects.Contains(block.Id)) continue;
                    if (_otherInlineObjects.TryGetValue(block.Id, out var other))
                        CompareAppearance(block, other);
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

                    case IRevisionMarked marked when marked.Inserted is null && marked.Deleted is null:
                        marked.Inserted = NewInfo();
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
                        return MarkParagraphDeleted(paragraph);

                    case TableBlock table:
                        foreach (var cell in table.Cells)
                            foreach (var paragraph in cell.ParagraphsDeep())
                                MarkParagraphDeleted(paragraph);
                        return true;

                    // Разрыв ни на какие файлы не ссылается — возвращается отмеченным
                    // и при сравнении версий.
                    case BreakBlock or ImageBlock or ShapeBlock when block is IRevisionMarked marked
                                                                && (_options.TrackEdit || block is BreakBlock):
                        return MarkWholeDeleted(marked);

                    default:
                        return false;
                }
            }

            /// <summary>
            /// Блок, удалённый целиком: остаётся на месте с отметкой удаления. Своя
            /// вставка уходит совсем — её как будто и не было.
            /// </summary>
            private bool MarkWholeDeleted(IRevisionMarked marked)
            {
                if (marked.Deleted is not null) return true;
                if (_options.TrackEdit && IsOwn(marked.Inserted)) return false;

                if (!_options.TrackEdit) marked.Inserted = null;
                marked.Deleted = NewInfo();
                _result.DeletedPlaces++;
                return true;
            }

            // ── Плавающие объекты ───────────────────────────────────────────

            /// <summary>
            /// Плавающие объекты при записи действия: убранный действием возвращается с
            /// отметкой удаления, новый без отметки получает отметку вставки. Объекты
            /// сопоставляются по Id — это тот же документ.
            /// </summary>
            public void MergeFloatingObjects(List<BlockModel> shownObjects, List<BlockModel> otherObjects)
            {
                var shownIds = new HashSet<Guid>();
                foreach (var block in shownObjects)
                {
                    shownIds.Add(block.Id);
                    if (block is IRevisionMarked { Inserted: null, Deleted: null } fresh
                        && !ContainsId(otherObjects, block.Id))
                    {
                        fresh.Inserted = NewInfo();
                        _result.InsertedPlaces++;
                        continue;
                    }

                    // Объект на месте — сверяется его вид.
                    foreach (var other in otherObjects)
                        if (other.Id == block.Id) { CompareAppearance(block, other); break; }
                }

                foreach (var block in otherObjects)
                {
                    if (shownIds.Contains(block.Id)) continue;
                    if (block is not IRevisionMarked marked) continue;
                    if (MarkWholeDeleted(marked))
                        shownObjects.Add(block);
                }
            }

            /// <summary>
            /// Плавающие объекты ячейки — по тем же правилам, что объекты листа: новый
            /// без отметки получает отметку вставки, убранный при записи действия
            /// возвращается с отметкой удаления.
            /// </summary>
            private void MergeCellFloats(TableCell shownCell, TableCell otherCell)
            {
                var otherIds = new HashSet<Guid>();
                if (otherCell.Floats is { } otherFloats)
                    foreach (var cellFloat in otherFloats)
                        otherIds.Add(cellFloat.Object.Id);

                var shownIds = new HashSet<Guid>();
                if (shownCell.Floats is { } shownFloats)
                {
                    foreach (var cellFloat in shownFloats)
                    {
                        shownIds.Add(cellFloat.Object.Id);
                        if (cellFloat.Object is IRevisionMarked { Inserted: null, Deleted: null } fresh
                            && !otherIds.Contains(cellFloat.Object.Id))
                        {
                            fresh.Inserted = NewInfo();
                            _result.InsertedPlaces++;
                            continue;
                        }

                        // Объект на месте — сверяется его вид.
                        if (otherCell.Floats is { } candidates)
                            foreach (var other in candidates)
                                if (other.Object.Id == cellFloat.Object.Id) { CompareAppearance(cellFloat.Object, other.Object); break; }
                    }
                }

                // Убранный объект возвращается только при записи действия: при сравнении
                // версий его файл мог уже уйти из проекта.
                if (!_options.TrackEdit || otherCell.Floats is not { } removedCandidates) return;

                foreach (var cellFloat in removedCandidates)
                {
                    if (shownIds.Contains(cellFloat.Object.Id)) continue;
                    if (cellFloat.Object is not IRevisionMarked marked) continue;
                    if (MarkWholeDeleted(marked))
                        (shownCell.Floats ??= new List<CellFloat>()).Add(cellFloat);
                }
            }

            private static bool ContainsId(List<BlockModel> blocks, Guid id)
            {
                foreach (var block in blocks)
                    if (block.Id == id) return true;
                return false;
            }

            // ── Таблицы ──────────────────────────────────────────────────────

            private void CompareTables(TableBlock shownTable, TableBlock otherTable)
            {
                // Строки и столбцы, вставленные или удалённые целиком, — правкой
                // самой строки или столбца (TableRevisions).
                MergeTableStructure(shownTable, otherTable);

                var otherCells = new Dictionary<Guid, TableCell>();
                foreach (var cell in otherTable.Cells)
                    otherCells[cell.Id] = cell;

                foreach (var cell in shownTable.Cells)
                {
                    if (!otherCells.TryGetValue(cell.Id, out var otherCell))
                    {
                        // Вставленная строка или столбец отмечены на самой ячейке: её
                        // текст — часть вставки.
                        if (cell.Inserted is not null) continue;

                        foreach (var paragraph in cell.ParagraphsDeep())
                            MarkParagraphInserted(paragraph);
                        continue;
                    }

                    // Удалённая строка, возвращённая из второй версии, — та же ячейка.
                    if (ReferenceEquals(cell, otherCell)) continue;

                    var mergedParagraphs = MergeParagraphs(cell.Paragraphs, otherCell.Paragraphs);
                    cell.Paragraphs.Clear();
                    cell.Paragraphs.AddRange(mergedParagraphs);

                    MergeCellFloats(cell, otherCell);

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

                CompareTableFormat(shownTable, otherTable);
            }

            /// <summary>
            /// Смена оформления таблицы: границы, заливка, ширины столбцов, высоты строк,
            /// поля и выравнивание ячеек, свойства таблицы — по общим строкам, столбцам
            /// и ячейкам. Прежний вид — оформление второй версии, каким оно было бы при
            /// отклонении её правок. Таблица, вернувшаяся к прежнему виду, теряет смену.
            /// </summary>
            private void CompareTableFormat(TableBlock shownTable, TableBlock otherTable)
            {
                if (ReferenceEquals(shownTable, otherTable)) return;

                string previous = TableFrame.RejectView(otherTable);
                string now = TableFrame.Capture(shownTable);

                if (!TableFrame.Differs(now, previous))
                {
                    shownTable.FormatChange = null;
                    return;
                }

                if (shownTable.FormatChange is not null) return;

                shownTable.FormatChange = new ObjectChange { Info = NewInfo(), Previous = previous };
                _result.FormatPlaces++;
            }

            /// <summary>
            /// Состав таблицы: строки или столбцы, которых во второй версии нет, —
            /// вставлены, а те, что есть только во второй, — удалены и возвращаются в
            /// показанную таблицу с отметкой удаления. Своя вставленная строка, убранная
            /// действием, уходит совсем. Строки узнаются по ячейке, с которой они
            /// начинаются слева, столбцы — по ячейке сверху. Если изменилось и то и
            /// другое или правка задела объединённые ячейки, состав остаётся как есть.
            /// </summary>
            private void MergeTableStructure(TableBlock shownTable, TableBlock otherTable)
            {
                var shownIds = new HashSet<Guid>();
                foreach (var cell in shownTable.Cells) shownIds.Add(cell.Id);
                var otherIds = new HashSet<Guid>();
                foreach (var cell in otherTable.Cells) otherIds.Add(cell.Id);

                bool anyNew = false;
                foreach (var cell in shownTable.Cells)
                    if (!otherIds.Contains(cell.Id)) { anyNew = true; break; }
                bool anyGone = false;
                foreach (var cell in otherTable.Cells)
                    if (!shownIds.Contains(cell.Id)) { anyGone = true; break; }

                if (!anyNew && !anyGone) return;

                if (TryMergeLines(shownTable, otherTable, shownIds, otherIds, TableRevisions.Axis.Row)) return;
                TryMergeLines(shownTable, otherTable, shownIds, otherIds, TableRevisions.Axis.Column);
            }

            private static int LineOf(TableCell cell, TableRevisions.Axis axis)
                => axis == TableRevisions.Axis.Row ? cell.Row : cell.Column;

            private static int SpanOf(TableCell cell, TableRevisions.Axis axis)
                => axis == TableRevisions.Axis.Row ? cell.RowSpan : cell.ColSpan;

            private static int LineCount(TableBlock table, TableRevisions.Axis axis)
                => axis == TableRevisions.Axis.Row ? table.RowCount : table.ColumnCount;

            private static TableCell? LineKeyCell(TableBlock table, int line, TableRevisions.Axis axis)
                => axis == TableRevisions.Axis.Row ? TableFrame.RowKeyCell(table, line) : TableFrame.ColumnKeyCell(table, line);

            private static List<TableCell> CellsOfLine(TableBlock table, int line, TableRevisions.Axis axis)
            {
                var cells = new List<TableCell>();
                foreach (var cell in table.Cells)
                    if (LineOf(cell, axis) == line) cells.Add(cell);
                return cells;
            }

            /// <summary>
            /// Сводит строки (или столбцы) двух версий таблицы. False — различие не
            /// сводится к строкам (столбцам) целиком.
            /// </summary>
            private bool TryMergeLines(
                TableBlock shownTable, TableBlock otherTable,
                HashSet<Guid> shownIds, HashSet<Guid> otherIds, TableRevisions.Axis axis)
            {
                // Поперёк — та же сетка: у строк те же столбцы, у столбцов те же строки.
                var across = axis == TableRevisions.Axis.Row ? TableRevisions.Axis.Column : TableRevisions.Axis.Row;
                if (LineCount(shownTable, across) != LineCount(otherTable, across)) return false;

                int shownCount = LineCount(shownTable, axis);
                int otherCount = LineCount(otherTable, axis);

                var ids = new Dictionary<Guid, int>();
                var a = new int[shownCount];
                var b = new int[otherCount];
                for (int i = 0; i < shownCount; i++)
                {
                    if (LineKeyCell(shownTable, i, axis) is not { } key) return false;
                    a[i] = KeyOf(key.Id, ids);
                }
                for (int i = 0; i < otherCount; i++)
                {
                    if (LineKeyCell(otherTable, i, axis) is not { } key) return false;
                    b[i] = KeyOf(key.Id, ids);
                }

                var ops = SequenceDiff.Compute(a, b, MaxEditDistance);

                // Проверка: у общих строк — те же ячейки, новые — целиком новые,
                // ушедшие — целиком ушедшие и без объединения поперёк.
                int shownLine = 0;
                foreach (var op in ops)
                {
                    switch (op.Kind)
                    {
                        case SequenceDiff.OpKind.Equal:
                        {
                            var shownCells = CellsOfLine(shownTable, op.IndexA, axis);
                            var otherCells = CellsOfLine(otherTable, op.IndexB, axis);
                            if (shownCells.Count != otherCells.Count) return false;
                            foreach (var cell in shownCells)
                                if (!otherIds.Contains(cell.Id)) return false;
                            shownLine = op.IndexA + 1;
                            break;
                        }

                        case SequenceDiff.OpKind.OnlyA:
                            foreach (var cell in CellsOfLine(shownTable, op.IndexA, axis))
                                if (otherIds.Contains(cell.Id)) return false;
                            shownLine = op.IndexA + 1;
                            break;

                        case SequenceDiff.OpKind.OnlyB:
                            foreach (var cell in CellsOfLine(otherTable, op.IndexB, axis))
                            {
                                if (shownIds.Contains(cell.Id)) return false;
                                if (SpanOf(cell, axis) != 1) return false;
                            }

                            // Объединение второй версии поперёк ушедшей строки.
                            foreach (var cell in otherTable.Cells)
                            {
                                int line = LineOf(cell, axis);
                                if (line < op.IndexB && line + SpanOf(cell, axis) > op.IndexB) return false;
                            }

                            // Объединение показанной версии через место, куда строка вернётся.
                            foreach (var cell in shownTable.Cells)
                            {
                                int line = LineOf(cell, axis);
                                if (line < shownLine && line + SpanOf(cell, axis) > shownLine) return false;
                            }
                            break;
                    }
                }

                // Сборка: строки по порядку, новые номера, высоты (ширины) — свои.
                var cells = new List<TableCell>(shownTable.Cells.Count + 8);
                var heights = new List<double>();
                var exact = new List<int>();
                var columns = new List<TableColumnDefinition>();
                int index = 0;
                bool insertedAny = false;
                bool deletedAny = false;

                void Take(TableBlock source, int line, List<TableCell> lineCells)
                {
                    foreach (var cell in lineCells)
                    {
                        if (axis == TableRevisions.Axis.Row) cell.Row = index;
                        else cell.Column = index;
                        cells.Add(cell);
                    }

                    if (axis == TableRevisions.Axis.Row)
                    {
                        heights.Add(source.GetRowMinHeightPt(line));
                        if (source.IsRowHeightExact(line)) exact.Add(index);
                    }
                    else
                    {
                        columns.Add(line < source.Columns.Count
                            ? source.Columns[line]
                            : new TableColumnDefinition { WidthType = TableColumnWidthType.Auto });
                    }

                    index++;
                }

                // Ячейки собираются заранее: номера меняются по ходу сборки.
                var shownLines = new List<TableCell>[shownCount];
                for (int i = 0; i < shownCount; i++) shownLines[i] = CellsOfLine(shownTable, i, axis);
                var otherLines = new List<TableCell>[otherCount];
                for (int i = 0; i < otherCount; i++) otherLines[i] = CellsOfLine(otherTable, i, axis);

                foreach (var op in ops)
                {
                    switch (op.Kind)
                    {
                        case SequenceDiff.OpKind.Equal:
                            Take(shownTable, op.IndexA, shownLines[op.IndexA]);
                            break;

                        case SequenceDiff.OpKind.OnlyA:
                        {
                            var lineCells = shownLines[op.IndexA];
                            bool fresh = false;
                            foreach (var cell in lineCells)
                            {
                                if (cell.Inserted is not null || cell.Deleted is not null) continue;
                                cell.Inserted = NewInfo();
                                fresh = true;
                            }
                            if (fresh) { _result.InsertedPlaces++; insertedAny = true; }
                            Take(shownTable, op.IndexA, lineCells);
                            break;
                        }

                        case SequenceDiff.OpKind.OnlyB:
                        {
                            var lineCells = otherLines[op.IndexB];

                            // Своя вставленная строка, убранная действием, уходит совсем.
                            bool ownInsertion = _options.TrackEdit
                                && lineCells.TrueForAll(cell => IsOwn(cell.Inserted));
                            if (ownInsertion) break;

                            foreach (var cell in lineCells)
                            {
                                if (!_options.TrackEdit) cell.Inserted = null;
                                cell.Deleted ??= NewInfo();
                                MarkCellContentDeleted(cell);
                            }

                            _result.DeletedPlaces++;
                            deletedAny = true;
                            Take(otherTable, op.IndexB, lineCells);
                            break;
                        }
                    }
                }

                if (!insertedAny && !deletedAny && index == shownCount) return true;

                shownTable.Cells.Clear();
                shownTable.Cells.AddRange(cells);

                if (axis == TableRevisions.Axis.Row)
                {
                    shownTable.RowCount = index;
                    shownTable.RowMinHeightsPt = heights;
                    shownTable.ExactHeightRows = exact.Count > 0 ? exact : null;
                }
                else
                {
                    shownTable.ColumnCount = index;
                    shownTable.Columns = columns;
                }

                return true;
            }

            /// <summary>
            /// Текст ячейки удалённой строки или столбца — удалённый. Своя вставка в
            /// нём уходит совсем; знаки абзацев не трогаются: строку решает её отметка.
            /// </summary>
            private void MarkCellContentDeleted(TableCell cell)
            {
                foreach (var paragraph in cell.ParagraphsDeep())
                {
                    var source = paragraph.ToCharCells();
                    var marked = new List<ParagraphBlock.CharCell>(source.Count);
                    foreach (var charCell in source)
                        if (WithDeleted(charCell) is { } deleted)
                            marked.Add(deleted);
                    paragraph.RebuildFromCharCells(marked);
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
                            if (MarkParagraphDeleted(otherParagraphs[op.IndexB]))
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

            /// <summary>
            /// Абзац второй версии, которого в показанной нет, — удалён. False — абзац
            /// не возвращается: при записи действия это была целиком своя вставка.
            /// </summary>
            private bool MarkParagraphDeleted(ParagraphBlock paragraph)
            {
                var source = paragraph.ToCharCells();
                var cells = new List<ParagraphBlock.CharCell>(source.Count);

                foreach (var cell in source)
                    if (WithDeleted(cell) is { } deleted)
                        cells.Add(deleted);

                var props = paragraph.Properties;

                if (_options.TrackEdit)
                {
                    // Своя вставленная строка, убранная действием целиком, уходит совсем.
                    if (cells.Count == 0 && IsOwn(props.MarkInserted)) return false;

                    paragraph.RebuildFromCharCells(cells);

                    // Отметка вставки знака абзаца остаётся: при отклонении он уйдёт,
                    // как уходил бы до действия.
                    if (props.MarkDeleted is null && !IsOwn(props.MarkInserted))
                        props.MarkDeleted = NewInfo();
                }
                else
                {
                    paragraph.RebuildFromCharCells(cells);
                    props.MarkInserted = null;
                    props.MarkDeleted = NewInfo();
                }

                _result.DeletedPlaces++;
                return true;
            }

            /// <summary>
            /// Текст сопоставленных абзацев: разность по словам. Совпавшие слова
            /// сверяются ещё и по оформлению.
            /// </summary>
            private void CompareParagraphs(ParagraphBlock shownParagraph, ParagraphBlock otherParagraph)
            {
                // Оформление и список абзаца — и при записи действия, и при сравнении
                // версий: сменённое выравнивание или пункт списка — такая же правка.
                CompareParagraphFormatting(shownParagraph, otherParagraph);

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

                                merged.Add(WithFormatChange(shownCell, otherCell.Props, NewInfo()));
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
                                if (WithDeleted(otherCells[token.Start + k]) is { } deletedCell)
                                    merged.Add(deletedCell);

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

            /// <summary>
            /// Оформление абзаца, сменённое действием мимо записи: прежнее
            /// запоминается сменой оформления абзаца (w:pPrChange).
            /// </summary>
            private void CompareParagraphFormatting(ParagraphBlock shownParagraph, ParagraphBlock otherParagraph)
            {
                var shownProps = shownParagraph.Properties;
                var otherProps = otherParagraph.Properties;

                // Прежний список второй стороны: если и у неё список уже сменён правкой —
                // тот, что был до этой правки.
                var otherList = otherProps.FormatChange is { ListChanged: true } otherChange
                    ? otherChange.PreviousList
                    : otherParagraph.ListProperties;
                bool listDiffers = !ParagraphFormatChange.SameList(shownParagraph.ListProperties, otherList);

                if (shownProps.FormatChange is { } existing)
                {
                    // Смена оформления уже записана; список, сменённый следом, дописывается
                    // в неё же — одна правка абзаца, как у Word.
                    if (!existing.ListChanged && listDiffers)
                    {
                        existing.ListChanged = true;
                        existing.PreviousList = otherList?.Clone();
                        _result.FormatPlaces++;
                    }
                    return;
                }

                string shownKey = JsonSerializer.Serialize(shownProps.WithoutRevisions(), FormattingKeyOptions);
                string otherKey = JsonSerializer.Serialize(otherProps.WithoutRevisions(), FormattingKeyOptions);
                bool propsDiffer = !string.Equals(shownKey, otherKey, StringComparison.Ordinal);
                if (!propsDiffer && !listDiffers) return;

                shownProps.FormatChange = new ParagraphFormatChange
                {
                    Info = NewInfo(),
                    Previous = otherProps.FormatChange?.Previous?.WithoutRevisions() ?? otherProps.WithoutRevisions(),
                    ListChanged = listDiffers,
                    PreviousList = listDiffers ? otherList?.Clone() : null
                };
                _result.FormatPlaces++;
            }

            // ── Знаки ────────────────────────────────────────────────────────

            private ParagraphBlock.CharCell WithInserted(ParagraphBlock.CharCell cell)
            {
                var props = cell.Props?.Clone() ?? new RunProperties();
                props.Inserted = NewInfo();
                return new ParagraphBlock.CharCell(cell.Ch, props, cell.InlineImageId);
            }

            /// <summary>
            /// Знак второй версии, которого в показанной нет, — удалённым. Null — знак не
            /// возвращается: при записи действия это своя вставка, она уходит совсем.
            /// </summary>
            private ParagraphBlock.CharCell? WithDeleted(ParagraphBlock.CharCell cell)
            {
                if (_options.TrackEdit)
                {
                    if (cell.Props?.Deleted is not null) return cell;
                    if (IsOwn(cell.Props?.Inserted)) return null;
                }

                // При записи действия чужая вставка остаётся вставкой — удалённой.
                var props = _options.TrackEdit
                    ? cell.Props?.Clone() ?? new RunProperties()
                    : cell.Props?.WithoutRevisions() ?? new RunProperties();
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

            private static ParagraphBlock.CharCell WithFormatChange(ParagraphBlock.CharCell cell, RunProperties? previous, RevisionInfo info)
            {
                var props = cell.Props?.Clone() ?? new RunProperties();
                props.FormatChange = new RunFormatChange
                {
                    Info = info,

                    // Прежнее оформление, уже сменённое правкой, остаётся тем, что было
                    // до первой смены: отклонение возвращает исходное.
                    Previous = previous?.FormatChange is { } earlier
                        ? earlier.Previous?.WithoutRevisions()
                        : previous is null || previous.IsDefaultFormatting() ? null : previous.WithoutRevisions()
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
