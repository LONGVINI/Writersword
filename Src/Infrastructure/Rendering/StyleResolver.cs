using System.Collections.Generic;
using Writersword.Modules.TextEditor.Models.Styles;

using RenderAlignment = Writersword.Core.Models.Rendering.TextAlignment;

namespace Writersword.Infrastructure.Rendering
{
    /// <summary>
    /// Резолвер стилей документа.
    /// Строит индекс стилей и резолвирует свойства шрифта/абзаца
    /// с обходом цепочки BasedOn.
    /// Используется SKTextRenderer для определения шрифта параграфа
    /// когда Run не задаёт форматирование явно.
    /// Создаётся один раз на документ — переиспользуется при каждом layout-проходе.
    /// </summary>
    public sealed class StyleResolver
    {
        private readonly Dictionary<string, DocumentStyle> _index;

        /// <summary>Имя стиля по умолчанию — используется если StyleName параграфа null.</summary>
        public const string DefaultStyleName = "Normal";

        /// <summary>Шрифт по умолчанию — используется если стиль не задаёт FontFamily.</summary>
        public const string FallbackFontFamily = "Times New Roman";

        /// <summary>Размер шрифта по умолчанию в pt.</summary>
        public const float FallbackFontSizePt = 14f;

        /// <summary>Межстрочный интервал по умолчанию.</summary>
        public const float FallbackLineSpacing = 1.0f;

        /// <summary>Интервал после абзаца по умолчанию в pt.</summary>
        public const float FallbackSpaceAfterPt = 8f;

        public StyleResolver(IEnumerable<DocumentStyle> styles)
        {
            _index = new Dictionary<string, DocumentStyle>(
                System.StringComparer.OrdinalIgnoreCase);

            foreach (var style in styles)
                if (!string.IsNullOrEmpty(style.Name))
                    _index[style.Name] = style;
        }

        // ── Резолверы шрифта ──────────────────────────────────────────────

        /// <summary>
        /// Резолвирует FontFamily из цепочки стилей BasedOn.
        /// Если цепочка не даёт результата — возвращает FallbackFontFamily.
        /// </summary>
        public string ResolveFontFamily(string? styleName)
        {
            foreach (var style in WalkChain(styleName))
                if (!string.IsNullOrEmpty(style.RunProperties?.FontFamily))
                    return style.RunProperties.FontFamily;

            return FallbackFontFamily;
        }

        /// <summary>
        /// Резолвирует FontSize из цепочки стилей BasedOn.
        /// Если цепочка не даёт результата — возвращает FallbackFontSizePt.
        /// </summary>
        public float ResolveFontSize(string? styleName)
        {
            foreach (var style in WalkChain(styleName))
                if (style.RunProperties?.FontSize.HasValue == true)
                    return (float)style.RunProperties.FontSize.Value;

            return FallbackFontSizePt;
        }

        /// <summary>
        /// Резолвирует IsBold из цепочки стилей BasedOn.
        /// </summary>
        public bool ResolveBold(string? styleName)
        {
            foreach (var style in WalkChain(styleName))
                if (style.RunProperties is not null)
                    return style.RunProperties.IsBold;

            return false;
        }

        /// <summary>
        /// Резолвирует IsItalic из цепочки стилей BasedOn.
        /// </summary>
        public bool ResolveItalic(string? styleName)
        {
            foreach (var style in WalkChain(styleName))
                if (style.RunProperties is not null)
                    return style.RunProperties.IsItalic;

            return false;
        }

        // ── Резолверы абзаца ──────────────────────────────────────────────

        /// <summary>
        /// Резолвирует межстрочный интервал из цепочки стилей BasedOn.
        /// Возвращает множитель (1.0 = одинарный, 1.5, 2.0).
        /// </summary>
        public float ResolveLineSpacing(string? styleName)
        {
            foreach (var style in WalkChain(styleName))
                if (style.ParagraphProperties?.LineSpacingValue.HasValue == true)
                    return (float)style.ParagraphProperties.LineSpacingValue.Value;

            return FallbackLineSpacing;
        }

        /// <summary>
        /// Резолвирует интервал до абзаца в pt из цепочки стилей BasedOn.
        /// </summary>
        public float ResolveSpaceBefore(string? styleName)
        {
            foreach (var style in WalkChain(styleName))
                if (style.ParagraphProperties?.SpaceBefore.HasValue == true)
                    return (float)style.ParagraphProperties.SpaceBefore.Value;

            return 0f;
        }

        /// <summary>
        /// Резолвирует интервал после абзаца в pt из цепочки стилей BasedOn.
        /// </summary>
        public float ResolveSpaceAfter(string? styleName)
        {
            foreach (var style in WalkChain(styleName))
                if (style.ParagraphProperties?.SpaceAfter.HasValue == true)
                    return (float)style.ParagraphProperties.SpaceAfter.Value;

            return FallbackSpaceAfterPt;
        }

        /// <summary>
        /// Резолвирует левый отступ абзаца в pt из цепочки стилей BasedOn.
        /// </summary>
        public float ResolveLeftIndent(string? styleName)
        {
            foreach (var style in WalkChain(styleName))
                if (style.ParagraphProperties?.LeftIndent.HasValue == true)
                    return (float)style.ParagraphProperties.LeftIndent.Value;

            return 0f;
        }

        /// <summary>
        /// Резолвирует правый отступ абзаца в pt из цепочки стилей BasedOn.
        /// </summary>
        public float ResolveRightIndent(string? styleName)
        {
            foreach (var style in WalkChain(styleName))
                if (style.ParagraphProperties?.RightIndent.HasValue == true)
                    return (float)style.ParagraphProperties.RightIndent.Value;

            return 0f;
        }

        /// <summary>
        /// Резолвирует выравнивание абзаца из цепочки стилей BasedOn.
        /// Возвращает Writersword.Core.Models.Rendering.TextAlignment
        /// через конвертацию из модельного enum через int.
        /// </summary>
        public RenderAlignment ResolveAlignment(string? styleName)
        {
            foreach (var style in WalkChain(styleName))
                if (style.ParagraphProperties?.Alignment.HasValue == true)
                    return (RenderAlignment)(int)style.ParagraphProperties.Alignment.Value;

            return RenderAlignment.Left;
        }

        // ── Вспомогательные ──────────────────────────────────────────────

        /// <summary>
        /// Обходит цепочку BasedOn начиная со стиля styleName.
        /// Защищён от циклических ссылок через HashSet visited.
        /// Возвращает стили в порядке от текущего к базовому.
        /// </summary>
        private IEnumerable<DocumentStyle> WalkChain(string? styleName)
        {
            var visited = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            string? current = styleName ?? DefaultStyleName;

            while (current is not null && visited.Add(current))
            {
                if (!_index.TryGetValue(current, out var style)) yield break;
                yield return style;
                current = style.BasedOn;
            }
        }
    }
}