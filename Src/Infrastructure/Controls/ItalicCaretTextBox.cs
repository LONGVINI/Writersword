using System;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Serilog;

namespace Writersword.Infrastructure.Controls
{
    public sealed class ItalicCaretTextBox : TextBox
    {
        private static readonly ILogger _log = Log.ForContext<ItalicCaretTextBox>();

        protected override Type StyleKeyOverride => typeof(TextBox);

        private TextPresenter? _presenter;
        private ScrollViewer? _scrollViewer;
        private ItalicCaretAdorner? _adorner;
        private IDisposable? _caretIndexSub;
        private IDisposable? _selectionSub;

        // Флаг: GotFocus пришёл до создания adorner (в DataTemplate это норма).
        // Используется в SetupAdorner чтобы правильно инициализировать HasFocus.
        private bool _pendingFocus;

        static ItalicCaretTextBox()
        {
            CaretBrushProperty.OverrideDefaultValue<ItalicCaretTextBox>(Brushes.Transparent);
        }

        protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
        {
            base.OnApplyTemplate(e);
            _presenter = e.NameScope.Find<TextPresenter>("PART_TextPresenter");
            _scrollViewer = e.NameScope.Find<ScrollViewer>("PART_ScrollViewer");

            if (_presenter != null)
                _presenter.CaretBrush = Brushes.Transparent;
            CaretBrush = Brushes.Transparent;

            // SetupAdorner вызываем здесь — только после OnApplyTemplate
            // гарантированно есть _presenter. В DataTemplate/ItemsControl
            // OnAttachedToVisualTree приходит раньше OnApplyTemplate, поэтому
            // откладывать из OnAttachedToVisualTree было неверно.
            Dispatcher.UIThread.Post(SetupAdorner, DispatcherPriority.Render);
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);

            _caretIndexSub = this.GetObservable(CaretIndexProperty)
                .Subscribe(_ =>
                {
                    _adorner?.ResetBlink();
                    Dispatcher.UIThread.Post(() => _adorner?.InvalidateVisual(), DispatcherPriority.Render);
                });

            _selectionSub = this.GetObservable(SelectionStartProperty)
                .Subscribe(_ =>
                    Dispatcher.UIThread.Post(() => _adorner?.InvalidateVisual(), DispatcherPriority.Render));

            GotFocus += OnGotFocusHandler;
            LostFocus += OnLostFocusHandler;
            TextInput += OnTextInputHandler;
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);

            GotFocus -= OnGotFocusHandler;
            LostFocus -= OnLostFocusHandler;
            TextInput -= OnTextInputHandler;

            _caretIndexSub?.Dispose();
            _selectionSub?.Dispose();
            _caretIndexSub = null;
            _selectionSub = null;

            _adorner?.Dispose();
            _adorner = null;
            _pendingFocus = false;
        }

        private void SetupAdorner()
        {
            if (_presenter == null) return;
            if (_adorner != null) return;

            var layer = AdornerLayer.GetAdornerLayer(this);
            if (layer == null)
            {
                _log.Warning("ItalicCaretTextBox: AdornerLayer not found, italic caret will not render");
                return;
            }

            _adorner = new ItalicCaretAdorner(this, _presenter, _scrollViewer);
            _adorner.HasFocus = _pendingFocus;

            if (_adorner.HasFocus)
                _adorner.ResetBlink();

            AdornerLayer.SetAdornedElement(_adorner, this);
            layer.Children.Add(_adorner);
            Dispatcher.UIThread.Post(() => _adorner?.InvalidateVisual(), DispatcherPriority.Render);
        }

        private void OnGotFocusHandler(object? sender, RoutedEventArgs e)
        {
            _pendingFocus = true;
            if (_adorner != null) _adorner.HasFocus = true;
            _adorner?.ResetBlink();
            Dispatcher.UIThread.Post(() => _adorner?.InvalidateVisual(), DispatcherPriority.Render);
        }

        private void OnLostFocusHandler(object? sender, RoutedEventArgs e)
        {
            _pendingFocus = false;
            if (_adorner != null) _adorner.HasFocus = false;
            _adorner?.HideCaret();
            Dispatcher.UIThread.Post(() => _adorner?.InvalidateVisual(), DispatcherPriority.Render);
        }

        private void OnTextInputHandler(object? sender, TextInputEventArgs e)
        {
            _adorner?.ResetBlink();
            Dispatcher.UIThread.Post(() => _adorner?.InvalidateVisual(), DispatcherPriority.Render);
        }
    }

    internal sealed class ItalicCaretAdorner : Control
    {
        // Коэффициент наклона: горизонтальное смещение верха = высота * ItalicSlant.
        // 0.22 ≈ 12.5° — типичный угол italic в большинстве шрифтов.
        private const double ItalicSlant = 0.22;

        // Небольшой сдвиг каретки вправо для визуальной точности.
        private const double CaretXOffset = 2.0;

        private readonly TextBox _textBox;
        private readonly TextPresenter _presenter;
        private readonly ScrollViewer? _scrollViewer;
        private readonly DispatcherTimer _blinkTimer;
        private bool _caretVisible = true;
        private bool _disposed;

        internal bool HasFocus { get; set; }

        // Доступ к внутреннему _caretBounds TextPresenter через рефлексию.
        private static readonly FieldInfo? CaretBoundsField =
            typeof(TextPresenter).GetField("_caretBounds",
                BindingFlags.NonPublic | BindingFlags.Instance);

        public ItalicCaretAdorner(TextBox textBox, TextPresenter presenter, ScrollViewer? scrollViewer)
        {
            _textBox = textBox;
            _presenter = presenter;
            _scrollViewer = scrollViewer;
            IsHitTestVisible = false;

            _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
            _blinkTimer.Tick += OnBlink;
            _blinkTimer.Start();
        }

        private void OnBlink(object? sender, EventArgs e)
        {
            if (_disposed) return;
            _caretVisible = !_caretVisible;
            InvalidateVisual();
        }

        public void ResetBlink()
        {
            if (_disposed) return;
            _caretVisible = true;
            _blinkTimer.Stop();
            _blinkTimer.Start();
        }

        public void HideCaret()
        {
            if (_disposed) return;
            _caretVisible = false;
            _blinkTimer.Stop();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _blinkTimer.Tick -= OnBlink;
            _blinkTimer.Stop();
        }

        public override void Render(DrawingContext context)
        {
            if (_disposed || !HasFocus || !_caretVisible) return;

            var caretRect = GetCaretRect();

            // Ограничиваем высоту каретки размером шрифта — рефлексия может вернуть
            // высоту всего content area TextBox вместо высоты строки.
            double maxH = _textBox.FontSize * 1.4;
            double h = Math.Min(caretRect.Height > 0 ? caretRect.Height : maxH, maxH);

            var padding = _textBox.Padding;
            double scrollX = _scrollViewer?.Offset.X ?? 0;

            // Вычитаем scroll offset — иначе при переполнении каретка уходит
            // за правый край TextBox и исчезает.
            double x = caretRect.X - scrollX + padding.Left + CaretXOffset;

            // Вертикально центрируем в content area.
            double contentH = Math.Max(1, _textBox.Bounds.Height - padding.Top - padding.Bottom);
            double y = padding.Top + (contentH - h) / 2.0;

            double slantOffset = h * ItalicSlant;
            var pen = new Pen(_textBox.Foreground ?? Brushes.Gray, 1.5, lineCap: PenLineCap.Round);

            context.DrawLine(pen,
                new Point(x + slantOffset, y),
                new Point(x, y + h));
        }

        private Rect GetCaretRect()
        {
            double fallbackH = _textBox.FontSize * 1.3;

            // Основной способ: рефлексия _caretBounds — обновляется самим Avalonia
            // при каждом изменении позиции каретки в TextPresenter.
            if (CaretBoundsField?.GetValue(_presenter) is Rect rf && rf.Height > 0)
                return rf;

            // Fallback: TextLayout.HitTestTextPosition.
            try
            {
                var layout = _presenter.TextLayout;
                if (layout != null)
                {
                    int len = _presenter.Text?.Length ?? 0;
                    int index = Math.Clamp(_textBox.CaretIndex, 0, len);
                    var rect = layout.HitTestTextPosition(index);
                    return new Rect(rect.X, rect.Y, 1, rect.Height > 0 ? rect.Height : fallbackH);
                }
            }
            catch { }

            // Последний fallback: начало поля с оценочной высотой.
            return new Rect(0, 0, 1, fallbackH);
        }
    }
}