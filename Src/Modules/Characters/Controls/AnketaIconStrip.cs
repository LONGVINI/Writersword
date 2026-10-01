using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Writersword.Infrastructure.Behaviours;

namespace Writersword.Modules.Characters.Controls
{
    /// <summary>Значок анкеты для полосы: что рисовать и что сказать в подсказке.</summary>
    public sealed record AnketaGlyph(string Id, string Icon, string IconColor, string Name, string Description);

    /// <summary>
    /// Полоса значков анкет у неактивного блока шаблона. Все значки рисуются
    /// одним контролом, а не по контролу на значок: шаблонов могут быть
    /// сотни, и в каждом по десятку анкет. У выбранного блока вместо полосы
    /// стоят настоящие значки — их двигают и правят, — а здесь только
    /// картинка и подсказка под указателем.
    /// </summary>
    public class AnketaIconStrip : Control
    {
        public static readonly StyledProperty<IReadOnlyList<AnketaGlyph>?> ItemsProperty =
            AvaloniaProperty.Register<AnketaIconStrip, IReadOnlyList<AnketaGlyph>?>(nameof(Items));

        public static readonly StyledProperty<double> IconSizeProperty =
            AvaloniaProperty.Register<AnketaIconStrip, double>(nameof(IconSize), 32d);

        public static readonly StyledProperty<double> SpacingProperty =
            AvaloniaProperty.Register<AnketaIconStrip, double>(nameof(Spacing), 6d);

        static AnketaIconStrip()
        {
            AffectsRender<AnketaIconStrip>(ItemsProperty, IconSizeProperty, SpacingProperty);
            AffectsMeasure<AnketaIconStrip>(ItemsProperty, IconSizeProperty, SpacingProperty);
        }

        public IReadOnlyList<AnketaGlyph>? Items
        {
            get => GetValue(ItemsProperty);
            set => SetValue(ItemsProperty, value);
        }

        public double IconSize
        {
            get => GetValue(IconSizeProperty);
            set => SetValue(IconSizeProperty, value);
        }

        public double Spacing
        {
            get => GetValue(SpacingProperty);
            set => SetValue(SpacingProperty, value);
        }

        private int Count => Items?.Count ?? 0;

        private int Columns(double width)
        {
            var step = IconSize + Spacing;
            return Math.Max(1, (int)Math.Floor((width + Spacing) / step));
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var count = Count;
            if (count == 0) return new Size(0, 0);

            var width = double.IsInfinity(availableSize.Width)
                ? count * (IconSize + Spacing) - Spacing
                : availableSize.Width;
            var columns = Columns(width);
            var rows = (int)Math.Ceiling(count / (double)columns);
            var usedWidth = Math.Min(count, columns) * (IconSize + Spacing) - Spacing;
            return new Size(usedWidth, rows * (IconSize + Spacing) - Spacing);
        }

        private Rect RectOf(int index, int columns)
        {
            var row = index / columns;
            var col = index % columns;
            return new Rect(col * (IconSize + Spacing), row * (IconSize + Spacing), IconSize, IconSize);
        }

        public override void Render(DrawingContext context)
        {
            var items = Items;
            if (items == null || items.Count == 0) return;

            // Прозрачная подложка — чтобы промежутки ловили указатель.
            context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

            var columns = Columns(Bounds.Width);
            for (int i = 0; i < items.Count; i++)
            {
                var glyph = items[i];
                AnketaIconBadge.Draw(context, RectOf(i, columns), glyph.Icon, glyph.IconColor);
            }
        }

        private int IndexAt(Point p)
        {
            var items = Items;
            if (items == null) return -1;

            var columns = Columns(Bounds.Width);
            for (int i = 0; i < items.Count; i++)
                if (RectOf(i, columns).Contains(p)) return i;
            return -1;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);

            var index = IndexAt(e.GetPosition(this));
            if (index < 0 || Items is not { } items)
            {
                TooltipBehavior.HideSpot(this);
                return;
            }

            var glyph = items[index];
            var rect = RectOf(index, Columns(Bounds.Width));
            TooltipBehavior.ShowSpot(this, glyph.Id + "#" + index, rect.Center.X, glyph.Name, glyph.Description);
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            TooltipBehavior.HideSpot(this);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            TooltipBehavior.HideSpot(this);
        }
    }
}
