using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Media;
using Avalonia.Threading;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.ViewModels;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Меню правой кнопки мыши по листу, как в Word. Что в нём, зависит от того, по
    /// чему щёлкнули:
    /// <list type="bullet">
    /// <item>текст — вырезать, копировать, вставить (с оформлением или только текст),
    /// оформление шрифта, абзац, списки, примечание, выделить всё; в таблице ещё
    /// вставка и удаление строк и столбцов, объединение и разделение ячеек;</item>
    /// <item>картинка — вырезать, копировать, вставить, обтекание, удаление; у
    /// диаграммы сверху «Изменить данные…»;</item>
    /// <item>фигура — то же, что у картинки, и порядок: на передний и задний план.</item>
    /// </list>
    ///
    /// Нажатие правой кнопкой ставит каретку или выделяет объект, как левой, но ничего
    /// не начинает тянуть: ни выделение, ни объект, ни ручки. Щелчок внутри выделенного
    /// текста или выделенных ячеек выделение не снимает — меню действует на него.
    /// Само меню открывается на отпускании кнопки.
    /// </summary>
    public partial class DocumentCanvas
    {
        // Значки пунктов меню.
        private const string MenuCutIcon = "M9.64 7.64c.23-.5.36-1.05.36-1.64 0-2.21-1.79-4-4-4S2 3.79 2 6s1.79 4 4 4c.59 0 1.14-.13 1.64-.36L10 12l-2.36 2.36C7.14 14.13 6.59 14 6 14c-2.21 0-4 1.79-4 4s1.79 4 4 4 4-1.79 4-4c0-.59-.13-1.14-.36-1.64L12 14l7 7h3v-1L9.64 7.64zM6 8c-1.1 0-2-.89-2-2s.9-2 2-2 2 .89 2 2-.9 2-2 2zm0 12c-1.1 0-2-.89-2-2s.9-2 2-2 2 .89 2 2-.9 2-2 2zm6-7.5c-.28 0-.5-.22-.5-.5s.22-.5.5-.5.5.22.5.5-.22.5-.5.5zM19 3l-6 6 2 2 7-7V3z";
        private const string MenuCopyIcon = "M16 1H4c-1.1 0-2 .9-2 2v14h2V3h12V1zm3 4H8c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h11c1.1 0 2-.9 2-2V7c0-1.1-.9-2-2-2zm0 16H8V7h11v14z";
        private const string MenuPasteIcon = "M19 2h-4.18C14.4.84 13.3 0 12 0c-1.3 0-2.4.84-2.82 2H5c-1.1 0-2 .9-2 2v16c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2V4c0-1.1-.9-2-2-2zm-7 0c.55 0 1 .45 1 1s-.45 1-1 1-1-.45-1-1 .45-1 1-1zm7 18H5V4h2v3h10V4h2v16z";
        private const string MenuPasteTextIcon = "M5 4v3h5.5v12h3V7H19V4z";
        private const string MenuCommentIcon = "M21.99 4c0-1.1-.89-2-1.99-2H4c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h14l4 4-.01-18zM18 14H6v-2h12v2zm0-3H6V9h12v2zm0-3H6V6h12v2z";
        private const string MenuSelectAllIcon = "M3 5h2V3c-1.1 0-2 .9-2 2zm0 8h2v-2H3v2zm4 8h2v-2H7v2zM3 9h2V7H3v2zm10-6h-2v2h2V3zm6 0v2h2c0-1.1-.9-2-2-2zM5 21v-2H3c0 1.1.9 2 2 2zm-2-4h2v-2H3v2zM9 3H7v2h2V3zm2 18h2v-2h-2v2zm8-8h2v-2h-2v2zm0 8c1.1 0 2-.9 2-2h-2v2zm0-12h2V7h-2v2zm0 8h2v-2h-2v2zm-4 4h2v-2h-2v2zm0-16h2V3h-2v2z";
        private const string MenuTableIcon = "M20 3H5c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h15c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2zm0 2v3H5V5h15zm-5 14h-5v-9h5v9zM5 10h3v9H5v-9zm12 9v-9h3v9h-3z";
        private const string MenuChartIcon = "M19 3H5c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2zM9 17H7v-7h2v7zm4 0h-2V7h2v10zm4 0h-2v-4h2v4z";
        private const string MenuInsertIcon = "M19 3H5c-1.11 0-2 .9-2 2v14c0 1.1.89 2 2 2h14c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2zm-2 10h-4v4h-2v-4H7v-2h4V7h2v4h4v2z";
        private const string MenuPictureIcon = "M21 19V5c0-1.1-.9-2-2-2H5c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2zM8.5 13.5l2.5 3.01L14.5 12l4.5 6H5l3.5-4.5z";
        private const string MenuTuneIcon = "M3 17v2h6v-2H3zM3 5v2h10V5H3zm10 16v-2h8v-2h-8v-2h-2v6h2zM7 9v2H3v2h4v2h2V9H7zm14 4v-2H11v2h10zm-6-4h2V7h4V5h-4V3h-2v6z";
        private const string MenuRowAboveIcon = "M3 13h18v8H3v-8zm2 2v4h4v-4H5zm6 0v4h2v-4h-2zm4 0v4h4v-4h-4zM11 2h2v3h3v2h-3v3h-2V7H8V5h3V2z";
        private const string MenuRowBelowIcon = "M3 3h18v8H3V3zm2 2v4h4V5H5zm6 0v4h2V5h-2zm4 0v4h4V5h-4zM11 14h2v3h3v2h-3v3h-2v-3H8v-2h3v-3z";
        private const string MenuColumnLeftIcon = "M13 3h8v18h-8V3zm2 2v4h4V5h-4zm0 6v2h4v-2h-4zm0 4v4h4v-4h-4zM5 8h2v3h3v2H7v3H5v-3H2v-2h3V8z";
        private const string MenuColumnRightIcon = "M3 3h8v18H3V3zm2 2v4h4V5H5zm0 6v2h4v-2H5zm0 4v4h4v-4H5zM17 8h2v3h3v2h-3v3h-2v-3h-3v-2h3V8z";
        private const string MenuRowDeleteIcon = "M3 3h18v8H3V3zm2 2v4h4V5H5zm6 0v4h2V5h-2zm4 0v4h4V5h-4zM7 16h10v2H7z";
        private const string MenuColumnDeleteIcon = "M3 3h8v18H3V3zm2 2v4h4V5H5zm0 6v2h4v-2H5zm0 4v4h4v-4H5zM14 11h8v2h-8z";
        private const string MenuMergeIcon = "M3 4h18v16H3V4zm2 2v12h14V6H5zm2 5h3V9l3 3-3 3v-2H7v-2zm10 0v2h-3v2l-3-3 3-3v2h3z";
        private const string MenuSplitIcon = "M3 4h8v16H3V4zm2 2v12h4V6H5zm8-2h8v16h-8V4zm2 2v12h4V6h-4z";
        private const string MenuRectangleIcon = "M3 5h18v14H3V5zm2 2v10h14V7H5z";
        private const string MenuEllipseIcon = "M12 5c-4.97 0-9 3.13-9 7s4.03 7 9 7 9-3.13 9-7-4.03-7-9-7zm0 12c-3.86 0-7-2.24-7-5s3.14-5 7-5 7 2.24 7 5-3.14 5-7 5z";
        private const string MenuLineIcon = "M4.4 18.2L18.2 4.4l1.4 1.4L5.8 19.6z";
        private const string MenuArrowIcon = "M5.4 19.6L4 18.2 14.6 7.6H9v-2h9v9h-2V9z";
        private const string MenuCalloutIcon = "M20 3H4c-1.1 0-2 .9-2 2v11c0 1.1.9 2 2 2h4l4 4 4-4h4c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2zm0 13h-4.83L12 19.17 8.83 16H4V5h16v11z";
        private const string MenuFormulaIcon = "M18 4H6v2l6.5 6L6 18v2h12v-3h-7l5-5-5-5h7V4z";
        private const string MenuFootnoteIcon = "M22 7h-2v1h3v1h-4V7c0-.55.45-1 1-1h2V5h-3V4h3c.55 0 1 .45 1 1v1c0 .55-.45 1-1 1zM5.88 20h2.66l3.4-5.42h.12l3.4 5.42h2.66l-4.65-7.27L17.81 6h-2.68l-3.07 4.99h-.12L8.85 6H6.19l4.32 6.73L5.88 20z";
        private const string MenuEndnoteIcon = "M22 18h-2v1h3v1h-4v-2c0-.55.45-1 1-1h2v-1h-3v-1h3c.55 0 1 .45 1 1v1c0 .55-.45 1-1 1zM5.88 18h2.66l3.4-5.42h.12l3.4 5.42h2.66l-4.65-7.27L17.81 4h-2.68l-3.07 4.99h-.12L8.85 4H6.19l4.32 6.73L5.88 18z";
        private const string MenuDateIcon = "M19 4h-1V2h-2v2H8V2H6v2H5c-1.11 0-2 .9-2 2v14c0 1.1.89 2 2 2h14c1.1 0 2-.9 2-2V6c0-1.1-.9-2-2-2zm0 16H5V9h14v11zM7 11h5v5H7z";
        private const string MenuPageBreakIcon = "M4 2h16v7H4V2zm2 2v3h12V4H6zM4 15h16v7H4v-7zm2 2v3h12v-3H6zM2 11h3v2H2zm5 0h3v2H7zm5 0h3v2h-3zm5 0h3v2h-3z";
        private const string MenuDeleteIcon = "M6 19c0 1.1.9 2 2 2h8c1.1 0 2-.9 2-2V7H6v12zM19 4h-3.5l-1-1h-5l-1 1H5v2h14V4z";
        private const string MenuWrapIcon = "M4 19h6v-2H4v2zM20 5H4v2h16V5zm-3 6H4v2h13.25c1.1 0 2 .9 2 2s-.9 2-2 2H15v-2l-3 3 3 3v-2h2c2.21 0 4-1.79 4-4s-1.79-4-4-4z";
        private const string MenuFrontIcon = "M3 13h2v-2H3v2zm0 4h2v-2H3v2zm2 4v-2H3c0 1.1.89 2 2 2zM3 9h2V7H3v2zm12 12h2v-2h-2v2zm4-18H9c-1.11 0-2 .9-2 2v10c0 1.1.89 2 2 2h10c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2zm0 12H9V5h10v10zm-8 6h2v-2h-2v2zm-4 0h2v-2H7v2z";
        private const string MenuBackIcon = "M3 5v14c0 1.1.89 2 2 2h14v-2H5V5H3zm16-4H9c-1.11 0-2 .9-2 2v10c0 1.1.89 2 2 2h10c1.1 0 2-.9 2-2V3c0-1.1-.9-2-2-2zm0 12H9V3h10v10z";

        /// <summary>
        /// Правая кнопка нажата внутри выделенного текста или выделенных ячеек: такое
        /// нажатие не трогает выделение, меню откроется на него. Иначе — false, и
        /// нажатие ставит каретку или выделяет объект обычным путём.
        /// </summary>
        private bool ContextPressKeepsSelection(Point pt, float xPt, float yPt)
        {
            if (_selectedImage is not null || _selectedShape is not null) return false;

            if (HasCellRangeSel())
            {
                var cell = HitTestTableCellGeometric(xPt, yPt);
                if (cell.HasValue && _tableSelections.TryGetValue(cell.Value.table, out var sel))
                {
                    int top = Math.Min(sel.sr, sel.er), bottom = Math.Max(sel.sr, sel.er);
                    int left = Math.Min(sel.sc, sel.ec), right = Math.Max(sel.sc, sel.ec);
                    if (cell.Value.gridRow >= top && cell.Value.gridRow <= bottom
                        && cell.Value.gridCol >= left && cell.Value.gridCol <= right)
                        return true;
                }
                return false;
            }

            if (!HasSel()) return false;

            var (pi, ci) = HitTest(pt);
            if (pi < 0) return false;
            var (sp, sc, ep, ec) = NormalizeSelection();
            bool afterStart = pi > sp || (pi == sp && ci >= sc);
            bool beforeEnd = pi < ep || (pi == ep && ci <= ec);
            return afterStart && beforeEnd;
        }

        // Меню просят двое: отпускание правой кнопки и ContextRequested (его же Avalonia
        // поднимает с клавиатуры — клавишей меню и Shift+F10). Открывается оно один раз,
        // отложенно — когда разбор нажатия закончился и выделение уже на месте.
        private bool _contextMenuPending;

        static DocumentCanvas()
        {
            ContextRequestedEvent.AddClassHandler<DocumentCanvas>(
                (canvas, e) => canvas.OnCanvasContextRequested(e), handledEventsToo: true);
        }

        private void OnCanvasContextRequested(ContextRequestedEventArgs e)
        {
            if (SpreadMode || ReadingRibbon) return;
            e.Handled = true;
            RequestContextMenu();
        }

        private void RequestContextMenu()
        {
            if (_contextMenuPending) return;
            _contextMenuPending = true;
            Dispatcher.UIThread.Post(() =>
            {
                if (!_contextMenuPending) return;
                _contextMenuPending = false;
                ShowContextMenu();
            }, DispatcherPriority.Input);
        }

        /// <summary>Открывает меню правой кнопки для того, что сейчас выделено или под кареткой.</summary>
        private void ShowContextMenu()
        {
            var doc = DocVm;
            if (doc is null) return;

            try
            {
                List<Control> items;
                if (_selectedShape is not null) items = ShapeMenuItems();
                else if (_selectedImage is { } image) items = ImageMenuItems(doc, image);
                else items = TextMenuItems(doc);

                _logger.Debug("[MENU] Меню правой кнопки: {Kind}, пунктов {Count}",
                    _selectedShape is not null ? "фигура" : _selectedImage is not null ? "картинка" : "текст", items.Count);
                if (items.Count == 0) return;
                // MenuFlyout, а не ContextMenu: отдельно открытый ContextMenu в окне
                // программы не показывался, а MenuFlyout — то же меню, что у кнопок ленты,
                // с их оформлением (Styles/Controls/Flyout.axaml).
                var menu = new MenuFlyout { ItemsSource = items, Placement = PlacementMode.Pointer };
                menu.Opened += (_, _) => _logger.Debug("[MENU] Меню открыто");
                menu.Closed += (_, _) =>
                {
                    _logger.Debug("[MENU] Меню закрыто");
                    if (!IsFocused) Focus();
                };
                menu.ShowAt(this, showAtPointer: true);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[MENU] Меню правой кнопки не открылось");
            }
        }

        // ── Текст ─────────────────────────────────────────────────────────

        private List<Control> TextMenuItems(DocumentViewModel doc)
        {
            bool locked = IsEditingBlocked;
            bool hasSelection = HasSel() || HasCellRangeSel();

            bool textByDefault = Services.PasteDefaults.TextOnly;

            // Вставка по умолчанию — то, что делает Ctrl+V; у этого пункта и стоит Ctrl+V.
            var pasteDefault = MenuEntry("Вставка по умолчанию", null, MenuTuneIcon, null, !locked,
                "Что делает Ctrl+V. Выбор запоминается и действует во всех проектах");
            pasteDefault.ItemsSource = new List<Control>
            {
                MenuChoice("С исходным оформлением", !textByDefault, "pasteDefault", () => Services.PasteDefaults.TextOnly = false,
                    "Ctrl+V вставляет как скопировано: шрифт, размер, цвет и абзацы источника"),
                MenuChoice("Только текст", textByDefault, "pasteDefault", () => Services.PasteDefaults.TextOnly = true,
                    "Ctrl+V вставляет один текст, он принимает оформление места вставки. Картинка из буфера вставляется картинкой")
            };

            var items = new List<Control>
            {
                MenuEntry("Вырезать", ExecuteCut, MenuCutIcon, "Ctrl+X", hasSelection && !locked,
                    hasSelection ? "Убрать выделенное в буфер обмена" : "Сначала выделите текст"),
                MenuEntry("Копировать", ExecuteCopy, MenuCopyIcon, "Ctrl+C", hasSelection,
                    hasSelection ? "Положить выделенное в буфер обмена" : "Сначала выделите текст"),
                new Separator(),
                MenuEntry("Вставить с оформлением", ExecutePasteWithFormatting, MenuPasteIcon, textByDefault ? null : "Ctrl+V", !locked,
                    "Как скопировано: шрифт, размер, цвет и абзацы источника"),
                MenuEntry("Вставить только текст", ExecutePasteTextOnly, MenuPasteTextIcon, textByDefault ? "Ctrl+V" : null, !locked,
                    "Без оформления источника: текст примет оформление места, куда вставлен"),
                pasteDefault,
                new Separator()
            };

            items.Add(InsertMenu(doc, !locked));

            if (_activeTableBlock is { } table && (IsInCell(_caretPara) || HasCellRangeSel()))
            {
                items.Add(new Separator());

                var insert = MenuEntry("Вставить в таблицу", null, MenuTableIcon, null, !locked, "Новые строки и столбцы рядом с ячейкой курсора");
                insert.ItemsSource = new List<Control>
                {
                    MenuEntry("Строку выше", () => ExecuteTableAddRow(above: true), MenuRowAboveIcon),
                    MenuEntry("Строку ниже", () => ExecuteTableAddRow(above: false), MenuRowBelowIcon),
                    MenuEntry("Столбец слева", () => ExecuteTableAddColumn(left: true), MenuColumnLeftIcon),
                    MenuEntry("Столбец справа", () => ExecuteTableAddColumn(left: false), MenuColumnRightIcon)
                };
                items.Add(insert);

                var remove = MenuEntry("Удалить из таблицы", null, MenuDeleteIcon, null, !locked, "Строку или столбец с ячейкой курсора, или всю таблицу");
                remove.ItemsSource = new List<Control>
                {
                    MenuEntry("Строку", ExecuteTableDeleteRow, MenuRowDeleteIcon),
                    MenuEntry("Столбец", ExecuteTableDeleteColumn, MenuColumnDeleteIcon),
                    MenuEntry("Таблицу", ExecuteTableDelete, MenuDeleteIcon)
                };
                items.Add(remove);

                var activeCell = table.GetCell(_activeCellRow, _activeCellCol);
                bool canSplit = activeCell is not null && (activeCell.RowSpan > 1 || activeCell.ColSpan > 1);
                items.Add(MenuEntry("Объединить ячейки", ExecuteTableMergeCells, MenuMergeIcon, null, !locked && HasCellRangeSel(),
                    HasCellRangeSel() ? "Выделенные ячейки станут одной" : "Сначала выделите несколько ячеек"));
                items.Add(MenuEntry("Разделить ячейку", ExecuteTableSplitCell, MenuSplitIcon, null, !locked && canSplit,
                    canSplit ? "Объединённая ячейка снова станет отдельными" : "Разделить можно только объединённую ячейку"));

                // Диаграмма по числам выделенных ячеек, без выделения — по всей таблице.
                items.Add(MenuEntry(HasCellRangeSel() ? "Диаграмма по выделенным ячейкам…" : "Диаграмма по таблице…",
                    doc.InsertChartFromTable, MenuChartIcon, null, !locked));
            }

            items.Add(new Separator());
            items.Add(MenuEntry("Создать примечание", () => doc.InsertComment(string.Empty), MenuCommentIcon, null, !locked,
                "Заметка на поле листа к выделенному тексту или к месту курсора"));
            items.Add(new Separator());
            items.Add(MenuEntry("Выделить всё", ExecuteSelectAll, MenuSelectAllIcon, "Ctrl+A", true, "Весь текст документа"));
            return items;
        }

        /// <summary>
        /// Подменю «Добавить»: то же, что на вкладке «Вставка», — в место каретки.
        /// Таблица выбирается сеткой размера, как у кнопки ленты; рисунок — из файла.
        /// </summary>
        private MenuItem InsertMenu(DocumentViewModel doc, bool enabled)
        {
            var chart = MenuEntry("Диаграмму", null, MenuChartIcon);
            chart.ItemsSource = new List<Control>
            {
                ChartKindEntry("Гистограмма", ChartKind.Column, () => doc.InsertChart(ChartKind.Column)),
                ChartKindEntry("Линейчатая", ChartKind.Bar, () => doc.InsertChart(ChartKind.Bar)),
                ChartKindEntry("График", ChartKind.Line, () => doc.InsertChart(ChartKind.Line)),
                ChartKindEntry("С областями", ChartKind.Area, () => doc.InsertChart(ChartKind.Area)),
                ChartKindEntry("Круговая", ChartKind.Pie, () => doc.InsertChart(ChartKind.Pie)),
                ChartKindEntry("Кольцевая", ChartKind.Doughnut, () => doc.InsertChart(ChartKind.Doughnut)),
                ChartKindEntry("Точечная", ChartKind.Scatter, () => doc.InsertChart(ChartKind.Scatter)),
                ChartKindEntry("Лепестковая", ChartKind.Radar, () => doc.InsertChart(ChartKind.Radar)),
                ChartKindEntry("Комбинированная", ChartKind.Column, doc.InsertComboChart, combo: true)
            };

            var shape = MenuEntry("Фигуру", null, MenuRectangleIcon);
            shape.ItemsSource = new List<Control>
            {
                MenuEntry("Прямоугольник", () => doc.InsertShape(ShapeType.Rectangle), MenuRectangleIcon),
                MenuEntry("Овал", () => doc.InsertShape(ShapeType.Ellipse), MenuEllipseIcon),
                MenuEntry("Линия", () => doc.InsertShape(ShapeType.Line), MenuLineIcon),
                MenuEntry("Стрелка", () => doc.InsertShape(ShapeType.Arrow), MenuArrowIcon),
                MenuEntry("Выноска", () => doc.InsertShape(ShapeType.Callout), MenuCalloutIcon)
            };

            var insert = MenuEntry("Добавить", null, MenuInsertIcon, null, enabled,
                "Таблица, рисунок, диаграмма, фигура, формула, сноска и другое — в место курсора");
            insert.ItemsSource = new List<Control>
            {
                MenuEntry("Таблицу…", ShowTablePickerAtPointer, MenuTableIcon),
                MenuEntry("Рисунок…", PickAndInsertPicture, MenuPictureIcon),
                chart,
                shape,
                MenuEntry("Формулу", doc.InsertFormula, MenuFormulaIcon),
                new Separator(),
                MenuEntry("Сноску", doc.InsertFootnote, MenuFootnoteIcon, null, true, "Пояснение внизу страницы со знаком в тексте"),
                MenuEntry("Концевую сноску", doc.InsertEndnote, MenuEndnoteIcon, null, true, "Пояснение в конце документа со знаком в тексте"),
                MenuEntry("Дату и время…", doc.InsertDateTime, MenuDateIcon),
                new Separator(),
                MenuEntry("Разрыв страницы", doc.InsertPageBreak, MenuPageBreakIcon, null, true, "Дальнейший текст начнётся с новой страницы")
            };
            return insert;
        }

        /// <summary>Сетка выбора размера таблицы у указателя — та же, что у кнопки «Таблица» на ленте.</summary>
        private void ShowTablePickerAtPointer()
        {
            var picker = new TableGridPickerControl();
            var frame = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6),
                Child = picker,
                Cursor = new Cursor(StandardCursorType.Arrow)
            };
            frame.Bind(Border.BackgroundProperty, frame.GetResourceObservable("BgSurfaceBrush"));
            frame.Bind(Border.BorderBrushProperty, frame.GetResourceObservable("BorderDefaultBrush"));

            var flyout = new Flyout { Content = frame, Placement = PlacementMode.Pointer };
            picker.TableSelected += (rows, columns) =>
            {
                flyout.Hide();
                DocVm?.InsertTable(rows, columns);
                Focus();
            };
            flyout.ShowAt(this, showAtPointer: true);
        }

        /// <summary>Выбор файла рисунка и вставка его в место каретки.</summary>
        private async void PickAndInsertPicture()
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage is null || DocVm is null) return;

            var files = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Вставить изображение",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("Изображения")
                    {
                        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.webp" }
                    }
                }
            });

            if (files.Count == 0) return;
            string? path = files[0].TryGetLocalPath();
            if (!string.IsNullOrEmpty(path)) DocVm?.InsertImage(path);
            Focus();
        }

        /// <summary>
        /// «Вставить только текст»: из буфера берётся один текст, без картинок, таблиц и
        /// оформления источника; вставленное получает оформление места вставки.
        /// </summary>
        public void ExecutePasteTextOnly()
        {
            if (IsEditingBlocked) return;
            _pasteTextOnly = true;
            ExecutePaste();
        }

        /// <summary>«Вставить с оформлением»: как скопировано, даже если по умолчанию вставляется только текст.</summary>
        public void ExecutePasteWithFormatting()
        {
            if (IsEditingBlocked) return;
            _pasteTextOnly = false;
            ExecutePaste();
        }

        /// <summary>
        /// Вид вставки для <see cref="PasteAsync"/>, выбранный пунктом меню: true — только
        /// текст, false — с оформлением, null — вставка по умолчанию. Сбрасывается в её начале.
        /// </summary>
        private bool? _pasteTextOnly;

        // ── Картинка и диаграмма ─────────────────────────────────────────

        private List<Control> ImageMenuItems(DocumentViewModel doc, ImageBlock image)
        {
            bool locked = IsEditingBlocked;
            var items = new List<Control>();

            if (image.IsChart)
            {
                items.Add(MenuEntry("Изменить данные…", () => doc.RequestChartEdit(image), MenuTableIcon, null, !locked));
                items.Add(new Separator());
            }

            items.Add(MenuEntry("Вырезать", ExecuteCut, MenuCutIcon, "Ctrl+X", !locked));
            items.Add(MenuEntry("Копировать", ExecuteCopy, MenuCopyIcon, "Ctrl+C"));
            items.Add(MenuEntry("Вставить", ExecutePaste, MenuPasteIcon, "Ctrl+V", !locked));
            items.Add(new Separator());
            items.Add(WrapMenu(image.WrapMode, doc.SetImageWrapMode, !locked));
            items.Add(new Separator());
            items.Add(MenuEntry(image.IsChart ? "Удалить диаграмму" : "Удалить рисунок",
                doc.DeleteSelectedImage, MenuDeleteIcon, "Delete", !locked));
            return items;
        }

        // ── Фигура ───────────────────────────────────────────────────────

        private List<Control> ShapeMenuItems()
        {
            bool locked = IsEditingBlocked;
            var shape = _selectedShape!;

            void CopyShape() => _shapeClipboard = Services.DocumentCloner.CloneBlock(shape) as ShapeBlock;

            return new List<Control>
            {
                MenuEntry("Вырезать", () => { CopyShape(); DeleteSelectedShape(); }, MenuCutIcon, "Ctrl+X", !locked),
                MenuEntry("Копировать", CopyShape, MenuCopyIcon, "Ctrl+C"),
                MenuEntry("Вставить", () =>
                {
                    if (_shapeClipboard is not null) PasteShapeFromClipboard();
                    else ExecutePaste();
                }, MenuPasteIcon, "Ctrl+V", !locked),
                new Separator(),
                MenuEntry("На передний план", () => DocVm?.SetShapeZOrder(true), MenuFrontIcon, null, !locked, "Поверх других фигур"),
                MenuEntry("На задний план", () => DocVm?.SetShapeZOrder(false), MenuBackIcon, null, !locked, "Под другими фигурами"),
                WrapMenu(shape.WrapMode, mode => DocVm?.SetShapeWrapMode(mode), !locked),
                new Separator(),
                MenuEntry("Удалить фигуру", DeleteSelectedShape, MenuDeleteIcon, "Delete", !locked)
            };
        }

        /// <summary>Подменю «Обтекание текстом» с отметкой нынешнего режима.</summary>
        private static MenuItem WrapMenu(WrapMode current, Action<WrapMode> apply, bool enabled)
        {
            MenuItem Mode(string header, WrapMode mode) => MenuChoice(header, current == mode, "wrap", () => apply(mode));

            var wrap = MenuEntry("Обтекание текстом", null, MenuWrapIcon, null, enabled, "Как текст располагается вокруг объекта");
            wrap.ItemsSource = new List<Control>
            {
                Mode("В тексте", WrapMode.Inline),
                Mode("Вокруг рамки", WrapMode.Square),
                Mode("По контуру", WrapMode.Tight),
                Mode("Перед текстом", WrapMode.InFront),
                Mode("За текстом", WrapMode.Behind)
            };
            return wrap;
        }

        /// <summary>
        /// Пункт меню: подпись, действие, значок и сочетание клавиш. Сочетание стоит у
        /// правого края пункта приглушённым цветом, значок — тоже приглушённый, как у
        /// кнопок ленты. Ширина у пунктов общая, чтобы сочетания стояли одним столбцом.
        /// </summary>
        private static MenuItem MenuEntry(string header, Action? action, string? icon = null, string? gesture = null, bool enabled = true, string? tip = null)
        {
            // Курсор свой: меню наследует его от листа, а там текстовая каретка.
            var item = new MenuItem { Header = MenuHeader(header, gesture), IsEnabled = enabled, MinWidth = MenuItemMinWidth, Cursor = new Cursor(StandardCursorType.Arrow) };
            SetMenuTip(item, header, tip);
            if (icon is not null)
            {
                var glyph = new PathIcon { Data = StreamGeometry.Parse(icon), Width = 15, Height = 15 };
                glyph[!PathIcon.ForegroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("TextSecondaryBrush");
                item.Icon = glyph;
            }
            if (action is not null)
                item.Click += (_, _) => action();
            return item;
        }

        /// <summary>Пункт-переключатель: отмечен выбранный вариант своей группы.</summary>
        private static MenuItem MenuChoice(string header, bool isChecked, string group, Action action, string? tip = null)
        {
            var item = new MenuItem
            {
                Header = MenuHeader(header, null),
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = isChecked,
                GroupName = group,
                MinWidth = MenuItemMinWidth,
                Cursor = new Cursor(StandardCursorType.Arrow)
            };
            SetMenuTip(item, header, tip);
            item.Click += (_, _) => action();
            return item;
        }

        /// <summary>Пункт вида диаграммы со значком этого вида — тем же, что в окне диаграммы.</summary>
        private static MenuItem ChartKindEntry(string header, ChartKind kind, Action action, bool combo = false)
        {
            var item = MenuEntry(header, action);
            item.Icon = new Views.Dialogs.ChartKindIcon { Kind = kind, Combo = combo, Width = 20, Height = 15 };
            return item;
        }

        /// <summary>Подсказка пункта: появляется у тех, где есть что пояснить.</summary>
        private static void SetMenuTip(MenuItem item, string header, string? tip)
        {
            if (string.IsNullOrEmpty(tip)) return;
            Writersword.Infrastructure.Behaviours.TooltipBehavior.SetTip(item, header);
            Writersword.Infrastructure.Behaviours.TooltipBehavior.SetDescription(item, tip);
        }

        private const double MenuItemMinWidth = 250;

        /// <summary>Подпись пункта: текст слева, сочетание клавиш у правого края.</summary>
        private static Control MenuHeader(string text, string? gesture)
        {
            // Своя ширина у подписи: заголовок пункта в шаблоне меню не растягивается на
            // всю строку, и без неё сочетание стояло бы сразу за текстом.
            var panel = new DockPanel
            {
                LastChildFill = true,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                MinWidth = MenuItemMinWidth - 80
            };
            if (gesture is not null)
            {
                var keys = new TextBlock
                {
                    Text = gesture,
                    Margin = new Thickness(32, 0, 0, 0),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                };
                keys[!TextBlock.ForegroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("TextMutedBrush");
                DockPanel.SetDock(keys, Avalonia.Controls.Dock.Right);
                panel.Children.Add(keys);
            }
            panel.Children.Add(new TextBlock { Text = text, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            return panel;
        }
    }
}
