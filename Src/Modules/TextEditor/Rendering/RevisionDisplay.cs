using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.Models.Styles;

namespace Writersword.Modules.TextEditor.Rendering
{
    /// <summary>
    /// Что из правок рецензирования видно на листе при выбранном виде
    /// (<see cref="RevisionView"/>). Одни правила на экран, печать и PDF.
    ///
    ///   «Все исправления» — видно всё: вставленное подчёркнуто, удалённое
    ///   зачёркнуто, цветом автора; у строк с правками на поле черта.
    ///   «Простая разметка» и «Без исправлений» — текст как после принятия всех
    ///   правок: удалённого нет; у первой на поле красная черта.
    ///   «Исходный документ» — текст как до правок: вставленного нет, у сменивших
    ///   оформление фрагментов и абзацев — прежнее оформление.
    ///
    /// Спрятанное правкой остаётся в абзаце и не занимает места, как скрытый текст:
    /// каретка его перешагивает.
    /// </summary>
    public static class RevisionDisplay
    {
        /// <summary>Пометки правок (подчёркивание, зачёркивание, цвет автора) рисуются.</summary>
        public static bool ShowsMarkup(RevisionView view) => view == RevisionView.AllMarkup;

        /// <summary>У строк с правками на поле рисуется черта.</summary>
        public static bool ShowsChangeBars(RevisionView view)
            => view == RevisionView.AllMarkup || view == RevisionView.SimpleMarkup;

        /// <summary>Вид показывает текст как до правок.</summary>
        public static bool ShowsOriginal(RevisionView view) => view == RevisionView.Original;

        /// <summary>Фрагмент спрятан правкой при этом виде.</summary>
        public static bool IsRunHidden(RunProperties? props, RevisionView view)
        {
            if (props is null) return false;

            return view switch
            {
                RevisionView.Original => props.Inserted is not null,
                RevisionView.SimpleMarkup or RevisionView.NoMarkup => props.Deleted is not null,
                _ => false
            };
        }

        /// <summary>Знак абзаца спрятан правкой при этом виде.</summary>
        public static bool IsMarkHidden(ParagraphProperties props, RevisionView view)
        {
            return view switch
            {
                RevisionView.Original => props.MarkInserted is not null,
                RevisionView.SimpleMarkup or RevisionView.NoMarkup => props.MarkDeleted is not null,
                _ => false
            };
        }

        /// <summary>
        /// Абзац спрятан целиком: его знак абзаца спрятан правкой и ни одного видимого
        /// знака в нём не осталось. Так выглядит удалённый целиком абзац в «Без
        /// исправлений» и вставленный целиком — в «Исходном документе».
        /// </summary>
        public static bool IsParagraphHidden(ParagraphBlock paragraph, RevisionView view)
        {
            if (view == RevisionView.AllMarkup) return false;
            if (!IsMarkHidden(paragraph.Properties, view)) return false;

            foreach (var chunk in paragraph.Chunks)
                foreach (var run in chunk.Runs)
                {
                    if (string.IsNullOrEmpty(run.Text)) continue;
                    if (!IsRunHidden(run.Properties, view)) return false;
                }

            return true;
        }

        /// <summary>
        /// Свойства фрагмента для показа: в «Исходном документе» — оформление до
        /// правки, если оно менялось. Отметки правки сохраняются.
        /// </summary>
        public static RunProperties? DisplayProperties(RunProperties? props, RevisionView view)
        {
            if (props?.FormatChange is not { } change || view != RevisionView.Original) return props;

            var previous = change.Previous?.Clone() ?? new RunProperties();
            previous.Inserted = props.Inserted;
            previous.Deleted = props.Deleted;
            previous.FormatChange = props.FormatChange;
            return previous;
        }

        /// <summary>
        /// Абзац для вёрстки: в «Исходном документе» абзац, сменивший оформление,
        /// верстается с прежним. Подставной абзац делит с настоящим текст и список —
        /// меняется только оформление.
        /// </summary>
        public static ParagraphBlock DisplayParagraph(ParagraphBlock paragraph, RevisionView view)
        {
            if (view != RevisionView.AllMarkup
                && IsMarkHidden(paragraph.Properties, view)
                && Joins.TryGetValue(paragraph, out var joined)
                && joined.Length > 0)
                return JoinedParagraph(paragraph, joined, view);

            return WithPreviousFormatting(paragraph, view);
        }

        private static ParagraphBlock WithPreviousFormatting(ParagraphBlock paragraph, RevisionView view)
        {
            if (view != RevisionView.Original
                || paragraph.Properties.FormatChange?.Previous is not { } previous)
                return paragraph;

            var properties = previous.Clone();
            properties.MarkInserted = paragraph.Properties.MarkInserted;
            properties.MarkDeleted = paragraph.Properties.MarkDeleted;
            properties.FormatChange = paragraph.Properties.FormatChange;

            return new ParagraphBlock
            {
                Id = paragraph.Id,
                Chunks = paragraph.Chunks,
                Properties = properties,
                ListProperties = paragraph.ListProperties,
                SuppressSpaceBefore = paragraph.SuppressSpaceBefore,
                SuppressSpaceAfter = paragraph.SuppressSpaceAfter
            };
        }

        // Абзацы, к которым при выбранном виде подклеены следующие: знак абзаца между
        // ними спрятан правкой. И абзацы, ушедшие внутрь предыдущего.
        private static readonly ConditionalWeakTable<ParagraphBlock, ParagraphBlock[]> Joins = new();
        private static readonly ConditionalWeakTable<ParagraphBlock, object> Absorbed = new();
        private static readonly object AbsorbedMark = new();

        /// <summary>
        /// Размечает склейку абзацев для вида показа: абзац, чей знак спрятан правкой
        /// (вставленный знак — в «Исходном документе», удалённый — в «Простой разметке»
        /// и «Без исправлений»), продолжается текстом следующего абзаца, как у Word.
        /// Склеенный абзац берёт оформление последнего знака — того, что остался.
        /// Вызывается перед раскладкой при каждой смене вида или текста.
        /// </summary>
        public static void PrepareJoins(DocumentModel? document, RevisionView view)
        {
            if (document is null) return;

            foreach (var section in document.Sections)
            {
                var blocks = section.Blocks;
                for (int i = 0; i < blocks.Count; i++)
                {
                    if (blocks[i] is not ParagraphBlock paragraph) continue;

                    Joins.Remove(paragraph);
                    Absorbed.Remove(paragraph);
                }
            }

            if (view == RevisionView.AllMarkup) return;

            foreach (var section in document.Sections)
            {
                var blocks = section.Blocks;
                for (int i = 0; i < blocks.Count; i++)
                {
                    if (blocks[i] is not ParagraphBlock paragraph) continue;
                    if (Absorbed.TryGetValue(paragraph, out _)) continue;
                    if (!IsMarkHidden(paragraph.Properties, view)) continue;
                    if (IsParagraphHidden(paragraph, view)) continue;

                    var chain = new List<ParagraphBlock>();
                    int j = i + 1;
                    while (j < blocks.Count && blocks[j] is ParagraphBlock next)
                    {
                        chain.Add(next);
                        Absorbed.AddOrUpdate(next, AbsorbedMark);
                        if (!IsMarkHidden(next.Properties, view)) break;
                        j++;
                    }

                    if (chain.Count > 0) Joins.AddOrUpdate(paragraph, chain.ToArray());
                }
            }
        }

        /// <summary>Абзац ушёл внутрь предыдущего: знак абзаца перед ним спрятан правкой.</summary>
        public static bool IsAbsorbed(ParagraphBlock paragraph) => Absorbed.TryGetValue(paragraph, out _);

        /// <summary>
        /// Абзац для показа вместе с подклеенными: свой текст, затем текст следующих
        /// абзацев (отметкой «только для показа»), оформление — последнего знака абзаца.
        /// </summary>
        private static ParagraphBlock JoinedParagraph(ParagraphBlock paragraph, ParagraphBlock[] joined, RevisionView view)
        {
            var last = WithPreviousFormatting(joined[^1], view);
            var own = WithPreviousFormatting(paragraph, view);

            var chunks = new List<TextChunk>(own.Chunks);
            foreach (var next in joined)
            {
                var tail = new TextChunk();
                tail.Runs.Clear();
                foreach (var chunk in next.Chunks)
                {
                    foreach (var run in chunk.Runs)
                    {
                        if (string.IsNullOrEmpty(run.Text)) continue;
                        tail.Runs.Add(new Models.Inline.RunModel
                        {
                            Id = run.Id,
                            Text = run.Text,
                            Properties = run.Properties,
                            InlineImageId = run.InlineImageId,
                            DisplayTail = true
                        });
                    }
                }

                if (tail.Runs.Count == 0) continue;
                tail.InvalidateLength();
                chunks.Add(tail);
            }

            return new ParagraphBlock
            {
                Id = paragraph.Id,
                Chunks = chunks,
                Properties = last.Properties,
                ListProperties = last.ListProperties,
                SuppressSpaceBefore = own.SuppressSpaceBefore,
                SuppressSpaceAfter = last.SuppressSpaceAfter
            };
        }

        /// <summary>В абзаце есть хоть одна правка — фрагмента, знака абзаца или оформления.</summary>
        public static bool HasRevisions(ParagraphBlock paragraph)
        {
            if (paragraph.Properties.HasRevision) return true;

            foreach (var chunk in paragraph.Chunks)
                foreach (var run in chunk.Runs)
                    if (run.Properties?.HasRevision == true)
                        return true;

            return false;
        }
    }
}
