using System;
using System.Collections.Generic;
using SkiaSharp;
using Writersword.Core.Models.Rendering;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Непечатаемые знаки — как кнопка «¶» в Word: конец абзаца, пробелы, табуляции,
    /// неразрывные пробелы, переносы строки внутри абзаца, явные разрывы страницы и
    /// отметка у абзацев, которые держатся за следующий или начинают новую страницу.
    ///
    /// Знаки рисуются поверх текста тем же проходом, что и сам текст, и попадают в
    /// снимок кадра: включение и выключение пересобирает кадр целиком. На печать и
    /// в чтение они не идут — только в правку.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        /// <summary>
        /// Отметка явного разрыва страницы: лист и высота, где он стоит. Tail — абзац,
        /// внутри которого разрыв стоит у Word: отметка рисуется на его последней строке
        /// (null — отдельной строкой под текстом листа). ContinuesParagraph — за разрывом
        /// идёт текст того же абзаца, IsColumn — разрыв колонки, работающий как разрыв
        /// страницы.
        /// </summary>
        private readonly record struct PageBreakMark(
            int PageIndex,
            float Ypt,
            ParagraphBlock? Tail = null,
            bool ContinuesParagraph = false,
            bool IsColumn = false);

        // Подписи отметок разрыва — как у Word.
        private const string PageBreakLabel = "Разрыв страницы";
        private const string ColumnBreakLabel = "Разрыв столбца";

        // Кегль подписи отметки разрыва.
        private const float BreakLabelSizePt = 7.5f;

        // Разрывы страницы последней раскладки. Меняются вместе с остальной раскладкой
        // под замком отрисовки (PublishPassResults).
        private List<PageBreakMark> _breakMarks = new();
        private List<PageBreakMark> _passBreakMarks = new();

        // Показ непечатаемых знаков. Читается при отрисовке, пишется с UI-потока.
        private volatile bool _showFormattingMarks;

        /// <summary>Знаки рисуются только в правке листов: не в чтении и не в книге.</summary>
        private bool FormattingMarksVisible => _showFormattingMarks && !ReadingActive && !SpreadMode && !ExportPassActive;

        // Цвет знаков — приглушённый синий, как в Word: виден и на белом листе, и на
        // тёмном, и не спорит с текстом.
        private static readonly SKColor FormattingMarkColor = new(0x4F, 0x81, 0xBD, 0xE0);

        // Знак конца ячейки таблицы — как в Word, вместо знака абзаца у последнего
        // абзаца ячейки.
        private const string ParagraphMarkGlyph = "\u00B6";
        private const string CellEndMarkGlyph = "\u00A4";

        private static readonly SKTypeface FormattingMarkTypeface =
            SKTypeface.FromFamilyName("Arial") ?? SKTypeface.Default;

        /// <summary>Показ знаков включили или выключили во вью-модели.</summary>
        private void OnFormattingMarksChanged()
        {
            bool show = DocVm?.ShowFormattingMarks ?? false;
            if (_showFormattingMarks == show) return;

            _showFormattingMarks = show;

            // Скрытый текст показывается вместе с непечатаемыми знаками и занимает место
            // в строке, а без них — не занимает (DocumentCanvas.HiddenText).
            if (ApplyHiddenTextRule())
            {
                RebuildLayouts();
                InvalidateMeasure();

                // Каретка не должна остаться внутри спрятанного текста.
                NormalizeCaretOutOfHiddenText();
            }

            InvalidateFull();
        }

        /// <summary>
        /// Знаки одного куска абзаца. Координаты — те же, что у каретки: x отсчитывается
        /// от левого края текстовой зоны, y — от верха куска на листе.
        /// </summary>
        private void DrawFormattingMarks(
            SKCanvas canvas, ParaLayout pl, SKTextLayout layout, float xPt, float yPt)
        {
            string text = pl.Vm?.PlainText ?? string.Empty;

            int lineFrom = Math.Max(pl.LineFrom, 0);
            int lineTo = Math.Min(pl.LineTo, layout.Lines.Count);
            bool sliceHasLastLine = lineTo >= layout.Lines.Count;

            float onePx = OnePixelPt(canvas);

            using var fill = new SKPaint
            {
                Color = FormattingMarkColor,
                IsAntialias = true,
                Style = SKPaintStyle.Fill
            };
            using var stroke = new SKPaint
            {
                Color = FormattingMarkColor,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = Math.Max(onePx, 0.6f),
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round
            };

            // Отметка абзаца со свойствами страницы: маленький квадрат слева от первой
            // строки, как у Word. Только у обычного абзаца и только у его начала.
            if (pl.Cell is null && lineFrom == 0 && pl.Vm?.Model is { } model
                && (model.Properties.KeepWithNext
                    || model.Properties.KeepTogether
                    || model.Properties.PageBreakBefore))
            {
                float firstLineMid = FirstLineMidY(layout, yPt);
                float side = 3.5f;
                canvas.DrawRect(
                    SKRect.Create(xPt - 8f, firstLineMid - side / 2f, side, side),
                    fill);
            }

            // Разрыв внутри этого абзаца (у Word): вместо знака абзаца на последней строке
            // рисуется отметка разрыва.
            PageBreakMark? inlineBreak = sliceHasLastLine && pl.Cell is null && pl.Vm?.Model is { } tailModel
                ? FindInlineBreakMark(tailModel)
                : null;

            if (layout.Lines.Count == 0 || text.Length == 0)
            {
                if (inlineBreak is { } emptyBreak)
                    DrawInlineBreakMark(canvas, pl, layout, xPt, yPt, text, emptyBreak, fill);
                else if (sliceHasLastLine)
                    DrawParagraphEndMark(canvas, pl, layout, xPt, yPt, text, fill);
                return;
            }

            float yBase = lineFrom < layout.Lines.Count ? layout.Lines[lineFrom].Y : 0f;

            for (int li = lineFrom; li < lineTo; li++)
            {
                var line = layout.Lines[li];
                if (line.LastCharIndex < line.FirstCharIndex) continue;

                float baseline = yPt + (line.Y - yBase) + line.Baseline;
                float size = MarkSize(line);
                float midY = baseline - size * 0.32f;

                int last = Math.Min(line.LastCharIndex, text.Length - 1);
                for (int c = Math.Max(line.FirstCharIndex, 0); c <= last; c++)
                {
                    char ch = text[c];
                    if (ch != ' ' && ch != '\t' && ch != '\u00A0'
                        && ch != '\n' && ch != '\u000B' && ch != '\u2028')
                        continue;

                    float left;
                    float right;

                    // Строка с кусками, переставленными по направлению письма: соседние
                    // по тексту знаки стоят на листе в разных местах, и середина между
                    // кареткой перед знаком и кареткой после него — не середина знака.
                    // Место знака берётся из раскладки напрямую.
                    if (line.IsBidiReordered && layout.TryGetCharBox(li, c, out float boxLeft, out float boxRight))
                    {
                        float lineOrigin = xPt + LineAlignShift(layout, li)
                            - (li == 0 ? layout.FirstLineIndentPt : 0f);
                        left = lineOrigin + boxLeft;
                        right = lineOrigin + boxRight;
                    }
                    else
                    {
                        left = CharScreenX(layout, xPt, li, c);
                        right = c < line.LastCharIndex
                            ? CharScreenX(layout, xPt, li, c + 1)
                            : left + size * 0.3f;
                        if (right < left) right = left;
                    }

                    switch (ch)
                    {
                        case ' ':
                            // Точка посередине пробела, на высоте середины строчных букв.
                            canvas.DrawCircle((left + right) / 2f, midY, Math.Max(size * 0.07f, onePx), fill);
                            break;

                        case '\u00A0':
                            // Неразрывный пробел — кружок, как градус у Word.
                            canvas.DrawCircle((left + right) / 2f, baseline - size * 0.55f, Math.Max(size * 0.12f, onePx * 1.5f), stroke);
                            break;

                        case '\t':
                            DrawTabArrow(canvas, left, right, midY, size, stroke);
                            break;

                        default:
                            // Перенос строки внутри абзаца (Shift+Enter).
                            DrawLineBreakArrow(canvas, left + size * 0.15f, baseline, size, stroke);
                            break;
                    }
                }
            }

            if (inlineBreak is { } textBreak)
                DrawInlineBreakMark(canvas, pl, layout, xPt, yPt, text, textBreak, fill);
            else if (sliceHasLastLine)
                DrawParagraphEndMark(canvas, pl, layout, xPt, yPt, text, fill);
        }

        /// <summary>
        /// Отметка разрыва, который у Word стоит внутри абзаца tail. Отметок в документе
        /// немного, поэтому перебор дешевле отдельного словаря, который пришлось бы
        /// пересобирать с каждой раскладкой.
        /// </summary>
        private PageBreakMark? FindInlineBreakMark(ParagraphBlock tail)
        {
            List<PageBreakMark> marks;
            lock (_renderLock) marks = _breakMarks;

            foreach (var mark in marks)
            {
                if (ReferenceEquals(mark.Tail, tail)) return mark;
            }

            return null;
        }

        /// <summary>
        /// Отметка разрыва на последней строке абзаца, как у Word: пунктир от конца текста
        /// с подписью посередине. Абзац продолжается за разрывом — пунктир идёт до правого
        /// края текста и знака абзаца нет. Абзац кончается разрывом — отметка короткая, и
        /// сразу за ней стоит знак абзаца.
        /// </summary>
        private void DrawInlineBreakMark(
            SKCanvas canvas, ParaLayout pl, SKTextLayout layout,
            float xPt, float yPt, string text, PageBreakMark mark, SKPaint fill)
        {
            float startX;
            float baseline;
            float size;

            if (layout.Lines.Count == 0 || text.Length == 0)
            {
                // Пустой кусок: отметка начинается там, где стояла бы каретка.
                var caret = layout.HitTestPosition(0);
                int baseLine = Math.Max(pl.LineFrom, 0);
                float yBase = baseLine < layout.Lines.Count ? layout.Lines[baseLine].Y : 0f;

                startX = xPt + caret.X;
                baseline = yPt + (caret.Y - yBase) + caret.Baseline;
                size = Math.Clamp(caret.Height * 0.72f, 5f, 28f);
            }
            else
            {
                int lastLine = layout.Lines.Count - 1;
                var line = layout.Lines[lastLine];
                int baseLine = Math.Max(pl.LineFrom, 0);
                float yBase = baseLine < layout.Lines.Count ? layout.Lines[baseLine].Y : 0f;

                startX = CharScreenX(layout, xPt, lastLine, text.Length) + 1f;
                baseline = yPt + (line.Y - yBase) + line.Baseline;
                size = MarkSize(line);
            }

            float onePx = OnePixelPt(canvas);
            float midY = baseline - size * 0.32f;

            using var labelFont = new SKFont(FormattingMarkTypeface, BreakLabelSizePt);
            string label = mark.IsColumn ? ColumnBreakLabel : PageBreakLabel;
            float labelWidth = labelFont.MeasureText(label);
            float labelGap = 3f;

            float textRight = xPt + layout.LeftIndentPt + layout.TextAreaWidthPt;

            // Абзац кончается разрывом: пунктир с каждой стороны подписи — в четверть её
            // ширины, как у Word. Продолжается — до правого края текста.
            float endX = mark.ContinuesParagraph
                ? Math.Max(textRight, startX)
                : startX + labelWidth + 2f * labelGap + labelWidth * 0.5f;

            using var dash = SKPathEffect.CreateDash(new[] { onePx, onePx }, 0f);
            using var dotted = new SKPaint
            {
                Color = FormattingMarkColor,
                IsAntialias = false,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = onePx,
                PathEffect = dash
            };

            float center = (startX + endX) / 2f;
            float labelLeft = center - labelWidth / 2f;
            float labelBaseline = midY + BreakLabelSizePt * 0.35f;

            if (endX - startX > labelWidth + 2f * labelGap)
            {
                canvas.DrawLine(startX, midY, labelLeft - labelGap, midY, dotted);
                canvas.DrawLine(labelLeft + labelWidth + labelGap, midY, endX, midY, dotted);
                canvas.DrawText(label, labelLeft, labelBaseline, SKTextAlign.Left, labelFont, fill);
            }
            else
            {
                canvas.DrawLine(startX, midY, endX, midY, dotted);
            }

            if (!mark.ContinuesParagraph)
            {
                using var markFont = new SKFont(FormattingMarkTypeface, size);
                canvas.DrawText(ParagraphMarkGlyph, endX + 1f, baseline, SKTextAlign.Left, markFont, fill);
            }
        }

        /// <summary>
        /// Знак конца абзаца сразу за последним символом, а у последнего абзаца
        /// ячейки — знак конца ячейки.
        /// </summary>
        private void DrawParagraphEndMark(
            SKCanvas canvas, ParaLayout pl, SKTextLayout layout,
            float xPt, float yPt, string text, SKPaint fill)
        {
            string glyph = ParagraphMarkGlyph;
            if (pl.Cell is { } cell && cell.CellParaIndex >= cell.Cell.Paragraphs.Count - 1)
                glyph = CellEndMarkGlyph;

            float x;
            float baseline;
            float size;

            // Абзац справа налево кончается у левого края строки: знак рисуется влево
            // от своего места, а не вправо.
            bool markGrowsLeft = layout.IsRightToLeft;

            if (layout.Lines.Count == 0 || text.Length == 0)
            {
                // Пустой абзац: знак встаёт туда же, куда встала бы каретка, — с учётом
                // отступа и выравнивания пустой строки.
                var caret = layout.HitTestPosition(0);
                float alignOffset = layout.Alignment switch
                {
                    TextAlignment.Center => layout.TextAreaWidthPt / 2f,
                    TextAlignment.Right => layout.TextAreaWidthPt,
                    _ => 0f
                };
                int baseLine = Math.Max(pl.LineFrom, 0);
                float yBase = baseLine < layout.Lines.Count ? layout.Lines[baseLine].Y : 0f;

                // Пустая строка абзаца справа налево начинается у правого края, и отступ
                // первой строки отсчитывается от него же — это уже учтено в сдвиге строки.
                x = layout.IsRightToLeft && layout.Lines.Count > 0
                    ? xPt + layout.LeftIndentPt + LineAlignShift(layout, 0)
                    : xPt + caret.X + alignOffset;
                baseline = yPt + (caret.Y - yBase) + caret.Baseline;
                size = Math.Clamp(caret.Height * 0.72f, 5f, 28f);
            }
            else
            {
                int lastLine = layout.Lines.Count - 1;
                var line = layout.Lines[lastLine];
                int baseLine = Math.Max(pl.LineFrom, 0);
                float yBase = baseLine < layout.Lines.Count ? layout.Lines[baseLine].Y : 0f;

                // Знак конца абзаца стоит в конце строки на листе. Обычно это и есть
                // место каретки за последним знаком. Но в абзаце справа налево конец
                // строки — её левый край, а в строке с переставленными кусками последний
                // знак текста может стоять посреди строки (иврит в конце обычного абзаца,
                // русское слово в конце абзаца справа налево) — и знак, поставленный за
                // ним, лёг бы поверх соседнего текста.
                if (layout.IsRightToLeft || line.IsBidiReordered)
                {
                    var (lineLeft, lineRight) = layout.GetLineVisualExtent(lastLine);
                    float lineOrigin = xPt + LineAlignShift(layout, lastLine)
                        - (lastLine == 0 ? layout.FirstLineIndentPt : 0f);

                    x = layout.IsRightToLeft
                        ? lineOrigin + lineLeft - 1f
                        : lineOrigin + lineRight + 1f;
                }
                else
                {
                    x = CharScreenX(layout, xPt, lastLine, text.Length) + 1f;
                }

                baseline = yPt + (line.Y - yBase) + line.Baseline;
                size = MarkSize(line);
            }

            using var font = new SKFont(FormattingMarkTypeface, size);
            canvas.DrawText(
                glyph, x, baseline,
                markGrowsLeft ? SKTextAlign.Right : SKTextAlign.Left,
                font, fill);
        }

        /// <summary>
        /// Стрелка табуляции во всю её ширину. Узкая табуляция получает стрелку
        /// хотя бы в полбуквы, иначе её не разглядеть.
        /// </summary>
        private static void DrawTabArrow(SKCanvas canvas, float left, float right, float midY, float size, SKPaint stroke)
        {
            float minLength = size * 0.5f;
            float start = left + size * 0.1f;
            float end = Math.Max(right - size * 0.1f, start + minLength);
            float head = Math.Min(size * 0.22f, (end - start) / 2f);

            using var path = new SKPath();
            path.MoveTo(start, midY);
            path.LineTo(end, midY);
            path.MoveTo(end - head, midY - head * 0.8f);
            path.LineTo(end, midY);
            path.LineTo(end - head, midY + head * 0.8f);
            canvas.DrawPath(path, stroke);
        }

        /// <summary>Уголок переноса строки: вниз и влево со стрелкой.</summary>
        private static void DrawLineBreakArrow(SKCanvas canvas, float x, float baseline, float size, SKPaint stroke)
        {
            float right = x + size * 0.55f;
            float top = baseline - size * 0.7f;
            float bottom = baseline - size * 0.2f;
            float head = size * 0.18f;

            using var path = new SKPath();
            path.MoveTo(right, top);
            path.LineTo(right, bottom);
            path.LineTo(x, bottom);
            path.MoveTo(x + head, bottom - head);
            path.LineTo(x, bottom);
            path.LineTo(x + head, bottom + head);
            canvas.DrawPath(path, stroke);
        }

        /// <summary>
        /// Отметки явных разрывов страницы на видимых листах: пунктир во всю ширину
        /// текста с подписью посередине, как «Разрыв страницы» у Word.
        /// </summary>
        private void DrawPageBreakMarks(SKCanvas canvas, List<PageRect> pages, int firstPage, int lastPage)
        {
            List<PageBreakMark> marks;
            lock (_renderLock) marks = _breakMarks;
            if (marks.Count == 0) return;

            var (_, _, marginRight, _) = GetPagePaddingPt();
            float onePx = OnePixelPt(canvas);

            using var dash = SKPathEffect.CreateDash(new[] { 2f * onePx, 2f * onePx }, 0f);
            using var line = new SKPaint
            {
                Color = FormattingMarkColor,
                IsAntialias = false,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = onePx,
                PathEffect = dash
            };
            using var textPaint = new SKPaint
            {
                Color = FormattingMarkColor,
                IsAntialias = true
            };
            using var font = new SKFont(FormattingMarkTypeface, BreakLabelSizePt);

            foreach (var mark in marks)
            {
                // Разрыв внутри абзаца отмечен на строке самого абзаца (DrawInlineBreakMark).
                if (mark.Tail is not null) continue;

                string label = mark.IsColumn ? ColumnBreakLabel : PageBreakLabel;
                float labelWidth = font.MeasureText(label);

                if (mark.PageIndex < firstPage || mark.PageIndex > lastPage) continue;
                if (mark.PageIndex < 0 || mark.PageIndex >= pages.Count) continue;

                var page = pages[mark.PageIndex];
                // На листе с переплётом справа правое поле шире на переплёт.
                float left = page.PadLeftPt + page.MarginLeftPt;
                float right = page.PadLeftPt + page.WidthPt - marginRight + page.GutterShiftPt;
                if (right <= left) continue;

                // Отметка стоит на месте разрыва: на строке под последним текстом листа.
                float y = mark.Ypt + FallbackLinePt * 0.5f;
                float center = (left + right) / 2f;
                float gap = labelWidth / 2f + 4f;

                if (right - left > labelWidth + 16f)
                {
                    canvas.DrawLine(left, y, center - gap, y, line);
                    canvas.DrawLine(center + gap, y, right, y, line);
                    canvas.DrawText(label, center - labelWidth / 2f, y + 2.5f, SKTextAlign.Left, font, textPaint);
                }
                else
                {
                    canvas.DrawLine(left, y, right, y, line);
                }
            }
        }

        /// <summary>
        /// X символа на листе — так же, как его ставит каретка: смещение выравнивания
        /// строки и растяжка пробелов по ширине учтены, отступ первой строки не
        /// считается дважды.
        /// </summary>
        private static float CharScreenX(SKTextLayout layout, float xPt, int lineIndex, int charIndex)
        {
            var caret = layout.HitTestPosition(charIndex);
            float firstLineBaked = lineIndex == 0 ? layout.FirstLineIndentPt : 0f;
            return xPt + caret.X
                + LineAlignShift(layout, lineIndex) - firstLineBaked
                + JustifyShiftBeforeChar(layout, lineIndex, charIndex);
        }

        /// <summary>Кегль знаков строки: по высоте её букв, в разумных пределах.</summary>
        private static float MarkSize(SKLineLayout line)
        {
            float textHeight = line.TextAscentPt + line.TextDescentPt;
            if (textHeight <= 0f) textHeight = line.Height * 0.8f;
            return Math.Clamp(textHeight * 0.85f, 5f, 28f);
        }

        /// <summary>Середина первой строки абзаца по высоте.</summary>
        private static float FirstLineMidY(SKTextLayout layout, float yPt)
        {
            if (layout.Lines.Count == 0) return yPt + FallbackLinePt / 2f;

            var first = layout.Lines[0];
            float textHeight = first.TextAscentPt + first.TextDescentPt;
            if (textHeight <= 0f || textHeight >= first.Height)
                return yPt + first.Height / 2f;

            return yPt + first.Baseline - first.TextAscentPt + textHeight / 2f;
        }

        /// <summary>Один пиксель экрана в пунктах документа при нынешнем масштабе холста.</summary>
        private static float OnePixelPt(SKCanvas canvas)
        {
            var m = canvas.TotalMatrix;
            float scale = MathF.Sqrt(m.ScaleX * m.ScaleX + m.SkewY * m.SkewY);
            return scale < 0.01f ? 1f : 1f / scale;
        }
    }
}
