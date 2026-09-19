using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Writersword.Modules.Notes.Models;
using Writersword.Modules.Notes.ViewModels;

namespace Writersword.Modules.Notes.Views
{
    /// <summary>
    /// Представление модуля заметок.
    ///
    /// Здесь нет правил работы с текстом — они целиком в модели представления.
    /// Задача этого кода одна: перевести нажатие или щелчок в вызов модели и
    /// вернуть каретку туда, куда указал результат вызова.
    /// </summary>
    public partial class NotesView : UserControl
    {
        /// <summary>Сколько плашка отмены держится на экране.</summary>
        private static readonly TimeSpan UndoMessageLifetime = TimeSpan.FromSeconds(7);

        private readonly DispatcherTimer _undoMessageTimer;
        private NotesViewModel? _subscribed;

        public NotesView()
        {
            InitializeComponent();

            // Нажатия разбираются на погружении, до поля ввода. Backspace и
            // Delete поле обрабатывает само и помечает нажатие обработанным —
            // на всплытии они сюда уже не приходят.
            BlocksHost.AddHandler(KeyDownEvent, OnBlocksKeyDown, RoutingStrategies.Tunnel);
            AddHandler(KeyDownEvent, OnViewKeyDown, RoutingStrategies.Tunnel);

            _undoMessageTimer = new DispatcherTimer { Interval = UndoMessageLifetime };
            _undoMessageTimer.Tick += OnUndoMessageTimerTick;

            DataContextChanged += OnDataContextChanged;
            SizeChanged += OnSizeChanged;
            DetachedFromVisualTree += OnDetached;
        }

        private NotesViewModel? ViewModel => DataContext as NotesViewModel;

        // ── Жизненный цикл ────────────────────────────────────────────────

        private void OnDataContextChanged(object? sender, EventArgs e)
        {
            if (_subscribed != null)
                _subscribed.PropertyChanged -= OnViewModelPropertyChanged;

            _subscribed = ViewModel;
            if (_subscribed != null)
            {
                _subscribed.PropertyChanged += OnViewModelPropertyChanged;
                if (Bounds.Width > 0)
                    _subscribed.IsCompact = Bounds.Width < NotesViewModel.CompactWidth;
            }

            _undoMessageTimer.Stop();
        }

        private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            _undoMessageTimer.Stop();
        }

        private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            // Модуль может стоять и в доке, и в плавающем окне: узкий он или
            // широкий, решает его собственная ширина, а не ширина окна.
            if (ViewModel is { } vm)
                vm.IsCompact = e.NewSize.Width < NotesViewModel.CompactWidth;
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(NotesViewModel.HasUndoMessage))
                return;

            _undoMessageTimer.Stop();
            if (ViewModel?.HasUndoMessage == true)
                _undoMessageTimer.Start();
        }

        private void OnUndoMessageTimerTick(object? sender, EventArgs e)
        {
            _undoMessageTimer.Stop();
            ViewModel?.DismissUndoMessage();
        }

        // ── Лента ─────────────────────────────────────────────────────────

        private void OnTogglePagesClick(object? sender, RoutedEventArgs e)
        {
            if (ViewModel is { } vm)
                vm.IsPagePanelOpen = !vm.IsPagePanelOpen;
        }

        private void OnAddPageClick(object? sender, RoutedEventArgs e)
        {
            if (ViewModel?.AddPage() is { } page)
                FocusBlock(new NoteCaret(page.Blocks[0], 0));
        }

        private void OnDuplicatePageClick(object? sender, RoutedEventArgs e)
        {
            if (ViewModel?.DuplicateSelectedPage() is { } page && page.Blocks.Count > 0)
                FocusBlock(new NoteCaret(page.Blocks[0], 0));
        }

        private void OnRemovePageClick(object? sender, RoutedEventArgs e) =>
            ViewModel?.RemoveSelectedPage();

        private void OnCopyPageClick(object? sender, RoutedEventArgs e)
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            var text = ViewModel?.SelectedPageAsText();
            if (clipboard == null || string.IsNullOrEmpty(text))
                return;

            _ = CopyAsync(clipboard, text);
        }

        private static async Task CopyAsync(IClipboard clipboard, string text)
        {
            try
            {
                await clipboard.SetTextAsync(text);
            }
            catch (Exception)
            {
                // Буфер обмена держит другое приложение. Повторить нажатие
                // человеку проще, чем читать сообщение об этом.
            }
        }

        private void OnMovePageUpClick(object? sender, RoutedEventArgs e) =>
            ViewModel?.MoveSelectedPage(-1);

        private void OnMovePageDownClick(object? sender, RoutedEventArgs e) =>
            ViewModel?.MoveSelectedPage(1);

        private void OnBlockTypeClick(object? sender, RoutedEventArgs e)
        {
            if (sender is not Control { Tag: string tag } ||
                !Enum.TryParse<NoteBlockType>(tag, out var type) || !Enum.IsDefined(type))
                return;

            ViewModel?.SetSelectedBlockType(type);
            EnsureFocus();
        }

        private void OnWrapClick(object? sender, RoutedEventArgs e)
        {
            if (sender is not Control { Tag: string marker } || marker.Length == 0)
                return;

            var vm = ViewModel;
            if (vm?.SelectedBlock is not { } block)
                return;

            // Лента ввод не забирает, поэтому выделение в поле строки живо и
            // его можно взять прямо оттуда.
            var box = FindEditor(block);
            var start = box?.SelectionStart ?? block.Text.Length;
            var end = box?.SelectionEnd ?? block.Text.Length;

            if (vm.WrapSelection(block, start, end, marker) is { } target)
                FocusBlock(target);
        }

        private void OnStrikeClick(object? sender, RoutedEventArgs e)
        {
            ViewModel?.ToggleSelectedStrikeThrough();
            EnsureFocus();
        }

        private void OnHighlightClick(object? sender, RoutedEventArgs e)
        {
            ViewModel?.ToggleSelectedHighlight();
            EnsureFocus();
        }

        private void OnMoveBlockUpClick(object? sender, RoutedEventArgs e)
        {
            var vm = ViewModel;
            if (vm?.SelectedBlock is not { } block)
                return;
            vm.MoveBlock(block, -1);
            FocusBlock(new NoteCaret(block, block.Text.Length));
        }

        private void OnMoveBlockDownClick(object? sender, RoutedEventArgs e)
        {
            var vm = ViewModel;
            if (vm?.SelectedBlock is not { } block)
                return;
            vm.MoveBlock(block, 1);
            FocusBlock(new NoteCaret(block, block.Text.Length));
        }

        private void OnRemoveBlockClick(object? sender, RoutedEventArgs e)
        {
            if (ViewModel?.RemoveBlock(ViewModel.SelectedBlock) is { } target)
                FocusBlock(target);
        }

        private void OnUndoClick(object? sender, RoutedEventArgs e) => ViewModel?.Undo();

        private void OnDismissUndoClick(object? sender, RoutedEventArgs e) =>
            ViewModel?.DismissUndoMessage();

        private void OnPanelResize(object? sender, VectorEventArgs e)
        {
            if (ViewModel is { } vm)
                vm.PagePanelWidth += e.Vector.X;
        }

        // ── Строки ────────────────────────────────────────────────────────

        // Аргументы объявлены базовым типом события, а не типом получения
        // ввода: обработчику нужен только источник, а имя частного типа
        // разнится от версии к версии Avalonia.
        /// <summary>
        /// Щелчок по пустому месту полотна ставит каретку в конец текста.
        /// Так полотно ведёт себя как одно поле: промахнуться мимо строки и
        /// не попасть никуда — нельзя.
        /// </summary>
        private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            // Щелчок по самой строке разбирает её поле ввода — сюда он
            // доходит всплытием, и перехватывать его не нужно.
            if (e.Source is Visual source &&
                source.GetSelfAndVisualAncestors().Any(visual => visual is TextBox or CheckBox))
                return;

            var vm = ViewModel;
            if (vm?.SelectedPage is not { } page || vm.IsReadOnly)
                return;

            var last = page.Blocks.LastOrDefault();
            if (last is { HasText: true })
            {
                FocusBlock(new NoteCaret(last, last.Text.Length));
                return;
            }

            // Последняя строка — черта, писать в неё нечего: заводим новую.
            if (vm.AppendParagraph() is { } appended)
                FocusBlock(appended);
        }

        /// <summary>
        /// Enter в заголовке страницы уводит ввод в первую строку — то же
        /// движение, что и в любом бланке.
        /// </summary>
        private void OnTitleKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None)
                return;

            var vm = ViewModel;
            if (vm?.SelectedPage?.Blocks.FirstOrDefault(block => block.HasText) is not { } first)
                return;

            FocusBlock(new NoteCaret(first, first.Text.Length));
            e.Handled = true;
        }

        private void OnBlockGotFocus(object? sender, RoutedEventArgs e)
        {
            if (sender is not Control { DataContext: NoteBlockViewModel block })
                return;

            ViewModel?.SelectBlock(block);
            block.IsEditing = true;
        }

        private void OnBlockLostFocus(object? sender, RoutedEventArgs e)
        {
            if (sender is Control { DataContext: NoteBlockViewModel block })
                block.IsEditing = false;
        }

        /// <summary>
        /// Выбрать строку щелчком по ней.
        /// Нужно только разделителю: текста у него нет, поля ввода тоже, и
        /// без этого его нельзя было бы ни выделить, ни убрать кнопкой ленты.
        /// Остальные строки выбираются сами — вводом в своё поле.
        /// </summary>
        private void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is Control { DataContext: NoteBlockViewModel { IsDivider: true } block })
                ViewModel?.SelectBlock(block);
        }

        private void OnViewKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Z || e.KeyModifiers != KeyModifiers.Control)
                return;

            // В поле ввода Ctrl+Z отменяет набранный текст средствами самого
            // поля. Своя история модуля туда не вмешивается: иначе одно
            // нажатие откатывало бы и букву, и удаление страницы разом.
            if (e.Source is TextBox)
                return;

            var vm = ViewModel;
            if (vm == null || !vm.CanUndo)
                return;

            vm.Undo();
            e.Handled = true;
        }

        private void OnBlocksKeyDown(object? sender, KeyEventArgs e)
        {
            var vm = ViewModel;
            if (vm == null || vm.IsReadOnly)
                return;
            if (e.Source is not TextBox box || box.DataContext is not NoteBlockViewModel block)
                return;

            var caret = Math.Clamp(box.CaretIndex, 0, block.Text.Length);
            var selectionStart = Math.Clamp(Math.Min(box.SelectionStart, box.SelectionEnd), 0, block.Text.Length);
            var selectionEnd = Math.Clamp(Math.Max(box.SelectionStart, box.SelectionEnd), 0, block.Text.Length);
            var hasSelection = selectionEnd > selectionStart;

            if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
            {
                // Выделенный кусок при переводе строки заменяется переводом —
                // так же, как в любом поле ввода.
                if (hasSelection)
                {
                    block.Text = block.Text.Remove(selectionStart, selectionEnd - selectionStart);
                    caret = selectionStart;
                }

                FocusBlock(vm.SplitBlock(block, caret));
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None && !hasSelection)
            {
                if (vm.TryApplyShortcut(block, caret, out var afterShortcut))
                {
                    FocusBlock(afterShortcut);
                    e.Handled = true;
                }

                return;
            }

            if (e.Key == Key.Back && e.KeyModifiers == KeyModifiers.None && !hasSelection && caret == 0)
            {
                if (vm.MergeWithPrevious(block) is { } merged)
                {
                    FocusBlock(merged);
                    e.Handled = true;
                }

                return;
            }

            if (e.Key == Key.Delete && e.KeyModifiers == KeyModifiers.None &&
                !hasSelection && caret == block.Text.Length)
            {
                if (vm.MergeWithNext(block) is { } joined)
                {
                    FocusBlock(joined);
                    e.Handled = true;
                }

                return;
            }

            if (e.Key is Key.Up or Key.Down && e.KeyModifiers == KeyModifiers.Alt)
            {
                vm.MoveBlock(block, e.Key == Key.Up ? -1 : 1);
                FocusBlock(new NoteCaret(block, caret));
                e.Handled = true;
                return;
            }

            // Переход на соседнюю строку — только с краёв текста: внутри
            // строки стрелки водят каретку по перенесённым строкам сами.
            if (e.Key == Key.Up && e.KeyModifiers == KeyModifiers.None && caret == 0)
            {
                if (vm.StepBlock(block, -1, int.MaxValue) is { } previous)
                {
                    FocusBlock(previous);
                    e.Handled = true;
                }

                return;
            }

            if (e.Key == Key.Down && e.KeyModifiers == KeyModifiers.None && caret == block.Text.Length)
            {
                if (vm.StepBlock(block, 1, 0) is { } next)
                {
                    FocusBlock(next);
                    e.Handled = true;
                }

                return;
            }

            if (e.KeyModifiers == KeyModifiers.Control && e.Key is Key.B or Key.I or Key.E)
            {
                var marker = e.Key switch
                {
                    Key.B => "**",
                    Key.I => "*",
                    _ => "`"
                };

                if (vm.WrapSelection(block, selectionStart, selectionEnd, marker) is { } wrapped)
                {
                    FocusBlock(wrapped);
                    e.Handled = true;
                }

                return;
            }

            if (e.Key == Key.V && e.KeyModifiers == KeyModifiers.Control)
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard == null)
                    return;

                // Вставка разбирается модулем: многострочный текст ложится
                // строками, а не одной кашей с переводами внутри строки.
                e.Handled = true;
                _ = PasteAsync(clipboard, block, caret, selectionStart, selectionEnd);
            }
        }

        private async Task PasteAsync(
            IClipboard clipboard, NoteBlockViewModel block, int caret, int selectionStart, int selectionEnd)
        {
            string? text;
            try
            {
                text = await clipboard.TryGetTextAsync();
            }
            catch (Exception)
            {
                // Буфер обмена держит другое приложение. Молчаливый отказ
                // здесь уместнее сообщения: человек просто нажмёт ещё раз.
                return;
            }

            var vm = ViewModel;
            if (vm == null || vm.IsReadOnly || string.IsNullOrEmpty(text))
                return;

            if (selectionEnd > selectionStart && selectionEnd <= block.Text.Length)
            {
                block.Text = block.Text.Remove(selectionStart, selectionEnd - selectionStart);
                caret = selectionStart;
            }

            if (vm.PasteText(block, caret, text) is { } target)
                FocusBlock(target);
        }

        // ── Ввод и каретка ────────────────────────────────────────────────

        /// <summary>
        /// Перевести ввод в строку и поставить каретку.
        /// Поле строки появляется в дереве не раньше следующего прохода
        /// разметки, поэтому переход откладывается до её завершения.
        /// </summary>
        private void FocusBlock(NoteCaret target)
        {
            var vm = ViewModel;
            if (vm == null)
                return;

            vm.SelectBlock(target.Block);
            Dispatcher.UIThread.Post(() =>
            {
                var box = FindEditor(target.Block);
                if (box == null)
                    return;

                box.BringIntoView();
                box.Focus();
                box.CaretIndex = Math.Clamp(target.Caret, 0, box.Text?.Length ?? 0);
            }, DispatcherPriority.Loaded);
        }

        /// <summary>
        /// Вернуть ввод в выбранную строку после нажатия в ленте.
        /// Кнопки ленты ввод не забирают, поэтому обычно возвращать нечего;
        /// исключение — превращение строки в разделитель: поле исчезает
        /// вместе с текстом, и ввод переходит на соседнюю строку.
        /// </summary>
        private void EnsureFocus()
        {
            var vm = ViewModel;
            if (vm?.SelectedBlock is not { } block)
                return;

            if (block.IsDivider)
            {
                var neighbour = vm.StepBlock(block, 1, 0) ?? vm.StepBlock(block, -1, int.MaxValue);
                if (neighbour is { } target)
                    FocusBlock(target);
                return;
            }

            var box = FindEditor(block);
            if (box is { IsFocused: false })
                FocusBlock(new NoteCaret(block, block.Text.Length));
        }

        /// <summary>
        /// Найти поле ввода строки.
        /// Поиск идёт по содержимому, а не по имени: строк на странице много,
        /// имя в шаблоне у всех одно, и различает их именно модель строки.
        /// </summary>
        private TextBox? FindEditor(NoteBlockViewModel block) =>
            BlocksHost.GetVisualDescendants()
                .OfType<TextBox>()
                .FirstOrDefault(box => ReferenceEquals(box.DataContext, block));
    }
}
