using System;
using System.Collections.Generic;
using Writersword.Modules.TextEditor.Models.Page;
using Writersword.Modules.TextEditor.Rendering;
using Writersword.Modules.TextEditor.Services;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Место под колонтитулы — по каждому листу отдельно.
    ///
    /// Как в Word: колонтитул, который выше поля листа, отодвигает текст только на тех
    /// листах, где он печатается. Лист без колонтитулов (правило «без колонтитулов»,
    /// начало главы, пустой вариант первой или чётной страницы) остаётся с обычным
    /// полем. Иначе документ, где колонтитулы есть лишь в части разделов, терял строку
    /// на каждом листе и разбивался на страницы не так, как в Word.
    ///
    /// Какие листы печатают колонтитулы, известно только после раскладки: правила
    /// привязаны к абзацам, и лист правила — это лист, куда лёг абзац. Поэтому место
    /// берётся из прошлой раскладки, а если по новой раскладке оно вышло другим,
    /// раскладка повторяется (<see cref="RefreshBandReserveFromPass"/>). Обычно
    /// хватает одного повтора, и только когда видимость колонтитулов у листов
    /// изменилась. Правка колонтитулов или их правил сверяет место с текущей
    /// раскладкой (<see cref="BandReserveOutdated"/>) и пересобирает её, если нужно.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        // Место сверху и снизу каждого листа по прошлой раскладке, в пунктах от края
        // листа. Ноль — колонтитула этой стороны на листе нет. Пустые массивы при
        // посчитанном месте — колонтитулов нет ни на одном листе.
        private float[] _bandReserveTopByPage = Array.Empty<float>();
        private float[] _bandReserveBottomByPage = Array.Empty<float>();

        // Место по листам уже посчитано хоть одной раскладкой. Пока нет — действует
        // общий запас документа (HeaderFooterPainter.BodyReservePt).
        private bool _bandReserveKnown;

        /// <summary>
        /// Верхнее и нижнее поле листа с учётом его колонтитулов: поле листа, но не
        /// меньше места, которое занимает колонтитул этого листа.
        /// </summary>
        private (float Top, float Bottom) PagePaddingForPage(int pageIndex, float baseTopPt, float baseBottomPt)
        {
            if (!HeaderFootersDrawable) return (baseTopPt, baseBottomPt);

            var (reserveTop, reserveBottom) = BandReserveForPage(pageIndex);
            return (Math.Max(baseTopPt, reserveTop), Math.Max(baseBottomPt, reserveBottom));
        }

        /// <summary>
        /// Место колонтитулов листа по прошлой раскладке. Лист дальше известных берёт
        /// место последнего известного: правила-отрезки действуют и на листы после себя.
        /// </summary>
        private (float Top, float Bottom) BandReserveForPage(int pageIndex)
        {
            var tops = _bandReserveTopByPage;
            var bottoms = _bandReserveBottomByPage;

            if (!_bandReserveKnown) return (_hfReserveTopPt, _hfReserveBottomPt);

            if (tops.Length == 0 || bottoms.Length != tops.Length) return (0f, 0f);

            int i = Math.Clamp(pageIndex, 0, tops.Length - 1);
            return (tops[i], bottoms[i]);
        }

        /// <summary>
        /// Место колонтитулов каждого листа по готовой раскладке. Пустые массивы —
        /// колонтитулов в документе нет или они не рисуются.
        /// </summary>
        private (float[] Top, float[] Bottom) ComputeBandReserve(List<ParaLayout> layouts, int pageCount)
        {
            var settings = DocVm?.HeaderFooter;
            var ps = DocVm?.Document.PageSettings;
            if (settings is null || ps is null || settings.IsEmpty || pageCount <= 0 || !HeaderFootersDrawable)
                return (Array.Empty<float>(), Array.Empty<float>());

            var facts = BuildPageFacts(layouts, pageCount);
            var decorations = PageNumbering.Compute(settings, pageCount,
                facts.ParagraphStartPages, facts.ChapterStartPages);

            float headerDistancePt = MmToPt(ps.HeaderDistanceMm);
            float footerDistancePt = MmToPt(ps.FooterDistanceMm);

            float lineHeight;
            using (var font = HeaderFooterPainter.CreateFont(settings, SKTextRenderer.ResolveTypeface,
                       HeaderFooterFallbackFamily()))
            {
                lineHeight = HeaderFooterPainter.LineHeight(font);
            }

            var tops = new float[pageCount];
            var bottoms = new float[pageCount];

            for (int p = 0; p < pageCount && p < decorations.Length; p++)
            {
                var decoration = decorations[p];
                if (!decoration.BandsVisible) continue;

                var (header, footer) = decoration.Variant switch
                {
                    HeaderFooterVariant.First => (settings.FirstHeader, settings.FirstFooter),
                    HeaderFooterVariant.Even => (settings.EvenHeader, settings.EvenFooter),
                    _ => (settings.Header, settings.Footer)
                };

                int headerLines = PrintedLines(header, decoration);
                int footerLines = PrintedLines(footer, decoration);

                // Без зазора, как в Word и как в HeaderFooterPainter.BodyReservePt: пока
                // колонтитул умещается в поле листа, текст стоит там же, где без него.
                tops[p] = headerLines > 0 ? headerDistancePt + lineHeight * headerLines : 0f;
                bottoms[p] = footerLines > 0 ? footerDistancePt + lineHeight * footerLines : 0f;
            }

            return (tops, bottoms);
        }

        /// <summary>
        /// Сколько строк полосы печатается на листе. Место, которое на этом листе
        /// гаснет (номер скрыт, колонтитулы скрыты), места не занимает.
        /// </summary>
        private static int PrintedLines(HeaderFooterBand? band, PageDecoration decoration)
        {
            if (band is null) return 0;

            int lines = 0;
            for (int s = 0; s < 3; s++)
            {
                string template = band.GetSlot(s);
                if (template.Length == 0) continue;
                if (PageNumbering.RenderSlot(template, decoration) is null) continue;

                lines = Math.Max(lines, HeaderFooterPainter.SplitLines(template).Count);
            }

            return lines;
        }

        /// <summary>
        /// Сверяет место колонтитулов, с которым прошла раскладка, с тем, что из неё
        /// вышло, и запоминает новое. True — у какого-то листа место другое, раскладку
        /// надо повторить. Частичный проход видит лишь кусок документа и места не
        /// пересчитывает.
        /// </summary>
        private bool RefreshBandReserveFromPass()
        {
            if (_partialFromBlock >= 0 || !HeaderFootersDrawable) return false;

            var layouts = _passLayouts;
            var pages = _passPages;
            if (layouts is null || pages is null) return false;

            return ApplyBandReserve(layouts, pages.Count);
        }

        /// <summary>
        /// Место колонтитулов текущей раскладки устарело — например, правило скрыло
        /// колонтитулы на листе, а запас документа остался прежним. True — раскладку
        /// надо пересобрать.
        /// </summary>
        private bool BandReserveOutdated()
        {
            if (!HeaderFootersDrawable) return false;

            List<ParaLayout> layouts;
            List<PageRect> pages;
            lock (_renderLock)
            {
                layouts = _layouts;
                pages = _pages;
            }

            if (layouts.Count == 0 || pages.Count == 0) return false;
            return ApplyBandReserve(layouts, pages.Count);
        }

        /// <summary>
        /// Считает место колонтитулов по раскладке и запоминает его. True — хоть у
        /// одного листа место вышло не тем, с которым эта раскладка строилась.
        /// </summary>
        private bool ApplyBandReserve(List<ParaLayout> layouts, int pageCount)
        {
            var (newTops, newBottoms) = ComputeBandReserve(layouts, pageCount);

            // Сравниваются поля, а не запасы: запас, который меньше поля листа, на
            // раскладку не влияет, и повторять её ради него незачем.
            var (_, baseTop, _, baseBottom) = GetPagePaddingPt();

            bool changed = false;
            for (int p = 0; p < pageCount; p++)
            {
                var (usedTop, usedBottom) = BandReserveForPage(p);
                float top = newTops.Length > 0 ? newTops[p] : 0f;
                float bottom = newBottoms.Length > 0 ? newBottoms[p] : 0f;

                if (Math.Abs(Math.Max(baseTop, usedTop) - Math.Max(baseTop, top)) > 0.01f
                    || Math.Abs(Math.Max(baseBottom, usedBottom) - Math.Max(baseBottom, bottom)) > 0.01f)
                {
                    changed = true;
                    break;
                }
            }

            _bandReserveTopByPage = newTops;
            _bandReserveBottomByPage = newBottoms;
            _bandReserveKnown = true;
            return changed;
        }
    }
}
