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

            // Вычисляем ширины колонок в pt.
            float tableWidthPt = textAreaWidthPt * (float)(table.WidthPercent / 100.0);
            var colWidthsPt = ComputeColumnWidths(table, tableWidthPt, colCount);

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

                    float contentWidthPt = Math.Max(
                        cellWidthPt - padLeftPt - padRightPt
                       - (float)cell.Borders.ThicknessPt
                       - (float)cell.Borders.ThicknessPt,
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

                    cellLayout.ContentHeightPt = cellContentY;
                    cellLayout.HeightPt = cellContentY + padTopPt + padBottomPt
                                        + (float)(cell.Borders.Top)
                                        + (float)(cell.Borders.Bottom);

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
            float tableY)
        {
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
                    RenderCellBorders(canvas, cell, cellX, cellY);

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

            if (currentPage.Paragraphs.Count > 0 || pageLayout.Pages.Count == 0)
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
        }

        /// <summary>
        /// Рендерит один параграф на SKCanvas.
        /// </summary>
        public static void RenderParagraph(
            SKCanvas canvas, SKTextLayout layout, float paraX, float paraY)
        {
            int _renderLineIdx = 0;
            foreach (var line in layout.Lines)
            {
                float lineY = paraY + line.Y;
                float offsetX = ComputeAlignmentOffset(layout, line);
                float firstLineX = (_renderLineIdx == 0) ? layout.FirstLineIndentPt : 0f;
                _renderLineIdx++;

                foreach (var seg in line.Segments)
                {
                    float segX = paraX + seg.X + offsetX + firstLineX;
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
            float currentW = 0f;  // seg.X всегда начинается с 0; FirstLineIndentPt добавляется отдельно при рендере и hit-тесте
            var currentLine = new SKLineLayout { FirstCharIndex = tokens[0].GlobalIndex };
            var wordBuffer = new List<(string Char, SKRunSegment Format, int GlobalIndex)>();
            float wordWidth = 0f;

            // Переносит слово из wordBuffer на следующую строку если оно не помещается.
            // Если слово само по себе шире строки (oversize word) — разбивает его
            // посимвольно: символы идут на строку пока влезают, остаток переносится.
            void FlushWord()
            {
                if (wordBuffer.Count == 0) return;

                // Если слово не влезает целиком на текущую строку — начинаем новую.
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

                // Слово помещается целиком — быстрый путь.
                if (wordWidth <= lineWidth)
                {
                    AppendWordToLine(currentLine, wordBuffer, ref currentW);
                    wordBuffer.Clear();
                    wordWidth = 0f;
                    return;
                }

                // Слово само по себе шире доступной ширины строки (oversize word).
                // Разбиваем его посимвольно: каждый символ что не влезает — начинает новую строку.
                foreach (var (wch, wfmt, widx) in wordBuffer)
                {
                    float charW = MeasureChar(wch, wfmt);

                    if (currentW + charW > lineWidth && currentLine.Segments.Count > 0)
                    {
                        FinalizeLine(currentLine, layout, lineSpacing);
                        lineWidth = availableWidthPt;
                        currentW = 0f;
                        currentLine = new SKLineLayout { FirstCharIndex = widx };
                    }

                    AppendCharToLine(currentLine, wch, wfmt, widx, ref currentW, charW);
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
                    {
                        // Пробел влезает — добавляем нормально.
                        AppendCharToLine(currentLine, ch, format, globalIdx,
                            ref currentW, spaceWidth);
                    }
                    else
                    {
                        // Пробел не влезает — добавляем с нулевой шириной.
                        // Символ должен существовать в индексации (LastCharIndex),
                        // иначе _caretChar после ввода пробела = FirstChar следующей строки
                        // и каретка прыгает вниз.
                        float zeroW = 0f;
                        AppendCharToLine(currentLine, ch, format, globalIdx,
                            ref currentW, zeroW);
                        // currentW не изменился (добавили 0) — следующее слово
                        // корректно перенесётся на новую строку.
                    }
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
            // textWidthPt — ширина текстовой зоны без отступов (то, что передавалось
            // в WrapTokensToLines). Не хранится в SKTextLayout, поэтому берём
            // максимальную ширину полной строки (не последней) как приближение.
            // Для Left-выравнивания offsetX = 0 и это поле не используется вообще.
            if (layout.Alignment == RenderAlignment.Left
                || layout.Lines.Count == 0) return 0f;

            // Ищем ширину самой широкой полной строки.
            float maxLineWidth = 0f;
            foreach (var l in layout.Lines)
                if (l.TextWidth > maxLineWidth) maxLineWidth = l.TextWidth;

            float availableWidth = maxLineWidth;

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
        /// Auto-колонки делят оставшееся место поровну.
        /// Fixed — фиксированная ширина в мм конвертируется в pt.
        /// Percent — процент от ширины таблицы.
        /// </summary>
        private static List<float> ComputeColumnWidths(
            TableBlock table, float tableWidthPt, int colCount)
        {
            var widths = new float[colCount];
            float usedPt = 0f;
            int autoCount = 0;

            for (int i = 0; i < colCount && i < table.Columns.Count; i++)
            {
                var col = table.Columns[i];
                switch (col.WidthType)
                {
                    case TableColumnWidthType.Fixed:
                        widths[i] = MmToPt(col.WidthValue);
                        usedPt += widths[i];
                        break;
                    case TableColumnWidthType.Percent:
                        widths[i] = tableWidthPt * (float)(col.WidthValue / 100.0);
                        usedPt += widths[i];
                        break;
                    default:
                        autoCount++;
                        break;
                }
            }

            // Для колонок без явно заданной ширины делим оставшееся пространство.
            if (autoCount > 0)
            {
                float autoWidth = Math.Max((tableWidthPt - usedPt) / autoCount, 10f);
                for (int i = 0; i < colCount; i++)
                    if (widths[i] == 0f)
                        widths[i] = autoWidth;
            }

            return new List<float>(widths);
        }

        /// <summary>
        /// Рендерит границы одной ячейки таблицы.
        /// </summary>
        private static void RenderCellBorders(
            SKCanvas canvas,
            SKTableCellLayout cell,
            float cellX,
            float cellY)
        {
            DrawBorderLine(canvas, cell.Borders.Top,
                cellX, cellY,
                cellX + cell.WidthPt, cellY);

            DrawBorderLine(canvas, cell.Borders.Bottom,
                cellX, cellY + cell.HeightPt,
                cellX + cell.WidthPt, cellY + cell.HeightPt);

            DrawBorderLine(canvas, cell.Borders.Left,
                cellX, cellY,
                cellX, cellY + cell.HeightPt);

            DrawBorderLine(canvas, cell.Borders.Right,
                cellX + cell.WidthPt, cellY,
                cellX + cell.WidthPt, cellY + cell.HeightPt);
        }

        /// <summary>
        /// Рисует одну линию границы ячейки с учётом стиля и толщины.
        /// </summary>
        private static void DrawBorderLine(
            SKCanvas canvas,
            SKTableBorderLineLayout border,
            float x1, float y1, float x2, float y2)
        {
            if (border.WidthPt <= 0 || border.Style == 3) return; // Style 3 = None

            if (!SKColor.TryParse(border.Color, out var color))
                color = SKColors.Black;

            using var paint = new SKPaint
            {
                Color = color,
                StrokeWidth = border.WidthPt,
                IsStroke = true,
                IsAntialias = false
            };

            // Стиль 1 = Dashed.
            if (border.Style == 1)
                paint.PathEffect = SKPathEffect.CreateDash(
                    new[] { border.WidthPt * 4f, border.WidthPt * 2f }, 0);

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

                // Первая строка параграфа визуально сдвигается на FirstLineIndentPt.
                // i == 0 — это именно layout line 0 (clampedFrom ≥ 1 для последующих слайсов).
                float firstLineX = (i == 0) ? layout.FirstLineIndentPt : 0f;

                foreach (var seg in line.Segments)
                {
                    float segX = paraX + seg.X + offsetX + firstLineX;
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