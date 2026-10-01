using System;
using System.Collections.Generic;
using SkiaSharp;

namespace Writersword.Core.Models.Rendering
{
    /// <summary>
    /// Результат вёрстки одного параграфа.
    /// Хранит строки, метрики и предоставляет методы HitTest для
    /// посимвольного позиционирования каретки и выделения.
    /// Координаты в points (pt) относительно начала текстовой области страницы.
    /// Пересчитывается при изменении текста или ширины текстовой области.
    /// </summary>
    public sealed class SKTextLayout
    {
        // ── Метрики параграфа ─────────────────────────────────────────────

        /// <summary>Строки параграфа в порядке следования сверху вниз.</summary>
        public List<SKLineLayout> Lines { get; } = new();

        /// <summary>Суммарная высота всех строк параграфа в pt.</summary>
        public float TotalHeightPt { get; set; }

        /// <summary>Интервал до параграфа в pt.</summary>
        public float SpaceBeforePt { get; set; }

        /// <summary>
        /// Строки абзаца, выровненного по ширине, набраны со сжатием пробелов: в строку
        /// могло войти слово, для которого нужно сжать пробелы до пятой части. Такая
        /// строка шире области, и выравнивание её сжимает, а не растягивает.
        /// </summary>
        public bool AllowsJustifyShrink { get; set; }

        /// <summary>Интервал после параграфа в pt.</summary>
        public float SpaceAfterPt { get; set; }

        /// <summary>
        /// Интервал до абзаца снят при раскладке: абзац не добавляет интервал к соседу
        /// своего стиля выше. Запоминается, чтобы раскладку из кеша можно было сверить с
        /// соседством: сменился стиль соседа — снятие меняется, и раскладка уже не та.
        /// </summary>
        public bool SpaceBeforeSuppressed { get; set; }

        /// <summary>Интервал после абзаца снят при раскладке: ниже сосед того же стиля.</summary>
        public bool SpaceAfterSuppressed { get; set; }

        /// <summary>Левый отступ параграфа в pt.</summary>
        public float LeftIndentPt { get; set; }

        /// <summary>Правый отступ параграфа в pt.</summary>
        public float RightIndentPt { get; set; }

        /// <summary>Отступ первой строки в pt.</summary>
        public float FirstLineIndentPt { get; set; }

        /// <summary>
        /// Первую строку занимает только маркер списка, текста в ней нет — он начинается
        /// со второй строки по левому отступу. Ставится вёрсткой, когда справа от номера
        /// не осталось места под текст (узкая ячейка, крупный кегль, номер утащен вправо):
        /// иначе строка налезала бы на номер. Такая строка не содержит сегментов и её
        /// диапазон символов пуст (LastCharIndex = FirstCharIndex - 1), поэтому каретка,
        /// выделение и навигация её пропускают.
        /// </summary>
        public bool MarkerOwnsFirstLine { get; set; }

        /// <summary>
        /// Выравнивание номера списка у точки номера: 0 — номер начинается в ней, 1 — стоит
        /// серединой, 2 — кончается (w:lvlJc у списков из Word). Ставится вёрсткой.
        /// </summary>
        public int MarkerAlignment { get; set; }

        /// <summary>
        /// Шрифт маркера списка, заданный уровнем Word (Symbol, Wingdings, Courier New).
        /// null — маркер рисуется шрифтом текста пункта. Ставится вёрсткой.
        /// </summary>
        public string? MarkerFontFamily { get; set; }

        /// <summary>
        /// Первая строка вытеснена под обтекаемый объект (полоса рядом с ним оказалась
        /// уже самого длинного слова абзаца). Канвас запоминает это значение и передаёт
        /// в следующую пересборку — на нём построен гистерезис: чтобы вернуться сбоку от
        /// объекта, полосе нужно стать заметно шире, чем требовалось для ухода вниз.
        /// Без гистерезиса абзац дребезжал между двумя состояниями при перетаскивании.
        /// </summary>
        public bool WrapPushedDown { get; set; }

        /// <summary>
        /// Ширина текстовой области строки в pt (без LeftIndentPt и RightIndentPt).
        /// Устанавливается в SKTextRenderer.WrapTokensToLines и используется
        /// в ComputeAlignmentOffset для корректного выравнивания по центру и правому краю.
        /// Было: ComputeAlignmentOffset использовал RightIndentPt + LeftIndentPt —
        /// это сумма отступов, а не ширина области, что ломало Center и Right выравнивание.
        /// </summary>
        public float TextAreaWidthPt { get; set; }

        /// <summary>Выравнивание текста в параграфе.</summary>
        public TextAlignment Alignment { get; set; }

        /// <summary>
        /// Рамка абзаца. null — рамки нет. Её верхняя и нижняя линии вместе с зазором
        /// до текста уже учтены в <see cref="SpaceBeforePt"/> и <see cref="SpaceAfterPt"/>:
        /// рамка отодвигает соседей так же, как интервалы, и разбивка на страницы,
        /// попадание мышью и каретка считают её без особых случаев.
        /// </summary>
        public SKParagraphBorders? Borders { get; set; }

        /// <summary>
        /// Заливка абзаца (HEX). null — без заливки. Рисуется вместе с рамкой, под
        /// текстом: под строками, а при рамке — всё поле внутри неё.
        /// </summary>
        public string? ShadingColor { get; set; }

        /// <summary>
        /// Узор заливки поверх цвета заливки — имя узора Word (pct25, diagStripe…).
        /// null — узора нет.
        /// </summary>
        public string? ShadingPattern { get; set; }

        /// <summary>Цвет узора (HEX). null — «авто»: цвет текста листа.</summary>
        public string? ShadingPatternColor { get; set; }

        /// <summary>
        /// Табуляции-черты абзаца: позиции вертикальных линий в pt от левого края
        /// текстовой области (там же, откуда отсчитываются позиции табуляции). Черта
        /// идёт через каждую строку. null — черт нет.
        /// </summary>
        public IReadOnlyList<float>? BarTabPositionsPt { get; set; }

        /// <summary>
        /// Как знаки разного кегля встают по высоте строки: 0 — авто, 1 — по базовой
        /// линии, 2 — по верху строки, 3 — по середине, 4 — по низу. Числа — те же, что
        /// у выравнивания w:textAlignment в модели документа.
        /// </summary>
        public int LineTextAlignment { get; set; }

        /// <summary>Абзац набран справа налево (w:bidi): его начало — у правого края.</summary>
        public bool IsRightToLeft { get; set; }

        /// <summary>
        /// В абзаце есть текст справа налево или сам абзац справа налево: строки
        /// раскладываются по направлению письма.
        /// </summary>
        public bool HasBidiText { get; set; }

        /// <summary>У абзаца есть заливка: цвет, узор или то и другое.</summary>
        public bool HasShading =>
            !string.IsNullOrWhiteSpace(ShadingColor) || !string.IsNullOrWhiteSpace(ShadingPattern);

        /// <summary>
        /// Суммарная высота параграфа включая интервалы до и после.
        /// Используется при разбивке на страницы.
        /// </summary>
        public float BlockHeightPt => SpaceBeforePt + TotalHeightPt + SpaceAfterPt;

        /// <summary>
        /// Длина текста параграфа в символах.
        /// Используется для проверки границ каретки.
        /// </summary>
        public int TextLength { get; set; }

        // ── HitTest ───────────────────────────────────────────────────────

        /// <summary>
        /// Определяет позицию каретки по точке клика.
        /// Точка задаётся в pt относительно начала параграфа (Y=0 — верх первой строки).
        /// Использует посимвольные метрики глифов — точность до символа.
        /// Если точка выше первой строки — возвращает позицию 0.
        /// Если точка ниже последней строки — возвращает позицию конца текста.
        /// </summary>
        /// <param name="xPt">X в pt относительно левого края текстовой области параграфа.</param>
        /// <param name="yPt">Y в pt относительно верхнего края параграфа.</param>
        public SKHitTestResult HitTestPoint(float xPt, float yPt)
        {
            if (Lines.Count == 0)
                return new SKHitTestResult { CharIndex = 0, IsInside = false };

            SKLineLayout targetLine = Lines[0];

            foreach (var line in Lines)
            {
                targetLine = line;
                if (yPt <= line.Y + line.Height) break;
            }

            // Строка, занятая маркером списка, текста не содержит. Клик по ней — это клик
            // в начало пункта, поэтому отдаём его первой текстовой строке: иначе каретка
            // вставала бы рядом с номером, а печать шла бы строкой ниже.
            if (MarkerOwnsFirstLine && Lines.Count > 1 && ReferenceEquals(targetLine, Lines[0]))
                targetLine = Lines[1];

            return HitTestLinePoint(targetLine, xPt);
        }

        /// <summary>
        /// Возвращает прямоугольник каретки для заданной позиции символа.
        /// Позиция 0 — перед первым символом.
        /// Позиция TextLength — после последнего символа.
        /// Координаты в pt относительно начала параграфа.
        /// </summary>
        /// <param name="charIndex">Индекс символа в PlainText параграфа.</param>
        public SKCaretRect HitTestPosition(int charIndex)
        {
            charIndex = Math.Clamp(charIndex, 0, TextLength);

            if (Lines.Count == 0)
                return new SKCaretRect { X = LeftIndentPt + FirstLineIndentPt, Y = 0, Height = 14f, Baseline = 11f };

            int lineIdx = 0;
            foreach (var line in Lines)
            {
                if (charIndex > line.LastCharIndex && !line.IsLastLine) { lineIdx++; continue; }

                float x = GetCaretXInLine(line, charIndex);
                float lineExtra = (lineIdx == 0) ? FirstLineIndentPt : 0f;

                var (caretY, caretH) = GetCaretBox(line);
                return new SKCaretRect
                {
                    X = LeftIndentPt + lineExtra + x,
                    Y = caretY,
                    Height = caretH,
                    Baseline = line.Baseline
                };
            }

            var lastLine = Lines[^1];
            float lastX = GetCaretXInLine(lastLine, charIndex);
            float lastLineExtra = (Lines.Count == 1) ? FirstLineIndentPt : 0f;

            var (lastCaretY, lastCaretH) = GetCaretBox(lastLine);
            return new SKCaretRect
            {
                X = LeftIndentPt + lastLineExtra + lastX,
                Y = lastCaretY,
                Height = lastCaretH,
                Baseline = lastLine.Baseline
            };
        }

        /// <summary>
        /// Вертикаль каретки в строке: верх и высота, в pt относительно начала параграфа.
        ///
        /// Обычную строку каретка перекрывает целиком. Но строку может растянуть
        /// встроенная картинка — тогда каретка во всю строку выглядела бы огромной рядом
        /// с текстом своего кегля. В такой строке каретка рисуется по метрикам текста
        /// и сидит на той же базовой линии, что и буквы.
        /// </summary>
        private static (float Y, float Height) GetCaretBox(SKLineLayout line)
        {
            float textHeight = line.TextAscentPt + line.TextDescentPt;
            if (textHeight <= 0f || textHeight >= line.Height)
                return (line.Y, line.Height);

            float top = line.Y + line.Baseline - line.TextAscentPt;
            return (top, textHeight);
        }

        /// <summary>
        /// Возвращает список прямоугольников выделения для диапазона символов.
        /// Диапазон [from, to) — от включительно, до не включительно.
        /// Каждая строка даёт отдельный прямоугольник.
        /// Координаты в pt относительно начала параграфа.
        /// </summary>
        /// <param name="from">Начало диапазона (включительно).</param>
        /// <param name="to">Конец диапазона (не включительно).</param>
        public List<SKSelectionRect> HitTestRange(int from, int to)
        {
            var result = new List<SKSelectionRect>();
            if (from >= to || Lines.Count == 0) return result;

            from = Math.Clamp(from, 0, TextLength);
            to = Math.Clamp(to, 0, TextLength);

            for (int i = 0; i < Lines.Count; i++)
            {
                var line = Lines[i];

                int lineFrom = Math.Max(from, line.FirstCharIndex);
                int lineTo = Math.Min(to, line.LastCharIndex + 1);

                if (lineFrom >= lineTo) continue;

                // Строка с кусками, переставленными по направлению письма: знаки подряд
                // по тексту стоят на листе в разных местах. Прямоугольник от края до края
                // накрыл бы и невыделенные куски между ними — выделение собирается из
                // мест самих выделенных знаков.
                if (line.IsBidiReordered && !line.HasWrapFragments)
                {
                    float reorderedExtra = (i == 0) ? FirstLineIndentPt : 0f;
                    foreach (var (spanLeft, spanRight) in ReorderedSelectionSpans(line, lineFrom, lineTo))
                    {
                        result.Add(new SKSelectionRect
                        {
                            Rect = new SKRect(
                                LeftIndentPt + reorderedExtra + spanLeft, line.Y,
                                LeftIndentPt + reorderedExtra + spanRight, line.Y + line.Height),
                            LineIndex = i
                        });
                    }
                    continue;
                }

                float x1 = GetCaretXInLine(line, lineFrom);
                float x2 = GetCaretXInLine(line, lineTo);

                if (x2 < x1) (x2, x1) = (x1, x2);

                float lineExtra = (i == 0) ? FirstLineIndentPt : 0f;

                float left = LeftIndentPt + lineExtra + x1;
                float right = LeftIndentPt + lineExtra + x2;

                // Строка, разорванная обтекаемым объектом, идёт по нескольким отрезкам,
                // и её X включает прыжок через объект. Один прямоугольник от начала до
                // конца накрыл бы и картинку — режем выделение по отрезкам строки.
                if (line.HasWrapFragments)
                {
                    float originPt = line.WrapFragments[0].LeftPt;
                    for (int f = 0; f < line.WrapFragments.Count; f++)
                    {
                        var fragment = line.WrapFragments[f];
                        float fragLeft = LeftIndentPt + (fragment.LeftPt - originPt);
                        float fragRight = fragLeft + fragment.WidthPt;

                        float clippedLeft = Math.Max(left, fragLeft);
                        float clippedRight = Math.Min(right, fragRight);
                        if (clippedRight <= clippedLeft) continue;

                        result.Add(new SKSelectionRect
                        {
                            Rect = new SKRect(clippedLeft, line.Y, clippedRight, line.Y + line.Height),
                            LineIndex = i,
                            FragmentIndex = f
                        });
                    }
                    continue;
                }

                result.Add(new SKSelectionRect
                {
                    Rect = new SKRect(left, line.Y, right, line.Y + line.Height),
                    LineIndex = i
                });
            }

            return result;
        }

        /// <summary>
        /// Возвращает индекс строки содержащей заданный символ.
        /// Используется для навигации стрелками вверх/вниз.
        /// </summary>
        public int GetLineIndexForChar(int charIndex)
        {
            for (int i = 0; i < Lines.Count; i++)
            {
                var line = Lines[i];
                if (charIndex <= line.LastCharIndex || line.IsLastLine)
                    return i;
            }
            return Lines.Count - 1;
        }

        /// <summary>
        /// Возвращает позицию каретки в строке выше/ниже текущей
        /// с сохранением X-позиции каретки (как в Word).
        /// </summary>
        /// <param name="charIndex">Текущая позиция каретки.</param>
        /// <param name="direction">-1 — вверх, +1 — вниз.</param>
        /// <param name="preferredX">Желаемая X-позиция в pt (сохраняется при навигации вверх/вниз).</param>
        public int GetCharIndexForVerticalMove(int charIndex, int direction, float preferredX)
        {
            int lineIdx = GetLineIndexForChar(charIndex);
            int targetIdx = lineIdx + direction;

            if (targetIdx < 0 || targetIdx >= Lines.Count)
                return charIndex;

            var targetLine = Lines[targetIdx];
            var result = HitTestLinePoint(targetLine, preferredX - LeftIndentPt);
            return result.CharIndex;
        }

        // ── Вспомогательные методы ────────────────────────────────────────

        private static SKHitTestResult HitTestLinePoint(SKLineLayout line, float xPt)
        {
            if (line.Segments.Count == 0)
                return new SKHitTestResult
                {
                    CharIndex = line.FirstCharIndex,
                    IsInside = false,
                    IsTrailingEdge = false
                };

            // Правый край строки — по последнему сегменту, а НЕ по сумме ширин: строка,
            // разорванная обтекаемым объектом, содержит прыжок через него, и сумма ширин
            // сегментов заметно меньше её реального размаха. По сумме любой клик правее
            // картинки считался кликом за концом строки — каретка прыгала в край.
            float totalWidth = 0f;
            foreach (var seg in line.Segments)
            {
                float segRight = seg.X + seg.Width;
                if (segRight > totalWidth) totalWidth = segRight;
            }

            // Строка справа налево: левый край — её конец, правый — начало.
            if (line.IsRightToLeft)
            {
                if (xPt <= 0)
                    return new SKHitTestResult
                    {
                        CharIndex = line.LastCharIndex + 1,
                        IsInside = false,
                        IsTrailingEdge = true
                    };

                if (xPt >= totalWidth)
                    return new SKHitTestResult
                    {
                        CharIndex = line.FirstCharIndex,
                        IsInside = false,
                        IsTrailingEdge = false
                    };
            }

            if (xPt <= 0)
                return new SKHitTestResult
                {
                    CharIndex = line.FirstCharIndex,
                    IsInside = false,
                    IsTrailingEdge = false
                };

            // Строка кончается переносом строки (Shift+Enter): клик правее её текста
            // ставит каретку перед переносом, как в Word. Позиция за переносом — уже
            // начало следующей строки, и каретка прыгала бы вниз.
            var lastSeg = line.Segments[^1];
            if (lastSeg.IsLineBreak && !line.IsBidiReordered && xPt >= lastSeg.X)
                return new SKHitTestResult
                {
                    CharIndex = lastSeg.GlobalCharOffset,
                    IsInside = false,
                    IsTrailingEdge = true
                };

            // Хвостовые пробелы на переносимой (не последней) строке — висячие: клик правее
            // последнего слова ставит каретку в конец содержимого строки, а не на LastCharIndex+1
            // (который для переносимой строки уже является первым символом следующей строки, из-за
            // чего каретка «прыгает вниз»).
            if (!line.IsLastLine && !line.IsBidiReordered)
            {
                int contentEnd = LastNonSpaceEnd(line);
                if (contentEnd <= line.LastCharIndex)
                {
                    float contentRight = GetCaretXInLine(line, contentEnd);
                    if (xPt >= contentRight)
                        return new SKHitTestResult
                        {
                            CharIndex = contentEnd,
                            IsInside = false,
                            IsTrailingEdge = true
                        };
                }
            }

            if (xPt >= totalWidth)
                return new SKHitTestResult
                {
                    CharIndex = line.LastCharIndex + 1,
                    IsInside = false,
                    IsTrailingEdge = true
                };

            foreach (var seg in line.Segments)
            {
                if (xPt < seg.X || xPt > seg.X + seg.Width) continue;

                float localX = xPt - seg.X;

                // Сегмент справа налево: знак начинается у своего правого края, поэтому
                // правая половина знака — место перед ним, левая — после.
                if (seg.IsRightToLeft)
                {
                    foreach (var glyph in seg.GlyphMetrics)
                    {
                        if (localX >= glyph.MidX)
                            return new SKHitTestResult
                            {
                                CharIndex = glyph.CharIndex,
                                IsInside = true,
                                IsTrailingEdge = false
                            };

                        if (localX >= glyph.X)
                            return new SKHitTestResult
                            {
                                CharIndex = glyph.CharIndex + 1,
                                IsInside = true,
                                IsTrailingEdge = false
                            };
                    }

                    continue;
                }

                foreach (var glyph in seg.GlyphMetrics)
                {
                    if (localX <= glyph.MidX)
                        return new SKHitTestResult
                        {
                            CharIndex = glyph.CharIndex,
                            IsInside = true,
                            IsTrailingEdge = false
                        };

                    if (localX <= glyph.Right)
                        return new SKHitTestResult
                        {
                            CharIndex = glyph.CharIndex + 1,
                            IsInside = true,
                            IsTrailingEdge = false
                        };
                }
            }

            // Точка не попала ни в один сегмент — это промежуток между отрезками строки,
            // то есть клик по самому обтекаемому объекту. Каретка встаёт к ближайшему
            // краю текста: слева от объекта — в конец левого куска, справа — в начало правого.
            float bestDistance = float.MaxValue;
            var nearest = new SKHitTestResult
            {
                CharIndex = line.LastCharIndex + 1,
                IsInside = false,
                IsTrailingEdge = true
            };

            foreach (var seg in line.Segments)
            {
                float segLeft = seg.X;
                float segRight = seg.X + seg.Width;

                float distLeft = Math.Abs(xPt - segLeft);
                if (distLeft < bestDistance)
                {
                    bestDistance = distLeft;
                    nearest = new SKHitTestResult
                    {
                        CharIndex = seg.GlobalCharOffset,
                        IsInside = false,
                        IsTrailingEdge = false
                    };
                }

                float distRight = Math.Abs(xPt - segRight);
                if (distRight < bestDistance)
                {
                    bestDistance = distRight;
                    nearest = new SKHitTestResult
                    {
                        CharIndex = seg.GlobalCharOffset + seg.Text.Length,
                        IsInside = false,
                        IsTrailingEdge = true
                    };
                }
            }

            return nearest;
        }

        /// <summary>
        /// Участки выделения [from, to) в строке с переставленными кусками, слева
        /// направо, в координатах строки. Каждый знак даёт своё место; места, стоящие
        /// вплотную, сливаются в один участок.
        /// </summary>
        private static List<(float Left, float Right)> ReorderedSelectionSpans(
            SKLineLayout line, int from, int to)
        {
            var spans = new List<(float Left, float Right)>();

            foreach (var seg in line.Segments)
            {
                int segFrom = seg.GlobalCharOffset;
                int segTo = segFrom + seg.Text.Length;
                int a = Math.Max(from, segFrom);
                int b = Math.Min(to, segTo);
                if (a >= b) continue;

                if (seg.GlyphMetrics.Length != seg.Text.Length)
                {
                    if (seg.Width > 0f) spans.Add((seg.X, seg.X + seg.Width));
                    continue;
                }

                for (int k = a - segFrom; k < b - segFrom; k++)
                {
                    var glyph = seg.GlyphMetrics[k];
                    if (glyph.Width <= 0f) continue;
                    spans.Add((seg.X + glyph.X, seg.X + glyph.Right));
                }
            }

            if (spans.Count < 2) return spans;

            spans.Sort((p, q) => p.Left.CompareTo(q.Left));

            var merged = new List<(float Left, float Right)>(spans.Count) { spans[0] };
            for (int s = 1; s < spans.Count; s++)
            {
                var last = merged[^1];
                var span = spans[s];
                if (span.Left <= last.Right + 0.01f)
                    merged[^1] = (last.Left, Math.Max(last.Right, span.Right));
                else
                    merged.Add(span);
            }

            return merged;
        }

        // Глобальный индекс сразу за последним непробельным символом строки.
        // Если непробельных нет — возвращает FirstCharIndex.
        private static int LastNonSpaceEnd(SKLineLayout line)
        {
            int last = line.FirstCharIndex;
            foreach (var seg in line.Segments)
                for (int k = 0; k < seg.Text.Length; k++)
                {
                    char c = seg.Text[k];
                    if (c != ' ' && c != '\t') last = seg.GlobalCharOffset + k + 1;
                }
            return last;
        }

        private static float GetCaretXInLine(SKLineLayout line, int charIndex)
        {
            foreach (var seg in line.Segments)
            {
                if (charIndex < seg.GlobalCharOffset) break;
                if (charIndex > seg.GlobalCharOffset + seg.GlyphMetrics.Length) continue;

                int localIndex = charIndex - seg.GlobalCharOffset;

                // Сегмент справа налево: его начало — у правого края, и место после
                // знака — у левого края этого знака.
                if (seg.IsRightToLeft)
                {
                    if (localIndex == 0) return seg.X + seg.Width;

                    if (localIndex >= seg.GlyphMetrics.Length)
                        return seg.X;

                    return seg.X + seg.GlyphMetrics[localIndex - 1].X;
                }

                if (localIndex == 0) return seg.X;

                if (localIndex >= seg.GlyphMetrics.Length)
                    return seg.X + seg.Width;

                return seg.X + seg.GlyphMetrics[localIndex - 1].Right;
            }

            if (line.Segments.Count == 0) return 0f;

            var lastSeg = line.Segments[^1];
            return lastSeg.X + lastSeg.Width;
        }
    }
}