using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using DocumentFormat.OpenXml;
using Writersword.Modules.TextEditor.Models.Inline;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Настраиваемые эффекты букв в .docx.
    ///
    /// Word 2010+ пишет их в свойства рана отдельными элементами своего пространства
    /// имён w14: w14:glow, w14:shadow, w14:reflection, w14:textOutline, w14:textFill.
    /// Их Writersword читает и пишет — эффект переходит в Word и обратно один в один.
    ///
    /// То, чего Word не умеет (контур снаружи букв, длинная тень), пишется в w14
    /// ближайшим видом, а полные настройки — рядом, в элемент wsx:effects своего
    /// пространства имён. Оба пространства объявлены пропускаемыми (mc:Ignorable):
    /// Word, не зная wsx, файл не отвергает, а показывает упрощённый вид; Writersword
    /// при открытии того же файла берёт полные настройки из wsx:effects.
    ///
    /// Элементы строятся и читаются без привязки к классам OpenXML SDK — по имени и
    /// пространству имён: так одинаково читаются и файлы Word, и файлы Writersword.
    /// </summary>
    internal static class DocxTextEffects
    {
        /// <summary>Пространство имён Word 2010 (w14).</summary>
        public const string W14Namespace = "http://schemas.microsoft.com/office/word/2010/wordml";

        /// <summary>Пространство имён совместимости разметки (mc).</summary>
        public const string McNamespace = "http://schemas.openxmlformats.org/markup-compatibility/2006";

        /// <summary>Своё пространство имён Writersword для полных настроек эффектов.</summary>
        public const string WsxNamespace = "urn:writersword:docx:effects:2026";

        private const double EmuPerPoint = 12700.0;
        private const double AngleUnitsPerDegree = 60000.0;
        private const double PercentUnits = 100000.0;

        // Цвета темы Office по умолчанию — для эффектов, заданных цветом темы
        // (w14:schemeClr). Своя тема документа у Writersword не хранится.
        private static readonly Dictionary<string, string> SchemeColors = new(StringComparer.Ordinal)
        {
            ["tx1"] = "000000", ["dk1"] = "000000",
            ["bg1"] = "FFFFFF", ["lt1"] = "FFFFFF",
            ["tx2"] = "44546A", ["dk2"] = "44546A",
            ["bg2"] = "E7E6E6", ["lt2"] = "E7E6E6",
            ["accent1"] = "4472C4",
            ["accent2"] = "ED7D31",
            ["accent3"] = "A5A5A5",
            ["accent4"] = "FFC000",
            ["accent5"] = "5B9BD5",
            ["accent6"] = "70AD47",
            ["hlink"] = "0563C1",
            ["folHlink"] = "954F72"
        };

        // ── Объявления пространств имён ──────────────────────────────────

        /// <summary>
        /// Объявляет w14, mc и wsx у корня части и помечает w14 и wsx пропускаемыми.
        /// Без этого Word 2007 и строгие читатели сочли бы элементы эффектов ошибкой.
        /// </summary>
        public static void DeclareNamespaces(OpenXmlPartRootElement root)
        {
            var declared = root.NamespaceDeclarations.Select(d => d.Key).ToHashSet(StringComparer.Ordinal);

            if (!declared.Contains("mc")) root.AddNamespaceDeclaration("mc", McNamespace);
            if (!declared.Contains("w14")) root.AddNamespaceDeclaration("w14", W14Namespace);
            if (!declared.Contains("wsx")) root.AddNamespaceDeclaration("wsx", WsxNamespace);

            root.MCAttributes = new MarkupCompatibilityAttributes { Ignorable = "w14 wsx" };
        }

        // ── Чтение ───────────────────────────────────────────────────────

        /// <summary>
        /// Читает эффекты одного уровня каскада (стиль, ран) в накопленное
        /// форматирование: эффект, заданный на этом уровне, заменяет унаследованный.
        /// </summary>
        public static void Read(OpenXmlElement container, RunFormat target)
        {
            foreach (var element in container.ChildElements)
            {
                if (element.NamespaceUri == W14Namespace)
                {
                    switch (element.LocalName)
                    {
                        case "glow":
                            target.GlowEffect = ReadGlow(element);
                            break;
                        case "shadow":
                            target.ShadowEffect = ReadShadow(element);
                            break;
                        case "reflection":
                            target.ReflectionEffect = ReadReflection(element);
                            break;
                        case "textOutline":
                            target.OutlineEffect = ReadOutline(element);
                            break;
                        case "textFill":
                            target.HollowFill = Child(element, "noFill") is not null;
                            break;
                    }
                }
                else if (element.NamespaceUri == WsxNamespace && element.LocalName == "effects")
                {
                    string? json = Attr(element, "val");
                    if (string.IsNullOrWhiteSpace(json)) continue;

                    try
                    {
                        target.ExactEffects = TextEffects.Normalize(JsonSerializer.Deserialize<TextEffects>(json));
                    }
                    catch (JsonException)
                    {
                        // Повреждённый раздел Writersword — остаются эффекты w14.
                    }
                }
            }
        }

        private static TextGlowEffect? ReadGlow(OpenXmlElement element)
        {
            double radius = Emu(Attr(element, "rad"));
            var (color, transparency) = ReadColor(element);
            if (radius <= 0 || color is null) return null;

            return new TextGlowEffect { Color = color, Transparency = transparency, RadiusPt = radius };
        }

        private static TextShadowEffect? ReadShadow(OpenXmlElement element)
        {
            var (color, transparency) = ReadColor(element);
            if (color is null) return null;

            return new TextShadowEffect
            {
                Color = color,
                Transparency = transparency,
                BlurPt = Emu(Attr(element, "blurRad")),
                DistancePt = Emu(Attr(element, "dist")),
                AngleDeg = Angle(Attr(element, "dir"))
            };
        }

        private static TextReflectionEffect? ReadReflection(OpenXmlElement element)
        {
            // stA — непрозрачность у букв; endPos — докуда отражение тает, доля высоты.
            double startOpacity = Percent(Attr(element, "stA"), 0.5);
            double endPos = Percent(Attr(element, "endPos"), 0.9);
            if (startOpacity <= 0 || endPos <= 0) return null;

            return new TextReflectionEffect
            {
                Transparency = Math.Clamp(1.0 - startOpacity, 0.0, 1.0),
                Size = Math.Clamp(endPos, 0.0, 1.0),
                DistancePt = Emu(Attr(element, "dist")),
                BlurPt = Emu(Attr(element, "blurRad"))
            };
        }

        private static TextOutlineEffect? ReadOutline(OpenXmlElement element)
        {
            // Контур без линии (w14:noFill) — контура нет.
            if (Child(element, "noFill") is not null) return null;

            var (color, _) = ReadColor(element);
            if (color is null) return null;

            double width = Emu(Attr(element, "w"));

            return new TextOutlineEffect
            {
                Color = color,
                WidthPt = width > 0 ? width : 0.75,
                Dash = DashFromOoxml(Attr(Child(element, "prstDash"), "val"))
            };
        }

        private static OutlineDash DashFromOoxml(string? value) => value switch
        {
            null or "solid" => OutlineDash.Solid,
            "dot" or "sysDot" => OutlineDash.Dot,
            "dash" or "sysDash" => OutlineDash.Dash,
            "lgDash" => OutlineDash.LongDash,
            _ => OutlineDash.DashDot
        };

        /// <summary>
        /// Цвет эффекта и его прозрачность: w14:srgbClr, w14:schemeClr или w14:prstClr —
        /// прямо в элементе либо в w14:solidFill / первой точке w14:gradFill. В w14
        /// w14:alpha — прозрачность (60000 — 60 %), а не непрозрачность.
        /// </summary>
        private static (string? Color, double Transparency) ReadColor(OpenXmlElement element)
        {
            var colorElement = FindColorElement(element);
            if (colorElement is null) return (null, 0);

            string? hex = colorElement.LocalName switch
            {
                "srgbClr" => Attr(colorElement, "val"),
                "schemeClr" => SchemeColors.TryGetValue(Attr(colorElement, "val") ?? string.Empty, out var scheme)
                    ? scheme
                    : "000000",
                "prstClr" => Attr(colorElement, "val") == "white" ? "FFFFFF" : "000000",
                _ => null
            };
            if (hex is null || hex.Length != 6) return (null, 0);

            double transparency = Percent(Attr(Child(colorElement, "alpha"), "val"), 0.0);

            // Оттенки цвета темы: яркость умножается на lumMod и сдвигается на lumOff.
            double? lumMod = Child(colorElement, "lumMod") is { } mod ? Percent(Attr(mod, "val"), 1.0) : null;
            double? lumOff = Child(colorElement, "lumOff") is { } off ? Percent(Attr(off, "val"), 0.0) : null;
            if (lumMod is not null || lumOff is not null)
                hex = AdjustLuminance(hex, lumMod ?? 1.0, lumOff ?? 0.0);

            return ("#" + hex.ToUpperInvariant(), Math.Clamp(transparency, 0.0, 1.0));
        }

        private static OpenXmlElement? FindColorElement(OpenXmlElement element)
        {
            foreach (var child in element.ChildElements)
            {
                if (child.NamespaceUri != W14Namespace) continue;

                switch (child.LocalName)
                {
                    case "srgbClr":
                    case "schemeClr":
                    case "prstClr":
                        return child;
                    case "solidFill":
                        return FindColorElement(child);
                    case "gradFill":
                        var stops = Child(child, "gsLst");
                        var first = stops is null ? null : Child(stops, "gs");
                        if (first is not null) return FindColorElement(first);
                        break;
                }
            }
            return null;
        }

        private static string AdjustLuminance(string hex, double lumMod, double lumOff)
        {
            int rgb = int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            double r = ((rgb >> 16) & 0xFF) / 255.0;
            double g = ((rgb >> 8) & 0xFF) / 255.0;
            double b = (rgb & 0xFF) / 255.0;

            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double l = (max + min) / 2.0;
            double h = 0, s = 0;

            if (max - min > 1e-9)
            {
                double d = max - min;
                s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
                if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
                else if (max == g) h = (b - r) / d + 2;
                else h = (r - g) / d + 4;
                h /= 6.0;
            }

            l = Math.Clamp(l * lumMod + lumOff, 0.0, 1.0);

            double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            double p = 2 * l - q;

            static double HueToRgb(double p, double q, double t)
            {
                if (t < 0) t += 1;
                if (t > 1) t -= 1;
                if (t < 1.0 / 6) return p + (q - p) * 6 * t;
                if (t < 0.5) return q;
                if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
                return p;
            }

            double nr = s <= 1e-9 ? l : HueToRgb(p, q, h + 1.0 / 3);
            double ng = s <= 1e-9 ? l : HueToRgb(p, q, h);
            double nb = s <= 1e-9 ? l : HueToRgb(p, q, h - 1.0 / 3);

            return string.Create(CultureInfo.InvariantCulture,
                $"{(int)Math.Round(nr * 255):X2}{(int)Math.Round(ng * 255):X2}{(int)Math.Round(nb * 255):X2}");
        }

        // ── Запись ───────────────────────────────────────────────────────

        /// <summary>
        /// Элементы эффектов для свойств рана в порядке схемы w14: glow, shadow,
        /// reflection, textOutline, textFill; за ними — wsx:effects, если в эффектах
        /// есть то, чего Word не умеет.
        /// </summary>
        public static List<OpenXmlElement> Write(TextEffects? effects)
        {
            var elements = new List<OpenXmlElement>();
            if (effects is null || effects.IsEmpty) return elements;

            if (effects.Glow is { } glow)
            {
                var element = W14("glow");
                SetAttr(element, "rad", Emu(glow.RadiusPt));
                element.AppendChild(ColorElement(glow.Color, glow.Transparency));
                elements.Add(element);
            }

            if (effects.Shadow is { } shadow)
            {
                // Длинная тень у Word — обычная тень без размытия на то же расстояние.
                var element = W14("shadow");
                SetAttr(element, "blurRad", Emu(shadow.IsLong ? 0 : shadow.BlurPt));
                SetAttr(element, "dist", Emu(shadow.DistancePt));
                SetAttr(element, "dir", Angle(shadow.AngleDeg));
                SetAttr(element, "sx", "100000");
                SetAttr(element, "sy", "100000");
                SetAttr(element, "kx", "0");
                SetAttr(element, "ky", "0");
                SetAttr(element, "algn", "tl");
                element.AppendChild(ColorElement(shadow.Color, shadow.Transparency));
                elements.Add(element);
            }

            if (effects.Reflection is { } reflection)
            {
                var element = W14("reflection");
                SetAttr(element, "blurRad", Emu(reflection.BlurPt));
                SetAttr(element, "stA", Percent(1.0 - reflection.Transparency));
                SetAttr(element, "stPos", "0");
                SetAttr(element, "endA", "300");
                SetAttr(element, "endPos", Percent(reflection.Size));
                SetAttr(element, "dist", Emu(reflection.DistancePt));
                SetAttr(element, "dir", "5400000");
                SetAttr(element, "fadeDir", "5400000");
                SetAttr(element, "sx", "100000");
                SetAttr(element, "sy", "-100000");
                SetAttr(element, "kx", "0");
                SetAttr(element, "ky", "0");
                SetAttr(element, "algn", "bl");
                elements.Add(element);
            }

            if (effects.Outline is { } outline)
            {
                // Контур снаружи у Word — контур той же толщины по краю букв.
                var element = W14("textOutline");
                SetAttr(element, "w", Emu(outline.WidthPt));
                SetAttr(element, "cap", "flat");
                SetAttr(element, "cmpd", "sng");
                SetAttr(element, "algn", "ctr");

                var fill = W14("solidFill");
                fill.AppendChild(ColorElement(outline.Color, 0));
                element.AppendChild(fill);

                var dash = W14("prstDash");
                SetAttr(dash, "val", outline.Dash switch
                {
                    OutlineDash.Dash => "dash",
                    OutlineDash.Dot => "sysDot",
                    OutlineDash.DashDot => "dashDot",
                    OutlineDash.LongDash => "lgDash",
                    _ => "solid"
                });
                element.AppendChild(dash);
                element.AppendChild(W14("round"));
                elements.Add(element);

                if (outline.Hollow)
                {
                    var textFill = W14("textFill");
                    textFill.AppendChild(W14("noFill"));
                    elements.Add(textFill);
                }
            }

            if (effects.HasWriterswordOnlyParts)
            {
                var exact = new OpenXmlUnknownElement("wsx", "effects", WsxNamespace);
                exact.SetAttribute(new OpenXmlAttribute("wsx", "val", WsxNamespace,
                    JsonSerializer.Serialize(effects)));
                elements.Add(exact);
            }

            return elements;
        }

        private static OpenXmlUnknownElement ColorElement(string color, double transparency)
        {
            var element = W14("srgbClr");
            SetAttr(element, "val", Hex6(color));

            if (transparency > 0.0005)
            {
                var alpha = W14("alpha");
                SetAttr(alpha, "val", Percent(transparency));
                element.AppendChild(alpha);
            }

            return element;
        }

        // ── Мелочи ───────────────────────────────────────────────────────

        private static OpenXmlUnknownElement W14(string localName)
            => new("w14", localName, W14Namespace);

        private static void SetAttr(OpenXmlElement element, string localName, string value)
            => element.SetAttribute(new OpenXmlAttribute("w14", localName, W14Namespace, value));

        private static OpenXmlElement? Child(OpenXmlElement? element, string localName)
        {
            if (element is null) return null;
            foreach (var child in element.ChildElements)
                if (child.LocalName == localName
                    && (child.NamespaceUri == W14Namespace || child.NamespaceUri == WsxNamespace))
                    return child;
            return null;
        }

        private static string? Attr(OpenXmlElement? element, string localName)
        {
            if (element is null) return null;
            foreach (var attribute in element.GetAttributes())
                if (attribute.LocalName == localName) return attribute.Value;
            return null;
        }

        private static double Emu(string? value)
            => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long emu)
                ? Math.Max(0, emu / EmuPerPoint)
                : 0;

        private static string Emu(double points)
            => ((long)Math.Round(Math.Max(0, points) * EmuPerPoint)).ToString(CultureInfo.InvariantCulture);

        private static double Angle(string? value)
            => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long units)
                ? units / AngleUnitsPerDegree
                : 0;

        private static string Angle(double degrees)
        {
            double normalized = ((degrees % 360.0) + 360.0) % 360.0;
            return ((long)Math.Round(normalized * AngleUnitsPerDegree)).ToString(CultureInfo.InvariantCulture);
        }

        private static double Percent(string? value, double fallback)
            => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long units)
                ? units / PercentUnits
                : fallback;

        private static string Percent(double fraction)
            => ((long)Math.Round(Math.Clamp(fraction, 0.0, 1.0) * PercentUnits)).ToString(CultureInfo.InvariantCulture);

        private static string Hex6(string color)
        {
            string value = (color ?? string.Empty).Trim().TrimStart('#');
            if (value.Length == 8) value = value.Substring(2);
            return value.Length == 6 ? value.ToUpperInvariant() : "000000";
        }
    }
}
