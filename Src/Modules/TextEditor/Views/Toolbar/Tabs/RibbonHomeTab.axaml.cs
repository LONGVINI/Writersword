using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System;
using Writersword.Modules.TextEditor.ViewModels.Toolbar;
using Writersword.Styles.UserControls;

namespace Writersword.Modules.TextEditor.Views.Toolbar.Tabs
{
    public partial class RibbonHomeTab : UserControl
    {
        private RibbonScrollContainer? _scrollContainer;
        private ListBox? _fontSizeList;
        private AutoCompleteBox? _fontAutoComplete;
        private TextBox? _fontInnerTextBox;
        private ListBox? _fontInnerList;
        private bool _fontScrolling;
        private string? _fontBeforeOpen;
        private string? _fontHovered;
        // Шрифт, выбранный пользователем: элемент под курсором в момент нажатия (клик) или
        // выбранный с клавиатуры. Нажатие ловим в фазе tunnel, до bubble, где Dock роняет
        // pointer-pressed (DockControl.PressedHandler) и съедает клик. Не сбрасывается при уводе
        // мыши со списка; сбрасывается при открытии, после коммита и Esc.
        private string? _fontChosen;
        // Пользователь нажал Escape в открытом дропдауне — отмена выбора.
        private bool _fontCancelled;
        private DispatcherTimer? _fontPreviewTimer;

        public RibbonHomeTab()
        {
            InitializeComponent();
            SizeChanged += OnSizeChanged;
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            AttachControls();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            DetachControls();
        }

        private void AttachControls()
        {
            _scrollContainer = this.FindControl<RibbonScrollContainer>("ScrollContainer");

            _fontSizeList = this.FindControl<ListBox>("FontSizeListBox");
            if (_fontSizeList is not null)
            {
                _fontSizeList.SelectionChanged -= OnFontSizeListSelectionChanged;
                _fontSizeList.SelectionChanged += OnFontSizeListSelectionChanged;
            }

            _fontAutoComplete = this.FindControl<AutoCompleteBox>("FontAutoComplete");
            if (_fontAutoComplete is not null)
            {
                _fontAutoComplete.TemplateApplied -= OnFontAutoCompleteTemplateApplied;
                _fontAutoComplete.TemplateApplied += OnFontAutoCompleteTemplateApplied;
                _fontAutoComplete.DropDownOpened -= OnFontDropDownOpened;
                _fontAutoComplete.DropDownOpened += OnFontDropDownOpened;
                _fontAutoComplete.DropDownClosed -= OnFontDropDownClosed;
                _fontAutoComplete.DropDownClosed += OnFontDropDownClosed;

                _fontAutoComplete.PointerReleased -= OnFontAutoCompletePointerReleased;
                _fontAutoComplete.PointerReleased += OnFontAutoCompletePointerReleased;

                // При повторном attach (после detach из-за перестроек с таблицей/сменой
                // документа) шаблон заново не применяется и TemplateApplied не срабатывает —
                // подписки на внутренние части теряются (клик не открывал список, выбор не
                // применялся). Отложенно восстанавливаем их по реализованному шаблону.
                Dispatcher.UIThread.Post(EnsureInnerWired, DispatcherPriority.Loaded);
            }
        }

        private void DetachControls()
        {
            if (_fontSizeList is not null)
                _fontSizeList.SelectionChanged -= OnFontSizeListSelectionChanged;

            if (_fontAutoComplete is not null)
            {
                _fontAutoComplete.TemplateApplied -= OnFontAutoCompleteTemplateApplied;
                _fontAutoComplete.DropDownOpened -= OnFontDropDownOpened;
                _fontAutoComplete.DropDownClosed -= OnFontDropDownClosed;
                _fontAutoComplete.PointerReleased -= OnFontAutoCompletePointerReleased;
            }

            DetachInnerControls();
        }

        private void DetachInnerControls()
        {
            _fontPreviewTimer?.Stop();
            if (_fontPreviewTimer is not null)
            {
                _fontPreviewTimer.Tick -= OnPreviewTimerTick;
                _fontPreviewTimer = null;
            }

            if (_fontInnerTextBox is not null)
            {
                _fontInnerTextBox.PointerReleased -= OnFontInnerPointerReleased;
                _fontInnerTextBox.KeyDown -= OnFontInnerKeyDown;
            }

            if (_fontInnerList is not null)
            {
                _fontInnerList.SelectionChanged -= OnFontInnerListSelectionChanged;
                _fontInnerList.PointerMoved -= OnFontInnerListPointerMoved;
                _fontInnerList.PointerExited -= OnFontInnerListPointerExited;
                _fontInnerList.PointerReleased -= OnFontInnerListPointerReleased;
            }

            // Обнуляем ссылки: при повторном attach (после detach из-за перестроек, связанных
            // с таблицей/сменой документа) шаблон не применяется заново, и EnsureInnerWired
            // по null-ссылкам понимает, что внутренние части надо найти и переподписать.
            _fontInnerTextBox = null;
            _fontInnerList = null;
        }

        private void OnFontAutoCompleteTemplateApplied(object? sender, TemplateAppliedEventArgs e)
        {
            DetachInnerControls();
            _fontInnerTextBox = e.NameScope.Find<TextBox>("PART_TextBox");
            _fontInnerList = e.NameScope.Find<ListBox>("PART_SelectingItemsControl");
            SubscribeInner();
        }

        // Идемпотентная подписка на внутренние части (сначала снимаем, потом вешаем) — можно
        // звать и из TemplateApplied, и из EnsureInnerWired при повторном attach.
        private void SubscribeInner()
        {
            if (_fontInnerTextBox is not null)
            {
                _fontInnerTextBox.MinHeight = 0;
                _fontInnerTextBox.Height = 22;
                _fontInnerTextBox.VerticalContentAlignment = VerticalAlignment.Center;
                _fontInnerTextBox.Padding = new Thickness(6, 0);

                _fontInnerTextBox.PointerReleased -= OnFontInnerPointerReleased;
                _fontInnerTextBox.PointerReleased += OnFontInnerPointerReleased;
                _fontInnerTextBox.KeyDown -= OnFontInnerKeyDown;
                _fontInnerTextBox.KeyDown += OnFontInnerKeyDown;
            }

            if (_fontInnerList is not null)
            {
                _fontInnerList.SelectionChanged -= OnFontInnerListSelectionChanged;
                _fontInnerList.SelectionChanged += OnFontInnerListSelectionChanged;
                _fontInnerList.PointerMoved -= OnFontInnerListPointerMoved;
                _fontInnerList.PointerMoved += OnFontInnerListPointerMoved;
                _fontInnerList.PointerExited -= OnFontInnerListPointerExited;
                _fontInnerList.PointerExited += OnFontInnerListPointerExited;
                _fontInnerList.PointerReleased -= OnFontInnerListPointerReleased;
                _fontInnerList.PointerReleased += OnFontInnerListPointerReleased;
                // Нажатие ловим в tunnel: bubble того же события перехватывает DockControl и
                // роняет его (PointToScreen на контроле вне визуального дерева), из-за чего клик
                // по элементу теряется. Tunnel идёт раньше bubble, поэтому выбор успеваем снять.
                _fontInnerList.RemoveHandler(InputElement.PointerPressedEvent, OnFontInnerListPointerPressedTunnel);
                _fontInnerList.AddHandler(InputElement.PointerPressedEvent, OnFontInnerListPointerPressedTunnel, RoutingStrategies.Tunnel);
            }
        }

        // Восстанавливает подписки на внутренние части, если они потерялись после повторного
        // attach (шаблон AutoCompleteBox при этом заново не применяется, TemplateApplied не
        // срабатывает). Ищет PART_TextBox/PART_SelectingItemsControl в реализованном шаблоне.
        private void EnsureInnerWired()
        {
            if (_fontAutoComplete is null) return;
            if (_fontInnerTextBox is not null && _fontInnerList is not null) return;

            TextBox? tb = _fontInnerTextBox;
            ListBox? lb = _fontInnerList;
            foreach (var v in _fontAutoComplete.GetVisualDescendants())
            {
                if (tb is null && v is TextBox t && t.Name == "PART_TextBox") tb = t;
                else if (lb is null && v is ListBox l && l.Name == "PART_SelectingItemsControl") lb = l;
                if (tb is not null && lb is not null) break;
            }

            if (tb is null && lb is null) return;

            _fontInnerTextBox = tb;
            _fontInnerList = lb;
            SubscribeInner();
        }

        // ── TextBox события ───────────────────────────────────────────────

        private void OnFontAutoCompletePointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            e.Handled = true;
        }

        private void OnFontInnerPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            // Не открываем если дропдаун уже открыт.
            if (_fontAutoComplete?.IsDropDownOpen == true) return;

            Dispatcher.UIThread.Post(() =>
            {
                if (DataContext is RibbonHomeTabViewModel vm)
                    _fontBeforeOpen = vm.CurrentFontFamily;
                if (_fontInnerTextBox is not null)
                    _fontInnerTextBox.Text = string.Empty;
                if (_fontAutoComplete is not null)
                    _fontAutoComplete.IsDropDownOpen = true;
            }, DispatcherPriority.Background);
        }

        private void OnFontInnerKeyDown(object? sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Up:
                case Key.Down:
                case Key.Enter:
                    // Навигация и подтверждение с клавиатуры. Выбор фиксируется в SelectionChanged
                    // (стрелки меняют SelectedItem), отдельный флаг не нужен.
                    return;
                case Key.Escape:
                    _fontCancelled = true;
                    return;
                case Key.Tab:
                case Key.Left:
                case Key.Right:
                case Key.Home:
                case Key.End:
                    return;
                default:
                    break;
            }
        }

        // ── Preview по списку ─────────────────────────────────────────────

        private void EnsurePreviewTimer()
        {
            if (_fontPreviewTimer is not null) return;
            _fontPreviewTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(60)
            };
            _fontPreviewTimer.Tick += OnPreviewTimerTick;
        }

        private void OnPreviewTimerTick(object? sender, EventArgs e)
        {
            _fontPreviewTimer?.Stop();
            if (_fontHovered is not null && DataContext is RibbonHomeTabViewModel vm)
                vm.PreviewFontFamily(_fontHovered);
        }

        private void SchedulePreview(string font)
        {
            _fontHovered = font;
            EnsurePreviewTimer();
            _fontPreviewTimer!.Stop();
            _fontPreviewTimer.Start();
        }

        private void StopPreviewTimer()
        {
            _fontPreviewTimer?.Stop();
        }

        private void OnFontInnerListSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            // Пропускаем программную прокрутку при открытии дропдауна.
            if (_fontScrolling) return;
            if (_fontInnerList?.SelectedItem is string font)
            {
                _fontChosen = font;
                SchedulePreview(font);
            }
        }

        private void OnFontInnerListPointerMoved(object? sender, PointerEventArgs e)
        {
            if (_fontInnerList is null) return;
            var pos = e.GetPosition(_fontInnerList);
            var hit = _fontInnerList.InputHitTest(pos) as Control;
            if (hit is null) return;

            var lbi = (hit as ListBoxItem) ?? hit.FindAncestorOfType<ListBoxItem>();
            if (lbi?.DataContext is not string font || font == _fontHovered) return;

            _fontHovered = font;
            SchedulePreview(font);
        }

        private void OnFontInnerListPointerPressedTunnel(object? sender, PointerPressedEventArgs e)
        {
            if (_fontInnerList is null) return;
            var pos = e.GetPosition(_fontInnerList);
            var hit = _fontInnerList.InputHitTest(pos) as Control;
            if (hit is null) return;

            var lbi = (hit as ListBoxItem) ?? hit.FindAncestorOfType<ListBoxItem>();
            if (lbi?.DataContext is not string font) return;

            // Нажатие по элементу = выбор. Фиксируем здесь, в tunnel, до того как bubble дойдёт
            // до DockControl и упадёт, потеряв клик. Превью обновляем сразу, чтобы видеть выбор.
            _fontChosen = font;
            SchedulePreview(font);
        }

        private void OnFontInnerListPointerExited(object? sender, PointerEventArgs e)
        {
            StopPreviewTimer();
            _fontHovered = null;
            // _fontChosen намеренно не сбрасываем: при закрытии дропдауна (в т.ч. по клику) сюда
            // приходит exited и откатил бы выбор. Откатываем только визуальное превью на исходный.
            if (DataContext is RibbonHomeTabViewModel vm && _fontBeforeOpen is not null)
                vm.PreviewFontFamily(_fontBeforeOpen);
        }

        private void OnFontInnerListPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            // Запасной захват выбора: там, где Dock не роняет событие, отпускание над элементом
            // тоже фиксирует выбор (основной путь — tunnel-нажатие и SelectionChanged).
            if (_fontInnerList is null) return;
            var pos = e.GetPosition(_fontInnerList);
            var hit = _fontInnerList.InputHitTest(pos) as Control;
            if (hit is null) return;

            var lbi = (hit as ListBoxItem) ?? hit.FindAncestorOfType<ListBoxItem>();
            if (lbi?.DataContext is string font)
                _fontChosen = font;
        }

        // ── Дропдаун ─────────────────────────────────────────────────────

        private void OnFontDropDownOpened(object? sender, EventArgs e)
        {
            if (DataContext is not RibbonHomeTabViewModel vm) return;
            _fontBeforeOpen ??= vm.CurrentFontFamily;
            _fontHovered = null;
            _fontChosen = null;
            _fontCancelled = false;
            // BeginFontPreview всегда вызывается здесь — это единственное надёжное место.
            // OnFontInnerPointerReleased не гарантирует вызов если бокс уже был в фокусе.
            vm.BeginFontPreview();

            if (_fontScrolling) return;
            _fontScrolling = true;

            string? current = _fontBeforeOpen;
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (_fontInnerList is null || current is null) return;
                    _fontInnerList.SelectedItem = current;
                    _fontInnerList.ScrollIntoView(current);
                }
                finally { _fontScrolling = false; }
            }, DispatcherPriority.Background);
        }

        private void OnFontDropDownClosed(object? sender, EventArgs e)
        {
            StopPreviewTimer();

            if (DataContext is not RibbonHomeTabViewModel vm) return;

            // Коммитим выбранный шрифт. Клик по элементу съедается доком (bubble pointer-pressed
            // роняет DockControl.PressedHandler), поэтому ловим выбор иначе: _fontChosen снят в
            // tunnel-нажатии или из SelectionChanged (клавиатура) и переживает exited. Esc отменяет.
            bool committed = !_fontCancelled && _fontChosen is not null;
            if (committed)
                // Увод мыши при закрытии откатил превью на исходный шрифт — возвращаем выбранный,
                // чтобы EndFontPreview применил именно его.
                vm.PreviewFontFamily(_fontChosen!);
            _fontChosen = null;
            _fontCancelled = false;
            vm.EndFontPreview(committed);

            string restore = vm.CurrentFontFamily ?? _fontBeforeOpen ?? string.Empty;
            if (_fontInnerTextBox is not null && _fontInnerTextBox.Text != restore)
                _fontInnerTextBox.Text = restore;

            _fontBeforeOpen = null;
            _fontHovered = null;
        }

        // ── Ribbon resize ─────────────────────────────────────────────────

        private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (DataContext is RibbonHomeTabViewModel vm)
            {
                vm.UpdateLayout(e.NewSize.Width);
                if (_scrollContainer is not null)
                    _scrollContainer.ArrowsVisible = !vm.IsClipboardGroupExpanded;
            }
            _scrollContainer?.NotifySizeChanged();
        }

        // ── FontSize list ─────────────────────────────────────────────────

        private void OnFontSizeListSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (sender is not ListBox lb) return;
            if (lb.SelectedItem is not string sizeStr) return;
            if (DataContext is RibbonHomeTabViewModel vm)
                vm.SelectFontSizeCommand.Execute(sizeStr);
            lb.SelectedItem = null;
        }
    }
}