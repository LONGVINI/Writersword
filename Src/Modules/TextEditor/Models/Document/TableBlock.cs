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
        Inset = 11
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
    /// Одна ячейка таблицы.
    /// Содержит список параграфов (как и обычный поток документа).
    /// </summary>
    public sealed class TableCell
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>Содержимое ячейки — список параграфов.</summary>
        public List<ParagraphBlock> Paragraphs { get; set; } = new() { new ParagraphBlock() };

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