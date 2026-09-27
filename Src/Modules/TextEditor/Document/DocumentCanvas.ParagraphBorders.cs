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
            if (!hasBorders && string.IsNullOrWhiteSpace(renderLayout.ShadingColor)) return;

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
            bool hasShading = !string.IsNullOrWhiteSpace(renderLayout.ShadingColor);
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
                if (string.IsNullOrWhiteSpace(other.Vm.Model?.Properties.ShadingColor)) return false;
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

                float expectedTop = other.Ypt + otherHeight
                                    + otherLayout.SpaceAfterPt + renderLayout.SpaceBeforePt;

                return Math.Abs(expectedTop - absY) <= BorderJoinTolerancePt;
            }

            // Сосед снизу должен начинаться в этом куске.
            if (otherFrom > 0) return false;
            bool sameGroupBelow = shading
                ? SKTextRenderer.ShadingJoin(renderLayout, otherLayout)
                : SKTextRenderer.BordersJoin(renderLayout, otherLayout);
            if (!sameGroupBelow) return false;

            float expectedNextTop = absY + sliceHeight
                                    + renderLayout.SpaceAfterPt + otherLayout.SpaceBeforePt;

            return Math.Abs(expectedNextTop - other.Ypt) <= BorderJoinTolerancePt;
        }
    }
}
