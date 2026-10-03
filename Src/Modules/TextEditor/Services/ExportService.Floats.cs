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

                    long cx = (long)Math.Round(Math.Max(image.WidthPt, 1) * EmuPerPoint);
                    long cy = (long)Math.Round(Math.Max(image.HeightPt, 1) * EmuPerPoint);

                    uint drawingId = ctx.NextDrawingId++;
                    string name = string.IsNullOrWhiteSpace(image.ImageFileName)
                        ? "Picture " + drawingId.ToString(CultureInfo.InvariantCulture)
                        : image.ImageFileName;

                    var graphic = new Dr.Graphic(
                        new Dr.GraphicData(BuildPictureElement(image, relationshipId, cx, cy, name))
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

                    if (!string.IsNullOrEmpty(shape.FillImageFileName))
                        ctx.Warnings.Add("Картинка внутри фигуры в .docx не переносится: фигура ушла с заливкой цветом.");

                    var graphic = new Dr.Graphic(
                        new Dr.GraphicData(XmlElement(BuildShapeXml(shape, cx, cy)))
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
        /// одна на рисунок в строке и на плавающий.
        /// </summary>
        private static Pic.Picture BuildPictureElement(
            ImageBlock image, string relationshipId, long cx, long cy, string name)
        {
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
                new Pic.ShapeProperties(
                    BuildPictureTransform(image, cx, cy),
                    new Dr.PresetGeometry(new Dr.AdjustValueList())
                    {
                        Preset = new EnumValue<Dr.ShapeTypeValues>(Dr.ShapeTypeValues.Rectangle)
                    }));
        }

        /// <summary>
        /// Рисунок Word вокруг готовой графики: в строке (объект на собственной полосе)
        /// или якорем с положением и обтеканием (плавающий объект).
        /// </summary>
        private static W.Drawing BuildObjectDrawing(
            IFloatingObject obj, TableFloatPosition? anchorPosition,
            long cx, long cy, uint drawingId, string name, string? description,
            Dr.Graphic graphic)
        {
            var properties = new Wp.DocProperties
            {
                Id = (UInt32Value)drawingId,
                Name = name
            };
            if (!string.IsNullOrWhiteSpace(description))
                properties.Description = description;

            var frameProperties = new Wp.NonVisualGraphicFrameDrawingProperties(
                new Dr.GraphicFrameLocks { NoChangeAspect = obj.LockAspectRatio });

            if (obj.WrapMode == WrapMode.Inline)
            {
                return new W.Drawing(new Wp.Inline(
                    new Wp.Extent { Cx = cx, Cy = cy },
                    new Wp.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
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
                (RelativeHeightBase + Math.Clamp((long)obj.ZOrder, 0L, 1000000L))
                    .ToString(CultureInfo.InvariantCulture));
            SetPlainAttribute(anchor, "behindDoc", obj.WrapMode == WrapMode.Behind ? "1" : "0");
            SetPlainAttribute(anchor, "locked", "0");
            SetPlainAttribute(anchor, "layoutInCell", "1");
            SetPlainAttribute(anchor, "allowOverlap", "1");

            anchor.AppendChild(XmlElement(
                $"<wp:simplePos xmlns:wp=\"{WordprocessingDrawingNamespace}\" x=\"0\" y=\"0\"/>"));
            anchor.AppendChild(XmlElement(BuildHorizontalPositionXml(obj, anchorPosition)));
            anchor.AppendChild(XmlElement(BuildVerticalPositionXml(obj, anchorPosition)));
            anchor.AppendChild(new Wp.Extent { Cx = cx, Cy = cy });
            anchor.AppendChild(new Wp.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L });
            anchor.AppendChild(XmlElement(BuildWrapXml(obj)));
            anchor.AppendChild(properties);
            anchor.AppendChild(frameProperties);
            anchor.AppendChild(graphic);

            return new W.Drawing(anchor);
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
                relativeFrom = position.HorizontalAnchor switch
                {
                    TableFloatAnchor.Page => "page",
                    TableFloatAnchor.Margin => "margin",
                    _ => "column"
                };

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
                relativeFrom = position.VerticalAnchor switch
                {
                    TableFloatAnchor.Page => "page",
                    TableFloatAnchor.Margin => "margin",
                    _ => "paragraph"
                };

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

        /// <summary>Обтекание: вокруг рамки, по контуру либо без обтекания.</summary>
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

            return obj.WrapMode switch
            {
                WrapMode.Square => $"<wp:wrapSquare {ns} wrapText=\"{side}\"/>",

                // Контур обтекания у Word обязателен; у прямоугольной рамки он — сама рамка.
                WrapMode.Tight =>
                    $"<wp:wrapTight {ns} wrapText=\"{side}\"><wp:wrapPolygon edited=\"0\">"
                    + "<wp:start x=\"0\" y=\"0\"/><wp:lineTo x=\"0\" y=\"21600\"/>"
                    + "<wp:lineTo x=\"21600\" y=\"21600\"/><wp:lineTo x=\"21600\" y=\"0\"/>"
                    + "<wp:lineTo x=\"0\" y=\"0\"/></wp:wrapPolygon></wp:wrapTight>",

                _ => $"<wp:wrapNone {ns}/>"
            };
        }

        /// <summary>
        /// Фигура Word (wps:wsp): вид, поворот и отражение, заливка, обводка с
        /// наконечниками и текст внутри.
        /// </summary>
        private static string BuildShapeXml(ShapeBlock shape, long cx, long cy)
        {
            bool hasText = shape.IsClosedShape && !string.IsNullOrEmpty(shape.InnerText);

            var xml = new StringBuilder();
            xml.Append("<wps:wsp xmlns:wps=\"").Append(WordprocessingShapeNamespace)
               .Append("\" xmlns:a=\"").Append(DrawingMainNamespace)
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

            string preset = shape.ShapeType switch
            {
                ShapeType.Ellipse => "ellipse",
                ShapeType.Line or ShapeType.Arrow => "line",
                ShapeType.Callout => "wedgeRectCallout",
                _ => shape.CornerRadiusPt > 0.0 ? "roundRect" : "rect"
            };
            xml.Append("<a:prstGeom prst=\"").Append(preset).Append("\"><a:avLst/></a:prstGeom>");

            // Заливка.
            string? fill = shape.IsClosedShape ? OpaqueRgb(shape.FillColor) : null;
            if (fill is not null)
                xml.Append("<a:solidFill><a:srgbClr val=\"").Append(fill).Append("\"/></a:solidFill>");
            else
                xml.Append("<a:noFill/>");

            // Обводка и наконечники.
            string? stroke = shape.StrokeThicknessPt > 0.0 ? OpaqueRgb(shape.StrokeColor) : null;
            if (stroke is not null)
            {
                xml.Append("<a:ln w=\"").Append(EmuText(shape.StrokeThicknessPt)).Append("\">")
                   .Append("<a:solidFill><a:srgbClr val=\"").Append(stroke).Append("\"/></a:solidFill>");

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
