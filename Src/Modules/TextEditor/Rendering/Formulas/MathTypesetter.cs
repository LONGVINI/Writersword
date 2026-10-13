using System;
using System.Collections.Generic;

namespace Writersword.Modules.TextEditor.Rendering.Formulas
{
    /// <summary>Что рисуется в формуле: знак, линия или вложенный блок.</summary>
    internal abstract class MathItem
    {
        /// <summary>Сдвиг вправо от начала блока, pt.</summary>
        public float X { get; init; }

        /// <summary>Сдвиг вниз от базовой линии блока, pt (вверх — отрицательный).</summary>
        public float Y { get; init; }
    }

    /// <summary>Знак шрифта. X, Y — его точка отсчёта на базовой линии.</summary>
    internal sealed class MathGlyphItem : MathItem
    {
        public ushort Glyph { get; init; }
        public MathFace Face { get; init; }
        public float SizePt { get; init; }

        /// <summary>Растяжение знака от его точки отсчёта: растущие скобки, корень.</summary>
        public float ScaleX { get; init; } = 1f;
        public float ScaleY { get; init; } = 1f;

        /// <summary>Свой цвет знака (шестнадцатеричный без решётки) или null — цвет формулы.</summary>
        public string? Color { get; init; }
    }

    /// <summary>Залитый прямоугольник: черта дроби, черта корня. X, Y — левый верхний угол.</summary>
    internal sealed class MathRuleItem : MathItem
    {
        public float Width { get; init; }
        public float Height { get; init; }
    }

    /// <summary>Наклонная линия от (X, Y) до (X2, Y2): косая дробь, зачёркивание.</summary>
    internal sealed class MathLineItem : MathItem
    {
        public float X2 { get; init; }
        public float Y2 { get; init; }
        public float Thickness { get; init; }
    }

    /// <summary>
    /// Пустое место конструкции в окне формулы — пунктирный квадратик, как у Word:
    /// показывает, куда вписать числитель, индекс или подкоренное выражение. На листе
    /// не рисуется. X, Y — левый верхний угол.
    /// </summary>
    internal sealed class MathPlaceholderItem : MathItem
    {
        public float Width { get; init; }
        public float Height { get; init; }
    }

    /// <summary>Вложенный блок, сдвинутый на (X, Y).</summary>
    internal sealed class MathBoxItem : MathItem
    {
        public MathBox Box { get; init; } = MathBox.Empty;
    }

    /// <summary>
    /// Свёрстанная часть формулы: ширина, подъём над базовой линией, спуск под неё и
    /// то, что в ней рисуется.
    /// </summary>
    internal sealed class MathBox
    {
        public static readonly MathBox Empty = new();

        public float Width { get; set; }
        public float Ascent { get; set; }
        public float Descent { get; set; }

        /// <summary>
        /// Наклонная поправка последнего знака: на неё верхний индекс отодвигается от
        /// наклонной буквы, чтобы не лечь на её верх.
        /// </summary>
        public float ItalicCorrection { get; set; }

        /// <summary>Блок — один знак: индексы у него встают по величинам шрифта.</summary>
        public bool IsCharacter { get; set; }

        public MathAtomClass LeftClass { get; set; } = MathAtomClass.Ordinary;
        public MathAtomClass RightClass { get; set; } = MathAtomClass.Ordinary;

        public List<MathItem> Items { get; } = new();

        public float Height => Ascent + Descent;
    }

    /// <summary>
    /// Вёрстка формулы: из дерева узлов строит блок с размерами и списком того, что
    /// рисовать.
    ///
    /// Правила — те же, что у TeX и у Word: четыре размера (формула отдельной
    /// строкой, формула в тексте, индекс, индекс индекса), просветы между знаками по
    /// их ролям, место числителя, знаменателя, индексов, пределов и корня — по
    /// величинам таблицы MATH шрифта формулы.
    /// </summary>
    internal sealed class MathTypesetter
    {
        private enum MathStyle
        {
            Display,
            Text,
            Script,
            ScriptScript
        }

        private readonly record struct Context(MathStyle Style, bool Cramped);

        private sealed class Atom
        {
            public MathBox Box = MathBox.Empty;

            /// <summary>Роль для просвета слева; она же справа, пока не задана своя.</summary>
            public MathAtomClass Class;

            /// <summary>
            /// Роль для просвета справа, когда она другая: выражение в скобках слева
            /// ведёт себя как открывающая скобка, справа — как закрывающая.
            /// </summary>
            public MathAtomClass? RightClass;

            public bool IsSpace;

            public MathAtomClass Right => RightClass ?? Class;
        }

        // Поля дроби по бокам: черта дроби не касается соседних знаков.
        private const float FractionSidePadEm = 0.12f;

        // Просветы таблицы и столбца уравнений.
        private const float MatrixColumnGapEm = 0.9f;
        private const float MatrixRowGapEm = 0.35f;
        private const float MatrixRowAscentEm = 0.7f;
        private const float MatrixRowDescentEm = 0.2f;

        // Поля рамки вокруг выражения.
        private const float BorderBoxPadEm = 0.12f;

        // Просветы между знаками по ролям: слева — по строкам, справа — по столбцам.
        // 0 — нет; 1 — тонкий; 2 — тонкий, кроме индексов; 3 — средний, кроме индексов;
        // 4 — толстый, кроме индексов.
        private static readonly byte[,] SpacingTable =
        {
            // Ord Op Bin Rel Open Close Punct Inner
            { 0, 1, 3, 4, 0, 0, 0, 2 },
            { 1, 1, 0, 4, 0, 0, 0, 2 },
            { 3, 3, 0, 0, 3, 0, 0, 3 },
            { 4, 4, 0, 0, 4, 0, 0, 4 },
            { 0, 0, 0, 0, 0, 0, 0, 0 },
            { 0, 1, 3, 4, 0, 0, 0, 2 },
            { 2, 2, 0, 2, 2, 2, 2, 2 },
            { 2, 1, 3, 4, 2, 0, 2, 2 }
        };

        // Квадратик пустого места: доли кегля.
        private const float PlaceholderWidthEm = 0.52f;
        private const float PlaceholderAscentEm = 0.66f;
        private const float PlaceholderDescentEm = 0.04f;

        private readonly IMathFont _font;
        private readonly MathConstants _c;
        private readonly float _baseSizePt;

        // Пустые места конструкций показываются квадратиками (окно формулы).
        private readonly bool _placeholders;

        // Глубина вложения рядов: пустая формула целиком квадратиком не становится.
        private int _depth;

        public MathTypesetter(IMathFont font, float baseSizePt, bool placeholders = false)
        {
            _font = font;
            _c = font.MathTable?.Constants ?? new MathConstants();
            _baseSizePt = baseSizePt > 0.5f ? baseSizePt : 11f;
            _placeholders = placeholders;
        }

        /// <summary>
        /// Часть конструкции верстается: в ней что-то есть, а в окне формулы — и пустая,
        /// чтобы на её месте встал квадратик.
        /// </summary>
        private bool Present(MathRow? row) => row is not null && (!row.IsEmpty || _placeholders);

        private MathBox PlaceholderBox(Context ctx)
        {
            float size = SizeOf(ctx.Style);
            float width = PlaceholderWidthEm * size;
            float ascent = PlaceholderAscentEm * size;
            float descent = PlaceholderDescentEm * size;
            float inset = size * 0.04f;

            var box = new MathBox
            {
                Width = width + inset * 2f,
                Ascent = ascent,
                Descent = descent,
                IsCharacter = true
            };
            box.Items.Add(new MathPlaceholderItem
            {
                X = inset,
                Y = -ascent,
                Width = width,
                Height = ascent + descent
            });
            return box;
        }

        /// <summary>
        /// Верстает формулу. display — формула отдельной строкой: знаки сумм крупнее,
        /// пределы над и под ними, дроби в полный размер.
        /// </summary>
        public MathBox Typeset(MathRow formula, bool display)
        {
            var box = LayoutRow(formula, new Context(display ? MathStyle.Display : MathStyle.Text, false));

            if (box.Ascent < 0f) box.Ascent = 0f;
            if (box.Descent < 0f) box.Descent = 0f;
            if (box.Width < 0f) box.Width = 0f;
            return box;
        }

        private float SizeOf(MathStyle style) => style switch
        {
            MathStyle.Script => _baseSizePt * _c.ScriptScale,
            MathStyle.ScriptScript => _baseSizePt * _c.ScriptScriptScale,
            _ => _baseSizePt
        };

        private static MathStyle ScriptStyleOf(MathStyle style)
            => style is MathStyle.Display or MathStyle.Text ? MathStyle.Script : MathStyle.ScriptScript;

        private static MathStyle FractionStyleOf(MathStyle style) => style switch
        {
            MathStyle.Display => MathStyle.Text,
            MathStyle.Text => MathStyle.Script,
            _ => MathStyle.ScriptScript
        };

        // ── Ряд ──────────────────────────────────────────────────────────

        private MathBox LayoutRow(MathRow? row, Context ctx)
        {
            if (_placeholders && _depth > 0 && (row is null || row.IsEmpty))
                return PlaceholderBox(ctx);

            _depth++;
            try
            {
                var atoms = new List<Atom>();
                if (row is not null) CollectAtoms(row, ctx, atoms);
                return AssembleRow(atoms, ctx);
            }
            finally
            {
                _depth--;
            }
        }

        private void CollectAtoms(MathRow row, Context ctx, List<Atom> atoms)
        {
            foreach (var node in row.Items)
            {
                switch (node)
                {
                    case MathRunNode run:
                        AppendRunAtoms(run, ctx, atoms);
                        break;

                    case MathRow nested:
                        CollectAtoms(nested, ctx, atoms);
                        break;

                    case MathNaryNode nary:
                        atoms.Add(new Atom { Box = LayoutNaryOperator(nary, ctx), Class = MathAtomClass.Operator });
                        if (_placeholders && nary.Body.IsEmpty)
                            atoms.Add(new Atom { Box = PlaceholderBox(ctx), Class = MathAtomClass.Ordinary });
                        else
                            CollectAtoms(nary.Body, ctx, atoms);
                        break;

                    case MathFunctionNode function:
                        atoms.Add(new Atom { Box = LayoutRow(function.Name, ctx), Class = MathAtomClass.Operator });
                        if (_placeholders && function.Argument.IsEmpty)
                            atoms.Add(new Atom { Box = PlaceholderBox(ctx), Class = MathAtomClass.Ordinary });
                        else
                            CollectAtoms(function.Argument, ctx, atoms);
                        break;

                    case MathFractionNode fraction:
                        atoms.Add(new Atom { Box = LayoutFraction(fraction, ctx), Class = MathAtomClass.Inner });
                        break;

                    case MathDelimiterNode delimiter:
                        atoms.Add(new Atom
                        {
                            Box = LayoutDelimiter(delimiter, ctx),
                            Class = MathAtomClass.Open,
                            RightClass = MathAtomClass.Close
                        });
                        break;

                    case MathScriptNode script:
                    {
                        var box = LayoutScript(script, ctx, out var scriptClass);
                        atoms.Add(new Atom { Box = box, Class = scriptClass });
                        break;
                    }

                    case MathLimitNode limit:
                        atoms.Add(new Atom { Box = LayoutLimit(limit, ctx), Class = MathAtomClass.Operator });
                        break;

                    case MathRadicalNode radical:
                        atoms.Add(new Atom { Box = LayoutRadical(radical, ctx), Class = MathAtomClass.Ordinary });
                        break;

                    case MathAccentNode accent:
                        atoms.Add(new Atom { Box = LayoutAccent(accent, ctx), Class = MathAtomClass.Ordinary });
                        break;

                    case MathBarNode bar:
                        atoms.Add(new Atom { Box = LayoutBar(bar, ctx), Class = MathAtomClass.Ordinary });
                        break;

                    case MathGroupCharNode group:
                        atoms.Add(new Atom { Box = LayoutGroupChar(group, ctx), Class = MathAtomClass.Ordinary });
                        break;

                    case MathBoxNode boxNode:
                        atoms.Add(new Atom { Box = LayoutBorderBox(boxNode, ctx), Class = MathAtomClass.Ordinary });
                        break;

                    case MathMatrixNode matrix:
                        atoms.Add(new Atom { Box = LayoutMatrix(matrix, ctx), Class = MathAtomClass.Ordinary });
                        break;

                    case MathPhantomNode phantom:
                        atoms.Add(new Atom { Box = LayoutPhantom(phantom, ctx), Class = MathAtomClass.Ordinary });
                        break;
                }
            }
        }

        private void AppendRunAtoms(MathRunNode run, Context ctx, List<Atom> atoms)
        {
            float size = SizeOf(ctx.Style);
            string text = run.Text;

            for (int i = 0; i < text.Length; i++)
            {
                int codepoint;
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    codepoint = char.ConvertToUtf32(text[i], text[i + 1]);
                    i++;
                }
                else
                {
                    codepoint = text[i];
                }

                if (MathAlphabet.IsInvisible(codepoint) || codepoint == '\r' || codepoint == '\n') continue;

                if (MathAlphabet.IsSpace(codepoint))
                {
                    atoms.Add(new Atom
                    {
                        Box = new MathBox { Width = SpaceWidth(codepoint, size) },
                        Class = MathAtomClass.None,
                        IsSpace = true
                    });
                    continue;
                }

                MathAtomClass atomClass;
                ushort glyph;
                MathFace face = MathFace.Regular;

                if (run.IsNormalText)
                {
                    atomClass = MathAtomClass.Ordinary;
                    face = run.Style switch
                    {
                        MathLetterStyle.Bold => MathFace.Bold,
                        MathLetterStyle.BoldItalic => MathFace.BoldItalic,
                        _ => MathFace.Regular
                    };
                    glyph = _font.GlyphOf(codepoint, face);
                    if (glyph == 0 && face != MathFace.Regular)
                    {
                        face = MathFace.Regular;
                        glyph = _font.GlyphOf(codepoint, face);
                    }
                }
                else
                {
                    atomClass = MathAlphabet.ClassOf(codepoint);
                    int shown = MathAlphabet.DisplayChar(codepoint);
                    int styled = MathAlphabet.Map(shown, run.Style, run.Script);

                    glyph = _font.GlyphOf(styled, MathFace.Regular);
                    if (glyph == 0 && styled != shown)
                    {
                        // Шрифт без математических букв: буква берётся обычная, своим
                        // начертанием гарнитуры.
                        face = run.Style switch
                        {
                            MathLetterStyle.Italic => MathFace.Italic,
                            MathLetterStyle.Bold => MathFace.Bold,
                            MathLetterStyle.BoldItalic => MathFace.BoldItalic,
                            _ => MathFace.Regular
                        };
                        glyph = _font.GlyphOf(shown, face);
                        if (glyph == 0)
                        {
                            face = MathFace.Regular;
                            glyph = _font.GlyphOf(shown, face);
                        }
                    }

                    if (glyph == 0 && shown != codepoint)
                        glyph = _font.GlyphOf(codepoint, MathFace.Regular);
                }

                atoms.Add(new Atom { Box = GlyphBox(glyph, face, size, run.Color), Class = atomClass });
            }
        }

        private float SpaceWidth(int codepoint, float size)
        {
            switch (codepoint)
            {
                case 0x2002: return size * 0.5f;
                case 0x2003: return size;
                case 0x2004: return size / 3f;
                case 0x2005: return size / 4f;
                case 0x2006: return size / 6f;
                case 0x2009: return size / 6f;
                case 0x200A: return size / 12f;
                case 0x200B: return 0f;
                case 0x205F: return size * 4f / 18f;
                case 0x3000: return size;
            }

            ushort glyph = _font.GlyphOf(' ', MathFace.Regular);
            return glyph != 0 ? _font.Advance(glyph, MathFace.Regular, size) : size * 0.25f;
        }

        private MathBox AssembleRow(List<Atom> atoms, Context ctx)
        {
            // Знак действия без операнда слева или справа — обычный знак: минус в
            // «−x» стоит вплотную к букве, а в «a − x» отбит с обеих сторон.
            for (int i = 0; i < atoms.Count; i++)
            {
                if (atoms[i].IsSpace || atoms[i].Class != MathAtomClass.Binary) continue;

                var previous = NeighbourClass(atoms, i, -1, right: true);
                var next = NeighbourClass(atoms, i, +1, right: false);

                bool noLeftOperand = previous is MathAtomClass.None or MathAtomClass.Binary
                    or MathAtomClass.Operator or MathAtomClass.Relation
                    or MathAtomClass.Open or MathAtomClass.Punctuation;
                bool noRightOperand = next is MathAtomClass.None or MathAtomClass.Relation
                    or MathAtomClass.Close or MathAtomClass.Punctuation;

                if (noLeftOperand || noRightOperand) atoms[i].Class = MathAtomClass.Ordinary;
            }

            var row = new MathBox { LeftClass = MathAtomClass.None, RightClass = MathAtomClass.None };
            float x = 0f;
            float ascent = 0f;
            float descent = 0f;
            bool any = false;
            int visible = 0;
            MathBox? last = null;
            var previousClass = MathAtomClass.None;

            foreach (var atom in atoms)
            {
                if (atom.IsSpace)
                {
                    x += atom.Box.Width;
                    last = null;
                    continue;
                }

                if (previousClass != MathAtomClass.None)
                    x += Spacing(previousClass, atom.Class, ctx);

                row.Items.Add(new MathBoxItem { Box = atom.Box, X = x, Y = 0f });
                x += atom.Box.Width;

                if (!any || atom.Box.Ascent > ascent) ascent = atom.Box.Ascent;
                if (!any || atom.Box.Descent > descent) descent = atom.Box.Descent;
                any = true;

                if (row.LeftClass == MathAtomClass.None) row.LeftClass = atom.Class;
                row.RightClass = atom.Right;
                previousClass = atom.Right;
                last = atom.Box;
                visible++;
            }

            row.Width = x;
            row.Ascent = ascent;
            row.Descent = descent;
            row.ItalicCorrection = last?.ItalicCorrection ?? 0f;
            row.IsCharacter = visible == 1 && atoms.Count == 1 && atoms[0].Box.IsCharacter;

            if (row.LeftClass == MathAtomClass.None) row.LeftClass = MathAtomClass.Ordinary;
            if (row.RightClass == MathAtomClass.None) row.RightClass = MathAtomClass.Ordinary;
            return row;
        }

        private static MathAtomClass NeighbourClass(List<Atom> atoms, int index, int step, bool right)
        {
            for (int i = index + step; i >= 0 && i < atoms.Count; i += step)
                if (!atoms[i].IsSpace) return right ? atoms[i].Right : atoms[i].Class;
            return MathAtomClass.None;
        }

        private float Spacing(MathAtomClass left, MathAtomClass right, Context ctx)
        {
            if ((int)left > 7 || (int)right > 7) return 0f;

            byte code = SpacingTable[(int)left, (int)right];
            if (code == 0) return 0f;

            bool script = ctx.Style is MathStyle.Script or MathStyle.ScriptScript;
            if (script && code != 1) return 0f;

            // Единица просвета — восемнадцатая доля кегля.
            float mu = SizeOf(ctx.Style) / 18f;
            return code switch
            {
                3 => 4f * mu,
                4 => 5f * mu,
                _ => 3f * mu
            };
        }

        // ── Знаки ────────────────────────────────────────────────────────

        private MathBox GlyphBox(ushort glyph, MathFace face, float size, string? color)
        {
            var extents = _font.Extents(glyph, face, size);
            var box = new MathBox
            {
                Width = _font.Advance(glyph, face, size),
                Ascent = Math.Max(extents.Top, 0f),
                Descent = Math.Max(extents.Bottom, 0f),
                IsCharacter = true
            };

            if (face == MathFace.Regular && _font.MathTable is { } table)
                box.ItalicCorrection = table.ItalicCorrectionEm(glyph) * size;

            box.Items.Add(new MathGlyphItem { Glyph = glyph, Face = face, SizePt = size, Color = color });
            return box;
        }

        private MathBox CharBox(int codepoint, float size)
        {
            ushort glyph = _font.GlyphOf(codepoint, MathFace.Regular);
            return glyph == 0 ? new MathBox() : GlyphBox(glyph, MathFace.Regular, size, null);
        }

        /// <summary>Блок, поднятый над базовой линией на up (опущенный — при отрицательном).</summary>
        private static MathBox Raise(MathBox box, float up)
        {
            if (Math.Abs(up) < 0.0005f) return box;

            var raised = new MathBox
            {
                Width = box.Width,
                Ascent = box.Ascent + up,
                Descent = box.Descent - up,
                ItalicCorrection = box.ItalicCorrection,
                LeftClass = box.LeftClass,
                RightClass = box.RightClass
            };
            raised.Items.Add(new MathBoxItem { Box = box, X = 0f, Y = -up });
            return raised;
        }

        /// <summary>
        /// Знак, выросший по высоте до заданной: сначала готовые увеличенные варианты
        /// шрифта, а если и самого большого мало — он растягивается. grew сообщает,
        /// что знак стал больше своего обычного вида. uniform — растягивать и в ширину,
        /// сохраняя рисунок знака (знаки сумм).
        /// </summary>
        private MathBox StretchVertical(int codepoint, float targetHeight, float size, bool uniform, out bool grew)
        {
            grew = false;

            ushort glyph = _font.GlyphOf(codepoint, MathFace.Regular);
            if (glyph == 0) return new MathBox();

            var extents = _font.Extents(glyph, MathFace.Regular, size);
            if (targetHeight <= 0f || extents.Height >= targetHeight - 0.01f || extents.Height <= 0.01f)
                return GlyphBox(glyph, MathFace.Regular, size, null);

            ushort best = glyph;
            var bestExtents = extents;

            if (_font.MathTable is { } table)
            {
                foreach (var variant in table.VerticalVariants(glyph))
                {
                    var variantExtents = _font.Extents(variant.Glyph, MathFace.Regular, size);
                    if (variantExtents.Height <= bestExtents.Height) continue;

                    best = variant.Glyph;
                    bestExtents = variantExtents;
                    if (bestExtents.Height >= targetHeight - 0.01f) break;
                }
            }

            grew = true;

            if (bestExtents.Height >= targetHeight - 0.01f)
                return GlyphBox(best, MathFace.Regular, size, null);

            float scale = targetHeight / bestExtents.Height;
            float scaleX = uniform ? scale : 1f;

            var box = new MathBox
            {
                Width = _font.Advance(best, MathFace.Regular, size) * scaleX,
                Ascent = Math.Max(bestExtents.Top, 0f) * scale,
                Descent = Math.Max(bestExtents.Bottom, 0f) * scale
            };

            if (_font.MathTable is { } italics)
                box.ItalicCorrection = italics.ItalicCorrectionEm(best) * size * scaleX;

            box.Items.Add(new MathGlyphItem
            {
                Glyph = best,
                Face = MathFace.Regular,
                SizePt = size,
                ScaleX = scaleX,
                ScaleY = scale
            });
            return box;
        }

        /// <summary>Выросший знак, поставленный серединой на ось формулы.</summary>
        private MathBox StretchCentered(int codepoint, float targetHeight, float size)
        {
            if (codepoint == 0) return new MathBox();

            var box = StretchVertical(codepoint, targetHeight, size, uniform: false, out bool grew);
            if (!grew) return box;

            float axis = _c.AxisHeight * size;
            return Raise(box, axis - (box.Ascent - box.Descent) / 2f);
        }

        // ── Дробь ────────────────────────────────────────────────────────

        private MathBox LayoutFraction(MathFractionNode node, Context ctx)
        {
            float size = SizeOf(ctx.Style);

            if (node.Kind == MathFractionKind.Linear)
            {
                var atoms = new List<Atom>();
                atoms.Add(new Atom { Box = LayoutRow(node.Numerator, ctx), Class = MathAtomClass.Ordinary });
                atoms.Add(new Atom { Box = CharBox('/', size), Class = MathAtomClass.Ordinary });
                atoms.Add(new Atom { Box = LayoutRow(node.Denominator, ctx), Class = MathAtomClass.Ordinary });
                return AssembleRow(atoms, ctx);
            }

            var partStyle = FractionStyleOf(ctx.Style);
            var numerator = LayoutRow(node.Numerator, new Context(partStyle, ctx.Cramped));
            var denominator = LayoutRow(node.Denominator, new Context(partStyle, true));

            bool display = ctx.Style == MathStyle.Display;
            float axis = _c.AxisHeight * size;

            if (node.Kind == MathFractionKind.Skewed)
                return LayoutSkewedFraction(numerator, denominator, size, axis);

            float shiftUp;
            float shiftDown;
            float thickness = 0f;

            if (node.Kind == MathFractionKind.NoBar)
            {
                shiftUp = (display ? _c.StackTopDisplayStyleShiftUp : _c.StackTopShiftUp) * size;
                shiftDown = (display ? _c.StackBottomDisplayStyleShiftDown : _c.StackBottomShiftDown) * size;
                float gapMin = (display ? _c.StackDisplayStyleGapMin : _c.StackGapMin) * size;

                float gap = (shiftUp - numerator.Descent) - (denominator.Ascent - shiftDown);
                if (gap < gapMin)
                {
                    float half = (gapMin - gap) / 2f;
                    shiftUp += half;
                    shiftDown += half;
                }
            }
            else
            {
                thickness = _c.FractionRuleThickness * size;
                shiftUp = (display ? _c.FractionNumeratorDisplayStyleShiftUp : _c.FractionNumeratorShiftUp) * size;
                shiftDown = (display ? _c.FractionDenominatorDisplayStyleShiftDown : _c.FractionDenominatorShiftDown) * size;
                float numeratorGapMin = (display ? _c.FractionNumDisplayStyleGapMin : _c.FractionNumeratorGapMin) * size;
                float denominatorGapMin = (display ? _c.FractionDenomDisplayStyleGapMin : _c.FractionDenominatorGapMin) * size;

                // Числитель и знаменатель не подходят к черте ближе наименьшего зазора.
                float barTop = axis + thickness / 2f;
                float barBottom = axis - thickness / 2f;

                float numeratorGap = (shiftUp - numerator.Descent) - barTop;
                if (numeratorGap < numeratorGapMin) shiftUp += numeratorGapMin - numeratorGap;

                float denominatorGap = barBottom - (denominator.Ascent - shiftDown);
                if (denominatorGap < denominatorGapMin) shiftDown += denominatorGapMin - denominatorGap;
            }

            float inner = Math.Max(numerator.Width, denominator.Width);
            float pad = FractionSidePadEm * size;

            var box = new MathBox
            {
                Width = inner + pad * 2f,
                Ascent = shiftUp + numerator.Ascent,
                Descent = shiftDown + denominator.Descent
            };

            box.Items.Add(new MathBoxItem
            {
                Box = numerator,
                X = pad + (inner - numerator.Width) / 2f,
                Y = -shiftUp
            });
            box.Items.Add(new MathBoxItem
            {
                Box = denominator,
                X = pad + (inner - denominator.Width) / 2f,
                Y = shiftDown
            });

            if (thickness > 0f)
            {
                box.Items.Add(new MathRuleItem
                {
                    X = pad,
                    Y = -(axis + thickness / 2f),
                    Width = inner,
                    Height = thickness
                });
            }

            return box;
        }

        private MathBox LayoutSkewedFraction(MathBox numerator, MathBox denominator, float size, float axis)
        {
            float horizontalGap = _c.SkewedFractionHorizontalGap * size;
            float verticalGap = _c.SkewedFractionVerticalGap * size;

            // Низ числителя — над осью, верх знаменателя — под ней.
            float numeratorUp = axis + verticalGap / 2f + numerator.Descent;
            float denominatorDown = denominator.Ascent + verticalGap / 2f - axis;

            float top = numeratorUp + numerator.Ascent;
            float bottom = denominatorDown + denominator.Descent;

            var box = new MathBox
            {
                Width = numerator.Width + horizontalGap + denominator.Width,
                Ascent = top,
                Descent = bottom
            };

            box.Items.Add(new MathBoxItem { Box = numerator, X = 0f, Y = -numeratorUp });
            box.Items.Add(new MathBoxItem
            {
                Box = denominator,
                X = numerator.Width + horizontalGap,
                Y = denominatorDown
            });
            box.Items.Add(new MathLineItem
            {
                X = numerator.Width,
                Y = bottom,
                X2 = numerator.Width + horizontalGap,
                Y2 = -top,
                Thickness = _c.FractionRuleThickness * size
            });

            return box;
        }

        // ── Индексы ──────────────────────────────────────────────────────

        private MathBox LayoutScript(MathScriptNode node, Context ctx, out MathAtomClass atomClass)
        {
            var baseBox = LayoutRow(node.Base, ctx);
            atomClass = baseBox.LeftClass == MathAtomClass.Operator && baseBox.RightClass == MathAtomClass.Operator
                ? MathAtomClass.Operator
                : MathAtomClass.Ordinary;

            var scriptStyle = ScriptStyleOf(ctx.Style);
            var sup = Present(node.Sup)
                ? LayoutRow(node.Sup, new Context(scriptStyle, ctx.Cramped))
                : null;
            var sub = Present(node.Sub)
                ? LayoutRow(node.Sub, new Context(scriptStyle, true))
                : null;

            return AttachScripts(baseBox, sub, sup, ctx, node.Pre, baseBox.ItalicCorrection);
        }

        /// <summary>
        /// Ставит индексы у основания. italic — наклонная поправка основания: верхний
        /// индекс отодвигается на неё вправо.
        /// </summary>
        private MathBox AttachScripts(
            MathBox baseBox, MathBox? sub, MathBox? sup, Context ctx, bool pre, float italic)
        {
            if (sub is null && sup is null) return baseBox;

            float size = SizeOf(ctx.Style);

            // У одиночного знака индексы стоят по величинам шрифта; у составного
            // основания — не выше и не ниже его габарита.
            float up = baseBox.IsCharacter ? 0f : baseBox.Ascent - _c.SuperscriptBaselineDropMax * size;
            float down = baseBox.IsCharacter ? 0f : baseBox.Descent + _c.SubscriptBaselineDropMin * size;

            if (sup is not null)
            {
                float shift = (ctx.Cramped ? _c.SuperscriptShiftUpCramped : _c.SuperscriptShiftUp) * size;
                up = Math.Max(up, Math.Max(shift, sup.Descent + _c.SuperscriptBottomMin * size));
            }

            if (sub is not null)
            {
                down = Math.Max(down, _c.SubscriptShiftDown * size);
                if (sup is null)
                    down = Math.Max(down, sub.Ascent - _c.SubscriptTopMax * size);
            }

            if (sub is not null && sup is not null)
            {
                float gapMin = _c.SubSuperscriptGapMin * size;
                float gap = (up - sup.Descent) - (sub.Ascent - down);
                if (gap < gapMin)
                {
                    down += gapMin - gap;

                    // Пару индексов можно поднять, пока низ верхнего не выше предела.
                    float lift = _c.SuperscriptBottomMaxWithSubscript * size - (up - sup.Descent);
                    if (lift > 0f)
                    {
                        up += lift;
                        down -= lift;
                    }
                }
            }

            float spaceAfter = _c.SpaceAfterScript * size;
            float supWidth = sup?.Width ?? 0f;
            float subWidth = sub?.Width ?? 0f;

            // Габарит — по всем трём частям: высокий нижний индекс (корень, дробь)
            // поднимается выше основания, а глубокий верхний опускается ниже базовой
            // линии. С пустым основанием — индекс без буквы — иначе верх индекса
            // оказывался за пределами формулы и обрезался.
            float ascent = baseBox.Ascent;
            float descent = baseBox.Descent;
            if (sup is not null)
            {
                ascent = Math.Max(ascent, up + sup.Ascent);
                descent = Math.Max(descent, sup.Descent - up);
            }
            if (sub is not null)
            {
                ascent = Math.Max(ascent, sub.Ascent - down);
                descent = Math.Max(descent, down + sub.Descent);
            }

            var box = new MathBox
            {
                Ascent = ascent,
                Descent = descent,
                LeftClass = baseBox.LeftClass,
                RightClass = baseBox.RightClass
            };

            if (pre)
            {
                float scriptsWidth = Math.Max(supWidth, subWidth);
                if (sup is not null)
                    box.Items.Add(new MathBoxItem { Box = sup, X = scriptsWidth - supWidth, Y = -up });
                if (sub is not null)
                    box.Items.Add(new MathBoxItem { Box = sub, X = scriptsWidth - subWidth, Y = down });

                box.Items.Add(new MathBoxItem { Box = baseBox, X = scriptsWidth + spaceAfter, Y = 0f });
                box.Width = scriptsWidth + spaceAfter + baseBox.Width;
                box.ItalicCorrection = baseBox.ItalicCorrection;
                return box;
            }

            box.Items.Add(new MathBoxItem { Box = baseBox, X = 0f, Y = 0f });

            float right = baseBox.Width;
            if (sup is not null)
            {
                box.Items.Add(new MathBoxItem { Box = sup, X = baseBox.Width + italic, Y = -up });
                right = Math.Max(right, baseBox.Width + italic + supWidth);
            }

            if (sub is not null)
            {
                box.Items.Add(new MathBoxItem { Box = sub, X = baseBox.Width, Y = down });
                right = Math.Max(right, baseBox.Width + subWidth);
            }

            box.Width = right + spaceAfter;
            return box;
        }

        // ── Большой оператор и пределы ───────────────────────────────────

        private MathBox LayoutNaryOperator(MathNaryNode node, Context ctx)
        {
            float size = SizeOf(ctx.Style);
            bool display = ctx.Style == MathStyle.Display;
            float axis = _c.AxisHeight * size;

            MathBox op;
            if (display && node.Grow)
                op = StretchVertical(node.OperatorChar, _c.DisplayOperatorMinHeight * size, size, uniform: true, out _);
            else
                op = CharBox(node.OperatorChar, size);

            float italic = op.ItalicCorrection;

            // Знак оператора стоит серединой на оси формулы.
            op = Raise(op, axis - (op.Ascent - op.Descent) / 2f);
            op.IsCharacter = false;

            var scriptStyle = ScriptStyleOf(ctx.Style);
            var sup = Present(node.Sup)
                ? LayoutRow(node.Sup, new Context(scriptStyle, ctx.Cramped))
                : null;
            var sub = Present(node.Sub)
                ? LayoutRow(node.Sub, new Context(scriptStyle, true))
                : null;

            if (sup is null && sub is null) return op;

            // Формула в строке текста держит пределы сбоку, чтобы не раздвигать строки.
            if (!(node.LimitsAboveBelow && display))
            {
                var scripted = AttachScripts(op, sub, sup, ctx, pre: false, italic);
                return scripted;
            }

            return StackLimits(op, sub, sup, size, italic);
        }

        /// <summary>Пределы над и под знаком, все три части по общей середине.</summary>
        private MathBox StackLimits(MathBox op, MathBox? below, MathBox? above, float size, float italic)
        {
            float width = Math.Max(op.Width, Math.Max(above?.Width ?? 0f, below?.Width ?? 0f));

            var box = new MathBox
            {
                Width = width,
                Ascent = op.Ascent,
                Descent = op.Descent,
                LeftClass = MathAtomClass.Operator,
                RightClass = MathAtomClass.Operator
            };

            box.Items.Add(new MathBoxItem { Box = op, X = (width - op.Width) / 2f, Y = 0f });

            if (above is not null)
            {
                float gap = Math.Max(
                    _c.UpperLimitGapMin * size,
                    _c.UpperLimitBaselineRiseMin * size - above.Descent);
                float up = op.Ascent + gap + above.Descent;

                box.Items.Add(new MathBoxItem
                {
                    Box = above,
                    X = (width - above.Width) / 2f + italic / 2f,
                    Y = -up
                });
                box.Ascent = up + above.Ascent;
            }

            if (below is not null)
            {
                float gap = Math.Max(
                    _c.LowerLimitGapMin * size,
                    _c.LowerLimitBaselineDropMin * size - below.Ascent);
                float down = op.Descent + gap + below.Ascent;

                box.Items.Add(new MathBoxItem
                {
                    Box = below,
                    X = (width - below.Width) / 2f - italic / 2f,
                    Y = down
                });
                box.Descent = down + below.Descent;
            }

            return box;
        }

        private MathBox LayoutLimit(MathLimitNode node, Context ctx)
        {
            float size = SizeOf(ctx.Style);
            var baseBox = LayoutRow(node.Base, ctx);
            var limit = LayoutRow(node.Limit, new Context(ScriptStyleOf(ctx.Style), !node.Upper || ctx.Cramped));

            return node.Upper
                ? StackLimits(baseBox, null, limit, size, 0f)
                : StackLimits(baseBox, limit, null, size, 0f);
        }

        // ── Скобки ───────────────────────────────────────────────────────

        private MathBox LayoutDelimiter(MathDelimiterNode node, Context ctx)
        {
            float size = SizeOf(ctx.Style);
            float axis = _c.AxisHeight * size;

            var contents = new List<MathBox>(node.Items.Count);
            float ascent = 0f;
            float descent = 0f;
            foreach (var item in node.Items)
            {
                var content = LayoutRow(item, ctx);
                contents.Add(content);
                if (content.Ascent > ascent) ascent = content.Ascent;
                if (content.Descent > descent) descent = content.Descent;
            }

            // Скобка растёт, когда содержимое выше обычной скобки: закрывает его
            // почти целиком, симметрично относительно оси.
            float target = 0f;
            if (node.Grow)
            {
                float reach = Math.Max(ascent - axis, descent + axis);
                float full = 2f * reach;
                target = Math.Max(full * 0.901f, full - 5f * size / 10f);
            }

            var box = new MathBox { LeftClass = MathAtomClass.Open, RightClass = MathAtomClass.Close };
            float x = 0f;
            float top = 0f;
            float bottom = 0f;

            void Place(MathBox part)
            {
                box.Items.Add(new MathBoxItem { Box = part, X = x, Y = 0f });
                x += part.Width;
                if (part.Ascent > top) top = part.Ascent;
                if (part.Descent > bottom) bottom = part.Descent;
            }

            if (node.OpenChar != 0) Place(StretchCentered(node.OpenChar, target, size));

            for (int i = 0; i < contents.Count; i++)
            {
                if (i > 0 && node.SeparatorChar != 0)
                    Place(StretchCentered(node.SeparatorChar, target, size));
                Place(contents[i]);
            }

            if (node.CloseChar != 0) Place(StretchCentered(node.CloseChar, target, size));

            box.Width = x;
            box.Ascent = top;
            box.Descent = bottom;
            return box;
        }

        // ── Корень ───────────────────────────────────────────────────────

        private MathBox LayoutRadical(MathRadicalNode node, Context ctx)
        {
            float size = SizeOf(ctx.Style);
            var inner = LayoutRow(node.Radicand, new Context(ctx.Style, true));

            float gap = (ctx.Style == MathStyle.Display
                ? _c.RadicalDisplayStyleVerticalGap
                : _c.RadicalVerticalGap) * size;
            float thickness = _c.RadicalRuleThickness * size;
            float extra = _c.RadicalExtraAscender * size;

            float target = inner.Ascent + inner.Descent + gap + thickness;
            var sign = StretchVertical(0x221A, target, size, uniform: false, out _);

            // Знак выше нужного — лишнее делится между зазором и низом.
            if (sign.Height > target) gap += (sign.Height - target) / 2f;

            float ruleTop = inner.Ascent + gap + thickness;
            float signUp = ruleTop - sign.Ascent;
            float signBottom = sign.Descent - signUp;

            float signX = 0f;
            MathBox? degree = null;
            float degreeUp = 0f;

            if (Present(node.Degree))
            {
                degree = LayoutRow(node.Degree, new Context(MathStyle.ScriptScript, ctx.Cramped));

                float kernBefore = _c.RadicalKernBeforeDegree * size;
                float kernAfter = _c.RadicalKernAfterDegree * size;
                signX = Math.Max(0f, kernBefore + degree.Width + kernAfter);

                // Низ показателя поднят на долю высоты знака от его низа.
                degreeUp = -signBottom + _c.RadicalDegreeBottomRaisePercent * sign.Height + degree.Descent;
            }

            var box = new MathBox
            {
                Width = signX + sign.Width + inner.Width,
                Ascent = ruleTop + extra,
                Descent = Math.Max(inner.Descent, signBottom)
            };

            if (degree is not null)
            {
                float degreeX = Math.Max(0f, signX - _c.RadicalKernAfterDegree * size - degree.Width);
                box.Items.Add(new MathBoxItem { Box = degree, X = degreeX, Y = -degreeUp });
                if (degreeUp + degree.Ascent > box.Ascent) box.Ascent = degreeUp + degree.Ascent;
            }

            box.Items.Add(new MathBoxItem { Box = sign, X = signX, Y = -signUp });
            box.Items.Add(new MathBoxItem { Box = inner, X = signX + sign.Width, Y = 0f });
            box.Items.Add(new MathRuleItem
            {
                X = signX + sign.Width,
                Y = -ruleTop,
                Width = inner.Width,
                Height = thickness
            });

            return box;
        }

        // ── Знаки и линии над и под выражением ───────────────────────────

        private MathBox LayoutAccent(MathAccentNode node, Context ctx)
        {
            float size = SizeOf(ctx.Style);
            var baseBox = LayoutRow(node.Base, new Context(ctx.Style, true));

            ushort glyph = _font.GlyphOf(node.AccentChar, MathFace.Regular);
            if (glyph == 0) return baseBox;

            var extents = _font.Extents(glyph, MathFace.Regular, size);
            float inkWidth = extents.Right - extents.Left;

            // Знак рассчитан на букву высотой до своей опорной линии; над более
            // высоким основанием он поднимается на разницу.
            float lift = Math.Max(0f, baseBox.Ascent - _c.AccentBaseHeight * size);

            float x = (baseBox.Width - inkWidth) / 2f - extents.Left + baseBox.ItalicCorrection / 2f;

            var box = new MathBox
            {
                Width = baseBox.Width,
                Ascent = Math.Max(baseBox.Ascent, lift + extents.Top),
                Descent = baseBox.Descent,
                ItalicCorrection = baseBox.ItalicCorrection
            };

            box.Items.Add(new MathBoxItem { Box = baseBox, X = 0f, Y = 0f });
            box.Items.Add(new MathGlyphItem
            {
                Glyph = glyph,
                Face = MathFace.Regular,
                SizePt = size,
                X = x,
                Y = -lift
            });
            return box;
        }

        private MathBox LayoutBar(MathBarNode node, Context ctx)
        {
            float size = SizeOf(ctx.Style);
            var baseBox = LayoutRow(node.Base, node.Top ? new Context(ctx.Style, true) : ctx);

            var box = new MathBox
            {
                Width = baseBox.Width,
                Ascent = baseBox.Ascent,
                Descent = baseBox.Descent,
                ItalicCorrection = baseBox.ItalicCorrection
            };
            box.Items.Add(new MathBoxItem { Box = baseBox, X = 0f, Y = 0f });

            if (node.Top)
            {
                float gap = _c.OverbarVerticalGap * size;
                float thickness = _c.OverbarRuleThickness * size;

                box.Items.Add(new MathRuleItem
                {
                    X = 0f,
                    Y = -(baseBox.Ascent + gap + thickness),
                    Width = baseBox.Width,
                    Height = thickness
                });
                box.Ascent = baseBox.Ascent + gap + thickness + _c.OverbarExtraAscender * size;
            }
            else
            {
                float gap = _c.UnderbarVerticalGap * size;
                float thickness = _c.UnderbarRuleThickness * size;

                box.Items.Add(new MathRuleItem
                {
                    X = 0f,
                    Y = baseBox.Descent + gap,
                    Width = baseBox.Width,
                    Height = thickness
                });
                box.Descent = baseBox.Descent + gap + thickness + _c.UnderbarExtraDescender * size;
            }

            return box;
        }

        private MathBox LayoutGroupChar(MathGroupCharNode node, Context ctx)
        {
            float size = SizeOf(ctx.Style);
            var baseBox = LayoutRow(node.Base, ctx);

            ushort glyph = _font.GlyphOf(node.Char, MathFace.Regular);
            if (glyph == 0) return baseBox;

            // Знак растёт по ширине выражения: сначала готовые варианты шрифта.
            var extents = _font.Extents(glyph, MathFace.Regular, size);
            if (_font.MathTable is { } table)
            {
                foreach (var variant in table.HorizontalVariants(glyph))
                {
                    if (extents.Right - extents.Left >= baseBox.Width - 0.01f) break;

                    var variantExtents = _font.Extents(variant.Glyph, MathFace.Regular, size);
                    if (variantExtents.Right - variantExtents.Left <= extents.Right - extents.Left) continue;

                    glyph = variant.Glyph;
                    extents = variantExtents;
                }
            }

            float inkWidth = extents.Right - extents.Left;
            float scaleX = inkWidth > 0.01f && baseBox.Width > inkWidth ? baseBox.Width / inkWidth : 1f;
            float width = Math.Max(baseBox.Width, inkWidth);

            var box = new MathBox
            {
                Width = width,
                Ascent = baseBox.Ascent,
                Descent = baseBox.Descent
            };

            box.Items.Add(new MathBoxItem { Box = baseBox, X = (width - baseBox.Width) / 2f, Y = 0f });

            float glyphX = (width - inkWidth * scaleX) / 2f - extents.Left * scaleX;
            float glyphY;

            if (node.Top)
            {
                float gap = _c.StretchStackGapAboveMin * size;
                glyphY = -(baseBox.Ascent + gap + extents.Bottom);
                box.Ascent = baseBox.Ascent + gap + extents.Height;
            }
            else
            {
                float gap = _c.StretchStackGapBelowMin * size;
                glyphY = baseBox.Descent + gap + extents.Top;
                box.Descent = baseBox.Descent + gap + extents.Height;
            }

            box.Items.Add(new MathGlyphItem
            {
                Glyph = glyph,
                Face = MathFace.Regular,
                SizePt = size,
                ScaleX = scaleX,
                X = glyphX,
                Y = glyphY
            });
            return box;
        }

        private MathBox LayoutBorderBox(MathBoxNode node, Context ctx)
        {
            var inner = LayoutRow(node.Base, ctx);

            bool anyLine = node.BorderTop || node.BorderBottom || node.BorderLeft || node.BorderRight
                || node.StrikeHorizontal || node.StrikeVertical
                || node.StrikeDiagonalUp || node.StrikeDiagonalDown;
            if (!anyLine) return inner;

            float size = SizeOf(ctx.Style);
            float pad = BorderBoxPadEm * size;
            float thickness = _c.FractionRuleThickness * size;

            float width = inner.Width + pad * 2f;
            float top = inner.Ascent + pad;
            float bottom = inner.Descent + pad;

            var box = new MathBox { Width = width, Ascent = top, Descent = bottom };
            box.Items.Add(new MathBoxItem { Box = inner, X = pad, Y = 0f });

            if (node.BorderTop)
                box.Items.Add(new MathRuleItem { X = 0f, Y = -top, Width = width, Height = thickness });
            if (node.BorderBottom)
                box.Items.Add(new MathRuleItem { X = 0f, Y = bottom - thickness, Width = width, Height = thickness });
            if (node.BorderLeft)
                box.Items.Add(new MathRuleItem { X = 0f, Y = -top, Width = thickness, Height = top + bottom });
            if (node.BorderRight)
                box.Items.Add(new MathRuleItem { X = width - thickness, Y = -top, Width = thickness, Height = top + bottom });

            float middle = (bottom - top) / 2f;
            if (node.StrikeHorizontal)
                box.Items.Add(new MathRuleItem { X = 0f, Y = middle - thickness / 2f, Width = width, Height = thickness });
            if (node.StrikeVertical)
                box.Items.Add(new MathRuleItem { X = width / 2f - thickness / 2f, Y = -top, Width = thickness, Height = top + bottom });
            if (node.StrikeDiagonalUp)
                box.Items.Add(new MathLineItem { X = 0f, Y = bottom, X2 = width, Y2 = -top, Thickness = thickness });
            if (node.StrikeDiagonalDown)
                box.Items.Add(new MathLineItem { X = 0f, Y = -top, X2 = width, Y2 = bottom, Thickness = thickness });

            return box;
        }

        // ── Матрица и столбец уравнений ──────────────────────────────────

        private MathBox LayoutMatrix(MathMatrixNode node, Context ctx)
        {
            if (node.Rows.Count == 0) return new MathBox();

            float size = SizeOf(ctx.Style);
            float axis = _c.AxisHeight * size;
            var cellContext = new Context(ctx.Style == MathStyle.Display ? MathStyle.Text : ctx.Style, false);

            int columnCount = 0;
            foreach (var row in node.Rows)
                if (row.Count > columnCount) columnCount = row.Count;
            if (columnCount == 0) return new MathBox();

            var cells = new MathBox[node.Rows.Count][];
            var columnWidths = new float[columnCount];
            var rowAscents = new float[node.Rows.Count];
            var rowDescents = new float[node.Rows.Count];

            for (int r = 0; r < node.Rows.Count; r++)
            {
                cells[r] = new MathBox[node.Rows[r].Count];
                rowAscents[r] = MatrixRowAscentEm * size;
                rowDescents[r] = MatrixRowDescentEm * size;

                for (int c = 0; c < node.Rows[r].Count; c++)
                {
                    var cell = LayoutRow(node.Rows[r][c], cellContext);
                    cells[r][c] = cell;

                    if (cell.Width > columnWidths[c]) columnWidths[c] = cell.Width;
                    if (cell.Ascent > rowAscents[r]) rowAscents[r] = cell.Ascent;
                    if (cell.Descent > rowDescents[r]) rowDescents[r] = cell.Descent;
                }
            }

            float columnGap = MatrixColumnGapEm * size;
            float rowGap = MatrixRowGapEm * size;

            float totalWidth = 0f;
            for (int c = 0; c < columnCount; c++)
                totalWidth += columnWidths[c] + (c > 0 ? columnGap : 0f);

            float totalHeight = 0f;
            for (int r = 0; r < node.Rows.Count; r++)
                totalHeight += rowAscents[r] + rowDescents[r] + (r > 0 ? rowGap : 0f);

            // Блок целиком стоит серединой на оси формулы.
            float top = axis + totalHeight / 2f;

            var box = new MathBox
            {
                Width = totalWidth,
                Ascent = top,
                Descent = totalHeight - top
            };

            float y = -top;
            for (int r = 0; r < node.Rows.Count; r++)
            {
                if (r > 0) y += rowGap;
                float baseline = y + rowAscents[r];

                float x = 0f;
                for (int c = 0; c < columnCount; c++)
                {
                    if (c > 0) x += columnGap;

                    if (c < cells[r].Length)
                    {
                        var cell = cells[r][c];
                        float offset = node.IsEquationArray ? 0f : (columnWidths[c] - cell.Width) / 2f;
                        box.Items.Add(new MathBoxItem { Box = cell, X = x + offset, Y = baseline });
                    }

                    x += columnWidths[c];
                }

                y = baseline + rowDescents[r];
            }

            return box;
        }

        private MathBox LayoutPhantom(MathPhantomNode node, Context ctx)
        {
            var inner = LayoutRow(node.Base, ctx);

            var box = new MathBox
            {
                Width = node.ZeroWidth ? 0f : inner.Width,
                Ascent = node.ZeroAscent ? 0f : inner.Ascent,
                Descent = node.ZeroDescent ? 0f : inner.Descent
            };

            if (node.Show) box.Items.Add(new MathBoxItem { Box = inner, X = 0f, Y = 0f });
            return box;
        }
    }
}
