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
        AvaloniaProperty.Register<CharactersFolderHeaderPanel, double>(nameof(LeftPreferredWidth), 250.0);

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

    // Запас сверх измеренного контента TextBox чтобы гарантировать scroll=0.
    // TextBox.DesiredSize включает padding; без запаса рендер может дать на 1-2px
    // меньше чем нужно тексту → TextBox скроллит вправо → Г обрезается слева.
    private const double TextBoxScrollBuffer = 8.0;

    // Кешируем натуральные (infinite) ширины детей из MeasureOverride,
    // чтобы ArrangeOverride не зависел от DesiredSize после constrained-меры.
    // При star-колонке в ScrollViewer Measure получает 0px, Arrange — реальную ширину;
    // без кеша стаканированный DesiredSize даёт неверные nameAlloc в Arrange.
    private double _naturalNameWidth;
    private double _naturalCommentWidth;

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
                        // TextChanged event вместо GetObservable(TextProperty):
                        // GetObservable на AvaloniaProperty не всегда стреляет при
                        // пользовательском вводе в сочетании с ReflectionBinding.
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

    // Вычисляет ширины для имени и комментария по заданному available.
    // Принимает натуральные (unconstrained) ширины детей, а не DesiredSize после
    // constrained-меры — это критично для корректной работы в star-колонке Grid
    // внутри ScrollViewer, где Arrange получает реальную ширину, а Measure — нет.

    private (double nameAlloc, double commentAlloc) ComputeAllocations(
        double available,
        double nameNaturalWidth,
        double commentNaturalWidth,
        bool nameEditing)
    {
        double nameAlloc;
        double commentAlloc;

        if (nameEditing)
        {
            // Минимум LeftPreferredWidth (250px), растёт по контенту.
            // nameAlloc = ровно столько, сколько нужно тексту — не заполняет строку целиком.
            double nameDesired = Math.Max(nameNaturalWidth + TextBoxScrollBuffer, LeftPreferredWidth);
            double commentNatural = commentNaturalWidth + VisualBuffer;

            if (nameDesired + commentNatural <= available)
            {
                nameAlloc = nameDesired;
                commentAlloc = commentNatural;
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
            double nameNatural = nameNaturalWidth;
            double commentNatural = commentNaturalWidth + VisualBuffer;

            if (nameNatural + commentNatural <= available)
            {
                // Имя занимает ровно свой контент — Button НЕ растягивается на всю строку.
                // Клик в пустое пространство правее текста не попадает в Button и не
                // запускает StartRenameCommand.
                nameAlloc = nameNatural;
                commentAlloc = commentNatural;
            }
            else
            {
                double availableForComment = available - nameNatural;

                if (availableForComment >= RightMinWidth + VisualBuffer)
                {
                    nameAlloc = nameNatural;
                    commentAlloc = availableForComment;
                }
                else
                {
                    commentAlloc = RightMinWidth + VisualBuffer;
                    nameAlloc = Math.Max(LeftMinWidth, available - commentAlloc);
                }
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

        // Сохраняем натуральные ширины до constrained-меры — они нужны в ArrangeOverride.
        _naturalNameWidth = nameChild.DesiredSize.Width;
        _naturalCommentWidth = commentChild.DesiredSize.Width;

        // Если пришла бесконечная ширина (star-колонка в ScrollViewer) — используем
        // preferred суммарную ширину как оценку для первого прохода Measure.
        // Реальная раскладка будет пересчитана в ArrangeOverride по finalSize.
        double available = double.IsInfinity(availableSize.Width)
            ? LeftPreferredWidth + RightPreferredWidth
            : availableSize.Width;

        bool nameEditing = HasVisibleTextBox(nameChild);

        _log.Debug(
            "FolderHeaderPanel Measure: availableSize.W={AW}, available={A}, nameEditing={NE}, naturalName={NN}, naturalComment={NC}",
            availableSize.Width, available, nameEditing,
            _naturalNameWidth, _naturalCommentWidth);

        var (nameAlloc, commentAlloc) = ComputeAllocations(
            available,
            _naturalNameWidth,
            _naturalCommentWidth,
            nameEditing);

        _log.Debug(
            "FolderHeaderPanel Measure result: nameAlloc={NA}, commentAlloc={CA}",
            nameAlloc, commentAlloc);

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

        // Используем кешированные натуральные ширины, а не DesiredSize после constrained-меры.
        // Пересчитываем аллокации по реальному finalSize.Width — он отличается от available
        // в Measure когда панель находится в star-колонке Grid внутри ScrollViewer.
        bool nameEditing = HasVisibleTextBox(nameChild);

        var (nameAlloc, commentAlloc) = ComputeAllocations(
            finalSize.Width,
            _naturalNameWidth,
            _naturalCommentWidth,
            nameEditing);

        double commentLeft = Math.Max(nameAlloc, finalSize.Width - commentAlloc);
        double commentWidth = Math.Max(0, finalSize.Width - commentLeft);

        _log.Debug(
            "FolderHeaderPanel Arrange: finalSize.W={FW}, nameEditing={NE}, naturalName={NN}, naturalComment={NC}, nameAlloc={NA}, commentAlloc={CA}, commentLeft={CL}, commentWidth={CW}",
            finalSize.Width, nameEditing,
            _naturalNameWidth, _naturalCommentWidth,
            nameAlloc, commentAlloc, commentLeft, commentWidth);

        // nameChild arrangeится строго по nameAlloc — и при редактировании, и при отображении
        // Button не растягивается на всю строку, занимает ровно столько, сколько нужно тексту.
        Children[0].Arrange(new Rect(0, 0, nameAlloc, finalSize.Height));
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