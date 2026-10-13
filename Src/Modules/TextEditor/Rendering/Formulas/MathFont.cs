using System;
using System.Collections.Generic;

namespace Writersword.Modules.TextEditor.Rendering.Formulas
{
    /// <summary>Начертание гарнитуры, которым набирается знак формулы.</summary>
    internal enum MathFace
    {
        Regular = 0,
        Italic = 1,
        Bold = 2,
        BoldItalic = 3
    }

    /// <summary>
    /// Габарит рисунка знака относительно его точки отсчёта на базовой линии, в
    /// пунктах: вверх от линии, вниз от неё, влево и вправо от точки отсчёта.
    /// </summary>
    internal readonly record struct MathGlyphExtents(float Top, float Bottom, float Left, float Right)
    {
        public float Height => Top + Bottom;
    }

    /// <summary>
    /// Шрифт формулы: знаки, их ширины и габариты, таблица математической вёрстки.
    /// Вёрстка формулы знает шрифт только через этот интерфейс и сама к рисованию
    /// не привязана.
    /// </summary>
    internal interface IMathFont
    {
        /// <summary>Знак шрифта для символа Юникода. Ноль — такого знака нет.</summary>
        ushort GlyphOf(int codepoint, MathFace face);

        /// <summary>Ширина знака вместе с его полями, в пунктах.</summary>
        float Advance(ushort glyph, MathFace face, float sizePt);

        /// <summary>Габарит рисунка знака, в пунктах.</summary>
        MathGlyphExtents Extents(ushort glyph, MathFace face, float sizePt);

        /// <summary>Таблица MATH шрифта или null, когда её нет.</summary>
        OpenTypeMathTable? MathTable { get; }
    }

    /// <summary>
    /// Величины математической вёрстки в долях кегля. Берутся из таблицы MATH шрифта
    /// формулы; у шрифта без неё — запасные, близкие к Cambria Math.
    /// </summary>
    internal sealed class MathConstants
    {
        public float ScriptScale = 0.73f;
        public float ScriptScriptScale = 0.60f;
        public float DelimitedSubFormulaMinHeight = 1.5f;
        public float DisplayOperatorMinHeight = 1.8f;
        public float MathLeading = 0.15f;
        public float AxisHeight = 0.286f;
        public float AccentBaseHeight = 0.47f;
        public float FlattenedAccentBaseHeight = 0.67f;
        public float SubscriptShiftDown = 0.14f;
        public float SubscriptTopMax = 0.38f;
        public float SubscriptBaselineDropMin = 0.11f;
        public float SuperscriptShiftUp = 0.36f;
        public float SuperscriptShiftUpCramped = 0.25f;
        public float SuperscriptBottomMin = 0.12f;
        public float SuperscriptBaselineDropMax = 0.23f;
        public float SubSuperscriptGapMin = 0.15f;
        public float SuperscriptBottomMaxWithSubscript = 0.38f;
        public float SpaceAfterScript = 0.04f;
        public float UpperLimitGapMin = 0.1f;
        public float UpperLimitBaselineRiseMin = 0.14f;
        public float LowerLimitGapMin = 0.1f;
        public float LowerLimitBaselineDropMin = 0.6f;
        public float StackTopShiftUp = 0.48f;
        public float StackTopDisplayStyleShiftUp = 0.68f;
        public float StackBottomShiftDown = 0.34f;
        public float StackBottomDisplayStyleShiftDown = 0.69f;
        public float StackGapMin = 0.15f;
        public float StackDisplayStyleGapMin = 0.35f;
        public float StretchStackTopShiftUp = 0.76f;
        public float StretchStackBottomShiftDown = 0.6f;
        public float StretchStackGapAboveMin = 0.1f;
        public float StretchStackGapBelowMin = 0.1f;
        public float FractionNumeratorShiftUp = 0.55f;
        public float FractionNumeratorDisplayStyleShiftUp = 0.68f;
        public float FractionDenominatorShiftDown = 0.5f;
        public float FractionDenominatorDisplayStyleShiftDown = 0.69f;
        public float FractionNumeratorGapMin = 0.05f;
        public float FractionNumDisplayStyleGapMin = 0.15f;
        public float FractionRuleThickness = 0.05f;
        public float FractionDenominatorGapMin = 0.05f;
        public float FractionDenomDisplayStyleGapMin = 0.15f;
        public float SkewedFractionHorizontalGap = 0.3f;
        public float SkewedFractionVerticalGap = 0.1f;
        public float OverbarVerticalGap = 0.15f;
        public float OverbarRuleThickness = 0.05f;
        public float OverbarExtraAscender = 0.05f;
        public float UnderbarVerticalGap = 0.15f;
        public float UnderbarRuleThickness = 0.05f;
        public float UnderbarExtraDescender = 0.05f;
        public float RadicalVerticalGap = 0.06f;
        public float RadicalDisplayStyleVerticalGap = 0.17f;
        public float RadicalRuleThickness = 0.05f;
        public float RadicalExtraAscender = 0.05f;
        public float RadicalKernBeforeDegree = 0.28f;
        public float RadicalKernAfterDegree = -0.56f;
        public float RadicalDegreeBottomRaisePercent = 0.6f;
    }

    /// <summary>Готовый увеличенный вариант знака из таблицы MATH: знак и его высота.</summary>
    internal readonly record struct MathGlyphVariant(ushort Glyph, float AdvanceEm);

    /// <summary>
    /// Таблица MATH шрифта OpenType: величины вёрстки, наклонные поправки знаков и
    /// готовые увеличенные варианты растущих знаков (скобок, корня, знаков суммы).
    /// Разбирается из сырых байтов таблицы, как они лежат в файле шрифта.
    /// </summary>
    internal sealed class OpenTypeMathTable
    {
        public MathConstants Constants { get; } = new();

        private readonly Dictionary<ushort, float> _italicCorrectionEm = new();
        private readonly Dictionary<ushort, MathGlyphVariant[]> _verticalVariants = new();
        private readonly Dictionary<ushort, MathGlyphVariant[]> _horizontalVariants = new();

        /// <summary>Наклонная поправка знака в долях кегля; ноль, если её нет.</summary>
        public float ItalicCorrectionEm(ushort glyph)
            => _italicCorrectionEm.TryGetValue(glyph, out float value) ? value : 0f;

        /// <summary>Увеличенные по высоте варианты знака, от меньшего к большему.</summary>
        public IReadOnlyList<MathGlyphVariant> VerticalVariants(ushort glyph)
            => _verticalVariants.TryGetValue(glyph, out var variants) ? variants : Array.Empty<MathGlyphVariant>();

        /// <summary>Увеличенные по ширине варианты знака, от меньшего к большему.</summary>
        public IReadOnlyList<MathGlyphVariant> HorizontalVariants(ushort glyph)
            => _horizontalVariants.TryGetValue(glyph, out var variants) ? variants : Array.Empty<MathGlyphVariant>();

        /// <summary>
        /// Разбирает таблицу. Возвращает null, если данные не похожи на таблицу MATH:
        /// тогда вёрстка идёт по запасным величинам.
        /// </summary>
        public static OpenTypeMathTable? Parse(byte[]? data, int unitsPerEm)
        {
            if (data is null || data.Length < 10 || unitsPerEm <= 0) return null;

            try
            {
                var reader = new Reader(data);
                if (reader.U16(0) != 1) return null;

                var table = new OpenTypeMathTable();
                float em = unitsPerEm;

                int constantsOffset = reader.U16(4);
                int glyphInfoOffset = reader.U16(6);
                int variantsOffset = reader.U16(8);

                if (constantsOffset != 0) table.ReadConstants(reader, constantsOffset, em);
                if (glyphInfoOffset != 0) table.ReadItalicCorrections(reader, glyphInfoOffset, em);
                if (variantsOffset != 0) table.ReadVariants(reader, variantsOffset, em);

                return table;
            }
            catch (IndexOutOfRangeException)
            {
                return null;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        private void ReadConstants(Reader reader, int offset, float em)
        {
            var c = Constants;

            c.ScriptScale = reader.S16(offset) / 100f;
            c.ScriptScriptScale = reader.S16(offset + 2) / 100f;
            c.DelimitedSubFormulaMinHeight = reader.U16(offset + 4) / em;
            c.DisplayOperatorMinHeight = reader.U16(offset + 6) / em;

            // Дальше идут записи из значения и ссылки на таблицу устройства: по четыре
            // байта, нужно только значение.
            int at = offset + 8;
            float Next()
            {
                float value = reader.S16(at) / em;
                at += 4;
                return value;
            }

            c.MathLeading = Next();
            c.AxisHeight = Next();
            c.AccentBaseHeight = Next();
            c.FlattenedAccentBaseHeight = Next();
            c.SubscriptShiftDown = Next();
            c.SubscriptTopMax = Next();
            c.SubscriptBaselineDropMin = Next();
            c.SuperscriptShiftUp = Next();
            c.SuperscriptShiftUpCramped = Next();
            c.SuperscriptBottomMin = Next();
            c.SuperscriptBaselineDropMax = Next();
            c.SubSuperscriptGapMin = Next();
            c.SuperscriptBottomMaxWithSubscript = Next();
            c.SpaceAfterScript = Next();
            c.UpperLimitGapMin = Next();
            c.UpperLimitBaselineRiseMin = Next();
            c.LowerLimitGapMin = Next();
            c.LowerLimitBaselineDropMin = Next();
            c.StackTopShiftUp = Next();
            c.StackTopDisplayStyleShiftUp = Next();
            c.StackBottomShiftDown = Next();
            c.StackBottomDisplayStyleShiftDown = Next();
            c.StackGapMin = Next();
            c.StackDisplayStyleGapMin = Next();
            c.StretchStackTopShiftUp = Next();
            c.StretchStackBottomShiftDown = Next();
            c.StretchStackGapAboveMin = Next();
            c.StretchStackGapBelowMin = Next();
            c.FractionNumeratorShiftUp = Next();
            c.FractionNumeratorDisplayStyleShiftUp = Next();
            c.FractionDenominatorShiftDown = Next();
            c.FractionDenominatorDisplayStyleShiftDown = Next();
            c.FractionNumeratorGapMin = Next();
            c.FractionNumDisplayStyleGapMin = Next();
            c.FractionRuleThickness = Next();
            c.FractionDenominatorGapMin = Next();
            c.FractionDenomDisplayStyleGapMin = Next();
            c.SkewedFractionHorizontalGap = Next();
            c.SkewedFractionVerticalGap = Next();
            c.OverbarVerticalGap = Next();
            c.OverbarRuleThickness = Next();
            c.OverbarExtraAscender = Next();
            c.UnderbarVerticalGap = Next();
            c.UnderbarRuleThickness = Next();
            c.UnderbarExtraDescender = Next();
            c.RadicalVerticalGap = Next();
            c.RadicalDisplayStyleVerticalGap = Next();
            c.RadicalRuleThickness = Next();
            c.RadicalExtraAscender = Next();
            c.RadicalKernBeforeDegree = Next();
            c.RadicalKernAfterDegree = Next();
            c.RadicalDegreeBottomRaisePercent = reader.S16(at) / 100f;

            // Шрифт с нулевыми масштабами индексов рисовал бы их нулевым кеглем.
            if (c.ScriptScale <= 0.05f) c.ScriptScale = 0.73f;
            if (c.ScriptScriptScale <= 0.05f) c.ScriptScriptScale = 0.60f;
        }

        private void ReadItalicCorrections(Reader reader, int glyphInfoOffset, float em)
        {
            int italicsOffset = reader.U16(glyphInfoOffset);
            if (italicsOffset == 0) return;

            int tableOffset = glyphInfoOffset + italicsOffset;
            int coverageOffset = reader.U16(tableOffset);
            int count = reader.U16(tableOffset + 2);
            if (coverageOffset == 0 || count == 0) return;

            var glyphs = ReadCoverage(reader, tableOffset + coverageOffset);
            for (int i = 0; i < count && i < glyphs.Count; i++)
            {
                short value = reader.S16(tableOffset + 4 + i * 4);
                if (value != 0) _italicCorrectionEm[glyphs[i]] = value / em;
            }
        }

        private void ReadVariants(Reader reader, int variantsOffset, float em)
        {
            int verticalCoverageOffset = reader.U16(variantsOffset + 2);
            int horizontalCoverageOffset = reader.U16(variantsOffset + 4);
            int verticalCount = reader.U16(variantsOffset + 6);
            int horizontalCount = reader.U16(variantsOffset + 8);

            int offsetsAt = variantsOffset + 10;

            if (verticalCoverageOffset != 0 && verticalCount > 0)
            {
                var glyphs = ReadCoverage(reader, variantsOffset + verticalCoverageOffset);
                for (int i = 0; i < verticalCount && i < glyphs.Count; i++)
                {
                    int constructionOffset = reader.U16(offsetsAt + i * 2);
                    if (constructionOffset == 0) continue;
                    var variants = ReadConstruction(reader, variantsOffset + constructionOffset, em);
                    if (variants.Length > 0) _verticalVariants[glyphs[i]] = variants;
                }
            }

            offsetsAt += verticalCount * 2;

            if (horizontalCoverageOffset != 0 && horizontalCount > 0)
            {
                var glyphs = ReadCoverage(reader, variantsOffset + horizontalCoverageOffset);
                for (int i = 0; i < horizontalCount && i < glyphs.Count; i++)
                {
                    int constructionOffset = reader.U16(offsetsAt + i * 2);
                    if (constructionOffset == 0) continue;
                    var variants = ReadConstruction(reader, variantsOffset + constructionOffset, em);
                    if (variants.Length > 0) _horizontalVariants[glyphs[i]] = variants;
                }
            }
        }

        private static MathGlyphVariant[] ReadConstruction(Reader reader, int offset, float em)
        {
            int count = reader.U16(offset + 2);
            if (count == 0) return Array.Empty<MathGlyphVariant>();

            var variants = new MathGlyphVariant[count];
            for (int i = 0; i < count; i++)
            {
                ushort glyph = reader.U16(offset + 4 + i * 4);
                ushort advance = reader.U16(offset + 6 + i * 4);
                variants[i] = new MathGlyphVariant(glyph, advance / em);
            }

            return variants;
        }

        /// <summary>Знаки таблицы покрытия по порядку их номеров в ней.</summary>
        private static List<ushort> ReadCoverage(Reader reader, int offset)
        {
            var glyphs = new List<ushort>();
            int format = reader.U16(offset);
            int count = reader.U16(offset + 2);

            if (format == 1)
            {
                for (int i = 0; i < count; i++)
                    glyphs.Add(reader.U16(offset + 4 + i * 2));
            }
            else if (format == 2)
            {
                for (int i = 0; i < count; i++)
                {
                    int start = reader.U16(offset + 4 + i * 6);
                    int end = reader.U16(offset + 6 + i * 6);
                    for (int glyph = start; glyph <= end && glyphs.Count < 0x10000; glyph++)
                        glyphs.Add((ushort)glyph);
                }
            }

            return glyphs;
        }

        /// <summary>Чтение чисел таблицы шрифта: старший байт первым.</summary>
        private readonly struct Reader
        {
            private readonly byte[] _data;

            public Reader(byte[] data) => _data = data;

            public ushort U16(int offset) => (ushort)((_data[offset] << 8) | _data[offset + 1]);

            public short S16(int offset) => (short)((_data[offset] << 8) | _data[offset + 1]);
        }
    }
}
