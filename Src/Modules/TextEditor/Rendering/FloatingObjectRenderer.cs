using SkiaSharp;
using System;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Rendering
{
    /// <summary>
    /// Отрисовка плавающих объектов — фигур и картинок — в отрыве от канваса.
    ///
    /// Вынесено сюда потому, что рисовать их должны двое: экранный DocumentCanvas и
    /// печать (TextEditorPrintDocument). Пока код жил в канвасе, печать про фигуры и
    /// картинки не знала вовсе — в PDF уходил один текст с таблицами, а всё
    /// плавающее пропадало. Теперь геометрия одна на оба пути, и разойтись им негде.
    ///
    /// Класс без состояния: кисти создаются на вызов. Печать идёт раз в документ, а
    /// экран держит свои кисти сам и зовёт отсюда только построение геометрии.
    /// </summary>
    public static class FloatingObjectRenderer
    {
        /// <summary>Качество ресемплинга картинок — то же, что у картинок документа.</summary>
        public static readonly SKSamplingOptions Sampling = new(SKCubicResampler.Mitchell);

        /// <summary>
        /// Замкнутый контур фигуры. По нему идёт и заливка, и обводка, и обрезка
        /// картинки-заливки — один источник геометрии на всё, иначе они разъезжаются.
        /// Для линии и стрелки контура нет: возвращается null.
        /// </summary>
        public static SKPath? BuildShapePath(ShapeBlock shape, SKRect rect)
            => BuildShapePath(shape.ShapeType, shape.CornerRadiusPt, rect);

        /// <summary>
        /// Тот же контур, но по типу и радиусу, а не по конкретному блоку: форма
        /// есть и у фигуры, и у картинки, а геометрия у них обязана быть одна.
        /// </summary>
        public static SKPath? BuildShapePath(ShapeType type, double cornerRadiusPt, SKRect rect)
        {
            switch (type)
            {
                case ShapeType.Rectangle:
                {
                    var path = new SKPath();
                    float r = (float)Math.Max(cornerRadiusPt, 0.0);
                    float maxR = Math.Min(rect.Width, rect.Height) / 2f;
                    if (r > maxR) r = maxR;
                    if (r > 0f) path.AddRoundRect(new SKRoundRect(rect, r, r));
                    else path.AddRect(rect);
                    return path;
                }

                case ShapeType.Ellipse:
                {
                    var path = new SKPath();
                    path.AddOval(rect);
                    return path;
                }

                case ShapeType.Callout:
                {
                    // Пузырь и хвост — один контур: иначе по низу пузыря проходит
                    // лишняя линия обводки в месте стыка.
                    float tailH = Math.Min(rect.Height * 0.28f, 24f);
                    float bubbleBottom = rect.Bottom - tailH;
                    if (bubbleBottom <= rect.Top + 1f) bubbleBottom = rect.Top + 1f;

                    float tailLeft = rect.Left + rect.Width * 0.22f;
                    float tailRight = rect.Left + rect.Width * 0.38f;
                    float tailTipX = rect.Left + rect.Width * 0.16f;

                    var path = new SKPath();
                    path.MoveTo(rect.Left, rect.Top);
                    path.LineTo(rect.Right, rect.Top);
                    path.LineTo(rect.Right, bubbleBottom);
                    path.LineTo(tailRight, bubbleBottom);
                    path.LineTo(tailTipX, rect.Bottom);
                    path.LineTo(tailLeft, bubbleBottom);
                    path.LineTo(rect.Left, bubbleBottom);
                    path.Close();
                    return path;
                }

                default:
                    return null;
            }
        }

        /// <summary>
        /// Штриховой эффект линии. null — сплошная. Длины штрихов заданы в толщинах
        /// линии: у тонкой линии пунктир иначе выродился бы в точки, а у толстой
        /// слипся бы в сплошную.
        ///
        /// Общий на рамку картинки и обводку фигуры — иначе один и тот же пунктир
        /// выглядел бы на них по-разному.
        /// </summary>
        public static SKPathEffect? BuildDashEffect(ShapeDashStyle dash, double thicknessPt)
        {
            float w = (float)Math.Max(thicknessPt, 0.25);
            return dash switch
            {
                ShapeDashStyle.Dash => SKPathEffect.CreateDash(new[] { w * 4f, w * 3f }, 0f),
                ShapeDashStyle.Dot => SKPathEffect.CreateDash(new[] { w, w * 2f }, 0f),
                ShapeDashStyle.DashDot => SKPathEffect.CreateDash(
                    new[] { w * 4f, w * 2f, w, w * 2f }, 0f),
                _ => null
            };
        }

        /// <summary>
        /// Прямоугольник картинки-заливки. При растяжении — весь габарит фигуры,
        /// иначе картинка вписывается целиком с сохранением пропорций и центрируется.
        /// </summary>
        public static SKRect FillImageRect(SKImage image, SKRect rect, bool stretch)
            => FillImageRect(image.Width, image.Height, rect, stretch);

        /// <summary>
        /// То же, но по размеру ИСХОДНОГО куска, а не всего файла: после
        /// кадрирования пропорции задаёт оставшаяся часть картинки, и вписывать
        /// надо её. По полному размеру файла обрезанная заливка вписывалась бы
        /// с чужими пропорциями и уезжала из центра фигуры.
        /// </summary>
        public static SKRect FillImageRect(
            float srcWidthPx, float srcHeightPx, SKRect rect, bool stretch)
        {
            if (stretch || srcWidthPx <= 0f || srcHeightPx <= 0f) return rect;

            float scale = Math.Min(rect.Width / srcWidthPx, rect.Height / srcHeightPx);
            float w = srcWidthPx * scale;
            float h = srcHeightPx * scale;
            float left = rect.MidX - w / 2f;
            float top = rect.MidY - h / 2f;
            return new SKRect(left, top, left + w, top + h);
        }

        /// <summary>
        /// Видимая часть исходной картинки после кадрирования, в пикселях файла.
        /// Общая на картинку документа и на картинку-заливку фигуры: кадрирование
        /// у них одно и то же действие, и считаться оно обязано одинаково.
        /// </summary>
        public static SKRect CropSrcRect(
            SKImage image,
            double cropLeftFrac, double cropTopFrac,
            double cropRightFrac, double cropBottomFrac)
        {
            float srcW = image.Width;
            float srcH = image.Height;

            var src = new SKRect(
                srcW * (float)Math.Clamp(cropLeftFrac, 0.0, 0.95),
                srcH * (float)Math.Clamp(cropTopFrac, 0.0, 0.95),
                srcW * (float)(1.0 - Math.Clamp(cropRightFrac, 0.0, 0.95)),
                srcH * (float)(1.0 - Math.Clamp(cropBottomFrac, 0.0, 0.95)));

            if (src.Right <= src.Left + 1f) src.Right = src.Left + 1f;
            if (src.Bottom <= src.Top + 1f) src.Bottom = src.Top + 1f;
            return src;
        }

        /// <summary>
        /// Прямоугольник, по которому идёт штрих линии контура. Кисть штрихует по
        /// центру линии, поэтому линия внутрь поджимает прямоугольник на половину
        /// толщины, а линия наружу — раздаёт на неё же. Одно правило на рамку
        /// картинки и обводку фигуры.
        /// </summary>
        public static SKRect OutlineRect(
            SKRect rect, double thicknessPt, ImageBorderAlign align)
        {
            float half = (float)thicknessPt / 2f;
            if (half <= 0f) return rect;

            float delta = align switch
            {
                ImageBorderAlign.Inside => -half,
                ImageBorderAlign.Outside => half,
                _ => 0f
            };
            if (delta == 0f) return rect;

            float left = rect.Left - delta;
            float top = rect.Top - delta;
            float right = rect.Right + delta;
            float bottom = rect.Bottom + delta;

            // Линия внутрь толще самого объекта вырождается в линию по его центру:
            // иначе стороны меняются местами и контур выворачивается наизнанку.
            if (right <= left) left = right = rect.MidX;
            if (bottom <= top) top = bottom = rect.MidY;

            return new SKRect(left, top, right, bottom);
        }

        /// <summary>
        /// Рисует фигуру в заданном прямоугольнике вместе с поворотом вокруг центра.
        ///
        /// fillImage — уже загруженная картинка-заливка (null, если её нет): грузить
        /// файлы отсюда нельзя, у экрана и у печати источники разные.
        /// alphaScale — множитель непрозрачности поверх собственной: экран гасит им
        /// объекты, промахнувшиеся мимо листа, печать всегда передаёт 1.
        /// </summary>
        public static void DrawShape(
            SKCanvas canvas, ShapeBlock shape, SKRect rect,
            SKImage? fillImage, double alphaScale = 1.0)
        {
            byte alpha = (byte)Math.Clamp(shape.Opacity * alphaScale * 255.0, 0.0, 255.0);
            if (alpha == 0) return;

            SKColor fillColor = default;
            bool hasFill = !string.IsNullOrEmpty(shape.FillColor)
                && SKColor.TryParse(shape.FillColor, out fillColor)
                && fillColor.Alpha > 0;

            SKColor strokeColor = default;
            bool hasStroke = shape.StrokeThicknessPt > 0.0
                && !string.IsNullOrEmpty(shape.StrokeColor)
                && SKColor.TryParse(shape.StrokeColor, out strokeColor)
                && strokeColor.Alpha > 0;

            bool hasImageFill = fillImage is not null && shape.IsClosedShape;

            if (!hasFill && !hasStroke && !hasImageFill) return;

            using var fillPaint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
            using var strokePaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                IsAntialias = true,
                StrokeCap = SKStrokeCap.Butt,
                StrokeJoin = SKStrokeJoin.Miter
            };
            using var imagePaint = new SKPaint { IsAntialias = true };

            if (hasFill)
                fillPaint.Color = fillColor.WithAlpha((byte)(fillColor.Alpha * alpha / 255));

            SKPathEffect? dash = null;
            if (hasStroke)
            {
                strokePaint.Color = strokeColor.WithAlpha((byte)(strokeColor.Alpha * alpha / 255));
                strokePaint.StrokeWidth = (float)shape.StrokeThicknessPt;
                dash = BuildDashEffect(shape.DashStyle, shape.StrokeThicknessPt);
                strokePaint.PathEffect = dash;
            }

            // Поворот и отражение — одно преобразование канваса, как у картинки
            // документа. Отражение осмысленно и у фигуры: им разворачивают выноску
            // хвостом в другую сторону, стрелку остриём назад и картинку-заливку.
            float rotDeg = (float)shape.RotationDeg;
            bool rotated = rotDeg != 0f;
            bool flipped = shape.FlipHorizontal || shape.FlipVertical;
            bool hasXform = rotated || flipped;
            if (hasXform)
            {
                canvas.Save();
                if (rotated) canvas.RotateDegrees(rotDeg, rect.MidX, rect.MidY);
                if (flipped)
                    canvas.Scale(
                        shape.FlipHorizontal ? -1f : 1f,
                        shape.FlipVertical ? -1f : 1f,
                        rect.MidX, rect.MidY);
            }

            try
            {
                if (shape.ShapeType is ShapeType.Line or ShapeType.Arrow)
                {
                    if (hasStroke) DrawShapeLine(canvas, shape, rect, fillPaint, strokePaint);
                    return;
                }

                using var path = BuildShapePath(shape, rect);
                if (path is null) return;

                if (hasFill) canvas.DrawPath(path, fillPaint);

                if (hasImageFill)
                {
                    // Картинка обрезается контуром фигуры: в эллипсе она круглая,
                    // в выноске — с хвостом. Клип снимается сразу после отрисовки.
                    canvas.Save();
                    canvas.ClipPath(path, SKClipOperation.Intersect, antialias: true);
                    imagePaint.Color = new SKColor(0xFF, 0xFF, 0xFF, alpha);

                    // Кадрирование заливки: рисуется только оставшаяся часть файла,
                    // и вписывается она по своим пропорциям, а не по полному размеру.
                    var srcRect = CropSrcRect(
                        fillImage!,
                        shape.CropLeftFrac, shape.CropTopFrac,
                        shape.CropRightFrac, shape.CropBottomFrac);

                    canvas.DrawImage(
                        fillImage!,
                        srcRect,
                        FillImageRect(
                            srcRect.Width, srcRect.Height, rect, shape.FillImageStretch),
                        Sampling, imagePaint);
                    canvas.Restore();
                }

                // Обводка идёт последней: она граница фигуры и не должна уходить
                // под заливку — ни цветную, ни картинкой. Прямоугольник штриха
                // сдвигается по положению обводки, как у рамки картинки.
                if (hasStroke)
                {
                    var strokeRect = OutlineRect(
                        rect, shape.StrokeThicknessPt, shape.StrokeAlign);

                    if (strokeRect == rect)
                    {
                        canvas.DrawPath(path, strokePaint);
                    }
                    else
                    {
                        using var strokePath = BuildShapePath(
                            shape.ShapeType, shape.CornerRadiusPt, strokeRect);
                        canvas.DrawPath(strokePath ?? path, strokePaint);
                    }
                }
            }
            finally
            {
                strokePaint.PathEffect = null;
                dash?.Dispose();
                if (hasXform) canvas.Restore();
            }
        }

        /// <summary>
        /// Линия и стрелка: горизонталь по середине габарита плюс наконечники.
        /// Любой другой угол задаётся поворотом фигуры — так прямоугольник
        /// выделения остаётся предсказуемым, а маркеры размера тянут линию
        /// вдоль неё самой.
        /// </summary>
        private static void DrawShapeLine(
            SKCanvas canvas, ShapeBlock shape, SKRect rect,
            SKPaint fillPaint, SKPaint strokePaint)
        {
            float cy = rect.MidY;
            float strokeW = (float)shape.StrokeThicknessPt;

            // Стрелка без явно выбранного наконечника всё равно рисуется со
            // стрелкой на конце: иначе она ничем не отличалась бы от линии.
            var startHead = shape.StartArrow;
            var endHead = shape.EndArrow;
            if (shape.ShapeType == ShapeType.Arrow
                && startHead == ShapeArrowHead.None && endHead == ShapeArrowHead.None)
                endHead = ShapeArrowHead.Triangle;

            float headLen = Math.Clamp(strokeW * 5f, 7f, Math.Max(rect.Width * 0.4f, 7f));

            // Древко не доводится до самого острия сплошного наконечника: иначе
            // конец линии выступает из него бугорком.
            float x1 = rect.Left + (startHead == ShapeArrowHead.Triangle ? headLen * 0.85f : 0f);
            float x2 = rect.Right - (endHead == ShapeArrowHead.Triangle ? headLen * 0.85f : 0f);
            if (x2 < x1) x2 = x1;

            canvas.DrawLine(x1, cy, x2, cy, strokePaint);

            DrawArrowHead(canvas, startHead, rect.Left, cy, -1f, headLen, strokeW, fillPaint, strokePaint);
            DrawArrowHead(canvas, endHead, rect.Right, cy, 1f, headLen, strokeW, fillPaint, strokePaint);
        }

        /// <summary>
        /// Наконечник в точке (tipX, cy). dir — направление, куда смотрит остриё:
        /// 1 вправо, -1 влево.
        /// </summary>
        private static void DrawArrowHead(
            SKCanvas canvas, ShapeArrowHead head,
            float tipX, float cy, float dir, float headLen, float strokeW,
            SKPaint fillPaint, SKPaint strokePaint)
        {
            if (head == ShapeArrowHead.None) return;

            float half = headLen * 0.45f;
            float baseX = tipX - dir * headLen;

            switch (head)
            {
                case ShapeArrowHead.Triangle:
                {
                    using var path = new SKPath();
                    path.MoveTo(tipX, cy);
                    path.LineTo(baseX, cy - half);
                    path.LineTo(baseX, cy + half);
                    path.Close();

                    // Наконечник — часть штриха, поэтому заливается цветом линии,
                    // а не заливкой фигуры.
                    var saved = fillPaint.Color;
                    fillPaint.Color = strokePaint.Color;
                    canvas.DrawPath(path, fillPaint);
                    fillPaint.Color = saved;
                    break;
                }

                case ShapeArrowHead.Open:
                {
                    // «Птичка» рисуется без пунктира: штриховой эффект разорвал бы
                    // короткие отрезки наконечника в пыль.
                    var savedEffect = strokePaint.PathEffect;
                    strokePaint.PathEffect = null;
                    canvas.DrawLine(tipX, cy, baseX, cy - half, strokePaint);
                    canvas.DrawLine(tipX, cy, baseX, cy + half, strokePaint);
                    strokePaint.PathEffect = savedEffect;
                    break;
                }

                case ShapeArrowHead.Circle:
                {
                    float r = Math.Max(strokeW * 1.8f, 3f);
                    var saved = fillPaint.Color;
                    fillPaint.Color = strokePaint.Color;
                    canvas.DrawCircle(tipX - dir * r, cy, r, fillPaint);
                    fillPaint.Color = saved;
                    break;
                }
            }
        }

        /// <summary>
        /// Рисует картинку документа с поворотом, отражением, обрезкой и рамкой.
        /// Повторяет экранный проход канваса — именно поэтому печать и экран
        /// показывают одно и то же.
        /// </summary>
        public static void DrawImage(
            SKCanvas canvas, ImageBlock block, SKRect rect, SKImage image)
        {
            byte alpha = (byte)Math.Clamp(block.Opacity * 255.0, 0.0, 255.0);
            if (alpha == 0) return;

            float rotDeg = (float)block.RotationDeg;
            bool hasXform = rotDeg != 0f || block.FlipHorizontal || block.FlipVertical;
            if (hasXform)
            {
                canvas.Save();
                if (rotDeg != 0f) canvas.RotateDegrees(rotDeg, rect.MidX, rect.MidY);
                if (block.FlipHorizontal || block.FlipVertical)
                    canvas.Scale(
                        block.FlipHorizontal ? -1f : 1f,
                        block.FlipVertical ? -1f : 1f,
                        rect.MidX, rect.MidY);
            }

            try
            {
                using var paint = new SKPaint
                {
                    IsAntialias = true,
                    Color = new SKColor(0xFF, 0xFF, 0xFF, alpha)
                };

                // Кадрирование: рисуется только видимая часть исходного изображения.
                var srcRect = CropSrcRect(
                    image,
                    block.CropLeftFrac, block.CropTopFrac,
                    block.CropRightFrac, block.CropBottomFrac);

                // Форма режет саму картинку: в эллипсе она круглая, в выноске — с
                // хвостом. Раньше по дуге шла только рамка, а углы картинки торчали
                // из-под неё.
                using var clip = block.HasShapeClip
                    ? BuildShapePath(block.ShapeType, block.CornerRadiusPt, rect)
                    : null;

                if (clip is not null)
                {
                    canvas.Save();
                    canvas.ClipPath(clip, SKClipOperation.Intersect, antialias: true);
                }

                canvas.DrawImage(image, srcRect, rect, Sampling, paint);

                if (clip is not null) canvas.Restore();

                DrawImageBorder(canvas, block, rect, alpha);
            }
            finally
            {
                if (hasXform) canvas.Restore();
            }
        }

        /// <summary>
        /// Рамка картинки: цвет, толщина, штрих и скругление. Прямоугольник штриха
        /// смещается по положению рамки — внутрь, по центру границы или наружу.
        /// </summary>
        public static void DrawImageBorder(
            SKCanvas canvas, ImageBlock block, SKRect rect, byte alpha)
        {
            if (block.BorderThicknessPt <= 0.0) return;
            if (string.IsNullOrEmpty(block.BorderColor)) return;
            if (!SKColor.TryParse(block.BorderColor, out var borderColor)) return;
            if (borderColor.Alpha == 0) return;

            using var paint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                IsAntialias = true,
                Color = borderColor.WithAlpha((byte)(borderColor.Alpha * alpha / 255)),
                StrokeWidth = (float)block.BorderThicknessPt
            };

            var dash = BuildDashEffect(block.BorderDashStyle, block.BorderThicknessPt);
            paint.PathEffect = dash;
            try
            {
                var borderRect = ImageBorderRect(rect, block);

                // Рамка идёт по контуру формы — по тому же, по которому обрезана
                // картинка. Иначе у эллипса рамка осталась бы прямоугольной.
                if (block.HasShapeClip)
                {
                    using var path = BuildShapePath(
                        block.ShapeType, block.CornerRadiusPt, borderRect);
                    if (path is not null) { canvas.DrawPath(path, paint); return; }
                }

                canvas.DrawRect(borderRect, paint);
            }
            finally
            {
                paint.PathEffect = null;
                dash?.Dispose();
            }
        }

        /// <summary>
        /// Прямоугольник, по которому идёт штрих рамки картинки. Правило общее
        /// с обводкой фигуры и живёт в OutlineRect — здесь только подстановка
        /// полей картинки, чтобы не менять вызовы снаружи.
        /// </summary>
        public static SKRect ImageBorderRect(SKRect rect, ImageBlock block)
            => OutlineRect(rect, block.BorderThicknessPt, block.BorderAlign);
    }
}
