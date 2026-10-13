using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.Models.Styles;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>Что за пункт рецензии.</summary>
    public enum ReviewKind
    {
        /// <summary>Вставленный текст.</summary>
        Insertion,

        /// <summary>Удалённый текст.</summary>
        Deletion,

        /// <summary>Замена: удалённый текст и вплотную за ним вставленный — одним пунктом.</summary>
        Replacement,

        /// <summary>Сменилось оформление знаков.</summary>
        Format,

        /// <summary>Сменилось оформление абзаца.</summary>
        ParagraphFormat,

        /// <summary>Перемещённый текст на прежнем месте.</summary>
        MoveFrom,

        /// <summary>Перемещённый текст на новом месте.</summary>
        MoveTo,

        /// <summary>Абзац разделён: вставлен знак абзаца.</summary>
        ParagraphInserted,

        /// <summary>Абзацы слиты: удалён знак абзаца.</summary>
        ParagraphDeleted,

        /// <summary>Обсуждение примечания.</summary>
        Comment
    }

    /// <summary>
    /// Пункт рецензии: правка или обсуждение примечания с его местом в тексте и всем,
    /// что о нём нужно показать в панели.
    /// </summary>
    public sealed class ReviewEntry
    {
        public ReviewKind Kind { get; init; }

        /// <summary>Правка; null — у примечания. У слитого пункта — первая из его правок.</summary>
        public RevisionService.Hit? Hit { get; init; }

        /// <summary>
        /// Все правки пункта. Соседние куски одной правки, которые модель держит
        /// раздельно (разные раны одного оформления), идут одним пунктом.
        /// </summary>
        public IReadOnlyList<RevisionService.Hit> Hits { get; init; } = Array.Empty<RevisionService.Hit>();

        /// <summary>Первое примечание обсуждения; null — у правки.</summary>
        public CommentModel? Comment { get; init; }

        /// <summary>Плавающая картинка, вставленная или удалённая правкой; null — правка текста.</summary>
        public ImageBlock? Image { get; init; }

        /// <summary>Ответы обсуждения по порядку.</summary>
        public IReadOnlyList<CommentModel> Replies { get; init; } = Array.Empty<CommentModel>();

        /// <summary>Абзац начала места и знаки места в нём.</summary>
        public ParagraphBlock Paragraph { get; init; } = null!;
        public int From { get; init; }
        public int To { get; init; }

        /// <summary>Всё место в тексте: куски абзацев (у примечания их бывает несколько).</summary>
        public IReadOnlyList<(ParagraphBlock Paragraph, int From, int To)> Ranges { get; init; }
            = Array.Empty<(ParagraphBlock, int, int)>();

        public string Author { get; init; } = string.Empty;
        public DateTime? Date { get; init; }

        /// <summary>Что сделано: «Вставлено», «Удалено», «Формат»…</summary>
        public string Action { get; init; } = string.Empty;

        /// <summary>Текст правки (в кавычках) или примечания.</summary>
        public string Text { get; init; } = string.Empty;

        /// <summary>Оформление до и после — у смены оформления; иначе пусто.</summary>
        public string Before { get; init; } = string.Empty;
        public string After { get; init; } = string.Empty;

        /// <summary>Обсуждение закрыто.</summary>
        public bool Done { get; init; }

        /// <summary>У замены — где кончается удалённое и начинается вставленное.</summary>
        public int SplitAt { get; init; } = -1;

        // Отрывок текста вокруг места — так, как его прочтёт человек: соседние
        // удалённые куски чужих правок в отрывок не попадают. Сам пункт идёт
        // тремя частями: что убрано, что добавлено и что отмечено (оформление,
        // место примечания); у каждой своя подача в панели.

        /// <summary>Текст перед местом, с многоточием, если обрезан.</summary>
        public string ContextBefore { get; internal set; } = string.Empty;

        /// <summary>Текст после места, с многоточием, если обрезан.</summary>
        public string ContextAfter { get; internal set; } = string.Empty;

        /// <summary>Убранный текст: удаление, прежнее место переноса, слитый знак абзаца.</summary>
        public string Removed { get; internal set; } = string.Empty;

        /// <summary>Добавленный текст: вставка, новое место переноса, новый знак абзаца.</summary>
        public string Added { get; internal set; } = string.Empty;

        /// <summary>Отмеченный текст: смена оформления или место примечания.</summary>
        public string Marked { get; internal set; } = string.Empty;

        /// <summary>Заголовок, под которым стоит место, — для группы в панели.</summary>
        public string Group { get; init; } = string.Empty;

        /// <summary>Блок потока, в котором лежит место (абзац или таблица), — по нему ищется страница.</summary>
        public Guid OwnerBlockId { get; init; }

        /// <summary>Порядок в документе: место абзаца в порядке чтения.</summary>
        public int Order { get; init; }

        /// <summary>Правка, а не примечание.</summary>
        public bool IsRevision => Comment is null;

        /// <summary>
        /// То, по чему пункт узнаётся после пересборки списка: у правки — абзац и
        /// знаки, у примечания — само примечание.
        /// </summary>
        public object Key => (object?)Comment ?? (object?)Image ?? Hit!.Value;
    }

    /// <summary>
    /// Собирает пункты рецензии документа: правки (как их делит RevisionService) и
    /// обсуждения примечаний — в порядке чтения, с заголовком, под которым каждый
    /// стоит, и блоком потока для номера страницы.
    /// </summary>
    public static class ReviewCollector
    {
        private const int QuoteMaxChars = 160;

        // Сколько знаков текста показывать по сторонам места.
        private const int ContextChars = 46;

        // Длинное место показывается началом и концом.
        private const int ChangeMaxChars = 150;

        public static List<ReviewEntry> Collect(DocumentModel document)
        {
            var entries = new List<ReviewEntry>();

            // Абзацы в порядке чтения с их блоком потока и заголовком над ними.
            var walk = new List<(ParagraphBlock Paragraph, Guid Owner, string Group)>();
            var floating = new List<(ImageBlock Image, int After, string Group)>();
            string group = string.Empty;

            foreach (var section in document.Sections)
            {
                foreach (var block in section.Blocks)
                {
                    if (block is ParagraphBlock paragraph)
                    {
                        if (TocService.LevelOf(paragraph, document, includeManual: true) > 0)
                        {
                            string title = TocService.OneLineText(paragraph).Trim();
                            if (title.Length > 0) group = title;
                        }
                        walk.Add((paragraph, paragraph.Id, group));
                    }
                    else if (block is ImageBlock { } image && (image.Inserted is not null || image.Deleted is not null))
                    {
                        floating.Add((image, walk.Count - 1, group));
                    }
                    else if (block is TableBlock table)
                    {
                        var cells = new List<TableCell>(table.Cells);
                        cells.Sort((a, b) => a.Row != b.Row ? a.Row.CompareTo(b.Row) : a.Column.CompareTo(b.Column));
                        foreach (var cell in cells)
                            foreach (var cellParagraph in cell.ParagraphsDeep())
                                walk.Add((cellParagraph, table.Id, group));
                    }
                }
            }

            for (int order = 0; order < walk.Count; order++)
            {
                var (paragraph, owner, paragraphGroup) = walk[order];
                if (!HasRevisions(paragraph)) continue;

                var cells = paragraph.ToCharCells();
                int paragraphStart = entries.Count;
                ReviewEntry? previous = null;
                foreach (var hit in RevisionService.HitsIn(paragraph))
                {
                    var entry = DescribeRevision(paragraph, cells, hit, owner, paragraphGroup, order);

                    // Кусок той же правки вплотную к предыдущему — один пункт.
                    if (previous is not null && CanMerge(previous, entry))
                    {
                        var merged = Merge(previous, entry, cells);
                        entries[^1] = merged;
                        previous = merged;
                        continue;
                    }

                    // Удалено и тут же вставлено тем же человеком — это замена слова, и
                    // читать её двумя пунктами незачем.
                    if (previous is not null && CanPair(previous, entry))
                    {
                        var paired = Pair(previous, entry, cells);
                        entries[^1] = paired;
                        previous = paired;
                        continue;
                    }

                    entries.Add(entry);
                    previous = entry;
                }

                for (int i = paragraphStart; i < entries.Count; i++)
                    FillSnippet(entries[i], cells);
            }

            AddComments(document, walk, entries);
            AddFloatingImages(document, walk, floating, entries);

            entries.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.From.CompareTo(b.From));
            return entries;
        }

        private static bool CanMerge(ReviewEntry a, ReviewEntry b)
            => a.Hit is { IsMark: false } && b.Hit is { IsMark: false }
               && a.Kind == b.Kind
               && a.To == b.From
               && string.Equals(a.Author, b.Author, StringComparison.Ordinal)
               && a.Date == b.Date
               && string.Equals(a.Before, b.Before, StringComparison.Ordinal)
               && string.Equals(a.After, b.After, StringComparison.Ordinal);

        /// <summary>Удаление, за которым вплотную идёт вставка того же автора, — или замена и ещё кусок её вставки.</summary>
        private static bool CanPair(ReviewEntry a, ReviewEntry b)
            => a.Hit is { IsMark: false } && b.Hit is { IsMark: false }
               && (a.Kind == ReviewKind.Deletion || a.Kind == ReviewKind.Replacement)
               && b.Kind == ReviewKind.Insertion
               && a.To == b.From
               && string.Equals(a.Author, b.Author, StringComparison.Ordinal);

        private static ReviewEntry Pair(ReviewEntry a, ReviewEntry b, List<ParagraphBlock.CharCell> cells)
        {
            var hits = new List<RevisionService.Hit>(a.Hits);
            hits.AddRange(b.Hits);

            int split = a.Kind == ReviewKind.Replacement ? a.SplitAt : a.To;

            return new ReviewEntry
            {
                Kind = ReviewKind.Replacement,
                Hit = a.Hit,
                Hits = hits,
                Paragraph = a.Paragraph,
                From = a.From,
                To = b.To,
                SplitAt = split,
                Ranges = new List<(ParagraphBlock, int, int)> { (a.Paragraph, a.From, b.To) },
                Author = a.Author,
                Date = Later(a.Date, b.Date),
                Action = "Заменено",
                Text = Quote(cells, a.From, split) + " → " + Quote(cells, split, b.To),
                Group = a.Group,
                OwnerBlockId = a.OwnerBlockId,
                Order = a.Order
            };
        }

        private static DateTime? Later(DateTime? a, DateTime? b)
            => a is null ? b : b is null ? a : (a > b ? a : b);

        // ── Отрывок ───────────────────────────────────────────────────────

        /// <summary>Отрывок текста вокруг места и сами изменённые знаки.</summary>
        private static void FillSnippet(ReviewEntry entry, List<ParagraphBlock.CharCell> cells)
        {
            int from = Math.Clamp(entry.From, 0, cells.Count);
            int to = Math.Clamp(entry.To, from, cells.Count);

            switch (entry.Kind)
            {
                case ReviewKind.Insertion:
                case ReviewKind.MoveTo:
                    entry.Added = Change(cells, from, to);
                    break;

                case ReviewKind.Deletion:
                case ReviewKind.MoveFrom:
                    entry.Removed = Change(cells, from, to);
                    break;

                case ReviewKind.Replacement:
                {
                    int split = Math.Clamp(entry.SplitAt, from, to);
                    entry.Removed = Change(cells, from, split);
                    entry.Added = Change(cells, split, to);
                    break;
                }

                case ReviewKind.Format:
                    entry.Marked = Change(cells, from, to);
                    break;

                case ReviewKind.ParagraphInserted:
                    entry.ContextBefore = Before(cells, cells.Count, ContextChars * 2);
                    entry.Added = "¶";
                    return;

                case ReviewKind.ParagraphDeleted:
                    entry.ContextBefore = Before(cells, cells.Count, ContextChars * 2);
                    entry.Removed = "¶";
                    return;

                case ReviewKind.ParagraphFormat:
                    entry.ContextAfter = After(cells, 0, ContextChars * 3, trimStart: true);
                    return;

                case ReviewKind.Comment:
                    entry.Marked = Change(cells, from, to, skipDeleted: true);
                    break;
            }

            entry.ContextBefore = Before(cells, from, ContextChars);
            entry.ContextAfter = After(cells, to, ContextChars, trimStart: false);
        }

        /// <summary>Знаки места как текст; длинное — началом и концом.</summary>
        private static string Change(List<ParagraphBlock.CharCell> cells, int from, int to, bool skipDeleted = false)
        {
            var sb = new StringBuilder();
            for (int i = from; i < to; i++)
            {
                var cell = cells[i];
                if (skipDeleted && cell.Props?.Deleted is not null) continue;
                AppendCell(sb, cell);
            }

            string text = sb.ToString();
            if (text.Length > ChangeMaxChars)
            {
                int head = ChangeMaxChars * 3 / 5;
                int tail = ChangeMaxChars - head;
                text = text.Substring(0, head).TrimEnd() + " … " + text.Substring(text.Length - tail).TrimStart();
            }

            return text.Trim().Length == 0 ? "␣" : text;
        }

        /// <summary>Текст перед позицией без чужих удалённых знаков; обрезанный — с многоточием по слову.</summary>
        private static string Before(List<ParagraphBlock.CharCell> cells, int position, int limit)
        {
            var sb = new StringBuilder();
            int i = position - 1;
            for (; i >= 0 && sb.Length < limit; i--)
            {
                var cell = cells[i];
                if (cell.Props?.Deleted is not null) continue;
                var piece = new StringBuilder();
                AppendCell(piece, cell);
                sb.Insert(0, piece.ToString());
            }

            string text = sb.ToString();
            bool cut = HasVisibleBefore(cells, i);
            if (cut)
            {
                int space = text.IndexOf(' ');
                if (space >= 0 && space < text.Length - 1) text = text.Substring(space + 1);
                return "…" + text.TrimStart();
            }

            return text.TrimStart();
        }

        /// <summary>Текст после позиции без чужих удалённых знаков; обрезанный — с многоточием по слову.</summary>
        private static string After(List<ParagraphBlock.CharCell> cells, int position, int limit, bool trimStart)
        {
            var sb = new StringBuilder();
            int i = position;
            for (; i < cells.Count && sb.Length < limit; i++)
            {
                var cell = cells[i];
                if (cell.Props?.Deleted is not null) continue;
                AppendCell(sb, cell);
            }

            string text = sb.ToString();
            if (trimStart) text = text.TrimStart();

            bool cut = HasVisibleAfter(cells, i);
            if (cut)
            {
                int space = text.LastIndexOf(' ');
                if (space > 0) text = text.Substring(0, space);
                return text.TrimEnd() + "…";
            }

            return text.TrimEnd();
        }

        private static bool HasVisibleBefore(List<ParagraphBlock.CharCell> cells, int i)
        {
            for (; i >= 0; i--)
                if (cells[i].Props?.Deleted is null && !char.IsWhiteSpace(cells[i].Ch)) return true;
            return false;
        }

        private static bool HasVisibleAfter(List<ParagraphBlock.CharCell> cells, int i)
        {
            for (; i < cells.Count; i++)
                if (cells[i].Props?.Deleted is null && !char.IsWhiteSpace(cells[i].Ch)) return true;
            return false;
        }

        private static void AppendCell(StringBuilder sb, ParagraphBlock.CharCell cell)
        {
            if (cell.IsInlineObject) sb.Append("[рисунок]");
            else if (cell.Ch == '\t' || cell.Ch == '\n' || cell.Ch == '\r' || cell.Ch == '\u000B') sb.Append(' ');
            else if (!char.IsControl(cell.Ch)) sb.Append(cell.Ch);
        }

        private static ReviewEntry Merge(ReviewEntry a, ReviewEntry b, List<ParagraphBlock.CharCell> cells)
        {
            var hits = new List<RevisionService.Hit>(a.Hits);
            hits.AddRange(b.Hits);

            return new ReviewEntry
            {
                Kind = a.Kind,
                Hit = a.Hit,
                Hits = hits,
                Paragraph = a.Paragraph,
                From = a.From,
                To = b.To,
                Ranges = new List<(ParagraphBlock, int, int)> { (a.Paragraph, a.From, b.To) },
                Author = a.Author,
                Date = a.Date,
                Action = a.Action,
                Text = Quote(cells, a.From, b.To),
                Before = a.Before,
                After = a.After,
                Group = a.Group,
                OwnerBlockId = a.OwnerBlockId,
                Order = a.Order
            };
        }

        private static bool HasRevisions(ParagraphBlock paragraph)
        {
            if (paragraph.Properties.HasRevision) return true;
            foreach (var chunk in paragraph.Chunks)
                foreach (var run in chunk.Runs)
                    if (run.Properties?.HasRevision == true) return true;
            return false;
        }

        // ── Правки ────────────────────────────────────────────────────────

        private static ReviewEntry DescribeRevision(
            ParagraphBlock paragraph, List<ParagraphBlock.CharCell> cells, RevisionService.Hit hit,
            Guid owner, string group, int order)
        {
            var props = paragraph.Properties;
            var ranges = new List<(ParagraphBlock, int, int)>();

            ReviewEntry Make(ReviewKind kind, RevisionInfo? info, string action, string text, string before = "", string after = "", int from = -1, int to = -1)
            {
                int f = from >= 0 ? from : hit.From;
                int t = to >= 0 ? to : hit.To;
                if (t > f) ranges.Add((paragraph, f, t));

                return new ReviewEntry
                {
                    Kind = kind,
                    Hit = hit,
                    Hits = new[] { hit },
                    Paragraph = paragraph,
                    From = f,
                    To = t,
                    Ranges = ranges,
                    Author = info?.Author ?? string.Empty,
                    Date = info?.Date,
                    Action = action,
                    Text = text,
                    Before = before,
                    After = after,
                    Group = group,
                    OwnerBlockId = owner,
                    Order = order
                };
            }

            // Знак абзаца: место — последние знаки абзаца, чтобы было что подсветить.
            if (hit.IsMark && hit.From == hit.To)
            {
                int tailFrom = Math.Max(0, cells.Count - 1);
                if (props.MarkDeleted is { } markDeleted)
                    return Make(ReviewKind.ParagraphDeleted, markDeleted, "Абзацы слиты",
                        "Удалён конец абзаца — он сольётся со следующим", from: tailFrom, to: cells.Count);

                return Make(ReviewKind.ParagraphInserted, props.MarkInserted, "Абзац разделён",
                    "Здесь начинается новый абзац", from: tailFrom, to: cells.Count);
            }

            if (hit.IsMark)
            {
                var change = props.FormatChange;
                var (before, after) = change?.Previous is { } previous
                    ? ParagraphDiff(previous, props)
                    : (string.Empty, string.Empty);

                return Make(ReviewKind.ParagraphFormat, change?.Info, "Оформление абзаца",
                    Quote(cells, 0, cells.Count), before, after, 0, cells.Count);
            }

            var runProps = hit.From < cells.Count ? cells[hit.From].Props : null;
            string quote = Quote(cells, hit.From, hit.To);

            if (runProps?.Deleted is { } deleted)
                return Make(deleted.IsMove ? ReviewKind.MoveFrom : ReviewKind.Deletion, deleted,
                    deleted.IsMove ? "Перемещено отсюда" : "Удалено", quote);

            if (runProps?.Inserted is { } inserted)
                return Make(inserted.IsMove ? ReviewKind.MoveTo : ReviewKind.Insertion, inserted,
                    inserted.IsMove ? "Перемещено сюда" : "Добавлено", quote);

            if (runProps?.FormatChange is { } formatChange)
            {
                var (before, after) = RunDiff(formatChange.Previous, runProps);
                return Make(ReviewKind.Format, formatChange.Info, "Формат", quote, before, after);
            }

            return Make(ReviewKind.Format, null, "Правка", quote);
        }

        /// <summary>Текст места в кавычках, длинный — с многоточием.</summary>
        private static string Quote(List<ParagraphBlock.CharCell> cells, int from, int to)
        {
            var sb = new StringBuilder();
            from = Math.Clamp(from, 0, cells.Count);
            to = Math.Clamp(to, from, cells.Count);

            for (int i = from; i < to && sb.Length <= QuoteMaxChars; i++)
            {
                var cell = cells[i];
                if (cell.IsInlineObject) sb.Append("[объект]");
                else if (cell.Ch == '\t' || cell.Ch == '\n' || cell.Ch == '\r') sb.Append(' ');
                else if (!char.IsControl(cell.Ch)) sb.Append(cell.Ch);
            }

            string text = sb.ToString().Trim();
            if (text.Length > QuoteMaxChars) text = text.Substring(0, QuoteMaxChars).TrimEnd() + "…";
            if (text.Length == 0) return "пробел";
            return "«" + text + "»";
        }

        /// <summary>Оформление знаков до и после — только то, что поменялось.</summary>
        public static (string Before, string After) RunDiff(RunProperties? before, RunProperties after)
        {
            var was = new List<string>();
            var now = new List<string>();

            void Flag(bool b, bool a, string on, string off)
            {
                if (b == a) return;
                was.Add(b ? on : off);
                now.Add(a ? on : off);
            }

            Flag(before?.IsBold == true, after.IsBold == true, "полужирный", "не полужирный");
            Flag(before?.IsItalic == true, after.IsItalic == true, "курсив", "не курсив");
            Flag(before?.IsUnderline == true, after.IsUnderline, "подчёркнутый", "без подчёркивания");
            Flag(before?.IsStrikethrough == true, after.IsStrikethrough, "зачёркнутый", "не зачёркнутый");
            Flag(before?.IsSuperscript == true, after.IsSuperscript, "надстрочный", "не надстрочный");
            Flag(before?.IsSubscript == true, after.IsSubscript, "подстрочный", "не подстрочный");
            Flag(before?.IsAllCaps == true, after.IsAllCaps, "все прописные", "строчные");
            Flag(before?.IsSmallCaps == true, after.IsSmallCaps, "малые прописные", "без малых прописных");

            if (!string.Equals(before?.FontFamily, after.FontFamily, StringComparison.OrdinalIgnoreCase))
            {
                was.Add(before?.FontFamily ?? "шрифт стиля");
                now.Add(after.FontFamily ?? "шрифт стиля");
            }

            if (before?.FontSize != after.FontSize)
            {
                was.Add(SizeText(before?.FontSize));
                now.Add(SizeText(after.FontSize));
            }

            if (!string.Equals(before?.TextColor, after.TextColor, StringComparison.OrdinalIgnoreCase))
            {
                was.Add(ColorName(before?.TextColor));
                now.Add(ColorName(after.TextColor));
            }

            if (!string.Equals(before?.HighlightColor, after.HighlightColor, StringComparison.OrdinalIgnoreCase))
            {
                was.Add(string.IsNullOrEmpty(before?.HighlightColor) ? "без выделения цветом" : "выделение цветом");
                now.Add(string.IsNullOrEmpty(after.HighlightColor) ? "без выделения цветом" : "выделение цветом");
            }

            if (!string.Equals(before?.StyleName, after.StyleName, StringComparison.Ordinal))
            {
                was.Add("стиль " + (before?.StyleName ?? "обычный"));
                now.Add("стиль " + (after.StyleName ?? "обычный"));
            }

            return (string.Join(", ", was), string.Join(", ", now));
        }

        private static string SizeText(double? size)
            => size is double value
                ? value.ToString("0.#", CultureInfo.InvariantCulture) + " пт"
                : "размер стиля";

        /// <summary>Цвет словами, если это один из основных; иначе код.</summary>
        public static string ColorName(string? color)
        {
            if (string.IsNullOrEmpty(color) || string.Equals(color, "auto", StringComparison.OrdinalIgnoreCase))
                return "цвет авто";

            string hex = color.TrimStart('#');
            if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
                return "цвет " + color;

            int r = (rgb >> 16) & 0xFF, g = (rgb >> 8) & 0xFF, b = rgb & 0xFF;

            (string Name, int R, int G, int B)[] named =
            {
                ("чёрный", 0, 0, 0), ("белый", 255, 255, 255),
                ("красный", 255, 0, 0), ("тёмно-красный", 192, 0, 0),
                ("зелёный", 0, 176, 80), ("тёмно-зелёный", 0, 128, 0),
                ("синий", 0, 0, 255), ("синий", 0, 112, 192), ("тёмно-синий", 0, 32, 96),
                ("жёлтый", 255, 255, 0), ("оранжевый", 255, 192, 0),
                ("фиолетовый", 112, 48, 160), ("серый", 128, 128, 128),
                ("голубой", 0, 176, 240)
            };

            foreach (var (name, nr, ng, nb) in named)
                if (Math.Abs(nr - r) + Math.Abs(ng - g) + Math.Abs(nb - b) <= 40) return name;

            return "цвет #" + hex.ToUpperInvariant();
        }

        /// <summary>Оформление абзаца до и после — только то, что поменялось.</summary>
        public static (string Before, string After) ParagraphDiff(ParagraphProperties before, ParagraphProperties after)
        {
            var was = new List<string>();
            var now = new List<string>();

            static string Align(TextAlignment? alignment) => alignment switch
            {
                TextAlignment.Center => "по центру",
                TextAlignment.Right => "по правому краю",
                TextAlignment.Justify => "по ширине",
                null => "выравнивание стиля",
                _ => "по левому краю"
            };

            static string Pt(double? value, string what)
                => value is double v ? what + " " + v.ToString("0.#", CultureInfo.InvariantCulture) + " пт" : what + " стиля";

            if (before.Alignment != after.Alignment)
            {
                was.Add(Align(before.Alignment));
                now.Add(Align(after.Alignment));
            }

            if (before.LeftIndent != after.LeftIndent)
            {
                was.Add(Pt(before.LeftIndent, "отступ слева"));
                now.Add(Pt(after.LeftIndent, "отступ слева"));
            }

            if (before.RightIndent != after.RightIndent)
            {
                was.Add(Pt(before.RightIndent, "отступ справа"));
                now.Add(Pt(after.RightIndent, "отступ справа"));
            }

            if (before.FirstLineIndent != after.FirstLineIndent)
            {
                was.Add(Pt(before.FirstLineIndent, "первая строка"));
                now.Add(Pt(after.FirstLineIndent, "первая строка"));
            }

            if (before.SpaceBefore != after.SpaceBefore)
            {
                was.Add(Pt(before.SpaceBefore, "интервал перед"));
                now.Add(Pt(after.SpaceBefore, "интервал перед"));
            }

            if (before.SpaceAfter != after.SpaceAfter)
            {
                was.Add(Pt(before.SpaceAfter, "интервал после"));
                now.Add(Pt(after.SpaceAfter, "интервал после"));
            }

            if (before.LineSpacingRule != after.LineSpacingRule || before.LineSpacingValue != after.LineSpacingValue)
            {
                was.Add("междустрочный " + (before.LineSpacingValue?.ToString("0.##", CultureInfo.InvariantCulture) ?? "стиля"));
                now.Add("междустрочный " + (after.LineSpacingValue?.ToString("0.##", CultureInfo.InvariantCulture) ?? "стиля"));
            }

            if (!string.Equals(before.StyleName, after.StyleName, StringComparison.Ordinal))
            {
                was.Add("стиль " + (before.StyleName ?? "обычный"));
                now.Add("стиль " + (after.StyleName ?? "обычный"));
            }

            if (before.KeepWithNext != after.KeepWithNext)
            {
                was.Add(before.KeepWithNext ? "не отрывать от следующего" : "можно отрывать от следующего");
                now.Add(after.KeepWithNext ? "не отрывать от следующего" : "можно отрывать от следующего");
            }

            if (before.KeepTogether != after.KeepTogether)
            {
                was.Add(before.KeepTogether ? "не разрывать" : "можно разрывать");
                now.Add(after.KeepTogether ? "не разрывать" : "можно разрывать");
            }

            if (before.PageBreakBefore != after.PageBreakBefore)
            {
                was.Add(before.PageBreakBefore ? "с новой страницы" : "без разрыва страницы");
                now.Add(after.PageBreakBefore ? "с новой страницы" : "без разрыва страницы");
            }

            if (!string.Equals(before.ShadingColor, after.ShadingColor, StringComparison.OrdinalIgnoreCase))
            {
                was.Add(string.IsNullOrEmpty(before.ShadingColor) ? "без заливки" : "заливка");
                now.Add(string.IsNullOrEmpty(after.ShadingColor) ? "без заливки" : "заливка");
            }

            if ((before.Borders is null) != (after.Borders is null))
            {
                was.Add(before.Borders is null ? "без рамки" : "рамка");
                now.Add(after.Borders is null ? "без рамки" : "рамка");
            }

            return (string.Join(", ", was), string.Join(", ", now));
        }

        // ── Плавающие картинки ────────────────────────────────────────────

        /// <summary>
        /// Плавающие картинки с правкой: место — абзац, за которым картинка стоит в
        /// потоке (у первой в документе — первый абзац); закреплённые на листе — в конце.
        /// </summary>
        private static void AddFloatingImages(
            DocumentModel document,
            List<(ParagraphBlock Paragraph, Guid Owner, string Group)> walk,
            List<(ImageBlock Image, int After, string Group)> floating,
            List<ReviewEntry> entries)
        {
            if (walk.Count == 0) return;

            foreach (var section in document.Sections)
                foreach (var block in section.FloatingObjects)
                    if (block is ImageBlock pinned && (pinned.Inserted is not null || pinned.Deleted is not null))
                        floating.Add((pinned, walk.Count - 1, walk[^1].Group));

            foreach (var (image, after, imageGroup) in floating)
            {
                int order = Math.Clamp(after, 0, walk.Count - 1);
                var (paragraph, owner, _) = walk[order];

                bool deleted = image.Deleted is not null;
                var info = image.Deleted ?? image.Inserted;
                const string label = "рисунок с обтеканием";

                var entry = new ReviewEntry
                {
                    Kind = deleted ? ReviewKind.Deletion : ReviewKind.Insertion,
                    Image = image,
                    Paragraph = paragraph,
                    From = paragraph.TotalLength,
                    To = paragraph.TotalLength,
                    Author = info?.Author ?? string.Empty,
                    Date = info?.Date,
                    Action = deleted ? "Удалено" : "Добавлено",
                    Text = "«" + label + "»",
                    Group = imageGroup,
                    OwnerBlockId = owner,
                    Order = order
                };

                if (deleted) entry.Removed = label;
                else entry.Added = label;

                entries.Add(entry);
            }
        }

        // ── Примечания ────────────────────────────────────────────────────

        private static void AddComments(
            DocumentModel document,
            List<(ParagraphBlock Paragraph, Guid Owner, string Group)> walk,
            List<ReviewEntry> entries)
        {
            if (document.Comments.Count == 0) return;

            var byId = new Dictionary<Guid, CommentModel>();
            foreach (var comment in document.Comments) byId[comment.Id] = comment;

            CommentModel RootOf(CommentModel comment)
            {
                var current = comment;
                for (int guard = 0; guard < 64 && current.ParentId is Guid parentId
                     && byId.TryGetValue(parentId, out var parent); guard++)
                    current = parent;
                return current;
            }

            var replies = new Dictionary<Guid, List<CommentModel>>();
            foreach (var comment in document.Comments)
            {
                if (comment.ParentId is null) continue;
                var root = RootOf(comment);
                if (!replies.TryGetValue(root.Id, out var list))
                    replies[root.Id] = list = new List<CommentModel>();
                list.Add(comment);
            }

            // Места обсуждений: куски абзацев, отмеченные им или ответами.
            var ranges = new Dictionary<Guid, List<(ParagraphBlock, int, int)>>();
            var starts = new Dictionary<Guid, int>();

            for (int order = 0; order < walk.Count; order++)
            {
                var paragraph = walk[order].Paragraph;
                int position = 0;

                foreach (var chunk in paragraph.Chunks)
                {
                    foreach (var run in chunk.Runs)
                    {
                        int length = run.InlineImageId.HasValue ? 1 : (run.Text?.Length ?? 0);

                        if (run.Properties?.CommentIds is { Count: > 0 } ids)
                        {
                            foreach (var id in ids)
                            {
                                if (!byId.TryGetValue(id, out var comment)) continue;
                                var rootId = RootOf(comment).Id;

                                if (!ranges.TryGetValue(rootId, out var list))
                                {
                                    ranges[rootId] = list = new List<(ParagraphBlock, int, int)>();
                                    starts[rootId] = order;
                                }

                                if (list.Count > 0 && ReferenceEquals(list[^1].Item1, paragraph) && list[^1].Item3 == position)
                                    list[^1] = (paragraph, list[^1].Item2, position + length);
                                else if (!list.Exists(r => ReferenceEquals(r.Item1, paragraph)
                                                           && r.Item2 <= position && r.Item3 >= position + length))
                                    list.Add((paragraph, position, position + length));
                            }
                        }

                        position += length;
                    }
                }
            }

            foreach (var comment in document.Comments)
            {
                if (comment.ParentId is not null) continue;
                if (!ranges.TryGetValue(comment.Id, out var place) || place.Count == 0) continue;

                int order = starts[comment.Id];
                var thread = replies.TryGetValue(comment.Id, out var list) ? list : new List<CommentModel>();
                thread.Sort((a, b) => Nullable.Compare(a.Date, b.Date));

                var commentEntry = new ReviewEntry
                {
                    Kind = ReviewKind.Comment,
                    Comment = comment,
                    Replies = thread,
                    Paragraph = place[0].Item1,
                    From = place[0].Item2,
                    To = place[0].Item3,
                    Ranges = place,
                    Author = comment.Author,
                    Date = comment.Date,
                    Action = "Примечание",
                    Text = comment.PlainText.Trim(),
                    Done = comment.Done,
                    Group = walk[order].Group,
                    OwnerBlockId = walk[order].Owner,
                    Order = order
                };

                FillSnippet(commentEntry, place[0].Item1.ToCharCells());
                entries.Add(commentEntry);
            }
        }
    }
}
