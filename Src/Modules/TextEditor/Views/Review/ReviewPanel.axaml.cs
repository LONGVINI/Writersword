using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Writersword.Modules.TextEditor.ViewModels.Review;

namespace Writersword.Modules.TextEditor.Views.Review
{
    /// <summary>
    /// Панель рецензии. Вид только переводит указатель и клавиши в действия
    /// вью-модели: щелчок по пункту — выбор и переход к месту, наведение — подсветка
    /// места на листе, стрелки — ход по списку, Enter в поле ответа — отправка.
    ///
    /// Кнопки и поле ответа внутри пункта нажатие забирают себе, поэтому щелчок по
    /// ним не выбирает пункт заново и не уводит лист.
    /// </summary>
    public partial class ReviewPanel : UserControl
    {
        private ItemsControl? _rows;
        private ReviewPanelViewModel? _vm;

        public ReviewPanel()
        {
            InitializeComponent();

            _rows = this.FindControl<ItemsControl>("RowsList");
            if (_rows is not null)
            {
                _rows.AddHandler(InputElement.PointerPressedEvent, OnRowsPointerPressed, RoutingStrategies.Bubble);
                _rows.PointerMoved += OnRowsPointerMoved;
                _rows.PointerExited += OnRowsPointerExited;
                _rows.AddHandler(Button.ClickEvent, OnRowsButtonClick, RoutingStrategies.Bubble, handledEventsToo: true);
            }

            AddHandler(KeyDownEvent, OnPanelKeyDown, RoutingStrategies.Tunnel);
            DataContextChanged += OnDataContextChanged;
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        private void OnDataContextChanged(object? sender, EventArgs e)
        {
            if (_vm is not null) _vm.ScrollIntoViewRequested -= OnScrollIntoViewRequested;
            _vm = DataContext as ReviewPanelViewModel;
            if (_vm is not null) _vm.ScrollIntoViewRequested += OnScrollIntoViewRequested;
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _vm?.Hover(null);
        }

        // ── Указатель ─────────────────────────────────────────────────────

        private void OnRowsPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.Handled || _vm is null) return;
            if (!e.GetCurrentPoint(_rows).Properties.IsLeftButtonPressed) return;

            var item = ItemOf(e.Source as Visual);
            if (item is null) return;

            _vm.Select(item);
            _rows?.Focus();
            e.Handled = true;
        }

        private void OnRowsPointerMoved(object? sender, PointerEventArgs e)
            => _vm?.Hover(ItemOf(e.Source as Visual));

        private void OnRowsPointerExited(object? sender, PointerEventArgs e)
            => _vm?.Hover(null);

        /// <summary>Открылось поле ответа — курсор сразу в нём.</summary>
        private void OnRowsButtonClick(object? sender, RoutedEventArgs e)
        {
            if (e.Source is not Visual source) return;
            if (ItemOf(source) is not { IsReplying: true } item) return;

            var card = source.GetVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("Card"));
            if (card is null) return;

            Dispatcher.UIThread.Post(() =>
            {
                if (!item.IsReplying) return;
                card.GetVisualDescendants().OfType<TextBox>()
                    .FirstOrDefault(t => t.Classes.Contains("ReplyBox"))?.Focus();
            }, DispatcherPriority.Background);
        }

        /// <summary>Пункт списка, которому принадлежит элемент под указателем.</summary>
        private static ReviewItemViewModel? ItemOf(Visual? source)
        {
            for (var v = source; v is not null; v = v.GetVisualParent())
            {
                if (v is Control { DataContext: ReviewItemViewModel item }) return item;
                if (v is ItemsControl { Name: "RowsList" }) return null;
            }

            return null;
        }

        // ── Клавиши ───────────────────────────────────────────────────────

        private void OnPanelKeyDown(object? sender, KeyEventArgs e)
        {
            if (_vm is null) return;

            // Поле ответа: Enter отправляет, Shift+Enter — новая строка.
            if (e.Source is TextBox box && box.Classes.Contains("ReplyBox"))
            {
                if (e.Key == Key.Enter && (e.KeyModifiers & KeyModifiers.Shift) == 0
                    && box.DataContext is ReviewItemViewModel replyItem)
                {
                    if (replyItem.ReplyCommand.CanExecute(null))
                        replyItem.ReplyCommand.Execute(null);
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    if (box.DataContext is ReviewItemViewModel closing) closing.IsReplying = false;
                    _rows?.Focus();
                    e.Handled = true;
                }
                return;
            }

            // В поиске стрелки двигают каретку строки поиска, а не список.
            if (e.Source is TextBox) return;
            if (e.Source is ComboBox) return;

            switch (e.Key)
            {
                case Key.Down:
                    _vm.Step(1);
                    e.Handled = true;
                    break;

                case Key.Up:
                    _vm.Step(-1);
                    e.Handled = true;
                    break;

                case Key.Enter when _vm.SelectedItem is { } selected:
                    _vm.Select(selected);
                    e.Handled = true;
                    break;
            }
        }

        // ── Прокрутка к пункту ────────────────────────────────────────────

        private void OnScrollIntoViewRequested(ReviewItemViewModel item)
        {
            if (_rows is null) return;

            // Раскрытие пункта меняет его высоту на следующем проходе раскладки; ведём
            // к нему после него, чтобы раскрытый пункт попал в окно целиком.
            Dispatcher.UIThread.Post(() =>
            {
                if (_rows is null) return;
                _rows.ScrollIntoView(item);
                if (_rows.ContainerFromItem(item) is Control container)
                    container.BringIntoView();
            }, DispatcherPriority.Background);
        }
    }
}
