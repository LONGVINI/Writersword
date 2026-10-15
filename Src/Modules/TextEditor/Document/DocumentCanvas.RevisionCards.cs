using Avalonia.Input;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.Models.Styles;
using Writersword.Modules.TextEditor.Rendering;
using Writersword.Modules.TextEditor.Services;
using Writersword.Modules.TextEditor.ViewModels;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Поле исправлений при «Всех исправлениях» — своё, не копия области выносок Word.
    ///
    /// Справа от листа, на фоне холста, стоят ярлыки: капсула из матового тёмного
    /// стекла с кружком автора и началом текста — у примечаний и у правок, которых в
    /// тексте не видно (смена оформления знаков и абзаца, перемещения). Вставки и
    /// удаления остаются в тексте цветом автора. Стекло размывает и притемняет то, что
    /// под ним, поэтому ярлык читается на любой картинке темы и в светлой, и в тёмной.
    ///
    /// От места в тексте к ярлыку идёт тонкий пунктир цвета автора с точкой у места.
    /// Указатель над ярлыком раскрывает его в выноску: автор, время, весь текст и
    /// кнопки (принять и отклонить у правки, открыть обсуждение у примечания); связь
    /// становится сплошной, место в тексте подсвечивается. Указатель над местом в
    /// тексте подсвечивает его ярлык, а над вставкой или удалением без ярлыка всплывает
    /// подсказка: что сделано, кто и когда. Ярлык места под кареткой тоже подсвечен.
    ///
    /// Полоса появляется, только когда есть что в неё положить; место под неё отводит
    /// холст. В выгрузке, в чтении и в остальных видах исправлений её нет: при простой
    /// разметке примечания остаются значками на поле листа (DocumentCanvas.Comments).
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        /// <summary>Выноска поля исправлений: правка или обсуждение примечания.</summary>
        private sealed record MarkupItem(
            RevisionService.Hit? Hit,
            CommentModel? Comment,
            int PageIndex,
            float AnchorXPt,
            float AnchorYPt,
            float AnchorBottomPt,
            string Author,
            string Head,
            string Body,
            string Meta,
            int Replies,
            bool Done,
            List<(ParagraphBlock Paragraph, int From, int To)> Ranges)
        {
            /// <summary>Чем выноска узнаётся между кадрами: местом правки или обсуждением.</summary>
            public object Key => (object?)Comment ?? Hit!.Value;
        }

        /// <summary>Ярлык выноски на поле: прямоугольник и подпись — в координатах её листа.</summary>
        private readonly record struct MarkupBox(MarkupItem Item, SKRect Rect, string Label);

        /// <summary>
        /// Раскрытая выноска под указателем: прямоугольник, строки текста и кнопки —
        /// принять и отклонить у правки, открыть обсуждение у примечания.
        /// </summary>
        private readonly record struct MarkupCard(
            MarkupItem Item, SKRect Rect, IReadOnlyList<string> BodyLines,
            SKRect Accept, SKRect Reject, SKRect Open);

        /// <summary>Подсказка над правкой в тексте: что, кто, когда; место — у указателя на листе.</summary>
        private sealed record MarkupTip(string Head, string Meta, string Body, int PageIndex, float XPt, float YPt);

        // Полоса ярлыков справа от листа, пт: от края листа до ярлыка, ярлык и запас.
        private const float MarkupPaneWidthPt = 160f;

        // Ярлыки поля исправлений на холсте включены.
        private static readonly bool MarkupLabelsEnabled = false;
        private const float MarkupLabelInsetPt = 10f;
        private const float MarkupLabelMaxWidthPt = 140f;
        private const float MarkupLabelHeightPt = 17f;
        private const float MarkupLabelGapPt = 4f;

        // Раскрытая выноска.
        private const float MarkupCardWidthPt = 220f;
        private const float MarkupCardPadPt = 8f;

        private const float MarkupHeadPt = 7.5f;
        private const float MarkupBodyPt = 7.5f;
        private const float MarkupMetaPt = 6.5f;
        private const float MarkupLinePt = 10f;
        private const float MarkupAvatarRadiusPt = 6f;
        private const float MarkupButtonHeightPt = 14f;
        private const int MarkupCardMaxLines = 8;
        private const int MarkupQuoteMaxChars = 90;

        // Выноски последней раскладки.
        private readonly object _markupLock = new();
        private List<ParaLayout>? _markupLayouts;
        private RevisionView _markupView;
        private int _markupCommentsVersion = -1;
        private List<MarkupItem> _markupItems = new();

        // Поле исправлений сейчас отведено: от этого зависит ширина холста и шаг листов в ряд.
        private bool _revisionLaneActive;

        // Выноска под указателем (или чьё место в тексте под указателем) и подсказка.
        private object? _markupHoverKey;
        private MarkupTip? _markupTip;

        /// <summary>Ширина поля исправлений справа от листа; ноль — поля нет.</summary>
        private float RevisionLaneWidthPtNow => _revisionLaneActive ? MarkupPaneWidthPt : 0f;

        /// <summary>Поле показывается при этом виде: «Все исправления» на листах правки.</summary>
        private bool MarkupPaneVisible
        {
            get
            {
                // Ярлыки на холсте выключены: рецензия переезжает в панель справа от
                // редактора, на листе остаются метки. До неё примечания при «Всех
                // исправлениях» снова показываются значками на поле листа.
                if (!MarkupLabelsEnabled) return false;

                if (ExportPassActive || ReadingActive || SpreadMode) return false;
                if (DocVm is not { } docVm) return false;
                if (docVm.ViewMode != EditorViewMode.Page) return false;

                var view = _styleResolver?.RevisionView ?? docVm.Document.RevisionView;
                return view == RevisionView.AllMarkup;
            }
        }

        // ── Сбор выносок ─────────────────────────────────────────────────

        /// <summary>Выноски для раскладки: из кеша, если раскладка и примечания те же.</summary>
        private List<MarkupItem> GetMarkupItems(List<ParaLayout> layouts)
        {
            var view = _styleResolver?.RevisionView ?? DocVm?.Document.RevisionView ?? RevisionView.AllMarkup;
            int commentsVersion = System.Threading.Volatile.Read(ref _commentsVersion);

            lock (_markupLock)
            {
                if (ReferenceEquals(_markupLayouts, layouts) && _markupView == view
                    && _markupCommentsVersion == commentsVersion)
                    return _markupItems;
            }

            var items = new List<MarkupItem>();
            try
            {
                items = BuildMarkupItems(layouts);
            }
            catch (InvalidOperationException)
            {
                // Документ правится в эту же секунду: выноски соберутся на следующем кадре.
                return items;
            }

            lock (_markupLock)
            {
                _markupLayouts = layouts;
                _markupView = view;
                _markupCommentsVersion = commentsVersion;
                _markupItems = items;
            }

            return items;
        }

        /// <summary>
        /// Поле исправлений нужно или больше не нужно: холст меняет ширину, листы в ряд —
        /// шаг. Меняется после кадра, на котором это выяснилось.
        /// </summary>
        private void UpdateRevisionLane(bool needed)
        {
            if (needed == _revisionLaneActive) return;

            _revisionLaneActive = needed;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                UpdateEffectivePagesPerRow();
                InvalidateMeasure();
                InvalidateFull();
            });
        }

        /// <summary>Куски раскладки по абзацам: абзац, разрезанный листами или колонками, даёт несколько.</summary>
        private static Dictionary<ParagraphBlock, List<ParaLayout>> SlicesByParagraph(List<ParaLayout> layouts)
        {
            var slices = new Dictionary<ParagraphBlock, List<ParaLayout>>(ReferenceEqualityComparer.Instance);
            foreach (var pl in layouts)
            {
                var model = pl.Cell?.ParaBlock ?? pl.Vm?.Model;
                if (model is null || pl.Layout is null) continue;

                if (!slices.TryGetValue(model, out var list))
                    slices[model] = list = new List<ParaLayout>();
                list.Add(pl);
            }
            return slices;
        }

        private List<MarkupItem> BuildMarkupItems(List<ParaLayout> layouts)
        {
            var items = new List<MarkupItem>();
            var document = DocVm?.Document;
            if (document is null || layouts.Count == 0) return items;

            var slices = SlicesByParagraph(layouts);
            var order = RevisionService.ReadingOrder(document);

            // Правки, которых в тексте не видно: оформление и перемещения.
            foreach (var paragraph in order)
            {
                if (!paragraph.Properties.HasRevision && !HasRunRevisions(paragraph)) continue;
                if (!slices.TryGetValue(paragraph, out var paraSlices)) continue;

                var cells = paragraph.ToCharCells();

                foreach (var hit in RevisionService.HitsIn(paragraph))
                {
                    if (!NeedsBalloon(paragraph, cells, hit)) continue;
                    if (!TryAnchor(paraSlices, hit.IsMark && hit.From == hit.To ? cells.Count : hit.From, cells.Count,
                            out int pageIndex, out float x, out float y, out float bottom))
                        continue;

                    var (author, head, body, info, _) = DescribeRevision(paragraph, cells, hit);
                    int to = hit.IsMark && hit.From == hit.To ? cells.Count : hit.To;
                    items.Add(new MarkupItem(
                        hit, null, pageIndex, x, y, bottom,
                        author, head, body, FormatMeta(author, info), 0, false,
                        new List<(ParagraphBlock, int, int)> { (paragraph, hit.From, to) }));
                }
            }

            // Обсуждения примечаний.
            AddCommentItems(document, order, slices, items);

            items.Sort((a, b) => a.PageIndex != b.PageIndex
                ? a.PageIndex.CompareTo(b.PageIndex)
                : a.AnchorYPt.CompareTo(b.AnchorYPt));

            return items;
        }

        /// <summary>
        /// Выноска у правки — как у Word: оформление знаков и абзаца и перемещения.
        /// Вставка и удаление видны в самом тексте.
        /// </summary>
        private static bool NeedsBalloon(ParagraphBlock paragraph, List<ParagraphBlock.CharCell> cells, RevisionService.Hit hit)
        {
            if (hit.IsMark) return hit.From != hit.To && paragraph.Properties.FormatChange is not null;

            var props = hit.From < cells.Count ? cells[hit.From].Props : null;
            if (props is null) return false;

            if (props.Inserted is { IsMove: true } || props.Deleted is { IsMove: true }) return true;
            return props.FormatChange is not null && props.Inserted is null && props.Deleted is null;
        }

        private static bool HasRunRevisions(ParagraphBlock paragraph)
        {
            foreach (var chunk in paragraph.Chunks)
                foreach (var run in chunk.Runs)
                    if (run.Properties?.HasRevision == true) return true;
            return false;
        }

        private void AddCommentItems(
            DocumentModel document, List<ParagraphBlock> order,
            Dictionary<ParagraphBlock, List<ParaLayout>> slices, List<MarkupItem> items)
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

            var replies = new Dictionary<Guid, int>();
            foreach (var comment in document.Comments)
            {
                if (comment.ParentId is null) continue;
                var root = RootOf(comment);
                replies[root.Id] = replies.TryGetValue(root.Id, out int count) ? count + 1 : 1;
            }

            // Место каждого обсуждения: куски абзацев, отмеченные им или ответами.
            var ranges = new Dictionary<Guid, List<(ParagraphBlock, int, int)>>();

            foreach (var paragraph in order)
            {
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
                                    ranges[rootId] = list = new List<(ParagraphBlock, int, int)>();

                                // Соседние раны одного места сливаются в один кусок.
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

            foreach (var (rootId, rootRanges) in ranges)
            {
                if (!byId.TryGetValue(rootId, out var root) || rootRanges.Count == 0) continue;

                var (startParagraph, startChar, _) = rootRanges[0];
                if (!slices.TryGetValue(startParagraph, out var paraSlices)) continue;
                if (!TryAnchor(paraSlices, startChar, startParagraph.TotalLength,
                        out int pageIndex, out float x, out float y, out float bottom))
                    continue;

                string author = string.IsNullOrWhiteSpace(root.Author) ? "Без автора" : root.Author;
                string meta = root.Date is DateTime date
                    ? date.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)
                    : string.Empty;

                items.Add(new MarkupItem(
                    null, root, pageIndex, x, y, bottom,
                    author, author, root.PlainText.Trim(), meta,
                    replies.TryGetValue(rootId, out int replyCount) ? replyCount : 0,
                    root.Done, rootRanges));
            }
        }

        /// <summary>
        /// Место знака на листе: кусок абзаца, в котором он стоит, левый край знака и
        /// верх и низ его строки.
        /// </summary>
        private static bool TryAnchor(
            List<ParaLayout> paraSlices, int charIndex, int length,
            out int pageIndex, out float xPt, out float yPt, out float bottomPt)
        {
            pageIndex = -1;
            xPt = yPt = bottomPt = 0f;
            charIndex = Math.Clamp(charIndex, 0, Math.Max(length, 0));

            foreach (var pl in paraSlices)
            {
                var layout = pl.Layout!;

                if (layout.Lines.Count == 0)
                {
                    pageIndex = pl.PageIndex;
                    xPt = pl.AbsXPt;
                    yPt = pl.Ypt;
                    bottomPt = pl.Ypt + pl.HeightPt;
                    return true;
                }

                int lineIndex = layout.GetLineIndexForChar(charIndex);
                if (lineIndex < pl.LineFrom || lineIndex >= pl.LineTo) continue;

                int from = Math.Clamp(pl.LineFrom, 0, layout.Lines.Count - 1);
                var line = layout.Lines[lineIndex];
                float top = pl.Ypt + (line.Y - layout.Lines[from].Y);
                var caret = layout.HitTestPosition(charIndex);

                pageIndex = pl.PageIndex;
                xPt = pl.AbsXPt + layout.LeftIndentPt + caret.X;
                yPt = top;
                bottomPt = top + line.Height;
                return true;
            }

            return false;
        }

        // ── Тексты выносок и подсказок ───────────────────────────────────

        /// <summary>Что сделано правкой: автор, заголовок, текст, отметка, удаление ли это.</summary>
        private static (string Author, string Head, string Body, RevisionInfo? Info, bool Deletion) DescribeRevision(
            ParagraphBlock paragraph, List<ParagraphBlock.CharCell> cells, RevisionService.Hit hit)
        {
            var props = paragraph.Properties;

            if (hit.IsMark && hit.From == hit.To)
            {
                if (props.MarkDeleted is { } markDeleted)
                    return (markDeleted.Author, "Удалено:", "конец абзаца — абзац сольётся со следующим", markDeleted, true);

                var markInserted = props.MarkInserted;
                return (markInserted?.Author ?? string.Empty, "Вставлено:", "новый абзац", markInserted, false);
            }

            if (hit.IsMark)
            {
                var change = props.FormatChange;
                string details = change?.Previous is { } previous
                    ? DescribeParagraphChange(previous, props)
                    : string.Empty;

                return (change?.Info.Author ?? string.Empty, "Отформатировано:",
                    details.Length > 0 ? details : "оформление абзаца", change?.Info, false);
            }

            var runProps = hit.From < cells.Count ? cells[hit.From].Props : null;
            string quote = Quote(cells, hit.From, hit.To);

            if (runProps?.Deleted is { } deleted)
                return (deleted.Author, deleted.IsMove ? "Перемещено (откуда):" : "Удалено:", quote, deleted, true);

            if (runProps?.Inserted is { } inserted)
                return (inserted.Author, inserted.IsMove ? "Перемещено (куда):" : "Вставлено:", quote, inserted, false);

            if (runProps?.FormatChange is { } formatChange)
            {
                string details = DescribeRunChange(formatChange.Previous, runProps);
                return (formatChange.Info.Author, "Отформатировано:",
                    details.Length > 0 ? details : "оформление знаков", formatChange.Info, false);
            }

            return (string.Empty, "Правка:", quote, null, false);
        }

        /// <summary>Текст правки в кавычках, длинный — с многоточием.</summary>
        private static string Quote(List<ParagraphBlock.CharCell> cells, int from, int to)
        {
            var sb = new StringBuilder();
            from = Math.Clamp(from, 0, cells.Count);
            to = Math.Clamp(to, from, cells.Count);

            for (int i = from; i < to && sb.Length <= MarkupQuoteMaxChars; i++)
            {
                var cell = cells[i];
                if (cell.IsInlineObject) sb.Append("[объект]");
                else if (cell.Ch == '\t') sb.Append(' ');
                else if (!char.IsControl(cell.Ch)) sb.Append(cell.Ch);
            }

            string text = sb.ToString();
            if (text.Length > MarkupQuoteMaxChars)
                text = text.Substring(0, MarkupQuoteMaxChars).TrimEnd() + "…";
            if (string.IsNullOrWhiteSpace(text)) text = "пробел";

            return "«" + text.Trim() + "»";
        }

        /// <summary>Что поменялось в оформлении знаков — словами Word: «Шрифт: полужирный, не курсив».</summary>
        private static string DescribeRunChange(RunProperties? before, RunProperties after)
        {
            var font = new List<string>();

            void Flag(string on, string off, bool was, bool now)
            {
                if (was != now) font.Add(now ? on : off);
            }

            Flag("полужирный", "не полужирный", before?.IsBold == true, after.IsBold == true);
            Flag("курсив", "не курсив", before?.IsItalic == true, after.IsItalic == true);
            Flag("подчёркнутый", "без подчёркивания", before?.IsUnderline == true, after.IsUnderline);
            Flag("зачёркнутый", "не зачёркнутый", before?.IsStrikethrough == true, after.IsStrikethrough);
            Flag("надстрочный", "не надстрочный", before?.IsSuperscript == true, after.IsSuperscript);
            Flag("подстрочный", "не подстрочный", before?.IsSubscript == true, after.IsSubscript);
            Flag("все прописные", "не все прописные", before?.IsAllCaps == true, after.IsAllCaps);
            Flag("малые прописные", "не малые прописные", before?.IsSmallCaps == true, after.IsSmallCaps);

            if (!string.Equals(before?.FontFamily, after.FontFamily, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(after.FontFamily))
                font.Add(after.FontFamily!);

            if (before?.FontSize != after.FontSize && after.FontSize is double size)
                font.Add(size.ToString("0.#", CultureInfo.InvariantCulture) + " пт");

            var parts = new List<string>();
            if (font.Count > 0) parts.Add("Шрифт: " + string.Join(", ", font));

            if (!string.Equals(before?.TextColor, after.TextColor, StringComparison.OrdinalIgnoreCase))
                parts.Add("Цвет шрифта: " + ColorName(after.TextColor));

            if (!string.Equals(before?.HighlightColor, after.HighlightColor, StringComparison.OrdinalIgnoreCase))
                parts.Add(string.IsNullOrEmpty(after.HighlightColor) ? "Без выделения цветом" : "Выделение цветом");

            if (!string.Equals(before?.StyleName, after.StyleName, StringComparison.Ordinal)
                && !string.IsNullOrEmpty(after.StyleName))
                parts.Add("Стиль: " + after.StyleName);

            return string.Join("; ", parts);
        }

        /// <summary>Цвет словами, если это один из основных; иначе код.</summary>
        private static string ColorName(string? color)
        {
            if (string.IsNullOrEmpty(color)) return "авто";
            if (!SKColor.TryParse(color, out var c)) return color!;

            (string Name, SKColor Color)[] named =
            {
                ("чёрный", new SKColor(0, 0, 0)), ("белый", new SKColor(255, 255, 255)),
                ("красный", new SKColor(255, 0, 0)), ("тёмно-красный", new SKColor(192, 0, 0)),
                ("зелёный", new SKColor(0, 176, 80)), ("тёмно-зелёный", new SKColor(0, 128, 0)),
                ("синий", new SKColor(0, 0, 255)), ("тёмно-синий", new SKColor(0, 32, 96)),
                ("жёлтый", new SKColor(255, 255, 0)), ("оранжевый", new SKColor(255, 192, 0)),
                ("фиолетовый", new SKColor(112, 48, 160)), ("серый", new SKColor(128, 128, 128)),
                ("голубой", new SKColor(0, 176, 240))
            };

            foreach (var (name, value) in named)
            {
                int d = Math.Abs(value.Red - c.Red) + Math.Abs(value.Green - c.Green) + Math.Abs(value.Blue - c.Blue);
                if (d <= 40) return name;
            }

            return "#" + c.Red.ToString("X2") + c.Green.ToString("X2") + c.Blue.ToString("X2");
        }

        /// <summary>Что поменялось в оформлении абзаца — словами Word.</summary>
        private static string DescribeParagraphChange(ParagraphProperties before, ParagraphProperties after)
        {
            var parts = new List<string>();

            if (before.Alignment != after.Alignment && after.Alignment is { } alignment)
            {
                parts.Add(alignment switch
                {
                    TextAlignment.Center => "По центру",
                    TextAlignment.Right => "По правому краю",
                    TextAlignment.Justify => "По ширине",
                    _ => "По левому краю"
                });
            }

            if (before.LeftIndent != after.LeftIndent || before.RightIndent != after.RightIndent)
                parts.Add("Отступы");
            if (before.FirstLineIndent != after.FirstLineIndent)
                parts.Add("Первая строка");
            if (before.SpaceBefore != after.SpaceBefore || before.SpaceAfter != after.SpaceAfter)
                parts.Add("Интервалы");
            if (before.LineSpacingRule != after.LineSpacingRule || before.LineSpacingValue != after.LineSpacingValue)
                parts.Add("Междустрочный интервал");
            if (!string.Equals(before.StyleName, after.StyleName, StringComparison.Ordinal)
                && !string.IsNullOrEmpty(after.StyleName))
                parts.Add("Стиль: " + after.StyleName);
            if (before.KeepWithNext != after.KeepWithNext || before.KeepTogether != after.KeepTogether
                || before.PageBreakBefore != after.PageBreakBefore)
                parts.Add("Положение на странице");
            if (!string.Equals(before.ShadingColor, after.ShadingColor, StringComparison.OrdinalIgnoreCase)
                || (before.Borders is null) != (after.Borders is null))
                parts.Add("Рамка и заливка");

            return string.Join(", ", parts);
        }

        private static string FormatMeta(string author, RevisionInfo? info)
        {
            string name = string.IsNullOrWhiteSpace(author) ? "Без автора" : author;
            if (info?.Date is DateTime date)
                return name + ", " + date.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
            return name;
        }

        // ── Раскладка ярлыков ────────────────────────────────────────────

        private static SKFont MarkupFont(bool bold, float size)
            => new(SKTextRenderer.ResolveTypeface("Segoe UI", bold, false), size);

        /// <summary>
        /// Перенос текста по словам в ширину, не больше maxLines строк; не
        /// уместившееся кончается многоточием. firstIndent — ширина, уже занятая в
        /// первой строке.
        /// </summary>
        private static List<string> WrapMarkupText(string text, SKFont font, float width, int maxLines, float firstIndent = 0f)
        {
            var lines = new List<string>();
            var words = new Queue<string>(text.Replace('\n', ' ').Replace('\r', ' ')
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));
            var current = new StringBuilder();

            float LineWidth() => lines.Count == 0 ? width - firstIndent : width;

            while (words.Count > 0 && lines.Count < maxLines)
            {
                string word = words.Peek();
                string candidate = current.Length == 0 ? word : current + " " + word;

                if (font.MeasureText(candidate) <= LineWidth())
                {
                    current.Clear().Append(candidate);
                    words.Dequeue();
                    continue;
                }

                if (current.Length > 0)
                {
                    lines.Add(current.ToString());
                    current.Clear();
                    continue;
                }

                // Слово шире строки режется по знакам.
                int cut = word.Length;
                while (cut > 1 && font.MeasureText(word.Substring(0, cut)) > LineWidth()) cut--;
                lines.Add(word.Substring(0, cut));
                words.Dequeue();
                if (cut < word.Length)
                {
                    var rest = new Queue<string>();
                    rest.Enqueue(word.Substring(cut));
                    foreach (var left in words) rest.Enqueue(left);
                    words = rest;
                }
            }

            if (current.Length > 0)
            {
                if (lines.Count < maxLines) lines.Add(current.ToString());
                else words.Enqueue(current.ToString());
            }

            if (words.Count > 0 && lines.Count > 0)
            {
                string last = lines[^1];
                float lastWidth = lines.Count == 1 ? width - firstIndent : width;
                while (last.Length > 1 && font.MeasureText(last + "…") > lastWidth)
                    last = last.Substring(0, last.Length - 1);
                lines[^1] = last + "…";
            }

            return lines;
        }

        /// <summary>Строка, урезанная до ширины, с многоточием.</summary>
        private static string FitMarkupText(string text, SKFont font, float width)
        {
            text = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
            if (font.MeasureText(text) <= width) return text;

            int length = text.Length;
            while (length > 1 && font.MeasureText(text.Substring(0, length).TrimEnd() + "…") > width) length--;
            return text.Substring(0, length).TrimEnd() + "…";
        }

        /// <summary>Подпись ярлыка: у примечания — его начало, у правки — что сделано.</summary>
        private static string MarkupLabelText(MarkupItem item)
        {
            if (item.Comment is not null)
                return item.Body.Length > 0 ? item.Body : "Примечание";

            string head = item.Head.TrimEnd(':');
            return head.StartsWith("Отформатировано", StringComparison.Ordinal)
                ? "Формат: " + item.Body
                : head + " " + item.Body;
        }

        /// <summary>
        /// Ярлыки на поле в координатах их листов: справа от листа, на высоте своего
        /// места в тексте; сошедшиеся по высоте расходятся вниз.
        /// </summary>
        private static List<MarkupBox> LayoutMarkup(List<MarkupItem> items, List<PageRect> pages)
        {
            var boxes = new List<MarkupBox>(items.Count);
            using var labelFont = MarkupFont(false, MarkupBodyPt);

            float textLeft = MarkupLabelHeightPt + 2f;
            int lastPage = -1;
            float lastBottom = float.NegativeInfinity;

            foreach (var item in items)
            {
                if (item.PageIndex < 0 || item.PageIndex >= pages.Count) continue;

                var page = pages[item.PageIndex];
                string label = FitMarkupText(MarkupLabelText(item), labelFont,
                    MarkupLabelMaxWidthPt - textLeft - 8f);
                float width = Math.Min(MarkupLabelMaxWidthPt, textLeft + labelFont.MeasureText(label) + 8f);

                float lineMid = (item.AnchorYPt + item.AnchorBottomPt) / 2f;
                float top = lineMid - MarkupLabelHeightPt / 2f;
                if (item.PageIndex == lastPage && top < lastBottom + MarkupLabelGapPt)
                    top = lastBottom + MarkupLabelGapPt;

                lastPage = item.PageIndex;
                lastBottom = top + MarkupLabelHeightPt;

                float left = page.PadLeftPt + page.WidthPt + page.GutterShiftPt + MarkupLabelInsetPt;
                boxes.Add(new MarkupBox(item, new SKRect(left, top, left + width, top + MarkupLabelHeightPt), label));
            }

            return boxes;
        }

        /// <summary>
        /// Раскрытая выноска ярлыка: от его верхнего левого угла вправо; если в полосу не
        /// влезает — сдвигается влево, на поле листа, и вниз не дальше листа.
        /// </summary>
        private static MarkupCard LayoutMarkupCard(MarkupBox box, PageRect page)
        {
            var item = box.Item;
            using var headFont = MarkupFont(true, MarkupHeadPt);
            using var bodyFont = MarkupFont(false, MarkupBodyPt);

            float textWidth = MarkupCardWidthPt - MarkupCardPadPt * 2f;
            string body = item.Comment is not null ? item.Body : item.Head + " " + item.Body;
            var lines = WrapMarkupText(body, bodyFont, textWidth, MarkupCardMaxLines);
            if (lines.Count == 0) lines.Add(string.Empty);

            float height = MarkupCardPadPt
                + MarkupAvatarRadiusPt * 2f + 4f                 // кто и когда
                + lines.Count * MarkupLinePt
                + (item.Comment is not null && item.Replies > 0 ? MarkupLinePt : 0f)
                + 6f + MarkupButtonHeightPt                      // кнопки
                + MarkupCardPadPt;

            float laneRight = page.PadLeftPt + page.WidthPt + page.GutterShiftPt + MarkupPaneWidthPt - 4f;
            float left = Math.Min(box.Rect.Left, laneRight - MarkupCardWidthPt);
            float top = box.Rect.Top;
            float pageBottom = page.Ypt + page.HeightPt - 4f;
            if (top + height > pageBottom) top = Math.Max(page.Ypt + 4f, pageBottom - height);

            var rect = new SKRect(left, top, left + MarkupCardWidthPt, top + height);

            float buttonTop = rect.Bottom - MarkupCardPadPt - MarkupButtonHeightPt;
            SKRect accept = SKRect.Empty, reject = SKRect.Empty, open = SKRect.Empty;

            if (item.Comment is not null)
            {
                float w = headFont.MeasureText("Открыть обсуждение") + 16f;
                open = new SKRect(rect.Left + MarkupCardPadPt, buttonTop, rect.Left + MarkupCardPadPt + w, buttonTop + MarkupButtonHeightPt);
            }
            else
            {
                float acceptW = headFont.MeasureText("Принять") + 22f;
                float rejectW = headFont.MeasureText("Отклонить") + 22f;
                accept = new SKRect(rect.Left + MarkupCardPadPt, buttonTop, rect.Left + MarkupCardPadPt + acceptW, buttonTop + MarkupButtonHeightPt);
                reject = new SKRect(accept.Right + 6f, buttonTop, accept.Right + 6f + rejectW, buttonTop + MarkupButtonHeightPt);
            }

            return new MarkupCard(item, rect, lines, accept, reject, open);
        }

        // ── Отрисовка ────────────────────────────────────────────────────

        /// <summary>
        /// Матовое стекло: фон под прямоугольником размывается и притемняется. Читается
        /// поверх любой картинки темы и любого листа.
        /// </summary>
        private static void DrawMarkupGlass(SKCanvas canvas, SKRect rect, float radius, byte tintAlpha, SKColor? border, float borderWidth)
        {
            var rounded = new SKRoundRect(rect, radius, radius);

            using (var shadow = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 0x38) })
            {
                var s = rect;
                s.Offset(0f, 1.5f);
                s.Inflate(0.5f, 0.5f);
                canvas.DrawRoundRect(s, radius, radius, shadow);
            }

            canvas.Save();
            canvas.ClipRoundRect(rounded, antialias: true);

            using (var blur = SKImageFilter.CreateBlur(7f, 7f))
            {
                var layer = new SKCanvasSaveLayerRec { Bounds = rect, Backdrop = blur };
                canvas.SaveLayer(layer);
                canvas.Restore();
            }

            using (var tint = new SKPaint { IsAntialias = true, Color = new SKColor(0x1C, 0x1C, 0x22, tintAlpha) })
                canvas.DrawRect(rect, tint);

            // Блик по верхней кромке — стекло, а не плашка.
            using (var sheen = new SKPaint { IsAntialias = true, Color = new SKColor(0xFF, 0xFF, 0xFF, 0x12) })
                canvas.DrawRect(rect.Left, rect.Top, rect.Width, Math.Min(rect.Height * 0.45f, 9f), sheen);

            canvas.Restore();

            using var edge = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = borderWidth,
                Color = border ?? new SKColor(0xFF, 0xFF, 0xFF, 0x2E)
            };
            canvas.DrawRoundRect(rounded, edge);
        }

        /// <summary>Кружок автора: цвет автора, белые инициалы.</summary>
        private static void DrawMarkupAvatar(SKCanvas canvas, float cx, float cy, float radius, SKColor color, string initials)
        {
            using (var fill = new SKPaint { IsAntialias = true, Color = color })
                canvas.DrawCircle(cx, cy, radius, fill);

            if (initials.Length > 2) initials = initials.Substring(0, 2);
            if (initials.Length == 0) return;

            using var font = MarkupFont(true, radius * 0.95f);
            using var text = new SKPaint { IsAntialias = true, Color = SKColors.White };
            float w = font.MeasureText(initials);
            font.GetFontMetrics(out var m);
            canvas.DrawText(initials, cx - w / 2f, cy - (m.Ascent + m.Descent) / 2f, SKTextAlign.Left, font, text);
        }

        private static string InitialsOf(string author)
        {
            var parts = author.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "?";
            if (parts.Length == 1) return parts[0].Substring(0, 1).ToUpperInvariant();
            return (parts[0].Substring(0, 1) + parts[^1].Substring(0, 1)).ToUpperInvariant();
        }

        /// <summary>
        /// Поле исправлений видимых листов: связи, ярлыки, раскрытая выноска под
        /// указателем, подсветка её места в тексте и подсказка над правкой. Рисуется
        /// поверх листов, вне их клипа: каждый ярлык — на визуальном месте своего листа.
        /// </summary>
        private void DrawRevisionCards(SKCanvas canvas, List<ParaLayout> layouts, List<PageRect> pages, int firstPage, int lastPage)
        {
            // Подсветка места под указателем в панели рецензии (DocumentCanvas.ReviewPanel).
            DrawReviewHighlight(canvas, layouts, pages, firstPage, lastPage);

            if (!MarkupPaneVisible || pages.Count == 0)
            {
                UpdateRevisionLane(false);
                return;
            }

            var items = GetMarkupItems(layouts);
            UpdateRevisionLane(items.Count > 0);

            if (items.Count > 0)
            {
                var hoverKey = _markupHoverKey;
                var caretKey = CaretMarkupKey(items);
                var boxes = LayoutMarkup(items, pages);

                MarkupBox? hovered = null;
                foreach (var box in boxes)
                {
                    if (box.Item.PageIndex < firstPage || box.Item.PageIndex > lastPage) continue;

                    bool isHover = Equals(box.Item.Key, hoverKey);
                    bool lit = isHover || Equals(box.Item.Key, caretKey);
                    DrawMarkupLabel(canvas, layouts, pages, box, lit);
                    if (isHover) hovered = box;
                }

                // Раскрытая выноска — поверх всех ярлыков.
                if (hovered is { } open)
                {
                    var page = pages[open.Item.PageIndex];
                    var (dx, dy) = PageVisualDelta(open.Item.PageIndex, pages);
                    canvas.Save();
                    canvas.Translate(dx, dy);
                    DrawMarkupCard(canvas, LayoutMarkupCard(open, page));
                    canvas.Restore();
                }
            }

            if (_markupTip is { } tip)
                DrawMarkupTip(canvas, pages, tip);
        }

        private SKColor MarkupColor(MarkupItem item)
        {
            if (item.Done) return new SKColor(0x9A, 0x9A, 0x9A);
            return _styleResolver?.AuthorColor(item.Author) ?? new SKColor(0xC0, 0x40, 0x40);
        }

        /// <summary>Ярлык и его связь с местом в тексте; подсвеченный — со сплошной связью и подсветкой места.</summary>
        private void DrawMarkupLabel(SKCanvas canvas, List<ParaLayout> layouts, List<PageRect> pages, MarkupBox box, bool lit)
        {
            var item = box.Item;
            var page = pages[item.PageIndex];
            var (dx, dy) = PageVisualDelta(item.PageIndex, pages);
            var color = MarkupColor(item);

            canvas.Save();
            canvas.Translate(dx, dy);

            if (lit) DrawMarkupRanges(canvas, layouts, item, color.WithAlpha(0x50));

            // Связь: тонкий пунктир от места в тексте до края листа и к ярлыку; у
            // подсвеченного — сплошная линия.
            float pageRight = page.PadLeftPt + page.WidthPt + page.GutterShiftPt;
            float lineY = item.AnchorBottomPt - 1f;
            float labelY = box.Rect.MidY;

            using (var dash = SKPathEffect.CreateDash(new[] { 1.5f, 2.5f }, 0f))
            using (var connector = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = lit ? 1.2f : 0.8f,
                StrokeCap = SKStrokeCap.Round,
                Color = color.WithAlpha(lit ? (byte)0xFF : (byte)0x8C),
                PathEffect = lit ? null : dash
            })
            using (var path = new SKPath())
            {
                path.MoveTo(item.AnchorXPt, lineY);
                path.LineTo(pageRight + 3f, lineY);
                path.CubicTo(
                    pageRight + MarkupLabelInsetPt * 0.6f, lineY,
                    box.Rect.Left - MarkupLabelInsetPt * 0.6f, labelY,
                    box.Rect.Left, labelY);
                canvas.DrawPath(path, connector);
            }

            // Точка у места в тексте — видно, откуда идёт связь.
            using (var dot = new SKPaint { IsAntialias = true, Color = color })
                canvas.DrawCircle(item.AnchorXPt, lineY, lit ? 1.8f : 1.3f, dot);

            // Сам ярлык: стекло, кружок автора, подпись.
            float radius = MarkupLabelHeightPt / 2f;
            DrawMarkupGlass(canvas, box.Rect, radius, lit ? (byte)0xE0 : (byte)0xC0,
                lit ? color : null, lit ? 1.2f : 0.6f);

            string initials = item.Comment?.DisplayInitials ?? InitialsOf(item.Author);
            float avatarR = radius - 2.5f;
            DrawMarkupAvatar(canvas, box.Rect.Left + radius, box.Rect.MidY, avatarR, color, initials);

            using var font = MarkupFont(false, MarkupBodyPt);
            using var text = new SKPaint
            {
                IsAntialias = true,
                Color = item.Done ? new SKColor(0xFF, 0xFF, 0xFF, 0x90) : new SKColor(0xF2, 0xF2, 0xF2)
            };
            font.GetFontMetrics(out var m);
            float baseline = box.Rect.MidY - (m.Ascent + m.Descent) / 2f;
            canvas.DrawText(box.Label, box.Rect.Left + MarkupLabelHeightPt + 2f, baseline, SKTextAlign.Left, font, text);

            canvas.Restore();
        }

        /// <summary>Раскрытая выноска: автор и время, весь текст, кнопки.</summary>
        private void DrawMarkupCard(SKCanvas canvas, MarkupCard card)
        {
            var item = card.Item;
            var color = MarkupColor(item);
            var rect = card.Rect;

            DrawMarkupGlass(canvas, rect, 6f, 0xEC, color, 1.2f);

            // Полоска цвета автора сверху.
            canvas.Save();
            canvas.ClipRoundRect(new SKRoundRect(rect, 6f, 6f), antialias: true);
            using (var band = new SKPaint { IsAntialias = true, Color = color })
                canvas.DrawRect(rect.Left, rect.Top, rect.Width, 2.5f, band);
            canvas.Restore();

            using var headFont = MarkupFont(true, MarkupHeadPt);
            using var bodyFont = MarkupFont(false, MarkupBodyPt);
            using var metaFont = MarkupFont(false, MarkupMetaPt);
            using var text = new SKPaint { IsAntialias = true };

            float x = rect.Left + MarkupCardPadPt;
            float cy = rect.Top + MarkupCardPadPt + MarkupAvatarRadiusPt + 1f;

            string initials = item.Comment?.DisplayInitials ?? InitialsOf(item.Author);
            DrawMarkupAvatar(canvas, x + MarkupAvatarRadiusPt, cy, MarkupAvatarRadiusPt, color, initials);

            float nameX = x + MarkupAvatarRadiusPt * 2f + 5f;
            string author = string.IsNullOrWhiteSpace(item.Author) ? "Без автора" : item.Author;
            text.Color = SKColors.White;
            canvas.DrawText(FitMarkupText(author, headFont, rect.Right - MarkupCardPadPt - nameX),
                nameX, cy - 1f, SKTextAlign.Left, headFont, text);

            // Время: у правки в Meta оно стоит после имени, у примечания Meta — само время.
            string when = item.Comment is not null
                ? item.Meta
                : item.Meta.Length > author.Length + 2 && item.Meta.StartsWith(author, StringComparison.Ordinal)
                    ? item.Meta.Substring(author.Length + 2)
                    : string.Empty;
            if (item.Done) when = when.Length > 0 ? when + " · закрыто" : "закрыто";

            text.Color = new SKColor(0xFF, 0xFF, 0xFF, 0x99);
            canvas.DrawText(when, nameX, cy + MarkupMetaPt + 1f, SKTextAlign.Left, metaFont, text);

            float y = rect.Top + MarkupCardPadPt + MarkupAvatarRadiusPt * 2f + 4f;
            for (int i = 0; i < card.BodyLines.Count; i++)
            {
                y += MarkupLinePt;
                string line = card.BodyLines[i];

                // У правки первое слово строки — что сделано, цветом автора.
                if (i == 0 && item.Comment is null && line.StartsWith(item.Head, StringComparison.Ordinal))
                {
                    text.Color = Lighter(color);
                    canvas.DrawText(item.Head, x, y - 2f, SKTextAlign.Left, headFont, text);
                    float restX = x + headFont.MeasureText(item.Head + " ");
                    text.Color = new SKColor(0xEE, 0xEE, 0xEE);
                    canvas.DrawText(line.Substring(item.Head.Length).TrimStart(), restX, y - 2f, SKTextAlign.Left, bodyFont, text);
                    continue;
                }

                text.Color = new SKColor(0xEE, 0xEE, 0xEE);
                canvas.DrawText(line, x, y - 2f, SKTextAlign.Left, bodyFont, text);
            }

            if (item.Comment is not null && item.Replies > 0)
            {
                y += MarkupLinePt;
                text.Color = new SKColor(0xFF, 0xFF, 0xFF, 0x99);
                canvas.DrawText("Ответов: " + item.Replies.ToString(CultureInfo.InvariantCulture),
                    x, y - 2f, SKTextAlign.Left, metaFont, text);
            }

            if (item.Comment is not null)
            {
                DrawMarkupButton(canvas, card.Open, "Открыть обсуждение", Lighter(color), icon: 0);
            }
            else
            {
                DrawMarkupButton(canvas, card.Accept, "Принять", new SKColor(0x4C, 0xC2, 0x6A), icon: 1);
                DrawMarkupButton(canvas, card.Reject, "Отклонить", new SKColor(0xF0, 0x6A, 0x6A), icon: 2);
            }
        }

        /// <summary>Кнопка выноски: капсула с обводкой своего цвета; icon — 1 галочка, 2 крестик, 0 без значка.</summary>
        private static void DrawMarkupButton(SKCanvas canvas, SKRect rect, string label, SKColor color, int icon)
        {
            float r = rect.Height / 2f;
            using (var fill = new SKPaint { IsAntialias = true, Color = color.WithAlpha(0x2C) })
                canvas.DrawRoundRect(rect, r, r, fill);
            using (var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 0.8f, Color = color.WithAlpha(0xC0) })
                canvas.DrawRoundRect(rect, r, r, edge);

            float x = rect.Left + 7f;
            using var mark = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.2f,
                StrokeCap = SKStrokeCap.Round,
                Color = color
            };

            if (icon == 1)
            {
                using var check = new SKPath();
                check.MoveTo(x, rect.MidY + 0.2f);
                check.LineTo(x + 2f, rect.MidY + 2.2f);
                check.LineTo(x + 5.5f, rect.MidY - 2f);
                canvas.DrawPath(check, mark);
                x += 9f;
            }
            else if (icon == 2)
            {
                canvas.DrawLine(x + 0.5f, rect.MidY - 2.2f, x + 4.9f, rect.MidY + 2.2f, mark);
                canvas.DrawLine(x + 0.5f, rect.MidY + 2.2f, x + 4.9f, rect.MidY - 2.2f, mark);
                x += 9f;
            }
            else
            {
                x += 1f;
            }

            using var font = MarkupFont(true, MarkupMetaPt + 0.3f);
            using var text = new SKPaint { IsAntialias = true, Color = SKColors.White };
            font.GetFontMetrics(out var m);
            canvas.DrawText(label, x, rect.MidY - (m.Ascent + m.Descent) / 2f, SKTextAlign.Left, font, text);
        }

        /// <summary>Подсветка места выноски в тексте — куски абзацев на их строках.</summary>
        private void DrawMarkupRanges(SKCanvas canvas, List<ParaLayout> layouts, MarkupItem item, SKColor color)
        {
            using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color };

            foreach (var (paragraph, from, to) in item.Ranges)
            {
                if (to <= from) continue;

                foreach (var pl in layouts)
                {
                    var model = pl.Cell?.ParaBlock ?? pl.Vm?.Model;
                    if (!ReferenceEquals(model, paragraph) || pl.Layout is not { } sl) continue;

                    float yBase = pl.LineFrom < sl.Lines.Count ? sl.Lines[pl.LineFrom].Y : 0f;
                    for (int li = pl.LineFrom; li < pl.LineTo && li < sl.Lines.Count; li++)
                    {
                        var line = sl.Lines[li];
                        int lineFrom = Math.Max(from, line.FirstCharIndex);
                        int lineTo = Math.Min(to, line.LastCharIndex + 1);
                        if (lineTo <= lineFrom) continue;

                        DrawSelectionSubRange(canvas, sl, li, lineFrom, lineTo, pl.AbsXPt, pl.Ypt, yBase, paint);
                    }
                }
            }
        }

        /// <summary>Подсказка над правкой в тексте: стеклянная плашка у указателя.</summary>
        private void DrawMarkupTip(SKCanvas canvas, List<PageRect> pages, MarkupTip tip)
        {
            if (tip.PageIndex < 0 || tip.PageIndex >= pages.Count) return;
            var (dx, dy) = PageVisualDelta(tip.PageIndex, pages);

            using var headFont = MarkupFont(true, MarkupHeadPt);
            using var bodyFont = MarkupFont(false, MarkupBodyPt);
            using var metaFont = MarkupFont(false, MarkupMetaPt);

            const float maxWidth = 230f;
            const float pad = 7f;
            float headWidth = headFont.MeasureText(tip.Head + " ");
            var bodyLines = WrapMarkupText(tip.Body, bodyFont, maxWidth - pad * 2f, 3, headWidth);
            if (bodyLines.Count == 0) bodyLines.Add(string.Empty);

            float width = pad * 2f + Math.Max(metaFont.MeasureText(tip.Meta), headWidth + bodyFont.MeasureText(bodyLines[0]));
            foreach (var line in bodyLines) width = Math.Max(width, pad * 2f + bodyFont.MeasureText(line));
            width = Math.Min(width, maxWidth);

            float height = pad * 2f + MarkupLinePt * (bodyLines.Count + 1);
            float left = tip.XPt + dx + 12f;
            float top = tip.YPt + dy + 16f;
            var rect = new SKRect(left, top, left + width, top + height);

            DrawMarkupGlass(canvas, rect, 5f, 0xE8, null, 0.6f);

            using var text = new SKPaint { IsAntialias = true };
            float x = rect.Left + pad;
            float y = rect.Top + pad + MarkupHeadPt;

            text.Color = new SKColor(0xFF, 0xFF, 0xFF, 0x99);
            canvas.DrawText(tip.Meta, x, y, SKTextAlign.Left, metaFont, text);

            y += MarkupLinePt;
            text.Color = SKColors.White;
            canvas.DrawText(tip.Head, x, y, SKTextAlign.Left, headFont, text);
            text.Color = new SKColor(0xEE, 0xEE, 0xEE);
            for (int i = 0; i < bodyLines.Count; i++)
            {
                if (i > 0) y += MarkupLinePt;
                canvas.DrawText(bodyLines[i], i == 0 ? x + headWidth : x, y, SKTextAlign.Left, bodyFont, text);
            }
        }

        /// <summary>Цвет автора, высветленный для текста на тёмном стекле.</summary>
        private static SKColor Lighter(SKColor color)
            => new((byte)(color.Red + (255 - color.Red) * 0.35f),
                   (byte)(color.Green + (255 - color.Green) * 0.35f),
                   (byte)(color.Blue + (255 - color.Blue) * 0.35f));

        /// <summary>Выноска места под кареткой: правка под ней или обсуждение, в которое она стоит.</summary>
        private object? CaretMarkupKey(List<MarkupItem> items)
        {
            var paragraph = ParagraphAtSlice(_caretPara);
            if (paragraph is null) return null;
            return MarkupKeyAt(items, paragraph, _caretChar);
        }

        /// <summary>Выноска, чьё место в тексте содержит этот знак абзаца.</summary>
        private static object? MarkupKeyAt(List<MarkupItem> items, ParagraphBlock paragraph, int charIndex)
        {
            foreach (var item in items)
            {
                foreach (var (p, from, to) in item.Ranges)
                {
                    if (!ReferenceEquals(p, paragraph)) continue;
                    if (charIndex >= from && charIndex < to) return item.Key;
                }
            }
            return null;
        }

        // ── Указатель ────────────────────────────────────────────────────

        /// <summary>
        /// Что под точкой холста (визуальной, как у указателя): раскрытая выноска под
        /// указателем или ярлык. Точка возвращается в координатах листа.
        /// </summary>
        private bool TryHitMarkup(float visualXPt, float visualYPt,
            out MarkupBox hitBox, out MarkupCard? hitCard, out float localX, out float localY)
        {
            hitBox = default;
            hitCard = null;
            localX = localY = 0f;
            if (!MarkupPaneVisible || DocVm is null || !_revisionLaneActive) return false;

            List<ParaLayout> layouts;
            List<PageRect> pages;
            lock (_renderLock)
            {
                layouts = _layouts;
                pages = _pages;
            }

            var items = GetMarkupItems(layouts);
            if (items.Count == 0) return false;

            float shiftX = GetPageShiftXPt();
            var boxes = LayoutMarkup(items, pages);

            // Раскрытая выноска лежит поверх ярлыков и ловит указатель первой.
            if (_markupHoverKey is { } hoverKey)
            {
                foreach (var box in boxes)
                {
                    if (!Equals(box.Item.Key, hoverKey)) continue;

                    var (dx, dy) = PageVisualDelta(box.Item.PageIndex, pages);
                    float x = visualXPt - dx - shiftX;
                    float y = visualYPt - dy;
                    var card = LayoutMarkupCard(box, pages[box.Item.PageIndex]);

                    if (card.Rect.Contains(x, y) || box.Rect.Contains(x, y))
                    {
                        hitBox = box;
                        hitCard = card;
                        localX = x;
                        localY = y;
                        return true;
                    }
                    break;
                }
            }

            foreach (var box in boxes)
            {
                var (dx, dy) = PageVisualDelta(box.Item.PageIndex, pages);
                float x = visualXPt - dx - shiftX;
                float y = visualYPt - dy;

                var hit = box.Rect;
                hit.Inflate(1f, MarkupLabelGapPt / 2f);
                if (!hit.Contains(x, y)) continue;

                hitBox = box;
                localX = x;
                localY = y;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Нажатие по полю исправлений: кнопки раскрытой выноски принимают или отклоняют
        /// правку или открывают обсуждение; нажатие по ярлыку правки выделяет её в
        /// тексте, по ярлыку примечания — открывает обсуждение.
        /// </summary>
        private bool TryRevisionCardPointerPressed(float visualXPt, float visualYPt)
        {
            if (!TryHitMarkup(visualXPt, visualYPt, out var box, out var card, out float x, out float y)) return false;

            var item = box.Item;

            if (card is { } open)
            {
                if (item.Comment is { } discussion && open.Open.Contains(x, y))
                {
                    OpenMarkupComment(discussion);
                    return true;
                }

                if (item.Hit is { } cardHit)
                {
                    if (open.Accept.Contains(x, y) || open.Reject.Contains(x, y))
                    {
                        if (!IsEditingBlocked) ResolveRevisionHit(cardHit, open.Accept.Contains(x, y));
                        return true;
                    }
                }
            }

            if (item.Comment is { } comment)
            {
                OpenMarkupComment(comment);
                return true;
            }

            if (item.Hit is { } hit) SelectRevision(hit);
            return true;
        }

        private void OpenMarkupComment(CommentModel comment)
        {
            if (DocVm is { } doc)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => doc.RequestCommentThread(comment));
        }

        /// <summary>
        /// Указатель двинулся без нажатой кнопки: ярлык под ним раскрывается, ярлык места
        /// в тексте под ним подсвечивается, над правкой в тексте всплывает подсказка.
        /// True — указатель над полем исправлений: курсор — рука, дальше разбирать нечего.
        /// </summary>
        private bool UpdateMarkupHover(float visualXPt, float visualYPt)
        {
            if (!MarkupPaneVisible || DocVm is null)
            {
                ClearMarkupHover();
                return false;
            }

            object? key = null;
            MarkupTip? tip = null;
            bool overPane = false;

            if (TryHitMarkup(visualXPt, visualYPt, out var box, out _, out _, out _))
            {
                key = box.Item.Key;
                overPane = true;
            }
            else
            {
                var (lx, ly) = VisualToLogicalPt(visualXPt, visualYPt);
                lx -= GetPageShiftXPt();

                if (TryCharUnderPoint(lx, ly, out var pl, out var paragraph, out int charIndex))
                {
                    List<ParaLayout> layouts;
                    lock (_renderLock) layouts = _layouts;
                    key = MarkupKeyAt(GetMarkupItems(layouts), paragraph, charIndex);

                    var cells = paragraph.ToCharCells();
                    foreach (var hit in RevisionService.HitsIn(paragraph))
                    {
                        if (hit.IsMark) continue;
                        if (charIndex < hit.From || charIndex >= hit.To) continue;

                        var (author, head, body, info, _) = DescribeRevision(paragraph, cells, hit);
                        tip = new MarkupTip(head, FormatMeta(author, info), body, pl.PageIndex, lx, ly);
                        break;
                    }

                    // Место, у которого есть ярлык, подсказку не дублирует: всё видно в
                    // раскрытой выноске.
                    if (key is not null) tip = null;
                }
            }

            bool changed = !Equals(key, _markupHoverKey)
                || (tip is null) != (_markupTip is null)
                || tip is not null && _markupTip is not null
                   && (tip.Body != _markupTip.Body || Math.Abs(tip.XPt - _markupTip.XPt) > 0.5f || Math.Abs(tip.YPt - _markupTip.YPt) > 0.5f);

            _markupHoverKey = key;
            _markupTip = tip;
            if (changed) InvalidateFull();

            if (overPane) Cursor = new Cursor(StandardCursorType.Hand);
            return overPane;
        }

        /// <summary>Указатель ушёл с холста или начал жест: подсветка, выноска и подсказка гаснут.</summary>
        private void ClearMarkupHover()
        {
            if (_markupHoverKey is null && _markupTip is null) return;
            _markupHoverKey = null;
            _markupTip = null;
            InvalidateFull();
        }

        /// <summary>
        /// Знак под точкой листа (логической): кусок абзаца вне таблиц, строка и знак,
        /// если точка над самим текстом строки.
        /// </summary>
        private bool TryCharUnderPoint(float xPt, float yPt, out ParaLayout slice, out ParagraphBlock paragraph, out int charIndex)
        {
            slice = null!;
            paragraph = null!;
            charIndex = -1;

            List<ParaLayout> layouts;
            lock (_renderLock) layouts = _layouts;

            foreach (var pl in layouts)
            {
                if (pl.Cell is not null || pl.Layout is not { } layout || pl.Vm?.Model is not { } model) continue;
                if (yPt < pl.Ypt || yPt > pl.Ypt + pl.HeightPt) continue;
                if (layout.Lines.Count == 0) continue;

                float yBase = pl.LineFrom < layout.Lines.Count ? layout.Lines[pl.LineFrom].Y : 0f;
                float localY = yPt - pl.Ypt + yBase;

                for (int li = pl.LineFrom; li < pl.LineTo && li < layout.Lines.Count; li++)
                {
                    var line = layout.Lines[li];
                    if (localY < line.Y || localY > line.Y + line.Height) continue;

                    float localX = xPt - pl.AbsXPt - layout.LeftIndentPt - LineAlignShift(layout, li);
                    localX = UnstretchJustifyX(layout, li, localX);
                    float lineLeft = line.Segments.Count > 0 ? line.Segments[0].X : 0f;
                    if (localX < lineLeft || localX > line.TextWidth) return false;

                    var hit = layout.HitTestPoint(localX, localY);
                    int index = hit.CharIndex;

                    // Точка ближе к правому краю знака даёт позицию после него: знак под
                    // указателем — тот, что левее.
                    if (index > line.FirstCharIndex
                        && layout.TryGetCharBox(li, index - 1, out float charLeft, out float charRight)
                        && localX >= charLeft && localX <= charRight)
                        index--;

                    slice = pl;
                    paragraph = model;
                    charIndex = index;
                    return true;
                }
            }

            return false;
        }

        /// <summary>Принять или отклонить одну правку с выноски.</summary>
        private void ResolveRevisionHit(RevisionService.Hit hit, bool accept)
        {
            if (RevisionResolveLocked()) return;

            var doc = DocVm!.Document;
            var paragraph = hit.Paragraph;
            if (RevisionService.Locate(doc, paragraph) is null) return;

            var scope = hit.IsMark
                ? new RevisionService.Scope(hit.To, hit.To, true)
                : new RevisionService.Scope(hit.From, hit.To, false);

            BeginTrackedSnapshot(accept ? "Принять исправление" : "Отклонить исправление");
            try
            {
                var (touched, structureChanged) = RevisionService.Resolve(
                    doc, candidate => ReferenceEquals(candidate, paragraph) ? scope : null, accept);
                RefreshAfterTrackedChange(touched, structureChanged);
                RestoreCaretAfterResolve(paragraph, hit.From);
            }
            finally
            {
                EndTrackedSnapshot();
            }

            _markupHoverKey = null;
            InvalidateFull();
        }
    }
}
