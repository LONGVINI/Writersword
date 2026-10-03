using System;
using System.Collections.Generic;
using Avalonia;
using SkiaSharp;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Page;
using Writersword.Modules.TextEditor.Rendering;
using Writersword.Modules.TextEditor.Services;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Колонтитулы и номера страниц на листах правки.
    ///
    /// Номер листа знает только раскладка, поэтому здесь же собираются сведения о листах
    /// (сколько их, где метки правил, где начинаются главы) — их спрашивают и лента, и
    /// выгрузка в Word. Колонтитулы рисуются в полях листа тем же художником, что и в
    /// печати, поверх листа и под выделением.
    ///
    /// Только в режиме страниц: ни в черновике, ни в чтении, ни в книге колонтитулов нет.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        // Сведения о листах и оформление листов последней раскладки. Считаются лениво,
        // один раз на раскладку и версию колонтитулов: отрисовка кадра спрашивает их
        // для каждого видимого листа, и пересчёт на каждом кадре был бы пустой тратой.
        private readonly object _headerFooterLock = new();
        private object? _factsLayoutsRef;
        private int _factsPageCount = -1;
        private PageFacts _pageFacts = PageFacts.Empty;
        private int _decorationsVersion = -1;
        private PageDecoration[] _decorations = Array.Empty<PageDecoration>();

        // Растёт при каждой правке колонтитулов и правил: оформление листов пересчитывается.
        private int _headerFooterVersion;

        // Цвет пометок правки на листе: номер, набранный руками, и метки правил в тексте.
        private static readonly SKColor HeaderFooterHintColor = new(0xE0, 0x7B, 0x39);

        // Границы колонтитулов в режиме колонтитулов — синий пунктир, как в Word.
        private static readonly SKColor HeaderFooterGuideColor = new(0x2B, 0x57, 0x9A);

        // Насколько блекнет текст документа, пока правятся колонтитулы (0..255): лист
        // накрывается полупрозрачной дымкой — белой, когда текст тёмный (светлая
        // бумага), чёрной, когда текст светлый. Судить по цвету бумаги вида нельзя: под
        // картинкой бумаги он бывает любым, а цвет текста всегда подобран к бумаге.
        private const byte HeaderFooterBodyDimAlpha = 120;

        // Полоса, которая сейчас правится полем ввода поверх листа. Полотно её текст
        // не рисует: иначе под набираемым текстом проступал бы прежний.
        private int _hfEditPage = -1;
        private bool _hfEditHeader;

        // Сколько места сверху и снизу листа занимают колонтитулы (HeaderFooterPainter.
        // BodyReservePt). Поле листа меньше этого раскладка увеличивает — текст не
        // заходит под колонтитул.
        private float _hfReserveTopPt;
        private float _hfReserveBottomPt;

        /// <summary>
        /// Колонтитулы рисуются: режим страниц, не чтение и не книга. Без колонтитулов в
        /// документе рисовать нечего.
        /// </summary>
        private bool HeaderFootersDrawable
            => DocVm?.ViewMode == EditorViewMode.Page && !ReadingActive && !SpreadMode;

        /// <summary>Подписка на вью-модель: полотно отвечает ей про листы и слушает правки колонтитулов.</summary>
        private void WireHeaderFooterDelegates()
        {
            if (DocVm is null) return;

            DocVm.GetCaretPageIndexDelegate = GetCaretPageIndex;
            DocVm.GetPageFactsDelegate = GetCurrentPageFacts;

            DocVm.HeaderFooterChanged -= OnHeaderFooterChanged;
            DocVm.HeaderFooterChanged += OnHeaderFooterChanged;

            DocVm.HeaderFooterModeChanged -= OnHeaderFooterModeChangedForCanvas;
            DocVm.HeaderFooterModeChanged += OnHeaderFooterModeChangedForCanvas;

            UpdateHeaderFooterReserve();
        }

        /// <summary>
        /// Пересчитывает место под колонтитулы. Изменилось — листы раскладываются
        /// заново: текст сдвигается из-под колонтитула или возвращается на место.
        /// </summary>
        private bool UpdateHeaderFooterReserve()
        {
            float top = 0f, bottom = 0f;

            var ps = DocVm?.Document.PageSettings;
            if (DocVm is not null && ps is not null)
            {
                (top, bottom) = HeaderFooterPainter.BodyReservePt(DocVm.HeaderFooter,
                    MmToPt(ps.HeaderDistanceMm), MmToPt(ps.FooterDistanceMm),
                    SKTextRenderer.ResolveTypeface, HeaderFooterFallbackFamily());
            }

            bool changed = Math.Abs(top - _hfReserveTopPt) > 0.01f || Math.Abs(bottom - _hfReserveBottomPt) > 0.01f;
            _hfReserveTopPt = top;
            _hfReserveBottomPt = bottom;
            return changed;
        }

        /// <summary>
        /// Вход в колонтитулы и выход из них: текст документа блекнет или возвращается,
        /// появляются или пропадают границы колонтитулов.
        /// </summary>
        private void OnHeaderFooterModeChangedForCanvas()
        {
            if (DocVm is { IsHeaderFooterMode: false })
                _hfEditPage = -1;
            InvalidateFull();
        }

        /// <summary>Полоса листа правится полем ввода — полотно её текст не рисует.</summary>
        public void SetHeaderFooterEditBand(int pageIndex, bool header)
        {
            if (_hfEditPage == pageIndex && _hfEditHeader == header) return;
            _hfEditPage = pageIndex;
            _hfEditHeader = header;
            InvalidateFull();
        }

        /// <summary>Правка полосы закончена — полотно снова рисует её текст.</summary>
        public void ClearHeaderFooterEditBand()
        {
            if (_hfEditPage < 0) return;
            _hfEditPage = -1;
            InvalidateFull();
        }

        /// <summary>Гарнитура колонтитулов, как её рисует полотно.</summary>
        public string HeaderFooterFontFamilyName
        {
            get
            {
                var settings = DocVm?.HeaderFooter;
                if (settings is not null && !string.IsNullOrWhiteSpace(settings.FontFamily))
                    return settings.FontFamily!;
                return DocVm is null ? "Times New Roman" : HeaderFooterFallbackFamily();
            }
        }

        /// <summary>Цвет текста колонтитулов на текущей бумаге — для поля правки.</summary>
        public Avalonia.Media.Color HeaderFooterInkColor
        {
            get
            {
                var ink = HeaderFooterInk(DocVm?.HeaderFooter ?? new HeaderFooterSettings());
                return Avalonia.Media.Color.FromArgb(ink.Alpha, ink.Red, ink.Green, ink.Blue);
            }
        }

        /// <summary>Высота строки колонтитула в точках экрана — для поля правки.</summary>
        public double HeaderFooterLineHeightPx
        {
            get
            {
                if (DocVm is null) return 16;
                var settings = DocVm.HeaderFooter ?? new HeaderFooterSettings();
                using var font = HeaderFooterPainter.CreateFont(settings, SKTextRenderer.ResolveTypeface,
                    HeaderFooterFallbackFamily());
                return HeaderFooterPainter.LineHeight(font) * PointToPixelScale;
            }
        }

        private void OnHeaderFooterChanged()
        {
            System.Threading.Interlocked.Increment(ref _headerFooterVersion);

            // Колонтитул стал выше или ниже поля листа, либо на каком-то листе он
            // появился или пропал (правило, вариант первой или чётной страницы) —
            // раскладка листов меняется. Строки абзацев при этом прежние (ширина та
            // же), кеш раскладки годен.
            bool reserveChanged = UpdateHeaderFooterReserve();
            if (reserveChanged | BandReserveOutdated())
            {
                RebuildLayouts();
                InvalidateMeasure();
            }

            InvalidateFull();

            // Счёт мог начаться заново или с другого числа — номера в оглавлении
            // (DocumentCanvas.Toc) печатаются по нему же и устаревают вместе с ним.
            OnTocPageNumbersStale(false);
        }

        /// <summary>Лист каретки с нуля.</summary>
        private int GetCaretPageIndex()
        {
            List<ParaLayout> layouts;
            lock (_renderLock) { layouts = _layouts; }

            if (_caretPara >= 0 && _caretPara < layouts.Count)
                return Math.Max(layouts[_caretPara].PageIndex, 0);

            return 0;
        }

        /// <summary>Сведения о листах текущей раскладки.</summary>
        public PageFacts GetCurrentPageFacts()
        {
            List<ParaLayout> layouts;
            List<PageRect> pages;
            lock (_renderLock)
            {
                layouts = _layouts;
                pages = _pages;
            }
            return GetPageFacts(layouts, pages.Count);
        }

        private PageFacts GetPageFacts(List<ParaLayout> layouts, int pageCount)
        {
            lock (_headerFooterLock)
            {
                if (ReferenceEquals(_factsLayoutsRef, layouts) && _factsPageCount == pageCount)
                    return _pageFacts;

                _pageFacts = BuildPageFacts(layouts, pageCount);
                _factsLayoutsRef = layouts;
                _factsPageCount = pageCount;
                _decorationsVersion = -1;
                return _pageFacts;
            }
        }

        /// <summary>
        /// Сведения о листах по раскладке: где начинается каждый абзац потока, какой
        /// абзац открывает лист и на каких листах начинаются главы.
        ///
        /// Абзац, разрезанный между листами, числится на листе своей первой строки.
        /// Абзацы ячеек таблиц в метки не годятся, но лист, который открывает таблица,
        /// открыт не абзацем — это важно выгрузке в Word.
        /// </summary>
        private PageFacts BuildPageFacts(List<ParaLayout> layouts, int pageCount)
        {
            var starts = new Dictionary<Guid, int>();
            var chapters = new HashSet<int>();
            var opening = new Guid?[Math.Max(pageCount, 0)];
            var seen = new bool[Math.Max(pageCount, 0)];
            var doc = DocVm?.Document;

            foreach (var pl in layouts)
            {
                int page = pl.PageIndex;
                if (page < 0 || page >= pageCount) continue;

                if (pl.Cell is not null)
                {
                    seen[page] = true;
                    continue;
                }

                var model = pl.Vm?.Model;
                if (model is null) continue;

                if (pl.LineFrom == 0)
                {
                    starts.TryAdd(model.Id, page);
                    if (!seen[page]) opening[page] = model.Id;

                    if (doc is not null && HeadingCollapseService.LevelOf(model, doc) == 1)
                        chapters.Add(page);
                }

                seen[page] = true;
            }

            return new PageFacts
            {
                PageCount = pageCount,
                ParagraphStartPages = starts,
                ChapterStartPages = chapters,
                PageOpeningParagraphs = opening
            };
        }

        /// <summary>Оформление листов текущей раскладки.</summary>
        private PageDecoration[] GetDecorations(List<ParaLayout> layouts, int pageCount)
        {
            var facts = GetPageFacts(layouts, pageCount);
            int version = System.Threading.Volatile.Read(ref _headerFooterVersion);

            lock (_headerFooterLock)
            {
                if (_decorationsVersion == version && _decorations.Length == pageCount)
                    return _decorations;

                _decorations = PageNumbering.Compute(DocVm?.HeaderFooter, pageCount,
                    facts.ParagraphStartPages, facts.ChapterStartPages);
                _decorationsVersion = version;
                return _decorations;
            }
        }

        /// <summary>Лист глазами колонтитула — в логических координатах раскладки.</summary>
        private HeaderFooterPageBox HeaderFooterBox(PageRect page)
        {
            var (_, _, marginRight, _) = GetPagePaddingPt();
            var ps = DocVm?.Document.PageSettings;

            return new HeaderFooterPageBox(
                page.PadLeftPt,
                page.Ypt,
                page.WidthPt,
                page.HeightPt,
                page.MarginLeftPt,
                // На листе с переплётом справа правое поле шире на переплёт.
                marginRight - page.GutterShiftPt,
                page.PadTopPt,
                page.PadBottomPt,
                MmToPt(ps?.HeaderDistanceMm ?? 12),
                MmToPt(ps?.FooterDistanceMm ?? 12));
        }

        /// <summary>Гарнитура колонтитулов по умолчанию — как у стиля «Обычный».</summary>
        private string HeaderFooterFallbackFamily()
            => (_styleResolver ?? CreateStyleResolver()).ResolveFontFamily(StyleResolver.DefaultStyleName);

        /// <summary>Цвет текста колонтитулов на текущей бумаге.</summary>
        private static SKColor HeaderFooterInk(HeaderFooterSettings settings)
        {
            var color = new SKColor(0x1A, 0x1A, 0x1A);
            if (!string.IsNullOrWhiteSpace(settings.TextColor) && SKColor.TryParse(settings.TextColor, out var own))
                color = own;
            return SKTextRenderer.ResolveInk(color);
        }

        /// <summary>
        /// Колонтитулы листов [first..last]. Вызывается из прохода содержимого, в
        /// логических координатах: лист на своё место переносит вызывающий.
        /// </summary>
        private void RenderHeaderFooters(
            SKCanvas canvas, List<ParaLayout> layouts, List<PageRect> pages, int firstPage, int lastPage)
        {
            if (!HeaderFootersDrawable || pages.Count == 0) return;

            var settings = DocVm?.HeaderFooter;
            bool marks = FormattingMarksVisible;

            // В PDF лист всегда обычный: дымка, пунктир и поле набора — это правка.
            bool exporting = ExportPassActive;
            bool mode = DocVm?.IsHeaderFooterMode == true && !exporting;

            // Набор в колонтитуле, как в Word: текст документа блекнет, у колонтитулов
            // видны пунктирные границы. Рисуется до текста колонтитулов — он остаётся ярким.
            // Пока каретка в тексте документа, лист обычный, хотя вкладка ещё открыта.
            if (mode && _hfEditPage >= 0)
                DrawHeaderFooterModeGuides(canvas, layouts, pages, firstPage, lastPage);

            if (settings is not null && !settings.IsEmpty)
            {
                var decorations = GetDecorations(layouts, pages.Count);
                var color = HeaderFooterInk(settings);
                string family = HeaderFooterFallbackFamily();

                using var font = HeaderFooterPainter.CreateFont(settings, SKTextRenderer.ResolveTypeface, family);

                for (int pi = Math.Max(firstPage, 0); pi <= lastPage && pi < pages.Count && pi < decorations.Length; pi++)
                {
                    var box = HeaderFooterBox(pages[pi]);
                    var decoration = decorations[pi];

                    bool editingHere = mode && pi == _hfEditPage;
                    HeaderFooterPainter.DrawPage(canvas, settings, decoration, box, color,
                        SKTextRenderer.ResolveTypeface, family,
                        drawHeader: !(editingHere && _hfEditHeader),
                        drawFooter: !(editingHere && !_hfEditHeader));

                    // Номер, набранный руками, обводится пунктиром — только на экране:
                    // видно, что на этом листе число живёт не по счёту.
                    if (!exporting && decoration.ManualText is not null && decoration.BandsVisible)
                        DrawManualNumberHint(canvas, settings, decoration, box, font);
                }
            }

            // Метки правил в тексте видны вместе с непечатаемыми знаками — как ¶.
            if (marks && settings is { Rules.Count: > 0 })
                DrawRuleAnchors(canvas, layouts, pages, settings, firstPage, lastPage);
        }

        /// <summary>
        /// Режим колонтитулов на листах: текст документа накрыт полупрозрачной дымкой, а
        /// верхний и нижний колонтитулы отделены пунктиром — как в Word.
        /// </summary>
        private void DrawHeaderFooterModeGuides(
            SKCanvas canvas, List<ParaLayout> layouts, List<PageRect> pages, int firstPage, int lastPage)
        {
            var settings = DocVm?.HeaderFooter ?? new HeaderFooterSettings();
            var decorations = GetDecorations(layouts, pages.Count);

            var ink = HeaderFooterInk(settings);
            float inkLuminance = (0.299f * ink.Red + 0.587f * ink.Green + 0.114f * ink.Blue) / 255f;
            var haze = inkLuminance < 0.5f ? SKColors.White : SKColors.Black;

            using var dim = new SKPaint
            {
                Color = haze.WithAlpha(HeaderFooterBodyDimAlpha),
                Style = SKPaintStyle.Fill,
                IsAntialias = false
            };

            using var line = new SKPaint
            {
                Color = HeaderFooterGuideColor,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 0.6f,
                PathEffect = SKPathEffect.CreateDash(new[] { 3f, 2f }, 0f)
            };

            using var bandFont = HeaderFooterPainter.CreateFont(settings, SKTextRenderer.ResolveTypeface,
                HeaderFooterFallbackFamily());

            for (int pi = Math.Max(firstPage, 0); pi <= lastPage && pi < pages.Count; pi++)
            {
                var box = HeaderFooterBox(pages[pi]);
                var decoration = pi < decorations.Length ? decorations[pi] : null;

                // Граница верхнего колонтитула — по нижнему краю поля листа, но не выше
                // нижней строки колонтитула; нижнего — зеркально.
                int headerLines = BandLines(settings, decoration, true);
                int footerLines = BandLines(settings, decoration, false);
                var headerSlot = HeaderFooterPainter.SlotRect(box, true, 0, bandFont, headerLines);
                var footerSlot = HeaderFooterPainter.SlotRect(box, false, 0, bandFont, footerLines);

                float headerLine = Math.Max(HeaderFooterPainter.ZoneRect(box, true).Bottom, headerSlot.Bottom + 2f);
                float footerLine = Math.Min(HeaderFooterPainter.ZoneRect(box, false).Top, footerSlot.Top - 2f);

                if (footerLine > headerLine)
                    canvas.DrawRect(new SKRect(box.X, headerLine, box.X + box.Width, footerLine), dim);

                canvas.DrawLine(box.X, headerLine, box.X + box.Width, headerLine, line);
                canvas.DrawLine(box.X, footerLine, box.X + box.Width, footerLine, line);
            }
        }

        /// <summary>Строк в полосе листа (не меньше одной).</summary>
        private static int BandLines(HeaderFooterSettings settings, PageDecoration? decoration, bool header)
        {
            if (decoration is null) return 1;

            var band = settings.GetBand(decoration.Variant, header);
            int lines = 1;
            for (int s = 0; s < 3; s++)
            {
                string text = band.GetSlot(s);
                if (text.Length > 0)
                    lines = Math.Max(lines, HeaderFooterPainter.SplitLines(text).Count);
            }
            return lines;
        }

        private static void DrawManualNumberHint(
            SKCanvas canvas, HeaderFooterSettings settings, PageDecoration decoration,
            HeaderFooterPageBox box, SKFont font)
        {
            using var paint = new SKPaint
            {
                Color = HeaderFooterHintColor,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 0.75f,
                PathEffect = SKPathEffect.CreateDash(new[] { 2f, 1.5f }, 0f)
            };

            for (int band = 0; band < 2; band++)
            {
                bool header = band == 0;
                var bandModel = settings.GetBand(decoration.Variant, header);

                for (int slot = 0; slot < 3; slot++)
                {
                    string template = bandModel.GetSlot(slot);
                    if (!template.Contains(HeaderFooterSettings.PageToken, StringComparison.Ordinal)) continue;

                    string? text = PageNumbering.RenderSlot(template, decoration);
                    var rect = HeaderFooterPainter.MeasureSlot(box, header, slot, text, font, settings);
                    if (rect is not { } r) continue;

                    r.Inflate(2f, 1f);
                    canvas.DrawRoundRect(r, 2f, 2f, paint);
                }
            }
        }

        /// <summary>
        /// Метки правил в тексте: знак λ перед первой строкой абзаца, к которому привязано
        /// правило. Потерянные метки (абзац удалён) не рисуются — их видно в списке правил.
        /// </summary>
        private void DrawRuleAnchors(
            SKCanvas canvas, List<ParaLayout> layouts, List<PageRect> pages,
            HeaderFooterSettings settings, int firstPage, int lastPage)
        {
            HashSet<Guid>? anchors = null;
            foreach (var rule in settings.Rules)
                if (rule.IsAnchored && rule.AnchorParagraphId is Guid id)
                    (anchors ??= new HashSet<Guid>()).Add(id);

            var marks = new List<(SKRect Rect, Guid Paragraph)>();

            if (anchors is not null)
            {
                using var paint = new SKPaint
                {
                    Color = HeaderFooterHintColor,
                    IsAntialias = true,
                    Style = SKPaintStyle.Fill
                };
                using var font = new SKFont(SKTypeface.Default, RuleMarkSizePt) { Subpixel = true };

                float glyphWidth = font.MeasureText(RuleMarkGlyph);
                var m = font.Metrics;

                foreach (var pl in layouts)
                {
                    if (pl.Cell is not null || pl.LineFrom != 0) continue;
                    if (pl.PageIndex < firstPage || pl.PageIndex > lastPage || pl.PageIndex >= pages.Count) continue;

                    var model = pl.Vm?.Model;
                    if (model is null || !anchors.Contains(model.Id)) continue;

                    // Знак λ — непечатаемый, как ¶: стоит вплотную перед первой строкой
                    // абзаца, в поле листа, и текст не сдвигает. Виден только вместе
                    // с остальными знаками, в печать не идёт.
                    var page = pages[pl.PageIndex];
                    float textLeft = page.PadLeftPt + page.MarginLeftPt;
                    float x = Math.Max(textLeft - glyphWidth - 3f, page.PadLeftPt + 2f);
                    float baseline = pl.Ypt - m.Ascent + 1f;

                    canvas.DrawText(RuleMarkGlyph, x, baseline, SKTextAlign.Left, font, paint);
                    marks.Add((new SKRect(x - 2f, baseline + m.Ascent - 2f, x + glyphWidth + 2f, baseline + m.Descent + 2f),
                        model.Id));
                }
            }

            lock (_headerFooterLock)
                _ruleMarks = marks;
        }

        // ── Подсказка у знака λ ───────────────────────────────────────────

        // Знак места, где действует исключение колонтитулов. Непечатаемый, как ¶.
        private const string RuleMarkGlyph = "λ";
        private const float RuleMarkSizePt = 10f;

        // Знаки, нарисованные последним кадром: где стоят и к какому абзацу относятся.
        private List<(SKRect Rect, Guid Paragraph)> _ruleMarks = new();

        // Абзац, чья подсказка сейчас показана.
        private Guid? _ruleMarkTipParagraph;

        /// <summary>
        /// Указатель над знаком λ — подсказка, что здесь за исключение и где его снять.
        /// Точка в логических координатах раскладки.
        /// </summary>
        private void UpdateRuleMarkTip(float xPt, float yPt)
        {
            Guid? hit = null;

            if (FormattingMarksVisible && DocVm?.HeaderFooter is { Rules.Count: > 0 })
            {
                List<(SKRect Rect, Guid Paragraph)> marks;
                lock (_headerFooterLock) marks = _ruleMarks;

                foreach (var (rect, paragraph) in marks)
                {
                    if (rect.Contains(xPt, yPt))
                    {
                        hit = paragraph;
                        break;
                    }
                }
            }

            if (hit == _ruleMarkTipParagraph) return;
            _ruleMarkTipParagraph = hit;

            if (hit is not Guid id || DocVm?.HeaderFooter is not { } settings)
            {
                Avalonia.Controls.ToolTip.SetIsOpen(this, false);
                Avalonia.Controls.ToolTip.SetTip(this, null);
                return;
            }

            var sb = new System.Text.StringBuilder();
            sb.Append("λ — отсюда действует исключение колонтитулов (в печать не идёт):");
            foreach (var rule in settings.Rules)
            {
                if (rule.AnchorParagraphId != id) continue;
                sb.Append('\n').Append("• ")
                  .Append(rule.IsRange ? "отсюда и дальше — " : "только эта страница — ")
                  .Append(ViewModels.DocumentViewModel.DescribeRuleAction(rule));
            }
            sb.Append('\n').Append("Снять: вкладка «Колонтитулы» → «Исключения». Знак едет вместе с абзацем.");

            Avalonia.Controls.ToolTip.SetTip(this, sb.ToString());
            Avalonia.Controls.ToolTip.SetIsOpen(this, true);
        }

        // ── Попадание и место поля правки ─────────────────────────────────

        /// <summary>
        /// Точка (логические координаты) лежит в поле листа над текстом или под ним.
        /// Место: треть ширины листа — лево, центр, право.
        /// </summary>
        private bool TryHitHeaderFooter(float xPt, float yPt, out int pageIndex, out bool header, out int slot)
        {
            pageIndex = -1;
            header = false;
            slot = 1;

            if (!HeaderFootersDrawable) return false;

            List<PageRect> pages;
            lock (_renderLock) { pages = _pages; }

            for (int pi = 0; pi < pages.Count; pi++)
            {
                var page = pages[pi];
                if (xPt < page.PadLeftPt || xPt > page.PadLeftPt + page.WidthPt) continue;
                if (yPt < page.Ypt || yPt > page.Ypt + page.HeightPt) continue;

                var box = HeaderFooterBox(page);
                var top = HeaderFooterPainter.ZoneRect(box, true);
                var bottom = HeaderFooterPainter.ZoneRect(box, false);

                if (top.Contains(xPt, yPt)) header = true;
                else if (!bottom.Contains(xPt, yPt)) return false;

                float third = Math.Max((box.ContentRight - box.ContentLeft) / 3f, 1f);
                slot = Math.Clamp((int)((xPt - box.ContentLeft) / third), 0, 2);
                pageIndex = pi;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Где на полотне (в точках экрана, координаты самого полотна) лежит полоса
        /// колонтитула листа: по ней вид ставит поле правки. Null — листа нет.
        /// </summary>
        public Rect? GetHeaderFooterBandRectPx(int pageIndex, bool header)
        {
            if (DocVm is null || !HeaderFootersDrawable) return null;

            List<PageRect> pages;
            lock (_renderLock) { pages = _pages; }
            if (pageIndex < 0 || pageIndex >= pages.Count) return null;

            var settings = DocVm?.HeaderFooter ?? new HeaderFooterSettings();
            var page = pages[pageIndex];
            var box = HeaderFooterBox(page);

            using var font = HeaderFooterPainter.CreateFont(settings, SKTextRenderer.ResolveTypeface,
                HeaderFooterFallbackFamily());

            int lines = 1;
            var decoration = DocVm?.GetPageDecoration(pageIndex);
            if (decoration is not null)
            {
                var band = settings.GetBand(decoration.Variant, header);
                for (int s = 0; s < 3; s++)
                {
                    string text = band.GetSlot(s);
                    if (text.Length > 0)
                        lines = Math.Max(lines, HeaderFooterPainter.SplitLines(text).Count);
                }
            }

            var left = HeaderFooterPainter.SlotRect(box, header, 0, font, lines);
            var right = HeaderFooterPainter.SlotRect(box, header, 2, font, lines);

            var (dx, dy) = PageVisualDelta(pageIndex, pages);

            // Лист в режиме «одна страница в ряд» во время жеста масштаба стоит со
            // сдвигом к живой середине полотна — тем же, что кладёт отрисовка.
            float shiftX = 0f;
            if (_pagesPerRow <= 1 && !SpreadMode)
            {
                float canvasWPt = (float)(_canvasWidth * PxToPt);
                shiftX = Math.Max((canvasWPt - GetPageWidthPt()) / 2f, 0f) - _layoutPageXPt;
            }

            double k = PtToPx * Zoom;
            double x = (left.Left + dx + shiftX) * k;
            double y = (left.Top + dy) * k;
            double w = (right.Right - left.Left) * k;
            double h = (left.Bottom - left.Top) * k;

            return new Rect(x, y, Math.Max(w, 40), Math.Max(h, 16));
        }

        /// <summary>Масштаб полотна: сколько точек экрана в пункте листа.</summary>
        public double PointToPixelScale => PtToPx * Zoom;
    }
}
