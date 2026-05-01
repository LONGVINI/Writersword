using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Writersword.Modules.Characters.ViewModels;

namespace Writersword.Modules.Characters.Views.Tabs
{
    public partial class CharactersListView : UserControl
    {
        private const double DragThreshold = 8.0;

        private Point _dragStartPoint;
        private CharacterListItemViewModel? _dragCandidate;
        private bool _isDragging;
        private bool _hasPointerCapture;

        // последнее известное состояние preview — чтобы не дёргать VM на каждый пиксель
        private string? _lastPreviewBeforeId;
        private string? _lastPreviewFolderId;
        // папка с активным IsDragOver (закрытая папка под курсором)
        private CharacterFolderViewModel? _currentDragOverFolder;

        private Canvas? _ghostCanvas;
        private Border? _ghostBorder;
        private TextBlock? _ghostText;

        public CharactersListView()
        {
            InitializeComponent();

            AddHandler(PointerPressedEvent, OnGlobalPointerPressed, RoutingStrategies.Tunnel);
            AddHandler(PointerMovedEvent, OnGlobalPointerMoved, RoutingStrategies.Tunnel);
            AddHandler(PointerReleasedEvent, OnGlobalPointerReleased, RoutingStrategies.Tunnel);
            AddHandler(KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel);

            AddHandler(InputElement.LostFocusEvent, OnCardTextBoxLostFocus, RoutingStrategies.Bubble);
        }

        // Вызывается снаружи (например из CharactersModuleView) по Ctrl+Z
        public void PerformUndo()
        {
            if (DataContext is CharactersViewModel vm && vm.CanUndo)
                vm.Undo();
        }

        public void PerformRedo()
        {
            if (DataContext is CharactersViewModel vm && vm.CanRedo)
                vm.Redo();
        }

        protected override void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);
            _ghostCanvas = this.FindControl<Canvas>("DragGhostCanvas");
            _ghostBorder = this.FindControl<Border>("DragGhostBorder");
            _ghostText = this.FindControl<TextBlock>("DragGhostText");
        }

        public void FocusSearch()
            => this.FindControl<TextBox>("SearchTextBox")?.Focus();

        // ── поиск VM в дереве ──────────────────────────────────────────────

        private static CharacterListItemViewModel? FindCharacterItemVm(Visual? visual)
        {
            var current = visual;
            while (current is not null)
            {
                if (current is Control c && c.DataContext is CharacterListItemViewModel vm)
                    return vm;
                if (current is Control c2 && c2.DataContext is CharacterFolderViewModel)
                    return null;
                current = current.GetVisualParent();
            }
            return null;
        }

        private static CharacterFolderViewModel? FindFolderVm(Visual? visual)
        {
            var current = visual;
            while (current is not null)
            {
                if (current is Control c && c.DataContext is CharacterFolderViewModel vm)
                    return vm;
                current = current.GetVisualParent();
            }
            return null;
        }

        // ── LostFocus: сохранение имени ────────────────────────────────────

        private static void OnCardTextBoxLostFocus(object? sender, RoutedEventArgs e)
        {
            if (e.Source is not TextBox) return;
            var charVm = FindCharacterItemVm(e.Source as Visual);
            if (charVm is null) return;
            if (charVm.IsBeingNamed)
                charVm.ConfirmNameCommand.Execute().Subscribe(_ => { }, _ => { });
            else if (charVm.IsRenaming)
                charVm.ConfirmRenameCommand.Execute().Subscribe(_ => { }, _ => { });
        }

        // ── Enter / Escape ─────────────────────────────────────────────────

        private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
        {
            if (DataContext is CharactersViewModel vm)
            {
                // Ctrl+Z — отмена
                if (e.Key == Key.Z && e.KeyModifiers == KeyModifiers.Control)
                {
                    if (vm.CanUndo) { vm.Undo(); e.Handled = true; return; }
                }
                // Ctrl+Y — повтор
                if (e.Key == Key.Y && e.KeyModifiers == KeyModifiers.Control)
                {
                    if (vm.CanRedo) { vm.Redo(); e.Handled = true; return; }
                }
            }

            if (e.Key is not (Key.Return or Key.Enter or Key.Escape)) return;

            var charVm = FindCharacterItemVm(e.Source as Visual);
            if (charVm is null) return;
            if (e.Key is Key.Return or Key.Enter)
            {
                if (charVm.IsBeingNamed)
                { charVm.ConfirmNameCommand.Execute().Subscribe(_ => { }, _ => { }); e.Handled = true; }
                else if (charVm.IsRenaming)
                { charVm.ConfirmRenameCommand.Execute().Subscribe(_ => { }, _ => { }); e.Handled = true; }
            }
            else
            {
                if (charVm.IsBeingNamed)
                { charVm.CancelNameCommand.Execute().Subscribe(_ => { }, _ => { }); e.Handled = true; }
                else if (charVm.IsRenaming)
                { charVm.CancelRenameCommand.Execute().Subscribe(_ => { }, _ => { }); e.Handled = true; }
            }
        }

        // ── PointerPressed ─────────────────────────────────────────────────

        private void OnGlobalPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (DataContext is not CharactersViewModel vm) return;

            var source = e.Source as Visual;
            var folderVm = FindFolderVm(source);
            if (folderVm is not null) vm.ActiveFolderId = folderVm.FolderId;

            var charVm = FindCharacterItemVm(source);
            if (charVm is not null
                && !charVm.IsBeingNamed
                && !charVm.IsRenaming
                && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                _dragCandidate = charVm;
                _dragStartPoint = e.GetPosition(this);
                _isDragging = false;
                _hasPointerCapture = false;
            }
            else
            {
                _dragCandidate = null;
                _isDragging = false;
            }
        }

        // ── PointerMoved ───────────────────────────────────────────────────

        private void OnGlobalPointerMoved(object? sender, PointerEventArgs e)
        {
            if (_dragCandidate is null) return;

            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                if (_isDragging && DataContext is CharactersViewModel vm)
                    vm.CancelDragPreview(_dragCandidate.Id);
                ClearDragVisuals();
                _dragCandidate = null;
                _isDragging = false;
                return;
            }

            var pos = e.GetPosition(this);

            if (!_isDragging)
            {
                var delta = pos - _dragStartPoint;
                if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
                    return;

                // порог превышен — начинаем drag
                _isDragging = true;
                _hasPointerCapture = true;
                e.Pointer.Capture(this);

                if (DataContext is CharactersViewModel startVm)
                    startVm.BeginDragPreview(_dragCandidate.Id);

                ShowGhost(_dragCandidate.Name, _dragCandidate.Color, pos);
            }
            else
            {
                MoveGhost(pos);
                UpdatePreview(pos);
            }
        }

        // ── UpdatePreview: сначала папка по Y, потом позиция внутри неё ──

        private void UpdatePreview(Point pos)
        {
            if (DataContext is not CharactersViewModel vm) return;
            if (_dragCandidate is null) return;

            // определяем папку под курсором по её визуальным границам
            var targetFolderVm = FindFolderAtPoint(pos, vm);

            // снять IsDragOver если ушли
            if (_currentDragOverFolder is not null && !ReferenceEquals(_currentDragOverFolder, targetFolderVm))
            {
                _currentDragOverFolder.IsDragOver = false;
                _currentDragOverFolder = null;
            }

            if (targetFolderVm is null) return;

            // закрытая папка → в конец
            if (!targetFolderVm.IsExpanded)
            {
                if (!ReferenceEquals(_currentDragOverFolder, targetFolderVm))
                {
                    targetFolderVm.IsDragOver = true;
                    _currentDragOverFolder = targetFolderVm;
                }
                SetPreviewIfChanged(vm, null, targetFolderVm.FolderId);
                return;
            }

            // открытая папка → ищем позицию вставки внутри неё
            var (beforeId, folderId) = FindInsertPositionInFolder(pos, targetFolderVm, vm);
            if (folderId is not null)
                SetPreviewIfChanged(vm, beforeId, folderId);
        }

        // Находит папку по Y-позиции курсора, проверяя границы каждой папки в дереве.
        private CharacterFolderViewModel? FindFolderAtPoint(Point pos, CharactersViewModel vm)
        {
            CharacterFolderViewModel? best = null;
            double bestTop = double.MinValue;

            foreach (var folderVm in vm.Folders)
            {
                // ищем Border заголовка папки — DataContext = folderVm
                var headerControl = FindControlForDataContext(folderVm);
                if (headerControl is null) continue;

                var topLeft = headerControl.TranslatePoint(new Point(0, 0), this);
                if (topLeft is null) continue;

                // граница папки: от верха заголовка до низа следующего заголовка (или бесконечность)
                var folderTop = topLeft.Value.Y;

                // выбираем ту папку, чей верх последний не превышает Y курсора
                if (pos.Y >= folderTop && folderTop > bestTop)
                {
                    bestTop = folderTop;
                    best = folderVm;
                }
            }

            return best;
        }

        // Ищет управляющий Control для заданного DataContext
        private Control? FindControlForDataContext(object dataContext)
        {
            foreach (var control in this.GetVisualDescendants().OfType<Control>())
            {
                if (ReferenceEquals(control.DataContext, dataContext) && control is StackPanel)
                    return control;
            }
            return null;
        }

        // Внутри открытой папки ищет позицию вставки.
        // Позиции карточек рассчитываются детерминированно по размеру карточки,
        // а не из визуального дерева (оно устаревает во время drag).
        private (string? beforeId, string? folderId) FindInsertPositionInFolder(
            Point pos, CharacterFolderViewModel folderVm, CharactersViewModel vm)
        {
            // все карточки папки кроме перетаскиваемой — в порядке коллекции
            var cards = folderVm.Characters.Where(c => !c.IsDragging).ToList();
            if (cards.Count == 0)
                return (null, folderVm.FolderId);

            // находим контейнер ItemsControl папки чтобы узнать его ширину и позицию
            Control? container = null;
            foreach (var ctrl in this.GetVisualDescendants().OfType<ItemsControl>())
            {
                if (ctrl.DataContext is CharacterFolderViewModel fvm &&
                    ReferenceEquals(fvm, folderVm))
                {
                    container = ctrl;
                    break;
                }
            }

            if (container is null)
                return (null, folderVm.FolderId);

            var containerTopLeft = container.TranslatePoint(new Point(0, 0), this);
            if (containerTopLeft is null)
                return (null, folderVm.FolderId);

            // размеры карточки с учётом margin
            const double cardWidth = 148.0;
            const double cardMargin = 6.0;
            const double slotWidth = cardWidth + cardMargin * 2; // 160px на слот
            const double cardHeight = 100.0;
            const double slotHeight = cardHeight + cardMargin * 2;

            double containerWidth = container.Bounds.Width;
            int cardsPerRow = Math.Max(1, (int)(containerWidth / slotWidth));

            // координаты курсора относительно контейнера
            double relX = pos.X - containerTopLeft.Value.X;
            double relY = pos.Y - containerTopLeft.Value.Y;

            // строка по Y
            int row = Math.Max(0, (int)(relY / slotHeight));

            // индекс в строке по X — зажимаем в пределах строки
            int col = (int)(relX / slotWidth);
            col = Math.Max(0, col);

            // зажимаем col в пределах строки — курсор правее последней позиции
            // остаётся в последней позиции этой строки (никакого переноса)
            col = Math.Min(col, cardsPerRow - 1);

            // глобальный индекс целевой позиции
            int targetIndex = row * cardsPerRow + col;
            targetIndex = Math.Min(targetIndex, cards.Count - 1);

            if (targetIndex < 0)
                return (null, folderVm.FolderId);

            return (cards[targetIndex].Id, folderVm.FolderId);
        }

        // Группирует карточки в строки по Y-пересечению

        private void SetPreviewIfChanged(CharactersViewModel vm, string? beforeId, string folderId)
        {
            if (beforeId == _lastPreviewBeforeId && folderId == _lastPreviewFolderId)
                return;
            _lastPreviewBeforeId = beforeId;
            _lastPreviewFolderId = folderId;
            vm.UpdateDragPreview(_dragCandidate!.Id, beforeId, folderId);
        }

        private static string? FindFolderContaining(CharactersViewModel vm, string charId)
        {
            foreach (var f in vm.Folders)
                if (f.Characters.Any(c => c.Id == charId))
                    return f.FolderId;
            return null;
        }

        // ── PointerReleased ────────────────────────────────────────────────

        private void OnGlobalPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (!_isDragging || _dragCandidate is null)
            {
                _dragCandidate = null;
                _isDragging = false;
                return;
            }

            // освобождаем только нашу собственную capture
            if (_hasPointerCapture)
            {
                e.Pointer.Capture(null);
                _hasPointerCapture = false;
            }

            HideGhost();
            ClearDragVisuals();

            if (DataContext is CharactersViewModel vm)
                vm.CommitDragPreview(_dragCandidate.Id);

            _dragCandidate = null;
            _isDragging = false;
            _lastPreviewBeforeId = null;
            _lastPreviewFolderId = null;
        }

        private void ClearDragVisuals()
        {
            if (_currentDragOverFolder is not null)
            {
                _currentDragOverFolder.IsDragOver = false;
                _currentDragOverFolder = null;
            }
            HideGhost();
        }

        // ── Призрак — полноразмерная копия карточки ────────────────────────

        private void ShowGhost(string name, string color, Point pos)
        {
            if (_ghostCanvas is null || _ghostBorder is null || _ghostText is null) return;

            _ghostText.Text = name;

            var brush = Color.TryParse(color, out var parsed)
                ? new SolidColorBrush(parsed)
                : new SolidColorBrush(Color.FromRgb(96, 125, 139));

            // красим верхнюю цветную панель призрака
            var topBg = _ghostBorder.FindControl<Border>("DragGhostTopBg");
            if (topBg is not null) topBg.Background = brush;

            MoveGhost(pos);
            _ghostCanvas.IsVisible = true;
        }

        private void MoveGhost(Point pos)
        {
            if (_ghostBorder is null) return;
            // центрируем призрак относительно курсора
            Canvas.SetLeft(_ghostBorder, pos.X - 74);
            Canvas.SetTop(_ghostBorder, pos.Y - 50);
        }

        private void HideGhost()
        {
            if (_ghostCanvas is not null)
                _ghostCanvas.IsVisible = false;
        }
    }
}