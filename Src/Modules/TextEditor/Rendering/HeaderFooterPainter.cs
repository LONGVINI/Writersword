using System;
using System.Collections.Generic;
using SkiaSharp;
using Writersword.Modules.TextEditor.Models.Page;
using Writersword.Modules.TextEditor.Services;

namespace Writersword.Modules.TextEditor.Rendering
{
    /// <summary>
    /// Лист глазами колонтитула: где он лежит и какие у него поля. Всё в пунктах, в
    /// координатах того холста, на котором рисуют.
    /// </summary>
    public readonly record struct HeaderFooterPageBox(
        float X,
        float Y,
        float Width,
        float Height,
        float MarginLeft,
        float MarginRight,
        float MarginTop,
        float MarginBottom,
        float HeaderDistance,
        float FooterDistance)
    {
        /// <summary>Левый край текста.</summary>
        public float ContentLeft => X + MarginLeft;

        /// <summary>Правый край текста.</summary>
        public float ContentRight => X + Width - MarginRight;
    }

    /// <summary>
    /// Шрифты колонтитулов под рукой: по одному на гарнитуру, кегль, жирность и курсив.
    /// У полос и их кусков бывает своё оформление — шрифт под каждое создаётся раз.
    /// Освобождает шрифты тот, кто создал набор.
    /// </summary>
    public sealed class HeaderFooterFonts : IDisposable
    {
        private readonly Func<string, bool, bool, SKTypeface> _typeface;
        private readonly string _fallbackFamily;
        private readonly Dictionary<(string Family, bool Bold, bool Italic, float Size), SKFont> _fonts = new();

        public HeaderFooterFonts(HeaderFooterSettings settings, Func<string, bool, bool, SKTypeface> typeface, string fallbackFamily)
        {
            Settings = settings;
            _typeface = typeface;
            _fallbackFamily = fallbackFamily;
        }

        /// <summary>Колонтитулы документа: их шрифт — под оформлением полос.</summary>
        public HeaderFooterSettings Settings { get; }

        /// <summary>Шрифт оформления.</summary>
        public SKFont Get(HeaderFooterFont font)
        {
            string family = string.IsNullOrWhiteSpace(font.FontFamily) ? _fallbackFamily : font.FontFamily!;
            float size = (float)Math.Clamp(font.FontSizePt > 0 ? font.FontSizePt : 10.0, 4.0, 72.0);
            var key = (family, font.IsBold, font.IsItalic, size);

            if (_fonts.TryGetValue(key, out var existing)) return existing;

            var created = new SKFont(_typeface(family, font.IsBold, font.IsItalic), size)
            {
                Subpixel = true,
                LinearMetrics = true
            };
            _fonts[key] = created;
            return created;
        }

        public void Dispose()
        {
            foreach (var font in _fonts.Values)
                font.Dispose();
            _fonts.Clear();
        }
    }

    /// <summary>
    /// Рисует колонтитулы листа. Один художник на всех: полотно правки, печать и PDF
    /// рисуют колонтитул одинаково, различаются только лист и шрифты под рукой.
    ///
    /// Колонтитул лежит в поле листа, как в Word: верхний — от края листа на
    /// расстоянии «от края до колонтитула» вниз, нижний — на том же расстоянии от
    /// нижнего края вверх. Текст документа он не двигает.
    /// </summary>
    public static class HeaderFooterPainter
    {
        /// <summary>Промежуток между строками одного места колонтитула в долях кегля.</summary>
        private const float LineGapFactor = 0.2f;

        /// <summary>
        /// Рисует верхний и нижний колонтитулы листа.
        /// </summary>
        /// <param name="canvas">Холст.</param>
        /// <param name="settings">Колонтитулы документа.</param>
        /// <param name="decoration">Оформление этого листа.</param>
        /// <param name="box">Лист.</param>
        /// <param name="color">Цвет текста.</param>
        /// <param name="typeface">Гарнитура по имени, жирности и курсиву.</param>
        /// <param name="fallbackFamily">Гарнитура, если своей у колонтитулов нет.</param>
        /// <param name="drawHeader">Рисовать верхний колонтитул. Полотно не рисует тот, что правится на листе полем ввода.</param>
        /// <param name="drawFooter">Рисовать нижний колонтитул.</param>
        /// <param name="ink">
        /// Как свой цвет полосы или куска выглядит на этой бумаге — так же, как
        /// <paramref name="color"/>. Null — как есть.
        /// </param>
        public static void DrawPage(
            SKCanvas canvas,
            HeaderFooterSettings settings,
            PageDecoration decoration,
            HeaderFooterPageBox box,
            SKColor color,
            Func<string, bool, bool, SKTypeface> typeface,
            string fallbackFamily,
            bool drawHeader = true,
            bool drawFooter = true,
            Func<SKColor, SKColor>? ink = null)
        {
            if (!decoration.BandsVisible) return;

            using var fonts = new HeaderFooterFonts(settings, typeface, fallbackFamily);
            using var paint = new SKPaint
            {
                Color = color,
                IsAntialias = true,
                Style = SKPaintStyle.Fill
            };

            var anchors = SlotAnchors(settings, box);
            var palette = new BandPalette(settings.TextColor, color, ink);

            if (drawHeader)
                DrawBand(canvas, settings.BandFor(decoration, true), decoration, box, true, fonts, paint, palette, anchors);
            if (drawFooter)
                DrawBand(canvas, settings.BandFor(decoration, false), decoration, box, false, fonts, paint, palette, anchors);
        }

        /// <summary>
        /// Цвет куска: свой цвет полосы или куска — через <see cref="Ink"/>, иначе цвет
        /// колонтитулов, который дал вызывающий.
        /// </summary>
        private readonly record struct BandPalette(string? DocumentColor, SKColor Color, Func<SKColor, SKColor>? Ink)
        {
            public SKColor For(HeaderFooterFont font)
            {
                if (string.IsNullOrWhiteSpace(font.TextColor)
                    || string.Equals(font.TextColor, DocumentColor, StringComparison.OrdinalIgnoreCase))
                    return Color;

                string hex = font.TextColor!.StartsWith('#') ? font.TextColor! : "#" + font.TextColor;
                if (!SKColor.TryParse(hex, out var own)) return Color;

                return Ink is null ? own : Ink(own);
            }
        }

        /// <summary>Шрифт колонтитулов. Вызывающий освобождает его сам.</summary>
        public static SKFont CreateFont(
            HeaderFooterSettings settings,
            Func<string, bool, bool, SKTypeface> typeface,
            string fallbackFamily)
        {
            string family = string.IsNullOrWhiteSpace(settings.FontFamily) ? fallbackFamily : settings.FontFamily!;
            float size = (float)Math.Clamp(settings.FontSizePt, 4.0, 72.0);

            return new SKFont(typeface(family, settings.IsBold, settings.IsItalic), size)
            {
                Subpixel = true,
                LinearMetrics = true
            };
        }


        /// <summary>
        /// Сколько места над текстом и под ним занимают колонтитулы — от края листа до
        /// границы, за которую текст документа заходить не должен. Ноль — колонтитула
        /// этой стороны нет. Как в Word: колонтитул, который выше поля листа, отодвигает
        /// текст, а не ложится на него. Поле листа меньше этой величины раскладка
        /// увеличивает до неё.
        /// </summary>
        public static (float TopPt, float BottomPt) BodyReservePt(
            HeaderFooterSettings? settings,
            float headerDistancePt,
            float footerDistancePt,
            Func<string, bool, bool, SKTypeface> typeface,
            string fallbackFamily)
        {
            if (settings is null || settings.IsEmpty) return (0f, 0f);

            using var fonts = new HeaderFooterFonts(settings, typeface, fallbackFamily);

            float headerHeight = MaxBandHeight(settings, true, fonts);
            float footerHeight = MaxBandHeight(settings, false, fonts);

            // Без зазора, как в Word: пока колонтитул умещается в поле листа, текст
            // документа стоит там же, где стоял бы без колонтитула.
            float top = headerHeight > 0f ? headerDistancePt + headerHeight : 0f;
            float bottom = footerHeight > 0f ? footerDistancePt + footerHeight : 0f;
            return (top, bottom);
        }

        /// <summary>Наибольшая высота полос одной стороны среди всех вариантов. Ноль — полосы пусты.</summary>
        private static float MaxBandHeight(HeaderFooterSettings settings, bool header, HeaderFooterFonts fonts)
        {
            float height = BandHeightPt(header ? settings.Header : settings.Footer, null, fonts);
            if (settings.DifferentFirstPage)
                height = Math.Max(height, BandHeightPt(header ? settings.FirstHeader : settings.FirstFooter, null, fonts));
            if (settings.DifferentOddEven)
                height = Math.Max(height, BandHeightPt(header ? settings.EvenHeader : settings.EvenFooter, null, fonts));
            return height;
        }

        /// <summary>
        /// Высота полосы — её самого высокого места. С оформлением листа считаются только
        /// места, которые на нём печатаются; без него — все. Ноль — печатать нечего.
        /// </summary>
        public static float BandHeightPt(HeaderFooterBand? band, PageDecoration? decoration, HeaderFooterFonts fonts)
        {
            if (band is null || band.IsEmpty) return 0f;

            float height = 0f;
            for (int slot = 0; slot < 3; slot++)
            {
                var lines = LayoutSlot(band, slot, decoration, fonts);
                if (lines is null) continue;

                float block = 0f;
                foreach (var line in lines)
                    block += line.Height;
                height = Math.Max(height, block);
            }

            return height;
        }

        /// <summary>Кусок строки колонтитула: текст одного оформления.</summary>
        private readonly record struct SlotPiece(string Text, HeaderFooterFont Style, SKFont Font);

        /// <summary>Строка места колонтитула: куски, высота, подъём над базовой линией и ширина.</summary>
        private sealed class SlotLine
        {
            public List<SlotPiece> Pieces { get; } = new();
            public float Height;
            public float Ascent;
            public float Width;
            public bool HasText;
        }

        /// <summary>
        /// Строки места по кускам оформления. С оформлением листа поля номера заменены
        /// значениями; место, которое на листе гаснет, — null. Без оформления поля
        /// остаются как есть: так считается высота.
        /// </summary>
        private static List<SlotLine>? LayoutSlot(
            HeaderFooterBand band, int slot, PageDecoration? decoration, HeaderFooterFonts fonts)
        {
            string template = band.GetSlot(slot);
            if (template.Length == 0) return null;
            if (decoration is not null && PageNumbering.RenderSlot(template, decoration) is null) return null;

            var pieces = band.SlotPieces(slot, fonts.Settings.BaseFont);
            if (pieces.Count == 0) return null;

            var lines = new List<SlotLine> { new() };

            foreach (var (pieceText, style) in pieces)
            {
                string text = decoration is null
                    ? pieceText.Replace("\r\n", "\n", StringComparison.Ordinal)
                    : PageNumbering.RenderTokens(pieceText, decoration).Replace("\r\n", "\n", StringComparison.Ordinal);

                var font = fonts.Get(style);
                var parts = text.Split('\n');

                for (int k = 0; k < parts.Length; k++)
                {
                    if (k > 0) lines.Add(new SlotLine());
                    lines[^1].Pieces.Add(new SlotPiece(parts[k], style, font));
                }
            }

            // Пустые строки в конце места не печатаются и места не занимают — как в SplitLines.
            foreach (var line in lines)
                line.HasText = line.Pieces.Exists(p => p.Text.Length > 0);
            while (lines.Count > 1 && !lines[^1].HasText)
                lines.RemoveAt(lines.Count - 1);

            foreach (var line in lines)
            {
                foreach (var piece in line.Pieces)
                {
                    line.Height = Math.Max(line.Height, LineHeight(piece.Font));
                    line.Ascent = Math.Max(line.Ascent, -piece.Font.Metrics.Ascent);
                    if (piece.Text.Length > 0)
                        line.Width += piece.Font.MeasureText(piece.Text);
                }
            }

            return lines;
        }

        private const float PointsPerMm = 72f / 25.4f;

        /// <summary>
        /// Где на листе стоят среднее и правое места: середина среднего и правый край
        /// правого, в тех же координатах, что и лист. Без своих позиций — середина и
        /// правый край текста.
        /// </summary>
        public static (float CenterX, float RightX) SlotAnchors(HeaderFooterSettings? settings, HeaderFooterPageBox box)
        {
            float width = box.ContentRight - box.ContentLeft;

            float center = settings?.CenterTabMm is double c
                ? box.ContentLeft + (float)c * PointsPerMm
                : box.ContentLeft + width / 2f;

            float right = settings?.RightTabMm is double r
                ? box.ContentLeft + (float)r * PointsPerMm
                : box.ContentRight;

            return (center, right);
        }

        /// <summary>Высота одной строки колонтитула.</summary>
        public static float LineHeight(SKFont font)
        {
            var m = font.Metrics;
            return (-m.Ascent + m.Descent) + font.Size * LineGapFactor;
        }

        /// <summary>
        /// Прямоугольник места колонтитула на листе: где его текст стоит сейчас или
        /// встанет, если место пустое. По нему ставится поле правки на листе.
        /// </summary>
        /// <param name="lines">Сколько строк в месте (не меньше одной).</param>
        public static SKRect SlotRect(HeaderFooterPageBox box, bool header, int slot, SKFont font, int lines)
        {
            float lineHeight = LineHeight(font);
            float height = lineHeight * Math.Max(lines, 1);

            float third = Math.Max((box.ContentRight - box.ContentLeft) / 3f, 1f);
            float left = box.ContentLeft + third * Math.Clamp(slot, 0, 2);

            float top = header
                ? box.Y + box.HeaderDistance
                : box.Y + box.Height - box.FooterDistance - height;

            return new SKRect(left, top, left + third, top + height);
        }

        /// <summary>
        /// Зона колонтитула на листе — всё поле над текстом или под ним. В ней двойной
        /// щелчок открывает правку колонтитула.
        /// </summary>
        public static SKRect ZoneRect(HeaderFooterPageBox box, bool header)
            => header
                ? new SKRect(box.X, box.Y, box.X + box.Width, box.Y + Math.Max(box.MarginTop, 1f))
                : new SKRect(box.X, box.Y + box.Height - Math.Max(box.MarginBottom, 1f),
                    box.X + box.Width, box.Y + box.Height);

        /// <summary>
        /// Где стоит текст места полосы на листе: прямоугольник по его фактической
        /// ширине, с оформлением кусков. Null — место на этом листе пустое. Нужен
        /// подсветке номера, набранного руками.
        /// </summary>
        public static SKRect? MeasureSlot(
            HeaderFooterPageBox box, bool header, int slot, HeaderFooterBand band,
            PageDecoration decoration, HeaderFooterFonts fonts)
        {
            if (!decoration.BandsVisible) return null;

            var lines = LayoutSlot(band, slot, decoration, fonts);
            if (lines is null) return null;

            float blockHeight = 0f;
            float widest = 0f;
            foreach (var line in lines)
            {
                blockHeight += line.Height;
                widest = Math.Max(widest, line.Width);
            }
            if (widest <= 0f) return null;

            float top = header
                ? box.Y + box.HeaderDistance
                : box.Y + box.Height - box.FooterDistance - blockHeight;

            var (centerX, rightX) = SlotAnchors(fonts.Settings, box);

            float left = slot switch
            {
                0 => box.ContentLeft,
                1 => centerX - widest / 2f,
                _ => rightX - widest
            };

            return new SKRect(left, top, left + widest, top + blockHeight);
        }

        /// <summary>
        /// Где стоит текст места на листе: прямоугольник по его фактической ширине.
        /// Null — место на этом листе пустое. Нужен подсветке номера, набранного руками.
        /// </summary>
        public static SKRect? MeasureSlot(
            HeaderFooterPageBox box, bool header, int slot, string? text, SKFont font,
            HeaderFooterSettings? settings = null)
        {
            if (string.IsNullOrEmpty(text)) return null;

            var lines = SplitLines(text);
            float lineHeight = LineHeight(font);
            float blockHeight = lineHeight * lines.Count;

            float top = header
                ? box.Y + box.HeaderDistance
                : box.Y + box.Height - box.FooterDistance - blockHeight;

            float widest = 0f;
            foreach (var line in lines)
                widest = Math.Max(widest, font.MeasureText(line));

            var (centerX, rightX) = SlotAnchors(settings, box);

            float left = slot switch
            {
                0 => box.ContentLeft,
                1 => centerX - widest / 2f,
                _ => rightX - widest
            };

            return new SKRect(left, top, left + widest, top + blockHeight);
        }

        private static void DrawBand(
            SKCanvas canvas,
            HeaderFooterBand band,
            PageDecoration decoration,
            HeaderFooterPageBox box,
            bool header,
            HeaderFooterFonts fonts,
            SKPaint paint,
            BandPalette palette,
            (float CenterX, float RightX) anchors)
        {
            if (band.IsEmpty) return;

            for (int slot = 0; slot < 3; slot++)
                DrawSlot(canvas, band, slot, decoration, box, header, fonts, paint, palette, anchors);
        }

        private static void DrawSlot(
            SKCanvas canvas, HeaderFooterBand band, int slot, PageDecoration decoration,
            HeaderFooterPageBox box, bool header, HeaderFooterFonts fonts, SKPaint paint,
            BandPalette palette, (float CenterX, float RightX) anchors)
        {
            var lines = LayoutSlot(band, slot, decoration, fonts);
            if (lines is null) return;

            float blockHeight = 0f;
            foreach (var line in lines)
                blockHeight += line.Height;

            // Верхний колонтитул растёт вниз от своей линии, нижний — вверх от своей:
            // так многострочный нижний колонтитул не уходит за край листа.
            float top = header
                ? box.Y + box.HeaderDistance
                : box.Y + box.Height - box.FooterDistance - blockHeight;

            var baseColor = paint.Color;
            float lineTop = top;

            foreach (var line in lines)
            {
                if (line.HasText)
                {
                    float baseline = lineTop + line.Ascent;

                    float x = slot switch
                    {
                        0 => box.ContentLeft,
                        1 => anchors.CenterX - line.Width / 2f,
                        _ => anchors.RightX - line.Width
                    };

                    foreach (var piece in line.Pieces)
                    {
                        if (piece.Text.Length == 0) continue;

                        paint.Color = palette.For(piece.Style);
                        canvas.DrawText(piece.Text, x, baseline, SKTextAlign.Left, piece.Font, paint);
                        x += piece.Font.MeasureText(piece.Text);
                    }
                }

                lineTop += line.Height;
            }

            paint.Color = baseColor;
        }

        /// <summary>Строки места колонтитула.</summary>
        public static List<string> SplitLines(string text)
        {
            var lines = new List<string>(text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'));
            while (lines.Count > 1 && lines[^1].Length == 0)
                lines.RemoveAt(lines.Count - 1);
            return lines;
        }
    }
}
