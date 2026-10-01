using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Writersword.Modules.Characters.Models;

namespace Writersword.Modules.Characters.Controls
{
    /// <summary>
    /// Значок анкеты: фигура её цвета на подложке того же цвета, только
    /// бледной. Один и тот же везде — в библиотеке анкет, в шаблонах, в
    /// заголовке раздела карточки, в меню подключения, — чтобы анкету узнавали
    /// по значку в любом месте.
    ///
    /// Фигура центруется по своим настоящим границам, а не растягивается
    /// Stretch: тот прижимает очертание к углу поля, и несимметричные значки
    /// стояли бы криво (та же история, что у значков меток).
    /// </summary>
    public class AnketaIconBadge : Control
    {
        public static readonly StyledProperty<string?> IconProperty =
            AvaloniaProperty.Register<AnketaIconBadge, string?>(nameof(Icon));

        public static readonly StyledProperty<string?> IconColorProperty =
            AvaloniaProperty.Register<AnketaIconBadge, string?>(nameof(IconColor));

        public static readonly StyledProperty<double> SizeProperty =
            AvaloniaProperty.Register<AnketaIconBadge, double>(nameof(Size), 26d);

        static AnketaIconBadge()
        {
            AffectsRender<AnketaIconBadge>(IconProperty, IconColorProperty, SizeProperty);
            AffectsMeasure<AnketaIconBadge>(SizeProperty);
        }

        public string? Icon
        {
            get => GetValue(IconProperty);
            set => SetValue(IconProperty, value);
        }

        public string? IconColor
        {
            get => GetValue(IconColorProperty);
            set => SetValue(IconColorProperty, value);
        }

        public double Size
        {
            get => GetValue(SizeProperty);
            set => SetValue(SizeProperty, value);
        }

        // Геометрия разбирается один раз на ключ: значков в списке много, а
        // разбор строки пути на каждой отрисовке был бы лишней работой.
        private static readonly Dictionary<string, Geometry> Cache = new(StringComparer.Ordinal);

        private static Geometry GeometryOf(string? key)
        {
            var data = CharacterAnketaIcons.GetPathData(key);
            if (!Cache.TryGetValue(data, out var geometry))
            {
                geometry = Geometry.Parse(data);
                Cache[data] = geometry;
            }
            return geometry;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var side = Math.Max(8, Size);
            return new Size(side, side);
        }

        public override void Render(DrawingContext context)
        {
            var side = Math.Min(Bounds.Width, Bounds.Height);
            if (side <= 0) return;

            var rect = new Rect((Bounds.Width - side) / 2, (Bounds.Height - side) / 2, side, side);
            Draw(context, rect, Icon, IconColor);
        }

        /// <summary>Геометрия значка по ключу — разобранная один раз.</summary>
        public static Geometry GetGeometry(string? key) => GeometryOf(key);

        /// <summary>
        /// Нарисовать значок в квадрат rect. Общий для этого контрола и для
        /// полосы значков в блоке шаблона: там их рисуется много одним
        /// контролом, а выглядеть они должны одинаково.
        /// </summary>
        public static void Draw(DrawingContext context, Rect rect, string? icon, string? iconColor)
        {
            var side = Math.Min(rect.Width, rect.Height);
            if (side <= 0) return;

            var colorText = string.IsNullOrWhiteSpace(iconColor) ? CharacterAnketaIcons.DefaultColor : iconColor;
            if (!Color.TryParse(colorText, out var color))
                Color.TryParse(CharacterAnketaIcons.DefaultColor, out color);

            var radius = side * 0.28;
            context.DrawRectangle(new SolidColorBrush(color, 0.2), null, rect, radius, radius);

            var geometry = GeometryOf(icon);
            var bounds = geometry.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0) return;

            var box = side * 0.58;
            var scale = Math.Min(box / bounds.Width, box / bounds.Height);
            var matrix =
                Matrix.CreateTranslation(-bounds.X - bounds.Width / 2, -bounds.Y - bounds.Height / 2) *
                Matrix.CreateScale(scale, scale) *
                Matrix.CreateTranslation(rect.Center.X, rect.Center.Y);

            using (context.PushTransform(matrix))
            {
                context.DrawGeometry(new SolidColorBrush(color), null, geometry);
            }
        }
    }
}
