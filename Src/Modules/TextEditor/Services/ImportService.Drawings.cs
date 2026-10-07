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
        /// Плавающие объекты абзаца ячейки таблицы, который сейчас разбирается. Не null —
        /// идёт разбор абзаца ячейки: плавающая картинка или фигура встаёт в ячейку
        /// плавающим объектом с обтеканием (<see cref="CellFloat"/>), как у Word.
        /// </summary>
        private List<BlockModel>? _cellFloatCollector;

        /// <summary>
        /// Файл картинки по части пакета: одна и та же часть, на которую документ
        /// ссылается много раз, кладётся в проект один раз. Пустое имя — часть уже
        /// разбиралась и показать её нечем.
        /// </summary>
        private readonly Dictionary<string, string> _imageFileByPart = new();

        /// <summary>
        /// Исходный файл перекодированной картинки по части пакета: TIFF, EMF или WMF в
        /// том виде, в каком он пришёл. Его и пишет экспорт в .docx.
        /// </summary>
        private readonly Dictionary<string, string> _sourceImageFileByPart = new();

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
                ImportWordShape(drawing, chunk, section, runProps, resolver, mainPart, extractedImages, warnings);
                return;
            }

            string? fileName = ExtractImagePart(
                mainPart, relId!, extractedImages, warnings, out string? sourceFileName);
            if (fileName is null) return;

            OpenXmlElement? container = (OpenXmlElement?)inline ?? anchor;

            // Габарит рисунка. Нулевой у Word — рисунок без места на листе: его не
            // видно, но он остаётся в документе и уходит обратно в .docx таким же.
            // Запасной размер ставится, только когда габарит не записан вовсе.
            var extentElement = ChildByName(container, "extent");
            bool extentWritten = AttributeOf(extentElement, "cx") is not null
                && AttributeOf(extentElement, "cy") is not null;

            var image = new ImageBlock
            {
                ImageFileName = fileName,
                SourceImageFileName = sourceFileName,
                WidthPt = extentWritten ? Math.Max(0L, extentCx) / EmuPerPoint : 100,
                HeightPt = extentWritten ? Math.Max(0L, extentCy) / EmuPerPoint : 100,
                WrapMode = WrapMode.Inline
            };

            ApplyPictureGeometry(image, graphic!);
            ApplyPictureLook(image, graphic!);

            if (container is not null)
                image.WordDrawing = ReadWordDrawing(container, image, graphic!);

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

                // Абзац ячейки таблицы: картинка остаётся плавающей внутри ячейки.
                // «Сверху и снизу» в ячейке — своя строка ячейки: в строку абзаца.
                if (_cellFloatCollector is not null)
                {
                    var cellPosition = ApplyAnchorPlacement(image, anchor);
                    if (cellPosition is not null)
                    {
                        image.AnchorPosition = cellPosition;
                        _cellFloatCollector.Add(image);
                        return;
                    }

                    AppendInlineImage(image, chunk, section, runProps);
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

            string? fileName = ExtractImagePart(
                mainPart, relId!, extractedImages, warnings, out string? sourceFileName);
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
                SourceImageFileName = sourceFileName,
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
        /// сам лист их не читает. Null — картинки в пакете нет или формат не опознан.
        ///
        /// Исходный файл перекодированной картинки тоже кладётся в набор импорта и
        /// возвращается в sourceFileName: в .docx уходит именно он — тот же вектор и тот
        /// же формат, что был у Word. Если перекодировать картинку на этой системе нечем,
        /// в документ встаёт сам исходник: лист его не покажет, но и из документа он не
        /// пропадёт и уйдёт обратно в .docx.
        /// </summary>
        private string? ExtractImagePart(
            MainDocumentPart mainPart,
            string relId,
            Dictionary<string, byte[]> extractedImages,
            List<string> warnings,
            out string? sourceFileName)
        {
            sourceFileName = null;

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
            {
                _sourceImageFileByPart.TryGetValue(partKey, out sourceFileName);
                return known.Length == 0 ? null : known;
            }

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
                if (extension.Length == 0)
                {
                    warnings.Add("Изображение неизвестного формата пропущено.");
                    _imageFileByPart[partKey] = string.Empty;
                    return null;
                }

                warnings.Add($"Изображение формата {extension.TrimStart('.').ToUpperInvariant()} не показано: перевести его в PNG на этой системе нечем. В документе оно сохранено и уйдёт в .docx без изменений.");

                string keptName = $"img_{Guid.NewGuid():N}{extension}";
                extractedImages[keptName] = data;
                _imageFileByPart[partKey] = keptName;
                return keptName;
            }

            string fileName = $"img_{Guid.NewGuid():N}{extension}";
            extractedImages[fileName] = normalizedData;
            _imageFileByPart[partKey] = fileName;

            // Перекодированная картинка: исходник лежит рядом под тем же именем со
            // своим расширением и уходит в .docx вместо копии в PNG.
            if (!ReferenceEquals(normalizedData, data))
            {
                string originalExtension = ImageFormats.SniffExtension(data);
                if (originalExtension.Length == 0) originalExtension = declaredExtension;
                if (originalExtension.Length > 0)
                {
                    if (!originalExtension.StartsWith(".", StringComparison.Ordinal))
                        originalExtension = "." + originalExtension;

                    sourceFileName = Path.GetFileNameWithoutExtension(fileName) + "_src" + originalExtension;
                    extractedImages[sourceFileName] = data;
                    _sourceImageFileByPart[partKey] = sourceFileName;
                }
            }

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
        ///
        /// Всё, что раскладка не различает, но Word хранит — «сквозное» обтекание,
        /// контур обтекания, закрепление якоря, запрет наложения, порядок наложения как
        /// он записан, точное имя опоры, — кладётся в <see cref="WordDrawingInfo"/> и
        /// уходит обратно в .docx без изменений.
        /// </summary>
        private static TableFloatPosition? ApplyAnchorPlacement(IFloatingObject image, OpenXmlElement anchor)
        {
            var word = image.WordDrawing ??= new WordDrawingInfo();

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

            // Флаги якоря. Значения по умолчанию — те, что Word пишет сам.
            word.LayoutInCell = AttributeOf(anchor, "layoutInCell") is not { } inCell || IsTrueValue(inCell);
            word.AllowOverlap = AttributeOf(anchor, "allowOverlap") is not { } overlap || IsTrueValue(overlap);
            word.Locked = IsTrueValue(AttributeOf(anchor, "locked"));
            word.HorizontalRelativeFrom = AttributeOf(horizontal, "relativeFrom");
            word.VerticalRelativeFrom = AttributeOf(vertical, "relativeFrom");

            // Расстояние до текста: у Word оно задано самой картинке.
            image.WrapPadTopPt = EmuAttributePt(anchor, "distT");
            image.WrapPadBottomPt = EmuAttributePt(anchor, "distB");
            image.WrapPadLeftPt = EmuAttributePt(anchor, "distL");
            image.WrapPadRightPt = EmuAttributePt(anchor, "distR");

            // Порядок наложения: больше — выше. Запись Word сохраняется как есть, чтобы
            // вернуть её в .docx, пока порядок объекта в редакторе не меняли.
            if (long.TryParse(AttributeOf(anchor, "relativeHeight"),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out long relativeHeight))
            {
                image.ZOrder = (int)Math.Clamp(relativeHeight, 0L, int.MaxValue);
                word.RelativeHeight = relativeHeight;
                word.RelativeHeightForZOrder = image.ZOrder;
            }

            var position = ReadAnchorPosition(horizontal, vertical);

            // «Сверху и снизу»: текст по бокам не идёт вовсе. Это картинка в потоке на
            // собственной полосе — она встаёт над своим абзацем и сдвигает его вниз.
            // Положение Word сохраняется для обратного переноса: в .docx объект уходит
            // тем же якорем, а не картинкой в строке.
            if (wrapKind == "wrapTopAndBottom")
            {
                image.WrapMode = WrapMode.Inline;
                image.Alignment = ChildByName(horizontal, "align")?.InnerText.Trim() switch
                {
                    "center" => TextAlignment.Center,
                    "right" or "outside" => TextAlignment.Right,
                    _ => TextAlignment.Left
                };
                word.WrapTopAndBottom = true;
                word.TopAndBottomPosition = position;
                return null;
            }

            bool behindText = IsTrueValue(AttributeOf(anchor, "behindDoc"));

            image.WrapMode = wrapKind switch
            {
                "wrapSquare" => WrapMode.Square,
                "wrapTight" or "wrapThrough" => WrapMode.Tight,
                _ => behindText ? WrapMode.Behind : WrapMode.InFront
            };

            word.WrapThrough = wrapKind == "wrapThrough";

            // Флаг «за текстом» бывает и у обтекаемого объекта: он задаёт только слой
            // рисования. Раскладке он не нужен, а в .docx уходит обратно.
            word.BehindDoc = behindText;

            image.WrapSide = AttributeOf(wrap, "wrapText") switch
            {
                "bothSides" => WrapSide.BothSides,
                "left" => WrapSide.LeftOnly,
                "right" => WrapSide.RightOnly,
                _ => WrapSide.LargestOnly
            };

            // Контур обтекания: точки в долях габарита (21600 — вся сторона).
            var polygon = ChildByName(wrap, "wrapPolygon");
            if (polygon is not null)
            {
                word.WrapPolygonEdited = IsTrueValue(AttributeOf(polygon, "edited"));
                var points = new List<WordWrapPoint>();
                foreach (var point in polygon.ChildElements)
                {
                    if (point.LocalName is not ("start" or "lineTo")) continue;
                    long.TryParse(AttributeOf(point, "x"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long x);
                    long.TryParse(AttributeOf(point, "y"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long y);
                    points.Add(new WordWrapPoint { X = x, Y = y });
                }

                if (points.Count > 0 && !IsFrameWrapPolygon(points))
                    word.WrapPolygon = points;
            }

            // У «по контуру» и «сквозного» расстояние слева и справа Word берёт из
            // самого элемента обтекания, если оно там записано.
            if (wrap is not null)
            {
                if (AttributeOf(wrap, "distL") is not null) image.WrapPadLeftPt = EmuAttributePt(wrap, "distL");
                if (AttributeOf(wrap, "distR") is not null) image.WrapPadRightPt = EmuAttributePt(wrap, "distR");
                if (AttributeOf(wrap, "distT") is not null) image.WrapPadTopPt = EmuAttributePt(wrap, "distT");
                if (AttributeOf(wrap, "distB") is not null) image.WrapPadBottomPt = EmuAttributePt(wrap, "distB");
            }

            return position;
        }

        /// <summary>
        /// Контур обтекания, совпадающий с рамкой объекта: такой Word пишет сам, и
        /// хранить его отдельно незачем — экспорт построит ровно его же.
        /// </summary>
        private static bool IsFrameWrapPolygon(List<WordWrapPoint> points)
        {
            if (points.Count != 5) return false;

            long[,] frame = { { 0, 0 }, { 0, 21600 }, { 21600, 21600 }, { 21600, 0 }, { 0, 0 } };
            for (int i = 0; i < 5; i++)
            {
                if (points[i].X != frame[i, 0] || points[i].Y != frame[i, 1]) return false;
            }

            return true;
        }

        /// <summary>
        /// Положение объекта относительно опоры (wp:positionH и wp:positionV).
        ///
        /// По горизонтали: лист, полоса набора (колонка, поле, знак — одна и та же
        /// полоса: колонок в разделе нет) или одно из боковых полей листа. По
        /// вертикали: лист, поля, верхнее или нижнее поле листа, или свой абзац —
        /// строка абзаца у якоря та же, первая.
        /// </summary>
        private static TableFloatPosition ReadAnchorPosition(OpenXmlElement? horizontal, OpenXmlElement? vertical)
        {
            var position = new TableFloatPosition
            {
                HorizontalAnchor = AttributeOf(horizontal, "relativeFrom") switch
                {
                    "page" => TableFloatAnchor.Page,
                    "leftMargin" => TableFloatAnchor.LeftMargin,
                    "rightMargin" => TableFloatAnchor.RightMargin,
                    "insideMargin" => TableFloatAnchor.InsideMargin,
                    "outsideMargin" => TableFloatAnchor.OutsideMargin,
                    _ => TableFloatAnchor.Text
                }
            };

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

            position.VerticalAnchor = AttributeOf(vertical, "relativeFrom") switch
            {
                "page" => TableFloatAnchor.Page,
                "margin" => TableFloatAnchor.Margin,
                "topMargin" => TableFloatAnchor.TopMargin,
                "bottomMargin" => TableFloatAnchor.BottomMargin,
                "insideMargin" => TableFloatAnchor.InsideMargin,
                "outsideMargin" => TableFloatAnchor.OutsideMargin,
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
        /// Свойства рисунка Word, общие для рисунка в строке и плавающего: поля
        /// обрамления, имя, заголовок и скрытость, запрет менять пропорции, размер
        /// самой картинки, когда он отличается от габарита рисунка.
        /// </summary>
        private static WordDrawingInfo ReadWordDrawing(
            OpenXmlElement container, IFloatingObject obj, OpenXmlElement? graphic)
        {
            // Контур картинки мог уже лечь сюда из её свойств (ApplyPictureLook).
            var word = obj.WordDrawing ?? new WordDrawingInfo();
            word.EffectForWidthPt = obj.WidthPt;
            word.EffectForHeightPt = obj.HeightPt;
            word.EffectForRotationDeg = obj.RotationDeg;

            var effect = ChildByName(container, "effectExtent");
            if (effect is not null)
            {
                word.HasEffectExtent = true;
                word.EffectLeftPt = EmuAttributePtSigned(effect, "l");
                word.EffectTopPt = EmuAttributePtSigned(effect, "t");
                word.EffectRightPt = EmuAttributePtSigned(effect, "r");
                word.EffectBottomPt = EmuAttributePtSigned(effect, "b");
            }

            var docPr = ChildByName(container, "docPr");
            if (docPr is not null)
            {
                word.Name = AttributeOf(docPr, "name");
                word.Title = AttributeOf(docPr, "title");
                word.Hidden = IsTrueValue(AttributeOf(docPr, "hidden"));
            }

            // Запрет менять пропорции (a:graphicFrameLocks noChangeAspect).
            var frameProperties = ChildByName(container, "cNvGraphicFramePr");
            var locks = ChildByName(frameProperties, "graphicFrameLocks");
            obj.LockAspectRatio = locks is not null && IsTrueValue(AttributeOf(locks, "noChangeAspect"));

            // Размер самой картинки, когда он отличается от габарита рисунка.
            if (graphic is not null)
            {
                foreach (var element in graphic.Descendants())
                {
                    if (element.LocalName != "xfrm") continue;

                    var extents = ChildByName(element, "ext");
                    if (extents is not null)
                    {
                        double pictureW = EmuAttributePt(extents, "cx");
                        double pictureH = EmuAttributePt(extents, "cy");
                        if (Math.Abs(pictureW - obj.WidthPt) > 0.01 || Math.Abs(pictureH - obj.HeightPt) > 0.01)
                        {
                            word.PictureWidthPt = pictureW;
                            word.PictureHeightPt = pictureH;
                        }
                    }
                    break;
                }
            }

            return word;
        }

        /// <summary>
        /// Оформление картинки из её свойств (pic:spPr) и заливки (a:blip): контур, по
        /// которому она обрезана (a:prstGeom), рамка (a:ln) и непрозрачность
        /// (a:alphaModFix). Контур, который модель различает грубее, сохраняется в
        /// <see cref="WordDrawingInfo.PresetGeometryXml"/> и уходит обратно без потерь.
        /// </summary>
        private static void ApplyPictureLook(ImageBlock image, OpenXmlElement graphic)
        {
            OpenXmlElement? pictureProps = null;
            OpenXmlElement? blip = null;
            foreach (var element in graphic.Descendants())
            {
                if (pictureProps is null && element.LocalName == "spPr" && element.Parent?.LocalName == "pic")
                    pictureProps = element;
                else if (blip is null && element.LocalName == "blip")
                    blip = element;

                if (pictureProps is not null && blip is not null) break;
            }

            // Непрозрачность: доля в стотысячных.
            var alpha = ChildByName(blip, "alphaModFix");
            if (alpha is not null
                && double.TryParse(AttributeOf(alpha, "amt"), NumberStyles.Integer, CultureInfo.InvariantCulture, out double amount))
            {
                image.Opacity = Math.Clamp(amount / 100000.0, 0.0, 1.0);
            }

            if (pictureProps is null) return;

            var preset = ChildByName(pictureProps, "prstGeom");
            string? presetName = AttributeOf(preset, "prst");
            switch (presetName)
            {
                case null:
                case "rect":
                    break;

                case "ellipse":
                    image.ShapeType = ShapeType.Ellipse;
                    break;

                case "roundRect":
                    image.ShapeType = ShapeType.Rectangle;
                    image.CornerRadiusPt = Math.Min(image.WidthPt, image.HeightPt) * RoundRectAdjust(preset);
                    break;

                case "wedgeRectCallout":
                case "wedgeRoundRectCallout":
                case "wedgeEllipseCallout":
                case "cloudCallout":
                    image.ShapeType = ShapeType.Callout;
                    break;

                default:
                    // Заготовка Word, которой у редактора нет: картинка остаётся
                    // прямоугольной на листе, а контур уходит в .docx как был.
                    break;
            }

            if (preset is not null && presetName is not null and not "rect")
            {
                var word = image.WordDrawing ??= new WordDrawingInfo();
                word.PresetGeometryXml = preset.OuterXml;
                word.PresetGeometryShapeType = image.ShapeType;
                word.PresetGeometryCornerPt = image.CornerRadiusPt;
            }

            var line = ChildByName(pictureProps, "ln");
            if (line is null || ChildByName(line, "noFill") is not null) return;

            string? color = ExplicitColor(ChildByName(line, "solidFill"));
            if (color is null) return;

            image.BorderColor = color;
            double thicknessPt = EmuAttributePt(line, "w");
            image.BorderThicknessPt = thicknessPt > 0 ? thicknessPt : 0.75;
            image.BorderDashStyle = AttributeOf(ChildByName(line, "prstDash"), "val") switch
            {
                "dash" or "sysDash" or "lgDash" => ShapeDashStyle.Dash,
                "dot" or "sysDot" => ShapeDashStyle.Dot,
                "dashDot" or "lgDashDot" or "sysDashDot" => ShapeDashStyle.DashDot,
                _ => ShapeDashStyle.Solid
            };
            image.BorderAlign = AttributeOf(line, "algn") == "in"
                ? ImageBorderAlign.Inside
                : ImageBorderAlign.Center;
        }

        /// <summary>
        /// Скругление roundRect в долях меньшей стороны: значение adj из a:avLst
        /// (в стотысячных, по умолчанию 16667 — шестая часть).
        /// </summary>
        private static double RoundRectAdjust(OpenXmlElement? preset)
        {
            var list = ChildByName(preset, "avLst");
            if (list is not null)
            {
                foreach (var guide in list.ChildElements)
                {
                    if (guide.LocalName != "gd" || AttributeOf(guide, "name") != "adj") continue;

                    string formula = AttributeOf(guide, "fmla") ?? string.Empty;
                    if (formula.StartsWith("val ", StringComparison.Ordinal)
                        && double.TryParse(formula.Substring(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out double adj))
                    {
                        return Math.Clamp(adj / 100000.0, 0.0, 0.5);
                    }
                }
            }

            return 16667.0 / 100000.0;
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
                ImportWordShape(drawing, chunk, section, runProps, resolver, mainPart, extractedImages, warnings);
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
        /// Фигура в строке (wp:inline) встаёт в строку текста своего абзаца — среди
        /// букв, на базовой линии, как у Word; так и в ячейке таблицы, и в колонтитуле.
        /// Плавающая (wp:anchor) несёт обтекание и положение относительно опоры, как
        /// плавающая картинка. В ячейке таблицы и колонтитуле потока блоков нет — там у
        /// плавающей фигуры в строку переносится её текст, чтобы он не потерялся.
        /// </summary>
        private void ImportWordShape(
            OpenXmlElement drawing,
            TextChunk chunk,
            SectionModel section,
            RunProperties runProps,
            DocxFormatResolver resolver,
            MainDocumentPart mainPart,
            Dictionary<string, byte[]> extractedImages,
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
            ApplyShapeFillImage(shape, shapeProps, mainPart, extractedImages, warnings);
            ApplyShapeText(shape, shapeElement, resolver);
            shape.WordDrawing = ReadWordDrawing(container, shape, null);

            string? description = AttributeOf(ChildByName(container, "docPr"), "descr");
            if (!string.IsNullOrWhiteSpace(description))
                shape.AltText = description;

            // Фигура в строке — объект строки, как картинка в строке: она живёт в
            // InlineObjects раздела, а run абзаца хранит только ссылку на неё.
            if (container.LocalName == "inline")
            {
                shape.WrapMode = WrapMode.Inline;
                section.InlineObjects.Add(shape);
                chunk.Runs.Add(new RunModel
                {
                    Text = RunModel.ObjectPlaceholder.ToString(),
                    Properties = runProps,
                    InlineImageId = shape.Id
                });
                return;
            }

            // Абзац ячейки таблицы: плавающая фигура остаётся плавающей внутри ячейки.
            if (_cellFloatCollector is not null)
            {
                var cellPosition = ApplyAnchorPlacement(shape, container);
                if (cellPosition is not null)
                {
                    shape.AnchorPosition = cellPosition;
                    _cellFloatCollector.Add(shape);
                    return;
                }

                // «Сверху и снизу» в ячейке — фигура в строке абзаца.
                shape.WrapMode = WrapMode.Inline;
                section.InlineObjects.Add(shape);
                chunk.Runs.Add(new RunModel
                {
                    Text = RunModel.ObjectPlaceholder.ToString(),
                    Properties = runProps,
                    InlineImageId = shape.Id
                });
                return;
            }

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
                    // Скругление — доля меньшей стороны из a:avLst; по умолчанию у Word
                    // это шестая часть.
                    shape.ShapeType = ShapeType.Rectangle;
                    shape.CornerRadiusPt = Math.Min(shape.WidthPt, shape.HeightPt)
                        * RoundRectAdjust(ChildByName(shapeProps, "prstGeom"));
                    break;

                default:
                    shape.ShapeType = ShapeType.Rectangle;
                    break;
            }

            // Контур Word сохраняется как записан: заготовок у Word много больше, чем
            // видов фигуры у редактора, и в .docx фигура уходит со своим контуром.
            var presetElement = ChildByName(shapeProps, "prstGeom");
            if (presetElement is not null && preset is not null and not "rect")
            {
                var word = shape.WordDrawing ??= new WordDrawingInfo();
                word.PresetGeometryXml = presetElement.OuterXml;
                word.PresetGeometryShapeType = shape.ShapeType;
                word.PresetGeometryCornerPt = shape.CornerRadiusPt;
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
            var solidFill = ChildByName(shapeProps, "solidFill");
            shape.FillColor = ExplicitColor(solidFill);

            // Прозрачность заливки (a:alpha в стотысячных) — непрозрачность фигуры.
            var fillAlpha = ChildByName(ChildByName(solidFill, "srgbClr"), "alpha");
            if (fillAlpha is not null
                && double.TryParse(AttributeOf(fillAlpha, "val"), NumberStyles.Integer, CultureInfo.InvariantCulture, out double alphaValue))
            {
                shape.Opacity = Math.Clamp(alphaValue / 100000.0, 0.0, 1.0);
            }

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

            if (AttributeOf(line, "algn") == "in")
                shape.StrokeAlign = ImageBorderAlign.Inside;

            shape.DashStyle = AttributeOf(ChildByName(line, "prstDash"), "val") switch
            {
                "dash" or "sysDash" or "lgDash" => ShapeDashStyle.Dash,
                "dot" or "sysDot" => ShapeDashStyle.Dot,
                "dashDot" or "lgDashDot" or "sysDashDot" => ShapeDashStyle.DashDot,
                _ => ShapeDashStyle.Solid
            };
        }

        /// <summary>
        /// Картинка-заливка фигуры (a:blipFill в её свойствах): файл, обрезка и
        /// непрозрачность. Растянутая на всю фигуру — как у Word по умолчанию.
        /// </summary>
        private void ApplyShapeFillImage(
            ShapeBlock shape, OpenXmlElement? shapeProps,
            MainDocumentPart mainPart, Dictionary<string, byte[]> extractedImages, List<string> warnings)
        {
            var blipFill = ChildByName(shapeProps, "blipFill");
            var blip = ChildByName(blipFill, "blip");
            if (blip is null) return;

            string? relId = null;
            foreach (var attribute in blip.GetAttributes())
            {
                if (attribute.LocalName != "embed" || attribute.NamespaceUri != RelationshipsNamespace) continue;
                relId = attribute.Value;
                break;
            }
            if (string.IsNullOrEmpty(relId)) return;

            string? fileName = ExtractImagePart(mainPart, relId!, extractedImages, warnings, out _);
            if (fileName is null) return;

            shape.FillImageFileName = fileName;
            shape.FillImageStretch = true;

            var sourceRect = ChildByName(blipFill, "srcRect");
            if (sourceRect is not null)
            {
                shape.CropLeftFrac = CropFraction(AttributeOf(sourceRect, "l"));
                shape.CropTopFrac = CropFraction(AttributeOf(sourceRect, "t"));
                shape.CropRightFrac = CropFraction(AttributeOf(sourceRect, "r"));
                shape.CropBottomFrac = CropFraction(AttributeOf(sourceRect, "b"));
            }

            var alpha = ChildByName(blip, "alphaModFix");
            if (alpha is not null
                && double.TryParse(AttributeOf(alpha, "amt"), NumberStyles.Integer, CultureInfo.InvariantCulture, out double amount))
            {
                shape.Opacity = Math.Clamp(amount / 100000.0, 0.0, 1.0);
            }
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

        /// <summary>Длина в EMU из атрибута, в пунктах, со знаком: поля обрамления бывают отрицательными.</summary>
        private static double EmuAttributePtSigned(OpenXmlElement element, string localName)
        {
            return double.TryParse(AttributeOf(element, localName),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out double emu)
                ? emu / EmuPerPoint
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
