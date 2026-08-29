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
using System.Linq;
using Writersword.Modules.TextEditor.Document;
using Writersword.Modules.TextEditor.Models.Styles;
using Writersword.Modules.TextEditor.ViewModels;
using Writersword.Modules.TextEditor.ViewModels.Toolbar;
using Writersword.Modules.TextEditor.Views;
using Writersword.Modules.TextEditor.Views.Dialogs;
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
                _fontAutoComplete.LostFocus -= OnFontAutoCompleteLostFocus;
                _fontAutoComplete.LostFocus += OnFontAutoCompleteLostFocus;

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
                _fontAutoComplete.LostFocus -= OnFontAutoCompleteLostFocus;
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

        /// <summary>
        /// Фокус ушёл с поля шрифта — возвращаем то, что было.
        ///
        /// Своей страховки тут раньше не было вовсе: восстановление держалось на
        /// том, что AutoCompleteBox сам закроет список и пришлёт DropDownClosed.
        /// Цепочка длинная и чужая, а поле к этому моменту уже очищено — если
        /// событие не придёт, оно так и останется пустым, и пустота уедет во
        /// вьюмодель через двустороннюю привязку.
        ///
        /// Уход фокуса без выбора — это отказ, а не выбор: возвращаем исходную
        /// гарнитуру и закрываем список сами.
        /// </summary>
        private void OnFontAutoCompleteLostFocus(object? sender, RoutedEventArgs e)
        {
            if (_fontAutoComplete is null) return;

            // Список ещё открыт — закрываем; DropDownClosed доделает остальное.
            if (_fontAutoComplete.IsDropDownOpen)
            {
                _fontAutoComplete.IsDropDownOpen = false;
                return;
            }

            // Список уже закрыт, а поле пустое — значит очистка при открытии не
            // была отменена. Возвращаем текущую гарнитуру.
            if (DataContext is not RibbonHomeTabViewModel vm) return;

            string restore = vm.CurrentFontFamily ?? _fontBeforeOpen ?? string.Empty;
            if (_fontInnerTextBox is not null && _fontInnerTextBox.Text != restore)
                _fontInnerTextBox.Text = restore;
        }

        private void OnFontAutoCompletePointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            // Захват, взятый при выборе пункта списка, снимается здесь: отпускание
            // дошло, состояние указателя закрыто.
            Serilog.Log.ForContext("SourceContext", "FontDropdown")
                .Debug("released over the font box, capture was held by {Cap}",
                    e.Pointer.Captured?.GetType().Name ?? "нет");

            if (ReferenceEquals(e.Pointer.Captured, _fontAutoComplete))
                e.Pointer.Capture(null);
            _capturedPointer = null;

            e.Handled = true;
        }

        private void OnFontInnerPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            // Не открываем если дропдаун уже открыт.
            if (_fontAutoComplete?.IsDropDownOpen == true) return;

            Dispatcher.UIThread.Post(() =>
            {
                // Очистка поля живёт в OnFontDropDownOpened — там сходятся все пути
                // открытия. Здесь её быть не должно: с клавиатуры список открывается
                // мимо этого метода, и имя гарнитуры оставалось в поле. А оно ещё и
                // фильтр: FilterMode="Contains" по нему отсекал список до пары пунктов.
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
                    // Только показ. Выбор не фиксируем: человек ещё выбирает.
                    return;

                case Key.Enter:
                    // Подтверждение с клавиатуры — вот теперь выбор состоялся.
                    if (_fontInnerList?.SelectedItem is string chosen)
                        _fontChosen = chosen;
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
                Interval = TimeSpan.FromMilliseconds(200)
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

            // Стрелки только ПОКАЗЫВАЮТ шрифт, но не выбирают его.
            //
            // Раньше здесь выставлялся _fontChosen, и любая смена выделения в списке
            // считалась выбором. Из-за этого «открыл, поводил стрелками, передумал,
            // кликнул мимо» применяло шрифт и клало правку в стек отмены. Выбор
            // фиксируют только Enter и щелчок по пункту — они пишут _fontChosen сами.
            if (_fontInnerList?.SelectedItem is string font)
                SchedulePreview(font);
        }

        /// <summary>
        /// Шрифт под указателем — по источнику события, а не хит-тестом.
        ///
        /// InputHitTest по списку возвращал null для нажатий по пунктам: список
        /// живёт в отдельном окне-всплывашке со своей системой координат, и
        /// пересчёт позиции туда не попадал. Обработчик из-за этого молча выходил,
        /// нажатие уходило дальше в Dock, тот начинал перетаскивание и накрывал
        /// окно своей мишенью GlobalDockTarget — ввод переставал доходить куда бы
        /// то ни было.
        ///
        /// e.Source указывает прямо на элемент под указателем, пересчитывать
        /// ничего не нужно.
        /// </summary>
        private static string? FontFromSource(object? source)
        {
            if (source is not Control control) return null;

            var item = (control as ListBoxItem) ?? control.FindAncestorOfType<ListBoxItem>();
            return item?.DataContext as string;
        }

        private void OnFontInnerListPointerMoved(object? sender, PointerEventArgs e)
        {
            if (FontFromSource(e.Source) is not string font || font == _fontHovered) return;

            _fontHovered = font;
            SchedulePreview(font);
        }

        private void OnFontInnerListPointerPressedTunnel(object? sender, PointerPressedEventArgs e)
        {
            var probe = Serilog.Log.ForContext("SourceContext", "FontDropdown");

            // Указатель захватывается САМЫМ ПЕРВЫМ и всегда — до того, как мы вообще
            // попытались понять, по какому пункту нажали.
            //
            // Захват решает две задачи разом. Первая: отпускание не теряется. Выбор
            // пункта закрывает список, всплывающее окно вместе с пунктом
            // уничтожается, и PointerReleased доставлять было бы некому — для
            // Avalonia кнопка мыши осталась бы нажатой навсегда. Поле шрифта живёт в
            // ленте и закрытие списка переживает, поэтому отпускание дойдёт до него.
            //
            // Вторая: нажатие не уходит в Dock. Иначе тот считает его началом
            // перетаскивания и накрывает окно мишенью GlobalDockTarget — плоскость
            // модулей перестаёт принимать ввод при живых меню и кнопках воркмодов,
            // и в воздухе повисает призрак вкладки.
            //
            // Безусловность здесь принципиальна. Источником события приходит и
            // оторванный от дерева TextBlock — у переработанного пункта списка
            // предков уже нет, опознать по нему ничего нельзя. Раньше на таком
            // нажатии обработчик выходил, не взяв захват, и всё ломалось: клик по
            // такому пункту и был тем самым «сломался как и тогда».
            if (_fontAutoComplete is not null)
            {
                e.Pointer.Capture(_fontAutoComplete);
                _capturedPointer = e.Pointer;
            }

            if (FontFromSource(e.Source) is not string font)
            {
                // Пункт не опознан — не беда: выбор возьмёт SelectionChanged, когда
                // список сам выделит элемент. Захват уже взят, ломаться нечему.
                probe.Debug("pressed in the list: item not recognized, source {Src}; "
                    + "захват взят, выбор возьмёт SelectionChanged",
                    e.Source?.GetType().Name ?? "нет");
                return;
            }

            probe.Debug("pressed in the list: item {Font}, capture held by {Cap}",
                font, e.Pointer.Captured?.GetType().Name ?? "нет");

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
            if (FontFromSource(e.Source) is string font)
                _fontChosen = font;
        }

        // ── Дропдаун ─────────────────────────────────────────────────────

        /// <summary>
        /// Снять захват указателя, если он всё ещё на поле шрифта.
        ///
        /// Страховка на случай, когда список закрылся не через отпускание — Esc,
        /// потеря фокуса, закрытие извне. Без неё захват пережил бы список, и ввод
        /// снова уходил бы в пустоту.
        /// </summary>
        private void ReleaseFontPointerCapture()
        {
            var pointer = _capturedPointer;
            _capturedPointer = null;

            if (pointer is null || _fontAutoComplete is null) return;
            if (ReferenceEquals(pointer.Captured, _fontAutoComplete))
                pointer.Capture(null);
        }

        private IPointer? _capturedPointer;

        private void OnFontDropDownOpened(object? sender, EventArgs e)
        {
            if (DataContext is not RibbonHomeTabViewModel vm) return;
            _fontBeforeOpen ??= vm.CurrentFontFamily;

            // Поле очищается при открытии списка, каким бы путём оно ни открылось —
            // мышью по полю или стрелками с клавиатуры. Раньше очистка стояла в
            // OnFontInnerPointerReleased, и открытие с клавиатуры проходило мимо неё:
            // имя гарнитуры оставалось в поле и работало фильтром — FilterMode
            // "Contains" отсекал список до пары пунктов.
            //
            // Набранный текст при этом трогать нельзя. Ввод символа тоже открывает
            // список, и очистка здесь стёрла бы то, что человек только что напечатал,
            // сделав поиск по имени невозможным. Отличаем одно от другого по тому,
            // совпадает ли содержимое поля с нынешней гарнитурой: совпадает — поле не
            // трогали, чистим; не совпадает — там набор, оставляем как есть.
            if (_fontInnerTextBox is not null
                && string.Equals(_fontInnerTextBox.Text ?? string.Empty,
                                 vm.CurrentFontFamily ?? string.Empty,
                                 StringComparison.Ordinal))
            {
                _fontInnerTextBox.Text = string.Empty;
            }
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

        private bool _dropdownClosePending;

        /// <summary>
        /// Список закрылся. Само решение — применять выбор или нет — откладывается
        /// на один оборот очереди.
        ///
        /// Причина в порядке событий. Нажатие по пункту и закрытие списка приходят
        /// не в том порядке, в каком их делает человек: светлое перекрытие главного
        /// окна (LightDismissOverlayLayer) перехватывает нажатие и гасит список
        /// раньше, чем событие дойдёт до самого пункта. Тогда _fontChosen на момент
        /// закрытия ещё пуст, выбор считается несостоявшимся, и шрифт применяется
        /// через раз — то сработает, то нет.
        ///
        /// Порядок при этом плавающий, а не всегда обратный: когда пункт успевает
        /// захватить указатель первым, всё приходит правильно. Поэтому не
        /// переставляем обработчики, а просто даём запоздавшему нажатию дойти.
        /// </summary>
        private void OnFontDropDownClosed(object? sender, EventArgs e)
        {
            // Таймер превью и захват снимаются сразу: они к решению не относятся,
            // а висеть лишний оборот им незачем.
            StopPreviewTimer();
            ReleaseFontPointerCapture();

            if (_dropdownClosePending) return;
            _dropdownClosePending = true;

            Dispatcher.UIThread.Post(() =>
            {
                _dropdownClosePending = false;
                FinishFontDropdown();
            }, DispatcherPriority.Background);
        }

        private void FinishFontDropdown()
        {
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

        // ── Настройки абзаца ──────────────────────────────────────────────

        // Открывает оверлей настроек абзаца внутри того же модуля (TextEditorView), которому
        // принадлежит этот риббон. Результат применяется одной командой отмены.
        private async void OnParagraphSettingsClick(object? sender, RoutedEventArgs e)
        {
            var host = this.FindAncestorOfType<TextEditorView>();
            if (host is null) return;

            var canvas = host.FindControl<DocumentCanvas>("PageCanvas")
                         ?? host.GetVisualDescendants().OfType<DocumentCanvas>().FirstOrDefault();
            if (canvas?.DataContext is not DocumentViewModel doc) return;

            var overlay = host.FindControl<ParagraphSettingsOverlay>("ParagraphOverlay")
                          ?? host.GetVisualDescendants().OfType<ParagraphSettingsOverlay>().FirstOrDefault();
            if (overlay is null) return;

            var current = doc.GetActiveParagraphProperties();
            if (current is null) return;

            var result = await overlay.ShowAsync(current);
            if (result is not null)
                doc.ApplyParagraphSettings(result);
        }

        // Открывает оверлей «Определить новый список» и применяет результат к выделению.
        private async void OnDefineListClick(object? sender, RoutedEventArgs e)
        {
            var host = this.FindAncestorOfType<TextEditorView>();
            if (host is null) return;

            var canvas = host.FindControl<DocumentCanvas>("PageCanvas")
                         ?? host.GetVisualDescendants().OfType<DocumentCanvas>().FirstOrDefault();
            if (canvas?.DataContext is not DocumentViewModel doc) return;

            var overlay = host.FindControl<ListSettingsOverlay>("ListOverlay")
                          ?? host.GetVisualDescendants().OfType<ListSettingsOverlay>().FirstOrDefault();
            if (overlay is null) return;

            var current = doc.GetActiveListProperties();
            var result = await overlay.ShowAsync(current);
            if (result is not null)
                doc.ApplyListSettings(result);
        }

        // Открывает оверлей «Уровни списка» и применяет выбранную схему многоуровневого списка.
        private async void OnMultilevelSettingsClick(object? sender, RoutedEventArgs e)
        {
            var host = this.FindAncestorOfType<TextEditorView>();
            if (host is null) return;

            var canvas = host.FindControl<DocumentCanvas>("PageCanvas")
                         ?? host.GetVisualDescendants().OfType<DocumentCanvas>().FirstOrDefault();
            if (canvas?.DataContext is not DocumentViewModel doc) return;

            var overlay = host.FindControl<ListLevelsOverlay>("ListLevelsOverlay")
                          ?? host.GetVisualDescendants().OfType<ListLevelsOverlay>().FirstOrDefault();
            if (overlay is null) return;

            var current = doc.GetActiveListLevelMarkers();
            var scheme = await overlay.ShowAsync(current);
            if (scheme is not null)
                doc.ApplyMultilevelScheme(scheme);
        }
    }
}