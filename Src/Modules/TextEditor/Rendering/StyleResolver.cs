using System.Collections.Generic;
using SkiaSharp;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.Models.Styles;

using RenderAlignment = Writersword.Core.Models.Rendering.TextAlignment;

namespace Writersword.Modules.TextEditor.Rendering
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

        /// <summary>
        /// Карта "Unicode-скрипт → шрифт" из пользовательских настроек.
        /// Используется SKTextRenderer как приоритетный фолбэк перед системным MatchCharacter.
        /// Ключи: "Cyrillic", "Greek", "Arabic", "Hebrew", "CJK", "Korean", "Japanese" и др.
        /// </summary>
        public IReadOnlyDictionary<string, string> ScriptFontMap { get; }

        /// <summary>
        /// Подставлять ли шрифт вместо знаков, которых нет в выбранной гарнитуре.
        /// Выключено — знак рисуется пустым прямоугольником, как его и отдаёт сама
        /// гарнитура. Ставится из настроек редактора.
        /// </summary>
        public bool SubstituteMissingGlyphs { get; }

        /// <summary>
        /// Шрифт подстановки для знаков, письмо которых в ScriptFontMap не описано.
        /// Работает только при включённом SubstituteMissingGlyphs.
        /// </summary>
        public string? SubstituteFontFamily { get; }

        /// <summary>
        /// Разрешён ли перенос строки после дефиса внутри слова. Ставится из
        /// настроек редактора.
        /// </summary>
        public bool BreakOnHyphen { get; }

        /// <summary>
        /// Шаг табуляции по умолчанию в пунктах. К ближайшей его отметке уходит символ
        /// табуляции в абзаце, которому своих позиций не задано. Свойство документа:
        /// в рукописи шаг один на всю книгу, иначе одинаковые на вид абзацы
        /// раскладывались бы по-разному.
        /// </summary>
        public float DefaultTabStopPt { get; }

        /// <summary>
        /// Строке, выровненной по ширине, разрешено сжать пробелы до пятой части, чтобы
        /// вместить ещё одно слово, — как у Word 2013 и новее. Свойство документа.
        /// </summary>
        public bool JustifyWithShrinking { get; }

        /// <summary>
        /// Показывать скрытый текст (w:vanish): на своём месте и с точечным
        /// подчёркиванием, как Word при включённых непечатаемых знаках. Выключено —
        /// скрытый текст не рисуется и не занимает места.
        /// </summary>
        public bool ShowHiddenText { get; }

        /// <summary>
        /// Как показываются исправления рецензирования: все с пометками, простая
        /// разметка, без пометок или исходный документ. Свойство вида документа.
        /// </summary>
        public RevisionView RevisionView { get; }

        // Цвета авторов правок: каждый новый автор получает следующий цвет набора, как
        // в Word при «цвет по автору». Порядок — по первому появлению в документе.
        private readonly Dictionary<string, SKColor> _authorColors = new(System.StringComparer.Ordinal);
        private readonly object _authorColorsLock = new();

        private static readonly SKColor[] AuthorPalette =
        {
            new(0xC0, 0x00, 0x00),
            new(0x2E, 0x74, 0xB5),
            new(0x53, 0x81, 0x35),
            new(0x70, 0x30, 0xA0),
            new(0xC5, 0x5A, 0x11),
            new(0x1F, 0x7A, 0x7A),
            new(0x7F, 0x60, 0x00),
            new(0xC0, 0x35, 0x9A)
        };

        public StyleResolver(
            IEnumerable<DocumentStyle> styles,
            IReadOnlyDictionary<string, string>? scriptFontMap = null,
            bool substituteMissingGlyphs = false,
            string? substituteFontFamily = null,
            bool breakOnHyphen = true,
            float defaultTabStopPt = 35.4f,
            bool justifyWithShrinking = false,
            bool showHiddenText = false,
            RevisionView revisionView = RevisionView.AllMarkup,
            IEnumerable<string>? revisionAuthors = null)
        {
            _index = new Dictionary<string, DocumentStyle>(
                System.StringComparer.OrdinalIgnoreCase);

            foreach (var style in styles)
                if (!string.IsNullOrEmpty(style.Name))
                    _index[style.Name] = style;

            ScriptFontMap = scriptFontMap
                ?? new Dictionary<string, string>();

            SubstituteMissingGlyphs = substituteMissingGlyphs;
            SubstituteFontFamily = substituteFontFamily;
            BreakOnHyphen = breakOnHyphen;
            DefaultTabStopPt = defaultTabStopPt > 1f ? defaultTabStopPt : 35.4f;
            JustifyWithShrinking = justifyWithShrinking;
            ShowHiddenText = showHiddenText;
            RevisionView = revisionView;

            if (revisionAuthors is not null)
                foreach (var author in revisionAuthors)
                    AuthorColor(author);
        }

        /// <summary>
        /// Цвет пометок правок автора. Автор, которого ещё не было, получает следующий
        /// цвет набора; набор кончился — цвета идут по кругу.
        /// </summary>
        public SKColor AuthorColor(string? author)
        {
            string key = author ?? string.Empty;

            lock (_authorColorsLock)
            {
                if (_authorColors.TryGetValue(key, out var color)) return color;

                color = AuthorPalette[_authorColors.Count % AuthorPalette.Length];
                _authorColors[key] = color;
                return color;
            }
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
                    return style.RunProperties.IsBold ?? false;

            return false;
        }

        /// <summary>
        /// Резолвирует IsItalic из цепочки стилей BasedOn.
        /// </summary>
        public bool ResolveItalic(string? styleName)
        {
            foreach (var style in WalkChain(styleName))
                if (style.RunProperties is not null)
                    return style.RunProperties.IsItalic ?? false;

            return false;
        }

        /// <summary>
        /// «Все прописные» стиля абзаца по цепочке BasedOn — по тому же правилу, что
        /// жирность и курсив: решает ближайший стиль, у которого есть свойства текста.
        /// </summary>
        public bool ResolveAllCaps(string? styleName)
        {
            foreach (var style in WalkChain(styleName))
                if (style.RunProperties is not null)
                    return style.RunProperties.IsAllCaps;

            return false;
        }

        /// <summary>«Малые прописные» стиля абзаца по цепочке BasedOn — см. ResolveAllCaps.</summary>
        public bool ResolveSmallCaps(string? styleName)
        {
            foreach (var style in WalkChain(styleName))
                if (style.RunProperties is not null)
                    return style.RunProperties.IsSmallCaps;

            return false;
        }

        // ── Символьный стиль ──────────────────────────────────────────────
        //
        // Отличаются эти четыре от резолверов выше тем, чего они НЕ делают.
        //
        // Те отвечают за вид абзаца целиком и обязаны дать значение всегда, поэтому в
        // конце подставляют запасное: не нашлось гарнитуры в цепочке — Times New Roman.
        // Символьному стилю так вести себя нельзя. Он ложится поверх абзаца и задаёт
        // ровно то, что в нём написано: стиль, который делает текст жирным и больше
        // ничего, обязан оставить гарнитуру абзаца в покое, а не подменить её запасной.
        //
        // Пустое имя здесь не подменяется на «Обычный», в отличие от WalkChain. Отсутствие
        // символьного стиля — это отсутствие слоя, а не слой со стилем по умолчанию.

        /// <summary>Гарнитура, заданная символьным стилем. Null — цепочка её не задаёт.</summary>
        public string? FindFontFamily(string? styleName)
        {
            if (string.IsNullOrEmpty(styleName)) return null;

            foreach (var style in WalkChain(styleName))
                if (!string.IsNullOrEmpty(style.RunProperties?.FontFamily))
                    return style.RunProperties!.FontFamily;

            return null;
        }

        /// <summary>Кегль, заданный символьным стилем. Null — цепочка его не задаёт.</summary>
        public float? FindFontSize(string? styleName)
        {
            if (string.IsNullOrEmpty(styleName)) return null;

            foreach (var style in WalkChain(styleName))
                if (style.RunProperties?.FontSize.HasValue == true)
                    return (float)style.RunProperties.FontSize.Value;

            return null;
        }

        /// <summary>Цвет текста, заданный символьным стилем. Null — цепочка его не задаёт.</summary>
        public string? FindTextColor(string? styleName)
        {
            if (string.IsNullOrEmpty(styleName)) return null;

            foreach (var style in WalkChain(styleName))
                if (!string.IsNullOrEmpty(style.RunProperties?.TextColor))
                    return style.RunProperties!.TextColor;

            return null;
        }

        /// <summary>
        /// Межбуквенный интервал, заданный символьным стилем. Null — цепочка его не задаёт.
        /// </summary>
        public float? FindCharacterSpacing(string? styleName)
        {
            if (string.IsNullOrEmpty(styleName)) return null;

            foreach (var style in WalkChain(styleName))
                if (style.RunProperties?.CharacterSpacing.HasValue == true)
                    return (float)style.RunProperties.CharacterSpacing.Value;

            return null;
        }

        /// <summary>
        /// Межбуквенный интервал стиля абзаца по цепочке BasedOn. Не задан нигде —
        /// обычный интервал, 0.
        /// </summary>
        public float ResolveCharacterSpacing(string? styleName)
        {
            foreach (var style in WalkChain(styleName))
                if (style.RunProperties?.CharacterSpacing.HasValue == true)
                    return (float)style.RunProperties.CharacterSpacing.Value;

            return 0f;
        }

        /// <summary>
        /// Делает ли символьный стиль текст жирным.
        ///
        /// Только добавляет: снять жирность стилем нельзя. Стили, сохранённые до того, как
        /// IsBold стал трёхзначным, записывали «не жирный» пропуском поля, и «не задано» от
        /// «задано ложью» в них не отличить. Из двух толкований выбрано то, ради которого
        /// символьные стили и заводят: ими выделяют, а не гасят. Снять жирность с
        /// фрагмента можно прямым форматированием — оно сильнее стиля.
        /// </summary>
        public bool AnyBold(string? styleName)
        {
            if (string.IsNullOrEmpty(styleName)) return false;

            foreach (var style in WalkChain(styleName))
                if (style.RunProperties?.IsBold == true) return true;

            return false;
        }

        /// <summary>Делает ли символьный стиль текст прописным. Только добавляет — см. AnyBold.</summary>
        public bool AnyAllCaps(string? styleName)
        {
            if (string.IsNullOrEmpty(styleName)) return false;

            foreach (var style in WalkChain(styleName))
                if (style.RunProperties?.IsAllCaps == true) return true;

            return false;
        }

        /// <summary>Делает ли символьный стиль текст малыми прописными. Только добавляет — см. AnyBold.</summary>
        public bool AnySmallCaps(string? styleName)
        {
            if (string.IsNullOrEmpty(styleName)) return false;

            foreach (var style in WalkChain(styleName))
                if (style.RunProperties?.IsSmallCaps == true) return true;

            return false;
        }

        /// <summary>Делает ли символьный стиль текст курсивным. Только добавляет — см. AnyBold.</summary>
        public bool AnyItalic(string? styleName)
        {
            if (string.IsNullOrEmpty(styleName)) return false;

            foreach (var style in WalkChain(styleName))
                if (style.RunProperties?.IsItalic == true) return true;

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
        /// Резолвирует правило межстрочного интервала из цепочки стилей BasedOn.
        /// Без правила значение интервала пришлось бы считать множителем всегда,
        /// а «точно» и «минимум» задают высоту строки в пунктах.
        /// </summary>
        public LineSpacingRule ResolveLineSpacingRule(string? styleName)
        {
            foreach (var style in WalkChain(styleName))
                if (style.ParagraphProperties?.LineSpacingRule.HasValue == true)
                    return style.ParagraphProperties.LineSpacingRule.Value;

            return LineSpacingRule.Auto;
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
        /// Резолвирует «не добавлять интервал между абзацами одного стиля» из цепочки
        /// стилей BasedOn. Цепочка молчит — интервалы добавляются.
        /// </summary>
        public bool ResolveContextualSpacing(string? styleName)
        {
            foreach (var style in WalkChain(styleName))
                if (style.ParagraphProperties?.ContextualSpacing is bool contextual)
                    return contextual;

            return false;
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