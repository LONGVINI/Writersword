using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Writersword.Modules.TextEditor.Models.Document
{
    /// <summary>
    /// Способ задания ширины столбца таблицы.
    /// </summary>
    public enum TableColumnWidthType
    {
        /// <summary>Ширина вычисляется автоматически.</summary>
        Auto = 0,
        /// <summary>Фиксированная ширина в мм.</summary>
        Fixed = 1,
        /// <summary>Процент от ширины таблицы.</summary>
        Percent = 2
    }

    /// <summary>
    /// Стиль границы ячейки.
    /// </summary>
    public enum BorderStyle
    {
        None = 0,
        Single = 1,
        Double = 2,
        Dashed = 3,
        Dotted = 4,
        Thick = 5,

        /// <summary>Три тонкие черты (w:val="triple").</summary>
        Triple = 6,

        /// <summary>Волнистая линия (w:val="wave").</summary>
        Wave = 7,

        /// <summary>Объёмная выпуклая рамка (w:val="threeDEmboss").</summary>
        ThreeDEmboss = 8,

        /// <summary>Объёмная вдавленная рамка (w:val="threeDEngrave").</summary>
        ThreeDEngrave = 9,

        /// <summary>Выпуклая рамка (w:val="outset"): светлая сверху и слева, тёмная снизу и справа.</summary>
        Outset = 10,

        /// <summary>Вдавленная рамка (w:val="inset"): тёмная сверху и слева, светлая снизу и справа.</summary>
        Inset = 11,

        /// <summary>Штрих и точка по очереди (w:val="dotDash").</summary>
        DotDash = 12,

        /// <summary>Штрих и две точки по очереди (w:val="dotDotDash").</summary>
        DotDotDash = 13,

        /// <summary>Штрихи с узким просветом (w:val="dashSmallGap").</summary>
        DashSmallGap = 14,

        /// <summary>Полоса из наклонных штрихов, толстого и тонкого по очереди (w:val="dashDotStroked").</summary>
        DashDotStroked = 15,

        /// <summary>Тонкая черта снаружи, толстая внутри, малый просвет (w:val="thinThickSmallGap").</summary>
        ThinThickSmallGap = 16,

        /// <summary>Толстая черта снаружи, тонкая внутри, малый просвет (w:val="thickThinSmallGap").</summary>
        ThickThinSmallGap = 17,

        /// <summary>Тонкая, толстая и тонкая черты, малые просветы (w:val="thinThickThinSmallGap").</summary>
        ThinThickThinSmallGap = 18,

        /// <summary>Тонкая черта снаружи, толстая внутри, средний просвет (w:val="thinThickMediumGap").</summary>
        ThinThickMediumGap = 19,

        /// <summary>Толстая черта снаружи, тонкая внутри, средний просвет (w:val="thickThinMediumGap").</summary>
        ThickThinMediumGap = 20,

        /// <summary>Тонкая, толстая и тонкая черты, средние просветы (w:val="thinThickThinMediumGap").</summary>
        ThinThickThinMediumGap = 21,

        /// <summary>Тонкая черта снаружи, толстая внутри, большой просвет (w:val="thinThickLargeGap").</summary>
        ThinThickLargeGap = 22,

        /// <summary>Толстая черта снаружи, тонкая внутри, большой просвет (w:val="thickThinLargeGap").</summary>
        ThickThinLargeGap = 23,

        /// <summary>Тонкая, толстая и тонкая черты, большие просветы (w:val="thinThickThinLargeGap").</summary>
        ThinThickThinLargeGap = 24,

        /// <summary>Две волнистые линии (w:val="doubleWave").</summary>
        DoubleWave = 25
    }

    /// <summary>
    /// Вертикальное выравнивание в ячейке таблицы.
    /// </summary>
    public enum VerticalAlignment
    {
        Top = 0,
        Middle = 1,
        Bottom = 2
    }

    /// <summary>
    /// Направление текста в ячейке (w:textDirection у Word).
    /// </summary>
    public enum CellTextDirection
    {
        /// <summary>Обычный горизонтальный текст.</summary>
        Horizontal = 0,

        /// <summary>Снизу вверх (btLr): строка повёрнута на 90° против часовой стрелки.</summary>
        BottomToTop = 1,

        /// <summary>Сверху вниз (tbRl): строка повёрнута на 90° по часовой стрелке.</summary>
        TopToBottom = 2
    }

    /// <summary>От чего отсчитывается положение таблицы с обтеканием текстом.</summary>
    public enum TableFloatAnchor
    {
        /// <summary>От текста: по горизонтали — от полосы набора, по вертикали — от абзаца под таблицей.</summary>
        Text = 0,

        /// <summary>От полей листа: от левого поля и от верхнего поля.</summary>
        Margin = 1,

        /// <summary>От краёв листа.</summary>
        Page = 2,

        // Опоры ниже есть только у рисунков Word: таблица с обтеканием их не знает.

        /// <summary>Левое поле листа: от края листа до полосы набора (leftMargin).</summary>
        LeftMargin = 3,

        /// <summary>Правое поле листа: от полосы набора до края листа (rightMargin).</summary>
        RightMargin = 4,

        /// <summary>Внутреннее поле (insideMargin). Без зеркальных полей — левое.</summary>
        InsideMargin = 5,

        /// <summary>Внешнее поле (outsideMargin). Без зеркальных полей — правое.</summary>
        OutsideMargin = 6,

        /// <summary>Верхнее поле листа: от края листа до полосы набора (topMargin).</summary>
        TopMargin = 7,

        /// <summary>Нижнее поле листа: от полосы набора до края листа (bottomMargin).</summary>
        BottomMargin = 8
    }

    /// <summary>Положение таблицы с обтеканием относительно своей опоры по одной оси.</summary>
    public enum TableFloatAlign
    {
        /// <summary>По смещению в пунктах.</summary>
        Offset = 0,

        /// <summary>К началу опоры: слева или сверху.</summary>
        Start = 1,

        /// <summary>По середине опоры.</summary>
        Center = 2,

        /// <summary>К концу опоры: справа или снизу.</summary>
        End = 3
    }

    /// <summary>
    /// Положение таблицы с обтеканием текстом (w:tblpPr у Word, «Обтекание: вокруг» в
    /// свойствах таблицы). Такая таблица не занимает строку в потоке: она стоит в своей
    /// точке листа, а текст абзацев обходит её с обеих сторон.
    /// </summary>
    public sealed class TableFloatPosition
    {
        /// <summary>От чего отсчитывается положение по горизонтали.</summary>
        public TableFloatAnchor HorizontalAnchor { get; set; } = TableFloatAnchor.Text;

        /// <summary>Положение по горизонтали: смещение или сторона опоры.</summary>
        public TableFloatAlign HorizontalAlign { get; set; } = TableFloatAlign.Offset;

        /// <summary>Смещение левого края таблицы от начала опоры, пт. Действует при положении «по смещению».</summary>
        public double XPt { get; set; }

        /// <summary>От чего отсчитывается положение по вертикали.</summary>
        public TableFloatAnchor VerticalAnchor { get; set; } = TableFloatAnchor.Text;

        /// <summary>Положение по вертикали: смещение или сторона опоры. От текста — только смещение.</summary>
        public TableFloatAlign VerticalAlign { get; set; } = TableFloatAlign.Offset;

        /// <summary>Смещение верха таблицы от начала опоры, пт. Действует при положении «по смещению».</summary>
        public double YPt { get; set; }

        /// <summary>Расстояние от таблицы до текста слева, пт.</summary>
        public double LeftFromTextPt { get; set; }

        /// <summary>Расстояние от таблицы до текста справа, пт.</summary>
        public double RightFromTextPt { get; set; }

        /// <summary>Расстояние от таблицы до текста сверху, пт.</summary>
        public double TopFromTextPt { get; set; }

        /// <summary>Расстояние от таблицы до текста снизу, пт.</summary>
        public double BottomFromTextPt { get; set; }

        public TableFloatPosition Clone() => (TableFloatPosition)MemberwiseClone();

        /// <summary>
        /// Левый верхний угол объекта на листе по этому положению. Тот же счёт, что у
        /// таблицы с обтеканием; нужен плавающей картинке, пришедшей из Word, — её
        /// положение описано теми же опорами.
        /// </summary>
        /// <param name="anchorYPt">Верх абзаца-опоры: место в потоке, где встречен объект.</param>
        public (float XPt, float YPt) ResolveOrigin(
            float objectWidthPt, float objectHeightPt,
            float textXPt, float textWidthPt, float pageXPt, float pageWidthPt,
            float pageYPt, float pageHeightPt, float marginTopPt, float marginBottomPt,
            float anchorYPt)
        {
            // Опора по горизонтали: лист целиком, полоса набора или одно из боковых
            // полей листа — промежуток между краем листа и полосой набора.
            float baseXPt;
            float spanXPt;
            switch (HorizontalAnchor)
            {
                case TableFloatAnchor.Page:
                    baseXPt = pageXPt;
                    spanXPt = pageWidthPt;
                    break;
                case TableFloatAnchor.LeftMargin:
                case TableFloatAnchor.InsideMargin:
                    baseXPt = pageXPt;
                    spanXPt = Math.Max(0f, textXPt - pageXPt);
                    break;
                case TableFloatAnchor.RightMargin:
                case TableFloatAnchor.OutsideMargin:
                    baseXPt = textXPt + textWidthPt;
                    spanXPt = Math.Max(0f, pageXPt + pageWidthPt - (textXPt + textWidthPt));
                    break;
                default:
                    baseXPt = textXPt;
                    spanXPt = textWidthPt;
                    break;
            }

            float xPt = HorizontalAlign switch
            {
                TableFloatAlign.Start => baseXPt,
                TableFloatAlign.Center => baseXPt + (spanXPt - objectWidthPt) / 2f,
                TableFloatAlign.End => baseXPt + spanXPt - objectWidthPt,
                _ => baseXPt + (float)XPt
            };

            // Опора по вертикали. От текста отсчёт идёт от верха абзаца, и стороны у
            // такой опоры нет — только смещение.
            float yPt;
            if (VerticalAnchor == TableFloatAnchor.Text)
            {
                yPt = anchorYPt + (float)YPt;
            }
            else
            {
                // Лист целиком, полоса набора или верхнее либо нижнее поле листа.
                float baseYPt;
                float spanYPt;
                switch (VerticalAnchor)
                {
                    case TableFloatAnchor.Page:
                        baseYPt = pageYPt;
                        spanYPt = pageHeightPt;
                        break;
                    case TableFloatAnchor.TopMargin:
                    case TableFloatAnchor.InsideMargin:
                        baseYPt = pageYPt;
                        spanYPt = marginTopPt;
                        break;
                    case TableFloatAnchor.BottomMargin:
                    case TableFloatAnchor.OutsideMargin:
                        baseYPt = pageYPt + pageHeightPt - marginBottomPt;
                        spanYPt = marginBottomPt;
                        break;
                    default:
                        baseYPt = pageYPt + marginTopPt;
                        spanYPt = pageHeightPt - marginTopPt - marginBottomPt;
                        break;
                }

                yPt = VerticalAlign switch
                {
                    TableFloatAlign.Start => baseYPt,
                    TableFloatAlign.Center => baseYPt + (spanYPt - objectHeightPt) / 2f,
                    TableFloatAlign.End => baseYPt + spanYPt - objectHeightPt,
                    _ => baseYPt + (float)YPt
                };
            }

            return (xPt, yPt);
        }

        /// <summary>
        /// На какую долю сдвига полосы набора по горизонтали уходит объект с этим
        /// положением. Полоса набора сдвигается, когда переплёт переходит на другую
        /// сторону листа: поля меняются местами, а край бумаги остаётся где был.
        /// От текста — объект уходит вместе с полосой целиком (1), от листа — стоит на
        /// месте (0), по середине бокового поля — на половину сдвига: поле с одной
        /// стороны сужается, с другой расширяется.
        /// </summary>
        public float HorizontalTextFollow()
        {
            // Отсчёт линейный по краю полосы набора при неизменном листе и ширине
            // полосы, поэтому доля — разность двух расчётов при единичном сдвиге.
            const float textXPt = 100f;
            const float textWidthPt = 200f;
            const float pageWidthPt = 400f;
            const float objectWidthPt = 10f;

            float atText = ResolveOrigin(
                objectWidthPt, objectWidthPt, textXPt, textWidthPt, 0f, pageWidthPt,
                0f, pageWidthPt, 0f, 0f, 0f).XPt;
            float atShifted = ResolveOrigin(
                objectWidthPt, objectWidthPt, textXPt + 1f, textWidthPt, 0f, pageWidthPt,
                0f, pageWidthPt, 0f, 0f, 0f).XPt;

            return atShifted - atText;
        }
    }

    /// <summary>
    /// Выравнивание таблицы по горизонтали внутри текстовой области (w:jc у w:tblPr).
    /// </summary>
    public enum TableBlockAlignment
    {
        /// <summary>По левому краю со сдвигом на LeftIndentPt.</summary>
        Left = 0,

        /// <summary>По центру текстовой области; LeftIndentPt не действует.</summary>
        Center = 1,

        /// <summary>По правому краю текстовой области; LeftIndentPt не действует.</summary>
        Right = 2
    }

    /// <summary>
    /// Описание одного столбца таблицы.
    /// </summary>
    public sealed class TableColumnDefinition
    {
        public TableColumnWidthType WidthType { get; set; } = TableColumnWidthType.Auto;
        public double WidthValue { get; set; }
    }

    /// <summary>
    /// Границы ячейки таблицы.
    /// </summary>
    public sealed class CellBorders
    {
        public BorderStyle Top { get; set; } = BorderStyle.Single;
        public BorderStyle Bottom { get; set; } = BorderStyle.Single;
        public BorderStyle Left { get; set; } = BorderStyle.Single;
        public BorderStyle Right { get; set; } = BorderStyle.Single;
        public double ThicknessPt { get; set; } = 0.5;

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Color { get; set; }

        // Цвет и толщина отдельной стороны, когда они не те, что общие (Color, ThicknessPt):
        // у Word у каждой стороны ячейки свой цвет и своя толщина. null — сторона берёт общие.
        // Правка границы в редакторе снимает их со своих сторон (TableCellSetBorder).

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? TopColor { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? BottomColor { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LeftColor { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? RightColor { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? TopThicknessPt { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? BottomThicknessPt { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? LeftThicknessPt { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? RightThicknessPt { get; set; }

        /// <summary>Действующий цвет верхней стороны.</summary>
        public string? EffectiveTopColor() => TopColor ?? Color;

        /// <summary>Действующий цвет нижней стороны.</summary>
        public string? EffectiveBottomColor() => BottomColor ?? Color;

        /// <summary>Действующий цвет левой стороны.</summary>
        public string? EffectiveLeftColor() => LeftColor ?? Color;

        /// <summary>Действующий цвет правой стороны.</summary>
        public string? EffectiveRightColor() => RightColor ?? Color;

        /// <summary>Действующая толщина верхней стороны, пт.</summary>
        public double EffectiveTopThicknessPt() => TopThicknessPt ?? ThicknessPt;

        /// <summary>Действующая толщина нижней стороны, пт.</summary>
        public double EffectiveBottomThicknessPt() => BottomThicknessPt ?? ThicknessPt;

        /// <summary>Действующая толщина левой стороны, пт.</summary>
        public double EffectiveLeftThicknessPt() => LeftThicknessPt ?? ThicknessPt;

        /// <summary>Действующая толщина правой стороны, пт.</summary>
        public double EffectiveRightThicknessPt() => RightThicknessPt ?? ThicknessPt;

        public CellBorders Clone() => (CellBorders)MemberwiseClone();

        /// <summary>
        /// Меняет местами левую и правую стороны вместе с их цветом и толщиной —
        /// для зеркального отражения колонок таблицы.
        /// </summary>
        public void SwapLeftRight()
        {
            (Left, Right) = (Right, Left);
            (LeftColor, RightColor) = (RightColor, LeftColor);
            (LeftThicknessPt, RightThicknessPt) = (RightThicknessPt, LeftThicknessPt);
        }
    }

    /// <summary>
    /// Таблица внутри ячейки другой таблицы.
    ///
    /// Стоит в содержимом ячейки перед абзацем <see cref="BeforeParagraphId"/> — так же
    /// устроена ячейка у Word: после вложенной таблицы в ней всегда идёт абзац. Привязка
    /// к абзацу, а не к номеру в списке, переживает правку текста над таблицей: абзац
    /// над ней можно разбить или удалить, и таблица останется на своём месте.
    /// </summary>
    public sealed class NestedTable
    {
        /// <summary>Абзац ячейки, перед которым стоит таблица.</summary>
        public Guid BeforeParagraphId { get; set; }

        /// <summary>
        /// Номер этого абзаца в ячейке на момент последней вёрстки. Запасная привязка:
        /// работает, когда абзаца с таким идентификатором в ячейке уже нет. Число, равное
        /// количеству абзацев, ставит таблицу после последнего.
        /// </summary>
        public int BeforeParagraphIndex { get; set; }

        /// <summary>Сама таблица.</summary>
        public TableBlock Table { get; set; } = new();
    }

    /// <summary>
    /// Плавающий объект в ячейке таблицы — картинка или фигура Word с привязкой к
    /// ячейке (layoutInCell). Стоит у своего абзаца ячейки, как плавающий объект
    /// документа стоит у своего абзаца: положение отсчитывается от области
    /// содержимого ячейки и от верха абзаца, текст ячейки его обтекает.
    /// </summary>
    public sealed class CellFloat
    {
        /// <summary>Абзац ячейки, к которому привязан объект.</summary>
        public Guid AnchorParagraphId { get; set; }

        /// <summary>
        /// Номер этого абзаца в ячейке на момент последней вёрстки. Запасная привязка:
        /// работает, когда абзаца с таким идентификатором в ячейке уже нет.
        /// </summary>
        public int AnchorParagraphIndex { get; set; }

        /// <summary>Сам объект: <see cref="ImageBlock"/> или <see cref="ShapeBlock"/>.</summary>
        public BlockModel Object { get; set; } = null!;
    }

    /// <summary>
    /// Одна ячейка таблицы.
    /// Содержит список параграфов (как и обычный поток документа) и, между ними,
    /// вложенные таблицы.
    /// </summary>
    public sealed class TableCell
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>Содержимое ячейки — список параграфов.</summary>
        public List<ParagraphBlock> Paragraphs { get; set; } = new() { new ParagraphBlock() };

        /// <summary>
        /// Плавающие объекты ячейки (картинки и фигуры Word с обтеканием). Null — их
        /// нет. Каждый привязан к своему абзацу ячейки (см. <see cref="CellFloat"/>).
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<CellFloat>? Floats { get; set; }

        /// <summary>
        /// Номер абзаца ячейки, к которому привязан плавающий объект. Абзац, которого
        /// уже нет, заменяется абзацем на его месте, а за концом ячейки — последним.
        /// </summary>
        public int FloatParagraphIndex(CellFloat cellFloat)
        {
            for (int i = 0; i < Paragraphs.Count; i++)
                if (Paragraphs[i].Id == cellFloat.AnchorParagraphId) return i;

            return Math.Clamp(cellFloat.AnchorParagraphIndex, 0, Math.Max(0, Paragraphs.Count - 1));
        }

        /// <summary>
        /// Освежает привязку плавающих объектов: запоминает номер абзаца каждого, а
        /// объект, чей абзац исчез, привязывает к абзацу на его месте.
        /// </summary>
        public void AnchorFloats()
        {
            if (Floats is not { Count: > 0 } floats || Paragraphs.Count == 0) return;

            foreach (var cellFloat in floats)
            {
                int position = FloatParagraphIndex(cellFloat);
                cellFloat.AnchorParagraphIndex = position;
                cellFloat.AnchorParagraphId = Paragraphs[position].Id;
            }
        }

        /// <summary>
        /// Таблицы внутри ячейки. Null — вложенных таблиц нет. Каждая стоит перед своим
        /// абзацем (см. <see cref="NestedTable"/>); порядок в списке — порядок таблиц,
        /// стоящих перед одним и тем же абзацем.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<NestedTable>? NestedTables { get; set; }

        /// <summary>
        /// Перед каким по счёту абзацем ячейки стоит вложенная таблица. Число, равное
        /// количеству абзацев, — после последнего.
        /// </summary>
        public int NestedTablePosition(NestedTable nested)
        {
            for (int i = 0; i < Paragraphs.Count; i++)
                if (Paragraphs[i].Id == nested.BeforeParagraphId) return i;

            return Math.Clamp(nested.BeforeParagraphIndex, 0, Paragraphs.Count);
        }

        /// <summary>
        /// Освежает привязку вложенных таблиц: запоминает номер абзаца, перед которым
        /// стоит каждая, а таблицу, чей абзац исчез, привязывает к абзацу на его месте.
        /// Вызывается вёрсткой — после неё привязка по идентификатору и по номеру совпадают.
        /// </summary>
        public void AnchorNestedTables()
        {
            if (NestedTables is not { Count: > 0 } nestedTables) return;

            foreach (var nested in nestedTables)
            {
                int position = NestedTablePosition(nested);
                nested.BeforeParagraphIndex = position;
                if (position < Paragraphs.Count)
                    nested.BeforeParagraphId = Paragraphs[position].Id;
            }
        }

        /// <summary>Стоит ли вложенная таблица прямо перед абзацем с этим номером.</summary>
        public bool HasNestedTableBefore(int paragraphIndex)
        {
            if (NestedTables is not { Count: > 0 } nestedTables) return false;

            foreach (var nested in nestedTables)
                if (NestedTablePosition(nested) == paragraphIndex) return true;

            return false;
        }

        /// <summary>
        /// Ставит таблицу в ячейку перед абзацем с заданным номером. Перед одним абзацем
        /// может стоять несколько таблиц: новая встаёт первой из них или последней.
        /// </summary>
        public NestedTable InsertNestedTable(TableBlock table, int beforeParagraphIndex, bool first)
        {
            int position = Math.Clamp(beforeParagraphIndex, 0, Paragraphs.Count);
            var nested = new NestedTable
            {
                Table = table,
                BeforeParagraphIndex = position,
                BeforeParagraphId = position < Paragraphs.Count ? Paragraphs[position].Id : Guid.Empty
            };

            NestedTables ??= new List<NestedTable>();

            int listIndex = NestedTables.Count;
            if (first)
            {
                for (int i = 0; i < NestedTables.Count; i++)
                {
                    if (NestedTablePosition(NestedTables[i]) != position) continue;
                    listIndex = i;
                    break;
                }
            }

            NestedTables.Insert(listIndex, nested);
            return nested;
        }

        /// <summary>Убирает таблицу из ячейки. False — такой записи в ячейке нет.</summary>
        public bool RemoveNestedTable(NestedTable nested)
        {
            if (NestedTables is null || !NestedTables.Remove(nested)) return false;
            if (NestedTables.Count == 0) NestedTables = null;
            return true;
        }

        /// <summary>
        /// Убирает таблицы, стоящие перед абзацами с номерами от firstPosition до
        /// lastPosition включительно. Нужна удалению выделения: абзацы этого промежутка
        /// исчезают, и таблицы между ними уходят вместе с ними.
        /// </summary>
        public void RemoveNestedTablesBetween(int firstPosition, int lastPosition)
        {
            if (NestedTables is not { Count: > 0 } nestedTables) return;

            for (int i = nestedTables.Count - 1; i >= 0; i--)
            {
                int position = NestedTablePosition(nestedTables[i]);
                if (position >= firstPosition && position <= lastPosition)
                    nestedTables.RemoveAt(i);
            }

            if (nestedTables.Count == 0) NestedTables = null;
        }

        /// <summary>
        /// Абзацы ячейки вместе с абзацами вложенных таблиц на всю глубину, в порядке
        /// чтения: таблица — перед своим абзацем.
        /// </summary>
        public IEnumerable<ParagraphBlock> ParagraphsDeep()
        {
            if (NestedTables is not { Count: > 0 } nestedTables)
            {
                foreach (var paragraph in Paragraphs)
                    yield return paragraph;
                yield break;
            }

            for (int i = 0; i <= Paragraphs.Count; i++)
            {
                foreach (var nested in nestedTables)
                {
                    if (NestedTablePosition(nested) != i) continue;

                    foreach (var nestedCell in nested.Table.Cells)
                        foreach (var paragraph in nestedCell.ParagraphsDeep())
                            yield return paragraph;
                }

                if (i < Paragraphs.Count)
                    yield return Paragraphs[i];
            }
        }

        /// <summary>
        /// Индекс строки (0-based). Для объединённых ячеек — строка начала.
        /// </summary>
        public int Row { get; set; }

        /// <summary>
        /// Индекс столбца (0-based). Для объединённых ячеек — столбец начала.
        /// </summary>
        public int Column { get; set; }

        /// <summary>Количество объединённых строк (1 = нет объединения).</summary>
        public int RowSpan { get; set; } = 1;

        /// <summary>Количество объединённых столбцов (1 = нет объединения).</summary>
        public int ColSpan { get; set; } = 1;

        /// <summary>Цвет фона ячейки. Null — прозрачный.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? BackgroundColor { get; set; }

        /// <summary>
        /// Узор заливки поверх цвета фона — имя из w:shd w:val, как его пишет Word (pct25,
        /// diagStripe, thinHorzCross…). Null — узора нет. Новый цвет фона из редактора
        /// узор снимает.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ShadingPattern { get; set; }

        /// <summary>Цвет узора заливки (w:shd w:color). Null — «авто», цвет текста.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ShadingPatternColor { get; set; }

        public CellBorders Borders { get; set; } = new();

        public VerticalAlignment VerticalAlignment { get; set; } = VerticalAlignment.Top;

        /// <summary>
        /// Направление текста. У повёрнутой ячейки строки идут вдоль её высоты:
        /// длина строки — высота ячейки, а строки складываются поперёк, по ширине.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public CellTextDirection TextDirection { get; set; } = CellTextDirection.Horizontal;

        /// <summary>Текст ячейки повёрнут на 90° в ту или другую сторону.</summary>
        [JsonIgnore]
        public bool IsRotated => TextDirection != CellTextDirection.Horizontal;

        /// <summary>Внутренние отступы ячейки в пунктах.</summary>
        public double PaddingTopPt { get; set; } = 4;
        public double PaddingBottomPt { get; set; } = 4;
        public double PaddingLeftPt { get; set; } = 6;
        public double PaddingRightPt { get; set; } = 6;
    }

    /// <summary>
    /// Таблица в документе.
    /// Реализована как кастомный контрол (не DataGrid).
    /// </summary>
    public sealed class TableBlock : BlockModel
    {
        public override BlockType BlockType => BlockType.Table;

        /// <summary>Количество строк.</summary>
        public int RowCount { get; set; }

        /// <summary>Количество столбцов.</summary>
        public int ColumnCount { get; set; }

        /// <summary>Определения столбцов (ширины).</summary>
        public List<TableColumnDefinition> Columns { get; set; } = new();

        /// <summary>
        /// Заданная пользователем минимальная высота строк в пунктах, по индексу строки.
        /// Именно минимальная, а не фиксированная: строка не может стать ниже этого
        /// значения, но если содержимое выше — растёт по содержимому, как обычно.
        /// Ноль или отсутствие записи означают «высота целиком по содержимому».
        /// Список может быть короче числа строк — у остальных высота не задавалась.
        /// </summary>
        public List<double> RowMinHeightsPt { get; set; } = new();

        /// <summary>Минимальная высота строки или 0, если не задавалась.</summary>
        public double GetRowMinHeightPt(int row)
            => row >= 0 && row < RowMinHeightsPt.Count ? RowMinHeightsPt[row] : 0;

        /// <summary>
        /// Задать минимальную высоту строки. Список при необходимости дополняется
        /// нулями: строки до неё высоту могли не задавать вовсе.
        /// </summary>
        public void SetRowMinHeightPt(int row, double heightPt)
        {
            if (row < 0) return;
            while (RowMinHeightsPt.Count <= row) RowMinHeightsPt.Add(0);
            RowMinHeightsPt[row] = heightPt < 0 ? 0 : heightPt;

            // Высота, заданная заново, — снова «не менее»: точная высота из Word уступает правке.
            SetRowHeightExact(row, false);
        }

        /// <summary>
        /// Строки с точной высотой (w:trHeight w:hRule="exact" у Word): строка ровно той
        /// высоты, что в <see cref="RowMinHeightsPt"/>, а лишнее содержимое срезается.
        /// null — таких строк нет. Остальные строки берут высоту как нижнюю границу.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<int>? ExactHeightRows { get; set; }

        /// <summary>У строки точная высота, а не «не менее».</summary>
        public bool IsRowHeightExact(int row)
            => ExactHeightRows is not null && ExactHeightRows.Contains(row);

        /// <summary>Отметить строку точной высоты или снять отметку.</summary>
        public void SetRowHeightExact(int row, bool exact)
        {
            if (row < 0) return;

            if (exact)
            {
                ExactHeightRows ??= new List<int>();
                if (!ExactHeightRows.Contains(row)) ExactHeightRows.Add(row);
                return;
            }

            if (ExactHeightRows is null) return;
            ExactHeightRows.Remove(row);
            if (ExactHeightRows.Count == 0) ExactHeightRows = null;
        }

        /// <summary>
        /// Подвинуть высоты при вставке строки. Без этого заданные высоты остались бы
        /// на прежних индексах и «переехали» бы на соседние строки.
        /// </summary>
        public void InsertRowMinHeight(int row)
        {
            ShiftExactHeightRows(row, +1);
            if (row < 0 || row > RowMinHeightsPt.Count) return;
            RowMinHeightsPt.Insert(row, 0);
        }

        /// <summary>Подвинуть высоты при удалении строки.</summary>
        public void RemoveRowMinHeight(int row)
        {
            if (row >= 0) SetRowHeightExact(row, false);
            ShiftExactHeightRows(row + 1, -1);
            if (row < 0 || row >= RowMinHeightsPt.Count) return;
            RowMinHeightsPt.RemoveAt(row);
        }

        /// <summary>Отметки точной высоты у строк начиная с from сдвигаются на delta.</summary>
        private void ShiftExactHeightRows(int from, int delta)
        {
            if (ExactHeightRows is null || from < 0) return;
            for (int i = 0; i < ExactHeightRows.Count; i++)
                if (ExactHeightRows[i] >= from) ExactHeightRows[i] += delta;
        }

        /// <summary>
        /// Все ячейки таблицы в порядке строк.
        /// При объединении ячеек в списке присутствует только "главная" ячейка.
        /// </summary>
        public List<TableCell> Cells { get; set; } = new();

        /// <summary>Таблицы, вложенные в ячейки этой таблицы, на всю глубину.</summary>
        public IEnumerable<TableBlock> NestedTablesDeep()
        {
            foreach (var cell in Cells)
            {
                if (cell.NestedTables is not { Count: > 0 } nestedTables) continue;

                foreach (var nested in nestedTables)
                {
                    yield return nested.Table;
                    foreach (var deeper in nested.Table.NestedTablesDeep())
                        yield return deeper;
                }
            }
        }

        /// <summary>
        /// Выдаёт новые идентификаторы таблице, её ячейкам, абзацам и всем вложенным
        /// таблицам. Нужна копии: копия, вставленная рядом с оригиналом, не должна
        /// делить с ним идентификаторы — по ним блоки ищут отмена и сохранение.
        /// Привязка вложенных таблиц к абзацам при этом сохраняется.
        /// </summary>
        public void RenewIds()
        {
            Id = Guid.NewGuid();

            foreach (var cell in Cells)
            {
                cell.AnchorNestedTables();
                cell.Id = Guid.NewGuid();

                foreach (var paragraph in cell.Paragraphs)
                    paragraph.Id = Guid.NewGuid();

                if (cell.NestedTables is not { Count: > 0 } nestedTables) continue;

                foreach (var nested in nestedTables)
                {
                    nested.BeforeParagraphId = nested.BeforeParagraphIndex < cell.Paragraphs.Count
                        ? cell.Paragraphs[nested.BeforeParagraphIndex].Id
                        : Guid.Empty;
                    nested.Table.RenewIds();
                }
            }
        }

        /// <summary>
        /// Ячейка, в которой стоит вложенная таблица, и запись о ней. Ищет на всю глубину.
        /// Null — таблица в эту не вложена.
        /// </summary>
        public (TableCell Cell, NestedTable Nested)? FindNestedOwner(TableBlock table)
        {
            foreach (var cell in Cells)
            {
                if (cell.NestedTables is not { Count: > 0 } nestedTables) continue;

                foreach (var nested in nestedTables)
                {
                    if (ReferenceEquals(nested.Table, table) || nested.Table.Id == table.Id)
                        return (cell, nested);

                    var deeper = nested.Table.FindNestedOwner(table);
                    if (deeper is not null) return deeper;
                }
            }

            return null;
        }

        /// <summary>Имя готового стиля таблицы. Null — кастомное оформление.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? StyleName { get; set; }

        /// <summary>Ширина таблицы в процентах от текстовой области (100 = во всю ширину).</summary>
        public double WidthPercent { get; set; } = 100;

        /// <summary>
        /// Отступ таблицы слева от начала текстовой области в пунктах.
        /// Сдвигает всю таблицу горизонтально. Управляется через левый маркер линейки в режиме таблицы.
        /// </summary>
        public double LeftIndentPt { get; set; } = 0;

        /// <summary>
        /// Выравнивание таблицы в текстовой области. По центру и справа таблица
        /// встаёт по ширине текстовой области, и LeftIndentPt не действует — как у Word,
        /// где w:tblInd при w:jc center/right не учитывается.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public TableBlockAlignment Alignment { get; set; } = TableBlockAlignment.Left;

        /// <summary>
        /// Таблица «справа налево» (w:bidiVisual у Word, «Направление таблицы» в её
        /// свойствах): первая колонка стоит справа. Колонки в модели хранятся уже в том
        /// порядке, в каком видны на листе, — так раскладка, попадание мышью и правка
        /// работают как у обычной таблицы. Флаг нужен выгрузке: она возвращает Word
        /// логический порядок колонок.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool BidiVisual { get; set; }

        /// <summary>
        /// Зеркально переставляет колонки: первая становится последней. Ячейки меняют
        /// столбец, левые и правые границы и поля меняются местами, ширины колонок
        /// идут в обратном порядке. Строки и высоты не меняются.
        /// </summary>
        public void MirrorColumns()
        {
            int columns = ColumnCount;
            if (columns <= 0) return;

            foreach (var cell in Cells)
            {
                cell.Column = Math.Max(0, columns - (cell.Column + cell.ColSpan));
                (cell.PaddingLeftPt, cell.PaddingRightPt) = (cell.PaddingRightPt, cell.PaddingLeftPt);
                cell.Borders.SwapLeftRight();
            }

            int definedColumns = Math.Min(Columns.Count, columns);
            Columns.Reverse(0, definedColumns);

            // Ячейки хранятся в порядке строк, внутри строки — слева направо.
            Cells.Sort((a, b) => a.Row != b.Row
                ? a.Row.CompareTo(b.Row)
                : a.Column.CompareTo(b.Column));
        }

        /// <summary>
        /// Сдвиг левого края таблицы от начала текстовой области с учётом выравнивания.
        /// </summary>
        /// <param name="textWidthPt">Ширина текстовой области в пунктах.</param>
        /// <param name="tableWidthPt">Фактическая ширина таблицы в пунктах.</param>
        public double ResolveLeftOffsetPt(double textWidthPt, double tableWidthPt)
        {
            return Alignment switch
            {
                TableBlockAlignment.Center => (textWidthPt - tableWidthPt) / 2.0,
                TableBlockAlignment.Right => textWidthPt - tableWidthPt,
                _ => LeftIndentPt
            };
        }

        /// <summary>
        /// Положение таблицы с обтеканием текстом. null — обычная таблица в потоке:
        /// занимает свою строку, текст идёт над ней и под ней.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public TableFloatPosition? FloatPosition { get; set; }

        /// <summary>
        /// Повторять первую строку как заголовок на каждой странице.
        /// </summary>
        public bool RepeatHeader { get; set; } = false;

        /// <summary>Режим разбивки таблицы по страницам.</summary>
        public TableSplitMode SplitMode { get; set; } = TableSplitMode.ByRow;

        /// <summary>Текст после таблицы перед разрывом страницы. Null = не рисовать.</summary>
        public string? BreakLabel { get; set; }

        /// <summary>Текст перед продолжением таблицы на следующей странице. Null = не рисовать.</summary>
        public string? ContinuationLabel { get; set; }

        /// <summary>
        /// Возвращает ячейку по индексу строки и столбца.
        /// Учитывает объединённые ячейки (возвращает главную ячейку).
        /// </summary>
        public TableCell? GetCell(int row, int column)
        {
            foreach (var cell in Cells)
            {
                if (row >= cell.Row && row < cell.Row + cell.RowSpan &&
                    column >= cell.Column && column < cell.Column + cell.ColSpan)
                    return cell;
            }
            return null;
        }
    }

    /// <summary>Режим разбивки таблицы по страницам.</summary>
    public enum TableSplitMode
    {
        ByRow = 0,
        ByCell = 1
    }
}