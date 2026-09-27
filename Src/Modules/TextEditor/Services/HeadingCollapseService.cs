using System;
using System.Collections.Generic;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Свёртывание разделов под заголовками — как стрелка у заголовка в Word.
    ///
    /// Раздел заголовка — всё, что идёт за ним до следующего заголовка того же или
    /// более высокого уровня (меньшего числа): абзацы, таблицы, картинки, разрывы.
    /// Заголовок третьего уровня внутри раздела второго уровня уходит вместе с ним.
    ///
    /// Свёртывание — только вид. Документ не меняется: скрытое остаётся в модели,
    /// попадает в сохранение, печать и выгрузку, а в выделение и удаление — если
    /// выделение захватило свёрнутый заголовок, как и в Word.
    ///
    /// Заголовком считается абзац с уровнем структуры — тем же, по которому строятся
    /// навигатор и оглавление (<see cref="TocService.LevelOf"/>), без ручной пометки
    /// «взять в оглавление»: она отвечает на вопрос «брать ли в оглавление», а не
    /// делает абзац главой.
    /// </summary>
    public static class HeadingCollapseService
    {
        /// <summary>Уровень заголовка у абзаца: 1…9, 0 — обычный текст.</summary>
        public static int LevelOf(ParagraphBlock para, DocumentModel doc)
            => TocService.LevelOf(para, doc, includeManual: false);

        /// <summary>
        /// Скрытые блоки и заголовок, который их скрывает. Если свёрнуты и внешний
        /// заголовок, и вложенный в его раздел, блок числится за внешним: развернуть
        /// нужно сперва его, вложенный при этом остаётся свёрнутым.
        /// </summary>
        public static Dictionary<BlockModel, Guid> HiddenBlocks(
            DocumentModel doc, IReadOnlyCollection<Guid> collapsed)
        {
            var result = new Dictionary<BlockModel, Guid>();
            if (doc is null || collapsed is null || collapsed.Count == 0) return result;
            if (doc.Sections.Count == 0) return result;

            var set = collapsed as ISet<Guid> ?? new HashSet<Guid>(collapsed);
            var blocks = doc.Sections[0].Blocks;

            int hidingLevel = 0;
            Guid owner = Guid.Empty;

            foreach (var block in blocks)
            {
                if (hidingLevel > 0)
                {
                    // Раздел кончается на заголовке того же или более высокого уровня.
                    if (block is ParagraphBlock para)
                    {
                        int level = LevelOf(para, doc);
                        if (level > 0 && level <= hidingLevel) hidingLevel = 0;
                    }

                    if (hidingLevel > 0)
                    {
                        result[block] = owner;
                        continue;
                    }
                }

                if (block is ParagraphBlock heading && set.Contains(heading.Id))
                {
                    int level = LevelOf(heading, doc);
                    if (level > 0)
                    {
                        hidingLevel = level;
                        owner = heading.Id;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Опознаватели из списка, которые больше не принадлежат заголовку: абзац
        /// удалён или перестал быть заголовком. Их свёртка ничего не значит.
        /// </summary>
        public static List<Guid> StaleHeadings(DocumentModel doc, IReadOnlyCollection<Guid> collapsed)
        {
            var stale = new List<Guid>();
            if (collapsed is null || collapsed.Count == 0) return stale;

            var alive = new HashSet<Guid>();
            if (doc is not null && doc.Sections.Count > 0)
            {
                foreach (var block in doc.Sections[0].Blocks)
                {
                    if (block is ParagraphBlock para && LevelOf(para, doc) > 0)
                        alive.Add(para.Id);
                }
            }

            foreach (var id in collapsed)
                if (!alive.Contains(id)) stale.Add(id);

            return stale;
        }

        /// <summary>
        /// Раздел заголовка в списке блоков: [start, end). Пустой диапазон — абзац не
        /// заголовок или под ним сразу идёт заголовок того же уровня.
        /// </summary>
        public static (int Start, int End) SectionRange(DocumentModel doc, ParagraphBlock heading)
        {
            if (doc is null || heading is null || doc.Sections.Count == 0) return (0, 0);

            var blocks = doc.Sections[0].Blocks;
            int headingIndex = blocks.IndexOf(heading);
            if (headingIndex < 0) return (0, 0);

            int level = LevelOf(heading, doc);
            if (level <= 0) return (headingIndex + 1, headingIndex + 1);

            int end = headingIndex + 1;
            for (; end < blocks.Count; end++)
            {
                if (blocks[end] is ParagraphBlock para)
                {
                    int l = LevelOf(para, doc);
                    if (l > 0 && l <= level) break;
                }
            }

            return (headingIndex + 1, end);
        }
    }
}
