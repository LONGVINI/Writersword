using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Writersword.Core.Models.Project;
using Writersword.Infrastructure.Converters;
using Writersword.Modules.Characters.Models;
using Writersword.Modules.Characters.Services;

namespace Writersword.Modules.Characters.Controls
{
    /// <summary>
    /// Значок метки — единственное место, где он рисуется. До этого значок
    /// собирался разметкой в шести местах: на карточке списка, в шапке
    /// карточки персонажа, в фишке метки, в превью редактора и в наборе
    /// встроенных значков. Разметка везде разошлась, и в четырёх из шести
    /// мест фигура стояла криво.
    ///
    /// Причина перекоса — в том, как Avalonia растягивает фигуру: Path со
    /// Stretch прижимает очертание к левому верхнему углу отведённого поля,
    /// а не ставит по середине. Пока очертание близко к квадрату (череп,
    /// звезда), это незаметно; у капли оно четырнадцать на двадцать, и капля
    /// заметно уезжала влево. Здесь фигура центруется явно — по своим
    /// настоящим границам, а не по границам поля.
    /// </summary>
    public class CharacterLabelIcon : Control
    {
        /// <summary>Доля значка, отведённая фигуре, когда под ней есть подложка.</summary>
        private const double GlyphShareOnBackdrop = 0.60;

        /// <summary>
        /// Цвет фигуры, когда он у метки не задан. Белая фигура на цветном
        /// кружке — вид по умолчанию: цветом метка отличается кружком, а
        /// фигура остаётся читаемой на любом из них.
        /// </summary>
        private static readonly IBrush DefaultGlyphBrush = Brushes.White;

        public static readonly StyledProperty<CharacterLabel?> LabelProperty =
            AvaloniaProperty.Register<CharacterLabelIcon, CharacterLabel?>(nameof(Label));

        /// <summary>
        /// Сторона значка. Раскладка по дуге вокруг аватарки задаёт её долей
        /// от размера аватарки, поэтому значки растут и уменьшаются вместе с
        /// карточкой.
        /// </summary>
        public static readonly StyledProperty<double> IconSizeProperty =
            AvaloniaProperty.Register<CharacterLabelIcon, double>(nameof(IconSize), 16d);

        /// <summary>
        /// Обводка по краю подложки. На карточке значки лежат на аватарке и
        /// наезжают друг на друга — без разделителя дуга сливается в пятно.
        /// </summary>
        public static readonly StyledProperty<IBrush?> RimBrushProperty =
            AvaloniaProperty.Register<CharacterLabelIcon, IBrush?>(nameof(RimBrush));

        public static readonly StyledProperty<double> RimThicknessProperty =
            AvaloniaProperty.Register<CharacterLabelIcon, double>(nameof(RimThickness));

        static CharacterLabelIcon()
        {
            AffectsRender<CharacterLabelIcon>(LabelProperty, IconSizeProperty, RimBrushProperty, RimThicknessProperty);
            AffectsMeasure<CharacterLabelIcon>(IconSizeProperty);
        }

        public CharacterLabel? Label
        {
            get => GetValue(LabelProperty);
            set => SetValue(LabelProperty, value);
        }

        public double IconSize
        {
            get => GetValue(IconSizeProperty);
            set => SetValue(IconSizeProperty, value);
        }

        public IBrush? RimBrush
        {
            get => GetValue(RimBrushProperty);
            set => SetValue(RimBrushProperty, value);
        }

        public double RimThickness
        {
            get => GetValue(RimThicknessProperty);
            set => SetValue(RimThicknessProperty, value);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var side = Math.Max(1d, IconSize);
            return new Size(side, side);
        }

        public override void Render(DrawingContext context)
        {
            var label = Label;
            if (label == null) return;

            var side = Math.Min(Bounds.Width, Bounds.Height);
            if (side <= 0) return;

            var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
            var radius = side / 2;
            var hasBackdrop = label.ShowBackdrop;

            if (hasBackdrop)
            {
                var backdrop = ResolveBackdropBrush(label);
                var rim = RimBrush != null && RimThickness > 0
                    ? new Pen(RimBrush, RimThickness)
                    : null;
                context.DrawEllipse(backdrop, rim, center, radius, radius);
            }

            if (label.HasCustomIcon)
            {
                RenderImage(context, label, center, radius, hasBackdrop);
                return;
            }

            RenderGlyph(context, label, center, radius, hasBackdrop);
        }

        // ── Встроенная фигура ─────────────────────────────────────────────

        private void RenderGlyph(DrawingContext context, CharacterLabel label, Point center, double radius, bool hasBackdrop)
        {
            var geometry = Geometry.Parse(CharacterLabelIcons.GetPathData(label.Icon));
            var bounds = geometry.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0) return;

            var box = radius * 2 * (hasBackdrop ? GlyphShareOnBackdrop : 1d);
            var scale = Math.Min(box / bounds.Width, box / bounds.Height);

            // Сдвиг считается от середины очертания, а не от угла поля:
            // именно этого не делает Stretch, и именно поэтому капля стояла
            // криво. Порядок множителей обратный порядку действий — сначала
            // очертание сдвигается своей серединой в ноль, потом
            // масштабируется, потом уезжает в середину значка.
            var matrix =
                Matrix.CreateTranslation(-bounds.X - bounds.Width / 2, -bounds.Y - bounds.Height / 2) *
                Matrix.CreateScale(scale, scale) *
                Matrix.CreateTranslation(center.X, center.Y);

            using (context.PushTransform(matrix))
            {
                context.DrawGeometry(ResolveGlyphBrush(label), null, geometry);
            }
        }

        // ── Своя картинка ─────────────────────────────────────────────────

        private void RenderImage(DrawingContext context, CharacterLabel label, Point center, double radius, bool hasBackdrop)
        {
            var vector = LabelIconImages.IsVector(label.IconImage);

            // Размер запрашивается с запасом на плотность экрана и на рост
            // карточки: значок кэшируется по размеру, и пересъём картинки на
            // каждый шаг ползунка размера карточек не нужен.
            var maxSide = (int)Math.Ceiling(Math.Max(16, radius * 2) * 2);
            var tint = vector ? ResolveTintColor(label) : (Color?)null;

            var bitmap = LabelIconImages.Get(label.IconImage, tint, maxSide);
            if (bitmap == null)
            {
                // Картинка пропала — метка показывает встроенную фигуру,
                // ключ которой всё это время сохранялся.
                RenderGlyph(context, label, center, radius, hasBackdrop);
                return;
            }

            var source = new Rect(bitmap.Size);
            if (source.Width <= 0 || source.Height <= 0) return;

            if (vector)
            {
                // Вектор — такой же значок, как встроенный: вписывается
                // целиком и не обрезается.
                var box = radius * 2 * (hasBackdrop ? GlyphShareOnBackdrop : 1d);
                var scale = Math.Min(box / source.Width, box / source.Height);
                var width = source.Width * scale;
                var height = source.Height * scale;
                context.DrawImage(bitmap, source,
                    new Rect(center.X - width / 2, center.Y - height / 2, width, height));
                return;
            }

            // Растр — фотография или готовая эмблема: заполняет значок
            // целиком и обрезается по кругу, как аватарка.
            var fill = Math.Max(radius * 2 / source.Width, radius * 2 / source.Height);
            var fillWidth = source.Width * fill;
            var fillHeight = source.Height * fill;
            var target = new Rect(center.X - fillWidth / 2, center.Y - fillHeight / 2, fillWidth, fillHeight);

            using (context.PushGeometryClip(new EllipseGeometry(
                       new Rect(center.X - radius, center.Y - radius, radius * 2, radius * 2))))
            {
                context.DrawImage(bitmap, source, target);
            }
        }

        // ── Цвета ─────────────────────────────────────────────────────────

        // Фигура и кружок красятся независимо: кружком метка отличается от
        // соседних, фигура остаётся читаемой. Градиент поддерживается там и
        // там тем же разбором, что у цвета персонажа.
        private static IBrush ResolveGlyphBrush(CharacterLabel label)
            => string.IsNullOrWhiteSpace(label.IconColor)
                ? DefaultGlyphBrush
                : GradientBrushFactory.FromCode(label.IconColor);

        private static IBrush ResolveBackdropBrush(CharacterLabel label)
            => GradientBrushFactory.FromCode(label.Color);

        /// <summary>
        /// Один представительный цвет фигуры: для одноцвета — он сам, для
        /// градиента — его первый переход. Нужен для перекраски вектора:
        /// градиентом залить чужой рисунок нечем.
        /// </summary>
        private static Color ResolveTintColor(CharacterLabel label)
        {
            if (string.IsNullOrWhiteSpace(label.IconColor)) return Colors.White;

            var spec = GradientSpec.Parse(label.IconColor);
            var hex = spec.IsSolid ? spec.SolidHex : FirstStopHex(spec);
            return Color.TryParse(hex, out var color) ? color : Colors.White;
        }

        private static string FirstStopHex(GradientSpec spec)
        {
            var stops = spec.SortedStops();
            return stops.Count > 0 ? stops[0].Hex : "#FFFFFF";
        }
    }
}
