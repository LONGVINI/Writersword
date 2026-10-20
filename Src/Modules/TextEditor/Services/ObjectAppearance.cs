using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Вид объекта на листе — для правки вида картинок и фигур.
    ///
    /// Вид — это те свойства, которые человек меняет у объекта мышью и лентой:
    /// место, размер, поворот, отражение, обрезка, обтекание, рамка, заливка,
    /// надпись фигуры. Тот же набор держат шаги отмены картинки и фигуры
    /// (ImagePropertiesCommand, ShapePropertiesCommand). Вид пишется строкой
    /// «имя — значение» в постоянном порядке: две записи равны, только если вид
    /// одинаков, а запись прежнего вида возвращается на объект при отклонении.
    /// </summary>
    public static class ObjectAppearance
    {
        /// <summary>
        /// Расширение DrawingML (a:ext у wp:docPr), в котором смена вида объекта
        /// уходит в .docx. Word такого расширения не знает и пропускает его, файл
        /// для него остаётся правильным; Writersword при открытии возвращает объекту
        /// смену вида с автором, временем и прежним видом.
        /// </summary>
        public const string DocxExtensionUri = "{6F3B2A51-8C4D-4E7A-9B1F-2D5C7A9E4B13}";

        /// <summary>Пространство имён элемента смены вида внутри расширения.</summary>
        public const string DocxNamespace = "urn:writersword:review:2026";

        /// <summary>Имя элемента смены вида внутри расширения.</summary>
        public const string DocxElement = "objectChange";

        private static readonly string[] ImageProperties =
        {
            nameof(ImageBlock.WidthPt), nameof(ImageBlock.HeightPt),
            nameof(ImageBlock.OffsetXPt), nameof(ImageBlock.OffsetYPt),
            nameof(ImageBlock.RotationDeg), nameof(ImageBlock.WrapMode),
            nameof(ImageBlock.Alignment), nameof(ImageBlock.Anchor),
            nameof(ImageBlock.AnchorPosition), nameof(ImageBlock.ZOrder),
            nameof(ImageBlock.PinnedPage), nameof(ImageBlock.AltText),
            nameof(ImageBlock.LockAspectRatio), nameof(ImageBlock.Opacity),
            nameof(ImageBlock.BorderColor), nameof(ImageBlock.BorderThicknessPt),
            nameof(ImageBlock.BorderDashStyle), nameof(ImageBlock.ShapeType),
            nameof(ImageBlock.CornerRadiusPt), nameof(ImageBlock.BorderAlign),
            nameof(ImageBlock.FlipHorizontal), nameof(ImageBlock.FlipVertical),
            nameof(ImageBlock.CropLeftFrac), nameof(ImageBlock.CropTopFrac),
            nameof(ImageBlock.CropRightFrac), nameof(ImageBlock.CropBottomFrac),
            nameof(ImageBlock.WrapPadTopPt), nameof(ImageBlock.WrapPadBottomPt),
            nameof(ImageBlock.WrapPadLeftPt), nameof(ImageBlock.WrapPadRightPt),
            nameof(ImageBlock.WrapSide)
        };

        private static readonly string[] ShapeProperties =
        {
            nameof(ShapeBlock.ShapeType), nameof(ShapeBlock.OffsetXPt), nameof(ShapeBlock.OffsetYPt),
            nameof(ShapeBlock.WidthPt), nameof(ShapeBlock.HeightPt), nameof(ShapeBlock.LockAspectRatio),
            nameof(ShapeBlock.RotationDeg), nameof(ShapeBlock.Opacity), nameof(ShapeBlock.FillColor),
            nameof(ShapeBlock.FillImageFileName), nameof(ShapeBlock.FillImageStretch),
            nameof(ShapeBlock.StrokeColor), nameof(ShapeBlock.StrokeThicknessPt), nameof(ShapeBlock.DashStyle),
            nameof(ShapeBlock.StrokeAlign), nameof(ShapeBlock.CornerRadiusPt), nameof(ShapeBlock.StartArrow),
            nameof(ShapeBlock.EndArrow), nameof(ShapeBlock.FlipHorizontal), nameof(ShapeBlock.FlipVertical),
            nameof(ShapeBlock.CropLeftFrac), nameof(ShapeBlock.CropTopFrac), nameof(ShapeBlock.CropRightFrac),
            nameof(ShapeBlock.CropBottomFrac), nameof(ShapeBlock.AltText), nameof(ShapeBlock.WrapMode),
            nameof(ShapeBlock.WrapSide), nameof(ShapeBlock.WrapPadTopPt), nameof(ShapeBlock.WrapPadBottomPt),
            nameof(ShapeBlock.WrapPadLeftPt), nameof(ShapeBlock.WrapPadRightPt), nameof(ShapeBlock.Alignment),
            nameof(ShapeBlock.Anchor), nameof(ShapeBlock.AnchorPosition), nameof(ShapeBlock.PinnedPage),
            nameof(ShapeBlock.ZOrder), nameof(ShapeBlock.InnerText), nameof(ShapeBlock.TextFontFamily),
            nameof(ShapeBlock.TextSizePt), nameof(ShapeBlock.TextColor), nameof(ShapeBlock.TextBold),
            nameof(ShapeBlock.TextItalic), nameof(ShapeBlock.TextAlign), nameof(ShapeBlock.TextVerticalAlign),
            nameof(ShapeBlock.TextInsetHorizontalPt), nameof(ShapeBlock.TextInsetVerticalPt)
        };

        // Свойства, по которым вид сравнивают при сравнении с файлом. Имя файла
        // заливки у сторон своё (файл пришёл из .docx), а место после Word записано
        // иначе (опора Word вместо смещения): сверяется то, что видно глазом, — с
        // допуском на перевод единиц.
        private static readonly HashSet<string> FileCompareSkipped = new(StringComparer.Ordinal)
        {
            nameof(ShapeBlock.FillImageFileName),
            nameof(ImageBlock.OffsetXPt), nameof(ImageBlock.OffsetYPt),
            nameof(ImageBlock.AnchorPosition), nameof(ImageBlock.PinnedPage),
            nameof(ImageBlock.ZOrder), nameof(ImageBlock.AltText), nameof(ImageBlock.Anchor)
        };

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

        private static readonly Dictionary<(Type, string), PropertyInfo?> PropertyCache = new();
        private static readonly object PropertyCacheLock = new();

        private static string[]? NamesFor(BlockModel block) => block switch
        {
            ImageBlock => ImageProperties,
            ShapeBlock => ShapeProperties,
            _ => null
        };

        private static PropertyInfo? PropertyOf(Type type, string name)
        {
            lock (PropertyCacheLock)
            {
                if (!PropertyCache.TryGetValue((type, name), out var property))
                {
                    property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                    PropertyCache[(type, name)] = property;
                }
                return property;
            }
        }

        /// <summary>Запись вида объекта; пусто — у объекта нет вида, который отслеживается.</summary>
        public static string Capture(BlockModel block)
        {
            var names = NamesFor(block);
            if (names is null) return string.Empty;

            var values = new Dictionary<string, object?>(names.Length, StringComparer.Ordinal);
            foreach (var name in names)
            {
                var property = PropertyOf(block.GetType(), name);
                if (property is null || !property.CanRead) continue;
                values[name] = property.GetValue(block);
            }

            return JsonSerializer.Serialize(values, JsonOptions);
        }

        /// <summary>Возвращает объекту вид из записи. Свойства, которых в записи нет, не трогаются.</summary>
        public static void Restore(BlockModel block, string appearance)
        {
            if (string.IsNullOrWhiteSpace(appearance)) return;

            using var parsed = JsonDocument.Parse(appearance);
            foreach (var item in parsed.RootElement.EnumerateObject())
            {
                var property = PropertyOf(block.GetType(), item.Name);
                if (property is null || !property.CanWrite) continue;

                object? value = item.Value.ValueKind == JsonValueKind.Null
                    ? null
                    : JsonSerializer.Deserialize(item.Value.GetRawText(), property.PropertyType, JsonOptions);
                property.SetValue(block, value);
            }
        }

        /// <summary>
        /// Копия документа в прежнем виде — для печати и выгрузки «Исходного документа»:
        /// картинки и фигуры со сменой вида, таблицы со сменой оформления, строками и
        /// столбцами, разделы со сменой параметров — такими, какими они были до правок.
        /// Текст копии не трогается: прежний текст показывает сам вид. Null — правок,
        /// меняющих вид, нет, копия не нужна.
        /// </summary>
        public static DocumentModel? WithPreviousAppearance(DocumentModel document)
        {
            if (!HasPreviousAppearance(document)) return null;

            var copy = DocumentCloner.Clone(document);
            foreach (var block in RevisionService.BlockRevisions(copy))
            {
                if (block is not IAppearanceTracked { Change: { } change } tracked) continue;
                Restore(block, change.Previous);
                tracked.Change = null;
            }

            if (copy.Sections.Count > 0)
            {
                foreach (var block in copy.Sections[0].Blocks)
                {
                    if (block is TableBlock table)
                    {
                        RejectTableStructure(table);
                    }
                    else if (block is BreakBlock { Section.FormatChange: { } sectionChange } sectionEnd)
                    {
                        SectionFrame.Restore(sectionEnd, sectionChange.Previous, copy);
                        sectionEnd.Section!.FormatChange = null;
                    }
                }
            }

            if (copy.PageSetupChange is { } pageChange)
            {
                SectionFrame.RestoreFinal(copy, pageChange.Previous);
                copy.PageSetupChange = null;
            }

            return copy;
        }

        /// <summary>Есть правки, которые меняют вид документа, а не его текст.</summary>
        private static bool HasPreviousAppearance(DocumentModel document)
        {
            foreach (var block in RevisionService.BlockRevisions(document))
                if (block is IAppearanceTracked { Change: not null }) return true;

            if (document.PageSetupChange is not null) return true;
            if (document.Sections.Count == 0) return false;

            foreach (var block in document.Sections[0].Blocks)
            {
                if (block is BreakBlock { Section.FormatChange: not null }) return true;
                if (block is TableBlock table && TableHasRevisions(table)) return true;
            }

            return false;
        }

        /// <summary>Таблица или вложенная в неё таблица со строками, столбцами или оформлением правкой.</summary>
        private static bool TableHasRevisions(TableBlock table)
        {
            if (table.FormatChange is not null || TableRevisions.HasMarks(table)) return true;

            foreach (var cell in table.Cells)
                if (cell.NestedTables is { Count: > 0 } nested)
                    foreach (var inner in nested)
                        if (TableHasRevisions(inner.Table)) return true;

            return false;
        }

        /// <summary>Таблица и вложенные в неё таблицы — со строками, столбцами и оформлением до правок.</summary>
        private static void RejectTableStructure(TableBlock table)
        {
            foreach (var cell in table.Cells)
                if (cell.NestedTables is { Count: > 0 } nested)
                    foreach (var inner in nested)
                        RejectTableStructure(inner.Table);

            if (table.FormatChange is not null || TableRevisions.HasMarks(table))
                TableRevisions.RejectStructure(table);
        }

        /// <summary>Вид, каким объект был бы при отклонении правок: прежний, если вид сменён правкой.</summary>
        public static string RejectView(BlockModel block)
            => block is IAppearanceTracked { Change: { } change } ? change.Previous : Capture(block);

        /// <summary>
        /// Правка вида после действия. Объект своей вставки смены вида не получает —
        /// его вид часть вставки; объект, вернувшийся к прежнему виду, теряет смену.
        /// Возвращает true, если отметка поменялась.
        /// </summary>
        public static bool Track(BlockModel block, string before, RevisionInfo info)
        {
            if (block is not IAppearanceTracked tracked) return false;
            if (tracked.Deleted is not null) return false;
            if (RevisionService.IsOwnInsertion(tracked.Inserted, info)) return false;

            string now = Capture(block);

            if (tracked.Change is { } existing)
            {
                if (!string.Equals(existing.Previous, now, StringComparison.Ordinal)) return false;
                tracked.Change = null;
                return true;
            }

            if (string.Equals(before, now, StringComparison.Ordinal)) return false;

            tracked.Change = new ObjectChange { Info = info.Clone(), Previous = before };
            return true;
        }

        /// <summary>
        /// Вид объекта файла отличается от вида объекта рукописи тем, что видно на
        /// листе: размер, поворот, отражение, обрезка, обтекание, рамка, заливка.
        /// Числа — с допуском на перевод единиц Word.
        /// </summary>
        public static bool DiffersVisibly(BlockModel current, BlockModel revised)
            => VisibleDifferences(current, revised, forFile: true).Count > 0;

        // Порядок наложения Word (relativeHeight) начинается с этого числа: порядок
        // редактора уходит в файл от него (ExportService.RelativeHeightFor).
        private const long WordRelativeHeightBase = 251658240L;

        /// <summary>Порядок наложения в шкале Word: порядок редактора и порядок из файла сравнимы.</summary>
        private static long NormalizedZOrder(object? value)
        {
            long z = value switch
            {
                int i => i,
                long l => l,
                double d => (long)d,
                _ => 0L
            };
            return z >= 1000000L ? z : WordRelativeHeightBase + Math.Clamp(z, 0L, 1000000L);
        }

        /// <summary>
        /// Свойства вида, которые различаются у двух объектов так, что это видно на
        /// листе. Место (смещения, опора, лист) сюда не входит — его сверяет
        /// <see cref="PlaceDiffers"/>. <paramref name="forFile"/> — сверка с файлом:
        /// имя файла заливки, замещающий текст и порядок наложения у сторон записаны
        /// по-своему и не сверяются.
        /// </summary>
        private static HashSet<string> VisibleDifferences(BlockModel current, BlockModel revised, bool forFile)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            var names = NamesFor(current);
            if (names is null || current.GetType() != revised.GetType()) return result;

            bool outlineHidden = !HasVisibleOutline(current) && !HasVisibleOutline(revised);
            bool noText = current is ShapeBlock { InnerText: null or "" } && revised is ShapeBlock { InnerText: null or "" };

            foreach (var name in names)
            {
                bool place = name is nameof(ImageBlock.OffsetXPt) or nameof(ImageBlock.OffsetYPt)
                    or nameof(ImageBlock.AnchorPosition) or nameof(ImageBlock.PinnedPage) or nameof(ImageBlock.Anchor);
                if (place) continue;
                if (forFile && FileCompareSkipped.Contains(name)) continue;
                if (name == nameof(ShapeBlock.FillImageFileName) && forFile) continue;

                // Толщина и штрих невидимой линии на листе не видны: Word пишет свою
                // толщину по умолчанию к линии без цвета.
                if (outlineHidden && OutlineDetails.Contains(name)) continue;

                // Оформление надписи без надписи не видно.
                bool textDetail = name.StartsWith("Text", StringComparison.Ordinal);
                if (noText && textDetail) continue;

                var property = PropertyOf(current.GetType(), name);
                if (property is null) continue;

                object? a = property.GetValue(current);
                object? b = property.GetValue(revised);

                // Не заданное у одной стороны оформление надписи — «как у документа»:
                // Word пишет его явно (шрифт, кегль, цвет), редактор — пустым.
                if (textDetail && (a is null || b is null)) continue;

                if (name == nameof(ImageBlock.ZOrder))
                {
                    if (NormalizedZOrder(a) != NormalizedZOrder(b)) result.Add(name);
                    continue;
                }

                if (a is double da && b is double db)
                {
                    double tolerance = name.EndsWith("Frac", StringComparison.Ordinal) ? 0.005 : 0.75;
                    if (Math.Abs(da - db) > tolerance) result.Add(name);
                    continue;
                }

                if (!Equals(a, b)) result.Add(name);
            }

            return result;
        }

        // Смещение в записи места Word: одно число EMU на ось.
        private static readonly System.Text.RegularExpressions.Regex PositionOffset =
            new(@"<wp:posOffset>(-?\d+)</wp:posOffset>", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        // Допуск места: 0,75 пт в EMU — перевод единиц Word.
        private const long PlaceToleranceEmu = 9525;

        /// <summary>
        /// Место объекта файла на листе отличается от места объекта рукописи. Место
        /// сверяется так, как его записал бы Word: от той же опоры, той же стороной
        /// или тем же смещением, — поэтому смещение рукописи и опора, прочитанная
        /// из .docx, сравниваются в одних единицах.
        /// </summary>
        public static bool PlaceDiffers(BlockModel current, BlockModel revised)
        {
            if (current is not IFloatingObject currentObject || revised is not IFloatingObject revisedObject) return false;
            if (current.GetType() != revised.GetType()) return false;

            var a = ExportService.PlacementOf(currentObject, AnchorPositionOf(current));
            var b = ExportService.PlacementOf(revisedObject, AnchorPositionOf(revised));

            if (a is null || b is null) return (a is null) != (b is null);

            return AxisDiffers(a.Value.Horizontal, b.Value.Horizontal)
                   || AxisDiffers(a.Value.Vertical, b.Value.Vertical);
        }

        private static bool AxisDiffers(string a, string b)
        {
            var matchA = PositionOffset.Match(a);
            var matchB = PositionOffset.Match(b);

            if (matchA.Success != matchB.Success) return true;
            if (!matchA.Success) return !string.Equals(a, b, StringComparison.Ordinal);

            // Опора — всё, кроме числа: она должна совпасть точно, смещение — с допуском.
            string restA = a.Remove(matchA.Groups[1].Index, matchA.Groups[1].Length);
            string restB = b.Remove(matchB.Groups[1].Index, matchB.Groups[1].Length);
            if (!string.Equals(restA, restB, StringComparison.Ordinal)) return true;

            long offsetA = long.Parse(matchA.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            long offsetB = long.Parse(matchB.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            return Math.Abs(offsetA - offsetB) > PlaceToleranceEmu;
        }

        private static TableFloatPosition? AnchorPositionOf(BlockModel block) => block switch
        {
            ImageBlock image => image.AnchorPosition,
            ShapeBlock shape => shape.AnchorPosition,
            _ => null
        };

        // Подробности линии по контуру: без видимой линии их на листе не видно.
        private static readonly HashSet<string> OutlineDetails = new(StringComparer.Ordinal)
        {
            nameof(ShapeBlock.StrokeThicknessPt), nameof(ShapeBlock.DashStyle), nameof(ShapeBlock.StrokeAlign),
            nameof(ShapeBlock.StrokeColor),
            nameof(ImageBlock.BorderThicknessPt), nameof(ImageBlock.BorderDashStyle), nameof(ImageBlock.BorderAlign),
            nameof(ImageBlock.BorderColor)
        };

        /// <summary>У объекта есть линия по контуру, которую видно: цвет и толщина.</summary>
        private static bool HasVisibleOutline(BlockModel block)
            => block is IFloatingObject obj
               && !string.IsNullOrWhiteSpace(obj.OutlineColor)
               && obj.OutlineThicknessPt > 0.0;

        /// <summary>
        /// Переносит видимый вид объекта файла на объект рукописи (без имени файла
        /// заливки — оно у сторон своё). Место переносится, только если объект
        /// сдвинут (<paramref name="withPlace"/>): иначе у рукописи остаётся её запись
        /// того же места.
        /// </summary>
        public static void CopyVisible(BlockModel from, BlockModel to, bool withPlace = false)
        {
            var names = NamesFor(to);
            if (names is null || from.GetType() != to.GetType()) return;

            // То, что сравнение не сверяет как невидимое, не переносится: иначе
            // смена вида показала бы то, чего на листе нет.
            bool outlineHidden = !HasVisibleOutline(from) && !HasVisibleOutline(to);
            bool noText = from is ShapeBlock { InnerText: null or "" } && to is ShapeBlock { InnerText: null or "" };

            foreach (var name in names)
            {
                bool place = name is nameof(ImageBlock.OffsetXPt) or nameof(ImageBlock.OffsetYPt)
                    or nameof(ImageBlock.AnchorPosition) or nameof(ImageBlock.PinnedPage) or nameof(ImageBlock.Anchor);

                if (place ? !withPlace : FileCompareSkipped.Contains(name)) continue;
                if (outlineHidden && OutlineDetails.Contains(name)) continue;

                bool textDetail = name.StartsWith("Text", StringComparison.Ordinal);
                if (noText && textDetail) continue;

                var property = PropertyOf(to.GetType(), name);
                if (property is null || !property.CanWrite) continue;

                object? value = property.GetValue(from);
                if (textDetail && (value is null || property.GetValue(to) is null)) continue;
                if (value is TableFloatPosition position) value = position.Clone();
                property.SetValue(to, value);
            }
        }

        /// <summary>
        /// Что сменилось в виде объекта — словами для панели рецензии. Сверяется то,
        /// что видно на листе, теми же правилами, что при сравнении с файлом: объект,
        /// побывавший в .docx, не получает в описание того, что Word лишь записал
        /// по-своему.
        /// </summary>
        public static string Describe(string before, BlockModel block)
        {
            var parts = new List<string>();

            try
            {
                var previousBlock = DocumentCloner.CloneBlock(block);
                Restore(previousBlock, before);

                var differences = VisibleDifferences(previousBlock, block, forFile: false);
                bool moved = PlaceDiffers(previousBlock, block);

                bool Changed(params string[] names)
                {
                    foreach (var name in names)
                        if (differences.Contains(name)) return true;
                    return false;
                }

                if (moved || Changed("Alignment")) parts.Add("перемещено");
                if (Changed("WidthPt", "HeightPt")) parts.Add("изменён размер");
                if (Changed("RotationDeg")) parts.Add("повёрнуто");
                if (Changed("FlipHorizontal", "FlipVertical")) parts.Add("отражено");
                if (Changed("CropLeftFrac", "CropTopFrac", "CropRightFrac", "CropBottomFrac")) parts.Add("обрезано");
                if (Changed("WrapMode", "WrapSide", "WrapPadTopPt", "WrapPadBottomPt", "WrapPadLeftPt", "WrapPadRightPt")) parts.Add("обтекание");
                if (Changed("ZOrder")) parts.Add("порядок наложения");
                if (Changed("Opacity")) parts.Add("прозрачность");
                if (Changed("ShapeType", "CornerRadiusPt")) parts.Add("форма");
                if (Changed("FillColor", "FillImageFileName", "FillImageStretch")) parts.Add("заливка");
                if (Changed("StrokeColor", "StrokeThicknessPt", "DashStyle", "StrokeAlign", "StartArrow", "EndArrow",
                        "BorderColor", "BorderThicknessPt", "BorderDashStyle", "BorderAlign")) parts.Add("линия");
                if (Changed("AltText")) parts.Add("замещающий текст");
                if (Changed("InnerText", "TextFontFamily", "TextSizePt", "TextColor", "TextBold", "TextItalic", "TextAlign", "TextVerticalAlign", "TextInsetHorizontalPt", "TextInsetVerticalPt")) parts.Add("надпись");

                if (parts.Count == 0) parts.Add("оформление");
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
            {
                parts.Add("оформление");
            }

            var sb = new StringBuilder();
            for (int i = 0; i < parts.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(parts[i]);
            }
            return sb.ToString();
        }
    }
}
