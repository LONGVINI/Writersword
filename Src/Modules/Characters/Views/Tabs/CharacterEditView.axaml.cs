using System;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Writersword.Modules.Characters.ViewModels;

namespace Writersword.Modules.Characters.Views.Tabs
{
    public partial class CharacterEditView : UserControl
    {
        // Ширина колонки бокового списка в компактном режиме (только аватарки)
        // и в скрытом (узкая полоса с кнопкой разворота).
        //
        // Компактная полоса: полоса прокрутки, поля плитки и сама строка
        // с полоской цвета и аватаркой. Ширина с запасом, чтобы плитка не
        // сжималась в квадрат; положение кружка внутри неё задают поля
        // площадки в разметке строки.
        private const double CompactSidebarWidth = 84;
        private const double HiddenSidebarWidth = 26;

        private CharactersViewModel? _subscribedViewModel;

        // ── Плавная смена ширины бокового списка ──────────────────────────
        // ColumnDefinition — не Animatable, обычные Transitions к нему не
        // применить, поэтому ширина ведётся по кадрам таймером. Кривая та же,
        // что у закладки группы в карточке: резкий старт, мягкое торможение.
        // Открытие и закрытие идут по-разному. Наружу список выбрасывается
        // резко и мягко тормозит у края — движение к человеку. Внутрь уходит
        // спокойнее: у той же кривой в обратную сторону слишком резкий старт,
        // и панель будто отдёргивают.
        private static readonly TimeSpan SidebarExpandDuration = TimeSpan.FromMilliseconds(260);
        private static readonly SplineEasing SidebarExpandEasing = new(0.165, 0.84, 0.44, 1);
        private static readonly TimeSpan SidebarCollapseDuration = TimeSpan.FromMilliseconds(320);
        private static readonly SplineEasing SidebarCollapseEasing = new(0.4, 0, 0.2, 1);

        private DispatcherTimer? _sidebarTimer;
        private ColumnDefinition? _sidebarColumn;
        private readonly Stopwatch _sidebarClock = new();
        private double _sidebarFrom;
        private double _sidebarTo;
        private double _sidebarTargetMin;
        private double _sidebarTargetMax;
        private TimeSpan _sidebarDuration = SidebarExpandDuration;
        private Easing _sidebarEasing = SidebarExpandEasing;

        // Первая раскладка при открытии вкладки идёт без хода: список должен
        // сразу стоять в своей ширине, а не выезжать на глазах.
        private bool _sidebarLayoutApplied;

        public CharacterEditView()
        {
            InitializeComponent();

            Loaded += OnViewLoaded;
            DataContextChanged += (_, _) => OnDataContextSwitched();
        }

        private void OnViewLoaded(object? sender, RoutedEventArgs e)
        {
            ApplySidebarLayout();

            // Запись ширины в вьюмодель после перетаскивания сплиттера —
            // оттуда она уходит в SessionData модуля и восстанавливается
            // при следующем открытии.
            var splitter = this.FindControl<GridSplitter>("SidebarSplitter");
            if (splitter is not null)
            {
                splitter.DragCompleted -= OnSplitterDragCompleted;
                splitter.DragCompleted += OnSplitterDragCompleted;
            }
        }

        private void OnDataContextSwitched()
        {
            // Смена режима панели приходит из вьюмодели (кнопки, восстановление
            // сессии) — колонку перестраивает подписка на PropertyChanged.
            if (_subscribedViewModel is not null)
                _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;

            _subscribedViewModel = DataContext as CharactersViewModel;
            if (_subscribedViewModel is not null)
                _subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;

            ApplySidebarLayout();
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CharactersViewModel.EditorSidebarMode))
                ApplySidebarLayout();
        }

        private void OnSplitterDragCompleted(object? sender, Avalonia.Input.VectorEventArgs e)
        {
            if (DataContext is not CharactersViewModel vm) return;

            // Ширина запоминается только в полном режиме: в компактном и скрытом
            // колонка фиксированная и не должна затирать сохранённое значение.
            if (vm.EditorSidebarMode != 0) return;

            var grid = this.FindControl<Grid>("EditorRootGrid");
            if (grid is null || grid.ColumnDefinitions.Count == 0) return;

            var width = grid.ColumnDefinitions[0].ActualWidth;
            if (width < 1) return;

            vm.EditorSidebarWidth = width;
        }

        /// <summary>
        /// Применяет режим и ширину бокового списка к колонке: полный режим —
        /// сохранённая в сессии ширина с пределами сплиттера, компактный —
        /// фиксированная колонка под аватарки, скрытый — узкая полоса.
        /// </summary>
        private void ApplySidebarLayout()
        {
            if (DataContext is not CharactersViewModel vm) return;

            var grid = this.FindControl<Grid>("EditorRootGrid");
            if (grid is null || grid.ColumnDefinitions.Count == 0) return;

            var column = grid.ColumnDefinitions[0];
            switch (vm.EditorSidebarMode)
            {
                case 1:
                    SetSidebarColumn(column, CompactSidebarWidth, CompactSidebarWidth, CompactSidebarWidth);
                    break;
                case 2:
                    SetSidebarColumn(column, HiddenSidebarWidth, HiddenSidebarWidth, HiddenSidebarWidth);
                    break;
                default:
                    if (Math.Abs(column.ActualWidth - vm.EditorSidebarWidth) > 0.5)
                    {
                        SetSidebarColumn(column, vm.EditorSidebarWidth, 170, 520);
                    }
                    else
                    {
                        column.MinWidth = 170;
                        column.MaxWidth = 520;
                    }
                    break;
            }

            _sidebarLayoutApplied = true;
        }

        /// <summary>
        /// Ведёт колонку к заданной ширине. Пределы колонки на время хода
        /// снимаются: с прежним MinWidth колонка упёрлась бы в него на первом
        /// же кадре и остаток пути прошла рывком. Конечные пределы ставятся,
        /// когда ход закончен.
        /// </summary>
        private void SetSidebarColumn(ColumnDefinition column, double width, double minWidth, double maxWidth)
        {
            StopSidebarAnimation();

            var from = column.ActualWidth;

            // Первая раскладка и случай, когда идти некуда, — сразу на месте.
            if (!_sidebarLayoutApplied || from < 1 || Math.Abs(from - width) < 0.5)
            {
                column.MinWidth = minWidth;
                column.MaxWidth = maxWidth;
                column.Width = new GridLength(width, GridUnitType.Pixel);
                return;
            }

            column.MinWidth = 0;
            column.MaxWidth = double.PositiveInfinity;

            _sidebarColumn = column;
            _sidebarFrom = from;
            _sidebarTo = width;
            _sidebarTargetMin = minWidth;
            _sidebarTargetMax = maxWidth;

            var collapsing = width < from;
            _sidebarDuration = collapsing ? SidebarCollapseDuration : SidebarExpandDuration;
            _sidebarEasing = collapsing ? SidebarCollapseEasing : SidebarExpandEasing;

            _sidebarTimer ??= new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _sidebarTimer.Tick -= OnSidebarAnimationTick;
            _sidebarTimer.Tick += OnSidebarAnimationTick;

            _sidebarClock.Restart();
            _sidebarTimer.Start();
        }

        private void OnSidebarAnimationTick(object? sender, EventArgs e)
        {
            if (_sidebarColumn is not { } column)
            {
                StopSidebarAnimation();
                return;
            }

            var progress = _sidebarClock.Elapsed.TotalMilliseconds
                           / _sidebarDuration.TotalMilliseconds;

            if (progress >= 1)
            {
                column.Width = new GridLength(_sidebarTo, GridUnitType.Pixel);
                column.MinWidth = _sidebarTargetMin;
                column.MaxWidth = _sidebarTargetMax;
                StopSidebarAnimation();
                return;
            }

            var eased = _sidebarEasing.Ease(progress);
            var current = _sidebarFrom + (_sidebarTo - _sidebarFrom) * eased;
            column.Width = new GridLength(current, GridUnitType.Pixel);
        }

        private void StopSidebarAnimation()
        {
            _sidebarTimer?.Stop();
            _sidebarClock.Reset();
            _sidebarColumn = null;
        }

        private void OnCompactToggleClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not CharactersViewModel vm) return;
            _sidebarExpandedForSearch = false;
            vm.EditorSidebarMode = vm.EditorSidebarMode == 1 ? 0 : 1;
        }

        private void OnHideSidebarClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not CharactersViewModel vm) return;
            _sidebarExpandedForSearch = false;
            vm.EditorSidebarMode = 2;
        }

        private void OnRestoreSidebarClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is CharactersViewModel vm)
                vm.RestoreSidebar();
        }

        // ── Поиск из компактного режима ───────────────────────────────────
        // В компактном списке поля поиска нет — только лупа. Она разворачивает
        // список на время набора: искать по одним аватаркам невозможно.
        // Свернётся он сам, когда искать перестали.

        private bool _sidebarExpandedForSearch;

        private void OnSidebarSearchClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not CharactersViewModel vm) return;

            _sidebarExpandedForSearch = true;
            vm.EditorSidebarMode = 0;

            // Фокус ставится после раскладки: до неё поле ещё скрыто
            // и фокус на него не встаёт.
            Dispatcher.UIThread.Post(
                () => this.FindControl<TextBox>("SidebarSearchBox")?.Focus(),
                DispatcherPriority.Input);
        }

        private void OnSidebarSearchKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            if (DataContext is not CharactersViewModel vm) return;

            // Первый Escape очищает запрос, второй — сворачивает список:
            // набранное не должно пропадать вместе с панелью.
            if (!string.IsNullOrEmpty(vm.SearchQuery))
            {
                vm.SearchQuery = string.Empty;
                e.Handled = true;
                return;
            }

            CollapseSidebarAfterSearch(vm);
            e.Handled = true;
        }

        private void OnSidebarSearchLostFocus(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not CharactersViewModel vm) return;

            // С непустым запросом список остаётся развёрнутым: найденное нужно
            // видеть и по нему щёлкать, а щелчок как раз уводит фокус.
            if (!string.IsNullOrEmpty(vm.SearchQuery)) return;

            CollapseSidebarAfterSearch(vm);
        }

        private void CollapseSidebarAfterSearch(CharactersViewModel vm)
        {
            if (!_sidebarExpandedForSearch) return;

            _sidebarExpandedForSearch = false;

            // Режим мог смениться руками, пока поле было открыто, — тогда
            // возвращать нечего.
            if (vm.EditorSidebarMode == 0)
                vm.EditorSidebarMode = 1;
        }

        // ── Перетаскивание персонажа из бокового списка ───────────────────
        // Строка списка одновременно кнопка открытия персонажа и источник
        // перетаскивания на полотно связей. Чтобы одно не мешало другому,
        // перетаскивание стартует не по нажатию, а после сдвига на порог:
        // обычный щелчок до порога не доходит и открывает карточку как раньше.

        private const double DragThreshold = 6.0;

        private Point? _pressOrigin;
        private string? _pressedCharacterId;

        // DoDragDropAsync принимает именно аргументы нажатия — их и храним
        // от PointerPressed до момента, когда сдвиг превысит порог.
        private PointerPressedEventArgs? _pressArgs;

        private void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            ClearPress();

            if (sender is not Control row) return;
            if (row.DataContext is not CharacterListItemViewModel item) return;
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

            _pressOrigin = e.GetPosition(this);
            _pressedCharacterId = item.Id;
            _pressArgs = e;
        }

        private async void OnRowPointerMoved(object? sender, PointerEventArgs e)
        {
            if (_pressOrigin is not { } origin) return;
            if (_pressedCharacterId is not { } characterId) return;
            if (_pressArgs is not { } pressArgs) return;
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

            var current = e.GetPosition(this);
            var dx = current.X - origin.X;
            var dy = current.Y - origin.Y;
            if (Math.Sqrt(dx * dx + dy * dy) < DragThreshold) return;

            // Порог пройден — дальше это перетаскивание, а не щелчок.
            ClearPress();

            var dataTransfer = new DataTransfer();
            dataTransfer.Add(DataTransferItem.Create(
                CharacterDragFormats.CharacterId, characterId));

            try
            {
                await DragDrop.DoDragDropAsync(pressArgs, dataTransfer, DragDropEffects.Link);
            }
            catch (Exception)
            {
                // Систему может прервать перетаскивание — связь просто
                // не создаётся, отдельной обработки не требуется.
            }
        }

        private void OnRowPointerReleased(object? sender, PointerReleasedEventArgs e)
            => ClearPress();

        private void ClearPress()
        {
            _pressOrigin = null;
            _pressedCharacterId = null;
            _pressArgs = null;
        }
    }
}
