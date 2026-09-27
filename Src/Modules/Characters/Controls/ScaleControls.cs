using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace Writersword.Modules.Characters.Controls
{
    /// <summary>
    /// Общее у шкал-полей: число делений, выбранное деление, подсветка под
    /// курсором, подпись. Деления нумеруются с единицы; ноль — ничего не выбрано.
    ///
    /// Под курсором шкала показывает, что будет после щелчка: у шариков
    /// заполняются до наведённого, у полюсов отмечается наведённый. Подпись
    /// при этом тоже показывает наведённое — так выбирают словом, а не номером.
    /// Щелчок по уже выбранному делению снимает выбор: иначе поставленное по
    /// ошибке значение нечем убрать.
    /// </summary>
    public abstract class ScaleControlBase : Control
    {
        public static readonly StyledProperty<int> CountProperty =
            AvaloniaProperty.Register<ScaleControlBase, int>(nameof(Count), 5);

        public static readonly StyledProperty<int> ValueProperty =
            AvaloniaProperty.Register<ScaleControlBase, int>(nameof(Value),
                defaultBindingMode: BindingMode.TwoWay);

        /// <summary>Свой цвет поля. Пусто — акцентный цвет темы.</summary>
        public static readonly StyledProperty<IBrush?> AccentBrushProperty =
            AvaloniaProperty.Register<ScaleControlBase, IBrush?>(nameof(AccentBrush));

        /// <summary>
        /// Подписи по номерам делений, с нуля: нулевая — когда ничего не
        /// выбрано. Короче числа делений — недостающие подписи пустые.
        /// </summary>
        public static readonly StyledProperty<IReadOnlyList<string>?> CaptionsProperty =
            AvaloniaProperty.Register<ScaleControlBase, IReadOnlyList<string>?>(nameof(Captions));

        /// <summary>Шкала только показывает значение и не меняет его.</summary>
        public static readonly StyledProperty<bool> IsReadOnlyProperty =
            AvaloniaProperty.Register<ScaleControlBase, bool>(nameof(IsReadOnly));

        public static readonly DirectProperty<ScaleControlBase, string> CaptionProperty =
            AvaloniaProperty.RegisterDirect<ScaleControlBase, string>(nameof(Caption), o => o.Caption);

        public static readonly DirectProperty<ScaleControlBase, int> HoverIndexProperty =
            AvaloniaProperty.RegisterDirect<ScaleControlBase, int>(nameof(HoverIndex), o => o.HoverIndex);

        static ScaleControlBase()
        {
            AffectsRender<ScaleControlBase>(CountProperty, ValueProperty, AccentBrushProperty, IsReadOnlyProperty);
            AffectsMeasure<ScaleControlBase>(CountProperty);
        }

        protected ScaleControlBase()
        {
            Cursor = new Cursor(StandardCursorType.Hand);
        }

        public int Count
        {
            get => GetValue(CountProperty);
            set => SetValue(CountProperty, value);
        }

        public int Value
        {
            get => GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        public IBrush? AccentBrush
        {
            get => GetValue(AccentBrushProperty);
            set => SetValue(AccentBrushProperty, value);
        }

        public IReadOnlyList<string>? Captions
        {
            get => GetValue(CaptionsProperty);
            set => SetValue(CaptionsProperty, value);
        }

        public bool IsReadOnly
        {
            get => GetValue(IsReadOnlyProperty);
            set => SetValue(IsReadOnlyProperty, value);
        }

        private string _caption = string.Empty;

        /// <summary>Подпись наведённого деления, а без наведения — выбранного.</summary>
        public string Caption
        {
            get => _caption;
            private set => SetAndRaise(CaptionProperty, ref _caption, value);
        }

        private int _hoverIndex;

        /// <summary>Деление под курсором; ноль — курсора над шкалой нет.</summary>
        public int HoverIndex
        {
            get => _hoverIndex;
            private set
            {
                if (SetAndRaise(HoverIndexProperty, ref _hoverIndex, value))
                {
                    UpdateCaption();
                    InvalidateVisual();
                }
            }
        }

        protected int ClampedValue => Math.Clamp(Value, 0, Math.Max(0, Count));

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == ValueProperty ||
                change.Property == CaptionsProperty ||
                change.Property == CountProperty)
            {
                UpdateCaption();
            }

            // Цвета берутся из темы при отрисовке — смена темы должна
            // перерисовать шкалу. Свойство сверяется по имени: в этой версии
            // Avalonia его поле объявлено не на самом контроле.
            if (change.Property.Name == "ActualThemeVariant")
                InvalidateVisual();

            if (change.Property == IsReadOnlyProperty)
                Cursor = IsReadOnly ? Cursor.Default : new Cursor(StandardCursorType.Hand);
        }

        private void UpdateCaption()
        {
            var index = HoverIndex > 0 ? HoverIndex : ClampedValue;
            var captions = Captions;
            Caption = captions != null && index >= 0 && index < captions.Count
                ? captions[index] ?? string.Empty
                : string.Empty;
        }

        /// <summary>Деление под точкой, с единицы; ноль — мимо.</summary>
        protected abstract int HitIndex(Point point);

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (IsReadOnly) return;
            HoverIndex = HitIndex(e.GetPosition(this));
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            HoverIndex = 0;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (IsReadOnly) return;
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

            var index = HitIndex(e.GetPosition(this));
            if (index <= 0) return;

            SetCurrentValue(ValueProperty, index == ClampedValue ? 0 : index);
            e.Handled = true;
        }

        // ── Цвета ────────────────────────────────────────────────────────

        protected IBrush ResolveAccent() =>
            AccentBrush ?? FindBrush("AccentDefaultBrush") ?? Brushes.DarkOrange;

        protected IBrush ResolveEmptyStroke() =>
            FindBrush("BorderDefaultBrush") ?? Brushes.Gray;

        protected IBrush ResolveEmptyFill() =>
            FindBrush("BorderSubtleBrush") ?? Brushes.DimGray;

        private IBrush? FindBrush(string key) =>
            this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush
                ? brush
                : null;

        protected static IBrush WithOpacity(IBrush brush, double opacity)
        {
            if (brush is ISolidColorBrush solid)
                return new SolidColorBrush(solid.Color, solid.Opacity * opacity);

            return brush;
        }
    }

    /// <summary>
    /// Шарики. Обычная шкала заполняется до выбранного шарика: «вспыльчивость
    /// четыре из пяти». Шкала полюсов отмечает одно положение между двумя
    /// крайностями: «ближе к интроверту».
    /// </summary>
    public class BallScale : ScaleControlBase
    {
        public static readonly StyledProperty<bool> IsBipolarProperty =
            AvaloniaProperty.Register<BallScale, bool>(nameof(IsBipolar));

        public static readonly StyledProperty<double> BallSizeProperty =
            AvaloniaProperty.Register<BallScale, double>(nameof(BallSize), 16d);

        public static readonly StyledProperty<double> SpacingProperty =
            AvaloniaProperty.Register<BallScale, double>(nameof(Spacing), 6d);

        static BallScale()
        {
            AffectsRender<BallScale>(IsBipolarProperty, BallSizeProperty, SpacingProperty);
            AffectsMeasure<BallScale>(BallSizeProperty, SpacingProperty);
        }

        public bool IsBipolar
        {
            get => GetValue(IsBipolarProperty);
            set => SetValue(IsBipolarProperty, value);
        }

        public double BallSize
        {
            get => GetValue(BallSizeProperty);
            set => SetValue(BallSizeProperty, value);
        }

        public double Spacing
        {
            get => GetValue(SpacingProperty);
            set => SetValue(SpacingProperty, value);
        }

        // Запас по краям под увеличенный шарик под курсором: без него он
        // обрезался бы по габариту контрола.
        private const double HoverGrow = 2;

        protected override Size MeasureOverride(Size availableSize)
        {
            var count = Math.Max(0, Count);
            var width = count * BallSize + Math.Max(0, count - 1) * Spacing + HoverGrow * 2;
            return new Size(width, BallSize + HoverGrow * 2);
        }

        private double Pitch => BallSize + Spacing;

        protected override int HitIndex(Point point)
        {
            var count = Math.Max(0, Count);
            if (count == 0) return 0;

            // Промежуток между шариками относится к ближайшему, а не пустой:
            // иначе курсор, ведомый вдоль шкалы, мигал бы подсветкой.
            var x = point.X - HoverGrow + Spacing / 2;
            var index = (int)Math.Floor(x / Pitch) + 1;
            return Math.Clamp(index, 1, count);
        }

        private bool IsMarked(int index, int value) =>
            IsBipolar ? index == value : index <= value;

        public override void Render(DrawingContext context)
        {
            // Прозрачная подложка ловит указатель и в промежутках между шариками.
            context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

            var count = Math.Max(0, Count);
            if (count == 0) return;

            var accent = ResolveAccent();
            var emptyPen = new Pen(ResolveEmptyStroke(), 1.5);
            var accentPen = new Pen(accent, 1.5);
            var preview = WithOpacity(accent, 0.45);
            var fading = WithOpacity(accent, 0.35);

            var value = ClampedValue;
            var hover = HoverIndex;
            var radius = BallSize / 2;
            var cy = HoverGrow + radius;

            for (int i = 1; i <= count; i++)
            {
                var cx = HoverGrow + radius + (i - 1) * Pitch;
                var r = i == hover ? radius + HoverGrow * 0.75 : radius - 0.75;
                var center = new Point(cx, cy);

                bool marked = IsMarked(i, value);
                bool willMark = hover > 0 && IsMarked(i, hover);

                if (hover > 0)
                {
                    if (willMark && marked)
                        context.DrawEllipse(accent, accentPen, center, r, r);
                    else if (willMark)
                        context.DrawEllipse(preview, accentPen, center, r, r);
                    else if (marked)
                        // Отметка, которую щелчок снимет: гаснет наполовину.
                        context.DrawEllipse(fading, accentPen, center, r, r);
                    else
                        context.DrawEllipse(null, emptyPen, center, r, r);
                }
                else
                {
                    if (marked)
                        context.DrawEllipse(accent, accentPen, center, r, r);
                    else
                        context.DrawEllipse(null, emptyPen, center, r, r);
                }
            }
        }
    }

    /// <summary>
    /// Полоса из делений — для длинных шкал, где шариков было бы слишком много.
    /// Тянется на всю отведённую ширину, но не шире потолка: полоса во всю
    /// карточку читается хуже короткой.
    /// </summary>
    public class SegmentBar : ScaleControlBase
    {
        public static readonly StyledProperty<double> BarHeightProperty =
            AvaloniaProperty.Register<SegmentBar, double>(nameof(BarHeight), 10d);

        public static readonly StyledProperty<double> GapProperty =
            AvaloniaProperty.Register<SegmentBar, double>(nameof(Gap), 3d);

        static SegmentBar()
        {
            AffectsRender<SegmentBar>(BarHeightProperty, GapProperty);
            AffectsMeasure<SegmentBar>(BarHeightProperty);
        }

        public double BarHeight
        {
            get => GetValue(BarHeightProperty);
            set => SetValue(BarHeightProperty, value);
        }

        public double Gap
        {
            get => GetValue(GapProperty);
            set => SetValue(GapProperty, value);
        }

        private const double DefaultWidth = 220;

        protected override Size MeasureOverride(Size availableSize)
        {
            var width = double.IsInfinity(availableSize.Width)
                ? DefaultWidth
                : Math.Min(availableSize.Width, DefaultWidth);
            return new Size(width, BarHeight + 4);
        }

        private double SegmentWidth(int count) =>
            count <= 0 ? 0 : Math.Max(1, (Bounds.Width - Gap * (count - 1)) / count);

        protected override int HitIndex(Point point)
        {
            var count = Math.Max(0, Count);
            if (count == 0 || Bounds.Width <= 0) return 0;

            var index = (int)Math.Floor(point.X / (SegmentWidth(count) + Gap)) + 1;
            return Math.Clamp(index, 1, count);
        }

        public override void Render(DrawingContext context)
        {
            context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

            var count = Math.Max(0, Count);
            if (count == 0) return;

            var accent = ResolveAccent();
            var empty = ResolveEmptyFill();
            var preview = WithOpacity(accent, 0.45);
            var fading = WithOpacity(accent, 0.35);

            var value = ClampedValue;
            var hover = HoverIndex;
            var width = SegmentWidth(count);
            var top = (Bounds.Height - BarHeight) / 2;

            for (int i = 1; i <= count; i++)
            {
                var rect = new Rect((i - 1) * (width + Gap), top, width, BarHeight);

                bool marked = i <= value;
                bool willMark = hover > 0 && i <= hover;

                IBrush fill = hover > 0
                    ? (willMark ? (marked ? accent : preview) : (marked ? fading : empty))
                    : (marked ? accent : empty);

                context.DrawRectangle(fill, null, rect, 3, 3);
            }
        }
    }
}
