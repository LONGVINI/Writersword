using System;
using System.Collections.Generic;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Rendering
{
    /// <summary>
    /// Правило «не добавлять интервал между абзацами одного стиля» (w:contextualSpacing).
    ///
    /// Правило действует со стороны самого абзаца: включив его, абзац отказывается от
    /// своего интервала до, если выше стоит абзац того же стиля, и от интервала после,
    /// если такой же стоит ниже. Интервалы соседа при этом не трогаются — сосед решает
    /// за себя сам. Так Word держит пункты списка вплотную друг к другу, а от окружающего
    /// текста отделяет их обычными интервалами.
    ///
    /// Соседство — это абзацы подряд в одном потоке. Таблица поток прерывает: абзац над
    /// ней и абзац под ней соседями не считаются. Разрыв страницы соседства не рвёт — в
    /// Word он знак внутри абзаца, и абзацы по обе стороны остаются рядом. Абзацы ячейки
    /// — свой поток, отдельный для каждой ячейки.
    ///
    /// Вывод кладётся в абзац (<see cref="ParagraphBlock.SuppressSpaceBefore"/>,
    /// <see cref="ParagraphBlock.SuppressSpaceAfter"/>) — так же, как текст маркера
    /// списка: раскладка абзаца соседей не видит и берёт готовое.
    /// Чистый проход без состояния — вызывается перед каждой раскладкой.
    /// </summary>
    public static class ContextualSpacingRules
    {
        /// <summary>
        /// Выставляет снятие интервалов всем абзацам набора блоков и абзацам ячеек его
        /// таблиц.
        /// </summary>
        /// <param name="contextualOf">
        /// Включено ли правило у абзаца — с учётом его стиля.
        /// </param>
        /// <returns>У какого-то абзаца снятие поменялось.</returns>
        public static bool Apply(
            IReadOnlyList<BlockModel> blocks,
            Func<ParagraphBlock, bool> contextualOf)
        {
            if (blocks is null || contextualOf is null) return false;

            // Поток абзацев; null — разрыв соседства (таблица или иной блок в потоке).
            var flow = new List<ParagraphBlock?>(blocks.Count);
            List<TableBlock>? tables = null;

            foreach (var block in blocks)
            {
                switch (block)
                {
                    case ParagraphBlock paragraph:
                        flow.Add(paragraph);
                        break;

                    case BreakBlock:
                        break;

                    case TableBlock table:
                        flow.Add(null);
                        (tables ??= new List<TableBlock>()).Add(table);
                        break;

                    default:
                        flow.Add(null);
                        break;
                }
            }

            bool changed = ApplyFlow(flow, contextualOf);

            if (tables is not null)
            {
                foreach (var table in tables)
                {
                    foreach (var cell in table.Cells)
                    {
                        if (cell?.Paragraphs is not { Count: > 0 } paragraphs) continue;

                        var cellFlow = new List<ParagraphBlock?>(paragraphs.Count);
                        foreach (var paragraph in paragraphs) cellFlow.Add(paragraph);

                        changed |= ApplyFlow(cellFlow, contextualOf);
                    }
                }
            }

            return changed;
        }

        private static bool ApplyFlow(
            List<ParagraphBlock?> flow,
            Func<ParagraphBlock, bool> contextualOf)
        {
            bool changed = false;

            ParagraphBlock? prev = null;
            bool prevContextual = false;

            foreach (var paragraph in flow)
            {
                if (paragraph is null)
                {
                    if (prev is not null) changed |= SetAfter(prev, false);
                    prev = null;
                    prevContextual = false;
                    continue;
                }

                bool contextual = contextualOf(paragraph);
                bool sameStyle = prev is not null && SameStyle(prev, paragraph);

                changed |= SetBefore(paragraph, contextual && sameStyle);
                if (prev is not null) changed |= SetAfter(prev, prevContextual && sameStyle);

                prev = paragraph;
                prevContextual = contextual;
            }

            if (prev is not null) changed |= SetAfter(prev, false);

            return changed;
        }

        /// <summary>
        /// Один ли стиль у абзацев. Абзац без стиля носит стиль по умолчанию, и с абзацем,
        /// где тот же стиль назван явно, у него стиль общий. Имена сравниваются без учёта
        /// регистра — так же их ищет <see cref="StyleResolver"/>.
        /// </summary>
        private static bool SameStyle(ParagraphBlock a, ParagraphBlock b)
            => string.Equals(
                StyleKey(a.Properties?.StyleName),
                StyleKey(b.Properties?.StyleName),
                StringComparison.OrdinalIgnoreCase);

        private static string StyleKey(string? styleName)
            => string.IsNullOrWhiteSpace(styleName) ? StyleResolver.DefaultStyleName : styleName;

        private static bool SetBefore(ParagraphBlock paragraph, bool value)
        {
            if (paragraph.SuppressSpaceBefore == value) return false;
            paragraph.SuppressSpaceBefore = value;
            return true;
        }

        private static bool SetAfter(ParagraphBlock paragraph, bool value)
        {
            if (paragraph.SuppressSpaceAfter == value) return false;
            paragraph.SuppressSpaceAfter = value;
            return true;
        }
    }
}
