using System;
using System.Collections.Generic;
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
    /// Значок анкеты: фигура её цвета на подложке того же цвета, только
    /// бледной. Один и тот же везде — в библиотеке анкет, в шаблонах, в
    /// заголовке раздела карточки, в меню подключения, — чтобы анкету узнавали
    /// по значку в любом месте.
    ///
    /// Фигура центруется по своим настоящим границам, а не растягивается
    /// Stretch: тот прижимает очертание к углу поля, и несимметричные значки
    /// стояли бы криво (та же история, что у значков меток).
    ///
    /// Значок бывает и своей картинкой из библиотеки (CharacterIcons):
    /// SVG красится цветом значка, как встроенная фигура, а растр — герб,
    /// эмблема, рисунок — занимает значок целиком. Цвет — код цвета
    /// программы, градиент тоже: им красятся и подложка, и фигура.
    ///
    /// Подложка задаётся отдельно (IconBackground): пусто — как обычно
    /// (бледная цвета значка), «fill» — залитая с белой фигурой, «none» — без
    /// подложки, иначе — свой код цвета. Вокруг подложки может быть рамка
    /// (IconBorder, CharacterIconFrame): цвета значка или своя, трёх
    /// толщин. Место (Look) меняет только
    /// пропорции: у шаблона плитка с меньшим скруглением. Окно значков рисует
    /// тем же методом, поэтому превью там ровно такое же, как на месте.
    /// </summary>
    public class AnketaIconBadge : Control
    {
        public static readonly StyledProperty<string?> IconProperty =
            AvaloniaProperty.Register<AnketaIconBadge, string?>(nameof(Icon));

        public static readonly StyledProperty<string?> IconColorProperty =
            AvaloniaProperty.Register<AnketaIconBadge, string?>(nameof(IconColor));

        public static readonly StyledProperty<double> SizeProperty =
            AvaloniaProperty.Register<AnketaIconBadge, double>(nameof(Size), 26d);

        public static readonly StyledProperty<string?> IconBackgroundProperty =
            AvaloniaProperty.Register<AnketaIconBadge, string?>(nameof(IconBackground));

        public static readonly StyledProperty<CharacterIconLook> LookProperty =
            AvaloniaProperty.Register<AnketaIconBadge, CharacterIconLook>(nameof(Look), CharacterIconLook.Badge);

        public static readonly StyledProperty<string?> IconBorderProperty =
            AvaloniaProperty.Register<AnketaIconBadge, string?>(nameof(IconBorder));

        static AnketaIconBadge()
        {
            AffectsRender<AnketaIconBadge>(IconProperty, IconColorProperty, SizeProperty,
                IconBackgroundProperty, LookProperty, IconBorderProperty);
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

        /// <summary>Подложка: пусто — как обычно, «none» — без подложки, иначе код цвета.</summary>
        public string? IconBackground
        {
            get => GetValue(IconBackgroundProperty);
            set => SetValue(IconBackgroundProperty, value);
        }

        /// <summary>Рамка вокруг подложки: пусто — нет, иначе строка CharacterIconFrame.</summary>
        public string? IconBorder
        {
            get => GetValue(IconBorderProperty);
            set => SetValue(IconBorderProperty, value);
        }

        /// <summary>Где значок стоит: значок анкеты или плитка шаблона.</summary>
        public CharacterIconLook Look
        {
            get => GetValue(LookProperty);
            set => SetValue(LookProperty, value);
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
            Draw(context, rect, Icon, IconColor, IconBackground, Look, IconBorder);
        }

        /// <summary>Геометрия значка по ключу — разобранная один раз.</summary>
        public static Geometry GetGeometry(string? key) => GeometryOf(key);

        /// <summary>
        /// Нарисовать значок в квадрат rect. Общий для этого контрола и для
        /// полосы значков в блоке шаблона: там их рисуется много одним
        /// контролом, а выглядеть они должны одинаково.
        /// </summary>
        public static void Draw(DrawingContext context, Rect rect, string? icon, string? iconColor) =>
            Draw(context, rect, icon, iconColor, null, CharacterIconLook.Badge);

        /// <summary>
        /// Нарисовать значок с подложкой в квадрат rect.
        ///
        /// Как обычно — бледная подложка цвета значка, фигура цветная.
        /// Залитая — подложка цвета значка во всю силу, фигура белая.
        /// Своя — подложка своего цвета во всю силу, фигура цветная.
        /// Без подложки — одна фигура того же размера, на фоне того, где стоит:
        /// выключение подложки не должно менять размер значка.
        ///
        /// Значок анкеты (Badge) и плитка шаблона (Tile) отличаются только
        /// скруглением и размером фигуры.
        ///
        /// Растровая картинка занимает значок целиком — подложка под ней не
        /// видна при любом выборе. Рамка рисуется поверх всего, по краю.
        /// </summary>
        public static void Draw(DrawingContext context, Rect rect, string? icon, string? iconColor,
            string? background, CharacterIconLook look = CharacterIconLook.Badge, string? border = null)
        {
            var side = Math.Min(rect.Width, rect.Height);
            if (side <= 0) return;

            var code = string.IsNullOrWhiteSpace(iconColor) ? CharacterAnketaIcons.DefaultColor : iconColor;
            var brush = BrushOf(code);
            var tile = look == CharacterIconLook.Tile;
            var radius = side * (tile ? TileRadius : BadgeRadius);

            var auto = CharacterIcons.IsAutoBackground(background);
            var none = CharacterIcons.IsNoBackground(background);
            var fill = CharacterIcons.IsFillBackground(background);

            // Чем подложка и чем фигура.
            IBrush? plate;
            double plateOpacity;
            IBrush figure;
            Color? figureTint;

            if (none)
            {
                plate = null;
                plateOpacity = 0;
                figure = brush;
                figureTint = TintOf(code);
            }
            else if (auto)
            {
                plate = brush;
                plateOpacity = 0.2;
                figure = brush;
                figureTint = TintOf(code);
            }
            else if (fill)
            {
                plate = brush;
                plateOpacity = 1;
                figure = Brushes.White;
                figureTint = Colors.White;
            }
            else
            {
                plate = BrushOf(background);
                plateOpacity = 1;
                figure = brush;
                figureTint = TintOf(code);
            }

            var box = side * (tile ? TileFigure : BadgeFigure);

            var imageRef = CharacterIcons.ImageRefOf(icon);
            if (imageRef != null &&
                DrawImage(context, rect, side, radius, imageRef, plate, plateOpacity, figureTint, box))
            {
                DrawFrame(context, rect, side, radius, border, brush);
                return;
            }

            DrawPlate(context, rect, radius, plate, plateOpacity);

            var geometry = GeometryOf(imageRef != null ? null : icon);
            var bounds = geometry.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0) return;

            var scale = Math.Min(box / bounds.Width, box / bounds.Height);
            var matrix =
                Matrix.CreateTranslation(-bounds.X - bounds.Width / 2, -bounds.Y - bounds.Height / 2) *
                Matrix.CreateScale(scale, scale) *
                Matrix.CreateTranslation(rect.Center.X, rect.Center.Y);

            using (context.PushTransform(matrix))
            {
                context.DrawGeometry(figure, null, geometry);
            }

            DrawFrame(context, rect, side, radius, border, brush);
        }

        /// <summary>
        /// Рамка по краю значка. Толщина — доля стороны, чтобы рамка в списке
        /// и в превью выглядела одинаково; линия идёт внутрь, значок не
        /// раздувается и соседей не задевает.
        /// </summary>
        private static void DrawFrame(DrawingContext context, Rect rect, double side, double radius,
            string? border, IBrush iconBrush)
        {
            if (CharacterIconFrame.Parse(border) is not { } frame) return;

            var thickness = Math.Max(1, side * frame.Share);
            var brush = frame.IsAuto ? iconBrush : BrushOf(frame.Color);
            var inner = rect.Deflate(thickness / 2);
            var r = Math.Max(0, radius - thickness / 2);

            context.DrawRectangle(null, new Pen(brush, thickness), inner, r, r);
        }

        // Пропорции от стороны значка. Плитка шаблона — те же 64 точки со
        // скруглением 8 и фигурой 28, что были у неё до общего значка.
        private const double BadgeRadius = 0.28;
        private const double TileRadius = 0.125;
        private const double BadgeFigure = 0.58;
        private const double TileFigure = 0.4375;

        private static void DrawPlate(DrawingContext context, Rect rect, double radius, IBrush? plate, double opacity)
        {
            if (plate == null || opacity <= 0) return;

            if (opacity >= 1)
            {
                context.DrawRectangle(plate, null, rect, radius, radius);
                return;
            }

            using (context.PushOpacity(opacity))
                context.DrawRectangle(plate, null, rect, radius, radius);
        }

        /// <summary>
        /// Своя картинка значка. Вектор — на подложке, перекрашенный так же,
        /// как встроенная фигура. Растр — на весь значок со скруглёнными
        /// углами: гербу и рисунку подложка только мешает.
        /// Картинки нет (файл пропал) — false, и рисуется книга.
        /// </summary>
        private static bool DrawImage(DrawingContext context, Rect rect, double side, double radius,
            string imageRef, IBrush? plate, double plateOpacity, Color? tint, double figureBox)
        {
            var vector = LabelIconImages.IsVector(imageRef);
            var pixels = Math.Max(16, (int)Math.Ceiling(side * 2));
            var bitmap = LabelIconImages.Get(imageRef, vector ? tint : null, pixels);
            if (bitmap == null) return false;

            var size = bitmap.Size;
            if (size.Width <= 0 || size.Height <= 0) return false;

            if (vector)
            {
                DrawPlate(context, rect, radius, plate, plateOpacity);

                // Картинка в своих полях обычно с отступами — чуть крупнее фигуры.
                var box = Math.Min(side, figureBox * 1.07);
                var k = Math.Min(box / size.Width, box / size.Height);
                var w = size.Width * k;
                var h = size.Height * k;
                context.DrawImage(bitmap, new Rect(size),
                    new Rect(rect.Center.X - w / 2, rect.Center.Y - h / 2, w, h));
                return true;
            }

            // Растр заполняет значок целиком, лишнее по краям срезается.
            var cover = Math.Max(rect.Width / size.Width, rect.Height / size.Height);
            var sw = rect.Width / cover;
            var sh = rect.Height / cover;
            var source = new Rect((size.Width - sw) / 2, (size.Height - sh) / 2, sw, sh);

            using (context.PushClip(new RoundedRect(rect, radius)))
                context.DrawImage(bitmap, source, rect);
            return true;
        }

        // Кисти по коду цвета разбираются один раз: значков в списках много,
        // а разбор градиента на каждой отрисовке — лишняя работа.
        private static readonly Dictionary<string, IBrush> BrushCache = new(StringComparer.Ordinal);

        /// <summary>Кисть по коду цвета: одноцвет или градиент.</summary>
        public static IBrush BrushOf(string? code)
        {
            var key = string.IsNullOrWhiteSpace(code) ? CharacterAnketaIcons.DefaultColor : code;
            if (BrushCache.TryGetValue(key, out var cached)) return cached;

            IBrush brush;
            try
            {
                brush = GradientBrushFactory.FromCode(key);
            }
            catch
            {
                brush = new SolidColorBrush(Color.Parse(CharacterAnketaIcons.DefaultColor));
            }

            BrushCache[key] = brush;
            return brush;
        }

        /// <summary>Цвет перекраски вектора: у градиента — его первый цвет.</summary>
        public static Color TintOf(string? code)
        {
            try
            {
                var hex = GradientSpec.Parse(string.IsNullOrWhiteSpace(code) ? CharacterAnketaIcons.DefaultColor : code).SolidHex;
                if (Color.TryParse(hex, out var color)) return color;
            }
            catch
            {
                // Битый код цвета красит значок цветом по умолчанию.
            }

            return Color.Parse(CharacterAnketaIcons.DefaultColor);
        }
    }
}
