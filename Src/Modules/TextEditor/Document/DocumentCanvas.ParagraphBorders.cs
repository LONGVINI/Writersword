using System;
using System.Collections.Generic;
using SkiaSharp;
using Writersword.Core.Models.Rendering;
using Writersword.Modules.TextEditor.Rendering;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Рамка абзаца на листе: черта слева у цитаты, линия под заголовком, рамка
    /// вокруг врезки. Рисует её рендер текста, здесь решается только одно —
    /// соединяется ли боковая черта с соседним абзацем.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        // Насколько могут разойтись край соседа и край этого абзаца, чтобы они всё
        // ещё считались стоящими вплотную. Раскладка считает в дробных пунктах, и
        // точного равенства после сложения интервалов ждать нельзя.
        private const float BorderJoinTolerancePt = 0.75f;

        /// <summary>
        /// Рамка одного куска абзаца — под текстом, после подложки оглавления.
        /// </summary>
        private void DrawParagraphBorders(
            SKCanvas canvas, int idx, ParaLayout pl,
            List<ParaLayout> layouts, SKTextLayout renderLayout,
            float absX, float absY)
        {
            // Рисовать нечего: ни рамки, ни заливки.
            bool hasBorders = renderLayout.Borders is not null && !renderLayout.Borders.IsEmpty;
            if (!hasBorders && !renderLayout.HasShading) return;

            int from = Math.Max(pl.LineFrom, 0);
            int to = Math.Min(pl.LineTo, renderLayout.Lines.Count);

            bool isStart = from == 0;
            bool isEnd = to >= renderLayout.Lines.Count;

            float sliceHeight = 0f;
            for (int i = from; i < to; i++)
                sliceHeight += renderLayout.Lines[i].Height;

            bool joinPrev = isStart && JoinsNeighbour(idx - 1, pl, layouts, renderLayout, absY, sliceHeight, above: true);
            bool joinNext = isEnd && JoinsNeighbour(idx + 1, pl, layouts, renderLayout, absY, sliceHeight, above: false);

            // Заливка без рамки сливается с соседом той же заливки — сплошным фоном,
            // без просвета на интервале между абзацами.
            bool hasShading = renderLayout.HasShading;
            bool shadeJoinPrev = hasShading && isStart
                && JoinsNeighbour(idx - 1, pl, layouts, renderLayout, absY, sliceHeight, above: true, shading: true);
            bool shadeJoinNext = hasShading && isEnd
                && JoinsNeighbour(idx + 1, pl, layouts, renderLayout, absY, sliceHeight, above: false, shading: true);

            SKTextRenderer.RenderParagraphBorders(
                canvas, renderLayout,
                absX + renderLayout.LeftIndentPt, absY,
                pl.LineFrom, pl.LineTo,
                joinPrev, joinNext,
                shadeJoinPrev, shadeJoinNext);
        }

        /// <summary>
        /// Стоит ли сосед вплотную и с той же боковой рамкой.
        ///
        /// «Вплотную» проверяется по листу, а не по списку: между двумя абзацами может
        /// стоять картинка или таблица, в списке абзацев их нет, и соседи по списку
        /// выглядели бы соседями. По листу же видно — нижний край соседа вместе с его
        /// интервалом после и нашим интервалом до должен прийтись ровно на наш верх.
        /// Лист тоже должен быть один: черта через межстраничный зазор не тянется.
        /// </summary>
        /// <param name="shading">
        /// false — сливается ли рамка; true — сливается ли заливка абзацев без рамки.
        /// </param>
        private bool JoinsNeighbour(
            int neighbourIdx, ParaLayout pl, List<ParaLayout> layouts,
            SKTextLayout renderLayout, float absY, float sliceHeight, bool above,
            bool shading = false)
        {
            if (neighbourIdx < 0 || neighbourIdx >= layouts.Count) return false;

            var other = layouts[neighbourIdx];
            if (other.PageIndex != pl.PageIndex) return false;

            // Абзац ячейки соединяется только с абзацем той же ячейки, обычный — только
            // с обычным.
            if ((other.Cell is null) != (pl.Cell is null)) return false;
            if (other.Cell is not null && !ReferenceEquals(other.Cell.Cell, pl.Cell!.Cell)) return false;

            if (ReferenceEquals(other.Vm, pl.Vm)) return false;

            // Рамка соседа проверяется по модели прежде, чем строить его раскладку:
            // сосед без рамки — обычный случай, и собирать раскладку ради ответа
            // «нет» незачем.
            if (shading)
            {
                var otherProps = other.Vm.Model?.Properties;
                if (string.IsNullOrWhiteSpace(otherProps?.ShadingColor)
                    && string.IsNullOrWhiteSpace(otherProps?.ShadingPattern))
                    return false;
            }
            else if (other.Vm.Model?.Properties.Borders is not { } otherBorders || otherBorders.IsEmpty)
            {
                return false;
            }

            var otherLayout = GetRenderLayout(other, (float)(_canvasWidth * PxToPt));

            int otherFrom = Math.Max(other.LineFrom, 0);
            int otherTo = Math.Min(other.LineTo, otherLayout.Lines.Count);

            float otherHeight = 0f;
            for (int i = otherFrom; i < otherTo; i++)
                otherHeight += otherLayout.Lines[i].Height;

            if (above)
            {
                // Сосед сверху должен кончаться в этом куске, а не продолжаться дальше.
                if (otherTo < otherLayout.Lines.Count) return false;
                bool sameGroup = shading
                    ? SKTextRenderer.ShadingJoin(otherLayout, renderLayout)
                    : SKTextRenderer.BordersJoin(otherLayout, renderLayout);
                if (!sameGroup) return false;

                float otherBottom = other.Ypt + otherHeight;
                return GapMatches(absY - otherBottom, otherLayout, renderLayout);
            }

            // Сосед снизу должен начинаться в этом куске.
            if (otherFrom > 0) return false;
            bool sameGroupBelow = shading
                ? SKTextRenderer.ShadingJoin(renderLayout, otherLayout)
                : SKTextRenderer.BordersJoin(renderLayout, otherLayout);
            if (!sameGroupBelow) return false;

            float sliceBottom = absY + sliceHeight;
            return GapMatches(other.Ypt - sliceBottom, renderLayout, otherLayout);
        }

        /// <summary>
        /// Промежуток между соседями на листе — это их интервал и ничего больше: между
        /// ними не стоит ни картинка, ни таблица.
        ///
        /// Интервал после верхнего и интервал до нижнего вёрстка не складывает, а берёт
        /// больший из двух, как Word. Прежде проверка ждала их сумму, и соседи с
        /// ненулевыми интервалами с обеих сторон — 6 и 12 пт, 12 и 24 пт — соседями не
        /// считались: сплошная заливка у Word рвалась у нас на отдельные полосы. Сумма
        /// тоже принимается — на случай вёрстки, которая интервалы складывает.
        ///
        /// Место под линии рамки в схлопывании не участвует (см.
        /// <see cref="CollapsibleSpaceAfterPt"/>): ожидаемый промежуток — обе линии
        /// целиком и больший из самих интервалов.
        /// </summary>
        private static bool GapMatches(float gapPt, SKTextLayout upper, SKTextLayout lower)
        {
            float summed = upper.SpaceAfterPt + lower.SpaceBeforePt;
            float collapsed = summed
                - Math.Min(CollapsibleSpaceAfterPt(upper), CollapsibleSpaceBeforePt(lower));

            return Math.Abs(gapPt - collapsed) <= BorderJoinTolerancePt
                || Math.Abs(gapPt - summed) <= BorderJoinTolerancePt;
        }

        /// <summary>
        /// Часть интервала после абзаца, которую может поглотить интервал до следующего:
        /// сам интервал, без места под нижнюю линию рамки.
        ///
        /// Раскладка кладёт место под линию в интервал (так её учитывают разбивка на
        /// листы, каретка и попадание мышью), но схлопывается у Word только интервал.
        /// Линия — часть абзаца: две рамки подряд стоят одна под другой, каждая со своим
        /// зазором. Прежде схлопывание съедало место под линию, и рамка следующего
        /// абзаца ложилась поверх рамки предыдущего.
        /// </summary>
        private static float CollapsibleSpaceAfterPt(SKTextLayout layout)
            => Math.Max(0f, layout.SpaceAfterPt - (layout.Borders?.Bottom?.ExtentPt ?? 0f));

        /// <summary>
        /// Часть интервала до абзаца, которая может слиться с интервалом после
        /// предыдущего: сам интервал, без места под верхнюю линию рамки.
        /// </summary>
        private static float CollapsibleSpaceBeforePt(SKTextLayout layout)
            => Math.Max(0f, layout.SpaceBeforePt - (layout.Borders?.Top?.ExtentPt ?? 0f));
    }
}
