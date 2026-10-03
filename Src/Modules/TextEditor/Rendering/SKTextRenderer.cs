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

        // Кеш SKFont — свой на каждый поток, а не один на всех.
        //
        // SKFont не рассчитан на две руки сразу: раскладка меряет им текст на потоке
        // прогрева, рисование идёт на потоке композиции, и один и тот же объект в
        // обеих руках роняет процесс внутри Skia (sk_font_text_to_glyphs). Поймать
        // это тем труднее, что падает не там, где ошиблись, а там, где в этот миг
        // рисовали, — например, на дорожке точек в оглавлении.
        //
        // Свой набор на поток снимает вопрос целиком и стоит недорого: SKFont — тонкая
        // обёртка, а потоков, которые верстают и рисуют, всего несколько.
        [ThreadStatic]
        private static Dictionary<(IntPtr Typeface, int SizeMils), SKFont>? _fontCache;

        // Кеш фолбэк-гарнитур по кодпоинту Unicode.
        // Заполняется при первом обращении к символу не поддержанному основным шрифтом.
        // null — система не нашла ни одного шрифта с нужным глифом.
        private static readonly ConcurrentDictionary<int, string?> _fallbackFamilyCache = new();

        // Кеш системных гарнитур для знаков-символов текста (FindSymbolFallback): по
        // знаку и начертанию, в котором его не нашлось. Отдельный от кеша маркеров:
        // фильтр гарнитур у них разный.
        private static readonly ConcurrentDictionary<(int Codepoint, string Family, bool Bold, bool Italic),
            (string Family, bool Bold, bool Italic)?> _symbolFallbackFamilyCache = new();

        /// <summary>
        /// Сбрасывает нативные SKFont объекты из кеша.
        /// SKFont — нативные объекты SkiaSharp, накапливаются при смене вкладок.
        /// SKTypeface не сбрасываем — они тяжёлые для повторной загрузки.
        /// </summary>
        public static void TrimFontCache()
        {
            // Ничего не освобождается вручную, только отпускаются ссылки.
            //
            // Освободить шрифт или гарнитуру может понадобиться ровно в тот миг, когда
            // ими рисует поток композиции: сброс кеша зовут при смене документа, а
            // кадр в это время уже в работе. Нативный объект, убитый под рукой, — это
            // падение процесса, а не сэкономленная память. Остальное сделает сборщик,
            // когда на них действительно никто не смотрит.
            _fontCache?.Clear();
            _typefaceCache.Clear();
            _fallbackFamilyCache.Clear();
            _symbolFallbackFamilyCache.Clear();
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

            // Отступы абзаца ужимаются вместе с листом чтения — тем же множителем, что
            // поля, картинки и колонки таблиц. Лист книги уже печатного, и печатный
            // отступ съедает у него куда большую долю колонки, а вынесенный влево
            // номер списка и вовсе уезжает за край листа: вынос отсчитывается от
            // текстовой зоны, а она на карманном формате вдвое ближе к краю.
            //
            // В правке множитель равен единице — там лист документа, и трогать его
            // отступы нельзя ничем.
            float indentScale = ReadingContentScale;

            float docLeftIndentPt = (float)(para.Properties.LeftIndent
                                        ?? styles.ResolveLeftIndent(styleName));
            float docRightIndentPt = (float)(para.Properties.RightIndent
                                        ?? styles.ResolveRightIndent(styleName));
            float docFirstLineIndentPt = (float)(para.Properties.FirstLineIndent ?? 0.0);

            float leftIndentPt = docLeftIndentPt * indentScale;
            float rightIndentPt = docRightIndentPt * indentScale;
            float firstLineIndentPt = docFirstLineIndentPt * indentScale;

            // Элемент списка. Без собственного левого отступа берём отступ по уровню.
            // Затем меряем ширину цифры/символа маркера и отодвигаем текст ПЕРВОЙ строки так,
            // чтобы между правым краем цифры и текстом всегда был зазор (MarkerTextMinGapPt).
            // Стрелка метки стоит по левому краю цифры (позиция markerAbs); двигая её вправо,
            // пользователь сдвигает и текст первой строки. Строки 2+ идут по левому отступу.
            // Первую строку занял номер, текст ушёл на вторую (см. ниже условие предела).
            bool markerOwnsFirstLine = false;

            // Шрифт текста, которым набран номер, и метрики шрифта самого номера:
            // по ним номер рисуется и первая строка получает свою высоту.
            string? markerFamily = null;
            float markerSizePt = 0f;
            float markerLineTopPt = 0f;
            float markerLineDescentPt = 0f;

            var listProps = para.ListProperties;
            if (listProps is not null && listProps.MarkerType != ListMarkerType.None)
            {
                if (para.Properties.LeftIndent is null)
                {
                    docLeftIndentPt = (float)listProps.EffectiveTextIndentPt();
                    leftIndentPt = docLeftIndentPt * indentScale;
                }

                // Позиция номера считается в документных пунктах и ужимается тем же
                // множителем: она абсолютна от поля, и ужатая наполовину зона с
                // печатным номером разъехались бы — номер оказался бы левее листа.
                double markerAbs = (listProps.MarkerIndentPt
                    ?? Math.Max(0.0, docLeftIndentPt - ListProperties.DefaultHangingPt))
                    * indentScale;


                string markerText = listProps.ComputedMarkerText ?? string.Empty;

                // Уровень списка из Word: номер стоит у точки номера по своему выравниванию,
                // а текст отделяет от него табуляция, пробел или ничего — как у Word.
                var wordLevel = listProps.WordLevelAt(listProps.Level);
                bool wordSpacing = wordLevel is not null && wordLevel.Suffix != ListMarkerSuffix.Gap;

                if (markerText.Length > 0)
                {
                    // Номер набирается шрифтом текста пункта, а не шрифтом стиля абзаца.
                    // У пункта с собственным кеглем ширина номера, посчитанная по стилю,
                    // расходилась с нарисованной: номер в 12 пунктов мерялся как номер в
                    // 14, казался шире выступа, и текст уходил к следующей отметке шага
                    // табуляции вместо отступа пункта.
                    var markerTextFont = ResolveMarkerTextFont(para, styleName, styles);
                    markerFamily = markerTextFont.Family;
                    markerSizePt = markerTextFont.SizePt;

                    // Один и тот же шрифт и для ширины, и для отрисовки (DrawListMarker):
                    // шрифт уровня Word, а для знака, которого в шрифте нет, — подстановка.
                    SKFont mfont = ResolveMarkerDrawFont(
                        markerText, markerFamily, markerSizePt, wordLevel?.FontFamily,
                        out string measuredText);

                    float markerW = mfont.MeasureText(measuredText);

                    mfont.GetFontMetrics(out var markerMetrics);
                    markerLineTopPt = Math.Abs(markerMetrics.Ascent) + Math.Abs(markerMetrics.Leading);
                    markerLineDescentPt = Math.Abs(markerMetrics.Descent);

                    double offset;
                    if (wordSpacing)
                    {
                        // Точка номера — начало, середина или конец номера.
                        double markerStart = wordLevel!.Alignment switch
                        {
                            ListMarkerAlignment.Right => markerAbs - markerW,
                            ListMarkerAlignment.Center => markerAbs - markerW / 2.0,
                            _ => markerAbs
                        };
                        double markerEnd = markerStart + markerW;

                        double textStart = wordLevel.Suffix switch
                        {
                            ListMarkerSuffix.Space => markerEnd + mfont.MeasureText(" "),
                            ListMarkerSuffix.Nothing => markerEnd,
                            _ => WordNumberTabStop(
                                markerEnd, leftIndentPt, docFirstLineIndentPt < 0.0,
                                para.Properties.TabStops, styles.DefaultTabStopPt, indentScale)
                        };

                        offset = textStart - leftIndentPt;
                    }
                    else
                    {
                        // Текст ПЕРВОЙ строки идёт сразу после номера: номер + ширина + зазор.
                        // Позиция абсолютна (от поля) и от левого края строк 2+ НЕ зависит —
                        // значение может быть отрицательным (первая строка левее строк 2+).
                        offset = markerAbs + markerW + listProps.MarkerTextMinGapPt - leftIndentPt;
                    }

                    // Не пускаем текст первой строки за правый край текстовой зоны: оставляем
                    // минимум места под текст, иначе строка уезжала бы за пределы страницы.
                    const double MinFirstLineWidthPt = 36.0;
                    double maxOffset = availableWidthPt - leftIndentPt - rightIndentPt - MinFirstLineWidthPt;
                    if (offset > maxOffset)
                    {
                        // Справа от номера тексту уже не остаётся места. Прежний вариант
                        // обрезал отступ по пределу, и первая строка ложилась поверх номера.
                        // Вместо этого отдаём первую строку номеру целиком, а текст начинаем
                        // со второй по левому отступу — так же поступает Word. Узкая ячейка,
                        // крупный кегль или утащенный вправо номер приводят сюда штатно.
                        markerOwnsFirstLine = true;
                        offset = 0;
                    }
                    firstLineIndentPt = (float)offset;

                    listProps.ComputedMarkerWidthPt = markerW;
                    listProps.ComputedFirstLineOffsetPt = offset;
                    listProps.ComputedMarkerIndentPt = markerAbs;
                }
                else if (wordSpacing && wordLevel!.Suffix == ListMarkerSuffix.Tab)
                {
                    // Пустой маркер Word: за ним всё равно стоит табуляция, и текст первой
                    // строки встаёт туда же, куда встал бы за номером, — на отступ текста.
                    double textStart = WordNumberTabStop(
                        markerAbs, leftIndentPt, docFirstLineIndentPt < 0.0,
                        para.Properties.TabStops, styles.DefaultTabStopPt, indentScale);
                    double offset = textStart - leftIndentPt;
                    firstLineIndentPt = (float)offset;
                    listProps.ComputedMarkerWidthPt = 0;
                    listProps.ComputedFirstLineOffsetPt = offset;
                    listProps.ComputedMarkerIndentPt = markerAbs;
                }
                else
                {
                    firstLineIndentPt = 0f;
                    listProps.ComputedMarkerWidthPt = 0;
                    listProps.ComputedFirstLineOffsetPt = 0;
                    listProps.ComputedMarkerIndentPt = markerAbs;
                }
            }

            // Внутри ячейки дефолтный SpaceBefore/SpaceAfter = 0.
            // Интервал применяется только если явно задан в свойствах параграфа.
            float spaceBeforePt = (float)(para.Properties.SpaceBefore
                                        ?? (isCell ? 0.0 : (double)styles.ResolveSpaceBefore(styleName)));
            float spaceAfterPt = (float)(para.Properties.SpaceAfter
                                        ?? (isCell ? 0.0 : (double)styles.ResolveSpaceAfter(styleName)));

            // Абзац не добавляет интервал к соседу своего стиля (ContextualSpacingRules):
            // вывод о соседях сделан до раскладки и лежит в самом абзаце. Снимается только
            // интервал — место под рамку абзаца ниже прибавляется как обычно.
            if (para.SuppressSpaceBefore) spaceBeforePt = 0f;
            if (para.SuppressSpaceAfter) spaceAfterPt = 0f;

            // Рамка абзаца отодвигает соседей так же, как интервалы: верхняя линия с
            // зазором до текста встаёт над первой строкой, нижняя — под последней. Их
            // место прибавляется к интервалам раскладки, и разбивка на страницы,
            // попадание мышью и каретка учитывают рамку без особых случаев. Боковые
            // линии стоят в поле отступа и ширину строк не меняют — как в Word.
            var borders = BuildBordersLayout(para.Properties.Borders, indentScale);
            if (borders is not null)
            {
                spaceBeforePt += borders.Top?.ExtentPt ?? 0f;
                spaceAfterPt += borders.Bottom?.ExtentPt ?? 0f;
            }

            // Межстрочный интервал несёт не только значение, но и правило: «множитель»,
            // «точно» и «минимум» дают разную высоту строки, и без правила значение
            // «точно 14 пт» читалось как четырнадцатикратный множитель.
            var lineSpacing = para.Properties.LineSpacingValue.HasValue
                ? new SKLineSpacing(
                    para.Properties.LineSpacingRule ?? Models.Styles.LineSpacingRule.Auto,
                    (float)para.Properties.LineSpacingValue.Value)
                : new SKLineSpacing(
                    styles.ResolveLineSpacingRule(styleName),
                    styles.ResolveLineSpacing(styleName));

            // Конвертируем TextAlignment из модели в Core enum через int.
            // Значения намеренно совпадают: Left=0, Center=1, Right=2, Justify=3, Distribute=4.
            RenderAlignment alignment = para.Properties.Alignment.HasValue
                ? (RenderAlignment)(int)para.Properties.Alignment.Value
                : styles.ResolveAlignment(styleName);

            // В абзаце справа налево Word читает «по левому» и «по правому» от начала
            // строки: w:jc="right" у такого абзаца — к концу строки, то есть к левому
            // краю листа. Модель хранит значение как в файле, а на лист оно ложится
            // зеркально.
            bool rightToLeft = para.Properties.RightToLeft;
            if (rightToLeft)
            {
                if (alignment == RenderAlignment.Left) alignment = RenderAlignment.Right;
                else if (alignment == RenderAlignment.Right) alignment = RenderAlignment.Left;
            }

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
                MarkerOwnsFirstLine = markerOwnsFirstLine,
                MarkerAlignment = (int)(listProps?.WordLevelAt(listProps.Level)?.Alignment ?? ListMarkerAlignment.Left),
                MarkerFontFamily = listProps?.WordLevelAt(listProps.Level)?.FontFamily,
                MarkerTextFontFamily = markerFamily,
                MarkerTextFontSizePt = markerSizePt,
                MarkerLineTopPt = markerLineTopPt,
                MarkerLineDescentPt = markerLineDescentPt,
                Alignment = alignment,
                Borders = borders,
                ShadingColor = string.IsNullOrWhiteSpace(para.Properties.ShadingColor)
                    ? null
                    : para.Properties.ShadingColor,
                ShadingPattern = string.IsNullOrWhiteSpace(para.Properties.ShadingPattern)
                    ? null
                    : para.Properties.ShadingPattern,
                ShadingPatternColor = string.IsNullOrWhiteSpace(para.Properties.ShadingPatternColor)
                    ? null
                    : para.Properties.ShadingPatternColor,
                AllowsJustifyShrink = (alignment == RenderAlignment.Justify || alignment == RenderAlignment.Distribute)
                    && styles.JustifyWithShrinking,
                SpaceBeforeSuppressed = para.SuppressSpaceBefore,
                SpaceAfterSuppressed = para.SuppressSpaceAfter,
                LineTextAlignment = (int)para.Properties.LineTextAlignment,
                IsRightToLeft = rightToLeft,
                HasBidiText = rightToLeft || BidiResolver.HasRightToLeft(para.GetPlainText())
            };

            var tokens = CollectTokens(para, styleName, styles, InlineImageSize);

            // Пустой абзац рисуется шрифтом своего стиля. Запасной Times New Roman 14
            // давал пустой строке чужую высоту, и пустые строки между блоками текста
            // занимали на листе не столько же места, сколько в Word.
            var emptyLineFormat = BuildEmptyLineFormat(para, styleName, styles);

            WrapTokensToLines(
                tokens, layout, textWidthPt, lineSpacing,
                wrapZones, wrapPreferPushDown, wrapPages, emptyLineFormat,
                styles.BreakOnHyphen,
                // Свои позиции табуляции абзаца берут верх над шагом по умолчанию;
                // нет своих — символ табуляции идёт к ближайшей отметке шага.
                para.Properties.TabStops, styles.DefaultTabStopPt);
            layout.TextLength = GetPlainTextLength(para);

            // Строки со смешанным направлением письма тянутся по ширине здесь же, при
            // вёрстке: их куски стоят на листе не в порядке текста, и растяжка, которую
            // отрисовка копит по порядку текста, к ним не подходит.
            if (layout.HasBidiText
                && (alignment == RenderAlignment.Justify || alignment == RenderAlignment.Distribute))
                JustifyReorderedLines(layout);

            // Растянутое выравнивание: последнюю строку, которую по ширине не тянут,
            // растягивает разводка букв.
            if (alignment == RenderAlignment.Distribute && layout.Lines.Count > 0)
                DistributeLastLine(layout, layout.Lines.Count - 1);

            // Текст цвета «авто» на тёмной заливке абзаца Word пишет белым.
            if (IsDarkFill(para.Properties.ShadingColor))
                ApplyAutoColorOnDark(layout);

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

            // Границы по сетке таблицы: общая граница двух ячеек — одна линия,
            // сильнейшая из двух (см. BuildTableEdges). От неё же отсчитывается отступ
            // текста в ячейке, поэтому сетка нужна до обмера ячеек.
            BuildTableEdges(table, rowCount, colCount, tableLayout);

            // Раскладка таблицы идёт в два прохода.
            //
            // Первый обмеряет ячейки: считает ширины, поля и раскладки абзацев и
            // выводит из них базовые высоты строк. Сами SKTableCellLayout здесь ещё
            // не создаются — их Ypt задаётся только при создании и потом неизменен,
            // а вертикальные позиции известны лишь когда высоты строк окончательны.
            // Окончательными они становятся после второго прохода: объединённая по
            // вертикали ячейка может растянуть свои строки под своё содержимое.
            var measured = new List<CellMeasure>();
            var rowHeightsPt = new float[rowCount];

            // Полосы под горизонтальные рамки. Рамка между двумя строками занимает
            // место один раз — в строке под ней, как у Word, и полоса эта общая на
            // всю строку: её высота — самая широкая из линий, лежащих по верхней
            // границе строки, то есть верхних у ячеек этой строки и нижних у ячеек
            // строки над ней. Текст всех ячеек строки начинается под полосой, на
            // одной высоте. Ширина линии — всё место поперёк неё: двойная занимает
            // три своих толщины, тройная — пять (BorderLineCodes.SpanPt).
            //
            // Низ ячейки под рамку места не отдаёт, кроме последней строки таблицы:
            // под ней границы с другой строкой нет, и полоса лежит у её низа.
            //
            // Когда место отдавали и верхняя, и нижняя ячейки, каждая строка выходила
            // выше вордовской на толщину линии; когда полоса мерилась толщиной одной
            // черты, строки под двойной и тройной рамкой выходили ниже вордовских.
            var rowTopBandPt = new float[rowCount];
            float tableBottomBandPt = 0f;

            for (int row = 0; row < rowCount; row++)
            {
                for (int col = 0; col < colCount; col++)
                {
                    var bandCell = table.GetCell(row, col);
                    if (bandCell is null) continue;

                    if (bandCell.Row == row)
                    {
                        float topSpanPt = BorderLineCodes.SpanPt(
                            bandCell.Borders.Top, bandCell.Borders.EffectiveTopThicknessPt());
                        if (topSpanPt > rowTopBandPt[row]) rowTopBandPt[row] = topSpanPt;
                    }

                    if (Math.Min(bandCell.Row + bandCell.RowSpan, rowCount) - 1 == row)
                    {
                        float bottomSpanPt = BorderLineCodes.SpanPt(
                            bandCell.Borders.Bottom, bandCell.Borders.EffectiveBottomThicknessPt());

                        if (row + 1 < rowCount)
                        {
                            if (bottomSpanPt > rowTopBandPt[row + 1]) rowTopBandPt[row + 1] = bottomSpanPt;
                        }
                        else if (bottomSpanPt > tableBottomBandPt)
                        {
                            tableBottomBandPt = bottomSpanPt;
                        }
                    }
                }
            }

            for (int row = 0; row < rowCount; row++)
            {
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

                    // Отступы внутри ячейки ужимаются вместе с колонками: иначе на
                    // уменьшенной таблице поля остаются печатными и съедают текст.
                    float contentScale = ReadingContentScale;
                    float padTopPt = (float)cell.PaddingTopPt * contentScale;
                    float padBottomPt = (float)cell.PaddingBottomPt * contentScale;
                    float padLeftPt = (float)cell.PaddingLeftPt * contentScale;
                    float padRightPt = (float)cell.PaddingRightPt * contentScale;

                    // Границы ячейки после спора с соседями: самая сильная линия на
                    // каждой стороне. От неё отсчитывается отступ текста, иначе текст
                    // ячейки с тонкой рамкой лёг бы под широкую линию соседа.
                    var cellBorders = ResolveCellBorderLayout(tableLayout, cell, row, col, rowCount, colCount);

                    float leftBorderW = cellBorders.Left.SpanPt;
                    float rightBorderW = cellBorders.Right.SpanPt;
                    // Поле ячейки отсчитывается от её края, рамка лежит поверх поля и ширину
                    // текста не отнимает — как у Word (см. SKTableCellLayout.ContentInsetLeftPt).
                    // Вычитание рамок целиком делало каждую ячейку на пункт уже вордовской,
                    // и слово, которое у Word помещается в строку, уходило на следующую.
                    float contentWidthPt = Math.Max(
                        cellWidthPt
                            - Math.Max(padLeftPt, leftBorderW / 2f)
                            - Math.Max(padRightPt, rightBorderW / 2f),
                        1f);

                    // Место под рамку сверху — полоса строки, снизу — полоса под
                    // последней строкой таблицы (см. rowTopBandPt выше).
                    float topEdgePt = rowTopBandPt[row];

                    bool reachesTableBottom = row + cell.RowSpan >= rowCount;
                    float bottomEdgePt = reachesTableBottom ? tableBottomBandPt : 0f;

                    var measure = new CellMeasure(cell, row, col)
                    {
                        IsRotated = cell.IsRotated,
                        XPt = colOffsetsPt[col],
                        WidthPt = cellWidthPt,
                        PadTopPt = padTopPt,
                        PadBottomPt = padBottomPt,
                        PadLeftPt = padLeftPt,
                        PadRightPt = padRightPt,
                        TopBorderPt = topEdgePt,
                        BottomBorderPt = bottomEdgePt,
                        Borders = cellBorders
                    };

                    // Повёрнутая ячейка: строки идут вдоль её высоты, поэтому длина строки —
                    // высота области содержимого, а не ширина. У строки точной высоты она
                    // известна сразу. Иначе, как у Word, строка не переносится, а тянет
                    // высоту строки таблицы за собой: обмеряем текст без переноса, а на
                    // окончательную высоту ячейки раскладка перестраивается во втором проходе.
                    float layoutWidthPt = contentWidthPt;
                    if (measure.IsRotated)
                    {
                        float rowMinPt = (float)table.GetRowMinHeightPt(row);
                        bool rotatedExact = cell.RowSpan == 1 && rowMinPt > 0f && table.IsRowHeightExact(row);
                        layoutWidthPt = rotatedExact
                            ? Math.Max(rowMinPt - measure.VerticalInsetPt, 1f)
                            : RotatedCellMeasureLengthPt;
                    }

                    // Привязка вложенных таблиц к абзацам освежается до вёрстки: абзац,
                    // перед которым стояла таблица, могли удалить.
                    cell.AnchorNestedTables();

                    // Верстаем параграфы ячейки с isCell = true — подавляем дефолтный SpaceAfter.
                    // Таблица, вложенная в ячейку, встаёт перед своим абзацем и занимает
                    // в содержимом свою высоту.
                    float cellContentY = 0f;
                    for (int pi = 0; pi < cell.Paragraphs.Count; pi++)
                    {
                        cellContentY = LayoutNestedTables(
                            measure, cell, pi, cellContentY, contentWidthPt, styles, cellFontPreview);

                        var para = cell.Paragraphs[pi];
                        // Превью шрифта в ячейке: если для абзаца задан preview-абзац (построен
                        // канвасом по выделенному диапазону), строим раскладку из него. Модель
                        // оригинала не трогается. Ширина та же — contentWidthPt.
                        var paraSrc = (cellFontPreview != null
                            && cellFontPreview.TryGetValue(para, out var pv)) ? pv : para;
                        var paraLayout = BuildLayout(paraSrc, layoutWidthPt, styles, isCell: true);

                        // Текст цвета «авто» на тёмной заливке ячейки Word пишет белым.
                        if (IsDarkFill(cell.BackgroundColor))
                            ApplyAutoColorOnDark(paraLayout);

                        measure.Paragraphs.Add(new SKTableParaLayout
                        {
                            Layout = paraLayout,
                            Ypt = cellContentY,
                            ParagraphIndex = pi
                        });

                        cellContentY += paraLayout.SpaceBeforePt
                                      + paraLayout.TotalHeightPt
                                      + paraLayout.SpaceAfterPt;
                    }

                    // Таблицы, стоящие после последнего абзаца.
                    cellContentY = LayoutNestedTables(
                        measure, cell, cell.Paragraphs.Count, cellContentY, contentWidthPt, styles, cellFontPreview);

                    measure.ContentHeightPt = cellContentY;
                    if (measure.IsRotated)
                        measure.RotatedLengthPt = MeasureRotatedLength(measure.Paragraphs);
                    measured.Add(measure);

                    // Высота строки определяется самой высокой ячейкой без RowSpan.
                    if (cell.RowSpan == 1 && measure.OwnHeightPt > rowHeight)
                        rowHeight = measure.OwnHeightPt;
                }

                // Строка без единой своей ячейки (все перекрыты объединением сверху) получает
                // высоту пустой строки. Строку с содержимым не подтягиваем до 14 пт: у
                // таблицы из Word с нулевыми полями строка в 12 пт Times New Roman — 13,8 пт,
                // и прибавка набегала на каждой строке длинной таблицы.
                if (rowHeight <= 0f) rowHeight = 14f;

                // Высота, заданная пользователем, работает как нижняя граница, а не как
                // жёсткий размер: строка не станет ниже неё, но при более высоком
                // содержимом растёт дальше, иначе текст оказался бы обрезан.
                float userMinPt = (float)table.GetRowMinHeightPt(row);

                // Точная высота (из Word) — строка ровно такая, лишнее срезает клип ячейки.
                if (userMinPt > 0f && table.IsRowHeightExact(row)) rowHeight = userMinPt;
                else if (userMinPt > rowHeight) rowHeight = userMinPt;

                rowHeightsPt[row] = rowHeight;
            }

            // Объединённая по вертикали ячейка растягивает свои строки, когда её
            // содержимому не хватает их суммарной высоты.
            //
            // Высоту строки задают только ячейки без RowSpan, поэтому строка, все
            // клетки которой накрыты объединением, оставалась минимальной — 14 pt.
            // Объединение нескольких строк давало плоскую полосу, в которой текст
            // не помещался и срезался клипом ячейки: со стороны это выглядит как
            // «объединение съело содержимое ячеек».
            foreach (var measure in measured)
            {
                if (measure.Cell.RowSpan <= 1) continue;

                int firstRow = measure.Row;
                int lastRow = Math.Min(firstRow + measure.Cell.RowSpan, rowCount) - 1;
                if (lastRow < firstRow) continue;

                float availablePt = 0f;
                for (int r = firstRow; r <= lastRow; r++)
                    availablePt += rowHeightsPt[r];

                float deficitPt = measure.OwnHeightPt - availablePt;
                if (deficitPt <= 0.01f) continue;

                // Недостача делится поровну между строками объединения: вся прибавка
                // на одной строке перекосила бы её соседей справа и слева от
                // объединённой ячейки. Строки точной высоты не растут.
                int growable = 0;
                for (int r = firstRow; r <= lastRow; r++)
                    if (!(table.IsRowHeightExact(r) && table.GetRowMinHeightPt(r) > 0)) growable++;
                if (growable == 0) continue;

                float sharePt = deficitPt / growable;
                for (int r = firstRow; r <= lastRow; r++)
                    if (!(table.IsRowHeightExact(r) && table.GetRowMinHeightPt(r) > 0))
                        rowHeightsPt[r] += sharePt;
            }

            // Второй проход — сборка. Высоты строк окончательны, поэтому вертикальные
            // позиции известны, и объекты раскладки создаются сразу правильными.
            var rowYPt = new float[rowCount];
            float tableY = 0f;
            for (int row = 0; row < rowCount; row++)
            {
                rowYPt[row] = tableY;
                tableY += rowHeightsPt[row];

                var rowLayout = new SKTableRowLayout { Row = row, Ypt = rowYPt[row] };
                rowLayout.HeightPt = rowHeightsPt[row];
                tableLayout.Rows.Add(rowLayout);
            }

            foreach (var measure in measured)
            {
                // Высота ячейки: своя строка, а у объединённой — сумма накрытых строк.
                float cellHeightPt = 0f;
                for (int r = measure.Row;
                     r < measure.Row + measure.Cell.RowSpan && r < rowCount; r++)
                    cellHeightPt += rowHeightsPt[r];

                // Повёрнутая ячейка получает строки ровно на свою окончательную высоту:
                // выравнивание абзаца по центру или вправо отмеряется вдоль неё.
                if (measure.IsRotated)
                    RelayoutRotatedCell(measure,
                        Math.Max(cellHeightPt - measure.VerticalInsetPt, 1f),
                        styles, cellFontPreview);

                var cellLayout = new SKTableCellLayout
                {
                    Row = measure.Row,
                    Column = measure.Column,
                    RowSpan = measure.Cell.RowSpan,
                    ColSpan = measure.Cell.ColSpan,
                    Xpt = measure.XPt,
                    Ypt = rowYPt[measure.Row],
                    WidthPt = measure.WidthPt,
                    PadTopPt = measure.PadTopPt,
                    PadBottomPt = measure.PadBottomPt,
                    PadLeftPt = measure.PadLeftPt,
                    PadRightPt = measure.PadRightPt,
                    TopInsetPt = measure.TopBorderPt,
                    BottomInsetPt = measure.BottomBorderPt,
                    BackgroundColor = measure.Cell.BackgroundColor,
                    ShadingPattern = measure.Cell.ShadingPattern,
                    ShadingPatternColor = measure.Cell.ShadingPatternColor,
                    VerticalAlignment = (int)measure.Cell.VerticalAlignment,
                    TextDirection = (int)measure.Cell.TextDirection,
                    Borders = measure.Borders
                };

                cellLayout.ContentHeightPt = measure.ContentHeightPt;
                cellLayout.HeightPt = cellHeightPt;

                foreach (var paraLayout in measure.Paragraphs)
                    cellLayout.Paragraphs.Add(paraLayout);

                foreach (var nestedLayout in measure.NestedTables)
                    cellLayout.NestedTables.Add(nestedLayout);

                tableLayout.Rows[measure.Row].Cells.Add(cellLayout);
            }

            tableLayout.TotalHeightPt = tableY;
            return tableLayout;
        }

        /// <summary>
        /// Верстает таблицы, стоящие в ячейке перед абзацем beforeParagraph (число, равное
        /// количеству абзацев, — после последнего), и возвращает высоту содержимого
        /// вместе с ними.
        ///
        /// Вложенная таблица верстается на ширину области содержимого ячейки тем же
        /// кодом, что обычная: от этой ширины считаются её ширина в процентах, отступ и
        /// выравнивание. В повёрнутой ячейке таблица не верстается: строки там идут
        /// вдоль высоты ячейки, и места поперёк под таблицу нет.
        /// </summary>
        private float LayoutNestedTables(
            CellMeasure measure, TableCell cell, int beforeParagraph, float contentY,
            float contentWidthPt, StyleResolver styles,
            IReadOnlyDictionary<ParagraphBlock, ParagraphBlock>? cellFontPreview)
        {
            if (measure.IsRotated) return contentY;
            if (cell.NestedTables is not { Count: > 0 } nestedTables) return contentY;

            for (int ni = 0; ni < nestedTables.Count; ni++)
            {
                var nested = nestedTables[ni];
                if (cell.NestedTablePosition(nested) != beforeParagraph) continue;

                var nestedLayout = BuildTableLayout(nested.Table, contentWidthPt, styles, cellFontPreview);
                float nestedX = (float)nested.Table.ResolveLeftOffsetPt(contentWidthPt, nestedLayout.TotalWidthPt);

                measure.NestedTables.Add(new SKNestedTableLayout
                {
                    Layout = nestedLayout,
                    Xpt = nestedX,
                    Ypt = contentY,
                    BeforeParagraphIndex = beforeParagraph,
                    SourceIndex = ni
                });

                contentY += nestedLayout.TotalHeightPt;
            }

            return contentY;
        }

        /// <summary>
        /// Обмеры ячейки между двумя проходами вёрстки таблицы: всё, что нужно для
        /// её раскладки, кроме вертикальной позиции.
        ///
        /// Отдельный тип нужен потому, что Ypt у SKTableRowLayout и SKTableCellLayout
        /// задаётся только при создании объекта, а зависит он от итоговых высот строк —
        /// значит, сами объекты раскладки можно создавать лишь после того, как высоты
        /// сойдутся. Раскладки абзацев при этом строятся один раз, в первом проходе:
        /// они от высоты строки не зависят, а стоят дорого.
        /// </summary>
        private sealed class CellMeasure
        {
            public CellMeasure(TableCell cell, int row, int column)
            {
                Cell = cell;
                Row = row;
                Column = column;
            }

            public TableCell Cell { get; }
            public int Row { get; }
            public int Column { get; }

            public float XPt { get; init; }
            public float WidthPt { get; init; }
            public float PadTopPt { get; init; }
            public float PadBottomPt { get; init; }
            public float PadLeftPt { get; init; }
            public float PadRightPt { get; init; }
            /// <summary>Место под рамку у верхнего края ячейки (SKTableCellLayout.TopInsetPt).</summary>
            public float TopBorderPt { get; init; }

            /// <summary>Место под рамку у нижнего края ячейки (SKTableCellLayout.BottomInsetPt).</summary>
            public float BottomBorderPt { get; init; }

            /// <summary>Границы ячейки после спора с соседями: сильнейшая линия на каждой стороне.</summary>
            public SKTableCellBorderLayout Borders { get; init; } = new();

            /// <summary>Текст ячейки повёрнут: строки идут вдоль её высоты.</summary>
            public bool IsRotated { get; init; }

            public float ContentHeightPt { get; set; }

            /// <summary>
            /// Длина самой длинной строки повёрнутой ячейки вместе с отступами абзаца:
            /// столько высоты ей нужно, чтобы текст встал без переноса.
            /// </summary>
            public float RotatedLengthPt { get; set; }

            /// <summary>Поля и рамки ячейки по вертикали.</summary>
            public float VerticalInsetPt => PadTopPt + PadBottomPt + TopBorderPt + BottomBorderPt;

            public List<SKTableParaLayout> Paragraphs { get; } = new();

            /// <summary>Таблицы внутри ячейки (SKTableCellLayout.NestedTables).</summary>
            public List<SKNestedTableLayout> NestedTables { get; } = new();

            /// <summary>
            /// Высота, которой ячейке хватает на собственное содержимое. У повёрнутой
            /// ячейки это длина её строк: стопка строк ложится поперёк, по ширине.
            /// </summary>
            public float OwnHeightPt => IsRotated
                ? RotatedLengthPt + VerticalInsetPt
                : ContentHeightPt + PadTopPt + PadBottomPt + TopBorderPt + BottomBorderPt;
        }

        /// <summary>
        /// Длина строки, на которую обмеряется повёрнутая ячейка без точной высоты.
        /// Строка такой ячейки не переносится, а растит высоту строки таблицы; предел
        /// нужен лишь затем, чтобы очень длинный текст не дал строку выше листа.
        /// </summary>
        private const float RotatedCellMeasureLengthPt = 720f;

        /// <summary>
        /// Длина самой длинной строки среди абзацев ячейки — с отступами абзаца и
        /// отступом первой строки, без хвостовых пробелов.
        /// </summary>
        private static float MeasureRotatedLength(List<SKTableParaLayout> paragraphs)
        {
            float longest = 0f;
            foreach (var para in paragraphs)
            {
                var layout = para.Layout;
                for (int li = 0; li < layout.Lines.Count; li++)
                {
                    float lineLength = layout.LeftIndentPt + layout.RightIndentPt
                        + layout.Lines[li].TextWidth
                        + (li == 0 ? layout.FirstLineIndentPt : 0f);
                    if (lineLength > longest) longest = lineLength;
                }
            }
            return longest;
        }

        /// <summary>
        /// Перестраивает раскладки абзацев повёрнутой ячейки на длину строки lengthPt —
        /// окончательную высоту её области содержимого — и пересчитывает толщину стопки.
        /// </summary>
        private void RelayoutRotatedCell(
            CellMeasure measure,
            float lengthPt,
            StyleResolver styles,
            IReadOnlyDictionary<ParagraphBlock, ParagraphBlock>? cellFontPreview)
        {
            var cell = measure.Cell;
            measure.Paragraphs.Clear();

            float cellContentY = 0f;
            for (int pi = 0; pi < cell.Paragraphs.Count; pi++)
            {
                var para = cell.Paragraphs[pi];
                var paraSrc = (cellFontPreview != null
                    && cellFontPreview.TryGetValue(para, out var pv)) ? pv : para;
                var paraLayout = BuildLayout(paraSrc, lengthPt, styles, isCell: true);

                if (IsDarkFill(cell.BackgroundColor))
                    ApplyAutoColorOnDark(paraLayout);

                measure.Paragraphs.Add(new SKTableParaLayout
                {
                    Layout = paraLayout,
                    Ypt = cellContentY,
                    ParagraphIndex = pi
                });

                cellContentY += paraLayout.SpaceBeforePt
                              + paraLayout.TotalHeightPt
                              + paraLayout.SpaceAfterPt;
            }

            measure.ContentHeightPt = cellContentY;
        }

        /// <summary>
        /// Матрица повёрнутой ячейки: переводит её содержимое, разложенное как обычный
        /// горизонтальный текст с началом в (contentLeft, contentTop) и строками длиной
        /// contentHeight, в настоящее положение на листе.
        /// Снизу вверх (1): начало строки — у нижнего края, верх строк смотрит влево.
        /// Сверху вниз (2): начало строки — у верхнего края, верх строк смотрит вправо.
        /// </summary>
        public static SKMatrix RotatedCellMatrix(
            int textDirection, float contentLeft, float contentTop,
            float contentWidth, float contentHeight)
        {
            if (textDirection == 1)
            {
                // x' = y − top + left;  y' = top + height − (x − left).
                return new SKMatrix(
                    0f, 1f, contentLeft - contentTop,
                    -1f, 0f, contentTop + contentHeight + contentLeft,
                    0f, 0f, 1f);
            }

            if (textDirection == 2)
            {
                // x' = left + width − (y − top);  y' = top + (x − left).
                return new SKMatrix(
                    0f, -1f, contentLeft + contentWidth + contentTop,
                    1f, 0f, contentTop - contentLeft,
                    0f, 0f, 1f);
            }

            return SKMatrix.Identity;
        }

        /// <summary>
        /// Сдвиг стопки строк повёрнутой ячейки поперёк неё — по её ширине. Вертикальное
        /// выравнивание ячейки у Word поворачивается вместе с текстом: «сверху» —
        /// это сторона, куда смотрит верх строк.
        /// </summary>
        public static float RotatedCellStackOffset(SKTableCellLayout cell, float contentWidth)
        {
            float free = contentWidth - cell.ContentHeightPt;
            float offset = cell.VerticalAlignment switch
            {
                1 => free / 2f,
                2 => free,
                _ => 0f
            };
            return Math.Max(0f, offset);
        }

        /// <summary>
        /// Рисует абзацы повёрнутой ячейки. Клип по ячейке вызывающий ставит до вызова,
        /// в координатах листа.
        /// </summary>
        private static void RenderRotatedCellParagraphs(
            SKCanvas canvas, SKTableCellLayout cell,
            float contentLeft, float contentTop, float contentWidth, float contentHeight)
        {
            var matrix = RotatedCellMatrix(
                cell.TextDirection, contentLeft, contentTop, contentWidth, contentHeight);

            canvas.Save();
            // Через SetMatrix, а не Concat: у Concat в разных версиях SkiaSharp разная
            // сигнатура (ref и in), а произведение матриц одинаково в любой.
            canvas.SetMatrix(SKMatrix.Concat(canvas.TotalMatrix, matrix));

            float stackY = contentTop + RotatedCellStackOffset(cell, contentWidth);

            for (int cpi = 0; cpi < cell.Paragraphs.Count; cpi++)
            {
                var paraLayout = cell.Paragraphs[cpi];
                float paraY = stackY + paraLayout.Ypt + paraLayout.Layout.SpaceBeforePt;

                RenderCellParagraphBorders(canvas, cell.Paragraphs, cpi,
                    contentLeft + paraLayout.Layout.LeftIndentPt, paraY);

                RenderParagraphLines(
                    canvas,
                    paraLayout.Layout,
                    contentLeft + paraLayout.Layout.LeftIndentPt,
                    paraY,
                    0,
                    paraLayout.Layout.Lines.Count);
            }

            canvas.Restore();
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

            // Сначала фон всех ячеек, потом рамки и текст. Нижняя линия ячейки лежит в
            // полосе под рамку у верха строки под ней — на месте ячейки снизу. Если бы
            // фон рисовался вперемежку с рамками, заливка нижней ячейки закрывала бы
            // линию верхней.
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
                    RenderCellShadingPattern(canvas, cell, cellX, cellY, cell.WidthPt, cell.HeightPt);
                }
            }

            foreach (var row in tableLayout.Rows)
            {
                foreach (var cell in row.Cells)
                {
                    float cellX = tableX + cell.Xpt;
                    float cellY = tableY + cell.Ypt;

                    // Границы ячейки.
                    RenderCellBorders(canvas, tableLayout, cell, cellX, cellY, cell.HeightPt, canvasScale);

                    // Содержимое — параграфы.
                    RenderCellContent(canvas, cell, cellX, cellY);
                }
            }
        }

        /// <summary>
        /// Текст одной строки таблицы — без фона и рамок. Нужен полотну для шапки,
        /// повторяемой над продолжением таблицы на следующей странице: рамки и фон оно
        /// рисует своим проходом, а абзацев у копии шапки в раскладке страницы нет.
        /// </summary>
        /// <param name="rowIndex">Строка раскладки таблицы.</param>
        /// <param name="tableX">Левый край таблицы в pt.</param>
        /// <param name="rowY">Верх строки на листе в pt.</param>
        public static void RenderTableRowContent(
            SKCanvas canvas, SKTableLayout tableLayout, int rowIndex, float tableX, float rowY)
        {
            if (rowIndex < 0 || rowIndex >= tableLayout.Rows.Count) return;

            var row = tableLayout.Rows[rowIndex];
            foreach (var cell in row.Cells)
            {
                if (cell.Row != rowIndex) continue;
                RenderCellContent(canvas, cell, tableX + cell.Xpt, rowY);
            }
        }

        /// <summary>
        /// Абзацы ячейки в её области содержимого: с вертикальным выравниванием, обрезкой
        /// по границам ячейки и поворотом текста. cellX/cellY — левый верхний угол ячейки.
        /// </summary>
        private static void RenderCellContent(
            SKCanvas canvas, SKTableCellLayout cell, float cellX, float cellY)
        {
            float contentX = cellX + cell.ContentInsetLeftPt;
            float contentAreaH = cell.HeightPt - cell.PadTopPt - cell.PadBottomPt
                               - cell.TopInsetPt - cell.BottomInsetPt;

            // Вертикальное выравнивание содержимого.
            float contentOffsetY = cell.VerticalAlignment switch
            {
                1 => (contentAreaH - cell.ContentHeightPt) / 2f, // Middle
                2 => contentAreaH - cell.ContentHeightPt,         // Bottom
                _ => 0f                                            // Top
            };
            contentOffsetY = Math.Max(0f, contentOffsetY);

            float contentY = cellY + cell.PadTopPt
                           + cell.TopInsetPt
                           + contentOffsetY;

            // Обрезаем рендеринг по границам ячейки — без этого длинный текст
            // вылезает за границы ячейки и перекрывает соседние.
            // Боковая рамка рисуется по краю ячейки: внутри ячейки лежит её половина.
            float clipX = cellX + cell.Borders.Left.SpanPt / 2f;
            float clipY = cellY + cell.TopInsetPt;
            float clipW = cell.WidthPt - (cell.Borders.Left.SpanPt + cell.Borders.Right.SpanPt) / 2f;
            float clipH = cell.HeightPt - cell.TopInsetPt - cell.BottomInsetPt;

            canvas.Save();
            canvas.ClipRect(new SKRect(clipX, clipY, clipX + clipW, clipY + clipH));

            if (cell.IsRotated)
            {
                float rotatedWidth = cell.ContentAreaWidthPt;
                RenderRotatedCellParagraphs(canvas, cell,
                    contentX, cellY + cell.PadTopPt + cell.TopInsetPt,
                    rotatedWidth, contentAreaH);
                canvas.Restore();
                return;
            }

            // Таблицы внутри ячейки: целиком, со своими рамками, заливками и текстом.
            foreach (var nested in cell.NestedTables)
                RenderTable(canvas, nested.Layout, contentX + nested.Xpt, contentY + nested.Ypt);

            for (int cpi = 0; cpi < cell.Paragraphs.Count; cpi++)
            {
                var paraLayout = cell.Paragraphs[cpi];
                float paraY = contentY + paraLayout.Ypt
                            + paraLayout.Layout.SpaceBeforePt;

                RenderCellParagraphBorders(canvas, cell.Paragraphs, cpi,
                    contentX + paraLayout.Layout.LeftIndentPt, paraY);

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

        /// <summary>
        /// Строит вёрстку всего документа — разбивает параграфы по страницам построчно.
        /// Один параграф может давать несколько SKPageParagraph если он пересекает границу страниц.
        /// Вызывается TextEditorPrintDocument и DocumentCanvas в Page mode.
        /// </summary>
        /// <summary>
        /// Записывает плавающий объект на страницу печати. Габарит нулевой ширины
        /// или высоты пропускается: рисовать нечего, а в PDF он дал бы пустой узел.
        /// </summary>
        private static void AddPageFloat(
            SKPageContent page, object block,
            WrapMode wrapMode, int zOrder,
            float offsetXPt, float offsetYPt, float widthPt, float heightPt,
            float marginLeftPt, float marginTopPt)
        {
            if (widthPt <= 0f || heightPt <= 0f) return;

            page.Floats.Add(new SKPageFloat
            {
                Block = block,
                XPt = marginLeftPt + offsetXPt,
                YPt = marginTopPt + offsetYPt,
                WidthPt = widthPt,
                HeightPt = heightPt,
                // За текстом и в потоке — до текста, остальное поверх: порядок тот же,
                // что в экранном проходе канваса.
                BeforeText = wrapMode is WrapMode.Behind or WrapMode.Inline,
                ZOrder = zOrder
            });
        }

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
                        float leftIndentPt = (float)tableBlock.ResolveLeftOffsetPt(textWidthPt, tableLayout.TotalWidthPt);
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


                    // ── Плавающие объекты: картинки и фигуры ─────────────
                    // Кладутся на ту страницу, где стоит их блок в потоке, по
                    // собственным смещениям от начала текстовой области — тот же
                    // отсчёт, что и на экране. Высоту потока они не занимают:
                    // печать повторяет экран, а не пересчитывает его заново.
                    if (block is ImageBlock floatImage)
                    {
                        float floatOffsetXPt = (float)floatImage.OffsetXPt;
                        float floatOffsetYPt = (float)floatImage.OffsetYPt;

                        // У картинки из Word точка отсчёта своя: лист, поля или верх её
                        // абзаца — место блока в потоке. Она переводится в смещение от
                        // начала текстовой области, от которого считает AddPageFloat.
                        if (floatImage.WrapMode != WrapMode.Inline
                            && floatImage.AnchorPosition is { } floatAnchor)
                        {
                            var (originXPt, originYPt) = floatAnchor.ResolveOrigin(
                                (float)floatImage.WidthPt, (float)floatImage.HeightPt,
                                marginLeftPt, textWidthPt, 0f, pageWidthPt,
                                0f, pageHeightPt, marginTopPt,
                                Math.Max(0f, pageHeightPt - marginTopPt - textHeightPt),
                                marginTopPt + currentY);

                            floatOffsetXPt += originXPt - marginLeftPt;
                            floatOffsetYPt += originYPt - marginTopPt;
                        }

                        AddPageFloat(currentPage, floatImage,
                            floatImage.WrapMode, floatImage.ZOrder,
                            floatOffsetXPt, floatOffsetYPt,
                            (float)floatImage.WidthPt, (float)floatImage.HeightPt,
                            marginLeftPt, marginTopPt);
                        paraIndex++;
                        continue;
                    }

                    if (block is ShapeBlock floatShape)
                    {
                        float shapeOffsetXPt = (float)floatShape.OffsetXPt;
                        float shapeOffsetYPt = (float)floatShape.OffsetYPt;

                        // Опора из Word — та же, что у картинки выше.
                        if (floatShape.WrapMode != WrapMode.Inline
                            && floatShape.AnchorPosition is { } shapeAnchor)
                        {
                            var (shapeOriginXPt, shapeOriginYPt) = shapeAnchor.ResolveOrigin(
                                (float)floatShape.WidthPt, (float)floatShape.HeightPt,
                                marginLeftPt, textWidthPt, 0f, pageWidthPt,
                                0f, pageHeightPt, marginTopPt,
                                Math.Max(0f, pageHeightPt - marginTopPt - textHeightPt),
                                marginTopPt + currentY);

                            shapeOffsetXPt += shapeOriginXPt - marginLeftPt;
                            shapeOffsetYPt += shapeOriginYPt - marginTopPt;
                        }

                        AddPageFloat(currentPage, floatShape,
                            floatShape.WrapMode, floatShape.ZOrder,
                            shapeOffsetXPt, shapeOffsetYPt,
                            (float)floatShape.WidthPt, (float)floatShape.HeightPt,
                            marginLeftPt, marginTopPt);
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

            // Плавающие объекты за текстом и в потоке — под ним.
            RenderPageFloats(canvas, page, beforeText: true);

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

                // Рамка и заливка — под текстом. Соединяется с соседом по листу, только
                // если он идёт в документе вплотную: между ними не таблица и не картинка.
                if (para.Layout.Borders is not null || para.Layout.HasShading)
                {
                    int at = page.Paragraphs.IndexOf(para);
                    var prevPara = at > 0 ? page.Paragraphs[at - 1] : null;
                    var nextPara = at >= 0 && at + 1 < page.Paragraphs.Count ? page.Paragraphs[at + 1] : null;

                    bool joinPrev = prevPara is not null
                        && prevPara.ParagraphIndex == para.ParagraphIndex - 1
                        && BordersJoin(prevPara.Layout, para.Layout);
                    bool joinNext = nextPara is not null
                        && nextPara.ParagraphIndex == para.ParagraphIndex + 1
                        && BordersJoin(para.Layout, nextPara.Layout);

                    bool shadeJoinPrev = prevPara is not null
                        && prevPara.ParagraphIndex == para.ParagraphIndex - 1
                        && ShadingJoin(prevPara.Layout, para.Layout);
                    bool shadeJoinNext = nextPara is not null
                        && nextPara.ParagraphIndex == para.ParagraphIndex + 1
                        && ShadingJoin(para.Layout, nextPara.Layout);

                    RenderParagraphBorders(canvas, para.Layout, paraX, paraY,
                        para.LineFrom, para.LineTo, joinPrev, joinNext,
                        shadeJoinPrev, shadeJoinNext);
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

            RenderPageTables(canvas, page);

            // Плавающие объекты поверх текста — последними, как на экране.
            RenderPageFloats(canvas, page, beforeText: false);
        }

        /// <summary>
        /// Плавающие объекты страницы одного слоя. Порядок — по Z-порядку, при
        /// равном сохраняется порядок блоков в документе.
        ///
        /// Картинку читает ResolvePrintImage: у печати нет кеша канваса, файлы
        /// берутся из хранилища проекта и живут ровно на время печати.
        /// </summary>
        private static void RenderPageFloats(SKCanvas canvas, SKPageContent page, bool beforeText)
        {
            if (page.Floats.Count == 0) return;

            var layer = new List<(SKPageFloat Item, int Order)>();
            for (int i = 0; i < page.Floats.Count; i++)
            {
                var f = page.Floats[i];
                if (f.BeforeText != beforeText) continue;
                layer.Add((f, i));
            }
            if (layer.Count == 0) return;

            layer.Sort((a, b) =>
            {
                int byZ = a.Item.ZOrder.CompareTo(b.Item.ZOrder);
                return byZ != 0 ? byZ : a.Order.CompareTo(b.Order);
            });

            foreach (var (item, _) in layer)
            {
                var rect = new SKRect(
                    item.XPt, item.YPt,
                    item.XPt + item.WidthPt, item.YPt + item.HeightPt);

                switch (item.Block)
                {
                    case ShapeBlock shape:
                    {
                        var fill = string.IsNullOrEmpty(shape.FillImageFileName)
                            ? null
                            : PrintImageResolver?.Invoke(shape.FillImageFileName!);
                        FloatingObjectRenderer.DrawShape(canvas, shape, rect, fill);
                        break;
                    }

                    case ImageBlock image:
                    {
                        var bitmap = PrintImageResolver?.Invoke(image.ImageFileName);
                        if (bitmap is not null)
                            FloatingObjectRenderer.DrawImage(canvas, image, rect, bitmap);
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Источник картинок для печати: имя файла в хранилище проекта — образ.
        /// Ставится перед печатью и общий на весь проход, как и остальные
        /// подмены статического рендера.
        /// </summary>
        public static Func<string, SKImage?>? PrintImageResolver { get; set; }

        /// <summary>Таблицы страницы (каждая может быть слайсом строк).</summary>
        private static void RenderPageTables(SKCanvas canvas, SKPageContent page)
        {
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
                        RenderCellShadingPattern(canvas, cell, cellX, cellY, cell.WidthPt, cell.HeightPt);
                        RenderCellBorders(canvas, layout, cell, cellX, cellY, cell.HeightPt, canvasScale);
                        float cx2 = cellX + cell.ContentInsetLeftPt;
                        float cy2 = cellY + cell.PadTopPt + cell.TopInsetPt;
                        canvas.Save();
                        canvas.ClipRect(new SKRect(cellX + cell.Borders.Left.SpanPt / 2f, cellY + cell.TopInsetPt,
                            cellX + cell.WidthPt - cell.Borders.Right.SpanPt / 2f, cellY + cell.HeightPt - cell.BottomInsetPt));
                        if (cell.IsRotated)
                        {
                            RenderRotatedCellParagraphs(canvas, cell, cx2, cy2,
                                cell.ContentAreaWidthPt,
                                cell.HeightPt - cell.PadTopPt - cell.PadBottomPt
                                    - cell.TopInsetPt - cell.BottomInsetPt);
                            canvas.Restore();
                            continue;
                        }
                        for (int cpi = 0; cpi < cell.Paragraphs.Count; cpi++)
                        {
                            var p = cell.Paragraphs[cpi];
                            RenderCellParagraphBorders(canvas, cell.Paragraphs, cpi,
                                cx2 + p.Layout.LeftIndentPt, cy2 + p.Ypt);
                            RenderParagraphLines(canvas, p.Layout, cx2 + p.Layout.LeftIndentPt, cy2 + p.Ypt, 0, p.Layout.Lines.Count);
                        }
                        canvas.Restore();
                    }
                }

                float headerOffset = pageTable.HeaderRowHeightPt;

                bool hasLastRowClip = pageTable.LastRowVisibleHeightPt >= 0f;
                bool hasFirstRowOffset = pageTable.IsContinuation && pageTable.FirstRowContentOffsetPt > 0f;

                // Фон всех ячеек куска — до рамок и текста: нижняя линия ячейки лежит на
                // месте ячейки под ней, и заливка той закрыла бы линию, рисуйся они
                // вперемежку.
                foreach (var row in layout.Rows)
                {
                    if (row.Row < rowFrom || row.Row >= rowTo) continue;

                    float fillRowH = row.HeightPt;
                    float fillRowShift = 0f;

                    if (row.Row == rowFrom && hasFirstRowOffset)
                    {
                        fillRowShift = pageTable.FirstRowContentOffsetPt;
                        fillRowH = row.HeightPt - fillRowShift;
                    }

                    if (row.Row == rowTo - 1 && hasLastRowClip)
                        fillRowH = pageTable.LastRowVisibleHeightPt;

                    foreach (var cell in row.Cells)
                    {
                        float fillX = tableX + cell.Xpt;
                        float fillY = tableBaseY + headerOffset + cell.Ypt - rowOffsetY;

                        if (!string.IsNullOrEmpty(cell.BackgroundColor)
                            && SKColor.TryParse(cell.BackgroundColor, out var bgColor))
                        {
                            using var bgPaint = new SKPaint { Color = bgColor };
                            canvas.DrawRect(fillX, fillY, cell.WidthPt, fillRowH, bgPaint);
                        }
                        RenderCellShadingPattern(canvas, cell, fillX, fillY, cell.WidthPt, fillRowH);
                    }
                }

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

                        bool suppressBottom = isLastRow && hasLastRowClip;
                        float visibleCellY = cellY + firstRowShift;

                        // Кусок таблицы кончается этой ячейкой, а таблица идёт дальше:
                        // линию между строками рисует ячейка снизу, на этой странице её
                        // нет, и кусок замыкается линией под ячейкой.
                        bool closesSlice = cell.Row + Math.Max(cell.RowSpan, 1) >= rowTo;
                        RenderCellBorders(canvas, layout, cell, cellX, visibleCellY, visibleRowH, canvasScale,
                            false, suppressBottom, closesSlice);

                        float contentX = cellX + cell.ContentInsetLeftPt;
                        float contentY = cellY + cell.PadTopPt + cell.TopInsetPt;

                        float clipTop = cellY + firstRowShift + cell.TopInsetPt;
                        float clipBottom = cellY + firstRowShift + visibleRowH - cell.BottomInsetPt;

                        canvas.Save();
                        canvas.ClipRect(new SKRect(
                            cellX + cell.Borders.Left.SpanPt / 2f,
                            clipTop,
                            cellX + cell.WidthPt - cell.Borders.Right.SpanPt / 2f,
                            clipBottom));
                        if (cell.IsRotated)
                        {
                            RenderRotatedCellParagraphs(canvas, cell, contentX, contentY,
                                cell.ContentAreaWidthPt,
                                cell.HeightPt - cell.PadTopPt - cell.PadBottomPt
                                    - cell.TopInsetPt - cell.BottomInsetPt);
                            canvas.Restore();
                            continue;
                        }
                        // Таблицы внутри ячейки.
                        foreach (var nested in cell.NestedTables)
                            RenderTable(canvas, nested.Layout, contentX + nested.Xpt, contentY + nested.Ypt);

                        for (int cpi = 0; cpi < cell.Paragraphs.Count; cpi++)
                        {
                            var paraLayout = cell.Paragraphs[cpi];
                            RenderCellParagraphBorders(canvas, cell.Paragraphs, cpi,
                                contentX + paraLayout.Layout.LeftIndentPt, contentY + paraLayout.Ypt);
                            RenderParagraphLines(canvas, paraLayout.Layout, contentX + paraLayout.Layout.LeftIndentPt,
                                contentY + paraLayout.Ypt, 0, paraLayout.Layout.Lines.Count);
                        }
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

                // Табуляции-черты — под текстом строки.
                DrawBarTabs(canvas, layout, paraX, lineY, line.Height);

                // Прямоугольник строки — для градиента текста в режиме «построчно».
                float lineStartX = paraX + offsetX + (line.Segments.Count > 0 ? line.Segments[0].X : 0f);
                var lineRect = new SKRect(lineStartX, lineY, lineStartX + line.TextWidth, lineY + line.Height);

                int lastContentSeg = LastContentSegIndex(line);
                int segIdx = -1;
                foreach (var seg in line.Segments)
                {
                    segIdx++;

                    // Скрытый текст не рисуется.
                    if (seg.IsHidden) continue;

                    // Перенос строки и невидимый мягкий перенос не рисуются.
                    if (seg.IsLineBreak || (seg.IsSoftHyphen && !seg.SoftHyphenShown)) continue;

                    float segX = paraX + seg.X + offsetX;
                    float baseY = lineY + line.Baseline;

                    // Объект в строке: рисует канвас, текстовые слои (подчёркивание,
                    // зачёркивание, градиент букв) к нему не применяются.
                    if (seg.IsInlineObject)
                    {
                        DrawInlineObject?.Invoke(canvas, seg, segX, baseY);
                        continue;
                    }

                    // Прыжок табуляции сам по себе пуст: рисуется только заполнитель,
                    // если он назначен. Сам символ табуляции печатать нечем — в шрифтах
                    // у него нет рисунка.
                    if (seg.IsTabJump)
                    {
                        DrawTabLeader(canvas, seg, segX, baseY, paraX);
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
                    // Краска приводится к чтению здесь, в момент отрисовки: в раскладке
                    // она запеклась бы в кэш и правка цвета до текста не доходила бы.
                    SKColor textColor = ApplyReadingInk(seg.Color);
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

                    DrawSegmentGlyphs(canvas, seg, segX, baseY, font, paint);

                    float decoWidth = SegHighlightWidth(line, segIdx, lastContentSeg);

                    if (seg.IsUnderline)
                        DrawSegmentUnderline(canvas, seg, segX, baseY, decoWidth, textColor, textShader);

                    DrawSegmentDecorations(canvas, line, segIdx, lastContentSeg, seg,
                        segX, baseY, decoWidth, font, textColor);

                    // Зачёркивание — на высоте из метрик шрифта, как в Word; хвостовые
                    // пробелы строки не зачёркиваются, как не подчёркиваются.
                    if (seg.IsStrikethrough || seg.IsDoubleStrikethrough)
                        StrikePainter.Draw(canvas, seg.IsDoubleStrikethrough, segX,
                            SegHighlightWidth(line, segIdx, lastContentSeg), baseY, seg.FontSizePt,
                            font, textColor, textShader);

                    textShader?.Dispose();
                }
            }
        }

        /// <summary>
        /// Подчёркивание сегмента по его виду. Ширина приходит уже обрезанной по
        /// хвостовым пробелам строки — так же, как у заливки: Word не подчёркивает
        /// пробелы, повисшие за краем строки при переносе.
        ///
        /// «Только слова» пропускает сегменты из одних пробелов: вёрстка режет строку
        /// на границе пробела и слова, так что пробел между словами — отдельный сегмент.
        /// Свой цвет линии идёт через ту же поправку чтения, что и буквы; без своего
        /// цвета линия берёт цвет и градиент букв.
        /// </summary>
        private static void DrawSegmentUnderline(
            SKCanvas canvas, SKRunSegment seg, float segX, float baselineY, float width,
            SKColor textColor, SKShader? textShader)
        {
            var style = UnderlineShape.StyleFromCode(seg.UnderlineStyle);
            if (style == Models.Inline.UnderlineStyle.None)
                style = Models.Inline.UnderlineStyle.Single;

            if (UnderlineShape.Of(style).WordsOnly && IsBlankText(seg.Text)) return;

            if (UnderlinePainter.TryParseLineColor(seg.UnderlineColor, out var ownColor))
            {
                UnderlinePainter.Draw(canvas, style, segX, width, baselineY, seg.FontSizePt,
                    ApplyReadingInk(ownColor), null, seg.IsBold);
                return;
            }

            UnderlinePainter.Draw(canvas, style, segX, width, baselineY, seg.FontSizePt,
                textColor, textShader, seg.IsBold);
        }

        private static bool IsBlankText(string text)
        {
            for (int i = 0; i < text.Length; i++)
                if (text[i] != ' ' && text[i] != '\t') return false;
            return true;
        }

        // ── Рамка абзаца ──────────────────────────────────────────────────

        /// <summary>
        /// Рамка абзаца для раскладки. Зазор до текста ужимается вместе с отступами
        /// листа чтения, толщина — нет: линия толщиной в долю пункта пропала бы.
        /// null — рамки нет или ни одна линия не видна.
        /// </summary>
        private static SKParagraphBorders? BuildBordersLayout(
            Models.Styles.ParagraphBorders? borders, float indentScale)
        {
            if (borders is null || borders.IsEmpty) return null;

            static SKParagraphBorderLine? Line(Models.Styles.ParagraphBorderLine? line, float scale)
            {
                if (line is not { IsVisible: true }) return null;

                return new SKParagraphBorderLine
                {
                    Style = (int)line.Style,
                    Color = string.IsNullOrWhiteSpace(line.Color) ? null : line.Color,
                    WidthPt = (float)line.WidthPt,
                    SpacePt = (float)Math.Max(0.0, line.SpacePt) * scale
                };
            }

            return new SKParagraphBorders
            {
                Top = Line(borders.Top, indentScale),
                Bottom = Line(borders.Bottom, indentScale),
                Left = Line(borders.Left, indentScale),
                Right = Line(borders.Right, indentScale)
            };
        }

        /// <summary>
        /// Продолжается ли боковая черта рамки от одного абзаца к другому: у обоих
        /// одинаковые боковые линии и одинаковые края текста. Так Word показывает
        /// подряд идущие абзацы одной врезки — одной сплошной чертой, а не
        /// пунктиром из кусков по абзацам.
        /// </summary>
        /// <summary>
        /// Сливается ли заливка двух соседних абзацев без рамки в один сплошной фон:
        /// цвет один и тот же, края текста совпадают. Как у Word — подряд идущие
        /// залитые абзацы дают один блок, без просветов на интервалах между ними.
        /// Абзацы с рамкой сливаются своей рамкой (<see cref="BordersJoin"/>).
        /// </summary>
        public static bool ShadingJoin(SKTextLayout? a, SKTextLayout? b)
        {
            if (a is null || b is null) return false;
            if (!a.HasShading || !b.HasShading) return false;
            if (!string.Equals(a.ShadingColor ?? string.Empty, b.ShadingColor ?? string.Empty, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.Equals(a.ShadingPattern ?? string.Empty, b.ShadingPattern ?? string.Empty, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.Equals(a.ShadingPatternColor ?? string.Empty, b.ShadingPatternColor ?? string.Empty, StringComparison.OrdinalIgnoreCase)) return false;
            if (a.Borders is { IsEmpty: false } || b.Borders is { IsEmpty: false }) return false;

            return Math.Abs(a.LeftIndentPt - b.LeftIndentPt) < 0.5f
                && Math.Abs((a.LeftIndentPt + a.TextAreaWidthPt) - (b.LeftIndentPt + b.TextAreaWidthPt)) < 0.5f;
        }

        public static bool BordersJoin(SKTextLayout? a, SKTextLayout? b)
        {
            if (a?.Borders is null || b?.Borders is null) return false;
            if (!SKParagraphBorders.SameSides(a.Borders, b.Borders)) return false;

            return Math.Abs(a.LeftIndentPt - b.LeftIndentPt) < 0.5f
                && Math.Abs((a.LeftIndentPt + a.TextAreaWidthPt) - (b.LeftIndentPt + b.TextAreaWidthPt)) < 0.5f;
        }

        /// <summary>
        /// Рисует рамку одного куска абзаца — целого или той его части, что легла на
        /// лист. Верхняя линия рисуется только у начала абзаца, нижняя — только у
        /// конца: у куска, разрезанного листом, ни той, ни другой на разрезе нет.
        ///
        /// Боковые черты при соединении с соседом тянутся через интервал между
        /// абзацами: сверху — на свой интервал до, снизу — на свой интервал после.
        /// Сосед делает то же со своей стороны, и черта идёт без разрыва.
        /// </summary>
        /// <param name="canvas">Холст.</param>
        /// <param name="layout">Раскладка абзаца.</param>
        /// <param name="textLeftX">Левый край текста абзаца (с отступом).</param>
        /// <param name="textTopY">Верх первой строки куска.</param>
        /// <param name="lineFrom">Первая строка куска.</param>
        /// <param name="lineTo">Строка за последней строкой куска.</param>
        /// <param name="joinPrev">Черта соединяется с абзацем выше.</param>
        /// <param name="joinNext">Черта соединяется с абзацем ниже.</param>
        /// <param name="shadeJoinPrev">Заливка без рамки сливается с абзацем выше.</param>
        /// <param name="shadeJoinNext">Заливка без рамки сливается с абзацем ниже.</param>
        public static void RenderParagraphBorders(
            SKCanvas canvas, SKTextLayout layout,
            float textLeftX, float textTopY,
            int lineFrom, int lineTo,
            bool joinPrev, bool joinNext,
            bool shadeJoinPrev = false, bool shadeJoinNext = false)
        {
            var b = layout.Borders;
            bool hasBorders = b is not null && !b.IsEmpty;
            bool hasFill = TryParseShading(layout.ShadingColor, out var shadingColor);
            var patternShader = GetShadingPatternShader(layout.ShadingPattern, layout.ShadingPatternColor);
            bool hasShading = hasFill || patternShader is not null;
            if (!hasBorders && !hasShading) return;

            int from = Math.Max(lineFrom, 0);
            int to = Math.Min(lineTo, layout.Lines.Count);

            float textHeight = 0f;
            for (int i = from; i < to; i++)
                textHeight += layout.Lines[i].Height;

            // У пустого абзаца строк нет, но рамка у него есть — как у пустой
            // строки в Word, обведённой вместе с соседями.
            if (layout.Lines.Count == 0) textHeight = 0f;

            bool isStart = from == 0;
            bool isEnd = to >= layout.Lines.Count;

            float textRightX = textLeftX + layout.TextAreaWidthPt;
            float textBottomY = textTopY + textHeight;

            var top = hasBorders && isStart && b!.Top is { IsVisible: true } ? b.Top : null;
            var bottom = hasBorders && isEnd && b!.Bottom is { IsVisible: true } ? b.Bottom : null;
            var left = hasBorders && b!.Left is { IsVisible: true } ? b.Left : null;
            var right = hasBorders && b!.Right is { IsVisible: true } ? b.Right : null;

            // Середины линий рамки. Зазор до текста Word отмеряет до середины ближней к
            // тексту черты, а не до её края: черта ложится на зазор своей половиной. Место
            // под линию в раскладке прежнее (зазор плюс вся толщина), поэтому снаружи от
            // рамки остаётся половина черты воздуха, и рамки соседних абзацев не
            // слипаются, а стоят с просветом, как у Word. Прежде черта стояла целиком за
            // зазором, и нижняя линия одной рамки ложилась вплотную на верхнюю следующей.
            float topY = top is not null ? textTopY - top.SpacePt - BorderBandInsetPt(top) : textTopY;
            float bottomY = bottom is not null ? textBottomY + bottom.SpacePt + BorderBandInsetPt(bottom) : textBottomY;
            float leftX = left is not null ? textLeftX - left.SpacePt - BorderBandInsetPt(left) : textLeftX;
            float rightX = right is not null ? textRightX + right.SpacePt + BorderBandInsetPt(right) : textRightX;

            float boxTop = top is not null ? topY - top.DrawnWidthPt / 2f : textTopY;
            float boxBottom = bottom is not null ? bottomY + bottom.DrawnWidthPt / 2f : textBottomY;

            float sideTop = joinPrev && isStart ? textTopY - layout.SpaceBeforePt : boxTop;
            float sideBottom = joinNext && isEnd ? textBottomY + layout.SpaceAfterPt : boxBottom;

            // ── Заливка ──
            // Под строками, а при рамке — всё поле внутри неё, до внутренней черты:
            // у двойной линии просвет между чертами остаётся цветом бумаги, как в Word.
            // Там, где абзац сливается с соседом той же рамки, заливка идёт через
            // интервал между ними — вместе с боковыми чертами. Так же сливаются подряд
            // идущие абзацы без рамки с одинаковой заливкой: каждый закрашивает свою
            // половину интервала — свой интервал до и свой интервал после.
            if (hasShading)
            {
                // Заливка доходит до самой внутренней черты линии: у двойной — до второй,
                // у тройной — до третьей.
                float fillLeft = left is not null ? StrandPos(left, leftX, StrandCount(left) - 1, -1f) : textLeftX;
                float fillRight = right is not null ? StrandPos(right, rightX, StrandCount(right) - 1, 1f) : textRightX;

                float fillTop;
                if (top is not null) fillTop = StrandPos(top, topY, StrandCount(top) - 1, -1f);
                else if (joinPrev && isStart) fillTop = sideTop;
                else if (shadeJoinPrev && isStart) fillTop = textTopY - layout.SpaceBeforePt;
                else fillTop = textTopY;

                float fillBottom;
                if (bottom is not null) fillBottom = StrandPos(bottom, bottomY, StrandCount(bottom) - 1, 1f);
                else if (joinNext && isEnd) fillBottom = sideBottom;
                else if (shadeJoinNext && isEnd) fillBottom = textBottomY + layout.SpaceAfterPt;
                else fillBottom = textBottomY;

                if (fillRight > fillLeft && fillBottom > fillTop)
                {
                    var fillRect = SKRect.Create(fillLeft, fillTop, fillRight - fillLeft, fillBottom - fillTop);

                    if (hasFill)
                    {
                        using var fill = new SKPaint
                        {
                            Color = shadingColor,
                            Style = SKPaintStyle.Fill,
                            IsAntialias = false
                        };
                        canvas.DrawRect(fillRect, fill);
                    }

                    // Узор — поверх цвета заливки. Его клетки привязаны к листу, а не к
                    // абзацу: у соседних абзацев с одним узором он идёт без шва. Клетка —
                    // пиксель устройства, как у Word: узор не крупнеет с масштабом, точки
                    // и штрихи остаются в пиксель при любом увеличении.
                    if (patternShader is not null)
                    {
                        float cellPt = 1f / CanvasScale(canvas);
                        using var cellShader = patternShader.WithLocalMatrix(SKMatrix.CreateScale(cellPt, cellPt));
                        using var pattern = new SKPaint
                        {
                            Shader = cellShader,
                            Style = SKPaintStyle.Fill,
                            IsAntialias = false
                        };
                        canvas.DrawRect(fillRect, pattern);
                    }
                }
            }

            if (!hasBorders) return;

            float scale = CanvasScale(canvas);

            // ── Линии ──
            // Каждая линия — одна черта или две (двойная). Черты считаются по рангам:
            // 0 — внешняя, 1 — внутренняя; у одинарной линии оба ранга — одна черта.
            // Черта ранга r соединяется на углу с чертой того же ранга соседней
            // стороны: двойная рамка складывается из двух вложенных прямоугольников.
            // Прежде каждая черта тянулась на всю длину стороны, внутренние черты
            // пересекали внешние, и в углах получались квадратики.
            if (left is not null)
            {
                for (int rank = 0; rank < StrandCount(left); rank++)
                {
                    float x = StrandPos(left, leftX, rank, -1f);
                    float y1 = top is not null ? StrandPos(top, topY, rank, -1f) - StrandHalf(top, scale) : sideTop;
                    float y2 = bottom is not null ? StrandPos(bottom, bottomY, rank, 1f) + StrandHalf(bottom, scale) : sideBottom;
                    DrawParagraphBorderStrand(canvas, left, x, y1, x, y2, scale);
                }
            }

            if (right is not null)
            {
                for (int rank = 0; rank < StrandCount(right); rank++)
                {
                    float x = StrandPos(right, rightX, rank, 1f);
                    float y1 = top is not null ? StrandPos(top, topY, rank, -1f) - StrandHalf(top, scale) : sideTop;
                    float y2 = bottom is not null ? StrandPos(bottom, bottomY, rank, 1f) + StrandHalf(bottom, scale) : sideBottom;
                    DrawParagraphBorderStrand(canvas, right, x, y1, x, y2, scale);
                }
            }

            // Горизонтальные черты доходят до внешних краёв боковых черт своего ранга:
            // углы закрыты. Без боковых — от края до края текста.
            if (top is not null)
            {
                for (int rank = 0; rank < StrandCount(top); rank++)
                {
                    float y = StrandPos(top, topY, rank, -1f);
                    float x1 = left is not null ? StrandPos(left, leftX, rank, -1f) - StrandHalf(left, scale) : textLeftX;
                    float x2 = right is not null ? StrandPos(right, rightX, rank, 1f) + StrandHalf(right, scale) : textRightX;
                    DrawParagraphBorderStrand(canvas, top, x1, y, x2, y, scale);
                }
            }

            if (bottom is not null)
            {
                for (int rank = 0; rank < StrandCount(bottom); rank++)
                {
                    float y = StrandPos(bottom, bottomY, rank, 1f);
                    float x1 = left is not null ? StrandPos(left, leftX, rank, -1f) - StrandHalf(left, scale) : textLeftX;
                    float x2 = right is not null ? StrandPos(right, rightX, rank, 1f) + StrandHalf(right, scale) : textRightX;
                    DrawParagraphBorderStrand(canvas, bottom, x1, y, x2, y, scale);
                }
            }
        }

        /// <summary>
        /// От середины ближней к тексту черты до середины всей линии. У одинарной линии
        /// это ноль — черта одна; у двойной — толщина черты, у тройной — две.
        /// </summary>
        private static float BorderBandInsetPt(SKParagraphBorderLine line)
            => Math.Max(0f, (line.DrawnWidthPt - line.WidthPt) / 2f);

        /// <summary>Сколько черт у линии: две у двойной, три у тройной, одна у прочих.</summary>
        private static int StrandCount(SKParagraphBorderLine line) => line.Style switch
        {
            SKParagraphBorderLine.StyleDouble => 2,
            SKParagraphBorderLine.StyleTriple => 3,
            _ => 1
        };

        /// <summary>
        /// Середина черты. center — середина всей линии, outward — направление наружу
        /// от текста (-1 влево и вверх, +1 вправо и вниз). Черты двойной линии
        /// отстоят от середины на её толщину: внешняя (ранг 0) наружу, внутренняя
        /// (ранг 1) к тексту. У тройной средняя черта (ранг 1) стоит посередине, а
        /// внешняя и внутренняя — на две толщины от неё: черты и просветы между ними
        /// одной толщины. У одинарной линии черта одна и стоит посередине.
        /// </summary>
        private static float StrandPos(SKParagraphBorderLine line, float center, int rank, float outward)
        {
            if (line.Style == SKParagraphBorderLine.StyleDouble)
                return rank == 0 ? center + outward * line.WidthPt : center - outward * line.WidthPt;

            if (line.Style == SKParagraphBorderLine.StyleTriple)
            {
                return rank switch
                {
                    0 => center + outward * line.WidthPt * 2f,
                    1 => center,
                    _ => center - outward * line.WidthPt * 2f
                };
            }

            return center;
        }

        /// <summary>Половина толщины черты такой, какой её нарисует холст.</summary>
        private static float StrandHalf(SKParagraphBorderLine line, float canvasScale)
        {
            float minWidthPt = canvasScale > 0f ? 1f / canvasScale : 0.75f;
            return Math.Max(minWidthPt, line.WidthPt) / 2f;
        }

        /// <summary>
        /// Одна черта линии. Черта двойной линии рисуется сплошной и без привязки к
        /// пикселю: концы черт выверены под углы рамки, и сдвиг одной из них на
        /// полпикселя открыл бы в углу щель.
        /// </summary>
        private static void DrawParagraphBorderStrand(
            SKCanvas canvas, SKParagraphBorderLine line,
            float x1, float y1, float x2, float y2, float canvasScale)
        {
            if (line.Style == SKParagraphBorderLine.StyleDouble
                || line.Style == SKParagraphBorderLine.StyleTriple)
            {
                var strand = new SKParagraphBorderLine
                {
                    Style = SKParagraphBorderLine.StyleSingle,
                    Color = line.Color,
                    WidthPt = line.WidthPt,
                    SpacePt = line.SpacePt
                };
                DrawParagraphBorderLine(canvas, strand, x1, y1, x2, y2, canvasScale, snapToPixel: false);
                return;
            }

            DrawParagraphBorderLine(canvas, line, x1, y1, x2, y2, canvasScale, snapToPixel: true);
        }

        // Узоры заливки: плитка 8×8 клеток, как у Word. Плитки на пару «узор + цвет»
        // строятся один раз в клетках-единицах; размер клетки — пиксель устройства —
        // ставится при отрисовке (см. RenderParagraphBorders).
        private const int ShadingPatternTile = 8;
        private static readonly Dictionary<(string Pattern, uint Color), (SKImage Image, SKShader Shader)?> _shadingPatternCache = new();
        private static readonly object _shadingPatternLock = new();

        // Порядок клеток для процентных узоров (упорядоченное растрирование): при доле
        // p закрашены клетки с номером меньше p·64. Точки ложатся равномерно, без
        // полос, — так выглядят узоры «25 %», «50 %» у Word.
        private static readonly int[,] ShadingDitherOrder =
        {
            {  0, 32,  8, 40,  2, 34, 10, 42 },
            { 48, 16, 56, 24, 50, 18, 58, 26 },
            { 12, 44,  4, 36, 14, 46,  6, 38 },
            { 60, 28, 52, 20, 62, 30, 54, 22 },
            {  3, 35, 11, 43,  1, 33,  9, 41 },
            { 51, 19, 59, 27, 49, 17, 57, 25 },
            { 15, 47,  7, 39, 13, 45,  5, 37 },
            { 63, 31, 55, 23, 61, 29, 53, 21 }
        };

        /// <summary>
        /// Шейдер узора заливки для кисти: клетки узора цветом узора, остальное
        /// прозрачно — под ним виден цвет заливки. null — узора нет или имя незнакомо.
        /// «Авто» в цвете узора — цвет текста листа, как у линий рамки.
        /// </summary>
        private static SKShader? GetShadingPatternShader(string? pattern, string? colorCode)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return null;

            string name = pattern.Trim();
            SKColor color = ResolveParagraphBorderColor(colorCode);
            var key = (name.ToLowerInvariant(), (uint)color);

            lock (_shadingPatternLock)
            {
                if (_shadingPatternCache.TryGetValue(key, out var cached))
                    return cached?.Shader;

                var mask = BuildShadingPatternMask(name);
                if (mask is null)
                {
                    _shadingPatternCache[key] = null;
                    return null;
                }

                using var bitmap = new SKBitmap(ShadingPatternTile, ShadingPatternTile, SKColorType.Rgba8888, SKAlphaType.Premul);
                for (int y = 0; y < ShadingPatternTile; y++)
                    for (int x = 0; x < ShadingPatternTile; x++)
                        bitmap.SetPixel(x, y, mask[y, x] ? color : SKColors.Transparent);

                var image = SKImage.FromBitmap(bitmap);
                var shader = image.ToShader(
                    SKShaderTileMode.Repeat, SKShaderTileMode.Repeat,
                    new SKSamplingOptions(SKFilterMode.Nearest),
                    SKMatrix.Identity);

                _shadingPatternCache[key] = (image, shader);
                return shader;
            }
        }

        /// <summary>
        /// Клетки плитки узора Word по его имени (w:shd w:val). Процентные — равномерные
        /// точки нужной доли; полосы — через клетку-две с шагом в четыре; «thin» — те же
        /// полосы в одну клетку. diagStripe у Word идёт снизу слева вверх направо,
        /// reverseDiagStripe — сверху слева вниз направо. null — имя незнакомо.
        /// </summary>
        private static bool[,]? BuildShadingPatternMask(string pattern)
        {
            var mask = new bool[ShadingPatternTile, ShadingPatternTile];
            string p = pattern.ToLowerInvariant();

            // Четверть у Word — точки через пиксель в каждой второй строке, и строки
            // сдвинуты друг относительно друга на пиксель: точки стоят вразбежку, а не
            // сеткой, как дало бы растрирование.
            if (p == "pct25")
            {
                for (int y = 0; y < ShadingPatternTile; y++)
                    for (int x = 0; x < ShadingPatternTile; x++)
                        mask[y, x] = (y & 1) == 0 && ((x + (y >> 1)) & 1) == 0;
                return mask;
            }

            if (p.StartsWith("pct", StringComparison.Ordinal)
                && int.TryParse(p.AsSpan(3), System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int percent))
            {
                int threshold = (int)Math.Round(Math.Clamp(percent, 0, 100) * 64 / 100.0);
                for (int y = 0; y < ShadingPatternTile; y++)
                    for (int x = 0; x < ShadingPatternTile; x++)
                        mask[y, x] = ShadingDitherOrder[y, x] < threshold;
                return mask;
            }

            Func<int, int, bool>? on = p switch
            {
                "horzstripe" => (x, y) => (y & 3) < 2,
                "vertstripe" => (x, y) => (x & 3) < 2,
                "diagstripe" => (x, y) => ((x + y) & 3) < 2,
                "reversediagstripe" => (x, y) => ((x - y) & 3) < 2,
                "horzcross" => (x, y) => (y & 3) < 2 || (x & 3) < 2,
                "diagcross" => (x, y) => ((x + y) & 3) < 2 || ((x - y) & 3) < 2,
                "thinhorzstripe" => (x, y) => (y & 3) == 0,
                "thinvertstripe" => (x, y) => (x & 3) == 0,
                "thindiagstripe" => (x, y) => ((x + y) & 3) == 0,
                "thinreversediagstripe" => (x, y) => ((x - y) & 3) == 0,
                "thinhorzcross" => (x, y) => (y & 3) == 0 || (x & 3) == 0,
                "thindiagcross" => (x, y) => ((x + y) & 3) == 0 || ((x - y) & 3) == 0,
                _ => null
            };

            if (on is null) return null;

            for (int y = 0; y < ShadingPatternTile; y++)
                for (int x = 0; x < ShadingPatternTile; x++)
                    mask[y, x] = on(x, y);

            return mask;
        }

        /// <summary>
        /// Цвет заливки абзаца. «Авто» и нераспознанное — заливки нет.
        /// </summary>
        private static bool TryParseShading(string? code, out SKColor color)
        {
            color = SKColors.Transparent;
            if (string.IsNullOrWhiteSpace(code)) return false;
            if (!SKColor.TryParse(code, out color)) return false;
            return color.Alpha > 0;
        }

        /// <summary>
        /// Рамки абзацев одной ячейки при печати. Черта соединяется только с соседом
        /// по той же ячейке: абзацы разных ячеек в одну врезку не складываются.
        /// </summary>
        private static void RenderCellParagraphBorders(
            SKCanvas canvas, IReadOnlyList<SKTableParaLayout> paragraphs, int index,
            float textLeftX, float textTopY)
        {
            var layout = paragraphs[index].Layout;
            if (layout is null || (layout.Borders is null && !layout.HasShading)) return;

            bool joinPrev = index > 0 && BordersJoin(paragraphs[index - 1].Layout, layout);
            bool joinNext = index + 1 < paragraphs.Count && BordersJoin(layout, paragraphs[index + 1].Layout);

            bool shadeJoinPrev = index > 0 && ShadingJoin(paragraphs[index - 1].Layout, layout);
            bool shadeJoinNext = index + 1 < paragraphs.Count && ShadingJoin(layout, paragraphs[index + 1].Layout);

            RenderParagraphBorders(canvas, layout, textLeftX, textTopY,
                0, layout.Lines.Count, joinPrev, joinNext,
                shadeJoinPrev, shadeJoinNext);
        }

        /// <summary>Во сколько пикселей ложится пункт — по текущей матрице холста.</summary>
        private static float CanvasScale(SKCanvas canvas)
        {
            var m = canvas.TotalMatrix;
            float scale = MathF.Sqrt(m.ScaleX * m.ScaleX + m.SkewY * m.SkewY);
            return scale < 0.01f ? 1f : scale;
        }

        /// <summary>
        /// Одна линия рамки. Одиночная тонкая линия ставится на целый пиксель, как
        /// рамки ячеек, — иначе на экране она расплывается в две бледные полосы.
        /// </summary>
        private static void DrawParagraphBorderLine(
            SKCanvas canvas, SKParagraphBorderLine line,
            float x1, float y1, float x2, float y2, float canvasScale,
            bool snapToPixel = true)
        {
            SKColor color = ResolveParagraphBorderColor(line.Color);

            float minWidthPt = canvasScale > 0f ? 1f / canvasScale : 0.75f;
            float strokeWidth = Math.Max(minWidthPt, line.WidthPt);

            bool vertical = Math.Abs(x1 - x2) < 0.01f;

            using var paint = new SKPaint
            {
                Color = color,
                StrokeWidth = strokeWidth,
                IsStroke = true,
                IsAntialias = true
            };

            switch (line.Style)
            {
                case SKParagraphBorderLine.StyleDashed:
                    paint.PathEffect = SKPathEffect.CreateDash(
                        new[] { strokeWidth * 4f, strokeWidth * 2f }, 0);
                    break;

                case SKParagraphBorderLine.StyleDotted:
                    paint.StrokeCap = SKStrokeCap.Round;
                    paint.PathEffect = SKPathEffect.CreateDash(
                        new[] { 0.01f, strokeWidth * 2f }, 0);
                    break;
            }

            if (line.Style == SKParagraphBorderLine.StyleWave)
            {
                DrawParagraphBorderWave(canvas, paint, line.WidthPt, minWidthPt, x1, y1, x2, y2, vertical);
                return;
            }

            if (line.Style == SKParagraphBorderLine.StyleTriple)
            {
                // Три черты толщиной в линию с просветами той же толщины: крайние
                // отстоят от середины на две толщины.
                float offset = line.WidthPt * 2f;

                if (vertical)
                {
                    canvas.DrawLine(x1 - offset, y1, x2 - offset, y2, paint);
                    canvas.DrawLine(x1, y1, x2, y2, paint);
                    canvas.DrawLine(x1 + offset, y1, x2 + offset, y2, paint);
                }
                else
                {
                    canvas.DrawLine(x1, y1 - offset, x2, y2 - offset, paint);
                    canvas.DrawLine(x1, y1, x2, y2, paint);
                    canvas.DrawLine(x1, y1 + offset, x2, y2 + offset, paint);
                }

                paint.PathEffect?.Dispose();
                return;
            }

            if (line.Style == SKParagraphBorderLine.StyleDouble)
            {
                // Две черты толщиной в линию с просветом той же толщины: центры черт
                // отстоят от середины на толщину линии в обе стороны.
                float offset = line.WidthPt;

                if (vertical)
                {
                    canvas.DrawLine(x1 - offset, y1, x2 - offset, y2, paint);
                    canvas.DrawLine(x1 + offset, y1, x2 + offset, y2, paint);
                }
                else
                {
                    canvas.DrawLine(x1, y1 - offset, x2, y2 - offset, paint);
                    canvas.DrawLine(x1, y1 + offset, x2, y2 + offset, paint);
                }

                paint.PathEffect?.Dispose();
                return;
            }

            if (snapToPixel
                && line.Style == SKParagraphBorderLine.StyleSingle && strokeWidth * canvasScale <= 1.5f)
            {
                paint.IsAntialias = false;

                if (vertical)
                {
                    float xPx = (float)Math.Round(x1 * canvasScale - 0.5f) + 0.5f;
                    x1 = x2 = xPx / canvasScale;
                }
                else
                {
                    float yPx = (float)Math.Round(y1 * canvasScale - 0.5f) + 0.5f;
                    y1 = y2 = yPx / canvasScale;
                }
            }

            canvas.DrawLine(x1, y1, x2, y2, paint);
            paint.PathEffect?.Dispose();
        }

        /// <summary>
        /// Волнистая линия рамки: зигзаг вдоль стороны. Размах от гребня до впадины —
        /// WaveSpanShare толщин линии (столько места линия и занимает, см. DrawnWidthPt), шаг —
        /// две с половиной толщины, сама черта — в половину толщины, но не тоньше
        /// пикселя. Такой её рисует Word: тонкая частая волна, а не толстая полоса.
        ///
        /// Число волн на стороне целое: зигзаг начинается и кончается на одной высоте,
        /// и волны соседних сторон сходятся в углах, а не обрываются на полуволне.
        /// </summary>
        private static void DrawParagraphBorderWave(
            SKCanvas canvas, SKPaint paint, float widthPt, float minWidthPt,
            float x1, float y1, float x2, float y2, bool vertical)
        {
            float stroke = Math.Max(minWidthPt, widthPt * 0.5f);
            paint.StrokeWidth = stroke;
            paint.StrokeJoin = SKStrokeJoin.Round;
            paint.StrokeCap = SKStrokeCap.Round;
            paint.PathEffect = null;

            float amplitude = Math.Max(0f, widthPt * WaveSpanShare - stroke) / 2f;
            float halfPeriod = Math.Max(1f, widthPt * 1.25f);

            float start = vertical ? Math.Min(y1, y2) : Math.Min(x1, x2);
            float end = vertical ? Math.Max(y1, y2) : Math.Max(x1, x2);
            float length = end - start;
            if (length <= 0f) return;

            int halves = Math.Max(2, (int)Math.Round(length / halfPeriod));
            if (halves % 2 != 0) halves++;
            float step = length / halves;

            using var path = new SKPath();
            for (int i = 0; i <= halves; i++)
            {
                float along = start + step * i;
                float across = i % 2 == 0 ? -amplitude : amplitude;

                float px = vertical ? x1 + across : along;
                float py = vertical ? along : y1 + across;

                if (i == 0) path.MoveTo(px, py);
                else path.LineTo(px, py);
            }

            canvas.DrawPath(path, paint);
        }

        /// <summary>
        /// Размах волнистой линии рамки от гребня до впадины в толщинах линии. Совпадает с
        /// местом, которое линия занимает (SKParagraphBorderLine.DrawnWidthPt): волна Word
        /// заметно выше своей толщины — при 1,5 пт около трёх с половиной пунктов.
        /// </summary>
        private const float WaveSpanShare = 2.75f;

        /// <summary>
        /// Цвет линии рамки. «Авто» — цвет текста листа: на тёмной бумаге черта
        /// светлеет вместе с буквами. Нейтральная линия в чтении перекрашивается
        /// под бумагу книги так же, как рамки таблиц; цвет, выбранный автором
        /// сознательно, остаётся его цветом.
        /// </summary>
        private static SKColor ResolveParagraphBorderColor(string? code)
        {
            if (string.IsNullOrWhiteSpace(code) || !SKColor.TryParse(code, out var color))
                return ReadingBorderColorOverride ?? DefaultTextColorOverride ?? SKColors.Black;

            if (ReadingBorderColorOverride is { } readingBorder && IsNeutralInk(color))
                return readingBorder;

            return color;
        }

        // Признак того, что строка-код описывает градиент (а не обычный hex).
        private static bool IsGradientCode(string? code)
            => code != null && code.StartsWith("grad|", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Градиент ли это, а не обычный цвет. Наружу — чтобы всё, что умеет красить
        /// цветом, тем же значением умело красить и градиентом: они хранятся одной
        /// строкой и приходят из одного и того же выбора цвета.
        /// </summary>
        public static bool IsGradient(string? code) => IsGradientCode(code);

        /// <summary>
        /// Сплошной цвет градиента: им заливают там, где шейдер не нужен или не
        /// получился. Для обычного цвета возвращает его самого.
        /// </summary>
        public static SKColor GradientSolidColor(string? code, SKColor fallback)
        {
            if (string.IsNullOrWhiteSpace(code)) return fallback;

            if (!IsGradientCode(code))
                return SKColor.TryParse(code, out var plain) ? plain : fallback;

            try
            {
                return GradientShaderFactory.SolidColor(GradientSpec.Parse(code));
            }
            catch
            {
                return fallback;
            }
        }

        /// <summary>
        /// Шейдер градиента, растянутый на заданный прямоугольник. null — значение
        /// градиентом не является или разобрать его не удалось.
        /// </summary>
        public static SKShader? BuildGradientShader(string? code, SKRect rect)
        {
            if (!IsGradientCode(code)) return null;

            try
            {
                return GradientShaderFactory.BuildShader(GradientSpec.Parse(code), rect);
            }
            catch
            {
                return null;
            }
        }

        // ── Сборка токенов ────────────────────────────────────────────────

        /// <summary>
        /// Собирает список токенов (символ + форматирование) из runs параграфа.
        /// Для каждого символа проверяет наличие глифа в назначенном шрифте.
        /// Если глиф отсутствует — подставляет системный фолбэк через SKFontManager.
        /// </summary>
        /// <summary>
        /// Метрики шрифта так, как их видит вёрстка: |ascent|, descent и зазор
        /// строки в пунктах. Нужны для сверки высоты строки с другими редакторами.
        /// </summary>
        public static (float Ascent, float Descent, float Leading) ProbeFontMetrics(
            string family, float sizePt, bool bold, bool italic)
        {
            var typeface = GetOrCreateTypeface(family, bold, italic);
            var font = GetOrCreateFont(typeface, sizePt);
            font.GetFontMetrics(out var metrics);

            return (Math.Abs(metrics.Ascent), Math.Abs(metrics.Descent), Math.Abs(metrics.Leading));
        }

        /// <summary>
        /// Формат, которым меряется пустая строка абзаца: собственные свойства рана,
        /// если они есть (их проставляет ввод), иначе шрифт стиля абзаца.
        /// </summary>
        private static SKRunSegment BuildEmptyLineFormat(
            ParagraphBlock para, string? styleName, StyleResolver styles)
        {
            Models.Inline.RunProperties? props = null;

            foreach (var chunk in para.Chunks)
            {
                foreach (var run in chunk.Runs)
                    if (run.Properties is not null) { props = run.Properties; break; }

                if (props is not null) break;
            }

            string family = !string.IsNullOrEmpty(props?.FontFamily)
                ? props!.FontFamily!
                : styles.ResolveFontFamily(styleName);

            float size = props?.FontSize.HasValue == true
                ? (float)props.FontSize.Value
                : styles.ResolveFontSize(styleName);

            return new SKRunSegment
            {
                FontFamily = ResolveReadingFamily(family),
                FontSizePt = ScaleReadingFont(size),
                IsBold = props?.IsBold ?? styles.ResolveBold(styleName),
                IsItalic = props?.IsItalic ?? styles.ResolveItalic(styleName)
            };
        }

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
            float styleSpacing = styles.ResolveCharacterSpacing(styleName);
            bool styleAllCaps = styles.ResolveAllCaps(styleName);
            bool styleSmallCaps = styles.ResolveSmallCaps(styleName);

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
                            FontFamily = ResolveReadingFamily(styleFontFamily),
                            FontSizePt = ScaleReadingFont(styleFontSize),
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

                    // Символьный стиль — слой между стилем абзаца и собственным
                    // форматированием фрагмента. Порядок силы такой: абзац даёт основу,
                    // символьный стиль её переопределяет, прямое форматирование сильнее
                    // обоих — его ставят руками, и по нему человек и ждёт результата.
                    //
                    // Стиль задаёт только то, что в нём написано: если гарнитуры в его
                    // цепочке нет, остаётся гарнитура абзаца.
                    string baseFamily = styleFontFamily;
                    float baseSize = styleFontSize;
                    bool styleAddsBold = false;
                    bool styleAddsItalic = false;
                    string? baseColor = null;
                    float baseSpacing = styleSpacing;
                    bool styleAddsAllCaps = false;
                    bool styleAddsSmallCaps = false;

                    if (!string.IsNullOrEmpty(p?.StyleName))
                    {
                        baseFamily = styles.FindFontFamily(p!.StyleName) ?? baseFamily;
                        baseSize = styles.FindFontSize(p.StyleName) ?? baseSize;
                        baseSpacing = styles.FindCharacterSpacing(p.StyleName) ?? baseSpacing;
                        styleAddsAllCaps = styles.AnyAllCaps(p.StyleName);
                        styleAddsSmallCaps = styles.AnySmallCaps(p.StyleName);
                        styleAddsBold = styles.AnyBold(p.StyleName);
                        styleAddsItalic = styles.AnyItalic(p.StyleName);
                        baseColor = styles.FindTextColor(p.StyleName);
                    }

                    string resolvedFamily = !string.IsNullOrEmpty(p?.FontFamily)
                        ? p!.FontFamily : baseFamily;
                    float resolvedSize = p?.FontSize.HasValue == true
                        ? (float)p.FontSize.Value : baseSize;

                    // Подмена чтения. Стоит именно здесь, после разбора собственных
                    // свойств run-а: читатель просит показать ему всю книгу одним
                    // шрифтом, включая абзацы со своим начертанием, а в самой рукописи
                    // при этом не меняется ничего.
                    resolvedFamily = ResolveReadingFamily(resolvedFamily);
                    float unscaledSize = resolvedSize;
                    resolvedSize = ScaleReadingFont(resolvedSize);

                    // Разрядка меряется в пунктах и растёт вместе с кеглем, если чтение
                    // увеличило шрифт: иначе увеличенный текст выглядел бы плотнее исходного.
                    float resolvedSpacing = p?.CharacterSpacing.HasValue == true
                        ? (float)p.CharacterSpacing!.Value
                        : baseSpacing;
                    if (unscaledSize > 0f)
                        resolvedSpacing *= resolvedSize / unscaledSize;
                    // Жирность и курсив: задано у фрагмента — решает фрагмент, в том числе
                    // когда жирность снята руками. Не задано — символьный стиль добавляет
                    // выделение поверх стиля абзаца (снять его стиль не может, см.
                    // StyleResolver.AnyBold). Раньше решал уже сам факт свойств у фрагмента:
                    // покрашенное слово в заголовке получало «не жирный» и теряло жирность.
                    bool resolvedBold = p?.IsBold ?? (styleAddsBold || styleBold);
                    bool resolvedItalic = p?.IsItalic ?? (styleAddsItalic || styleItalic);

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

                    // Смещение от базовой линии (w:position) — поверх индекса, в пунктах,
                    // и растёт вместе с кеглем, если чтение увеличило шрифт.
                    if (p?.BaselineOffset is double baselineOffset)
                    {
                        float offsetScale = unscaledSize > 0f ? resolvedSize / unscaledSize : 1f;
                        baselineShift += (float)baselineOffset * offsetScale;
                    }

                    // Масштаб знаков по ширине (w:w): 1 — обычная ширина.
                    float horizontalScale = p?.CharacterScale is int scalePct && scalePct > 0
                        ? scalePct / 100f
                        : 1f;

                    // Скрытый текст (w:vanish): при выключенных непечатаемых знаках он не
                    // рисуется и не занимает места, при включённых — виден с точечным
                    // подчёркиванием. Знаки в любом случае остаются в раскладке: по ним
                    // считаются позиции каретки.
                    bool hiddenCollapsed = p?.IsHidden == true && !styles.ShowHiddenText;
                    bool hiddenMarked = p?.IsHidden == true && styles.ShowHiddenText;

                    // Настраиваемые эффекты букв (контур, тень, свечение, отражение) —
                    // один раз на ран: сегменты рана делят одну запись.
                    SKTextEffects? effects = ToRenderEffects(p?.Effects);

                    var format = new SKRunSegment
                    {
                        FontFamily = resolvedFamily,
                        FontSizePt = segFontSize,
                        BaselineShiftPt = baselineShift,
                        CharacterSpacingPt = resolvedSpacing,
                        HorizontalScale = horizontalScale,
                        IsBold = resolvedBold,
                        IsItalic = resolvedItalic,
                        IsUnderline = p?.IsUnderline ?? false,
                        UnderlineStyle = (int)(p?.UnderlineStyle ?? default),
                        UnderlineColor = p?.UnderlineColor,
                        IsStrikethrough = p?.IsStrikethrough ?? false,
                        IsDoubleStrikethrough = p?.IsDoubleStrikethrough ?? false,
                        IsHidden = hiddenCollapsed,
                        IsHiddenMarked = hiddenMarked,
                        IsOutline = p?.IsOutline ?? false,
                        IsShadow = p?.IsShadow ?? false,
                        IsEmboss = p?.IsEmboss ?? false,
                        IsImprint = p?.IsImprint ?? false,
                        EmphasisMark = (int)(p?.EmphasisMark ?? default),
                        CharBorderColor = p?.CharBorderColor,
                        CharBorderWidthPt = (float)(p?.CharBorderWidthPt ?? 0),
                        CharBorderStyle = (int)(p?.CharBorderStyle ?? default),
                        Effects = effects,
                        Color = ParseColor(p?.TextColor ?? baseColor),
                        HighlightColor = ParseHighlight(p?.HighlightColor),
                        ColorCode = p?.TextColor ?? baseColor,
                        HighlightCode = p?.HighlightColor,
                        GlobalCharOffset = globalIndex
                    };

                    // «Все прописные» и «малые прописные» — форматирование, а не текст: в
                    // рукописи буквы остаются как набраны, прописными они только рисуются.
                    // Так ведёт себя Word, и так снятие признака возвращает строчные на место.
                    // Раньше признак читался импортом и хранился, но вёрстка его не знала:
                    // заголовок, у которого в стиле стоят прописные, выходил строчными.
                    //
                    // Малые прописные — строчные буквы рисуются прописными уменьшенного кегля,
                    // прописные остаются как есть. Прописные перекрывают малые, как в Word.
                    bool resolvedAllCaps = p?.IsAllCaps == true || styleAddsAllCaps || styleAllCaps;
                    bool resolvedSmallCaps = !resolvedAllCaps
                        && (p?.IsSmallCaps == true || styleAddsSmallCaps || styleSmallCaps);

                    SKRunSegment? smallCapsFormat = resolvedSmallCaps
                        ? new SKRunSegment
                        {
                            FontFamily = resolvedFamily,
                            FontSizePt = segFontSize * SmallCapsScale,
                            BaselineShiftPt = baselineShift,
                            CharacterSpacingPt = resolvedSpacing,
                            HorizontalScale = horizontalScale,
                            IsBold = resolvedBold,
                            IsItalic = resolvedItalic,
                            IsUnderline = p?.IsUnderline ?? false,
                            UnderlineStyle = (int)(p?.UnderlineStyle ?? default),
                            UnderlineColor = p?.UnderlineColor,
                            IsStrikethrough = p?.IsStrikethrough ?? false,
                            IsDoubleStrikethrough = p?.IsDoubleStrikethrough ?? false,
                            IsHidden = hiddenCollapsed,
                            IsHiddenMarked = hiddenMarked,
                            IsOutline = p?.IsOutline ?? false,
                            IsShadow = p?.IsShadow ?? false,
                            IsEmboss = p?.IsEmboss ?? false,
                            IsImprint = p?.IsImprint ?? false,
                            EmphasisMark = (int)(p?.EmphasisMark ?? default),
                            CharBorderColor = p?.CharBorderColor,
                            CharBorderWidthPt = (float)(p?.CharBorderWidthPt ?? 0),
                            CharBorderStyle = (int)(p?.CharBorderStyle ?? default),
                            Effects = effects,
                            Color = ParseColor(p?.TextColor ?? baseColor),
                            HighlightColor = ParseHighlight(p?.HighlightColor),
                            ColorCode = p?.TextColor ?? baseColor,
                            HighlightCode = p?.HighlightColor,
                            GlobalCharOffset = globalIndex
                        }
                        : null;

                    // Получаем typeface один раз на run для проверки глифов.
                    var typeface = GetOrCreateTypeface(resolvedFamily, resolvedBold, resolvedItalic);

                    foreach (char ch in run.Text)
                    {
                        SKRunSegment charFormat = format;
                        char drawCh = ch;

                        // Замена регистра — знак в знак: длина текста не меняется, и каретка,
                        // выделение и поиск продолжают считать по буквам рукописи.
                        if (resolvedAllCaps)
                        {
                            drawCh = char.ToUpper(ch, System.Globalization.CultureInfo.CurrentCulture);
                        }
                        else if (smallCapsFormat is not null && char.IsLower(ch))
                        {
                            drawCh = char.ToUpper(ch, System.Globalization.CultureInfo.CurrentCulture);
                            charFormat = smallCapsFormat;
                        }

                        // Управляющие символы (\r, \n и прочие C0 < U+0020) не имеют глифа и
                        // рисуются шрифтом как .notdef — квадрат (□). Затекают в текст ячейки при
                        // вставке многострочного текста. Рисуем как пробел, сохраняя счётчик
                        // символов, чтобы каретка/хит-тест не смещались.
                        //
                        // Табуляция из этого правила исключена. Она не знак, а прыжок к отметке
                        // на строке, и ширину ей даёт позиция табуляции, а не шрифт. Пока она
                        // подменялась пробелом, вёрстка не видела её вовсе: весь разбор позиций
                        // ниже ищет токен "\t" и получал пробел. Номер страницы в оглавлении
                        // из-за этого вставал вплотную к названию, дорожка точек не рисовалась,
                        // а длинная строка уносила номер на перенос.
                        //
                        // Перенос строки внутри абзаца (\n — Shift+Enter, w:br, w:cr) тоже не
                        // пробел: вёрстка по нему обрывает строку, как Word. Рисовать его
                        // нечем, и он не рисуется (сегмент IsLineBreak).
                        if (ch < ' ' && ch != '\t' && ch != '\n') drawCh = ' ';

                        // Проверяем глифы только для символов вне Basic Latin (U+0080+).
                        // Basic Latin всегда есть в любом текстовом шрифте — проверять незачем,
                        // а MatchCharacter для них может вернуть Marlett/Wingdings.
                        // Неразрывный дефис и мягкий перенос рисуются дефисом самой
                        // гарнитуры (или не рисуются вовсе) — подменять им шрифт незачем.
                        if (!char.IsSurrogate(drawCh) && drawCh >= '\u0080'
                            && drawCh != '\u2011' && drawCh != '\u00AD')
                        {
                            int codepoint = drawCh;
                            if (typeface.GetGlyph(codepoint) == 0)
                            {
                                string? fallbackFamily = FindFallbackFamily(codepoint, styles);
                                bool fallbackBold = resolvedBold;
                                bool fallbackItalic = resolvedItalic;

                                if (fallbackFamily is null
                                    && FindSymbolFallback(codepoint, resolvedFamily, resolvedBold, resolvedItalic) is { } symbol)
                                {
                                    fallbackFamily = symbol.Family;
                                    fallbackBold = symbol.Bold;
                                    fallbackItalic = symbol.Italic;
                                }

                                // Подстановка — другая гарнитура либо другое начертание той же:
                                // знак бывает в прямом начертании гарнитуры и отсутствует в её курсиве.
                                if (fallbackFamily != null
                                    && (fallbackFamily != resolvedFamily
                                        || fallbackBold != resolvedBold
                                        || fallbackItalic != resolvedItalic))
                                {
                                    charFormat = new SKRunSegment
                                    {
                                        FontFamily = fallbackFamily,
                                        FontSizePt = charFormat.FontSizePt,
                                        BaselineShiftPt = baselineShift,
                                        CharacterSpacingPt = resolvedSpacing,
                                        HorizontalScale = horizontalScale,
                                        IsBold = fallbackBold,
                                        IsItalic = fallbackItalic,
                                        IsUnderline = p?.IsUnderline ?? false,
                                        UnderlineStyle = (int)(p?.UnderlineStyle ?? default),
                                        UnderlineColor = p?.UnderlineColor,
                                        IsStrikethrough = p?.IsStrikethrough ?? false,
                                        IsDoubleStrikethrough = p?.IsDoubleStrikethrough ?? false,
                                        IsHidden = hiddenCollapsed,
                                        IsHiddenMarked = hiddenMarked,
                                        IsOutline = p?.IsOutline ?? false,
                                        IsShadow = p?.IsShadow ?? false,
                                        IsEmboss = p?.IsEmboss ?? false,
                                        IsImprint = p?.IsImprint ?? false,
                                        EmphasisMark = (int)(p?.EmphasisMark ?? default),
                                        CharBorderColor = p?.CharBorderColor,
                                        CharBorderWidthPt = (float)(p?.CharBorderWidthPt ?? 0),
                                        CharBorderStyle = (int)(p?.CharBorderStyle ?? default),
                                        Effects = effects,
                                        Color = ParseColor(p?.TextColor ?? baseColor),
                                        HighlightColor = ParseHighlight(p?.HighlightColor),
                                        ColorCode = p?.TextColor ?? baseColor,
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
            SKLineSpacing lineSpacing,
            IReadOnlyList<SKWrapZone>? wrapZones = null,
            bool wrapPreferPushDown = false,
            WrapPageContext? wrapPages = null,
            SKRunSegment? emptyLineFormat = null,
            bool breakOnHyphen = true,
            IReadOnlyList<Models.Styles.TabStop>? tabStops = null,
            float defaultTabStopPt = 35.4f)
        {
            // Сохраняем ширину текстовой области — используется в ComputeAlignmentOffset.
            // textAreaWidthPt = availableWidthPt - leftIndentPt - rightIndentPt,
            // т.е. именно то пространство в котором располагаются строки.
            layout.TextAreaWidthPt = textAreaWidthPt;

            // Табуляции-черты — не позиции для прыжка: они уходят в раскладку как
            // вертикальные линии, а символ табуляции ищет только настоящие позиции. Если
            // у абзаца одни черты, табуляция идёт по шагу по умолчанию, как у Word.
            //
            // На листе чтения, уже листа рукописи, черта правее текстовой области не
            // рисуется: там у неё нет поля, и она легла бы за обрез листа.
            if (tabStops is { Count: > 0 })
            {
                bool hasBar = false;
                foreach (var stop in tabStops)
                {
                    if (stop.Alignment != Models.Styles.TabAlignment.Bar) continue;
                    hasBar = true;
                    break;
                }

                if (hasBar)
                {
                    var bars = new List<float>();
                    var jumps = new List<Models.Styles.TabStop>(tabStops.Count);

                    foreach (var stop in tabStops)
                    {
                        if (stop.Alignment != Models.Styles.TabAlignment.Bar)
                        {
                            jumps.Add(stop);
                            continue;
                        }

                        float barPt = (float)stop.PositionPt;
                        if (barPt < 0f) continue;
                        if (ReadingContentScale < 0.999f && barPt > textAreaWidthPt + 0.01f) continue;

                        bars.Add(barPt);
                    }

                    layout.BarTabPositionsPt = bars.Count > 0 ? bars : null;
                    tabStops = jumps;
                }
            }

            // Отметка табуляции правее текстовой области подрезается по её краю.
            //
            // Позиция отметки — величина запечённая: у строк оглавления её ставит
            // сборщик по ширине текста на листе рукописи. Стоит той ширине стать
            // меньше — уже лист чтения, другие поля, колонки, — и правая отметка
            // остаётся снаружи: дорожка точек с номером страницы уходит за обрез, и
            // видно это на каждой строке оглавления сразу.
            //
            // Подрезка идёт по копиям: сами позиции абзаца принадлежат рукописи и
            // должны вернуться, как только ширина станет прежней.
            if (tabStops is { Count: > 0 })
            {
                List<Models.Styles.TabStop>? clipped = null;

                for (int i = 0; i < tabStops.Count; i++)
                {
                    if (tabStops[i].PositionPt <= textAreaWidthPt + 0.01) continue;

                    clipped ??= new List<Models.Styles.TabStop>(tabStops);

                    var moved = tabStops[i].Clone();
                    moved.PositionPt = textAreaWidthPt;
                    clipped[i] = moved;
                }

                if (clipped is not null) tabStops = clipped;
            }

            if (tokens.Count == 0)
            {
                var emptyLine = BuildEmptyLine(layout, lineSpacing, emptyLineFormat);
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

                return lineSpacing.ResolveProbe(ascent, descent, Math.Abs(m.Leading));
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

                // Полоса ЛИСТА, на котором стоит эта строка, в координатах абзаца.
                // Строка обтекает только объекты своего листа: соседний лист — другая
                // бумага, картинки на ней не видно, и двигать текст там нечем.
                //
                // Без этой границы строка, ушедшая на следующую страницу, продолжала
                // считаться с зонами предыдущей. Ярче всего это после вытеснения: первая
                // строка не влезает сбоку от объекта (длинные слова), уезжает вниз и
                // тянет за собой весь хвост абзаца — на чистом листе открывается коридор
                // вокруг картинки, оставшейся страницей выше.
                float sheetTopRelPt = float.NegativeInfinity;
                float sheetBottomRelPt = float.PositiveInfinity;

                // Границы листа пересчитываются каждый раз, когда строка переезжает на
                // следующую страницу: дальше она живёт уже на другой бумаге.
                void UpdateSheetBounds()
                {
                    if (wrapPages is not { } sheetCtx) return;

                    sheetTopRelPt = pageCrossings > 0
                        ? sheetCtx.NextPageTopPt
                            + (pageCrossings - 1) * sheetCtx.PageStepPt
                            - sheetCtx.ParaStartYPt
                        : float.NegativeInfinity;

                    sheetBottomRelPt = sheetCtx.PageBottomPt
                        + pageCrossings * sheetCtx.PageStepPt
                        - sheetCtx.ParaStartYPt;
                }

                UpdateSheetBounds();

                float extraTop = 0f;

                // Нижний край препятствий последней проверенной вертикали. Нужен на
                // выходе из цикла: там строке отдаётся полная колонка, и без этого
                // значения увести её из-под объекта уже нечем.
                float lastPushBottomPt = float.MinValue;

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
                        // Объект с чужого листа для этой строки не существует.
                        if (z.BottomPt <= sheetTopRelPt || z.TopPt >= sheetBottomRelPt) continue;

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

                    lastPushBottomPt = pushBottom;

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

                    // Вытеснение не переходит границу листа. Если под объектом до низа
                    // страницы места уже нет, строка не остаётся висеть у края, а
                    // переезжает на верх следующего листа целиком — и с этого момента
                    // считается его жительницей: объекты прежнего листа для неё и для
                    // всех строк ниже перестают существовать.
                    //
                    // Прежде здесь стоял выход из цикла. Строка оставалась на месте, а
                    // на следующий лист её уносила уже пагинация — о чём вёрстка не
                    // знала. Строки ниже продолжали считаться с зонами прежнего листа и
                    // приходили на чистую бумагу с коридором вокруг картинки, которой
                    // там нет. Ровно тот случай, когда вытесненная строка уходит на
                    // следующую страницу: пока она оставалась на своей, всё считалось
                    // верно.
                    if (wrapPages is { } pageCtx)
                    {
                        float pushedDocYPt = pageCtx.ParaStartYPt + yTop + nextExtraTop;
                        float pageBottomPt = pageCtx.PageBottomPt + pageCrossings * pageCtx.PageStepPt;

                        if (pushedDocYPt + lineHPt > pageBottomPt)
                        {
                            float nextTopPt = pageCtx.NextPageTopPt
                                + pageCrossings * pageCtx.PageStepPt;
                            float docYNowPt = pageCtx.ParaStartYPt + yTop;
                            float toNextSheetPt = nextTopPt - docYNowPt;

                            // Уже ниже верха следующего листа — двигать нечем, иначе
                            // строка поехала бы вверх.
                            if (toNextSheetPt <= extraTop) break;

                            extraTop = toNextSheetPt;
                            pageCrossings++;
                            UpdateSheetBounds();
                            continue;
                        }
                    }

                    extraTop = nextExtraTop;
                }

                // Свободной полосы не нашлось, и опускать строку дальше нельзя. Для
                // обычного текста полная колонка здесь допустима: слово всё равно
                // придётся рвать, и рвать его лучше в полной строке. Но требование
                // шире половины колонки может прийти только от встроенной картинки —
                // BandRequirement обрезает текстовое требование этим потолком и
                // поднимает его выше только под габарит объекта. Картинку рвать
                // нечем, и полная колонка означала бы её поверх обтекаемого объекта:
                // уводим строку под нижний край препятствия.
                if (lastPushBottomPt > float.MinValue
                    && requiredWidthPt > textAreaWidthPt * 0.5f)
                {
                    float belowPt = lastPushBottomPt - yTop + 0.5f;
                    if (belowPt > extraTop) extraTop = belowPt;
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

            // Пробел, который не поместился в строку и потому в неё не лёг (ветка пробела
            // в цикле токенов). Слово за ним встаёт в ту же строку только вместе с ним:
            // в Word пробел между ними есть, и без этого слово прилипало бы к предыдущему
            // вплотную («место,и»), занимая строку, в которую у Word не входит.
            SKLineLayout? droppedSpaceLine = null;
            int droppedSpaceFrag = -1;
            float droppedSpaceWidth = 0f;
            SKRunSegment? droppedSpaceFormat = null;
            int droppedSpaceIdx = -1;

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

            // ── Табуляция ─────────────────────────────────────────────────
            //
            // Символ табуляции — не «широкий пробел», а прыжок к назначенной точке строки:
            // его ширина считается по позиции табуляции, а не по шрифту. На этом держится
            // всё, что должно стоять столбиком без таблицы — номера страниц в оглавлении
            // прежде всего.
            //
            // Правая, средняя и десятичная позиции узнают свою ширину задним числом: пока
            // кусок текста за прыжком не кончился, неизвестно, насколько его двигать. Такой
            // прыжок откладывается и закрывается на следующем табе или в конце строки —
            // тогда сегменты за ним сдвигаются, а сам прыжок дорастает на ту же величину.

            // Прыжок не может быть нулевым: два таба подряд слились бы в один, и текст
            // встал бы на место предыдущего.
            const float MinTabJumpPt = 1f;

            float tabStepPt = defaultTabStopPt > 1f ? defaultTabStopPt : 35.4f;

            SKRunSegment? pendingTabSeg = null;
            Models.Styles.TabStop? pendingTabStop = null;
            int pendingContentSegIdx = -1;
            float pendingContentStartW = 0f;

            // Формат и место самого знака табуляции. Нужны, чтобы прыжок, за которым на
            // строке ничего не встало, можно было заново выпустить на следующей строке.
            SKRunSegment? pendingTabFormat = null;
            int pendingTabGlobalIdx = -1;

            // Ближайшая своя позиция правее точки. null — свои кончились.
            Models.Styles.TabStop? NextExplicitStop(float fromAbsPt)
            {
                if (tabStops is null || tabStops.Count == 0) return null;

                Models.Styles.TabStop? best = null;
                foreach (var stop in tabStops)
                {
                    if (stop.PositionPt <= fromAbsPt + 0.01f) continue;
                    if (best is null || stop.PositionPt < best.PositionPt) best = stop;
                }
                return best;
            }

            // Ближайшая отметка шага по умолчанию правее точки.
            float NextDefaultStopPt(float fromAbsPt)
            {
                float index = (float)Math.Floor(fromAbsPt / tabStepPt) + 1f;
                return index * tabStepPt;
            }

            void ClearPendingTab()
            {
                pendingTabSeg = null;
                pendingTabStop = null;
                pendingContentSegIdx = -1;
                pendingContentStartW = 0f;
                pendingTabFormat = null;
                pendingTabGlobalIdx = -1;
            }

            // Снимает со строки прыжок, за которым на ней так ничего и не встало, —
            // чтобы выпустить его заново на следующей строке.
            //
            // Так ведёт себя строка оглавления с длинным названием: название занимает
            // строку целиком, номеру страницы места не остаётся. Оставить прыжок здесь
            // значило бы дотянуть дорожку точек до правого поля под названием, а номер
            // бросить в начало следующей строки — оторванным от своего названия числом.
            // Прыжок уходит вниз вместе с номером, и номер снова встаёт к отметке.
            //
            // Прыжок, который на строке один, не переносится: новая строка была бы такой
            // же пустой, и перенос повторялся бы до конца абзаца.
            bool DetachPendingTabForCarry(out SKRunSegment? format, out int globalIdx)
            {
                format = null;
                globalIdx = -1;

                if (pendingTabSeg is null || pendingTabStop is null) return false;
                if (pendingTabFormat is null || pendingTabGlobalIdx < 0) return false;
                if (pendingContentSegIdx < currentLine.Segments.Count) return false;
                if (currentLine.Segments.Count < 2) return false;
                if (!ReferenceEquals(currentLine.Segments[^1], pendingTabSeg)) return false;

                format = pendingTabFormat;
                globalIdx = pendingTabGlobalIdx;

                currentW -= pendingTabSeg.Width;
                if (currentW < 0f) currentW = 0f;

                currentLine.Segments.RemoveAt(currentLine.Segments.Count - 1);
                currentLine.TextWidth = currentW;
                currentLine.LastCharIndex = globalIdx - 1;

                ClearPendingTab();
                return true;
            }

            // Ширина куска от начала до десятичного разделителя. Разделителя нет —
            // весь кусок: целое число Word прижимает к позиции правым краем.
            float DecimalPrefixWidth()
            {
                string sep = pendingTabStop?.DecimalSeparator is { Length: > 0 } custom
                    ? custom
                    : System.Globalization.CultureInfo.CurrentCulture
                        .NumberFormat.NumberDecimalSeparator;

                char sepChar = sep.Length > 0 ? sep[0] : '.';

                float width = 0f;
                for (int si = pendingContentSegIdx; si < currentLine.Segments.Count; si++)
                {
                    var seg = currentLine.Segments[si];
                    for (int k = 0; k < seg.Text.Length; k++)
                    {
                        if (seg.Text[k] == sepChar) return width;
                        width += MeasureChar(seg.Text[k].ToString(), seg);
                    }
                }

                return width;
            }

            // Закрывает отложенный прыжок тем, что успело набраться после него.
            void ResolvePendingTab()
            {
                if (pendingTabSeg is null || pendingTabStop is null)
                {
                    ClearPendingTab();
                    return;
                }

                float stopW = (float)pendingTabStop.PositionPt - lineIndentPt;
                float contentWidth = currentW - pendingContentStartW;
                if (contentWidth < 0f) contentWidth = 0f;

                float desiredStartW = pendingTabStop.Alignment switch
                {
                    Models.Styles.TabAlignment.Right => stopW - contentWidth,
                    Models.Styles.TabAlignment.Center => stopW - contentWidth * 0.5f,
                    Models.Styles.TabAlignment.Decimal => stopW - DecimalPrefixWidth(),
                    _ => pendingContentStartW
                };

                float delta = desiredStartW - pendingContentStartW;

                // Двигаем только вправо. Сдвиг влево затащил бы кусок под текст, стоящий
                // до табуляции, и строка наложилась бы сама на себя; Word в такой строке
                // тоже оставляет текст на месте — позиция просто не срабатывает.
                if (delta <= 0.01f)
                {
                    ClearPendingTab();
                    return;
                }

                for (int si = pendingContentSegIdx; si < currentLine.Segments.Count; si++)
                    currentLine.Segments[si].X += delta;

                pendingTabSeg.Width += delta;
                currentW += delta;
                currentLine.TextWidth = currentW;

                ClearPendingTab();
            }

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

            // Первая строка отдана номеру списка: текста в ней нет. Закрываем её пустой и
            // начинаем вторую — с неё пойдёт текст, причём уже без отступа первой строки
            // (ApplyBandToCurrentLine добавляет его только пока строк ещё нет). Высоту
            // пустой строке даёт формат первого токена абзаца: по сегментам её посчитать
            // не из чего, а нулевая высота посадила бы номер и текст на одну базовую линию.
            if (layout.MarkerOwnsFirstLine)
            {
                currentLine.LastCharIndex = currentLine.FirstCharIndex - 1;
                FinalizeLine(currentLine, layout, lineSpacing, tokens[0].Format);

                bandExtraTop = ComputeBand(
                    ZoneY(layout.TotalHeightPt, lineProbeHPt), lineProbeHPt,
                    minBandWidthPt, bandFragments);
                currentLine = new SKLineLayout { FirstCharIndex = tokens[0].GlobalIndex };
                ApplyBandToCurrentLine();
            }

            void StartNewLine(int firstCharIndex, float probeHPt, float requiredWidthPt)
            {
                // Слово разорвано на мягком переносе в конце строки — перенос виден дефисом.
                ShowTrailingSoftHyphen(currentLine);

                // Прыжок, за которым на этой строке так ничего и не встало, уезжает на
                // новую строку вместе со своим куском.
                bool carryTab = DetachPendingTabForCarry(out var carryFormat, out int carryIdx);

                // Прыжок, не закрытый к переносу, закрывается тем, что успело набраться:
                // строка кончилась, и ждать продолжения куска больше нечего.
                ResolvePendingTab();

                FinalizeLine(currentLine, layout, lineSpacing);
                lineProbeHPt = probeHPt;
                bandExtraTop = ComputeBand(
                    ZoneY(layout.TotalHeightPt, probeHPt), probeHPt, requiredWidthPt, bandFragments);
                currentLine = new SKLineLayout
                {
                    FirstCharIndex = carryTab ? carryIdx : firstCharIndex
                };
                ApplyBandToCurrentLine();

                if (carryTab && carryFormat is not null)
                    AppendTab(carryFormat, carryIdx);
            }

            // Сколько строка может ужаться за счёт пробелов: пятая часть их ширины. Так
            // Word 2013 и новее верстает абзацы по ширине — слово, которому не хватает
            // пары пунктов, остаётся в строке, а пробелы в ней становятся уже. Только
            // для строк без обтекания: в полосах рядом с объектом место считается
            // отрезками, и сжимать там нечего.
            float JustifyShrinkAllowance()
            {
                if (!layout.AllowsJustifyShrink || hasZones) return 0f;

                float spacesPt = 0f;
                foreach (var seg in currentLine.Segments)
                {
                    if (seg.IsTabJump || seg.IsInlineObject || seg.Text.Length == 0) continue;
                    if (seg.Text[0] == ' ') spacesPt += seg.Width;
                }

                return spacesPt * MaxJustifyShrink;
            }

            // Слово, за которым в тексте идёт пробел, Word примеряет к строке вместе с этим
            // пробелом: хвостовой пробел тоже входит в число сжимаемых, хоть и не рисуется.
            // Без него строке «…три величины, которые» не хватало пункта, и слово уходило
            // вниз, а у Word оставалось.
            void FlushWord(float followingSpacePt = 0f)
            {
                if (wordBuffer.Count == 0) return;

                float trailingShrinkPt = layout.AllowsJustifyShrink && !hasZones
                    ? followingSpacePt * MaxJustifyShrink
                    : 0f;

                // Сжатием пробелов Word втягивает в строку слово, которому не хватает
                // меньше половины его собственной ширины. Короткое слово, вылезающее за край
                // больше чем наполовину, уходит на следующую строку, даже если пробелов в
                // строке хватило бы: у Word «…будто вспыхнули | по краям» при запасе в 7,8 пт
                // и нехватке в 6,6 пт, а «положение каждой» с нехваткой 7,9 пт остаётся.
                float wordShrinkCapPt = wordWidth * MaxJustifyShrinkWordShare;

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

                // Перед словом стоял пробел, который в строку не лёг. Слово остаётся в этой
                // строке, только если помещается вместе с ним (с учётом сжатия пробелов,
                // куда входит и он сам), — тогда пробел ставится обратно. Иначе слово
                // начинает новую строку, а пробел остаётся хвостовым, как в Word.
                if (droppedSpaceFormat is not null)
                {
                    bool sameSpot = ReferenceEquals(droppedSpaceLine, currentLine)
                        && droppedSpaceFrag == fragIdx
                        && currentLine.Segments.Count > 0;

                    if (sameSpot)
                    {
                        float allowanceWithSpacePt = Math.Min(
                            JustifyShrinkAllowance()
                                + (layout.AllowsJustifyShrink && !hasZones
                                    ? droppedSpaceWidth * MaxJustifyShrink
                                    : 0f)
                                + trailingShrinkPt,
                            wordShrinkCapPt);

                        if (currentW + droppedSpaceWidth + wordWidth <= fragEndW + allowanceWithSpacePt)
                        {
                            AppendCharToLine(currentLine, " ", droppedSpaceFormat, droppedSpaceIdx,
                                ref currentW, droppedSpaceWidth, segmentBreakPending, fragIdx);
                            segmentBreakPending = false;
                        }
                        else
                        {
                            StartNewLine(wordBuffer[0].GlobalIndex, wordProbeHPt, wordRequiredPt);
                        }
                    }

                    droppedSpaceFormat = null;
                    droppedSpaceLine = null;
                }

                float shrinkAllowancePt = Math.Min(JustifyShrinkAllowance() + trailingShrinkPt, wordShrinkCapPt);

                if (currentW + wordWidth <= fragEndW + shrinkAllowancePt
                    || currentLine.Segments.Count == 0 && wordWidth <= fragEndW)
                {
                    if (currentW + wordWidth > fragEndW + shrinkAllowancePt && currentLine.Segments.Count > 0)
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

            // Прыжок табуляции: слово перед ним закрывается, ширина считается по позиции.
            void AppendTab(SKRunSegment format, int globalIdx)
            {
                ResolvePendingTab();

                // Позиции табуляции отсчитываются от левого края текста абзаца — там же,
                // где их показывает линейка. Отступ первой строки уже съеден полосой,
                // поэтому в её координатах он добавляется обратно.
                float fromAbsW = currentW + lineIndentPt;

                var explicitStop = NextExplicitStop(fromAbsW);
                float targetAbsW = explicitStop is not null
                    ? (float)explicitStop.PositionPt
                    : NextDefaultStopPt(fromAbsW);

                // Прыжок откладывается, когда текст за ним прижимается к отметке правым
                // краем, серединой или разделителем: его ширина зависит от того, что за
                // ним встанет, а этого мы ещё не знаем.
                bool deferred = explicitStop is not null
                    && explicitStop.Alignment != Models.Styles.TabAlignment.Left;

                float jump;

                if (deferred)
                {
                    // Наименьший прыжок сейчас, дорастёт в ResolvePendingTab.
                    //
                    // Гнать его сразу до отметки нельзя, хотя так и просится: у правой
                    // позиции на отметке должен КОНЧАТЬСЯ идущий следом кусок, а не
                    // начинаться. Прыгнув до отметки, мы поставили бы начало куска туда,
                    // где ему полагается кончиться, и его пришлось бы тащить влево — а
                    // влево двигать нечего, там уже стоит текст до табуляции. Именно
                    // поэтому номера страниц в оглавлении липли к названию главы, и
                    // точкам между ними не оставалось места.
                    jump = MinTabJumpPt;
                }
                else
                {
                    jump = targetAbsW - lineIndentPt - currentW;
                    if (jump < MinTabJumpPt) jump = MinTabJumpPt;

                    // Прыжок за правый край строку не переносит: табуляция упирается в
                    // край, как в Word. Перенос оторвал бы номер страницы от своей строки.
                    if (currentW + jump > fragEndW)
                        jump = Math.Max(fragEndW - currentW, MinTabJumpPt);
                }

                AppendCharToLine(currentLine, "\t", format, globalIdx,
                    ref currentW, jump, forceNewSegment: true, fragIdx);
                segmentBreakPending = false;

                var tabSeg = currentLine.Segments[^1];
                tabSeg.IsTabJump = true;
                tabSeg.TabLeader = explicitStop is null
                    ? SKTabLeader.None
                    : (SKTabLeader)(int)explicitStop.Leader;

                tabSeg.TabLeaderDensity = explicitStop is null
                    ? 0f
                    : (float)explicitStop.LeaderDensity;

                // Левая позиция закрыта сразу: её кусок начинается ровно на отметке, и
                // ждать конца текста незачем.
                if (deferred)
                {
                    pendingTabSeg = tabSeg;
                    pendingTabStop = explicitStop;
                    pendingContentSegIdx = currentLine.Segments.Count;
                    pendingContentStartW = currentW;
                    pendingTabFormat = format;
                    pendingTabGlobalIdx = globalIdx;
                }
            }

            // Строку открыл перенос строки внутри абзаца: если текста за ним нет, строка
            // всё равно есть — пустая, как у Word, со знаком абзаца на ней.
            bool lineOpenedByBreak = false;
            SKRunSegment? lineBreakFormat = null;

            foreach (var (ch, format, globalIdx) in tokens)
            {
                lineOpenedByBreak = false;

                if (ch == "\n" && !format.IsHidden)
                {
                    // Перенос строки внутри абзаца (Shift+Enter): строка кончается на нём,
                    // следующий знак начинает новую. Сам знак — нулевой ширины в конце
                    // строки: на нём стоит каретка перед переносом.
                    FlushWord();
                    droppedSpaceFormat = null;
                    droppedSpaceLine = null;

                    AppendCharToLine(currentLine, ch, format, globalIdx,
                        ref currentW, 0f, true, fragIdx);
                    currentLine.Segments[^1].IsLineBreak = true;
                    segmentBreakPending = false;

                    StartNewLine(globalIdx + 1, lineProbeHPt, minBandWidthPt);
                    lineOpenedByBreak = true;
                    lineBreakFormat = format;
                }
                else if (ch == SoftHyphen && !format.IsHidden)
                {
                    // Мягкий перенос: слово до него — отдельный кусок, и строка может
                    // кончиться здесь. Сам знак своим сегментом: он станет дефисом, только
                    // если строка на нём и оборвётся.
                    FlushWord();
                    AppendCharToLine(currentLine, ch, format, globalIdx,
                        ref currentW, 0f, true, fragIdx);
                    currentLine.Segments[^1].IsSoftHyphen = true;
                    segmentBreakPending = false;
                }
                else if (ch == "\t" && format.IsHidden)
                {
                    // Скрытая табуляция не прыгает к позиции: у скрытого текста нет места
                    // в строке. Знак остаётся — на нём стоит позиция каретки.
                    FlushWord();
                    AppendCharToLine(currentLine, ch, format, globalIdx,
                        ref currentW, 0f, true, fragIdx);
                    segmentBreakPending = false;
                }
                else if (ch == "\t")
                {
                    FlushWord();

                    // Табуляция сама отделяет следующий текст — не легший пробел перед
                    // ней уже никому не нужен.
                    droppedSpaceFormat = null;
                    droppedSpaceLine = null;

                    AppendTab(format, globalIdx);
                }
                else if (ch == " ")
                {
                    float spaceWidth = MeasureChar(ch, format);
                    FlushWord(spaceWidth);

                    // Пробел на границе отрезка не переносит текст за объект: он просто
                    // не рисуется, как хвостовой пробел в конце строки. Слово за ним
                    // помнит о нём (droppedSpace*) и в ту же строку без него не встанет.
                    if (currentW + spaceWidth <= fragEndW || currentLine.Segments.Count == 0)
                    {
                        AppendCharToLine(currentLine, ch, format, globalIdx,
                            ref currentW, spaceWidth, segmentBreakPending, fragIdx);
                        segmentBreakPending = false;
                    }
                    else if (droppedSpaceFormat is null)
                    {
                        droppedSpaceLine = currentLine;
                        droppedSpaceFrag = fragIdx;
                        droppedSpaceWidth = spaceWidth;
                        droppedSpaceFormat = format;
                        droppedSpaceIdx = globalIdx;
                    }
                }
                else
                {
                    float charWidth = MeasureChar(ch, format);
                    wordBuffer.Add((ch, format, globalIdx));
                    wordWidth += charWidth;

                    // Перенос допускается сразу после дефиса внутри слова: «чьего-то»
                    // Word разрывает на «чьего-» и «то». Без этого слово с дефисом
                    // уезжает на следующую строку целиком, и абзац занимает лишнюю
                    // строку — на листе это накапливается и уводит границы страниц.
                    // Дефис в начале куска (тире прямой речи, минус перед числом)
                    // точкой переноса не считается: отрывать его не от чего.
                    if (breakOnHyphen && ch == "-" && wordBuffer.Count > 1)
                        FlushWord();
                }
            }

            FlushWord();
            ResolvePendingTab();

            if (currentLine.Segments.Count > 0 || layout.Lines.Count == 0)
            {
                currentLine.IsLastLine = true;
                FinalizeLine(currentLine, layout, lineSpacing);
            }
            else if (lineOpenedByBreak)
            {
                // Абзац кончается переносом строки: за ним пустая строка со знаком
                // абзаца, как у Word. Высоту ей даёт формат переноса.
                currentLine.IsLastLine = true;
                currentLine.LastCharIndex = currentLine.FirstCharIndex - 1;
                FinalizeLine(currentLine, layout, lineSpacing, lineBreakFormat);
            }

            if (layout.Lines.Count > 0)
                layout.Lines[^1].IsLastLine = true;

            // Состояние для гистерезиса следующей пересборки.
            layout.WrapPushedDown = hasZones
                && layout.Lines.Count > 0
                && layout.Lines[0].WrapExtraTopPt > 0.01f;
        }

        /// <summary>
        /// Последняя строка растянутого абзаца (w:jc="distribute"): свободное место до
        /// правого края делится поровну между всеми промежутками между знаками строки —
        /// и внутри слов, и на пробелах. Так Word растягивает последнюю строку: буквы
        /// разводятся, последний знак встаёт к правому краю.
        ///
        /// Разводка записывается в раскладку — места знаков (GlyphMetrics), края и
        /// ширины сегментов: каретка, выделение и отрисовка берут их оттуда, и всё
        /// совпадает с тем, что нарисовано.
        ///
        /// Строка с табуляцией или объектом в строке, строка, разорванная обтекаемым
        /// объектом, и строка, уже упёршаяся в край, не разводятся.
        /// </summary>
        private static void DistributeLastLine(SKTextLayout layout, int lineIndex)
        {
            var line = layout.Lines[lineIndex];
            if (line.Segments.Count == 0 || line.HasWrapFragments || line.IsBidiReordered) return;

            foreach (var seg in line.Segments)
                if (seg.IsTabJump || seg.IsInlineObject) return;

            // Последний видимый знак строки: хвостовые пробелы и знаки нулевой ширины
            // (скрытый текст, перенос строки) не разводятся — дальше него места нет.
            int lastSeg = -1, lastGlyph = -1;
            for (int si = line.Segments.Count - 1; si >= 0 && lastSeg < 0; si--)
            {
                var seg = line.Segments[si];
                if (seg.GlyphMetrics.Length != seg.Text.Length) return;

                for (int gi = seg.GlyphMetrics.Length - 1; gi >= 0; gi--)
                {
                    if (seg.GlyphMetrics[gi].Width <= 0f || char.IsWhiteSpace(seg.Text[gi])) continue;
                    lastSeg = si;
                    lastGlyph = gi;
                    break;
                }
            }
            if (lastSeg < 0) return;

            // Промежутки — после каждого знака ненулевой ширины до последнего видимого.
            int gaps = 0;
            for (int si = 0; si <= lastSeg; si++)
            {
                var seg = line.Segments[si];
                int until = si == lastSeg ? lastGlyph : seg.GlyphMetrics.Length;
                for (int gi = 0; gi < until; gi++)
                    if (seg.GlyphMetrics[gi].Width > 0f) gaps++;
            }
            if (gaps == 0) return;

            var endSeg = line.Segments[lastSeg];
            float contentRight = endSeg.X + endSeg.GlyphMetrics[lastGlyph].Right;

            float area = line.WrapAreaWidthPt > 0f ? line.WrapAreaWidthPt : layout.TextAreaWidthPt;
            float firstExtra = lineIndex == 0 ? layout.FirstLineIndentPt : 0f;
            float free = area - firstExtra - contentRight;
            if (free <= 0.01f) return;

            float extra = free / gaps;
            float shift = 0f;

            for (int si = 0; si < line.Segments.Count; si++)
            {
                var seg = line.Segments[si];
                seg.X += shift;

                var glyphs = seg.GlyphMetrics;
                float local = 0f;

                for (int gi = 0; gi < glyphs.Length; gi++)
                {
                    bool spreads = glyphs[gi].Width > 0f
                        && (si < lastSeg || (si == lastSeg && gi < lastGlyph));

                    float widthAdd = spreads ? extra : 0f;
                    glyphs[gi] = new SKGlyphMetrics
                    {
                        CharIndex = glyphs[gi].CharIndex,
                        X = glyphs[gi].X + local,
                        Width = glyphs[gi].Width + widthAdd
                    };
                    local += widthAdd;
                }

                if (local > 0f)
                {
                    seg.Width += local;
                    seg.UsesGlyphPositions = true;
                }

                shift += local;
            }

            line.TextWidth += shift;
        }

        /// <summary>Мягкий перенос (U+00AD, w:softHyphen).</summary>
        private const string SoftHyphen = "\u00AD";

        /// <summary>Неразрывный дефис (U+2011, w:noBreakHyphen).</summary>
        private const string NonBreakingHyphen = "\u2011";

        /// <summary>
        /// Строка кончается мягким переносом — слово разорвано на нём, и перенос
        /// становится видимым дефисом своей ширины, как у Word.
        /// </summary>
        private static void ShowTrailingSoftHyphen(SKLineLayout line)
        {
            if (line.Segments.Count == 0) return;

            var seg = line.Segments[^1];
            if (!seg.IsSoftHyphen || seg.SoftHyphenShown) return;

            float width = MeasureChar("-", seg);
            seg.SoftHyphenShown = true;
            seg.Text = "-";
            seg.Width = width;
            line.TextWidth += width;
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

            // Неразрывный дефис рисуется обычным дефисом шрифта: знака U+2011 во многих
            // гарнитурах нет, и подстановка другим шрифтом дала бы чужой дефис. Строку по
            // нему не рвут — это решено раньше, по самому знаку (токен «\u2011»).
            string glyphText = ch == NonBreakingHyphen ? "-" : ch;

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

            // Прыжок табуляции тоже стоит особняком: его ширину правит выравнивание уже
            // после того, как строка набрана, и слитый с соседями он потерялся бы среди
            // пробелов — сдвигать было бы нечего.
            bool tabInvolved = ch == "\t" || (lastSeg is not null && lastSeg.IsTabJump);

            // Буквы, пишущиеся справа налево, не сливаются в один сегмент с остальными
            // знаками: такой сегмент встанет в строке по направлению письма и будет
            // нарисован справа налево целиком. Для текста без иврита и арабского ничего
            // не меняется.
            bool directionBreak = lastSeg is not null && DirectionGroupChanges(lastSeg, ch);

            if (!objectInvolved && !tabInvolved && lastSeg is not null
                && IsSameFormat(lastSeg, format) && curSpace == lastSpace && !directionBreak)
            {
                lastSeg.Text += glyphText;
                lastSeg.Width += charWidth;
            }
            else
            {
                var seg = new SKRunSegment
                {
                    Text = glyphText,
                    FontFamily = format.FontFamily,
                    FontSizePt = format.FontSizePt,
                    BaselineShiftPt = format.BaselineShiftPt,
                    CharacterSpacingPt = format.CharacterSpacingPt,
                    HorizontalScale = format.HorizontalScale,
                    IsBold = format.IsBold,
                    IsItalic = format.IsItalic,
                    IsUnderline = format.IsUnderline,
                    UnderlineStyle = format.UnderlineStyle,
                    UnderlineColor = format.UnderlineColor,
                    IsStrikethrough = format.IsStrikethrough,
                    IsDoubleStrikethrough = format.IsDoubleStrikethrough,
                    IsHidden = format.IsHidden,
                    IsHiddenMarked = format.IsHiddenMarked,
                    IsOutline = format.IsOutline,
                    IsShadow = format.IsShadow,
                    IsEmboss = format.IsEmboss,
                    IsImprint = format.IsImprint,
                    EmphasisMark = format.EmphasisMark,
                    CharBorderColor = format.CharBorderColor,
                    CharBorderWidthPt = format.CharBorderWidthPt,
                    CharBorderStyle = format.CharBorderStyle,
                    Effects = format.Effects,
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

        /// <param name="emptyLineMetrics">
        /// Формат, по которому берутся метрики, когда сегментов в строке нет. Нужен строке
        /// под номером списка: без него высота вышла бы нулевой и следующая строка встала
        /// бы на ту же базовую линию, что и номер.
        /// </param>
        private static void FinalizeLine(
            SKLineLayout line,
            SKTextLayout layout,
            SKLineSpacing lineSpacing,
            SKRunSegment? emptyLineMetrics = null)
        {
            float maxAscent = 0f;
            float maxDescent = 0f;

            // Верх строки над базовой линией вместе с межстрочным зазором гарнитуры.
            // Word кладёт зазор над подъёмом каждого шрифта и берёт наибольшую из этих
            // сумм: зазор одной гарнитуры не прибавляется к подъёму другой. Строка
            // Times и Comic Sans выходила выше вордовской на зазор Times, и страница,
            // которую Word заполняет впритык, теряла последнюю строку.
            float maxTop = 0f;

            // Метрики одного текста, без учёта габарита картинок: по ним рисуется каретка.
            // Иначе рядом с крупной картинкой каретка растягивалась бы на всю её высоту,
            // хотя печатается текст своего кегля.
            float maxTextAscent = 0f;
            float maxTextDescent = 0f;

            // Кегельная площадка самого крупного знака строки: подъём и спуск, сведённые
            // к кеглю в пропорции гарнитуры. По ней Word выравнивает знаки разного кегля
            // (w:textAlignment), а не по полным метрикам шрифта.
            float maxEmAscent = 0f;
            float maxEmDescent = 0f;

            // Скрытый текст высоту строки не задаёт — кроме строки, где кроме него
            // ничего нет: у неё остаётся высота его шрифта, а не нулевая.
            bool anyVisible = false;
            foreach (var candidate in line.Segments)
            {
                if (!candidate.IsHidden) { anyVisible = true; break; }
            }

            foreach (var seg in line.Segments)
            {
                var typeface = GetOrCreateTypeface(seg.FontFamily, seg.IsBold, seg.IsItalic);
                var font = GetOrCreateFont(typeface, seg.FontSizePt);

                if (seg.IsHidden && anyVisible)
                {
                    seg.GlyphMetrics = BuildGlyphMetrics(seg, font);
                    continue;
                }

                font.GetFontMetrics(out var metrics);

                float ascent = Math.Abs(metrics.Ascent);
                float descent = Math.Abs(metrics.Descent);

                // Межстрочный зазор гарнитуры входит в одинарный интервал Word — над
                // подъёмом именно этой гарнитуры.
                float top = ascent + Math.Abs(metrics.Leading);

                // Шрифтовые метрики берём и у сегмента-картинки: он несёт кегль стиля,
                // поэтому строка из одной картинки всё равно знает высоту своего текста.
                if (ascent > maxTextAscent) maxTextAscent = ascent;
                if (descent > maxTextDescent) maxTextDescent = descent;

                var (emAscent, emDescent) = EmBoxExtents(seg.FontSizePt, ascent, descent);
                if (emAscent > maxEmAscent) maxEmAscent = emAscent;
                if (emDescent > maxEmDescent) maxEmDescent = emDescent;

                // Картинка в строке стоит на базовой линии и поднимает высоту строки
                // под себя — как крупный глиф. Иначе строка осталась бы высотой в
                // шрифт, а картинка налезла бы на соседние строки.
                if (seg.IsInlineObject && seg.ObjectHeightPt > ascent)
                    ascent = seg.ObjectHeightPt;

                if (ascent > top) top = ascent;

                // Знаки ударения стоят над подъёмом шрифта (точка снизу — под спуском) и
                // раздвигают строку, как у Word: иначе они налезали бы на соседнюю строку.
                if (seg.EmphasisMark != 0 && !seg.IsInlineObject)
                {
                    if (seg.EmphasisMark == (int)Models.Inline.EmphasisMark.UnderDot)
                        descent += seg.FontSizePt * EmphasisUnderReachEm;
                    else
                        top = Math.Max(top, ascent + seg.FontSizePt * EmphasisOverReachEm);
                }

                // Текст, поднятый или опущенный от базовой линии (индексы, w:position),
                // раздвигает строку, как у Word: поднятый — вверх, опущенный — вниз.
                // Иначе он налезал бы на соседние строки.
                if (!seg.IsInlineObject && seg.BaselineShiftPt != 0f)
                {
                    top += seg.BaselineShiftPt;
                    descent -= seg.BaselineShiftPt;
                }

                if (ascent > maxAscent) maxAscent = ascent;
                if (descent > maxDescent) maxDescent = descent;
                if (top > maxTop) maxTop = top;

                seg.GlyphMetrics = BuildGlyphMetrics(seg, font);
            }

            // Знаки разного кегля по высоте строки (w:textAlignment): мелкие встают не на
            // общую базовую линию, а верхом к верху строки, серединой к её середине или
            // низом к низу — по самому крупному знаку. Строка при этом не растёт: знак
            // остаётся внутри её подъёма и спуска, двигается только его базовая линия.
            // Верх, середина и низ берутся по кегельной площадке, как у Word: по полным
            // метрикам Times New Roman мелкий знак при «по верху» вставал на два пункта
            // выше вордовского, при «по середине» — на пункт.
            if (layout.LineTextAlignment is LineTextAlignTop or LineTextAlignCenter or LineTextAlignBottom
                && line.Segments.Count > 0)
            {
                foreach (var seg in line.Segments)
                {
                    if (seg.IsInlineObject || seg.IsTabJump || (seg.IsHidden && anyVisible)) continue;

                    var segFont = GetOrCreateFont(
                        GetOrCreateTypeface(seg.FontFamily, seg.IsBold, seg.IsItalic), seg.FontSizePt);
                    segFont.GetFontMetrics(out var segMetrics);

                    float segAscent = Math.Abs(segMetrics.Ascent);
                    float segDescent = Math.Abs(segMetrics.Descent);
                    var (segEmAscent, segEmDescent) = EmBoxExtents(seg.FontSizePt, segAscent, segDescent);

                    // Сдвиг вверх — положительный, как у поднятого текста.
                    float lift = layout.LineTextAlignment switch
                    {
                        LineTextAlignTop => maxEmAscent - segEmAscent,
                        LineTextAlignBottom => -(maxEmDescent - segEmDescent),
                        _ => -((maxEmDescent - maxEmAscent) + (segEmAscent - segEmDescent)) / 2f
                    };

                    if (Math.Abs(lift) > 0.01f) seg.BaselineShiftPt += lift;
                }
            }

            if (line.Segments.Count == 0 && emptyLineMetrics is not null)
            {
                var emptyTypeface = GetOrCreateTypeface(
                    emptyLineMetrics.FontFamily, emptyLineMetrics.IsBold, emptyLineMetrics.IsItalic);
                var emptyFont = GetOrCreateFont(emptyTypeface, emptyLineMetrics.FontSizePt);
                emptyFont.GetFontMetrics(out var emptyMetrics);

                maxAscent = maxTextAscent = Math.Abs(emptyMetrics.Ascent);
                maxDescent = maxTextDescent = Math.Abs(emptyMetrics.Descent);
                maxTop = maxAscent + Math.Abs(emptyMetrics.Leading);
            }

            // Номер списка стоит в первой строке абзаца и раздвигает её, когда его шрифт
            // выше строки: знак из Symbol или из шрифта подстановки («★») выше Times New
            // Roman того же кегля, и Word отдаёт такой строке высоту шрифта номера. Шрифт
            // ниже строки её не меняет, хотя спуск у него бывает глубже (Courier New):
            // сравнивается высота шрифта целиком, а не подъём и спуск порознь.
            if (layout.Lines.Count == 0
                && layout.MarkerLineTopPt + layout.MarkerLineDescentPt > maxTop + maxDescent + 0.01f)
            {
                if (layout.MarkerLineTopPt > maxTop) maxTop = layout.MarkerLineTopPt;
                if (layout.MarkerLineDescentPt > maxDescent) maxDescent = layout.MarkerLineDescentPt;
            }

            // Зазор уже сидит в верхе строки (maxTop), поэтому отдельно не передаётся.
            // Базовая линия — под зазором, как у Word: прибавка от множителя интервала
            // делится поровну сверху и снизу, как и прежде.
            float lineHeightBase = maxTop + maxDescent;
            float lineHeight = lineSpacing.Resolve(maxTop, maxDescent, 0f);
            float baseline = (lineHeight - lineHeightBase) / 2f + maxTop;

            // Вытеснение строки под обтекаемый объект: зазор входит в высоту параграфа.
            layout.TotalHeightPt += line.WrapExtraTopPt;

            line.Y = layout.TotalHeightPt;
            line.Height = lineHeight;
            line.Baseline = baseline;
            line.TextAscentPt = maxTextAscent;
            line.TextDescentPt = maxTextDescent;

            layout.TotalHeightPt += lineHeight;

            // Направление письма: строка абзаца справа налево начинается у правого края,
            // а куски иврита и арабского в любой строке встают в порядке письма.
            line.IsRightToLeft = layout.IsRightToLeft;
            if (layout.HasBidiText) ApplyBidiOrder(line, layout);

            layout.Lines.Add(line);
        }

        // ── Направление письма ───────────────────────────────────────────

        /// <summary>
        /// Шрифт HarfBuzz для набора текста справа налево: данные гарнитуры, её face и
        /// шрифт в собственных единицах гарнитуры. Держатся вместе, пока жива гарнитура.
        /// </summary>
        private sealed class HarfBuzzFontEntry
        {
            public HarfBuzzFontEntry(HarfBuzzSharp.Blob blob, HarfBuzzSharp.Face face, HarfBuzzSharp.Font font, int unitsPerEm)
            {
                Blob = blob;
                Face = face;
                Font = font;
                UnitsPerEm = unitsPerEm;
            }

            public HarfBuzzSharp.Blob Blob { get; }
            public HarfBuzzSharp.Face Face { get; }
            public HarfBuzzSharp.Font Font { get; }
            public int UnitsPerEm { get; }
        }

        private static readonly Dictionary<SKTypeface, HarfBuzzFontEntry?> _harfBuzzFonts = new();
        private static readonly object _harfBuzzLock = new();

        /// <summary>
        /// Шрифт HarfBuzz для гарнитуры — из её файла. null — файл гарнитуры недоступен
        /// (системная подмена без данных): такой сегмент рисуется без набора.
        /// </summary>
        private static HarfBuzzFontEntry? GetHarfBuzzFont(SKTypeface typeface)
        {
            if (_harfBuzzFonts.TryGetValue(typeface, out var cached)) return cached;

            HarfBuzzFontEntry? entry = null;
            try
            {
                using var stream = typeface.OpenStream(out int ttcIndex);
                if (stream is not null)
                {
                    var data = SKData.Create(stream);
                    if (data is not null && data.Size > 0)
                    {
                        var blob = new HarfBuzzSharp.Blob(
                            data.Data, (int)data.Size, HarfBuzzSharp.MemoryMode.ReadOnly, () => data.Dispose());
                        var face = new HarfBuzzSharp.Face(blob, ttcIndex);
                        var font = new HarfBuzzSharp.Font(face);
                        int unitsPerEm = face.UnitsPerEm > 0 ? face.UnitsPerEm : 2048;
                        font.SetScale(unitsPerEm, unitsPerEm);
                        font.SetFunctionsOpenType();
                        entry = new HarfBuzzFontEntry(blob, face, font, unitsPerEm);
                    }
                    else
                    {
                        data?.Dispose();
                    }
                }
            }
            catch (Exception)
            {
                entry = null;
            }

            _harfBuzzFonts[typeface] = entry;
            return entry;
        }

        /// <summary>
        /// Набирает текст HarfBuzz справа налево шрифтом гарнитуры: знаки в обратном
        /// порядке, арабские буквы — в начертаниях по соседям, связки и огласовки на
        /// своих местах. Глифы идут слева направо, у каждого — индекс первого знака его
        /// кластера в тексте (Cluster). false — набрать нечем: у гарнитуры нет файла или
        /// в ней нет какого-то знака.
        /// </summary>
        private static bool TryShapeRightToLeft(
            SKTypeface typeface,
            string text,
            out HarfBuzzSharp.GlyphInfo[] infos,
            out HarfBuzzSharp.GlyphPosition[] positions,
            out int unitsPerEm)
        {
            infos = Array.Empty<HarfBuzzSharp.GlyphInfo>();
            positions = Array.Empty<HarfBuzzSharp.GlyphPosition>();
            unitsPerEm = 0;

            if (string.IsNullOrEmpty(text)) return false;

            lock (_harfBuzzLock)
            {
                var entry = GetHarfBuzzFont(typeface);
                if (entry is null) return false;

                using var buffer = new HarfBuzzSharp.Buffer();
                buffer.AddUtf16(text);
                buffer.Direction = HarfBuzzSharp.Direction.RightToLeft;
                buffer.GuessSegmentProperties();
                entry.Font.Shape(buffer);

                infos = buffer.GlyphInfos;
                positions = buffer.GlyphPositions;
                unitsPerEm = entry.UnitsPerEm;
            }

            if (infos.Length == 0 || positions.Length != infos.Length || unitsPerEm <= 0) return false;

            foreach (var info in infos)
            {
                if (info.Codepoint == 0) return false;
                if (info.Cluster >= (uint)text.Length) return false;
            }

            return true;
        }

        /// <summary>
        /// Ширины знаков сегмента справа налево по набору HarfBuzz, в порядке текста:
        /// ширина кластера целиком достаётся его первому знаку, остальные знаки кластера
        /// (огласовки, вторая буква связки) — нулевой ширины. null — набрать нечем,
        /// остаются ширины, измеренные по одному знаку.
        /// </summary>
        private static float[]? ShapeRightToLeftAdvances(SKRunSegment seg)
        {
            var typeface = GetOrCreateTypeface(seg.FontFamily, seg.IsBold, seg.IsItalic);
            var font = GetOrCreateFont(typeface, seg.FontSizePt);

            if (!TryShapeRightToLeft(font.Typeface, seg.Text, out var infos, out var positions, out int unitsPerEm))
                return null;

            float unitScale = font.Size / unitsPerEm;
            var advances = new float[seg.Text.Length];
            for (int i = 0; i < infos.Length; i++)
                advances[(int)infos[i].Cluster] += positions[i].XAdvance * unitScale;

            return advances;
        }

        /// <summary>
        /// Меряет кусок справа налево набором HarfBuzz вместо суммы ширин отдельных
        /// знаков. Арабские буквы в слове стоят в связных начертаниях, и связное слово
        /// уже, чем те же буквы по одной: по прежней мерке отрисовка растягивала слово
        /// до ширины отдельных знаков, и между связанными буквами появлялись просветы.
        /// Масштаб по ширине и разрядка прикладываются к знаку так же, как при обычном
        /// измерении. Места знаков — в порядке текста, от начала куска.
        /// </summary>
        private static void ApplyShapedAdvances(SKRunSegment seg)
        {
            if (seg.IsInlineObject || seg.IsHidden || seg.IsTabJump || seg.Text.Length == 0) return;
            if (seg.GlyphMetrics.Length != seg.Text.Length) return;
            if (!BidiResolver.HasRightToLeft(seg.Text)) return;

            var advances = ShapeRightToLeftAdvances(seg);
            if (advances is null) return;

            var metrics = seg.GlyphMetrics;
            var rebuilt = new SKGlyphMetrics[metrics.Length];
            float x = 0f;
            for (int k = 0; k < metrics.Length; k++)
            {
                float width = advances[k] * seg.HorizontalScale + seg.CharacterSpacingPt;
                rebuilt[k] = new SKGlyphMetrics
                {
                    CharIndex = metrics[k].CharIndex,
                    X = x,
                    Width = width
                };
                x += width;
            }

            seg.GlyphMetrics = rebuilt;
            seg.Width = x;
        }

        /// <summary>
        /// Рисует сегмент справа налево. Текст набирается HarfBuzz (TryShapeRightToLeft),
        /// и каждый кластер встаёт на место своего первого знака из вёрстки: правым краем
        /// к правому краю места знака. Так буквы совпадают с кареткой и выделением, а
        /// пробел, растянутый выравниванием по ширине, раздвигает слова, не растягивая их.
        ///
        /// Мест знаков нет (их число не совпадает с текстом) — набранная строка
        /// укладывается в ширину сегмента целиком. Набрать нечем — знаки рисуются по
        /// одному на отражённых местах: порядок верный, связных начертаний нет.
        /// </summary>
        private static void DrawRightToLeftText(
            SKCanvas canvas, SKRunSegment seg, float x, float baseY, SKFont font, SKPaint paint)
        {
            if (TryShapeRightToLeft(font.Typeface, seg.Text, out var infos, out var positions, out int unitsPerEm))
            {
                int count = infos.Length;
                float unitScale = font.Size / unitsPerEm;

                // Сегмент с масштабом по ширине рисуется растянутым шрифтом (ScaleX), а
                // набор HarfBuzz идёт в единицах нерастянутой гарнитуры.
                float scaleX = font.ScaleX > 0f ? font.ScaleX : 1f;

                var glyphs = new ushort[count];
                var points = new SKPoint[count];
                var metrics = seg.GlyphMetrics;

                if (metrics.Length == seg.Text.Length)
                {
                    var clusterAdvance = new float[seg.Text.Length];
                    for (int i = 0; i < count; i++)
                        clusterAdvance[(int)infos[i].Cluster] += positions[i].XAdvance * unitScale * scaleX;

                    int currentCluster = -1;
                    float clusterLeft = 0f;
                    float clusterPen = 0f;
                    for (int i = 0; i < count; i++)
                    {
                        int cluster = (int)infos[i].Cluster;
                        if (cluster != currentCluster)
                        {
                            currentCluster = cluster;
                            clusterPen = 0f;

                            // Место первого знака кластера отражено: его правый край —
                            // начало кластера по ходу письма. Разрядка, стоящая за знаком,
                            // остаётся слева от него.
                            float boxRight = metrics[cluster].X + metrics[cluster].Width;
                            clusterLeft = boxRight - clusterAdvance[cluster];
                        }

                        glyphs[i] = (ushort)infos[i].Codepoint;
                        points[i] = new SKPoint(
                            x + clusterLeft + clusterPen + positions[i].XOffset * unitScale * scaleX,
                            baseY - positions[i].YOffset * unitScale);
                        clusterPen += positions[i].XAdvance * unitScale * scaleX;
                    }
                }
                else
                {
                    float total = 0f;
                    for (int i = 0; i < count; i++) total += positions[i].XAdvance * unitScale;

                    // Набранная ширина прижимается к ширине сегмента из вёрстки.
                    float fit = total > 0.01f && seg.Width > 0.01f ? seg.Width / total : 1f;

                    float pen = 0f;
                    for (int i = 0; i < count; i++)
                    {
                        glyphs[i] = (ushort)infos[i].Codepoint;
                        points[i] = new SKPoint(
                            x + (pen + positions[i].XOffset * unitScale) * fit,
                            baseY - positions[i].YOffset * unitScale);
                        pen += positions[i].XAdvance * unitScale;
                    }
                }

                using var builder = new SKTextBlobBuilder();
                var run = builder.AllocatePositionedRun(font, count);
                run.SetGlyphs(glyphs);
                run.SetPositions(points);

                using var blob = builder.Build();
                if (blob is not null) canvas.DrawText(blob, 0f, 0f, paint);
                return;
            }

            // Без набора: каждый знак на своём отражённом месте.
            if (seg.GlyphMetrics.Length == seg.Text.Length)
            {
                for (int i = 0; i < seg.Text.Length; i++)
                    canvas.DrawText(seg.Text[i].ToString(), x + seg.GlyphMetrics[i].X, baseY, font, paint);
                return;
            }

            var reversed = seg.Text.ToCharArray();
            Array.Reverse(reversed);
            canvas.DrawText(new string(reversed), x, baseY, font, paint);
        }

        /// <summary>
        /// Меняется ли группа направления между концом сегмента и новым знаком: буквы
        /// справа налево (иврит, арабский) — одна группа, всё остальное — другая. Группа
        /// сегмента — по его первому знаку: сегмент справа налево начинается с такой
        /// буквы, а метки (огласовки арабского) внутри него группу не рвут, иначе слово
        /// разошлось бы на куски и потеряло связное начертание.
        /// </summary>
        private static bool DirectionGroupChanges(SKRunSegment lastSeg, string ch)
        {
            if (ch.Length == 0 || lastSeg.Text.Length == 0) return false;

            char c = ch[0];
            char first = lastSeg.Text[0];

            // Быстрый отсев: латиница, кириллица и знаки до блока иврита направления не
            // меняют — обычный текст сюда и не доходит.
            if (c < '\u0590' && first < '\u0590') return false;

            if (BidiResolver.Classify(c) == BidiResolver.BidiClass.NSM) return false;

            return BidiResolver.IsRightToLeftStrong(c) != BidiResolver.IsRightToLeftStrong(first);
        }

        /// <summary>
        /// Раскладывает куски строки по направлению письма (UAX #9, см. BidiResolver):
        /// знакам строки — уровни, сегменту, в котором уровни разные, — разрез по ним,
        /// кускам — места на листе по правилу L2. Сегменты справа налево получают
        /// отражённые места знаков и рисуются справа налево.
        ///
        /// Порядок сегментов в строке остаётся порядком текста: по нему каретка и
        /// выделение находят знак. Меняются только X — места кусков на листе.
        ///
        /// Строки с табуляцией и строки, разорванные обтекаемым объектом, не
        /// переставляются: их места считаются от позиций табуляции и отрезков полосы.
        /// </summary>
        private static void ApplyBidiOrder(SKLineLayout line, SKTextLayout layout)
        {
            var segs = line.Segments;
            if (segs.Count == 0 || line.HasWrapFragments) return;

            foreach (var seg in segs)
                if (seg.IsTabJump) return;

            var textBuilder = new System.Text.StringBuilder();
            foreach (var seg in segs) textBuilder.Append(seg.Text);
            string text = textBuilder.ToString();

            if (!layout.IsRightToLeft && !BidiResolver.HasRightToLeft(text)) return;

            byte baseLevel = (byte)(layout.IsRightToLeft ? 1 : 0);
            byte[] levels = BidiResolver.ResolveLevels(text, baseLevel);

            // Сегменты с разными уровнями внутри режутся по ним: запятая после русского
            // слова в строке справа налево встаёт по другую сторону от слова, чем оно само.
            var pieces = new List<SKRunSegment>(segs.Count);
            var pieceLevels = new List<byte>(segs.Count);

            int offset = 0;
            foreach (var seg in segs)
            {
                int length = seg.Text.Length;
                bool canSplit = length > 1
                    && !seg.IsInlineObject
                    && seg.GlyphMetrics.Length == length;

                if (length == 0 || !canSplit)
                {
                    pieces.Add(seg);
                    pieceLevels.Add(length == 0 ? baseLevel : levels[offset]);
                    offset += length;
                    continue;
                }

                int runStart = 0;
                for (int k = 1; k <= length; k++)
                {
                    if (k < length && levels[offset + k] == levels[offset + runStart]) continue;

                    if (runStart == 0 && k == length)
                        pieces.Add(seg);
                    else
                        pieces.Add(CloneSegmentSlice(seg, runStart, k - runStart));

                    pieceLevels.Add(levels[offset + runStart]);
                    runStart = k;
                }

                offset += length;
            }

            var levelArray = pieceLevels.ToArray();
            bool anyOdd = false;
            foreach (byte level in levelArray)
                if ((level & 1) == 1) { anyOdd = true; break; }

            if (!anyOdd) return;

            // Куски справа налево меряются набором HarfBuzz — до того, как встать на лист.
            for (int i = 0; i < pieces.Count; i++)
            {
                if ((levelArray[i] & 1) == 1) ApplyShapedAdvances(pieces[i]);
            }

            float originX = float.MaxValue;
            foreach (var seg in pieces)
                if (seg.X < originX) originX = seg.X;
            if (originX == float.MaxValue) originX = 0f;

            int[] order = BidiResolver.VisualOrder(levelArray);

            float x = originX;
            foreach (int index in order)
            {
                var seg = pieces[index];
                seg.X = x;
                x += seg.Width;
            }

            // Набор мог изменить ширины кусков — правый край строки берётся по ним.
            line.TextWidth = x;

            for (int i = 0; i < pieces.Count; i++)
            {
                if ((levelArray[i] & 1) == 0) continue;

                var seg = pieces[i];
                seg.IsRightToLeft = true;

                var metrics = seg.GlyphMetrics;
                if (metrics.Length == 0) continue;

                var mirrored = new SKGlyphMetrics[metrics.Length];
                for (int g = 0; g < metrics.Length; g++)
                {
                    mirrored[g] = new SKGlyphMetrics
                    {
                        CharIndex = metrics[g].CharIndex,
                        X = seg.Width - metrics[g].X - metrics[g].Width,
                        Width = metrics[g].Width
                    };
                }
                seg.GlyphMetrics = mirrored;
            }

            segs.Clear();
            segs.AddRange(pieces);
            line.IsBidiReordered = true;
        }

        /// <summary>
        /// Выравнивание по ширине строк, куски которых переставлены по направлению письма.
        /// Добавка на пробел считается тем же расчётом, что у обычной строки
        /// (JustifyExtraPerSpace), и вписывается в раскладку: пробелы между словами
        /// получают её в свою ширину, куски заново встают на лист в прежнем порядке.
        /// Каретка, выделение и отрисовка берут места из раскладки и совпадают.
        ///
        /// Хвостовые пробелы строки не растягиваются — как и у обычной строки.
        /// </summary>
        private static void JustifyReorderedLines(SKTextLayout layout)
        {
            for (int i = 0; i < layout.Lines.Count; i++)
            {
                var line = layout.Lines[i];
                if (!line.IsBidiReordered || line.HasWrapFragments) continue;

                line.IsBidiReordered = false;
                float extra = JustifyExtraPerSpace(layout, i, 0);
                line.IsBidiReordered = true;
                if (extra == 0f) continue;

                var segs = line.Segments;

                // Последний по тексту сегмент со словом: пробелы за ним — хвостовые.
                int lastWordSeg = -1;
                for (int si = segs.Count - 1; si >= 0 && lastWordSeg < 0; si--)
                {
                    if (segs[si].IsHidden) continue;
                    foreach (var c in segs[si].Text)
                    {
                        if (c != ' ' && c != '\t') { lastWordSeg = si; break; }
                    }
                }
                if (lastWordSeg < 0) continue;

                bool changed = false;
                for (int si = 0; si <= lastWordSeg; si++)
                {
                    if (segs[si].IsHidden) continue;
                    if (WidenSpaces(segs[si], extra)) changed = true;
                }

                if (changed) PlaceInVisualOrder(line);
            }
        }

        /// <summary>
        /// Прибавляет к каждому пробелу сегмента добавку растяжки. Места знаков
        /// пересчитываются по ходу письма: у сегмента справа налево — от правого края.
        /// true — в сегменте были пробелы и его ширина изменилась.
        /// </summary>
        private static bool WidenSpaces(SKRunSegment seg, float extra)
        {
            var text = seg.Text;
            var metrics = seg.GlyphMetrics;

            int spaces = 0;
            foreach (var c in text)
                if (c == ' ' || c == '\t') spaces++;
            if (spaces == 0) return false;

            if (metrics.Length != text.Length)
            {
                seg.Width += spaces * extra;
                return true;
            }

            var widths = new float[metrics.Length];
            float total = 0f;
            for (int k = 0; k < metrics.Length; k++)
            {
                float width = metrics[k].Width;
                if (text[k] == ' ' || text[k] == '\t') width += extra;
                widths[k] = width;
                total += width;
            }

            var rebuilt = new SKGlyphMetrics[metrics.Length];
            float logicalX = 0f;
            for (int k = 0; k < metrics.Length; k++)
            {
                rebuilt[k] = new SKGlyphMetrics
                {
                    CharIndex = metrics[k].CharIndex,
                    X = seg.IsRightToLeft ? total - logicalX - widths[k] : logicalX,
                    Width = widths[k]
                };
                logicalX += widths[k];
            }

            seg.GlyphMetrics = rebuilt;
            seg.Width = total;
            return true;
        }

        /// <summary>
        /// Заново ставит куски переставленной строки на лист в том порядке, в каком они
        /// там уже стоят, — после того как их ширины изменились. Кусок нулевой ширины,
        /// стоящий в одной точке с соседом, идёт перед ним: окажись он после соседа, его
        /// место было бы правее.
        /// </summary>
        private static void PlaceInVisualOrder(SKLineLayout line)
        {
            var segs = line.Segments;
            if (segs.Count == 0) return;

            var visual = new List<SKRunSegment>(segs);
            visual.Sort((a, b) =>
            {
                int byX = a.X.CompareTo(b.X);
                if (byX != 0) return byX;
                int byWidth = (a.Width > 0f ? 1 : 0).CompareTo(b.Width > 0f ? 1 : 0);
                if (byWidth != 0) return byWidth;
                return segs.IndexOf(a).CompareTo(segs.IndexOf(b));
            });

            float x = visual[0].X;
            foreach (var seg in visual)
            {
                seg.X = x;
                x += seg.Width;
            }

            line.TextWidth = x;
        }

        /// <summary>
        /// Кусок сегмента: знаки [start, start + length) с тем же оформлением, своей
        /// шириной и своими местами знаков, отсчитанными от начала куска.
        /// </summary>
        private static SKRunSegment CloneSegmentSlice(SKRunSegment seg, int start, int length)
        {
            var source = seg.GlyphMetrics;
            float left = source[start].X;
            float right = source[start + length - 1].Right;

            var metrics = new SKGlyphMetrics[length];
            for (int i = 0; i < length; i++)
            {
                var g = source[start + i];
                metrics[i] = new SKGlyphMetrics
                {
                    CharIndex = g.CharIndex,
                    X = g.X - left,
                    Width = g.Width
                };
            }

            return new SKRunSegment
            {
                Text = seg.Text.Substring(start, length),
                FontFamily = seg.FontFamily,
                FontSizePt = seg.FontSizePt,
                BaselineShiftPt = seg.BaselineShiftPt,
                CharacterSpacingPt = seg.CharacterSpacingPt,
                HorizontalScale = seg.HorizontalScale,
                IsBold = seg.IsBold,
                IsItalic = seg.IsItalic,
                IsUnderline = seg.IsUnderline,
                UnderlineStyle = seg.UnderlineStyle,
                UnderlineColor = seg.UnderlineColor,
                IsStrikethrough = seg.IsStrikethrough,
                IsDoubleStrikethrough = seg.IsDoubleStrikethrough,
                IsHidden = seg.IsHidden,
                IsHiddenMarked = seg.IsHiddenMarked,
                IsOutline = seg.IsOutline,
                IsShadow = seg.IsShadow,
                IsEmboss = seg.IsEmboss,
                IsImprint = seg.IsImprint,
                EmphasisMark = seg.EmphasisMark,
                CharBorderColor = seg.CharBorderColor,
                CharBorderWidthPt = seg.CharBorderWidthPt,
                CharBorderStyle = seg.CharBorderStyle,
                Effects = seg.Effects,
                Color = seg.Color,
                HighlightColor = seg.HighlightColor,
                ColorCode = seg.ColorCode,
                HighlightCode = seg.HighlightCode,
                GlobalCharOffset = seg.GlobalCharOffset + start,
                InlineImageId = seg.InlineImageId,
                ObjectWidthPt = seg.ObjectWidthPt,
                ObjectHeightPt = seg.ObjectHeightPt,
                WrapFragmentIndex = seg.WrapFragmentIndex,
                X = seg.X + left,
                Width = right - left,
                GlyphMetrics = metrics,
                IsLineBreak = seg.IsLineBreak,
                IsSoftHyphen = seg.IsSoftHyphen,
                SoftHyphenShown = seg.SoftHyphenShown,
                UsesGlyphPositions = seg.UsesGlyphPositions
            };
        }

        /// <summary>
        /// Кегельная площадка знака: высота ровно в кегль, поделённая на подъём и спуск
        /// в пропорции метрик гарнитуры. У Times New Roman это 0,805 кегля над базовой
        /// линией и 0,195 под ней. Без метрик площадка целиком над базовой линией.
        /// </summary>
        private static (float Ascent, float Descent) EmBoxExtents(float fontSizePt, float ascent, float descent)
        {
            float total = ascent + descent;
            if (total <= 0f || fontSizePt <= 0f) return (Math.Max(fontSizePt, 0f), 0f);

            float emAscent = fontSizePt * ascent / total;
            return (emAscent, fontSizePt - emAscent);
        }

        // Значения SKTextLayout.LineTextAlignment — числа выравнивания w:textAlignment модели.
        private const int LineTextAlignTop = 2;
        private const int LineTextAlignCenter = 3;
        private const int LineTextAlignBottom = 4;

        private static SKLineLayout BuildEmptyLine(
            SKTextLayout layout, SKLineSpacing lineSpacing, SKRunSegment? format = null)
        {
            var typeface = GetOrCreateTypeface(
                format?.FontFamily ?? StyleResolver.FallbackFontFamily,
                format?.IsBold ?? false,
                format?.IsItalic ?? false);
            var font = GetOrCreateFont(typeface, format?.FontSizePt ?? StyleResolver.FallbackFontSizePt);

            font.GetFontMetrics(out var metrics);
            float ascent = Math.Abs(metrics.Ascent);
            float descent = Math.Abs(metrics.Descent);

            // Межстрочный зазор — над подъёмом, как в FinalizeLine и у Word.
            float top = ascent + Math.Abs(metrics.Leading);
            float height = lineSpacing.Resolve(top, descent, 0f);
            float baseline = (height - (top + descent)) / 2f + top;

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

            // Абзац справа налево: начало строки — у правого края, туда же уходит и
            // отступ первой строки. По ширине тянутся все строки, кроме последней, — она
            // встаёт к началу, то есть вправо. Строка с переставленными кусками растянута
            // при вёрстке и тоже прижимается к началу: висящие пробелы её конца уходят
            // за левый край, как у Word.
            if (layout.IsRightToLeft)
            {
                float toStart = area - firstExtra - line.TextWidth;

                return line.WrapLeftPt + layout.Alignment switch
                {
                    RenderAlignment.Center => (area - firstExtra - line.TextWidth) / 2f,
                    RenderAlignment.Left => 0f,
                    RenderAlignment.Justify or RenderAlignment.Distribute =>
                        line.IsLastLine || line.IsBidiReordered ? toStart : 0f,
                    _ => toStart
                };
            }

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
            // Растянутое выравнивание тянет строки так же, как по ширине; последнюю строку
            // оно разводит по буквам ещё при вёрстке (DistributeLastLine).
            if (layout.Alignment != RenderAlignment.Justify && layout.Alignment != RenderAlignment.Distribute)
                return 0f;
            if (lineIndex < 0 || lineIndex >= layout.Lines.Count) return 0f;
            var line = layout.Lines[lineIndex];

            // Строка с переставленными по направлению кусками растянута ещё при вёрстке
            // (JustifyReorderedLines): ширина пробелов уже в её раскладке, и добавки
            // поверх неё нет. Растяжка, которая копит сдвиг по порядку текста, к такой
            // строке не подходит — её куски стоят на листе в другом порядке.
            if (line.IsBidiReordered) return 0f;

            // Последняя строка по ширине не растягивается. Сжиматься ей приходится, если
            // в неё вошло слово за счёт сжатия пробелов: иначе она вылезла бы за край.
            if (line.IsLastLine && !layout.AllowsJustifyShrink) return 0f;

            var segs = line.Segments;
            bool fragmented = line.HasWrapFragments;
            if (fragmented && (fragmentIndex < 0 || fragmentIndex >= line.WrapFragments.Count))
                return 0f;

            // Скрытый текст в растяжку не входит: его пробелы места не занимают.
            bool InFragment(SKRunSegment seg)
                => !seg.IsHidden && (!fragmented || seg.WrapFragmentIndex == fragmentIndex);

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

            // Строка шире области: в неё вошло слово за счёт сжатия пробелов, и теперь
            // пробелы делят нехватку. Сжатие не глубже того, что допустила вёрстка.
            if (free < 0f)
                return layout.AllowsJustifyShrink ? free / spaces : 0f;

            if (line.IsLastLine) return 0f;
            if (free == 0f) return 0f;

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

        /// <summary>
        /// Предел сжатия пробелов при выравнивании по ширине: пятая часть их ширины,
        /// как у Word 2013 и новее.
        /// </summary>
        private const float MaxJustifyShrink = 0.2f;

        /// <summary>
        /// Какую долю своей ширины слово может не уместить в строку, чтобы сжатие
        /// пробелов его всё же втянуло. Подобрано по разбивке Word на строках, где
        /// решает именно это: короткие слова («по», «не») с нехваткой больше половины
        /// длины Word переносит, длинные с той же нехваткой оставляет.
        /// </summary>
        private const float MaxJustifyShrinkWordShare = 0.5f;

        /// <summary>
        /// Кегль строчных букв в «малых прописных» относительно кегля текста.
        /// </summary>
        private const float SmallCapsScale = 0.8f;

        // Индекс последнего сегмента строки, содержащего непробельный символ. -1 — таких нет.
        private static int LastContentSegIndex(SKLineLayout line)
        {
            int last = -1;
            for (int si = 0; si < line.Segments.Count; si++)
            {
                var s = line.Segments[si];
                if (s.IsHidden) continue;
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
            // Скрытый текст места в строке не занимает.
            if (format.IsHidden) return 0f;

            // Перенос строки и мягкий перенос места в строке не занимают: первый стоит
            // в её конце, второй виден только там, где по нему разорвано слово, — и
            // тогда его ширину даёт дефис (ShowTrailingSoftHyphen).
            if (ch == "\n" || ch == SoftHyphen) return 0f;

            // Неразрывный дефис шириной обычного — им он и рисуется.
            if (ch == NonBreakingHyphen) ch = "-";

            // Объект в строке занимает собственный габарит, а не ширину глифа
            // символа-заполнителя.
            if (format.IsInlineObject) return format.ObjectWidthPt;

            var typeface = GetOrCreateTypeface(format.FontFamily, format.IsBold, format.IsItalic);
            var font = GetOrCreateFont(typeface, format.FontSizePt);

            // Разрядка прибавляется к каждому знаку, включая пробелы, — так её считает
            // Word, и строка переносится там же, где у него. Масштаб по ширине растягивает
            // сам знак, разрядку — нет.
            return font.MeasureText(ch) * format.HorizontalScale + format.CharacterSpacingPt;
        }

        /// <summary>
        /// Рисует текст сегмента. Без разрядки — обычным DrawText. С разрядкой знаки
        /// ставятся по одному на позиции из метрик глифов: DrawText расставил бы их по
        /// собственным ширинам шрифта, и текст разошёлся бы с кареткой и выделением.
        /// </summary>
        private static void DrawSegmentText(
            SKCanvas canvas, SKRunSegment seg, float x, float baseY, SKFont font, SKPaint paint)
        {
            // Масштаб по ширине (w:w): знаки рисуются шрифтом, растянутым по горизонтали.
            // Общий шрифт из кеша не трогается — у растянутого сегмента свой экземпляр.
            if (Math.Abs(seg.HorizontalScale - 1f) > 0.0001f && !seg.IsInlineObject)
            {
                using var scaledFont = new SKFont(font.Typeface, font.Size)
                {
                    ScaleX = seg.HorizontalScale,
                    Subpixel = font.Subpixel,
                    LinearMetrics = font.LinearMetrics,
                    Edging = font.Edging,
                    Hinting = font.Hinting
                };
                DrawSegmentTextWithFont(canvas, seg, x, baseY, scaledFont, paint);
                return;
            }

            DrawSegmentTextWithFont(canvas, seg, x, baseY, font, paint);
        }

        /// <summary>
        /// Рисует буквы сегмента с эффектами Word: тенью (w:shadow), рельефом (w:emboss),
        /// гравировкой (w:imprint) и контуром (w:outline). Без эффектов — как обычно.
        ///
        /// Рельеф и гравировка — светлые буквы с тёмным краем, сдвинутым вниз-вправо у
        /// рельефа и вверх-влево у гравировки: так Word показывает выпуклый и вдавленный
        /// текст. Контур — обвод букв без заливки.
        /// </summary>
        private static void DrawSegmentGlyphs(
            SKCanvas canvas, SKRunSegment seg, float x, float baseY, SKFont font, SKPaint paint)
        {
            // Настраиваемые эффекты (Word 2010+ и свои) — отдельным путём; без них
            // буквы рисуются, как прежде.
            if (seg.Effects is not null)
            {
                DrawSegmentGlyphsWithEffects(canvas, seg, x, baseY, font, paint, seg.Effects);
                return;
            }

            if (!seg.IsOutline && !seg.IsShadow && !seg.IsEmboss && !seg.IsImprint)
            {
                DrawSegmentText(canvas, seg, x, baseY, font, paint);
                return;
            }

            float offset = Math.Max(0.5f, seg.FontSizePt * 0.04f);
            SKColor ink = paint.Color;

            if (seg.IsEmboss || seg.IsImprint)
            {
                float edgeOffset = seg.IsEmboss ? offset : -offset;

                using (var edge = new SKPaint { Color = BlendColor(ink, SKColors.Black, 0.35f), IsAntialias = true })
                    DrawSegmentText(canvas, seg, x + edgeOffset, baseY + edgeOffset, font, edge);

                using var face = new SKPaint { Color = BlendColor(ink, SKColors.White, 0.8f), IsAntialias = true };
                DrawSegmentText(canvas, seg, x, baseY, font, face);
                return;
            }

            if (seg.IsShadow)
            {
                using var shadow = new SKPaint
                {
                    Color = BlendColor(ink, new SKColor(0x80, 0x80, 0x80), 0.6f).WithAlpha(0xB0),
                    IsAntialias = true
                };
                DrawSegmentText(canvas, seg, x + offset, baseY + offset, font, shadow);
            }

            if (seg.IsOutline)
            {
                using var stroke = paint.Clone();
                stroke.Style = SKPaintStyle.Stroke;
                stroke.StrokeWidth = Math.Max(0.3f, seg.FontSizePt * 0.03f);
                stroke.StrokeJoin = SKStrokeJoin.Round;
                DrawSegmentText(canvas, seg, x, baseY, font, stroke);
                return;
            }

            DrawSegmentText(canvas, seg, x, baseY, font, paint);
        }

        /// <summary>Смесь двух цветов: 0 — первый, 1 — второй. Прозрачность — от первого.</summary>
        private static SKColor BlendColor(SKColor from, SKColor to, float amount)
        {
            float t = Math.Clamp(amount, 0f, 1f);
            return new SKColor(
                (byte)(from.Red + (to.Red - from.Red) * t),
                (byte)(from.Green + (to.Green - from.Green) * t),
                (byte)(from.Blue + (to.Blue - from.Blue) * t),
                from.Alpha);
        }

        /// <summary>
        /// Буквы с настраиваемыми эффектами. Слои снизу вверх, как у Word: отражение,
        /// свечение, тень, сами буквы, контур. Старые эффекты Word (w:shadow, w:outline,
        /// w:emboss, w:imprint) идут там, где настраиваемого того же рода нет.
        /// </summary>
        private static void DrawSegmentGlyphsWithEffects(
            SKCanvas canvas, SKRunSegment seg, float x, float baseY, SKFont font, SKPaint paint,
            SKTextEffects effects)
        {
            float offset = Math.Max(0.5f, seg.FontSizePt * 0.04f);
            SKColor ink = paint.Color;

            if (effects.Reflection is { } reflection)
                DrawReflectionEffect(canvas, seg, x, baseY, font, paint, reflection, effects.Outline);

            if (effects.Glow is { } glow)
                DrawGlowEffect(canvas, seg, x, baseY, font, glow);

            if (effects.Shadow is { } shadow)
            {
                DrawShadowEffect(canvas, seg, x, baseY, font, shadow);
            }
            else if (seg.IsShadow)
            {
                using var legacyShadow = new SKPaint
                {
                    Color = BlendColor(ink, new SKColor(0x80, 0x80, 0x80), 0.6f).WithAlpha(0xB0),
                    IsAntialias = true
                };
                DrawSegmentText(canvas, seg, x + offset, baseY + offset, font, legacyShadow);
            }

            if (seg.IsEmboss || seg.IsImprint)
            {
                float edgeOffset = seg.IsEmboss ? offset : -offset;

                using (var edge = new SKPaint { Color = BlendColor(ink, SKColors.Black, 0.35f), IsAntialias = true })
                    DrawSegmentText(canvas, seg, x + edgeOffset, baseY + edgeOffset, font, edge);

                using var face = new SKPaint { Color = BlendColor(ink, SKColors.White, 0.8f), IsAntialias = true };
                DrawSegmentText(canvas, seg, x, baseY, font, face);
            }
            else
            {
                bool hollow = effects.Outline is { } own ? own.Hollow : seg.IsOutline;
                if (!hollow)
                    DrawSegmentText(canvas, seg, x, baseY, font, paint);
            }

            if (effects.Outline is { } outline)
            {
                DrawOutlineEffect(canvas, seg, x, baseY, font, outline);
            }
            else if (seg.IsOutline)
            {
                using var legacyStroke = paint.Clone();
                legacyStroke.Style = SKPaintStyle.Stroke;
                legacyStroke.StrokeWidth = Math.Max(0.3f, seg.FontSizePt * 0.03f);
                legacyStroke.StrokeJoin = SKStrokeJoin.Round;
                DrawSegmentText(canvas, seg, x, baseY, font, legacyStroke);
            }
        }

        /// <summary>
        /// Размытие Skia в сигмах по радиусу размытия Word: радиус — примерно две сигмы.
        /// </summary>
        private static float BlurSigma(float radiusPt) => Math.Max(0.1f, radiusPt / 2f);

        /// <summary>
        /// Прямоугольник, в который гарантированно ложатся буквы сегмента вместе с
        /// эффектом шириной <paramref name="pad"/>: по нему ограничиваются слои Skia,
        /// чтобы эффект не заводил слой во весь лист.
        /// </summary>
        private static SKRect EffectBounds(SKRunSegment seg, float x, float baseY, SKFont font, float pad)
        {
            font.GetFontMetrics(out var metrics);
            float over = seg.FontSizePt * 0.3f + pad;
            return new SKRect(
                x - over,
                baseY - Math.Abs(metrics.Ascent) - seg.FontSizePt * 0.1f - pad,
                x + seg.Width + over,
                baseY + Math.Abs(metrics.Descent) + pad);
        }

        /// <summary>
        /// Тень: копия букв цветом тени, сдвинутая на расстояние под углом и размытая.
        /// Длинная тень — сплошной след букв на всё расстояние; копии кладутся в общий
        /// слой, чтобы наложение не темнило полупрозрачную тень.
        /// </summary>
        private static void DrawShadowEffect(
            SKCanvas canvas, SKRunSegment seg, float x, float baseY, SKFont font, SKShadowEffect shadow)
        {
            double angle = shadow.AngleDeg * Math.PI / 180.0;
            float dx = (float)(Math.Cos(angle) * shadow.DistancePt);
            float dy = (float)(Math.Sin(angle) * shadow.DistancePt);

            if (shadow.IsLong)
            {
                int steps = Math.Clamp((int)MathF.Ceiling(shadow.DistancePt / 0.5f), 1, 80);
                var bounds = EffectBounds(seg, x, baseY, font, shadow.DistancePt + 1f);

                using var layerPaint = new SKPaint { Color = SKColors.Black.WithAlpha(shadow.Color.Alpha) };
                int layer = canvas.SaveLayer(bounds, layerPaint);

                using var trail = new SKPaint { Color = shadow.Color.WithAlpha(0xFF), IsAntialias = true };
                for (int i = steps; i >= 1; i--)
                {
                    float t = i / (float)steps;
                    DrawSegmentText(canvas, seg, x + dx * t, baseY + dy * t, font, trail);
                }

                canvas.RestoreToCount(layer);
                return;
            }

            using var paint = new SKPaint { Color = shadow.Color, IsAntialias = true };
            if (shadow.BlurPt > 0.01f)
                paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, BlurSigma(shadow.BlurPt));

            DrawSegmentText(canvas, seg, x + dx, baseY + dy, font, paint);
        }

        /// <summary>Свечение: размытый ореол цвета свечения вокруг букв.</summary>
        private static void DrawGlowEffect(
            SKCanvas canvas, SKRunSegment seg, float x, float baseY, SKFont font, SKGlowEffect glow)
        {
            if (glow.RadiusPt <= 0.01f) return;

            using var paint = new SKPaint
            {
                Color = glow.Color,
                IsAntialias = true,
                Style = SKPaintStyle.StrokeAndFill,
                StrokeWidth = glow.RadiusPt,
                StrokeJoin = SKStrokeJoin.Round,
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, BlurSigma(glow.RadiusPt))
            };
            DrawSegmentText(canvas, seg, x, baseY, font, paint);
        }

        /// <summary>
        /// Отражение: буквы, перевёрнутые вокруг нижней кромки строки (с зазором), тают
        /// книзу от непрозрачности у букв до нуля на видимой доле высоты. Буквы и маска
        /// тают в своём слое, чтобы не задеть то, что уже нарисовано под ними.
        /// </summary>
        private static void DrawReflectionEffect(
            SKCanvas canvas, SKRunSegment seg, float x, float baseY, SKFont font, SKPaint paint,
            SKReflectionEffect reflection, SKOutlineEffect? outline)
        {
            if (reflection.StartOpacity <= 0.001f || reflection.Size <= 0.001f) return;

            font.GetFontMetrics(out var metrics);
            float ascent = Math.Abs(metrics.Ascent);
            float descent = Math.Abs(metrics.Descent);
            float height = ascent + descent;

            float bottom = baseY + descent;
            float top = bottom + reflection.DistancePt;
            float blurPad = reflection.BlurPt * 3f + 1f;
            float over = seg.FontSizePt * 0.3f + blurPad;

            var layerRect = new SKRect(x - over, top - blurPad, x + seg.Width + over, top + height + blurPad);
            int layer = canvas.SaveLayer(layerRect, null);

            canvas.Save();
            canvas.Translate(0f, bottom * 2f + reflection.DistancePt);
            canvas.Scale(1f, -1f);

            using (var mirror = new SKPaint { Color = paint.Color, IsAntialias = true })
            {
                if (reflection.BlurPt > 0.01f)
                    mirror.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, BlurSigma(reflection.BlurPt));

                // Полые буквы отражаются контуром, остальные — заливкой.
                if (outline is { Hollow: true })
                {
                    mirror.Color = ApplyReadingInk(outline.Color);
                    mirror.Style = SKPaintStyle.Stroke;
                    mirror.StrokeWidth = outline.WidthPt;
                    mirror.StrokeJoin = SKStrokeJoin.Round;
                }

                DrawSegmentText(canvas, seg, x, baseY, font, mirror);
            }

            canvas.Restore();

            float fadeEnd = top + Math.Max(0.5f, height * reflection.Size);
            using var fade = SKShader.CreateLinearGradient(
                new SKPoint(0f, top),
                new SKPoint(0f, fadeEnd),
                new[]
                {
                    SKColors.Black.WithAlpha((byte)Math.Round(255f * Math.Clamp(reflection.StartOpacity, 0f, 1f))),
                    SKColors.Black.WithAlpha(0)
                },
                SKShaderTileMode.Clamp);
            using var mask = new SKPaint { Shader = fade, BlendMode = SKBlendMode.DstIn };
            canvas.DrawRect(layerRect, mask);

            canvas.RestoreToCount(layer);
        }

        /// <summary>
        /// Контур букв своей толщины, цвета и штриха. «Снаружи» — линия двойной толщины,
        /// у которой внутренняя половина вырезана по форме букв: буквы сохраняют свою
        /// толщину, контур обводит их снаружи.
        /// </summary>
        private static void DrawOutlineEffect(
            SKCanvas canvas, SKRunSegment seg, float x, float baseY, SKFont font, SKOutlineEffect outline)
        {
            float width = Math.Max(0.05f, outline.WidthPt);

            using var stroke = new SKPaint
            {
                Color = ApplyReadingInk(outline.Color),
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = outline.Outside ? width * 2f : width,
                StrokeJoin = SKStrokeJoin.Round,
                StrokeCap = SKStrokeCap.Butt
            };

            float[]? dash = outline.Dash switch
            {
                SKOutlineDash.Dash => new[] { 4f * width, 3f * width },
                SKOutlineDash.Dot => new[] { 1f * width, 1f * width },
                SKOutlineDash.DashDot => new[] { 4f * width, 3f * width, 1f * width, 3f * width },
                SKOutlineDash.LongDash => new[] { 8f * width, 3f * width },
                _ => null
            };

            using var dashEffect = dash is null ? null : SKPathEffect.CreateDash(dash, 0f);
            if (dashEffect is not null) stroke.PathEffect = dashEffect;

            if (!outline.Outside)
            {
                DrawSegmentText(canvas, seg, x, baseY, font, stroke);
                return;
            }

            var bounds = EffectBounds(seg, x, baseY, font, width * 2f + 1f);
            int layer = canvas.SaveLayer(bounds, null);

            DrawSegmentText(canvas, seg, x, baseY, font, stroke);

            using var cut = new SKPaint { Color = SKColors.Black, IsAntialias = true, BlendMode = SKBlendMode.DstOut };
            DrawSegmentText(canvas, seg, x, baseY, font, cut);

            canvas.RestoreToCount(layer);
        }

        /// <summary>
        /// Настраиваемые эффекты модели в эффекты отрисовки: цвета — в SKColor вместе с
        /// прозрачностью, величины — в пункты. Пустой набор — null.
        /// </summary>
        private static SKTextEffects? ToRenderEffects(TextEffects? effects)
        {
            if (effects is null || effects.IsEmpty) return null;

            return new SKTextEffects(
                effects.Outline is { } outline
                    ? new SKOutlineEffect(
                        ParseColor(outline.Color),
                        (float)Math.Max(0.05, outline.WidthPt),
                        (SKOutlineDash)(int)outline.Dash,
                        outline.Placement == OutlinePlacement.Outside,
                        outline.Hollow)
                    : null,
                effects.Shadow is { } shadow
                    ? new SKShadowEffect(
                        WithTransparency(ParseColor(shadow.Color), shadow.Transparency),
                        (float)Math.Max(0.0, shadow.BlurPt),
                        (float)Math.Max(0.0, shadow.DistancePt),
                        (float)shadow.AngleDeg,
                        shadow.IsLong)
                    : null,
                effects.Glow is { } glow
                    ? new SKGlowEffect(
                        WithTransparency(ParseColor(glow.Color), glow.Transparency),
                        (float)Math.Max(0.0, glow.RadiusPt))
                    : null,
                effects.Reflection is { } reflection
                    ? new SKReflectionEffect(
                        (float)Math.Clamp(1.0 - reflection.Transparency, 0.0, 1.0),
                        (float)Math.Clamp(reflection.Size, 0.0, 1.0),
                        (float)Math.Max(0.0, reflection.DistancePt),
                        (float)Math.Max(0.0, reflection.BlurPt))
                    : null);
        }

        /// <summary>Цвет с прозрачностью эффекта: 0 — сплошной, 1 — невидимый.</summary>
        private static SKColor WithTransparency(SKColor color, double transparency)
            => color.WithAlpha((byte)Math.Round(255.0 * Math.Clamp(1.0 - transparency, 0.0, 1.0)));

        /// <summary>Ширина текста образца эффектов — чтобы поставить его по середине.</summary>
        public static float MeasureEffectsPreview(string text, string fontFamily, float fontSizePt)
        {
            if (string.IsNullOrEmpty(text)) return 0f;

            var font = GetOrCreateFont(GetOrCreateTypeface(fontFamily, false, false), fontSizePt);
            return font.MeasureText(text);
        }

        /// <summary>
        /// Образец эффектов для окна настройки: строка текста, нарисованная тем же
        /// движком, что и документ, — со всеми эффектами букв и рамкой знаков. По нему
        /// видно результат до применения.
        /// </summary>
        /// <param name="canvas">Холст образца.</param>
        /// <param name="text">Текст образца.</param>
        /// <param name="fontFamily">Гарнитура.</param>
        /// <param name="fontSizePt">Кегль.</param>
        /// <param name="textColor">Цвет букв.</param>
        /// <param name="effects">Настраиваемые эффекты; null — без них.</param>
        /// <param name="borderWidthPt">Толщина рамки знаков; ноль — рамки нет.</param>
        /// <param name="borderColor">Цвет рамки знаков; null — цвет букв.</param>
        /// <param name="borderStyle">Вид линии рамки знаков.</param>
        /// <param name="x">Левый край текста.</param>
        /// <param name="baselineY">Базовая линия текста.</param>
        /// <returns>Ширина нарисованного текста.</returns>
        public static float DrawEffectsPreview(
            SKCanvas canvas, string text, string fontFamily, float fontSizePt, SKColor textColor,
            TextEffects? effects, float borderWidthPt, string? borderColor, CharBorderStyle borderStyle,
            float x, float baselineY)
        {
            if (string.IsNullOrEmpty(text)) return 0f;

            var seg = new SKRunSegment
            {
                Text = text,
                FontFamily = fontFamily,
                FontSizePt = fontSizePt,
                Color = textColor,
                Effects = ToRenderEffects(effects),
                CharBorderWidthPt = Math.Max(0f, borderWidthPt),
                CharBorderColor = borderColor,
                CharBorderStyle = (int)borderStyle
            };

            var typeface = GetOrCreateTypeface(fontFamily, false, false);
            var font = GetOrCreateFont(typeface, fontSizePt);

            seg.GlyphMetrics = BuildGlyphMetrics(seg, font);
            float width = 0f;
            foreach (var glyph in seg.GlyphMetrics) width += glyph.Width;
            seg.Width = width;

            var line = new SKLineLayout();
            line.Segments.Add(seg);

            using var paint = new SKPaint { Color = textColor, IsAntialias = true };
            DrawSegmentGlyphs(canvas, seg, x, baselineY, font, paint);

            if (seg.CharBorderWidthPt > 0f)
                DrawCharBorder(canvas, line, 0, 0, seg, x, baselineY, width, font, textColor);

            return width;
        }

        /// <summary>
        /// Образец набора «Мои эффекты» для плитки меню: текст, нарисованный тем же
        /// движком, что и документ, — со всем, что ставит набор: настраиваемыми
        /// эффектами, эффектами из окна шрифта Word, рамкой знаков и знаком ударения.
        /// </summary>
        /// <param name="canvas">Холст образца.</param>
        /// <param name="text">Текст образца.</param>
        /// <param name="fontFamily">Гарнитура.</param>
        /// <param name="fontSizePt">Кегль.</param>
        /// <param name="textColor">Цвет букв.</param>
        /// <param name="preset">Набор.</param>
        /// <param name="x">Левый край текста.</param>
        /// <param name="baselineY">Базовая линия текста.</param>
        /// <returns>Ширина нарисованного текста.</returns>
        public static float DrawPresetPreview(
            SKCanvas canvas, string text, string fontFamily, float fontSizePt, SKColor textColor,
            TextEffectPreset preset, float x, float baselineY)
        {
            if (string.IsNullOrEmpty(text)) return 0f;

            var border = preset.Border is { Enabled: true } enabled ? enabled : null;

            var seg = new SKRunSegment
            {
                Text = text,
                FontFamily = fontFamily,
                FontSizePt = fontSizePt,
                Color = textColor,
                Effects = ToRenderEffects(preset.Effects),
                IsOutline = preset.IsOutline == true,
                IsShadow = preset.IsShadow == true,
                IsEmboss = preset.IsEmboss == true,
                IsImprint = preset.IsImprint == true,
                EmphasisMark = (int)(preset.EmphasisMark ?? Models.Inline.EmphasisMark.None),
                CharBorderWidthPt = border is null ? 0f : Math.Max(0.25f, (float)border.WidthPt),
                CharBorderColor = border?.Color,
                CharBorderStyle = (int)(border?.Style ?? Models.Inline.CharBorderStyle.Single)
            };

            var typeface = GetOrCreateTypeface(fontFamily, false, false);
            var font = GetOrCreateFont(typeface, fontSizePt);

            seg.GlyphMetrics = BuildGlyphMetrics(seg, font);
            float width = 0f;
            foreach (var glyph in seg.GlyphMetrics) width += glyph.Width;
            seg.Width = width;

            var line = new SKLineLayout();
            line.Segments.Add(seg);

            using var paint = new SKPaint { Color = textColor, IsAntialias = true };
            DrawSegmentGlyphs(canvas, seg, x, baselineY, font, paint);

            if (seg.CharBorderWidthPt > 0f)
                DrawCharBorder(canvas, line, 0, 0, seg, x, baselineY, width, font, textColor);

            if (seg.EmphasisMark != 0)
                DrawEmphasisMarks(canvas, seg, x, baselineY, font, textColor);

            return width;
        }

        /// <summary>
        /// Украшения сегмента поверх букв: точечное подчёркивание показанного скрытого
        /// текста, рамка вокруг знаков (w:bdr) и знаки ударения (w:em).
        /// </summary>
        private static void DrawSegmentDecorations(
            SKCanvas canvas, SKLineLayout line, int segIdx, int lastContentSeg, SKRunSegment seg,
            float segX, float baseY, float width, SKFont font, SKColor textColor)
        {
            // Скрытый текст при показанных непечатаемых знаках — точечное подчёркивание,
            // как в Word.
            if (seg.IsHiddenMarked && width > 0f)
                UnderlinePainter.Draw(canvas, Models.Inline.UnderlineStyle.Dotted,
                    segX, width, baseY, seg.FontSizePt, textColor, null);

            if (seg.CharBorderWidthPt > 0f && width > 0f)
                DrawCharBorder(canvas, line, segIdx, lastContentSeg, seg, segX, baseY, width, font, textColor);

            if (seg.EmphasisMark != 0)
                DrawEmphasisMarks(canvas, seg, segX, baseY, font, textColor);
        }

        /// <summary>Просвет рамки знаков над подъёмом шрифта, в долях кегля (снято с Word).</summary>
        private const float CharBorderTopPadEm = 0.15f;

        /// <summary>Просвет рамки знаков под спуском шрифта, в долях кегля.</summary>
        private const float CharBorderBottomPadEm = 0.05f;

        /// <summary>Середина знака ударения над подъёмом шрифта, в долях кегля (снято с Word).</summary>
        private const float EmphasisOverGapEm = 0.27f;

        /// <summary>Середина точки снизу под спуском шрифта, в долях кегля.</summary>
        private const float EmphasisUnderGapEm = 0.12f;

        /// <summary>Радиус знака ударения в долях кегля.</summary>
        private const float EmphasisRadiusEm = 0.055f;

        /// <summary>
        /// Сколько места над подъёмом шрифта занимает знак ударения вместе с запасом —
        /// на столько он раздвигает строку.
        /// </summary>
        private const float EmphasisOverReachEm = EmphasisOverGapEm + EmphasisRadiusEm * 2f;

        /// <summary>Сколько места под спуском шрифта занимает точка снизу вместе с запасом.</summary>
        private const float EmphasisUnderReachEm = EmphasisUnderGapEm + EmphasisRadiusEm * 2f;

        /// <summary>
        /// Рамка вокруг знаков. Слова одного рана лежат в разных сегментах (пробелы —
        /// отдельно), поэтому рамка рисуется по кускам: верх и низ у каждого сегмента,
        /// боковые стороны — только там, где у соседнего сегмента рамки нет. Так
        /// выходит одна рамка на весь фрагмент, как у Word, без перегородок между словами.
        ///
        /// Линии ставятся на сетку пикселей, как подчёркивание: иначе тонкая рамка
        /// расплывается сглаживанием в два бледных ряда, а на стыках кусков темнеют
        /// точки перекрытия.
        /// </summary>
        private static void DrawCharBorder(
            SKCanvas canvas, SKLineLayout line, int segIdx, int lastContentSeg, SKRunSegment seg,
            float segX, float baseY, float width, SKFont font, SKColor textColor)
        {
            bool SameBorder(int index)
            {
                if (index < 0 || index >= line.Segments.Count) return false;
                var other = line.Segments[index];
                if (other.IsHidden || other.IsTabJump || other.InlineImageId.HasValue) return false;
                if (SegHighlightWidth(line, index, lastContentSeg) <= 0f) return false;
                return Math.Abs(other.CharBorderWidthPt - seg.CharBorderWidthPt) < 0.01f
                    && other.CharBorderColor == seg.CharBorderColor
                    && other.CharBorderStyle == seg.CharBorderStyle;
            }

            font.GetFontMetrics(out var metrics);
            float top = baseY - Math.Abs(metrics.Ascent) - seg.FontSizePt * CharBorderTopPadEm;
            float bottom = baseY + Math.Abs(metrics.Descent) + seg.FontSizePt * CharBorderBottomPadEm;
            float left = segX;
            float right = segX + width;
            float stroke = seg.CharBorderWidthPt;

            var style = (Models.Inline.CharBorderStyle)seg.CharBorderStyle;

            // Двойная рамка — две линии заданной толщины с просветом в толщину линии:
            // внешняя и внутренняя, на полторы толщины от середины.
            float[] rings = style == Models.Inline.CharBorderStyle.Double
                ? new[] { stroke, -stroke }
                : new[] { 0f };

            bool openLeft = SameBorder(segIdx - 1);
            bool openRight = SameBorder(segIdx + 1);

            var matrix = canvas.TotalMatrix;
            bool snap = UnderlinePainter.IsAxisAligned(matrix);
            float strokePx = 0f;
            if (snap)
            {
                strokePx = MathF.Max(1f, MathF.Round(stroke * matrix.ScaleY));
                stroke = strokePx / matrix.ScaleY;

                // Края кусков — на целых пикселях: соседние куски смыкаются встык, без
                // просвета и без перекрытия.
                left = (MathF.Round(matrix.ScaleX * left + matrix.TransX) - matrix.TransX) / matrix.ScaleX;
                right = (MathF.Round(matrix.ScaleX * right + matrix.TransX) - matrix.TransX) / matrix.ScaleX;
            }

            SKColor color = textColor;
            if (UnderlinePainter.TryParseLineColor(seg.CharBorderColor, out var own))
                color = ApplyReadingInk(own);

            using var pen = new SKPaint
            {
                Color = color,
                StrokeWidth = stroke,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeCap = SKStrokeCap.Butt
            };

            // Точки и штрихи отсчитываются от нулевой отметки холста, как у подчёркивания:
            // у соседних кусков рамки рисунок продолжается, а не начинается заново.
            float[]? dash = style switch
            {
                Models.Inline.CharBorderStyle.Dotted => new[] { stroke, stroke },
                Models.Inline.CharBorderStyle.Dashed => new[] { stroke * 3f, stroke * 2f },
                _ => null
            };
            using var dashEffect = dash is null ? null : SKPathEffect.CreateDash(dash, 0f);
            if (dashEffect is not null) pen.PathEffect = dashEffect;

            float half = stroke / 2f;

            foreach (float ring in rings)
            {
                // Кольцо рамки: у двойной внешнее отходит от середины наружу, внутреннее —
                // внутрь. Боковые стороны сдвигаются только у закрытых краёв фрагмента.
                float ringTop = top - ring;
                float ringBottom = bottom + ring;
                float ringLeft = openLeft ? left : left - ring;
                float ringRight = openRight ? right : right + ring;

                if (snap)
                {
                    ringTop = UnderlinePainter.SnapLineY(ringTop, strokePx, matrix);
                    ringBottom = UnderlinePainter.SnapLineY(ringBottom, strokePx, matrix);
                }

                // Верх и низ у крайних кусков заходят на полтолщины за боковую сторону —
                // углы рамки закрыты. Между кусками линии идут встык.
                float hLeft = openLeft ? ringLeft : ringLeft - half;
                float hRight = openRight ? ringRight : ringRight + half;

                canvas.DrawLine(hLeft, ringTop, hRight, ringTop, pen);
                canvas.DrawLine(hLeft, ringBottom, hRight, ringBottom, pen);

                if (!openLeft)
                    canvas.DrawLine(ringLeft, ringTop, ringLeft, ringBottom, pen);
                if (!openRight)
                    canvas.DrawLine(ringRight, ringTop, ringRight, ringBottom, pen);
            }
        }

        /// <summary>
        /// Знаки ударения над каждой буквой сегмента (под буквой — у «точки снизу»).
        /// Пробелы знака не получают — как в Word. Высота знака — над подъёмом шрифта:
        /// так Word ставит его выше самых высоких букв, и строка под него раздвигается
        /// (FinalizeLine).
        /// </summary>
        private static void DrawEmphasisMarks(
            SKCanvas canvas, SKRunSegment seg, float segX, float baseY, SKFont font, SKColor textColor)
        {
            if (seg.GlyphMetrics.Length != seg.Text.Length) return;

            font.GetFontMetrics(out var metrics);
            float radius = Math.Max(0.6f, seg.FontSizePt * EmphasisRadiusEm);

            bool below = seg.EmphasisMark == (int)Models.Inline.EmphasisMark.UnderDot;
            float centerY = below
                ? baseY + Math.Abs(metrics.Descent) + seg.FontSizePt * EmphasisUnderGapEm
                : baseY - Math.Abs(metrics.Ascent) - seg.FontSizePt * EmphasisOverGapEm;

            using var paint = new SKPaint { Color = textColor, IsAntialias = true };
            if (seg.EmphasisMark == (int)Models.Inline.EmphasisMark.Circle)
            {
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = Math.Max(0.3f, radius * 0.35f);
            }

            using var tail = new SKPaint
            {
                Color = textColor,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = Math.Max(0.3f, radius * 0.6f),
                StrokeCap = SKStrokeCap.Round
            };

            for (int i = 0; i < seg.Text.Length; i++)
            {
                if (char.IsWhiteSpace(seg.Text[i])) continue;

                var glyph = seg.GlyphMetrics[i];
                float centerX = segX + glyph.X + glyph.Width / 2f;

                canvas.DrawCircle(centerX, centerY, radius, paint);

                // Запятая — точка с хвостиком вниз-влево.
                if (seg.EmphasisMark == (int)Models.Inline.EmphasisMark.Comma)
                    canvas.DrawLine(centerX + radius * 0.6f, centerY,
                        centerX - radius * 0.4f, centerY + radius * 2.2f, tail);
            }
        }

        /// <summary>
        /// Рисует текст сегмента готовым шрифтом: без разрядки — обычным DrawText, с
        /// разрядкой — знаками на позициях из метрик глифов.
        /// </summary>
        private static void DrawSegmentTextWithFont(
            SKCanvas canvas, SKRunSegment seg, float x, float baseY, SKFont font, SKPaint paint)
        {
            // Сегмент справа налево рисуется целиком по правилам своей письменности:
            // обратный порядок букв, у арабского — связные начертания по соседям.
            if (seg.IsRightToLeft && !seg.IsInlineObject && seg.Text.Length > 0)
            {
                DrawRightToLeftText(canvas, seg, x, baseY, font, paint);
                return;
            }
            if ((Math.Abs(seg.CharacterSpacingPt) < 0.0001f && !seg.UsesGlyphPositions)
                || seg.IsInlineObject
                || seg.GlyphMetrics.Length != seg.Text.Length)
            {
                canvas.DrawText(seg.Text, x, baseY, font, paint);
                return;
            }

            var glyphIds = font.GetGlyphs(seg.Text);

            // Суррогатные пары дают глифов меньше, чем символов, и позиции по символам
            // к ним не прикладываются — такой сегмент рисуется без разрядки.
            if (glyphIds.Length != seg.Text.Length)
            {
                canvas.DrawText(seg.Text, x, baseY, font, paint);
                return;
            }

            var positions = new float[glyphIds.Length];
            for (int i = 0; i < glyphIds.Length; i++)
                positions[i] = x + seg.GlyphMetrics[i].X;

            using var builder = new SKTextBlobBuilder();
            var run = builder.AllocateHorizontalRun(font, glyphIds.Length, baseY);
            run.SetGlyphs(glyphIds);
            run.SetPositions(positions);

            using var blob = builder.Build();
            if (blob is null) return;

            canvas.DrawText(blob, 0f, 0f, paint);
        }

        /// <summary>
        /// Рисует заполнитель прыжка табуляции: точки, чёрточки или сплошную линию.
        ///
        /// Сетка точек отсчитывается от КОНЦА прыжка влево, а не от его начала. Начало у
        /// каждой строки своё — названия глав разной длины, — и точки, расставленные от
        /// него, вставали бы вразнобой. Конец же у всех строк один: это отметка табуляции,
        /// к которой прижат номер страницы. Отсчёт от неё выстраивает точки столбиками
        /// сверху вниз — именно так набирают книжные оглавления.
        ///
        /// Крайняя точка у номера отбрасывается: зазор перед цифрой читается как воздух,
        /// а слипшиеся точка и цифра — как опечатка.
        ///
        /// Так рисуется заполнитель с заданной плотностью — наш, настраиваемый. Без
        /// плотности (ноль: позиция пришла из Word или поставлена без настройки)
        /// заполнитель рисуется как у Word, см. <see cref="DrawWordTabLeader"/>.
        /// </summary>
        /// <param name="gridOriginPt">Левый край текстовой области абзаца: от него Word
        /// отсчитывает сетку знаков заполнителя.</param>
        private static void DrawTabLeader(SKCanvas canvas, SKRunSegment seg, float xPt, float baseYPt, float gridOriginPt)
        {
            if (seg.TabLeader == SKTabLeader.None) return;
            if (seg.Width <= 0.5f) return;

            SKColor color = ApplyReadingInk(seg.Color);

            if (seg.TabLeaderDensity <= 0.01f)
            {
                DrawWordTabLeader(canvas, seg, xPt, baseYPt, gridOriginPt, color);
                return;
            }

            if (seg.TabLeader == SKTabLeader.Line)
            {
                using var linePaint = new SKPaint
                {
                    Color = color,
                    StrokeWidth = Math.Max(0.5f, seg.FontSizePt * 0.05f),
                    IsAntialias = true
                };

                float lineY = baseYPt + seg.FontSizePt * 0.12f;
                canvas.DrawLine(xPt, lineY, xPt + seg.Width, lineY, linePaint);
                return;
            }

            string mark = seg.TabLeader == SKTabLeader.Dashes ? "-" : ".";

            var typeface = GetOrCreateTypeface(seg.FontFamily, seg.IsBold, seg.IsItalic);
            var font = GetOrCreateFont(typeface, seg.FontSizePt);

            float markWidth = MeasureChar(mark, seg);
            if (markWidth <= 0.01f) return;

            // Шаг сетки: у точки он шире собственной ширины знака — сплошная дорожка точек
            // читается как многоточие, а не как ведущая линия.
            // Чёрточки идут вплотную, шаг в ширину самого знака: так Word рисует
            // заполнитель «дефисы» — сплошным рядом, а не пунктиром с просветами.
            float stepPt = seg.TabLeader == SKTabLeader.Dots
                ? Math.Max(markWidth * 1.35f, seg.FontSizePt * 0.2f)
                : markWidth;

            // Плотность из позиции табуляции: вдвое больше единицы — вдвое чаще знаки.
            // Ноль означает «как обычно» — так рисуются прыжки, собранные без этой
            // величины. Пределы стоят здесь, а не только в ленте: величина приезжает и
            // из файла, где её мог поправить кто угодно, а шаг в ноль повесил бы цикл.
            float density = seg.TabLeaderDensity;
            if (density > 0.01f)
                stepPt /= Math.Clamp(density, 0.25f, 4f);

            if (stepPt < 0.2f) stepPt = 0.2f;

            // Зазор после названия главы — в ширину знака: точка вплотную к букве
            // читается как точка в конце слова. Перед номером страницы зазор шире, в шаг
            // сетки: там цифра, и слипшиеся точка с цифрой читаются как опечатка.
            float rightPt = xPt + seg.Width - stepPt;
            float leftPt = xPt + markWidth;

            if (rightPt < leftPt) return;

            // Знаки заполняют отрезок от края до края: крайний правый стоит у номера,
            // крайний левый — сразу за названием. Раньше шаг был ровно заданным, и
            // остаток отрезка, не кратный шагу, уходил пустотой слева — между названием
            // и первой точкой зияла дыра до целого шага, а при редком заполнителе (малая
            // плотность) — в несколько знаков шириной. Теперь число промежутков берётся с
            // округлением вверх, а шаг чуть ужимается под отрезок: дорожка становится
            // гуще не больше чем на один знак, и дыры нет.
            float spanPt = rightPt - leftPt;
            int count;
            float drawStepPt = stepPt;

            if (spanPt < 0.01f)
            {
                count = 1;
            }
            else
            {
                int gaps = (int)Math.Ceiling(spanPt / stepPt - 0.001f);
                if (gaps < 1) gaps = 1;
                count = gaps + 1;
                drawStepPt = spanPt / gaps;
            }

            if (count <= 0) return;
            if (count > MaxLeaderMarks) count = MaxLeaderMarks;

            ushort glyph = typeface.GetGlyph(mark[0]);
            if (glyph == 0) return;

            // Вся дорожка уходит в Skia одним блобом, а не знак за знаком.
            //
            // Поштучная отрисовка стоила вызова DrawText на каждую точку: в строке
            // оглавления их до полусотни, на листе с оглавлением — за тысячу, и всё
            // это заново на каждый кадр, включая мигание каретки. Каждый такой вызов
            // — это ещё и разбор строки в глифы. Правка внутри оглавления шла рывками
            // именно поэтому. Блоб собирает глифы один раз на строку и отдаёт их
            // одной отрисовкой.
            EnsureLeaderBuffers(count);

            var glyphs = _leaderGlyphs!;
            var positions = _leaderPositions!;

            for (int i = 0; i < count; i++)
            {
                glyphs[i] = glyph;
                positions[i] = rightPt - drawStepPt * i;
            }

            using var paint = new SKPaint { Color = color, IsAntialias = true };

            using var builder = new SKTextBlobBuilder();
            var run = builder.AllocateHorizontalRun(font, count, baseYPt);
            run.SetGlyphs(new ReadOnlySpan<ushort>(glyphs, 0, count));
            run.SetPositions(new ReadOnlySpan<float>(positions, 0, count));

            using var blob = builder.Build();
            if (blob is null) return;

            canvas.DrawText(blob, 0f, 0f, paint);
        }

        /// <summary>
        /// Заполнитель как у Word: целые знаки — точки, дефисы или подчёркивания — с шагом
        /// в ширину самого знака, на сетке, отсчитанной от левого края текстовой области.
        /// Рисуются только знаки, целиком вошедшие в прыжок, поэтому у концов остаются
        /// неполные клетки сетки — зазоры до текста, как в Word («349.90 ___ 4198.80»).
        /// Сетка общая для всех строк абзаца: знаки соседних строк стоят столбиками.
        /// Линия-заполнитель у Word — тоже ряд знаков подчёркивания, а не сплошная черта.
        /// </summary>
        private static void DrawWordTabLeader(
            SKCanvas canvas, SKRunSegment seg, float xPt, float baseYPt, float gridOriginPt, SKColor color)
        {
            char markChar = seg.TabLeader switch
            {
                SKTabLeader.Dots => '.',
                SKTabLeader.Dashes => '-',
                _ => '_'
            };
            string mark = markChar.ToString();

            var typeface = GetOrCreateTypeface(seg.FontFamily, seg.IsBold, seg.IsItalic);
            var font = GetOrCreateFont(typeface, seg.FontSizePt);

            float pitchPt = MeasureChar(mark, seg);
            if (pitchPt <= 0.01f) return;

            float startPt = xPt;
            float endPt = xPt + seg.Width;

            // Первая клетка сетки, начинающаяся не левее начала прыжка, и последняя,
            // целиком кончающаяся до его конца. Допуск — на дробные ошибки сложения.
            int first = (int)Math.Ceiling((startPt - gridOriginPt) / pitchPt - 0.001f);
            int last = (int)Math.Floor((endPt - gridOriginPt) / pitchPt + 0.001f) - 1;

            int count = last - first + 1;
            if (count <= 0) return;
            if (count > MaxLeaderMarks) count = MaxLeaderMarks;

            ushort glyph = typeface.GetGlyph(markChar);
            if (glyph == 0) return;

            EnsureLeaderBuffers(count);

            var glyphs = _leaderGlyphs!;
            var positions = _leaderPositions!;

            for (int i = 0; i < count; i++)
            {
                glyphs[i] = glyph;
                positions[i] = gridOriginPt + (first + i) * pitchPt;
            }

            using var paint = new SKPaint { Color = color, IsAntialias = true };

            using var builder = new SKTextBlobBuilder();
            var run = builder.AllocateHorizontalRun(font, count, baseYPt);
            run.SetGlyphs(new ReadOnlySpan<ushort>(glyphs, 0, count));
            run.SetPositions(new ReadOnlySpan<float>(positions, 0, count));

            using var blob = builder.Build();
            if (blob is null) return;

            canvas.DrawText(blob, 0f, 0f, paint);
        }

        /// <summary>
        /// Табуляции-черты абзаца на одной строке: тонкая вертикальная линия во всю
        /// высоту строки на каждой отметке-черте. Цвет — цвет текста листа, как у линий
        /// рамки «авто». Линия ставится на целый пиксель, иначе на экране расплывается.
        /// </summary>
        /// <param name="textLeftX">Левый край текстовой области абзаца — от него отсчитаны отметки.</param>
        private static void DrawBarTabs(SKCanvas canvas, SKTextLayout layout, float textLeftX, float lineTopY, float lineHeight)
        {
            var bars = layout.BarTabPositionsPt;
            if (bars is null || bars.Count == 0 || lineHeight <= 0f) return;

            float scale = CanvasScale(canvas);
            float minWidthPt = 1f / scale;

            using var paint = new SKPaint
            {
                Color = ResolveParagraphBorderColor(null),
                StrokeWidth = Math.Max(minWidthPt, BarTabWidthPt),
                IsStroke = true,
                IsAntialias = false
            };

            foreach (float positionPt in bars)
            {
                float x = textLeftX + positionPt;
                float xPx = (float)Math.Round(x * scale - 0.5f) + 0.5f;
                x = xPx / scale;

                canvas.DrawLine(x, lineTopY, x, lineTopY + lineHeight, paint);
            }
        }

        /// <summary>Толщина табуляции-черты: пиксель при 100 %, как у Word.</summary>
        private const float BarTabWidthPt = 0.75f;

        /// <summary>
        /// Предел знаков в одной дорожке заполнителя.
        ///
        /// Нужен не ради красоты: плотность приезжает из файла, где её мог поправить кто
        /// угодно, а очень широкая дорожка при крошечном шаге дала бы десятки тысяч
        /// знаков, которых всё равно не различить.
        /// </summary>
        private const int MaxLeaderMarks = 4096;

        // Буферы дорожки живут между вызовами: отрисовка идёт на одном потоке, а заводить
        // на каждую строку по два массива значит кормить сборщик мусора на каждом кадре.
        [ThreadStatic] private static ushort[]? _leaderGlyphs;
        [ThreadStatic] private static float[]? _leaderPositions;

        private static void EnsureLeaderBuffers(int count)
        {
            if (_leaderGlyphs is not null && _leaderGlyphs.Length >= count) return;

            int size = count < 256 ? 256 : count;
            _leaderGlyphs = new ushort[size];
            _leaderPositions = new float[size];
        }

        private static SKGlyphMetrics[] BuildGlyphMetrics(SKRunSegment seg, SKFont font)
        {
            if (string.IsNullOrEmpty(seg.Text))
                return Array.Empty<SKGlyphMetrics>();

            // Прыжок табуляции — один «глиф» шириной во весь прыжок. Без этого каретка
            // и выделение мерили бы его глифом табуляции из шрифта, который почти везде
            // нулевой ширины: каретка вставала бы в начало прыжка, а выделение его теряло.
            if (seg.IsTabJump)
            {
                return new[]
                {
                    new SKGlyphMetrics
                    {
                        CharIndex = seg.GlobalCharOffset,
                        X = 0f,
                        Width = seg.Width
                    }
                };
            }

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

                // Масштаб по ширине растягивает знак вместе с его местом в строке.
                width *= seg.HorizontalScale;

                // Разрядка — часть ширины знака: каретка и выделение встают туда же,
                // где знак нарисован.
                width += seg.CharacterSpacingPt;

                // Скрытый текст — нулевой ширины: все его позиции каретки в одной точке.
                if (seg.IsHidden) width = 0f;

                // Перенос строки и невидимый мягкий перенос — тоже нулевой ширины.
                if (seg.IsLineBreak || (seg.IsSoftHyphen && !seg.SoftHyphenShown)) width = 0f;

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
                        // Ужатие под лист чтения. Печатный лист шире экранного, и
                        // таблица с фиксированными колонками уезжает за его край;
                        // здесь она уменьшается в той же пропорции, что и сам лист.
                        widths[i] = MmToPt(col.WidthValue) * ReadingContentScale;
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
        /// Публичная обёртка RenderCellBorders для DocumentCanvas. Полотно рисует на
        /// экран, поэтому линии строятся из целых пикселей.
        /// </summary>
        public static void RenderCellBordersPublic(
            SKCanvas canvas, SKTableLayout table, SKTableCellLayout cell,
            float cellX, float cellY,
            float visibleH,
            float canvasScale = 1f,
            bool suppressTop = false, bool suppressBottom = false,
            bool sliceEnd = false)
            => RenderCellBorders(canvas, table, cell, cellX, cellY, visibleH, canvasScale,
                suppressTop, suppressBottom, sliceEnd, snapToPixels: true);

        /// <summary>
        /// Узор заливки ячейки (pct25, diagStripe…) поверх её цвета фона — тем же узором,
        /// что у заливки абзаца. Публичная: заливку ячеек рисует и полотно.
        /// </summary>
        public static void RenderCellShadingPattern(
            SKCanvas canvas, SKTableCellLayout cell, float x, float y, float width, float height)
        {
            var shader = GetShadingPatternShader(cell.ShadingPattern, cell.ShadingPatternColor);
            if (shader is null || width <= 0f || height <= 0f) return;

            using var paint = new SKPaint { Shader = shader, IsAntialias = false };
            canvas.DrawRect(x, y, width, height, paint);
        }

        /// <summary>
        /// Границы ячейки. Рисует их TableBorderPainter по сетке границ таблицы: общая
        /// граница двух ячеек — одна линия, линии стыкуются в узлах сетки, на экране
        /// строятся из целых пикселей.
        ///
        /// sliceEnd — ячейка последняя в куске таблицы на странице. snapToPixels —
        /// рисуем на экран; на печати размеры линий точные.
        /// </summary>
        private static void RenderCellBorders(
            SKCanvas canvas,
            SKTableLayout? table,
            SKTableCellLayout cell,
            float cellX,
            float cellY,
            float visibleH,
            float canvasScale = 1f,
            bool suppressTop = false,
            bool suppressBottom = false,
            bool sliceEnd = false,
            bool snapToPixels = false)
        {
            TableBorderPainter.DrawCell(canvas, table, cell, cellX, cellY, visibleH, canvasScale,
                suppressTop, suppressBottom, sliceEnd, snapToPixels, ResolveParagraphBorderColor);
        }

        /// <summary>
        /// Нейтральные ли это чернила: чёрный, около-чёрный или серый без явного
        /// оттенка. Только такую рамку чтение перекрашивает под свою бумагу — цвет,
        /// выбранный автором сознательно, остаётся авторским.
        /// </summary>
        private static bool IsNeutralInk(SKColor c)
        {
            int max = Math.Max(c.Red, Math.Max(c.Green, c.Blue));
            int min = Math.Min(c.Red, Math.Min(c.Green, c.Blue));
            return max - min <= 12 && max <= 140;
        }

        private static (BorderStyle Style, double ThicknessPt, string? Color) TopLineOf(TableCell cell)
            => cell.Borders.Top == BorderStyle.None
                ? (BorderStyle.None, 0.0, null)
                : (cell.Borders.Top, cell.Borders.EffectiveTopThicknessPt(), cell.Borders.EffectiveTopColor());

        private static (BorderStyle Style, double ThicknessPt, string? Color) BottomLineOf(TableCell cell)
            => cell.Borders.Bottom == BorderStyle.None
                ? (BorderStyle.None, 0.0, null)
                : (cell.Borders.Bottom, cell.Borders.EffectiveBottomThicknessPt(), cell.Borders.EffectiveBottomColor());

        private static (BorderStyle Style, double ThicknessPt, string? Color) LeftLineOf(TableCell cell)
            => cell.Borders.Left == BorderStyle.None
                ? (BorderStyle.None, 0.0, null)
                : (cell.Borders.Left, cell.Borders.EffectiveLeftThicknessPt(), cell.Borders.EffectiveLeftColor());

        private static (BorderStyle Style, double ThicknessPt, string? Color) RightLineOf(TableCell cell)
            => cell.Borders.Right == BorderStyle.None
                ? (BorderStyle.None, 0.0, null)
                : (cell.Borders.Right, cell.Borders.EffectiveRightThicknessPt(), cell.Borders.EffectiveRightColor());

        /// <summary>
        /// Границы таблицы по её сетке.
        ///
        /// У Word граница между двумя ячейками одна: если ячейки по обе стороны задали
        /// разные линии, остаётся сильнейшая (BorderLineCodes.Stronger), вторая не
        /// рисуется вовсе. Когда каждая ячейка рисовала свою линию, на общей границе
        /// лежали обе: тонкая штриховая выходила вдвое толще, а разные линии соседей
        /// накладывались одна на другую.
        ///
        /// Сетка хранит линию на каждый отрезок между узлами: вдоль стороны
        /// объединённой ячейки соседи разные, и с каждым спор свой. По сетке же
        /// отрисовка видит, какие линии сходятся в узле, и стыкует их (TableBorderPainter).
        /// </summary>
        private static void BuildTableEdges(TableBlock table, int rowCount, int colCount, SKTableLayout layout)
        {
            if (rowCount <= 0 || colCount <= 0) return;

            // Какая ячейка занимает клетку. Порядок — как у TableBlock.GetCell: первая
            // подходящая ячейка списка.
            var owner = new TableCell?[rowCount, colCount];
            foreach (var cell in table.Cells)
            {
                int lastRow = Math.Min(cell.Row + Math.Max(cell.RowSpan, 1), rowCount) - 1;
                int lastCol = Math.Min(cell.Column + Math.Max(cell.ColSpan, 1), colCount) - 1;

                for (int r = Math.Max(cell.Row, 0); r <= lastRow; r++)
                    for (int c = Math.Max(cell.Column, 0); c <= lastCol; c++)
                        owner[r, c] ??= cell;
            }

            var filled = new bool[rowCount, colCount];
            for (int r = 0; r < rowCount; r++)
                for (int c = 0; c < colCount; c++)
                    filled[r, c] = owner[r, c] is not null;

            (BorderStyle Style, double ThicknessPt, string? Color) none = (BorderStyle.None, 0.0, null);

            var horizontal = new SKTableBorderLineLayout?[rowCount + 1, colCount];
            for (int r = 0; r <= rowCount; r++)
            {
                for (int c = 0; c < colCount; c++)
                {
                    var upper = r > 0 ? owner[r - 1, c] : null;
                    var lower = r < rowCount ? owner[r, c] : null;

                    // Обе клетки — одна объединённая ячейка: границы между ними нет.
                    if (upper is not null && ReferenceEquals(upper, lower)) continue;

                    var line = BorderLineCodes.Stronger(
                        upper is not null ? BottomLineOf(upper) : none,
                        lower is not null ? TopLineOf(lower) : none);

                    if (line.Style != BorderStyle.None && line.ThicknessPt > 0)
                        horizontal[r, c] = BorderLineToLayout(line.Style, line.ThicknessPt, line.Color);
                }
            }

            var vertical = new SKTableBorderLineLayout?[rowCount, colCount + 1];
            for (int r = 0; r < rowCount; r++)
            {
                for (int c = 0; c <= colCount; c++)
                {
                    var before = c > 0 ? owner[r, c - 1] : null;
                    var after = c < colCount ? owner[r, c] : null;

                    if (before is not null && ReferenceEquals(before, after)) continue;

                    var line = BorderLineCodes.Stronger(
                        before is not null ? RightLineOf(before) : none,
                        after is not null ? LeftLineOf(after) : none);

                    if (line.Style != BorderStyle.None && line.ThicknessPt > 0)
                        vertical[r, c] = BorderLineToLayout(line.Style, line.ThicknessPt, line.Color);
                }
            }

            layout.HorizontalEdges = horizontal;
            layout.VerticalEdges = vertical;
            layout.SlotFilled = filled;
        }

        /// <summary>
        /// Границы ячейки для вёрстки: самая сильная из линий сетки на каждой её стороне.
        /// row и col — первая строка и первая колонка ячейки.
        /// </summary>
        private static SKTableCellBorderLayout ResolveCellBorderLayout(
            SKTableLayout layout, TableCell cell, int row, int col, int rowCount, int colCount)
        {
            var horizontal = layout.HorizontalEdges;
            var vertical = layout.VerticalEdges;

            var noLine = new SKTableBorderLineLayout { WidthPt = 0f, Style = SKBorderLineShape.None };
            if (horizontal is null || vertical is null)
                return new SKTableCellBorderLayout { Top = noLine, Bottom = noLine, Left = noLine, Right = noLine };

            int lastRow = Math.Max(row, Math.Min(row + cell.RowSpan, rowCount) - 1);
            int lastCol = Math.Max(col, Math.Min(col + cell.ColSpan, colCount) - 1);

            SKTableBorderLineLayout? top = null;
            SKTableBorderLineLayout? bottom = null;
            for (int c = col; c <= lastCol; c++)
            {
                top = StrongerEdge(top, horizontal[row, c]);
                bottom = StrongerEdge(bottom, horizontal[lastRow + 1, c]);
            }

            SKTableBorderLineLayout? left = null;
            SKTableBorderLineLayout? right = null;
            for (int r = row; r <= lastRow; r++)
            {
                left = StrongerEdge(left, vertical[r, col]);
                right = StrongerEdge(right, vertical[r, lastCol + 1]);
            }

            return new SKTableCellBorderLayout
            {
                Top = top ?? noLine,
                Bottom = bottom ?? noLine,
                Left = left ?? noLine,
                Right = right ?? noLine
            };
        }

        private static SKTableBorderLineLayout? StrongerEdge(SKTableBorderLineLayout? current, SKTableBorderLineLayout? candidate)
        {
            if (candidate is null) return current;
            if (current is null) return candidate;
            return candidate.Weight > current.Weight ? candidate : current;
        }

        /// <summary>
        /// Заливка тёмная настолько, что текст цвета «авто» Word пишет на ней белым:
        /// яркость по YIQ ниже половины шкалы.
        /// </summary>
        private static bool IsDarkFill(string? color)
        {
            if (string.IsNullOrWhiteSpace(color)) return false;
            string hex = color.Trim();
            if (!hex.StartsWith("#", StringComparison.Ordinal)) hex = "#" + hex;
            if (!SKColor.TryParse(hex, out var fill) || fill.Alpha == 0) return false;

            double brightness = (fill.Red * 299 + fill.Green * 587 + fill.Blue * 114) / 1000.0;
            return brightness < 128.0;
        }

        /// <summary>
        /// Текст без своего цвета (цвет «авто») становится белым: так его пишет Word на
        /// тёмной заливке. Текст со своим цветом не трогается.
        /// </summary>
        private static void ApplyAutoColorOnDark(SKTextLayout layout)
        {
            foreach (var line in layout.Lines)
            {
                foreach (var seg in line.Segments)
                {
                    if (string.IsNullOrWhiteSpace(seg.ColorCode)
                        || string.Equals(seg.ColorCode, "auto", StringComparison.OrdinalIgnoreCase))
                        seg.Color = SKColors.White;
                }
            }
        }

        private static SKTableBorderLineLayout BorderLineToLayout(
            BorderStyle style, double thicknessPt, string? color)
        {
            return new SKTableBorderLineLayout
            {
                WidthPt = style == BorderStyle.None ? 0f : (float)thicknessPt,
                Color = color ?? "#000000",
                Style = BorderLineCodes.Of(style),
                Weight = BorderLineCodes.Weight(style, thicknessPt)
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
            => a.IsLineBreak == b.IsLineBreak
            && a.IsSoftHyphen == b.IsSoftHyphen
            && a.FontFamily == b.FontFamily
            && a.FontSizePt == b.FontSizePt
            && a.BaselineShiftPt == b.BaselineShiftPt
            && a.CharacterSpacingPt == b.CharacterSpacingPt
            && a.HorizontalScale == b.HorizontalScale
            && a.IsBold == b.IsBold
            && a.IsItalic == b.IsItalic
            && a.IsUnderline == b.IsUnderline
            && a.UnderlineStyle == b.UnderlineStyle
            && a.UnderlineColor == b.UnderlineColor
            && a.IsStrikethrough == b.IsStrikethrough
            && a.IsDoubleStrikethrough == b.IsDoubleStrikethrough
            && a.IsHidden == b.IsHidden
            && a.IsHiddenMarked == b.IsHiddenMarked
            && a.IsOutline == b.IsOutline
            && a.IsShadow == b.IsShadow
            && a.IsEmboss == b.IsEmboss
            && a.IsImprint == b.IsImprint
            && a.EmphasisMark == b.EmphasisMark
            && a.CharBorderColor == b.CharBorderColor
            && a.CharBorderWidthPt == b.CharBorderWidthPt
            && a.CharBorderStyle == b.CharBorderStyle
            && Equals(a.Effects, b.Effects)
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

            // Умолчания Skia здесь не годятся, и обе поправки — про то, чтобы буква
            // стояла там, где её посчитала раскладка, а не там, куда её округлил
            // растр.
            //
            // Subpixel: без него начало каждой буквы округляется до целого пикселя.
            // Строка, сдвинувшаяся на долю пункта — от правки рамки картинки, от
            // выключки, от чего угодно, — расползается: одна буква осталась на
            // месте, соседняя перевалила через границу пикселя и уехала, и
            // просветы между ними гуляют, хотя в раскладке не менялись вовсе.
            //
            // LinearMetrics: без него округляются и ширины букв. Тогда ширина
            // строки зависит от того, в каком масштабе её сейчас показывают, и один
            // и тот же абзац разбивается на 100% иначе, чем на 200%, а на печати
            // иначе, чем на экране. Для рукописи это недопустимо: страница обязана
            // быть одной и той же страницей.
            //
            // Плата — текст чуть мягче по горизонтали: штрихи перестают ложиться
            // точно на пиксель. Word и просмотрщики PDF платят её по той же причине.
            //
            // Edging: субпиксельное сглаживание (ClearType), как у Word на экране. Шрифт
            // его только просит — включает поверхность, на которой рисуют: у холста
            // без порядка субпикселей (печать, PDF, снимки листа книги, снимок кадра
            // при выключенном в Windows ClearType) Skia рисует буквы серым, как
            // прежде. См. ScreenTextSmoothing.
            var cache = _fontCache ??= new Dictionary<(IntPtr, int), SKFont>();

            if (cache.TryGetValue(key, out var ready)) return ready;

            var font = new SKFont(typeface, sizePt)
            {
                Subpixel = true,
                LinearMetrics = true,
                Edging = SKFontEdging.SubpixelAntialias
            };

            cache[key] = font;
            return font;
        }

        private static SKTypeface GetOrCreateTypeface(string family, bool bold, bool italic)
        {
            var key = (family, bold, italic);

            var style = (bold, italic) switch
            {
                (true, true) => SKFontStyle.BoldItalic,
                (true, false) => SKFontStyle.Bold,
                (false, true) => SKFontStyle.Italic,
                _ => SKFontStyle.Normal
            };

            // Шрифт, уложенный в проект, идёт впереди системного: он и есть тот
            // самый, которым набрана рукопись, а системный с тем же именем может
            // оказаться другой версией или вовсе другим шрифтом.
            //
            // В общий кеш такая гарнитура не кладётся: она принадлежит хранилищу
            // шрифтов проекта и живёт до его смены, а кеш свои гарнитуры
            // освобождает. Освободить чужую значит уронить отрисовку в нативном
            // коде — без стека и без объяснений.
            if (Services.ProjectFonts.HasAny)
            {
                var embedded = Services.ProjectFonts.Match(family, style);
                if (embedded is not null) return embedded;
            }

            if (_typefaceCache.TryGetValue(key, out var cached))
                return cached;

            var typeface = SKTypeface.FromFamilyName(family, style)
                ?? SKTypeface.FromFamilyName(StyleResolver.FallbackFontFamily, style)
                ?? SKTypeface.Default;

            _typefaceCache.TryAdd(key, typeface);
            return typeface;
        }

        /// <summary>
        /// Ищет шрифт для символа с указанным кодпоинтом.
        ///
        /// По умолчанию не ищет ничего и возвращает null: символ рисуется как
        /// .notdef — привычный пустой квадрат, ровно как в любом другом редакторе.
        /// Системный MatchCharacter отсюда убран намеренно. Он молча дорисовывал
        /// символы, которых в выбранной гарнитуре нет, чужим шрифтом; выглядело
        /// удобно — берёшь латинскую гарнитуру и пишешь по-русски, — но цена в
        /// том, что человек не видит, что половина текста набрана не тем, что он
        /// выбрал, и узнаёт об этом в вёрстке или в печати.
        ///
        /// Подстановка включается настройкой SubstituteMissingGlyphs и тогда идёт
        /// в два шага: сперва карта скриптов — заданное человеком правило
        /// «кириллицу набирать вот этим», — потом общий шрифт подстановки.
        ///
        /// Шрифт, в котором знака тоже нет, не подставляется: квадрат был бы тот
        /// же самый, а кусок текста молча сменил бы гарнитуру.
        /// </summary>
        private static string? FindFallbackFamily(int codepoint, StyleResolver? styles)
        {
            if (styles is null || !styles.SubstituteMissingGlyphs) return null;

            if (styles.ScriptFontMap.Count > 0)
            {
                string? scriptName = GetScriptName(codepoint);
                if (scriptName != null && styles.ScriptFontMap.TryGetValue(scriptName, out var preferred)
                    && !string.IsNullOrEmpty(preferred))
                    return preferred;
            }

            string? substitute = styles.SubstituteFontFamily;
            if (string.IsNullOrEmpty(substitute) || IsDecorationFont(substitute)) return null;

            var typeface = GetOrCreateTypeface(substitute, false, false);
            return typeface.GetGlyph(codepoint) != 0 ? substitute : null;
        }

        /// <summary>
        /// Системный шрифт для знака-символа — стрелки, математического знака, фигуры,
        /// типографского знака, — которого нет в гарнитуре текста. Так делает Word, и так
        /// же рисуется маркер списка (DrawListMarker).
        ///
        /// Буквы и цифры сюда не попадают: их подмена чужим шрифтом остаётся под
        /// настройкой SubstituteMissingGlyphs (FindFallbackFamily) — иначе человек не
        /// видел бы, что текст набран не той гарнитурой. У знака же своей гарнитуры в
        /// тексте нет: квадрат вместо стрелки ничего не сообщает, только портит строку.
        /// </summary>
        private static (string Family, bool Bold, bool Italic)? FindSymbolFallback(
            int codepoint, string family, bool bold, bool italic)
        {
            if (!IsSymbolCodepoint(codepoint)) return null;

            return _symbolFallbackFamilyCache.GetOrAdd((codepoint, family, bold, italic), key =>
            {
                // Поиск идёт от гарнитуры и начертания текста: система сперва смотрит
                // другие начертания той же гарнитуры и родственные ей шрифты, а не
                // первый попавшийся шрифт со знаком.
                var style = new SKFontStyle(
                    key.Bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
                    SKFontStyleWidth.Normal,
                    key.Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);

                using var match = SKFontManager.Default.MatchCharacter(
                    key.Family, style, Array.Empty<string>(), key.Codepoint);

                // Segoe UI Symbol — обычный юникодный шрифт знаков, а не декоративный:
                // в нём стрелки и математика стоят на своих местах. Прочие гарнитуры
                // из списка декоративных рисуют на месте знака картинку.
                bool usable = match is not null
                    && match.GetGlyph(key.Codepoint) != 0
                    && (!IsDecorationFont(match.FamilyName)
                        || match.FamilyName.Equals("Segoe UI Symbol", StringComparison.OrdinalIgnoreCase));

                // Система предложила шрифт эмодзи или декоративный (так бывает с «✓»
                // U+2713: Windows отдаёт его Segoe UI Emoji) либо ничего. Тогда знак ищется
                // в юникодных шрифтах знаков по порядку — так же, как его находит Word.
                // Без этого вместо галочки рисовался квадрат.
                if (!usable)
                {
                    foreach (var candidate in SymbolFallbackFamilies)
                    {
                        var candidateFace = GetOrCreateTypeface(candidate, key.Bold, key.Italic);
                        if (!string.Equals(candidateFace.FamilyName, candidate, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (candidateFace.GetGlyph(key.Codepoint) == 0) continue;

                        return (candidate, key.Bold, key.Italic);
                    }

                    return null;
                }

                if (match is null) return null;

                string matchFamily = match.FamilyName;

                bool matchBold = match.FontStyle.Weight >= (int)SKFontStyleWeight.SemiBold;
                bool matchItalic = match.FontStyle.Slant != SKFontStyleSlant.Upright;

                // Другая гарнитура — в начертании текста, если знак в нём есть: жирный и
                // курсив сохраняются. Нет — тем начертанием, в котором знак нашёлся.
                if (!string.Equals(matchFamily, key.Family, StringComparison.OrdinalIgnoreCase))
                {
                    bool sameStyleHasGlyph = GetOrCreateTypeface(matchFamily, key.Bold, key.Italic)
                        .GetGlyph(key.Codepoint) != 0;
                    return sameStyleHasGlyph
                        ? (matchFamily, key.Bold, key.Italic)
                        : (matchFamily, matchBold, matchItalic);
                }

                // Та же гарнитура: знак есть в другом её начертании (у курсива Arial нет
                // «₂», у прямого есть). Рисуется тем начертанием, где он есть.
                if (matchBold == key.Bold && matchItalic == key.Italic) return null;

                return (matchFamily, matchBold, matchItalic);
            });
        }

        /// <summary>
        /// Знак-символ или знак препинания, а не буква, обычная цифра или пробел.
        /// Подстрочные и надстрочные цифры (₂, ²), дроби (½) и цифры в кружках — тоже
        /// знаки: в текстовых гарнитурах их часто нет, а Word рисует их подстановкой.
        /// </summary>
        private static bool IsSymbolCodepoint(int codepoint)
        {
            var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(codepoint);
            return category is System.Globalization.UnicodeCategory.MathSymbol
                or System.Globalization.UnicodeCategory.OtherNumber
                or System.Globalization.UnicodeCategory.CurrencySymbol
                or System.Globalization.UnicodeCategory.ModifierSymbol
                or System.Globalization.UnicodeCategory.OtherSymbol
                or System.Globalization.UnicodeCategory.DashPunctuation
                or System.Globalization.UnicodeCategory.OpenPunctuation
                or System.Globalization.UnicodeCategory.ClosePunctuation
                or System.Globalization.UnicodeCategory.InitialQuotePunctuation
                or System.Globalization.UnicodeCategory.FinalQuotePunctuation
                or System.Globalization.UnicodeCategory.OtherPunctuation
                or System.Globalization.UnicodeCategory.ConnectorPunctuation;
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
        /// <summary>
        /// Юникодные шрифты знаков, в которых ищется знак, когда система подходящего не
        /// предложила. Порядок — от шрифтов Windows, которыми знаки рисует Word, к
        /// шрифтам других систем.
        /// </summary>
        private static readonly string[] SymbolFallbackFamilies =
        {
            "Segoe UI Symbol",
            "Cambria Math",
            "Segoe UI",
            "Arial Unicode MS",
            "MS Gothic",
            "DejaVu Sans",
            "Noto Sans Symbols",
            "Noto Sans Symbols 2"
        };

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

        /// <summary>
        /// Цвет текста, у которого нет собственного. Ставится режимом чтения, чтобы
        /// тема бумаги меняла и написанное на ней. Текст, которому цвет задан вручную,
        /// остаётся своим: тема меняет вид документа, а не его содержание.
        /// null — обычное поведение, чёрный.
        /// </summary>
        public static SKColor? DefaultTextColorOverride { get; set; }

        /// <summary>
        /// Шрифт, которым читатель просит показывать текст. null — как в документе.
        /// Ставится режимом чтения перед проходом отрисовки и меняет исключительно
        /// вёрстку на экране: в модели документа шрифт остаётся авторским.
        /// </summary>
        public static string? ReadingFontFamilyOverride { get; set; }

        /// <summary>
        /// Множитель кегля для чтения. 1 — как в документе. Тоже только вёрстка:
        /// размер шрифта в рукописи не меняется ни на пункт.
        /// </summary>
        public static float ReadingFontScale { get; set; } = 1f;

        /// <summary>
        /// Цвет маркера списка в чтении. Маркер своего цвета не имеет и рисуется
        /// чёрным; на тёмной бумаге это чёрное по тёмному, и точки списка пропадают.
        /// null — обычное поведение.
        /// </summary>
        public static SKColor? ReadingMarkerColorOverride { get; set; }

        /// <summary>
        /// Цвет линий таблицы в чтении. По той же причине, что и маркер: чёрная
        /// рамка на тёмной бумаге превращает таблицу в дыру. null — как задано.
        /// </summary>
        public static SKColor? ReadingBorderColorOverride { get; set; }

        /// <summary>
        /// Во сколько раз ужимается содержимое, размер которого задан в документе:
        /// ширины колонок таблиц и отступы ячеек. Лист чтения меньше печатного, и
        /// таблица в исходных величинах уезжает за его край. 1 — не ужимать.
        /// </summary>
        public static float ReadingContentScale { get; set; } = 1f;

        /// <summary>Кегль с поправкой на ступень размера чтения.</summary>
        private static float ScaleReadingFont(float sizePt)
        {
            float k = ReadingFontScale;
            if (k <= 0f || Math.Abs(k - 1f) < 0.0005f) return sizePt;
            return sizePt * k;
        }

        /// <summary>Семейство шрифта с поправкой на подмену чтения.</summary>
        private static string ResolveReadingFamily(string family)
            => string.IsNullOrWhiteSpace(ReadingFontFamilyOverride) ? family : ReadingFontFamilyOverride!;

        /// <summary>
        /// Цвет краски документа как он записан. Подмена чтения СЮДА не лезет
        /// намеренно: разбор идёт при сборке раскладки, а раскладка кэшируется, и
        /// подменённый здесь цвет запекался бы в кэш. Из-за этого правка цвета и
        /// контраста не доходила до основного текста — она видна только там, где
        /// цвет берётся в момент отрисовки. Подмена и делается в момент отрисовки:
        /// см. <see cref="ApplyReadingInk"/>.
        /// </summary>
        private static SKColor ParseColor(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return SKColors.Black;
            return SKColor.TryParse(hex, out var c) ? c : SKColors.Black;
        }

        /// <summary>
        /// Цвет бумаги чтения. Нужен затем, чтобы разводить с ним документную краску:
        /// серый текст обязан остаться светлее основного и на светлой бумаге, и на
        /// тёмной. null — обычное поведение.
        /// </summary>
        public static SKColor? ReadingPaperColorOverride { get; set; }

        /// <summary>
        /// Приводит документную краску к чтению.
        ///
        /// Чёрный и серый без оттенка — это не выбор автора, а цвет по умолчанию:
        /// им набрана почти вся рукопись, и записан он в неё явно. Пока чтение
        /// подменяло цвет только там, где его нет вовсе, ни контраст, ни цвет текста,
        /// ни тёмная бумага до обычного текста не доходили — а именно ради него всё
        /// это и делалось.
        ///
        /// Цвет с оттенком автор задал сознательно и остаётся авторским: тема меняет
        /// вид документа, а не его содержание.
        /// </summary>
        /// <summary>
        /// Цвет документной краски на текущей бумаге. Тот же расчёт, что применяется к
        /// тексту при отрисовке, — нужен всем, кто рисует рядом с текстом и обязан
        /// попасть в его цвет: каретке в первую очередь.
        /// </summary>
        public static SKColor ResolveInk(SKColor authorColor) => ApplyReadingInk(authorColor);

        /// <summary>
        /// Гарнитура из того же кеша, что у текста документа: шрифт, уложенный в проект,
        /// идёт впереди системного. Нужна тем, кто рисует текст рядом с документом и
        /// обязан попасть в его шрифт, — колонтитулам в первую очередь.
        /// </summary>
        public static SKTypeface ResolveTypeface(string family, bool bold, bool italic)
            => GetOrCreateTypeface(family, bold, italic);

        private static SKColor ApplyReadingInk(SKColor c)
        {
            if (DefaultTextColorOverride is not { } ink) return c;
            if (c.Alpha == 0) return c;
            if (!IsNeutralTextInk(c)) return c;

            // Насколько документная краска светлее чёрного. Серый текст (сноски,
            // служебные пометки) обязан остаться светлее основного, иначе чтение
            // стирает разницу между ним и обычным текстом.
            float grey = Math.Max(c.Red, Math.Max(c.Green, c.Blue)) / 140f;
            if (grey <= 0.02f) return ink.WithAlpha(c.Alpha);

            // Доля бумаги растёт со светлотой серого без ступеньки: светлее в
            // документе — светлее и на бумаге. Раньше доля упиралась в 0.6 уже у
            // #8C8C8C, а серый светлее вовсе не считался краской: «Заголовок 7»
            // (#888888) перекрашивался в цвет темы, а «Заголовок 8» (#999999) рядом
            // оставался чистым серым — два соседних серых выглядели разными цветами.
            var paper = ReadingPaperColorOverride ?? SKColors.White;
            float k = Math.Clamp(grey * 0.6f, 0f, MaxTextInkPaperShare);

            return new SKColor(
                MixChannel(ink.Red, paper.Red, k),
                MixChannel(ink.Green, paper.Green, k),
                MixChannel(ink.Blue, paper.Blue, k),
                c.Alpha);
        }

        /// <summary>
        /// Предельная доля бумаги в перекрашенном сером тексте: самый светлый серый
        /// остаётся заметно темнее бумаги и читается.
        /// </summary>
        private const float MaxTextInkPaperShare = 0.85f;

        /// <summary>
        /// Серый без оттенка любой светлоты — краска по умолчанию, которую тема
        /// перекрашивает. У рамок таблиц предел по светлоте свой (IsNeutralInk):
        /// светлая рамка — это выбор автора, а не цвет по умолчанию.
        /// </summary>
        private static bool IsNeutralTextInk(SKColor c)
        {
            int max = Math.Max(c.Red, Math.Max(c.Green, c.Blue));
            int min = Math.Min(c.Red, Math.Min(c.Green, c.Blue));
            return max - min <= 12 && max < 250;
        }

        private static byte MixChannel(byte from, byte to, float k)
            => (byte)Math.Clamp(from + (to - from) * k, 0f, 255f);

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

                // Табуляции-черты — под текстом строки: черта во всю высоту строки, и у
                // строк подряд она сливается в одну линию через абзац.
                DrawBarTabs(canvas, layout, paraX, lineY, line.Height);

                // Единый сдвиг строки по выравниванию (центр/право + абзацный отступ первой
                // строки по вордовской модели). Тот же расчёт у каретки/хит-теста/выделения.
                float lineShift = LineAlignShift(layout, i);

                // Растяжение по ширине: распределяем свободное место по межсловным пробелам.
                // У строки, разорванной обтекаемым объектом, каждый отрезок растягивается
                // сам по себе — своя добавка и свой накопленный сдвиг.
                int justifyFragment = 0;
                float extraPerSpace = JustifyExtraPerSpace(layout, i, justifyFragment);
                bool doJustify = extraPerSpace != 0f;
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
                        doJustify = extraPerSpace != 0f;
                        justifyShift = 0f;
                    }

                    // Скрытый текст не рисуется, и растяжка по его пробелам не копится:
                    // места в строке у него нет.
                    if (seg.IsHidden) continue;

                    // Перенос строки и невидимый мягкий перенос не рисуются.
                    if (seg.IsLineBreak || (seg.IsSoftHyphen && !seg.SoftHyphenShown)) continue;

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

                    // Прыжок табуляции сам по себе пуст: рисуется только заполнитель,
                    // если он назначен. Сам символ табуляции печатать нечем — в шрифтах
                    // у него нет рисунка.
                    if (seg.IsTabJump)
                    {
                        DrawTabLeader(canvas, seg, segX, baseY, paraX);
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
                    // Краска приводится к чтению здесь, в момент отрисовки: в раскладке
                    // она запеклась бы в кэш и правка цвета до текста не доходила бы.
                    SKColor textColor = ApplyReadingInk(seg.Color);
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

                    DrawSegmentGlyphs(canvas, seg, segX, segBaseY, font, paint);

                    // Ширина линий и рамки под сегментом: без хвостовых пробелов строки и
                    // с добавкой растяжки под растянутыми пробелами — иначе между словами
                    // зияли бы щели.
                    float decoWidth = SegHighlightWidth(line, segIdx, lastContentSeg);
                    if (doJustify && segSpaces > 0 && segIdx < lastContentSeg)
                        decoWidth += segSpaces * extraPerSpace;

                    if (seg.IsUnderline)
                        DrawSegmentUnderline(canvas, seg, segX, segBaseY, decoWidth, textColor, textShader);

                    DrawSegmentDecorations(canvas, line, segIdx, lastContentSeg, seg,
                        segX, segBaseY, decoWidth, font, textColor);

                    // Зачёркивание — на высоте из метрик шрифта, как в Word. Под
                    // растянутыми пробелами линия тянется на добавку растяжки, как и
                    // подчёркивание: иначе между зачёркнутыми словами оставались бы щели.
                    if (seg.IsStrikethrough || seg.IsDoubleStrikethrough)
                    {
                        float strikeWidth = SegHighlightWidth(line, segIdx, lastContentSeg);
                        if (doJustify && segSpaces > 0 && segIdx < lastContentSeg)
                            strikeWidth += segSpaces * extraPerSpace;
                        StrikePainter.Draw(canvas, seg.IsDoubleStrikethrough, segX, strikeWidth,
                            segBaseY, seg.FontSizePt, font, textColor, textShader);
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
        /// Позиция текста за номером Word, отделённым табуляцией: ближайшая справа от номера
        /// из отступа текста (он служит позицией табуляции, когда у абзаца выступ) и своих
        /// позиций табуляции абзаца; нет таких — следующая отметка шага табуляции от поля.
        /// Так Word ставит текст «1.» и «01.» на один отступ, а длинный номер «Один» или
        /// «118.» — на следующую отметку шага.
        /// </summary>
        private static double WordNumberTabStop(
            double markerEnd, double leftIndentPt, bool hanging,
            List<Models.Styles.TabStop>? tabStops, double defaultTabStopPt, float indentScale)
        {
            const double Epsilon = 0.01;

            // Допуск на округление ширины знаков: номер шириной ровно в выступ считается
            // дошедшим до отступа текста, а не перешедшим его.
            const double HangingReachTolerancePt = 0.05;

            double? best = null;

            // Номер, кончающийся ровно на отступе текста, текст с отступа не сдвигает:
            // «8.1.» шириной в выступ оставляет текст на отступе пункта, как у Word,
            // а не уводит его к следующей отметке шага табуляции.
            if (hanging && leftIndentPt >= markerEnd - HangingReachTolerancePt)
                best = leftIndentPt;

            if (tabStops is not null)
            {
                foreach (var stop in tabStops)
                {
                    double position = stop.PositionPt * indentScale;
                    if (position > markerEnd + Epsilon && (best is null || position < best))
                        best = position;
                }
            }

            if (best is double found) return found;

            double step = defaultTabStopPt * indentScale;
            if (step <= Epsilon) return markerEnd;

            return (Math.Floor((markerEnd + Epsilon) / step) + 1.0) * step;
        }

        /// <summary>
        /// Шрифт маркера уровня Word. Шрифт уровня есть в системе и знак в нём есть —
        /// маркер рисуется им; в символьном шрифте код меньше 0x100 — это знак U+F0xx, как
        /// читает его Word. Шрифта нет — знак из символьного шрифта заменяется юникодным
        /// двойником. Знака нет в шрифте текста — его ищут в юникодных шрифтах знаков.
        /// </summary>
        /// <param name="drawText">Текст, которым маркер рисуется выбранным шрифтом.</param>
        private static SKFont ResolveListMarkerFont(
            string markerText, string textFamily, float sizePt, string? levelFamily, out string drawText)
        {
            drawText = markerText;

            if (!string.IsNullOrWhiteSpace(levelFamily))
            {
                var levelFace = GetOrCreateTypeface(levelFamily, false, false);
                if (string.Equals(levelFace.FamilyName, levelFamily, StringComparison.OrdinalIgnoreCase))
                {
                    if (HasAllGlyphs(levelFace, markerText))
                        return GetOrCreateFont(levelFace, sizePt);

                    if (Services.SymbolFontMarkers.IsSymbolFont(levelFamily))
                    {
                        string shifted = ShiftToSymbolArea(markerText);
                        if (HasAllGlyphs(levelFace, shifted))
                        {
                            drawText = shifted;
                            return GetOrCreateFont(levelFace, sizePt);
                        }
                    }
                }

                if (Services.SymbolFontMarkers.ToUnicode(levelFamily, markerText) is { } unicode)
                    drawText = unicode;
            }

            var textFace = GetOrCreateTypeface(textFamily, false, false);
            int codepoint = drawText.Length > 0 ? drawText[0] : 0;
            if (codepoint >= 0x0080 && textFace.GetGlyph(codepoint) == 0
                && FindSymbolFallback(codepoint, textFamily, false, false) is { } fallback)
            {
                var fallbackFace = GetOrCreateTypeface(fallback.Family, fallback.Bold, fallback.Italic);
                return GetOrCreateFont(fallbackFace, sizePt);
            }

            return GetOrCreateFont(textFace, sizePt);
        }

        /// <summary>
        /// Шрифт текста, которым набирается номер списка: гарнитура и кегль первого
        /// фрагмента пункта с текстом — без уменьшения под индекс, без жирности и курсива.
        /// У пустого пункта — шрифт пустой строки абзаца.
        /// </summary>
        private static (string Family, float SizePt) ResolveMarkerTextFont(
            ParagraphBlock para, string? styleName, StyleResolver styles)
        {
            foreach (var chunk in para.Chunks)
            {
                foreach (var run in chunk.Runs)
                {
                    if (string.IsNullOrEmpty(run.Text) || run.InlineImageId is not null) continue;

                    var p = run.Properties;

                    string family = styles.ResolveFontFamily(styleName);
                    float size = styles.ResolveFontSize(styleName);

                    // Тот же порядок силы, что у текста (CollectTokens): стиль абзаца,
                    // поверх него символьный стиль, поверх обоих — свойства фрагмента.
                    if (!string.IsNullOrEmpty(p?.StyleName))
                    {
                        family = styles.FindFontFamily(p!.StyleName) ?? family;
                        size = styles.FindFontSize(p.StyleName) ?? size;
                    }

                    if (!string.IsNullOrEmpty(p?.FontFamily)) family = p!.FontFamily!;
                    if (p?.FontSize.HasValue == true) size = (float)p.FontSize.Value;

                    return (ResolveReadingFamily(family), ScaleReadingFont(size));
                }
            }

            var empty = BuildEmptyLineFormat(para, styleName, styles);
            return (empty.FontFamily, empty.FontSizePt);
        }

        /// <summary>
        /// Шрифт, которым номер списка и меряется при вёрстке, и рисуется. Расчёт один
        /// на оба места: иначе ширина номера в раскладке расходится с нарисованной.
        /// </summary>
        /// <param name="textFamily">Гарнитура текста пункта.</param>
        /// <param name="levelFamily">Шрифт уровня Word; null — номер набран шрифтом текста.</param>
        /// <param name="drawText">Текст, которым номер рисуется выбранным шрифтом.</param>
        private static SKFont ResolveMarkerDrawFont(
            string markerText, string textFamily, float sizePt, string? levelFamily, out string drawText)
        {
            drawText = markerText;

            var typeface = GetOrCreateTypeface(textFamily, false, false);
            var font = GetOrCreateFont(typeface, sizePt);

            // Маркер уровня Word со своим шрифтом (Symbol, Wingdings, Courier New) рисуется
            // этим шрифтом, как у Word; нет шрифта — юникодным двойником знака.
            if (levelFamily is not null)
            {
                font = ResolveListMarkerFont(markerText, textFamily, sizePt, levelFamily, out drawText);
                typeface = font.Typeface;
            }

            // Некоторые символы маркеров (например ➤) могут отсутствовать в основном шрифте —
            // подставляем системный фолбэк, иначе вместо маркера рисуется .notdef-квадрат.
            int mcp = drawText.Length > 0 ? drawText[0] : 0;
            if (mcp >= 0x0080 && typeface.GetGlyph(mcp) == 0)
            {
                if (!_fallbackFamilyCache.TryGetValue(mcp, out var fb))
                {
                    // Декоративные гарнитуры отсеиваются здесь же. Раньше фильтр
                    // доставался этому месту даром — через общий кеш с
                    // FindFallbackFamily, — а тот больше системный шрифт не ищет.
                    using var fm = SKFontManager.Default.MatchCharacter(mcp);
                    fb = fm != null && !IsDecorationFont(fm.FamilyName) ? fm.FamilyName : null;
                    _fallbackFamilyCache[mcp] = fb;
                }
                if (!string.IsNullOrEmpty(fb))
                {
                    typeface = GetOrCreateTypeface(fb!, false, false);
                    font = GetOrCreateFont(typeface, sizePt);
                }
                else if (FindSymbolFallback(mcp, textFamily, false, false) is { } symbolFallback)
                {
                    // Система отдала знак декоративному шрифту («★» — Segoe UI Symbol), и он
                    // отсеян. Знак ищется так же, как для текста: в юникодных шрифтах знаков.
                    typeface = GetOrCreateTypeface(symbolFallback.Family, symbolFallback.Bold, symbolFallback.Italic);
                    font = GetOrCreateFont(typeface, sizePt);
                }
            }

            return font;
        }

        /// <summary>В шрифте есть все знаки текста (пробелы не проверяются).</summary>
        private static bool HasAllGlyphs(SKTypeface typeface, string text)
        {
            if (text.Length == 0) return true;

            foreach (char c in text)
            {
                if (c == ' ') continue;
                if (typeface.GetGlyph(c) == 0) return false;
            }

            return true;
        }

        /// <summary>Коды символьного шрифта меньше 0x100 — в область U+F000, где шрифт их хранит.</summary>
        private static string ShiftToSymbolArea(string text)
        {
            var chars = text.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (chars[i] >= 0x20 && chars[i] < 0x100)
                    chars[i] = (char)(0xF000 + chars[i]);
            }

            return new string(chars);
        }

        /// <summary>
        /// Рисует маркер списка слева от текста первой строки абзаца.
        /// paraX — левый край текста (margin + отступ текста списка).
        /// markerHangingPt — выступ маркера: маркер рисуется на markerHangingPt левее текста.
        /// Гарнитура и кегль маркера — те, что выбрала вёрстка (шрифт текста пункта); у
        /// раскладки без этих данных — из первого сегмента строки, для пустого элемента —
        /// фолбэк-шрифт.
        /// </summary>
        private static void DrawListMarker(
            SKCanvas canvas, SKTextLayout layout,
            float paraX, float paraY, float yBase,
            string markerText, float markerHangingPt, SKColor markerColor,
            float markerMinGapPt)
        {
            if (layout.Lines.Count == 0) return;
            var line = layout.Lines[0];

            // Гарнитуру и кегль берём у первой строки, где есть текст. Когда номер занимает
            // первую строку один (MarkerOwnsFirstLine), сегментов в ней нет, и по прежнему
            // коду номер рисовался бы фолбэк-шрифтом — не тем, которым набран сам пункт.
            SKRunSegment? fontSource = null;
            foreach (var candidate in layout.Lines)
            {
                if (candidate.Segments.Count == 0) continue;
                fontSource = candidate.Segments[0];
                break;
            }

            string family = fontSource?.FontFamily ?? StyleResolver.FallbackFontFamily;
            float sizePt = fontSource?.FontSizePt ?? StyleResolver.FallbackFontSizePt;

            // Вёрстка уже выбрала шрифт текста для номера и по нему посчитала его ширину:
            // рисуется номер тем же шрифтом. Первый сегмент строки для этого не годится —
            // он бывает уменьшен под индекс или набран шрифтом подстановки.
            if (layout.MarkerTextFontSizePt > 0f && !string.IsNullOrEmpty(layout.MarkerTextFontFamily))
            {
                family = layout.MarkerTextFontFamily!;
                sizePt = layout.MarkerTextFontSizePt;
            }

            var font = ResolveMarkerDrawFont(markerText, family, sizePt, layout.MarkerFontFamily, out markerText);

            float lineY = paraY + (line.Y - yBase);
            float baseY = lineY + line.Baseline;
            // Цифра/символ маркера рисуется строго по своему левому краю (там же, где стрелка на
            // линейке). Зазор до текста обеспечивает отступ первой строки, вычисленный в раскладке
            // по ширине цифры, — поэтому здесь маркер не сдвигаем.
            float markerX = paraX - markerHangingPt;

            // Номер Word, выровненный по правому краю или по центру, кончается или стоит
            // серединой в точке номера: «i.», «ii.», «iii.» — по правому краю.
            if (layout.MarkerAlignment == 2)
                markerX -= font.MeasureText(markerText);
            else if (layout.MarkerAlignment == 1)
                markerX -= font.MeasureText(markerText) / 2f;

            using var paint = new SKPaint
            {
                Color = markerColor != default
                    ? markerColor
                    : (ReadingMarkerColorOverride ?? SKColors.Black),
                IsAntialias = true
            };
            canvas.DrawText(markerText, markerX, baseY, font, paint);
        }
    }
}