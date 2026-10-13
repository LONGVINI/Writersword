using System;
using System.Collections.Generic;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Листы в ряд: где на холсте стоит каждый лист. Раскладка ставит листы столбиком,
    /// а в ряд их разносит отрисовка (PageVisualDelta). Листы документа бывают разного
    /// размера — у раздела свой лист (DocumentCanvas.Sections), и альбомный лист шире и
    /// ниже книжного. Поэтому ряд берёт высоту по самому высокому своему листу, листы
    /// в ряду идут друг за другом каждый своей ширины, а ряды — один под другим по
    /// своим высотам. Счёт по ширине и высоте одного листа на всех ставил листы
    /// другого размера внахлёст и оставлял на холсте пустые дыры.
    ///
    /// Ряды набираются по ширине, как в Word: листы встают в ряд, пока помещаются в
    /// ширину холста, и лист, который в оставшееся место не входит, начинает следующий
    /// ряд. Альбомный лист шире книжного примерно в полтора раза и встаёт с тем, кто
    /// рядом с ним помещается, или один. Раньше ряд брал ровно столько листов, сколько
    /// задано, и альбомный лист в нём уходил за край окна.
    ///
    /// Место ряда — ширина холста, а не число листов документа: листы раздела бывают
    /// шире листа документа (A4 в документе на 200 мм), и по листу документа три таких
    /// листа «не помещались», хотя в окне им места хватало. Заданное число листов в
    /// ряду — верхний предел; в авто-режиме предела нет, кроме ширины.
    ///
    /// Лист ниже ряда (альбомный рядом с книжными) стоит по середине ряда по высоте,
    /// как у Word, а не прижат к его верху.
    ///
    /// Сетка считается один раз на список листов, число листов в ряду и ширину
    /// холста и дальше берётся готовой: отрисовка спрашивает место каждого листа
    /// много раз за кадр.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        private sealed class PageGrid
        {
            public required List<PageRect> Pages { get; init; }
            public required int PageCount { get; init; }
            public required int Columns { get; init; }
            public required float CanvasWidthPt { get; init; }
            public required float LaneWidthPt { get; init; }
            public required float BasePageWidthPt { get; init; }
            public required int MaxPerRow { get; init; }

            /// <summary>Первый лист каждого ряда.</summary>
            public required int[] RowFirstPage { get; init; }

            /// <summary>Ряд каждого листа.</summary>
            public required int[] RowOfPage { get; init; }

            /// <summary>Левый верхний угол каждого листа на холсте.</summary>
            public required float[] VisXPt { get; init; }
            public required float[] VisYPt { get; init; }

            /// <summary>Верх и высота каждого ряда.</summary>
            public required float[] RowTopPt { get; init; }
            public required float[] RowHeightPt { get; init; }

            /// <summary>Высота всех рядов вместе с зазорами сверху и снизу.</summary>
            public required float TotalHeightPt { get; init; }
        }

        private PageGrid? _pageGrid;

        /// <summary>
        /// Сетка листов в ряд: ряды сверху вниз, в ряду листы слева направо, каждый ряд
        /// по центру холста.
        /// </summary>
        /// <param name="laneWidthPt">
        /// Полоса карточек правок справа от каждого листа (DocumentCanvas.RevisionCards):
        /// листы в ряду расходятся на её ширину.
        /// </param>
        /// <param name="basePageWidthPt">
        /// Ширина листа документа: ряду отводится не меньше места, чем под заданное число
        /// таких листов, — холст бывает уже, и тогда он прокручивается.
        /// </param>
        /// <param name="maxPerRow">Больше листов в ряду не ставится.</param>
        private static PageGrid BuildPageGrid(
            List<PageRect> pages, int columns, float canvasWidthPt, float laneWidthPt, float basePageWidthPt,
            int maxPerRow)
        {
            int count = pages.Count;
            columns = Math.Max(1, columns);
            maxPerRow = Math.Max(1, maxPerRow);
            float gapX = PageGapPt * 2f + laneWidthPt;

            float baseWidth = basePageWidthPt > 1f ? basePageWidthPt : (count > 0 ? pages[0].WidthPt : 0f);
            float budget = Math.Max(
                columns * baseWidth + (columns - 1) * gapX + laneWidthPt,
                canvasWidthPt - PageGapPt * 2f);
            budget -= laneWidthPt;

            // Ряды по ширине: лист идёт в текущий ряд, пока ряд не полон по числу и
            // лист входит в отведённое ряду место. Первый лист ряда встаёт всегда —
            // даже шире места, иначе он не встал бы никуда.
            var firsts = new List<int>();
            var rowOfPage = new int[count];
            float rowWidth = 0f;
            int inRow = 0;
            for (int i = 0; i < count; i++)
            {
                float w = pages[i].WidthPt;
                bool fits = inRow > 0
                    && inRow < maxPerRow
                    && rowWidth + gapX + w <= budget + 0.5f;

                if (fits)
                {
                    rowWidth += gapX + w;
                    inRow++;
                }
                else
                {
                    firsts.Add(i);
                    rowWidth = w;
                    inRow = 1;
                }

                rowOfPage[i] = firsts.Count - 1;
            }

            int rows = firsts.Count;
            var visX = new float[count];
            var visY = new float[count];
            var rowTop = new float[rows];
            var rowHeight = new float[rows];

            float y = PageGapPt;
            for (int row = 0; row < rows; row++)
            {
                int first = firsts[row];
                int last = row + 1 < rows ? firsts[row + 1] : count;

                float totalWidth = 0f;
                float height = 0f;
                for (int i = first; i < last; i++)
                {
                    totalWidth += pages[i].WidthPt;
                    height = Math.Max(height, pages[i].HeightPt);
                }
                totalWidth += (last - first - 1) * gapX + laneWidthPt;

                // Каждый ряд — по центру по числу листов именно в нём: неполный
                // последний ряд иначе прижимался бы влево.
                float x = Math.Max((canvasWidthPt - totalWidth) / 2f, 0f);
                for (int i = first; i < last; i++)
                {
                    visX[i] = x;
                    visY[i] = y + Math.Max(height - pages[i].HeightPt, 0f) / 2f;
                    x += pages[i].WidthPt + gapX;
                }

                rowTop[row] = y;
                rowHeight[row] = height;
                y += height + PageGapPt;
            }

            return new PageGrid
            {
                Pages = pages,
                PageCount = count,
                Columns = columns,
                CanvasWidthPt = canvasWidthPt,
                LaneWidthPt = laneWidthPt,
                BasePageWidthPt = basePageWidthPt,
                MaxPerRow = maxPerRow,
                RowFirstPage = firsts.ToArray(),
                RowOfPage = rowOfPage,
                VisXPt = visX,
                VisYPt = visY,
                RowTopPt = rowTop,
                RowHeightPt = rowHeight,
                TotalHeightPt = rows == 0 ? 0f : y
            };
        }

        /// <summary>
        /// Предел листов в ряду: заданное число, а в авто-режиме — сколько поместится по
        /// ширине (узкие листы раздела встают в ряд и сверх числа листов документа).
        /// </summary>
        private int MaxPagesPerRow => _pagesPerRowSetting > 0 ? _pagesPerRow : 12;

        /// <summary>Сетка листов в ряд для этого списка листов — готовая или новая.</summary>
        private PageGrid GetPageGrid(List<PageRect> pages)
        {
            float canvasWidthPt = (float)(_canvasWidth * PxToPt);
            float basePageWidthPt = GetPageWidthPt();
            int maxPerRow = MaxPagesPerRow;
            var grid = _pageGrid;

            if (grid is not null
                && ReferenceEquals(grid.Pages, pages)
                && grid.PageCount == pages.Count
                && grid.Columns == _pagesPerRow
                && Math.Abs(grid.CanvasWidthPt - canvasWidthPt) < 0.01f
                && Math.Abs(grid.LaneWidthPt - RevisionLaneWidthPtNow) < 0.01f
                && Math.Abs(grid.BasePageWidthPt - basePageWidthPt) < 0.01f
                && grid.MaxPerRow == maxPerRow)
                return grid;

            grid = BuildPageGrid(pages, _pagesPerRow, canvasWidthPt, RevisionLaneWidthPtNow, basePageWidthPt, maxPerRow);
            _pageGrid = grid;
            return grid;
        }

        /// <summary>
        /// Верх ряда, в котором стоит лист: по нему листы одного ряда узнают друг друга.
        /// Свой верх у листа ряда бывает ниже — лист ниже ряда стоит по его середине.
        /// Без листов в ряд — верх самого листа.
        /// </summary>
        private float VisualRowTopPt(int pageIdx, List<PageRect> pages, float pageTopPt)
        {
            if (_pagesPerRow <= 1 || SpreadMode || ReadingRibbon) return pageTopPt;
            if (pageIdx < 0 || pageIdx >= pages.Count) return pageTopPt;

            var grid = GetPageGrid(pages);
            if (pageIdx >= grid.RowOfPage.Length) return pageTopPt;
            return grid.RowTopPt[grid.RowOfPage[pageIdx]];
        }

        /// <summary>Ряды листов в ряд, которые задевает полоса холста [topPt, bottomPt].</summary>
        private (int FirstPage, int LastPage) VisiblePagesInGrid(List<PageRect> pages, float topPt, float bottomPt)
        {
            var grid = GetPageGrid(pages);
            int rows = grid.RowTopPt.Length;
            if (rows == 0) return (0, -1);

            int firstRow = rows - 1;
            for (int row = 0; row < rows; row++)
            {
                if (grid.RowTopPt[row] + grid.RowHeightPt[row] + PageGapPt >= topPt)
                {
                    firstRow = row;
                    break;
                }
            }

            int lastRow = firstRow;
            for (int row = firstRow; row < rows; row++)
            {
                lastRow = row;
                if (grid.RowTopPt[row] > bottomPt) break;
            }

            int lastPage = lastRow + 1 < rows ? grid.RowFirstPage[lastRow + 1] - 1 : pages.Count - 1;
            return (grid.RowFirstPage[firstRow], Math.Min(pages.Count - 1, lastPage));
        }
    }
}
