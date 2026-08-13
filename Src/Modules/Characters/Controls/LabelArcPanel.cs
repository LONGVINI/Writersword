using System;
using Avalonia;
using Avalonia.Controls;

namespace Writersword.Modules.Characters.Controls
{
    /// <summary>
    /// Раскладка значков меток по нижней дуге вокруг аватарки.
    ///
    /// Прежде значки лежали строкой в нижнем левом углу карточки и залезали
    /// на подпись имени, а при наведении — под кнопки правки. Дуга уводит их
    /// на край аватарки, где ничего больше не рисуется, и заодно позволяет
    /// показывать все метки разом: строка упиралась в ширину карточки и
    /// обрывалась счётчиком «плюс сколько-то».
    ///
    /// Шаг между значками не постоянный и не подобранный на глаз. Пока меток
    /// мало, берётся шаг, при котором соседи стоят с небольшим зазором. Когда
    /// их становится столько, что дуга кончается, шаг сжимается ровно
    /// настолько, чтобы уместились все: сперва значки сходятся вплотную,
    /// потом начинают наезжать друг на друга веером. Ни один значок при этом
    /// не пропадает.
    /// </summary>
    public class LabelArcPanel : Panel
    {
        /// <summary>Сторона значка. Задаётся долей от размера аватарки.</summary>
        public static readonly StyledProperty<double> ItemSizeProperty =
            AvaloniaProperty.Register<LabelArcPanel, double>(nameof(ItemSize), 16d);

        /// <summary>
        /// Радиус дуги — расстояние от середины аватарки до середины значка.
        /// </summary>
        public static readonly StyledProperty<double> ArcRadiusProperty =
            AvaloniaProperty.Register<LabelArcPanel, double>(nameof(ArcRadius), 28d);

        /// <summary>
        /// Раствор дуги в градусах, симметрично относительно низа. Больше
        /// ста шестидесяти брать нельзя: значки полезут выше середины
        /// аватарки, на её лицо.
        /// </summary>
        public static readonly StyledProperty<double> SweepProperty =
            AvaloniaProperty.Register<LabelArcPanel, double>(nameof(Sweep), 150d);

        /// <summary>Просвет между соседними значками, пока они помещаются.</summary>
        public static readonly StyledProperty<double> GapProperty =
            AvaloniaProperty.Register<LabelArcPanel, double>(nameof(Gap), 2d);

        /// <summary>
        /// Середина дуги в градусах. Ноль — вправо, рост по часовой стрелке
        /// (ось Y смотрит вниз), девяносто — низ. Значение по умолчанию —
        /// низ аватарки; метка «Мёртв» выносится на минус сорок пять, то
        /// есть вверх-вправо, где её ничто не перекрывает.
        /// </summary>
        public static readonly StyledProperty<double> CenterAngleProperty =
            AvaloniaProperty.Register<LabelArcPanel, double>(nameof(CenterAngle), 90d);

        /// <summary>
        /// Раскладывать прямым рядом у края зоны вместо дуги. Дуга имеет
        /// смысл, пока есть кружок аватарки, вокруг которого её вести. В
        /// режиме, где фотография занимает всю зону карточки, кружка нет, и
        /// дуга посреди снимка читается как случайно рассыпанные значки —
        /// ряд у нижнего края, прямо над подписью имени, выглядит уместнее.
        ///
        /// Направление прижатия берётся из того же CenterAngle: низ (девяносто
        /// градусов) даёт ряд по центру нижнего края, минус сорок пять —
        /// правый верхний угол зоны, где стоит «Мёртв».
        /// </summary>
        public static readonly StyledProperty<bool> StraightProperty =
            AvaloniaProperty.Register<LabelArcPanel, bool>(nameof(Straight));

        /// <summary>
        /// Отступы от краёв зоны в прямом режиме, по одному на сторону.
        /// Порознь они нужны затем, что в правом верхнем углу карточки живут
        /// кнопки правки: значку «Мёртв» там задаётся большой отступ справа,
        /// чтобы встать левее них, и почти нулевой сверху.
        /// </summary>
        public static readonly StyledProperty<Thickness> EdgePaddingProperty =
            AvaloniaProperty.Register<LabelArcPanel, Thickness>(nameof(EdgePadding), new Thickness(5));

        static LabelArcPanel()
        {
            AffectsMeasure<LabelArcPanel>(ItemSizeProperty);
            AffectsArrange<LabelArcPanel>(ItemSizeProperty, ArcRadiusProperty, SweepProperty,
                GapProperty, CenterAngleProperty, StraightProperty, EdgePaddingProperty);
        }

        public double ItemSize
        {
            get => GetValue(ItemSizeProperty);
            set => SetValue(ItemSizeProperty, value);
        }

        public double ArcRadius
        {
            get => GetValue(ArcRadiusProperty);
            set => SetValue(ArcRadiusProperty, value);
        }

        public double Sweep
        {
            get => GetValue(SweepProperty);
            set => SetValue(SweepProperty, value);
        }

        public double Gap
        {
            get => GetValue(GapProperty);
            set => SetValue(GapProperty, value);
        }

        public double CenterAngle
        {
            get => GetValue(CenterAngleProperty);
            set => SetValue(CenterAngleProperty, value);
        }

        public bool Straight
        {
            get => GetValue(StraightProperty);
            set => SetValue(StraightProperty, value);
        }

        public Thickness EdgePadding
        {
            get => GetValue(EdgePaddingProperty);
            set => SetValue(EdgePaddingProperty, value);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var side = Math.Max(1d, ItemSize);
            var itemConstraint = new Size(side, side);

            foreach (var child in Children)
                child.Measure(itemConstraint);

            // Панель лежит поверх цветной зоны карточки и занимает её целиком:
            // середина панели — это середина аватарки, от неё и считается дуга.
            // Своего размера она не просит.
            var width = double.IsInfinity(availableSize.Width) ? side : availableSize.Width;
            var height = double.IsInfinity(availableSize.Height) ? side : availableSize.Height;
            return new Size(width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var count = Children.Count;
            if (count == 0) return finalSize;

            var side = Math.Max(1d, ItemSize);
            var radius = Math.Max(side / 2, ArcRadius);
            var center = new Point(finalSize.Width / 2, finalSize.Height / 2);

            var middle = CenterAngle * Math.PI / 180.0;

            if (Straight)
            {
                ArrangeStraight(finalSize, center, middle, side, count);
                return finalSize;
            }

            if (count == 1)
            {
                ArrangeAt(Children[0], center, radius, middle, side);
                return finalSize;
            }

            // Шаг, при котором соседи стоят с зазором: хорда между их
            // серединами равна стороне значка плюс просвет. Из хорды угол
            // берётся через арксинус половины — обычная связь хорды и
            // центрального угла, а не приближение по длине дуги: на радиусах
            // в пару десятков точек приближение врёт заметно.
            var chord = side + Math.Max(0d, Gap);
            var half = Math.Min(1d, chord / (2 * radius));
            var looseStep = 2 * Math.Asin(half);

            var sweep = Math.Clamp(Sweep, 0d, 160d) * Math.PI / 180.0;
            var tightStep = sweep / (count - 1);

            // Сжатие включается само: пока свободный шаг помещается в дугу,
            // берётся он, дальше — то, что осталось. Отдельного порога и
            // счётчика «плюс сколько-то» здесь нет.
            var step = Math.Min(looseStep, tightStep);
            var total = step * (count - 1);

            // Отсчёт углов: ноль — вправо, рост по часовой стрелке (ось Y
            // смотрит вниз), низ — девяносто градусов. Раскладка идёт слева
            // направо, поэтому угол убывает.
            var angle = middle + total / 2;

            for (var i = 0; i < count; i++)
            {
                ArrangeAt(Children[i], center, radius, angle, side);
                angle -= step;
            }

            return finalSize;
        }

        // Прямой ряд. Точка прижатия выводится из направления угла: косинус
        // отвечает за прижатие по горизонтали, синус — по вертикали. Низ по
        // центру и правый верхний угол получаются из одного и того же расчёта,
        // отдельных ветвей под каждый случай нет.
        private void ArrangeStraight(Size finalSize, Point center, double middle, double side, int count)
        {
            var padding = EdgePadding;
            var toward = new Point(Math.Cos(middle), Math.Sin(middle));

            // Отступ берётся с той стороны, к которой прижимаемся: угол задаёт
            // направление, знак косинуса и синуса — сторону.
            var padX = toward.X >= 0 ? padding.Right : padding.Left;
            var padY = toward.Y >= 0 ? padding.Bottom : padding.Top;

            var reachX = Math.Max(0d, finalSize.Width / 2 - side / 2 - padX);
            var reachY = Math.Max(0d, finalSize.Height / 2 - side / 2 - padY);

            var anchorX = center.X + toward.X * reachX;
            var anchorY = center.Y + toward.Y * reachY;

            if (count == 1)
            {
                ArrangeAtPoint(Children[0], anchorX, anchorY, side);
                return;
            }

            // Тот же принцип, что на дуге: пока ряд помещается в ширину зоны,
            // значки стоят с зазором, дальше шаг сжимается и они наезжают
            // друг на друга. Ни один не пропадает.
            var available = Math.Max(side, finalSize.Width - side - padding.Left - padding.Right);
            var step = Math.Min(side + Math.Max(0d, Gap), available / (count - 1));
            var total = step * (count - 1);

            // Ряд, прижатый к краю по горизонтали, растёт от этого края
            // внутрь зоны; ряд по центру расходится в обе стороны.
            var startX = anchorX - total / 2 - total / 2 * toward.X;

            for (var i = 0; i < count; i++)
                ArrangeAtPoint(Children[i], startX + step * i, anchorY, side);
        }

        private static void ArrangeAtPoint(Control child, double centerX, double centerY, double side)
        {
            var size = child.DesiredSize;
            var width = size.Width > 0 ? size.Width : side;
            var height = size.Height > 0 ? size.Height : side;
            child.Arrange(new Rect(centerX - width / 2, centerY - height / 2, width, height));
        }

        private static void ArrangeAt(Control child, Point center, double radius, double angle, double side)
        {
            var size = child.DesiredSize;
            var width = size.Width > 0 ? size.Width : side;
            var height = size.Height > 0 ? size.Height : side;

            var x = center.X + radius * Math.Cos(angle) - width / 2;
            var y = center.Y + radius * Math.Sin(angle) - height / 2;
            child.Arrange(new Rect(x, y, width, height));
        }
    }
}
