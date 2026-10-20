using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;

namespace Writersword.Modules.TextEditor.Views.Dialogs
{
    /// <summary>
    /// Лист данных диаграммы: прямоугольник строк и столбцов текста, как лист Excel у
    /// диаграммы Word. Строка 0 — шапка (имена рядов), столбец 0 — подписи категорий
    /// или значения X; остальное — числа. Хранит текст как есть: что не число, в
    /// диаграмму не попадает, но и не теряется.
    /// </summary>
    public sealed class ChartSheet
    {
        private readonly List<List<string>> _rows = new();

        public ChartSheet(int rows, int columns)
        {
            EnsureSize(Math.Max(1, rows), Math.Max(1, columns));
        }

        private ChartSheet()
        {
        }

        public int RowCount => _rows.Count;

        public int ColumnCount { get; private set; }

        public string Get(int row, int column)
            => row >= 0 && column >= 0 && row < _rows.Count && column < ColumnCount ? _rows[row][column] : string.Empty;

        /// <summary>Пишет текст ячейки; ячейка за краем листа его расширяет.</summary>
        public void Set(int row, int column, string? text)
        {
            if (row < 0 || column < 0) return;
            EnsureSize(row + 1, column + 1);
            _rows[row][column] = text ?? string.Empty;
        }

        public void EnsureSize(int rows, int columns)
        {
            if (columns > ColumnCount)
            {
                foreach (var row in _rows)
                    while (row.Count < columns) row.Add(string.Empty);
                ColumnCount = columns;
            }

            while (_rows.Count < rows)
            {
                var row = new List<string>(ColumnCount);
                for (int c = 0; c < ColumnCount; c++) row.Add(string.Empty);
                _rows.Add(row);
            }
        }

        public void InsertRows(int at, int count)
        {
            at = Math.Clamp(at, 0, _rows.Count);
            for (int i = 0; i < count; i++)
            {
                var row = new List<string>(ColumnCount);
                for (int c = 0; c < ColumnCount; c++) row.Add(string.Empty);
                _rows.Insert(at, row);
            }
        }

        public void RemoveRows(int at, int count)
        {
            if (at < 0 || at >= _rows.Count) return;
            count = Math.Min(count, _rows.Count - at);
            _rows.RemoveRange(at, count);
        }

        public void InsertColumns(int at, int count)
        {
            at = Math.Clamp(at, 0, ColumnCount);
            foreach (var row in _rows)
                for (int i = 0; i < count; i++) row.Insert(at, string.Empty);
            ColumnCount += count;
        }

        public void RemoveColumns(int at, int count)
        {
            if (at < 0 || at >= ColumnCount) return;
            count = Math.Min(count, ColumnCount - at);
            foreach (var row in _rows) row.RemoveRange(at, count);
            ColumnCount -= count;
        }

        public ChartSheet Clone()
        {
            var copy = new ChartSheet { ColumnCount = ColumnCount };
            foreach (var row in _rows) copy._rows.Add(new List<string>(row));
            return copy;
        }

        public bool ContentEquals(ChartSheet other)
        {
            if (other is null || other.RowCount != RowCount || other.ColumnCount != ColumnCount) return false;
            for (int r = 0; r < RowCount; r++)
                for (int c = 0; c < ColumnCount; c++)
                    if (!string.Equals(_rows[r][c], other._rows[r][c], StringComparison.Ordinal)) return false;
            return true;
        }
    }

    /// <summary>Роль ячейки листа: от неё зависят вид текста и проверка числа.</summary>
    public enum ChartCellRole
    {
        Corner,
        SeriesName,
        SizeHeader,
        Category,
        XValue,
        Value,
        Size
    }

    /// <summary>Куда пришёлся щелчок по таблице.</summary>
    public enum ChartGridHitKind
    {
        None,
        Corner,
        RowHeader,
        ColumnHeader,
        Cell
    }

    /// <summary>Попадание по таблице: строка и столбец листа и что именно задето.</summary>
    public readonly record struct ChartGridHit(ChartGridHitKind Kind, int Row, int Column);

    /// <summary>
    /// Таблица данных диаграммы, устроенная как лист Excel.
    ///
    /// Ячейку выбирают щелчком или стрелками, диапазон — протягиванием или с Shift.
    /// Печатать можно сразу: первая буква начинает правку ячейки и заменяет её текст;
    /// F2 и двойной щелчок правят текст, не стирая. Enter уходит вниз, Tab — вправо,
    /// Shift — в обратную сторону, Esc отменяет правку. Delete очищает выделение.
    /// Ctrl+C, Ctrl+X и Ctrl+V работают с текстом через табуляцию — тем, что кладут в
    /// буфер Excel, Word и таблицы самого редактора: таблицу оттуда можно вставить
    /// целиком. Ctrl+A выделяет весь лист.
    ///
    /// За краем данных всегда видна пустая строка и пустой столбец: ввод в них
    /// добавляет категорию или ряд. Строки, ряды и их удаление — в меню правой кнопки.
    /// </summary>
    public sealed class ChartDataGrid : Control
    {
        public const double RowHeaderWidth = 34;
        public const double ColumnHeaderHeight = 22;
        public const double RowHeight = 26;
        public const double FirstColumnWidth = 148;
        public const double ValueColumnWidth = 92;
        public const double SizeColumnWidth = 78;

        private readonly TextBox _editor;

        private ChartSheet _sheet = new(1, 1);

        // Выделение: опорная ячейка, активная ячейка и что выделяли — ячейки, строки целиком
        // или столбцы целиком.
        private int _anchorRow, _anchorColumn, _activeRow, _activeColumn;
        private ChartGridHitKind _selectionKind = ChartGridHitKind.Cell;

        private bool _editing;
        private bool _editByTyping;
        private int _editRow, _editColumn;
        private string _editOriginal = string.Empty;

        private bool _dragging;
        private ChartGridHit _lastContextHit;
        private ScrollViewer? _scroller;

        public ChartDataGrid()
        {
            Focusable = true;
            IsTabStop = true;
            ClipToBounds = true;
            Cursor = new Cursor(StandardCursorType.Arrow);

            _editor = new TextBox
            {
                IsVisible = false,
                FontSize = 12,
                Padding = new Thickness(5, 3),
                MinHeight = 0,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(0),
                AcceptsReturn = false,
                AcceptsTab = false,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            _editor.AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
            _editor.LostFocus += (_, _) =>
            {
                if (_editing) CommitEdit(moveRow: 0, moveColumn: 0, keepFocus: false);
            };
            VisualChildren.Add(_editor);
            LogicalChildren.Add(_editor);

            AddHandler(ContextRequestedEvent, OnGridContextRequested, RoutingStrategies.Bubble);
        }

        // ── Настройка ────────────────────────────────────────────────────

        /// <summary>Лист, который показывает и правит таблица.</summary>
        public ChartSheet Sheet
        {
            get => _sheet;
            set
            {
                CancelEdit();
                _sheet = value ?? new ChartSheet(1, 1);
                ClampSelection();
                InvalidateMeasure();
                InvalidateVisual();
            }
        }

        /// <summary>Пустых столбцов за краем данных: ввод в них добавляет ряд.</summary>
        public int GhostColumns { get; set; } = 1;

        /// <summary>Пустых строк за краем данных: ввод в них добавляет категорию.</summary>
        public int GhostRows { get; set; } = 1;

        /// <summary>Роль ячейки — задаёт её вид и требование числа.</summary>
        public Func<int, int, ChartCellRole>? RoleOf { get; set; }

        /// <summary>Подсказка в пустой ячейке: «+ ряд», «Размер» и подобное. Null — нет.</summary>
        public Func<int, int, string?>? PlaceholderOf { get; set; }

        /// <summary>Ширина столбца листа; без неё — по умолчанию.</summary>
        public Func<int, double>? ColumnWidthOf { get; set; }

        /// <summary>Перед изменением листа: окно запоминает состояние для отмены.</summary>
        public event Action? BeforeChange;

        /// <summary>Лист изменён правкой из таблицы.</summary>
        public event Action? Changed;

        /// <summary>Выделение сдвинулось.</summary>
        public event Action? SelectionChanged;

        /// <summary>Запрошено меню правой кнопки по этому месту таблицы.</summary>
        public event Action<ChartGridHit>? MenuRequested;

        private int TotalRows => _sheet.RowCount + Math.Max(0, GhostRows);
        private int TotalColumns => _sheet.ColumnCount + Math.Max(0, GhostColumns);

        /// <summary>Выделенный диапазон: первая и последняя строка и столбец, включительно.</summary>
        public (int Top, int Left, int Bottom, int Right) Selection
        {
            get
            {
                int top = Math.Min(_anchorRow, _activeRow);
                int bottom = Math.Max(_anchorRow, _activeRow);
                int left = Math.Min(_anchorColumn, _activeColumn);
                int right = Math.Max(_anchorColumn, _activeColumn);

                if (_selectionKind == ChartGridHitKind.RowHeader) { left = 0; right = TotalColumns - 1; }
                if (_selectionKind == ChartGridHitKind.ColumnHeader) { top = 0; bottom = TotalRows - 1; }

                return (top, left, bottom, right);
            }
        }

        /// <summary>Активная ячейка: строка и столбец листа.</summary>
        public (int Row, int Column) Active => (_activeRow, _activeColumn);

        /// <summary>Идёт правка текста ячейки.</summary>
        public bool IsEditing => _editing;

        /// <summary>Выделяет одну ячейку и показывает её.</summary>
        public void SelectCell(int row, int column)
        {
            _selectionKind = ChartGridHitKind.Cell;
            _anchorRow = _activeRow = row;
            _anchorColumn = _activeColumn = column;
            ClampSelection();
            AfterSelectionMoved();
        }

        /// <summary>
        /// Выделяет ячейку и сразу открывает её правку с кареткой; selectAll — текст
        /// выделен целиком, и ввод заменяет его (имя нового ряда, новая категория).
        /// </summary>
        public void EditCell(int row, int column, bool selectAll)
        {
            CommitPendingEdit();
            SelectCell(row, column);
            BeginEdit(replace: false, initial: null, selectAll);
        }

        /// <summary>Выделяет диапазон ячеек.</summary>
        public void SelectRange(int top, int left, int bottom, int right)
        {
            _selectionKind = ChartGridHitKind.Cell;
            _anchorRow = top;
            _anchorColumn = left;
            _activeRow = bottom;
            _activeColumn = right;
            ClampSelection();
            AfterSelectionMoved();
        }

        /// <summary>Сбрасывает незаконченную правку ячейки, принимая введённое.</summary>
        public void CommitPendingEdit()
        {
            if (_editing) CommitEdit(0, 0, keepFocus: false);
        }

        // ── Геометрия ────────────────────────────────────────────────────

        private double WidthOf(int column)
        {
            if (ColumnWidthOf is { } width) return width(column);
            return column == 0 ? FirstColumnWidth : ValueColumnWidth;
        }

        private double ColumnLeft(int column)
        {
            double x = RowHeaderWidth;
            for (int c = 0; c < column; c++) x += WidthOf(c);
            return x;
        }

        private static double RowTop(int row) => ColumnHeaderHeight + row * RowHeight;

        private Rect CellRect(int row, int column)
            => new(ColumnLeft(column), RowTop(row), WidthOf(column), RowHeight);

        private double TotalWidth()
        {
            double width = RowHeaderWidth;
            for (int c = 0; c < TotalColumns; c++) width += WidthOf(c);
            return width;
        }

        private ChartGridHit HitTest(Point point)
        {
            bool inHeaderRow = point.Y < ColumnHeaderHeight;
            bool inHeaderColumn = point.X < RowHeaderWidth;
            if (inHeaderRow && inHeaderColumn) return new ChartGridHit(ChartGridHitKind.Corner, 0, 0);

            int row = (int)Math.Floor((point.Y - ColumnHeaderHeight) / RowHeight);
            int column = -1;
            double x = RowHeaderWidth;
            for (int c = 0; c < TotalColumns; c++)
            {
                double w = WidthOf(c);
                if (point.X >= x && point.X < x + w) { column = c; break; }
                x += w;
            }

            if (inHeaderRow)
                return column >= 0 ? new ChartGridHit(ChartGridHitKind.ColumnHeader, 0, column) : default;
            if (inHeaderColumn)
                return row >= 0 && row < TotalRows ? new ChartGridHit(ChartGridHitKind.RowHeader, row, 0) : default;
            if (row < 0 || row >= TotalRows || column < 0) return default;
            return new ChartGridHit(ChartGridHitKind.Cell, row, column);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            _editor.Measure(new Size(ValueColumnWidth, RowHeight));
            return new Size(TotalWidth(), ColumnHeaderHeight + TotalRows * RowHeight + 1);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            if (_editing)
            {
                var rect = CellRect(_editRow, _editColumn);
                _editor.Arrange(new Rect(rect.X - 1, rect.Y - 1, rect.Width + 2, rect.Height + 2));
            }
            else
            {
                _editor.Arrange(new Rect(0, 0, 0, 0));
            }
            return finalSize;
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _scroller = this.FindAncestorOfType<ScrollViewer>();
            if (_scroller is not null) _scroller.ScrollChanged += OnScrollChanged;
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            if (_scroller is not null) _scroller.ScrollChanged -= OnScrollChanged;
            _scroller = null;
            base.OnDetachedFromVisualTree(e);
        }

        private void OnScrollChanged(object? sender, ScrollChangedEventArgs e) => InvalidateVisual();

        // ── Рисование ────────────────────────────────────────────────────

        private IBrush Brush(string key, Color fallback)
        {
            if (this.TryFindResource(key, ActualThemeVariant, out var value))
            {
                if (value is IBrush brush) return brush;
                if (value is Color color) return new SolidColorBrush(color);
            }
            return new SolidColorBrush(fallback);
        }

        private static Color ColorOf(IBrush brush, Color fallback)
            => brush is ISolidColorBrush solid ? solid.Color : fallback;

        public override void Render(DrawingContext context)
        {
            var cellBack = Brush("BgAppBrush", Color.FromRgb(0x1E, 0x1E, 0x1E));
            var headerBack = Brush("BgSurfaceBrush", Color.FromRgb(0x2A, 0x2A, 0x2A));
            var lineBrush = Brush("BorderSubtleBrush", Color.FromRgb(0x3A, 0x3A, 0x3A));
            var textBrush = Brush("TextPrimaryBrush", Colors.White);
            var mutedBrush = Brush("TextMutedBrush", Color.FromRgb(0x90, 0x90, 0x90));
            var secondaryBrush = Brush("TextSecondaryBrush", Color.FromRgb(0xB0, 0xB0, 0xB0));
            var accent = ColorOf(Brush("AccentDefaultBrush", Color.FromRgb(0xD0, 0x8A, 0x3E)), Color.FromRgb(0xD0, 0x8A, 0x3E));
            var errorBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x4A, 0x4A));

            var lines = new Pen(lineBrush, 1);
            var ghostLines = new Pen(lineBrush, 1, dashStyle: new DashStyle(new double[] { 2, 2 }, 0));

            double width = TotalWidth();
            double height = ColumnHeaderHeight + TotalRows * RowHeight;

            // Видимая часть: рисуется только она — лист может быть большим.
            Rect visible = _scroller is { } sv
                ? new Rect(sv.Offset.X, sv.Offset.Y, sv.Viewport.Width, sv.Viewport.Height)
                : new Rect(Bounds.Size);
            if (visible.Width <= 0 || visible.Height <= 0) visible = new Rect(Bounds.Size);

            int firstRow = Math.Max(0, (int)Math.Floor((visible.Top - ColumnHeaderHeight) / RowHeight));
            int lastRow = Math.Min(TotalRows - 1, (int)Math.Ceiling((visible.Bottom - ColumnHeaderHeight) / RowHeight));

            context.FillRectangle(cellBack, new Rect(0, 0, width, height));

            var (selTop, selLeft, selBottom, selRight) = Selection;
            var selectionFill = new SolidColorBrush(Color.FromArgb(0x38, accent.R, accent.G, accent.B));
            var headerMark = new SolidColorBrush(Color.FromArgb(0x55, accent.R, accent.G, accent.B));

            // Ячейки.
            for (int r = firstRow; r <= lastRow; r++)
            {
                bool ghostRow = r >= _sheet.RowCount;
                double y = RowTop(r);
                if (y > visible.Bottom || y + RowHeight < visible.Top) continue;

                double x = RowHeaderWidth;
                for (int c = 0; c < TotalColumns; c++)
                {
                    double w = WidthOf(c);
                    if (x > visible.Right) break;
                    if (x + w < visible.Left) { x += w; continue; }

                    bool ghost = ghostRow || c >= _sheet.ColumnCount;
                    var rect = new Rect(x, y, w, RowHeight);
                    var role = RoleOf?.Invoke(r, c) ?? (r == 0 ? ChartCellRole.SeriesName : c == 0 ? ChartCellRole.Category : ChartCellRole.Value);

                    if (r == 0 || ghost)
                        context.FillRectangle(r == 0 ? headerBack : new SolidColorBrush(Color.FromArgb(0x10, 0x80, 0x80, 0x80)), rect);

                    // Выделение заливается целиком, без отдельной клетки внутри: ввод с
                    // клавиатуры идёт в клетку, с которой выделение начали.
                    bool selected = r >= selTop && r <= selBottom && c >= selLeft && c <= selRight;
                    bool single = selTop == selBottom && selLeft == selRight;
                    if (selected && !single)
                        context.FillRectangle(selectionFill, rect);

                    var pen = ghost ? ghostLines : lines;
                    context.DrawLine(pen, rect.TopRight, rect.BottomRight);
                    context.DrawLine(pen, rect.BottomLeft, rect.BottomRight);

                    // Угловая ячейка A1 ничего не значит для диаграммы: она пустая и не
                    // правится. В столбцах рядов имя ряда и числа стоят по центру,
                    // подписи категорий — от левого края.
                    if (role != ChartCellRole.Corner && !(_editing && r == _editRow && c == _editColumn))
                    {
                        bool centered = c > 0;
                        string text = _sheet.Get(r, c);
                        if (text.Length > 0)
                        {
                            bool numeric = TryNumber(text, out _);
                            bool needsNumber = role is ChartCellRole.Value or ChartCellRole.Size or ChartCellRole.XValue;
                            var brush = needsNumber && !numeric ? errorBrush : textBrush;
                            bool bold = role == ChartCellRole.SeriesName;
                            DrawCellText(context, text, rect, brush, bold, italic: false, centered);
                        }
                        else if (PlaceholderOf?.Invoke(r, c) is { Length: > 0 } hint)
                        {
                            DrawCellText(context, hint, rect, mutedBrush, bold: false, italic: true, centered);
                        }
                    }

                    x += w;
                }
            }

            // Заголовки столбцов (буквы) и строк (номера) — как у Excel.
            context.FillRectangle(headerBack, new Rect(0, 0, width, ColumnHeaderHeight));
            {
                double x = RowHeaderWidth;
                for (int c = 0; c < TotalColumns; c++)
                {
                    double w = WidthOf(c);
                    var rect = new Rect(x, 0, w, ColumnHeaderHeight);
                    if (c >= selLeft && c <= selRight) context.FillRectangle(headerMark, rect);
                    context.DrawLine(lines, rect.TopRight, rect.BottomRight);
                    DrawCentered(context, ColumnLetter(c), rect, c >= _sheet.ColumnCount ? mutedBrush : secondaryBrush, 11);
                    x += w;
                }
            }

            context.FillRectangle(headerBack, new Rect(0, ColumnHeaderHeight, RowHeaderWidth, height - ColumnHeaderHeight));
            for (int r = firstRow; r <= lastRow; r++)
            {
                var rect = new Rect(0, RowTop(r), RowHeaderWidth, RowHeight);
                if (r >= selTop && r <= selBottom) context.FillRectangle(headerMark, rect);
                context.DrawLine(lines, rect.BottomLeft, rect.BottomRight);
                DrawCentered(context, (r + 1).ToString(CultureInfo.InvariantCulture), rect, r >= _sheet.RowCount ? mutedBrush : secondaryBrush, 11);
            }

            context.DrawLine(lines, new Point(0, ColumnHeaderHeight), new Point(width, ColumnHeaderHeight));
            context.DrawLine(lines, new Point(RowHeaderWidth, 0), new Point(RowHeaderWidth, height));
            context.FillRectangle(headerBack, new Rect(0, 0, RowHeaderWidth, ColumnHeaderHeight));

            // Рамка выделения.
            var accentBrush = new SolidColorBrush(accent);
            var rangeRect = new Rect(
                ColumnLeft(selLeft), RowTop(selTop),
                ColumnLeft(selRight + 1) - ColumnLeft(selLeft), RowTop(selBottom + 1) - RowTop(selTop));
            context.DrawRectangle(null, new Pen(accentBrush, 2), rangeRect.Deflate(1));
        }

        private void DrawCellText(DrawingContext context, string text, Rect rect, IBrush brush, bool bold, bool italic, bool centered)
        {
            var typeface = new Typeface(FontFamily.Default, italic ? FontStyle.Italic : FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal);
            var formatted = new FormattedText(text.Replace('\n', ' '), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 12, brush)
            {
                MaxTextWidth = Math.Max(1, rect.Width - 12),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis
            };
            double x = centered ? rect.X + Math.Max(6, (rect.Width - formatted.Width) / 2) : rect.X + 6;
            double y = rect.Y + (rect.Height - formatted.Height) / 2;
            using (context.PushClip(rect.Deflate(1)))
                context.DrawText(formatted, new Point(x, y));
        }

        private static void DrawCentered(DrawingContext context, string text, Rect rect, IBrush brush, double size)
        {
            var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default), size, brush);
            context.DrawText(formatted, new Point(rect.X + (rect.Width - formatted.Width) / 2, rect.Y + (rect.Height - formatted.Height) / 2));
        }

        /// <summary>Буквы столбца, как у Excel: A…Z, AA…</summary>
        public static string ColumnLetter(int column)
        {
            var builder = new StringBuilder();
            int n = column + 1;
            while (n > 0)
            {
                int rem = (n - 1) % 26;
                builder.Insert(0, (char)('A' + rem));
                n = (n - 1) / 26;
            }
            return builder.ToString();
        }

        /// <summary>Число из текста ячейки: через точку или запятую, с пробелами разрядов и знаком %.</summary>
        public static bool TryNumber(string? text, out double value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;

            string cleaned = text.Trim()
                .Replace(" ", string.Empty)
                .Replace(" ", string.Empty)
                .Replace(" ", string.Empty)
                .Replace('−', '-')
                .Replace(',', '.');
            bool percent = cleaned.EndsWith('%');
            if (percent) cleaned = cleaned.TrimEnd('%');

            if (!double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)) return false;
            if (double.IsNaN(parsed) || double.IsInfinity(parsed)) return false;
            value = percent ? parsed / 100.0 : parsed;
            return true;
        }

        // ── Выделение ────────────────────────────────────────────────────

        private void ClampSelection()
        {
            int maxRow = Math.Max(0, TotalRows - 1);
            int maxColumn = Math.Max(0, TotalColumns - 1);
            _anchorRow = Math.Clamp(_anchorRow, 0, maxRow);
            _activeRow = Math.Clamp(_activeRow, 0, maxRow);
            _anchorColumn = Math.Clamp(_anchorColumn, 0, maxColumn);
            _activeColumn = Math.Clamp(_activeColumn, 0, maxColumn);
        }

        private void AfterSelectionMoved()
        {
            InvalidateVisual();
            var rect = CellRect(_activeRow, _activeColumn);
            // Строка и столбец заголовков прокручиваются вместе с листом: чтобы ячейка
            // у края не пряталась под ними, показываем её с запасом на заголовок.
            ScrollToShow(new Rect(rect.X - RowHeaderWidth, rect.Y - ColumnHeaderHeight, rect.Width + RowHeaderWidth, rect.Height + ColumnHeaderHeight));
            SelectionChanged?.Invoke();
        }

        /// <summary>Прокручивает окружающую прокрутку так, чтобы прямоугольник листа был виден.</summary>
        private void ScrollToShow(Rect rect)
        {
            if (_scroller is not { } sv) return;
            var viewport = sv.Viewport;
            if (viewport.Width <= 0 || viewport.Height <= 0) return;

            double x = sv.Offset.X;
            double y = sv.Offset.Y;
            if (rect.Left < x) x = rect.Left;
            else if (rect.Right > x + viewport.Width) x = rect.Right - viewport.Width;
            if (rect.Top < y) y = rect.Top;
            else if (rect.Bottom > y + viewport.Height) y = rect.Bottom - viewport.Height;

            x = Math.Max(0, x);
            y = Math.Max(0, y);
            if (Math.Abs(x - sv.Offset.X) > 0.5 || Math.Abs(y - sv.Offset.Y) > 0.5)
                sv.Offset = new Vector(x, y);
        }

        private void MoveActive(int deltaRow, int deltaColumn, bool extend)
        {
            if (_selectionKind != ChartGridHitKind.Cell)
            {
                _selectionKind = ChartGridHitKind.Cell;
                if (!extend)
                {
                    _anchorRow = _activeRow;
                    _anchorColumn = _activeColumn;
                }
            }

            _activeRow = Math.Clamp(_activeRow + deltaRow, 0, TotalRows - 1);
            _activeColumn = Math.Clamp(_activeColumn + deltaColumn, 0, TotalColumns - 1);
            if (!extend)
            {
                _anchorRow = _activeRow;
                _anchorColumn = _activeColumn;
            }
            AfterSelectionMoved();
        }

        private void JumpActive(int row, int column, bool extend)
        {
            _selectionKind = ChartGridHitKind.Cell;
            _activeRow = Math.Clamp(row, 0, TotalRows - 1);
            _activeColumn = Math.Clamp(column, 0, TotalColumns - 1);
            if (!extend)
            {
                _anchorRow = _activeRow;
                _anchorColumn = _activeColumn;
            }
            AfterSelectionMoved();
        }

        // ── Мышь ─────────────────────────────────────────────────────────

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (_editing && e.Source is Visual source && IsInsideEditor(source)) return;
            if (_editing) CommitEdit(0, 0, keepFocus: true);

            Focus();
            var point = e.GetPosition(this);
            var hit = HitTest(point);
            var props = e.GetCurrentPoint(this).Properties;
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

            if (props.IsRightButtonPressed)
            {
                // Правая кнопка по месту вне выделения переносит выделение туда —
                // меню действует на то, по чему щёлкнули.
                var (top, left, bottom, right) = Selection;
                bool inside = hit.Kind switch
                {
                    ChartGridHitKind.Cell => hit.Row >= top && hit.Row <= bottom && hit.Column >= left && hit.Column <= right,
                    ChartGridHitKind.RowHeader => _selectionKind == ChartGridHitKind.RowHeader && hit.Row >= top && hit.Row <= bottom,
                    ChartGridHitKind.ColumnHeader => _selectionKind == ChartGridHitKind.ColumnHeader && hit.Column >= left && hit.Column <= right,
                    _ => true
                };
                if (!inside) ApplyHit(hit, extend: false);
                _lastContextHit = hit;
                return;
            }

            if (!props.IsLeftButtonPressed) return;

            if (hit.Kind == ChartGridHitKind.Cell && e.ClickCount == 2)
            {
                ApplyHit(hit, extend: false);
                BeginEdit(replace: false, initial: null);
                e.Handled = true;
                return;
            }

            ApplyHit(hit, shift);
            if (hit.Kind != ChartGridHitKind.None && hit.Kind != ChartGridHitKind.Corner)
            {
                _dragging = true;
                e.Pointer.Capture(this);
            }
            e.Handled = true;
        }

        private void ApplyHit(ChartGridHit hit, bool extend)
        {
            switch (hit.Kind)
            {
                case ChartGridHitKind.Corner:
                    _selectionKind = ChartGridHitKind.Cell;
                    _anchorRow = 0;
                    _anchorColumn = 0;
                    _activeRow = Math.Max(0, _sheet.RowCount - 1);
                    _activeColumn = Math.Max(0, _sheet.ColumnCount - 1);
                    break;

                case ChartGridHitKind.RowHeader:
                    if (!extend || _selectionKind != ChartGridHitKind.RowHeader) _anchorRow = hit.Row;
                    _selectionKind = ChartGridHitKind.RowHeader;
                    _activeRow = hit.Row;
                    _anchorColumn = 0;
                    _activeColumn = 0;
                    break;

                case ChartGridHitKind.ColumnHeader:
                    if (!extend || _selectionKind != ChartGridHitKind.ColumnHeader) _anchorColumn = hit.Column;
                    _selectionKind = ChartGridHitKind.ColumnHeader;
                    _activeColumn = hit.Column;
                    _anchorRow = 0;
                    _activeRow = 0;
                    break;

                case ChartGridHitKind.Cell:
                    _selectionKind = ChartGridHitKind.Cell;
                    _activeRow = hit.Row;
                    _activeColumn = hit.Column;
                    if (!extend)
                    {
                        _anchorRow = hit.Row;
                        _anchorColumn = hit.Column;
                    }
                    break;

                default:
                    return;
            }

            ClampSelection();
            AfterSelectionMoved();
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!_dragging) return;

            var point = e.GetPosition(this);
            var hit = HitTest(new Point(Math.Max(point.X, RowHeaderWidth + 1), Math.Max(point.Y, ColumnHeaderHeight + 1)));

            switch (_selectionKind)
            {
                case ChartGridHitKind.RowHeader:
                    if (hit.Kind is ChartGridHitKind.Cell or ChartGridHitKind.RowHeader && hit.Row != _activeRow)
                    {
                        _activeRow = hit.Row;
                        AfterSelectionMoved();
                    }
                    break;

                case ChartGridHitKind.ColumnHeader:
                    if (hit.Kind is ChartGridHitKind.Cell or ChartGridHitKind.ColumnHeader && hit.Column != _activeColumn)
                    {
                        _activeColumn = hit.Column;
                        AfterSelectionMoved();
                    }
                    break;

                default:
                    if (hit.Kind == ChartGridHitKind.Cell && (hit.Row != _activeRow || hit.Column != _activeColumn))
                    {
                        _activeRow = hit.Row;
                        _activeColumn = hit.Column;
                        AfterSelectionMoved();
                    }
                    break;
            }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (_dragging)
            {
                _dragging = false;
                e.Pointer.Capture(null);
            }
        }

        private void OnGridContextRequested(object? sender, ContextRequestedEventArgs e)
        {
            if (_editing) return;
            var hit = _lastContextHit;
            if (hit.Kind == ChartGridHitKind.None)
                hit = new ChartGridHit(ChartGridHitKind.Cell, _activeRow, _activeColumn);
            _lastContextHit = default;
            MenuRequested?.Invoke(hit);
            e.Handled = true;
        }

        private bool IsInsideEditor(Visual visual)
        {
            for (Visual? v = visual; v is not null; v = v.GetVisualParent())
                if (ReferenceEquals(v, _editor)) return true;
            return false;
        }

        // ── Клавиатура ───────────────────────────────────────────────────

        protected override void OnGotFocus(FocusChangedEventArgs e)
        {
            base.OnGotFocus(e);
            InvalidateVisual();
        }

        protected override void OnLostFocus(FocusChangedEventArgs e)
        {
            base.OnLostFocus(e);
            InvalidateVisual();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled || _editing) return;

            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

            switch (e.Key)
            {
                case Key.Up:
                    if (ctrl) JumpActive(DataEdge(_activeRow, -1, rows: true), _activeColumn, shift);
                    else MoveActive(-1, 0, shift);
                    break;
                case Key.Down:
                    if (ctrl) JumpActive(DataEdge(_activeRow, 1, rows: true), _activeColumn, shift);
                    else MoveActive(1, 0, shift);
                    break;
                case Key.Left:
                    if (ctrl) JumpActive(_activeRow, DataEdge(_activeColumn, -1, rows: false), shift);
                    else MoveActive(0, -1, shift);
                    break;
                case Key.Right:
                    if (ctrl) JumpActive(_activeRow, DataEdge(_activeColumn, 1, rows: false), shift);
                    else MoveActive(0, 1, shift);
                    break;
                case Key.Home:
                    JumpActive(ctrl ? 0 : _activeRow, 0, shift);
                    break;
                case Key.End:
                    JumpActive(ctrl ? Math.Max(0, _sheet.RowCount - 1) : _activeRow, Math.Max(0, _sheet.ColumnCount - 1), shift);
                    break;
                case Key.PageUp:
                    MoveActive(-10, 0, shift);
                    break;
                case Key.PageDown:
                    MoveActive(10, 0, shift);
                    break;
                case Key.Enter:
                    MoveActive(shift ? -1 : 1, 0, extend: false);
                    break;
                case Key.Tab:
                    MoveActive(0, shift ? -1 : 1, extend: false);
                    break;
                case Key.F2:
                    BeginEdit(replace: false, initial: null);
                    break;
                case Key.Delete:
                    ClearSelection();
                    break;
                case Key.Back:
                    ClearSelection();
                    BeginEdit(replace: true, initial: string.Empty);
                    break;
                case Key.A when ctrl:
                    ApplyHit(new ChartGridHit(ChartGridHitKind.Corner, 0, 0), extend: false);
                    break;
                case Key.C when ctrl:
                    _ = CopySelectionAsync(cut: false);
                    break;
                case Key.X when ctrl:
                    _ = CopySelectionAsync(cut: true);
                    break;
                case Key.V when ctrl:
                    _ = PasteFromClipboardAsync();
                    break;
                case Key.Insert when shift:
                    _ = PasteFromClipboardAsync();
                    break;
                default:
                    return;
            }

            e.Handled = true;
        }

        protected override void OnTextInput(TextInputEventArgs e)
        {
            base.OnTextInput(e);
            if (e.Handled || _editing || string.IsNullOrEmpty(e.Text)) return;
            if (char.IsControl(e.Text[0])) return;

            BeginEdit(replace: true, initial: e.Text);
            e.Handled = true;
        }

        /// <summary>Край данных для Ctrl+стрелки: последняя или первая непустая клетка в направлении.</summary>
        private int DataEdge(int from, int direction, bool rows)
        {
            int limit = rows ? _sheet.RowCount - 1 : _sheet.ColumnCount - 1;
            if (direction < 0) return 0;
            return Math.Max(0, Math.Max(from, limit));
        }

        // ── Правка ячейки ────────────────────────────────────────────────

        private void BeginEdit(bool replace, string? initial, bool selectAll = false)
        {
            if (_editing) return;
            if ((RoleOf?.Invoke(_anchorRow, _anchorColumn) ?? ChartCellRole.Value) == ChartCellRole.Corner) return;

            // Правится клетка, с которой начали выделение, — выделение сводится к ней.
            _selectionKind = ChartGridHitKind.Cell;
            _activeRow = _anchorRow;
            _activeColumn = _anchorColumn;

            _editing = true;
            _editByTyping = replace;
            _editRow = _activeRow;
            _editColumn = _activeColumn;
            _editOriginal = _sheet.Get(_editRow, _editColumn);

            var role = RoleOf?.Invoke(_editRow, _editColumn) ?? ChartCellRole.Value;
            _editor.FontWeight = role == ChartCellRole.SeriesName ? FontWeight.SemiBold : FontWeight.Normal;
            _editor.TextAlignment = _editColumn > 0 ? TextAlignment.Center : TextAlignment.Left;
            _editor.HorizontalContentAlignment = role is ChartCellRole.Value or ChartCellRole.Size
                ? HorizontalAlignment.Right
                : HorizontalAlignment.Left;
            _editor.Text = replace ? initial ?? string.Empty : _editOriginal;
            _editor.Background = Brush("BgAppBrush", Color.FromRgb(0x1E, 0x1E, 0x1E));
            _editor.Foreground = Brush("TextPrimaryBrush", Colors.White);
            _editor.BorderBrush = Brush("AccentDefaultBrush", Color.FromRgb(0xD0, 0x8A, 0x3E));
            _editor.IsVisible = true;
            InvalidateArrange();
            InvalidateVisual();

            Dispatcher.UIThread.Post(() =>
            {
                if (!_editing) return;
                _editor.Focus();
                int length = _editor.Text?.Length ?? 0;
                _editor.CaretIndex = length;
                _editor.SelectionStart = selectAll ? 0 : length;
                _editor.SelectionEnd = length;
            }, DispatcherPriority.Input);
        }

        private void CommitEdit(int moveRow, int moveColumn, bool keepFocus)
        {
            if (!_editing) return;
            _editing = false;

            string text = _editor.Text ?? string.Empty;
            _editor.IsVisible = false;
            InvalidateArrange();

            if (!string.Equals(text, _editOriginal, StringComparison.Ordinal)
                && !(text.Length == 0 && (_editRow >= _sheet.RowCount || _editColumn >= _sheet.ColumnCount)))
            {
                BeforeChange?.Invoke();
                _sheet.Set(_editRow, _editColumn, text);
                InvalidateMeasure();
                Changed?.Invoke();
            }

            if (keepFocus) Focus();
            if (moveRow != 0 || moveColumn != 0) MoveActive(moveRow, moveColumn, extend: false);
            else InvalidateVisual();
        }

        private void CancelEdit()
        {
            if (!_editing) return;
            _editing = false;
            _editor.IsVisible = false;
            InvalidateArrange();
            InvalidateVisual();
            Focus();
        }

        private void OnEditorKeyDown(object? sender, KeyEventArgs e)
        {
            if (!_editing) return;
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

            switch (e.Key)
            {
                case Key.Enter:
                    CommitEdit(shift ? -1 : 1, 0, keepFocus: true);
                    break;
                case Key.Tab:
                    CommitEdit(0, shift ? -1 : 1, keepFocus: true);
                    break;
                case Key.Escape:
                    CancelEdit();
                    break;
                case Key.Up:
                    CommitEdit(-1, 0, keepFocus: true);
                    break;
                case Key.Down:
                    CommitEdit(1, 0, keepFocus: true);
                    break;
                case Key.Left when _editByTyping:
                    CommitEdit(0, -1, keepFocus: true);
                    break;
                case Key.Right when _editByTyping:
                    CommitEdit(0, 1, keepFocus: true);
                    break;
                default:
                    return;
            }

            e.Handled = true;
        }

        // ── Очистка и буфер обмена ───────────────────────────────────────

        /// <summary>Стирает текст выделенных ячеек.</summary>
        public void ClearSelection()
        {
            var (top, left, bottom, right) = Selection;
            bool any = false;
            for (int r = top; r <= Math.Min(bottom, _sheet.RowCount - 1); r++)
                for (int c = left; c <= Math.Min(right, _sheet.ColumnCount - 1); c++)
                    if (_sheet.Get(r, c).Length > 0) any = true;
            if (!any) return;

            BeforeChange?.Invoke();
            for (int r = top; r <= Math.Min(bottom, _sheet.RowCount - 1); r++)
                for (int c = left; c <= Math.Min(right, _sheet.ColumnCount - 1); c++)
                    _sheet.Set(r, c, string.Empty);
            Changed?.Invoke();
            InvalidateVisual();
        }

        private string SelectionAsText()
        {
            var (top, left, bottom, right) = Selection;
            bottom = Math.Min(bottom, _sheet.RowCount - 1);
            right = Math.Min(right, _sheet.ColumnCount - 1);

            var builder = new StringBuilder();
            for (int r = top; r <= bottom; r++)
            {
                for (int c = left; c <= right; c++)
                {
                    if (c > left) builder.Append('\t');
                    builder.Append(_sheet.Get(r, c).Replace('\t', ' ').Replace("\r", string.Empty).Replace('\n', ' '));
                }
                builder.Append("\r\n");
            }
            return builder.ToString();
        }

        /// <summary>Копирует выделение текстом через табуляцию; с cut — и очищает его.</summary>
        public async Task CopySelectionAsync(bool cut)
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null) return;

            string text = SelectionAsText();
            try
            {
                await clipboard.SetTextAsync(text);
            }
            catch
            {
                return;
            }

            if (cut) ClearSelection();
        }

        /// <summary>Вставляет текст из буфера с активной ячейки.</summary>
        public async Task PasteFromClipboardAsync()
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null) return;

            string? text;
            try
            {
#pragma warning disable CS0618
                text = await clipboard.TryGetTextAsync();
#pragma warning restore CS0618
            }
            catch
            {
                text = null;
            }
            if (string.IsNullOrEmpty(text)) return;

            PasteText(text);
        }

        /// <summary>
        /// Вставляет текст с табуляциями в лист с активной ячейки. Одно значение при
        /// выделенном диапазоне заполняет весь диапазон, как у Excel.
        /// </summary>
        public void PasteText(string text)
        {
            var table = ParseTable(text);
            if (table.Count == 0) return;

            var (top, left, bottom, right) = Selection;

            BeforeChange?.Invoke();

            if (table.Count == 1 && table[0].Count == 1 && (bottom > top || right > left))
            {
                for (int r = top; r <= bottom; r++)
                    for (int c = left; c <= right; c++)
                        _sheet.Set(r, c, table[0][0]);
            }
            else
            {
                int columns = 0;
                for (int r = 0; r < table.Count; r++)
                {
                    columns = Math.Max(columns, table[r].Count);
                    for (int c = 0; c < table[r].Count; c++)
                        _sheet.Set(top + r, left + c, table[r][c]);
                }
                bottom = top + table.Count - 1;
                right = left + Math.Max(1, columns) - 1;
            }

            InvalidateMeasure();
            Changed?.Invoke();
            SelectRange(top, left, bottom, right);
        }

        /// <summary>
        /// Таблица из текста буфера: строки по переводам строки, ячейки по табуляции.
        /// Ячейка в кавычках (так Excel кладёт текст с переводом строки) снимается с них.
        /// </summary>
        private static List<List<string>> ParseTable(string text)
        {
            var rows = new List<List<string>>();
            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            if (normalized.EndsWith('\n')) normalized = normalized[..^1];
            if (normalized.Length == 0) return rows;

            foreach (string line in normalized.Split('\n'))
            {
                var cells = new List<string>();
                foreach (string raw in line.Split('\t'))
                {
                    string cell = raw;
                    if (cell.Length >= 2 && cell[0] == '"' && cell[^1] == '"')
                        cell = cell[1..^1].Replace("\"\"", "\"");
                    cells.Add(cell.Trim());
                }
                rows.Add(cells);
            }
            return rows;
        }
    }
}
