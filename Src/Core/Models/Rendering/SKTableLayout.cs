using System;
using System.Collections.Generic;

namespace Writersword.Core.Models.Rendering
{
    /// <summary>
    /// Параметры одной линии границы ячейки для рендеринга.
    /// </summary>
    public sealed class SKTableBorderLineLayout
    {
        /// <summary>
        /// Толщина линии в pt — толщина её основной черты. 0 — не рисовать.
        /// Составная линия шире своей черты, см. <see cref="SpanPt"/>.
        /// </summary>
        public float WidthPt { get; init; } = 0.75f;

        /// <summary>Цвет линии в формате #RRGGBB.</summary>
        public string Color { get; init; } = "#000000";

        /// <summary>
        /// Стиль линии: 0 — сплошная, 1 — штрих, 2 — двойная, 3 — нет, 4 — точки,
        /// 5 — тройная, 6 — волна, 7 — объёмная выпуклая, 8 — объёмная вдавленная,
        /// 9 — выпуклая (outset), 10 — вдавленная (inset); дальше — штрихпунктирные,
        /// «тонкая и толстая» и двойная волна. Полный список — <see cref="SKBorderLineShape"/>.
        /// </summary>
        public int Style { get; init; }

        /// <summary>
        /// Сколько места линия занимает поперёк себя в pt: у двойной — три толщины,
        /// у тройной — пять, у волны — её размах. По этому числу строка таблицы
        /// отводит место под рамку, а текст отступает от края ячейки.
        /// </summary>
        public float SpanPt => SKBorderLineShape.SpanPt(Style, WidthPt);

        /// <summary>
        /// Сила линии: старшинство её вида на толщину. По ней решается стык двух линий
        /// в узле сетки — более слабая упирается в более сильную и не пересекает её.
        /// </summary>
        public float Weight { get; init; }
    }

    /// <summary>
    /// Настройки всех четырёх границ ячейки для рендеринга.
    /// </summary>
    public sealed class SKTableCellBorderLayout
    {
        /// <summary>Верхняя граница.</summary>
        public SKTableBorderLineLayout Top { get; init; } = new();

        /// <summary>Нижняя граница.</summary>
        public SKTableBorderLineLayout Bottom { get; init; } = new();

        /// <summary>Левая граница.</summary>
        public SKTableBorderLineLayout Left { get; init; } = new();

        /// <summary>Правая граница.</summary>
        public SKTableBorderLineLayout Right { get; init; } = new();
    }

    /// <summary>
    /// Лейаут одного параграфа внутри ячейки таблицы.
    /// </summary>
    public sealed class SKTableParaLayout
    {
        /// <summary>Результат вёрстки параграфа.</summary>
        public SKTextLayout Layout { get; init; } = null!;

        /// <summary>
        /// Y-позиция верхнего края параграфа в pt
        /// относительно начала текстовой области ячейки (после PadTopPt).
        /// Включает SpaceBeforePt параграфа.
        /// </summary>
        public float Ypt { get; init; }

        /// <summary>Индекс параграфа в Paragraphs ячейки (0-based).</summary>
        public int ParagraphIndex { get; init; }
    }

    /// <summary>
    /// Вёрстка таблицы, вложенной в ячейку. Стоит в содержимом ячейки между абзацами
    /// и занимает там свою высоту, как абзац.
    /// </summary>
    public sealed class SKNestedTableLayout
    {
        /// <summary>Вёрстка самой таблицы.</summary>
        public SKTableLayout Layout { get; init; } = null!;

        /// <summary>X левого края таблицы в pt от левого края области содержимого ячейки.</summary>
        public float Xpt { get; init; }

        /// <summary>
        /// Y верха таблицы в pt от начала области содержимого ячейки — в тех же
        /// координатах, что <see cref="SKTableParaLayout.Ypt"/>.
        /// </summary>
        public float Ypt { get; init; }

        /// <summary>
        /// Перед каким по счёту абзацем ячейки стоит таблица. Число, равное количеству
        /// абзацев, — после последнего.
        /// </summary>
        public int BeforeParagraphIndex { get; init; }

        /// <summary>Номер таблицы в списке вложенных таблиц ячейки модели.</summary>
        public int SourceIndex { get; init; }
    }

    /// <summary>
    /// Результат вёрстки одной ячейки таблицы.
    /// Координаты в pt относительно верхнего левого угла таблицы.
    /// </summary>
    public sealed class SKTableCellLayout
    {
        /// <summary>Индекс строки (0-based).</summary>
        public int Row { get; init; }

        /// <summary>Индекс колонки (0-based).</summary>
        public int Column { get; init; }

        /// <summary>Количество объединённых строк.</summary>
        public int RowSpan { get; init; } = 1;

        /// <summary>Количество объединённых колонок.</summary>
        public int ColSpan { get; init; } = 1;

        /// <summary>X-позиция левого края ячейки в pt относительно таблицы.</summary>
        public float Xpt { get; init; }

        /// <summary>Y-позиция верхнего края ячейки в pt относительно таблицы.</summary>
        public float Ypt { get; init; }

        /// <summary>Ширина ячейки в pt.</summary>
        public float WidthPt { get; init; }

        /// <summary>Высота ячейки в pt — определяется содержимым с учётом отступов.</summary>
        public float HeightPt { get; set; }

        /// <summary>Внутренний отступ сверху в pt.</summary>
        public float PadTopPt { get; init; }

        /// <summary>Внутренний отступ снизу в pt.</summary>
        public float PadBottomPt { get; init; }

        /// <summary>Внутренний отступ слева в pt.</summary>
        public float PadLeftPt { get; init; }

        /// <summary>Внутренний отступ справа в pt.</summary>
        public float PadRightPt { get; init; }

        /// <summary>Цвет фона ячейки. Null — нет заливки.</summary>
        public string? BackgroundColor { get; init; }

        /// <summary>Узор заливки поверх фона (имя w:shd w:val). Null — узора нет.</summary>
        public string? ShadingPattern { get; init; }

        /// <summary>Цвет узора заливки. Null — «авто».</summary>
        public string? ShadingPatternColor { get; init; }

        /// <summary>Вертикальное выравнивание содержимого (0=Top, 1=Middle, 2=Bottom).</summary>
        public int VerticalAlignment { get; init; }

        /// <summary>
        /// Направление текста: 0 — горизонтально, 1 — снизу вверх (btLr),
        /// 2 — сверху вниз (tbRl). У повёрнутой ячейки раскладки абзацев построены
        /// на длину строки, равную высоте области содержимого, а ContentHeightPt —
        /// толщина стопки строк поперёк ячейки, то есть по её ширине.
        /// </summary>
        public int TextDirection { get; init; }

        /// <summary>Текст ячейки повёрнут.</summary>
        public bool IsRotated => TextDirection != 0;

        /// <summary>
        /// Границы ячейки для вёрстки: самая сильная линия на каждой из четырёх сторон
        /// после спора с соседями. По ним текст отступает от края ячейки. Рисуются
        /// границы не отсюда, а из общей сетки таблицы (SKTableLayout.HorizontalEdges,
        /// VerticalEdges): вдоль стороны объединённой ячейки линии бывают разные.
        /// </summary>
        public SKTableCellBorderLayout Borders { get; init; } = new();

        /// <summary>
        /// Отступ содержимого от левого края ячейки. Как у Word: поле ячейки отсчитывается
        /// от самого края ячейки, а рамка рисуется по этому краю и лежит поверх поля —
        /// ширину текста она не отнимает. Обычная рамка в полпункта при поле в полпункта
        /// текста не сдвигает вовсе. Только рамка шире двух полей отодвигает текст:
        /// внутри ячейки лежит её половина, и текст встаёт сразу за ней. Ширина рамки
        /// здесь — всё место поперёк линии: у двойной это обе черты с просветом.
        /// </summary>
        public float ContentInsetLeftPt => Math.Max(PadLeftPt, Borders.Left.SpanPt / 2f);

        /// <summary>
        /// Место под рамку у верхнего края ячейки в pt: на столько содержимое отступает
        /// от верха ячейки сверх своего поля. Рамка между двумя строками занимает место
        /// один раз — в строке под ней, как у Word. Полоса под рамку общая на всю
        /// строку: её высота — самая широкая из линий, лежащих по верхней границе
        /// строки, — верхних у ячеек этой строки и нижних у ячеек строки над ней.
        /// Поэтому текст всех ячеек строки начинается на одной высоте, какой бы ни была
        /// рамка у каждой. Ставится вёрсткой таблицы.
        /// </summary>
        public float TopInsetPt { get; init; }

        /// <summary>
        /// Место под рамку у нижнего края ячейки в pt. Ноль у всех ячеек, кроме дошедших
        /// до последней строки таблицы: рамке между строками место отдаёт строка под ней.
        /// У последней строки полоса тоже общая — по самой широкой нижней линии.
        /// </summary>
        public float BottomInsetPt { get; init; }

        /// <summary>Отступ содержимого от правого края ячейки — см. <see cref="ContentInsetLeftPt"/>.</summary>
        public float ContentInsetRightPt => Math.Max(PadRightPt, Borders.Right.SpanPt / 2f);

        /// <summary>Ширина области содержимого ячейки: ширина ячейки без отступов слева и справа.</summary>
        public float ContentAreaWidthPt => WidthPt - ContentInsetLeftPt - ContentInsetRightPt;

        /// <summary>
        /// Суммарная высота содержимого ячейки в pt (без отступов).
        /// Вычисляется при вёрстке.
        /// </summary>
        public float ContentHeightPt { get; set; }

        /// <summary>Лейауты параграфов содержимого ячейки в порядке следования.</summary>
        public List<SKTableParaLayout> Paragraphs { get; } = new();

        /// <summary>
        /// Таблицы внутри ячейки в порядке следования. Их высота входит в
        /// <see cref="ContentHeightPt"/>, абзацы под ними стоят ниже на эту высоту.
        /// </summary>
        public List<SKNestedTableLayout> NestedTables { get; } = new();
    }

    /// <summary>
    /// Результат вёрстки одной строки таблицы.
    /// </summary>
    public sealed class SKTableRowLayout
    {
        /// <summary>Индекс строки (0-based).</summary>
        public int Row { get; init; }

        /// <summary>Y-позиция верхнего края строки в pt относительно таблицы.</summary>
        public float Ypt { get; init; }

        /// <summary>Высота строки в pt — определяется самой высокой ячейкой.</summary>
        public float HeightPt { get; set; }

        /// <summary>Ячейки строки в порядке следования слева направо.</summary>
        public List<SKTableCellLayout> Cells { get; } = new();
    }

    /// <summary>
    /// Результат вёрстки всей таблицы.
    /// Координаты в pt относительно верхнего левого угла таблицы.
    /// Используется DocumentCanvas для рендеринга и линейкой для маркеров колонок.
    /// </summary>
    public sealed class SKTableLayout
    {
        /// <summary>Строки таблицы в порядке следования сверху вниз.</summary>
        public List<SKTableRowLayout> Rows { get; } = new();

        /// <summary>
        /// Ширины колонок в pt в порядке слева направо.
        /// Используется линейкой для отображения маркеров колонок.
        /// </summary>
        public List<float> ColumnWidthsPt { get; } = new();

        /// <summary>
        /// X-позиции левых краёв колонок в pt относительно таблицы.
        /// Длина всегда равна ColumnWidthsPt.Count.
        /// </summary>
        public List<float> ColumnOffsetsPt { get; } = new();

        /// <summary>Суммарная ширина таблицы в pt.</summary>
        public float TotalWidthPt { get; set; }

        /// <summary>Суммарная высота таблицы в pt.</summary>
        public float TotalHeightPt { get; set; }

        /// <summary>Количество строк.</summary>
        public int RowCount { get; init; }

        /// <summary>Количество колонок.</summary>
        public int ColumnCount { get; init; }

        /// <summary>
        /// Горизонтальные границы по сетке таблицы: [граница строк, колонка]. Граница
        /// строк 0 — верх таблицы, RowCount — её низ. У Word граница между двумя ячейками
        /// одна: из линий, заданных ячейками по обе стороны, здесь оставлена сильнейшая.
        /// Null — линии нет (в том числе внутри объединённой ячейки).
        /// </summary>
        public SKTableBorderLineLayout?[,]? HorizontalEdges { get; set; }

        /// <summary>
        /// Вертикальные границы по сетке таблицы: [строка, граница колонок]. Граница
        /// колонок 0 — левый край таблицы, ColumnCount — правый.
        /// </summary>
        public SKTableBorderLineLayout?[,]? VerticalEdges { get; set; }

        /// <summary>Занятые клетки сетки: [строка, колонка]. В пустой клетке ячейки нет.</summary>
        public bool[,]? SlotFilled { get; set; }

        /// <summary>
        /// Находит лейаут ячейки по индексам строки и колонки.
        /// Учитывает объединённые ячейки.
        /// Возвращает null если позиция вне таблицы.
        /// </summary>
        public SKTableCellLayout? FindCell(int row, int col)
        {
            foreach (var tableRow in Rows)
                foreach (var cell in tableRow.Cells)
                    if (row >= cell.Row && row < cell.Row + cell.RowSpan
                        && col >= cell.Column && col < cell.Column + cell.ColSpan)
                        return cell;
            return null;
        }

        /// <summary>
        /// Находит лейаут ячейки по точке клика в pt относительно таблицы.
        /// Возвращает null если точка вне таблицы.
        /// </summary>
        public SKTableCellLayout? HitTestCell(float xPt, float yPt)
        {
            foreach (var row in Rows)
                foreach (var cell in row.Cells)
                    if (xPt >= cell.Xpt && xPt <= cell.Xpt + cell.WidthPt
                        && yPt >= cell.Ypt && yPt <= cell.Ypt + cell.HeightPt)
                        return cell;
            return null;
        }

        /// <summary>
        /// Находит параграф внутри ячейки по точке клика в pt относительно таблицы.
        /// Возвращает null если точка вне любой ячейки.
        /// Используется DocumentCanvas для установки каретки по клику мыши.
        /// </summary>
        public (SKTableCellLayout Cell, SKTableParaLayout Para)? HitTestParagraph(
            float xPt, float yPt)
        {
            var cell = HitTestCell(xPt, yPt);
            if (cell is null) return null;

            float localY = yPt - cell.Ypt - cell.PadTopPt;

            SKTableParaLayout? bestPara = null;
            float bestDist = float.MaxValue;

            foreach (var para in cell.Paragraphs)
            {
                float paraBottom = para.Ypt + para.Layout.BlockHeightPt;
                float dist = localY < para.Ypt
                    ? para.Ypt - localY
                    : localY > paraBottom
                        ? localY - paraBottom
                        : 0f;

                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestPara = para;
                    if (dist == 0f) break;
                }
            }

            if (bestPara is null) return null;
            return (cell, bestPara);
        }

        /// <summary>
        /// Возвращает индекс колонки по X-позиции в pt относительно таблицы.
        /// Используется линейкой при drag маркера колонки.
        /// Возвращает -1 если позиция вне таблицы.
        /// </summary>
        public int HitTestColumn(float xPt)
        {
            for (int i = 0; i < ColumnOffsetsPt.Count; i++)
            {
                float left = ColumnOffsetsPt[i];
                float right = left + ColumnWidthsPt[i];
                if (xPt >= left && xPt <= right)
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// Возвращает общую высоту таблицы с учётом всех строк.
        /// Используется DocumentCanvas для разбивки таблицы по страницам.
        /// </summary>
        public float GetTotalHeightPt()
        {
            float h = 0f;
            foreach (var row in Rows)
                h += row.HeightPt;
            return h;
        }
    }
}