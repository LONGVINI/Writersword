using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Xaml.Interactivity;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Writersword.ViewModels;
using Writersword.ViewModels.Components;

namespace Writersword.Behaviors
{
    /// <summary>
    /// Behavior для Drag and Drop вкладок в TabBar
    /// Smooth reorder как в Chrome
    /// </summary>
    public class TabDragDropBehavior : Behavior<ItemsControl>
    {
        private const double TAB_SPACING = 4;
        private const double DRAG_THRESHOLD = 10;

        private Point _dragStartPoint;
        private double _currentOffsetX = 0;
        private bool _isDragging = false;
        private bool _isSwapping = false;
        private Button? _draggedButton = null;
        private ContentPresenter? _draggedPresenter = null;
        private int _originalIndex = -1;
        private int _currentVisualIndex = -1;
        private double _tabWidth = 180;

        private Dictionary<TranslateTransform, CancellationTokenSource> _activeAnimations = new Dictionary<TranslateTransform, CancellationTokenSource>();
        private Dictionary<TranslateTransform, double> _targetPositions = new Dictionary<TranslateTransform, double>();

        protected override void OnAttached()
        {
            base.OnAttached();

            if (AssociatedObject != null)
            {
                AssociatedObject.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, handledEventsToo: true);
                AssociatedObject.AddHandler(InputElement.PointerMovedEvent, OnPointerMoved, handledEventsToo: true);
                AssociatedObject.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased, handledEventsToo: true);

                Console.WriteLine("[TabDragDrop] Behavior attached");
            }
        }

        protected override void OnDetaching()
        {
            base.OnDetaching();

            if (AssociatedObject != null)
            {
                AssociatedObject.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
                AssociatedObject.RemoveHandler(InputElement.PointerMovedEvent, OnPointerMoved);
                AssociatedObject.RemoveHandler(InputElement.PointerReleasedEvent, OnPointerReleased);
            }
        }

        private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (_isSwapping || !e.GetCurrentPoint(AssociatedObject).Properties.IsLeftButtonPressed)
                return;

            var button = FindTabButton(e.Source);
            if (button == null)
            {
                Console.WriteLine("[TabDragDrop] Button is null, ignoring press");
                return;
            }

            _dragStartPoint = e.GetPosition(AssociatedObject);
            _currentOffsetX = 0;
            _draggedButton = button;
            _draggedPresenter = button.FindAncestorOfType<ContentPresenter>();
            _originalIndex = GetTabIndex(button);
            _currentVisualIndex = _originalIndex;
            _tabWidth = button.Bounds.Width;
            _isDragging = false;

            Console.WriteLine($"[TabDragDrop] === POINTER PRESSED ===");
            Console.WriteLine($"[TabDragDrop]   Index: {_originalIndex}");
            Console.WriteLine($"[TabDragDrop]   Tab width: {_tabWidth}");
            Console.WriteLine($"[TabDragDrop]   Start point: {_dragStartPoint}");
        }

        private void OnPointerMoved(object? sender, PointerEventArgs e)
        {
            if (_isSwapping || _draggedButton == null || AssociatedObject == null)
                return;

            var currentPoint = e.GetPosition(AssociatedObject);
            var rawOffsetX = currentPoint.X - _dragStartPoint.X;

            if (!_isDragging)
            {
                if (Math.Abs(rawOffsetX) > DRAG_THRESHOLD)
                {
                    Console.WriteLine($"[TabDragDrop] Threshold exceeded: {Math.Abs(rawOffsetX):F1}px");
                    StartDragging();
                }
                return;
            }

            var viewModel = AssociatedObject.DataContext as TabBarViewModel;
            if (viewModel == null)
                return;

            var leftLimit = -_originalIndex * (_tabWidth + TAB_SPACING);
            var maxRightShift = (viewModel.Tabs.Count - 1 - _originalIndex) * (_tabWidth + TAB_SPACING);
            var rightLimit = maxRightShift;

            _currentOffsetX = Math.Max(leftLimit, Math.Min(rightLimit, rawOffsetX));

            UpdateDraggedButton();
            UpdateVisualPositions();
        }

        private async void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (!_isDragging)
            {
                _draggedButton = null;
                _draggedPresenter = null;
                _originalIndex = -1;
                _currentVisualIndex = -1;
                return;
            }

            Console.WriteLine($"[TabDragDrop] === POINTER RELEASED ===");
            Console.WriteLine($"[TabDragDrop]   Original index: {_originalIndex}");
            Console.WriteLine($"[TabDragDrop]   Current visual index: {_currentVisualIndex}");
            Console.WriteLine($"[TabDragDrop]   Final offset: {_currentOffsetX:F1}px");

            _isDragging = false;
            _isSwapping = true;

            Console.WriteLine($"[TabDragDrop] Cancelling {_activeAnimations.Count} active animations");
            foreach (var cts in _activeAnimations.Values)
            {
                cts.Cancel();
            }
            _activeAnimations.Clear();
            _targetPositions.Clear();

            Console.WriteLine($"[TabDragDrop] === REMOVING ALL TRANSFORMS ===");
            var allButtons = GetAllButtons();
            Console.WriteLine($"[TabDragDrop]   Found {allButtons.Count} buttons");
            foreach (var button in allButtons)
            {
                Console.WriteLine($"[TabDragDrop]     Removing transform from button");
                button.RenderTransform = null;  // УБИРАЕМ полностью, не сбрасываем!
                button.Transitions = null;
            }

            var viewModel = AssociatedObject?.DataContext as TabBarViewModel;
            if (viewModel == null)
            {
                Console.WriteLine($"[TabDragDrop] ERROR: ViewModel is null");
                StopDragging();
                ResetState();
                _isSwapping = false;
                return;
            }

            if (_currentVisualIndex != _originalIndex)
            {
                Console.WriteLine($"[TabDragDrop] === APPLYING SWAP ===");
                Console.WriteLine($"[TabDragDrop]   From: {_originalIndex}");
                Console.WriteLine($"[TabDragDrop]   To: {_currentVisualIndex}");

                if (_currentVisualIndex > _originalIndex)
                {
                    Console.WriteLine($"[TabDragDrop]   Direction: RIGHT (forward swaps)");
                    for (int i = _originalIndex; i < _currentVisualIndex; i++)
                    {
                        Console.WriteLine($"[TabDragDrop]     Swap: {i} <-> {i + 1}");
                        viewModel.SwapTabs(i, i + 1);
                    }
                }
                else
                {
                    Console.WriteLine($"[TabDragDrop]   Direction: LEFT (backward swaps)");
                    for (int i = _originalIndex; i > _currentVisualIndex; i--)
                    {
                        Console.WriteLine($"[TabDragDrop]     Swap: {i} <-> {i - 1}");
                        viewModel.SwapTabs(i, i - 1);
                    }
                }

                viewModel.SaveTabsOrder();
            }
            else
            {
                Console.WriteLine($"[TabDragDrop] No swap needed (same position)");
            }

            StopDragging();
            ResetState();
            _isSwapping = false;
            Console.WriteLine($"[TabDragDrop] === DRAG COMPLETE ===");
        }

        private void ResetState()
        {
            _isDragging = false;
            _draggedButton = null;
            _draggedPresenter = null;
            _originalIndex = -1;
            _currentVisualIndex = -1;
            _currentOffsetX = 0;
        }

        private void StartDragging()
        {
            _isDragging = true;

            if (_draggedButton == null)
                return;

            Console.WriteLine($"[TabDragDrop] === START DRAGGING ===");

            Console.WriteLine($"[TabDragDrop]   Cancelling {_activeAnimations.Count} previous animations");
            foreach (var cts in _activeAnimations.Values)
            {
                cts.Cancel();
            }
            _activeAnimations.Clear();
            _targetPositions.Clear();

            var viewModel = AssociatedObject?.DataContext as TabBarViewModel;
            if (viewModel != null && _draggedButton.DataContext is DocumentTabViewModel tab)
            {
                viewModel.ActiveTab = tab;
                Console.WriteLine($"[TabDragDrop]   Activated tab: {tab.Title}");
            }

            if (_draggedPresenter != null)
            {
                _draggedPresenter.ZIndex = 1000;
                Console.WriteLine($"[TabDragDrop]   Set ZIndex=1000 on ContentPresenter");
            }

            _draggedButton.Transitions = null;
            Console.WriteLine($"[TabDragDrop] === DRAG STARTED ===");
        }

        private void StopDragging()
        {
            Console.WriteLine($"[TabDragDrop] === STOP DRAGGING ===");

            if (_draggedPresenter != null)
            {
                _draggedPresenter.ZIndex = 0;
                Console.WriteLine($"[TabDragDrop]   Reset dragged presenter ZIndex to 0");
            }

            var allButtons = GetAllButtons();
            Console.WriteLine($"[TabDragDrop]   Resetting ZIndex for {allButtons.Count} buttons");

            foreach (var button in allButtons)
            {
                var presenter = button.FindAncestorOfType<ContentPresenter>();
                if (presenter != null)
                {
                    presenter.ZIndex = 0;
                }
            }

            Console.WriteLine($"[TabDragDrop] === STOP COMPLETE ===");
        }

        private void UpdateDraggedButton()
        {
            if (_draggedButton == null)
                return;

            var transform = GetOrCreateTransform(_draggedButton);
            transform.X = _currentOffsetX;
            transform.Y = 0;
        }

        private void UpdateVisualPositions()
        {
            if (_originalIndex == -1 || AssociatedObject == null)
                return;

            var viewModel = AssociatedObject.DataContext as TabBarViewModel;
            if (viewModel == null)
                return;

            var allButtons = GetAllButtons();
            if (allButtons.Count == 0)
                return;

            int newVisualIndex = CalculateNewVisualIndex();

            if (newVisualIndex != _currentVisualIndex)
            {
                Console.WriteLine($"[TabDragDrop] === VISUAL INDEX CHANGED ===");
                Console.WriteLine($"[TabDragDrop]   From: {_currentVisualIndex}");
                Console.WriteLine($"[TabDragDrop]   To: {newVisualIndex}");
                Console.WriteLine($"[TabDragDrop]   Offset: {_currentOffsetX:F1}px");
                _currentVisualIndex = newVisualIndex;
            }

            for (int i = 0; i < allButtons.Count; i++)
            {
                if (i == _originalIndex)
                    continue;

                var button = allButtons[i];
                var transform = GetOrCreateTransform(button);

                double targetOffset = 0;

                if (_currentVisualIndex > _originalIndex)
                {
                    if (i > _originalIndex && i <= _currentVisualIndex)
                    {
                        targetOffset = -(_tabWidth + TAB_SPACING);
                    }
                }
                else if (_currentVisualIndex < _originalIndex)
                {
                    if (i >= _currentVisualIndex && i < _originalIndex)
                    {
                        targetOffset = _tabWidth + TAB_SPACING;
                    }
                }

                if (!_targetPositions.ContainsKey(transform) || Math.Abs(_targetPositions[transform] - targetOffset) > 0.1)
                {
                    var oldTarget = _targetPositions.ContainsKey(transform) ? _targetPositions[transform] : transform.X;
                    Console.WriteLine($"[TabDragDrop]   Button {i}: animating {oldTarget:F1} -> {targetOffset:F1}");
                    _targetPositions[transform] = targetOffset;
                    AnimateTransform(transform, targetOffset);
                }
            }
        }

        /// <summary>
        /// Плавно анимирует TranslateTransform.X к целевому значению
        /// </summary>
        private async void AnimateTransform(TranslateTransform transform, double targetX)
        {
            if (_activeAnimations.ContainsKey(transform))
            {
                _activeAnimations[transform].Cancel();
                _activeAnimations.Remove(transform);
            }

            var cts = new CancellationTokenSource();
            _activeAnimations[transform] = cts;

            try
            {
                var startX = transform.X;
                var duration = 250;
                var startTime = DateTime.Now;

                while (true)
                {
                    if (cts.Token.IsCancellationRequested)
                        break;

                    var elapsed = (DateTime.Now - startTime).TotalMilliseconds;
                    var progress = Math.Min(elapsed / duration, 1.0);
                    var easedProgress = EaseOutCubic(progress);

                    transform.X = startX + (targetX - startX) * easedProgress;

                    if (progress >= 1.0)
                        break;

                    await Task.Delay(2, cts.Token);
                }

                transform.X = targetX;
            }
            catch (TaskCanceledException)
            {
            }
            finally
            {
                if (_activeAnimations.ContainsKey(transform))
                {
                    _activeAnimations.Remove(transform);
                }
            }
        }

        /// <summary>
        /// Easing функция Cubic Out
        /// </summary>
        private double EaseOutCubic(double t)
        {
            return 1 - Math.Pow(1 - t, 3);
        }

        private int CalculateNewVisualIndex()
        {
            if (_originalIndex == -1 || AssociatedObject == null)
                return _originalIndex;

            var viewModel = AssociatedObject.DataContext as TabBarViewModel;
            if (viewModel == null)
                return _originalIndex;

            var tabsCount = viewModel.Tabs.Count;

            var positionsOffset = _currentOffsetX / (_tabWidth + TAB_SPACING);

            int newIndex = _originalIndex + (int)Math.Round(positionsOffset);

            newIndex = Math.Max(0, Math.Min(newIndex, tabsCount - 1));

            return newIndex;
        }

        private TranslateTransform GetOrCreateTransform(Button button)
        {
            if (button.RenderTransform is TranslateTransform transform)
                return transform;

            transform = new TranslateTransform();
            button.RenderTransform = transform;
            return transform;
        }

        private Button? FindTabButton(object? source)
        {
            var current = source as Control;

            while (current != null)
            {
                if (current is Button button && button.DataContext is DocumentTabViewModel)
                {
                    if (button.Bounds.Width > 50)
                    {
                        return button;
                    }
                }

                current = current.Parent as Control;
            }

            return null;
        }

        private int GetTabIndex(Button button)
        {
            if (AssociatedObject?.DataContext is not TabBarViewModel viewModel)
                return -1;

            var tab = button.DataContext as DocumentTabViewModel;
            if (tab == null)
                return -1;

            return viewModel.Tabs.IndexOf(tab);
        }

        private List<Button> GetAllButtons()
        {
            if (AssociatedObject == null)
                return new List<Button>();

            var panel = AssociatedObject.ItemsPanelRoot as StackPanel;
            if (panel == null)
                return new List<Button>();

            var buttons = new List<Button>();

            foreach (var child in panel.Children)
            {
                if (child is ContentPresenter presenter && presenter.Child is Button button)
                    buttons.Add(button);
                else if (child is Button directButton)
                    buttons.Add(directButton);
            }

            return buttons;
        }
    }
}