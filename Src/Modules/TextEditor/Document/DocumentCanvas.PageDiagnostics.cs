using System;
using System.Globalization;
using System.Text;
using Writersword.Core.Models.Rendering;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Диагностика вёрстки первых листов: что встало на лист, сколько строк заняло,
    /// какой высоты вышло и сколько места осталось внизу.
    ///
    /// Нужна для сверки с Word. Расхождение «у Word влезает, у нас нет» набирается из
    /// мелочей — лишняя строка в заголовке, другой шрифт у подписи, иной интервал, — и
    /// увидеть его можно только построчно. Пишется после каждого полного прохода, но
    /// только если состав листов изменился: иначе лог забивался бы на каждом кадре.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        /// <summary>Сколько первых листов описывать.</summary>
        private const int DiagnosticPageCount = 2;

        private string? _lastPageDiagnostics;

        private void LogFirstPagesComposition()
        {
            if (DocVm is null) return;

            var pages = _pages;
            var layouts = _layouts;
            if (pages.Count == 0) return;

            var (marginLeft, marginTop, marginRight, marginBottom) = GetPagePaddingPt();
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();

            int pageCount = Math.Min(DiagnosticPageCount, pages.Count);
            for (int pageIdx = 0; pageIdx < pageCount; pageIdx++)
            {
                var page = pages[pageIdx];
                float textTop = page.Ypt + marginTop;
                float textBottom = page.Ypt + page.HeightPt - marginBottom;
                float textWidth = page.WidthPt - marginLeft - marginRight;

                sb.Append(inv, $"\n  Лист {pageIdx + 1}: высота текста {textBottom - textTop:F1} пт, ширина {textWidth:F1} пт");

                float lastBottom = textTop;
                foreach (var pl in layouts)
                {
                    if (pl.PageIndex != pageIdx || pl.Cell is not null) continue;

                    var props = pl.Vm.Model.Properties;
                    var layout = pl.Layout
                        ?? (_layoutCache.TryGetValue(pl.Vm, out var cached) ? cached.Layout : null);

                    int lines = Math.Max(0, pl.LineTo - pl.LineFrom);
                    string font = "?";
                    float firstLineWidth = 0f;

                    if (layout is not null && pl.LineFrom >= 0 && pl.LineFrom < layout.Lines.Count)
                    {
                        var line = layout.Lines[pl.LineFrom];
                        firstLineWidth = line.TextWidth;
                        if (line.Segments.Count > 0)
                        {
                            var seg = line.Segments[0];
                            font = string.Format(inv, "{0} {1:F1}{2}{3}{4}",
                                seg.FontFamily,
                                seg.FontSizePt,
                                seg.IsBold ? " ж" : string.Empty,
                                seg.IsItalic ? " к" : string.Empty,
                                Math.Abs(seg.CharacterSpacingPt) > 0.001f
                                    ? string.Format(inv, " разрядка {0:F2}", seg.CharacterSpacingPt)
                                    : string.Empty);
                        }
                    }

                    string text = pl.Vm.PlainText ?? string.Empty;
                    if (text.Length > 40) text = text.Substring(0, 40) + "…";
                    text = text.Replace('\t', '→');

                    sb.Append(inv,
                        $"\n    y {pl.Ypt - textTop,6:F1}  h {pl.HeightPt,6:F1}  строк {lines,2}  до/после {(props.SpaceBefore ?? 0):F1}/{(props.SpaceAfter ?? 0):F1}  первая строка {firstLineWidth,6:F1}  [{props.StyleName ?? "—"}] {font}  «{text}»");

                    lastBottom = Math.Max(lastBottom, pl.Ypt + pl.HeightPt);
                }

                sb.Append(inv, $"\n    занято {lastBottom - textTop:F1} пт, свободно внизу {textBottom - lastBottom:F1} пт");
            }

            string report = sb.ToString();
            if (string.Equals(report, _lastPageDiagnostics, StringComparison.Ordinal)) return;
            _lastPageDiagnostics = report;

            _logger.Debug("[PAGES] Состав первых листов:{Report}", report);
        }
    }
}
