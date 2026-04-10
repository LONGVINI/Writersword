using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
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
    /// Behavior для перетаскивания кнопок WorkMode с визуальной анимацией.
    /// Использует AttachedProperty вместо Avalonia.Xaml.Interactivity.
    /// Применение в XAML: behaviors:WorkModeDragDropBehavior.IsEnabled="True"
    /// </summary>
    public class WorkModeDragDropBehavior
    {
        // ── AttachedProperty API ──────────────────────────────────────────

        public static readonly AttachedProperty<bool> IsEnabledProperty =
            AvaloniaProperty.RegisterAttached<ItemsControl, bool>(
                "IsEnabled", typeof(WorkModeDragDropBehavior));

        private static readonly AttachedProperty<WorkModeDragDropBehavior?> InstanceProperty =
            AvaloniaProperty.RegisterAttached<ItemsControl, WorkModeDragDropBehavior?>(
                "Instance", typeof(WorkModeDragDropBehavior));

        static WorkModeDragDropBehavior()
        {
            IsEnabledProperty.Changed.AddClassHandler<ItemsControl>(OnIsEnabledChanged);
        }

        public static bool GetIsEnabled(ItemsControl element) =>
            element.GetValue(IsEnabledProperty);

        public static void SetIsEnabled(ItemsControl element, bool value) =>
            element.SetValue(IsEnabledProperty, value);

        private static void OnIsEnabledChanged(ItemsControl control,
            AvaloniaPropertyChangedEventArgs e)
        {
            if (e.NewValue is true)
            {
                var behavior = new WorkModeDragDropBehavior(control);
                control.SetValue(InstanceProperty, behavior);
                behavior.Attach();
            }
            else
            {
                var behavior = control.GetValue(InstanceProperty);
                behavior?.Detach();
                control.SetValue(InstanceProperty, null);
            }
        }

        // ── Состояние экземпляра ──────────────────────────────────────────

        private readonly ItemsControl _control;
        private ILogger<WorkModeDragDropBehavior>? _logger;

        private const double DRAG_THRESHOLD = 10;
        private const double BUTTON_SPACING = 5;

        public static bool IsDragging { get; private set; } = false;

        private Point _dragStartPoint;
        private double _currentOffsetX = 0;
        private bool _isDragging = false;
        private bool _isSwapping = false;

        private WorkMode? _draggedWorkMode = null;
        private Button? _draggedButton = null;
        private ContentPresenter? _draggedPresenter = null;

        private int _originalIndex = -1;
        private int _currentVisualIndex = -1;
        private double _draggedWidth = 0;
        private double[] _buttonWidths = Array.Empty<double>();

        private readonly Dictionary<TranslateTransform, CancellationTokenSource> _activeAnimations = new();
        private readonly Dictionary<TranslateTransform, double> _targetPositions = new();

        private WorkModeDragDropBehavior(ItemsControl control)
        {
            _control = control;
        }

        private void Attach()
        {
            _logger = App.Services.GetService<ILogger<WorkModeDragDropBehavior>>();

            _control.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, handledEventsToo: true);
            _control.AddHandler(InputElement.PointerMovedEvent, OnPointerMoved, handledEventsToo: true);
            _control.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased, handledEventsToo: true);
            _logger?.LogDebug("WorkModeDragDropBehavior attached");
        }

        private void Detach()
        {
            _control.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
            _control.RemoveHandler(InputElement.PointerMovedEvent, OnPointerMoved);
            _control.RemoveHandler(InputElement.PointerReleasedEvent, OnPointerReleased);
        }

        // ── Обработчики событий ───────────────────────────────────────────

        private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (_isSwapping || !e.GetCurrentPoint(_control).Properties.IsLeftButtonPressed)
                return;

            var button = FindWorkModeButton(e.Source);
            if (button == null) return;

            _draggedWorkMode = button.DataContext as WorkMode;
            _draggedButton = button;
            _draggedPresenter = button.FindAncestorOfType<ContentPresenter>();
            _dragStartPoint = e.GetPosition(_control);
            _currentOffsetX = 0;
            _originalIndex = GetWorkModeIndex(button);
            _currentVisualIndex = _originalIndex;
            _isDragging = false;

            _logger?.LogDebug("Pointer pressed - Index: {Index}, Button width: {Width}",
                _originalIndex, button.Bounds.Width);

            button.Classes.Add("dragging");
            _logger?.LogDebug("Added class 'dragging'");

            // Активируем WorkMode сразу при нажатии.
            // После этого LoadWorkModes может пересоздать кнопки —
            // в UpdateDraggedButton мы найдём актуальную кнопку по _draggedWorkMode.
            ActivateWorkModeImmediately();
        }

        private void OnPointerMoved(object? sender, PointerEventArgs e)
        {
            if (_draggedWorkMode == null) return;

            var currentPoint = e.GetPosition(_control);
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

            var viewModel = _control.DataContext as WorkModeBarViewModel;
            if (viewModel == null) return;

            var leftLimit = CalculateOffsetToIndex(0);
            var rightLimit = CalculateOffsetToIndex(viewModel.WorkModes.Count - 1);
            _currentOffsetX = Math.Max(leftLimit, Math.Min(rightLimit, rawOffsetX));

            UpdateDraggedButton();
            UpdateVisualPositions();
        }

        private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            var currentButton = GetCurrentDraggedButton();
            if (currentButton != null)
            {
                currentButton.Classes.Remove("dragging");
                _logger?.LogDebug("Removed class 'dragging'");
            }

            if (!_isDragging)
            {
                ResetState();
                return;
            }

            _logger?.LogDebug("Pointer released - Original: {Original}, Visual: {Visual}, Offset: {Offset:F1}px",
                _originalIndex, _currentVisualIndex, _currentOffsetX);

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

            var viewModel = _control.DataContext as WorkModeBarViewModel;
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
                _logger?.LogDebug("Applying swap: {Original} -> {Visual}",
                    _originalIndex, _currentVisualIndex);

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

        // ── Вспомогательные методы ────────────────────────────────────────

        private void ResetState()
        {
            var currentButton = GetCurrentDraggedButton();
            if (currentButton != null)
            {
                currentButton.Classes.Remove("dragging");
                _logger?.LogDebug("ResetState: Removed class 'dragging'");
            }

            _isDragging = false;
            IsDragging = false;
            _draggedWorkMode = null;
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

            foreach (var cts in _activeAnimations.Values) cts.Cancel();
            _activeAnimations.Clear();
            _targetPositions.Clear();

            // Находим актуальную кнопку — LoadWorkModes мог пересоздать ItemsControl
            var current = GetCurrentDraggedButton();
            if (current != null)
            {
                _draggedButton = current;
                _draggedPresenter = current.FindAncestorOfType<ContentPresenter>();
                _originalIndex = GetWorkModeIndex(current);
                _currentVisualIndex = _originalIndex;
            }

            var allButtons = GetAllButtons();
            _buttonWidths = allButtons.Select(b => b.Bounds.Width).ToArray();
            _draggedWidth = _originalIndex >= 0 && _originalIndex < _buttonWidths.Length
                ? _buttonWidths[_originalIndex]
                : 0;

            _logger?.LogDebug("Start dragging - Widths: [{Widths}], Dragged: {DraggedWidth:F0}",
                string.Join(", ", _buttonWidths.Select(w => $"{w:F0}")), _draggedWidth);

            if (_draggedPresenter != null)
                _draggedPresenter.ZIndex = 1000;

            if (_draggedButton != null)
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

                if (button.RenderTransform is TranslateTransform t)
                {
                    t.X = 0;
                    t.Y = 0;
                }
                button.Transitions = null;
            }
        }

        /// <summary>
        /// Найти актуальную кнопку по _draggedWorkMode.
        /// ItemsControl мог пересоздать кнопки после LoadWorkModes —
        /// поэтому ищем каждый раз заново, не полагаемся на сохранённую ссылку.
        /// </summary>
        private Button? GetCurrentDraggedButton()
        {
            if (_draggedWorkMode == null) return null;
            return GetAllButtons().FirstOrDefault(b => b.DataContext == _draggedWorkMode);
        }

        private void UpdateDraggedButton()
        {
            var current = GetCurrentDraggedButton();
            if (current == null) return;

            _draggedButton = current;

            var transform = GetOrCreateTransform(_draggedButton);
            transform.X = _currentOffsetX;
            transform.Y = 0;
        }

        private void UpdateVisualPositions()
        {
            if (_originalIndex == -1) return;

            var viewModel = _control.DataContext as WorkModeBarViewModel;
            if (viewModel == null) return;

            var allButtons = GetAllButtons();
            if (allButtons.Count == 0) return;

            int newVisualIndex = CalculateNewVisualIndex();

            if (newVisualIndex != _currentVisualIndex)
            {
                _logger?.LogDebug("Visual index: {Old} -> {New}, Offset: {Offset:F1}px",
                    _currentVisualIndex, newVisualIndex, _currentOffsetX);
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

                if (!_targetPositions.ContainsKey(transform) ||
                    Math.Abs(_targetPositions[transform] - targetOffset) > 0.1)
                {
                    _logger?.LogDebug("Button {Index}: {Old:F1} -> {New:F1}", i,
                        _targetPositions.ContainsKey(transform) ? _targetPositions[transform] : transform.X,
                        targetOffset);
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

            var viewModel = _control.DataContext as WorkModeBarViewModel;
            if (viewModel == null) return _originalIndex;

            double accumulated = 0;
            int newIndex = _originalIndex;

            if (_currentOffsetX > 0)
            {
                for (int i = _originalIndex + 1;
                     i < viewModel.WorkModes.Count && i < _buttonWidths.Length; i++)
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
            if (_draggedWorkMode == null) return;

            var viewModel = _control.DataContext as WorkModeBarViewModel;
            if (viewModel == null) return;

            _logger?.LogDebug("Activating WorkMode: {Title}", _draggedWorkMode.Title);

            foreach (var wm in viewModel.WorkModes)
                wm.IsActive = false;

            _draggedWorkMode.IsActive = true;

            _logger?.LogDebug("Executing SwitchWorkModeCommand");
            viewModel.SwitchWorkModeCommand.Execute(_draggedWorkMode).Subscribe();
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
            if (_control.DataContext is not WorkModeBarViewModel viewModel) return -1;
            var workMode = button.DataContext as WorkMode;
            if (workMode == null) return -1;
            return viewModel.WorkModes.IndexOf(workMode);
        }

        private List<Button> GetAllButtons()
        {
            var panel = _control.ItemsPanelRoot as StackPanel;
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