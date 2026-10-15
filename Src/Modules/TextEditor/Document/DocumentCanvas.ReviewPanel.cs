using System;
using System.Collections.Generic;
using System.Text;
using Avalonia.Threading;
using SkiaSharp;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.Services;
using Writersword.Modules.TextEditor.ViewModels;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Полотно для панели рецензии (Views/Review/ReviewPanel): переход к месту пункта,
    /// подсветка места под указателем в панели и принятие или отклонение правок
    /// пачкой одним шагом отмены. Панель зовёт это через делегаты вью-модели
    /// документа — о листе она ничего не знает.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        // Места, подсвеченные из панели, и цвет подсветки.
        private IReadOnlyList<(ParagraphBlock Paragraph, int From, int To)>? _reviewHighlight;
        private SKColor _reviewHighlightColor;

        // Плавающая картинка, подсвеченная из панели.
        private ImageBlock? _reviewHighlightImage;

        // Вспышка правок, вернувшихся отменой или повтором: место мягко загорается и
        // гаснет, чтобы было видно, что именно вернулось.
        private static readonly TimeSpan FlashDuration = TimeSpan.FromMilliseconds(1100);
        private static readonly TimeSpan FlashRise = TimeSpan.FromMilliseconds(140);
        private const byte FlashPeakAlpha = 0x6A;
        private static readonly SKColor FlashColor = new(0xFF, 0xD5, 0x4F);

        private List<(ParagraphBlock Paragraph, int From, int To)>? _flashPlaces;
        private DateTime _flashStart;
        private DispatcherTimer? _flashTimer;

        private void WireReviewPanel(DocumentViewModel docVm)
        {
            docVm.GoToReviewPlaceDelegate = GoToReviewPlace;
            docVm.HighlightReviewPlacesDelegate = HighlightReviewPlaces;
            docVm.ResolveRevisionHitsDelegate = ResolveRevisionHits;
            docVm.GoToReviewImageDelegate = GoToReviewImage;
            docVm.HighlightReviewImageDelegate = HighlightReviewImage;
            docVm.ResolveImageRevisionsDelegate = ResolveImageRevisions;
            docVm.ResolveBlockRevisionsDelegate = ResolveBlockRevisions;
        }

        private void UnwireReviewPanel(DocumentViewModel docVm)
        {
            docVm.GoToReviewPlaceDelegate = null;
            docVm.HighlightReviewPlacesDelegate = null;
            docVm.ResolveRevisionHitsDelegate = null;
            docVm.GoToReviewImageDelegate = null;
            docVm.HighlightReviewImageDelegate = null;
            docVm.ResolveImageRevisionsDelegate = null;
            docVm.ResolveBlockRevisionsDelegate = null;
            _reviewHighlight = null;
            _reviewHighlightImage = null;
        }

        /// <summary>Выделяет плавающую картинку и прокручивает к ней.</summary>
        private void GoToReviewImage(ImageBlock image)
        {
            if (DocVm is null) return;

            List<ImageEntry> images;
            List<PageRect> pages;
            lock (_renderLock) { images = _images; pages = _pages; }

            var entry = images.Find(e => ReferenceEquals(Rendering.RevisionDisplay.RealObject(e.Block), image));

            // Картинка, плавающая в ячейке таблицы, в списке картинок листа не стоит:
            // к ней ведёт её абзац ячейки.
            if (entry is null && CellFloatAnchor(image) is { } anchor)
            {
                GoToReviewPlace(anchor, 0, 0, false);
                return;
            }

            ExitImageCropMode(apply: false);
            _tableSelections.Clear();
            _selectedImage = image;
            ImageSelectionChanged?.Invoke(true);
            InvalidateFull();

            if (entry is null || _parentScrollViewer is null) return;

            var (_, dy) = PageVisualDelta(entry.PageIndex, pages);
            double zoom = Zoom;
            double topPx = (entry.Ypt + dy) * PtToPx * zoom;
            double heightPx = entry.HeightPt * PtToPx * zoom;
            double scrollY = _parentScrollViewer.Offset.Y;
            double viewportH = _parentScrollViewer.Viewport.Height;

            if (topPx >= scrollY && topPx + heightPx <= scrollY + viewportH) return;

            double target = topPx - Math.Max((viewportH - heightPx) / 2.0, 24.0);
            SmoothScrollTo(Math.Max(0, target));
        }

        /// <summary>
        /// Знак сноски в тексте для абзаца из текста сноски: абзац со знаком и место.
        /// Null — абзац не в сноске или знака у сноски нет.
        /// </summary>
        private (ParagraphBlock Owner, int Index)? NoteMarkPlace(ParagraphBlock paragraph)
        {
            if (DocVm is null) return null;
            if (RevisionService.NoteOfParagraph(DocVm.Document, paragraph) is not { } note) return null;
            return RevisionService.NoteReferenceOf(DocVm.Document, note);
        }

        /// <summary>Абзац ячейки, к которому привязан плавающий в ячейке объект; null — объект не в ячейке.</summary>
        private ParagraphBlock? CellFloatAnchor(BlockModel block)
        {
            if (DocVm is null) return null;

            foreach (var section in DocVm.Document.Sections)
            {
                foreach (var candidate in section.Blocks)
                {
                    if (candidate is not TableBlock table) continue;

                    foreach (var (cell, cellFloat) in RevisionService.CellFloats(table))
                    {
                        if (!ReferenceEquals(cellFloat.Object, block)) continue;
                        if (cell.Paragraphs.Count == 0) return null;
                        return cell.Paragraphs[Math.Clamp(cell.FloatParagraphIndex(cellFloat), 0, cell.Paragraphs.Count - 1)];
                    }
                }
            }

            return null;
        }

        /// <summary>Подсвечивает плавающую картинку пункта под указателем; null — снимает.</summary>
        private void HighlightReviewImage(ImageBlock? image, uint argb)
        {
            if (image is null && _reviewHighlightImage is null) return;
            _reviewHighlightImage = image;
            if (image is not null) _reviewHighlightColor = new SKColor(argb);
            InvalidateFull();
        }

        /// <summary>
        /// Выделяет место в тексте и прокручивает к нему. Знак абзаца — каретка в
        /// конце абзаца. Место из одной картинки строки выделяется как картинка —
        /// рамкой с маркерами, а не заливкой текста.
        /// </summary>
        private void GoToReviewPlace(ParagraphBlock paragraph, int from, int to, bool isMark)
        {
            if (DocVm is null) return;

            // Правка в тексте сноски: каретки там нет — выделяется знак сноски в тексте.
            if (NoteMarkPlace(paragraph) is { } noteMark)
            {
                if (SelectRevision(new RevisionService.Hit(noteMark.Owner, noteMark.Index, noteMark.Index + 1, false)))
                    ScrollToCaret();
                return;
            }

            int length = paragraph.TotalLength;
            from = Math.Clamp(from, 0, length);
            to = Math.Clamp(to, from, length);

            if (!isMark && to - from == 1
                && paragraph.GetInlineImageIdAt(from) is Guid imageId
                && FindInlineImage(imageId) is { } image)
            {
                if (!SelectRevision(new RevisionService.Hit(paragraph, from, from, false))) return;
                ScrollToCaret();

                ExitImageCropMode(apply: false);
                _tableSelections.Clear();
                _selectedImage = image;
                ImageSelectionChanged?.Invoke(true);
                InvalidateFull();
                return;
            }

            var hit = isMark && from == to
                ? new RevisionService.Hit(paragraph, length, length, true)
                : new RevisionService.Hit(paragraph, from, to, false);

            if (!SelectRevision(hit)) return;
            ScrollToCaret();
        }

        /// <summary>Подсвечивает места пункта под указателем в панели; null — снимает.</summary>
        private void HighlightReviewPlaces(IReadOnlyList<(ParagraphBlock Paragraph, int From, int To)>? places, uint argb)
        {
            if (places is null && _reviewHighlight is null) return;

            // Места в тексте сноски подсвечиваются её знаком в тексте.
            if (places is { Count: > 0 })
            {
                List<(ParagraphBlock Paragraph, int From, int To)>? mapped = null;
                for (int i = 0; i < places.Count; i++)
                {
                    if (NoteMarkPlace(places[i].Paragraph) is not { } noteMark) continue;
                    mapped ??= new List<(ParagraphBlock Paragraph, int From, int To)>(places);
                    mapped[i] = (noteMark.Owner, noteMark.Index, noteMark.Index + 1);
                }

                if (mapped is not null) places = mapped;
            }

            _reviewHighlight = places;
            _reviewHighlightColor = new SKColor(argb);
            InvalidateFull();
        }

        /// <summary>Подсветка мест из панели и вспышка вернувшихся правок — поверх текста.</summary>
        private void DrawReviewHighlight(SKCanvas canvas, List<ParaLayout> layouts, List<PageRect> pages, int firstPage, int lastPage)
        {
            if (ExportPassActive) return;

            if (_reviewHighlight is { Count: > 0 } hovered)
                DrawPlaces(canvas, layouts, pages, firstPage, lastPage, hovered, _reviewHighlightColor);

            if (_reviewHighlightImage is { } hoveredImage)
            {
                List<ImageEntry> images;
                lock (_renderLock) images = _images;
                var entry = images.Find(e => ReferenceEquals(e.Block, hoveredImage));
                if (entry is not null && entry.PageIndex >= firstPage && entry.PageIndex <= lastPage)
                {
                    var (dx, dy) = PageVisualDelta(entry.PageIndex, pages);
                    using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = _reviewHighlightColor };
                    canvas.DrawRect(new SKRect(entry.XPt + dx, entry.Ypt + dy,
                        entry.XPt + dx + entry.WidthPt, entry.Ypt + dy + entry.HeightPt), fill);
                }
            }

            if (_flashPlaces is { Count: > 0 } flashed)
            {
                byte alpha = FlashAlpha(DateTime.UtcNow - _flashStart);
                if (alpha > 0)
                    DrawPlaces(canvas, layouts, pages, firstPage, lastPage, flashed, FlashColor.WithAlpha(alpha));
            }
        }

        private void DrawPlaces(
            SKCanvas canvas, List<ParaLayout> layouts, List<PageRect> pages, int firstPage, int lastPage,
            IReadOnlyList<(ParagraphBlock Paragraph, int From, int To)> places, SKColor color)
        {
            using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color };

            foreach (var (paragraph, from, to) in places)
            {
                if (to <= from) continue;

                foreach (var pl in layouts)
                {
                    if (pl.PageIndex < firstPage || pl.PageIndex > lastPage) continue;

                    var model = pl.Cell?.ParaBlock ?? pl.Vm?.Model;
                    if (!ReferenceEquals(model, paragraph) || pl.Layout is not { } sl) continue;

                    var (dx, dy) = PageVisualDelta(pl.PageIndex, pages);
                    canvas.Save();
                    canvas.Translate(dx, dy);

                    float yBase = pl.LineFrom < sl.Lines.Count ? sl.Lines[pl.LineFrom].Y : 0f;
                    for (int li = pl.LineFrom; li < pl.LineTo && li < sl.Lines.Count; li++)
                    {
                        var line = sl.Lines[li];
                        int lineFrom = Math.Max(from, line.FirstCharIndex);
                        int lineTo = Math.Min(to, line.LastCharIndex + 1);
                        if (lineTo <= lineFrom) continue;

                        DrawSelectionSubRange(canvas, sl, li, lineFrom, lineTo, pl.AbsXPt, pl.Ypt, yBase, paint);
                    }

                    canvas.Restore();
                }
            }
        }

        // ── Отметка правки на картинке строки ─────────────────────────────

        // Картинки строк, вставленные или удалённые правкой: Id объекта — его правка.
        // Собирается при первой отрисовке после раскладки.
        private Dictionary<Guid, (bool Deleted, SKColor Color, bool Changed)>? _inlineRevisionMarks;

        private static readonly SKColor DeletedImageColor = new(0xE0, 0x4F, 0x45);

        private Dictionary<Guid, (bool Deleted, SKColor Color, bool Changed)> InlineRevisionMarks()
        {
            if (_inlineRevisionMarks is { } ready) return ready;

            var marks = new Dictionary<Guid, (bool Deleted, SKColor Color, bool Changed)>();
            if (DocVm is { } vm)
            {
                foreach (var paragraph in RevisionService.ReadingOrder(vm.Document))
                {
                    foreach (var chunk in paragraph.Chunks)
                    {
                        foreach (var run in chunk.Runs)
                        {
                            if (run.InlineImageId is not Guid id) continue;
                            var props = run.Properties;
                            if (props?.Deleted is { } deleted)
                                marks[id] = (true, DeletedImageColor, false);
                            else if (props?.Inserted is { } inserted)
                                marks[id] = (false, inserted.IsMove
                                    ? new SKColor(0x00, 0x80, 0x00)
                                    : _styleResolver?.AuthorColor(inserted.Author) ?? new SKColor(0x2E, 0x9E, 0x5B), false);
                        }
                    }
                }

                // Картинка в строке, у которой правкой сменён вид (размер, поворот,
                // обрезка), — отметкой смены вида, если её знак не вставлен и не удалён.
                foreach (var section in vm.Document.Sections)
                {
                    foreach (var block in section.InlineObjects)
                    {
                        if (RevisionMarks.ChangeOf(block) is not { } change) continue;
                        if (marks.ContainsKey(block.Id)) continue;
                        marks[block.Id] = (false, _styleResolver?.AuthorColor(change.Info.Author) ?? new SKColor(0x2E, 0x9E, 0x5B), true);
                    }
                }
            }

            _inlineRevisionMarks = marks;
            return marks;
        }

        /// <summary>
        /// Отметка правки на картинке строки — как у текста, но видная с одного
        /// взгляда: вставленная картинка обведена цветом автора правки и несёт
        /// значок «+» в углу; удалённая — красная рамка, перечёркнута крест-накрест
        /// и приглушена, значок «−». Без отметок исправлений и при выгрузке не рисуется.
        /// </summary>
        private void DrawInlineRevisionMark(SKCanvas canvas, Guid id, float left, float top, float width, float height)
        {
            if (ExportPassActive || ReadingActive || DocVm is null) return;
            if (!Rendering.RevisionDisplay.ShowsMarkup(DocVm.Document.RevisionView)) return;
            if (width <= 0f || height <= 0f) return;
            if (!InlineRevisionMarks().TryGetValue(id, out var mark)) return;

            DrawRevisionMarkBox(canvas, mark.Deleted, mark.Color, left, top, width, height, mark.Changed);
        }

        /// <summary>Плавающая картинка спрятана правкой при текущем виде показа.</summary>
        private bool FloatingImageHiddenByRevision(ImageBlock image) => BlockHiddenByRevision(image);

        /// <summary>
        /// Блок, вставленный или удалённый правкой целиком (картинка, фигура), спрятан
        /// при текущем виде показа.
        /// </summary>
        private bool BlockHiddenByRevision(IRevisionMarked block)
            => DocVm is not null && Rendering.RevisionDisplay.IsBlockHidden(block, DocVm.Document.RevisionView);

        /// <summary>Отметка правки плавающей картинки — та же, что у картинки в строке.</summary>
        private void DrawFloatingRevisionMark(SKCanvas canvas, ImageBlock image, SKRect rect)
            => DrawBlockRevisionMark(canvas, image, rect);

        /// <summary>
        /// Отметка правки блока, вставленного или удалённого целиком: рамка цвета
        /// автора со значком «+», у удалённого — пунктир, крест и «−».
        /// </summary>
        private void DrawBlockRevisionMark(SKCanvas canvas, IRevisionMarked block, SKRect rect)
        {
            if (ExportPassActive || ReadingActive || DocVm is null) return;
            if (!Rendering.RevisionDisplay.ShowsMarkup(DocVm.Document.RevisionView)) return;

            // Только смена вида: рамка цвета автора и значок правки вида.
            if (block.Inserted is null && block.Deleted is null)
            {
                if (block is not IAppearanceTracked { Change: { } change }) return;
                var changeColor = _styleResolver?.AuthorColor(change.Info.Author) ?? new SKColor(0x2E, 0x9E, 0x5B);
                DrawRevisionMarkBox(canvas, false, changeColor, rect.Left, rect.Top, rect.Width, rect.Height, changed: true);
                return;
            }

            bool deleted = block.Deleted is not null;
            var color = deleted
                ? DeletedImageColor
                : block.Inserted!.IsMove
                    ? new SKColor(0x00, 0x80, 0x00)
                    : _styleResolver?.AuthorColor(block.Inserted.Author) ?? new SKColor(0x2E, 0x9E, 0x5B);

            DrawRevisionMarkBox(canvas, deleted, color, rect.Left, rect.Top, rect.Width, rect.Height);
        }

        /// <summary>
        /// Принимает или отклоняет правки разрывов и фигур одним шагом отмены — из
        /// панели рецензии. Картинки идут своим путём (ResolveImageRevisions).
        /// </summary>
        private void ResolveBlockRevisions(IReadOnlyList<BlockModel> blocks, bool accept)
        {
            if (DocVm is null || blocks.Count == 0 || IsEditingBlocked) return;
            if (RevisionResolveLocked()) return;

            var images = new List<ImageBlock>();
            var others = new List<BlockModel>();
            foreach (var block in blocks)
            {
                if (block is ImageBlock image) images.Add(image);
                else others.Add(block);
            }

            BeginTrackedSnapshot(accept ? "Принять исправления" : "Отклонить исправления");
            try
            {
                if (images.Count > 0) ApplyImageResolution(images, accept);
                ApplyMarkedBlockResolution(others, accept, whole: false);
                RebuildLayouts();
                InvalidateMeasure();
            }
            finally
            {
                EndTrackedSnapshot();
            }

            _reviewHighlight = null;
            _reviewHighlightImage = null;
            InvalidateFull();
        }

        /// <summary>
        /// Решение по правкам разрывов и фигур без своего шага отмены: убранный
        /// блок снимается с выделения, состав блоков пересобирается.
        /// </summary>
        private void ApplyMarkedBlockResolution(IReadOnlyList<BlockModel> blocks, bool accept, bool whole)
        {
            if (DocVm is null || blocks.Count == 0) return;

            var removed = RevisionService.ResolveBlocks(DocVm.Document, blocks, accept, whole);
            foreach (var block in removed)
                if (ReferenceEquals(_selectedShape, block)) _selectedShape = null;

            // Строки, столбцы и оформление таблиц: их раскладки строятся заново.
            foreach (var block in blocks)
            {
                if (block is not (TableBlock or Services.TableRevisionPart)) continue;
                _layoutCache.Clear();
                InvalidateCellLayoutCaches();
                DocVm.RebuildParagraphViewModelsPublic();
                break;
            }

            // Параметры разделов: лист и колонки другие — раскладка заново целиком.
            foreach (var block in blocks)
            {
                if (block is not Services.SectionRevisionPart) continue;
                _layoutCache.Clear();
                InvalidateCellLayoutCaches();
                DocVm.RaisePageSettingsChanged();
                break;
            }

            _inlineRevisionMarks = null;
            DocVm.RaiseStructureChanged();
        }

        private static void DrawRevisionMarkBox(SKCanvas canvas, bool deletedMark, SKColor markColor, float left, float top, float width, float height, bool changed = false)
        {
            if (changed)
            {
                DrawAppearanceChangeMark(canvas, markColor, left, top, width, height);
                return;
            }

            var mark = (Deleted: deletedMark, Color: markColor);
            if (width <= 0f || height <= 0f) return;

            const float outset = 2f;
            var box = new SKRect(left - outset, top - outset, left + width + outset, top + height + outset);

            if (mark.Deleted)
            {
                // Приглушение — цветом правки, а не белым: лист бывает любого цвета темы.
                using var veil = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = mark.Color.WithAlpha(0x30) };
                canvas.DrawRect(new SKRect(left, top, left + width, top + height), veil);

                using var cross = new SKPaint
                {
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 1.4f,
                    StrokeCap = SKStrokeCap.Round,
                    Color = mark.Color.WithAlpha(0xC0)
                };
                canvas.DrawLine(left, top, left + width, top + height, cross);
                canvas.DrawLine(left + width, top, left, top + height, cross);
            }

            using (var frame = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.6f,
                Color = mark.Color,
                PathEffect = mark.Deleted ? SKPathEffect.CreateDash(new[] { 4f, 2.5f }, 0f) : null
            })
            {
                canvas.DrawRoundRect(box, 2.5f, 2.5f, frame);
            }

            // Значок в правом верхнем углу: кружок цвета правки с «+» или «−».
            float r = Math.Clamp(Math.Min(width, height) * 0.12f, 4.5f, 7f);
            float cx = box.Right;
            float cy = box.Top;

            using (var badge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = mark.Color })
                canvas.DrawCircle(cx, cy, r, badge);

            using var sign = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = Math.Max(1.2f, r * 0.28f),
                StrokeCap = SKStrokeCap.Round,
                Color = SKColors.White
            };
            float arm = r * 0.5f;
            canvas.DrawLine(cx - arm, cy, cx + arm, cy, sign);
            if (!mark.Deleted) canvas.DrawLine(cx, cy - arm, cx, cy + arm, sign);
        }

        /// <summary>
        /// Отметка смены вида объекта: рамка цвета автора точками и значок в углу —
        /// кружок с двумя стрелками «было — стало». Не путается ни со вставкой («+»),
        /// ни с удалением (крест).
        /// </summary>
        private static void DrawAppearanceChangeMark(SKCanvas canvas, SKColor color, float left, float top, float width, float height)
        {
            if (width <= 0f || height <= 0f) return;

            const float outset = 2f;
            var box = new SKRect(left - outset, top - outset, left + width + outset, top + height + outset);

            using (var frame = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.6f,
                Color = color,
                StrokeCap = SKStrokeCap.Round,
                PathEffect = SKPathEffect.CreateDash(new[] { 0.1f, 3.2f }, 0f)
            })
            {
                canvas.DrawRoundRect(box, 2.5f, 2.5f, frame);
            }

            float r = Math.Clamp(Math.Min(width, height) * 0.12f, 4.5f, 7f);
            float cx = box.Right;
            float cy = box.Top;

            using (var badge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color })
                canvas.DrawCircle(cx, cy, r, badge);

            using var arrows = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = Math.Max(1.1f, r * 0.22f),
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round,
                Color = SKColors.White
            };

            float arm = r * 0.5f;
            float gap = r * 0.22f;
            float head = r * 0.22f;

            // Верхняя стрелка вправо, нижняя — влево.
            canvas.DrawLine(cx - arm, cy - gap, cx + arm, cy - gap, arrows);
            canvas.DrawLine(cx + arm, cy - gap, cx + arm - head, cy - gap - head, arrows);
            canvas.DrawLine(cx - arm, cy + gap, cx + arm, cy + gap, arrows);
            canvas.DrawLine(cx - arm, cy + gap, cx - arm + head, cy + gap + head, arrows);
        }

        // ── Удаление картинки под рецензированием ─────────────────────────

        /// <summary>
        /// Картинка в строке под записью исправлений: её знак помечается удалённым, а
        /// сама картинка остаётся в тексте до решения по правке. Своя вставка
        /// убирается совсем — как и текст. Картинка вне строки правкой в модели не
        /// отмечается, её удаление идёт прежним путём.
        /// </summary>
        /// <returns>true — удаление записано правкой.</returns>
        private bool TryTrackedImageDelete(ImageBlock image)
        {
            if (!TrackingActive || DocVm is null) return false;
            if (DocVm.FindInlineImageOwner(image) is not { } owner) return TryTrackedFloatingDelete(image);

            var paragraph = owner.Para;
            int at = owner.CharIndex;

            BeginTrackedSnapshot("Удаление изображения");
            try
            {
                RevisionService.MarkDeleted(paragraph, at, at + 1, DocVm.NewRevisionInfo());
                _inlineRevisionMarks = null;
                RefreshAfterTrackedChange(new[] { paragraph }, false);
                PlaceCaret(paragraph, Math.Min(at, paragraph.TotalLength));
            }
            finally
            {
                EndTrackedSnapshot();
            }

            return true;
        }

        /// <summary>
        /// Плавающая картинка под записью исправлений: помечается удалённой и остаётся на
        /// листе. Своя вставка убирается совсем, уже удалённая не трогается.
        /// </summary>
        private bool TryTrackedFloatingDelete(ImageBlock image)
        {
            if (DocVm is null) return false;

            var info = DocVm.NewRevisionInfo();
            if (RevisionService.IsOwnInsertion(image.Inserted, info)) return false;
            if (image.Deleted is not null) return true;

            BeginTrackedSnapshot("Удаление изображения");
            try
            {
                image.Deleted = info;
                _inlineRevisionMarks = null;
            }
            finally
            {
                EndTrackedSnapshot();
            }

            InvalidateFull();
            return true;
        }

        /// <summary>
        /// Принимает или отклоняет правки плавающих картинок одним шагом отмены:
        /// принятое удаление и отклонённая вставка убирают картинку, остальное снимает
        /// отметку.
        /// </summary>
        private void ResolveImageRevisions(IReadOnlyList<ImageBlock> images, bool accept)
        {
            if (DocVm is null || images.Count == 0 || IsEditingBlocked) return;

            BeginTrackedSnapshot(accept ? "Принять исправления" : "Отклонить исправления");
            try
            {
                ApplyImageResolution(images, accept);
                RebuildLayouts();
                InvalidateMeasure();
            }
            finally
            {
                EndTrackedSnapshot();
            }

            _reviewHighlight = null;
            _reviewHighlightImage = null;
            InvalidateFull();
        }

        /// <summary>
        /// Решение по правкам плавающих картинок без своего шага отмены. Принять:
        /// удалённая уходит, вставленная остаётся без отметки. Отклонить: удалённая
        /// возвращается (вставленная правкой — остаётся вставкой), вставленная уходит.
        /// <paramref name="whole"/> — решение по всем правкам разом: удалённая вставка
        /// при отклонении уходит сразу, как у Word.
        /// </summary>
        private void ApplyImageResolution(IReadOnlyList<ImageBlock> images, bool accept, bool whole = false)
        {
            foreach (var image in images)
            {
                // Смена вида: принять — новый вид остаётся, отклонить — прежний.
                if (image.Change is { } change)
                {
                    if (!accept) ObjectAppearance.Restore(image, change.Previous);
                    image.Change = null;
                }

                if (image.Inserted is null && image.Deleted is null) continue;

                bool remove = accept
                    ? image.Deleted is not null
                    : image.Inserted is not null && (image.Deleted is null || whole);

                if (remove)
                {
                    if (ReferenceEquals(_selectedImage, image)) _selectedImage = null;
                    RemoveFloatingImageBlock(image);
                    continue;
                }

                if (accept) image.Inserted = null;
                else image.Deleted = null;
            }

            _inlineRevisionMarks = null;
        }

        private void RemoveFloatingImageBlock(ImageBlock image)
        {
            if (DocVm is null) return;

            // Где бы картинка ни стояла — в потоке, на листе, в строке или в ячейке.
            RevisionService.RemoveBlock(DocVm.Document, image);
            DocVm.RaiseStructureChanged();
        }

        // ── Вспышка вернувшихся правок ───────────────────────────────────

        /// <summary>
        /// Запоминает правки документа до отмены или повтора и после шага подсвечивает
        /// те, что появились: принятую по ошибке правку отмена возвращает — и видно,
        /// куда она вернулась.
        /// </summary>
        private void ScheduleRestoredRevisionFlash()
        {
            if (DocVm is null) return;

            // Без правок до шага и тоже вспыхивает: «Принять все» оставляет документ
            // чистым, и отмена возвращает правки в пустое место.
            // Вспышка — украшение: любой сбой в ней не имеет права остановить отмену.
            Dictionary<string, int>? before;
            try { before = CountRevisionMarks(DocVm.Document); }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[UNDO] Вспышка вернувшихся правок: не удалось снять правки до шага");
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (DocVm is null) return;

                try
                {
                    var places = new List<(ParagraphBlock Paragraph, int From, int To)>();
                    var seen = new Dictionary<string, int>(StringComparer.Ordinal);

                    foreach (var (key, place) in CollectRevisionMarks(DocVm.Document))
                    {
                        seen.TryGetValue(key, out int count);
                        seen[key] = ++count;

                        int had = before is not null && before.TryGetValue(key, out int n) ? n : 0;
                        if (count > had) places.Add(place);
                    }

                    if (places.Count > 0) StartFlash(places);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "[UNDO] Вспышка вернувшихся правок не удалась");
                }
            }, DispatcherPriority.Background);
        }

        private static Dictionary<string, int>? CountRevisionMarks(DocumentModel document)
        {
            Dictionary<string, int>? counts = null;
            foreach (var (key, _) in CollectRevisionMarks(document))
            {
                counts ??= new Dictionary<string, int>(StringComparer.Ordinal);
                counts.TryGetValue(key, out int n);
                counts[key] = n + 1;
            }
            return counts;
        }

        /// <summary>
        /// Правки документа с ключом, по которому та же правка узнаётся после шага
        /// отмены: абзац, вид, автор и текст. Позиция в ключ не входит — вернувшееся
        /// удаление сдвигает знаки правок, стоящих дальше в абзаце, а сами они не
        /// менялись и загораться не должны.
        /// </summary>
        private static IEnumerable<(string Key, (ParagraphBlock Paragraph, int From, int To) Place)> CollectRevisionMarks(DocumentModel document)
        {
            foreach (var paragraph in RevisionService.ReadingOrder(document))
            {
                if (!ParagraphHasRevisions(paragraph)) continue;

                var cells = paragraph.ToCharCells();
                foreach (var hit in RevisionService.HitsIn(paragraph))
                {
                    var sb = new StringBuilder();
                    sb.Append(paragraph.Id.ToString("N")).Append('|');

                    if (hit.IsMark)
                    {
                        var props = paragraph.Properties;
                        sb.Append(hit.From == hit.To
                            ? (props.MarkDeleted is not null ? "mark-del|" + props.MarkDeleted.Author : "mark-ins|" + props.MarkInserted?.Author)
                            : "para-fmt|" + props.FormatChange?.Info.Author);

                        int tail = Math.Max(0, cells.Count - 1);
                        yield return (sb.ToString(), (paragraph, hit.From == hit.To ? tail : 0, cells.Count));
                        continue;
                    }

                    int from = Math.Clamp(hit.From, 0, cells.Count);
                    int to = Math.Clamp(hit.To, from, cells.Count);
                    var run = from < cells.Count ? cells[from].Props : null;

                    if (run?.Deleted is { } deleted) sb.Append("del|").Append(deleted.Author);
                    else if (run?.Inserted is { } inserted) sb.Append("ins|").Append(inserted.Author);
                    else sb.Append("fmt|").Append(run?.FormatChange?.Info.Author);

                    sb.Append('|');
                    for (int i = from; i < to; i++) sb.Append(cells[i].IsInlineObject ? '\uFFFC' : cells[i].Ch);

                    yield return (sb.ToString(), (paragraph, from, to));
                }
            }
        }

        private static bool ParagraphHasRevisions(ParagraphBlock paragraph)
        {
            if (paragraph.Properties.HasRevision) return true;
            foreach (var chunk in paragraph.Chunks)
                foreach (var run in chunk.Runs)
                    if (run.Properties?.HasRevision == true) return true;
            return false;
        }

        private void StartFlash(List<(ParagraphBlock Paragraph, int From, int To)> places)
        {
            _flashPlaces = places;
            _flashStart = DateTime.UtcNow;

            if (_flashTimer is null)
            {
                _flashTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
                _flashTimer.Tick += (_, _) =>
                {
                    if (DateTime.UtcNow - _flashStart >= FlashDuration)
                    {
                        _flashTimer!.Stop();
                        _flashPlaces = null;
                    }
                    InvalidateFull();
                };
            }

            _flashTimer.Stop();
            _flashTimer.Start();
            InvalidateFull();
        }

        /// <summary>Яркость вспышки: быстро загорается и плавно гаснет.</summary>
        private static byte FlashAlpha(TimeSpan elapsed)
        {
            if (elapsed <= TimeSpan.Zero) return 0;
            if (elapsed >= FlashDuration) return 0;

            double k;
            if (elapsed < FlashRise)
            {
                k = elapsed.TotalMilliseconds / FlashRise.TotalMilliseconds;
                k = 1 - (1 - k) * (1 - k);
            }
            else
            {
                double t = (elapsed - FlashRise).TotalMilliseconds / (FlashDuration - FlashRise).TotalMilliseconds;
                k = 1 - t;
                k = k * k * (3 - 2 * k);
            }

            return (byte)Math.Round(FlashPeakAlpha * Math.Clamp(k, 0, 1));
        }

        /// <summary>
        /// Принимает или отклоняет правки одним шагом отмены. Правки идут с конца
        /// документа к началу и в абзаце — с конца к началу: так снятие одной не
        /// сдвигает места тех, что ещё впереди, а слияние абзаца со следующим трогает
        /// уже пройденный.
        /// </summary>
        private void ResolveRevisionHits(IReadOnlyList<RevisionService.Hit> hits, bool accept)
        {
            if (DocVm is null || hits.Count == 0 || IsEditingBlocked) return;

            var doc = DocVm.Document;
            var order = RevisionService.ReadingOrder(doc);
            var orderOf = new Dictionary<ParagraphBlock, int>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < order.Count; i++) orderOf[order[i]] = i;

            var sorted = new List<RevisionService.Hit>(hits);
            sorted.Sort((a, b) =>
            {
                int oa = orderOf.TryGetValue(a.Paragraph, out int ia) ? ia : -1;
                int ob = orderOf.TryGetValue(b.Paragraph, out int ib) ? ib : -1;
                if (oa != ob) return ob.CompareTo(oa);

                // Знак абзаца стоит в конце абзаца — он первый с конца.
                bool ma = a.IsMark && a.From == a.To;
                bool mb = b.IsMark && b.From == b.To;
                if (ma != mb) return ma ? -1 : 1;
                return b.From.CompareTo(a.From);
            });

            var caretParagraph = ParagraphAtSlice(_caretPara);
            int caretChar = _caretChar;

            BeginTrackedSnapshot(accept
                ? (hits.Count > 1 ? "Принять исправления" : "Принять исправление")
                : (hits.Count > 1 ? "Отклонить исправления" : "Отклонить исправление"));
            try
            {
                var touched = new HashSet<ParagraphBlock>(ReferenceEqualityComparer.Instance);
                bool structureChanged = false;

                foreach (var hit in sorted)
                {
                    // Абзац мог уйти слиянием с предыдущим шагом.
                    if (RevisionService.Locate(doc, hit.Paragraph) is null) continue;

                    var scope = hit.IsMark
                        ? new RevisionService.Scope(hit.To, hit.To, true)
                        : new RevisionService.Scope(hit.From, hit.To, false);

                    var target = hit.Paragraph;
                    var (changed, merged) = RevisionService.Resolve(
                        doc, candidate => ReferenceEquals(candidate, target) ? scope : null, accept);

                    touched.UnionWith(changed);
                    structureChanged |= merged;
                }

                RefreshAfterTrackedChange(touched, structureChanged);
                RestoreCaretAfterResolve(caretParagraph, caretChar);
            }
            finally
            {
                EndTrackedSnapshot();
            }

            _reviewHighlight = null;
            _inlineRevisionMarks = null;
            InvalidateFull();
        }
    }
}
