using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Serilog;

namespace Writersword.Modules.Characters.Controls;

/// <summary>
/// Горизонтальная панель заголовка папки персонажей с последовательным сжатием.
///
/// Поведение:
/// - Имя — у левого края, natural/preferred ширина, растёт вправо.
/// - Комментарий — у правого края (finalSize), natural/preferred ширина, растёт влево.
/// - Между ними свободное пространство, которое исчезает по мере роста текста.
/// - Пространство исчезло → сначала сжимается комментарий до RightMinWidth.
/// - Комментарий на минимуме → сжимается имя до LeftMinWidth.
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
            // Видимость прямого дочернего элемента
            _childSubscriptions.Add(
                child.GetObservable(IsVisibleProperty)
                     .Subscribe(_ => InvalidateMeasure()));

            // Заходим внутрь дочерних Panel для подписки на TextBox-внуков.
            // Именно они меняют содержимое во время ввода текста, но их изменения
            // не проталкивают InvalidateMeasure вверх до этой панели — только
            // InvalidateArrange. Из-за этого MeasureOverride не вызывается при
            // наборе текста, _nameAlloc/_commentAlloc остаются устаревшими,
            // и бокс не растёт. Явная подписка на TextProperty это исправляет.
            if (child is Panel childPanel)
            {
                foreach (Control grandchild in childPanel.Children)
                {
                    _childSubscriptions.Add(
                        grandchild.GetObservable(IsVisibleProperty)
                                  .Subscribe(_ => InvalidateMeasure()));

                    if (grandchild is TextBox textBox)
                    {
                        _childSubscriptions.Add(
                            textBox.GetObservable(TextBox.TextProperty)
                                   .Subscribe(_ => Dispatcher.UIThread.Post(
                                       InvalidateMeasure, DispatcherPriority.Render)));
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

        double nameNatural = Math.Max(nameChild.DesiredSize.Width, LeftPreferredWidth);
        // Комментарий: зона = реальный размер текста без preferred-floor.
        // Это гарантирует что commentAlloc меняется при каждом символе →
        // InvalidateArrange вызывается → commentLeft обновляется → зона растёт влево.
        // Preferred используется только как порог сжатия (ниже).
        double commentNatural = commentChild.DesiredSize.Width;

        double available = double.IsInfinity(availableSize.Width)
            ? nameNatural + commentNatural
            : availableSize.Width;

        if (nameNatural + commentNatural <= available)
        {
            // Оба влезают — свободное пространство между ними
            _nameAlloc = nameNatural;
            _commentAlloc = commentNatural;
        }
        else
        {
            double spaceForComment = available - nameNatural;

            if (spaceForComment >= RightMinWidth)
            {
                // Имя целиком, комментарий сжат (выше минимума)
                _nameAlloc = nameNatural;
                _commentAlloc = spaceForComment;
            }
            else
            {
                // Комментарий на минимуме, сжимается имя
                _commentAlloc = RightMinWidth;
                _nameAlloc = Math.Max(LeftMinWidth, available - _commentAlloc);
            }
        }

        _log.Debug(
            "Measure: available={Available:F0} nameDesired={ND:F0} commentDesired={CD:F0} → nameAlloc={NA:F0} commentAlloc={CA:F0}",
            availableSize.Width, nameChild.DesiredSize.Width, commentChild.DesiredSize.Width, _nameAlloc, _commentAlloc);

        nameChild.Measure(new Size(_nameAlloc, availableSize.Height));
        commentChild.Measure(new Size(_commentAlloc, availableSize.Height));

        double height = Math.Max(nameChild.DesiredSize.Height, commentChild.DesiredSize.Height);
        return new Size(_nameAlloc + _commentAlloc, height);
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

        // Имя — у левого края, ширина _nameAlloc.
        // Комментарий — у правого края finalSize, ширина _commentAlloc.
        // Между ними: finalSize.Width - _nameAlloc - _commentAlloc (свободное пространство).
        double nameWidth = Math.Max(0, _nameAlloc);
        double commentLeft = Math.Max(nameWidth, finalSize.Width - _commentAlloc);
        double commentWidth = Math.Max(0, finalSize.Width - commentLeft);

        Children[0].Arrange(new Rect(0, 0, nameWidth, finalSize.Height));
        Children[1].Arrange(new Rect(commentLeft, 0, commentWidth, finalSize.Height));

        return finalSize;
    }
}