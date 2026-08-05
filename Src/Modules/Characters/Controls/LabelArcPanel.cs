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

        static LabelArcPanel()
        {
            AffectsMeasure<LabelArcPanel>(ItemSizeProperty);
            AffectsArrange<LabelArcPanel>(ItemSizeProperty, ArcRadiusProperty, SweepProperty, GapProperty, CenterAngleProperty);
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
