using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Page;
using Writersword.Modules.TextEditor.Rendering;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Параметры раздела — для правки параметров страницы.
    ///
    /// Параметры — то, что задаёт лист раздела: размер и ориентация бумаги, поля,
    /// переплёт, расстояния до колонтитулов, колонки, выравнивание текста по высоте
    /// листа, рамка страницы и номера строк (у Word — w:sectPr, правка — w:sectPrChange).
    /// Раздел, который кончается разрывом, держит их в разрыве (BreakBlock.Section);
    /// последний раздел — в параметрах страницы и колонках документа и в
    /// DocumentModel.FinalSection.
    ///
    /// Запись пишется строкой JSON: она ложится прежним видом в
    /// <see cref="SectionLayout.FormatChange"/> или <see cref="DocumentModel.PageSetupChange"/>
    /// и возвращается разделу при отклонении.
    /// </summary>
    public static class SectionFrame
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

        /// <summary>Параметры раздела целиком.</summary>
        public sealed class Look
        {
            public TextEditorPageSettings Page { get; set; } = new();
            public SectionColumns Columns { get; set; } = new();
            public SectionVerticalAlignment VerticalAlignment { get; set; }
            public SectionPageBorders? PageBorders { get; set; }
            public SectionLineNumbering? LineNumbering { get; set; }
        }

        // ── Запись ───────────────────────────────────────────────────────

        /// <summary>Параметры раздела, который кончается этим разрывом.</summary>
        public static string Capture(BreakBlock sectionEnd, DocumentModel document)
        {
            var layout = sectionEnd.Section;
            return Serialize(new Look
            {
                Page = (layout?.Page ?? document.PageSettings).Clone(),
                Columns = ColumnFrames.ColumnsOf(document, sectionEnd).Clone(),
                VerticalAlignment = layout?.VerticalAlignment ?? SectionVerticalAlignment.Top,
                PageBorders = layout?.PageBorders?.Clone(),
                LineNumbering = layout?.LineNumbering?.Clone()
            });
        }

        /// <summary>Параметры последнего раздела документа.</summary>
        public static string CaptureFinal(DocumentModel document)
        {
            var final = document.FinalSection;
            return Serialize(new Look
            {
                Page = document.PageSettings.Clone(),
                Columns = ColumnFrames.FinalColumns(document).Clone(),
                VerticalAlignment = final?.VerticalAlignment ?? SectionVerticalAlignment.Top,
                PageBorders = final?.PageBorders?.Clone(),
                LineNumbering = final?.LineNumbering?.Clone()
            });
        }

        /// <summary>Параметры раздела, какими они были бы при отклонении правки.</summary>
        public static string RejectView(BreakBlock sectionEnd, DocumentModel document)
            => sectionEnd.Section?.FormatChange?.Previous ?? Capture(sectionEnd, document);

        /// <summary>Параметры последнего раздела, какими они были бы при отклонении правки.</summary>
        public static string RejectViewFinal(DocumentModel document)
            => document.PageSetupChange?.Previous ?? CaptureFinal(document);

        private static string Serialize(Look look) => JsonSerializer.Serialize(look, JsonOptions);

        /// <summary>
        /// Параметры раздела, прочитанные из файла: лист и колонки у раздела свои. Нужна
        /// чтению прежних параметров (w:sectPrChange).
        /// </summary>
        public static string CaptureLayout(SectionLayout layout, TextEditorPageSettings page, SectionColumns columns)
            => Serialize(new Look
            {
                Page = page.Clone(),
                Columns = columns.Clone(),
                VerticalAlignment = layout.VerticalAlignment,
                PageBorders = layout.PageBorders?.Clone(),
                LineNumbering = layout.LineNumbering?.Clone()
            });

        /// <summary>Запись, прочитанная обратно; null — запись не читается.</summary>
        public static Look? Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                return JsonSerializer.Deserialize<Look>(json, JsonOptions);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // ── Возврат ──────────────────────────────────────────────────────

        /// <summary>Возвращает разделу, который кончается этим разрывом, параметры из записи.</summary>
        public static void Restore(BreakBlock sectionEnd, string json)
        {
            if (Parse(json) is not { } look) return;

            var layout = sectionEnd.Section ??= new SectionLayout();
            if (layout.Page is null) layout.Page = look.Page.Clone();
            else CopyPage(look.Page, layout.Page);

            layout.Columns = look.Columns.Clone();
            layout.VerticalAlignment = look.VerticalAlignment;
            layout.PageBorders = look.PageBorders?.Clone();
            layout.LineNumbering = look.LineNumbering?.Clone();
        }

        /// <summary>
        /// Возвращает последнему разделу параметры из записи: лист — в параметры
        /// страницы документа (тот же объект: на него смотрят полотно и лента),
        /// колонки — в колонки документа или раздела.
        /// </summary>
        public static void RestoreFinal(DocumentModel document, string json)
        {
            if (Parse(json) is not { } look) return;

            CopyPage(look.Page, document.PageSettings);

            var columns = look.Columns;
            if (columns.Columns is { Count: > 0 })
            {
                (document.FinalSection ??= new SectionLayout()).Columns = columns.Clone();
            }
            else
            {
                var settings = (document.Sections.Count > 0 ? document.Sections[0].ColumnSettings : null)
                    ?? document.ColumnSettings;
                settings.ColumnCount = Math.Max(1, columns.Count);
                settings.GapMm = columns.GapMm;
                settings.ShowSeparator = columns.Separator;
                if (document.FinalSection is { Columns: not null } final) final.Columns = null;
            }

            bool decorated = look.VerticalAlignment != SectionVerticalAlignment.Top
                             || look.PageBorders is not null
                             || look.LineNumbering is not null;
            if (document.FinalSection is null && !decorated) return;

            var layout = document.FinalSection ??= new SectionLayout();
            layout.VerticalAlignment = look.VerticalAlignment;
            layout.PageBorders = look.PageBorders?.Clone();
            layout.LineNumbering = look.LineNumbering?.Clone();
        }

        private static void CopyPage(TextEditorPageSettings from, TextEditorPageSettings to)
        {
            to.PaperSize = from.PaperSize;
            to.WidthMm = from.WidthMm;
            to.HeightMm = from.HeightMm;
            to.Orientation = from.Orientation;
            to.MarginTopMm = from.MarginTopMm;
            to.MarginBottomMm = from.MarginBottomMm;
            to.MarginLeftMm = from.MarginLeftMm;
            to.MarginRightMm = from.MarginRightMm;
            to.MarginGutterMm = from.MarginGutterMm;
            to.HeaderDistanceMm = from.HeaderDistanceMm;
            to.FooterDistanceMm = from.FooterDistanceMm;
        }

        // ── Сравнение ────────────────────────────────────────────────────

        /// <summary>
        /// Что различается в параметрах двух записей: «size», «orientation», «margins»,
        /// «gutter», «distances», «columns», «valign», «borders», «lines». Пусто —
        /// параметры одни и те же. <paramref name="forFile"/> — с допуском перевода
        /// единиц Word (твипы).
        /// </summary>
        public static HashSet<string> Differences(Look a, Look b, bool forFile = false)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            double tolerance = forFile ? 0.1 : 0.001;

            bool Differ(double x, double y) => Math.Abs(x - y) > tolerance;

            if (a.Page.Orientation != b.Page.Orientation) result.Add("orientation");
            if (Differ(a.Page.GetPhysicalWidthMm(), b.Page.GetPhysicalWidthMm())
                || Differ(a.Page.GetPhysicalHeightMm(), b.Page.GetPhysicalHeightMm()))
                result.Add("size");
            if (Differ(a.Page.MarginTopMm, b.Page.MarginTopMm) || Differ(a.Page.MarginBottomMm, b.Page.MarginBottomMm)
                || Differ(a.Page.MarginLeftMm, b.Page.MarginLeftMm) || Differ(a.Page.MarginRightMm, b.Page.MarginRightMm))
                result.Add("margins");
            if (Differ(a.Page.MarginGutterMm, b.Page.MarginGutterMm)) result.Add("gutter");
            if (Differ(a.Page.HeaderDistanceMm, b.Page.HeaderDistanceMm) || Differ(a.Page.FooterDistanceMm, b.Page.FooterDistanceMm))
                result.Add("distances");

            if (!SameColumns(a.Columns, b.Columns, tolerance)) result.Add("columns");
            if (a.VerticalAlignment != b.VerticalAlignment) result.Add("valign");
            if (!SameJson(a.PageBorders, b.PageBorders)) result.Add("borders");
            if (!SameJson(a.LineNumbering, b.LineNumbering)) result.Add("lines");

            return result;
        }

        private static bool SameColumns(SectionColumns a, SectionColumns b, double tolerance)
        {
            int countA = a.Columns is { Count: > 1 } ownA ? ownA.Count : Math.Max(1, a.Count);
            int countB = b.Columns is { Count: > 1 } ownB ? ownB.Count : Math.Max(1, b.Count);
            if (countA != countB) return false;
            if (countA <= 1) return true;
            if ((a.Separator && a.Count > 1) != (b.Separator && b.Count > 1)) return false;

            bool unequalA = a.Columns is { Count: > 1 };
            bool unequalB = b.Columns is { Count: > 1 };
            if (unequalA != unequalB) return false;

            if (!unequalA) return Math.Abs(a.GapMm - b.GapMm) <= tolerance;

            for (int i = 0; i < a.Columns!.Count; i++)
            {
                if (Math.Abs(a.Columns[i].WidthMm - b.Columns![i].WidthMm) > tolerance) return false;
                if (Math.Abs(a.Columns[i].SpaceAfterMm - b.Columns[i].SpaceAfterMm) > tolerance) return false;
            }
            return true;
        }

        private static bool SameJson(object? a, object? b)
            => string.Equals(
                a is null ? "null" : JsonSerializer.Serialize(a, a.GetType(), JsonOptions),
                b is null ? "null" : JsonSerializer.Serialize(b, b.GetType(), JsonOptions),
                StringComparison.Ordinal);

        /// <summary>Параметры двух записей различаются.</summary>
        public static bool Differs(string a, string b, bool forFile = false)
        {
            if (string.Equals(a, b, StringComparison.Ordinal)) return false;
            var lookA = Parse(a);
            var lookB = Parse(b);
            if (lookA is null || lookB is null) return !string.Equals(a, b, StringComparison.Ordinal);
            return Differences(lookA, lookB, forFile).Count > 0;
        }

        /// <summary>Что сменилось в параметрах раздела — словами для панели рецензии.</summary>
        public static string Describe(string previous, string current)
        {
            var parts = new List<string>();
            if (Parse(previous) is { } before && Parse(current) is { } now)
            {
                var differences = Differences(before, now);
                if (differences.Contains("size")) parts.Add("размер листа");
                if (differences.Contains("orientation")) parts.Add("ориентация");
                if (differences.Contains("margins")) parts.Add("поля");
                if (differences.Contains("gutter")) parts.Add("переплёт");
                if (differences.Contains("distances")) parts.Add("колонтитулы");
                if (differences.Contains("columns")) parts.Add("колонки");
                if (differences.Contains("valign")) parts.Add("выравнивание по высоте");
                if (differences.Contains("borders")) parts.Add("рамка страницы");
                if (differences.Contains("lines")) parts.Add("номера строк");
            }

            if (parts.Count == 0) parts.Add("параметры");

            var sb = new StringBuilder();
            for (int i = 0; i < parts.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(parts[i]);
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Пункт рецензии параметров раздела: раздел, который кончается разрывом
    /// <see cref="Break"/>, или последний раздел документа. В документе не живёт —
    /// служит ключом пункта панели и тем, что решает пункт
    /// (RevisionService.ResolveBlocks).
    /// </summary>
    public sealed class SectionRevisionPart : BlockModel
    {
        public override BlockType BlockType => BlockType.Break;

        /// <summary>Разрыв, которым кончается раздел; null — последний раздел документа.</summary>
        public BreakBlock? Break { get; init; }

        public override bool Equals(object? obj)
            => obj is SectionRevisionPart other && ReferenceEquals(other.Break, Break);

        public override int GetHashCode() => Break?.Id.GetHashCode() ?? 0x5ec;
    }
}
