using System;
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

                    // Добавляем SpaceBefore только перед первым слайсом параграфа.
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

                    // Записываем финальный слайс параграфа (остаток или весь параграф).
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
        /// Использует RenderParagraphLines для корректного рендеринга
        /// параграфов разбитых по страницам — рисует только строки слайса.
        /// Canvas должен быть настроен на размер страницы в pt.
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

                // Выделение — рисуем под текстом.
                if (selectionParaIndex == para.ParagraphIndex && selectionFrom < selectionTo)
                {
                    var rects = para.Layout.HitTestRange(selectionFrom, selectionTo);

                    // Y-база первой строки слайса — для корректного смещения выделения.
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

                // Каретка — рисуем поверх текста.
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
        /// Координаты параграфа (paraX, paraY) в pt — включают поля страницы.
        /// Public — используется DocumentCanvas напрямую для отрисовки параграфов
        /// вне контекста SKPageLayout.
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

                    // Highlight — фон под текстом.
                    if (seg.HighlightColor != SKColors.Transparent)
                    {
                        using var hlPaint = new SKPaint { Color = seg.HighlightColor };
                        canvas.DrawRect(segX, lineY, seg.Width, line.Height, hlPaint);
                    }

                    using var typeface = CreateTypeface(seg.FontFamily, seg.IsBold, seg.IsItalic);
                    using var font = new SKFont(typeface, seg.FontSizePt);
                    using var paint = new SKPaint
                    {
                        Color = seg.Color,
                        IsAntialias = true
                    };

                    canvas.DrawText(seg.Text, segX, baseY, font, paint);

                    // Подчёркивание.
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

                    // Зачёркивание.
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
        /// Один токен = один символ — обеспечивает точный посимвольный HitTest.
        /// Форматирование берётся из Run.Properties или из стиля параграфа.
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

        // ── Вёрстка строк ────────────────────────────────────────────────

        /// <summary>
        /// Жадный алгоритм переноса токенов по строкам с учётом ширины текстовой области.
        /// Строит строки — заполняет layout.Lines и вычисляет высоты.
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

        /// <summary>
        /// Добавляет слово (буфер символов) в текущую строку.
        /// </summary>
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

        /// <summary>
        /// Добавляет один символ в строку.
        /// Если предыдущий сегмент имеет то же форматирование — добавляет к нему.
        /// Иначе создаёт новый сегмент.
        /// </summary>
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

        /// <summary>
        /// Завершает строку — вычисляет метрики глифов, высоту, baseline.
        /// </summary>
        private static void FinalizeLine(
            SKLineLayout line,
            SKTextLayout layout,
            float lineSpacing)
        {
            float maxAscent = 0f;
            float maxDescent = 0f;

            foreach (var seg in line.Segments)
            {
                using var typeface = CreateTypeface(seg.FontFamily, seg.IsBold, seg.IsItalic);
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

        /// <summary>
        /// Строит пустую строку для пустого параграфа.
        /// </summary>
        private static SKLineLayout BuildEmptyLine(SKTextLayout layout, float lineSpacing)
        {
            using var typeface = CreateTypeface(
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

        // ── Выравнивание ─────────────────────────────────────────────────

        /// <summary>
        /// Вычисляет X-смещение строки для выравнивания (center, right, justify).
        /// Justify не применяется к последней строке параграфа.
        /// </summary>
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
            using var typeface = CreateTypeface(format.FontFamily, format.IsBold, format.IsItalic);
            using var font = new SKFont(typeface, format.FontSizePt);
            return font.MeasureText(ch);
        }

        /// <summary>
        /// Строит массив метрик глифов для сегмента.
        /// Каждый элемент — X-позиция и ширина одного символа.
        /// Используется для посимвольного HitTest.
        /// </summary>
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

        // ── Вспомогательные ──────────────────────────────────────────────

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

        private static SKTypeface CreateTypeface(string family, bool bold, bool italic)
        {
            var style = (bold, italic) switch
            {
                (true, true) => SKFontStyle.BoldItalic,
                (true, false) => SKFontStyle.Bold,
                (false, true) => SKFontStyle.Italic,
                _ => SKFontStyle.Normal
            };

            return SKTypeface.FromFamilyName(family, style)
                ?? SKTypeface.FromFamilyName(StyleResolver.FallbackFontFamily, style)
                ?? SKTypeface.Default;
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
        /// Y-смещение вычисляется относительно первой строки диапазона —
        /// корректно при разбивке параграфа по страницам.
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

                foreach (var seg in line.Segments)
                {
                    float segX = paraX + seg.X + offsetX;
                    float baseY = lineY + line.Baseline;

                    if (seg.HighlightColor != SKColors.Transparent)
                    {
                        using var hlPaint = new SKPaint { Color = seg.HighlightColor };
                        canvas.DrawRect(segX, lineY, seg.Width, line.Height, hlPaint);
                    }

                    using var typeface = CreateTypeface(seg.FontFamily, seg.IsBold, seg.IsItalic);
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