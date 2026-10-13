using System;
using System.Collections.Generic;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.Models.Styles;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Правка текста из нескольких абзацев через простое окно без потери оформления.
    ///
    /// Окно показывает только буквы: абзацы через перевод строки. Новый текст
    /// сравнивается со старым, и меняется лишь отличающийся кусок между общим началом
    /// и общим концом. Знаки вне куска остаются со своим начертанием (жирный, курсив,
    /// цвет), объекты в строке — на своих местах; вставленные знаки берут начертание
    /// знака слева в том же абзаце, как при наборе с клавиатуры.
    /// </summary>
    public static class RichTextEdit
    {
        /// <summary>Знак текста или граница абзацев в сплошной записи текста.</summary>
        /// <param name="Cell">Знак с оформлением; у границы не используется.</param>
        /// <param name="IsBreak">Граница абзацев.</param>
        /// <param name="Source">
        /// Исходный абзац: у знака — абзац, где он стоял; у границы — абзац, который
        /// она начинает; −1 у новой границы, набранной в окне.
        /// </param>
        private readonly record struct Item(ParagraphBlock.CharCell Cell, bool IsBreak, int Source);

        /// <summary>
        /// Абзацы с новым текстом. Исходные абзацы не меняются: возвращаются копии,
        /// первая копия каждого абзаца сохраняет его Id.
        /// </summary>
        /// <param name="paragraphs">Абзацы до правки.</param>
        /// <param name="newText">Новый текст: абзацы через перевод строки.</param>
        /// <param name="fallbackRun">Начертание знаков, если взять его не у кого.</param>
        /// <param name="fallbackParagraph">Оформление абзаца, если исходных абзацев нет.</param>
        public static List<ParagraphBlock> Apply(
            IReadOnlyList<ParagraphBlock> paragraphs,
            string newText,
            RunProperties fallbackRun,
            ParagraphProperties fallbackParagraph)
        {
            newText = (newText ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');

            var items = new List<Item>();
            var emptyProps = new RunProperties?[paragraphs.Count];

            for (int k = 0; k < paragraphs.Count; k++)
            {
                if (k > 0) items.Add(new Item(default, true, k));

                var cells = paragraphs[k].ToCharCells();
                foreach (var cell in cells)
                    items.Add(new Item(cell, false, k));

                if (cells.Count == 0) emptyProps[k] = FirstRunProperties(paragraphs[k]);
            }

            int oldLength = items.Count;
            int newLength = newText.Length;

            int prefix = 0;
            while (prefix < oldLength && prefix < newLength && CharOf(items[prefix]) == newText[prefix])
                prefix++;

            int suffix = 0;
            while (suffix < oldLength - prefix && suffix < newLength - prefix
                   && CharOf(items[oldLength - 1 - suffix]) == newText[newLength - 1 - suffix])
                suffix++;

            int contextParagraph = 0;
            for (int i = 0; i < prefix; i++)
                if (items[i].IsBreak) contextParagraph = items[i].Source;

            var insertProps = InsertProperties(items, prefix, oldLength - suffix, emptyProps, contextParagraph)
                              ?? fallbackRun;
            if (insertProps.HasRevision) insertProps = insertProps.WithoutRevisions();

            var result = new List<Item>(prefix + (newLength - prefix - suffix) + suffix);
            for (int i = 0; i < prefix; i++) result.Add(items[i]);

            for (int i = prefix; i < newLength - suffix; i++)
            {
                char ch = newText[i];
                result.Add(ch == '\n'
                    ? new Item(default, true, -1)
                    : new Item(new ParagraphBlock.CharCell(ch, insertProps), false, contextParagraph));
            }

            for (int i = oldLength - suffix; i < oldLength; i++) result.Add(items[i]);

            var output = new List<ParagraphBlock>();
            var used = new HashSet<int>();
            var current = new List<ParagraphBlock.CharCell>();
            int source = 0;

            void Flush()
            {
                output.Add(MakeParagraph(paragraphs, source, current, used, emptyProps, insertProps, fallbackParagraph));
                current = new List<ParagraphBlock.CharCell>();
            }

            foreach (var item in result)
            {
                if (!item.IsBreak)
                {
                    current.Add(item.Cell);
                    continue;
                }

                Flush();
                source = item.Source >= 0 ? item.Source : contextParagraph;
            }

            Flush();
            return output;
        }

        private static char CharOf(Item item) => item.IsBreak ? '\n' : item.Cell.Ch;

        /// <summary>
        /// Начертание вставляемых знаков. Заменённый кусок отдаёт начертание своего
        /// первого знака — как набор поверх выделенного слова у Word; чистая вставка —
        /// знака слева в том же абзаце, иначе знака справа, иначе пустого абзаца, где
        /// идёт набор. Объекты в строке (знак сноски, картинка) начертание не дают.
        /// </summary>
        private static RunProperties? InsertProperties(
            List<Item> items, int from, int to, RunProperties?[] emptyProps, int contextParagraph)
        {
            if (from < to && !items[from].IsBreak && !items[from].Cell.IsInlineObject && items[from].Cell.Props is { } replaced)
                return replaced;

            for (int i = from - 1; i >= 0; i--)
            {
                if (items[i].IsBreak) break;
                if (items[i].Cell.IsInlineObject || items[i].Cell.Props is null) continue;
                return items[i].Cell.Props;
            }

            for (int i = to; i < items.Count; i++)
            {
                if (items[i].IsBreak) break;
                if (items[i].Cell.IsInlineObject || items[i].Cell.Props is null) continue;
                return items[i].Cell.Props;
            }

            if (contextParagraph >= 0 && contextParagraph < emptyProps.Length && emptyProps[contextParagraph] is { } empty)
                return empty;

            foreach (var item in items)
                if (!item.IsBreak && !item.Cell.IsInlineObject && item.Cell.Props is not null)
                    return item.Cell.Props;

            return null;
        }

        private static RunProperties? FirstRunProperties(ParagraphBlock paragraph)
        {
            foreach (var chunk in paragraph.Chunks)
                foreach (var run in chunk.Runs)
                    if (run.Properties is not null && !run.IsInlineObject)
                        return run.Properties;
            return null;
        }

        /// <summary>
        /// Абзац результата: копия исходного абзаца с новыми знаками. Первая копия
        /// сохраняет Id исходного, следующие (абзац разбит в окне) получают свои.
        /// </summary>
        private static ParagraphBlock MakeParagraph(
            IReadOnlyList<ParagraphBlock> paragraphs,
            int source,
            List<ParagraphBlock.CharCell> cells,
            HashSet<int> used,
            RunProperties?[] emptyProps,
            RunProperties insertProps,
            ParagraphProperties fallbackParagraph)
        {
            ParagraphBlock paragraph;

            if (source >= 0 && source < paragraphs.Count)
            {
                paragraph = (ParagraphBlock)DocumentCloner.CloneBlock(paragraphs[source]);
                if (!used.Add(source)) paragraph.Id = Guid.NewGuid();
            }
            else
            {
                paragraph = new ParagraphBlock { Properties = fallbackParagraph.Clone() };
            }

            paragraph.RebuildFromCharCells(cells);

            if (cells.Count == 0)
            {
                // Пустой абзац хранит начертание в пустом ране: с него продолжится набор.
                var props = source >= 0 && source < emptyProps.Length && emptyProps[source] is { } empty
                    ? empty
                    : insertProps;

                foreach (var chunk in paragraph.Chunks)
                    foreach (var run in chunk.Runs)
                        run.Properties = props.Clone();
            }

            return paragraph;
        }
    }
}
