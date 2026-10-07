using System.Collections.Generic;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Styles;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Таблицы с обтеканием текстом (w:tblpPr у Word).
    ///
    /// Обычная таблица занимает в потоке свою строку: текст идёт над ней и под ней.
    /// Таблица с обтеканием стоит в своей точке листа — от текста, от полей или от
    /// краёв листа — и высоты в потоке не занимает, а строки абзацев под ней обходят её
    /// с обеих сторон, как плавающую картинку.
    ///
    /// Обтекание написано один раз для плавающих объектов (FloatEntry, ComputeWrapZones),
    /// поэтому таблица попадает в него записью того же вида: её габарит на листе и
    /// расстояния до текста. Ячейки при этом раскладываются как у обычной таблицы —
    /// каретка, выделение и правка в них работают без особых случаев.
    ///
    /// Таблицу обходят абзацы, стоящие в документе после неё: она привязана к абзацу
    /// под собой, и его строки первыми её и встречают. По страницам такая таблица не
    /// делится — она целиком стоит на листе своего абзаца.
    ///
    /// В ленте чтения, в развороте книги и в печатной раскладке листы условные, и
    /// таблица с обтеканием идёт там в потоке, как обычная.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        /// <summary>
        /// Таблица глазами обтекания: всё, что раскладка спрашивает у плавающего
        /// объекта, чтобы построить вокруг него зону. Таблица не поворачивается, не
        /// обрезается и рамки поверх габарита не имеет — значимы только режим
        /// обтекания и расстояния до текста.
        /// </summary>
        private sealed class TableWrapProxy : IFloatingObject
        {
            public TableWrapProxy(TableFloatPosition position)
            {
                WrapPadLeftPt = position.LeftFromTextPt;
                WrapPadRightPt = position.RightFromTextPt;
                WrapPadTopPt = position.TopFromTextPt;
                WrapPadBottomPt = position.BottomFromTextPt;
            }

            public WrapMode WrapMode { get; set; } = WrapMode.Square;
            public WrapSide WrapSide { get; set; } = WrapSide.BothSides;
            public double RotationDeg { get; set; }
            public double WidthPt { get; set; }
            public double HeightPt { get; set; }
            public bool LockAspectRatio { get; set; }
            public double Opacity { get; set; } = 1.0;
            public ShapeType ShapeType { get; set; } = ShapeType.Rectangle;
            public double CornerRadiusPt { get; set; }
            public bool IsClosedShape => true;
            public string? OutlineColor { get; set; }
            public double OutlineThicknessPt { get; set; }
            public ShapeDashStyle OutlineDash { get; set; } = ShapeDashStyle.Solid;
            public ImageBorderAlign OutlineAlign { get; set; } = ImageBorderAlign.Inside;
            public bool FlipHorizontal { get; set; }
            public bool FlipVertical { get; set; }
            public double CropLeftFrac { get; set; }
            public double CropTopFrac { get; set; }
            public double CropRightFrac { get; set; }
            public double CropBottomFrac { get; set; }
            public string? AltText { get; set; }
            public TextAlignment Alignment { get; set; } = TextAlignment.Left;
            public FloatAnchor Anchor { get; set; } = FloatAnchor.Paragraph;
            public double WrapPadTopPt { get; set; }
            public double WrapPadBottomPt { get; set; }
            public double WrapPadLeftPt { get; set; }
            public double WrapPadRightPt { get; set; }
            public double WrapOutsetPt => 0.0;
            public int PinnedPage { get; set; }
            public double OffsetXPt { get; set; }
            public double OffsetYPt { get; set; }
            public int ZOrder { get; set; }
            public WordDrawingInfo? WordDrawing { get; set; }
        }

        // ── Команды ленты: обтекание, направление таблицы, узор заливки ячеек ─────

        /// <summary>
        /// Ставит таблице под кареткой положение с обтеканием или возвращает её в поток
        /// (null). Один шаг отмены.
        /// </summary>
        private void ExecuteTableSetFloatPosition(TableFloatPosition? position)
        {
            var table = _activeTableBlock;
            if (table is null) return;

            BeginTableEdit(table, "Set table wrapping");
            table.FloatPosition = position?.Clone();
            CommitTableEdit();

            RebuildLayouts();
            NotifyCaretEnteredTableCallback();
            InvalidateFull();
        }

        /// <summary>
        /// Меняет направление таблицы под кареткой: колонки переставляются зеркально, и
        /// первая встаёт у другого края. Выравнивание таблицы зеркалится вместе с ними:
        /// таблица справа налево стоит у правого поля, как у Word.
        /// </summary>
        private void ExecuteTableToggleDirection()
        {
            var table = _activeTableBlock;
            if (table is null) return;

            // Ячейка под кареткой после перестановки окажется в другом столбце.
            var activeCell = table.GetCell(_activeCellRow, _activeCellCol);

            BeginTableEdit(table, "Toggle table direction");
            table.BidiVisual = !table.BidiVisual;
            table.MirrorColumns();
            table.Alignment = table.Alignment switch
            {
                TableBlockAlignment.Left => TableBlockAlignment.Right,
                TableBlockAlignment.Right => TableBlockAlignment.Left,
                _ => TableBlockAlignment.Center
            };
            CommitTableEdit();

            if (activeCell is not null) _activeCellCol = activeCell.Column;

            // Выделение ячеек записано номерами столбцов — после перестановки оно
            // показывало бы на другие ячейки.
            _tableSelections.Remove(table);

            InvalidateCellLayoutCaches();
            RebuildLayouts();
            NotifyCaretEnteredTableCallback();
            InvalidateFull();
        }

        /// <summary>
        /// Узор заливки выделенных ячеек (имя узора Word: pct25, diagStripe…) и его цвет.
        /// Узор ложится поверх цвета фона ячейки; null снимает узор, фон остаётся.
        /// </summary>
        private void ExecuteTableSetCellShadingPattern(string? pattern, string? patternColor)
        {
            var targets = CollectTargetCells(out var touchedTables);
            if (targets.Count == 0) return;

            string? value = string.IsNullOrWhiteSpace(pattern) ? null : pattern;
            string? color = value is null || string.IsNullOrWhiteSpace(patternColor) ? null : patternColor;

            bool singleTable = touchedTables.Count == 1;
            if (singleTable) BeginTableEdit(System.Linq.Enumerable.First(touchedTables), "Set cell shading pattern");
            else BeginEdit("Set cell shading pattern");

            foreach (var cell in targets)
            {
                cell.ShadingPattern = value;
                cell.ShadingPatternColor = color;
            }

            if (singleTable) CommitTableEdit();
            else CommitEdit();

            InvalidateCellLayoutCaches();
            RebuildLayouts();
            InvalidateFull();
        }

        /// <summary>
        /// Выравнивание таблицы под кареткой в полосе набора: слева (со своим отступом),
        /// по центру или справа.
        /// </summary>
        private void ExecuteTableSetAlignment(TableBlockAlignment alignment)
        {
            var table = _activeTableBlock;
            if (table is null) return;

            BeginTableEdit(table, "Set table alignment");
            table.Alignment = alignment;

            // У таблицы с обтеканием выравнивание — её положение от опоры по горизонтали.
            if (table.FloatPosition is { } position)
            {
                position.HorizontalAlign = alignment switch
                {
                    TableBlockAlignment.Center => TableFloatAlign.Center,
                    TableBlockAlignment.Right => TableFloatAlign.End,
                    _ => TableFloatAlign.Start
                };
            }
            CommitTableEdit();

            RebuildLayouts();
            NotifyCaretEnteredTableCallback();
            InvalidateFull();
        }

        /// <summary>
        /// Направление текста выделенных ячеек: обычное, снизу вверх или сверху вниз.
        /// </summary>
        private void ExecuteTableSetCellTextDirection(CellTextDirection direction)
        {
            var targets = CollectTargetCells(out var touchedTables);
            if (targets.Count == 0) return;

            bool singleTable = touchedTables.Count == 1;
            if (singleTable) BeginTableEdit(System.Linq.Enumerable.First(touchedTables), "Set cell text direction");
            else BeginEdit("Set cell text direction");

            foreach (var cell in targets)
                cell.TextDirection = direction;

            if (singleTable) CommitTableEdit();
            else CommitEdit();

            InvalidateCellLayoutCaches();
            RebuildLayouts();
            InvalidateFull();
        }

        /// <summary>Направление текста ячейки под кареткой.</summary>
        private CellTextDirection QueryTableCellTextDirection()
            => _activeTableBlock?.GetCell(_activeCellRow, _activeCellCol)?.TextDirection
               ?? CellTextDirection.Horizontal;

        /// <summary>
        /// Точная высота строки под кареткой: строка ровно заданной высоты, а текст, который
        /// в неё не входит, срезается — как «Точно» в свойствах строки Word. Строке без
        /// заданной высоты записывается та, что у неё сейчас на листе. Повторное нажатие
        /// возвращает высоту «не менее».
        /// </summary>
        private void ExecuteTableToggleRowHeightExact()
        {
            var table = _activeTableBlock;
            if (table is null || _activeCellRow < 0 || _activeCellRow >= table.RowCount) return;

            bool exact = !table.IsRowHeightExact(_activeCellRow);

            BeginTableEdit(table, "Toggle exact row height");
            if (exact && table.GetRowMinHeightPt(_activeCellRow) <= 0)
            {
                float currentPt = 14f;
                if (_activeCellTableEntryIdx >= 0 && _activeCellTableEntryIdx < _tables.Count)
                {
                    var rows = _tables[_activeCellTableEntryIdx].Layout.Rows;
                    if (_activeCellRow < rows.Count) currentPt = rows[_activeCellRow].HeightPt;
                }

                // Запись высоты снимает отметку «точно» — отметка ставится после неё.
                table.SetRowMinHeightPt(_activeCellRow, currentPt);
            }
            table.SetRowHeightExact(_activeCellRow, exact);
            CommitTableEdit();

            InvalidateCellLayoutCaches();
            RebuildLayouts();
            InvalidateFull();
        }

        /// <summary>У строки под кареткой точная высота.</summary>
        private bool QueryTableRowHeightExact()
            => _activeTableBlock is not null && _activeTableBlock.IsRowHeightExact(_activeCellRow);

        // Начало перетаскивания таблицы с обтеканием: смещение по вертикали на момент
        // нажатия и точка указателя. По горизонтали работает общее поле
        // _tableDragStartVal — оно хранит смещение от опоры.
        private float _tableDragStartFloatYPt;
        private float _tableDragStartPointerYPt;

        /// <summary>
        /// Готовит перетаскивание таблицы с обтеканием: запоминает её смещения от опор,
        /// от которых указатель поведёт таблицу. Обычную таблицу не трогает.
        /// </summary>
        private void BeginFloatingTableDrag(TableEntry te, float pointerYPt)
        {
            if (te.Table.FloatPosition is not { } position) return;
            if (te.PageIndex < 0 || te.PageIndex >= _pages.Count) return;

            var page = _pages[te.PageIndex];

            // Край листа у показанной страницы стоит без сдвига переплёта, а таблица — со
            // сдвигом: смещение от листа считается от сдвинутого края, иначе на листе с
            // переплётом справа таблица при перетаскивании прыгала бы на его ширину.
            float baseXPt = position.HorizontalAnchor == TableFloatAnchor.Page
                ? page.PadLeftPt + page.GutterShiftPt
                : page.PadLeftPt + page.MarginLeftPt;
            _tableDragStartVal = te.XPt - baseXPt;

            _tableDragStartFloatYPt = position.VerticalAnchor switch
            {
                TableFloatAnchor.Page => te.Ypt - page.Ypt,
                TableFloatAnchor.Margin => te.Ypt - (page.Ypt + page.PadTopPt),
                _ => (float)position.YPt
            };
            _tableDragStartPointerYPt = pointerYPt;
        }

        /// <summary>Узор заливки ячейки под кареткой и его цвет; null — узора нет.</summary>
        private (string? Pattern, string? Color) QueryTableCellShadingPattern()
        {
            var cell = _activeTableBlock?.GetCell(_activeCellRow, _activeCellCol);
            return (cell?.ShadingPattern, cell?.ShadingPatternColor);
        }

        /// <summary>Таблица стоит в своей точке листа и обтекается текстом.</summary>
        private static bool IsFloatingTable(BlockModel? block)
            => block is TableBlock { FloatPosition: not null };

        /// <summary>
        /// Левый верхний угол таблицы с обтеканием на листе.
        /// </summary>
        /// <param name="anchorYPt">Верх абзаца, к которому привязана таблица: место в потоке, где она встречена.</param>
        private static (float XPt, float YPt) FloatingTableOrigin(
            TableFloatPosition position, float tableWidthPt, float tableHeightPt,
            float textXPt, float textWidthPt, float pageXPt, float pageWidthPt,
            float pageYPt, float pageHeightPt, float marginTopPt, float marginBottomPt,
            float anchorYPt)
        {
            // Опора по горизонтали: лист целиком или полоса набора. Колонок в разделе
            // нет, поэтому «от текста» и «от поля» — одна и та же полоса.
            bool fromPageX = position.HorizontalAnchor == TableFloatAnchor.Page;
            float baseXPt = fromPageX ? pageXPt : textXPt;
            float spanXPt = fromPageX ? pageWidthPt : textWidthPt;

            float xPt = position.HorizontalAlign switch
            {
                TableFloatAlign.Start => baseXPt,
                TableFloatAlign.Center => baseXPt + (spanXPt - tableWidthPt) / 2f,
                TableFloatAlign.End => baseXPt + spanXPt - tableWidthPt,
                _ => baseXPt + (float)position.XPt
            };

            // Опора по вертикали. От текста таблица отсчитывается от верха своего
            // абзаца, и стороны у такой опоры нет — только смещение.
            float yPt;
            if (position.VerticalAnchor == TableFloatAnchor.Text)
            {
                yPt = anchorYPt + (float)position.YPt;
            }
            else
            {
                bool fromPageY = position.VerticalAnchor == TableFloatAnchor.Page;
                float baseYPt = fromPageY ? pageYPt : pageYPt + marginTopPt;
                float spanYPt = fromPageY
                    ? pageHeightPt
                    : pageHeightPt - marginTopPt - marginBottomPt;

                yPt = position.VerticalAlign switch
                {
                    TableFloatAlign.Start => baseYPt,
                    TableFloatAlign.Center => baseYPt + (spanYPt - tableHeightPt) / 2f,
                    TableFloatAlign.End => baseYPt + spanYPt - tableHeightPt,
                    _ => baseYPt + (float)position.YPt
                };
            }

            return (xPt, yPt);
        }

        /// <summary>
        /// Дополняет источник зон обтекания таблицами с обтеканием из списка кусков
        /// таблиц. Такая таблица по страницам не делится, и её запись одна.
        /// </summary>
        private static void AppendFloatingTables(List<FloatEntry> target, List<TableEntry> tables)
        {
            foreach (var te in tables)
            {
                if (te.Table.FloatPosition is not { } position) continue;

                target.Add(new FloatEntry(
                    new TableWrapProxy(position),
                    te.XPt, te.Ypt,
                    te.Layout.TotalWidthPt, te.Layout.GetTotalHeightPt(),
                    te.PageIndex));
            }
        }

        /// <summary>
        /// То же для одного абзаца вне прохода раскладки: в источник идут только таблицы,
        /// стоящие в документе до абзаца, — как в самом проходе, где абзац видит лишь
        /// уже встреченные таблицы. Иначе пересборка абзаца при наборе и полный проход
        /// раскладывали бы его по-разному.
        /// </summary>
        private void AppendFloatingTablesBefore(
            List<FloatEntry> target, List<TableEntry> tables, ParagraphBlock? paragraph)
        {
            if (paragraph is null) return;

            bool any = false;
            foreach (var te in tables)
            {
                if (te.Table.FloatPosition is not null) { any = true; break; }
            }
            if (!any) return;

            var sections = DocVm?.Document.Sections;
            if (sections is null || sections.Count == 0) return;

            var blocks = sections[0].Blocks;
            int paragraphIndex = blocks.IndexOf(paragraph);
            if (paragraphIndex < 0) return;

            foreach (var te in tables)
            {
                if (te.Table.FloatPosition is not { } position) continue;

                int tableIndex = blocks.IndexOf(te.Table);
                if (tableIndex < 0 || tableIndex > paragraphIndex) continue;

                target.Add(new FloatEntry(
                    new TableWrapProxy(position),
                    te.XPt, te.Ypt,
                    te.Layout.TotalWidthPt, te.Layout.GetTotalHeightPt(),
                    te.PageIndex));
            }
        }
    }
}
