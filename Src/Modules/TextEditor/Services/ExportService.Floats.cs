using System;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Styles;
using W = DocumentFormat.OpenXml.Wordprocessing;
using Wp = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using Dr = DocumentFormat.OpenXml.Drawing;
using Pic = DocumentFormat.OpenXml.Drawing.Pictures;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Картинки и фигуры, стоящие в потоке документа блоками: картинка или фигура на
    /// собственной полосе и плавающие объекты с обтеканием.
    ///
    /// У Word такой объект живёт внутри абзаца. Объект на собственной полосе уходит
    /// отдельным абзацем с рисунком в строке (wp:inline). Плавающий уходит якорем
    /// (wp:anchor) в начало следующего абзаца — того самого, перед которым его блок
    /// стоит в потоке: от него Word отсчитывает положение «от абзаца».
    /// </summary>
    public sealed partial class ExportService
    {
        private const string WordprocessingDrawingNamespace =
            "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
        private const string DrawingMainNamespace =
            "http://schemas.openxmlformats.org/drawingml/2006/main";
        private const string WordprocessingShapeNamespace =
            "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";
        private const string WordprocessingMainNamespace =
            "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        private const string PictureGraphicUri =
            "http://schemas.openxmlformats.org/drawingml/2006/picture";
        private const string RelationshipsNamespace =
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

        /// <summary>Порядок наложения у Word (relativeHeight) начинается с этого числа.</summary>
        private const long RelativeHeightBase = 251658240L;

        /// <summary>
        /// Ран с рисунком для картинки или фигуры из потока блоков. Null — объект
        /// переносить нечем (нет файла картинки, пустой габарит, неизвестный блок).
        /// </summary>
        private W.Run? BuildFlowObjectRun(BlockModel block, DocxWriteContext ctx)
        {
            switch (block)
            {
                case ImageBlock image:
                {
                    string? relationshipId = EnsureImagePart(image, ctx);
                    if (relationshipId is null) return null;

                    // Нулевой габарит — рисунок Word без места на листе: уходит таким же.
                    long cx = (long)Math.Round(Math.Max(image.WidthPt, 0) * EmuPerPoint);
                    long cy = (long)Math.Round(Math.Max(image.HeightPt, 0) * EmuPerPoint);

                    uint drawingId = ctx.NextDrawingId++;
                    string name = string.IsNullOrWhiteSpace(image.ImageFileName)
                        ? "Picture " + drawingId.ToString(CultureInfo.InvariantCulture)
                        : image.ImageFileName;

                    var graphic = new Dr.Graphic(
                        new Dr.GraphicData(BuildPictureElement(image, relationshipId, cx, cy, name, ctx))
                        {
                            Uri = PictureGraphicUri
                        });

                    return new W.Run(BuildObjectDrawing(
                        image, image.AnchorPosition, cx, cy, drawingId, name, image.AltText, graphic));
                }

                case ShapeBlock shape:
                {
                    long cx = (long)Math.Round(Math.Max(shape.WidthPt, 1) * EmuPerPoint);
                    long cy = (long)Math.Round(Math.Max(shape.HeightPt, 1) * EmuPerPoint);

                    uint drawingId = ctx.NextDrawingId++;
                    string name = "Shape " + drawingId.ToString(CultureInfo.InvariantCulture);

                    // Картинка-заливка уходит в пакет отдельной частью, как обычная картинка.
                    string? fillRelationshipId = string.IsNullOrEmpty(shape.FillImageFileName)
                        ? null
                        : EnsureImageFilePart(shape.FillImageFileName!, ctx);

                    var graphic = new Dr.Graphic(
                        new Dr.GraphicData(XmlElement(BuildShapeXml(shape, cx, cy, fillRelationshipId, ctx)))
                        {
                            Uri = WordprocessingShapeNamespace
                        });

                    return new W.Run(BuildObjectDrawing(
                        shape, shape.AnchorPosition, cx, cy, drawingId, name, shape.AltText, graphic));
                }

                default:
                    return null;
            }
        }

        /// <summary>
        /// Картинка (pic:pic) со ссылкой на файл, обрезкой, поворотом и отражением —
        /// одна на рисунок в строке и на плавающий. Оформление картинки уходит в её
        /// свойства (pic:spPr): контур, по которому она обрезана, и рамка; прозрачность —
        /// в заливку (a:alphaModFix).
        /// </summary>
        private static Pic.Picture BuildPictureElement(
            ImageBlock image, string relationshipId, long cx, long cy, string name, DocxWriteContext ctx)
        {
            var properties = new Pic.ShapeProperties(
                BuildPictureTransform(image, cx, cy),
                BuildPictureGeometry(image));

            if (BuildPictureOutline(image, ctx) is { } outline)
                properties.AppendChild(outline);

            return new Pic.Picture(
                new Pic.NonVisualPictureProperties(
                    new Pic.NonVisualDrawingProperties
                    {
                        Id = (UInt32Value)0U,
                        Name = name,
                        Description = image.AltText ?? string.Empty
                    },
                    new Pic.NonVisualPictureDrawingProperties()),
                BuildPictureFill(image, relationshipId),
                properties);
        }

        /// <summary>
        /// Контур картинки: заготовка Word, пришедшая из .docx, пока её не меняли;
        /// иначе — по виду картинки в редакторе: эллипс, выноска, прямоугольник со
        /// скруглёнными углами или обычный прямоугольник.
        /// </summary>
        private static OpenXmlElement BuildPictureGeometry(ImageBlock image)
        {
            if (image.WordDrawing is { } word && word.PresetGeometryValidFor(image))
                return XmlElement(WithDrawingNamespace(word.PresetGeometryXml!));

            return XmlElement(PresetGeometryXml(image.ShapeType, image.CornerRadiusPt, image.WidthPt, image.HeightPt));
        }

        /// <summary>
        /// Заготовка контура (a:prstGeom) по виду объекта в редакторе. Скругление у
        /// Word — доля меньшей стороны в стотысячных (adj).
        /// </summary>
        private static string PresetGeometryXml(ShapeType shapeType, double cornerRadiusPt, double widthPt, double heightPt)
        {
            string ns = $"xmlns:a=\"{DrawingMainNamespace}\"";

            switch (shapeType)
            {
                case ShapeType.Ellipse:
                    return $"<a:prstGeom {ns} prst=\"ellipse\"><a:avLst/></a:prstGeom>";
                case ShapeType.Line:
                case ShapeType.Arrow:
                    return $"<a:prstGeom {ns} prst=\"line\"><a:avLst/></a:prstGeom>";
                case ShapeType.Callout:
                    return $"<a:prstGeom {ns} prst=\"wedgeRectCallout\"><a:avLst/></a:prstGeom>";
            }

            double side = Math.Min(widthPt, heightPt);
            if (cornerRadiusPt > 0.0 && side > 0.0)
            {
                long adj = (long)Math.Round(Math.Clamp(cornerRadiusPt / side, 0.0, 0.5) * 100000.0);
                return $"<a:prstGeom {ns} prst=\"roundRect\"><a:avLst><a:gd name=\"adj\" fmla=\"val {adj.ToString(CultureInfo.InvariantCulture)}\"/></a:avLst></a:prstGeom>";
            }

            return $"<a:prstGeom {ns} prst=\"rect\"><a:avLst/></a:prstGeom>";
        }

        /// <summary>
        /// Разметка, сохранённая из .docx, с объявлением пространства DrawingML на
        /// корне: внешний XML элемента несёт префикс «a», но объявление пространства
        /// у него было на предке.
        /// </summary>
        private static string WithDrawingNamespace(string outerXml)
        {
            if (outerXml.Contains("xmlns:a=", StringComparison.Ordinal)) return outerXml;

            int nameEnd = outerXml.IndexOfAny(new[] { ' ', '>', '/' }, 1);
            if (nameEnd < 0) return outerXml;

            return outerXml.Substring(0, nameEnd) + $" xmlns:a=\"{DrawingMainNamespace}\"" + outerXml.Substring(nameEnd);
        }

        /// <summary>
        /// Рамка картинки (a:ln). Линию снаружи рамки Word не рисует — у него линия
        /// только по центру границы или внутри неё; такая рамка уходит по центру, а
        /// человек получает предупреждение.
        /// </summary>
        private static Dr.Outline? BuildPictureOutline(ImageBlock image, DocxWriteContext ctx)
        {
            string? color = image.BorderThicknessPt > 0.0 ? OpaqueRgb(image.BorderColor) : null;
            if (color is null) return null;

            var outline = new Dr.Outline
            {
                Width = (Int32Value)(int)Math.Round(Math.Max(0.0, image.BorderThicknessPt) * EmuPerPoint)
            };

            if (image.BorderAlign == ImageBorderAlign.Inside)
                outline.Alignment = new EnumValue<Dr.PenAlignmentValues>(Dr.PenAlignmentValues.Insert);
            else if (image.BorderAlign == ImageBorderAlign.Outside)
                ctx.Warnings.Add("Рамка картинки снаружи её границы в Word нарисована по центру границы: такого положения рамки у Word нет.");

            outline.AppendChild(new Dr.SolidFill(new Dr.RgbColorModelHex { Val = color }));

            Dr.PresetLineDashValues? dash = image.BorderDashStyle switch
            {
                ShapeDashStyle.Dash => Dr.PresetLineDashValues.Dash,
                ShapeDashStyle.Dot => Dr.PresetLineDashValues.SystemDot,
                ShapeDashStyle.DashDot => Dr.PresetLineDashValues.DashDot,
                _ => null
            };
            if (dash is { } dashValue)
                outline.AppendChild(new Dr.PresetDash { Val = new EnumValue<Dr.PresetLineDashValues>(dashValue) });

            return outline;
        }

        /// <summary>
        /// Рисунок Word вокруг готовой графики: в строке (объект на собственной полосе)
        /// или якорем с положением и обтеканием (плавающий объект).
        ///
        /// Всё, что Word хранит о рисунке и что пришло с ним из .docx, уходит обратно:
        /// поля обрамления, имя, заголовок и скрытость, флаги якоря, «сквозное»
        /// обтекание и его контур, порядок наложения, точное имя опоры. Объект
        /// «сверху и снизу» уходит якорем, как был у Word, если topAndBottomAnchor.
        /// </summary>
        private static W.Drawing BuildObjectDrawing(
            IFloatingObject obj, TableFloatPosition? anchorPosition,
            long cx, long cy, uint drawingId, string name, string? description,
            Dr.Graphic graphic, bool topAndBottomAnchor = true)
        {
            var word = obj.WordDrawing;

            var properties = new Wp.DocProperties
            {
                Id = (UInt32Value)drawingId,
                Name = string.IsNullOrWhiteSpace(word?.Name) ? name : word!.Name
            };
            if (!string.IsNullOrWhiteSpace(description))
                properties.Description = description;
            if (!string.IsNullOrWhiteSpace(word?.Title))
                properties.Title = word!.Title;
            if (word is { Hidden: true })
                properties.Hidden = true;

            var frameProperties = new Wp.NonVisualGraphicFrameDrawingProperties(
                new Dr.GraphicFrameLocks { NoChangeAspect = obj.LockAspectRatio });

            var (effectLeft, effectTop, effectRight, effectBottom) = FloatingObjectBox.EffectExtentPt(obj);
            Wp.EffectExtent BuildEffectExtent() => new()
            {
                LeftEdge = SignedEmu(effectLeft),
                TopEdge = SignedEmu(effectTop),
                RightEdge = SignedEmu(effectRight),
                BottomEdge = SignedEmu(effectBottom)
            };

            bool topAndBottom = obj.WrapMode == WrapMode.Inline
                && topAndBottomAnchor
                && word is { WrapTopAndBottom: true };

            if (obj.WrapMode == WrapMode.Inline && !topAndBottom)
            {
                return new W.Drawing(new Wp.Inline(
                    new Wp.Extent { Cx = cx, Cy = cy },
                    BuildEffectExtent(),
                    properties,
                    frameProperties,
                    graphic));
            }

            var anchor = new Wp.Anchor();
            SetPlainAttribute(anchor, "distT", EmuText(obj.WrapPadTopPt));
            SetPlainAttribute(anchor, "distB", EmuText(obj.WrapPadBottomPt));
            SetPlainAttribute(anchor, "distL", EmuText(obj.WrapPadLeftPt));
            SetPlainAttribute(anchor, "distR", EmuText(obj.WrapPadRightPt));
            SetPlainAttribute(anchor, "simplePos", "0");
            SetPlainAttribute(anchor, "relativeHeight",
                RelativeHeightFor(obj).ToString(CultureInfo.InvariantCulture));
            bool behindDoc = obj.WrapMode == WrapMode.Behind
                || (obj.WrapMode is WrapMode.Square or WrapMode.Tight && word is { BehindDoc: true });
            SetPlainAttribute(anchor, "behindDoc", behindDoc ? "1" : "0");
            SetPlainAttribute(anchor, "locked", word is { Locked: true } ? "1" : "0");
            SetPlainAttribute(anchor, "layoutInCell", word is null || word.LayoutInCell ? "1" : "0");
            SetPlainAttribute(anchor, "allowOverlap", word is null || word.AllowOverlap ? "1" : "0");

            var position = topAndBottom
                ? TopAndBottomPositionFor(obj, word!.TopAndBottomPosition)
                : anchorPosition;

            anchor.AppendChild(XmlElement(
                $"<wp:simplePos xmlns:wp=\"{WordprocessingDrawingNamespace}\" x=\"0\" y=\"0\"/>"));
            anchor.AppendChild(XmlElement(BuildHorizontalPositionXml(obj, position)));
            anchor.AppendChild(XmlElement(BuildVerticalPositionXml(obj, position)));
            anchor.AppendChild(new Wp.Extent { Cx = cx, Cy = cy });
            anchor.AppendChild(BuildEffectExtent());
            anchor.AppendChild(XmlElement(topAndBottom
                ? $"<wp:wrapTopAndBottom xmlns:wp=\"{WordprocessingDrawingNamespace}\"/>"
                : BuildWrapXml(obj)));
            anchor.AppendChild(properties);
            anchor.AppendChild(frameProperties);
            anchor.AppendChild(graphic);

            return new W.Drawing(anchor);
        }

        /// <summary>Длина в EMU со знаком: поля обрамления бывают отрицательными.</summary>
        private static long SignedEmu(double points) => (long)Math.Round(points * EmuPerPoint);

        /// <summary>
        /// Порядок наложения для Word. Запись из .docx уходит как была, пока порядок
        /// объекта в редакторе не меняли. Порядок, заведомо взятый из Word (большое
        /// число), уходит как есть; порядок редактора отсчитывается от начала шкалы Word.
        /// </summary>
        private static long RelativeHeightFor(IFloatingObject obj)
        {
            if (obj.WordDrawing is { RelativeHeight: > 0 } word && word.RelativeHeightForZOrder == obj.ZOrder)
                return word.RelativeHeight;

            if (obj.ZOrder >= 1000000)
                return obj.ZOrder;

            return RelativeHeightBase + Math.Clamp((long)obj.ZOrder, 0L, 1000000L);
        }

        /// <summary>
        /// Положение объекта «сверху и снизу» для Word: исходное, а сторона по
        /// горизонтали — по выравниванию объекта в редакторе, если его меняли.
        /// </summary>
        private static TableFloatPosition TopAndBottomPositionFor(IFloatingObject obj, TableFloatPosition? source)
        {
            var position = source?.Clone() ?? new TableFloatPosition
            {
                HorizontalAnchor = TableFloatAnchor.Text,
                VerticalAnchor = TableFloatAnchor.Text,
                VerticalAlign = TableFloatAlign.Offset
            };

            bool keepOffset = position.HorizontalAlign == TableFloatAlign.Offset
                && obj.Alignment == TextAlignment.Left;

            if (!keepOffset)
            {
                position.HorizontalAlign = obj.Alignment switch
                {
                    TextAlignment.Center => TableFloatAlign.Center,
                    TextAlignment.Right => TableFloatAlign.End,
                    _ => TableFloatAlign.Start
                };
            }

            return position;
        }

        /// <summary>
        /// Имя опоры для Word. Когда модель различает опору грубее, чем Word, — колонка,
        /// поле и знак для неё одна полоса набора, абзац и строка одна опора, — уходит
        /// имя, пришедшее из .docx, если оно описывает ту же опору.
        /// </summary>
        private static string RelativeFromName(TableFloatAnchor anchor, string? sourceName, bool horizontal)
        {
            string name = anchor switch
            {
                TableFloatAnchor.Page => "page",
                TableFloatAnchor.Margin => "margin",
                TableFloatAnchor.LeftMargin => "leftMargin",
                TableFloatAnchor.RightMargin => "rightMargin",
                TableFloatAnchor.InsideMargin => "insideMargin",
                TableFloatAnchor.OutsideMargin => "outsideMargin",
                TableFloatAnchor.TopMargin => "topMargin",
                TableFloatAnchor.BottomMargin => "bottomMargin",
                _ => horizontal ? "column" : "paragraph"
            };

            if (sourceName is null) return name;

            bool sameAnchor = horizontal
                ? anchor == TableFloatAnchor.Text && sourceName is "column" or "margin" or "character"
                : anchor == TableFloatAnchor.Text && sourceName is "paragraph" or "line";

            return sameAnchor ? sourceName : name;
        }

        /// <summary>
        /// Положение по горизонтали. Объект с опорой из Word уходит с ней же; объект,
        /// вставленный в редакторе, отсчитывается от левого поля — так же, как на листе.
        /// </summary>
        private static string BuildHorizontalPositionXml(IFloatingObject obj, TableFloatPosition? position)
        {
            string relativeFrom = "margin";
            string? align = null;
            double offsetPt = obj.OffsetXPt;

            if (position is not null)
            {
                relativeFrom = RelativeFromName(
                    position.HorizontalAnchor, obj.WordDrawing?.HorizontalRelativeFrom, horizontal: true);

                // Сторона опоры сохраняется, пока объект с неё не сдвинули: смещение
                // от стороны Word записать не умеет.
                bool moved = Math.Abs(obj.OffsetXPt) > 0.05;
                align = moved
                    ? null
                    : position.HorizontalAlign switch
                    {
                        TableFloatAlign.Start => "left",
                        TableFloatAlign.Center => "center",
                        TableFloatAlign.End => "right",
                        _ => null
                    };

                offsetPt = (position.HorizontalAlign == TableFloatAlign.Offset ? position.XPt : 0.0)
                    + obj.OffsetXPt;
            }

            return PositionXml("positionH", relativeFrom, align, offsetPt);
        }

        /// <summary>Положение по вертикали: от абзаца, от полей или от листа.</summary>
        private static string BuildVerticalPositionXml(IFloatingObject obj, TableFloatPosition? position)
        {
            string relativeFrom = "margin";
            string? align = null;
            double offsetPt = obj.OffsetYPt;

            if (position is not null)
            {
                relativeFrom = RelativeFromName(
                    position.VerticalAnchor, obj.WordDrawing?.VerticalRelativeFrom, horizontal: false);

                bool moved = Math.Abs(obj.OffsetYPt) > 0.05;
                align = moved || position.VerticalAnchor == TableFloatAnchor.Text
                    ? null
                    : position.VerticalAlign switch
                    {
                        TableFloatAlign.Start => "top",
                        TableFloatAlign.Center => "center",
                        TableFloatAlign.End => "bottom",
                        _ => null
                    };

                offsetPt = (position.VerticalAlign == TableFloatAlign.Offset ? position.YPt : 0.0)
                    + obj.OffsetYPt;
            }

            return PositionXml("positionV", relativeFrom, align, offsetPt);
        }

        private static string PositionXml(string element, string relativeFrom, string? align, double offsetPt)
        {
            string inner = align is not null
                ? $"<wp:align>{align}</wp:align>"
                : $"<wp:posOffset>{EmuText(offsetPt, allowNegative: true)}</wp:posOffset>";

            return $"<wp:{element} xmlns:wp=\"{WordprocessingDrawingNamespace}\" relativeFrom=\"{relativeFrom}\">{inner}</wp:{element}>";
        }

        /// <summary>
        /// Обтекание: вокруг рамки, по контуру, сквозное либо без обтекания. Контур
        /// обтекания — пришедший из Word, иначе сама рамка объекта.
        /// </summary>
        private static string BuildWrapXml(IFloatingObject obj)
        {
            string side = obj.WrapSide switch
            {
                WrapSide.BothSides => "bothSides",
                WrapSide.LeftOnly => "left",
                WrapSide.RightOnly => "right",
                _ => "largest"
            };

            string ns = $"xmlns:wp=\"{WordprocessingDrawingNamespace}\"";

            if (obj.WrapMode == WrapMode.Tight)
            {
                var word = obj.WordDrawing;
                string element = word is { WrapThrough: true } ? "wrapThrough" : "wrapTight";
                string edited = word is { WrapPolygonEdited: true } ? "1" : "0";

                var polygon = new StringBuilder();
                polygon.Append("<wp:wrapPolygon edited=\"").Append(edited).Append("\">");

                if (word?.WrapPolygon is { Count: >= 3 } points)
                {
                    for (int i = 0; i < points.Count; i++)
                    {
                        polygon.Append(i == 0 ? "<wp:start x=\"" : "<wp:lineTo x=\"")
                            .Append(points[i].X.ToString(CultureInfo.InvariantCulture))
                            .Append("\" y=\"")
                            .Append(points[i].Y.ToString(CultureInfo.InvariantCulture))
                            .Append("\"/>");
                    }
                }
                else
                {
                    // Контур обтекания у Word обязателен; у прямоугольной рамки он — сама рамка.
                    polygon.Append("<wp:start x=\"0\" y=\"0\"/><wp:lineTo x=\"0\" y=\"21600\"/>")
                        .Append("<wp:lineTo x=\"21600\" y=\"21600\"/><wp:lineTo x=\"21600\" y=\"0\"/>")
                        .Append("<wp:lineTo x=\"0\" y=\"0\"/>");
                }

                polygon.Append("</wp:wrapPolygon>");

                return $"<wp:{element} {ns} wrapText=\"{side}\">{polygon}</wp:{element}>";
            }

            return obj.WrapMode switch
            {
                WrapMode.Square => $"<wp:wrapSquare {ns} wrapText=\"{side}\"/>",
                _ => $"<wp:wrapNone {ns}/>"
            };
        }

        /// <summary>
        /// Фигура Word (wps:wsp): вид, поворот и отражение, заливка, обводка с
        /// наконечниками и текст внутри.
        /// </summary>
        private static string BuildShapeXml(
            ShapeBlock shape, long cx, long cy, string? fillRelationshipId, DocxWriteContext ctx)
        {
            bool hasText = shape.IsClosedShape && !string.IsNullOrEmpty(shape.InnerText);

            // Непрозрачность фигуры у Word — прозрачность её заливки и обводки.
            double opacity = Math.Clamp(shape.Opacity, 0.0, 1.0);

            var xml = new StringBuilder();
            xml.Append("<wps:wsp xmlns:wps=\"").Append(WordprocessingShapeNamespace)
               .Append("\" xmlns:a=\"").Append(DrawingMainNamespace)
               .Append("\" xmlns:r=\"").Append(RelationshipsNamespace)
               .Append("\" xmlns:w=\"").Append(WordprocessingMainNamespace).Append("\">");

            xml.Append(hasText ? "<wps:cNvSpPr txBox=\"1\"/>" : "<wps:cNvSpPr/>");

            // Вид, поворот и отражение.
            xml.Append("<wps:spPr><a:xfrm");
            double degrees = shape.RotationDeg % 360.0;
            if (degrees < 0) degrees += 360.0;
            if (degrees != 0.0)
                xml.Append(" rot=\"")
                   .Append(((long)Math.Round(degrees * 60000.0)).ToString(CultureInfo.InvariantCulture))
                   .Append('"');
            if (shape.FlipHorizontal) xml.Append(" flipH=\"1\"");
            if (shape.FlipVertical) xml.Append(" flipV=\"1\"");
            xml.Append("><a:off x=\"0\" y=\"0\"/><a:ext cx=\"")
               .Append(cx.ToString(CultureInfo.InvariantCulture)).Append("\" cy=\"")
               .Append(cy.ToString(CultureInfo.InvariantCulture)).Append("\"/></a:xfrm>");

            // Контур: заготовка Word, пришедшая из .docx, пока её не меняли; иначе — по
            // виду фигуры в редакторе.
            if (shape.WordDrawing is { } word && word.PresetGeometryValidFor(shape))
                xml.Append(WithDrawingNamespace(word.PresetGeometryXml!));
            else
                xml.Append(PresetGeometryXml(shape.ShapeType, shape.CornerRadiusPt, shape.WidthPt, shape.HeightPt));

            // Заливка: картинка, цвет или ничего.
            string? fill = shape.IsClosedShape ? OpaqueRgb(shape.FillColor) : null;
            if (shape.IsClosedShape && fillRelationshipId is not null)
            {
                xml.Append("<a:blipFill rotWithShape=\"1\"><a:blip r:embed=\"").Append(fillRelationshipId).Append('"');
                if (opacity < 1.0)
                    xml.Append("><a:alphaModFix amt=\"").Append(AlphaUnits(opacity)).Append("\"/></a:blip>");
                else
                    xml.Append("/>");

                bool cropped = shape.CropLeftFrac > 0 || shape.CropTopFrac > 0
                    || shape.CropRightFrac > 0 || shape.CropBottomFrac > 0;
                if (cropped)
                {
                    xml.Append("<a:srcRect l=\"").Append(CropUnits(shape.CropLeftFrac).ToString(CultureInfo.InvariantCulture))
                       .Append("\" t=\"").Append(CropUnits(shape.CropTopFrac).ToString(CultureInfo.InvariantCulture))
                       .Append("\" r=\"").Append(CropUnits(shape.CropRightFrac).ToString(CultureInfo.InvariantCulture))
                       .Append("\" b=\"").Append(CropUnits(shape.CropBottomFrac).ToString(CultureInfo.InvariantCulture))
                       .Append("\"/>");
                }

                xml.Append("<a:stretch><a:fillRect/></a:stretch></a:blipFill>");

                if (!shape.FillImageStretch)
                    ctx.Warnings.Add("Картинка-заливка фигуры «вписать с сохранением пропорций» в Word растянута на всю фигуру: такого режима заливки у Word нет.");
            }
            else if (fill is not null)
            {
                xml.Append("<a:solidFill>").Append(ColorXml(fill, opacity * ColorAlpha(shape.FillColor))).Append("</a:solidFill>");
            }
            else
            {
                xml.Append("<a:noFill/>");
            }

            // Обводка и наконечники.
            string? stroke = shape.StrokeThicknessPt > 0.0 ? OpaqueRgb(shape.StrokeColor) : null;
            if (stroke is not null)
            {
                xml.Append("<a:ln w=\"").Append(EmuText(shape.StrokeThicknessPt)).Append('"');
                if (shape.StrokeAlign == ImageBorderAlign.Inside)
                    xml.Append(" algn=\"in\"");
                else if (shape.StrokeAlign == ImageBorderAlign.Outside)
                    ctx.Warnings.Add("Обводка фигуры снаружи контура в Word нарисована по центру контура: такого положения обводки у Word нет.");
                xml.Append('>')
                   .Append("<a:solidFill>").Append(ColorXml(stroke, opacity * ColorAlpha(shape.StrokeColor))).Append("</a:solidFill>");

                string? dash = shape.DashStyle switch
                {
                    ShapeDashStyle.Dash => "dash",
                    ShapeDashStyle.Dot => "sysDot",
                    ShapeDashStyle.DashDot => "dashDot",
                    _ => null
                };
                if (dash is not null)
                    xml.Append("<a:prstDash val=\"").Append(dash).Append("\"/>");

                if (!shape.IsClosedShape)
                {
                    var startHead = shape.StartArrow;
                    var endHead = shape.EndArrow;

                    // Стрелка без выбранного наконечника рисуется со стрелкой на конце.
                    if (shape.ShapeType == ShapeType.Arrow
                        && startHead == ShapeArrowHead.None && endHead == ShapeArrowHead.None)
                        endHead = ShapeArrowHead.Triangle;

                    if (ArrowHeadName(startHead) is { } headName)
                        xml.Append("<a:headEnd type=\"").Append(headName).Append("\"/>");
                    if (ArrowHeadName(endHead) is { } tailName)
                        xml.Append("<a:tailEnd type=\"").Append(tailName).Append("\"/>");
                }

                xml.Append("</a:ln>");
            }
            else
            {
                xml.Append("<a:ln><a:noFill/></a:ln>");
            }

            xml.Append("</wps:spPr>");

            // Текст: абзац на каждую строку надписи.
            if (hasText)
            {
                string justification = shape.TextAlign switch
                {
                    TextAlignment.Center => "center",
                    TextAlignment.Right => "right",
                    _ => "left"
                };

                var runProperties = new StringBuilder("<w:rPr>");
                if (!string.IsNullOrWhiteSpace(shape.TextFontFamily))
                {
                    string font = XmlEscape(shape.TextFontFamily!);
                    runProperties.Append("<w:rFonts w:ascii=\"").Append(font)
                        .Append("\" w:hAnsi=\"").Append(font)
                        .Append("\" w:cs=\"").Append(font).Append("\"/>");
                }
                if (shape.TextBold) runProperties.Append("<w:b/>");
                if (shape.TextItalic) runProperties.Append("<w:i/>");
                if (OpaqueRgb(shape.TextColor) is { } textColor)
                    runProperties.Append("<w:color w:val=\"").Append(textColor).Append("\"/>");

                // Кегль у Word — в полупунктах.
                string halfPoints = ((long)Math.Round(Math.Clamp(shape.TextSizePt, 1.0, 400.0) * 2.0))
                    .ToString(CultureInfo.InvariantCulture);
                runProperties.Append("<w:sz w:val=\"").Append(halfPoints).Append("\"/>")
                    .Append("<w:szCs w:val=\"").Append(halfPoints).Append("\"/></w:rPr>");

                xml.Append("<wps:txbx><w:txbxContent>");
                foreach (var line in shape.InnerText!.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                {
                    xml.Append("<w:p><w:pPr><w:jc w:val=\"").Append(justification).Append("\"/></w:pPr>");
                    if (line.Length > 0)
                    {
                        xml.Append("<w:r>").Append(runProperties)
                           .Append("<w:t xml:space=\"preserve\">").Append(XmlEscape(line)).Append("</w:t></w:r>");
                    }
                    xml.Append("</w:p>");
                }
                xml.Append("</w:txbxContent></wps:txbx>");
            }

            string insetX = EmuText(shape.TextInsetHorizontalPt);
            string insetY = EmuText(shape.TextInsetVerticalPt);
            string verticalAnchor = shape.TextVerticalAlign switch
            {
                VerticalAlignment.Middle => "ctr",
                VerticalAlignment.Bottom => "b",
                _ => "t"
            };

            xml.Append("<wps:bodyPr rot=\"0\" vert=\"horz\" wrap=\"square\" lIns=\"").Append(insetX)
               .Append("\" tIns=\"").Append(insetY)
               .Append("\" rIns=\"").Append(insetX)
               .Append("\" bIns=\"").Append(insetY)
               .Append("\" anchor=\"").Append(verticalAnchor)
               .Append("\"><a:noAutofit/></wps:bodyPr></wps:wsp>");

            return xml.ToString();
        }

        /// <summary>
        /// Цвет DrawingML (a:srgbClr) с прозрачностью: альфа — доля непрозрачности
        /// (1 — непрозрачен), у Word — в стотысячных.
        /// </summary>
        private static string ColorXml(string rgb, double alpha)
        {
            if (alpha >= 0.9999)
                return "<a:srgbClr val=\"" + rgb + "\"/>";

            return "<a:srgbClr val=\"" + rgb + "\"><a:alpha val=\"" + AlphaUnits(alpha) + "\"/></a:srgbClr>";
        }

        /// <summary>Непрозрачность в стотысячных, как её пишет Word.</summary>
        private static string AlphaUnits(double alpha) =>
            ((long)Math.Round(Math.Clamp(alpha, 0.0, 1.0) * 100000.0)).ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Непрозрачность цвета «#AARRGGBB» (0..1). У цвета без альфы — 1.
        /// </summary>
        private static double ColorAlpha(string? color)
        {
            if (string.IsNullOrWhiteSpace(color)) return 1.0;

            string hex = color.Trim().TrimStart('#');
            if (hex.Length != 8) return 1.0;

            return int.TryParse(hex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int alpha)
                ? alpha / 255.0
                : 1.0;
        }

        private static string? ArrowHeadName(ShapeArrowHead head) => head switch
        {
            ShapeArrowHead.Triangle => "triangle",
            ShapeArrowHead.Open => "arrow",
            ShapeArrowHead.Circle => "oval",
            _ => null
        };

        /// <summary>
        /// Цвет в виде «RRGGBB» для Word. Null — цвета нет или он полностью прозрачен.
        /// Прозрачность из «#AARRGGBB» в .docx не переносится.
        /// </summary>
        private static string? OpaqueRgb(string? color)
        {
            if (string.IsNullOrWhiteSpace(color)) return null;

            string hex = color.Trim().TrimStart('#');
            if (hex.Length == 8)
            {
                if (hex.StartsWith("00", StringComparison.Ordinal)) return null;
                hex = hex.Substring(2);
            }
            if (hex.Length != 6) return null;

            foreach (char c in hex)
                if (!Uri.IsHexDigit(c)) return null;

            return hex.ToUpperInvariant();
        }

        private static string EmuText(double points, bool allowNegative = false)
        {
            double emu = Math.Round(points * EmuPerPoint);
            if (!allowNegative && emu < 0) emu = 0;
            return ((long)emu).ToString(CultureInfo.InvariantCulture);
        }

        private static string XmlEscape(string text)
        {
            var escaped = new StringBuilder(text.Length + 8);
            foreach (char c in text)
            {
                switch (c)
                {
                    case '&': escaped.Append("&amp;"); break;
                    case '<': escaped.Append("&lt;"); break;
                    case '>': escaped.Append("&gt;"); break;
                    case '"': escaped.Append("&quot;"); break;
                    default:
                        // Управляющие знаки в XML недопустимы; табуляция остаётся.
                        if (c >= ' ' || c == '\t') escaped.Append(c);
                        break;
                }
            }
            return escaped.ToString();
        }

        /// <summary>
        /// Элемент из готовой разметки. В OpenXML SDK 3 фабрики из строки нет:
        /// корень собирается вручную — имя, объявления пространств и атрибуты, —
        /// а содержимое отдаётся разбору самого SDK через InnerXml. Разметка
        /// здесь всегда своя и самодостаточна: каждый корень объявляет свои
        /// пространства имён.
        /// </summary>
        private static OpenXmlElement XmlElement(string outerXml)
        {
            var parsed = XElement.Parse(outerXml);
            var name = parsed.Name;
            var prefix = parsed.GetPrefixOfNamespace(name.Namespace) ?? string.Empty;

            var element = new OpenXmlUnknownElement(prefix, name.LocalName, name.NamespaceName);

            foreach (var attribute in parsed.Attributes())
            {
                if (attribute.IsNamespaceDeclaration)
                {
                    var declared = attribute.Name.Namespace == XNamespace.None ? string.Empty : attribute.Name.LocalName;
                    element.AddNamespaceDeclaration(declared, attribute.Value);
                    continue;
                }

                var attributeNamespace = attribute.Name.Namespace;
                var attributePrefix = attributeNamespace == XNamespace.None
                    ? string.Empty
                    : parsed.GetPrefixOfNamespace(attributeNamespace) ?? string.Empty;

                element.SetAttribute(new OpenXmlAttribute(
                    attributePrefix, attribute.Name.LocalName, attributeNamespace.NamespaceName, attribute.Value));
            }

            var inner = new StringBuilder();
            foreach (var node in parsed.Nodes())
                inner.Append(node.ToString(SaveOptions.DisableFormatting));

            if (inner.Length > 0) element.InnerXml = inner.ToString();
            return element;
        }

        private static void SetPlainAttribute(OpenXmlElement element, string name, string value) =>
            element.SetAttribute(new OpenXmlAttribute(name, string.Empty, value));
    }
}
