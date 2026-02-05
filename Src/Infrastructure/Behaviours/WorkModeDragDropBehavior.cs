using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Xaml.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Writersword.Core.Models.WorkModes;
using Writersword.ViewModels.Components;

namespace Writersword.Behaviors
{
    /// <summary>
    /// Behavior для перетаскивания кнопок WorkMode с визуальной анимацией
    /// </summary>
    public class WorkModeDragDropBehavior : Behavior<ItemsControl>
    {
        private const double DRAG_THRESHOLD = 10;
        private const double BUTTON_SPACING = 5;

        public static bool IsDragging { get; private set; } = false;

        private ILogger<WorkModeDragDropBehavior>? _logger;

        private Point _dragStartPoint;
        private double _currentOffsetX = 0;
        private bool _isDragging = false;
        private bool _isSwapping = false;
        private Button? _draggedButton = null;
        private ContentPresenter? _draggedPresenter = null;
        private int _originalIndex = -1;
        private int _currentVisualIndex = -1;

        private double _draggedWidth = 0;

        private double[] _buttonWidths = Array.Empty<double>();

        private Dictionary<TranslateTransform, CancellationTokenSource> _activeAnimations = new();
        private Dictionary<TranslateTransform, double> _targetPositions = new();

        protected override void OnAttached()
        {
            base.OnAttached();

            _logger = App.Services.GetService<ILogger<WorkModeDragDropBehavior>>();

            if (AssociatedObject != null)
            {
                AssociatedObject.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, handledEventsToo: true);
                AssociatedObject.AddHandler(InputElement.PointerMovedEvent, OnPointerMoved, handledEventsToo: true);
                AssociatedObject.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased, handledEventsToo: true);
                _logger?.LogDebug("Behavior attached");
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

            var button = FindWorkModeButton(e.Source);
            if (button == null) return;

            _dragStartPoint = e.GetPosition(AssociatedObject);
            _currentOffsetX = 0;
            _draggedButton = button;
            _draggedPresenter = button.FindAncestorOfType<ContentPresenter>();
            _originalIndex = GetWorkModeIndex(button);
            _currentVisualIndex = _originalIndex;
            _isDragging = false;

            _logger?.LogDebug("Pointer pressed - Index: {Index}, Button width: {Width}", _originalIndex, button.Bounds.Width);

            button.Classes.Add("dragging");
            _logger?.LogDebug("Added class 'dragging'");

            ActivateWorkModeImmediately();
        }

        private void OnPointerMoved(object? sender, PointerEventArgs e)
        {
            if (_draggedButton == null || AssociatedObject == null) return;

            var currentPoint = e.GetPosition(AssociatedObject);
            var rawOffsetX = currentPoint.X - _dragStartPoint.X;

            if (!_isDragging)
            {
                if (Math.Abs(rawOffsetX) > DRAG_THRESHOLD)
                {
                    _logger?.LogDebug("Threshold exceeded: {Offset:F1}px", Math.Abs(rawOffsetX));
                    StartDragging();
                }
                return;
            }

            var viewModel = AssociatedObject.DataContext as WorkModeBarViewModel;
            if (viewModel == null) return;

            var leftLimit = CalculateOffsetToIndex(0);
            var rightLimit = CalculateOffsetToIndex(viewModel.WorkModes.Count - 1);
            _currentOffsetX = Math.Max(leftLimit, Math.Min(rightLimit, rawOffsetX));

            UpdateDraggedButton();
            UpdateVisualPositions();
        }

        private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_draggedButton != null)
            {
                _draggedButton.Classes.Remove("dragging");
                _logger?.LogDebug("Removed class 'dragging'");
            }

            if (!_isDragging)
            {
                ResetState();
                return;
            }

            _logger?.LogDebug("Pointer released - Original: {Original}, Visual: {Visual}, Offset: {Offset:F1}px", _originalIndex, _currentVisualIndex, _currentOffsetX);

            _isDragging = false;
            _isSwapping = true;

            foreach (var cts in _activeAnimations.Values) cts.Cancel();
            _activeAnimations.Clear();
            _targetPositions.Clear();

            var allButtons = GetAllButtons();
            _logger?.LogDebug("Removing all transforms - Found {Count} buttons", allButtons.Count);

            foreach (var button in allButtons)
            {
                button.RenderTransform = null;
                button.Transitions = null;
            }

            var viewModel = AssociatedObject?.DataContext as WorkModeBarViewModel;
            if (viewModel == null)
            {
                _logger?.LogError("ViewModel is null");
                StopDragging();
                ResetState();
                _isSwapping = false;
                return;
            }

            if (_currentVisualIndex != _originalIndex)
            {
                _logger?.LogDebug("Applying swap: {Original} -> {Visual}", _originalIndex, _currentVisualIndex);

                if (_currentVisualIndex > _originalIndex)
                {
                    for (int i = _originalIndex; i < _currentVisualIndex; i++)
                    {
                        _logger?.LogDebug("Swap: {A} <-> {B}", i, i + 1);
                        viewModel.SwapWorkModes(i, i + 1);
                    }
                }
                else
                {
                    for (int i = _originalIndex; i > _currentVisualIndex; i--)
                    {
                        _logger?.LogDebug("Swap: {A} <-> {B}", i, i - 1);
                        viewModel.SwapWorkModes(i, i - 1);
                    }
                }

                viewModel.SaveWorkModesOrder();
            }
            else
            {
                _logger?.LogDebug("No swap needed");
            }

            StopDragging();
            ResetState();
            _isSwapping = false;
            _logger?.LogDebug("Drag complete");
        }

        private void ResetState()
        {
            if (_draggedButton != null)
            {
                _draggedButton.Classes.Remove("dragging");
                _logger?.LogDebug("ResetState: Removed class 'dragging'");
            }

            _isDragging = false;
            IsDragging = false;
            _draggedButton = null;
            _draggedPresenter = null;
            _originalIndex = -1;
            _currentVisualIndex = -1;
            _currentOffsetX = 0;
            _draggedWidth = 0;
            _buttonWidths = Array.Empty<double>();
        }

        private void StartDragging()
        {
            _isDragging = true;
            IsDragging = true;
            if (_draggedButton == null) return;

            foreach (var cts in _activeAnimations.Values) cts.Cancel();
            _activeAnimations.Clear();
            _targetPositions.Clear();

            var allButtons = GetAllButtons();
            _buttonWidths = allButtons.Select(b => b.Bounds.Width).ToArray();
            _draggedWidth = _originalIndex < _buttonWidths.Length ? _buttonWidths[_originalIndex] : 0;

            _logger?.LogDebug("Start dragging - Widths: [{Widths}], Dragged: {DraggedWidth:F0}",
                string.Join(", ", _buttonWidths.Select(w => $"{w:F0}")), _draggedWidth);

            if (_draggedPresenter != null)
                _draggedPresenter.ZIndex = 1000;

            _draggedButton.Transitions = null;
        }

        private void StopDragging()
        {
            if (_draggedPresenter != null)
                _draggedPresenter.ZIndex = 0;

            var allButtons = GetAllButtons();
            foreach (var button in allButtons)
            {
                var presenter = button.FindAncestorOfType<ContentPresenter>();
                if (presenter != null) presenter.ZIndex = 0;
            }

            foreach (var button in allButtons)
            {
                var t = button.RenderTransform as TranslateTransform;
                if (t != null) { t.X = 0; t.Y = 0; }
                button.Transitions = null;
            }
        }

        private void UpdateDraggedButton()
        {
            if (_draggedButton == null) return;
            var transform = GetOrCreateTransform(_draggedButton);
            transform.X = _currentOffsetX;
            transform.Y = 0;
        }

        private void UpdateVisualPositions()
        {
            if (_originalIndex == -1 || AssociatedObject == null) return;

            var viewModel = AssociatedObject.DataContext as WorkModeBarViewModel;
            if (viewModel == null) return;

            var allButtons = GetAllButtons();
            if (allButtons.Count == 0) return;

            int newVisualIndex = CalculateNewVisualIndex();

            if (newVisualIndex != _currentVisualIndex)
            {
                _logger?.LogDebug("Visual index: {Old} -> {New}, Offset: {Offset:F1}px", _currentVisualIndex, newVisualIndex, _currentOffsetX);
                _currentVisualIndex = newVisualIndex;
            }

            for (int i = 0; i < allButtons.Count; i++)
            {
                if (i == _originalIndex) continue;

                var button = allButtons[i];
                var transform = GetOrCreateTransform(button);
                double targetOffset = 0;

                if (_currentVisualIndex > _originalIndex)
                {
                    if (i > _originalIndex && i <= _currentVisualIndex)
                        targetOffset = -(_draggedWidth + BUTTON_SPACING);
                }
                else if (_currentVisualIndex < _originalIndex)
                {
                    if (i >= _currentVisualIndex && i < _originalIndex)
                        targetOffset = _draggedWidth + BUTTON_SPACING;
                }

                if (!_targetPositions.ContainsKey(transform) || Math.Abs(_targetPositions[transform] - targetOffset) > 0.1)
                {
                    _logger?.LogDebug("Button {Index}: {Old:F1} -> {New:F1}", i,
                        _targetPositions.ContainsKey(transform) ? _targetPositions[transform] : transform.X, targetOffset);
                    _targetPositions[transform] = targetOffset;
                    AnimateTransform(transform, targetOffset);
                }
            }
        }

        private double CalculateOffsetToIndex(int targetIndex)
        {
            if (_buttonWidths.Length == 0) return 0;

            double offset = 0;
            if (targetIndex > _originalIndex)
            {
                for (int i = _originalIndex + 1; i <= targetIndex && i < _buttonWidths.Length; i++)
                    offset += _buttonWidths[i] + BUTTON_SPACING;
            }
            else if (targetIndex < _originalIndex)
            {
                for (int i = _originalIndex - 1; i >= targetIndex && i >= 0; i--)
                    offset -= _buttonWidths[i] + BUTTON_SPACING;
            }
            return offset;
        }

        private int CalculateNewVisualIndex()
        {
            if (_originalIndex == -1 || _buttonWidths.Length == 0) return _originalIndex;

            var viewModel = AssociatedObject?.DataContext as WorkModeBarViewModel;
            if (viewModel == null) return _originalIndex;

            double accumulated = 0;
            int newIndex = _originalIndex;

            if (_currentOffsetX > 0)
            {
                for (int i = _originalIndex + 1; i < viewModel.WorkModes.Count && i < _buttonWidths.Length; i++)
                {
                    accumulated += _buttonWidths[i] + BUTTON_SPACING;
                    if (_currentOffsetX >= accumulated * 0.5)
                        newIndex = i;
                    else
                        break;
                }
            }
            else if (_currentOffsetX < 0)
            {
                for (int i = _originalIndex - 1; i >= 0; i--)
                {
                    accumulated -= _buttonWidths[i] + BUTTON_SPACING;
                    if (_currentOffsetX <= accumulated * 0.5)
                        newIndex = i;
                    else
                        break;
                }
            }

            return newIndex;
        }

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
                    if (cts.Token.IsCancellationRequested) break;

                    var elapsed = (DateTime.Now - startTime).TotalMilliseconds;
                    var progress = Math.Min(elapsed / duration, 1.0);
                    transform.X = startX + (targetX - startX) * EaseOutCubic(progress);

                    if (progress >= 1.0) break;
                    await Task.Delay(2, cts.Token);
                }
                transform.X = targetX;
            }
            catch (TaskCanceledException) { }
            finally
            {
                if (_activeAnimations.ContainsKey(transform))
                    _activeAnimations.Remove(transform);
            }
        }

        private double EaseOutCubic(double t) => 1 - Math.Pow(1 - t, 3);

        private TranslateTransform GetOrCreateTransform(Button button)
        {
            if (button.RenderTransform is TranslateTransform t) return t;
            t = new TranslateTransform();
            button.RenderTransform = t;
            return t;
        }

        private void ActivateWorkModeImmediately()
        {
            if (_draggedButton == null || AssociatedObject == null) return;

            var viewModel = AssociatedObject.DataContext as WorkModeBarViewModel;
            if (viewModel == null) return;

            var workMode = _draggedButton.DataContext as WorkMode;
            if (workMode == null) return;

            _logger?.LogDebug("Activating WorkMode: {Title}", workMode.Title);

            foreach (var wm in viewModel.WorkModes)
            {
                wm.IsActive = false;
            }

            workMode.IsActive = true;

            _logger?.LogDebug("Executing SwitchWorkModeCommand");
            viewModel.SwitchWorkModeCommand.Execute(workMode).Subscribe();
        }

        private Button? FindWorkModeButton(object? source)
        {
            var current = source as Control;
            while (current != null)
            {
                if (current is Button button && button.DataContext is WorkMode)
                    return button;
                current = current.Parent as Control;
            }
            return null;
        }

        private int GetWorkModeIndex(Button button)
        {
            if (AssociatedObject?.DataContext is not WorkModeBarViewModel viewModel) return -1;
            var workMode = button.DataContext as WorkMode;
            if (workMode == null) return -1;
            return viewModel.WorkModes.IndexOf(workMode);
        }

        private List<Button> GetAllButtons()
        {
            if (AssociatedObject == null) return new List<Button>();
            var panel = AssociatedObject.ItemsPanelRoot as StackPanel;
            if (panel == null) return new List<Button>();

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