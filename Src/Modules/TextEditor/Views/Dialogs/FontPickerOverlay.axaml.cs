using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Writersword.Modules.TextEditor.Services;

namespace Writersword.Modules.TextEditor.Views.Dialogs
{
    /// <summary>
    /// Выбор гарнитуры. Список живёт внутри модуля, а не во всплывающем окне.
    ///
    /// Всплывающее окно здесь и было источником бед. У его содержимого не
    /// определяется визуальный корень, отчего Avalonia отдаёт Dock нулевые
    /// координаты нажатия; Dock хит-тестит левый верхний угол раскладки, находит
    /// там тело вкладки документа и начинает перетаскивание. Плюс светлое
    /// перекрытие главного окна перехватывало нажатие раньше пункта списка, и
    /// выбор приходилось вылавливать захватом указателя и отложенным решением.
    /// Обычный контрол в сетке модуля не имеет ни того, ни другого: нажатия,
    /// выделение и клавиатура работают штатно.
    ///
    /// Наверху списка идут закреплённые и недавние гарнитуры — их помнит
    /// FontUsage. Разделы собираются заново на каждое закрепление и на каждый
    /// набор в поле поиска.
    ///
    /// Предпросмотр отдаётся наружу обратным вызовом и приходит с задержкой:
    /// пробег мыши по списку иначе перекладывал бы рукопись на каждый пункт.
    /// </summary>
    public partial class FontPickerOverlay : UserControl
    {
        private const double PreviewDelayMs = 200;
        private const double PanelGap = 2;
        private const double ViewportMargin = 8;
        private const double MinPanelHeight = 140;

        private const string SectionPinned = "ЗАКРЕПЛЁННЫЕ";
        private const string SectionRecent = "НЕДАВНИЕ";
        private const string SectionAll = "ВСЕ ШРИФТЫ";

        private TaskCompletionSource<string?>? _tcs;

        private Border _scrim = null!;
        private Border _panel = null!;
        private TextBox _searchBox = null!;
        private TextBlock _emptyLabel = null!;
        private ListBox _fontList = null!;

        private IReadOnlyList<string> _allFonts = Array.Empty<string>();
        private Action<string>? _preview;
        private string? _current;
        private string? _pendingPreview;
        private DispatcherTimer? _previewTimer;

        public FontPickerOverlay()
        {
            InitializeComponent();
            IsVisible = false;

            _scrim = this.FindControl<Border>("Scrim")!;
            _panel = this.FindControl<Border>("Panel")!;
            _searchBox = this.FindControl<TextBox>("SearchBox")!;
            _emptyLabel = this.FindControl<TextBlock>("EmptyLabel")!;
            _fontList = this.FindControl<ListBox>("FontList")!;

            _scrim.PointerPressed += OnScrimPressed;
            _searchBox.TextChanged += OnSearchTextChanged;
            _searchBox.KeyDown += OnSearchKeyDown;
            _fontList.SelectionChanged += OnSelectionChanged;
            _fontList.PointerMoved += OnListPointerMoved;
            _fontList.PointerReleased += OnListPointerReleased;
            _fontList.KeyDown += OnListKeyDown;
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            TopLevel.GetTopLevel(this)?.AddHandler(KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            TopLevel.GetTopLevel(this)?.RemoveHandler(KeyDownEvent, OnGlobalKeyDown);
            StopPreviewTimer();
            base.OnDetachedFromVisualTree(e);
        }

        /// <summary>
        /// Показать список под указанным элементом ленты.
        /// </summary>
        /// <param name="fonts">Гарнитуры для показа.</param>
        /// <param name="current">Гарнитура под курсором — выделяется при открытии.</param>
        /// <param name="anchor">Элемент ленты, под которым встаёт панель.</param>
        /// <param name="preview">Показать гарнитуру в рукописи, не применяя её.</param>
        /// <returns>Выбранная гарнитура или null при отмене.</returns>
        public Task<string?> ShowAsync(
            IReadOnlyList<string> fonts,
            string? current,
            Control anchor,
            Action<string> preview)
        {
            _tcs?.TrySetResult(null);
            _tcs = new TaskCompletionSource<string?>();

            _allFonts = fonts ?? (IReadOnlyList<string>)Array.Empty<string>();
            _preview = preview;
            _current = current;

            _fontList.ItemsSource = null;
            _searchBox.Text = string.Empty;
            RebuildItems();

            PositionUnder(anchor);
            IsVisible = true;

            // Прокрутка к выделенному пункту и фокус ставятся после раскладки:
            // до неё у списка нет ни высоты, ни созданных контейнеров, и
            // ScrollIntoView уходит в никуда.
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsVisible) return;
                _searchBox.Focus();
                ScrollToSelected();
            }, DispatcherPriority.Loaded);

            return _tcs.Task;
        }

        // ── Размещение ────────────────────────────────────────────────────

        /// <summary>
        /// Ставит панель под элементом ленты.
        ///
        /// Координаты считаются относительно родителя оверлея, а не самого
        /// оверлея: пока он скрыт, своей раскладки у него нет и Bounds пусты.
        /// Оверлей растянут на все строки сетки модуля, поэтому начала координат
        /// у него и у родителя совпадают.
        /// </summary>
        private void PositionUnder(Control anchor)
        {
            if (this.GetVisualParent() is not Visual host)
                return;

            var origin = anchor.TranslatePoint(new Point(0, anchor.Bounds.Height + PanelGap), host);
            if (origin is not { } point)
                return;

            double maxX = Math.Max(ViewportMargin, host.Bounds.Width - _panel.Width - ViewportMargin);
            double x = Math.Clamp(point.X, ViewportMargin, maxX);
            double y = Math.Max(ViewportMargin, point.Y);

            // Высота ограничивается остатком окна: без этого длинный список
            // уезжает под нижний край модуля и последние гарнитуры недостижимы.
            double available = host.Bounds.Height - y - ViewportMargin;
            _panel.MaxHeight = Math.Max(MinPanelHeight, available);
            _panel.Margin = new Thickness(x, y, 0, 0);
        }

        // ── Сборка списка ─────────────────────────────────────────────────

        /// <summary>
        /// Пересобирает разделы под нынешний поиск и нынешние закрепления.
        ///
        /// Выделение переносится по имени гарнитуры. Список без выделения не
        /// отзывается на стрелки, и набор имени пришлось бы завершать мышью.
        /// </summary>
        private void RebuildItems()
        {
            string query = (_searchBox.Text ?? string.Empty).Trim();
            string? keepName = (_fontList.SelectedItem as FontEntry)?.Name ?? _current;

            var known = new HashSet<string>(_allFonts, StringComparer.OrdinalIgnoreCase);
            var pinnedSet = new HashSet<string>(FontUsage.Pinned, StringComparer.OrdinalIgnoreCase);

            List<string> pinned = FontUsage.Pinned
                .Where(f => known.Contains(f) && Matches(f, query))
                .ToList();

            List<string> recent = FontUsage.Recent
                .Where(f => known.Contains(f) && !pinnedSet.Contains(f) && Matches(f, query))
                .ToList();

            List<string> all = _allFonts
                .Where(f => Matches(f, query))
                .ToList();

            // Заголовки нужны только когда разделов больше одного. Пока человек
            // ничего не закрепил и ничем не пользовался, «ВСЕ ШРИФТЫ» над полным
            // списком не сообщает ничего.
            bool grouped = pinned.Count > 0 || recent.Count > 0;

            var items = new List<FontEntry>(pinned.Count + recent.Count + all.Count);
            AppendSection(items, pinned, grouped ? SectionPinned : null, pinnedSet);
            AppendSection(items, recent, grouped ? SectionRecent : null, pinnedSet);
            AppendSection(items, all, grouped ? SectionAll : null, pinnedSet);

            _fontList.ItemsSource = items;
            _fontList.IsVisible = items.Count > 0;
            _emptyLabel.IsVisible = items.Count == 0;

            FontEntry? select = items.FirstOrDefault(
                                    i => string.Equals(i.Name, keepName, StringComparison.OrdinalIgnoreCase))
                                ?? items.FirstOrDefault();

            _fontList.SelectedItem = select;
        }

        private static bool Matches(string family, string query) =>
            query.Length == 0 || family.Contains(query, StringComparison.OrdinalIgnoreCase);

        private static void AppendSection(
            List<FontEntry> items,
            List<string> names,
            string? title,
            HashSet<string> pinnedSet)
        {
            for (int i = 0; i < names.Count; i++)
                items.Add(new FontEntry(names[i], pinnedSet.Contains(names[i]), i == 0 ? title : null));
        }

        private void ScrollToSelected()
        {
            if (_fontList.SelectedItem is FontEntry entry)
                _fontList.ScrollIntoView(entry);
        }

        private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
        {
            RebuildItems();
        }

        // ── Закрепление ───────────────────────────────────────────────────

        /// <summary>
        /// Звёздочка. Закрепление переносит гарнитуру в верхний раздел, поэтому
        /// список пересобирается, а выделение уезжает туда же вслед за ней.
        /// </summary>
        private void OnPinClick(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.DataContext is not FontEntry entry)
                return;

            FontUsage.TogglePin(entry.Name);
            RebuildItems();
            ScrollToSelected();

            e.Handled = true;
        }

        // ── Предпросмотр ──────────────────────────────────────────────────

        private void SchedulePreview(string font)
        {
            if (_preview is null) return;
            if (string.Equals(font, _pendingPreview, StringComparison.Ordinal)) return;

            _pendingPreview = font;

            _previewTimer ??= CreatePreviewTimer();
            _previewTimer.Stop();
            _previewTimer.Start();
        }

        private DispatcherTimer CreatePreviewTimer()
        {
            var timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(PreviewDelayMs)
            };
            timer.Tick += OnPreviewTick;
            return timer;
        }

        private void OnPreviewTick(object? sender, EventArgs e)
        {
            _previewTimer?.Stop();
            if (_pendingPreview is { } font)
                _preview?.Invoke(font);
        }

        private void StopPreviewTimer()
        {
            _previewTimer?.Stop();
            _pendingPreview = null;
        }

        // ── Ввод ──────────────────────────────────────────────────────────

        private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_fontList.SelectedItem is FontEntry entry)
                SchedulePreview(entry.Name);
        }

        private void OnListPointerMoved(object? sender, PointerEventArgs e)
        {
            if (EntryFromSource(e.Source) is { } entry)
                SchedulePreview(entry.Name);
        }

        private void OnListPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            if (EntryFromSource(e.Source) is not { } entry) return;

            Complete(entry.Name);
            e.Handled = true;
        }

        private void OnListKeyDown(object? sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter:
                    CommitSelected();
                    e.Handled = true;
                    break;
                case Key.Escape:
                    Complete(null);
                    e.Handled = true;
                    break;
            }
        }

        /// <summary>
        /// Стрелки в поле поиска ведут по списку, не выходя из набора: человек
        /// печатает часть имени и тут же доводит выбор клавишами.
        /// </summary>
        private void OnSearchKeyDown(object? sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Down:
                    MoveSelection(1);
                    e.Handled = true;
                    break;
                case Key.Up:
                    MoveSelection(-1);
                    e.Handled = true;
                    break;
                case Key.PageDown:
                    MoveSelection(10);
                    e.Handled = true;
                    break;
                case Key.PageUp:
                    MoveSelection(-10);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    CommitSelected();
                    e.Handled = true;
                    break;
                case Key.Escape:
                    Complete(null);
                    e.Handled = true;
                    break;
            }
        }

        private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
        {
            if (!IsVisible) return;
            if (e.Key != Key.Escape) return;

            Complete(null);
            e.Handled = true;
        }

        private void OnScrimPressed(object? sender, PointerPressedEventArgs e)
        {
            Complete(null);
            e.Handled = true;
        }

        private void MoveSelection(int delta)
        {
            if (_fontList.ItemCount == 0) return;

            int index = Math.Clamp(_fontList.SelectedIndex + delta, 0, _fontList.ItemCount - 1);
            _fontList.SelectedIndex = index;

            ScrollToSelected();
        }

        private void CommitSelected()
        {
            if (_fontList.SelectedItem is FontEntry entry)
                Complete(entry.Name);
            else
                Complete(null);
        }

        private static FontEntry? EntryFromSource(object? source)
        {
            Visual? visual = source as Visual;
            while (visual is not null)
            {
                if (visual is ListBoxItem item && item.DataContext is FontEntry entry)
                    return entry;

                visual = visual.GetVisualParent();
            }

            return null;
        }

        // ── Завершение ────────────────────────────────────────────────────

        private void Complete(string? result)
        {
            StopPreviewTimer();

            IsVisible = false;
            _fontList.ItemsSource = null;
            _allFonts = Array.Empty<string>();
            _preview = null;
            _current = null;

            var tcs = _tcs;
            _tcs = null;
            tcs?.TrySetResult(result);
        }
    }
}
