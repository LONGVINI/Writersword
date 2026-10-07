using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Writersword.Modules.TextEditor.Commands;
using Writersword.Modules.TextEditor.Contracts;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.Rendering;
using Writersword.Modules.TextEditor.Services;
using Writersword.Modules.TextEditor.ViewModels.Blocks;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Рецензирование на полотне: запись исправлений при правке, принятие и
    /// отклонение, переход между правками, виды показа.
    ///
    /// Пока запись исправлений включена, правка не меняет текст молча:
    ///   набранное отмечается вставкой (автор, время);
    ///   удаляемое остаётся в абзаце и отмечается удалением — кроме своей же
    ///   вставки, которая уходит совсем, как в Word;
    ///   Enter вставляет знак абзаца, Backspace в начале абзаца и Delete в конце
    ///   отмечают знак абзаца удалённым;
    ///   вставка из буфера отмечается вставкой целиком.
    ///
    /// Частые правки — знак набран, знак удалён — идут лёгким шагом отмены на один
    /// абзац (RevisionParagraphCommand). Правки, меняющие состав абзацев, и всё
    /// сложное (вставка из буфера, удаление выделения, принятие правок) — снимком
    /// документа: внутренние шаги обычной правки в это время в стек не идут, и
    /// Ctrl+Z откатывает всё действие одним шагом.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        // Глубина действий рецензирования, идущих снимком документа. Пока она больше
        // нуля, шаги обычной правки в стек не кладутся: всё действие откатывает снимок.
        private int _trackedSnapshotDepth;

        // Внутри действия рецензирования работает обычная правка (вставка, Enter):
        // запись исправлений к ней не применяется — отметки ставит само действие.
        private bool _trackedInner;

        /// <summary>Запись исправлений сейчас действует на правку.</summary>
        private bool TrackingActive
            => DocVm is { } vm && vm.Document.TrackRevisions && !_trackedInner && !IsEditingBlocked;

        /// <summary>Абзац слайса раскладки: абзац потока или абзац ячейки.</summary>
        private ParagraphBlock? ParagraphAtSlice(int index)
            => index >= 0 && index < _layouts.Count
                ? _layouts[index].Cell?.ParaBlock ?? _layouts[index].Vm?.Model
                : null;

        // ── Набор ─────────────────────────────────────────────────────────

        /// <summary>
        /// Набор текста под рецензированием. Без выделения — лёгкий шаг на один абзац;
        /// поверх выделения — выделенное отмечается удалённым, набранное встаёт за ним.
        /// </summary>
        private bool TryTrackedInsertText(string text)
        {
            if (!TrackingActive || string.IsNullOrEmpty(text)) return false;

            if (HasSel())
            {
                RunTrackedInsertion("Ввод с исправлениями", () => InsertText(text));
                return true;
            }

            var paragraph = ParagraphAtSlice(_caretPara);
            if (paragraph is null) return false;

            int pos = Clamp(_caretChar, 0, paragraph.TotalLength);

            var before = paragraph.ToCharCells();
            var propertiesBefore = paragraph.Properties.Clone();

            paragraph.SpliceText(pos, pos, text);
            RevisionService.MarkInserted(paragraph, pos, pos + text.Length, DocVm!.NewRevisionInfo());

            PushRevisionStep(
                RevisionParagraphCommand.EditKind.Typing, "Ввод с исправлениями",
                new List<(ParagraphBlock, List<ParagraphBlock.CharCell>, Models.Styles.ParagraphProperties)>
                {
                    (paragraph, before, propertiesBefore)
                },
                paragraph, pos, paragraph, pos + text.Length, text);

            RefreshTrackedParagraphs(new[] { paragraph });
            PlaceCaret(paragraph, pos + text.Length);
            return true;
        }

        /// <summary>
        /// Enter под рецензированием: обычное деление абзаца, после которого новый
        /// знак абзаца отмечен вставкой.
        /// </summary>
        private bool TryTrackedNewParagraph(Action split)
        {
            if (!TrackingActive) return false;

            RunTrackedInsertion("Новый абзац с исправлениями", split);
            return true;
        }

        // ── Удаление ──────────────────────────────────────────────────────

        /// <summary>
        /// Backspace под рецензированием: знак перед кареткой отмечается удалённым,
        /// каретка встаёт перед ним. Уже удалённое перешагивается. В начале абзаца
        /// удалённым отмечается знак предыдущего абзаца.
        /// </summary>
        private bool TryTrackedDeleteBack()
        {
            if (!TrackingActive) return false;

            if (HasSel())
            {
                RunTrackedSelectionDelete("Удаление с исправлениями");
                return true;
            }

            var paragraph = ParagraphAtSlice(_caretPara);
            if (paragraph is null) return false;

            var cells = paragraph.ToCharCells();
            int pos = SkipHiddenBackward(GetLayoutAt(_caretPara), Clamp(_caretChar, 0, cells.Count));
            while (pos > 0 && cells[pos - 1].Props?.Deleted is not null) pos--;

            if (pos == 0) return TrackedJoinWithPrevious(paragraph);

            DeleteCharsTracked(paragraph, pos - 1, pos, caretBefore: _caretChar, caretAfter: pos - 1);
            return true;
        }

        /// <summary>
        /// Delete под рецензированием: знак за кареткой отмечается удалённым, каретка
        /// встаёт за ним. В конце абзаца удалённым отмечается его знак абзаца.
        /// </summary>
        private bool TryTrackedDeleteForward()
        {
            if (!TrackingActive) return false;

            if (HasSel())
            {
                RunTrackedSelectionDelete("Удаление с исправлениями");
                return true;
            }

            var paragraph = ParagraphAtSlice(_caretPara);
            if (paragraph is null) return false;

            var cells = paragraph.ToCharCells();
            int pos = SkipHiddenForward(GetLayoutAt(_caretPara), Clamp(_caretChar, 0, cells.Count), cells.Count);
            while (pos < cells.Count && cells[pos].Props?.Deleted is not null) pos++;

            if (pos >= cells.Count) return TrackedJoinWithNext(paragraph);

            // Своя вставка уходит совсем, и каретка остаётся на месте; отмеченный
            // удалённым знак каретка перешагивает.
            bool own = RevisionService.IsOwnInsertion(cells[pos].Props?.Inserted, DocVm!.NewRevisionInfo());
            DeleteCharsTracked(paragraph, pos, pos + 1, caretBefore: _caretChar, caretAfter: own ? pos : pos + 1);
            return true;
        }

        /// <summary>Удаляет знаки абзаца под рецензированием лёгким шагом.</summary>
        private void DeleteCharsTracked(ParagraphBlock paragraph, int from, int to, int caretBefore, int caretAfter)
        {
            var before = paragraph.ToCharCells();
            var propertiesBefore = paragraph.Properties.Clone();

            RevisionService.MarkDeleted(paragraph, from, to, DocVm!.NewRevisionInfo());

            caretAfter = Clamp(caretAfter, 0, paragraph.TotalLength);

            PushRevisionStep(
                RevisionParagraphCommand.EditKind.Delete, "Удаление с исправлениями",
                new List<(ParagraphBlock, List<ParagraphBlock.CharCell>, Models.Styles.ParagraphProperties)>
                {
                    (paragraph, before, propertiesBefore)
                },
                paragraph, caretBefore, paragraph, caretAfter, string.Empty);

            RefreshTrackedParagraphs(new[] { paragraph });
            PlaceCaret(paragraph, caretAfter);
        }

        /// <summary>
        /// Backspace в начале абзаца: знак предыдущего абзаца отмечается удалённым.
        /// Если этот знак вставлен тем же автором — абзацы сливаются по-настоящему.
        /// Если слить не с чем (начало документа, таблица, разрыв), действует обычная
        /// правка.
        /// </summary>
        private bool TrackedJoinWithPrevious(ParagraphBlock paragraph)
        {
            var doc = DocVm!.Document;
            if (RevisionService.Locate(doc, paragraph) is not { } location) return false;

            // Номер списка в начале пункта Backspace снимает и при рецензировании:
            // смену списка модель правкой не описывает, поэтому она идёт обычной правкой.
            if (paragraph.ListProperties is not null) return false;

            int previousIndex = location.List.PreviousMergeable(location.Index);
            if (previousIndex < 0) return false;

            var previous = location.List.ParagraphAt(previousIndex)!;
            var info = DocVm.NewRevisionInfo();

            if (previous.Properties.MarkDeleted is not null)
            {
                PlaceCaret(previous, previous.TotalLength);
                return true;
            }

            if (RevisionService.IsOwnInsertion(previous.Properties.MarkInserted, info))
            {
                RunTrackedStructural("Удаление с исправлениями", () =>
                {
                    int joinAt = RevisionService.MergeWithNext(location.List, previousIndex);
                    return (new List<ParagraphBlock> { previous }, true, previous, Math.Max(joinAt, 0));
                });
                return true;
            }

            MarkParagraphEndDeleted(previous, info, caretTarget: previous, caretChar: previous.TotalLength);
            return true;
        }

        /// <summary>
        /// Delete в конце абзаца: его знак абзаца отмечается удалённым, каретка уходит
        /// в начало следующего. Свой вставленный знак — абзацы сливаются.
        /// </summary>
        private bool TrackedJoinWithNext(ParagraphBlock paragraph)
        {
            var doc = DocVm!.Document;
            if (RevisionService.Locate(doc, paragraph) is not { } location) return false;

            int nextIndex = location.List.NextMergeable(location.Index);
            if (nextIndex < 0) return true;

            var next = location.List.ParagraphAt(nextIndex)!;
            var info = DocVm.NewRevisionInfo();

            if (paragraph.Properties.MarkDeleted is not null)
            {
                PlaceCaret(next, 0);
                return true;
            }

            if (RevisionService.IsOwnInsertion(paragraph.Properties.MarkInserted, info))
            {
                int length = paragraph.TotalLength;
                RunTrackedStructural("Удаление с исправлениями", () =>
                {
                    RevisionService.MergeWithNext(location.List, location.Index);
                    return (new List<ParagraphBlock> { paragraph }, true, paragraph, length);
                });
                return true;
            }

            MarkParagraphEndDeleted(paragraph, info, caretTarget: next, caretChar: 0);
            return true;
        }

        /// <summary>Отмечает знак абзаца удалённым лёгким шагом.</summary>
        private void MarkParagraphEndDeleted(
            ParagraphBlock paragraph, RevisionInfo info, ParagraphBlock caretTarget, int caretChar)
        {
            var caretFrom = ParagraphAtSlice(_caretPara) ?? paragraph;
            int caretFromChar = _caretChar;

            var cells = paragraph.ToCharCells();
            var propertiesBefore = paragraph.Properties.Clone();

            paragraph.Properties.MarkDeleted = info.Clone();

            PushRevisionStep(
                RevisionParagraphCommand.EditKind.Mark, "Удаление знака абзаца",
                new List<(ParagraphBlock, List<ParagraphBlock.CharCell>, Models.Styles.ParagraphProperties)>
                {
                    (paragraph, cells, propertiesBefore)
                },
                caretFrom, caretFromChar, caretTarget, caretChar, string.Empty);

            RefreshTrackedParagraphs(new[] { paragraph });
            PlaceCaret(caretTarget, caretChar);
        }

        // ── Действия снимком документа ────────────────────────────────────

        /// <summary>
        /// Удаление выделения под рецензированием: выделенное отмечается удалённым,
        /// знаки абзацев внутри него — тоже; своя вставка уходит совсем. Каретка
        /// встаёт в конец выделения — за удалённым текстом, как в Word.
        /// </summary>
        private void RunTrackedSelectionDelete(string description)
        {
            RunTrackedStructural(description, () =>
            {
                var result = TrackedDeleteSelectionCore();
                return (result.Touched, result.StructureChanged, result.CaretParagraph, result.CaretChar);
            });
        }

        private (List<ParagraphBlock> Touched, bool StructureChanged, ParagraphBlock? CaretParagraph, int CaretChar)
            TrackedDeleteSelectionCore()
        {
            var touched = new List<ParagraphBlock>();
            var doc = DocVm!.Document;

            var (sp, sc, ep, ec) = NormalizeSelection();
            var startParagraph = ParagraphAtSlice(sp);
            var endParagraph = ParagraphAtSlice(ep);
            if (startParagraph is null || endParagraph is null) return (touched, false, null, 0);

            var order = RevisionService.ReadingOrder(doc);
            int startIndex = IndexOfReference(order, startParagraph);
            int endIndex = IndexOfReference(order, endParagraph);
            if (startIndex < 0 || endIndex < 0 || endIndex < startIndex) return (touched, false, startParagraph, sc);

            var info = DocVm.NewRevisionInfo();
            var ownMarks = new List<ParagraphBlock>();

            ParagraphBlock caretParagraph = endParagraph;
            int caretChar = ec;

            for (int k = endIndex; k >= startIndex; k--)
            {
                var paragraph = order[k];
                int length = paragraph.TotalLength;
                int from = k == startIndex ? Clamp(sc, 0, length) : 0;
                int to = k == endIndex ? Clamp(ec, 0, length) : length;

                int removed = RevisionService.MarkDeleted(paragraph, from, to, info);
                touched.Add(paragraph);

                if (k == endIndex) caretChar = to - removed;

                if (k == endIndex) continue;

                // Знак абзаца внутри выделения.
                var props = paragraph.Properties;
                if (props.MarkDeleted is not null) continue;

                if (RevisionService.IsOwnInsertion(props.MarkInserted, info)) ownMarks.Add(paragraph);
                else props.MarkDeleted = info.Clone();
            }

            // Свои вставленные знаки абзаца уходят совсем: абзацы сливаются. Обход
            // с конца — слияние не сдвигает ещё не слитые абзацы.
            bool structureChanged = false;
            foreach (var paragraph in ownMarks)
            {
                if (RevisionService.Locate(doc, paragraph) is not { } location) continue;

                int nextIndex = location.List.NextMergeable(location.Index);
                var absorbed = nextIndex >= 0 ? location.List.ParagraphAt(nextIndex) : null;

                int joinAt = RevisionService.MergeWithNext(location.List, location.Index);
                if (joinAt < 0)
                {
                    paragraph.Properties.MarkDeleted = info.Clone();
                    continue;
                }

                structureChanged = true;
                if (ReferenceEquals(absorbed, caretParagraph))
                {
                    caretParagraph = paragraph;
                    caretChar += joinAt;
                }
            }

            return (touched, structureChanged, caretParagraph, caretChar);
        }

        /// <summary>
        /// Вставка под рецензированием (Enter, вставка из буфера, набор поверх
        /// выделения): выделенное сначала отмечается удалённым, затем идёт обычная
        /// вставка, и всё, что она добавила, отмечается вставленным.
        /// </summary>
        private void RunTrackedInsertion(string description, Action insert)
        {
            if (DocVm is null) return;

            BeginTrackedSnapshot(description);
            try
            {
                PrepareTrackedInsertion(out var anchor);
                insert();
                FinishTrackedInsertion(anchor);
            }
            finally
            {
                EndTrackedSnapshot();
            }
        }

        /// <summary>То же для вставки, которая ждёт буфер обмена.</summary>
        private async Task RunTrackedInsertionAsync(string description, Func<Task> insert)
        {
            if (DocVm is null) return;

            BeginTrackedSnapshot(description);
            try
            {
                PrepareTrackedInsertion(out var anchor);
                await insert();
                FinishTrackedInsertion(anchor);
            }
            finally
            {
                EndTrackedSnapshot();
            }
        }

        /// <summary>
        /// Место вставки: абзац каретки, соседние с ним блоки его списка и сколько
        /// знаков стоит до и после каретки. После вставки всё между соседями — это
        /// прежнее начало абзаца, вставленное и прежний хвост.
        /// </summary>
        private sealed class InsertionAnchor
        {
            public required TableCell? Cell { get; init; }
            public required int SectionIndex { get; init; }
            public required BlockModel? PreviousBlock { get; init; }
            public required BlockModel? NextBlock { get; init; }
            public required ParagraphBlock Paragraph { get; init; }
            public required int HeadLength { get; init; }
            public required int TailLength { get; init; }
            public required RevisionInfo? MarkInserted { get; init; }
            public required RevisionInfo? MarkDeleted { get; init; }
        }

        private void PrepareTrackedInsertion(out InsertionAnchor? anchor)
        {
            anchor = null;
            var doc = DocVm!.Document;

            if (HasSel())
            {
                var deleted = TrackedDeleteSelectionCore();
                RefreshAfterTrackedChange(deleted.Touched, deleted.StructureChanged);
                if (deleted.CaretParagraph is not null)
                    PlaceCaret(deleted.CaretParagraph, deleted.CaretChar);
            }

            var paragraph = ParagraphAtSlice(_caretPara);
            if (paragraph is null) return;
            if (RevisionService.Locate(doc, paragraph) is not { } location) return;

            int sectionIndex = 0;
            if (location.List.IsFlow)
            {
                for (int s = 0; s < doc.Sections.Count; s++)
                {
                    if (!doc.Sections[s].Blocks.Contains(paragraph)) continue;
                    sectionIndex = s;
                    break;
                }
            }

            int length = paragraph.TotalLength;
            int head = Clamp(_caretChar, 0, length);

            anchor = new InsertionAnchor
            {
                Cell = location.List.Cell,
                SectionIndex = sectionIndex,
                PreviousBlock = location.Index > 0 ? location.List.BlockAt(location.Index - 1) : null,
                NextBlock = location.Index + 1 < location.List.Count ? location.List.BlockAt(location.Index + 1) : null,
                Paragraph = paragraph,
                HeadLength = head,
                TailLength = length - head,
                MarkInserted = paragraph.Properties.MarkInserted?.Clone(),
                MarkDeleted = paragraph.Properties.MarkDeleted?.Clone()
            };
        }

        private void FinishTrackedInsertion(InsertionAnchor? anchor)
        {
            if (anchor is null || DocVm is null) return;

            var doc = DocVm.Document;
            var list = anchor.Cell is not null
                ? RevisionService.ParagraphList.OfCell(anchor.Cell)
                : RevisionService.ParagraphList.Flow(doc.Sections[Math.Min(anchor.SectionIndex, doc.Sections.Count - 1)].Blocks);

            int start = 0;
            if (anchor.PreviousBlock is not null)
            {
                int previousAt = IndexOfBlock(list, anchor.PreviousBlock);
                if (previousAt < 0) return;
                start = previousAt + 1;
            }

            int end = list.Count;
            if (anchor.NextBlock is not null)
            {
                int nextAt = IndexOfBlock(list, anchor.NextBlock);
                if (nextAt < 0) return;
                end = nextAt;
            }

            var region = new List<ParagraphBlock>();
            for (int i = start; i < end; i++)
                if (list.ParagraphAt(i) is { } paragraph)
                    region.Add(paragraph);

            if (region.Count == 0) return;

            int total = 0;
            foreach (var paragraph in region) total += paragraph.TotalLength;

            int insertedFrom = Math.Min(anchor.HeadLength, total);
            int insertedTo = Math.Max(insertedFrom, total - anchor.TailLength);

            var info = DocVm.NewRevisionInfo();
            int position = 0;

            for (int j = 0; j < region.Count; j++)
            {
                var paragraph = region[j];
                int length = paragraph.TotalLength;

                int from = Math.Max(insertedFrom - position, 0);
                int to = Math.Min(insertedTo - position, length);
                if (to > from) RevisionService.MarkInserted(paragraph, from, to, info);

                position += length;

                if (j < region.Count - 1)
                {
                    // Знак абзаца внутри вставленного — новый.
                    paragraph.Properties.MarkInserted = info.Clone();
                    paragraph.Properties.MarkDeleted = null;
                }
                else
                {
                    // Последний абзац держит прежний знак абзаца — с его отметками.
                    paragraph.Properties.MarkInserted = anchor.MarkInserted?.Clone();
                    paragraph.Properties.MarkDeleted = anchor.MarkDeleted?.Clone();
                }
            }

            RefreshAfterTrackedChange(region, region.Count > 1);
        }

        private static int IndexOfBlock(RevisionService.ParagraphList list, BlockModel block)
        {
            for (int i = 0; i < list.Count; i++)
                if (ReferenceEquals(list.BlockAt(i), block)) return i;
            return -1;
        }

        private static int IndexOfReference(List<ParagraphBlock> order, ParagraphBlock paragraph)
        {
            for (int i = 0; i < order.Count; i++)
                if (ReferenceEquals(order[i], paragraph)) return i;
            return -1;
        }

        /// <summary>
        /// Правка модели одним шагом отмены-снимком. Действие возвращает изменённые
        /// абзацы, признак смены состава абзацев и место каретки.
        /// </summary>
        private void RunTrackedStructural(
            string description,
            Func<(List<ParagraphBlock> Touched, bool StructureChanged, ParagraphBlock? CaretParagraph, int CaretChar)> change)
        {
            if (DocVm is null) return;

            BeginTrackedSnapshot(description);
            try
            {
                var result = change();
                RefreshAfterTrackedChange(result.Touched, result.StructureChanged);
                if (result.CaretParagraph is not null)
                    PlaceCaret(result.CaretParagraph, result.CaretChar);
            }
            finally
            {
                EndTrackedSnapshot();
            }
        }

        private void BeginTrackedSnapshot(string description)
        {
            BeginEdit(description);
            _trackedSnapshotDepth++;
            _trackedInner = true;
        }

        private void EndTrackedSnapshot()
        {
            _trackedSnapshotDepth = Math.Max(0, _trackedSnapshotDepth - 1);
            _trackedInner = _trackedSnapshotDepth > 0;
            CommitEdit();
            DocVm?.RaiseRevisionsChanged();
        }

        // ── Шаг отмены и обновление вида ──────────────────────────────────

        /// <summary>Кладёт лёгкий шаг правки под рецензированием в стек отмены.</summary>
        private void PushRevisionStep(
            RevisionParagraphCommand.EditKind kind,
            string description,
            List<(ParagraphBlock Paragraph, List<ParagraphBlock.CharCell> Before, Models.Styles.ParagraphProperties PropertiesBefore)> changed,
            ParagraphBlock caretBeforeParagraph, int caretBefore,
            ParagraphBlock caretAfterParagraph, int caretAfter,
            string typed)
        {
            var entries = new List<RevisionParagraphCommand.Entry>(changed.Count);
            foreach (var (paragraph, before, propertiesBefore) in changed)
            {
                entries.Add(new RevisionParagraphCommand.Entry
                {
                    ParaId = paragraph.Id,
                    Before = before,
                    After = paragraph.ToCharCells(),
                    PropertiesBefore = propertiesBefore,
                    PropertiesAfter = paragraph.Properties.Clone()
                });
            }

            var command = new RevisionParagraphCommand(entries, kind, description)
            {
                CaretParaBefore = caretBeforeParagraph.Id,
                CaretCharBefore = caretBefore,
                CaretParaAfter = caretAfterParagraph.Id,
                CaretCharAfter = caretAfter,
                TypedText = typed,
                RefreshCallback = OnRevisionStepReplayed
            };

            PushTextCommand(command);

            DocVm?.RaiseContentModified();
            DocVm?.RaiseRevisionsChanged();
        }

        /// <summary>Откат или повтор лёгкого шага: абзацы перечитываются, каретка встаёт на место.</summary>
        private void OnRevisionStepReplayed(IReadOnlyList<Guid> paragraphIds, Guid caretParagraphId, int caretChar)
        {
            if (DocVm is null) return;

            var doc = DocVm.Document;
            var paragraphs = new List<ParagraphBlock>();
            foreach (var id in paragraphIds)
                if (DocumentModelHelper.FindParagraph(doc, id) is { } paragraph)
                    paragraphs.Add(paragraph);

            RefreshTrackedParagraphs(paragraphs);

            if (DocumentModelHelper.FindParagraph(doc, caretParagraphId) is { } caretParagraph)
                PlaceCaret(caretParagraph, caretChar);

            DocVm.RaiseRevisionsChanged();
        }

        /// <summary>
        /// Перечитывает абзацы после правки на месте: текст вью-модели, раскладка.
        /// Пока вид прячет правки, абзац может спрятаться целиком — тогда раскладка
        /// пересобирается полностью, иначе — только у этих абзацев.
        /// </summary>
        private void RefreshTrackedParagraphs(IEnumerable<ParagraphBlock> paragraphs)
        {
            if (DocVm is null) return;

            bool anyCell = false;
            bool fullRebuild = DocVm.Document.RevisionView != RevisionView.AllMarkup;
            var flowVms = FlowViewModelsByModel();

            foreach (var paragraph in paragraphs)
            {
                if (flowVms.TryGetValue(paragraph, out var pvm))
                {
                    pvm.RefreshPlainTextFromModel();
                    _layoutCache.Remove(pvm);

                    if (!fullRebuild)
                        ScheduleRebuild(DocVm.Paragraphs.IndexOf(pvm));
                    continue;
                }

                if (_cellVmCache.TryGetValue(paragraph, out var cellVm))
                {
                    cellVm.RefreshPlainTextFromModel();
                    _layoutCache.Remove(cellVm);
                }

                anyCell = true;
            }

            if (anyCell) InvalidateCellLayoutCaches();

            if (anyCell || fullRebuild)
            {
                RebuildLayouts();
                InvalidateMeasure();
            }

            InvalidateFull();
        }

        /// <summary>
        /// Обновление вида после правки модели снимком: состав абзацев, их тексты,
        /// раскладка целиком.
        /// </summary>
        private void RefreshAfterTrackedChange(IEnumerable<ParagraphBlock> touched, bool structureChanged)
        {
            if (DocVm is null) return;

            if (structureChanged) DocVm.SyncParagraphViewModelsPublic();

            var flowVms = FlowViewModelsByModel();
            foreach (var paragraph in touched)
            {
                if (!flowVms.TryGetValue(paragraph, out var pvm)) continue;
                pvm.RefreshPlainTextFromModel();
                _layoutCache.Remove(pvm);
            }

            RefreshAfterTableCommand();
        }

        private Dictionary<ParagraphBlock, ParagraphViewModel> FlowViewModelsByModel()
        {
            var map = new Dictionary<ParagraphBlock, ParagraphViewModel>(ReferenceEqualityComparer.Instance);
            if (DocVm is null) return map;

            foreach (var pvm in DocVm.Paragraphs)
                map.TryAdd(pvm.Model, pvm);

            return map;
        }

        /// <summary>
        /// Ставит каретку в абзац. Абзац спрятан видом показа (целиком удалённый в
        /// «Без исправлений») — каретка уходит в ближайший видимый абзац после него,
        /// а если таких нет — перед ним.
        /// </summary>
        private void PlaceCaret(ParagraphBlock paragraph, int charIndex)
        {
            if (_layouts.Count == 0) return;

            int? slice = FindSliceFor(new LayoutPoint(paragraph, Math.Max(0, charIndex)));
            int caretChar = charIndex;

            if (slice is null && DocVm is not null)
            {
                var order = RevisionService.ReadingOrder(DocVm.Document);
                int at = IndexOfReference(order, paragraph);

                for (int i = at + 1; at >= 0 && i < order.Count && slice is null; i++)
                {
                    slice = FindSliceFor(new LayoutPoint(order[i], 0));
                    caretChar = 0;
                }

                for (int i = at - 1; at >= 0 && i >= 0 && slice is null; i--)
                {
                    slice = FindSliceFor(new LayoutPoint(order[i], order[i].TotalLength));
                    caretChar = order[i].TotalLength;
                }
            }

            if (slice is not int index) return;

            _caretPara = index;
            _caretChar = Clamp(caretChar, 0, ParagraphAtSlice(index)?.TotalLength ?? 0);
            _caretLineHint = -1;
            SnapCaretToCorrectSlice();
            SyncSel();
            UpdatePreferredX();
            UpdateSelectionContext();
            ResetCaret();
            InvalidateFull();
        }

        // ── Кнопки рецензирования ─────────────────────────────────────────

        /// <summary>
        /// Команда вкладки «Рецензирование». Возвращает false, если делать нечего:
        /// правок нет или под кареткой и дальше их не нашлось.
        /// </summary>
        public bool ExecuteReviewCommand(ReviewAction action)
        {
            if (DocVm is null) return false;

            switch (action)
            {
                case ReviewAction.Next:
                    return GoToRevision(forward: true);

                case ReviewAction.Previous:
                    return GoToRevision(forward: false);

                case ReviewAction.AcceptAll:
                case ReviewAction.RejectAll:
                    if (IsEditingBlocked) return false;
                    return ResolveAll(action == ReviewAction.AcceptAll);

                case ReviewAction.Accept:
                case ReviewAction.Reject:
                    if (IsEditingBlocked) return false;
                    return ResolveCurrent(action == ReviewAction.Accept);
            }

            return false;
        }

        private bool ResolveAll(bool accept)
        {
            var doc = DocVm!.Document;
            if (RevisionService.CountRevisions(doc) == 0) return false;

            var caretParagraph = ParagraphAtSlice(_caretPara);
            int caretChar = _caretChar;

            BeginTrackedSnapshot(accept ? "Принять все исправления" : "Отклонить все исправления");
            try
            {
                var (touched, structureChanged) = RevisionService.Resolve(
                    doc, _ => new RevisionService.Scope(0, int.MaxValue, true), accept);

                RefreshAfterTrackedChange(touched, structureChanged);
                RestoreCaretAfterResolve(caretParagraph, caretChar);
            }
            finally
            {
                EndTrackedSnapshot();
            }

            return true;
        }

        /// <summary>
        /// Принять или отклонить: выделение — все правки в нём; без выделения —
        /// правку под кареткой. Затем каретка уходит к следующей правке, как у кнопки
        /// Word «Принять и перейти к следующему».
        /// </summary>
        private bool ResolveCurrent(bool accept)
        {
            var doc = DocVm!.Document;
            Func<ParagraphBlock, RevisionService.Scope?>? scopeOf = null;
            ParagraphBlock? caretParagraph;
            int caretChar;

            if (HasSel())
            {
                var (sp, sc, ep, ec) = NormalizeSelection();
                var startParagraph = ParagraphAtSlice(sp);
                var endParagraph = ParagraphAtSlice(ep);
                if (startParagraph is null || endParagraph is null) return false;

                var order = RevisionService.ReadingOrder(doc);
                int startIndex = IndexOfReference(order, startParagraph);
                int endIndex = IndexOfReference(order, endParagraph);
                if (startIndex < 0 || endIndex < startIndex) return false;

                var scopes = new Dictionary<ParagraphBlock, RevisionService.Scope>(ReferenceEqualityComparer.Instance);
                for (int k = startIndex; k <= endIndex; k++)
                {
                    var paragraph = order[k];
                    int length = paragraph.TotalLength;
                    int from = k == startIndex ? Clamp(sc, 0, length) : 0;
                    int to = k == endIndex ? Clamp(ec, 0, length) : length;
                    bool mark = k < endIndex || to >= length;
                    scopes[paragraph] = new RevisionService.Scope(from, to, mark);
                }

                scopeOf = paragraph => scopes.TryGetValue(paragraph, out var scope) ? scope : null;
                caretParagraph = startParagraph;
                caretChar = sc;
            }
            else
            {
                var paragraph = ParagraphAtSlice(_caretPara);
                if (paragraph is null) return false;

                if (RevisionService.HitAt(paragraph, _caretChar) is not { } hit)
                    return GoToRevision(forward: true);

                var scope = hit.IsMark
                    ? new RevisionService.Scope(hit.To, hit.To, true)
                    : new RevisionService.Scope(hit.From, hit.To, false);

                scopeOf = candidate => ReferenceEquals(candidate, paragraph) ? scope : null;
                caretParagraph = paragraph;
                caretChar = hit.From;
            }

            BeginTrackedSnapshot(accept ? "Принять исправление" : "Отклонить исправление");
            try
            {
                var (touched, structureChanged) = RevisionService.Resolve(doc, scopeOf, accept);
                RefreshAfterTrackedChange(touched, structureChanged);
                RestoreCaretAfterResolve(caretParagraph, caretChar);
            }
            finally
            {
                EndTrackedSnapshot();
            }

            GoToRevision(forward: true, fromCaret: true);
            return true;
        }

        /// <summary>
        /// Каретка после принятия или отклонения: в тот же абзац, если он остался,
        /// иначе — в начало документа.
        /// </summary>
        private void RestoreCaretAfterResolve(ParagraphBlock? paragraph, int charIndex)
        {
            if (DocVm is null) return;

            if (paragraph is not null && RevisionService.Locate(DocVm.Document, paragraph) is not null)
            {
                PlaceCaret(paragraph, Math.Min(charIndex, paragraph.TotalLength));
                return;
            }

            var order = RevisionService.ReadingOrder(DocVm.Document);
            if (order.Count > 0) PlaceCaret(order[0], 0);
        }

        /// <summary>
        /// Переход к следующей или предыдущей правке: правка выделяется (знак
        /// абзаца — каретка в конце абзаца), лист прокручивается к ней. В конце
        /// документа поиск продолжается с начала. Правки в спрятанных видом абзацах
        /// пропускаются.
        /// </summary>
        private bool GoToRevision(bool forward, bool fromCaret = false)
        {
            if (DocVm is null) return false;

            var order = RevisionService.ReadingOrder(DocVm.Document);
            if (order.Count == 0) return false;

            var hits = new List<(int ParagraphIndex, RevisionService.Hit Hit)>();
            for (int i = 0; i < order.Count; i++)
                foreach (var hit in RevisionService.HitsIn(order[i]))
                    hits.Add((i, hit));

            if (hits.Count == 0) return false;

            int currentParagraph;
            int threshold;

            if (HasSel() && !fromCaret)
            {
                var (sp, sc, _, _) = NormalizeSelection();
                var anchorParagraph = ParagraphAtSlice(sp);
                currentParagraph = anchorParagraph is null ? 0 : IndexOfReference(order, anchorParagraph);
                threshold = forward ? sc + 1 : sc - 1;
            }
            else
            {
                var paragraph = ParagraphAtSlice(_caretPara);
                currentParagraph = paragraph is null ? 0 : IndexOfReference(order, paragraph);
                threshold = forward ? _caretChar : _caretChar - 1;
            }

            if (currentParagraph < 0) currentParagraph = 0;

            int count = hits.Count;
            int first = -1;

            if (forward)
            {
                for (int i = 0; i < count; i++)
                {
                    var (pi, hit) = hits[i];
                    if (pi > currentParagraph || (pi == currentParagraph && hit.From >= threshold))
                    {
                        first = i;
                        break;
                    }
                }

                if (first < 0) first = 0;
            }
            else
            {
                for (int i = count - 1; i >= 0; i--)
                {
                    var (pi, hit) = hits[i];
                    if (pi < currentParagraph || (pi == currentParagraph && hit.From <= threshold))
                    {
                        first = i;
                        break;
                    }
                }

                if (first < 0) first = count - 1;
            }

            for (int step = 0; step < count; step++)
            {
                int index = forward ? (first + step) % count : (first - step + count) % count;
                if (SelectRevision(hits[index].Hit)) return true;
            }

            return false;
        }

        /// <summary>Выделяет правку на листе. False — её абзац сейчас не на листе.</summary>
        private bool SelectRevision(RevisionService.Hit hit)
        {
            int? startSlice = FindSliceFor(new LayoutPoint(hit.Paragraph, hit.From));
            int? endSlice = FindSliceFor(new LayoutPoint(hit.Paragraph, hit.To));
            if (startSlice is not int start || endSlice is not int end) return false;

            if (hit.From == hit.To)
            {
                _caretPara = end;
                _caretChar = hit.To;
                _caretLineHint = -1;
                SnapCaretToCorrectSlice();
                SyncSel();
            }
            else
            {
                _selStartPara = start;
                _selStartChar = hit.From;
                _selEndPara = end;
                _selEndChar = hit.To;
                _caretPara = end;
                _caretChar = hit.To;
                _caretLineHint = -1;
            }

            UpdatePreferredX();
            UpdateSelectionContext();
            ResetCaret();
            InvalidateFull();
            return true;
        }

        // ── Вид показа ────────────────────────────────────────────────────

        /// <summary>
        /// Вид показа исправлений сменился: раскладка собирается заново по новому
        /// правилу, каретка остаётся на своём месте или уходит из спрятанного.
        /// </summary>
        private void OnRevisionViewChanged()
        {
            if (DocVm is null) return;

            var anchor = CaptureCaretAnchor();

            _styleResolver = CreateStyleResolver();
            _layoutCache.Clear();
            _cellVmCache.Clear();
            InvalidateCellLayoutCaches();

            RefreshAfterTableCommand();
            ApplyCaretAnchor(anchor);
            NormalizeCaretOutOfHiddenText();
            UpdatePreferredX();
            InvalidateMeasure();
            InvalidateFull();
        }

        /// <summary>
        /// Абзацы, целиком спрятанные видом показа исправлений, добавляются к
        /// скрытым блокам раскладки — как разделы под свёрнутыми заголовками.
        /// </summary>
        private void MergeRevisionHiddenBlocks()
        {
            var docVm = DocVm;
            if (docVm is null) return;

            var view = docVm.Document.RevisionView;
            if (view == RevisionView.AllMarkup) return;

            HashSet<BlockModel>? hidden = null;

            foreach (var section in docVm.Document.Sections)
            {
                foreach (var block in section.Blocks)
                {
                    if (block is not ParagraphBlock paragraph) continue;
                    if (!RevisionDisplay.IsParagraphHidden(paragraph, view)) continue;

                    hidden ??= _collapsedBlocks ?? new HashSet<BlockModel>();
                    hidden.Add(paragraph);
                }
            }

            if (hidden is not null) _collapsedBlocks = hidden;
        }

        // ── Вырезание и вставка ───────────────────────────────────────────

        /// <summary>Вырезание под рецензированием: копия в буфер, выделенное отмечается удалённым.</summary>
        private async Task TrackedCutAsync()
        {
            await CopyAsync();
            RunTrackedSelectionDelete("Вырезание с исправлениями");
        }
    }
}
