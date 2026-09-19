using System;
using System.Collections.Generic;
using System.Text;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Styles;
using Writersword.Modules.TextEditor.Models.Toc;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Всё, что программа знает о заголовках рукописи: кто из абзацев заголовок,
    /// какого он уровня и на какой странице лежит.
    ///
    /// Сервис ничего не хранит и ничего не меняет — он только читает документ и
    /// отвечает списком. Одним и тем же ответом пользуются и навигатор сбоку, и
    /// оглавление в тексте: две разные правды о структуре книги расходились бы
    /// ровно в тот момент, когда человек на них смотрит.
    /// </summary>
    public static class TocService
    {
        /// <summary>Приставка встроенных стилей заголовков: Heading1 … Heading9.</summary>
        private const string HeadingStylePrefix = "Heading";

        /// <summary>Глубина подъёма по цепочке BasedOn при опознании пользовательского стиля.</summary>
        private const int MaxStyleChainDepth = 8;

        // ── Уровень абзаца ────────────────────────────────────────────────

        /// <summary>
        /// Уровень заголовка у абзаца: 1 — глава, 9 — самый глубокий подзаголовок,
        /// 0 — обычный текст.
        ///
        /// Порядок опознания важен и выбран так, чтобы человек мог переспорить стиль.
        /// Сперва смотрится структурный уровень абзаца: его ставят вручную именно тогда,
        /// когда нужна глава без вида главы — эпиграф, интерлюдия, письмо посреди текста.
        /// Затем — стиль абзаца, включая пользовательский, унаследованный от заголовка.
        /// Ручная пометка «взять в оглавление» стоит последней и даёт первый уровень:
        /// она отвечает на вопрос «брать ли», а не «насколько глубоко».
        /// </summary>
        public static int LevelOf(ParagraphBlock para, DocumentModel doc, bool includeManual)
        {
            if (para is null) return 0;

            var props = para.Properties;

            // Строка самого оглавления заголовком не считается — иначе оглавление
            // при следующей пересборке попало бы в себя же.
            if (props.TocOwnerId.HasValue) return 0;

            if (props.OutlineLevel >= 1 && props.OutlineLevel <= 9)
                return props.OutlineLevel;

            int byStyle = LevelOfStyle(props.StyleName, doc);
            if (byStyle > 0) return byStyle;

            if (includeManual && props.IncludeInToc) return 1;

            return 0;
        }

        /// <summary>
        /// Уровень по имени стиля. Пользовательский стиль опознаётся по цепочке BasedOn:
        /// «Название главы», основанное на Heading1, остаётся главой.
        /// </summary>
        public static int LevelOfStyle(string? styleName, DocumentModel doc)
        {
            if (string.IsNullOrEmpty(styleName)) return 0;

            string? name = styleName;

            for (int depth = 0; depth < MaxStyleChainDepth && !string.IsNullOrEmpty(name); depth++)
            {
                int level = BuiltInHeadingLevel(name!);
                if (level > 0) return level;

                var style = doc?.FindStyle(name!);
                if (style is null) return 0;
                name = style.BasedOn;
            }

            return 0;
        }

        /// <summary>Разбирает имя вида Heading3 в число 3. Ноль — имя не заголовочное.</summary>
        private static int BuiltInHeadingLevel(string styleName)
        {
            if (!styleName.StartsWith(HeadingStylePrefix, StringComparison.Ordinal))
                return 0;

            if (styleName.Length != HeadingStylePrefix.Length + 1)
                return 0;

            char digit = styleName[HeadingStylePrefix.Length];
            if (digit < '1' || digit > '9') return 0;

            return digit - '0';
        }

        // ── Сбор заголовков ───────────────────────────────────────────────

        /// <summary>
        /// Все заголовки рукописи в порядке следования, плоским списком.
        ///
        /// Индекс абзаца считается по тому же правилу, по которому строится
        /// DocumentViewModel.Paragraphs: только абзацы верхнего уровня первого раздела,
        /// в порядке блоков. Иначе переход по клику уводил бы не туда.
        ///
        /// Абзацы ячеек таблиц и надписей заголовками не считаются намеренно: строка
        /// таблицы не глава, и в оглавлении ей делать нечего.
        /// </summary>
        /// <param name="doc">Документ.</param>
        /// <param name="includeManual">Брать абзацы с ручной пометкой.</param>
        /// <param name="pageMap">Карта «абзац — номер страницы» от раскладки. Null — номера неизвестны.</param>
        public static List<TocHeading> Collect(
            DocumentModel doc,
            bool includeManual = true,
            IReadOnlyDictionary<Guid, int>? pageMap = null)
        {
            var result = new List<TocHeading>();
            if (doc is null || doc.Sections.Count == 0) return result;

            var blocks = doc.Sections[0].Blocks;
            int paragraphIndex = 0;

            for (int i = 0; i < blocks.Count; i++)
            {
                if (blocks[i] is not ParagraphBlock para) continue;

                int level = LevelOf(para, doc, includeManual);
                if (level > 0)
                {
                    string text = OneLineText(para);
                    if (text.Length > 0)
                    {
                        var heading = new TocHeading(para.Id, text, level, paragraphIndex);
                        if (pageMap is not null && pageMap.TryGetValue(para.Id, out int page))
                            heading.PageNumber = page;
                        result.Add(heading);
                    }
                }

                paragraphIndex++;
            }

            return result;
        }

        /// <summary>
        /// Только те заголовки, что подходят одному оглавлению по диапазону уровней.
        /// </summary>
        public static List<TocHeading> Collect(
            DocumentModel doc,
            TocSettings settings,
            IReadOnlyDictionary<Guid, int>? pageMap = null)
        {
            var all = Collect(doc, settings?.IncludeManualEntries ?? true, pageMap);
            if (settings is null) return all;

            var result = new List<TocHeading>(all.Count);
            foreach (var heading in all)
                if (settings.AcceptsLevel(heading.Level))
                    result.Add(heading);

            return result;
        }

        /// <summary>
        /// Собирает плоский список в дерево по уровням — для навигатора.
        ///
        /// Пропуски уровней не выпрямляются: если за главой сразу идёт заголовок
        /// четвёртого уровня, он и станет ребёнком главы. Придумывать несуществующие
        /// промежуточные узлы значило бы показать человеку структуру, которой он не писал.
        /// </summary>
        public static List<TocHeading> BuildTree(IReadOnlyList<TocHeading> flat)
        {
            var roots = new List<TocHeading>();
            if (flat is null || flat.Count == 0) return roots;

            var stack = new List<TocHeading>();

            foreach (var heading in flat)
            {
                // Список заголовков переживает несколько построений дерева подряд (отбор
                // по уровню, поиск), и без сброса дети накапливались бы от прошлых раз.
                heading.Children.Clear();

                while (stack.Count > 0 && stack[stack.Count - 1].Level >= heading.Level)
                    stack.RemoveAt(stack.Count - 1);

                if (stack.Count == 0) roots.Add(heading);
                else stack[stack.Count - 1].Children.Add(heading);

                stack.Add(heading);
            }

            return roots;
        }

        // ── Сборка самого оглавления ──────────────────────────────────────

        /// <summary>Пунктов в миллиметре: размеры страницы хранятся в мм, вёрстка считает в пунктах.</summary>
        private const double MmToPt = 72.0 / 25.4;

        /// <summary>
        /// Абзацы, из которых состоит оглавление: название и по строке на заголовок.
        ///
        /// Оглавление — не особый вид блока, а обычные абзацы с пометкой, чьи они. Из этого
        /// следует всё остальное: они печатаются, экспортируются, ищутся, правятся и
        /// отменяются тем же кодом, что и рукопись, и заводить под них ничего не пришлось.
        /// Отличает их только TocOwnerId — по нему пересборка находит своё и сносит ровно его.
        ///
        /// Номер страницы прижимается к правому краю позицией табуляции с точечным
        /// заполнителем, как это устроено и в Word. Ни пробелов, ни таблицы здесь нет.
        /// </summary>
        /// <param name="settings">Настройки оглавления.</param>
        /// <param name="headings">Заголовки, уже отобранные по уровням, с номерами страниц.</param>
        /// <param name="textWidthPt">Ширина текстовой области страницы в пунктах.</param>
        /// <param name="title">Название над списком.</param>
        public static List<ParagraphBlock> BuildParagraphs(
            TocSettings settings,
            IReadOnlyList<TocHeading> headings,
            double textWidthPt,
            string title)
        {
            var result = new List<ParagraphBlock>();
            if (settings is null) return result;

            // Своё название списка старше присланного: присланное — это значение по
            // умолчанию, а в настройках лежит то, что человек вписал сам. Без этого
            // пересборка стирала его название и возвращала «Оглавление».
            string headingText = string.IsNullOrWhiteSpace(settings.Title) ? title : settings.Title;

            if (settings.ShowTitle && !string.IsNullOrWhiteSpace(headingText))
            {
                var head = new ParagraphBlock();
                head.SetPlainText(headingText);
                head.Properties.StyleName = "TocTitle";
                head.Properties.Alignment = TextAlignment.Center;
                head.Properties.TocOwnerId = settings.Id;
                head.Properties.TocEntryLevel = 0;
                result.Add(head);
            }

            foreach (var heading in headings)
                result.Add(BuildEntry(settings, heading, textWidthPt));

            return result;
        }

        /// <summary>
        /// Имя стиля для строки этого уровня: Toc1 … Toc9.
        ///
        /// Отдельные стили нужны не ради красоты. Пока строка оглавления носит стиль
        /// обычного текста, она обычным текстом и выглядит — и человек справедливо не
        /// понимает, где кончается оглавление и начинается книга. Со своим стилем её
        /// вид правится один раз на весь документ, как в Word.
        /// </summary>
        public static string TocStyleName(int level)
        {
            int clamped = level < 1 ? 1 : (level > 9 ? 9 : level);
            return "Toc" + clamped.ToString();
        }

        /// <summary>Одна строка оглавления.</summary>
        private static ParagraphBlock BuildEntry(
            TocSettings settings, TocHeading heading, double textWidthPt)
        {
            var para = new ParagraphBlock();

            bool withPage = settings.ShowPageNumbers && heading.PageNumber > 0;

            para.SetPlainText(withPage
                ? heading.Text + "\t" + heading.PageNumber.ToString()
                : heading.Text);

            var props = para.Properties;

            props.TocOwnerId = settings.Id;
            props.TocEntryLevel = heading.Level;
            props.TocTargetBlockId = heading.BlockId;
            props.StyleName = TocStyleName(heading.Level);
            props.Alignment = TextAlignment.Left;
            props.SpaceBefore = heading.Level == settings.MinLevel ? 4 : 0;
            props.SpaceAfter = 2;

            // Строку оглавления держат вместе со следующей: разорванное между листами
            // оглавление читается как две разных таблицы содержания.
            props.KeepWithNext = true;

            // Отступ по уровню. Красной строки здесь нет намеренно: она разрывала бы
            // столбик названий, по которому глаз и ведёт сверху вниз.
            double indent = settings.IndentByLevel
                ? Math.Max(heading.Level - settings.MinLevel, 0) * settings.LevelIndentPt
                : 0;

            props.LeftIndent = indent;
            props.FirstLineIndent = 0;
            props.RightIndent = 0;

            // Позиция отсчитывается от левого края текста абзаца, а он уже сдвинут
            // отступом уровня: без вычитания номера подглав уехали бы за правое поле.
            ApplyEntryTabStop(props, settings, indent, textWidthPt, withPage);

            return para;
        }

        /// <summary>
        /// Где в потоке документа лежит оглавление: первый его абзац и сколько их всего.
        /// (-1, 0) — оглавления с таким опознавателем в рукописи нет.
        ///
        /// Ищется непрерывный кусок: строки оглавления идут подряд, и разорвать их может
        /// только человек, вставив что-то в середину. Тогда пересборка заменит первую
        /// часть, а остаток останется на месте — это видно и поправимо, в отличие от
        /// молчаливой чистки всего, что где-то помечено.
        /// </summary>
        public static (int Start, int Count) FindRange(DocumentModel doc, Guid tocId)
        {
            if (doc is null || doc.Sections.Count == 0) return (-1, 0);

            var blocks = doc.Sections[0].Blocks;
            int start = -1;
            int count = 0;

            for (int i = 0; i < blocks.Count; i++)
            {
                bool mine = blocks[i] is ParagraphBlock p && p.Properties.TocOwnerId == tocId;

                if (mine)
                {
                    if (start < 0) start = i;
                    count++;
                }
                else if (start >= 0)
                {
                    break;
                }
            }

            return (start, count);
        }

        /// <summary>
        /// Дописывает документу встроенные стили, которых в нём ещё нет.
        ///
        /// Рукопись, начатая до появления стилей оглавления, их не содержит: список
        /// стилей записан в файл целиком и при открытии не пополняется. Без этого
        /// строки оглавления ссылались бы на стиль, которого в документе нет, и вышли
        /// бы обычным текстом — ровно то, чего от них не ждут.
        ///
        /// Существующие стили не трогаются: человек мог их править под себя.
        /// </summary>
        /// <returns>
        /// true — список стилей пополнился, и об этом нужно уведомить полотно.
        ///
        /// Ответ здесь не для порядка. Уведомление о смене стилей заставляет полотно
        /// собрать резолвер заново и вычистить кэш раскладки ЦЕЛИКОМ — то есть
        /// переверстать всю книгу. На рукописи в три тысячи абзацев это около секунды,
        /// и раньше эта секунда уходила на каждое нажатие в ленте оглавления, хотя
        /// добавлять было нечего: стили давно на месте.
        /// </returns>
        public static bool EnsureBuiltInStyles(DocumentModel doc)
        {
            if (doc is null) return false;
            doc.Styles ??= new List<DocumentStyle>();

            bool added = false;

            foreach (var builtIn in DocumentStyle.CreateBuiltInStyles())
            {
                if (doc.FindStyle(builtIn.Name) is not null) continue;
                doc.Styles.Add(builtIn);
                added = true;
            }

            return added;
        }

        /// <summary>
        /// Ширина текстовой области страницы в пунктах — по ней ставится правая позиция
        /// табуляции, к которой прижимается номер.
        /// </summary>
        public static double TextWidthPt(DocumentModel doc)
        {
            if (doc?.PageSettings is null) return 400;
            return doc.PageSettings.GetTextWidthMm() * MmToPt;
        }

        // ── Обновление номеров страниц ────────────────────────────────────

        /// <summary>
        /// Ставит строке оглавления её номер страницы, не трогая название.
        ///
        /// Нужен отдельно от полной пересборки по простой причине: оглавление само
        /// сдвигает книгу. Номера, посчитанные до его вставки, устаревают ровно в тот
        /// миг, когда оглавление легло на лист, и второй проход по готовой раскладке —
        /// единственный способ показать правду. Пересобирать ради этого весь список
        /// нельзя: человек мог поправить в строке слово или выделить его, и пересборка
        /// стёрла бы правку.
        ///
        /// Название строки берётся до последней табуляции — за ней стоит номер, и
        /// только он заменяется. Текст правится склейкой, а не SetPlainText: та
        /// уничтожает форматирование ранов.
        /// </summary>
        /// <returns>true — строка изменилась.</returns>
        public static bool ApplyPageNumber(
            ParagraphBlock entry, TocSettings settings, int pageNumber, double textWidthPt)
        {
            if (entry is null || settings is null) return false;

            var props = entry.Properties;
            if (props.TocOwnerId != settings.Id) return false;

            // Название над списком номера не носит.
            if (props.TocEntryLevel <= 0) return false;

            string raw = entry.GetPlainText();
            int tabAt = raw.LastIndexOf('\t');
            int headLength = tabAt >= 0 ? tabAt : raw.Length;

            bool withPage = settings.ShowPageNumbers && pageNumber > 0;
            string tail = withPage ? "\t" + pageNumber.ToString() : string.Empty;
            string current = raw.Substring(headLength);

            bool textChanged = !string.Equals(current, tail, StringComparison.Ordinal);
            if (textChanged)
                entry.SpliceText(headLength, raw.Length, tail);

            // Отступы берутся у самой строки, а не считаются по её уровню: их двигает
            // человек стрелками на линейке, и правая отметка обязана уехать вместе с
            // ними. Считая по уровню, проход по номерам страниц возвращал бы отметку на
            // место при каждом пересчёте, то есть молча отменял правку линейкой.
            bool tabsChanged = ApplyEntryTabStop(props, settings, IndentOf(props), textWidthPt, withPage);

            return textChanged || tabsChanged;
        }

        /// <summary>
        /// Пересчитывает позицию табуляции строки по её нынешним отступам.
        ///
        /// Нужен отдельно от построения списка: отступы строки человек двигает сам —
        /// стрелками на линейке, — и правая отметка, к которой прижат номер страницы,
        /// обязана уехать следом. Иначе номера остаются на прежнем месте, а названия
        /// из-под них уходят.
        ///
        /// Текст строки здесь не трогается вовсе — только её позиции табуляции.
        /// </summary>
        /// <returns>true — позиции табуляции изменились.</returns>
        public static bool RefreshEntryTabStop(
            ParagraphProperties props, TocSettings settings, double textWidthPt)
        {
            if (props is null || settings is null) return false;
            if (props.TocOwnerId != settings.Id) return false;

            // Название над списком номера не носит, значит и отметки ему не надо.
            if (props.TocEntryLevel <= 0) return false;

            return ApplyEntryTabStop(
                props, settings, IndentOf(props), textWidthPt, settings.ShowPageNumbers);
        }

        /// <summary>
        /// Насколько текстовая область строки уже листа: левый отступ плюс правый.
        /// Именно на эту величину отъезжает правая отметка.
        /// </summary>
        private static double IndentOf(ParagraphProperties props)
            => (props.LeftIndent ?? 0) + (props.RightIndent ?? 0);

        /// <summary>
        /// Позиция табуляции строки: правая отметка у границы текстовой области, с
        /// заполнителем и его плотностью из настроек. Без номера страницы отметка
        /// снимается — иначе за названием остаётся дорожка точек в никуда.
        /// </summary>
        /// <returns>true — позиции табуляции изменились.</returns>
        private static bool ApplyEntryTabStop(
            ParagraphProperties props, TocSettings settings,
            double indent, double textWidthPt, bool withPage)
        {
            if (!withPage)
            {
                if (props.TabStops is null || props.TabStops.Count == 0) return false;
                props.TabStops = null;
                return true;
            }

            double stopPt = textWidthPt - indent;
            if (stopPt < 1) stopPt = 1;

            var leader = settings.Leader == TocLeader.None
                ? TabLeaderStyle.None
                : (TabLeaderStyle)(int)settings.Leader;

            double density = settings.LeaderDensity;

            var existing = props.TabStops;
            if (existing is { Count: 1 }
                && Math.Abs(existing[0].PositionPt - stopPt) < 0.01
                && existing[0].Alignment == TabAlignment.Right
                && existing[0].Leader == leader
                && Math.Abs(existing[0].LeaderDensity - density) < 0.001)
                return false;

            props.TabStops = new List<TabStop>
            {
                new TabStop
                {
                    PositionPt = stopPt,
                    Alignment = TabAlignment.Right,
                    Leader = leader,
                    LeaderDensity = density
                }
            };

            return true;
        }

        // ── Текст заголовка ───────────────────────────────────────────────

        /// <summary>
        /// Текст абзаца одной строкой: без переводов строки, без двойных пробелов и
        /// без символов-заполнителей, которыми в тексте стоят картинки в строке.
        /// </summary>
        public static string OneLineText(ParagraphBlock para)
        {
            string raw = para?.GetPlainText() ?? string.Empty;
            if (raw.Length == 0) return string.Empty;

            var sb = new StringBuilder(raw.Length);
            bool lastWasSpace = false;

            foreach (char ch in raw)
            {
                // Картинка в строке стоит символом-заполнителем: в строке оглавления он
                // читается пустым прямоугольником, а смысла не несёт.
                if (ch == Models.Inline.RunModel.ObjectPlaceholder) continue;

                // Неразрывный (U+00A0), узкий неразрывный (U+202F) и тонкий (U+2009)
                // пробелы ведут себя здесь как обычные: строка всё равно сжимается в одну.
                bool isSpace = ch == ' ' || ch == '\t' || ch == '\n' || ch == '\r'
                               || ch == '\u00A0' || ch == '\u202F' || ch == '\u2009';

                if (isSpace)
                {
                    if (!lastWasSpace && sb.Length > 0) sb.Append(' ');
                    lastWasSpace = true;
                    continue;
                }

                sb.Append(ch);
                lastWasSpace = false;
            }

            while (sb.Length > 0 && sb[sb.Length - 1] == ' ')
                sb.Length--;

            return sb.ToString();
        }
    }
}
