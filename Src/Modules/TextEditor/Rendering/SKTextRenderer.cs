using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Writersword.Core.Models.Print;
using Writersword.Core.Models.Project;
using Writersword.Core.Models.Rendering;
using Writersword.Modules.TextEditor.Rendering;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using RenderAlignment = Writersword.Core.Models.Rendering.TextAlignment;

namespace Writersword.Modules.TextEditor.Rendering
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

        // Кеш SKFont по ключу (typeface handle, размер в тысячных pt).
        // SKFont — тонкая обёртка над нативным объектом; без кеша создаётся заново
        // для каждого сегмента каждого рендер-кадра и при измерении в layout.
        private static readonly ConcurrentDictionary<(IntPtr Typeface, int SizeMils), SKFont>
            _fontCache = new();

        // Кеш фолбэк-гарнитур по кодпоинту Unicode.
        // Заполняется при первом обращении к символу не поддержанному основным шрифтом.
        // null — система не нашла ни одного шрифта с нужным глифом.
        private static readonly ConcurrentDictionary<int, string?> _fallbackFamilyCache = new();

        /// <summary>
        /// Сбрасывает нативные SKFont объекты из кеша.
        /// SKFont — нативные объекты SkiaSharp, накапливаются при смене вкладок.
        /// SKTypeface не сбрасываем — они тяжёлые для повторной загрузки.
        /// </summary>
        public static void TrimFontCache()
        {
            // SKFont сначала — они держат внутреннюю ссылку на SKTypeface.
            // Диспозим шрифты до диспоза гарнитур.
            foreach (var font in _fontCache.Values)
                font?.Dispose();
            _fontCache.Clear();

            // SKTypeface — нативные объекты (данные шрифтового файла в памяти).
            // При следующем открытии документа загружаются с диска за ~50 мс.
            foreach (var typeface in _typefaceCache.Values)
                typeface?.Dispose();
            _typefaceCache.Clear();

            _fallbackFamilyCache.Clear();
        }

        // ── Публичный API ─────────────────────────────────────────────────

        /// <summary>
        /// Строит вёрстку одного параграфа.
        /// Вызывается DocumentCanvas для каждого параграфа при изменении текста или ширины.
        /// isCell = true подавляет дефолтный SpaceAfter/SpaceBefore из StyleResolver:
        /// внутри ячейки интервалы применяются только если заданы явно в свойствах параграфа.
        /// </summary>
        /// <param name="para">Блок параграфа из модели документа.</param>
        /// <param name="availableWidthPt">Ширина текстовой области в pt.</param>
        /// <param name="styles">Резолвер стилей документа.</param>
        /// <param name="isCell">true — параграф внутри ячейки таблицы.</param>
        /// <param name="wrapZones">Зоны исключения обтекания текстом (координаты
        /// относительно верха первой строки и левого края текстовой области).</param>
        /// <summary>
        /// Геометрия страниц для абзаца, который может быть разрезан разрывом.
        /// Без неё строки после разрыва считают полосу обтекания по накопленной
        /// высоте внутри абзаца, тогда как физически они уже на следующей странице —
        /// и проверяются против зоны, сдвинутой на высоту переноса.
        /// Все координаты — документные, в pt.
        /// </summary>
        /// <param name="ParaStartYPt">Верх первой строки абзаца.</param>
        /// <param name="PageBottomPt">Нижняя граница текстовой области текущей страницы.</param>
        /// <param name="NextPageTopPt">Верх текстовой области следующей страницы.</param>
        /// <param name="PageStepPt">Шаг между одноимёнными границами соседних страниц.</param>
        public readonly record struct WrapPageContext(
            float ParaStartYPt,
            float PageBottomPt,
            float NextPageTopPt,
            float PageStepPt);

        /// <summary>
        /// Габарит встроенной картинки по её Id, в pt. Устанавливается канвасом:
        /// сам рендер документа не видит и достать размер объекта не может.
        /// null — встроенных объектов в документе нет.
        /// </summary>
        public Func<Guid, (float WidthPt, float HeightPt)?>? InlineImageSize { get; set; }

        public SKTextLayout BuildLayout(
            ParagraphBlock para,
            float availableWidthPt,
            StyleResolver styles,
            bool isCell = false,
            IReadOnlyList<SKWrapZone>? wrapZones = null,
            bool wrapPreferPushDown = false,
            WrapPageContext? wrapPages = null)
        {
            string? styleName = para.Properties.StyleName;

            float leftIndentPt = (float)(para.Properties.LeftIndent
                                        ?? styles.ResolveLeftIndent(styleName));
            float rightIndentPt = (float)(para.Properties.RightIndent
                                        ?? styles.ResolveRightIndent(styleName));
            float firstLineIndentPt = (float)(para.Properties.FirstLineIndent ?? 0.0);

            // Элемент списка. Без собственного левого отступа берём отступ по уровню.
            // Затем меряем ширину цифры/символа маркера и отодвигаем текст ПЕРВОЙ строки так,
            // чтобы между правым краем цифры и текстом всегда был зазор (MarkerTextMinGapPt).
            // Стрелка метки стоит по левому краю цифры (позиция markerAbs); двигая её вправо,
            // пользователь сдвигает и текст первой строки. Строки 2+ идут по левому отступу.
            var listProps = para.ListProperties;
            if (listProps is not null && listProps.MarkerType != ListMarkerType.None)
            {
                if (para.Properties.LeftIndent is null)
                    leftIndentPt = (float)listProps.EffectiveTextIndentPt();

                double markerAbs = listProps.MarkerIndentPt
                    ?? Math.Max(0.0, leftIndentPt - ListProperties.DefaultHangingPt);

                string markerText = listProps.ComputedMarkerText ?? string.Empty;
                if (markerText.Length > 0)
                {
                    var mtf = GetOrCreateTypeface(styles.ResolveFontFamily(styleName), false, false);
                    var mfont = GetOrCreateFont(mtf, styles.ResolveFontSize(styleName));
                    float markerW = mfont.MeasureText(markerText);
                    // Текст ПЕРВОЙ строки идёт сразу после номера: номер + ширина + зазор.
                    // Позиция абсолютна (от поля) и от левого края строк 2+ НЕ зависит —
                    // значение может быть отрицательным (первая строка левее строк 2+).
                    double offset = markerAbs + markerW + listProps.MarkerTextMinGapPt - leftIndentPt;

                    // Не пускаем текст первой строки за правый край текстовой зоны: оставляем
                    // минимум места под текст, иначе строка уезжала бы за пределы страницы.
                    const double MinFirstLineWidthPt = 36.0;
                    double maxOffset = availableWidthPt - leftIndentPt - rightIndentPt - MinFirstLineWidthPt;
                    if (offset > maxOffset) offset = maxOffset;
                    firstLineIndentPt = (float)offset;

                    listProps.ComputedMarkerWidthPt = markerW;
                    listProps.ComputedFirstLineOffsetPt = offset;
                }
                else
                {
                    firstLineIndentPt = 0f;
                    listProps.ComputedMarkerWidthPt = 0;
                    listProps.ComputedFirstLineOffsetPt = 0;
                }
            }

            // Внутри ячейки дефолтный SpaceBefore/SpaceAfter = 0.
            // Интервал применяется только если явно задан в свойствах параграфа.
            float spaceBeforePt = (float)(para.Properties.SpaceBefore
                                        ?? (isCell ? 0.0 : (double)styles.ResolveSpaceBefore(styleName)));
            float spaceAfterPt = (float)(para.Properties.SpaceAfter
                                        ?? (isCell ? 0.0 : (double)styles.ResolveSpaceAfter(styleName)));

            float lineSpacing = para.Properties.LineSpacingValue.HasValue
                                        ? (float)para.Properties.LineSpacingValue.Value
                                        : styles.ResolveLineSpacing(styleName);

            // Конвертируем TextAlignment из модели в Core enum через int.
            // Значения намеренно совпадают: Left=0, Center=1, Right=2, Justify=3.
            RenderAlignment alignment = para.Properties.Alignment.HasValue
                ? (RenderAlignment)(int)para.Properties.Alignment.Value
                : styles.ResolveAlignment(styleName);

            // textWidthPt — ширина строки текста без учёта отступов параграфа.
            // Это та ширина по которой выполняется перенос строк.
            // Она же используется в ComputeAlignmentOffset для правильного
            // вычисления сдвига при выравнивании по центру / правому краю.
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

            var tokens = CollectTokens(para, styleName, styles, InlineImageSize);
            WrapTokensToLines(tokens, layout, textWidthPt, lineSpacing, wrapZones, wrapPreferPushDown, wrapPages);
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
            StyleResolver styles,
            IReadOnlyDictionary<ParagraphBlock, ParagraphBlock>? cellFontPreview = null)
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

                    // Верстаем параграфы ячейки с isCell = true — подавляем дефолтный SpaceAfter.
                    float cellContentY = 0f;
                    for (int pi = 0; pi < cell.Paragraphs.Count; pi++)
                    {
                        var para = cell.Paragraphs[pi];
                        // Превью шрифта в ячейке: если для абзаца задан preview-абзац (построен
                        // канвасом по выделенному диапазону), строим раскладку из него. Модель
                        // оригинала не трогается. Ширина та же — contentWidthPt.
                        var paraSrc = (cellFontPreview != null
                            && cellFontPreview.TryGetValue(para, out var pv)) ? pv : para;
                        var paraLayout = BuildLayout(paraSrc, contentWidthPt, styles, isCell: true);

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
                var blocks = section.Blocks;
                for (int bi = 0; bi < blocks.Count; bi++)
                {
                    var block = blocks[bi];
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
                        float sliceFirstRowOffset = 0f;
                        float sliceStartOffset = 0f;

                        for (int ri = 0; ri < tableLayout.Rows.Count; ri++)
                        {
                            var row = tableLayout.Rows[ri];

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
                                    float visibleH = available;
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
                                    ri--;
                                    continue;
                                }
                                else
                                {
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

                    bool prevIsTable = bi > 0 && blocks[bi - 1] is TableBlock;
                    bool nextIsTable = bi + 1 < blocks.Count && blocks[bi + 1] is TableBlock;
                    bool isSystemAnchor = string.IsNullOrEmpty(para.GetPlainText())
                        && (prevIsTable || nextIsTable);
                    if (isSystemAnchor)
                    {
                        paraIndex++;
                        continue;
                    }

                    if (layout.Lines.Count == 0)
                    {
                        paraIndex++;
                        continue;
                    }

                    currentY += layout.SpaceBeforePt;

                    int lineFrom = 0;
                    float sliceStartY = currentY;

                    for (int li = 0; li < layout.Lines.Count; li++)
                    {
                        var line = layout.Lines[li];
                        bool isLastLine = li == layout.Lines.Count - 1;

                        if (currentY + line.Height > textHeightPt
                            && (currentPage.Paragraphs.Count > 0 || li > lineFrom))
                        {
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

                        if (isLastLine)
                        {
                            bool spaceNextIsTable = false;
                            for (int nb = bi + 1; nb < blocks.Count; nb++)
                            {
                                if (blocks[nb] is ParagraphBlock nbp
                                    && string.IsNullOrEmpty(nbp.GetPlainText())
                                    && (nb > 0 && blocks[nb - 1] is TableBlock
                                        || nb + 1 < blocks.Count && blocks[nb + 1] is TableBlock))
                                    continue;
                                spaceNextIsTable = blocks[nb] is TableBlock;
                                break;
                            }
                            if (!spaceNextIsTable)
                                currentY += layout.SpaceAfterPt;
                        }
                    }

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
                    var font = GetOrCreateFont(tf, 9f);
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

                bool hasLastRowClip = pageTable.LastRowVisibleHeightPt >= 0f;
                bool hasFirstRowOffset = pageTable.IsContinuation && pageTable.FirstRowContentOffsetPt > 0f;

                foreach (var row in layout.Rows)
                {
                    if (row.Row < rowFrom || row.Row >= rowTo) continue;

                    bool isLastRow = (row.Row == rowTo - 1);
                    bool isFirstRow = (row.Row == rowFrom);

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
                        float cellY = tableBaseY + headerOffset + cell.Ypt - rowOffsetY - firstRowShift;

                        if (!string.IsNullOrEmpty(cell.BackgroundColor)
                            && SKColor.TryParse(cell.BackgroundColor, out var bgColor))
                        {
                            using var bgPaint = new SKPaint { Color = bgColor };
                            canvas.DrawRect(cellX, cellY + firstRowShift, cell.WidthPt, visibleRowH, bgPaint);
                        }

                        bool suppressBottom = isLastRow && hasLastRowClip;
                        float visibleCellY = cellY + firstRowShift;
                        RenderCellBorders(canvas, cell, cellX, visibleCellY, visibleRowH, canvasScale, false, suppressBottom);

                        float contentX = cellX + cell.PadLeftPt + cell.Borders.Left.WidthPt;
                        float contentY = cellY + cell.PadTopPt + cell.Borders.Top.WidthPt;

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
                    var font2 = GetOrCreateFont(tf2, 9f);
                    canvas.DrawText(pageTable.BreakLabel, tableX, lastRowBottom + 11f, font2, lbPaint);
                }
            }
        }

        /// <summary>
        /// Отрисовка объекта, встроенного в строку. Ставится канвасом перед
        /// проходом рендера: сам текстовый рендер картинок рисовать не умеет —
        /// у него нет ни кеша битмапов, ни доступа к документу.
        /// Аргументы: канвас, сегмент-объект, X левого края сегмента,
        /// Y базовой линии строки.
        /// </summary>
        public static Action<SKCanvas, SKRunSegment, float, float>? DrawInlineObject { get; set; }

        /// <summary>
        /// Рендерит один параграф на SKCanvas.
        /// </summary>
        public static void RenderParagraph(
            SKCanvas canvas, SKTextLayout layout, float paraX, float paraY)
        {
            // Прямоугольник всего абзаца — для градиента текста в режиме «весь блок».
            var blockRect = new SKRect(
                paraX + layout.LeftIndentPt,
                paraY,
                paraX + layout.LeftIndentPt + layout.TextAreaWidthPt,
                paraY + layout.TotalHeightPt);

            for (int i = 0; i < layout.Lines.Count; i++)
            {
                var line = layout.Lines[i];
                float lineY = paraY + line.Y;
                float offsetX = LineAlignShift(layout, i);

                // Прямоугольник строки — для градиента текста в режиме «построчно».
                float lineStartX = paraX + offsetX + (line.Segments.Count > 0 ? line.Segments[0].X : 0f);
                var lineRect = new SKRect(lineStartX, lineY, lineStartX + line.TextWidth, lineY + line.Height);

                int lastContentSeg = LastContentSegIndex(line);
                int segIdx = -1;
                foreach (var seg in line.Segments)
                {
                    segIdx++;
                    float segX = paraX + seg.X + offsetX;
                    float baseY = lineY + line.Baseline;

                    // Объект в строке: рисует канвас, текстовые слои (подчёркивание,
                    // зачёркивание, градиент букв) к нему не применяются.
                    if (seg.IsInlineObject)
                    {
                        DrawInlineObject?.Invoke(canvas, seg, segX, baseY);
                        continue;
                    }

                    // Задник за текстом: плоский цвет либо градиент по прямоугольнику сегмента.
                    // Ширина обрезается по хвостовым пробелам в конце строки.
                    bool hlGradient = IsGradientCode(seg.HighlightCode);
                    float hlWidth = SegHighlightWidth(line, segIdx, lastContentSeg);
                    if (hlWidth > 0f && (seg.HighlightColor != SKColors.Transparent || hlGradient))
                    {
                        using var hlPaint = new SKPaint { Color = seg.HighlightColor };
                        SKShader? hlShader = null;
                        if (hlGradient)
                        {
                            var hlSpec = GradientSpec.Parse(seg.HighlightCode);
                            hlPaint.Color = GradientShaderFactory.SolidColor(hlSpec);
                            var hlRect = new SKRect(segX, lineY, segX + hlWidth, lineY + line.Height);
                            hlShader = GradientShaderFactory.BuildShader(hlSpec, hlRect);
                            hlPaint.Shader = hlShader;
                        }
                        canvas.DrawRect(segX, lineY, hlWidth, line.Height, hlPaint);
                        hlShader?.Dispose();
                    }

                    // Цвет либо градиент букв. Для одноцвета путь прежний — без шейдера.
                    SKColor textColor = seg.Color;
                    SKShader? textShader = null;
                    if (IsGradientCode(seg.ColorCode))
                    {
                        var spec = GradientSpec.Parse(seg.ColorCode);
                        textColor = GradientShaderFactory.SolidColor(spec);
                        var rect = spec.TextFill == GradientTextFill.PerLine ? lineRect : blockRect;
                        textShader = GradientShaderFactory.BuildShader(spec, rect);
                    }

                    var typeface = GetOrCreateTypeface(seg.FontFamily, seg.IsBold, seg.IsItalic);
                    var font = GetOrCreateFont(typeface, seg.FontSizePt);
                    using var paint = new SKPaint
                    {
                        Color = textColor,
                        IsAntialias = true
                    };
                    if (textShader != null) paint.Shader = textShader;

                    canvas.DrawText(seg.Text, segX, baseY, font, paint);

                    if (seg.IsUnderline)
                    {
                        using var uPaint = new SKPaint
                        {
                            Color = textColor,
                            StrokeWidth = Math.Max(0.5f, seg.FontSizePt * 0.05f),
                            IsAntialias = true
                        };
                        if (textShader != null) uPaint.Shader = textShader;
                        float underlineY = baseY + seg.FontSizePt * 0.12f;
                        canvas.DrawLine(segX, underlineY, segX + seg.Width, underlineY, uPaint);
                    }

                    if (seg.IsStrikethrough)
                    {
                        using var sPaint = new SKPaint
                        {
                            Color = textColor,
                            StrokeWidth = Math.Max(0.5f, seg.FontSizePt * 0.05f),
                            IsAntialias = true
                        };
                        if (textShader != null) sPaint.Shader = textShader;
                        float strikeY = baseY - seg.FontSizePt * 0.3f;
                        canvas.DrawLine(segX, strikeY, segX + seg.Width, strikeY, sPaint);
                    }

                    textShader?.Dispose();
                }
            }
        }

        // Признак того, что строка-код описывает градиент (а не обычный hex).
        private static bool IsGradientCode(string? code)
            => code != null && code.StartsWith("grad|", StringComparison.OrdinalIgnoreCase);

        // ── Сборка токенов ────────────────────────────────────────────────

        /// <summary>
        /// Собирает список токенов (символ + форматирование) из runs параграфа.
        /// Для каждого символа проверяет наличие глифа в назначенном шрифте.
        /// Если глиф отсутствует — подставляет системный фолбэк через SKFontManager.
        /// </summary>
        private static List<(string Char, SKRunSegment Format, int GlobalIndex)> CollectTokens(
            ParagraphBlock para,
            string? styleName,
            StyleResolver styles,
            Func<Guid, (float WidthPt, float HeightPt)?>? inlineImageSize = null)
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

                    // Объект в строке: один токен со своим габаритом вместо глифа.
                    // Размер берётся из самой картинки — она живёт в InlineObjects
                    // раздела, а run хранит только ссылку.
                    if (run.InlineImageId is Guid inlineId)
                    {
                        var size = inlineImageSize?.Invoke(inlineId);

                        var objectFormat = new SKRunSegment
                        {
                            FontFamily = styleFontFamily,
                            FontSizePt = styleFontSize,
                            Color = ParseColor(run.Properties?.TextColor),
                            GlobalCharOffset = globalIndex,
                            InlineImageId = inlineId,
                            ObjectWidthPt = size?.WidthPt ?? 0f,
                            ObjectHeightPt = size?.HeightPt ?? 0f
                        };

                        tokens.Add((RunModel.ObjectPlaceholder.ToString(), objectFormat, globalIndex));
                        globalIndex++;
                        continue;
                    }

                    var p = run.Properties;

                    string resolvedFamily = !string.IsNullOrEmpty(p?.FontFamily)
                        ? p!.FontFamily : styleFontFamily;
                    float resolvedSize = p?.FontSize.HasValue == true
                        ? (float)p.FontSize.Value : styleFontSize;
                    bool resolvedBold = p?.IsBold ?? styleBold;
                    bool resolvedItalic = p?.IsItalic ?? styleItalic;

                    // Над/подстрочный: уменьшаем кегль и смещаем базовую линию. Сдвиг считаем от
                    // исходного размера, чтобы надстрочный поднимался к верху обычного текста,
                    // а подстрочный опускался под него. 0 — обычный текст.
                    float segFontSize = resolvedSize;
                    float baselineShift = 0f;
                    if (p?.IsSuperscript == true)
                    {
                        segFontSize = resolvedSize * 0.65f;
                        baselineShift = resolvedSize * 0.34f;
                    }
                    else if (p?.IsSubscript == true)
                    {
                        segFontSize = resolvedSize * 0.65f;
                        baselineShift = -resolvedSize * 0.16f;
                    }

                    var format = new SKRunSegment
                    {
                        FontFamily = resolvedFamily,
                        FontSizePt = segFontSize,
                        BaselineShiftPt = baselineShift,
                        IsBold = resolvedBold,
                        IsItalic = resolvedItalic,
                        IsUnderline = p?.IsUnderline ?? false,
                        IsStrikethrough = p?.IsStrikethrough ?? false,
                        Color = ParseColor(p?.TextColor),
                        HighlightColor = ParseHighlight(p?.HighlightColor),
                        ColorCode = p?.TextColor,
                        HighlightCode = p?.HighlightColor,
                        GlobalCharOffset = globalIndex
                    };

                    // Получаем typeface один раз на run для проверки глифов.
                    var typeface = GetOrCreateTypeface(resolvedFamily, resolvedBold, resolvedItalic);

                    foreach (char ch in run.Text)
                    {
                        SKRunSegment charFormat = format;
                        char drawCh = ch;

                        // Управляющие символы (\r, \n, \t и прочие C0 < U+0020) не имеют глифа и
                        // рисуются шрифтом как .notdef — квадрат (□). Затекают в текст ячейки при
                        // вставке многострочного текста. Рисуем как пробел, сохраняя счётчик
                        // символов, чтобы каретка/хит-тест не смещались.
                        if (ch < ' ') drawCh = ' ';

                        // Проверяем глифы только для символов вне Basic Latin (U+0080+).
                        // Basic Latin всегда есть в любом текстовом шрифте — проверять незачем,
                        // а MatchCharacter для них может вернуть Marlett/Wingdings.
                        if (!char.IsSurrogate(ch) && ch >= '\u0080')
                        {
                            int codepoint = ch;
                            if (typeface.GetGlyph(codepoint) == 0)
                            {
                                string? fallbackFamily = FindFallbackFamily(codepoint, styles);
                                if (fallbackFamily != null && fallbackFamily != resolvedFamily)
                                {
                                    charFormat = new SKRunSegment
                                    {
                                        FontFamily = fallbackFamily,
                                        FontSizePt = segFontSize,
                                        BaselineShiftPt = baselineShift,
                                        IsBold = resolvedBold,
                                        IsItalic = resolvedItalic,
                                        IsUnderline = p?.IsUnderline ?? false,
                                        IsStrikethrough = p?.IsStrikethrough ?? false,
                                        Color = ParseColor(p?.TextColor),
                                        HighlightColor = ParseHighlight(p?.HighlightColor),
                                        ColorCode = p?.TextColor,
                                        HighlightCode = p?.HighlightColor,
                                        GlobalCharOffset = globalIndex
                                    };
                                }
                            }
                        }

                        tokens.Add((drawCh.ToString(), charFormat, globalIndex));
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
        /// textAreaWidthPt — ширина строки текста без LeftIndent/RightIndent (уже вычтены).
        /// Сохраняется в layout.TextAreaWidthPt для корректного ComputeAlignmentOffset.
        /// </summary>
        private static void WrapTokensToLines(
            List<(string Char, SKRunSegment Format, int GlobalIndex)> tokens,
            SKTextLayout layout,
            float textAreaWidthPt,
            float lineSpacing,
            IReadOnlyList<SKWrapZone>? wrapZones = null,
            bool wrapPreferPushDown = false,
            WrapPageContext? wrapPages = null)
        {
            // Сохраняем ширину текстовой области — используется в ComputeAlignmentOffset.
            // textAreaWidthPt = availableWidthPt - leftIndentPt - rightIndentPt,
            // т.е. именно то пространство в котором располагаются строки.
            layout.TextAreaWidthPt = textAreaWidthPt;

            if (tokens.Count == 0)
            {
                var emptyLine = BuildEmptyLine(layout, lineSpacing);
                layout.Lines.Add(emptyLine);
                layout.TotalHeightPt = emptyLine.Height;
                return;
            }

            bool hasZones = wrapZones is { Count: > 0 };

            // Полоса рядом с объектом пригодна, если в неё целиком влезает самое
            // длинное слово абзаца. Иначе слово пришлось бы рвать посимвольно
            // (см. FlushWord), а рваные слова недопустимы — строка уходит под объект.
            // Порог по кеглю (кегль × N) сюда не годится: он не связан с реальным
            // текстом и на кегле 20 давал границу ровно в рабочем диапазоне,
            // из-за чего абзац перекидывался туда-обратно при сдвиге картинки на 7 pt.
            const float MinBandFloorPt = 36f;

            // Во сколько раз шире должна стать полоса, чтобы вытесненный вниз абзац
            // вернулся сбоку от объекта. Гистерезис: без него абзац дребезжит на
            // границе порога при перетаскивании картинки.
            const float PushDownHysteresis = 1.2f;

            // Зоны обтекания приходят в координатах текстовой колонки (отсчёт от левого
            // поля страницы), а полосы строк вычисляются внутри области абзаца, начало
            // которой сдвинуто на левый отступ. Без приведения к одной системе координат
            // при ненулевом LeftIndent текст налезал на объект слева, а при обтекании
            // справа сдвигался на величину отступа и уходил за правое поле страницы.
            float zoneShiftPt = layout.LeftIndentPt;

            // Пробная высота строки для проверки пересечения с зоной: реальная
            // высота известна только после FinalizeLine, поэтому берём верхнюю оценку
            // по фактическим метрикам самого высокого формата параграфа. Оценка через
            // кегль * 1.35 занижала высоту у шрифтов с крупными выносными элементами:
            // строка не считалась пересекающей зону и нижней частью налезала на картинку.
            float probeLineHPt = 10f;
            float minBandWidthPt = MinBandFloorPt;

            // Высота строки, которую даст этот формат: у картинки в строке высоту задаёт
            // её габарит, а не шрифт (так же считает FinalizeLine). Без этого строка
            // с картинкой считалась высотой в кегль текста, зона обтекания рядом с ней
            // «не пересекалась», и картинка ложилась прямо на обтекаемый объект.
            float TokenLineHeight(SKRunSegment format)
            {
                var tf = GetOrCreateTypeface(format.FontFamily, format.IsBold, format.IsItalic);
                var f = GetOrCreateFont(tf, format.FontSizePt);
                f.GetFontMetrics(out var m);

                float ascent = Math.Abs(m.Ascent);
                float descent = Math.Abs(m.Descent);
                if (format.IsInlineObject && format.ObjectHeightPt > ascent)
                    ascent = format.ObjectHeightPt;

                return (ascent + descent) * Math.Max(lineSpacing, 1f);
            }

            if (hasZones)
            {
                // Пробная высота обычной строки: по самому высокому ТЕКСТОВОМУ формату.
                // Картинки сюда не входят — их высота учитывается построчно, только для
                // тех строк, куда они реально попали. Иначе одна крупная картинка задрала
                // бы пробу всему абзацу, и обычные строки уезжали бы от зон и со страниц.
                float maxLineHPt = 0f;
                float maxObjectWidthPt = 0f;
                SKRunSegment? probedFormat = null;
                foreach (var (_, format, _) in tokens)
                {
                    if (format.IsInlineObject)
                    {
                        if (format.ObjectWidthPt > maxObjectWidthPt)
                            maxObjectWidthPt = format.ObjectWidthPt;
                        continue;
                    }

                    // Формат — общий объект на весь run, соседние символы ссылаются на
                    // один и тот же экземпляр: метрики считаем один раз на run.
                    if (ReferenceEquals(format, probedFormat)) continue;
                    probedFormat = format;

                    float h = TokenLineHeight(format);
                    if (h > maxLineHPt) maxLineHPt = h;
                }
                probeLineHPt = Math.Max(probeLineHPt, maxLineHPt);

                // Самое длинное слово абзаца: непрерывный отрезок между пробелами.
                float maxWordWidthPt = 0f;
                float runWidthPt = 0f;
                foreach (var (ch, format, _) in tokens)
                {
                    if (ch == " " || ch == "\t")
                    {
                        if (runWidthPt > maxWordWidthPt) maxWordWidthPt = runWidthPt;
                        runWidthPt = 0f;
                        continue;
                    }
                    runWidthPt += MeasureChar(ch, format);
                }
                if (runWidthPt > maxWordWidthPt) maxWordWidthPt = runWidthPt;

                minBandWidthPt = BandRequirement(maxWordWidthPt, maxObjectWidthPt);
            }

            // Какой ширины должна быть полоса, чтобы в неё имело смысл ставить строку.
            // Считается по тому, что в неё реально пойдёт: слово шире полосы пришлось бы
            // рвать посимвольно, а этого делать нельзя.
            float BandRequirement(float wordWidthPt, float objectWidthPt)
            {
                float required = Math.Max(MinBandFloorPt, wordWidthPt);
                if (wrapPreferPushDown) required *= PushDownHysteresis;

                // Потолок в половину области: абзац с одним очень длинным словом иначе
                // вытеснялся бы под объект всегда, и обтекание не работало бы вовсе.
                // Такое слово всё равно придётся разорвать — но уже в полной строке.
                required = Math.Min(required, textAreaWidthPt * 0.5f);

                // Картинку разорвать нельзя: полоса уже её габарита не годится никогда,
                // и потолок в половину колонки на неё не распространяется. Иначе широкая
                // картинка «влезала» в узкую полосу и наезжала на обтекаемый объект.
                if (objectWidthPt > required)
                    required = Math.Min(objectWidthPt, textAreaWidthPt);

                return required;
            }

            // Занятые участки колонки на вертикали одной строки. Список переиспользуется
            // между строками — полоса считается на каждую строку абзаца.
            var occupiedSpans = new List<(float Left, float Right)>();

            // Накопленный сдвиг строк, уехавших на следующие страницы, и число
            // пересечённых границ. Строки идут по возрастанию localY, поэтому
            // сдвиг только растёт и вычисляется один раз на каждом переходе.
            // Объявлены до расчёта полосы: он смотрит на границу страницы, чтобы
            // не вытеснять строку за неё.
            float pageShiftPt = 0f;
            int pageCrossings = 0;

            // Полоса строки на вертикали yTop (координата верха строки относительно
            // верха первой строки параграфа): левый край и ширина внутри текстовой
            // области плюс вытеснение вниз, если рядом с зонами не осталось места.
            //
            // Все объекты, пересекающие строку, сводятся в ОДНУ картину занятости:
            // пересекающиеся и соприкасающиеся зоны сливаются в один участок, после чего
            // выбирается самый широкий свободный промежуток. Прежний код сужал полосу
            // зона за зоной, решая для каждой отдельно, с какой стороны её обходить, —
            // и результат зависел от порядка объектов в списке: две наложенные картинки
            // отправляли текст то влево, то вправо, а между ними мог «открыться»
            // просвет, которого на листе нет.
            float ComputeBand(
                float yTop, float lineHPt, float requiredWidthPt,
                List<SKWrapFragment> result)
            {
                result.Clear();

                if (!hasZones)
                {
                    result.Add(new SKWrapFragment(0f, textAreaWidthPt));
                    return 0f;
                }

                float extraTop = 0f;
                for (int guard = 0; guard < 16; guard++)
                {
                    float y = yTop + extraTop;
                    float pushBottom = float.MinValue;

                    // Ограничения по сторонам от объектов, пересекающих эту строку:
                    // текст не должен появляться правее объекта с «только слева»
                    // и левее объекта с «только справа».
                    float allowedLeftPt = 0f;
                    float allowedRightPt = textAreaWidthPt;
                    bool largestOnly = false;

                    occupiedSpans.Clear();
                    foreach (var z in wrapZones!)
                    {
                        if (z.BottomPt <= y + 0.5f || z.TopPt >= y + lineHPt) continue;

                        // Зоны приходят в координатах колонки, полосы считаются внутри
                        // области абзаца — приводим к одной системе и обрезаем по колонке.
                        float zLeftPt = Math.Max(z.LeftPt - zoneShiftPt, 0f);
                        float zRightPt = Math.Min(z.RightPt - zoneShiftPt, textAreaWidthPt);
                        if (zRightPt <= zLeftPt) continue;

                        occupiedSpans.Add((zLeftPt, zRightPt));
                        if (z.BottomPt > pushBottom) pushBottom = z.BottomPt;

                        switch (z.Side)
                        {
                            case SKWrapSide.LeftOnly:
                                if (zLeftPt < allowedRightPt) allowedRightPt = zLeftPt;
                                break;
                            case SKWrapSide.RightOnly:
                                if (zRightPt > allowedLeftPt) allowedLeftPt = zRightPt;
                                break;
                            case SKWrapSide.LargestOnly:
                                largestOnly = true;
                                break;
                        }
                    }

                    // Ни один объект не пересекает строку — вся колонка свободна.
                    if (occupiedSpans.Count == 0)
                    {
                        result.Add(new SKWrapFragment(0f, textAreaWidthPt));
                        return extraTop;
                    }

                    // Перекрывающиеся объекты — одно препятствие: интервалы сливаются
                    // курсором, поэтому промежутки между ними считаются по реальной
                    // занятости колонки, а не по каждому объекту отдельно.
                    occupiedSpans.Sort((a, b) => a.Left.CompareTo(b.Left));

                    result.Clear();
                    float cursor = 0f;
                    void TryAddGap(float gapLeft, float gapRight)
                    {
                        float l = Math.Max(gapLeft, allowedLeftPt);
                        float r = Math.Min(gapRight, allowedRightPt);
                        if (r - l >= requiredWidthPt)
                            result.Add(new SKWrapFragment(l, r - l));
                    }

                    foreach (var (spanLeft, spanRight) in occupiedSpans)
                    {
                        if (spanLeft > cursor) TryAddGap(cursor, spanLeft);
                        if (spanRight > cursor) cursor = spanRight;
                    }
                    TryAddGap(cursor, textAreaWidthPt);

                    if (result.Count > 0)
                    {
                        // «По большей стороне» — исторический режим: из всех промежутков
                        // остаётся только самый широкий, строка не разрывается объектом.
                        if (largestOnly && result.Count > 1)
                        {
                            var widest = result[0];
                            foreach (var fragment in result)
                                if (fragment.WidthPt > widest.WidthPt) widest = fragment;
                            result.Clear();
                            result.Add(widest);
                        }
                        return extraTop;
                    }

                    // Ни один промежуток не годится — строка уходит под нижний край
                    // препятствия и проверяется заново: ниже может лежать следующее.
                    float nextExtraTop = pushBottom - yTop + 0.5f;
                    if (nextExtraTop <= extraTop) break;

                    // Но не за границу страницы. Дальше начинается следующая страница,
                    // объекта этой страницы там уже нет, и опускать строку не за чем:
                    // перенос сделает пагинация. Иначе вытеснение переживало разрыв
                    // и превращалось в пустой провал наверху следующей страницы —
                    // текст отодвигала картинка, которой на этой странице не видно.
                    if (wrapPages is { } pageCtx)
                    {
                        float pushedDocYPt = pageCtx.ParaStartYPt + yTop + nextExtraTop;
                        float pageBottomPt = pageCtx.PageBottomPt + pageCrossings * pageCtx.PageStepPt;
                        if (pushedDocYPt + lineHPt > pageBottomPt) break;
                    }

                    extraTop = nextExtraTop;
                }

                result.Clear();
                result.Add(new SKWrapFragment(0f, textAreaWidthPt));
                return extraTop;
            }

            // Локальный Y строки (от верха первой строки абзаца) в систему координат
            // зон. Пока абзац целиком на своей странице сдвиг нулевой. Как только
            // очередная строка не помещается до низа страницы, она и все следующие
            // физически уходят на верх следующей — и сравниваться с зонами должны
            // уже оттуда, иначе полоса проверяется на высоту переноса выше места,
            // где строка нарисована.
            float ZoneY(float localYPt, float lineHPt)
            {
                if (wrapPages is not { } pg) return localYPt;

                float shifted = localYPt + pageShiftPt;
                for (int k = 0; k < 8; k++)
                {
                    float docY = pg.ParaStartYPt + shifted;
                    float bottomPt = pg.PageBottomPt + pageCrossings * pg.PageStepPt;
                    if (docY + lineHPt <= bottomPt) break;

                    float nextTopPt = pg.NextPageTopPt + pageCrossings * pg.PageStepPt;
                    pageShiftPt += nextTopPt - docY;
                    shifted = localYPt + pageShiftPt;
                    pageCrossings++;
                }
                return shifted;
            }

            // Отрезки полосы текущей строки и её вертикальное вытеснение.
            var bandFragments = new List<SKWrapFragment>();
            float bandExtraTop = 0f;

            // Индекс отрезка, который сейчас заполняется, и координата его конца.
            // currentW и X сегментов отсчитываются от левого края ПЕРВОГО отрезка:
            // прыжок через объект просто входит в X, поэтому отрисовка, каретка и
            // хит-тест работают с разорванной строкой без изменений.
            int fragIdx = 0;
            float fragEndW = 0f;
            float lineIndentPt = 0f;

            // Первый символ после прыжка обязан начать новый сегмент: иначе он слился бы
            // с предыдущим по совпадению формата, и разрыв строки объектом потерялся бы.
            bool segmentBreakPending = false;

            // Высота, по которой посчитана полоса текущей строки. Пока в строке один текст,
            // это проба абзаца; строка, куда попадает картинка, пересчитывает полосу по
            // своей реальной высоте.
            float lineProbeHPt = probeLineHPt;

            // Проба для слова: слово с картинкой выше текстовой строки, и полосу под него
            // надо искать по его высоте — иначе оно встанет сбоку от объекта туда, где
            // помещается только текст.
            float WordProbeHeight(List<(string Char, SKRunSegment Format, int GlobalIndex)> word)
            {
                float h = probeLineHPt;
                foreach (var (_, format, _) in word)
                {
                    if (!format.IsInlineObject) continue;
                    float th = TokenLineHeight(format);
                    if (th > h) h = th;
                }
                return h;
            }

            // Требование к полосе для конкретного слова: по нему и решается, встанет ли
            // строка сбоку от объекта. Порог по САМОМУ ДЛИННОМУ слову абзаца выгонял вниз
            // весь текст, даже когда сбоку спокойно помещались короткие слова — картинка
            // по центру колонки просто разрывала абзац пустой полосой во всю свою высоту.
            float WordBandRequirement(
                List<(string Char, SKRunSegment Format, int GlobalIndex)> word, float widthPt)
            {
                float objectWidthPt = 0f;
                foreach (var (_, format, _) in word)
                    if (format.IsInlineObject && format.ObjectWidthPt > objectWidthPt)
                        objectWidthPt = format.ObjectWidthPt;

                return BandRequirement(widthPt, objectWidthPt);
            }

            float currentW = 0f;
            var currentLine = new SKLineLayout { FirstCharIndex = tokens[0].GlobalIndex };
            var wordBuffer = new List<(string Char, SKRunSegment Format, int GlobalIndex)>();
            float wordWidth = 0f;

            // Раскладывает посчитанные отрезки на текущую (ещё пустую) строку.
            void ApplyBandToCurrentLine()
            {
                if (bandFragments.Count == 0)
                    bandFragments.Add(new SKWrapFragment(0f, textAreaWidthPt));

                var first = bandFragments[0];

                if (hasZones)
                {
                    currentLine.WrapLeftPt = first.LeftPt;
                    currentLine.WrapAreaWidthPt = first.WidthPt;
                    currentLine.WrapExtraTopPt = bandExtraTop;
                }

                currentLine.WrapFragments.Clear();
                currentLine.WrapFragments.Add(first);

                // Абзацный отступ первой строки ужимается до того, что реально влезает
                // в полосу обтекания. Полный отступ применялся как есть: в полосе слева
                // от объекта шириной 195 pt при отступе 191 pt строке оставалось 4 pt,
                // и первый символ принудительно ставился по отступу — под объектом.
                //
                // Ужимаем до половины полосы, а НЕ до (полоса − требование): при
                // привязке к порогу вытеснения отступ схлопывался почти в ноль, стоило
                // полосе оказаться чуть выше порога, и прыгал 191 → 0.7 → 191 при
                // перетаскивании картинки. Два независимых решения не должны делить
                // одну константу.
                lineIndentPt = 0f;
                if (layout.Lines.Count == 0)
                {
                    if (hasZones && layout.FirstLineIndentPt > 0f)
                        layout.FirstLineIndentPt = Math.Min(
                            layout.FirstLineIndentPt, Math.Max(first.WidthPt * 0.5f, 0f));
                    lineIndentPt = layout.FirstLineIndentPt;
                }

                fragIdx = 0;
                currentW = 0f;
                fragEndW = Math.Max(first.WidthPt - lineIndentPt, 1f);
                segmentBreakPending = false;
            }

            // Переход в следующий отрезок этой же строки: текст обходит объект и
            // продолжается за ним. Прыжок входит в координату X сегментов.
            bool AdvanceFragment()
            {
                if (fragIdx + 1 >= bandFragments.Count) return false;

                fragIdx++;
                var fragment = bandFragments[fragIdx];
                currentLine.WrapFragments.Add(fragment);

                currentW = fragment.LeftPt - bandFragments[0].LeftPt - lineIndentPt;
                fragEndW = currentW + fragment.WidthPt;
                segmentBreakPending = true;
                return true;
            }

            bandExtraTop = ComputeBand(
                ZoneY(0f, lineProbeHPt), lineProbeHPt, minBandWidthPt, bandFragments);
            ApplyBandToCurrentLine();

            void StartNewLine(int firstCharIndex, float probeHPt, float requiredWidthPt)
            {
                FinalizeLine(currentLine, layout, lineSpacing);
                lineProbeHPt = probeHPt;
                bandExtraTop = ComputeBand(
                    ZoneY(layout.TotalHeightPt, probeHPt), probeHPt, requiredWidthPt, bandFragments);
                currentLine = new SKLineLayout { FirstCharIndex = firstCharIndex };
                ApplyBandToCurrentLine();
            }

            void FlushWord()
            {
                if (wordBuffer.Count == 0) return;

                float wordProbeHPt = hasZones ? WordProbeHeight(wordBuffer) : lineProbeHPt;
                float wordRequiredPt = hasZones
                    ? WordBandRequirement(wordBuffer, wordWidth)
                    : minBandWidthPt;

                // Строка ещё пуста — полосу под неё ищем по тому слову, которое в неё
                // сейчас пойдёт: по его ширине и его высоте. Полоса, посчитанная по
                // абзацу целиком, отправляла бы вниз даже короткие слова.
                if (hasZones && currentLine.Segments.Count == 0)
                {
                    lineProbeHPt = wordProbeHPt;
                    bandExtraTop = ComputeBand(
                        ZoneY(layout.TotalHeightPt, wordProbeHPt), wordProbeHPt,
                        wordRequiredPt, bandFragments);
                    ApplyBandToCurrentLine();
                }
                else if (hasZones && wordProbeHPt > lineProbeHPt + 0.5f)
                {
                    // В начатой строке картинка уже не поместится по высоте —
                    // переносим её на свою строку с честной полосой.
                    StartNewLine(wordBuffer[0].GlobalIndex, wordProbeHPt, wordRequiredPt);
                }

                // Слово не влезло в текущий отрезок — пробуем следующий отрезок ЭТОЙ ЖЕ
                // строки: при двустороннем обтекании текст перескакивает через объект
                // и продолжается за ним, и только когда отрезки кончились — переносим строку.
                // Переходим только в тот отрезок, куда слово реально влезет: иначе строка
                // числилась бы разорванной, ничего в новый отрезок не поставив, и теряла
                // бы выравнивание по центру и правому краю.
                while (currentW + wordWidth > fragEndW
                    && currentLine.Segments.Count > 0
                    && fragIdx + 1 < bandFragments.Count
                    && bandFragments[fragIdx + 1].WidthPt >= wordWidth
                    && AdvanceFragment())
                {
                }

                if (currentW + wordWidth <= fragEndW || currentLine.Segments.Count == 0 && wordWidth <= fragEndW)
                {
                    if (currentW + wordWidth > fragEndW && currentLine.Segments.Count > 0)
                    {
                        StartNewLine(wordBuffer[0].GlobalIndex, wordProbeHPt, wordRequiredPt);
                    }
                    AppendWordToLine(currentLine, wordBuffer, ref currentW,
                        segmentBreakPending, fragIdx);
                    segmentBreakPending = false;
                    wordBuffer.Clear();
                    wordWidth = 0f;
                    return;
                }

                if (currentLine.Segments.Count > 0)
                {
                    StartNewLine(wordBuffer[0].GlobalIndex, wordProbeHPt, wordRequiredPt);
                }

                foreach (var (ch, format, globalIdx) in wordBuffer)
                {
                    float charWidth = MeasureChar(ch, format);
                    if (currentW + charWidth > fragEndW && currentLine.Segments.Count > 0
                        && !AdvanceFragment())
                    {
                        StartNewLine(globalIdx, wordProbeHPt, wordRequiredPt);
                    }
                    AppendCharToLine(currentLine, ch, format, globalIdx,
                        ref currentW, charWidth, segmentBreakPending, fragIdx);
                    segmentBreakPending = false;
                }

                wordBuffer.Clear();
                wordWidth = 0f;
            }

            foreach (var (ch, format, globalIdx) in tokens)
            {
                if (ch == " " || ch == "\t")
                {
                    FlushWord();

                    // Пробел на границе отрезка не переносит текст за объект: он просто
                    // не рисуется, как хвостовой пробел в конце строки.
                    float spaceWidth = MeasureChar(ch, format);
                    if (currentW + spaceWidth <= fragEndW || currentLine.Segments.Count == 0)
                    {
                        AppendCharToLine(currentLine, ch, format, globalIdx,
                            ref currentW, spaceWidth, segmentBreakPending, fragIdx);
                        segmentBreakPending = false;
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

            // Состояние для гистерезиса следующей пересборки.
            layout.WrapPushedDown = hasZones
                && layout.Lines.Count > 0
                && layout.Lines[0].WrapExtraTopPt > 0.01f;
        }

        private static void AppendWordToLine(
            SKLineLayout line,
            List<(string Char, SKRunSegment Format, int GlobalIndex)> word,
            ref float currentW,
            bool forceNewSegment = false,
            int wrapFragmentIndex = 0)
        {
            bool breakSegment = forceNewSegment;
            foreach (var (ch, format, globalIdx) in word)
            {
                float charWidth = MeasureChar(ch, format);
                AppendCharToLine(line, ch, format, globalIdx, ref currentW, charWidth,
                    breakSegment, wrapFragmentIndex);
                breakSegment = false;
            }
        }

        /// <param name="forceNewSegment">
        /// Символ обязан начать новый сегмент. Нужно после прыжка через обтекаемый объект:
        /// иначе он слился бы с предыдущим сегментом по совпадению формата, и разрыв
        /// строки объектом потерялся бы — текст поехал бы поверх картинки.
        /// </param>
        private static void AppendCharToLine(
            SKLineLayout line,
            string ch,
            SKRunSegment format,
            int globalIdx,
            ref float currentW,
            float charWidth,
            bool forceNewSegment = false,
            int wrapFragmentIndex = 0)
        {
            var lastSeg = forceNewSegment || line.Segments.Count == 0
                ? null
                : line.Segments[^1];

            // Разрываем сегмент на границе пробел/не-пробел: тогда пробелы образуют отдельные
            // сегменты и при выравнивании по ширине между словами можно раздвигать промежутки.
            // Внутри слова и внутри групп пробелов того же формата символы по-прежнему сливаются.
            bool curSpace = ch == " " || ch == "\t";
            bool lastSpace = lastSeg is not null && lastSeg.Text.Length > 0
                && (lastSeg.Text[^1] == ' ' || lastSeg.Text[^1] == '\t');

            // Объект в строке (картинка) всегда занимает отдельный сегмент: слияние
            // с соседним текстом растворило бы ссылку на картинку, и на её месте
            // нарисовался бы символ-заполнитель.
            bool objectInvolved = format.IsInlineObject
                || (lastSeg is not null && lastSeg.IsInlineObject);

            if (!objectInvolved && lastSeg is not null
                && IsSameFormat(lastSeg, format) && curSpace == lastSpace)
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
                    BaselineShiftPt = format.BaselineShiftPt,
                    IsBold = format.IsBold,
                    IsItalic = format.IsItalic,
                    IsUnderline = format.IsUnderline,
                    IsStrikethrough = format.IsStrikethrough,
                    Color = format.Color,
                    HighlightColor = format.HighlightColor,
                    ColorCode = format.ColorCode,
                    HighlightCode = format.HighlightCode,
                    GlobalCharOffset = globalIdx,
                    // Ссылка на картинку и её габарит — часть сегмента: без них строка
                    // получила бы обычный текстовый сегмент с символом-заполнителем
                    // вместо объекта.
                    InlineImageId = format.InlineImageId,
                    ObjectWidthPt = format.ObjectWidthPt,
                    ObjectHeightPt = format.ObjectHeightPt,
                    WrapFragmentIndex = wrapFragmentIndex,
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

            // Метрики одного текста, без учёта габарита картинок: по ним рисуется каретка.
            // Иначе рядом с крупной картинкой каретка растягивалась бы на всю её высоту,
            // хотя печатается текст своего кегля.
            float maxTextAscent = 0f;
            float maxTextDescent = 0f;

            foreach (var seg in line.Segments)
            {
                var typeface = GetOrCreateTypeface(seg.FontFamily, seg.IsBold, seg.IsItalic);
                var font = GetOrCreateFont(typeface, seg.FontSizePt);

                font.GetFontMetrics(out var metrics);

                float ascent = Math.Abs(metrics.Ascent);
                float descent = Math.Abs(metrics.Descent);

                // Шрифтовые метрики берём и у сегмента-картинки: он несёт кегль стиля,
                // поэтому строка из одной картинки всё равно знает высоту своего текста.
                if (ascent > maxTextAscent) maxTextAscent = ascent;
                if (descent > maxTextDescent) maxTextDescent = descent;

                // Картинка в строке стоит на базовой линии и поднимает высоту строки
                // под себя — как крупный глиф. Иначе строка осталась бы высотой в
                // шрифт, а картинка налезла бы на соседние строки.
                if (seg.IsInlineObject && seg.ObjectHeightPt > ascent)
                    ascent = seg.ObjectHeightPt;

                if (ascent > maxAscent) maxAscent = ascent;
                if (descent > maxDescent) maxDescent = descent;

                seg.GlyphMetrics = BuildGlyphMetrics(seg, font);
            }

            float lineHeightBase = maxAscent + maxDescent;
            float lineHeight = lineHeightBase * lineSpacing;
            float baseline = (lineHeight - lineHeightBase) / 2f + maxAscent;

            // Вытеснение строки под обтекаемый объект: зазор входит в высоту параграфа.
            layout.TotalHeightPt += line.WrapExtraTopPt;

            line.Y = layout.TotalHeightPt;
            line.Height = lineHeight;
            line.Baseline = baseline;
            line.TextAscentPt = maxTextAscent;
            line.TextDescentPt = maxTextDescent;

            layout.TotalHeightPt += lineHeight;
            layout.Lines.Add(line);
        }

        private static SKLineLayout BuildEmptyLine(SKTextLayout layout, float lineSpacing)
        {
            var typeface = GetOrCreateTypeface(
                StyleResolver.FallbackFontFamily, false, false);
            var font = GetOrCreateFont(typeface, StyleResolver.FallbackFontSizePt);

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

        /// <summary>
        /// Горизонтальный сдвиг строки по выравниванию относительно начала текстовой области.
        /// Модель как в Word: область первой строки — [абзацный отступ, ширина области], прочих —
        /// [0, ширина области]. По центру строка центрируется внутри своей области (с учётом
        /// абзацного отступа первой строки), по правому краю — упирается в правый край (отступ не
        /// влияет), по левому/ширине — начинается у абзацного отступа (для первой строки).
        /// Общий публичный метод: используется рендером, кареткой, хит-тестом и выделением —
        /// чтобы все считали позицию одинаково.
        /// </summary>
        public static float LineAlignShift(SKTextLayout layout, int lineIndex)
        {
            if (lineIndex < 0 || lineIndex >= layout.Lines.Count) return 0f;
            var line = layout.Lines[lineIndex];
            // Полоса обтекания сужает область строки и сдвигает её левый край:
            // выравнивание работает внутри полосы, а не всей текстовой области.
            float area = line.WrapAreaWidthPt > 0f ? line.WrapAreaWidthPt : layout.TextAreaWidthPt;
            float firstExtra = lineIndex == 0 ? layout.FirstLineIndentPt : 0f;

            // Строка разорвана объектом и идёт по нескольким отрезкам: её ширина включает
            // прыжок через объект, поэтому центрировать и прижимать вправо ПО СТРОКЕ нельзя —
            // текст уехал бы на картинку. Базовая точка такой строки — левый край её первого
            // отрезка; выравнивание по ширине при этом работает: растяжка считается внутри
            // каждого отрезка отдельно (см. JustifyExtraPerSpace).
            if (line.HasWrapFragments)
                return line.WrapLeftPt + firstExtra;

            return line.WrapLeftPt + layout.Alignment switch
            {
                RenderAlignment.Center => firstExtra + (area - firstExtra - line.TextWidth) / 2f,
                RenderAlignment.Right => area - line.TextWidth,
                _ => firstExtra
            };
        }

        /// <summary>
        /// Добавка ширины на один пробел при выравнивании по ширине для строки lineIndex.
        /// Свободное место распределяется только по межсловным пробелам (хвостовые пробелы строки
        /// исключаются — иначе их доля растяжки уходит впустую и последнее слово не достаёт до
        /// правого края). Для последней/одиночной строки и не-Justify — 0.
        /// </summary>
        /// <param name="fragmentIndex">
        /// Отрезок строки, для которого считается растяжка. Строка, разорванная обтекаемым
        /// объектом, растягивается по каждому отрезку отдельно: свободное место у левого
        /// края объекта и у правого — это разные величины, а общая ширина такой строки
        /// включает прыжок через картинку и для расчёта не годится.
        /// </param>
        public static float JustifyExtraPerSpace(
            SKTextLayout layout, int lineIndex, int fragmentIndex = 0)
        {
            if (layout.Alignment != RenderAlignment.Justify) return 0f;
            if (lineIndex < 0 || lineIndex >= layout.Lines.Count) return 0f;
            var line = layout.Lines[lineIndex];
            if (line.IsLastLine) return 0f;

            var segs = line.Segments;
            bool fragmented = line.HasWrapFragments;
            if (fragmented && (fragmentIndex < 0 || fragmentIndex >= line.WrapFragments.Count))
                return 0f;

            bool InFragment(SKRunSegment seg)
                => !fragmented || seg.WrapFragmentIndex == fragmentIndex;

            // Индекс последнего сегмента отрезка, содержащего непробельный символ.
            int lastWordSeg = -1;
            for (int si = segs.Count - 1; si >= 0; si--)
            {
                if (!InFragment(segs[si])) continue;
                bool hasWord = false;
                foreach (var c in segs[si].Text)
                    if (c != ' ' && c != '\t') { hasWord = true; break; }
                if (hasWord) { lastWordSeg = si; break; }
            }
            if (lastWordSeg < 0) return 0f;

            int spaces = 0;
            float contentWidth = 0f;
            for (int si = 0; si <= lastWordSeg; si++)
            {
                if (!InFragment(segs[si])) continue;
                contentWidth += segs[si].Width;
                foreach (var c in segs[si].Text)
                    if (c == ' ' || c == '\t') spaces++;
            }
            if (spaces == 0) return 0f;

            // Абзацный отступ съедает место только в первом отрезке первой строки.
            float firstExtra = lineIndex == 0 && (!fragmented || fragmentIndex == 0)
                ? layout.FirstLineIndentPt
                : 0f;

            // При обтекании строка растягивается до края своей полосы (своего отрезка),
            // а не всей текстовой области.
            float areaW = fragmented
                ? line.WrapFragments[fragmentIndex].WidthPt
                : (line.WrapAreaWidthPt > 0f ? line.WrapAreaWidthPt : layout.TextAreaWidthPt);

            float free = (areaW - firstExtra) - contentWidth;
            if (free <= 0f) return 0f;

            float perSpace = free / spaces;

            // Предел растяжки. В узкой полосе рядом с картинкой в строку попадает два-три
            // слова, и свободное место, размазанное по одному-двум пробелам, разносит их
            // на полколонки: строка выглядит развалившейся, а не выровненной. Как только
            // пробел приходится растягивать сверх предела, отрезок оставляем по левому
            // краю — рваный край читается лучше дыр между словами.
            // Предел растяжки действует только там, где полосу сузила картинка: обычный
            // текст выравнивается как раньше. По умолчанию предела нет — строка тянется
            // до края своей полосы, как в Word.
            //
            // Ограничение имеет смысл только в широкой полосе: в колонке шириной в два
            // слова единственный пробел приходится растягивать в восемь раз и больше,
            // так что любой разумный предел там просто отключил бы выравнивание целиком.
            // Если дыры между словами окажутся неприемлемы — поднимать нужно не предел,
            // а ширину полосы (отступы обтекания или размер картинки).
            bool narrowedByWrap = fragmented || line.WrapAreaWidthPt > 0f;
            if (!narrowedByWrap || MaxSpaceStretch <= 0f) return perSpace;

            float naturalSpacePt = 0f;
            for (int si = 0; si <= lastWordSeg; si++)
            {
                if (!InFragment(segs[si]) || segs[si].IsInlineObject) continue;
                naturalSpacePt = MeasureChar(" ", segs[si]);
                break;
            }

            if (naturalSpacePt > 0f && perSpace > naturalSpacePt * MaxSpaceStretch)
                return 0f;

            return perSpace;
        }

        /// <summary>
        /// Предел растяжки пробела при выравнивании по ширине в полосе обтекания:
        /// сколько СВОИХ ширин пробел может добрать сверх нормальной.
        ///
        /// 0 — предела нет: любой отрезок тянется до края своей полосы, даже когда слова
        /// в нём расходятся к самым краям. Так ведёт себя Word, и так же выглядит ровнее
        /// в узких полосах обтекания: короткий кусок, оставленный по левому краю, читается
        /// как обрубок рядом с выровненными соседями.
        ///
        /// Положительное значение возвращает откат на левый край для строк, которым нужно
        /// растянуть пробел сверх предела. Обычного текста, вне полос обтекания, предел
        /// не касается ни при каком значении.
        /// </summary>
        private const float MaxSpaceStretch = 0f;

        // Индекс последнего сегмента строки, содержащего непробельный символ. -1 — таких нет.
        private static int LastContentSegIndex(SKLineLayout line)
        {
            int last = -1;
            for (int si = 0; si < line.Segments.Count; si++)
            {
                var s = line.Segments[si];
                for (int k = 0; k < s.Text.Length; k++)
                    if (s.Text[k] != ' ' && s.Text[k] != '\t') { last = si; break; }
            }
            return last;
        }

        // Запас заливки справа в pt: перекрывает вынос рисунка глифа за его advance-ширину,
        // иначе последняя буква закрашенного фрагмента остаётся закрытой не целиком.
        // Внутри сплошной заливки запас перекрывается прямоугольником следующего сегмента.
        // Публичная: тем же запасом пользуется отрисовка выделения в DocumentCanvas.
        public const float HighlightRightOverhangPt = 1.5f;

        // Ширина заливки сегмента с обрезкой хвостовых пробелов в конце визуальной строки:
        // сегменты целиком из хвостовых пробелов не заливаются, в последнем содержательном
        // сегменте хвостовые пробелы отсекаются. Для внутренних сегментов — полная ширина.
        // Обрезка действует только на строках с мягким переносом: на последней строке
        // абзаца хвостовые пробелы никуда не переносятся, стоят в пределах строки и
        // закрашиваются целиком (как в Word).
        private static float SegHighlightWidth(SKLineLayout line, int segIndex, int lastContentSeg)
        {
            var seg = line.Segments[segIndex];
            if (line.IsLastLine) return seg.Width;
            if (lastContentSeg < 0) return seg.Width;
            if (segIndex > lastContentSeg) return 0f;
            if (segIndex < lastContentSeg) return seg.Width;
            float right = 0f;
            for (int k = 0; k < seg.Text.Length && k < seg.GlyphMetrics.Length; k++)
                if (seg.Text[k] != ' ' && seg.Text[k] != '\t') right = seg.GlyphMetrics[k].Right;
            return right > 0f ? right : seg.Width;
        }

        // ── Измерение текста ──────────────────────────────────────────────

        private static float MeasureChar(string ch, SKRunSegment format)
        {
            // Объект в строке занимает собственный габарит, а не ширину глифа
            // символа-заполнителя.
            if (format.IsInlineObject) return format.ObjectWidthPt;

            var typeface = GetOrCreateTypeface(format.FontFamily, format.IsBold, format.IsItalic);
            var font = GetOrCreateFont(typeface, format.FontSizePt);
            return font.MeasureText(ch);
        }

        private static SKGlyphMetrics[] BuildGlyphMetrics(SKRunSegment seg, SKFont font)
        {
            if (string.IsNullOrEmpty(seg.Text))
                return Array.Empty<SKGlyphMetrics>();

            // Объект в строке — один «глиф» со своей шириной. Хит-тест, каретка и
            // выделение работают с ним как с обычным символом.
            if (seg.IsInlineObject)
            {
                return new[]
                {
                    new SKGlyphMetrics
                    {
                        CharIndex = seg.GlobalCharOffset,
                        X = 0f,
                        Width = seg.ObjectWidthPt
                    }
                };
            }

            // GetGlyphWidths измеряет все символы за один нативный вызов Skia.
            // Было: N вызовов font.MeasureText(char.ToString()) = N string аллокаций
            // и N обращений к glyph cache по одному символу.
            // Стало: 1 вызов GetGlyphWidths на весь сегмент = 0 string аллокаций.
            var glyphIds = font.GetGlyphs(seg.Text);
            var widths = font.GetGlyphWidths(glyphIds);

            var glyphs = new SKGlyphMetrics[seg.Text.Length];
            float x = 0f;

            for (int i = 0; i < seg.Text.Length; i++)
            {
                float width = (widths is not null && i < widths.Length) ? widths[i] : 0f;
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

            if (autoCount > 0)
            {
                float available = Math.Max(textAreaWidthPt - usedFixedPt, autoCount * 10f);
                float autoWidth = available / autoCount;
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
        /// Публичная обёртка RenderCellBorders для DocumentCanvas.
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

        private static void DrawBorderLine(
            SKCanvas canvas,
            SKTableBorderLineLayout border,
            float x1, float y1, float x2, float y2,
            float canvasScale = 1f)
        {
            if (border.Style == 3) return; // None

            if (!SKColor.TryParse(border.Color, out var color))
                color = SKColors.Black;

            float minWidthPt = canvasScale > 0f ? 1f / canvasScale : 0.75f;
            float strokeWidth = Math.Max(minWidthPt, border.WidthPt > 0f ? border.WidthPt : minWidthPt);

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
            && a.BaselineShiftPt == b.BaselineShiftPt
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

        private static SKFont GetOrCreateFont(SKTypeface typeface, float sizePt)
        {
            // sizePt хранится как целое число тысячных чтобы избежать float-ключей.
            var key = (typeface.Handle, (int)(sizePt * 1000));
            return _fontCache.GetOrAdd(key, _ => new SKFont(typeface, sizePt));
        }

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

        /// <summary>
        /// Ищет шрифт для символа с указанным кодпоинтом.
        /// Порядок: пользовательская карта скриптов → системный MatchCharacter → null.
        /// MatchCharacter кешируется; пользовательская карта проверяется всегда напрямую.
        /// Декоративные шрифты (Marlett, Wingdings и пр.) исключаются из результата.
        /// </summary>
        private static string? FindFallbackFamily(int codepoint, StyleResolver? styles)
        {
            // Пользовательская карта скриптов имеет приоритет над системным фолбэком.
            if (styles is not null && styles.ScriptFontMap.Count > 0)
            {
                string? scriptName = GetScriptName(codepoint);
                if (scriptName != null && styles.ScriptFontMap.TryGetValue(scriptName, out var preferred)
                    && !string.IsNullOrEmpty(preferred))
                    return preferred;
            }

            if (_fallbackFamilyCache.TryGetValue(codepoint, out var cached))
                return cached;

            SKTypeface? fallback = null;
            try
            {
                fallback = SKFontManager.Default.MatchCharacter(codepoint);
            }
            catch
            {
                // MatchCharacter может бросить исключение на некоторых конфигурациях.
            }

            string? result = null;
            if (fallback != null && !IsDecorationFont(fallback.FamilyName))
                result = fallback.FamilyName;

            _fallbackFamilyCache.TryAdd(codepoint, result);
            return result;
        }

        /// <summary>
        /// Определяет имя Unicode-скрипта по кодпоинту.
        /// Используется для поиска в пользовательской карте шрифтов.
        /// </summary>
        private static string? GetScriptName(int codepoint)
        {
            if (codepoint >= 0x0370 && codepoint <= 0x03FF) return "Greek";
            if (codepoint >= 0x0400 && codepoint <= 0x052F) return "Cyrillic";
            if (codepoint >= 0x0590 && codepoint <= 0x05FF) return "Hebrew";
            if (codepoint >= 0x0600 && codepoint <= 0x06FF) return "Arabic";
            if (codepoint >= 0x0900 && codepoint <= 0x097F) return "Devanagari";
            if (codepoint >= 0x0E00 && codepoint <= 0x0E7F) return "Thai";
            if (codepoint >= 0x3040 && codepoint <= 0x309F) return "Japanese";
            if (codepoint >= 0x30A0 && codepoint <= 0x30FF) return "Japanese";
            if (codepoint >= 0x4E00 && codepoint <= 0x9FFF) return "CJK";
            if (codepoint >= 0xAC00 && codepoint <= 0xD7AF) return "Korean";
            return null;
        }

        /// <summary>
        /// Возвращает true для декоративных и символьных шрифтов Windows.
        /// Такие шрифты отображают ASCII-символы как иконки/стрелки,
        /// поэтому не подходят для текстового фолбэка.
        /// </summary>
        private static bool IsDecorationFont(string familyName)
        {
            return familyName.Equals("Marlett", StringComparison.OrdinalIgnoreCase)
                || familyName.StartsWith("Wingdings", StringComparison.OrdinalIgnoreCase)
                || familyName.StartsWith("Webdings", StringComparison.OrdinalIgnoreCase)
                || familyName.IndexOf("MDL2", StringComparison.OrdinalIgnoreCase) >= 0
                || familyName.IndexOf("Symbol", StringComparison.OrdinalIgnoreCase) >= 0
                || familyName.IndexOf("Dingbats", StringComparison.OrdinalIgnoreCase) >= 0
                || familyName.IndexOf("Emoji", StringComparison.OrdinalIgnoreCase) >= 0;
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
            int lineFrom, int lineTo,
            string? markerText = null,
            float markerHangingPt = 0f,
            SKColor markerColor = default,
            float markerMinGapPt = 0f)
        {
            if (layout.Lines.Count == 0)
            {
                // Пустой элемент списка (например только что созданный) всё равно показывает маркер.
                if (!string.IsNullOrEmpty(markerText))
                    DrawListMarker(canvas, layout, paraX, paraY, 0f, markerText!, markerHangingPt, markerColor, markerMinGapPt);
                return;
            }

            int clampedFrom = Math.Max(0, lineFrom);
            int clampedTo = Math.Min(lineTo, layout.Lines.Count);
            float yBase = clampedFrom < layout.Lines.Count
                                    ? layout.Lines[clampedFrom].Y : 0f;

            // Маркер списка рисуется один раз — на первой строке абзаца (слайс с lineFrom == 0).
            // На страницах-продолжениях (clampedFrom > 0) маркер не повторяется — как в Word.
            if (!string.IsNullOrEmpty(markerText) && clampedFrom == 0)
                DrawListMarker(canvas, layout, paraX, paraY, yBase, markerText!, markerHangingPt, markerColor, markerMinGapPt);

            // Прямоугольник всего абзаца — для градиента текста в режиме «весь блок».
            // line.Y == 0 отображается в paraY - yBase, от него и считаем верх блока.
            float blockTop = paraY - yBase;
            var blockRect = new SKRect(
                paraX + layout.LeftIndentPt,
                blockTop,
                paraX + layout.LeftIndentPt + layout.TextAreaWidthPt,
                blockTop + layout.TotalHeightPt);

            for (int i = clampedFrom; i < clampedTo; i++)
            {
                var line = layout.Lines[i];
                float lineY = paraY + (line.Y - yBase);

                // Единый сдвиг строки по выравниванию (центр/право + абзацный отступ первой
                // строки по вордовской модели). Тот же расчёт у каретки/хит-теста/выделения.
                float lineShift = LineAlignShift(layout, i);

                // Растяжение по ширине: распределяем свободное место по межсловным пробелам.
                // У строки, разорванной обтекаемым объектом, каждый отрезок растягивается
                // сам по себе — своя добавка и свой накопленный сдвиг.
                int justifyFragment = 0;
                float extraPerSpace = JustifyExtraPerSpace(layout, i, justifyFragment);
                bool doJustify = extraPerSpace > 0f;
                float justifyShift = 0f;

                // Прямоугольник строки — для градиента текста в режиме «построчно».
                float lineStartX = paraX + lineShift + (line.Segments.Count > 0 ? line.Segments[0].X : 0f);
                var lineRect = new SKRect(lineStartX, lineY, lineStartX + line.TextWidth, lineY + line.Height);

                int lastContentSeg = LastContentSegIndex(line);
                int segIdx = -1;
                foreach (var seg in line.Segments)
                {
                    segIdx++;

                    // Переход в следующий отрезок разорванной строки: накопленный сдвиг
                    // растяжки обнуляется, добавка берётся своя — иначе текст справа от
                    // картинки уехал бы на сдвиг, набранный слева от неё.
                    if (seg.WrapFragmentIndex != justifyFragment)
                    {
                        justifyFragment = seg.WrapFragmentIndex;
                        extraPerSpace = JustifyExtraPerSpace(layout, i, justifyFragment);
                        doJustify = extraPerSpace > 0f;
                        justifyShift = 0f;
                    }

                    float segX = paraX + seg.X + lineShift + justifyShift;
                    float baseY = lineY + line.Baseline;

                    // Объект в строке (картинка): рисует канвас через обработчик, сам сегмент
                    // текстом не рисуется. Без этой ветки на месте картинки печатался бы
                    // её символ-заполнитель — пустой квадрат.
                    // Текстовые слои (заливка, подчёркивание, зачёркивание, градиент букв)
                    // к объекту не применяются.
                    if (seg.IsInlineObject)
                    {
                        DrawInlineObject?.Invoke(canvas, seg, segX, baseY);
                        continue;
                    }

                    // Над/подстрочный: смещаем базовую линию сегмента (вверх для надстрочного,
                    // вниз для подстрочного). Для обычного текста BaselineShiftPt = 0.
                    float segBaseY = baseY - seg.BaselineShiftPt;

                    // Число пробелов в сегменте: используется и для растяжки заливки,
                    // и для накопления сдвига последующих сегментов при выравнивании по ширине.
                    int segSpaces = 0;
                    foreach (var c in seg.Text)
                        if (c == ' ' || c == '\t') segSpaces++;

                    // Задник за текстом: плоский цвет либо градиент по прямоугольнику сегмента.
                    // Ширина обрезается по хвостовым пробелам в конце строки — иначе заливка
                    // тянется до правого поля и «растёт» при вводе пробелов.
                    bool hlGradient = IsGradientCode(seg.HighlightCode);
                    float hlWidth = SegHighlightWidth(line, segIdx, lastContentSeg);
                    // При выравнивании по ширине межсловные пробелы визуально шире на добавку
                    // растяжки — расширяем заливку на неё, иначе между словами остаются
                    // незакрашенные щели. Хвостовые пробелы строки (segIdx >= lastContentSeg)
                    // по-прежнему не заливаются.
                    if (doJustify && segSpaces > 0 && segIdx < lastContentSeg)
                        hlWidth += segSpaces * extraPerSpace;
                    // Запас справа только для реально рисуемой заливки (hlWidth > 0):
                    // хвостовые пробелы с нулевой шириной заливки не появляются.
                    if (hlWidth > 0f)
                        hlWidth += HighlightRightOverhangPt;
                    if (hlWidth > 0f && (seg.HighlightColor != SKColors.Transparent || hlGradient))
                    {
                        using var hlPaint = new SKPaint { Color = seg.HighlightColor };
                        SKShader? hlShader = null;
                        if (hlGradient)
                        {
                            var hlSpec = GradientSpec.Parse(seg.HighlightCode);
                            hlPaint.Color = GradientShaderFactory.SolidColor(hlSpec);
                            var hlRect = new SKRect(segX, lineY, segX + hlWidth, lineY + line.Height);
                            hlShader = GradientShaderFactory.BuildShader(hlSpec, hlRect);
                            hlPaint.Shader = hlShader;
                        }
                        canvas.DrawRect(segX, lineY, hlWidth, line.Height, hlPaint);
                        hlShader?.Dispose();
                    }

                    // Цвет либо градиент букв. Для одноцвета путь прежний — без шейдера.
                    SKColor textColor = seg.Color;
                    SKShader? textShader = null;
                    if (IsGradientCode(seg.ColorCode))
                    {
                        var spec = GradientSpec.Parse(seg.ColorCode);
                        textColor = GradientShaderFactory.SolidColor(spec);
                        var rect = spec.TextFill == GradientTextFill.PerLine ? lineRect : blockRect;
                        textShader = GradientShaderFactory.BuildShader(spec, rect);
                    }

                    var typeface = GetOrCreateTypeface(seg.FontFamily, seg.IsBold, seg.IsItalic);
                    var font = GetOrCreateFont(typeface, seg.FontSizePt);
                    using var paint = new SKPaint
                    {
                        Color = textColor,
                        IsAntialias = true
                    };
                    if (textShader != null) paint.Shader = textShader;

                    canvas.DrawText(seg.Text, segX, segBaseY, font, paint);

                    if (seg.IsUnderline)
                    {
                        using var uPaint = new SKPaint
                        {
                            Color = textColor,
                            StrokeWidth = Math.Max(0.5f, seg.FontSizePt * 0.05f),
                            IsAntialias = true
                        };
                        if (textShader != null) uPaint.Shader = textShader;
                        float underlineY = segBaseY + seg.FontSizePt * 0.12f;
                        canvas.DrawLine(segX, underlineY, segX + seg.Width, underlineY, uPaint);
                    }

                    if (seg.IsStrikethrough)
                    {
                        using var sPaint = new SKPaint
                        {
                            Color = textColor,
                            StrokeWidth = Math.Max(0.5f, seg.FontSizePt * 0.05f),
                            IsAntialias = true
                        };
                        if (textShader != null) sPaint.Shader = textShader;
                        float strikeY = segBaseY - seg.FontSizePt * 0.3f;
                        canvas.DrawLine(segX, strikeY, segX + seg.Width, strikeY, sPaint);
                    }

                    textShader?.Dispose();

                    // После сегмента сдвигаем следующие на накопленную добавку по его пробелам —
                    // так растягиваются промежутки между словами при выравнивании по ширине.
                    if (doJustify)
                        justifyShift += segSpaces * extraPerSpace;
                }
            }
        }

        /// <summary>
        /// Рисует маркер списка слева от текста первой строки абзаца.
        /// paraX — левый край текста (margin + отступ текста списка).
        /// markerHangingPt — выступ маркера: маркер рисуется на markerHangingPt левее текста.
        /// Гарнитура и кегль маркера берутся из первого сегмента строки (совпадают с текстом),
        /// для пустого элемента — фолбэк-шрифт.
        /// </summary>
        private static void DrawListMarker(
            SKCanvas canvas, SKTextLayout layout,
            float paraX, float paraY, float yBase,
            string markerText, float markerHangingPt, SKColor markerColor,
            float markerMinGapPt)
        {
            if (layout.Lines.Count == 0) return;
            var line = layout.Lines[0];

            string family = StyleResolver.FallbackFontFamily;
            float sizePt = StyleResolver.FallbackFontSizePt;
            if (line.Segments.Count > 0)
            {
                family = line.Segments[0].FontFamily;
                sizePt = line.Segments[0].FontSizePt;
            }

            var typeface = GetOrCreateTypeface(family, false, false);
            var font = GetOrCreateFont(typeface, sizePt);

            // Некоторые символы маркеров (например ➤) могут отсутствовать в основном шрифте —
            // подставляем системный фолбэк, иначе вместо маркера рисуется .notdef-квадрат.
            int mcp = markerText.Length > 0 ? markerText[0] : 0;
            if (mcp >= 0x0080 && typeface.GetGlyph(mcp) == 0)
            {
                if (!_fallbackFamilyCache.TryGetValue(mcp, out var fb))
                {
                    using var fm = SKFontManager.Default.MatchCharacter(mcp);
                    fb = fm?.FamilyName;
                    _fallbackFamilyCache[mcp] = fb;
                }
                if (!string.IsNullOrEmpty(fb))
                {
                    typeface = GetOrCreateTypeface(fb!, false, false);
                    font = GetOrCreateFont(typeface, sizePt);
                }
            }

            float lineY = paraY + (line.Y - yBase);
            float baseY = lineY + line.Baseline;
            // Цифра/символ маркера рисуется строго по своему левому краю (там же, где стрелка на
            // линейке). Зазор до текста обеспечивает отступ первой строки, вычисленный в раскладке
            // по ширине цифры, — поэтому здесь маркер не сдвигаем.
            float markerX = paraX - markerHangingPt;

            using var paint = new SKPaint
            {
                Color = markerColor == default ? SKColors.Black : markerColor,
                IsAntialias = true
            };
            canvas.DrawText(markerText, markerX, baseY, font, paint);
        }
    }
}