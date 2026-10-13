using SkiaSharp;
using System;
using System.Collections.Generic;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.Rendering;
using Writersword.Modules.TextEditor.ViewModels;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Примечания рецензентов на листах.
    ///
    /// Место примечания в тексте при «Всех исправлениях» подсвечено бледным цветом
    /// его автора (подсветку кладёт вёрстка строки, цвет даёт этот файл). На правом
    /// поле листа у строки, где место начинается, стоит значок с инициалами автора и
    /// числом ответов; нажатие на значок открывает обсуждение. Закрытое обсуждение —
    /// значок бледный, место не подсвечено. «Без исправлений» и «Исходный документ»
    /// примечаний не показывают, как у Word; в PDF значки не идут. При «Всех
    /// исправлениях» на листах правки примечания стоят выносками в поле исправлений
    /// (DocumentCanvas.RevisionCards), и значков нет.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        /// <summary>Значок примечания на поле листа: обсуждение, лист, верх строки начала места.</summary>
        private sealed record CommentMarker(CommentModel Root, int PageIndex, float Ypt, int Replies, bool Done);

        // Ширина и высота значка, пт.
        private const float CommentMarkerWidthPt = 18f;
        private const float CommentMarkerHeightPt = 13f;

        // От правого края листа до середины значка, пт.
        private const float CommentMarkerInsetPt = 22f;

        // Наименьший шаг между значками одного листа по высоте, пт.
        private const float CommentMarkerStepPt = 16f;

        // Насколько подсветка места бледнее цвета автора: доля белого.
        private const float CommentHighlightWhiteShare = 0.78f;

        // Значки последней раскладки. Пересчитываются, когда сменилась раскладка или
        // сами примечания (номер версии).
        private readonly object _commentMarkersLock = new();
        private List<ParaLayout>? _commentMarkersLayouts;
        private int _commentMarkersVersion = -1;
        private int _commentsVersion;
        private List<CommentMarker> _commentMarkers = new();

        private void OnCommentsChanged()
        {
            System.Threading.Interlocked.Increment(ref _commentsVersion);
            InvalidateFull();
        }

        /// <summary>
        /// Подсветка места примечаний для вёрстки строки: бледный цвет автора первого
        /// незакрытого примечания. null — не подсвечивать.
        /// </summary>
        private string? GetCommentHighlight(IReadOnlyList<Guid> commentIds)
        {
            var comments = DocVm?.Document?.Comments;
            if (comments is null || comments.Count == 0) return null;

            foreach (var id in commentIds)
            {
                CommentModel? comment = null;
                foreach (var candidate in comments)
                {
                    if (candidate.Id != id) continue;
                    comment = candidate;
                    break;
                }

                if (comment is null || comment.Done) continue;

                var authorColor = CommentColor(comment);
                byte Blend(byte channel) => (byte)Math.Round(channel + (255 - channel) * CommentHighlightWhiteShare);

                return $"#{Blend(authorColor.Red):X2}{Blend(authorColor.Green):X2}{Blend(authorColor.Blue):X2}";
            }

            return null;
        }

        /// <summary>
        /// Цвет обсуждения на листе: у примечания — цвет автора, как у Word; у
        /// предложения и требования — цвет вида, тот же, что у их карточек в панели
        /// рецензии: вид важнее, чем кто его оставил.
        /// </summary>
        private SKColor CommentColor(CommentModel comment)
        {
            var root = comment;
            if (comment.ParentId is Guid parentId && DocVm?.Document.Comments is { } all)
            {
                foreach (var candidate in all)
                {
                    if (candidate.Id != parentId) continue;
                    root = candidate;
                    break;
                }
            }

            return root.Kind switch
            {
                CommentKind.Suggestion => new SKColor(0x2E, 0xA3, 0x7A),
                CommentKind.Requirement => new SKColor(0xD9, 0x4F, 0x49),
                _ => _styleResolver?.AuthorColor(root.Author) ?? new SKColor(0xC0, 0x80, 0x00)
            };
        }

        /// <summary>
        /// Значки примечаний на полях показываются при этом виде исправлений — на листах
        /// редактора и в чтении, в том числе разворотом: Word тоже показывает примечания
        /// в режиме чтения. В выгрузке их нет.
        /// </summary>
        private bool CommentMarkersVisible
        {
            get
            {
                if (ExportPassActive) return false;
                if (DocVm is not { } docVm) return false;
                if (docVm.ViewMode != EditorViewMode.Page && !ReadingActive && !SpreadMode) return false;

                var view = _styleResolver?.RevisionView ?? docVm.Document.RevisionView;

                // Выноски поля исправлений заменяют значки.
                if (view == RevisionView.AllMarkup && MarkupPaneVisible) return false;

                return view is RevisionView.AllMarkup or RevisionView.SimpleMarkup;
            }
        }

        /// <summary>
        /// Значки примечаний для раскладки: для каждого обсуждения — лист и высота строки,
        /// в которой начинается его место.
        /// </summary>
        private List<CommentMarker> GetCommentMarkers(List<ParaLayout> layouts)
        {
            int version = System.Threading.Volatile.Read(ref _commentsVersion);

            lock (_commentMarkersLock)
            {
                if (ReferenceEquals(_commentMarkersLayouts, layouts) && _commentMarkersVersion == version)
                    return _commentMarkers;
            }

            var markers = new List<CommentMarker>();
            try
            {
                markers = BuildCommentMarkers(layouts);
            }
            catch (InvalidOperationException)
            {
                // Документ правится в эту же секунду (коллекция изменилась во время
                // обхода): значки пересчитаются на следующем кадре.
                return markers;
            }

            lock (_commentMarkersLock)
            {
                _commentMarkersLayouts = layouts;
                _commentMarkersVersion = version;
                _commentMarkers = markers;
            }

            return markers;
        }

        private List<CommentMarker> BuildCommentMarkers(List<ParaLayout> layouts)
        {
            var markers = new List<CommentMarker>();
            var document = DocVm?.Document;
            if (document is null || document.Comments.Count == 0 || layouts.Count == 0) return markers;

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

            // Где начинается место каждого обсуждения: абзац и знак.
            var starts = new Dictionary<Guid, (ParagraphBlock Para, int Char)>();

            void Visit(ParagraphBlock paragraph)
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
                                starts.TryAdd(RootOf(comment).Id, (paragraph, position));
                            }
                        }

                        position += length;
                    }
                }
            }

            foreach (var section in document.Sections)
            {
                foreach (var block in section.Blocks)
                {
                    switch (block)
                    {
                        case ParagraphBlock paragraph:
                            Visit(paragraph);
                            break;

                        case TableBlock table:
                            foreach (var cell in table.Cells)
                                foreach (var cellParagraph in cell.ParagraphsDeep())
                                    Visit(cellParagraph);
                            break;
                    }
                }
            }

            if (starts.Count == 0) return markers;

            // Куски раскладки по абзацам: абзац, разрезанный листами, даёт несколько.
            var slices = new Dictionary<ParagraphBlock, List<ParaLayout>>();
            foreach (var pl in layouts)
            {
                var model = pl.Cell?.ParaBlock ?? pl.Vm?.Model;
                if (model is null || pl.Layout is null) continue;

                if (!slices.TryGetValue(model, out var list))
                    slices[model] = list = new List<ParaLayout>();
                list.Add(pl);
            }

            foreach (var (rootId, (para, charIndex)) in starts)
            {
                if (!byId.TryGetValue(rootId, out var root)) continue;
                if (!slices.TryGetValue(para, out var paraSlices)) continue;

                foreach (var pl in paraSlices)
                {
                    var layout = pl.Layout!;
                    if (layout.Lines.Count == 0) continue;

                    int lineIndex = layout.GetLineIndexForChar(charIndex);
                    if (lineIndex < pl.LineFrom || lineIndex >= pl.LineTo) continue;

                    int from = Math.Clamp(pl.LineFrom, 0, layout.Lines.Count - 1);
                    float yPt = pl.Ypt + (layout.Lines[lineIndex].Y - layout.Lines[from].Y);

                    markers.Add(new CommentMarker(
                        root, pl.PageIndex, yPt,
                        replies.TryGetValue(rootId, out int replyCount) ? replyCount : 0,
                        root.Done));
                    break;
                }
            }

            markers.Sort((a, b) => a.PageIndex != b.PageIndex
                ? a.PageIndex.CompareTo(b.PageIndex)
                : a.Ypt.CompareTo(b.Ypt));

            return markers;
        }

        /// <summary>
        /// Прямоугольники значков на листах: у правого края листа, на высоте своей
        /// строки; значки, сошедшиеся по высоте, расходятся вниз.
        /// </summary>
        private static List<(CommentMarker Marker, SKRect Rect)> CommentMarkerRects(
            List<CommentMarker> markers, List<PageRect> pages)
        {
            var rects = new List<(CommentMarker, SKRect)>(markers.Count);
            int lastPage = -1;
            float lastTopPt = float.NegativeInfinity;

            foreach (var marker in markers)
            {
                if (marker.PageIndex < 0 || marker.PageIndex >= pages.Count) continue;

                var page = pages[marker.PageIndex];
                float topPt = marker.Ypt + 1f;
                if (marker.PageIndex == lastPage && topPt < lastTopPt + CommentMarkerStepPt)
                    topPt = lastTopPt + CommentMarkerStepPt;

                lastPage = marker.PageIndex;
                lastTopPt = topPt;

                float centerXPt = page.PadLeftPt + page.WidthPt - CommentMarkerInsetPt;
                rects.Add((marker, new SKRect(
                    centerXPt - CommentMarkerWidthPt / 2f, topPt,
                    centerXPt + CommentMarkerWidthPt / 2f, topPt + CommentMarkerHeightPt)));
            }

            return rects;
        }

        /// <summary>Рисует значки примечаний на полях листов.</summary>
        private void RenderCommentMarkers(
            SKCanvas canvas, List<ParaLayout> layouts, List<PageRect> pages, int firstPage, int lastPage)
        {
            if (!CommentMarkersVisible || pages.Count == 0) return;

            var markers = GetCommentMarkers(layouts);
            if (markers.Count == 0) return;

            using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            using var text = new SKPaint { IsAntialias = true, Color = SKColors.White };
            using var font = new SKFont(SKTextRenderer.ResolveTypeface("Segoe UI", true, false), 6.5f);
            using var badgeFont = new SKFont(SKTextRenderer.ResolveTypeface("Segoe UI", true, false), 6f);

            foreach (var (marker, rect) in CommentMarkerRects(markers, pages))
            {
                if (marker.PageIndex < firstPage || marker.PageIndex > lastPage) continue;

                var color = CommentColor(marker.Root);
                if (marker.Done) color = new SKColor(0x9A, 0x9A, 0x9A);
                fill.Color = marker.Done ? color.WithAlpha(0x90) : color;

                // Облачко с хвостиком снизу слева.
                using (var bubble = new SKPath())
                {
                    bubble.AddRoundRect(rect, 3f, 3f);
                    bubble.MoveTo(rect.Left + 4f, rect.Bottom - 0.5f);
                    bubble.LineTo(rect.Left + 3f, rect.Bottom + 3.5f);
                    bubble.LineTo(rect.Left + 8f, rect.Bottom - 0.5f);
                    bubble.Close();
                    canvas.DrawPath(bubble, fill);
                }

                string initials = marker.Root.DisplayInitials;
                if (initials.Length > 2) initials = initials.Substring(0, 2);

                float textWidth = font.MeasureText(initials);
                font.GetFontMetrics(out var metrics);
                float baseline = rect.MidY - (metrics.Ascent + metrics.Descent) / 2f;
                canvas.DrawText(initials, rect.MidX - textWidth / 2f, baseline, SKTextAlign.Left, font, text);

                if (marker.Replies > 0)
                {
                    string count = marker.Replies > 9 ? "9+" : marker.Replies.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    float badgeX = rect.Right - 1f;
                    float badgeY = rect.Top + 1f;

                    fill.Color = new SKColor(0x40, 0x40, 0x40);
                    canvas.DrawCircle(badgeX, badgeY, 4.2f, fill);

                    float countWidth = badgeFont.MeasureText(count);
                    badgeFont.GetFontMetrics(out var badgeMetrics);
                    canvas.DrawText(count, badgeX - countWidth / 2f,
                        badgeY - (badgeMetrics.Ascent + badgeMetrics.Descent) / 2f,
                        SKTextAlign.Left, badgeFont, text);
                }
            }
        }

        /// <summary>Значок примечания под точкой листа (в координатах раскладки).</summary>
        private bool TryHitCommentMarker(float xPt, float yPt, out CommentModel? root)
        {
            root = null;
            if (!CommentMarkersVisible) return false;

            List<ParaLayout> layouts;
            List<PageRect> pages;
            lock (_renderLock)
            {
                layouts = _layouts;
                pages = _pages;
            }

            var markers = GetCommentMarkers(layouts);
            if (markers.Count == 0) return false;

            foreach (var (marker, rect) in CommentMarkerRects(markers, pages))
            {
                var hit = rect;
                hit.Inflate(3f, 3f);
                if (!hit.Contains(xPt, yPt)) continue;

                root = marker.Root;
                return true;
            }

            return false;
        }
    }
}
