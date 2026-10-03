using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.Models.Styles;
using W = DocumentFormat.OpenXml.Wordprocessing;
using Wp = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using Dr = DocumentFormat.OpenXml.Drawing;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Картинки документа Word: в строке текста (wp:inline), плавающие (wp:anchor) и
    /// старого вида (w:pict с v:imagedata).
    ///
    /// Плавающая картинка у Word привязана к абзацу и стоит в своей точке листа; текст
    /// её обтекает, проходит под ней или над ней. В Writersword это блок-картинка в
    /// потоке документа: она ставится прямо перед своим абзацем и несёт положение
    /// относительно опоры (<see cref="ImageBlock.AnchorPosition"/>) — тогда раскладка
    /// знает и страницу, и верх абзаца, от которого Word отсчитывает смещение.
    /// </summary>
    public sealed partial class ImportService
    {
        /// <summary>
        /// Можно ли сейчас ставить плавающие картинки блоками. Истинно только на время
        /// разбора абзаца основного потока: у абзаца ячейки таблицы и колонтитула
        /// потока блоков нет, там картинка остаётся в строке.
        /// </summary>
        private bool _floatingAllowed;

        /// <summary>Плавающие картинки абзаца, который сейчас разбирается.</summary>
        private readonly List<BlockModel> _pendingFloats = new();

        /// <summary>
        /// Файл картинки по части пакета: одна и та же часть, на которую документ
        /// ссылается много раз, кладётся в проект один раз. Пустое имя — часть уже
        /// разбиралась и показать её нечем.
        /// </summary>
        private readonly Dictionary<string, string> _imageFileByPart = new();

        /// <summary>Предупреждение о картинке, оставшейся в строке, выдаётся один раз.</summary>
        private bool _floatKeptInlineWarned;

        private const string RelationshipsNamespace =
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

        /// <summary>Доля обрезки у Word — в тысячных долях процента.</summary>
        private const double CropUnitsPerWhole = 100000.0;

        /// <summary>Угол у Word — в 60000-х долях градуса.</summary>
        private const double RotationUnitsPerDegree = 60000.0;

        /// <summary>Сколько картинки обязано остаться после обрезки с двух сторон.</summary>
        private const double MaxCropFrac = 0.95;

        private void ImportDrawing(
            W.Drawing drawing,
            TextChunk chunk,
            SectionModel section,
            RunProperties runProps,
            DocxFormatResolver resolver,
            MainDocumentPart mainPart,
            Dictionary<string, byte[]> extractedImages,
            List<string> warnings)
        {
            Wp.Inline? inline = drawing.Inline;
            Wp.Anchor? anchor = drawing.Anchor;

            // У плавающего объекта (wp:anchor) графика лежит дочерним элементом:
            // отдельного свойства, как у wp:inline, у него нет.
            Dr.Graphic? graphic = inline?.Graphic ?? anchor?.GetFirstChild<Dr.Graphic>();
            long extentCx = inline?.Extent?.Cx ?? anchor?.Extent?.Cx ?? 0;
            long extentCy = inline?.Extent?.Cy ?? anchor?.Extent?.Cy ?? 0;

            var blip = graphic?.GraphicData?.Descendants<Dr.Blip>().FirstOrDefault();
            string? relId = blip?.Embed?.Value;
            if (string.IsNullOrEmpty(relId))
            {
                // Не картинка. Фигура Word (wps:wsp) переносится фигурой; диаграмма и
                // объект OLE пропускаются молча — текста документа в них нет.
                ImportWordShape(drawing, chunk, section, runProps, resolver, warnings);
                return;
            }

            string? fileName = ExtractImagePart(mainPart, relId!, extractedImages, warnings);
            if (fileName is null) return;

            var image = new ImageBlock
            {
                ImageFileName = fileName,
                WidthPt = extentCx > 0 ? extentCx / EmuPerPoint : 100,
                HeightPt = extentCy > 0 ? extentCy / EmuPerPoint : 100,
                WrapMode = WrapMode.Inline
            };

            ApplyPictureGeometry(image, graphic!);

            OpenXmlElement? container = (OpenXmlElement?)inline ?? anchor;
            string? description = container is null ? null : AttributeOf(ChildByName(container, "docPr"), "descr");
            if (!string.IsNullOrWhiteSpace(description))
                image.AltText = description;

            if (anchor is not null)
            {
                if (_floatingAllowed)
                {
                    image.AnchorPosition = ApplyAnchorPlacement(image, anchor);
                    _pendingFloats.Add(image);
                    return;
                }

                if (!_floatKeptInlineWarned)
                {
                    _floatKeptInlineWarned = true;
                    warnings.Add("Плавающие картинки внутри таблиц и колонтитулов вставлены как обычные (в тексте): обтекание там не переносится.");
                }
            }

            AppendInlineImage(image, chunk, section, runProps);
        }

        /// <summary>
        /// Картинка старого вида: w:pict с v:shape, внутри которого v:imagedata ссылается
        /// на файл. Так Word хранит рисунки из старых документов и часть вставленных
        /// объектов. Размер записан в стиле фигуры (width, height).
        /// </summary>
        private void ImportVmlPicture(
            OpenXmlElement picture,
            TextChunk chunk,
            SectionModel section,
            RunProperties runProps,
            MainDocumentPart mainPart,
            Dictionary<string, byte[]> extractedImages,
            List<string> warnings)
        {
            OpenXmlElement? imageData = null;
            foreach (var element in picture.Descendants())
            {
                if (element.LocalName != "imagedata") continue;
                imageData = element;
                break;
            }
            if (imageData is null) return; // фигура без картинки: текста в ней для потока нет

            string? relId = null;
            foreach (var attribute in imageData.GetAttributes())
            {
                if (attribute.LocalName != "id" || attribute.NamespaceUri != RelationshipsNamespace) continue;
                relId = attribute.Value;
                break;
            }
            if (string.IsNullOrEmpty(relId)) return;

            string? fileName = ExtractImagePart(mainPart, relId!, extractedImages, warnings);
            if (fileName is null) return;

            // Размер — в стиле фигуры, внутри которой лежит картинка.
            double widthPt = 0, heightPt = 0;
            for (OpenXmlElement? shape = imageData.Parent; shape is not null; shape = shape.Parent)
            {
                string? style = AttributeOf(shape, "style");
                if (style is null) continue;

                widthPt = CssLengthPt(style, "width");
                heightPt = CssLengthPt(style, "height");
                break;
            }

            var image = new ImageBlock
            {
                ImageFileName = fileName,
                WidthPt = widthPt > 0 ? widthPt : 100,
                HeightPt = heightPt > 0 ? heightPt : 100,
                WrapMode = WrapMode.Inline
            };

            string? title = null;
            foreach (var attribute in imageData.GetAttributes())
            {
                if (attribute.LocalName != "title") continue;
                title = attribute.Value;
                break;
            }
            if (!string.IsNullOrWhiteSpace(title))
                image.AltText = title;

            AppendInlineImage(image, chunk, section, runProps);
        }

        /// <summary>Ставит картинку символом в строку абзаца.</summary>
        private static void AppendInlineImage(
            ImageBlock image, TextChunk chunk, SectionModel section, RunProperties runProps)
        {
            section.InlineObjects.Add(image);
            chunk.Runs.Add(new RunModel
            {
                Text = RunModel.ObjectPlaceholder.ToString(),
                Properties = runProps,
                InlineImageId = image.Id
            });
        }

        /// <summary>
        /// Кладёт файл картинки в набор импорта и возвращает его имя. Формат берётся по
        /// сигнатуре файла, а не по типу из пакета: пересохранённый Word кладёт PNG с
        /// расширением .bin и типом image/unknown. TIFF, EMF и WMF переводятся в PNG —
        /// сам лист их не читает. Null — показать картинку нечем.
        /// </summary>
        private string? ExtractImagePart(
            MainDocumentPart mainPart,
            string relId,
            Dictionary<string, byte[]> extractedImages,
            List<string> warnings)
        {
            ImagePart? imagePart = null;
            foreach (var pair in mainPart.Parts)
            {
                if (pair.RelationshipId != relId) continue;
                imagePart = pair.OpenXmlPart as ImagePart;
                break;
            }
            if (imagePart is null) return null;

            string partKey = imagePart.Uri.ToString();
            if (_imageFileByPart.TryGetValue(partKey, out var known))
                return known.Length == 0 ? null : known;

            byte[] data;
            using (var stream = imagePart.GetStream(FileMode.Open, FileAccess.Read))
            using (var mem = new MemoryStream())
            {
                stream.CopyTo(mem);
                data = mem.ToArray();
            }

            string declaredExtension = ContentTypeToExtension(imagePart.ContentType);
            if (declaredExtension.Length == 0)
            {
                declaredExtension = imagePart.ContentType switch
                {
                    "image/x-emf" or "image/emf" => ".emf",
                    "image/x-wmf" or "image/wmf" => ".wmf",
                    _ => string.Empty
                };
            }

            if (!ImageFormats.TryNormalize(data, declaredExtension, out var normalizedData, out var extension))
            {
                warnings.Add(extension.Length == 0
                    ? "Изображение неизвестного формата пропущено."
                    : $"Изображение формата {extension.TrimStart('.').ToUpperInvariant()} пропущено: перевести его в PNG на этой системе нечем.");
                _imageFileByPart[partKey] = string.Empty;
                return null;
            }

            string fileName = $"img_{Guid.NewGuid():N}{extension}";
            extractedImages[fileName] = normalizedData;
            _imageFileByPart[partKey] = fileName;
            return fileName;
        }

        /// <summary>
        /// Обрезка, отражение и поворот картинки: a:srcRect у заливки и a:xfrm у
        /// свойств фигуры.
        /// </summary>
        private static void ApplyPictureGeometry(ImageBlock image, OpenXmlElement graphic)
        {
            OpenXmlElement? sourceRect = null;
            OpenXmlElement? transform = null;

            foreach (var element in graphic.Descendants())
            {
                if (sourceRect is null && element.LocalName == "srcRect") sourceRect = element;
                else if (transform is null && element.LocalName == "xfrm") transform = element;

                if (sourceRect is not null && transform is not null) break;
            }

            if (sourceRect is not null)
            {
                // Отрицательная обрезка у Word раздвигает рамку за края картинки; такого
                // режима на листе нет, и сторона остаётся необрезанной.
                double left = CropFraction(AttributeOf(sourceRect, "l"));
                double top = CropFraction(AttributeOf(sourceRect, "t"));
                double right = CropFraction(AttributeOf(sourceRect, "r"));
                double bottom = CropFraction(AttributeOf(sourceRect, "b"));

                if (left + right > MaxCropFrac)
                {
                    double scale = MaxCropFrac / (left + right);
                    left *= scale;
                    right *= scale;
                }
                if (top + bottom > MaxCropFrac)
                {
                    double scale = MaxCropFrac / (top + bottom);
                    top *= scale;
                    bottom *= scale;
                }

                image.CropLeftFrac = left;
                image.CropTopFrac = top;
                image.CropRightFrac = right;
                image.CropBottomFrac = bottom;
            }

            if (transform is not null)
            {
                image.FlipHorizontal = IsTrueValue(AttributeOf(transform, "flipH"));
                image.FlipVertical = IsTrueValue(AttributeOf(transform, "flipV"));

                if (double.TryParse(AttributeOf(transform, "rot"),
                        NumberStyles.Integer, CultureInfo.InvariantCulture, out double rotation))
                {
                    double degrees = rotation / RotationUnitsPerDegree % 360.0;
                    if (degrees < 0) degrees += 360.0;
                    image.RotationDeg = degrees;
                }
            }
        }

        /// <summary>
        /// Переносит на объект обтекание и порядок наложения плавающего объекта Word и
        /// возвращает его положение относительно опоры. Null — объект встаёт в поток
        /// отдельной полосой («сверху и снизу»), и опора ему не нужна.
        /// </summary>
        private static TableFloatPosition? ApplyAnchorPlacement(IFloatingObject image, OpenXmlElement anchor)
        {
            OpenXmlElement? wrap = null;
            foreach (var child in anchor.ChildElements)
            {
                if (!child.LocalName.StartsWith("wrap", StringComparison.Ordinal)) continue;
                wrap = child;
                break;
            }

            var horizontal = ChildByName(anchor, "positionH");
            var vertical = ChildByName(anchor, "positionV");
            string wrapKind = wrap?.LocalName ?? "wrapNone";

            // «Сверху и снизу»: текст по бокам не идёт вовсе. Это картинка в потоке на
            // собственной полосе — она встаёт над своим абзацем и сдвигает его вниз.
            if (wrapKind == "wrapTopAndBottom")
            {
                image.WrapMode = WrapMode.Inline;
                image.Alignment = ChildByName(horizontal, "align")?.InnerText.Trim() switch
                {
                    "center" => TextAlignment.Center,
                    "right" or "outside" => TextAlignment.Right,
                    _ => TextAlignment.Left
                };
                return null;
            }

            bool behindText = IsTrueValue(AttributeOf(anchor, "behindDoc"));

            image.WrapMode = wrapKind switch
            {
                "wrapSquare" => WrapMode.Square,
                "wrapTight" or "wrapThrough" => WrapMode.Tight,
                _ => behindText ? WrapMode.Behind : WrapMode.InFront
            };

            image.WrapSide = AttributeOf(wrap, "wrapText") switch
            {
                "bothSides" => WrapSide.BothSides,
                "left" => WrapSide.LeftOnly,
                "right" => WrapSide.RightOnly,
                _ => WrapSide.LargestOnly
            };

            // Расстояние до текста: у Word оно задано самой картинке.
            image.WrapPadTopPt = EmuAttributePt(anchor, "distT");
            image.WrapPadBottomPt = EmuAttributePt(anchor, "distB");
            image.WrapPadLeftPt = EmuAttributePt(anchor, "distL");
            image.WrapPadRightPt = EmuAttributePt(anchor, "distR");

            // Порядок наложения: больше — выше.
            if (long.TryParse(AttributeOf(anchor, "relativeHeight"),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out long relativeHeight))
                image.ZOrder = (int)Math.Clamp(relativeHeight, 0L, int.MaxValue);

            var position = new TableFloatPosition();

            // По горизонтали: от листа или от полосы набора. Колонка, поле и знак —
            // одна и та же полоса: колонок в разделе нет.
            position.HorizontalAnchor = AttributeOf(horizontal, "relativeFrom") == "page"
                ? TableFloatAnchor.Page
                : TableFloatAnchor.Text;

            switch (ChildByName(horizontal, "align")?.InnerText.Trim())
            {
                case "left":
                case "inside":
                    position.HorizontalAlign = TableFloatAlign.Start;
                    break;
                case "center":
                    position.HorizontalAlign = TableFloatAlign.Center;
                    break;
                case "right":
                case "outside":
                    position.HorizontalAlign = TableFloatAlign.End;
                    break;
                default:
                    position.HorizontalAlign = TableFloatAlign.Offset;
                    position.XPt = EmuTextPt(ChildByName(horizontal, "posOffset"));
                    break;
            }

            // По вертикали: от листа, от полей или от своего абзаца (строка абзаца —
            // тот же отсчёт, от его верха).
            position.VerticalAnchor = AttributeOf(vertical, "relativeFrom") switch
            {
                "page" => TableFloatAnchor.Page,
                "margin" or "topMargin" or "bottomMargin" or "insideMargin" or "outsideMargin"
                    => TableFloatAnchor.Margin,
                _ => TableFloatAnchor.Text
            };

            string? verticalAlign = ChildByName(vertical, "align")?.InnerText.Trim();
            if (position.VerticalAnchor == TableFloatAnchor.Text || verticalAlign is null)
            {
                position.VerticalAlign = TableFloatAlign.Offset;
                position.YPt = EmuTextPt(ChildByName(vertical, "posOffset"));
            }
            else
            {
                position.VerticalAlign = verticalAlign switch
                {
                    "center" => TableFloatAlign.Center,
                    "bottom" or "outside" => TableFloatAlign.End,
                    _ => TableFloatAlign.Start
                };
            }

            return position;
        }

        /// <summary>
        /// Содержимое mc:AlternateContent внутри рана. Так Word хранит фигуры: в ветке
        /// Choice лежит w:drawing с wps:wsp, в ветке Fallback — её же вид для старых
        /// версий (w:pict). Берётся ветка Choice; Fallback читается, только если в
        /// Choice нет рисунка, — иначе объект встал бы дважды.
        /// </summary>
        private void ImportAlternateContent(
            OpenXmlElement alternate,
            TextChunk chunk,
            SectionModel section,
            RunProperties runProps,
            DocxFormatResolver resolver,
            MainDocumentPart mainPart,
            Dictionary<string, byte[]> extractedImages,
            List<string> warnings)
        {
            OpenXmlElement? drawing = null;
            var choice = ChildByName(alternate, "Choice");
            if (choice is not null)
            {
                foreach (var element in choice.Descendants())
                {
                    if (element.LocalName != "drawing") continue;
                    drawing = element;
                    break;
                }
            }

            if (drawing is W.Drawing typedDrawing)
            {
                ImportDrawing(typedDrawing, chunk, section, runProps, resolver,
                    mainPart, extractedImages, warnings);
                return;
            }

            if (drawing is not null)
            {
                ImportWordShape(drawing, chunk, section, runProps, resolver, warnings);
                return;
            }

            var fallback = ChildByName(alternate, "Fallback");
            if (fallback is null) return;

            foreach (var element in fallback.Descendants())
            {
                if (element.LocalName != "pict") continue;
                ImportVmlPicture(element, chunk, section, runProps, mainPart, extractedImages, warnings);
                break;
            }
        }

        /// <summary>
        /// Фигура Word (wps:wsp) — прямоугольник, эллипс, выноска, надпись. Переносится
        /// фигурой Writersword с заливкой, обводкой и текстом внутри.
        ///
        /// Фигура в строке (wp:inline) встаёт отдельной полосой над своим абзацем: в
        /// строку текста фигуры в Writersword не ставятся. Плавающая (wp:anchor) несёт
        /// обтекание и положение относительно опоры, как плавающая картинка. В ячейке
        /// таблицы и колонтитуле потока блоков нет — там в строку переносится текст
        /// фигуры, чтобы он не потерялся.
        /// </summary>
        private void ImportWordShape(
            OpenXmlElement drawing,
            TextChunk chunk,
            SectionModel section,
            RunProperties runProps,
            DocxFormatResolver resolver,
            List<string> warnings)
        {
            var container = ChildByName(drawing, "inline") ?? ChildByName(drawing, "anchor");
            if (container is null) return;

            OpenXmlElement? shapeElement = null;
            foreach (var element in container.Descendants())
            {
                if (element.LocalName != "wsp") continue;
                shapeElement = element;
                break;
            }
            if (shapeElement is null) return;

            var shapeProps = ChildByName(shapeElement, "spPr");
            var extent = ChildByName(container, "extent");

            double widthPt = extent is null ? 0.0 : EmuAttributePt(extent, "cx");
            double heightPt = extent is null ? 0.0 : EmuAttributePt(extent, "cy");

            var shape = new ShapeBlock
            {
                WidthPt = widthPt > 0 ? widthPt : 100,
                HeightPt = heightPt > 0 ? heightPt : 100,
                WrapMode = WrapMode.Inline,
                StrokeColor = null,
                FillColor = null
            };

            ApplyShapeGeometry(shape, shapeProps);
            ApplyShapeFillAndLine(shape, shapeProps);
            ApplyShapeText(shape, shapeElement, resolver);

            string? description = AttributeOf(ChildByName(container, "docPr"), "descr");
            if (!string.IsNullOrWhiteSpace(description))
                shape.AltText = description;

            if (!_floatingAllowed)
            {
                // Блоков здесь ставить некуда: в строку уходит текст фигуры.
                if (!string.IsNullOrEmpty(shape.InnerText))
                {
                    chunk.Runs.Add(new RunModel
                    {
                        Text = shape.InnerText!.Replace('\n', ' '),
                        Properties = runProps
                    });
                }

                warnings.Add("Фигура внутри таблицы или колонтитула не перенесена: в текст вставлена только её надпись.");
                return;
            }

            if (container.LocalName == "anchor")
                shape.AnchorPosition = ApplyAnchorPlacement(shape, container);

            _pendingFloats.Add(shape);
        }

        /// <summary>Вид фигуры (a:prstGeom), поворот и отражение (a:xfrm).</summary>
        private static void ApplyShapeGeometry(ShapeBlock shape, OpenXmlElement? shapeProps)
        {
            string? preset = AttributeOf(ChildByName(shapeProps, "prstGeom"), "prst");

            switch (preset)
            {
                case "ellipse":
                    shape.ShapeType = ShapeType.Ellipse;
                    break;

                case "line":
                case "straightConnector1":
                    shape.ShapeType = ShapeType.Line;
                    break;

                case "wedgeRectCallout":
                case "wedgeRoundRectCallout":
                case "wedgeEllipseCallout":
                case "cloudCallout":
                    shape.ShapeType = ShapeType.Callout;
                    break;

                case "roundRect":
                    // Скругление по умолчанию у Word — шестая часть меньшей стороны.
                    shape.ShapeType = ShapeType.Rectangle;
                    shape.CornerRadiusPt = Math.Min(shape.WidthPt, shape.HeightPt) / 6.0;
                    break;

                default:
                    shape.ShapeType = ShapeType.Rectangle;
                    break;
            }

            var transform = ChildByName(shapeProps, "xfrm");
            if (transform is null) return;

            shape.FlipHorizontal = IsTrueValue(AttributeOf(transform, "flipH"));
            shape.FlipVertical = IsTrueValue(AttributeOf(transform, "flipV"));

            if (double.TryParse(AttributeOf(transform, "rot"),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out double rotation))
            {
                double degrees = rotation / RotationUnitsPerDegree % 360.0;
                if (degrees < 0) degrees += 360.0;
                shape.RotationDeg = degrees;
            }
        }

        /// <summary>
        /// Заливка (a:solidFill) и обводка (a:ln) фигуры. Цвет берётся только заданный
        /// явно (a:srgbClr): цвет из темы документа сюда не переносится, и такая
        /// заливка или обводка остаётся пустой.
        /// </summary>
        private static void ApplyShapeFillAndLine(ShapeBlock shape, OpenXmlElement? shapeProps)
        {
            shape.FillColor = ExplicitColor(ChildByName(shapeProps, "solidFill"));

            var line = ChildByName(shapeProps, "ln");
            if (line is null)
            {
                shape.StrokeThicknessPt = 0.0;
                return;
            }

            shape.StrokeColor = ChildByName(line, "noFill") is not null
                ? null
                : ExplicitColor(ChildByName(line, "solidFill"));

            // Толщина линии по умолчанию у Word — 0,75 пт.
            double thicknessPt = EmuAttributePt(line, "w");
            shape.StrokeThicknessPt = thicknessPt > 0 ? thicknessPt : 0.75;

            shape.DashStyle = AttributeOf(ChildByName(line, "prstDash"), "val") switch
            {
                "dash" or "sysDash" or "lgDash" => ShapeDashStyle.Dash,
                "dot" or "sysDot" => ShapeDashStyle.Dot,
                "dashDot" or "lgDashDot" or "sysDashDot" => ShapeDashStyle.DashDot,
                _ => ShapeDashStyle.Solid
            };
        }

        /// <summary>Цвет из a:solidFill, заданный явно, в виде «#RRGGBB». Иначе — null.</summary>
        private static string? ExplicitColor(OpenXmlElement? solidFill)
        {
            string? value = AttributeOf(ChildByName(solidFill, "srgbClr"), "val");
            return value is { Length: 6 } ? "#" + value.ToUpperInvariant() : null;
        }

        /// <summary>
        /// Текст фигуры (wps:txbx) и его оформление. Абзацы надписи складываются в
        /// строки; шрифт, кегль, цвет и начертание берутся у первого рана с учётом
        /// стилей, выравнивание — у первого абзаца. Положение по высоте и отступы —
        /// из wps:bodyPr.
        /// </summary>
        private static void ApplyShapeText(ShapeBlock shape, OpenXmlElement shapeElement, DocxFormatResolver resolver)
        {
            var body = ChildByName(shapeElement, "bodyPr");
            if (body is not null)
            {
                shape.TextVerticalAlign = AttributeOf(body, "anchor") switch
                {
                    "ctr" => VerticalAlignment.Middle,
                    "b" => VerticalAlignment.Bottom,
                    _ => VerticalAlignment.Top
                };

                if (AttributeOf(body, "lIns") is not null)
                    shape.TextInsetHorizontalPt = EmuAttributePt(body, "lIns");
                if (AttributeOf(body, "tIns") is not null)
                    shape.TextInsetVerticalPt = EmuAttributePt(body, "tIns");
            }
            else
            {
                shape.TextVerticalAlign = VerticalAlignment.Top;
            }

            var textBox = ChildByName(shapeElement, "txbx");
            if (textBox is null) return;

            var lines = new List<string>();
            bool formatTaken = false;

            foreach (var element in textBox.Descendants())
            {
                if (element.LocalName != "p") continue;

                var text = new System.Text.StringBuilder();
                foreach (var inner in element.Descendants())
                {
                    switch (inner.LocalName)
                    {
                        case "t":
                            text.Append(inner.InnerText);
                            break;
                        case "tab":
                            text.Append('\t');
                            break;
                        case "br":
                        case "cr":
                            text.Append('\n');
                            break;
                    }
                }
                lines.Add(text.ToString());

                if (formatTaken || element is not W.Paragraph paragraph) continue;

                var effPara = resolver.ResolveEffectiveParagraph(paragraph);
                var firstRun = paragraph.Descendants<W.Run>().FirstOrDefault();
                if (firstRun is null && text.Length == 0) continue;

                var runFormat = firstRun is not null
                    ? resolver.ResolveEffectiveRun(firstRun, effPara).ToRunProperties()
                    : effPara.BaseRun.ToRunProperties();

                shape.TextFontFamily = runFormat.FontFamily;
                if (runFormat.FontSize is { } fontSize && fontSize > 0)
                    shape.TextSizePt = fontSize;
                shape.TextBold = runFormat.IsBold == true;
                shape.TextItalic = runFormat.IsItalic == true;
                shape.TextColor = string.IsNullOrWhiteSpace(runFormat.TextColor) ? null : runFormat.TextColor;
                // Выравнивание абзаца может быть не задано — тогда у надписи остаётся своё.
                shape.TextAlign = effPara.ToParagraphProperties(resolver.MapStyleName(paragraph, effPara)).Alignment ?? shape.TextAlign;
                formatTaken = true;
            }

            // Пустые абзацы в конце надписи высоты ей не добавляют.
            while (lines.Count > 0 && lines[^1].Length == 0)
                lines.RemoveAt(lines.Count - 1);

            shape.InnerText = lines.Count == 0 ? null : string.Join("\n", lines);
        }

        /// <summary>
        /// Ставит отложенные плавающие картинки в поток — перед абзацем, который будет
        /// добавлен следом.
        /// </summary>
        private void FlushPendingFloats(SectionModel section)
        {
            foreach (var floating in _pendingFloats)
                section.Blocks.Add(floating);
            _pendingFloats.Clear();
        }

        private static OpenXmlElement? ChildByName(OpenXmlElement? parent, string localName)
        {
            if (parent is null) return null;

            foreach (var child in parent.ChildElements)
                if (child.LocalName == localName) return child;

            return null;
        }

        private static string? AttributeOf(OpenXmlElement? element, string localName)
        {
            if (element is null) return null;

            foreach (var attribute in element.GetAttributes())
                if (attribute.LocalName == localName) return attribute.Value;

            return null;
        }

        private static bool IsTrueValue(string? value) =>
            value is "1" or "true" or "on";

        private static double CropFraction(string? value)
        {
            if (!double.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out double units))
                return 0.0;

            return Math.Clamp(units / CropUnitsPerWhole, 0.0, MaxCropFrac);
        }

        private static double EmuAttributePt(OpenXmlElement element, string localName)
        {
            return double.TryParse(AttributeOf(element, localName),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out double emu)
                ? Math.Max(0.0, emu / EmuPerPoint)
                : 0.0;
        }

        private static double EmuTextPt(OpenXmlElement? element)
        {
            return double.TryParse(element?.InnerText.Trim(),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out double emu)
                ? emu / EmuPerPoint
                : 0.0;
        }

        /// <summary>
        /// Длина из стиля фигуры VML («width:119.7pt;height:90.4pt»), в пунктах.
        /// Ноль — свойства нет или единица не опознана.
        /// </summary>
        private static double CssLengthPt(string style, string property)
        {
            foreach (var declaration in style.Split(';'))
            {
                int colon = declaration.IndexOf(':');
                if (colon <= 0) continue;
                if (!declaration.Substring(0, colon).Trim().Equals(property, StringComparison.OrdinalIgnoreCase))
                    continue;

                string value = declaration.Substring(colon + 1).Trim().ToLowerInvariant();

                double unitPt = 1.0;
                string number = value;
                if (value.EndsWith("pt", StringComparison.Ordinal)) { number = value[..^2]; }
                else if (value.EndsWith("in", StringComparison.Ordinal)) { number = value[..^2]; unitPt = 72.0; }
                else if (value.EndsWith("cm", StringComparison.Ordinal)) { number = value[..^2]; unitPt = 72.0 / 2.54; }
                else if (value.EndsWith("mm", StringComparison.Ordinal)) { number = value[..^2]; unitPt = 72.0 / 25.4; }
                else if (value.EndsWith("px", StringComparison.Ordinal)) { number = value[..^2]; unitPt = 72.0 / 96.0; }
                else if (value.EndsWith("pc", StringComparison.Ordinal)) { number = value[..^2]; unitPt = 12.0; }
                else return 0.0;

                return double.TryParse(number.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double amount)
                    ? Math.Max(0.0, amount * unitPt)
                    : 0.0;
            }

            return 0.0;
        }
    }
}
