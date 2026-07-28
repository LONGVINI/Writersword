using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Writersword.Modules.Characters.ViewModels;

namespace Writersword.Modules.Characters.Views.Tabs
{
    public partial class CharacterEditView : UserControl
    {
        // Ширина колонки бокового списка в компактном режиме (только аватарки)
        // и в скрытом (узкая полоса с кнопкой разворота).
        private const double CompactSidebarWidth = 76;
        private const double HiddenSidebarWidth = 26;

        private CharactersViewModel? _subscribedViewModel;

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
                    column.MinWidth = CompactSidebarWidth;
                    column.MaxWidth = CompactSidebarWidth;
                    column.Width = new GridLength(CompactSidebarWidth, GridUnitType.Pixel);
                    break;
                case 2:
                    column.MinWidth = HiddenSidebarWidth;
                    column.MaxWidth = HiddenSidebarWidth;
                    column.Width = new GridLength(HiddenSidebarWidth, GridUnitType.Pixel);
                    break;
                default:
                    column.MinWidth = 170;
                    column.MaxWidth = 520;
                    if (Math.Abs(column.ActualWidth - vm.EditorSidebarWidth) > 0.5)
                        column.Width = new GridLength(vm.EditorSidebarWidth, GridUnitType.Pixel);
                    break;
            }
        }

        private void OnCompactToggleClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not CharactersViewModel vm) return;
            vm.EditorSidebarMode = vm.EditorSidebarMode == 1 ? 0 : 1;
        }

        private void OnHideSidebarClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is CharactersViewModel vm)
                vm.EditorSidebarMode = 2;
        }

        private void OnRestoreSidebarClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is CharactersViewModel vm)
                vm.RestoreSidebar();
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
