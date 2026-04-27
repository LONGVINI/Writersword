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

    private const double VisualBuffer = 12.0;
    private const double TextBoxScrollBuffer = 8.0;

    // Минимальная ширина блока имени в режиме просмотра (не редактирования).
    // Если текст короче — блок всё равно занимает минимум этого пространства,
    // давая пользователю удобную зону для клика.
    private const double NameViewMinWidth = 250.0;

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
                     .Subscribe(_ => InvalidateMeasure()));

            if (child is Panel childPanel)
            {
                foreach (Control grandchild in childPanel.Children)
                {
                    _childSubscriptions.Add(
                        grandchild.GetObservable(IsVisibleProperty)
                                  .Subscribe(_ => InvalidateMeasure()));

                    if (grandchild is TextBox tb)
                    {
                        void OnTextChanged(object? s, TextChangedEventArgs _) => InvalidateMeasure();
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

    private (double nameAlloc, double commentAlloc) ComputeAllocations(
        double available,
        double nameDesiredWidth,
        double commentDesiredWidth,
        bool nameEditing)
    {
        double nameAlloc;
        double commentAlloc;

        if (nameEditing)
        {
            double nameDesired = Math.Max(nameDesiredWidth + TextBoxScrollBuffer, LeftPreferredWidth);
            double commentNatural = commentDesiredWidth + VisualBuffer;

            if (nameDesired + commentNatural <= available)
            {
                commentAlloc = commentNatural;
                nameAlloc = available - commentAlloc;
            }
            else if (nameDesired + RightMinWidth + VisualBuffer <= available)
            {
                nameAlloc = nameDesired;
                commentAlloc = available - nameDesired;
            }
            else
            {
                commentAlloc = RightMinWidth + VisualBuffer;
                nameAlloc = Math.Max(LeftMinWidth, available - commentAlloc);
            }
        }
        else
        {
            double commentNatural = commentDesiredWidth + VisualBuffer;

            // Имя занимает максимум из натурального размера текста и минимума 250px.
            // При росте текста блок увеличивается вместе с ним.
            // Ограничиваем сверху чтобы не вытеснить комментарий за экран.
            double nameIdeal = Math.Max(nameDesiredWidth, NameViewMinWidth);

            if (nameIdeal + commentNatural <= available)
            {
                nameAlloc = nameIdeal;
                commentAlloc = commentNatural;
            }
            else if (nameDesiredWidth + RightMinWidth + VisualBuffer <= available)
            {
                // Текст не влезает с минимальным комментарием — сжимаем имя до натурального
                double availableForComment = available - nameDesiredWidth;
                if (availableForComment >= RightMinWidth + VisualBuffer)
                {
                    nameAlloc = nameDesiredWidth;
                    commentAlloc = availableForComment;
                }
                else
                {
                    commentAlloc = RightMinWidth + VisualBuffer;
                    nameAlloc = Math.Max(LeftMinWidth, available - commentAlloc);
                }
            }
            else
            {
                commentAlloc = RightMinWidth + VisualBuffer;
                nameAlloc = Math.Max(LeftMinWidth, available - commentAlloc);
            }
        }

        return (nameAlloc, commentAlloc);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count == 0)
            return default;

        if (Children.Count == 1)
        {
            Children[0].Measure(availableSize);
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

        var (nameAlloc, commentAlloc) = ComputeAllocations(
            available,
            nameChild.DesiredSize.Width,
            commentChild.DesiredSize.Width,
            nameEditing);

        nameChild.Measure(new Size(nameAlloc, availableSize.Height));
        commentChild.Measure(new Size(commentAlloc, availableSize.Height));

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

        var nameChild = Children[0];
        var commentChild = Children[1];

        bool nameEditing = HasVisibleTextBox(nameChild);

        var (nameAlloc, commentAlloc) = ComputeAllocations(
            finalSize.Width,
            nameChild.DesiredSize.Width,
            commentChild.DesiredSize.Width,
            nameEditing);

        double commentLeft = Math.Max(nameAlloc, finalSize.Width - commentAlloc);
        double commentWidth = Math.Max(0, finalSize.Width - commentLeft);

        Children[0].Arrange(new Rect(0, 0, commentLeft, finalSize.Height));
        Children[1].Arrange(new Rect(commentLeft, 0, commentWidth, finalSize.Height));

        return finalSize;
    }

    private sealed class ActionDisposable : IDisposable
    {
        private Action? _action;
        public ActionDisposable(Action action) => _action = action;
        public void Dispose() { _action?.Invoke(); _action = null; }
    }
}