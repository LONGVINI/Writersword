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
        public static void DrawPage(
            SKCanvas canvas,
            HeaderFooterSettings settings,
            PageDecoration decoration,
            HeaderFooterPageBox box,
            SKColor color,
            Func<string, bool, bool, SKTypeface> typeface,
            string fallbackFamily,
            bool drawHeader = true,
            bool drawFooter = true)
        {
            if (!decoration.BandsVisible) return;

            using var font = CreateFont(settings, typeface, fallbackFamily);
            using var paint = new SKPaint
            {
                Color = color,
                IsAntialias = true,
                Style = SKPaintStyle.Fill
            };

            var anchors = SlotAnchors(settings, box);

            if (drawHeader)
                DrawBand(canvas, settings.GetBand(decoration.Variant, true), decoration, box, true, font, paint, anchors);
            if (drawFooter)
                DrawBand(canvas, settings.GetBand(decoration.Variant, false), decoration, box, false, font, paint, anchors);
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

            int headerLines = MaxLines(settings, true);
            int footerLines = MaxLines(settings, false);
            if (headerLines == 0 && footerLines == 0) return (0f, 0f);

            using var font = CreateFont(settings, typeface, fallbackFamily);
            float lineHeight = LineHeight(font);

            // Без зазора, как в Word: пока колонтитул умещается в поле листа, текст
            // документа стоит там же, где стоял бы без колонтитула.
            float top = headerLines > 0 ? headerDistancePt + lineHeight * headerLines : 0f;
            float bottom = footerLines > 0 ? footerDistancePt + lineHeight * footerLines : 0f;
            return (top, bottom);
        }

        /// <summary>Наибольшее число строк у полос одной стороны среди всех вариантов. Ноль — полосы пусты.</summary>
        private static int MaxLines(HeaderFooterSettings settings, bool header)
        {
            int lines = 0;

            void Take(HeaderFooterBand band)
            {
                for (int s = 0; s < 3; s++)
                {
                    string text = band.GetSlot(s);
                    if (text.Length > 0)
                        lines = Math.Max(lines, SplitLines(text).Count);
                }
            }

            Take(header ? settings.Header : settings.Footer);
            if (settings.DifferentFirstPage) Take(header ? settings.FirstHeader : settings.FirstFooter);
            if (settings.DifferentOddEven) Take(header ? settings.EvenHeader : settings.EvenFooter);
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
            SKFont font,
            SKPaint paint,
            (float CenterX, float RightX) anchors)
        {
            if (band.IsEmpty) return;

            string? left = PageNumbering.RenderSlot(band.Left, decoration);
            string? center = PageNumbering.RenderSlot(band.Center, decoration);
            string? right = PageNumbering.RenderSlot(band.Right, decoration);

            DrawSlot(canvas, left, 0, box, header, font, paint, anchors);
            DrawSlot(canvas, center, 1, box, header, font, paint, anchors);
            DrawSlot(canvas, right, 2, box, header, font, paint, anchors);
        }

        private static void DrawSlot(
            SKCanvas canvas, string? text, int slot,
            HeaderFooterPageBox box, bool header, SKFont font, SKPaint paint,
            (float CenterX, float RightX) anchors)
        {
            if (string.IsNullOrEmpty(text)) return;

            var lines = SplitLines(text);
            var m = font.Metrics;
            float lineHeight = LineHeight(font);
            float blockHeight = lineHeight * lines.Count;

            // Верхний колонтитул растёт вниз от своей линии, нижний — вверх от своей:
            // так многострочный нижний колонтитул не уходит за край листа.
            float top = header
                ? box.Y + box.HeaderDistance
                : box.Y + box.Height - box.FooterDistance - blockHeight;

            float center = anchors.CenterX;

            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];
                if (line.Length == 0) continue;

                float baseline = top + lineHeight * i - m.Ascent;
                float width = font.MeasureText(line);

                float x = slot switch
                {
                    0 => box.ContentLeft,
                    1 => center - width / 2f,
                    _ => anchors.RightX - width
                };

                canvas.DrawText(line, x, baseline, SKTextAlign.Left, font, paint);
            }
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
