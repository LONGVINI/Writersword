using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using SkiaSharp;
using Writersword.Core.Models.Print;
using Writersword.Core.Models.Rendering;
using Writersword.Modules.TextEditor.Models.Document;

using RenderAlignment = Writersword.Core.Models.Rendering.TextAlignment;

namespace Writersword.Infrastructure.Rendering
{
    /// <summary>
    /// Единый движок вёрстки и рендеринга текста через SkiaSharp.
    /// Используется и DocumentCanvas (экран) и TextEditorPrintDocument (PDF).
    /// Один движок — одинаковый результат везде — точное совпадение переносов.
    /// Stateless — создаётся через new() без DI.
    /// </summary>
    public sealed class SKTextRenderer
    {
        // Кеш объектов SKTypeface по ключу (гарнитура, жирный, курсив).
        // Создание SKTypeface дорогое — запрашивает шрифт у системы.
        // Один документ обычно использует 2-5 шрифтов — кеш живёт всё время работы.
        // ConcurrentDictionary — потокобезопасен для чтения из фонового потока статистики.
        private static readonly ConcurrentDictionary<(string Family, bool Bold, bool Italic), SKTypeface>
            _typefaceCache = new();

        // ── Публичный API ─────────────────────────────────────────────────

        /// <summary>
        /// Строит вёрстку одного параграфа.
        /// Вызывается DocumentCanvas для каждого параграфа при изменении текста или ширины.
        /// </summary>
        /// <param name="para">Блок параграфа из модели документа.</param>
        /// <param name="availableWidthPt">Ширина текстовой области в pt.</param>
        /// <param name="styles">Резолвер стилей документа.</param>
        public SKTextLayout BuildLayout(
            ParagraphBlock para,
            float availableWidthPt,
            StyleResolver styles)
        {
            string? styleName = para.Properties.StyleName;

            float leftIndentPt = (float)(para.Properties.LeftIndent
                                        ?? styles.ResolveLeftIndent(styleName));
            float rightIndentPt = (float)(para.Properties.RightIndent
                                        ?? styles.ResolveRightIndent(styleName));
            float firstLineIndentPt = (float)(para.Properties.FirstLineIndent ?? 0.0);
            float spaceBeforePt = (float)(para.Properties.SpaceBefore
                                        ?? styles.ResolveSpaceBefore(styleName));
            float spaceAfterPt = (float)(para.Properties.SpaceAfter
                                        ?? styles.ResolveSpaceAfter(styleName));
            float lineSpacing = para.Properties.LineSpacingValue.HasValue
                                        ? (float)para.Properties.LineSpacingValue.Value
                                        : styles.ResolveLineSpacing(styleName);

            // Конвертируем TextAlignment из модели в Core enum через int.
            // Значения намеренно совпадают: Left=0, Center=1, Right=2, Justify=3.
            RenderAlignment alignment = para.Properties.Alignment.HasValue
                ? (RenderAlignment)(int)para.Properties.Alignment.Value
                : styles.ResolveAlignment(styleName);

            float textWidthPt = Math.Max(availableWidthPt - leftIndentPt - rightIndentPt, 1f);

            var layout = new SKTextLayout
            {
                SpaceBeforePt = spaceBeforePt,
                SpaceAfterPt = spaceAfterPt,
                LeftIndentPt = leftIndentPt,
                RightIndentPt = rightIndentPt,
                FirstLineIndentPt = firstLineIndentPt,
                Alignment = alignment
            };

            var tokens = CollectTokens(para, styleName, styles);
            WrapTokensToLines(tokens, layout, textWidthPt, lineSpacing);
            layout.TextLength = GetPlainTextLength(para);

            return layout;
        }

        /// <summary>
        /// Строит вёрстку таблицы.
        /// Вычисляет ширины колонок, верстает содержимое каждой ячейки,
        /// определяет высоту строк по самой высокой ячейке.
        /// Вызывается DocumentCanvas при изменении таблицы или ширины канваса.
        /// </summary>
        /// <param name="table">Блок таблицы из модели документа.</param>
        /// <param name="textAreaWidthPt">Ширина текстовой области в pt.</param>
        /// <param name="styles">Резолвер стилей документа.</param>
        public SKTableLayout BuildTableLayout(
            TableBlock table,
            float textAreaWidthPt,
            StyleResolver styles)
        {
            int colCount = table.ColumnCount;
            int rowCount = table.RowCount;

            // Реальная ширина таблицы = сумма фиксированных ширин колонок.
            // Auto-колонки (новая таблица) распределяются равномерно по доступной ширине.
            // После первого drag все колонки становятся Fixed и tableWidthPt = их сумма.
            // LeftIndentPt только позиционирует таблицу — не ограничивает ширину.
            // За правый край страницы выходить можно — рендер обрежет по клипу страницы.
            var colWidthsPt = ComputeColumnWidths(table, textAreaWidthPt, colCount);
            float tableWidthPt = 0f;
            foreach (var w in colWidthsPt) tableWidthPt += w;

            // Накапливаем X-смещения колонок.
            var colOffsetsPt = new List<float>(colCount);
            float xOff = 0f;
            foreach (var w in colWidthsPt)
            {
                colOffsetsPt.Add(xOff);
                xOff += w;
            }

            var tableLayout = new SKTableLayout
            {
                RowCount = rowCount,
                ColumnCount = colCount,
                TotalWidthPt = tableWidthPt
            };
            tableLayout.ColumnWidthsPt.AddRange(colWidthsPt);
            tableLayout.ColumnOffsetsPt.AddRange(colOffsetsPt);

            float tableY = 0f;

            for (int row = 0; row < rowCount; row++)
            {
                var rowLayout = new SKTableRowLayout { Row = row, Ypt = tableY };
                float rowHeight = 0f;

                for (int col = 0; col < colCount; col++)
                {
                    var cell = table.GetCell(row, col);

                    // Пропускаем ячейки которые являются частью объединения
                    // но не являются главной ячейкой.
                    if (cell is null || (cell.Row != row || cell.Column != col))
                        continue;

                    // Ширина ячейки с учётом ColSpan.
                    float cellWidthPt = 0f;
                    for (int c = col; c < col + cell.ColSpan && c < colCount; c++)
                        cellWidthPt += colWidthsPt[c];

                    float padTopPt = (float)cell.PaddingTopPt;
                    float padBottomPt = (float)cell.PaddingBottomPt;
                    float padLeftPt = (float)cell.PaddingLeftPt;
                    float padRightPt = (float)cell.PaddingRightPt;

                    float leftBorderW = cell.Borders.Left != BorderStyle.None ? (float)cell.Borders.ThicknessPt : 0f;
                    float rightBorderW = cell.Borders.Right != BorderStyle.None ? (float)cell.Borders.ThicknessPt : 0f;
                    float contentWidthPt = Math.Max(
                        cellWidthPt - padLeftPt - padRightPt - leftBorderW - rightBorderW,
                        1f);

                    var cellLayout = new SKTableCellLayout
                    {
                        Row = row,
                        Column = col,
                        RowSpan = cell.RowSpan,
                        ColSpan = cell.ColSpan,
                        Xpt = colOffsetsPt[col],
                        Ypt = tableY,
                        WidthPt = cellWidthPt,
                        PadTopPt = padTopPt,
                        PadBottomPt = padBottomPt,
                        PadLeftPt = padLeftPt,
                        PadRightPt = padRightPt,
                        BackgroundColor = cell.BackgroundColor,
                        VerticalAlignment = (int)cell.VerticalAlignment,
                        Borders = BuildCellBorderLayout(cell.Borders)
                    };

                    // Верстаем параграфы ячейки.
                    float cellContentY = 0f;
                    for (int pi = 0; pi < cell.Paragraphs.Count; pi++)
                    {
                        var para = cell.Paragraphs[pi];
                        var paraLayout = BuildLayout(para, contentWidthPt, styles);

                        cellLayout.Paragraphs.Add(new SKTableParaLayout
                        {
                            Layout = paraLayout,
                            Ypt = cellContentY,
                            ParagraphIndex = pi
                        });

                        cellContentY += paraLayout.SpaceBeforePt
                                      + paraLayout.TotalHeightPt
                                      + paraLayout.SpaceAfterPt;
                    }

                    float topBorderW = cell.Borders.Top != BorderStyle.None ? (float)cell.Borders.ThicknessPt : 0f;
                    float botBorderW = cell.Borders.Bottom != BorderStyle.None ? (float)cell.Borders.ThicknessPt : 0f;
                    cellLayout.ContentHeightPt = cellContentY;
                    cellLayout.HeightPt = cellContentY + padTopPt + padBottomPt + topBorderW + botBorderW;

                    // Высота строки определяется самой высокой ячейкой без RowSpan.
                    if (cell.RowSpan == 1 && cellLayout.HeightPt > rowHeight)
                        rowHeight = cellLayout.HeightPt;

                    rowLayout.Cells.Add(cellLayout);
                }

                // Минимальная высота строки — высота пустой строки.
                if (rowHeight < 14f) rowHeight = 14f;

                rowLayout.HeightPt = rowHeight;

                // Проставляем финальную высоту всем ячейкам строки
                // (без RowSpan — для ячеек с RowSpan высота будет пересчитана позже).
                foreach (var cellLayout in rowLayout.Cells)
                    if (cellLayout.RowSpan == 1)
                        cellLayout.HeightPt = rowHeight;

                tableLayout.Rows.Add(rowLayout);
                tableY += rowHeight;
            }

            // Пересчёт высот для объединённых ячеек (RowSpan > 1).
            foreach (var rowLayout in tableLayout.Rows)
            {
                foreach (var cellLayout in rowLayout.Cells)
                {
                    if (cellLayout.RowSpan <= 1) continue;

                    float totalH = 0f;
                    for (int r = cellLayout.Row;
                         r < cellLayout.Row + cellLayout.RowSpan
                         && r < tableLayout.Rows.Count; r++)
                        totalH += tableLayout.Rows[r].HeightPt;

                    cellLayout.HeightPt = totalH;
                }
            }

            tableLayout.TotalHeightPt = tableY;
            return tableLayout;
        }

        /// <summary>
        /// Рендерит таблицу на SKCanvas.
        /// tableX/tableY — позиция верхнего левого угла таблицы в pt.
        /// Рисует фон ячеек, границы и содержимое параграфов.
        /// </summary>
        public static void RenderTable(
            SKCanvas canvas,
            SKTableLayout tableLayout,
            float tableX,
            float tableY,
            float canvasScale = 1f)
        {
            // Извлекаем реальный масштаб из матрицы канваса (ScaleX = DPI/72 * zoom).
            // Это даёт правильный px-размер для pixel-snapping на любом DPI и зуме.
            var m = canvas.TotalMatrix;
            float actualScale = MathF.Sqrt(m.ScaleX * m.ScaleX + m.SkewY * m.SkewY);
            if (actualScale > 0.01f) canvasScale = actualScale;
            foreach (var row in tableLayout.Rows)
            {
                foreach (var cell in row.Cells)
                {
                    float cellX = tableX + cell.Xpt;
                    float cellY = tableY + cell.Ypt;

                    // Фон ячейки.
                    if (!string.IsNullOrEmpty(cell.BackgroundColor)
                        && SKColor.TryParse(cell.BackgroundColor, out var bgColor))
                    {
                        using var bgPaint = new SKPaint { Color = bgColor };
                        canvas.DrawRect(cellX, cellY, cell.WidthPt, cell.HeightPt, bgPaint);
                    }

                    // Границы ячейки.
                    RenderCellBorders(canvas, cell, cellX, cellY, cell.HeightPt, canvasScale);

                    // Содержимое — параграфы.
                    float contentX = cellX + cell.PadLeftPt + cell.Borders.Left.WidthPt;
                    float contentAreaH = cell.HeightPt - cell.PadTopPt - cell.PadBottomPt
                                       - cell.Borders.Top.WidthPt - cell.Borders.Bottom.WidthPt;

                    // Вертикальное выравнивание содержимого.
                    float contentOffsetY = cell.VerticalAlignment switch
                    {
                        1 => (contentAreaH - cell.ContentHeightPt) / 2f, // Middle
                        2 => contentAreaH - cell.ContentHeightPt,         // Bottom
                        _ => 0f                                            // Top
                    };
                    contentOffsetY = Math.Max(0f, contentOffsetY);

                    float contentY = cellY + cell.PadTopPt
                                   + cell.Borders.Top.WidthPt
                                   + contentOffsetY;

                    // Обрезаем рендеринг по границам ячейки — без этого длинный текст
                    // вылезает за границы ячейки и перекрывает соседние.
                    float clipX = cellX + cell.Borders.Left.WidthPt;
                    float clipY = cellY + cell.Borders.Top.WidthPt;
                    float clipW = cell.WidthPt - cell.Borders.Left.WidthPt - cell.Borders.Right.WidthPt;
                    float clipH = cell.HeightPt - cell.Borders.Top.WidthPt - cell.Borders.Bottom.WidthPt;

                    canvas.Save();
                    canvas.ClipRect(new SKRect(clipX, clipY, clipX + clipW, clipY + clipH));

                    foreach (var paraLayout in cell.Paragraphs)
                    {
                        float paraY = contentY + paraLayout.Ypt
                                    + paraLayout.Layout.SpaceBeforePt;

                        RenderParagraphLines(
                            canvas,
                            paraLayout.Layout,
                            contentX + paraLayout.Layout.LeftIndentPt,
                            paraY,
                            0,
                            paraLayout.Layout.Lines.Count);
                    }

                    canvas.Restore();
                }
            }
        }

        /// <summary>
        /// Строит вёрстку всего документа — разбивает параграфы по страницам построчно.
        /// Один параграф может давать несколько SKPageParagraph если он пересекает границу страниц.
        /// Вызывается TextEditorPrintDocument и DocumentCanvas в Page mode.
        /// </summary>
        public SKPageLayout BuildPageLayout(
            DocumentModel document,
            PrintPageSettings pageSettings,
            StyleResolver styles)
        {
            float pageWidthPt = MmToPt(pageSettings.GetPhysicalWidthMm());
            float pageHeightPt = MmToPt(pageSettings.GetPhysicalHeightMm());
            float marginLeftPt = MmToPt(pageSettings.MarginLeftMm + pageSettings.MarginGutterMm);
            float marginTopPt = MmToPt(pageSettings.MarginTopMm);
            float textWidthPt = MmToPt(pageSettings.GetTextWidthMm());
            float textHeightPt = MmToPt(pageSettings.GetTextHeightMm());

            var pageLayout = new SKPageLayout();
            var currentPage = CreatePage(pageWidthPt, pageHeightPt,
                                         marginLeftPt, marginTopPt,
                                         textWidthPt, textHeightPt);
            float currentY = 0f;
            int paraIndex = 0;

            foreach (var section in document.Sections)
            {
                foreach (var block in section.Blocks)
                {
                    if (block is BreakBlock bb && bb.BreakType == BreakType.Page)
                    {
                        pageLayout.Pages.Add(currentPage);
                        currentPage = CreatePage(pageWidthPt, pageHeightPt,
                                                 marginLeftPt, marginTopPt,
                                                 textWidthPt, textHeightPt);
                        currentY = 0f;
                        continue;
                    }

                    // ── Таблица: разбивка по страницам ───────────────────
                    if (block is TableBlock tableBlock)
                    {
                        var tableLayout = BuildTableLayout(tableBlock, textWidthPt, styles);
                        float leftIndentPt = (float)tableBlock.LeftIndentPt;
                        bool repeatHeader = tableBlock.RepeatHeader && tableLayout.Rows.Count > 0;
                        bool byCell = tableBlock.SplitMode == TableSplitMode.ByCell;
                        string? breakLabel = tableBlock.BreakLabel;
                        string? contLabel = tableBlock.ContinuationLabel;

                        float headerH = repeatHeader ? tableLayout.Rows[0].HeightPt : 0f;
                        const float LabelLinePt = 14f;
                        float breakLabelH = string.IsNullOrEmpty(breakLabel) ? 0f : LabelLinePt;
                        float contLabelH = string.IsNullOrEmpty(contLabel) ? 0f : LabelLinePt;

                        int rowFrom = 0;
                        float tableSliceStartY = currentY;
                        bool isFirstSlice = true;
                        // Смещение откуда начинать рисовать первую строку текущего слайса.
                        // Ненулевое только если предыдущий слайс разорвал строку ByCell.
                        float sliceFirstRowOffset = 0f;
                        // Фиксирует sliceFirstRowOffset на момент начала слайса.
                        // В отличие от sliceFirstRowOffset не сбрасывается else-веткой при обработке
                        // первой строки слайса — используется в FirstRowContentOffsetPt при закрытии слайса.
                        float sliceStartOffset = 0f;

                        for (int ri = 0; ri < tableLayout.Rows.Count; ri++)
                        {
                            var row = tableLayout.Rows[ri];

                            // Для первой строки слайса её "эффективная" высота уменьшена
                            // на ту часть которая уже была показана на предыдущей странице.
                            float effectiveH = row.HeightPt - sliceFirstRowOffset;

                            if (repeatHeader && ri == 0 && !isFirstSlice) continue;

                            float reservedH = (!isFirstSlice && repeatHeader) ? headerH : 0f;
                            reservedH += !isFirstSlice ? contLabelH : 0f;
                            float afterH = (ri == tableLayout.Rows.Count - 1) ? 0f : breakLabelH;
                            float available = textHeightPt - currentY - reservedH - afterH;

                            if (effectiveH > available && currentY > 0)
                            {
                                if (byCell && available > 5f)
                                {
                                    // ByCell: разрываем строку ri — показываем доступную высоту
                                    float visibleH = available;
                                    // На следующей странице: пропускаем уже показанный контент
                                    float nextOffset = sliceFirstRowOffset + visibleH;

                                    currentPage.Tables.Add(new SKPageTable
                                    {
                                        Layout = tableLayout,
                                        Y = tableSliceStartY,
                                        LeftIndentPt = leftIndentPt,
                                        RowFrom = rowFrom,
                                        RowTo = ri + 1,
                                        HeaderRowIndex = isFirstSlice ? -1 : (repeatHeader ? 0 : -1),
                                        HeaderRowHeightPt = isFirstSlice ? 0f : headerH,
                                        LastRowVisibleHeightPt = visibleH,
                                        LastRowContentOffsetPt = sliceFirstRowOffset,
                                        BreakLabel = breakLabel,
                                        ContinuationLabel = isFirstSlice ? null : contLabel,
                                        IsContinuation = !isFirstSlice,
                                        FirstRowContentOffsetPt = sliceFirstRowOffset
                                    });

                                    pageLayout.Pages.Add(currentPage);
                                    currentPage = CreatePage(pageWidthPt, pageHeightPt, marginLeftPt, marginTopPt, textWidthPt, textHeightPt);
                                    currentY = contLabelH + (repeatHeader ? headerH : 0f);
                                    tableSliceStartY = 0f;
                                    rowFrom = ri;
                                    sliceFirstRowOffset = nextOffset;
                                    sliceStartOffset = nextOffset;
                                    isFirstSlice = false;
                                    // Не делаем continue: ri остаётся тем же — переходим на следующую страницу
                                    // и повторяем проверку для этой же строки с новым offset.
                                    // Это позволяет корректно обрабатывать строки больше двух страниц.
                                    ri--;
                                    continue;
                                }
                                else
                                {
                                    // ByRow: строка ri целиком уходит на следующую страницу.
                                    // Либо ByCell но места < 5pt — тоже переносим целиком.
                                    if (ri > rowFrom)
                                    {
                                        currentPage.Tables.Add(new SKPageTable
                                        {
                                            Layout = tableLayout,
                                            Y = tableSliceStartY,
                                            LeftIndentPt = leftIndentPt,
                                            RowFrom = rowFrom,
                                            RowTo = ri,
                                            HeaderRowIndex = isFirstSlice ? -1 : (repeatHeader ? 0 : -1),
                                            HeaderRowHeightPt = isFirstSlice ? 0f : headerH,
                                            LastRowVisibleHeightPt = -1f,
                                            BreakLabel = breakLabel,
                                            ContinuationLabel = isFirstSlice ? null : contLabel,
                                            IsContinuation = !isFirstSlice,
                                            FirstRowContentOffsetPt = sliceStartOffset
                                        });
                                    }
                                    pageLayout.Pages.Add(currentPage);
                                    currentPage = CreatePage(pageWidthPt, pageHeightPt, marginLeftPt, marginTopPt, textWidthPt, textHeightPt);
                                    currentY = contLabelH + (repeatHeader ? headerH : 0f);
                                    tableSliceStartY = 0f;
                                    rowFrom = ri;
                                    sliceFirstRowOffset = 0f;
                                    sliceStartOffset = 0f;
                                    isFirstSlice = false;
                                }
                            }
                            else
                            {
                                // Строка помещается: сбрасываем offset — следующая строка рисуется с начала
                                sliceFirstRowOffset = 0f;
                            }

                            currentY += effectiveH;
                        }

                        // Финальный слайс
                        if (rowFrom < tableLayout.Rows.Count)
                        {
                            currentPage.Tables.Add(new SKPageTable
                            {
                                Layout = tableLayout,
                                Y = tableSliceStartY,
                                LeftIndentPt = leftIndentPt,
                                RowFrom = rowFrom,
                                RowTo = -1,
                                HeaderRowIndex = isFirstSlice ? -1 : (repeatHeader ? 0 : -1),
                                HeaderRowHeightPt = isFirstSlice ? 0f : headerH,
                                LastRowVisibleHeightPt = -1f,
                                BreakLabel = null,
                                ContinuationLabel = isFirstSlice ? null : contLabel,
                                IsContinuation = !isFirstSlice,
                                FirstRowContentOffsetPt = sliceStartOffset
                            });
                        }

                        paraIndex++;
                        continue;
                    }


                    if (block is not ParagraphBlock para)
                    {
                        paraIndex++;
                        continue;
                    }

                    var layout = BuildLayout(para, textWidthPt, styles);

                    if (layout.Lines.Count == 0)
                    {
                        paraIndex++;
                        continue;
                    }

                    // SpaceBefore добавляем только перед первым слайсом параграфа.
                    currentY += layout.SpaceBeforePt;

                    int lineFrom = 0;
                    float sliceStartY = currentY;

                    for (int li = 0; li < layout.Lines.Count; li++)
                    {
                        var line = layout.Lines[li];
                        bool isLastLine = li == layout.Lines.Count - 1;

                        // Если строка не влезает и на текущей странице уже есть контент —
                        // закрываем слайс, добавляем страницу, продолжаем тот же параграф.
                        if (currentY + line.Height > textHeightPt
                            && (currentPage.Paragraphs.Count > 0 || li > lineFrom))
                        {
                            // Записываем накопленный слайс если в нём есть строки.
                            if (li > lineFrom)
                            {
                                currentPage.Paragraphs.Add(new SKPageParagraph
                                {
                                    Layout = layout,
                                    Y = sliceStartY,
                                    LineFrom = lineFrom,
                                    LineTo = li,
                                    ParagraphIndex = paraIndex
                                });
                            }

                            pageLayout.Pages.Add(currentPage);
                            currentPage = CreatePage(pageWidthPt, pageHeightPt,
                                                     marginLeftPt, marginTopPt,
                                                     textWidthPt, textHeightPt);
                            currentY = 0f;
                            lineFrom = li;
                            sliceStartY = currentY;
                        }

                        currentY += line.Height;

                        // SpaceAfter добавляем только после последней строки параграфа.
                        if (isLastLine)
                            currentY += layout.SpaceAfterPt;
                    }

                    // Финальный слайс параграфа (остаток или весь параграф).
                    currentPage.Paragraphs.Add(new SKPageParagraph
                    {
                        Layout = layout,
                        Y = sliceStartY,
                        LineFrom = lineFrom,
                        LineTo = layout.Lines.Count,
                        ParagraphIndex = paraIndex
                    });

                    paraIndex++;
                }
            }

            if (currentPage.Paragraphs.Count > 0 || currentPage.Tables.Count > 0 || pageLayout.Pages.Count == 0)
                pageLayout.Pages.Add(currentPage);

            return pageLayout;
        }

        /// <summary>
        /// Рендерит одну страницу на SKCanvas.
        /// </summary>
        public static void RenderPage(
            SKCanvas canvas,
            SKPageContent page,
            SKColor selectionColor,
            int? selectionParaIndex = null,
            int selectionFrom = 0,
            int selectionTo = 0,
            int? caretParaIndex = null,
            int caretCharIndex = 0,
            bool drawCaret = false)
        {
            canvas.Clear(SKColors.White);

            foreach (var para in page.Paragraphs)
            {
                float paraX = page.MarginLeftPt + para.Layout.LeftIndentPt;
                float paraY = page.MarginTopPt + para.Y;

                if (selectionParaIndex == para.ParagraphIndex && selectionFrom < selectionTo)
                {
                    var rects = para.Layout.HitTestRange(selectionFrom, selectionTo);

                    float yBase = para.LineFrom < para.Layout.Lines.Count
                        ? para.Layout.Lines[para.LineFrom].Y : 0f;

                    using var selPaint = new SKPaint { Color = selectionColor };
                    foreach (var r in rects)
                    {
                        if (r.LineIndex < para.LineFrom || r.LineIndex >= para.LineTo) continue;
                        canvas.DrawRect(
                            r.Rect.Left + page.MarginLeftPt,
                            r.Rect.Top - yBase + paraY,
                            r.Rect.Width,
                            r.Rect.Height,
                            selPaint);
                    }
                }

                RenderParagraphLines(canvas, para.Layout, paraX, paraY,
                    para.LineFrom, para.LineTo);

                if (drawCaret && caretParaIndex == para.ParagraphIndex)
                {
                    float yBase = para.LineFrom < para.Layout.Lines.Count
                        ? para.Layout.Lines[para.LineFrom].Y : 0f;

                    var caret = para.Layout.HitTestPosition(caretCharIndex);
                    using var caretPaint = new SKPaint
                    {
                        Color = SKColors.Black,
                        StrokeWidth = 1.5f,
                        IsAntialias = false
                    };
                    float cx = page.MarginLeftPt + caret.X;
                    float cy = paraY + (caret.Y - yBase);
                    canvas.DrawLine(cx, cy, cx, cy + caret.Height, caretPaint);
                }
            }

            // Рендерим таблицы страницы (каждая может быть слайсом строк).
            foreach (var pageTable in page.Tables)
            {
                var layout = pageTable.Layout;
                float tableX = page.MarginLeftPt + pageTable.LeftIndentPt;
                float tableBaseY = page.MarginTopPt + pageTable.Y;
                int rowFrom = pageTable.RowFrom;
                int rowTo = pageTable.RowTo < 0 ? layout.Rows.Count : pageTable.RowTo;
                float rowOffsetY = rowFrom > 0 && rowFrom < layout.Rows.Count
                    ? layout.Rows[rowFrom].Ypt : 0f;
                const float canvasScale = 1f;

                // Метка продолжения над таблицей
                if (!string.IsNullOrEmpty(pageTable.ContinuationLabel))
                {
                    using var lblPaint = new SKPaint { Color = SKColors.Gray, IsAntialias = true };
                    var tf = GetOrCreateTypeface("Arial", false, true);
                    using var font = new SKFont(tf, 9f);
                    canvas.DrawText(pageTable.ContinuationLabel, tableX, tableBaseY - 2f, font, lblPaint);
                }

                // Заголовок (строка 0) рисуется первой на каждой не-первой странице
                if (pageTable.HeaderRowIndex >= 0 && pageTable.HeaderRowIndex < layout.Rows.Count)
                {
                    var headerRow = layout.Rows[pageTable.HeaderRowIndex];
                    foreach (var cell in headerRow.Cells)
                    {
                        float cellX = tableX + cell.Xpt;
                        float cellY = tableBaseY;
                        if (!string.IsNullOrEmpty(cell.BackgroundColor)
                            && SKColor.TryParse(cell.BackgroundColor, out var bg2))
                        { using var bp = new SKPaint { Color = bg2 }; canvas.DrawRect(cellX, cellY, cell.WidthPt, cell.HeightPt, bp); }
                        RenderCellBorders(canvas, cell, cellX, cellY, cell.HeightPt, canvasScale);
                        float cx2 = cellX + cell.PadLeftPt + cell.Borders.Left.WidthPt;
                        float cy2 = cellY + cell.PadTopPt + cell.Borders.Top.WidthPt;
                        canvas.Save();
                        canvas.ClipRect(new SKRect(cellX + cell.Borders.Left.WidthPt, cellY + cell.Borders.Top.WidthPt,
                            cellX + cell.WidthPt - cell.Borders.Right.WidthPt, cellY + cell.HeightPt - cell.Borders.Bottom.WidthPt));
                        foreach (var p in cell.Paragraphs)
                            RenderParagraphLines(canvas, p.Layout, cx2 + p.Layout.LeftIndentPt, cy2 + p.Ypt, 0, p.Layout.Lines.Count);
                        canvas.Restore();
                    }
                }

                float headerOffset = pageTable.HeaderRowHeightPt;

                // Последняя строка слайса может быть видна частично (ByCell).
                bool hasLastRowClip = pageTable.LastRowVisibleHeightPt >= 0f;
                // Первая строка слайса-продолжения начинает рисоваться со смещения.
                bool hasFirstRowOffset = pageTable.IsContinuation && pageTable.FirstRowContentOffsetPt > 0f;

                foreach (var row in layout.Rows)
                {
                    if (row.Row < rowFrom || row.Row >= rowTo) continue;

                    bool isLastRow = (row.Row == rowTo - 1);
                    bool isFirstRow = (row.Row == rowFrom);

                    // Видимая высота ячейки для этой строки.
                    // Для первой строки продолжения — высота уменьшена сверху на firstRowShift.
                    // Для последней строки слайса с ByCell-разрывом — ограничиваем снизу.
                    // lastRowVisibleHeightPt выражен как высота видимого окна на данной странице
                    // и имеет приоритет над вычислением из firstRowShift.
                    float visibleRowH = row.HeightPt;
                    float firstRowShift = 0f;

                    if (isFirstRow && hasFirstRowOffset)
                    {
                        firstRowShift = pageTable.FirstRowContentOffsetPt;
                        visibleRowH = row.HeightPt - firstRowShift;
                    }

                    if (isLastRow && hasLastRowClip)
                        visibleRowH = pageTable.LastRowVisibleHeightPt;

                    foreach (var cell in row.Cells)
                    {
                        float cellX = tableX + cell.Xpt;
                        // cellY — верхний край прямоугольника ячейки на странице.
                        // Для первой строки продолжения сдвигаем вверх на firstRowShift,
                        // чтобы обрезанная часть "уехала" за верхний край страницы.
                        float cellY = tableBaseY + headerOffset + cell.Ypt - rowOffsetY - firstRowShift;

                        // Фон рисуем только в пределах видимой части.
                        if (!string.IsNullOrEmpty(cell.BackgroundColor)
                            && SKColor.TryParse(cell.BackgroundColor, out var bgColor))
                        {
                            using var bgPaint = new SKPaint { Color = bgColor };
                            canvas.DrawRect(cellX, cellY + firstRowShift, cell.WidthPt, visibleRowH, bgPaint);
                        }

                        // Для последней строки слайса не рисуем нижнюю границу
                        // (она продолжится на следующей странице).
                        bool suppressBottom = isLastRow && hasLastRowClip;
                        // Видимый верхний край ячейки (для первой строки продолжения cellY сдвинут вверх).
                        float visibleCellY = cellY + firstRowShift;
                        RenderCellBorders(canvas, cell, cellX, visibleCellY, visibleRowH, canvasScale, false, suppressBottom);

                        float contentX = cellX + cell.PadLeftPt + cell.Borders.Left.WidthPt;
                        float contentY = cellY + cell.PadTopPt + cell.Borders.Top.WidthPt;

                        // Клип-регион: ограничиваем видимой частью строки.
                        float clipTop = cellY + firstRowShift + cell.Borders.Top.WidthPt;
                        float clipBottom = cellY + firstRowShift + visibleRowH - cell.Borders.Bottom.WidthPt;

                        canvas.Save();
                        canvas.ClipRect(new SKRect(
                            cellX + cell.Borders.Left.WidthPt,
                            clipTop,
                            cellX + cell.WidthPt - cell.Borders.Right.WidthPt,
                            clipBottom));
                        foreach (var paraLayout in cell.Paragraphs)
                            RenderParagraphLines(canvas, paraLayout.Layout, contentX + paraLayout.Layout.LeftIndentPt,
                                contentY + paraLayout.Ypt, 0, paraLayout.Layout.Lines.Count);
                        canvas.Restore();
                    }
                }

                // Метка разрыва под таблицей
                if (!string.IsNullOrEmpty(pageTable.BreakLabel))
                {
                    float lastRowBottom = tableBaseY + headerOffset;
                    int lastRenderedRow = (rowTo > 0 && rowTo <= layout.Rows.Count)
                        ? rowTo - 1 : layout.Rows.Count - 1;
                    if (lastRenderedRow >= rowFrom && lastRenderedRow < layout.Rows.Count)
                    {
                        var lr = layout.Rows[lastRenderedRow];
                        lastRowBottom = tableBaseY + headerOffset + lr.Ypt + lr.HeightPt - rowOffsetY;
                    }
                    using var lbPaint = new SKPaint { Color = SKColors.Gray, IsAntialias = true };
                    var tf2 = GetOrCreateTypeface("Arial", false, true);
                    using var font2 = new SKFont(tf2, 9f);
                    canvas.DrawText(pageTable.BreakLabel, tableX, lastRowBottom + 11f, font2, lbPaint);
                }
            }
        }

        /// <summary>
        /// Рендерит один параграф на SKCanvas.
        /// </summary>
        public static void RenderParagraph(
            SKCanvas canvas, SKTextLayout layout, float paraX, float paraY)
        {
            foreach (var line in layout.Lines)
            {
                float lineY = paraY + line.Y;
                float offsetX = ComputeAlignmentOffset(layout, line);

                foreach (var seg in line.Segments)
                {
                    float segX = paraX + seg.X + offsetX;
                    float baseY = lineY + line.Baseline;

                    if (seg.HighlightColor != SKColors.Transparent)
                    {
                        using var hlPaint = new SKPaint { Color = seg.HighlightColor };
                        canvas.DrawRect(segX, lineY, seg.Width, line.Height, hlPaint);
                    }

                    var typeface = GetOrCreateTypeface(seg.FontFamily, seg.IsBold, seg.IsItalic);
                    using var font = new SKFont(typeface, seg.FontSizePt);
                    using var paint = new SKPaint
                    {
                        Color = seg.Color,
                        IsAntialias = true
                    };

                    canvas.DrawText(seg.Text, segX, baseY, font, paint);

                    if (seg.IsUnderline)
                    {
                        using var uPaint = new SKPaint
                        {
                            Color = seg.Color,
                            StrokeWidth = Math.Max(0.5f, seg.FontSizePt * 0.05f),
                            IsAntialias = true
                        };
                        float underlineY = baseY + seg.FontSizePt * 0.12f;
                        canvas.DrawLine(segX, underlineY, segX + seg.Width, underlineY, uPaint);
                    }

                    if (seg.IsStrikethrough)
                    {
                        using var sPaint = new SKPaint
                        {
                            Color = seg.Color,
                            StrokeWidth = Math.Max(0.5f, seg.FontSizePt * 0.05f),
                            IsAntialias = true
                        };
                        float strikeY = baseY - seg.FontSizePt * 0.3f;
                        canvas.DrawLine(segX, strikeY, segX + seg.Width, strikeY, sPaint);
                    }
                }
            }
        }

        // ── Сборка токенов ────────────────────────────────────────────────

        /// <summary>
        /// Собирает список токенов (символ + форматирование) из runs параграфа.
        /// </summary>
        private static List<(string Char, SKRunSegment Format, int GlobalIndex)> CollectTokens(
            ParagraphBlock para,
            string? styleName,
            StyleResolver styles)
        {
            var tokens = new List<(string, SKRunSegment, int)>();
            int globalIndex = 0;

            string styleFontFamily = styles.ResolveFontFamily(styleName);
            float styleFontSize = styles.ResolveFontSize(styleName);
            bool styleBold = styles.ResolveBold(styleName);
            bool styleItalic = styles.ResolveItalic(styleName);

            foreach (var chunk in para.Chunks)
            {
                foreach (var run in chunk.Runs)
                {
                    if (string.IsNullOrEmpty(run.Text)) continue;

                    var p = run.Properties;

                    var format = new SKRunSegment
                    {
                        FontFamily = !string.IsNullOrEmpty(p?.FontFamily)
                                               ? p!.FontFamily : styleFontFamily,
                        FontSizePt = p?.FontSize.HasValue == true
                                               ? (float)p.FontSize.Value : styleFontSize,
                        IsBold = p?.IsBold ?? styleBold,
                        IsItalic = p?.IsItalic ?? styleItalic,
                        IsUnderline = p?.IsUnderline ?? false,
                        IsStrikethrough = p?.IsStrikethrough ?? false,
                        Color = ParseColor(p?.TextColor),
                        HighlightColor = ParseHighlight(p?.HighlightColor),
                        GlobalCharOffset = globalIndex
                    };

                    foreach (char ch in run.Text)
                    {
                        tokens.Add((ch.ToString(), format, globalIndex));
                        globalIndex++;
                    }
                }

                chunk.InvalidateLength();
            }

            return tokens;
        }

        // ── Вёрстка строк ─────────────────────────────────────────────────

        /// <summary>
        /// Жадный алгоритм переноса токенов по строкам с учётом ширины текстовой области.
        /// </summary>
        private static void WrapTokensToLines(
            List<(string Char, SKRunSegment Format, int GlobalIndex)> tokens,
            SKTextLayout layout,
            float availableWidthPt,
            float lineSpacing)
        {
            if (tokens.Count == 0)
            {
                var emptyLine = BuildEmptyLine(layout, lineSpacing);
                layout.Lines.Add(emptyLine);
                layout.TotalHeightPt = emptyLine.Height;
                return;
            }

            float lineWidth = availableWidthPt - layout.FirstLineIndentPt;
            float currentW = 0f;
            var currentLine = new SKLineLayout { FirstCharIndex = tokens[0].GlobalIndex };
            var wordBuffer = new List<(string Char, SKRunSegment Format, int GlobalIndex)>();
            float wordWidth = 0f;

            void FlushWord()
            {
                if (wordBuffer.Count == 0) return;

                // Слово целиком помещается — стандартный путь.
                if (currentW + wordWidth <= lineWidth || currentLine.Segments.Count == 0 && wordWidth <= lineWidth)
                {
                    if (currentW + wordWidth > lineWidth && currentLine.Segments.Count > 0)
                    {
                        FinalizeLine(currentLine, layout, lineSpacing);
                        lineWidth = availableWidthPt;
                        currentW = 0f;
                        currentLine = new SKLineLayout
                        {
                            FirstCharIndex = wordBuffer[0].GlobalIndex
                        };
                    }
                    AppendWordToLine(currentLine, wordBuffer, ref currentW);
                    wordBuffer.Clear();
                    wordWidth = 0f;
                    return;
                }

                // Слово не помещается ни в остаток текущей строки, ни в целую строку.
                // Переносим по символам (принудительный разрыв).

                // Если на текущей строке уже что-то есть — сначала завершаем её.
                if (currentLine.Segments.Count > 0)
                {
                    FinalizeLine(currentLine, layout, lineSpacing);
                    lineWidth = availableWidthPt;
                    currentW = 0f;
                    currentLine = new SKLineLayout
                    {
                        FirstCharIndex = wordBuffer[0].GlobalIndex
                    };
                }

                // Раскладываем символы слова посимвольно, разбивая на строки.
                foreach (var (ch, format, globalIdx) in wordBuffer)
                {
                    float charWidth = MeasureChar(ch, format);
                    if (currentW + charWidth > lineWidth && currentLine.Segments.Count > 0)
                    {
                        FinalizeLine(currentLine, layout, lineSpacing);
                        lineWidth = availableWidthPt;
                        currentW = 0f;
                        currentLine = new SKLineLayout { FirstCharIndex = globalIdx };
                    }
                    AppendCharToLine(currentLine, ch, format, globalIdx, ref currentW, charWidth);
                }

                wordBuffer.Clear();
                wordWidth = 0f;
            }

            foreach (var (ch, format, globalIdx) in tokens)
            {
                if (ch == " " || ch == "\t")
                {
                    FlushWord();

                    float spaceWidth = MeasureChar(ch, format);
                    if (currentW + spaceWidth <= lineWidth || currentLine.Segments.Count == 0)
                        AppendCharToLine(currentLine, ch, format, globalIdx,
                            ref currentW, spaceWidth);
                }
                else
                {
                    float charWidth = MeasureChar(ch, format);
                    wordBuffer.Add((ch, format, globalIdx));
                    wordWidth += charWidth;
                }
            }

            FlushWord();

            if (currentLine.Segments.Count > 0 || layout.Lines.Count == 0)
            {
                currentLine.IsLastLine = true;
                FinalizeLine(currentLine, layout, lineSpacing);
            }

            if (layout.Lines.Count > 0)
                layout.Lines[^1].IsLastLine = true;
        }

        private static void AppendWordToLine(
            SKLineLayout line,
            List<(string Char, SKRunSegment Format, int GlobalIndex)> word,
            ref float currentW)
        {
            foreach (var (ch, format, globalIdx) in word)
            {
                float charWidth = MeasureChar(ch, format);
                AppendCharToLine(line, ch, format, globalIdx, ref currentW, charWidth);
            }
        }

        private static void AppendCharToLine(
            SKLineLayout line,
            string ch,
            SKRunSegment format,
            int globalIdx,
            ref float currentW,
            float charWidth)
        {
            var lastSeg = line.Segments.Count > 0 ? line.Segments[^1] : null;

            if (lastSeg is not null && IsSameFormat(lastSeg, format))
            {
                lastSeg.Text += ch;
                lastSeg.Width += charWidth;
            }
            else
            {
                var seg = new SKRunSegment
                {
                    Text = ch,
                    FontFamily = format.FontFamily,
                    FontSizePt = format.FontSizePt,
                    IsBold = format.IsBold,
                    IsItalic = format.IsItalic,
                    IsUnderline = format.IsUnderline,
                    IsStrikethrough = format.IsStrikethrough,
                    Color = format.Color,
                    HighlightColor = format.HighlightColor,
                    GlobalCharOffset = globalIdx,
                    X = currentW,
                    Width = charWidth
                };
                line.Segments.Add(seg);
            }

            line.LastCharIndex = globalIdx;
            currentW += charWidth;
            line.TextWidth = currentW;
        }

        private static void FinalizeLine(
            SKLineLayout line,
            SKTextLayout layout,
            float lineSpacing)
        {
            float maxAscent = 0f;
            float maxDescent = 0f;

            foreach (var seg in line.Segments)
            {
                var typeface = GetOrCreateTypeface(seg.FontFamily, seg.IsBold, seg.IsItalic);
                using var font = new SKFont(typeface, seg.FontSizePt);

                font.GetFontMetrics(out var metrics);

                float ascent = Math.Abs(metrics.Ascent);
                float descent = Math.Abs(metrics.Descent);

                if (ascent > maxAscent) maxAscent = ascent;
                if (descent > maxDescent) maxDescent = descent;

                seg.GlyphMetrics = BuildGlyphMetrics(seg, font);
            }

            float lineHeightBase = maxAscent + maxDescent;
            float lineHeight = lineHeightBase * lineSpacing;
            float baseline = (lineHeight - lineHeightBase) / 2f + maxAscent;

            line.Y = layout.TotalHeightPt;
            line.Height = lineHeight;
            line.Baseline = baseline;

            layout.TotalHeightPt += lineHeight;
            layout.Lines.Add(line);
        }

        private static SKLineLayout BuildEmptyLine(SKTextLayout layout, float lineSpacing)
        {
            var typeface = GetOrCreateTypeface(
                StyleResolver.FallbackFontFamily, false, false);
            using var font = new SKFont(typeface, StyleResolver.FallbackFontSizePt);

            font.GetFontMetrics(out var metrics);
            float ascent = Math.Abs(metrics.Ascent);
            float descent = Math.Abs(metrics.Descent);
            float height = (ascent + descent) * lineSpacing;
            float baseline = (height - (ascent + descent)) / 2f + ascent;

            return new SKLineLayout
            {
                Y = layout.TotalHeightPt,
                Height = height,
                Baseline = baseline,
                FirstCharIndex = 0,
                LastCharIndex = -1,
                IsLastLine = true
            };
        }

        // ── Выравнивание ──────────────────────────────────────────────────

        private static float ComputeAlignmentOffset(SKTextLayout layout, SKLineLayout line)
        {
            float availableWidth = layout.RightIndentPt + layout.LeftIndentPt;

            return layout.Alignment switch
            {
                RenderAlignment.Center => availableWidth / 2f - line.TextWidth / 2f,
                RenderAlignment.Right => availableWidth - line.TextWidth,
                RenderAlignment.Justify when !line.IsLastLine => 0f,
                _ => 0f
            };
        }

        // ── Измерение текста ──────────────────────────────────────────────

        private static float MeasureChar(string ch, SKRunSegment format)
        {
            var typeface = GetOrCreateTypeface(format.FontFamily, format.IsBold, format.IsItalic);
            using var font = new SKFont(typeface, format.FontSizePt);
            return font.MeasureText(ch);
        }

        private static SKGlyphMetrics[] BuildGlyphMetrics(SKRunSegment seg, SKFont font)
        {
            if (string.IsNullOrEmpty(seg.Text))
                return Array.Empty<SKGlyphMetrics>();

            var glyphs = new SKGlyphMetrics[seg.Text.Length];
            float x = 0f;

            for (int i = 0; i < seg.Text.Length; i++)
            {
                float width = font.MeasureText(seg.Text[i].ToString());
                glyphs[i] = new SKGlyphMetrics
                {
                    CharIndex = seg.GlobalCharOffset + i,
                    X = x,
                    Width = width
                };
                x += width;
            }

            return glyphs;
        }

        // ── Таблицы — вспомогательные ─────────────────────────────────────

        /// <summary>
        /// Вычисляет ширины колонок в pt.
        /// Fixed — фиксированная ширина, без ограничений (пользователь сам решает).
        /// Auto — равномерно делят доступное пространство (страница), масштабируются если не влезают.
        /// </summary>
        private static List<float> ComputeColumnWidths(
            TableBlock table, float textAreaWidthPt, int colCount)
        {
            var widths = new float[colCount];
            float usedFixedPt = 0f;
            int autoCount = 0;

            for (int i = 0; i < colCount && i < table.Columns.Count; i++)
            {
                var col = table.Columns[i];
                switch (col.WidthType)
                {
                    case TableColumnWidthType.Fixed:
                        // Fixed ширина не ограничивается — таблица может выходить за страницу.
                        widths[i] = MmToPt(col.WidthValue);
                        usedFixedPt += widths[i];
                        break;
                    case TableColumnWidthType.Percent:
                        widths[i] = textAreaWidthPt * (float)(col.WidthValue / 100.0);
                        usedFixedPt += widths[i];
                        break;
                    default:
                        autoCount++;
                        break;
                }
            }

            // Auto-колонки (только при создании новой таблицы): равномерно делим страницу.
            // Если суммарная желаемая ширина > страницы — масштабируем вниз.
            if (autoCount > 0)
            {
                float available = Math.Max(textAreaWidthPt - usedFixedPt, autoCount * 10f);
                float autoWidth = available / autoCount;
                // Масштабирование вниз если не влезают
                float totalWanted = usedFixedPt + autoWidth * autoCount;
                if (totalWanted > textAreaWidthPt && textAreaWidthPt > 0)
                    autoWidth = Math.Max(10f, (textAreaWidthPt - usedFixedPt) / autoCount);
                for (int i = 0; i < colCount; i++)
                    if (widths[i] == 0f)
                        widths[i] = autoWidth;
            }

            return new List<float>(widths);
        }

        /// <summary>
        /// Рендерит границы одной ячейки таблицы.
        /// </summary>
        /// <summary>
        /// Рисует границы ячейки. Публичный для DocumentCanvas (рендерит структуру таблицы отдельно от содержимого).
        /// </summary>
        public static void RenderCellBordersPublic(
            SKCanvas canvas, SKTableCellLayout cell,
            float cellX, float cellY,
            float visibleH,
            float canvasScale = 1f,
            bool suppressTop = false, bool suppressBottom = false)
            => RenderCellBorders(canvas, cell, cellX, cellY, visibleH, canvasScale, suppressTop, suppressBottom);

        private static void RenderCellBorders(
            SKCanvas canvas,
            SKTableCellLayout cell,
            float cellX,
            float cellY,
            float visibleH,
            float canvasScale = 1f,
            bool suppressTop = false,
            bool suppressBottom = false)
        {
            if (!suppressTop)
                DrawBorderLine(canvas, cell.Borders.Top,
                    cellX, cellY,
                    cellX + cell.WidthPt, cellY, canvasScale);

            if (!suppressBottom)
                DrawBorderLine(canvas, cell.Borders.Bottom,
                    cellX, cellY + visibleH,
                    cellX + cell.WidthPt, cellY + visibleH, canvasScale);

            DrawBorderLine(canvas, cell.Borders.Left,
                cellX, cellY,
                cellX, cellY + visibleH, canvasScale);

            DrawBorderLine(canvas, cell.Borders.Right,
                cellX + cell.WidthPt, cellY,
                cellX + cell.WidthPt, cellY + visibleH, canvasScale);
        }

        /// <summary>
        /// Рисует одну линию границы ячейки с учётом стиля и толщины.
        /// </summary>
        private static void DrawBorderLine(
            SKCanvas canvas,
            SKTableBorderLineLayout border,
            float x1, float y1, float x2, float y2,
            float canvasScale = 1f)
        {
            if (border.Style == 3) return; // None

            if (!SKColor.TryParse(border.Color, out var color))
                color = SKColors.Black;

            // Минимальная ширина = 1 физический пиксель независимо от DPI и зума.
            // canvasScale переводит pt → px. Минимум 1px = 1/canvasScale pt.
            float minWidthPt = canvasScale > 0f ? 1f / canvasScale : 0.75f;
            float strokeWidth = Math.Max(minWidthPt, border.WidthPt > 0f ? border.WidthPt : minWidthPt);

            // Pixel-snapping в пиксельном пространстве:
            // переводим в px, округляем до ближайшего 0.5px, переводим обратно в pt.
            // Это гарантирует чёткую линию на любом разрешении (HD, 4K, Retina).
            if (Math.Abs(x1 - x2) < 0.01f) // вертикальная
            {
                float xPx = (float)Math.Round(x1 * canvasScale - 0.5f) + 0.5f;
                x1 = x2 = xPx / canvasScale;
            }
            else // горизонтальная
            {
                float yPx = (float)Math.Round(y1 * canvasScale - 0.5f) + 0.5f;
                y1 = y2 = yPx / canvasScale;
            }

            using var paint = new SKPaint
            {
                Color = color,
                StrokeWidth = strokeWidth,
                IsStroke = true,
                IsAntialias = false
            };

            if (border.Style == 1) // Dashed
                paint.PathEffect = SKPathEffect.CreateDash(
                    new[] { strokeWidth * 4f, strokeWidth * 2f }, 0);

            canvas.DrawLine(x1, y1, x2, y2, paint);
        }

        /// <summary>
        /// Строит SKTableCellBorderLayout из модели CellBorders.
        /// </summary>
        private static SKTableCellBorderLayout BuildCellBorderLayout(CellBorders borders)
        {
            return new SKTableCellBorderLayout
            {
                Top = BorderLineToLayout(borders.Top, borders.ThicknessPt, borders.Color),
                Bottom = BorderLineToLayout(borders.Bottom, borders.ThicknessPt, borders.Color),
                Left = BorderLineToLayout(borders.Left, borders.ThicknessPt, borders.Color),
                Right = BorderLineToLayout(borders.Right, borders.ThicknessPt, borders.Color)
            };
        }

        /// <summary>
        /// Конвертирует BorderStyle + толщину в SKTableBorderLineLayout.
        /// </summary>
        private static SKTableBorderLineLayout BorderLineToLayout(
            BorderStyle style, double thicknessPt, string? color)
        {
            return new SKTableBorderLineLayout
            {
                WidthPt = style == BorderStyle.None ? 0f : (float)thicknessPt,
                Color = color ?? "#000000",
                Style = style switch
                {
                    BorderStyle.None => 3,
                    BorderStyle.Dashed => 1,
                    BorderStyle.Dotted => 1,
                    _ => 0
                }
            };
        }

        /// <summary>
        /// Возвращает толщину границы в pt для расчёта внутренних отступов ячейки.
        /// </summary>
        private static float BorderToPt(CellBorders borders)
            => (float)borders.ThicknessPt;

        private static float BorderToPt(SKTableBorderLineLayout border)
            => border.WidthPt;

        // ── Вспомогательные ───────────────────────────────────────────────

        private static SKPageContent CreatePage(
            float pageWidthPt, float pageHeightPt,
            float marginLeftPt, float marginTopPt,
            float textWidthPt, float textHeightPt) => new()
            {
                PageWidthPt = pageWidthPt,
                PageHeightPt = pageHeightPt,
                MarginLeftPt = marginLeftPt,
                MarginTopPt = marginTopPt,
                TextWidthPt = textWidthPt,
                TextHeightPt = textHeightPt
            };

        private static bool IsSameFormat(SKRunSegment a, SKRunSegment b)
            => a.FontFamily == b.FontFamily
            && a.FontSizePt == b.FontSizePt
            && a.IsBold == b.IsBold
            && a.IsItalic == b.IsItalic
            && a.IsUnderline == b.IsUnderline
            && a.IsStrikethrough == b.IsStrikethrough
            && a.Color == b.Color
            && a.HighlightColor == b.HighlightColor;

        private static int GetPlainTextLength(ParagraphBlock para)
        {
            int len = 0;
            foreach (var chunk in para.Chunks)
                foreach (var run in chunk.Runs)
                    len += run.Text?.Length ?? 0;
            return len;
        }

        private static float MmToPt(double mm) => (float)(mm * 72.0 / 25.4);

        private static SKTypeface GetOrCreateTypeface(string family, bool bold, bool italic)
        {
            var key = (family, bold, italic);

            if (_typefaceCache.TryGetValue(key, out var cached))
                return cached;

            var style = (bold, italic) switch
            {
                (true, true) => SKFontStyle.BoldItalic,
                (true, false) => SKFontStyle.Bold,
                (false, true) => SKFontStyle.Italic,
                _ => SKFontStyle.Normal
            };

            var typeface = SKTypeface.FromFamilyName(family, style)
                ?? SKTypeface.FromFamilyName(StyleResolver.FallbackFontFamily, style)
                ?? SKTypeface.Default;

            _typefaceCache.TryAdd(key, typeface);
            return typeface;
        }

        private static SKColor ParseColor(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return SKColors.Black;
            return SKColor.TryParse(hex, out var c) ? c : SKColors.Black;
        }

        private static SKColor ParseHighlight(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return SKColors.Transparent;
            return SKColor.TryParse(hex, out var c) ? c : SKColors.Transparent;
        }

        /// <summary>
        /// Рендерит диапазон строк параграфа [lineFrom, lineTo).
        /// </summary>
        public static void RenderParagraphLines(
            SKCanvas canvas, SKTextLayout layout,
            float paraX, float paraY,
            int lineFrom, int lineTo)
        {
            if (layout.Lines.Count == 0) return;

            int clampedFrom = Math.Max(0, lineFrom);
            int clampedTo = Math.Min(lineTo, layout.Lines.Count);
            float yBase = clampedFrom < layout.Lines.Count
                                    ? layout.Lines[clampedFrom].Y : 0f;

            for (int i = clampedFrom; i < clampedTo; i++)
            {
                var line = layout.Lines[i];
                float lineY = paraY + (line.Y - yBase);
                float offsetX = ComputeAlignmentOffset(layout, line);

                // Первая физическая строка параграфа (i == 0) сдвигается вправо
                // на FirstLineIndentPt — это классический отступ первой строки.
                // Ширина этой строки была уже уменьшена на FirstLineIndentPt в WrapTokensToLines,
                // поэтому текст корректно вписывается без выхода за правый край.
                float firstLineExtra = (i == 0) ? layout.FirstLineIndentPt : 0f;

                foreach (var seg in line.Segments)
                {
                    float segX = paraX + seg.X + offsetX + firstLineExtra;
                    float baseY = lineY + line.Baseline;

                    if (seg.HighlightColor != SKColors.Transparent)
                    {
                        using var hlPaint = new SKPaint { Color = seg.HighlightColor };
                        canvas.DrawRect(segX, lineY, seg.Width, line.Height, hlPaint);
                    }

                    var typeface = GetOrCreateTypeface(seg.FontFamily, seg.IsBold, seg.IsItalic);
                    using var font = new SKFont(typeface, seg.FontSizePt);
                    using var paint = new SKPaint
                    {
                        Color = seg.Color,
                        IsAntialias = true
                    };

                    canvas.DrawText(seg.Text, segX, baseY, font, paint);

                    if (seg.IsUnderline)
                    {
                        using var uPaint = new SKPaint
                        {
                            Color = seg.Color,
                            StrokeWidth = Math.Max(0.5f, seg.FontSizePt * 0.05f),
                            IsAntialias = true
                        };
                        float underlineY = baseY + seg.FontSizePt * 0.12f;
                        canvas.DrawLine(segX, underlineY, segX + seg.Width, underlineY, uPaint);
                    }

                    if (seg.IsStrikethrough)
                    {
                        using var sPaint = new SKPaint
                        {
                            Color = seg.Color,
                            StrokeWidth = Math.Max(0.5f, seg.FontSizePt * 0.05f),
                            IsAntialias = true
                        };
                        float strikeY = baseY - seg.FontSizePt * 0.3f;
                        canvas.DrawLine(segX, strikeY, segX + seg.Width, strikeY, sPaint);
                    }
                }
            }
        }
    }
}