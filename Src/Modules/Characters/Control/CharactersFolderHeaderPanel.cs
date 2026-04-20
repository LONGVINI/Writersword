using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Serilog;

namespace Writersword.Modules.Characters.Controls;

/// <summary>
/// Горизонтальная панель заголовка папки персонажей с последовательным сжатием.
/// </summary>
public sealed class CharactersFolderHeaderPanel : Panel
{
    public static readonly StyledProperty<double> LeftMinWidthProperty =
        AvaloniaProperty.Register<CharactersFolderHeaderPanel, double>(nameof(LeftMinWidth), 55.0);

    public static readonly StyledProperty<double> RightMinWidthProperty =
        AvaloniaProperty.Register<CharactersFolderHeaderPanel, double>(nameof(RightMinWidth), 63.0);

    public static readonly StyledProperty<double> LeftPreferredWidthProperty =
        AvaloniaProperty.Register<CharactersFolderHeaderPanel, double>(nameof(LeftPreferredWidth), 260.0);

    public static readonly StyledProperty<double> RightPreferredWidthProperty =
        AvaloniaProperty.Register<CharactersFolderHeaderPanel, double>(nameof(RightPreferredWidth), 210.0);

    public double LeftMinWidth
    {
        get => GetValue(LeftMinWidthProperty);
        set => SetValue(LeftMinWidthProperty, value);
    }

    public double RightMinWidth
    {
        get => GetValue(RightMinWidthProperty);
        set => SetValue(RightMinWidthProperty, value);
    }

    public double LeftPreferredWidth
    {
        get => GetValue(LeftPreferredWidthProperty);
        set => SetValue(LeftPreferredWidthProperty, value);
    }

    public double RightPreferredWidth
    {
        get => GetValue(RightPreferredWidthProperty);
        set => SetValue(RightPreferredWidthProperty, value);
    }

    private static readonly ILogger _log = Log.ForContext<CharactersFolderHeaderPanel>();

    private double _nameAlloc;
    private double _commentAlloc;
    private const double VisualBuffer = 12.0;

    // Запас сверх измеренного контента TextBox чтобы гарантировать scroll=0.
    // TextBox.DesiredSize включает padding; без запаса рендер может дать на 1-2px
    // меньше чем нужно тексту → TextBox скроллит вправо → Г обрезается слева.
    private const double TextBoxScrollBuffer = 8.0;

    private readonly List<IDisposable> _childSubscriptions = new();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SubscribeToChildren();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ClearSubscriptions();
    }

    private void SubscribeToChildren()
    {
        ClearSubscriptions();
        foreach (var child in Children)
        {
            _childSubscriptions.Add(
                child.GetObservable(IsVisibleProperty)
                     .Subscribe(_ =>
                     {
                         InvalidateMeasure();
                     }));

            if (child is Panel childPanel)
            {
                foreach (Control grandchild in childPanel.Children)
                {
                    _childSubscriptions.Add(
                        grandchild.GetObservable(IsVisibleProperty)
                                  .Subscribe(_ =>
                                  {
                                      InvalidateMeasure();
                                  }));

                    if (grandchild is TextBox tb)
                    {
                        // TextChanged event вместо GetObservable(TextProperty):
                        // GetObservable на AvaloniaProperty не всегда стреляет при
                        // пользовательском вводе в сочетании с ReflectionBinding.
                        void OnTextChanged(object? s, TextChangedEventArgs _)
                        {
                            InvalidateMeasure();
                        }
                        tb.TextChanged += OnTextChanged;
                        _childSubscriptions.Add(new ActionDisposable(() => tb.TextChanged -= OnTextChanged));
                    }
                }
            }
        }
    }

    private void ClearSubscriptions()
    {
        foreach (var sub in _childSubscriptions)
            sub.Dispose();
        _childSubscriptions.Clear();
    }

    private static bool HasVisibleTextBox(Control container)
    {
        if (container is Panel panel)
            foreach (Control child in panel.Children)
                if (child is TextBox && child.IsVisible) return true;
        return false;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count == 0)
            return default;

        if (Children.Count == 1)
        {
            Children[0].Measure(availableSize);
            _nameAlloc = Children[0].DesiredSize.Width;
            _commentAlloc = 0;
            return Children[0].DesiredSize;
        }

        var nameChild = Children[0];
        var commentChild = Children[1];

        var infinite = new Size(double.PositiveInfinity, availableSize.Height);

        nameChild.Measure(infinite);
        commentChild.Measure(infinite);

        double available = double.IsInfinity(availableSize.Width)
            ? LeftPreferredWidth + RightPreferredWidth
            : availableSize.Width;

        bool nameEditing = HasVisibleTextBox(nameChild);


        if (nameEditing)
        {
            double nameDesired = Math.Max(nameChild.DesiredSize.Width + TextBoxScrollBuffer, LeftPreferredWidth);
            double commentNatural = commentChild.DesiredSize.Width + VisualBuffer;

            if (nameDesired + commentNatural <= available)
            {
                // Оба влезают на своих натуральных размерах.
                _nameAlloc = nameDesired;
                _commentAlloc = commentNatural;
            }
            else if (nameDesired + RightMinWidth + VisualBuffer <= available)
            {
                // Имя на желаемом размере, комментарий сжимается.
                _nameAlloc = nameDesired;
                _commentAlloc = available - nameDesired;
            }
            else
            {
                // Панель очень узкая: комментарий на минимуме, имя берёт остаток.
                _commentAlloc = RightMinWidth + VisualBuffer;
                _nameAlloc = Math.Max(LeftMinWidth, available - _commentAlloc);
            }
        }
        else
        {
            double nameNatural = Math.Max(nameChild.DesiredSize.Width, LeftPreferredWidth);
            double commentNatural = commentChild.DesiredSize.Width + VisualBuffer;

            if (nameNatural + commentNatural <= available)
            {
                _nameAlloc = nameNatural;
                _commentAlloc = commentNatural;
            }
            else
            {
                double availableForComment = available - nameNatural;

                if (availableForComment >= RightMinWidth + VisualBuffer)
                {
                    _nameAlloc = nameNatural;
                    _commentAlloc = availableForComment;
                }
                else
                {
                    _commentAlloc = RightMinWidth + VisualBuffer;
                    _nameAlloc = Math.Max(LeftMinWidth, available - _commentAlloc);
                }
            }
        }

        nameChild.Measure(new Size(_nameAlloc, availableSize.Height));
        commentChild.Measure(new Size(_commentAlloc, availableSize.Height));

        double height = Math.Max(nameChild.DesiredSize.Height, commentChild.DesiredSize.Height);
        return new Size(available, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count == 0)
            return finalSize;

        if (Children.Count == 1)
        {
            Children[0].Arrange(new Rect(finalSize));
            return finalSize;
        }

        double nameWidth = Math.Max(0, _nameAlloc);
        double commentLeft = Math.Max(nameWidth, finalSize.Width - _commentAlloc);
        double commentWidth = Math.Max(0, finalSize.Width - commentLeft);

        Children[0].Arrange(new Rect(0, 0, nameWidth, finalSize.Height));
        Children[1].Arrange(new Rect(commentLeft, 0, commentWidth, finalSize.Height));

        return finalSize;
    }

    // Вспомогательный класс для хранения event-подписки в виде IDisposable.
    private sealed class ActionDisposable : IDisposable
    {
        private Action? _action;
        public ActionDisposable(Action action) => _action = action;
        public void Dispose() { _action?.Invoke(); _action = null; }
    }
}