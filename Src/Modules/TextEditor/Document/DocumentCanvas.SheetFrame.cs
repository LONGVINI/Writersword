using SkiaSharp;
using System;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Рамка листа — линия в один пиксель экрана вокруг страницы, цвет задаёт вид
    /// (ReadingTheme.FrameColor). Нет цвета — нет и рамки: так у всех видов по
    /// умолчанию.
    ///
    /// Ширина рамки — пиксель экрана, а не пункт документа. Рамка отделяет лист от
    /// поля, а не оформляет страницу: на печать она не идёт, и при крупном масштабе
    /// толстая линия выглядела бы нарисованной на бумаге, а при мелком пропадала бы
    /// вовсе.
    ///
    /// Линия лежит снаружи листа, вплотную к его краю: бумага, картинка бумаги и
    /// содержимое страницы её не закрывают, а сама она не отъедает от листа ни
    /// точки. Рисуется там же, где кладётся бумага: подложкой, основным проходом,
    /// лентой, колонкой чтения и книгой — везде, где на экране лежит лист вида.
    /// Летящий при перевороте лист рамки не получает: он гнётся, и прямоугольник
    /// вокруг него лёг бы мимо бумаги.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        // Волосяная линия (толщина 0) — ровно один пиксель устройства при любом
        // масштабе холста. Без сглаживания: сглаженная линия на дробной координате
        // размазывается на два бледных пикселя, и тонкая рамка перестаёт читаться.
        private readonly SKPaint _paintSheetFrame = new()
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 0f,
            IsAntialias = false
        };

        /// <summary>
        /// Цвет рамки листа у активного вида. false — рамки нет: вид не выбран,
        /// цвет не задан или задан «без цвета».
        /// </summary>
        private bool TryGetSheetFrameColor(out SKColor color)
        {
            color = SKColors.Transparent;

            if (!ThemedSurface) return false;

            var theme = ActiveTheme;
            if (theme is null || string.IsNullOrWhiteSpace(theme.FrameColor)) return false;

            color = ParseHex(theme.FrameColor, SKColors.Transparent);
            return color.Alpha > 0;
        }

        /// <summary>
        /// Рисует рамку вокруг листа (x, y, w, h — в точках документа, в текущей
        /// системе координат холста). Рамки у вида нет — ничего не делает.
        /// </summary>
        private void DrawSheetFrame(SKCanvas canvas, float x, float y, float w, float h)
        {
            if (w <= 0f || h <= 0f) return;
            if (!TryGetSheetFrameColor(out var color)) return;

            // Полпикселя устройства в точках документа: линия встаёт на пиксель сразу
            // за краем листа, а не поверх его крайнего столбца.
            var m = canvas.TotalMatrix;
            float pxPerPtX = MathF.Sqrt(m.ScaleX * m.ScaleX + m.SkewY * m.SkewY);
            float pxPerPtY = MathF.Sqrt(m.ScaleY * m.ScaleY + m.SkewX * m.SkewX);
            float halfX = pxPerPtX > 0.0001f ? 0.5f / pxPerPtX : 0f;
            float halfY = pxPerPtY > 0.0001f ? 0.5f / pxPerPtY : 0f;

            if (_paintSheetFrame.Color != color) _paintSheetFrame.Color = color;

            canvas.DrawRect(
                new SKRect(x - halfX, y - halfY, x + w + halfX, y + h + halfY),
                _paintSheetFrame);
        }
    }
}
