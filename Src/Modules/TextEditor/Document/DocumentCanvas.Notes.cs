using SkiaSharp;
using System;
using System.Collections.Generic;
using Writersword.Core.Models.Rendering;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.Models.Styles;
using Writersword.Modules.TextEditor.Rendering;
using Writersword.Modules.TextEditor.Services;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Сноски на листах.
    ///
    /// Знак сноски стоит в тексте объектом строки — маленькой цифрой над строкой.
    /// Текст сноски лежит внизу того же листа, под короткой чертой: место под него
    /// отнимается у листа по ходу раскладки, строка за строкой. Строка со знаком
    /// остаётся на листе, только если вместе с ней помещается начало её сноски;
    /// сноска, не поместившаяся целиком, продолжается внизу следующего листа под
    /// чертой во всю ширину — как у Word.
    ///
    /// Концевые сноски стоят после последнего абзаца документа, под такой же чертой.
    ///
    /// Только при раскладке страницами. В черновике листов нет, и там от сноски
    /// виден один знак — как в черновике Word.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        /// <summary>Кусок текста сноски на листе.</summary>
        /// <param name="Note">Сноска.</param>
        /// <param name="Layout">Раскладка абзаца сноски.</param>
        /// <param name="LineFrom">Первая строка куска.</param>
        /// <param name="LineTo">Строка за последней строкой куска.</param>
        /// <param name="PageIndex">Лист.</param>
        /// <param name="TopInPagePt">Верх куска от верхнего края листа.</param>
        /// <param name="TextTopInPagePt">Верх первой строки куска от верхнего края листа.</param>
        /// <param name="HeightPt">Высота куска вместе с интервалами абзаца.</param>
        private sealed record NoteSlice(
            NoteBlock Note,
            SKTextLayout Layout,
            int LineFrom,
            int LineTo,
            int PageIndex,
            float TopInPagePt,
            float TextTopInPagePt,
            float HeightPt);

        /// <summary>Черта над сносками листа.</summary>
        /// <param name="PageIndex">Лист.</param>
        /// <param name="TopInPagePt">Верх строки с чертой от верхнего края листа.</param>
        /// <param name="BandPt">Высота строки с чертой.</param>
        /// <param name="LengthPt">Длина черты.</param>
        private sealed record NoteRule(int PageIndex, float TopInPagePt, float BandPt, float LengthPt);

        // Номера сносок, по которым рисуются знаки, и номера, с которыми сверены кеши
        // раскладок. Вторые отстают от первых на время между появлением новой сноски
        // и ближайшей пересборкой: по разнице пересборка узнаёт, какие абзацы сверстаны
        // с прежними номерами.
        private NoteNumbering _noteNumbering = NoteNumbering.Empty;
        private NoteNumbering _noteNumberingApplied = NoteNumbering.Empty;

        // Раскладки абзацев сносок. Ключ — абзац сноски; раскладка годится, пока те же
        // текст, номер и ширина.
        private readonly Dictionary<ParagraphBlock,
            (string Text, string Mark, float Width, SKTextLayout Layout)> _noteLayoutCache = new();

        // Сноски на листах: итог последней раскладки и итог идущего прохода.
        private List<NoteSlice> _noteSlices = new();
        private List<NoteRule> _noteRules = new();
        private List<NoteSlice> _passNoteSlices = new();
        private List<NoteRule> _passNoteRules = new();

        /// <summary>Вид исправлений, при котором считаются номера сносок.</summary>
        private RevisionView NoteRevisionView()
            => _styleResolver?.RevisionView
               ?? DocVm?.Document?.RevisionView
               ?? RevisionView.AllMarkup;

        /// <summary>Сноска по Id её знака. Живёт в InlineObjects раздела, как картинка строки.</summary>
        private NoteBlock? FindInlineNote(Guid id)
        {
            var document = DocVm?.Document;
            if (document is null) return null;

            foreach (var section in document.Sections)
            {
                foreach (var block in section.InlineObjects)
                {
                    if (block is NoteBlock note && note.Id == id)
                        return note;
                }
            }

            return null;
        }

        /// <summary>
        /// Знак сноски для вёрстки строки. Сноска, появившаяся после последнего счёта
        /// (вставка из буфера, отмена удаления), получает номер сразу: счёт повторяется,
        /// а абзацы, чьи номера при этом сдвинулись, перевёрстывает ближайшая пересборка.
        /// </summary>
        private string? GetInlineNoteMark(Guid id)
        {
            if (_noteNumbering.MarkOf(id) is { } mark) return mark;
            if (FindInlineNote(id) is null) return null;

            var fresh = NoteNumbering.Compute(DocVm?.Document, NoteRevisionView());
            _noteNumbering = fresh;
            return fresh.MarkOf(id);
        }

        /// <summary>
        /// Пересчитывает номера сносок перед раскладкой. Абзац, у знака в котором
        /// сменился номер, перевёрстывается: текст его не менялся, и кеш раскладок
        /// сам этого не заметил бы, а ширина знака «9» и знака «10» разная.
        /// </summary>
        private void RefreshNoteNumbering()
        {
            var fresh = NoteNumbering.Compute(DocVm?.Document, NoteRevisionView());
            var applied = _noteNumberingApplied;

            _noteNumbering = fresh;
            _noteNumberingApplied = fresh;

            if (!fresh.HasNotes)
            {
                _noteLayoutCache.Clear();
                return;
            }

            HashSet<ParagraphBlock>? stale = null;
            bool cellsStale = false;
            var alive = new HashSet<ParagraphBlock>();

            void Check(IReadOnlyList<NoteReference> references)
            {
                foreach (var reference in references)
                {
                    foreach (var paragraph in reference.Note.Paragraphs)
                        alive.Add(paragraph);

                    if (string.Equals(applied.MarkOf(reference.Note.Id), reference.Mark, StringComparison.Ordinal))
                        continue;

                    if (reference.InTable) cellsStale = true;
                    else (stale ??= new HashSet<ParagraphBlock>()).Add(reference.Owner);
                }
            }

            Check(fresh.Footnotes);
            Check(fresh.Endnotes);

            if (stale is not null && _layoutCache.Count > 0)
            {
                List<ViewModels.Blocks.ParagraphViewModel>? drop = null;
                foreach (var key in _layoutCache.Keys)
                {
                    if (key.Model is not null && stale.Contains(key.Model))
                        (drop ??= new List<ViewModels.Blocks.ParagraphViewModel>()).Add(key);
                }

                if (drop is not null)
                    foreach (var key in drop)
                        _layoutCache.Remove(key);
            }

            if (cellsStale) InvalidateCellLayoutCaches();

            // Раскладки сносок, которых в документе больше нет.
            if (_noteLayoutCache.Count > 0)
            {
                List<ParagraphBlock>? dead = null;
                foreach (var key in _noteLayoutCache.Keys)
                    if (!alive.Contains(key)) (dead ??= new List<ParagraphBlock>()).Add(key);

                if (dead is not null)
                    foreach (var key in dead)
                        _noteLayoutCache.Remove(key);
            }
        }

        /// <summary>
        /// Раскладки абзацев сноски, по одной на абзац. Первый абзац верстается вместе
        /// с номером сноски в начале.
        /// </summary>
        private List<SKTextLayout> NoteLayoutsOf(NoteReference reference, float widthPt)
        {
            var note = reference.Note;
            var layouts = new List<SKTextLayout>(note.Paragraphs.Count);

            for (int i = 0; i < note.Paragraphs.Count; i++)
            {
                var paragraph = note.Paragraphs[i];
                string mark = NoteLayout.MarkFor(note, i, reference.Mark);
                string text = paragraph.GetPlainText();

                if (_noteLayoutCache.TryGetValue(paragraph, out var cached)
                    && cached.Text == text
                    && cached.Mark == mark
                    && Math.Abs(cached.Width - widthPt) < 0.1f)
                {
                    layouts.Add(cached.Layout);
                    continue;
                }

                var layout = NoteLayout.Build(_renderer, note, i, reference.Mark, widthPt, _styleResolver!);

                _noteLayoutCache[paragraph] = (text, mark, widthPt, layout);
                layouts.Add(layout);
            }

            return layouts;
        }

        /// <summary>
        /// Начинает счёт сносок для прохода раскладки страниц. null — сносок в документе
        /// нет, и проход идёт как шёл.
        /// </summary>
        private NoteFlow? BeginNoteFlow(float textWidthPt)
        {
            var numbering = _noteNumbering;
            if (!numbering.HasNotes || _styleResolver is null) return null;

            return new NoteFlow(
                numbering,
                reference => NoteLayoutsOf(reference, textWidthPt),
                NoteLayout.RuleBandPt(_renderer, textWidthPt, _styleResolver, FallbackLinePt),
                FallbackLinePt);
        }

        /// <summary>
        /// Ставит области сносок на листы: область прижата к нижнему полю своего листа,
        /// сверху черта, под ней сноски. Над продолжением сноски с прошлого листа черта
        /// идёт во всю ширину текста, как у Word.
        /// </summary>
        private static void PlaceNoteAreas(
            NoteFlow noteFlow,
            List<PageRect> pages,
            float textWidthPt,
            List<NoteSlice> slices,
            List<NoteRule> rules)
        {
            foreach (var area in noteFlow.BuildAreas())
            {
                if (area.PageIndex < 0 || area.PageIndex >= pages.Count) continue;

                var page = pages[area.PageIndex];
                float topPt = page.HeightPt - page.PadBottomPt - area.HeightPt;

                rules.Add(new NoteRule(
                    area.PageIndex, topPt, area.RuleBandPt,
                    area.Continued ? textWidthPt : Math.Min(NoteFlow.RuleLengthPt, textWidthPt)));

                foreach (var piece in area.Pieces)
                {
                    slices.Add(new NoteSlice(
                        piece.Note, piece.Layout, piece.LineFrom, piece.LineTo, area.PageIndex,
                        topPt + piece.OffsetPt, topPt + piece.TextOffsetPt, piece.HeightPt));
                }
            }
        }

        /// <summary>
        /// Сноски листов, которые частичный проход не трогает: они переходят из прошлой
        /// раскладки как есть, а сами сноски считаются уже расставленными.
        /// </summary>
        private void SeedPartialNotes(
            NoteFlow? noteFlow, int fromPage, List<NoteSlice> slices, List<NoteRule> rules)
        {
            List<NoteSlice> previousSlices;
            List<NoteRule> previousRules;
            lock (_renderLock)
            {
                previousSlices = _noteSlices;
                previousRules = _noteRules;
            }

            foreach (var slice in previousSlices)
            {
                if (slice.PageIndex >= fromPage) continue;
                slices.Add(slice);
                noteFlow?.MarkPlaced(slice.Note.Id);
            }

            foreach (var rule in previousRules)
                if (rule.PageIndex < fromPage) rules.Add(rule);
        }

        /// <summary>
        /// Сноски, чьи знаки стоят в ячейках только что разложенной таблицы. Таблица
        /// делит строки по листам сама и про сноски не знает, поэтому её сноски
        /// встают после неё: на последнем листе таблицы — в место, оставшееся под ней,
        /// на прежних листах — в свободный остаток под куском таблицы. Что не
        /// поместилось, продолжается на следующем листе.
        /// </summary>
        private void CommitTableNotes(
            NoteFlow noteFlow,
            List<ParaLayout> newLayouts,
            int layoutsFrom,
            List<TableEntry> newTables,
            int tablesFrom,
            List<PageRect> newPages,
            int pageIdx,
            float contentYPt,
            ref float pageBottomPt)
        {
            List<(NoteReference Reference, int PageIndex)>? found = null;
            HashSet<Guid>? seen = null;

            for (int i = layoutsFrom; i < newLayouts.Count; i++)
            {
                var pl = newLayouts[i];
                if (pl.Cell is null || pl.Layout is null || pl.Vm.Model is null) continue;
                if (!noteFlow.HasMarksIn(pl.Vm.Model)) continue;

                int lineFrom = Math.Max(0, pl.LineFrom);
                int lineTo = Math.Min(pl.LineTo, pl.Layout.Lines.Count);

                for (int li = lineFrom; li < lineTo; li++)
                {
                    foreach (var reference in noteFlow.ReferencesIn(pl.Layout.Lines[li]))
                    {
                        if (!(seen ??= new HashSet<Guid>()).Add(reference.Note.Id)) continue;
                        (found ??= new List<(NoteReference, int)>()).Add((reference, pl.PageIndex));
                    }
                }
            }

            if (found is null) return;

            found.Sort((a, b) => a.Reference.Ordinal.CompareTo(b.Reference.Ordinal));

            foreach (var (reference, notePage) in found)
            {
                if (notePage >= pageIdx)
                {
                    float freePt = Math.Max(0f, pageBottomPt - contentYPt);
                    float beforePt = freePt;
                    noteFlow.CommitAt(reference, pageIdx, ref freePt);
                    pageBottomPt -= beforePt - freePt;
                    continue;
                }

                if (notePage < 0 || notePage >= newPages.Count) continue;

                // Лист, который таблица уже прошла: свободно то, что осталось между
                // низом её куска и сносками, уже стоящими на этом листе.
                var page = newPages[notePage];
                float bottomPt = page.Ypt + page.HeightPt - page.PadBottomPt - noteFlow.ReservedOn(notePage);
                float usedBottomPt = page.Ypt + page.PadTopPt;

                for (int ti = tablesFrom; ti < newTables.Count; ti++)
                {
                    var te = newTables[ti];
                    if (te.PageIndex != notePage) continue;
                    usedBottomPt = Math.Max(usedBottomPt, te.Ypt + TableSliceHeightPt(te));
                }

                float finishedFreePt = Math.Max(0f, bottomPt - usedBottomPt);
                noteFlow.CommitAt(reference, notePage, ref finishedFreePt);
            }
        }

        /// <summary>
        /// Рисует сноски листов: черту и текст. В координатах раскладки, после
        /// абзацев листа — тем же рендером, что и текст документа.
        /// </summary>
        private void RenderNotes(SKCanvas canvas, List<PageRect> pages, int firstPage, int lastPage)
        {
            List<NoteSlice> slices;
            List<NoteRule> rules;
            lock (_renderLock)
            {
                slices = _noteSlices;
                rules = _noteRules;
            }

            if (slices.Count == 0 && rules.Count == 0) return;

            if (rules.Count > 0)
            {
                using var rulePaint = new SKPaint
                {
                    Color = SKTextRenderer.DefaultTextColorOverride ?? SKColors.Black,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = NoteFlow.RuleThicknessPt,
                    IsAntialias = true
                };

                foreach (var rule in rules)
                {
                    if (rule.PageIndex < firstPage || rule.PageIndex > lastPage) continue;
                    if (rule.PageIndex < 0 || rule.PageIndex >= pages.Count) continue;

                    var page = pages[rule.PageIndex];
                    float leftPt = page.PadLeftPt + page.MarginLeftPt;
                    float yPt = page.Ypt + rule.TopInPagePt + rule.BandPt * NoteFlow.RulePositionShare;

                    canvas.DrawLine(leftPt, yPt, leftPt + rule.LengthPt, yPt, rulePaint);
                }
            }

            foreach (var slice in slices)
            {
                if (slice.PageIndex < firstPage || slice.PageIndex > lastPage) continue;
                if (slice.PageIndex < 0 || slice.PageIndex >= pages.Count) continue;

                var page = pages[slice.PageIndex];
                float leftPt = page.PadLeftPt + page.MarginLeftPt;

                canvas.Save();
                canvas.ClipRect(new SKRect(
                    page.PadLeftPt, page.Ypt,
                    page.PadLeftPt + page.WidthPt, page.Ypt + page.HeightPt));

                SKTextRenderer.RenderParagraphLines(
                    canvas, slice.Layout,
                    leftPt + slice.Layout.LeftIndentPt,
                    page.Ypt + slice.TextTopInPagePt,
                    slice.LineFrom, slice.LineTo);

                canvas.Restore();
            }
        }

        /// <summary>Сноска, по тексту которой попала точка листа (в координатах раскладки).</summary>
        private bool TryHitNoteText(float xPt, float yPt, out NoteBlock? note)
        {
            note = null;

            List<NoteSlice> slices;
            List<PageRect> pages;
            lock (_renderLock)
            {
                slices = _noteSlices;
                pages = _pages;
            }

            foreach (var slice in slices)
            {
                if (slice.PageIndex < 0 || slice.PageIndex >= pages.Count) continue;

                var page = pages[slice.PageIndex];
                float topPt = page.Ypt + slice.TopInPagePt;
                if (yPt < topPt || yPt >= topPt + slice.HeightPt) continue;
                if (xPt < page.PadLeftPt || xPt > page.PadLeftPt + page.WidthPt) continue;

                note = slice.Note;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Формула в строке у позиции символа в абзаце: под указателем или сразу перед ним.
        /// </summary>
        private FormulaBlock? FormulaAt(ParagraphBlock? paragraph, int charIndex)
        {
            if (paragraph is null) return null;

            foreach (int index in new[] { charIndex, charIndex - 1 })
            {
                if (paragraph.GetInlineImageIdAt(index) is Guid id && FindInlineFormula(id) is { } formula)
                    return formula;
            }

            return null;
        }

        /// <summary>
        /// Сноска, чей знак стоит у позиции символа в абзаце: сам знак под указателем
        /// или сразу перед ним.
        /// </summary>
        private NoteBlock? NoteMarkAt(ParagraphBlock? paragraph, int charIndex)
        {
            if (paragraph is null) return null;

            foreach (int index in new[] { charIndex, charIndex - 1 })
            {
                if (paragraph.GetInlineImageIdAt(index) is Guid id
                    && _noteNumbering.TryGet(id, out var reference))
                    return reference.Note;
            }

            return null;
        }
    }
}
