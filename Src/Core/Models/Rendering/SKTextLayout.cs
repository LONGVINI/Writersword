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

        /// <summary>Интервал после параграфа в pt.</summary>
        public float SpaceAfterPt { get; set; }

        /// <summary>Левый отступ параграфа в pt.</summary>
        public float LeftIndentPt { get; set; }

        /// <summary>Правый отступ параграфа в pt.</summary>
        public float RightIndentPt { get; set; }

        /// <summary>Отступ первой строки в pt.</summary>
        public float FirstLineIndentPt { get; set; }

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

                return new SKCaretRect
                {
                    X = LeftIndentPt + lineExtra + x,
                    Y = line.Y,
                    Height = line.Height,
                    Baseline = line.Baseline
                };
            }

            var lastLine = Lines[^1];
            float lastX = GetCaretXInLine(lastLine, charIndex);
            float lastLineExtra = (Lines.Count == 1) ? FirstLineIndentPt : 0f;

            return new SKCaretRect
            {
                X = LeftIndentPt + lastLineExtra + lastX,
                Y = lastLine.Y,
                Height = lastLine.Height,
                Baseline = lastLine.Baseline
            };
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

                float x1 = GetCaretXInLine(line, lineFrom);
                float x2 = GetCaretXInLine(line, lineTo);

                if (x2 < x1) (x2, x1) = (x1, x2);

                float lineExtra = (i == 0) ? FirstLineIndentPt : 0f;

                result.Add(new SKSelectionRect
                {
                    Rect = new SKRect(LeftIndentPt + lineExtra + x1, line.Y,
                                           LeftIndentPt + lineExtra + x2, line.Y + line.Height),
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

            float totalWidth = 0f;
            foreach (var seg in line.Segments) totalWidth += seg.Width;

            if (xPt <= 0)
                return new SKHitTestResult
                {
                    CharIndex = line.FirstCharIndex,
                    IsInside = false,
                    IsTrailingEdge = false
                };

            // Хвостовые пробелы на переносимой (не последней) строке — висячие: клик правее
            // последнего слова ставит каретку в конец содержимого строки, а не на LastCharIndex+1
            // (который для переносимой строки уже является первым символом следующей строки, из-за
            // чего каретка «прыгает вниз»).
            if (!line.IsLastLine)
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

            return new SKHitTestResult
            {
                CharIndex = line.LastCharIndex + 1,
                IsInside = false,
                IsTrailingEdge = true
            };
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